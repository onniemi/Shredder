using BenchmarkDotNet.Attributes;
using Shredder.Core.Algorithms;
using Shredder.Core.Models;
using Shredder.Core.Services;

namespace Shredder.Benchmarks;

/// <summary>
/// 多小文件目录粉碎并发对比:1000 个 4KB 文件,串行(MaxConcurrentFiles=1)vs 并行(=4)。
/// <para>
/// 选用 <see cref="ZeroFillRenameAlgorithm"/> 而非 Crypto/Random 是为了让 I/O 调度而非
/// CPU 算法成本主导耗时,这样并发收益(或负收益,例如机械盘随机寻道)能更直观地反映出来。
/// </para>
/// 通过 <see cref="ShredService.CreateForTests(System.Collections.Generic.IEnumerable{IShredAlgorithm}, string, string?, Shredder.Core.FileSystem.SsdDetector?, int)"/>
/// 直接构造服务,跳过 DI/宿主配置成本。每次迭代重建整棵目录,因为 <c>ShredService</c>
/// 在成功路径上会把目录删除。
/// </summary>
[MemoryDiagnoser]
public class SmallFilesDirectoryBenchmark
{
    /// <summary>单文件大小(字节),默认 4 KB。</summary>
    public const int FileSize = 4 * 1024;

    /// <summary>文件数量,默认 1000。</summary>
    public const int FileCount = 1000;

    private string _rootDir = string.Empty;
    private string _currentDir = string.Empty;
    private byte[] _payload = Array.Empty<byte>();
    private IShredAlgorithm _zeroFill = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "shredder-bench-smallfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);

        // 用固定字节填充避免每次 benchmark 重新生成随机内容引入额外噪声
        _payload = new byte[FileSize];
        for (int i = 0; i < _payload.Length; i++) _payload[i] = (byte)(i & 0xFF);

        _zeroFill = new ZeroFillRenameAlgorithm();
    }

    /// <summary>每次迭代重建目录树:ShredService 成功后会删除原目录。</summary>
    [IterationSetup]
    public void IterationSetup()
    {
        _currentDir = Path.Combine(_rootDir, "tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_currentDir);
        for (int i = 0; i < FileCount; i++)
        {
            var path = Path.Combine(_currentDir, $"f{i:D4}.bin");
            File.WriteAllBytes(path, _payload);
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // ShredService 成功路径会清理目录,这里只兜底失败场景
        if (Directory.Exists(_currentDir))
        {
            try { Directory.Delete(_currentDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Benchmark(Baseline = true, Description = "Serial / MaxConcurrentFiles=1")]
    public async Task Serial() => await RunAsync(maxConcurrentFiles: 1);

    [Benchmark(Description = "Parallel4 / MaxConcurrentFiles=4")]
    public async Task Parallel4() => await RunAsync(maxConcurrentFiles: 4);

    private async Task RunAsync(int maxConcurrentFiles)
    {
        var svc = ShredService.CreateForTests(
            new[] { _zeroFill },
            defaultId: _zeroFill.Id,
            ssdDefault: null,
            ssdDetector: null,
            maxConcurrentFiles: maxConcurrentFiles);

        var job = new ShredJob { Path = _currentDir, IsDirectory = true };
        await svc.ShredAsync(job, progress: null, ct: default);
    }
}
