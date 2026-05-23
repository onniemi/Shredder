using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Shredder.Core.Diagnostics;

/// <summary>
/// 路径脱敏:对原始路径做 SHA-256,取前 8 个十六进制字符,产出 <c>[hash:xxxxxxxx]</c>
/// 形式的占位符。若路径包含文件扩展名(且不属于纯目录路径),会保留扩展名以便排错。
/// </summary>
/// <remarks>
/// 此哈希结果"无法还原原路径",但相同输入会产生相同输出,因此可用于在日志中关联同一文件
/// 的多条记录(写入/失败/重试),却不暴露真实路径。仅当 <c>Shredder:Logging:RecordRawPaths</c>
/// 为 <c>false</c>(默认)时,日志富化器会使用此工具。
/// </remarks>
public static class PathHasher
{
    private const int HashHexLength = 8;
    private static readonly SearchValues<char> s_pathSeparators = SearchValues.Create(['/', '\\']);

    /// <summary>
    /// 将 <paramref name="path"/> 转换为脱敏后的标签。空或空白输入按原样返回。
    /// </summary>
    public static string Hash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;

        var trimmed = path.Trim();

        // 提取扩展名:仅当最后一段含点号且不是纯目录(不以 / \ 结尾)时
        var ext = string.Empty;
        if (!trimmed.EndsWith('/') && !trimmed.EndsWith('\\'))
        {
            var lastSep = trimmed.AsSpan().LastIndexOfAny(s_pathSeparators);
            var lastSegment = lastSep >= 0 ? trimmed[(lastSep + 1)..] : trimmed;
            var dot = lastSegment.LastIndexOf('.');
            if (dot > 0 && dot < lastSegment.Length - 1)
            {
                ext = lastSegment[dot..];
                if (ext.Length > 16) ext = string.Empty; // 异常长的扩展名,放弃保留
            }
        }

        var bytes = Encoding.UTF8.GetBytes(trimmed);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(HashHexLength + 2 + ext.Length + 8);
        sb.Append("[hash:");
        for (var i = 0; i < HashHexLength / 2; i++)
        {
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        if (ext.Length > 0) sb.Append(ext);
        return sb.ToString();
    }
}
