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
        await _vm.EmptyAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();
}
