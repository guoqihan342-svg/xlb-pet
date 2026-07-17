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
    private const double BubbleGap = 12;
    private const double CuteBubbleWidth = 215;
    private const double TodoBubbleWidth = 280;
    private const double TodoWindowHeight = 240;
    private const double ScreenEdgeMargin = 12;
    private static readonly Duration CrossFadeDuration = new(TimeSpan.FromMilliseconds(90));

    private readonly BitmapImage _idleImage;
    private readonly AnimationClip[] _reactionClips;
    private readonly DispatcherTimer _frameTimer;
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly TodoStore _todoStore = TodoStore.CreateDefault();

    private BubbleMode _bubbleMode;
    private Point _pointerDownScreen;
    private bool _pointerDown;
    private bool _dragStarted;
    private AnimationClip? _activeClip;
    private int _activeFrameIndex = -1;
    private int _nextClipIndex;
    private int _fadeGeneration;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _idleImage = LoadResourceImage("Assets/luban-idle.png");
        _reactionClips =
        [
            CreateClip("刚睡醒，让我伸个懒腰～", "Assets/luban-yawn.png", 420,
                "Assets/luban-idle-to-yawn.png", "Assets/luban-idle-to-yawn-2.png"),
            CreateClip("呜……主人要哄哄我", "Assets/luban-cry.png", 380,
                "Assets/luban-idle-to-cry.png", "Assets/luban-idle-to-cry-2.png"),
            CreateClip("小鲁班出发！", "Assets/luban-run.png", 280,
                "Assets/luban-idle-to-run.png", "Assets/luban-idle-to-run-2.png"),
            CreateClip("给你卖个萌 ♡", "Assets/luban-cute.png", 350,
                "Assets/luban-idle-to-cute-1.png", "Assets/luban-idle-to-cute.png"),
            CreateClip("主人真棒！", "Assets/luban-like.png", 340,
                "Assets/luban-idle-to-like.png", "Assets/luban-idle-to-like-2.png"),
            CreateClip("吃块饼干，补充能量！", "Assets/luban-eat.png", 420,
                "Assets/luban-idle-to-eat.png"),
            CreateClip("嗨～我在这里！", "Assets/luban-wave.png", 350,
                "Assets/luban-idle-to-wave.png", "Assets/luban-idle-to-wave-2.png"),
            CreateClip("让我认真想一想……", "Assets/luban-think.png", 420,
                "Assets/luban-think-to-idle.png")
        ];
        PetImage.Source = _idleImage;

        TodoItemsControl.ItemsSource = _todos;
        foreach (var item in _todoStore.Load())
        {
            _todos.Add(item);
        }

        _frameTimer = new DispatcherTimer();
        _frameTimer.Tick += FrameTimer_Tick;
    }

    private static AnimationClip CreateClip(
        string message,
        string actionPath,
        int actionHoldMilliseconds,
        params string[] bridgePaths)
    {
        var bridgeImages = Array.ConvertAll(bridgePaths, LoadResourceImage);
        var frames = new AnimationFrame[bridgeImages.Length * 2 + 1];
        var bridgeHold = TimeSpan.FromMilliseconds(30);

        for (var index = 0; index < bridgeImages.Length; index++)
        {
            frames[index] = new AnimationFrame(bridgeImages[index], bridgeHold);
            frames[frames.Length - index - 1] = new AnimationFrame(bridgeImages[index], bridgeHold);
        }

        frames[bridgeImages.Length] = new AnimationFrame(
            LoadResourceImage(actionPath),
            TimeSpan.FromMilliseconds(actionHoldMilliseconds));
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
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
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
        _fadeGeneration++;
        _frameTimer.Stop();
        _frameTimer.Tick -= FrameTimer_Tick;
        _activeClip = null;
        _activeFrameIndex = -1;
        PetImage.BeginAnimation(OpacityProperty, null);
        PetImageOverlay.BeginAnimation(OpacityProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
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
        if (_isClosing || _activeClip is not null)
        {
            return;
        }

        var clip = _reactionClips[_nextClipIndex];
        _nextClipIndex = (_nextClipIndex + 1) % _reactionClips.Length;
        _activeClip = clip;
        _activeFrameIndex = -1;
        CuteMessageText.Text = clip.Message;

        if (_bubbleMode != BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.Cute);
        }

        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateBounceAnimation());
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateBounceAnimation());

        _frameTimer.Stop();
        ShowActiveClipFrame(0);
    }

    private static DoubleAnimationUsingKeyFrames CreateBounceAnimation()
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(420)
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.03, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.99, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
        return animation;
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

        var visibleSource = PetImageOverlay.Source is not null &&
                            PetImageOverlay.Opacity > PetImage.Opacity
            ? PetImageOverlay.Source
            : PetImage.Source;

        _fadeGeneration++;
        var generation = _fadeGeneration;

        PetImage.BeginAnimation(OpacityProperty, null);
        PetImageOverlay.BeginAnimation(OpacityProperty, null);
        PetImage.Source = visibleSource;
        PetImage.Opacity = 1;
        PetImageOverlay.Source = target;
        PetImageOverlay.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var fadeOut = new DoubleAnimation(1, 0, CrossFadeDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var fadeIn = new DoubleAnimation(0, 1, CrossFadeDuration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

        fadeIn.Completed += (_, _) =>
        {
            if (_isClosing || generation != _fadeGeneration)
            {
                return;
            }

            PetImage.Opacity = 0;
            PetImageOverlay.Opacity = 1;
            PetImage.BeginAnimation(OpacityProperty, null);
            PetImageOverlay.BeginAnimation(OpacityProperty, null);
            PetImage.Source = target;
            PetImage.Opacity = 1;
            PetImageOverlay.Source = null;
            PetImageOverlay.Opacity = 0;
            completed?.Invoke();
        };

        PetImage.BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
        PetImageOverlay.BeginAnimation(OpacityProperty, fadeIn, HandoffBehavior.SnapshotAndReplace);

    }

    private void SetBubbleMode(BubbleMode mode)
    {
        if (_bubbleMode == mode)
        {
            return;
        }

        // Width and Height are explicit for every bubble mode. ActualWidth and
        // ActualHeight may still describe the previous layout during a rapid
        // toggle, which would move the pet away from its screen anchor.
        var anchoredRight = Left + Width;
        var anchoredBottom = Top + Height;

        double bubbleWidth;
        double targetWidth;
        double targetHeight;

        switch (mode)
        {
            case BubbleMode.Cute:
                bubbleWidth = CuteBubbleWidth;
                targetWidth = CuteBubbleWidth + BubbleGap + PetWidth;
                targetHeight = PetHeight;
                break;
            case BubbleMode.Todo:
                bubbleWidth = TodoBubbleWidth;
                targetWidth = TodoBubbleWidth + BubbleGap + PetWidth;
                targetHeight = TodoWindowHeight;
                break;
            default:
                bubbleWidth = 0;
                targetWidth = PetWidth;
                targetHeight = PetHeight;
                break;
        }

        var workArea = SystemParameters.WorkArea;
        var targetLeft = Math.Max(workArea.Left + ScreenEdgeMargin, anchoredRight - targetWidth);
        var targetTop = Math.Max(workArea.Top + ScreenEdgeMargin, anchoredBottom - targetHeight);

        // A transparent WPF window can be composited between individual native
        // size and position changes. Keep the bubble hidden during that phase
        // and use the narrow window as a clip so the pet can never be rendered
        // at the bubble's former screen position.
        HideBubbleVisuals();

        if (_bubbleMode != BubbleMode.None)
        {
            // The current non-zero bubble columns keep PetHost outside this
            // temporary narrow viewport while the window is repositioned.
            Width = PetWidth;
            Height = PetHeight;
        }

        if (mode == BubbleMode.None)
        {
            // Keep the old columns until the narrow window reaches its final
            // location. Only then move PetHost back to column zero.
            Left = targetLeft;
            Top = targetTop;
            GapColumn.Width = new GridLength(0);
            BubbleColumn.Width = new GridLength(0);
        }
        else
        {
            // Move PetHost into the target right-hand column while it is still
            // clipped by the narrow viewport, then reveal it by expanding.
            BubbleColumn.Width = new GridLength(bubbleWidth);
            GapColumn.Width = new GridLength(BubbleGap);
            Left = targetLeft;
            Top = targetTop;
            Width = targetWidth;
            Height = targetHeight;
        }

        ShowBubbleVisuals(mode);
        _bubbleMode = mode;
    }

    private void HideBubbleVisuals()
    {
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

        BubbleHost.Visibility = Visibility.Visible;
        BubbleTailHost.Visibility = Visibility.Visible;
        CuteBubble.Visibility = mode == BubbleMode.Cute ? Visibility.Visible : Visibility.Collapsed;
        TodoBubble.Visibility = mode == BubbleMode.Todo ? Visibility.Visible : Visibility.Collapsed;
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
