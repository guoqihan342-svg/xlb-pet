using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class TodoWindow : Window
{
    private static readonly Brush TodoDropIndicatorBrush =
        CreateTodoDropIndicatorBrush();

    private bool _settingPetSizeScale;
    private bool _petSizeAdjustmentActive;
    private bool _petSizeScaleNotificationQueued;
    private double _pendingPetSizeScale = 1;
    private int _displayedPetSizePercent = int.MinValue;
    private readonly Action _resetImeCompositionAfterFocusLossAction;
    private readonly Action _finishTodoEditAfterFocusLossAction;
    private readonly Action _finishTodoEditAfterOutsideClickAction;
    private readonly Action _focusInputAction;
    private readonly Action _focusSelectedPageInputAfterTabAction;
    private readonly Action _retryClipboardCopyAction;
    private TextBox? _imeCompositionOwner;
    private TextBox? _editingTodoTextBox;
    private Button? _editingTodoButton;
    private TodoItem? _editingTodoItem;
    private string _editingTodoOriginalText = string.Empty;
    private string _editingTodoDraftText = string.Empty;
    private bool _outsideTodoEditCommitPending;
    private bool _outsideTodoEditCommitQueued;
    private TodoItem? _todoDragCandidate;
    private Point _todoDragStartPoint;
    private bool _todoDragInProgress;
    private ListBoxItem? _todoDropTargetContainer;
    private bool _todoDropTargetInsertAfter;
    private ScrollViewer? _todoScrollViewer;
    private long _lastTodoAutoScrollTimestamp;
    private string? _pendingClipboardCopyText;
    private bool _clipboardCopyRetryQueued;
    private ScheduledTaskItem? _editingScheduledTask;
    private bool _tailOnRight = true;
    private bool _isTransientPopupOpen;
    private bool _allowClose;
    private bool _hasClosed;

    public TodoWindow()
    {
        InitializeComponent();
        _resetImeCompositionAfterFocusLossAction =
            ResetImeCompositionAfterFocusLoss;
        _finishTodoEditAfterFocusLossAction =
            FinishTodoEditAfterFocusLoss;
        _finishTodoEditAfterOutsideClickAction =
            FinishTodoEditAfterOutsideClick;
        _focusInputAction = FocusInputCore;
        _focusSelectedPageInputAfterTabAction =
            FocusSelectedPageInputAfterTabChange;
        _retryClipboardCopyAction = RetryClipboardCopy;

        TextCompositionManager.AddPreviewTextInputStartHandler(
            TodoInput,
            TodoInput_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(
            TodoInput,
            TodoInput_PreviewTextInputUpdate);
        TodoInput.PreviewTextInput += TodoInput_PreviewTextInputCommitted;
        TodoInput.LostKeyboardFocus += TodoInput_LostKeyboardFocus;
        TextCompositionManager.AddPreviewTextInputStartHandler(
            ScheduledTaskInput,
            TodoInput_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(
            ScheduledTaskInput,
            TodoInput_PreviewTextInputUpdate);
        ScheduledTaskInput.PreviewTextInput += TodoInput_PreviewTextInputCommitted;
        ScheduledTaskInput.LostKeyboardFocus += TodoInput_LostKeyboardFocus;
        PetSizeSlider.PreviewMouseLeftButtonDown += PetSizeSlider_PreviewMouseLeftButtonDown;
        PetSizeSlider.PreviewMouseLeftButtonUp += PetSizeSlider_PreviewMouseLeftButtonUp;
        PetSizeSlider.LostMouseCapture += PetSizeSlider_LostMouseCapture;
        PetSizeSlider.PreviewKeyDown += PetSizeSlider_PreviewKeyDown;
        PetSizeSlider.PreviewKeyUp += PetSizeSlider_PreviewKeyUp;
        PetSizeSlider.LostKeyboardFocus += PetSizeSlider_LostKeyboardFocus;
        AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(TodoWindow_PreviewMouseDown),
            handledEventsToo: true);
        PreviewKeyDown += TodoWindow_PreviewKeyDown;
        Closing += TodoWindow_Closing;
        Closed += TodoWindow_Closed;
        ResetScheduledTaskDraftClock(DateTimeOffset.Now);
    }

    private static Brush CreateTodoDropIndicatorBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
        brush.Freeze();
        return brush;
    }

    public IEnumerable? Todos
    {
        get => TodoItemsControl.ItemsSource;
        set => TodoItemsControl.ItemsSource = value;
    }

    public IEnumerable? ScheduledTasks
    {
        get => ScheduledTaskItemsControl.ItemsSource;
        set => ScheduledTaskItemsControl.ItemsSource = value;
    }

    public bool IsImeComposing { get; private set; }

    public bool IsTodoDragInProgress => _todoDragInProgress;

    public bool IsTransientPopupOpen => _isTransientPopupOpen;

    public event Action<string>? AddRequested;

    public event Action<TodoItem>? TodoChanged;

    public event Action<TodoItem>? TodoEdited;

    public event Action<TodoItem, int>? TodoMoveRequested;

    public event Action? TodoDragCompleted;

    public event Action<TodoItem>? DeleteRequested;

    public event Action<string, DateTimeOffset>? ScheduledTaskAddRequested;

    public event Action<ScheduledTaskItem, string, DateTimeOffset>?
        ScheduledTaskEditRequested;

    public event Action<ScheduledTaskItem>? ScheduledTaskDeleteRequested;

    public event Action? TransientInteractionCompleted;

    public event Action<double>? PetSizeScaleChanged;

    public event Action? PetSizeAdjustmentStarted;

    public event Action? PetSizeAdjustmentCompleted;

    public event EventHandler? CloseRequested;

    public event EventHandler? ExitRequested;

    public event Action<bool>? ImeCompositionChanged;

    public void ShowDefaultTab()
    {
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
        SelectTaskPage(showScheduledTasks: false, focusInput: false);
    }

    public void FocusInput()
    {
        if (!IsVisible)
        {
            return;
        }

        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, _focusInputAction);
    }

    private void FocusInputCore()
    {
        // A rapid second right-click can hide the owned window before the Input
        // priority callback runs. Do not revive focus/IME state after it closed.
        if (!IsVisible || _hasClosed)
        {
            return;
        }

        // Do not steal a selection from either the input or a read-only todo
        // row when a delayed Input-priority focus request finally runs.
        if (IsKeyboardFocusWithin)
        {
            return;
        }

        FocusSelectedPageInput();
    }

    private void FocusSelectedPageInputAfterTabChange()
    {
        if (!IsVisible || _hasClosed)
        {
            return;
        }

        FocusSelectedPageInput();
    }

    private void FocusSelectedPageInput()
    {
        var input = ScheduledTaskTabButton.IsChecked == true
            ? ScheduledTaskInput
            : TodoInput;
        input.Focus();
        Keyboard.Focus(input);
        input.Select(input.Text.Length, 0);
    }

    private void CopyCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox textBox)
        {
            e.CanExecute = false;
            return;
        }

        if (!IsCopySource(textBox))
        {
            return;
        }

        e.CanExecute = CanCopyFromTextBox(textBox);
        e.Handled = true;
    }

    private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox textBox)
        {
            return;
        }

        var text = GetCopyText(textBox);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        CopyTextToClipboard(text);
        e.Handled = true;
    }

    private void TodoWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // TextBox's built-in Copy command disables itself when the selection
        // is empty before a parent CommandBinding can reliably replace that
        // behavior. Intercept the physical shortcut at the owned-window root:
        // input copies its full value without a selection, while read-only
        // rows still require an explicit text selection.
        if (e.Key != Key.C ||
            (Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
            Keyboard.FocusedElement is not TextBox textBox ||
            !IsCopySource(textBox))
        {
            return;
        }

        var text = GetCopyText(textBox);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        CopyTextToClipboard(text);
        e.Handled = true;
    }

    private bool IsCopySource(TextBox textBox) =>
        ReferenceEquals(textBox, TodoInput) ||
        ReferenceEquals(textBox, ScheduledTaskInput) ||
        (textBox.DataContext is TodoItem or ScheduledTaskItem);

    private bool CanCopyFromTextBox(TextBox textBox) =>
        !string.IsNullOrEmpty(GetCopyText(textBox));

    private string? GetCopyText(TextBox textBox)
    {
        if (ReferenceEquals(textBox, TodoInput) ||
            ReferenceEquals(textBox, ScheduledTaskInput))
        {
            return textBox.SelectionLength > 0
                ? textBox.SelectedText
                : textBox.Text;
        }

        return (textBox.DataContext is TodoItem or ScheduledTaskItem) &&
               textBox.SelectionLength > 0
            ? textBox.SelectedText
            : null;
    }

    private void CopyTextToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            _pendingClipboardCopyText = null;
        }
        catch (ExternalException)
        {
            _pendingClipboardCopyText = text;
            if (_clipboardCopyRetryQueued)
            {
                return;
            }

            _clipboardCopyRetryQueued = true;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                _retryClipboardCopyAction);
        }
    }

    private void RetryClipboardCopy()
    {
        _clipboardCopyRetryQueued = false;
        var text = _pendingClipboardCopyText;
        _pendingClipboardCopyText = null;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetDataObject(text, true);
        }
        catch (ExternalException)
        {
            // The clipboard can be held briefly by another process. One
            // deferred retry keeps Ctrl+C responsive without a blocking loop.
        }
    }

    public void SetPetSizeScale(double scale)
    {
        var normalizedScale = Math.Clamp(
            double.IsFinite(scale) ? scale : 1.0,
            0.75,
            1.40);
        _settingPetSizeScale = true;
        try
        {
            PetSizeSlider.Value = normalizedScale * 100;
            UpdatePetSizeLabel(PetSizeSlider.Value);
        }
        finally
        {
            _settingPetSizeScale = false;
        }
    }

    public void SetTailOnRight(bool tailOnRight)
    {
        if (_tailOnRight == tailOnRight)
        {
            return;
        }

        _tailOnRight = tailOnRight;
        FirstColumn.Width = new GridLength(tailOnRight ? 280 : 12);
        SecondColumn.Width = new GridLength(tailOnRight ? 12 : 280);
        Grid.SetColumn(TodoBorder, tailOnRight ? 0 : 1);
        Grid.SetColumn(TailHost, tailOnRight ? 1 : 0);
        TailHost.Margin = tailOnRight
            ? new Thickness(-1, 0, 0, 0)
            : new Thickness(0, 0, -1, 0);
        TailHost.HorizontalAlignment = tailOnRight
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        TailPolygon.Points = tailOnRight
            ? PointCollection.Parse("0,0 12,9 0,18")
            : PointCollection.Parse("12,0 0,9 12,18");
    }

    public void CloseForApplication()
    {
        _allowClose = true;
        if (!_hasClosed)
        {
            Close();
        }
    }

    public void AllowApplicationClose()
    {
        _allowClose = true;
    }

    private void TodoWindow_Closing(object? sender, CancelEventArgs e)
    {
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);

        if (_allowClose)
        {
            if (_editingTodoTextBox is { } textBox)
            {
                CaptureTodoEditDraft(textBox);
                if (IsImeComposing)
                {
                    // Windows TSF can still own an uncommitted candidate here.
                    // Closing the dispatcher cannot wait for its final TextChanged,
                    // so discard this one unconfirmed edit instead of persisting a
                    // stale or half-composed value.
                    CancelTodoEdit();
                }
                else
                {
                    CommitTodoEdit();
                }
            }

            return;
        }

        CommitPendingTodoEdit();
        e.Cancel = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TodoWindow_Closed(object? sender, EventArgs e)
    {
        _hasClosed = true;
        _outsideTodoEditCommitPending = false;
        _outsideTodoEditCommitQueued = false;
        _isTransientPopupOpen = false;
        if (_petSizeAdjustmentActive)
        {
            EndPetSizeAdjustment();
        }
        else
        {
            FlushPendingPetSizeScaleChanged();
        }
    }

    private void TodoInput_PreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        _imeCompositionOwner = sender as TextBox;
        SetImeComposing(true);
    }

    private void TodoInput_PreviewTextInputUpdate(object sender, TextCompositionEventArgs e)
    {
        var composition = e.TextComposition;
        var hasCompositionText =
            !string.IsNullOrEmpty(composition.CompositionText) ||
            !string.IsNullOrEmpty(composition.SystemCompositionText);
        if (hasCompositionText)
        {
            _imeCompositionOwner = sender as TextBox;
            SetImeComposing(true);
        }
        else if (ReferenceEquals(_imeCompositionOwner, sender))
        {
            SetImeComposing(false);
        }
    }

    private void TodoInput_PreviewTextInputCommitted(object sender, TextCompositionEventArgs e)
    {
        if (_imeCompositionOwner is null ||
            ReferenceEquals(_imeCompositionOwner, sender))
        {
            SetImeComposing(false);
        }
    }

    private void TodoInput_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            _resetImeCompositionAfterFocusLossAction);
    }

    private void ResetImeCompositionAfterFocusLoss()
    {
        var owner = _imeCompositionOwner;
        if (owner is not null)
        {
            if (!owner.IsKeyboardFocusWithin)
            {
                SetImeComposing(false);
            }

            return;
        }

        if (!TodoInput.IsKeyboardFocusWithin &&
            !ScheduledTaskInput.IsKeyboardFocusWithin)
        {
            SetImeComposing(false);
        }
    }

    private void SetImeComposing(bool value)
    {
        if (!value)
        {
            _imeCompositionOwner = null;
        }

        if (IsImeComposing != value)
        {
            IsImeComposing = value;
            ImeCompositionChanged?.Invoke(value);
        }

        if (!value && _outsideTodoEditCommitPending)
        {
            ScheduleTodoEditAfterOutsideClick();
        }
    }

    private void TodoTabButton_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
        SelectTaskPage(showScheduledTasks: false, focusInput: true);
    }

    private void ScheduledTaskTabButton_Click(object sender, RoutedEventArgs e)
    {
        CommitTodoEdit();
        SelectTaskPage(showScheduledTasks: true, focusInput: true);
    }

    private void SelectTaskPage(bool showScheduledTasks, bool focusInput)
    {
        TodoTabButton.IsChecked = !showScheduledTasks;
        ScheduledTaskTabButton.IsChecked = showScheduledTasks;
        TodoPage.Visibility = showScheduledTasks
            ? Visibility.Collapsed
            : Visibility.Visible;
        ScheduledTaskPage.Visibility = showScheduledTasks
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (focusInput && IsVisible && !_hasClosed)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                _focusSelectedPageInputAfterTabAction);
        }
    }

    private void ScheduledTaskAddButton_Click(object sender, RoutedEventArgs e)
    {
        RequestScheduledTaskSubmit();
    }

    private void ScheduledTaskInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || IsImeComposing)
        {
            return;
        }

        RequestScheduledTaskSubmit();
        e.Handled = true;
    }

    private void ScheduledTimeInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || IsImeComposing)
        {
            return;
        }

        RequestScheduledTaskSubmit();
        e.Handled = true;
    }

    private void RequestScheduledTaskSubmit()
    {
        if (!TryReadScheduledTaskDraft(out var text, out var dueAt))
        {
            return;
        }

        if (_editingScheduledTask is { } item)
        {
            ScheduledTaskEditRequested?.Invoke(item, text, dueAt);
            CancelScheduledTaskEdit(resetDraft: true, focusInput: true);
            return;
        }

        ScheduledTaskAddRequested?.Invoke(text, dueAt);
        ScheduledTaskInput.Clear();
        ResetScheduledTaskDraftClock(DateTimeOffset.Now);
        SetScheduledTaskValidation(string.Empty);
        ScheduledTaskInput.Focus();
    }

    // Kept as a small compatibility shim for the existing UI-state contract.
    private void RequestScheduledTaskAdd() => RequestScheduledTaskSubmit();

    private bool TryReadScheduledTaskDraft(
        out string text,
        out DateTimeOffset dueAt)
    {
        dueAt = default;
        text = ScheduledTaskInput.Text.Trim();
        if (text.Length == 0)
        {
            SetScheduledTaskValidation("先写下要提醒的事情哦");
            ScheduledTaskInput.Focus();
            return false;
        }

        if (ScheduledDatePicker.SelectedDate is not { } selectedDate)
        {
            SetScheduledTaskValidation("请选择提醒日期");
            ScheduledDatePicker.Focus();
            return false;
        }

        if (!DateTime.TryParseExact(
                ScheduledTimeInput.Text.Trim(),
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            SetScheduledTaskValidation("时间格式要写成 HH:mm:ss");
            ScheduledTimeInput.Focus();
            ScheduledTimeInput.SelectAll();
            return false;
        }

        var localDateTime = DateTime.SpecifyKind(
            selectedDate.Date.Add(parsedTime.TimeOfDay),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localDateTime))
        {
            SetScheduledTaskValidation("这个本地时间不存在，请换一个时间");
            ScheduledTimeInput.Focus();
            return false;
        }

        dueAt = new DateTimeOffset(
            localDateTime,
            TimeZoneInfo.Local.GetUtcOffset(localDateTime));
        if (dueAt <= DateTimeOffset.Now)
        {
            SetScheduledTaskValidation("提醒时间要晚于现在哦");
            ScheduledTimeInput.Focus();
            ScheduledTimeInput.SelectAll();
            return false;
        }

        return true;
    }

    private void ScheduledTaskEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScheduledTaskItem item })
        {
            return;
        }

        _editingScheduledTask = item;
        var localDueAt = item.DueAt.ToLocalTime();
        ScheduledTaskInput.Text = item.Text;
        ScheduledDatePicker.SelectedDate = localDueAt.Date;
        ScheduledTimeInput.Text = localDueAt.ToString(
            "HH:mm:ss",
            CultureInfo.InvariantCulture);
        ScheduledTaskSubmitButton.Content = "保存";
        ScheduledTaskSubmitButton.ToolTip = "保存定时任务修改";
        ScheduledTaskEditCancelButton.Visibility = Visibility.Visible;
        SetScheduledTaskValidation(string.Empty);
        ScheduledTaskInput.Focus();
        Keyboard.Focus(ScheduledTaskInput);
        ScheduledTaskInput.SelectAll();
        e.Handled = true;
    }

    private void ScheduledTaskEditCancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CancelScheduledTaskEdit(resetDraft: true, focusInput: true);
        e.Handled = true;
    }

    private void CancelScheduledTaskEdit(bool resetDraft, bool focusInput)
    {
        if (_editingScheduledTask is null)
        {
            return;
        }

        _editingScheduledTask = null;
        ScheduledTaskSubmitButton.Content = "设定";
        ScheduledTaskSubmitButton.ToolTip = "添加定时任务";
        ScheduledTaskEditCancelButton.Visibility = Visibility.Collapsed;
        SetScheduledTaskValidation(string.Empty);

        if (resetDraft)
        {
            ScheduledTaskInput.Clear();
            ResetScheduledTaskDraftClock(DateTimeOffset.Now);
        }

        if (focusInput && IsVisible && !_hasClosed)
        {
            ScheduledTaskInput.Focus();
            Keyboard.Focus(ScheduledTaskInput);
        }
    }

    private void ScheduledTaskDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledTaskItem item })
        {
            if (ReferenceEquals(item, _editingScheduledTask))
            {
                CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
            }

            ScheduledTaskDeleteRequested?.Invoke(item);
        }
    }

    private void ScheduledTaskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearScheduledTaskValidation();
    }

    private void ScheduledTimeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearScheduledTaskValidation();
    }

    private void ScheduledDatePicker_SelectedDateChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ClearScheduledTaskValidation();
    }

    private void ScheduledDatePicker_CalendarOpened(object sender, RoutedEventArgs e)
    {
        _isTransientPopupOpen = true;
    }

    private void ScheduledDatePicker_CalendarClosed(object sender, RoutedEventArgs e)
    {
        if (!_isTransientPopupOpen)
        {
            return;
        }

        _isTransientPopupOpen = false;
        TransientInteractionCompleted?.Invoke();
    }

    private void ResetScheduledTaskDraftClock(DateTimeOffset now)
    {
        var suggested = now.LocalDateTime.AddMinutes(5);
        suggested = new DateTime(
            suggested.Year,
            suggested.Month,
            suggested.Day,
            suggested.Hour,
            suggested.Minute,
            suggested.Second,
            DateTimeKind.Unspecified);
        ScheduledDatePicker.SelectedDate = suggested.Date;
        ScheduledTimeInput.Text = suggested.ToString(
            "HH:mm:ss",
            CultureInfo.InvariantCulture);
    }

    private void ClearScheduledTaskValidation()
    {
        if (ScheduledTaskValidationText is not null &&
            ScheduledTaskValidationText.Text.Length > 0)
        {
            ScheduledTaskValidationText.Text = string.Empty;
        }
    }

    private void SetScheduledTaskValidation(string message)
    {
        ScheduledTaskValidationText.Text = message;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        RequestAdd();
    }

    private void TodoInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || IsImeComposing)
        {
            return;
        }

        RequestAdd();
        e.Handled = true;
    }

    private void RequestAdd()
    {
        var text = TodoInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        AddRequested?.Invoke(text);
        TodoInput.Clear();
    }

    public void CommitPendingTodoEdit()
    {
        if (_editingTodoTextBox is { } textBox)
        {
            CaptureTodoEditDraft(textBox);
        }

        if (IsImeComposing)
        {
            _outsideTodoEditCommitPending = true;
            ScheduleTodoEditAfterOutsideClick();
            return;
        }

        CommitTodoEdit();
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoItem item })
        {
            return;
        }

        var container = TodoItemsControl.ItemContainerGenerator.ContainerFromItem(item)
            as ListBoxItem;
        var textBox = FindVisualDescendant<TextBox>(container, "TodoTextBox");
        if (textBox is not null)
        {
            BeginTodoEdit(textBox, item);
        }
    }

    private void BeginTodoEdit(TextBox textBox, TodoItem item)
    {
        if (ReferenceEquals(_editingTodoTextBox, textBox) &&
            ReferenceEquals(_editingTodoItem, item))
        {
            textBox.Focus();
            Keyboard.Focus(textBox);
            return;
        }

        CommitTodoEdit();
        if (_editingTodoTextBox is not null)
        {
            return;
        }

        _editingTodoTextBox = textBox;
        _editingTodoItem = item;
        _editingTodoOriginalText = item.Text;
        _editingTodoDraftText = textBox.Text;
        var container = TodoItemsControl.ItemContainerGenerator.ContainerFromItem(item)
            as ListBoxItem;
        _editingTodoButton = FindVisualDescendant<Button>(container, "TodoEditButton");
        if (_editingTodoButton is not null)
        {
            _editingTodoButton.IsEnabled = false;
        }

        TextCompositionManager.AddPreviewTextInputStartHandler(
            textBox,
            TodoInput_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(
            textBox,
            TodoInput_PreviewTextInputUpdate);
        textBox.PreviewTextInput += TodoInput_PreviewTextInputCommitted;
        textBox.IsReadOnly = false;
        textBox.Focus();
        Keyboard.Focus(textBox);
        textBox.SelectAll();
    }

    private bool CommitTodoEdit()
    {
        var textBox = _editingTodoTextBox;
        var item = _editingTodoItem;
        if (textBox is null || item is null)
        {
            return false;
        }

        if (IsImeComposing && textBox.IsKeyboardFocusWithin)
        {
            return false;
        }

        if (IsImeComposing &&
            (_imeCompositionOwner is null ||
             ReferenceEquals(_imeCompositionOwner, textBox)))
        {
            SetImeComposing(false);
        }

        var normalizedText = _editingTodoDraftText.Trim();
        if (normalizedText.Length == 0)
        {
            CancelTodoEdit();
            return false;
        }

        var changed = !string.Equals(item.Text, normalizedText, StringComparison.Ordinal);
        if (changed)
        {
            item.Text = normalizedText;
        }

        EndTodoEdit(restoreBindingTarget: true);
        if (changed)
        {
            TodoEdited?.Invoke(item);
        }

        return changed;
    }

    private void CancelTodoEdit()
    {
        if (_editingTodoItem is not null)
        {
            _editingTodoItem.Text = _editingTodoOriginalText;
        }

        EndTodoEdit(restoreBindingTarget: true);
    }

    private void EndTodoEdit(bool restoreBindingTarget)
    {
        var textBox = _editingTodoTextBox;
        if (textBox is not null)
        {
            TextCompositionManager.RemovePreviewTextInputStartHandler(
                textBox,
                TodoInput_PreviewTextInputStart);
            TextCompositionManager.RemovePreviewTextInputUpdateHandler(
                textBox,
                TodoInput_PreviewTextInputUpdate);
            textBox.PreviewTextInput -= TodoInput_PreviewTextInputCommitted;
            textBox.IsReadOnly = true;
            if (restoreBindingTarget)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            }
        }

        if (_editingTodoButton is not null)
        {
            _editingTodoButton.IsEnabled = true;
        }

        _editingTodoTextBox = null;
        _editingTodoButton = null;
        _editingTodoItem = null;
        _editingTodoOriginalText = string.Empty;
        _editingTodoDraftText = string.Empty;
        _outsideTodoEditCommitPending = false;
        if (_imeCompositionOwner is null ||
            ReferenceEquals(_imeCompositionOwner, textBox))
        {
            SetImeComposing(false);
        }
    }

    private void TodoEditTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TodoItem item } textBox)
        {
            return;
        }

        if (textBox.IsReadOnly)
        {
            if (e.Key == Key.F2)
            {
                BeginTodoEdit(textBox, item);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            if (!IsImeComposing)
            {
                CancelTodoEdit();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Enter && !IsImeComposing)
        {
            CommitTodoEdit();
            e.Handled = true;
        }
    }

    private void TodoEditTextBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox ||
            !ReferenceEquals(textBox, _editingTodoTextBox))
        {
            return;
        }

        HandleTodoEditAfterFocusDeparture(textBox);
    }

    private void TodoWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var textBox = _editingTodoTextBox;
        if (textBox is null ||
            IsWithin(e.OriginalSource as DependencyObject, textBox))
        {
            return;
        }

        CaptureTodoEditDraft(textBox);
        if (!IsImeComposing)
        {
            CommitTodoEdit();
            return;
        }

        _outsideTodoEditCommitPending = true;
        ScheduleTodoEditAfterOutsideClick();
    }

    private void TodoEditTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (sender is TextBox textBox &&
            ReferenceEquals(textBox, _editingTodoTextBox))
        {
            CaptureTodoEditDraft(textBox);
        }
    }

    private void TodoEditTextBox_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox &&
            ReferenceEquals(textBox, _editingTodoTextBox) &&
            !ReferenceEquals(e.NewValue, _editingTodoItem))
        {
            if (IsImeComposing)
            {
                // A recycled container can no longer receive a trustworthy
                // final TSF TextChanged for the original item.  Discard the
                // unconfirmed candidate and make the reused editor read-only.
                CancelTodoEdit();
                return;
            }

            HandleTodoEditAfterFocusDeparture(textBox);
        }
    }

    private void TodoEditTextBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox &&
            ReferenceEquals(textBox, _editingTodoTextBox))
        {
            if (IsImeComposing)
            {
                // Once the editor is unloaded, waiting for the IME candidate
                // could write a partial value back through a recycled control.
                CancelTodoEdit();
                return;
            }

            HandleTodoEditAfterFocusDeparture(textBox);
        }
    }

    private void HandleTodoEditAfterFocusDeparture(TextBox textBox)
    {
        CaptureTodoEditDraft(textBox);
        if (!IsImeComposing)
        {
            CommitTodoEdit();
            return;
        }

        _outsideTodoEditCommitPending = true;
        ScheduleTodoEditAfterFocusLoss();
    }

    private void CaptureTodoEditDraft(TextBox textBox)
    {
        if (ReferenceEquals(textBox.DataContext, _editingTodoItem))
        {
            _editingTodoDraftText = textBox.Text;
        }
    }

    private void ScheduleTodoEditAfterFocusLoss()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            _finishTodoEditAfterFocusLossAction);
    }

    private void ScheduleTodoEditAfterOutsideClick()
    {
        if (_outsideTodoEditCommitQueued)
        {
            return;
        }

        _outsideTodoEditCommitQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            _finishTodoEditAfterOutsideClickAction);
    }

    private void FinishTodoEditAfterOutsideClick()
    {
        _outsideTodoEditCommitQueued = false;
        if (_hasClosed || !_outsideTodoEditCommitPending)
        {
            return;
        }

        var textBox = _editingTodoTextBox;
        if (textBox is null)
        {
            _outsideTodoEditCommitPending = false;
            return;
        }

        CaptureTodoEditDraft(textBox);
        if (IsImeComposing)
        {
            return;
        }

        CommitTodoEdit();
        if (!ReferenceEquals(_editingTodoTextBox, textBox))
        {
            _outsideTodoEditCommitPending = false;
        }
    }

    private void FinishTodoEditAfterFocusLoss()
    {
        if (_hasClosed)
        {
            return;
        }

        var textBox = _editingTodoTextBox;
        if (textBox is null)
        {
            return;
        }

        var containerWasRecycled =
            !ReferenceEquals(textBox.DataContext, _editingTodoItem) ||
            !textBox.IsLoaded;
        if (textBox.IsKeyboardFocusWithin && !containerWasRecycled)
        {
            return;
        }

        if (containerWasRecycled && IsImeComposing)
        {
            // Fail closed even if an unusual WPF event order reaches this
            // delayed fallback before DataContextChanged/Unloaded handled it.
            // A recycled control cannot supply a trustworthy final IME value.
            CancelTodoEdit();
            return;
        }

        ResetImeCompositionAfterFocusLoss();
        _outsideTodoEditCommitPending = true;
        FinishTodoEditAfterOutsideClick();
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private void TodoDragHandle_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TodoItem item } handle)
        {
            return;
        }

        _todoDragCandidate = item;
        _todoDragStartPoint = e.GetPosition(TodoItemsControl);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void TodoDragHandle_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement handle && handle.IsMouseCaptured)
        {
            handle.ReleaseMouseCapture();
        }

        _todoDragCandidate = null;
        e.Handled = true;
    }

    private void TodoDragHandle_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _todoDragCandidate = null;
    }

    private void TodoDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_todoDragInProgress ||
            _todoDragCandidate is not TodoItem item ||
            e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement handle)
        {
            return;
        }

        var position = e.GetPosition(TodoItemsControl);
        if (Math.Abs(position.X - _todoDragStartPoint.X) <
                SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _todoDragStartPoint.Y) <
                SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (handle.IsMouseCaptured)
        {
            handle.ReleaseMouseCapture();
        }

        if (_editingTodoTextBox is { } editor &&
            IsImeComposing && editor.IsKeyboardFocusWithin)
        {
            _todoDragCandidate = null;
            return;
        }

        CommitTodoEdit();
        _todoDragInProgress = true;
        _todoDragCandidate = null;
        _lastTodoAutoScrollTimestamp = 0;

        try
        {
            var data = new DataObject(typeof(TodoItem), item);
            DragDrop.DoDragDrop(handle, data, DragDropEffects.Move);
        }
        finally
        {
            ClearTodoDropTarget();
            _lastTodoAutoScrollTimestamp = 0;
            _todoDragInProgress = false;
            TodoDragCompleted?.Invoke();
        }

        e.Handled = true;
    }

    private void TodoItemsControl_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!_todoDragInProgress ||
            !e.Data.GetDataPresent(typeof(TodoItem)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var container = FindTodoContainer(e.OriginalSource as DependencyObject);
        var insertAfter = container is not null &&
                          e.GetPosition(container).Y >= container.ActualHeight / 2;
        UpdateTodoDropTarget(container, insertAfter);
        AutoScrollTodoList(e.GetPosition(TodoItemsControl).Y);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TodoItemsControl_PreviewDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!_todoDragInProgress ||
                e.Data.GetData(typeof(TodoItem)) is not TodoItem item)
            {
                return;
            }

            var oldIndex = TodoItemsControl.Items.IndexOf(item);
            if (oldIndex < 0 || TodoItemsControl.Items.Count < 2)
            {
                return;
            }

            var container = FindTodoContainer(e.OriginalSource as DependencyObject);
            var targetIndex = container?.DataContext is TodoItem target
                ? TodoItemsControl.Items.IndexOf(target)
                : TodoItemsControl.Items.Count - 1;
            var insertAfter = container is null ||
                              e.GetPosition(container).Y >= container.ActualHeight / 2;
            var finalIndex = targetIndex + (insertAfter ? 1 : 0);
            if (oldIndex < finalIndex)
            {
                finalIndex--;
            }

            finalIndex = Math.Clamp(finalIndex, 0, TodoItemsControl.Items.Count - 1);
            if (finalIndex != oldIndex)
            {
                TodoMoveRequested?.Invoke(item, finalIndex);
            }

            e.Effects = DragDropEffects.Move;
        }
        finally
        {
            ClearTodoDropTarget();
            e.Handled = true;
        }
    }

    private ListBoxItem? FindTodoContainer(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, TodoItemsControl))
        {
            if (source is ListBoxItem container &&
                ReferenceEquals(
                    ItemsControl.ItemsControlFromItemContainer(container),
                    TodoItemsControl))
            {
                return container;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void UpdateTodoDropTarget(ListBoxItem? container, bool insertAfter)
    {
        if (ReferenceEquals(_todoDropTargetContainer, container) &&
            _todoDropTargetInsertAfter == insertAfter)
        {
            return;
        }

        if (!ReferenceEquals(_todoDropTargetContainer, container))
        {
            ClearTodoDropTarget();
            _todoDropTargetContainer = container;
        }

        if (container is null)
        {
            return;
        }

        _todoDropTargetInsertAfter = insertAfter;
        container.BorderBrush = TodoDropIndicatorBrush;
        container.BorderThickness = insertAfter
            ? new Thickness(0, 0, 0, 2)
            : new Thickness(0, 2, 0, 0);
    }

    private void ClearTodoDropTarget()
    {
        if (_todoDropTargetContainer is null)
        {
            return;
        }

        _todoDropTargetContainer.ClearValue(Control.BorderBrushProperty);
        _todoDropTargetContainer.ClearValue(Control.BorderThicknessProperty);
        _todoDropTargetContainer = null;
        _todoDropTargetInsertAfter = false;
    }

    private void AutoScrollTodoList(double pointerY)
    {
        var scrollViewer = _todoScrollViewer ??=
            FindVisualDescendant<ScrollViewer>(TodoItemsControl);
        if (scrollViewer is null)
        {
            return;
        }

        const double edgeSize = 20;
        var scrollUp = pointerY < edgeSize;
        var scrollDown = pointerY > TodoItemsControl.ActualHeight - edgeSize;
        if (!scrollUp && !scrollDown)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastTodoAutoScrollTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastTodoAutoScrollTimestamp, now) <
                TimeSpan.FromMilliseconds(50))
        {
            return;
        }

        _lastTodoAutoScrollTimestamp = now;
        if (scrollUp)
        {
            scrollViewer.LineUp();
        }
        else
        {
            scrollViewer.LineDown();
        }
    }

    private static T? FindVisualDescendant<T>(
        DependencyObject? root,
        string? name = null)
        where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match &&
                (name is null || string.Equals(match.Name, name, StringComparison.Ordinal)))
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child, name);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void TodoCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoItem item } checkBox)
        {
            return;
        }

        item.IsCompleted = checkBox.IsChecked == true;
        TodoChanged?.Invoke(item);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TodoItem item })
        {
            if (ReferenceEquals(item, _editingTodoItem))
            {
                if (IsImeComposing)
                {
                    // The item is about to disappear, so an unfinished IME
                    // candidate must not be committed later to a deleted object.
                    CancelTodoEdit();
                }
                else
                {
                    CommitPendingTodoEdit();
                }
            }

            DeleteRequested?.Invoke(item);
        }
    }

    private void PetSizeSlider_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e) => BeginPetSizeAdjustment();

    private void PetSizeSlider_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e) => EndPetSizeAdjustment();

    private void PetSizeSlider_LostMouseCapture(
        object sender,
        MouseEventArgs e) => EndPetSizeAdjustment();

    private void PetSizeSlider_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsPetSizeAdjustmentKey(e.Key))
        {
            BeginPetSizeAdjustment();
        }
    }

    private void PetSizeSlider_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (IsPetSizeAdjustmentKey(e.Key))
        {
            EndPetSizeAdjustment();
        }
    }

    private void PetSizeSlider_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) => EndPetSizeAdjustment();

    private static bool IsPetSizeAdjustmentKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or
            Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private void BeginPetSizeAdjustment()
    {
        if (_settingPetSizeScale || _petSizeAdjustmentActive)
        {
            return;
        }

        _petSizeAdjustmentActive = true;
        PetSizeAdjustmentStarted?.Invoke();
    }

    private void EndPetSizeAdjustment()
    {
        if (!_petSizeAdjustmentActive)
        {
            // Lost focus/capture and key-up can arrive in either order. Always
            // commit a value already sampled by ValueChanged, but only pair a
            // completion event with a matching start event.
            FlushPendingPetSizeScaleChanged();
            return;
        }

        FlushPendingPetSizeScaleChanged();
        _petSizeAdjustmentActive = false;
        PetSizeAdjustmentCompleted?.Invoke();
    }

    private void QueuePetSizeScaleChanged(double scale)
    {
        _pendingPetSizeScale = scale;
        _petSizeScaleNotificationQueued = true;
    }

    internal void FlushPendingPetSizeScaleChanged()
    {
        if (!_petSizeScaleNotificationQueued)
        {
            return;
        }

        var scale = _pendingPetSizeScale;
        _petSizeScaleNotificationQueued = false;
        PetSizeScaleChanged?.Invoke(scale);
    }


    private void PetSizeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (PetSizeLabel is null)
        {
            return;
        }

        UpdatePetSizeLabel(e.NewValue);

        if (!_settingPetSizeScale)
        {
            var scale = e.NewValue / 100;
            if (_petSizeAdjustmentActive)
            {
                QueuePetSizeScaleChanged(scale);
            }
            else
            {
                PetSizeScaleChanged?.Invoke(scale);
            }
        }
    }

    private void UpdatePetSizeLabel(double percentage)
    {
        var roundedPercentage = (int)Math.Round(percentage);
        if (_displayedPetSizePercent == roundedPercentage)
        {
            return;
        }

        _displayedPetSizePercent = roundedPercentage;
        PetSizeLabel.Text = $"{roundedPercentage}%";
    }
}
