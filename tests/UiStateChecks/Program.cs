using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LubanDesktopPet;

internal static class Program
{
    private const int WakeFrameCount = 12;
    private const int ActionPoseFrameCount = 24;
    private const int ForwardFrameCount = WakeFrameCount + ActionPoseFrameCount;
    private const int ActionLoopStartPoseNumber = 21;
    private const int ActionLoopPoseCount = 4;
    private const int ActionLoopCycleCount = 10;
    private const int ActionLoopFrameCount = ActionLoopPoseCount * ActionLoopCycleCount;
    private const int ActionLoopStartFrameIndex = ForwardFrameCount;
    private const int ReverseFrameStartIndex = ForwardFrameCount + ActionLoopFrameCount;
    private const int MotionFrameCount = ForwardFrameCount + ActionLoopFrameCount + ForwardFrameCount;
    private const int ActionFrameIndex = ForwardFrameCount - 1;
    private const int EdgePeekFrameCount = 4;
    private const int RoamFrameCount = 4;
    private static readonly TimeSpan MotionFrameInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ActionLoopFrameInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan EdgePeekFrameInterval = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan RoamRenderInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RoamCornerTurnDuration = TimeSpan.FromMilliseconds(320);
    private static readonly string[] RoamAssetNames = ["wriggle", "crawl", "hop"];

    [STAThread]
    private static void Main()
    {
        _ = new Application();
        AssertLoggingContract();
        var window = new MainWindow
        {
            Left = 1000,
            Top = 700
        };

        var petImage = GetField<Image>(window, "PetImage");
        var petImageOverlay = GetField<Image>(window, "PetImageOverlay");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var petHost = GetField<Grid>(window, "PetHost");
        var cuteBubble = GetField<Border>(window, "CuteBubble");
        var todoBubble = GetField<Border>(window, "TodoBubble");
        var todoInput = GetField<TextBox>(window, "TodoInput");
        var bubbleColumn = GetField<ColumnDefinition>(window, "BubbleColumn");
        var gapColumn = GetField<ColumnDefinition>(window, "GapColumn");
        var bubbleHost = GetField<Grid>(window, "BubbleHost");
        var bubbleTailHost = GetField<Grid>(window, "BubbleTailHost");
        var bubblePopup = GetField<Popup>(window, "BubblePopup");
        var cuteMessageText = GetField<TextBlock>(window, "CuteMessageText");

        var expectedClips = new[]
        {
            new ExpectedClip("刚睡醒，让我伸个懒腰～", "yawn"),
            new ExpectedClip("呜……主人要哄哄我", "cry"),
            new ExpectedClip("小鲁班出发！", "run"),
            new ExpectedClip("给你卖个萌 ♡", "cute"),
            new ExpectedClip("主人真棒！", "like"),
            new ExpectedClip("吃块饼干，补充能量！", "eat"),
            new ExpectedClip("嗨～我在这里！", "wave"),
            new ExpectedClip("让我认真想一想……", "think")
        };
        var reactionClips = GetField<Array>(window, "_reactionClips");
        Assert(reactionClips.Length == expectedClips.Length,
            "应配置 8 个独立短动作 Clip");
        for (var clipIndex = 0; clipIndex < reactionClips.Length; clipIndex++)
        {
            AssertClipTiming(reactionClips.GetValue(clipIndex)!, expectedClips[clipIndex]);
        }
        AssertRealMotionFrames(reactionClips);
        AssertEdgeAndRoamAssets(window);
        AssertLegacyTransitionStateRemoved();
        Assert(automaticTimer.Interval == TimeSpan.FromSeconds(10),
            "自动动画计时器间隔应为 10 秒");
        Assert(!automaticTimer.IsEnabled,
            "窗口加载前不应启动自动动画计时器");

        AssertClose(window.Width, 145, "收起时宽度");
        AssertClose(window.Height, 185, "收起时高度");
        AssertImage(petImage, "luban-idle.png", "启动应显示待机图");
        AssertSingleLayerInvariant(petImage, petImageOverlay, "主窗口构造完成");
        Assert(RenderOptions.GetBitmapScalingMode(petImage) == BitmapScalingMode.HighQuality,
            "图片应使用高质量缩放");
        ArrangeWindow(window);
        RenderState(window, "idle.png");

        var initialRight = window.Left + window.Width;
        var initialBottom = window.Top + window.Height;

        var firstClip = expectedClips[0];
        AssertPropertyTransition(
            cuteBubble,
            UIElement.VisibilityProperty,
            () => Invoke(window, "ShowCuteReaction"),
            () => cuteBubble.Visibility == Visibility.Visible,
            () =>
            {
                AssertBubbleBounds(window, 145, 185, initialRight, initialBottom,
                    "卖萌气泡显示时宠物窗口应保持固定");
                Assert(bubbleHost.Visibility == Visibility.Visible,
                    "卖萌气泡显示时气泡容器应已可见");
                Assert(bubbleTailHost.Visibility == Visibility.Visible,
                    "卖萌气泡显示时气泡尾巴应已可见");
            },
            "卖萌气泡应在固定窗口外安全显示");
        ArrangeWindow(window);
        RenderState(window, "frame-transition.png");
        var firstClipFrames = BuildExpectedFrameNames(firstClip.ActionName);
        AssertActiveFrame(window, petImage, firstClipFrames[0], 0,
            "第一个短动作的首帧不正确");
        AssertSingleLayerSettled(petImage, petImageOverlay);
        Assert(frameTimer.Interval == MotionFrameInterval,
            "苏醒与动作进入帧应按 50ms 播放");
        Assert(cuteMessageText.Text == firstClip.Message, "第一个短动作对白不正确");
        Assert(cuteBubble.Visibility == Visibility.Visible, "单击后应显示卖萌对话气泡");
        Assert(IsPopupRequestedOpen(bubblePopup), "卖萌气泡应显示在独立 Popup 中");
        AssertClose(window.Width, 145, "卖萌气泡不应改变宠物窗口宽度");
        AssertClose(window.Height, 185, "卖萌气泡不应改变宠物窗口高度");
        AssertClose(window.Left + window.Width, initialRight, "卖萌气泡不应移动宠物窗口");
        AssertClose(window.Top + window.Height, initialBottom, "卖萌气泡不应移动宠物窗口底部");
        RenderState(window, "clip-1-frame-1.png");

        AssertBusyClickIgnored(window, petImage, petImageOverlay, frameTimer, cuteMessageText,
            "短动作播放期间");

        for (var frameIndex = 1; frameIndex < firstClipFrames.Length; frameIndex++)
        {
            AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                firstClipFrames[frameIndex], frameIndex, $"短动作 1 第 {frameIndex + 1} 帧");
            Assert(frameTimer.Interval == GetExpectedFrameDuration(frameIndex),
                $"短动作 1 第 {frameIndex + 1} 帧停留时长不正确");
        }

        AssertPropertyTransition(
            cuteBubble,
            UIElement.VisibilityProperty,
            () =>
            {
                Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
                frameTimer.Stop();
                AssertImage(petImage, "luban-idle.png", "短动作结束后应直接回到待机图");
                AssertSingleLayerInvariant(petImage, petImageOverlay, "返回待机");
            },
            () => cuteBubble.Visibility == Visibility.Collapsed,
            () =>
            {
                Assert(!IsPopupRequestedOpen(bubblePopup), "卖萌气泡收回后应关闭 Popup");
                AssertBubbleBounds(window, 145, 185, initialRight, initialBottom,
                    "卖萌气泡收回时宠物窗口应保持固定");
                AssertClose(gapColumn.Width.Value, 0, "卖萌气泡收回时间隔列宽");
                Assert(bubbleHost.Visibility == Visibility.Collapsed,
                    "卖萌气泡收回时气泡容器应已隐藏");
                Assert(bubbleTailHost.Visibility == Visibility.Collapsed,
                    "卖萌气泡收回时气泡尾巴应已隐藏");
            },
            "卖萌气泡应直接关闭 Popup，不裁剪或移动宠物窗口");

        AssertImage(petImage, "luban-idle.png", "第一个短动作结束后应回到待机图");
        AssertClose(window.Width, 145, "卖萌结束后应收起气泡");

