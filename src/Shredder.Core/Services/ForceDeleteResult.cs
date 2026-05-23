namespace Shredder.Core.Services;

public sealed class ForceDeleteResult
{
    public int DeletedFiles { get; internal set; }
    public int DeletedDirectories { get; internal set; }
    public int TerminatedProcesses { get; internal set; }
    public int ScheduledForReboot { get; internal set; }
}
