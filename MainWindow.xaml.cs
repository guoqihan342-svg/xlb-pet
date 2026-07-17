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
    private static readonly TimeSpan ActionHoldDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BridgeHoldDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);
    private static readonly Duration TargetRevealDuration = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration PreviousFrameRetireDuration = new(TimeSpan.FromMilliseconds(80));

    private readonly BitmapImage _idleImage;
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip?[] _automaticSequence;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _automaticTimer;
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
    private int _fadeGeneration;
    private int _pillowAnimationGeneration;
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
        PetImage.Source = _idleImage;

        TodoItemsControl.ItemsSource = _todos;
        foreach (var item in _todoStore.Load())
        {
            _todos.Add(item);
        }

        _frameTimer = new DispatcherTimer();
        _frameTimer.Tick += FrameTimer_Tick;

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
        _fadeGeneration++;
        _pillowAnimationGeneration++;
        _frameTimer.Stop();
        _frameTimer.Tick -= FrameTimer_Tick;
        _automaticTimer.Stop();
        _automaticTimer.Tick -= AutomaticTimer_Tick;
        _activeClip = null;
        _activeFrameIndex = -1;
        PetImage.BeginAnimation(OpacityProperty, null);
        PetImageOverlay.BeginAnimation(OpacityProperty, null);
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

        CrossFadeTo(_idleImage, () =>
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
        CrossFadeTo(frame.Image, () =>
        {
            if (_isClosing || !ReferenceEquals(_activeClip, clip) || _activeFrameIndex != frameIndex)
            {
                return;
            }

            _frameTimer.Interval = frame.HoldDuration;
            _frameTimer.Start();
        });
    }

    private void CrossFadeTo(BitmapImage target, Action? completed = null)
    {
        if (_isClosing)
        {
            return;
        }

        if (ReferenceEquals(PetImage.Source, target) && PetImageOverlay.Source is null)
        {
            PetImage.BeginAnimation(OpacityProperty, null);
            PetImage.Opacity = 1;
            PetImageOverlay.BeginAnimation(OpacityProperty, null);
            PetImageOverlay.Opacity = 0;
            completed?.Invoke();
            return;
        }

        var visibleSource = PetImageOverlay.Source is not null &&
                            PetImageOverlay.Opacity > PetImage.Opacity
            ? PetImageOverlay.Source
            : PetImage.Source ?? target;

        _fadeGeneration++;
        var generation = _fadeGeneration;

        PetImage.BeginAnimation(OpacityProperty, null);
        PetImageOverlay.BeginAnimation(OpacityProperty, null);
        PetImage.Source = visibleSource;
        PetImage.Opacity = 1;
        PetImageOverlay.Source = target;
        PetImageOverlay.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var revealTarget = new DoubleAnimation(0, 1, TargetRevealDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

        revealTarget.Completed += (_, _) =>
        {
            if (_isClosing || generation != _fadeGeneration)
            {
                return;
            }

            // The previous frame stays fully opaque while the target is revealed,
            // so transparent PNG pixels never produce the 25% brightness dip of
            // two complementary opacity animations. Retire only the old-only
            // silhouette after the target is fully visible.
            PetImageOverlay.Opacity = 1;
            PetImageOverlay.BeginAnimation(OpacityProperty, null);

            var retirePrevious = new DoubleAnimation(1, 0, PreviousFrameRetireDuration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            };
            retirePrevious.Completed += (_, _) =>
            {
                if (_isClosing || generation != _fadeGeneration)
                {
                    return;
                }

                // Commit the target beneath the still-visible overlay first.
                // At no point are both layers transparent, avoiding a one-frame
                // blink in transparent WPF windows.
                PetImage.Source = target;
                PetImage.Opacity = 1;
                PetImage.BeginAnimation(OpacityProperty, null);
                PetImageOverlay.Opacity = 0;
                PetImageOverlay.Source = null;
                completed?.Invoke();
            };

            PetImage.BeginAnimation(
                OpacityProperty,
                retirePrevious,
                HandoffBehavior.SnapshotAndReplace);
        };

        PetImageOverlay.BeginAnimation(
            OpacityProperty,
            revealTarget,
            HandoffBehavior.SnapshotAndReplace);
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
}
