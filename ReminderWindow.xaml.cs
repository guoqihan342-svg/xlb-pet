using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class ReminderWindow : Window
{
    private const double DefaultWidth = 372;
    private const double MinimumUsableHeight = 210;
    private const double MaximumPreferredHeight = 4096;
    private const double WorkAreaInset = 12;
    private const int AdjacentWindowGapPixels = 6;
    private const double FixedContentHeight = 158;
    private const double EstimatedTextLineHeight = 21;
    private const int EstimatedCharactersPerLine = 28;
    private const int MaximumPreferredTextLines = 300;

    private readonly OwnedWindowPositioner.PositionCache _positionCache;
    private readonly Action _repositionAction;
    private Window? _anchor;
    private double _preferredHeight = 360;
    private string _presentationTitle = string.Empty;
    private string _presentationContent = string.Empty;
    private int _presentationVisibleCount = -1;
    private long _presentationOverflowCount = -1;
    private bool _allowClose;
    private bool _dismissRequestPending;
    private bool _hasClosed;
    private bool _repositionQueued;

    public ReminderWindow()
    {
        InitializeComponent();
        _positionCache = new OwnedWindowPositioner.PositionCache(this);
        _repositionAction = RepositionNow;
    }

    public event EventHandler? AcknowledgeRequested;

    public event EventHandler? DismissRequested;

    public void SetPresentation(
        string? title,
        string? content,
        int visibleCount,
        long overflowCount)
    {
        visibleCount = Math.Max(0, visibleCount);
        overflowCount = Math.Max(0L, overflowCount);

        var presentationTitle = string.IsNullOrWhiteSpace(title)
            ? "小鲁班提醒"
            : title.Trim();
        var presentationContent = content ?? string.Empty;

        var titleChanged = !string.Equals(
            _presentationTitle,
            presentationTitle,
            StringComparison.Ordinal);
        var contentChanged = !string.Equals(
            _presentationContent,
            presentationContent,
            StringComparison.Ordinal);
        var countChanged =
            _presentationVisibleCount != visibleCount ||
            _presentationOverflowCount != overflowCount;
        if (!titleChanged && !contentChanged && !countChanged)
        {
            return;
        }

        if (titleChanged)
        {
            _presentationTitle = presentationTitle;
            Title = presentationTitle;
            ReminderTitleText.Text = presentationTitle;
        }

        if (countChanged)
        {
            _presentationVisibleCount = visibleCount;
            _presentationOverflowCount = overflowCount;
            ReminderCountText.Text = overflowCount > 0
                ? $"当前 {visibleCount} 条，另有 {overflowCount} 条"
                : $"{visibleCount} 条提醒";
        }

        if (contentChanged)
        {
            _presentationContent = presentationContent;
            ReminderContentTextBox.Text = presentationContent;
            ReminderContentTextBox.Select(0, 0);
            ReminderContentTextBox.ScrollToHome();
        }

        if (contentChanged || countChanged)
        {
            _preferredHeight = CalculatePreferredHeight(
                presentationContent,
                visibleCount);
            ApplyAnchorWorkAreaSize();
        }

        if (IsVisible && (contentChanged || countChanged))
        {
            ScheduleReposition();
        }
    }

    public void ShowBeside(Window anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowBeside(anchor));
            return;
        }

        AttachAnchor(anchor);
        ApplyAnchorWorkAreaSize();

        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        RepositionNow();
        Opacity = 1;
    }

    public void HideSafely()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(HideSafely));
            return;
        }

        _repositionQueued = false;
        DetachAnchor();
        if (IsVisible)
        {
            Hide();
        }

        Opacity = 1;
    }

    public void ClearPresentation()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted &&
                !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(ClearPresentation));
            }

            return;
        }

        _presentationTitle = string.Empty;
        _presentationContent = string.Empty;
        _presentationVisibleCount = -1;
        _presentationOverflowCount = -1;
        _preferredHeight = 360;
        ReminderTitleText.Text = "小鲁班提醒";
        ReminderCountText.Text = "0 条提醒";
        ReminderContentTextBox.Clear();
    }

    public void CloseForApplication()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(CloseForApplication);
            }

            return;
        }

        if (_hasClosed)
        {
            return;
        }

        _allowClose = true;
        DetachAnchor();
        Close();
    }

    private static double CalculatePreferredHeight(
        string content,
        int visibleCount)
    {
        var explicitLines = 1;
        foreach (var character in content)
        {
            if (character == '\n')
            {
                explicitLines++;
            }
        }

        var wrappedLines = Math.Max(
            explicitLines,
            (content.Length + EstimatedCharactersPerLine - 1) /
            EstimatedCharactersPerLine);
        var itemLines = Math.Max(1, visibleCount) * 2;
        var preferredLines = Math.Clamp(
            Math.Max(wrappedLines, itemLines),
            4,
            MaximumPreferredTextLines);
        return Math.Clamp(
            FixedContentHeight + preferredLines * EstimatedTextLineHeight,
            MinimumUsableHeight,
            MaximumPreferredHeight);
    }

    private void AttachAnchor(Window anchor)
    {
        if (ReferenceEquals(_anchor, anchor))
        {
            return;
        }

        DetachAnchor();
        _anchor = anchor;
        _anchor.LocationChanged += Anchor_GeometryChanged;
        _anchor.SizeChanged += Anchor_SizeChanged;
        _anchor.StateChanged += Anchor_GeometryChanged;
        _anchor.DpiChanged += Anchor_DpiChanged;
        _positionCache.InvalidateGeometry();
    }

    private void DetachAnchor()
    {
        if (_anchor is null)
        {
            return;
        }

        _anchor.LocationChanged -= Anchor_GeometryChanged;
        _anchor.SizeChanged -= Anchor_SizeChanged;
        _anchor.StateChanged -= Anchor_GeometryChanged;
        _anchor.DpiChanged -= Anchor_DpiChanged;
        _anchor = null;
        _positionCache.InvalidateGeometry();
    }

    private void ApplyAnchorWorkAreaSize()
    {
        if (_anchor is null)
        {
            ApplyWindowSize(
                DefaultWidth,
                _preferredHeight,
                double.PositiveInfinity);
            return;
        }

        var workArea = MonitorWorkArea.GetForWindow(_anchor);
        var usableWidth = Math.Max(1, workArea.Width - WorkAreaInset * 2);
        var usableHeight = Math.Max(1, workArea.Height - WorkAreaInset * 2);

        ApplyWindowSize(
            Math.Min(DefaultWidth, usableWidth),
            Math.Min(_preferredHeight, usableHeight),
            usableHeight);
    }

    private void ApplyWindowSize(
        double width,
        double height,
        double maximumHeight)
    {
        var geometryChanged =
            !AreClose(Width, width) ||
            !AreClose(Height, height) ||
            !AreClose(MaxHeight, maximumHeight);
        MaxHeight = maximumHeight;
        Width = width;
        Height = height;
        if (geometryChanged)
        {
            _positionCache.InvalidateGeometry();
        }
    }

    private static bool AreClose(double left, double right) =>
        left.Equals(right) || Math.Abs(left - right) < 0.1;

    private void ScheduleReposition()
    {
        if (_repositionQueued || !IsVisible)
        {
            return;
        }

        _repositionQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            _repositionAction);
    }

    private void RepositionNow()
    {
        _repositionQueued = false;
        var anchor = _anchor;
        if (anchor is null || !IsVisible || !anchor.IsLoaded)
        {
            return;
        }

        ApplyAnchorWorkAreaSize();
        UpdateLayout();

        if (TryShouldPlaceOnLeft(anchor, out var placeOnLeft))
        {
            if (OwnedWindowPositioner.TryPosition(
                    anchor,
                    this,
                    _positionCache,
                    out var childIsOnLeft,
                    preferredChildIsOnLeft: placeOnLeft))
            {
                ApplyAdjacentWindowGap(childIsOnLeft);
            }

            return;
        }

        if (!OwnedWindowPositioner.TryPosition(
                anchor,
                this,
                _positionCache,
                out var initiallyOnLeft,
                preferredChildIsOnLeft: false))
        {
            return;
        }

        if (TryShouldPlaceOnLeft(anchor, out placeOnLeft) && placeOnLeft)
        {
            if (OwnedWindowPositioner.TryPosition(
                    anchor,
                    this,
                    _positionCache,
                    out var correctedOnLeft,
                    preferredChildIsOnLeft: true))
            {
                ApplyAdjacentWindowGap(correctedOnLeft);
            }

            return;
        }

        ApplyAdjacentWindowGap(initiallyOnLeft);
    }

    private bool TryShouldPlaceOnLeft(
        Window anchor,
        out bool placeOnLeft)
    {
        placeOnLeft = false;
        if (!_positionCache._hasMonitorGeometry ||
            !_positionCache._hasChildGeometry)
        {
            return false;
        }

        try
        {
            var anchorBottomRight = anchor.PointToScreen(
                new Point(anchor.ActualWidth, anchor.ActualHeight));
            var anchorTopLeft = anchor.PointToScreen(new Point(0, 0));
            var canPlaceOnRight =
                Math.Round(anchorBottomRight.X) +
                _positionCache._childWidth +
                AdjacentWindowGapPixels <=
                _positionCache._workArea.Right;
            var canPlaceOnLeft =
                Math.Round(anchorTopLeft.X) -
                _positionCache._childWidth -
                AdjacentWindowGapPixels >=
                _positionCache._workArea.Left;
            placeOnLeft = !canPlaceOnRight && canPlaceOnLeft;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyAdjacentWindowGap(bool childIsOnLeft)
    {
        if (!_positionCache._hasMonitorGeometry ||
            !_positionCache._hasChildGeometry ||
            !double.IsFinite(Left))
        {
            return;
        }

        var physicalDelta = childIsOnLeft
            ? -AdjacentWindowGapPixels
            : AdjacentWindowGapPixels;
        var desiredPhysicalLeft =
            _positionCache._lastLeft + physicalDelta;
        var maximumPhysicalLeft = Math.Max(
            _positionCache._workArea.Left,
            _positionCache._workArea.Right -
            _positionCache._childWidth);
        if (desiredPhysicalLeft < _positionCache._workArea.Left ||
            desiredPhysicalLeft > maximumPhysicalLeft)
        {
            return;
        }

        var dpiScaleX = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left += physicalDelta /
                (double.IsFinite(dpiScaleX) && dpiScaleX > 0
                    ? dpiScaleX
                    : 1);
        _positionCache._lastLeft = desiredPhysicalLeft;
    }

    private void Anchor_GeometryChanged(object? sender, EventArgs e)
    {
        ScheduleReposition();
    }

    private void Anchor_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        ScheduleReposition();
    }

    private void Anchor_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        ScheduleReposition();
    }

    private void ReminderWindow_DpiChanged(
        object sender,
        DpiChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        ScheduleReposition();
    }

    private void AcknowledgeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        AcknowledgeRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RequestDismiss(deferUntilClosingCompletes: false);
        e.Handled = true;
    }

    private void ReminderWindow_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        RequestDismiss(deferUntilClosingCompletes: false);
    }

    private void ReminderWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        RequestDismiss(deferUntilClosingCompletes: true);
    }

    private void RequestDismiss(bool deferUntilClosingCompletes)
    {
        if (_allowClose || _hasClosed || _dismissRequestPending)
        {
            return;
        }

        _dismissRequestPending = true;
        if (deferUntilClosingCompletes)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(DispatchDismissRequest));
            return;
        }

        DispatchDismissRequest();
    }

    private void DispatchDismissRequest()
    {
        if (_allowClose || _hasClosed)
        {
            _dismissRequestPending = false;
            return;
        }

        try
        {
            if (DismissRequested is { } dismissRequested)
            {
                dismissRequested.Invoke(this, EventArgs.Empty);
            }
            else
            {
                HideSafely();
            }
        }
        finally
        {
            if (Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                _dismissRequestPending = false;
            }
            else
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => _dismissRequestPending = false));
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _hasClosed = true;
        _dismissRequestPending = false;
        DetachAnchor();
        base.OnClosed(e);
    }
}
