using Shredder.Cli;
using Shredder.Core.Models;
using Xunit;

namespace Shredder.Tests.Cli;

/// <summary>
/// 覆盖 <see cref="ConsoleProgressRenderer"/> 的两个格式化纯函数。
/// 实际的节流 / Console 写入逻辑涉及 stdout side-effect,需要捕获 <c>Console.Out</c>,本次不测。
/// </summary>
public class ConsoleProgressRendererTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1024L * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 1024, "1.0 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.0 TB")]
    public void FormatBytes_VariousSizes_FormattedWithInvariantCulture(long bytes, string expected)
    {
        Assert.Equal(expected, ConsoleProgressRenderer.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_OverTB_ClampsAtTB()
    {
        // 1 PB = 1024 TB,函数最大单位是 TB,会停在 i = 3 并显示为 "1024.0 TB"
        var result = ConsoleProgressRenderer.FormatBytes(1024L * 1024 * 1024 * 1024 * 1024);
        Assert.EndsWith(" TB", result);
    }

    [Fact]
    public void FormatLine_BasicProgress_ContainsExpectedTokens()
    {
        var p = new ShredProgress(FilePath: @"D:\x", PassIndex: 2, PassCount: 3,
                                  BytesWritten: 512, TotalBytes: 1024);
        var line = ConsoleProgressRenderer.FormatLine(p);
        Assert.Contains("pass 2/3", line);
        Assert.Contains("50.0", line);
        Assert.Contains("512 B", line);
        Assert.Contains("1.0 KB", line);
    }

    [Fact]
    public void FormatLine_ZeroTotal_PctIsZeroAndDoesNotThrow()
    {
        // TotalBytes=0 走的是 `p.TotalBytes > 0` 的 false 分支,pct=0,不应触发除零
        var p = new ShredProgress(FilePath: @"D:\x", PassIndex: 1, PassCount: 1,
                                  BytesWritten: 0, TotalBytes: 0);
        var line = ConsoleProgressRenderer.FormatLine(p);
        Assert.Contains("0.0", line);
        Assert.Contains("0 B", line);
    }

    [Fact]
    public void FormatLine_ZeroPassCount_TreatedAsOne()
    {
        // Math.Max(1, p.PassCount) 防御性兜底:即使 PassCount=0 也显示为 /1
        var p = new ShredProgress(FilePath: @"D:\x", PassIndex: 0, PassCount: 0,
                                  BytesWritten: 0, TotalBytes: 1024);
        var line = ConsoleProgressRenderer.FormatLine(p);
        Assert.Contains("pass 0/1", line);
    }

    [Fact]
    public void FormatLine_FullProgress_ShowsHundredPercent()
    {
        var p = new ShredProgress(FilePath: @"D:\x", PassIndex: 3, PassCount: 3,
                                  BytesWritten: 1024, TotalBytes: 1024);
        var line = ConsoleProgressRenderer.FormatLine(p);
        Assert.Contains("100.0", line);
        Assert.Contains("pass 3/3", line);
    }

    [Fact]
    public void FormatLine_OverflowBytes_ClampedTo100Pct()
    {
        // Math.Clamp 0..100:即使 BytesWritten 大于 Total,百分比也不应超过 100
        var p = new ShredProgress(FilePath: @"D:\x", PassIndex: 1, PassCount: 1,
                                  BytesWritten: 2048, TotalBytes: 1024);
        var line = ConsoleProgressRenderer.FormatLine(p);
        Assert.Contains("100.0", line);
    }
}
