using BenchmarkDotNet.Attributes;
using Shredder.Core.Algorithms;

namespace Shredder.Benchmarks;

/// <summary>
/// 单文件覆写算法对比:
/// <list type="bullet">
///   <item><b>Clear</b>(SinglePassRandom):一次密码学随机覆写。</item>
///   <item><b>ZeroFill</b>:一次 0x00 覆写。</item>
///   <item><b>CryptoErase</b>:一次 AES-CTR 密钥流覆写。</item>
/// </list>
/// 测量直接调用 <see cref="IShredAlgorithm.ShredAsync"/> 在已打开 <see cref="FileStream"/>
/// 上的耗时,隔离掉 <c>ShredService</c> 的路径校验/属性备份/ADS 处理等编排开销,
/// 让结果只反映"算法本体 + 物理 I/O"。文件大小默认 64 MB,可在开发机几分钟内跑完。
/// </summary>
[MemoryDiagnoser]
public class SingleFileOverwriteBenchmark
{
    /// <summary>测试文件大小(字节),默认 64 MB。</summary>
    public const long FileSize = 64L * 1024 * 1024;

    private string _tempDir = string.Empty;
    private string _filePath = string.Empty;
    private IShredAlgorithm _clearAlgorithm = null!;
    private IShredAlgorithm _zeroFillAlgorithm = null!;
    private IShredAlgorithm _cryptoEraseAlgorithm = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "shredder-bench-singlefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "target.bin");

        _clearAlgorithm = new SinglePassRandomAlgorithm();
        _zeroFillAlgorithm = new ZeroFillRenameAlgorithm();
        _cryptoEraseAlgorithm = new CryptoEraseAlgorithm();
    }

    /// <summary>每次迭代重新生成目标文件,避免上一次 benchmark 把文件写废。</summary>
    [IterationSetup]
    public void IterationSetup()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);

        // 用 SetLength 创建稀疏文件,极快;真正的物理写入由 benchmark 内部完成
        using var fs = new FileStream(_filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.SetLength(FileSize);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不掩盖 benchmark 本身的失败
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Benchmark(Baseline = true, Description = "Clear / 单次随机覆写")]
    public async Task Clear() => await ShredAsync(_clearAlgorithm);

    [Benchmark(Description = "ZeroFill / 单次 0x00 覆写")]
    public async Task ZeroFill() => await ShredAsync(_zeroFillAlgorithm);

    [Benchmark(Description = "CryptoErase / AES-CTR 一次覆写")]
    public async Task CryptoErase() => await ShredAsync(_cryptoEraseAlgorithm);

    private async Task ShredAsync(IShredAlgorithm algorithm)
    {
        await using var fs = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await algorithm.ShredAsync(fs, FileSize, _filePath, progress: null, ct: default);
    }
}
