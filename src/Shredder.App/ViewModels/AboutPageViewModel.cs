using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Diagnostics;

namespace Shredder.App.ViewModels;

/// <summary>
/// 关于页 VM。展示版本、构建、运行时信息、许可与第三方组件;
/// 同时提供「导出诊断包」入口,采集后写到 <c>%LOCALAPPDATA%\Shredder\Diagnostics</c>。
/// </summary>
public sealed partial class AboutPageViewModel : ObservableObject
{
    private readonly IDiagnosticsCollector _collector;
    private readonly IDiagnosticsExporter _exporter;
    private readonly IOptionsMonitor<ShredderOptions> _options;
    private readonly ILogger<AboutPageViewModel> _logger;

    public string AppName { get; }
    public string Version { get; }
    public string BuildConfiguration { get; }
    public string RuntimeVersion { get; }
    public string OsVersion { get; }
    public string Copyright { get; }
    public string Repository { get; } = "https://github.com/ — 待发布";
    public string License { get; } = "MIT License（开源、免费、可自由使用、修改和分发）";

    public IReadOnlyList<ThirdPartyNoticeItem> ThirdPartyNotices { get; }

    [ObservableProperty]
    private string? _lastDiagnosticsPath;

    [ObservableProperty]
    private bool _isExportingDiagnostics;

    [ObservableProperty]
    private string? _diagnosticsStatus;

    public AboutPageViewModel(
        IDiagnosticsCollector collector,
        IDiagnosticsExporter exporter,
        IOptionsMonitor<ShredderOptions> options,
        ILogger<AboutPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _collector = collector;
        _exporter = exporter;
        _options = options;
        _logger = logger;

        var asm = Assembly.GetExecutingAssembly();
        AppName = asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "Shredder";

        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var file = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var nameVersion = asm.GetName().Version?.ToString();
        Version = informational ?? file ?? nameVersion ?? "0.0.0";

#if DEBUG
        BuildConfiguration = "Debug";
#else
        BuildConfiguration = "Release";
#endif

        RuntimeVersion = $".NET {Environment.Version}";
        OsVersion = Environment.OSVersion.VersionString;
        Copyright = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
                    ?? $"© {DateTime.Now.Year} Shredder Contributors";

        ThirdPartyNotices = new[]
        {
            new ThirdPartyNoticeItem("WPF-UI", "MIT", "https://github.com/lepoco/wpfui"),
            new ThirdPartyNoticeItem("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
            new ThirdPartyNoticeItem("Microsoft.Extensions.*", "MIT", "https://github.com/dotnet/runtime"),
        };
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:采集/写盘失败不应把异常冲到 Dispatcher;统一记录并在 UI 显示状态文本即可。")]
    public async Task ExportDiagnosticsAsync()
    {
        if (IsExportingDiagnostics) return;
        IsExportingDiagnostics = true;
        DiagnosticsStatus = "采集中…";
        try
        {
            var info = await Task.Run(_collector.Collect).ConfigureAwait(true);
            DiagnosticsStatus = "正在写入诊断包…";
            var path = await _exporter.ExportAsync(info).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(path))
            {
                LastDiagnosticsPath = path;
                DiagnosticsStatus = $"已生成: {path}";
            }
            else
            {
                DiagnosticsStatus = "导出失败,详情见日志。";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export diagnostics failed.");
            DiagnosticsStatus = "导出失败,详情见日志。";
        }
        finally
        {
            IsExportingDiagnostics = false;
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "打开资源管理器失败不应崩溃 Dispatcher。")]
    public void OpenDiagnosticsFolder()
    {
        var dir = ResolveDiagnosticsDirectory();
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open diagnostics folder failed: {Dir}", dir);
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "打开资源管理器失败不应崩溃 Dispatcher。")]
    public void OpenLogsFolder()
    {
        var dir = ResolveLogsDirectory();
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open logs folder failed: {Dir}", dir);
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "打开诊断包失败不应崩溃 Dispatcher。")]
    public void OpenLastDiagnostics()
    {
        if (string.IsNullOrEmpty(LastDiagnosticsPath) || !File.Exists(LastDiagnosticsPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(LastDiagnosticsPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open last diagnostics failed: {Path}", LastDiagnosticsPath);
        }
    }

    private static string ResolveDiagnosticsDirectory()
    {
        var expanded = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\Shredder\\Diagnostics");
        return Path.GetFullPath(expanded);
    }

    private string ResolveLogsDirectory()
    {
        var raw = _options.CurrentValue.Logging.OutputDirectory;
        var expanded = Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(raw) ? "%LOCALAPPDATA%\\Shredder\\Logs" : raw);
        return Path.GetFullPath(expanded);
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:Process.Start 失败(Shell 未关联浏览器、URL 异常等)不允许把异常冲到 Dispatcher;吞掉即可。")]
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 浏览器未配置或权限不足时静默失败,UI 不应崩溃
        }
    }
}

/// <summary>第三方组件条目，绑定给 ListView。</summary>
public sealed record ThirdPartyNoticeItem(string Name, string License, string Url);
