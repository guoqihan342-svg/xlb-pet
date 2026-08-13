using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LubanDesktopPet;

/// <summary>
/// Retries a clipboard copy without blocking the UI thread. A newer copy
/// request, or any intervening clipboard change, permanently supersedes the
/// pending request so stale text can never overwrite the user's clipboard.
/// </summary>
internal sealed class ClipboardCopyRetry : IDisposable
{
    internal const int MaximumAttemptCount = 6;
    internal const int MaximumRetryWindowMilliseconds = 300;

    private static long s_nextRequestGeneration;
    private static long s_latestRequestGeneration;

    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _retryTimer;
    private string? _pendingText;
    private long _requestGeneration;
    private uint _clipboardSequence;
    private long _retryDeadlineTimestamp;
    private int _attemptCount;
    private int _nextRetryIndex;
    private bool _disposed;

    internal ClipboardCopyRetry(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    internal void Copy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancelPending(invalidateRequest: true);
        var requestGeneration = Interlocked.Increment(
            ref s_nextRequestGeneration);
        Volatile.Write(
            ref s_latestRequestGeneration,
            requestGeneration);

        var requestStartedTimestamp = Stopwatch.GetTimestamp();
        var sequenceBeforeCopy = GetClipboardSequenceNumber();
        if (TrySetClipboardText(text))
        {
            return;
        }

        var sequenceAfterCopy = GetClipboardSequenceNumber();
        if (!IsLatestRequest(requestGeneration) ||
            HasClipboardSequenceChanged(
                sequenceBeforeCopy,
                sequenceAfterCopy))
        {
            return;
        }

        _pendingText = text;
        _requestGeneration = requestGeneration;
        _clipboardSequence = sequenceAfterCopy;
        _retryDeadlineTimestamp = checked(
            requestStartedTimestamp +
            (long)Math.Ceiling(
                MaximumRetryWindowMilliseconds *
                (double)Stopwatch.Frequency /
                1000d));
        _attemptCount = 1;
        _nextRetryIndex = 0;
        ScheduleNextRetry();
    }

    internal void Cancel()
    {
        _dispatcher.VerifyAccess();
        CancelPending(invalidateRequest: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(Dispose));
            return;
        }

