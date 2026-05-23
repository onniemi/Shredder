namespace Shredder.Core.Diagnostics;

/// <summary>
/// 把 <see cref="DiagnosticsInfo"/> 写到磁盘的诊断包(zip 内含 info.json + report.html)。
/// </summary>
public interface IDiagnosticsExporter
{
    /// <summary>
    /// 写诊断包到指定目录(若 <paramref name="outputDirectory"/> 为 null,使用 <c>%LOCALAPPDATA%\Shredder\Diagnostics</c>)。
    /// 返回生成的 zip 文件绝对路径,失败时返回 null(已记日志)。
    /// </summary>
    Task<string?> ExportAsync(
        DiagnosticsInfo info,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default);
}
