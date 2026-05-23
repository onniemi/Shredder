using System.Runtime.InteropServices;
using Shredder.Core.Native;

namespace Shredder.Core.FileSystem;

/// <summary>
/// 检测路径是否为 NTFS 重解析点(符号链接 / Junction / mount point)。
/// 默认行为:发现重解析点立刻拒绝粉碎,避免顺着符号链接误删链外的真实目标。
/// </summary>
public static class ReparsePointDetector
{
    /// <summary>路径是否为重解析点(包含符号链接、Junction、mount point)。</summary>
    /// <remarks>
    /// 用 <c>GetFileAttributesExW</c> 而非 <see cref="System.IO.File.GetAttributes"/>,
    /// 因为前者不会抛 IOException,且对超长路径友好。
    /// </remarks>
    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var extended = LongPathHelper.ToExtendedPathIfNeeded(path);
        if (!NativeMethods.GetFileAttributesExW(
                extended,
                NativeMethods.GET_FILEEX_INFO_LEVELS.GetFileExInfoStandard,
                out var data))
        {
            // 路径不存在或权限不足:不算重解析点,交给上层报错
            return false;
        }
        return (data.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    /// <summary>取路径的属性原始位,供 SetAttributes 备份/恢复使用。返回 <c>null</c> 表示获取失败。</summary>
    public static uint? TryGetAttributes(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var extended = LongPathHelper.ToExtendedPathIfNeeded(path);
        uint attrs = NativeMethods.GetFileAttributesW(extended);
        if (attrs == NativeMethods.INVALID_FILE_ATTRIBUTES) return null;
        return attrs;
    }

    /// <summary>设置属性原始位。失败时抛 <see cref="System.ComponentModel.Win32Exception"/>。</summary>
    public static void SetAttributes(string path, uint attrs)
    {
        var extended = LongPathHelper.ToExtendedPathIfNeeded(path);
        if (!NativeMethods.SetFileAttributesW(extended, attrs))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
