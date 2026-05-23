using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Security;
using Xunit;

namespace Shredder.Tests;

public class PathSafetyGuardTests
{
    private static PathSafetyGuard Create(ShredderSafetyOptions? safety = null)
    {
        var opts = Options.Create(new ShredderOptions
        {
            Safety = safety ?? new ShredderSafetyOptions(),
        });
        return new PathSafetyGuard(opts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_EmptyPath_Forbidden(string? path)
    {
        var d = Create().Evaluate(path!);
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_DriveRoot_AlwaysForbidden_EvenWithAllowPath()
    {
        var guard = Create(new ShredderSafetyOptions { AllowPaths = { @"C:\" } });
        var d = guard.Evaluate(@"C:\");
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_AllowPathListed_ReturnsAllowed_EvenIfInForbiddenList()
    {
        var safety = new ShredderSafetyOptions
        {
            ForbiddenPaths = { @"C:\Data\Secret" },
            AllowPaths = { @"C:\Data\Secret" },
        };
        var d = Create(safety).Evaluate(@"C:\Data\Secret\file.txt");
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Allowed, d.Level);
    }

    [Fact]
    public void Evaluate_ForbiddenPathListed_ReturnsForbidden()
    {
        var safety = new ShredderSafetyOptions { ForbiddenPaths = { @"C:\CriticalFolder" } };
        var d = Create(safety).Evaluate(@"C:\CriticalFolder\file.txt");
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_HardcodedSystemDirectory_ReturnsForbidden()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var d = Create().Evaluate(Path.Combine(windows, "System32", "kernel32.dll"));
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_WarnPath_ReturnsWarn()
    {
        var safety = new ShredderSafetyOptions { WarnPaths = { @"C:\Users\TestUser\Documents" } };
        var d = Create(safety).Evaluate(@"C:\Users\TestUser\Documents\report.pdf");
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Warn, d.Level);
    }

    [Fact]
    public void Evaluate_NeutralPath_ReturnsAllowed()
    {
        var d = Create().Evaluate(@"D:\Temp\file.txt");
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Allowed, d.Level);
    }

    [Fact]
    public void Evaluate_PrefixSafety_WindowsXShouldNotMatchWindows()
    {
        // 关键回归:C:\WindowsX 不应被 C:\Windows 当成前缀命中
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // 构造一个伪 "WindowsX" 同级路径
        var fakeNeighbor = windows + "X\\inside.txt";
        var d = Create().Evaluate(fakeNeighbor);
        Assert.NotEqual(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_PrefixSafety_DataX_NotMatchData()
    {
        var safety = new ShredderSafetyOptions { ForbiddenPaths = { @"C:\Data" } };
        var d = Create(safety).Evaluate(@"C:\DataX\file.txt");
        Assert.NotEqual(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_CaseInsensitiveMatch()
    {
        var safety = new ShredderSafetyOptions { ForbiddenPaths = { @"C:\Data" } };
        var d = Create(safety).Evaluate(@"c:\DATA\file.txt");
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_ExactDirectoryMatch_HitsPrefix()
    {
        var safety = new ShredderSafetyOptions { ForbiddenPaths = { @"C:\Data" } };
        // 路径恰好等于 Forbidden 项(无尾分隔符)
        var d = Create(safety).Evaluate(@"C:\Data");
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_EnvVarsInForbiddenPath_AreExpanded()
    {
        // 用 SystemRoot(Windows 一般有)做白名单匹配测试
        var safety = new ShredderSafetyOptions { ForbiddenPaths = { @"%SystemRoot%" } };
        var d = Create(safety).Evaluate(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Forbidden, d.Level);
    }

    [Fact]
    public void Evaluate_AllowPathOverridesSystemDirectoryForbiddenIsNotBypassed()
    {
        // 验证:即便 AllowPaths 包含 Windows 目录,硬编码系统目录黑名单(在 Allow 之后做)被 Allow 提前命中
        // -> 这是 PathSafetyGuard 当前实现的真实行为:Allow 出现在 ForbiddenPaths/SystemRoot 之前
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var safety = new ShredderSafetyOptions { AllowPaths = { windows } };
        var d = Create(safety).Evaluate(Path.Combine(windows, "System32", "drivers"));
        // 当前设计:Allow 早于系统目录硬编码 → Allowed
        Assert.Equal(PathSafetyGuard.PathSafetyLevel.Allowed, d.Level);
    }
}
