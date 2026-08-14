using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LubanDesktopPet;

internal static partial class Program
{
    private static void AssertScheduledReminderOccurrenceStackContract(
        MainWindow window)
    {
        var scheduledTasks =
            GetField<ObservableCollection<ScheduledTaskItem>>(
                window,
                "_scheduledTasks");
        var reminderQueue =
            GetField<Queue<ScheduledTaskItem>>(
                window,
                "_reminderQueue");
        var queuedReminderIds =
            GetField<HashSet<Guid>>(
                window,
                "_queuedReminderIds");
        var activeBatch =
            GetField<List<ScheduledTaskItem>>(
                window,
                "_activeReminderBatch");
        var visibleOccurrences =
            (IList)GetRawField(
                window,
                "_visibleReminderOccurrences")!;
        var observedCounts =
            (IDictionary)GetRawField(
                window,
                "_presentedReminderOccurrenceCounts")!;
        var scheduledStore =
            GetField<ScheduledTaskStore>(
                window,
                "_scheduledTaskStore");
        var scheduledTimer =
            GetField<DispatcherTimer>(
                window,
                "_scheduledTaskTimer");
        var automaticTimer =
            GetField<DispatcherTimer>(
                window,
                "_automaticTimer");
        var reminderSizeTimer =
            GetField<DispatcherTimer>(
                window,
                "_reminderSizeCommitTimer");
        var todoWindow =
            GetField<TodoWindow>(window, "_todoWindow");
        var originalNowProvider =
            GetField<Func<DateTimeOffset>>(window, "_nowProvider");
        var originalAutomaticEnabled =
            GetField<bool>(window, "_automaticAnimationEnabled");
        var originalScale = GetField<double>(window, "_petSizeScale");
        var originalHitTestVisible = window.IsHitTestVisible;
        var now = new DateTimeOffset(
            2032,
            6,
            7,
            8,
            9,
            10,
            TimeSpan.FromHours(8));
        var firstDueAt = now;
        Func<DateTimeOffset> controlledNow = () => now;
        ReminderWindow? observedReminderWindow = null;
        TaskTextEditWindow? interruptedTodoEditor = null;
        EventHandler? dismissObserver = null;
        var dismissRequestCount = 0;

        var mainSource = File.ReadAllText(
            FindWorkspaceFile("MainWindow.xaml.cs"));
        var reminderSource = File.ReadAllText(
            FindWorkspaceFile("ReminderWindow.xaml.cs"));
        var reminderXaml = File.ReadAllText(
            FindWorkspaceFile("ReminderWindow.xaml"));
        var scheduleSource = ExtractPrivateMethodSource(
            mainSource,
            "ScheduleNextReminderAt");
        var acknowledgeSource = ExtractPrivateMethodSource(
            mainSource,
            "AcknowledgeActiveReminder");
        var refreshActiveReminderSource = ExtractPrivateMethodSource(
            mainSource,
            "RefreshActiveReminderPresentation");
        var restartReminderAttentionSource = ExtractPrivateMethodSource(
            mainSource,
            "RestartReminderAttentionAnimation");
        var dismissReminderSource = ExtractPrivateMethodSource(
            mainSource,
            "ReminderWindow_DismissRequested");
        var deleteScheduledTaskSource = ExtractPrivateMethodSource(
            mainSource,
            "TodoWindow_ScheduledTaskDeleteRequested");
        var removeDeletedReminderSource = ExtractPrivateMethodSource(
            mainSource,
            "RemoveDeletedScheduledTaskFromReminderState");
        var finishDeletedReminderSource = ExtractPrivateMethodSource(
            mainSource,
            "FinishReminderAfterScheduledTaskDeletion");
        Assert(mainSource.Contains(
                   "MaximumVisibleReminderOccurrences = 100",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "_visibleReminderOccurrences",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "_presentedReminderOccurrenceCounts",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "TryGetReminderOccurrenceDueAt(",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "ScheduledRepeatSchedule.TryGetOccurrence(",
                   StringComparison.Ordinal) &&
               scheduleSource.Contains(
                   "FindNextReminderDueAt(now)",
                   StringComparison.Ordinal) &&
               !scheduleSource.Contains(
                   "_queuedReminderIds",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "GroupBy(occurrence => occurrence.TaskId)",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "AdvanceAcknowledgedScheduledTask(",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "carriedForwardCounts",
                   StringComparison.Ordinal) &&
               refreshActiveReminderSource.Contains(
                   "hasNewReminderOccurrences",
                   StringComparison.Ordinal) &&
               refreshActiveReminderSource.Contains(
                   "RestartReminderAttentionAnimation()",
                   StringComparison.Ordinal) &&
                restartReminderAttentionSource.Contains(
                    "StartReminderHoldAnimation()",
                    StringComparison.Ordinal) &&
                dismissReminderSource.Contains(
                    "AcknowledgeActiveReminder()",
                    StringComparison.Ordinal) &&
                !dismissReminderSource.Contains(
                    "_isReminderPresentationDismissed = true",
                    StringComparison.Ordinal) &&
                mainSource.Contains(
                    "_reminderWindow.DismissRequested +=",
                    StringComparison.Ordinal) &&
                reminderSource.Contains(
                    "ShowBeside(Window anchor)",
                    StringComparison.Ordinal) &&
                reminderSource.Contains(
                    "public event EventHandler? DismissRequested",
                    StringComparison.Ordinal) &&
                reminderSource.Contains(
                    "RequestDismiss(",
                    StringComparison.Ordinal) &&
                reminderSource.Contains(
                    "usableHeight",
                    StringComparison.Ordinal) &&
                reminderSource.Contains(
                    "MonitorWorkArea.GetForVisual(_anchor, _placementAnchor)",
                    StringComparison.Ordinal) &&
               reminderSource.Contains(
                    "Math.Min(PreferredPagedHeight, usableHeight)",
                    StringComparison.Ordinal) &&
                reminderXaml.Contains(
                    "ShowActivated=\"False\"",
                    StringComparison.Ordinal) &&
                reminderXaml.Contains(
                    "x:Name=\"ReminderCloseButton\"",
                    StringComparison.Ordinal) &&
                reminderXaml.Contains(
                    "ToolTip=\"关闭并清空本批提醒\"",
                    StringComparison.Ordinal) &&
                reminderXaml.Contains(
                    "Click=\"CloseButton_Click\"",
                    StringComparison.Ordinal) &&
                reminderXaml.Contains(
                    "PreviewMouseLeftButtonDown=\"CloseButton_PreviewMouseLeftButtonDown\"",
                    StringComparison.Ordinal) &&
                reminderSource.Contains(
                    "e.ClickCount > 1",
                    StringComparison.Ordinal) &&
                reminderSource.Contains(
                    "if (!e.IsRepeat)",
                    StringComparison.Ordinal) &&
                reminderXaml.Split(
                    "<TextBox ",
                    StringSplitOptions.None).Length == 2 &&
                deleteScheduledTaskSource.Contains(
                    "RemoveDeletedScheduledTaskFromReminderState(item.Id)",
                    StringComparison.Ordinal) &&
                deleteScheduledTaskSource.Contains(
                    "RebuildReminderQueueAt(now)",
                    StringComparison.Ordinal) &&
                deleteScheduledTaskSource.Contains(
                    "RefreshActiveReminderPresentation(now)",
                    StringComparison.Ordinal) &&
                removeDeletedReminderSource.Contains(
                    "_visibleReminderOccurrences.RemoveAll(",
                    StringComparison.Ordinal) &&
                removeDeletedReminderSource.Contains(
                    "_presentedReminderOccurrenceCounts.Remove(itemId)",
                    StringComparison.Ordinal) &&
                finishDeletedReminderSource.Contains(
                    "RestoreReminderPetSizeAt(",
                    StringComparison.Ordinal),
            "提醒必须按 occurrence 建模、未确认时继续布下一次定时器；" +
            "关闭与知道啦必须统一消费当前显示项，删除活动任务要同步重整提醒状态，" +
            "并在单个轻量窗口内最多显示100条");

        var reminderSetPresentation = typeof(ReminderWindow).GetMethod(
            "SetPresentation",
            InstanceFlags);
        var reminderPresentationParameters =
            reminderSetPresentation?.GetParameters() ?? [];
        var reminderPageSizeConstant = typeof(ReminderWindow)
            .GetFields(StaticFlags)
            .FirstOrDefault(field =>
                field.IsLiteral &&
                field.FieldType == typeof(int) &&
                field.Name.Contains("Page", StringComparison.Ordinal) &&
                Equals(field.GetRawConstantValue(), 5));
        Assert(reminderSetPresentation is not null &&
               reminderPresentationParameters.Length == 3 &&
               reminderPresentationParameters[0].ParameterType ==
                   typeof(string) &&
               reminderPresentationParameters[1].ParameterType ==
                   typeof(IReadOnlyList<string>) &&
               reminderPresentationParameters[2].ParameterType ==
                   typeof(long) &&
               reminderPageSizeConstant is not null &&
               mainSource.Contains(
                   "BuildReminderPresentationEntries()",
                   StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "BuildReminderPresentationText(",
                   StringComparison.Ordinal) &&
               reminderSource.Contains(
                   "PreferredPagedHeight = 468",
                   StringComparison.Ordinal) &&
               !reminderSource.Contains(
                   "MaximumPreferredHeight = 4096",
                   StringComparison.Ordinal) &&
               reminderXaml.Contains(
                   "x:Name=\"ReminderPagingPanel\"",
                   StringComparison.Ordinal) &&
               reminderXaml.Contains(
                   "x:Name=\"ReminderPreviousPageButton\"",
                   StringComparison.Ordinal) &&
               reminderXaml.Contains(
                   "x:Name=\"ReminderPageText\"",
                   StringComparison.Ordinal) &&
               reminderXaml.Contains(
                   "x:Name=\"ReminderNextPageButton\"",
                   StringComparison.Ordinal) &&
               reminderXaml.Contains(
                   "x:Name=\"ReminderAcknowledgeButton\"",
                   StringComparison.Ordinal) &&
               reminderXaml.Contains(
                   "x:Key=\"CuteReminderPageButtonStyle\"",
                   StringComparison.Ordinal) &&
               reminderXaml.Contains(
                   "FontFamily=\"Microsoft YaHei\"",
                   StringComparison.Ordinal),
            "提醒分页必须保留100条批次安全上限，改用字符串条目列表并以5条为一页；" +
            "窗口必须固定紧凑高度并提供可测的萌橘分页控件，不能继续拼接一整段百条文本");

        scheduledTimer.Stop();
        automaticTimer.Stop();
        reminderSizeTimer.Stop();
        scheduledTasks.Clear();
        reminderQueue.Clear();
        queuedReminderIds.Clear();
        activeBatch.Clear();
        visibleOccurrences.Clear();
        observedCounts.Clear();
        SetField(window, "_activeReminder", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_isReminderPresentationDismissed", false);
        SetField(window, "_totalReminderOccurrenceCount", 0L);
        SetField(window, "_upcomingReminderPreloadPageName", null);
        SetField(window, "_nowProvider", controlledNow);
        SetField(window, "_automaticAnimationEnabled", false);
        window.IsHitTestVisible = false;

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
            }

            var deleteFirst = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "42D00000-0000-0000-0000-000000000001"),
                Text = "提醒时删除的第一行",
                DueAt = now.AddSeconds(-2),
                CreatedAt = now.AddMinutes(-2)
            };
            var deleteSecond = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "42D00000-0000-0000-0000-000000000002"),
                Text = "提醒时必须保留的第二行",
                DueAt = now.AddSeconds(-1),
                CreatedAt = now.AddMinutes(-1)
            };
            Invoke(window, "InsertScheduledTaskSorted", deleteFirst);
            Invoke(window, "InsertScheduledTaskSorted", deleteSecond);
            Assert(scheduledStore.Save(scheduledTasks),
                "提醒中删除回归必须先持久化两条到点任务");
            Invoke(window, "ProcessScheduledTasksAt", now);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var deletionReminderWindow =
                (ReminderWindow?)GetRawField(window, "_reminderWindow")
                ?? throw new InvalidOperationException(
                    "提醒中删除回归没有创建提醒窗口");
            var deletionReminderText = GetField<TextBox>(
                deletionReminderWindow,
                "ReminderContentTextBox");
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       deleteFirst) &&
                   activeBatch.SequenceEqual(
                       [deleteFirst, deleteSecond]) &&
                   visibleOccurrences.Count == 2 &&
                   deletionReminderWindow.IsVisible,
                "两条到点任务必须先形成活动提醒批次，第一行才具备真实删除条件");

            Invoke(
                window,
                "TodoWindow_ScheduledTaskDeleteRequested",
                deleteFirst);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledTasks.SequenceEqual([deleteSecond]) &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual([deleteSecond.Id]) &&
                   ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       deleteSecond) &&
                   activeBatch.SequenceEqual([deleteSecond]) &&
                   visibleOccurrences.Count == 1 &&
                   GetProperty<Guid>(
                       visibleOccurrences[0]!,
                       "TaskId") == deleteSecond.Id &&
                   observedCounts.Count == 1 &&
                   queuedReminderIds.SetEquals([deleteSecond.Id]) &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 1 &&
                   GetField<bool>(window, "_isReminderActive") &&
                   deletionReminderWindow.IsVisible &&
                   deletionReminderText.Text.Contains(
                       deleteSecond.Text,
                       StringComparison.Ordinal) &&
                   !deletionReminderText.Text.Contains(
                       deleteFirst.Text,
                       StringComparison.Ordinal),
                "提醒弹窗活跃时删除第一行，必须立即移除其occurrence并把剩余任务重整为新的活动首项");

            Invoke(
                window,
                "TodoWindow_ScheduledTaskDeleteRequested",
                deleteSecond);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledTasks.Count == 0 &&
                   scheduledStore.Load().Count == 0 &&
                   GetRawField(window, "_activeReminder") is null &&
                   activeBatch.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   visibleOccurrences.Count == 0 &&
                   observedCounts.Count == 0 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 0 &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   !deletionReminderWindow.IsVisible &&
                   GetField<object>(
                       window,
                       "_bubbleMode").ToString() == "None",
                "删除活动提醒的最后一项必须完整退出提醒、清空批次/队列/计数并保留空持久化状态");
            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);

            Invoke(
                window,
                "SetBubbleMode",
                GetNestedEnum("BubbleMode", "Todo"));
            Invoke(
                todoWindow,
                "SelectTaskPage",
                true,
                false);
            var scheduledInput =
                GetField<TextBox>(
                    todoWindow,
                    "ScheduledTaskInput");
            scheduledInput.Text = "正在编辑，提醒不得关闭或清空";
            var interruptedTodoItem = new TodoItem
            {
                Text = "提醒共存测试原文"
            };
            Invoke(
                todoWindow,
                "OpenTodoItemEditor",
                interruptedTodoItem);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            interruptedTodoEditor = GetField<TaskTextEditWindow>(
                todoWindow,
                "_taskTextEditWindow");
            var interruptedTodoEditorText =
                GetField<TextBox>(
                    interruptedTodoEditor,
                    "EditorTextBox");
            interruptedTodoEditorText.Text =
                "提醒期间未保存的待办草稿";
            interruptedTodoEditor.Width = 430;
            interruptedTodoEditor.Height = 470;
            interruptedTodoEditor.UpdateLayout();
            Assert(todoWindow.IsVisible &&
                   interruptedTodoEditor.IsVisible,
                "提醒共存测试必须先显示定时任务面板和待办修改窗口");

            var reminderSizeAnchor =
                Invoke(window, "CapturePetSizeAnchor", true)
                ?? throw new InvalidOperationException(
                    "提醒尺寸回归无法取得当前桌宠锚点");
            var reminderDpi = VisualTreeHelper.GetDpi(window);
            var expectedReminderEnvelope = (Rect)InvokeStatic(
                typeof(MainWindow),
                "CalculatePetSizeWindowBounds",
                1.40d,
                reminderSizeAnchor,
                reminderDpi.DpiScaleX,
                reminderDpi.DpiScaleY)!;
            var recurring = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "43000000-0000-0000-0000-000000000001"),
                Text = "循环堆叠提醒",
                DueAt = firstDueAt,
                CreatedAt = firstDueAt.AddDays(-1),
                RepeatInterval = TimeSpan.FromMinutes(1)
            };
            Invoke(window, "InsertScheduledTaskSorted", recurring);
            Assert(scheduledStore.Save(scheduledTasks),
                "循环堆叠测试数据必须先持久化");

            Invoke(window, "ProcessScheduledTasksAt", now);
            PumpDispatcher(TimeSpan.FromMilliseconds(400));
            var reminderWindow =
                (ReminderWindow?)GetRawField(
                    window,
                    "_reminderWindow");
            observedReminderWindow = reminderWindow;
            dismissObserver = (_, _) => dismissRequestCount++;
            reminderWindow!.DismissRequested += dismissObserver;
            var expectedReminderPetBounds = (Rect)InvokeStatic(
                typeof(MainWindow),
                "CalculatePetSizeLogicalWindowBounds",
                1.40d,
                reminderSizeAnchor)!;
            var actualReminderPetBounds = (Rect)Invoke(
                window,
                "GetPetViewboxBoundsInScreenDips")!;
            Assert(reminderWindow?.IsVisible == true &&
                   todoWindow.IsVisible &&
                   scheduledInput.Text ==
                   "正在编辑，提醒不得关闭或清空" &&
                   ReferenceEquals(
                       GetRawField(
                           todoWindow,
                           "_editorInterruptedByReminder"),
                       interruptedTodoEditor) &&
                   ReferenceEquals(
                       GetRawField(
                           todoWindow,
                           "_taskTextEditWindow"),
                       interruptedTodoEditor) &&
                   interruptedTodoEditor.IsVisible &&
                   interruptedTodoEditorText.Text ==
                       "提醒期间未保存的待办草稿" &&
                   Math.Abs(
                       actualReminderPetBounds.Left -
                       expectedReminderPetBounds.Left) <= 1 &&
                   Math.Abs(
                       actualReminderPetBounds.Top -
                       expectedReminderPetBounds.Top) <= 1 &&
                   Math.Abs(
                       actualReminderPetBounds.Width -
                       expectedReminderPetBounds.Width) <= 1 &&
                   Math.Abs(
                       actualReminderPetBounds.Height -
                       expectedReminderPetBounds.Height) <= 1 &&
                   Math.Abs(window.Width - expectedReminderEnvelope.Width) <= 1 &&
                   Math.Abs(window.Height - expectedReminderEnvelope.Height) <= 1 &&
                   visibleOccurrences.Count == 1 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 1,
                "面板打开时到点提醒必须显示在旁边并按原锚点放大，不能关闭面板、清空草稿或跳到屏幕角落；" +
                $"reminderVisible={reminderWindow?.IsVisible}, todoVisible={todoWindow.IsVisible}, " +
                $"draftMatch={scheduledInput.Text == "正在编辑，提醒不得关闭或清空"}, " +
                $"editorTracked={ReferenceEquals(GetRawField(todoWindow, "_editorInterruptedByReminder"), interruptedTodoEditor)}, " +
                $"editorVisible={interruptedTodoEditor.IsVisible}, editorText={interruptedTodoEditorText.Text}, " +
                $"outer=({window.Left:F2},{window.Top:F2},{window.Width:F2},{window.Height:F2}), " +
                $"pet={actualReminderPetBounds}, expectedPet={expectedReminderPetBounds}, " +
                $"expectedEnvelope={expectedReminderEnvelope}, occurrences={visibleOccurrences.Count}, " +
                $"total={GetField<long>(window, "_totalReminderOccurrenceCount")}");

            var todoBounds = new Rect(
                todoWindow.Left,
                todoWindow.Top,
                todoWindow.ActualWidth,
                todoWindow.ActualHeight);
            var reminderBounds = new Rect(
                reminderWindow!.Left,
                reminderWindow.Top,
                reminderWindow.ActualWidth,
                reminderWindow.ActualHeight);
            var reminderWorkArea =
                (Rect)InvokeStatic(
                    typeof(MainWindow).Assembly.GetType(
                        "LubanDesktopPet.MonitorWorkArea",
                        throwOnError: true)!,
                    "GetForWindow",
                    todoWindow)!;
            Assert(!todoBounds.IntersectsWith(reminderBounds) &&
                   reminderBounds.Left >= reminderWorkArea.Left - 1 &&
                   reminderBounds.Top >= reminderWorkArea.Top - 1 &&
                   reminderBounds.Right <= reminderWorkArea.Right + 1 &&
                   reminderBounds.Bottom <= reminderWorkArea.Bottom + 1,
                "提醒窗口必须位于任务面板旁边且完整留在同一显示器工作区");

            var reminderHoldFrames =
                GetField<Array>(window, "_reminderHoldFrames");
            PrimeSpritePageForFrame(
                window,
                reminderHoldFrames.GetValue(0)!);
            now = firstDueAt.AddMinutes(1);
            Invoke(window, "ProcessScheduledTasksAt", now);
            var reminderHoldClip =
                GetRawField(window, "_reminderHoldClip");
            Assert(visibleOccurrences.Count == 2 &&
                   ReferenceEquals(
                       GetRawField(window, "_activeClip"),
                       reminderHoldClip) &&
                   GetField<int>(window, "_activeFrameIndex") == 0 &&
                   todoWindow.IsVisible &&
                   scheduledInput.Text ==
                       "正在编辑，提醒不得关闭或清空",
                "提醒仍显示时新到一次必须追加第二条，并从喇叭保持序列首帧重新摇一次；Todo草稿不得受影响");

            var replayTimestamp = Stopwatch.GetTimestamp();
            Invoke(
                window,
                "TryStartDeferredActiveClipClockAt",
                replayTimestamp);
            Assert(GetField<long>(
                       window,
                       "_activeClipStartedTimestamp") ==
                   replayTimestamp,
                "新 occurrence 的喇叭重摇必须能建立一条新的绝对时间轴");
            var replayFrameIndex =
                GetField<int>(window, "_activeFrameIndex");
            var replayDeadline =
                GetField<long>(
                    window,
                    "_activeFrameDeadlineTimestamp");
            Invoke(window, "ProcessScheduledTasksAt", now);
            Assert(visibleOccurrences.Count == 2 &&
                   ReferenceEquals(
                       GetRawField(window, "_activeClip"),
                       reminderHoldClip) &&
                   GetField<long>(
                       window,
                       "_activeClipStartedTimestamp") ==
                       replayTimestamp &&
                   GetField<long>(
                       window,
                       "_activeFrameDeadlineTimestamp") ==
                       replayDeadline &&
                   GetField<int>(
                       window,
                       "_activeFrameIndex") ==
                       replayFrameIndex,
                "同一 now 重复 Process 不得重复追加消息、重置喇叭时间轴或再次抖动");

            now = firstDueAt.AddMinutes(2);
            Invoke(window, "ProcessScheduledTasksAt", now);
            Invoke(window, "ProcessScheduledTasksAt", now);
            var reminderTextBox =
                GetField<TextBox>(
                    reminderWindow,
                    "ReminderContentTextBox");
            Assert(visibleOccurrences.Count == 3 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 3 &&
                   reminderTextBox.Text.Split(
                       recurring.Text,
                       StringSplitOptions.None).Length - 1 == 3,
                "未确认的一分钟循环任务在三次到点后必须按时间堆叠3条，同一时间重复处理不得重复");

            var stackedDueTimes = visibleOccurrences
                .Cast<object>()
                .Select(occurrence =>
                    GetProperty<DateTimeOffset>(
                        occurrence,
                        "DueAt"))
                .ToArray();
            Assert(stackedDueTimes.SequenceEqual(
                       new[]
                       {
                           firstDueAt,
                           firstDueAt.AddMinutes(1),
                           firstDueAt.AddMinutes(2)
                        }),
                "循环提醒必须按每次计划时间严格升序堆叠");

            var reminderRestoreScale =
                GetField<double>(window, "_reminderRestoreScale");
            var reminderCloseButton =
                GetField<Button>(reminderWindow, "ReminderCloseButton");
            reminderCloseButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            reminderCloseButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(!GetField<bool>(window, "_isReminderActive") &&
                   !GetField<bool>(
                       window,
                       "_isReminderPresentationDismissed") &&
                   GetRawField(window, "_activeReminder") is null &&
                   activeBatch.Count == 0 &&
                   visibleOccurrences.Count == 0 &&
                   observedCounts.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 0 &&
                   scheduledTasks.Count == 1 &&
                   recurring.DueAt == firstDueAt.AddMinutes(3) &&
                   recurring.RepeatInterval == TimeSpan.FromMinutes(1) &&
                   scheduledStore.Load().Single().DueAt ==
                       firstDueAt.AddMinutes(3) &&
                   !reminderWindow.IsVisible &&
                   !GetField<bool>(reminderWindow, "_hasClosed") &&
                   reminderTextBox.Text.Length == 0 &&
                   todoWindow.IsVisible &&
                   scheduledInput.Text ==
                       "正在编辑，提醒不得关闭或清空" &&
                   GetRawField(
                       todoWindow,
                       "_editorInterruptedByReminder") is null &&
                   ReferenceEquals(
                       GetRawField(
                           todoWindow,
                           "_taskTextEditWindow"),
                       interruptedTodoEditor) &&
                   interruptedTodoEditor.IsVisible &&
                   interruptedTodoEditorText.Text ==
                       "提醒期间未保存的待办草稿" &&
                   Math.Abs(interruptedTodoEditor.Width - 430) <= 0.1 &&
                   Math.Abs(interruptedTodoEditor.Height - 470) <= 0.1 &&
                   interruptedTodoEditor.IsKeyboardFocusWithin &&
                   GetField<object>(
                       window,
                       "_bubbleMode").ToString() == "Todo" &&
                   GetField<bool>(
                       window,
                       "_isTransientPetSizeOverride") &&
                   GetField<bool>(
                       window,
                       "_isRestoringReminderSize") &&
                   scheduledTimer.IsEnabled &&
                   dismissRequestCount == 1,
                "连续点击提醒右上角关闭按钮必须只处理一次，清空已显示旧提醒、推进循环并保留Todo草稿和尺寸恢复");

            Invoke(window, "ProcessScheduledTasksAt", now);
            Assert(!GetField<bool>(window, "_isReminderActive") &&
                   !GetField<bool>(
                       window,
                       "_isReminderPresentationDismissed") &&
                   GetRawField(window, "_activeReminder") is null &&
                   visibleOccurrences.Count == 0 &&
                   observedCounts.Count == 0 &&
                   !reminderWindow.IsVisible &&
                   reminderTextBox.Text.Length == 0 &&
                   dismissRequestCount == 1,
                "关闭并清空提醒后对同一时刻重复调度不得重弹，也不得在隐藏窗口重新滞留旧文本");

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Assert(!GetField<bool>(
                       window,
                       "_isTransientPetSizeOverride") &&
                   !GetField<bool>(
                       window,
                       "_isRestoringReminderSize") &&
                   Math.Abs(
                       GetField<double>(window, "_petSizeScale") -
                       reminderRestoreScale) < 0.001,
                "只关闭提醒显示也必须完成尺寸恢复并解除临时放大状态");

            now = firstDueAt.AddMinutes(3);
            Invoke(window, "ProcessScheduledTasksAt", now);
            Assert(GetField<bool>(window, "_isReminderActive") &&
                   !GetField<bool>(
                       window,
                       "_isReminderPresentationDismissed") &&
                   visibleOccurrences.Count == 1 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 1 &&
                   GetProperty<DateTimeOffset>(
                       visibleOccurrences[0]!,
                       "DueAt") == firstDueAt.AddMinutes(3) &&
                   reminderTextBox.Text.Split(
                       recurring.Text,
                       StringSplitOptions.None).Length - 1 == 1 &&
                   reminderWindow.IsVisible &&
                   scheduledInput.Text ==
                       "正在编辑，提醒不得关闭或清空",
                "下一次新的循环 occurrence 到点后只能显示这一条，关闭前的3条旧提醒不得回流");

            reminderWindow.Close();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(!GetField<bool>(window, "_isReminderActive") &&
                   !GetField<bool>(
                       window,
                       "_isReminderPresentationDismissed") &&
                   GetRawField(window, "_activeReminder") is null &&
                   activeBatch.Count == 0 &&
                   visibleOccurrences.Count == 0 &&
                   observedCounts.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   !reminderWindow.IsVisible &&
                   !GetField<bool>(reminderWindow, "_hasClosed") &&
                   scheduledTasks.Count == 1 &&
                   scheduledStore.Load().Single().DueAt ==
                       firstDueAt.AddMinutes(4) &&
                   recurring.RepeatInterval == TimeSpan.FromMinutes(1) &&
                   dismissRequestCount == 2,
                "系统关闭请求也必须清空当前已显示提醒、推进循环并保留可复用窗口实例");

            Invoke(window, "ProcessScheduledTasksAt", now);
            Assert(!GetField<bool>(window, "_isReminderActive") &&
                   visibleOccurrences.Count == 0 &&
                   observedCounts.Count == 0 &&
                   !reminderWindow.IsVisible,
                "系统关闭并清空后同一 occurrence 不得因轮询立即重弹");

            now = firstDueAt.AddMinutes(4);
            Invoke(window, "ProcessScheduledTasksAt", now);
            var effectivePetSizeTarget =
                GetField<bool>(window, "_petSizeTargetUpdatePending")
                    ? GetField<double>(
                        window,
                        "_pendingPetSizeTargetScale")
                    : GetField<double>(window, "_petSizeTargetScale");
            Assert(GetField<bool>(window, "_isReminderActive") &&
                   !GetField<bool>(
                       window,
                       "_isReminderPresentationDismissed") &&
                   visibleOccurrences.Count == 1 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 1 &&
                   GetProperty<DateTimeOffset>(
                       visibleOccurrences[0]!,
                       "DueAt") == firstDueAt.AddMinutes(4) &&
                   reminderTextBox.Text.Split(
                       recurring.Text,
                       StringSplitOptions.None).Length - 1 == 1 &&
                   reminderWindow.IsVisible &&
                   GetField<bool>(
                       window,
                       "_isTransientPetSizeOverride") &&
                   !GetField<bool>(
                       window,
                       "_isRestoringReminderSize") &&
                   Math.Abs(effectivePetSizeTarget - 1.40d) < 0.001,
                "关闭旧提醒后新的 occurrence 必须单独显示；尺寸尚在恢复时要取消缩小并重新平滑放大");

            now = firstDueAt.AddMinutes(103);
            Invoke(window, "ProcessScheduledTasksAt", now);
            reminderWindow.UpdateLayout();
            var reminderPagingPanel = GetField<Border>(
                reminderWindow,
                "ReminderPagingPanel");
            var reminderPreviousPageButton = GetField<Button>(
                reminderWindow,
                "ReminderPreviousPageButton");
            var reminderPageText = GetField<TextBlock>(
                reminderWindow,
                "ReminderPageText");
            var reminderNextPageButton = GetField<Button>(
                reminderWindow,
                "ReminderNextPageButton");
            var reminderAcknowledgeButton = GetField<Button>(
                reminderWindow,
                "ReminderAcknowledgeButton");
            var countText =
                GetField<TextBlock>(
                    reminderWindow,
                    "ReminderCountText").Text;
            Assert(visibleOccurrences.Count == 100 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 100 &&
                   !countText.Contains("另有", StringComparison.Ordinal) &&
                   GetProperty<DateTimeOffset>(
                       visibleOccurrences[0]!,
                       "DueAt") == firstDueAt.AddMinutes(4) &&
                   GetProperty<DateTimeOffset>(
                       visibleOccurrences[99]!,
                       "DueAt") == firstDueAt.AddMinutes(103) &&
                   reminderPagingPanel.Visibility == Visibility.Visible &&
                   reminderPageText.Text.Contains(
                       "1 / 20",
                       StringComparison.Ordinal) &&
                   !reminderPreviousPageButton.IsEnabled &&
                   reminderNextPageButton.IsEnabled &&
                   reminderTextBox.Text.Split(
                       recurring.Text,
                       StringSplitOptions.None).Length - 1 == 5 &&
                   reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(4).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal) &&
                   reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(8).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal) &&
                   !reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(9).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal),
                "100条提醒必须保留完整批次但第一页严格只显示最早5条，共20页；" +
                "第一页必须禁用上一页并启用下一页");

            reminderPreviousPageButton.ApplyTemplate();
            reminderNextPageButton.ApplyTemplate();
            reminderAcknowledgeButton.ApplyTemplate();
            var previousPageChrome =
                reminderPreviousPageButton.Template.FindName(
                    "PageButtonBackground",
                    reminderPreviousPageButton) as Border;
            var nextPageChrome =
                reminderNextPageButton.Template.FindName(
                    "PageButtonBackground",
                    reminderNextPageButton) as Border;
            var acknowledgeChrome =
                reminderAcknowledgeButton.Template.FindName(
                    "ButtonBackground",
                    reminderAcknowledgeButton) as Border;
            Assert(reminderPagingPanel.Background is SolidColorBrush
                       pagingBackground &&
                   pagingBackground.Color ==
                       Color.FromRgb(0xFF, 0xF6, 0xEC) &&
                   reminderPagingPanel.BorderBrush is SolidColorBrush
                       pagingBorder &&
                   pagingBorder.Color ==
                       Color.FromRgb(0xF3, 0xC8, 0x9F) &&
                   reminderPagingPanel.CornerRadius.TopLeft == 19 &&
                   previousPageChrome?.Background is SolidColorBrush
                       previousBackground &&
                   previousBackground.Color ==
                       Color.FromRgb(0xFF, 0xF8, 0xF1) &&
                   nextPageChrome?.Background is SolidColorBrush
                       nextBackground &&
                   nextBackground.Color ==
                       Color.FromRgb(0xFF, 0xF1, 0xE2) &&
                   nextPageChrome.BorderBrush is SolidColorBrush
                       nextBorder &&
                   nextBorder.Color ==
                       Color.FromRgb(0xF0, 0xB4, 0x77) &&
                   reminderPageText.Foreground is SolidColorBrush
                       pageTextBrush &&
                   pageTextBrush.Color ==
                       Color.FromRgb(0xB9, 0x63, 0x26) &&
                   reminderPageText.FontFamily.Source ==
                       "Microsoft YaHei" &&
                   acknowledgeChrome?.Background is SolidColorBrush
                       acknowledgeBackground &&
                   acknowledgeBackground.Color ==
                       Color.FromRgb(0xF2, 0xA0, 0x52) &&
                   acknowledgeChrome.BorderBrush is SolidColorBrush
                       acknowledgeBorder &&
                   acknowledgeBorder.Color ==
                       Color.FromRgb(0xD9, 0x84, 0x35),
                "定时提醒分页、页码和确认按钮必须使用圆润暖橘萌系样式；" +
                "禁用与可用翻页按钮也要有明确且一致的橘色层级");

            var pagingActiveClip = GetRawField(window, "_activeClip");
            var pagingClipStartedTimestamp = GetField<long>(
                window,
                "_activeClipStartedTimestamp");
            var pagingFrameDeadlineTimestamp = GetField<long>(
                window,
                "_activeFrameDeadlineTimestamp");
            var pagingFrameIndex = GetField<int>(
                window,
                "_activeFrameIndex");
            var pagingOccurrenceSnapshot = visibleOccurrences
                .Cast<object>()
                .Select(occurrence => (
                    TaskId: GetProperty<Guid>(occurrence, "TaskId"),
                    DueAt: GetProperty<DateTimeOffset>(occurrence, "DueAt"),
                    OccurrenceOffset:
                        GetProperty<long>(occurrence, "OccurrenceOffset")))
                .ToArray();
            var pagingTotalCount = GetField<long>(
                window,
                "_totalReminderOccurrenceCount");
            var pagingDueAt = recurring.DueAt;
            var persistedPagingDueAt =
                scheduledStore.Load().Single().DueAt;
            var pagingWindowLeft = reminderWindow.Left;
            var pagingWindowTop = reminderWindow.Top;
            var pagingWindowHeight = reminderWindow.Height;
            Assert(pagingWindowHeight > 0 &&
                   pagingWindowHeight <= 468.1,
                "分页提醒窗口必须使用不超过468 DIP的固定紧凑高度");

            for (var pageIndex = 1; pageIndex < 20; pageIndex++)
            {
                reminderNextPageButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
            }

            var pagingOccurrenceAfterNavigation = visibleOccurrences
                .Cast<object>()
                .Select(occurrence => (
                    TaskId: GetProperty<Guid>(occurrence, "TaskId"),
                    DueAt: GetProperty<DateTimeOffset>(occurrence, "DueAt"),
                    OccurrenceOffset:
                        GetProperty<long>(occurrence, "OccurrenceOffset")))
                .ToArray();
            Assert(reminderPageText.Text.Contains(
                       "20 / 20",
                       StringComparison.Ordinal) &&
                   reminderPreviousPageButton.IsEnabled &&
                   !reminderNextPageButton.IsEnabled &&
                   reminderTextBox.Text.Split(
                       recurring.Text,
                       StringSplitOptions.None).Length - 1 == 5 &&
                   reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(99).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal) &&
                   reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(103).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal) &&
                   !reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(98).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal) &&
                   ReferenceEquals(
                       GetRawField(window, "_activeClip"),
                       pagingActiveClip) &&
                   GetField<long>(
                       window,
                       "_activeClipStartedTimestamp") ==
                       pagingClipStartedTimestamp &&
                   GetField<long>(
                       window,
                       "_activeFrameDeadlineTimestamp") ==
                       pagingFrameDeadlineTimestamp &&
                   GetField<int>(
                       window,
                       "_activeFrameIndex") == pagingFrameIndex &&
                   pagingOccurrenceAfterNavigation.SequenceEqual(
                       pagingOccurrenceSnapshot) &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") ==
                       pagingTotalCount &&
                   recurring.DueAt == pagingDueAt &&
                   scheduledStore.Load().Single().DueAt ==
                       persistedPagingDueAt &&
                   Math.Abs(reminderWindow.Left - pagingWindowLeft) < 0.1 &&
                   Math.Abs(reminderWindow.Top - pagingWindowTop) < 0.1 &&
                   Math.Abs(
                       reminderWindow.Height - pagingWindowHeight) < 0.1,
                "翻到第20页必须严格显示第96至100条并禁用下一页；" +
                "翻页只能切换正文，不能重播提醒动画、改变occurrence、推进截止时间或让窗口跳动");

            now = firstDueAt.AddMinutes(105);
            Invoke(window, "ProcessScheduledTasksAt", now);
            countText = GetField<TextBlock>(
                reminderWindow,
                "ReminderCountText").Text;
            Assert(visibleOccurrences.Count == 100 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 102 &&
                   countText.Contains("另有 2 条", StringComparison.Ordinal) &&
                   reminderPageText.Text.Contains(
                       "20 / 20",
                       StringComparison.Ordinal) &&
                   !reminderNextPageButton.IsEnabled &&
                   reminderTextBox.Text.Split(
                       recurring.Text,
                       StringSplitOptions.None).Length - 1 == 5,
                "100条批次外新增2条只能进入overflow，不能把当前批次错误扩成21页；" +
                "追加提醒时应保留用户正在查看的有效页");
            var reminderVerticalScrollBar =
                FindVisualDescendants<ScrollBar>(reminderWindow)
                    .FirstOrDefault(scrollBar =>
                        scrollBar.Orientation == Orientation.Vertical);
            reminderVerticalScrollBar?.ApplyTemplate();
            var reminderScrollTrack =
                reminderVerticalScrollBar?.Template.FindName(
                    "PART_Track",
                    reminderVerticalScrollBar) as Track;
            reminderScrollTrack?.Thumb.ApplyTemplate();
            Assert(reminderScrollTrack?.Thumb.Template.FindName(
                       "ReminderThumbPill",
                       reminderScrollTrack.Thumb) is Border reminderThumbPill &&
                   reminderThumbPill.CornerRadius.TopLeft == 5 &&
                   reminderThumbPill.Background is SolidColorBrush
                       reminderThumbBrush &&
                   reminderThumbBrush.Color ==
                       Color.FromRgb(0xF3, 0xA4, 0x5E),
                "定时提醒长内容的滑动块必须使用带高光的橘色圆润萌系滑块，不能退回系统蓝色滑块");

            now = firstDueAt.AddMinutes(4);
            // Earlier dismiss checks intentionally retain their double-click
            // guard until the dispatcher reaches Input priority.  A physical
            // next click can only arrive after that turn; mirror that ordering
            // before synthesizing the later page-20 close in the same test.
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(!GetField<bool>(reminderWindow, "_dismissRequestPending"),
                "新一批提醒可交互前必须释放上一批的关闭防重入门禁");
            reminderCloseButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            // The full suite may still be draining deferred WPF work from the
            // preceding window/cache checks.  Give the dismiss request one
            // bounded dispatcher turn instead of assuming an otherwise idle
            // queue; the reminder-only run normally completes much sooner.
            PumpDispatcher(TimeSpan.FromMilliseconds(120));
            Assert(GetField<bool>(window, "_isReminderActive") &&
                   visibleOccurrences.Count == 2 &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 2 &&
                   recurring.DueAt == firstDueAt.AddMinutes(104) &&
                   todoWindow.IsVisible &&
                   reminderWindow.IsVisible &&
                   dismissRequestCount == 3 &&
                   reminderPagingPanel.Visibility ==
                       Visibility.Collapsed &&
                   reminderPageText.Text.Contains(
                       "1 / 1",
                       StringComparison.Ordinal) &&
                   !reminderPreviousPageButton.IsEnabled &&
                   !reminderNextPageButton.IsEnabled &&
                   reminderTextBox.Text.Split(
                       recurring.Text,
                       StringSplitOptions.None).Length - 1 == 2 &&
                   reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(104).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal) &&
                   reminderTextBox.Text.Contains(
                       firstDueAt.AddMinutes(105).ToLocalTime()
                           .ToString("M月d日 HH:mm:ss"),
                       StringComparison.Ordinal),
                "在第20页关闭也必须一次清空整个100条批次；剩余2条应立即回到第一页，" +
                "隐藏分页栏且不能丢失overflow；实际 " +
                $"active={GetField<bool>(window, "_isReminderActive")}, " +
                $"visible={visibleOccurrences.Count}, " +
                $"total={GetField<long>(window, "_totalReminderOccurrenceCount")}, " +
                $"due={recurring.DueAt:O}, " +
                $"todo={todoWindow.IsVisible}, reminder={reminderWindow.IsVisible}, " +
                $"dismiss={dismissRequestCount}, paging={reminderPagingPanel.Visibility}, " +
                $"page={reminderPageText.Text}, items=" +
                $"{reminderTextBox.Text.Split(recurring.Text, StringSplitOptions.None).Length - 1}");

            reminderAcknowledgeButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var persisted = scheduledStore.Load().Single();
            Assert(!GetField<bool>(window, "_isReminderActive") &&
                   visibleOccurrences.Count == 0 &&
                   recurring.DueAt == firstDueAt.AddMinutes(106) &&
                   persisted.DueAt == recurring.DueAt &&
                   recurring.DueAt > now &&
                   todoWindow.IsVisible &&
                   !reminderWindow.IsVisible &&
                   GetField<object>(
                       window,
                       "_bubbleMode").ToString() == "Todo" &&
                   scheduledTimer.IsEnabled,
                "确认剩余2条后才可推进到首个未来周期；关闭操作不能丢失未显示提醒，Todo必须保持原样打开");
        }
        finally
        {
            scheduledTimer.Stop();
            automaticTimer.Stop();
            reminderSizeTimer.Stop();
            GetField<DispatcherTimer>(
                window,
                "_petSizePersistTimer").Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            activeBatch.Clear();
            visibleOccurrences.Clear();
            observedCounts.Clear();
            scheduledStore.Save(scheduledTasks);
            interruptedTodoEditor?.CloseWithoutSaving();
            if (observedReminderWindow is not null &&
                dismissObserver is not null)
            {
                observedReminderWindow.DismissRequested -= dismissObserver;
            }

            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_isReminderPresentationDismissed", false);
            SetField(window, "_totalReminderOccurrenceCount", 0L);
            SetField(window, "_upcomingReminderPreloadPageName", null);
            SetField(window, "_isTransientPetSizeOverride", false);
            SetField(window, "_isRestoringReminderSize", false);
            if (GetRawField(window, "_reminderWindow") is
                ReminderWindow reminderWindow)
            {
                reminderWindow.HideSafely();
            }

            Invoke(
                window,
                "SetBubbleMode",
                GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "ClearDeferredActiveClipClock");
            Invoke(window, "ResetPetVisualTransforms");
            Invoke(
                window,
                "ApplyPetSizeScale",
                originalScale,
                false,
                false);
            SetField(window, "_nowProvider", originalNowProvider);
            SetField(
                window,
                "_automaticAnimationEnabled",
                originalAutomaticEnabled);
            if (originalAutomaticEnabled && window.IsVisible)
            {
                Invoke(window, "RestartAutomaticCountdown");
            }

            window.IsHitTestVisible = originalHitTestVisible;
        }
    }

    private static void AssertScheduledQuietHoursRuntimeContract(
        MainWindow window)
    {
        var scheduledTasks =
            GetField<ObservableCollection<ScheduledTaskItem>>(
                window,
                "_scheduledTasks");
        var reminderQueue =
            GetField<Queue<ScheduledTaskItem>>(window, "_reminderQueue");
        var queuedReminderIds =
            GetField<HashSet<Guid>>(window, "_queuedReminderIds");
        var activeBatch =
            GetField<List<ScheduledTaskItem>>(
                window,
                "_activeReminderBatch");
        var visibleOccurrences =
            (IList)GetRawField(window, "_visibleReminderOccurrences")!;
        var observedCounts =
            (IDictionary)GetRawField(
                window,
                "_presentedReminderOccurrenceCounts")!;
        var scheduledStore =
            GetField<ScheduledTaskStore>(window, "_scheduledTaskStore");
        var scheduledTimer =
            GetField<DispatcherTimer>(window, "_scheduledTaskTimer");
        var automaticTimer =
            GetField<DispatcherTimer>(window, "_automaticTimer");
        var reminderSizeTimer =
            GetField<DispatcherTimer>(
                window,
                "_reminderSizeCommitTimer");
        var originalNowProvider =
            GetField<Func<DateTimeOffset>>(window, "_nowProvider");
        var originalAutomaticEnabled =
            GetField<bool>(window, "_automaticAnimationEnabled");
        var originalScale = GetField<double>(window, "_petSizeScale");
        var originalHitTestVisible = window.IsHitTestVisible;
        var now = DateTimeOffset.Now;
        Func<DateTimeOffset> controlledNow = () => now;
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(
            "China Standard Time");

        static DateTimeOffset ResolveLocal(
            TimeZoneInfo zone,
            DateTime local) =>
            new(
                DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
                zone.GetUtcOffset(local));

        void ResetReminderState()
        {
            scheduledTimer.Stop();
            reminderSizeTimer.Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            activeBatch.Clear();
            visibleOccurrences.Clear();
            observedCounts.Clear();
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(
                window,
                "_isReminderPresentationDismissed",
                false);
            SetField(window, "_totalReminderOccurrenceCount", 0L);
            SetField(window, "_upcomingReminderPreloadPageName", null);
            SetField(window, "_isTransientPetSizeOverride", false);
            SetField(window, "_isRestoringReminderSize", false);
            if (GetRawField(window, "_reminderWindow") is
                ReminderWindow existingReminder)
            {
                existingReminder.HideSafely();
                existingReminder.ClearPresentation();
            }

            Invoke(
                window,
                "SetBubbleMode",
                GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "ClearDeferredActiveClipClock");
            Invoke(window, "ResetPetVisualTransforms");
            Invoke(
                window,
                "ApplyPetSizeScale",
                originalScale,
                false,
                false);
            Assert(scheduledStore.Save(scheduledTasks),
                "The quiet-hours fixture must reset its isolated store.");
        }

        void AssertSilentState(
            ScheduledTaskItem task,
            DateTimeOffset expectedNextDueAt,
            DateTimeOffset expectedQuietEnd,
            string stage)
        {
            var persisted = scheduledStore.Load().Single(
                item => item.Id == task.Id);
            var reminderWindow =
                (ReminderWindow?)GetRawField(window, "_reminderWindow");
            var persistedRuleIsConsistent =
                task.RepeatRule is null
                    ? persisted.RepeatRule is null
                    : persisted.RepeatRule?.NextOrdinal ==
                          task.RepeatRule.NextOrdinal &&
                      ScheduledRepeatSchedule.TryGetOccurrence(
                          persisted.RepeatRule,
                          persisted.RepeatRule.NextOrdinal,
                          out var persistedOccurrence) &&
                      persistedOccurrence == persisted.DueAt;
            Assert(GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   activeBatch.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   visibleOccurrences.Count == 0 &&
                   observedCounts.Count == 0 &&
                   GetField<object>(window, "_bubbleMode").ToString() !=
                       "Reminder" &&
                   !GetField<bool>(
                       window,
                       "_isTransientPetSizeOverride") &&
                   reminderWindow?.IsVisible != true &&
                   task.DueAt == expectedNextDueAt &&
                   persisted.DueAt == expectedNextDueAt &&
                   persisted.QuietHours == task.QuietHours &&
                   persistedRuleIsConsistent &&
                   (DateTimeOffset)Invoke(
                       window,
                       "FindNextReminderDueAt",
                       now)! == expectedQuietEnd &&
                   scheduledTimer.IsEnabled,
                $"{stage}: quiet recurring occurrences must be skipped " +
                "without UI, animation, queue state, or later replay.");
        }

        try
        {
            scheduledTimer.Stop();
            automaticTimer.Stop();
            reminderSizeTimer.Stop();
            SetField(window, "_automaticAnimationEnabled", false);
            SetField(window, "_nowProvider", controlledNow);
            window.IsHitTestVisible = false;
            ResetReminderState();

            var quietStartLocal = new DateTime(
                2034,
                6,
                12,
                22,
                0,
                0,
                DateTimeKind.Unspecified);
            var quietEndLocal = quietStartLocal.AddHours(9);
            var quietStart = ResolveLocal(timeZone, quietStartLocal);
            var quietEnd = ResolveLocal(timeZone, quietEndLocal);
            Assert(
                ScheduledRepeatSchedule.TryCreate(
                    ScheduledRepeatUnit.Hour,
                    1,
                    quietStartLocal,
                    timeZone,
                    out var repeatRule,
                    out var firstDueAt) &&
                repeatRule is not null &&
                firstDueAt == quietStart,
                "The cross-midnight quiet-hours fixture needs a valid rule.");
            var quietHours = new ScheduledQuietHours
            {
                Start = TimeSpan.FromHours(22),
                End = TimeSpan.FromHours(7),
                TimeZoneId = timeZone.Id
            };
            var crossMidnight = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "44000000-0000-0000-0000-000000000101"),
                Text = "cross-midnight quiet recurring reminder",
                DueAt = firstDueAt,
                CreatedAt = quietStart.AddDays(-1),
                RepeatInterval = TimeSpan.FromHours(1),
                RepeatRule = repeatRule,
                QuietHours = quietHours
            };
            Invoke(window, "InsertScheduledTaskSorted", crossMidnight);
            Assert(scheduledStore.Save(scheduledTasks),
                "The cross-midnight task must be persisted first.");

            var quietProbes = new[]
            {
                (Local: quietStartLocal, Next: quietStartLocal.AddHours(1)),
                (Local: quietStartLocal.AddHours(1),
                    Next: quietStartLocal.AddHours(2)),
                (Local: quietStartLocal.AddHours(2),
                    Next: quietStartLocal.AddHours(3)),
                (Local: quietStartLocal.AddHours(4),
                    Next: quietStartLocal.AddHours(5)),
                (Local: quietEndLocal.AddTicks(-TimeSpan.TicksPerSecond),
                    Next: quietEndLocal)
            };
            foreach (var probe in quietProbes)
            {
                now = ResolveLocal(timeZone, probe.Local);
                Invoke(
                    window,
                    "ScheduledTaskTimer_Tick",
                    null,
                    EventArgs.Empty);
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                AssertSilentState(
                    crossMidnight,
                    ResolveLocal(timeZone, probe.Next),
                    quietEnd,
                    $"quiet probe {probe.Local:yyyy-MM-dd HH:mm:ss}");

                var dueBeforeDuplicateTick = crossMidnight.DueAt;
                Invoke(
                    window,
                    "ScheduledTaskTimer_Tick",
                    null,
                    EventArgs.Empty);
                Assert(crossMidnight.DueAt == dueBeforeDuplicateTick &&
                       GetRawField(window, "_activeReminder") is null,
                    "Repeating the same quiet tick must be idempotent.");
            }

            now = quietEnd;
            Invoke(
                window,
                "ScheduledTaskTimer_Tick",
                null,
                EventArgs.Empty);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       crossMidnight) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   activeBatch.SequenceEqual([crossMidnight]) &&
                   visibleOccurrences.Count == 1 &&
                   observedCounts[crossMidnight.Id] is 1L &&
                   crossMidnight.DueAt == quietEnd,
                "The end boundary is exclusive: only the exact 07:00 " +
                "occurrence may appear, never the skipped quiet backlog.");
            Invoke(window, "AcknowledgeActiveReminder");
            var nextAfterAcknowledge = quietEnd.AddHours(1);
            var persistedAfterAcknowledge = scheduledStore.Load().Single();
            Assert(crossMidnight.DueAt == nextAfterAcknowledge &&
                   crossMidnight.RepeatRule?.NextOrdinal == 10 &&
                   crossMidnight.QuietHours == quietHours &&
                   persistedAfterAcknowledge.DueAt ==
                       nextAfterAcknowledge &&
                   persistedAfterAcknowledge.RepeatRule?.NextOrdinal == 10 &&
                   persistedAfterAcknowledge.QuietHours == quietHours,
                "Acknowledging the end-boundary occurrence must continue " +
                "from the authored recurrence anchor and retain quiet hours.");

            ResetReminderState();
            var boundaryStartLocal = new DateTime(
                2034,
                6,
                13,
                22,
                0,
                0,
                DateTimeKind.Unspecified);
            var boundaryEndLocal = boundaryStartLocal.AddHours(1);
            var boundaryStart = ResolveLocal(timeZone, boundaryStartLocal);
            var boundaryEnd = ResolveLocal(timeZone, boundaryEndLocal);
            var preQuietDueAt = boundaryStart.AddMinutes(-10);
            var boundaryQuietHours = new ScheduledQuietHours
            {
                Start = TimeSpan.FromHours(22),
                End = TimeSpan.FromHours(23),
                TimeZoneId = timeZone.Id
            };
            var preQuietActive = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "44000000-0000-0000-0000-000000000102"),
                Text = "unacknowledged reminder before quiet hours",
                DueAt = preQuietDueAt,
                CreatedAt = preQuietDueAt.AddDays(-1),
                RepeatInterval = TimeSpan.FromMinutes(30),
                QuietHours = boundaryQuietHours
            };
            Invoke(window, "InsertScheduledTaskSorted", preQuietActive);
            Assert(scheduledStore.Save(scheduledTasks),
                "The pre-quiet legacy task must be persisted first.");
            now = preQuietDueAt;
            Invoke(window, "ProcessScheduledTasksAt", now);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       preQuietActive) &&
                   visibleOccurrences.Count == 1 &&
                   observedCounts[preQuietActive.Id] is 1L,
                "A recurring occurrence before quiet hours must initially " +
                "remain visible.");

            var normalAtQuietStart = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "44000000-0000-0000-0000-000000000103"),
                Text = "one-shot reminder at the quiet boundary",
                DueAt = boundaryStart,
                CreatedAt = boundaryStart.AddMinutes(-1)
            };
            Invoke(window, "InsertScheduledTaskSorted", normalAtQuietStart);
            Assert(scheduledStore.Save(scheduledTasks),
                "The mixed quiet-boundary tasks must be persisted first.");
            now = boundaryStart;
            Invoke(
                window,
                "ScheduledTaskTimer_Tick",
                null,
                EventArgs.Empty);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var mixedReminderWindow =
                (ReminderWindow?)GetRawField(window, "_reminderWindow");
            var mixedReminderContent = mixedReminderWindow is null
                ? null
                : GetField<TextBox>(
                    mixedReminderWindow,
                    "ReminderContentTextBox");
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       normalAtQuietStart) &&
                   activeBatch.SequenceEqual([normalAtQuietStart]) &&
                   observedCounts[preQuietActive.Id] is 1L &&
                   visibleOccurrences.Count == 1 &&
                   mixedReminderWindow?.IsVisible == true &&
                   mixedReminderContent?.Text.Contains(
                       normalAtQuietStart.Text,
                       StringComparison.Ordinal) == true &&
                   mixedReminderContent.Text.Contains(
                       preQuietActive.Text,
                       StringComparison.Ordinal) == false,
                "Quiet hours must suspend the older recurring reminder while " +
                "a same-instant one-shot reminder remains fully visible.");
            Invoke(window, "AcknowledgeActiveReminder");
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            if (GetRawField(window, "_activeClip") is { } reminderExitClip)
            {
                Invoke(
                    window,
                    "CompleteActiveClipAt",
                    reminderExitClip,
                    Stopwatch.GetTimestamp());
            }

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Assert(GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   activeBatch.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   visibleOccurrences.Count == 0 &&
                   observedCounts[preQuietActive.Id] is 1L &&
                   GetField<object>(window, "_bubbleMode").ToString() !=
                       "Reminder" &&
                   !GetField<bool>(
                       window,
                       "_isTransientPetSizeOverride") &&
                   mixedReminderWindow?.IsVisible != true &&
                   preQuietActive.DueAt == preQuietDueAt,
                "After acknowledging the one-shot reminder, the pre-quiet " +
                "backlog must stay preserved but completely hidden. " +
                $"active={GetRawField(window, "_activeReminder") is not null}, " +
                $"reminderActive={GetField<bool>(window, "_isReminderActive")}, " +
                $"batch={activeBatch.Count}, queue={reminderQueue.Count}, " +
                $"queued={queuedReminderIds.Count}, visible={visibleOccurrences.Count}, " +
                $"observed={observedCounts.Count}/" +
                $"{observedCounts[preQuietActive.Id]}, " +
                $"bubble={GetField<object>(window, "_bubbleMode")}, " +
                $"transient={GetField<bool>(window, "_isTransientPetSizeOverride")}, " +
                $"windowVisible={mixedReminderWindow?.IsVisible}, " +
                $"due={preQuietActive.DueAt:O}");

            now = boundaryEnd;
            Invoke(
                window,
                "ScheduledTaskTimer_Tick",
                null,
                EventArgs.Empty);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       preQuietActive) &&
                   visibleOccurrences.Count == 1 &&
                   observedCounts[preQuietActive.Id] is 1L &&
                   GetProperty<DateTimeOffset>(
                       visibleOccurrences[0]!,
                       "DueAt") == preQuietDueAt,
                "At the quiet end, the older non-quiet reminder must return " +
                "without either quiet occurrence being replayed.");
            var wakeWhilePreQuietReminderIsUnacknowledged =
                (DateTimeOffset)Invoke(
                    window,
                    "FindNextReminderDueAt",
                    now)!;
            Assert(wakeWhilePreQuietReminderIsUnacknowledged ==
                       boundaryStart.AddDays(1) &&
                   wakeWhilePreQuietReminderIsUnacknowledged > now &&
                   scheduledTimer.Interval > TimeSpan.FromMilliseconds(1),
                "A past quiet occurrence blocked behind the restored reminder " +
                "must not create a one-millisecond dispatcher retry loop.");
            Invoke(window, "AcknowledgeActiveReminder");
            var expectedAfterQuietPrefix = boundaryEnd.AddMinutes(20);
            var persistedAfterQuietPrefix = scheduledStore.Load().Single();
            Assert(preQuietActive.DueAt == expectedAfterQuietPrefix &&
                   persistedAfterQuietPrefix.DueAt ==
                       expectedAfterQuietPrefix &&
                   persistedAfterQuietPrefix.QuietHours ==
                       boundaryQuietHours &&
                   GetRawField(window, "_activeReminder") is null,
                "Acknowledging the pre-quiet reminder must skip the 22:20 and " +
                "22:50 occurrences and continue at 23:20.");

            ResetReminderState();
            var offlineAcrossQuiet = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "44000000-0000-0000-0000-000000000104"),
                Text = "offline across non-quiet and quiet occurrences",
                DueAt = preQuietDueAt,
                CreatedAt = preQuietDueAt.AddDays(-1),
                RepeatInterval = TimeSpan.FromMinutes(30),
                QuietHours = new ScheduledQuietHours
                {
                    Start = TimeSpan.FromHours(22),
                    End = TimeSpan.FromHours(7),
                    TimeZoneId = timeZone.Id
                }
            };
            Invoke(window, "InsertScheduledTaskSorted", offlineAcrossQuiet);
            Assert(scheduledStore.Save(scheduledTasks),
                "The offline mixed-prefix task must be persisted first.");
            now = ResolveLocal(
                timeZone,
                boundaryStartLocal.Date.AddDays(1).AddHours(8));
            Invoke(window, "ProcessSystemTimeChanged");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       offlineAcrossQuiet) &&
                   visibleOccurrences.Count == 1 &&
                   GetProperty<DateTimeOffset>(
                       visibleOccurrences[0]!,
                       "DueAt") == preQuietDueAt &&
                   offlineAcrossQuiet.DueAt == preQuietDueAt,
                "Recovery after a full quiet interval must first preserve the " +
                "older non-quiet occurrence instead of dropping it.");
            var offlineWakeWhileOldReminderIsUnacknowledged =
                (DateTimeOffset)Invoke(
                    window,
                    "FindNextReminderDueAt",
                    now)!;
            Assert(offlineWakeWhileOldReminderIsUnacknowledged ==
                       boundaryStart.AddDays(1) &&
                   offlineWakeWhileOldReminderIsUnacknowledged > now &&
                   scheduledTimer.Interval > TimeSpan.FromMilliseconds(1),
                "Offline recovery must not busy-loop on the old quiet prefix " +
                "while the non-quiet head awaits acknowledgement.");
            Invoke(window, "ProcessSystemTimeChanged");
            Assert(visibleOccurrences.Count == 1 &&
                   offlineAcrossQuiet.DueAt == preQuietDueAt,
                "Repeated recovery at the same instant must be idempotent.");
            Invoke(window, "AcknowledgeActiveReminder");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var expectedPostQuietOccurrences = new[]
            {
                boundaryStart.AddHours(9).AddMinutes(20),
                boundaryStart.AddHours(9).AddMinutes(50)
            };
            var actualPostQuietOccurrences = visibleOccurrences
                .Cast<object>()
                .Select(occurrence => GetProperty<DateTimeOffset>(
                    occurrence,
                    "DueAt"))
                .ToArray();
            Assert(actualPostQuietOccurrences.SequenceEqual(
                       expectedPostQuietOccurrences) &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 2,
                "After confirming the pre-quiet backlog, recovery must skip " +
                "all 22:00-07:00 occurrences and show only 07:20 and 07:50.");
            Invoke(window, "AcknowledgeActiveReminder");
            var nextAfterOfflineRecovery =
                boundaryStart.AddHours(10).AddMinutes(20);
            Assert(offlineAcrossQuiet.DueAt == nextAfterOfflineRecovery &&
                   scheduledStore.Load().Single().DueAt ==
                       nextAfterOfflineRecovery,
                "Confirming the post-quiet backlog must resume the authored " +
                "30-minute sequence at 08:20.");

            ResetReminderState();
            var cappedBacklogDueAt = ResolveLocal(
                timeZone,
                boundaryStartLocal.Date.AddDays(2).AddHours(19));
            var ordinaryBacklog = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "44000000-0000-0000-0000-000000000105"),
                Text = "ordinary large recurring backlog",
                DueAt = cappedBacklogDueAt,
                CreatedAt = cappedBacklogDueAt.AddDays(-1),
                RepeatInterval = TimeSpan.FromMinutes(1),
                QuietHours = new ScheduledQuietHours
                {
                    Start = TimeSpan.FromHours(22),
                    End = TimeSpan.FromHours(23),
                    TimeZoneId = timeZone.Id
                }
            };
            Invoke(window, "InsertScheduledTaskSorted", ordinaryBacklog);
            Assert(scheduledStore.Save(scheduledTasks),
                "The ordinary large-backlog task must be persisted first.");
            now = cappedBacklogDueAt.AddMinutes(120);
            Invoke(window, "ProcessScheduledTasksAt", now);
            var cappedBacklogWake =
                (DateTimeOffset)Invoke(
                    window,
                    "FindNextReminderDueAt",
                    now)!;
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       ordinaryBacklog) &&
                   GetField<long>(
                       window,
                       "_totalReminderOccurrenceCount") == 100 &&
                   cappedBacklogWake == cappedBacklogDueAt.AddHours(3) &&
                   cappedBacklogWake > now &&
                   scheduledTimer.Interval > TimeSpan.FromMilliseconds(1),
                "A full 100-entry visible page with quiet hours must wait for " +
                "a real future event instead of polling its overdue 101st entry.");
            Invoke(window, "AcknowledgeActiveReminder");
            Assert(GetField<long>(
                   window,
                       "_totalReminderOccurrenceCount") == 21 &&
                   ordinaryBacklog.DueAt ==
                       cappedBacklogDueAt.AddMinutes(100),
                "Confirming the first 100 visible entries must expose the " +
                "remaining 21 without losing the authored minute offsets.");
        }
        finally
        {
            scheduledTimer.Stop();
            automaticTimer.Stop();
            reminderSizeTimer.Stop();
            GetField<DispatcherTimer>(
                window,
                "_petSizePersistTimer").Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            activeBatch.Clear();
            visibleOccurrences.Clear();
            observedCounts.Clear();
            scheduledStore.Save(scheduledTasks);
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(
                window,
                "_isReminderPresentationDismissed",
                false);
            SetField(window, "_totalReminderOccurrenceCount", 0L);
            SetField(window, "_upcomingReminderPreloadPageName", null);
            SetField(window, "_isTransientPetSizeOverride", false);
            SetField(window, "_isRestoringReminderSize", false);
            if (GetRawField(window, "_reminderWindow") is
                ReminderWindow reminderWindow)
            {
                reminderWindow.HideSafely();
                reminderWindow.ClearPresentation();
            }

            Invoke(
                window,
                "SetBubbleMode",
                GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "ClearDeferredActiveClipClock");
            Invoke(window, "ResetPetVisualTransforms");
            Invoke(
                window,
                "ApplyPetSizeScale",
                originalScale,
                false,
                false);
            SetField(window, "_nowProvider", originalNowProvider);
            SetField(
                window,
                "_automaticAnimationEnabled",
                originalAutomaticEnabled);
            if (originalAutomaticEnabled && window.IsVisible)
            {
                Invoke(window, "RestartAutomaticCountdown");
            }

            window.IsHitTestVisible = originalHitTestVisible;
        }
    }

    private static void AssertScheduledReminderBatchContract(MainWindow window)
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
        var activeBatch = GetField<List<ScheduledTaskItem>>(
            window,
            "_activeReminderBatch");
        var scheduledStore = GetField<ScheduledTaskStore>(
            window,
            "_scheduledTaskStore");
        var scheduledTimer = GetField<DispatcherTimer>(
            window,
            "_scheduledTaskTimer");
        var automaticTimer = GetField<DispatcherTimer>(
            window,
            "_automaticTimer");
        var reminderSizeTimer = GetField<DispatcherTimer>(
            window,
            "_reminderSizeCommitTimer");
        var originalNowProvider = GetField<Func<DateTimeOffset>>(
            window,
            "_nowProvider");
        var originalAutomaticEnabled = GetField<bool>(
            window,
            "_automaticAnimationEnabled");
        var originalScale = GetField<double>(window, "_petSizeScale");
        var originalHitTestVisible = window.IsHitTestVisible;
        var now = new DateTimeOffset(
            2032,
            6,
            7,
            8,
            9,
            10,
            TimeSpan.FromHours(8));
        Func<DateTimeOffset> controlledNow = () => now;

        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var processSource = ExtractPrivateMethodSource(
            mainSource,
            "ProcessScheduledTasksAt");
        var refreshSource = ExtractPrivateMethodSource(
            mainSource,
            "RefreshActiveReminderPresentation");
        var moveSource = ExtractPrivateMethodSource(
            mainSource,
            "MovePetToReminderCorner");
        var acknowledgeSource = ExtractPrivateMethodSource(
            mainSource,
            "AcknowledgeActiveReminder");
        var rebuildQueueSource = ExtractPrivateMethodSource(
            mainSource,
            "RebuildReminderQueueAt");
        var systemTimeChangedSource = ExtractPrivateMethodSource(
            mainSource,
            "ProcessSystemTimeChanged");
        Assert(processSource.Contains(
                   "RefreshActiveReminderPresentation(now)",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_activeReminderBatch.Sort(CompareScheduledTasks)",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "string.Join(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "Environment.NewLine",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "M月d日 HH:mm:ss",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_reminderMissedOccurrenceCounts.TryAdd(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_reminderMissedOccurrenceCounts.GetValueOrDefault(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_activeReminderBatch.RemoveAll(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "ReminderAcknowledgeButton.Content",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "StopEdgeRoaming(",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "immediate: true",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "workArea.Right - width",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "workArea.Bottom - height",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "_activeReminderBatch.ToArray()",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "foreach (var item in acknowledged)",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "_reminderMissedOccurrenceCounts.Remove(item.Id)",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "SaveScheduledTasks()",
                   StringComparison.Ordinal) &&
               rebuildQueueSource.Contains(
                   "foreach (var displayedItem in _activeReminderBatch)",
                   StringComparison.Ordinal) &&
               systemTimeChangedSource.Contains(
                   "ProcessScheduledTasksAt(_nowProvider())",
                   StringComparison.Ordinal),
            "到点提醒必须抢占绕屏、移动到右下角，并把同批任务稳定排序合并到一个泡泡和一次确认中");

        scheduledTimer.Stop();
        automaticTimer.Stop();
        reminderSizeTimer.Stop();
        scheduledTasks.Clear();
        reminderQueue.Clear();
        queuedReminderIds.Clear();
        activeBatch.Clear();
        SetField(window, "_activeReminder", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_upcomingReminderPreloadPageName", null);
        SetField(window, "_nowProvider", controlledNow);
        SetField(window, "_automaticAnimationEnabled", false);
        window.IsHitTestVisible = false;

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
            }

            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "HideBubbleVisuals");
            SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));

            var firstByDue = new ScheduledTaskItem
            {
                Id = Guid.Parse("41000000-0000-0000-0000-000000000003"),
                Text = "最早到点",
                DueAt = now.AddSeconds(-2),
                CreatedAt = now.AddMinutes(-1)
            };
            var firstByCreated = new ScheduledTaskItem
            {
                Id = Guid.Parse("41000000-0000-0000-0000-000000000002"),
                Text = "同秒先创建",
                DueAt = now.AddSeconds(-1),
                CreatedAt = now.AddMinutes(-3)
            };
            var firstById = new ScheduledTaskItem
            {
                Id = Guid.Parse("41000000-0000-0000-0000-000000000001"),
                Text = "同秒按编号",
                DueAt = now.AddSeconds(-1),
                CreatedAt = now.AddMinutes(-3)
            };
            foreach (var item in new[]
                     {
                         firstByCreated,
                         firstByDue,
                         firstById
                     })
            {
                Invoke(window, "InsertScheduledTaskSorted", item);
            }

            var expectedOrder = new[]
            {
                firstByDue,
                firstById,
                firstByCreated
            };
            Assert(scheduledTasks.SequenceEqual(expectedOrder) &&
                   scheduledStore.Save(scheduledTasks),
                "提醒合并测试数据必须先按 DueAt、CreatedAt、Id 建立稳定顺序");

            var monitorType = typeof(MainWindow).Assembly.GetType(
                "LubanDesktopPet.MonitorWorkArea",
                throwOnError: true)!;
            var workArea = (Rect)InvokeStatic(
                monitorType,
                "GetForWindow",
                window)!;
            window.Left = workArea.Left +
                          Math.Max(10, workArea.Width * 0.2);
            window.Top = workArea.Top +
                         Math.Max(10, workArea.Height * 0.2);
            Invoke(window, "ApplyPetSizeScale", 1d, false, false);
            var sizePreviewTimestamp = Stopwatch.GetTimestamp();
            Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
            Invoke(
                window,
                "QueuePetSizeScaleTargetAt",
                1.18d,
                sizePreviewTimestamp);
            Invoke(
                window,
                "ConsumeLatestPetSizeInputAt",
                sizePreviewTimestamp +
                StopwatchTicksFromMilliseconds(1));
            Assert(GetField<bool>(
                       window,
                       "_isPetSizePreviewSessionActive"),
                "提醒抢占回归必须先建立一个尚未提交的尺寸预览会话");
            SetField(window, "_isEdgeRoaming", true);
            SetField(
                window,
                "_edgeRoamPhase",
                GetNestedEnum("EdgeRoamPhase", "Traveling"));
            SetField(window, "_edgeRoamRotationDegrees", 90d);
            GetField<RotateTransform>(window, "PetRoamRotate").Angle = 90;

            Invoke(window, "ProcessScheduledTasksAt", now);
            var reminderMessage = GetField<TextBox>(
                window,
                "ReminderMessageText");
            var acknowledgeButton = GetField<Button>(
                window,
                "ReminderAcknowledgeButton");
            var expectedMessage = string.Join(
                Environment.NewLine,
                expectedOrder.Select(item =>
                    $"{item.DueAt.ToLocalTime():M月d日 HH:mm:ss}  {item.Text}"));
            var reminderPetBounds = (Rect)Invoke(
                window,
                "GetPetViewboxBoundsInScreenDips")!;
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       expectedOrder[0]) &&
                   activeBatch.SequenceEqual(expectedOrder) &&
                   reminderQueue.SequenceEqual(expectedOrder.Skip(1)) &&
                   queuedReminderIds.SetEquals(
                       expectedOrder.Select(item => item.Id)) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   !GetField<bool>(
                       window,
                       "_isPetSizeAdjustmentActive") &&
                   GetField<bool>(
                       window,
                       "_isTransientPetSizeOverride") &&
                   Math.Abs(
                       GetField<double>(window, "_reminderRestoreScale") -
                       1.18d) < 0.001 &&
                   !GetField<bool>(window, "_isEdgeRoaming") &&
                   GetField<object>(window, "_edgeRoamPhase").ToString() ==
                       "None" &&
                   Math.Abs(
                       GetField<RotateTransform>(
                           window,
                           "PetRoamRotate").Angle) < 0.001 &&
                   GetField<object>(window, "_bubbleMode").ToString() ==
                       "Reminder" &&
                   reminderMessage.Text == expectedMessage &&
                   reminderMessage.IsReadOnly &&
                   acknowledgeButton.Content?.ToString()?.Contains(
                       expectedOrder.Length.ToString(
                           CultureInfo.InvariantCulture),
                       StringComparison.Ordinal) == true &&
                   Math.Abs(reminderPetBounds.Right - workArea.Right) <= 1 &&
                   Math.Abs(reminderPetBounds.Bottom - workArea.Bottom) <= 1,
                "三个到点任务必须抢占绕屏、清零旋转、移到当前屏幕右下角，并按稳定顺序合并为一个可复制泡泡；" +
                $"active={GetRawField(window, "_activeReminder") is not null}, " +
                $"batch={activeBatch.Count}, queue={reminderQueue.Count}, ids={queuedReminderIds.Count}, " +
                $"reminder={GetField<bool>(window, "_isReminderActive")}, " +
                $"preview={GetField<bool>(window, "_isPetSizePreviewSessionActive")}, " +
                $"roam={GetField<bool>(window, "_isEdgeRoaming")}, " +
                $"phase={GetField<object>(window, "_edgeRoamPhase")}, " +
                $"angle={GetField<RotateTransform>(window, "PetRoamRotate").Angle:F2}, " +
                $"bubble={GetField<object>(window, "_bubbleMode")}, " +
                $"textMatch={reminderMessage.Text == expectedMessage}, " +
                $"button={acknowledgeButton.Content}, " +
                $"petBounds={reminderPetBounds}, " +
                $"targetRightBottom=({workArea.Right:F2},{workArea.Bottom:F2})");

            var frozenTexts = expectedOrder
                .Select(item => item.Text)
                .ToArray();
            var frozenDueTimes = expectedOrder
                .Select(item => item.DueAt)
                .ToArray();
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                expectedOrder[1],
                "不允许覆盖已展示批次",
                now.AddHours(1),
                null,
                null,
                null);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskDeleteRequested",
                expectedOrder[0]);
            var expectedAfterDelete = expectedOrder.Skip(1).ToArray();
            var expectedMessageAfterDelete = string.Join(
                Environment.NewLine,
                expectedAfterDelete.Select(item =>
                    $"{item.DueAt.ToLocalTime():M月d日 HH:mm:ss}  {item.Text}"));
            Assert(scheduledTasks.SequenceEqual(expectedAfterDelete) &&
                   activeBatch.SequenceEqual(expectedAfterDelete) &&
                   ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       expectedAfterDelete[0]) &&
                   expectedAfterDelete.Select(item => item.Text)
                       .SequenceEqual(frozenTexts.Skip(1)) &&
                   expectedAfterDelete.Select(item => item.DueAt)
                       .SequenceEqual(frozenDueTimes.Skip(1)) &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual(expectedAfterDelete.Select(item => item.Id)) &&
                   queuedReminderIds.SetEquals(
                       expectedAfterDelete.Select(item => item.Id)) &&
                   reminderMessage.Text == expectedMessageAfterDelete,
                "进入可见或排队提醒批次后，修改和删除都必须冻结，不能让气泡、队列与磁盘状态分裂");

            reminderMessage.SelectAll();
            Assert(reminderMessage.SelectedText == expectedMessageAfterDelete,
                "合并提醒泡泡的全部文本必须支持一次选中复制");

            now = now.AddMinutes(-10);
            Invoke(window, "ProcessSystemTimeChanged");
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       expectedAfterDelete[0]) &&
                   activeBatch.SequenceEqual(expectedAfterDelete) &&
                   queuedReminderIds.SetEquals(
                       expectedAfterDelete.Select(item => item.Id)) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   reminderMessage.Text == expectedMessageAfterDelete,
                "系统时间回拨到批次到点前，已经展示的合并提醒不得消失、拆批或重新排序");

            Invoke(window, "AcknowledgeActiveReminder");
            Assert(GetRawField(window, "_activeReminder") is null &&
                   activeBatch.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   scheduledTasks.Count == 0 &&
                   scheduledStore.Load().Count == 0 &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   GetField<object>(window, "_bubbleMode").ToString() ==
                       "None",
                "一次确认必须原子删除合并批次的全部任务，只保存一次并关闭唯一提醒泡泡");

            now = new DateTimeOffset(
                2032,
                6,
                10,
                12,
                0,
                0,
                TimeSpan.FromHours(8));
            var recurringInterval = TimeSpan.FromHours(6);
            var missedRecurringDueAt = now.AddHours(-55);
            var missedRecurring = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "42000000-0000-0000-0000-000000000001"),
                Text = "重启后继续的循环提醒",
                DueAt = missedRecurringDueAt,
                CreatedAt = missedRecurringDueAt.AddDays(-1),
                RepeatInterval = recurringInterval
            };
            Invoke(window, "InsertScheduledTaskSorted", missedRecurring);
            Assert(scheduledStore.Save(scheduledTasks),
                "漏提醒循环回归必须先把原锚点和周期持久化");
            Invoke(window, "ProcessScheduledTasksAt", now);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       missedRecurring) &&
                   activeBatch.SequenceEqual([missedRecurring]) &&
                   reminderMessage.Text.Contains(
                       "每6小时",
                       StringComparison.Ordinal) &&
                   reminderMessage.Text.Contains(
                       "已错过 10 次",
                       StringComparison.Ordinal),
                "重启加载到逾期循环任务时必须只弹一次，并显示从原锚点计算的漏提醒次数");

            var expectedNextDueAt =
                missedRecurringDueAt.AddHours(60);
            Invoke(window, "AcknowledgeActiveReminder");
            var persistedRecurring = scheduledStore.Load().Single();
            Assert(scheduledTasks.Count == 1 &&
                   ReferenceEquals(scheduledTasks[0], missedRecurring) &&
                   missedRecurring.DueAt == expectedNextDueAt &&
                   missedRecurring.DueAt > now &&
                   missedRecurring.RepeatInterval == recurringInterval &&
                   persistedRecurring.Id == missedRecurring.Id &&
                   persistedRecurring.DueAt == expectedNextDueAt &&
                   persistedRecurring.RepeatInterval == recurringInterval &&
                   GetRawField(window, "_activeReminder") is null &&
                   activeBatch.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   scheduledTimer.IsEnabled,
                "确认漏提醒后必须从原到期锚点一次跨过全部遗漏周期，持久化首个未来时间且不补播历史提醒");

        }
        finally
        {
            scheduledTimer.Stop();
            automaticTimer.Stop();
            reminderSizeTimer.Stop();
            GetField<DispatcherTimer>(window, "_petSizePersistTimer").Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            activeBatch.Clear();
            scheduledStore.Save(scheduledTasks);
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_upcomingReminderPreloadPageName", null);
            SetField(window, "_isEdgeRoaming", false);
            SetField(
                window,
                "_edgeRoamPhase",
                GetNestedEnum("EdgeRoamPhase", "None"));
            SetField(window, "_isTransientPetSizeOverride", false);
            SetField(window, "_isRestoringReminderSize", false);
            Invoke(window, "HideBubbleVisuals");
            SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "ClearDeferredActiveClipClock");
            Invoke(window, "ResetPetVisualTransforms");
            Invoke(window, "ApplyPetSizeScale", originalScale, false, false);
            SetField(window, "_nowProvider", originalNowProvider);
            SetField(
                window,
                "_automaticAnimationEnabled",
                originalAutomaticEnabled);
            if (originalAutomaticEnabled && window.IsVisible)
            {
                Invoke(window, "RestartAutomaticCountdown");
            }

            window.IsHitTestVisible = originalHitTestVisible;
        }
    }

    private static void AssertScheduledReminderContract(MainWindow window)
    {
        const double baselineScale = 1.13;
        const double maximumScale = 1.40;
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
        var settingsStore = GetField<AppSettingsStore>(window, "_settingsStore");
        var scheduledTimer = GetField<DispatcherTimer>(
            window,
            "_scheduledTaskTimer");
        var reminderSizeTimer = GetField<DispatcherTimer>(
            window,
            "_reminderSizeCommitTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var originalNowProvider = GetField<Func<DateTimeOffset>>(
            window,
            "_nowProvider");
        var originalAutomaticAnimationEnabled = GetField<bool>(
            window,
            "_automaticAnimationEnabled");
        var reminderPreloadLeadTime = (TimeSpan)(typeof(MainWindow).GetField(
                "ReminderSpritePreloadLeadTime",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var originalHitTestVisible = window.IsHitTestVisible;
        var currentNow = new DateTimeOffset(
            2026,
            7,
            22,
            12,
            0,
            0,
            TimeSpan.FromHours(8));
        Func<DateTimeOffset> controlledNow = () => currentNow;

        scheduledTimer.Stop();
        reminderSizeTimer.Stop();
        automaticTimer.Stop();
        scheduledTasks.Clear();
        reminderQueue.Clear();
        queuedReminderIds.Clear();
        SetField(window, "_activeReminder", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_upcomingReminderPreloadPageName", null);
        SetField(window, "_nowProvider", controlledNow);
        SetField(window, "_automaticAnimationEnabled", false);
        window.IsHitTestVisible = false;
        Assert(scheduledStore.Save(scheduledTasks),
            "提醒回归必须使用临时 ScheduledTaskStore");

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
            }

            automaticTimer.Stop();
            SetField(window, "_automaticAnimationEnabled", false);
            Invoke(window, "ApplyPetSizeScale", baselineScale, true, false);
            AssertClose(
                settingsStore.Load().PetSizeScale,
                baselineScale,
                "定时提醒前的用户尺寸设置");

            var reminderEnterClip = GetField<object>(window, "_reminderEnterClip");
            var reminderHoldClip = GetField<object>(window, "_reminderHoldClip");
            var reminderExitClip = GetField<object>(window, "_reminderExitClip");
            var enterFrames = GetClipFrames(reminderEnterClip);
            var holdFrames = GetClipFrames(reminderHoldClip);
            var exitFrames = GetClipFrames(reminderExitClip);
            Assert(GetProperty<string>(reminderEnterClip, "ActionName") ==
                   "reminder-open" &&
                   GetProperty<string>(reminderHoldClip, "ActionName") ==
                   "reminder-hold" &&
                   GetProperty<string>(reminderExitClip, "ActionName") ==
                   "reminder-close" &&
                   !ReferenceEquals(reminderEnterClip, reminderExitClip) &&
                   !ReferenceEquals(reminderEnterClip, reminderHoldClip) &&
                   !ReferenceEquals(reminderHoldClip, reminderExitClip) &&
                   enterFrames.Length == 33 &&
                   holdFrames.Length == 48 &&
                   exitFrames.Length == 33 &&
                   GetProperty<int>(reminderEnterClip, "ActionFrameIndex") == 0 &&
                   GetProperty<int>(reminderHoldClip, "ActionFrameIndex") == 0 &&
                   GetProperty<int>(reminderExitClip, "ActionFrameIndex") == 0,
                "定时提醒必须使用独立的33帧入场、48帧播报保持和33帧退场clip");
            var motionFrameInterval = (TimeSpan)(
                typeof(MainWindow).GetField(
                    "MotionFrameInterval",
                    StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
            Assert(motionFrameInterval == TimeSpan.FromTicks(
                       TimeSpan.TicksPerSecond / 60) &&
                   reminderPreloadLeadTime == TimeSpan.FromSeconds(2) &&
                   enterFrames.Cast<object>()
                        .All(frame =>
                            GetFrameDuration(frame) == motionFrameInterval) &&
                   holdFrames.Cast<object>()
                        .All(frame =>
                            GetFrameDuration(frame) == motionFrameInterval) &&
                   exitFrames.Cast<object>()
                        .All(frame =>
                            GetFrameDuration(frame) == motionFrameInterval),
                "提醒入场、播报保持和退场必须统一使用60fps绝对时间帧时长，" +
                "并只在到期前2秒按需预热");

            for (var frameIndex = 0; frameIndex < enterFrames.Length; frameIndex++)
            {
                var enterImage = GetProperty<object>(
                    enterFrames.GetValue(frameIndex)!,
                    "Image");
                var exitImage = GetProperty<object>(
                    exitFrames.GetValue(exitFrames.Length - 1 - frameIndex)!,
                    "Image");
                var enterInfo = GetSpriteFrameInfo(enterImage);
                Assert(enterInfo.PageName.StartsWith(
                           "action-reminder-enter",
                           StringComparison.Ordinal) &&
                       enterInfo.Name.EndsWith(
                           $"luban-reminder-enter-{frameIndex + 1:000}.png",
                           StringComparison.Ordinal) &&
                       Equals(enterImage, exitImage),
                    $"提醒入场第{frameIndex + 1}帧必须来自专用action-reminder-enter序列，" +
                    "退场必须直接复用同一SpriteFrame的倒序，不能复制第二套图或混入wave");
            }

            for (var frameIndex = 0; frameIndex < holdFrames.Length; frameIndex++)
            {
                var holdInfo = GetSpriteFrameInfo(GetProperty<object>(
                    holdFrames.GetValue(frameIndex)!,
                    "Image"));
                Assert(holdInfo.PageName.StartsWith(
                           "action-reminder-hold",
                           StringComparison.Ordinal) &&
                       holdInfo.Name.EndsWith(
                           $"luban-reminder-hold-{frameIndex + 1:000}.png",
                           StringComparison.Ordinal),
                    $"提醒播报第{frameIndex + 1}帧必须来自专用action-reminder-hold序列");
            }

            var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
            var mainXaml = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml"));
            var createReminderEnterSource = ExtractPrivateMethodSource(
                mainSource,
                "CreateReminderEnterClip");
            var createReminderHoldSource = ExtractPrivateMethodSource(
                mainSource,
                "CreateReminderHoldClip");
            var createReminderExitSource = ExtractPrivateMethodSource(
                mainSource,
                "CreateReminderExitClip");
            foreach (var reminderClipSource in new[]
                     {
                         createReminderEnterSource,
                         createReminderHoldSource,
                         createReminderExitSource
                     })
            {
                Assert(!reminderClipSource.Contains("BuildActionTimeline", StringComparison.Ordinal) &&
                       !reminderClipSource.Contains("\"wave\"", StringComparison.Ordinal),
                    "专用提醒clip不得再次复用普通wave时间轴或wave素材");
            }

            Assert(createReminderExitSource.Contains(
                       "_reminderEnterFrames",
                       StringComparison.Ordinal) &&
                   createReminderExitSource.Contains(
                       "reverse: true",
                       StringComparison.Ordinal) &&
                   !mainSource.Contains("ReminderMotionFrameInterval", StringComparison.Ordinal) &&
                   !mainSource.Contains("ShowReminderMegaphoneAt", StringComparison.Ordinal) &&
                   !mainSource.Contains("AdvanceReminderMegaphoneAnimation", StringComparison.Ordinal) &&
                   !mainSource.Contains("_isReminderMegaphoneAnimating", StringComparison.Ordinal) &&
                   typeof(MainWindow).GetField(
                       "ReminderMegaphone",
                       InstanceFlags) is null &&
                   !mainXaml.Contains("ReminderMegaphone", StringComparison.Ordinal) &&
                   !mainXaml.Contains("MegaphonePulseScale", StringComparison.Ordinal) &&
                   !mainXaml.Contains("MegaphoneSoundWave", StringComparison.Ordinal),
                "喇叭必须烘焙进专用人物帧；不得保留独立矢量贴层、正弦漂浮动画或1/180秒旧时钟");

            AssertProductionDiscreteVsyncTimeline(
                window,
                reminderEnterClip,
                "reminder-open-60fps");
            AssertProductionDiscreteVsyncTimeline(
                window,
                reminderHoldClip,
                "reminder-hold-60fps");
            AssertProductionDiscreteVsyncTimeline(
                window,
                reminderExitClip,
                "reminder-close-60fps");

            var dueAt = currentNow.AddSeconds(10);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "同秒提醒甲",
                dueAt,
                null,
                null,
                null);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "同秒提醒乙",
                dueAt,
                null,
                null,
                null);
            Assert(scheduledTasks.Count == 2 &&
                   scheduledTasks.All(item => item.DueAt == dueAt) &&
                   GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive"),
                "两条同秒定时任务在到点前必须只持久化，不得提前提醒");
            Assert(scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(8))
                       .TotalMilliseconds) < 1,
                "距到期10秒时调度器必须先对准到期前2秒的预热点，不做高频轮询");
            Assert(GetRawField(window, "_upcomingReminderPreloadPageName") is null &&
                   (TimeSpan)InvokeStatic(
                       typeof(MainWindow),
                       "CalculateReminderWakeDelay",
                       currentNow,
                       dueAt)! == TimeSpan.FromSeconds(8) &&
                   (TimeSpan)InvokeStatic(
                       typeof(MainWindow),
                       "CalculateReminderWakeDelay",
                       dueAt.AddSeconds(-1.5),
                       dueAt)! == TimeSpan.FromSeconds(1.5) &&
                   (TimeSpan)InvokeStatic(
                       typeof(MainWindow),
                       "CalculateReminderWakeDelay",
                       currentNow,
                       currentNow.AddHours(13))! == TimeSpan.FromHours(12),
                "提醒唤醒延迟必须在2秒窗口外减去预热提前量、窗口内直达截止秒，" +
                "并保留12小时上限");
            var expectedSameSecondOrder = scheduledTasks.ToArray();
            Assert(expectedSameSecondOrder[0].CreatedAt <=
                   expectedSameSecondOrder[1].CreatedAt &&
                   (expectedSameSecondOrder[0].CreatedAt <
                        expectedSameSecondOrder[1].CreatedAt ||
                    expectedSameSecondOrder[0].Id.CompareTo(
                        expectedSameSecondOrder[1].Id) < 0),
                "同秒任务必须按 CreatedAt / Id 稳定排序");
            Assert(scheduledStore.Load().Select(item => item.Id)
                    .SequenceEqual(expectedSameSecondOrder.Select(item => item.Id)),
                "同秒任务的稳定顺序必须与磁盘持久化顺序一致");

            var reminderPreloadPageName = GetSpriteFrameInfo(
                GetProperty<object>(enterFrames.GetValue(0)!, "Image")).PageName;
            currentNow = dueAt - reminderPreloadLeadTime;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            WaitForSpritePagePrefetchToSettle(window);
            Assert(string.Equals(
                       GetField<string>(window, "_upcomingReminderPreloadPageName"),
                       reminderPreloadPageName,
                       StringComparison.Ordinal) &&
                   GetField<IDictionary>(window, "_residentSpritePages")
                       .Contains(reminderPreloadPageName) &&
                   (bool)Invoke(
                       window,
                       "IsSpritePageProtected",
                       reminderPreloadPageName,
                       null)! &&
                   !(bool)Invoke(window, "CanRunIdleSpritePageCollection")! &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - reminderPreloadLeadTime)
                       .TotalMilliseconds) < 1,
                "到期前2秒必须只预取提醒首屏分页、动态保护该页，" +
                "并阻止Gen2回收直至到期");

            currentNow = dueAt.AddTicks(-1);
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(GetRawField(window, "_activeReminder") is null &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   string.Equals(
                       GetField<string>(window, "_upcomingReminderPreloadPageName"),
                       reminderPreloadPageName,
                       StringComparison.Ordinal),
                "到点前 1 tick 仍不得触发定时任务，预热页保护必须保持到截止边界");

            currentNow = dueAt;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            var firstActive = GetField<ScheduledTaskItem>(
                window,
                "_activeReminder");
            Assert(ReferenceEquals(firstActive, expectedSameSecondOrder[0]) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   reminderQueue.Count == 1 &&
                   queuedReminderIds.Count == 2 &&
                   GetRawField(window, "_upcomingReminderPreloadPageName") is null &&
                   enterFrames.Cast<object>()
                       .Concat(holdFrames.Cast<object>())
                       .Select(frame => GetSpriteFrameInfo(
                           GetProperty<object>(frame, "Image")).PageName)
                       .Distinct(StringComparer.Ordinal)
                       .All(pageName => (bool)Invoke(
                           window,
                           "IsSpritePageProtected",
                           pageName,
                           null)!),
                "整秒边界必须立即显示稳定顺序的第一条，其余同秒任务只入队一次");
            Assert(GetRawField(window, "_activeClip") is { } activeReminderClip &&
                   ReferenceEquals(activeReminderClip, reminderEnterClip) &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Reminder",
                "到点后必须切换 BubbleMode.Reminder 并启动 reminder-open clip");

            var reminderBubble = GetField<Border>(window, "ReminderBubble");
            var reminderMessage = GetField<TextBox>(window, "ReminderMessageText");
            var reminderButton = GetField<Button>(
                window,
                "ReminderAcknowledgeButton");
            Assert(reminderBubble.Visibility == Visibility.Visible &&
                   GetField<Popup>(window, "BubblePopup").IsOpen &&
                   reminderMessage.Text == firstActive.Text &&
                   reminderMessage.IsReadOnly &&
                   Equals(reminderButton.Content, "知道啦"),
                "Reminder 模式必须显示可选中内容、可爱气泡和“知道啦”确认按钮");
            reminderMessage.SelectAll();
            Assert(reminderMessage.SelectedText == firstActive.Text,
                "提醒对话框内容必须可选中复制");
            reminderMessage.Select(0, 0);
            var firstReminderSprite = GetProperty<object>(
                enterFrames.GetValue(0)!,
                "Image");
            var activeReminderFrameIndex = GetField<int>(window, "_activeFrameIndex");
            var currentReminderSprite = GetRawField(window, "_currentSpriteFrame");
            var isReminderFrameBlending = GetField<bool>(window, "_isFrameBlending");
            var pendingReminderSprite = GetRawField(window, "_pendingSpriteFrame");
            var expectedVisibleReminderSprite =
                activeReminderFrameIndex is >= 0 and <= 1
                    ? GetProperty<object>(
                        enterFrames.GetValue(activeReminderFrameIndex)!,
                        "Image")
                    : null;
            Assert(activeReminderFrameIndex is >= 0 and <= 1 &&
                   Equals(currentReminderSprite, expectedVisibleReminderSprite) &&
                   !isReminderFrameBlending &&
                   pendingReminderSprite is null,
                "提醒到点后的首个可见姿势必须直接来自专用烘焙喇叭序列，" +
                "允许断言前合成器自然前进一帧，但不得跳过更多姿势、整图淡化、" +
                "叠加旧贴层或留下待补播帧；" +
                $"index={activeReminderFrameIndex}, " +
                $"firstMatches={Equals(currentReminderSprite, firstReminderSprite)}, " +
                $"currentMatches={Equals(currentReminderSprite, expectedVisibleReminderSprite)}, " +
                $"blending={isReminderFrameBlending}, " +
                $"pending={pendingReminderSprite is not null}");

            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       firstActive) &&
                   reminderQueue.Count == 1 &&
                   queuedReminderIds.Count == 2,
                "在同一整秒重复执行调度不得重复入队或覆盖正在显示的提醒");

            // The production clock calls PrefetchNextClipPage at each displayed
            // frame boundary. This focused test jumps directly to clip
            // completion, so synchronously prime the same four entry/hold pages
            // before asserting the 33 -> 48 transition. The earlier assertions
            // still prove these pages were neither pinned nor resident by
            // startup warm-up, while _isReminderActive dynamically protects
            // them from eviction during the reminder.
            PrimeAllClipPagesForTest(window, enterFrames);
            PrimeAllClipPagesForTest(window, holdFrames);
            var activeReminderPageNames = enterFrames.Cast<object>()
                .Concat(holdFrames.Cast<object>())
                .Select(frame => GetSpriteFrameInfo(
                    GetProperty<object>(frame, "Image")).PageName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert(activeReminderPageNames.Length == 4 &&
                   activeReminderPageNames.All(pageName =>
                       GetField<IDictionary>(window, "_residentSpritePages")
                           .Contains(pageName)) &&
                   activeReminderPageNames.All(pageName => (bool)Invoke(
                       window,
                       "IsSpritePageProtected",
                       pageName,
                       null)!),
                "模拟逐页PrefetchNextClipPage完成后，提醒33帧入场与48帧保持的四页" +
                "必须全部resident，并在活动提醒期间动态保护");

            Invoke(window, "CompleteActiveClip", reminderEnterClip);
            Assert(ReferenceEquals(
                       GetRawField(window, "_activeClip"),
                       reminderHoldClip) &&
                   GetField<int>(window, "_activeFrameIndex") == 0 &&
                   Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       GetProperty<object>(holdFrames.GetValue(0)!, "Image")),
                "33帧专用入场完成后必须无缝衔接48帧专用播报动作的第一帧");
            Invoke(window, "CompleteActiveClip", reminderHoldClip);
            Assert(GetRawField(window, "_activeClip") is null &&
                   GetField<int>(window, "_activeFrameIndex") == -1 &&
                   Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       GetProperty<object>(holdFrames.GetValue(holdFrames.Length - 1)!, "Image")) &&
                   !GetField<bool>(window, "_isFrameBlending"),
                "48帧播报完成后必须定格在烘焙喇叭末姿势，不得循环漂浮、闪回或整图淡化");

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            AssertClose(GetField<double>(window, "_petSizeScale"), maximumScale,
                "提醒触发后的临时最大显示尺寸");
            Assert(GetField<bool>(window, "_isTransientPetSizeOverride") &&
                   !GetField<DispatcherTimer>(window, "_petSizePersistTimer").IsEnabled,
                "定时提醒放大必须使用 transient override，不得启动用户设置落盘计时器");
            AssertClose(
                settingsStore.Load().PetSizeScale,
                baselineScale,
                "临时放大到 140% 时不得覆盖用户尺寸设置");

            Invoke(window, "AcknowledgeActiveReminder");
            var secondActive = GetField<ScheduledTaskItem>(
                window,
                "_activeReminder");
            var persistedAfterFirstAcknowledge = scheduledStore.Load();
            Assert(ReferenceEquals(secondActive, expectedSameSecondOrder[1]) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Reminder" &&
                   ReferenceEquals(GetRawField(window, "_activeClip"), reminderHoldClip) &&
                   scheduledTasks.Count == 1 &&
                   persistedAfterFirstAcknowledge.Count == 1 &&
                   persistedAfterFirstAcknowledge[0].Id == secondActive.Id,
                "确认第一条后必须只删除已确认任务并持久化，随即用专用hold动作显示第二条");
            AssertClose(GetField<double>(window, "_petSizeScale"), maximumScale,
                "同秒提醒队列未清空前应继续保持最大尺寸");

            Invoke(window, "AcknowledgeActiveReminder");
            Assert(GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   scheduledTasks.Count == 0 &&
                   scheduledStore.Load().Count == 0 &&
                   GetField<object>(window, "_bubbleMode").ToString() == "None" &&
                   ReferenceEquals(GetRawField(window, "_activeClip"), reminderExitClip),
                "最后一条确认后必须清空并持久化队列、关闭气泡并启动 reminder-close clip");
            Assert(GetField<bool>(window, "_isTransientPetSizeOverride") &&
                   GetField<bool>(window, "_isRestoringReminderSize") &&
                   Math.Abs(GetField<double>(window, "_pendingPetSizeTargetScale") -
                            baselineScale) < 0.0001,
                "最后一条确认后必须平滑返回提醒前的尺寸目标");
            Assert(Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       GetProperty<object>(exitFrames.GetValue(0)!, "Image")) &&
                   !GetField<bool>(window, "_isFrameBlending"),
                "reminder-close 必须从烘焙喇叭入场序列的末姿势直接倒放，不能闪回或整图淡化");

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            AssertClose(GetField<double>(window, "_petSizeScale"), baselineScale,
                "提醒队列清空后的最终显示尺寸");
            AssertClose(GetField<double>(window, "_petSizeTargetScale"), baselineScale,
                "提醒队列清空后的最终尺寸目标");
            Assert(!GetField<bool>(window, "_isTransientPetSizeOverride") &&
                   !GetField<bool>(window, "_isRestoringReminderSize"),
                "尺寸恢复完成后必须清理 transient override 状态");
            AssertClose(
                settingsStore.Load().PetSizeScale,
                baselineScale,
                "提醒完整结束后用户设置仍应保持原值");

            Invoke(window, "CompleteActiveClip", reminderExitClip);
            currentNow = dueAt.AddSeconds(30);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "应用恢复后的逾期提醒",
                currentNow.AddSeconds(-5),
                null,
                null,
                null);
            var overdueActive = GetField<ScheduledTaskItem>(
                window,
                "_activeReminder");
            Assert(overdueActive.Text == "应用恢复后的逾期提醒" &&
                   overdueActive.DueAt < currentNow &&
                   GetField<bool>(window, "_isReminderActive") &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Reminder",
                "新增或应用恢复时发现的逾期任务必须立即触发，不等下一个轮询周期");
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       overdueActive) &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 1,
                "逾期任务立即触发后重复校时也不得重复入队");
            Invoke(window, "AcknowledgeActiveReminder");
            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Assert(scheduledTasks.Count == 0 && scheduledStore.Load().Count == 0,
                "逾期提醒确认后也必须立即从内存和磁盘删除");
            AssertClose(GetField<double>(window, "_petSizeScale"), baselineScale,
                "逾期提醒结束后的恢复尺寸");

            Invoke(window, "CompleteActiveClip", reminderExitClip);
            currentNow = dueAt.AddMinutes(2);
            var rewindDueAt = currentNow.AddSeconds(10);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "回拨提醒甲",
                rewindDueAt,
                null,
                null,
                null);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "回拨提醒乙",
                rewindDueAt,
                null,
                null,
                null);
            var rewindOrder = scheduledTasks.ToArray();
            Assert(rewindOrder.Length == 2,
                "系统时间回拨回归必须准备两条同秒任务");

            currentNow = rewindDueAt;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       rewindOrder[0]) &&
                   reminderQueue.Count == 1 &&
                   queuedReminderIds.Count == 2,
                "回拨前应由第一条提醒占用气泡，第二条只进入待显示队列");

            currentNow = rewindDueAt.AddSeconds(-5);
            Invoke(window, "ProcessSystemTimeChanged");
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       rewindOrder[0]),
                "系统时间回拨不得撤销已经显示的第一条提醒");

            Invoke(window, "AcknowledgeActiveReminder");
            var persistedAfterRewindAcknowledge = scheduledStore.Load();
            Assert(GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   scheduledTasks.Count == 1 &&
                   scheduledTasks[0].Id == rewindOrder[1].Id &&
                   persistedAfterRewindAcknowledge.Count == 1 &&
                   persistedAfterRewindAcknowledge[0].Id == rewindOrder[1].Id &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(3))
                       .TotalMilliseconds) < 1,
                "回拨后确认第一条时，第二条不得提前显示，必须重新调度到原截止秒");

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Invoke(window, "CompleteActiveClip", reminderExitClip);

            currentNow = rewindDueAt;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       rewindOrder[1]) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.SetEquals([rewindOrder[1].Id]),
                "系统时间再次到达原截止秒后，第二条提醒必须正常显示且只触发一次");

            Invoke(window, "AcknowledgeActiveReminder");
            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Assert(scheduledTasks.Count == 0 && scheduledStore.Load().Count == 0,
                "回拨回归中的第二条提醒确认后必须正常清理内存和持久化数据");
        }
        finally
        {
            scheduledTimer.Stop();
            reminderSizeTimer.Stop();
            automaticTimer.Stop();
            GetField<DispatcherTimer>(window, "_petSizePersistTimer").Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            scheduledStore.Save(scheduledTasks);
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_upcomingReminderPreloadPageName", null);
            SetField(window, "_isTransientPetSizeOverride", false);
            SetField(window, "_isRestoringReminderSize", false);
            Invoke(window, "HideBubbleVisuals");
            SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "ClearDeferredActiveClipClock");
            Invoke(window, "ApplyPetSizeScale", baselineScale, false, false);
            SetField(window, "_nowProvider", originalNowProvider);
            SetField(
                window,
                "_automaticAnimationEnabled",
                originalAutomaticAnimationEnabled);
            if (originalAutomaticAnimationEnabled && window.IsVisible)
            {
                Invoke(window, "RestartAutomaticCountdown");
            }

            window.IsHitTestVisible = originalHitTestVisible;
        }
    }
}
