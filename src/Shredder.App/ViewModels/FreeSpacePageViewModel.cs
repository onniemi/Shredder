using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Shredder.Core.Models;
using Shredder.Core.Services;

namespace Shredder.App.ViewModels;

/// <summary>
/// 空闲空间擦除页面 VM。
/// 列出固定盘符,允许用户挑一个进行 free-space wipe。SSD 由 FreeSpaceService 内部决策(配置见 FreeSpace.DisableOnSsd / FallbackToTrimOnSsd)。
/// </summary>
public sealed partial class FreeSpacePageViewModel : ObservableObject, IDisposable
{
    private readonly FreeSpaceService _freeSpaceService;
    private readonly ILogger<FreeSpacePageViewModel> _logger;

    public ObservableCollection<DriveInfo> Drives { get; } = new();

    [ObservableProperty] private DriveInfo? _selectedDrive;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    private CancellationTokenSource? _cts;

    public FreeSpacePageViewModel(
        FreeSpaceService freeSpaceService,
        ILogger<FreeSpacePageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(freeSpaceService);
        ArgumentNullException.ThrowIfNull(logger);
        _freeSpaceService = freeSpaceService;
        _logger = logger;

        RefreshDrives();
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "枚举盘符可能因移动介质瞬时不可用抛任意异常,刷新动作应静默降级而不是崩 UI。")]
    public void RefreshDrives()
    {
        Drives.Clear();
        try
        {
            foreach (var d in DriveInfo.GetDrives()
                         .Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                Drives.Add(d);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerate drives failed.");
        }
        SelectedDrive ??= Drives.FirstOrDefault();
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:擦除空闲空间是高风险长任务,失败需要展示完整错误信息给用户,不应让异常冒泡到 Dispatcher。")]
    public async Task WipeAsync()
    {
        if (IsBusy) return;
        if (SelectedDrive is null) { ProgressText = "请选择一个盘符"; return; }

        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ShredProgress>(OnProgress);
        string drive = SelectedDrive.RootDirectory.FullName;
        try
        {
            var result = await _freeSpaceService.WipeAsync(drive, progress, _cts.Token);
            switch (result.Outcome)
            {
                case FreeSpaceWipeOutcome.OverwriteCompleted:
                    ProgressText = result.Message ?? $"{drive} 空闲空间擦除完成";
                    ProgressPercent = 100;
                    break;
                case FreeSpaceWipeOutcome.TrimFallbackInvoked:
                    // SSD 走 ReTrim:没有真正的覆写进度,只展示结果文案,不把进度条拉满制造误导
                    ProgressText = result.Message ?? $"{drive} 已重新发送 TRIM(defrag /L)";
                    ProgressPercent = 0;
                    break;
                case FreeSpaceWipeOutcome.SkippedSsdNoFallback:
                    // 软件覆写不保证 SSD 物理擦除,按配置跳过时如实告诉用户什么都没发生
                    ProgressText = result.Message ?? $"{drive} 为 SSD/NVMe,已按配置跳过软件覆写";
                    ProgressPercent = 0;
                    break;
                default:
                    ProgressText = result.Message ?? string.Empty;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WipeFreeSpace failed: {Drive}", drive);
            ProgressText = $"失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }

    private void OnProgress(ShredProgress p)
    {
        // 空闲空间写满时 TotalBytes 是 -1(未知),只能显示累计写入量
        if (p.TotalBytes > 0)
        {
            ProgressPercent = (double)p.BytesWritten / p.TotalBytes * 100;
            ProgressText = $"pass {p.PassIndex}/{p.PassCount} · {FormatBytes(p.BytesWritten)}/{FormatBytes(p.TotalBytes)}";
        }
        else
        {
            ProgressText = $"pass {p.PassIndex}/{p.PassCount} · 已写入 {FormatBytes(p.BytesWritten)}";
        }
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
