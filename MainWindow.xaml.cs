using System;
using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LubanDesktopPet;

public partial class MainWindow : Window
{
    private const double PetWidth = 190;
    private const double PetHeight = 242;
    private const double MinimumPetSizeScale = 0.75;
    private const double MaximumPetSizeScale = 1.40;
    private const double CuteBubbleHeight = 76;
    private const double ScreenEdgeMargin = 12;
    private const double EdgeContactTolerance = 1;
    private const int DisplayPixelWidth = 190;
    private const int DisplayPixelHeight = 242;
    private const int SpriteAtlasFrameCount = 291;
    private const string SpriteAtlasManifestPath = "Assets/luban-sprite-pages.json";
    private const long SpritePageCollectionThresholdBytes = 320L * 1024L * 1024L;
    private const int WakeFrameCount = 14;
    private const int ActionPoseFrameCount = 24;
    private const int ActionLoopStartPoseNumber = 21;
    private const int ActionLoopPoseCount = 4;
    private const int ActionLoopCycleCount = 8;
    private const int RunLoopStartPoseNumber = 9;
    private const int RunLoopPoseCount = 16;
    private const int RunLoopCycleCount = 6;
    private const int EdgePeekFrameCount = 4;
    private const int RoamFrameCount = 8;
    private static readonly TimeSpan MotionFrameInterval = TimeSpan.FromMilliseconds(85);
    private static readonly TimeSpan ActionLoopFrameInterval = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan RunLoopFrameInterval = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan EdgePeekFrameInterval = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan EdgePeekEndpointHold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RoamRenderInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan RoamSpriteFrameInterval = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan RoamCornerTurnDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan RoamVisualTransitionDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan FrameBlendDuration = TimeSpan.FromMilliseconds(55);
    private static readonly TimeSpan RoamFrameBlendDuration = TimeSpan.FromMilliseconds(65);
    private static readonly TimeSpan EdgeFrameBlendDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TodoStateBlendDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan SpritePageCollectionCooldown = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumRoamScheduleDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumRoamScheduleDelay = TimeSpan.FromMinutes(20);

    private readonly IReadOnlyDictionary<string, SpriteAtlasPage> _spritePages;
    private readonly WriteableBitmap _spritePageBuffer;
    private readonly WriteableBitmap _displayFrameBuffer;
    private readonly byte[] _displayFramePixels =
        new byte[DisplayPixelWidth * DisplayPixelHeight * 4];
    private readonly byte[] _frameBlendFromPixels =
        new byte[DisplayPixelWidth * DisplayPixelHeight * 4];
    private readonly byte[] _frameBlendTargetPixels =
        new byte[DisplayPixelWidth * DisplayPixelHeight * 4];
    private readonly byte[] _frameBlendOutputPixels =
        new byte[DisplayPixelWidth * DisplayPixelHeight * 4];
    private readonly byte[] _transformedDisplayFramePixels =
        new byte[DisplayPixelWidth * DisplayPixelHeight * 4];
    private readonly SpriteFrame _idleFrame;
    private readonly SpriteFrame _todoFrame;
    private readonly SpriteFrame[] _edgeLeftFrames;
    private readonly SpriteFrame[] _edgeTopFrames;
    private readonly SpriteFrame[] _edgeBottomFrames;
    private readonly SpriteFrame[][] _roamHorizontalFrames;
    private readonly SpriteFrame[][] _roamVerticalUpFrames;
    private readonly SpriteFrame[][] _roamVerticalDownFrames;
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip _todoEnterClip;
    private readonly AnimationClip _todoExitClip;
    private readonly AnimationClip?[] _automaticActivities;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _edgePeekTimer;
    private readonly DispatcherTimer _roamTimer;
    private readonly DispatcherTimer _automaticTimer;
    private readonly Stopwatch _roamStopwatch = new();
    private readonly Queue<int> _automaticActivityBag = new();
    private readonly Random _random = new();
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly TodoStore _todoStore = TodoStore.CreateDefault();
    private readonly AppSettingsStore _settingsStore = AppSettingsStore.CreateDefault();
    private readonly TodoWindow _todoWindow;

    private BubbleMode _bubbleMode;
    private Point _pointerDownPosition;
    private bool _pointerDown;
    private bool _dragStarted;
    private bool _dragInteractionActive;
    private AnimationClip? _activeClip;
    private int _activeFrameIndex = -1;
    private int _nextClipIndex;
    private int _lastAutomaticActivityIndex = -1;
    private int _pillowAnimationGeneration;
    private int _outsideTodoCloseGeneration;
    private int _spritePageCleanupGeneration;
    private int _spritePageCollectionInFlight;
    private DateTimeOffset _lastSpritePageCollectionUtc = DateTimeOffset.MinValue;
    private int _edgePeekFrameIndex;
    private int _edgePeekFrameDirection = 1;
    private EdgeDock _edgeDock;
    private EdgeDock _roamEdge;
    private EdgeDock _roamVisualEdge;
    private EdgeDock _roamCornerTargetEdge;
    private RoamVisualDirection _roamVisualDirection;
    private RoamMode _roamMode;
    private Rect _roamWorkArea;
    private Point _roamApproachTarget;
    private Point _roamBoundaryStart;
    private TimeSpan _roamLastElapsed;
    private TimeSpan _roamCornerTurnElapsed;
    private TimeSpan _roamVisualPhaseStartedAt;
    private TimeSpan _roamBaseOffsetTransitionStartedAt;
    private Point _roamBaseOffsetTransitionStart;
    private Point _roamBaseOffsetTransitionTarget;
    private double _roamBoundaryTargetDistance;
    private double _roamBoundaryTravelled;
    private DateTimeOffset _nextRoamDueUtc;
    private bool _roamClockwise;
    private bool _roamApproaching;
    private bool _isRoamCornerTurning;
    private bool _isRoamBaseOffsetTransitioning;
    private bool _isEdgeRoaming;
    private bool _edgeRoamingEnabled;
    private double _petSizeScale = 1;
    private bool _automaticAnimationEnabled;
    private bool _isPillowBreathing;
    private bool _isClosing;
    private bool _suppressTodoWindowDeactivate;
    private bool _displaySettingsSubscribed;
    private SpriteFrame? _currentSpriteFrame;
    private string? _loadedSpritePageName;
    private bool _isFrameBlending;
    private long _frameBlendStartedTimestamp;
    private TimeSpan _activeFrameBlendDuration;
    private TimeSpan? _nextFrameBlendDuration;
    private TimeSpan _nextFrameMinimumHold;

