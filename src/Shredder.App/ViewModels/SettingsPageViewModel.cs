using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Integration;

namespace Shredder.App.ViewModels;

/// <summary>
/// 设置页 VM。负责把 <see cref="ShredderOptions"/> 各子节绑定到 UI,
/// 并把改动回写到 appsettings.json(System.Text.Json 节点级合并,保留未知字段)。
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions s_serializeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // 保持 PascalCase
    };

    private readonly ShredderOptions _options;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private readonly string _settingsPath;

    public IReadOnlyList<IShredAlgorithm> Algorithms { get; }
    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "System", "Light", "Dark" };

    // ---- UI ----
    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _confirmationKeyword;
    [ObservableProperty] private int _confirmationCooldownSeconds;

    // ---- Algorithm ----
    [ObservableProperty] private IShredAlgorithm? _defaultAlgorithm;
    [ObservableProperty] private IShredAlgorithm? _ssdDefaultAlgorithm;

    // ---- Safety ----
    [ObservableProperty] private bool _rejectReparsePoints;
    [ObservableProperty] private bool _shredAlternateDataStreams;
    [ObservableProperty] private bool _detectSolidStateDrives;
    [ObservableProperty] private bool _preferTrimForSsd;
    [ObservableProperty] private bool _useRestartManagerForLockedFiles;
    [ObservableProperty] private bool _allowScheduleOnRebootDelete;
    [ObservableProperty] private int _mftInflateThresholdBytes;
    [ObservableProperty] private int _mftInflateTargetBytes;

    // ---- IO ----
    [ObservableProperty] private int _ioBufferSizeBytes;
    [ObservableProperty] private int _maxConcurrentFiles;
    [ObservableProperty] private int _progressReportIntervalMs;
    [ObservableProperty] private bool _useUnbufferedIo;

    // ---- FreeSpace ----
    [ObservableProperty] private int _freeSpaceBlockSizeBytes;
    [ObservableProperty] private long _freeSpaceMinimumBufferBytes;
    [ObservableProperty] private bool _freeSpaceDisableOnSsd;
    [ObservableProperty] private bool _freeSpaceFallbackToTrimOnSsd;

    // ---- RecycleBin ----
    [ObservableProperty] private bool _recycleBinProcessAllDrives;
    [ObservableProperty] private bool _recycleBinOverwriteContents;
    [ObservableProperty] private bool _recycleBinCallShellEmpty;

    // ---- Reporting ----
    [ObservableProperty] private bool _reportingEnabled;
    [ObservableProperty] private string _reportingOutputDirectory = string.Empty;
    [ObservableProperty] private bool _reportingFormatJson;
    [ObservableProperty] private bool _reportingFormatHtml;
    [ObservableProperty] private bool _reportingAutoOpen;

    // ---- Paths ----
    public ObservableCollection<string> ForbiddenPaths { get; } = new();
    public ObservableCollection<string> WarnPaths { get; } = new();
    public ObservableCollection<string> AllowPaths { get; } = new();

    // ---- Save status ----
    [ObservableProperty] private string _saveStatus = string.Empty;

    // ---- ShellMenu (资源管理器右键菜单 HKCU) ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShellMenuStateLabel))]
    private bool _isShellMenuInstalled;
    [ObservableProperty] private string _shellMenuStatus = string.Empty;
    [ObservableProperty] private string _shellMenuInstalledExePath = string.Empty;
    [ObservableProperty] private string _shellMenuCurrentExePath = string.Empty;

    public string ShellMenuStateLabel => IsShellMenuInstalled ? "已安装" : "未安装";

    public SettingsPageViewModel(
        IOptions<ShredderOptions> options,
        IShredAlgorithmRegistry registry,
        ILogger<SettingsPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;

        Algorithms = registry.All;
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        // 初值
        _theme = _options.Ui.Theme;
        _confirmationKeyword = _options.Ui.ConfirmationKeyword;
        _confirmationCooldownSeconds = _options.Ui.ConfirmationCooldownSeconds;

        _defaultAlgorithm = registry.Find(_options.Algorithm.Default);
        _ssdDefaultAlgorithm = string.IsNullOrWhiteSpace(_options.Algorithm.SsdDefault)
            ? null : registry.Find(_options.Algorithm.SsdDefault);

        _rejectReparsePoints = _options.Safety.RejectReparsePoints;
        _shredAlternateDataStreams = _options.Safety.ShredAlternateDataStreams;
        _detectSolidStateDrives = _options.Safety.DetectSolidStateDrives;
        _preferTrimForSsd = _options.Safety.PreferTrimForSsd;
        _useRestartManagerForLockedFiles = _options.Safety.UseRestartManagerForLockedFiles;
        _allowScheduleOnRebootDelete = _options.Safety.AllowScheduleOnRebootDelete;
        _mftInflateThresholdBytes = _options.Safety.MftResidentInflateThresholdBytes;
        _mftInflateTargetBytes = _options.Safety.MftResidentInflateTargetBytes;

        _ioBufferSizeBytes = _options.Io.BufferSizeBytes;
        _maxConcurrentFiles = _options.Io.MaxConcurrentFiles;
        _progressReportIntervalMs = _options.Io.ProgressReportIntervalMs;
        _useUnbufferedIo = _options.Io.UseUnbufferedIo;

        _freeSpaceBlockSizeBytes = _options.FreeSpace.BlockSizeBytes;
        _freeSpaceMinimumBufferBytes = _options.FreeSpace.MinimumFreeBytesBuffer;
        _freeSpaceDisableOnSsd = _options.FreeSpace.DisableOnSsd;
        _freeSpaceFallbackToTrimOnSsd = _options.FreeSpace.FallbackToTrimOnSsd;

        _recycleBinProcessAllDrives = _options.RecycleBin.ProcessAllDrives;
        _recycleBinOverwriteContents = _options.RecycleBin.OverwriteContents;
        _recycleBinCallShellEmpty = _options.RecycleBin.CallShellEmptyAfterShred;

        _reportingEnabled = _options.Reporting.Enabled;
        _reportingOutputDirectory = _options.Reporting.OutputDirectory;
        _reportingFormatJson = _options.Reporting.FormatJson;
        _reportingFormatHtml = _options.Reporting.FormatHtml;
        _reportingAutoOpen = _options.Reporting.AutoOpen;

        foreach (var p in _options.Safety.ForbiddenPaths) ForbiddenPaths.Add(p);
        foreach (var p in _options.Safety.WarnPaths) WarnPaths.Add(p);
        foreach (var p in _options.Safety.AllowPaths) AllowPaths.Add(p);

        _shellMenuCurrentExePath = ResolveCurrentExePath();
        RefreshShellMenuStatus();
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:写配置失败需要展示给用户而非崩溃 Dispatcher;IOException/UnauthorizedAccessException/JsonException 已涵盖,兜底 catch 防意外类型。")]
    public async Task SaveAsync()
    {
        try
        {
            // 同步到内存 _options(下次 Get<IOptions> 仍会拿旧值,但本会话内的 VM 是热的)
            _options.Ui.Theme = Theme;
            _options.Ui.ConfirmationKeyword = ConfirmationKeyword;
            _options.Ui.ConfirmationCooldownSeconds = ConfirmationCooldownSeconds;

            _options.Algorithm.Default = DefaultAlgorithm?.Id ?? _options.Algorithm.Default;
            _options.Algorithm.SsdDefault = SsdDefaultAlgorithm?.Id ?? string.Empty;

            _options.Safety.RejectReparsePoints = RejectReparsePoints;
            _options.Safety.ShredAlternateDataStreams = ShredAlternateDataStreams;
            _options.Safety.DetectSolidStateDrives = DetectSolidStateDrives;
            _options.Safety.PreferTrimForSsd = PreferTrimForSsd;
            _options.Safety.UseRestartManagerForLockedFiles = UseRestartManagerForLockedFiles;
            _options.Safety.AllowScheduleOnRebootDelete = AllowScheduleOnRebootDelete;
            _options.Safety.MftResidentInflateThresholdBytes = MftInflateThresholdBytes;
            _options.Safety.MftResidentInflateTargetBytes = MftInflateTargetBytes;
            _options.Safety.ForbiddenPaths = ForbiddenPaths.ToList();
            _options.Safety.WarnPaths = WarnPaths.ToList();
            _options.Safety.AllowPaths = AllowPaths.ToList();

            _options.Io.BufferSizeBytes = IoBufferSizeBytes;
            _options.Io.MaxConcurrentFiles = MaxConcurrentFiles;
            _options.Io.ProgressReportIntervalMs = ProgressReportIntervalMs;
            _options.Io.UseUnbufferedIo = UseUnbufferedIo;

            _options.FreeSpace.BlockSizeBytes = FreeSpaceBlockSizeBytes;
            _options.FreeSpace.MinimumFreeBytesBuffer = FreeSpaceMinimumBufferBytes;
            _options.FreeSpace.DisableOnSsd = FreeSpaceDisableOnSsd;
            _options.FreeSpace.FallbackToTrimOnSsd = FreeSpaceFallbackToTrimOnSsd;

            _options.RecycleBin.ProcessAllDrives = RecycleBinProcessAllDrives;
            _options.RecycleBin.OverwriteContents = RecycleBinOverwriteContents;
            _options.RecycleBin.CallShellEmptyAfterShred = RecycleBinCallShellEmpty;

            _options.Reporting.Enabled = ReportingEnabled;
            _options.Reporting.OutputDirectory = ReportingOutputDirectory ?? string.Empty;
            _options.Reporting.FormatJson = ReportingFormatJson;
            _options.Reporting.FormatHtml = ReportingFormatHtml;
            _options.Reporting.AutoOpen = ReportingAutoOpen;

            // 节点级合并到磁盘文件:只覆盖 Shredder 节,保留其它节(Logging 等)
            await PersistToDiskAsync();

            SaveStatus = $"已保存 · {DateTime.Now:HH:mm:ss}";
            _logger.LogInformation("Settings saved to {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save settings failed: {Path}", _settingsPath);
            SaveStatus = $"保存失败：{ex.Message}";
        }
    }

    private async Task PersistToDiskAsync()
    {
        JsonObject root;
        if (File.Exists(_settingsPath))
        {
            var text = await File.ReadAllTextAsync(_settingsPath);
            root = (JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        // 把 _options 序列化为节点,塞回 Shredder 节
        var optsJson = JsonSerializer.SerializeToNode(_options, s_serializeOptions)!;
        root[ShredderOptions.SectionName] = optsJson;

        var output = root.ToJsonString(s_serializeOptions);
        await File.WriteAllTextAsync(_settingsPath, output);
    }

    // ---------------------------------------------------------------
    // 资源管理器右键菜单 (HKCU)
    // ---------------------------------------------------------------

    /// <summary>
    /// 刷新当前右键菜单安装状态，同时回填已注册的目标 exe 路径供 UI 展示。
    /// </summary>
    public void RefreshShellMenuStatus()
    {
        try
        {
            IsShellMenuInstalled = ShellMenuInstaller.IsInstalled();
            ShellMenuInstalledExePath = ShellMenuInstaller.GetInstalledExePath() ?? string.Empty;
        }
        catch (System.Security.SecurityException ex)
        {
            IsShellMenuInstalled = false;
            ShellMenuInstalledExePath = string.Empty;
            ShellMenuStatus = $"读取注册表失败：{ex.Message}";
            _logger.LogWarning(ex, "ShellMenu: read registry failed");
        }
        catch (System.UnauthorizedAccessException ex)
        {
            IsShellMenuInstalled = false;
            ShellMenuInstalledExePath = string.Empty;
            ShellMenuStatus = $"读取注册表权限不足：{ex.Message}";
            _logger.LogWarning(ex, "ShellMenu: read registry unauthorized");
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界：写注册表失败需要展示给用户而非崩溃 Dispatcher。")]
    public void InstallShellMenu()
    {
        var exe = ResolveCurrentExePath();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            ShellMenuStatus = "安装失败：无法定位当前可执行文件路径。";
            _logger.LogWarning("ShellMenu install aborted: exe path not resolvable ({Path})", exe);
            return;
        }

        try
        {
            ShellMenuInstaller.Install(exe);
            RefreshShellMenuStatus();
            ShellMenuStatus = $"已安装 · {DateTime.Now:HH:mm:ss}";
            _logger.LogInformation("ShellMenu installed -> {Exe}", exe);
        }
        catch (Exception ex)
        {
            ShellMenuStatus = $"安装失败：{ex.Message}";
            _logger.LogError(ex, "ShellMenu install failed");
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界：写注册表失败需要展示给用户而非崩溃 Dispatcher。")]
    public void UninstallShellMenu()
    {
        try
        {
            ShellMenuInstaller.Uninstall();
            RefreshShellMenuStatus();
            ShellMenuStatus = $"已卸载 · {DateTime.Now:HH:mm:ss}";
            _logger.LogInformation("ShellMenu uninstalled");
        }
        catch (Exception ex)
        {
            ShellMenuStatus = $"卸载失败：{ex.Message}";
            _logger.LogError(ex, "ShellMenu uninstall failed");
        }
    }

    private static string ResolveCurrentExePath()
    {
        // .NET 6+ 推荐：Environment.ProcessPath
        var p = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(p)) return p;

        return string.Empty;
    }
}
