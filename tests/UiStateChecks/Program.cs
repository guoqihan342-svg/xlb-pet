using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LubanDesktopPet;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        _ = new Application();
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
            new ExpectedClip("刚睡醒，让我伸个懒腰～",
                ["luban-idle-to-yawn.png", "luban-idle-to-yawn-2.png", "luban-yawn.png",
                    "luban-idle-to-yawn-2.png", "luban-idle-to-yawn.png"]),
            new ExpectedClip("呜……主人要哄哄我",
                ["luban-idle-to-cry.png", "luban-idle-to-cry-2.png", "luban-cry.png",
                    "luban-idle-to-cry-2.png", "luban-idle-to-cry.png"]),
            new ExpectedClip("小鲁班出发！",
                ["luban-idle-to-run.png", "luban-idle-to-run-2.png", "luban-run.png",
                    "luban-idle-to-run-2.png", "luban-idle-to-run.png"]),
            new ExpectedClip("给你卖个萌 ♡",
                ["luban-idle-to-cute-1.png", "luban-idle-to-cute.png", "luban-cute.png",
                    "luban-idle-to-cute.png", "luban-idle-to-cute-1.png"]),
            new ExpectedClip("主人真棒！",
                ["luban-idle-to-like.png", "luban-idle-to-like-2.png", "luban-like.png",
                    "luban-idle-to-like-2.png", "luban-idle-to-like.png"]),
            new ExpectedClip("吃块饼干，补充能量！",
                ["luban-idle-to-eat.png", "luban-eat.png", "luban-idle-to-eat.png"]),
            new ExpectedClip("嗨～我在这里！",
                ["luban-idle-to-wave.png", "luban-idle-to-wave-2.png", "luban-wave.png",
                    "luban-idle-to-wave-2.png", "luban-idle-to-wave.png"]),
            new ExpectedClip("让我认真想一想……",
                ["luban-think-to-idle.png", "luban-think.png", "luban-think-to-idle.png"])
        };
        var reactionClips = GetField<Array>(window, "_reactionClips");
        Assert(reactionClips.Length == expectedClips.Length,
            "应配置 8 个独立短动作 Clip");
        for (var clipIndex = 0; clipIndex < reactionClips.Length; clipIndex++)
        {
            AssertClipTiming(reactionClips.GetValue(clipIndex)!, expectedClips[clipIndex]);
        }
        Assert(automaticTimer.Interval == TimeSpan.FromSeconds(10),
            "自动动画计时器间隔应为 10 秒");
        Assert(!automaticTimer.IsEnabled,
            "窗口加载前不应启动自动动画计时器");

        AssertClose(window.Width, 145, "收起时宽度");
        AssertClose(window.Height, 185, "收起时高度");
        AssertImage(petImage, "luban-idle.png", "启动应显示待机图");
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
        WaitForCrossFadeSample(petImage, petImageOverlay);
        RenderState(window, "frame-transition.png");
        WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
        AssertImage(petImage, firstClip.Frames[0], "第一个短动作的首帧不正确");
        AssertCrossFadeSettled(petImage, petImageOverlay);
        Assert(frameTimer.Interval == TimeSpan.FromMilliseconds(220),
            "桥接帧应稳定停留 220ms");
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

        for (var frameIndex = 1; frameIndex < firstClip.Frames.Length; frameIndex++)
        {
            AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                firstClip.Frames[frameIndex], $"短动作 1 第 {frameIndex + 1} 帧");
            Assert(frameTimer.Interval == (frameIndex == firstClip.Frames.Length / 2
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromMilliseconds(220)),
                $"短动作 1 第 {frameIndex + 1} 帧停留时长不正确");
        }

        AssertPropertyTransition(
            cuteBubble,
            UIElement.VisibilityProperty,
            () =>
            {
                Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
                frameTimer.Stop();
                AssertImage(petImageOverlay, "luban-idle.png", "短动作结束后应开始淡回待机图");
                AssertBusyClickIgnored(window, petImage, petImageOverlay, frameTimer, cuteMessageText,
                    "返回待机的交叉淡入期间");
                WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
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
            Invoke(window, "ShowCuteReaction");
            ArrangeWindow(window);
            WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
            Assert(cuteMessageText.Text == clip.Message, $"短动作 {clipIndex + 1} 对白不正确");
            AssertImage(petImage, clip.Frames[0], $"短动作 {clipIndex + 1} 第 1 帧不正确");

            for (var frameIndex = 1; frameIndex < clip.Frames.Length; frameIndex++)
            {
                AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                    clip.Frames[frameIndex], $"短动作 {clipIndex + 1} 第 {frameIndex + 1} 帧");
                Assert(frameTimer.Interval == (frameIndex == clip.Frames.Length / 2
                        ? TimeSpan.FromSeconds(5)
                        : TimeSpan.FromMilliseconds(220)),
                    $"短动作 {clipIndex + 1} 第 {frameIndex + 1} 帧停留时长不正确");
            }

            Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
            frameTimer.Stop();
            WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
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
        Invoke(window, "ShowCuteReaction");
        ArrangeWindow(window);
        WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
        AssertImage(petImage, todoClip.Frames[0], "待办模式中的短动作首帧不正确");
        Assert(todoBubble.Visibility == Visibility.Visible, "待办打开时人物动画不应关闭待办");
        AssertClose(window.Width, 145, "待办打开时人物动画不应改变宠物窗口宽度");
        RenderState(window, "todo-animated.png");

        for (var frameIndex = 1; frameIndex < todoClip.Frames.Length; frameIndex++)
        {
            AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                todoClip.Frames[frameIndex], $"待办模式短动作第 {frameIndex + 1} 帧");
            Assert(todoBubble.Visibility == Visibility.Visible,
                "待办模式中的人物动画不应在中途关闭待办");
        }

        Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
        frameTimer.Stop();
        WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
        AssertImage(petImage, "luban-idle.png", "待办模式中的短动作结束后应回到待机");
        Assert(todoBubble.Visibility == Visibility.Visible, "人物动画结束不应关闭已打开的待办");
        AssertClose(window.Width, 145, "人物动画结束后宠物窗口宽度应保持不变");
        AssertClose(window.Height, 185, "人物动画结束后宠物窗口高度应保持不变");
        AssertClose(bubbleColumn.Width.Value, 0, "Popup 模式不应占用主窗口气泡列");
        AssertClose(gapColumn.Width.Value, 0, "Popup 模式不应占用主窗口间隔列");

        AssertAutomaticAnimationContract();
        AssertRealTimeAutomaticTrigger();
        AssertRealTimeSingleAction();

        Application.Current.Shutdown();
        Console.WriteLine("UI state checks passed.");
    }

    private static void AssertAutomaticAnimationContract()
    {
        var window = new MainWindow();
        var primary = GetField<Image>(window, "PetImage");
        var overlay = GetField<Image>(window, "PetImageOverlay");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var cuteBubble = GetField<Border>(window, "CuteBubble");
        var expectedAutomaticFrames = new[]
        {
            "luban-yawn.png",
            "luban-idle.png",
            "luban-cry.png",
            "luban-run.png",
            "luban-cute.png",
            "luban-like.png",
            "luban-eat.png",
            "luban-wave.png",
            "luban-think.png"
        };

        Invoke(window, "Window_Loaded", window, new RoutedEventArgs());
        Assert(automaticTimer.IsEnabled, "窗口加载后应启动自动动画计时器");
        Assert(automaticTimer.Interval == TimeSpan.FromSeconds(10),
            "自动动画计时器应保持 10 秒周期");
        automaticTimer.Stop();

        var expectedManualIndex = 0;
        for (var sequenceIndex = 0; sequenceIndex < expectedAutomaticFrames.Length; sequenceIndex++)
        {
            Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") == sequenceIndex,
                $"自动轮换第 {sequenceIndex + 1} 项开始前索引不正确");
            Invoke(window, "AutomaticTimer_Tick", null, EventArgs.Empty);
            Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") ==
                   (sequenceIndex + 1) % expectedAutomaticFrames.Length,
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
                AssertCrossFadeSettled(primary, overlay);

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
            Assert(GetCanonicalFrameName(activeClip) == expectedAutomaticFrames[sequenceIndex],
                $"自动轮换第 {sequenceIndex + 1} 项资源不正确");
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
        AssertCrossFadeSettled(primary, overlay);

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
        WaitForCrossFadeToSettle(primary, overlay, frameTimer);
        while (GetRawField(window, "_activeClip") is not null)
        {
            Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
            frameTimer.Stop();
            WaitForCrossFadeToSettle(primary, overlay, frameTimer);
        }

        AssertImage(primary, "luban-idle.png", "动作完成后应回到抱枕待机图");
        AssertCrossFadeSettled(primary, overlay);
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
        Assert(GetCanonicalFrameName(activeClip) == "luban-yawn.png",
            "第一次真实自动触发应播放打哈欠动作");
        Assert(GetValueField<int>(window, "_nextAutomaticSequenceIndex") == 1,
            "真实自动触发应且仅应消费一个轮换项");

        Invoke(window, "Window_Closing", window, new CancelEventArgs());
        Assert(!automaticTimer.IsEnabled, "真实自动计时测试结束后应停止计时器");
        Assert(!frameTimer.IsEnabled, "窗口关闭时应停止动作帧计时器");
    }

    private static void AssertClipTiming(object clip, ExpectedClip expected)
    {
        var frames = GetClipFrames(clip);
        Assert(frames.Length == expected.Frames.Length,
            $"{expected.Message} 的帧数不正确");

        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var frame = frames.GetValue(frameIndex)
                ?? throw new InvalidOperationException("动画帧不应为空");
            var image = (BitmapImage)(frame.GetType().GetProperty("Image")?.GetValue(frame)
                ?? throw new InvalidOperationException("动画帧缺少图片"));
            var holdDuration = (TimeSpan)(frame.GetType().GetProperty("HoldDuration")?.GetValue(frame)
                ?? throw new InvalidOperationException("动画帧缺少停留时长"));
            Assert(Path.GetFileName(image.UriSource.LocalPath)
                    .Equals(expected.Frames[frameIndex], StringComparison.OrdinalIgnoreCase),
                $"{expected.Message} 第 {frameIndex + 1} 帧资源不正确");
            Assert(holdDuration == (frameIndex == frames.Length / 2
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromMilliseconds(220)),
                $"{expected.Message} 第 {frameIndex + 1} 帧停留时长不正确");
        }
    }

    private static Array GetClipFrames(object clip)
    {
        return (Array)(clip.GetType().GetProperty("Frames")?.GetValue(clip)
            ?? throw new InvalidOperationException("Clip 缺少 Frames"));
    }

    private static string GetCanonicalFrameName(object clip)
    {
        var frames = GetClipFrames(clip);
        var canonicalFrame = frames.GetValue(frames.Length / 2)
            ?? throw new InvalidOperationException("Clip 主体帧不应为空");
        var image = (BitmapImage)(canonicalFrame.GetType().GetProperty("Image")?.GetValue(canonicalFrame)
            ?? throw new InvalidOperationException("主体帧缺少图片"));
        return Path.GetFileName(image.UriSource.LocalPath);
    }

    private static void AssertRealTimeSingleAction()
    {
        var window = new MainWindow();
        var primary = GetField<Image>(window, "PetImage");
        var overlay = GetField<Image>(window, "PetImageOverlay");
        var frameTimer = GetField<DispatcherTimer>(window, "_frameTimer");
        var messageText = GetField<TextBlock>(window, "CuteMessageText");
        var allowedFrames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "luban-idle.png",
            "luban-idle-to-yawn.png",
            "luban-idle-to-yawn-2.png",
            "luban-yawn.png"
        };
        var observedFrameIndexes = new HashSet<int>();
        var entryFadeBurstObserved = false;
        var actionHoldBurstObserved = false;
        var returnFadeBurstObserved = false;
        var blendedSamples = 0;
        TimeSpan? actionHoldStarted = null;
        TimeSpan? actionHoldEnded = null;
        var stopwatch = Stopwatch.StartNew();

        Invoke(window, "ShowCuteReaction");

        while (GetRawField(window, "_activeClip") is not null &&
               stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(16));
            if (GetRawField(window, "_activeClip") is null)
            {
                break;
            }

            var frameIndex = GetValueField<int>(window, "_activeFrameIndex");
            observedFrameIndexes.Add(frameIndex);
            AssertLayerContinuity(primary, overlay, "实时播放");
            AssertAllowedRuntimeFrame(primary, allowedFrames, "实时播放主图层出现了其他动作");
            AssertAllowedRuntimeFrame(overlay, allowedFrames, "实时播放覆盖图层出现了其他动作");

            if (IsCrossFadeInProgress(primary, overlay))
            {
                blendedSamples++;
                Assert(Math.Max(primary.Opacity, overlay.Opacity) >= 0.99,
                    "实时过渡中必须始终有一个完全可见的图层，避免人物变暗闪烁");
            }

            if (!entryFadeBurstObserved &&
                frameIndex == 0 &&
                IsImageNamed(overlay, "luban-idle-to-yawn.png") &&
                IsCrossFadeInProgress(primary, overlay))
            {
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "首帧淡入期间");
                entryFadeBurstObserved = true;
            }

            if (!actionHoldBurstObserved &&
                frameIndex == 2 &&
                overlay.Source is null &&
                frameTimer.IsEnabled)
            {
                actionHoldStarted = stopwatch.Elapsed;
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "动作持帧期间");
                actionHoldBurstObserved = true;
            }

            if (actionHoldStarted is not null && actionHoldEnded is null && frameIndex > 2)
            {
                actionHoldEnded = stopwatch.Elapsed;
            }

            if (!returnFadeBurstObserved &&
                frameIndex == 4 &&
                IsImageNamed(overlay, "luban-idle.png") &&
                IsCrossFadeInProgress(primary, overlay))
            {
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "返回待机淡入期间");
                returnFadeBurstObserved = true;
            }
        }

        var elapsed = stopwatch.Elapsed;
        Assert(GetRawField(window, "_activeClip") is null, "实时动作应在 10 秒内返回待机");
        Assert(elapsed >= TimeSpan.FromSeconds(7.2) && elapsed <= TimeSpan.FromSeconds(9.1),
            $"两桥接动作总时长应约 7.8 秒，实际 {elapsed.TotalMilliseconds:F0}ms");
        Assert(actionHoldStarted is not null && actionHoldEnded is not null,
            "实时播放应完整观察到动作主体的静止阶段");
        var actionHold = actionHoldEnded!.Value - actionHoldStarted!.Value;
        Assert(actionHold >= TimeSpan.FromSeconds(4.85) && actionHold <= TimeSpan.FromSeconds(5.45),
            $"动作主体应静止约 5 秒，实际 {actionHold.TotalMilliseconds:F0}ms");
        Assert(entryFadeBurstObserved, "实时播放应在首帧淡入期间覆盖连续点击");
        Assert(actionHoldBurstObserved, "实时播放应在动作持帧期间覆盖连续点击");
        Assert(returnFadeBurstObserved, "实时播放应在返回待机淡入期间覆盖连续点击");
        Assert(observedFrameIndexes.SetEquals([0, 1, 2, 3, 4]),
            $"实时播放应且仅应经过 5 个帧索引，实际 {string.Join(", ", observedFrameIndexes.Order())}");
        Assert(GetValueField<int>(window, "_nextClipIndex") == 1,
            "忙时连点不应排队或消耗后续动作");
        Assert(blendedSamples >= 24,
            $"实时播放应观察到足够的渐变采样点，实际 {blendedSamples}");
        AssertImage(primary, "luban-idle.png", "实时单动作结束后应回到待机图");
        AssertCrossFadeSettled(primary, overlay);

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

    private static void AssertAllowedRuntimeFrame(
        Image image,
        IReadOnlySet<string> allowedFrames,
        string message)
    {
        if (image.Source is not BitmapImage bitmap)
        {
            return;
        }

        var fileName = Path.GetFileName(bitmap.UriSource.LocalPath);
        Assert(allowedFrames.Contains(fileName), $"{message}：{fileName}");
    }

    private static void AssertLayerContinuity(Image primary, Image overlay, string stage)
    {
        Assert(primary.Source is not null, $"{stage}主图层不应出现空帧");
        Assert(primary.Opacity >= -0.01 && primary.Opacity <= 1.01,
            $"{stage}主图层透明度越界：{primary.Opacity:F3}");
        Assert(overlay.Opacity >= -0.01 && overlay.Opacity <= 1.01,
            $"{stage}覆盖图层透明度越界：{overlay.Opacity:F3}");

        if (overlay.Source is null)
        {
            Assert(primary.Opacity >= 0.99 && overlay.Opacity <= 0.01,
                $"{stage}覆盖图层为空时主图层必须完全可见");
            return;
        }

        Assert(Math.Max(primary.Opacity, overlay.Opacity) >= 0.99,
            $"{stage}必须始终有一个完全可见图层，实际最大透明度 " +
            $"{Math.Max(primary.Opacity, overlay.Opacity):F3}");
    }

    private static T GetField<T>(object instance, string name) where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"找不到字段 {name}");
        return field.GetValue(instance) as T
            ?? throw new InvalidOperationException($"字段 {name} 类型不正确");
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
        var fadeGeneration = GetValueField<int>(window, "_fadeGeneration");
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
        Assert(GetValueField<int>(window, "_fadeGeneration") == fadeGeneration,
            $"{stage}再次点击不应重启动画淡入");
        Assert(ReferenceEquals(primary.Source, primarySource),
            $"{stage}再次点击不应替换主图层");
        Assert(ReferenceEquals(overlay.Source, overlaySource),
            $"{stage}再次点击不应替换淡入图层");
        Assert(messageText.Text == message, $"{stage}再次点击不应更新对白");
        Assert(frameTimer.IsEnabled == timerEnabled, $"{stage}再次点击不应重启帧计时器");
        Assert(frameTimer.Interval == timerInterval, $"{stage}再次点击不应修改帧间隔");
        AssertClose(window.Width, width, $"{stage}再次点击不应修改窗口宽度");
        AssertClose(window.Height, height, $"{stage}再次点击不应修改窗口高度");
    }

    private static void AdvanceFrameAndAssert(
        MainWindow window,
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer,
        string expectedFileName,
        string message)
    {
        Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
        frameTimer.Stop();
        WaitForCrossFadeToSettle(primary, overlay, frameTimer);
        AssertImage(primary, expectedFileName, message);
        AssertCrossFadeSettled(primary, overlay);
    }

    private static void WaitForCrossFadeToSettle(
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer)
    {
        const int maximumAttempts = 80;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            frameTimer.Stop();
            if (overlay.Source is null &&
                Math.Abs(primary.Opacity - 1) < 0.01 &&
                Math.Abs(overlay.Opacity) < 0.01)
            {
                return;
            }

            PumpDispatcher(TimeSpan.FromMilliseconds(10));
        }

        frameTimer.Stop();
        AssertCrossFadeSettled(primary, overlay);
    }

    private static void WaitForCrossFadeSample(Image primary, Image overlay)
    {
        const int maximumAttempts = 40;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            if (IsCrossFadeInProgress(primary, overlay))
            {
                Assert(Math.Max(primary.Opacity, overlay.Opacity) >= 0.99,
                    "过渡过程中必须始终有一个完全可见的图层");
                return;
            }

            PumpDispatcher(TimeSpan.FromMilliseconds(5));
        }

        throw new InvalidOperationException(
            $"未采样到交叉淡入中间状态：主图层 {primary.Opacity:F3}，覆盖图层 {overlay.Opacity:F3}");
    }

    private static bool IsCrossFadeInProgress(Image primary, Image overlay)
    {
        return overlay.Source is not null &&
               ((primary.Opacity >= 0.99 && overlay.Opacity > 0.01 && overlay.Opacity < 0.99) ||
                (overlay.Opacity >= 0.99 && primary.Opacity > 0.01 && primary.Opacity < 0.99));
    }

    private static bool IsImageNamed(Image image, string expectedFileName)
    {
        return image.Source is BitmapImage bitmap &&
               Path.GetFileName(bitmap.UriSource.LocalPath)
                   .Equals(expectedFileName, StringComparison.OrdinalIgnoreCase);
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
        Assert(image.Source is BitmapImage bitmap &&
               bitmap.UriSource.ToString().EndsWith(expectedFileName, StringComparison.OrdinalIgnoreCase), message);
        Assert(image.Source is BitmapImage { PixelWidth: 900, PixelHeight: 1100 },
            "状态图应保持 900×1100 高分辨率资源");
    }

    private static void AssertCrossFadeSettled(Image primary, Image overlay)
    {
        AssertClose(primary.Opacity, 1, "交叉淡入完成后主图层应完全可见");
        AssertClose(overlay.Opacity, 0, "交叉淡入完成后覆盖图层应透明");
        Assert(overlay.Source is null, "交叉淡入完成后覆盖图层应释放图片");
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

    private sealed record ExpectedClip(string Message, string[] Frames);
}
