using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.FileSystem;
using Shredder.Core.Models;

namespace Shredder.Core.Services;

/// <summary>
/// 空闲空间擦除:在目标卷创建大文件,循环写入随机/零数据直至接近磁盘满,
/// 然后粉碎该临时文件。SSD 默认改走 <see cref="TrimFallbackRunner"/>(ReTrim)。
/// </summary>
/// <remarks>
/// <para>
/// SSD 决策表(由 <see cref="ShredderFreeSpaceOptions.DisableOnSsd"/> 与
/// <see cref="ShredderFreeSpaceOptions.FallbackToTrimOnSsd"/> 控制):
/// </para>
/// <list type="table">
///   <listheader><term>检测</term><term>DisableOnSsd</term><term>FallbackToTrimOnSsd</term><term>动作</term></listheader>
///   <item><description>SSD/NVMe</description><description>true</description><description>true</description><description>调 defrag /L,返回 <see cref="FreeSpaceWipeOutcome.TrimFallbackInvoked"/></description></item>
///   <item><description>SSD/NVMe</description><description>true</description><description>false</description><description>跳过,返回 <see cref="FreeSpaceWipeOutcome.SkippedSsdNoFallback"/></description></item>
///   <item><description>SSD/NVMe</description><description>false</description><description>—</description><description>按 HDD 路径继续覆写(用户显式选择)</description></item>
///   <item><description>HDD / Unknown</description><description>—</description><description>—</description><description>随机 + 零 两 pass,返回 <see cref="FreeSpaceWipeOutcome.OverwriteCompleted"/></description></item>
/// </list>
/// </remarks>
public sealed class FreeSpaceService
{
    // ERROR_DISK_FULL (0x70), ERROR_HANDLE_DISK_FULL (0x27)
    private const int ERROR_DISK_FULL = 0x70;
    private const int ERROR_HANDLE_DISK_FULL = 0x27;

    private readonly ShredderOptions _options;
    private readonly SsdDetector _ssdDetector;
    private readonly TrimFallbackRunner _trimRunner;
    private readonly ILogger<FreeSpaceService> _logger;

