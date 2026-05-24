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
        // 序号:8 个文件 + concurrency=3,应稳定观察到 3 个文件同时进入算法,且不超过 3。
        var algo = new ConcurrencyTrackingAlgorithm(blockMs: 10, releaseWhenConcurrent: 3);
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
            Assert.Equal(3, algo.PeakConcurrency);
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

    private sealed class ConcurrencyTrackingAlgorithm : IShredAlgorithm
    {
        private readonly int _blockMs;
        private readonly int? _releaseWhenConcurrent;
        private readonly Action<int>? _onInvocation;
        private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _currentConcurrent;
        private int _peak;
        private int _invocations;
        private readonly object _gate = new();

        public ConcurrencyTrackingAlgorithm(
            int blockMs,
            int? releaseWhenConcurrent = null,
            Action<int>? onInvocation = null)
        {
            _blockMs = blockMs;
            _releaseWhenConcurrent = releaseWhenConcurrent;
            _onInvocation = onInvocation;
        }

        public string Id => "concurrency-tracker";
        public string Name => "ConcurrencyTracker";
        public int PassCount => 1;
        public int TotalInvocations => Volatile.Read(ref _invocations);
        public int PeakConcurrency => Volatile.Read(ref _peak);

        public async Task ShredAsync(
            Stream stream,
            long length,
            string filePath,
            IProgress<ShredProgress>? progress,
            CancellationToken ct)
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
                await WaitForConcurrentEntrantsIfNeededAsync(cur, ct);
                await Task.Delay(_blockMs, ct);

                if (length > 0)
                {
                    stream.Seek(0, SeekOrigin.Begin);
                    await stream.WriteAsync(new byte[] { 0xCD }, ct);
                    progress?.Report(new ShredProgress(filePath, 1, 1, length, length));
                }
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }
        }

        private async Task WaitForConcurrentEntrantsIfNeededAsync(int currentConcurrency, CancellationToken ct)
        {
            if (_releaseWhenConcurrent is null) return;
            if (currentConcurrency >= _releaseWhenConcurrent.Value)
            {
                _releaseGate.TrySetResult();
                return;
            }

            var timeout = Task.Delay(TimeSpan.FromSeconds(5), ct);
            var completed = await Task.WhenAny(_releaseGate.Task, timeout);
            if (completed == timeout)
            {
                throw new TimeoutException(
                    $"等待并发进入超时:当前峰值 {PeakConcurrency},目标 {_releaseWhenConcurrent.Value}。");
            }
        }
    }
}
