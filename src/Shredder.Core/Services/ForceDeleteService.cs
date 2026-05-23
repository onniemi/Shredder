using System.ComponentModel;
using System.Diagnostics;
using Shredder.Core.Configuration;
using Shredder.Core.FileSystem;
using Shredder.Core.Models;
using Shredder.Core.Native;
using Shredder.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shredder.Core.Services;

public sealed class ForceDeleteService
{
    private const uint AttributeBlockers =
        (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

    private readonly ShredderOptions _options;
    private readonly PathSafetyGuard _pathGuard;
    private readonly FileLockResolver _lockResolver;
    private readonly ILogger<ForceDeleteService> _logger;

    public ForceDeleteService(
        IOptions<ShredderOptions> options,
        PathSafetyGuard pathGuard,
        FileLockResolver lockResolver,
        ILogger<ForceDeleteService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pathGuard);
        ArgumentNullException.ThrowIfNull(lockResolver);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _pathGuard = pathGuard;
        _lockResolver = lockResolver;
        _logger = logger;
    }

    public async Task<ForceDeleteResult> DeleteAsync(ShredJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsurePathAllowed(job.Path);

        job.Status = ShredJobStatus.Running;
        try
        {
            var result = await Task.Run(() => DeleteCore(job, ct), ct).ConfigureAwait(false);
            job.Status = ShredJobStatus.Success;
            return result;
        }
        catch (OperationCanceledException)
        {
            job.Status = ShredJobStatus.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            job.Status = ShredJobStatus.Failed;
            job.ErrorMessage = BuildFailureMessage(ex);
            _logger.LogError(ex, "Force delete failed: {Path}", job.Path);
            throw;
        }
    }

    private ForceDeleteResult DeleteCore(ShredJob job, CancellationToken ct)
    {
        var result = new ForceDeleteResult();
        if (job.IsDirectory)
        {
            DeleteDirectory(job.Path, result, ct);
        }
        else
        {
            DeleteFile(job.Path, result, ct);
        }

        return result;
    }

