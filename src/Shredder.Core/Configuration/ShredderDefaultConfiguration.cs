namespace Shredder.Core.Configuration;

public static class ShredderDefaultConfiguration
{
    public static Dictionary<string, string?> Create()
    {
        return new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
            ["Serilog:MinimumLevel:Override:Microsoft.Hosting.Lifetime"] = "Information",
            ["Serilog:MinimumLevel:Override:System"] = "Warning",
            ["Serilog:MinimumLevel:Override:Shredder"] = "Debug",

            ["Shredder:Io:BufferSizeBytes"] = "4194304",
            ["Shredder:Io:UseUnbufferedIo"] = "true",
            ["Shredder:Io:FlushEveryNBuffers"] = "0",
            ["Shredder:Io:MaxConcurrentFiles"] = "1",
            ["Shredder:Io:ProgressReportIntervalMs"] = "200",

            ["Shredder:Safety:ForbiddenPaths:0"] = "%WINDIR%",
            ["Shredder:Safety:ForbiddenPaths:1"] = "%ProgramFiles%",
            ["Shredder:Safety:ForbiddenPaths:2"] = "%ProgramFiles(x86)%",
            ["Shredder:Safety:ForbiddenPaths:3"] = "%ProgramData%\\Microsoft",
            ["Shredder:Safety:ForbiddenPaths:4"] = "%SystemRoot%\\System32",
            ["Shredder:Safety:ForbiddenPaths:5"] = "%SystemDrive%\\Boot",
            ["Shredder:Safety:ForbiddenPaths:6"] = "%SystemDrive%\\System Volume Information",
            ["Shredder:Safety:ForbiddenPaths:7"] = "%SystemDrive%\\Recovery",
            ["Shredder:Safety:WarnPaths:0"] = "%USERPROFILE%",
            ["Shredder:Safety:WarnPaths:1"] = "%USERPROFILE%\\Documents",
            ["Shredder:Safety:WarnPaths:2"] = "%USERPROFILE%\\Desktop",
            ["Shredder:Safety:WarnPaths:3"] = "%LOCALAPPDATA%",
            ["Shredder:Safety:WarnPaths:4"] = "%APPDATA%",
            ["Shredder:Safety:RejectReparsePoints"] = "true",
            ["Shredder:Safety:ResolveReparseTargets"] = "true",
            ["Shredder:Safety:ShredAlternateDataStreams"] = "true",
            ["Shredder:Safety:MftResidentInflateThresholdBytes"] = "700",
            ["Shredder:Safety:MftResidentInflateTargetBytes"] = "4096",
            ["Shredder:Safety:UseRestartManagerForLockedFiles"] = "true",
            ["Shredder:Safety:AllowScheduleOnRebootDelete"] = "true",
            ["Shredder:Safety:DetectSolidStateDrives"] = "true",
            ["Shredder:Safety:PreferTrimForSsd"] = "true",

            ["Shredder:RecycleBin:ProcessAllDrives"] = "true",
            ["Shredder:RecycleBin:OverwriteContents"] = "true",
            ["Shredder:RecycleBin:CallShellEmptyAfterShred"] = "true",
            ["Shredder:FreeSpace:BlockSizeBytes"] = "67108864",
            ["Shredder:FreeSpace:MinimumFreeBytesBuffer"] = "268435456",
            ["Shredder:FreeSpace:ScrubMftSlack"] = "true",
            ["Shredder:FreeSpace:DisableOnSsd"] = "true",
            ["Shredder:FreeSpace:FallbackToTrimOnSsd"] = "true",
            ["Shredder:Algorithm:Default"] = "Purge-7Pass",
            ["Shredder:Algorithm:SsdDefault"] = "CryptoErase",
            ["Shredder:Ui:ConfirmationKeyword"] = "粉碎",
            ["Shredder:Ui:ConfirmationCooldownSeconds"] = "5",
            ["Shredder:Ui:Theme"] = "System",
            ["Shredder:Reporting:Enabled"] = "true",
            ["Shredder:Reporting:OutputDirectory"] = ShredderAppPaths.ReportsDirectory,
            ["Shredder:Reporting:FormatJson"] = "true",
            ["Shredder:Reporting:FormatHtml"] = "true",
            ["Shredder:Reporting:AutoOpen"] = "false",
            ["Shredder:Logging:RecordRawPaths"] = "false",
            ["Shredder:Logging:OutputDirectory"] = ShredderAppPaths.LogsDirectory,
            ["Shredder:Logging:FileSizeLimitBytes"] = "10485760",
            ["Shredder:Logging:RetainedFileCountLimit"] = "14",
            ["Shredder:Logging:FileSinkEnabled"] = "true",
        };
    }
}
