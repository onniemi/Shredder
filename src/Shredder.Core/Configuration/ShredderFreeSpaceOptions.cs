namespace Shredder.Core.Configuration;

public sealed class ShredderFreeSpaceOptions
{
    /// <summary>填充用的块大小(字节)。默认 64 MB,平衡内存与系统响应。</summary>
    public int BlockSizeBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>剩余多少字节时停止写入,避免触发磁盘满告警/影响系统稳定。</summary>
    public long MinimumFreeBytesBuffer { get; set; } = 256L * 1024 * 1024;

    /// <summary>是否启用清理小文件 slack(创建大量小文件填充 MFT)。</summary>
    public bool ScrubMftSlack { get; set; } = true;

    /// <summary>是否在 SSD 上拒绝执行空闲空间擦除(SSD 上几乎无意义且伤寿命)。</summary>
    public bool DisableOnSsd { get; set; } = true;

    /// <summary>SSD 拒绝时是否改用 <c>defrag /L</c>(TRIM 重发)。</summary>
    public bool FallbackToTrimOnSsd { get; set; } = true;
}
