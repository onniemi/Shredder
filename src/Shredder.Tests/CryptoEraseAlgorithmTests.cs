using System.Collections.Concurrent;
using System.Security.Cryptography;
using Shredder.Core.Algorithms;
using Xunit;

namespace Shredder.Tests;

public class CryptoEraseAlgorithmTests
{
    [Fact]
    public void Id_IsStableConstant()
    {
        var algo = new CryptoEraseAlgorithm();
        Assert.Equal(ShredAlgorithmIds.CryptoErase, algo.Id);
        Assert.Equal(1, algo.PassCount);
    }

    [Fact]
    public async Task ShredAsync_RewritesEntireFile_NoOriginalBytesRemain()
    {
        // 写一段全 0xAA 的 32 KiB 数据,粉碎后绝不应再看到 0xAA 主导
        var path = Path.GetTempFileName();
        try
        {
            byte[] original = new byte[32 * 1024];
            Array.Fill(original, (byte)0xAA);
            await File.WriteAllBytesAsync(path, original);

            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var algo = new CryptoEraseAlgorithm();
                await algo.ShredAsync(fs, fs.Length, path, null, CancellationToken.None);
            }

            byte[] after = await File.ReadAllBytesAsync(path);
            Assert.Equal(original.Length, after.Length);
            Assert.NotEqual(Hash(original), Hash(after));

            // 至少应有一半字节不等于 0xAA(粗略检测随机化)
            int differing = 0;
            for (int i = 0; i < after.Length; i++)
                if (after[i] != 0xAA) differing++;
            Assert.True(differing > after.Length / 2,
                $"加密擦除后仍有 {after.Length - differing}/{after.Length} 字节未变");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ShredAsync_TwoRuns_ProduceDifferentOutput()
    {
        // 同一明文连续粉碎两次 → 两次结果不同(密钥每次重新随机)
        var path1 = Path.GetTempFileName();
        var path2 = Path.GetTempFileName();
        try
        {
            byte[] payload = new byte[8 * 1024];
            await File.WriteAllBytesAsync(path1, payload);
            await File.WriteAllBytesAsync(path2, payload);

            var algo = new CryptoEraseAlgorithm();
            await using (var fs1 = new FileStream(path1, FileMode.Open, FileAccess.Write, FileShare.None))
                await algo.ShredAsync(fs1, fs1.Length, path1, null, CancellationToken.None);
            await using (var fs2 = new FileStream(path2, FileMode.Open, FileAccess.Write, FileShare.None))
                await algo.ShredAsync(fs2, fs2.Length, path2, null, CancellationToken.None);

            var h1 = Hash(await File.ReadAllBytesAsync(path1));
            var h2 = Hash(await File.ReadAllBytesAsync(path2));
            Assert.NotEqual(h1, h2);
        }
        finally
        {
            if (File.Exists(path1)) File.Delete(path1);
            if (File.Exists(path2)) File.Delete(path2);
        }
    }

    [Fact]
    public async Task ShredAsync_NonAlignedSize_DoesNotCorruptStream()
    {
        // 非 16 字节对齐(AES block)的长度,确认末尾 partial block 被正确写入
        var path = Path.GetTempFileName();
        try
        {
            byte[] payload = new byte[1 + 16 * 7]; // 113 字节
            await File.WriteAllBytesAsync(path, payload);

            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var algo = new CryptoEraseAlgorithm();
                await algo.ShredAsync(fs, fs.Length, path, null, CancellationToken.None);
            }

            Assert.Equal(113, new FileInfo(path).Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ShredAsync_StateSurvivesContinuationOnDifferentThread()
    {
        using var syncContext = new DedicatedThreadSynchronizationContext();
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(syncContext);
            var stream = new AsyncBoundaryStream(4 * 1024 * 1024 + 17);

            var algo = new CryptoEraseAlgorithm();
            await algo.ShredAsync(stream, stream.Length, "async-boundary.bin", null, CancellationToken.None);

            Assert.Equal(stream.Length, stream.Position);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private sealed class AsyncBoundaryStream : Stream
    {
        private readonly long _length;

        public AsyncBoundaryStream(long length)
        {
            _length = length;
        }

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.Delay(1, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Position += count;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Position += buffer.Length;
            return new ValueTask(Task.Delay(1, cancellationToken));
        }
    }

    private sealed class DedicatedThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private int _threadId;

        public DedicatedThreadSynchronizationContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "CryptoEraseTestContext" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (Environment.CurrentManagedThreadId == _threadId)
            {
                // 当前正运行在专用线程内部的 foreach 上,不能 Join 自己,也不能 Dispose 队列
                // (否则下一次 MoveNext 会抛 ObjectDisposedException 把测试宿主炸掉)。
                // CompleteAdding 后 foreach 自然退出,线程结束,队列与本对象由 GC 回收。
                return;
            }
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }

        private void Run()
        {
            _threadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
