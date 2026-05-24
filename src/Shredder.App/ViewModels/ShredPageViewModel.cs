using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.Json;
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
/// 单文件 / 目录粉碎页面的 VM。承担 Jobs 队列管理、执行与取消，
/// 并在每批运行结束后生成审计报告。
/// </summary>
public sealed partial class ShredPageViewModel : ObservableObject, IDisposable
{
    private readonly ShredService _shredService;
    private readonly ShredderOptions _options;
    private readonly IShredReportWriter _reportWriter;
    private readonly ILogger<ShredPageViewModel> _logger;

    public ObservableCollection<ShredJobRow> Jobs { get; } = new();
    public ObservableCollection<ShredHistoryRow> HistoryItems { get; } = new();

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "就绪";
    [ObservableProperty] private string _progressState = "Idle";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isHistoryVisible;
    [ObservableProperty] private string _historySummary = "暂无粉碎历史";
    [ObservableProperty] private string? _lastRunFailureMessage;
    [ObservableProperty] private string? _lastRunFailureReason;

    /// <summary>最近一次生成的报告（HTML 优先）的绝对路径，为 null 表示尚未生成。</summary>
    [ObservableProperty] private string? _lastReportPath;

    private CancellationTokenSource? _cts;
    private int _acceptProgressUpdates;

    public ShredPageViewModel(
        ShredService shredService,
        IShredReportWriter reportWriter,
        IOptions<ShredderOptions> options,
        ILogger<ShredPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(shredService);
        ArgumentNullException.ThrowIfNull(reportWriter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _shredService = shredService;
        _reportWriter = reportWriter;
        _options = options.Value;
        _logger = logger;
    }

    public void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        bool isDir = Directory.Exists(path);
        long size = 0;
        try { size = isDir ? -1 : new FileInfo(path).Length; }
        catch (IOException) { /* 路径无法访问时仍允许加入,执行时会失败 */ }
        var row = new ShredJobRow(path, isDir, size);
        Jobs.Add(row);

        if (isDir)
        {
            _ = RefreshDirectorySizeAsync(row);
        }
    }

    public void Clear() => Jobs.Clear();

