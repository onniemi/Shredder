using System.Windows;
using System.Windows.Controls;
using Shredder.App.ViewModels;

namespace Shredder.App.Views.Pages;

/// <summary>
/// 空闲空间擦除页。承载 FreeSpacePageViewModel。
/// </summary>
public partial class FreeSpacePage : Page
{
    private readonly FreeSpacePageViewModel _vm;

    public FreeSpacePage(FreeSpacePageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _vm.RefreshDrives();

    private async void OnWipeClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedDrive is null)
        {
            MessageBox.Show("请先选择目标分区。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _vm.WipeAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();
}
