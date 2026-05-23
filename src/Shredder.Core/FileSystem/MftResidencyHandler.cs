using System.Security.Cryptography;

namespace Shredder.Core.FileSystem;

/// <summary>
/// 处理 NTFS MFT 驻留小文件:小于阈值(默认 700 字节)的文件其数据完全嵌在 MFT
/// record 内,而非分配独立簇。普通磁盘覆写只能改簇,改不到 MFT 内的数据残留,
/// 因此粉碎前必须先把文件膨胀到 ≥1 个簇(默认 4096 字节),让数据搬出 MFT。
/// </summary>
/// <remarks>
/// 严格意义上,NTFS 不保证文件搬出 MFT 后旧的驻留副本一定被立刻覆写;
/// 但本工具的目标是「让用户原始内容在通用工具下不可恢复」,而非
/// 「对抗 raw MFT 取证」。后者需要 sdelete -p 那种循环重写卷的能力。
/// </remarks>
public sealed class MftResidencyHandler
{
    private readonly int _thresholdBytes;
    private readonly int _targetBytes;

    public MftResidencyHandler(int thresholdBytes, int targetBytes)
    {
        if (thresholdBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdBytes), "阈值不能为负数。");
        if (targetBytes <= thresholdBytes)
            throw new ArgumentOutOfRangeException(nameof(targetBytes), "目标膨胀大小必须大于阈值。");
        _thresholdBytes = thresholdBytes;
        _targetBytes = targetBytes;
    }

    /// <summary>
    /// 若文件长度 ≤ 阈值,把它扩展到 <see cref="_targetBytes"/>,写入随机字节后调用方再正常粉碎。
    /// 返回是否实际进行了膨胀。
    /// </summary>
    /// <param name="path">已规范化的绝对路径。</param>
    public async Task<bool> InflateIfResidentAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var extended = LongPathHelper.ToExtendedPathIfNeeded(path);
        var info = new FileInfo(extended);
        if (!info.Exists) return false;
        if (info.Length > _thresholdBytes) return false;

        long inflateBy = _targetBytes - info.Length;
        if (inflateBy <= 0) return false;

        // 用 Open + 末尾追加随机字节,保留原内容(随后会被粉碎算法覆写)。
        await using var fs = new FileStream(
            extended,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        fs.Seek(0, SeekOrigin.End);

        const int chunk = 4096;
        var buffer = new byte[chunk];
        long remaining = inflateBy;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            int write = (int)Math.Min(chunk, remaining);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, write));
            await fs.WriteAsync(buffer.AsMemory(0, write), ct);
            remaining -= write;
        }
        await fs.FlushAsync(ct);
        fs.Flush(true);
        return true;
    }
}
