using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Core.Models;
using Shredder.Core.Reporting;
using Shredder.Core.Services;

namespace Shredder.App.ViewModels;

/// <summary>
/// 单文件 / 目录粉碎页面的 VM。承担 Jobs 队列管理、算法选择、执行与取消，
/// 并在每批运行结束后生成审计报告。
/// </summary>
public sealed partial class ShredPageViewModel : ObservableObject, IDisposable
{
    private readonly ShredService _shredService;
    private readonly ShredderOptions _options;
    private readonly IShredAlgorithmRegistry _registry;
    private readonly IShredReportWriter _reportWriter;
    private readonly ILogger<ShredPageViewModel> _logger;

    public ObservableCollection<ShredJobRow> Jobs { get; } = new();
    public IReadOnlyList<IShredAlgorithm> Algorithms { get; }

    [ObservableProperty] private IShredAlgorithm _selectedAlgorithm;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>最近一次生成的报告（HTML 优先）的绝对路径，为 null 表示尚未生成。</summary>
    [ObservableProperty] private string? _lastReportPath;

    private CancellationTokenSource? _cts;

    public ShredPageViewModel(
        IShredAlgorithmRegistry registry,
        ShredService shredService,
        IShredReportWriter reportWriter,
        IOptions<ShredderOptions> options,
        ILogger<ShredPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(shredService);
        ArgumentNullException.ThrowIfNull(reportWriter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _shredService = shredService;
        _reportWriter = reportWriter;
        _options = options.Value;
        _registry = registry;
        _logger = logger;

        Algorithms = registry.All;
        _selectedAlgorithm = registry.Find(_options.Algorithm.Default)
            ?? (Algorithms.Count > 0 ? Algorithms[0] : null)
            ?? throw new InvalidOperationException("没有可用的粉碎算法。");
    }

    public void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        bool isDir = Directory.Exists(path);
        long size = 0;
        try { size = isDir ? 0 : new FileInfo(path).Length; }
        catch (IOException) { /* 路径无法访问时仍允许加入,执行时会失败 */ }
        Jobs.Add(new ShredJobRow(path, isDir, size, SelectedAlgorithm));
    }

    public void Clear() => Jobs.Clear();

    public void Remove(ShredJobRow row)
    {
        if (row is null) return;
        Jobs.Remove(row);
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:单个 job 失败不应中断整批 job 队列,异常已记录到日志并写入 Job.ErrorMessage(由 ShredService)。")]
    public async Task RunAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ShredProgress>(OnProgress);

        var entries = new List<ShredAuditEntry>();
        var batchStart = DateTimeOffset.Now;

        try
        {
            foreach (var row in Jobs.Where(r => r.Status == ShredJobStatus.Pending).ToList())
            {
                if (_cts.IsCancellationRequested) break;
                var job = row.BuildJob();
                var entryStart = DateTimeOffset.Now;
                try
                {
                    await _shredService.ShredAsync(job, progress, _cts.Token);
                }
                catch (OperationCanceledException) { row.SyncFrom(job); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Run job failed: {Path}", row.Path);
                }
                row.SyncFrom(job);

                var alg = _registry.Find(job.AlgorithmId ?? row.Algorithm.Id);
                entries.Add(new ShredAuditEntry
                {
                    Path = row.Path,
                    IsDirectory = row.IsDirectory,
                    SizeBytes = row.SizeBytes,
                    AlgorithmId = job.AlgorithmId ?? row.Algorithm.Id,
                    AlgorithmName = alg?.Name ?? row.Algorithm.Name,
                    PassCount = alg?.PassCount ?? row.Algorithm.PassCount,
                    StartedAt = entryStart,
                    CompletedAt = DateTimeOffset.Now,
                    Status = job.Status,
                    ErrorMessage = job.ErrorMessage,
                });

                if (_cts.IsCancellationRequested) break;
            }
            ProgressText = _cts.IsCancellationRequested ? "已取消" : "完成";
            ProgressPercent = _cts.IsCancellationRequested ? ProgressPercent : 100;
        }
        finally
        {
            IsBusy = false;
            if (entries.Count > 0)
            {
                var report = BuildReport(entries, batchStart);
                try
                {
                    var path = await _reportWriter.WriteAsync(report, CancellationToken.None);
                    if (!string.IsNullOrEmpty(path))
                    {
                        LastReportPath = path;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Write audit report failed.");
                }
            }
        }
    }

    public void Cancel() => _cts?.Cancel();

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "打开报告/资源管理器失败不应崩溃 Dispatcher。")]
    public void OpenLastReport()
    {
        if (string.IsNullOrEmpty(LastReportPath) || !File.Exists(LastReportPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(LastReportPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open last report failed: {Path}", LastReportPath);
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "打开资源管理器失败不应崩溃 Dispatcher。")]
    public void OpenReportsFolder()
    {
        var dir = ResolveReportsDirectory();
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open reports folder failed: {Dir}", dir);
        }
    }

    private string ResolveReportsDirectory()
    {
        var raw = _options.Reporting.OutputDirectory;
        var expanded = Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(raw) ? "%LOCALAPPDATA%\\Shredder\\Reports" : raw);
        return Path.GetFullPath(expanded);
    }

    private static ShredReport BuildReport(IReadOnlyList<ShredAuditEntry> entries, DateTimeOffset start)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        return new ShredReport
        {
            ReportId = Guid.NewGuid().ToString("N").Substring(0, 8),
            StartedAt = start,
            CompletedAt = DateTimeOffset.Now,
            AppVersion = version,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Entries = entries,
        };
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }

    private void OnProgress(ShredProgress p)
    {
        ProgressText = $"{Path.GetFileName(p.FilePath)} · pass {p.PassIndex}/{p.PassCount}";
        if (p.TotalBytes > 0)
            ProgressPercent = (double)p.BytesWritten / p.TotalBytes * 100;
    }
}

/// <summary>
/// Jobs 列表的可观察行。允许用户为单行覆盖算法,执行后回写状态。
/// </summary>
public sealed partial class ShredJobRow : ObservableObject
{
    public ShredJobRow(string path, bool isDirectory, long sizeBytes, IShredAlgorithm algorithm)
    {
        Path = path;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
        _algorithm = algorithm;
    }

    public string Path { get; }
    public bool IsDirectory { get; }
    public long SizeBytes { get; }

    [ObservableProperty] private IShredAlgorithm _algorithm;
    [ObservableProperty] private ShredJobStatus _status = ShredJobStatus.Pending;
    [ObservableProperty] private string? _errorMessage;

    public string DisplaySize =>
        IsDirectory ? "(目录)" : FormatBytes(SizeBytes);

    internal ShredJob BuildJob() => new()
    {
        Path = Path,
        IsDirectory = IsDirectory,
        SizeBytes = SizeBytes,
        AlgorithmId = Algorithm.Id,
    };

    internal void SyncFrom(ShredJob job)
    {
        Status = job.Status;
        ErrorMessage = job.ErrorMessage;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.##} {units[i]}";
    }
}
