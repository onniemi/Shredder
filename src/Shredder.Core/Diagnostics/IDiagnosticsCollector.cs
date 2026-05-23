namespace Shredder.Core.Diagnostics;

/// <summary>
/// 诊断信息采集器。汇总运行环境、卷、配置、已注册算法等,产出 <see cref="DiagnosticsInfo"/>。
/// </summary>
public interface IDiagnosticsCollector
{
    DiagnosticsInfo Collect();
}
