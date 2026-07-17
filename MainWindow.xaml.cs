using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private const double TodoBubbleHeight = 270;
    private const double ScreenEdgeMargin = 12;
    private const int SpriteDecodePixelWidth = 240;
    private const int WakeFrameCount = 12;
    private const int ActionPoseFrameCount = 24;
    private const int ActionLoopStartPoseNumber = 21;
    private const int ActionLoopPoseCount = 4;
    private const int ActionLoopCycleCount = 10;
    private const int EdgePeekFrameCount = 4;
    private const int RoamFrameCount = 4;
    private const double EdgeDockThreshold = 32;
    private static readonly TimeSpan MotionFrameInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ActionLoopFrameInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan EdgePeekFrameInterval = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan RoamRenderInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RoamSpriteFrameInterval = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan RoamCornerTurnDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan RoamBoundaryDuration = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);

    private readonly BitmapImage _idleImage;
    private readonly BitmapSource[] _wakeFrames;
    private readonly BitmapSource[] _edgeLeftFrames;
    private readonly BitmapSource[] _edgeTopFrames;
    private readonly BitmapSource[] _edgeBottomFrames;
    private readonly BitmapSource[][] _roamHorizontalFrames;
    private readonly BitmapSource[][] _roamVerticalFrames;
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip?[] _automaticSequence;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _edgePeekTimer;
    private readonly DispatcherTimer _roamTimer;
    private readonly DispatcherTimer _automaticTimer;
    private readonly Stopwatch _roamStopwatch = new();
    private readonly Dictionary<BitmapImage, BitmapSource> _displayImages = new();
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly TodoStore _todoStore = TodoStore.CreateDefault();
    private readonly AppSettingsStore _settingsStore = AppSettingsStore.CreateDefault();

    private BubbleMode _bubbleMode;
    private Point _pointerDownPosition;
    private bool _pointerDown;
    private bool _dragStarted;
    private bool _dragInteractionActive;
    private AnimationClip? _activeClip;
    private int _activeFrameIndex = -1;
    private int _nextClipIndex;
    private int _nextAutomaticSequenceIndex;
    private int _pillowAnimationGeneration;
    private int _edgePeekFrameIndex;
    private int _automaticActivityCount;
    private int _nextRoamModeIndex;
    private EdgeDock _edgeDock;
    private EdgeDock _roamEdge;
    private EdgeDock _roamVisualEdge;
    private EdgeDock _roamCornerTargetEdge;
    private RoamMode _roamMode;
    private Rect _roamWorkArea;
    private Point _roamApproachTarget;
    private TimeSpan _roamLastElapsed;
    private TimeSpan _roamBoundaryElapsed;
    private TimeSpan _roamCornerTurnElapsed;
    private bool _roamClockwise;
    private bool _roamApproaching;
    private bool _isRoamCornerTurning;
    private bool _isEdgeRoaming;
    private bool _edgeRoamingEnabled;
    private bool _settingsReady;
    private bool _automaticAnimationEnabled;
    private bool _isPillowBreathing;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _idleImage = LoadResourceImage("Assets/luban-idle.png");
        _wakeFrames = Enumerable.Range(1, WakeFrameCount)
            .Select(frameNumber => GetDisplayImage(LoadResourceImage(
                $"Assets/luban-wake-{frameNumber:00}.png")))
            .ToArray();
        _edgeLeftFrames = LoadFrameSequence("luban-edge-left", EdgePeekFrameCount);
        _edgeTopFrames = LoadFrameSequence("luban-edge-top", EdgePeekFrameCount);
        _edgeBottomFrames = LoadFrameSequence("luban-edge-bottom", EdgePeekFrameCount);
        _roamHorizontalFrames = Enum.GetValues<RoamMode>()
            .Select(mode => LoadFrameSequence(
                $"luban-roam-{GetRoamAssetName(mode)}-horizontal",
                RoamFrameCount))
            .ToArray();
        _roamVerticalFrames = Enum.GetValues<RoamMode>()
            .Select(mode => LoadFrameSequence(
                $"luban-roam-{GetRoamAssetName(mode)}-vertical",
                RoamFrameCount))
            .ToArray();
        _reactionClips =
        [
            CreateMotionClip("刚睡醒，让我伸个懒腰～", "yawn"),
            CreateMotionClip("呜……主人要哄哄我", "cry"),
            CreateMotionClip("小鲁班出发！", "run"),
            CreateMotionClip("给你卖个萌 ♡", "cute"),
            CreateMotionClip("主人真棒！", "like"),
            CreateMotionClip("吃块饼干，补充能量！", "eat"),
            CreateMotionClip("嗨～我在这里！", "wave"),
            CreateMotionClip("让我认真想一想……", "think")
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
        PetImage.Source = GetDisplayImage(_idleImage);

        TodoItemsControl.ItemsSource = _todos;
        foreach (var item in _todoStore.Load())
        {
            _todos.Add(item);
        }

        _edgeRoamingEnabled = _settingsStore.Load().EdgeRoamingEnabled;
        AutoRoamToggle.IsChecked = _edgeRoamingEnabled;
        _settingsReady = true;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render);
        _frameTimer.Tick += FrameTimer_Tick;

        _edgePeekTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = EdgePeekFrameInterval
        };
        _edgePeekTimer.Tick += EdgePeekTimer_Tick;

        _roamTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = RoamRenderInterval
        };
        _roamTimer.Tick += RoamTimer_Tick;

        _automaticTimer = new DispatcherTimer
        {
            Interval = AutomaticAnimationInterval
        };
        _automaticTimer.Tick += AutomaticTimer_Tick;
        AppLogger.Info(
            $"主窗口初始化完成，已加载 {_reactionClips.Length} 组动作补帧，" +
            $"自动绕屏：{_edgeRoamingEnabled}");
    }

    private BitmapSource[] LoadFrameSequence(string resourcePrefix, int frameCount)
    {
        return Enumerable.Range(1, frameCount)
            .Select(frameNumber => GetDisplayImage(LoadResourceImage(
                $"Assets/{resourcePrefix}-{frameNumber:00}.png")))
            .ToArray();
    }

    private AnimationClip CreateMotionClip(string message, string actionName)
    {
        var timeline = new BitmapSource[WakeFrameCount + ActionPoseFrameCount + 1];
        var names = new string[timeline.Length];
        timeline[0] = GetDisplayImage(_idleImage);
        names[0] = "luban-idle.png";

        for (var wakeFrameNumber = 1;
             wakeFrameNumber <= WakeFrameCount;
             wakeFrameNumber++)
        {
            var timelineIndex = wakeFrameNumber;
            timeline[timelineIndex] = _wakeFrames[wakeFrameNumber - 1];
            names[timelineIndex] = $"luban-wake-{wakeFrameNumber:00}.png";
        }

        for (var actionFrameNumber = 1;
             actionFrameNumber <= ActionPoseFrameCount;
             actionFrameNumber++)
        {
            var frameName = $"luban-{actionName}-frame-{actionFrameNumber:00}.png";
            var timelineIndex = WakeFrameCount + actionFrameNumber;
            timeline[timelineIndex] = GetDisplayImage(
                LoadResourceImage($"Assets/{frameName}"));
            names[timelineIndex] = frameName;
        }

        var frames = new List<AnimationFrame>(
            (timeline.Length - 1) * 2 +
            ActionLoopPoseCount * ActionLoopCycleCount);
        for (var timelineIndex = 1;
             timelineIndex < timeline.Length;
             timelineIndex++)
        {
            frames.Add(new AnimationFrame(
                timeline[timelineIndex],
                MotionFrameInterval,
                names[timelineIndex]));
        }

        var actionFrameIndex = frames.Count - 1;
        for (var cycle = 0; cycle < ActionLoopCycleCount; cycle++)
        {
            for (var poseOffset = 0;
                 poseOffset < ActionLoopPoseCount;
                 poseOffset++)
            {
                var poseNumber = ActionLoopStartPoseNumber + poseOffset;
                var timelineIndex = WakeFrameCount + poseNumber;
                frames.Add(new AnimationFrame(
                    timeline[timelineIndex],
                    ActionLoopFrameInterval,
                    names[timelineIndex]));
            }
        }

        for (var timelineIndex = timeline.Length - 2;
             timelineIndex >= 0;
             timelineIndex--)
        {
            frames.Add(new AnimationFrame(
                timeline[timelineIndex],
                MotionFrameInterval,
                names[timelineIndex]));
        }

        return new AnimationClip(message, actionName, frames.ToArray(), actionFrameIndex);
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
        var workArea = MonitorWorkArea.GetForWindow(this);
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - ScreenEdgeMargin);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - ScreenEdgeMargin);
        _automaticAnimationEnabled = true;
        RestartAutomaticCountdown();
        AppLogger.Info($"主窗口已显示，位置 ({Left:F0}, {Top:F0})");
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
        AppLogger.Info("主窗口正在关闭");
        _automaticAnimationEnabled = false;
        _pillowAnimationGeneration++;
        _frameTimer.Stop();
        _frameTimer.Tick -= FrameTimer_Tick;
        _edgePeekTimer.Stop();
        _edgePeekTimer.Tick -= EdgePeekTimer_Tick;
        _roamTimer.Stop();
        _roamTimer.Tick -= RoamTimer_Tick;
        _roamStopwatch.Stop();
        _automaticTimer.Stop();
        _automaticTimer.Tick -= AutomaticTimer_Tick;
        _activeClip = null;
        _activeFrameIndex = -1;
        _edgeDock = EdgeDock.None;
        PetImage.BeginAnimation(OpacityProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.YProperty, null);
        PetRoamBaseOffset.X = 0;
        PetRoamBaseOffset.Y = 0;
        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;
        HideBubbleVisuals();
        SaveTodos();
    }

    private void PetHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        StopEdgeRoaming("用户开始拖动", restartAutomaticCountdown: false);
        _automaticTimer.Stop();
        _dragInteractionActive = true;
        _pointerDown = true;
        _dragStarted = false;
        _pointerDownPosition = e.GetPosition(this);
        PetHost.CaptureMouse();
        e.Handled = true;
    }

    private void PetHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerDown || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        var movedFarEnough =
            Math.Abs(currentPosition.X - _pointerDownPosition.X) >=
            SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentPosition.Y - _pointerDownPosition.Y) >=
            SystemParameters.MinimumVerticalDragDistance;

        if (!movedFarEnough)
        {
            return;
        }

        _dragStarted = true;
        _pointerDown = false;
        ExitEdgePeek(restartAutomaticCountdown: false);
        PetHost.ReleaseMouseCapture();

        try
        {
            DragMove();
            UpdateEdgeDockAfterDrag();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在系统接管拖动前已经松开时，无需处理。
        }
        finally
        {
            _dragInteractionActive = false;
            RestartAutomaticCountdown();
        }

        e.Handled = true;
    }

    private void PetHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var shouldActCute = _pointerDown && !_dragStarted &&
                            _edgeDock == EdgeDock.None;
        _pointerDown = false;
        _dragInteractionActive = false;
        PetHost.ReleaseMouseCapture();

        if (shouldActCute)
        {
            ShowCuteReaction();
        }
        else
        {
            RestartAutomaticCountdown();
        }

        e.Handled = true;
    }

    private void PetHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragStarted || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            _pointerDown = false;
            _dragStarted = false;
            _dragInteractionActive = false;
            RestartAutomaticCountdown();
        }
    }

    private void PetHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopEdgeRoaming("用户打开待办", restartAutomaticCountdown: false);
        SetBubbleMode(_bubbleMode == BubbleMode.Todo ? BubbleMode.None : BubbleMode.Todo);

        if (_bubbleMode == BubbleMode.Todo)
        {
            TodoInput.Focus();
        }

        e.Handled = true;
    }

    private void UpdateEdgeDockAfterDrag()
    {
        if (_isClosing)
        {
            return;
        }

        var workArea = MonitorWorkArea.GetForWindow(this);
        var distances = new (EdgeDock Dock, double Distance)[]
        {
            (EdgeDock.Left, Left <= workArea.Left ? 0 : Left - workArea.Left),
            (EdgeDock.Right, Left + ActualWidth >= workArea.Right
                ? 0
                : workArea.Right - (Left + ActualWidth)),
            (EdgeDock.Top, Top <= workArea.Top ? 0 : Top - workArea.Top),
            (EdgeDock.Bottom, Top + ActualHeight >= workArea.Bottom
                ? 0
                : workArea.Bottom - (Top + ActualHeight))
        };
        var nearest = distances.MinBy(candidate => candidate.Distance);
        if (nearest.Distance > EdgeDockThreshold)
        {
            RestartAutomaticCountdown();
            return;
        }

        switch (nearest.Dock)
        {
            case EdgeDock.Left:
                Left = workArea.Left;
                Top = Math.Clamp(Top, workArea.Top, workArea.Bottom - ActualHeight);
                break;
            case EdgeDock.Right:
                Left = workArea.Right - ActualWidth;
                Top = Math.Clamp(Top, workArea.Top, workArea.Bottom - ActualHeight);
                break;
            case EdgeDock.Top:
                Left = Math.Clamp(Left, workArea.Left, workArea.Right - ActualWidth);
                Top = workArea.Top;
                break;
            case EdgeDock.Bottom:
                Left = Math.Clamp(Left, workArea.Left, workArea.Right - ActualWidth);
                Top = workArea.Bottom - ActualHeight;
                break;
        }

        EnterEdgePeek(nearest.Dock);
    }

    private void EnterEdgePeek(EdgeDock dock)
    {
        if (_isClosing || dock == EdgeDock.None)
        {
            return;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        if (_activeClip is { } activeClip)
        {
            _frameTimer.Stop();
            _activeClip = null;
            _activeFrameIndex = -1;
            AppLogger.Info($"动作中止：{activeClip.ActionName}，原因：拖到屏幕边缘");
            if (_bubbleMode == BubbleMode.Cute)
            {
                SetBubbleMode(BubbleMode.None);
            }
        }

        _edgePeekTimer.Stop();
        _edgeDock = dock;
        _edgePeekFrameIndex = 0;
        PetFacingScale.ScaleX = dock == EdgeDock.Right ? -1 : 1;
        PetFacingScale.ScaleY = 1;
        ShowStableFrame(GetEdgeFrames(dock)[0]);
        _edgePeekTimer.Start();
        AppLogger.Info($"边缘探头开始：{GetEdgeName(dock)}");
    }

    private void ExitEdgePeek(bool restartAutomaticCountdown)
    {
        if (_edgeDock == EdgeDock.None)
        {
            return;
        }

        var previousDock = _edgeDock;
        _edgePeekTimer.Stop();
        _edgeDock = EdgeDock.None;
        _edgePeekFrameIndex = 0;
        PetFacingScale.ScaleX = 1;
        PetFacingScale.ScaleY = 1;
        ShowStableFrame(GetDisplayImage(_idleImage));
        AppLogger.Info($"边缘探头结束：{GetEdgeName(previousDock)}");
        if (restartAutomaticCountdown)
        {
            RestartAutomaticCountdown();
        }
    }

    private void EdgePeekTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || _edgeDock == EdgeDock.None)
        {
            _edgePeekTimer.Stop();
            return;
        }

        var frames = GetEdgeFrames(_edgeDock);
        _edgePeekFrameIndex = (_edgePeekFrameIndex + 1) % frames.Length;
        ShowStableFrame(frames[_edgePeekFrameIndex]);
    }

    private BitmapSource[] GetEdgeFrames(EdgeDock dock)
    {
        return dock switch
        {
            EdgeDock.Left or EdgeDock.Right => _edgeLeftFrames,
            EdgeDock.Top => _edgeTopFrames,
            EdgeDock.Bottom => _edgeBottomFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(dock), dock, null)
        };
    }

    private static string GetEdgeName(EdgeDock dock)
    {
        return dock switch
        {
            EdgeDock.Left => "左边缘",
            EdgeDock.Right => "右边缘",
            EdgeDock.Top => "上边缘",
            EdgeDock.Bottom => "下边缘",
            _ => "无"
        };
    }

    private void StartEdgeRoaming()
    {
        if (_isClosing || !_edgeRoamingEnabled || _edgeDock != EdgeDock.None ||
            _activeClip is not null || _isPillowBreathing ||
            _bubbleMode == BubbleMode.Todo)
        {
            RestartAutomaticCountdown();
            return;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        _roamWorkArea = MonitorWorkArea.GetForWindow(this);
        _roamEdge = FindNearestEdge(_roamWorkArea);
        _roamApproachTarget = GetDockedPosition(_roamEdge, _roamWorkArea);
        _roamApproaching = Math.Abs(Left - _roamApproachTarget.X) > 0.5 ||
                           Math.Abs(Top - _roamApproachTarget.Y) > 0.5;
        _roamMode = (RoamMode)(_nextRoamModeIndex % Enum.GetValues<RoamMode>().Length);
        _roamClockwise = _nextRoamModeIndex % 2 == 0;
        _nextRoamModeIndex++;
        _roamLastElapsed = TimeSpan.Zero;
        _roamBoundaryElapsed = TimeSpan.Zero;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        _roamCornerTargetEdge = EdgeDock.None;
        _isRoamCornerTurning = false;
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
        _isEdgeRoaming = true;
        _roamStopwatch.Restart();
        UpdateRoamVisual();
        _roamTimer.Start();
        AppLogger.Info(
            $"自动绕屏开始：{GetRoamModeName(_roamMode)}，" +
            $"显示器工作区 {_roamWorkArea.Left:F0},{_roamWorkArea.Top:F0}," +
            $"{_roamWorkArea.Width:F0}×{_roamWorkArea.Height:F0}");
    }

    private void StopEdgeRoaming(string reason, bool restartAutomaticCountdown)
    {
        if (!_isEdgeRoaming)
        {
            return;
        }

        _roamTimer.Stop();
        _roamStopwatch.Stop();
        _isEdgeRoaming = false;
        _roamApproaching = false;
        _roamBoundaryElapsed = TimeSpan.Zero;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        _roamCornerTargetEdge = EdgeDock.None;
        _isRoamCornerTurning = false;
        _roamVisualEdge = EdgeDock.None;
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.YProperty, null);
        PetRoamBaseOffset.X = 0;
        PetRoamBaseOffset.Y = 0;
        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
        PetFacingScale.ScaleX = 1;
        PetFacingScale.ScaleY = 1;
        if (_activeClip is null && _edgeDock == EdgeDock.None)
        {
            ShowStableFrame(GetDisplayImage(_idleImage));
        }

        AppLogger.Info($"自动绕屏结束：{reason}");
        if (restartAutomaticCountdown)
        {
            RestartAutomaticCountdown();
        }
    }

    private void RoamTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || !_isEdgeRoaming || !_edgeRoamingEnabled ||
            _edgeDock != EdgeDock.None)
        {
            StopEdgeRoaming("状态已切换", restartAutomaticCountdown: true);
            return;
        }

        var elapsed = _roamStopwatch.Elapsed;
        var delta = elapsed - _roamLastElapsed;
        _roamLastElapsed = elapsed;
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        if (delta > TimeSpan.FromMilliseconds(100))
        {
            delta = TimeSpan.FromMilliseconds(100);
        }

        var distance = GetRoamSpeed(_roamMode) * delta.TotalSeconds;
        if (_roamApproaching)
        {
            MoveTowardRoamBoundary(distance);
        }
        else
        {
            _roamBoundaryElapsed += delta;
            if (_roamBoundaryElapsed >= RoamBoundaryDuration)
            {
                StopEdgeRoaming("本轮完成", restartAutomaticCountdown: true);
                return;
            }

            if (_isRoamCornerTurning)
            {
                AdvanceRoamCornerTurn(delta);
            }
            else
            {
                AdvanceRoamAlongBoundary(distance);
            }
        }

        UpdateRoamVisual();
    }

    private void MoveTowardRoamBoundary(double distance)
    {
        var deltaX = _roamApproachTarget.X - Left;
        var deltaY = _roamApproachTarget.Y - Top;
        var remaining = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (remaining <= distance || remaining <= 0.5)
        {
            Left = _roamApproachTarget.X;
            Top = _roamApproachTarget.Y;
            _roamApproaching = false;
            return;
        }

        Left += deltaX / remaining * distance;
        Top += deltaY / remaining * distance;
    }

    private void AdvanceRoamAlongBoundary(double distance)
    {
        if (distance <= 0.001)
        {
            return;
        }

        var positiveDirection = IsPositiveRoamDirection(_roamEdge, _roamClockwise);
        var horizontal = _roamEdge is EdgeDock.Top or EdgeDock.Bottom;
        var current = horizontal ? Left : Top;
        var minimum = horizontal ? _roamWorkArea.Left : _roamWorkArea.Top;
        var maximum = horizontal
            ? _roamWorkArea.Right - ActualWidth
            : _roamWorkArea.Bottom - ActualHeight;
        var available = positiveDirection ? maximum - current : current - minimum;
        if (distance <= available)
        {
            if (horizontal)
            {
                Left += positiveDirection ? distance : -distance;
            }
            else
            {
                Top += positiveDirection ? distance : -distance;
            }

            return;
        }

        if (horizontal)
        {
            Left = positiveDirection ? maximum : minimum;
        }
        else
        {
            Top = positiveDirection ? maximum : minimum;
        }

        BeginRoamCornerTurn(GetNextRoamEdge(_roamEdge, _roamClockwise));
    }

    private void BeginRoamCornerTurn(EdgeDock targetEdge)
    {
        if (_isRoamCornerTurning || targetEdge == EdgeDock.None || targetEdge == _roamEdge)
        {
            return;
        }

        _roamCornerTargetEdge = targetEdge;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        _isRoamCornerTurning = true;
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
    }

    private void AdvanceRoamCornerTurn(TimeSpan delta)
    {
        if (!_isRoamCornerTurning)
        {
            return;
        }

        _roamCornerTurnElapsed += delta;
        var progress = Math.Clamp(
            _roamCornerTurnElapsed.TotalMilliseconds /
            RoamCornerTurnDuration.TotalMilliseconds,
            0,
            1);
        var turnPhase = progress <= 0.5
            ? progress * 2
            : (1 - progress) * 2;
        var easedPhase = turnPhase * turnPhase * (3 - 2 * turnPhase);

        // 素材切换发生在角色被压缩到最小的时刻。始终保留单图层和不透明显示，
        // 因而不会重引入点击动作曾出现的双层闪烁。
        PetCornerScale.ScaleX = 1 - easedPhase * 0.88;
        PetCornerScale.ScaleY = 1 - easedPhase * 0.72;

        if (progress >= 0.5 && _roamEdge != _roamCornerTargetEdge)
        {
            _roamEdge = _roamCornerTargetEdge;
        }

        if (progress < 1)
        {
            return;
        }

        _isRoamCornerTurning = false;
        _roamCornerTargetEdge = EdgeDock.None;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
    }

    private void UpdateRoamVisual()
    {
        if (!_isEdgeRoaming)
        {
            return;
        }

        var horizontal = _roamApproaching
            ? Math.Abs(_roamApproachTarget.X - Left) >=
              Math.Abs(_roamApproachTarget.Y - Top)
            : _roamEdge is EdgeDock.Top or EdgeDock.Bottom;
        var movingPositive = _roamApproaching
            ? horizontal
                ? _roamApproachTarget.X >= Left
                : _roamApproachTarget.Y >= Top
            : IsPositiveRoamDirection(_roamEdge, _roamClockwise);
        var baseFrameIndex = (int)(_roamStopwatch.Elapsed.TotalMilliseconds /
                                   RoamSpriteFrameInterval.TotalMilliseconds) %
                             RoamFrameCount;
        var frameIndex = baseFrameIndex;
        var modeIndex = (int)_roamMode;
        var frames = horizontal
            ? _roamHorizontalFrames[modeIndex]
            : _roamVerticalFrames[modeIndex];

        if (horizontal)
        {
            // 横向原画朝左；向右移动时镜像，帧循环顺序保持不变。
            PetFacingScale.ScaleX = movingPositive ? -1 : 1;
        }
        else
        {
            PetFacingScale.ScaleX = _roamEdge == EdgeDock.Left ? -1 : 1;
        }

        PetFacingScale.ScaleY = 1;
        var visualEdge = _roamApproaching ? EdgeDock.None : _roamEdge;
        if (visualEdge != _roamVisualEdge)
        {
            AnimateRoamBaseOffset(visualEdge);
            _roamVisualEdge = visualEdge;
        }

        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;

        if (_roamMode == RoamMode.Hop && !_roamApproaching)
        {
            var phase = _roamStopwatch.Elapsed.TotalMilliseconds /
                        (RoamSpriteFrameInterval.TotalMilliseconds * RoamFrameCount) *
                        Math.PI * 2;
            var bob = Math.Abs(Math.Sin(phase)) * 6;
            switch (_roamEdge)
            {
                case EdgeDock.Top:
                    PetRoamOffset.Y += bob;
                    break;
                case EdgeDock.Bottom:
                    PetRoamOffset.Y -= bob;
                    break;
                case EdgeDock.Left:
                    PetRoamOffset.X += bob;
                    break;
                case EdgeDock.Right:
                    PetRoamOffset.X -= bob;
                    break;
            }
        }

        ShowStableFrame(frames[frameIndex]);
    }

    private void AnimateRoamBaseOffset(EdgeDock edge)
    {
        var target = edge switch
        {
            EdgeDock.Top => new Point(0, -GetTopRoamOffset(_roamMode)),
            EdgeDock.Bottom => new Point(0, 8),
            EdgeDock.Left => new Point(-27, 0),
            EdgeDock.Right => new Point(27, 0),
            _ => new Point(0, 0)
        };
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(260));
        PetRoamBaseOffset.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(PetRoamBaseOffset.X, target.X, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
        PetRoamBaseOffset.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(PetRoamBaseOffset.Y, target.Y, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.HoldEnd
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private EdgeDock FindNearestEdge(Rect workArea)
    {
        return new (EdgeDock Dock, double Distance)[]
            {
                (EdgeDock.Left, Math.Abs(Left - workArea.Left)),
                (EdgeDock.Right, Math.Abs(Left + ActualWidth - workArea.Right)),
                (EdgeDock.Top, Math.Abs(Top - workArea.Top)),
                (EdgeDock.Bottom, Math.Abs(Top + ActualHeight - workArea.Bottom))
            }
            .MinBy(candidate => candidate.Distance)
            .Dock;
    }

    private Point GetDockedPosition(EdgeDock edge, Rect workArea)
    {
        return edge switch
        {
            EdgeDock.Left => new Point(
                workArea.Left,
                Math.Clamp(Top, workArea.Top, workArea.Bottom - ActualHeight)),
            EdgeDock.Right => new Point(
                workArea.Right - ActualWidth,
                Math.Clamp(Top, workArea.Top, workArea.Bottom - ActualHeight)),
            EdgeDock.Top => new Point(
                Math.Clamp(Left, workArea.Left, workArea.Right - ActualWidth),
                workArea.Top),
            EdgeDock.Bottom => new Point(
                Math.Clamp(Left, workArea.Left, workArea.Right - ActualWidth),
                workArea.Bottom - ActualHeight),
            _ => new Point(Left, Top)
        };
    }

    private static bool IsPositiveRoamDirection(EdgeDock edge, bool clockwise)
    {
        return edge switch
        {
            EdgeDock.Top or EdgeDock.Right => clockwise,
            EdgeDock.Bottom or EdgeDock.Left => !clockwise,
            _ => true
        };
    }

    private static EdgeDock GetNextRoamEdge(EdgeDock edge, bool clockwise)
    {
        return (edge, clockwise) switch
        {
            (EdgeDock.Top, true) => EdgeDock.Right,
            (EdgeDock.Right, true) => EdgeDock.Bottom,
            (EdgeDock.Bottom, true) => EdgeDock.Left,
            (EdgeDock.Left, true) => EdgeDock.Top,
            (EdgeDock.Top, false) => EdgeDock.Left,
            (EdgeDock.Left, false) => EdgeDock.Bottom,
            (EdgeDock.Bottom, false) => EdgeDock.Right,
            (EdgeDock.Right, false) => EdgeDock.Top,
            _ => EdgeDock.Bottom
        };
    }

    private static double GetRoamSpeed(RoamMode mode)
    {
        return mode switch
        {
            RoamMode.Wriggle => 42,
            RoamMode.Crawl => 60,
            RoamMode.Hop => 86,
            _ => 60
        };
    }

    private static double GetTopRoamOffset(RoamMode mode)
    {
        return mode switch
        {
            RoamMode.Wriggle => 90,
            RoamMode.Crawl => 74,
            RoamMode.Hop => 43,
            _ => 0
        };
    }

    private static string GetRoamAssetName(RoamMode mode)
    {
        return mode switch
        {
            RoamMode.Wriggle => "wriggle",
            RoamMode.Crawl => "crawl",
            RoamMode.Hop => "hop",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private static string GetRoamModeName(RoamMode mode)
    {
        return mode switch
        {
            RoamMode.Wriggle => "趴着蠕动",
            RoamMode.Crawl => "爬行",
            RoamMode.Hop => "走跳",
            _ => "未知"
        };
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
        if (_isClosing || _activeClip is not null || _dragInteractionActive ||
            _edgeDock != EdgeDock.None || _isEdgeRoaming)
        {
            return false;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        _activeClip = clip;
        _activeFrameIndex = -1;
        CuteMessageText.Text = clip.Message;
        AppLogger.Info(
            $"动作开始：{clip.ActionName}，触发方式：{(showCuteBubble ? "点击" : "自动")}");

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
            _isPillowBreathing || _dragInteractionActive ||
            _bubbleMode == BubbleMode.Todo || _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            return;
        }

        _automaticTimer.Stop();
        _automaticActivityCount++;
        if (_edgeRoamingEnabled && _automaticActivityCount % 4 == 0)
        {
            StartEdgeRoaming();
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
        _automaticTimer.Stop();
        if (_isClosing || !_automaticAnimationEnabled ||
            _activeClip is not null || _isPillowBreathing || _dragInteractionActive ||
            _bubbleMode == BubbleMode.Todo || _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            return;
        }

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
            RestartAutomaticCountdown();
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

        ShowStableFrame(GetDisplayImage(_idleImage));
        if (!ReferenceEquals(_activeClip, clip))
        {
            return;
        }

        _activeClip = null;
        _activeFrameIndex = -1;
        AppLogger.Info($"动作完成：{clip.ActionName}");
        if (_bubbleMode == BubbleMode.Cute)
        {
            SetBubbleMode(BubbleMode.None);
        }
        RestartAutomaticCountdown();
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
        ShowStableFrame(frame.Image);
        if (_isClosing || !ReferenceEquals(_activeClip, clip) || _activeFrameIndex != frameIndex)
        {
            return;
        }

        _frameTimer.Interval = frame.HoldDuration;
        _frameTimer.Start();
    }

    private void ShowStableFrame(BitmapSource image)
    {
        PetImage.BeginAnimation(OpacityProperty, null);
        PetImage.Opacity = 1;
        PetImage.Source = image;
        PetImageOverlay.Source = null;
        PetImageOverlay.Opacity = 0;
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

        if (mode == BubbleMode.Todo)
        {
            StopEdgeRoaming("打开待办", restartAutomaticCountdown: false);
        }

        var previousMode = _bubbleMode;
        HideBubbleVisuals();
        ShowBubbleVisuals(mode);
        _bubbleMode = mode;
        AppLogger.Info($"气泡状态：{previousMode} -> {mode}");

        if (mode == BubbleMode.Todo)
        {
            _automaticTimer.Stop();
        }
        else if (previousMode == BubbleMode.Todo)
        {
            RestartAutomaticCountdown();
        }
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

    private void AutoRoamToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady)
        {
            return;
        }

        var enabled = AutoRoamToggle.IsChecked == true;
        if (_edgeRoamingEnabled == enabled)
        {
            return;
        }

        _edgeRoamingEnabled = enabled;
        if (!enabled)
        {
            StopEdgeRoaming("已通过右键开关关闭", restartAutomaticCountdown: false);
        }

        var saved = _settingsStore.Save(new AppSettings
        {
            EdgeRoamingEnabled = enabled
        });
        AppLogger.Info($"自动绕屏开关：{enabled}，设置保存：{saved}");
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
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
        AppLogger.Info($"新增待办，当前数量：{_todos.Count}");
    }

    private void TodoCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: TodoItem item } checkBox)
        {
            item.IsCompleted = checkBox.IsChecked == true;
            SaveTodos();
            AppLogger.Info($"待办完成状态已更新，已完成：{item.IsCompleted}");
        }
    }

    private void DeleteTodoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TodoItem item })
        {
            _todos.Remove(item);
            SaveTodos();
            AppLogger.Info($"删除待办，当前数量：{_todos.Count}");
        }
    }

    private void SaveTodos()
    {
        if (!_todoStore.Save(_todos))
        {
            AppLogger.Info("待办保存失败，请检查本地应用数据目录权限");
        }
    }

    private enum BubbleMode
    {
        None,
        Cute,
        Todo
    }

    private enum EdgeDock
    {
        None,
        Left,
        Right,
        Top,
        Bottom
    }

    private enum RoamMode
    {
        Wriggle,
        Crawl,
        Hop
    }

    private sealed record AnimationClip(
        string Message,
        string ActionName,
        AnimationFrame[] Frames,
        int ActionFrameIndex);

    private sealed record AnimationFrame(
        BitmapSource Image,
        TimeSpan HoldDuration,
        string Name);
}
