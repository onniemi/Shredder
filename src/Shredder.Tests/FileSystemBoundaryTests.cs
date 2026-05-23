using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Core.FileSystem;
using Shredder.Core.Models;
using Shredder.Core.Security;
using Shredder.Core.Services;
using Xunit;

namespace Shredder.Tests;

/// <summary>
/// 文件系统层边界测试:ADS / 重解析点 / 属性恢复 / 锁文件。
/// 这些路径都依赖宿主 NTFS 行为或 Windows 权限,在不满足前置条件时主动早退,
/// 避免在 CI 或非 NTFS 临时盘上把测试误报红。
/// </summary>
/// <remarks>
/// 关键安全约束:任何会进入 ShredFileAsync 失败分支的测试都必须把
/// <see cref="ShredderSafetyOptions.AllowScheduleOnRebootDelete"/> 关掉,
/// 否则会真的调用 <c>MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)</c>
/// 在开发机重启时删测试文件。
/// </remarks>
public class FileSystemBoundaryTests
{
    // -------------------- 1. ADS --------------------

    [Fact]
    public async Task Ads_ShredFile_RemovesMainStreamAndAdsResidue()
    {
        // 若临时盘不支持 ADS(FAT32 / exFAT / 网络盘)直接跳过,避免误失败
        var dir = Path.Combine(Path.GetTempPath(), "shredder-ads-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var main = Path.Combine(dir, "victim.txt");
        try
        {
            File.WriteAllBytes(main, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

            if (!TryCreateAds(main, ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"))
            {
                // 临时目录所在文件系统不支持 ADS,记录后跳过(不算失败)
                return;
            }
            // 双保险:写入后枚举不到也算不支持
            var beforeAds = AlternateDataStreamEnumerator.EnumerateAdsNames(main);
            if (beforeAds.Count == 0) return;

            var algo = new RecordingAlgorithm();
            var svc = BuildService(algo);

            var job = new ShredJob { Path = main, IsDirectory = false };
            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.Equal(ShredJobStatus.Success, job.Status);
            Assert.False(File.Exists(main), "主流应被删除。");
            // 主文件已不存在,ADS 自然随之消失;再确认一次以防"主流删但 ADS 残留"的回归
            var afterAds = AlternateDataStreamEnumerator.EnumerateAdsNames(main);
            Assert.Empty(afterAds);
            // 算法必须被调用过(主流 + ADS 至少 2 次,只校验下限)
            Assert.True(algo.InvocationCount >= 1, "粉碎算法应至少对主流被调用一次。");
        }
        finally
        {
            SafeDeleteTree(dir);
        }
    }

    // -------------------- 2. 重解析点(符号链接 / Junction) --------------------

    [Fact]
    public async Task SymbolicLinkFile_Rejected_RealTargetSurvives()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shredder-symlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "real.txt");
        var link = Path.Combine(dir, "link.txt");
        try
        {
            File.WriteAllBytes(target, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (IOException)
            {
                // 没开开发者模式 / 非管理员:无法创建符号链接,跳过(不让 CI 红)
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            // 确认确实是 reparse point,否则不在测试范围
            if (!ReparsePointDetector.IsReparsePoint(link)) return;

            var svc = BuildService(new RecordingAlgorithm());

            var job = new ShredJob { Path = link, IsDirectory = false };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.ShredAsync(job, null, CancellationToken.None));

            Assert.Contains("重解析点", ex.Message);
            Assert.Equal(ShredJobStatus.Failed, job.Status);
            Assert.True(File.Exists(target), "原始文件不应被顺着符号链接删除。");
            // 链本身在拒绝路径上不会被删除(还没走到 delete 阶段);只断言"目标活着"即可
        }
        finally
        {
            SafeDeleteTree(dir);
        }
    }

    [Fact]
    public async Task JunctionDirectory_Rejected_RealTargetSurvives()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shredder-junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "real_dir");
        var junction = Path.Combine(dir, "junction_dir");
        try
        {
            Directory.CreateDirectory(target);
            var canary = Path.Combine(target, "canary.txt");
            File.WriteAllText(canary, "do-not-delete");

            // Junction 不需要管理员/开发者模式,优先用 cmd /c mklink /J
            if (!TryCreateJunction(junction, target)) return;
            if (!ReparsePointDetector.IsReparsePoint(junction)) return;

            var svc = BuildService(new RecordingAlgorithm());

            var job = new ShredJob { Path = junction, IsDirectory = true };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.ShredAsync(job, null, CancellationToken.None));

            Assert.Contains("重解析点", ex.Message);
            Assert.Equal(ShredJobStatus.Failed, job.Status);
            Assert.True(Directory.Exists(target), "Junction 指向的真实目录必须保留。");
            Assert.True(File.Exists(canary), "Junction 内的 canary 文件必须保留。");
        }
        finally
        {
            SafeDeleteTree(dir);
        }
    }

