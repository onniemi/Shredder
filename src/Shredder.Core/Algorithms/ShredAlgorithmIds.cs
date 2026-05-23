namespace Shredder.Core.Algorithms;

/// <summary>算法稳定标识。配置、序列化、UI 选择均以此为键,不要随便改值。</summary>
public static class ShredAlgorithmIds
{
    public const string Clear        = "Clear";
    public const string Purge3Pass   = "Purge-3Pass";
    public const string Purge7Pass   = "Purge-7Pass";
    public const string ZeroFill     = "ZeroFill";
    public const string CryptoErase  = "CryptoErase";
}
