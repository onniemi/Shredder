using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Diagnostics;
using Shredder.Core.Models;

namespace Shredder.Core.Services;

/// <summary>清空回收站:先对其中的项做粉碎,再调用 Win32 SHEmptyRecycleBin。</summary>
/// <remarks>
/// 单项失败被记录到 <see cref="RecycleBinEmptyResult.FailedItems"/>但不中断整体流程;
/// 失败路径在记录前会经过 <see cref="PathHasher"/>脱敏,日志中的 <c>{Path}</c>占位符也会被
/// <c>PathRedactingEnricher</c>统一脱敏,因此本类不需要再手工拼接原始路径。
/// </remarks>
public sealed class RecycleBinService
{
    private readonly IRecycleBinEnumerator _enumerator;
    private readonly IRecycleBinFileShredder _fileShredder;
    private readonly IRecycleBinShell _shell;
    private readonly ShredderOptions _options;
    private readonly ILogger<RecycleBinService> _logger;

    public RecycleBinService(
        IRecycleBinEnumerator enumerator,
        IRecycleBinFileShredder fileShredder,
        IRecycleBinShell shell,
        IOptions<ShredderOptions> options,
        ILogger<RecycleBinService> logger)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(fileShredder);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _enumerator = enumerator;
        _fileShredder = fileShredder;
        _shell = shell;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 清空回收站。单项失败不会中断整体处理,失败明细以脱敏形式聚合到返回的
    /// <see cref="RecycleBinEmptyResult"/>中。
    /// </summary>
    public async Task<RecycleBinEmptyResult> EmptyAsync(IProgress<ShredProgress>? progress, CancellationToken ct)
    {
        _logger.LogInformation(
            "RecycleBin empty start: overwrite={Overwrite} callShell={CallShell}",
            _options.RecycleBin.OverwriteContents,
            _options.RecycleBin.CallShellEmptyAfterShred);

        int total = 0;
        int succeeded = 0;
        int skipped = 0;
        var failedItems = new List<RecycleBinFailedItem>();

        if (_options.RecycleBin.OverwriteContents)
        {
            foreach (var file in _enumerator.EnumerateFiles(ct))
            {
                ct.ThrowIfCancellationRequested();
                total++;
                var status = await TryShredOneAsync(file, progress, ct);
                switch (status.Kind)
                {
                    case TryShredResultKind.Succeeded: succeeded++; break;
                    case TryShredResultKind.Failed:
                        failedItems.Add(new RecycleBinFailedItem
                        {
                            PathRedacted = PathHasher.Hash(file),
                            Reason = status.Reason ?? "Unknown",
                            HResult = status.HResult,
                        });
                        break;
                }
            }
        }
        else
        {
            // OverwriteContents=false:不去枚举/粉碎,直接走 shell 清理元数据
            _logger.LogInformation("RecycleBin overwrite disabled by configuration, shred phase skipped");
        }

        int? shellHr = null;
        if (_options.RecycleBin.CallShellEmptyAfterShred)
        {
            shellHr = _shell.Empty();
            // S_OK=0, S_FALSE=1 (回收站已空,正常),其它视为告警
            if (shellHr != 0 && shellHr != 1)
                _logger.LogWarning("SHEmptyRecycleBin returned HRESULT 0x{Hr:X8}", shellHr);
        }

        var result = new RecycleBinEmptyResult
        {
            TotalCandidates = total,
            Succeeded = succeeded,
            Failed = failedItems.Count,
            Skipped = skipped,
            ShellHResult = shellHr,
            FailedItems = failedItems,
        };

        _logger.LogInformation(
            "RecycleBin empty done: total={Total} ok={Ok} failed={Failed} skipped={Skipped} shellHr={ShellHr}",
            result.TotalCandidates, result.Succeeded, result.Failed, result.Skipped,
            result.ShellHResult is null ? "(skipped)" : $"0x{result.ShellHResult:X8}");

        return result;
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "回收站逐项粉碎:单项失败应记录并继续,不应中断整个清空流程。OperationCanceledException 已单独重抛。")]
    private async Task<TryShredResult> TryShredOneAsync(string file, IProgress<ShredProgress>? progress, CancellationToken ct)
    {
        try
        {
            await _fileShredder.ShredFileAsync(file, progress, ct);
            return TryShredResult.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 日志里只用 {Path} 占位符,PathRedactingEnricher 会脱敏;消息体也不要拼接原始路径
            _logger.LogWarning(ex, "RecycleBin item failed: type={Type} hr=0x{Hr:X8} Path={Path}",
                ex.GetType().Name, ex.HResult, file);
            return new TryShredResult(TryShredResultKind.Failed, ex.GetType().Name, ex.HResult);
        }
    }

    private enum TryShredResultKind { Succeeded, Failed }

    private readonly struct TryShredResult
    {
        public TryShredResultKind Kind { get; }
        public string? Reason { get; }
        public int? HResult { get; }

        public TryShredResult(TryShredResultKind kind, string? reason, int? hresult)
        {
            Kind = kind;
            Reason = reason;
            HResult = hresult;
        }

        public static TryShredResult Success { get; } = new(TryShredResultKind.Succeeded, null, null);
    }
}