        _disposed = true;
        CancelPending(invalidateRequest: true);
        if (_retryTimer is not null)
        {
            _retryTimer.Tick -= RetryTimer_Tick;
            _retryTimer = null;
        }
    }

    internal static bool TrySetClipboardText(string text)
    {
        // WPF's OLE clipboard path can wait internally while another process
        // owns the clipboard. Probe the native lock first so the UI thread can
        // hand the request to our bounded Dispatcher retry immediately.
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        if (!CloseClipboard())
        {
            return false;
        }

        try
        {
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    internal static void InvalidatePendingCopies()
    {
        var requestGeneration = Interlocked.Increment(
            ref s_nextRequestGeneration);
        Volatile.Write(
            ref s_latestRequestGeneration,
            requestGeneration);
    }

    private void ScheduleNextRetry()
    {
        var remainingWindow = GetRemainingRetryWindow();
        if (_pendingText is null ||
            _attemptCount >= MaximumAttemptCount ||
            _nextRetryIndex >= MaximumAttemptCount - 1 ||
            remainingWindow <= TimeSpan.Zero)
        {
            CancelPending(invalidateRequest: true);
            return;
        }

        _retryTimer ??= CreateRetryTimer();
        _retryTimer.Interval = TimeSpan.FromTicks(Math.Min(
            GetRetryDelay(_nextRetryIndex++).Ticks,
            remainingWindow.Ticks));
        _retryTimer.Start();
    }

    private DispatcherTimer CreateRetryTimer()
    {
        var timer = new DispatcherTimer(
            DispatcherPriority.Background,
            _dispatcher);
        timer.Tick += RetryTimer_Tick;
        return timer;
    }

    private void RetryTimer_Tick(object? sender, EventArgs e)
    {
        _retryTimer?.Stop();
        if (_disposed)
        {
            return;
        }

        if (GetRemainingRetryWindow() <= TimeSpan.Zero)
        {
            CancelPending(invalidateRequest: true);
            return;
        }

        var text = _pendingText;
        var requestGeneration = _requestGeneration;
        if (text is null ||
            !IsLatestRequest(requestGeneration) ||
            HasClipboardSequenceChanged(
                _clipboardSequence,
                GetClipboardSequenceNumber()))
        {
            CancelPending(invalidateRequest: true);
            return;
        }

        _attemptCount++;
        var sequenceBeforeCopy = _clipboardSequence;
        if (TrySetClipboardText(text))
        {
            CancelPending(invalidateRequest: false);
            return;
        }

        var sequenceAfterCopy = GetClipboardSequenceNumber();
        if (!IsLatestRequest(requestGeneration) ||
            HasClipboardSequenceChanged(
                sequenceBeforeCopy,
                sequenceAfterCopy))
        {
            CancelPending(invalidateRequest: true);
            return;
        }

        _clipboardSequence = sequenceAfterCopy;
        ScheduleNextRetry();
    }

    private void CancelPending(bool invalidateRequest)
    {
        _retryTimer?.Stop();
        if (invalidateRequest && _requestGeneration != 0)
        {
            Interlocked.CompareExchange(
                ref s_latestRequestGeneration,
                0,
                _requestGeneration);
        }

        _pendingText = null;
        _requestGeneration = 0;
        _clipboardSequence = 0;
        _retryDeadlineTimestamp = 0;
        _attemptCount = 0;
        _nextRetryIndex = 0;
    }

    private static bool IsLatestRequest(long requestGeneration) =>
        requestGeneration != 0 &&
        Volatile.Read(ref s_latestRequestGeneration) == requestGeneration;

    private static bool HasClipboardSequenceChanged(
        uint expected,
        uint current) =>
        expected != 0 && current != expected;

    private static TimeSpan GetRetryDelay(int retryIndex) =>
        retryIndex switch
        {
            0 => TimeSpan.FromMilliseconds(20),
            1 => TimeSpan.FromMilliseconds(40),
            2 => TimeSpan.FromMilliseconds(60),
            3 => TimeSpan.FromMilliseconds(80),
            _ => TimeSpan.FromMilliseconds(100)
        };

    private TimeSpan GetRemainingRetryWindow()
    {
        if (_retryDeadlineTimestamp <= 0)
        {
            return TimeSpan.Zero;
        }

        var remainingStopwatchTicks =
            _retryDeadlineTimestamp - Stopwatch.GetTimestamp();
        if (remainingStopwatchTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(
            remainingStopwatchTicks / (double)Stopwatch.Frequency);
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
}

internal static class TextClipboardCommands
{
    internal static TextBox? ResolveTextBox(
        object? sender,
        RoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return ResolveTextBox(e.OriginalSource) ??
               ResolveTextBox(e.Source) ??
               ResolveTextBox(sender) ??
               ResolveTextBox(Keyboard.FocusedElement);
    }

    internal static bool CanCut(TextBox textBox) =>
        textBox.IsEnabled &&
        !textBox.IsReadOnly &&
        textBox.SelectionLength > 0;

    internal static bool TryCutSelectedText(TextBox textBox)
    {
        if (!CanCut(textBox))
        {
            return false;
        }

        var selectionStart = textBox.SelectionStart;
        var selectionLength = textBox.SelectionLength;
        var textSnapshot = textBox.Text;
        var selectedText = textBox.SelectedText;
        if (selectedText.Length == 0)
        {
            return false;
        }

        // Cut is intentionally synchronous and fail-closed: never schedule a
        // later deletion after TSF/IME has had an opportunity to move or alter
        // the selection.
        ClipboardCopyRetry.InvalidatePendingCopies();
        if (!ClipboardCopyRetry.TrySetClipboardText(selectedText))
        {
            return false;
        }

        if (textBox.IsReadOnly ||
            !string.Equals(
                textBox.Text,
                textSnapshot,
                StringComparison.Ordinal) ||
            selectionStart < 0 ||
            selectionLength <= 0 ||
            selectionStart > textSnapshot.Length - selectionLength ||
            !string.Equals(
                textSnapshot.Substring(selectionStart, selectionLength),
                selectedText,
                StringComparison.Ordinal))
        {
            return false;
        }

        textBox.Select(selectionStart, selectionLength);
        textBox.SelectedText = string.Empty;
        textBox.Select(selectionStart, 0);
        return true;
    }

    internal static bool IsAsciiDigitsPaste(
        DataObjectPastingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        try
        {
            var dataObject = e.DataObject;
            var text = dataObject.GetDataPresent(
                    DataFormats.UnicodeText,
                    autoConvert: true)
                ? dataObject.GetData(
                    DataFormats.UnicodeText,
                    autoConvert: true) as string
                : dataObject.GetDataPresent(
                    DataFormats.Text,
                    autoConvert: true)
                    ? dataObject.GetData(
                        DataFormats.Text,
                        autoConvert: true) as string
                    : null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var character in text)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static TextBox? ResolveTextBox(object? source)
    {
        if (source is ContextMenu sourceContextMenu)
        {
            return ResolveTextBox(sourceContextMenu.PlacementTarget);
        }

        if (source is TextBox textBox)
        {
            return textBox;
        }

        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is TextBox currentTextBox)
            {
                return currentTextBox;
            }

            if (current is ContextMenu contextMenu)
            {
                return ResolveTextBox(contextMenu.PlacementTarget);
            }

            if (current is MenuItem menuItem &&
                ItemsControl.ItemsControlFromItemContainer(menuItem) is
                    { } menuOwner)
            {
                if (menuOwner is ContextMenu ownerContextMenu)
                {
                    return ResolveTextBox(
                        ownerContextMenu.PlacementTarget);
                }

                current = menuOwner;
                continue;
            }

            DependencyObject? parent = null;
            if (current is Visual)
            {
                parent = VisualTreeHelper.GetParent(current);
            }

            parent ??= LogicalTreeHelper.GetParent(current);
            if (parent is null &&
                current is FrameworkElement element)
            {
                parent = element.Parent;
            }

            if (ReferenceEquals(parent, current))
            {
                break;
            }

            current = parent;
        }

        return null;
    }
}
