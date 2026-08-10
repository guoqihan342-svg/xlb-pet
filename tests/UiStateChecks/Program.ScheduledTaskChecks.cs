using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LubanDesktopPet;

internal static partial class Program
{
    private static void AssertScheduledTaskTabContract()
    {
        var type = typeof(TodoWindow);
        foreach (var propertyName in new[]
                 {
                     "ScheduledTasks",
                     "IsTransientPopupOpen"
                 })
        {
            Assert(type.GetProperty(
                       propertyName,
                       BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {propertyName} 属性");
        }

        Assert(type.GetMethod(
                   "ShowDefaultTab",
                   BindingFlags.Instance | BindingFlags.Public) is not null,
            "TodoWindow 应公开 ShowDefaultTab 以便每次右键打开时回到待办页");
        foreach (var eventName in new[]
                 {
                     "ScheduledTaskAddRequested",
                     "ScheduledTaskEditRequested",
                     "ScheduledTaskDeleteRequested",
                     "TransientInteractionCompleted"
                 })
        {
            Assert(type.GetEvent(
                       eventName,
                       BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {eventName} 事件");
        }

        var scheduledTasks = new ObservableCollection<ScheduledTaskItem>();
        var todoWindow = new TodoWindow
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ScheduledTasks = scheduledTasks
        };
        try
        {
            var todoTab = GetField<RadioButton>(todoWindow, "TodoTabButton");
            var scheduledTab = GetField<RadioButton>(todoWindow, "ScheduledTaskTabButton");
            var todoPage = GetField<Grid>(todoWindow, "TodoPage");
            var todoInput = GetField<TextBox>(todoWindow, "TodoInput");
            var scheduledPage = GetField<Grid>(todoWindow, "ScheduledTaskPage");
            var scheduledList = GetField<ListBox>(todoWindow, "ScheduledTaskItemsControl");
            var scheduledInput = GetField<TextBox>(todoWindow, "ScheduledTaskInput");
            var scheduledRepeatEditor = GetField<Grid>(
                todoWindow,
                "ScheduledRepeatEditor");
            var scheduledQuietHoursEditor = GetField<Grid>(
                todoWindow,
                "ScheduledQuietHoursEditor");
            var scheduledDatePickerHost = GetField<Border>(
                todoWindow,
                "ScheduledDatePickerHost");
            var scheduledDateInput = GetField<TextBox>(
                todoWindow,
                "ScheduledDateInput");
            var scheduledDatePickerPopup = GetField<Popup>(
                todoWindow,
                "ScheduledDatePickerPopup");
            var scheduledDateItems = GetField<ItemsControl>(
                todoWindow,
                "ScheduledDateItemsControl");
            var scheduledDateMonthText = GetField<TextBlock>(
                todoWindow,
                "ScheduledDateMonthText");
            var scheduledDatePreviousMonthButton = GetField<Button>(
                todoWindow,
                "ScheduledDatePreviousMonthButton");
            var scheduledDateNextMonthButton = GetField<Button>(
                todoWindow,
                "ScheduledDateNextMonthButton");
            var scheduledDateTodayButton = GetField<Button>(
                todoWindow,
                "ScheduledDatePickerTodayButton");
            var scheduledTime = GetField<TextBox>(todoWindow, "ScheduledTimeInput");
            var scheduledTimePickerHost = GetField<Border>(
                todoWindow,
                "ScheduledTimePickerHost");
            var scheduledTimePickerPopup = GetField<Popup>(
                todoWindow,
                "ScheduledTimePickerPopup");
            var scheduledTimePickerTitle = GetField<TextBlock>(
                todoWindow,
                "ScheduledTimePickerTitle");
            var scheduledHourPicker = GetField<ComboBox>(
                todoWindow,
                "ScheduledHourComboBox");
            var scheduledMinutePicker = GetField<ComboBox>(
                todoWindow,
                "ScheduledMinuteComboBox");
            var scheduledSecondPicker = GetField<ComboBox>(
                todoWindow,
                "ScheduledSecondComboBox");
            var scheduledRepeatToggle = GetField<CheckBox>(
                todoWindow,
                "ScheduledRepeatToggle");
            var scheduledQuietHoursToggle = GetField<CheckBox>(
                todoWindow,
                "ScheduledQuietHoursToggle");
            var scheduledQuietHoursStart = GetField<TextBox>(
                todoWindow,
                "ScheduledQuietHoursStartInput");
            var scheduledQuietHoursEnd = GetField<TextBox>(
                todoWindow,
                "ScheduledQuietHoursEndInput");
            var scheduledQuietHoursOvernightHint = GetField<TextBlock>(
                todoWindow,
                "ScheduledQuietHoursOvernightHint");
            var scheduledRepeatCount = GetField<TextBox>(
                todoWindow,
                "ScheduledRepeatCountInput");
            var scheduledRepeatUnit = GetField<ComboBox>(
                todoWindow,
                "ScheduledRepeatUnitComboBox");
            var scheduledRepeatRulePreview = GetField<TextBlock>(
                todoWindow,
                "ScheduledRepeatRulePreviewText");
            var scheduledRepeatHint = GetField<TextBlock>(
                todoWindow,
                "ScheduledRepeatHintText");
            var scheduledSubmit = GetField<Button>(
                todoWindow,
                "ScheduledTaskSubmitButton");
            var scheduledEditCancel = GetField<Button>(
                todoWindow,
                "ScheduledTaskEditCancelButton");
            var validationText = GetField<TextBlock>(
                todoWindow,
                "ScheduledTaskValidationText");
            var roamingToggle = GetField<CheckBox>(
                todoWindow,
                "EdgeRoamingToggle");
            var startupToggle = GetField<CheckBox>(
                todoWindow,
                "StartupToggle");
            var petSizeSlider = GetField<Slider>(
                todoWindow,
                "PetSizeSlider");

            Rect BoundsInTodo(FrameworkElement element)
            {
                var origin = element.TranslatePoint(
                    new Point(0, 0),
                    todoWindow);
                return new Rect(
                    origin,
                    new Size(element.ActualWidth, element.ActualHeight));
            }

            void AssertScheduledFormLayout(string stage)
            {
                todoWindow.UpdateLayout();
                var rowHeights = scheduledPage.RowDefinitions
                    .Select(row => row.Height)
                    .ToArray();
                var statusRowHost =
                    VisualTreeHelper.GetParent(validationText) as Grid;
                var inputRowHost =
                    VisualTreeHelper.GetParent(scheduledInput) as Grid;
                var dateRowHost =
                    VisualTreeHelper.GetParent(
                        scheduledDatePickerHost) as Grid;
                Assert(rowHeights.Length == 6 &&
                       rowHeights[0].IsStar &&
                       rowHeights[1].GridUnitType ==
                           GridUnitType.Pixel &&
                       Math.Abs(rowHeights[1].Value - 18) <= 0.01 &&
                       Math.Abs(rowHeights[2].Value - 36) <= 0.01 &&
                       Math.Abs(rowHeights[3].Value - 28) <= 0.01 &&
                       rowHeights[4].GridUnitType ==
                           GridUnitType.Auto &&
                       Math.Abs(rowHeights[5].Value - 34) <= 0.01 &&
                       statusRowHost is not null &&
                       Grid.GetRow(statusRowHost) == 1 &&
                       inputRowHost is not null &&
                       Grid.GetRow(inputRowHost) == 2 &&
                       Grid.GetRow(scheduledRepeatEditor) == 3 &&
                       Grid.GetRow(scheduledQuietHoursEditor) == 4 &&
                       dateRowHost is not null &&
                       Grid.GetRow(dateRowHost) == 5,
                    $"{stage}定时页行顺序必须固定为 */18校验/36输入/28循环/Auto免打扰/34日期");

                var todoInputBounds = BoundsInTodo(todoInput);
                var scheduledInputBounds = BoundsInTodo(scheduledInput);
                var repeatBounds = BoundsInTodo(scheduledRepeatEditor);
                var dateBounds = BoundsInTodo(scheduledDatePickerHost);
                var timeBounds = BoundsInTodo(scheduledTimePickerHost);
                var submitBounds = BoundsInTodo(scheduledSubmit);
                var footerBottoms = new[]
                {
                    dateBounds.Bottom,
                    timeBounds.Bottom,
                    submitBounds.Bottom,
                    todoInputBounds.Bottom
                };
                Assert(footerBottoms.Max() - footerBottoms.Min() <= 0.5,
                    $"{stage}定时日期/时间/新增底边必须与TodoInput齐平：" +
                    string.Join(
                        ", ",
                        footerBottoms.Select(value =>
                            value.ToString(
                                "F2",
                                CultureInfo.InvariantCulture))));
                Assert(scheduledInputBounds.Bottom <=
                           repeatBounds.Top + 0.5 &&
                       repeatBounds.Bottom <= dateBounds.Top + 0.5,
                    $"{stage}定时输入、循环、免打扰和日期各行不得垂直重叠");
                if (scheduledQuietHoursEditor.Visibility ==
                    Visibility.Visible)
                {
                    var quietBounds =
                        BoundsInTodo(scheduledQuietHoursEditor);
                    Assert(repeatBounds.Bottom <= quietBounds.Top + 0.5 &&
                           quietBounds.Bottom <= dateBounds.Top + 0.5,
                        $"{stage}可见免打扰时段不得覆盖循环或日期行");
                }
                else
                {
                    Assert(scheduledQuietHoursEditor.ActualHeight <= 0.5,
                        $"{stage}非循环状态必须折叠免打扰行且不占布局高度");
                }

                var statusBounds = scheduledRepeatRulePreview.Visibility ==
                        Visibility.Visible
                    ? BoundsInTodo(scheduledRepeatRulePreview)
                    : BoundsInTodo(validationText);
                Assert(statusBounds.Bottom <=
                           scheduledInputBounds.Top + 0.5,
                    $"{stage}校验/循环预览行不得覆盖定时任务输入框");
                if (scheduledEditCancel.Visibility == Visibility.Visible)
                {
                    var cancelBounds = BoundsInTodo(scheduledEditCancel);
                    Assert(cancelBounds.Bottom <=
                               scheduledInputBounds.Top + 0.5 &&
                           statusBounds.Right <= cancelBounds.Left + 0.5,
                        $"{stage}编辑取消按钮不得覆盖校验/循环预览或输入框");
                }
            }

            void AssertTaskPageSettingsTheme(
                bool scheduled,
                string stage)
            {
                todoWindow.UpdateLayout();
                roamingToggle.ApplyTemplate();
                startupToggle.ApplyTemplate();
                petSizeSlider.ApplyTemplate();
                var roamingTrack = roamingToggle.Template.FindName(
                    "SwitchTrack",
                    roamingToggle) as Border;
                var startupTrack = startupToggle.Template.FindName(
                    "SwitchTrack",
                    startupToggle) as Border;
                var sliderTrack = petSizeSlider.Template.FindName(
                    "PART_Track",
                    petSizeSlider) as Track;
                sliderTrack?.DecreaseRepeatButton.ApplyTemplate();
                sliderTrack?.IncreaseRepeatButton.ApplyTemplate();
                sliderTrack?.Thumb.ApplyTemplate();
                var decreaseBorder = sliderTrack is null
                    ? null
                    : FindVisualDescendant<Border>(
                        sliderTrack.DecreaseRepeatButton);
                var increaseBorder = sliderTrack is null
                    ? null
                    : FindVisualDescendant<Border>(
                        sliderTrack.IncreaseRepeatButton);
                var thumbFace = sliderTrack?.Thumb.Template.FindName(
                    "ThumbFace",
                    sliderTrack.Thumb) as System.Windows.Shapes.Ellipse;
                var expectedSettingsText = scheduled
                    ? Color.FromRgb(0x67, 0x53, 0x44)
                    : Color.FromRgb(0x51, 0x59, 0x66);
                var expectedUncheckedTrack = scheduled
                    ? Color.FromRgb(0xFF, 0xF3, 0xE3)
                    : Color.FromRgb(0xEA, 0xF3, 0xFF);
                var expectedCheckedTrack = scheduled
                    ? Color.FromRgb(0xF7, 0xB4, 0x6E)
                    : Color.FromRgb(0x78, 0xA8, 0xEB);
                var expectedDecrease = scheduled
                    ? Color.FromRgb(0xF5, 0xAD, 0x68)
                    : Color.FromRgb(0x77, 0xA7, 0xEB);
                var expectedIncrease = scheduled
                    ? Color.FromRgb(0xFF, 0xF0, 0xDD)
                    : Color.FromRgb(0xE5, 0xEF, 0xFC);
                var expectedThumb = scheduled
                    ? Color.FromRgb(0xFF, 0xE7, 0xC5)
                    : Color.FromRgb(0xEA, 0xF3, 0xFF);
                var expectedThumbBorder = scheduled
                    ? Color.FromRgb(0xE2, 0x8E, 0x43)
                    : Color.FromRgb(0x5B, 0x8D, 0xEF);
                Assert(roamingToggle.Foreground is SolidColorBrush roamingText &&
                       roamingText.Color == expectedSettingsText &&
                       startupToggle.Foreground is SolidColorBrush startupText &&
                       startupText.Color == expectedSettingsText &&
                       roamingTrack?.Background is SolidColorBrush roamingBackground &&
                       roamingBackground.Color == expectedCheckedTrack &&
                       startupTrack?.Background is SolidColorBrush startupBackground &&
                       startupBackground.Color == expectedUncheckedTrack &&
                       decreaseBorder?.Background is SolidColorBrush decreaseBrush &&
                       decreaseBrush.Color == expectedDecrease &&
                       increaseBorder?.Background is SolidColorBrush increaseBrush &&
                       increaseBrush.Color == expectedIncrease &&
                       thumbFace?.Fill is SolidColorBrush thumbBrush &&
                       thumbBrush.Color == expectedThumb &&
                       thumbFace.Stroke is SolidColorBrush thumbBorder &&
                       thumbBorder.Color == expectedThumbBorder,
                    $"{stage}底部绕屏、开机自启和桌宠大小必须整套使用" +
                    $"{(scheduled ? "橘色" : "蓝色")}主题");
            }

            void AssertPrimaryInputTextViewport(
                TextBox input,
                double expectedHeight,
                string stage)
            {
                input.ApplyTemplate();
                todoWindow.UpdateLayout();
                var contentHost = input.Template.FindName(
                    "PART_ContentHost",
                    input) as ScrollViewer;
                Assert(input.FontFamily.Source == "Microsoft YaHei" &&
                       input.FontSize >= 13 &&
                       input.VerticalContentAlignment ==
                           VerticalAlignment.Center &&
                       input.Padding.Top <= 3.01 &&
                       input.Padding.Bottom <= 4.01 &&
                       input.Padding.Top + input.Padding.Bottom <= 7.01 &&
                       contentHost is not null &&
                       contentHost.UseLayoutRounding &&
                       contentHost.SnapsToDevicePixels &&
                       contentHost.VerticalAlignment ==
                           VerticalAlignment.Stretch &&
                       contentHost.VerticalContentAlignment ==
                           VerticalAlignment.Center &&
                       Math.Abs(input.ActualHeight - expectedHeight) <= 0.5 &&
                       contentHost.ActualHeight >= input.FontSize * 1.5,
                    $"{stage}微软雅黑输入文字必须拥有完整下沿视口，不能被圆角框或高DPI像素取整裁掉底部");

                var worstCaseLogicalViewport =
                    expectedHeight -
                    input.Padding.Top -
                    input.Padding.Bottom -
                    3d;
                foreach (var dpiScale in new[] { 1d, 1.25d, 1.5d })
                {
                    var availablePhysicalPixels =
                        Math.Floor(worstCaseLogicalViewport * dpiScale) - 1;
                    var requiredPhysicalPixels = Math.Ceiling(
                        (input.FontFamily.LineSpacing * input.FontSize + 2d) *
                        dpiScale);
                    Assert(availablePhysicalPixels >=
                               requiredPhysicalPixels,
                        $"{stage}在{dpiScale:P0} DPI下必须为微软雅黑下沿和物理像素取整保留余量：" +
                        $"available={availablePhysicalPixels:F0}px, " +
                        $"required={requiredPhysicalPixels:F0}px");
                }
            }

            Assert(todoTab.IsChecked == true &&
                   scheduledTab.IsChecked != true &&
                   todoPage.Visibility == Visibility.Visible &&
                   scheduledPage.Visibility == Visibility.Hidden,
                "TodoWindow 创建后必须默认显示左侧“待办事项”选项卡");
            Assert(ReferenceEquals(scheduledList.ItemsSource, scheduledTasks),
                "定时任务列表必须直接绑定传入的 ObservableCollection");
            Assert(scheduledInput.MaxLength == 5000 &&
                   scheduledTime.MaxLength == 8 &&
                   Equals(scheduledSubmit.Content, "新增") &&
                   scheduledEditCancel.Visibility == Visibility.Collapsed &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime &&
                   scheduledDateInput.IsReadOnly &&
                   !InputMethod.GetIsInputMethodEnabled(scheduledDateInput) &&
                   scheduledRepeatToggle.IsChecked != true &&
                   scheduledRepeatCount.Text == "1" &&
                   scheduledRepeatUnit.SelectedIndex ==
                       (int)ScheduledRepeatUnit.Hour &&
                   scheduledRepeatRulePreview.Visibility ==
                       Visibility.Collapsed &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   DateTime.TryParseExact(
                       scheduledTime.Text,
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out _),
                "定时页必须默认提供单次日期和 HH:mm:ss 秒级时间；循环编辑器默认关闭并预填1小时");
            Assert(string.IsNullOrEmpty(validationText.Text),
                "定时任务初始状态不应显示错误提示");

            var exactNow = new DateTimeOffset(
                2031,
                5,
                6,
                7,
                8,
                9,
                TimeZoneInfo.Local.GetUtcOffset(
                    new DateTime(2031, 5, 6, 7, 8, 9)));
            Invoke(todoWindow, "ResetScheduledTaskDraftClock", exactNow);
            var expectedLocalNow = exactNow.LocalDateTime;
            Assert(GetRawField(todoWindow, "_scheduledDate") is DateTime scheduledDate &&
                   scheduledDate == expectedLocalNow.Date &&
                   scheduledDateInput.Text == expectedLocalNow.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == expectedLocalNow.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture) &&
                   scheduledHourPicker.SelectedIndex == expectedLocalNow.Hour &&
                   scheduledMinutePicker.SelectedIndex == expectedLocalNow.Minute &&
                   scheduledSecondPicker.SelectedIndex == expectedLocalNow.Second &&
                   scheduledHourPicker.Items.Count == 24 &&
                   scheduledMinutePicker.Items.Count == 60 &&
                   scheduledSecondPicker.Items.Count == 60 &&
                   scheduledTime.IsReadOnly &&
                   ReferenceEquals(
                       scheduledDatePickerPopup.PlacementTarget,
                       scheduledDatePickerHost) &&
                   scheduledDatePickerPopup.AllowsTransparency &&
                   scheduledDatePickerPopup.StaysOpen &&
                    ReferenceEquals(
                        scheduledTimePickerPopup.PlacementTarget,
                        scheduledTimePickerHost) &&
                    scheduledTimePickerPopup.AllowsTransparency &&
                    scheduledTimePickerPopup.StaysOpen,
                "定时任务默认值必须精确使用当前本地秒，并通过自定义日期月历和 24/60/60 时间浮层编辑");

            Invoke(todoWindow, "SelectTaskPage", true, false);
            Assert(todoTab.IsChecked != true &&
                   scheduledTab.IsChecked == true &&
                   todoPage.Visibility == Visibility.Hidden &&
                   scheduledPage.Visibility == Visibility.Visible,
                "点击右侧“定时任务”后必须只显示定时页");
            var scheduledShortItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "短提醒",
                DueAt = DateTimeOffset.Now.AddHours(1),
                CreatedAt = DateTimeOffset.Now
            };
            var scheduledLongText = string.Concat(Enumerable.Repeat(
                "这是一条需要两行之外才能显示完整内容的定时任务。",
                12));
            var scheduledLongItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = scheduledLongText,
                DueAt = DateTimeOffset.Now.AddHours(2),
                CreatedAt = DateTimeOffset.Now
            };
            scheduledTasks.Add(scheduledShortItem);
            scheduledTasks.Add(scheduledLongItem);
            scheduledTasks.Add(new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "第三条用于验证默认视口",
                DueAt = DateTimeOffset.Now.AddHours(3),
                CreatedAt = DateTimeOffset.Now
            });
            scheduledTasks.Add(new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "第四条用于产生滚动范围",
                DueAt = DateTimeOffset.Now.AddHours(4),
                CreatedAt = DateTimeOffset.Now
            });
            todoWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            todoWindow.ShowDefaultTab();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            AssertPrimaryInputTextViewport(
                todoInput,
                38,
                "待办事项主输入框");
            roamingToggle.ApplyTemplate();
            startupToggle.ApplyTemplate();
            petSizeSlider.ApplyTemplate();
            var originalRoamingTemplate = roamingToggle.Template;
            var originalStartupTemplate = startupToggle.Template;
            var originalSliderTemplate = petSizeSlider.Template;
            var originalRoamingTrack = roamingToggle.Template.FindName(
                "SwitchTrack",
                roamingToggle);
            var originalStartupTrack = startupToggle.Template.FindName(
                "SwitchTrack",
                startupToggle);
            var originalSliderTrack = petSizeSlider.Template.FindName(
                "PART_Track",
                petSizeSlider);
            var originalRoamingValue = roamingToggle.IsChecked;
            var originalStartupValue = startupToggle.IsChecked;
            var originalPetSizeValue = petSizeSlider.Value;
            AssertTaskPageSettingsTheme(
                scheduled: false,
                "待办页");
            Invoke(todoWindow, "SelectTaskPage", true, false);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            AssertPrimaryInputTextViewport(
                scheduledInput,
                36,
                "定时任务主输入框");
            AssertTaskPageSettingsTheme(
                scheduled: true,
                "定时任务页");
            Assert(ReferenceEquals(roamingToggle.Template, originalRoamingTemplate) &&
                   ReferenceEquals(startupToggle.Template, originalStartupTemplate) &&
                   ReferenceEquals(petSizeSlider.Template, originalSliderTemplate) &&
                   ReferenceEquals(
                       roamingToggle.Template.FindName(
                           "SwitchTrack",
                           roamingToggle),
                       originalRoamingTrack) &&
                   ReferenceEquals(
                       startupToggle.Template.FindName(
                           "SwitchTrack",
                           startupToggle),
                       originalStartupTrack) &&
                   ReferenceEquals(
                       petSizeSlider.Template.FindName(
                           "PART_Track",
                           petSizeSlider),
                       originalSliderTrack) &&
                   roamingToggle.IsChecked == originalRoamingValue &&
                   startupToggle.IsChecked == originalStartupValue &&
                   Math.Abs(petSizeSlider.Value - originalPetSizeValue) <=
                       0.000001,
                "切到定时任务页只能替换动态主题画刷，不能重建开关/滑块模板或重置值");
            Invoke(todoWindow, "SelectTaskPage", false, false);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            AssertTaskPageSettingsTheme(
                scheduled: false,
                "切回待办页");
            Assert(ReferenceEquals(roamingToggle.Template, originalRoamingTemplate) &&
                   ReferenceEquals(startupToggle.Template, originalStartupTemplate) &&
                   ReferenceEquals(petSizeSlider.Template, originalSliderTemplate) &&
                   roamingToggle.IsChecked == originalRoamingValue &&
                   startupToggle.IsChecked == originalStartupValue &&
                   Math.Abs(petSizeSlider.Value - originalPetSizeValue) <=
                       0.000001,
                "切回待办页必须恢复蓝色且保持底部控件实例、模板和值");
            Invoke(todoWindow, "SelectTaskPage", true, false);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            todoWindow.UpdateLayout();
            AssertScheduledFormLayout("默认新增态");
            Assert(scheduledQuietHoursEditor.Visibility ==
                       Visibility.Collapsed &&
                   scheduledQuietHoursToggle.IsChecked != true &&
                   scheduledQuietHoursStart.Text == "22:00:00" &&
                   scheduledQuietHoursEnd.Text == "07:00:00" &&
                   scheduledQuietHoursStart.IsReadOnly &&
                   scheduledQuietHoursEnd.IsReadOnly &&
                   !InputMethod.GetIsInputMethodEnabled(
                       scheduledQuietHoursStart) &&
                   !InputMethod.GetIsInputMethodEnabled(
                       scheduledQuietHoursEnd) &&
                   scheduledQuietHoursStart.Cursor == Cursors.Hand &&
                   scheduledQuietHoursEnd.Cursor == Cursors.Hand,
                "免打扰时段必须仅供循环任务使用，新增态默认关闭并预填22:00:00到07:00:00；开始和结束都只能点击选择、不能手输");
            scheduledRepeatToggle.ApplyTemplate();
            var scheduledRepeatShell = scheduledRepeatToggle.Template.FindName(
                "CuteCheckShell",
                scheduledRepeatToggle) as Border;
            scheduledRepeatToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Assert(scheduledRepeatShell?.Background is SolidColorBrush
                       repeatCheckedBackground &&
                   repeatCheckedBackground.Color ==
                       Color.FromRgb(0xF2, 0xA0, 0x52) &&
                   scheduledRepeatShell.BorderBrush is SolidColorBrush
                       repeatCheckedBorder &&
                       repeatCheckedBorder.Color ==
                        Color.FromRgb(0xD9, 0x84, 0x35),
                "定时任务的循环勾选必须继续使用橘色萌系样式");
            scheduledQuietHoursToggle.ApplyTemplate();
            var scheduledQuietHoursShell =
                scheduledQuietHoursToggle.Template.FindName(
                    "CuteCheckShell",
                    scheduledQuietHoursToggle) as Border;
            Assert(scheduledQuietHoursEditor.Visibility ==
                       Visibility.Visible &&
                   scheduledQuietHoursEditor.IsEnabled &&
                   scheduledQuietHoursToggle.IsChecked != true &&
                   !scheduledQuietHoursStart.IsEnabled &&
                   !scheduledQuietHoursEnd.IsEnabled &&
                   scheduledQuietHoursToggle.Foreground is SolidColorBrush
                       quietHoursTextBrush &&
                   quietHoursTextBrush.Color ==
                       Color.FromRgb(0x8A, 0x56, 0x2E),
                "勾选循环后必须显示萌橘色免打扰行，开关默认关闭且时间框保持禁用");
            AssertSingleLineTextIsFullyVisible(
                scheduledQuietHoursOvernightHint,
                scheduledQuietHoursEditor,
                "新增定时任务的免打扰行尾部提示");
            scheduledQuietHoursToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Assert(scheduledQuietHoursStart.IsEnabled &&
                   scheduledQuietHoursEnd.IsEnabled &&
                   scheduledQuietHoursShell?.Background is SolidColorBrush
                       quietHoursCheckedBackground &&
                   quietHoursCheckedBackground.Color ==
                       Color.FromRgb(0xF2, 0xA0, 0x52) &&
                   scheduledQuietHoursShell.BorderBrush is SolidColorBrush
                        quietHoursCheckedBorder &&
                   quietHoursCheckedBorder.Color ==
                        Color.FromRgb(0xD9, 0x84, 0x35),
                "免打扰勾选、时间选择和焦点视觉必须保持定时任务的萌橘色风格");

