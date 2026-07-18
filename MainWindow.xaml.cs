using System;
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
    private const int DisplayPixelWidth = 399;
    private const int DisplayPixelHeight = 509;
    private const string SpriteAtlasManifestPath = "Assets/luban-sprite-pages.json";
    private const long SpritePageCollectionThresholdBytes = 320L * 1024L * 1024L;
    private const int WakeFrameCount = 14;
    private const int ActionPoseFrameCount = 24;
    private const int ActionLoopStartPoseNumber = 21;
    private const int ActionLoopPoseCount = 4;
    private const int ActionLoopCycleCount = 8;
    private const int EdgePeekFrameCount = 4;
    private const int WriggleRoamFrameCount = 48;
    private const int WriggleCornerFrameCount = 48;
    private const int WriggleCornerFacingSwitchFrameNumber = 43;
    private const double WriggleFrameTravelDistance = 1;
    private const double PetSizeSpringAngularFrequency = 28;
    private const double MaximumPetSizeVelocity = 4;
    private const double MaximumRoamDeltaSeconds = 0.250;
    private const double MaximumRoamSubstepSeconds = 1d / 60d;
    private static readonly TimeSpan MotionFrameInterval = TimeSpan.FromMilliseconds(85);
    private static readonly TimeSpan ActionLoopFrameInterval = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan EdgePeekFrameInterval = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan EdgePeekEndpointHold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RoamCornerTurnDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan RoamVisualTransitionDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan PetSizeTransitionDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PetSizePersistDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan FrameBlendDuration = TimeSpan.Zero;
    private static readonly TimeSpan EdgeFrameBlendDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TodoStateBlendDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan SpritePageCollectionCooldown = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumRoamScheduleDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumRoamScheduleDelay = TimeSpan.FromMinutes(20);

    private readonly IReadOnlyDictionary<string, SpriteAtlasPage> _spritePages;
    private readonly byte[] _spritePagePixels;
    private readonly byte[] _spritePageCompressedBytes;
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
    private readonly SpriteFrame[] _roamWriggleCornerFrames;
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip _todoEnterClip;
    private readonly AnimationClip _todoExitClip;
    private readonly AnimationClip?[] _automaticActivities;
    private readonly DispatcherTimer _automaticTimer;
    private readonly DispatcherTimer _petSizePersistTimer;
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
    private long _activeClipStartedTimestamp;
    private long _activeFrameDeadlineTimestamp;
    private int _nextClipIndex;
    private int _lastAutomaticActivityIndex = -1;
    private int _pillowAnimationGeneration;
    private int _outsideTodoCloseGeneration;
    private int _spritePageCleanupGeneration;
    private int _spritePageCollectionInFlight;
    private DateTimeOffset _lastSpritePageCollectionUtc = DateTimeOffset.MinValue;
    private int _edgePeekFrameIndex;
    private int _edgePeekFrameDirection = 1;
    private long _edgePeekFrameDeadlineTimestamp;
    private EdgeDock _edgeDock;
    private EdgeDock _roamEdge;
    private EdgeDock _roamVisualEdge;
    private EdgeDock _roamCornerSourceEdge;
    private EdgeDock _roamCornerTargetEdge;
    private RoamVisualDirection _roamVisualDirection;
    private RoamMode _roamMode;
    private Rect _roamWorkArea;
    private Point _roamApproachTarget;
    private Point _roamBoundaryStart;
    private TimeSpan _roamElapsed;
    private TimeSpan _roamCornerTurnElapsed;
    private TimeSpan _roamVisualPhaseStartedAt;
    private TimeSpan _roamVisualTransitionEndsAt;
    private TimeSpan _roamBaseOffsetTransitionStartedAt;
    private Point _roamBaseOffsetTransitionStart;
    private Point _roamBaseOffsetTransitionTarget;
    private double _roamBoundaryTargetDistance;
    private double _roamBoundaryTravelled;
    private double _roamLogicalLeft;
    private double _roamLogicalTop;
    private double _roamVisualTravelDistance;
    private long _roamLastRenderingTimestamp;
    private DateTimeOffset _nextRoamDueUtc;
    private bool _roamClockwise;
    private bool _roamApproaching;
    private bool _isRoamCornerTurning;
    private bool _isRoamBaseOffsetTransitioning;
    private bool _isEdgeRoaming;
    private bool _edgeRoamingEnabled;
    private double _petSizeScale = 1;
    private double _persistedPetSizeScale = 1;
    private double _petSizePreviewBaseScale = 1;
    private double _petSizeTransitionStartScale = 1;
    private double _petSizeTransitionStartVelocity;
    private double _petSizeVelocity;
    private double _petSizeTargetScale = 1;
    private long _petSizeTransitionStartedTimestamp;
    private bool _isPetSizeTransitioning;
    private bool _isPetSizePreviewSessionActive;
    private bool _petSizeEnvelopePrepared;
    private bool _isPetSizeAdjustmentActive;
    private bool _petSizeCommitPending;
    private bool _petSizeTodoPositionNeedsUpdate;
    private bool _petSizeSettingsDirty;
    private bool _isApplyingPetSizeLayout;
    private bool _todoPositionUpdateQueued;
    private PetSizeAnchor? _petSizePreviewAnchor;
    private bool? _petSizeTodoChildOnLeft;
    private bool _automaticAnimationEnabled;
    private bool _isPillowBreathing;
    private bool _isClosing;
    private bool _suppressTodoWindowDeactivate;
    private bool _displaySettingsSubscribed;
    private SpriteFrame? _currentSpriteFrame;
    private string? _loadedSpritePageName;
    private int _loadedSpritePageStride;
    private bool _isFrameBlending;
    private bool _isVisualClockSubscribed;
    private long _frameBlendStartedTimestamp;
    private TimeSpan _activeFrameBlendDuration;
    private TimeSpan? _nextFrameBlendDuration;
    private TimeSpan _nextFrameMinimumHold;

    public MainWindow()
    {
        InitializeComponent();

        _spritePages = LoadSpritePages(BuildSpriteResourcePaths());
        _spritePagePixels = new byte[_spritePages.Values.Max(page =>
            checked(page.Width * page.Height * 4))];
        _spritePageCompressedBytes = new byte[_spritePages.Values.Max(page =>
            page.CompressedByteCount)];
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
                mode == RoamMode.Wriggle
                    ? "roam-wriggle-horizontal"
                    : $"roam-{GetRoamAssetName(mode)}",
                $"luban-roam-{GetRoamAssetName(mode)}-horizontal",
                GetRoamFrameCount(mode)))
            .ToArray();
        _roamVerticalUpFrames = Enum.GetValues<RoamMode>()
            .Select(mode => LoadFrameSequence(
                mode == RoamMode.Wriggle
                    ? "roam-wriggle-vertical"
                    : $"roam-{GetRoamAssetName(mode)}",
                $"luban-roam-{GetRoamAssetName(mode)}-vertical-up",
                GetRoamFrameCount(mode)))
            .ToArray();
        _roamVerticalDownFrames = Enum.GetValues<RoamMode>()
            .Select(mode => mode == RoamMode.Wriggle
                ? _roamVerticalUpFrames[(int)mode].Reverse().ToArray()
                : LoadFrameSequence(
                    $"roam-{GetRoamAssetName(mode)}",
                    $"luban-roam-{GetRoamAssetName(mode)}-vertical-down",
                    GetRoamFrameCount(mode)))
            .ToArray();
        _roamWriggleCornerFrames = LoadFrameSequence(
            "roam-wriggle-corner",
            "luban-roam-wriggle-corner",
            WriggleCornerFrameCount);
        _reactionClips =
        [
            CreateMotionClip("刚睡醒，让我伸个懒腰～", "yawn"),
            CreateMotionClip("呜……主人要哄哄我", "cry"),
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
            _reactionClips[6]
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
        _todoWindow.PetSizeAdjustmentStarted += TodoWindow_PetSizeAdjustmentStarted;
        _todoWindow.PetSizeAdjustmentCompleted += TodoWindow_PetSizeAdjustmentCompleted;
        _todoWindow.CloseRequested += TodoWindow_CloseRequested;
        _todoWindow.ExitRequested += TodoWindow_ExitRequested;
        _todoWindow.ImeCompositionChanged += TodoWindow_ImeCompositionChanged;
        _todoWindow.Deactivated += TodoWindow_Deactivated;
        _todoWindow.LostKeyboardFocus += TodoWindow_LostKeyboardFocus;

        _petSizePersistTimer = new DispatcherTimer
        {
            Interval = PetSizePersistDelay
        };
        _petSizePersistTimer.Tick += PetSizePersistTimer_Tick;

        var settings = _settingsStore.Load();
        _edgeRoamingEnabled = settings.EdgeRoamingEnabled;
        _petSizeScale = NormalizePetSizeScale(settings.PetSizeScale);
        _persistedPetSizeScale = _petSizeScale;
        _petSizeTargetScale = _petSizeScale;
        _nextRoamDueUtc = DateTimeOffset.UtcNow + GetRandomRoamScheduleDelay();
        _todoWindow.SetAutoRoam(_edgeRoamingEnabled);
        _todoWindow.SetPetSizeScale(_petSizeScale);
        ApplyPetSizeScale(_petSizeScale, persist: false, preservePosition: false);

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
        var resourcePaths = new List<string>()
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
            var frameCount = GetRoamFrameCount(mode);
            foreach (var directionName in new[]
                     {
                         "horizontal", "vertical-up", "vertical-down"
                     })
            {
                resourcePaths.AddRange(Enumerable.Range(1, frameCount)
                    .Select(frameNumber =>
                        $"Assets/luban-roam-{modeName}-{directionName}-{frameNumber:00}.png"));
            }

            if (mode == RoamMode.Wriggle)
            {
                resourcePaths.AddRange(Enumerable.Range(1, WriggleCornerFrameCount)
                    .Select(frameNumber =>
                        $"Assets/luban-roam-wriggle-corner-{frameNumber:00}.png"));
            }
        }

        foreach (var actionName in new[]
                 {
                     "yawn", "cry", "cute",
                     "like", "eat", "wave", "think"
                 })
        {
            resourcePaths.AddRange(Enumerable.Range(1, ActionPoseFrameCount)
                .Select(frameNumber =>
                    $"Assets/luban-{actionName}-frame-{frameNumber:00}.png"));
        }

        if (resourcePaths.Distinct(StringComparer.Ordinal).Count() != resourcePaths.Count)
        {
            throw new InvalidOperationException(
                $"精灵图集资源清单包含重复项，实际 {resourcePaths.Count} 帧。");
        }

        return resourcePaths.ToArray();
    }

    private static IReadOnlyDictionary<string, SpriteAtlasPage> LoadSpritePages(
        IReadOnlyList<string> resourcePaths)
    {
        if (resourcePaths.Count == 0)
        {
            throw new ArgumentException(
                "精灵图集资源清单不能为空。",
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

        if (manifest.Version != 3 ||
            manifest.DisplayWidth != DisplayPixelWidth ||
            manifest.DisplayHeight != DisplayPixelHeight ||
            manifest.SourceFrameCount != resourcePaths.Count ||
            manifest.PageFrameCount < resourcePaths.Count ||
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
                string.IsNullOrWhiteSpace(pageDescriptor.PreviewResource) ||
                pageDescriptor.Width <= 0 || pageDescriptor.Height <= 0 ||
                pageDescriptor.UncompressedByteCount != checked(
                    pageDescriptor.Width * pageDescriptor.Height * 4) ||
                pageDescriptor.CompressedByteCount <= 0 ||
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
                    pageDescriptor.PreviewResource,
                    pageDescriptor.Width,
                    pageDescriptor.Height,
                    pageDescriptor.UncompressedByteCount,
                    pageDescriptor.CompressedByteCount,
                    new ReadOnlyDictionary<string, SpriteFrame>(frameMap)));
        }

        if (!foundResources.SetEquals(expectedResources) ||
            pageFrameCount != manifest.PageFrameCount)
        {
            throw new InvalidOperationException("精灵图集分页清单未完整覆盖源帧。");
        }

        return new ReadOnlyDictionary<string, SpriteAtlasPage>(pageMap);
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
            if (_isApplyingPetSizeLayout)
            {
                QueueTodoWindowPositionUpdate();
            }
            else
            {
                UpdateTodoWindowPosition();
            }
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

                    if (_isPetSizePreviewSessionActive)
                    {
                        var deferSettingsSave = _isPetSizeAdjustmentActive;
                        CommitPetSizePreviewSession(persist: !deferSettingsSave);
                        if (deferSettingsSave)
                        {
                            _petSizeCommitPending = true;
                        }
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

        if (OwnedWindowPositioner.TryPosition(
                PetSizeViewbox,
                _todoWindow,
                out var childIsOnLeft,
                _isPetSizePreviewSessionActive ? _petSizeTodoChildOnLeft : null))
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
        var petTopLeft = PetSizeViewbox.TranslatePoint(new Point(0, 0), this);
        var petBottomRight = PetSizeViewbox.TranslatePoint(
            new Point(PetSizeViewbox.ActualWidth, PetSizeViewbox.ActualHeight),
            this);
        var petLeft = Left + Math.Min(petTopLeft.X, petBottomRight.X);
        var petRight = Left + Math.Max(petTopLeft.X, petBottomRight.X);
        var petBottom = Top + Math.Max(petTopLeft.Y, petBottomRight.Y);
        var canPlaceOnLeft = _isPetSizePreviewSessionActive &&
                             _petSizeTodoChildOnLeft is { } lockedSide
            ? lockedSide
            : petLeft - bubbleWidth >= workArea.Left;
        var desiredLeft = canPlaceOnLeft
            ? petLeft - bubbleWidth
            : petRight;
        var desiredTop = petBottom - bubbleHeight;
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - bubbleWidth);
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - bubbleHeight);

        _todoWindow.SetTailOnRight(canPlaceOnLeft);
        _todoWindow.Left = Math.Clamp(desiredLeft, workArea.Left, maximumLeft);
        _todoWindow.Top = Math.Clamp(desiredTop, workArea.Top, maximumTop);
    }

    private void QueueTodoWindowPositionUpdate()
    {
        if (_isClosing || !_todoWindow.IsVisible || _todoPositionUpdateQueued)
        {
            return;
        }

        _todoPositionUpdateQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _todoPositionUpdateQueued = false;
                if (!_isClosing && _todoWindow.IsVisible)
                {
                    UpdateTodoWindowPosition();
                }
            }));
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
        _petSizePersistTimer.Stop();
        _petSizePersistTimer.Tick -= PetSizePersistTimer_Tick;
        _isPetSizeTransitioning = false;
        _isPetSizePreviewSessionActive = false;
        _petSizeEnvelopePrepared = false;
        if (_petSizeSettingsDirty)
        {
            _petSizeScale = _petSizeTargetScale;
            if (SaveSettings())
            {
                _petSizeSettingsDirty = false;
            }
        }
        StopVisualClock();
        StopFrameBlend(snapToTarget: false);
        _automaticTimer.Stop();
        _automaticTimer.Tick -= AutomaticTimer_Tick;
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
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
        AppLogger.Flush(TimeSpan.FromSeconds(1));
    }

    private void PetHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_isPetSizePreviewSessionActive)
        {
            CommitPetSizePreviewSession(persist: true);
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
            _edgeDock = EdgeDock.None;
            _edgePeekFrameDeadlineTimestamp = 0;
            ResetPetVisualTransforms();
            ShowStableTodoFrame();
            UpdateVisualClockSubscription();
            return;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        if (_activeClip is { } activeClip)
        {
            _activeClip = null;
            _activeFrameIndex = -1;
            _activeFrameDeadlineTimestamp = 0;
            AppLogger.Info($"动作中止：{activeClip.ActionName}，原因：拖到屏幕边缘");
            if (_bubbleMode == BubbleMode.Cute)
            {
                SetBubbleMode(BubbleMode.None);
            }
        }

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
        _edgePeekFrameDeadlineTimestamp = Stopwatch.GetTimestamp() +
                                          ToStopwatchTicks(EdgePeekEndpointHold);
        UpdateVisualClockSubscription();
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
        _edgeDock = EdgeDock.None;
        _edgePeekFrameIndex = 0;
        _edgePeekFrameDirection = 1;
        _edgePeekFrameDeadlineTimestamp = 0;
        if (restoreIdleFrame)
        {
            BakeCurrentPetVisualTransformIntoDisplayFrame();
        }

        PetFacingScale.ScaleX = 1;
        PetFacingScale.ScaleY = 1;
        if (restoreIdleFrame)
        {
            _nextFrameBlendDuration = EdgeFrameBlendDuration;
            ShowStableFrame(_idleFrame);
        }
        AppLogger.Info($"边缘探头结束：{GetEdgeName(previousDock)}");
        if (restartAutomaticCountdown)
        {
            RestartAutomaticCountdown();
        }

        UpdateVisualClockSubscription();
    }

    private void AdvanceEdgePeek(long timestamp)
    {
        if (_bubbleMode == BubbleMode.Todo)
        {
            ExitEdgePeek(
                restartAutomaticCountdown: false,
                restoreIdleFrame: false);
            ResetPetVisualTransforms();
            ShowStableTodoFrame();
            return;
        }

        if (_isClosing || _edgeDock == EdgeDock.None)
        {
            return;
        }

        var frames = GetEdgeFrames(_edgeDock);
        var cycleDurationTicks = ToStopwatchTicks(
            EdgePeekEndpointHold + EdgePeekEndpointHold +
            EdgePeekFrameInterval + EdgePeekFrameInterval +
            EdgePeekFrameInterval + EdgePeekFrameInterval);
        var overdueTicks = timestamp - _edgePeekFrameDeadlineTimestamp;
        if (overdueTicks >= cycleDurationTicks)
        {
            _edgePeekFrameDeadlineTimestamp +=
                overdueTicks / cycleDurationTicks * cycleDurationTicks;
        }

        var frameChanged = false;
        while (timestamp >= _edgePeekFrameDeadlineTimestamp &&
               _edgeDock != EdgeDock.None)
        {
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
            frameChanged = true;
            var holdDuration =
                _edgePeekFrameIndex == 0 || _edgePeekFrameIndex == frames.Length - 1
                    ? EdgePeekEndpointHold
                    : EdgePeekFrameInterval;
            _edgePeekFrameDeadlineTimestamp += ToStopwatchTicks(holdDuration);
        }

        if (frameChanged)
        {
            _nextFrameBlendDuration = TimeSpan.Zero;
            ShowStableFrame(frames[_edgePeekFrameIndex]);
        }
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

        if (_isPetSizePreviewSessionActive)
        {
            CommitPetSizePreviewSession(persist: true);
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        _roamWorkArea = MonitorWorkArea.GetForWindow(this);
        _roamLogicalLeft = Left;
        _roamLogicalTop = Top;
        _roamEdge = FindNearestEdge(_roamWorkArea);
        _roamApproachTarget = GetDockedPosition(_roamEdge, _roamWorkArea);
        _roamApproaching = Math.Abs(_roamLogicalLeft - _roamApproachTarget.X) > 0.5 ||
                           Math.Abs(_roamLogicalTop - _roamApproachTarget.Y) > 0.5;
        _roamMode = RoamMode.Wriggle;
        _roamClockwise = _random.Next(2) == 0;
        _roamElapsed = TimeSpan.Zero;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        _roamVisualTravelDistance = 0;
        _roamBoundaryTravelled = 0;
        _roamBoundaryTargetDistance = CalculateRoamPerimeter(
            _roamWorkArea,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);
        _roamBoundaryStart = _roamApproachTarget;
        _roamCornerSourceEdge = EdgeDock.None;
        _roamCornerTargetEdge = EdgeDock.None;
        _isRoamCornerTurning = false;
        _roamVisualDirection = RoamVisualDirection.None;
        _roamVisualPhaseStartedAt = TimeSpan.Zero;
        _roamVisualTransitionEndsAt = TimeSpan.Zero;
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
        _roamLastRenderingTimestamp = Stopwatch.GetTimestamp();
        UpdateRoamVisual();
        UpdateVisualClockSubscription();
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

        _isEdgeRoaming = false;
        _roamLastRenderingTimestamp = 0;
        if (restoreIdleFrame)
        {
            BakeCurrentPetVisualTransformIntoDisplayFrame();
        }

        _roamApproaching = false;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        _roamCornerSourceEdge = EdgeDock.None;
        _roamCornerTargetEdge = EdgeDock.None;
        _isRoamCornerTurning = false;
        _roamVisualEdge = EdgeDock.None;
        _roamVisualDirection = RoamVisualDirection.None;
        _roamVisualPhaseStartedAt = TimeSpan.Zero;
        _roamVisualTransitionEndsAt = TimeSpan.Zero;
        _roamVisualTravelDistance = 0;
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

        UpdateVisualClockSubscription();
    }

    private void AdvanceEdgeRoaming(long timestamp)
    {
        if (_isClosing || !_isEdgeRoaming || !_edgeRoamingEnabled ||
            _edgeDock != EdgeDock.None)
        {
            StopEdgeRoaming("状态已切换", restartAutomaticCountdown: true);
            return;
        }

        if (_roamLastRenderingTimestamp == 0)
        {
            _roamLastRenderingTimestamp = timestamp;
            return;
        }

        var deltaSeconds = Math.Max(
            0,
            (timestamp - _roamLastRenderingTimestamp) / (double)Stopwatch.Frequency);
        _roamLastRenderingTimestamp = timestamp;
        if (deltaSeconds <= 0)
        {
            return;
        }

        // A suspended or blocked UI must resume from the current position instead
        // of replaying queued movement and visibly teleporting around a corner.
        if (deltaSeconds > MaximumRoamDeltaSeconds)
        {
            var visualDelta = TimeSpan.FromSeconds(deltaSeconds);
            _roamElapsed += visualDelta;
            if (_isRoamCornerTurning)
            {
                // Keep the pose clock absolute, but deliberately do not turn the
                // skipped wall-clock interval into boundary movement distance.
                AdvanceRoamCornerTurn(visualDelta);
            }

            UpdateRoamVisual();
            return;
        }

        var remainingSeconds = deltaSeconds;
        var roamSpeed = GetRoamSpeed(_roamMode);
        while (remainingSeconds > 0.000_001 && _isEdgeRoaming)
        {
            var substepRemainingSeconds = Math.Min(
                remainingSeconds,
                MaximumRoamSubstepSeconds);
            remainingSeconds -= substepRemainingSeconds;
            while (substepRemainingSeconds > 0.000_001 && _isEdgeRoaming)
            {
                if (ConsumeRoamVisualTransitionPause(ref substepRemainingSeconds))
                {
                    continue;
                }

                if (_isRoamCornerTurning)
                {
                    var cornerRemainingSeconds = Math.Max(
                        0,
                        (RoamCornerTurnDuration - _roamCornerTurnElapsed).TotalSeconds);
                    var consumedSeconds = Math.Min(
                        substepRemainingSeconds,
                        cornerRemainingSeconds);
                    if (consumedSeconds <= 0.000_001)
                    {
                        // Resolve a duration reached within floating-point tolerance,
                        // then continue this rendering substep on the new edge.
                        AdvanceRoamCornerTurn(
                            RoamCornerTurnDuration - _roamCornerTurnElapsed);
                        continue;
                    }

                    var consumed = TimeSpan.FromSeconds(consumedSeconds);
                    _roamElapsed += consumed;
                    AdvanceRoamCornerTurn(consumed);
                    substepRemainingSeconds -= consumedSeconds;
                    continue;
                }

                var requestedDistance = roamSpeed * substepRemainingSeconds;
                var wasApproaching = _roamApproaching;
                var travelled = _roamApproaching
                    ? MoveTowardRoamBoundary(requestedDistance)
                    : AdvanceRoamAlongBoundary(requestedDistance);
                _roamVisualTravelDistance += travelled;

                var consumedSecondsByMovement = roamSpeed > 0
                    ? Math.Min(substepRemainingSeconds, travelled / roamSpeed)
                    : substepRemainingSeconds;
                if (consumedSecondsByMovement > 0)
                {
                    _roamElapsed += TimeSpan.FromSeconds(consumedSecondsByMovement);
                    substepRemainingSeconds -= consumedSecondsByMovement;
                }

                var stateChanged = wasApproaching != _roamApproaching ||
                                   _isRoamCornerTurning ||
                                   !_isEdgeRoaming;
                if (stateChanged)
                {
                    if (wasApproaching && !_roamApproaching &&
                        !_isRoamCornerTurning && _isEdgeRoaming)
                    {
                        // Resolve the approach -> boundary direction before consuming
                        // this substep's remainder. UpdateRoamVisual starts the 120ms
                        // contact transition, which the next loop iteration pauses.
                        UpdateRoamVisual();
                    }

                    // The movement reached a boundary, corner, or lap endpoint before
                    // this substep ended. Consume the remainder in the new state.
                    continue;
                }

                // Ordinary movement consumes the complete requested interval. This
                // fallback also prevents a floating-point no-progress loop.
                if (substepRemainingSeconds > 0.000_001)
                {
                    _roamElapsed += TimeSpan.FromSeconds(substepRemainingSeconds);
                    substepRemainingSeconds = 0;
                }
            }
        }

        if (_isEdgeRoaming)
        {
            ApplyLogicalRoamPosition();
            if (_isRoamCornerTurning)
            {
                // Corner frames use their own absolute corner phase below.
                _roamVisualTravelDistance = 0;
            }

            UpdateRoamVisual();
        }
    }

    private bool ConsumeRoamVisualTransitionPause(ref double remainingSeconds)
    {
        if (_isRoamCornerTurning || remainingSeconds <= 0.000_001)
        {
            return false;
        }

        var transitionRemainingSeconds =
            (_roamVisualTransitionEndsAt - _roamElapsed).TotalSeconds;
        if (transitionRemainingSeconds <= 0.000_001)
        {
            return false;
        }

        // The state clock remains absolute, but the logical window position and
        // distance-driven pose phase stay fixed until the one-off transition ends.
        // If this rendering slice crosses the deadline, its unconsumed remainder
        // immediately continues as ordinary movement in the next loop iteration.
        var consumedSeconds = Math.Min(remainingSeconds, transitionRemainingSeconds);
        _roamElapsed += TimeSpan.FromSeconds(consumedSeconds);
        remainingSeconds -= consumedSeconds;
        return true;
    }

    private double MoveTowardRoamBoundary(double distance)
    {
        var deltaX = _roamApproachTarget.X - _roamLogicalLeft;
        var deltaY = _roamApproachTarget.Y - _roamLogicalTop;
        var remaining = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (remaining <= distance || remaining <= 0.5)
        {
            _roamLogicalLeft = _roamApproachTarget.X;
            _roamLogicalTop = _roamApproachTarget.Y;
            _roamApproaching = false;
            _roamBoundaryStart = _roamApproachTarget;
            _roamBoundaryTravelled = 0;
            return remaining;
        }

        _roamLogicalLeft += deltaX / remaining * distance;
        _roamLogicalTop += deltaY / remaining * distance;
        return distance;
    }

    private void ApplyLogicalRoamPosition()
    {
        var snappedLeft = SnapDipToPhysicalPixel(_roamLogicalLeft, horizontal: true);
        var snappedTop = SnapDipToPhysicalPixel(_roamLogicalTop, horizontal: false);
        if (Left != snappedLeft)
        {
            Left = snappedLeft;
        }

        if (Top != snappedTop)
        {
            Top = snappedTop;
        }
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
        var current = horizontal ? _roamLogicalLeft : _roamLogicalTop;
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
                _roamLogicalLeft += positiveDirection ? travelled : -travelled;
            }
            else
            {
                _roamLogicalTop += positiveDirection ? travelled : -travelled;
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
                _roamLogicalLeft = positiveDirection ? maximum : minimum;
            }
            else
            {
                _roamLogicalTop = positiveDirection ? maximum : minimum;
            }

            BeginRoamCornerTurn(GetNextRoamEdge(_roamEdge, _roamClockwise));
        }

        return travelled;
    }

    private void CompleteRoamLap()
    {
        _roamLogicalLeft = _roamBoundaryStart.X;
        _roamLogicalTop = _roamBoundaryStart.Y;
        ApplyLogicalRoamPosition();
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

        _roamCornerSourceEdge = _roamEdge;
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
        _roamCornerSourceEdge = EdgeDock.None;
        _roamCornerTargetEdge = EdgeDock.None;
        _roamCornerTurnElapsed = TimeSpan.Zero;
        var horizontal = _roamEdge is EdgeDock.Top or EdgeDock.Bottom;
        _roamVisualDirection = GetRoamVisualDirection(
            horizontal,
            IsPositiveRoamDirection(_roamEdge, _roamClockwise));
        _roamVisualPhaseStartedAt = _roamElapsed;
        _roamVisualTransitionEndsAt = _roamElapsed;
        _roamVisualTravelDistance = 0;
        PetCornerScale.ScaleX = 1;
        PetCornerScale.ScaleY = 1;
    }

    private void UpdateRoamVisual()
    {
        if (!_isEdgeRoaming)
        {
            return;
        }

        var elapsed = _roamElapsed;
        if (_roamMode == RoamMode.Wriggle && _isRoamCornerTurning)
        {
            UpdateWriggleCornerVisual(elapsed);
            return;
        }

        var horizontal = _roamApproaching
            ? Math.Abs(_roamApproachTarget.X - _roamLogicalLeft) >=
              Math.Abs(_roamApproachTarget.Y - _roamLogicalTop)
            : _roamEdge is EdgeDock.Top or EdgeDock.Bottom;
        var movingPositive = _roamApproaching
            ? horizontal
                ? _roamApproachTarget.X >= _roamLogicalLeft
                : _roamApproachTarget.Y >= _roamLogicalTop
            : IsPositiveRoamDirection(_roamEdge, _roamClockwise);
        var direction = GetRoamVisualDirection(horizontal, movingPositive);
        var previousDirection = _roamVisualDirection;
        var directionChanged = direction != previousDirection;
        if (directionChanged)
        {
            _roamVisualDirection = direction;
            _roamVisualPhaseStartedAt = elapsed;
            _roamVisualTransitionEndsAt = elapsed + RoamVisualTransitionDuration;
            _roamVisualTravelDistance = 0;
        }

        var modeIndex = (int)_roamMode;
        var frames = direction switch
        {
            RoamVisualDirection.Horizontal => _roamHorizontalFrames[modeIndex],
            RoamVisualDirection.VerticalDown => _roamVerticalDownFrames[modeIndex],
            _ => _roamVerticalUpFrames[modeIndex]
        };
        var transitioning = elapsed < _roamVisualTransitionEndsAt;
        if (transitioning)
        {
            _roamVisualTravelDistance = 0;
        }

        var frameIndex = transitioning
            ? 0
            : (int)(_roamVisualTravelDistance / WriggleFrameTravelDistance) %
              frames.Length;

        var targetFacingScaleX = horizontal
            // 横向原画朝右；朝左移动时镜像，始终让脸朝向前进方向。
            ? movingPositive ? 1 : -1
            // 竖边原画朝右；左边缘保持原向，右边缘镜像，始终面向屏幕内部。
            : _roamEdge == EdgeDock.Left ? 1 : -1;
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
            var transitionStartedAt = _isRoamCornerTurning
                ? elapsed - _roamCornerTurnElapsed
                : elapsed;
            AnimateRoamBaseOffset(visualEdge, transitionStartedAt);
            _roamVisualEdge = visualEdge;
        }
        UpdateRoamBaseOffsetTransition(elapsed);

        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;

        var nextFrame = frames[frameIndex];
        _nextFrameBlendDuration = directionChanged
            ? RoamVisualTransitionDuration
            : TimeSpan.Zero;
        ShowStableFrame(nextFrame);
    }

    private void UpdateWriggleCornerVisual(TimeSpan elapsed)
    {
        var targetEdge = _roamCornerTargetEdge;
        if (targetEdge == EdgeDock.None)
        {
            return;
        }

        var progress = Math.Clamp(
            _roamCornerTurnElapsed.TotalMilliseconds /
            RoamCornerTurnDuration.TotalMilliseconds,
            0,
            0.999_999);
        var targetHorizontal = targetEdge is EdgeDock.Top or EdgeDock.Bottom;
        var frameIndex = Math.Min(
            _roamWriggleCornerFrames.Length - 1,
            (int)(progress * _roamWriggleCornerFrames.Length));
        // The authored sequence is prone-horizontal -> upright-vertical.
        // Leaving a vertical edge reuses it in reverse.
        if (targetHorizontal)
        {
            frameIndex = _roamWriggleCornerFrames.Length - 1 - frameIndex;
        }

        // Every authored corner pose faces right, so the two corner orientations
        // that change mirror direction need one discrete hand-off. Frame 43 has
        // the highest horizontal Alpha-mask symmetry at the final 190x242 size;
        // switching only there is less visible than an unconditional midpoint flip
        // and avoids scale-through-zero, fading, or overlapping silhouettes.
        var switchFrameIndex = Math.Clamp(
            WriggleCornerFacingSwitchFrameNumber - 1,
            0,
            _roamWriggleCornerFrames.Length - 1);
        var useTargetFacing = targetHorizontal
            ? frameIndex <= switchFrameIndex
            : frameIndex >= switchFrameIndex;
        var facingEdge = useTargetFacing || _roamCornerSourceEdge == EdgeDock.None
            ? targetEdge
            : _roamCornerSourceEdge;
        var facingHorizontal = facingEdge is EdgeDock.Top or EdgeDock.Bottom;
        var facingMovingPositive = IsPositiveRoamDirection(facingEdge, _roamClockwise);
        PetFacingScale.ScaleX = facingHorizontal
            ? facingMovingPositive ? 1 : -1
            : facingEdge == EdgeDock.Left ? 1 : -1;
        PetFacingScale.ScaleY = 1;

        if (targetEdge != _roamVisualEdge)
        {
            AnimateRoamBaseOffset(
                targetEdge,
                elapsed - _roamCornerTurnElapsed);
            _roamVisualEdge = targetEdge;
        }

        UpdateRoamBaseOffsetTransition(elapsed);
        PetRoamOffset.X = 0;
        PetRoamOffset.Y = 0;

        _nextFrameBlendDuration = TimeSpan.Zero;
        ShowStableFrame(_roamWriggleCornerFrames[frameIndex]);
    }

    private static RoamVisualDirection GetRoamVisualDirection(
        bool horizontal,
        bool movingPositive)
    {
        return horizontal
            ? RoamVisualDirection.Horizontal
            : movingPositive
                ? RoamVisualDirection.VerticalDown
                : RoamVisualDirection.VerticalUp;
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
        return mode == RoamMode.Wriggle
            ? 60
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
    }

    private static int GetRoamFrameCount(RoamMode mode)
    {
        return mode == RoamMode.Wriggle
            ? WriggleRoamFrameCount
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
    }

    private static double GetTopRoamOffset(RoamMode mode)
    {
        return mode == RoamMode.Wriggle
            ? 118
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
    }

    private static string GetRoamAssetName(RoamMode mode)
    {
        return mode == RoamMode.Wriggle
            ? "wriggle"
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
    }

    private static string GetRoamModeName(RoamMode mode)
    {
        return mode == RoamMode.Wriggle
            ? "趴着蠕动"
            : "未知";
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

        _nextFrameBlendDuration = RoamVisualTransitionDuration;
        _nextFrameMinimumHold = RoamVisualTransitionDuration;
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

    private void AdvanceActiveClip(long timestamp)
    {
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

        var resolvedFrameIndex = _activeFrameIndex;
        while (timestamp >= _activeFrameDeadlineTimestamp)
        {
            var nextFrameIndex = resolvedFrameIndex + 1;
            if (nextFrameIndex >= clip.Frames.Length)
            {
                CompleteActiveClip(clip);
                return;
            }

            resolvedFrameIndex = nextFrameIndex;
            _activeFrameDeadlineTimestamp +=
                ToStopwatchTicks(clip.Frames[resolvedFrameIndex].HoldDuration);
        }

        if (resolvedFrameIndex != _activeFrameIndex)
        {
            if (resolvedFrameIndex - _activeFrameIndex > 1)
            {
                _nextFrameBlendDuration = TimeSpan.Zero;
            }

            _activeFrameIndex = resolvedFrameIndex;
            ShowStableFrame(clip.Frames[resolvedFrameIndex].Image);
        }
    }

    private void CompleteActiveClip(AnimationClip clip)
    {
        ShowStableFrame(clip.Frames[^1].Image);
        if (!ReferenceEquals(_activeClip, clip))
        {
            return;
        }

        var elapsedMilliseconds = _activeClipStartedTimestamp > 0
            ? Math.Max(
                0,
                (Stopwatch.GetTimestamp() - _activeClipStartedTimestamp) * 1000d /
                Stopwatch.Frequency)
            : 0;
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        AppLogger.Info(
            $"动作完成：{clip.ActionName}，实际耗时 {elapsedMilliseconds:F1} ms");
        ScheduleUnusedSpritePageCollection($"动作完成/{clip.ActionName}");
        if (_bubbleMode == BubbleMode.Cute)
        {
            SetBubbleMode(BubbleMode.None);
        }
        RestartAutomaticCountdown();
        UpdateVisualClockSubscription();
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
        _activeClipStartedTimestamp = Stopwatch.GetTimestamp();
        _activeFrameDeadlineTimestamp = _activeClipStartedTimestamp +
                                        ToStopwatchTicks(holdDuration);
        UpdateVisualClockSubscription();
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
            UpdateVisualClockSubscription();
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

        if (_loadedSpritePageStride <= 0)
        {
            throw new InvalidOperationException("精灵分页尚未载入像素缓冲区。");
        }

        var destinationStride = checked(DisplayPixelWidth * 4);
        var sourceX = frame.X + visibleLeft - frame.DestinationX;
        var sourceY = frame.Y + visibleTop - frame.DestinationY;
        var rowBytes = checked(visibleWidth * 4);
        for (var row = 0; row < visibleHeight; row++)
        {
            Buffer.BlockCopy(
                _spritePagePixels,
                checked((sourceY + row) * _loadedSpritePageStride + sourceX * 4),
                destination,
                checked((visibleTop + row) * destinationStride + visibleLeft * 4),
                rowBytes);
        }
    }

    private void VisualClock_Rendering(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            StopVisualClock();
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (_isPetSizeTransitioning)
        {
            AdvancePetSizeTransition(timestamp);
            if (_petSizeTodoPositionNeedsUpdate && _todoWindow.IsVisible)
            {
                _petSizeTodoPositionNeedsUpdate = false;
                UpdateTodoWindowPosition();
            }
        }

        if (_activeClip is not null)
        {
            AdvanceActiveClip(timestamp);
        }

        if (_edgeDock != EdgeDock.None)
        {
            AdvanceEdgePeek(timestamp);
        }

        if (_isEdgeRoaming)
        {
            AdvanceEdgeRoaming(timestamp);
        }

        UpdateFrameBlend(timestamp, force: false);
        UpdateVisualClockSubscription();
    }

    private void UpdateVisualClockSubscription()
    {
        var shouldRun = !_isClosing &&
                        (_isPetSizeTransitioning ||
                         _activeClip is not null ||
                         _edgeDock != EdgeDock.None ||
                         _isEdgeRoaming ||
                         _isFrameBlending);
        if (shouldRun == _isVisualClockSubscribed)
        {
            return;
        }

        if (shouldRun)
        {
            CompositionTarget.Rendering += VisualClock_Rendering;
        }
        else
        {
            CompositionTarget.Rendering -= VisualClock_Rendering;
        }

        _isVisualClockSubscribed = shouldRun;
    }

    private void StopVisualClock()
    {
        if (!_isVisualClockSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= VisualClock_Rendering;
        _isVisualClockSubscribed = false;
    }

    private static long ToStopwatchTicks(TimeSpan duration)
    {
        return Math.Max(
            1,
            (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency));
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
        var logicalOrigin = new Point(
            PetVisual.RenderTransformOrigin.X * PetWidth,
            PetVisual.RenderTransformOrigin.Y * PetHeight);
        var logicalMatrix = Matrix.Identity;
        logicalMatrix.Translate(-logicalOrigin.X, -logicalOrigin.Y);
        logicalMatrix.Append(transform);
        logicalMatrix.Translate(logicalOrigin.X, logicalOrigin.Y);

        var pixelToLogical = new Matrix(
            PetWidth / DisplayPixelWidth,
            0,
            0,
            PetHeight / DisplayPixelHeight,
            0,
            0);
        var logicalToPixel = new Matrix(
            DisplayPixelWidth / PetWidth,
            0,
            0,
            DisplayPixelHeight / PetHeight,
            0,
            0);
        pixelToLogical.Append(logicalMatrix);
        pixelToLogical.Append(logicalToPixel);
        return pixelToLogical;
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
        UpdateVisualClockSubscription();
    }

    private void LoadSpritePageIntoBuffer(string pageName, SpriteAtlasPage page)
    {
        var loadStopwatch = Stopwatch.StartNew();
        var resource = Application.GetResourceStream(CreatePackUri(page.ResourcePath))
            ?? throw new InvalidOperationException(
                $"找不到精灵图集分页：{page.ResourcePath}");
        var stride = checked(page.Width * 4);
        var readStartedAt = Stopwatch.GetTimestamp();
        TimeSpan readElapsed;
        using (resource.Stream)
        {
            resource.Stream.ReadExactly(
                _spritePageCompressedBytes.AsSpan(0, page.CompressedByteCount));
            if (resource.Stream.ReadByte() != -1)
            {
                throw new InvalidDataException("LZ4分页压缩尺寸与清单不一致。");
            }

            readElapsed = Stopwatch.GetElapsedTime(readStartedAt);
            DecodeLz4Block(
                _spritePageCompressedBytes.AsSpan(0, page.CompressedByteCount),
                _spritePagePixels,
                page.UncompressedByteCount);
        }

        _loadedSpritePageName = pageName;
        _loadedSpritePageStride = stride;
        loadStopwatch.Stop();
        AppLogger.Info(
            $"精灵分页已写入复用缓冲区：{pageName}，" +
            $"{page.Width}x{page.Height}，{loadStopwatch.Elapsed.TotalMilliseconds:F1} ms" +
            $"（读取 {readElapsed.TotalMilliseconds:F1} ms，" +
            $"解压 {loadStopwatch.Elapsed.TotalMilliseconds - readElapsed.TotalMilliseconds:F1} ms）");
    }

    private static void DecodeLz4Block(
        ReadOnlySpan<byte> input,
        byte[] output,
        int expectedLength)
    {
        if (expectedLength < 0 || expectedLength > output.Length)
        {
            throw new InvalidDataException("LZ4分页输出尺寸超出复用缓冲区。");
        }

        var inputIndex = 0;
        var outputIndex = 0;
        while (outputIndex < expectedLength)
        {
            var token = ReadRequiredLz4Byte(input, ref inputIndex);
            var literalLength = ReadLz4Length(input, ref inputIndex, token >> 4);
            if (literalLength > expectedLength - outputIndex ||
                literalLength > input.Length - inputIndex)
            {
                throw new InvalidDataException("LZ4分页字面量越过输出边界。");
            }

            input.Slice(inputIndex, literalLength).CopyTo(
                output.AsSpan(outputIndex, literalLength));
            inputIndex += literalLength;
            outputIndex += literalLength;
            if (outputIndex == expectedLength)
            {
                if (inputIndex != input.Length)
                {
                    throw new InvalidDataException("LZ4分页包含多余压缩数据。");
                }

                return;
            }

            var offsetLow = ReadRequiredLz4Byte(input, ref inputIndex);
            var offsetHigh = ReadRequiredLz4Byte(input, ref inputIndex);
            var matchOffset = offsetLow | (offsetHigh << 8);
            if (matchOffset <= 0 || matchOffset > outputIndex)
            {
                throw new InvalidDataException("LZ4分页回溯偏移无效。");
            }

            var matchLength = checked(
                ReadLz4Length(input, ref inputIndex, token & 0x0f) + 4);
            if (matchLength > expectedLength - outputIndex)
            {
                throw new InvalidDataException("LZ4分页匹配段越过输出边界。");
            }

            var matchDestination = outputIndex;
            var copiedMatchLength = Math.Min(matchOffset, matchLength);
            output.AsSpan(outputIndex - matchOffset, copiedMatchLength).CopyTo(
                output.AsSpan(matchDestination, copiedMatchLength));
            while (copiedMatchLength < matchLength)
            {
                var copyLength = Math.Min(
                    copiedMatchLength,
                    matchLength - copiedMatchLength);
                output.AsSpan(matchDestination, copyLength).CopyTo(
                    output.AsSpan(
                        matchDestination + copiedMatchLength,
                        copyLength));
                copiedMatchLength += copyLength;
            }

            outputIndex += matchLength;
        }

        throw new InvalidDataException("LZ4分页未以完整字面量序列结束。");
    }

    private static int ReadLz4Length(
        ReadOnlySpan<byte> input,
        ref int inputIndex,
        int initialLength)
    {
        var length = initialLength;
        if (initialLength != 15)
        {
            return length;
        }

        int extension;
        do
        {
            extension = ReadRequiredLz4Byte(input, ref inputIndex);
            length = checked(length + extension);
        }
        while (extension == byte.MaxValue);

        return length;
    }

    private static int ReadRequiredLz4Byte(
        ReadOnlySpan<byte> input,
        ref int inputIndex)
    {
        if ((uint)inputIndex >= (uint)input.Length)
        {
            throw new EndOfStreamException("LZ4分页数据提前结束。");
        }

        return input[inputIndex++];
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
            _activeClip = null;
            _activeFrameIndex = -1;
            _activeFrameDeadlineTimestamp = 0;
            AppLogger.Info(
                $"动作中止：{activeClip.ActionName}，原因：打开待办");
        }
        else
        {
            _activeFrameIndex = -1;
            _activeFrameDeadlineTimestamp = 0;
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

        // 普通动作、绕屏和边缘探头都已是离开枕头的姿势；从 wake14
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
        _activeClip = _todoExitClip;
        _activeFrameIndex = exitStartIndex - 1;
        _nextFrameBlendDuration = TodoStateBlendDuration;
        _nextFrameMinimumHold = TodoStateBlendDuration;
        AppLogger.Info("待办收起过渡开始");
        ShowActiveClipFrame(exitStartIndex);
    }

    private void ShowStableTodoFrame()
    {
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeFrameDeadlineTimestamp = 0;
        ShowStableFrame(_todoFrame);
        UpdateVisualClockSubscription();
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
            var wasDeactivateSuppressed = _suppressTodoWindowDeactivate;
            _suppressTodoWindowDeactivate = true;
            try
            {
                _todoWindow.Hide();
            }
            finally
            {
                _suppressTodoWindowDeactivate = wasDeactivateSuppressed;
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
        StartPetSizeScaleTransition(scale);
    }

    private void TodoWindow_PetSizeAdjustmentStarted()
    {
        _isPetSizeAdjustmentActive = true;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();
    }

    private void TodoWindow_PetSizeAdjustmentCompleted()
    {
        _isPetSizeAdjustmentActive = false;
        var shouldScheduleCommit =
            _petSizeCommitPending ||
            _isPetSizePreviewSessionActive ||
            _petSizeSettingsDirty;
        if (!shouldScheduleCommit)
        {
            return;
        }

        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();
        _petSizePersistTimer.Start();
    }

    private static double NormalizePetSizeScale(double scale)
    {
        if (!double.IsFinite(scale))
        {
            return 1;
        }

        var clamped = Math.Clamp(scale, MinimumPetSizeScale, MaximumPetSizeScale);
        return Math.Round(clamped, 3, MidpointRounding.AwayFromZero);
    }

    private void StartPetSizeScaleTransition(double scale)
    {
        StartPetSizeScaleTransitionAt(scale, Stopwatch.GetTimestamp());
    }

    private void StartPetSizeScaleTransitionAt(double scale, long timestamp)
    {
        if (_isClosing)
        {
            return;
        }

        var normalizedScale = NormalizePetSizeScale(scale);
        var currentMotion = GetPetSizeMotionStateAt(timestamp);
        var currentScale = currentMotion.Scale;
        var currentVelocity = currentMotion.Velocity;
        if (!_isPetSizePreviewSessionActive &&
            Math.Abs(normalizedScale - currentScale) < 0.0005)
        {
            _todoWindow.SetPetSizeScale(normalizedScale);
            return;
        }

        if (!_isPetSizePreviewSessionActive)
        {
            _petSizePreviewAnchor = CapturePetSizeAnchor(preservePosition: true);
            _petSizeTodoChildOnLeft = _todoWindow.IsVisible
                ? _todoWindow.Left < Left + Width / 2
                : null;
            _petSizePreviewBaseScale = currentScale;
            _isPetSizePreviewSessionActive = true;
            _petSizeEnvelopePrepared = false;
            PreparePetSizePreviewEnvelope();
        }

        ApplyPetSizePreviewScale(currentScale);
        var previousTargetScale = _petSizeTargetScale;
        _petSizeTransitionStartScale = currentScale;
        _petSizeTargetScale = normalizedScale;
        var distanceToTarget = _petSizeTargetScale - _petSizeTransitionStartScale;
        var targetDelta = _petSizeTargetScale - previousTargetScale;
        var targetContinuesWithVelocity = Math.Abs(targetDelta) < 0.0005 ||
                                          targetDelta * currentVelocity > 0;
        if (distanceToTarget * currentVelocity <= 0 ||
            !targetContinuesWithVelocity)
        {
            currentVelocity = 0;
        }
        else
        {
            var maximumNoOvershootVelocity =
                PetSizeSpringAngularFrequency * Math.Abs(distanceToTarget);
            currentVelocity = Math.CopySign(
                Math.Min(Math.Abs(currentVelocity), maximumNoOvershootVelocity),
                currentVelocity);
        }

        _petSizeTransitionStartVelocity = Math.Clamp(
            currentVelocity,
            -MaximumPetSizeVelocity,
            MaximumPetSizeVelocity);
        _petSizeVelocity = _petSizeTransitionStartVelocity;
        _petSizeTransitionStartedTimestamp = timestamp;
        _isPetSizeTransitioning =
            Math.Abs(distanceToTarget) >= 0.0005 ||
            Math.Abs(_petSizeTransitionStartVelocity) >= 0.005;
        _petSizeSettingsDirty =
            Math.Abs(_petSizeTargetScale - _persistedPetSizeScale) >= 0.0005;
        if (!_isPetSizeTransitioning)
        {
            ApplyPetSizePreviewScale(_petSizeTargetScale);
        }

        _petSizePersistTimer.Stop();
        if (!_isPetSizeAdjustmentActive)
        {
            _petSizePersistTimer.Start();
        }
        UpdateVisualClockSubscription();
    }

    private double GetPetSizeScaleAt(long timestamp)
    {
        return GetPetSizeMotionStateAt(timestamp).Scale;
    }

    private PetSizeMotionState GetPetSizeMotionStateAt(long timestamp)
    {
        if (!_isPetSizeTransitioning)
        {
            return new PetSizeMotionState(_petSizeScale, 0);
        }

        var elapsedTicks = Math.Max(0, timestamp - _petSizeTransitionStartedTimestamp);
        var durationTicks = ToStopwatchTicks(PetSizeTransitionDuration);
        if (elapsedTicks >= durationTicks)
        {
            return new PetSizeMotionState(_petSizeTargetScale, 0);
        }

        var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        var initialOffset = _petSizeTransitionStartScale - _petSizeTargetScale;
        var dampingTerm = _petSizeTransitionStartVelocity +
                          PetSizeSpringAngularFrequency * initialOffset;
        var decay = Math.Exp(-PetSizeSpringAngularFrequency * elapsedSeconds);
        var scale = _petSizeTargetScale +
                    (initialOffset + dampingTerm * elapsedSeconds) * decay;
        var velocity = (_petSizeTransitionStartVelocity -
                        PetSizeSpringAngularFrequency * dampingTerm * elapsedSeconds) * decay;
        return new PetSizeMotionState(
            Math.Clamp(scale, MinimumPetSizeScale, MaximumPetSizeScale),
            velocity);
    }

    private void AdvancePetSizeTransition(long timestamp)
    {
        if (!_isPetSizeTransitioning)
        {
            return;
        }

        PreparePetSizePreviewEnvelope();
        var motion = GetPetSizeMotionStateAt(timestamp);
        _petSizeVelocity = motion.Velocity;
        ApplyPetSizePreviewScale(motion.Scale);
        if (timestamp - _petSizeTransitionStartedTimestamp >=
                ToStopwatchTicks(PetSizeTransitionDuration) ||
            (Math.Abs(motion.Scale - _petSizeTargetScale) < 0.0005 &&
             Math.Abs(motion.Velocity) < 0.005))
        {
            _isPetSizeTransitioning = false;
            _petSizeVelocity = 0;
            ApplyPetSizePreviewScale(_petSizeTargetScale);
        }
    }

    private void PreparePetSizePreviewEnvelope()
    {
        if (_petSizeEnvelopePrepared || !_isPetSizePreviewSessionActive)
        {
            return;
        }

        ConfigurePetSizeViewboxAnchor(_petSizePreviewAnchor);
        PetSizeViewbox.Width = PetWidth * _petSizePreviewBaseScale;
        PetSizeViewbox.Height = PetHeight * _petSizePreviewBaseScale;
        PetUserSizeScale.ScaleX = 1;
        PetUserSizeScale.ScaleY = 1;
        PetUserSizeOffset.X = 0;
        PetUserSizeOffset.Y = 0;
        ApplyPetSizeWindowBounds(MaximumPetSizeScale, _petSizePreviewAnchor);
        _petSizeEnvelopePrepared = true;
        _petSizeTodoPositionNeedsUpdate = true;
        QueueTodoWindowPositionUpdate();
    }

    private void ApplyPetSizePreviewScale(double scale)
    {
        var previousScaleX = PetUserSizeScale.ScaleX;
        var previousScaleY = PetUserSizeScale.ScaleY;
        var previousOffsetX = PetUserSizeOffset.X;
        var previousOffsetY = PetUserSizeOffset.Y;
        var baseWidth = PetSizeViewbox.ActualWidth > 0
            ? PetSizeViewbox.ActualWidth
            : PetWidth * Math.Max(MinimumPetSizeScale, _petSizePreviewBaseScale);
        var baseHeight = PetSizeViewbox.ActualHeight > 0
            ? PetSizeViewbox.ActualHeight
            : PetHeight * Math.Max(MinimumPetSizeScale, _petSizePreviewBaseScale);
        var alignedWidth = SnapDipToPhysicalPixel(PetWidth * scale, horizontal: true);
        var alignedHeight = SnapDipToPhysicalPixel(PetHeight * scale, horizontal: false);
        var visualScaleX = alignedWidth / baseWidth;
        var visualScaleY = alignedHeight / baseHeight;
        _petSizeScale = scale;
        PetUserSizeScale.ScaleX = visualScaleX;
        PetUserSizeScale.ScaleY = visualScaleY;
        PetUserSizeOffset.X = 0;
        PetUserSizeOffset.Y = 0;
        AlignPetSizePreviewToPhysicalPixels();
        var transformChanged =
            Math.Abs(previousScaleX - visualScaleX) >= 0.000001 ||
            Math.Abs(previousScaleY - visualScaleY) >= 0.000001 ||
            Math.Abs(previousOffsetX - PetUserSizeOffset.X) >= 0.000001 ||
            Math.Abs(previousOffsetY - PetUserSizeOffset.Y) >= 0.000001;
        _petSizeTodoPositionNeedsUpdate |= transformChanged;
    }

    private void AlignPetSizePreviewToPhysicalPixels()
    {
        if (!PetSizeViewbox.IsLoaded)
        {
            return;
        }

        var compositionTarget = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (compositionTarget is null)
        {
            return;
        }

        try
        {
            var topLeft = PetSizeViewbox.PointToScreen(new Point(0, 0));
            var transform = compositionTarget.TransformToDevice;
            if (double.IsFinite(transform.M11) && transform.M11 > 0)
            {
                PetUserSizeOffset.X =
                    (Math.Round(topLeft.X, MidpointRounding.AwayFromZero) - topLeft.X) /
                    transform.M11;
            }

            if (double.IsFinite(transform.M22) && transform.M22 > 0)
            {
                PetUserSizeOffset.Y =
                    (Math.Round(topLeft.Y, MidpointRounding.AwayFromZero) - topLeft.Y) /
                    transform.M22;
            }
        }
        catch (InvalidOperationException)
        {
            PetUserSizeOffset.X = 0;
            PetUserSizeOffset.Y = 0;
        }
    }

    private PetSizeAnchor? CapturePetSizeAnchor(bool preservePosition)
    {
        if (!preservePosition || !IsLoaded)
        {
            return null;
        }

        var workArea = MonitorWorkArea.GetForWindow(this);
        var currentWidth = double.IsFinite(Width) && Width > 0 ? Width : ActualWidth;
        var currentHeight = double.IsFinite(Height) && Height > 0 ? Height : ActualHeight;
        var currentLeft = Left;
        var currentTop = Top;
        var preserveLeftEdge = Math.Abs(currentLeft - workArea.Left) <= EdgeContactTolerance;
        var preserveRightEdge = Math.Abs(
            currentLeft + currentWidth - workArea.Right) <= EdgeContactTolerance;
        var preserveTopEdge = Math.Abs(currentTop - workArea.Top) <= EdgeContactTolerance;
        var preserveBottomEdge = Math.Abs(
            currentTop + currentHeight - workArea.Bottom) <= EdgeContactTolerance;
        var horizontal = preserveLeftEdge
            ? workArea.Left
            : preserveRightEdge
                ? workArea.Right
                : currentLeft + currentWidth / 2;
        var vertical = preserveTopEdge
            ? workArea.Top
            : preserveBottomEdge
                ? workArea.Bottom
                : currentTop + currentHeight;
        return new PetSizeAnchor(
            workArea,
            preserveLeftEdge,
            preserveRightEdge,
            preserveTopEdge,
            preserveBottomEdge,
            horizontal,
            vertical);
    }

    private void ConfigurePetSizeViewboxAnchor(PetSizeAnchor? anchor)
    {
        var horizontalAlignment = anchor is { PreserveLeftEdge: true }
            ? HorizontalAlignment.Left
            : anchor is { PreserveRightEdge: true }
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Center;
        var verticalAlignment = anchor is { PreserveTopEdge: true }
            ? VerticalAlignment.Top
            : VerticalAlignment.Bottom;
        var originX = horizontalAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => 1,
            _ => 0.5
        };
        var originY = verticalAlignment == VerticalAlignment.Top ? 0 : 1;

        PetSizeViewbox.HorizontalAlignment = horizontalAlignment;
        PetSizeViewbox.VerticalAlignment = verticalAlignment;
        PetSizeViewbox.RenderTransformOrigin = new Point(originX, originY);
    }

    private void ApplyPetSizeWindowBounds(double scale, PetSizeAnchor? anchor)
    {
        var displayedWidth = PetWidth * scale;
        var displayedHeight = PetHeight * scale;
        var wasApplyingLayout = _isApplyingPetSizeLayout;
        _isApplyingPetSizeLayout = true;
        try
        {
            Width = displayedWidth;
            Height = displayedHeight;
            if (anchor is not { } fixedAnchor)
            {
                return;
            }

            var workArea = fixedAnchor.WorkArea;
            var desiredLeft = fixedAnchor.PreserveLeftEdge
                ? workArea.Left
                : fixedAnchor.PreserveRightEdge
                    ? workArea.Right - displayedWidth
                    : fixedAnchor.Horizontal - displayedWidth / 2;
            var desiredTop = fixedAnchor.PreserveTopEdge
                ? workArea.Top
                : fixedAnchor.Vertical - displayedHeight;
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
        finally
        {
            _isApplyingPetSizeLayout = wasApplyingLayout;
        }
    }

    private void PetSizePersistTimer_Tick(object? sender, EventArgs e)
    {
        _petSizePersistTimer.Stop();
        if (_isPetSizeAdjustmentActive)
        {
            _petSizeCommitPending = true;
            return;
        }

        if (_isPetSizePreviewSessionActive)
        {
            CommitPetSizePreviewSession(persist: true);
            return;
        }

        if (_petSizeSettingsDirty && SaveSettings())
        {
            _petSizeSettingsDirty = false;
            _petSizeCommitPending = false;
        }
    }

    private void CommitPetSizePreviewSession(bool persist)
    {
        if (!_isPetSizePreviewSessionActive)
        {
            return;
        }

        _petSizePersistTimer.Stop();
        var finalScale = _petSizeTargetScale;
        var fixedAnchor = _petSizePreviewAnchor;
        _isPetSizeTransitioning = false;
        _petSizeVelocity = 0;
        _petSizeTransitionStartVelocity = 0;
        _petSizeScale = finalScale;
        PetUserSizeScale.ScaleX = 1;
        PetUserSizeScale.ScaleY = 1;
        PetUserSizeOffset.X = 0;
        PetUserSizeOffset.Y = 0;
        ConfigurePetSizeViewboxAnchor(fixedAnchor);
        PetSizeViewbox.Width = PetWidth * finalScale;
        PetSizeViewbox.Height = PetHeight * finalScale;
        ApplyPetSizeWindowBounds(finalScale, fixedAnchor);
        _todoWindow.SetPetSizeScale(finalScale);

        _isPetSizePreviewSessionActive = false;
        _petSizeEnvelopePrepared = false;
        _petSizePreviewBaseScale = finalScale;
        _petSizeTransitionStartScale = finalScale;
        _petSizePreviewAnchor = null;
        _petSizeTodoChildOnLeft = null;
        _petSizeTodoPositionNeedsUpdate = false;
        _petSizeCommitPending = false;
        QueueTodoWindowPositionUpdate();
        UpdateVisualClockSubscription();

        var saved = true;
        if (persist && _petSizeSettingsDirty)
        {
            saved = SaveSettings();
            if (saved)
            {
                _petSizeSettingsDirty = false;
            }
        }

        AppLogger.Info(
            $"桌宠大小：{finalScale:P1}，" +
            $"显示尺寸 {PetWidth * finalScale:F1}×{PetHeight * finalScale:F1} DIP，" +
            $"设置保存：{saved}");
    }

    private void ApplyPetSizeScale(
        double scale,
        bool persist,
        bool preservePosition)
    {
        var normalizedScale = NormalizePetSizeScale(scale);
        var fixedAnchor = preservePosition
            ? _petSizePreviewAnchor ?? CapturePetSizeAnchor(preservePosition: true)
            : null;
        _petSizePersistTimer.Stop();
        _isPetSizeTransitioning = false;
        _isPetSizePreviewSessionActive = false;
        _petSizeEnvelopePrepared = false;
        _petSizeSettingsDirty = false;
        _petSizeCommitPending = false;
        _petSizeTodoPositionNeedsUpdate = false;
        _petSizeVelocity = 0;
        _petSizeTransitionStartVelocity = 0;
        var displayedWidth = PetWidth * normalizedScale;
        var displayedHeight = PetHeight * normalizedScale;
        _petSizeScale = normalizedScale;
        _petSizeTargetScale = normalizedScale;
        _petSizeTransitionStartScale = normalizedScale;
        _petSizePreviewBaseScale = normalizedScale;
        _petSizePreviewAnchor = null;
        _petSizeTodoChildOnLeft = null;
        PetUserSizeScale.ScaleX = 1;
        PetUserSizeScale.ScaleY = 1;
        PetUserSizeOffset.X = 0;
        PetUserSizeOffset.Y = 0;
        ConfigurePetSizeViewboxAnchor(fixedAnchor);
        PetSizeViewbox.Width = displayedWidth;
        PetSizeViewbox.Height = displayedHeight;
        ApplyPetSizeWindowBounds(normalizedScale, fixedAnchor);
        _todoWindow.SetPetSizeScale(normalizedScale);
        QueueTodoWindowPositionUpdate();
        UpdateVisualClockSubscription();

        if (persist)
        {
            SaveSettings();
        }

        AppLogger.Info(
            $"桌宠大小：{normalizedScale:P1}，" +
            $"显示尺寸 {displayedWidth:F1}×{displayedHeight:F1} DIP");
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
                _activeClip = null;
                _activeFrameIndex = -1;
                _activeFrameDeadlineTimestamp = 0;
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
        var scaleToPersist = NormalizePetSizeScale(
            _petSizeSettingsDirty ? _petSizeTargetScale : _petSizeScale);
        var saved = _settingsStore.Save(new AppSettings
        {
            EdgeRoamingEnabled = _edgeRoamingEnabled,
            PetSizeScale = scaleToPersist
        });
        if (saved)
        {
            _persistedPetSizeScale = scaleToPersist;
            if (Math.Abs(_petSizeTargetScale - scaleToPersist) < 0.0005)
            {
                _petSizeSettingsDirty = false;
            }
        }

        return saved;
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
        Wriggle
    }

    private enum RoamVisualDirection
    {
        None,
        Horizontal,
        VerticalUp,
        VerticalDown
    }

    private readonly record struct PetSizeAnchor(
        Rect WorkArea,
        bool PreserveLeftEdge,
        bool PreserveRightEdge,
        bool PreserveTopEdge,
        bool PreserveBottomEdge,
        double Horizontal,
        double Vertical);

    private readonly record struct PetSizeMotionState(
        double Scale,
        double Velocity);

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
        string PreviewResource,
        int Width,
        int Height,
        int UncompressedByteCount,
        int CompressedByteCount,
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
        string PreviewResourcePath,
        int Width,
        int Height,
        int UncompressedByteCount,
        int CompressedByteCount,
        IReadOnlyDictionary<string, SpriteFrame> Frames);

    private sealed record AnimationFrame(
        SpriteFrame Image,
        TimeSpan HoldDuration,
        string Name);
}
