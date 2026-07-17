using System.ComponentModel;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        var petHost = GetField<Grid>(window, "PetHost");
        var cuteBubble = GetField<Border>(window, "CuteBubble");
        var todoBubble = GetField<Border>(window, "TodoBubble");
        var todoInput = GetField<TextBox>(window, "TodoInput");
        var bubbleColumn = GetField<ColumnDefinition>(window, "BubbleColumn");
        var gapColumn = GetField<ColumnDefinition>(window, "GapColumn");
        var bubbleHost = GetField<Grid>(window, "BubbleHost");
        var bubbleTailHost = GetField<Grid>(window, "BubbleTailHost");
        var cuteMessageText = GetField<TextBlock>(window, "CuteMessageText");

        var expectedClips = new[]
        {
            new ExpectedClip("刚睡醒，让我伸个懒腰～",
                ["luban-idle-to-yawn.png", "luban-yawn.png", "luban-idle-to-yawn.png"]),
            new ExpectedClip("呜……主人要哄哄我",
                ["luban-idle-to-cry.png", "luban-yawn-to-cry.png", "luban-cry.png",
                    "luban-yawn-to-cry.png", "luban-idle-to-cry.png"]),
            new ExpectedClip("小鲁班出发！",
                ["luban-idle-to-run.png", "luban-run.png", "luban-idle-to-run.png"]),
            new ExpectedClip("给你卖个萌 ♡",
                ["luban-idle-to-cute.png", "luban-cute.png", "luban-idle-to-cute.png"]),
            new ExpectedClip("主人真棒！",
                ["luban-idle-to-like.png", "luban-like-to-cute.png", "luban-like.png",
                    "luban-like-to-cute.png", "luban-idle-to-like.png"]),
            new ExpectedClip("吃块饼干，补充能量！",
                ["luban-idle-to-eat.png", "luban-eat-to-run.png", "luban-eat.png",
                    "luban-eat-to-run.png", "luban-idle-to-eat.png"]),
            new ExpectedClip("嗨～我在这里！",
                ["luban-idle-to-wave.png", "luban-run-to-wave.png", "luban-wave.png",
                    "luban-run-to-wave.png", "luban-idle-to-wave.png"]),
            new ExpectedClip("让我认真想一想……",
                ["luban-think-to-idle.png", "luban-think.png", "luban-think-to-idle.png"])
        };
        Assert(GetField<Array>(window, "_reactionClips").Length == expectedClips.Length,
            "应配置 8 个独立短动作 Clip");

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
                AssertBubbleBounds(window, 372, 185, initialRight, initialBottom,
                    "卖萌气泡显示时窗口应已完成定位");
                Assert(bubbleHost.Visibility == Visibility.Visible,
                    "卖萌气泡显示时气泡容器应已可见");
                Assert(bubbleTailHost.Visibility == Visibility.Visible,
                    "卖萌气泡显示时气泡尾巴应已可见");
            },
            "卖萌气泡应在窗口完成展开后再显示");
        ArrangeWindow(window);
        PumpDispatcher(TimeSpan.FromMilliseconds(48));
        Assert(petImage.Opacity > 0.01 && petImage.Opacity < 0.99,
            $"过渡中主图层透明度应处于渐变状态，实际 {petImage.Opacity:F3}");
        Assert(petImageOverlay.Opacity > 0.01 && petImageOverlay.Opacity < 0.99,
            $"过渡中覆盖图层透明度应处于渐变状态，实际 {petImageOverlay.Opacity:F3}");
        Assert(Math.Abs(petImage.Opacity + petImageOverlay.Opacity - 1) < 0.12,
            "交叉淡入过程中两层透明度之和应接近 1");
        RenderState(window, "frame-transition.png");
        WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
        AssertImage(petImage, firstClip.Frames[0], "第一个短动作的首帧不正确");
        AssertCrossFadeSettled(petImage, petImageOverlay);
        Assert(cuteMessageText.Text == firstClip.Message, "第一个短动作对白不正确");
        Assert(cuteBubble.Visibility == Visibility.Visible, "单击后应显示卖萌对话气泡");
        AssertClose(window.Width, 372, "卖萌气泡展开宽度");
        AssertClose(window.Height, 185, "卖萌气泡不应改变底部高度");
        AssertClose(window.Left + window.Width, initialRight, "卖萌气泡应向左展开");
        AssertClose(window.Top + window.Height, initialBottom, "卖萌气泡展开时底部应固定");
        RenderState(window, "clip-1-frame-1.png");

        AssertBusyClickIgnored(window, petImage, petImageOverlay, frameTimer, cuteMessageText,
            "短动作播放期间");

        for (var frameIndex = 1; frameIndex < firstClip.Frames.Length; frameIndex++)
        {
            AdvanceFrameAndAssert(window, petImage, petImageOverlay, frameTimer,
                firstClip.Frames[frameIndex], $"短动作 1 第 {frameIndex + 1} 帧");
        }

        AssertPropertyTransition(
            bubbleColumn,
            ColumnDefinition.WidthProperty,
            () =>
            {
                Invoke(window, "FrameTimer_Tick", null, EventArgs.Empty);
                frameTimer.Stop();
                AssertImage(petImageOverlay, "luban-idle.png", "短动作结束后应开始淡回待机图");
                AssertBusyClickIgnored(window, petImage, petImageOverlay, frameTimer, cuteMessageText,
                    "返回待机的交叉淡入期间");
                WaitForCrossFadeToSettle(petImage, petImageOverlay, frameTimer);
            },
            () => bubbleColumn.Width.IsAbsolute && Math.Abs(bubbleColumn.Width.Value) < 0.01,
            () =>
            {
                AssertBubbleBounds(window, 145, 185, initialRight, initialBottom,
                    "卖萌气泡收回时窗口应先完成定位");
                AssertClose(gapColumn.Width.Value, 0, "卖萌气泡收回时间隔列宽");
                Assert(bubbleHost.Visibility == Visibility.Collapsed,
                    "卖萌气泡收回前气泡容器应已隐藏");
                Assert(bubbleTailHost.Visibility == Visibility.Collapsed,
                    "卖萌气泡收回前气泡尾巴应已隐藏");
            },
            "卖萌气泡应在窗口完成收回后再把宠物移回第一列");

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
                AssertBubbleBounds(window, 437, 240, initialRight, initialBottom,
                    "待办气泡显示时窗口应已完成定位");
                Assert(bubbleHost.Visibility == Visibility.Visible,
                    "待办气泡显示时气泡容器应已可见");
                Assert(bubbleTailHost.Visibility == Visibility.Visible,
                    "待办气泡显示时气泡尾巴应已可见");
            },
            "待办气泡应在窗口完成展开后再显示");

        Assert(todoBubble.Visibility == Visibility.Visible, "右键待办状态应显示白色待办气泡");
        Assert(todoBubble.Background is SolidColorBrush brush && brush.Color == Colors.White,
            "待办气泡背景应为白色");
        AssertClose(window.Width, 437, "待办气泡展开宽度");
        AssertClose(window.Height, 240, "待办气泡展开高度");
        AssertClose(window.Left + window.Width, initialRight, "待办气泡应向左展开");
        AssertClose(window.Top + window.Height, initialBottom, "待办气泡展开时底部应固定");
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
            bubbleColumn,
            ColumnDefinition.WidthProperty,
            () => Invoke(window, "PetHost_MouseRightButtonUp", petHost, rightClick),
            () => bubbleColumn.Width.IsAbsolute && Math.Abs(bubbleColumn.Width.Value) < 0.01,
            () =>
            {
                AssertBubbleBounds(window, 145, 185, initialRight, initialBottom,
                    "待办气泡收回时窗口应先完成定位");
                AssertClose(gapColumn.Width.Value, 0, "待办气泡收回时间隔列宽");
                Assert(bubbleHost.Visibility == Visibility.Collapsed,
                    "待办气泡收回前气泡容器应已隐藏");
                Assert(bubbleTailHost.Visibility == Visibility.Collapsed,
                    "待办气泡收回前气泡尾巴应已隐藏");
            },
            "待办气泡应在窗口完成收回后再把宠物移回第一列");
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
        AssertClose(window.Width, 437, "待办打开时人物动画不应改变气泡宽度");
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
        AssertClose(window.Width, 437, "人物动画结束后待办窗口宽度应保持不变");
        AssertClose(window.Height, 240, "人物动画结束后待办窗口高度应保持不变");
        AssertClose(bubbleColumn.Width.Value, 280, "人物动画结束后待办气泡列宽应保持不变");
        AssertClose(gapColumn.Width.Value, 12, "人物动画结束后待办间隔列宽应保持不变");

        Console.WriteLine("UI state checks passed.");
    }

    private static T GetField<T>(object instance, string name) where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"找不到字段 {name}");
        return field.GetValue(instance) as T
            ?? throw new InvalidOperationException($"字段 {name} 类型不正确");
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
