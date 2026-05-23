using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;

namespace Shredder.Core.Algorithms;

/// <summary>零填充一次，配合 ShredService 做多轮文件名随机化。</summary>
public sealed class ZeroFillRenameAlgorithm : ShredAlgorithmBase
{
    public ZeroFillRenameAlgorithm(IOptions<ShredderOptions>? options = null) : base(options) { }

    public override string Id => ShredAlgorithmIds.ZeroFill;
    public override string Name => "零填充 + 文件名随机化";
    public override int PassCount => 1;

    protected override void FillBuffer(int passIndex, byte[] buffer, int count)
        => FillConstant(buffer, count, 0x00);
}
