using Shredder.Core.Models;

namespace Shredder.Core.Services;

/// <summary>
/// 抽象"对回收站中的一个数据文件做粉碎"的能力,便于单元测试注入 fake,
/// 避免在测试里真的去 new 一个 <see cref="ShredService"/>并触发文件 IO。
/// </summary>
public interface IRecycleBinFileShredder
{
    Task ShredFileAsync(string path, IProgress<ShredProgress>? progress, CancellationToken ct);
}

/// <summary>默认实现:把任务交给 <see cref="ShredService"/>。</summary>
internal sealed class DefaultRecycleBinFileShredder : IRecycleBinFileShredder
{
    private readonly ShredService _shredService;

    public DefaultRecycleBinFileShredder(ShredService shredService)
    {
        ArgumentNullException.ThrowIfNull(shredService);
        _shredService = shredService;
    }

    public Task ShredFileAsync(string path, IProgress<ShredProgress>? progress, CancellationToken ct)
    {
        var job = new ShredJob
        {
            Path = path,
            SizeBytes = new FileInfo(path).Length,
            IsDirectory = false,
        };
        return _shredService.ShredAsync(job, progress, ct);
    }
}
