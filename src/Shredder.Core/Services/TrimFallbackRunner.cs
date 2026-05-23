using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Shredder.Core.Services;

/// <summary>
/// SSD 上空闲空间擦除的替代方案:调用 <c>defrag &lt;drive&gt; /L</c> 触发 ReTrim,
/// 把卷上未使用的 LBA 重新通知给 SSD,效果近似于物理擦除自由空间,且不写穿。
/// </summary>
/// <remarks>
/// <para>
/// 不在 <see cref="FreeSpaceService"/> 内部直接 spawn 进程,而是抽出成可注入的虚类,
/// 原因:① 单测里 fake 子类不依赖真实 defrag.exe;② 未来想换成 <c>fsutil behavior set DisableDeleteNotify 0 + defrag</c>
/// 这类更复杂的策略时,只需扩展子类。
/// </para>
/// <para>
/// 本类不验证调用者身份。defrag 的 ReTrim 不修改文件内容、仅向 SSD 重发 TRIM,
/// 但仍可能改变性能特征,因此调用方应在 <c>FallbackToTrimOnSsd=true</c> 时才使用。
/// </para>
/// </remarks>
public class TrimFallbackRunner
{
    /// <summary>
    /// 对给定卷执行 ReTrim。<paramref name="driveRoot"/> 可以是 <c>"D:\"</c> 或 <c>"D:"</c>,
    /// 大小写不敏感。返回包含退出码与标准输出 / 标准错误的结果对象,不抛异常(进程未启动除外)。
    /// </summary>
    [SuppressMessage("Performance", "CA1822", Justification = "Virtual so tests can override; intentionally instance API.")]
    public virtual async Task<TrimFallbackResult> RunAsync(
        string driveRoot,
        ILogger? logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);

        // defrag.exe 接受 "D:" 形式;给 "D:\" 也能 work,但为了一致剥掉末尾反斜杠
        string driveArg = driveRoot.TrimEnd('\\', '/');
        if (driveArg.Length == 0) driveArg = driveRoot;

        var psi = new ProcessStartInfo
        {
            FileName = "defrag.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(driveArg);
        psi.ArgumentList.Add("/L"); // ReTrim

        logger?.LogInformation("TrimFallback start: drive={Drive} cmd=defrag.exe {Args}", driveArg, "/L");

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                return new TrimFallbackResult(-1, string.Empty, "defrag.exe 启动失败");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 没装 defrag.exe(理论上每台 Windows 都有,但保留兜底)或权限不足
            logger?.LogWarning(ex, "TrimFallback spawn failed: drive={Drive}", driveArg);
            return new TrimFallbackResult(-1, string.Empty, ex.Message);
        }

        // 先 attach 异步读管道,避免 stdout/stderr 缓冲区填满导致 defrag 阻塞
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时尽量优雅地结束子进程,避免悬挂
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        logger?.LogInformation(
            "TrimFallback done: drive={Drive} exit={Exit}",
            driveArg, proc.ExitCode);
        return new TrimFallbackResult(proc.ExitCode, stdout, stderr);
    }
}

/// <summary>ReTrim 子进程的结果。<see cref="Success"/> 由 <see cref="ExitCode"/>=0 决定。</summary>
public sealed record TrimFallbackResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}
