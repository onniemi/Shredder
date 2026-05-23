namespace Shredder.Core.Configuration;

/// <summary>
/// 全局配置根。来自 appsettings.json 的 <c>Shredder</c> 节,可被环境变量覆盖。
/// </summary>
public sealed class ShredderOptions
{
    public const string SectionName = "Shredder";

    public ShredderIoOptions Io { get; set; } = new();
    public ShredderSafetyOptions Safety { get; set; } = new();
    public ShredderRecycleBinOptions RecycleBin { get; set; } = new();
    public ShredderFreeSpaceOptions FreeSpace { get; set; } = new();
    public ShredderAlgorithmOptions Algorithm { get; set; } = new();
    public ShredderUiOptions Ui { get; set; } = new();
    public ShredderReportingOptions Reporting { get; set; } = new();
    public ShredderLoggingOptions Logging { get; set; } = new();
}
