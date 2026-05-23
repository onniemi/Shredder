namespace Shredder.Core.Models;

/// <summary>粉碎进度回调载荷。</summary>
public sealed record ShredProgress(
    string FilePath,
    int PassIndex,
    int PassCount,
    long BytesWritten,
    long TotalBytes);
