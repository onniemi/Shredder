using Shredder.Core.Models;

namespace Shredder.Core.Algorithms;

/// <summary>
/// 文件覆写算法接口。
/// 算法只负责对已打开的写流做覆写,文件的打开、改名、删除由 ShredService 编排。
/// </summary>
public interface IShredAlgorithm
{
    /// <summary>稳定标识,用作 DI/配置/序列化键。见 <see cref="ShredAlgorithmIds"/>。</summary>
    string Id { get; }

    /// <summary>算法显示名(可本地化)。</summary>
    string Name { get; }

    /// <summary>覆写次数(pass)。</summary>
    int PassCount { get; }

    /// <summary>对流执行覆写。length 为目标文件长度(字节)。</summary>
    Task ShredAsync(
        Stream stream,
        long length,
        string filePath,
        IProgress<ShredProgress>? progress,
        CancellationToken ct);
}
