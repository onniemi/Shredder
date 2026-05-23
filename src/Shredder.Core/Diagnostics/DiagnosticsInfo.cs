namespace Shredder.Core.Diagnostics;

/// <summary>
/// 一次诊断采集的快照。所有字段都已脱敏(配置中的路径保留,但不含用户内容),
/// 用于让用户/支持人员排查问题时一键导出。
/// </summary>
public sealed record DiagnosticsInfo
{
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.Now;
    public string AppName { get; init; } = "Shredder";
    public string AppVersion { get; init; } = "0.0.0";
    public string BuildConfiguration { get; init; } = "Unknown";

    public string RuntimeVersion { get; init; } = string.Empty;
    public string OsVersion { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public bool IsElevated { get; init; }
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string OsArchitecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public long WorkingSetBytes { get; init; }

    public IReadOnlyList<DiagnosticsDrive> Drives { get; init; } = Array.Empty<DiagnosticsDrive>();
    public IReadOnlyList<DiagnosticsAlgorithm> Algorithms { get; init; } = Array.Empty<DiagnosticsAlgorithm>();
    public DiagnosticsOptionsSnapshot Options { get; init; } = new();

    public string ReportId { get; init; } = Guid.NewGuid().ToString("N").Substring(0, 8);
}

/// <summary>采集到的某个卷信息。</summary>
public sealed record DiagnosticsDrive(
    string Name,
    string Format,
    string Type,
    string Profile,
    bool TrimEnabled,
    long TotalSizeBytes,
    long AvailableFreeSpaceBytes,
    bool IsReady);

/// <summary>采集到的某个已注册算法。</summary>
public sealed record DiagnosticsAlgorithm(string Id, string Name, int PassCount);

/// <summary>
/// 脱敏后的配置快照。只保留对排错有意义的字段,绝不写入用户内容。
/// </summary>
public sealed record DiagnosticsOptionsSnapshot
{
    public int IoBufferSizeBytes { get; init; }
    public int IoMaxConcurrentFiles { get; init; }
    public int IoProgressReportIntervalMs { get; init; }
    public string AlgorithmDefault { get; init; } = string.Empty;
    public int FreeSpaceBlockSizeBytes { get; init; }
    public bool ReportingEnabled { get; init; }
    public bool ReportingFormatJson { get; init; }
    public bool ReportingFormatHtml { get; init; }
    public string ReportingOutputDirectory { get; init; } = string.Empty;
    public string UiConfirmationKeyword { get; init; } = string.Empty;
    public int SafetyMftResidentInflateThresholdBytes { get; init; }
    public int SafetyMftResidentInflateTargetBytes { get; init; }
    public bool LoggingRecordRawPaths { get; init; }
    public string LoggingOutputDirectory { get; init; } = string.Empty;
    public bool LoggingFileSinkEnabled { get; init; }
    public long LoggingFileSizeLimitBytes { get; init; }
    public int LoggingRetainedFileCountLimit { get; init; }
}
