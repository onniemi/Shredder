namespace Shredder.Core.Algorithms;

/// <summary>算法仓库:按 Id 查找,枚举全部。</summary>
public interface IShredAlgorithmRegistry
{
    IReadOnlyList<IShredAlgorithm> All { get; }

    /// <summary>按 Id 解析。未找到返回 null。</summary>
    IShredAlgorithm? Find(string id);

    /// <summary>按 Id 解析,未找到抛 <see cref="KeyNotFoundException"/>。</summary>
    IShredAlgorithm Require(string id);
}
