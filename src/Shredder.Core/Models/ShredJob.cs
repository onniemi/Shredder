namespace Shredder.Core.Models;

public enum ShredJobStatus { Pending, Running, Success, Failed, Cancelled }

public sealed class ShredJob
{
    public required string Path { get; init; }
    public long SizeBytes { get; init; }
    public bool IsDirectory { get; init; }

    /// <summary>
    /// 该任务使用的算法 Id（见 <see cref="Algorithms.ShredAlgorithmIds"/>）。
    /// 为空时由 ShredService 退化到 <c>ShredderOptions.Algorithm.Default</c>，
    /// 检测到 SSD 时进一步退化到 <c>SsdDefault</c>。
    /// </summary>
    public string? AlgorithmId { get; init; }

    public ShredJobStatus Status { get; set; } = ShredJobStatus.Pending;
    public string? ErrorMessage { get; set; }
}
