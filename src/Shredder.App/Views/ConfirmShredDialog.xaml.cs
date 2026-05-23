using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using Shredder.Core.Configuration;
using Wpf.Ui.Controls;

namespace Shredder.App.Views;

/// <summary>
/// 「粉碎不可逆」二次确认对话框。
/// 要求用户同时满足两个条件才能点确认按钮:
/// <list type="number">
///   <item>在输入框中精确输入配置中的关键字(默认「粉碎」)</item>
///   <item>等待冷静期倒计时结束(默认 5 秒)</item>
/// </list>
/// 这两个条件来自 <see cref="ShredderUiOptions"/>,可由用户调高/调低。
/// </summary>
public partial class ConfirmShredDialog : FluentWindow, INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsRemaining;
    private bool _keywordMatched;
    private string _hintText = string.Empty;

    public string RequiredKeyword { get; }
    public string HintText
    {
        get => _hintText;
        private set { _hintText = value; OnPropertyChanged(); }
    }

    public ConfirmShredDialog(IOptions<ShredderOptions> options, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        var ui = options.Value.Ui;
        RequiredKeyword = string.IsNullOrWhiteSpace(ui.ConfirmationKeyword) ? "粉碎" : ui.ConfirmationKeyword;
        _secondsRemaining = Math.Max(0, ui.ConfirmationCooldownSeconds);

        DataContext = this;
        InitializeComponent();

        PathList.ItemsSource = paths;
        UpdateHint();

        _timer.Tick += OnTimerTick;
        if (_secondsRemaining > 0) _timer.Start();
        else UpdateConfirmEnabled();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining = Math.Max(0, _secondsRemaining - 1);
        UpdateHint();
        if (_secondsRemaining == 0)
        {
            _timer.Stop();
            UpdateConfirmEnabled();
        }
    }

    private void OnKeywordChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _keywordMatched = string.Equals(KeywordBox.Text, RequiredKeyword, StringComparison.Ordinal);
        UpdateHint();
        UpdateConfirmEnabled();
    }

    private void UpdateHint()
    {
        if (!_keywordMatched)
            HintText = $"请输入「{RequiredKeyword}」";
        else if (_secondsRemaining > 0)
            HintText = $"冷静一下…{_secondsRemaining} 秒后可继续";
        else
            HintText = "✓ 已具备粉碎条件";
    }

    private void UpdateConfirmEnabled()
    {
        ConfirmButton.IsEnabled = _keywordMatched && _secondsRemaining == 0;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
