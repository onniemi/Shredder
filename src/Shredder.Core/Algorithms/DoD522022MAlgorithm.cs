using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;

namespace Shredder.Core.Algorithms;

/// <summary>DoD 5220.22-M 标准实现，支持 3 次与 7 次两种变体。</summary>
public sealed class DoD522022MAlgorithm : ShredAlgorithmBase
{
    private readonly int _passes;

    public DoD522022MAlgorithm(int passes = 3, IOptions<ShredderOptions>? options = null) : base(options)
    {
        if (passes != 3 && passes != 7)
            throw new ArgumentException("DoD 仅支持 3 或 7 次", nameof(passes));
        _passes = passes;
    }

    public override string Id => _passes == 3 ? ShredAlgorithmIds.Purge3Pass : ShredAlgorithmIds.Purge7Pass;
    public override string Name => $"DoD 5220.22-M ({_passes} Pass)";
    public override int PassCount => _passes;

    protected override void FillBuffer(int passIndex, byte[] buffer, int count)
    {
        // 3 次：0x00 → 0xFF → 随机
        // 7 次：随机 → 取反 → 随机 → 0x00 → 0xFF → 随机 → 随机
        if (_passes == 3)
        {
            switch (passIndex)
            {
                case 0: FillConstant(buffer, count, 0x00); break;
                case 1: FillConstant(buffer, count, 0xFF); break;
                default: FillRandom(buffer, count); break;
            }
        }
        else
        {
            switch (passIndex)
            {
                case 0: FillRandom(buffer, count); break;
                case 1:
                    FillRandom(buffer, count);
                    for (int i = 0; i < count; i++) buffer[i] = (byte)~buffer[i];
                    break;
                case 2: FillRandom(buffer, count); break;
                case 3: FillConstant(buffer, count, 0x00); break;
                case 4: FillConstant(buffer, count, 0xFF); break;
                default: FillRandom(buffer, count); break;
            }
        }
    }
}
