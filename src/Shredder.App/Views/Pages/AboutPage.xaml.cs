using System.Windows;
using System.Windows.Controls;
using Shredder.App.ViewModels;

namespace Shredder.App.Views.Pages;

/// <summary>
/// 关于页:展示应用版本、许可与第三方组件,并提供「导出诊断包」入口。
/// </summary>
public partial class AboutPage : Page
{
    private readonly AboutPageViewModel _vm;

    public AboutPage(AboutPageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
    }

    private void OnOpenRepoClick(object sender, RoutedEventArgs e) => AboutPageViewModel.OpenUrl(_vm.Repository);

    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
        => await _vm.ExportDiagnosticsAsync();

    private void OnOpenDiagnosticsFolderClick(object sender, RoutedEventArgs e) => _vm.OpenDiagnosticsFolder();

    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e) => _vm.OpenLogsFolder();

    private void OnOpenLastDiagnosticsClick(object sender, RoutedEventArgs e) => _vm.OpenLastDiagnostics();
}
