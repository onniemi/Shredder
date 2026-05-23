using Shredder.Core.Models;

namespace Shredder.Core.Reporting;

/// <summary>
/// 单个 ShredJob 的执行记录。序列化用，所有字段使用 init 以保证记录不可变。
/// </summary>
public sealed class ShredAuditEntry
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }

    public string? AlgorithmId { get; init; }
    public string? AlgorithmName { get; init; }
    public int PassCount { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Elapsed => CompletedAt - StartedAt;

    public ShredJobStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
}
