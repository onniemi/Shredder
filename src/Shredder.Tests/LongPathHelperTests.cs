using Shredder.Core.FileSystem;
using Xunit;

namespace Shredder.Tests;

public class LongPathHelperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToExtendedPath_NullOrEmpty_ReturnedAsIs(string? input)
    {
        Assert.Equal(input, LongPathHelper.ToExtendedPath(input!));
    }

    [Fact]
    public void ToExtendedPath_LocalAbsolute_PrependsLongPrefix()
    {
        Assert.Equal(@"\\?\C:\foo\bar", LongPathHelper.ToExtendedPath(@"C:\foo\bar"));
    }

    [Fact]
    public void ToExtendedPath_AlreadyPrefixed_Unchanged()
    {
        const string p = @"\\?\C:\foo";
        Assert.Equal(p, LongPathHelper.ToExtendedPath(p));
    }

    [Fact]
    public void ToExtendedPath_DevicePath_Unchanged()
    {
        const string p = @"\\.\PHYSICALDRIVE0";
        Assert.Equal(p, LongPathHelper.ToExtendedPath(p));
    }

    [Fact]
    public void ToExtendedPath_UncPath_ConvertedToUncLongForm()
    {
        Assert.Equal(@"\\?\UNC\server\share\dir",
            LongPathHelper.ToExtendedPath(@"\\server\share\dir"));
    }

    [Fact]
    public void ToExtendedPath_RelativePath_Unchanged()
    {
        // 相对路径无法安全加 \\?\;函数原样返回,让上层报错
        Assert.Equal(@"foo\bar", LongPathHelper.ToExtendedPath(@"foo\bar"));
    }

    [Fact]
    public void ToExtendedPathIfNeeded_ShortPath_Unchanged()
    {
        const string p = @"C:\foo";
        Assert.Equal(p, LongPathHelper.ToExtendedPathIfNeeded(p));
    }

    [Fact]
    public void ToExtendedPathIfNeeded_LongPath_Prefixed()
    {
        string longSegment = new('a', 260);
        string p = @"C:\foo\" + longSegment;
        string expected = @"\\?\" + p;
        Assert.Equal(expected, LongPathHelper.ToExtendedPathIfNeeded(p));
    }

    [Fact]
    public void ToExtendedPathIfNeeded_CustomThreshold_RespectsThreshold()
    {
        const string p = @"C:\foobar";
        // 阈值为 5,长度 8 应触发
        Assert.Equal(@"\\?\C:\foobar", LongPathHelper.ToExtendedPathIfNeeded(p, threshold: 5));
    }

    [Fact]
    public void StripExtendedPrefix_LocalLong_StripsBackToOriginal()
    {
        Assert.Equal(@"C:\foo\bar", LongPathHelper.StripExtendedPrefix(@"\\?\C:\foo\bar"));
    }

    [Fact]
    public void StripExtendedPrefix_UncLong_RestoresDoubleBackslash()
    {
        Assert.Equal(@"\\server\share\dir",
            LongPathHelper.StripExtendedPrefix(@"\\?\UNC\server\share\dir"));
    }

    [Fact]
    public void StripExtendedPrefix_NoPrefix_Unchanged()
    {
        const string p = @"C:\foo";
        Assert.Equal(p, LongPathHelper.StripExtendedPrefix(p));
    }

    [Fact]
    public void Roundtrip_ToExtendedThenStrip_RestoresOriginal()
    {
        const string p = @"C:\foo\bar";
        var ext = LongPathHelper.ToExtendedPath(p);
        var back = LongPathHelper.StripExtendedPrefix(ext);
        Assert.Equal(p, back);
    }

    [Fact]
    public void Roundtrip_Unc_ToExtendedThenStrip_RestoresOriginal()
    {
        const string p = @"\\server\share\dir";
        var ext = LongPathHelper.ToExtendedPath(p);
        var back = LongPathHelper.StripExtendedPrefix(ext);
        Assert.Equal(p, back);
    }
}
