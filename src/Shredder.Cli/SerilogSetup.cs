using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Shredder.Core.Configuration;
using Shredder.Core.Diagnostics;

namespace Shredder.Cli;

/// <summary>
/// CLI 的 Serilog 流水线配置。与 <c>Shredder.App.App.xaml.cs.ConfigureSerilog</c> 保持一致,
/// 共享同一份 <see cref="PathRedactingEnricher"/>(在 <c>Shredder.Core.Diagnostics</c>)。
/// 区别:
/// <list type="bullet">
///   <item>始终启用 Console sink(CLI 默认就是控制台用户)。</item>
///   <item>文件 sink 路径展开 <c>%LOCALAPPDATA%</c>,失败时静默降级。</item>
///   <item>不写 Debug sink(CLI 没必要)。</item>
/// </list>
/// </summary>
internal static class SerilogSetup
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "文件 sink 初始化失败必须降级:Console sink 仍然可用,不应阻挡 CLI 启动。")]
    public static void Configure(IConfiguration configuration, IServiceProvider sp, LoggerConfiguration lc)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(lc);

        var loggingOpts = sp.GetRequiredService<IOptions<ShredderOptions>>().Value.Logging;
        var enricher = sp.GetRequiredService<PathRedactingEnricher>();

        ApplyConfiguredMinimumLevel(configuration, lc);

        lc.Enrich.FromLogContext()
          .Enrich.WithMachineName()
          .Enrich.WithProcessId()
          .Enrich.WithThreadId()
          .Enrich.With(enricher);

        if (!loggingOpts.FileSinkEnabled) return;

        try
        {
            var dir = Environment.ExpandEnvironmentVariables(
                string.IsNullOrWhiteSpace(loggingOpts.OutputDirectory)
                    ? "%LOCALAPPDATA%\\Shredder\\Logs"
                    : loggingOpts.OutputDirectory);
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "shredder-cli-.log");

            lc.WriteTo.File(
                path: path,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: loggingOpts.FileSizeLimitBytes,
                retainedFileCountLimit: loggingOpts.RetainedFileCountLimit,
                shared: false,
                buffered: false,
                outputTemplate: OutputTemplate);
        }
        catch
        {
            // 文件 sink 初始化失败:Console sink 仍可工作,不阻挡 CLI 启动
        }
    }

    private static void ApplyConfiguredMinimumLevel(IConfiguration configuration, LoggerConfiguration lc)
    {
        var section = configuration.GetSection("Serilog:MinimumLevel");
        lc.MinimumLevel.Is(ParseLogEventLevel(section["Default"]) ?? LogEventLevel.Information);

        foreach (var item in section.GetSection("Override").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(item.Key)) continue;
            var level = ParseLogEventLevel(item.Value);
            if (level is not null)
            {
                lc.MinimumLevel.Override(item.Key, level.Value);
            }
        }
    }

    private static LogEventLevel? ParseLogEventLevel(string? value)
    {
        return Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : null;
    }
}