            void SelectInlineQuietHoursTime(
                TextBox input,
                int hour,
                int minute,
                int second,
                string expectedTitle)
            {
                RaisePreviewMouseDown(input);
                Assert(scheduledTimePickerPopup.IsOpen &&
                       ReferenceEquals(
                           scheduledTimePickerPopup.PlacementTarget,
                           input) &&
                       scheduledTimePickerTitle.Text == expectedTitle &&
                       scheduledHourPicker.Items.Count == 24 &&
                       scheduledMinutePicker.Items.Count == 60 &&
                       scheduledSecondPicker.Items.Count == 60,
                    $"{expectedTitle}必须复用上方同一个24/60/60萌橘色时分秒组件：" +
                    $"open={scheduledTimePickerPopup.IsOpen}, " +
                    $"target={(scheduledTimePickerPopup.PlacementTarget as FrameworkElement)?.Name}, " +
                    $"title={scheduledTimePickerTitle.Text}, " +
                    $"items={scheduledHourPicker.Items.Count}/" +
                    $"{scheduledMinutePicker.Items.Count}/" +
                    $"{scheduledSecondPicker.Items.Count}");
                scheduledHourPicker.SelectedIndex = hour;
                scheduledMinutePicker.SelectedIndex = minute;
                scheduledSecondPicker.SelectedIndex = second;
                var confirmButton = new Button();
                Invoke(
                    todoWindow,
                    "ScheduledTimePickerConfirmButton_Click",
                    confirmButton,
                    new RoutedEventArgs(
                        ButtonBase.ClickEvent,
                        confirmButton));
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                Assert(!scheduledTimePickerPopup.IsOpen &&
                       input.Text == string.Format(
                           CultureInfo.InvariantCulture,
                           "{0:00}:{1:00}:{2:00}",
                           hour,
                           minute,
                           second),
                    $"{expectedTitle}确定后必须回填完整秒级时间并只收起选择浮层");
            }

