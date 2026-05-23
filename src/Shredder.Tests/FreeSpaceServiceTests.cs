using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.FileSystem;
using Shredder.Core.Models;
using Shredder.Core.Services;
using Xunit;

namespace Shredder.Tests;

/// <summary>
/// 验证 <see cref="FreeSpaceService"/> 的 SSD 路由与 MinimumFreeBytesBuffer 兜底:
/// SSD + FallbackToTrim=true  → 调 TrimFallbackRunner;
/// SSD + FallbackToTrim=false → SkippedSsdNoFallback;
/// HDD / Unknown              → OverwriteCompleted,并通过把 reserve 设到比盘还大来让循环立即退出,
///                              避免单测真的把临时盘写满。
/// </summary>
public class FreeSpaceServiceTests
{
    [Fact]
    public async Task SolidState_WithTrimFallback_InvokesRunner()
    {
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.SolidState, trim: true);
        var runner = new FakeTrimFallbackRunner(
            new TrimFallbackResult(ExitCode: 0, StandardOutput: "Retrim OK", StandardError: string.Empty));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions
        {
            DisableOnSsd = true,
            FallbackToTrimOnSsd = true,
        });

        var drive = MakeProbeDir();
        try
        {
            var result = await svc.WipeAsync(drive, progress: null, CancellationToken.None);

            Assert.Equal(FreeSpaceWipeOutcome.TrimFallbackInvoked, result.Outcome);
            Assert.Equal(0, result.BytesWritten);
            Assert.True(runner.Called, "FallbackToTrimOnSsd=true 时必须调用 TrimFallbackRunner。");
            Assert.False(File.Exists(Path.Combine(drive, "~shred_freespace.tmp")),
                "SSD fallback 路径不应留下任何临时文件。");
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task SolidState_WithoutTrimFallback_SkipsSilently()
    {
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.SolidState, trim: false);
        var runner = new FakeTrimFallbackRunner(new TrimFallbackResult(0, string.Empty, string.Empty));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions
        {
            DisableOnSsd = true,
            FallbackToTrimOnSsd = false,
        });

        var drive = MakeProbeDir();
        try
        {
            var result = await svc.WipeAsync(drive, progress: null, CancellationToken.None);

            Assert.Equal(FreeSpaceWipeOutcome.SkippedSsdNoFallback, result.Outcome);
            Assert.Equal(0, result.BytesWritten);
            Assert.False(runner.Called, "FallbackToTrimOnSsd=false 时不应再去碰 defrag。");
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task SolidState_UserDisablesProtection_FallsThroughToOverwrite()
    {
        // 用户显式 DisableOnSsd=false 时,即便是 SSD 也按 HDD 覆写路径走(用户自己承担伤寿命)
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.SolidState, trim: true);
        var runner = new FakeTrimFallbackRunner(new TrimFallbackResult(0, string.Empty, string.Empty));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions
        {
            DisableOnSsd = false,
            FallbackToTrimOnSsd = true, // 即使开启也不该被使用,因为压根没进 SSD 分支
            BlockSizeBytes = 4096,
            MinimumFreeBytesBuffer = long.MaxValue, // 让 reserve 远大于剩余空间,首块就退出
        });

        var drive = MakeProbeDir();
        try
        {
            var result = await svc.WipeAsync(drive, progress: null, CancellationToken.None);

            Assert.Equal(FreeSpaceWipeOutcome.OverwriteCompleted, result.Outcome);
            Assert.Equal(0, result.BytesWritten);
            Assert.False(runner.Called, "DisableOnSsd=false 时 TRIM fallback 不应被触发。");
            AssertNoLeftoverTempFiles(drive);
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task HardDisk_RunsOverwritePass_AndDeletesTempFile()
    {
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.HardDisk, trim: false);
        var runner = new FakeTrimFallbackRunner(new TrimFallbackResult(0, string.Empty, string.Empty));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions
        {
            DisableOnSsd = true,
            FallbackToTrimOnSsd = true,
            BlockSizeBytes = 4096,
            // MinimumFreeBytesBuffer 设到比当前盘剩余还大,首块 polling 就会退出
            // 这样 OverwriteCompleted 分支被覆盖但不会真把测试盘灌满
            MinimumFreeBytesBuffer = long.MaxValue,
        });

        var drive = MakeProbeDir();
        try
        {
            var result = await svc.WipeAsync(drive, progress: null, CancellationToken.None);

            Assert.Equal(FreeSpaceWipeOutcome.OverwriteCompleted, result.Outcome);
            Assert.Equal(0, result.BytesWritten);
            Assert.False(runner.Called, "HDD 路径下绝不应调用 TRIM。");
            AssertNoLeftoverTempFiles(drive);
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownProfile_TreatedAsHardDisk()
    {
        // 探测不出来时按 Unknown 走 HDD 覆写路径(避免因探测失败拒绝擦除)
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.Unknown, trim: false);
        var runner = new FakeTrimFallbackRunner(new TrimFallbackResult(0, string.Empty, string.Empty));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions
        {
            DisableOnSsd = true,
            FallbackToTrimOnSsd = true,
            BlockSizeBytes = 4096,
            MinimumFreeBytesBuffer = long.MaxValue,
        });

        var drive = MakeProbeDir();
        try
        {
            var result = await svc.WipeAsync(drive, progress: null, CancellationToken.None);

            Assert.Equal(FreeSpaceWipeOutcome.OverwriteCompleted, result.Outcome);
            Assert.False(runner.Called);
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task TempFileName_IsUnique_PerInvocation()
    {
        // 防御回归:历史上临时文件叫固定的 ~shred_freespace.tmp,导致同卷并发 WipeAsync
        // 会在第二个任务的 FileMode.Create + FileShare.None 上抛 IOException。
        // 现在改成 ~shred_freespace_{guid}.tmp,这里用并发跑 5 个 WipeAsync 做证据:
        // 只要文件名唯一,5 个任务都应顺利拿到 OverwriteCompleted,清理后无残留临时文件。
        // (FileSystemWatcher 观察法在 long.MaxValue buffer 导致临时文件只存活几十微秒时不稳定,故不采用。)
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.HardDisk, trim: false);
        var runner = new FakeTrimFallbackRunner(new TrimFallbackResult(0, string.Empty, string.Empty));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions
        {
            DisableOnSsd = true,
            FallbackToTrimOnSsd = true,
            BlockSizeBytes = 4096,
            MinimumFreeBytesBuffer = long.MaxValue,
        });

        var drive = MakeProbeDir();
        try
        {
            var tasks = Enumerable.Range(0, 5)
                .Select(_ => Task.Run(() => svc.WipeAsync(drive, progress: null, CancellationToken.None)))
                .ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.Equal(FreeSpaceWipeOutcome.OverwriteCompleted, r.Outcome));
            AssertNoLeftoverTempFiles(drive);
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task TrimFallback_ReportsNonZeroExitCode_InMessage()
    {
        // defrag 返回非 0 时,WipeAsync 仍然结束、不抛,但 Message 必须把 stderr/退出码传出来
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.SolidState, trim: true);
        var runner = new FakeTrimFallbackRunner(
            new TrimFallbackResult(ExitCode: 5, StandardOutput: string.Empty, StandardError: "卷不支持 TRIM"));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions
        {
            DisableOnSsd = true,
            FallbackToTrimOnSsd = true,
        });

        var drive = MakeProbeDir();
        try
        {
            var result = await svc.WipeAsync(drive, progress: null, CancellationToken.None);

            Assert.Equal(FreeSpaceWipeOutcome.TrimFallbackInvoked, result.Outcome);
            Assert.Contains("5", result.Message);
            Assert.Contains("卷不支持 TRIM", result.Message);
        }
        finally
        {
            Directory.Delete(drive, recursive: true);
        }
    }

    [Fact]
    public async Task WipeAsync_NonExistingDirectory_Throws()
    {
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.HardDisk, trim: false);
        var runner = new FakeTrimFallbackRunner(new TrimFallbackResult(0, string.Empty, string.Empty));
        var svc = CreateService(detector, runner, freeSpace: new ShredderFreeSpaceOptions());

        var bogus = Path.Combine(Path.GetTempPath(), "shredder-nonexistent-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => svc.WipeAsync(bogus, progress: null, CancellationToken.None));
    }

    private static FreeSpaceService CreateService(
        SsdDetector detector,
        TrimFallbackRunner runner,
        ShredderFreeSpaceOptions freeSpace)
    {
        var options = Options.Create(new ShredderOptions
        {
            FreeSpace = freeSpace,
        });
        return new FreeSpaceService(
            options,
            detector,
            runner,
            NullLogger<FreeSpaceService>.Instance);
    }

    private static string MakeProbeDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "shredder-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertNoLeftoverTempFiles(string drive)
    {
        var leftovers = Directory.EnumerateFiles(drive, "~shred_freespace_*.tmp").ToArray();
        Assert.True(leftovers.Length == 0,
            $"WipeAsync 完成后必须清理临时文件,但发现:{string.Join(", ", leftovers)}");
    }

    private sealed class FakeSsdDetector : SsdDetector
    {
        private readonly DeviceProfile _profile;
        private readonly bool _trim;

        public FakeSsdDetector(DeviceProfile profile, bool trim)
        {
            _profile = profile;
            _trim = trim;
        }

        public override StorageInfo Probe(string anyPathOnVolume) => new(_profile, _trim);
    }

    private sealed class FakeTrimFallbackRunner : TrimFallbackRunner
    {
        private readonly TrimFallbackResult _result;
        public bool Called { get; private set; }

        public FakeTrimFallbackRunner(TrimFallbackResult result) { _result = result; }

        public override Task<TrimFallbackResult> RunAsync(
            string driveRoot,
            ILogger? logger,
            CancellationToken ct)
        {
            Called = true;
            return Task.FromResult(_result);
        }
    }
}
