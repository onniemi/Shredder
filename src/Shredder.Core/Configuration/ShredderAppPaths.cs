using System.IO;

namespace Shredder.Core.Configuration;

/// <summary>
/// 软件自身产生的配置、日志、报告、诊断包统一放在程序目录下。
/// </summary>
public static class ShredderAppPaths
{
    public static string AppDirectory => AppContext.BaseDirectory;

    public static string DataDirectory => Path.Combine(AppDirectory, "data");

    public static string ReportsDirectory => Path.Combine(DataDirectory, "reports");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string DiagnosticsDirectory => Path.Combine(DataDirectory, "diagnostics");

    public static string SettingsPath => Path.Combine(AppDirectory, "appsettings.json");

    public static string ResolveDirectory(string? configuredPath, string fallbackRelativePath)
    {
        var raw = string.IsNullOrWhiteSpace(configuredPath) ? fallbackRelativePath : configuredPath;
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        return Path.GetFullPath(Path.IsPathFullyQualified(expanded)
            ? expanded
            : Path.Combine(AppDirectory, expanded));
    }
}
