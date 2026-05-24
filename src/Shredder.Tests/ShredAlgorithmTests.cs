using System.Security.Cryptography;
using Shredder.Core.Algorithms;
using Shredder.Core.Models;
using Shredder.Core.Services;
using Xunit;

namespace Shredder.Tests;

public class ShredAlgorithmTests
{
    [Fact]
    public async Task SinglePassRandom_ChangesContent()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var originalHash = Sha256(File.ReadAllBytes(path));

        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var algo = new SinglePassRandomAlgorithm();
            await algo.ShredAsync(fs, fs.Length, path, null, CancellationToken.None);
        }

        var newHash = Sha256(File.ReadAllBytes(path));
        Assert.NotEqual(originalHash, newHash);
        File.Delete(path);
    }

    [Fact]
    public async Task ShredService_DeletesFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "secret");
        var svc = ShredService.CreateForTests(new SinglePassRandomAlgorithm());
        var job = new ShredJob { Path = path, SizeBytes = new FileInfo(path).Length, IsDirectory = false };
        await svc.ShredAsync(job, null, CancellationToken.None);
        Assert.False(File.Exists(path));
        Assert.Equal(ShredJobStatus.Success, job.Status);
    }

    [Fact]
    public async Task ShredService_DeletesDirectoryTree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shredder-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dir, "root.txt"), "root-secret");
        await File.WriteAllTextAsync(Path.Combine(dir, "nested", "child.txt"), "child-secret");

        try
        {
            var svc = ShredService.CreateForTests(new SinglePassRandomAlgorithm());
            var job = new ShredJob { Path = dir, IsDirectory = true };

            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.False(Directory.Exists(dir));
            Assert.Equal(ShredJobStatus.Success, job.Status);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FastDelete_DeletesLargeFileWithoutOverwritePass()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[8 * 1024 * 1024]);

        try
        {
            var algo = new FastDeleteAlgorithm();
            var svc = ShredService.CreateForTests(algo);
            var job = new ShredJob
            {
                Path = path,
                SizeBytes = new FileInfo(path).Length,
                IsDirectory = false,
                AlgorithmId = ShredAlgorithmIds.FastDelete,
            };

            await svc.ShredAsync(job, null, CancellationToken.None);

            Assert.False(File.Exists(path));
            Assert.Equal(ShredJobStatus.Success, job.Status);
            Assert.Equal(0, algo.PassCount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
