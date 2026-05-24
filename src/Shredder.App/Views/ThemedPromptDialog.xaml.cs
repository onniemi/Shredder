using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Shredder.App.Views;

public partial class ThemedPromptDialog : Window
{
    private ThemedPromptDialog()
    {
        InitializeComponent();
    }

    public static bool Confirm(Window owner, string title, string message, string primaryText, string secondaryText)
    {
        var dialog = Create(owner, title, message, SymbolRegular.Warning24, primaryText, secondaryText);
        return dialog.ShowDialog() == true;
    }

    public static void Alert(Window owner, string title, string message, string buttonText = "确定")
    {
        var dialog = Create(owner, title, message, SymbolRegular.Info24, buttonText, null);
        dialog.Width = 320;
        _ = dialog.ShowDialog();
    }

    private static ThemedPromptDialog Create(
        Window owner,
        string title,
        string message,
        SymbolRegular symbol,
        string primaryText,
        string? secondaryText)
    {
        var dialog = new ThemedPromptDialog
        {
            Owner = owner,
            Title = title,
        };
        dialog.TitleText.Text = title;
        dialog.WindowTitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.DialogIcon.Symbol = symbol;
        dialog.PrimaryButton.Content = primaryText;

        if (string.IsNullOrWhiteSpace(secondaryText))
        {
            dialog.SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.SecondaryButton.Content = secondaryText;
        }

        return dialog;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
