using Shredder.Core.Models;

namespace Shredder.Core.Algorithms;

/// <summary>
/// 快速粉碎模式。实际删除由 ShredService 走截断、随机改名和删除路径完成。
/// </summary>
public sealed class FastDeleteAlgorithm : IShredAlgorithm
{
    public string Id => ShredAlgorithmIds.FastDelete;
    public string Name => "快速粉碎";
    public int PassCount => 0;

    public Task ShredAsync(
        Stream stream,
        long length,
        string filePath,
        IProgress<ShredProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ShredProgress(filePath, 0, 0, 0, length));
        return Task.CompletedTask;
    }
}