    public void Remove(ShredJobRow row)
    {
        if (row is null) return;
        Jobs.Remove(row);
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:单个 job 失败不应中断整批 job 队列,异常已记录到日志并写入 Job.ErrorMessage(由 ShredService)。")]
    public Task RunAsync() => RunAsync(algorithmId: null);

    public Task RunFastAsync() => RunAsync(ShredAlgorithmIds.FastDelete);

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI command boundary: a single job failure should be logged and stored without stopping the whole batch.")]
    private async Task RunAsync(string? algorithmId)
    {
        if (IsBusy) return;
        IsBusy = true;
        LastRunFailureMessage = null;
        LastRunFailureReason = null;
        Volatile.Write(ref _acceptProgressUpdates, 1);
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ShredProgress>(OnProgress);

        var entries = new ConcurrentBag<ShredAuditEntry>();
        var batchStart = DateTimeOffset.Now;

        try
        {
            var pendingRows = Jobs.Where(r => r.Status == ShredJobStatus.Pending).ToList();
            if (IsFastDeleteAlgorithm(algorithmId) && pendingRows.Count > 1)
            {
                await RunFastRowsInParallelAsync(pendingRows, algorithmId, progress, entries, _cts.Token);
            }
            else
            {
                foreach (var row in pendingRows)
                {
                    if (_cts.IsCancellationRequested) break;
                    var entry = await ProcessRowForBatchAsync(row, algorithmId, progress, SynchronizationContext.Current, _cts.Token);
                    entries.Add(entry);
                    if (_cts.IsCancellationRequested) break;
                }
            }
            UpdateBatchProgress(entries, _cts.IsCancellationRequested);
            Volatile.Write(ref _acceptProgressUpdates, 0);
            ProgressPercent = _cts.IsCancellationRequested ? ProgressPercent : 100;
            RemoveCompletedJobs();
        }
        finally
        {
            Volatile.Write(ref _acceptProgressUpdates, 0);
            IsBusy = false;
            var entryList = entries.OrderBy(static e => e.StartedAt).ToList();
            if (entryList.Count > 0)
            {
                var report = BuildReport(entryList, batchStart);
                try
                {
                    var path = await _reportWriter.WriteAsync(report, CancellationToken.None);
                    if (!string.IsNullOrEmpty(path))
                    {
                        LastReportPath = path;
                    }
                    AddHistoryEntries(report);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Write audit report failed.");
                }
            }
        }
    }

    private async Task RunFastRowsInParallelAsync(
        IReadOnlyList<ShredJobRow> rows,
        string? algorithmId,
        IProgress<ShredProgress> progress,
        ConcurrentBag<ShredAuditEntry> entries,
        CancellationToken ct)
    {
        var uiContext = SynchronizationContext.Current;
        var parallelism = ResolveFastParallelism();
        using var semaphore = new SemaphoreSlim(parallelism, parallelism);

        var tasks = rows.Select(row => RunFastRowWithThrottleAsync(
            row,
            algorithmId,
            progress,
            entries,
            semaphore,
            uiContext,
            ct)).ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task RunFastRowWithThrottleAsync(
        ShredJobRow row,
        string? algorithmId,
        IProgress<ShredProgress> progress,
        ConcurrentBag<ShredAuditEntry> entries,
        SemaphoreSlim semaphore,
        SynchronizationContext? uiContext,
        CancellationToken ct)
    {
        try
        {
            await semaphore.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (ct.IsCancellationRequested) return;
            var entry = await Task.Run(
                () => ProcessRowForBatchAsync(row, algorithmId, progress, uiContext, ct),
                CancellationToken.None);
            entries.Add(entry);
        }
        finally
        {
            semaphore.Release();
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI command boundary: a single job failure should be logged and stored without stopping the whole batch.")]
    private async Task<ShredAuditEntry> ProcessRowForBatchAsync(
        ShredJobRow row,
        string? algorithmId,
        IProgress<ShredProgress> progress,
        SynchronizationContext? uiContext,
        CancellationToken ct)
    {
        var job = row.BuildJob(algorithmId);
        var entryStart = DateTimeOffset.Now;
        IShredAlgorithm? algorithm = null;
        try
        {
            algorithm = _shredService.PreviewAlgorithm(row.Path, job.AlgorithmId);
        }
        catch (InvalidOperationException)
        {
            // ShredAsync will surface the same configuration issue as the job failure.
        }

        try
        {
            RunOnContext(uiContext, () =>
            {
                row.Status = ShredJobStatus.Running;
                ProgressState = "Running";
                ProgressText = IsFastDeleteAlgorithm(algorithmId)
                    ? $"正在快速粉碎 {row.Name}"
                    : $"正在粉碎 {row.Name}";
            });
            await _shredService.ShredAsync(job, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { RunOnContext(uiContext, () => row.SyncFrom(job)); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run job failed: {Path}", row.Path);
        }

        RunOnContext(uiContext, () => row.SyncFrom(job));

        return new ShredAuditEntry
        {
            Path = row.Path,
            IsDirectory = row.IsDirectory,
            SizeBytes = row.SizeBytes,
            AlgorithmId = algorithm?.Id ?? job.AlgorithmId,
            AlgorithmName = algorithm?.Name,
            PassCount = algorithm?.PassCount ?? 0,
            StartedAt = entryStart,
            CompletedAt = DateTimeOffset.Now,
            Status = job.Status,
            ErrorMessage = job.ErrorMessage,
        };
    }

    private int ResolveFastParallelism()
    {
        var configured = _options.Io.MaxConcurrentFiles;
        if (configured <= 1) configured = 4;
        return Math.Clamp(configured, 1, 8);
    }

    private void UpdateBatchProgress(IEnumerable<ShredAuditEntry> entries, bool cancelled)
    {
        if (cancelled)
        {
            ProgressState = "Cancelled";
            ProgressText = "已取消";
            return;
        }

        var snapshot = entries.ToList();
        if (snapshot.Count == 0)
        {
            ProgressState = "Idle";
            ProgressText = "就绪";
            return;
        }

        var successCount = snapshot.Count(static e => e.Status == ShredJobStatus.Success);
        var failedCount = snapshot.Count(static e => e.Status == ShredJobStatus.Failed);
        var cancelledCount = snapshot.Count(static e => e.Status == ShredJobStatus.Cancelled);

        if (failedCount > 0)
        {
            ProgressState = "Failed";
            var firstFailure = snapshot.FirstOrDefault(static e => e.Status == ShredJobStatus.Failed);
            var reason = BuildFailureReason(firstFailure?.ErrorMessage);
            ProgressText = $"{failedCount} 个失败，{reason}";
            LastRunFailureReason = reason;
            LastRunFailureMessage = $"失败原因：{reason}";
            return;
        }

        if (cancelledCount > 0)
        {
            ProgressState = "Cancelled";
            ProgressText = $"粉碎已取消：{cancelledCount} 个取消，{successCount} 个成功";
            return;
        }

        ProgressState = "Success";
        ProgressText = $"粉碎完成：成功 {successCount} 个";
    }

    private static bool IsFastDeleteAlgorithm(string? algorithmId) =>
        string.Equals(algorithmId, ShredAlgorithmIds.FastDelete, StringComparison.OrdinalIgnoreCase);

    private static void RunOnContext(SynchronizationContext? context, Action action)
    {
        if (context is null || context == SynchronizationContext.Current)
        {
            action();
            return;
        }

        context.Send(_ => action(), null);
    }

    public void ShowHistory()
    {
        RefreshHistory();
        IsHistoryVisible = true;
    }

    public void HideHistory() => IsHistoryVisible = false;

    public void ClearHistory()
    {
        var dir = ResolveReportsDirectory();
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*")
                                          .Where(static f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                              || f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Delete history report failed: {Path}", file);
                }
            }
        }

        HistoryItems.Clear();
        HistorySummary = "暂无粉碎历史";
    }

    public void RefreshHistory()
    {
        HistoryItems.Clear();
        foreach (var item in LoadHistoryRows())
        {
            HistoryItems.Add(item);
        }

        HistorySummary = HistoryItems.Count == 0
            ? "暂无粉碎历史"
            : $"共 {HistoryItems.Count} 条记录，最近 {HistoryItems[0].CompletedAtText}";
    }

    private static string BuildFailureReason(string? rawReason)
    {
        if (string.IsNullOrWhiteSpace(rawReason))
        {
            return "权限不足，请以管理员运行";
        }

        if (rawReason.Contains("占用", StringComparison.OrdinalIgnoreCase)
            || rawReason.Contains("PID=", StringComparison.OrdinalIgnoreCase))
        {
            return "文件被占用，请以管理员运行";
        }

        if (rawReason.Contains("权限", StringComparison.OrdinalIgnoreCase)
            || rawReason.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase)
            || rawReason.Contains("Access", StringComparison.OrdinalIgnoreCase)
            || rawReason.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return "权限不足，请以管理员运行";
        }

        return rawReason;
    }

    private void RemoveCompletedJobs()
    {
        foreach (var row in Jobs.Where(static r => r.Status is ShredJobStatus.Success or ShredJobStatus.Failed).ToList())
        {
            Jobs.Remove(row);
        }
    }

    private void AddHistoryEntries(ShredReport report)
    {
        var rows = report.Entries
            .OrderByDescending(static e => e.CompletedAt)
            .Select(ShredHistoryRow.FromAuditEntry)
            .ToList();

        foreach (var row in rows)
        {
            HistoryItems.Insert(0, row);
        }

        HistorySummary = HistoryItems.Count == 0
            ? "暂无粉碎历史"
            : $"共 {HistoryItems.Count} 条记录，最近 {HistoryItems[0].CompletedAtText}";
    }

    private async Task RefreshDirectorySizeAsync(ShredJobRow row)
    {
        try
        {
            var size = await Task.Run(() => CalculateDirectorySize(row.Path)).ConfigureAwait(true);
            row.SizeBytes = size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            row.SizeBytes = 0;
            _logger.LogWarning(ex, "Calculate directory size failed: {Path}", row.Path);
        }
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

    private List<ShredHistoryRow> LoadHistoryRows()
    {
        var dir = ResolveReportsDirectory();
        if (!Directory.Exists(dir)) return [];

        var rows = new List<ShredHistoryRow>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json")
                                      .OrderByDescending(File.GetLastWriteTime)
                                      .Take(50))
        {
            try
            {
                var json = File.ReadAllText(file);
                var report = JsonSerializer.Deserialize<ShredReport>(json);
                if (report is null) continue;

                rows.AddRange(report.Entries.Select(ShredHistoryRow.FromAuditEntry));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(ex, "Load history report failed: {Path}", file);
            }
        }

        return rows.OrderByDescending(static r => r.CompletedAt).Take(200).ToList();
    }

    private string ResolveReportsDirectory()
    {
        var raw = _options.Reporting.OutputDirectory;
        return ShredderAppPaths.ResolveDirectory(raw, Path.Combine("data", "reports"));
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }

    private void OnProgress(ShredProgress p)
    {
        if (Volatile.Read(ref _acceptProgressUpdates) == 0)
        {
            return;
        }

        if (p.PassCount <= 0)
        {
            ProgressState = "Running";
            ProgressText = $"正在快速粉碎 {Path.GetFileName(p.FilePath)}";
            return;
        }

        ProgressState = "Running";
        ProgressText = $"{Path.GetFileName(p.FilePath)} · pass {p.PassIndex}/{p.PassCount}";
        if (p.TotalBytes > 0)
            ProgressPercent = (double)p.BytesWritten / p.TotalBytes * 100;
    }

    private static long CalculateDirectorySize(string root)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            foreach (var file in EnumerateFilesSafe(dir))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                }
            }

            foreach (var subDir in EnumerateDirectoriesSafe(dir))
            {
                stack.Push(subDir);
            }
        }

