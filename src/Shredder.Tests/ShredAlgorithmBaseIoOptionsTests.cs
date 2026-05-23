using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Core.Models;
using Xunit;

namespace Shredder.Tests;

/// <summary>
/// 验证 ShredderIoOptions 的 BufferSizeBytes / FlushEveryNBuffers / ProgressReportIntervalMs
/// 三个配置项在算法层真正生效（参见 docs/ClaudeCode_剩余工作交接.md §4.1）。
/// </summary>
public class ShredAlgorithmBaseIoOptionsTests
{
    [Fact]
    public async Task ShredAsync_UsesConfiguredBufferSize()
    {
        const int bufferSize = 64 * 1024;
        const int fileLength = 1024 * 1024;
        var opts = OptionsWithIo(bufferSize: bufferSize);
        var algo = new SinglePassRandomAlgorithm(opts);

        await using var recording = new RecordingStream(new MemoryStream(new byte[fileLength]));

        await algo.ShredAsync(recording, fileLength, "test", null, CancellationToken.None);

        Assert.NotEmpty(recording.WriteSizes);
        Assert.All(recording.WriteSizes,
            size => Assert.True(size <= bufferSize, $"Chunk size {size} exceeds configured buffer {bufferSize}"));
        // 1 MB / 64 KB = 16 满块写入
        Assert.Equal(fileLength / bufferSize, recording.WriteSizes.Count);
    }

    [Fact]
    public async Task ShredAsync_ForcesFinalProgressAtPassEnd()
    {
        // 大节流间隔 + 小文件:确保只有 "pass 末尾强制上报" 路径会触发
        const int bufferSize = 64 * 1024;
        const int fileLength = 4 * 1024;
        var opts = OptionsWithIo(bufferSize: bufferSize, progressIntervalMs: 999_999);
        var algo = new DoD522022MAlgorithm(passes: 3, opts);

        await using var inner = new MemoryStream(new byte[fileLength]);
        var progress = new CapturingProgress();

        await algo.ShredAsync(inner, fileLength, "test", progress, CancellationToken.None);

        // 每个 pass 末尾至少强制一次最终上报
        Assert.True(progress.Reports.Count >= algo.PassCount,
            $"Expected ≥{algo.PassCount} reports (one per pass forced), got {progress.Reports.Count}");

        // 最后一次上报必须反映最末 pass 的完成
        var last = progress.Reports[^1];
        Assert.Equal(fileLength, last.BytesWritten);
        Assert.Equal(fileLength, last.TotalBytes);
        Assert.Equal(algo.PassCount, last.PassIndex);
    }

    [Fact]
    public async Task ShredAsync_ThrottlesProgressReports()
    {
        // 64 chunks × 写入延迟 → 节流必定生效;上报数应远小于 chunk 数
        const int bufferSize = 64 * 1024;
        const int fileLength = 4 * 1024 * 1024;
        const int chunkCount = fileLength / bufferSize; // 64
        var opts = OptionsWithIo(bufferSize: bufferSize, progressIntervalMs: 100);
        var algo = new SinglePassRandomAlgorithm(opts);

        await using var recording = new RecordingStream(
            new MemoryStream(new byte[fileLength]),
            writeDelayMs: 10);
        var progress = new CapturingProgress();

        await algo.ShredAsync(recording, fileLength, "test", progress, CancellationToken.None);

        // 没有节流时每个 chunk 都会上报,总数 == chunkCount;节流生效则严格小于
        Assert.True(progress.Reports.Count < chunkCount,
            $"Expected throttling: reports {progress.Reports.Count} should be < chunks {chunkCount}");

        // 最终强制上报依然存在
        var last = progress.Reports[^1];
        Assert.Equal(fileLength, last.BytesWritten);
        Assert.Equal(fileLength, last.TotalBytes);
    }