    public MainWindow()
    {
        InitializeComponent();

        _spritePages = LoadSpritePages(BuildSpriteResourcePaths());
        _spritePageBuffer = new WriteableBitmap(
            _spritePages.Values.Max(page => page.Width),
            _spritePages.Values.Max(page => page.Height),
            96,
            96,
            PixelFormats.Pbgra32,
            null);
        _displayFrameBuffer = new WriteableBitmap(
            DisplayPixelWidth,
            DisplayPixelHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null);
        PetSpriteBrush.ImageSource = _displayFrameBuffer;
        _idleFrame = GetSpriteFrame("idle", "Assets/luban-idle.png");
        _todoFrame = GetSpriteFrame(
            "action-think",
            "Assets/luban-think-frame-24.png");
        _edgeLeftFrames = LoadFrameSequence("edge", "luban-edge-left", EdgePeekFrameCount);
        _edgeTopFrames = LoadFrameSequence("edge", "luban-edge-top", EdgePeekFrameCount);
        _edgeBottomFrames = LoadFrameSequence("edge", "luban-edge-bottom", EdgePeekFrameCount);
        _roamHorizontalFrames = Enum.GetValues<RoamMode>()
            .Select(mode => LoadFrameSequence(
                $"roam-{GetRoamAssetName(mode)}",
                $"luban-roam-{GetRoamAssetName(mode)}-horizontal",
                RoamFrameCount))
            .ToArray();
        _roamVerticalUpFrames = Enum.GetValues<RoamMode>()
            .Select(mode => LoadFrameSequence(
                $"roam-{GetRoamAssetName(mode)}",
                $"luban-roam-{GetRoamAssetName(mode)}-vertical-up",
                RoamFrameCount))
            .ToArray();
        _roamVerticalDownFrames = Enum.GetValues<RoamMode>()
            .Select(mode => LoadFrameSequence(
                $"roam-{GetRoamAssetName(mode)}",
                $"luban-roam-{GetRoamAssetName(mode)}-vertical-down",
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
        _todoExitClip = CreateTodoExitClip();
        _todoEnterClip = CreateTodoEnterClip();
        _automaticActivities =
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
        ShowStableFrame(_idleFrame);

        foreach (var item in _todoStore.Load())
        {
            _todos.Add(item);
        }

        _todoWindow = new TodoWindow
        {
            Todos = _todos
        };
        _todoWindow.AddRequested += TodoWindow_AddRequested;
        _todoWindow.TodoChanged += TodoWindow_TodoChanged;
        _todoWindow.DeleteRequested += TodoWindow_DeleteRequested;
        _todoWindow.AutoRoamChanged += TodoWindow_AutoRoamChanged;
        _todoWindow.PetSizeScaleChanged += TodoWindow_PetSizeScaleChanged;
        _todoWindow.CloseRequested += TodoWindow_CloseRequested;
        _todoWindow.ExitRequested += TodoWindow_ExitRequested;
        _todoWindow.ImeCompositionChanged += TodoWindow_ImeCompositionChanged;
        _todoWindow.Deactivated += TodoWindow_Deactivated;
        _todoWindow.LostKeyboardFocus += TodoWindow_LostKeyboardFocus;

        var settings = _settingsStore.Load();
        _edgeRoamingEnabled = settings.EdgeRoamingEnabled;
        _petSizeScale = NormalizePetSizeScale(settings.PetSizeScale);
        _nextRoamDueUtc = DateTimeOffset.UtcNow + GetRandomRoamScheduleDelay();
        _todoWindow.SetAutoRoam(_edgeRoamingEnabled);
        _todoWindow.SetPetSizeScale(_petSizeScale);
        ApplyPetSizeScale(_petSizeScale, persist: false, preservePosition: false);

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render);
        _frameTimer.Tick += FrameTimer_Tick;

        _edgePeekTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = EdgePeekEndpointHold
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
        AppLogger.Info(
            $"渲染管线：{DisplayPixelWidth}×{DisplayPixelHeight} 固定完整帧，" +
            "单缓冲预乘 Alpha 淡化，活动过渡跟随屏幕刷新率");
    }

    private void ScheduleUnusedSpritePageCollection(string reason)
    {
        var generation = ++_spritePageCleanupGeneration;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                if (_isClosing || generation != _spritePageCleanupGeneration ||
                    _activeClip is not null || _isEdgeRoaming ||
                    _dragInteractionActive || _pointerDown ||
                    _bubbleMode == BubbleMode.Todo)
                {
                    return;
                }

                long privateMemoryBefore;
                using (var process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    privateMemoryBefore = process.PrivateMemorySize64;
                }

                var now = DateTimeOffset.UtcNow;
                if (privateMemoryBefore < SpritePageCollectionThresholdBytes ||
                    now - _lastSpritePageCollectionUtc < SpritePageCollectionCooldown ||
                    Interlocked.CompareExchange(
                        ref _spritePageCollectionInFlight,
                        1,
                        0) != 0)
                {
                    return;
                }

                _lastSpritePageCollectionUtc = now;

                _ = Task.Run(() =>
                {
                    try
                    {
                        GC.Collect(
                            GC.MaxGeneration,
                            GCCollectionMode.Optimized,
                            blocking: false,
                            compacting: false);

                        using var process = Process.GetCurrentProcess();
                        process.Refresh();
                        AppLogger.Info(
                            $"精灵分页后台回收完成：{reason}，私有内存 " +
                            $"{privateMemoryBefore / 1024d / 1024d:F1} -> " +
                            $"{process.PrivateMemorySize64 / 1024d / 1024d:F1} MiB");
                    }
                    catch (Exception exception)
                    {
                        AppLogger.Error("精灵分页后台回收失败", exception);
                    }
                    finally
                    {
                        Volatile.Write(ref _spritePageCollectionInFlight, 0);
                    }
                });
            }));
    }

    private SpriteFrame[] LoadFrameSequence(
        string pageName,
        string resourcePrefix,
        int frameCount)
    {
        return Enumerable.Range(1, frameCount)
            .Select(frameNumber => GetSpriteFrame(
                pageName,
                $"Assets/{resourcePrefix}-{frameNumber:00}.png"))
            .ToArray();
    }

    private AnimationClip CreateMotionClip(string message, string actionName)
    {
        var spritePageName = $"action-{actionName}";
        var timeline = new SpriteFrame[WakeFrameCount + ActionPoseFrameCount + 1];
        var names = new string[timeline.Length];
        timeline[0] = GetSpriteFrame(spritePageName, "Assets/luban-idle.png");
        names[0] = "luban-idle.png";

        for (var wakeFrameNumber = 1;
             wakeFrameNumber <= WakeFrameCount;
             wakeFrameNumber++)
        {
            var timelineIndex = wakeFrameNumber;
            timeline[timelineIndex] = GetSpriteFrame(
                spritePageName,
                $"Assets/luban-wake-{wakeFrameNumber:00}.png");
            names[timelineIndex] = $"luban-wake-{wakeFrameNumber:00}.png";
        }

        for (var actionFrameNumber = 1;
             actionFrameNumber <= ActionPoseFrameCount;
             actionFrameNumber++)
        {
            var frameName = $"luban-{actionName}-frame-{actionFrameNumber:00}.png";
            var timelineIndex = WakeFrameCount + actionFrameNumber;
            timeline[timelineIndex] = GetSpriteFrame(
                spritePageName,
                $"Assets/{frameName}");
            names[timelineIndex] = frameName;
        }

        if (string.Equals(actionName, "run", StringComparison.Ordinal))
        {
            return CreateRunMotionClip(message, actionName, timeline, names);
        }

        var frames = new List<AnimationFrame>(
            (timeline.Length - 1) * 2 + ActionLoopPoseCount * ActionLoopCycleCount);
        for (var timelineIndex = 1; timelineIndex < timeline.Length; timelineIndex++)
        {
            frames.Add(new AnimationFrame(
                timeline[timelineIndex], MotionFrameInterval, names[timelineIndex]));
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

    private static AnimationClip CreateRunMotionClip(
        string message,
        string actionName,
        SpriteFrame[] timeline,
        string[] names)
    {
        var entryLastTimelineIndex = WakeFrameCount;
        var frames = new List<AnimationFrame>(
            entryLastTimelineIndex +
            RunLoopPoseCount * RunLoopCycleCount +
            1 +
            WakeFrameCount + 1);

        for (var timelineIndex = 1;
             timelineIndex <= entryLastTimelineIndex;
             timelineIndex++)
        {
            frames.Add(new AnimationFrame(
                timeline[timelineIndex], MotionFrameInterval, names[timelineIndex]));
        }

        var actionFrameIndex = frames.Count - 1;
        for (var cycle = 0; cycle < RunLoopCycleCount; cycle++)
        {
            for (var poseOffset = 0; poseOffset < RunLoopPoseCount; poseOffset++)
            {
                var poseNumber = RunLoopStartPoseNumber + poseOffset;
                var timelineIndex = WakeFrameCount + poseNumber;
                frames.Add(new AnimationFrame(
                    timeline[timelineIndex], RunLoopFrameInterval, names[timelineIndex]));
            }
        }

        // 用下一周期的接触相位落稳，再回到 wake/idle。旧实现把 16 帧
        // 侧身跑完整倒放，既像倒着跑，也会混入踢腿姿势。
        var contactTimelineIndex = WakeFrameCount + RunLoopStartPoseNumber;
        frames.Add(new AnimationFrame(
            timeline[contactTimelineIndex],
            RunLoopFrameInterval,
            names[contactTimelineIndex]));

        for (var timelineIndex = WakeFrameCount;
             timelineIndex >= 0;
             timelineIndex--)
        {
            frames.Add(new AnimationFrame(
                timeline[timelineIndex], MotionFrameInterval, names[timelineIndex]));
        }

        return new AnimationClip(message, actionName, frames.ToArray(), actionFrameIndex);
    }

    private AnimationClip CreateTodoExitClip()
    {
        const string pageName = "action-think";
        var frames = new List<AnimationFrame>(WakeFrameCount + 2)
        {
            new(
                _todoFrame,
                TimeSpan.FromMilliseconds(140),
                "luban-think-frame-24.png")
        };

        for (var wakeFrameNumber = WakeFrameCount;
             wakeFrameNumber >= 1;
             wakeFrameNumber--)
        {
            var frameName = $"luban-wake-{wakeFrameNumber:00}.png";
            frames.Add(new AnimationFrame(
                GetSpriteFrame(pageName, $"Assets/{frameName}"),
                MotionFrameInterval,
                frameName));
        }

        // 最后一帧也留在 action-think 分页，避免收起时切换整张图集造成闪帧。
        frames.Add(new AnimationFrame(
            GetSpriteFrame(pageName, "Assets/luban-idle.png"),
            MotionFrameInterval,
            "luban-idle.png"));
        return new AnimationClip(
            string.Empty,
            "todo-close",
            frames.ToArray(),
            ActionFrameIndex: 0);
    }

    private AnimationClip CreateTodoEnterClip()
    {
        return new AnimationClip(
            string.Empty,
            "todo-open",
            _todoExitClip.Frames.Reverse().ToArray(),
            ActionFrameIndex: WakeFrameCount + 1);
    }

    private static string[] BuildSpriteResourcePaths()
    {
        var resourcePaths = new List<string>(SpriteAtlasFrameCount)
        {
            "Assets/luban-idle.png"
        };

        resourcePaths.AddRange(Enumerable.Range(1, WakeFrameCount)
            .Select(frameNumber => $"Assets/luban-wake-{frameNumber:00}.png"));

        foreach (var edgeName in new[] { "left", "top", "bottom" })
        {
            resourcePaths.AddRange(Enumerable.Range(1, EdgePeekFrameCount)
                .Select(frameNumber =>
                    $"Assets/luban-edge-{edgeName}-{frameNumber:00}.png"));
        }

        foreach (var mode in Enum.GetValues<RoamMode>())
        {
            var modeName = GetRoamAssetName(mode);
            foreach (var directionName in new[]
                     {
                         "horizontal", "vertical-up", "vertical-down"
                     })
            {
                resourcePaths.AddRange(Enumerable.Range(1, RoamFrameCount)
                    .Select(frameNumber =>
                        $"Assets/luban-roam-{modeName}-{directionName}-{frameNumber:00}.png"));
            }
        }

        foreach (var actionName in new[]
                 {
                     "yawn", "cry", "run", "cute",
                     "like", "eat", "wave", "think"
                 })
        {
            resourcePaths.AddRange(Enumerable.Range(1, ActionPoseFrameCount)
                .Select(frameNumber =>
                    $"Assets/luban-{actionName}-frame-{frameNumber:00}.png"));
        }

        if (resourcePaths.Count != SpriteAtlasFrameCount ||
            resourcePaths.Distinct(StringComparer.Ordinal).Count() != resourcePaths.Count)
        {
            throw new InvalidOperationException(
                $"精灵图集资源清单异常：期望 {SpriteAtlasFrameCount}，实际 {resourcePaths.Count}。");
        }

        return resourcePaths.ToArray();
    }

    private static IReadOnlyDictionary<string, SpriteAtlasPage> LoadSpritePages(
        IReadOnlyList<string> resourcePaths)
    {
        if (resourcePaths.Count != SpriteAtlasFrameCount)
        {
            throw new ArgumentException(
                $"精灵图集必须包含 {SpriteAtlasFrameCount} 帧。",
                nameof(resourcePaths));
        }

        var manifestUri = CreatePackUri(SpriteAtlasManifestPath);
        var manifestResource = Application.GetResourceStream(manifestUri)
            ?? throw new InvalidOperationException(
                $"找不到精灵图集清单：{SpriteAtlasManifestPath}");
        SpriteAtlasManifest manifest;
        using (manifestResource.Stream)
        {
            manifest = JsonSerializer.Deserialize<SpriteAtlasManifest>(
                           manifestResource.Stream,
                           new JsonSerializerOptions
                           {
                               PropertyNameCaseInsensitive = true
                           })
                       ?? throw new InvalidOperationException("精灵图集清单为空。");
        }

        if (manifest.Version != 2 ||
            manifest.DisplayWidth != DisplayPixelWidth ||
            manifest.DisplayHeight != DisplayPixelHeight ||
            manifest.SourceFrameCount != SpriteAtlasFrameCount ||
            manifest.PageFrameCount < SpriteAtlasFrameCount ||
            manifest.Pages.Count == 0)
        {
            throw new InvalidOperationException("精灵图集分页清单的尺寸或版本不匹配。");
        }

        var expectedResources = resourcePaths.ToHashSet(StringComparer.Ordinal);
        var foundResources = new HashSet<string>(StringComparer.Ordinal);
        var pageMap = new Dictionary<string, SpriteAtlasPage>(
            manifest.Pages.Count,
            StringComparer.Ordinal);
        var pageFrameCount = 0;
        foreach (var (pageName, pageDescriptor) in manifest.Pages)
        {
            if (string.IsNullOrWhiteSpace(pageName) ||
                string.IsNullOrWhiteSpace(pageDescriptor.Resource) ||
                pageDescriptor.Width <= 0 || pageDescriptor.Height <= 0 ||
                pageDescriptor.LogicalFrameCount != pageDescriptor.Frames.Count ||
                pageDescriptor.UniqueSpriteCount <= 0 ||
                pageDescriptor.UniqueSpriteCount > pageDescriptor.Frames.Count)
            {
                throw new InvalidOperationException(
                    $"精灵图集分页清单异常：{pageName}");
            }

            var frameMap = new Dictionary<string, SpriteFrame>(
                pageDescriptor.Frames.Count,
                StringComparer.Ordinal);
            foreach (var (resourcePath, descriptor) in pageDescriptor.Frames)
            {
                if (!expectedResources.Contains(resourcePath) ||
                    descriptor.Width <= 0 || descriptor.Height <= 0 ||
                    descriptor.X < 0 || descriptor.Y < 0 ||
                    descriptor.X + descriptor.Width > pageDescriptor.Width ||
                    descriptor.Y + descriptor.Height > pageDescriptor.Height ||
                    descriptor.DestinationX >= DisplayPixelWidth ||
                    descriptor.DestinationY >= DisplayPixelHeight ||
                    descriptor.DestinationX + descriptor.Width <= 0 ||
                    descriptor.DestinationY + descriptor.Height <= 0)
                {
                    throw new InvalidOperationException(
                        $"精灵图集帧越界：{pageName}/{resourcePath}");
                }

                foundResources.Add(resourcePath);
                frameMap.Add(
                    resourcePath,
                    new SpriteFrame(
                        descriptor.X,
                        descriptor.Y,
                        descriptor.Width,
                        descriptor.Height,
                        descriptor.DestinationX,
                        descriptor.DestinationY,
                        pageName,
                        resourcePath));
            }

            pageFrameCount += frameMap.Count;
            pageMap.Add(
                pageName,
                new SpriteAtlasPage(
                    pageDescriptor.Resource,
                    pageDescriptor.Width,
                    pageDescriptor.Height,
                    new ReadOnlyDictionary<string, SpriteFrame>(frameMap)));
        }

        if (!foundResources.SetEquals(expectedResources) ||
            pageFrameCount != manifest.PageFrameCount)
        {
            throw new InvalidOperationException("精灵图集分页清单未完整覆盖源帧。");
        }

        return new ReadOnlyDictionary<string, SpriteAtlasPage>(pageMap);
    }

    private static BitmapSource LoadPackagedBitmap(string resourcePath)
    {
        var resource = Application.GetResourceStream(CreatePackUri(resourcePath))
            ?? throw new InvalidOperationException($"找不到桌宠图片资源：{resourcePath}");
        BitmapImage bitmap;
        using (resource.Stream)
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = resource.Stream;
            bitmap.EndInit();
            bitmap.Freeze();
        }

        return bitmap;
    }

    private static Uri CreatePackUri(string resourcePath)
    {
        return new Uri(
            $"pack://application:,,,/LubanDesktopPet;component/{resourcePath}",
            UriKind.Absolute);
    }

    private SpriteFrame GetSpriteFrame(string pageName, string resourcePath)
    {
        return _spritePages.TryGetValue(pageName, out var page) &&
               page.Frames.TryGetValue(resourcePath, out var frame)
            ? frame
            : throw new KeyNotFoundException(
                $"精灵图集不包含资源：{pageName}/{resourcePath}");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_todoWindow.Owner is null)
        {
            _todoWindow.Owner = this;
        }

        if (!_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            _displaySettingsSubscribed = true;
        }

        var workArea = MonitorWorkArea.GetForWindow(this);
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - ScreenEdgeMargin);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - ScreenEdgeMargin);
        _automaticAnimationEnabled = true;
        RestartAutomaticCountdown();
        AppLogger.Info($"主窗口已显示，位置 ({Left:F0}, {Top:F0})");
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (_todoWindow.IsVisible)
        {
            UpdateTodoWindowPosition();
        }

        if (!BubblePopup.IsOpen)
        {
            return;
        }

        // WPF Popup 使用独立 HWND，父窗口移动时不会自行重算屏幕坐标。
        // 轻微重设偏移会在 DragMove 的每次 LocationChanged 中强制它跟随宠物。
        var horizontalOffset = BubblePopup.HorizontalOffset;
        BubblePopup.HorizontalOffset = horizontalOffset + 0.01;
        BubblePopup.HorizontalOffset = horizontalOffset;
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    if (_isClosing)
                    {
                        return;
                    }

                    var resumeRoaming = _isEdgeRoaming && _edgeRoamingEnabled;
                    StopEdgeRoaming("显示器配置已变化", restartAutomaticCountdown: false);
                    ExitEdgePeek(restartAutomaticCountdown: false);
                    var workArea = MonitorWorkArea.GetForWindow(this);
                    var width = ActualWidth > 0 ? ActualWidth : Width;
                    var height = ActualHeight > 0 ? ActualHeight : Height;
                    Left = Math.Clamp(
                        Left,
                        workArea.Left,
                        Math.Max(workArea.Left, workArea.Right - width));
                    Top = Math.Clamp(
                        Top,
                        workArea.Top,
                        Math.Max(workArea.Top, workArea.Bottom - height));
                    UpdateTodoWindowPosition();
                    if (resumeRoaming)
                    {
                        _nextRoamDueUtc = DateTimeOffset.UtcNow;
                    }

                    RestartAutomaticCountdown();
                    AppLogger.Info("显示器配置已变化，桌宠位置与绕屏路径已重新校准");
                }));
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 正在关闭；在途的系统显示事件可以安全忽略。
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        RestartAutomaticCountdown();

        if (_bubbleMode != BubbleMode.Todo)
        {
            return;
        }

        var eventSource = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject;
        // PetHost 需要先区分“点击”和“拖动”。拖动时待办应跟随，不能在 MouseDown
        // 阶段提前收起；单击则在 MouseUp 阶段收起。
        if (IsWithin(eventSource, PetHost))
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
        ScheduleOutsideTodoClose();
    }

    private void TodoWindow_Deactivated(object? sender, EventArgs e)
    {
        ScheduleOutsideTodoClose();
    }

    private void TodoWindow_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        ScheduleOutsideTodoClose();
    }

    private void ScheduleOutsideTodoClose()
    {
        if (_isClosing || _suppressTodoWindowDeactivate ||
            _bubbleMode != BubbleMode.Todo)
        {
            return;
        }

        var generation = ++_outsideTodoCloseGeneration;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                if (_isClosing || generation != _outsideTodoCloseGeneration ||
                    _bubbleMode != BubbleMode.Todo ||
                    _todoWindow.IsImeComposing ||
                    _todoWindow.IsKeyboardFocusWithin ||
                    _todoWindow.IsActive || IsActive ||
                    _dragInteractionActive || _pointerDown)
                {
                    return;
                }

                SetBubbleMode(BubbleMode.None);
            }));
    }

    private void UpdateTodoWindowPosition()
    {
        if (_isClosing)
        {
            return;
        }

        if (OwnedWindowPositioner.TryPosition(PetHost, _todoWindow, out var childIsOnLeft))
        {
            _todoWindow.SetTailOnRight(childIsOnLeft);
            return;
        }

        var workArea = MonitorWorkArea.GetForWindow(this);
        var bubbleWidth = _todoWindow.ActualWidth > 0
            ? _todoWindow.ActualWidth
            : _todoWindow.Width;
        var bubbleHeight = _todoWindow.ActualHeight > 0
            ? _todoWindow.ActualHeight
            : _todoWindow.Height;
        var petWidth = ActualWidth > 0 ? ActualWidth : Width;
        var petHeight = ActualHeight > 0 ? ActualHeight : Height;

        var canPlaceOnLeft = Left - bubbleWidth >= workArea.Left;
        var desiredLeft = canPlaceOnLeft
            ? Left - bubbleWidth
            : Left + petWidth;
        var desiredTop = Top + petHeight - bubbleHeight;
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - bubbleWidth);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - bubbleHeight);

        _todoWindow.SetTailOnRight(canPlaceOnLeft);
        _todoWindow.Left = Math.Clamp(desiredLeft, workArea.Left, maximumLeft);
        _todoWindow.Top = Math.Clamp(desiredTop, workArea.Top, maximumTop);
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

    private double SnapDipToPhysicalPixel(double value, bool horizontal)
    {
        var compositionTarget = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (compositionTarget is null)
        {
            return Math.Round(value);
        }

        var transform = compositionTarget.TransformToDevice;
        var scale = horizontal ? transform.M11 : transform.M22;
        return double.IsFinite(scale) && scale > 0
            ? Math.Round(value * scale) / scale
            : Math.Round(value);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        if (_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displaySettingsSubscribed = false;
        }
        AppLogger.Info("主窗口正在关闭");
        _automaticAnimationEnabled = false;
        _pillowAnimationGeneration++;
        _frameTimer.Stop();
        _frameTimer.Tick -= FrameTimer_Tick;
        StopFrameBlend(snapToTarget: false);
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
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
        _isRoamBaseOffsetTransitioning = false;
        _roamBaseOffsetTransitionStartedAt = TimeSpan.Zero;
        _roamBaseOffsetTransitionStart = new Point(0, 0);
        _roamBaseOffsetTransitionTarget = new Point(0, 0);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.YProperty, null);
        PetRoamBaseOffset.X = 0;
        PetRoamBaseOffset.Y = 0;
        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;
        HideBubbleVisuals();
        _suppressTodoWindowDeactivate = true;
        _todoWindow.Deactivated -= TodoWindow_Deactivated;
        _todoWindow.CloseForApplication();
        SaveTodos();
    }

    private void PetHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        StopEdgeRoaming(
            "用户开始点击或拖动",
            restartAutomaticCountdown: false,
            scheduleNextRoam: true);
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

        var wasSimpleClick = _pointerDown && !_dragStarted;
        var shouldActCute = wasSimpleClick &&
                            _edgeDock == EdgeDock.None;
        _pointerDown = false;
        _dragInteractionActive = false;
        PetHost.ReleaseMouseCapture();

        if (wasSimpleClick && _bubbleMode == BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.None);
        }

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
        SetBubbleMode(_bubbleMode == BubbleMode.Todo ? BubbleMode.None : BubbleMode.Todo);

        if (_bubbleMode == BubbleMode.Todo)
        {
            _todoWindow.FocusInput();
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
        var windowBounds = new Rect(
            Left,
            Top,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);
        var touchedEdge = FindTouchedEdge(workArea, windowBounds, EdgeContactTolerance);
        if (touchedEdge == EdgeDock.None)
        {
            RestartAutomaticCountdown();
            return;
        }

        switch (touchedEdge)
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

        if (_bubbleMode == BubbleMode.Todo)
        {
            ExitEdgePeek(
                restartAutomaticCountdown: false,
                restoreIdleFrame: false);
            ResetPetVisualTransforms();
            ShowStableTodoFrame();
            return;
        }

        EnterEdgePeek(touchedEdge);
    }

    private static EdgeDock FindTouchedEdge(
        Rect workArea,
        Rect windowBounds,
        double tolerance)
    {
        var candidates = new (EdgeDock Dock, double Gap)[]
        {
            (EdgeDock.Left, windowBounds.Left - workArea.Left),
            (EdgeDock.Right, workArea.Right - windowBounds.Right),
            (EdgeDock.Top, windowBounds.Top - workArea.Top),
            (EdgeDock.Bottom, workArea.Bottom - windowBounds.Bottom)
        };

        return candidates
            .Where(candidate => candidate.Gap <= tolerance)
            .OrderBy(candidate => Math.Abs(candidate.Gap))
            .ThenBy(candidate => (int)candidate.Dock)
            .Select(candidate => candidate.Dock)
            .DefaultIfEmpty(EdgeDock.None)
            .First();
    }

    private void EnterEdgePeek(EdgeDock dock)
    {
        if (_isClosing || dock == EdgeDock.None)
        {
            return;
        }

        if (_bubbleMode == BubbleMode.Todo)
        {
            _edgePeekTimer.Stop();
            _edgeDock = EdgeDock.None;
            ResetPetVisualTransforms();
            ShowStableTodoFrame();
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
        _edgePeekFrameDirection = 1;
        var targetFacingScaleX = dock == EdgeDock.Right ? -1 : 1;
        if (Math.Abs(PetFacingScale.ScaleX - targetFacingScaleX) > 0.001)
        {
            var previousVisualMatrix = GetPetVisualMatrix();
            PetFacingScale.ScaleX = targetFacingScaleX;
            RebaseDisplayFrameForPetTransformChange(
                previousVisualMatrix,
                GetPetVisualMatrix());
        }
        else
        {
            PetFacingScale.ScaleX = targetFacingScaleX;
        }

        PetFacingScale.ScaleY = 1;
        _nextFrameBlendDuration = EdgeFrameBlendDuration;
        ShowStableFrame(GetEdgeFrames(dock)[0]);
        _edgePeekTimer.Interval = EdgePeekEndpointHold;
        _edgePeekTimer.Start();
        AppLogger.Info($"边缘探头开始：{GetEdgeName(dock)}");
    }

    private void ExitEdgePeek(
        bool restartAutomaticCountdown,
        bool restoreIdleFrame = true)
    {
        if (_edgeDock == EdgeDock.None)
        {
            return;
        }

        var previousDock = _edgeDock;
        _edgePeekTimer.Stop();
        _edgeDock = EdgeDock.None;
        _edgePeekFrameIndex = 0;
        _edgePeekFrameDirection = 1;
        if (restoreIdleFrame)
        {
            BakeCurrentPetVisualTransformIntoDisplayFrame();
        }

        PetFacingScale.ScaleX = 1;
        PetFacingScale.ScaleY = 1;
        if (restoreIdleFrame)
        {
            ShowStableFrame(_idleFrame);
        }
        AppLogger.Info($"边缘探头结束：{GetEdgeName(previousDock)}");
        if (restartAutomaticCountdown)
        {
            RestartAutomaticCountdown();
        }
    }

    private void EdgePeekTimer_Tick(object? sender, EventArgs e)
    {
        if (_bubbleMode == BubbleMode.Todo)
        {
            _edgePeekTimer.Stop();
            ExitEdgePeek(
                restartAutomaticCountdown: false,
                restoreIdleFrame: false);
            ResetPetVisualTransforms();
            ShowStableTodoFrame();
            return;
        }

        if (_isClosing || _edgeDock == EdgeDock.None)
        {
            _edgePeekTimer.Stop();
            return;
        }

        var frames = GetEdgeFrames(_edgeDock);
        var nextFrameIndex = _edgePeekFrameIndex + _edgePeekFrameDirection;
        if (nextFrameIndex >= frames.Length)
        {
            _edgePeekFrameDirection = -1;
            nextFrameIndex = Math.Max(0, frames.Length - 2);
        }
        else if (nextFrameIndex < 0)
        {
            _edgePeekFrameDirection = 1;
            nextFrameIndex = Math.Min(frames.Length - 1, 1);
        }

        _edgePeekFrameIndex = nextFrameIndex;
        _nextFrameBlendDuration = EdgeFrameBlendDuration;
        ShowStableFrame(frames[_edgePeekFrameIndex]);
        _edgePeekTimer.Interval =
            _edgePeekFrameIndex == 0 || _edgePeekFrameIndex == frames.Length - 1
                ? EdgePeekEndpointHold
                : EdgePeekFrameInterval;
    }

    private SpriteFrame[] GetEdgeFrames(EdgeDock dock)
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

    private bool StartEdgeRoaming()
    {
        if (_isClosing || !_edgeRoamingEnabled || _dragInteractionActive ||
            _edgeDock != EdgeDock.None ||
            _activeClip is not null || _isPillowBreathing ||
            _bubbleMode == BubbleMode.Todo)
        {
            RestartAutomaticCountdown();
            return false;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        _roamWorkArea = MonitorWorkArea.GetForWindow(this);
        _roamEdge = FindNearestEdge(_roamWorkArea);
        _roamApproachTarget = GetDockedPosition(_roamEdge, _roamWorkArea);
        _roamApproaching = Math.Abs(Left - _roamApproachTarget.X) > 0.5 ||
                           Math.Abs(Top - _roamApproachTarget.Y) > 0.5;
        _roamMode = (RoamMode)_random.Next(Enum.GetValues<RoamMode>().Length);
        _roamClockwise = _random.Next(2) == 0;
        _roamLastElapsed = TimeSpan.Zero;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        _roamBoundaryTravelled = 0;
        _roamBoundaryTargetDistance = CalculateRoamPerimeter(
            _roamWorkArea,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);
        _roamBoundaryStart = _roamApproachTarget;
        _roamCornerTargetEdge = EdgeDock.None;
        _isRoamCornerTurning = false;
        _roamVisualDirection = RoamVisualDirection.None;
        _roamVisualPhaseStartedAt = TimeSpan.Zero;
        _isRoamBaseOffsetTransitioning = false;
        _roamBaseOffsetTransitionStartedAt = TimeSpan.Zero;
        _roamBaseOffsetTransitionStart = new Point(0, 0);
        _roamBaseOffsetTransitionTarget = new Point(0, 0);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.YProperty, null);
        PetRoamBaseOffset.X = 0;
        PetRoamBaseOffset.Y = 0;
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
        _isEdgeRoaming = true;
        _roamStopwatch.Restart();
        UpdateRoamVisual();
        _roamTimer.Start();
        AppLogger.Info(
            $"自动绕屏开始：{GetRoamModeName(_roamMode)}，" +
            $"方向：{(_roamClockwise ? "顺时针" : "逆时针")}，" +
            $"目标 {_roamBoundaryTargetDistance:F0} DIP，" +
            $"显示器工作区 {_roamWorkArea.Left:F0},{_roamWorkArea.Top:F0}," +
            $"{_roamWorkArea.Width:F0}×{_roamWorkArea.Height:F0}");
        return true;
    }

    private void StopEdgeRoaming(
        string reason,
        bool restartAutomaticCountdown,
        bool scheduleNextRoam = false,
        bool restoreIdleFrame = true)
    {
        if (!_isEdgeRoaming)
        {
            return;
        }

        _roamTimer.Stop();
        _roamStopwatch.Stop();
        _isEdgeRoaming = false;
        if (restoreIdleFrame)
        {
            BakeCurrentPetVisualTransformIntoDisplayFrame();
        }

        _roamApproaching = false;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        _roamCornerTargetEdge = EdgeDock.None;
        _isRoamCornerTurning = false;
        _roamVisualEdge = EdgeDock.None;
        _roamVisualDirection = RoamVisualDirection.None;
        _roamVisualPhaseStartedAt = TimeSpan.Zero;
        _isRoamBaseOffsetTransitioning = false;
        _roamBaseOffsetTransitionStartedAt = TimeSpan.Zero;
        _roamBaseOffsetTransitionStart = new Point(0, 0);
        _roamBaseOffsetTransitionTarget = new Point(0, 0);
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
        if (restoreIdleFrame && _activeClip is null && _edgeDock == EdgeDock.None)
        {
            _nextFrameBlendDuration = RoamVisualTransitionDuration;
            ShowStableFrame(_idleFrame);
        }

        AppLogger.Info(
            $"自动绕屏结束：{reason}，进度 " +
            $"{Math.Min(_roamBoundaryTravelled, _roamBoundaryTargetDistance):F0}/" +
            $"{_roamBoundaryTargetDistance:F0} DIP");
        if (scheduleNextRoam)
        {
            ScheduleNextRoam();
        }

        _roamBoundaryTravelled = 0;
        _roamBoundaryTargetDistance = 0;
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
            if (_isRoamCornerTurning)
            {
                AdvanceRoamCornerTurn(delta);
            }
            else
            {
                AdvanceRoamAlongBoundary(distance);
            }
        }

        if (_isEdgeRoaming)
        {
            UpdateRoamVisual();
        }
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
            _roamBoundaryStart = _roamApproachTarget;
            _roamBoundaryTravelled = 0;
            return;
        }

        Left = SnapDipToPhysicalPixel(
            Left + deltaX / remaining * distance,
            horizontal: true);
        Top = SnapDipToPhysicalPixel(
            Top + deltaY / remaining * distance,
            horizontal: false);
    }

    private double AdvanceRoamAlongBoundary(double distance)
    {
        if (distance <= 0.001)
        {
            return 0;
        }

        var lapRemaining = _roamBoundaryTargetDistance - _roamBoundaryTravelled;
        if (lapRemaining <= 0.01)
        {
            CompleteRoamLap();
            return 0;
        }

        var positiveDirection = IsPositiveRoamDirection(_roamEdge, _roamClockwise);
        var horizontal = _roamEdge is EdgeDock.Top or EdgeDock.Bottom;
        var current = horizontal ? Left : Top;
        var minimum = horizontal ? _roamWorkArea.Left : _roamWorkArea.Top;
        var maximum = horizontal
            ? _roamWorkArea.Right - ActualWidth
            : _roamWorkArea.Bottom - ActualHeight;
        var available = Math.Max(
            0,
            positiveDirection ? maximum - current : current - minimum);
        var travelled = Math.Min(Math.Min(distance, available), lapRemaining);
        if (travelled > 0)
        {
            if (horizontal)
            {
                Left = SnapDipToPhysicalPixel(
                    Left + (positiveDirection ? travelled : -travelled),
                    horizontal: true);
            }
            else
            {
                Top = SnapDipToPhysicalPixel(
                    Top + (positiveDirection ? travelled : -travelled),
                    horizontal: false);
            }

            _roamBoundaryTravelled += travelled;
        }

        if (_roamBoundaryTargetDistance - _roamBoundaryTravelled <= 0.01)
        {
            CompleteRoamLap();
            return travelled;
        }

        if (available - travelled <= 0.01)
        {
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

        return travelled;
    }

    private void CompleteRoamLap()
    {
        Left = _roamBoundaryStart.X;
        Top = _roamBoundaryStart.Y;
        _roamBoundaryTravelled = _roamBoundaryTargetDistance;
        StopEdgeRoaming(
            "完整一圈完成",
            restartAutomaticCountdown: true,
            scheduleNextRoam: true);
    }

    private static double CalculateRoamPerimeter(
        Rect workArea,
        double petWidth,
        double petHeight)
    {
        return 2 * (
            Math.Max(0, workArea.Width - petWidth) +
            Math.Max(0, workArea.Height - petHeight));
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

        var elapsed = _roamStopwatch.Elapsed;
        var horizontal = _roamApproaching
            ? Math.Abs(_roamApproachTarget.X - Left) >=
              Math.Abs(_roamApproachTarget.Y - Top)
            : _roamEdge is EdgeDock.Top or EdgeDock.Bottom;
        var movingPositive = _roamApproaching
            ? horizontal
                ? _roamApproachTarget.X >= Left
                : _roamApproachTarget.Y >= Top
            : IsPositiveRoamDirection(_roamEdge, _roamClockwise);
        var direction = horizontal
            ? RoamVisualDirection.Horizontal
            : movingPositive
                ? RoamVisualDirection.VerticalDown
                : RoamVisualDirection.VerticalUp;
        var previousDirection = _roamVisualDirection;
        var directionChanged = direction != previousDirection;
        if (directionChanged)
        {
            // Every new edge begins at its contact pose.  Reusing the global
            // stopwatch phase used to jump from an arbitrary crawl frame to an
            // unrelated climbing frame at the exact middle of a corner.
            _roamVisualDirection = direction;
            _roamVisualPhaseStartedAt = elapsed + RoamVisualTransitionDuration;
        }

        var phaseElapsed = elapsed - _roamVisualPhaseStartedAt;
        var frameIndex = (int)(Math.Max(0, phaseElapsed.TotalMilliseconds) /
                               RoamSpriteFrameInterval.TotalMilliseconds) %
                         RoamFrameCount;
        var modeIndex = (int)_roamMode;
        var frames = direction switch
        {
            RoamVisualDirection.Horizontal => _roamHorizontalFrames[modeIndex],
            RoamVisualDirection.VerticalDown => _roamVerticalDownFrames[modeIndex],
            _ => _roamVerticalUpFrames[modeIndex]
        };

        var targetFacingScaleX = horizontal
            // 横向原画朝右；朝左移动时镜像，始终让脸朝向前进方向。
            ? movingPositive ? 1 : -1
            : _roamEdge == EdgeDock.Left ? -1 : 1;
        if (directionChanged &&
            Math.Abs(PetFacingScale.ScaleX - targetFacingScaleX) > 0.001)
        {
            var previousVisualMatrix = GetPetVisualMatrix();
            PetFacingScale.ScaleX = targetFacingScaleX;
            RebaseDisplayFrameForPetTransformChange(
                previousVisualMatrix,
                GetPetVisualMatrix());
        }
        else
        {
            PetFacingScale.ScaleX = targetFacingScaleX;
        }

        PetFacingScale.ScaleY = 1;
        var visualEdge = _roamApproaching
            ? EdgeDock.None
            : _isRoamCornerTurning && _roamCornerTargetEdge != EdgeDock.None
                ? _roamCornerTargetEdge
                : _roamEdge;
        if (visualEdge != _roamVisualEdge)
        {
            AnimateRoamBaseOffset(visualEdge, elapsed);
            _roamVisualEdge = visualEdge;
        }
        UpdateRoamBaseOffsetTransition(elapsed);

        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;

        var nextFrame = frames[frameIndex];
        _nextFrameBlendDuration = directionChanged
            ? RoamVisualTransitionDuration
            : RoamFrameBlendDuration;
        ShowStableFrame(nextFrame);
    }

    private void AnimateRoamBaseOffset(EdgeDock edge, TimeSpan elapsed)
    {
        var target = edge switch
        {
            EdgeDock.Top => new Point(0, -GetTopRoamOffset(_roamMode)),
            EdgeDock.Bottom => new Point(0, 10),
            EdgeDock.Left => new Point(-35, 0),
            EdgeDock.Right => new Point(35, 0),
            _ => new Point(0, 0)
        };
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _roamBaseOffsetTransitionStart = new Point(
            PetRoamBaseOffset.X,
            PetRoamBaseOffset.Y);
        _roamBaseOffsetTransitionTarget = new Point(
            SnapDipToPhysicalPixel(target.X, horizontal: true),
            SnapDipToPhysicalPixel(target.Y, horizontal: false));
        _roamBaseOffsetTransitionStartedAt = elapsed;
        _isRoamBaseOffsetTransitioning =
            Math.Abs(_roamBaseOffsetTransitionStart.X -
                     _roamBaseOffsetTransitionTarget.X) > 0.1 ||
            Math.Abs(_roamBaseOffsetTransitionStart.Y -
                     _roamBaseOffsetTransitionTarget.Y) > 0.1;
        if (!_isRoamBaseOffsetTransitioning)
        {
            PetRoamBaseOffset.X = _roamBaseOffsetTransitionTarget.X;
            PetRoamBaseOffset.Y = _roamBaseOffsetTransitionTarget.Y;
        }
    }

    private void UpdateRoamBaseOffsetTransition(TimeSpan elapsed)
    {
        if (!_isRoamBaseOffsetTransitioning)
        {
            return;
        }

        var progress = Math.Clamp(
            (elapsed - _roamBaseOffsetTransitionStartedAt).TotalMilliseconds /
            RoamCornerTurnDuration.TotalMilliseconds,
            0,
            1);
        var easedProgress = progress * progress * (3 - 2 * progress);
        PetRoamBaseOffset.X = SnapDipToPhysicalPixel(
            _roamBaseOffsetTransitionStart.X +
            (_roamBaseOffsetTransitionTarget.X -
             _roamBaseOffsetTransitionStart.X) * easedProgress,
            horizontal: true);
        PetRoamBaseOffset.Y = SnapDipToPhysicalPixel(
            _roamBaseOffsetTransitionStart.Y +
            (_roamBaseOffsetTransitionTarget.Y -
             _roamBaseOffsetTransitionStart.Y) * easedProgress,
            horizontal: false);
        if (progress >= 1)
        {
            _isRoamBaseOffsetTransitioning = false;
            PetRoamBaseOffset.X = _roamBaseOffsetTransitionTarget.X;
            PetRoamBaseOffset.Y = _roamBaseOffsetTransitionTarget.Y;
        }
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
        var position = edge switch
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
        return new Point(
            SnapDipToPhysicalPixel(position.X, horizontal: true),
            SnapDipToPhysicalPixel(position.Y, horizontal: false));
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
            RoamMode.Wriggle => 94,
            RoamMode.Crawl => 126,
            RoamMode.Hop => 164,
            _ => 126
        };
    }

    private static double GetTopRoamOffset(RoamMode mode)
    {
        return mode switch
        {
            RoamMode.Wriggle => 118,
            RoamMode.Crawl => 97,
            RoamMode.Hop => 56,
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
            _bubbleMode == BubbleMode.Todo ||
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
        if (_edgeRoamingEnabled && DateTimeOffset.UtcNow >= _nextRoamDueUtc)
        {
            if (StartEdgeRoaming())
            {
                return;
            }
        }

        var activity = GetNextAutomaticActivity();
        if (activity is null)
        {
            StartPillowBreathing();
        }
        else if (!TryStartReaction(activity, showCuteBubble: false))
        {
            RestartAutomaticCountdown();
            return;
        }
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

        var interval = AutomaticAnimationInterval;
        if (_edgeRoamingEnabled)
        {
            var untilRoam = _nextRoamDueUtc - DateTimeOffset.UtcNow;
            if (untilRoam <= TimeSpan.Zero)
            {
                interval = TimeSpan.FromMilliseconds(100);
            }
            else if (untilRoam < interval)
            {
                interval = untilRoam;
            }
        }

        _automaticTimer.Interval = interval;
        _automaticTimer.Start();
    }

    private AnimationClip? GetNextAutomaticActivity()
    {
        if (_automaticActivityBag.Count == 0)
        {
            var indices = Enumerable.Range(0, _automaticActivities.Length).ToArray();
            for (var index = indices.Length - 1; index > 0; index--)
            {
                var swapIndex = _random.Next(index + 1);
                (indices[index], indices[swapIndex]) = (indices[swapIndex], indices[index]);
            }

            if (indices.Length > 1 && indices[0] == _lastAutomaticActivityIndex)
            {
                (indices[0], indices[1]) = (indices[1], indices[0]);
            }

            foreach (var index in indices)
            {
                _automaticActivityBag.Enqueue(index);
            }
        }

        var selectedIndex = _automaticActivityBag.Dequeue();
        _lastAutomaticActivityIndex = selectedIndex;
        return _automaticActivities[selectedIndex];
    }

    private TimeSpan GetRandomRoamScheduleDelay()
    {
        var range = MaximumRoamScheduleDelay - MinimumRoamScheduleDelay;
        return MinimumRoamScheduleDelay +
               TimeSpan.FromMilliseconds(_random.NextDouble() * range.TotalMilliseconds);
    }

    private void ScheduleNextRoam()
    {
        var delay = GetRandomRoamScheduleDelay();
        _nextRoamDueUtc = DateTimeOffset.UtcNow + delay;
        AppLogger.Info($"下一次自动绕屏将在 {delay.TotalMinutes:F1} 分钟内开始");
    }

    private void StartPillowBreathing()
    {
        StopPillowBreathing();
        _isPillowBreathing = true;
        var generation = _pillowAnimationGeneration;
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

        // 新待机图本身就是趴在枕头上打呼噜。这里保留动作时长，但不再缩放
        // 整张位图，避免半透明像素被逐帧重采样后出现亮纹波动。
        var scaleX = CreatePillowBreathingAnimation(1, easing);
        var scaleY = CreatePillowBreathingAnimation(1, easing);
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
        if (_bubbleMode == BubbleMode.Todo &&
            !ReferenceEquals(_activeClip, _todoEnterClip))
        {
            ShowStableTodoFrame();
            return;
        }

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

        ShowStableFrame(clip.Frames[^1].Image);
        if (!ReferenceEquals(_activeClip, clip))
        {
            return;
        }

        _activeClip = null;
        _activeFrameIndex = -1;
        AppLogger.Info($"动作完成：{clip.ActionName}");
        ScheduleUnusedSpritePageCollection($"动作完成/{clip.ActionName}");
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

        var holdDuration = frame.HoldDuration;
        if (_nextFrameMinimumHold > holdDuration)
        {
            holdDuration = _nextFrameMinimumHold;
        }

        _nextFrameMinimumHold = TimeSpan.Zero;
        _frameTimer.Interval = holdDuration;
        _frameTimer.Start();
    }

    private void ShowStableFrame(SpriteFrame frame)
    {
        if (_currentSpriteFrame is SpriteFrame currentFrame && currentFrame == frame)
        {
            _nextFrameBlendDuration = null;
            return;
        }

        if (!_spritePages.TryGetValue(frame.PageName, out var page))
        {
            throw new KeyNotFoundException($"找不到精灵图集分页：{frame.PageName}");
        }

        if (!string.Equals(
                _loadedSpritePageName,
                frame.PageName,
                StringComparison.Ordinal))
        {
            LoadSpritePageIntoBuffer(frame.PageName, page);
        }

        var requestedBlendDuration = _nextFrameBlendDuration ?? FrameBlendDuration;
        _nextFrameBlendDuration = null;
        UpdateFrameBlend(Stopwatch.GetTimestamp(), force: true);
        CopyFramePixels(frame, _frameBlendTargetPixels);
        if (_currentSpriteFrame is not null &&
            requestedBlendDuration > TimeSpan.Zero &&
            IsLoaded &&
            PresentationSource.FromVisual(this) is not null)
        {
            Array.Copy(
                _displayFramePixels,
                _frameBlendFromPixels,
                _displayFramePixels.Length);
            _activeFrameBlendDuration = requestedBlendDuration;
            _frameBlendStartedTimestamp = Stopwatch.GetTimestamp();
            _isFrameBlending = true;
            CompositionTarget.Rendering -= FrameBlend_Rendering;
            CompositionTarget.Rendering += FrameBlend_Rendering;
        }
        else
        {
            StopFrameBlend(snapToTarget: false);
            WriteDisplayFrame(_frameBlendTargetPixels);
        }

        _currentSpriteFrame = frame;
    }

    private void CopyFramePixels(SpriteFrame frame, byte[] destination)
    {
        Array.Clear(destination);
        var visibleLeft = Math.Max(0, frame.DestinationX);
        var visibleTop = Math.Max(0, frame.DestinationY);
        var visibleRight = Math.Min(
            DisplayPixelWidth,
            frame.DestinationX + frame.Width);
        var visibleBottom = Math.Min(
            DisplayPixelHeight,
            frame.DestinationY + frame.Height);
        var visibleWidth = visibleRight - visibleLeft;
        var visibleHeight = visibleBottom - visibleTop;
        if (visibleWidth <= 0 || visibleHeight <= 0)
        {
            return;
        }

        var stride = checked(DisplayPixelWidth * 4);
        var sourceBounds = new Int32Rect(
            frame.X + visibleLeft - frame.DestinationX,
            frame.Y + visibleTop - frame.DestinationY,
            visibleWidth,
            visibleHeight);
        _spritePageBuffer.CopyPixels(
            sourceBounds,
            destination,
            stride,
            checked(visibleTop * stride + visibleLeft * 4));
    }

    private void FrameBlend_Rendering(object? sender, EventArgs e)
    {
        UpdateFrameBlend(Stopwatch.GetTimestamp(), force: false);
    }

    private void UpdateFrameBlend(long timestamp, bool force)
    {
        if (!_isFrameBlending)
        {
            return;
        }

        var elapsedTicks = Math.Max(0, timestamp - _frameBlendStartedTimestamp);
        var durationTicks = Math.Max(
            1,
            _activeFrameBlendDuration.TotalSeconds * Stopwatch.Frequency);
        var progress = Math.Clamp(elapsedTicks / durationTicks, 0, 1);
        var easedProgress = progress * progress * (3 - 2 * progress);
        BlendPremultipliedPixels(
            _frameBlendFromPixels,
            _frameBlendTargetPixels,
            _frameBlendOutputPixels,
            easedProgress);

        WriteDisplayFrame(_frameBlendOutputPixels);
        if (progress >= 1)
        {
            StopFrameBlend(snapToTarget: true);
        }
    }

    private static void BlendPremultipliedPixels(
        byte[] fromPixels,
        byte[] toPixels,
        byte[] outputPixels,
        double progress)
    {
        if (fromPixels.Length != toPixels.Length ||
            outputPixels.Length != fromPixels.Length)
        {
            throw new ArgumentException("帧淡化缓冲区尺寸必须一致。");
        }

        var clampedProgress = Math.Clamp(progress, 0, 1);
        for (var index = 0; index < outputPixels.Length; index++)
        {
            var from = fromPixels[index];
            var to = toPixels[index];
            outputPixels[index] = (byte)Math.Clamp(
                Math.Round(from + (to - from) * clampedProgress),
                byte.MinValue,
                byte.MaxValue);
        }
    }

    private Matrix GetPetVisualMatrix()
    {
        var transform = PetVisual.RenderTransform.Value;
        var origin = new Point(
            PetVisual.RenderTransformOrigin.X * DisplayPixelWidth,
            PetVisual.RenderTransformOrigin.Y * DisplayPixelHeight);
        var visualMatrix = Matrix.Identity;
        visualMatrix.Translate(-origin.X, -origin.Y);
        visualMatrix.Append(transform);
        visualMatrix.Translate(origin.X, origin.Y);
        return visualMatrix;
    }

    private void RebaseDisplayFrameForPetTransformChange(
        Matrix previousVisualMatrix,
        Matrix nextVisualMatrix)
    {
        if (previousVisualMatrix == nextVisualMatrix ||
            !IsFiniteInvertibleMatrix(nextVisualMatrix))
        {
            return;
        }

        UpdateFrameBlend(Stopwatch.GetTimestamp(), force: true);
        StopFrameBlend(snapToTarget: false);
        var inverseNextMatrix = nextVisualMatrix;
        inverseNextMatrix.Invert();
        var relativeMatrix = previousVisualMatrix;
        relativeMatrix.Append(inverseNextMatrix);
        TransformPremultipliedPixels(
            _displayFramePixels,
            _transformedDisplayFramePixels,
            DisplayPixelWidth,
            DisplayPixelHeight,
            relativeMatrix);
        WriteDisplayFrame(_transformedDisplayFramePixels);
    }

    private void BakeCurrentPetVisualTransformIntoDisplayFrame()
    {
        var visualMatrix = GetPetVisualMatrix();
        var opacity = double.IsFinite(PetVisual.Opacity)
            ? Math.Clamp(PetVisual.Opacity, 0, 1)
            : 1;
        if (visualMatrix.IsIdentity && opacity >= 1)
        {
            return;
        }

        // The frame buffer stores the untransformed sprite while roaming and
        // edge-peek mirroring are WPF transforms. Materialize the exact current
        // blend first, then bake those transforms into one Pbgra frame before
        // clearing them. The following Todo transition therefore starts from
        // what the user actually saw instead of briefly flipping or jumping.
        UpdateFrameBlend(Stopwatch.GetTimestamp(), force: true);
        StopFrameBlend(snapToTarget: false);

        if (visualMatrix.IsIdentity)
        {
            Array.Copy(
                _displayFramePixels,
                _transformedDisplayFramePixels,
                _displayFramePixels.Length);
        }
        else
        {
            TransformPremultipliedPixels(
                _displayFramePixels,
                _transformedDisplayFramePixels,
                DisplayPixelWidth,
                DisplayPixelHeight,
                visualMatrix);
        }

        if (opacity < 1)
        {
            for (var index = 0; index < _transformedDisplayFramePixels.Length; index++)
            {
                _transformedDisplayFramePixels[index] = (byte)Math.Clamp(
                    Math.Round(_transformedDisplayFramePixels[index] * opacity),
                    byte.MinValue,
                    byte.MaxValue);
            }
        }

        WriteDisplayFrame(_transformedDisplayFramePixels);
    }

    private static void TransformPremultipliedPixels(
        byte[] sourcePixels,
        byte[] outputPixels,
        int width,
        int height,
        Matrix visualMatrix)
    {
        var expectedLength = checked(width * height * 4);
        if (sourcePixels.Length != expectedLength || outputPixels.Length != expectedLength)
        {
            throw new ArgumentException("变换帧缓冲区尺寸必须与完整帧一致。");
        }

        if (!IsFiniteInvertibleMatrix(visualMatrix))
        {
            Array.Copy(sourcePixels, outputPixels, expectedLength);
            return;
        }

        var inverse = visualMatrix;
        inverse.Invert();
        Array.Clear(outputPixels);
        for (var destinationY = 0; destinationY < height; destinationY++)
        {
            for (var destinationX = 0; destinationX < width; destinationX++)
            {
                var sourcePoint = inverse.Transform(new Point(
                    destinationX + 0.5,
                    destinationY + 0.5));
                var sourceX = sourcePoint.X - 0.5;
                var sourceY = sourcePoint.Y - 0.5;
                if (sourceX < -1 || sourceX > width ||
                    sourceY < -1 || sourceY > height)
                {
                    continue;
                }

                var x0 = (int)Math.Floor(sourceX);
                var y0 = (int)Math.Floor(sourceY);
                var xWeight = sourceX - x0;
                var yWeight = sourceY - y0;
                var destinationOffset = (destinationY * width + destinationX) * 4;
                var alpha = SamplePremultipliedChannel(
                    sourcePixels,
                    width,
                    height,
                    x0,
                    y0,
                    xWeight,
                    yWeight,
                    channel: 3);
                outputPixels[destinationOffset + 3] = alpha;
                for (var channel = 0; channel < 3; channel++)
                {
                    outputPixels[destinationOffset + channel] = Math.Min(
                        alpha,
                        SamplePremultipliedChannel(
                            sourcePixels,
                            width,
                            height,
                            x0,
                            y0,
                            xWeight,
                            yWeight,
                            channel));
                }
            }
        }
    }

    private static bool IsFiniteInvertibleMatrix(Matrix matrix)
    {
        return double.IsFinite(matrix.M11) &&
               double.IsFinite(matrix.M12) &&
               double.IsFinite(matrix.M21) &&
               double.IsFinite(matrix.M22) &&
               double.IsFinite(matrix.OffsetX) &&
               double.IsFinite(matrix.OffsetY) &&
               matrix.HasInverse;
    }

    private static byte SamplePremultipliedChannel(
        byte[] pixels,
        int width,
        int height,
        int x0,
        int y0,
        double xWeight,
        double yWeight,
        int channel)
    {
        static byte ReadPixel(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int x,
            int y,
            int sourceChannel)
        {
            return x >= 0 && x < sourceWidth && y >= 0 && y < sourceHeight
                ? source[(y * sourceWidth + x) * 4 + sourceChannel]
                : (byte)0;
        }

        var topLeft = ReadPixel(pixels, width, height, x0, y0, channel);
        var topRight = ReadPixel(pixels, width, height, x0 + 1, y0, channel);
        var bottomLeft = ReadPixel(pixels, width, height, x0, y0 + 1, channel);
        var bottomRight = ReadPixel(pixels, width, height, x0 + 1, y0 + 1, channel);
        var top = topLeft + (topRight - topLeft) * xWeight;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * xWeight;
        return (byte)Math.Clamp(
            Math.Round(top + (bottom - top) * yWeight),
            byte.MinValue,
            byte.MaxValue);
    }

    private void WriteDisplayFrame(byte[] pixels)
    {
        var stride = checked(DisplayPixelWidth * 4);
        _displayFrameBuffer.WritePixels(
            new Int32Rect(0, 0, DisplayPixelWidth, DisplayPixelHeight),
            pixels,
            stride,
            0);
        if (!ReferenceEquals(pixels, _displayFramePixels))
        {
            Array.Copy(pixels, _displayFramePixels, _displayFramePixels.Length);
        }
    }

    private void StopFrameBlend(bool snapToTarget)
    {
        if (_isFrameBlending && snapToTarget)
        {
            WriteDisplayFrame(_frameBlendTargetPixels);
        }

        _isFrameBlending = false;
        _frameBlendStartedTimestamp = 0;
        CompositionTarget.Rendering -= FrameBlend_Rendering;
    }

    private void LoadSpritePageIntoBuffer(string pageName, SpriteAtlasPage page)
    {
        var loadStopwatch = Stopwatch.StartNew();
        var bitmap = LoadPackagedBitmap(page.ResourcePath);
        if (bitmap.PixelWidth != page.Width || bitmap.PixelHeight != page.Height)
        {
            throw new InvalidOperationException(
                $"精灵图集分页尺寸不匹配：{pageName}");
        }

        BitmapSource premultipliedBitmap = bitmap;
        if (bitmap.Format != PixelFormats.Pbgra32)
        {
            premultipliedBitmap = new FormatConvertedBitmap(
                bitmap,
                PixelFormats.Pbgra32,
                null,
                0);
            premultipliedBitmap.Freeze();
        }

        var stride = checked(page.Width * 4);
        var byteCount = checked(stride * page.Height);
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var pageBounds = new Int32Rect(0, 0, page.Width, page.Height);
            premultipliedBitmap.CopyPixels(pageBounds, pixels, stride, 0);
            _spritePageBuffer.WritePixels(pageBounds, pixels, stride, 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels, clearArray: false);
        }

        _loadedSpritePageName = pageName;
        loadStopwatch.Stop();
        AppLogger.Info(
            $"精灵分页已写入复用缓冲区：{pageName}，" +
            $"{page.Width}x{page.Height}，{loadStopwatch.Elapsed.TotalMilliseconds:F1} ms");
    }

    private void SetBubbleMode(BubbleMode mode)
    {
        if (_bubbleMode == mode)
        {
            return;
        }

        var previousMode = _bubbleMode;
        if (mode == BubbleMode.Todo)
        {
            EnterTodoVisualState();
        }

        HideBubbleVisuals();
        _bubbleMode = mode;
        ShowBubbleVisuals(mode);
        AppLogger.Info($"气泡状态：{previousMode} -> {mode}");

        if (mode == BubbleMode.Todo)
        {
            _automaticTimer.Stop();
        }
        else if (previousMode == BubbleMode.Todo)
        {
            StartTodoExitTransition();
        }
    }

    private void EnterTodoVisualState()
    {
        _automaticTimer.Stop();
        BakeCurrentPetVisualTransformIntoDisplayFrame();
        StopPillowBreathing();

        var enterStartIndex = 0;
        if (ReferenceEquals(_activeClip, _todoExitClip) && _activeFrameIndex >= 0)
        {
            enterStartIndex = Math.Clamp(
                _todoEnterClip.Frames.Length - 1 - _activeFrameIndex,
                0,
                _todoEnterClip.Frames.Length - 1);
        }
        else
        {
            enterStartIndex = GetTodoEnterStartIndex(_currentSpriteFrame);
        }

        if (_activeClip is { } activeClip)
        {
            _frameTimer.Stop();
            _activeClip = null;
            _activeFrameIndex = -1;
            AppLogger.Info(
                $"动作中止：{activeClip.ActionName}，原因：打开待办");
        }
        else
        {
            _frameTimer.Stop();
            _activeFrameIndex = -1;
        }

        ExitEdgePeek(
            restartAutomaticCountdown: false,
            restoreIdleFrame: false);
        StopEdgeRoaming(
            "打开待办",
            restartAutomaticCountdown: false,
            scheduleNextRoam: true,
            restoreIdleFrame: false);
        ResetPetVisualTransforms();
        _frameTimer.Stop();
        _activeClip = _todoEnterClip;
        _activeFrameIndex = enterStartIndex - 1;
        _nextFrameBlendDuration = TodoStateBlendDuration;
        _nextFrameMinimumHold = TodoStateBlendDuration;
        AppLogger.Info("待办打开过渡开始");
        ShowActiveClipFrame(enterStartIndex);
    }

    private static int GetTodoEnterStartIndex(SpriteFrame? frame)
    {
        if (frame is not { } currentFrame)
        {
            return 0;
        }

        if (currentFrame.Name.EndsWith(
                "luban-idle.png",
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        const string wakePrefix = "luban-wake-";
        var fileName = Path.GetFileNameWithoutExtension(currentFrame.Name);
        if (fileName.StartsWith(wakePrefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(fileName.AsSpan(wakePrefix.Length), out var wakeFrameNumber))
        {
            return Math.Clamp(wakeFrameNumber, 1, WakeFrameCount);
        }

        // 普通动作、跑步、绕屏和边缘探头都已是离开枕头的姿势；从 wake14
        // 接入思考动作可避免右键时先闪回趴枕头状态。
        return WakeFrameCount;
    }

    private void StartTodoExitTransition()
    {
        if (_isClosing)
        {
            return;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        var exitStartIndex = 0;
        if (ReferenceEquals(_activeClip, _todoEnterClip) && _activeFrameIndex >= 0)
        {
            exitStartIndex = Math.Clamp(
                _todoExitClip.Frames.Length - 1 - _activeFrameIndex,
                0,
                _todoExitClip.Frames.Length - 1);
        }

        ExitEdgePeek(
            restartAutomaticCountdown: false,
            restoreIdleFrame: false);
        StopEdgeRoaming(
            "收起待办",
            restartAutomaticCountdown: false,
            restoreIdleFrame: false);
        ResetPetVisualTransforms();
        _frameTimer.Stop();
        _activeClip = _todoExitClip;
        _activeFrameIndex = exitStartIndex - 1;
        _nextFrameBlendDuration = TodoStateBlendDuration;
        _nextFrameMinimumHold = TodoStateBlendDuration;
        AppLogger.Info("待办收起过渡开始");
        ShowActiveClipFrame(exitStartIndex);
    }

    private void ShowStableTodoFrame()
    {
        _frameTimer.Stop();
        _activeClip = null;
        _activeFrameIndex = -1;
        ShowStableFrame(_todoFrame);
    }

    private void ResetPetVisualTransforms()
    {
        PetFacingScale.ScaleX = 1;
        PetFacingScale.ScaleY = 1;
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PetScale.ScaleX = 1;
        PetScale.ScaleY = 1;
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
        _isRoamBaseOffsetTransitioning = false;
        _roamBaseOffsetTransitionStartedAt = TimeSpan.Zero;
        _roamBaseOffsetTransitionStart = new Point(0, 0);
        _roamBaseOffsetTransitionTarget = new Point(0, 0);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PetRoamBaseOffset.BeginAnimation(TranslateTransform.YProperty, null);
        PetRoamBaseOffset.X = 0;
        PetRoamBaseOffset.Y = 0;
        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;
    }

    private void HideBubbleVisuals()
    {
        _outsideTodoCloseGeneration++;
        BubblePopup.IsOpen = false;
        BubbleHost.Visibility = Visibility.Collapsed;
        BubbleTailHost.Visibility = Visibility.Collapsed;
        CuteBubble.Visibility = Visibility.Collapsed;
        if (_todoWindow.IsVisible)
        {
            _suppressTodoWindowDeactivate = true;
            try
            {
                _todoWindow.Hide();
            }
            finally
            {
                _suppressTodoWindowDeactivate = false;
            }
        }
    }

    private void ShowBubbleVisuals(BubbleMode mode)
    {
        if (mode == BubbleMode.None)
        {
            return;
        }

        if (mode == BubbleMode.Todo)
        {
            _todoWindow.SetAutoRoam(_edgeRoamingEnabled);
            _todoWindow.SetPetSizeScale(_petSizeScale);
            UpdateTodoWindowPosition();
            _todoWindow.Opacity = 0;
            if (!_todoWindow.IsVisible)
            {
                _todoWindow.Show();
            }

            UpdateTodoWindowPosition();
            _todoWindow.Opacity = 1;
            return;
        }

        var displayedPetHeight = PetHost.ActualHeight > 0
            ? PetHost.ActualHeight
            : PetHost.Height;
        BubblePopup.VerticalOffset = displayedPetHeight - CuteBubbleHeight;
        BubbleHost.Visibility = Visibility.Visible;
        BubbleTailHost.Visibility = Visibility.Visible;
        CuteBubble.Visibility = Visibility.Visible;
        BubblePopup.IsOpen = true;
    }

    private void TodoWindow_AutoRoamChanged(bool enabled)
    {
        ApplyAutoRoamSetting(enabled);
    }

    private void TodoWindow_PetSizeScaleChanged(double scale)
    {
        ApplyPetSizeScale(scale, persist: true, preservePosition: true);
    }

    private static double NormalizePetSizeScale(double scale)
    {
        if (!double.IsFinite(scale))
        {
            return 1;
        }

        var clamped = Math.Clamp(scale, MinimumPetSizeScale, MaximumPetSizeScale);
        return Math.Round(clamped * 20) / 20;
    }

    private void ApplyPetSizeScale(
        double scale,
        bool persist,
        bool preservePosition)
    {
        var normalizedScale = NormalizePetSizeScale(scale);
        var previousWidth = double.IsFinite(Width) && Width > 0 ? Width : ActualWidth;
        var previousHeight = double.IsFinite(Height) && Height > 0 ? Height : ActualHeight;
        var previousLeft = Left;
        var previousTop = Top;
        Rect workArea = Rect.Empty;
        var preserveLeftEdge = false;
        var preserveRightEdge = false;
        var preserveTopEdge = false;
        var preserveBottomEdge = false;
        if (preservePosition && IsLoaded)
        {
            workArea = MonitorWorkArea.GetForWindow(this);
            preserveLeftEdge = Math.Abs(previousLeft - workArea.Left) <= 1;
            preserveRightEdge = Math.Abs(
                previousLeft + previousWidth - workArea.Right) <= 1;
            preserveTopEdge = Math.Abs(previousTop - workArea.Top) <= 1;
            preserveBottomEdge = Math.Abs(
                previousTop + previousHeight - workArea.Bottom) <= 1;
        }

        var displayedWidth = PetWidth * normalizedScale;
        var displayedHeight = PetHeight * normalizedScale;
        _petSizeScale = normalizedScale;
        Width = displayedWidth;
        Height = displayedHeight;
        PetColumn.Width = new GridLength(displayedWidth);
        PetHost.Width = displayedWidth;
        PetHost.Height = displayedHeight;
        PetSizeViewbox.Width = displayedWidth;
        PetSizeViewbox.Height = displayedHeight;
        _todoWindow.SetPetSizeScale(normalizedScale);

        if (preservePosition && IsLoaded && workArea != Rect.Empty)
        {
            var desiredLeft = preserveLeftEdge
                ? workArea.Left
                : preserveRightEdge
                    ? workArea.Right - displayedWidth
                    : previousLeft + previousWidth / 2 - displayedWidth / 2;
            var desiredTop = preserveTopEdge
                ? workArea.Top
                : preserveBottomEdge
                    ? workArea.Bottom - displayedHeight
                    : previousTop + previousHeight - displayedHeight;
            Left = SnapDipToPhysicalPixel(
                Math.Clamp(
                    desiredLeft,
                    workArea.Left,
                    Math.Max(workArea.Left, workArea.Right - displayedWidth)),
                horizontal: true);
            Top = SnapDipToPhysicalPixel(
                Math.Clamp(
                    desiredTop,
                    workArea.Top,
                    Math.Max(workArea.Top, workArea.Bottom - displayedHeight)),
                horizontal: false);
        }

        if (_todoWindow.IsVisible)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(UpdateTodoWindowPosition));
        }

        if (persist)
        {
            SaveSettings();
        }

        AppLogger.Info(
            $"桌宠大小：{normalizedScale:P0}，" +
            $"显示尺寸 {displayedWidth:F0}×{displayedHeight:F0} DIP");
    }

    private void ApplyAutoRoamSetting(bool enabled)
    {
        if (_edgeRoamingEnabled == enabled)
        {
            return;
        }

        _edgeRoamingEnabled = enabled;
        _todoWindow.SetAutoRoam(enabled);

        if (!enabled)
        {
            StopEdgeRoaming("已通过右键开关关闭", restartAutomaticCountdown: false);
        }
        else
        {
            _nextRoamDueUtc = DateTimeOffset.UtcNow;
            StopPillowBreathing();
            if (_activeClip is { } activeClip)
            {
                _frameTimer.Stop();
                _activeClip = null;
                _activeFrameIndex = -1;
                ShowStableFrame(activeClip.Frames[^1].Image);
                AppLogger.Info($"动作中止：{activeClip.ActionName}，原因：立即开始自动绕屏");
            }
        }

        var saved = SaveSettings();
        AppLogger.Info($"自动绕屏开关：{enabled}，设置保存：{saved}");

        if (!enabled)
        {
            RestartAutomaticCountdown();
            return;
        }

        // 勾选本身发生在待办气泡中；先安全收起输入界面，再在当前事件完成后
        // 立即开始完整一圈。若用户正手动停靠，截止时间保持“已到期”，拖离后补做。
        SetBubbleMode(BubbleMode.None);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!StartEdgeRoaming())
                {
                    RestartAutomaticCountdown();
                }
            }));
    }

    private bool SaveSettings()
    {
        return _settingsStore.Save(new AppSettings
        {
            EdgeRoamingEnabled = _edgeRoamingEnabled,
            PetSizeScale = _petSizeScale
        });
    }

    private void TodoWindow_AddRequested(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _todos.Add(new TodoItem { Text = text.Trim() });
        SaveTodos();
        AppLogger.Info($"新增待办，当前数量：{_todos.Count}");
    }

    private void TodoWindow_TodoChanged(TodoItem item)
    {
        SaveTodos();
        AppLogger.Info($"待办完成状态已更新，已完成：{item.IsCompleted}");
    }

    private void TodoWindow_DeleteRequested(TodoItem item)
    {
        if (_todos.Remove(item))
        {
            SaveTodos();
            AppLogger.Info($"删除待办，当前数量：{_todos.Count}");
        }
    }

    private void TodoWindow_CloseRequested(object? sender, EventArgs e)
    {
        SetBubbleMode(BubbleMode.None);
    }

    private void TodoWindow_ExitRequested(object? sender, EventArgs e)
    {
        _todoWindow.AllowApplicationClose();
        Application.Current.Shutdown();
    }

    private void TodoWindow_ImeCompositionChanged(bool composing)
    {
        AppLogger.Info($"微软 TSF 输入组合状态：{(composing ? "开始或更新" : "完成")}");
        if (!composing)
        {
            ScheduleOutsideTodoClose();
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

    private enum RoamVisualDirection
    {
        None,
        Horizontal,
        VerticalUp,
        VerticalDown
    }

    private sealed record AnimationClip(
        string Message,
        string ActionName,
        AnimationFrame[] Frames,
        int ActionFrameIndex);

    private sealed record SpriteAtlasManifest(
        int Version,
        int DisplayWidth,
        int DisplayHeight,
        int SourceFrameCount,
        int PageFrameCount,
        Dictionary<string, SpriteAtlasPageManifest> Pages);

    private sealed record SpriteAtlasPageManifest(
        string Resource,
        int Width,
        int Height,
        int LogicalFrameCount,
        int UniqueSpriteCount,
        Dictionary<string, SpriteAtlasFrameManifest> Frames);

    private sealed record SpriteAtlasFrameManifest(
        int X,
        int Y,
        int Width,
        int Height,
        int DestinationX,
        int DestinationY);

    private readonly record struct SpriteFrame(
        int X,
        int Y,
        int Width,
        int Height,
        int DestinationX,
        int DestinationY,
        string PageName,
        string Name);

    private sealed record SpriteAtlasPage(
        string ResourcePath,
        int Width,
        int Height,
        IReadOnlyDictionary<string, SpriteFrame> Frames);

    private sealed record AnimationFrame(
        SpriteFrame Image,
        TimeSpan HoldDuration,
        string Name);
}
