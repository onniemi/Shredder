using System;
using Microsoft.Win32;
using Shredder.Integration;
using Xunit;

namespace Shredder.Tests;

/// <summary>
/// 验证 ShellMenuInstaller 的状态检测 + 幂等性。
/// 通过把 root 指向 HKCU 下一个临时子树，避免污染真实右键菜单注册。
/// </summary>
public sealed class ShellMenuInstallerTests : IDisposable
{
    private readonly string _testRootPath;
    private readonly RegistryKey _testRoot;

    public ShellMenuInstallerTests()
    {
        // 每个测试实例一个 GUID 子键，确保并行/复跑互不干扰
        _testRootPath = $@"Software\ShredderTests\{Guid.NewGuid():N}";
        _testRoot = Registry.CurrentUser.CreateSubKey(_testRootPath)!;
    }

    public void Dispose()
    {
        _testRoot.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_testRootPath, throwOnMissingSubKey: false);
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenNotInstalled()
    {
        Assert.False(ShellMenuInstaller.IsInstalled(_testRoot));
        Assert.Null(ShellMenuInstaller.GetInstalledExePath(_testRoot));
    }

    [Fact]
    public void Install_CreatesBothKeys_AndIsInstalledReturnsTrue()
    {
        const string exe = @"C:\Tools\Shredder.exe";
        ShellMenuInstaller.Install(exe, _testRoot);

        Assert.True(ShellMenuInstaller.IsInstalled(_testRoot));
        using var fileCmd = _testRoot.OpenSubKey(ShellMenuInstaller.FileKey + @"\command");
        using var dirCmd  = _testRoot.OpenSubKey(ShellMenuInstaller.DirKey  + @"\command");
        Assert.NotNull(fileCmd);
        Assert.NotNull(dirCmd);
        Assert.Equal($"\"{exe}\" \"%1\"", fileCmd!.GetValue(null));
        Assert.Equal($"\"{exe}\" \"%1\"", dirCmd!.GetValue(null));
    }

    [Fact]
    public void Install_IsIdempotent_RepeatedCallsKeepInstalled()
    {
        const string exe = @"C:\Tools\Shredder.exe";

        ShellMenuInstaller.Install(exe, _testRoot);
        ShellMenuInstaller.Install(exe, _testRoot);
        ShellMenuInstaller.Install(exe, _testRoot);

        Assert.True(ShellMenuInstaller.IsInstalled(_testRoot));
        Assert.Equal(exe, ShellMenuInstaller.GetInstalledExePath(_testRoot));
    }

    [Fact]
    public void Install_OverwritesExePath_WhenCalledWithDifferentExe()
    {
        ShellMenuInstaller.Install(@"C:\Old\Shredder.exe", _testRoot);
        ShellMenuInstaller.Install(@"D:\New\Shredder.exe", _testRoot);

        Assert.True(ShellMenuInstaller.IsInstalled(_testRoot));
        Assert.Equal(@"D:\New\Shredder.exe", ShellMenuInstaller.GetInstalledExePath(_testRoot));
    }

    [Fact]
    public void Uninstall_RemovesBothKeys_AndIsInstalledReturnsFalse()
    {
        ShellMenuInstaller.Install(@"C:\Tools\Shredder.exe", _testRoot);
        Assert.True(ShellMenuInstaller.IsInstalled(_testRoot));

        ShellMenuInstaller.Uninstall(_testRoot);

        Assert.False(ShellMenuInstaller.IsInstalled(_testRoot));
        using var file = _testRoot.OpenSubKey(ShellMenuInstaller.FileKey);
        using var dir  = _testRoot.OpenSubKey(ShellMenuInstaller.DirKey);
        Assert.Null(file);
        Assert.Null(dir);
    }

    [Fact]
    public void Uninstall_IsIdempotent_NoExceptionWhenAlreadyUninstalled()
    {
        // never installed
        ShellMenuInstaller.Uninstall(_testRoot);
        ShellMenuInstaller.Uninstall(_testRoot);
        Assert.False(ShellMenuInstaller.IsInstalled(_testRoot));

        // install -> uninstall -> uninstall again
        ShellMenuInstaller.Install(@"C:\Tools\Shredder.exe", _testRoot);
        ShellMenuInstaller.Uninstall(_testRoot);
        ShellMenuInstaller.Uninstall(_testRoot);
        Assert.False(ShellMenuInstaller.IsInstalled(_testRoot));
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenOnlyOneSideExists()
    {
        // 手工只建 File 侧（不要 command），模拟半安装状态
        using (var file = _testRoot.CreateSubKey(ShellMenuInstaller.FileKey)!)
        {
            file.SetValue(null, "粉碎一切");
        }
        Assert.False(ShellMenuInstaller.IsInstalled(_testRoot));

        // 加上 command 但 Dir 侧仍然缺失
        using (var fileCmd = _testRoot.CreateSubKey(ShellMenuInstaller.FileKey + @"\command")!)
        {
            fileCmd.SetValue(null, "\"x.exe\" \"%1\"");
        }
        Assert.False(ShellMenuInstaller.IsInstalled(_testRoot));
    }

    [Fact]
    public void GetInstalledExePath_ReturnsNull_WhenSidesDisagree()
    {
        ShellMenuInstaller.Install(@"C:\A\Shredder.exe", _testRoot);
        // 手动篡改 Dir 侧的 command，模拟不一致
        using (var dirCmd = _testRoot.CreateSubKey(ShellMenuInstaller.DirKey + @"\command")!)
        {
            dirCmd.SetValue(null, "\"C:\\B\\Shredder.exe\" \"%1\"");
        }

        Assert.Null(ShellMenuInstaller.GetInstalledExePath(_testRoot));
    }

    [Fact]
    public void Install_WithEmptyExePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => ShellMenuInstaller.Install("", _testRoot));
        Assert.Throws<ArgumentException>(() => ShellMenuInstaller.Install("   ", _testRoot));
    }

    [Fact]
    public void Install_WithNullExePath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ShellMenuInstaller.Install(null!, _testRoot));
    }
}
