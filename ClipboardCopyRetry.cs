using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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

    private const uint ClipboardFormatUnicodeText = 13;
    private const uint GlobalMemoryMoveable = 0x0002;
    private const uint GlobalMemoryZeroInit = 0x0040;

    private static readonly object s_clipboardTransactionGate = new();
    private static long s_nextRequestGeneration;
    private static long s_latestRequestGeneration;

    private readonly Dispatcher _dispatcher;
    private readonly Window _ownerWindow;
    private DispatcherTimer? _retryTimer;
    private string? _pendingText;
    private long _requestGeneration;
    private uint _clipboardSequence;
    private long _retryDeadlineTimestamp;
    private int _attemptCount;
    private int _nextRetryIndex;
    private bool _disposed;

    private IntPtr _ownerHandle;

    internal ClipboardCopyRetry(Window ownerWindow)
    {
        ArgumentNullException.ThrowIfNull(ownerWindow);
        _ownerWindow = ownerWindow;
        _dispatcher = ownerWindow.Dispatcher;
    }

    internal void Copy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancelPending(invalidateRequest: true);
        var requestStartedTimestamp = Stopwatch.GetTimestamp();
        var ownerHandle = GetOwnerHandle();
        ClipboardWriteResult writeResult;
        long requestGeneration;
        uint sequenceBeforeCopy;
        lock (s_clipboardTransactionGate)
        {
            requestGeneration = StartNewRequest();
            sequenceBeforeCopy = GetClipboardSequenceNumber();
            writeResult = TryWriteClipboardText(
                text,
                ownerHandle,
                requestGeneration,
                sequenceBeforeCopy);
        }

        if (writeResult == ClipboardWriteResult.Success)
        {
            return;
        }

        if (writeResult != ClipboardWriteResult.ClipboardBusy)
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

    internal bool TryWriteNow(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A synchronous Cut is a newer request even when the clipboard is
        // busy. It must permanently supersede every delayed Copy so an old
        // request can never overwrite the user's later Cut attempt.
        CancelPending(invalidateRequest: true);
        var ownerHandle = GetOwnerHandle();
        lock (s_clipboardTransactionGate)
        {
            var requestGeneration = StartNewRequest();
            var expectedSequence = GetClipboardSequenceNumber();
            return TryWriteClipboardText(
                       text,
                       ownerHandle,
                       requestGeneration,
                       expectedSequence) ==
                   ClipboardWriteResult.Success;
        }
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

    private ClipboardWriteResult TryWriteClipboardText(
        string text,
        IntPtr ownerHandle,
        long requestGeneration,
        uint expectedSequence)
    {
        if (ownerHandle == IntPtr.Zero)
        {
            return ClipboardWriteResult.Failed;
        }

        var byteCount = checked((nuint)(text.Length + 1) * sizeof(char));
        var clipboardMemory = GlobalAlloc(
            GlobalMemoryMoveable | GlobalMemoryZeroInit,
            byteCount);
        if (clipboardMemory == IntPtr.Zero)
        {
            return ClipboardWriteResult.Failed;
        }

        try
        {
            var buffer = GlobalLock(clipboardMemory);
            if (buffer == IntPtr.Zero)
            {
                return ClipboardWriteResult.Failed;
            }

            try
            {
                if (text.Length > 0)
                {
                    Marshal.Copy(
                        text.ToCharArray(),
                        0,
                        buffer,
                        text.Length);
                }

                Marshal.WriteInt16(
                    buffer,
                    checked(text.Length * sizeof(char)),
                    0);
            }
            finally
            {
                _ = GlobalUnlock(clipboardMemory);
            }

            if (!OpenClipboard(ownerHandle))
            {
                return ClipboardWriteResult.ClipboardBusy;
            }

            try
            {
                // The guard and the actual Empty/Set operation run while the
                // same native clipboard lock is held. An external owner can no
                // longer update the clipboard between validation and commit.
                if (!IsLatestRequest(requestGeneration) ||
                    GetClipboardSequenceNumber() != expectedSequence)
                {
                    return ClipboardWriteResult.Superseded;
                }

                if (!EmptyClipboard())
                {
                    return ClipboardWriteResult.Failed;
                }

                // EmptyClipboard can synchronously notify the previous owner.
                // Recheck our generation before transferring the buffer in
                // case a newer in-process command was raised reentrantly.
                if (!IsLatestRequest(requestGeneration))
                {
                    return ClipboardWriteResult.Superseded;
                }

                if (SetClipboardData(
                        ClipboardFormatUnicodeText,
                        clipboardMemory) == IntPtr.Zero)
                {
                    return ClipboardWriteResult.Failed;
                }

                // SetClipboardData transfers ownership to the operating
                // system. The caller must not free this handle after success.
                clipboardMemory = IntPtr.Zero;
                return ClipboardWriteResult.Success;
            }
            finally
            {
                _ = CloseClipboard();
            }
        }
        finally
        {
            if (clipboardMemory != IntPtr.Zero)
            {
                _ = GlobalFree(clipboardMemory);
            }
        }
    }

    private IntPtr GetOwnerHandle()
    {
        if (_ownerHandle != IntPtr.Zero)
        {
            return _ownerHandle;
        }

        var helper = new WindowInteropHelper(_ownerWindow);
        _ownerHandle = helper.Handle;
        if (_ownerHandle == IntPtr.Zero)
        {
            _ownerHandle = helper.EnsureHandle();
        }

        return _ownerHandle;
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
        ClipboardWriteResult writeResult;
        lock (s_clipboardTransactionGate)
        {
            writeResult = TryWriteClipboardText(
                text,
                GetOwnerHandle(),
                requestGeneration,
                sequenceBeforeCopy);
        }

        if (writeResult == ClipboardWriteResult.Success)
        {
            CancelPending(invalidateRequest: false);
            return;
        }

        if (writeResult != ClipboardWriteResult.ClipboardBusy)
        {
            CancelPending(invalidateRequest: true);
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

    private static long StartNewRequest()
    {
        var requestGeneration = Interlocked.Increment(
            ref s_nextRequestGeneration);
        Volatile.Write(
            ref s_latestRequestGeneration,
            requestGeneration);
        return requestGeneration;
    }

    private static bool HasClipboardSequenceChanged(
        uint expected,
        uint current) =>
        current != expected;

    private static TimeSpan GetRetryDelay(int retryIndex) =>
        retryIndex switch
        {
            0 => TimeSpan.FromMilliseconds(20),
            1 => TimeSpan.FromMilliseconds(35),
            2 => TimeSpan.FromMilliseconds(45),
            3 => TimeSpan.FromMilliseconds(60),
            _ => TimeSpan.FromMilliseconds(80)
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(
        uint format,
        IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(
        uint flags,
        nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    private enum ClipboardWriteResult
    {
        Success,
        ClipboardBusy,
        Superseded,
        Failed
    }
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

    internal static bool TryCutSelectedText(
        TextBox textBox,
        ClipboardCopyRetry clipboardCopyRetry)
    {
        ArgumentNullException.ThrowIfNull(clipboardCopyRetry);
        if (!CanCut(textBox))
        {
            return false;
        }

        var selectionStart = textBox.SelectionStart;
        var selectionLength = textBox.SelectionLength;
        var textSnapshot = textBox.Text;
        var selectedText = textBox.SelectedText;
        var dataContextSnapshot = textBox.DataContext;
        if (selectedText.Length == 0)
        {
            return false;
        }

        // Cut is intentionally synchronous and fail-closed: never schedule a
        // later deletion after TSF/IME has had an opportunity to move or alter
        // the selection.
        if (!clipboardCopyRetry.TryWriteNow(selectedText))
        {
            return false;
        }

        return TryDeleteCutSelection(
            textBox,
            dataContextSnapshot,
            selectionStart,
            selectionLength,
            textSnapshot,
            selectedText);
    }

    internal static bool TryDeleteCutSelection(
        TextBox textBox,
        object? dataContextSnapshot,
        int selectionStart,
        int selectionLength,
        string textSnapshot,
        string selectedText)
    {
        ArgumentNullException.ThrowIfNull(textBox);
        ArgumentNullException.ThrowIfNull(textSnapshot);
        ArgumentNullException.ThrowIfNull(selectedText);

        if (!textBox.IsEnabled ||
            textBox.IsReadOnly ||
            !ReferenceEquals(textBox.DataContext, dataContextSnapshot) ||
            !string.Equals(
                textBox.Text,
                textSnapshot,
                StringComparison.Ordinal) ||
            textBox.SelectionStart != selectionStart ||
            textBox.SelectionLength != selectionLength ||
            !string.Equals(
                textBox.SelectedText,
                selectedText,
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
            var format = e.FormatToApply;
            var text = !string.IsNullOrEmpty(format) &&
                       dataObject.GetDataPresent(
                           format,
                           autoConvert: false)
                ? dataObject.GetData(
                    format,
                    autoConvert: false) as string
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
