using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Models;

namespace Shredder.Core.Reporting;

/// <summary>
/// 默认报告写入器。把 JSON 和 HTML 写到配置目录，文件名形如
/// <c>2025-11-08_153045_a4f0...json|html</c>。
/// </summary>
public sealed class ShredReportWriter : IShredReportWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private readonly IOptionsMonitor<ShredderOptions> _options;
    private readonly ILogger<ShredReportWriter> _logger;

    public ShredReportWriter(
        IOptionsMonitor<ShredderOptions> options,
        ILogger<ShredReportWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "报告写盘失败不应反向中断业务流程，记录日志即可。")]
    public async Task<string?> WriteAsync(ShredReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var cfg = _options.CurrentValue.Reporting;
        if (!cfg.Enabled) return null;
        if (report.Entries.Count == 0) return null;
        if (!cfg.FormatJson && !cfg.FormatHtml) return null;

        try
        {
            var dir = ResolveDirectory(cfg.OutputDirectory);
            Directory.CreateDirectory(dir);

            var baseName = $"{report.StartedAt.LocalDateTime:yyyy-MM-dd_HHmmss}_{report.ReportId}";
            string? primaryPath = null;

            if (cfg.FormatJson)
            {
                var jsonPath = Path.Combine(dir, baseName + ".json");
                var json = JsonSerializer.Serialize(report, s_jsonOptions);
                await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8, cancellationToken);
                primaryPath = jsonPath;
            }

            if (cfg.FormatHtml)
            {
                var htmlPath = Path.Combine(dir, baseName + ".html");
                var html = RenderHtml(report);
                await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
                primaryPath = htmlPath; // HTML 优先作为返回值
            }

            if (cfg.AutoOpen && primaryPath is not null)
            {
                TryOpen(primaryPath);
            }

            _logger.LogInformation("Audit report written: {Path} ({Count} entries)", primaryPath, report.Entries.Count);
            return primaryPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write audit report failed.");
            return null;
        }
    }

    private static string ResolveDirectory(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(raw) ? "%LOCALAPPDATA%\\Shredder\\Reports" : raw);
        return Path.GetFullPath(expanded);
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "AutoOpen 失败不应抛出，写日志后忽略。")]
    private void TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open report failed: {Path}", path);
        }
    }

    private static string RenderHtml(ShredReport r)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Shredder 审计报告 · {WebUtility.HtmlEncode(r.ReportId)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("  :root { color-scheme: light dark; }");
        sb.AppendLine("  body { font-family: 'Segoe UI', system-ui, sans-serif; margin: 24px; max-width: 1200px; }");
        sb.AppendLine("  h1 { font-weight: 600; margin: 0 0 8px; }");
        sb.AppendLine("  .meta { color: #666; font-size: 13px; margin-bottom: 16px; }");
        sb.AppendLine("  .summary { display: flex; flex-wrap: wrap; gap: 8px 24px; margin: 12px 0 24px; }");
        sb.AppendLine("  .summary div { background: rgba(127,127,127,0.08); padding: 8px 12px; border-radius: 6px; font-size: 13px; }");
        sb.AppendLine("  table { width: 100%; border-collapse: collapse; font-size: 13px; }");
        sb.AppendLine("  th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid rgba(127,127,127,0.25); vertical-align: top; }");
        sb.AppendLine("  th { background: rgba(127,127,127,0.10); font-weight: 600; }");
        sb.AppendLine("  td.path { font-family: ui-monospace, Consolas, monospace; word-break: break-all; }");
        sb.AppendLine("  .ok { color: #1f7a3a; font-weight: 600; }");
        sb.AppendLine("  .fail { color: #b3261e; font-weight: 600; }");
        sb.AppendLine("  .cancel { color: #8a6d00; font-weight: 600; }");
        sb.AppendLine("  .err { color: #b3261e; font-size: 12px; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>Shredder 审计报告</h1>");
        sb.AppendLine("<div class=\"meta\">");
        sb.Append("报告 ID: ").Append(WebUtility.HtmlEncode(r.ReportId)).Append(" · ");
        sb.Append("机器: ").Append(WebUtility.HtmlEncode(r.MachineName)).Append(" · ");
        sb.Append("用户: ").Append(WebUtility.HtmlEncode(r.UserName)).Append(" · ");
        sb.Append("版本: ").Append(WebUtility.HtmlEncode(r.AppVersion)).Append(" · ");
        sb.Append("开始: ").Append(r.StartedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · ");
        sb.Append("结束: ").Append(r.CompletedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"summary\">");
        sb.Append("<div>总数 <b>").Append(r.TotalCount).AppendLine("</b></div>");
        sb.Append("<div>成功 <b class=\"ok\">").Append(r.SuccessCount).AppendLine("</b></div>");
        sb.Append("<div>失败 <b class=\"fail\">").Append(r.FailedCount).AppendLine("</b></div>");
        sb.Append("<div>取消 <b class=\"cancel\">").Append(r.CancelledCount).AppendLine("</b></div>");
        sb.Append("<div>总字节 <b>").Append(FormatBytes(r.TotalBytes)).AppendLine("</b></div>");
        sb.Append("<div>总耗时 <b>").Append(FormatElapsed(r.TotalElapsed)).AppendLine("</b></div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<table>");
        sb.AppendLine("<thead><tr>");
        sb.AppendLine("<th>#</th><th>路径</th><th>类型</th><th>大小</th><th>算法</th><th>Pass</th><th>开始</th><th>耗时</th><th>状态</th>");
        sb.AppendLine("</tr></thead><tbody>");

        int i = 0;
        foreach (var e in r.Entries)
        {
            i++;
            sb.Append("<tr>");
            sb.Append("<td>").Append(i).Append("</td>");
            sb.Append("<td class=\"path\">").Append(WebUtility.HtmlEncode(e.Path)).Append("</td>");
            sb.Append("<td>").Append(e.IsDirectory ? "目录" : "文件").Append("</td>");
            sb.Append("<td>").Append(e.IsDirectory ? "—" : FormatBytes(e.SizeBytes)).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(e.AlgorithmName ?? e.AlgorithmId ?? "—")).Append("</td>");
            sb.Append("<td>").Append(e.PassCount).Append("</td>");
            sb.Append("<td>").Append(e.StartedAt.LocalDateTime.ToString("HH:mm:ss")).Append("</td>");
            sb.Append("<td>").Append(FormatElapsed(e.Elapsed)).Append("</td>");
            sb.Append("<td class=\"").Append(StatusClass(e.Status)).Append("\">").Append(StatusText(e.Status));
            if (!string.IsNullOrEmpty(e.ErrorMessage))
            {
                sb.Append("<br><span class=\"err\">").Append(WebUtility.HtmlEncode(e.ErrorMessage)).Append("</span>");
            }
            sb.AppendLine("</td></tr>");
        }
        sb.AppendLine("</tbody></table>");

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string StatusClass(ShredJobStatus s) => s switch
    {
        ShredJobStatus.Success => "ok",
        ShredJobStatus.Failed => "fail",
        ShredJobStatus.Cancelled => "cancel",
        _ => "",
    };

    private static string StatusText(ShredJobStatus s) => s switch
    {
        ShredJobStatus.Pending => "待执行",
        ShredJobStatus.Running => "执行中",
        ShredJobStatus.Success => "成功",
        ShredJobStatus.Failed => "失败",
        ShredJobStatus.Cancelled => "取消",
        _ => s.ToString(),
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return $"{v:0.##} {units[i]}";
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return $"{t.TotalMilliseconds:0} ms";
        if (t.TotalMinutes < 1) return $"{t.TotalSeconds:0.##} s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
    }
}
