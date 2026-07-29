using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class ScheduledTaskEditWindow : Window
{
    private const double TargetEditorWidth = 378;
    private const double TargetEditorHeight = 414;
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
    private readonly Action _closePickersAfterDeactivationAction;
    private readonly Action _positionBesideOwnerAction;
    private readonly OwnedWindowPositioner.PositionCache _positionCache;
    private Window? _positionOwner;
    private bool _initializing;
    private bool _isImeComposing;
    private bool _scheduleEdited;
    private bool _repeatEdited;
    private bool _internalPopupOpen;
    private bool _repeatUnitSelectionCommitted;
    private bool _updatingTimePickerSelection;
    private DateTime _displayedScheduledCalendarMonth;
    private bool _positionBesideOwnerQueued;

    public ScheduledTaskEditWindow(ScheduledTaskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        InitializeComponent();
        _item = item;
        _originalDueAt = item.DueAt;
        _originalRepeatInterval = item.RepeatInterval;
        _originalRepeatRule = item.RepeatRule;
        _closePickersAfterDeactivationAction =
            ClosePickersAfterDeactivation;
        _positionBesideOwnerAction = PositionBesideOwner;
        _positionCache = new OwnedWindowPositioner.PositionCache(this);

        _initializing = true;
        try
        {
            HourComboBox.ItemsSource = HourOptions;
            MinuteComboBox.ItemsSource = MinuteSecondOptions;
            SecondComboBox.ItemsSource = MinuteSecondOptions;
            ScheduledHourComboBox.ItemsSource = HourOptions;
            ScheduledMinuteComboBox.ItemsSource = MinuteSecondOptions;
            ScheduledSecondComboBox.ItemsSource = MinuteSecondOptions;
            RepeatUnitComboBox.ItemsSource = RepeatUnitOptions;

            var localDueAt = item.DueAt.ToLocalTime();
            TaskTextBox.Text = item.Text;
            DueDatePicker.SelectedDate = localDueAt.Date;
            HourComboBox.SelectedIndex = localDueAt.Hour;
            MinuteComboBox.SelectedIndex = localDueAt.Minute;
            SecondComboBox.SelectedIndex = localDueAt.Second;
            SetScheduledTimePickerSelection(
                localDueAt.Hour,
                localDueAt.Minute,
                localDueAt.Second);
            UpdateScheduledTimeTextFromPicker();
            SetRepeatDraft(item.RepeatInterval, item.RepeatRule);
        }
        finally
        {
            _initializing = false;
        }

        TextCompositionManager.AddPreviewTextInputStartHandler(
            TaskTextBox,
            TaskTextBox_PreviewTextInputStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(
            TaskTextBox,
            TaskTextBox_PreviewTextInputUpdate);
        TaskTextBox.PreviewTextInput +=
            TaskTextBox_PreviewTextInputCommitted;
        Activated += ScheduledTaskEditWindow_Activated;
        Deactivated += ScheduledTaskEditWindow_Deactivated;
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
        CloseScheduledPickers();
        if (IsLoaded)
        {
            Close();
        }
    }

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
        if (e.Key == Key.Escape && !_isImeComposing)
        {
            if (ScheduledTimePickerPopup.IsOpen ||
                IsAnyTimePickerDropDownOpen())
            {
                CloseScheduledTimePicker();
                FocusScheduledTimeInput();
                e.Handled = true;
                return;
            }

            if (ScheduledDatePickerPopup.IsOpen)
            {
                CloseScheduledDatePicker();
                FocusScheduledDateInput();
                e.Handled = true;
                return;
            }

            CloseWithoutSaving();
            e.Handled = true;
        }
    }

    private void TaskTextBox_PreviewTextInputStart(
        object sender,
        TextCompositionEventArgs e)
    {
        _isImeComposing = true;
    }

    private void TaskTextBox_PreviewTextInputUpdate(
        object sender,
        TextCompositionEventArgs e)
    {
        var composition = e.TextComposition;
        _isImeComposing =
            !string.IsNullOrEmpty(composition.CompositionText) ||
            !string.IsNullOrEmpty(composition.SystemCompositionText);
    }

    private void TaskTextBox_PreviewTextInputCommitted(
        object sender,
        TextCompositionEventArgs e)
    {
        _isImeComposing = false;
    }

    private void Window_PreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!ScheduledDatePickerPopup.IsOpen &&
            !ScheduledTimePickerPopup.IsOpen)
        {
            return;
        }

        var originalSource = e.OriginalSource as DependencyObject;
        var isRepeatEditorInteraction =
            IsWithin(originalSource, RepeatToggle) ||
            IsWithin(originalSource, RepeatCountTextBox) ||
            IsWithin(originalSource, RepeatUnitComboBox) ||
            IsWithinComboBoxPopup(originalSource, RepeatUnitComboBox);
        var isDatePickerInteraction =
            IsWithin(originalSource, ScheduledDatePickerHost) ||
            IsWithinPopup(originalSource, ScheduledDatePickerPopup);
        var isTimePickerInteraction =
            IsWithin(originalSource, ScheduledTimePickerHost) ||
            IsWithinPopup(originalSource, ScheduledTimePickerPopup) ||
            IsWithinComboBoxPopup(originalSource, ScheduledHourComboBox) ||
            IsWithinComboBoxPopup(originalSource, ScheduledMinuteComboBox) ||
            IsWithinComboBoxPopup(originalSource, ScheduledSecondComboBox);

        if (ScheduledDatePickerPopup.IsOpen &&
            !isDatePickerInteraction)
        {
            CloseScheduledDatePicker();
        }

        // The repeat controls are part of the reminder-time editor. Just like
        // the create form, changing the repeat rule must not dismiss the outer
        // time picker while the user is still choosing hour/minute/second.
        if (ScheduledTimePickerPopup.IsOpen &&
            !isTimePickerInteraction &&
            !isRepeatEditorInteraction)
        {
            CloseScheduledTimePicker();
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
            if (ReferenceEquals(sender, RepeatUnitComboBox) &&
                RepeatUnitComboBox.IsDropDownOpen)
            {
                _repeatUnitSelectionCommitted = true;
            }
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

    private void DueDatePicker_SelectedDateChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (DueDatePicker.SelectedDate is not { } selectedDate)
        {
            ScheduledDateInput.Text = string.Empty;
            return;
        }

        ScheduledDateInput.Text = selectedDate.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        ValidationText.Text = string.Empty;
        if (!_initializing)
        {
            _scheduleEdited = true;
        }

        if (ScheduledDatePickerPopup.IsOpen)
        {
            RefreshScheduledCalendar(selectedDate);
        }
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
            return;
        }

        if (e.Key == Key.Escape && ScheduledDatePickerPopup.IsOpen)
        {
            CloseScheduledDatePicker();
            e.Handled = true;
        }
    }

    private void OpenScheduledDatePicker()
    {
        var selectedDate = DueDatePicker.SelectedDate ?? DateTime.Today;
        RefreshScheduledCalendar(selectedDate);
        CloseScheduledTimePicker();
        ScheduledDatePickerPopup.IsOpen = true;
        UpdateInternalPopupState();
        ScheduledDatePickerTodayButton.Focus();
        Keyboard.Focus(ScheduledDatePickerTodayButton);
    }

    private void CloseScheduledDatePicker()
    {
        if (ScheduledDatePickerPopup.IsOpen)
        {
            ScheduledDatePickerPopup.IsOpen = false;
        }

        UpdateInternalPopupState();
    }

    private void ScheduledDatePickerPopup_Opened(
        object sender,
        EventArgs e)
    {
        UpdateInternalPopupState();
    }

    private void ScheduledDatePickerPopup_Closed(
        object sender,
        EventArgs e)
    {
        UpdateInternalPopupState();
    }

    private void ScheduledDatePickerPopup_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseScheduledDatePicker();
                FocusScheduledDateInput();
                e.Handled = true;
                break;
            case Key.PageUp:
                ChangeScheduledCalendarMonth(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                ChangeScheduledCalendarMonth(1);
                e.Handled = true;
                break;
            case Key.Home:
                SelectScheduledDate(DateTime.Today);
                e.Handled = true;
                break;
        }
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
        var selectedDate = DueDatePicker.SelectedDate ?? DateTime.Today;
        var displayedMonth = _displayedScheduledCalendarMonth == default
            ? new DateTime(selectedDate.Year, selectedDate.Month, 1)
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
        var selectedDate = DueDatePicker.SelectedDate?.Date;
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
        SelectScheduledDate(DateTime.Today);
        e.Handled = true;
    }

    private void SelectScheduledDate(DateTime date)
    {
        DueDatePicker.SelectedDate = date.Date;
        ScheduledDateInput.Text = date.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        if (!_initializing)
        {
            _scheduleEdited = true;
        }

        CloseScheduledDatePicker();
        FocusScheduledDateInput();
    }

    private void FocusScheduledDateInput()
    {
        ScheduledDateInput.Focus();
        Keyboard.Focus(ScheduledDateInput);
    }

    private void ScheduledTimeInput_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        OpenScheduledTimePicker();
        e.Handled = true;
    }

    private void ScheduledTimeInput_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter or Key.Down or Key.F4)
        {
            OpenScheduledTimePicker();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ScheduledTimePickerPopup.IsOpen)
        {
            CloseScheduledTimePicker();
            e.Handled = true;
        }
    }

    private void OpenScheduledTimePicker()
    {
        CloseScheduledDatePicker();
        SynchronizeScheduledTimePickerSelection();
        ScheduledTimePickerPopup.IsOpen = true;
        UpdateInternalPopupState();
        ScheduledHourComboBox.Focus();
        Keyboard.Focus(ScheduledHourComboBox);
    }

    private void CloseScheduledTimePicker()
    {
        ScheduledHourComboBox.IsDropDownOpen = false;
        ScheduledMinuteComboBox.IsDropDownOpen = false;
        ScheduledSecondComboBox.IsDropDownOpen = false;
        HourComboBox.IsDropDownOpen = false;
        MinuteComboBox.IsDropDownOpen = false;
        SecondComboBox.IsDropDownOpen = false;
        RepeatUnitComboBox.IsDropDownOpen = false;
        if (ScheduledTimePickerPopup.IsOpen)
        {
            ScheduledTimePickerPopup.IsOpen = false;
        }

        UpdateInternalPopupState();
    }

    private void CloseScheduledPickers()
    {
        CloseScheduledDatePicker();
        CloseScheduledTimePicker();
    }

    private void ScheduledTimePickerPopup_Opened(
        object sender,
        EventArgs e)
    {
        UpdateInternalPopupState();
    }

    private void ScheduledTimePickerPopup_Closed(
        object sender,
        EventArgs e)
    {
        UpdateInternalPopupState();
    }

    private void ScheduledPickerPopup_PreviewMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _internalPopupOpen = true;
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
        FocusScheduledTimeInput();
        e.Handled = true;
    }

    private void ScheduledTimePartComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingTimePickerSelection ||
            ScheduledHourComboBox.SelectedIndex < 0 ||
            ScheduledMinuteComboBox.SelectedIndex < 0 ||
            ScheduledSecondComboBox.SelectedIndex < 0)
        {
            return;
        }

        SetScheduledTimePickerSelection(
            ScheduledHourComboBox.SelectedIndex,
            ScheduledMinuteComboBox.SelectedIndex,
            ScheduledSecondComboBox.SelectedIndex);
        ValidationText.Text = string.Empty;
        if (!_initializing)
        {
            _scheduleEdited = true;
        }
    }

    private void ScheduledTimePartComboBox_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape ||
            !ScheduledTimePickerPopup.IsOpen)
        {
            return;
        }

        CloseScheduledTimePicker();
        FocusScheduledTimeInput();
        e.Handled = true;
    }

    private void LegacyTimeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_updatingTimePickerSelection ||
            HourComboBox.SelectedIndex < 0 ||
            MinuteComboBox.SelectedIndex < 0 ||
            SecondComboBox.SelectedIndex < 0)
        {
            return;
        }

        SetScheduledTimePickerSelection(
            HourComboBox.SelectedIndex,
            MinuteComboBox.SelectedIndex,
            SecondComboBox.SelectedIndex);
        ValidationText.Text = string.Empty;
        if (!_initializing)
        {
            _scheduleEdited = true;
        }
    }

    private void ScheduledTimePartItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ComboBoxItem item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not
                ComboBox comboBox)
        {
            return;
        }

        comboBox.SelectedItem =
            comboBox.ItemContainerGenerator.ItemFromContainer(item);
        comboBox.IsDropDownOpen = false;
        UpdateScheduledTimeTextFromPicker();
        _scheduleEdited = true;
        Activate();
        comboBox.Focus();
        Keyboard.Focus(comboBox);
        UpdateInternalPopupState();
        e.Handled = true;
    }

    private void ScheduledTimePickerNowButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now.LocalDateTime;
        DueDatePicker.SelectedDate = now.Date;
        SetScheduledTimePickerSelection(
            now.Hour,
            now.Minute,
            now.Second);
        _scheduleEdited = true;
        e.Handled = true;
    }

    private void ScheduledTimePickerConfirmButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        UpdateScheduledTimeTextFromPicker();
        CloseScheduledTimePicker();
        FocusScheduledTimeInput();
        e.Handled = true;
    }

    private void SetScheduledTimePickerSelection(
        int hour,
        int minute,
        int second)
    {
        _updatingTimePickerSelection = true;
        try
        {
            HourComboBox.SelectedIndex = Math.Clamp(hour, 0, 23);
            MinuteComboBox.SelectedIndex = Math.Clamp(minute, 0, 59);
            SecondComboBox.SelectedIndex = Math.Clamp(second, 0, 59);
            ScheduledHourComboBox.SelectedIndex =
                Math.Clamp(hour, 0, 23);
            ScheduledMinuteComboBox.SelectedIndex =
                Math.Clamp(minute, 0, 59);
            ScheduledSecondComboBox.SelectedIndex =
                Math.Clamp(second, 0, 59);
            UpdateScheduledTimeTextFromPicker();
        }
        finally
        {
            _updatingTimePickerSelection = false;
        }
    }

    private void SynchronizeScheduledTimePickerSelection()
    {
        SetScheduledTimePickerSelection(
            HourComboBox.SelectedIndex,
            MinuteComboBox.SelectedIndex,
            SecondComboBox.SelectedIndex);
    }

    private void UpdateScheduledTimeTextFromPicker()
    {
        if (HourComboBox.SelectedIndex < 0 ||
            MinuteComboBox.SelectedIndex < 0 ||
            SecondComboBox.SelectedIndex < 0)
        {
            ScheduledTimeInput.Text = string.Empty;
            return;
        }

        ScheduledTimeInput.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}",
            HourComboBox.SelectedIndex,
            MinuteComboBox.SelectedIndex,
            SecondComboBox.SelectedIndex);
    }

    private void FocusScheduledTimeInput()
    {
        ScheduledTimeInput.Focus();
        Keyboard.Focus(ScheduledTimeInput);
    }

    private void InternalComboBox_DropDownOpened(
        object sender,
        EventArgs e)
    {
        if (ReferenceEquals(sender, RepeatUnitComboBox))
        {
            _repeatUnitSelectionCommitted = false;
        }

        if ((ReferenceEquals(sender, ScheduledHourComboBox) ||
             ReferenceEquals(sender, ScheduledMinuteComboBox) ||
             ReferenceEquals(sender, ScheduledSecondComboBox)) &&
            !ScheduledTimePickerPopup.IsOpen)
        {
            ScheduledTimePickerPopup.IsOpen = true;
        }

        _internalPopupOpen = true;
    }

    private void InternalComboBox_DropDownClosed(
        object sender,
        EventArgs e)
    {
        if (ReferenceEquals(sender, RepeatUnitComboBox))
        {
            if (_repeatUnitSelectionCommitted)
            {
                Activate();
                RepeatUnitComboBox.Focus();
                Keyboard.Focus(RepeatUnitComboBox);
            }

            _repeatUnitSelectionCommitted = false;
        }

        UpdateInternalPopupState();
    }

    private bool IsAnyComboDropDownOpen() =>
        HourComboBox.IsDropDownOpen ||
        MinuteComboBox.IsDropDownOpen ||
        SecondComboBox.IsDropDownOpen ||
        ScheduledHourComboBox.IsDropDownOpen ||
        ScheduledMinuteComboBox.IsDropDownOpen ||
        ScheduledSecondComboBox.IsDropDownOpen ||
        RepeatUnitComboBox.IsDropDownOpen;

    private bool IsAnyTimePickerDropDownOpen() =>
        HourComboBox.IsDropDownOpen ||
        MinuteComboBox.IsDropDownOpen ||
        SecondComboBox.IsDropDownOpen ||
        ScheduledHourComboBox.IsDropDownOpen ||
        ScheduledMinuteComboBox.IsDropDownOpen ||
        ScheduledSecondComboBox.IsDropDownOpen ||
        RepeatUnitComboBox.IsDropDownOpen;

    private void UpdateInternalPopupState()
    {
        _internalPopupOpen =
            ScheduledDatePickerPopup.IsOpen ||
            ScheduledTimePickerPopup.IsOpen ||
            IsAnyComboDropDownOpen();
    }

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

        CloseScheduledPickers();
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

        var preserveOriginalSchedule = !_scheduleEdited && !_repeatEdited;
        DateTime selectedLocal;
        if (preserveOriginalSchedule)
        {
            dueAt = _originalDueAt;
            repeatInterval = _originalRepeatInterval;
            repeatRule = _originalRepeatRule;
            selectedLocal = DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTime(
                        _originalDueAt,
                        TimeZoneInfo.Local)
                    .DateTime,
                DateTimeKind.Unspecified);
        }
        else
        {
            if (DueDatePicker.SelectedDate is not { } selectedDate ||
                HourComboBox.SelectedIndex is < 0 or > 23 ||
                MinuteComboBox.SelectedIndex is < 0 or > 59 ||
                SecondComboBox.SelectedIndex is < 0 or > 59)
            {
                SetValidation("请选择完整的提醒日期和时分秒");
                FocusScheduledDateInput();
                return false;
            }

            selectedLocal = DateTime.SpecifyKind(
                selectedDate.Date
                    .AddHours(HourComboBox.SelectedIndex)
                    .AddMinutes(MinuteComboBox.SelectedIndex)
                    .AddSeconds(SecondComboBox.SelectedIndex),
                DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(selectedLocal))
            {
                SetValidation("这个本地时间不存在，请换一个时间");
                FocusScheduledDateInput();
                return false;
            }

            dueAt = new DateTimeOffset(
                selectedLocal,
                TimeZoneInfo.Local.GetUtcOffset(selectedLocal));
        }

        if (!preserveOriginalSchedule &&
            RepeatToggle.IsChecked == true)
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

        var now = DateTimeOffset.Now;
        if (dueAt <= now &&
            RepeatToggle.IsChecked == true)
        {
            if (repeatRule is null &&
                !TryReadRepeatRule(
                    selectedLocal,
                    out repeatRule,
                    out repeatInterval,
                    out dueAt))
            {
                return false;
            }

            if (!ScheduledRepeatSchedule.TryAdvanceToFuture(
                    repeatRule,
                    dueAt,
                    now,
                    out var futureRule,
                    out var futureDueAt))
            {
                SetValidation("这个循环时间无法推进，请换一个时间");
                FocusScheduledDateInput();
                return false;
            }

            repeatRule = futureRule;
            dueAt = futureDueAt;
        }
        else if (dueAt <= now)
        {
            SetValidation("提醒时间要晚于现在哦");
            FocusScheduledDateInput();
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

    private static bool IsWithinPopup(
        DependencyObject? source,
        System.Windows.Controls.Primitives.Popup popup) =>
        popup.Child is DependencyObject popupChild &&
        IsWithin(source, popupChild);

    private static bool IsWithinComboBoxPopup(
        DependencyObject? source,
        ComboBox comboBox) =>
        comboBox.Template.FindName("PART_Popup", comboBox) is
            System.Windows.Controls.Primitives.Popup
            {
                Child: DependencyObject popupChild
            } &&
        IsWithin(source, popupChild);

    private static bool IsWithin(
        DependencyObject? source,
        DependencyObject ancestor)
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

    private void ScheduledTaskEditWindow_Deactivated(
        object? sender,
        EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            _closePickersAfterDeactivationAction);
    }

    private void ClosePickersAfterDeactivation()
    {
        if (!IsLoaded ||
            IsActive ||
            IsKeyboardFocusWithin ||
            !_internalPopupOpen ||
            IsAnyComboDropDownOpen() ||
            IsPointerInsideScheduledPicker())
        {
            return;
        }

        // A real switch to another window must not leave a Topmost picker
        // floating over unrelated applications. This only closes picker UI;
        // the unconfirmed edit window and draft remain intact.
        CloseScheduledPickers();
    }

    private bool IsPointerInsideScheduledPicker() =>
        ScheduledDatePickerHost.IsMouseOver ||
        ScheduledTimePickerHost.IsMouseOver ||
        ScheduledDatePickerPopup.Child?.IsMouseOver == true ||
        ScheduledTimePickerPopup.Child?.IsMouseOver == true ||
        RepeatToggle.IsMouseOver ||
        RepeatCountTextBox.IsMouseOver ||
        RepeatUnitComboBox.IsMouseOver;

    private void ScheduledTaskEditWindow_Activated(
        object? sender,
        EventArgs e)
    {
        _isImeComposing = false;
        ValidationText.Text = string.Empty;
    }

    private void ScheduledTaskEditWindow_Closed(
        object? sender,
        EventArgs e)
    {
        DetachPositionOwner();
        TextCompositionManager.RemovePreviewTextInputStartHandler(
            TaskTextBox,
            TaskTextBox_PreviewTextInputStart);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(
            TaskTextBox,
            TaskTextBox_PreviewTextInputUpdate);
        TaskTextBox.PreviewTextInput -=
            TaskTextBox_PreviewTextInputCommitted;
        Activated -= ScheduledTaskEditWindow_Activated;
        Deactivated -= ScheduledTaskEditWindow_Deactivated;
        Closed -= ScheduledTaskEditWindow_Closed;
        Loaded -= ScheduledTaskEditWindow_Loaded;
        DpiChanged -= ScheduledTaskEditWindow_DpiChanged;
    }

    private sealed class ScheduledCalendarDateCell
    {
        public DateTime Date { get; init; }

        public string DayText { get; init; } = string.Empty;

        public string AccessibleName { get; init; } = string.Empty;

        public bool IsCurrentMonth { get; init; }

        public bool IsSelected { get; init; }

        public bool IsToday { get; init; }

        public bool IsWeekend { get; init; }
    }
}
