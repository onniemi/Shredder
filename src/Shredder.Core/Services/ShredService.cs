using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Core.FileSystem;
using Shredder.Core.Models;
using Shredder.Core.Native;
using Shredder.Core.Security;

namespace Shredder.Core.Services;

/// <summary>
/// 编排单文件 / 目录的粉碎流程。
/// </summary>
/// <remarks>
/// 流程(单文件):
/// <list type="number">
///   <item>路径安全分级:<see cref="PathSafetyGuard"/> 判 Forbidden 直接拒绝</item>
///   <item>重解析点检测:若是符号链接 / Junction 且配置禁止,直接拒绝</item>
///   <item>属性备份:保存原 attrs(只读/隐藏/系统),失败时回滚</item>
///   <item>枚举 ADS,主流之外的每条流单独覆写并删除</item>
///   <item>若文件 ≤ MFT 阈值,先膨胀到目标大小,把数据搬出 MFT</item>
///   <item>调用算法覆写主流</item>
///   <item>截断、多次随机改名、删除</item>
/// </list>
/// </remarks>
public sealed class ShredService
{
    private const int MetadataRenamePasses = 7;
    private static readonly DateTime s_scrubbedFileTimeUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IShredAlgorithmRegistry _registry;
    private readonly ShredderOptions _options;
    private readonly PathSafetyGuard _pathGuard;
    private readonly MftResidencyHandler _mftHandler;
    private readonly FileLockResolver _lockResolver;
    private readonly SsdDetector _ssdDetector;
    private readonly ILogger<ShredService> _logger;

