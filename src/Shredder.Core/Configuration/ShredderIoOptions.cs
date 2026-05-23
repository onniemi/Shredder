namespace Shredder.Core.Configuration;

/// <summary>I/O 层调参:缓冲区、刷新频率、并发上限。</summary>
public sealed class ShredderIoOptions
{
    /// <summary>每 pass 使用的缓冲区大小(字节)。默认 4 MB。</summary>
    public int BufferSizeBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// 是否使用 <c>FILE_FLAG_NO_BUFFERING</c> 直写(绕过 OS 缓存,要求扇区对齐)。
    /// <para>
    /// 配置项保留以维持向后兼容,**当前版本暂未实装**。原因:Windows 无缓冲 I/O 需要按物理扇区
    /// 对齐缓冲区地址与写入长度,出错代价高。该选项会在后续版本完整接入。
    /// </para>
    /// </summary>
    public bool UseUnbufferedIo { get; set; } = true;

    /// <summary>每 N 个缓冲块强制 Flush 到磁盘。0(默认) = 仅每 pass 末 Flush。</summary>
    public int FlushEveryNBuffers { get; set; }

    /// <summary>
    /// 同时粉碎的文件数上限。<c>1</c>(默认) = 串行,保守。
    /// <para>
    /// 仅对**目录粉碎**生效(<c>ShredService.ShredDirectoryAsync</c>);单文件粉碎不并发。
    /// 经验值:HDD 上 <c>1–2</c>,寻道竞争会让更高并发反而变慢;SSD 上 <c>2–4</c> 可提高吞吐,
    /// 但要避免与 GUI 渲染抢 I/O。最低 <c>1</c>,<c>&lt;= 0</c> 会被 Options 验证拒绝。
    /// </para>
    /// </summary>
    public int MaxConcurrentFiles { get; set; } = 1;

    /// <summary>进度上报间隔(毫秒)。避免每个 chunk 上报导致 UI 抖动。</summary>
    public int ProgressReportIntervalMs { get; set; } = 200;
}
