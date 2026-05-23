using Microsoft.Win32;

namespace Shredder.Integration;

/// <summary>
/// 资源管理器右键菜单注册（HKCU，无需管理员权限）。
/// Install/Uninstall 均为幂等操作。
/// </summary>
public static class ShellMenuInstaller
{
    private const string MenuText = "粉碎一切";
    internal const string FileKey = @"Software\Classes\*\shell\Shredder";
    internal const string DirKey  = @"Software\Classes\Directory\shell\Shredder";

    public static void Install(string exePath)
        => Install(exePath, Registry.CurrentUser);

    public static void Uninstall()
        => Uninstall(Registry.CurrentUser);

    /// <summary>
    /// 当前右键菜单是否已安装。两个根键 (File / Directory) 同时存在且都包含 command 子键时返回 true。
    /// </summary>
    public static bool IsInstalled()
        => IsInstalled(Registry.CurrentUser);

    /// <summary>
    /// 当前安装记录的目标 exe 路径（若两个根键的 command 值不一致或缺失，返回 null）。
    /// </summary>
    public static string? GetInstalledExePath()
        => GetInstalledExePath(Registry.CurrentUser);

    // -------- testable overloads (root 可指向任意 HKCU 子树) --------

    internal static void Install(string exePath, RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("exePath 不能为空", nameof(exePath));

        Register(root, FileKey, exePath);
        Register(root, DirKey,  exePath);
    }

    internal static void Uninstall(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.DeleteSubKeyTree(FileKey, throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(DirKey,  throwOnMissingSubKey: false);
    }

    internal static bool IsInstalled(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return HasCommand(root, FileKey) && HasCommand(root, DirKey);
    }

    internal static string? GetInstalledExePath(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var f = ReadCommandExe(root, FileKey);
        var d = ReadCommandExe(root, DirKey);
        if (f is null || d is null) return null;
        return string.Equals(f, d, StringComparison.OrdinalIgnoreCase) ? f : null;
    }

    private static bool HasCommand(RegistryKey root, string baseKey)
    {
        using var node = root.OpenSubKey(baseKey);
        if (node is null) return false;
        using var cmd = node.OpenSubKey("command");
        return cmd is not null && cmd.GetValue(null) is string s && !string.IsNullOrWhiteSpace(s);
    }

    private static string? ReadCommandExe(RegistryKey root, string baseKey)
    {
        using var node = root.OpenSubKey(baseKey);
        using var cmd = node?.OpenSubKey("command");
        if (cmd?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw))
            return null;
        // 形如  "exe" "%1"   抽出第一个引号包裹的部分
        if (raw.Length > 0 && raw[0] == '"')
        {
            var end = raw.IndexOf('"', 1);
            if (end > 1) return raw.Substring(1, end - 1);
        }
        var space = raw.IndexOf(' ');
        return space > 0 ? raw[..space] : raw;
    }

    private static void Register(RegistryKey root, string baseKey, string exePath)
    {
        using var node = root.CreateSubKey(baseKey)!;
        node.SetValue(null, MenuText);
        node.SetValue("Icon", exePath);
        using var cmd = node.CreateSubKey("command")!;
        cmd.SetValue(null, $"\"{exePath}\" \"%1\"");
    }
}
