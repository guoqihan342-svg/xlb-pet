using System.Windows;
using System.Windows.Input;

namespace LubanDesktopPet;

public readonly record struct CuteConfirmationResult(
    bool Confirmed,
    bool SuppressForSession);

public partial class CuteConfirmationWindow : Window
{
    public CuteConfirmationWindow(
        string title,
        string message,
        string confirmText = "确认",
        bool showSessionSuppression = true)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        SessionSuppressionCheckBox.Visibility =
            showSessionSuppression
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    public bool Confirmed { get; private set; }

    public bool SuppressForSession =>
        SessionSuppressionCheckBox.IsChecked == true;

    public static CuteConfirmationResult ShowFor(
        Window owner,
        string title,
        string message,
        string confirmText = "删除",
        bool showSessionSuppression = true)
    {
        var dialog = new CuteConfirmationWindow(
            title,
            message,
            confirmText,
            showSessionSuppression)
        {
            Owner = owner
        };
        var confirmed = dialog.ShowDialog() == true;
        return new CuteConfirmationResult(
            confirmed,
            confirmed && dialog.SuppressForSession);
    }

    private void ConfirmButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        e.Handled = true;
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        Confirmed = false;
        DialogResult = false;
        e.Handled = true;
    }
}