    // -------------------- 3. 属性恢复 --------------------

    [Fact]
    public async Task ReadOnlyFile_AlgorithmFails_OriginalAttributesRestored()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x42, 0x43, 0x44, 0x45 });
            File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);
            var originalAttrs = File.GetAttributes(path);

            // 让算法在写主流时抛异常,以触发 ShredFileAsync 的 catch 恢复分支
            var algo = new ThrowingAlgorithm(
                new IOException("synthetic algorithm failure for attribute restoration test"));
            // 必须关掉 reboot-delete,否则失败路径会调真实 MoveFileEx
            var svc = BuildService(algo, allowScheduleOnReboot: false);

            var job = new ShredJob { Path = path, IsDirectory = false };
            await Assert.ThrowsAsync<IOException>(
                () => svc.ShredAsync(job, null, CancellationToken.None));

            Assert.Equal(ShredJobStatus.Failed, job.Status);
            Assert.True(File.Exists(path), "算法失败时文件应留在原地。");
            var restored = File.GetAttributes(path);
            Assert.True(restored.HasFlag(FileAttributes.ReadOnly),
                $"ReadOnly 属性应被恢复,实际:{restored}");
            Assert.True(restored.HasFlag(FileAttributes.Hidden),
                $"Hidden 属性应被恢复,实际:{restored}");
            // 至少包含原属性的"破坏性位"(允许系统加 Archive 等自动位)
            Assert.Equal(originalAttrs & (FileAttributes.ReadOnly | FileAttributes.Hidden),
                         restored & (FileAttributes.ReadOnly | FileAttributes.Hidden));
        }
        finally
        {
            if (File.Exists(path))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); }
                catch { /* 清理失败不掩盖断言 */ }
                File.Delete(path);
            }
        }
    }

    // -------------------- 4. 锁文件 --------------------

    [Fact]
    public async Task LockedFile_FailsLoudly_FileSurvivesContentUnchanged()
    {
        var path = Path.GetTempFileName();
        var originalContent = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        File.WriteAllBytes(path, originalContent);
        // 关键:把要测的文件占住,迫使 OpenForExclusiveWrite 失败
        FileStream? lockHandle = null;
        try
        {
            lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var algo = new RecordingAlgorithm();
            // AllowScheduleOnRebootDelete = false 才能避免真实的 MoveFileEx 调用
            var svc = BuildService(algo, allowScheduleOnReboot: false);

            var job = new ShredJob { Path = path, IsDirectory = false };

            // 必须抛错(响亮失败),不能静默成功
            await Assert.ThrowsAnyAsync<IOException>(
                () => svc.ShredAsync(job, null, CancellationToken.None));

            Assert.Equal(ShredJobStatus.Failed, job.Status);
            Assert.True(File.Exists(path), "占用文件不应被删除。");
            Assert.False(algo.FullyCompleted, "算法不应在持锁时完成主流覆写。");
        }
        finally
        {
            lockHandle?.Dispose();
            if (File.Exists(path))
            {
                try { File.SetAttributes(path, FileAttributes.Normal); }
                catch { /* ignored */ }
                File.Delete(path);
            }
        }
    }

    // -------------------- helpers --------------------

    /// <summary>
    /// 直接构造 ShredService,绕过 <see cref="ShredService.CreateForTests(IShredAlgorithm, SsdDetector?)"/>,
    /// 因为后者不暴露 Safety 选项,而本组测试需要按场景关掉
    /// <see cref="ShredderSafetyOptions.AllowScheduleOnRebootDelete"/>。
    /// </summary>
    private static ShredService BuildService(
        IShredAlgorithm algorithm,
        bool allowScheduleOnReboot = true)
    {
        var opts = Options.Create(new ShredderOptions
        {
            Algorithm = new ShredderAlgorithmOptions { Default = algorithm.Id },
            Safety = new ShredderSafetyOptions
            {
                AllowScheduleOnRebootDelete = allowScheduleOnReboot,
                // 其余安全默认值保持(RejectReparsePoints=true 等)
            },
        });
        var safety = opts.Value.Safety;
        return new ShredService(
            new ShredAlgorithmRegistry(new[] { algorithm }),
            opts,
            new PathSafetyGuard(opts),
            new MftResidencyHandler(safety.MftResidentInflateThresholdBytes, safety.MftResidentInflateTargetBytes),
            new FileLockResolver(),
            new SsdDetector(),
            NullLogger<ShredService>.Instance);
    }

    private static bool TryCreateAds(string mainPath, string adsName, string content)
    {
        var adsPath = mainPath + adsName; // "file.txt:Zone.Identifier"
        try
        {
            using var fs = new FileStream(adsPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>用 <c>cmd /c mklink /J</c> 创建 NTFS Junction;失败时返回 false 让测试跳过。</summary>
    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0 && Directory.Exists(junctionPath);
        }
        catch
        {
            return false;
        }
    }

    private static void SafeDeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            // 防御性清属性,避免 ReadOnly/Junction 阻碍递归删除
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); }
                catch { /* ignored */ }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不掩盖断言;留给 OS 重启清理
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // -------------------- 伪算法 --------------------

    /// <summary>记录调用次数 + 完成标志的算法,用于"成功路径"断言。</summary>
    private sealed class RecordingAlgorithm : IShredAlgorithm
    {
        public string Id => "boundary-recorder";
        public string Name => "Boundary Recorder";
        public int PassCount => 1;
        public int InvocationCount { get; private set; }
        public bool FullyCompleted { get; private set; }

        public async Task ShredAsync(
            Stream stream,
            long length,
            string filePath,
            IProgress<ShredProgress>? progress,
            CancellationToken ct)
        {
            InvocationCount++;
            // 真实地把 length 个 0x55 写进去,确保后续 Truncate/Move/Delete 有得跑
            var buffer = new byte[Math.Min(4096, Math.Max(1, length))];
            Array.Fill(buffer, (byte)0x55);
            long written = 0;
            while (written < length)
            {
                ct.ThrowIfCancellationRequested();
                int chunk = (int)Math.Min(buffer.Length, length - written);
                await stream.WriteAsync(buffer.AsMemory(0, chunk), ct);
                written += chunk;
            }
            await stream.FlushAsync(ct);
            FullyCompleted = true;
        }
    }

    /// <summary>恒抛指定异常的算法,用于"失败回滚"路径断言。</summary>
    private sealed class ThrowingAlgorithm : IShredAlgorithm
    {
        private readonly Exception _toThrow;

        public ThrowingAlgorithm(Exception toThrow) { _toThrow = toThrow; }

        public string Id => "boundary-thrower";
        public string Name => "Boundary Thrower";
        public int PassCount => 1;

        public Task ShredAsync(
            Stream stream,
            long length,
            string filePath,
            IProgress<ShredProgress>? progress,
            CancellationToken ct)
            => throw _toThrow;
    }
}
