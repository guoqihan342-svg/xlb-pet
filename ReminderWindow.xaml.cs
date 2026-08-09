using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class ReminderWindow : Window
{
    private const double DefaultWidth = 372;
    private const double PreferredPagedHeight = 468;
    private const double WorkAreaInset = 12;
    private const int AdjacentWindowGapPixels = 6;
    private const int ReminderItemsPerPage = 5;

    private readonly OwnedWindowPositioner.PositionCache _positionCache;
    private readonly Action _repositionAction;
    private readonly List<string> _presentationEntries = [];
    private Window? _anchor;
    private FrameworkElement? _placementAnchor;
    private string _presentationTitle = string.Empty;
    private long _presentationOverflowCount = -1;
    private string _renderedPageContent = string.Empty;
    private int _currentPageIndex;
    private bool _allowClose;
    private bool _dismissRequestPending;
    private bool _hasClosed;
    private bool _repositionQueued;

    public ReminderWindow()
    {
        InitializeComponent();
        WindowChromeAppearance.ExcludeFromAltTab(this);
        _positionCache = new OwnedWindowPositioner.PositionCache(this);
        _repositionAction = RepositionNow;
    }

    public event EventHandler? AcknowledgeRequested;

    public event EventHandler? DismissRequested;

    public void SetPresentation(
        string? title,
        IReadOnlyList<string>? entries,
        long overflowCount)
    {
        overflowCount = Math.Max(0L, overflowCount);

        var presentationTitle = string.IsNullOrWhiteSpace(title)
            ? "小鲁班提醒"
            : title.Trim();
        var presentationEntries = NormalizeEntries(entries);

        var titleChanged = !string.Equals(
            _presentationTitle,
            presentationTitle,
            StringComparison.Ordinal);
        var entriesChanged = !EntriesEqual(
            _presentationEntries,
            presentationEntries);
        var overflowChanged =
            _presentationOverflowCount != overflowCount;
        if (!titleChanged && !entriesChanged && !overflowChanged)
        {
            return;
        }

        if (titleChanged)
        {
            _presentationTitle = presentationTitle;
            Title = presentationTitle;
            ReminderTitleText.Text = presentationTitle;
        }

        if (entriesChanged)
        {
            var preserveCurrentPage = HasStablePrefix(
                _presentationEntries,
                presentationEntries);
            _presentationEntries.Clear();
            _presentationEntries.AddRange(presentationEntries);
            if (!preserveCurrentPage)
            {
                _currentPageIndex = 0;
            }
        }

        _presentationOverflowCount = overflowCount;
        ClampCurrentPage();
        RefreshPresentationChrome();
        RenderCurrentPage();
    }

    private static List<string> NormalizeEntries(
        IReadOnlyList<string>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            normalized.Add(entries[index] ?? string.Empty);
        }

        return normalized;
    }

    private static bool EntriesEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                    left[index],
                    right[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasStablePrefix(
        IReadOnlyList<string> previousEntries,
        IReadOnlyList<string> nextEntries)
    {
        if (previousEntries.Count == 0 || nextEntries.Count == 0)
        {
            return previousEntries.Count == nextEntries.Count;
        }

        var sharedLength = Math.Min(
            previousEntries.Count,
            nextEntries.Count);
        for (var index = 0; index < sharedLength; index++)
        {
            if (!string.Equals(
                    previousEntries[index],
                    nextEntries[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public void ShowBeside(Window anchor) =>
        ShowBeside(anchor, anchor);

    public void ShowBeside(
        Window anchor,
        FrameworkElement placementAnchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(placementAnchor);

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowBeside(anchor, placementAnchor));
            return;
        }

        AttachAnchor(anchor, placementAnchor);
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
        _presentationEntries.Clear();
        _presentationOverflowCount = -1;
        _renderedPageContent = string.Empty;
        _currentPageIndex = 0;
        ReminderTitleText.Text = "小鲁班提醒";
        ReminderCountText.Text = "0 条提醒";
        ReminderContentTextBox.Clear();
        ReminderPageText.Text = "第 1 / 1 页";
        ReminderPreviousPageButton.IsEnabled = false;
        ReminderNextPageButton.IsEnabled = false;
        ReminderPagingPanel.Visibility = Visibility.Collapsed;
        ReminderAcknowledgeButton.Content = "知道啦";
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

    private int PageCount =>
        Math.Max(
            1,
            (_presentationEntries.Count + ReminderItemsPerPage - 1) /
            ReminderItemsPerPage);

    private void ClampCurrentPage()
    {
        _currentPageIndex = Math.Clamp(
            _currentPageIndex,
            0,
            PageCount - 1);
    }

    private void RefreshPresentationChrome()
    {
        var entryCount = _presentationEntries.Count;
        ReminderCountText.Text = _presentationOverflowCount > 0
            ? $"本批 {entryCount} 条，另有 {_presentationOverflowCount} 条稍后显示"
            : $"{entryCount} 条提醒";

        var pageCount = PageCount;
        ReminderPageText.Text = entryCount == 0
            ? "第 1 / 1 页"
            : $"第 {_currentPageIndex + 1} / {pageCount} 页";
        ReminderPreviousPageButton.IsEnabled =
            entryCount > 0 && _currentPageIndex > 0;
        ReminderNextPageButton.IsEnabled =
            entryCount > 0 && _currentPageIndex < pageCount - 1;
        ReminderPagingPanel.Visibility = pageCount > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReminderAcknowledgeButton.Content =
            pageCount > 1 || _presentationOverflowCount > 0
                ? "本批都知道啦"
                : "知道啦";
    }

    private void RenderCurrentPage()
    {
        var startIndex = _currentPageIndex * ReminderItemsPerPage;
        var itemCount = Math.Min(
            ReminderItemsPerPage,
            Math.Max(0, _presentationEntries.Count - startIndex));
        var pageContent = itemCount == 0
            ? string.Empty
            : string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                _presentationEntries.GetRange(startIndex, itemCount));
        if (string.Equals(
                _renderedPageContent,
                pageContent,
                StringComparison.Ordinal))
        {
            return;
        }

        _renderedPageContent = pageContent;
        ReminderContentTextBox.Text = pageContent;
        ReminderContentTextBox.Select(0, 0);
        ReminderContentTextBox.ScrollToHome();
    }

    private void PreviousPageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_currentPageIndex > 0)
        {
            _currentPageIndex--;
            RefreshPresentationChrome();
            RenderCurrentPage();
        }

        e.Handled = true;
    }

    private void NextPageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_currentPageIndex < PageCount - 1)
        {
            _currentPageIndex++;
            RefreshPresentationChrome();
            RenderCurrentPage();
        }

        e.Handled = true;
    }

    private void AttachAnchor(
        Window anchor,
        FrameworkElement placementAnchor)
    {
        if (ReferenceEquals(_anchor, anchor) &&
            ReferenceEquals(_placementAnchor, placementAnchor))
        {
            return;
        }

        DetachAnchor();
        _anchor = anchor;
        _placementAnchor = placementAnchor;
        _anchor.LocationChanged += Anchor_GeometryChanged;
        _anchor.SizeChanged += Anchor_SizeChanged;
        _anchor.StateChanged += Anchor_GeometryChanged;
        _anchor.DpiChanged += Anchor_DpiChanged;
        if (!ReferenceEquals(_placementAnchor, _anchor))
        {
            _placementAnchor.SizeChanged += PlacementAnchor_SizeChanged;
        }

        _positionCache.InvalidateGeometry();
    }

    private void DetachAnchor()
    {
        if (_anchor is null)
        {
            _placementAnchor = null;
            return;
        }

        if (_placementAnchor is not null &&
            !ReferenceEquals(_placementAnchor, _anchor))
        {
            _placementAnchor.SizeChanged -= PlacementAnchor_SizeChanged;
        }

        _anchor.LocationChanged -= Anchor_GeometryChanged;
        _anchor.SizeChanged -= Anchor_SizeChanged;
        _anchor.StateChanged -= Anchor_GeometryChanged;
        _anchor.DpiChanged -= Anchor_DpiChanged;
        _anchor = null;
        _placementAnchor = null;
        _positionCache.InvalidateGeometry();
    }

    private void ApplyAnchorWorkAreaSize()
    {
        if (_anchor is null)
        {
            ApplyWindowSize(
                DefaultWidth,
                PreferredPagedHeight,
                PreferredPagedHeight);
            return;
        }

        var workArea = _placementAnchor is not null
            ? MonitorWorkArea.GetForVisual(_anchor, _placementAnchor)
            : MonitorWorkArea.GetForWindow(_anchor);
        var usableWidth = Math.Max(1, workArea.Width - WorkAreaInset * 2);
        var usableHeight = Math.Max(1, workArea.Height - WorkAreaInset * 2);

        ApplyWindowSize(
            Math.Min(DefaultWidth, usableWidth),
            Math.Min(PreferredPagedHeight, usableHeight),
            Math.Min(PreferredPagedHeight, usableHeight));
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
        var placementAnchor = _placementAnchor;
        if (anchor is null ||
            placementAnchor is null ||
            !IsVisible ||
            !anchor.IsLoaded ||
            !placementAnchor.IsLoaded)
        {
            return;
        }

        ApplyAnchorWorkAreaSize();
        UpdateLayout();

        if (TryShouldPlaceOnLeft(placementAnchor, out var placeOnLeft))
        {
            if (OwnedWindowPositioner.TryPosition(
                    placementAnchor,
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
                placementAnchor,
                this,
                _positionCache,
                out var initiallyOnLeft,
                preferredChildIsOnLeft: false))
        {
            return;
        }

        if (TryShouldPlaceOnLeft(
                placementAnchor,
                out placeOnLeft) &&
            placeOnLeft)
        {
            if (OwnedWindowPositioner.TryPosition(
                    placementAnchor,
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
        FrameworkElement anchor,
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

    private void PlacementAnchor_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
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

    private void CloseButton_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
        {
            e.Handled = true;
        }
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
        if (!e.IsRepeat)
        {
            RequestDismiss(deferUntilClosingCompletes: false);
        }
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
                // Keep the guard through the current routed-input dispatch so
                // a physical double-click cannot acknowledge the next batch.
                // Reset at Input priority (rather than Background), otherwise
                // continuous rendering can starve the reset and swallow the
                // next deliberate close click.
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
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
