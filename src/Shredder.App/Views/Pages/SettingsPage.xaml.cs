using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shredder.App.ViewModels;

namespace Shredder.App.Views.Pages;

/// <summary>
/// 设置页。承载 SettingsPageViewModel,负责持久化到 appsettings.json。
/// </summary>
public partial class SettingsPage : Page
{
    private readonly SettingsPageViewModel _vm;

    public SettingsPage(SettingsPageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
    }

    [SuppressMessage("Design", "CA1031:DoNotCatchGeneralExceptionTypes",
        Justification = "UI 命令边界:保存配置失败需要弹窗给用户而非崩溃 Dispatcher,任何异常都必须被吞掉再展示。")]
    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.SaveAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"保存设置失败:\n{ex.Message}",
                "Shredder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnInstallShellMenu(object sender, RoutedEventArgs e)
        => _vm.InstallShellMenu();

    private void OnUninstallShellMenu(object sender, RoutedEventArgs e)
        => _vm.UninstallShellMenu();

    private void OnRefreshShellMenu(object sender, RoutedEventArgs e)
        => _vm.RefreshShellMenuStatus();

    private void OnSettingsMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SettingsScrollViewer.ScrollToVerticalOffset(SettingsScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
