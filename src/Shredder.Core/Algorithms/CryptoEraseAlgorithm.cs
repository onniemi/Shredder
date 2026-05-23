using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Models;

namespace Shredder.Core.Algorithms;

/// <summary>
/// 加密擦除：用一次性 AES-CTR 密钥流覆写文件内容，随后丢弃密钥。
/// </summary>
/// <remarks>
/// 适用于 SSD/NVMe/U 盘等闪存介质：
/// <list type="bullet">
///   <item>FTL(Flash Translation Layer) 会把同一 LBA 重定向到不同物理块,反复覆写 0x00/0xFF 既无效又伤盘。</item>
///   <item>用强随机字节(密钥流)覆写一次即等效于"密文化",再丢弃密钥后,即使物理块被 wear-leveling 保留也无法解读。</item>
///   <item>之后调用方再发 TRIM(<c>FSCTL_FILE_LEVEL_TRIM</c>)即可让控制器物理回收。</item>
/// </list>
/// 单次 Pass 即可,密钥/IV 全程留在 RAM,方法返回后被 <see cref="CryptographicOperations.ZeroMemory"/> 清零。
/// 通过覆写 <see cref="ShredAsync"/> 在调用期间安装线程本地状态,避免把密钥泄漏给基类的 FillBuffer 钩子接口。
/// </remarks>
public sealed class CryptoEraseAlgorithm : ShredAlgorithmBase
{
    private const int KeySize = 32;        // AES-256
    private const int BlockSize = 16;      // AES block bytes

    // ShredAsync 会跨 await 续体，不能用 ThreadStatic；AsyncLocal 随逻辑调用链流动。
    private static readonly AsyncLocal<AesCtrState?> s_state = new();

    public CryptoEraseAlgorithm(IOptions<ShredderOptions>? options = null) : base(options) { }

    public override string Id => ShredAlgorithmIds.CryptoErase;
    public override string Name => "Crypto Erase (AES-CTR)";
    public override int PassCount => 1;

    public override async Task ShredAsync(
        Stream stream,
        long length,
        string filePath,
        IProgress<ShredProgress>? progress,
        CancellationToken ct)
    {
        using var state = AesCtrState.CreateRandom();
        s_state.Value = state;
        try
        {
            await base.ShredAsync(stream, length, filePath, progress, ct);
        }
        finally
        {
            s_state.Value = null;
        }
    }

    protected override void FillBuffer(int passIndex, byte[] buffer, int count)
    {
        var state = s_state.Value ?? throw new InvalidOperationException("CryptoErase 状态未初始化。");
        state.NextKeystream(buffer.AsSpan(0, count));
    }

    /// <summary>AES-CTR 密钥流发生器：固定 key + 自增 counter,加密 counter 得到 keystream。</summary>
    private sealed class AesCtrState : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptor;
        private readonly byte[] _counter = new byte[BlockSize];
        private readonly byte[] _block = new byte[BlockSize];
        private int _blockOffset = BlockSize; // BlockSize 表示当前 block 已耗尽,需要重新加密

        public static AesCtrState CreateRandom()
        {
            var aes = Aes.Create();
            aes.KeySize = KeySize * 8;
            aes.Mode = CipherMode.ECB;       // 我们手动做 CTR,只借用 ECB 单块加密
            aes.Padding = PaddingMode.None;
            aes.GenerateKey();

            var state = new AesCtrState(aes);
            RandomNumberGenerator.Fill(state._counter);
            return state;
        }

        private AesCtrState(Aes aes)
        {
            _aes = aes;
            _encryptor = aes.CreateEncryptor();
        }

        public void NextKeystream(Span<byte> dest)
        {
            int written = 0;
            while (written < dest.Length)
            {
                if (_blockOffset >= BlockSize)
                {
                    _encryptor.TransformBlock(_counter, 0, BlockSize, _block, 0);
                    IncrementCounter(_counter);
                    _blockOffset = 0;
                }
                int take = Math.Min(BlockSize - _blockOffset, dest.Length - written);
                _block.AsSpan(_blockOffset, take).CopyTo(dest.Slice(written, take));
                _blockOffset += take;
                written += take;
            }
        }

        private static void IncrementCounter(byte[] counter)
        {
            // 大端自增,从最低字节(末位)开始进位
            for (int i = counter.Length - 1; i >= 0; i--)
            {
                if (++counter[i] != 0) return;
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_counter);
            CryptographicOperations.ZeroMemory(_block);
            _encryptor.Dispose();
            _aes.Dispose();
        }
    }
}
