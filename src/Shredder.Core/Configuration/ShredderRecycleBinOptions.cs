namespace Shredder.Core.Configuration;

public sealed class ShredderRecycleBinOptions
{
    /// <summary>清空时是否同时遍历所有固定盘的 $Recycle.Bin(否则仅当前用户)。</summary>
    public bool ProcessAllDrives { get; set; } = true;

    /// <summary>是否对回收站内文件实际覆写(而非仅调用 SHEmptyRecycleBin)。</summary>
    public bool OverwriteContents { get; set; } = true;

    /// <summary>是否在结束后调用 SHEmptyRecycleBin 静默清理元数据。</summary>
    public bool CallShellEmptyAfterShred { get; set; } = true;
}