    public ShredService(
        IShredAlgorithmRegistry registry,
        IOptions<ShredderOptions> options,
        PathSafetyGuard pathGuard,
        MftResidencyHandler mftHandler,
        FileLockResolver lockResolver,
        SsdDetector ssdDetector,
        ILogger<ShredService> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pathGuard);
        ArgumentNullException.ThrowIfNull(mftHandler);
        ArgumentNullException.ThrowIfNull(lockResolver);
        ArgumentNullException.ThrowIfNull(ssdDetector);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _options = options.Value;
        _pathGuard = pathGuard;
        _mftHandler = mftHandler;
        _lockResolver = lockResolver;
        _ssdDetector = ssdDetector;
        _logger = logger;
    }

    /// <summary>仅供单元测试构造时使用，直接传一个算法实例。</summary>
    internal static ShredService CreateForTests(IShredAlgorithm algorithm, SsdDetector? ssdDetector = null)
        => CreateForTests(new[] { algorithm }, algorithm.Id, ssdDefault: null, ssdDetector);

    /// <summary>仅供单元测试构造,允许多算法注册以验证 ResolveAlgorithm 的路由分支。</summary>
    internal static ShredService CreateForTests(
        IEnumerable<IShredAlgorithm> algorithms,
        string defaultId,
        string? ssdDefault,
        SsdDetector? ssdDetector = null,
        int maxConcurrentFiles = 1)
    {
        var opts = Options.Create(new ShredderOptions
        {
            Algorithm = new ShredderAlgorithmOptions
            {
                Default = defaultId,
                SsdDefault = ssdDefault ?? string.Empty,
            },
            Io = new ShredderIoOptions
            {
                MaxConcurrentFiles = Math.Max(1, maxConcurrentFiles),
            },
        });
        return new ShredService(
            new ShredAlgorithmRegistry(algorithms),
            opts,
            new PathSafetyGuard(opts),
            new MftResidencyHandler(
                opts.Value.Safety.MftResidentInflateThresholdBytes,
                opts.Value.Safety.MftResidentInflateTargetBytes),
            new FileLockResolver(),
            ssdDetector ?? new SsdDetector(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShredService>.Instance);
    }

    public async Task ShredAsync(
        ShredJob job,
        IProgress<ShredProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsurePathAllowed(job.Path);

        var algorithm = ResolveAlgorithm(job);
        _logger.LogInformation(
            "Shred start: path={Path} dir={IsDir} size={Size} algo={Algo}",
            job.Path, job.IsDirectory, job.SizeBytes, algorithm.Id);

        job.Status = ShredJobStatus.Running;
        try
        {
            if (job.IsDirectory) await ShredDirectoryAsync(job.Path, algorithm, progress, ct);
            else                 await ShredFileAsync(job.Path, algorithm, progress, ct);
            job.Status = ShredJobStatus.Success;
            _logger.LogInformation("Shred success: path={Path}", job.Path);
        }
        catch (OperationCanceledException)
        {
            job.Status = ShredJobStatus.Cancelled;
            _logger.LogWarning("Shred cancelled: path={Path}", job.Path);
            throw;
        }
        catch (Exception ex)
        {
            job.Status = ShredJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Shred failed: path={Path}", job.Path);
            throw;
        }
    }

    private IShredAlgorithm ResolveAlgorithm(ShredJob job) => PreviewAlgorithm(job.Path, job.AlgorithmId);

    /// <summary>
    /// 给定路径与可选算法 ID,返回最终会用到的 <see cref="IShredAlgorithm"/>。
    /// 与真实粉碎流程共享同一套路由逻辑,但本身完全只读 —— dry-run / explain / UI 预检都可调用。
    /// </summary>
    /// <remarks>
    /// 路由优先级:
    /// <list type="number">
    ///   <item>用户显式 <paramref name="algorithmId"/>:严格命中 → 配置默认 → 注册表第一个;均无则抛。</item>
    ///   <item>未指定时按 <see cref="SsdDetector"/> 判介质:SSD 走 SsdDefault,否则走 Default。</item>
    ///   <item>探测异常(IO/权限)降级到默认算法,绝不因为探测崩了就拦截粉碎。</item>
    /// </list>
    /// </remarks>
    public IShredAlgorithm PreviewAlgorithm(string path, string? algorithmId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // 1. 用户显式指定算法时,严格服从,不再做 SSD 智能路由
        if (!string.IsNullOrWhiteSpace(algorithmId))
        {
            return _registry.Find(algorithmId)
                ?? _registry.Find(_options.Algorithm.Default)
                ?? FirstAvailable()
                ?? throw new InvalidOperationException("没有可用的粉碎算法。");
        }

        // 2. 未指定时,先按介质类型智能选择:SSD/NVMe/U 盘 → 加密擦除,HDD/未知 → 多次覆写
        string preferredId = _options.Algorithm.Default;
        try
        {
            var storage = _ssdDetector.Probe(path);
            if (storage.Profile == SsdDetector.DeviceProfile.SolidState
                && !string.IsNullOrWhiteSpace(_options.Algorithm.SsdDefault))
            {
                preferredId = _options.Algorithm.SsdDefault;
                _logger.LogInformation(
                    "SSD detected for {Path}; routing to algorithm={Algo} (TRIM enabled={Trim})",
                    path, preferredId, storage.TrimEnabled);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 探测失败时降级到默认算法,绝不因为探测崩了就拦截粉碎
            _logger.LogWarning(ex, "SSD detection failed for {Path}; falling back to default algorithm.", path);
        }

        return _registry.Find(preferredId)
            ?? _registry.Find(_options.Algorithm.Default)
            ?? FirstAvailable()
            ?? throw new InvalidOperationException("没有可用的粉碎算法。");

        IShredAlgorithm? FirstAvailable() => _registry.All.Count > 0 ? _registry.All[0] : null;
    }

    private async Task ShredDirectoryAsync(string dir, IShredAlgorithm algorithm, IProgress<ShredProgress>? p, CancellationToken ct)
    {
        // 目录本身也需要先过重解析点检测,避免顺着 Junction 把链外的真实目标删了
        if (_options.Safety.RejectReparsePoints && ReparsePointDetector.IsReparsePoint(dir))
            throw new InvalidOperationException($"拒绝粉碎重解析点(符号链接 / Junction):{dir}");

        // 后序遍历:先文件再目录。文件阶段按 MaxConcurrentFiles 受控并发(默认 1=串行),
        // 目录删除阶段强制串行(并发删父目录可能与正在删的子目录冲突)。
        int concurrency = ResolveFileConcurrency(algorithm);
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);

        if (concurrency == 1)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                await ShredFileAsync(file, algorithm, p, ct);
            }
        }
        else
        {
            // Parallel.ForEachAsync 的 cancellation 在任一任务抛出时会自动取消剩余分片,
            // 第一条异常会包成 AggregateException 但 await 时会自动 Unwrap 出原始异常。
            _logger.LogInformation(
                "Directory shred concurrency: dir={Dir} maxConcurrentFiles={Concurrency}",
                dir, concurrency);

            var parallelOpts = new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = ct,
            };
            await Parallel.ForEachAsync(files, parallelOpts, async (file, innerCt) =>
            {
                innerCt.ThrowIfCancellationRequested();
                await ShredFileAsync(file, algorithm, p, innerCt);
            });
        }

        // 子目录:按路径长度倒序确保先删叶子。即便上面并发,这里也始终串行——
        // 目录树删除并不是热点(I/O 成本远小于文件覆写),并行只会增加竞态风险。
        foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                                     .OrderByDescending(s => s.Length))
        {
            ct.ThrowIfCancellationRequested();
            TryRenameAndDeleteDir(sub);
        }
        TryRenameAndDeleteDir(dir);
    }

    private async Task ShredFileAsync(string path, IShredAlgorithm algorithm, IProgress<ShredProgress>? p, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;

        // 1. 重解析点检测
        if (_options.Safety.RejectReparsePoints && ReparsePointDetector.IsReparsePoint(path))
            throw new InvalidOperationException($"拒绝粉碎重解析点(符号链接 / Junction):{path}");

        // 2. 备份原始属性,失败时恢复(避免「清属性 → 写失败 → 文件变 Normal 残留」)
        uint? originalAttrs = ReparsePointDetector.TryGetAttributes(path);
        bool attrsCleared = false;
        try
        {
            if (originalAttrs.HasValue && (originalAttrs.Value & (uint)FileAttributes.Normal) != originalAttrs.Value)
            {
                ReparsePointDetector.SetAttributes(path, (uint)FileAttributes.Normal);
                attrsCleared = true;
            }

            var fastDelete = algorithm.Id.Equals(ShredAlgorithmIds.FastDelete, StringComparison.OrdinalIgnoreCase);

            if (!fastDelete)
            {
                // 3. ADS:先处理非主流(主流由后面的 fs 覆写)
                if (_options.Safety.ShredAlternateDataStreams)
                    await ShredAdsAsync(path, algorithm, p, ct);

                // 4. MFT 驻留小文件膨胀
                await InflateIfResidentResolvingLocksAsync(path, ct);
            }

            // 5. 主流覆写。快速粉碎跳过大文件覆写,只做截断、随机改名和删除。
            long length = new FileInfo(path).Length;
            if (length > 0 && !fastDelete)
            {
                var fs = OpenForExclusiveWriteResolvingLocks(path);

                await using (fs)
                {
                    await algorithm.ShredAsync(fs, length, path, p, ct);
                }
            }
            else if (fastDelete)
            {
                await EnsureExclusiveAccessForFastDeleteAsync(path, p, ct);
            }

            // 6. 截断到 0,清理时间戳,并多轮随机改名后删除。
            using (var trunc = new FileStream(path, FileMode.Truncate)) { }
            TryScrubFileTimestamps(path);
            var dir = Path.GetDirectoryName(path)!;
            string current = path;
            for (int i = 0; i < MetadataRenamePasses; i++)
            {
                string next = Path.Combine(dir, Guid.NewGuid().ToString("N"));
                File.Move(current, next);
                current = next;
                TryScrubFileTimestamps(current);
            }
            File.Delete(current);
        }
        catch
        {
            // 失败时尽量恢复原属性,避免留下属性被清的脏文件
            if (attrsCleared && originalAttrs.HasValue && File.Exists(path))
            {
                try { ReparsePointDetector.SetAttributes(path, originalAttrs.Value); }
                catch (Win32Exception) { /* 记日志不阻断 */ }
            }

            // 占用文件 + 允许重启删除时,排队到下次重启
            if (_options.Safety.AllowScheduleOnRebootDelete && File.Exists(path))
            {
                ScheduleDeleteOnReboot(path);
            }
            throw;
        }
    }

    internal int ResolveFileConcurrency(IShredAlgorithm algorithm)
    {
        int configured = Math.Max(1, _options.Io.MaxConcurrentFiles);
        if (!algorithm.Id.Equals(ShredAlgorithmIds.FastDelete, StringComparison.OrdinalIgnoreCase))
        {
            return configured;
        }

        if (configured <= 1) configured = 4;
        return Math.Clamp(configured, 1, 8);
    }

    private static FileStream OpenForExclusiveWrite(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

    private async Task EnsureExclusiveAccessForFastDeleteAsync(
        string path,
        IProgress<ShredProgress>? progress,
        CancellationToken ct)
    {
        await using var fs = OpenForExclusiveWriteResolvingLocks(path);
        progress?.Report(new ShredProgress(path, 0, 0, 0, fs.Length));
        await fs.FlushAsync(ct);
    }

    private async Task InflateIfResidentResolvingLocksAsync(string path, CancellationToken ct)
    {
        try
        {
            await _mftHandler.InflateIfResidentAsync(path, ct);
        }
        catch (Exception inflateEx) when (CanResolveLockedFile(inflateEx))
        {
            if (!TryReleaseLocksForPath(path, out var lockers))
            {
                if (lockers.Count > 0)
                {
                    throw BuildLockedFileException(path, lockers, inflateEx);
                }

                throw;
            }

            try
            {
                await _mftHandler.InflateIfResidentAsync(path, ct);
            }
            catch (Exception retryEx) when (IsFileLockOrAccessException(retryEx))
            {
                throw BuildLockedFileException(path, lockers, retryEx);
            }
        }
    }

    private FileStream OpenForExclusiveWriteResolvingLocks(string path)
    {
        try
        {
            return OpenForExclusiveWrite(path);
        }
        catch (Exception openEx) when (CanResolveLockedFile(openEx))
        {
            if (!TryReleaseLocksForPath(path, out var lockers))
            {
                if (lockers.Count > 0)
                {
                    throw BuildLockedFileException(path, lockers, openEx);
                }

                throw;
            }

            try
            {
                return OpenForExclusiveWrite(path);
            }
            catch (Exception retryEx) when (IsFileLockOrAccessException(retryEx))
            {
                throw BuildLockedFileException(path, lockers, retryEx);
            }
        }
    }

    private bool CanResolveLockedFile(Exception ex) =>
        _options.Safety.UseRestartManagerForLockedFiles && IsFileLockOrAccessException(ex);

    private bool TryReleaseLocksForPath(
        string path,
        out IReadOnlyList<FileLockResolver.LockingProcess> lockers)
    {
        lockers = _lockResolver.GetLockingProcesses(path);
        return lockers.Count > 0 && TryTerminateLockers(lockers);
    }

    private static bool IsFileLockOrAccessException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private bool TryTerminateLockers(IReadOnlyList<FileLockResolver.LockingProcess> lockers)
    {
        var currentPid = Environment.ProcessId;
        var candidates = lockers
            .Where(p => p.ProcessId != currentPid)
            .Where(IsSafeToTerminate)
            .Distinct()
            .ToArray();

        if (candidates.Length == 0) return false;

        var attempted = false;
        foreach (var locker in candidates)
        {
            try
            {
                using var process = Process.GetProcessById(locker.ProcessId);
                if (process.HasExited) continue;
                attempted = true;

                _logger.LogWarning(
                    "Closing locking process before shred: {AppName}({Pid}) type={Type}",
                    locker.AppName,
                    locker.ProcessId,
                    locker.Type);

                if (process.CloseMainWindow() && process.WaitForExit(1500))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to close locking process before shred: {AppName}({Pid})",
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

    private static IOException BuildLockedFileException(
        string path,
        IReadOnlyList<FileLockResolver.LockingProcess> lockers,
        Exception inner)
    {
        string who = string.Join(", ", lockers.Select(l => $"{l.AppName}(PID={l.ProcessId})"));
        return new IOException($"文件被以下进程占用,无法粉碎:{who}", inner);
    }

    private async Task ShredAdsAsync(string path, IShredAlgorithm algorithm, IProgress<ShredProgress>? p, CancellationToken ct)
    {
        var adsNames = AlternateDataStreamEnumerator.EnumerateAdsNames(path);
        foreach (var ads in adsNames)
        {
            ct.ThrowIfCancellationRequested();
            string adsPath = path + ads; // 形如 "C:\a.txt:Zone.Identifier"
            try
            {
                var adsInfo = new FileInfo(adsPath);
                if (!adsInfo.Exists || adsInfo.Length == 0)
                {
                    TryDeleteAds(adsPath);
                    continue;
                }
                await using (var fs = OpenForExclusiveWrite(adsPath))
                {
                    await algorithm.ShredAsync(fs, adsInfo.Length, adsPath, p, ct);
                }
                using (new FileStream(adsPath, FileMode.Truncate)) { }
                TryDeleteAds(adsPath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "ADS 处理失败,跳过:{AdsPath}", adsPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "ADS 处理无权限,跳过:{AdsPath}", adsPath);
            }
        }
    }

    private static void TryDeleteAds(string adsPath)
    {
        try { File.Delete(adsPath); }
        catch (IOException) { /* 某些 ADS 不能直接删,只能随主流一起销毁 */ }
        catch (UnauthorizedAccessException) { }
    }

    private static void ScheduleDeleteOnReboot(string path)
    {
        // MOVEFILE_DELAY_UNTIL_REBOOT 要求 lpNewFileName 为 null
        NativeMethods.MoveFileExW(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
    }

    private static void TryRenameAndDeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        var parent = Path.GetDirectoryName(dir)!;
        string current = dir;
        TryScrubDirectoryTimestamps(current);
        for (int i = 0; i < MetadataRenamePasses; i++)
        {
            var renamed = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.Move(current, renamed);
            current = renamed;
            TryScrubDirectoryTimestamps(current);
        }
        Directory.Delete(current, recursive: true);
    }

    private static void TryScrubFileTimestamps(string path)
    {
        try
        {
            File.SetCreationTimeUtc(path, s_scrubbedFileTimeUtc);
            File.SetLastAccessTimeUtc(path, s_scrubbedFileTimeUtc);
            File.SetLastWriteTimeUtc(path, s_scrubbedFileTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static void TryScrubDirectoryTimestamps(string path)
    {
        try
        {
            Directory.SetCreationTimeUtc(path, s_scrubbedFileTimeUtc);
            Directory.SetLastAccessTimeUtc(path, s_scrubbedFileTimeUtc);
            Directory.SetLastWriteTimeUtc(path, s_scrubbedFileTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private void EnsurePathAllowed(string path)
    {
        var decision = _pathGuard.Evaluate(path);
        if (decision.Level == PathSafetyGuard.PathSafetyLevel.Forbidden)
            throw new InvalidOperationException(decision.Reason);
        // Warn 只保留风险分级信息,不拦截执行。
    }
}
