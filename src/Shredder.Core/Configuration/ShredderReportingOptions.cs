namespace Shredder.Core.Configuration;

/// <summary>
/// 审计报告输出选项。一次批量粉碎结束后自动产出 JSON + HTML 报告。
/// </summary>
public sealed class ShredderReportingOptions
{
    /// <summary>是否启用自动报告输出。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 报告输出目录，支持 <c>%LOCALAPPDATA%</c> 等环境变量。
    /// 留空时默认 <c>%LOCALAPPDATA%\Shredder\Reports</c>。
    /// </summary>
    public string OutputDirectory { get; set; } = "%LOCALAPPDATA%\\Shredder\\Reports";

    /// <summary>是否输出机器可读的 JSON 报告（用于二次处理 / 留存）。</summary>
    public bool FormatJson { get; set; } = true;

    /// <summary>是否输出人类可读的 HTML 报告（用于打印 / 归档）。</summary>
    public bool FormatHtml { get; set; } = true;

    /// <summary>是否在生成完成后自动用关联程序打开 HTML 报告。</summary>
    public bool AutoOpen { get; set; }
}
