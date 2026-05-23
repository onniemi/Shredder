namespace Shredder.Core.Services;

/// <summary>
/// 抽象回收站底层条目枚举,便于测试时注入 fake(避免污染真实 OS 回收站)。
/// </summary>
/// <remarks>
/// 由默认实现 <see cref="DefaultRecycleBinEnumerator"/>负责扫描所有固定盘的 <c>$Recycle.Bin</c>,
/// 同时对 ACL/权限/损坏目录抛出的异常做静默降级,避免单点失败阻断整体清空。
/// </remarks>
public interface IRecycleBinEnumerator
{
    /// <summary>
    /// 枚举所有候选回收站文件路径(物理数据文件,不含 $I 元数据头)。
    /// 在迭代中遇到不可枚举的子目录应自行降级(吞掉异常,记录到日志),不应让单点故障阻断全局遍历。
    /// </summary>
    IEnumerable<string> EnumerateFiles(CancellationToken ct);
}

/// <summary>
/// 默认实现:遍历所有 <see cref="DriveType.Fixed"/>且 IsReady 的盘符下的 <c>$Recycle.Bin</c>。
/// </summary>
internal sealed class DefaultRecycleBinEnumerator : IRecycleBinEnumerator
{
    public IEnumerable<string> EnumerateFiles(CancellationToken ct)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

            var binPath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            if (!Directory.Exists(binPath)) continue;

            // SafeEnumerateFiles 会把 ACL/损坏目录的异常吞掉,返回空集合而非中断
            foreach (var file in SafeEnumerateFiles(binPath))
            {
                ct.ThrowIfCancellationRequested();
                yield return file;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }
}
