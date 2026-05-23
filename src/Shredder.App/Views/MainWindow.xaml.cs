using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Shredder.App.Views.Pages;

namespace Shredder.App.Views;

/// <summary>
/// 主壳:FluentWindow + NavigationView。
/// 业务页面通过 IPageService 从 DI 解析,无业务逻辑。
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow(IPageService pageService)
    {
        ArgumentNullException.ThrowIfNull(pageService);
        InitializeComponent();
        RootNavigation.SetPageService(pageService);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 默认进入"文件粉碎"页,避免启动后右侧一片空白。
        RootNavigation.Navigate(typeof(ShredPage));
    }
}

