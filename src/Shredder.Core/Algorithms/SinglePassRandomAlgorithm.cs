using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;

namespace Shredder.Core.Algorithms;

/// <summary>单次密码学随机覆写，速度最快。</summary>
public sealed class SinglePassRandomAlgorithm : ShredAlgorithmBase
{
    public SinglePassRandomAlgorithm(IOptions<ShredderOptions>? options = null) : base(options) { }

    public override string Id => ShredAlgorithmIds.Clear;
    public override string Name => "单次随机覆写";
    public override int PassCount => 1;

    protected override void FillBuffer(int passIndex, byte[] buffer, int count)
        => FillRandom(buffer, count);
}
