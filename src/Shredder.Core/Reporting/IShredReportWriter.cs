namespace Shredder.Core.Reporting;

/// <summary>
/// 报告写入器抽象。实现负责把 <see cref="ShredReport"/> 持久化到磁盘并按配置打开。
/// </summary>
public interface IShredReportWriter
{
    /// <summary>
    /// 写入报告（按配置同时产生 JSON + HTML），返回主要产物（HTML 优先）的完整路径，
    /// 若 <c>Enabled=false</c> 或没有条目则返回 <c>null</c>。
    /// </summary>
    Task<string?> WriteAsync(ShredReport report, CancellationToken cancellationToken = default);
}
