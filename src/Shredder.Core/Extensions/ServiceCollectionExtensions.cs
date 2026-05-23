using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shredder.Core.Algorithms;
using Shredder.Core.Configuration;
using Shredder.Core.Diagnostics;
using Shredder.Core.FileSystem;
using Shredder.Core.Reporting;
using Shredder.Core.Security;
using Shredder.Core.Services;

namespace Shredder.Core.Extensions;

/// <summary>Shredder.Core 的 DI 注册入口。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Shredder.Core 的所有服务、算法、选项绑定。
    /// 调用方负责 <see cref="IHostBuilder"/> 或类似容器中的 Logging/Configuration 配置。
    /// </summary>
    public static IServiceCollection AddShredderCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 1. 选项绑定 + 校验
        services
            .AddOptions<ShredderOptions>()
            .Bind(configuration.GetSection(ShredderOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ShredderOptions>, ShredderOptionsValidator>();

        // 2. 算法实现(单例)
        services.AddSingleton<IShredAlgorithm, SinglePassRandomAlgorithm>();
        services.AddSingleton<IShredAlgorithm>(sp =>
            new DoD522022MAlgorithm(passes: 3, sp.GetRequiredService<IOptions<ShredderOptions>>()));
        services.AddSingleton<IShredAlgorithm>(sp =>
            new DoD522022MAlgorithm(passes: 7, sp.GetRequiredService<IOptions<ShredderOptions>>()));
        services.AddSingleton<IShredAlgorithm, ZeroFillRenameAlgorithm>();
        services.AddSingleton<IShredAlgorithm, CryptoEraseAlgorithm>();

        // 3. 算法仓库
        services.AddSingleton<IShredAlgorithmRegistry, ShredAlgorithmRegistry>();

        // 4. 安全模块(无状态,单例即可)
        services.AddSingleton<PathSafetyGuard>();
        services.AddSingleton<FileLockResolver>();
        services.AddSingleton<SsdDetector>();
        services.AddSingleton<TrimFallbackRunner>();
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ShredderOptions>>().Value.Safety;
            return new MftResidencyHandler(opts.MftResidentInflateThresholdBytes, opts.MftResidentInflateTargetBytes);
        });

        // 5. 业务服务(无状态,单例即可)
        services.AddSingleton<ShredService>();
        services.AddSingleton<ForceDeleteService>();
        services.AddSingleton<IRecycleBinEnumerator, DefaultRecycleBinEnumerator>();
        services.AddSingleton<IRecycleBinFileShredder, DefaultRecycleBinFileShredder>();
        services.AddSingleton<IRecycleBinShell, DefaultRecycleBinShell>();
        services.AddSingleton<RecycleBinService>();
        services.AddSingleton<FreeSpaceService>();

        // 6. 审计报告
        services.AddSingleton<IShredReportWriter, ShredReportWriter>();

        // 7. 诊断
        services.AddSingleton<IDiagnosticsCollector, DiagnosticsCollector>();
        services.AddSingleton<IDiagnosticsExporter, DiagnosticsExporter>();

        // 8. 日志:路径脱敏富化器。宿主自行 .Enrich.With(sp.GetRequiredService<PathRedactingEnricher>())
        services.AddSingleton<PathRedactingEnricher>();

        return services;
    }
}

internal sealed class ShredderOptionsValidator : IValidateOptions<ShredderOptions>
{
    public ValidateOptionsResult Validate(string? name, ShredderOptions options)
    {
        var errors = new List<string>();

        if (options.Io.BufferSizeBytes <= 0)
            errors.Add("Shredder:Io:BufferSizeBytes 必须为正数。");
        if (options.Io.MaxConcurrentFiles <= 0)
            errors.Add("Shredder:Io:MaxConcurrentFiles 必须为正数。");
        if (options.Io.ProgressReportIntervalMs < 0)
            errors.Add("Shredder:Io:ProgressReportIntervalMs 不能为负数。");
        if (options.FreeSpace.BlockSizeBytes <= 0)
            errors.Add("Shredder:FreeSpace:BlockSizeBytes 必须为正数。");
        if (string.IsNullOrWhiteSpace(options.Algorithm.Default))
            errors.Add("Shredder:Algorithm:Default 不能为空。");
        if (string.IsNullOrWhiteSpace(options.Ui.ConfirmationKeyword))
            errors.Add("Shredder:Ui:ConfirmationKeyword 不能为空。");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
