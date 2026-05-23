namespace Shredder.Core.Configuration;

/// <summary>
/// 日志记录的隐私与位置配置。文件粉碎操作不可逆,排错日志也可能泄露用户文件路径,
/// 因此默认对路径字段进行 SHA-256 短哈希脱敏。仅在主动排错时才应短期开启 <see cref="RecordRawPaths"/>。
/// </summary>
public sealed class ShredderLoggingOptions
{
    /// <summary>
    /// 是否在日志中记录原始路径。默认 <c>false</c>:Path / FilePath / TargetPath / SourcePath /
    /// Source / Dir / Directory / Folder 等属性会被替换为 <c>[hash:xxxxxxxx]</c>(保留扩展名)。
    /// 仅排错时临时开启,不应长期开启。
    /// </summary>
    public bool RecordRawPaths { get; set; }

    /// <summary>
    /// 日志输出目录,支持 <c>%LOCALAPPDATA%</c> 等环境变量。
    /// 留空时默认 <c>程序目录\data\logs</c>。
    /// </summary>
    public string OutputDirectory { get; set; } = "data\\logs";

    /// <summary>单个日志文件大小上限(字节),超过后滚动到新文件。默认 10 MiB。</summary>
    public long FileSizeLimitBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>保留的滚动文件数量上限,超过会被自动清理。默认 14 个。</summary>
    public int RetainedFileCountLimit { get; set; } = 14;

    /// <summary>是否启用文件 Sink(写到磁盘)。默认 <c>true</c>。</summary>
    public bool FileSinkEnabled { get; set; } = true;
}