            SelectInlineQuietHoursTime(
                scheduledQuietHoursStart,
                20,
                30,
                40,
                "选择免打扰开始时间");
            SelectInlineQuietHoursTime(
                scheduledQuietHoursEnd,
                6,
                7,
                8,
                "选择免打扰结束时间");
            Assert(scheduledTime.Text == expectedLocalNow.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture),
                "选择免打扰开始或结束时间不能串改上方提醒时间");
            scheduledQuietHoursToggle.IsChecked = false;
            scheduledRepeatToggle.IsChecked = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Invoke(todoWindow, "ResetScheduledQuietHoursDraft");
            scheduledInput.ApplyTemplate();
            var scheduledInputBorder = scheduledInput.Template.FindName(
                "ScheduledInputBorder",
                scheduledInput) as Border;
            var scheduledInputHoverTrigger =
                scheduledInput.Template.Triggers
                    .OfType<Trigger>()
                    .Single(trigger =>
                        trigger.Property == UIElement.IsMouseOverProperty &&
                        Equals(trigger.Value, true));
            var scheduledInputHoverBorder =
                scheduledInputHoverTrigger.Setters
                    .OfType<Setter>()
                    .Single(setter =>
                        setter.TargetName == "ScheduledInputBorder" &&
                        setter.Property == Border.BorderBrushProperty)
                    .Value as SolidColorBrush;
            var scheduledInputFocusTrigger =
                scheduledInput.Template.Triggers
                    .OfType<Trigger>()
                    .Single(trigger =>
                        trigger.Property == UIElement.IsKeyboardFocusedProperty &&
                        Equals(trigger.Value, true));
            var scheduledInputFocusBorder =
                scheduledInputFocusTrigger.Setters
                    .OfType<Setter>()
                    .Single(setter =>
                        setter.TargetName == "ScheduledInputBorder" &&
                        setter.Property == Border.BorderBrushProperty)
                    .Value as SolidColorBrush;
            Assert(scheduledInputBorder is not null &&
                   scheduledInputFocusBorder?.Color ==
                       Color.FromRgb(0xE8, 0x9D, 0x52) &&
                   scheduledInputHoverBorder?.Color ==
                       Color.FromRgb(0xF0, 0xAE, 0x6D) &&
                   scheduledInput.SelectionBrush is SolidColorBrush
                       scheduledInputSelection &&
                   scheduledInputSelection.Color ==
                       Color.FromRgb(0xFF, 0xD7, 0xA6),
                "定时任务输入框的悬停、焦点边框和文字选区必须使用橘色系，不能泄露系统蓝色视觉");
            var formattedTime = new FormattedText(
                scheduledTime.Text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    scheduledTime.FontFamily,
                    scheduledTime.FontStyle,
                    scheduledTime.FontWeight,
                    scheduledTime.FontStretch),
                scheduledTime.FontSize,
                scheduledTime.Foreground,
                VisualTreeHelper.GetDpi(scheduledTime).PixelsPerDip);
            Assert(scheduledDatePickerHost.ActualWidth >= 101.5 &&
                   scheduledTimePickerHost.ActualWidth >= 91.5 &&
                   scheduledTime.ActualWidth >=
                   formattedTime.WidthIncludingTrailingWhitespace,
                "日期行必须给 HH:mm:ss 留足宽度，不能被时钟图标或下拉箭头裁掉");
            var scheduledScrollViewer =
                FindVisualDescendant<ScrollViewer>(scheduledList)
                ?? throw new InvalidOperationException(
                    "定时任务列表找不到内部 ScrollViewer");
            var scheduledVisibleContainers = Enumerable.Range(0, 3)
                .Select(index =>
                    scheduledList.ItemContainerGenerator
                        .ContainerFromIndex(index))
                .OfType<FrameworkElement>()
                .ToArray();
            Assert(scheduledList.MinHeight >= 168 &&
                   scheduledList.ActualHeight >= 168 &&
                   scheduledVisibleContainers.Length == 3 &&
                   scheduledVisibleContainers.All(container =>
                   {
                       var top = container.TranslatePoint(
                           new Point(0, 0),
                           scheduledList).Y;
                       var bottom = container.TranslatePoint(
                           new Point(0, container.ActualHeight),
                           scheduledList).Y;
                       return top >= -0.5 &&
                              bottom <= scheduledList.ActualHeight + 0.5;
                   }),
                $"定时任务页默认必须至少完整容纳三项；list={scheduledList.ActualHeight:F1}, " +
                $"containers={scheduledVisibleContainers.Length}, heights=" +
                string.Join(
                    ",",
                    scheduledVisibleContainers.Select(container =>
                        container.ActualHeight.ToString(
                            "F1",
                            CultureInfo.InvariantCulture))));
            Assert(ScrollViewer.GetCanContentScroll(scheduledList) &&
                   !ScrollViewer.GetIsDeferredScrollingEnabled(scheduledList) &&
                   ScrollViewer.GetPanningMode(scheduledList) ==
                       PanningMode.VerticalOnly &&
                   VirtualizingPanel.GetIsVirtualizing(scheduledList) &&
                   VirtualizingPanel.GetVirtualizationMode(scheduledList) ==
                       VirtualizationMode.Recycling &&
                   VirtualizingPanel.GetScrollUnit(scheduledList) ==
                       ScrollUnit.Pixel,
                "定时任务列表必须使用 Pixel + Recycling 即时滚动，滚轮和拖动滑块不得按整项跳动");
            scheduledScrollViewer.ScrollToVerticalOffset(12.5);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(Math.Abs(scheduledScrollViewer.VerticalOffset - 12.5) <
                   0.75,
                $"Pixel 滚动必须保留小数级中间位置，实际 offset={scheduledScrollViewer.VerticalOffset:F2}");
            var scheduledVerticalScrollBar =
                FindVisualDescendants<ScrollBar>(scheduledList)
                    .First(scrollBar =>
                        scrollBar.Orientation == Orientation.Vertical);
            AssertTaskListScrollBarVisual(
                scheduledVerticalScrollBar,
                "定时任务列表",
                Color.FromRgb(0xE7, 0xA1, 0x5E));

            var scheduledShortContainer =
                scheduledList.ItemContainerGenerator.ContainerFromItem(
                    scheduledShortItem) as ListBoxItem
                ?? throw new InvalidOperationException(
                    "短定时任务没有生成可视容器");
            scheduledScrollViewer.ScrollToTop();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var scheduledLongContainer =
                scheduledList.ItemContainerGenerator.ContainerFromItem(
                    scheduledLongItem) as ListBoxItem
                ?? throw new InvalidOperationException(
                    "长定时任务没有生成可视容器");
            scheduledShortContainer.ApplyTemplate();
            Assert(scheduledShortContainer.FocusVisualStyle is null &&
                   scheduledShortContainer.Template.Triggers.Count == 0 &&
                   scheduledShortContainer.BorderBrush is SolidColorBrush
                       scheduledContainerBorder &&
                   scheduledContainerBorder.Color == Colors.Transparent,
                "定时任务行容器必须使用无选择触发器的透明自绘模板，不能在橘色行外叠加系统蓝色选择框");
            var scheduledShortRow =
                FindVisualDescendants<Border>(scheduledShortContainer)
                    .First(border =>
                        string.Equals(
                            border.Name,
                            "ScheduledTaskRowBorder",
                            StringComparison.Ordinal));
            var scheduledLongRow =
                FindVisualDescendants<Border>(scheduledLongContainer)
                    .First(border =>
                        string.Equals(
                            border.Name,
                            "ScheduledTaskRowBorder",
                            StringComparison.Ordinal));
            var scheduledRowHoverTrigger =
                scheduledShortRow.Style.Triggers
                    .OfType<Trigger>()
                    .Single(trigger =>
                        trigger.Property == UIElement.IsMouseOverProperty &&
                        Equals(trigger.Value, true));
            var scheduledRowHoverBackground =
                scheduledRowHoverTrigger.Setters
                    .OfType<Setter>()
                    .Single(setter =>
                        setter.Property == Border.BackgroundProperty)
                    .Value as SolidColorBrush;
            var scheduledRowHoverBorder =
                scheduledRowHoverTrigger.Setters
                    .OfType<Setter>()
                    .Single(setter =>
                        setter.Property == Border.BorderBrushProperty)
                    .Value as SolidColorBrush;
            Assert(scheduledRowHoverBackground?.Color ==
                       Color.FromRgb(0xFF, 0xE8, 0xD0) &&
                   scheduledRowHoverBorder?.Color ==
                       Color.FromRgb(0xF0, 0xA4, 0x5F),
                "鼠标移到定时任务行时必须显示橘色底和橘色框，不能继续使用蓝色悬停框");
            var scheduledShortTextBox =
                FindVisualDescendant<TextBox>(scheduledShortContainer)
                ?? throw new InvalidOperationException(
                    "短定时任务行缺少只读文字框");
            var scheduledShortEditButton =
                FindVisualDescendants<Button>(scheduledShortContainer)
                    .Single(button =>
                        button.Name == "ScheduledTaskEditButton");
            var scheduledShortDeleteButton =
                FindVisualDescendants<Button>(scheduledShortContainer)
                    .Single(button =>
                        button.Name == "ScheduledTaskDeleteButton");
            scheduledShortDeleteButton.ApplyTemplate();
            var scheduledDeleteChrome =
                scheduledShortDeleteButton.Template.FindName(
                    "ScheduledDeleteChrome",
                    scheduledShortDeleteButton) as Border;
            Assert(scheduledShortDeleteButton.Style !=
                       scheduledShortEditButton.Style &&
                   scheduledShortDeleteButton.Background is SolidColorBrush
                       scheduledDeleteBackground &&
                   scheduledDeleteBackground.Color ==
                       Color.FromRgb(0xFF, 0xF3, 0xEE) &&
                   scheduledShortDeleteButton.BorderBrush is SolidColorBrush
                       scheduledDeleteBorder &&
                   scheduledDeleteBorder.Color ==
                       Color.FromRgb(0xF0, 0xC3, 0xB0) &&
                   scheduledShortDeleteButton.Foreground is SolidColorBrush
                       scheduledDeleteForeground &&
                   scheduledDeleteForeground.Color ==
                       Color.FromRgb(0xC8, 0x56, 0x3E) &&
                   scheduledDeleteChrome is not null &&
                   scheduledShortDeleteButton.Content is
                       Viewbox scheduledDeleteIcon &&
                   FindVisualDescendants<System.Windows.Shapes.Ellipse>(
                       scheduledDeleteIcon).Count() == 1 &&
                   FindVisualDescendants<System.Windows.Shapes.Path>(
                       scheduledDeleteIcon).Count() == 2,
                "定时任务删除必须是独立橘红闹钟删除按钮，不能与蓝色待办垃圾桶或铅笔相同");
            var scheduledLongTextBox =
                FindVisualDescendant<TextBox>(scheduledLongContainer)
                ?? throw new InvalidOperationException(
                    "长定时任务行缺少只读文字框");
            var scheduledTimeText =
                FindVisualDescendants<TextBlock>(scheduledShortContainer)
                    .First(textBlock =>
                        textBlock.Text ==
                        scheduledShortItem.DueAtDisplayText);
            AssertClose(
                scheduledTimeText.FontSize,
                11.5,
                "定时任务时间字体");
            AssertClose(
                scheduledShortTextBox.FontSize,
                13,
                "定时任务内容字体");
            Assert(Math.Abs(scheduledShortRow.MaxHeight - 62) <= 0.01 &&
                   Math.Abs(scheduledLongRow.MaxHeight - 62) <= 0.01 &&
                   scheduledLongTextBox.MaxLines == 2 &&
                   Math.Abs(scheduledLongTextBox.MaxHeight - 36) <= 0.01 &&
                   scheduledLongTextBox.ActualHeight >= 33.5 &&
                   scheduledLongTextBox.ActualHeight <= 36.5,
                $"定时任务每行必须给正文完整保留两行36 DIP并把行上限扩到62 DIP；" +
                $"row={scheduledLongRow.MaxHeight:F1}, text=" +
                $"{scheduledLongTextBox.ActualHeight:F1}/{scheduledLongTextBox.MaxHeight:F1}");
            Assert(!(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "IsTaskRowTextClipped",
                       scheduledShortTextBox,
                       scheduledShortItem.Text)!,
                "短定时任务必须被实际识别为完整显示");
            var scheduledShortVisibleEnd = (int)InvokeStatic(
                typeof(TodoWindow),
                "GetTaskRowVisibleTextEnd",
                scheduledShortTextBox)!;
            var scheduledShortRightEdgeCaret = (int)InvokeStatic(
                typeof(TodoWindow),
                "GetTaskRowCharacterIndex",
                scheduledShortTextBox,
                new Point(
                    scheduledShortTextBox.ActualWidth + 20,
                    scheduledShortTextBox.ActualHeight / 2),
                scheduledShortVisibleEnd)!;
            Assert(scheduledShortVisibleEnd == scheduledShortTextBox.Text.Length &&
                   scheduledShortRightEdgeCaret ==
                       scheduledShortTextBox.Text.Length,
                "短定时任务拖到行尾时必须落在末字符之后，最后一个字符必须能够选中复制");
            var taskFullTextPopup = GetField<Popup>(
                todoWindow,
                "TaskFullTextPopup");
            var taskFullTextPreview = GetField<TextBox>(
                todoWindow,
                "TaskFullTextPreviewTextBox");
            var taskFullTextPopupChrome = GetField<Border>(
                todoWindow,
                "TaskFullTextPopupChrome");
            var taskFullTextTitle = GetField<TextBlock>(
                todoWindow,
                "TaskFullTextTitle");
            Invoke(
                todoWindow,
                "TaskRow_MouseEnter",
                scheduledShortRow,
                new MouseEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(!taskFullTextPopup.IsOpen,
                "完整显示的短定时任务悬停时不得弹出全文窗口");
            Assert((bool)InvokeStatic(
                       typeof(TodoWindow),
                       "IsTaskRowTextClipped",
                       scheduledLongTextBox,
                       scheduledLongItem.Text)!,
                "长定时任务测试数据必须被实际识别为裁剪显示");
            var scheduledLongVisibleEnd = (int)InvokeStatic(
                typeof(TodoWindow),
                "GetTaskRowVisibleTextEnd",
                scheduledLongTextBox)!;
            var scheduledLongRightEdgeCaret = (int)InvokeStatic(
                typeof(TodoWindow),
                "GetTaskRowCharacterIndex",
                scheduledLongTextBox,
                new Point(
                    scheduledLongTextBox.ActualWidth + 20,
                    Math.Max(0, scheduledLongTextBox.ActualHeight - 1)),
                scheduledLongVisibleEnd)!;
            Assert(scheduledLongVisibleEnd > 0 &&
                   scheduledLongVisibleEnd <
                       scheduledLongTextBox.Text.Length &&
                   scheduledLongRightEdgeCaret == scheduledLongVisibleEnd,
                "两行长定时任务拖到右下边界时必须选中最后一个可见字符，且不能越入隐藏第三行");
            Invoke(
                todoWindow,
                "TaskRow_MouseEnter",
                scheduledLongRow,
                new MouseEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(taskFullTextPopup.IsOpen &&
                   taskFullTextPopup.PlacementTarget ==
                        scheduledLongRow &&
                   taskFullTextPreview.DataContext ==
                       scheduledLongItem &&
                   taskFullTextPreview.Text == scheduledLongText &&
                   GetField<TextBlock>(
                       todoWindow,
                       "TaskFullTextTitle").Text ==
                        "提醒完整内容 · 可选择复制",
                "只有实际裁剪的定时任务才可打开左优先全文窗，并显示完整可选择文字");
            AssertTaskFullTextTheme(
                todoWindow,
                scheduled: true);
            Invoke(todoWindow, "CloseTaskFullTextPreview");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(taskFullTextPopup.PlacementTarget is null &&
                   taskFullTextPreview.DataContext is null &&
                   taskFullTextPreview.Text.Length == 0 &&
                   GetField<bool>(
                       todoWindow,
                       "_isTaskFullTextPopupOpen") is false &&
                   !todoWindow.IsTransientPopupOpen,
                "关闭定时任务全文窗后必须同步释放长文本、行容器和 transient 状态，不能阻塞后续外点收起");
            scheduledTasks.Clear();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            todoWindow.ShowDefaultTab();
            Assert(todoTab.IsChecked == true &&
                   scheduledTab.IsChecked != true &&
                   todoPage.Visibility == Visibility.Visible &&
                   scheduledPage.Visibility == Visibility.Hidden,
                "ShowDefaultTab 必须可重用地恢复默认待办页");

            var transientCompletionCount = 0;
            todoWindow.TransientInteractionCompleted += () =>
                transientCompletionCount++;
            Invoke(todoWindow, "SelectTaskPage", true, false);
            var timeBeforeDateBrowsing = scheduledTime.Text;
            Invoke(todoWindow, "OpenScheduledDatePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledDatePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen &&
                   scheduledDateItems.Items.Count == 42 &&
                   scheduledDateMonthText.Text ==
                   expectedLocalNow.ToString(
                       "yyyy年M月",
                       CultureInfo.GetCultureInfo("zh-CN")),
                "自定义日期浮层必须使用固定六周、可切换月份并标记 transient 交互");
            var currentMonthCells = scheduledDateItems.Items
                .Cast<object>()
                .Count(cell => GetProperty<bool>(cell, "IsCurrentMonth"));
            var selectedCells = scheduledDateItems.Items
                .Cast<object>()
                .Where(cell => GetProperty<bool>(cell, "IsSelected"))
                .ToArray();
            Assert(currentMonthCells == DateTime.DaysInMonth(
                       expectedLocalNow.Year,
                       expectedLocalNow.Month) &&
                   selectedCells.Length == 1 &&
                   GetProperty<DateTime>(selectedCells[0], "Date") ==
                   expectedLocalNow.Date,
                "自定义日期浮层必须准确生成本月天数并唯一标出当前草稿日期");

            Invoke(
                todoWindow,
                "RefreshScheduledCalendar",
                new DateTime(2031, 12, 1));
            Assert(scheduledDateMonthText.Text == "2031年12月" &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime browsedDate &&
                   browsedDate == expectedLocalNow.Date &&
                   scheduledDateInput.Text == "2031-05-06" &&
                   scheduledTime.Text == timeBeforeDateBrowsing &&
                   GetRawField(todoWindow, "_scheduledTaskDraftClockEdited") is false,
                "浏览到同年其他月份不得偷偷修改已选日期、时分秒或草稿编辑状态");
            scheduledDateNextMonthButton.RaiseEvent(
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    scheduledDateNextMonthButton));
            Assert(scheduledDateMonthText.Text == "2032年1月",
                "点击“下一个月”必须从十二月正确跨到下一年一月");
            scheduledDatePreviousMonthButton.RaiseEvent(
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    scheduledDatePreviousMonthButton));
            Assert(scheduledDateMonthText.Text == "2031年12月" &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime returnedDate &&
                   returnedDate == expectedLocalNow.Date,
                "点击“上一个月”必须从一月正确退回上一年十二月，且不得改动已选日期");
            Invoke(
                todoWindow,
                "RefreshScheduledCalendar",
                new DateTime(2032, 2, 1));
            Assert(scheduledDateItems.Items
                       .Cast<object>()
                       .Any(cell =>
                           GetProperty<DateTime>(cell, "Date") ==
                           new DateTime(2032, 2, 29) &&
                           GetProperty<bool>(cell, "IsCurrentMonth")),
                "固定六周月历必须正确显示闰年二月二十九日");

            var dateInputSource = PresentationSource.FromVisual(scheduledDateInput)
                ?? throw new InvalidOperationException("日期入口未建立输入源");
            var dateEscape = CreateKeyEvent(dateInputSource, Key.Escape);
            Invoke(
                todoWindow,
                "TodoWindow_PreviewKeyDown",
                todoWindow,
                dateEscape);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(dateEscape.Handled &&
                   !scheduledDatePickerPopup.IsOpen &&
                   !todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 1 &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime escapedDate &&
                   escapedDate == expectedLocalNow.Date &&
                   scheduledTime.Text == timeBeforeDateBrowsing,
                $"Esc 必须只关闭日期浮层，不改变已选日期或时分秒，并只通知一次结束；" +
                $"Handled={dateEscape.Handled}, DateOpen={scheduledDatePickerPopup.IsOpen}, " +
                $"Transient={todoWindow.IsTransientPopupOpen}, Completions={transientCompletionCount}, " +
                $"Date={GetRawField(todoWindow, "_scheduledDate")}, Time={scheduledTime.Text}");
            Invoke(todoWindow, "CloseScheduledDatePicker");
            Assert(transientCompletionCount == 1,
                "重复关闭日期浮层不得重复发出 transient 完成事件");

            Invoke(todoWindow, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledTimePickerPopup.IsOpen &&
                   !scheduledDatePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen,
                "打开秒级时间浮层时必须关闭日期浮层并保持 transient 保护");
            Invoke(todoWindow, "OpenScheduledDatePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledDatePickerPopup.IsOpen &&
                   !scheduledTimePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 1,
                "日期和时间浮层必须原子互斥，切换中不能误报交互结束");
            Invoke(todoWindow, "CloseScheduledDatePicker");
            Assert(!todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 2,
                "最后一个日期或时间浮层关闭时必须且只能通知一次结束");

            Invoke(
                todoWindow,
                "OpenScheduledDatePicker");
            Invoke(
                todoWindow,
                "RefreshScheduledCalendar",
                new DateTime(2032, 2, 1));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var leapDate = new DateTime(2032, 2, 29);
            var leapCell = scheduledDateItems.Items
                .Cast<object>()
                .Single(cell => GetProperty<DateTime>(cell, "Date") == leapDate);
            var leapContainer = scheduledDateItems.ItemContainerGenerator
                .ContainerFromItem(leapCell) as DependencyObject
                ?? throw new InvalidOperationException("闰日日期格没有生成可视容器");
            var leapButton = FindVisualDescendants<Button>(leapContainer).Single();
            Assert(leapButton.Tag is DateTime leapTag && leapTag == leapDate,
                "闰日按钮必须通过真实 Tag 绑定携带 2032-02-29");
            var timeBeforeLeapSelection = scheduledTime.Text;
            leapButton.RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent, leapButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(GetRawField(todoWindow, "_scheduledDate") is DateTime selectedLeapDay &&
                   selectedLeapDay == leapDate &&
                   scheduledDateInput.Text == "2032-02-29" &&
                   scheduledTime.Text == timeBeforeLeapSelection &&
                   GetRawField(todoWindow, "_scheduledTaskDraftClockEdited") is true &&
                   !scheduledDatePickerPopup.IsOpen &&
                   !todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 3,
                "真实闰日按钮必须选择日期后收起浮层，且不能改动已经选好的时分秒或重复报告交互结束");
            Invoke(todoWindow, "CloseScheduledDatePicker");
            Assert(!scheduledDatePickerPopup.IsOpen &&
                   transientCompletionCount == 3,
                "日期选中自动收起后，重复关闭不得再次报告交互结束");

            var todayBeforeClick = DateTime.Today;
            var timeBeforeToday = scheduledTime.Text;
            Invoke(todoWindow, "OpenScheduledDatePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            scheduledDateTodayButton.RaiseEvent(
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    scheduledDateTodayButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var todayAfterClick = DateTime.Today;
            Assert(GetRawField(todoWindow, "_scheduledDate") is DateTime selectedToday &&
                   (selectedToday == todayBeforeClick ||
                    selectedToday == todayAfterClick) &&
                   scheduledDateInput.Text == selectedToday.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == timeBeforeToday &&
                   !scheduledDatePickerPopup.IsOpen &&
                   !todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 4,
                "真实“今天”按钮必须选择本地今天后收起日期浮层，且不能改动时分秒");
            Invoke(todoWindow, "CloseScheduledDatePicker");
            Assert(!scheduledDatePickerPopup.IsOpen &&
                   transientCompletionCount == 4,
                "“今天”选择自动收起后，重复关闭不得再次报告交互结束");
            Invoke(todoWindow, "ResetScheduledTaskDraftClock", exactNow);
            Invoke(todoWindow, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            scheduledRepeatToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledTimePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen,
                "勾选循环后仍必须显示首次/下一次日期和时间，且已经打开的时间浮层不能消失");
            var completionBeforeTimeParts =
                transientCompletionCount;
            scheduledRepeatCount.Text = "7";
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledTimePickerPopup.IsOpen &&
                   scheduledRepeatCount.Text == "7" &&
                   transientCompletionCount ==
                       completionBeforeTimeParts,
                "在时间窗口中修改循环次数不得关闭外层时间Popup");
            scheduledRepeatToggle.IsChecked = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledTimePickerPopup.IsOpen &&
                   transientCompletionCount ==
                       completionBeforeTimeParts,
                "取消循环勾选也不得把仍在选择的时间窗口收起");
            scheduledRepeatToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledTimePickerPopup.IsOpen &&
                   transientCompletionCount ==
                       completionBeforeTimeParts,
                "重新勾选循环必须保持当前时间窗口和已选时分秒");
            scheduledRepeatUnit.IsDropDownOpen = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var repeatUnitOption =
                scheduledRepeatUnit.ItemContainerGenerator
                    .ContainerFromIndex((int)ScheduledRepeatUnit.Day)
                    as ComboBoxItem
                ?? throw new InvalidOperationException(
                    "循环单位选择器未生成“天”选项");
            var repeatUnitOptionClick = new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Left)
            {
                RoutedEvent =
                    UIElement.PreviewMouseLeftButtonDownEvent,
                Source = repeatUnitOption
            };
            repeatUnitOption.RaiseEvent(repeatUnitOptionClick);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(repeatUnitOptionClick.Handled &&
                   scheduledRepeatUnit.SelectedIndex ==
                       (int)ScheduledRepeatUnit.Day &&
                   !scheduledRepeatUnit.IsDropDownOpen &&
                   scheduledTimePickerPopup.IsOpen &&
                   transientCompletionCount ==
                       completionBeforeTimeParts,
                "切换循环单位只能收起单位下拉，不能关闭正在连续选择的提醒时间窗口");
            foreach (var (picker, selectedIndex) in new[]
                     {
                         (scheduledHourPicker, 11),
                         (scheduledMinutePicker, 22),
                         (scheduledSecondPicker, 33)
                     })
            {
                picker.IsDropDownOpen = true;
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                var option = picker.ItemContainerGenerator.ContainerFromIndex(selectedIndex)
                    as ComboBoxItem
                    ?? throw new InvalidOperationException(
                        $"时分秒选择器未生成第 {selectedIndex} 个可点击选项");
                var optionClick = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = option
                };
                option.RaiseEvent(optionClick);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Assert(optionClick.Handled &&
                       picker.SelectedIndex == selectedIndex &&
                       !picker.IsDropDownOpen &&
                       scheduledTimePickerPopup.IsOpen &&
                       todoWindow.IsTransientPopupOpen &&
                       transientCompletionCount ==
                           completionBeforeTimeParts,
                    "逐一选择时、分、秒并处理完 Dispatcher 消息后，只能关闭当前下拉层，外层时间浮层必须保持打开");
            }

            Assert(scheduledTime.Text == "11:22:33",
                "依次选择时、分、秒后，右侧入口必须完整同步为 11:22:33");
            scheduledRepeatUnit.IsDropDownOpen = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledRepeatUnit.IsDropDownOpen,
                "Esc 回归前必须真实打开循环单位下拉");
            var timePickerInputSource =
                PresentationSource.FromVisual(scheduledHourPicker)
                ?? throw new InvalidOperationException(
                    "时间浮层没有为小时选择器建立输入源");
            var timePickerEscape = CreateKeyEvent(
                timePickerInputSource,
                Key.Escape);
            Invoke(
                todoWindow,
                "ScheduledTimePickerPopup_PreviewKeyDown",
                scheduledHourPicker,
                timePickerEscape);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(timePickerEscape.Handled &&
                   !scheduledTimePickerPopup.IsOpen &&
                   !scheduledHourPicker.IsDropDownOpen &&
                   !scheduledMinutePicker.IsDropDownOpen &&
                   !scheduledSecondPicker.IsDropDownOpen &&
                   !scheduledRepeatUnit.IsDropDownOpen &&
                   !todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 5,
                "焦点位于独立时间 Popup 时，Esc 仍必须关闭外层和三列下拉并且只通知一次结束");

            Invoke(todoWindow, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var timeConfirmButton = new Button();
            Invoke(
                todoWindow,
                "ScheduledTimePickerConfirmButton_Click",
                timeConfirmButton,
                new RoutedEventArgs(ButtonBase.ClickEvent, timeConfirmButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(!scheduledTimePickerPopup.IsOpen &&
                   scheduledTime.Text == "11:22:33",
                "确定时必须关闭时间浮层并保留刚选好的完整 HH:mm:ss");
            var completionBeforeOutsideClick =
                transientCompletionCount;
            Invoke(todoWindow, "OpenScheduledTimePicker");
            Assert(scheduledTimePickerPopup.IsOpen,
                "真实外点回归必须先重新打开时间窗口");
            // This window is intentionally off-screen and non-activating.
            // Pumping the real desktop message queue here lets the unrelated
            // foreground application look like an OS-level outside click.
            // Raise the in-form outside press in the same input transaction;
            // its PreviewMouseDown path is the behavior under test.
            RaisePreviewMouseDown(scheduledInput);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(!scheduledTimePickerPopup.IsOpen &&
                   transientCompletionCount ==
                       completionBeforeOutsideClick + 1,
                "点击时间/循环区域之外的真实表单内容必须关闭时间窗口，并且只通知一次交互结束");
            var repeatPreviewCases = new[]
            {
                (
                    Unit: ScheduledRepeatUnit.Hour,
                    Every: 1,
                    Hour: 19,
                    Minute: 11,
                    Second: 10,
                    Expected: "每 1 小时，在第 11 分 10 秒提醒"),
                (
                    Unit: ScheduledRepeatUnit.Hour,
                    Every: 3,
                    Hour: 19,
                    Minute: 11,
                    Second: 10,
                    Expected: "每 3 小时，在第 11 分 10 秒提醒"),
                (
                    Unit: ScheduledRepeatUnit.Day,
                    Every: 1,
                    Hour: 11,
                    Minute: 11,
                    Second: 11,
                    Expected: "每 1 天，在 11:11:11 提醒"),
                (
                    Unit: ScheduledRepeatUnit.Day,
                    Every: 4,
                    Hour: 11,
                    Minute: 11,
                    Second: 11,
                    Expected: "每 4 天，在 11:11:11 提醒"),
                (
                    Unit: ScheduledRepeatUnit.Hour,
                    Every: 1,
                    Hour: 19,
                    Minute: 22,
                    Second: 0,
                    Expected: "每 1 小时，在第 22 分 00 秒提醒"),
                (
                    Unit: ScheduledRepeatUnit.Hour,
                    Every: 5,
                    Hour: 19,
                    Minute: 22,
                    Second: 0,
                    Expected: "每 5 小时，在第 22 分 00 秒提醒")
            };
            foreach (var previewCase in repeatPreviewCases)
            {
                scheduledRepeatUnit.SelectedIndex =
                    (int)previewCase.Unit;
                scheduledRepeatCount.Text =
                    previewCase.Every.ToString(
                        CultureInfo.InvariantCulture);
                Invoke(
                    todoWindow,
                    "SetScheduledTimePickerSelection",
                    previewCase.Hour,
                    previewCase.Minute,
                    previewCase.Second,
                    true);
                Assert(
                    scheduledRepeatRulePreview.Visibility ==
                        Visibility.Visible &&
                    scheduledRepeatRulePreview.Text ==
                        previewCase.Expected,
                    $"循环规则预览必须简单直白：{previewCase.Expected}");
                AssertScheduledFormLayout(
                    $"循环预览“{previewCase.Expected}”");
            }

            Invoke(todoWindow, "ResetScheduledRepeatDraft");

            var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
            var outsideCloseSource = ExtractPrivateMethodSource(
                mainSource,
                "ProcessOutsideTodoClose");
            var todoSource = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml.cs"));
            var todoXaml = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml"));
            var scheduledTabClickSource = ExtractPrivateMethodSource(
                todoSource,
                "ScheduledTaskTabButton_Click");
            var resetDraftClockSource = ExtractPrivateMethodSource(
                todoSource,
                "ResetScheduledTaskDraftClock");
            var queuePickerOutsideProbeSource = ExtractPrivateMethodSource(
                todoSource,
                "QueueScheduledPickerOutsideProbe");
            var processPickerOutsideProbeSource =
                ExtractPrivateMethodSource(
                    todoSource,
                    "ProcessScheduledPickerOutsideProbe");
            var isScheduledPickerOpenSource =
                ExtractPrivateMethodSource(
                    todoSource,
                    "IsScheduledPickerOpen");
            Assert(isScheduledPickerOpenSource.Contains(
                       "ScheduledDatePickerPopup?.IsOpen == true",
                       StringComparison.Ordinal) &&
                   isScheduledPickerOpenSource.Contains(
                       "ScheduledTimePickerPopup?.IsOpen == true",
                       StringComparison.Ordinal),
                "TodoWindow 构造期的 TextChanged 可能早于 Popup 字段初始化；选择器状态检查必须空安全，首次启动不得崩溃");
            Assert(outsideCloseSource.Contains(
                       "_todoWindow.IsTransientPopupOpen",
                       StringComparison.Ordinal) &&
                   scheduledTabClickSource.Contains(
                       "PrepareScheduledTaskDraftClockForDisplay(DateTimeOffset.Now)",
                       StringComparison.Ordinal) &&
                   resetDraftClockSource.Contains(
                       "var suggested = now.LocalDateTime",
                       StringComparison.Ordinal) &&
                   !resetDraftClockSource.Contains(
                       "AddMinutes(",
                       StringComparison.Ordinal),
                "MainWindow 的外部点击收起判定必须显式保护自定义日期和时间 transient popup");
            Assert(queuePickerOutsideProbeSource.Contains(
                       "DispatcherPriority.ApplicationIdle",
                       StringComparison.Ordinal) &&
                   queuePickerOutsideProbeSource.Contains(
                       "_scheduledPickerState",
                       StringComparison.Ordinal) &&
                   processPickerOutsideProbeSource.Contains(
                       "probe.StateAtQueue == ScheduledPickerState.InternalCommit",
                       StringComparison.Ordinal) &&
                   processPickerOutsideProbeSource.Contains(
                       "var currentForegroundWindow = GetForegroundWindow()",
                       StringComparison.Ordinal) &&
                   processPickerOutsideProbeSource.Contains(
                       "WindowFromPoint(currentPointer)",
                       StringComparison.Ordinal) &&
                   processPickerOutsideProbeSource.Contains(
                       "IsKnownScheduledPickerWindow(currentForegroundWindow)",
                       StringComparison.Ordinal) &&
                   processPickerOutsideProbeSource.Contains(
                       "IsPointerOverScheduledPickerSurface()",
                       StringComparison.Ordinal) &&
                   processPickerOutsideProbeSource.Contains(
                       "CloseScheduledPickers()",
                       StringComparison.Ordinal),
                "ComboBox子Popup失活后必须等到ApplicationIdle再读取当前HWND和指针；内部切换保活，只有真实外点才关闭");
            var datePopupChild = scheduledDatePickerPopup.Child
                ?? throw new InvalidOperationException("日期浮层缺少可视子树");
            var timePopupChild = scheduledTimePickerPopup.Child
                ?? throw new InvalidOperationException("时间浮层缺少可视子树");
            Assert(!(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledDatePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       datePopupChild,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)! &&
                   (bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledInput,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)!,
                "日期浮层外点判定必须保护日期宿主、时间宿主和自身子树，只把表单其他区域视为外部");
            Assert(!(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledTimePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       timePopupChild,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)! &&
                   (bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledInput,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)!,
                "时间浮层外点判定必须保护时间宿主、日期宿主和自身子树，只把表单其他区域视为外部");
            Assert(!todoXaml.Contains("<DatePicker", StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "x:Name=\"ScheduledDatePickerPopup\"",
                       StringComparison.Ordinal),
                "日期入口必须彻底摆脱系统 DatePicker，并使用命名的自定义日期浮层");
            Assert(todoXaml.Contains(
                       "x:Name=\"ScheduledQuietHoursStartInput\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "x:Name=\"ScheduledQuietHoursEndInput\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "PreviewMouseLeftButtonDown=\"ScheduledQuietHoursTimeInput_PreviewMouseLeftButtonDown\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "PreviewKeyDown=\"ScheduledQuietHoursTimeInput_PreviewKeyDown\"",
                       StringComparison.Ordinal) &&
                   !todoXaml.Contains(
                       "ScheduledQuietHoursTimeInput_PreviewTextInput",
                       StringComparison.Ordinal),
                "新增和内联修改的免打扰开始/结束时间必须只通过共用选择器编辑，不能保留手输事件");
            var repeatUnitTemplateStart = todoXaml.IndexOf(
                "<ComboBox x:Name=\"ScheduledRepeatUnitComboBox\"",
                StringComparison.Ordinal);
            var repeatUnitTemplateEnd = repeatUnitTemplateStart >= 0
                ? todoXaml.IndexOf(
                    "</ComboBox>",
                    repeatUnitTemplateStart,
                    StringComparison.Ordinal)
                : -1;
            Assert(repeatUnitTemplateStart >= 0 &&
                   repeatUnitTemplateEnd > repeatUnitTemplateStart,
                "循环单位下拉必须存在完整自定义模板");
            var repeatUnitTemplate = todoXaml[
                repeatUnitTemplateStart..
                (repeatUnitTemplateEnd + "</ComboBox>".Length)];
            Assert(repeatUnitTemplate.Contains(
                       "x:Name=\"RepeatUnitButtonBorder\"",
                       StringComparison.Ordinal) &&
                   repeatUnitTemplate.Contains(
                       "Background=\"#FFFFF3DF\"",
                       StringComparison.Ordinal) &&
                   repeatUnitTemplate.Contains(
                       "BorderBrush=\"#E8BE89\"",
                       StringComparison.Ordinal) &&
                   repeatUnitTemplate.Contains(
                       "x:Name=\"PART_Popup\"",
                       StringComparison.Ordinal) &&
                   repeatUnitTemplate.Contains(
                       "BorderBrush=\"#EAB777\"",
                       StringComparison.Ordinal) &&
                   repeatUnitTemplate.Contains(
                       "<Trigger Property=\"IsHighlighted\" Value=\"True\">",
                       StringComparison.Ordinal) &&
                   repeatUnitTemplate.Contains(
                       "<Trigger Property=\"IsSelected\" Value=\"True\">",
                       StringComparison.Ordinal),
                "分钟/小时/天下拉必须使用与定时任务一致的橘色按钮、Popup、悬停和选中视觉");
            Assert(mainSource.Contains(
                       "_todoWindow.ScheduledTaskEditRequested +=",
                       StringComparison.Ordinal) &&
                   mainSource.Contains(
                       "TodoWindow_ScheduledTaskEditRequested",
                       StringComparison.Ordinal),
                "MainWindow 必须订阅定时任务编辑事件并交给排序、持久化和重调度处理器");

            var requestedCount = 0;
            string? requestedText = null;
            DateTimeOffset requestedDueAt = default;
            TimeSpan? requestedRepeatInterval = null;
            ScheduledRepeatRule? requestedRepeatRule = null;
            ScheduledQuietHours? requestedQuietHours = null;
            todoWindow.ScheduledTaskAddRequested += (
                text,
                dueAt,
                repeatInterval,
                repeatRule,
                quietHours) =>
            {
                requestedCount++;
                requestedText = text;
                requestedDueAt = dueAt;
                requestedRepeatInterval = repeatInterval;
                requestedRepeatRule = repeatRule;
                requestedQuietHours = quietHours;
            };

            var futureLocal = DateTime.Now.AddHours(2);
            futureLocal = new DateTime(
                futureLocal.Year,
                futureLocal.Month,
                futureLocal.Day,
                futureLocal.Hour,
                futureLocal.Minute,
                futureLocal.Second,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(futureLocal))
            {
                futureLocal = futureLocal.AddHours(1);
            }

            scheduledInput.Text = "  明天带好小喇叭  ";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                futureLocal.Date,
                true);
            scheduledTime.Text = futureLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            var expectedDueAt = new DateTimeOffset(
                futureLocal,
                TimeZoneInfo.Local.GetUtcOffset(futureLocal));
            Assert(requestedCount == 1 &&
                   requestedText == "明天带好小喇叭" &&
                   requestedDueAt == expectedDueAt &&
                   requestedDueAt.Ticks % TimeSpan.TicksPerSecond == 0 &&
                   requestedRepeatInterval is null &&
                   requestedRepeatRule is null &&
                   requestedQuietHours is null,
                "定时页应发出去除首尾空白的内容和精确到整秒的本地 DateTimeOffset");
            Assert(scheduledInput.Text.Length == 0 &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime &&
                   DateTime.TryParseExact(
                       scheduledDateInput.Text,
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out _) &&
                   DateTime.TryParseExact(
                       scheduledTime.Text,
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out _),
                "成功设定后应清空内容并重置为新的秒级默认时间");

            var requestedRecurringInterval = TimeSpan.FromHours(3);
            var recurringFirstLocal = DateTime.Now.AddDays(3);
            recurringFirstLocal = new DateTime(
                recurringFirstLocal.Year,
                recurringFirstLocal.Month,
                recurringFirstLocal.Day,
                14,
                25,
                36,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(recurringFirstLocal))
            {
                recurringFirstLocal = recurringFirstLocal.AddHours(1);
            }

            Invoke(
                todoWindow,
                "SetScheduledDate",
                recurringFirstLocal.Date,
                true);
            Invoke(
                todoWindow,
                "SetScheduledTimePickerSelection",
                recurringFirstLocal.Hour,
                recurringFirstLocal.Minute,
                recurringFirstLocal.Second,
                true);
            scheduledRepeatToggle.IsChecked = true;
            scheduledRepeatCount.Text = "3";
            scheduledRepeatUnit.SelectedIndex =
                (int)ScheduledRepeatUnit.Hour;
            scheduledQuietHoursToggle.IsChecked = true;
            SelectInlineQuietHoursTime(
                scheduledQuietHoursStart,
                21,
                15,
                30,
                "选择免打扰开始时间");
            SelectInlineQuietHoursTime(
                scheduledQuietHoursEnd,
                6,
                45,
                10,
                "选择免打扰结束时间");
            scheduledInput.Text = "  循环检查小喇叭  ";
            Assert(scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledRepeatRulePreview.Visibility ==
                       Visibility.Visible &&
                   scheduledRepeatRulePreview.Text ==
                       "每 3 小时，在第 25 分 36 秒提醒" &&
                   scheduledDateInput.Text == recurringFirstLocal.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == recurringFirstLocal.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture),
                "勾选循环后必须继续显示并保留用户选择的首次提醒日期和 HH:mm:ss");
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            var expectedRecurringFirstDueAt = new DateTimeOffset(
                recurringFirstLocal,
                TimeZoneInfo.Local.GetUtcOffset(recurringFirstLocal));
            Assert(requestedCount == 2 &&
                   requestedText == "循环检查小喇叭" &&
                   requestedRepeatInterval == requestedRecurringInterval &&
                   requestedRepeatRule is
                   {
                       Unit: ScheduledRepeatUnit.Hour,
                       Every: 3,
                       NextOrdinal: 0
                   } &&
                   requestedQuietHours is
                   {
                       Start.Hours: 21,
                       Start.Minutes: 15,
                       Start.Seconds: 30,
                       End.Hours: 6,
                       End.Minutes: 45,
                       End.Seconds: 10
                   } &&
                   requestedQuietHours.TimeZoneId ==
                       TimeZoneInfo.Local.Id &&
                   requestedDueAt == expectedRecurringFirstDueAt &&
                   requestedDueAt.Ticks % TimeSpan.TicksPerSecond == 0,
                "新增循环任务必须保存首次 DueAt、后续间隔以及可跨午夜的萌橘色免打扰时段");
            Assert(scheduledInput.Text.Length == 0 &&
                   scheduledRepeatToggle.IsChecked != true &&
                   scheduledQuietHoursEditor.Visibility ==
                       Visibility.Collapsed &&
                   scheduledQuietHoursToggle.IsChecked != true &&
                   scheduledQuietHoursStart.Text == "22:00:00" &&
                   scheduledQuietHoursEnd.Text == "07:00:00" &&
                   scheduledRepeatCount.Text == "1" &&
                   scheduledRepeatUnit.SelectedIndex ==
                       (int)ScheduledRepeatUnit.Hour &&
                   scheduledRepeatRulePreview.Visibility ==
                       Visibility.Collapsed &&
                   scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed,
                "循环任务新增成功后必须安全恢复默认单次草稿，避免下一条被误设为循环");

            scheduledRepeatToggle.IsChecked = true;
            scheduledQuietHoursToggle.IsChecked = true;
            SelectInlineQuietHoursTime(
                scheduledQuietHoursStart,
                23,
                10,
                5,
                "选择免打扰开始时间");
            SelectInlineQuietHoursTime(
                scheduledQuietHoursEnd,
                23,
                10,
                5,
                "选择免打扰结束时间");
            scheduledInput.Text = "免打扰同值校验";
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(requestedCount == 2 &&
                   validationText.Text.Contains(
                       "不能相同",
                       StringComparison.Ordinal) &&
                   scheduledTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledTimePickerPopup.PlacementTarget,
                       scheduledQuietHoursEnd) &&
                   scheduledHourPicker.SelectedIndex == 23 &&
                   scheduledMinutePicker.SelectedIndex == 10 &&
                   scheduledSecondPicker.SelectedIndex == 5,
                "免打扰开始和结束相同必须重新打开结束时间选择器并阻止误设为全天静默");
            scheduledInput.Clear();
            Invoke(todoWindow, "ResetScheduledRepeatDraft");
            Invoke(todoWindow, "ResetScheduledQuietHoursDraft");
            Invoke(todoWindow, "SetScheduledTaskValidation", string.Empty);

            var editLocal = DateTime.Now.AddHours(4);
            editLocal = new DateTime(
                editLocal.Year,
                editLocal.Month,
                editLocal.Day,
                editLocal.Hour,
                editLocal.Minute,
                editLocal.Second,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(editLocal))
            {
                editLocal = editLocal.AddHours(1);
            }

            var editItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "要修改的定时任务",
                DueAt = new DateTimeOffset(
                    editLocal,
                    TimeZoneInfo.Local.GetUtcOffset(editLocal)),
                CreatedAt = DateTimeOffset.Now.AddMinutes(-2)
            };
            scheduledTasks.Add(editItem);
            Invoke(todoWindow, "SelectTaskPage", true, false);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));

            var editContainer = scheduledList.ItemContainerGenerator.ContainerFromItem(editItem)
                as FrameworkElement
                ?? throw new InvalidOperationException("定时任务编辑回归未生成列表行");
            var editButton = FindVisualDescendants<Button>(editContainer)
                .SingleOrDefault(button => button.Name == "ScheduledTaskEditButton")
                ?? throw new InvalidOperationException("定时任务行缺少铅笔编辑按钮");
            Assert(ReferenceEquals(editButton.Tag, editItem) &&
                   editButton.Content is Viewbox editIcon &&
                   FindVisualDescendants<System.Windows.Shapes.Path>(editIcon).Count() == 2,
                "定时任务编辑按钮必须使用与待办关闭按钮同风格的双路径斜铅笔图标并绑定当前项");

            var editRequestedCount = 0;
            ScheduledTaskItem? requestedEditItem = null;
            string? requestedEditText = null;
            DateTimeOffset requestedEditDueAt = default;
            TimeSpan? requestedEditRepeatInterval = null;
            ScheduledRepeatRule? requestedEditRepeatRule = null;
            ScheduledQuietHours? requestedEditQuietHours = null;
            todoWindow.ScheduledTaskEditRequested += (
                item,
                text,
                dueAt,
                repeatInterval,
                repeatRule,
                quietHours) =>
            {
                editRequestedCount++;
                requestedEditItem = item;
                requestedEditText = text;
                requestedEditDueAt = dueAt;
                requestedEditRepeatInterval = repeatInterval;
                requestedEditRepeatRule = repeatRule;
                requestedEditQuietHours = quietHours;
            };

            Invoke(
                todoWindow,
                "ScheduledTaskEditButton_Click",
                editButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    editButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            var scheduledEditor = GetField<ScheduledTaskEditWindow>(
                todoWindow,
                "_scheduledTaskEditWindow");
            var scheduledEditorText = GetField<TextBox>(
                scheduledEditor,
                "TaskTextBox");
            var scheduledEditorDate = GetField<DatePicker>(
                scheduledEditor,
                "DueDatePicker");
            var scheduledEditorHour = GetField<ComboBox>(
                scheduledEditor,
                "HourComboBox");
            var scheduledEditorMinute = GetField<ComboBox>(
                scheduledEditor,
                "MinuteComboBox");
            var scheduledEditorSecond = GetField<ComboBox>(
                scheduledEditor,
                "SecondComboBox");
            var scheduledEditorTimeInput = GetField<TextBox>(
                scheduledEditor,
                "ScheduledTimeInput");
            var scheduledEditorTimePickerPopup = GetField<Popup>(
                scheduledEditor,
                "ScheduledTimePickerPopup");
            var scheduledEditorTimePickerTitle = GetField<TextBlock>(
                scheduledEditor,
                "ScheduledTimePickerTitle");
            var scheduledEditorHourPicker = GetField<ComboBox>(
                scheduledEditor,
                "ScheduledHourComboBox");
            var scheduledEditorMinutePicker = GetField<ComboBox>(
                scheduledEditor,
                "ScheduledMinuteComboBox");
            var scheduledEditorSecondPicker = GetField<ComboBox>(
                scheduledEditor,
                "ScheduledSecondComboBox");
            var scheduledEditorRepeatToggle = GetField<CheckBox>(
                scheduledEditor,
                "RepeatToggle");
            var scheduledEditorQuietHoursEditor = GetField<Grid>(
                scheduledEditor,
                "QuietHoursEditor");
            var scheduledEditorQuietHoursToggle = GetField<CheckBox>(
                scheduledEditor,
                "QuietHoursToggle");
            var scheduledEditorQuietHoursStartHost = GetField<Border>(
                scheduledEditor,
                "QuietHoursStartPickerHost");
            var scheduledEditorQuietHoursStart = GetField<TextBox>(
                scheduledEditor,
                "QuietHoursStartTextBox");
            var scheduledEditorQuietHoursEndHost = GetField<Border>(
                scheduledEditor,
                "QuietHoursEndPickerHost");
            var scheduledEditorQuietHoursEnd = GetField<TextBox>(
                scheduledEditor,
                "QuietHoursEndTextBox");
            var scheduledEditorQuietHoursOvernightHint = GetField<TextBlock>(
                scheduledEditor,
                "QuietHoursOvernightHint");
            var scheduledEditorRepeatCount = GetField<TextBox>(
                scheduledEditor,
                "RepeatCountTextBox");
            var scheduledEditorRepeatUnit = GetField<ComboBox>(
                scheduledEditor,
                "RepeatUnitComboBox");
            var scheduledEditorDateHost = GetField<Border>(
                scheduledEditor,
                "ScheduledDatePickerHost");
            var scheduledEditorTimeHost = GetField<Border>(
                scheduledEditor,
                "ScheduledTimePickerHost");
            var scheduledEditorSaveButton = GetField<Button>(
                scheduledEditor,
                "SaveButton");
            var scheduledEditorChrome = GetField<Border>(
                scheduledEditor,
                "EditorChrome");
            Assert(scheduledEditor.IsVisible &&
                   ReferenceEquals(scheduledEditor.Owner, todoWindow) &&
                   ReferenceEquals(scheduledEditor.Item, editItem) &&
                   scheduledEditor.Width == 378 &&
                   scheduledEditor.Height == 360 &&
                   scheduledEditor.ResizeMode == ResizeMode.CanResize &&
                   scheduledEditor.FontFamily.Source == "Microsoft YaHei" &&
                   scheduledEditor.Title == "修改定时任务" &&
                   scheduledEditorText.Text == editItem.Text &&
                   scheduledEditorDate.SelectedDate == editLocal.Date &&
                   scheduledEditorHour.SelectedIndex == editLocal.Hour &&
                   scheduledEditorMinute.SelectedIndex == editLocal.Minute &&
                   scheduledEditorSecond.SelectedIndex == editLocal.Second &&
                   scheduledEditorRepeatToggle.IsChecked != true &&
                   scheduledEditorQuietHoursEditor.Visibility ==
                       Visibility.Collapsed &&
                   scheduledEditorQuietHoursToggle.IsChecked != true &&
                   scheduledEditorQuietHoursStart.Text == "22:00:00" &&
                   scheduledEditorQuietHoursEnd.Text == "07:00:00" &&
                   scheduledEditorQuietHoursStart.IsReadOnly &&
                   scheduledEditorQuietHoursEnd.IsReadOnly &&
                   !InputMethod.GetIsInputMethodEnabled(
                       scheduledEditorQuietHoursStart) &&
                   !InputMethod.GetIsInputMethodEnabled(
                       scheduledEditorQuietHoursEnd) &&
                   scheduledEditorQuietHoursStartHost.Cursor == Cursors.Hand &&
                   scheduledEditorQuietHoursEndHost.Cursor == Cursors.Hand &&
                   !scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorHourPicker.Items.Count == 24 &&
                   scheduledEditorMinutePicker.Items.Count == 60 &&
                   scheduledEditorSecondPicker.Items.Count == 60 &&
                   Math.Abs(scheduledEditorDateHost.ActualWidth - 102) <= 0.1 &&
                   Math.Abs(scheduledEditorTimeHost.ActualWidth - 92) <= 0.1 &&
                   Math.Abs(scheduledEditorSaveButton.Height - 38) <= 0.1 &&
                   scheduledEditorSaveButton.FontSize >= 13 &&
                   scheduledEditorRepeatToggle.Template is not null &&
                   scheduledEditorRepeatCount.Template.FindName(
                       "RepeatCountBorder",
                       scheduledEditorRepeatCount) is Border &&
                   scheduledEditorRepeatUnit.Template.FindName(
                        "RepeatUnitButtonBorder",
                        scheduledEditorRepeatUnit) is Border &&
                   scheduledEditorChrome.Margin == new Thickness(0) &&
                   scheduledEditorChrome.Effect is null &&
                   Math.Abs(
                       scheduledEditorChrome.ActualWidth -
                       scheduledEditor.ActualWidth) <= 0.5 &&
                   Math.Abs(
                       scheduledEditorChrome.ActualHeight -
                       scheduledEditor.ActualHeight) <= 0.5 &&
                   GetRawField(todoWindow, "_editingScheduledTask") is null &&
                   scheduledInput.Text.Length == 0 &&
                   Equals(scheduledSubmit.Content, "新增") &&
                   scheduledEditCancel.Visibility == Visibility.Collapsed &&
                   todoWindow.IsTransientPopupOpen,
                $"点击定时任务铅笔必须以378×360 DIP打开紧凑可缩放橘色Owned Window，" +
                $"根圆角边框必须贴满客户区且不得露白；实际 size=" +
                $"{scheduledEditor.Width}×{scheduledEditor.Height}, date=" +
                $"{scheduledEditorDateHost.ActualWidth}, time=" +
                $"{scheduledEditorTimeHost.ActualWidth}, button=" +
                $"{scheduledEditorSaveButton.Height}");
            scheduledEditor.Width = 450;
            scheduledEditor.Height = 500;
            scheduledEditor.UpdateLayout();
            Invoke(scheduledEditor, "PositionBesideOwner");
            Assert(Math.Abs(scheduledEditor.Width - 450) <= 0.1 &&
                   Math.Abs(scheduledEditor.Height - 500) <= 0.1,
                "定时任务修改窗由用户调整大小后，Owner重新定位不得重置回378×360");
            scheduledEditorText.Focus();
            Keyboard.Focus(scheduledEditorText);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var scheduledEditorBodyBorder =
                scheduledEditorText.Template.FindName(
                    "TaskBodyBorder",
                    scheduledEditorText) as Border;
            Assert(scheduledEditorText.IsKeyboardFocused &&
                   scheduledEditorText.FocusVisualStyle is null &&
                   scheduledEditorText.BorderBrush is SolidColorBrush editorBorderBrush &&
                   editorBorderBrush.Color == Color.FromRgb(0xE8, 0x9D, 0x52) &&
                   scheduledEditorText.CaretBrush is SolidColorBrush editorCaretBrush &&
                   editorCaretBrush.Color == Color.FromRgb(0xA8, 0x5E, 0x25) &&
                   scheduledEditorText.SelectionBrush is SolidColorBrush editorSelectionBrush &&
                   editorSelectionBrush.Color == Color.FromRgb(0xFF, 0xD9, 0xA9) &&
                   scheduledEditorBodyBorder?.BorderBrush is SolidColorBrush
                       renderedEditorBorderBrush &&
                   renderedEditorBorderBrush.Color ==
                       Color.FromRgb(0xE8, 0x9D, 0x52),
                "定时任务修改窗口的正文框聚焦态必须由自有模板实际绘制橘色边框、光标和选区，不得泄露 Windows 蓝色强调焦点框");
            var scheduledEditorPresentationSource =
                PresentationSource.FromVisual(scheduledEditor)
                ?? throw new InvalidOperationException(
                    "定时任务修改窗未建立输入源");
            SetField(scheduledEditor, "_isImeComposing", true);
            var scheduledImeEscape = CreateKeyEvent(
                scheduledEditorPresentationSource,
                Key.Escape);
            Invoke(
                scheduledEditor,
                "Window_PreviewKeyDown",
                scheduledEditor,
                scheduledImeEscape);
            Assert(!scheduledImeEscape.Handled &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "定时任务文字使用微软输入法组合候选时，Esc 不能误关修改窗或提交草稿");
            Invoke(
                scheduledEditor,
                "ScheduledTaskEditWindow_Activated",
                scheduledEditor,
                EventArgs.Empty);
            Assert(GetRawField(scheduledEditor, "_isImeComposing") is false,
                "定时任务修改窗重新激活后必须清除输入法失焦时遗留的组合标志");

            Invoke(scheduledEditor, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledEditorTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledEditorTimePickerPopup.PlacementTarget,
                       scheduledEditorTimeHost) &&
                   scheduledEditorTimePickerTitle.Text ==
                       "选择提醒时间" &&
                   scheduledEditor.IsVisible &&
                   GetRawField(
                       scheduledEditor,
                       "_internalPopupOpen") is true &&
                   editRequestedCount == 0,
                "修改定时任务必须真实打开独立的选择提醒时间 Popup，打开过程不得提交或关闭编辑窗");
            foreach (var (picker, selectedIndex) in new[]
                     {
                         (scheduledEditorHourPicker, 11),
                         (scheduledEditorMinutePicker, 22),
                         (scheduledEditorSecondPicker, 33)
                     })
            {
                picker.IsDropDownOpen = true;
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                var option = picker.ItemContainerGenerator
                    .ContainerFromIndex(selectedIndex) as ComboBoxItem
                    ?? throw new InvalidOperationException(
                        $"定时编辑窗时分秒选择器未生成第 {selectedIndex} 个可点击选项");
                var optionClick = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent =
                        UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = option
                };
                option.RaiseEvent(optionClick);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Assert(optionClick.Handled &&
                       picker.SelectedIndex == selectedIndex &&
                       !picker.IsDropDownOpen &&
                       scheduledEditorTimePickerPopup.IsOpen &&
                       scheduledEditor.IsVisible &&
                       GetRawField(
                           scheduledEditor,
                           "_internalPopupOpen") is true &&
                       editRequestedCount == 0,
                    "修改定时任务依次选择时、分、秒时，只能关闭当前下拉层，外层时间 Popup 和编辑窗必须保持");
            }

            Assert(scheduledEditorTimeInput.Text == "11:22:33" &&
                   scheduledEditorHour.SelectedIndex == 11 &&
                   scheduledEditorMinute.SelectedIndex == 22 &&
                   scheduledEditorSecond.SelectedIndex == 33,
                "修改定时任务选择完时分秒后，橙色主框和保存数据控件必须同步为 11:22:33");

            foreach (var repeatControl in new UIElement[]
                     {
                         scheduledEditorRepeatToggle,
                         scheduledEditorRepeatCount,
                         scheduledEditorRepeatUnit
                     })
            {
                var repeatPreviewClick = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = repeatControl
                };
                repeatControl.RaiseEvent(repeatPreviewClick);
                PumpDispatcher(TimeSpan.FromMilliseconds(10));
                Assert(scheduledEditorTimePickerPopup.IsOpen &&
                       scheduledEditor.IsVisible &&
                       editRequestedCount == 0,
                    "修改定时任务真实点击循环勾选、次数或单位时，窗口级 PreviewMouseDown 不得先收起选择提醒时间 Popup");
            }

            scheduledEditorRepeatToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledEditorTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledEditorTimePickerPopup.PlacementTarget,
                       scheduledEditorTimeHost) &&
                   scheduledEditorTimePickerTitle.Text ==
                       "选择提醒时间" &&
                   scheduledEditor.IsVisible &&
                   scheduledEditorRepeatToggle.IsChecked == true &&
                   scheduledEditorQuietHoursEditor.Visibility ==
                       Visibility.Visible &&
                   editRequestedCount == 0,
                "修改定时任务勾选循环时必须显示免打扰行，并保持选择提醒时间 Popup 和编辑窗");
            scheduledEditor.Width = scheduledEditor.MinWidth;
            scheduledEditor.UpdateLayout();
            AssertSingleLineTextIsFullyVisible(
                scheduledEditorQuietHoursOvernightHint,
                scheduledEditorQuietHoursEditor,
                "最小宽度修改窗的免打扰行尾部提示");
            scheduledEditor.Width = 450;
            scheduledEditor.UpdateLayout();
            scheduledEditorQuietHoursToggle.ApplyTemplate();
            scheduledEditorQuietHoursToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            var scheduledEditorQuietHoursShell =
                scheduledEditorQuietHoursToggle.Template.FindName(
                    "CheckBoxShell",
                    scheduledEditorQuietHoursToggle) as Border;
            Assert(scheduledEditorTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledEditorTimePickerPopup.PlacementTarget,
                       scheduledEditorTimeHost) &&
                   scheduledEditorTimePickerTitle.Text ==
                       "选择提醒时间" &&
                   scheduledEditorQuietHoursStart.IsEnabled &&
                   scheduledEditorQuietHoursEnd.IsEnabled &&
                   scheduledEditorQuietHoursShell?.Background is
                       SolidColorBrush editorQuietCheckedBackground &&
                   editorQuietCheckedBackground.Color ==
                       Color.FromRgb(0xF2, 0xA0, 0x52) &&
                   scheduledEditorQuietHoursShell.BorderBrush is
                       SolidColorBrush editorQuietCheckedBorder &&
                   editorQuietCheckedBorder.Color ==
                       Color.FromRgb(0xD9, 0x84, 0x35),
                "修改窗操作萌橘色免打扰开关和时间框时不得收起外层提醒时间选择窗");
            scheduledEditorRepeatCount.Text = "3";
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditor.IsVisible &&
                   scheduledEditorRepeatCount.Text == "3" &&
                   editRequestedCount == 0,
                "修改定时任务在时间 Popup 打开时修改循环次数，不得触发自动保存或收起窗口");
            scheduledEditorRepeatUnit.IsDropDownOpen = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledEditorRepeatUnit.IsDropDownOpen &&
                   scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditor.IsVisible,
                "修改定时任务打开循环单位下拉时，外层时间 Popup 必须保持");
            scheduledEditorRepeatUnit.SelectedIndex =
                (int)ScheduledRepeatUnit.Day;
            scheduledEditorRepeatUnit.IsDropDownOpen = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledEditorRepeatUnit.SelectedIndex ==
                       (int)ScheduledRepeatUnit.Day &&
                   !scheduledEditorRepeatUnit.IsDropDownOpen &&
                   scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditor.IsVisible &&
                   GetRawField(
                       scheduledEditor,
                       "_internalPopupOpen") is true &&
                   editRequestedCount == 0,
                "修改定时任务选择循环单位后只能关闭单位下拉，选择提醒时间 Popup 和编辑窗必须继续显示");

            void OpenQuietHoursPicker(
                Border host,
                int expectedHour,
                int expectedMinute,
                int expectedSecond,
                string pickerName)
            {
                var hostClick = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent =
                        UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = host
                };
                host.RaiseEvent(hostClick);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Assert(hostClick.Handled &&
                       scheduledEditorTimePickerPopup.IsOpen &&
                       ReferenceEquals(
                           scheduledEditorTimePickerPopup.PlacementTarget,
                           host) &&
                       scheduledEditorTimePickerTitle.Text ==
                           $"选择{pickerName}" &&
                       scheduledEditorHourPicker.SelectedIndex ==
                           expectedHour &&
                       scheduledEditorMinutePicker.SelectedIndex ==
                           expectedMinute &&
                       scheduledEditorSecondPicker.SelectedIndex ==
                           expectedSecond &&
                       scheduledEditor.IsVisible &&
                       editRequestedCount == 0,
                    $"点击{pickerName}必须让同一个时分秒选择器切换目标、标题和当前值，不得创建第二层 Popup");
            }

            void SelectQuietHoursPart(
                ComboBox picker,
                int selectedIndex,
                string pickerName)
            {
                picker.IsDropDownOpen = true;
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                var option = picker.ItemContainerGenerator
                    .ContainerFromIndex(selectedIndex) as ComboBoxItem
                    ?? throw new InvalidOperationException(
                        $"{pickerName}未生成第 {selectedIndex} 个可点击选项");
                var optionClick = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent =
                        UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = option
                };
                option.RaiseEvent(optionClick);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Assert(optionClick.Handled &&
                       picker.SelectedIndex == selectedIndex &&
                       !picker.IsDropDownOpen &&
                       scheduledEditorTimePickerPopup.IsOpen &&
                       scheduledEditor.IsVisible &&
                       editRequestedCount == 0,
                    $"{pickerName}选择一列后只能关闭当前下拉，共用时分秒 Popup 和修改窗必须保持");
            }

            OpenQuietHoursPicker(
                scheduledEditorQuietHoursStartHost,
                22,
                0,
                0,
                "免打扰开始时间");
            scheduledEditorQuietHoursToggle.IsChecked = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(!scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorQuietHoursToggle.IsChecked != true &&
                   !scheduledEditorQuietHoursStart.IsEnabled &&
                   !scheduledEditorQuietHoursEnd.IsEnabled &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "共用 Popup 定位免打扰开始时间时，关闭免打扰必须只收起 Popup 并保留修改窗口");
            scheduledEditorQuietHoursToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledEditorQuietHoursStart.IsEnabled &&
                   scheduledEditorQuietHoursEnd.IsEnabled,
                "重新开启免打扰后开始和结束时间必须恢复可选");

            OpenQuietHoursPicker(
                scheduledEditorQuietHoursEndHost,
                7,
                0,
                0,
                "免打扰结束时间");
            scheduledEditorRepeatToggle.IsChecked = false;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(!scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorRepeatToggle.IsChecked != true &&
                   scheduledEditorQuietHoursEditor.Visibility ==
                       Visibility.Collapsed &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "共用 Popup 定位免打扰结束时间时，关闭循环必须收起 Popup 和免打扰行，但不能关闭或提交修改窗口");
            scheduledEditorRepeatToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledEditorQuietHoursEditor.Visibility ==
                       Visibility.Visible &&
                   scheduledEditorQuietHoursToggle.IsChecked == true &&
                   scheduledEditorQuietHoursStart.IsEnabled &&
                   scheduledEditorQuietHoursEnd.IsEnabled,
                "重新开启循环后必须恢复原免打扰开关和可选时间，不得丢失草稿状态");

            OpenQuietHoursPicker(
                scheduledEditorQuietHoursStartHost,
                22,
                0,
                0,
                "免打扰开始时间");
            SelectQuietHoursPart(
                scheduledEditorHourPicker,
                20,
                "免打扰开始小时");
            SelectQuietHoursPart(
                scheduledEditorMinutePicker,
                20,
                "免打扰开始分钟");
            SelectQuietHoursPart(
                scheduledEditorSecondPicker,
                21,
                "免打扰开始秒");
            var quietHoursConfirmButton = new Button();
            Invoke(
                scheduledEditor,
                "ScheduledTimePickerConfirmButton_Click",
                quietHoursConfirmButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    quietHoursConfirmButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(!scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorQuietHoursStart.Text == "20:20:21" &&
                   scheduledEditorQuietHoursEnd.Text == "07:00:00" &&
                   scheduledEditorTimeInput.Text == "11:22:33" &&
                   scheduledEditorHour.SelectedIndex == 11 &&
                   scheduledEditorMinute.SelectedIndex == 22 &&
                   scheduledEditorSecond.SelectedIndex == 33 &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "确定免打扰开始时间只能收起选择器，并精确保留20:20:21草稿；" +
                "不能串改上方提醒时间");

            OpenQuietHoursPicker(
                scheduledEditorQuietHoursEndHost,
                7,
                0,
                0,
                "免打扰结束时间");
            scheduledEditorHourPicker.IsDropDownOpen = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var quietHoursInputSource =
                PresentationSource.FromVisual(
                    scheduledEditorHourPicker)
                ?? throw new InvalidOperationException(
                    "免打扰时分秒选择器没有建立输入源");
            var quietHoursEscape = CreateKeyEvent(
                quietHoursInputSource,
                Key.Escape);
            Invoke(
                scheduledEditor,
                "Window_PreviewKeyDown",
                scheduledEditor,
                quietHoursEscape);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(quietHoursEscape.Handled &&
                   !scheduledEditorTimePickerPopup.IsOpen &&
                   !scheduledEditorHourPicker.IsDropDownOpen &&
                   !scheduledEditorMinutePicker.IsDropDownOpen &&
                   !scheduledEditorSecondPicker.IsDropDownOpen &&
                   scheduledEditor.IsVisible &&
                   scheduledEditorQuietHoursStart.Text == "20:20:21" &&
                   scheduledEditorQuietHoursEnd.Text == "07:00:00" &&
                   editRequestedCount == 0,
                "免打扰时分秒选择器按Esc必须只关闭Popup和三列下拉，不能保存、取消或关闭修改窗");

            OpenQuietHoursPicker(
                scheduledEditorQuietHoursEndHost,
                7,
                0,
                0,
                "免打扰结束时间");
            SelectQuietHoursPart(
                scheduledEditorHourPicker,
                20,
                "免打扰结束小时同值校验");
            SelectQuietHoursPart(
                scheduledEditorMinutePicker,
                20,
                "免打扰结束分钟同值校验");
            SelectQuietHoursPart(
                scheduledEditorSecondPicker,
                21,
                "免打扰结束秒同值校验");
            Invoke(
                scheduledEditor,
                "ScheduledTimePickerConfirmButton_Click",
                quietHoursConfirmButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    quietHoursConfirmButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(!scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorQuietHoursStart.Text == "20:20:21" &&
                   scheduledEditorQuietHoursEnd.Text == "20:20:21" &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "独立修改窗必须允许先形成待校验的同值草稿，但不能提前提交");

            var sameQuietHoursSaveClick = new RoutedEventArgs(
                ButtonBase.ClickEvent,
                scheduledEditorSaveButton);
            Invoke(
                scheduledEditor,
                "SaveButton_Click",
                scheduledEditorSaveButton,
                sameQuietHoursSaveClick);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(sameQuietHoursSaveClick.Handled &&
                   scheduledEditorTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledEditorTimePickerPopup.PlacementTarget,
                       scheduledEditorQuietHoursEndHost) &&
                   scheduledEditorTimePickerTitle.Text ==
                       "选择免打扰结束时间" &&
                   scheduledEditorHourPicker.SelectedIndex == 20 &&
                   scheduledEditorMinutePicker.SelectedIndex == 20 &&
                   scheduledEditorSecondPicker.SelectedIndex == 21 &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "免打扰开始和结束同值时确定修改必须拦截提交，并让共用 Popup 精确定位结束时间供修正");

            OpenQuietHoursPicker(
                scheduledEditorQuietHoursEndHost,
                20,
                20,
                21,
                "免打扰结束时间");
            SelectQuietHoursPart(
                scheduledEditorHourPicker,
                6,
                "免打扰结束小时");
            SelectQuietHoursPart(
                scheduledEditorMinutePicker,
                6,
                "免打扰结束分钟");
            SelectQuietHoursPart(
                scheduledEditorSecondPicker,
                7,
                "免打扰结束秒");
            Invoke(
                scheduledEditor,
                "ScheduledTimePickerConfirmButton_Click",
                quietHoursConfirmButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    quietHoursConfirmButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(!scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorQuietHoursStart.Text == "20:20:21" &&
                   scheduledEditorQuietHoursEnd.Text == "06:06:07" &&
                   scheduledEditorTimeInput.Text == "11:22:33" &&
                   scheduledEditorHour.SelectedIndex == 11 &&
                   scheduledEditorMinute.SelectedIndex == 22 &&
                   scheduledEditorSecond.SelectedIndex == 33 &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "确定免打扰结束时间必须形成20:20:21至次日06:06:07的跨夜草稿，" +
                "且不能串改上方提醒时间");

            Invoke(scheduledEditor, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledEditorTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledEditorTimePickerPopup.PlacementTarget,
                       scheduledEditorTimeHost) &&
                   scheduledEditorTimePickerTitle.Text ==
                       "选择提醒时间" &&
                   scheduledEditorHourPicker.SelectedIndex == 11 &&
                   scheduledEditorMinutePicker.SelectedIndex == 22 &&
                   scheduledEditorSecondPicker.SelectedIndex == 33,
                "从免打扰切回提醒时间时必须复用同一个 Popup，并恢复提醒时间目标、标题和 11:22:33");

            scheduledEditorText.Text = "提醒期间必须保留的定时任务草稿";
            Invoke(todoWindow, "BeginReminderInterruption");
            Invoke(
                scheduledEditor,
                "ScheduledTaskEditWindow_Deactivated",
                scheduledEditor,
                EventArgs.Empty);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(ReferenceEquals(
                       GetRawField(todoWindow, "_editorInterruptedByReminder"),
                       scheduledEditor) &&
                   GetField<bool>(
                       scheduledEditor,
                       "_reminderInterruptionActive") &&
                   scheduledEditor.IsVisible &&
                   scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorText.Text ==
                       "提醒期间必须保留的定时任务草稿" &&
                   Math.Abs(scheduledEditor.Width - 450) <= 0.1 &&
                   Math.Abs(scheduledEditor.Height - 500) <= 0.1,
                "提醒抢走焦点时必须保留定时任务修改窗口、未保存草稿、用户尺寸和内部时间选择层");
            Invoke(todoWindow, "EndReminderInterruption", true);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            Assert(GetRawField(todoWindow, "_editorInterruptedByReminder") is null &&
                   !GetField<bool>(
                       scheduledEditor,
                       "_reminderInterruptionActive") &&
                   scheduledEditor.IsVisible &&
                   scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditorText.Text ==
                       "提醒期间必须保留的定时任务草稿" &&
                   scheduledEditor.IsKeyboardFocusWithin,
                "关闭提醒后必须回到原定时任务修改窗口，不能关闭选择层、清空草稿或留下中断标记");

            var scheduledEditorTimeSource =
                PresentationSource.FromVisual(scheduledEditorHourPicker)
                ?? throw new InvalidOperationException(
                    "定时任务编辑时间浮层没有建立输入源");
            var scheduledEditorTimeEscape = CreateKeyEvent(
                scheduledEditorTimeSource,
                Key.Escape);
            Invoke(
                scheduledEditor,
                "ScheduledTimePickerPopup_PreviewKeyDown",
                scheduledEditorHourPicker,
                scheduledEditorTimeEscape);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledEditorTimeEscape.Handled &&
                   !scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "修改定时任务时间浮层按 Esc 必须只关闭 Popup，不能取消、保存或关闭编辑窗");

            Invoke(scheduledEditor, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditor.IsVisible,
                "验证确定按钮前必须重新打开修改定时任务时间 Popup");
            var scheduledEditorTimeConfirmButton = new Button();
            Invoke(
                scheduledEditor,
                "ScheduledTimePickerConfirmButton_Click",
                scheduledEditorTimeConfirmButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    scheduledEditorTimeConfirmButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(!scheduledEditorTimePickerPopup.IsOpen &&
                   scheduledEditor.IsVisible &&
                   scheduledEditorTimeInput.Text == "11:22:33" &&
                   editRequestedCount == 0,
                "修改定时任务点击时间 Popup 的确定只能收起 Popup，并保留已选时分秒和编辑窗");

            todoWindow.Activate();
            todoTab.Focus();
            Keyboard.Focus(todoTab);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            Assert(scheduledEditor.IsVisible &&
                   editRequestedCount == 0,
                "定时任务修改窗失去焦点后必须继续显示且不保存，只有确定修改才能提交");
            scheduledEditor.Activate();
            scheduledEditorText.Focus();
            Keyboard.Focus(scheduledEditorText);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));

            var scheduledEditorSource = File.ReadAllText(
                FindWorkspaceFile("ScheduledTaskEditWindow.xaml.cs"));
            var scheduledEditorXaml = File.ReadAllText(
                FindWorkspaceFile("ScheduledTaskEditWindow.xaml"));
            var chromeAppearanceSource = File.ReadAllText(
                FindWorkspaceFile("WindowChromeAppearance.cs"));
            var scheduledEditorDeactivatedSource =
                ExtractPrivateMethodSource(
                    scheduledEditorSource,
                    "ScheduledTaskEditWindow_Deactivated");
            var scheduledEditorClosePickersSource =
                ExtractPrivateMethodSource(
                    scheduledEditorSource,
                    "ClosePickersAfterDeactivation");
            Assert(!scheduledEditorSource.Contains(
                       "CommitAfterDeactivation",
                       StringComparison.Ordinal) &&
                   !scheduledEditorSource.Contains(
                       "QueueCommitAfterDeactivation",
                       StringComparison.Ordinal) &&
                   !scheduledEditorSource.Contains(
                       "public bool SaveAndClose",
                       StringComparison.Ordinal) &&
                   scheduledEditorSource.Contains(
                       "private void SaveButton_Click",
                       StringComparison.Ordinal) &&
                   scheduledEditorSource.Contains(
                       "Deactivated += ScheduledTaskEditWindow_Deactivated",
                       StringComparison.Ordinal) &&
                   scheduledEditorDeactivatedSource.Contains(
                       "_closePickersAfterDeactivationAction",
                       StringComparison.Ordinal) &&
                    scheduledEditorClosePickersSource.Contains(
                        "CloseScheduledPickers()",
                        StringComparison.Ordinal) &&
                    !scheduledEditorClosePickersSource.Contains(
                        "CommitAndClose",
                        StringComparison.Ordinal) &&
                    scheduledEditorSource.Contains(
                        "TargetEditorHeight = 360",
                        StringComparison.Ordinal) &&
                    scheduledEditorSource.Contains(
                        "SourceInitialized += ScheduledTaskEditWindow_SourceInitialized",
                        StringComparison.Ordinal) &&
                    scheduledEditorSource.Contains(
                        "WindowChromeAppearance.TryHideSystemBorder(this)",
                        StringComparison.Ordinal) &&
                    scheduledEditorSource.Contains(
                        "OpenScheduledTimePickerForTarget",
                        StringComparison.Ordinal) &&
                    scheduledEditorSource.Contains(
                        "ScheduledTimePickerTarget.QuietStart",
                        StringComparison.Ordinal) &&
                    scheduledEditorSource.Contains(
                        "ScheduledTimePickerTarget.QuietEnd",
                        StringComparison.Ordinal) &&
                    !scheduledEditorSource.Contains(
                        "OpenQuietHoursTimePicker",
                        StringComparison.Ordinal) &&
                    scheduledEditorXaml.Contains(
                        "<Border x:Name=\"EditorChrome\"",
                        StringComparison.Ordinal) &&
                    scheduledEditorXaml.Contains(
                        "Margin=\"0\"",
                        StringComparison.Ordinal) &&
                    scheduledEditorXaml.Contains(
                        "x:Name=\"ScheduledTimePickerTitle\"",
                        StringComparison.Ordinal) &&
                    !scheduledEditorXaml.Contains(
                        "QuietHoursTimePickerPopup",
                        StringComparison.Ordinal) &&
                    !scheduledEditorXaml.Contains(
                        "QuietHoursHourComboBox",
                        StringComparison.Ordinal) &&
                    !scheduledEditorXaml.Contains(
                        "QuietHoursTimeInput_PreviewTextInput",
                        StringComparison.Ordinal) &&
                    chromeAppearanceSource.Contains(
                        "DwmWindowAttributeBorderColor = 34",
                        StringComparison.Ordinal) &&
                    chromeAppearanceSource.Contains(
                        "DwmColorNone = 0xFFFFFFFE",
                        StringComparison.Ordinal) &&
                    chromeAppearanceSource.Contains(
                        "DwmSetWindowAttribute(",
                        StringComparison.Ordinal),
                "定时编辑窗失活只能清理picker、默认高度必须为360；" +
                "提醒和免打扰必须复用唯一时分秒 Popup 且禁止手输；" +
                "根圆角边框贴边并在SourceInitialized通过DWM关闭系统白边");

            var externalSavedLocal = DateTime.Now.AddDays(3);
            externalSavedLocal = new DateTime(
                externalSavedLocal.Year,
                externalSavedLocal.Month,
                externalSavedLocal.Day,
                17,
                26,
                39,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(externalSavedLocal))
            {
                externalSavedLocal = externalSavedLocal.AddHours(1);
            }

            scheduledEditorText.Text = "  独立窗口修改完成  ";
            scheduledEditorDate.SelectedDate = externalSavedLocal.Date;
            scheduledEditorHour.SelectedIndex = externalSavedLocal.Hour;
            scheduledEditorMinute.SelectedIndex = externalSavedLocal.Minute;
            scheduledEditorSecond.SelectedIndex = externalSavedLocal.Second;
            scheduledEditorRepeatToggle.IsChecked = true;
            scheduledEditorRepeatCount.Text = "2";
            scheduledEditorRepeatUnit.SelectedIndex =
                (int)ScheduledRepeatUnit.Day;
            var scheduledEditorSaveClick = new RoutedEventArgs(
                ButtonBase.ClickEvent,
                scheduledEditorSaveButton);
            Invoke(
                scheduledEditor,
                "SaveButton_Click",
                scheduledEditorSaveButton,
                scheduledEditorSaveClick);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var expectedExternalDueAt = new DateTimeOffset(
                externalSavedLocal,
                TimeZoneInfo.Local.GetUtcOffset(externalSavedLocal));
            Assert(editRequestedCount == 1 &&
                   ReferenceEquals(requestedEditItem, editItem) &&
                   requestedEditText == "独立窗口修改完成" &&
                   requestedEditDueAt == expectedExternalDueAt &&
                   requestedEditRepeatInterval == TimeSpan.FromDays(2) &&
                   requestedEditRepeatRule is
                   {
                       Unit: ScheduledRepeatUnit.Day,
                       Every: 2,
                       NextOrdinal: 0
                   } &&
                   requestedEditQuietHours is
                   {
                       Start.Hours: 20,
                       Start.Minutes: 20,
                       Start.Seconds: 21,
                       End.Hours: 6,
                       End.Minutes: 6,
                       End.Seconds: 7
                   } &&
                   GetRawField(todoWindow, "_scheduledTaskEditWindow") is null &&
                   !todoWindow.IsTransientPopupOpen &&
                   scheduledEditorSaveClick.Handled,
                "独立编辑窗提交必须传回同一个 ScheduledTaskItem，并精确保留秒级时间、循环调度和免打扰数据");

            // A reminder can already be visible before the user opens an
            // editor. The newly opened editor must still join that reminder
            // interruption session so its picker and draft survive.
            Invoke(todoWindow, "BeginReminderInterruption");
            Invoke(todoWindow, "OpenScheduledTaskEditor", editItem);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var lateReminderEditor =
                GetField<ScheduledTaskEditWindow>(
                    todoWindow,
                    "_scheduledTaskEditWindow");
            var lateReminderEditorText =
                GetField<TextBox>(lateReminderEditor, "TaskTextBox");
            var lateReminderEditorPopup =
                GetField<Popup>(
                    lateReminderEditor,
                    "ScheduledTimePickerPopup");
            lateReminderEditorText.Text =
                "draft opened after reminder became active";
            Invoke(lateReminderEditor, "OpenScheduledTimePicker");
            Invoke(
                lateReminderEditor,
                "ScheduledTaskEditWindow_Deactivated",
                lateReminderEditor,
                EventArgs.Empty);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(GetField<bool>(
                       todoWindow,
                       "_isReminderInterruptionActive") &&
                   ReferenceEquals(
                       GetRawField(
                           todoWindow,
                           "_editorInterruptedByReminder"),
                       lateReminderEditor) &&
                   GetField<bool>(
                       lateReminderEditor,
                       "_reminderInterruptionActive") &&
                   lateReminderEditorPopup.IsOpen &&
                   lateReminderEditorText.Text ==
                       "draft opened after reminder became active",
                "Reminder模式下后来打开的定时任务编辑器必须立即加入中断会话，并保留草稿与时间选择Popup");
            Invoke(todoWindow, "EndReminderInterruption", true);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            Assert(!GetField<bool>(
                       todoWindow,
                       "_isReminderInterruptionActive") &&
                   GetRawField(
                       todoWindow,
                       "_editorInterruptedByReminder") is null &&
                   !GetField<bool>(
                       lateReminderEditor,
                       "_reminderInterruptionActive") &&
                   lateReminderEditorPopup.IsOpen &&
                   lateReminderEditorText.Text ==
                       "draft opened after reminder became active" &&
                   lateReminderEditor.IsKeyboardFocusWithin,
                "关闭提醒后必须恢复后来打开的定时任务编辑器焦点，且不得关闭Popup或丢弃草稿");
            lateReminderEditor.CloseWithoutSaving();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));

            // If editor A closes while the reminder is still visible, editor B
            // must replace it as the session's restoration target.
            Invoke(todoWindow, "BeginReminderInterruption");
            Invoke(todoWindow, "OpenScheduledTaskEditor", editItem);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var reminderEditorA =
                GetField<ScheduledTaskEditWindow>(
                    todoWindow,
                    "_scheduledTaskEditWindow");
            Invoke(reminderEditorA, "OpenScheduledTimePicker");
            reminderEditorA.CloseWithoutSaving();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(GetRawField(
                       todoWindow,
                       "_editorInterruptedByReminder") is null &&
                   GetField<bool>(
                       todoWindow,
                       "_isReminderInterruptionActive") &&
                   !GetField<bool>(
                       reminderEditorA,
                       "_reminderInterruptionActive") &&
                   GetRawField(
                       todoWindow,
                       "_scheduledTaskEditWindow") is null,
                "提醒仍显示时关闭修改窗口，必须立即释放旧窗口引用和中断标记，且提醒会话继续有效");
            var replacementReminderItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "replacement reminder editor",
                DueAt = DateTimeOffset.Now.AddHours(8),
                CreatedAt = DateTimeOffset.Now
            };
            Invoke(
                todoWindow,
                "OpenScheduledTaskEditor",
                replacementReminderItem);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var reminderEditorB =
                GetField<ScheduledTaskEditWindow>(
                    todoWindow,
                    "_scheduledTaskEditWindow");
            var reminderEditorBText =
                GetField<TextBox>(reminderEditorB, "TaskTextBox");
            var reminderEditorBPopup =
                GetField<Popup>(
                    reminderEditorB,
                    "ScheduledTimePickerPopup");
            reminderEditorBText.Text = "replacement draft must survive";
            Invoke(reminderEditorB, "OpenScheduledTimePicker");
            Invoke(
                reminderEditorB,
                "ScheduledTaskEditWindow_Deactivated",
                reminderEditorB,
                EventArgs.Empty);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(!reminderEditorA.IsVisible &&
                   ReferenceEquals(
                       GetRawField(
                           todoWindow,
                           "_editorInterruptedByReminder"),
                       reminderEditorB) &&
                   GetField<bool>(
                       reminderEditorB,
                       "_reminderInterruptionActive") &&
                   reminderEditorBPopup.IsOpen &&
                   reminderEditorBText.Text ==
                       "replacement draft must survive",
                "提醒期间A关闭并打开B后，中断会话必须改为跟踪B并保护B的草稿与Popup");
            Invoke(todoWindow, "EndReminderInterruption", true);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            Assert(GetRawField(
                       todoWindow,
                       "_editorInterruptedByReminder") is null &&
                   !GetField<bool>(
                       reminderEditorB,
                       "_reminderInterruptionActive") &&
                   reminderEditorB.IsVisible &&
                   reminderEditorBPopup.IsOpen &&
                   reminderEditorBText.Text ==
                       "replacement draft must survive" &&
                   reminderEditorB.IsKeyboardFocusWithin,
                "关闭提醒后只能恢复替换后的编辑器B，B的草稿、Popup与焦点必须全部保留");
            reminderEditorB.CloseWithoutSaving();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));

            editRequestedCount = 0;
            requestedEditItem = null;
            requestedEditText = null;
            requestedEditDueAt = default;
            requestedEditRepeatInterval = null;
            requestedEditRepeatRule = null;
            Invoke(
                todoWindow,
                "BeginScheduledTaskFormEdit",
                editItem);
            Assert(ReferenceEquals(
                       GetRawField(todoWindow, "_editingScheduledTask"),
                       editItem) &&
                   scheduledInput.Text == editItem.Text &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime editingDate &&
                   editingDate == editLocal.Date &&
                   scheduledDateInput.Text == editLocal.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == editLocal.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture) &&
                   scheduledRepeatToggle.IsChecked != true &&
                   Equals(scheduledSubmit.Content, "确定修改") &&
                   scheduledEditCancel.Visibility == Visibility.Visible,
                "兼容编辑草稿入口仍须完整回填原任务，避免旧状态恢复路径丢失调度数据");
            AssertScheduledFormLayout("编辑态");

            var savedLocal = DateTime.Now.AddHours(6);
            savedLocal = new DateTime(
                savedLocal.Year,
                savedLocal.Month,
                savedLocal.Day,
                savedLocal.Hour,
                savedLocal.Minute,
                savedLocal.Second,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(savedLocal))
            {
                savedLocal = savedLocal.AddHours(1);
            }

            scheduledInput.Text = "  修改后的定时任务  ";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                savedLocal.Date,
                true);
            scheduledTime.Text = savedLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            scheduledRepeatToggle.IsChecked = true;
            scheduledRepeatCount.Text = "2";
            scheduledRepeatUnit.SelectedIndex =
                (int)ScheduledRepeatUnit.Day;
            var scheduledInputSource = PresentationSource.FromVisual(scheduledInput)
                ?? throw new InvalidOperationException("定时任务输入框未建立输入源");
            Invoke(todoWindow, "SetImeComposing", true);
            var composingScheduledEnter = CreateKeyEvent(
                scheduledInputSource,
                Key.Enter);
            Invoke(
                todoWindow,
                "ScheduledTaskInput_PreviewKeyDown",
                scheduledInput,
                composingScheduledEnter);
            Assert(editRequestedCount == 0 &&
                   ReferenceEquals(
                       GetRawField(todoWindow, "_editingScheduledTask"),
                       editItem),
                "微软输入法仍在组合时，定时任务编辑 Enter 只能选词，不得提前保存或退出编辑");

            Invoke(todoWindow, "SetImeComposing", false);
            var committedScheduledEnter = CreateKeyEvent(
                scheduledInputSource,
                Key.Enter);
            Invoke(
                todoWindow,
                "ScheduledTaskInput_PreviewKeyDown",
                scheduledInput,
                committedScheduledEnter);
            var expectedEditedDueAt = new DateTimeOffset(
                savedLocal,
                TimeZoneInfo.Local.GetUtcOffset(savedLocal));
            Assert(editRequestedCount == 1 &&
                   ReferenceEquals(requestedEditItem, editItem) &&
                   requestedEditText == "修改后的定时任务" &&
                   requestedEditDueAt == expectedEditedDueAt &&
                   requestedEditRepeatInterval == TimeSpan.FromDays(2) &&
                   requestedEditRepeatRule is
                   {
                       Unit: ScheduledRepeatUnit.Day,
                       Every: 2,
                       NextOrdinal: 0
                   } &&
                   committedScheduledEnter.Handled &&
                   GetRawField(todoWindow, "_editingScheduledTask") is null &&
                   Equals(scheduledSubmit.Content, "新增") &&
                   scheduledEditCancel.Visibility == Visibility.Collapsed,
                "同一次定时修改必须只提交一次，同时保存内容、日期、HH:mm:ss和循环规则，再恢复新增状态");

            Invoke(
                todoWindow,
                "BeginScheduledTaskFormEdit",
                editItem);
            scheduledInput.Text = "这一版应该被取消";
            Invoke(
                todoWindow,
                "ScheduledTaskEditCancelButton_Click",
                scheduledEditCancel,
                new RoutedEventArgs(ButtonBase.ClickEvent, scheduledEditCancel));
            Assert(editRequestedCount == 1 &&
                   editItem.Text == "要修改的定时任务" &&
                   editItem.DueAt == new DateTimeOffset(
                       editLocal,
                       TimeZoneInfo.Local.GetUtcOffset(editLocal)) &&
                   scheduledInput.Text.Length == 0 &&
                   GetRawField(todoWindow, "_editingScheduledTask") is null &&
                   Equals(scheduledSubmit.Content, "新增") &&
                   scheduledEditCancel.Visibility == Visibility.Collapsed,
                "取消修改不得触发保存或改动原任务，并必须清空草稿、恢复新增状态");
            AssertScheduledFormLayout("取消编辑后新增态");

            Invoke(
                todoWindow,
                "BeginScheduledTaskFormEdit",
                editItem);
            var pastLocal = DateTime.Now.AddMinutes(-5);
            pastLocal = new DateTime(
                pastLocal.Year,
                pastLocal.Month,
                pastLocal.Day,
                pastLocal.Hour,
                pastLocal.Minute,
                pastLocal.Second,
             DateTimeKind.Unspecified);
            scheduledInput.Text = "不能保存到过去";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                pastLocal.Date,
                true);
            scheduledTime.Text = pastLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(editRequestedCount == 1 &&
                   ReferenceEquals(
                       GetRawField(todoWindow, "_editingScheduledTask"),
                       editItem) &&
                   validationText.Text.Contains("晚于现在", StringComparison.Ordinal) &&
                   editItem.Text == "要修改的定时任务",
                "编辑到过去时间必须保留编辑态、显示校验提示并且不得改变原任务");
            AssertScheduledFormLayout("编辑校验提示态");

            todoWindow.ShowDefaultTab();
            Assert(GetRawField(todoWindow, "_editingScheduledTask") is null &&
                   todoTab.IsChecked == true &&
                   scheduledInput.Text.Length == 0 &&
                   editRequestedCount == 1,
                "切回默认待办页必须取消未提交的定时任务修改，不能静默保存草稿");

            var recurringEditDueAt = DateTimeOffset.Now.AddDays(5);
            recurringEditDueAt = recurringEditDueAt.AddTicks(
                -(recurringEditDueAt.Ticks % TimeSpan.TicksPerSecond));
            var recurringEditLocal = recurringEditDueAt.LocalDateTime;
            var recurringEditInterval =
                TimeSpan.FromDays(1) +
                TimeSpan.FromHours(2) +
                TimeSpan.FromMinutes(30);
            var recurringEditQuietHours = new ScheduledQuietHours
            {
                Start = new TimeSpan(22, 30, 15),
                End = new TimeSpan(7, 5, 45),
                TimeZoneId = TimeZoneInfo.Local.Id
            };
            var recurringEditItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "要修改的循环任务",
                DueAt = recurringEditDueAt,
                CreatedAt = DateTimeOffset.Now.AddMinutes(-3),
                RepeatInterval = recurringEditInterval,
                QuietHours = recurringEditQuietHours
            };
            scheduledTasks.Add(recurringEditItem);
            Invoke(todoWindow, "SelectTaskPage", true, false);
            var recurringEditButton = new Button { Tag = recurringEditItem };
            Invoke(
                todoWindow,
                "BeginScheduledTaskFormEdit",
                recurringEditItem);
            Assert(ReferenceEquals(
                       GetRawField(todoWindow, "_editingScheduledTask"),
                       recurringEditItem) &&
                   scheduledInput.Text == recurringEditItem.Text &&
                   scheduledRepeatToggle.IsChecked == true &&
                   scheduledQuietHoursEditor.Visibility ==
                       Visibility.Visible &&
                   scheduledQuietHoursToggle.IsChecked == true &&
                   scheduledQuietHoursStart.Text == "22:30:15" &&
                   scheduledQuietHoursEnd.Text == "07:05:45" &&
                   scheduledRepeatCount.Text == "1590" &&
                   scheduledRepeatUnit.SelectedIndex ==
                       (int)ScheduledRepeatUnit.Minute &&
                   scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledDateInput.Text == recurringEditLocal.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == recurringEditLocal.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture),
                 "点击循环任务铅笔必须回填循环模式、天时分和可见的下一次日期时间");
            RaisePreviewMouseDown(scheduledQuietHoursStart);
            Assert(scheduledTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledTimePickerPopup.PlacementTarget,
                       scheduledQuietHoursStart) &&
                   scheduledHourPicker.SelectedIndex == 22 &&
                   scheduledMinutePicker.SelectedIndex == 30 &&
                   scheduledSecondPicker.SelectedIndex == 15,
                "内联修改已有循环任务时，打开免打扰开始选择器必须回显原来的22:30:15");
            Invoke(todoWindow, "CloseScheduledTimePicker");
            RaisePreviewMouseDown(scheduledQuietHoursEnd);
            Assert(scheduledTimePickerPopup.IsOpen &&
                   ReferenceEquals(
                       scheduledTimePickerPopup.PlacementTarget,
                       scheduledQuietHoursEnd) &&
                   scheduledHourPicker.SelectedIndex == 7 &&
                   scheduledMinutePicker.SelectedIndex == 5 &&
                   scheduledSecondPicker.SelectedIndex == 45,
                "内联修改已有循环任务时，打开免打扰结束选择器必须回显原来的07:05:45");
            Invoke(todoWindow, "CloseScheduledTimePicker");

            scheduledInput.Text = "  循环任务只改文案  ";
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(editRequestedCount == 2 &&
                   ReferenceEquals(
                       requestedEditItem,
                       recurringEditItem) &&
                   requestedEditText == "循环任务只改文案" &&
                   requestedEditDueAt == recurringEditDueAt &&
                   requestedEditRepeatInterval == recurringEditInterval &&
                   requestedEditQuietHours == recurringEditQuietHours,
                "循环任务只修改文案时必须保留原下一次到期时间、周期和免打扰时段，重启后不能重新计时");

            Invoke(
                todoWindow,
                "BeginScheduledTaskFormEdit",
                recurringEditItem);
            var changedRecurringInterval =
                TimeSpan.FromHours(4);
            scheduledInput.Text = "循环任务修改周期";
            scheduledRepeatCount.Text = "4";
            scheduledRepeatUnit.SelectedIndex =
                (int)ScheduledRepeatUnit.Hour;
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(editRequestedCount == 3 &&
                   ReferenceEquals(
                       requestedEditItem,
                       recurringEditItem) &&
                   requestedEditText == "循环任务修改周期" &&
                   requestedEditRepeatInterval == changedRecurringInterval &&
                   requestedEditRepeatRule is
                   {
                       Unit: ScheduledRepeatUnit.Hour,
                       Every: 4,
                       NextOrdinal: 0
                   } &&
                   requestedEditDueAt.Ticks % TimeSpan.TicksPerSecond == 0 &&
                   requestedEditDueAt == recurringEditDueAt,
                "循环任务只修改间隔时必须保留已经选择的下一次 DueAt，不能从保存时刻偷偷重新计时");

            Invoke(
                todoWindow,
                "BeginScheduledTaskFormEdit",
                recurringEditItem);
            var selectedNextLocal = DateTime.Now.AddDays(7);
            selectedNextLocal = new DateTime(
                selectedNextLocal.Year,
                selectedNextLocal.Month,
                selectedNextLocal.Day,
                16,
                47,
                28,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(selectedNextLocal))
            {
                selectedNextLocal = selectedNextLocal.AddHours(1);
            }

            scheduledInput.Text = "循环任务修改下次时间";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                selectedNextLocal.Date,
                true);
            Invoke(
                todoWindow,
                "SetScheduledTimePickerSelection",
                selectedNextLocal.Hour,
                selectedNextLocal.Minute,
                selectedNextLocal.Second,
                true);
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            var expectedSelectedNextDueAt = new DateTimeOffset(
                selectedNextLocal,
                TimeZoneInfo.Local.GetUtcOffset(selectedNextLocal));
            Assert(editRequestedCount == 4 &&
                   ReferenceEquals(
                       requestedEditItem,
                       recurringEditItem) &&
                   requestedEditText == "循环任务修改下次时间" &&
                   requestedEditRepeatInterval == recurringEditInterval &&
                   requestedEditDueAt == expectedSelectedNextDueAt,
                "循环任务修改下一次日期和 HH:mm:ss 时，必须把该精确选择作为新的 DueAt");

            var preservedRuleAnchor = DateTime.Now.AddDays(14);
            preservedRuleAnchor = new DateTime(
                preservedRuleAnchor.Year,
                preservedRuleAnchor.Month,
                preservedRuleAnchor.Day,
                9,
                11,
                10,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(preservedRuleAnchor))
            {
                preservedRuleAnchor = preservedRuleAnchor.AddHours(1);
            }

            Assert(
                ScheduledRepeatSchedule.TryCreate(
                    ScheduledRepeatUnit.Hour,
                    3,
                    preservedRuleAnchor,
                    TimeZoneInfo.Local,
                    out var basePreservedRule,
                    out _),
                "非零序号循环任务回归必须先创建有效规则");
            var preservedRule =
                (basePreservedRule ??
                 throw new InvalidOperationException(
                     "循环规则创建成功后不得返回空规则")) with
                {
                    NextOrdinal = 7
                };
            Assert(
                ScheduledRepeatSchedule.TryGetOccurrence(
                    preservedRule,
                    preservedRule.NextOrdinal,
                    out var preservedRuleDueAt),
                "非零序号循环任务回归必须能解析当前到期时间");
            var ruleEditItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "保留循环进度",
                DueAt = preservedRuleDueAt,
                CreatedAt = DateTimeOffset.Now,
                RepeatInterval = TimeSpan.FromHours(3),
                RepeatRule = preservedRule,
                QuietHours = recurringEditQuietHours
            };
            scheduledTasks.Add(ruleEditItem);
            Invoke(
                todoWindow,
                "BeginScheduledTaskFormEdit",
                ruleEditItem);
            scheduledInput.Text = "  只修改循环任务文案与免打扰  ";
            SelectInlineQuietHoursTime(
                scheduledQuietHoursStart,
                20,
                1,
                2,
                "选择免打扰开始时间");
            SelectInlineQuietHoursTime(
                scheduledQuietHoursEnd,
                5,
                3,
                4,
                "选择免打扰结束时间");
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(editRequestedCount == 5 &&
                   ReferenceEquals(requestedEditItem, ruleEditItem) &&
                   requestedEditText == "只修改循环任务文案与免打扰" &&
                   requestedEditDueAt == preservedRuleDueAt &&
                   requestedEditRepeatInterval == TimeSpan.FromHours(3) &&
                   requestedEditRepeatRule == preservedRule &&
                   requestedEditRepeatRule?.NextOrdinal == 7 &&
                   requestedEditQuietHours is
                   {
                       Start.Hours: 20,
                       Start.Minutes: 1,
                       Start.Seconds: 2,
                       End.Hours: 5,
                       End.Minutes: 3,
                       End.Seconds: 4
                   },
                "只修改循环任务文案与免打扰时段必须原样保留 DueAt、规则和 NextOrdinal，不能让重启后的循环进度归零");

            var deleteItem = new ScheduledTaskItem
            {
                Text = "可删除的定时任务",
                DueAt = requestedDueAt,
                CreatedAt = DateTimeOffset.Now
            };
            ScheduledTaskItem? requestedDelete = null;
            todoWindow.ScheduledTaskDeleteRequested += item =>
                requestedDelete = item;
            SetField(
                todoWindow,
                "_suppressDeleteConfirmationForSession",
                true);
            Invoke(
                todoWindow,
                "ScheduledTaskDeleteButton_Click",
                new Button { Tag = deleteItem },
                new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(ReferenceEquals(requestedDelete, deleteItem),
                "定时任务删除按钮必须传回当前绑定实例");

            var confirmationWindow = new CuteConfirmationWindow(
                "删除这条待办？",
                "删掉以后就找不回来啦，真的要删除吗？",
                "确认删除",
                theme: CuteConfirmationTheme.TodoBlue)
            {
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            confirmationWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var confirmationSuppression = GetField<CheckBox>(
                confirmationWindow,
                "SessionSuppressionCheckBox");
            var confirmationButton = GetField<Button>(
                confirmationWindow,
                "ConfirmButton");
            var confirmationChrome = GetField<Border>(
                confirmationWindow,
                "ConfirmationChrome");
            var confirmationBadgeText = GetField<TextBlock>(
                confirmationWindow,
                "ConfirmationBadgeText");
            confirmationSuppression.IsChecked = true;
            var confirmationSource = File.ReadAllText(
                FindWorkspaceFile("CuteConfirmationWindow.xaml.cs"));
            var confirmationXaml = File.ReadAllText(
                FindWorkspaceFile("CuteConfirmationWindow.xaml"));
            var confirmationCallerSource = File.ReadAllText(
                FindWorkspaceFile("TodoWindow.xaml.cs"));
            SetField(todoWindow, "_isDeleteConfirmationOpen", true);
            Assert(todoWindow.IsTransientPopupOpen,
                "删除确认窗显示期间必须纳入临时交互状态，不能因 Owner 失活把整个待办面板收起");
            SetField(todoWindow, "_isDeleteConfirmationOpen", false);
            Assert(confirmationWindow.IsVisible &&
                   confirmationWindow.Width == 368 &&
                   confirmationWindow.Height == 238 &&
                   confirmationWindow.AllowsTransparency &&
                   confirmationWindow.FontFamily.Source ==
                       "Microsoft YaHei" &&
                   confirmationWindow.Theme ==
                       CuteConfirmationTheme.TodoBlue &&
                   confirmationChrome.Margin == new Thickness(2) &&
                   confirmationChrome.Effect is null &&
                   confirmationBadgeText.Text == "办" &&
                   confirmationButton.Background is SolidColorBrush
                       todoDeleteButtonBackground &&
                   todoDeleteButtonBackground.Color ==
                       Color.FromRgb(0x5B, 0x8D, 0xEF) &&
                   confirmationSuppression.Content?.ToString() ==
                       "本次运行不再提示" &&
                   confirmationWindow.SuppressForSession &&
                   confirmationButton.Content?.ToString() ==
                       "确认删除" &&
                   confirmationSource.Contains(
                       "CuteConfirmationResult",
                       StringComparison.Ordinal) &&
                   confirmationSource.Contains(
                       "confirmed && dialog.SuppressForSession",
                       StringComparison.Ordinal) &&
                   confirmationCallerSource.Contains(
                       "CuteConfirmationTheme.TodoBlue",
                       StringComparison.Ordinal) &&
                   confirmationCallerSource.Contains(
                       "CuteConfirmationTheme.ScheduledWarm",
                       StringComparison.Ordinal) &&
                   confirmationXaml.Contains(
                       "本次运行不再提示",
                       StringComparison.Ordinal) &&
                   confirmationXaml.Contains(
                       "AllowsTransparency=\"True\"",
                       StringComparison.Ordinal) &&
                   !confirmationXaml.Contains(
                       "WindowChrome.WindowChrome",
                       StringComparison.Ordinal),
                "待办删除确认窗必须使用独立蓝色主题和干净透明圆角，不能残留裁切成黑边的外阴影，" +
                "并只把“本次运行不再提示”作为当前调用结果返回，不自行持久化");
            confirmationWindow.Close();

            var scheduledConfirmationWindow =
                new CuteConfirmationWindow(
                    "删除定时任务？",
                    "删除后将不再按时提醒。",
                    "删除任务",
                    theme: CuteConfirmationTheme.ScheduledWarm)
                {
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
            scheduledConfirmationWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var scheduledConfirmationButton = GetField<Button>(
                scheduledConfirmationWindow,
                "ConfirmButton");
            var scheduledConfirmationBadgeText = GetField<TextBlock>(
                scheduledConfirmationWindow,
                "ConfirmationBadgeText");
            Assert(scheduledConfirmationWindow.Theme ==
                       CuteConfirmationTheme.ScheduledWarm &&
                   scheduledConfirmationBadgeText.Text == "时" &&
                   scheduledConfirmationButton.Background is
                       SolidColorBrush scheduledDeleteButtonBackground &&
                   scheduledDeleteButtonBackground.Color ==
                       Color.FromRgb(0xE7, 0x6F, 0x51) &&
                   scheduledDeleteButtonBackground.Color !=
                       Color.FromRgb(0x5B, 0x8D, 0xEF),
                "定时任务删除确认窗必须使用独立橘红主题和“时”徽章，不能与待办蓝色删除窗相同");
            scheduledConfirmationWindow.Close();

            var manualSaveAcceptedCount = 0;
            var manualSaveEditor = new TaskTextEditWindow(
                "显式保存回归",
                "  尚未确认的文字  ",
                showAdvancedEdit: false)
            {
                Left = -10000,
                Top = -10000,
                ShowActivated = false
            };
            manualSaveEditor.TextAccepted += _ =>
                manualSaveAcceptedCount++;
            manualSaveEditor.Show();
            var manualSaveButton = GetField<Button>(
                manualSaveEditor,
                "SaveButton");
            todoWindow.Activate();
            todoTab.Focus();
            Keyboard.Focus(todoTab);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            Assert(manualSaveAcceptedCount == 0 &&
                   manualSaveEditor.IsVisible &&
                   manualSaveEditor.Width == 378 &&
                   manualSaveEditor.Height == 414 &&
                   manualSaveButton.Height == 38,
                "白色修改窗必须使用 378×414 DIP 布局和更高的确定按钮，失去焦点后保留草稿且不触发保存");
            var taskEditorManualSource = File.ReadAllText(
                FindWorkspaceFile("TaskTextEditWindow.xaml.cs"));
            Assert(!taskEditorManualSource.Contains(
                       "Deactivated +=",
                       StringComparison.Ordinal) &&
                   !taskEditorManualSource.Contains(
                       "CommitAfterDeactivation",
                       StringComparison.Ordinal) &&
                   !taskEditorManualSource.Contains(
                       "QueueCommitAfterDeactivation",
                       StringComparison.Ordinal),
                "白色修改窗不得保留失活自动保存链");
            var manualSavePresentationSource =
                PresentationSource.FromVisual(manualSaveEditor)
                ?? throw new InvalidOperationException(
                    "白色修改窗未建立输入源");
            SetField(manualSaveEditor, "_isImeComposing", true);
            var imeEscape = CreateKeyEvent(
                manualSavePresentationSource,
                Key.Escape);
            Invoke(
                manualSaveEditor,
                "Window_PreviewKeyDown",
                manualSaveEditor,
                imeEscape);
            Assert(!imeEscape.Handled &&
                   manualSaveEditor.IsVisible &&
                   manualSaveAcceptedCount == 0,
                "微软输入法仍在组合候选时，Esc 必须只交给输入法取消候选，不能关闭或保存修改窗");
            Invoke(
                manualSaveEditor,
                "TaskTextEditWindow_Activated",
                manualSaveEditor,
                EventArgs.Empty);
            Assert(GetRawField(manualSaveEditor, "_isImeComposing") is false,
                "微软输入法在失焦时未发最终组合事件，修改窗重新激活后也必须清除陈旧组合状态");
            var manualSaveEscape = CreateKeyEvent(
                manualSavePresentationSource,
                Key.Escape);
            Invoke(
                manualSaveEditor,
                "Window_PreviewKeyDown",
                manualSaveEditor,
                manualSaveEscape);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(manualSaveEscape.Handled &&
                   manualSaveAcceptedCount == 0 &&
                   !manualSaveEditor.IsVisible,
                "白色修改窗按 Esc 必须取消且不得提交草稿");
        }
        finally
        {
            todoWindow.CloseForApplication();
        }

        AssertPastRecurringDraftAdvanceContract();
        AssertDeleteConfirmationFlowContract();
    }

    private static void AssertPastRecurringDraftAdvanceContract()
    {
        var pastLocal = DateTime.Now.AddMinutes(-5);
        pastLocal = new DateTime(
            pastLocal.Year,
            pastLocal.Month,
            pastLocal.Day,
            pastLocal.Hour,
            pastLocal.Minute,
            pastLocal.Second,
            DateTimeKind.Unspecified);
        while (TimeZoneInfo.Local.IsInvalidTime(pastLocal))
        {
            pastLocal = pastLocal.AddHours(1);
        }

        var todoWindow = new TodoWindow
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ScheduledTasks = new ObservableCollection<ScheduledTaskItem>()
        };
        try
        {
            todoWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Invoke(todoWindow, "SelectTaskPage", true, false);
            var input = GetField<TextBox>(
                todoWindow,
                "ScheduledTaskInput");
            var timeInput = GetField<TextBox>(
                todoWindow,
                "ScheduledTimeInput");
            var repeatToggle = GetField<CheckBox>(
                todoWindow,
                "ScheduledRepeatToggle");
            var repeatCount = GetField<TextBox>(
                todoWindow,
                "ScheduledRepeatCountInput");
            var repeatUnit = GetField<ComboBox>(
                todoWindow,
                "ScheduledRepeatUnitComboBox");
            var validation = GetField<TextBlock>(
                todoWindow,
                "ScheduledTaskValidationText");
            var addCount = 0;
            DateTimeOffset advancedDueAt = default;
            ScheduledRepeatRule? advancedRule = null;
            todoWindow.ScheduledTaskAddRequested += (
                _,
                dueAt,
                _,
                repeatRule,
                _) =>
            {
                addCount++;
                advancedDueAt = dueAt;
                advancedRule = repeatRule;
            };

            input.Text = "过去时间循环自动推进";
            Invoke(todoWindow, "SetScheduledDate", pastLocal.Date, true);
            timeInput.Text = pastLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            repeatToggle.IsChecked = true;
            repeatCount.Text = "1";
            repeatUnit.SelectedIndex = (int)ScheduledRepeatUnit.Minute;
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(addCount == 1 &&
                   advancedDueAt > DateTimeOffset.Now &&
                   advancedRule is
                   {
                       Unit: ScheduledRepeatUnit.Minute,
                       Every: 1,
                       NextOrdinal: > 0
                   } &&
                   ScheduledRepeatSchedule.TryGetOccurrence(
                       advancedRule,
                       advancedRule.NextOrdinal,
                       out var advancedOccurrence) &&
                   advancedOccurrence == advancedDueAt,
                "新增过去时间的循环任务必须直接推进到严格未来的下一次并同步 NextOrdinal");

            input.Text = "一次性过去时间仍应拒绝";
            Invoke(todoWindow, "SetScheduledDate", pastLocal.Date, true);
            timeInput.Text = pastLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            repeatToggle.IsChecked = false;
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(addCount == 1 &&
                   validation.Text.Contains("晚于现在", StringComparison.Ordinal),
                "一次性过去时间仍必须拒绝，不能被循环自动推进逻辑误放行");
        }
        finally
        {
            todoWindow.CloseForApplication();
        }

        Assert(
            ScheduledRepeatSchedule.TryCreate(
                ScheduledRepeatUnit.Minute,
                1,
                pastLocal,
                TimeZoneInfo.Local,
                out var originalRule,
                out var originalDueAt) &&
            originalRule is not null,
            "定时编辑窗过去循环推进测试必须先创建有效规则");
        var requiredOriginalRule = originalRule ??
                                   throw new InvalidOperationException(
                                       "有效循环规则不得为空");
        var recurringItem = new ScheduledTaskItem
        {
            Text = "修改过去循环时间",
            DueAt = originalDueAt,
            CreatedAt = DateTimeOffset.Now.AddHours(-1),
            RepeatInterval = TimeSpan.FromMinutes(1),
            RepeatRule = requiredOriginalRule
        };
        var recurringEditor = new ScheduledTaskEditWindow(recurringItem)
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false
        };
        var editAccepted = 0;
        DateTimeOffset editedDueAt = default;
        ScheduledRepeatRule? editedRule = null;
        recurringEditor.EditAccepted += (
            _,
            dueAt,
            _,
            repeatRule,
            _) =>
        {
            editAccepted++;
            editedDueAt = dueAt;
            editedRule = repeatRule;
        };
        recurringEditor.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Invoke(
            recurringEditor,
            "SaveButton_Click",
            GetField<Button>(recurringEditor, "SaveButton"),
            new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(editAccepted == 1 &&
               !recurringEditor.IsVisible &&
               editedDueAt > DateTimeOffset.Now &&
               editedRule is { NextOrdinal: > 0 } futureEditRule &&
               futureEditRule.NextOrdinal >
               requiredOriginalRule.NextOrdinal,
            "独立定时修改窗保存过去循环任务时必须推进到未来并递增 NextOrdinal");

        var oneOffEditor = new ScheduledTaskEditWindow(
            new ScheduledTaskItem
            {
                Text = "一次性过去时间",
                DueAt = originalDueAt,
                CreatedAt = DateTimeOffset.Now.AddHours(-1)
            })
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false
        };
        var oneOffAccepted = 0;
        oneOffEditor.EditAccepted += (_, _, _, _, _) => oneOffAccepted++;
        oneOffEditor.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Invoke(
            oneOffEditor,
            "SaveButton_Click",
            GetField<Button>(oneOffEditor, "SaveButton"),
            new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert(oneOffAccepted == 0 &&
               oneOffEditor.IsVisible &&
               GetField<TextBlock>(oneOffEditor, "ValidationText")
                   .Text.Contains("晚于现在", StringComparison.Ordinal),
            "独立定时修改窗仍必须拒绝一次性过去时间并保留草稿窗口");
        oneOffEditor.CloseWithoutSaving();
    }

    private static void AssertSingleLineTextIsFullyVisible(
        TextBlock text,
        FrameworkElement host,
        string description)
    {
        host.UpdateLayout();
        text.UpdateLayout();

        var probe = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontStretch = text.FontStretch,
            FontStyle = text.FontStyle,
            FontWeight = text.FontWeight,
            FlowDirection = text.FlowDirection,
            TextWrapping = TextWrapping.NoWrap
        };
        probe.Measure(new Size(
            double.PositiveInfinity,
            double.PositiveInfinity));

        var origin = text.TranslatePoint(new Point(0, 0), host);
        var layoutClip = LayoutInformation.GetLayoutClip(text);
        Console.WriteLine(
            $"[METRIC] {description}: actual={text.ActualWidth:F2} DIP, " +
            $"required={probe.DesiredSize.Width:F2} DIP, " +
            $"host={host.ActualWidth:F2} DIP");
        Assert(text.Visibility == Visibility.Visible &&
               text.TextWrapping == TextWrapping.NoWrap &&
               text.TextTrimming == TextTrimming.None &&
               text.Clip is null &&
               !text.ClipToBounds &&
               layoutClip is null &&
               text.ActualWidth + 0.5 >= probe.DesiredSize.Width &&
               origin.X >= -0.5 &&
               origin.X + text.ActualWidth <= host.ActualWidth + 0.5,
            $"{description}必须完整显示且不能裁字：" +
            $"actual={text.ActualWidth:F2}, required={probe.DesiredSize.Width:F2}, " +
            $"right={origin.X + text.ActualWidth:F2}, host={host.ActualWidth:F2}, " +
            $"layoutClip={layoutClip}");
    }

    private static void AssertScheduledTaskEditContract(MainWindow window)
    {
        var scheduledTasks = GetField<ObservableCollection<ScheduledTaskItem>>(
            window,
            "_scheduledTasks");
        var reminderQueue = GetField<Queue<ScheduledTaskItem>>(
            window,
            "_reminderQueue");
        var queuedReminderIds = GetField<HashSet<Guid>>(
            window,
            "_queuedReminderIds");
        var scheduledStore = GetField<ScheduledTaskStore>(
            window,
            "_scheduledTaskStore");
        var scheduledTimer = GetField<DispatcherTimer>(
            window,
            "_scheduledTaskTimer");
        var originalNowProvider = GetField<Func<DateTimeOffset>>(
            window,
            "_nowProvider");
        var now = new DateTimeOffset(
            2026,
            7,
            22,
            15,
            0,
            0,
            TimeSpan.FromHours(8));
        Func<DateTimeOffset> controlledNow = () => now;

        scheduledTimer.Stop();
        scheduledTasks.Clear();
        reminderQueue.Clear();
        queuedReminderIds.Clear();
        SetField(window, "_activeReminder", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_nowProvider", controlledNow);
        Assert(scheduledStore.Save(scheduledTasks),
            "定时任务编辑回归必须使用临时 ScheduledTaskStore");

        try
        {
            var first = new ScheduledTaskItem
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Text = "原本最早",
                DueAt = now.AddSeconds(20),
                CreatedAt = now.AddMinutes(-3).AddMilliseconds(111)
            };
            var middle = new ScheduledTaskItem
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                Text = "原本居中",
                DueAt = now.AddSeconds(30),
                CreatedAt = now.AddMinutes(-2).AddMilliseconds(222)
            };
            var moving = new ScheduledTaskItem
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000003"),
                Text = "原本最晚",
                DueAt = now.AddSeconds(40),
                CreatedAt = now.AddMinutes(-1).AddMilliseconds(333)
            };
            foreach (var item in new[] { middle, moving, first })
            {
                Invoke(window, "InsertScheduledTaskSorted", item);
            }

            Assert(scheduledStore.Save(scheduledTasks),
                "编辑排序回归的初始任务必须成功持久化");
            var movingId = moving.Id;
            var movingCreatedAt = moving.CreatedAt;
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                moving,
                "  改到更早并修正文案  ",
                now.AddSeconds(10).AddMilliseconds(875),
                null,
                null,
                null);
            Assert(scheduledTasks.SequenceEqual([moving, first, middle]) &&
                   moving.Id == movingId &&
                   moving.CreatedAt == movingCreatedAt &&
                   moving.Text == "改到更早并修正文案" &&
                   moving.DueAt == now.AddSeconds(10) &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual([moving.Id, first.Id, middle.Id]) &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(8))
                       .TotalMilliseconds) < 1,
                "编辑到更早时间必须保留 Id/CreatedAt、Trim并归一到整秒，" +
                "同时重排内存/磁盘并重新对准到期前2秒预热点");

            var editedRepeatInterval =
                TimeSpan.FromDays(2) +
                TimeSpan.FromHours(3) +
                TimeSpan.FromMinutes(15);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                moving,
                "改到更晚",
                now.AddSeconds(50).AddMilliseconds(499),
                editedRepeatInterval,
                null,
                null);
            Assert(scheduledTasks.SequenceEqual([first, middle, moving]) &&
                   moving.Id == movingId &&
                   moving.CreatedAt == movingCreatedAt &&
                   moving.DueAt == now.AddSeconds(50) &&
                   moving.RepeatInterval == editedRepeatInterval &&
                   scheduledStore.Load().Select(item => item.Id)
                        .SequenceEqual([first.Id, middle.Id, moving.Id]) &&
                   scheduledStore.Load().Single(item => item.Id == moving.Id)
                       .RepeatInterval == editedRepeatInterval &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(18))
                       .TotalMilliseconds) < 1,
                "编辑到更晚循环时间必须持久化周期、移动到正确位置并把调度器切回最早任务的预热点");

            scheduledTimer.Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            var active = new ScheduledTaskItem
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
                Text = "正在显示的提醒",
                DueAt = now,
                CreatedAt = now.AddMinutes(-3)
            };
            var queuedFirst = new ScheduledTaskItem
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000002"),
                Text = "排队提醒甲",
                DueAt = now,
                CreatedAt = now.AddMinutes(-2)
            };
            var queuedSecond = new ScheduledTaskItem
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000003"),
                Text = "排队提醒乙",
                DueAt = now,
                CreatedAt = now.AddMinutes(-1)
            };
            foreach (var item in new[] { active, queuedFirst, queuedSecond })
            {
                Invoke(window, "InsertScheduledTaskSorted", item);
            }

            SetField(window, "_activeReminder", active);
            SetField(window, "_isReminderActive", true);
            Invoke(window, "RebuildReminderQueueAt", now);
            Assert(reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   queuedReminderIds.SetEquals(
                       [active.Id, queuedFirst.Id, queuedSecond.Id]),
                "已到点的同秒任务必须先形成一个活动项和稳定顺序的等待队列");
            Assert(scheduledStore.Save(scheduledTasks),
                "排队编辑回归的初始状态必须成功持久化");

            var activeCreatedAt = active.CreatedAt;
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                active,
                "不允许覆盖正在显示的内容",
                now.AddMinutes(10),
                null,
                null,
                null);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       active) &&
                   active.Text == "正在显示的提醒" &&
                   active.DueAt == now &&
                   active.CreatedAt == activeCreatedAt &&
                   reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   scheduledStore.Load().Single(item => item.Id == active.Id).Text ==
                       "正在显示的提醒",
                "正在气泡中显示的任务必须拒绝编辑，避免画面、队列和磁盘内容分裂");

            var queuedFirstId = queuedFirst.Id;
            var queuedFirstCreatedAt = queuedFirst.CreatedAt;
            var queuedFirstOriginalText = queuedFirst.Text;
            var queuedFirstOriginalDueAt = queuedFirst.DueAt;
            var frozenBatchOrder = scheduledTasks.ToArray();
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                queuedFirst,
                "  排队任务延后  ",
                now.AddSeconds(30).AddMilliseconds(900),
                null,
                null,
                null);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       active) &&
                   queuedFirst.Id == queuedFirstId &&
                   queuedFirst.CreatedAt == queuedFirstCreatedAt &&
                   queuedFirst.Text == queuedFirstOriginalText &&
                   queuedFirst.DueAt == queuedFirstOriginalDueAt &&
                   scheduledTasks.SequenceEqual(frozenBatchOrder) &&
                   reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   queuedReminderIds.SetEquals(
                       [active.Id, queuedFirst.Id, queuedSecond.Id]) &&
                   !scheduledTimer.IsEnabled &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual(frozenBatchOrder.Select(item => item.Id)),
                "已进入可见或排队提醒批次的任务必须冻结修改，保持内存、队列与磁盘顺序一致");

            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                queuedFirst,
                "排队任务提前",
                now.AddSeconds(-1),
                null,
                null,
                null);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       active) &&
                   queuedFirst.Text == queuedFirstOriginalText &&
                   queuedFirst.DueAt == queuedFirstOriginalDueAt &&
                   scheduledTasks.SequenceEqual(frozenBatchOrder) &&
                   reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   queuedReminderIds.SetEquals(
                       [active.Id, queuedFirst.Id, queuedSecond.Id]) &&
                   queuedReminderIds.Count == 3 &&
                   !scheduledTimer.IsEnabled &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual(frozenBatchOrder.Select(item => item.Id)),
                "对冻结批次重复发起修改也不得改变内容、截止时间、顺序或产生重复Id");
        }
        finally
        {
            scheduledTimer.Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            scheduledStore.Save(scheduledTasks);
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_nowProvider", originalNowProvider);
        }
    }

}
