using System.Windows;
using System.Windows.Controls;
using Shredder.App.ViewModels;

namespace Shredder.App.Views.Pages;

/// <summary>
/// 回收站清空页。承载 RecycleBinPageViewModel。
/// </summary>
public partial class RecycleBinPage : Page
{
    private readonly RecycleBinPageViewModel _vm;

    public RecycleBinPage(RecycleBinPageViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        InitializeComponent();
        DataContext = _vm;
    }

    private async void OnEmptyClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "将先粉碎所有固定盘回收站中的文件，再调用系统接口清空。该操作不可恢复，确认继续？",
            "Shredder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        await _vm.EmptyAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();
}
