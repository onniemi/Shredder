using Shredder.Core.Algorithms;
using Shredder.Core.FileSystem;
using Shredder.Core.Models;
using Shredder.Core.Services;
using Xunit;

namespace Shredder.Tests;

/// <summary>
/// 验证 <see cref="ShredService"/> 在未显式指定算法时,会按 <see cref="SsdDetector"/>
/// 的探测结果在「默认算法」和「SSD 默认算法」之间路由。
/// 真实的 SsdDetector 依赖 Win32 IOCTL,这里用 fake 子类绕开内核态调用。
/// </summary>
public class ShredServiceSsdRoutingTests
{
    [Fact]
    public async Task SolidState_NoAlgorithmId_RoutesToSsdDefault()
    {
        var defaultAlgo = new SpyAlgorithm(ShredAlgorithmIds.Purge3Pass);
        var ssdAlgo = new SpyAlgorithm(ShredAlgorithmIds.CryptoErase);
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.SolidState, trim: true);

        var svc = ShredService.CreateForTests(
            new IShredAlgorithm[] { defaultAlgo, ssdAlgo },
            defaultId: defaultAlgo.Id,
            ssdDefault: ssdAlgo.Id,
            ssdDetector: detector);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, 4 });
            var job = new ShredJob { Path = path, SizeBytes = 4, IsDirectory = false };
            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.True(ssdAlgo.Invoked, "SSD 上应路由到 CryptoErase。");
            Assert.False(defaultAlgo.Invoked, "SSD 上不应再用多次覆写算法。");
            Assert.Equal(ShredJobStatus.Success, job.Status);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task HardDisk_NoAlgorithmId_RoutesToDefault()
    {
        var defaultAlgo = new SpyAlgorithm(ShredAlgorithmIds.Purge3Pass);
        var ssdAlgo = new SpyAlgorithm(ShredAlgorithmIds.CryptoErase);
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.HardDisk, trim: false);

        var svc = ShredService.CreateForTests(
            new IShredAlgorithm[] { defaultAlgo, ssdAlgo },
            defaultId: defaultAlgo.Id,
            ssdDefault: ssdAlgo.Id,
            ssdDetector: detector);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 9, 9, 9, 9 });
            var job = new ShredJob { Path = path, SizeBytes = 4, IsDirectory = false };
            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.True(defaultAlgo.Invoked, "HDD 上应走默认多次覆写算法。");
            Assert.False(ssdAlgo.Invoked, "HDD 上不应路由到 CryptoErase。");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SolidState_ExplicitAlgorithmId_BypassesSsdRouting()
    {
        // 用户显式选择 7-pass DoD 时,即便检测到 SSD 也不应被悄悄降级
        var defaultAlgo = new SpyAlgorithm(ShredAlgorithmIds.Purge3Pass);
        var ssdAlgo = new SpyAlgorithm(ShredAlgorithmIds.CryptoErase);
        var explicitAlgo = new SpyAlgorithm(ShredAlgorithmIds.Purge7Pass);
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.SolidState, trim: true);

        var svc = ShredService.CreateForTests(
            new IShredAlgorithm[] { defaultAlgo, ssdAlgo, explicitAlgo },
            defaultId: defaultAlgo.Id,
            ssdDefault: ssdAlgo.Id,
            ssdDetector: detector);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 7, 7, 7, 7 });
            var job = new ShredJob
            {
                Path = path,
                SizeBytes = 4,
                IsDirectory = false,
                AlgorithmId = ShredAlgorithmIds.Purge7Pass,
            };
            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.True(explicitAlgo.Invoked, "显式选择的算法必须被使用。");
            Assert.False(ssdAlgo.Invoked, "SSD 路由不得覆盖用户显式选择。");
            Assert.False(defaultAlgo.Invoked);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task UnknownProfile_FallsBackToDefault()
    {
        // 探测不出来时(虚拟磁盘/U 盘桥接控制器)按 Default 走,绝不能因探测失败而拒绝粉碎
        var defaultAlgo = new SpyAlgorithm(ShredAlgorithmIds.Purge3Pass);
        var ssdAlgo = new SpyAlgorithm(ShredAlgorithmIds.CryptoErase);
        var detector = new FakeSsdDetector(SsdDetector.DeviceProfile.Unknown, trim: false);

        var svc = ShredService.CreateForTests(
            new IShredAlgorithm[] { defaultAlgo, ssdAlgo },
            defaultId: defaultAlgo.Id,
            ssdDefault: ssdAlgo.Id,
            ssdDetector: detector);

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 0, 0, 0, 0 });
            var job = new ShredJob { Path = path, SizeBytes = 4, IsDirectory = false };
            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.True(defaultAlgo.Invoked, "未知介质类型应走 Default。");
            Assert.False(ssdAlgo.Invoked);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class SpyAlgorithm : ShredAlgorithmBase
    {
        public SpyAlgorithm(string id) { Id = id; }

        public override string Id { get; }
        public override string Name => Id;
        public override int PassCount => 1;
        public bool Invoked { get; private set; }

        protected override void FillBuffer(int passIndex, byte[] buffer, int count)
        {
            Invoked = true;
            // 写一段确定字节,确保 ShredService 后续的截断/改名/删除流程能跑通
            Array.Fill(buffer, (byte)0xCD, 0, count);
        }
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

        public override StorageInfo Probe(string anyPathOnVolume)
            => new(_profile, _trim);
    }
}
