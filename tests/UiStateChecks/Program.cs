using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
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
        var transitionTimer = GetField<DispatcherTimer>(window, "_transitionTimer");
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
        AssertInterpolatedFrames(window, reactionClips);
        Assert(automaticTimer.Interval == TimeSpan.FromSeconds(10),
            "自动动画计时器间隔应为 10 秒");
        Assert(!automaticTimer.IsEnabled,
            "窗口加载前不应启动自动动画计时器");
        Assert(transitionTimer.Interval == TimeSpan.FromMilliseconds(20),
            "预合成中间帧应按 20ms 间隔播放");
        Assert(!transitionTimer.IsEnabled,
            "窗口加载前不应启动姿势过渡计时器");

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
        RenderState(window, "frame-transition.png");
        WaitForTransitionToSettle(window, petImage, petImageOverlay, frameTimer);
        AssertImage(petImage, firstClip.Frames[0], "第一个短动作的首帧不正确");
        AssertSingleLayerSettled(petImage, petImageOverlay);
        Assert(frameTimer.Interval == TimeSpan.FromMilliseconds(750),
            "桥接帧应稳定停留 750ms");
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
                    ? TimeSpan.FromSeconds(6)
                    : TimeSpan.FromMilliseconds(750)),
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
                WaitForTransitionToSettle(window, petImage, petImageOverlay, frameTimer);
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
            if (clipIndex == expectedClips.Length - 1)
            {
                AssertBusyClickIgnored(window, petImage, petImageOverlay, frameTimer, cuteMessageText,
                    "思考动作短补间期间");
                RenderState(window, "think-transition.png");
                CompleteTransitionFrameByFrame(window, petImage, petImageOverlay);
            }
            WaitForTransitionToSettle(window, petImage, petImageOverlay, frameTimer);
            Assert(cuteMessageText.Text == clip.Message, $"短动作 {clipIndex + 1} 对白不正确");
            AssertImage(petImage, clip.Frames[0], $"短动作 {clipIndex + 1} 第 1 帧不正确");

            for (var frameIndex = 1; frameIndex < clip.Frames.Length; frameIndex++)
            {
                AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                    clip.Frames[frameIndex], $"短动作 {clipIndex + 1} 第 {frameIndex + 1} 帧");
                Assert(frameTimer.Interval == (frameIndex == clip.Frames.Length / 2
                        ? TimeSpan.FromSeconds(6)
                        : TimeSpan.FromMilliseconds(750)),
                    $"短动作 {clipIndex + 1} 第 {frameIndex + 1} 帧停留时长不正确");
            }

            Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
            frameTimer.Stop();
            WaitForTransitionToSettle(window, petImage, petImageOverlay, frameTimer);
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
        WaitForTransitionToSettle(window, petImage, petImageOverlay, frameTimer);
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
        WaitForTransitionToSettle(window, petImage, petImageOverlay, frameTimer);
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
        WaitForTransitionToSettle(window, primary, overlay, frameTimer);
        while (GetRawField(window, "_activeClip") is not null)
        {
            Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
            frameTimer.Stop();
            WaitForTransitionToSettle(window, primary, overlay, frameTimer);
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
                    ? TimeSpan.FromSeconds(6)
                    : TimeSpan.FromMilliseconds(750)),
                $"{expected.Message} 第 {frameIndex + 1} 帧停留时长不正确");
        }
    }

    private static void AssertInterpolatedFrames(MainWindow window, Array reactionClips)
    {
        var idle = GetField<BitmapImage>(window, "_idleImage");
        var shouldInterpolate = typeof(MainWindow).GetMethod(
            "ShouldInterpolate",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到轮廓门禁方法");

        BitmapImage? thinkBridge = null;
        foreach (var clipIndex in Enumerable.Range(0, reactionClips.Length))
        {
            var clip = reactionClips.GetValue(clipIndex)
                ?? throw new InvalidOperationException($"动作 {clipIndex + 1} Clip 不应为空");
            var clipFrames = GetClipFrames(clip);
            var sequence = new List<BitmapImage> { idle };
            foreach (var frame in clipFrames)
            {
                sequence.Add((BitmapImage)(frame?.GetType().GetProperty("Image")?.GetValue(frame)
                    ?? throw new InvalidOperationException($"动作 {clipIndex + 1} 存在无图片帧")));
            }

            sequence.Add(idle);
            for (var pairIndex = 0; pairIndex < sequence.Count - 1; pairIndex++)
            {
                var source = sequence[pairIndex];
                var target = sequence[pairIndex + 1];
                var expected = clipIndex == reactionClips.Length - 1;
                var forward = (bool)(shouldInterpolate.Invoke(null, [source, target]) ?? false);
                var reverse = (bool)(shouldInterpolate.Invoke(null, [target, source]) ?? false);
                Assert(forward == expected,
                    $"动作 {clipIndex + 1} 第 {pairIndex + 1} 段补间门禁不正确");
                Assert(reverse == expected,
                    $"动作 {clipIndex + 1} 第 {pairIndex + 1} 段返程补间门禁不正确");
            }

            if (clipIndex == reactionClips.Length - 1)
            {
                thinkBridge = sequence[1];
            }
        }

        if (thinkBridge is null)
        {
            throw new InvalidOperationException("缺少思考动作桥接帧");
        }

        var transitionCache = GetRawField(window, "_transitionFrames") as System.Collections.IDictionary;
        Assert(transitionCache is { Count: 4 },
            "思考动作的两组正反向补间应在窗口显示前全部缓存");
        var buildFrames = typeof(MainWindow).GetMethod(
            "BuildInterpolatedFrames",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到预合成中间帧方法");
        var frames = (BitmapSource[])(buildFrames.Invoke(null, [idle, thinkBridge])
            ?? throw new InvalidOperationException("预合成中间帧不应为空"));
        var reverseFrames = (BitmapSource[])(buildFrames.Invoke(null, [thinkBridge, idle])
            ?? throw new InvalidOperationException("返程预合成中间帧不应为空"));

        Assert(frames.Length == 6, "每次姿势切换应生成 6 张中间帧");
        var sourceHash = GetPixelHash(idle);
        var targetHash = GetPixelHash(thinkBridge);
        var previousHash = sourceHash;
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var frame = frames[frameIndex];
            Assert(frame.IsFrozen, "预合成中间帧应冻结后再交给 UI 播放");
            Assert(frame.Format == PixelFormats.Pbgra32, "预合成中间帧应使用 Pbgra32 格式");
            Assert(frame.PixelWidth == 360 && frame.PixelHeight == 440,
                "预合成中间帧应保持 360×440 尺寸");

            var hash = GetPixelHash(frame);
            Assert(hash != sourceHash, "中间帧不应退化为起始姿势");
            Assert(hash != targetHash, "中间帧不应提前退化为目标姿势");
            Assert(hash != previousHash, "相邻中间帧不应重复");
            Assert(hash == GetPixelHash(reverseFrames[frames.Length - frameIndex - 1]),
                "返程补间应与正向补间逐像素反序一致");
            previousHash = hash;
        }

        AssertInterpolationFormula(idle, thinkBridge, frames);
    }

    private static void AssertInterpolationFormula(
        BitmapSource source,
        BitmapSource target,
        IReadOnlyList<BitmapSource> frames)
    {
        var sourcePixels = GetPbgraPixels(source);
        var targetPixels = GetPbgraPixels(target);
        var denominator = frames.Count + 1;
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var actualPixels = GetPbgraPixels(frames[frameIndex]);
            var targetWeight = frameIndex + 1;
            var sourceWeight = denominator - targetWeight;
            for (var byteIndex = 0; byteIndex < actualPixels.Length; byteIndex++)
            {
                var expected = (byte)(
                    (sourcePixels[byteIndex] * sourceWeight +
                     targetPixels[byteIndex] * targetWeight +
                     denominator / 2) /
                    denominator);
                if (actualPixels[byteIndex] != expected)
                {
                    throw new InvalidOperationException(
                        $"第 {frameIndex + 1} 张补间帧第 {byteIndex} 个字节权重方向不正确");
                }
            }
        }
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
        var entryBridgeBurstObserved = false;
        var actionHoldBurstObserved = false;
        var returnBridgeBurstObserved = false;
        var interpolatedSamples = 0;
        TimeSpan? actionHoldStarted = null;
        TimeSpan? actionHoldEnded = null;
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
            AssertAllowedRuntimeFrame(primary, allowedFrames, "实时播放主图层出现了其他动作");

            if (IsTransitionInProgress(window))
            {
                interpolatedSamples++;
            }

            if (!entryBridgeBurstObserved &&
                frameIndex == 0 &&
                !IsTransitionInProgress(window) &&
                frameTimer.IsEnabled)
            {
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "首个完整不透明桥接姿势期间");
                entryBridgeBurstObserved = true;
            }

            if (!actionHoldBurstObserved &&
                frameIndex == 2 &&
                !IsTransitionInProgress(window) &&
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

            if (!returnBridgeBurstObserved &&
                frameIndex == 4 &&
                !IsTransitionInProgress(window) &&
                frameTimer.IsEnabled)
            {
                AssertBusyClickBurst(window, primary, overlay, frameTimer, messageText,
                    "返回待机的完整不透明桥接姿势期间");
                returnBridgeBurstObserved = true;
            }
        }

        var elapsed = stopwatch.Elapsed;
        Assert(GetRawField(window, "_activeClip") is null, "实时动作应在 12 秒内返回待机");
        Assert(elapsed >= TimeSpan.FromSeconds(8.6) && elapsed <= TimeSpan.FromSeconds(9.8),
            $"放慢后的两桥接动作总时长应约 9 秒，实际 {elapsed.TotalMilliseconds:F0}ms");
        Assert(actionHoldStarted is not null && actionHoldEnded is not null,
            "实时播放应完整观察到动作主体的静止阶段");
        var actionHold = actionHoldEnded!.Value - actionHoldStarted!.Value;
        Assert(actionHold >= TimeSpan.FromSeconds(5.8) && actionHold <= TimeSpan.FromSeconds(6.5),
            $"动作主体应静止约 6 秒，实际 {actionHold.TotalMilliseconds:F0}ms");
        Assert(entryBridgeBurstObserved, "实时播放应在首个完整不透明桥接姿势期间覆盖连续点击");
        Assert(actionHoldBurstObserved, "实时播放应在动作持帧期间覆盖连续点击");
        Assert(returnBridgeBurstObserved, "实时播放应在返回待机的完整不透明桥接姿势期间覆盖连续点击");
        Assert(observedFrameIndexes.SetEquals([0, 1, 2, 3, 4]),
            $"实时播放应且仅应经过 5 个帧索引，实际 {string.Join(", ", observedFrameIndexes.Order())}");
        Assert(GetValueField<int>(window, "_nextClipIndex") == 1,
            "忙时连点不应排队或消耗后续动作");
        Assert(interpolatedSamples == 0,
            $"哈欠轮廓跨度较大，不应出现淡化中间帧，实际采样 {interpolatedSamples}");
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

    private static void AssertAllowedRuntimeFrame(
        Image image,
        IReadOnlySet<string> allowedFrames,
        string message)
    {
        var bitmap = GetOriginalBitmapImage(image.Source);
        if (bitmap is null)
        {
            return;
        }

        var fileName = Path.GetFileName(bitmap.UriSource.LocalPath);
        Assert(allowedFrames.Contains(fileName), $"{message}：{fileName}");
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
        Assert(bitmap.PixelWidth == 360 && bitmap.PixelHeight == 440,
            $"{stage}主图层应保持 360×440 的高质量显示资源");
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
        var transitionGeneration = GetValueField<int>(window, "_transitionGeneration");
        var activeTransition = GetRawField(window, "_activeTransition");
        var transitionTimer = GetField<DispatcherTimer>(window, "_transitionTimer");
        var transitionTimerEnabled = transitionTimer.IsEnabled;
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
        Assert(GetValueField<int>(window, "_transitionGeneration") == transitionGeneration,
            $"{stage}再次点击不应重启预合成过渡");
        Assert(ReferenceEquals(GetRawField(window, "_activeTransition"), activeTransition),
            $"{stage}再次点击不应替换当前预合成过渡");
        Assert(ReferenceEquals(primary.Source, primarySource),
            $"{stage}再次点击不应替换主图层");
        Assert(ReferenceEquals(overlay.Source, overlaySource),
            $"{stage}再次点击不应启用闲置覆盖图层");
        Assert(messageText.Text == message, $"{stage}再次点击不应更新对白");
        Assert(frameTimer.IsEnabled == timerEnabled, $"{stage}再次点击不应重启帧计时器");
        Assert(frameTimer.Interval == timerInterval, $"{stage}再次点击不应修改帧间隔");
        Assert(transitionTimer.IsEnabled == transitionTimerEnabled,
            $"{stage}再次点击不应重启过渡计时器");
        AssertClose(window.Width, width, $"{stage}再次点击不应修改窗口宽度");
        AssertClose(window.Height, height, $"{stage}再次点击不应修改窗口高度");
        AssertSingleLayerInvariant(primary, overlay, stage);
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
        WaitForTransitionToSettle(window, primary, overlay, frameTimer);
        AssertImage(primary, expectedFileName, message);
        AssertSingleLayerSettled(primary, overlay);
    }

    private static void WaitForTransitionToSettle(
        MainWindow window,
        Image primary,
        Image overlay,
        DispatcherTimer frameTimer)
    {
        var transitionTimer = GetField<DispatcherTimer>(window, "_transitionTimer");
        const int maximumAttempts = 100;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            frameTimer.Stop();
            if (GetRawField(window, "_activeTransition") is null &&
                !transitionTimer.IsEnabled)
            {
                AssertSingleLayerSettled(primary, overlay);
                return;
            }

            PumpDispatcher(TimeSpan.FromMilliseconds(10));
        }

        frameTimer.Stop();
        Assert(GetRawField(window, "_activeTransition") is null,
            "预合成姿势过渡应在 1 秒内完成");
        Assert(!transitionTimer.IsEnabled, "姿势过渡完成后应停止过渡计时器");
        AssertSingleLayerSettled(primary, overlay);
    }

    private static void CompleteTransitionFrameByFrame(
        MainWindow window,
        Image primary,
        Image overlay)
    {
        var transitionTimer = GetField<DispatcherTimer>(window, "_transitionTimer");
        transitionTimer.Stop();
        var intermediateFrameCount = 0;
        while (GetRawField(window, "_activeTransition") is not null)
        {
            AssertSingleLayerInvariant(primary, overlay, "逐帧补间播放");
            Assert(GetOriginalBitmapImage(primary.Source) is null,
                "补间尚未完成时主图应为预生成中间帧");
            intermediateFrameCount++;
            Assert(intermediateFrameCount <= 6, "补间播放不应超过 6 张中间帧");

            Invoke(window, "TransitionTimer_Tick", null, EventArgs.Empty);
            transitionTimer.Stop();
        }

        Assert(intermediateFrameCount == 6,
            $"思考动作每段应完整播放 6 张中间帧，实际 {intermediateFrameCount}");
        Assert(!transitionTimer.IsEnabled, "逐帧补间完成后应停止过渡计时器");
        AssertSingleLayerSettled(primary, overlay);
    }

    private static bool IsTransitionInProgress(MainWindow window)
    {
        return GetRawField(window, "_activeTransition") is not null;
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
        Assert(image.Source is BitmapSource { PixelWidth: 360, PixelHeight: 440 },
            "状态图应预解码为 360×440，避免切换时重复缩放大图");
        Assert(image.Source is BitmapSource { Format: var format } && format == PixelFormats.Pbgra32,
            "稳定状态与补间帧应统一使用 Pbgra32，避免终点格式切换闪动");
    }

    private static void AssertSingleLayerSettled(Image primary, Image overlay)
    {
        AssertSingleLayerInvariant(primary, overlay, "稳定状态");
        Assert(GetOriginalBitmapImage(primary.Source) is not null,
            "稳定状态应落在精确的原始姿势图上");
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

    private sealed record ExpectedClip(string Message, string[] Frames);
}
