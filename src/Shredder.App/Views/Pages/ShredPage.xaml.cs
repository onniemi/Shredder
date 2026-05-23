using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Shredder.App.ViewModels;
using Shredder.Core.Configuration;
using Shredder.Core.Models;

namespace Shredder.App.Views.Pages;

/// <summary>
/// 单文件 / 目录粉碎页。承载 ShredPageViewModel 的拖放、添加、开始、取消等交互。
/// </summary>
public partial class ShredPage : Page
{
    private readonly ShredPageViewModel _vm;
    private readonly IOptions<ShredderOptions> _options;

    public ShredPage(ShredPageViewModel vm, IOptions<ShredderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(options);
        _vm = vm;
        _options = options;
        InitializeComponent();
        DataContext = _vm;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var p in paths) _vm.AddPath(p);
        }
    }

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, CheckFileExists = false, ValidateNames = false };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FileNames) _vm.AddPath(f);
        }
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
        {
            _vm.AddPath(dlg.FolderName);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _vm.Clear();

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        var pending = _vm.Jobs
            .Where(j => j.Status == ShredJobStatus.Pending)
            .Select(j => j.Path)
            .ToList();
        if (pending.Count == 0)
        {
            MessageBox.Show("没有待处理的项目。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new ConfirmShredDialog(_options, pending) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        await _vm.RunAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _vm.Cancel();

    private void OnOpenLastReportClick(object sender, RoutedEventArgs e) => _vm.OpenLastReport();

    private void OnOpenReportsFolderClick(object sender, RoutedEventArgs e) => _vm.OpenReportsFolder();
}