    [Fact]
    public async Task ShredAsync_FlushesEveryNBuffers()
    {
        // 256 KB / 64 KB = 4 个 buffer,FlushEveryNBuffers=2:
        //   buffer 2 (written=128K<256K):  中段 flush ✓
        //   buffer 4 (written=256K==length): 中段 flush 跳过(避免与 pass 末重复)
        //   pass 末尾 FlushAsync                                ✓
        // 期望:FlushAsync 调用 ≥ 2
        const int bufferSize = 64 * 1024;
        const int fileLength = 256 * 1024;
        var opts = OptionsWithIo(bufferSize: bufferSize, flushEveryNBuffers: 2);
        var algo = new SinglePassRandomAlgorithm(opts);

        await using var recording = new RecordingStream(new MemoryStream(new byte[fileLength]));

        await algo.ShredAsync(recording, fileLength, "test", null, CancellationToken.None);

        Assert.True(recording.FlushAsyncCount >= 2,
            $"Expected ≥2 FlushAsync (mid-pass + pass-end), got {recording.FlushAsyncCount}");
    }

    [Fact]
    public async Task ShredAsync_FlushesOnlyAtPassEnd_WhenFlushEveryNBuffersIsZero()
    {
        const int bufferSize = 64 * 1024;
        const int fileLength = 256 * 1024;
        var opts = OptionsWithIo(bufferSize: bufferSize, flushEveryNBuffers: 0);
        var algo = new SinglePassRandomAlgorithm(opts);

        await using var recording = new RecordingStream(new MemoryStream(new byte[fileLength]));

        await algo.ShredAsync(recording, fileLength, "test", null, CancellationToken.None);

        // 单 pass × 单 pass 末尾 flush = 恰好 1
        Assert.Equal(1, recording.FlushAsyncCount);
    }

    [Fact]
    public async Task CryptoEraseAlgorithm_RespectsBufferSizeOption()
    {
        // CryptoEraseAlgorithm 自带 ShredAsync override;验证 IOptions 透传到基类后,
        // 覆写真实文件正常完成且内容确实被改写(override 不破坏配置传递)。
        const int bufferSize = 128 * 1024;
        const int fileLength = 512 * 1024;
        var opts = OptionsWithIo(bufferSize: bufferSize);
        var algo = new CryptoEraseAlgorithm(opts);

        var path = Path.GetTempFileName();
        try
        {
            var originalData = new byte[fileLength];
            RandomNumberGenerator.Fill(originalData);
            File.WriteAllBytes(path, originalData);
            var originalHash = Sha256(originalData);

            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                await algo.ShredAsync(fs, fs.Length, path, null, CancellationToken.None);
            }

            var newHash = Sha256(File.ReadAllBytes(path));
            Assert.NotEqual(originalHash, newHash);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- Helpers ----------

    private static IOptions<ShredderOptions> OptionsWithIo(
        int? bufferSize = null,
        int? flushEveryNBuffers = null,
        int? progressIntervalMs = null)
    {
        var io = new ShredderIoOptions();
        if (bufferSize.HasValue) io.BufferSizeBytes = bufferSize.Value;
        if (flushEveryNBuffers.HasValue) io.FlushEveryNBuffers = flushEveryNBuffers.Value;
        if (progressIntervalMs.HasValue) io.ProgressReportIntervalMs = progressIntervalMs.Value;
        return Options.Create(new ShredderOptions { Io = io });
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>同步实现的 IProgress,避免 Progress&lt;T&gt; 的 SynchronizationContext 异步排队带来的竞态。</summary>
    private sealed class CapturingProgress : IProgress<ShredProgress>
    {
        public List<ShredProgress> Reports { get; } = new();

        public void Report(ShredProgress value) => Reports.Add(value);
    }

    /// <summary>
    /// 装饰 Stream,记录每次 WriteAsync 的 chunk 大小与 FlushAsync 调用次数,
    /// 可选 writeDelayMs 用于测试进度节流的时序行为。
    /// </summary>
    private sealed class RecordingStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _writeDelayMs;

        public List<int> WriteSizes { get; } = new();
        public int FlushAsyncCount { get; private set; }

        public RecordingStream(Stream inner, int writeDelayMs = 0)
        {
            _inner = inner;
            _writeDelayMs = writeDelayMs;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteSizes.Add(buffer.Length);
            if (_writeDelayMs > 0)
                await Task.Delay(_writeDelayMs, cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushAsyncCount++;
            return _inner.FlushAsync(cancellationToken);
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteSizes.Add(count);
            _inner.Write(buffer, offset, count);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
