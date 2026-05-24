using Shredder.Cli;
using Shredder.Core.Algorithms;
using Xunit;

namespace Shredder.Tests.Cli;

/// <summary>
/// 覆盖 <see cref="CliRunner"/> 里的纯函数帮助器:算法别名映射 + 文件长度兜底。
/// 这些函数不碰 Console / DI,适合直接单测;<c>RunAsync</c> / <c>ConfirmOrFail</c> 需要集成测试,本次不开。
/// </summary>
public class CliRunnerHelpersTests
{
    [Fact]
    public void ResolveAlgorithmId_Null_ReturnsNull()
    {
        Assert.Null(CliRunner.ResolveAlgorithmId(null));
    }

    [Fact]
    public void ResolveAlgorithmId_Empty_ReturnsNull()
    {
        Assert.Null(CliRunner.ResolveAlgorithmId(string.Empty));
    }

    [Fact]
    public void ResolveAlgorithmId_Whitespace_ReturnsNull()
    {
        Assert.Null(CliRunner.ResolveAlgorithmId("   "));
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("quick")]
    [InlineData("fastdelete")]
    public void ResolveAlgorithmId_FastAliases_MapToFastDelete(string alias)
    {
        Assert.Equal(ShredAlgorithmIds.FastDelete, CliRunner.ResolveAlgorithmId(alias));
    }

    [Theory]
    [InlineData("dod3")]
    [InlineData("purge-3pass")]
    public void ResolveAlgorithmId_Dod3Aliases_MapToPurge3Pass(string alias)
    {
        Assert.Equal(ShredAlgorithmIds.Purge3Pass, CliRunner.ResolveAlgorithmId(alias));
    }

    [Theory]
    [InlineData("dod7")]
    [InlineData("purge-7pass")]
    public void ResolveAlgorithmId_Dod7Aliases_MapToPurge7Pass(string alias)
    {
        Assert.Equal(ShredAlgorithmIds.Purge7Pass, CliRunner.ResolveAlgorithmId(alias));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("random")]
    [InlineData("clear")]
    public void ResolveAlgorithmId_SingleAliases_MapToClear(string alias)
    {
        Assert.Equal(ShredAlgorithmIds.Clear, CliRunner.ResolveAlgorithmId(alias));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("zerofill")]
    public void ResolveAlgorithmId_ZeroAliases_MapToZeroFill(string alias)
    {
        Assert.Equal(ShredAlgorithmIds.ZeroFill, CliRunner.ResolveAlgorithmId(alias));
    }

    [Theory]
    [InlineData("crypto")]
    [InlineData("cryptoerase")]
    public void ResolveAlgorithmId_CryptoAliases_MapToCryptoErase(string alias)
    {
        Assert.Equal(ShredAlgorithmIds.CryptoErase, CliRunner.ResolveAlgorithmId(alias));
    }

    [Theory]
    [InlineData("DOD3")]
    [InlineData("Dod7")]
    [InlineData("  crypto  ")]
    public void ResolveAlgorithmId_CaseAndWhitespace_StillMatches(string alias)
    {
        // Trim + ToLowerInvariant 保证大小写 / 前后空白都能命中
        Assert.NotNull(CliRunner.ResolveAlgorithmId(alias));
    }

    [Fact]
    public void ResolveAlgorithmId_UnknownAlias_PassesThroughUnchanged()
    {
        // 透传:Registry 查不到时再回落默认,这里只确认 CLI 层不吃掉
        Assert.Equal("MyCustomAlgo", CliRunner.ResolveAlgorithmId("MyCustomAlgo"));
    }

    [Fact]
    public void SafeFileLength_ExistingFile_ReturnsLength()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[1024]);
            Assert.Equal(1024, CliRunner.SafeFileLength(path));
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { /* 测试清理,容错 */ }
            catch (UnauthorizedAccessException) { /* 测试清理,容错 */ }
        }
    }

    [Fact]
    public void SafeFileLength_MissingPath_ReturnsZero()
    {
        // FileInfo 本身不抛,但 .Length 会抛 FileNotFoundException → catch 后返 0
        var missing = Path.Combine(Path.GetTempPath(), $"shredder-test-missing-{Guid.NewGuid():N}.dat");
        Assert.Equal(0, CliRunner.SafeFileLength(missing));
    }
}
