namespace Shredder.Core.FileSystem;

/// <summary>
/// Windows 长路径(&gt;260 字符)支持工具:为路径前缀添加 <c>\\?\</c>。
/// </summary>
/// <remarks>
/// 路径规则:
/// <list type="bullet">
///   <item>本地路径 <c>C:\foo</c> → <c>\\?\C:\foo</c></item>
///   <item>UNC 路径 <c>\\server\share\foo</c> → <c>\\?\UNC\server\share\foo</c></item>
///   <item>已含前缀的路径或设备路径 <c>\\.\</c> 保持不变</item>
/// </list>
/// 注意:加了 <c>\\?\</c> 之后,所有相对路径 / 短文件名 / "." ".." 规约都会被关闭,
/// 调用方必须传入已 <c>Path.GetFullPath</c> 规范化过的绝对路径。
/// </remarks>
public static class LongPathHelper
{
    private const string LongPathPrefix = @"\\?\";
    private const string UncLongPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";

    /// <summary>
    /// 给路径加 <c>\\?\</c> 前缀。若已带前缀或为设备路径,原样返回。
    /// 传入空字符串或 null 视为无效,直接返回原值。
    /// </summary>
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(LongPathPrefix, StringComparison.Ordinal)) return path;
        if (path.StartsWith(DevicePrefix, StringComparison.Ordinal)) return path;

        // UNC: \\server\share\... → \\?\UNC\server\share\...
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return UncLongPrefix + path[2..];

        // 必须是绝对盘符路径,例如 "C:\..."
        if (path.Length >= 2 && path[1] == ':')
            return LongPathPrefix + path;

        // 其它情况(相对路径、UNC 但格式不对)直接返回,让上层报错
        return path;
    }

    /// <summary>
    /// 仅当路径接近 MAX_PATH(默认 248,留出一些安全余量)时才转长路径。
    /// 普通短路径维持原样,便于日志/对话框显示。
    /// </summary>
    public static string ToExtendedPathIfNeeded(string path, int threshold = 248)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return path.Length >= threshold ? ToExtendedPath(path) : path;
    }

    /// <summary>剥离 <c>\\?\</c> 前缀,主要给日志/UI 显示用。</summary>
    public static string StripExtendedPrefix(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(UncLongPrefix, StringComparison.Ordinal))
            return @"\\" + path[UncLongPrefix.Length..];
        if (path.StartsWith(LongPathPrefix, StringComparison.Ordinal))
            return path[LongPathPrefix.Length..];
        return path;
    }
}
