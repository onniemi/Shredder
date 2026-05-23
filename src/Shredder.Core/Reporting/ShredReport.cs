using Shredder.Core.Models;

namespace Shredder.Core.Reporting;

/// <summary>
/// 一次完整运行（一批 Jobs）的审计报告。
/// </summary>
public sealed class ShredReport
{
    public required string ReportId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string AppVersion { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;

    public IReadOnlyList<ShredAuditEntry> Entries { get; init; } = Array.Empty<ShredAuditEntry>();

    // -------- 计算字段 --------
    public int TotalCount => Entries.Count;
    public int SuccessCount => Entries.Count(e => e.Status == ShredJobStatus.Success);
    public int FailedCount => Entries.Count(e => e.Status == ShredJobStatus.Failed);
    public int CancelledCount => Entries.Count(e => e.Status == ShredJobStatus.Cancelled);
    public long TotalBytes => Entries.Sum(e => e.SizeBytes);
    public TimeSpan TotalElapsed => CompletedAt - StartedAt;
}
