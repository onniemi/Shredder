namespace Shredder.Core.Configuration;

public sealed class ShredderAlgorithmOptions
{
    /// <summary>默认算法标识。Clear / Purge-3Pass / Purge-7Pass / CryptoErase。</summary>
    public string Default { get; set; } = "Purge-3Pass";

    /// <summary>SSD 上的默认算法。</summary>
    public string SsdDefault { get; set; } = "CryptoErase";
}
