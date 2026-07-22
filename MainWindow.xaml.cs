using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    // Code-only playback setting: 1.0 is the authored 60fps timing; values
    // above 1.0 play character poses faster. Rebuild after changing it.
    private const double AnimationPlaybackSpeed = 1.25;
    private const double CuteBubbleHeight = 76;
    private const double ReminderBubbleHeight = 148;
    private const double ScreenEdgeMargin = 12;
    private const double EdgeContactTolerance = 1;
    private const int DisplayPixelWidth = 399;
    private const int DisplayPixelHeight = 509;
    private const string SpriteAtlasManifestPath = "Assets/luban-sprite-pages.json";
    private const string SpriteAtlasCompression = "brotli";
    private const string SpriteAtlasDirectEncoding = "pbgra32";
    private const string SpriteAtlasDeltaSubEncoding = "pbgra32-delta-sub-v1";
    private const int DeltaSubFrameHeaderByteCount = sizeof(ushort) * 4;
    private const int SpriteFrameDescriptorValueCount = 6;
    private const int MaximumDecodedSpritePageBytes = 24 * 1024 * 1024;
    private const int MaximumSpritePagePayloadBytes = 32 * 1024 * 1024;
    // Decoded atlas pages are byte-for-byte identical regardless of whether
    // they are cached. Keep enough headroom for the complete largest action,
    // the reminder/edge hot set, and the currently displayed page without
    // retaining the entire 387 MiB decoded atlas for the lifetime of the app.
    private const long SpritePageResidentBudgetBytes = 144L * 1024 * 1024;
    private const long SpritePageIdleResidentTargetBytes = 96L * 1024 * 1024;
    private const long SpritePageCollectionThresholdBytes = 48L * 1024 * 1024;
    private const int ActionLoopFrameCount = 48;
    private const int ActionLoopCycleCount = 3;
    private const double PetSizeSpringAngularFrequency = 28;
    private const double MaximumPetSizeVelocity = 4;
    private static readonly TimeSpan MotionFrameInterval =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
    private static readonly TimeSpan TodoMotionFrameInterval = MotionFrameInterval;
    private static readonly TimeSpan ActionLoopFrameInterval = MotionFrameInterval;
    private static readonly long VisualFrameDeadlineToleranceTicks =
        ToCharacterAnimationTicks(TimeSpan.FromMilliseconds(2));
    private static readonly TimeSpan MinimumNearSixtyHzPresentationInterval =
        TimeSpan.FromSeconds(1d / 62d);
    private static readonly TimeSpan MaximumNearSixtyHzPresentationInterval =
        TimeSpan.FromSeconds(1d / 58d);
    private static readonly TimeSpan EdgePeekEndpointHold = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ActionTransitionDuration = TimeSpan.Zero;
    private static readonly TimeSpan PetSizeTransitionDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PetSizePersistDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SpritePageCollectionDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpritePageCollectionRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumSpritePageCollectionInterval =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FrameBlendDuration = TimeSpan.Zero;
    private static readonly TimeSpan EdgeFrameBlendDuration = TimeSpan.Zero;
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumReminderWakeInterval =
        TimeSpan.FromHours(12);
    private static readonly string[] ActionNames =
    [
        "yawn", "cry", "cute", "like", "eat", "wave", "think"
    ];
    private readonly IReadOnlyDictionary<string, SpriteAtlasPage> _spritePages;
    private readonly Dictionary<string, ResidentSpritePage> _residentSpritePages =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _residentSpritePageLru = new();
    private readonly HashSet<string> _pinnedSpritePageNames =
        new(StringComparer.Ordinal);
    private readonly string[] _spritePageWarmupOrder;
    private readonly SpriteFrame[] _wakeFrames;
    private readonly IReadOnlyDictionary<string, SpriteFrame[]> _actionSmoothFrames;
    private readonly IReadOnlyDictionary<string, SpriteFrame[]> _actionLoopFrames;
    private byte[] _spritePagePixels = Array.Empty<byte>();
    private readonly byte[] _spritePageCompressedBytes;
    private readonly byte[] _spritePagePayloadBytes;
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
    private readonly SpriteFrame[] _reminderEnterFrames;
    private readonly SpriteFrame[] _reminderHoldFrames;
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip _todoEnterClip;
    private readonly AnimationClip _todoExitClip;
    private readonly AnimationClip _reminderEnterClip;
    private readonly AnimationClip _reminderHoldClip;
    private readonly AnimationClip _reminderExitClip;
    private readonly AnimationClip?[] _automaticActivities;
    private readonly DispatcherTimer _automaticTimer;
    private readonly DispatcherTimer _petSizePersistTimer;
    private readonly DispatcherTimer _scheduledTaskTimer;
    private readonly DispatcherTimer _reminderSizeCommitTimer;
    private readonly DispatcherTimer _spritePagePrefetchDispatchTimer;
    private readonly DispatcherTimer _spritePageCollectionTimer;
    private readonly Queue<int> _automaticActivityBag = new();
    private readonly Random _random = new();
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly TodoStore _todoStore = TodoStore.CreateDefault();
    private readonly ObservableCollection<ScheduledTaskItem> _scheduledTasks = new();
    private ScheduledTaskStore _scheduledTaskStore = ScheduledTaskStore.CreateDefault();
    private readonly Queue<ScheduledTaskItem> _reminderQueue = new();
    private readonly HashSet<Guid> _queuedReminderIds = new();
    private readonly AppSettingsStore _settingsStore = AppSettingsStore.CreateDefault();
    private readonly TodoWindow _todoWindow;
    private readonly OwnedWindowPositioner.PositionCache _todoWindowPositionCache;
    private readonly Action _processOutsideTodoCloseAction;
    private readonly Action _processTodoWindowPositionUpdateAction;
    private readonly Action _processSystemTimeChangedAction;

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
    private int _spritePageWarmupIndex;
    private long _residentSpritePageBytes;
    private long _spritePageEvictedBytesSinceCollection;
    private long _lastSpritePageCollectionTimestamp;
    private long _spritePageCollectionDebtAtRequest;
    private int _spritePageCollectionGenerationAtRequest;
    private int _lastObservedSpritePageCollectionGeneration;
    private int _spritePageCollectionPollCount;
    private int _edgePeekFrameIndex;
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
    private bool _systemTimeChangedSubscribed;
    private bool _isReminderActive;
    private bool _isTransientPetSizeOverride;
    private bool _isRestoringReminderSize;
    private double _reminderRestoreScale = 1;
    private double _reminderFacingScaleX = 1;
    private ScheduledTaskItem? _activeReminder;
    private Func<DateTimeOffset> _nowProvider = static () => DateTimeOffset.Now;
    private SpriteFrame? _currentSpriteFrame;
    private Int32Rect? _directDisplayFrameBounds;
    private SpriteFrame? _pendingSpriteFrame;
    private TimeSpan _pendingSpriteFrameBlendDuration;
    private string? _loadedSpritePageName;
    private int _loadedSpritePageStride;
    private string? _desiredSpritePageName;
    private bool _desiredSpritePageUrgent;
    private bool _spritePageWarmupEnabled;
    private string? _failedSpritePageName;
    private CancellationTokenSource? _spritePagePrefetchCancellation;
    private Task<SpritePageLoadResult>? _spritePagePrefetchTask;
    private string? _spritePagePrefetchPageName;
    private bool _isFrameBlending;
    private bool _isVisualClockSubscribed;
    private bool _isInsideVisualRenderingCallback;
    private bool _synchronizeActiveClipToRenderingCadence;
    private TimeSpan _lastVisualRenderingTime = TimeSpan.MinValue;
    private string? _renderDeferredSpritePageName;
    private bool _renderDeferredSpritePageUrgent;
    private bool _renderDeferredSpritePageCancellation;
    private string? _renderDeferredSpritePageFailureName;
    private string? _renderDeferredSpritePageFailureReason;
    private bool _residentSpritePageTrimPending;
    private bool _residentSpritePageIdleTrimPending;
    private bool _spritePageCollectionInProgress;
    private long _frameBlendStartedTimestamp;
    private TimeSpan _activeFrameBlendDuration;
    private TimeSpan? _nextFrameBlendDuration;
    private TimeSpan _nextFrameMinimumHold;

    public MainWindow()
    {
        InitializeComponent();
        _processOutsideTodoCloseAction = ProcessOutsideTodoClose;
        _processTodoWindowPositionUpdateAction = ProcessTodoWindowPositionUpdate;
        _processSystemTimeChangedAction = ProcessSystemTimeChanged;
        _spritePagePrefetchDispatchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _spritePagePrefetchDispatchTimer.Tick +=
            SpritePagePrefetchDispatchTimer_Tick;
        _spritePageCollectionTimer = new DispatcherTimer
        {
            Interval = SpritePageCollectionDelay
        };
        _spritePageCollectionTimer.Tick += SpritePageCollectionTimer_Tick;
        _lastObservedSpritePageCollectionGeneration =
            GC.CollectionCount(GC.MaxGeneration);

        _spritePages = LoadSpritePages();
        _spritePageCompressedBytes = new byte[_spritePages.Values.Max(page =>
            page.CompressedByteCount)];
        _spritePagePayloadBytes = new byte[_spritePages.Values
            .Where(page => !string.Equals(
                page.Encoding,
                SpriteAtlasDirectEncoding,
                StringComparison.Ordinal))
            .Max(page => page.PayloadByteCount)];
        _displayFrameBuffer = new WriteableBitmap(
            DisplayPixelWidth,
            DisplayPixelHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null);
        PetSpriteBrush.ImageSource = _displayFrameBuffer;
        _idleFrame = GetSpriteFrame("idle", "Assets/luban-idle.png");
        _wakeFrames = LoadNumberedFrameSequence(
            "idle",
            "Assets/luban-wake-smooth-");
        _actionSmoothFrames = new ReadOnlyDictionary<string, SpriteFrame[]>(
            ActionNames.ToDictionary(
                actionName => actionName,
                actionName => LoadNumberedFrameSequence(
                    $"action-{actionName}",
                    $"Assets/luban-{actionName}-smooth-"),
                StringComparer.Ordinal));
        _actionLoopFrames = new ReadOnlyDictionary<string, SpriteFrame[]>(
            ActionNames.ToDictionary(
                actionName => actionName,
                actionName => LoadNumberedFrameSequence(
                    $"loop-{actionName}",
                    $"Assets/luban-{actionName}-loop-",
                    ActionLoopFrameCount),
                StringComparer.Ordinal));
        _todoFrame = _actionSmoothFrames["think"][^1];
        _edgeLeftFrames = LoadEdgeFrameSequence(
            "edge-left",
            "Assets/luban-edge-left-smooth-");
        _edgeTopFrames = LoadEdgeFrameSequence(
            "edge-top",
            "Assets/luban-edge-top-smooth-");
        _edgeBottomFrames = LoadEdgeFrameSequence(
            "edge-bottom",
            "Assets/luban-edge-bottom-smooth-");
        _reminderEnterFrames = LoadNumberedFrameSequence(
            "action-reminder-enter",
            "Assets/luban-reminder-enter-",
            expectedFrameCount: 33);
        _reminderHoldFrames = LoadNumberedFrameSequence(
            "action-reminder-hold",
            "Assets/luban-reminder-hold-",
            expectedFrameCount: 48);
        AddPinnedSpritePageNames([_idleFrame]);
        AddPinnedSpritePageNames(_reminderEnterFrames);
        AddPinnedSpritePageNames(_reminderHoldFrames);
        AddPinnedSpritePageNames(_edgeLeftFrames);
        AddPinnedSpritePageNames(_edgeTopFrames);
        AddPinnedSpritePageNames(_edgeBottomFrames);
        _spritePageWarmupOrder = BuildSpritePageWarmupOrder();
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
        _reminderEnterClip = CreateReminderEnterClip();
        _reminderHoldClip = CreateReminderHoldClip();
        _reminderExitClip = CreateReminderExitClip();
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

        if (_scheduledTaskStore.TryLoad(out var scheduledTasks))
        {
            foreach (var item in scheduledTasks)
            {
                _scheduledTasks.Add(item);
            }
        }
        else
        {
            AppLogger.Info(
                "定时任务读取失败，本次运行将保护原文件且拒绝覆盖保存");
        }

        _todoWindow = new TodoWindow
        {
            Todos = _todos,
            ScheduledTasks = _scheduledTasks
        };
        _todoWindowPositionCache = new OwnedWindowPositioner.PositionCache(_todoWindow);
        _todoWindow.AddRequested += TodoWindow_AddRequested;
        _todoWindow.TodoChanged += TodoWindow_TodoChanged;
        _todoWindow.TodoEdited += TodoWindow_TodoEdited;
        _todoWindow.TodoMoveRequested += TodoWindow_TodoMoveRequested;
        _todoWindow.TodoDragCompleted += TodoWindow_TodoDragCompleted;
        _todoWindow.DeleteRequested += TodoWindow_DeleteRequested;
        _todoWindow.ScheduledTaskAddRequested +=
            TodoWindow_ScheduledTaskAddRequested;
        _todoWindow.ScheduledTaskEditRequested +=
            TodoWindow_ScheduledTaskEditRequested;
        _todoWindow.ScheduledTaskDeleteRequested +=
            TodoWindow_ScheduledTaskDeleteRequested;
        _todoWindow.TransientInteractionCompleted +=
            TodoWindow_TransientInteractionCompleted;
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

        _reminderSizeCommitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _reminderSizeCommitTimer.Tick += ReminderSizeCommitTimer_Tick;

        _scheduledTaskTimer = new DispatcherTimer
        {
            Interval = MaximumReminderWakeInterval
        };
        _scheduledTaskTimer.Tick += ScheduledTaskTimer_Tick;

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
            $"主窗口初始化完成，已加载 {_reactionClips.Length} 组动作补帧，" +
            $"{_scheduledTasks.Count} 条定时任务");
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

    private void AddPinnedSpritePageNames(IEnumerable<SpriteFrame> frames)
    {
        foreach (var frame in frames)
        {
            _pinnedSpritePageNames.Add(frame.PageName);
        }
    }

    private string[] BuildSpritePageWarmupOrder()
    {
        var warmupPages = new List<string>();
        var seenPages = new HashSet<string>(StringComparer.Ordinal)
        {
            _idleFrame.PageName
        };

        void AddFramePages(IEnumerable<SpriteFrame> frames)
        {
            foreach (var frame in frames)
            {
                if (seenPages.Add(frame.PageName) &&
                    _spritePages.ContainsKey(frame.PageName))
                {
                    warmupPages.Add(frame.PageName);
                }
            }
        }

        // Deadline reminders and edge-peek gestures must be instantly ready.
        // They are the small fixed hot set and remain pinned after warm-up.
        AddFramePages(_reminderEnterFrames);
        AddFramePages(_reminderHoldFrames);
        AddFramePages(_edgeLeftFrames);
        AddFramePages(_edgeTopFrames);
        AddFramePages(_edgeBottomFrames);

        // Prime only the next two wake pages. The remaining action pages are
        // decoded on demand and protected for the full lifetime of that clip.
        var addedWakePages = 0;
        foreach (var frame in _wakeFrames)
        {
            if (seenPages.Add(frame.PageName) &&
                _spritePages.ContainsKey(frame.PageName))
            {
                warmupPages.Add(frame.PageName);
                addedWakePages++;
                if (addedWakePages == 2)
                {
                    break;
                }
            }
        }

        return warmupPages.ToArray();
    }

    private SpriteFrame[] LoadNumberedFrameSequence(
        string pageNamePrefix,
        string resourcePrefix,
        int? expectedFrameCount = null)
    {
        var numberedFrames = new SortedDictionary<int, SpriteFrame>();
        var matchedPageParts = new SortedSet<int>();
        foreach (var (pageName, page) in _spritePages)
        {
            if (!TryGetNumberedSequencePagePart(
                    pageName,
                    pageNamePrefix,
                    out var pagePart))
            {
                continue;
            }

            if (!matchedPageParts.Add(pagePart))
            {
                throw new InvalidOperationException(
                    $"Duplicate numbered sprite page part: {pageNamePrefix}/{pagePart:00}");
            }

            var frameCountBeforePage = numberedFrames.Count;
            foreach (var (resourcePath, frame) in page.Frames)
            {
                if (!resourcePath.StartsWith(resourcePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                const string extension = ".png";
                if (!resourcePath.EndsWith(extension, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Invalid numbered sprite resource: {resourcePath}");
                }

                var numberText = resourcePath.Substring(
                    resourcePrefix.Length,
                    resourcePath.Length - resourcePrefix.Length - extension.Length);
                if (numberText.Length == 0 ||
                    !numberText.All(character => character is >= '0' and <= '9') ||
                    !int.TryParse(numberText, out var frameNumber) ||
                    frameNumber <= 0 ||
                    !numberedFrames.TryAdd(frameNumber, frame))
                {
                    throw new InvalidOperationException(
                        $"Invalid or duplicate numbered sprite resource: {resourcePath}");
                }
            }

            if (numberedFrames.Count == frameCountBeforePage)
            {
                throw new InvalidOperationException(
                    $"Numbered sprite page contains no matching frames: {pageName}");
            }
        }

        if (matchedPageParts.Count == 0 || numberedFrames.Count == 0 ||
            (expectedFrameCount is { } exactCount &&
             numberedFrames.Count != exactCount))
        {
            throw new InvalidOperationException(
                $"Numbered sprite sequence count mismatch: {pageNamePrefix}/{resourcePrefix}, " +
                $"expected {expectedFrameCount?.ToString() ?? "one or more"}, " +
                $"actual {numberedFrames.Count}");
        }

        for (var expectedPagePart = 1;
             expectedPagePart <= matchedPageParts.Count;
             expectedPagePart++)
        {
            if (!matchedPageParts.Contains(expectedPagePart))
            {
                throw new InvalidOperationException(
                    $"Numbered sprite pages are not contiguous: " +
                    $"{pageNamePrefix}, missing part {expectedPagePart:00}");
            }
        }

        var expectedNumber = 1;
        foreach (var frameNumber in numberedFrames.Keys)
        {
            if (frameNumber != expectedNumber)
            {
                throw new InvalidOperationException(
                    $"Numbered sprite sequence is not contiguous: " +
                    $"{pageNamePrefix}/{resourcePrefix}, missing {expectedNumber:000}");
            }

            expectedNumber++;
        }

        return numberedFrames.Values.ToArray();
    }

    private SpriteFrame[] LoadEdgeFrameSequence(
        string pageNamePrefix,
        string resourcePrefix)
    {
        var frames = LoadNumberedFrameSequence(pageNamePrefix, resourcePrefix);
        if (frames.Length < 8 || frames.Length % 4 != 0)
        {
            throw new InvalidOperationException(
                $"Edge sprite sequence must contain at least 8 frames and " +
                $"be divisible into four phases: {pageNamePrefix}/{resourcePrefix}, " +
                $"actual {frames.Length}");
        }

        return frames;
    }

    private static bool TryGetNumberedSequencePagePart(
        string pageName,
        string pageNamePrefix,
        out int pagePart)
    {
        if (string.Equals(pageName, pageNamePrefix, StringComparison.Ordinal))
        {
            pagePart = 1;
            return true;
        }

        var marker = pageNamePrefix + "-part-";
        if (pageName.Length == marker.Length + 2 &&
            pageName.StartsWith(marker, StringComparison.Ordinal) &&
            pageName[marker.Length] is >= '0' and <= '9' &&
            pageName[marker.Length + 1] is >= '0' and <= '9')
        {
            pagePart = (pageName[marker.Length] - '0') * 10 +
                       (pageName[marker.Length + 1] - '0');
            return pagePart >= 2;
        }

        pagePart = 0;
        return false;
    }

    private AnimationClip CreateMotionClip(string message, string actionName)
    {
        var timeline = BuildActionTimeline(actionName);
        var loopFrames = _actionLoopFrames[actionName];
        var frames = new List<AnimationFrame>(
            (timeline.Frames.Length - 1) * 2 +
            loopFrames.Length * ActionLoopCycleCount);
        for (var timelineIndex = 1;
             timelineIndex < timeline.Frames.Length;
             timelineIndex++)
        {
            frames.Add(new AnimationFrame(
                timeline.Frames[timelineIndex],
                MotionFrameInterval,
                timeline.Names[timelineIndex]));
        }

        // Clip indices omit timeline idle at index 0, so this points to the
        // first frame resident on the action page. It is a prefetch target, not
        // the loop endpoint or the moment at which the action is considered done.
        var actionFrameIndex = timeline.ActionStartIndex - 1;
        for (var cycle = 0; cycle < ActionLoopCycleCount; cycle++)
        {
            foreach (var loopFrame in loopFrames)
            {
                frames.Add(new AnimationFrame(
                    loopFrame,
                    ActionLoopFrameInterval,
                    Path.GetFileName(loopFrame.Name)));
            }
        }

        for (var timelineIndex = timeline.Frames.Length - 2;
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

    private AnimationClip CreateReminderEnterClip()
    {
        return CreateReminderClip(
            "reminder-open",
            _reminderEnterFrames,
            reverse: false);
    }

    private AnimationClip CreateReminderHoldClip()
    {
        return CreateReminderClip(
            "reminder-hold",
            _reminderHoldFrames,
            reverse: false);
    }

    private AnimationClip CreateReminderExitClip()
    {
        return CreateReminderClip(
            "reminder-close",
            _reminderEnterFrames,
            reverse: true);
    }

    private static AnimationClip CreateReminderClip(
        string actionName,
        IReadOnlyList<SpriteFrame> sourceFrames,
        bool reverse)
    {
        var frames = new AnimationFrame[sourceFrames.Count];
        for (var index = 0; index < sourceFrames.Count; index++)
        {
            var sourceIndex = reverse
                ? sourceFrames.Count - 1 - index
                : index;
            var frame = sourceFrames[sourceIndex];
            frames[index] = new AnimationFrame(
                frame,
                MotionFrameInterval,
                Path.GetFileName(frame.Name));
        }

        return new AnimationClip(
            string.Empty,
            actionName,
            frames,
            ActionFrameIndex: 0);
    }

    private ActionTimeline BuildActionTimeline(string actionName)
    {
        var actionFrames = _actionSmoothFrames[actionName];
        var frames = new List<SpriteFrame>(1 + _wakeFrames.Length + actionFrames.Length);
        var names = new List<string>(frames.Capacity);

        void Add(SpriteFrame frame)
        {
            frames.Add(frame);
            names.Add(Path.GetFileName(frame.Name));
        }

        Add(_idleFrame);
        foreach (var wakeFrame in _wakeFrames)
        {
            Add(wakeFrame);
        }

        var actionStartIndex = frames.Count;
        foreach (var actionFrame in actionFrames)
        {
            Add(actionFrame);
        }

        return new ActionTimeline(
            frames.ToArray(),
            names.ToArray(),
            actionStartIndex);
    }

    private static string[] BuildSpriteResourcePaths(SpriteAtlasManifest manifest)
    {
        var resourcePaths = manifest.Pages.Values
            .SelectMany(page => page.Frames.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (resourcePaths.Count == 0 ||
            resourcePaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"精灵图集资源清单包含重复项，实际 {resourcePaths.Count} 帧。");
        }

        return resourcePaths.ToArray();
    }

    private static IReadOnlyDictionary<string, SpriteAtlasPage> LoadSpritePages(
        IReadOnlyList<string>? resourcePaths = null)
    {
        if (resourcePaths is { Count: 0 })
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

        // The embedded manifest is the runtime resource contract. Derive the
        // complete logical source set from it so variable-length dense action
        // sequences and their independent loop pages never rely on code counts.
        resourcePaths = BuildSpriteResourcePaths(manifest);

        ValidateSpriteAtlasDecodedPageLimit(manifest.MaxDecodedPageBytes);

        if (manifest.Version != 4 ||
            !string.Equals(
                manifest.Compression,
                SpriteAtlasCompression,
                StringComparison.Ordinal) ||
            manifest.DisplayWidth != DisplayPixelWidth ||
            manifest.DisplayHeight != DisplayPixelHeight ||
            manifest.SourceFrameCount != resourcePaths.Count ||
            manifest.PageFrameCount != resourcePaths.Count ||
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
                pageDescriptor.Encoding,
                pageDescriptor.Width,
                pageDescriptor.Height,
                pageDescriptor.UncompressedByteCount,
                pageDescriptor.PayloadByteCount,
                pageDescriptor.CompressedByteCount,
                manifest.MaxDecodedPageBytes);
            if (string.IsNullOrWhiteSpace(pageName) ||
                string.IsNullOrWhiteSpace(pageDescriptor.Resource) ||
                string.IsNullOrWhiteSpace(pageDescriptor.PreviewResource) ||
                !string.Equals(
                    pageDescriptor.Resource,
                    $"Assets/sprite-pages/luban-{pageName}.pbgra.br",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    pageDescriptor.PreviewResource,
                    $"Assets/sprite-pages/luban-{pageName}.png",
                    StringComparison.Ordinal) ||
                !IsCanonicalSha256(pageDescriptor.ContentSha256) ||
                !IsCanonicalSha256(pageDescriptor.DecodedSha256) ||
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
            var uniqueRegions = new List<SpriteAtlasRegion>(
                pageDescriptor.UniqueSpriteCount);
            var frameDescriptorValues = new int[checked(
                pageDescriptor.Frames.Count * SpriteFrameDescriptorValueCount)];
            var frameDescriptorOffset = 0;
            foreach (var (resourcePath, descriptor) in pageDescriptor.Frames)
            {
                if (!expectedResources.Contains(resourcePath) ||
                    !IsValidSpriteAtlasFrameDescriptor(
                        descriptor.X,
                        descriptor.Y,
                        descriptor.Width,
                        descriptor.Height,
                        descriptor.DestinationX,
                        descriptor.DestinationY,
                        pageDescriptor.Width,
                        pageDescriptor.Height))
                {
                    throw new InvalidOperationException(
                        $"精灵图集帧越界：{pageName}/{resourcePath}");
                }

                var region = new SpriteAtlasRegion(
                    descriptor.X,
                    descriptor.Y,
                    descriptor.Width,
                    descriptor.Height);
                if (!uniqueRegions.Contains(region))
                {
                    if (uniqueRegions.Any(existing => existing.Intersects(region)))
                    {
                        throw new InvalidOperationException(
                            $"Overlapping sprite regions are not permitted: " +
                            $"{pageName}/{resourcePath}");
                    }

                    uniqueRegions.Add(region);
                }

                if (!foundResources.Add(resourcePath))
                {
                    throw new InvalidOperationException(
                        $"Duplicate sprite resource across pages: {resourcePath}");
                }
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
                frameDescriptorValues[frameDescriptorOffset++] = descriptor.X;
                frameDescriptorValues[frameDescriptorOffset++] = descriptor.Y;
                frameDescriptorValues[frameDescriptorOffset++] = descriptor.Width;
                frameDescriptorValues[frameDescriptorOffset++] = descriptor.Height;
                frameDescriptorValues[frameDescriptorOffset++] = descriptor.DestinationX;
                frameDescriptorValues[frameDescriptorOffset++] = descriptor.DestinationY;
            }

            if (uniqueRegions.Count != pageDescriptor.UniqueSpriteCount ||
                (string.Equals(
                     pageDescriptor.Encoding,
                     SpriteAtlasDeltaSubEncoding,
                     StringComparison.Ordinal) &&
                 pageDescriptor.PayloadByteCount < checked(
                     pageDescriptor.Frames.Count * DeltaSubFrameHeaderByteCount)))
            {
                throw new InvalidOperationException(
                    $"Sprite page region or delta payload declaration is invalid: {pageName}");
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
                    pageDescriptor.PayloadByteCount,
                    pageDescriptor.CompressedByteCount,
                    pageDescriptor.Encoding,
                    pageDescriptor.ContentSha256,
                    pageDescriptor.DecodedSha256,
                    frameDescriptorValues,
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
        string encoding,
        int width,
        int height,
        int uncompressedByteCount,
        int payloadByteCount,
        int compressedByteCount,
        int maxDecodedPageBytes)
    {
        var pixelCount = (long)width * height;
        if (!IsSupportedSpriteAtlasEncoding(encoding) ||
            width <= 0 || height <= 0 || pixelCount > int.MaxValue / 4 ||
            maxDecodedPageBytes <= 0 ||
            uncompressedByteCount != pixelCount * 4 ||
            uncompressedByteCount > maxDecodedPageBytes ||
            payloadByteCount <= 0 ||
            payloadByteCount > MaximumSpritePagePayloadBytes ||
            (string.Equals(
                 encoding,
                 SpriteAtlasDirectEncoding,
                 StringComparison.Ordinal) &&
             payloadByteCount != uncompressedByteCount) ||
            compressedByteCount <= 0 ||
            // A compressed page larger than its declared payload is never a
            // useful runtime asset, regardless of Brotli overhead.
            compressedByteCount > payloadByteCount)
        {
            throw new InvalidOperationException(
                $"精灵图集分页解码尺寸或Brotli压缩尺寸异常：{pageName}");
        }
    }

    private static bool IsCanonicalSha256(string? value)
    {
        return value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsSupportedSpriteAtlasEncoding(string? encoding)
    {
        return string.Equals(
                   encoding,
                   SpriteAtlasDirectEncoding,
                   StringComparison.Ordinal) ||
               string.Equals(
                   encoding,
                   SpriteAtlasDeltaSubEncoding,
                   StringComparison.Ordinal);
    }

    private static bool IsValidSpriteAtlasFrameDescriptor(
        int x,
        int y,
        int width,
        int height,
        int destinationX,
        int destinationY,
        int atlasWidth,
        int atlasHeight)
    {
        return width > 0 && height > 0 &&
               x >= 0 && y >= 0 &&
               (long)x + width <= atlasWidth &&
               (long)y + height <= atlasHeight &&
               destinationX < DisplayPixelWidth &&
               destinationY < DisplayPixelHeight &&
               (long)destinationX + width > 0 &&
               (long)destinationY + height > 0;
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

        if (!_systemTimeChangedSubscribed)
        {
            SystemEvents.TimeChanged += SystemEvents_TimeChanged;
            _systemTimeChangedSubscribed = true;
        }

        var workArea = MonitorWorkArea.GetForWindow(this);
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - ScreenEdgeMargin);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - ScreenEdgeMargin);
        _automaticAnimationEnabled = true;
        ProcessScheduledTasksAt(_nowProvider());
        RestartAutomaticCountdown();
        _spritePageWarmupEnabled = true;
        ResumeSpritePageWarmup();
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
                        var deferSettingsSave = _isPetSizeAdjustmentActive ||
                                                _isTransientPetSizeOverride;
                        CommitPetSizePreviewSession(persist: !deferSettingsSave);
                        if (deferSettingsSave)
                        {
                            _petSizeCommitPending = !_isTransientPetSizeOverride;
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

    private void SystemEvents_TimeChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                _processSystemTimeChangedAction);
        }
        catch (InvalidOperationException)
        {
            // The application is already shutting down.
        }
    }

    private void ProcessSystemTimeChanged()
    {
        if (_isClosing)
        {
            return;
        }

        ProcessScheduledTasksAt(_nowProvider());
        AppLogger.Info("系统时间已变化，定时任务触发点已重新校准");
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
             _todoWindow.IsTransientPopupOpen ||
             _todoWindow.IsTodoDragInProgress ||
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
            _processTodoWindowPositionUpdateAction);
    }

    private void ProcessTodoWindowPositionUpdate()
    {
        _todoPositionUpdateQueued = false;
        if (!_isClosing && _todoWindow.IsVisible)
        {
            UpdateTodoWindowPosition();
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
        if (!_isTransientPetSizeOverride)
        {
            PersistLatestPetSizeForShutdownAt(Stopwatch.GetTimestamp());
        }
        _isClosing = true;
        CancelSpritePagePrefetchForShutdown();
        if (_displaySettingsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displaySettingsSubscribed = false;
        }
        if (_systemTimeChangedSubscribed)
        {
            SystemEvents.TimeChanged -= SystemEvents_TimeChanged;
            _systemTimeChangedSubscribed = false;
        }
        AppLogger.Info("主窗口正在关闭");
        _automaticAnimationEnabled = false;
        _petSizePersistTimer.Stop();
        _petSizePersistTimer.Tick -= PetSizePersistTimer_Tick;
        _reminderSizeCommitTimer.Stop();
        _reminderSizeCommitTimer.Tick -= ReminderSizeCommitTimer_Tick;
        _scheduledTaskTimer.Stop();
        _scheduledTaskTimer.Tick -= ScheduledTaskTimer_Tick;
        _spritePagePrefetchDispatchTimer.Stop();
        _spritePagePrefetchDispatchTimer.Tick -=
            SpritePagePrefetchDispatchTimer_Tick;
        _spritePageCollectionTimer.Stop();
        _spritePageCollectionTimer.Tick -= SpritePageCollectionTimer_Tick;
        _isPetSizeTransitioning = false;
        _isPetSizePreviewSessionActive = false;
        _petSizeEnvelopePrepared = false;
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
        _isReminderActive = false;
        _activeReminder = null;
        _reminderQueue.Clear();
        _queuedReminderIds.Clear();
        _edgeDock = EdgeDock.None;
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HideBubbleVisuals();
        _suppressTodoWindowDeactivate = true;
        _todoWindow.Deactivated -= TodoWindow_Deactivated;
        _todoWindow.CloseForApplication();
        SaveTodos();
        SaveScheduledTasks();
        ClearResidentSpritePages();
        AppLogger.Flush(TimeSpan.FromSeconds(1));
    }

    private void PetHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_isReminderActive || _isTransientPetSizeOverride)
        {
            e.Handled = true;
            return;
        }

        if (_isPetSizePreviewSessionActive)
        {
            CommitPetSizePreviewSession(persist: true);
        }

        StopPillowBreathing();
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
        if (_isReminderActive || _isTransientPetSizeOverride)
        {
            e.Handled = true;
            return;
        }

        SetBubbleMode(_bubbleMode == BubbleMode.Todo ? BubbleMode.None : BubbleMode.Todo);

        if (_bubbleMode == BubbleMode.Todo)
        {
            _todoWindow.ShowDefaultTab();
            _todoWindow.FocusInput();
        }

        e.Handled = true;
    }

    private void UpdateEdgeDockAfterDrag()
    {
        if (_isClosing || _isReminderActive)
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
        if (_isClosing || _isReminderActive || dock == EdgeDock.None)
        {
            return;
        }

        var frames = GetEdgeFrames(dock);
        var restFrameIndex = frames.Length - 1;
        var restFrame = frames[restFrameIndex];
        var edgePageName = restFrame.PageName;
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

        RequestIdleSpritePageTrim();
        _edgeDock = dock;
        _edgePeekFrameIndex = restFrameIndex;
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
        ShowStableFrame(restFrame);
        if (_currentSpriteFrame is SpriteFrame displayedFrame &&
            displayedFrame == restFrame)
        {
            StartEdgePeekFrameClockAt(Stopwatch.GetTimestamp());
        }
        else
        {
            // A cold atlas page keeps the old stable pixels on screen. Do not
            // let its logical pose clock run until this exact rest frame is
            // published by a composition pass.
            _edgePeekFrameDeadlineTimestamp = long.MaxValue;
        }

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
        if (_isReminderActive)
        {
            ExitEdgePeek(
                restartAutomaticCountdown: false,
                restoreIdleFrame: false);
            return;
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

        if (_isClosing || _edgeDock == EdgeDock.None)
        {
            return;
        }

        if (_edgePeekFrameDeadlineTimestamp == long.MaxValue)
        {
            return;
        }

        var frames = GetEdgeFrames(_edgeDock);
        var cycleDurationTicks = GetEdgePeekCycleDurationTicks(frames.Length);
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
            _edgePeekFrameIndex = (_edgePeekFrameIndex + 1) % frames.Length;
            frameChanged = true;
            _edgePeekFrameDeadlineTimestamp += ToCharacterAnimationTicks(
                GetEdgePeekFrameHoldDuration(
                    _edgePeekFrameIndex,
                    frames.Length));
        }

        if (frameChanged)
        {
            _nextFrameBlendDuration = TimeSpan.Zero;
            var targetFrame = frames[_edgePeekFrameIndex];
            ShowStableFrame(targetFrame);
            if (_currentSpriteFrame is not SpriteFrame displayedFrame ||
                displayedFrame != targetFrame)
            {
                // Atlas-page boundaries use the same presentation backpressure
                // as initial entry. A delayed decode must never make the edge
                // loop race ahead and replay a backlog of invisible poses.
                _edgePeekFrameDeadlineTimestamp = long.MaxValue;
                if (_pendingSpriteFrame is null &&
                    string.Equals(
                        _failedSpritePageName,
                        targetFrame.PageName,
                        StringComparison.Ordinal))
                {
                    HandleSpritePagePrefetchFailure(
                        targetFrame.PageName,
                        "page was previously marked unavailable");
                }
            }
        }
    }

    private static TimeSpan GetEdgePeekFrameHoldDuration(
        int frameIndex,
        int frameCount)
    {
        if (frameCount < 8 || frameCount % 4 != 0 ||
            frameIndex < 0 || frameIndex >= frameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameIndex),
                $"Invalid edge frame phase: {frameIndex}/{frameCount}");
        }

        var fullyPeekedFrameIndex = frameCount * 3 / 4 - 1;
        return frameIndex == fullyPeekedFrameIndex ||
               frameIndex == frameCount - 1
            ? EdgePeekEndpointHold
            : MotionFrameInterval;
    }

    private static long GetEdgePeekCycleDurationTicks(int frameCount)
    {
        if (frameCount < 8 || frameCount % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                $"Invalid edge frame count: {frameCount}");
        }

        return checked(
            (frameCount - 2L) * ToCharacterAnimationTicks(MotionFrameInterval) +
            2L * ToCharacterAnimationTicks(EdgePeekEndpointHold));
    }

    private void StartEdgePeekFrameClockAt(long timestamp)
    {
        if (_edgeDock == EdgeDock.None)
        {
            return;
        }

        var frames = GetEdgeFrames(_edgeDock);
        _edgePeekFrameDeadlineTimestamp = checked(
            timestamp + ToCharacterAnimationTicks(
                GetEdgePeekFrameHoldDuration(
                    _edgePeekFrameIndex,
                    frames.Length)));
    }

    private void TryStartDeferredEdgePeekClockAt(long timestamp)
    {
        if (_edgeDock == EdgeDock.None ||
            _edgePeekFrameDeadlineTimestamp != long.MaxValue)
        {
            return;
        }

        var frames = GetEdgeFrames(_edgeDock);
        if (_edgePeekFrameIndex < 0 ||
            _edgePeekFrameIndex >= frames.Length ||
            _currentSpriteFrame is not SpriteFrame displayedFrame ||
            displayedFrame != frames[_edgePeekFrameIndex])
        {
            return;
        }

        StartEdgePeekFrameClockAt(timestamp);
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
            _isReminderActive ||
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
        if (_isClosing || _isReminderActive || !_automaticAnimationEnabled)
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
            _isReminderActive ||
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
        // Composition callbacks can arrive a fraction of a millisecond before
        // their nominal 60 Hz boundary. Requiring the Stopwatch sample to be
        // strictly past that boundary creates a visible two-refresh hold
        // followed by a skipped pose. A small presentation tolerance keeps the
        // cadence locked to the compositor while deadlines still advance from
        // the original absolute timeline and therefore never drift.
        while (_activeFrameDeadlineTimestamp != long.MaxValue &&
               timestamp >= _activeFrameDeadlineTimestamp -
                            VisualFrameDeadlineToleranceTicks)
        {
            var nextFrameIndex = resolvedFrameIndex + 1;
            if (nextFrameIndex >= clip.Frames.Length)
            {
                CompleteActiveClip(clip);
                return;
            }

            var nextFrame = clip.Frames[nextFrameIndex];
            if (!IsSpritePageImmediatelyAvailable(nextFrame.Image.PageName))
            {
                // Do not let the logical clock run through poses that cannot be
                // presented yet. Keep exactly the first frame on the cold page
                // pending, then give it a complete hold interval from the
                // composition pass that actually publishes it.
                _activeFrameIndex = nextFrameIndex;
                ShowStableFrame(nextFrame.Image);
                if (_currentSpriteFrame is not SpriteFrame displayedFrame ||
                    displayedFrame != nextFrame.Image)
                {
                    DeferActiveClipClockUntilFramePresented(
                        clip,
                        nextFrame.Image,
                        nextFrameIndex,
                        nextFrame.HoldDuration,
                        resetClipStartTimestamp: false);
                    if (_pendingSpriteFrame is null &&
                        string.Equals(
                            _failedSpritePageName,
                            nextFrame.Image.PageName,
                            StringComparison.Ordinal))
                    {
                        HandleSpritePagePrefetchFailure(
                            nextFrame.Image.PageName,
                            "page was previously marked unavailable");
                    }

                    return;
                }
            }

            resolvedFrameIndex = nextFrameIndex;
            _activeFrameDeadlineTimestamp +=
                ToCharacterAnimationTicks(nextFrame.HoldDuration);
            if (_synchronizeActiveClipToRenderingCadence)
            {
                // A nominal 59/59.94/60 Hz desktop cannot present 60 distinct
                // poses against an independent 60 Hz Stopwatch phase without
                // periodically holding one pose and skipping the next. Lock
                // one pose to each healthy near-60-Hz composition instead. The
                // resulting duration differs by less than 2%, while a real
                // stall has a much larger RenderingTime gap and still uses the
                // absolute catch-up path above.
                _activeFrameDeadlineTimestamp = checked(
                    timestamp + ToCharacterAnimationTicks(nextFrame.HoldDuration));
                break;
            }
        }

        if (resolvedFrameIndex != _activeFrameIndex)
        {
            if (resolvedFrameIndex - _activeFrameIndex > 1)
            {
                _nextFrameBlendDuration = TimeSpan.Zero;
            }

            _activeFrameIndex = resolvedFrameIndex;
            ShowStableFrame(clip.Frames[resolvedFrameIndex].Image);
            PrefetchNextClipPage(clip, resolvedFrameIndex);
        }
    }

    private bool IsSpritePageImmediatelyAvailable(string pageName) =>
        _residentSpritePages.ContainsKey(pageName);

    private void PrefetchNextClipPage(AnimationClip clip, int displayedFrameIndex)
    {
        if (!ReferenceEquals(_activeClip, clip) ||
            displayedFrameIndex < 0 ||
            displayedFrameIndex >= clip.Frames.Length ||
            _currentSpriteFrame is not SpriteFrame currentFrame ||
            currentFrame != clip.Frames[displayedFrameIndex].Image)
        {
            return;
        }

        var currentPageName = currentFrame.PageName;
        for (var frameIndex = displayedFrameIndex + 1;
             frameIndex < clip.Frames.Length;
             frameIndex++)
        {
            var nextPageName = clip.Frames[frameIndex].Image.PageName;
            if (string.Equals(nextPageName, currentPageName, StringComparison.Ordinal))
            {
                continue;
            }

            currentPageName = nextPageName;
            if (IsSpritePageImmediatelyAvailable(nextPageName))
            {
                continue;
            }

            // Usually the idle warm-up has already made this a no-op. If the
            // user clicks immediately after launch, the next on-demand page
            // preempts sequential warm-up and becomes resident before this clip
            // reaches it.
            RequestSpritePagePrefetch(nextPageName, urgent: true);
            return;
        }

        if (ReferenceEquals(clip, _reminderEnterClip))
        {
            var holdPageName = _reminderHoldClip.Frames[0].Image.PageName;
            if (!IsSpritePageImmediatelyAvailable(holdPageName))
            {
                // The hold sequence starts on a different atlas page. As soon
                // as all entry pages are resident, spend the remaining entry
                // time loading it so raising the megaphone cannot end in a
                // cold-page pause.
                RequestSpritePagePrefetch(holdPageName, urgent: true);
            }
        }
    }

    private void CompleteActiveClip(AnimationClip clip)
    {
        ShowStableFrame(clip.Frames[^1].Image);
        if (!ReferenceEquals(_activeClip, clip))
        {
            return;
        }

        if (ReferenceEquals(clip, _reminderEnterClip) && _isReminderActive)
        {
            LogInfo("定时提醒举喇叭入场完成，开始播报动作");
            StartReminderHoldAnimation();
            return;
        }

        if (ReferenceEquals(clip, _reminderHoldClip) && _isReminderActive)
        {
            _activeClip = null;
            _activeFrameIndex = -1;
            _activeClipStartedTimestamp = 0;
            _activeFrameDeadlineTimestamp = 0;
            ClearDeferredActiveClipClock();
            LogInfo("定时提醒播报动作完成，保持举喇叭姿势");
            RequestIdleSpritePageTrim();
            UpdateVisualClockSubscription();
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
        if (ReferenceEquals(clip, _reminderExitClip))
        {
            ResetPetVisualTransforms();
            ShowStableFrame(_idleFrame);
        }
        RequestIdleSpritePageTrim();
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
        PrefetchNextClipPage(clip, frameIndex);
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
        // A WriteableBitmap update is not presentation. This applies equally to
        // a hot atlas page and a cold one: arm a sentinel deadline now, then
        // start the absolute Stopwatch timeline from the first distinct WPF
        // composition pass that can actually present this exact first pose.
        DeferActiveClipClockUntilFramePresented(
            clip,
            frame.Image,
            frameIndex,
            holdDuration,
            resetClipStartTimestamp: true);

        UpdateVisualClockSubscription();
    }

    private void DeferActiveClipClockUntilFramePresented(
        AnimationClip clip,
        SpriteFrame frame,
        int frameIndex,
        TimeSpan holdDuration,
        bool resetClipStartTimestamp)
    {
        _deferredActiveClipClock = clip;
        _deferredActiveClipClockFrame = frame;
        _deferredActiveClipClockFrameIndex = frameIndex;
        _deferredActiveClipClockHoldDuration = holdDuration;
        if (resetClipStartTimestamp)
        {
            _activeClipStartedTimestamp = 0;
        }

        _activeFrameDeadlineTimestamp = long.MaxValue;
    }

    private void StartActiveClipClockAt(long timestamp, TimeSpan holdDuration)
    {
        ClearDeferredActiveClipClock();
        if (_activeClipStartedTimestamp <= 0)
        {
            _activeClipStartedTimestamp = timestamp;
        }

        _activeFrameDeadlineTimestamp = checked(
            timestamp + ToCharacterAnimationTicks(holdDuration));
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
        var canBlend = _currentSpriteFrame is not null &&
                       requestedBlendDuration > TimeSpan.Zero &&
                       IsLoaded &&
                       PresentationSource.FromVisual(this) is not null;
        if (canBlend)
        {
            UpdateFrameBlend(Stopwatch.GetTimestamp(), force: true);
            CopyFramePixels(frame, _frameBlendTargetPixels);
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
            WriteDirectSpriteFrame(frame);
        }

        // Visibility would invalidate layout from inside Rendering exactly when
        // the first edge pose is published. Opacity is render-only, and changing
        // it here keeps the character pixels and pillow state atomic without a
        // one-frame flash or a layout pass on the animation clock.
        var pillowOpacity = IsEdgeSpriteFrame(frame) ? 0d : 1d;
        if (PillowImage.Opacity != pillowOpacity)
        {
            PillowImage.Opacity = pillowOpacity;
        }

        _currentSpriteFrame = frame;
    }

    private static bool IsEdgeSpriteFrame(SpriteFrame frame) =>
        frame.Name.StartsWith("Assets/luban-edge-", StringComparison.Ordinal);

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
            if (!HasDeferredSpritePageDispatchWork())
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
        if (!_residentSpritePages.TryGetValue(pageName, out var residentPage))
        {
            return false;
        }

        TouchResidentSpritePage(residentPage);
        // Decoded pages are published to the resident dictionary only on the UI
        // thread. Rendering therefore changes pages by swapping one byte[]
        // reference and its stride; it never reads a resource, decompresses a
        // page, copies an atlas, or allocates a buffer.
        _spritePagePixels = residentPage.Pixels;
        _loadedSpritePageName = pageName;
        _loadedSpritePageStride = residentPage.Stride;
        if (string.Equals(_desiredSpritePageName, pageName, StringComparison.Ordinal))
        {
            _desiredSpritePageName = null;
            _desiredSpritePageUrgent = false;
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
            if (_activeClip is { } activeClip)
            {
                PrefetchNextClipPage(activeClip, _activeFrameIndex);
            }
        }

        TryStartDeferredEdgePeekClockAt(timestamp);
        TryStartDeferredActiveClipClockAt(timestamp);
    }

    private void TryStartDeferredActiveClipClockAt(long timestamp)
    {
        if (_currentSpriteFrame is not SpriteFrame displayedFrame ||
            !ReferenceEquals(_activeClip, _deferredActiveClipClock) ||
            _activeFrameIndex != _deferredActiveClipClockFrameIndex ||
            _deferredActiveClipClockFrame is not SpriteFrame deferredFrame ||
            deferredFrame != displayedFrame)
        {
            return;
        }

        var holdDuration = _deferredActiveClipClockHoldDuration;
        StartActiveClipClockAt(timestamp, holdDuration);
    }

    private void CopyFramePixels(SpriteFrame frame, byte[] destination)
    {
        Array.Clear(destination);
        CopyFramePixels(frame, destination, GetVisibleFrameBounds(frame));
    }

    private void WriteDirectSpriteFrame(SpriteFrame frame)
    {
        var nextBounds = GetVisibleFrameBounds(frame);
        Int32Rect dirtyBounds;
        if (_directDisplayFrameBounds is { } previousBounds)
        {
            dirtyBounds = UnionPixelBounds(previousBounds, nextBounds);
            ClearPixelBounds(_displayFramePixels, dirtyBounds);
        }
        else
        {
            // A blend, baked transform, or other full-frame writer may have
            // left pixels outside any SpriteFrame descriptor. The first direct
            // pose after that state clears and submits the complete surface;
            // subsequent direct poses use only the old/new bounds union.
            dirtyBounds = new Int32Rect(
                0,
                0,
                DisplayPixelWidth,
                DisplayPixelHeight);
            Array.Clear(_displayFramePixels);
        }

        CopyFramePixels(frame, _displayFramePixels, nextBounds);
        WriteDisplayFrame(_displayFramePixels, dirtyBounds);
        _directDisplayFrameBounds = nextBounds;
    }

    private static Int32Rect GetVisibleFrameBounds(SpriteFrame frame)
    {
        var visibleLeft = Math.Max(0, frame.DestinationX);
        var visibleTop = Math.Max(0, frame.DestinationY);
        var visibleRight = Math.Min(
            DisplayPixelWidth,
            frame.DestinationX + frame.Width);
        var visibleBottom = Math.Min(
            DisplayPixelHeight,
            frame.DestinationY + frame.Height);
        return visibleRight > visibleLeft && visibleBottom > visibleTop
            ? new Int32Rect(
                visibleLeft,
                visibleTop,
                visibleRight - visibleLeft,
                visibleBottom - visibleTop)
            : Int32Rect.Empty;
    }

    private static Int32Rect UnionPixelBounds(Int32Rect first, Int32Rect second)
    {
        if (first.Width <= 0 || first.Height <= 0)
        {
            return second;
        }

        if (second.Width <= 0 || second.Height <= 0)
        {
            return first;
        }

        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private static void ClearPixelBounds(byte[] pixels, Int32Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var stride = checked(DisplayPixelWidth * 4);
        var rowBytes = checked(bounds.Width * 4);
        for (var row = 0; row < bounds.Height; row++)
        {
            Array.Clear(
                pixels,
                checked((bounds.Y + row) * stride + bounds.X * 4),
                rowBytes);
        }
    }

    private void CopyFramePixels(
        SpriteFrame frame,
        byte[] destination,
        Int32Rect visibleBounds)
    {
        if (visibleBounds.Width <= 0 || visibleBounds.Height <= 0)
        {
            return;
        }

        if (_loadedSpritePageStride <= 0)
        {
            throw new InvalidOperationException("精灵分页尚未载入像素缓冲区。");
        }

        var destinationStride = checked(DisplayPixelWidth * 4);
        var sourceX = frame.X + visibleBounds.X - frame.DestinationX;
        var sourceY = frame.Y + visibleBounds.Y - frame.DestinationY;
        var rowBytes = checked(visibleBounds.Width * 4);
        for (var row = 0; row < visibleBounds.Height; row++)
        {
            Buffer.BlockCopy(
                _spritePagePixels,
                checked((sourceY + row) * _loadedSpritePageStride + sourceX * 4),
                destination,
                checked((visibleBounds.Y + row) * destinationStride +
                        visibleBounds.X * 4),
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

        if (e is RenderingEventArgs renderingEventArgs)
        {
            var renderingTime = renderingEventArgs.RenderingTime;
            if (renderingTime == _lastVisualRenderingTime)
            {
                return;
            }

            var previousRenderingTime = _lastVisualRenderingTime;
            _lastVisualRenderingTime = renderingTime;
            if (previousRenderingTime != TimeSpan.MinValue &&
                renderingTime > previousRenderingTime)
            {
                var presentationInterval = renderingTime - previousRenderingTime;
                _synchronizeActiveClipToRenderingCadence =
                    ShouldSynchronizeActiveClipToRenderingCadence(
                        presentationInterval);
            }
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
            _synchronizeActiveClipToRenderingCadence = false;
        }
    }

    private static bool ShouldSynchronizeActiveClipToRenderingCadence(
        TimeSpan presentationInterval) =>
        // One-pose-per-composition locking is valid only for the authored 1x
        // 60fps timing. Faster or slower code-configured playback must use the
        // absolute clock so it can skip or hold poses without changing duration.
        Math.Abs(AnimationPlaybackSpeed - 1d) <= 0.0001 &&
        presentationInterval >= MinimumNearSixtyHzPresentationInterval &&
        presentationInterval <= MaximumNearSixtyHzPresentationInterval;

    private void UpdateVisualClockSubscription()
    {
        var shouldRun = !_isClosing &&
                         (_isPetSizeAdjustmentActive ||
                          _petSizeTargetUpdatePending ||
                           _isPetSizeTransitioning ||
                           _activeClip is not null ||
                            _edgeDock != EdgeDock.None ||
                           _isFrameBlending ||
                           _pendingSpriteFrame is not null ||
                            (_petSizeTodoPositionNeedsUpdate &&
                             !_isPetSizeAdjustmentActive &&
                             !_isPetSizeTransitioning &&
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

    private static long ToCharacterAnimationTicks(TimeSpan baseDuration)
    {
        if (AnimationPlaybackSpeed <= 0 ||
            !double.IsFinite(AnimationPlaybackSpeed))
        {
            throw new InvalidOperationException(
                "AnimationPlaybackSpeed must be a finite value greater than zero.");
        }

        return Math.Max(
            1,
            (long)Math.Round(
                baseDuration.TotalSeconds * Stopwatch.Frequency /
                AnimationPlaybackSpeed));
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

    private void WriteDisplayFrame(byte[] pixels, Int32Rect? dirtyBounds = null)
    {
        var updateBounds = dirtyBounds ?? new Int32Rect(
            0,
            0,
            DisplayPixelWidth,
            DisplayPixelHeight);
        _directDisplayFrameBounds = null;
        if (updateBounds.Width <= 0 || updateBounds.Height <= 0)
        {
            return;
        }

        var stride = checked(DisplayPixelWidth * 4);
        _displayFrameBuffer.WritePixels(
            updateBounds,
            pixels,
            stride,
            checked(updateBounds.Y * stride + updateBounds.X * 4));
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
                _renderDeferredSpritePageFailureName = null;
                _renderDeferredSpritePageFailureReason = null;
                _residentSpritePageTrimPending = false;
                _residentSpritePageIdleTrimPending = false;
                return;
            }

            if (_renderDeferredSpritePageCancellation)
            {
                _renderDeferredSpritePageCancellation = false;
                _spritePagePrefetchCancellation?.Cancel();
            }

            var failurePageName = _renderDeferredSpritePageFailureName;
            if (failurePageName is not null)
            {
                var failureReason = _renderDeferredSpritePageFailureReason;
                _renderDeferredSpritePageFailureName = null;
                _renderDeferredSpritePageFailureReason = null;
                HandleSpritePagePrefetchFailure(failurePageName, failureReason);
            }

            var pageName = _renderDeferredSpritePageName;
            if (pageName is not null)
            {
                var urgent = _renderDeferredSpritePageUrgent;
                _renderDeferredSpritePageName = null;
                _renderDeferredSpritePageUrgent = false;
                RequestSpritePagePrefetch(pageName, urgent);
            }

            if (_residentSpritePageTrimPending)
            {
                var reduceToIdleTarget = _residentSpritePageIdleTrimPending;
                _residentSpritePageTrimPending = false;
                _residentSpritePageIdleTrimPending = false;
                if (reduceToIdleTarget)
                {
                    TrimResidentSpritePagesToIdleTarget();
                }
                else
                {
                    TrimResidentSpritePagesToBudget();
                }
            }
        }
        finally
        {
            _spritePagePrefetchDispatchTimer.Stop();
        }
    }

    private bool HasDeferredSpritePageDispatchWork() =>
        _renderDeferredSpritePageName is not null ||
        _renderDeferredSpritePageCancellation ||
        _renderDeferredSpritePageFailureName is not null ||
        _residentSpritePageTrimPending;

    private void RequestSpritePagePrefetch(string pageName, bool urgent)
    {
        if (_isClosing ||
            _residentSpritePages.ContainsKey(pageName))
        {
            return;
        }

        if (!urgent &&
            (_pendingSpriteFrame is not null || _desiredSpritePageUrgent))
        {
            return;
        }

        if (string.Equals(_desiredSpritePageName, pageName, StringComparison.Ordinal))
        {
            _desiredSpritePageUrgent |= urgent;
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
        _desiredSpritePageUrgent = urgent;
        _spritePagePrefetchGeneration++;
        if (_spritePagePrefetchTask is not null)
        {
            // An on-demand action page always preempts sequential warm-up. The
            // warm-up cursor is not advanced until its page becomes resident,
            // so it resumes exactly where it stopped after urgent work settles.
            RequestSpritePagePrefetchCancellation();
            return;
        }

        StartSpritePagePrefetch();
    }

    private void ResumeSpritePageWarmup()
    {
        if (_isClosing || !_spritePageWarmupEnabled ||
            _spritePagePrefetchTask is not null ||
            _desiredSpritePageName is not null)
        {
            return;
        }

        while (_spritePageWarmupIndex < _spritePageWarmupOrder.Length &&
               _residentSpritePages.ContainsKey(
                   _spritePageWarmupOrder[_spritePageWarmupIndex]))
        {
            _spritePageWarmupIndex++;
        }

        if (_spritePageWarmupIndex >= _spritePageWarmupOrder.Length)
        {
            return;
        }

        RequestSpritePagePrefetch(
            _spritePageWarmupOrder[_spritePageWarmupIndex],
            urgent: false);
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
            () => DecodeSpritePage(page, cancellation.Token),
            cancellation.Token);
        _spritePagePrefetchTask = task;
        _spritePagePrefetchPageName = pageName;

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
            _spritePagePrefetchPageName = null;
        }

        cancellation.Dispose();
        if (_isClosing)
        {
            _ = completedTask.Exception;
            return;
        }

        if (completedTask.IsCanceled)
        {
            _ = completedTask.Exception;
            StartSpritePagePrefetch();
            ResumeSpritePageWarmup();
            return;
        }

        if (generation != _spritePagePrefetchGeneration)
        {
            // A warm-up decode may finish just after an urgent request has
            // superseded it. Discard either a successful or failed stale result
            // so old work cannot displace useful pages from the bounded cache.
            _ = completedTask.Exception;
            if (completedTask.IsCompletedSuccessfully)
            {
                RecordDiscardedSpritePageBytes(
                    completedTask.Result.Pixels.LongLength);
            }
            StartSpritePagePrefetch();
            ResumeSpritePageWarmup();
            return;
        }

        if (completedTask.IsFaulted)
        {
            var error = completedTask.Exception?.GetBaseException();
            HandleSpritePagePrefetchFailure(pageName, error?.Message);
            return;
        }

        var result = completedTask.Result;
        AddResidentSpritePage(pageName, result);
        if (generation == _spritePagePrefetchGeneration &&
            string.Equals(_desiredSpritePageName, pageName, StringComparison.Ordinal))
        {
            _desiredSpritePageName = null;
            _desiredSpritePageUrgent = false;
        }

        _failedSpritePageName = null;
        PublishSpritePageLoad(pageName, result, prefetched: true);
        UpdateVisualClockSubscription();
        if (_desiredSpritePageName is not null)
        {
            StartSpritePagePrefetch();
        }
        else
        {
            ResumeSpritePageWarmup();
        }
    }

    private void HandleSpritePagePrefetchFailure(string pageName, string? errorMessage)
    {
        if (_isInsideVisualRenderingCallback)
        {
            _renderDeferredSpritePageFailureName = pageName;
            _renderDeferredSpritePageFailureReason = errorMessage;
            if (!_spritePagePrefetchDispatchTimer.IsEnabled)
            {
                _spritePagePrefetchDispatchTimer.Start();
            }

            return;
        }

        _desiredSpritePageName = null;
        _desiredSpritePageUrgent = false;
        if (string.Equals(
                _renderDeferredSpritePageName,
                pageName,
                StringComparison.Ordinal))
        {
            _renderDeferredSpritePageName = null;
            _renderDeferredSpritePageUrgent = false;
        }
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
                RequestIdleSpritePageTrim();
            }
        }

        StopAnimatedStateForFailedSpritePage(pageName);

        AppLogger.Info(
            $"Sprite page prefetch failed: {pageName}, {errorMessage}");
        UpdateVisualClockSubscription();
        if (_spritePageWarmupIndex < _spritePageWarmupOrder.Length &&
            string.Equals(
                _spritePageWarmupOrder[_spritePageWarmupIndex],
                pageName,
                StringComparison.Ordinal))
        {
            _spritePageWarmupIndex++;
        }

        ResumeSpritePageWarmup();
    }

    private bool StopAnimatedStateForFailedSpritePage(string pageName)
    {
        var failedCurrentEdge = false;
        if (_edgeDock != EdgeDock.None)
        {
            var edgeFrames = GetEdgeFrames(_edgeDock);
            failedCurrentEdge = _edgePeekFrameIndex >= 0 &&
                                _edgePeekFrameIndex < edgeFrames.Length &&
                                string.Equals(
                                    edgeFrames[_edgePeekFrameIndex].PageName,
                                    pageName,
                                    StringComparison.Ordinal);
        }

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
        _spritePageWarmupEnabled = false;
        _spritePagePrefetchGeneration++;
        _desiredSpritePageName = null;
        _desiredSpritePageUrgent = false;
        _renderDeferredSpritePageName = null;
        _renderDeferredSpritePageUrgent = false;
        _renderDeferredSpritePageCancellation = false;
        _renderDeferredSpritePageFailureName = null;
        _renderDeferredSpritePageFailureReason = null;
        _residentSpritePageTrimPending = false;
        _residentSpritePageIdleTrimPending = false;
        _pendingSpriteFrame = null;
        _pendingSpriteFrameBlendDuration = TimeSpan.Zero;
        _spritePagePrefetchDispatchTimer.Stop();
        _spritePagePrefetchCancellation?.Cancel();
        _spritePagePrefetchPageName = null;
    }

    private void LoadSpritePageIntoBuffer(string pageName, SpriteAtlasPage page)
    {
        if (_residentSpritePages.TryGetValue(pageName, out var residentPage))
        {
            TouchResidentSpritePage(residentPage);
            _spritePagePixels = residentPage.Pixels;
            _loadedSpritePageName = pageName;
            _loadedSpritePageStride = residentPage.Stride;
            return;
        }

        if (_spritePagePrefetchTask is not null)
        {
            throw new InvalidOperationException(
                "Synchronous sprite decoding cannot overlap background warm-up.");
        }

        var result = DecodeSpritePage(page, CancellationToken.None);
        AddResidentSpritePage(pageName, result);
        _spritePagePixels = result.Pixels;
        PublishSpritePageLoad(pageName, result);
    }

    private SpritePageLoadResult DecodeSpritePage(
        SpriteAtlasPage page,
        CancellationToken cancellationToken)
    {
        var loadStopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var decodedPixels = new byte[page.UncompressedByteCount];
        var payloadBytes = string.Equals(
            page.Encoding,
            SpriteAtlasDirectEncoding,
            StringComparison.Ordinal)
            ? decodedPixels
            : _spritePagePayloadBytes;
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
                    "Brotli sprite page compressed length does not match the manifest.");
            }

            readElapsed = Stopwatch.GetElapsedTime(readStartedAt);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSpriteAtlasPageContentHash(
                page.ResourcePath,
                _spritePageCompressedBytes,
                page.CompressedByteCount,
                page.ContentSha256);
            cancellationToken.ThrowIfCancellationRequested();
            DecodeBrotliPage(
                _spritePageCompressedBytes.AsSpan(0, page.CompressedByteCount),
                payloadBytes,
                page.PayloadByteCount,
                cancellationToken);
            DecodeSpritePagePayload(
                page.ResourcePath,
                page.Encoding,
                payloadBytes,
                page.PayloadByteCount,
                decodedPixels,
                page.Width,
                page.Height,
                page.FrameDescriptorValues,
                page.DecodedSha256,
                cancellationToken);
        }

        loadStopwatch.Stop();
        return new SpritePageLoadResult(
            decodedPixels,
            stride,
            page.Width,
            page.Height,
            loadStopwatch.Elapsed,
            readElapsed);
    }

    private static void ValidateSpriteAtlasPageContentHash(
        string resourcePath,
        byte[] compressedBytes,
        int compressedByteCount,
        string expectedSha256)
    {
        if (compressedByteCount <= 0 ||
            compressedByteCount > compressedBytes.Length ||
            !IsCanonicalSha256(expectedSha256))
        {
            throw new InvalidDataException(
                $"Brotli sprite page hash declaration is invalid: {resourcePath}");
        }

        Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(
            compressedBytes.AsSpan(0, compressedByteCount),
            actualHash);
        var expectedHash = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException(
                $"Brotli sprite page SHA-256 does not match the manifest: {resourcePath}");
        }
    }

    private void AddResidentSpritePage(
        string pageName,
        SpritePageLoadResult result)
    {
        if (_residentSpritePages.TryGetValue(pageName, out var existingPage))
        {
            TouchResidentSpritePage(existingPage);
            if (!ReferenceEquals(existingPage.Pixels, result.Pixels))
            {
                RecordDiscardedSpritePageBytes(result.Pixels.LongLength);
            }
            return;
        }

        var lruNode = new LinkedListNode<string>(pageName);
        var residentPage = new ResidentSpritePage(
            result.Pixels,
            result.Stride,
            lruNode);
        _residentSpritePages.Add(pageName, residentPage);
        _residentSpritePageLru.AddLast(lruNode);
        _residentSpritePageBytes = checked(
            _residentSpritePageBytes + residentPage.ByteCount);
        TrimResidentSpritePagesToBudget(pageName);
    }

    private void TouchResidentSpritePage(ResidentSpritePage residentPage)
    {
        var node = residentPage.LruNode;
        if (!ReferenceEquals(node.List, _residentSpritePageLru) ||
            ReferenceEquals(_residentSpritePageLru.Last, node))
        {
            return;
        }

        _residentSpritePageLru.Remove(node);
        _residentSpritePageLru.AddLast(node);
    }

    private void TrimResidentSpritePagesToBudget(string? preservePageName = null)
    {
        TrimResidentSpritePagesToTarget(
            SpritePageResidentBudgetBytes,
            preservePageName);
    }

    private void TrimResidentSpritePagesToIdleTarget()
    {
        TrimResidentSpritePagesToTarget(
            SpritePageIdleResidentTargetBytes,
            preservePageName: null);
    }

    private void TrimResidentSpritePagesToTarget(
        long targetBytes,
        string? preservePageName)
    {
        var removedPageCount = 0;
        var candidate = _residentSpritePageLru.First;
        while (_residentSpritePageBytes > targetBytes &&
               candidate is not null)
        {
            var nextCandidate = candidate.Next;
            var pageName = candidate.Value;
            if (!IsSpritePageProtected(pageName, preservePageName) &&
                RemoveResidentSpritePage(pageName))
            {
                removedPageCount++;
            }

            candidate = nextCandidate;
        }

        if (removedPageCount > 0)
        {
            AppLogger.Info(
                $"Sprite cache trimmed: removed {removedPageCount}, " +
                $"resident {_residentSpritePageBytes / (1024d * 1024d):F1}/" +
                $"{targetBytes / (1024d * 1024d):F0} MiB target");
        }

        if (_residentSpritePageBytes > SpritePageResidentBudgetBytes)
        {
            // Protection wins over the soft budget: dropping the current,
            // pending, or active-clip page would create a visible flash. The
            // next protection release schedules another trim automatically.
            AppLogger.Info(
                $"Sprite cache temporarily over budget: " +
                $"{_residentSpritePageBytes / (1024d * 1024d):F1}/" +
                $"{SpritePageResidentBudgetBytes / (1024d * 1024d):F0} MiB, " +
                "all remaining pages are protected");
        }

        ScheduleSpritePageCollectionIfNeeded();
    }

    private void RequestResidentSpritePageTrim()
    {
        if (_isClosing)
        {
            return;
        }

        if (!_isInsideVisualRenderingCallback)
        {
            TrimResidentSpritePagesToBudget();
            return;
        }

        // Clip completion can occur inside CompositionTarget.Rendering. Only
        // publish a flag there; dictionary/LRU mutation and its summary log run
        // on the already-created dispatcher timer after composition returns.
        _residentSpritePageTrimPending = true;
        if (!_spritePagePrefetchDispatchTimer.IsEnabled)
        {
            _spritePagePrefetchDispatchTimer.Start();
        }
    }

    private void RequestIdleSpritePageTrim()
    {
        if (_isClosing)
        {
            return;
        }

        if (!_isInsideVisualRenderingCallback)
        {
            TrimResidentSpritePagesToIdleTarget();
            return;
        }

        _residentSpritePageTrimPending = true;
        _residentSpritePageIdleTrimPending = true;
        if (!_spritePagePrefetchDispatchTimer.IsEnabled)
        {
            _spritePagePrefetchDispatchTimer.Start();
        }
    }

    private void ScheduleSpritePageCollectionIfNeeded()
    {
        ObserveNaturalSpritePageCollection();
        if (_isClosing || _spritePageCollectionInProgress ||
            _spritePageEvictedBytesSinceCollection <
            SpritePageCollectionThresholdBytes ||
            _spritePageCollectionTimer.IsEnabled ||
            !CanRunIdleSpritePageCollection())
        {
            return;
        }

        var delay = SpritePageCollectionDelay;
        if (_lastSpritePageCollectionTimestamp > 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(
                _lastSpritePageCollectionTimestamp,
                Stopwatch.GetTimestamp());
            var remaining = MinimumSpritePageCollectionInterval - elapsed;
            if (remaining > delay)
            {
                delay = remaining;
            }
        }

        _spritePageCollectionTimer.Interval = delay;
        _spritePageCollectionTimer.Start();
    }

    private bool CanRunIdleSpritePageCollection() =>
        !_isClosing &&
        !_isInsideVisualRenderingCallback &&
        _activeClip is null &&
        !_isReminderActive &&
        !_dragInteractionActive &&
        !_pointerDown &&
        !_isPetSizeTransitioning &&
        !_isPetSizePreviewSessionActive &&
        !_isPetSizeAdjustmentActive &&
        !_petSizeTargetUpdatePending &&
        !_petSizeCommitPending &&
        !_isFrameBlending &&
        !_isPillowBreathing &&
        _bubbleMode == BubbleMode.None &&
        !_todoWindow.IsVisible &&
        !BubblePopup.IsOpen &&
        _edgeDock == EdgeDock.None &&
        _pendingSpriteFrame is null &&
        _spritePagePrefetchTask is null &&
        _desiredSpritePageName is null &&
        _renderDeferredSpritePageName is null &&
        _renderDeferredSpritePageFailureName is null &&
        !_renderDeferredSpritePageCancellation &&
        !_residentSpritePageTrimPending;

    private void ObserveNaturalSpritePageCollection()
    {
        if (_spritePageCollectionInProgress)
        {
            return;
        }

        var generation = GC.CollectionCount(GC.MaxGeneration);
        if (generation <= _lastObservedSpritePageCollectionGeneration)
        {
            return;
        }

        _lastObservedSpritePageCollectionGeneration = generation;
        var clearedEvictionDebt =
            _spritePageEvictedBytesSinceCollection > 0;
        _spritePageEvictedBytesSinceCollection = 0;
        if (clearedEvictionDebt)
        {
            _lastSpritePageCollectionTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void SpritePageCollectionTimer_Tick(object? sender, EventArgs e)
    {
        _spritePageCollectionTimer.Stop();
        if (_isClosing)
        {
            return;
        }

        if (_spritePageCollectionInProgress)
        {
            var collectionGeneration = GC.CollectionCount(GC.MaxGeneration);
            if (collectionGeneration >
                _spritePageCollectionGenerationAtRequest)
            {
                _spritePageEvictedBytesSinceCollection = Math.Max(
                    0,
                    _spritePageEvictedBytesSinceCollection -
                    _spritePageCollectionDebtAtRequest);
                _spritePageCollectionDebtAtRequest = 0;
                _spritePageCollectionInProgress = false;
                _spritePageCollectionPollCount = 0;
                _lastObservedSpritePageCollectionGeneration =
                    collectionGeneration;
                AppLogger.Info(
                    $"Sprite cache idle Gen2 completed: debt " +
                    $"{_spritePageEvictedBytesSinceCollection / (1024d * 1024d):F1} MiB");
                ScheduleSpritePageCollectionIfNeeded();
                return;
            }

            _spritePageCollectionPollCount++;
            if (_spritePageCollectionPollCount >= 10)
            {
                _spritePageCollectionInProgress = false;
                _spritePageCollectionDebtAtRequest = 0;
                _spritePageCollectionPollCount = 0;
                AppLogger.Info(
                    "Sprite cache idle Gen2 was not observed; eviction debt retained");
                ScheduleSpritePageCollectionIfNeeded();
                return;
            }

            _spritePageCollectionTimer.Interval = SpritePageCollectionDelay;
            _spritePageCollectionTimer.Start();
            return;
        }

        if (_spritePageEvictedBytesSinceCollection <
            SpritePageCollectionThresholdBytes)
        {
            return;
        }

        if (!CanRunIdleSpritePageCollection())
        {
            _spritePageCollectionTimer.Interval =
                SpritePageCollectionRetryDelay;
            _spritePageCollectionTimer.Start();
            return;
        }

        ObserveNaturalSpritePageCollection();
        if (_spritePageEvictedBytesSinceCollection <
            SpritePageCollectionThresholdBytes)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (_lastSpritePageCollectionTimestamp > 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(
                _lastSpritePageCollectionTimestamp,
                timestamp);
            if (elapsed < MinimumSpritePageCollectionInterval)
            {
                _spritePageCollectionTimer.Interval =
                    MinimumSpritePageCollectionInterval - elapsed;
                _spritePageCollectionTimer.Start();
                return;
            }
        }

        _lastSpritePageCollectionTimestamp = timestamp;
        _spritePageCollectionDebtAtRequest =
            _spritePageEvictedBytesSinceCollection;
        _spritePageCollectionGenerationAtRequest =
            GC.CollectionCount(GC.MaxGeneration);
        _spritePageCollectionPollCount = 0;
        _spritePageCollectionInProgress = true;
        AppLogger.Info(
            $"Sprite cache idle Gen2 requested: evicted " +
            $"{_spritePageCollectionDebtAtRequest / (1024d * 1024d):F1} MiB");

        _ = Task.Run(static () =>
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Forced,
                    blocking: false,
                    compacting: false))
            .ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    try
                    {
                        if (!Dispatcher.HasShutdownStarted &&
                            !Dispatcher.HasShutdownFinished)
                        {
                            Dispatcher.BeginInvoke(
                                DispatcherPriority.Background,
                                new Action(() =>
                                {
                                    if (_isClosing)
                                    {
                                        return;
                                    }

                                    _spritePageCollectionTimer.Interval =
                                        SpritePageCollectionDelay;
                                    _spritePageCollectionTimer.Start();
                                }));
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Dispatcher shutdown races are terminal and safe.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private bool IsSpritePageProtected(
        string pageName,
        string? preservePageName = null)
    {
        if (_pinnedSpritePageNames.Contains(pageName) ||
            string.Equals(pageName, preservePageName, StringComparison.Ordinal) ||
            string.Equals(pageName, _loadedSpritePageName, StringComparison.Ordinal) ||
            string.Equals(pageName, _desiredSpritePageName, StringComparison.Ordinal) ||
            string.Equals(pageName, _spritePagePrefetchPageName, StringComparison.Ordinal) ||
            string.Equals(pageName, _renderDeferredSpritePageName, StringComparison.Ordinal) ||
            (_currentSpriteFrame is SpriteFrame currentFrame &&
             string.Equals(pageName, currentFrame.PageName, StringComparison.Ordinal)) ||
            (_pendingSpriteFrame is SpriteFrame pendingFrame &&
             string.Equals(pageName, pendingFrame.PageName, StringComparison.Ordinal)) ||
            (_deferredActiveClipClockFrame is SpriteFrame deferredFrame &&
             string.Equals(pageName, deferredFrame.PageName, StringComparison.Ordinal)))
        {
            return true;
        }

        if (_activeClip is not { } activeClip)
        {
            return false;
        }

        foreach (var frame in activeClip.Frames)
        {
            if (string.Equals(
                    pageName,
                    frame.Image.PageName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool RemoveResidentSpritePage(string pageName)
    {
        if (!_residentSpritePages.Remove(pageName, out var residentPage))
        {
            return false;
        }

        if (ReferenceEquals(residentPage.LruNode.List, _residentSpritePageLru))
        {
            _residentSpritePageLru.Remove(residentPage.LruNode);
        }

        if (_residentSpritePageBytes < residentPage.ByteCount)
        {
            throw new InvalidOperationException(
                "Resident sprite page byte accounting underflowed.");
        }

        _residentSpritePageBytes -= residentPage.ByteCount;
        RecordDiscardedSpritePageBytes(residentPage.ByteCount);
        return true;
    }

    private void RecordDiscardedSpritePageBytes(long byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        ObserveNaturalSpritePageCollection();
        _spritePageEvictedBytesSinceCollection = checked(
            _spritePageEvictedBytesSinceCollection + byteCount);
        ScheduleSpritePageCollectionIfNeeded();
    }

    private void ClearResidentSpritePages()
    {
        _residentSpritePages.Clear();
        _residentSpritePageLru.Clear();
        _residentSpritePageBytes = 0;
        _spritePageEvictedBytesSinceCollection = 0;
        _spritePageCollectionDebtAtRequest = 0;
        _spritePageCollectionInProgress = false;
        _spritePageCollectionPollCount = 0;
        _spritePageCollectionTimer.Stop();
        _spritePagePixels = Array.Empty<byte>();
        _loadedSpritePageName = null;
        _loadedSpritePageStride = 0;
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
            $"{result.TotalElapsed.TotalMilliseconds - result.ReadElapsed.TotalMilliseconds:F1} ms), " +
            $"cache {_residentSpritePageBytes / (1024d * 1024d):F1}/" +
            $"{SpritePageResidentBudgetBytes / (1024d * 1024d):F0} MiB");
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
                throw new EndOfStreamException("Brotli sprite page data ended early.");
            }

            offset += read;
        }
    }

    private static void DecodeBrotliPage(
        ReadOnlySpan<byte> input,
        byte[] output,
        int expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0 || expectedLength > output.Length)
        {
            throw new InvalidDataException("Brotli分页输出尺寸超出解码缓冲区。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var decoder = new BrotliDecoder();
        var status = decoder.Decompress(
            input,
            output.AsSpan(0, expectedLength),
            out var bytesConsumed,
            out var bytesWritten);
        if (status != OperationStatus.Done ||
            bytesConsumed != input.Length ||
            bytesWritten != expectedLength)
        {
            throw new InvalidDataException(
                "Brotli分页未完整解码：" +
                $"状态 {status}，输入 {bytesConsumed}/{input.Length} 字节，" +
                $"输出 {bytesWritten}/{expectedLength} 字节。");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void DecodeSpritePagePayload(
        string resourcePath,
        string encoding,
        byte[] payload,
        int expectedPayloadByteCount,
        byte[] decodedPixels,
        int atlasWidth,
        int atlasHeight,
        int[] frameDescriptorValues,
        string expectedDecodedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedSpriteAtlasEncoding(encoding) ||
            atlasWidth <= 0 || atlasHeight <= 0 ||
            (long)atlasWidth * atlasHeight > int.MaxValue / 4 ||
            expectedPayloadByteCount <= 0 ||
            expectedPayloadByteCount > MaximumSpritePagePayloadBytes ||
            payload.Length < expectedPayloadByteCount ||
            !IsCanonicalSha256(expectedDecodedSha256))
        {
            throw new InvalidDataException(
                $"Sprite page payload declaration is invalid: {resourcePath}");
        }

        var expectedAtlasByteCount = checked(atlasWidth * atlasHeight * 4);
        if (decodedPixels.Length != expectedAtlasByteCount)
        {
            throw new InvalidDataException(
                $"Sprite page atlas buffer length is invalid: {resourcePath}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(
                encoding,
                SpriteAtlasDirectEncoding,
                StringComparison.Ordinal))
        {
            if (expectedPayloadByteCount != expectedAtlasByteCount)
            {
                throw new InvalidDataException(
                    $"Direct sprite page payload length is invalid: {resourcePath}");
            }

            if (!ReferenceEquals(payload, decodedPixels))
            {
                payload.AsSpan(0, expectedPayloadByteCount).CopyTo(decodedPixels);
            }
        }
        else
        {
            ReconstructDeltaSubSpritePage(
                payload,
                expectedPayloadByteCount,
                decodedPixels,
                atlasWidth,
                atlasHeight,
                frameDescriptorValues,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateSpriteAtlasDecodedHash(
            resourcePath,
            decodedPixels,
            expectedDecodedSha256);
    }

    private static void ReconstructDeltaSubSpritePage(
        byte[] payload,
        int payloadByteCount,
        byte[] atlasPixels,
        int atlasWidth,
        int atlasHeight,
        int[] frameDescriptorValues,
        CancellationToken cancellationToken)
    {
        if (atlasWidth <= 0 || atlasHeight <= 0 ||
            (long)atlasWidth * atlasHeight > int.MaxValue / 4 ||
            frameDescriptorValues.Length == 0 ||
            frameDescriptorValues.Length % SpriteFrameDescriptorValueCount != 0)
        {
            throw new InvalidDataException("Delta-sub sprite page geometry is invalid.");
        }


        var expectedAtlasByteCount = checked(atlasWidth * atlasHeight * 4);
        if (atlasPixels.Length != expectedAtlasByteCount)
        {
            throw new InvalidDataException("Delta-sub sprite page buffer length is invalid.");
        }

        var frameCount = frameDescriptorValues.Length /
                         SpriteFrameDescriptorValueCount;
        if (payloadByteCount <= 0 ||
            payloadByteCount > payload.Length ||
            payloadByteCount < checked(frameCount * DeltaSubFrameHeaderByteCount))
        {
            throw new InvalidDataException("Delta-sub sprite page payload ended before its headers.");
        }

        Array.Clear(atlasPixels);
        var previousDisplayFrameByteCount = checked(
            DisplayPixelWidth * DisplayPixelHeight * 4);
        var previousDisplayFrame = ArrayPool<byte>.Shared.Rent(
            previousDisplayFrameByteCount);
        Array.Clear(previousDisplayFrame, 0, previousDisplayFrameByteCount);
        try
        {
        var writtenRegionDestinations =
            new Dictionary<SpriteAtlasRegion, (int X, int Y)>();
        var validatedRegions = new List<SpriteAtlasRegion>();
        var payloadOffset = 0;
        var atlasStride = checked(atlasWidth * 4);
        var displayStride = checked(DisplayPixelWidth * 4);
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payloadByteCount - payloadOffset < DeltaSubFrameHeaderByteCount)
            {
                throw new InvalidDataException(
                    $"Delta-sub frame header is truncated at frame {frameIndex}.");
            }

            var header = payload.AsSpan(
                payloadOffset,
                DeltaSubFrameHeaderByteCount);
            var deltaX = BinaryPrimitives.ReadUInt16LittleEndian(header);
            var deltaY = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            var deltaWidth = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            var deltaHeight = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            payloadOffset = checked(payloadOffset + DeltaSubFrameHeaderByteCount);

            var emptyDelta = deltaX == 0 && deltaY == 0 &&
                             deltaWidth == 0 && deltaHeight == 0;
            if (!emptyDelta &&
                (deltaWidth == 0 || deltaHeight == 0 ||
                 (long)deltaX + deltaWidth > DisplayPixelWidth ||
                 (long)deltaY + deltaHeight > DisplayPixelHeight))
            {
                throw new InvalidDataException(
                    $"Delta-sub frame rectangle is invalid at frame {frameIndex}.");
            }

            if (!emptyDelta)
            {
                var deltaByteCount = checked((long)deltaWidth * deltaHeight * 4);
                if (deltaByteCount > payloadByteCount - payloadOffset)
                {
                    throw new InvalidDataException(
                        $"Delta-sub frame bytes are truncated at frame {frameIndex}.");
                }

                var deltaRowByteCount = checked(deltaWidth * 4);
                for (var row = 0; row < deltaHeight; row++)
                {
                    var previousOffset = checked(
                        (deltaY + row) * displayStride + deltaX * 4);
                    for (var byteIndex = 0;
                         byteIndex < deltaRowByteCount;
                         byteIndex++)
                    {
                        previousDisplayFrame[previousOffset + byteIndex] = unchecked(
                            (byte)(previousDisplayFrame[previousOffset + byteIndex] +
                                   payload[payloadOffset++]));
                    }
                }
            }

            var descriptorOffset = checked(
                frameIndex * SpriteFrameDescriptorValueCount);
            var atlasX = frameDescriptorValues[descriptorOffset];
            var atlasY = frameDescriptorValues[descriptorOffset + 1];
            var spriteWidth = frameDescriptorValues[descriptorOffset + 2];
            var spriteHeight = frameDescriptorValues[descriptorOffset + 3];
            var destinationX = frameDescriptorValues[descriptorOffset + 4];
            var destinationY = frameDescriptorValues[descriptorOffset + 5];
            if (!IsValidSpriteAtlasFrameDescriptor(
                    atlasX,
                    atlasY,
                    spriteWidth,
                    spriteHeight,
                    destinationX,
                    destinationY,
                    atlasWidth,
                    atlasHeight))
            {
                throw new InvalidDataException(
                    $"Delta-sub atlas descriptor is invalid at frame {frameIndex}.");
            }

            var region = new SpriteAtlasRegion(
                atlasX,
                atlasY,
                spriteWidth,
                spriteHeight);
            var regionWasWritten = writtenRegionDestinations.TryGetValue(
                region,
                out var previousDestination);
            if (regionWasWritten &&
                previousDestination != (destinationX, destinationY))
            {
                throw new InvalidDataException(
                    $"Repeated delta-sub sprite destination differs at frame {frameIndex}.");
            }

            if (!regionWasWritten)
            {
                if (validatedRegions.Any(existing => existing.Intersects(region)))
                {
                    throw new InvalidDataException(
                        $"Delta-sub atlas regions overlap at frame {frameIndex}.");
                }

                validatedRegions.Add(region);
                writtenRegionDestinations.Add(
                    region,
                    (destinationX, destinationY));
            }

            for (var row = 0; row < spriteHeight; row++)
            {
                var atlasRowOffset = checked(
                    (atlasY + row) * atlasStride + atlasX * 4);
                var displayY = (long)destinationY + row;
                for (var column = 0; column < spriteWidth; column++)
                {
                    var atlasPixelOffset = checked(atlasRowOffset + column * 4);
                    var displayX = (long)destinationX + column;
                    var sourcePixelOffset = displayX >= 0 &&
                                            displayX < DisplayPixelWidth &&
                                            displayY >= 0 &&
                                            displayY < DisplayPixelHeight
                        ? checked((int)(displayY * displayStride + displayX * 4))
                        : -1;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var value = sourcePixelOffset >= 0
                            ? previousDisplayFrame[sourcePixelOffset + channel]
                            : (byte)0;
                        if (regionWasWritten &&
                            atlasPixels[atlasPixelOffset + channel] != value)
                        {
                            throw new InvalidDataException(
                                $"Repeated delta-sub sprite differs at frame {frameIndex}.");
                        }

                        atlasPixels[atlasPixelOffset + channel] = value;
                    }
                }
            }
        }

        if (payloadOffset != payloadByteCount)
        {
            throw new InvalidDataException(
                $"Delta-sub sprite page has {payloadByteCount - payloadOffset} trailing bytes.");
        }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(
                previousDisplayFrame,
                clearArray: false);
        }
    }

    private static void ValidateSpriteAtlasDecodedHash(
        string resourcePath,
        byte[] decodedPixels,
        string expectedSha256)
    {
        if (decodedPixels.Length == 0 || !IsCanonicalSha256(expectedSha256))
        {
            throw new InvalidDataException(
                $"Sprite page decoded hash declaration is invalid: {resourcePath}");
        }

        Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(decodedPixels, actualHash);
        var expectedHash = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new InvalidDataException(
                $"Sprite page decoded SHA-256 does not match the manifest: {resourcePath}");
        }
    }

    private void SetBubbleMode(BubbleMode mode)
    {
        if (_bubbleMode == mode)
        {
            return;
        }

        var previousMode = _bubbleMode;
        if (mode is BubbleMode.Todo or BubbleMode.Reminder)
        {
            // Showing an owned WPF window can synchronously pump layout/render work.
            // Unsubscribe before Show() so a re-entrant composition callback cannot
            // observe BubbleMode.Todo while the old reaction is still active and
            // replace it with the final think pose. EnterTodoVisualState subscribes
            // again only after the owned window has finished its one-time work.
            _automaticTimer.Stop();
            StopVisualClock();
        }

        HideBubbleVisuals();
        _bubbleMode = mode;
        ShowBubbleVisuals(mode);
        if (mode == BubbleMode.Todo)
        {
            EnterTodoVisualState();
        }
        else if (mode == BubbleMode.Reminder)
        {
            EnterReminderVisualState();
        }

        LogInfo($"气泡状态：{previousMode} -> {mode}");

        if (mode != BubbleMode.Todo &&
            mode != BubbleMode.Reminder &&
            previousMode == BubbleMode.Todo)
        {
            StartTodoExitTransition();
        }
        else if (mode != BubbleMode.Reminder &&
                 mode != BubbleMode.Todo &&
                 previousMode == BubbleMode.Reminder)
        {
            StartReminderExitTransition();
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
        RequestResidentSpritePageTrim();
        // Todo owns the complete wake-to-think pose path, including approved
        // bridge poses. Publish adjacent poses
        // directly instead of cross-fading whole RGBA images, which creates
        // double silhouettes and shimmering semi-transparent outlines.
        _nextFrameBlendDuration = TimeSpan.Zero;
        _nextFrameMinimumHold = TimeSpan.Zero;
        AppLogger.Info("待办打开过渡开始");
        ShowActiveClipFrame(enterStartIndex);
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
            animationFrame => string.Equals(
                animationFrame.Image.Name,
                _wakeFrames[^1].Name,
                StringComparison.Ordinal));
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
        RequestIdleSpritePageTrim();
        UpdateVisualClockSubscription();
    }

    private void EnterReminderVisualState()
    {
        _automaticTimer.Stop();
        BakeCurrentPetVisualTransformIntoDisplayFrame();
        StopPillowBreathing();

        if (_activeClip is { } activeClip)
        {
            AppLogger.Info(
                $"动作中止：{activeClip.ActionName}，原因：定时任务到点");
        }

        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        ExitEdgePeek(
            restartAutomaticCountdown: false,
            restoreIdleFrame: false);
        ResetPetVisualTransforms();
        PetFacingScale.ScaleX = _reminderFacingScaleX;
        PetFacingScale.ScaleY = 1;
        _activeClip = _reminderEnterClip;
        _activeFrameIndex = -1;
        RequestResidentSpritePageTrim();
        _nextFrameBlendDuration = TimeSpan.Zero;
        _nextFrameMinimumHold = TimeSpan.Zero;
        RequestSpritePagePrefetch(
            _reminderEnterClip.Frames[0].Image.PageName,
            urgent: true);
        AppLogger.Info("定时提醒举喇叭入场开始");
        ShowActiveClipFrame(0);
    }

    private void StartReminderHoldAnimation()
    {
        if (_isClosing || !_isReminderActive)
        {
            return;
        }

        _activeClip = _reminderHoldClip;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        _nextFrameBlendDuration = TimeSpan.Zero;
        _nextFrameMinimumHold = TimeSpan.Zero;
        RequestSpritePagePrefetch(
            _reminderHoldClip.Frames[0].Image.PageName,
            urgent: true);
        ShowActiveClipFrame(0);
    }

    private void StartReminderExitTransition()
    {
        if (_isClosing)
        {
            return;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        ExitEdgePeek(
            restartAutomaticCountdown: false,
            restoreIdleFrame: false);
        _activeClip = _reminderExitClip;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        _nextFrameBlendDuration = TimeSpan.Zero;
        _nextFrameMinimumHold = TimeSpan.Zero;
        AppLogger.Info("定时提醒收起过渡开始");
        ShowActiveClipFrame(0);
    }

    private void ConfigureReminderBubblePlacement()
    {
        var workArea = MonitorWorkArea.GetForWindow(this);
        var petLeft = Left;
        var petRight = Left + (ActualWidth > 0 ? ActualWidth : Width);
        var requiredWidth = ReminderBubble.Width + 12;
        var availableOnLeft = petLeft - workArea.Left;
        var availableOnRight = workArea.Right - petRight;
        var placeOnLeft = availableOnLeft >= requiredWidth ||
                          availableOnLeft >= availableOnRight;

        BubblePopup.Placement = placeOnLeft
            ? PlacementMode.Left
            : PlacementMode.Right;
        BubbleBodyColumn.Width = placeOnLeft
            ? GridLength.Auto
            : new GridLength(12);
        BubbleTailColumn.Width = placeOnLeft
            ? new GridLength(12)
            : GridLength.Auto;
        Grid.SetColumn(BubbleHost, placeOnLeft ? 0 : 1);
        Grid.SetColumn(BubbleTailHost, placeOnLeft ? 1 : 0);
        BubbleTailHost.Margin = placeOnLeft
            ? new Thickness(-1, 0, 0, 0)
            : new Thickness(0, 0, -1, 0);
        BubbleTailHost.HorizontalAlignment = placeOnLeft
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        BubbleTailPolygon.Points = placeOnLeft
            ? PointCollection.Parse("0,0 12,9 0,18")
            : PointCollection.Parse("12,0 0,9 12,18");
        BubbleTailPolygon.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0xC9));
        BubbleTailPolygon.Stroke = new SolidColorBrush(Color.FromRgb(0xE9, 0xAD, 0x3C));
        _reminderFacingScaleX = placeOnLeft ? 1 : -1;
    }

    private void RefreshReminderBubbleOffset()
    {
        if (_bubbleMode != BubbleMode.Reminder || !BubblePopup.IsOpen)
        {
            return;
        }

        var targetHeight = double.IsFinite(Height) && Height > 0
            ? Height
            : PetHost.ActualHeight;
        BubblePopup.VerticalOffset = targetHeight - ReminderBubbleHeight;
        // Popup owns a separate HWND. Touch the horizontal offset once so WPF
        // recomputes placement after the pet's one-time maximum-size envelope.
        var horizontalOffset = BubblePopup.HorizontalOffset;
        BubblePopup.HorizontalOffset = horizontalOffset + 0.01;
        BubblePopup.HorizontalOffset = horizontalOffset;
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
        ReminderBubble.Visibility = Visibility.Collapsed;
        if (_todoWindow.IsVisible)
        {
            _todoWindow.CommitPendingTodoEdit();
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
            _todoWindow.ShowDefaultTab();
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

        if (mode == BubbleMode.Reminder)
        {
            ConfigureReminderBubblePlacement();
            var reminderPetHeight = PetHost.ActualHeight > 0
                ? PetHost.ActualHeight
                : PetHost.Height;
            BubblePopup.VerticalOffset = reminderPetHeight - ReminderBubbleHeight;
            BubbleHost.Visibility = Visibility.Visible;
            BubbleTailHost.Visibility = Visibility.Visible;
            ReminderBubble.Visibility = Visibility.Visible;
            BubblePopup.IsOpen = true;
            return;
        }

        var displayedPetHeight = PetHost.ActualHeight > 0
            ? PetHost.ActualHeight
            : PetHost.Height;
        BubblePopup.Placement = PlacementMode.Left;
        BubbleBodyColumn.Width = GridLength.Auto;
        BubbleTailColumn.Width = new GridLength(12);
        Grid.SetColumn(BubbleHost, 0);
        Grid.SetColumn(BubbleTailHost, 1);
        BubbleTailHost.Margin = new Thickness(-1, 0, 0, 0);
        BubbleTailHost.HorizontalAlignment = HorizontalAlignment.Left;
        BubbleTailPolygon.Points = PointCollection.Parse("0,0 12,9 0,18");
        BubbleTailPolygon.Fill = Brushes.White;
        BubbleTailPolygon.Stroke = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE8));
        BubblePopup.VerticalOffset = displayedPetHeight - CuteBubbleHeight;
        BubbleHost.Visibility = Visibility.Visible;
        BubbleTailHost.Visibility = Visibility.Visible;
        CuteBubble.Visibility = Visibility.Visible;
        BubblePopup.IsOpen = true;
    }

    private void TodoWindow_PetSizeScaleChanged(double scale)
    {
        if (_isTransientPetSizeOverride)
        {
            return;
        }

        QueuePetSizeScaleTargetAt(scale, Stopwatch.GetTimestamp());
    }

    private void TodoWindow_PetSizeAdjustmentStarted()
    {
        if (_isTransientPetSizeOverride)
        {
            return;
        }

        _isPetSizeAdjustmentActive = true;
        _petSizeAdjustmentValueChanged = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();

        if (!_isPetSizePreviewSessionActive)
        {
            var currentScale = GetPetSizeMotionStateAt(Stopwatch.GetTimestamp()).Scale;
            BeginPetSizePreviewSession(currentScale);
            PreparePetSizePreviewEnvelope();
        }

        // The one-time native envelope is ready before the first slider value.
        // Rendering can now stay transform-only for the entire gesture.
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
        if (!_isPetSizePreviewSessionActive)
        {
            var currentScale = GetPetSizeMotionStateAt(timestamp).Scale;
            if (Math.Abs(normalizedScale - currentScale) >= 0.0005)
            {
                BeginPetSizePreviewSession(currentScale);
                PreparePetSizePreviewEnvelope();
            }
        }

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
        if (_isTransientPetSizeOverride)
        {
            return;
        }

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
            // Direct internal callers are rare; production queued targets have
            // already prepared this one-time envelope outside Rendering.
            BeginPetSizePreviewSession(currentScale);
            PreparePetSizePreviewEnvelope();
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
        _petSizeSettingsDirty = !_isTransientPetSizeOverride &&
            Math.Abs(_petSizeTargetScale - _persistedPetSizeScale) >= 0.0005;

        _petSizePersistTimer.Stop();
        if (!_isPetSizeAdjustmentActive && !_isTransientPetSizeOverride)
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
        // MainWindow subscribes as soon as a size gesture starts, before the
        // TodoWindow coalescer receives its first value. Pull the newest slider
        // sample here so handler subscription order cannot add a one-refresh
        // lag or make the thumb feel sticky during a fast drag.
        _todoWindow.FlushPendingPetSizeScaleChanged();

        // Multiple input events between two composition frames collapse into
        // one target retarget. The latest input timestamp preserves absolute
        // spring time, while the visual transform is written only below.
        ConsumePendingPetSizeTargetAt(timestamp);
        if (_isPetSizeTransitioning)
        {
            AdvancePetSizeTransition(timestamp);
        }

        // Keep the window that owns the Slider physically stationary while its
        // Thumb has mouse capture. Moving that HWND changes Track.PointToScreen
        // underneath the pointer and creates a sticky feedback loop during fast
        // drags. Preserve the dirty flag and follow the pet once after release.
        if (_petSizeTodoPositionNeedsUpdate &&
            !_isPetSizeAdjustmentActive &&
            !_isPetSizeTransitioning &&
            _todoWindow.IsVisible)
        {
            _petSizeTodoPositionNeedsUpdate = false;
            // OwnedWindowPositioner ultimately updates a second HWND. Keep that
            // synchronous layout work outside CompositionTarget.Rendering so a
            // slow native position pass cannot steal the pet's next frame.
            QueueTodoWindowPositionUpdate();
        }
    }

    private void AdvancePetSizeTransition(long timestamp)
    {
        if (!_isPetSizeTransitioning)
        {
            return;
        }

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
        ApplyPetSizePreviewScale(_petSizePreviewBaseScale);
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

    private void BeginReminderPetSizeOverrideAt(long timestamp)
    {
        if (_isClosing || _isTransientPetSizeOverride)
        {
            return;
        }

        ConsumeLatestPetSizeInputAt(timestamp);
        if (_isPetSizeAdjustmentActive)
        {
            _isPetSizeAdjustmentActive = false;
            _petSizeAdjustmentValueChanged = false;
            _petSizeCommitPending = false;
        }

        if (_isPetSizePreviewSessionActive)
        {
            CommitPetSizePreviewSession(persist: true);
        }
        else if (_petSizeSettingsDirty)
        {
            SaveSettings();
        }

        _reminderRestoreScale = NormalizePetSizeScale(_petSizeScale);
        _isTransientPetSizeOverride = true;
        _isRestoringReminderSize = false;
        _petSizeSettingsDirty = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();
        QueuePetSizeScaleTargetAt(MaximumPetSizeScale, timestamp);
        RefreshReminderBubbleOffset();
        AppLogger.Info(
            $"定时提醒临时放大：{_reminderRestoreScale:P1} -> {MaximumPetSizeScale:P1}");
    }

    private void RestoreReminderPetSizeAt(long timestamp)
    {
        if (!_isTransientPetSizeOverride || _isRestoringReminderSize)
        {
            return;
        }

        _isRestoringReminderSize = true;
        QueuePetSizeScaleTargetAt(_reminderRestoreScale, timestamp);
        _reminderSizeCommitTimer.Stop();
        _reminderSizeCommitTimer.Interval = PetSizeTransitionDuration +
                                             TimeSpan.FromMilliseconds(20);
        _reminderSizeCommitTimer.Start();
    }

    private void ReminderSizeCommitTimer_Tick(object? sender, EventArgs e)
    {
        _reminderSizeCommitTimer.Stop();
        if (_isClosing || !_isTransientPetSizeOverride ||
            !_isRestoringReminderSize)
        {
            return;
        }

        ConsumePendingPetSizeTargetAt(Stopwatch.GetTimestamp());
        if (_isPetSizeTransitioning || _petSizeTargetUpdatePending)
        {
            _reminderSizeCommitTimer.Interval = TimeSpan.FromMilliseconds(16);
            _reminderSizeCommitTimer.Start();
            return;
        }

        if (_isPetSizePreviewSessionActive)
        {
            CommitPetSizePreviewSession(persist: false);
        }

        _isTransientPetSizeOverride = false;
        _isRestoringReminderSize = false;
        _petSizeSettingsDirty = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();
        AppLogger.Info($"定时提醒结束，桌宠大小恢复为 {_reminderRestoreScale:P1}");
        UpdateVisualClockSubscription();
    }

    private void PetSizePersistTimer_Tick(object? sender, EventArgs e)
    {
        _petSizePersistTimer.Stop();
        if (_isTransientPetSizeOverride)
        {
            return;
        }

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
        if (persist && !_isTransientPetSizeOverride && _petSizeSettingsDirty)
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

    private void TodoWindow_TodoEdited(TodoItem item)
    {
        SaveTodos();
        AppLogger.Info("待办文字已修改");
    }

    private void TodoWindow_TodoMoveRequested(TodoItem item, int newIndex)
    {
        var oldIndex = _todos.IndexOf(item);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= _todos.Count ||
            oldIndex == newIndex)
        {
            return;
        }

        _todos.Move(oldIndex, newIndex);
        SaveTodos();
        AppLogger.Info($"待办顺序已调整：{oldIndex + 1} -> {newIndex + 1}");
    }

    private void TodoWindow_TodoDragCompleted()
    {
        ScheduleOutsideTodoClose();
    }

    private void TodoWindow_DeleteRequested(TodoItem item)
    {
        if (_todos.Remove(item))
        {
            SaveTodos();
            AppLogger.Info($"删除待办，当前数量：{_todos.Count}");
        }
    }

    private void TodoWindow_ScheduledTaskAddRequested(
        string text,
        DateTimeOffset dueAt)
    {
        var normalizedText = text.Trim();
        if (normalizedText.Length == 0)
        {
            return;
        }

        var now = _nowProvider();
        var item = new ScheduledTaskItem
        {
            Id = Guid.NewGuid(),
            Text = normalizedText,
            DueAt = ScheduledTaskStore.NormalizeToWholeSecond(dueAt),
            CreatedAt = now
        };
        InsertScheduledTaskSorted(item);
        SaveScheduledTasks();
        ProcessScheduledTasksAt(now);
        AppLogger.Info(
            $"新增定时任务：{item.Id}，触发时间 {item.DueAt:O}，" +
            $"当前数量：{_scheduledTasks.Count}");
    }

    private void TodoWindow_ScheduledTaskDeleteRequested(ScheduledTaskItem item)
    {
        if (ReferenceEquals(item, _activeReminder))
        {
            return;
        }

        if (_scheduledTasks.Remove(item))
        {
            _queuedReminderIds.Remove(item.Id);
            SaveScheduledTasks();
            ScheduleNextReminderAt(_nowProvider());
            AppLogger.Info(
                $"删除定时任务：{item.Id}，当前数量：{_scheduledTasks.Count}");
        }
    }

    private void TodoWindow_ScheduledTaskEditRequested(
        ScheduledTaskItem item,
        string text,
        DateTimeOffset dueAt)
    {
        if (ReferenceEquals(item, _activeReminder))
        {
            AppLogger.Info($"忽略正在提示的定时任务修改：{item.Id}");
            return;
        }

        var existingIndex = _scheduledTasks.IndexOf(item);
        var normalizedText = text.Trim();
        if (existingIndex < 0 || normalizedText.Length == 0)
        {
            return;
        }

        _scheduledTasks.RemoveAt(existingIndex);
        item.Text = normalizedText;
        item.DueAt = ScheduledTaskStore.NormalizeToWholeSecond(dueAt);
        InsertScheduledTaskSorted(item);

        var now = _nowProvider();
        SaveScheduledTasks();
        ProcessScheduledTasksAt(now);
        AppLogger.Info(
            $"修改定时任务：{item.Id}，触发时间 {item.DueAt:O}，" +
            $"排序位置 {_scheduledTasks.IndexOf(item) + 1}/{_scheduledTasks.Count}");
    }

    private void TodoWindow_TransientInteractionCompleted()
    {
        ScheduleOutsideTodoClose();
    }

    private void InsertScheduledTaskSorted(ScheduledTaskItem item)
    {
        var insertIndex = 0;
        while (insertIndex < _scheduledTasks.Count &&
               CompareScheduledTasks(_scheduledTasks[insertIndex], item) <= 0)
        {
            insertIndex++;
        }

        _scheduledTasks.Insert(insertIndex, item);
    }

    private static int CompareScheduledTasks(
        ScheduledTaskItem left,
        ScheduledTaskItem right)
    {
        var dueComparison = left.DueAt.UtcDateTime.Ticks.CompareTo(
            right.DueAt.UtcDateTime.Ticks);
        if (dueComparison != 0)
        {
            return dueComparison;
        }

        var createdComparison = left.CreatedAt.UtcDateTime.Ticks.CompareTo(
            right.CreatedAt.UtcDateTime.Ticks);
        return createdComparison != 0
            ? createdComparison
            : left.Id.CompareTo(right.Id);
    }

    private void ScheduledTaskTimer_Tick(object? sender, EventArgs e)
    {
        _scheduledTaskTimer.Stop();
        ProcessScheduledTasksAt(_nowProvider());
    }

    private void ProcessScheduledTasksAt(DateTimeOffset now)
    {
        if (_isClosing)
        {
            return;
        }

        _scheduledTaskTimer.Stop();
        RebuildReminderQueueAt(now);
        if (_activeReminder is null)
        {
            ShowNextQueuedReminderAt(now);
        }

        ScheduleNextReminderAt(now);
    }

    private void RebuildReminderQueueAt(DateTimeOffset now)
    {
        _reminderQueue.Clear();
        _queuedReminderIds.Clear();
        if (_activeReminder is { } activeReminder &&
            _scheduledTasks.Contains(activeReminder))
        {
            _queuedReminderIds.Add(activeReminder.Id);
        }

        foreach (var item in _scheduledTasks)
        {
            if (item.DueAt > now)
            {
                break;
            }

            if (_queuedReminderIds.Add(item.Id))
            {
                _reminderQueue.Enqueue(item);
            }
        }
    }

    private bool ShowNextQueuedReminderAt(DateTimeOffset now)
    {
        while (_reminderQueue.Count > 0)
        {
            var item = _reminderQueue.Dequeue();
            if (!_scheduledTasks.Contains(item))
            {
                _queuedReminderIds.Remove(item.Id);
                continue;
            }

            _activeReminder = item;
            _isReminderActive = true;
            ReminderMessageText.Text = item.Text;
            ReminderMessageText.Select(0, 0);
            if (_bubbleMode != BubbleMode.Reminder)
            {
                SetBubbleMode(BubbleMode.Reminder);
                BeginReminderPetSizeOverrideAt(Stopwatch.GetTimestamp());
            }
            else
            {
                StartReminderHoldAnimation();
            }

            AppLogger.Info(
                $"定时任务触发：{item.Id}，计划时间 {item.DueAt:O}，" +
                $"延迟 {Math.Max(0, (now - item.DueAt).TotalSeconds):F1} 秒");
            return true;
        }

        return false;
    }

    private void ReminderAcknowledgeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        AcknowledgeActiveReminder();
        e.Handled = true;
    }

    private void AcknowledgeActiveReminder()
    {
        var acknowledged = _activeReminder;
        if (acknowledged is null)
        {
            return;
        }

        _activeReminder = null;
        _queuedReminderIds.Remove(acknowledged.Id);
        _scheduledTasks.Remove(acknowledged);
        SaveScheduledTasks();

        var now = _nowProvider();
        RebuildReminderQueueAt(now);
        if (ShowNextQueuedReminderAt(now))
        {
            ScheduleNextReminderAt(now);
            return;
        }

        _isReminderActive = false;
        SetBubbleMode(BubbleMode.None);
        RestoreReminderPetSizeAt(Stopwatch.GetTimestamp());
        ScheduleNextReminderAt(now);
        AppLogger.Info($"定时任务已确认：{acknowledged.Id}");
    }

    private void ScheduleNextReminderAt(DateTimeOffset now)
    {
        _scheduledTaskTimer.Stop();
        if (_isClosing)
        {
            return;
        }

        ScheduledTaskItem? next = null;
        foreach (var item in _scheduledTasks)
        {
            if (_queuedReminderIds.Contains(item.Id))
            {
                continue;
            }

            next = item;
            break;
        }

        if (next is null)
        {
            return;
        }

        _scheduledTaskTimer.Interval = CalculateReminderWakeDelay(now, next.DueAt);
        _scheduledTaskTimer.Start();
    }

    private static TimeSpan CalculateReminderWakeDelay(
        DateTimeOffset now,
        DateTimeOffset nextDueAt)
    {
        var remaining = nextDueAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return remaining > MaximumReminderWakeInterval
            ? MaximumReminderWakeInterval
            : remaining;
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

    private void SaveScheduledTasks()
    {
        if (!_scheduledTaskStore.Save(_scheduledTasks))
        {
            AppLogger.Info("定时任务保存失败，请检查本地应用数据目录权限");
        }
    }

    private enum BubbleMode
    {
        None,
        Cute,
        Todo,
        Reminder
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
        int ActionStartIndex);

    private sealed record SpriteAtlasManifest(
        int Version,
        string Compression,
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
        int PayloadByteCount,
        int CompressedByteCount,
        string Encoding,
        string ContentSha256,
        string DecodedSha256,
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

    private readonly record struct SpriteAtlasRegion(
        int X,
        int Y,
        int Width,
        int Height)
    {
        public bool Intersects(SpriteAtlasRegion other)
        {
            return (long)X < (long)other.X + other.Width &&
                   (long)other.X < (long)X + Width &&
                   (long)Y < (long)other.Y + other.Height &&
                   (long)other.Y < (long)Y + Height;
        }
    }

    private sealed record SpriteAtlasPage(
        string ResourcePath,
        string PreviewResourcePath,
        int Width,
        int Height,
        int UncompressedByteCount,
        int PayloadByteCount,
        int CompressedByteCount,
        string Encoding,
        string ContentSha256,
        string DecodedSha256,
        int[] FrameDescriptorValues,
        IReadOnlyDictionary<string, SpriteFrame> Frames);

    private sealed class ResidentSpritePage
    {
        public ResidentSpritePage(
            byte[] pixels,
            int stride,
            LinkedListNode<string> lruNode)
        {
            Pixels = pixels;
            Stride = stride;
            LruNode = lruNode;
        }

        public byte[] Pixels { get; }

        public int Stride { get; }

        public long ByteCount => Pixels.LongLength;

        public LinkedListNode<string> LruNode { get; }
    }

    private readonly record struct SpritePageLoadResult(
        byte[] Pixels,
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
