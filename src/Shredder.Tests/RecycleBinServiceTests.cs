using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Shredder.Core.Models;
using Shredder.Core.Services;
using Xunit;

namespace Shredder.Tests;

/// <summary>
/// 验证 <see cref="RecycleBinService"/> 的失败容忍/统计/脱敏行为。
/// 全部使用 fake 抽象(枚举器/单项粉碎器/Shell32 调用),绝不触碰真实 OS 回收站。
/// </summary>
public class RecycleBinServiceTests
{
    [Fact]
    public async Task SingleFailure_DoesNotAbortRemainingItems()
    {
        var items = new[] { @"C:\$Recycle.Bin\a.bin", @"C:\$Recycle.Bin\b.bin", @"C:\$Recycle.Bin\c.bin" };
        var enumerator = new FakeRecycleBinEnumerator(items);
        var failingPath = items[1];
        var fileShredder = new FakeRecycleBinFileShredder(p =>
        {
            if (string.Equals(p, failingPath, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("no permission");
        });
        var shell = new FakeRecycleBinShell(hresult: 0);

        var svc = CreateService(enumerator, fileShredder, shell);
        var result = await svc.EmptyAsync(progress: null, CancellationToken.None);

        Assert.Equal(3, result.TotalCandidates);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(3, fileShredder.AttemptedPaths.Count);
        Assert.Contains(items[0], fileShredder.AttemptedPaths);
        Assert.Contains(items[2], fileShredder.AttemptedPaths);
    }

    [Fact]
    public async Task ResultCounts_MatchPerItemOutcomes()
    {
        var items = new[]
        {
            @"D:\$Recycle.Bin\ok1.dat",
            @"D:\$Recycle.Bin\fail1.dat",
            @"D:\$Recycle.Bin\ok2.dat",
            @"D:\$Recycle.Bin\fail2.dat",
        };
        var enumerator = new FakeRecycleBinEnumerator(items);
        var fileShredder = new FakeRecycleBinFileShredder(p =>
        {
            if (p.Contains("fail", StringComparison.OrdinalIgnoreCase))
                throw new IOException("locked");
        });
        var shell = new FakeRecycleBinShell(hresult: 0);

        var result = await CreateService(enumerator, fileShredder, shell)
            .EmptyAsync(progress: null, CancellationToken.None);

        Assert.Equal(4, result.TotalCandidates);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, result.FailedItems.Count);
        Assert.All(result.FailedItems, fi => Assert.Equal(nameof(IOException), fi.Reason));
    }

    [Fact]
    public async Task ShellNonZeroHResult_IsObservableAndMarksFailure()
    {
        var enumerator = new FakeRecycleBinEnumerator(Array.Empty<string>());
        var fileShredder = new FakeRecycleBinFileShredder(_ => { });
        // 自造一个非 S_OK/S_FALSE 的 HRESULT,例如 E_ACCESSDENIED 0x80070005
        const int eAccessDenied = unchecked((int)0x80070005);
        var shell = new FakeRecycleBinShell(hresult: eAccessDenied);

        var result = await CreateService(enumerator, fileShredder, shell)
            .EmptyAsync(progress: null, CancellationToken.None);

        Assert.Equal(eAccessDenied, result.ShellHResult);
        Assert.False(result.ShellSucceeded);
        Assert.False(result.OverallSucceeded);
    }

    [Fact]
    public async Task ShellSFalse_IsTreatedAsSuccess()
    {
        var enumerator = new FakeRecycleBinEnumerator(Array.Empty<string>());
        var fileShredder = new FakeRecycleBinFileShredder(_ => { });
        var shell = new FakeRecycleBinShell(hresult: 1); // S_FALSE: 已空

        var result = await CreateService(enumerator, fileShredder, shell)
            .EmptyAsync(progress: null, CancellationToken.None);

        Assert.True(result.ShellSucceeded);
        Assert.True(result.OverallSucceeded);
    }

    [Fact]
    public async Task OverwriteDisabled_SkipsShredButStillCallsShell()
    {
        var items = new[] { @"C:\$Recycle.Bin\x.bin", @"C:\$Recycle.Bin\y.bin" };
        var enumerator = new FakeRecycleBinEnumerator(items);
        var fileShredder = new FakeRecycleBinFileShredder(_ => { });
        var shell = new FakeRecycleBinShell(hresult: 0);

        var svc = CreateService(enumerator, fileShredder, shell,
            options => { options.RecycleBin.OverwriteContents = false; });
        var result = await svc.EmptyAsync(progress: null, CancellationToken.None);

        Assert.Equal(0, result.TotalCandidates);
        Assert.Empty(fileShredder.AttemptedPaths);
        // 即使关闭 overwrite,shell 元数据清理仍然要发生
        Assert.Equal(1, shell.CallCount);
        Assert.Equal(0, result.ShellHResult);
        Assert.True(result.OverallSucceeded);
    }

    [Fact]
    public async Task CallShellDisabled_LeavesShellHResultNullAndStillSucceeds()
    {
        var items = new[] { @"C:\$Recycle.Bin\a.dat" };
        var enumerator = new FakeRecycleBinEnumerator(items);
        var fileShredder = new FakeRecycleBinFileShredder(_ => { });
        var shell = new FakeRecycleBinShell(hresult: 0);

        var svc = CreateService(enumerator, fileShredder, shell,
            options => { options.RecycleBin.CallShellEmptyAfterShred = false; });
        var result = await svc.EmptyAsync(progress: null, CancellationToken.None);

        Assert.Null(result.ShellHResult);
        Assert.Equal(0, shell.CallCount);
        Assert.True(result.ShellSucceeded); // 未调用视为成功
        Assert.True(result.OverallSucceeded);
        Assert.Equal(1, result.Succeeded);
    }

    [Fact]
    public async Task Cancellation_StopsIterationAndThrows()
    {
        var items = Enumerable.Range(0, 10).Select(i => @$"C:\$Recycle.Bin\f{i}.bin").ToArray();
        using var cts = new CancellationTokenSource();
        var enumerator = new FakeRecycleBinEnumerator(items);
        var fileShredder = new FakeRecycleBinFileShredder(_ =>
        {
            // 处理第一个后取消,后续应该抛 OperationCanceledException
            cts.Cancel();
        });
        var shell = new FakeRecycleBinShell(hresult: 0);

        var svc = CreateService(enumerator, fileShredder, shell);
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await svc.EmptyAsync(progress: null, cts.Token));

        // 第一次粉碎肯定发生过;但绝不可能完成全部 10 项
        Assert.True(fileShredder.AttemptedPaths.Count >= 1);
        Assert.True(fileShredder.AttemptedPaths.Count < items.Length);
        // shell 在取消时不应被调用
        Assert.Equal(0, shell.CallCount);
    }