    private void DeleteDirectory(string root, ForceDeleteResult result, CancellationToken ct)
    {
        if (!Directory.Exists(root)) return;
        RejectReparsePointIfNeeded(root);

        var directories = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            RejectReparsePointIfNeeded(dir);
            TryClearDeleteBlockingAttributes(dir);
            directories.Add(dir);

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                DeleteFile(file, result, ct);
            }

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                RejectReparsePointIfNeeded(subDir);
                stack.Push(subDir);
            }
        }

        foreach (var dir in directories.OrderByDescending(static d => d.Length))
        {
            ct.ThrowIfCancellationRequested();
            DeleteEmptyDirectory(dir, result);
        }
    }

    private void DeleteFile(string path, ForceDeleteResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return;
        RejectReparsePointIfNeeded(path);
        TryClearDeleteBlockingAttributes(path);

        try
        {
            File.Delete(path);
            result.DeletedFiles++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (TryReleaseLocksAndDeleteFile(path, result, ct)) return;
            if (TryScheduleDeleteOnReboot(path, result)) return;
            throw;
        }
    }

    private void DeleteEmptyDirectory(string path, ForceDeleteResult result)
    {
        if (!Directory.Exists(path)) return;
        TryClearDeleteBlockingAttributes(path);

        try
        {
            Directory.Delete(path, recursive: false);
            result.DeletedDirectories++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (TryReleaseLocksAndDeleteDirectory(path, result)) return;
            if (TryScheduleDeleteOnReboot(path, result)) return;
            throw;
        }
    }

    private bool TryReleaseLocksAndDeleteFile(string path, ForceDeleteResult result, CancellationToken ct)
    {
        if (!_options.Safety.UseRestartManagerForLockedFiles) return false;

        var lockers = _lockResolver.GetLockingProcesses(path);
        if (!TryTerminateLockers(lockers, result)) return false;

        ct.ThrowIfCancellationRequested();
        try
        {
            TryClearDeleteBlockingAttributes(path);
            File.Delete(path);
            result.DeletedFiles++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryReleaseLocksAndDeleteDirectory(string path, ForceDeleteResult result)
    {
        if (!_options.Safety.UseRestartManagerForLockedFiles) return false;

        var files = EnumerateFilesForLockScan(path);
        var lockers = files.SelectMany(_lockResolver.GetLockingProcesses).Distinct().ToArray();
        if (!TryTerminateLockers(lockers, result)) return false;

        try
        {
            TryClearDeleteBlockingAttributes(path);
            Directory.Delete(path, recursive: false);
            result.DeletedDirectories++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryTerminateLockers(IEnumerable<FileLockResolver.LockingProcess> lockers, ForceDeleteResult result)
    {
        var candidates = lockers.Where(IsSafeToTerminate).Distinct().ToArray();
        if (candidates.Length == 0) return false;

        var attempted = false;
        foreach (var locker in candidates)
        {
            try
            {
                using var process = Process.GetProcessById(locker.ProcessId);
                if (process.HasExited) continue;
                attempted = true;

                if (process.CloseMainWindow() && process.WaitForExit(1500))
                {
                    result.TerminatedProcesses++;
                    continue;
                }

                process.Kill(entireProcessTree: true);
                if (process.WaitForExit(3000))
                {
                    result.TerminatedProcesses++;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to terminate locking process: {AppName}({Pid})",
                    locker.AppName,
                    locker.ProcessId);
            }
        }

        return attempted;
    }

    private static bool IsSafeToTerminate(FileLockResolver.LockingProcess process)
    {
        if (process.ProcessId <= 4) return false;
        return process.Type is
            FileLockResolver.AppType.MainWindow or
            FileLockResolver.AppType.OtherWindow or
            FileLockResolver.AppType.Console or
            FileLockResolver.AppType.Unknown;
    }

    private static string[] EnumerateFilesForLockScan(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private void RejectReparsePointIfNeeded(string path)
    {
        if (_options.Safety.RejectReparsePoints && ReparsePointDetector.IsReparsePoint(path))
        {
            throw new InvalidOperationException($"拒绝删除重解析点(符号链接 / Junction):{path}");
        }
    }

    private static void TryClearDeleteBlockingAttributes(string path)
    {
        var attrs = ReparsePointDetector.TryGetAttributes(path);
        if (!attrs.HasValue || (attrs.Value & AttributeBlockers) == 0) return;

        var cleaned = attrs.Value & ~AttributeBlockers;
        if (cleaned == 0) cleaned = NativeMethods.FILE_ATTRIBUTE_NORMAL;

        try
        {
            ReparsePointDetector.SetAttributes(path, cleaned);
        }
        catch (Win32Exception)
        {
            // Delete will surface the real failure if the attribute change mattered.
        }
    }

    private bool TryScheduleDeleteOnReboot(string path, ForceDeleteResult result)
    {
        if (!_options.Safety.AllowScheduleOnRebootDelete) return false;

        var target = LongPathHelper.ToExtendedPathIfNeeded(path);
        if (!NativeMethods.MoveFileExW(target, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
        {
            return false;
        }

        result.ScheduledForReboot++;
        _logger.LogWarning("Path scheduled for delete on reboot: {Path}", path);
        return true;
    }

    private void EnsurePathAllowed(string path)
    {
        var decision = _pathGuard.Evaluate(path);
        if (decision.Level == PathSafetyGuard.PathSafetyLevel.Forbidden)
        {
            throw new InvalidOperationException(decision.Reason);
        }
    }

    private static string BuildFailureMessage(Exception ex)
    {
        var message = ex.Message;
        if (ex is UnauthorizedAccessException)
        {
            return message + "；可能需要以管理员身份运行，或文件正在被更高权限/系统进程占用。";
        }

        if (ex is IOException)
        {
            return message + "；文件可能仍被进程占用。如果占用进程是管理员权限，请以管理员身份运行后重试。";
        }

        return message;
    }
}
