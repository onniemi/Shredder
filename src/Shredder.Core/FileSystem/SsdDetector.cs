using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Shredder.Core.Native;

namespace Shredder.Core.FileSystem;

/// <summary>
/// 检测一个卷所在的物理设备是否为 SSD,以及是否启用 TRIM。
/// 用于「SSD 上多次覆写既无效又伤盘,改走 TRIM + 加密擦除」的策略分支。
/// </summary>
/// <remarks>
/// 检测方法是给卷设备发 <c>IOCTL_STORAGE_QUERY_PROPERTY</c>:
/// <list type="bullet">
///   <item><c>StorageDeviceSeekPenaltyProperty</c> = false → 无寻道延迟 → SSD</item>
///   <item><c>StorageDeviceTrimProperty</c> = true → 启用 TRIM</item>
/// </list>
/// 注意:RAID / 虚拟磁盘 / USB 桥接控制器可能返回不准确的结果,因此本类暴露
/// <see cref="DeviceProfile"/> 三态(SSD/HDD/Unknown),由调用方决定回退策略。
/// </remarks>
public class SsdDetector
{
    public enum DeviceProfile
    {
        Unknown = 0,
        HardDisk = 1,
        SolidState = 2,
    }

    public readonly record struct StorageInfo(DeviceProfile Profile, bool TrimEnabled);

    /// <summary>
    /// 查询给定路径所在卷的存储信息。失败时返回 <see cref="DeviceProfile.Unknown"/>。
    /// 设为 virtual 是为了在单元测试里用 fake 子类覆盖,真实运行路径不变。
    /// </summary>
    [SuppressMessage("Performance", "CA1822", Justification = "Registered as DI singleton; instance API for future logger injection.")]
    public virtual StorageInfo Probe(string anyPathOnVolume)
    {
        if (string.IsNullOrEmpty(anyPathOnVolume))
            return new StorageInfo(DeviceProfile.Unknown, false);

        // 取根路径,例如 "C:\foo\bar.txt" → "C:\"
        string? root = Path.GetPathRoot(Path.GetFullPath(anyPathOnVolume));
        if (string.IsNullOrEmpty(root)) return new StorageInfo(DeviceProfile.Unknown, false);

        // 转成卷设备路径:"C:\" → "\\.\C:"
        string drive = root.TrimEnd('\\').TrimEnd(':');
        if (drive.Length != 1) return new StorageInfo(DeviceProfile.Unknown, false);
        string devicePath = @"\\.\" + drive + ":";

        using var handle = NativeMethods.CreateFileW(
            devicePath,
            0, // 0 表示仅查询属性,无需读权限,可避免要管理员
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid) return new StorageInfo(DeviceProfile.Unknown, false);

        var profile = QuerySeekPenalty(handle.DangerousGetHandle()) switch
        {
            true => DeviceProfile.HardDisk,
            false => DeviceProfile.SolidState,
            null => DeviceProfile.Unknown,
        };
        // 只有 SSD 才需要 TRIM;HDD 即使返回 true 也无意义
        bool trim = profile == DeviceProfile.SolidState && (QueryTrim(handle.DangerousGetHandle()) ?? false);
        return new StorageInfo(profile, trim);
    }

    private static bool? QuerySeekPenalty(IntPtr deviceHandle)
    {
        var query = new NativeMethods.STORAGE_PROPERTY_QUERY
        {
            PropertyId = NativeMethods.STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty,
            QueryType = NativeMethods.STORAGE_QUERY_TYPE.PropertyStandardQuery,
            AdditionalParameters0 = 0,
        };
        var descriptor = new NativeMethods.DEVICE_SEEK_PENALTY_DESCRIPTOR();

        return RunIoctl(deviceHandle, query, ref descriptor) ? descriptor.IncursSeekPenalty : null;
    }

    private static bool? QueryTrim(IntPtr deviceHandle)
    {
        var query = new NativeMethods.STORAGE_PROPERTY_QUERY
        {
            PropertyId = NativeMethods.STORAGE_PROPERTY_ID.StorageDeviceTrimProperty,
            QueryType = NativeMethods.STORAGE_QUERY_TYPE.PropertyStandardQuery,
            AdditionalParameters0 = 0,
        };
        var descriptor = new NativeMethods.DEVICE_TRIM_DESCRIPTOR();

        return RunIoctl(deviceHandle, query, ref descriptor) ? descriptor.TrimEnabled : null;
    }

    /// <summary>
    /// 通用 IOCTL_STORAGE_QUERY_PROPERTY 调用包装。
    /// 把 <typeparamref name="TQuery"/> 和 <typeparamref name="TDescriptor"/> 分别拷到非托管内存,
    /// 因为 ref-out 的 Marshal 行为对嵌套结构不可靠。
    /// </summary>
    private static bool RunIoctl<TQuery, TDescriptor>(IntPtr deviceHandle, TQuery query, ref TDescriptor descriptor)
        where TQuery : struct
        where TDescriptor : struct
    {
        int querySize = Marshal.SizeOf<TQuery>();
        int descSize = Marshal.SizeOf<TDescriptor>();

        IntPtr inBuf = Marshal.AllocHGlobal(querySize);
        IntPtr outBuf = Marshal.AllocHGlobal(descSize);
        try
        {
            Marshal.StructureToPtr(query, inBuf, fDeleteOld: false);
            // 清零 outBuf,避免读到旧内存的杂音
            for (int i = 0; i < descSize; i++) Marshal.WriteByte(outBuf, i, 0);

            using var safe = new Microsoft.Win32.SafeHandles.SafeFileHandle(deviceHandle, ownsHandle: false);
            bool ok = NativeMethods.DeviceIoControl(
                safe,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                inBuf, (uint)querySize,
                outBuf, (uint)descSize,
                out _,
                IntPtr.Zero);

            if (!ok) return false;
            descriptor = Marshal.PtrToStructure<TDescriptor>(outBuf);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }
}
