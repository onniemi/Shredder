using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shredder.Core.Configuration;

namespace Shredder.Core.Diagnostics;

/// <summary>
/// 默认诊断包导出器:写出
/// <c>yyyy-MM-dd_HHmmss_{reportId}.zip</c>,zip 内含 <c>diagnostics.json</c> 与 <c>diagnostics.html</c>。
/// 写入失败返回 null 而不抛异常,以便 UI 处可安全 fire-and-forget。
/// </summary>
public sealed class DiagnosticsExporter : IDiagnosticsExporter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private readonly ILogger<DiagnosticsExporter> _logger;

    public DiagnosticsExporter(ILogger<DiagnosticsExporter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "诊断包写盘失败不应阻塞 UI;记录日志并向调用方返回 null 即可。")]
    public async Task<string?> ExportAsync(
        DiagnosticsInfo info,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        try
        {
            var dir = ResolveDirectory(outputDirectory);
            Directory.CreateDirectory(dir);

            var baseName = $"{info.CollectedAt.LocalDateTime:yyyy-MM-dd_HHmmss}_{info.ReportId}";
            var zipPath = Path.Combine(dir, baseName + ".zip");

            var json = JsonSerializer.Serialize(info, s_jsonOptions);
            var html = RenderHtml(info);

            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(zip, "diagnostics.json", json, cancellationToken);
                await WriteEntryAsync(zip, "diagnostics.html", html, cancellationToken);
            }

            _logger.LogInformation("Diagnostics bundle written: {Path}", zipPath);
            return zipPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export diagnostics failed.");
            return null;
        }
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, ct);
    }

    private static string ResolveDirectory(string? raw)
    {
        return ShredderAppPaths.ResolveDirectory(raw, Path.Combine("data", "diagnostics"));
    }

    private static string RenderHtml(DiagnosticsInfo d)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Shredder 诊断包 · {WebUtility.HtmlEncode(d.ReportId)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("  :root { color-scheme: light dark; }");
        sb.AppendLine("  body { font-family: 'Segoe UI', system-ui, sans-serif; margin: 24px; max-width: 1200px; }");
        sb.AppendLine("  h1 { font-weight: 600; margin: 0 0 8px; }");
        sb.AppendLine("  h2 { font-weight: 600; margin: 24px 0 8px; font-size: 16px; }");
        sb.AppendLine("  .meta { color: #666; font-size: 13px; margin-bottom: 16px; }");
        sb.AppendLine("  table { width: 100%; border-collapse: collapse; font-size: 13px; margin-bottom: 12px; }");
        sb.AppendLine("  th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid rgba(127,127,127,0.25); vertical-align: top; }");
        sb.AppendLine("  th { background: rgba(127,127,127,0.10); font-weight: 600; }");
        sb.AppendLine("  td.k { width: 240px; color: #555; }");
        sb.AppendLine("  .ok { color: #1f7a3a; font-weight: 600; }");
        sb.AppendLine("  .warn { color: #b3261e; font-weight: 600; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>Shredder 诊断包</h1>");
        sb.Append("<div class=\"meta\">报告 ID: ").Append(WebUtility.HtmlEncode(d.ReportId))
          .Append(" · 采集时间: ").Append(d.CollectedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
          .AppendLine("</div>");

        sb.AppendLine("<h2>环境</h2><table>");
        Row(sb, "应用", $"{d.AppName} {d.AppVersion} ({d.BuildConfiguration})");
        Row(sb, "运行时", d.RuntimeVersion);
        Row(sb, "操作系统", d.OsVersion);
        Row(sb, "机器名", d.MachineName);
        Row(sb, "用户名", d.UserName);
        Row(sb, "管理员权限", d.IsElevated ? "是" : "否");
        Row(sb, "进程架构", d.ProcessArchitecture);
        Row(sb, "OS 架构", d.OsArchitecture);
        Row(sb, "CPU 核数", d.ProcessorCount.ToString());
        Row(sb, "工作集", FormatBytes(d.WorkingSetBytes));
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>卷</h2><table>");
        sb.AppendLine("<thead><tr><th>名称</th><th>类型</th><th>格式</th><th>介质</th><th>TRIM</th><th>总容量</th><th>可用</th><th>就绪</th></tr></thead><tbody>");
        foreach (var v in d.Drives)
        {
            sb.Append("<tr>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(v.Name)).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(v.Type)).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(v.Format)).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(v.Profile)).Append("</td>");
            sb.Append("<td>").Append(v.TrimEnabled ? "<span class=\"ok\">on</span>" : "off").Append("</td>");
            sb.Append("<td>").Append(v.IsReady ? FormatBytes(v.TotalSizeBytes) : "—").Append("</td>");
            sb.Append("<td>").Append(v.IsReady ? FormatBytes(v.AvailableFreeSpaceBytes) : "—").Append("</td>");
            sb.Append("<td>").Append(v.IsReady ? "是" : "<span class=\"warn\">否</span>").Append("</td>");
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table>");

        sb.AppendLine("<h2>已注册算法</h2><table>");
        sb.AppendLine("<thead><tr><th>Id</th><th>名称</th><th>Pass 数</th></tr></thead><tbody>");
        foreach (var a in d.Algorithms)
        {
            sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(a.Id)).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(a.Name)).Append("</td>");
            sb.Append("<td>").Append(a.PassCount).AppendLine("</td></tr>");
        }
        sb.AppendLine("</tbody></table>");

        sb.AppendLine("<h2>配置(脱敏)</h2><table>");
        var o = d.Options;
        Row(sb, "Io.BufferSizeBytes", o.IoBufferSizeBytes.ToString());
        Row(sb, "Io.MaxConcurrentFiles", o.IoMaxConcurrentFiles.ToString());
        Row(sb, "Io.ProgressReportIntervalMs", o.IoProgressReportIntervalMs.ToString());
        Row(sb, "Algorithm.Default", o.AlgorithmDefault);
        Row(sb, "FreeSpace.BlockSizeBytes", o.FreeSpaceBlockSizeBytes.ToString());
        Row(sb, "Reporting.Enabled", o.ReportingEnabled.ToString());
        Row(sb, "Reporting.FormatJson", o.ReportingFormatJson.ToString());
        Row(sb, "Reporting.FormatHtml", o.ReportingFormatHtml.ToString());
        Row(sb, "Reporting.OutputDirectory", o.ReportingOutputDirectory);
        Row(sb, "Ui.ConfirmationKeyword", o.UiConfirmationKeyword);
        Row(sb, "Safety.MftResidentInflateThresholdBytes", o.SafetyMftResidentInflateThresholdBytes.ToString());
        Row(sb, "Safety.MftResidentInflateTargetBytes", o.SafetyMftResidentInflateTargetBytes.ToString());
        Row(sb, "Logging.RecordRawPaths", o.LoggingRecordRawPaths ? "是 (会写原始路径)" : "否 (路径已脱敏为 [hash:xxxxxxxx])");
        Row(sb, "Logging.FileSinkEnabled", o.LoggingFileSinkEnabled.ToString());
        Row(sb, "Logging.OutputDirectory", o.LoggingOutputDirectory);
        Row(sb, "Logging.FileSizeLimitBytes", FormatBytes(o.LoggingFileSizeLimitBytes));
        Row(sb, "Logging.RetainedFileCountLimit", o.LoggingRetainedFileCountLimit.ToString());
        sb.AppendLine("</table>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string k, string? v)
    {
        sb.Append("<tr><td class=\"k\">").Append(WebUtility.HtmlEncode(k)).Append("</td>");
        sb.Append("<td>").Append(WebUtility.HtmlEncode(v ?? string.Empty)).AppendLine("</td></tr>");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.##} {units[i]}";
    }
}
