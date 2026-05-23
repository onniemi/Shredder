using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Shredder.Core.Native;

namespace Shredder.Core.FileSystem;

/// <summary>
/// 通过 Windows Restart Manager 找出占用指定文件的进程列表。
/// 用于「文件被占用无法粉碎」时给用户提供可操作的诊断信息(谁在占,是否可重启)。
/// </summary>
/// <remarks>
/// 本类只查询,不强杀进程。结果展示在 UI 上,由用户决定是关闭进程、跳过文件,
/// 还是走「重启后删除」(MoveFileEx + MOVEFILE_DELAY_UNTIL_REBOOT)的路径。
/// </remarks>
public sealed class FileLockResolver
{
    /// <summary>占用方类型。映射自 Restart Manager 的 <c>RM_APP_TYPE</c>。</summary>
    public enum AppType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 1000,
    }

    /// <summary>占用文件的进程描述。</summary>
    public readonly record struct LockingProcess(
        int ProcessId,
        string AppName,
        AppType Type,
        bool IsRestartable);

    /// <summary>
    /// 查询占用 <paramref name="filePath"/> 的进程。
    /// 若 RM 会话创建失败或文件未被占用,返回空集合;不会抛异常。
    /// </summary>
    [SuppressMessage("Performance", "CA1822", Justification = "Registered as DI singleton; keeping instance method for future logger injection.")]
    public IReadOnlyList<LockingProcess> GetLockingProcesses(string filePath)
    {
        var result = new List<LockingProcess>();
        if (string.IsNullOrEmpty(filePath)) return result;

        // RM 会话 key 必须 ≤ CCH_RM_SESSION_KEY(32),且为 GUID-like 字符串
        string sessionKey = Guid.NewGuid().ToString("N")[..NativeMethods.CCH_RM_SESSION_KEY];
        int rc = NativeMethods.RmStartSession(out uint session, 0, sessionKey);
        if (rc != NativeMethods.ERROR_SUCCESS) return result;

        try
        {
            string[] files = { filePath };
            rc = NativeMethods.RmRegisterResources(
                session,
                (uint)files.Length, files,
                0, null,
                0, null);
            if (rc != NativeMethods.ERROR_SUCCESS) return result;

            // 先调一次拿所需 buffer 大小
            uint pnProcInfo = 0;
            uint lpdwRebootReasons = 0; // RmRebootReasonNone

            rc = NativeMethods.RmGetList(
                session,
                out uint pnProcInfoNeeded,
                ref pnProcInfo,
                null,
                ref lpdwRebootReasons);

            if (rc == NativeMethods.ERROR_SUCCESS) return result; // 没人占用
            if (rc != NativeMethods.ERROR_MORE_DATA) return result;

            var processes = new NativeMethods.RM_PROCESS_INFO[pnProcInfoNeeded];
            pnProcInfo = pnProcInfoNeeded;

            rc = NativeMethods.RmGetList(
                session,
                out pnProcInfoNeeded,
                ref pnProcInfo,
                processes,
                ref lpdwRebootReasons);

            if (rc != NativeMethods.ERROR_SUCCESS) return result;

            for (int i = 0; i < pnProcInfo; i++)
            {
                var p = processes[i];
                result.Add(new LockingProcess(
                    p.Process.dwProcessId,
                    p.strAppName ?? string.Empty,
                    MapAppType(p.ApplicationType),
                    p.bRestartable));
            }
        }
        finally
        {
            // RmEndSession 的返回码本质上无法处理:会话已无用,失败也只能记日志。
            _ = NativeMethods.RmEndSession(session);
        }

        return result;
    }

    private static AppType MapAppType(NativeMethods.RM_APP_TYPE t) => t switch
    {
        NativeMethods.RM_APP_TYPE.RmMainWindow => AppType.MainWindow,
        NativeMethods.RM_APP_TYPE.RmOtherWindow => AppType.OtherWindow,
        NativeMethods.RM_APP_TYPE.RmService => AppType.Service,
        NativeMethods.RM_APP_TYPE.RmExplorer => AppType.Explorer,
        NativeMethods.RM_APP_TYPE.RmConsole => AppType.Console,
        NativeMethods.RM_APP_TYPE.RmCritical => AppType.Critical,
        _ => AppType.Unknown,
    };
}
