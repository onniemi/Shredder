using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Models;
using Shredder.Core.Security;
using Shredder.Core.Services;
using Xunit;

namespace Shredder.Tests;

public class ForceDeleteServiceTests
{
    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "secret");
        var job = new ShredJob { Path = path, IsDirectory = false, SizeBytes = new FileInfo(path).Length };

        var result = await CreateService().DeleteAsync(job, CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal(ShredJobStatus.Success, job.Status);
        Assert.Equal(1, result.DeletedFiles);
    }

    [Fact]
    public async Task DeleteAsync_RemovesReadOnlyFile()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "secret");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var job = new ShredJob { Path = path, IsDirectory = false, SizeBytes = new FileInfo(path).Length };

        await CreateService().DeleteAsync(job, CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Equal(ShredJobStatus.Success, job.Status);
    }

    [Fact]
    public async Task DeleteAsync_RemovesNestedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "shredder-force-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        await File.WriteAllTextAsync(Path.Combine(root, "a", "b", "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(root, "two.txt"), "two");
        var job = new ShredJob { Path = root, IsDirectory = true };

        try
        {
            var result = await CreateService().DeleteAsync(job, CancellationToken.None);

            Assert.False(Directory.Exists(root));
            Assert.Equal(ShredJobStatus.Success, job.Status);
            Assert.Equal(2, result.DeletedFiles);
            Assert.Equal(3, result.DeletedDirectories);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_ForbiddenPath_IsRejected()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var job = new ShredJob { Path = windows, IsDirectory = true };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().DeleteAsync(job, CancellationToken.None));
        Assert.Equal(ShredJobStatus.Pending, job.Status);
    }

    private static ForceDeleteService CreateService()
    {
        var options = Options.Create(new ShredderOptions());
        return new ForceDeleteService(
            options,
            new PathSafetyGuard(options),
            new Shredder.Core.FileSystem.FileLockResolver(),
            NullLogger<ForceDeleteService>.Instance);
    }
}
