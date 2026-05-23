namespace Shredder.Core.Algorithms;

/// <inheritdoc cref="IShredAlgorithmRegistry"/>
public sealed class ShredAlgorithmRegistry : IShredAlgorithmRegistry
{
    private readonly Dictionary<string, IShredAlgorithm> _byId;

    public ShredAlgorithmRegistry(IEnumerable<IShredAlgorithm> algorithms)
    {
        ArgumentNullException.ThrowIfNull(algorithms);
        _byId = algorithms.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        All = _byId.Values.OrderBy(a => a.PassCount).ThenBy(a => a.Id).ToArray();
    }

    public IReadOnlyList<IShredAlgorithm> All { get; }

    public IShredAlgorithm? Find(string id) =>
        string.IsNullOrEmpty(id) ? null : _byId.GetValueOrDefault(id);

    public IShredAlgorithm Require(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"未注册的粉碎算法: {id}");
}
