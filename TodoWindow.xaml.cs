using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class TodoWindow : Window
{
    internal const double DefaultWindowWidth = 292;
    internal const double DefaultWindowHeight = 414;
    private const double TaskFullTextPopupGap = 12;
    private static readonly Brush TodoDropIndicatorBrush =
        CreateTodoDropIndicatorBrush();
    private static readonly string[] ScheduledHourOptions =
        CreateClockPartOptions(24);
    private static readonly string[] ScheduledMinuteSecondOptions =
        CreateClockPartOptions(60);
    private static readonly string[] ScheduledRepeatUnitOptions =
        ["分钟", "小时", "天"];
    private static readonly TaskFullTextTheme TodoTaskFullTextTheme =
        CreateTaskFullTextTheme(
            chromeBackground: Color.FromRgb(0xF3, 0xF7, 0xFD),
            chromeBorder: Color.FromRgb(0x8C, 0xB4, 0xF4),
            shadow: Color.FromRgb(0x5B, 0x78, 0xA8),
            title: Color.FromRgb(0x3E, 0x70, 0xC6),
            textBackground: Color.FromRgb(0xF8, 0xFB, 0xFF),
            textBorder: Color.FromRgb(0xBD, 0xD2, 0xEF),
            textForeground: Color.FromRgb(0x30, 0x37, 0x44),
            selection: Color.FromRgb(0xA9, 0xC9, 0xF4),
            count: Color.FromRgb(0x82, 0x95, 0xB3),
            scrollTrack: Color.FromArgb(0x24, 0x5B, 0x8D, 0xEF),
            scrollDrag: Color.FromRgb(0x3E, 0x70, 0xC6),
            scrollThumb: Color.FromRgb(0x7B, 0xA6, 0xE8),
            scrollHover: Color.FromRgb(0x5B, 0x8D, 0xEF));
    private static readonly TaskFullTextTheme ScheduledTaskFullTextTheme =
        CreateTaskFullTextTheme(
            chromeBackground: Color.FromRgb(0xFF, 0xF8, 0xF1),
            chromeBorder: Color.FromRgb(0xEF, 0x94, 0x65),
            shadow: Color.FromRgb(0xA8, 0x55, 0x36),
            title: Color.FromRgb(0xC4, 0x5F, 0x3D),
            textBackground: Color.FromRgb(0xFF, 0xFC, 0xF8),
            textBorder: Color.FromRgb(0xF1, 0xC1, 0x9E),
            textForeground: Color.FromRgb(0x4B, 0x34, 0x2B),
            selection: Color.FromRgb(0xF5, 0xB0, 0x7D),
            count: Color.FromRgb(0xC1, 0x8A, 0x6C),
            scrollTrack: Color.FromArgb(0x22, 0xD6, 0xA0, 0x6A),
            scrollDrag: Color.FromRgb(0xD9, 0x7B, 0x2F),
            scrollThumb: Color.FromRgb(0xE7, 0xA1, 0x5E),
            scrollHover: Color.FromRgb(0xF0, 0x9A, 0x48));
    private static readonly TaskPageControlTheme TodoTaskPageControlTheme =
        CreateTaskPageControlTheme(
            settingsText: Color.FromRgb(0x51, 0x59, 0x66),
            switchTrack: Color.FromRgb(0xEA, 0xF3, 0xFF),
            switchBorder: Color.FromRgb(0xBB, 0xD3, 0xF5),
            switchHoverTrack: Color.FromRgb(0xDC, 0xEB, 0xFF),
            switchHoverBorder: Color.FromRgb(0x8C, 0xB4, 0xF4),
            switchCheckedTrack: Color.FromRgb(0x78, 0xA8, 0xEB),
            switchCheckedBorder: Color.FromRgb(0x5B, 0x8D, 0xEF),
            switchKnobFill: Color.FromRgb(0xF8, 0xFB, 0xFF),
            switchKnobStroke: Color.FromRgb(0x8C, 0xB4, 0xF4),
            switchFace: Color.FromRgb(0x4F, 0x7F, 0xD7),
            switchShadow: Color.FromArgb(0x45, 0x5B, 0x8D, 0xEF),
            sliderDecrease: Color.FromRgb(0x77, 0xA7, 0xEB),
            sliderIncrease: Color.FromRgb(0xE5, 0xEF, 0xFC),
            sliderIncreaseBorder: Color.FromRgb(0xBB, 0xD3, 0xF5),
            sliderShadow: Color.FromArgb(0x45, 0x3E, 0x70, 0xC6),
            sliderThumb: Color.FromRgb(0xEA, 0xF3, 0xFF),
            sliderThumbBorder: Color.FromRgb(0x5B, 0x8D, 0xEF),
            sliderThumbHover: Color.FromRgb(0xDC, 0xEB, 0xFF),
            sliderThumbHoverBorder: Color.FromRgb(0x3E, 0x70, 0xC6),
            sliderThumbDrag: Color.FromRgb(0xBD, 0xD7, 0xFA),
            sliderFace: Color.FromRgb(0x4F, 0x7F, 0xD7));
    private static readonly TaskPageControlTheme ScheduledTaskPageControlTheme =
        CreateTaskPageControlTheme(
            settingsText: Color.FromRgb(0x67, 0x53, 0x44),
            switchTrack: Color.FromRgb(0xFF, 0xF3, 0xE3),
            switchBorder: Color.FromRgb(0xE2, 0xB9, 0x88),
            switchHoverTrack: Color.FromRgb(0xFF, 0xE7, 0xC9),
            switchHoverBorder: Color.FromRgb(0xE4, 0x9A, 0x50),
            switchCheckedTrack: Color.FromRgb(0xF7, 0xB4, 0x6E),
            switchCheckedBorder: Color.FromRgb(0xDB, 0x87, 0x3B),
            switchKnobFill: Color.FromRgb(0xFF, 0xFC, 0xF7),
            switchKnobStroke: Color.FromRgb(0xE2, 0xB2, 0x7E),
            switchFace: Color.FromRgb(0xB6, 0x78, 0x45),
            switchShadow: Color.FromArgb(0x45, 0xA8, 0x5E, 0x25),
            sliderDecrease: Color.FromRgb(0xF5, 0xAD, 0x68),
            sliderIncrease: Color.FromRgb(0xFF, 0xF0, 0xDD),
            sliderIncreaseBorder: Color.FromRgb(0xE8, 0xC3, 0x9A),
            sliderShadow: Color.FromArgb(0x45, 0xA8, 0x5E, 0x25),
            sliderThumb: Color.FromRgb(0xFF, 0xE7, 0xC5),
            sliderThumbBorder: Color.FromRgb(0xE2, 0x8E, 0x43),
            sliderThumbHover: Color.FromRgb(0xFF, 0xD9, 0xA7),
            sliderThumbHoverBorder: Color.FromRgb(0xD8, 0x79, 0x2C),
            sliderThumbDrag: Color.FromRgb(0xFF, 0xC9, 0x8A),
            sliderFace: Color.FromRgb(0xA7, 0x64, 0x35));

    private bool _settingEdgeRoamingEnabled;
    private bool _settingStartupEnabled;
    private bool _settingPetSizeScale;
    private bool _petSizeAdjustmentActive;
    private long _petSizeAdjustmentGeneration;
    private bool _petSizeFirstScalePublishedDuringAdjustment;
    private bool _petSizeScaleNotificationQueued;
    private double _pendingPetSizeScale = 1;
    private int _displayedPetSizePercent = int.MinValue;
    private readonly Action _resetImeCompositionAfterFocusLossAction;
    private readonly Action _finishTodoEditAfterFocusLossAction;
    private readonly Action _finishTodoEditAfterOutsideClickAction;
    private readonly Action _focusInputAction;
    private readonly Action _focusSelectedPageInputAfterTabAction;
    private readonly Action _retryClipboardCopyAction;
    private readonly Action _finishScheduledPickerInternalCommitAction;
    private readonly DispatcherTimer _taskFullTextCloseTimer;
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
    private DateTimeOffset _editingScheduledOriginalDueAt;
    private TimeSpan? _editingScheduledOriginalRepeatInterval;
    private ScheduledRepeatRule? _editingScheduledOriginalRepeatRule;
    private ScheduledQuietHours? _editingScheduledOriginalQuietHours;
    private DateTime? _scheduledDate;
    private DateTime _displayedScheduledCalendarMonth;
    private bool _scheduledTaskDraftClockEdited;
    private bool _updatingScheduledTaskDraftClock;
    private bool _updatingScheduledRepeatDraft;
    private bool _scheduledRepeatDraftEdited;
    private bool _updatingScheduledQuietHoursDraft;
    private bool _scheduledQuietHoursDraftEdited;
    private bool _updatingScheduledTimePickerSelection;
    private ScheduledTimePickerTarget _scheduledTimePickerTarget =
        ScheduledTimePickerTarget.Reminder;
    private bool _switchingScheduledPickerPopup;
    private bool _isScheduledDatePickerPopupOpen;
    private bool _isScheduledTimePickerPopupOpen;
    private ScheduledPickerState _scheduledPickerState;
    private ScheduledTimePartCloseCause _scheduledTimePartCloseCause;
    private bool _scheduledRepeatUnitCommitIsInternal;
    private ComboBox? _activeScheduledTimePart;
    private long _scheduledPickerInteractionGeneration;
    private FrameworkElement? _taskFullTextOwner;
    private TaskFullTextTheme _activeTaskFullTextTheme =
        TodoTaskFullTextTheme;
    private bool _isTaskFullTextPopupOpen;
    private TextBox? _taskRowSelectionTextBox;
    private int _taskRowSelectionAnchor;
    private int _taskRowSelectionVisibleEnd;
    private bool _adjustingTaskRowSelection;
    private TaskTextEditWindow? _taskTextEditWindow;
    private ScheduledTaskEditWindow? _scheduledTaskEditWindow;
    private Window? _editorInterruptedByReminder;
    private bool _isReminderInterruptionActive;
    private bool _suppressDeleteConfirmationForSession;
    private bool _isDeleteConfirmationOpen;
    private const double TailPlacementEpsilon = 0.01;
    private bool _tailOnRight = true;
    private double _tailTop = 198;
    private bool _allowClose;
    private bool _hasClosed;

    public TodoWindow()
    {
        InitializeComponent();
        WindowChromeAppearance.ExcludeFromAltTab(this);
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
        _finishScheduledPickerInternalCommitAction =
            FinishScheduledPickerInternalCommit;
        _taskFullTextCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _taskFullTextCloseTimer.Tick +=
            TaskFullTextCloseTimer_Tick;
        TaskFullTextPopup.CustomPopupPlacementCallback =
            PlaceTaskFullTextPopup;
        if (ScheduledDatePickerPopup.Child is UIElement datePickerChild)
        {
            datePickerChild.AddHandler(
                Mouse.PreviewMouseDownEvent,
                new MouseButtonEventHandler(
                    ScheduledDatePickerPopup_PreviewMouseDown),
                handledEventsToo: true);
        }

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
        PetSizeSlider.AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(
                PetSizeSlider_PreviewMouseLeftButtonUp),
            handledEventsToo: true);
        PetSizeSlider.LostMouseCapture += PetSizeSlider_LostMouseCapture;
        PetSizeSlider.PreviewKeyDown += PetSizeSlider_PreviewKeyDown;
        PetSizeSlider.PreviewKeyUp += PetSizeSlider_PreviewKeyUp;
        PetSizeSlider.LostKeyboardFocus += PetSizeSlider_LostKeyboardFocus;
        AddHandler(
            Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(TodoWindow_PreviewMouseDown),
            handledEventsToo: true);
        PreviewKeyDown += TodoWindow_PreviewKeyDown;
        Deactivated += TodoWindow_TransientPopupDeactivated;
        IsVisibleChanged += TodoWindow_IsVisibleChanged;
        Closing += TodoWindow_Closing;
        Closed += TodoWindow_Closed;
        ScheduledHourComboBox.ItemsSource = ScheduledHourOptions;
        ScheduledMinuteComboBox.ItemsSource = ScheduledMinuteSecondOptions;
        ScheduledSecondComboBox.ItemsSource = ScheduledMinuteSecondOptions;
        ScheduledRepeatUnitComboBox.ItemsSource =
            ScheduledRepeatUnitOptions;
        ScheduledRepeatUnitComboBox.SelectedIndex =
            (int)ScheduledRepeatUnit.Hour;
        ResetScheduledTaskDraftClock(DateTimeOffset.Now);
        ResetScheduledRepeatDraft();
        ResetScheduledQuietHoursDraft();
    }

    private static Brush CreateTodoDropIndicatorBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
        brush.Freeze();
        return brush;
    }

    private static string[] CreateClockPartOptions(int count)
    {
        var options = new string[count];
        for (var value = 0; value < count; value++)
        {
            options[value] = value.ToString("00", CultureInfo.InvariantCulture);
        }

        return options;
    }

    private sealed class ScheduledCalendarDateCell
    {
        public required DateTime Date { get; init; }

        public required string DayText { get; init; }

        public required string AccessibleName { get; init; }

        public bool IsCurrentMonth { get; init; }

        public bool IsSelected { get; init; }

        public bool IsToday { get; init; }

        public bool IsWeekend { get; init; }
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

    public bool IsTransientPopupOpen =>
        _isScheduledDatePickerPopupOpen ||
        _isScheduledTimePickerPopupOpen ||
        _isTaskFullTextPopupOpen ||
        _isDeleteConfirmationOpen ||
        _taskTextEditWindow?.IsVisible == true ||
        _scheduledTaskEditWindow?.IsVisible == true ||
        ScheduledDatePickerPopup.IsOpen ||
        ScheduledTimePickerPopup.IsOpen ||
        TaskFullTextPopup.IsOpen;

    internal void BeginReminderInterruption()
    {
        _isReminderInterruptionActive = true;
        PetSizeSlider.IsEnabled = false;
        CompletePetSizeAdjustmentForInterruption();

        if (_scheduledTaskEditWindow is { IsVisible: true } scheduledEditor)
        {
            TrackEditorDuringReminderInterruption(scheduledEditor);
            return;
        }

        if (_taskTextEditWindow is { IsVisible: true } textEditor)
        {
            TrackEditorDuringReminderInterruption(textEditor);
        }
    }

    internal void EndReminderInterruption(bool restoreEditorFocus)
    {
        _isReminderInterruptionActive = false;
        PetSizeSlider.IsEnabled = true;
        var interruptedEditor = _editorInterruptedByReminder;
        _editorInterruptedByReminder = null;

        if (interruptedEditor is ScheduledTaskEditWindow scheduledEditor)
        {
            scheduledEditor.SetReminderInterruptionActive(false);
            if (restoreEditorFocus)
            {
                scheduledEditor.RestoreAfterReminder();
            }

            return;
        }

        if (restoreEditorFocus &&
            interruptedEditor is TaskTextEditWindow textEditor)
        {
            textEditor.RestoreAfterReminder();
        }
    }

    private void TrackEditorDuringReminderInterruption(Window editor)
    {
        if (!_isReminderInterruptionActive)
        {
            return;
        }

        if (!ReferenceEquals(_editorInterruptedByReminder, editor) &&
            _editorInterruptedByReminder is
                ScheduledTaskEditWindow previousScheduledEditor)
        {
            previousScheduledEditor.SetReminderInterruptionActive(false);
        }

        _editorInterruptedByReminder = editor;
        if (editor is ScheduledTaskEditWindow scheduledEditor)
        {
            scheduledEditor.SetReminderInterruptionActive(true);
        }
    }

    private void ReleaseReminderInterruptedEditor(Window editor)
    {
        if (!ReferenceEquals(_editorInterruptedByReminder, editor))
        {
            return;
        }

        _editorInterruptedByReminder = null;
        if (editor is ScheduledTaskEditWindow scheduledEditor)
        {
            scheduledEditor.SetReminderInterruptionActive(false);
        }
    }

    internal void RecoverAfterSystemResume()
    {
        CloseScheduledPickers();
        WindowState = WindowState.Normal;
        Width = DefaultWindowWidth;
        Height = DefaultWindowHeight;
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        UpdateLayout();
    }

    public event Action<string>? AddRequested;

    public event Action<TodoItem>? TodoChanged;

    public event Action<TodoItem>? TodoEdited;

    public event Action<TodoItem, int>? TodoMoveRequested;

    public event Action? TodoDragCompleted;

    public event Action<TodoItem>? DeleteRequested;

    public event Action<
        string,
        DateTimeOffset,
        TimeSpan?,
        ScheduledRepeatRule?,
        ScheduledQuietHours?>?
        ScheduledTaskAddRequested;

    public event Action<
        ScheduledTaskItem,
        string,
        DateTimeOffset,
        TimeSpan?,
        ScheduledRepeatRule?,
        ScheduledQuietHours?>?
        ScheduledTaskEditRequested;

    public event Action<ScheduledTaskItem>? ScheduledTaskDeleteRequested;

    public event Action? TransientInteractionCompleted;

    public event Action<bool>? EdgeRoamingEnabledChanged;

    public event Action<bool>? StartupEnabledChanged;

    public event Action<double>? PetSizeScaleChanged;

    public event Action? PetSizeAdjustmentStarted;

    public event Action? PetSizeAdjustmentCompleted;

    public event EventHandler? CloseRequested;

    public event EventHandler? ExitRequested;

    public event Action<bool>? ImeCompositionChanged;

    public void ShowDefaultTab()
    {
        CloseScheduledPickers();
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
        if (ScheduledDatePickerPopup.IsOpen &&
            HandleScheduledDatePickerKey(e))
        {
            return;
        }

        if (e.Key == Key.Escape && ScheduledTimePickerPopup.IsOpen)
        {
            CloseScheduledTimePicker();
            e.Handled = true;
            return;
        }

        var effectiveKey = GetEffectiveShortcutKey(e);
        if (effectiveKey == Key.X &&
            Keyboard.Modifiers == ModifierKeys.Control &&
            Keyboard.FocusedElement is TextBox cutTextBox &&
            IsEditableTextSource(cutTextBox) &&
            cutTextBox.SelectionLength > 0)
        {
            e.Handled = true;
            TryCutSelectedText(cutTextBox);
            return;
        }

        // TextBox's built-in Copy command disables itself when the selection
        // is empty before a parent CommandBinding can reliably replace that
        // behavior. Intercept the physical shortcut at the owned-window root:
        // input copies its full value without a selection, while read-only
        // rows still require an explicit text selection.
        if (effectiveKey != Key.C ||
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

    private static Key GetEffectiveShortcutKey(KeyEventArgs e) =>
        e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

    private bool TryCutSelectedText(TextBox textBox)
    {
        if (textBox.IsReadOnly ||
            textBox.SelectionLength <= 0 ||
            !IsEditableTextSource(textBox))
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

        _pendingClipboardCopyText = null;

        // WPF's routed Cut command can intermittently lose the selection that
        // begins at index zero while TSF focus state settles. Capture the
        // selection before touching the clipboard, then remove exactly that
        // range and restore a deterministic caret position.
        if (!TryCopyTextToClipboard(selectedText))
        {
            // Match WPF's fail-closed Cut behavior: if another process owns
            // the clipboard, preserve both the text and its selection. A
            // deferred deletion could otherwise target a selection that TSF
            // has already changed.
            return false;
        }

        return RemovePendingCutSelection(
            textBox,
            textSnapshot,
            selectionStart,
            selectionLength,
            selectedText,
            requireSelectionMatch: false);
    }

    private static bool RemovePendingCutSelection(
        TextBox textBox,
        string textSnapshot,
        int selectionStart,
        int selectionLength,
        string selectedText,
        bool requireSelectionMatch)
    {
        if (textBox.IsReadOnly ||
            !string.Equals(textBox.Text, textSnapshot, StringComparison.Ordinal) ||
            selectionStart < 0 ||
            selectionLength <= 0 ||
            selectionStart > textSnapshot.Length - selectionLength ||
            !string.Equals(
                textSnapshot.Substring(selectionStart, selectionLength),
                selectedText,
                StringComparison.Ordinal) ||
            (requireSelectionMatch &&
              (textBox.SelectionStart != selectionStart ||
               textBox.SelectionLength != selectionLength)))
        {
            return false;
        }

        textBox.Select(selectionStart, selectionLength);
        textBox.SelectedText = string.Empty;
        textBox.Select(selectionStart, 0);
        return true;
    }

    private bool IsEditableTextSource(TextBox textBox) =>
        ReferenceEquals(textBox, TodoInput) ||
        ReferenceEquals(textBox, ScheduledTaskInput) ||
        ReferenceEquals(textBox, _editingTodoTextBox);

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
        if (TryCopyTextToClipboard(text))
        {
            return;
        }

        _pendingClipboardCopyText = text;
        QueueClipboardRetry();
    }

    private bool TryCopyTextToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            _pendingClipboardCopyText = null;
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private void QueueClipboardRetry()
    {
        if (_clipboardCopyRetryQueued)
        {
            return;
        }

        _clipboardCopyRetryQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            _retryClipboardCopyAction);
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

    public void SetEdgeRoamingEnabled(bool enabled)
    {
        _settingEdgeRoamingEnabled = true;
        try
        {
            EdgeRoamingToggle.IsChecked = enabled;
        }
        finally
        {
            _settingEdgeRoamingEnabled = false;
        }
    }

    public void SetStartupEnabled(bool enabled, string? statusMessage = null)
    {
        _settingStartupEnabled = true;
        try
        {
            StartupToggle.IsChecked = enabled;
            StartupToggle.ToolTip = string.IsNullOrWhiteSpace(statusMessage)
                ? "登录 Windows 后自动启动小鲁班"
                : statusMessage;
        }
        finally
        {
            _settingStartupEnabled = false;
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
        UpdateTailHorizontalMargin();
        TailHost.HorizontalAlignment = tailOnRight
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        TailPolygon.Points = tailOnRight
            ? PointCollection.Parse("0,0 12,9 0,18")
            : PointCollection.Parse("12,0 0,9 12,18");
    }

    public void SetTailPlacement(bool tailOnRight, double centerY)
    {
        var bubbleHeight = ActualHeight > 0 ? ActualHeight : Height;
        var maximumTop = Math.Max(18, bubbleHeight - 36);
        var tailTop = Math.Clamp(
            double.IsFinite(centerY) ? centerY - 9 : bubbleHeight / 2 - 9,
            18,
            maximumTop);
        var sideChanged = _tailOnRight != tailOnRight;
        var topChanged =
            Math.Abs(_tailTop - tailTop) > TailPlacementEpsilon;
        if (!sideChanged && !topChanged)
        {
            return;
        }

        if (sideChanged)
        {
            SetTailOnRight(tailOnRight);
        }

        if (topChanged)
        {
            _tailTop = tailTop;
            TailVerticalTransform.Y = tailTop;
        }
    }

    private void UpdateTailHorizontalMargin()
    {
        TailHost.Margin = _tailOnRight
            ? new Thickness(-1, 0, 0, 0)
            : new Thickness(0, 0, -1, 0);
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
        CloseTaskFullTextPreview();
        CloseOwnedEditorsWithoutSaving();
        CloseScheduledPickers();
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

    private void TodoWindow_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            return;
        }

        CloseTaskFullTextPreview();
        CloseOwnedEditorsWithoutSaving();
        CloseScheduledPickers();
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
    }

    private void CloseOwnedEditorsWithoutSaving()
    {
        _taskTextEditWindow?.CloseWithoutSaving();
        _scheduledTaskEditWindow?.CloseWithoutSaving();
    }

    private void TodoWindow_Closed(object? sender, EventArgs e)
    {
        _hasClosed = true;
        if (_editorInterruptedByReminder is
            ScheduledTaskEditWindow interruptedScheduledEditor)
        {
            interruptedScheduledEditor.SetReminderInterruptionActive(false);
        }

        _editorInterruptedByReminder = null;
        _isReminderInterruptionActive = false;
        _taskFullTextCloseTimer.Stop();
        _taskFullTextCloseTimer.Tick -=
            TaskFullTextCloseTimer_Tick;
        _taskTextEditWindow = null;
        _scheduledTaskEditWindow = null;
        _pendingClipboardCopyText = null;
        _outsideTodoEditCommitPending = false;
        _outsideTodoEditCommitQueued = false;
        _isScheduledDatePickerPopupOpen = false;
        _isScheduledTimePickerPopupOpen = false;
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

    private void TaskRow_MouseEnter(object sender, MouseEventArgs e)
    {
        _taskFullTextCloseTimer.Stop();
        if (sender is not FrameworkElement
            {
                Tag: TodoItem or ScheduledTaskItem
            } owner)
        {
            CloseTaskFullTextPreview();
            return;
        }

        var text = owner.Tag switch
        {
            TodoItem todo => todo.Text,
            ScheduledTaskItem scheduled => scheduled.Text,
            _ => string.Empty
        };
        var rowTextBox = FindVisualDescendant<TextBox>(owner);
        if (text.Length == 0 ||
            rowTextBox is not { IsReadOnly: true } ||
            !IsTaskRowTextClipped(rowTextBox, text))
        {
            CloseTaskFullTextPreview();
            return;
        }

        _taskFullTextOwner = owner;
        TaskFullTextPopup.PlacementTarget = owner;
        TaskFullTextPreviewTextBox.DataContext = owner.Tag;
        TaskFullTextPreviewTextBox.Text = text;
        TaskFullTextPreviewTextBox.Select(0, 0);
        var isScheduledTask = owner.Tag is ScheduledTaskItem;
        _activeTaskFullTextTheme = isScheduledTask
            ? ScheduledTaskFullTextTheme
            : TodoTaskFullTextTheme;
        ApplyTaskFullTextTheme(_activeTaskFullTextTheme);
        TaskFullTextTitle.Text = isScheduledTask
            ? "提醒完整内容 · 可选择复制"
            : "待办完整内容 · 可选择复制";
        TaskFullTextCountText.Text = $"{text.Length}/5000 字";
        TaskFullTextPopup.IsOpen = true;
        TaskFullTextPreviewTextBox.ApplyTemplate();
        ApplyTaskFullTextScrollBarTheme(
            TaskFullTextPreviewTextBox,
            _activeTaskFullTextTheme);
    }

    private void ApplyTaskFullTextTheme(TaskFullTextTheme theme)
    {
        TaskFullTextPopupChrome.Background = theme.ChromeBackground;
        TaskFullTextPopupChrome.BorderBrush = theme.ChromeBorder;
        if (TaskFullTextPopupChrome.Effect is DropShadowEffect shadow)
        {
            shadow.Color = theme.Shadow;
        }

        TaskFullTextTitle.Foreground = theme.Title;
        TaskFullTextPreviewTextBox.Background = theme.TextBackground;
        TaskFullTextPreviewTextBox.BorderBrush = theme.TextBorder;
        TaskFullTextPreviewTextBox.Foreground = theme.TextForeground;
        TaskFullTextPreviewTextBox.SelectionBrush = theme.Selection;
        TaskFullTextCountText.Foreground = theme.Count;
    }

    private static void ApplyTaskFullTextScrollBarTheme(
        DependencyObject parent,
        TaskFullTextTheme theme)
    {
        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(parent);
             index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollBar scrollBar)
            {
                scrollBar.Background = theme.ScrollTrack;
                scrollBar.BorderBrush = theme.ScrollDrag;
                scrollBar.Foreground = theme.ScrollThumb;
                scrollBar.Tag = theme.ScrollHover;
            }

            ApplyTaskFullTextScrollBarTheme(child, theme);
        }
    }

    private static TaskFullTextTheme CreateTaskFullTextTheme(
        Color chromeBackground,
        Color chromeBorder,
        Color shadow,
        Color title,
        Color textBackground,
        Color textBorder,
        Color textForeground,
        Color selection,
        Color count,
        Color scrollTrack,
        Color scrollDrag,
        Color scrollThumb,
        Color scrollHover) =>
        new(
            CreateSharedBrush(chromeBackground),
            CreateSharedBrush(chromeBorder),
            shadow,
            CreateSharedBrush(title),
            CreateSharedBrush(textBackground),
            CreateSharedBrush(textBorder),
            CreateSharedBrush(textForeground),
            CreateSharedBrush(selection),
            CreateSharedBrush(count),
            CreateSharedBrush(scrollTrack),
            CreateSharedBrush(scrollDrag),
            CreateSharedBrush(scrollThumb),
            CreateSharedBrush(scrollHover));

    private static SolidColorBrush CreateSharedBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static TaskPageControlTheme CreateTaskPageControlTheme(
        Color settingsText,
        Color switchTrack,
        Color switchBorder,
        Color switchHoverTrack,
        Color switchHoverBorder,
        Color switchCheckedTrack,
        Color switchCheckedBorder,
        Color switchKnobFill,
        Color switchKnobStroke,
        Color switchFace,
        Color switchShadow,
        Color sliderDecrease,
        Color sliderIncrease,
        Color sliderIncreaseBorder,
        Color sliderShadow,
        Color sliderThumb,
        Color sliderThumbBorder,
        Color sliderThumbHover,
        Color sliderThumbHoverBorder,
        Color sliderThumbDrag,
        Color sliderFace) =>
        new(
            CreateSharedBrush(settingsText),
            CreateSharedBrush(switchTrack),
            CreateSharedBrush(switchBorder),
            CreateSharedBrush(switchHoverTrack),
            CreateSharedBrush(switchHoverBorder),
            CreateSharedBrush(switchCheckedTrack),
            CreateSharedBrush(switchCheckedBorder),
            CreateSharedBrush(switchKnobFill),
            CreateSharedBrush(switchKnobStroke),
            CreateSharedBrush(switchFace),
            switchShadow,
            CreateSharedBrush(sliderDecrease),
            CreateSharedBrush(sliderIncrease),
            CreateSharedBrush(sliderIncreaseBorder),
            CreateSharedBrush(sliderShadow),
            CreateSharedBrush(sliderThumb),
            CreateSharedBrush(sliderThumbBorder),
            CreateSharedBrush(sliderThumbHover),
            CreateSharedBrush(sliderThumbHoverBorder),
            CreateSharedBrush(sliderThumbDrag),
            CreateSharedBrush(sliderFace));

    private static bool IsTaskRowTextClipped(
        TextBox textBox,
        string expectedText)
    {
        if (!textBox.IsLoaded ||
            textBox.ActualWidth <= 0 ||
            textBox.ActualHeight <= 0 ||
            string.IsNullOrEmpty(textBox.Text) ||
            !string.Equals(
                textBox.Text,
                expectedText,
                StringComparison.Ordinal))
        {
            return false;
        }

        var lineCount = textBox.LineCount;
        if (lineCount <= 0)
        {
            return false;
        }

        if (textBox.MaxLines > 0 && lineCount > textBox.MaxLines)
        {
            return true;
        }

        var firstVisibleLine = textBox.GetFirstVisibleLineIndex();
        var lastVisibleLine = textBox.GetLastVisibleLineIndex();
        if (firstVisibleLine > 0 ||
            (lastVisibleLine >= 0 && lastVisibleLine < lineCount - 1))
        {
            return true;
        }

        // Reading the TextBox template's existing viewport does not invalidate
        // measure or arrange, so hover detection cannot resize the row and
        // retrigger MouseEnter/MouseLeave. The small tolerance filters normal
        // device-pixel rounding at fractional DPI scales.
        const double layoutTolerance = 0.5;
        var contentHost = FindVisualDescendant<ScrollViewer>(textBox);
        return contentHost is not null &&
               ((contentHost.ViewportHeight > 0 &&
                 contentHost.ExtentHeight >
                 contentHost.ViewportHeight + layoutTolerance) ||
                (textBox.TextWrapping == TextWrapping.NoWrap &&
                 contentHost.ViewportWidth > 0 &&
                 contentHost.ExtentWidth >
                 contentHost.ViewportWidth + layoutTolerance));
    }

    private static CustomPopupPlacement[] PlaceTaskFullTextPopup(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        var verticalOffset = Math.Round(
            (targetSize.Height - popupSize.Height) / 2);
        return
        [
            new CustomPopupPlacement(
                new Point(
                    -popupSize.Width - TaskFullTextPopupGap + offset.X,
                    verticalOffset + offset.Y),
                PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(
                new Point(
                    targetSize.Width + TaskFullTextPopupGap + offset.X,
                    verticalOffset + offset.Y),
                PopupPrimaryAxis.Vertical)
        ];
    }

    private void TaskRowTextBox_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBox { IsReadOnly: true } textBox)
        {
            ClearTaskRowSelectionDrag();
            return;
        }

        RestoreTaskRowTextViewport(textBox);
        _taskRowSelectionTextBox = textBox;
        _taskRowSelectionVisibleEnd =
            GetTaskRowVisibleTextEnd(textBox);
        _taskRowSelectionAnchor = GetTaskRowCharacterIndex(
            textBox,
            e.GetPosition(textBox),
            _taskRowSelectionVisibleEnd);
    }

    private void TaskRowTextBox_PreviewMouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (sender is not TextBox { IsReadOnly: true } textBox ||
            !ReferenceEquals(textBox, _taskRowSelectionTextBox) ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var selectionEnd = GetTaskRowCharacterIndex(
            textBox,
            e.GetPosition(textBox),
            _taskRowSelectionVisibleEnd);
        var selectionStart = Math.Min(
            _taskRowSelectionAnchor,
            selectionEnd);
        SetTaskRowSelection(
            textBox,
            selectionStart,
            Math.Abs(selectionEnd - _taskRowSelectionAnchor),
            _taskRowSelectionVisibleEnd);
        e.Handled = true;
    }

    private void TaskRowTextBox_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is TextBox textBox &&
            ReferenceEquals(textBox, _taskRowSelectionTextBox))
        {
            ClampTaskRowSelection(textBox);
        }

        ClearTaskRowSelectionDrag();
    }

    private void TaskRowTextBox_LostMouseCapture(
        object sender,
        MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _taskRowSelectionTextBox))
        {
            ClearTaskRowSelectionDrag();
        }
    }

    private void TaskRowTextBox_Unloaded(
        object sender,
        RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _taskRowSelectionTextBox))
        {
            ClearTaskRowSelectionDrag();
        }
    }

    private void TaskRowTextBox_SelectionChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (!_adjustingTaskRowSelection &&
            sender is TextBox { IsReadOnly: true } textBox)
        {
            ClampTaskRowSelection(textBox);
        }
    }

    private void ClampTaskRowSelection(TextBox textBox)
    {
        var visibleEnd = GetTaskRowVisibleTextEnd(textBox);
        var selectionStart = Math.Clamp(
            textBox.SelectionStart,
            0,
            visibleEnd);
        var selectionEnd = Math.Clamp(
            textBox.SelectionStart + textBox.SelectionLength,
            0,
            visibleEnd);
        SetTaskRowSelection(
            textBox,
            Math.Min(selectionStart, selectionEnd),
            Math.Abs(selectionEnd - selectionStart),
            visibleEnd);
    }

    private void SetTaskRowSelection(
        TextBox textBox,
        int selectionStart,
        int selectionLength,
        int visibleEnd)
    {
        var normalizedStart = Math.Clamp(
            selectionStart,
            0,
            visibleEnd);
        var normalizedLength = Math.Clamp(
            selectionLength,
            0,
            visibleEnd - normalizedStart);

        _adjustingTaskRowSelection = true;
        try
        {
            if (textBox.SelectionStart != normalizedStart ||
                textBox.SelectionLength != normalizedLength)
            {
                textBox.Select(normalizedStart, normalizedLength);
            }

            RestoreTaskRowTextViewport(textBox);
        }
        finally
        {
            _adjustingTaskRowSelection = false;
        }
    }

    private static int GetTaskRowVisibleTextEnd(TextBox textBox)
    {
        var lineCount = textBox.LineCount;
        if (lineCount <= 0)
        {
            return textBox.Text.Length;
        }

        var visibleLineCount = textBox.MaxLines > 0
            ? Math.Min(textBox.MaxLines, lineCount)
            : lineCount;
        var lastVisibleLine = Math.Max(0, visibleLineCount - 1);
        var lineStart =
            textBox.GetCharacterIndexFromLineIndex(lastVisibleLine);
        if (lineStart < 0)
        {
            return textBox.Text.Length;
        }

        return Math.Clamp(
            lineStart + textBox.GetLineLength(lastVisibleLine),
            0,
            textBox.Text.Length);
    }

    private static int GetTaskRowCharacterIndex(
        TextBox textBox,
        Point point,
        int visibleEnd)
    {
        var clampedPoint = new Point(
            Math.Clamp(point.X, 0, Math.Max(0, textBox.ActualWidth - 1)),
            Math.Clamp(point.Y, 0, Math.Max(0, textBox.ActualHeight - 1)));
        var characterIndex = textBox.GetCharacterIndexFromPoint(
            clampedPoint,
            snapToText: true);
        if (characterIndex < 0)
        {
            characterIndex = point.Y <= 0 ? 0 : visibleEnd;
        }
        else if (characterIndex < visibleEnd)
        {
            // GetCharacterIndexFromPoint returns the character that was hit,
            // while TextBox.Select expects an exclusive caret end. Resolve
            // the trailing half of the glyph to the caret after that glyph so
            // dragging to a row's right edge can include its final visible
            // character without exposing text on a clipped third line.
            var leading = textBox.GetRectFromCharacterIndex(
                characterIndex,
                trailingEdge: false);
            var trailing = textBox.GetRectFromCharacterIndex(
                characterIndex,
                trailingEdge: true);
            if (!leading.IsEmpty && !trailing.IsEmpty)
            {
                var sameLine =
                    Math.Abs(leading.Y - trailing.Y) <=
                    Math.Max(1, Math.Max(leading.Height, trailing.Height) / 2);
                var isTrailingHalf = sameLine
                    ? trailing.X >= leading.X
                        ? clampedPoint.X >= (leading.X + trailing.X) / 2
                        : clampedPoint.X <= (leading.X + trailing.X) / 2
                    : clampedPoint.Y >= (leading.Y + trailing.Y) / 2;
                if (isTrailingHalf)
                {
                    characterIndex++;
                }
            }
            else if (point.X >= textBox.ActualWidth - 1)
            {
                characterIndex++;
            }
        }

        return Math.Clamp(characterIndex, 0, visibleEnd);
    }

    private static void RestoreTaskRowTextViewport(TextBox textBox)
    {
        if (textBox.LineCount > 0)
        {
            textBox.ScrollToLine(0);
        }

        textBox.ScrollToHorizontalOffset(0);
        textBox.ScrollToVerticalOffset(0);
    }

    private void ClearTaskRowSelectionDrag()
    {
        _taskRowSelectionTextBox = null;
        _taskRowSelectionAnchor = 0;
        _taskRowSelectionVisibleEnd = 0;
    }

    private void TaskRow_MouseLeave(object sender, MouseEventArgs e)
    {
        ScheduleTaskFullTextPreviewClose();
    }

    private void TaskRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _taskFullTextOwner))
        {
            CloseTaskFullTextPreview();
        }
    }

    private void TaskFullTextPopup_MouseEnter(
        object sender,
        MouseEventArgs e)
    {
        _taskFullTextCloseTimer.Stop();
    }

    private void TaskFullTextPopup_MouseLeave(
        object sender,
        MouseEventArgs e)
    {
        ScheduleTaskFullTextPreviewClose();
    }

    private void ScheduleTaskFullTextPreviewClose()
    {
        if (!TaskFullTextPopup.IsOpen)
        {
            return;
        }

        _taskFullTextCloseTimer.Stop();
        _taskFullTextCloseTimer.Start();
    }

    private void TaskFullTextCloseTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _taskFullTextCloseTimer.Stop();
        if (_taskFullTextOwner?.IsMouseOver == true ||
            TaskFullTextPopup.Child is UIElement
            {
                IsMouseOver: true
            })
        {
            return;
        }

        CloseTaskFullTextPreview();
    }

    private void CloseTaskFullTextPreview()
    {
        _taskFullTextCloseTimer.Stop();
        _taskFullTextOwner = null;
        var wasTransientOpen =
            _isTaskFullTextPopupOpen ||
            TaskFullTextPopup.IsOpen;
        _isTaskFullTextPopupOpen = false;
        if (TaskFullTextPopup.IsOpen)
        {
            TaskFullTextPopup.IsOpen = false;
        }

        ReleaseTaskFullTextPreviewContent();
        if (wasTransientOpen && !IsTransientPopupOpen)
        {
            TransientInteractionCompleted?.Invoke();
        }
    }

    private void ReleaseTaskFullTextPreviewContent()
    {
        TaskFullTextPopup.PlacementTarget = null;
        TaskFullTextPreviewTextBox.DataContext = null;
        TaskFullTextPreviewTextBox.Text = string.Empty;
        TaskFullTextTitle.Text = "完整内容 · 可选择复制";
        TaskFullTextCountText.Text = string.Empty;
    }

    private void TaskFullTextPopup_Opened(object sender, EventArgs e)
    {
        _isTaskFullTextPopupOpen = true;
        TaskFullTextPreviewTextBox.ApplyTemplate();
        ApplyTaskFullTextScrollBarTheme(
            TaskFullTextPreviewTextBox,
            _activeTaskFullTextTheme);
    }

    private void TaskFullTextPopup_Closed(object sender, EventArgs e)
    {
        var wasTransientOpen = _isTaskFullTextPopupOpen;
        _isTaskFullTextPopupOpen = false;
        _taskFullTextOwner = null;
        ReleaseTaskFullTextPreviewContent();
        if (wasTransientOpen && !IsTransientPopupOpen)
        {
            TransientInteractionCompleted?.Invoke();
        }
    }

    private void TodoTabButton_Click(object sender, RoutedEventArgs e)
    {
        CloseScheduledPickers();
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
        SelectTaskPage(showScheduledTasks: false, focusInput: true);
    }

    private void ScheduledTaskTabButton_Click(object sender, RoutedEventArgs e)
    {
        CommitTodoEdit();
        PrepareScheduledTaskDraftClockForDisplay(DateTimeOffset.Now);
        SelectTaskPage(showScheduledTasks: true, focusInput: true);
    }

    private void SelectTaskPage(bool showScheduledTasks, bool focusInput)
    {
        CloseTaskFullTextPreview();
        ApplyTaskPageControlTheme(
            showScheduledTasks
                ? ScheduledTaskPageControlTheme
                : TodoTaskPageControlTheme);
        TodoTabButton.IsChecked = !showScheduledTasks;
        ScheduledTaskTabButton.IsChecked = showScheduledTasks;
        TodoPage.Visibility = showScheduledTasks
            ? Visibility.Hidden
            : Visibility.Visible;
        ScheduledTaskPage.Visibility = showScheduledTasks
            ? Visibility.Visible
            : Visibility.Hidden;

        if (focusInput && IsVisible && !_hasClosed)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                _focusSelectedPageInputAfterTabAction);
        }
    }

    private void ApplyTaskPageControlTheme(TaskPageControlTheme theme)
    {
        Resources["TaskPageSettingsTextBrush"] = theme.SettingsText;
        Resources["TaskPageSwitchTrackBrush"] = theme.SwitchTrack;
        Resources["TaskPageSwitchBorderBrush"] = theme.SwitchBorder;
        Resources["TaskPageSwitchHoverTrackBrush"] =
            theme.SwitchHoverTrack;
        Resources["TaskPageSwitchHoverBorderBrush"] =
            theme.SwitchHoverBorder;
        Resources["TaskPageSwitchCheckedTrackBrush"] =
            theme.SwitchCheckedTrack;
        Resources["TaskPageSwitchCheckedBorderBrush"] =
            theme.SwitchCheckedBorder;
        Resources["TaskPageSwitchKnobFillBrush"] =
            theme.SwitchKnobFill;
        Resources["TaskPageSwitchKnobStrokeBrush"] =
            theme.SwitchKnobStroke;
        Resources["TaskPageSwitchFaceBrush"] = theme.SwitchFace;
        Resources["TaskPageSwitchShadowColor"] = theme.SwitchShadow;
        Resources["TaskPageSliderDecreaseBrush"] =
            theme.SliderDecrease;
        Resources["TaskPageSliderIncreaseBrush"] =
            theme.SliderIncrease;
        Resources["TaskPageSliderIncreaseBorderBrush"] =
            theme.SliderIncreaseBorder;
        Resources["TaskPageSliderShadowBrush"] = theme.SliderShadow;
        Resources["TaskPageSliderThumbBrush"] = theme.SliderThumb;
        Resources["TaskPageSliderThumbBorderBrush"] =
            theme.SliderThumbBorder;
        Resources["TaskPageSliderThumbHoverBrush"] =
            theme.SliderThumbHover;
        Resources["TaskPageSliderThumbHoverBorderBrush"] =
            theme.SliderThumbHoverBorder;
        Resources["TaskPageSliderThumbDragBrush"] =
            theme.SliderThumbDrag;
        Resources["TaskPageSliderFaceBrush"] = theme.SliderFace;
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

    private void ScheduledDateInput_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        OpenScheduledDatePicker();
        e.Handled = true;
    }

    private void ScheduledDateInput_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter or Key.Down or Key.F4)
        {
            OpenScheduledDatePicker();
            e.Handled = true;
        }
    }

    private void ScheduledDatePickerPopup_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        HandleScheduledDatePickerKey(e);
    }

    private bool HandleScheduledDatePickerKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseScheduledDatePicker();
                FocusScheduledDateInput();
                e.Handled = true;
                return true;
            case Key.PageUp:
                ChangeScheduledCalendarMonth(-1);
                e.Handled = true;
                return true;
            case Key.PageDown:
                ChangeScheduledCalendarMonth(1);
                e.Handled = true;
                return true;
            case Key.Home:
                SelectScheduledToday(DateTime.Today);
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }

    private void OpenScheduledDatePicker()
    {
        if (_hasClosed)
        {
            return;
        }

        var selectedDate = _scheduledDate ?? DateTime.Today;
        RefreshScheduledCalendar(selectedDate);
        SwitchScheduledPickerPopup(
            () =>
            {
                CloseScheduledTimePicker();
                ScheduledDatePickerPopup.IsOpen = true;
                SetTransientPopupState(
                    isDatePicker: true,
                    isOpen: true);
            });
        ScheduledDatePickerTodayButton.Focus();
        Keyboard.Focus(ScheduledDatePickerTodayButton);
    }

    private void CloseScheduledDatePicker()
    {
        if (ScheduledDatePickerPopup.IsOpen)
        {
            ScheduledDatePickerPopup.IsOpen = false;
        }

        SetTransientPopupState(
            isDatePicker: true,
            isOpen: false);
    }

    private void CloseScheduledPickers()
    {
        CloseScheduledDatePicker();
        CloseScheduledTimePicker();
    }

    private void SwitchScheduledPickerPopup(Action switchAction)
    {
        var wasOpen = IsTransientPopupOpen;
        _switchingScheduledPickerPopup = true;
        try
        {
            switchAction();
        }
        finally
        {
            _switchingScheduledPickerPopup = false;
        }

        if (wasOpen && !IsTransientPopupOpen)
        {
            TransientInteractionCompleted?.Invoke();
        }
    }

    private void ScheduledDatePickerPopup_Opened(object sender, EventArgs e)
    {
        SetTransientPopupState(
            isDatePicker: true,
            isOpen: true);
    }

    private void ScheduledDatePickerPopup_Closed(object sender, EventArgs e)
    {
        SetTransientPopupState(
            isDatePicker: true,
            isOpen: false);
    }

    private void ScheduledDatePreviousMonthButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ChangeScheduledCalendarMonth(-1);
        e.Handled = true;
    }

    private void ScheduledDateNextMonthButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ChangeScheduledCalendarMonth(1);
        e.Handled = true;
    }

    private void ChangeScheduledCalendarMonth(int monthOffset)
    {
        var displayedMonth = _displayedScheduledCalendarMonth == default
            ? new DateTime(
                (_scheduledDate ?? DateTime.Today).Year,
                (_scheduledDate ?? DateTime.Today).Month,
                1)
            : _displayedScheduledCalendarMonth;

        if ((monthOffset < 0 &&
             displayedMonth.Year == DateTime.MinValue.Year &&
             displayedMonth.Month == 1) ||
            (monthOffset > 0 &&
             displayedMonth.Year == DateTime.MaxValue.Year &&
             displayedMonth.Month == 12))
        {
            return;
        }

        RefreshScheduledCalendar(displayedMonth.AddMonths(monthOffset));
    }

    private void RefreshScheduledCalendar(DateTime month)
    {
        var firstOfMonth = new DateTime(month.Year, month.Month, 1);
        _displayedScheduledCalendarMonth = firstOfMonth;
        ScheduledDateMonthText.Text = firstOfMonth.ToString(
            "yyyy年M月",
            CultureInfo.GetCultureInfo("zh-CN"));

        var mondayOffset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        var firstCellDate = firstOfMonth.AddDays(-mondayOffset);
        var selectedDate = _scheduledDate?.Date;
        var today = DateTime.Today;
        var cells = new List<ScheduledCalendarDateCell>(42);
        for (var index = 0; index < 42; index++)
        {
            var date = firstCellDate.AddDays(index);
            cells.Add(new ScheduledCalendarDateCell
            {
                Date = date,
                DayText = date.Day.ToString(CultureInfo.InvariantCulture),
                AccessibleName = date.ToString(
                    "yyyy年M月d日 dddd",
                    CultureInfo.GetCultureInfo("zh-CN")),
                IsCurrentMonth = date.Month == firstOfMonth.Month &&
                                 date.Year == firstOfMonth.Year,
                IsSelected = selectedDate == date,
                IsToday = today == date,
                IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or
                    DayOfWeek.Sunday
            });
        }

        ScheduledDateItemsControl.ItemsSource = cells;
    }

    private void ScheduledDateDayButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateTime date })
        {
            SelectScheduledDate(date);
        }

        e.Handled = true;
    }

    private void ScheduledDatePickerTodayButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SelectScheduledToday(DateTime.Today);
        e.Handled = true;
    }

    private void SelectScheduledToday(DateTime localToday)
    {
        SelectScheduledDate(localToday.Date);
    }

    private void SelectScheduledDate(DateTime date)
    {
        MarkScheduledPickerInteractionIfOpen();
        SetScheduledDate(date, markEdited: true);
        CloseScheduledDatePicker();
        FocusScheduledDateInput();
    }

    private void SetScheduledDate(DateTime date, bool markEdited)
    {
        _scheduledDate = date.Date;
        ScheduledDateInput.Text = date.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        if (markEdited && !_updatingScheduledTaskDraftClock)
        {
            _scheduledTaskDraftClockEdited = true;
        }

        ClearScheduledTaskValidation();
        if (ScheduledDatePickerPopup.IsOpen)
        {
            RefreshScheduledCalendar(date);
        }
    }

    private void FocusScheduledDateInput()
    {
        ScheduledDateInput.Focus();
        Keyboard.Focus(ScheduledDateInput);
    }

    private void ScheduledTimeInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Down or Key.F4)
        {
            OpenScheduledTimePicker();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ScheduledTimePickerPopup.IsOpen)
        {
            CloseScheduledTimePicker();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || IsImeComposing)
        {
            return;
        }

        RequestScheduledTaskSubmit();
        e.Handled = true;
    }

    private void ScheduledTimeInput_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        OpenScheduledTimePicker();
        e.Handled = true;
    }

    private void ScheduledQuietHoursTimeInput_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        OpenScheduledTimePickerForTarget(
            GetScheduledQuietHoursTimeTarget(sender));
        e.Handled = true;
    }

    private void ScheduledQuietHoursTimeInput_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        var target = GetScheduledQuietHoursTimeTarget(sender);
        if (e.Key is Key.Space or Key.Down or Key.F4 or Key.Enter)
        {
            OpenScheduledTimePickerForTarget(target);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ScheduledTimePickerPopup.IsOpen)
        {
            CloseScheduledTimePicker();
            e.Handled = true;
        }
    }

    private ScheduledTimePickerTarget GetScheduledQuietHoursTimeTarget(
        object sender) =>
        ReferenceEquals(sender, ScheduledQuietHoursEndInput)
            ? ScheduledTimePickerTarget.QuietEnd
            : ScheduledTimePickerTarget.QuietStart;

    private void OpenScheduledTimePicker() =>
        OpenScheduledTimePickerForTarget(
            ScheduledTimePickerTarget.Reminder);

    private void OpenScheduledTimePickerForTarget(
        ScheduledTimePickerTarget target)
    {
        if (_hasClosed)
        {
            return;
        }

        var targetChanged = _scheduledTimePickerTarget != target;
        BeginScheduledPickerInternalCommit();
        SwitchScheduledPickerPopup(
            () =>
            {
                if (targetChanged && ScheduledTimePickerPopup.IsOpen)
                {
                    CloseScheduledTimePicker();
                }

                CloseScheduledDatePicker();
                ConfigureScheduledTimePickerTarget(target);
                SynchronizeScheduledTimePickerSelection();
                ScheduledTimePickerPopup.IsOpen = true;
                SetTransientPopupState(
                    isDatePicker: false,
                    isOpen: true);
            });
        ScheduledHourComboBox.Focus();
        Keyboard.Focus(ScheduledHourComboBox);
        ScheduleFinishScheduledPickerInternalCommit();
    }

    private void ConfigureScheduledTimePickerTarget(
        ScheduledTimePickerTarget target)
    {
        _scheduledTimePickerTarget = target;
        FrameworkElement placementTarget = target switch
        {
            ScheduledTimePickerTarget.QuietStart =>
                ScheduledQuietHoursStartInput,
            ScheduledTimePickerTarget.QuietEnd =>
                ScheduledQuietHoursEndInput,
            _ => ScheduledTimePickerHost
        };
        ScheduledTimePickerPopup.PlacementTarget = placementTarget;
        ScheduledTimePickerPopup.HorizontalOffset =
            (GetScheduledTimePickerTargetWidth(target) - 222d) / 2d;
        ScheduledTimePickerTitle.Text = target switch
        {
            ScheduledTimePickerTarget.QuietStart =>
                "选择免打扰开始时间",
            ScheduledTimePickerTarget.QuietEnd =>
                "选择免打扰结束时间",
            _ => "选择提醒时间"
        };
        ScheduledQuietHoursStartInput.Tag =
            target == ScheduledTimePickerTarget.QuietStart
                ? "Active"
                : null;
        ScheduledQuietHoursEndInput.Tag =
            target == ScheduledTimePickerTarget.QuietEnd
                ? "Active"
                : null;
    }

    private static double GetScheduledTimePickerTargetWidth(
        ScheduledTimePickerTarget target) =>
        target == ScheduledTimePickerTarget.Reminder ? 92d : 67d;

    private void CloseScheduledTimePicker()
    {
        _scheduledPickerInteractionGeneration++;
        _scheduledPickerState = ScheduledPickerState.Closing;
        _scheduledTimePartCloseCause =
            ScheduledTimePartCloseCause.ExplicitClose;
        _activeScheduledTimePart = null;
        ScheduledHourComboBox.IsDropDownOpen = false;
        ScheduledMinuteComboBox.IsDropDownOpen = false;
        ScheduledSecondComboBox.IsDropDownOpen = false;
        _scheduledRepeatUnitCommitIsInternal = true;
        ScheduledRepeatUnitComboBox.IsDropDownOpen = false;

        if (ScheduledTimePickerPopup.IsOpen)
        {
            ScheduledTimePickerPopup.IsOpen = false;
        }

        SetTransientPopupState(
            isDatePicker: false,
            isOpen: false);
        _scheduledTimePartCloseCause = ScheduledTimePartCloseCause.None;
        _scheduledRepeatUnitCommitIsInternal = false;
        _scheduledPickerState = ScheduledPickerState.Closed;
        ScheduledQuietHoursStartInput.Tag = null;
        ScheduledQuietHoursEndInput.Tag = null;
    }

    private void ScheduledTimePickerPopup_Opened(object sender, EventArgs e)
    {
        if (_scheduledPickerState == ScheduledPickerState.Closed)
        {
            _scheduledPickerState = ScheduledPickerState.OpenIdle;
        }

        SetTransientPopupState(
            isDatePicker: false,
            isOpen: true);
    }

    private void ScheduledTimePickerPopup_Closed(object sender, EventArgs e)
    {
        _scheduledPickerInteractionGeneration++;
        _scheduledPickerState = ScheduledPickerState.Closed;
        _scheduledTimePartCloseCause = ScheduledTimePartCloseCause.None;
        _activeScheduledTimePart = null;
        _scheduledRepeatUnitCommitIsInternal = true;
        ScheduledRepeatUnitComboBox.IsDropDownOpen = false;
        _scheduledRepeatUnitCommitIsInternal = false;
        ScheduledQuietHoursStartInput.Tag = null;
        ScheduledQuietHoursEndInput.Tag = null;
        SetTransientPopupState(
            isDatePicker: false,
            isOpen: false);
    }

    private void ScheduledTimePickerPopup_PreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        MarkScheduledPickerInternalInteraction();
    }

    private void ScheduledDatePickerPopup_PreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        MarkScheduledPickerInternalInteraction();
    }

    private void ScheduledTimePickerPopup_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        CloseScheduledTimePicker();
        e.Handled = true;
    }

    private void ScheduledTimePartComboBox_DropDownOpened(
        object sender,
        EventArgs e)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        MarkScheduledPickerInternalInteraction();
        _activeScheduledTimePart = comboBox;
        _scheduledTimePartCloseCause = ScheduledTimePartCloseCause.None;
        _scheduledPickerState = ScheduledPickerState.PartOpen;
    }

    private void ScheduledTimePartComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingScheduledTimePickerSelection ||
            ScheduledHourComboBox.SelectedIndex < 0 ||
            ScheduledMinuteComboBox.SelectedIndex < 0 ||
            ScheduledSecondComboBox.SelectedIndex < 0)
        {
            return;
        }

        MarkScheduledPickerInteractionIfOpen();
        ApplyScheduledTimePickerSelection();
    }

    private void ScheduledTimePartComboBox_DropDownClosed(
        object sender,
        EventArgs e)
    {
        if (!ScheduledTimePickerPopup.IsOpen ||
            _scheduledPickerState is
                ScheduledPickerState.Closing or
                ScheduledPickerState.Closed)
        {
            return;
        }

        _activeScheduledTimePart = null;
        if (_scheduledTimePartCloseCause !=
            ScheduledTimePartCloseCause.None)
        {
            _scheduledTimePartCloseCause =
                ScheduledTimePartCloseCause.None;
            _scheduledPickerState =
                ScheduledPickerState.InternalCommit;
            ScheduleFinishScheduledPickerInternalCommit();
            return;
        }

        // A ComboBox popup can deactivate its owner before WPF finishes the
        // same input transaction. Refresh the interaction generation here so
        // a probe captured before DropDownClosed cannot dismiss the outer
        // reminder-time popup.
        MarkScheduledPickerInternalInteraction();
        _scheduledPickerState = ScheduledPickerState.OpenIdle;
        QueueScheduledPickerOutsideProbe();
    }

    private void ScheduledTimePartComboBox_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            !ScheduledTimePickerPopup.IsOpen)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            CloseScheduledTimePicker();
            e.Handled = true;
            return;
        }

        if (!comboBox.IsDropDownOpen)
        {
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            BeginScheduledTimePartCommit(
                ScheduledTimePartCloseCause.KeyboardCommit);
            comboBox.IsDropDownOpen = false;
            comboBox.Focus();
            Keyboard.Focus(comboBox);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            BeginScheduledTimePartCommit(
                ScheduledTimePartCloseCause.InternalNavigation);
            comboBox.IsDropDownOpen = false;
            comboBox.MoveFocus(
                new TraversalRequest(
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                        ? FocusNavigationDirection.Previous
                        : FocusNavigationDirection.Next));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F4 ||
            (e.Key == Key.System &&
             e.SystemKey == Key.Up &&
             Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)))
        {
            BeginScheduledTimePartCommit(
                ScheduledTimePartCloseCause.InternalNavigation);
            comboBox.IsDropDownOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key is
            Key.Up or
            Key.Down or
            Key.PageUp or
            Key.PageDown or
            Key.Home or
            Key.End)
        {
            MarkScheduledPickerInternalInteraction();
        }
    }

    private void ScheduledTimePartItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ComboBox comboBox)
        {
            return;
        }

        BeginScheduledTimePartCommit(
            ScheduledTimePartCloseCause.MouseCommit);
        comboBox.SelectedItem =
            comboBox.ItemContainerGenerator.ItemFromContainer(item);
        comboBox.IsDropDownOpen = false;
        ApplyScheduledTimePickerSelection();
        Activate();
        comboBox.Focus();
        Keyboard.Focus(comboBox);
        ScheduleFinishScheduledPickerInternalCommit();
        e.Handled = true;
    }

    private void ScheduledTimePickerNowButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now.LocalDateTime;
        if (_scheduledTimePickerTarget ==
            ScheduledTimePickerTarget.Reminder)
        {
            _updatingScheduledTaskDraftClock = true;
            try
            {
                SetScheduledDate(now.Date, markEdited: false);
            }
            finally
            {
                _updatingScheduledTaskDraftClock = false;
            }

            SetScheduledTimePickerSelection(
                now.Hour,
                now.Minute,
                now.Second,
                updateText: true);
            _scheduledTaskDraftClockEdited = true;
        }
        else
        {
            SetScheduledTimePickerSelection(
                now.Hour,
                now.Minute,
                now.Second,
                updateText: false);
            ApplyScheduledTimePickerSelection();
        }

        e.Handled = true;
    }

    private void ScheduledTimePickerConfirmButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var target = _scheduledTimePickerTarget;
        ApplyScheduledTimePickerSelection();
        CloseScheduledTimePicker();
        FocusScheduledTimePickerTarget(target);
        e.Handled = true;
    }

    private void SynchronizeScheduledTimePickerSelection()
    {
        var sourceText = _scheduledTimePickerTarget switch
        {
            ScheduledTimePickerTarget.QuietStart =>
                ScheduledQuietHoursStartInput.Text,
            ScheduledTimePickerTarget.QuietEnd =>
                ScheduledQuietHoursEndInput.Text,
            _ => ScheduledTimeInput.Text
        };
        if (!DateTime.TryParseExact(
                sourceText.Trim(),
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
        {
            parsedTime = DateTimeOffset.Now.LocalDateTime;
        }

        SetScheduledTimePickerSelection(
            parsedTime.Hour,
            parsedTime.Minute,
            parsedTime.Second,
            updateText: false);
    }

    private void SetScheduledTimePickerSelection(
        int hour,
        int minute,
        int second,
        bool updateText)
    {
        _updatingScheduledTimePickerSelection = true;
        try
        {
            ScheduledHourComboBox.SelectedIndex = Math.Clamp(hour, 0, 23);
            ScheduledMinuteComboBox.SelectedIndex = Math.Clamp(minute, 0, 59);
            ScheduledSecondComboBox.SelectedIndex = Math.Clamp(second, 0, 59);
            if (updateText)
            {
                UpdateScheduledTimeTextFromPicker();
            }
        }
        finally
        {
            _updatingScheduledTimePickerSelection = false;
        }
    }

    private void UpdateScheduledTimeTextFromPicker()
    {
        if (ScheduledHourComboBox.SelectedIndex < 0 ||
            ScheduledMinuteComboBox.SelectedIndex < 0 ||
            ScheduledSecondComboBox.SelectedIndex < 0)
        {
            return;
        }

        ScheduledTimeInput.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}",
            ScheduledHourComboBox.SelectedIndex,
            ScheduledMinuteComboBox.SelectedIndex,
            ScheduledSecondComboBox.SelectedIndex);
    }

    private void ApplyScheduledTimePickerSelection()
    {
        if (ScheduledHourComboBox.SelectedIndex < 0 ||
            ScheduledMinuteComboBox.SelectedIndex < 0 ||
            ScheduledSecondComboBox.SelectedIndex < 0)
        {
            return;
        }

        if (_scheduledTimePickerTarget ==
            ScheduledTimePickerTarget.Reminder)
        {
            _scheduledTaskDraftClockEdited = true;
            UpdateScheduledTimeTextFromPicker();
            return;
        }

        var value = string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}",
            ScheduledHourComboBox.SelectedIndex,
            ScheduledMinuteComboBox.SelectedIndex,
            ScheduledSecondComboBox.SelectedIndex);
        if (_scheduledTimePickerTarget ==
            ScheduledTimePickerTarget.QuietStart)
        {
            ScheduledQuietHoursStartInput.Text = value;
        }
        else
        {
            ScheduledQuietHoursEndInput.Text = value;
        }

        _scheduledQuietHoursDraftEdited = true;
        ClearScheduledTaskValidation();
    }

    private void FocusScheduledTimePickerTarget(
        ScheduledTimePickerTarget target)
    {
        var input = target switch
        {
            ScheduledTimePickerTarget.QuietStart =>
                ScheduledQuietHoursStartInput,
            ScheduledTimePickerTarget.QuietEnd =>
                ScheduledQuietHoursEndInput,
            _ => ScheduledTimeInput
        };
        input.Focus();
        Keyboard.Focus(input);
    }

    private void RequestScheduledTaskSubmit()
    {
        if (!TryReadScheduledTaskDraft(
                out var text,
                out var dueAt,
                out var repeatInterval,
                out var repeatRule,
                out var quietHours))
        {
            return;
        }

        if (_editingScheduledTask is { } item)
        {
            ScheduledTaskEditRequested?.Invoke(
                item,
                text,
                dueAt,
                repeatInterval,
                repeatRule,
                quietHours);
            CancelScheduledTaskEdit(resetDraft: true, focusInput: true);
            return;
        }

        ScheduledTaskAddRequested?.Invoke(
            text,
            dueAt,
            repeatInterval,
            repeatRule,
            quietHours);
        ScheduledTaskInput.Clear();
        ResetScheduledTaskDraftClock(DateTimeOffset.Now);
        ResetScheduledRepeatDraft();
        ResetScheduledQuietHoursDraft();
        SetScheduledTaskValidation(string.Empty);
        ScheduledTaskInput.Focus();
    }

    private bool TryReadScheduledTaskDraft(
        out string text,
        out DateTimeOffset dueAt,
        out TimeSpan? repeatInterval,
        out ScheduledRepeatRule? repeatRule,
        out ScheduledQuietHours? quietHours)
    {
        dueAt = default;
        repeatInterval = null;
        repeatRule = null;
        quietHours = null;
        text = ScheduledTaskInput.Text.Trim();
        if (text.Length == 0)
        {
            SetScheduledTaskValidation("先写下要提醒的事情哦");
            ScheduledTaskInput.Focus();
            return false;
        }

        if (_scheduledDate is not { } selectedDate)
        {
            SetScheduledTaskValidation("请选择提醒日期");
            FocusScheduledDateInput();
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
        if (_editingScheduledTask is not null &&
            !_scheduledTaskDraftClockEdited &&
            !_scheduledRepeatDraftEdited)
        {
            dueAt = _editingScheduledOriginalDueAt;
            repeatInterval =
                _editingScheduledOriginalRepeatInterval;
            repeatRule = _editingScheduledOriginalRepeatRule;
            return TryReadScheduledQuietHours(out quietHours);
        }

        if (ScheduledRepeatToggle.IsChecked == true)
        {
            if (_editingScheduledTask is not null &&
                !_scheduledRepeatDraftEdited &&
                _editingScheduledOriginalRepeatRule is null &&
                _editingScheduledOriginalRepeatInterval is { } legacyInterval)
            {
                repeatInterval =
                    ScheduledTaskStore.NormalizeRepeatInterval(
                        legacyInterval);
            }
            else if (!TryReadScheduledRepeatRule(
                         localDateTime,
                         out repeatRule,
                         out repeatInterval,
                         out dueAt))
            {
                return false;
            }
        }

        var now = DateTimeOffset.Now;
        if (dueAt <= now &&
            repeatRule is not null)
        {
            if (!ScheduledRepeatSchedule.TryAdvanceToFuture(
                    repeatRule,
                    dueAt,
                    now,
                    out repeatRule,
                    out dueAt))
            {
                SetScheduledTaskValidation(
                    "循环时间无法自动推进，请换一个时间");
                ScheduledTimeInput.Focus();
                ScheduledTimeInput.SelectAll();
                return false;
            }
        }
        else if (dueAt <= now &&
                 repeatInterval is { } legacyInterval)
        {
            if (!TryAdvanceLegacyScheduledDueAt(
                    dueAt,
                    legacyInterval,
                    now,
                    out dueAt))
            {
                SetScheduledTaskValidation(
                    "循环时间无法自动推进，请换一个时间");
                ScheduledTimeInput.Focus();
                ScheduledTimeInput.SelectAll();
                return false;
            }
        }
        else if (dueAt <= now)
        {
            SetScheduledTaskValidation("提醒时间要晚于现在哦");
            ScheduledTimeInput.Focus();
            ScheduledTimeInput.SelectAll();
            return false;
        }

        return TryReadScheduledQuietHours(out quietHours);
    }

    private bool TryReadScheduledQuietHours(
        out ScheduledQuietHours? quietHours)
    {
        quietHours = null;
        if (ScheduledRepeatToggle.IsChecked != true ||
            ScheduledQuietHoursToggle.IsChecked != true)
        {
            return true;
        }

        if (_editingScheduledTask is not null &&
            !_scheduledQuietHoursDraftEdited)
        {
            quietHours = _editingScheduledOriginalQuietHours;
            return true;
        }

        if (!DateTime.TryParseExact(
                ScheduledQuietHoursStartInput.Text.Trim(),
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start))
        {
            SetScheduledTaskValidation(
                "请选择免打扰开始时间");
            OpenScheduledTimePickerForTarget(
                ScheduledTimePickerTarget.QuietStart);
            return false;
        }

        if (!DateTime.TryParseExact(
                ScheduledQuietHoursEndInput.Text.Trim(),
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end))
        {
            SetScheduledTaskValidation(
                "请选择免打扰结束时间");
            OpenScheduledTimePickerForTarget(
                ScheduledTimePickerTarget.QuietEnd);
            return false;
        }

        if (start.TimeOfDay == end.TimeOfDay)
        {
            SetScheduledTaskValidation(
                "免打扰开始和结束时间不能相同哦");
            OpenScheduledTimePickerForTarget(
                ScheduledTimePickerTarget.QuietEnd);
            return false;
        }

        quietHours = new ScheduledQuietHours
        {
            Start = start.TimeOfDay,
            End = end.TimeOfDay,
            TimeZoneId = TimeZoneInfo.Local.Id
        };
        return true;
    }

    private static bool TryAdvanceLegacyScheduledDueAt(
        DateTimeOffset dueAt,
        TimeSpan repeatInterval,
        DateTimeOffset now,
        out DateTimeOffset futureDueAt)
    {
        futureDueAt = default;
        if (repeatInterval <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            var elapsedTicks = checked(
                now.UtcDateTime.Ticks - dueAt.UtcDateTime.Ticks);
            var steps = checked(elapsedTicks / repeatInterval.Ticks + 1);
            futureDueAt = dueAt.AddTicks(
                checked(steps * repeatInterval.Ticks));
            return futureDueAt > now;
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentOutOfRangeException)
        {
            futureDueAt = default;
            return false;
        }
    }

    private bool TryReadScheduledRepeatRule(
        DateTime selectedLocal,
        out ScheduledRepeatRule? rule,
        out TimeSpan? interval,
        out DateTimeOffset dueAt)
    {
        rule = null;
        interval = null;
        dueAt = default;
        if (!int.TryParse(
                ScheduledRepeatCountInput.Text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var every) ||
            ScheduledRepeatUnitComboBox.SelectedIndex is < 0 or > 2)
        {
            SetScheduledTaskValidation("循环间隔要填写正整数");
            ScheduledRepeatCountInput.Focus();
            ScheduledRepeatCountInput.SelectAll();
            return false;
        }

        var unit = (ScheduledRepeatUnit)
            ScheduledRepeatUnitComboBox.SelectedIndex;
        var maximum = unit switch
        {
            ScheduledRepeatUnit.Minute => 1_439_999,
            ScheduledRepeatUnit.Hour => 23_999,
            ScheduledRepeatUnit.Day => 999,
            _ => 0
        };
        if (every < 1 || every > maximum)
        {
            SetScheduledTaskValidation(
                $"循环{ScheduledRepeatUnitOptions[(int)unit]}数要在 1-{maximum} 之间");
            ScheduledRepeatCountInput.Focus();
            ScheduledRepeatCountInput.SelectAll();
            return false;
        }

        if (!ScheduledRepeatSchedule.TryCreate(
                unit,
                every,
                selectedLocal,
                TimeZoneInfo.Local,
                out rule,
                out dueAt) ||
            !ScheduledRepeatSchedule.TryGetNominalInterval(
                rule,
                out var nominalInterval))
        {
            SetScheduledTaskValidation("这个循环时间无法使用，请换一个");
            ScheduledRepeatCountInput.Focus();
            ScheduledRepeatCountInput.SelectAll();
            rule = null;
            return false;
        }

        interval = nominalInterval;
        return true;
    }

    private void ScheduledTaskEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScheduledTaskItem item })
        {
            return;
        }

        CloseTaskFullTextPreview();
        _taskTextEditWindow?.CloseWithoutSaving();
        OpenScheduledTaskEditor(item);
        e.Handled = true;
    }

    private void OpenScheduledTaskEditor(ScheduledTaskItem item)
    {
        if (_scheduledTaskEditWindow is { IsVisible: true } existing)
        {
            if (ReferenceEquals(existing.Item, item))
            {
                TrackEditorDuringReminderInterruption(existing);
                existing.Activate();
                return;
            }

            existing.CloseWithoutSaving();
        }

        var editor = new ScheduledTaskEditWindow(item)
        {
            Owner = this
        };
        _scheduledTaskEditWindow = editor;
        TrackEditorDuringReminderInterruption(editor);
        editor.EditAccepted += (
            text,
            dueAt,
            repeatInterval,
            repeatRule,
            quietHours) =>
        {
            ScheduledTaskEditRequested?.Invoke(
                item,
                text,
                dueAt,
                repeatInterval,
                repeatRule,
                quietHours);
        };
        editor.Closed += (_, _) =>
        {
            ReleaseReminderInterruptedEditor(editor);
            if (!ReferenceEquals(_scheduledTaskEditWindow, editor))
            {
                return;
            }

            _scheduledTaskEditWindow = null;
            if (!_hasClosed && !IsTransientPopupOpen)
            {
                TransientInteractionCompleted?.Invoke();
            }
        };
        editor.Show();
    }

    private void BeginScheduledTaskFormEdit(ScheduledTaskItem item)
    {
        CloseScheduledPickers();
        _editingScheduledTask = item;
        _editingScheduledOriginalDueAt = item.DueAt;
        _editingScheduledOriginalRepeatInterval =
            item.RepeatInterval;
        _editingScheduledOriginalRepeatRule = item.RepeatRule;
        _editingScheduledOriginalQuietHours = item.QuietHours;
        _scheduledTaskDraftClockEdited = false;
        _scheduledRepeatDraftEdited = false;
        _scheduledQuietHoursDraftEdited = false;
        var localDueAt = item.DueAt.ToLocalTime();
        ScheduledTaskInput.Text = item.Text;
        SetScheduledDate(localDueAt.Date, markEdited: false);
        ScheduledTimeInput.Text = localDueAt.ToString(
            "HH:mm:ss",
            CultureInfo.InvariantCulture);
        SetScheduledRepeatDraft(item.RepeatInterval, item.RepeatRule);
        _scheduledRepeatDraftEdited = false;
        SetScheduledQuietHoursDraft(item.QuietHours);
        _scheduledQuietHoursDraftEdited = false;
        ScheduledTaskSubmitButton.Content = "确定修改";
        ScheduledTaskSubmitButton.ToolTip = "保存定时任务修改";
        ScheduledTaskEditCancelButton.Visibility = Visibility.Visible;
        SetScheduledTaskValidation(string.Empty);
        ScheduledTaskInput.Focus();
        Keyboard.Focus(ScheduledTaskInput);
        ScheduledTaskInput.SelectAll();
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
        _editingScheduledOriginalDueAt = default;
        _editingScheduledOriginalRepeatInterval = null;
        _editingScheduledOriginalRepeatRule = null;
        _scheduledRepeatDraftEdited = false;
        _editingScheduledOriginalQuietHours = null;
        _scheduledQuietHoursDraftEdited = false;
        ScheduledTaskSubmitButton.Content = "新增";
        ScheduledTaskSubmitButton.ToolTip = "添加定时任务";
        ScheduledTaskEditCancelButton.Visibility = Visibility.Collapsed;
        SetScheduledTaskValidation(string.Empty);

        if (resetDraft)
        {
            ScheduledTaskInput.Clear();
            ResetScheduledTaskDraftClock(DateTimeOffset.Now);
            ResetScheduledRepeatDraft();
            ResetScheduledQuietHoursDraft();
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
            CloseTaskFullTextPreview();
            if (!ConfirmDeleteForSession(
                    "定时任务",
                    item.Text,
                    CuteConfirmationTheme.ScheduledWarm))
            {
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(item, _editingScheduledTask))
            {
                CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
            }

            ScheduledTaskDeleteRequested?.Invoke(item);
            e.Handled = true;
        }
    }

    private void ScheduledTaskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearScheduledTaskValidation();
    }

    private void ScheduledTimeInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearScheduledTaskValidation();
        UpdateScheduledRepeatRulePreview();
    }

    private void ScheduledRepeatToggle_Changed(
        object sender,
        RoutedEventArgs e)
    {
        MarkScheduledPickerInteractionIfOpen();
        if (ScheduledRepeatToggle.IsChecked != true &&
            _scheduledTimePickerTarget !=
                ScheduledTimePickerTarget.Reminder &&
            ScheduledTimePickerPopup.IsOpen)
        {
            CloseScheduledTimePicker();
        }

        UpdateScheduledRepeatEditorVisibility();
        if (!_updatingScheduledRepeatDraft)
        {
            _scheduledRepeatDraftEdited = true;
            ClearScheduledTaskValidation();
        }

        UpdateScheduledRepeatRulePreview();
    }

    private void ScheduledQuietHoursToggle_Changed(
        object sender,
        RoutedEventArgs e)
    {
        MarkScheduledPickerInteractionIfOpen();
        if (ScheduledQuietHoursToggle.IsChecked != true &&
            _scheduledTimePickerTarget !=
                ScheduledTimePickerTarget.Reminder &&
            ScheduledTimePickerPopup.IsOpen)
        {
            CloseScheduledTimePicker();
        }

        if (!_updatingScheduledQuietHoursDraft)
        {
            _scheduledQuietHoursDraftEdited = true;
            ClearScheduledTaskValidation();
        }
    }

    private void ScheduledQuietHoursTimeInput_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_updatingScheduledQuietHoursDraft)
        {
            return;
        }

        MarkScheduledPickerInteractionIfOpen();
        _scheduledQuietHoursDraftEdited = true;
        ClearScheduledTaskValidation();
    }

    private void ScheduledRepeatInput_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!_updatingScheduledRepeatDraft)
        {
            MarkScheduledPickerInteractionIfOpen();
            _scheduledRepeatDraftEdited = true;
            ClearScheduledTaskValidation();
        }

        UpdateScheduledRepeatRulePreview();
    }

    private void ScheduledRepeatInput_PreviewTextInput(
        object sender,
        TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private void ScheduledRepeatUnitComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_updatingScheduledRepeatDraft &&
            IsScheduledPickerOpen())
        {
            if (sender is ComboBox { IsDropDownOpen: true })
            {
                _scheduledRepeatUnitCommitIsInternal = true;
            }

            MarkScheduledPickerInternalInteraction();
        }

        if (!_updatingScheduledRepeatDraft)
        {
            _scheduledRepeatDraftEdited = true;
            ClearScheduledTaskValidation();
        }

        UpdateScheduledRepeatRulePreview();
    }

    private void ScheduledRepeatUnitComboBox_DropDownOpened(
        object sender,
        EventArgs e)
    {
        if (!IsScheduledPickerOpen())
        {
            return;
        }

        MarkScheduledPickerInternalInteraction();
        _scheduledRepeatUnitCommitIsInternal = false;
        _scheduledPickerState = ScheduledPickerState.PartOpen;
    }

    private void ScheduledRepeatUnitComboBox_DropDownClosed(
        object sender,
        EventArgs e)
    {
        if (!IsScheduledPickerOpen())
        {
            return;
        }

        if (_scheduledRepeatUnitCommitIsInternal)
        {
            _scheduledRepeatUnitCommitIsInternal = false;
            _scheduledPickerState =
                ScheduledPickerState.InternalCommit;
            ScheduleFinishScheduledPickerInternalCommit();
            return;
        }

        MarkScheduledPickerInternalInteraction();
        _scheduledPickerState = ScheduledPickerState.OpenIdle;
        QueueScheduledPickerOutsideProbe();
    }

    private void ScheduledRepeatUnitComboBox_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (!IsScheduledPickerOpen() ||
            sender is not ComboBox comboBox ||
            !comboBox.IsDropDownOpen)
        {
            return;
        }

        if (e.Key is
            Key.Enter or
            Key.Space or
            Key.Tab or
            Key.Escape or
            Key.F4 or
            Key.Up or
            Key.Down or
            Key.PageUp or
            Key.PageDown or
            Key.Home or
            Key.End)
        {
            _scheduledRepeatUnitCommitIsInternal = true;
            MarkScheduledPickerInternalInteraction();
        }
    }

    private void ScheduledRepeatUnitItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not
                ComboBox comboBox)
        {
            return;
        }

        _scheduledRepeatUnitCommitIsInternal = true;
        MarkScheduledPickerInteractionIfOpen();
        comboBox.SelectedItem =
            comboBox.ItemContainerGenerator.ItemFromContainer(item);
        comboBox.IsDropDownOpen = false;
        Activate();
        comboBox.Focus();
        Keyboard.Focus(comboBox);
        e.Handled = true;
    }

    private bool IsScheduledPickerOpen() =>
        ScheduledDatePickerPopup?.IsOpen == true ||
        ScheduledTimePickerPopup?.IsOpen == true;

    private void MarkScheduledPickerInteractionIfOpen()
    {
        if (IsScheduledPickerOpen())
        {
            MarkScheduledPickerInternalInteraction();
        }
    }

    private void SetScheduledRepeatDraft(
        TimeSpan? repeatInterval,
        ScheduledRepeatRule? repeatRule = null)
    {
        var normalized = ScheduledTaskStore.NormalizeRepeatInterval(
            repeatInterval);
        var unit = ScheduledRepeatUnit.Hour;
        var every = 1L;
        if (repeatRule is { } rule &&
            ScheduledRepeatSchedule.TryGetNominalInterval(
                rule,
                out _))
        {
            unit = rule.Unit;
            every = rule.Every;
        }
        else if (normalized is { } value)
        {
            var totalMinutes = Math.Max(1L, (long)value.TotalMinutes);
            if (totalMinutes % (24 * 60) == 0)
            {
                unit = ScheduledRepeatUnit.Day;
                every = totalMinutes / (24 * 60);
            }
            else if (totalMinutes % 60 == 0)
            {
                unit = ScheduledRepeatUnit.Hour;
                every = totalMinutes / 60;
            }
            else
            {
                unit = ScheduledRepeatUnit.Minute;
                every = totalMinutes;
            }
        }

        _updatingScheduledRepeatDraft = true;
        try
        {
            ScheduledRepeatToggle.IsChecked =
                normalized is not null || repeatRule is not null;
            ScheduledRepeatCountInput.Text =
                every.ToString(CultureInfo.InvariantCulture);
            ScheduledRepeatUnitComboBox.SelectedIndex = (int)unit;
            UpdateScheduledRepeatEditorVisibility();
            UpdateScheduledRepeatRulePreview();
        }
        finally
        {
            _updatingScheduledRepeatDraft = false;
        }
    }

    private void ResetScheduledRepeatDraft() =>
        SetScheduledRepeatDraft(repeatInterval: null);

    private void SetScheduledQuietHoursDraft(
        ScheduledQuietHours? quietHours)
    {
        _updatingScheduledQuietHoursDraft = true;
        try
        {
            ScheduledQuietHoursToggle.IsChecked = quietHours is not null;
            ScheduledQuietHoursStartInput.Text =
                FormatScheduledQuietTime(
                    quietHours?.Start ?? TimeSpan.FromHours(22));
            ScheduledQuietHoursEndInput.Text =
                FormatScheduledQuietTime(
                    quietHours?.End ?? TimeSpan.FromHours(7));
        }
        finally
        {
            _updatingScheduledQuietHoursDraft = false;
        }
    }

    private void ResetScheduledQuietHoursDraft()
    {
        SetScheduledQuietHoursDraft(quietHours: null);
        _scheduledQuietHoursDraftEdited = false;
    }

    private static string FormatScheduledQuietTime(TimeSpan value) =>
        value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private void UpdateScheduledRepeatEditorVisibility()
    {
        if (ScheduledDatePickerHost is null ||
            ScheduledTimePickerHost is null ||
            ScheduledRepeatHintText is null ||
            ScheduledRepeatRulePreviewText is null)
        {
            return;
        }

        // A recurring task still needs an explicit first/next occurrence.
        // Keep both pickers visible and preserve an already-open time popup;
        // the interval controls only define occurrences after that anchor.
        ScheduledDatePickerHost.Visibility = Visibility.Visible;
        ScheduledTimePickerHost.Visibility = Visibility.Visible;
        ScheduledRepeatHintText.Visibility = Visibility.Collapsed;
        UpdateScheduledRepeatRulePreview();
    }

    private void UpdateScheduledRepeatRulePreview()
    {
        if (ScheduledRepeatRulePreviewText is null)
        {
            return;
        }

        var repeatUnitComboBox = ScheduledRepeatUnitComboBox;
        var repeatCountInput = ScheduledRepeatCountInput;
        var scheduledTimeInput = ScheduledTimeInput;
        if (ScheduledRepeatToggle?.IsChecked != true ||
            ScheduledTaskValidationText?.Text.Length > 0 ||
            repeatUnitComboBox is null ||
            repeatUnitComboBox.SelectedIndex is < 0 or > 2 ||
            repeatCountInput is null ||
            !int.TryParse(
                repeatCountInput.Text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var every) ||
            every < 1 ||
            scheduledTimeInput is null ||
            !DateTime.TryParseExact(
                scheduledTimeInput.Text.Trim(),
                "HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var selectedTime))
        {
            ScheduledRepeatRulePreviewText.Text = string.Empty;
            ScheduledRepeatRulePreviewText.Visibility =
                Visibility.Collapsed;
            return;
        }

        var unit = (ScheduledRepeatUnit)
            repeatUnitComboBox.SelectedIndex;
        ScheduledRepeatRulePreviewText.Text = unit switch
        {
            ScheduledRepeatUnit.Minute =>
                $"每 {every} 分钟，在第 {selectedTime.Second:00} 秒提醒",
            ScheduledRepeatUnit.Hour =>
                $"每 {every} 小时，在第 {selectedTime.Minute:00} 分 {selectedTime.Second:00} 秒提醒",
            ScheduledRepeatUnit.Day =>
                $"每 {every} 天，在 {selectedTime:HH:mm:ss} 提醒",
            _ => string.Empty
        };
        ScheduledRepeatRulePreviewText.Visibility =
            Visibility.Visible;
    }

    private void SetTransientPopupState(bool isDatePicker, bool isOpen)
    {
        var wasOpen = IsTransientPopupOpen;
        if (isDatePicker)
        {
            _isScheduledDatePickerPopupOpen = isOpen;
        }
        else
        {
            _isScheduledTimePickerPopupOpen = isOpen;
        }

        if (wasOpen &&
            !IsTransientPopupOpen &&
            !_switchingScheduledPickerPopup)
        {
            TransientInteractionCompleted?.Invoke();
        }
    }

    private void TodoWindow_TransientPopupDeactivated(
        object? sender,
        EventArgs e)
    {
        QueueScheduledPickerOutsideProbe();
    }

    private void BeginScheduledTimePartCommit(
        ScheduledTimePartCloseCause cause)
    {
        MarkScheduledPickerInternalInteraction();
        _scheduledTimePartCloseCause = cause;
        _scheduledPickerState = ScheduledPickerState.InternalCommit;
    }

    private void BeginScheduledPickerInternalCommit()
    {
        MarkScheduledPickerInternalInteraction();
        _scheduledPickerState = ScheduledPickerState.InternalCommit;
    }

    private void MarkScheduledPickerInternalInteraction()
    {
        _scheduledPickerInteractionGeneration++;
    }

    private void ScheduleFinishScheduledPickerInternalCommit()
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.SystemIdle,
            _finishScheduledPickerInternalCommitAction);
    }

    private void FinishScheduledPickerInternalCommit()
    {
        if (_hasClosed ||
            !ScheduledTimePickerPopup.IsOpen ||
            _scheduledPickerState is
                ScheduledPickerState.Closing or
                ScheduledPickerState.Closed)
        {
            return;
        }

        _scheduledPickerState =
            IsScheduledTimePartDropDownOpen() ||
            ScheduledRepeatUnitComboBox.IsDropDownOpen
            ? ScheduledPickerState.PartOpen
            : ScheduledPickerState.OpenIdle;
    }

    private void QueueScheduledPickerOutsideProbe()
    {
        if (_hasClosed || !IsScheduledPickerOpen())
        {
            return;
        }

        var probe = new ScheduledPickerOutsideProbe(
            _scheduledPickerInteractionGeneration,
            _scheduledPickerState);

        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
                ProcessScheduledPickerOutsideProbe(probe)));
    }

    private void ProcessScheduledPickerOutsideProbe(
        ScheduledPickerOutsideProbe probe)
    {
        var timePickerOpen = ScheduledTimePickerPopup.IsOpen;
        if (_hasClosed ||
            !IsScheduledPickerOpen() ||
            probe.StateAtQueue == ScheduledPickerState.InternalCommit ||
            probe.InteractionGeneration !=
                _scheduledPickerInteractionGeneration ||
            (timePickerOpen &&
             (IsScheduledTimePartDropDownOpen() ||
              ScheduledRepeatUnitComboBox.IsDropDownOpen)))
        {
            return;
        }

        // Deactivated and DropDownClosed can run while WPF is still moving
        // focus between an outer Popup and a ComboBox child Popup. Sample the
        // settled input state at ApplicationIdle before deciding that the user
        // really clicked outside all reminder-time surfaces.
        var currentForegroundWindow = GetForegroundWindow();
        var currentWindowAtPointer =
            GetCursorPos(out var currentPointer)
                ? WindowFromPoint(currentPointer)
                : IntPtr.Zero;
        if (IsKnownScheduledPickerWindow(currentForegroundWindow) ||
            IsKnownScheduledPickerWindow(currentWindowAtPointer) ||
            IsPointerOverScheduledPickerSurface())
        {
            return;
        }

        // If Win32 could not identify either current target, fail safe and keep
        // the picker. A later real outside click or Esc can still close it,
        // while guessing here would reproduce the hour-selection bug.
        if (currentForegroundWindow == IntPtr.Zero &&
            currentWindowAtPointer == IntPtr.Zero)
        {
            return;
        }

        CloseScheduledPickers();
    }

    private bool IsPointerOverScheduledPickerSurface()
    {
        return ScheduledDatePickerHost.IsMouseOver ||
               ScheduledTimePickerHost.IsMouseOver ||
               ScheduledRepeatEditor.IsMouseOver ||
               ScheduledQuietHoursEditor.IsMouseOver ||
               IsPointerOverPopupChild(ScheduledDatePickerPopup) ||
               IsPointerOverPopupChild(ScheduledTimePickerPopup) ||
               IsPointerOverComboBoxPopup(ScheduledHourComboBox) ||
               IsPointerOverComboBoxPopup(ScheduledMinuteComboBox) ||
               IsPointerOverComboBoxPopup(ScheduledSecondComboBox) ||
               IsPointerOverComboBoxPopup(ScheduledRepeatUnitComboBox);
    }

    private static bool IsPointerOverComboBoxPopup(ComboBox comboBox)
    {
        return comboBox.Template.FindName("PART_Popup", comboBox) is
                   Popup popup &&
               IsPointerOverPopupChild(popup);
    }

    private static bool IsPointerOverPopupChild(Popup popup)
    {
        return popup.Child is UIElement child && child.IsMouseOver;
    }

    private bool IsKnownScheduledPickerWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (new WindowInteropHelper(this).Handle == handle)
        {
            return true;
        }

        return IsPopupWindowHandle(ScheduledDatePickerPopup, handle) ||
               IsPopupWindowHandle(ScheduledTimePickerPopup, handle) ||
               IsComboBoxPopupWindowHandle(
                   ScheduledHourComboBox,
                   handle) ||
               IsComboBoxPopupWindowHandle(
                   ScheduledMinuteComboBox,
                   handle) ||
               IsComboBoxPopupWindowHandle(
                   ScheduledSecondComboBox,
                   handle) ||
               IsComboBoxPopupWindowHandle(
                   ScheduledRepeatUnitComboBox,
                   handle);
    }

    private static bool IsComboBoxPopupWindowHandle(
        ComboBox comboBox,
        IntPtr handle)
    {
        return comboBox.Template.FindName("PART_Popup", comboBox) is
                   Popup popup &&
               IsPopupWindowHandle(popup, handle);
    }

    private static bool IsPopupWindowHandle(Popup popup, IntPtr handle)
    {
        return popup.Child is Visual child &&
               PresentationSource.FromVisual(child) is
                   HwndSource source &&
               source.Handle == handle;
    }

    private bool IsScheduledTimePartDropDownOpen() =>
        ScheduledHourComboBox.IsDropDownOpen ||
        ScheduledMinuteComboBox.IsDropDownOpen ||
        ScheduledSecondComboBox.IsDropDownOpen;

    private void PrepareScheduledTaskDraftClockForDisplay(DateTimeOffset now)
    {
        if (_editingScheduledTask is not null ||
            !string.IsNullOrWhiteSpace(ScheduledTaskInput.Text) ||
            _scheduledTaskDraftClockEdited)
        {
            return;
        }

        ResetScheduledTaskDraftClock(now);
    }

    private void ResetScheduledTaskDraftClock(DateTimeOffset now)
    {
        var suggested = now.LocalDateTime;
        suggested = new DateTime(
            suggested.Year,
            suggested.Month,
            suggested.Day,
            suggested.Hour,
            suggested.Minute,
            suggested.Second,
            DateTimeKind.Unspecified);
        _updatingScheduledTaskDraftClock = true;
        try
        {
            SetScheduledDate(suggested.Date, markEdited: false);
            ScheduledTimeInput.Text = suggested.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            SetScheduledTimePickerSelection(
                suggested.Hour,
                suggested.Minute,
                suggested.Second,
                updateText: false);
            _scheduledTaskDraftClockEdited = false;
        }
        finally
        {
            _updatingScheduledTaskDraftClock = false;
        }
    }

    private void ClearScheduledTaskValidation()
    {
        if (ScheduledTaskValidationText is not null &&
            ScheduledTaskValidationText.Text.Length > 0)
        {
            ScheduledTaskValidationText.Text = string.Empty;
        }

        UpdateScheduledRepeatRulePreview();
    }

    private void SetScheduledTaskValidation(string message)
    {
        ScheduledTaskValidationText.Text = message;
        UpdateScheduledRepeatRulePreview();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOwnedEditorsWithoutSaving();
        CloseScheduledPickers();
        CancelScheduledTaskEdit(resetDraft: true, focusInput: false);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOwnedEditorsWithoutSaving();
        CloseScheduledPickers();
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

    private void OpenTaskTextEditor(
        string title,
        string text,
        Action<string> acceptText,
        Action? openAdvancedEditor = null)
    {
        _scheduledTaskEditWindow?.CloseWithoutSaving();

        if (_taskTextEditWindow is { IsVisible: true } existing)
        {
            TrackEditorDuringReminderInterruption(existing);
            existing.Activate();
            return;
        }

        CloseTaskFullTextPreview();
        CommitPendingTodoEdit();
        var editor = new TaskTextEditWindow(
            title,
            text,
            showAdvancedEdit: openAdvancedEditor is not null)
        {
            Owner = this
        };
        _taskTextEditWindow = editor;
        TrackEditorDuringReminderInterruption(editor);
        editor.TextAccepted += acceptText;
        if (openAdvancedEditor is not null)
        {
            editor.AdvancedEditRequested += openAdvancedEditor;
        }

        editor.Closed += (_, _) =>
        {
            ReleaseReminderInterruptedEditor(editor);
            if (!ReferenceEquals(_taskTextEditWindow, editor))
            {
                return;
            }

            _taskTextEditWindow = null;
            if (!_hasClosed && !IsTransientPopupOpen)
            {
                TransientInteractionCompleted?.Invoke();
            }
        };
        editor.Show();
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TodoItem item })
        {
            return;
        }

        OpenTodoItemEditor(item);
        e.Handled = true;
    }

    private void OpenTodoItemEditor(TodoItem item)
    {
        OpenTaskTextEditor(
            "修改待办",
            item.Text,
            updatedText =>
            {
                if (string.Equals(
                        item.Text,
                        updatedText,
                        StringComparison.Ordinal))
                {
                    return;
                }

                item.Text = updatedText;
                TodoEdited?.Invoke(item);
            });
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
        if (sender is not TextBox
            {
                IsReadOnly: true,
                DataContext: TodoItem item
            } ||
            e.Key != Key.F2)
        {
            return;
        }

        OpenTodoItemEditor(item);
        e.Handled = true;
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
        var originalSource = e.OriginalSource as DependencyObject;
        if (e.ChangedButton == MouseButton.Left &&
            IsWithin(originalSource, PetSizeSlider))
        {
            // Slider handles a move-to-point Track press in its class handler
            // before the Slider instance's PreviewMouseLeftButtonDown handler.
            // Begin at the owning Window while the tunneling event is still on
            // its way to Slider, so ValueChanged is coalesced into the same
            // composition-driven gesture as a Thumb drag.
            BeginPetSizeAdjustment();
            if (!IsPetSizeSliderThumbInteraction(originalSource) &&
                !ReferenceEquals(Mouse.Captured, PetSizeSlider) &&
                !Mouse.Capture(PetSizeSlider, CaptureMode.SubTree))
            {
                // A rare failed capture must not strand the adjustment if the
                // pointer is released outside the Track. Slider updates its
                // move-to-point value synchronously before this fallback runs.
                var adjustmentGeneration = _petSizeAdjustmentGeneration;
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(
                        () => EndPetSizeAdjustmentIfCurrent(
                            adjustmentGeneration)));
            }
        }

        var isRepeatEditorInteraction =
            IsWithin(originalSource, ScheduledRepeatEditor) ||
            IsWithin(originalSource, ScheduledQuietHoursEditor) ||
            IsWithinScheduledTimePartPopup(
                originalSource,
                ScheduledRepeatUnitComboBox);
        var isTimePartInteraction =
            IsScheduledTimePartInteraction(originalSource);
        if (isRepeatEditorInteraction ||
            isTimePartInteraction ||
            IsWithin(originalSource, ScheduledDatePickerHost) ||
            IsWithin(originalSource, ScheduledTimePickerHost))
        {
            MarkScheduledPickerInternalInteraction();
        }

        if (ScheduledDatePickerPopup.IsOpen &&
            !isRepeatEditorInteraction &&
            ShouldCloseScheduledPickerPopup(
                originalSource,
                ScheduledDatePickerHost,
                ScheduledTimePickerHost,
                ScheduledDatePickerPopup))
        {
            CloseScheduledDatePicker();
        }

        if (ScheduledTimePickerPopup.IsOpen &&
            !isRepeatEditorInteraction &&
            !isTimePartInteraction &&
            ShouldCloseScheduledPickerPopup(
                originalSource,
                ScheduledTimePickerHost,
                ScheduledDatePickerHost,
                ScheduledTimePickerPopup))
        {
            CloseScheduledTimePicker();
        }

        var textBox = _editingTodoTextBox;
        if (textBox is null ||
            IsWithin(originalSource, textBox))
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

    private bool IsScheduledTimePartInteraction(DependencyObject? source)
    {
        return IsWithinScheduledTimePartPopup(
                   source,
                   ScheduledHourComboBox) ||
               IsWithinScheduledTimePartPopup(
                   source,
                   ScheduledMinuteComboBox) ||
               IsWithinScheduledTimePartPopup(
                   source,
                   ScheduledSecondComboBox);
    }

    private static bool IsWithinScheduledTimePartPopup(
        DependencyObject? source,
        ComboBox comboBox)
    {
        return comboBox.Template.FindName("PART_Popup", comboBox) is
                   Popup { Child: DependencyObject popupChild } &&
               IsWithin(source, popupChild);
    }

    private static bool ShouldCloseScheduledPickerPopup(
        DependencyObject? source,
        DependencyObject pickerHost,
        DependencyObject peerPickerHost,
        Popup popup)
    {
        return !IsWithin(source, pickerHost) &&
               !IsWithin(source, peerPickerHost) &&
               (popup.Child is not DependencyObject popupChild ||
                !IsWithin(source, popupChild));
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
        TaskRowTextBox_Unloaded(sender, e);
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

    private bool IsPetSizeSliderThumbInteraction(DependencyObject? source)
    {
        PetSizeSlider.ApplyTemplate();
        return PetSizeSlider.Template.FindName(
                   "PART_Track",
                   PetSizeSlider) is Track { Thumb: { } thumb } &&
               IsWithin(source, thumb);
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
            CloseTaskFullTextPreview();
            if (!ConfirmDeleteForSession(
                    "待办事项",
                    item.Text,
                    CuteConfirmationTheme.TodoBlue))
            {
                e.Handled = true;
                return;
            }

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
            e.Handled = true;
        }
    }

    private bool ConfirmDeleteForSession(
        string itemKind,
        string text,
        CuteConfirmationTheme theme)
    {
        if (_suppressDeleteConfirmationForSession)
        {
            return true;
        }

        const int previewLength = 38;
        var normalizedText = text.Trim();
        var preview = normalizedText.Length <= previewLength
            ? normalizedText
            : $"{normalizedText[..previewLength]}…";
        CuteConfirmationResult result;
        _isDeleteConfirmationOpen = true;
        try
        {
            result = CuteConfirmationWindow.ShowFor(
                this,
                $"删除{itemKind}",
                $"确定删除“{preview}”吗？\n删除后无法恢复。",
                confirmText: "删除",
                showSessionSuppression: true,
                theme: theme);
        }
        finally
        {
            _isDeleteConfirmationOpen = false;
            if (!_hasClosed && !IsTransientPopupOpen)
            {
                TransientInteractionCompleted?.Invoke();
            }
        }

        if (result.Confirmed && result.SuppressForSession)
        {
            _suppressDeleteConfirmationForSession = true;
        }

        return result.Confirmed;
    }

    private void PetSizeSlider_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e) => BeginPetSizeAdjustment();

    private void PetSizeSlider_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        EndPetSizeAdjustment();
        if (ReferenceEquals(Mouse.Captured, PetSizeSlider))
        {
            PetSizeSlider.ReleaseMouseCapture();
        }
    }

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

        _petSizeFirstScalePublishedDuringAdjustment = false;
        _petSizeAdjustmentGeneration = unchecked(
            _petSizeAdjustmentGeneration + 1);
        _petSizeAdjustmentActive = true;
        PetSizeAdjustmentStarted?.Invoke();
    }

    private void EndPetSizeAdjustmentIfCurrent(long expectedGeneration)
    {
        // A capture-failure fallback belongs only to the press that queued it.
        // If the user starts a Thumb drag before that dispatcher callback
        // runs, the stale fallback must not finish the newer gesture.
        if (expectedGeneration != _petSizeAdjustmentGeneration)
        {
            return;
        }

        EndPetSizeAdjustment();
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
        _petSizeFirstScalePublishedDuringAdjustment = false;
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

    internal void CompletePetSizeAdjustmentForInterruption()
    {
        EndPetSizeAdjustment();
        if (Mouse.Captured is DependencyObject captured &&
            IsWithin(captured, PetSizeSlider))
        {
            Mouse.Capture(null);
        }
    }

    private void EdgeRoamingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingEdgeRoamingEnabled)
        {
            EdgeRoamingEnabledChanged?.Invoke(
                EdgeRoamingToggle.IsChecked == true);
        }
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingStartupEnabled)
        {
            StartupEnabledChanged?.Invoke(StartupToggle.IsChecked == true);
        }
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
                if (!_petSizeFirstScalePublishedDuringAdjustment)
                {
                    // Publish only the first real change synchronously while
                    // still inside the input event. MainWindow can prepare its
                    // one-time transparent preview envelope before Rendering;
                    // later high-frequency samples remain coalesced.
                    _petSizeFirstScalePublishedDuringAdjustment = true;
                    PetSizeScaleChanged?.Invoke(scale);
                }
                else
                {
                    QueuePetSizeScaleChanged(scale);
                }
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private enum ScheduledTimePickerTarget
    {
        Reminder,
        QuietStart,
        QuietEnd
    }

    private enum ScheduledPickerState
    {
        Closed,
        OpenIdle,
        PartOpen,
        InternalCommit,
        Closing
    }

    private enum ScheduledTimePartCloseCause
    {
        None,
        MouseCommit,
        KeyboardCommit,
        InternalNavigation,
        ExplicitClose
    }

    private readonly record struct ScheduledPickerOutsideProbe(
        long InteractionGeneration,
        ScheduledPickerState StateAtQueue);

    private sealed record TaskFullTextTheme(
        Brush ChromeBackground,
        Brush ChromeBorder,
        Color Shadow,
        Brush Title,
        Brush TextBackground,
        Brush TextBorder,
        Brush TextForeground,
        Brush Selection,
        Brush Count,
        Brush ScrollTrack,
        Brush ScrollDrag,
        Brush ScrollThumb,
        Brush ScrollHover);

    private sealed record TaskPageControlTheme(
        Brush SettingsText,
        Brush SwitchTrack,
        Brush SwitchBorder,
        Brush SwitchHoverTrack,
        Brush SwitchHoverBorder,
        Brush SwitchCheckedTrack,
        Brush SwitchCheckedBorder,
        Brush SwitchKnobFill,
        Brush SwitchKnobStroke,
        Brush SwitchFace,
        Color SwitchShadow,
        Brush SliderDecrease,
        Brush SliderIncrease,
        Brush SliderIncreaseBorder,
        Brush SliderShadow,
        Brush SliderThumb,
        Brush SliderThumbBorder,
        Brush SliderThumbHover,
        Brush SliderThumbHoverBorder,
        Brush SliderThumbDrag,
        Brush SliderFace);
}
