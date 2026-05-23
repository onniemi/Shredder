using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Shredder.App.ViewModels;
using Shredder.Core.Configuration;
using Shredder.Core.Models;
using Shredder.Core.Security;

namespace Shredder.App.Views.Pages;

/// <summary>
/// 单文件 / 目录粉碎页。承载 ShredPageViewModel 的拖放、添加、开始、取消等交互。
/// </summary>
public partial class ShredPage : Page
{
    private readonly ShredPageViewModel _vm;
    private readonly IOptions<ShredderOptions> _options;
    private readonly PathSafetyGuard _guard;

    public ShredPage(ShredPageViewModel vm, IOptions<ShredderOptions> options, PathSafetyGuard guard)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(guard);
        _vm = vm;
        _options = options;
        _guard = guard;
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
            AddPaths(paths);
        }
    }

    public void AddPathFromShellDrop(string path) => _vm.AddPath(path);

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            _vm.AddPath(path);
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

        var warnings = new List<string>();
        foreach (var path in pending)
        {
            var decision = _guard.Evaluate(path);
            if (decision.Level == PathSafetyGuard.PathSafetyLevel.Forbidden)
            {
                MessageBox.Show(
                    $"{path}\n\n{decision.Reason}",
                    "已阻止高风险目标",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (decision.Level == PathSafetyGuard.PathSafetyLevel.Warn)
            {
                warnings.Add($"{path}\n{decision.Reason}");
            }
        }

        if (warnings.Count > 0)
        {
            var dlg = new ConfirmShredDialog(_options, warnings) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
        }

        await _vm.RunAsync();
    }

    private void OnRemoveJobClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShredJobRow row })
        {
            _vm.Remove(row);
        }
    }

    private void OnOpenLastReportClick(object sender, RoutedEventArgs e) => _vm.ShowHistory();

    private void OnBackFromHistoryClick(object sender, RoutedEventArgs e) => _vm.HideHistory();

    private void OnClearHistoryClick(object sender, RoutedEventArgs e) => _vm.ClearHistory();

    private void OnRefreshHistoryClick(object sender, RoutedEventArgs e) => _vm.RefreshHistory();
}
