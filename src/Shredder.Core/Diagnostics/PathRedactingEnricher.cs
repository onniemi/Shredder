using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using Shredder.Core.Configuration;

namespace Shredder.Core.Diagnostics;

/// <summary>
/// Serilog 富化器:当 <see cref="ShredderLoggingOptions.RecordRawPaths"/> 为 <c>false</c>(默认)时,
/// 把日志事件里那些"看起来像路径"的字符串属性替换为 <c>[hash:xxxxxxxx]</c> 形式的脱敏值。
/// 通过 <see cref="IOptionsMonitor{T}"/> 监听配置热更新,可以在运行时切换开/关而无需重启。
/// </summary>
/// <remarks>
/// <para>设计取舍:</para>
/// <list type="bullet">
/// <item>采用富化器而非"改写所有调用点",可让 <see cref="Shredder.Core"/> 内已有的
/// <c>LogInformation("...{Path}...", path)</c> 调用站点自动获得隐私保护。</item>
/// <item>识别按"属性名"而非"值是否长得像路径",防止误伤其他字符串。</item>
/// <item>仅在 <see cref="LogEventPropertyValue"/> 是 <see cref="ScalarValue"/> 且其值是
/// 非空字符串时改写,避免破坏结构化对象。</item>
/// <item>本类型放在 <c>Shredder.Core.Diagnostics</c> 命名空间下,供 WPF 宿主与 CLI 宿主共用,
/// 避免在两个项目里维护两份。</item>
/// </list>
/// </remarks>
public sealed class PathRedactingEnricher : ILogEventEnricher
{
    private static readonly HashSet<string> PathProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path",
        "FilePath",
        "TargetPath",
        "SourcePath",
        "DestinationPath",
        "Source",
        "Destination",
        "Dir",
        "Directory",
        "Folder",
        "FullName",
        "FileName",
        "AdsPath",
    };

    private readonly IOptionsMonitor<ShredderOptions> _options;

    public PathRedactingEnricher(IOptionsMonitor<ShredderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        if (_options.CurrentValue.Logging.RecordRawPaths) return;

        // 复制键列表,避免在迭代时修改集合
        var keys = logEvent.Properties.Keys.ToArray();
        foreach (var key in keys)
        {
            if (!PathProperties.Contains(key)) continue;
            if (logEvent.Properties[key] is not ScalarValue { Value: string s } || s.Length == 0) continue;

            var redacted = PathHasher.Hash(s);
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(key, redacted));
        }
    }
}
