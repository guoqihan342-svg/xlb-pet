using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LubanDesktopPet;

public partial class MainWindow : Window
{
    private const double PetWidth = 145;
    private const double PetHeight = 185;
    private const double CuteBubbleHeight = 76;
    private const double TodoBubbleHeight = 232;
    private const double ScreenEdgeMargin = 12;
    private const int SpriteDecodePixelWidth = 360;
    private const int TransitionFrameCount = 6;
    private static readonly TimeSpan ActionHoldDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan BridgeHoldDuration = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan TransitionFrameInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);

    private readonly BitmapImage _idleImage;
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip?[] _automaticSequence;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _transitionTimer;
    private readonly DispatcherTimer _automaticTimer;
    private readonly Dictionary<BitmapImage, BitmapSource> _displayImages = new();
    private readonly Dictionary<(BitmapImage Source, BitmapImage Target), BitmapSource[]>
        _transitionFrames = new();
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly TodoStore _todoStore = TodoStore.CreateDefault();

    private BubbleMode _bubbleMode;
    private Point _pointerDownScreen;
    private bool _pointerDown;
    private bool _dragStarted;
    private AnimationClip? _activeClip;
    private int _activeFrameIndex = -1;
    private int _nextClipIndex;
    private int _nextAutomaticSequenceIndex;
    private int _transitionGeneration;
    private int _pillowAnimationGeneration;
    private FrameTransition? _activeTransition;
    private BitmapImage _currentImage;
    private bool _automaticAnimationEnabled;
    private bool _isPillowBreathing;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _idleImage = LoadResourceImage("Assets/luban-idle.png");
        _reactionClips =
        [
            CreateClip("刚睡醒，让我伸个懒腰～", "Assets/luban-yawn.png",
                "Assets/luban-idle-to-yawn.png", "Assets/luban-idle-to-yawn-2.png"),
            CreateClip("呜……主人要哄哄我", "Assets/luban-cry.png",
                "Assets/luban-idle-to-cry.png", "Assets/luban-idle-to-cry-2.png"),
            CreateClip("小鲁班出发！", "Assets/luban-run.png",
                "Assets/luban-idle-to-run.png", "Assets/luban-idle-to-run-2.png"),
            CreateClip("给你卖个萌 ♡", "Assets/luban-cute.png",
                "Assets/luban-idle-to-cute-1.png", "Assets/luban-idle-to-cute.png"),
            CreateClip("主人真棒！", "Assets/luban-like.png",
                "Assets/luban-idle-to-like.png", "Assets/luban-idle-to-like-2.png"),
            CreateClip("吃块饼干，补充能量！", "Assets/luban-eat.png",
                "Assets/luban-idle-to-eat.png"),
            CreateClip("嗨～我在这里！", "Assets/luban-wave.png",
                "Assets/luban-idle-to-wave.png", "Assets/luban-idle-to-wave-2.png"),
            CreateClip("让我认真想一想……", "Assets/luban-think.png",
                "Assets/luban-think-to-idle.png")
        ];
        _automaticSequence =
        [
            _reactionClips[0],
            null,
            _reactionClips[1],
            _reactionClips[2],
            _reactionClips[3],
            _reactionClips[4],
            _reactionClips[5],
            _reactionClips[6],
            _reactionClips[7]
        ];
        _currentImage = _idleImage;
        _ = GetDisplayImage(_idleImage);
        foreach (var clip in _reactionClips)
        {
            foreach (var frame in clip.Frames)
            {
                _ = GetDisplayImage(frame.Image);
            }
        }

        var thinkFrames = _reactionClips[^1].Frames;
        CacheTransitionPair(_idleImage, thinkFrames[0].Image);
        CacheTransitionPair(thinkFrames[0].Image, thinkFrames[1].Image);

        PetImage.Source = GetDisplayImage(_idleImage);

        TodoItemsControl.ItemsSource = _todos;
        foreach (var item in _todoStore.Load())
        {
            _todos.Add(item);
        }

        _frameTimer = new DispatcherTimer();
        _frameTimer.Tick += FrameTimer_Tick;

        _transitionTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TransitionFrameInterval
        };
        _transitionTimer.Tick += TransitionTimer_Tick;

        _automaticTimer = new DispatcherTimer
        {
            Interval = AutomaticAnimationInterval
        };
        _automaticTimer.Tick += AutomaticTimer_Tick;
    }

    private static AnimationClip CreateClip(
        string message,
        string actionPath,
        params string[] bridgePaths)
    {
        var bridgeImages = Array.ConvertAll(bridgePaths, LoadResourceImage);
        var frames = new AnimationFrame[bridgeImages.Length * 2 + 1];
        for (var index = 0; index < bridgeImages.Length; index++)
        {
            frames[index] = new AnimationFrame(bridgeImages[index], BridgeHoldDuration);
            frames[frames.Length - index - 1] = new AnimationFrame(bridgeImages[index], BridgeHoldDuration);
        }

        frames[bridgeImages.Length] = new AnimationFrame(
            LoadResourceImage(actionPath),
            ActionHoldDuration);
        return new AnimationClip(message, frames);
    }

    private static BitmapImage LoadResourceImage(string resourcePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = SpriteDecodePixelWidth;
        image.UriSource = new Uri(
            $"pack://application:,,,/LubanDesktopPet;component/{resourcePath}",
            UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - ScreenEdgeMargin);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - ScreenEdgeMargin);
        _automaticAnimationEnabled = true;
        RestartAutomaticCountdown();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        RestartAutomaticCountdown();

        if (_bubbleMode != BubbleMode.Todo)
        {
            return;
        }

        var eventSource = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject;
        if (IsWithin(eventSource, TodoBubble))
        {
            return;
        }

        // PetHost owns the right-click toggle. Closing here would make its MouseUp
        // handler immediately reopen the todo bubble instead of toggling it closed.
        if (e.ChangedButton == MouseButton.Right && IsWithin(eventSource, PetHost))
        {
            return;
        }

        SetBubbleMode(BubbleMode.None);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        RestartAutomaticCountdown();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_isClosing && _bubbleMode == BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.None);
        }
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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _automaticAnimationEnabled = false;
        _transitionGeneration++;
        _pillowAnimationGeneration++;
        _frameTimer.Stop();
        _frameTimer.Tick -= FrameTimer_Tick;
        _transitionTimer.Stop();
        _transitionTimer.Tick -= TransitionTimer_Tick;
        _automaticTimer.Stop();
        _automaticTimer.Tick -= AutomaticTimer_Tick;
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeTransition = null;
        PetImage.BeginAnimation(OpacityProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HideBubbleVisuals();
        SaveTodos();
    }

    private void PetHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _pointerDown = true;
        _dragStarted = false;
        _pointerDownScreen = PointToScreen(e.GetPosition(this));
        PetHost.CaptureMouse();
        e.Handled = true;
    }

    private void PetHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentScreen = PointToScreen(e.GetPosition(this));
        var movedFarEnough =
            Math.Abs(currentScreen.X - _pointerDownScreen.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentScreen.Y - _pointerDownScreen.Y) >= SystemParameters.MinimumVerticalDragDistance;

        if (!movedFarEnough)
        {
            return;
        }

        _dragStarted = true;
        _pointerDown = false;
        PetHost.ReleaseMouseCapture();

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在系统接管拖动前已经松开时，无需处理。
        }

        e.Handled = true;
    }

    private void PetHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var shouldActCute = _pointerDown && !_dragStarted;
        _pointerDown = false;
        PetHost.ReleaseMouseCapture();

        if (shouldActCute)
        {
            ShowCuteReaction();
        }

        e.Handled = true;
    }

    private void PetHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            _pointerDown = false;
            _dragStarted = false;
        }
    }

    private void PetHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        SetBubbleMode(_bubbleMode == BubbleMode.Todo ? BubbleMode.None : BubbleMode.Todo);

        if (_bubbleMode == BubbleMode.Todo)
        {
            TodoInput.Focus();
        }

        e.Handled = true;
    }

    private void ShowCuteReaction()
    {
        RestartAutomaticCountdown();

        if (_isClosing || _activeClip is not null)
        {
            return;
        }

        var clip = _reactionClips[_nextClipIndex];
        if (!TryStartReaction(clip, showCuteBubble: true))
        {
            return;
        }

        _nextClipIndex = (_nextClipIndex + 1) % _reactionClips.Length;
    }

    private bool TryStartReaction(AnimationClip clip, bool showCuteBubble)
    {
        if (_isClosing || _activeClip is not null)
        {
            return false;
        }

        StopPillowBreathing();
        _activeClip = clip;
        _activeFrameIndex = -1;
        CuteMessageText.Text = clip.Message;

        if (showCuteBubble && _bubbleMode != BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.Cute);
        }

        _frameTimer.Stop();
        ShowActiveClipFrame(0);
        return true;
    }

    private void AutomaticTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || !_automaticAnimationEnabled || _activeClip is not null ||
            _isPillowBreathing ||
            _bubbleMode == BubbleMode.Todo)
        {
            return;
        }

        var sequenceItem = _automaticSequence[_nextAutomaticSequenceIndex];
        if (sequenceItem is null)
        {
            StartPillowBreathing();
        }
        else if (!TryStartReaction(sequenceItem, showCuteBubble: false))
        {
            return;
        }

        _nextAutomaticSequenceIndex =
            (_nextAutomaticSequenceIndex + 1) % _automaticSequence.Length;
    }

    private void RestartAutomaticCountdown()
    {
        if (_isClosing || !_automaticAnimationEnabled)
        {
            return;
        }

        _automaticTimer.Stop();
        _automaticTimer.Interval = AutomaticAnimationInterval;
        _automaticTimer.Start();
    }

    private void StartPillowBreathing()
    {
        StopPillowBreathing();
        _isPillowBreathing = true;
        var generation = _pillowAnimationGeneration;
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

        var scaleX = CreatePillowBreathingAnimation(1.012, easing);
        var scaleY = CreatePillowBreathingAnimation(0.988, easing);
        scaleY.Completed += (_, _) =>
        {
            if (_isClosing || generation != _pillowAnimationGeneration)
            {
                return;
            }

            _isPillowBreathing = false;
            PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PetScale.ScaleX = 1;
            PetScale.ScaleY = 1;
        };

        PetScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            scaleX,
            HandoffBehavior.SnapshotAndReplace);
        PetScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            scaleY,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimationUsingKeyFrames CreatePillowBreathingAnimation(
        double middleValue,
        IEasingFunction easing)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(PillowAnimationDuration),
            FillBehavior = FillBehavior.Stop
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(TimeSpan.Zero),
            easing));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            middleValue,
            KeyTime.FromTimeSpan(TimeSpan.FromTicks(PillowAnimationDuration.Ticks / 2)),
            easing));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(PillowAnimationDuration),
            easing));
        return animation;
    }

    private void StopPillowBreathing()
    {
        _pillowAnimationGeneration++;
        _isPillowBreathing = false;
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PetScale.ScaleX = 1;
        PetScale.ScaleY = 1;
    }

    private void FrameTimer_Tick(object? sender, EventArgs e)
    {
        _frameTimer.Stop();
        var clip = _activeClip;
        if (_isClosing || clip is null)
        {
            return;
        }

        var nextFrameIndex = _activeFrameIndex + 1;
        if (nextFrameIndex < clip.Frames.Length)
        {
            ShowActiveClipFrame(nextFrameIndex);
            return;
        }

        TransitionTo(_idleImage, () =>
        {
            if (!ReferenceEquals(_activeClip, clip))
            {
                return;
            }

            _activeClip = null;
            _activeFrameIndex = -1;
            if (_bubbleMode == BubbleMode.Cute)
            {
                SetBubbleMode(BubbleMode.None);
            }
        });
    }

    private void ShowActiveClipFrame(int frameIndex)
    {
        var clip = _activeClip;
        if (_isClosing || clip is null)
        {
            return;
        }

        _activeFrameIndex = frameIndex;
        var frame = clip.Frames[frameIndex];
        TransitionTo(frame.Image, () =>
        {
            if (_isClosing || !ReferenceEquals(_activeClip, clip) || _activeFrameIndex != frameIndex)
            {
                return;
            }

            _frameTimer.Interval = frame.HoldDuration;
            _frameTimer.Start();
        });
    }

    private void TransitionTo(BitmapImage target, Action? completed = null)
    {
        if (_isClosing)
        {
            return;
        }

        if (_activeTransition is null && ReferenceEquals(_currentImage, target))
        {
            PetImage.BeginAnimation(OpacityProperty, null);
            PetImage.Opacity = 1;
            completed?.Invoke();
            return;
        }

        _transitionTimer.Stop();
        _transitionGeneration++;
        var generation = _transitionGeneration;
        var source = _currentImage;
        var sourceDisplay = GetDisplayImage(source);
        var targetDisplay = GetDisplayImage(target);

        PetImage.BeginAnimation(OpacityProperty, null);
        PetImage.Opacity = 1;
        PetImageOverlay.Source = null;
        PetImageOverlay.Opacity = 0;

        if (!ShouldInterpolate(source, target))
        {
            _activeTransition = null;
            _currentImage = target;
            PetImage.Source = targetDisplay;
            completed?.Invoke();
            return;
        }

        if (!_transitionFrames.TryGetValue((source, target), out var intermediateFrames))
        {
            throw new InvalidOperationException("允许补间的姿势缺少预生成帧。");
        }

        _activeTransition = new FrameTransition(
            generation,
            target,
            targetDisplay,
            intermediateFrames,
            completed);
        PetImage.Source = intermediateFrames[0];
        _transitionTimer.Start();
    }

    private void TransitionTimer_Tick(object? sender, EventArgs e)
    {
        var transition = _activeTransition;
        if (_isClosing || transition is null || transition.Generation != _transitionGeneration)
        {
            _transitionTimer.Stop();
            return;
        }

        transition.FrameIndex++;
        if (transition.FrameIndex < transition.IntermediateFrames.Length)
        {
            PetImage.Source = transition.IntermediateFrames[transition.FrameIndex];
            return;
        }

        _transitionTimer.Stop();
        _currentImage = transition.Target;
        PetImage.Source = transition.TargetDisplay;
        PetImage.Opacity = 1;
        _activeTransition = null;
        transition.Completed?.Invoke();
    }

    private static bool ShouldInterpolate(BitmapImage source, BitmapImage target)
    {
        var sourceName = GetResourceFileName(source);
        var targetName = GetResourceFileName(target);
        return IsFramePair(sourceName, targetName, "luban-idle.png", "luban-think-to-idle.png") ||
               IsFramePair(sourceName, targetName, "luban-think-to-idle.png", "luban-think.png");
    }

    private static bool IsFramePair(
        string sourceName,
        string targetName,
        string firstName,
        string secondName)
    {
        return sourceName.Equals(firstName, StringComparison.OrdinalIgnoreCase) &&
               targetName.Equals(secondName, StringComparison.OrdinalIgnoreCase) ||
               sourceName.Equals(secondName, StringComparison.OrdinalIgnoreCase) &&
               targetName.Equals(firstName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetResourceFileName(BitmapImage image)
    {
        return Uri.UnescapeDataString(image.UriSource.Segments[^1]);
    }

    private BitmapSource GetDisplayImage(BitmapImage image)
    {
        if (_displayImages.TryGetValue(image, out var displayImage))
        {
            return displayImage;
        }

        displayImage = EnsurePbgra32(image);
        _displayImages.Add(image, displayImage);
        return displayImage;
    }

    private void CacheTransitionPair(BitmapImage first, BitmapImage second)
    {
        var forward = BuildInterpolatedFrames(
            GetDisplayImage(first),
            GetDisplayImage(second));
        var reverse = new BitmapSource[forward.Length];
        for (var index = 0; index < forward.Length; index++)
        {
            reverse[index] = forward[forward.Length - index - 1];
        }

        _transitionFrames.Add((first, second), forward);
        _transitionFrames.Add((second, first), reverse);
    }

    private static BitmapSource[] BuildInterpolatedFrames(
        BitmapSource source,
        BitmapSource target)
    {
        var sourcePbgra = EnsurePbgra32(source);
        var targetPbgra = EnsurePbgra32(target);
        if (sourcePbgra.PixelWidth != targetPbgra.PixelWidth ||
            sourcePbgra.PixelHeight != targetPbgra.PixelHeight)
        {
            throw new InvalidOperationException("动画图片尺寸必须完全一致。");
        }

        var width = sourcePbgra.PixelWidth;
        var height = sourcePbgra.PixelHeight;
        var stride = width * 4;
        var bufferLength = stride * height;
        var sourcePixels = new byte[bufferLength];
        var targetPixels = new byte[bufferLength];
        sourcePbgra.CopyPixels(sourcePixels, stride, 0);
        targetPbgra.CopyPixels(targetPixels, stride, 0);

        var frames = new BitmapSource[TransitionFrameCount];
        const int denominator = TransitionFrameCount + 1;
        var dpiX = targetPbgra.DpiX > 0 ? targetPbgra.DpiX : 96;
        var dpiY = targetPbgra.DpiY > 0 ? targetPbgra.DpiY : 96;

        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var targetWeight = frameIndex + 1;
            var sourceWeight = denominator - targetWeight;
            var pixels = new byte[bufferLength];
            for (var byteIndex = 0; byteIndex < pixels.Length; byteIndex++)
            {
                pixels[byteIndex] = (byte)(
                    (sourcePixels[byteIndex] * sourceWeight +
                     targetPixels[byteIndex] * targetWeight +
                     denominator / 2) /
                    denominator);
            }

            var frame = BitmapSource.Create(
                width,
                height,
                dpiX,
                dpiY,
                PixelFormats.Pbgra32,
                null,
                pixels,
                stride);
            frame.Freeze();
            frames[frameIndex] = frame;
        }

        return frames;
    }

    private static BitmapSource EnsurePbgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Pbgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(
            source,
            PixelFormats.Pbgra32,
            null,
            0);
        converted.Freeze();
        return converted;
    }

    private void SetBubbleMode(BubbleMode mode)
    {
        if (_bubbleMode == mode)
        {
            return;
        }

        HideBubbleVisuals();
        ShowBubbleVisuals(mode);
        _bubbleMode = mode;
    }

    private void HideBubbleVisuals()
    {
        BubblePopup.IsOpen = false;
        BubbleHost.Visibility = Visibility.Collapsed;
        BubbleTailHost.Visibility = Visibility.Collapsed;
        CuteBubble.Visibility = Visibility.Collapsed;
        TodoBubble.Visibility = Visibility.Collapsed;
    }

    private void ShowBubbleVisuals(BubbleMode mode)
    {
        if (mode == BubbleMode.None)
        {
            return;
        }

        BubblePopup.VerticalOffset = mode == BubbleMode.Cute
            ? PetHeight - CuteBubbleHeight
            : PetHeight - TodoBubbleHeight;
        BubbleHost.Visibility = Visibility.Visible;
        BubbleTailHost.Visibility = Visibility.Visible;
        CuteBubble.Visibility = mode == BubbleMode.Cute ? Visibility.Visible : Visibility.Collapsed;
        TodoBubble.Visibility = mode == BubbleMode.Todo ? Visibility.Visible : Visibility.Collapsed;
        BubblePopup.IsOpen = true;
    }

    private void CloseTodoButton_Click(object sender, RoutedEventArgs e)
    {
        SetBubbleMode(BubbleMode.None);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        SaveTodos();
        Application.Current.Shutdown();
    }

    private void AddTodoButton_Click(object sender, RoutedEventArgs e)
    {
        AddTodoFromInput();
    }

    private void TodoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTodoFromInput();
            e.Handled = true;
        }
    }

    private void AddTodoFromInput()
    {
        var text = TodoInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _todos.Add(new TodoItem { Text = text });
        TodoInput.Clear();
        SaveTodos();
    }

    private void TodoCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TodoItem item } checkBox)
        {
            item.IsCompleted = checkBox.IsChecked == true;
            SaveTodos();
        }
    }

    private void DeleteTodoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TodoItem item })
        {
            _todos.Remove(item);
            SaveTodos();
        }
    }

    private void SaveTodos()
    {
        _todoStore.Save(_todos);
    }

    private enum BubbleMode
    {
        None,
        Cute,
        Todo
    }

    private sealed record AnimationClip(string Message, AnimationFrame[] Frames);

    private sealed record AnimationFrame(BitmapImage Image, TimeSpan HoldDuration);

    private sealed class FrameTransition(
        int generation,
        BitmapImage target,
        BitmapSource targetDisplay,
        BitmapSource[] intermediateFrames,
        Action? completed)
    {
        public int Generation { get; } = generation;

        public BitmapImage Target { get; } = target;

        public BitmapSource TargetDisplay { get; } = targetDisplay;

        public BitmapSource[] IntermediateFrames { get; } = intermediateFrames;

        public Action? Completed { get; } = completed;

        public int FrameIndex { get; set; }
    }
}
