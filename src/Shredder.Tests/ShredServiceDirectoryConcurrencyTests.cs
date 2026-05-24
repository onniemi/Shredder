using Shredder.Core.Algorithms;
using Shredder.Core.FileSystem;
using Shredder.Core.Models;
using Shredder.Core.Services;
using Xunit;

namespace Shredder.Tests;

/// <summary>
/// 覆盖 <see cref="ShredService"/> 目录粉碎的并发分支:
/// MaxConcurrentFiles=1 保持串行,&gt;1 走 <see cref="Parallel.ForEachAsync"/>,
/// 并发期间任一文件抛取消应让剩余文件也被取消。
/// </summary>
public class ShredServiceDirectoryConcurrencyTests
{
    [Fact]
    public async Task MaxConcurrentFiles_One_KeepsSerialBehaviour()
    {
        // 序号:并发==1 时不应该走 Parallel 分支,所有文件都会被处理且峰值并发恒为 1。
        var algo = new ConcurrencyTrackingAlgorithm(blockMs: 30);
        var dir = CreateTempTree(fileCount: 5);
        try
        {
            var svc = ShredService.CreateForTests(
                new IShredAlgorithm[] { algo },
                defaultId: algo.Id,
                ssdDefault: null,
                ssdDetector: null,
                maxConcurrentFiles: 1);

            var job = new ShredJob { Path = dir, IsDirectory = true };
            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.Equal(ShredJobStatus.Success, job.Status);
            Assert.Equal(5, algo.TotalInvocations);
            Assert.Equal(1, algo.PeakConcurrency);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MaxConcurrentFiles_Three_AllowsParallelButRespectsCeiling()
    {
        // 序号:8 个文件 + concurrency=3,应观察到峰值并发 >1 且不超过 3。
        var algo = new ConcurrencyTrackingAlgorithm(blockMs: 80);
        var dir = CreateTempTree(fileCount: 8);
        try
        {
            var svc = ShredService.CreateForTests(
                new IShredAlgorithm[] { algo },
                defaultId: algo.Id,
                ssdDefault: null,
                ssdDetector: null,
                maxConcurrentFiles: 3);

            var job = new ShredJob { Path = dir, IsDirectory = true };
            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.Equal(ShredJobStatus.Success, job.Status);
            Assert.Equal(8, algo.TotalInvocations);
            Assert.InRange(algo.PeakConcurrency, 2, 3);
            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(3, 3)]
    [InlineData(99, 8)]
    public void FastDelete_Concurrency_IsBoostedAndCapped(int configured, int expected)
    {
        var svc = ShredService.CreateForTests(
            new IShredAlgorithm[] { new FastDeleteAlgorithm() },
            defaultId: ShredAlgorithmIds.FastDelete,
            ssdDefault: null,
            ssdDetector: null,
            maxConcurrentFiles: configured);

        Assert.Equal(expected, svc.ResolveFileConcurrency(new FastDeleteAlgorithm()));
    }

    [Fact]
    public void OverwriteAlgorithm_Concurrency_UsesConfiguredValue()
    {
        var algo = new ConcurrencyTrackingAlgorithm(blockMs: 1);
        var svc = ShredService.CreateForTests(
            new IShredAlgorithm[] { algo },
            defaultId: algo.Id,
            ssdDefault: null,
            ssdDetector: null,
            maxConcurrentFiles: 1);

        Assert.Equal(1, svc.ResolveFileConcurrency(algo));
    }

    [Fact]
    public async Task MaxConcurrentFiles_Cancellation_StopsRemainingFiles()
    {
        // 序号:并发模式下取消信号必须及时阻断后续文件,不能等所有文件都处理完。
        var cts = new CancellationTokenSource();
        var algo = new ConcurrencyTrackingAlgorithm(
            blockMs: 200,
            onInvocation: invocations =>
            {
                // 处理过若干文件之后触发取消,验证剩余文件不会被吃光
                if (invocations >= 2) cts.Cancel();
            });
        var dir = CreateTempTree(fileCount: 20);
        try
        {
            var svc = ShredService.CreateForTests(
                new IShredAlgorithm[] { algo },
                defaultId: algo.Id,
                ssdDefault: null,
                ssdDetector: null,
                maxConcurrentFiles: 4);

            var job = new ShredJob { Path = dir, IsDirectory = true };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.ShredAsync(job, null, cts.Token));

            Assert.Equal(ShredJobStatus.Cancelled, job.Status);
            // 取消之后不应该把全部 20 个文件都跑完
            Assert.True(algo.TotalInvocations < 20,
                $"取消信号未能阻断后续文件:实际跑了 {algo.TotalInvocations}/20。");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { /* 取消后可能残留只读句柄,不让清理失败遮蔽测试断言 */ }
            }
        }
    }

    private static string CreateTempTree(int fileCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "shredder-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (int i = 0; i < fileCount; i++)
        {
            File.WriteAllBytes(
                Path.Combine(root, $"f{i:D2}.bin"),
                new byte[] { (byte)i, 0x55, 0xAA, (byte)~i });
        }
        return root;
    }

    /// <summary>
    /// 算法骨架:每次进入 <see cref="FillBuffer"/> 时把并发计数 +1,延时模拟磁盘 I/O,
    /// 离开时 -1,过程中记录峰值。 用作"是否真的并发"的观测端。
    /// </summary>
    private sealed class ConcurrencyTrackingAlgorithm : ShredAlgorithmBase
    {
        private readonly int _blockMs;
        private readonly Action<int>? _onInvocation;
        private int _currentConcurrent;
        private int _peak;
        private int _invocations;
        private readonly object _gate = new();

        public ConcurrencyTrackingAlgorithm(int blockMs, Action<int>? onInvocation = null)
        {
            _blockMs = blockMs;
            _onInvocation = onInvocation;
        }

        public override string Id => "concurrency-tracker";
        public override string Name => "ConcurrencyTracker";
        public override int PassCount => 1;
        public int TotalInvocations => Volatile.Read(ref _invocations);
        public int PeakConcurrency => Volatile.Read(ref _peak);

        protected override void FillBuffer(int passIndex, byte[] buffer, int count)
        {
            int cur = Interlocked.Increment(ref _currentConcurrent);
            int invocationIndex = Interlocked.Increment(ref _invocations);
            lock (_gate)
            {
                if (cur > _peak) _peak = cur;
            }
            try
            {
                _onInvocation?.Invoke(invocationIndex);
                // 模拟磁盘耗时,确保多线程可观察到峰值并发
                Thread.Sleep(_blockMs);
                Array.Fill(buffer, (byte)0xCD, 0, count);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }
        }
    }
}
