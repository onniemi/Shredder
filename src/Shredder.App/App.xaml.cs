using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Shredder.App.ViewModels;
using Shredder.App.Views;
using Shredder.App.Views.Pages;
using Shredder.Core.Configuration;
using Shredder.Core.Diagnostics;
using Shredder.Core.Extensions;
using Wpf.Ui;
using Wpf.Ui.Appearance;

namespace Shredder.App;

public partial class App : Application
{
    private IHost? _host;

    public IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("Host 尚未初始化。");

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "WPF 启动顶层边界:必须捕获所有异常,弹出诊断对话框后优雅退出;否则未处理异常会冲到 Dispatcher 导致进程被异常码终止,用户看不到任何信息。")]
    protected override async void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureAppConfiguration((ctx, cfg) =>
                {
                    cfg.SetBasePath(AppContext.BaseDirectory);
                    cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    cfg.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
                        optional: true, reloadOnChange: true);
                    cfg.AddEnvironmentVariables(prefix: "SHREDDER_");
                    cfg.AddCommandLine(e.Args);
                })
                .UseSerilog((ctx, sp, lc) => ConfigureSerilog(ctx.Configuration, sp, lc))
                .ConfigureServices((ctx, services) =>
                {
                    services.AddShredderCore(ctx.Configuration);

                    // WPF-UI 基础服务(用于 ContentDialog / Snackbar / Theme 等)
                    services.AddSingleton<IThemeService, ThemeService>();
                    services.AddSingleton<ISnackbarService, SnackbarService>();
                    services.AddSingleton<IContentDialogService, ContentDialogService>();
                    services.AddSingleton<IPageService, ServiceLocatorPageProvider>();

                    // ViewModels
                    services.AddSingleton<ShredPageViewModel>();
                    services.AddSingleton<FreeSpacePageViewModel>();
                    services.AddSingleton<RecycleBinPageViewModel>();
                    services.AddSingleton<SettingsPageViewModel>();
                    services.AddSingleton<AboutPageViewModel>();

                    // Pages (transient: NavigationView 内部缓存)
                    services.AddTransient<ShredPage>();
                    services.AddTransient<FreeSpacePage>();
                    services.AddTransient<RecycleBinPage>();
                    services.AddTransient<SettingsPage>();
                    services.AddTransient<AboutPage>();

                    // 主窗口
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync().ConfigureAwait(true);

            ApplyConfiguredTheme();

            var main = _host.Services.GetRequiredService<MainWindow>();
            main.Show();

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"应用启动失败：\n\n{ex.Message}\n\n{ex.StackTrace}",
                "Shredder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void ApplyConfiguredTheme()
    {
        if (_host is null) return;
        var ui = _host.Services.GetRequiredService<IOptions<ShredderOptions>>().Value.Ui;
        var theme = ui.Theme?.Trim().ToLowerInvariant() switch
        {
            "dark" => ApplicationTheme.Dark,
            "light" => ApplicationTheme.Light,
            _ => ApplicationTheme.Unknown, // System
        };
        ApplicationThemeManager.Apply(
            theme == ApplicationTheme.Unknown ? ApplicationThemeManager.GetSystemTheme() switch
            {
                SystemTheme.Dark => ApplicationTheme.Dark,
                _ => ApplicationTheme.Light,
            } : theme);
    }

    /// <summary>
    /// 配置 Serilog 流水线:Debug + 滚动文件 + 路径脱敏富化器。
    /// <see cref="Serilog.LoggerConfiguration.ReadFrom"/> 从 appsettings 的 <c>Serilog</c> 节读取 MinimumLevel/Override/Enrich。
    /// 文件 sink 的路径在代码里展开环境变量(<c>Serilog.Settings.Configuration</c> 不会替我们做),
    /// 因此 appsettings 里只放最小级别与"非物理"开关。
    /// </summary>
    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "日志初始化失败必须降级:即使目录创建失败也要让 Debug/Console sink 继续工作,否则用户连排错日志都看不到。")]
    private static void ConfigureSerilog(IConfiguration configuration, IServiceProvider sp, LoggerConfiguration lc)
    {
        const string outputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

        var loggingOpts = sp.GetRequiredService<IOptions<ShredderOptions>>().Value.Logging;
        var enricher = sp.GetRequiredService<PathRedactingEnricher>();

        lc.ReadFrom.Configuration(configuration)
          .Enrich.FromLogContext()
          .Enrich.With(enricher)
          .WriteTo.Debug(outputTemplate: outputTemplate);

#if DEBUG
        lc.WriteTo.Console(outputTemplate: outputTemplate);
#endif

        if (!loggingOpts.FileSinkEnabled) return;

        try
        {
            var dir = Environment.ExpandEnvironmentVariables(
                string.IsNullOrWhiteSpace(loggingOpts.OutputDirectory)
                    ? "%LOCALAPPDATA%\\Shredder\\Logs"
                    : loggingOpts.OutputDirectory);
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "shredder-.log");

            lc.WriteTo.File(
                path: path,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: loggingOpts.FileSizeLimitBytes,
                retainedFileCountLimit: loggingOpts.RetainedFileCountLimit,
                shared: false,
                buffered: false,
                outputTemplate: outputTemplate);
        }
        catch
        {
            // 文件 sink 初始化失败(权限不足、磁盘满、路径非法等)时静默降级,
            // Debug/Console sink 仍然可用,不影响应用启动。
        }
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "WPF 退出阶段:必须吞掉所有 Stop/Dispose 异常,否则会导致进程以非零码退出,影响包管理器/任务计划程序判定。")]
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                using (_host)
                {
                    await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
                }
            }
            catch
            {
                // 退出阶段任何异常都不阻挡进程结束
            }
        }

        try { Log.CloseAndFlush(); } catch { /* Serilog 关闭失败不阻挡退出 */ }

        base.OnExit(e);
    }
}

/// <summary>WPF-UI NavigationView 通过这个 PageService 从 DI 解析具体的 Page 类型。</summary>
internal sealed class ServiceLocatorPageProvider : IPageService
{
    private readonly IServiceProvider _services;
    public ServiceLocatorPageProvider(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public T? GetPage<T>() where T : class
    {
        if (!typeof(FrameworkElement).IsAssignableFrom(typeof(T)))
            throw new InvalidOperationException($"页面类型 {typeof(T).FullName} 必须继承自 FrameworkElement。");
        return _services.GetService(typeof(T)) as T;
    }

    public FrameworkElement? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);
        if (!typeof(FrameworkElement).IsAssignableFrom(pageType))
            throw new InvalidOperationException($"页面类型 {pageType.FullName} 必须继承自 FrameworkElement。");
        return _services.GetService(pageType) as FrameworkElement;
    }
}
