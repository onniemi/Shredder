using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Wpf.Ui.Controls;
using Shredder.App.Views.Pages;

namespace Shredder.App.Views;

/// <summary>
/// 极简主窗口:不再使用导航壳,启动后直接呈现一键粉碎工作台。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly ShredPage _shredPage;
    private readonly FreeSpacePage _freeSpacePage;
    private readonly RecycleBinPage _recycleBinPage;
    private readonly SettingsPage _settingsPage;
    private readonly AboutPage _aboutPage;

    public MainWindow(
        ShredPage shredPage,
        FreeSpacePage freeSpacePage,
        RecycleBinPage recycleBinPage,
        SettingsPage settingsPage,
        AboutPage aboutPage)
    {
        ArgumentNullException.ThrowIfNull(shredPage);
        ArgumentNullException.ThrowIfNull(freeSpacePage);
        ArgumentNullException.ThrowIfNull(recycleBinPage);
        ArgumentNullException.ThrowIfNull(settingsPage);
        ArgumentNullException.ThrowIfNull(aboutPage);
        _shredPage = shredPage;
        _freeSpacePage = freeSpacePage;
        _recycleBinPage = recycleBinPage;
        _settingsPage = settingsPage;
        _aboutPage = aboutPage;
        InitializeComponent();
#if SIMPLE_UI
        FullShell.Visibility = Visibility.Collapsed;
        SimpleFrame.Visibility = Visibility.Visible;
        SimpleFrame.Content = _shredPage;
#else
        ShowPage(_shredPage);
#endif
        SourceInitialized += OnSourceInitialized;
    }

    public void AddStartupPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var added = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            _shredPage.AddPathFromShellDrop(path);
            added = true;
        }

        if (!added) return;

#if SIMPLE_UI
        SimpleFrame.Content = _shredPage;
#else
        ShowPage(_shredPage);
#endif
    }

    private void OnShowShredClick(object sender, RoutedEventArgs e) => ShowPage(_shredPage);

    private void OnShowFreeSpaceClick(object sender, RoutedEventArgs e) => ShowPage(_freeSpacePage);

    private void OnShowRecycleBinClick(object sender, RoutedEventArgs e) => ShowPage(_recycleBinPage);

    private void OnShowSettingsClick(object sender, RoutedEventArgs e) => ShowPage(_settingsPage);

    private void OnShowAboutClick(object sender, RoutedEventArgs e) => ShowPage(_aboutPage);

    private void ShowPage(Page page) => MainFrame.Content = page;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        if (source is null) return;

        var hwnd = source.Handle;
        NativeDropMethods.DragAcceptFiles(hwnd, true);
        NativeDropMethods.ChangeWindowMessageFilterEx(hwnd, NativeDropMethods.WM_DROPFILES, NativeDropMethods.MSGFLT_ALLOW, IntPtr.Zero);
        NativeDropMethods.ChangeWindowMessageFilterEx(hwnd, NativeDropMethods.WM_COPYDATA, NativeDropMethods.MSGFLT_ALLOW, IntPtr.Zero);
        NativeDropMethods.ChangeWindowMessageFilterEx(hwnd, NativeDropMethods.WM_COPYGLOBALDATA, NativeDropMethods.MSGFLT_ALLOW, IntPtr.Zero);
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeDropMethods.WM_DROPFILES) return IntPtr.Zero;

        foreach (var path in ReadDroppedPaths(wParam))
        {
            _shredPage.AddPathFromShellDrop(path);
        }

#if SIMPLE_UI
        SimpleFrame.Content = _shredPage;
#else
        ShowPage(_shredPage);
#endif
        handled = true;
        return IntPtr.Zero;
    }

    private static IEnumerable<string> ReadDroppedPaths(IntPtr dropHandle)
    {
        try
        {
            var count = NativeDropMethods.DragQueryFileW(dropHandle, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var length = NativeDropMethods.DragQueryFileW(dropHandle, i, null, 0);
                if (length == 0) continue;

                var buffer = new char[(int)length + 1];
                var copied = NativeDropMethods.DragQueryFileW(dropHandle, i, buffer, (uint)buffer.Length);
                if (copied == 0) continue;

                var path = new string(buffer, 0, (int)copied);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }
        finally
        {
            NativeDropMethods.DragFinish(dropHandle);
        }
    }

    private static class NativeDropMethods
    {
        public const int WM_DROPFILES = 0x0233;
        public const int WM_COPYDATA = 0x004A;
        public const int WM_COPYGLOBALDATA = 0x0049;
        public const uint MSGFLT_ALLOW = 1;

        [DllImport("shell32.dll")]
        public static extern void DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFileW(
            IntPtr hDrop,
            uint iFile,
            char[]? lpszFile,
            uint cch);

        [DllImport("shell32.dll")]
        public static extern void DragFinish(IntPtr hDrop);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeWindowMessageFilterEx(
            IntPtr hWnd,
            uint message,
            uint action,
            IntPtr pChangeFilterStruct);
    }
}
