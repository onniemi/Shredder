using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Shredder.App.ViewModels;
using Shredder.App.Views;
using Shredder.Core.Models;
using Shredder.Core.Security;

namespace Shredder.App.Views.Pages;

/// <summary>
/// 单文件 / 目录粉碎页。承载 ShredPageViewModel 的拖放、添加、开始、取消等交互。
/// </summary>
public partial class ShredPage : Page
{
    private readonly ShredPageViewModel _vm;
    private readonly PathSafetyGuard _guard;

    public ShredPage(ShredPageViewModel vm, PathSafetyGuard guard)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(guard);
        _vm = vm;
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
        if (!CanRunPendingJobs()) return;
        await _vm.RunAsync();
        ShowFailureAdviceIfNeeded();
    }

    private async void OnFastStartClick(object sender, RoutedEventArgs e)
    {
        if (!CanRunPendingJobs()) return;
        await _vm.RunFastAsync();
        ShowFailureAdviceIfNeeded();
    }

    private void ShowFailureAdviceIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_vm.LastRunFailureMessage)) return;

        var owner = Window.GetWindow(this);
        if (owner is null) return;

        var result = ThemedPromptDialog.Confirm(
            owner,
            "粉碎失败",
            _vm.LastRunFailureMessage + "\n\n是否现在以管理员身份重新打开软件？",
            "是",
            "否");

        if (result)
        {
            RestartAsAdministrator();
        }
    }

    private static void RestartAsAdministrator()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 用户取消 UAC 或系统拒绝时保持当前窗口继续可用。
        }
    }

    private bool CanRunPendingJobs()
    {
        var pending = _vm.Jobs
            .Where(j => j.Status == ShredJobStatus.Pending)
            .Select(j => j.Path)
            .ToList();
        if (pending.Count == 0)
        {
            ShowAlert("提示", "没有待处理的文件。");
            return false;
        }

        foreach (var path in pending)
        {
            var decision = _guard.Evaluate(path);
            if (decision.Level == PathSafetyGuard.PathSafetyLevel.Forbidden)
            {
                ShowAlert(
                    "已阻止高风险目标",
                    $"{path}\n\n{decision.Reason}");
                return false;
            }
        }

        return true;
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

    private void ShowAlert(string title, string message)
    {
        var owner = Window.GetWindow(this);
        if (owner is null) return;
        ThemedPromptDialog.Alert(owner, title, message);
    }
}