        return total;
    }

    private static string[] EnumerateFilesSafe(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string[] EnumerateDirectoriesSafe(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }
}

/// <summary>
/// Jobs 列表的可观察行。执行后回写状态。
/// </summary>
public sealed partial class ShredJobRow : ObservableObject
{
    public ShredJobRow(string path, bool isDirectory, long sizeBytes)
    {
        Path = path;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
    }

    public string Path { get; }
    public bool IsDirectory { get; }
    [ObservableProperty] private long _sizeBytes;
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }

    public string ParentPath => System.IO.Path.GetDirectoryName(
        Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) ?? Path;

    [ObservableProperty] private ShredJobStatus _status = ShredJobStatus.Pending;
    [ObservableProperty] private string? _errorMessage;

    public string DisplaySize => SizeBytes < 0 ? "计算中" : FormatBytes(SizeBytes);

    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(DisplaySize));

    internal ShredJob BuildJob(string? algorithmId = null) => new()
    {
        Path = Path,
        IsDirectory = IsDirectory,
        SizeBytes = SizeBytes,
        AlgorithmId = algorithmId,
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

public sealed class ShredHistoryRow
{
    public required DateTimeOffset CompletedAt { get; init; }
    public required string CompletedAtText { get; init; }
    public required string Name { get; init; }
    public required string ParentPath { get; init; }
    public required string Detail { get; init; }
    public required string DisplaySize { get; init; }
    public required string StatusText { get; init; }

    public static ShredHistoryRow FromAuditEntry(ShredAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var trimmed = entry.Path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        return new ShredHistoryRow
        {
            CompletedAt = entry.CompletedAt,
            CompletedAtText = entry.CompletedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            Name = string.IsNullOrWhiteSpace(name) ? entry.Path : name,
            ParentPath = Path.GetDirectoryName(trimmed) ?? entry.Path,
            Detail = string.IsNullOrEmpty(entry.ErrorMessage) ? entry.AlgorithmName ?? string.Empty : entry.ErrorMessage,
            DisplaySize = FormatBytes(Math.Max(0, entry.SizeBytes)),
            StatusText = entry.Status switch
            {
                ShredJobStatus.Success => "成功",
                ShredJobStatus.Failed => "失败",
                ShredJobStatus.Cancelled => "取消",
                ShredJobStatus.Running => "执行中",
                _ => "待执行",
            },
        };
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
