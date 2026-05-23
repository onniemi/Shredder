using System.Runtime.InteropServices;
using Shredder.Core.Native;

namespace Shredder.Core.FileSystem;

/// <summary>
/// 枚举 NTFS Alternate Data Streams(ADS)。
/// 粉碎主流前必须先处理 ADS,否则 :Zone.Identifier 之类的备用流会残留。
/// </summary>
public static class AlternateDataStreamEnumerator
{
    /// <summary>
    /// 枚举给定文件的所有数据流,返回 <c>:streamName:$DATA</c> 格式的相对名 + 字节长度。
    /// 主流也会被返回(<c>::$DATA</c>),调用方需自行过滤。
    /// 非 NTFS 卷或文件不存在时返回空集合。
    /// </summary>
    public static IReadOnlyList<StreamInfo> Enumerate(string filePath)
    {
        var result = new List<StreamInfo>();
        if (string.IsNullOrEmpty(filePath)) return result;

        var extended = LongPathHelper.ToExtendedPathIfNeeded(filePath);
        using var handle = NativeMethods.FindFirstStreamW(
            extended,
            NativeMethods.STREAM_INFO_LEVELS.FindStreamInfoStandard,
            out var data,
            0);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_HANDLE_EOF(38) / 文件不存在 / 非 NTFS:返回空
            return result;
        }

        result.Add(new StreamInfo(data.cStreamName, data.StreamSize));
        while (NativeMethods.FindNextStreamW(handle, out data))
            result.Add(new StreamInfo(data.cStreamName, data.StreamSize));

        return result;
    }

    /// <summary>
    /// 仅返回非主流的 ADS 名(去掉主流 <c>::$DATA</c>),格式为 <c>:streamName</c>。
    /// </summary>
    public static IReadOnlyList<string> EnumerateAdsNames(string filePath)
    {
        var all = Enumerate(filePath);
        var list = new List<string>();
        foreach (var s in all)
        {
            // 主流名为 "::$DATA",非主流为 ":Zone.Identifier:$DATA" 等
            if (s.Name.Equals("::$DATA", StringComparison.Ordinal)) continue;
            // 形如 ":Zone.Identifier:$DATA" → 取出 ":Zone.Identifier"
            int idx = s.Name.IndexOf(":$DATA", 1, StringComparison.Ordinal);
            list.Add(idx > 0 ? s.Name[..idx] : s.Name);
        }
        return list;
    }

    public readonly record struct StreamInfo(string Name, long Size);
}