    [Fact]
    public async Task FailedItems_ContainHashedPathsNotRawPaths()
    {
        const string raw = @"C:\$Recycle.Bin\S-1-5-21-secret-id\$RXXXX.docx";
        var enumerator = new FakeRecycleBinEnumerator(new[] { raw });
        var fileShredder = new FakeRecycleBinFileShredder(_ =>
            throw new UnauthorizedAccessException("denied"));
        var shell = new FakeRecycleBinShell(hresult: 0);

        var result = await CreateService(enumerator, fileShredder, shell)
            .EmptyAsync(progress: null, CancellationToken.None);

        Assert.Equal(1, result.Failed);
        var item = Assert.Single(result.FailedItems);

        // 关键安全断言:绝不出现原始路径片段
        Assert.DoesNotContain("S-1-5-21-secret-id", item.PathRedacted);
        Assert.DoesNotContain("$RXXXX", item.PathRedacted);
        Assert.DoesNotContain(@"C:\", item.PathRedacted);

        // 应该是 [hash:xxxxxxxx] 形态(可能带扩展名)
        Assert.StartsWith("[hash:", item.PathRedacted);
        Assert.Contains("]", item.PathRedacted);
        Assert.Equal(nameof(UnauthorizedAccessException), item.Reason);
    }

    // -------- helpers --------

    private static RecycleBinService CreateService(
        IRecycleBinEnumerator enumerator,
        IRecycleBinFileShredder fileShredder,
        IRecycleBinShell shell,
        Action<ShredderOptions>? configure = null)
    {
        var options = new ShredderOptions();
        // 默认开启 OverwriteContents + CallShellEmptyAfterShred,模型默认即如此
        configure?.Invoke(options);
        return new RecycleBinService(
            enumerator, fileShredder, shell,
            Options.Create(options),
            NullLogger<RecycleBinService>.Instance);
    }

    private sealed class FakeRecycleBinEnumerator : IRecycleBinEnumerator
    {
        private readonly IReadOnlyList<string> _items;
        public FakeRecycleBinEnumerator(IEnumerable<string> items) => _items = items.ToList();

        public IEnumerable<string> EnumerateFiles(CancellationToken ct)
        {
            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private sealed class FakeRecycleBinFileShredder : IRecycleBinFileShredder
    {
        private readonly Action<string> _onPath;
        public List<string> AttemptedPaths { get; } = new();

        public FakeRecycleBinFileShredder(Action<string> onPath) => _onPath = onPath;

        public Task ShredFileAsync(string path, IProgress<ShredProgress>? progress, CancellationToken ct)
        {
            AttemptedPaths.Add(path);
            ct.ThrowIfCancellationRequested();
            _onPath(path);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecycleBinShell : IRecycleBinShell
    {
        private readonly int _hresult;
        public int CallCount { get; private set; }

        public FakeRecycleBinShell(int hresult) => _hresult = hresult;

        public int Empty()
        {
            CallCount++;
            return _hresult;
        }
    }
}
