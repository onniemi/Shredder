using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Xunit;

namespace Shredder.Tests;

public class DefaultConfigurationTests
{
    [Fact]
    public void DefaultAlgorithm_UsesSevenPassForStrongerFileShredding()
    {
        var defaults = ShredderDefaultConfiguration.Create();

        Assert.Equal(ShredAlgorithmIds.Purge7Pass, defaults["Shredder:Algorithm:Default"]);
        Assert.Equal(ShredAlgorithmIds.Purge7Pass, new ShredderAlgorithmOptions().Default);
    }
}
