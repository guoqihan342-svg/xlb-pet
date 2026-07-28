using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class ScheduledTaskEditWindow : Window
{
    private const double TargetEditorWidth = 378;
    private const double TargetEditorHeight = 208;
    private static readonly string[] HourOptions =
        CreateClockPartOptions(24);
    private static readonly string[] MinuteSecondOptions =
        CreateClockPartOptions(60);
    private static readonly string[] RepeatUnitOptions =
        ["分钟", "小时", "天"];
    private static readonly Regex DigitsOnlyRegex =
        new(@"^\d+$", RegexOptions.CultureInvariant);

    private readonly ScheduledTaskItem _item;
    private readonly DateTimeOffset _originalDueAt;
    private readonly TimeSpan? _originalRepeatInterval;
    private readonly ScheduledRepeatRule? _originalRepeatRule;
    private readonly Action _commitAfterDeactivationAction;
    private readonly Action _positionBesideOwnerAction;
    private readonly OwnedWindowPositioner.PositionCache _positionCache;
    private Window? _positionOwner;
    private bool _allowClose;
    private bool _initializing;
    private bool _scheduleEdited;
    private bool _repeatEdited;
    private bool _internalPopupOpen;
    private bool _positionBesideOwnerQueued;

    public ScheduledTaskEditWindow(ScheduledTaskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        InitializeComponent();
        _item = item;
        _originalDueAt = item.DueAt;
        _originalRepeatInterval = item.RepeatInterval;
        _originalRepeatRule = item.RepeatRule;
        _commitAfterDeactivationAction = CommitAfterDeactivation;
        _positionBesideOwnerAction = PositionBesideOwner;
        _positionCache = new OwnedWindowPositioner.PositionCache(this);

        _initializing = true;
        try
        {
            HourComboBox.ItemsSource = HourOptions;
            MinuteComboBox.ItemsSource = MinuteSecondOptions;
            SecondComboBox.ItemsSource = MinuteSecondOptions;
            RepeatUnitComboBox.ItemsSource = RepeatUnitOptions;

            var localDueAt = item.DueAt.ToLocalTime();
            TaskTextBox.Text = item.Text;
            DueDatePicker.SelectedDate = localDueAt.Date;
            HourComboBox.SelectedIndex = localDueAt.Hour;
            MinuteComboBox.SelectedIndex = localDueAt.Minute;
            SecondComboBox.SelectedIndex = localDueAt.Second;
            SetRepeatDraft(item.RepeatInterval, item.RepeatRule);
        }
        finally
        {
            _initializing = false;
        }

        Activated += ScheduledTaskEditWindow_Activated;
        Deactivated += ScheduledTaskEditWindow_Deactivated;
        Closing += ScheduledTaskEditWindow_Closing;
        Closed += ScheduledTaskEditWindow_Closed;
        Loaded += ScheduledTaskEditWindow_Loaded;
        DpiChanged += ScheduledTaskEditWindow_DpiChanged;
    }

    public event Action<
        string,
        DateTimeOffset,
        TimeSpan?,
        ScheduledRepeatRule?>?
        EditAccepted;

    public ScheduledTaskItem Item => _item;

    public void CloseWithoutSaving()
    {
        _allowClose = true;
        if (IsLoaded)
        {
            Close();
        }
    }

    public bool SaveAndClose() => CommitAndClose();

    private static string[] CreateClockPartOptions(int count)
    {
        var options = new string[count];
        for (var value = 0; value < count; value++)
        {
            options[value] =
                value.ToString("00", CultureInfo.InvariantCulture);
        }

        return options;
    }

    private void SetRepeatDraft(
        TimeSpan? repeatInterval,
        ScheduledRepeatRule? repeatRule)
    {
        var normalized = ScheduledTaskStore.NormalizeRepeatInterval(
            repeatInterval);
        var unit = ScheduledRepeatUnit.Hour;
        var every = 1L;
        if (repeatRule is { } rule &&
            ScheduledRepeatSchedule.TryGetNominalInterval(rule, out _))
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

        RepeatToggle.IsChecked =
            normalized is not null || repeatRule is not null;
        RepeatCountTextBox.Text =
            every.ToString(CultureInfo.InvariantCulture);
        RepeatUnitComboBox.SelectedIndex = (int)unit;
    }

    private void ScheduledTaskEditWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        AttachPositionOwner();
        ApplyEditorSizeForOwnerWorkArea();
        UpdateLayout();
        PositionBesideOwner();
        Opacity = 1;
        TaskTextBox.Focus();
        Keyboard.Focus(TaskTextBox);
        TaskTextBox.SelectAll();
    }

    private void AttachPositionOwner()
    {
        var owner = Owner;
        if (ReferenceEquals(_positionOwner, owner))
        {
            return;
        }

        DetachPositionOwner();
        _positionOwner = owner;
        if (_positionOwner is null)
        {
            return;
        }

        _positionOwner.LocationChanged += PositionOwner_GeometryChanged;
        _positionOwner.SizeChanged += PositionOwner_SizeChanged;
        _positionOwner.StateChanged += PositionOwner_GeometryChanged;
        _positionOwner.DpiChanged += PositionOwner_DpiChanged;
        _positionCache.InvalidateGeometry();
    }

    private void DetachPositionOwner()
    {
        if (_positionOwner is null)
        {
            return;
        }

        _positionOwner.LocationChanged -= PositionOwner_GeometryChanged;
        _positionOwner.SizeChanged -= PositionOwner_SizeChanged;
        _positionOwner.StateChanged -= PositionOwner_GeometryChanged;
        _positionOwner.DpiChanged -= PositionOwner_DpiChanged;
        _positionOwner = null;
        _positionCache.InvalidateGeometry();
    }

    private void ApplyEditorSizeForOwnerWorkArea()
    {
        var width = TargetEditorWidth;
        var height = TargetEditorHeight;
        if (_positionOwner is { } owner)
        {
            var workArea = MonitorWorkArea.GetForWindow(owner);
            width = Math.Min(width, Math.Max(1, workArea.Width));
            height = Math.Min(height, Math.Max(1, workArea.Height));
        }

        if (Math.Abs(Width - width) < 0.1 &&
            Math.Abs(Height - height) < 0.1)
        {
            return;
        }

        Width = width;
        Height = height;
        _positionCache.InvalidateGeometry();
    }

    private void SchedulePositionBesideOwner()
    {
        if (_positionBesideOwnerQueued || !IsVisible)
        {
            return;
        }

        _positionBesideOwnerQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            _positionBesideOwnerAction);
    }

    private void PositionBesideOwner()
    {
        _positionBesideOwnerQueued = false;
        if (_positionOwner is not { IsLoaded: true } owner ||
            !IsVisible)
        {
            return;
        }

        ApplyEditorSizeForOwnerWorkArea();
        UpdateLayout();
        if (!OwnedWindowPositioner.TryPosition(
                owner,
                this,
                _positionCache,
                out _))
        {
            PositionBesideOwnerFallback(owner);
        }
    }

    private void PositionBesideOwnerFallback(Window owner)
    {
        var workArea = MonitorWorkArea.GetForWindow(owner);
        var ownerWidth = owner.ActualWidth > 0
            ? owner.ActualWidth
            : owner.Width;
        var ownerHeight = owner.ActualHeight > 0
            ? owner.ActualHeight
            : owner.Height;
        var leftCandidate = owner.Left - Width;
        var desiredLeft = leftCandidate >= workArea.Left
            ? leftCandidate
            : owner.Left + ownerWidth;
        var desiredTop = owner.Top + ownerHeight - Height;
        var maximumLeft = Math.Max(
            workArea.Left,
            workArea.Right - Width);
        var maximumTop = Math.Max(
            workArea.Top,
            workArea.Bottom - Height);

        Left = Math.Clamp(
            desiredLeft,
            workArea.Left,
            maximumLeft);
        Top = Math.Clamp(
            desiredTop,
            workArea.Top,
            maximumTop);
    }

    private void PositionOwner_GeometryChanged(
        object? sender,
        EventArgs e)
    {
        SchedulePositionBesideOwner();
    }

    private void PositionOwner_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        SchedulePositionBesideOwner();
    }

    private void PositionOwner_DpiChanged(
        object sender,
        DpiChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        SchedulePositionBesideOwner();
    }

    private void ScheduledTaskEditWindow_DpiChanged(
        object sender,
        DpiChangedEventArgs e)
    {
        _positionCache.InvalidateGeometry();
        SchedulePositionBesideOwner();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        CommitAndClose();
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithoutSaving();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseWithoutSaving();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CommitAndClose();
            e.Handled = true;
        }
    }

    private void DraftControl_Changed(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (_initializing)
        {
            return;
        }

        if (ReferenceEquals(sender, RepeatToggle) ||
            ReferenceEquals(sender, RepeatCountTextBox) ||
            ReferenceEquals(sender, RepeatUnitComboBox))
        {
            _repeatEdited = true;
        }
        else if (!ReferenceEquals(sender, TaskTextBox))
        {
            _scheduleEdited = true;
        }
    }

    private void RepeatToggle_Changed(object sender, RoutedEventArgs e)
    {
        DraftControl_Changed(sender, e);
    }

    private void RepeatCountTextBox_PreviewTextInput(
        object sender,
        TextCompositionEventArgs e)
    {
        e.Handled = !DigitsOnlyRegex.IsMatch(e.Text);
    }

    private void DueDatePicker_CalendarOpened(
        object? sender,
        RoutedEventArgs e)
    {
        _internalPopupOpen = true;
    }

    private void DueDatePicker_CalendarClosed(
        object? sender,
        RoutedEventArgs e)
    {
        _internalPopupOpen = IsAnyComboDropDownOpen();
        QueueCommitAfterDeactivation();
    }

    private void InternalComboBox_DropDownOpened(
        object sender,
        EventArgs e)
    {
        _internalPopupOpen = true;
    }

    private void InternalComboBox_DropDownClosed(
        object sender,
        EventArgs e)
    {
        _internalPopupOpen =
            DueDatePicker.IsDropDownOpen ||
            IsAnyComboDropDownOpen();
        QueueCommitAfterDeactivation();
    }

    private bool IsAnyComboDropDownOpen() =>
        HourComboBox.IsDropDownOpen ||
        MinuteComboBox.IsDropDownOpen ||
        SecondComboBox.IsDropDownOpen ||
        RepeatUnitComboBox.IsDropDownOpen;

    private bool CommitAndClose()
    {
        if (!TryReadDraft(
                out var text,
                out var dueAt,
                out var repeatInterval,
                out var repeatRule))
        {
            System.Media.SystemSounds.Beep.Play();
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (IsVisible)
                    {
                        Activate();
                    }
                }));
            return false;
        }

        _allowClose = true;
        EditAccepted?.Invoke(
            text,
            dueAt,
            repeatInterval,
            repeatRule);
        Close();
        return true;
    }

    private bool TryReadDraft(
        out string text,
        out DateTimeOffset dueAt,
        out TimeSpan? repeatInterval,
        out ScheduledRepeatRule? repeatRule)
    {
        text = TaskTextBox.Text.Trim();
        dueAt = default;
        repeatInterval = null;
        repeatRule = null;
        if (text.Length == 0)
        {
            SetValidation("先写下要提醒的事情哦");
            TaskTextBox.Focus();
            return false;
        }

        if (!_scheduleEdited && !_repeatEdited)
        {
            dueAt = _originalDueAt;
            repeatInterval = _originalRepeatInterval;
            repeatRule = _originalRepeatRule;
            return true;
        }

        if (DueDatePicker.SelectedDate is not { } selectedDate ||
            HourComboBox.SelectedIndex is < 0 or > 23 ||
            MinuteComboBox.SelectedIndex is < 0 or > 59 ||
            SecondComboBox.SelectedIndex is < 0 or > 59)
        {
            SetValidation("请选择完整的提醒日期和时分秒");
            DueDatePicker.Focus();
            return false;
        }

        var selectedLocal = DateTime.SpecifyKind(
            selectedDate.Date
                .AddHours(HourComboBox.SelectedIndex)
                .AddMinutes(MinuteComboBox.SelectedIndex)
                .AddSeconds(SecondComboBox.SelectedIndex),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(selectedLocal))
        {
            SetValidation("这个本地时间不存在，请换一个时间");
            DueDatePicker.Focus();
            return false;
        }

        dueAt = new DateTimeOffset(
            selectedLocal,
            TimeZoneInfo.Local.GetUtcOffset(selectedLocal));

        if (RepeatToggle.IsChecked == true)
        {
            if (!_repeatEdited &&
                _originalRepeatRule is null &&
                _originalRepeatInterval is { } legacyInterval)
            {
                repeatInterval =
                    ScheduledTaskStore.NormalizeRepeatInterval(
                        legacyInterval);
            }
            else if (!TryReadRepeatRule(
                         selectedLocal,
                         out repeatRule,
                         out repeatInterval,
                         out dueAt))
            {
                return false;
            }
        }

        if (dueAt <= DateTimeOffset.Now)
        {
            SetValidation("提醒时间要晚于现在哦");
            DueDatePicker.Focus();
            return false;
        }

        return true;
    }

    private bool TryReadRepeatRule(
        DateTime selectedLocal,
        out ScheduledRepeatRule? repeatRule,
        out TimeSpan? repeatInterval,
        out DateTimeOffset dueAt)
    {
        repeatRule = null;
        repeatInterval = null;
        dueAt = default;
        if (!int.TryParse(
                RepeatCountTextBox.Text.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var every) ||
            RepeatUnitComboBox.SelectedIndex is < 0 or > 2)
        {
            SetValidation("循环间隔要填写正整数");
            RepeatCountTextBox.Focus();
            RepeatCountTextBox.SelectAll();
            return false;
        }

        var unit = (ScheduledRepeatUnit)
            RepeatUnitComboBox.SelectedIndex;
        var maximum = unit switch
        {
            ScheduledRepeatUnit.Minute => 1_439_999,
            ScheduledRepeatUnit.Hour => 23_999,
            ScheduledRepeatUnit.Day => 999,
            _ => 0
        };
        if (every < 1 || every > maximum)
        {
            SetValidation(
                $"循环{RepeatUnitOptions[(int)unit]}数要在 1-{maximum} 之间");
            RepeatCountTextBox.Focus();
            RepeatCountTextBox.SelectAll();
            return false;
        }

        if (!ScheduledRepeatSchedule.TryCreate(
                unit,
                every,
                selectedLocal,
                TimeZoneInfo.Local,
                out repeatRule,
                out dueAt) ||
            !ScheduledRepeatSchedule.TryGetNominalInterval(
                repeatRule,
                out var nominalInterval))
        {
            SetValidation("这个循环时间无法使用，请换一个");
            RepeatCountTextBox.Focus();
            return false;
        }

        repeatInterval = nominalInterval;
        return true;
    }

    private void SetValidation(string message)
    {
        ValidationText.Text = message;
    }

    private void ScheduledTaskEditWindow_Deactivated(
        object? sender,
        EventArgs e)
    {
        QueueCommitAfterDeactivation();
    }

    private void ScheduledTaskEditWindow_Activated(
        object? sender,
        EventArgs e)
    {
        ValidationText.Text = string.Empty;
    }

    private void QueueCommitAfterDeactivation()
    {
        if (_allowClose)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            _commitAfterDeactivationAction);
    }

    private void CommitAfterDeactivation()
    {
        if (_allowClose ||
            _internalPopupOpen ||
            IsActive ||
            IsKeyboardFocusWithin)
        {
            return;
        }

        CommitAndClose();
    }

    private void ScheduledTaskEditWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        _allowClose = true;
    }

    private void ScheduledTaskEditWindow_Closed(
        object? sender,
        EventArgs e)
    {
        DetachPositionOwner();
        Activated -= ScheduledTaskEditWindow_Activated;
        Deactivated -= ScheduledTaskEditWindow_Deactivated;
        Closing -= ScheduledTaskEditWindow_Closing;
        Closed -= ScheduledTaskEditWindow_Closed;
        Loaded -= ScheduledTaskEditWindow_Loaded;
        DpiChanged -= ScheduledTaskEditWindow_DpiChanged;
    }
}
