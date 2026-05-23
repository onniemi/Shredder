using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Core.FileSystem;

namespace Shredder.Core.Diagnostics;

/// <summary>
/// 默认采集器:聚合反射版本信息、运行环境、所有卷(通过 <see cref="SsdDetector"/> 标注 SSD/TRIM)、
/// 已注册算法列表与脱敏后的 <see cref="ShredderOptions"/> 快照。
/// </summary>
public sealed class DiagnosticsCollector : IDiagnosticsCollector
{
    private readonly IOptionsMonitor<ShredderOptions> _options;
    private readonly IShredAlgorithmRegistry _registry;
    private readonly SsdDetector _ssdDetector;
    private readonly ILogger<DiagnosticsCollector> _logger;

    public DiagnosticsCollector(
        IOptionsMonitor<ShredderOptions> options,
        IShredAlgorithmRegistry registry,
        SsdDetector ssdDetector,
        ILogger<DiagnosticsCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(ssdDetector);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _registry = registry;
        _ssdDetector = ssdDetector;
        _logger = logger;
    }

    public DiagnosticsInfo Collect()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";
        var appName = asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "Shredder";

#if DEBUG
        var build = "Debug";
#else
        var build = "Release";
#endif

        var opts = _options.CurrentValue;

        return new DiagnosticsInfo
        {
            AppName = appName,
            AppVersion = version,
            BuildConfiguration = build,
            RuntimeVersion = $".NET {Environment.Version}",
            OsVersion = RuntimeInformation.OSDescription,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            IsElevated = IsCurrentProcessElevated(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            WorkingSetBytes = SafeWorkingSet(),
            Drives = CollectDrives(),
            Algorithms = _registry.All
                .Select(a => new DiagnosticsAlgorithm(a.Id, a.Name, a.PassCount))
                .ToList(),
            Options = new DiagnosticsOptionsSnapshot
            {
                IoBufferSizeBytes = opts.Io.BufferSizeBytes,
                IoMaxConcurrentFiles = opts.Io.MaxConcurrentFiles,
                IoProgressReportIntervalMs = opts.Io.ProgressReportIntervalMs,
                AlgorithmDefault = opts.Algorithm.Default ?? string.Empty,
                FreeSpaceBlockSizeBytes = opts.FreeSpace.BlockSizeBytes,
                ReportingEnabled = opts.Reporting.Enabled,
                ReportingFormatJson = opts.Reporting.FormatJson,
                ReportingFormatHtml = opts.Reporting.FormatHtml,
                ReportingOutputDirectory = opts.Reporting.OutputDirectory ?? string.Empty,
                UiConfirmationKeyword = opts.Ui.ConfirmationKeyword ?? string.Empty,
                SafetyMftResidentInflateThresholdBytes = opts.Safety.MftResidentInflateThresholdBytes,
                SafetyMftResidentInflateTargetBytes = opts.Safety.MftResidentInflateTargetBytes,
                LoggingRecordRawPaths = opts.Logging.RecordRawPaths,
                LoggingOutputDirectory = opts.Logging.OutputDirectory ?? string.Empty,
                LoggingFileSinkEnabled = opts.Logging.FileSinkEnabled,
                LoggingFileSizeLimitBytes = opts.Logging.FileSizeLimitBytes,
                LoggingRetainedFileCountLimit = opts.Logging.RetainedFileCountLimit,
            },
        };
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "诊断采集需对每个卷独立容错(权限/未就绪/远程不可达),失败应记录而非中断整体采集。")]
    private List<DiagnosticsDrive> CollectDrives()
    {
        var list = new List<DiagnosticsDrive>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DriveInfo.GetDrives failed.");
            return list;
        }

        foreach (var d in drives)
        {
            try
            {
                if (!d.IsReady)
                {
                    list.Add(new DiagnosticsDrive(
                        d.Name, string.Empty, d.DriveType.ToString(),
                        SsdDetector.DeviceProfile.Unknown.ToString(), false, 0, 0, false));
                    continue;
                }

                var info = _ssdDetector.Probe(d.RootDirectory.FullName);
                list.Add(new DiagnosticsDrive(
                    d.Name,
                    d.DriveFormat ?? string.Empty,
                    d.DriveType.ToString(),
                    info.Profile.ToString(),
                    info.TrimEnabled,
                    d.TotalSize,
                    d.AvailableFreeSpace,
                    true));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Probe drive {Name} failed.", d.Name);
                list.Add(new DiagnosticsDrive(
                    d.Name, string.Empty, d.DriveType.ToString(),
                    SsdDetector.DeviceProfile.Unknown.ToString(), false, 0, 0, false));
            }
        }
        return list;
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "WindowsIdentity 在非 Windows / 受限令牌下可能抛异常,降级为 false 即可。")]
    private static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "Process.WorkingSet64 在罕见情况下可能抛异常;诊断指标缺失不应阻塞采集。")]
    private static long SafeWorkingSet()
    {
        try { return Process.GetCurrentProcess().WorkingSet64; }
        catch { return 0; }
    }
}