    public FreeSpaceService(
        IOptions<ShredderOptions> options,
        SsdDetector ssdDetector,
        TrimFallbackRunner trimRunner,
        ILogger<FreeSpaceService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ssdDetector);
        ArgumentNullException.ThrowIfNull(trimRunner);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _ssdDetector = ssdDetector;
        _trimRunner = trimRunner;
        _logger = logger;
    }

    public async Task<FreeSpaceWipeResult> WipeAsync(
        string driveRoot,
        IProgress<ShredProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        if (!Directory.Exists(driveRoot))
            throw new DirectoryNotFoundException(driveRoot);

        var freeSpaceCfg = _options.FreeSpace;
        int blockSize = freeSpaceCfg.BlockSizeBytes;
        if (blockSize <= 0) throw new InvalidOperationException("FreeSpace.BlockSizeBytes 必须为正数。");
        long minBuffer = Math.Max(0, freeSpaceCfg.MinimumFreeBytesBuffer);

        // 1. SSD 路由决策
        var storage = ProbeStorageSafe(driveRoot);
        if (storage.Profile == SsdDetector.DeviceProfile.SolidState && freeSpaceCfg.DisableOnSsd)
        {
            if (freeSpaceCfg.FallbackToTrimOnSsd)
            {
                _logger.LogInformation(
                    "FreeSpace wipe: SSD detected on {Drive}; switching to defrag /L (ReTrim).",
                    driveRoot);
                var trim = await _trimRunner.RunAsync(driveRoot, _logger, ct).ConfigureAwait(false);
                return new FreeSpaceWipeResult(
                    Outcome: FreeSpaceWipeOutcome.TrimFallbackInvoked,
                    BytesWritten: 0,
                    Message: trim.Success
                        ? $"已对 {driveRoot} 重新发送 TRIM(defrag /L,退出码 0)。"
                        : $"ReTrim 退出码 {trim.ExitCode}: {trim.StandardError}".Trim());
            }

            _logger.LogInformation(
                "FreeSpace wipe: SSD detected on {Drive} and FallbackToTrimOnSsd disabled; skipping.",
                driveRoot);
            return new FreeSpaceWipeResult(
                Outcome: FreeSpaceWipeOutcome.SkippedSsdNoFallback,
                BytesWritten: 0,
                Message: $"目标 {driveRoot} 为 SSD/NVMe,已按配置跳过软件覆写(覆写不保证物理擦除)。");
        }

        // 2. 覆写路径:HDD 或用户显式 DisableOnSsd=false
        var tempPath = Path.Combine(driveRoot, $"~shred_freespace_{Guid.NewGuid():N}.tmp");
        var buffer = new byte[blockSize];
        long bytesWritten = 0;

        _logger.LogInformation(
            "FreeSpace wipe start: drive={Drive} block={Block} reserve={Reserve} ssd={Ssd}",
            driveRoot, blockSize, minBuffer, storage.Profile);

        try
        {
            // Pass 1: 随机
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                                                 FileShare.None, blockSize, useAsync: true))
            {
                bytesWritten += await FillUntilFullAsync(
                    fs, buffer, random: true, progress, tempPath, driveRoot, minBuffer, 1, 2, ct);
                fs.Flush(true);
            }
            // Pass 2: 零
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                                                 FileShare.None, blockSize, useAsync: true))
            {
                Array.Clear(buffer);
                bytesWritten += await FillUntilFullAsync(
                    fs, buffer, random: false, progress, tempPath, driveRoot, minBuffer, 2, 2, ct);
                fs.Flush(true);
            }
        }
        finally
        {
            TryDeleteTemp(tempPath);
        }

        _logger.LogInformation("FreeSpace wipe done: drive={Drive} bytes={Bytes}", driveRoot, bytesWritten);
        return new FreeSpaceWipeResult(
            Outcome: FreeSpaceWipeOutcome.OverwriteCompleted,
            BytesWritten: bytesWritten,
            Message: $"{driveRoot} 空闲空间覆写完成,累计写入 {bytesWritten:N0} 字节。");
    }

    private SsdDetector.StorageInfo ProbeStorageSafe(string driveRoot)
    {
        try { return _ssdDetector.Probe(driveRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 探测失败时按 Unknown 走 HDD 覆写路径,绝不因探测崩了就拒绝擦除
            _logger.LogWarning(ex, "FreeSpace: SSD detection failed for {Drive}, falling back to overwrite.", driveRoot);
            return new SsdDetector.StorageInfo(SsdDetector.DeviceProfile.Unknown, false);
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "临时填充文件清理是 best-effort:即使删除失败(被杀软占用/卷只读)也不应让擦除流程整体失败,异常已记录。")]
    private void TryDeleteTemp(string tempPath)
    {
        if (!File.Exists(tempPath)) return;
        try { File.Delete(tempPath); }
        catch (Exception ex) { _logger.LogWarning(ex, "FreeSpace temp file delete failed: {Path}", tempPath); }
    }

    private async Task<long> FillUntilFullAsync(
        FileStream fs, byte[] buffer, bool random,
        IProgress<ShredProgress>? progress, string path,
        string driveRoot, long minBuffer,
        int pass, int total, CancellationToken ct)
    {
        long totalWritten = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // 在每个 block 前主动检查 AvailableFreeSpace,确保留出 MinimumFreeBytesBuffer
                // 不再依赖 ERROR_DISK_FULL(那意味着已经写穿了 0 字节余量),也保留 catch 作为兜底
                if (minBuffer > 0 && !CanWriteMoreWithoutCrossingBuffer(driveRoot, buffer.Length, minBuffer))
                {
                    _logger.LogDebug(
                        "FreeSpace pass {Pass}/{Total} stop: would cross MinimumFreeBytesBuffer={Reserve} after writing {Block} bytes.",
                        pass, total, minBuffer, buffer.Length);
                    break;
                }

                if (random) RandomNumberGenerator.Fill(buffer);
                await fs.WriteAsync(buffer, ct);
                totalWritten += buffer.Length;
                progress?.Report(new ShredProgress(path, pass, total, totalWritten, -1));
            }
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            // 兜底:并发其他写入抢先一步把空间吃光时,polling 间隙也可能撞上 ERROR_DISK_FULL
            _logger.LogDebug("FreeSpace pass {Pass}/{Total} reached disk-full at {Bytes} bytes", pass, total, totalWritten);
        }
        return totalWritten;
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "DriveInfo 抛任何异常时按'还能写'继续,真正的写失败会在 WriteAsync 触发 ERROR_DISK_FULL 兜底。")]
    private static bool CanWriteMoreWithoutCrossingBuffer(string driveRoot, int blockSize, long minBuffer)
    {
        try
        {
            var di = new DriveInfo(driveRoot);
            // 写入 blockSize 之后,剩余空间必须仍 >= minBuffer
            return di.AvailableFreeSpace - blockSize >= minBuffer;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsDiskFull(IOException ex)
    {
        var hr = Marshal.GetHRForException(ex);
        var code = hr & 0xFFFF;
        if (code == ERROR_DISK_FULL || code == ERROR_HANDLE_DISK_FULL) return true;
        if (ex.InnerException is Win32Exception w32 &&
            (w32.NativeErrorCode == ERROR_DISK_FULL || w32.NativeErrorCode == ERROR_HANDLE_DISK_FULL))
            return true;
        return false;
    }
}

/// <summary>空闲空间擦除的结果。同一次调用要么走覆写、要么走 TRIM、要么被跳过。</summary>
public sealed record FreeSpaceWipeResult(
    FreeSpaceWipeOutcome Outcome,
    long BytesWritten,
    string? Message);

/// <summary>空闲空间擦除的实际动作。</summary>
public enum FreeSpaceWipeOutcome
{
    /// <summary>HDD / 未知介质 / 用户禁用 SSD 保护时:随机+零两 pass 覆写完成。</summary>
    OverwriteCompleted = 0,
    /// <summary>SSD 上 DisableOnSsd=true 且 FallbackToTrimOnSsd=false:仅记日志,不做物理覆写。</summary>
    SkippedSsdNoFallback = 1,
    /// <summary>SSD 上 DisableOnSsd=true 且 FallbackToTrimOnSsd=true:调用 <c>defrag /L</c> ReTrim。</summary>
    TrimFallbackInvoked = 2,
}
