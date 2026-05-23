using System.Diagnostics.CodeAnalysis;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Shredder.Core.Models;
using Shredder.Core.Services;

namespace Shredder.App.ViewModels;

/// <summary>
/// 回收站清空页面 VM。先粉碎 $Recycle.Bin 内的数据,再调用 Shell32 SHEmptyRecycleBin 清元数据。
/// </summary>
public sealed partial class RecycleBinPageViewModel : ObservableObject, IDisposable
{
    private readonly RecycleBinService _recycleBinService;
    private readonly ILogger<RecycleBinPageViewModel> _logger;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _statusText = "尚未开始";
    [ObservableProperty] private bool _isBusy;

    private CancellationTokenSource? _cts;

    public RecycleBinPageViewModel(
        RecycleBinService recycleBinService,
        ILogger<RecycleBinPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(recycleBinService);
        ArgumentNullException.ThrowIfNull(logger);
        _recycleBinService = recycleBinService;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:用户触发的清空回收站操作失败应展示友好错误,不应让异常冒泡到 WPF Dispatcher 触发进程崩溃。")]
    public async Task EmptyAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ShredProgress>(OnProgress);
        StatusText = "清空中…";
        try
        {
            var result = await _recycleBinService.EmptyAsync(progress, _cts.Token);
            StatusText = FormatStatus(result);
            ProgressText = result.OverallSucceeded ? "完成" : "部分失败";
            ProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            ProgressText = "已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmptyRecycleBin failed");
            StatusText = $"失败：{ex.Message}";
            ProgressText = "失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatStatus(RecycleBinEmptyResult r)
    {
        var shell = r.ShellHResult is null
            ? "(跳过)"
            : r.ShellSucceeded ? "OK" : $"FAIL 0x{r.ShellHResult:X8}";

        if (r.OverallSucceeded)
            return r.TotalCandidates == 0
                ? $"回收站已清空(无候选项,shell={shell})"
                : $"回收站已清空({r.Succeeded}/{r.TotalCandidates},shell={shell})";

        return $"部分失败:成功 {r.Succeeded},失败 {r.Failed},跳过 {r.Skipped},shell={shell}";
    }

    public void Cancel() => _cts?.Cancel();

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
