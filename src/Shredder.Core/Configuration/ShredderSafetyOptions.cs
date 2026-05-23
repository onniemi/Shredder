namespace Shredder.Core.Configuration;

/// <summary>
/// 安全护栏:路径黑名单分级、二次确认、ADS/MFT/重解析点策略。
/// 任何破坏性默认值均偏保守(默认开启)。
/// </summary>
public sealed class ShredderSafetyOptions
{
    /// <summary>禁止粉碎的路径(精确匹配或前缀匹配 + 大小写不敏感)。</summary>
    public List<string> ForbiddenPaths { get; set; } = [];

    /// <summary>粉碎前需要弹出风险确认的路径。</summary>
    public List<string> WarnPaths { get; set; } = [];

    /// <summary>白名单(用户显式允许的高风险路径)。</summary>
    public List<string> AllowPaths { get; set; } = [];

    /// <summary>遇到符号链接 / Junction / 重解析点时是否拒绝。强烈建议保持 true。</summary>
    public bool RejectReparsePoints { get; set; } = true;

    /// <summary>枚举目录时是否解析符号链接的真实目标。</summary>
    public bool ResolveReparseTargets { get; set; } = true;

    /// <summary>是否枚举并粉碎 NTFS Alternate Data Streams。</summary>
    public bool ShredAlternateDataStreams { get; set; } = true;

    /// <summary>对 MFT 驻留小文件先膨胀再粉碎的阈值(字节)。</summary>
    public int MftResidentInflateThresholdBytes { get; set; } = 700;

    /// <summary>膨胀到的目标大小(字节),需大于簇大小。</summary>
    public int MftResidentInflateTargetBytes { get; set; } = 4096;

    /// <summary>是否启用 Restart Manager 解析文件占用。</summary>
    public bool UseRestartManagerForLockedFiles { get; set; } = true;

    /// <summary>是否允许文件正在被占用时安排重启删除(MoveFileEx)。</summary>
    public bool AllowScheduleOnRebootDelete { get; set; } = true;

    /// <summary>SSD 检测启用。</summary>
    public bool DetectSolidStateDrives { get; set; } = true;

    /// <summary>SSD 上自动改用 TRIM + 加密擦除路径(而非覆写)。</summary>
    public bool PreferTrimForSsd { get; set; } = true;
}
