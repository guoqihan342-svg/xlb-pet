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
    private const int MaximumDecodedSpritePageBytes = 24 * 1024 * 1024;
    private const int WakeFrameCount = 27;
    private const int ActionPoseFrameCount = 24;
    private const int ActionLoopPoseCount = 4;
    private const int ActionLoopCycleCount = 43;
    private const int EdgePeekFrameCount = 4;
    private const double PetSizeSpringAngularFrequency = 28;
    private const double MaximumPetSizeVelocity = 4;
    private static readonly TimeSpan MotionFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan TodoMotionFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan ActionLoopFrameInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan EdgePeekFrameInterval = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan EdgePeekEndpointHold = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ActionTransitionDuration = TimeSpan.Zero;
    private static readonly TimeSpan PetSizeTransitionDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PetSizePersistDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan FrameBlendDuration = TimeSpan.Zero;
    private static readonly TimeSpan EdgeFrameBlendDuration = TimeSpan.Zero;
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);
    private static readonly string[] ActionNames =
    [
        "yawn", "cry", "cute", "like", "eat", "wave", "think"
    ];
    private static readonly HashSet<string> ActionsWithEntryBridge = new(
        ActionNames,
        StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, int[]> ActionBridgeAfterFrames =
        new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["yawn"] = [6],
            ["cry"] = [3],
            ["think"] = [6]
        };

    private readonly IReadOnlyDictionary<string, SpriteAtlasPage> _spritePages;
    private byte[] _spritePagePixels;
    private byte[] _spritePagePrefetchPixels;
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
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip _todoEnterClip;
    private readonly AnimationClip _todoExitClip;
    private readonly AnimationClip?[] _automaticActivities;
    private readonly DispatcherTimer _automaticTimer;
    private readonly DispatcherTimer _petSizePersistTimer;
    private readonly DispatcherTimer _spritePagePrefetchDispatchTimer;
    private readonly Queue<int> _automaticActivityBag = new();
    private readonly Random _random = new();
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly TodoStore _todoStore = TodoStore.CreateDefault();
    private readonly AppSettingsStore _settingsStore = AppSettingsStore.CreateDefault();
    private readonly TodoWindow _todoWindow;
    private readonly OwnedWindowPositioner.PositionCache _todoWindowPositionCache;
    private readonly Action _processOutsideTodoCloseAction;

    private BubbleMode _bubbleMode;
    private Point _pointerDownPosition;
    private bool _pointerDown;
    private bool _dragStarted;
    private bool _dragInteractionActive;
    private AnimationClip? _activeClip;
    private int _activeFrameIndex = -1;
    private long _activeClipStartedTimestamp;
    private long _activeFrameDeadlineTimestamp;
    private AnimationClip? _deferredActiveClipClock;
    private SpriteFrame? _deferredActiveClipClockFrame;
    private int _deferredActiveClipClockFrameIndex = -1;
    private TimeSpan _deferredActiveClipClockHoldDuration;
    private int _nextClipIndex;
    private int _lastAutomaticActivityIndex = -1;
    private int _outsideTodoCloseGeneration;
    private int _outsideTodoCloseScheduledGeneration;
    private int _spritePagePrefetchGeneration;
    private int _edgePeekFrameIndex;
    private int _edgePeekFrameDirection = 1;
    private long _edgePeekFrameDeadlineTimestamp;
    private EdgeDock _edgeDock;
    private double _petSizeScale = 1;
    private double _persistedPetSizeScale = 1;
    private double _petSizePreviewBaseScale = 1;
    private double _petSizeTransitionStartScale = 1;
    private double _petSizeTransitionStartVelocity;
    private double _petSizeVelocity;
    private double _petSizeTargetScale = 1;
    private double _pendingPetSizeTargetScale = 1;
    private long _petSizeTransitionStartedTimestamp;
    private long _pendingPetSizeTargetTimestamp;
    private bool _isPetSizeTransitioning;
    private bool _isPetSizePreviewSessionActive;
    private bool _petSizeEnvelopePrepared;
    private bool _petSizeEnvelopePreparationPending;
    private bool _petSizeTargetUpdatePending;
    private bool _isPetSizeAdjustmentActive;
    private bool _petSizeAdjustmentValueChanged;
    private bool _petSizeCommitPending;
    private bool _petSizeTodoPositionNeedsUpdate;
    private bool _petSizeSettingsDirty;
    private bool _isApplyingPetSizeLayout;
    private bool _todoPositionUpdateQueued;
    private bool _outsideTodoCloseQueued;
    private PetSizeAnchor? _petSizePreviewAnchor;
    private PetSizeAnchor? _petSizeLogicalAnchor;
    private bool? _petSizeTodoChildOnLeft;
    private bool _automaticAnimationEnabled;
    private bool _isPillowBreathing;
    private bool _isClosing;
    private bool _suppressTodoWindowDeactivate;
    private bool _displaySettingsSubscribed;
    private SpriteFrame? _currentSpriteFrame;
    private SpriteFrame? _pendingSpriteFrame;
    private TimeSpan _pendingSpriteFrameBlendDuration;
    private string? _loadedSpritePageName;
    private int _loadedSpritePageStride;
    private string? _prefetchedSpritePageName;
    private int _prefetchedSpritePageStride;
    private string? _desiredSpritePageName;
    private string? _failedSpritePageName;
    private CancellationTokenSource? _spritePagePrefetchCancellation;
    private Task<SpritePageLoadResult>? _spritePagePrefetchTask;
    private bool _isFrameBlending;
    private bool _isVisualClockSubscribed;
    private bool _isInsideVisualRenderingCallback;
    private string? _renderDeferredSpritePageName;
    private bool _renderDeferredSpritePageUrgent;
    private bool _renderDeferredSpritePageCancellation;
    private long _frameBlendStartedTimestamp;
    private TimeSpan _activeFrameBlendDuration;
    private TimeSpan? _nextFrameBlendDuration;
    private TimeSpan _nextFrameMinimumHold;

    public MainWindow()
    {
        InitializeComponent();
        _processOutsideTodoCloseAction = ProcessOutsideTodoClose;
        _spritePagePrefetchDispatchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _spritePagePrefetchDispatchTimer.Tick +=
            SpritePagePrefetchDispatchTimer_Tick;

        _spritePages = LoadSpritePages(BuildSpriteResourcePaths());
        _spritePagePixels = new byte[_spritePages.Values.Max(page =>
            checked(page.Width * page.Height * 4))];
        _spritePagePrefetchPixels = new byte[_spritePagePixels.Length];
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
        var edgeLeftSourceFrames = LoadFrameSequence(
            "idle",
            "luban-edge-left",
            EdgePeekFrameCount);
        // left-02 has a visibly smaller independently-redrawn outline. Skip it
        // in the live loop so the edge pose no longer appears to shrink by
        // roughly nine DIPs between two 70 ms samples.
        _edgeLeftFrames = new[]
        {
            edgeLeftSourceFrames[0],
            edgeLeftSourceFrames[3],
            edgeLeftSourceFrames[2],
            edgeLeftSourceFrames[3]
        };
        _edgeTopFrames = LoadFrameSequence("idle", "luban-edge-top", EdgePeekFrameCount);
        _edgeBottomFrames = LoadFrameSequence("idle", "luban-edge-bottom", EdgePeekFrameCount);
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
        if (!_spritePages.TryGetValue(_idleFrame.PageName, out var idlePage))
        {
            throw new InvalidOperationException(
                $"Missing startup sprite atlas page: {_idleFrame.PageName}");
        }

        // Prime exactly one startup page before CompositionTarget.Rendering is
        // ever subscribed. All runtime page changes use the asynchronous path.
        LoadSpritePageIntoBuffer(_idleFrame.PageName, idlePage);
        ShowStableFrame(_idleFrame);

        foreach (var item in _todoStore.Load())
        {
            _todos.Add(item);
        }

        _todoWindow = new TodoWindow
        {
            Todos = _todos
        };
        _todoWindowPositionCache = new OwnedWindowPositioner.PositionCache(_todoWindow);
        _todoWindow.AddRequested += TodoWindow_AddRequested;
        _todoWindow.TodoChanged += TodoWindow_TodoChanged;
        _todoWindow.DeleteRequested += TodoWindow_DeleteRequested;
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
        _petSizeScale = NormalizePetSizeScale(settings.PetSizeScale);
        _persistedPetSizeScale = _petSizeScale;
        _petSizeTargetScale = _petSizeScale;
        _todoWindow.SetPetSizeScale(_petSizeScale);
        ApplyPetSizeScale(_petSizeScale, persist: false, preservePosition: false);

        _automaticTimer = new DispatcherTimer
        {
            Interval = AutomaticAnimationInterval
        };
        _automaticTimer.Tick += AutomaticTimer_Tick;
        AppLogger.Info(
            $"主窗口初始化完成，已加载 {_reactionClips.Length} 组动作补帧");
        AppLogger.Info(
            $"渲染管线：{DisplayPixelWidth}×{DisplayPixelHeight} 固定完整帧，" +
            "单缓冲预乘 Alpha 淡化，活动过渡跟随屏幕刷新率");
    }

    private static void LogInfo(string message)
    {
        // AppLogger.Info only publishes to its bounded background queue. State
        // transitions may allocate their one summary string, but the Rendering
        // call graph never creates a captured Func/Action or performs disk I/O.
        AppLogger.Info(message);
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
        var timeline = BuildActionTimeline(actionName);

        var frames = new List<AnimationFrame>(
            (timeline.Frames.Length - 1) * 2 +
            ActionLoopPoseCount * ActionLoopCycleCount);
        for (var timelineIndex = 1;
             timelineIndex < timeline.Frames.Length;
             timelineIndex++)
        {
            frames.Add(new AnimationFrame(
                timeline.Frames[timelineIndex],
                MotionFrameInterval,
                timeline.Names[timelineIndex]));
        }

        var actionFrameIndex = frames.Count - 1;
        var loopPoseNumbers = actionName == "cute"
            ? new[] { 21, 22, 24, 22 }
            : new[] { 21, 22, 23, 24 };
        for (var cycle = 0; cycle < ActionLoopCycleCount; cycle++)
        {
            for (var poseOffset = 0; poseOffset < loopPoseNumbers.Length; poseOffset++)
            {
                var poseNumber = loopPoseNumbers[poseOffset];
                var timelineIndex = timeline.PoseIndices[poseNumber];
                frames.Add(new AnimationFrame(
                    timeline.Frames[timelineIndex],
                    ActionLoopFrameInterval,
                    timeline.Names[timelineIndex]));
            }
        }

        var returnStartIndex = timeline.PoseIndices[loopPoseNumbers[^1]] - 1;
        for (var timelineIndex = returnStartIndex;
             timelineIndex >= 0;
             timelineIndex--)
        {
            frames.Add(new AnimationFrame(
                timeline.Frames[timelineIndex],
                MotionFrameInterval,
                timeline.Names[timelineIndex]));
        }

        return new AnimationClip(message, actionName, frames.ToArray(), actionFrameIndex);
    }

    private AnimationClip CreateTodoExitClip()
    {
        var timeline = BuildActionTimeline("think");
        var frames = new List<AnimationFrame>(timeline.Frames.Length);
        for (var timelineIndex = timeline.Frames.Length - 1;
             timelineIndex >= 0;
             timelineIndex--)
        {
            frames.Add(new AnimationFrame(
                timeline.Frames[timelineIndex],
                TodoMotionFrameInterval,
                timeline.Names[timelineIndex]));
        }

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
            ActionFrameIndex: _todoExitClip.Frames.Length - 1);
    }

    private ActionTimeline BuildActionTimeline(string actionName)
    {
        var frames = new List<SpriteFrame>(
            WakeFrameCount + ActionPoseFrameCount + 4);
        var names = new List<string>(frames.Capacity);
        var poseIndices = new int[ActionPoseFrameCount + 1];

        void Add(string pageName, string resourcePath)
        {
            frames.Add(GetSpriteFrame(pageName, resourcePath));
            names.Add(Path.GetFileName(resourcePath));
        }

        Add("idle", "Assets/luban-idle.png");
        for (var wakeFrameNumber = 1;
             wakeFrameNumber <= WakeFrameCount;
             wakeFrameNumber++)
        {
            Add("idle", $"Assets/luban-wake-{wakeFrameNumber:00}.png");
        }

        var actionPageName = $"action-{actionName}";
        if (ActionsWithEntryBridge.Contains(actionName))
        {
            Add(actionPageName, $"Assets/luban-{actionName}-entry-bridge.png");
        }

        ActionBridgeAfterFrames.TryGetValue(actionName, out var bridgeAfterFrames);
        for (var actionFrameNumber = 1;
             actionFrameNumber <= ActionPoseFrameCount;
             actionFrameNumber++)
        {
            poseIndices[actionFrameNumber] = frames.Count;
            Add(
                actionPageName,
                $"Assets/luban-{actionName}-frame-{actionFrameNumber:00}.png");
            if (bridgeAfterFrames is not null &&
                Array.IndexOf(bridgeAfterFrames, actionFrameNumber) >= 0)
            {
                Add(
                    actionPageName,
                    $"Assets/luban-{actionName}-bridge-" +
                    $"{actionFrameNumber:00}-{actionFrameNumber + 1:00}.png");
            }
        }

        return new ActionTimeline(frames.ToArray(), names.ToArray(), poseIndices);
    }

    private static IEnumerable<string> BuildActionResourcePaths(string actionName)
    {
        if (ActionsWithEntryBridge.Contains(actionName))
        {
            yield return $"Assets/luban-{actionName}-entry-bridge.png";
        }

        ActionBridgeAfterFrames.TryGetValue(actionName, out var bridgeAfterFrames);
        for (var frameNumber = 1;
             frameNumber <= ActionPoseFrameCount;
             frameNumber++)
        {
            yield return $"Assets/luban-{actionName}-frame-{frameNumber:00}.png";
            if (bridgeAfterFrames is not null &&
                Array.IndexOf(bridgeAfterFrames, frameNumber) >= 0)
            {
                yield return $"Assets/luban-{actionName}-bridge-" +
                             $"{frameNumber:00}-{frameNumber + 1:00}.png";
            }
        }
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

        foreach (var actionName in ActionNames)
        {
            resourcePaths.AddRange(BuildActionResourcePaths(actionName));
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

        ValidateSpriteAtlasDecodedPageLimit(manifest.MaxDecodedPageBytes);

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
            ValidateSpriteAtlasPageDecodedSize(
                pageName,
                pageDescriptor.Width,
                pageDescriptor.Height,
                pageDescriptor.UncompressedByteCount,
                pageDescriptor.CompressedByteCount,
                manifest.MaxDecodedPageBytes);
            if (string.IsNullOrWhiteSpace(pageName) ||
                string.IsNullOrWhiteSpace(pageDescriptor.Resource) ||
                string.IsNullOrWhiteSpace(pageDescriptor.PreviewResource) ||
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

    private static void ValidateSpriteAtlasDecodedPageLimit(int maxDecodedPageBytes)
    {
        if (maxDecodedPageBytes is <= 0 or > MaximumDecodedSpritePageBytes)
        {
            throw new InvalidOperationException(
                "精灵图集分页清单的maxDecodedPageBytes必须为正数且不超过24MiB。");
        }
    }

    private static void ValidateSpriteAtlasPageDecodedSize(
        string pageName,
        int width,
        int height,
        int uncompressedByteCount,
        int compressedByteCount,
        int maxDecodedPageBytes)
    {
        var pixelCount = (long)width * height;
        var maximumCompressedByteCount = uncompressedByteCount > 0
            ? (long)uncompressedByteCount + uncompressedByteCount / 255L + 16
            : 0;
        if (width <= 0 || height <= 0 || pixelCount > int.MaxValue / 4 ||
            maxDecodedPageBytes <= 0 ||
            uncompressedByteCount != pixelCount * 4 ||
            uncompressedByteCount > maxDecodedPageBytes ||
            compressedByteCount <= 0 ||
            compressedByteCount > maximumCompressedByteCount)
        {
            throw new InvalidOperationException(
                $"精灵图集分页解码尺寸或LZ4压缩尺寸异常：{pageName}");
        }
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
        if (!_isApplyingPetSizeLayout)
        {
            // A drag, monitor recovery, or any other real window
            // movement establishes a new logical anchor. Size-only layout
            // writes keep the existing high-precision anchor instead of
            // feeding the snapped Window.Left/Top values back into it.
            _petSizeLogicalAnchor = null;
        }

        if (_todoWindow.IsVisible)
        {
            if (_isApplyingPetSizeLayout)
            {
                if (_isPetSizePreviewSessionActive)
                {
                    // The composition callback positions the child once after
                    // the current scale transform is visible. Avoid a second
                    // queued native move based on intermediate layout.
                    _petSizeTodoPositionNeedsUpdate = true;
                }
                else
                {
                    QueueTodoWindowPositionUpdate();
                }
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

                    ConsumeLatestPetSizeInputAt(Stopwatch.GetTimestamp());
                    if (_isPetSizePreviewSessionActive)
                    {
                        var deferSettingsSave = _isPetSizeAdjustmentActive;
                        CommitPetSizePreviewSession(persist: !deferSettingsSave);
                        if (deferSettingsSave)
                        {
                            _petSizeCommitPending = true;
                        }
                    }

                    _petSizeLogicalAnchor = null;
                    _todoWindowPositionCache.InvalidateGeometry();

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
                    RestartAutomaticCountdown();
                    AppLogger.Info("显示器配置已变化，桌宠位置已重新校准");
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

        _outsideTodoCloseScheduledGeneration = ++_outsideTodoCloseGeneration;
        if (_outsideTodoCloseQueued)
        {
            return;
        }

        _outsideTodoCloseQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            _processOutsideTodoCloseAction);
    }

    private void ProcessOutsideTodoClose()
    {
        _outsideTodoCloseQueued = false;
        if (_isClosing ||
            _outsideTodoCloseScheduledGeneration != _outsideTodoCloseGeneration ||
            _bubbleMode != BubbleMode.Todo ||
            _todoWindow.IsImeComposing ||
            _todoWindow.IsKeyboardFocusWithin ||
            _todoWindow.IsActive || IsActive ||
            _dragInteractionActive || _pointerDown)
        {
            return;
        }

        SetBubbleMode(BubbleMode.None);
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
                _todoWindowPositionCache,
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
        PersistLatestPetSizeForShutdownAt(Stopwatch.GetTimestamp());
        _isClosing = true;
        CancelSpritePagePrefetchForShutdown();
        if (_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displaySettingsSubscribed = false;
        }
        AppLogger.Info("主窗口正在关闭");
        _automaticAnimationEnabled = false;
        _petSizePersistTimer.Stop();
        _petSizePersistTimer.Tick -= PetSizePersistTimer_Tick;
        _spritePagePrefetchDispatchTimer.Stop();
        _spritePagePrefetchDispatchTimer.Tick -=
            SpritePagePrefetchDispatchTimer_Tick;
        _isPetSizeTransitioning = false;
        _isPetSizePreviewSessionActive = false;
        _petSizeEnvelopePrepared = false;
        _petSizeEnvelopePreparationPending = false;
        _petSizeTargetUpdatePending = false;
        StopVisualClock();
        StopFrameBlend(snapToTarget: false);
        _automaticTimer.Stop();
        _automaticTimer.Tick -= AutomaticTimer_Tick;
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        _edgeDock = EdgeDock.None;
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
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

        var edgePageName = GetEdgeFrames(dock)[0].PageName;
        if (string.Equals(
                _failedSpritePageName,
                edgePageName,
                StringComparison.Ordinal))
        {
            _edgeDock = EdgeDock.None;
            _edgePeekFrameDeadlineTimestamp = 0;
            RestartAutomaticCountdown();
            AppLogger.Info(
                $"边缘探头已跳过：精灵分页不可用 {edgePageName}");
            UpdateVisualClockSubscription();
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
            _activeClipStartedTimestamp = 0;
            _activeFrameDeadlineTimestamp = 0;
            ClearDeferredActiveClipClock();
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
        LogInfo($"边缘探头结束：{GetEdgeName(previousDock)}");
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
            _edgeDock != EdgeDock.None)
        {
            return false;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        _activeClip = clip;
        _activeFrameIndex = -1;
        RequestSpritePagePrefetch(
            clip.Frames[clip.ActionFrameIndex].Image.PageName,
            urgent: true);
        CuteMessageText.Text = clip.Message;
        AppLogger.Info(
            $"动作开始：{clip.ActionName}，触发方式：{(showCuteBubble ? "点击" : "自动")}");

        if (showCuteBubble && _bubbleMode != BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.Cute);
        }

        _nextFrameBlendDuration = ActionTransitionDuration;
        _nextFrameMinimumHold = ActionTransitionDuration;
        ShowActiveClipFrame(0);
        return true;
    }

    private void AutomaticTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing || !_automaticAnimationEnabled)
        {
            return;
        }

        if (_isPillowBreathing)
        {
            _automaticTimer.Stop();
            _isPillowBreathing = false;
            RestartAutomaticCountdown();
            return;
        }

        if (_activeClip is not null || _dragInteractionActive ||
            _bubbleMode == BubbleMode.Todo || _edgeDock != EdgeDock.None)
        {
            return;
        }

        _automaticTimer.Stop();
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
            _bubbleMode == BubbleMode.Todo || _edgeDock != EdgeDock.None)
        {
            return;
        }

        _automaticTimer.Interval = AutomaticAnimationInterval;
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

    private void StartPillowBreathing()
    {
        StopPillowBreathing();
        _isPillowBreathing = true;
        // Idle already depicts Luban sleeping on a pillow. Reuse the existing
        // one-shot timer for the five-second idle slot instead of creating two
        // no-op WPF scale animations. This keeps the compositor asleep and
        // avoids periodic allocations without changing any visible pixels.
        _automaticTimer.Interval = PillowAnimationDuration;
        _automaticTimer.Start();
    }

    private void StopPillowBreathing()
    {
        _isPillowBreathing = false;
        _automaticTimer.Stop();
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
        ClearDeferredActiveClipClock();
        LogInfo(
            $"动作完成：{clip.ActionName}，实际耗时 {elapsedMilliseconds:F1} ms");
        if (_bubbleMode == BubbleMode.Cute)
        {
            SetBubbleMode(BubbleMode.None);
        }
        RestartAutomaticCountdown();
        UpdateVisualClockSubscription();
    }

    private void ShowActiveClipFrame(int frameIndex)
    {
        ShowActiveClipFrameAt(frameIndex, timestamp: 0);
    }

    private void ShowActiveClipFrameAt(int frameIndex, long timestamp)
    {
        var clip = _activeClip;
        if (_isClosing || clip is null)
        {
            return;
        }

        _activeFrameIndex = frameIndex;
        var frame = clip.Frames[frameIndex];
        ClearDeferredActiveClipClock();
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
        if (_pendingSpriteFrame is SpriteFrame pendingFrame &&
            pendingFrame == frame.Image &&
            (_currentSpriteFrame is not SpriteFrame currentFrame ||
             currentFrame != frame.Image))
        {
            // A cold page retains the last stable pixels while decoding. Do not
            // spend the new clip's first-frame hold behind that old image: arm a
            // sentinel deadline and start the clip clock only when Rendering can
            // actually publish the requested frame.
            _deferredActiveClipClock = clip;
            _deferredActiveClipClockFrame = frame.Image;
            _deferredActiveClipClockFrameIndex = frameIndex;
            _deferredActiveClipClockHoldDuration = holdDuration;
            _activeClipStartedTimestamp = 0;
            _activeFrameDeadlineTimestamp = long.MaxValue;
        }
        else
        {
            StartActiveClipClockAt(
                timestamp > 0 ? timestamp : Stopwatch.GetTimestamp(),
                holdDuration);
        }

        UpdateVisualClockSubscription();
    }

    private void StartActiveClipClockAt(long timestamp, TimeSpan holdDuration)
    {
        ClearDeferredActiveClipClock();
        _activeClipStartedTimestamp = timestamp;
        _activeFrameDeadlineTimestamp = checked(
            timestamp + ToStopwatchTicks(holdDuration));
    }

    private void ClearDeferredActiveClipClock()
    {
        _deferredActiveClipClock = null;
        _deferredActiveClipClockFrame = null;
        _deferredActiveClipClockFrameIndex = -1;
        _deferredActiveClipClockHoldDuration = TimeSpan.Zero;
    }

    private void ShowStableFrame(SpriteFrame frame)
    {
        if (_currentSpriteFrame is SpriteFrame currentFrame && currentFrame == frame)
        {
            DiscardSupersededPendingSpriteFrame(frame);

            _nextFrameBlendDuration = null;
            return;
        }

        if (!_spritePages.ContainsKey(frame.PageName))
        {
            throw new KeyNotFoundException($"找不到精灵图集分页：{frame.PageName}");
        }

        if (!string.Equals(
                _loadedSpritePageName,
                frame.PageName,
                StringComparison.Ordinal))
        {
            if (string.Equals(
                    _failedSpritePageName,
                    frame.PageName,
                    StringComparison.Ordinal))
            {
                _nextFrameBlendDuration = null;
                return;
            }

            if (!TryPromotePrefetchedSpritePage(frame.PageName))
            {
                // Never read or decode a page from CompositionTarget.Rendering.
                // Keep the already displayed frame and remember only the latest
                // requested pose. The next composition pass retries after the
                // background decoder has published the page.
                var deferredBlendDuration =
                    _nextFrameBlendDuration ?? FrameBlendDuration;
                _nextFrameBlendDuration = null;
                if (_pendingSpriteFrame is not SpriteFrame previousPending ||
                    !string.Equals(
                        previousPending.PageName,
                        frame.PageName,
                        StringComparison.Ordinal))
                {
                    _pendingSpriteFrameBlendDuration = deferredBlendDuration;
                }
                else if (deferredBlendDuration > _pendingSpriteFrameBlendDuration)
                {
                    _pendingSpriteFrameBlendDuration = deferredBlendDuration;
                }

                _pendingSpriteFrame = frame;
                RequestSpritePagePrefetch(frame.PageName, urgent: true);
                UpdateVisualClockSubscription();
                return;
            }
        }

        DiscardSupersededPendingSpriteFrame(frame);
        if (_pendingSpriteFrame is SpriteFrame pending && pending == frame)
        {
            _pendingSpriteFrame = null;
            _pendingSpriteFrameBlendDuration = TimeSpan.Zero;
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

    private void DiscardSupersededPendingSpriteFrame(SpriteFrame displayedFrame)
    {
        if (_pendingSpriteFrame is not SpriteFrame pendingFrame ||
            pendingFrame == displayedFrame)
        {
            return;
        }

        _pendingSpriteFrame = null;
        _pendingSpriteFrameBlendDuration = TimeSpan.Zero;

        // A cold-page request made during Rendering has not populated
        // _desiredSpritePageName yet; it is only waiting in the one-shot
        // dispatcher signal. If a newer hot frame wins before that Tick,
        // discard the deferred request as well so it cannot start obsolete
        // decode/CTS work after the current composition pass.
        if (string.Equals(
                _renderDeferredSpritePageName,
                pendingFrame.PageName,
                StringComparison.Ordinal))
        {
            _renderDeferredSpritePageName = null;
            _renderDeferredSpritePageUrgent = false;
            if (!_renderDeferredSpritePageCancellation)
            {
                _spritePagePrefetchDispatchTimer.Stop();
            }
        }

        // A frame that can be displayed from the current page is newer than an
        // outstanding cold-page request. Invalidate that demand as well as the
        // pending pose so a completion already queued behind DragMove cannot
        // publish and replay the stale frame on the next composition pass.
        if (string.Equals(
                _desiredSpritePageName,
                pendingFrame.PageName,
                StringComparison.Ordinal))
        {
            _desiredSpritePageName = null;
            _spritePagePrefetchGeneration++;
            RequestSpritePagePrefetchCancellation();
        }

        UpdateVisualClockSubscription();
    }

    private bool TryPromotePrefetchedSpritePage(string pageName)
    {
        if (!string.Equals(
                _prefetchedSpritePageName,
                pageName,
                StringComparison.Ordinal))
        {
            return false;
        }

        // The decoder has completed before publishing _prefetchedSpritePageName,
        // so exchanging the two fixed buffers on the UI thread is atomic from
        // the renderer's perspective. Keep the old current page as a second
        // one-page cache, so the reverse half of an action can return to the
        // shared wake page without decoding it again. No third page is retained.
        var previousLoadedPageName = _loadedSpritePageName;
        var previousLoadedPageStride = _loadedSpritePageStride;
        (_spritePagePixels, _spritePagePrefetchPixels) =
            (_spritePagePrefetchPixels, _spritePagePixels);
        _loadedSpritePageName = pageName;
        _loadedSpritePageStride = _prefetchedSpritePageStride;
        _prefetchedSpritePageName = previousLoadedPageName;
        _prefetchedSpritePageStride = previousLoadedPageStride;
        if (string.Equals(_desiredSpritePageName, pageName, StringComparison.Ordinal))
        {
            _desiredSpritePageName = null;
        }

        return true;
    }

    private void TryShowPendingSpriteFrame()
    {
        TryShowPendingSpriteFrameAt(Stopwatch.GetTimestamp());
    }

    private void TryShowPendingSpriteFrameAt(long timestamp)
    {
        if (_pendingSpriteFrame is SpriteFrame pendingFrame)
        {
            _nextFrameBlendDuration = _pendingSpriteFrameBlendDuration;
            ShowStableFrame(pendingFrame);
            if (_currentSpriteFrame is SpriteFrame displayedFrame &&
                displayedFrame == pendingFrame &&
                ReferenceEquals(_activeClip, _deferredActiveClipClock) &&
                _activeFrameIndex == _deferredActiveClipClockFrameIndex &&
                _deferredActiveClipClockFrame is SpriteFrame deferredFrame &&
                deferredFrame == pendingFrame)
            {
                var holdDuration = _deferredActiveClipClockHoldDuration;
                StartActiveClipClockAt(timestamp, holdDuration);
            }
        }
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

        _isInsideVisualRenderingCallback = true;
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            TryShowPendingSpriteFrameAt(timestamp);
            AdvancePetSizeCompositionFrame(timestamp);

            if (_activeClip is not null)
            {
                AdvanceActiveClip(timestamp);
            }

            if (_edgeDock != EdgeDock.None)
            {
                AdvanceEdgePeek(timestamp);
            }

            UpdateFrameBlend(timestamp, force: false);
            UpdateVisualClockSubscription();
        }
        finally
        {
            _isInsideVisualRenderingCallback = false;
        }
    }

    private void UpdateVisualClockSubscription()
    {
        var shouldRun = !_isClosing &&
                         (_petSizeEnvelopePreparationPending ||
                          _petSizeTargetUpdatePending ||
                          _isPetSizeTransitioning ||
                          _activeClip is not null ||
                           _edgeDock != EdgeDock.None ||
                           _isFrameBlending ||
                           _pendingSpriteFrame is not null ||
                           (_petSizeTodoPositionNeedsUpdate &&
                            !_isPetSizeAdjustmentActive &&
                            _todoWindow.IsVisible));
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

        // The frame buffer stores the untransformed sprite while edge-peek
        // mirroring is a WPF transform. Materialize the exact current
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
        if (Math.Abs(inverse.M12) < 0.000_000_1 &&
            Math.Abs(inverse.M21) < 0.000_000_1)
        {
            TransformAxisAlignedPremultipliedPixels(
                sourcePixels,
                outputPixels,
                width,
                height,
                inverse);
            return;
        }

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

    private static void TransformAxisAlignedPremultipliedPixels(
        byte[] sourcePixels,
        byte[] outputPixels,
        int width,
        int height,
        Matrix inverse)
    {
        // Every production PetVisual transform is scale/mirror + translation.
        // Keep that common path out of Matrix.Transform/Sample helper calls so
        // a right-click during a mirrored edge pose does not spend a frame in general affine
        // resampling before the Todo timeline can start.
        Array.Clear(outputPixels);
        for (var destinationY = 0; destinationY < height; destinationY++)
        {
            var sourceY = (destinationY + 0.5) * inverse.M22 +
                          inverse.OffsetY - 0.5;
            if (sourceY < -1 || sourceY > height)
            {
                continue;
            }

            var y0 = (int)Math.Floor(sourceY);
            var yWeight = sourceY - y0;
            var topRowOffset = y0 >= 0 && y0 < height
                ? y0 * width * 4
                : -1;
            var bottomRowOffset = y0 + 1 >= 0 && y0 + 1 < height
                ? (y0 + 1) * width * 4
                : -1;

            var sourceX = 0.5 * inverse.M11 + inverse.OffsetX - 0.5;
            var destinationOffset = destinationY * width * 4;
            for (var destinationX = 0; destinationX < width; destinationX++)
            {
                if (sourceX >= -1 && sourceX <= width)
                {
                    var x0 = (int)Math.Floor(sourceX);
                    var xWeight = sourceX - x0;
                    var leftColumnOffset = x0 >= 0 && x0 < width ? x0 * 4 : -1;
                    var rightColumnOffset = x0 + 1 >= 0 && x0 + 1 < width
                        ? (x0 + 1) * 4
                        : -1;
                    var inverseXWeight = 1 - xWeight;
                    var inverseYWeight = 1 - yWeight;
                    var topLeftWeight = inverseXWeight * inverseYWeight;
                    var topRightWeight = xWeight * inverseYWeight;
                    var bottomLeftWeight = inverseXWeight * yWeight;
                    var bottomRightWeight = xWeight * yWeight;

                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = 0d;
                        if (topRowOffset >= 0)
                        {
                            if (leftColumnOffset >= 0)
                            {
                                value += sourcePixels[
                                    topRowOffset + leftColumnOffset + channel] *
                                    topLeftWeight;
                            }

                            if (rightColumnOffset >= 0)
                            {
                                value += sourcePixels[
                                    topRowOffset + rightColumnOffset + channel] *
                                    topRightWeight;
                            }
                        }

                        if (bottomRowOffset >= 0)
                        {
                            if (leftColumnOffset >= 0)
                            {
                                value += sourcePixels[
                                    bottomRowOffset + leftColumnOffset + channel] *
                                    bottomLeftWeight;
                            }

                            if (rightColumnOffset >= 0)
                            {
                                value += sourcePixels[
                                    bottomRowOffset + rightColumnOffset + channel] *
                                    bottomRightWeight;
                            }
                        }

                        outputPixels[destinationOffset + channel] = (byte)Math.Clamp(
                            Math.Round(value),
                            byte.MinValue,
                            byte.MaxValue);
                    }

                    var alpha = outputPixels[destinationOffset + 3];
                    outputPixels[destinationOffset] = Math.Min(
                        alpha,
                        outputPixels[destinationOffset]);
                    outputPixels[destinationOffset + 1] = Math.Min(
                        alpha,
                        outputPixels[destinationOffset + 1]);
                    outputPixels[destinationOffset + 2] = Math.Min(
                        alpha,
                        outputPixels[destinationOffset + 2]);
                }

                sourceX += inverse.M11;
                destinationOffset += 4;
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

    private void SpritePagePrefetchDispatchTimer_Tick(object? sender, EventArgs e)
    {
        _spritePagePrefetchDispatchTimer.Stop();
        try
        {
            if (_isClosing)
            {
                _renderDeferredSpritePageName = null;
                _renderDeferredSpritePageUrgent = false;
                _renderDeferredSpritePageCancellation = false;
                return;
            }

            if (_renderDeferredSpritePageCancellation)
            {
                _renderDeferredSpritePageCancellation = false;
                _spritePagePrefetchCancellation?.Cancel();
            }

            var pageName = _renderDeferredSpritePageName;
            if (pageName is not null)
            {
                var urgent = _renderDeferredSpritePageUrgent;
                _renderDeferredSpritePageName = null;
                _renderDeferredSpritePageUrgent = false;
                RequestSpritePagePrefetch(pageName, urgent);
            }
        }
        finally
        {
            _spritePagePrefetchDispatchTimer.Stop();
        }
    }

    private void RequestSpritePagePrefetch(string pageName, bool urgent)
    {
        if (_isClosing ||
            string.Equals(_loadedSpritePageName, pageName, StringComparison.Ordinal) ||
            string.Equals(_prefetchedSpritePageName, pageName, StringComparison.Ordinal))
        {
            return;
        }

        if (!urgent &&
            (_pendingSpriteFrame is not null ||
             _spritePagePrefetchTask is not null ||
             _prefetchedSpritePageName is not null))
        {
            return;
        }

        if (string.Equals(_desiredSpritePageName, pageName, StringComparison.Ordinal))
        {
            return;
        }

        if (!_spritePages.ContainsKey(pageName))
        {
            throw new KeyNotFoundException($"Missing sprite atlas page: {pageName}");
        }

        if (_isInsideVisualRenderingCallback)
        {
            // Rendering publishes only an existing string reference and two
            // value flags. A pre-created one-shot dispatcher timer creates or
            // cancels background work after the composition callback returns.
            if (_renderDeferredSpritePageName is null ||
                urgent || !_renderDeferredSpritePageUrgent)
            {
                _renderDeferredSpritePageName = pageName;
                _renderDeferredSpritePageUrgent = urgent;
            }

            if (!_spritePagePrefetchDispatchTimer.IsEnabled)
            {
                _spritePagePrefetchDispatchTimer.Start();
            }

            return;
        }

        _desiredSpritePageName = pageName;
        _spritePagePrefetchGeneration++;
        if (_spritePagePrefetchTask is not null)
        {
            RequestSpritePagePrefetchCancellation();
            return;
        }

        _prefetchedSpritePageName = null;
        _prefetchedSpritePageStride = 0;
        StartSpritePagePrefetch();
    }

    private void RequestSpritePagePrefetchCancellation()
    {
        if (_isInsideVisualRenderingCallback)
        {
            _renderDeferredSpritePageCancellation = true;
            if (!_spritePagePrefetchDispatchTimer.IsEnabled)
            {
                _spritePagePrefetchDispatchTimer.Start();
            }

            return;
        }

        _spritePagePrefetchCancellation?.Cancel();
    }

    private void StartSpritePagePrefetch()
    {
        var pageName = _desiredSpritePageName;
        if (_isClosing || pageName is null || _spritePagePrefetchTask is not null ||
            !_spritePages.TryGetValue(pageName, out var page))
        {
            return;
        }

        var generation = _spritePagePrefetchGeneration;
        var cancellation = new CancellationTokenSource();
        _spritePagePrefetchCancellation = cancellation;
        var task = Task.Run(
            () => DecodeSpritePageIntoBuffer(
                page,
                _spritePagePrefetchPixels,
                cancellation.Token),
            cancellation.Token);
        _spritePagePrefetchTask = task;

        _ = task.ContinueWith(
            completedTask =>
            {
                try
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        _ = completedTask.Exception;
                        cancellation.Dispose();
                        return;
                    }

                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() => CompleteSpritePagePrefetch(
                            pageName,
                            generation,
                            cancellation,
                            completedTask)));
                }
                catch (InvalidOperationException)
                {
                    _ = completedTask.Exception;
                    cancellation.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteSpritePagePrefetch(
        string pageName,
        int generation,
        CancellationTokenSource cancellation,
        Task<SpritePageLoadResult> completedTask)
    {
        if (ReferenceEquals(_spritePagePrefetchTask, completedTask))
        {
            _spritePagePrefetchTask = null;
            _spritePagePrefetchCancellation = null;
        }

        cancellation.Dispose();
        if (_isClosing)
        {
            _ = completedTask.Exception;
            return;
        }

        if (generation != _spritePagePrefetchGeneration ||
            completedTask.IsCanceled)
        {
            _ = completedTask.Exception;
            StartSpritePagePrefetch();
            return;
        }

        if (completedTask.IsFaulted)
        {
            var error = completedTask.Exception?.GetBaseException();
            HandleSpritePagePrefetchFailure(pageName, error?.Message);
            return;
        }

        var result = completedTask.Result;
        _prefetchedSpritePageName = pageName;
        _prefetchedSpritePageStride = result.Stride;
        _desiredSpritePageName = null;
        _failedSpritePageName = null;
        PublishSpritePageLoad(pageName, result, prefetched: true);
        UpdateVisualClockSubscription();
    }

    private void HandleSpritePagePrefetchFailure(string pageName, string? errorMessage)
    {
        _desiredSpritePageName = null;
        _failedSpritePageName = pageName;
        if (_pendingSpriteFrame is SpriteFrame pending &&
            string.Equals(pending.PageName, pageName, StringComparison.Ordinal))
        {
            _pendingSpriteFrame = null;
            _pendingSpriteFrameBlendDuration = TimeSpan.Zero;
        }

        var deferredFrameFailed =
            _deferredActiveClipClockFrame is SpriteFrame deferredFrame &&
            string.Equals(deferredFrame.PageName, pageName, StringComparison.Ordinal);
        if (deferredFrameFailed)
        {
            var failedClip = _deferredActiveClipClock;
            ClearDeferredActiveClipClock();
            if (failedClip is not null && ReferenceEquals(_activeClip, failedClip))
            {
                _activeClip = null;
                _activeFrameIndex = -1;
                _activeClipStartedTimestamp = 0;
                _activeFrameDeadlineTimestamp = 0;
                if (_bubbleMode == BubbleMode.Cute)
                {
                    HideBubbleVisuals();
                    _bubbleMode = BubbleMode.None;
                }

                RestartAutomaticCountdown();
            }
        }

        StopAnimatedStateForFailedSpritePage(pageName);

        AppLogger.Info(
            $"Sprite page prefetch failed: {pageName}, {errorMessage}");
        UpdateVisualClockSubscription();
    }

    private bool StopAnimatedStateForFailedSpritePage(string pageName)
    {
        var failedCurrentEdge = _edgeDock != EdgeDock.None &&
                                string.Equals(
                                    GetEdgeFrames(_edgeDock)[0].PageName,
                                    pageName,
                                    StringComparison.Ordinal);
        if (!failedCurrentEdge)
        {
            return false;
        }

        // The old decoded pixels are still valid. Bake the currently visible
        // mirror/offset into that fixed buffer before clearing WPF transforms,
        // then leave the exact last stable picture on screen without requesting
        // another cold page from inside the recovery path.
        BakeCurrentPetVisualTransformIntoDisplayFrame();
        if (failedCurrentEdge)
        {
            ExitEdgePeek(
                restartAutomaticCountdown: false,
                restoreIdleFrame: false);
        }

        ResetPetVisualTransforms();
        RestartAutomaticCountdown();
        UpdateVisualClockSubscription();
        return true;
    }

    private void CancelSpritePagePrefetchForShutdown()
    {
        _spritePagePrefetchGeneration++;
        _desiredSpritePageName = null;
        _renderDeferredSpritePageName = null;
        _renderDeferredSpritePageUrgent = false;
        _renderDeferredSpritePageCancellation = false;
        _pendingSpriteFrame = null;
        _pendingSpriteFrameBlendDuration = TimeSpan.Zero;
        _prefetchedSpritePageName = null;
        _prefetchedSpritePageStride = 0;
        _spritePagePrefetchDispatchTimer.Stop();
        _spritePagePrefetchCancellation?.Cancel();
    }

    private void LoadSpritePageIntoBuffer(string pageName, SpriteAtlasPage page)
    {
        var result = DecodeSpritePageIntoBuffer(
            page,
            _spritePagePixels,
            CancellationToken.None);
        PublishSpritePageLoad(pageName, result);
    }

    private SpritePageLoadResult DecodeSpritePageIntoBuffer(
        SpriteAtlasPage page,
        byte[] destination,
        CancellationToken cancellationToken)
    {
        var loadStopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var resource = Application.GetResourceStream(CreatePackUri(page.ResourcePath))
            ?? throw new InvalidOperationException(
                $"Missing sprite atlas page resource: {page.ResourcePath}");
        var stride = checked(page.Width * 4);
        var readStartedAt = Stopwatch.GetTimestamp();
        TimeSpan readElapsed;
        using (resource.Stream)
        {
            ReadExactly(
                resource.Stream,
                _spritePageCompressedBytes.AsSpan(0, page.CompressedByteCount),
                cancellationToken);
            if (resource.Stream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "LZ4 sprite page compressed length does not match the manifest.");
            }

            readElapsed = Stopwatch.GetElapsedTime(readStartedAt);
            DecodeLz4Block(
                _spritePageCompressedBytes.AsSpan(0, page.CompressedByteCount),
                destination,
                page.UncompressedByteCount,
                cancellationToken);
        }

        loadStopwatch.Stop();
        return new SpritePageLoadResult(
            stride,
            page.Width,
            page.Height,
            loadStopwatch.Elapsed,
            readElapsed);
    }

    private void PublishSpritePageLoad(
        string pageName,
        SpritePageLoadResult result,
        bool prefetched = false)
    {
        if (!prefetched)
        {
            _loadedSpritePageName = pageName;
            _loadedSpritePageStride = result.Stride;
        }

        AppLogger.Info(
            $"Sprite page {(prefetched ? "prefetched" : "loaded")}: {pageName}, " +
            $"{result.Width}x{result.Height}, {result.TotalElapsed.TotalMilliseconds:F1} ms " +
            $"(read {result.ReadElapsed.TotalMilliseconds:F1} ms, decode " +
            $"{result.TotalElapsed.TotalMilliseconds - result.ReadElapsed.TotalMilliseconds:F1} ms)");
    }

    private static void ReadExactly(
        Stream stream,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(destination[offset..]);
            if (read <= 0)
            {
                throw new EndOfStreamException("LZ4 sprite page data ended early.");
            }

            offset += read;
        }
    }

    private static void DecodeLz4Block(
        ReadOnlySpan<byte> input,
        byte[] output,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0 || expectedLength > output.Length)
        {
            throw new InvalidDataException("LZ4分页输出尺寸超出复用缓冲区。");
        }

        var inputIndex = 0;
        var outputIndex = 0;
        while (outputIndex < expectedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            // Showing an owned WPF window can synchronously pump layout/render work.
            // Do not start the Todo pose clock until that one-time work has finished,
            // otherwise a hot sprite page can spend (or skip) its first 35 ms pose
            // before the caller regains control.
            _automaticTimer.Stop();
        }

        HideBubbleVisuals();
        _bubbleMode = mode;
        ShowBubbleVisuals(mode);
        if (mode == BubbleMode.Todo)
        {
            EnterTodoVisualState();
        }

        LogInfo($"气泡状态：{previousMode} -> {mode}");

        if (mode != BubbleMode.Todo && previousMode == BubbleMode.Todo)
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
            _activeClipStartedTimestamp = 0;
            _activeFrameDeadlineTimestamp = 0;
            ClearDeferredActiveClipClock();
            AppLogger.Info(
                $"动作中止：{activeClip.ActionName}，原因：打开待办");
        }
        else
        {
            _activeFrameIndex = -1;
            _activeClipStartedTimestamp = 0;
            _activeFrameDeadlineTimestamp = 0;
            ClearDeferredActiveClipClock();
        }

        ExitEdgePeek(
            restartAutomaticCountdown: false,
            restoreIdleFrame: false);
        ResetPetVisualTransforms();
        _activeClip = _todoEnterClip;
        _activeFrameIndex = enterStartIndex - 1;
        // Todo owns the complete wake-to-think pose path, including approved
        // bridge poses. Publish adjacent poses
        // directly instead of cross-fading whole RGBA images, which creates
        // double silhouettes and shimmering semi-transparent outlines.
        _nextFrameBlendDuration = TimeSpan.Zero;
        _nextFrameMinimumHold = TimeSpan.Zero;
        AppLogger.Info("待办打开过渡开始");
        ShowActiveClipFrame(enterStartIndex);
        RequestSpritePagePrefetch(_todoFrame.PageName, urgent: true);
    }

    private int GetTodoEnterStartIndex(SpriteFrame? frame)
    {
        if (frame is not { } currentFrame)
        {
            return 0;
        }

        var exactIndex = Array.FindIndex(
            _todoEnterClip.Frames,
            animationFrame => string.Equals(
                animationFrame.Image.Name,
                currentFrame.Name,
                StringComparison.OrdinalIgnoreCase));
        if (exactIndex >= 0)
        {
            return exactIndex;
        }

        // Non-think actions and edge-peek poses are already upright. Resume
        // from the final wake pose so a right click never flashes back to the
        // sleeping pillow before entering the think sequence.
        return Array.FindIndex(
            _todoEnterClip.Frames,
            animationFrame => animationFrame.Image.Name.EndsWith(
                $"luban-wake-{WakeFrameCount:00}.png",
                StringComparison.OrdinalIgnoreCase));
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
        ResetPetVisualTransforms();
        _activeClip = _todoExitClip;
        _activeFrameIndex = exitStartIndex - 1;
        _nextFrameBlendDuration = TimeSpan.Zero;
        _nextFrameMinimumHold = TimeSpan.Zero;
        AppLogger.Info("待办收起过渡开始");
        ShowActiveClipFrame(exitStartIndex);
    }

    private void ShowStableTodoFrame()
    {
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
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
            _todoWindow.SetPetSizeScale(_petSizeScale);
            _todoWindow.Opacity = 0;
            if (!_todoWindow.IsVisible)
            {
                _todoWindow.Show();
            }

            _todoWindowPositionCache.InvalidateGeometry();
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

    private void TodoWindow_PetSizeScaleChanged(double scale)
    {
        QueuePetSizeScaleTargetAt(scale, Stopwatch.GetTimestamp());
    }

    private void TodoWindow_PetSizeAdjustmentStarted()
    {
        _isPetSizeAdjustmentActive = true;
        _petSizeAdjustmentValueChanged = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();

        if (!_isPetSizePreviewSessionActive)
        {
            var currentScale = GetPetSizeMotionStateAt(Stopwatch.GetTimestamp()).Scale;
            BeginPetSizePreviewSession(currentScale);
        }

        // Native window resize and layout are deferred to the next composition
        // frame. Mouse/key down remains constant-time and cannot stall the
        // first burst of slider input.
        UpdateVisualClockSubscription();
    }

    private void TodoWindow_PetSizeAdjustmentCompleted()
    {
        CompletePetSizeAdjustmentAt(Stopwatch.GetTimestamp());
    }

    private void CompletePetSizeAdjustmentAt(long timestamp)
    {
        // TodoWindow flushes its latest coalesced value before raising this
        // event. Consume that final target now so delayed persistence can never
        // save the preceding frame's value.
        ConsumeLatestPetSizeInputAt(timestamp);
        _isPetSizeAdjustmentActive = false;
        UpdateVisualClockSubscription();

        if (!_petSizeAdjustmentValueChanged && !_petSizeSettingsDirty)
        {
            _petSizeCommitPending = false;
            _petSizePersistTimer.Stop();
            CommitPetSizePreviewSession(persist: false);
            return;
        }

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
        QueuePetSizeScaleTargetAt(scale, Stopwatch.GetTimestamp());
    }

    private void QueuePetSizeScaleTargetAt(double scale, long timestamp)
    {
        if (_isClosing)
        {
            return;
        }

        var normalizedScale = NormalizePetSizeScale(scale);
        var previousQueuedTarget = _petSizeTargetUpdatePending
            ? _pendingPetSizeTargetScale
            : _petSizeTargetScale;
        if (_isPetSizeAdjustmentActive &&
            Math.Abs(normalizedScale - previousQueuedTarget) >= 0.0005)
        {
            _petSizeAdjustmentValueChanged = true;
        }

        _pendingPetSizeTargetScale = normalizedScale;
        _pendingPetSizeTargetTimestamp = timestamp;
        _petSizeTargetUpdatePending = true;
        _petSizePersistTimer.Stop();
        UpdateVisualClockSubscription();
    }

    private void ConsumePendingPetSizeTargetAt(long timestamp)
    {
        if (!_petSizeTargetUpdatePending)
        {
            return;
        }

        var targetScale = _pendingPetSizeTargetScale;
        var minimumTimestamp = _isPetSizeTransitioning
            ? _petSizeTransitionStartedTimestamp
            : long.MinValue;
        var effectiveTimestamp = Math.Max(timestamp, minimumTimestamp);
        var targetTimestamp = Math.Clamp(
            _pendingPetSizeTargetTimestamp,
            minimumTimestamp,
            effectiveTimestamp);
        _petSizeTargetUpdatePending = false;
        StartPetSizeScaleTransitionAt(targetScale, targetTimestamp);
    }

    private void ConsumeLatestPetSizeInputAt(long timestamp)
    {
        _todoWindow.FlushPendingPetSizeScaleChanged();
        ConsumePendingPetSizeTargetAt(timestamp);
    }

    private void PersistLatestPetSizeForShutdownAt(long timestamp)
    {
        ConsumeLatestPetSizeInputAt(timestamp);
        if (!_petSizeSettingsDirty)
        {
            return;
        }

        _petSizeScale = _petSizeTargetScale;
        if (SaveSettings())
        {
            _petSizeSettingsDirty = false;
        }
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
            // Non-gesture callers can still start a preview, but envelope
            // layout is deferred to the composition callback below. During a
            // real slider gesture the Started event has already prepared it.
            BeginPetSizePreviewSession(currentScale);
        }

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

        _petSizePersistTimer.Stop();
        if (!_isPetSizeAdjustmentActive)
        {
            _petSizePersistTimer.Start();
        }
        UpdateVisualClockSubscription();
    }

    private void BeginPetSizePreviewSession(double currentScale)
    {
        if (_isPetSizePreviewSessionActive)
        {
            return;
        }

        _petSizePreviewAnchor = CapturePetSizeAnchor(preservePosition: true);
        _petSizeTodoChildOnLeft = _todoWindow.IsVisible
            ? _todoWindow.Left < Left + Width / 2
            : null;
        _petSizePreviewBaseScale = currentScale;
        _isPetSizePreviewSessionActive = true;
        _petSizeEnvelopePrepared = false;
        _petSizeEnvelopePreparationPending = true;
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

    private void AdvancePetSizeCompositionFrame(long timestamp)
    {
        var envelopePreparedThisFrame = _petSizeEnvelopePreparationPending;
        if (_petSizeEnvelopePreparationPending)
        {
            PreparePetSizePreviewEnvelope();
        }

        // Multiple input events between two composition frames collapse into
        // one target retarget. The latest input timestamp preserves absolute
        // spring time, while the visual transform is written only below.
        ConsumePendingPetSizeTargetAt(timestamp);
        if (_isPetSizeTransitioning)
        {
            AdvancePetSizeTransition(timestamp);
        }
        else if (envelopePreparedThisFrame && _isPetSizePreviewSessionActive)
        {
            // Preparing the fixed maximum envelope can clamp that transparent
            // HWND at a work-area edge. Apply the matching render-only offset
            // even when the slider value itself did not change, so the visible
            // pet never moves on the first preview frame.
            ApplyPetSizePreviewScale(_petSizeScale);
        }

        // Keep the window that owns the Slider physically stationary while its
        // Thumb has mouse capture. Moving that HWND changes Track.PointToScreen
        // underneath the pointer and creates a sticky feedback loop during fast
        // drags. Preserve the dirty flag and follow the pet once after release.
        if (_petSizeTodoPositionNeedsUpdate &&
            !_isPetSizeAdjustmentActive &&
            _todoWindow.IsVisible)
        {
            _petSizeTodoPositionNeedsUpdate = false;
            UpdateTodoWindowPosition();
        }
    }

    private void AdvancePetSizeTransition(long timestamp)
    {
        if (!_isPetSizeTransitioning)
        {
            return;
        }

        PreparePetSizePreviewEnvelope();
        var motion = GetPetSizeMotionStateAt(timestamp);
        var completed = timestamp - _petSizeTransitionStartedTimestamp >=
                            ToStopwatchTicks(PetSizeTransitionDuration) ||
                        (Math.Abs(motion.Scale - _petSizeTargetScale) < 0.0005 &&
                         Math.Abs(motion.Velocity) < 0.005);
        if (completed)
        {
            _isPetSizeTransitioning = false;
            _petSizeVelocity = 0;
            motion = new PetSizeMotionState(_petSizeTargetScale, 0);
        }
        else
        {
            _petSizeVelocity = motion.Velocity;
        }

        // One composition callback performs at most one visual transform
        // commit, including the terminal frame. This keeps high-frequency
        // slider retargeting from turning the final render into a double write.
        ApplyPetSizePreviewScale(motion.Scale);
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
        ApplyPetSizeWindowBounds(MaximumPetSizeScale, _petSizePreviewAnchor);
        _petSizeEnvelopePrepared = true;
        _petSizeEnvelopePreparationPending = false;
        _petSizeTodoPositionNeedsUpdate = true;
    }

    private void ApplyPetSizePreviewScale(double scale)
    {
        var previousScaleX = PetUserSizeScale.ScaleX;
        var previousScaleY = PetUserSizeScale.ScaleY;
        var previousOffsetX = PetUserSizeOffset.X;
        var previousOffsetY = PetUserSizeOffset.Y;
        var baseScale = Math.Max(MinimumPetSizeScale, _petSizePreviewBaseScale);
        var visualScale = scale / baseScale;
        var previewOffset = CalculatePetSizePreviewOffset(
            scale,
            _petSizePreviewAnchor,
            new Rect(Left, Top, Width, Height));
        _petSizeScale = scale;
        PetUserSizeScale.ScaleX = visualScale;
        PetUserSizeScale.ScaleY = visualScale;
        PetUserSizeOffset.X = previewOffset.X;
        PetUserSizeOffset.Y = previewOffset.Y;
        var transformChanged =
            Math.Abs(previousScaleX - visualScale) >= 0.000001 ||
            Math.Abs(previousScaleY - visualScale) >= 0.000001 ||
            Math.Abs(previousOffsetX - previewOffset.X) >= 0.000001 ||
            Math.Abs(previousOffsetY - previewOffset.Y) >= 0.000001;
        _petSizeTodoPositionNeedsUpdate |= transformChanged;
    }

    private static Vector CalculatePetSizePreviewOffset(
        double scale,
        PetSizeAnchor? anchor,
        Rect envelopeBounds)
    {
        if (anchor is not { } fixedAnchor ||
            !double.IsFinite(envelopeBounds.Left) ||
            !double.IsFinite(envelopeBounds.Top) ||
            !double.IsFinite(envelopeBounds.Width) ||
            !double.IsFinite(envelopeBounds.Height) ||
            envelopeBounds.Width <= 0 || envelopeBounds.Height <= 0)
        {
            return default;
        }

        var displayedWidth = PetWidth * scale;
        var displayedHeight = PetHeight * scale;
        var desiredBounds = CalculatePetSizeLogicalWindowBounds(scale, fixedAnchor);
        var originX = fixedAnchor.PreserveLeftEdge
            ? 0d
            : fixedAnchor.PreserveRightEdge
                ? 1d
                : 0.5d;
        var originY = fixedAnchor.PreserveTopEdge ? 0d : 1d;
        var envelopeAnchorX = envelopeBounds.Left + originX * envelopeBounds.Width;
        var envelopeAnchorY = envelopeBounds.Top + originY * envelopeBounds.Height;
        var unoffsetLeft = envelopeAnchorX - originX * displayedWidth;
        var unoffsetTop = envelopeAnchorY - originY * displayedHeight;
        return new Vector(
            desiredBounds.Left - unoffsetLeft,
            desiredBounds.Top - unoffsetTop);
    }

    private PetSizeAnchor? CapturePetSizeAnchor(bool preservePosition)
    {
        if (!preservePosition || !IsLoaded)
        {
            return null;
        }

        var workArea = MonitorWorkArea.GetForWindow(this);
        if (_petSizeLogicalAnchor is { } logicalAnchor &&
            logicalAnchor.WorkArea == workArea)
        {
            return logicalAnchor;
        }

        var currentWidth = double.IsFinite(Width) && Width > 0 ? Width : ActualWidth;
        var currentHeight = double.IsFinite(Height) && Height > 0 ? Height : ActualHeight;
        var anchor = CreatePetSizeAnchor(
            workArea,
            new Rect(Left, Top, currentWidth, currentHeight));
        _petSizeLogicalAnchor = anchor;
        return anchor;
    }

    private static PetSizeAnchor CreatePetSizeAnchor(Rect workArea, Rect currentBounds)
    {
        var preserveLeftEdge = Math.Abs(
            currentBounds.Left - workArea.Left) <= EdgeContactTolerance;
        var preserveRightEdge = Math.Abs(
            currentBounds.Right - workArea.Right) <= EdgeContactTolerance;
        var preserveTopEdge = Math.Abs(
            currentBounds.Top - workArea.Top) <= EdgeContactTolerance;
        var preserveBottomEdge = Math.Abs(
            currentBounds.Bottom - workArea.Bottom) <= EdgeContactTolerance;
        var horizontal = preserveLeftEdge
            ? workArea.Left
            : preserveRightEdge
                ? workArea.Right
                : currentBounds.Left + currentBounds.Width / 2;
        var vertical = preserveTopEdge
            ? workArea.Top
            : preserveBottomEdge
                ? workArea.Bottom
                : currentBounds.Top + currentBounds.Height;
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

            var compositionTarget = PresentationSource.FromVisual(this)?.CompositionTarget;
            var transform = compositionTarget?.TransformToDevice ?? Matrix.Identity;
            var dpiScaleX = double.IsFinite(transform.M11) && transform.M11 > 0
                ? transform.M11
                : 1;
            var dpiScaleY = double.IsFinite(transform.M22) && transform.M22 > 0
                ? transform.M22
                : 1;
            var bounds = CalculatePetSizeWindowBounds(
                scale,
                fixedAnchor,
                dpiScaleX,
                dpiScaleY);
            Left = bounds.Left;
            Top = bounds.Top;
            _petSizeLogicalAnchor = fixedAnchor;
        }
        finally
        {
            _isApplyingPetSizeLayout = wasApplyingLayout;
        }
    }

    private static Rect CalculatePetSizeWindowBounds(
        double scale,
        PetSizeAnchor anchor,
        double dpiScaleX,
        double dpiScaleY)
    {
        var logicalBounds = CalculatePetSizeLogicalWindowBounds(scale, anchor);
        return new Rect(
            SnapDipToPhysicalPixelAtScale(logicalBounds.Left, dpiScaleX),
            SnapDipToPhysicalPixelAtScale(logicalBounds.Top, dpiScaleY),
            logicalBounds.Width,
            logicalBounds.Height);
    }

    private static Rect CalculatePetSizeLogicalWindowBounds(
        double scale,
        PetSizeAnchor anchor)
    {
        var displayedWidth = PetWidth * scale;
        var displayedHeight = PetHeight * scale;
        var workArea = anchor.WorkArea;
        var desiredLeft = anchor.PreserveLeftEdge
            ? workArea.Left
            : anchor.PreserveRightEdge
                ? workArea.Right - displayedWidth
                : anchor.Horizontal - displayedWidth / 2;
        var desiredTop = anchor.PreserveTopEdge
            ? workArea.Top
            : anchor.Vertical - displayedHeight;
        var left = Math.Clamp(
            desiredLeft,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - displayedWidth));
        var top = Math.Clamp(
            desiredTop,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - displayedHeight));
        return new Rect(left, top, displayedWidth, displayedHeight);
    }

    private static double SnapDipToPhysicalPixelAtScale(double value, double dpiScale) =>
        double.IsFinite(dpiScale) && dpiScale > 0
            ? Math.Round(value * dpiScale) / dpiScale
            : Math.Round(value);

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
        _pendingPetSizeTargetScale = finalScale;

        // Keep the preview transform intact while the transparent envelope is
        // replaced by the final native bounds. Resetting it first would expose
        // one frame at the envelope's clamped anchor before the HWND catches up.
        ApplyPetSizeWindowBounds(finalScale, fixedAnchor);
        ConfigurePetSizeViewboxAnchor(fixedAnchor);
        PetSizeViewbox.Width = PetWidth * finalScale;
        PetSizeViewbox.Height = PetHeight * finalScale;
        PetUserSizeScale.ScaleX = 1;
        PetUserSizeScale.ScaleY = 1;
        PetUserSizeOffset.X = 0;
        PetUserSizeOffset.Y = 0;
        _todoWindow.SetPetSizeScale(finalScale);

        _isPetSizePreviewSessionActive = false;
        _petSizeEnvelopePrepared = false;
        _petSizeEnvelopePreparationPending = false;
        _petSizeTargetUpdatePending = false;
        _petSizePreviewBaseScale = finalScale;
        _petSizeTransitionStartScale = finalScale;
        _petSizePreviewAnchor = null;
        _petSizeLogicalAnchor = fixedAnchor;
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
        _petSizeEnvelopePreparationPending = false;
        _petSizeTargetUpdatePending = false;
        _petSizeSettingsDirty = false;
        _petSizeCommitPending = false;
        _petSizeTodoPositionNeedsUpdate = false;
        _petSizeVelocity = 0;
        _petSizeTransitionStartVelocity = 0;
        var displayedWidth = PetWidth * normalizedScale;
        var displayedHeight = PetHeight * normalizedScale;
        _petSizeScale = normalizedScale;
        _petSizeTargetScale = normalizedScale;
        _pendingPetSizeTargetScale = normalizedScale;
        _pendingPetSizeTargetTimestamp = 0;
        _petSizeTransitionStartScale = normalizedScale;
        _petSizePreviewBaseScale = normalizedScale;
        _petSizePreviewAnchor = null;
        _petSizeLogicalAnchor = fixedAnchor;
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

    private bool SaveSettings()
    {
        var scaleToPersist = NormalizePetSizeScale(
            _petSizeSettingsDirty ? _petSizeTargetScale : _petSizeScale);
        var saved = _settingsStore.Save(new AppSettings
        {
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

    private sealed record ActionTimeline(
        SpriteFrame[] Frames,
        string[] Names,
        int[] PoseIndices);

    private sealed record SpriteAtlasManifest(
        int Version,
        int DisplayWidth,
        int DisplayHeight,
        int SourceFrameCount,
        int PageFrameCount,
        int MaxDecodedPageBytes,
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

    private readonly record struct SpritePageLoadResult(
        int Stride,
        int Width,
        int Height,
        TimeSpan TotalElapsed,
        TimeSpan ReadElapsed);

    private sealed record AnimationFrame(
        SpriteFrame Image,
        TimeSpan HoldDuration,
        string Name);
}
