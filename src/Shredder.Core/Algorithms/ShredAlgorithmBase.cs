using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Models;

namespace Shredder.Core.Algorithms;

/// <summary>覆写算法的公共基类：分块写、汇报进度、强制刷盘。</summary>
public abstract class ShredAlgorithmBase : IShredAlgorithm
{
    /// <summary>缓冲区大小，由 <see cref="ShredderIoOptions.BufferSizeBytes"/> 注入；默认 4 MB。</summary>
    protected int BufferSize { get; }

    private readonly int _flushEveryNBuffers;
    private readonly int _progressIntervalMs;

    protected ShredAlgorithmBase(IOptions<ShredderOptions>? options = null)
    {
        var io = options?.Value.Io ?? new ShredderIoOptions();
        BufferSize = io.BufferSizeBytes > 0 ? io.BufferSizeBytes : 4 * 1024 * 1024;
        _flushEveryNBuffers = Math.Max(0, io.FlushEveryNBuffers);
        _progressIntervalMs = Math.Max(0, io.ProgressReportIntervalMs);
    }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract int PassCount { get; }

    public virtual async Task ShredAsync(
        Stream stream,
        long length,
        string filePath,
        IProgress<ShredProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[BufferSize];
        for (int pass = 0; pass < PassCount; pass++)
        {
            ct.ThrowIfCancellationRequested();
            stream.Seek(0, SeekOrigin.Begin);
            long written = 0;
            int buffersSinceFlush = 0;
            long lastReportTickMs = Environment.TickCount64;
            while (written < length)
            {
                ct.ThrowIfCancellationRequested();
                int chunk = (int)Math.Min(BufferSize, length - written);
                FillBuffer(pass, buffer, chunk);
                await stream.WriteAsync(buffer.AsMemory(0, chunk), ct);
                written += chunk;
                buffersSinceFlush++;

                // 中段 flush：仅当配置 > 0 且未到 pass 末尾时触发，避免和 pass 末尾的强制 flush 重复
                if (_flushEveryNBuffers > 0 && buffersSinceFlush >= _flushEveryNBuffers && written < length)
                {
                    await stream.FlushAsync(ct);
                    buffersSinceFlush = 0;
                }

                // 进度节流：每 _progressIntervalMs 上报一次；pass 末尾强制上报最终进度
                long now = Environment.TickCount64;
                bool isLast = written == length;
                if (progress is not null &&
                    (isLast || _progressIntervalMs == 0 || (now - lastReportTickMs) >= _progressIntervalMs))
                {
                    progress.Report(new ShredProgress(filePath, pass + 1, PassCount, written, length));
                    lastReportTickMs = now;
                }
            }
            await stream.FlushAsync(ct);
            // 强制写到物理介质，避免被系统缓存吞掉
            if (stream is FileStream fs) fs.Flush(true);
        }
    }

    /// <summary>子类实现：根据 pass 序号填充缓冲区。</summary>
    protected abstract void FillBuffer(int passIndex, byte[] buffer, int count);

    protected static void FillRandom(byte[] buffer, int count)
    {
        RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
    }

    protected static void FillConstant(byte[] buffer, int count, byte value)
    {
        Array.Fill(buffer, value, 0, count);
    }
}
