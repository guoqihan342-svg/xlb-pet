using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LubanDesktopPet;

public enum CuteConfirmationTheme
{
    TodoBlue,
    ScheduledWarm
}

public readonly record struct CuteConfirmationResult(
    bool Confirmed,
    bool SuppressForSession);

public partial class CuteConfirmationWindow : Window
{
    public CuteConfirmationWindow(
        string title,
        string message,
        string confirmText = "确认",
        bool showSessionSuppression = true,
        CuteConfirmationTheme theme =
            CuteConfirmationTheme.ScheduledWarm)
    {
        InitializeComponent();
        WindowChromeAppearance.ExcludeFromAltTab(this);
        Theme = theme;
        ApplyTheme(theme);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        SessionSuppressionCheckBox.Visibility =
            showSessionSuppression
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    public CuteConfirmationTheme Theme { get; }

    public bool Confirmed { get; private set; }

    public bool SuppressForSession =>
        SessionSuppressionCheckBox.IsChecked == true;

    public static CuteConfirmationResult ShowFor(
        Window owner,
        string title,
        string message,
        string confirmText = "删除",
        bool showSessionSuppression = true,
        CuteConfirmationTheme theme =
            CuteConfirmationTheme.ScheduledWarm)
    {
        var dialog = new CuteConfirmationWindow(
            title,
            message,
            confirmText,
            showSessionSuppression,
            theme)
        {
            Owner = owner
        };
        var confirmed = dialog.ShowDialog() == true;
        return new CuteConfirmationResult(
            confirmed,
            confirmed && dialog.SuppressForSession);
    }

    private void ApplyTheme(CuteConfirmationTheme theme)
    {
        ConfirmationBadgeText.Text =
            theme == CuteConfirmationTheme.TodoBlue ? "办" : "时";
        if (theme != CuteConfirmationTheme.TodoBlue)
        {
            return;
        }

        SetColorResource(
            "ConfirmationGradientStartColor",
            "#FFFFFFFF");
        SetColorResource(
            "ConfirmationGradientMiddleColor",
            "#FFF8FBFF");
        SetColorResource(
            "ConfirmationGradientEndColor",
            "#FFEAF3FF");
        SetBrushResource("ConfirmationBorderBrush", "#8CB4F4");
        SetBrushResource(
            "ConfirmationBadgeBackgroundBrush",
            "#E7F1FF");
        SetBrushResource(
            "ConfirmationBadgeBorderBrush",
            "#A9C6EF");
        SetBrushResource(
            "ConfirmationBadgeTextBrush",
            "#4778D0");
        SetBrushResource("ConfirmationTitleBrush", "#365F9D");
        SetBrushResource(
            "ConfirmationMessageBackgroundBrush",
            "#CCFFFFFF");
        SetBrushResource(
            "ConfirmationMessageBorderBrush",
            "#C6D9F5");
        SetBrushResource(
            "ConfirmationMessageTextBrush",
            "#41566F");
        SetBrushResource(
            "ConfirmationNeutralBackgroundBrush",
            "#F2F7FF");
        SetBrushResource(
            "ConfirmationNeutralBorderBrush",
            "#C1D6F2");
        SetBrushResource(
            "ConfirmationNeutralForegroundBrush",
            "#49688F");
        SetBrushResource(
            "ConfirmationNeutralHoverBackgroundBrush",
            "#E1EDFF");
        SetBrushResource(
            "ConfirmationNeutralHoverBorderBrush",
            "#91B4E7");
        SetBrushResource("ConfirmationFocusBrush", "#3E70C6");
        SetBrushResource(
            "ConfirmationCheckTextBrush",
            "#587092");
        SetBrushResource(
            "ConfirmationCheckBackgroundBrush",
            "#F7FBFF");
        SetBrushResource(
            "ConfirmationCheckBorderBrush",
            "#B7CDEC");
        SetBrushResource(
            "ConfirmationCheckHoverBackgroundBrush",
            "#E7F1FF");
        SetBrushResource(
            "ConfirmationCheckCheckedBackgroundBrush",
            "#5B8DEF");
        SetBrushResource(
            "ConfirmationCheckCheckedBorderBrush",
            "#4778D0");
        SetBrushResource(
            "ConfirmationPrimaryBackgroundBrush",
            "#5B8DEF");
        SetBrushResource(
            "ConfirmationPrimaryBorderBrush",
            "#4778D0");
        SetBrushResource(
            "ConfirmationPrimaryHoverBackgroundBrush",
            "#4F7FD7");
        SetBrushResource(
            "ConfirmationPrimaryHoverBorderBrush",
            "#365F9D");
    }

    private void SetBrushResource(string key, string colorText)
    {
        var brush = new SolidColorBrush(ParseColor(colorText));
        brush.Freeze();
        Resources[key] = brush;
    }

    private void SetColorResource(string key, string colorText) =>
        Resources[key] = ParseColor(colorText);

    private static Color ParseColor(string colorText) =>
        (Color)ColorConverter.ConvertFromString(colorText);

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button can release between input dispatch and DragMove.
        }
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
