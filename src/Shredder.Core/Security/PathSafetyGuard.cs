using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;

namespace Shredder.Core.Security;

/// <summary>
/// 路径级安全护栏:把粉碎请求分级为 <see cref="PathSafetyLevel.Forbidden"/> / <see cref="PathSafetyLevel.Warn"/>
/// / <see cref="PathSafetyLevel.Allowed"/>,调用方拿到结果后决定拦截或直接执行。
/// </summary>
/// <remarks>
/// 决策顺序(从硬到软):
/// <list type="number">
///   <item>盘符根目录(<c>C:\</c>) → 永远 Forbidden,不可被白名单覆盖</item>
///   <item>配置 ForbiddenPaths + 系统目录硬编码 → Forbidden,除非命中 AllowPaths</item>
///   <item>配置 WarnPaths(用户文档/桌面/下载 等)→ Warn</item>
///   <item>其它 → Allowed</item>
/// </list>
/// 路径比较前都会:<c>Path.GetFullPath</c> + 展开环境变量 + 大小写不敏感前缀匹配,
/// 末尾补 <c>\</c> 防止 <c>C:\WindowsX\</c> 被 <c>C:\Windows</c> 当成前缀命中。
/// </remarks>
public sealed class PathSafetyGuard
{
    public enum PathSafetyLevel
    {
        Allowed = 0,
        Warn = 1,
        Forbidden = 2,
    }

    public readonly record struct Decision(PathSafetyLevel Level, string Reason);

    private static readonly string[] HardcodedSystemRoots = BuildHardcodedRoots();

    private readonly ShredderSafetyOptions _options;

    public PathSafetyGuard(IOptions<ShredderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Safety;
    }

    /// <summary>对 <paramref name="path"/> 做安全分级。<paramref name="path"/> 可以是相对路径。</summary>
    public Decision Evaluate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new Decision(PathSafetyLevel.Forbidden, "路径为空。");

        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException ex) { return new Decision(PathSafetyLevel.Forbidden, $"路径无法规范化:{ex.Message}"); }
        catch (PathTooLongException ex) { return new Decision(PathSafetyLevel.Forbidden, $"路径过长:{ex.Message}"); }
        catch (System.Security.SecurityException ex) { return new Decision(PathSafetyLevel.Forbidden, $"无权限解析路径:{ex.Message}"); }
        catch (NotSupportedException ex) { return new Decision(PathSafetyLevel.Forbidden, $"路径格式不支持:{ex.Message}"); }

        // 1. 驱动器根目录硬拒(白名单也不行)
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
            return new Decision(PathSafetyLevel.Forbidden, "禁止直接粉碎整个驱动器根目录。");

        // 2. 用户白名单(可绕过配置 ForbiddenPaths,但绕不过根目录)
        if (Matches(full, _options.AllowPaths, out _))
            return new Decision(PathSafetyLevel.Allowed, "用户白名单。");

        // 3. 配置 ForbiddenPaths
        if (Matches(full, _options.ForbiddenPaths, out var blockedBy))
            return new Decision(PathSafetyLevel.Forbidden, $"命中配置黑名单:{blockedBy}");

        // 4. 硬编码系统目录(Windows / ProgramFiles / ProgramFilesX86)
        foreach (var sys in HardcodedSystemRoots)
        {
            if (PrefixMatch(full, sys))
                return new Decision(PathSafetyLevel.Forbidden, $"系统目录:{sys}");
        }

        // 5. 警告路径
        if (Matches(full, _options.WarnPaths, out var warnedBy))
            return new Decision(PathSafetyLevel.Warn, $"高风险路径:{warnedBy}");

        return new Decision(PathSafetyLevel.Allowed, string.Empty);
    }

    private static bool Matches(string full, IEnumerable<string> patterns, out string matched)
    {
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (string.IsNullOrWhiteSpace(expanded)) continue;
            try { expanded = Path.GetFullPath(expanded); }
            catch (ArgumentException) { continue; }
            catch (PathTooLongException) { continue; }
            catch (System.Security.SecurityException) { continue; }
            catch (NotSupportedException) { continue; }

            if (PrefixMatch(full, expanded))
            {
                matched = expanded;
                return true;
            }
        }
        matched = string.Empty;
        return false;
    }

    /// <summary>大小写不敏感的目录前缀匹配,末尾自动补 <c>\</c> 防止子串误判。</summary>
    private static bool PrefixMatch(string full, string prefix)
    {
        if (string.Equals(full, prefix, StringComparison.OrdinalIgnoreCase)) return true;
        var withSep = prefix.EndsWith(Path.DirectorySeparatorChar) ? prefix : prefix + Path.DirectorySeparatorChar;
        return full.StartsWith(withSep, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildHardcodedRoots()
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        };
        return list
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