        for (var clipIndex = 1; clipIndex < expectedClips.Length; clipIndex++)
        {
            var clip = expectedClips[clipIndex];
            var expectedFrames = BuildExpectedFrameNames(clip.ActionName);
            Invoke(window, "ShowCuteReaction");
            ArrangeWindow(window);
            Assert(cuteMessageText.Text == clip.Message, $"短动作 {clipIndex + 1} 对白不正确");
            AssertActiveFrame(window, petImage, expectedFrames[0], 0,
                $"短动作 {clipIndex + 1} 第 1 帧不正确");

            for (var frameIndex = 1; frameIndex < expectedFrames.Length; frameIndex++)
            {
                AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                    expectedFrames[frameIndex], frameIndex,
                    $"短动作 {clipIndex + 1} 第 {frameIndex + 1} 帧");
                Assert(frameTimer.Interval == GetExpectedFrameDuration(frameIndex),
                    $"短动作 {clipIndex + 1} 第 {frameIndex + 1} 帧停留时长不正确");
            }

            Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
            frameTimer.Stop();
            AssertImage(petImage, "luban-idle.png", $"短动作 {clipIndex + 1} 结束后应回到待机图");
            Assert(cuteBubble.Visibility == Visibility.Collapsed,
                $"短动作 {clipIndex + 1} 结束后应收起卖萌气泡");
            AssertClose(window.Width, 145, $"短动作 {clipIndex + 1} 结束后窗口宽度");
        }

        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = Mouse.MouseUpEvent,
            Source = petHost
        };
        AssertPropertyTransition(
            todoBubble,
            UIElement.VisibilityProperty,
            () => Invoke(window, "PetHost_MouseRightButtonUp", petHost, rightClick),
            () => todoBubble.Visibility == Visibility.Visible,
            () =>
            {
                AssertBubbleBounds(window, 145, 185, initialRight, initialBottom,
                    "待办气泡显示时宠物窗口应保持固定");
                Assert(bubbleHost.Visibility == Visibility.Visible,
                    "待办气泡显示时气泡容器应已可见");
                Assert(bubbleTailHost.Visibility == Visibility.Visible,
                    "待办气泡显示时气泡尾巴应已可见");
            },
            "待办气泡应在固定窗口外安全显示");

        Assert(todoBubble.Visibility == Visibility.Visible, "右键待办状态应显示白色待办气泡");
        Assert(IsPopupRequestedOpen(bubblePopup), "待办气泡应显示在独立 Popup 中");
        Assert(todoBubble.Background is SolidColorBrush brush && brush.Color == Colors.White,
            "待办气泡背景应为白色");
        AssertClose(window.Width, 145, "待办气泡不应改变宠物窗口宽度");
        AssertClose(window.Height, 185, "待办气泡不应改变宠物窗口高度");
        AssertClose(window.Left + window.Width, initialRight, "待办气泡不应移动宠物窗口");
        AssertClose(window.Top + window.Height, initialBottom, "待办气泡不应移动宠物窗口底部");
        RenderState(window, "todo.png");

        var insideTodoClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = todoInput
        };
        Invoke(window, "Window_PreviewMouseDown", window, insideTodoClick);
        Assert(todoBubble.Visibility == Visibility.Visible,
            "点击待办气泡内部不应收起待办");

        var petRightDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = petHost
        };
        Invoke(window, "Window_PreviewMouseDown", window, petRightDown);
        Assert(todoBubble.Visibility == Visibility.Visible,
            "宠物右键预览事件不应抢先收起待办");
        AssertPropertyTransition(
            todoBubble,
            UIElement.VisibilityProperty,
            () => Invoke(window, "PetHost_MouseRightButtonUp", petHost, rightClick),
            () => todoBubble.Visibility == Visibility.Collapsed,
            () =>
            {
                Assert(!IsPopupRequestedOpen(bubblePopup), "待办气泡收回后应关闭 Popup");
                AssertBubbleBounds(window, 145, 185, initialRight, initialBottom,
                    "待办气泡收回时宠物窗口应保持固定");
                AssertClose(gapColumn.Width.Value, 0, "待办气泡收回时间隔列宽");
                Assert(bubbleHost.Visibility == Visibility.Collapsed,
                    "待办气泡收回时气泡容器应已隐藏");
                Assert(bubbleTailHost.Visibility == Visibility.Collapsed,
                    "待办气泡收回时气泡尾巴应已隐藏");
            },
            "待办气泡应直接关闭 Popup，不裁剪或移动宠物窗口");
        Assert(todoBubble.Visibility == Visibility.Collapsed,
            "待办打开时再右键宠物应只 toggle 一次并收起");

        Invoke(window, "PetHost_MouseRightButtonUp", petHost, rightClick);
        Assert(todoBubble.Visibility == Visibility.Visible, "右键应能重新打开待办");

        var outsideTodoClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = petHost
        };
        Invoke(window, "Window_PreviewMouseDown", window, outsideTodoClick);
        Assert(todoBubble.Visibility == Visibility.Collapsed,
            "点击窗口内待办气泡之外应收起待办");
        AssertClose(window.Width, 145, "点击待办外部收起后宽度");

        Invoke(window, "PetHost_MouseRightButtonUp", petHost, rightClick);
        Assert(todoBubble.Visibility == Visibility.Visible, "失活测试前应重新打开待办");
        Invoke(window, "Window_Deactivated", window, EventArgs.Empty);
        Assert(todoBubble.Visibility == Visibility.Collapsed,
            "点击桌面或其他应用导致窗口失活时应收起待办");

        Invoke(window, "PetHost_MouseRightButtonUp", petHost, rightClick);
        Assert(todoBubble.Visibility == Visibility.Visible, "后续动画测试前应重新打开待办");

        var todoClip = expectedClips[0];
        var todoClipFrames = BuildExpectedFrameNames(todoClip.ActionName);
        Invoke(window, "ShowCuteReaction");
        ArrangeWindow(window);
        AssertActiveFrame(window, petImage, todoClipFrames[0], 0,
            "待办模式中的短动作首帧不正确");
        Assert(todoBubble.Visibility == Visibility.Visible, "待办打开时人物动画不应关闭待办");
        AssertClose(window.Width, 145, "待办打开时人物动画不应改变宠物窗口宽度");
        RenderState(window, "todo-animated.png");

        for (var frameIndex = 1; frameIndex < todoClipFrames.Length; frameIndex++)
        {
            AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                todoClipFrames[frameIndex], frameIndex,
                $"待办模式短动作第 {frameIndex + 1} 帧");
            Assert(todoBubble.Visibility == Visibility.Visible,
                "待办模式中的人物动画不应在中途关闭待办");
        }

        Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
        frameTimer.Stop();
        AssertImage(petImage, "luban-idle.png", "待办模式中的短动作结束后应回到待机");
        Assert(todoBubble.Visibility == Visibility.Visible, "人物动画结束不应关闭已打开的待办");
        AssertClose(window.Width, 145, "人物动画结束后宠物窗口宽度应保持不变");
        AssertClose(window.Height, 185, "人物动画结束后宠物窗口高度应保持不变");
        AssertClose(bubbleColumn.Width.Value, 0, "Popup 模式不应占用主窗口气泡列");
        AssertClose(gapColumn.Width.Value, 0, "Popup 模式不应占用主窗口间隔列");

        AssertEdgePeekContract();
        AssertMonitorAndEdgeRoamingContract();
        AssertAutomaticRoamCadence();
        AssertRoamToggleIsolation();
        AssertClosingStopsAllTimers();
        AssertAutomaticAnimationContract();
        AssertRealTimeAutomaticTrigger();
        AssertRealTimeSingleAction();

        Application.Current.Shutdown();
        Console.WriteLine("UI state checks passed.");
    }

    private static void AssertEdgeAndRoamAssets(MainWindow window)
    {
        var edgeLeftFrames = GetField<BitmapSource[]>(window, "_edgeLeftFrames");
        var edgeTopFrames = GetField<BitmapSource[]>(window, "_edgeTopFrames");
        var edgeBottomFrames = GetField<BitmapSource[]>(window, "_edgeBottomFrames");
        AssertRealPngSequence(edgeLeftFrames, "luban-edge-left", EdgePeekFrameCount,
            "左侧探头");
        AssertRealPngSequence(edgeTopFrames, "luban-edge-top", EdgePeekFrameCount,
            "上侧探头");
        AssertRealPngSequence(edgeBottomFrames, "luban-edge-bottom", EdgePeekFrameCount,
            "下侧探头");

        var rightFrames = InvokeResult<BitmapSource[]>(
            window,
            "GetEdgeFrames",
            GetMainWindowEnumValue("EdgeDock", "Right"));
        Assert(ReferenceEquals(rightFrames, edgeLeftFrames),
            "右侧探头应复用左侧 4 帧，并由朝向变换完成镜像");

        var horizontalFrames = GetField<BitmapSource[][]>(window, "_roamHorizontalFrames");
        var verticalFrames = GetField<BitmapSource[][]>(window, "_roamVerticalFrames");
        Assert(horizontalFrames.Length == RoamAssetNames.Length,
            "绕屏横向资源应包含趴着蠕动、爬行、走跳 3 组");
        Assert(verticalFrames.Length == RoamAssetNames.Length,
            "绕屏纵向资源应包含趴着蠕动、爬行、走跳 3 组");

        for (var modeIndex = 0; modeIndex < RoamAssetNames.Length; modeIndex++)
        {
            var assetName = RoamAssetNames[modeIndex];
            AssertRealPngSequence(
                horizontalFrames[modeIndex],
                $"luban-roam-{assetName}-horizontal",
                RoamFrameCount,
                $"{assetName} 横向绕屏");
            AssertRealPngSequence(
                verticalFrames[modeIndex],
                $"luban-roam-{assetName}-vertical",
                RoamFrameCount,
                $"{assetName} 纵向绕屏");
        }

        var allFrames = edgeLeftFrames
            .Concat(edgeTopFrames)
            .Concat(edgeBottomFrames)
            .Concat(horizontalFrames.SelectMany(frames => frames))
            .Concat(verticalFrames.SelectMany(frames => frames))
            .ToArray();
        var expectedWidth = allFrames[0].PixelWidth;
        var expectedHeight = allFrames[0].PixelHeight;
        Assert(expectedWidth == 240 && expectedHeight is 293 or 294,
            $"探头与绕屏资源应按 DecodePixelWidth=240 预解码，实际 " +
            $"{expectedWidth}×{expectedHeight}");
        Assert(allFrames.All(frame =>
                frame.PixelWidth == expectedWidth &&
                frame.PixelHeight == expectedHeight &&
                frame.Format == PixelFormats.Pbgra32 &&
                frame.IsFrozen),
            "全部探头与绕屏帧应冻结，并保持统一尺寸和 Pbgra32 格式");
    }

    private static void AssertRealPngSequence(
        BitmapSource[] frames,
        string resourcePrefix,
        int expectedCount,
        string stage)
    {
        Assert(frames.Length == expectedCount,
            $"{stage}应包含 {expectedCount} 帧真实 PNG");
        var expectedWidth = frames[0].PixelWidth;
        var expectedHeight = frames[0].PixelHeight;
        var imageInstances = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);

        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var frame = frames[frameIndex];
            var expectedName = $"{resourcePrefix}-{frameIndex + 1:00}.png";
            var original = GetOriginalBitmapImage(frame);
            Assert(original is not null,
                $"{stage}第 {frameIndex + 1} 帧必须来自真实 PNG，而不是运行时混合图");
            Assert(Path.GetFileName(original!.UriSource.LocalPath)
                    .Equals(expectedName, StringComparison.OrdinalIgnoreCase),
                $"{stage}第 {frameIndex + 1} 帧资源名不正确：" +
                $"{Path.GetFileName(original.UriSource.LocalPath)}");
            Assert(original.IsFrozen && frame.IsFrozen,
                $"{stage}第 {frameIndex + 1} 帧及其原始位图都应冻结");
            Assert(frame.Format == PixelFormats.Pbgra32,
                $"{stage}第 {frameIndex + 1} 帧应使用 Pbgra32");
            Assert(frame.PixelWidth == expectedWidth && frame.PixelHeight == expectedHeight,
                $"{stage}的 {expectedCount} 帧像素尺寸应统一");
            Assert(imageInstances.Add(frame),
                $"{stage}第 {frameIndex + 1} 帧应使用独立的真实资源实例");
        }
    }

    private static void AssertEdgePeekContract()
    {
        var window = new MainWindow();
        var primary = GetField<Image>(window, "PetImage");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var edgePeekTimer = GetField<DispatcherTimer>(window, "_edgePeekTimer");
        var roamTimer = GetField<DispatcherTimer>(window, "_roamTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var facingScale = GetField<ScaleTransform>(window, "PetFacingScale");
        var leftFrames = GetField<BitmapSource[]>(window, "_edgeLeftFrames");
        var topFrames = GetField<BitmapSource[]>(window, "_edgeTopFrames");
        var bottomFrames = GetField<BitmapSource[]>(window, "_edgeBottomFrames");
        var leftDock = GetMainWindowEnumValue("EdgeDock", "Left");
        var rightDock = GetMainWindowEnumValue("EdgeDock", "Right");
        var topDock = GetMainWindowEnumValue("EdgeDock", "Top");
        var bottomDock = GetMainWindowEnumValue("EdgeDock", "Bottom");

        Invoke(window, "Window_Loaded", window, new RoutedEventArgs());
        Invoke(window, "ShowCuteReaction");
        Assert(GetRawField(window, "_activeClip") is not null,
            "边缘探头测试前应有一个正在播放的人物动作");
        automaticTimer.Start();

        Invoke(window, "EnterEdgePeek", leftDock);
        Assert(GetRawField(window, "_activeClip") is null,
            "进入手动左侧探头应立即中止人物动作");
        Assert(!frameTimer.IsEnabled,
            "进入手动探头应停止 112 帧动作计时器");
        Assert(!automaticTimer.IsEnabled,
            "进入手动探头应停止自动活动倒计时");
        Assert(edgePeekTimer.IsEnabled && edgePeekTimer.Interval == EdgePeekFrameInterval,
            "探头应以 240ms 间隔循环播放");
        Assert(!GetValueField<bool>(window, "_isEdgeRoaming") && !roamTimer.IsEnabled,
            "手动探头状态不得启动自动绕屏");
        Assert(GetRawField(window, "_edgeDock")?.ToString() == "Left",
            "手动停靠状态应记录左边缘");
        Assert(ReferenceEquals(primary.Source, leftFrames[0]),
            "左侧探头应从 left-01 开始");
        AssertClose(facingScale.ScaleX, 1, "左侧探头朝向");

        for (var tick = 1; tick <= EdgePeekFrameCount; tick++)
        {
            Invoke(window, "EdgePeekTimer_Tick", null, EventArgs.Empty);
            var expectedFrameIndex = tick % EdgePeekFrameCount;
            Assert(GetValueField<int>(window, "_edgePeekFrameIndex") == expectedFrameIndex,
                $"探头第 {tick} 次 Tick 的循环索引不正确");
            Assert(ReferenceEquals(primary.Source, leftFrames[expectedFrameIndex]),
                $"探头第 {tick} 次 Tick 应显示 left-{expectedFrameIndex + 1:00}");
        }

        Invoke(window, "EnterEdgePeek", rightDock);
        Assert(GetRawField(window, "_edgeDock")?.ToString() == "Right",
            "右侧手动停靠状态不正确");
        Assert(ReferenceEquals(primary.Source, leftFrames[0]),
            "右侧探头应复用 left-01");
        AssertClose(facingScale.ScaleX, -1, "右侧探头必须水平镜像");

        Invoke(window, "EnterEdgePeek", topDock);
        Assert(ReferenceEquals(primary.Source, topFrames[0]),
            "上侧探头应显示 top-01");
        AssertClose(facingScale.ScaleX, 1, "上侧探头不应水平镜像");

        Invoke(window, "EnterEdgePeek", bottomDock);
        Assert(ReferenceEquals(primary.Source, bottomFrames[0]),
            "下侧探头应显示 bottom-01");
        AssertClose(facingScale.ScaleX, 1, "下侧探头不应水平镜像");

        Invoke(window, "ExitEdgePeek", true);
        Assert(!edgePeekTimer.IsEnabled &&
               GetRawField(window, "_edgeDock")?.ToString() == "None",
            "退出手动探头应清除停靠状态并停止探头计时器");
        AssertImage(primary, "luban-idle.png", "退出探头后应回到待机图");
        Assert(automaticTimer.IsEnabled,
            "退出手动探头后应重新开始 10 秒自动活动倒计时");
        Assert(!GetValueField<bool>(window, "_isEdgeRoaming"),
            "退出手动探头不应顺带启动绕屏");

        Invoke(window, "Window_Closing", window, new CancelEventArgs());
    }

    private static void AssertMonitorAndEdgeRoamingContract()
    {
        var window = new MainWindow
        {
            Left = 240,
            Top = 180
        };
        SetField(window, "_edgeRoamingEnabled", true);
        window.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(100));

        Assert(new WindowInteropHelper(window).Handle != IntPtr.Zero,
            "双屏工作区测试要求窗口已经创建有效 HWND");
        var workArea = GetMonitorWorkArea(window);
        AssertValidWorkArea(workArea, "已显示窗口的当前显示器工作区");
        var windowCenter = new Point(
            window.Left + window.ActualWidth / 2,
            window.Top + window.ActualHeight / 2);
        Assert(workArea.Contains(windowCenter),
            "MonitorWorkArea 应返回当前承载小鲁班的显示器工作区（含副屏坐标）");

        var primary = GetField<Image>(window, "PetImage");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var edgePeekTimer = GetField<DispatcherTimer>(window, "_edgePeekTimer");
        var roamTimer = GetField<DispatcherTimer>(window, "_roamTimer");
        var roamStopwatch = GetField<Stopwatch>(window, "_roamStopwatch");
        var facingScale = GetField<ScaleTransform>(window, "PetFacingScale");
        var cornerScale = GetField<ScaleTransform>(window, "PetCornerScale");
        var roamOffset = GetField<TranslateTransform>(window, "PetRoamOffset");
        var overlay = GetField<Image>(window, "PetImageOverlay");

        Assert(automaticTimer.IsEnabled,
            "已显示窗口在绕屏前应处于自动倒计时状态");
        Invoke(window, "StartEdgeRoaming");
        Assert(GetValueField<bool>(window, "_isEdgeRoaming"),
            "启用绕屏后 StartEdgeRoaming 应进入运行状态");
        Assert(roamTimer.IsEnabled && roamTimer.Interval == RoamRenderInterval,
            "绕屏应启动 16ms 渲染计时器");
        Assert(roamStopwatch.IsRunning,
            "绕屏应启动独立 Stopwatch 计算真实移动距离");
        Assert(!automaticTimer.IsEnabled,
            "绕屏期间应停止普通自动活动倒计时");
        Assert(GetRawField(window, "_activeClip") is null &&
               GetRawField(window, "_edgeDock")?.ToString() == "None",
            "绕屏只能从无人物动作、无手动停靠的空闲状态开始");

        var roamWorkArea = GetValueField<Rect>(window, "_roamWorkArea");
        AssertRectClose(roamWorkArea, workArea,
            "StartEdgeRoaming 应锁定窗口当前所在显示器的工作区");
        Assert(GetRawField(window, "_roamEdge")?.ToString() != "None",
            "绕屏开始时应选中当前工作区的最近边缘");

        SetField(window, "_roamApproaching", false);
        SetField(window, "_roamEdge", GetMainWindowEnumValue("EdgeDock", "Top"));
        SetField(window, "_roamVisualEdge", GetMainWindowEnumValue("EdgeDock", "Top"));
        SetField(window, "_roamClockwise", true);
        window.Left = workArea.Right - window.ActualWidth - 1;
        window.Top = workArea.Top;
        Invoke(window, "UpdateRoamVisual");
        Invoke(window, "AdvanceRoamAlongBoundary", 2d);
        Assert(GetValueField<bool>(window, "_isRoamCornerTurning"),
            "到达拐角后应先进入短暂转身状态，而不是瞬时切换横竖素材");
        Assert(GetRawField(window, "_roamEdge")?.ToString() == "Top",
            "转身前半段应继续显示原边缘方向的素材");

        Invoke(window, "AdvanceRoamCornerTurn",
            TimeSpan.FromTicks(RoamCornerTurnDuration.Ticks / 4));
        Assert(cornerScale.ScaleX is > 0.12 and < 1 &&
               cornerScale.ScaleY is > 0.28 and < 1,
            "转身前半段应平滑压缩人物姿态");

        Invoke(window, "AdvanceRoamCornerTurn",
            TimeSpan.FromTicks(RoamCornerTurnDuration.Ticks / 4));
        Invoke(window, "UpdateRoamVisual");
        Assert(GetRawField(window, "_roamEdge")?.ToString() == "Right",
            "人物压缩到最小时才应切换到下一条边的纵向素材");
        AssertClose(cornerScale.ScaleX, 0.12, "转角素材切换时的水平压缩比例");
        AssertClose(cornerScale.ScaleY, 0.28, "转角素材切换时的垂直压缩比例");
        AssertSingleLayerInvariant(primary, overlay, "绕屏转角素材切换");

        Invoke(window, "AdvanceRoamCornerTurn",
            TimeSpan.FromTicks(RoamCornerTurnDuration.Ticks / 2));
        Assert(!GetValueField<bool>(window, "_isRoamCornerTurning"),
            "320ms 转身完成后应恢复沿新边缘移动");
        AssertClose(cornerScale.ScaleX, 1, "转身完成后的水平缩放");
        AssertClose(cornerScale.ScaleY, 1, "转身完成后的垂直缩放");

        Invoke(window, "StopEdgeRoaming", "测试主动停止", true);
        Assert(!GetValueField<bool>(window, "_isEdgeRoaming") &&
               !roamTimer.IsEnabled && !roamStopwatch.IsRunning,
            "StopEdgeRoaming 应停止绕屏计时器与 Stopwatch");
        AssertImage(primary, "luban-idle.png", "StopEdgeRoaming 后应回到待机图");
        Assert(automaticTimer.IsEnabled,
            "StopEdgeRoaming(restart=true) 应恢复 10 秒自动倒计时");
        AssertClose(facingScale.ScaleX, 1, "停止绕屏后的水平朝向");
        AssertClose(facingScale.ScaleY, 1, "停止绕屏后的垂直朝向");
        AssertClose(cornerScale.ScaleX, 1, "停止绕屏后的转角水平缩放");
        AssertClose(cornerScale.ScaleY, 1, "停止绕屏后的转角垂直缩放");
        AssertClose(roamOffset.X, 0, "停止绕屏后的横向跳跃偏移");
        AssertClose(roamOffset.Y, 0, "停止绕屏后的纵向跳跃偏移");

        Invoke(window, "StartEdgeRoaming");
        Assert(GetValueField<bool>(window, "_isEdgeRoaming"),
            "手动拖动互斥测试前应再次开始绕屏");
        var petHost = GetField<Grid>(window, "PetHost");
        var leftDown = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseDownEvent,
            Source = petHost
        };
        Invoke(window, "PetHost_MouseLeftButtonDown", petHost, leftDown);
        Assert(!GetValueField<bool>(window, "_isEdgeRoaming") && !roamTimer.IsEnabled,
            "用户开始手动拖动时应先停止自动绕屏");
        Assert(!automaticTimer.IsEnabled,
            "手动拖动中止绕屏后不应立刻重启自动倒计时");

        Invoke(window, "EnterEdgePeek", GetMainWindowEnumValue("EdgeDock", "Left"));
        Assert(edgePeekTimer.IsEnabled &&
               GetRawField(window, "_edgeDock")?.ToString() == "Left",
            "手动拖到边界后应进入探头状态");
        Assert(!GetValueField<bool>(window, "_isEdgeRoaming") && !roamTimer.IsEnabled,
            "手动探头优先级应高于绕屏，停靠期间不得恢复绕屏");
        petHost.ReleaseMouseCapture();
        SetField(window, "_pointerDown", false);
        Invoke(window, "ExitEdgePeek", true);
        AssertImage(primary, "luban-idle.png", "离开手动边界后应恢复待机图");

        window.Close();
    }

    private static void AssertAutomaticRoamCadence()
    {
        var disabledWindow = new MainWindow();
        SetField(disabledWindow, "_edgeRoamingEnabled", false);
        Invoke(disabledWindow, "Window_Loaded", disabledWindow, new RoutedEventArgs());
        var disabledPrimary = GetField<Image>(disabledWindow, "PetImage");
        var disabledOverlay = GetField<Image>(disabledWindow, "PetImageOverlay");
        var disabledFrameTimer = GetField<DispatcherTimer>(disabledWindow, "_frameTimer");

        for (var activityNumber = 1; activityNumber <= 3; activityNumber++)
        {
            Invoke(disabledWindow, "AutomaticTimer_Tick", null, EventArgs.Empty);
            Assert(!GetValueField<bool>(disabledWindow, "_isEdgeRoaming"),
                $"关闭绕屏后第 {activityNumber} 次自动活动不应开始绕屏");
            CompleteAutomaticActivity(
                disabledWindow,
                disabledPrimary,
                disabledOverlay,
                disabledFrameTimer);
        }

        Invoke(disabledWindow, "AutomaticTimer_Tick", null, EventArgs.Empty);
        Assert(GetValueField<int>(disabledWindow, "_automaticActivityCount") == 4,
            "关闭绕屏时也应正常累计到第 4 次自动活动");
        Assert(!GetValueField<bool>(disabledWindow, "_isEdgeRoaming"),
            "关闭绕屏后第 4 次自动活动不得开始绕屏");
        var disabledFourthClip = GetRawField(disabledWindow, "_activeClip")
            ?? throw new InvalidOperationException(
                "关闭绕屏后第 4 次仍应触发普通可爱人物动作");
        Assert(GetClipActionName(disabledFourthClip) == "run",
            "关闭绕屏后第 4 次应继续消费普通自动序列中的 run 动作");
        Assert(GetValueField<int>(disabledWindow, "_nextAutomaticSequenceIndex") == 4,
            "关闭绕屏后第 4 次应正常推进普通自动序列");
        CompleteActiveClip(
            disabledWindow,
            disabledPrimary,
            disabledOverlay,
            disabledFrameTimer);
        Invoke(disabledWindow, "Window_Closing", disabledWindow, new CancelEventArgs());

        var enabledWindow = new MainWindow();
        SetField(enabledWindow, "_edgeRoamingEnabled", true);
        Invoke(enabledWindow, "Window_Loaded", enabledWindow, new RoutedEventArgs());
        var enabledPrimary = GetField<Image>(enabledWindow, "PetImage");
        var enabledOverlay = GetField<Image>(enabledWindow, "PetImageOverlay");
        var enabledFrameTimer = GetField<DispatcherTimer>(enabledWindow, "_frameTimer");
        var enabledRoamTimer = GetField<DispatcherTimer>(enabledWindow, "_roamTimer");

        for (var activityNumber = 1; activityNumber <= 3; activityNumber++)
        {
            Invoke(enabledWindow, "AutomaticTimer_Tick", null, EventArgs.Empty);
            Assert(!GetValueField<bool>(enabledWindow, "_isEdgeRoaming"),
                $"启用绕屏时前 3 次中的第 {activityNumber} 次仍应是普通自动活动");
            CompleteAutomaticActivity(
                enabledWindow,
                enabledPrimary,
                enabledOverlay,
                enabledFrameTimer);
        }

        Invoke(enabledWindow, "AutomaticTimer_Tick", null, EventArgs.Empty);
        Assert(GetValueField<int>(enabledWindow, "_automaticActivityCount") == 4,
            "启用绕屏后第 4 次自动 Tick 应累计活动次数");
        Assert(GetValueField<bool>(enabledWindow, "_isEdgeRoaming") &&
               enabledRoamTimer.IsEnabled,
            "启用绕屏后第 4 次自动活动应开始沿当前屏幕边界移动");
        Assert(GetRawField(enabledWindow, "_activeClip") is null,
            "第 4 次进入绕屏时不应并行启动普通人物动作");
        Assert(GetValueField<int>(enabledWindow, "_nextAutomaticSequenceIndex") == 3,
            "绕屏活动不应消费普通可爱动作的轮换索引");
        Invoke(enabledWindow, "StopEdgeRoaming", "自动节奏测试结束", true);
        Invoke(enabledWindow, "Window_Closing", enabledWindow, new CancelEventArgs());
    }

    private static void CompleteAutomaticActivity(
        MainWindow window,
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer)
    {
        if (GetRawField(window, "_activeClip") is not null)
        {
            CompleteActiveClip(window, primary, overlay, frameTimer);
            return;
        }

        Assert(GetValueField<bool>(window, "_isPillowBreathing"),
            "普通自动活动应是人物动作或抱枕呼吸之一");
        Invoke(window, "StopPillowBreathing");
        Invoke(window, "RestartAutomaticCountdown");
        Assert(!GetValueField<bool>(window, "_isPillowBreathing"),
            "测试收尾应停止抱枕呼吸，不留下动画状态");
        AssertImage(primary, "luban-idle.png", "抱枕呼吸期间应保持待机图");
        AssertSingleLayerSettled(primary, overlay);
    }

    private static void AssertRoamToggleIsolation()
    {
        var window = new MainWindow();
        var toggle = GetField<CheckBox>(window, "AutoRoamToggle");
        var todoBubble = GetField<Border>(window, "TodoBubble");
        var primary = GetField<Image>(window, "PetImage");
        var overlay = GetField<Image>(window, "PetImageOverlay");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var settingsStore = GetField<AppSettingsStore>(window, "_settingsStore");
        var settingsPath = settingsStore.FilePath;
        var settingsExisted = File.Exists(settingsPath);
        var settingsBefore = settingsExisted ? File.ReadAllBytes(settingsPath) : null;

        ArrangeWindow(window);
        Assert(IsVisualDescendantOf(toggle, todoBubble),
            "自动绕屏开关应位于右键待办气泡内");
        Assert(toggle.Content?.ToString() == "自动绕屏移动",
            "右键待办中的开关文案应明确只控制自动绕屏移动");
        Assert(toggle.ToolTip?.ToString()?.Contains("其他自动可爱动作不受影响",
                   StringComparison.Ordinal) == true,
            "右键开关提示应说明其他可爱动作不受影响");

        SetField(window, "_edgeRoamingEnabled", false);
        var clip = GetField<Array>(window, "_reactionClips").GetValue(0)
            ?? throw new InvalidOperationException("缺少手动动作 Clip");
        var started = InvokeResult<bool>(window, "TryStartReaction", clip, false);
        Assert(started && GetRawField(window, "_activeClip") is not null,
            "关闭自动绕屏后 TryStartReaction 仍应能开始人物动作");
        Assert(!GetValueField<bool>(window, "_isEdgeRoaming"),
            "关闭自动绕屏后手动人物动作不应改变绕屏状态");
        CompleteActiveClip(window, primary, overlay, frameTimer);

        Assert(File.Exists(settingsPath) == settingsExisted,
            "UiStateChecks 不应创建或删除用户 settings.json");
        if (settingsBefore is not null)
        {
            Assert(File.ReadAllBytes(settingsPath).SequenceEqual(settingsBefore),
                "UiStateChecks 不应改写用户 settings.json");
        }

        Invoke(window, "Window_Closing", window, new CancelEventArgs());
    }

    private static void AssertClosingStopsAllTimers()
    {
        var window = new MainWindow();
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var edgePeekTimer = GetField<DispatcherTimer>(window, "_edgePeekTimer");
        var roamTimer = GetField<DispatcherTimer>(window, "_roamTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var roamStopwatch = GetField<Stopwatch>(window, "_roamStopwatch");

        frameTimer.Start();
        edgePeekTimer.Start();
        roamTimer.Start();
        automaticTimer.Start();
        roamStopwatch.Restart();
        Invoke(window, "Window_Closing", window, new CancelEventArgs());

        Assert(!frameTimer.IsEnabled,
            "窗口关闭时应停止 112 帧人物动作计时器");
        Assert(!edgePeekTimer.IsEnabled,
            "窗口关闭时应停止 240ms 边缘探头计时器");
        Assert(!roamTimer.IsEnabled,
            "窗口关闭时应停止 16ms 绕屏移动计时器");
        Assert(!automaticTimer.IsEnabled,
            "窗口关闭时应停止 10 秒自动活动计时器");
        Assert(!roamStopwatch.IsRunning,
            "窗口关闭时应停止绕屏 Stopwatch");
    }

    private static void AssertAutomaticAnimationContract()
    {
        var window = new MainWindow();
        // 本测试专门覆盖普通九项自动序列；绕屏第 4 次分流由
        // AssertAutomaticRoamCadence 独立验证。直接设置字段，避免改写用户设置。
        SetField(window, "_edgeRoamingEnabled", false);
        var primary = GetField<Image>(window, "PetImage");
        var overlay = GetField<Image>(window, "PetImageOverlay");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var cuteBubble = GetField<Border>(window, "CuteBubble");
        string?[] expectedAutomaticActions =
        {
            "yawn",
            null,
            "cry",
            "run",
            "cute",
            "like",
            "eat",
            "wave",
            "think"
        };

        Invoke(window, "Window_Loaded", window, new RoutedEventArgs());
        Assert(automaticTimer.IsEnabled, "窗口加载后应启动自动动画计时器");
        Assert(automaticTimer.Interval == TimeSpan.FromSeconds(10),
            "自动动画计时器应保持 10 秒周期");
        automaticTimer.Stop();

        var expectedManualIndex = 0;
        for (var sequenceIndex = 0; sequenceIndex < expectedAutomaticActions.Length; sequenceIndex++)
        {
            Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") == sequenceIndex,
                $"自动轮换第 {sequenceIndex + 1} 项开始前索引不正确");
            Invoke(window, "AutomaticTimer_Tick", null, EventArgs.Empty);
            Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") ==
                   (sequenceIndex + 1) % expectedAutomaticActions.Length,
                $"自动轮换第 {sequenceIndex + 1} 项应只消费一次索引");
            Assert(GetValueField<int>(window, "_nextClipIndex") == expectedManualIndex,
                $"自动轮换第 {sequenceIndex + 1} 项不应消费手动点击索引");
            Assert(cuteBubble.Visibility == Visibility.Collapsed,
                "自动动画不应弹出卖萌气泡干扰用户");

            if (sequenceIndex == 1)
            {
                Assert(GetRawField(window, "_activeClip") is null,
                    "抱枕自动动画不应创建动作 Clip 或屏蔽点击");
                Assert(GetValueField<bool>(window, "_isPillowBreathing"),
                    "自动轮换应包含缓慢的抱枕呼吸动画");
                AssertImage(primary, "luban-idle.png", "抱枕动画应保持待机图");
                AssertSingleLayerSettled(primary, overlay);

                Invoke(window, "ShowCuteReaction");
                automaticTimer.Stop();
                Assert(GetRawField(window, "_activeClip") is not null,
                    "点击应能立即打断抱枕呼吸并开始人物动作");
                Assert(!GetValueField<bool>(window, "_isPillowBreathing"),
                    "人物动作开始前应停止抱枕呼吸缩放");
                expectedManualIndex = 1;
                CompleteActiveClip(window, primary, overlay, frameTimer);
                continue;
            }

            var activeClip = GetRawField(window, "_activeClip")
                ?? throw new InvalidOperationException("自动动作应创建活动 Clip");
            Assert(GetClipActionName(activeClip) == expectedAutomaticActions[sequenceIndex],
                $"自动轮换第 {sequenceIndex + 1} 项动作不正确");
            if (sequenceIndex == 0)
            {
                var automaticIndexWhileBusy =
                    GetValueField<int>(window, "_nextAutomaticSequenceIndex");
                Invoke(window, "AutomaticTimer_Tick", null, EventArgs.Empty);
                Assert(ReferenceEquals(GetRawField(window, "_activeClip"), activeClip),
                    "人物动作播放期间的自动 Tick 不应替换活动 Clip");
                Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") ==
                       automaticIndexWhileBusy,
                    "人物动作播放期间的自动 Tick 不应消费或排队下一项");
            }
            CompleteActiveClip(window, primary, overlay, frameTimer);
        }

        Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") == 0,
            "九项自动轮换完成后应回到第一项");
        Assert(GetValueField<int>(window, "_nextClipIndex") == expectedManualIndex,
            "自动九项轮换不应额外消费手动点击动作");
        AssertImage(primary, "luban-idle.png", "自动九项轮换结束后应回到抱枕待机");
        AssertSingleLayerSettled(primary, overlay);

        automaticTimer.Start();
        Invoke(window, "Window_Closing", window, new CancelEventArgs());
        Assert(!automaticTimer.IsEnabled, "窗口关闭时应停止自动动画计时器");
    }

    private static void CompleteActiveClip(
        MainWindow window,
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer)
    {
        while (GetRawField(window, "_activeClip") is not null)
        {
            Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
            frameTimer.Stop();
            AssertSingleLayerInvariant(primary, overlay, "快速完成动作");
        }

        AssertImage(primary, "luban-idle.png", "动作完成后应回到抱枕待机图");
        AssertSingleLayerSettled(primary, overlay);
    }

    private static void AssertRealTimeAutomaticTrigger()
    {
        var window = new MainWindow();
        var primary = GetField<Image>(window, "PetImage");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        Invoke(window, "Window_Loaded", window, new RoutedEventArgs());

        PumpDispatcher(TimeSpan.FromSeconds(9.2));
        Assert(GetRawField(window, "_activeClip") is null,
            "无操作未满 10 秒时不应提前播放自动动作");
        Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") == 0,
            "无操作未满 10 秒时不应提前消费自动轮换项");
        AssertImage(primary, "luban-idle.png", "自动计时满 10 秒前应保持抱枕待机");

        PumpDispatcher(TimeSpan.FromSeconds(1.3));
        var activeClip = GetRawField(window, "_activeClip")
            ?? throw new InvalidOperationException("无操作满 10 秒后应自动开始人物动作");
        Assert(GetClipActionName(activeClip) == "yawn",
            "第一次真实自动触发应播放打哈欠动作");
        Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") == 1,
            "真实自动触发应且仅应消费一个轮换项");

        Invoke(window, "Window_Closing", window, new CancelEventArgs());
        Assert(!automaticTimer.IsEnabled, "真实自动计时测试结束后应停止计时器");
        Assert(!frameTimer.IsEnabled, "窗口关闭时应停止动作帧计时器");
    }

    private static void AssertClipTiming(object clip, ExpectedClip expected)
    {
        Assert(GetClipMessage(clip) == expected.Message,
            $"{expected.ActionName} Clip 对白不正确");
        Assert(GetClipActionName(clip) == expected.ActionName,
            $"{expected.Message} 的 ActionName 不正确");
        Assert(GetClipActionFrameIndex(clip) == ActionFrameIndex,
            $"{expected.Message} 的动作主体索引应为 {ActionFrameIndex}");

        var frames = GetClipFrames(clip);
        var expectedNames = BuildExpectedFrameNames(expected.ActionName);
        Assert(frames.Length == MotionFrameCount,
            $"{expected.Message} 应包含 {MotionFrameCount} 帧");
        Assert(expectedNames.Length == MotionFrameCount,
            "测试构造的动作序列长度不正确");
        var firstImage = GetFrameImage(GetFrame(frames, 0));
        var expectedPixelWidth = firstImage.PixelWidth;
        var expectedPixelHeight = firstImage.PixelHeight;
        Assert(expectedPixelWidth == 240 && expectedPixelHeight is 293 or 294,
            $"DecodePixelWidth=240 的输出尺寸异常：{expectedPixelWidth}×{expectedPixelHeight}");

        var totalDuration = TimeSpan.Zero;
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var frame = GetFrame(frames, frameIndex);
            var image = GetFrameImage(frame);
            var holdDuration = GetFrameHoldDuration(frame);
            var name = GetFrameName(frame);
            var expectedDuration = GetExpectedFrameDuration(frameIndex);

            Assert(name == expectedNames[frameIndex],
                $"{expected.Message} 第 {frameIndex + 1} 帧名称不正确：{name}");
            Assert(holdDuration == expectedDuration,
                $"{expected.Message} 第 {frameIndex + 1} 帧停留时长不正确");
            Assert(image.IsFrozen,
                $"{expected.Message} 第 {frameIndex + 1} 帧应预生成并冻结");
            Assert(image.Format == PixelFormats.Pbgra32,
                $"{expected.Message} 第 {frameIndex + 1} 帧应使用 Pbgra32");
            Assert(image.PixelWidth == expectedPixelWidth &&
                   image.PixelHeight == expectedPixelHeight,
                $"{expected.Message} 第 {frameIndex + 1} 帧像素尺寸不统一，实际 " +
                $"{image.PixelWidth}×{image.PixelHeight}");
            totalDuration += holdDuration;
        }

        Assert(totalDuration == TimeSpan.FromMilliseconds(9600),
            $"{expected.Message} 总时长应为 9.6 秒，实际 {totalDuration.TotalMilliseconds}ms");

        var expectedWakeNames = Enumerable.Range(1, WakeFrameCount)
            .Select(index => $"luban-wake-{index:00}.png")
            .ToArray();
        var forwardWakeNames = Enumerable.Range(0, WakeFrameCount)
            .Select(index => GetFrameName(GetFrame(frames, index)))
            .ToArray();
        Assert(forwardWakeNames.SequenceEqual(expectedWakeNames),
            $"{expected.Message} 正向 12 张公共苏醒资源顺序不正确");

        var expectedActionNames = Enumerable.Range(1, ActionPoseFrameCount)
            .Select(index => $"luban-{expected.ActionName}-frame-{index:00}.png")
            .ToArray();
        var forwardActionNames = Enumerable.Range(WakeFrameCount, ActionPoseFrameCount)
            .Select(index => GetFrameName(GetFrame(frames, index)))
            .ToArray();
        Assert(forwardActionNames.SequenceEqual(expectedActionNames),
            $"{expected.Message} 正向 24 张专属动作资源顺序不正确");
        Assert(GetFrameName(GetFrame(frames, ActionFrameIndex)) == expectedActionNames[^1],
            $"{expected.Message} 动作主体索引应指向 frame-24");

        var expectedLoopNames = Enumerable.Range(0, ActionLoopCycleCount)
            .SelectMany(_ => Enumerable.Range(
                ActionLoopStartPoseNumber,
                ActionLoopPoseCount))
            .Select(index => $"luban-{expected.ActionName}-frame-{index:00}.png")
            .ToArray();
        var loopNames = Enumerable.Range(ActionLoopStartFrameIndex, ActionLoopFrameCount)
            .Select(index => GetFrameName(GetFrame(frames, index)))
            .ToArray();
        Assert(loopNames.SequenceEqual(expectedLoopNames),
            $"{expected.Message} 动作末 4 姿势应按 21..24 顺序循环 10 次");

        var expectedReverseActionNames = Enumerable.Range(1, ActionPoseFrameCount - 1)
            .Select(offset =>
                $"luban-{expected.ActionName}-frame-{ActionPoseFrameCount - offset:00}.png")
            .ToArray();
        var reverseActionNames = Enumerable.Range(
                ReverseFrameStartIndex,
                ActionPoseFrameCount - 1)
            .Select(index => GetFrameName(GetFrame(frames, index)))
            .ToArray();
        Assert(reverseActionNames.SequenceEqual(expectedReverseActionNames),
            $"{expected.Message} 返程专属动作应从 frame-23 反向播放到 frame-01");

        var reverseWakeStartIndex = ReverseFrameStartIndex + ActionPoseFrameCount - 1;
        var reverseWakeNames = Enumerable.Range(reverseWakeStartIndex, WakeFrameCount)
            .Select(index => GetFrameName(GetFrame(frames, index)))
            .ToArray();
        Assert(reverseWakeNames.SequenceEqual(expectedWakeNames.Reverse()),
            $"{expected.Message} 返程公共苏醒资源应从 wake-12 反向播放到 wake-01");
        Assert(GetFrameName(GetFrame(frames, frames.Length - 1)) == "luban-idle.png",
            $"{expected.Message} 反向序列末帧应回到待机资源");
        Assert(expectedNames.All(name =>
                !name.Contains("-motion-", StringComparison.Ordinal) &&
                !name.Contains("-inbetween-", StringComparison.Ordinal)),
            $"{expected.Message} 新时间线不应再引用旧 motion/inbetween 资源名");
    }

    private static void AssertRealMotionFrames(Array reactionClips)
    {
        BitmapSource[]? sharedWakeImages = null;
        for (var clipIndex = 0; clipIndex < reactionClips.Length; clipIndex++)
        {
            var clip = reactionClips.GetValue(clipIndex)
                ?? throw new InvalidOperationException($"动作 {clipIndex + 1} Clip 不应为空");
            var actionName = GetClipActionName(clip);
            var frames = GetClipFrames(clip);
            var forwardHashes = new HashSet<string>(StringComparer.Ordinal);
            var forwardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var previousImage = GetFrameImage(GetFrame(frames, frames.Length - 1));

            for (var forwardIndex = 0; forwardIndex < ForwardFrameCount; forwardIndex++)
            {
                var frame = GetFrame(frames, forwardIndex);
                var image = GetFrameImage(frame);
                var original = GetOriginalBitmapImage(image);
                Assert(original is not null,
                    $"{actionName} 正向第 {forwardIndex + 1} 帧必须来自真实 PNG，而非像素混合");
                Assert(Path.GetFileName(original!.UriSource.LocalPath)
                        .Equals(GetFrameName(frame), StringComparison.OrdinalIgnoreCase),
                    $"{actionName} 正向第 {forwardIndex + 1} 帧名称应与真实资源一致");
                Assert(forwardNames.Add(GetFrameName(frame)),
                    $"{actionName} 的 36 张正向资源名不应重复");
                Assert(forwardHashes.Add(GetPixelHash(image)),
                    $"{actionName} 的 12 张公共苏醒与 24 张动作姿势不应出现逐像素重复");
                AssertFrameContinuity(
                    previousImage,
                    image,
                    $"{actionName} 正向第 {forwardIndex + 1} 帧");
                previousImage = image;
            }

            var wakeImages = Enumerable.Range(0, WakeFrameCount)
                .Select(index => GetFrameImage(GetFrame(frames, index)))
                .ToArray();
            if (sharedWakeImages is null)
            {
                sharedWakeImages = wakeImages;
            }
            else
            {
                for (var wakeIndex = 0; wakeIndex < WakeFrameCount; wakeIndex++)
                {
                    Assert(ReferenceEquals(wakeImages[wakeIndex], sharedWakeImages[wakeIndex]),
                        $"{actionName} 的 wake-{wakeIndex + 1:00} 应复用公共苏醒位图实例");
                }
            }

            for (var cycle = 0; cycle < ActionLoopCycleCount; cycle++)
            {
                for (var poseOffset = 0; poseOffset < ActionLoopPoseCount; poseOffset++)
                {
                    var poseNumber = ActionLoopStartPoseNumber + poseOffset;
                    var forwardFrameIndex = WakeFrameCount + poseNumber - 1;
                    var loopFrameIndex = ActionLoopStartFrameIndex +
                                         cycle * ActionLoopPoseCount + poseOffset;
                    Assert(ReferenceEquals(
                            GetFrameImage(GetFrame(frames, loopFrameIndex)),
                            GetFrameImage(GetFrame(frames, forwardFrameIndex))),
                        $"{actionName} 第 {cycle + 1} 轮 frame-{poseNumber:00} 应复用正向位图实例");
                }
            }

            for (var poseNumber = ActionPoseFrameCount - 1; poseNumber >= 1; poseNumber--)
            {
                var reverseOffset = ActionPoseFrameCount - 1 - poseNumber;
                var reverseFrameIndex = ReverseFrameStartIndex + reverseOffset;
                var forwardFrameIndex = WakeFrameCount + poseNumber - 1;
                Assert(ReferenceEquals(
                        GetFrameImage(GetFrame(frames, reverseFrameIndex)),
                        GetFrameImage(GetFrame(frames, forwardFrameIndex))),
                    $"{actionName} frame-{poseNumber:00} 返程应复用正向位图实例");
            }

            var reverseWakeStartIndex = ReverseFrameStartIndex + ActionPoseFrameCount - 1;
            for (var wakeNumber = WakeFrameCount; wakeNumber >= 1; wakeNumber--)
            {
                var reverseOffset = WakeFrameCount - wakeNumber;
                Assert(ReferenceEquals(
                        GetFrameImage(GetFrame(frames, reverseWakeStartIndex + reverseOffset)),
                        GetFrameImage(GetFrame(frames, wakeNumber - 1))),
                    $"{actionName} wake-{wakeNumber:00} 返程应复用正向位图实例");
            }

            var idle = GetOriginalBitmapImage(GetFrameImage(GetFrame(frames, frames.Length - 1)));
            Assert(idle is not null &&
                   Path.GetFileName(idle.UriSource.LocalPath)
                       .Equals("luban-idle.png", StringComparison.OrdinalIgnoreCase),
                $"{actionName} 返程末帧必须使用真实待机 PNG");

            previousImage = GetFrameImage(GetFrame(frames, frames.Length - 1));
            for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                var image = GetFrameImage(GetFrame(frames, frameIndex));
                AssertFrameContinuity(previousImage, image,
                    $"{actionName} 完整时间线第 {frameIndex + 1} 帧");
                previousImage = image;
            }
        }
    }

    private static void AssertFrameContinuity(
        BitmapSource previous,
        BitmapSource current,
        string stage)
    {
        Assert(previous.PixelWidth == current.PixelWidth &&
               previous.PixelHeight == current.PixelHeight,
            $"{stage} 与前一帧尺寸必须一致");

        var previousPixels = GetPbgraPixels(previous);
        var currentPixels = GetPbgraPixels(current);
        var previousArea = 0;
        var currentArea = 0;
        var intersection = 0;
        var union = 0;
        for (var offset = 3; offset < previousPixels.Length; offset += 4)
        {
            var previousVisible = previousPixels[offset] > 16;
            var currentVisible = currentPixels[offset] > 16;
            previousArea += previousVisible ? 1 : 0;
            currentArea += currentVisible ? 1 : 0;
            intersection += previousVisible && currentVisible ? 1 : 0;
            union += previousVisible || currentVisible ? 1 : 0;
        }

        Assert(previousArea > 0 && currentArea > 0 && union > 0,
            $"{stage} 不应为空白帧");
        var areaRatio = Math.Max(previousArea, currentArea) /
                        (double)Math.Min(previousArea, currentArea);
        var alphaIntersectionOverUnion = intersection / (double)union;
        Assert(areaRatio <= 1.65,
            $"{stage} 人物面积相对前一帧跳变过大：{areaRatio:F3}");
        Assert(alphaIntersectionOverUnion >= 0.45,
            $"{stage} 人物轮廓相对前一帧跳变过大：IoU={alphaIntersectionOverUnion:F3}");
    }

    private static string GetPixelHash(BitmapSource source)
    {
        return Convert.ToHexString(SHA256.HashData(GetPbgraPixels(source)));
    }

    private static byte[] GetPbgraPixels(BitmapSource source)
    {
        BitmapSource pbgra = source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var stride = pbgra.PixelWidth * 4;
        var pixels = new byte[stride * pbgra.PixelHeight];
        pbgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static Array GetClipFrames(object clip)
    {
        return (Array)(clip.GetType().GetProperty("Frames")?.GetValue(clip)
            ?? throw new InvalidOperationException("Clip 缺少 Frames"));
    }

    private static string GetClipMessage(object clip)
    {
        return (string)(clip.GetType().GetProperty("Message")?.GetValue(clip)
            ?? throw new InvalidOperationException("Clip 缺少 Message"));
    }

    private static string GetClipActionName(object clip)
    {
        return (string)(clip.GetType().GetProperty("ActionName")?.GetValue(clip)
            ?? throw new InvalidOperationException("Clip 缺少 ActionName"));
    }

    private static int GetClipActionFrameIndex(object clip)
    {
        return (int)(clip.GetType().GetProperty("ActionFrameIndex")?.GetValue(clip)
            ?? throw new InvalidOperationException("Clip 缺少 ActionFrameIndex"));
    }

    private static object GetFrame(Array frames, int index)
    {
        return frames.GetValue(index)
            ?? throw new InvalidOperationException($"动画帧 {index} 不应为空");
    }

    private static BitmapSource GetFrameImage(object frame)
    {
        return frame.GetType().GetProperty("Image")?.GetValue(frame) as BitmapSource
            ?? throw new InvalidOperationException("AnimationFrame.Image 应为 BitmapSource");
    }

    private static TimeSpan GetFrameHoldDuration(object frame)
    {
        return (TimeSpan)(frame.GetType().GetProperty("HoldDuration")?.GetValue(frame)
            ?? throw new InvalidOperationException("AnimationFrame 缺少 HoldDuration"));
    }

    private static string GetFrameName(object frame)
    {
        return (string)(frame.GetType().GetProperty("Name")?.GetValue(frame)
            ?? throw new InvalidOperationException("AnimationFrame 缺少 Name"));
    }

    private static string[] BuildExpectedFrameNames(string actionName)
    {
        var names = new List<string>(MotionFrameCount);
        for (var wakeNumber = 1; wakeNumber <= WakeFrameCount; wakeNumber++)
        {
            names.Add($"luban-wake-{wakeNumber:00}.png");
        }

        for (var poseNumber = 1; poseNumber <= ActionPoseFrameCount; poseNumber++)
        {
            names.Add($"luban-{actionName}-frame-{poseNumber:00}.png");
        }

        for (var cycle = 0; cycle < ActionLoopCycleCount; cycle++)
        {
            for (var poseOffset = 0; poseOffset < ActionLoopPoseCount; poseOffset++)
            {
                var poseNumber = ActionLoopStartPoseNumber + poseOffset;
                names.Add($"luban-{actionName}-frame-{poseNumber:00}.png");
            }
        }

        for (var poseNumber = ActionPoseFrameCount - 1; poseNumber >= 1; poseNumber--)
        {
            names.Add($"luban-{actionName}-frame-{poseNumber:00}.png");
        }

        for (var wakeNumber = WakeFrameCount; wakeNumber >= 1; wakeNumber--)
        {
            names.Add($"luban-wake-{wakeNumber:00}.png");
        }

        names.Add("luban-idle.png");

        return names.ToArray();
    }

    private static TimeSpan GetExpectedFrameDuration(int frameIndex)
    {
        return frameIndex >= ActionLoopStartFrameIndex &&
               frameIndex < ReverseFrameStartIndex
            ? ActionLoopFrameInterval
            : MotionFrameInterval;
    }

    private static void AssertRealTimeSingleAction()
    {
        var window = new MainWindow();
        var primary = GetField<Image>(window, "PetImage");
        var overlay = GetField<Image>(window, "PetImageOverlay");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var messageText = GetField<TextBlock>(window, "CuteMessageText");
        var expectedFrames = BuildExpectedFrameNames("yawn");
        var observedFrameIndexes = new HashSet<int>();
        var wakeBurstObserved = false;
        var actionLoopBurstObserved = false;
        var returnBurstObserved = false;
        TimeSpan? actionLoopStarted = null;
        TimeSpan? actionLoopEnded = null;
        var stopwatch = Stopwatch.StartNew();

        Invoke(window, "ShowCuteReaction");

        while (GetRawField(window, "_activeClip") is not null &&
               stopwatch.Elapsed < TimeSpan.FromSeconds(12))
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(16));
            if (GetRawField(window, "_activeClip") is null)
            {
                break;
            }

            var frameIndex = GetValueField<int>(window, "_activeFrameIndex");
            observedFrameIndexes.Add(frameIndex);
            AssertSingleLayerInvariant(primary, overlay, "实时播放");
            AssertActiveFrame(window, primary, expectedFrames[frameIndex], frameIndex,
                "实时播放主图层出现了其他动作");

            if (!wakeBurstObserved &&
                frameIndex == 0 &&
                frameTimer.IsEnabled)
            {
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "首个公共苏醒姿势期间");
                wakeBurstObserved = true;
            }

            if (!actionLoopBurstObserved &&
                frameIndex == ActionLoopStartFrameIndex &&
                frameTimer.IsEnabled)
            {
                actionLoopStarted = stopwatch.Elapsed;
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "动作末四姿势循环期间");
                actionLoopBurstObserved = true;
            }

            if (actionLoopStarted is not null && actionLoopEnded is null &&
                frameIndex >= ReverseFrameStartIndex)
            {
                actionLoopEnded = stopwatch.Elapsed;
            }

            if (!returnBurstObserved &&
                frameIndex == ReverseFrameStartIndex &&
                frameTimer.IsEnabled)
            {
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "返程首个动作姿势期间");
                returnBurstObserved = true;
            }
        }

        var elapsed = stopwatch.Elapsed;
        Assert(GetRawField(window, "_activeClip") is null, "实时动作应在 12 秒内返回待机");
        Assert(elapsed >= TimeSpan.FromSeconds(9.2) && elapsed <= TimeSpan.FromSeconds(11),
            $"112 帧动作总时长应约 9.6 秒，实际 {elapsed.TotalMilliseconds:F0}ms");
        Assert(actionLoopStarted is not null && actionLoopEnded is not null,
            "实时播放应完整观察到动作末四姿势循环阶段");
        var actionLoopDuration = actionLoopEnded!.Value - actionLoopStarted!.Value;
        Assert(actionLoopDuration >= TimeSpan.FromSeconds(5.8) &&
               actionLoopDuration <= TimeSpan.FromSeconds(6.5),
            $"动作末四姿势循环应持续约 6 秒，实际 {actionLoopDuration.TotalMilliseconds:F0}ms");
        Assert(wakeBurstObserved, "实时播放应在首个公共苏醒姿势期间覆盖连续点击");
        Assert(actionLoopBurstObserved, "实时播放应在动作末四姿势循环期间覆盖连续点击");
        Assert(returnBurstObserved, "实时播放应在返程首个动作姿势期间覆盖连续点击");
        Assert(observedFrameIndexes.SetEquals(Enumerable.Range(0, MotionFrameCount)),
            $"实时播放应完整经过 112 个帧索引，实际 {string.Join(", ", observedFrameIndexes.Order())}");
        Assert(GetValueField<int>(window, "_nextClipIndex") == 1,
            "忙时连点不应排队或消耗后续动作");
        AssertImage(primary, "luban-idle.png", "实时单动作结束后应回到待机图");
        AssertSingleLayerSettled(primary, overlay);

        PumpDispatcher(TimeSpan.FromMilliseconds(250));
        Assert(GetRawField(window, "_activeClip") is null, "动作结束后不应自动追加下一轮");
        Assert(GetValueField<int>(window, "_nextClipIndex") == 1,
            "动作结束后等待仍应只消费一个 Clip，不应存在隐藏队列");
        AssertImage(primary, "luban-idle.png", "动作结束后应持续保持待机图");
        Assert(!frameTimer.IsEnabled, "动作结束后帧计时器应停止");
    }

    private static void AssertBusyClickBurst(
        MainWindow window,
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer,
        TextBlock messageText,
        string stage)
    {
        const int clickCount = 5;
        for (var clickIndex = 0; clickIndex < clickCount; clickIndex++)
        {
            AssertBusyClickIgnored(window, primary, overlay, frameTimer, messageText,
                $"{stage}第 {clickIndex + 1} 次连点");
        }
    }

    private static void AssertSingleLayerInvariant(Image primary, Image overlay, string stage)
    {
        var bitmap = primary.Source as BitmapSource;
        Assert(bitmap is not null, $"{stage}主图层不应出现空帧");
        if (bitmap is null)
        {
            throw new InvalidOperationException($"{stage}主图层不应出现空帧");
        }
        Assert(bitmap.IsFrozen, $"{stage}主图层位图应预先生成并冻结");
        Assert(bitmap.PixelWidth == 240 && bitmap.PixelHeight is 293 or 294,
            $"{stage}主图层应保持 DecodePixelWidth=240 的统一显示资源，实际 " +
            $"{bitmap.PixelWidth}×{bitmap.PixelHeight}");
        Assert(bitmap.Format == PixelFormats.Pbgra32,
            $"{stage}主图层应始终使用同一种 Pbgra32 显示格式");
        AssertClose(primary.Opacity, 1, $"{stage}主图层必须始终完全不透明");
        Assert(!DependencyPropertyHelper.GetValueSource(primary, UIElement.OpacityProperty).IsAnimated,
            $"{stage}主图层不应再使用透明度动画");
        Assert(overlay.Source is null, $"{stage}闲置覆盖图层不应装载图片");
        AssertClose(overlay.Opacity, 0, $"{stage}闲置覆盖图层应保持透明");
        Assert(overlay.Visibility == Visibility.Collapsed,
            $"{stage}闲置覆盖图层应从渲染树中折叠");
        Assert(!DependencyPropertyHelper.GetValueSource(overlay, UIElement.OpacityProperty).IsAnimated,
            $"{stage}覆盖图层不应有透明度动画");
    }

    private static T GetField<T>(object instance, string name) where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"找不到字段 {name}");
        return field.GetValue(instance) as T
            ?? throw new InvalidOperationException($"字段 {name} 类型不正确");
    }

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"找不到字段 {name}");
        field.SetValue(instance, value);
    }

    private static object GetMainWindowEnumValue(string enumName, string valueName)
    {
        var enumType = typeof(MainWindow).GetNestedType(enumName, BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"找不到 MainWindow.{enumName}");
        return Enum.Parse(enumType, valueName);
    }

    private static Rect GetMonitorWorkArea(MainWindow window)
    {
        var monitorWorkAreaType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.MonitorWorkArea",
            throwOnError: true)
            ?? throw new InvalidOperationException("找不到 MonitorWorkArea");
        var method = monitorWorkAreaType.GetMethod(
            "GetForWindow",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("找不到 MonitorWorkArea.GetForWindow");
        return (Rect)(method.Invoke(null, [window])
            ?? throw new InvalidOperationException("MonitorWorkArea.GetForWindow 不应返回空值"));
    }

    private static void AssertValidWorkArea(Rect workArea, string stage)
    {
        Assert(!workArea.IsEmpty &&
               double.IsFinite(workArea.Left) &&
               double.IsFinite(workArea.Top) &&
               double.IsFinite(workArea.Width) &&
               double.IsFinite(workArea.Height) &&
               workArea.Width > 0 && workArea.Height > 0,
            $"{stage}应为有限且宽高大于 0 的矩形，实际 {workArea}");
    }

    private static void AssertRectClose(Rect actual, Rect expected, string message)
    {
        AssertClose(actual.Left, expected.Left, $"{message}：Left");
        AssertClose(actual.Top, expected.Top, $"{message}：Top");
        AssertClose(actual.Width, expected.Width, $"{message}：Width");
        AssertClose(actual.Height, expected.Height, $"{message}：Height");
    }

    private static bool IsVisualDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsPopupRequestedOpen(Popup popup)
    {
        return popup.IsOpen || Equals(popup.ReadLocalValue(Popup.IsOpenProperty), true);
    }

    private static void Invoke(object instance, string name, params object?[]? arguments)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"找不到方法 {name}");
        method.Invoke(instance, arguments);
    }

    private static T InvokeResult<T>(
        object instance,
        string name,
        params object?[]? arguments)
    {
        var method = instance.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"找不到方法 {name}");
        var result = method.Invoke(instance, arguments);
        return result is T typed
            ? typed
            : throw new InvalidOperationException($"方法 {name} 返回值类型不正确");
    }

    private static object? GetRawField(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"找不到字段 {name}");
        return field.GetValue(instance);
    }

    private static T GetValueField<T>(object instance, string name)
    {
        var value = GetRawField(instance, name)
            ?? throw new InvalidOperationException($"字段 {name} 不应为空");
        return (T)value;
    }

    private static void AssertBusyClickIgnored(
        MainWindow window,
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer,
        TextBlock messageText,
        string stage)
    {
        var primarySource = primary.Source;
        var overlaySource = overlay.Source;
        var activeClip = GetRawField(window, "_activeClip");
        var activeFrameIndex = GetValueField<int>(window, "_activeFrameIndex");
        var nextClipIndex = GetValueField<int>(window, "_nextClipIndex");
        var nextAutomaticSequenceIndex = GetValueField<int>(window, "_nextAutomaticSequenceIndex");
        var message = messageText.Text;
        var timerEnabled = frameTimer.IsEnabled;
        var timerInterval = frameTimer.Interval;
        var width = window.Width;
        var height = window.Height;

        Invoke(window, "ShowCuteReaction");

        Assert(ReferenceEquals(GetRawField(window, "_activeClip"), activeClip),
            $"{stage}再次点击不应更换当前 Clip");
        Assert(GetValueField<int>(window, "_activeFrameIndex") == activeFrameIndex,
            $"{stage}再次点击不应推进或重置帧索引");
        Assert(GetValueField<int>(window, "_nextClipIndex") == nextClipIndex,
            $"{stage}再次点击不应消费下一个 Clip");
        Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") == nextAutomaticSequenceIndex,
            $"{stage}再次点击不应消费自动轮换项");
        Assert(ReferenceEquals(primary.Source, primarySource),
            $"{stage}再次点击不应替换主图层");
        Assert(ReferenceEquals(overlay.Source, overlaySource),
            $"{stage}再次点击不应启用闲置覆盖图层");
        Assert(messageText.Text == message, $"{stage}再次点击不应更新对白");
        Assert(frameTimer.IsEnabled == timerEnabled, $"{stage}再次点击不应重启帧计时器");
        Assert(frameTimer.Interval == timerInterval, $"{stage}再次点击不应修改帧间隔");
        AssertClose(window.Width, width, $"{stage}再次点击不应修改窗口宽度");
        AssertClose(window.Height, height, $"{stage}再次点击不应修改窗口高度");
        AssertSingleLayerInvariant(primary, overlay, stage);
    }

    private static void AdvanceFrameAndAssert(
        MainWindow window,
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer,
        string expectedName,
        int expectedFrameIndex,
        string message)
    {
        Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
        frameTimer.Stop();
        AssertActiveFrame(window, primary, expectedName, expectedFrameIndex, message);
        AssertSingleLayerSettled(primary, overlay);
    }

    private static void AssertActiveFrame(
        MainWindow window,
        Image primary,
        string expectedName,
        int expectedFrameIndex,
        string message)
    {
        var clip = GetRawField(window, "_activeClip")
            ?? throw new InvalidOperationException($"{message}：活动 Clip 不应为空");
        Assert(GetValueField<int>(window, "_activeFrameIndex") == expectedFrameIndex,
            $"{message}：活动帧索引不正确");
        var frame = GetFrame(GetClipFrames(clip), expectedFrameIndex);
        Assert(GetFrameName(frame) == expectedName,
            $"{message}：期望 {expectedName}，实际 {GetFrameName(frame)}");
        Assert(ReferenceEquals(primary.Source, GetFrameImage(frame)),
            $"{message}：主图层必须直接显示 Clip 中的预合成帧实例");
        Assert(GetField<DispatcherTimer>(window, "_frameTimer").Interval ==
               GetFrameHoldDuration(frame),
            $"{message}：帧计时器间隔必须与 HoldDuration 一致");

        Assert(expectedName.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
            $"{message}：112 帧时间线只能包含真实 PNG 资源名");
        AssertImage(primary, expectedName, message);
    }

    private static void AssertLegacyTransitionStateRemoved()
    {
        foreach (var fieldName in new[]
                 {
                     "_transitionTimer",
                     "_activeTransition",
                     "_transitionGeneration",
                     "_transitionFrames"
                 })
        {
            Assert(typeof(MainWindow).GetField(
                       fieldName,
                       BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is null,
                $"新动画模型不应保留旧字段 {fieldName}");
        }

        Assert(typeof(MainWindow).GetMethod(
                   "TransitionTimer_Tick",
                   BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "新动画模型不应保留旧过渡计时器 Tick");
        Assert(typeof(MainWindow).GetMethod(
                   "BuildInterpolatedFrames",
                   BindingFlags.Static | BindingFlags.NonPublic) is null,
            "真实姿势模型不应保留像素混合 BuildInterpolatedFrames");
    }

    private static void AssertLoggingContract()
    {
        var loggerType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.AppLogger",
            throwOnError: true)
            ?? throw new InvalidOperationException("找不到 AppLogger");
        var initialize = loggerType.GetMethod(
            "Initialize",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("找不到 AppLogger.Initialize");
        var info = loggerType.GetMethod(
            "Info",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("找不到 AppLogger.Info");
        var error = loggerType.GetMethod(
            "Error",
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("找不到 AppLogger.Error");
        var probe = $"ui-state-check-{Guid.NewGuid():N}";
        var errorProbe = $"ui-state-error-{Guid.NewGuid():N}";
        var sensitiveMessage = $"sensitive-{Guid.NewGuid():N}";
        var privacyException = new InvalidOperationException(sensitiveMessage);
        var hostileExceptionProbe = $"hostile-exception-{Guid.NewGuid():N}";
        var today = DateTimeOffset.Now;

        initialize.Invoke(null, null);
        info.Invoke(null, [probe]);
        error.Invoke(null, [errorProbe, privacyException]);
        error.Invoke(null, [hostileExceptionProbe, new ThrowingStackTraceException()]);

        var logPath = Path.Combine(
            AppContext.BaseDirectory,
            "log",
            $"xlb-pet-{today:yyyy-MM-dd}.log");
        Assert(File.Exists(logPath), $"当天日志文件不存在：{logPath}");
        var content = File.ReadAllText(logPath);
        Assert(content.Contains("[INFO]", StringComparison.Ordinal) &&
               content.Contains(probe, StringComparison.Ordinal),
            "当天日志应包含本次唯一 INFO 探针");
        Assert(content.Contains("[ERROR]", StringComparison.Ordinal) &&
               content.Contains(errorProbe, StringComparison.Ordinal),
            "当天日志应包含本次唯一 ERROR 探针");
        Assert(!content.Contains(sensitiveMessage, StringComparison.Ordinal),
            "异常日志不得写入 Exception.Message 中的敏感文本");
        Assert(content.Contains(typeof(InvalidOperationException).FullName!,
                   StringComparison.Ordinal),
            "异常日志应保留异常类型，便于排查但不泄露异常消息");
        Assert(content.Contains($"HResult：0x{privacyException.HResult:X8}",
                   StringComparison.Ordinal),
            "异常日志应保留 HResult，便于排查但不泄露异常消息");
        Assert(content.Contains(hostileExceptionProbe, StringComparison.Ordinal) &&
               content.Contains(typeof(ThrowingStackTraceException).FullName!,
                   StringComparison.Ordinal),
            "异常元数据 getter 抛错时，日志仍应安全落盘且不得反向抛出");
    }

    private static void AssertPropertyTransition(
        DependencyObject target,
        DependencyProperty property,
        Action transition,
        Func<bool> reachedTarget,
        Action assertAtTarget,
        string message)
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType())
            ?? throw new InvalidOperationException($"无法监听属性 {property.Name}");
        var observed = false;

        EventHandler handler = (_, _) =>
        {
            if (!reachedTarget())
            {
                return;
            }

            assertAtTarget();
            observed = true;
        };

        descriptor.AddValueChanged(target, handler);
        try
        {
            transition();
        }
        finally
        {
            descriptor.RemoveValueChanged(target, handler);
        }

        Assert(observed, message);
    }

    private static void AssertBubbleBounds(
        Window window,
        double expectedWidth,
        double expectedHeight,
        double expectedRight,
        double expectedBottom,
        string message)
    {
        AssertClose(window.Width, expectedWidth, $"{message}：宽度");
        AssertClose(window.Height, expectedHeight, $"{message}：高度");
        AssertClose(window.Left + window.Width, expectedRight, $"{message}：右侧锚点");
        AssertClose(window.Top + window.Height, expectedBottom, $"{message}：底部锚点");
    }

    private static void AssertImage(Image image, string expectedFileName, string message)
    {
        var bitmap = GetOriginalBitmapImage(image.Source);
        Assert(bitmap is not null &&
               bitmap.UriSource.ToString().EndsWith(expectedFileName, StringComparison.OrdinalIgnoreCase), message);
        Assert(image.Source is BitmapSource { PixelWidth: 240, PixelHeight: 293 or 294 },
            "状态图应按 DecodePixelWidth=240 预解码（450×550 源图输出约 240×293）");
        Assert(image.Source is BitmapSource { Format: var format } && format == PixelFormats.Pbgra32,
            "所有真实动作帧应统一使用 Pbgra32，避免格式切换闪动");
    }

    private static void AssertSingleLayerSettled(Image primary, Image overlay)
    {
        AssertSingleLayerInvariant(primary, overlay, "稳定状态");
    }

    private static BitmapImage? GetOriginalBitmapImage(ImageSource? source)
    {
        return source switch
        {
            BitmapImage bitmap => bitmap,
            FormatConvertedBitmap converted => GetOriginalBitmapImage(converted.Source),
            _ => null
        };
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static FrameworkElement ArrangeWindow(MainWindow window)
    {
        var root = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("窗口没有可渲染内容");
        root.Measure(new Size(window.Width, window.Height));
        root.Arrange(new Rect(0, 0, window.Width, window.Height));
        root.UpdateLayout();
        return root;
    }

    private static void RenderState(MainWindow window, string fileName)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("LUBAN_UI_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var root = ArrangeWindow(window);

        const double scale = 2;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.Width * scale),
            (int)Math.Ceiling(window.Height * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(outputDirectory, fileName));
        encoder.Save(stream);
    }

    private static void AssertClose(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) < 0.01, $"{message}：期望 {expected}，实际 {actual}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ThrowingStackTraceException : Exception
    {
        public override string? StackTrace =>
            throw new InvalidOperationException("stack-trace-getter-must-not-escape");
    }

    private sealed record ExpectedClip(string Message, string ActionName);
}
