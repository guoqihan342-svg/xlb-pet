using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
    private const double PetRoamRotationOriginY = 130;
    private const double PetRoamRotationOriginYRatio =
        PetRoamRotationOriginY / PetHeight;
    // Roaming continuously rotates the authored 190x242 sprite around (95,130).
    // Its farthest corner sweeps a ~451 DIP diameter at 140%; 454 DIP keeps
    // that full circle plus antialiasing inside one permanently sized HWND.
    private const double PetEnvelopeWidth = 454;
    private const double PetEnvelopeHeight = 454;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    // Code-only playback setting: 1.0 is the authored 60fps timing; values
    // above 1.0 play character poses faster. Rebuild after changing it.
    private const double AnimationPlaybackSpeed = 1.25;
    private const double CuteBubbleWidth = 215;
    private const double CuteBubbleHeight = 76;
    private const double ReminderBubbleHeight = 148;
    private const double ScreenEdgeMargin = 12;
    private const double EdgeContactTolerance = 1;
    private const double EdgeDockActivationDistance = 12;
    // Code-only roaming tuning. Position is evaluated from the absolute visual
    // clock on every monitor refresh; these values do not create a UI slider.
    private const double EdgeRoamBaseSpeedDipsPerSecond = 160;
    private const double EdgeRoamCornerRadiusDips = 48;
    private const double EdgeRoamSupportAnchorYRatio = 457d / 509d;
    private static readonly PointCollection CuteTailPointsRight = new()
    {
        new Point(0, 0),
        new Point(12, 9),
        new Point(0, 18)
    };
    private static readonly PointCollection CuteTailPointsLeft = new()
    {
        new Point(12, 0),
        new Point(0, 9),
        new Point(12, 18)
    };
    private static readonly Brush CuteBubbleStrokeBrush =
        new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE8));
    // 48 authored poses at the global 1.25x playback setting land exactly on
    // 60fps. A 75fps pose clock on a 60Hz desktop periodically skipped two
    // adjacent in-between frames and made an otherwise smooth path pulse.
    private const double EdgeRoamPoseFramesPerSecond = 48;
    private static readonly TimeSpan EdgeRoamPreloadLeadTime =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EdgeRoamPreloadWatchdogInterval =
        TimeSpan.FromMilliseconds(250);
    private const int EdgeRoamClosestPointSamples = 256;
    private const double SnoreBubbleMinimumScale = 1;
    private const double SnoreBubbleMaximumScale = 1.58;
    private const int DisplayPixelWidth = 399;
    private const int DisplayPixelHeight = 509;
    private const int DisplayFrameByteCount =
        DisplayPixelWidth * DisplayPixelHeight * 4;
    private const string SpriteAtlasManifestPath = "Assets/luban-sprite-pages.json";
    private const string SpriteAtlasCompression = "brotli";
    private const string SpriteAtlasDirectEncoding = "pbgra32";
    private const string SpriteAtlasDeltaSubEncoding = "pbgra32-delta-sub-v1";
    private const int DeltaSubFrameHeaderByteCount = sizeof(ushort) * 4;
    private const int SpriteFrameDescriptorValueCount = 6;
    private const int MaximumDecodedSpritePageBytes = 24 * 1024 * 1024;
    private const int MaximumSpritePagePayloadBytes = 32 * 1024 * 1024;
    // Ordinary clips keep only their rolling current/next-page working set.
    // The generated manifest leaves a small bounded best-fit reuse margin
    // without retaining an unrelated decoded page.
    private const long SpritePageResidentBudgetBytes = 52L * 1024 * 1024;
    // The three-page work loop plus the permanently pinned idle page needs
    // 55,392,540 exact bytes and at most 59,191,344 bytes when every page reuses
    // the largest compatible capacity reachable from the current manifest.
    // Keep that hot set only while work is active so its 1.6-second loop never
    // evicts and re-decodes the next page.
    private const long SpritePageWorkResidentBudgetBytes = 57L * 1024 * 1024;
    // Serious typing also keeps its authored expression-exit page ready while
    // cycling across three loop pages. Give only that short-lived state enough
    // room for idle + loop + exit; ordinary work stays on the 57 MiB budget.
    private const long SpritePageSeriousWorkResidentBudgetBytes =
        73L * 1024 * 1024;
    // The higher roaming ceiling holds its complete active page set plus the
    // bounded best-fit reuse margin, then releases back to the idle target.
    private const long SpritePageRoamResidentBudgetBytes = 92L * 1024 * 1024;
    // Pin just the startup idle page. Its exact-sized array remains within
    // 12 MiB after every action, reminder, Todo, or roaming sequence.
    private const long SpritePageIdleResidentTargetBytes = 12L * 1024 * 1024;
    private const long SpritePageCollectionThresholdBytes = 8L * 1024 * 1024;
    private const int ActionLoopFrameCount = 48;
    private const int ActionLoopCycleCount = 3;
    private const int WorkEnterFrameCount = 48;
    private const int WorkLoopFrameCount = 96;
    private const int WorkSeriousLoopFrameCount = 96;
    private const int WorkSeriousExitFrameCount = 24;
    private const int WorkEnterPillowVisibleFrameCount = 24;
    private const double WorkNormalPoseFramesPerSecond = 60;
    private const double WorkFastPlaybackMultiplier = 2;
    private const double WorkModeIconTransitionDurationSeconds = 0.42;
    // The 96-pose keyboard cycle comes closest to its authored home-row pose at
    // these short neutral micro-seams. Serious/downshift transitions wait for the
    // next one on the unchanged 1x/2x clock instead of racing to loop frame 001.
    private static readonly int[] WorkNeutralMicroSeamFrameIndices =
        [0, 10, 21, 33, 44, 56, 69, 81, 93];
    private const int MaximumVisibleReminderOccurrences = 100;
    // The regenerated cute prefix spends the former second-bob frame budget on
    // eight-step interpolation around its one intentional crouch/recovery, and
    // reaches the complete raised-hands/open-smile pose at frame 56.
    private const int CuteCleanSmoothFrameCount = 56;
    private const double PetSizeSpringAngularFrequency = 28;
    private const double MaximumPetSizeVelocity = 4;
    private static readonly TimeSpan MotionFrameInterval =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
    private static readonly TimeSpan EdgePeekMotionFrameInterval = MotionFrameInterval;
    private static readonly TimeSpan TodoMotionFrameInterval = MotionFrameInterval;
    private static readonly TimeSpan ActionLoopFrameInterval = MotionFrameInterval;
    // Work clips intentionally counter the global character speed multiplier:
    // after ToCharacterAnimationTicks applies AnimationPlaybackSpeed, their
    // authored pose clock is exactly 60fps. Fast typing changes only the
    // absolute loop phase and therefore remains continuous at 2x.
    private static readonly TimeSpan WorkFrameInterval = TimeSpan.FromSeconds(
        AnimationPlaybackSpeed / WorkNormalPoseFramesPerSecond);
    // Reuse eight evenly spaced poses from the authored serious-to-normal brow
    // relaxation in reverse. An explicit 60fps sequence is deterministic on
    // 59/60Hz displays, unlike relying on the compositor to skip a 180fps clip.
    private static readonly int[] WorkSeriousEnterSourceFrameIndices =
        [23, 20, 16, 13, 10, 7, 3, 0];
    private static readonly TimeSpan WorkFastDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StableReactionEndpointHoldDuration =
        TimeSpan.FromMilliseconds(1875);
    private static readonly long VisualFrameDeadlineToleranceTicks =
        ToCharacterAnimationTicks(TimeSpan.FromMilliseconds(2));
    private static readonly long EdgeVisualFrameDeadlineToleranceTicks =
        ToStopwatchTicks(TimeSpan.FromMilliseconds(2));
    private static readonly TimeSpan MinimumNearSixtyHzPresentationInterval =
        TimeSpan.FromSeconds(1d / 62d);
    private static readonly TimeSpan MaximumNearSixtyHzPresentationInterval =
        TimeSpan.FromSeconds(1d / 58d);
    private static readonly TimeSpan EdgePeekFullyPeekedHold =
        TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan EdgePeekCycleInterval =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActionTransitionDuration = TimeSpan.Zero;
    private static readonly TimeSpan PetSizeTransitionDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PetSizePersistDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SpritePageCollectionDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpritePageCollectionRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SpritePageIdleTrimGracePeriod =
        TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SpritePageIdleTrimRetryDelay =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumSpritePageCollectionInterval =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FrameBlendDuration = TimeSpan.Zero;
    private static readonly TimeSpan EdgeFrameBlendDuration = TimeSpan.Zero;
    private static readonly bool UsesFrameBlendBuffers =
        FrameBlendDuration > TimeSpan.Zero ||
        EdgeFrameBlendDuration > TimeSpan.Zero ||
        ActionTransitionDuration > TimeSpan.Zero;
    private static readonly ArrayPool<byte> SpriteDecodeScratchPool =
        ArrayPool<byte>.Create(
            DisplayFrameByteCount,
            maxArraysPerBucket: 1);
    private static readonly TimeSpan AutomaticAnimationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EdgeRoamInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EdgeRoamBusyRetryDelay =
        TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EdgeRoamMaximumClockGap =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EdgeRoamDisembarkStraightenDuration =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PillowAnimationDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SnoreBubbleCycleDuration =
        TimeSpan.FromSeconds(2.4);
    private static readonly TimeSpan MaximumReminderWakeInterval =
        TimeSpan.FromHours(12);
    private static readonly TimeSpan ReminderSpritePreloadLeadTime =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MissedReminderGracePeriod =
        TimeSpan.FromSeconds(5);
    private const string TodoPoseActionName = "think";
    private static readonly string[] ReactionActionNames =
    [
        "cry", "cute", "like", "eat"
    ];
    private static readonly string[] SmoothActionNames =
    [
        "cry", "cute", "like", "eat", TodoPoseActionName
    ];
    private static readonly string[] LoopActionNames =
    [
        "cry", "like", "eat"
    ];
    private readonly IReadOnlyDictionary<string, SpriteAtlasPage> _spritePages;
    private readonly Dictionary<string, ResidentSpritePage> _residentSpritePages =
        new(StringComparer.Ordinal);
    private readonly SpritePageBufferPool _spritePageBufferPool = new();
    private readonly LinkedList<string> _residentSpritePageLru = new();
    private readonly HashSet<string> _pinnedSpritePageNames =
        new(StringComparer.Ordinal);
    private readonly string[] _spritePageWarmupOrder;
    private readonly SpriteFrame[] _wakeFrames;
    private readonly IReadOnlyDictionary<string, SpriteFrame[]> _actionSmoothFrames;
    private readonly IReadOnlyDictionary<string, SpriteFrame[]> _actionLoopFrames;
    private byte[] _spritePagePixels = Array.Empty<byte>();
    private readonly WriteableBitmap _displayFrameBuffer;
    private readonly byte[] _displayFramePixels =
        new byte[DisplayFrameByteCount];
    private readonly byte[] _frameBlendFromPixels = UsesFrameBlendBuffers
        ? new byte[DisplayFrameByteCount]
        : Array.Empty<byte>();
    private readonly byte[] _frameBlendTargetPixels = UsesFrameBlendBuffers
        ? new byte[DisplayFrameByteCount]
        : Array.Empty<byte>();
    private readonly byte[] _frameBlendOutputPixels = UsesFrameBlendBuffers
        ? new byte[DisplayFrameByteCount]
        : Array.Empty<byte>();
    private readonly SpriteFrame _idleFrame;
    private readonly SpriteFrame _todoFrame;
    private readonly SpriteFrame[] _edgeLeftFrames;
    private readonly SpriteFrame[] _edgeBottomFrames;
    private readonly SpriteFrame[] _roamBoardingFrames;
    private readonly SpriteFrame[] _roamFlightFrames;
    private readonly SpriteFrame[] _roamWaveFrames;
    private readonly SpriteFrame[] _reminderEnterFrames;
    private readonly SpriteFrame[] _reminderHoldFrames;
    private readonly SpriteFrame[] _workEnterFrames;
    private readonly SpriteFrame[] _workLoopFrames;
    private readonly SpriteFrame[] _workSeriousLoopFrames;
    private readonly SpriteFrame[] _workSeriousExitFrames;
    private readonly AnimationClip[] _reactionClips;
    private readonly AnimationClip _todoEnterClip;
    private readonly AnimationClip _todoExitClip;
    private readonly AnimationClip _reminderEnterClip;
    private readonly AnimationClip _reminderHoldClip;
    private readonly AnimationClip _reminderExitClip;
    private readonly AnimationClip _workEnterClip;
    private readonly AnimationClip _workLoopClip;
    private readonly AnimationClip _workSeriousLoopClip;
    private readonly AnimationClip _workSeriousEnterClip;
    private readonly AnimationClip _workSeriousExitClip;
    private readonly AnimationClip _workExitClip;
    private readonly AnimationClip?[] _automaticActivities;
    private readonly DispatcherTimer _automaticTimer;
    private readonly DispatcherTimer _petSizePersistTimer;
    private readonly DispatcherTimer _scheduledTaskTimer;
    private readonly DispatcherTimer _reminderSizeCommitTimer;
    private readonly DispatcherTimer _spritePagePrefetchDispatchTimer;
    private readonly DispatcherTimer _spritePageIdleTrimTimer;
    private readonly DispatcherTimer _spritePageCollectionTimer;
    private readonly DispatcherTimer _edgePeekHoldTimer;
    private readonly AnimationClock _snoreBubbleScaleClock;
    private readonly Queue<int> _automaticActivityBag = new();
    private readonly Random _random = new();
    private readonly Random _clickReactionRandom = new();
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly TodoStore _todoStore = TodoStore.CreateDefault();
    private readonly ObservableCollection<ScheduledTaskItem> _scheduledTasks = new();
    private ScheduledTaskStore _scheduledTaskStore = ScheduledTaskStore.CreateDefault();
    private StartupRegistration? _startupRegistration;
    private readonly Queue<ScheduledTaskItem> _reminderQueue = new();
    private readonly HashSet<Guid> _queuedReminderIds = new();
    // Counts are relative to each task's current DueAt/NextOrdinal. They are
    // intentionally monotonic while a reminder batch is awaiting
    // acknowledgement so a system-clock rollback cannot make already
    // presented occurrences disappear.
    private readonly Dictionary<Guid, long> _presentedReminderOccurrenceCounts = new();
    private readonly List<ScheduledTaskItem> _activeReminderBatch = [];
    private readonly List<ReminderOccurrence> _visibleReminderOccurrences = [];
    private readonly AppSettingsStore _settingsStore = AppSettingsStore.CreateDefault();
    private readonly TodoWindow _todoWindow;
    private readonly OwnedWindowPositioner.PositionCache _todoWindowPositionCache;
    private ReminderWindow? _reminderWindow;
    private readonly Action _processOutsideTodoCloseAction;
    private readonly Action _processTodoWindowPositionUpdateAction;
    private readonly Action _processSystemTimeChangedAction;
    private readonly Action _processSystemRecoveryAction;
    private readonly Action _processReminderWindowPositionUpdateAction;
    private readonly Action _processTodoOpenAfterEdgeRoamStopAction;
    private readonly Action _processTodoOpenAfterWorkExitAction;

    private BubbleMode _bubbleMode;
    private bool? _cuteBubblePlacedOnLeft;
    private bool? _cuteBubbleTailPlacedOnLeft;
    private bool _cuteBubbleTailReconciliationQueued;
    private Point _pointerDownPosition;
    private Point _pointerDownScreenPosition;
    private Vector _pointerDownPixelsPerDip = new(1d, 1d);
    // Null in production. UiStateChecks can replace the native cursor sample
    // without moving the operator's real pointer while exercising the exact
    // production gesture path.
    private Func<Point?>? _pointerScreenPointProviderForTesting = null;
    private Point _latestDragScreenPosition;
    private Vector _dragPointerOffsetFromWindowInPhysicalPixels;
    private double _dragContactTopOffsetInPhysicalPixels;
    private bool _directDragPhysicalGeometryReady;
    private bool _directDragTopClamped;
    private double _directDragTopClampPointerYInPhysicalPixels = double.NaN;
    private bool _pointerDown;
    private bool _dragStarted;
    private bool _dragInteractionActive;
    private bool _dragPreservesWorkMode;
    private EdgeDock _workEdgeDock;
    private long _workEdgeHandoffFrozenTimestamp;
    private EdgeDockDragContext? _edgeDockDragContext;
    private AnimationClip? _activeClip;
    private int _activeFrameIndex = -1;
    private long _activeClipStartedTimestamp;
    private long _activeFrameDeadlineTimestamp;
    private AnimationClip? _deferredActiveClipClock;
    private SpriteFrame? _deferredActiveClipClockFrame;
    private int _deferredActiveClipClockFrameIndex = -1;
    private TimeSpan _deferredActiveClipClockHoldDuration;
    private int _lastClickReactionIndex = -1;
    private WorkState _workState;
    private int _workPointerClickCount;
    private bool _workExitRequested;
    private bool _workSeriousEnterRequested;
    private bool _workSeriousExitRequested;
    private bool _workEnterAfterEdgePeekExitRequested;
    private bool _openTodoAfterWorkExitRequested;
    private bool _todoOpenAfterWorkExitQueued;
    private double _workLoopAnchorFramePosition;
    private double _workLoopPlaybackRate = 1;
    private double _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
    private double _workSeriousExitTargetFramePosition = double.PositiveInfinity;
    private double _workExitTargetFramePosition = double.PositiveInfinity;
    private long _workLoopAnchorTimestamp;
    private long _workFastUntilTimestamp;
    private bool _workModeIconVisualsInitialized;
    private bool _workModeIconVisualStateInitialized;
    private bool _workModeIconTransitionActive;
    private bool _workModeIconTargetWorking;
    private bool _workModeIconTransitionToWorking;
    private long _workModeIconTransitionStartedTimestamp;
    private WorkModeIconVisualState _workModeIconCurrentVisualState;
    private WorkModeIconVisualState _workModeIconTransitionStartState;
    private FrameworkElement? _workSunIconVisual;
    private FrameworkElement? _workMoonIconVisual;
    private FrameworkElement? _workSunModeHaloVisual;
    private FrameworkElement? _workMoonModeHaloVisual;
    private FrameworkElement? _workIconTwinkleVisual;
    private ScaleTransform? _workSunIconScale;
    private RotateTransform? _workSunIconRotate;
    private TranslateTransform? _workSunIconTranslate;
    private ScaleTransform? _workMoonIconScale;
    private RotateTransform? _workMoonIconRotate;
    private TranslateTransform? _workMoonIconTranslate;
    private ScaleTransform? _workSunModeHaloScale;
    private ScaleTransform? _workMoonModeHaloScale;
    private ScaleTransform? _workIconTwinkleScale;
    private RotateTransform? _workIconTwinkleRotate;
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
    private bool _edgeRoamingEnabled = true;
    private bool _isEdgeRoaming;
    private bool _isApplyingEdgeRoamPosition;
    private bool _edgeRoamClockStarted;
    private bool _edgeRoamBoardingPagesReady;
    private bool _edgeRoamFlightPagesReady;
    private bool _edgeRoamPreloadRequested;
    private bool _edgeRoamBoardingReverse;
    private bool _edgeRoamCurrentSupportPointValid;
    private bool _edgeRoamStopScheduleNext;
    private bool _edgeRoamStopInterrupted;
    private bool _openTodoAfterEdgeRoamStopRequested;
    private bool _todoOpenAfterEdgeRoamStopQueued;
    private int _edgeRoamDirection = 1;
    private int _edgeRoamBoardingStartIndex;
    private EdgeRoamPhase _edgeRoamPhase;
    private double _edgeRoamRouteStartDistance;
    private double _edgeRoamRouteLength;
    private double _edgeRoamApproachLength;
    private double _edgeRoamReturnLength;
    private double _edgeRoamLandingSeamElapsedSeconds;
    private double _edgeRoamTravelPoseFramesPerSecond =
        EdgeRoamPoseFramesPerSecond * AnimationPlaybackSpeed;
    private double _edgeRoamLogicalLeft;
    private double _edgeRoamLogicalTop;
    private double _edgeRoamFacingScaleX = 1;
    private double _edgeRoamRotationDegrees;
    private double _edgeRoamDisembarkStartRotationDegrees;
    private long _edgeRoamStartedTimestamp;
    private long _edgeRoamLastRenderingTimestamp;
    private long _nextEdgeRoamDueTimestamp;
    private long _nextAutomaticActivityDueTimestamp;
    private long _pillowBreathingDueTimestamp;
    private Rect _edgeRoamRouteBounds;
    private Point _edgeRoamStartPoint;
    private Point _edgeRoamRouteStartPoint;
    private Point _edgeRoamCurrentSupportPoint;
    private Point _edgeRoamDisembarkSupportPoint;
    private Vector _edgeRoamRouteTangent;
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
    private bool _petSizePreviewEnvelopePinnedForTodo;
    private bool _petSizeEnvelopePrepared;
    private bool _petSizeTargetUpdatePending;
    private bool _isPetSizeAdjustmentActive;
    private bool _petSizeAdjustmentValueChanged;
    private bool _petSizeCommitPending;
    private bool _petSizeTodoPositionNeedsUpdate;
    private bool _petSizeSettingsDirty;
    private bool _isApplyingPetSizeLayout;
    private bool _todoPositionUpdateQueued;
    private bool _reminderPositionUpdateQueued;
    private bool _outsideTodoCloseQueued;
    private PetSizeAnchor? _petSizePreviewAnchor;
    private PetSizeAnchor? _petSizeLogicalAnchor;
    private bool? _petSizeTodoChildOnLeft;
    private bool _automaticAnimationEnabled;
    private bool _isPillowBreathing;
    private bool _isSnoreBubbleAnimating;
    private bool _isClosing;
    private bool _suppressTodoWindowDeactivate;
    private bool _displaySettingsSubscribed;
    private bool _systemTimeChangedSubscribed;
    private bool _sessionSwitchSubscribed;
    private bool _powerModeSubscribed;
    private bool _userPreferenceChangedSubscribed;
    private bool _sessionInactive;
    private int _systemRecoveryQueued;
    private bool _suppressClickReactionAfterRoamInterruption;
    private bool _isReminderActive;
    private bool _isReminderPresentationDismissed;
    private bool _isTransientPetSizeOverride;
    private bool _isRestoringReminderSize;
    private double _reminderRestoreScale = 1;
    private double _reminderFacingScaleX = 1;
    private ScheduledTaskItem? _activeReminder;
    private long _totalReminderOccurrenceCount;
    private Func<DateTimeOffset> _nowProvider = static () => DateTimeOffset.Now;
    private SpriteFrame? _currentSpriteFrame;
    // Null in production. UiStateChecks installs this observer briefly to
    // verify descriptor-level handoffs that cannot be inferred from the final
    // pixels when two authored endpoints happen to be pixel-identical.
    private Action<string, string, int, int, int, int>?
        _spriteFrameDescriptorPublishedForTesting = null;
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
    private bool _synchronizeEdgePeekToRenderingCadence;
    private TimeSpan _lastVisualRenderingTime = TimeSpan.MinValue;
    private string? _renderDeferredSpritePageName;
    private bool _renderDeferredSpritePageUrgent;
    private bool _renderDeferredSpritePageCancellation;
    private string? _renderDeferredSpritePageFailureName;
    private string? _renderDeferredSpritePageFailureReason;
    private bool _residentSpritePageTrimPending;
    private string? _upcomingReminderPreloadPageName;
    private bool _spritePageCollectionInProgress;
    private long _frameBlendStartedTimestamp;
    private TimeSpan _activeFrameBlendDuration;
    private TimeSpan? _nextFrameBlendDuration;
    private TimeSpan _nextFrameMinimumHold;
    private HwndSource? _mainHwndSource;

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeAppearance.ExcludeFromAltTab(this);
        _snoreBubbleScaleClock = CreateSnoreBubbleScaleClock();
        SourceInitialized += MainWindow_SourceInitialized;
        _processOutsideTodoCloseAction = ProcessOutsideTodoClose;
        _processTodoWindowPositionUpdateAction = ProcessTodoWindowPositionUpdate;
        _processSystemTimeChangedAction = ProcessSystemTimeChanged;
        _processSystemRecoveryAction = ProcessSystemRecovery;
        _processReminderWindowPositionUpdateAction =
            ProcessReminderWindowPositionUpdate;
        _processTodoOpenAfterEdgeRoamStopAction =
            ProcessTodoOpenAfterEdgeRoamStop;
        _processTodoOpenAfterWorkExitAction =
            ProcessTodoOpenAfterWorkExit;
        _spritePagePrefetchDispatchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _spritePagePrefetchDispatchTimer.Tick +=
            SpritePagePrefetchDispatchTimer_Tick;
        _spritePageIdleTrimTimer = new DispatcherTimer
        {
            Interval = SpritePageIdleTrimGracePeriod
        };
        _spritePageIdleTrimTimer.Tick += SpritePageIdleTrimTimer_Tick;
        _spritePageCollectionTimer = new DispatcherTimer
        {
            Interval = SpritePageCollectionDelay
        };
        _spritePageCollectionTimer.Tick += SpritePageCollectionTimer_Tick;
        _edgePeekHoldTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = EdgePeekFullyPeekedHold
        };
        _edgePeekHoldTimer.Tick += EdgePeekHoldTimer_Tick;
        _lastObservedSpritePageCollectionGeneration =
            GC.CollectionCount(GC.MaxGeneration);

        _spritePages = LoadSpritePages();
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
            SmoothActionNames.ToDictionary(
                actionName => actionName,
                actionName => LoadNumberedFrameSequence(
                    $"action-{actionName}",
                    $"Assets/luban-{actionName}-smooth-"),
                StringComparer.Ordinal));
        _actionLoopFrames = new ReadOnlyDictionary<string, SpriteFrame[]>(
            LoopActionNames.ToDictionary(
                actionName => actionName,
                actionName => LoadNumberedFrameSequence(
                    $"loop-{actionName}",
                    $"Assets/luban-{actionName}-loop-",
                    ActionLoopFrameCount),
                StringComparer.Ordinal));
        _todoFrame = _actionSmoothFrames[TodoPoseActionName][^1];
        _edgeLeftFrames = LoadEdgeFrameSequence(
            "edge-left",
            "Assets/luban-edge-left-smooth-");
        _edgeBottomFrames = LoadEdgeFrameSequence(
            "edge-bottom",
            "Assets/luban-edge-bottom-smooth-");
        _roamBoardingFrames = LoadNumberedFrameSequence(
            "roam-boarding",
            "Assets/luban-roam-boarding-");
        _roamFlightFrames = LoadNumberedFrameSequence(
            "roam-flight",
            "Assets/luban-roam-flight-");
        _roamWaveFrames = LoadOptionalNumberedFrameSequence(
            "roam-wave",
            "Assets/luban-roam-wave-");
        if (_roamBoardingFrames.Length < 48 ||
            _roamFlightFrames.Length < 48 ||
            (_roamWaveFrames.Length > 0 && _roamWaveFrames.Length < 48))
        {
            throw new InvalidOperationException(
                "Roaming boarding/flight must contain at least 48 frames and " +
                "an optional wave sequence cannot be shorter than 48 frames.");
        }
        _reminderEnterFrames = LoadNumberedFrameSequence(
            "action-reminder-enter",
            "Assets/luban-reminder-enter-",
            expectedFrameCount: 33);
        _reminderHoldFrames = LoadNumberedFrameSequence(
            "action-reminder-hold",
            "Assets/luban-reminder-hold-",
            expectedFrameCount: 48);
        _workEnterFrames = LoadNumberedFrameSequence(
            "work-enter",
            "Assets/luban-work-enter-",
            expectedFrameCount: WorkEnterFrameCount);
        _workLoopFrames = LoadNumberedFrameSequence(
            "work-loop",
            "Assets/luban-work-loop-",
            expectedFrameCount: WorkLoopFrameCount);
        _workSeriousLoopFrames = LoadNumberedFrameSequence(
            "work-serious-loop",
            "Assets/luban-work-serious-loop-",
            expectedFrameCount: WorkSeriousLoopFrameCount);
        _workSeriousExitFrames = LoadNumberedFrameSequence(
            "work-serious-exit",
            "Assets/luban-work-serious-exit-",
            expectedFrameCount: WorkSeriousExitFrameCount);
        // Keep only the idle page permanently hot. Wake and action pages use
        // the same rolling look-ahead and return to the 12 MiB idle target.
        AddPinnedSpritePageNames([_idleFrame]);
        _spritePageWarmupOrder = BuildSpritePageWarmupOrder();
        _reactionClips =
        [
            CreateMotionClip("呜……主人要哄哄我", "cry"),
            CreateMotionClip("给你卖个萌 ♡", "cute"),
            CreateMotionClip("主人真棒！", "like"),
            CreateMotionClip("吃块饼干，补充能量！", "eat")
        ];
        _todoExitClip = CreateTodoExitClip();
        _todoEnterClip = CreateTodoEnterClip();
        _reminderEnterClip = CreateReminderEnterClip();
        _reminderHoldClip = CreateReminderHoldClip();
        _reminderExitClip = CreateReminderExitClip();
        _workEnterClip = CreateWorkClip(
            "work-enter",
            _workEnterFrames,
            reverse: false);
        _workLoopClip = CreateWorkClip(
            "work-loop",
            _workLoopFrames,
            reverse: false);
        _workSeriousLoopClip = CreateWorkClip(
            "work-serious-loop",
            _workSeriousLoopFrames,
            reverse: false);
        _workSeriousEnterClip = CreateWorkSampledClip(
            "work-serious-enter",
            _workSeriousExitFrames,
            WorkSeriousEnterSourceFrameIndices);
        _workSeriousExitClip = CreateWorkClip(
            "work-serious-exit",
            _workSeriousExitFrames,
            reverse: false);
        _workExitClip = CreateWorkClip(
            "work-exit",
            _workEnterFrames,
            reverse: true);
        _automaticActivities = [.. _reactionClips, null];
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
        _todoWindow.EdgeRoamingEnabledChanged +=
            TodoWindow_EdgeRoamingEnabledChanged;
        _todoWindow.StartupEnabledChanged += TodoWindow_StartupEnabledChanged;
        _todoWindow.CloseRequested += TodoWindow_CloseRequested;
        _todoWindow.ExitRequested += TodoWindow_ExitRequested;
        _todoWindow.ImeCompositionChanged += TodoWindow_ImeCompositionChanged;
        _todoWindow.Deactivated += TodoWindow_Deactivated;
        _todoWindow.LostKeyboardFocus += TodoWindow_LostKeyboardFocus;
        _todoWindow.DpiChanged += TodoWindow_DpiChanged;
        _todoWindow.SizeChanged += TodoWindow_SizeChanged;
        _todoWindow.LocationChanged += TodoWindow_LocationChanged;
        DpiChanged += MainWindow_DpiChanged;

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
        _edgeRoamingEnabled = settings.EdgeRoamingEnabled;
        _petSizeScale = NormalizePetSizeScale(settings.PetSizeScale);
        _persistedPetSizeScale = _petSizeScale;
        _petSizeTargetScale = _petSizeScale;
        _todoWindow.SetPetSizeScale(_petSizeScale);
        _todoWindow.SetEdgeRoamingEnabled(_edgeRoamingEnabled);
        ApplyPetSizeScale(_petSizeScale, persist: false, preservePosition: false);

        _automaticTimer = new DispatcherTimer
        {
            Interval = AutomaticAnimationInterval
        };
        _automaticTimer.Tick += AutomaticTimer_Tick;
    }

    internal void ConfigureStartupRegistration(StartupRegistration registration)
    {
        _startupRegistration = registration ??
            throw new ArgumentNullException(nameof(registration));
        if (registration.TryReadAndRepair(
                out var enabled,
                out var error))
        {
            _todoWindow.SetStartupEnabled(enabled);
            return;
        }

        _todoWindow.SetStartupEnabled(
            enabled,
            $"开机自启状态读取或修复失败：{error}");
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
        return [];
    }

    private SpriteFrame[] LoadOptionalNumberedFrameSequence(
        string pageNamePrefix,
        string resourcePrefix)
    {
        foreach (var pageName in _spritePages.Keys)
        {
            if (TryGetNumberedSequencePagePart(
                    pageName,
                    pageNamePrefix,
                    out _))
            {
                return LoadNumberedFrameSequence(
                    pageNamePrefix,
                    resourcePrefix);
            }
        }

        return [];
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

    private AnimationClip CreateMotionClip(
        string message,
        string actionName,
        string? poseActionName = null)
    {
        poseActionName ??= actionName;
        var profile = ResolveMotionClipProfile(actionName, poseActionName);
        var timeline = BuildActionTimeline(
            poseActionName,
            profile.SmoothFrameCount);
        var loopFrames = profile.LoopCycleCount == 0
            ? Array.Empty<SpriteFrame>()
            : _actionLoopFrames.TryGetValue(actionName, out var authoredLoopFrames)
                ? authoredLoopFrames
                : throw new InvalidOperationException(
                    $"Motion action '{actionName}' requires loop frames.");
        var frames = new List<AnimationFrame>(
            (timeline.Frames.Length - 1) * 2 +
            loopFrames.Length * profile.LoopCycleCount);
        for (var timelineIndex = 1;
             timelineIndex < timeline.Frames.Length;
             timelineIndex++)
        {
            var holdDuration =
                timelineIndex == timeline.Frames.Length - 1 &&
                profile.EndpointHoldDuration > TimeSpan.Zero
                    ? profile.EndpointHoldDuration
                    : profile.FrameInterval;
            frames.Add(new AnimationFrame(
                timeline.Frames[timelineIndex],
                holdDuration));
        }

        // Clip indices omit timeline idle at index 0, so this points to the
        // first frame resident on the action page. It is a prefetch target, not
        // the loop endpoint or the moment at which the action is considered done.
        var actionFrameIndex = timeline.ActionStartIndex - 1;
        for (var cycle = 0; cycle < profile.LoopCycleCount; cycle++)
        {
            foreach (var loopFrame in loopFrames)
            {
                frames.Add(new AnimationFrame(
                    loopFrame,
                    profile.FrameInterval));
            }
        }

        for (var timelineIndex = timeline.Frames.Length - 2;
             timelineIndex >= 0;
             timelineIndex--)
        {
            frames.Add(new AnimationFrame(
                timeline.Frames[timelineIndex],
                profile.FrameInterval));
        }

        return new AnimationClip(message, actionName, frames.ToArray(), actionFrameIndex);
    }

    private MotionClipProfile ResolveMotionClipProfile(
        string actionName,
        string poseActionName)
    {
        var availableSmoothFrameCount = _actionSmoothFrames[poseActionName].Length;
        var profile = actionName switch
        {
            "cute" => new MotionClipProfile(
                CuteCleanSmoothFrameCount,
                LoopCycleCount: 0,
                EndpointHoldDuration: StableReactionEndpointHoldDuration,
                FrameInterval: MotionFrameInterval),
            _ => new MotionClipProfile(
                availableSmoothFrameCount,
                ActionLoopCycleCount,
                EndpointHoldDuration: TimeSpan.Zero,
                FrameInterval: MotionFrameInterval)
        };

        if (profile.SmoothFrameCount <= 0 ||
            profile.SmoothFrameCount > availableSmoothFrameCount ||
            profile.LoopCycleCount < 0 ||
            profile.FrameInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Invalid motion profile for '{actionName}': " +
                $"pose={poseActionName}, " +
                $"smooth={profile.SmoothFrameCount}/{availableSmoothFrameCount}, " +
                $"loops={profile.LoopCycleCount}, " +
                $"interval={profile.FrameInterval}.");
        }

        return profile;
    }

    private AnimationClip CreateTodoExitClip()
    {
        var timeline = BuildActionTimeline(TodoPoseActionName);
        var frames = new List<AnimationFrame>(timeline.Frames.Length);
        for (var timelineIndex = timeline.Frames.Length - 1;
             timelineIndex >= 0;
             timelineIndex--)
        {
            frames.Add(new AnimationFrame(
                timeline.Frames[timelineIndex],
                TodoMotionFrameInterval));
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
                MotionFrameInterval);
        }

        return new AnimationClip(
            string.Empty,
            actionName,
            frames,
            ActionFrameIndex: 0);
    }

    private static AnimationClip CreateWorkClip(
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
            frames[index] = new AnimationFrame(
                sourceFrames[sourceIndex],
                WorkFrameInterval);
        }

        return new AnimationClip(
            string.Empty,
            actionName,
            frames,
            ActionFrameIndex: 0);
    }

    private static AnimationClip CreateWorkSampledClip(
        string actionName,
        IReadOnlyList<SpriteFrame> sourceFrames,
        IReadOnlyList<int> sourceFrameIndices)
    {
        var frames = new AnimationFrame[sourceFrameIndices.Count];
        for (var index = 0; index < sourceFrameIndices.Count; index++)
        {
            var sourceIndex = sourceFrameIndices[index];
            if (sourceIndex < 0 || sourceIndex >= sourceFrames.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceFrameIndices),
                    sourceIndex,
                    $"Clip '{actionName}' sampled outside its source sequence.");
            }

            frames[index] = new AnimationFrame(
                sourceFrames[sourceIndex],
                WorkFrameInterval);
        }

        return new AnimationClip(
            string.Empty,
            actionName,
            frames,
            ActionFrameIndex: 0);
    }

    private ActionTimeline BuildActionTimeline(
        string actionName,
        int? actionFrameCount = null)
    {
        var actionFrames = _actionSmoothFrames[actionName];
        var selectedActionFrameCount = actionFrameCount ?? actionFrames.Length;
        if (selectedActionFrameCount <= 0 ||
            selectedActionFrameCount > actionFrames.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actionFrameCount),
                selectedActionFrameCount,
                $"Action '{actionName}' has {actionFrames.Length} smooth frames.");
        }

        var frames = new List<SpriteFrame>(
            1 + _wakeFrames.Length + selectedActionFrameCount);

        void Add(SpriteFrame frame)
        {
            frames.Add(frame);
        }

        Add(_idleFrame);
        foreach (var wakeFrame in _wakeFrames)
        {
            Add(wakeFrame);
        }

        var actionStartIndex = frames.Count;
        for (var actionFrameIndex = 0;
             actionFrameIndex < selectedActionFrameCount;
             actionFrameIndex++)
        {
            Add(actionFrames[actionFrameIndex]);
        }

        return new ActionTimeline(
            frames.ToArray(),
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
                !string.Equals(
                    pageDescriptor.Resource,
                    $"Assets/sprite-pages/luban-{pageName}.pbgra.br",
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
                    CreatePackUri(pageDescriptor.Resource),
                    pageDescriptor.Width,
                    pageDescriptor.Height,
                    pageDescriptor.UncompressedByteCount,
                    pageDescriptor.PayloadByteCount,
                    pageDescriptor.CompressedByteCount,
                    pageDescriptor.Encoding,
                    pageDescriptor.ContentSha256,
                    pageDescriptor.DecodedSha256,
                    Convert.FromHexString(pageDescriptor.ContentSha256),
                    Convert.FromHexString(pageDescriptor.DecodedSha256),
                    pageDescriptor.UniqueSpriteCount,
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

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _mainHwndSource =
            PresentationSource.FromVisual(this) as HwndSource;
        _mainHwndSource?.AddHook(MainWindowWindowProc);
    }

    private IntPtr MainWindowWindowProc(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message != WmNcHitTest ||
            !PetVisual.IsLoaded ||
            PetVisual.ActualWidth <= 0 ||
            PetVisual.ActualHeight <= 0)
        {
            return IntPtr.Zero;
        }

        try
        {
            var packedPoint = longParameter.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packedPoint & 0xFFFF)),
                unchecked((short)((packedPoint >> 16) & 0xFFFF)));
            // Hit-test the transformed sprite rather than the unrotated
            // Viewbox. During vertical roaming the rotated character can
            // extend beyond the Viewbox's layout rectangle; those visible
            // pixels must still accept a click or drag that interrupts roam.
            var petPoint = PetVisual.PointFromScreen(screenPoint);
            if (petPoint.X < 0 ||
                petPoint.Y < 0 ||
                petPoint.X > PetVisual.ActualWidth ||
                petPoint.Y > PetVisual.ActualHeight)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }
        }
        catch (InvalidOperationException)
        {
            // The visual can briefly detach while Windows is closing it.
        }

        return IntPtr.Zero;
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

        if (!_sessionSwitchSubscribed)
        {
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            _sessionSwitchSubscribed = true;
        }

        if (!_powerModeSubscribed)
        {
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            _powerModeSubscribed = true;
        }

        if (!_userPreferenceChangedSubscribed)
        {
            SystemEvents.UserPreferenceChanged +=
                SystemEvents_UserPreferenceChanged;
            _userPreferenceChangedSubscribed = true;
        }

        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        MoveMainWindowTo(
            Left + workArea.Right - ScreenEdgeMargin - visiblePetBounds.Right,
            Top + workArea.Bottom - ScreenEdgeMargin - visiblePetBounds.Bottom);
        _automaticAnimationEnabled = true;
        RefreshSnoreBubbleAnimationState();
        ScheduleNextEdgeRoam(Stopwatch.GetTimestamp(), EdgeRoamInterval);
        ProcessScheduledTasksAt(_nowProvider());
        RestartAutomaticCountdown();
        _spritePageWarmupEnabled = true;
        ResumeSpritePageWarmup();
        RefreshWorkModeButton();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (!_isApplyingPetSizeLayout && !_isApplyingEdgeRoamPosition)
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

        RefreshReminderWindowPosition();
        if (!BubblePopup.IsOpen)
        {
            return;
        }

        if (_bubbleMode == BubbleMode.Cute)
        {
            UpdateCuteBubblePlacementAndTail();
        }

        // WPF Popup 使用独立 HWND，父窗口移动时不会自行重算屏幕坐标。
        // 轻微重设偏移会在捕获式拖动的每次 LocationChanged 中强制它跟随宠物。
        var horizontalOffset = BubblePopup.HorizontalOffset;
        BubblePopup.HorizontalOffset = horizontalOffset + 0.01;
        BubblePopup.HorizontalOffset = horizontalOffset;
        QueueCuteBubbleTailReconciliation();
    }

    private void MainWindow_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        _todoWindowPositionCache.InvalidateGeometry();
        QueueTodoWindowPositionUpdate();
        RefreshReminderWindowPosition();
        if (BubblePopup.IsOpen && _bubbleMode == BubbleMode.Cute)
        {
            UpdateCuteBubblePlacementAndTail();
        }
    }

    private void TodoWindow_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        _todoWindowPositionCache.InvalidateGeometry();
        QueueTodoWindowPositionUpdate();
        RefreshReminderWindowPosition();
    }

    private void TodoWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _todoWindowPositionCache.InvalidateGeometry();
        QueueTodoWindowPositionUpdate();
        RefreshReminderWindowPosition();
    }

    private void TodoWindow_LocationChanged(object? sender, EventArgs e)
    {
        RefreshReminderWindowPosition();
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        QueueSystemRecovery();
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

    private void SystemEvents_SessionSwitch(
        object sender,
        SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                QueueSessionInactive();
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                QueueSystemRecovery();
                break;
        }
    }

    private void SystemEvents_PowerModeChanged(
        object sender,
        PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            QueueSessionInactive();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            QueueSystemRecovery();
        }
    }

    private void SystemEvents_UserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or
            UserPreferenceCategory.General or
            UserPreferenceCategory.Window)
        {
            QueueSystemRecovery();
        }
    }

    private void QueueSessionInactive()
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

                    _sessionInactive = true;
                    StopIdleSpritePageTrim();
                    CancelPetPointerInteractionForInterruption();
                    CancelTodoOpenAfterEdgeRoamStop();
                    CancelTodoOpenAfterWorkExit();
                    StopWorkModeImmediately(restoreIdleFrame: true);
                    _suppressTodoWindowDeactivate = true;
                    _outsideTodoCloseGeneration++;
                    _automaticTimer.Stop();
                    StopEdgeRoaming(
                        scheduleNext: false,
                        restoreIdleFrame: true,
                        interrupted: true,
                        immediate: true);
                }));
        }
        catch (InvalidOperationException)
        {
            // The dispatcher is already shutting down.
        }
    }

    private void QueueSystemRecovery()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(ref _systemRecoveryQueued, 1) != 0)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                _processSystemRecoveryAction);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _systemRecoveryQueued, 0);
        }
    }

    private void ProcessSystemRecovery()
    {
        Interlocked.Exchange(ref _systemRecoveryQueued, 0);
        if (_isClosing)
        {
            return;
        }

        _sessionInactive = false;
        var timestamp = Stopwatch.GetTimestamp();
        ConsumeLatestPetSizeInputAt(timestamp);
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
        if (_petSizePreviewEnvelopePinnedForTodo &&
            _bubbleMode == BubbleMode.Todo &&
            _todoWindow.IsVisible)
        {
            EnsureTodoPetSizePreviewEnvelope();
        }

        StopEdgeRoaming(
            scheduleNext: false,
            restoreIdleFrame: true,
            interrupted: true,
            immediate: true);
        ExitEdgePeek(restartAutomaticCountdown: false);
        // A monitor/DPI recovery may clamp the window away from the exact edge
        // that was recorded while working. Do not hand a stale edge to idle
        // when the user later chooses 去睡觉.
        CancelPendingWorkEdgeHandoff(resumeWork: true);

        _petSizeLogicalAnchor = null;
        _todoWindowPositionCache.InvalidateGeometry();
        _todoWindow.RecoverAfterSystemResume();
        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        var correctedVisibleLeft = Math.Clamp(
            visiblePetBounds.Left,
            workArea.Left,
            Math.Max(
                workArea.Left,
                workArea.Right - visiblePetBounds.Width));
        var correctedVisibleTop = Math.Clamp(
            visiblePetBounds.Top,
            workArea.Top,
            Math.Max(
                workArea.Top,
                workArea.Bottom - visiblePetBounds.Height));
        MoveMainWindowTo(
            Left + correctedVisibleLeft - visiblePetBounds.Left,
            Top + correctedVisibleTop - visiblePetBounds.Top);

        if (_todoWindow.IsVisible)
        {
            UpdateTodoWindowPosition();
        }

        _suppressTodoWindowDeactivate = false;

        ScheduleNextEdgeRoam(timestamp, EdgeRoamInterval);
        RestartAutomaticCountdown();
        ProcessScheduledTasksAt(_nowProvider());
        if (_isReminderActive)
        {
            MovePetToReminderCorner();
            ConfigureReminderBubblePlacement();
            RefreshReminderBubbleOffset();
        }

        if (_todoWindow.IsVisible)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    if (_isClosing || !_todoWindow.IsVisible)
                    {
                        return;
                    }

                    _todoWindowPositionCache.InvalidateGeometry();
                    UpdateTodoWindowPosition();
                }));
        }

        RefreshWorkModeButton();
        RequestIdleSpritePageTrim();

    }

    private void ProcessSystemTimeChanged()
    {
        if (_isClosing)
        {
            return;
        }

        ProcessScheduledTasksAt(_nowProvider());
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        RestartAutomaticCountdown();
        DeferIdleSpritePageTrim();

        if (_openTodoAfterEdgeRoamStopRequested &&
            !IsWithin(
                e.OriginalSource as DependencyObject ??
                e.Source as DependencyObject,
                PetHost))
        {
            CancelTodoOpenAfterEdgeRoamStop();
        }
        if (_openTodoAfterWorkExitRequested &&
            !IsWithin(
                e.OriginalSource as DependencyObject ??
                e.Source as DependencyObject,
                PetHost))
        {
            CancelTodoOpenAfterWorkExit();
        }

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
        DeferIdleSpritePageTrim();
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
                _isPetSizePreviewSessionActive ? _petSizeTodoChildOnLeft : null,
                new Size(
                    TodoWindow.DefaultWindowWidth,
                    TodoWindow.DefaultWindowHeight)))
        {
            UpdateTodoWindowTailPlacement(childIsOnLeft);
            return;
        }

        // Keep the WPF fallback authoritative too. If the native path failed
        // during a transient lock-screen or topology change, never reuse the
        // damaged HWND's ActualWidth/ActualHeight and make the half-size state
        // persistent on the next open.
        _todoWindow.Width = TodoWindow.DefaultWindowWidth;
        _todoWindow.Height = TodoWindow.DefaultWindowHeight;
        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var bubbleWidth = TodoWindow.DefaultWindowWidth;
        var bubbleHeight = TodoWindow.DefaultWindowHeight;
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

        var actualLeft = Math.Clamp(
            desiredLeft,
            workArea.Left,
            maximumLeft);
        var childIsActuallyOnLeft =
            actualLeft + bubbleWidth / 2 <=
            (petLeft + petRight) / 2;
        _todoWindow.Left = actualLeft;
        _todoWindow.Top = Math.Clamp(desiredTop, workArea.Top, maximumTop);
        UpdateTodoWindowTailPlacement(childIsActuallyOnLeft);
    }

    private void UpdateTodoWindowTailPlacement(bool childIsOnLeft)
    {
        try
        {
            var petTopLeft =
                PetSizeViewbox.PointToScreen(new Point(0, 0));
            var petBottomRight = PetSizeViewbox.PointToScreen(
                new Point(
                    PetSizeViewbox.ActualWidth,
                    PetSizeViewbox.ActualHeight));
            var childOrigin =
                _todoWindow.PointToScreen(new Point(0, 0));
            var childDpiScaleY =
                VisualTreeHelper.GetDpi(_todoWindow).DpiScaleY;
            var petCenterY = (petTopLeft.Y + petBottomRight.Y) / 2;
            var tailCenterY =
                (petCenterY - childOrigin.Y) /
                Math.Max(0.01, childDpiScaleY);
            _todoWindow.SetTailPlacement(
                childIsOnLeft,
                tailCenterY);
        }
        catch
        {
            _todoWindow.SetTailOnRight(childIsOnLeft);
        }
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
            return Math.Round(value, MidpointRounding.AwayFromZero);
        }

        var transform = compositionTarget.TransformToDevice;
        var scale = horizontal ? transform.M11 : transform.M22;
        return double.IsFinite(scale) && scale > 0
            ? Math.Round(
                  value * scale,
                  MidpointRounding.AwayFromZero) / scale
            : Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private void MoveMainWindowTo(double logicalLeft, double logicalTop)
    {
        if (OwnedWindowPositioner.TrySetPosition(
                this,
                logicalLeft,
                logicalTop))
        {
            return;
        }

        // The native path already performs DPI conversion and physical-pixel
        // snapping. Only the pre-HWND/failure fallback needs the WPF values.
        Left = SnapDipToPhysicalPixel(
            logicalLeft,
            horizontal: true);
        Top = SnapDipToPhysicalPixel(
            logicalTop,
            horizontal: false);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isTransientPetSizeOverride)
        {
            PersistLatestPetSizeForShutdownAt(Stopwatch.GetTimestamp());
        }
        _isClosing = true;
        CancelTodoOpenAfterWorkExit();
        _workState = WorkState.Idle;
        _workModeIconTransitionActive = false;
        _workModeIconTransitionStartedTimestamp = 0;
        _workExitRequested = false;
        _workSeriousEnterRequested = false;
        _workSeriousExitRequested = false;
        _workEnterAfterEdgePeekExitRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workLoopAnchorTimestamp = 0;
        _workFastUntilTimestamp = 0;
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
        if (_sessionSwitchSubscribed)
        {
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
            _sessionSwitchSubscribed = false;
        }
        if (_powerModeSubscribed)
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            _powerModeSubscribed = false;
        }
        if (_userPreferenceChangedSubscribed)
        {
            SystemEvents.UserPreferenceChanged -=
                SystemEvents_UserPreferenceChanged;
            _userPreferenceChangedSubscribed = false;
        }
        _automaticAnimationEnabled = false;
        _isEdgeRoaming = false;
        _edgeRoamPhase = EdgeRoamPhase.None;
        _edgeRoamClockStarted = false;
        _edgeRoamBoardingPagesReady = false;
        _edgeRoamFlightPagesReady = false;
        _edgeRoamBoardingReverse = false;
        _edgeRoamCurrentSupportPointValid = false;
        _edgeRoamStopScheduleNext = false;
        _edgeRoamStopInterrupted = false;
        _openTodoAfterEdgeRoamStopRequested = false;
        _todoOpenAfterEdgeRoamStopQueued = false;
        _nextEdgeRoamDueTimestamp = 0;
        _nextAutomaticActivityDueTimestamp = 0;
        _pillowBreathingDueTimestamp = 0;
        RefreshSnoreBubbleAnimationState();
        _edgePeekHoldTimer.Stop();
        _edgePeekHoldTimer.Tick -= EdgePeekHoldTimer_Tick;
        _petSizePersistTimer.Stop();
        _petSizePersistTimer.Tick -= PetSizePersistTimer_Tick;
        _reminderSizeCommitTimer.Stop();
        _reminderSizeCommitTimer.Tick -= ReminderSizeCommitTimer_Tick;
        _scheduledTaskTimer.Stop();
        _scheduledTaskTimer.Tick -= ScheduledTaskTimer_Tick;
        _spritePagePrefetchDispatchTimer.Stop();
        _spritePagePrefetchDispatchTimer.Tick -=
            SpritePagePrefetchDispatchTimer_Tick;
        StopIdleSpritePageTrim();
        _spritePageIdleTrimTimer.Tick -= SpritePageIdleTrimTimer_Tick;
        _spritePageCollectionTimer.Stop();
        _spritePageCollectionTimer.Tick -= SpritePageCollectionTimer_Tick;
        _isPetSizeTransitioning = false;
        _isPetSizePreviewSessionActive = false;
        _petSizePreviewEnvelopePinnedForTodo = false;
        _petSizeEnvelopePrepared = false;
        _petSizeTargetUpdatePending = false;
        StopVisualClock();
        if (_mainHwndSource is { } mainHwndSource)
        {
            mainHwndSource.RemoveHook(MainWindowWindowProc);
            _mainHwndSource = null;
        }
        StopFrameBlend(snapToTarget: false);
        _automaticTimer.Stop();
        _automaticTimer.Tick -= AutomaticTimer_Tick;
        DpiChanged -= MainWindow_DpiChanged;
        _todoWindow.DpiChanged -= TodoWindow_DpiChanged;
        _todoWindow.SizeChanged -= TodoWindow_SizeChanged;
        _todoWindow.LocationChanged -= TodoWindow_LocationChanged;
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        _isReminderActive = false;
        _isReminderPresentationDismissed = false;
        _activeReminder = null;
        _activeReminderBatch.Clear();
        _visibleReminderOccurrences.Clear();
        _reminderQueue.Clear();
        _queuedReminderIds.Clear();
        _presentedReminderOccurrenceCounts.Clear();
        _totalReminderOccurrenceCount = 0;
        _edgeDock = EdgeDock.None;
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HideBubbleVisuals();
        _suppressTodoWindowDeactivate = true;
        _todoWindow.Deactivated -= TodoWindow_Deactivated;
        _todoWindow.CloseForApplication();
        if (_reminderWindow is { } reminderWindow)
        {
            reminderWindow.AcknowledgeRequested -=
                ReminderWindow_AcknowledgeRequested;
            reminderWindow.DismissRequested -=
                ReminderWindow_DismissRequested;
            reminderWindow.CloseForApplication();
            _reminderWindow = null;
        }
        SaveTodos();
        SaveScheduledTasks();
        ClearResidentSpritePages();
    }

    private void PetHost_PreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        _workEnterAfterEdgePeekExitRequested = false;
        RefreshWorkModeButton();
    }

    private void CancelPetPointerInteractionForInterruption()
    {
        _pointerDown = false;
        _dragStarted = false;
        _dragInteractionActive = false;
        _dragPreservesWorkMode = false;
        _workEdgeDock = EdgeDock.None;
        _workEdgeHandoffFrozenTimestamp = 0;
        _pointerDownPosition = default;
        _pointerDownScreenPosition = default;
        _pointerDownPixelsPerDip = new Vector(1d, 1d);
        _latestDragScreenPosition = default;
        _dragPointerOffsetFromWindowInPhysicalPixels = default;
        _dragContactTopOffsetInPhysicalPixels = 0;
        _directDragPhysicalGeometryReady = false;
        _directDragTopClamped = false;
        _directDragTopClampPointerYInPhysicalPixels = double.NaN;
        _edgeDockDragContext = null;
        _suppressClickReactionAfterRoamInterruption = false;
        _workPointerClickCount = 0;
        _workEnterAfterEdgePeekExitRequested = false;
        if (PetHost.IsMouseCaptured)
        {
            // All gesture state is already clear when LostMouseCapture runs,
            // so it cannot finish a stale drag or restart an idle reaction.
            PetHost.ReleaseMouseCapture();
        }

        RefreshSnoreBubbleAnimationState();
        RefreshWorkModeButton();
    }

    private void PetHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        DeferIdleSpritePageTrim();
        _workEnterAfterEdgePeekExitRequested = false;
        _dragPreservesWorkMode = false;
        _workPointerClickCount = e.ClickCount;
        if (_isReminderActive || _isTransientPetSizeOverride)
        {
            CancelTodoOpenAfterEdgeRoamStop();
            e.Handled = true;
            return;
        }

        if (_isPetSizePreviewSessionActive &&
            !_petSizePreviewEnvelopePinnedForTodo)
        {
            CommitPetSizePreviewSession(persist: true);
        }

        CancelTodoOpenAfterEdgeRoamStop();
        CancelTodoOpenAfterWorkExit();
        var interruptedRoam = _isEdgeRoaming;
        _pointerDownPixelsPerDip = GetPointerPixelsPerDip();
        _pointerDownPosition = e.GetPosition(this);
        _pointerDownScreenPosition = GetPointerScreenPointOrFallback(
            _pointerDownPosition,
            new Point(double.NaN, double.NaN),
            allowLocalFallback: !interruptedRoam);
        _latestDragScreenPosition = _pointerDownScreenPosition;
        _suppressClickReactionAfterRoamInterruption = interruptedRoam;
        StopEdgeRoaming(
            scheduleNext: true,
            restoreIdleFrame: true,
            interrupted: true);
        StopPillowBreathing();
        _automaticTimer.Stop();
        _dragInteractionActive = true;
        _pointerDown = true;
        _dragStarted = false;
        _edgeDockDragContext = null;
        _directDragPhysicalGeometryReady = false;
        _directDragTopClamped = false;
        _directDragTopClampPointerYInPhysicalPixels = double.NaN;
        if (!PetHost.CaptureMouse())
        {
            _pointerDown = false;
            _dragStarted = false;
            _dragInteractionActive = false;
            _dragPreservesWorkMode = false;
            _pointerDownPixelsPerDip = new Vector(1d, 1d);
            _edgeDockDragContext = null;
            _suppressClickReactionAfterRoamInterruption = false;
            _workPointerClickCount = 0;
            RestartAutomaticCountdown();
        }
        RefreshSnoreBubbleAnimationState();
        RefreshWorkModeButton();
        e.Handled = true;
    }

    private void PetHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragInteractionActive ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        var currentScreenPosition = GetPointerScreenPointOrFallback(
            currentPosition,
            _latestDragScreenPosition,
            allowLocalFallback:
                !_suppressClickReactionAfterRoamInterruption);
        _latestDragScreenPosition = currentScreenPosition;
        if (_dragStarted)
        {
            ContinueDirectPetDrag(
                currentPosition,
                currentScreenPosition);
            e.Handled = true;
            return;
        }

        if (!_pointerDown)
        {
            return;
        }

        if (!TryBeginPetPointerDrag(
                currentPosition,
                currentScreenPosition))
        {
            return;
        }

        e.Handled = true;
    }

    private bool TryBeginPetPointerDrag(
        Point currentLocalPosition,
        Point currentScreenPosition)
    {
        if (!HasExceededPetDragThreshold(
                _pointerDownPosition,
                currentLocalPosition,
                _pointerDownScreenPosition,
                currentScreenPosition,
                _pointerDownPixelsPerDip,
                allowLocalFallback:
                    !_suppressClickReactionAfterRoamInterruption,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            return false;
        }

        BeginDirectPetDrag(
            currentLocalPosition,
            currentScreenPosition);
        return true;
    }

    private static bool HasExceededPetDragThreshold(
        Point pointerDownLocalDips,
        Point currentLocalDips,
        Point pointerDownScreenPixels,
        Point currentScreenPixels,
        Vector pixelsPerDipAtPointerDown,
        bool allowLocalFallback,
        double minimumHorizontalDragDips,
        double minimumVerticalDragDips)
    {
        var hasPhysicalCoordinates =
            double.IsFinite(pointerDownScreenPixels.X) &&
            double.IsFinite(pointerDownScreenPixels.Y) &&
            double.IsFinite(currentScreenPixels.X) &&
            double.IsFinite(currentScreenPixels.Y) &&
            double.IsFinite(pixelsPerDipAtPointerDown.X) &&
            double.IsFinite(pixelsPerDipAtPointerDown.Y) &&
            pixelsPerDipAtPointerDown.X > 0 &&
            pixelsPerDipAtPointerDown.Y > 0;
        if (hasPhysicalCoordinates)
        {
            return Math.Abs(
                       currentScreenPixels.X -
                       pointerDownScreenPixels.X) >=
                   minimumHorizontalDragDips *
                   pixelsPerDipAtPointerDown.X ||
                   Math.Abs(
                       currentScreenPixels.Y -
                       pointerDownScreenPixels.Y) >=
                   minimumVerticalDragDips *
                   pixelsPerDipAtPointerDown.Y;
        }

        if (!allowLocalFallback)
        {
            // During roam disembark the HWND moves under a stationary pointer.
            // A temporarily unavailable screen point must not reinterpret that
            // local-coordinate drift as an intentional drag.
            return false;
        }

        return Math.Abs(currentLocalDips.X - pointerDownLocalDips.X) >=
                   minimumHorizontalDragDips ||
               Math.Abs(currentLocalDips.Y - pointerDownLocalDips.Y) >=
                   minimumVerticalDragDips;
    }

    private Vector GetPointerPixelsPerDip()
    {
        try
        {
            var transform =
                PresentationSource.FromVisual(this)?.CompositionTarget?
                    .TransformToDevice ?? Matrix.Identity;
            return new Vector(
                double.IsFinite(transform.M11) && transform.M11 > 0
                    ? transform.M11
                    : 1d,
                double.IsFinite(transform.M22) && transform.M22 > 0
                    ? transform.M22
                    : 1d);
        }
        catch (InvalidOperationException)
        {
            return new Vector(1d, 1d);
        }
    }

    private void BeginDirectPetDrag(
        Point currentPosition,
        Point currentScreenPosition)
    {
        _workPointerClickCount = 0;
        // A real drag owns window position only. Keep every work animation
        // state and its absolute clock intact so moving the working pet cannot
        // restart a pose, become a click, or briefly fall back to idle.
        _dragPreservesWorkMode = _workState != WorkState.Idle;
        if (_dragPreservesWorkMode)
        {
            // A work snap owns only the last release position. Clear that
            // marker as soon as a new drag begins; if its edge page was cold,
            // resume from the frozen pose instead of catching the clock up.
            CancelPendingWorkEdgeHandoff(resumeWork: true);
        }
        CancelTodoOpenAfterEdgeRoamStop();
        if (_isEdgeRoaming)
        {
            // A click can finish the authored disembark in place, but once the
            // gesture becomes a real drag the composition clock must stop
            // owning Left/Top before captured input starts moving the HWND.
            StopEdgeRoaming(
                scheduleNext: true,
                restoreIdleFrame: true,
                interrupted: true,
                immediate: true);
        }

        _dragStarted = true;
        _pointerDown = false;
        var dragOriginDock = _edgeDock;
        ExitEdgePeek(restartAutomaticCountdown: false);
        var startWindowBounds = GetPetViewboxBoundsInScreenDips();
        _edgeDockDragContext = new EdgeDockDragContext(
            dragOriginDock,
            MonitorWorkArea.GetForVisual(this, PetSizeViewbox),
            startWindowBounds,
            GetDragReleaseContactBounds(startWindowBounds, dragOriginDock));
        _directDragPhysicalGeometryReady =
            TryPrepareDirectPetDragGeometry(
                startWindowBounds,
                dragOriginDock);
        ContinueDirectPetDrag(
            currentPosition,
            currentScreenPosition);
    }

    private void PetHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var dragStartedOnRelease = false;
        var releaseLocalPosition = e.GetPosition(this);
        if (_dragInteractionActive && (_pointerDown || _dragStarted))
        {
            _latestDragScreenPosition = GetPointerScreenPointOrFallback(
                releaseLocalPosition,
                _latestDragScreenPosition,
                allowLocalFallback:
                    !_suppressClickReactionAfterRoamInterruption);
            if (!_dragStarted && _pointerDown)
            {
                // A fast press-move-release can be coalesced without a WPF
                // MouseMove reaching this window. Give the authoritative
                // physical release point the same final threshold check so a
                // real drag cannot fall through as a click.
                dragStartedOnRelease = TryBeginPetPointerDrag(
                    releaseLocalPosition,
                    _latestDragScreenPosition);
            }
        }

        if (_dragStarted)
        {
            if (!dragStartedOnRelease)
            {
                ContinueDirectPetDrag(
                    releaseLocalPosition,
                    _latestDragScreenPosition);
            }
            CompleteDirectPetDrag(updateEdgeDock: true);
            e.Handled = true;
            return;
        }

        var wasSimpleClick = _pointerDown && !_dragStarted;
        var workClickCount = _workPointerClickCount;
        var wasWorkInteraction = _workState != WorkState.Idle;
        var shouldActCute = wasSimpleClick &&
                            !wasWorkInteraction &&
                            _edgeDock == EdgeDock.None &&
                            !_suppressClickReactionAfterRoamInterruption;
        _pointerDown = false;
        _dragInteractionActive = false;
        _pointerDownPixelsPerDip = new Vector(1d, 1d);
        _edgeDockDragContext = null;
        _suppressClickReactionAfterRoamInterruption = false;
        _workPointerClickCount = 0;
        PetHost.ReleaseMouseCapture();

        if (wasSimpleClick && _bubbleMode == BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.None);
        }

        if (wasSimpleClick && wasWorkInteraction)
        {
            HandleWorkPetClick(workClickCount);
        }
        else if (shouldActCute)
        {
            ShowCuteReaction();
        }
        else
        {
            RestartAutomaticCountdown();
        }

        RefreshWorkModeButton();
        e.Handled = true;
    }

    private void PetHost_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragInteractionActive)
        {
            return;
        }

        if (_dragStarted)
        {
            _latestDragScreenPosition = GetPointerScreenPointOrFallback(
                e.GetPosition(this),
                _latestDragScreenPosition,
                allowLocalFallback:
                    !_suppressClickReactionAfterRoamInterruption);
            CompleteDirectPetDrag(updateEdgeDock: true);
            return;
        }

        _pointerDown = false;
        _dragStarted = false;
        _dragInteractionActive = false;
        _pointerDownPixelsPerDip = new Vector(1d, 1d);
        _dragPreservesWorkMode = false;
        _directDragPhysicalGeometryReady = false;
        _directDragTopClamped = false;
        _directDragTopClampPointerYInPhysicalPixels = double.NaN;
        _edgeDockDragContext = null;
        _suppressClickReactionAfterRoamInterruption = false;
        _workPointerClickCount = 0;
        RestartAutomaticCountdown();
        RefreshWorkModeButton();
        RefreshSnoreBubbleAnimationState();
    }

    private bool TryPrepareDirectPetDragGeometry(
        Rect startWindowBounds,
        EdgeDock dragOriginDock)
    {
        if (!OwnedWindowPositioner.TryGetPhysicalBounds(
                this,
                out var physicalWindowBounds) ||
            !double.IsFinite(_pointerDownScreenPosition.X) ||
            !double.IsFinite(_pointerDownScreenPosition.Y))
        {
            return false;
        }

        var contactBounds = GetDragReleaseContactBounds(
            startWindowBounds,
            dragOriginDock);
        var contactTopScreenPoint = GetScreenPointOrFallback(
            new Point(0, contactBounds.Top - Top),
            new Point(double.NaN, double.NaN));
        if (!double.IsFinite(contactTopScreenPoint.Y))
        {
            return false;
        }

        _dragPointerOffsetFromWindowInPhysicalPixels = new Vector(
            _pointerDownScreenPosition.X - physicalWindowBounds.Left,
            _pointerDownScreenPosition.Y - physicalWindowBounds.Top);
        _dragContactTopOffsetInPhysicalPixels =
            contactTopScreenPoint.Y - physicalWindowBounds.Top;
        return double.IsFinite(
                   _dragPointerOffsetFromWindowInPhysicalPixels.X) &&
               double.IsFinite(
                   _dragPointerOffsetFromWindowInPhysicalPixels.Y) &&
               double.IsFinite(_dragContactTopOffsetInPhysicalPixels);
    }

    private void ContinueDirectPetDrag(
        Point currentLocalPosition,
        Point currentScreenPosition)
    {
        if (_directDragPhysicalGeometryReady &&
            double.IsFinite(currentScreenPosition.X) &&
            double.IsFinite(currentScreenPosition.Y))
        {
            // Re-sample the current frame's contact edge on every input event.
            // Keep the physical pointer-to-HWND offset established by the last
            // successful SetWindowPos; rebuilding it from the original DIP
            // point would jump after WM_DPICHANGED.
            if (OwnedWindowPositioner.TryGetPhysicalBounds(
                    this,
                    out var currentPhysicalBounds))
            {
                var currentViewboxBounds =
                    GetPetViewboxBoundsInScreenDips();
                var currentContactBounds = GetDragReleaseContactBounds(
                    currentViewboxBounds,
                    _edgeDockDragContext?.OriginDock ?? EdgeDock.None);
                var contactTopScreenPoint = GetScreenPointOrFallback(
                    new Point(0, currentContactBounds.Top - Top),
                    new Point(double.NaN, double.NaN));
                if (double.IsFinite(contactTopScreenPoint.Y))
                {
                    _dragContactTopOffsetInPhysicalPixels =
                        contactTopScreenPoint.Y - currentPhysicalBounds.Top;
                }
            }

            var hasPhysicalWorkArea = MonitorWorkArea.TryGetPhysicalWorkAreaAt(
                currentScreenPosition,
                out var physicalWorkArea);
            var unclampedTop = Math.Round(
                currentScreenPosition.Y -
                _dragPointerOffsetFromWindowInPhysicalPixels.Y,
                MidpointRounding.AwayFromZero);
            var targetPosition = CalculateDirectPetDragPosition(
                currentScreenPosition,
                _dragPointerOffsetFromWindowInPhysicalPixels,
                _dragContactTopOffsetInPhysicalPixels,
                physicalWorkArea,
                keepTopContactPinned:
                    _directDragTopClamped &&
                    currentScreenPosition.Y <=
                    _directDragTopClampPointerYInPhysicalPixels + 0.5);
            if (_directDragTopClamped &&
                (!hasPhysicalWorkArea ||
                 currentScreenPosition.Y >
                 _directDragTopClampPointerYInPhysicalPixels + 0.5))
            {
                _directDragTopClamped = false;
                _directDragTopClampPointerYInPhysicalPixels = double.NaN;
                targetPosition = CalculateDirectPetDragPosition(
                    currentScreenPosition,
                    _dragPointerOffsetFromWindowInPhysicalPixels,
                    _dragContactTopOffsetInPhysicalPixels,
                    physicalWorkArea,
                    keepTopContactPinned: false);
            }
            if (double.IsFinite(targetPosition.X) &&
                double.IsFinite(targetPosition.Y) &&
                targetPosition.X >= int.MinValue &&
                targetPosition.X <= int.MaxValue &&
                targetPosition.Y >= int.MinValue &&
                targetPosition.Y <= int.MaxValue &&
                OwnedWindowPositioner.TrySetPhysicalPosition(
                    this,
                    checked((int)targetPosition.X),
                    checked((int)targetPosition.Y)))
            {
                if (!_directDragTopClamped &&
                    targetPosition.Y > unclampedTop)
                {
                    _directDragTopClamped = true;
                    _directDragTopClampPointerYInPhysicalPixels =
                        currentScreenPosition.Y;
                }

                if (OwnedWindowPositioner.TryGetPhysicalBounds(
                        this,
                        out var positionedPhysicalBounds))
                {
                    _dragPointerOffsetFromWindowInPhysicalPixels = new Vector(
                        currentScreenPosition.X - positionedPhysicalBounds.Left,
                        currentScreenPosition.Y - positionedPhysicalBounds.Top);
                }

                try
                {
                    // Keep the DIP fallback continuous if a later native move
                    // fails, and rebuild it naturally after a DPI transition.
                    _pointerDownPosition = PointFromScreen(
                        currentScreenPosition);
                }
                catch (InvalidOperationException)
                {
                    // The next captured input event will retry.
                }

                return;
            }

            _directDragPhysicalGeometryReady = false;
        }

        // This path is only for a not-yet-created HWND or a transient native
        // positioning failure. It still follows the captured grab point and
        // clamps the current frame's visible pixels, rather than handing the
        // fixed transparent envelope back to system window dragging.
        var desiredLeft = Left +
                          currentLocalPosition.X -
                          _pointerDownPosition.X;
        var desiredTop = Top +
                         currentLocalPosition.Y -
                         _pointerDownPosition.Y;
        var viewboxBounds = GetPetViewboxBoundsInScreenDips();
        var contactBounds = GetDragReleaseContactBounds(
            viewboxBounds,
            _edgeDockDragContext?.OriginDock ?? EdgeDock.None);
        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var translatedContactTop = contactBounds.Top + desiredTop - Top;
        var topWasClamped = translatedContactTop < workArea.Top;
        if (topWasClamped)
        {
            desiredTop += workArea.Top - translatedContactTop;
        }

        MoveMainWindowTo(desiredLeft, desiredTop);
        if (topWasClamped &&
            double.IsFinite(currentScreenPosition.X) &&
            double.IsFinite(currentScreenPosition.Y))
        {
            try
            {
                _pointerDownPosition = PointFromScreen(
                    currentScreenPosition);
            }
            catch (InvalidOperationException)
            {
                // The next captured input event will retry.
            }
        }
    }

    private void CompleteDirectPetDrag(bool updateEdgeDock)
    {
        var hadActiveDrag = _dragStarted;
        _pointerDown = false;
        _dragStarted = false;
        _dragInteractionActive = false;
        _directDragPhysicalGeometryReady = false;
        _directDragTopClamped = false;
        _directDragTopClampPointerYInPhysicalPixels = double.NaN;
        if (PetHost.IsMouseCaptured)
        {
            PetHost.ReleaseMouseCapture();
        }

        try
        {
            if (hadActiveDrag && updateEdgeDock)
            {
                UpdateEdgeDockAfterDrag();
            }
        }
        finally
        {
            _pointerDownPixelsPerDip = new Vector(1d, 1d);
            _edgeDockDragContext = null;
            _dragPreservesWorkMode = false;
            _suppressClickReactionAfterRoamInterruption = false;
            _workPointerClickCount = 0;
            RestartAutomaticCountdown();
            RefreshWorkModeButton();
            RefreshSnoreBubbleAnimationState();
        }
    }

    private static Point CalculateDirectPetDragPosition(
        Point pointerScreenPosition,
        Vector pointerOffsetFromWindow,
        double contactTopOffset,
        Rect physicalWorkArea,
        bool keepTopContactPinned)
    {
        if (!double.IsFinite(pointerScreenPosition.X) ||
            !double.IsFinite(pointerScreenPosition.Y) ||
            !double.IsFinite(pointerOffsetFromWindow.X) ||
            !double.IsFinite(pointerOffsetFromWindow.Y) ||
            !double.IsFinite(contactTopOffset))
        {
            return new Point(double.NaN, double.NaN);
        }

        var desiredLeft = pointerScreenPosition.X -
                          pointerOffsetFromWindow.X;
        var desiredTop = pointerScreenPosition.Y -
                         pointerOffsetFromWindow.Y;
        if (!physicalWorkArea.IsEmpty &&
            IsFiniteRect(physicalWorkArea) &&
            physicalWorkArea.Width > 0 &&
            physicalWorkArea.Height > 0)
        {
            var topContactWindowPosition =
                physicalWorkArea.Top - contactTopOffset;
            desiredTop = keepTopContactPinned
                ? topContactWindowPosition
                : Math.Max(desiredTop, topContactWindowPosition);
        }

        return new Point(
            Math.Round(desiredLeft, MidpointRounding.AwayFromZero),
            Math.Round(desiredTop, MidpointRounding.AwayFromZero));
    }

    private void PetHost_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        DeferIdleSpritePageTrim();
        if (_isReminderActive || _isTransientPetSizeOverride)
        {
            CancelTodoOpenAfterEdgeRoamStop();
            e.Handled = true;
            return;
        }

        if (_workState != WorkState.Idle)
        {
            CancelPendingWorkEdgeHandoff(resumeWork: true);
            _openTodoAfterWorkExitRequested = true;
            RequestWorkExit();
            e.Handled = true;
            return;
        }

        if (_isEdgeRoaming)
        {
            // Stop at the currently presented support point and play the real
            // boarding sequence backwards. Opening an owned WPF window is
            // deferred until that sequence completes so Show() never re-enters
            // the active CompositionTarget.Rendering callback.
            _openTodoAfterEdgeRoamStopRequested = true;
            StopEdgeRoaming(
                scheduleNext: true,
                restoreIdleFrame: true,
                interrupted: true);
            e.Handled = true;
            return;
        }

        CancelTodoOpenAfterEdgeRoamStop();
        if (_bubbleMode == BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.None);
        }
        else
        {
            OpenTodoFromPetRightClick();
        }

        e.Handled = true;
    }

    private void OpenTodoFromPetRightClick()
    {
        SetBubbleMode(BubbleMode.Todo);

        if (_bubbleMode == BubbleMode.Todo)
        {
            _todoWindow.ShowDefaultTab();
            _todoWindow.FocusInput();
        }
    }

    private void CancelTodoOpenAfterEdgeRoamStop()
    {
        _openTodoAfterEdgeRoamStopRequested = false;
    }

    private void QueueTodoOpenAfterEdgeRoamStop()
    {
        if (_todoOpenAfterEdgeRoamStopQueued ||
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                CancelTodoOpenAfterEdgeRoamStop();
            }

            return;
        }

        _todoOpenAfterEdgeRoamStopQueued = true;
        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                _processTodoOpenAfterEdgeRoamStopAction);
        }
        catch (InvalidOperationException)
        {
            _todoOpenAfterEdgeRoamStopQueued = false;
            CancelTodoOpenAfterEdgeRoamStop();
        }
    }

    private void ProcessTodoOpenAfterEdgeRoamStop()
    {
        _todoOpenAfterEdgeRoamStopQueued = false;
        if (!_openTodoAfterEdgeRoamStopRequested)
        {
            return;
        }

        CancelTodoOpenAfterEdgeRoamStop();
        if (_isClosing || _sessionInactive || _isReminderActive ||
            _isTransientPetSizeOverride || _isEdgeRoaming ||
            _dragInteractionActive || _pointerDown ||
            _bubbleMode != BubbleMode.None)
        {
            return;
        }

        DeferIdleSpritePageTrim();
        OpenTodoFromPetRightClick();
    }

    private void WorkModeButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_edgeDock != EdgeDock.None ||
            !WorkModeButton.IsHitTestVisible)
        {
            return;
        }

        DeferIdleSpritePageTrim();
        if (_workState == WorkState.Idle)
        {
            _ = TryEnterWorkMode();
        }
        else if (_workState == WorkState.Typing && !_workExitRequested)
        {
            RequestWorkExit();
        }
    }

    private bool CanEnterWorkMode()
    {
        return CanShowIdleWorkModeButton() &&
               !_dragInteractionActive && !_pointerDown;
    }

    private bool CanShowIdleWorkModeButton()
    {
        return IsLoaded &&
               !_isClosing && !_sessionInactive && _automaticAnimationEnabled &&
               _workState == WorkState.Idle &&
               _activeClip is null &&
               _currentSpriteFrame is SpriteFrame currentFrame &&
               currentFrame == _idleFrame &&
               !_isReminderActive && !_isTransientPetSizeOverride &&
               !_isPetSizeTransitioning && !_isPetSizePreviewSessionActive &&
               !_isPetSizeAdjustmentActive && !_petSizeTargetUpdatePending &&
               !_isFrameBlending && _pendingSpriteFrame is null &&
               _bubbleMode == BubbleMode.None && !_todoWindow.IsVisible &&
               !BubblePopup.IsOpen && _edgeDock == EdgeDock.None &&
               !_isEdgeRoaming;
    }

    private bool CanEnterWorkModeAfterEdgePeekExit()
    {
        return IsLoaded &&
               !_isClosing && !_sessionInactive && _automaticAnimationEnabled &&
               _workState == WorkState.Idle && _activeClip is null &&
               !_workEnterAfterEdgePeekExitRequested &&
               !_isReminderActive && !_isTransientPetSizeOverride &&
               !_dragInteractionActive && !_pointerDown && !_dragStarted &&
               !_isPetSizeTransitioning && !_isPetSizePreviewSessionActive &&
               !_isPetSizeAdjustmentActive && !_petSizeTargetUpdatePending &&
               _bubbleMode == BubbleMode.None && !_todoWindow.IsVisible &&
               !BubblePopup.IsOpen &&
               _edgeDock is EdgeDock.Left or EdgeDock.Right or EdgeDock.Bottom &&
               !_isEdgeRoaming;
    }

    private bool TryEnterWorkModeAfterEdgePeekExit()
    {
        if (!CanEnterWorkModeAfterEdgePeekExit())
        {
            RefreshWorkModeButton();
            return false;
        }

        _workEnterAfterEdgePeekExitRequested = true;
        _edgePeekHoldTimer.Stop();
        var frames = GetEdgeFrames(_edgeDock);
        if (_edgePeekFrameIndex == frames.Length - 1)
        {
            CompleteWorkModeEntryAfterEdgePeekExit();
            return true;
        }

        // Finish the authored retreat at 60fps instead of cutting directly
        // from an arbitrary peek pose to idle. The stable rest endpoint then
        // hands off pixel-safely to work-enter frame 001.
        _edgePeekFrameDeadlineTimestamp = Stopwatch.GetTimestamp();
        RefreshWorkModeButton();
        UpdateVisualClockSubscription();
        return true;
    }

    private void CompleteWorkModeEntryAfterEdgePeekExit()
    {
        if (!_workEnterAfterEdgePeekExitRequested)
        {
            return;
        }

        _workEnterAfterEdgePeekExitRequested = false;
        ExitEdgePeek(restartAutomaticCountdown: false);
        if (!TryEnterWorkMode())
        {
            RestartAutomaticCountdown();
            RefreshWorkModeButton();
        }
    }

    private bool TryEnterWorkMode()
    {
        if (!CanEnterWorkMode())
        {
            RefreshWorkModeButton();
            return false;
        }

        var timestamp = Stopwatch.GetTimestamp();
        CancelTodoOpenAfterEdgeRoamStop();
        CancelTodoOpenAfterWorkExit();
        _edgeRoamPreloadRequested = false;
        CancelEdgeRoamSpritePagePrefetch(includeBoarding: true);
        ScheduleNextEdgeRoam(timestamp, EdgeRoamInterval);
        StopPillowBreathing();
        _automaticTimer.Stop();
        _workState = WorkState.Entering;
        _workExitRequested = false;
        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workFastUntilTimestamp = 0;
        _workLoopPlaybackRate = 1;
        _workEdgeDock = EdgeDock.None;
        _workEdgeHandoffFrozenTimestamp = 0;
        // The first work page may still be loading, so the current visible
        // descriptor can remain idle for a few render ticks. Hide the snore
        // bubble from the high-level state change itself instead of waiting
        // for the first work frame to publish.
        RefreshSnoreBubbleAnimationState();
        StartWorkClip(_workEnterClip);
        RefreshWorkModeButton();
        return true;
    }

    private void StartWorkClip(AnimationClip clip)
    {
        StartWorkClipAt(clip, startFrameIndex: 0);
    }

    private void StartWorkClipAt(AnimationClip clip, int startFrameIndex)
    {
        if (startFrameIndex < 0 || startFrameIndex >= clip.Frames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrameIndex));
        }

        _activeClip = clip;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        RequestSpritePagePrefetch(
            clip.Frames[startFrameIndex].Image.PageName,
            urgent: true);
        _nextFrameBlendDuration = TimeSpan.Zero;
        _nextFrameMinimumHold = TimeSpan.Zero;
        ShowActiveClipFrame(startFrameIndex);
    }

    private void StartWorkLoopAt(long timestamp)
    {
        _workState = WorkState.Typing;
        _workLoopAnchorFramePosition = 0;
        _workLoopAnchorTimestamp = 0;
        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workFastUntilTimestamp = 0;
        _workLoopPlaybackRate = 1;

        StartWorkClip(_workLoopClip);
        RefreshWorkModeButton();
    }

    private void StartWorkSeriousExitClip()
    {
        if (_workState != WorkState.Typing ||
            (!ReferenceEquals(_activeClip, _workSeriousLoopClip) &&
             !ReferenceEquals(_activeClip, _workSeriousEnterClip)))
        {
            return;
        }

        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workFastUntilTimestamp = 0;
        _workLoopAnchorTimestamp = 0;
        _workLoopAnchorFramePosition = 0;
        _workLoopPlaybackRate = 1;
        StartWorkClip(_workSeriousExitClip);
        RefreshWorkModeButton();
    }

    private void StartWorkSeriousEnterClip(long timestamp)
    {
        if (_workState != WorkState.Typing ||
            !ReferenceEquals(_activeClip, _workLoopClip) ||
            !_workSeriousEnterRequested ||
            !double.IsFinite(_workSeriousEnterTargetFramePosition))
        {
            return;
        }

        // Preserve the exact absolute loop phase selected by the 2x clock.
        // The sampled entry clip begins pixel-identically at this normal neutral
        // seam and ends at the matching serious neutral seam.
        _workLoopAnchorFramePosition = _workSeriousEnterTargetFramePosition;
        _workLoopAnchorTimestamp = timestamp;
        _workSeriousEnterRequested = false;
        _workFastUntilTimestamp = 0;
        StartWorkClip(_workSeriousEnterClip);
    }

    private void StartSeriousWorkLoopAt(long timestamp)
    {
        var framePosition = double.IsFinite(
                _workSeriousEnterTargetFramePosition)
            ? _workSeriousEnterTargetFramePosition
            : 0;
        var frameIndex = (int)(
            Math.Floor(framePosition) % WorkSeriousLoopFrameCount);
        if (frameIndex < 0)
        {
            frameIndex += WorkSeriousLoopFrameCount;
        }

        _workState = WorkState.Typing;
        _workLoopAnchorFramePosition = framePosition;
        _workLoopAnchorTimestamp = 0;
        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workLoopPlaybackRate = WorkFastPlaybackMultiplier;
        _workFastUntilTimestamp = checked(
            timestamp + ToStopwatchTicks(WorkFastDuration));

        StartWorkClipAt(_workSeriousLoopClip, frameIndex);
        RefreshWorkModeButton();
    }

    private void RequestWorkExit()
    {
        if (_workState is WorkState.Idle or WorkState.Exiting)
        {
            // A right-click can arrive after the authored exit has already
            // started. Hide the work button in this input turn as soon as the
            // deferred Todo request is recorded, rather than exposing it until
            // the next composition callback.
            PrefetchPendingWorkTransitionPages();
            RefreshWorkModeButton();
            UpdateVisualClockSubscription();
            return;
        }

        _workExitRequested = true;
        if (_workSeriousEnterRequested)
        {
            // A right-click before the selected seam cancels the expression
            // change. Keep the current 2x phase untouched until the exit seam.
            _workSeriousEnterRequested = false;
            _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        }
        PrefetchPendingWorkTransitionPages();
        if (_workState == WorkState.Entering &&
            ReferenceEquals(_activeClip, _workEnterClip))
        {
            var enterFrameIndex = GetDisplayedWorkEnterFrameIndex();
            StartWorkExitClipAt(
                _workExitClip.Frames.Length - 1 - enterFrameIndex);
            return;
        }

        if (!IsWorkTypingLoopClip(_activeClip))
        {
            RefreshWorkModeButton();
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        var framePosition = GetWorkLoopFramePositionAt(timestamp);
        if (_workLoopAnchorTimestamp <= 0)
        {
            if (ReferenceEquals(_activeClip, _workSeriousLoopClip))
            {
                StartWorkSeriousExitClip();
            }
            else
            {
                StartWorkExitClip();
            }
            return;
        }

        _workExitTargetFramePosition =
            GetNextWorkNeutralMicroSeamFramePosition(framePosition);
        var remainingFrames = Math.Max(
            0,
            _workExitTargetFramePosition - framePosition);
        if (remainingFrames <= 0.000001)
        {
            if (ReferenceEquals(_activeClip, _workSeriousLoopClip))
            {
                StartWorkSeriousExitClip();
            }
            else
            {
                StartWorkExitClip();
            }
            return;
        }

        // Keep the absolute loop clock untouched while waiting. Re-anchoring or
        // temporarily accelerating here makes the hands visibly jerk just before
        // the exit, especially when the current pose is a key-down pose.
        RefreshWorkModeButton();
    }

    private int GetDisplayedWorkEnterFrameIndex()
    {
        if (_currentSpriteFrame is SpriteFrame currentFrame)
        {
            for (var index = 0; index < _workEnterFrames.Length; index++)
            {
                if (currentFrame == _workEnterFrames[index])
                {
                    return index;
                }
            }

            if (currentFrame == _idleFrame)
            {
                return 0;
            }
        }

        return Math.Clamp(_activeFrameIndex, 0, _workEnterFrames.Length - 1);
    }

    private void StartWorkExitClip()
    {
        StartWorkExitClipAt(startFrameIndex: 0);
    }

    private void StartWorkExitClipAt(int startFrameIndex)
    {
        if (_workState == WorkState.Idle)
        {
            return;
        }

        _workState = WorkState.Exiting;
        _workExitRequested = false;
        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workFastUntilTimestamp = 0;
        _workLoopAnchorTimestamp = 0;
        _workLoopPlaybackRate = 1;
        StartWorkClipAt(_workExitClip, startFrameIndex);
        RefreshWorkModeButton();
    }

    private void FinishWorkExit()
    {
        var openTodo = _openTodoAfterWorkExitRequested;
        _workEdgeDock = EdgeDock.None;
        _workEdgeHandoffFrozenTimestamp = 0;
        _workState = WorkState.Idle;
        _workExitRequested = false;
        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workLoopAnchorTimestamp = 0;
        _workLoopAnchorFramePosition = 0;
        _workLoopPlaybackRate = 1;
        _workFastUntilTimestamp = 0;
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        ScheduleNextEdgeRoam(Stopwatch.GetTimestamp(), EdgeRoamInterval);
        _nextFrameBlendDuration = TimeSpan.Zero;
        ShowStableFrame(_idleFrame);
        RequestIdleSpritePageTrim();
        RestartAutomaticCountdown();
        ScheduleUnpinnedPetSizePreviewCommit();
        RefreshWorkModeButton();
        UpdateVisualClockSubscription();
        if (openTodo)
        {
            QueueTodoOpenAfterWorkExit();
        }
    }

    private void StartWorkEdgeHandoff(EdgeDock dock)
    {
        if (_workState == WorkState.Idle ||
            dock is not (EdgeDock.Left or EdgeDock.Right or EdgeDock.Bottom) ||
            _isClosing || _isReminderActive)
        {
            return;
        }

        CancelTodoOpenAfterWorkExit();
        _workEdgeDock = dock;
        _workEdgeHandoffFrozenTimestamp = Stopwatch.GetTimestamp();
        RequestSpritePagePrefetch(
            GetEdgeFrames(dock)[^1].PageName,
            urgent: true);
        RefreshWorkModeButton();

        // A resident edge page can replace the currently displayed work pose
        // in this input turn. A cold page leaves every work descriptor and
        // absolute clock value untouched until a later Rendering callback.
        if (!TryCompletePendingWorkEdgeHandoff())
        {
            UpdateVisualClockSubscription();
        }
    }

    private bool TryCompletePendingWorkEdgeHandoff()
    {
        if (_workEdgeDock == EdgeDock.None || _workState == WorkState.Idle)
        {
            return false;
        }

        var dock = _workEdgeDock;
        var edgeRestFrame = GetEdgeFrames(dock)[^1];
        if (string.Equals(
                _failedSpritePageName,
                edgeRestFrame.PageName,
                StringComparison.Ordinal))
        {
            // A corrupt target cannot be displayed. This is the sole edge
            // handoff path allowed to publish idle; terminating work is safer
            // than leaving a frozen clip subscribed forever.
            StopWorkModeImmediately(restoreIdleFrame: true);
            ScheduleNextEdgeRoam(Stopwatch.GetTimestamp(), EdgeRoamInterval);
            RestartAutomaticCountdown();
            return true;
        }

        if (!IsSpritePageImmediatelyAvailable(edgeRestFrame.PageName))
        {
            RequestSpritePagePrefetch(edgeRestFrame.PageName, urgent: true);
            return false;
        }

        CompleteWorkEdgeHandoff(dock, edgeRestFrame);
        return true;
    }

    private void CompleteWorkEdgeHandoff(
        EdgeDock dock,
        SpriteFrame edgeRestFrame)
    {
        _workEdgeDock = EdgeDock.None;
        _workEdgeHandoffFrozenTimestamp = 0;
        _workState = WorkState.Idle;
        _workExitRequested = false;
        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workLoopAnchorTimestamp = 0;
        _workLoopAnchorFramePosition = 0;
        _workLoopPlaybackRate = 1;
        _workFastUntilTimestamp = 0;
        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        ScheduleNextEdgeRoam(Stopwatch.GetTimestamp(), EdgeRoamInterval);

        // State reset and the only new descriptor publication happen together.
        // No work-exit, pillow-visible, or idle descriptor is emitted first.
        var enteredEdge = EnterEdgePeekCore(dock, TimeSpan.Zero) &&
                          _currentSpriteFrame is SpriteFrame displayedFrame &&
                          displayedFrame == edgeRestFrame;
        if (!enteredEdge)
        {
            _nextFrameBlendDuration = TimeSpan.Zero;
            ShowStableFrame(_idleFrame);
            RequestIdleSpritePageTrim();
            RestartAutomaticCountdown();
        }

        ScheduleUnpinnedPetSizePreviewCommit();
        RefreshWorkModeButton();
        UpdateVisualClockSubscription();
    }

    private void CancelPendingWorkEdgeHandoff(bool resumeWork)
    {
        if (_workEdgeDock == EdgeDock.None)
        {
            _workEdgeHandoffFrozenTimestamp = 0;
            return;
        }

        var frozenTimestamp = _workEdgeHandoffFrozenTimestamp;
        _workEdgeDock = EdgeDock.None;
        _workEdgeHandoffFrozenTimestamp = 0;
        if (!resumeWork || frozenTimestamp <= 0 || _workState == WorkState.Idle)
        {
            return;
        }

        var pauseTicks = Math.Max(0, Stopwatch.GetTimestamp() - frozenTimestamp);
        _activeClipStartedTimestamp = ShiftTimestampAfterPause(
            _activeClipStartedTimestamp,
            pauseTicks);
        _activeFrameDeadlineTimestamp = ShiftTimestampAfterPause(
            _activeFrameDeadlineTimestamp,
            pauseTicks);
        _workLoopAnchorTimestamp = ShiftTimestampAfterPause(
            _workLoopAnchorTimestamp,
            pauseTicks);
        _workFastUntilTimestamp = ShiftTimestampAfterPause(
            _workFastUntilTimestamp,
            pauseTicks);
    }

    private static long ShiftTimestampAfterPause(long timestamp, long pauseTicks)
    {
        if (timestamp <= 0 || timestamp == long.MaxValue || pauseTicks <= 0)
        {
            return timestamp;
        }

        return timestamp > long.MaxValue - pauseTicks
            ? long.MaxValue
            : timestamp + pauseTicks;
    }

    private void StopWorkModeImmediately(bool restoreIdleFrame)
    {
        CancelTodoOpenAfterWorkExit();
        _workEdgeDock = EdgeDock.None;
        _workEdgeHandoffFrozenTimestamp = 0;
        if (_workState == WorkState.Idle)
        {
            return;
        }

        if (_activeClip is { } activeClip && IsWorkClip(activeClip))
        {
            _activeClip = null;
            _activeFrameIndex = -1;
            _activeClipStartedTimestamp = 0;
            _activeFrameDeadlineTimestamp = 0;
            ClearDeferredActiveClipClock();
        }

        _workState = WorkState.Idle;
        _workExitRequested = false;
        _workSeriousEnterRequested = false;
        _workSeriousEnterTargetFramePosition = double.PositiveInfinity;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        _workExitTargetFramePosition = double.PositiveInfinity;
        _workLoopAnchorTimestamp = 0;
        _workLoopAnchorFramePosition = 0;
        _workLoopPlaybackRate = 1;
        _workFastUntilTimestamp = 0;
        if (restoreIdleFrame)
        {
            _nextFrameBlendDuration = TimeSpan.Zero;
            ShowStableFrame(_idleFrame);
        }

        RequestIdleSpritePageTrim();
        RefreshWorkModeButton();
        UpdateVisualClockSubscription();
    }

    private void HandleWorkPetClick(int clickCount)
    {
        if (_workState != WorkState.Typing || _workExitRequested ||
            _workEdgeDock != EdgeDock.None ||
            !IsWorkTypingLoopClip(_activeClip))
        {
            return;
        }

        if (clickCount < 2)
        {
            // A single click is deliberately a visual no-op in work mode.
            // It may be the first half of a double-click, so only warm the
            // tiny expression-entry page while the ordinary loop is active;
            // do not change the active clip, typing phase, speed, or
            // serious-mode deadline.
            if (ReferenceEquals(_activeClip, _workLoopClip))
            {
                PrefetchWorkSeriousEntryPage();
            }
            return;
        }

        StartFastWorkTypingAt(Stopwatch.GetTimestamp());
    }

    private void PrefetchWorkSeriousEntryPage()
    {
        // A ClickCount=1 event is also the first half of every double-click.
        // Warm the short facial-entry page only when no normal-loop page is
        // already decoding. This speculative request must never cancel or
        // replace visible-loop work; the double-click path issues its own
        // urgent request when the serious transition is actually selected.
        if (_spritePagePrefetchTask is not null ||
            _desiredSpritePageName is not null)
        {
            return;
        }

        RequestSpritePagePrefetch(
            _workSeriousEnterClip.Frames[0].Image.PageName,
            urgent: false);
    }

    private void StartFastWorkTypingAt(long timestamp)
    {
        if (_workState != WorkState.Typing || _workExitRequested ||
            !IsWorkTypingLoopClip(_activeClip))
        {
            return;
        }

        if (ReferenceEquals(_activeClip, _workSeriousLoopClip))
        {
            if (_workLoopAnchorTimestamp > 0)
            {
                _workLoopAnchorFramePosition =
                    GetWorkLoopFramePositionAt(timestamp);
            }
            _workLoopAnchorTimestamp = timestamp;
            _workLoopPlaybackRate = WorkFastPlaybackMultiplier;
            _workFastUntilTimestamp = checked(
                timestamp + ToStopwatchTicks(WorkFastDuration));
            _workSeriousExitRequested = false;
            _workSeriousExitTargetFramePosition = double.PositiveInfinity;
            return;
        }

        if (_workSeriousEnterRequested)
        {
            // Repeated double-click notifications must not move the selected
            // seam and make the visible expression transition feel delayed.
            return;
        }

        var framePosition = GetWorkLoopFramePositionAt(timestamp);
        _workLoopAnchorFramePosition = framePosition;
        _workLoopAnchorTimestamp = timestamp;
        _workLoopPlaybackRate = WorkFastPlaybackMultiplier;
        _workFastUntilTimestamp = 0;
        _workSeriousEnterTargetFramePosition =
            GetNextWorkNeutralMicroSeamFramePosition(framePosition);
        _workSeriousEnterRequested = true;
        _workSeriousExitRequested = false;
        _workSeriousExitTargetFramePosition = double.PositiveInfinity;
        RequestSpritePagePrefetch(
            _workSeriousEnterClip.Frames[0].Image.PageName,
            urgent: true);
        PrefetchPendingWorkTransitionPages();
        UpdateVisualClockSubscription();
    }

    private void AdvanceWorkLoop(long timestamp)
    {
        if (_workState != WorkState.Typing ||
            !IsWorkTypingLoopClip(_activeClip) ||
            _workLoopAnchorTimestamp <= 0)
        {
            return;
        }

        PrefetchPendingWorkTransitionPages();
        if (_workFastUntilTimestamp > 0 &&
            timestamp >= _workFastUntilTimestamp &&
            !_workExitRequested &&
            ReferenceEquals(_activeClip, _workSeriousLoopClip))
        {
            var currentFramePosition = GetWorkLoopFramePositionAt(timestamp);
            if (!_workSeriousExitRequested)
            {
                _workSeriousExitRequested = true;
                _workSeriousExitTargetFramePosition =
                    GetNextWorkNeutralMicroSeamFramePosition(
                        currentFramePosition);
            }

            if (currentFramePosition >= _workSeriousExitTargetFramePosition)
            {
                StartWorkSeriousExitClip();
                return;
            }
        }

        var framePosition = GetWorkLoopFramePositionAt(timestamp);
        if (_workExitRequested &&
            framePosition >= _workExitTargetFramePosition)
        {
            if (ReferenceEquals(_activeClip, _workSeriousLoopClip))
            {
                // Preserve the authored brow/face relaxation before reversing
                // the seated work transition. Cutting straight to work-exit
                // would otherwise remove the serious expression in one frame.
                StartWorkSeriousExitClip();
            }
            else
            {
                StartWorkExitClip();
            }
            return;
        }

        if (_workSeriousEnterRequested &&
            framePosition >= _workSeriousEnterTargetFramePosition)
        {
            StartWorkSeriousEnterClip(timestamp);
            return;
        }

        var frameIndex = (int)(Math.Floor(framePosition) % WorkLoopFrameCount);
        if (frameIndex < 0)
        {
            frameIndex += WorkLoopFrameCount;
        }
        if (frameIndex == _activeFrameIndex)
        {
            return;
        }

        _activeFrameIndex = frameIndex;
        _nextFrameBlendDuration = TimeSpan.Zero;
        var activeLoopFrames = GetActiveWorkLoopFrames();
        ShowStableFrame(activeLoopFrames[frameIndex]);
        PrefetchNextWorkLoopPage(activeLoopFrames, frameIndex);
    }

    private double GetWorkLoopFramePositionAt(long timestamp)
    {
        if (_workLoopAnchorTimestamp <= 0 ||
            timestamp <= _workLoopAnchorTimestamp)
        {
            return _workLoopAnchorFramePosition;
        }

        var elapsedSeconds =
            (timestamp - _workLoopAnchorTimestamp) /
            (double)Stopwatch.Frequency;
        return _workLoopAnchorFramePosition +
               elapsedSeconds * WorkNormalPoseFramesPerSecond *
               _workLoopPlaybackRate;
    }

    private static double GetNextWorkNeutralMicroSeamFramePosition(
        double framePosition)
    {
        if (!double.IsFinite(framePosition))
        {
            throw new ArgumentOutOfRangeException(nameof(framePosition));
        }

        var cycleStart =
            Math.Floor(framePosition / WorkLoopFrameCount) * WorkLoopFrameCount;
        foreach (var seamFrameIndex in WorkNeutralMicroSeamFrameIndices)
        {
            var candidate = cycleStart + seamFrameIndex;
            if (candidate >= framePosition)
            {
                return candidate;
            }
        }

        return cycleStart + WorkLoopFrameCount +
               WorkNeutralMicroSeamFrameIndices[0];
    }

    private void PrefetchNextWorkLoopPage(
        IReadOnlyList<SpriteFrame> loopFrames,
        int displayedFrameIndex)
    {
        var currentPageName = loopFrames[displayedFrameIndex].PageName;
        for (var offset = 1; offset < loopFrames.Count; offset++)
        {
            var frameIndex =
                (displayedFrameIndex + offset) % loopFrames.Count;
            var nextPageName = loopFrames[frameIndex].PageName;
            if (string.Equals(
                    nextPageName,
                    currentPageName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsSpritePageImmediatelyAvailable(nextPageName))
            {
                RequestSpritePagePrefetch(nextPageName, urgent: true);
            }
            return;
        }
    }

    private void PrefetchPendingWorkTransitionPages()
    {
        if (_workEdgeDock != EdgeDock.None)
        {
            var edgeRestPageName = GetEdgeFrames(_workEdgeDock)[^1].PageName;
            if (!IsSpritePageImmediatelyAvailable(edgeRestPageName) &&
                !string.Equals(
                    _failedSpritePageName,
                    edgeRestPageName,
                    StringComparison.Ordinal))
            {
                // A direct work-edge handoff freezes whichever authored phase
                // is currently visible. Keep its one target rest page urgent
                // until the atomic replacement can complete.
                RequestSpritePagePrefetch(edgeRestPageName, urgent: true);
            }

            // The frozen handoff owns the one decode slot. In particular, a
            // queued serious-expression seam is also urgent and would replace
            // the edge request later in this method while Rendering is active.
            // Do not let any superseded work transition starve the rest page.
            return;
        }

        if (_workSeriousEnterRequested &&
            _workState == WorkState.Typing &&
            !IsSpritePageImmediatelyAvailable(
                _workSeriousEnterClip.Frames[0].Image.PageName))
        {
            RequestSpritePagePrefetch(
                _workSeriousEnterClip.Frames[0].Image.PageName,
                urgent: true);
        }

        if ((ReferenceEquals(_activeClip, _workSeriousEnterClip) ||
             _workSeriousEnterRequested) &&
            !_workExitRequested)
        {
            var framePosition = double.IsFinite(
                    _workSeriousEnterTargetFramePosition)
                ? _workSeriousEnterTargetFramePosition
                : 0;
            var frameIndex = (int)(
                Math.Floor(framePosition) % WorkSeriousLoopFrameCount);
            if (frameIndex < 0)
            {
                frameIndex += WorkSeriousLoopFrameCount;
            }

            var pageName = _workSeriousLoopFrames[frameIndex].PageName;
            if (!IsSpritePageImmediatelyAvailable(pageName))
            {
                RequestSpritePagePrefetch(pageName, urgent: false);
            }
        }

        if (ReferenceEquals(_activeClip, _workSeriousLoopClip) &&
            (_workFastUntilTimestamp > 0 || _workSeriousExitRequested) &&
            !IsSpritePageImmediatelyAvailable(
                _workSeriousExitClip.Frames[0].Image.PageName))
        {
            RequestSpritePagePrefetch(
                _workSeriousExitClip.Frames[0].Image.PageName,
                urgent: false);
        }

        if (ReferenceEquals(_activeClip, _workSeriousExitClip) &&
            !_workExitRequested &&
            !IsSpritePageImmediatelyAvailable(
                _workLoopClip.Frames[0].Image.PageName))
        {
            RequestSpritePagePrefetch(
                _workLoopClip.Frames[0].Image.PageName,
                urgent: false);
        }

        if (_workExitRequested &&
            _workState is not WorkState.Idle and not WorkState.Exiting &&
            !ReferenceEquals(_activeClip, _workSeriousEnterClip) &&
            !ReferenceEquals(_activeClip, _workSeriousLoopClip) &&
            !IsSpritePageImmediatelyAvailable(
                _workExitClip.Frames[0].Image.PageName))
        {
            RequestSpritePagePrefetch(
                _workExitClip.Frames[0].Image.PageName,
                urgent: false);
        }
    }

    private bool IsWorkTypingLoopClip(AnimationClip? clip) =>
        ReferenceEquals(clip, _workLoopClip) ||
        ReferenceEquals(clip, _workSeriousLoopClip);

    private SpriteFrame[] GetActiveWorkLoopFrames() =>
        ReferenceEquals(_activeClip, _workSeriousLoopClip)
            ? _workSeriousLoopFrames
            : _workLoopFrames;

    private bool IsWorkClip(AnimationClip clip) =>
        ReferenceEquals(clip, _workEnterClip) ||
        ReferenceEquals(clip, _workLoopClip) ||
        ReferenceEquals(clip, _workSeriousLoopClip) ||
        ReferenceEquals(clip, _workSeriousEnterClip) ||
        ReferenceEquals(clip, _workSeriousExitClip) ||
        ReferenceEquals(clip, _workExitClip);

    private void CancelTodoOpenAfterWorkExit()
    {
        _openTodoAfterWorkExitRequested = false;
    }

    private void QueueTodoOpenAfterWorkExit()
    {
        if (_todoOpenAfterWorkExitQueued ||
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                CancelTodoOpenAfterWorkExit();
            }
            return;
        }

        _todoOpenAfterWorkExitQueued = true;
        try
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                _processTodoOpenAfterWorkExitAction);
        }
        catch (InvalidOperationException)
        {
            _todoOpenAfterWorkExitQueued = false;
            CancelTodoOpenAfterWorkExit();
            RefreshWorkModeButton();
        }
    }

    private void ProcessTodoOpenAfterWorkExit()
    {
        _todoOpenAfterWorkExitQueued = false;
        if (!_openTodoAfterWorkExitRequested)
        {
            RefreshWorkModeButton();
            return;
        }

        CancelTodoOpenAfterWorkExit();
        if (_isClosing || _sessionInactive || _isReminderActive ||
            _isTransientPetSizeOverride || _workState != WorkState.Idle ||
            _isEdgeRoaming || _dragInteractionActive || _pointerDown ||
            _bubbleMode != BubbleMode.None)
        {
            RefreshWorkModeButton();
            return;
        }

        DeferIdleSpritePageTrim();
        OpenTodoFromPetRightClick();
        RefreshWorkModeButton();
    }

    private void RefreshWorkModeButton()
    {
        if (!IsLoaded)
        {
            return;
        }

        var workActive = _workState != WorkState.Idle;
        var mirrored = PetFacingScale.ScaleX < 0;
        var facingCompensation = mirrored ? -1d : 1d;
        if (WorkModeFacingCompensation.ScaleX != facingCompensation)
        {
            WorkModeFacingCompensation.ScaleX = facingCompensation;
        }
        var iconAlignment = mirrored
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        if (WorkModeButton.HorizontalAlignment != iconAlignment)
        {
            WorkModeButton.HorizontalAlignment = iconAlignment;
        }
        var canShowIdle = !workActive && CanShowIdleWorkModeButton();
        var canEnter = !workActive && CanEnterWorkMode();
        var todoOpenPending = _openTodoAfterWorkExitRequested ||
                              _todoOpenAfterWorkExitQueued;
        var workEdgeHandoffPending =
            _workEdgeDock != EdgeDock.None && workActive;
        // Preserve the existing work-dock exit path: ordinary edge-peek hides
        // the idle sun, while a working pet keeps its moon available so the
        // user can still return to rest without dragging away first.
        var docked = _edgeDock != EdgeDock.None;
        var shouldShow = !todoOpenPending && !workEdgeHandoffPending &&
                         !docked &&
                         (workActive || canShowIdle);
        var shouldEnable = shouldShow &&
                           (canEnter ||
                            (_workState == WorkState.Typing &&
                             !_workExitRequested));
        var buttonVisibility = shouldShow
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (WorkModeButton.Visibility != buttonVisibility)
        {
            WorkModeButton.Visibility = buttonVisibility;
        }
        var buttonOpacity = shouldShow ? 1d : 0d;
        if (WorkModeButton.Opacity != buttonOpacity)
        {
            WorkModeButton.Opacity = buttonOpacity;
        }
        if (WorkModeButton.IsHitTestVisible != (shouldShow && shouldEnable))
        {
            WorkModeButton.IsHitTestVisible = shouldShow && shouldEnable;
        }
        if (WorkModeButton.IsEnabled != shouldEnable)
        {
            WorkModeButton.IsEnabled = shouldEnable;
        }

        var visualStateTag = workActive ? "Working" : "Idle";
        if (!string.Equals(
                WorkModeButton.Tag as string,
                visualStateTag,
                StringComparison.Ordinal))
        {
            WorkModeButton.Tag = visualStateTag;
        }
        var workIconTargetWorking = workActive &&
                                    !_workExitRequested &&
                                    _workState != WorkState.Exiting;
        UpdateWorkModeIconVisualTarget(workIconTargetWorking, shouldShow);

        var automationName = workActive ? "去睡觉" : "去打工";
        var helpText = workActive
            ? "月亮图标，点击让小鲁班回去休息"
            : "太阳图标，点击让小鲁班去打工";
        if (!string.Equals(
                AutomationProperties.GetName(WorkModeButton),
                automationName,
                StringComparison.Ordinal))
        {
            AutomationProperties.SetName(WorkModeButton, automationName);
        }
        if (!string.Equals(
                AutomationProperties.GetHelpText(WorkModeButton),
                helpText,
                StringComparison.Ordinal))
        {
            AutomationProperties.SetHelpText(WorkModeButton, helpText);
        }

        WorkModeButton.ToolTip = workActive
            ? "月亮：去睡觉"
            : "太阳：去打工";
    }

    private void UpdateWorkModeIconVisualTarget(
        bool workActive,
        bool shouldShow)
    {
        if (!EnsureWorkModeIconVisuals())
        {
            _workModeIconTransitionActive = false;
            return;
        }

        if (!_workModeIconVisualStateInitialized)
        {
            _workModeIconVisualStateInitialized = true;
            _workModeIconTargetWorking = workActive;
            _workModeIconTransitionToWorking = workActive;
            _workModeIconTransitionActive = false;
            _workModeIconTransitionStartedTimestamp = 0;
            ApplyWorkModeIconVisualState(
                GetWorkModeIconStableState(workActive));
            return;
        }

        if (!shouldShow)
        {
            if (_workModeIconTransitionActive ||
                _workModeIconTargetWorking != workActive)
            {
                _workModeIconTargetWorking = workActive;
                _workModeIconTransitionToWorking = workActive;
                _workModeIconTransitionActive = false;
                _workModeIconTransitionStartedTimestamp = 0;
                ApplyWorkModeIconVisualState(
                    GetWorkModeIconStableState(workActive));
            }
            return;
        }

        if (_workModeIconTargetWorking == workActive)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        if (_workModeIconTransitionActive)
        {
            // A state interruption can reverse the target between two monitor
            // refreshes. Resolve the exact current mix first, then continue from
            // those same transforms and opacities without jumping to an endpoint.
            AdvanceWorkModeIconTransition(timestamp);
        }

        _workModeIconTargetWorking = workActive;
        _workModeIconTransitionToWorking = workActive;
        _workModeIconTransitionStartState = _workModeIconCurrentVisualState;
        _workModeIconTransitionStartedTimestamp = timestamp;
        _workModeIconTransitionActive = true;
        // Publish the exact outgoing endpoint now. The first composition pass
        // advances from this absolute timestamp, so click feedback arrives in
        // no more than one monitor refresh without a Storyboard or extra timer.
        ApplyWorkModeIconVisualState(
            ResolveWorkModeIconTransitionState(
                _workModeIconTransitionStartState,
                elapsedSeconds: 0,
                toWorking: workActive));
    }

    private bool EnsureWorkModeIconVisuals()
    {
        if (_workModeIconVisualsInitialized)
        {
            return true;
        }

        _ = WorkModeButton.ApplyTemplate();
        var template = WorkModeButton.Template;
        if (template.FindName("SunIcon", WorkModeButton) is not FrameworkElement sunIcon ||
            template.FindName("MoonIcon", WorkModeButton) is not FrameworkElement moonIcon ||
            template.FindName("WorkSunModeHalo", WorkModeButton) is not FrameworkElement sunHalo ||
            template.FindName("WorkMoonModeHalo", WorkModeButton) is not FrameworkElement moonHalo ||
            template.FindName("WorkIconTwinkle", WorkModeButton) is not FrameworkElement twinkle ||
            template.FindName("WorkSunIconScale", WorkModeButton) is not ScaleTransform sunScale ||
            template.FindName("WorkSunIconRotate", WorkModeButton) is not RotateTransform sunRotate ||
            template.FindName("WorkSunIconTranslate", WorkModeButton) is not TranslateTransform sunTranslate ||
            template.FindName("WorkMoonIconScale", WorkModeButton) is not ScaleTransform moonScale ||
            template.FindName("WorkMoonIconRotate", WorkModeButton) is not RotateTransform moonRotate ||
            template.FindName("WorkMoonIconTranslate", WorkModeButton) is not TranslateTransform moonTranslate ||
            template.FindName("WorkSunModeHaloScale", WorkModeButton) is not ScaleTransform sunHaloScale ||
            template.FindName("WorkMoonModeHaloScale", WorkModeButton) is not ScaleTransform moonHaloScale ||
            template.FindName("WorkIconTwinkleScale", WorkModeButton) is not ScaleTransform twinkleScale ||
            template.FindName("WorkIconTwinkleRotate", WorkModeButton) is not RotateTransform twinkleRotate)
        {
            return false;
        }

        _workSunIconVisual = sunIcon;
        _workMoonIconVisual = moonIcon;
        _workSunModeHaloVisual = sunHalo;
        _workMoonModeHaloVisual = moonHalo;
        _workIconTwinkleVisual = twinkle;
        _workSunIconScale = sunScale;
        _workSunIconRotate = sunRotate;
        _workSunIconTranslate = sunTranslate;
        _workMoonIconScale = moonScale;
        _workMoonIconRotate = moonRotate;
        _workMoonIconTranslate = moonTranslate;
        _workSunModeHaloScale = sunHaloScale;
        _workMoonModeHaloScale = moonHaloScale;
        _workIconTwinkleScale = twinkleScale;
        _workIconTwinkleRotate = twinkleRotate;
        _workModeIconVisualsInitialized = true;
        return true;
    }

    private void AdvanceWorkModeIconTransition(long timestamp)
    {
        if (!_workModeIconTransitionActive ||
            _workModeIconTransitionStartedTimestamp <= 0)
        {
            return;
        }

        var elapsedSeconds = Math.Max(
            0,
            (timestamp - _workModeIconTransitionStartedTimestamp) /
            (double)Stopwatch.Frequency);
        ApplyWorkModeIconVisualState(
            ResolveWorkModeIconTransitionState(
                _workModeIconTransitionStartState,
                elapsedSeconds,
                _workModeIconTransitionToWorking));
        if (elapsedSeconds >= WorkModeIconTransitionDurationSeconds)
        {
            _workModeIconTransitionActive = false;
            _workModeIconTransitionStartedTimestamp = 0;
        }
    }

    private static WorkModeIconVisualState ResolveWorkModeIconTransitionState(
        WorkModeIconVisualState startState,
        double elapsedSeconds,
        bool toWorking)
    {
        var finiteElapsedSeconds = double.IsFinite(elapsedSeconds)
            ? Math.Max(0, elapsedSeconds)
            : WorkModeIconTransitionDurationSeconds;
        var progress = Math.Clamp(
            finiteElapsedSeconds / WorkModeIconTransitionDurationSeconds,
            0,
            1);
        var eased = SmoothStep(progress);
        var arc = Math.Sin(progress * Math.PI);
        var outgoingDirection = toWorking ? -1d : 1d;
        var incomingDirection = -outgoingDirection;
        var startOutgoingOpacity = toWorking
            ? startState.SunOpacity
            : startState.MoonOpacity;
        var startIncomingOpacity = toWorking
            ? startState.MoonOpacity
            : startState.SunOpacity;
        var outgoingOpacity = startOutgoingOpacity *
                              (1 - SmoothStep(Math.Clamp(
                                  (progress - 0.18) / 0.82,
                                  0,
                                  1)));
        var incomingOpacity = InterpolateWorkModeIconValue(
            startIncomingOpacity,
            1,
            SmoothStep(Math.Clamp(progress / 0.82, 0, 1)));
        var combinedOpacity = outgoingOpacity + incomingOpacity;
        if (combinedOpacity < 1)
        {
            incomingOpacity = Math.Min(1, incomingOpacity + 1 - combinedOpacity);
        }
        else if (combinedOpacity > 1.05)
        {
            var opacityNormalization = 1.05 / combinedOpacity;
            outgoingOpacity *= opacityNormalization;
            incomingOpacity *= opacityNormalization;
        }

        var startOutgoingScale = toWorking
            ? startState.SunScale
            : startState.MoonScale;
        var startIncomingScale = toWorking
            ? startState.MoonScale
            : startState.SunScale;
        var earlySettle = SmoothStep(Math.Clamp(progress / 0.06, 0, 1));
        var outgoingScale = InterpolateWorkModeIconValue(
                                startOutgoingScale,
                                0.72,
                                eased) -
                            0.06 * earlySettle * (1 - eased);
        var incomingScale = Math.Min(
            1.08,
            InterpolateWorkModeIconValue(
                startIncomingScale,
                1,
                eased) + 0.174 * arc);

        var startOutgoingRotation = toWorking
            ? startState.SunRotationDegrees
            : startState.MoonRotationDegrees;
        var startIncomingRotation = toWorking
            ? startState.MoonRotationDegrees
            : startState.SunRotationDegrees;
        var outgoingRotation = InterpolateWorkModeIconValue(
                                   startOutgoingRotation,
                                   outgoingDirection * 18,
                                   eased) +
                               outgoingDirection * 2.5 * arc;
        var incomingRotation = InterpolateWorkModeIconValue(
                                   startIncomingRotation,
                                   0,
                                   eased) -
                               incomingDirection * 3.5 * arc;

        var startOutgoingTranslateX = toWorking
            ? startState.SunTranslateX
            : startState.MoonTranslateX;
        var startIncomingTranslateX = toWorking
            ? startState.MoonTranslateX
            : startState.SunTranslateX;
        var startOutgoingTranslateY = toWorking
            ? startState.SunTranslateY
            : startState.MoonTranslateY;
        var startIncomingTranslateY = toWorking
            ? startState.MoonTranslateY
            : startState.SunTranslateY;
        var outgoingTranslateX = InterpolateWorkModeIconValue(
            startOutgoingTranslateX,
            outgoingDirection * 1.8,
            eased);
        var incomingTranslateX = InterpolateWorkModeIconValue(
            startIncomingTranslateX,
            0,
            eased);
        var outgoingTranslateY = InterpolateWorkModeIconValue(
                                     startOutgoingTranslateY,
                                     3.4,
                                     eased) +
                                 0.6 * arc;
        var incomingTranslateY = InterpolateWorkModeIconValue(
                                     startIncomingTranslateY,
                                     0,
                                     eased) -
                                 1.6 * arc;

        var startOutgoingHaloOpacity = toWorking
            ? startState.SunHaloOpacity
            : startState.MoonHaloOpacity;
        var startIncomingHaloOpacity = toWorking
            ? startState.MoonHaloOpacity
            : startState.SunHaloOpacity;
        var startOutgoingHaloScale = toWorking
            ? startState.SunHaloScale
            : startState.MoonHaloScale;
        var startIncomingHaloScale = toWorking
            ? startState.MoonHaloScale
            : startState.SunHaloScale;
        var outgoingHaloOpacity = InterpolateWorkModeIconValue(
                                      startOutgoingHaloOpacity,
                                      0,
                                      eased) +
                                  0.10 * arc;
        var incomingHaloOpacity = InterpolateWorkModeIconValue(
                                      startIncomingHaloOpacity,
                                      0.30,
                                      eased) +
                                  0.14 * arc;
        var outgoingHaloScale = InterpolateWorkModeIconValue(
                                    startOutgoingHaloScale,
                                    0.86,
                                    eased) +
                                0.08 * arc;
        var incomingHaloScale = InterpolateWorkModeIconValue(
                                    startIncomingHaloScale,
                                    1,
                                    eased) +
                                0.12 * arc;

        var twinkleProgress = Math.Clamp(
            (progress - 0.38) / 0.52,
            0,
            1);
        var twinkleEnvelope = progress is > 0.38 and < 0.90
            ? Math.Sin(twinkleProgress * Math.PI)
            : 0;
        var twinkleFlicker = 0.62 +
                             0.38 * Math.Abs(Math.Sin(
                                 twinkleProgress * Math.PI * 3));
        var twinklePulse = twinkleEnvelope * twinkleFlicker;
        var twinkleOpacity = Math.Min(
            1,
            startState.TwinkleOpacity * (1 - eased) + twinklePulse);
        var twinkleScale = InterpolateWorkModeIconValue(
                               startState.TwinkleScale,
                               0.55,
                               eased) +
                           0.68 * twinklePulse;
        var twinkleRotation = InterpolateWorkModeIconValue(
                                  startState.TwinkleRotationDegrees,
                                  toWorking ? 85 : -85,
                                  eased) +
                              (toWorking ? 1 : -1) * 35 * twinklePulse;

        return toWorking
            ? new WorkModeIconVisualState(
                SunOpacity: outgoingOpacity,
                MoonOpacity: incomingOpacity,
                SunScale: outgoingScale,
                MoonScale: incomingScale,
                SunRotationDegrees: outgoingRotation,
                MoonRotationDegrees: incomingRotation,
                SunTranslateX: outgoingTranslateX,
                SunTranslateY: outgoingTranslateY,
                MoonTranslateX: incomingTranslateX,
                MoonTranslateY: incomingTranslateY,
                SunHaloOpacity: outgoingHaloOpacity,
                MoonHaloOpacity: incomingHaloOpacity,
                SunHaloScale: outgoingHaloScale,
                MoonHaloScale: incomingHaloScale,
                TwinkleOpacity: twinkleOpacity,
                TwinkleScale: twinkleScale,
                TwinkleRotationDegrees: twinkleRotation)
            : new WorkModeIconVisualState(
                SunOpacity: incomingOpacity,
                MoonOpacity: outgoingOpacity,
                SunScale: incomingScale,
                MoonScale: outgoingScale,
                SunRotationDegrees: incomingRotation,
                MoonRotationDegrees: outgoingRotation,
                SunTranslateX: incomingTranslateX,
                SunTranslateY: incomingTranslateY,
                MoonTranslateX: outgoingTranslateX,
                MoonTranslateY: outgoingTranslateY,
                SunHaloOpacity: incomingHaloOpacity,
                MoonHaloOpacity: outgoingHaloOpacity,
                SunHaloScale: incomingHaloScale,
                MoonHaloScale: outgoingHaloScale,
                TwinkleOpacity: twinkleOpacity,
                TwinkleScale: twinkleScale,
                TwinkleRotationDegrees: twinkleRotation);
    }

    private static WorkModeIconVisualState GetWorkModeIconStableState(
        bool working) =>
        working
            ? new WorkModeIconVisualState(
                SunOpacity: 0,
                MoonOpacity: 1,
                SunScale: 0.72,
                MoonScale: 1,
                SunRotationDegrees: -18,
                MoonRotationDegrees: 0,
                SunTranslateX: -1.8,
                SunTranslateY: 3.4,
                MoonTranslateX: 0,
                MoonTranslateY: 0,
                SunHaloOpacity: 0,
                MoonHaloOpacity: 0.30,
                SunHaloScale: 0.86,
                MoonHaloScale: 1,
                TwinkleOpacity: 0,
                TwinkleScale: 0.55,
                TwinkleRotationDegrees: 85)
            : new WorkModeIconVisualState(
                SunOpacity: 1,
                MoonOpacity: 0,
                SunScale: 1,
                MoonScale: 0.72,
                SunRotationDegrees: 0,
                MoonRotationDegrees: 18,
                SunTranslateX: 0,
                SunTranslateY: 0,
                MoonTranslateX: 1.8,
                MoonTranslateY: 3.4,
                SunHaloOpacity: 0.30,
                MoonHaloOpacity: 0,
                SunHaloScale: 1,
                MoonHaloScale: 0.86,
                TwinkleOpacity: 0,
                TwinkleScale: 0.55,
                TwinkleRotationDegrees: -85);

    private static double InterpolateWorkModeIconValue(
        double from,
        double to,
        double progress) =>
        from + (to - from) * progress;

    private void ApplyWorkModeIconVisualState(WorkModeIconVisualState state)
    {
        if (!_workModeIconVisualsInitialized)
        {
            return;
        }

        _workModeIconCurrentVisualState = state;
        _workSunIconVisual!.Opacity = state.SunOpacity;
        _workMoonIconVisual!.Opacity = state.MoonOpacity;
        _workSunIconScale!.ScaleX = state.SunScale;
        _workSunIconScale.ScaleY = state.SunScale;
        _workSunIconRotate!.Angle = state.SunRotationDegrees;
        _workSunIconTranslate!.X = state.SunTranslateX;
        _workSunIconTranslate.Y = state.SunTranslateY;
        _workMoonIconScale!.ScaleX = state.MoonScale;
        _workMoonIconScale.ScaleY = state.MoonScale;
        _workMoonIconRotate!.Angle = state.MoonRotationDegrees;
        _workMoonIconTranslate!.X = state.MoonTranslateX;
        _workMoonIconTranslate.Y = state.MoonTranslateY;
        _workSunModeHaloVisual!.Opacity = state.SunHaloOpacity;
        _workMoonModeHaloVisual!.Opacity = state.MoonHaloOpacity;
        _workSunModeHaloScale!.ScaleX = state.SunHaloScale;
        _workSunModeHaloScale.ScaleY = state.SunHaloScale;
        _workMoonModeHaloScale!.ScaleX = state.MoonHaloScale;
        _workMoonModeHaloScale.ScaleY = state.MoonHaloScale;
        _workIconTwinkleVisual!.Opacity = state.TwinkleOpacity;
        _workIconTwinkleScale!.ScaleX = state.TwinkleScale;
        _workIconTwinkleScale.ScaleY = state.TwinkleScale;
        _workIconTwinkleRotate!.Angle = state.TwinkleRotationDegrees;
    }

    private void UpdateEdgeDockAfterDrag()
    {
        if (_isClosing || _isReminderActive)
        {
            return;
        }

        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var windowBounds = GetPetViewboxBoundsInScreenDips();
        var dragContext = _edgeDockDragContext;
        EdgeDockDragContext? applicableDragContext =
            dragContext is { } candidateContext &&
            AreEquivalentWorkAreas(candidateContext.WorkArea, workArea)
                ? dragContext
                : null;
        var contactBounds = GetDragReleaseContactBounds(
            windowBounds,
            applicableDragContext?.OriginDock ?? EdgeDock.None);
        var touchedEdges = FindTouchedEdges(
                workArea,
                contactBounds,
                EdgeDockActivationDistance)
            .ToList();
        if (applicableDragContext is { } context)
        {
            foreach (var sweptEdge in FindSweptTouchedEdges(
                         workArea,
                         context.StartWindowBounds,
                         windowBounds,
                         context.StartContactBounds,
                         contactBounds))
            {
                if (!touchedEdges.Contains(sweptEdge))
                {
                    touchedEdges.Add(sweptEdge);
                }
            }
        }

        var touchedEdge = EdgeDock.None;
        foreach (var candidate in PrioritizeTouchedEdgesAfterDrag(
                     touchedEdges,
                     applicableDragContext,
                     windowBounds))
        {
            var screenEdge = candidate switch
            {
                EdgeDock.Left => MonitorWorkArea.ScreenEdge.Left,
                EdgeDock.Right => MonitorWorkArea.ScreenEdge.Right,
                EdgeDock.Bottom => MonitorWorkArea.ScreenEdge.Bottom,
                _ => throw new ArgumentOutOfRangeException()
            };
            var orthogonalContact = candidate == EdgeDock.Bottom
                ? contactBounds.Left + contactBounds.Width / 2
                : contactBounds.Top + contactBounds.Height / 2;
            if (!MonitorWorkArea.IsExternalWorkAreaEdgeAt(
                    this,
                    PetSizeViewbox,
                    screenEdge,
                    orthogonalContact))
            {
                continue;
            }

            touchedEdge = candidate;
            break;
        }

        if (touchedEdge == EdgeDock.None)
        {
            if (_dragPreservesWorkMode)
            {
                _workEdgeDock = EdgeDock.None;
                _workEdgeHandoffFrozenTimestamp = 0;
            }
            RestartAutomaticCountdown();
            return;
        }

        switch (touchedEdge)
        {
            case EdgeDock.Left:
                MoveMainWindowTo(
                    Left + workArea.Left - windowBounds.Left,
                    Top + Math.Clamp(
                              windowBounds.Top,
                              workArea.Top,
                              workArea.Bottom - windowBounds.Height) -
                          windowBounds.Top);
                break;
            case EdgeDock.Right:
                MoveMainWindowTo(
                    Left + workArea.Right - windowBounds.Right,
                    Top + Math.Clamp(
                              windowBounds.Top,
                              workArea.Top,
                              workArea.Bottom - windowBounds.Height) -
                          windowBounds.Top);
                break;
            case EdgeDock.Bottom:
                MoveMainWindowTo(
                    Left + Math.Clamp(
                               windowBounds.Left,
                               workArea.Left,
                               workArea.Right - windowBounds.Width) -
                           windowBounds.Left,
                    Top + workArea.Bottom - windowBounds.Bottom);
                break;
        }

        if (_dragPreservesWorkMode && _workState != WorkState.Idle)
        {
            // Freeze the currently visible work pose. A hot edge page replaces
            // it now; a cold page keeps this exact descriptor on screen until
            // the next Rendering callback can publish edge rest atomically.
            StartWorkEdgeHandoff(touchedEdge);
            return;
        }

        _workEdgeDock = EdgeDock.None;
        _workEdgeHandoffFrozenTimestamp = 0;
        EnterEdgePeek(touchedEdge);
    }

    private Point GetScreenPointOrFallback(Point localPoint, Point fallback)
    {
        try
        {
            var screenPoint = PointToScreen(localPoint);
            return double.IsFinite(screenPoint.X) &&
                   double.IsFinite(screenPoint.Y)
                ? screenPoint
                : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private Point GetPointerScreenPointOrFallback(
        Point localPoint,
        Point fallback,
        bool allowLocalFallback)
    {
        if (TryGetPointerScreenPoint(out var screenPoint))
        {
            return screenPoint;
        }

        // A roaming window moves underneath a stationary pointer while the
        // reverse boarding animation straightens it. Reconstructing a screen
        // point from WPF local coordinates in that state can turn HWND motion
        // into a false drag, so a failed native sample deliberately holds the
        // last known physical point. Ordinary dragging keeps the existing WPF
        // fallback for a not-yet-created HWND or a transient native failure.
        return allowLocalFallback
            ? GetScreenPointOrFallback(localPoint, fallback)
            : fallback;
    }

    private bool TryGetPointerScreenPoint(out Point screenPoint)
    {
        var provider = _pointerScreenPointProviderForTesting;
        if (provider is not null)
        {
            var providedPoint = provider();
            if (providedPoint is Point point &&
                double.IsFinite(point.X) &&
                double.IsFinite(point.Y))
            {
                screenPoint = point;
                return true;
            }

            screenPoint = default;
            return false;
        }

        if (GetCursorPos(out var cursorPoint))
        {
            screenPoint = new Point(cursorPoint.X, cursorPoint.Y);
            return true;
        }

        screenPoint = default;
        return false;
    }

    private static bool IsFiniteRect(Rect rect) =>
        double.IsFinite(rect.Left) &&
        double.IsFinite(rect.Top) &&
        double.IsFinite(rect.Width) &&
        double.IsFinite(rect.Height) &&
        rect.Width >= 0 && rect.Height >= 0;

    private Rect GetDragReleaseContactBounds(
        Rect windowBounds,
        EdgeDock dragOriginDock)
    {
        // Exiting an edge pose can leave that atlas frame visible for one
        // background-decode turn. Drag hit-testing must follow the idle/todo
        // pose and pillow that the interaction has already requested,
        // otherwise a fast Right -> Bottom toss samples the old short
        // side-peek silhouette and misses the lower edge.
        return dragOriginDock == EdgeDock.None
            ? GetPetContactBounds(windowBounds)
            : GetPetContactBoundsForFrame(
                windowBounds,
                _bubbleMode == BubbleMode.Todo
                    ? _todoFrame
                    : _idleFrame,
                includePillow: true);
    }

    private Rect GetPetContactBounds(Rect windowBounds)
    {
        return GetPetContactBoundsForFrame(
            windowBounds,
            _currentSpriteFrame,
            PillowImage.Opacity > 0.5);
    }

    private Rect GetPetContactBoundsForFrame(
        Rect windowBounds,
        SpriteFrame? sourceFrame,
        bool includePillow)
    {
        if (sourceFrame is not { } frame ||
            windowBounds.Width <= 0 || windowBounds.Height <= 0)
        {
            return windowBounds;
        }

        var leftPixel = Math.Clamp(frame.DestinationX, 0, DisplayPixelWidth);
        var topPixel = Math.Clamp(frame.DestinationY, 0, DisplayPixelHeight);
        var rightPixel = Math.Clamp(
            frame.DestinationX + frame.Width,
            0,
            DisplayPixelWidth);
        var bottomPixel = Math.Clamp(
            frame.DestinationY + frame.Height,
            0,
            DisplayPixelHeight);

        if (includePillow)
        {
            // Tight non-transparent bounds of luban-pillow-layer.png. Contact
            // follows the pixels the user can actually see instead of the
            // transparent HWND margin around the sprite.
            leftPixel = Math.Min(leftPixel, 6);
            topPixel = Math.Min(topPixel, 366);
            rightPixel = Math.Max(rightPixel, 393);
            bottomPixel = Math.Max(bottomPixel, 503);
        }

        if (rightPixel <= leftPixel || bottomPixel <= topPixel)
        {
            return windowBounds;
        }

        var horizontalScale = windowBounds.Width / DisplayPixelWidth;
        var verticalScale = windowBounds.Height / DisplayPixelHeight;
        return new Rect(
            windowBounds.Left + leftPixel * horizontalScale,
            windowBounds.Top + topPixel * verticalScale,
            (rightPixel - leftPixel) * horizontalScale,
            (bottomPixel - topPixel) * verticalScale);
    }

    private static EdgeDock FindTouchedEdge(
        Rect workArea,
        Rect windowBounds,
        double activationDistance) =>
        FindTouchedEdges(workArea, windowBounds, activationDistance)
            .DefaultIfEmpty(EdgeDock.None)
            .First();

    private static IEnumerable<EdgeDock> FindTouchedEdges(
        Rect workArea,
        Rect windowBounds,
        double activationDistance)
    {
        var overlapsVertically =
            windowBounds.Bottom > workArea.Top &&
            windowBounds.Top < workArea.Bottom;
        var overlapsHorizontally =
            windowBounds.Right > workArea.Left &&
            windowBounds.Left < workArea.Right;
        var candidates = new (EdgeDock Dock, double Gap, bool StillVisible)[]
        {
            (
                EdgeDock.Left,
                windowBounds.Left - workArea.Left,
                overlapsVertically && windowBounds.Right > workArea.Left),
            (
                EdgeDock.Right,
                workArea.Right - windowBounds.Right,
                overlapsVertically && windowBounds.Left < workArea.Right),
            (
                EdgeDock.Bottom,
                workArea.Bottom - windowBounds.Bottom,
                overlapsHorizontally && windowBounds.Top < workArea.Bottom)
        };

        return candidates
            .Where(candidate =>
                candidate.StillVisible &&
                double.IsFinite(candidate.Gap) &&
                candidate.Gap <= activationDistance)
            .OrderBy(candidate => Math.Abs(candidate.Gap))
            .ThenBy(candidate => (int)candidate.Dock)
            .Select(candidate => candidate.Dock);
    }

    private static IEnumerable<EdgeDock> FindSweptTouchedEdges(
        Rect workArea,
        Rect startWindowBounds,
        Rect endWindowBounds,
        Rect startContactBounds,
        Rect endContactBounds)
    {
        var movedLeft = endWindowBounds.Left < startWindowBounds.Left;
        var movedRight = endWindowBounds.Left > startWindowBounds.Left;
        var movedDown = endWindowBounds.Top > startWindowBounds.Top;
        var endContactOverlapsVertically =
            endContactBounds.Bottom > workArea.Top &&
            endContactBounds.Top < workArea.Bottom;
        var endContactOverlapsHorizontally =
            endContactBounds.Right > workArea.Left &&
            endContactBounds.Left < workArea.Right;

        if (movedLeft &&
            endContactOverlapsVertically &&
            startContactBounds.Right > workArea.Left &&
            endContactBounds.Right <= workArea.Left)
        {
            yield return EdgeDock.Left;
        }

        if (movedRight &&
            endContactOverlapsVertically &&
            startContactBounds.Left < workArea.Right &&
            endContactBounds.Left >= workArea.Right)
        {
            yield return EdgeDock.Right;
        }

        if (movedDown &&
            endContactOverlapsHorizontally &&
            startContactBounds.Top < workArea.Bottom &&
            endContactBounds.Top >= workArea.Bottom)
        {
            yield return EdgeDock.Bottom;
        }
    }

    private static IEnumerable<EdgeDock> PrioritizeTouchedEdgesAfterDrag(
        IReadOnlyList<EdgeDock> candidates,
        EdgeDockDragContext? dragContext,
        Rect endWindowBounds)
    {
        if (candidates.Count <= 1 || dragContext is not { } context)
        {
            return candidates;
        }

        var deltaX =
            endWindowBounds.Left - context.StartWindowBounds.Left;
        var deltaY =
            endWindowBounds.Top - context.StartWindowBounds.Top;
        var preferredDock = EdgeDock.None;
        if (deltaY > 0 &&
            Math.Abs(deltaY) >= Math.Abs(deltaX) &&
            candidates.Contains(EdgeDock.Bottom))
        {
            preferredDock = EdgeDock.Bottom;
        }
        else if (Math.Abs(deltaX) > Math.Abs(deltaY))
        {
            var horizontalDock = deltaX < 0
                ? EdgeDock.Left
                : EdgeDock.Right;
            if (candidates.Contains(horizontalDock))
            {
                preferredDock = horizontalDock;
            }
        }

        if (preferredDock == EdgeDock.None &&
            context.OriginDock != EdgeDock.None &&
            candidates.Contains(context.OriginDock))
        {
            preferredDock = context.OriginDock;
        }

        return candidates
            .Select((dock, index) => (Dock: dock, Index: index))
            .OrderBy(candidate =>
                candidate.Dock == preferredDock
                    ? 0
                    : candidate.Dock == context.OriginDock
                        ? 2
                        : 1)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Dock);
    }

    private static bool AreEquivalentWorkAreas(Rect first, Rect second) =>
        Math.Abs(first.Left - second.Left) <= EdgeContactTolerance &&
        Math.Abs(first.Top - second.Top) <= EdgeContactTolerance &&
        Math.Abs(first.Right - second.Right) <= EdgeContactTolerance &&
        Math.Abs(first.Bottom - second.Bottom) <= EdgeContactTolerance;

    private void EnterEdgePeek(EdgeDock dock)
    {
        _ = EnterEdgePeekCore(dock, EdgeFrameBlendDuration);
    }

    private bool EnterEdgePeekCore(
        EdgeDock dock,
        TimeSpan entryBlendDuration)
    {
        if (_isClosing || _isReminderActive || dock == EdgeDock.None)
        {
            return false;
        }

        _edgePeekHoldTimer.Stop();
        _workEnterAfterEdgePeekExitRequested = false;
        StopEdgeRoaming(
            scheduleNext: true,
            restoreIdleFrame: false,
            interrupted: true);
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
            UpdateVisualClockSubscription();
            return false;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        _edgeDock = dock;
        CancelTodoOpenAfterEdgeRoamStop();
        CancelTodoOpenAfterWorkExit();
        if (_bubbleMode == BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.None);
        }
        else if (_todoWindow.IsVisible)
        {
            // Todo and scheduled tasks share this owned panel. A docked pet
            // must not leave either tab floating behind at its old position.
            HideTodoWindowVisual();
        }

        if (_activeClip is { } activeClip)
        {
            _activeClip = null;
            _activeFrameIndex = -1;
            _activeClipStartedTimestamp = 0;
            _activeFrameDeadlineTimestamp = 0;
            ClearDeferredActiveClipClock();
            if (_bubbleMode == BubbleMode.Cute)
            {
                SetBubbleMode(BubbleMode.None);
            }
        }

        RequestIdleSpritePageTrim();
        // Match the established v1.0.57 docking cadence: every supported edge
        // first settles on the authored rest pose, then begins the peek cycle.
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
        _nextFrameBlendDuration = entryBlendDuration;
        ShowStableFrame(restFrame);
        if (_currentSpriteFrame is SpriteFrame displayedFrame &&
            displayedFrame == restFrame)
        {
            StartEdgePeekFrameClockAt(Stopwatch.GetTimestamp());
        }
        else
        {
            // A cold atlas page keeps the old stable pixels on screen. Do not
            // let its logical pose clock run until this exact entry frame is
            // published by a composition pass.
            _edgePeekFrameDeadlineTimestamp = long.MaxValue;
        }

        UpdateVisualClockSubscription();
        return _currentSpriteFrame is SpriteFrame currentFrame &&
               currentFrame == restFrame;
    }

    private void ExitEdgePeek(
        bool restartAutomaticCountdown,
        bool restoreIdleFrame = true)
    {
        if (_edgeDock == EdgeDock.None)
        {
            _workEnterAfterEdgePeekExitRequested = false;
            return;
        }

        _edgePeekHoldTimer.Stop();
        _workEnterAfterEdgePeekExitRequested = false;
        _edgeDock = EdgeDock.None;
        _edgePeekFrameIndex = 0;
        _edgePeekFrameDeadlineTimestamp = 0;
        if (restoreIdleFrame)
        {
            BakeCurrentPetVisualTransformIntoDisplayFrame();
        }

        ResetPetVisualTransforms();
        if (restoreIdleFrame)
        {
            _nextFrameBlendDuration = EdgeFrameBlendDuration;
            if (_bubbleMode == BubbleMode.Todo)
            {
                ShowStableTodoFrame();
            }
            else
            {
                ShowStableFrame(_idleFrame);
            }
        }
        if (restartAutomaticCountdown)
        {
            RestartAutomaticCountdown();
        }

        ScheduleUnpinnedPetSizePreviewCommit();
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

        if (_isClosing || _edgeDock == EdgeDock.None)
        {
            return;
        }

        if (_workEnterAfterEdgePeekExitRequested)
        {
            var exitFrames = GetEdgeFrames(_edgeDock);
            if (_edgePeekFrameIndex == exitFrames.Length - 1)
            {
                CompleteWorkModeEntryAfterEdgePeekExit();
                return;
            }
        }

        if (_edgePeekHoldTimer.IsEnabled)
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
        var fullyPeekedFrameIndex = frames.Length / 2 - 1;
        var currentFrameIsEndpoint =
            _edgePeekFrameIndex == fullyPeekedFrameIndex ||
            _edgePeekFrameIndex == frames.Length - 1;
        var deadlineToleranceTicks =
            _synchronizeEdgePeekToRenderingCadence && !currentFrameIsEndpoint
                ? EdgeVisualFrameDeadlineToleranceTicks
                : 0;
        while (timestamp >= _edgePeekFrameDeadlineTimestamp -
                            deadlineToleranceTicks &&
               _edgeDock != EdgeDock.None)
        {
            _edgePeekFrameIndex = (_edgePeekFrameIndex + 1) % frames.Length;
            frameChanged = true;
            var nextHoldDuration = _workEnterAfterEdgePeekExitRequested
                ? EdgePeekMotionFrameInterval
                : GetEdgePeekFrameHoldDuration(
                    _edgePeekFrameIndex,
                    frames.Length);
            _edgePeekFrameDeadlineTimestamp +=
                ToStopwatchTicks(nextHoldDuration);
            if (_synchronizeEdgePeekToRenderingCadence)
            {
                // A 59/59.94/60 Hz compositor cannot present an independent
                // 60 Hz pose clock without periodically holding one pose and
                // skipping the next. During a healthy near-60-Hz sequence,
                // publish exactly one motion pose per composition and rebase
                // its deadline to that presentation. Endpoint holds use no
                // early tolerance. A real stall has a non-near-60 gap and
                // therefore keeps the absolute catch-up behavior.
                _edgePeekFrameDeadlineTimestamp = checked(
                    timestamp + ToStopwatchTicks(nextHoldDuration));
                break;
            }
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

        if (!_workEnterAfterEdgePeekExitRequested)
        {
            ArmEdgePeekHoldTimerIfStable(timestamp);
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

        var fullyPeekedFrameIndex = frameCount / 2 - 1;
        if (frameIndex == fullyPeekedFrameIndex)
        {
            return EdgePeekFullyPeekedHold;
        }

        return frameIndex == frameCount - 1
            ? GetEdgePeekRestHoldDuration(frameCount)
            : EdgePeekMotionFrameInterval;
    }

    private static TimeSpan GetEdgePeekRestHoldDuration(int frameCount)
    {
        if (frameCount < 8 || frameCount % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                $"Invalid edge frame count: {frameCount}");
        }

        var restTicks = checked(
            EdgePeekCycleInterval.Ticks -
            EdgePeekFullyPeekedHold.Ticks -
            (frameCount - 2L) * EdgePeekMotionFrameInterval.Ticks);
        if (restTicks <= 0)
        {
            throw new InvalidOperationException(
                $"Edge peek cycle {EdgePeekCycleInterval} is too short " +
                $"for {frameCount} frames.");
        }

        return TimeSpan.FromTicks(restTicks);
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
            (frameCount - 2L) * ToStopwatchTicks(EdgePeekMotionFrameInterval) +
            ToStopwatchTicks(EdgePeekFullyPeekedHold) +
            ToStopwatchTicks(GetEdgePeekRestHoldDuration(frameCount)));
    }

    private void StartEdgePeekFrameClockAt(long timestamp)
    {
        if (_edgeDock == EdgeDock.None)
        {
            return;
        }

        var frames = GetEdgeFrames(_edgeDock);
        _edgePeekFrameDeadlineTimestamp = checked(
            timestamp + ToStopwatchTicks(
                GetEdgePeekFrameHoldDuration(
                    _edgePeekFrameIndex,
                    frames.Length)));
        ArmEdgePeekHoldTimerIfStable(timestamp);
    }

    private void ArmEdgePeekHoldTimerIfStable(long timestamp)
    {
        _edgePeekHoldTimer.Stop();
        if (_isClosing ||
            _edgeDock == EdgeDock.None ||
            _edgePeekFrameDeadlineTimestamp is <= 0 or long.MaxValue)
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

        var fullyPeekedFrameIndex = frames.Length / 2 - 1;
        if (_edgePeekFrameIndex != fullyPeekedFrameIndex &&
            _edgePeekFrameIndex != frames.Length - 1)
        {
            return;
        }

        var remainingTicks = _edgePeekFrameDeadlineTimestamp - timestamp;
        if (remainingTicks <= 0)
        {
            return;
        }

        _edgePeekHoldTimer.Interval = TimeSpan.FromSeconds(
            Math.Max(0.001, remainingTicks / (double)Stopwatch.Frequency));
        _edgePeekHoldTimer.Start();
    }

    private void EdgePeekHoldTimer_Tick(object? sender, EventArgs e)
    {
        _edgePeekHoldTimer.Stop();
        if (_isClosing || _edgeDock == EdgeDock.None)
        {
            UpdateVisualClockSubscription();
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        AdvanceEdgePeek(timestamp);
        UpdateVisualClockSubscription();
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
            EdgeDock.Bottom => _edgeBottomFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(dock), dock, null)
        };
    }

    private void ShowCuteReaction()
    {
        RestartAutomaticCountdown();

        if (_isClosing || _activeClip is not null)
        {
            return;
        }

        var selectedIndex = SelectRandomClickReactionIndex();
        var clip = _reactionClips[selectedIndex];
        if (!TryStartReaction(clip, showCuteBubble: true))
        {
            return;
        }

        _lastClickReactionIndex = selectedIndex;
    }

    private int SelectRandomClickReactionIndex()
    {
        if (_reactionClips.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one click reaction is required.");
        }

        if (_reactionClips.Length == 1 ||
            _lastClickReactionIndex < 0 ||
            _lastClickReactionIndex >= _reactionClips.Length)
        {
            return _clickReactionRandom.Next(_reactionClips.Length);
        }

        var candidate = _clickReactionRandom.Next(_reactionClips.Length - 1);
        return candidate >= _lastClickReactionIndex
            ? candidate + 1
            : candidate;
    }

    private bool TryStartReaction(AnimationClip clip, bool showCuteBubble)
    {
        if (_isClosing || _sessionInactive ||
            _workState != WorkState.Idle ||
            _activeClip is not null || _dragInteractionActive ||
            _isReminderActive ||
            _bubbleMode == BubbleMode.Todo ||
            _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
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

        if (showCuteBubble && _bubbleMode != BubbleMode.Todo)
        {
            SetBubbleMode(BubbleMode.Cute);
        }

        _nextFrameBlendDuration = ActionTransitionDuration;
        _nextFrameMinimumHold = ActionTransitionDuration;
        ShowActiveClipFrame(0);
        return true;
    }

    private void ScheduleNextEdgeRoam(long timestamp, TimeSpan delay)
    {
        _edgeRoamPreloadRequested = false;
        if (_isClosing || !_edgeRoamingEnabled)
        {
            _nextEdgeRoamDueTimestamp = 0;
            return;
        }

        _nextEdgeRoamDueTimestamp = checked(
            timestamp + ToStopwatchTicks(delay));
    }

    private bool IsEdgeRoamDue(long timestamp) =>
        _edgeRoamingEnabled &&
        _nextEdgeRoamDueTimestamp > 0 &&
        timestamp >= _nextEdgeRoamDueTimestamp;

    private void StartEdgeRoamPreloadIfDue(long timestamp)
    {
        if (_edgeRoamPreloadRequested ||
            !_edgeRoamingEnabled ||
            _nextEdgeRoamDueTimestamp <= 0 ||
            timestamp <
            _nextEdgeRoamDueTimestamp -
            ToStopwatchTicks(EdgeRoamPreloadLeadTime))
        {
            return;
        }

        _edgeRoamPreloadRequested = true;
        ContinueEdgeRoamPreload();
    }

    private void ContinueEdgeRoamPreload()
    {
        if (!_edgeRoamPreloadRequested ||
            _isClosing ||
            !_edgeRoamingEnabled ||
            _isEdgeRoaming ||
            _nextEdgeRoamDueTimestamp <= 0)
        {
            return;
        }

        foreach (var frame in _roamBoardingFrames)
        {
            if (!_residentSpritePages.ContainsKey(frame.PageName))
            {
                RequestSpritePagePrefetch(frame.PageName, urgent: true);
                return;
            }
        }

        foreach (var frame in _roamFlightFrames)
        {
            if (!_residentSpritePages.ContainsKey(frame.PageName))
            {
                RequestSpritePagePrefetch(frame.PageName, urgent: true);
                return;
            }
        }

        TrimReusableSpritePageBuffersForReadyRoam();

        // The final decode completion can wake an already-due roam directly;
        // no 16 ms polling timer is needed while background I/O is in flight.
        ArmAutomaticWakeTimer(Stopwatch.GetTimestamp());
    }

    private bool AreAllEdgeRoamPreloadPagesResident()
    {
        foreach (var frame in _roamBoardingFrames)
        {
            if (!_residentSpritePages.ContainsKey(frame.PageName))
            {
                return false;
            }
        }

        return AreAllSequencePagesResident(_roamFlightFrames);
    }

    private void TrimReusableSpritePageBuffersForReadyRoam()
    {
        if (_spritePagePrefetchTask is not null ||
            !AreAllEdgeRoamPreloadPagesResident())
        {
            return;
        }

        // Every page needed by forward travel and immediate reverse boarding
        // is resident now. Free decode arrays cannot improve this roam, so drop
        // only those free arrays before animation starts; resident pixels and
        // all frame timing remain untouched.
        TrimSpritePageBufferPoolToTarget(_residentSpritePageBytes);
    }

    private bool StartEdgeRoaming()
    {
        if (_isClosing || _sessionInactive ||
            !_edgeRoamingEnabled || _isEdgeRoaming ||
            !_automaticAnimationEnabled || _isReminderActive ||
            _workState != WorkState.Idle ||
            _activeClip is not null || _dragInteractionActive || _pointerDown ||
            _isPetSizeTransitioning || _isPetSizePreviewSessionActive ||
            _isPetSizeAdjustmentActive || _bubbleMode != BubbleMode.None ||
            _todoWindow.IsVisible || BubblePopup.IsOpen ||
            _edgeDock != EdgeDock.None)
        {
            return false;
        }

        CenterPetViewboxForRotation();
        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var routeLeft = workArea.Left + ScreenEdgeMargin;
        var routeTop = workArea.Top + ScreenEdgeMargin;
        var routeRight = workArea.Right - ScreenEdgeMargin;
        var routeBottom = workArea.Bottom - ScreenEdgeMargin;
        if (!double.IsFinite(routeLeft) || !double.IsFinite(routeTop) ||
            !double.IsFinite(routeRight) || !double.IsFinite(routeBottom) ||
            routeRight - routeLeft < 24 || routeBottom - routeTop < 24)
        {
            ScheduleNextEdgeRoam(
                Stopwatch.GetTimestamp(),
                EdgeRoamInterval);
            return false;
        }

        _edgeRoamRouteBounds = new Rect(
            routeLeft,
            routeTop,
            routeRight - routeLeft,
            routeBottom - routeTop);
        var radius = GetEdgeRoamCornerRadius(_edgeRoamRouteBounds);
        _edgeRoamRouteLength = GetEdgeRoamRouteLength(
            _edgeRoamRouteBounds,
            radius);
        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        var currentWindowLeft = double.IsFinite(visiblePetBounds.Left)
            ? visiblePetBounds.Left
            : routeRight;
        var currentWindowTop = double.IsFinite(visiblePetBounds.Top)
            ? visiblePetBounds.Top
            : routeBottom;
        var displayedWidth = visiblePetBounds.Width;
        var displayedHeight = visiblePetBounds.Height;
        var supportOffset = GetEdgeRoamSupportOffset(
            displayedWidth,
            displayedHeight,
            rotationDegrees: 0);
        _edgeRoamStartPoint = new Point(
            currentWindowLeft + supportOffset.X,
            currentWindowTop + supportOffset.Y);
        _edgeRoamCurrentSupportPoint = _edgeRoamStartPoint;
        _edgeRoamDisembarkSupportPoint = _edgeRoamStartPoint;
        _edgeRoamCurrentSupportPointValid = true;
        _edgeRoamRouteStartDistance = FindClosestEdgeRoamRouteDistance(
            _edgeRoamRouteBounds,
            radius,
            _edgeRoamStartPoint);
        _edgeRoamRouteStartPoint = GetEdgeRoamRoutePoint(
            _edgeRoamRouteBounds,
            radius,
            _edgeRoamRouteStartDistance);
        _edgeRoamDirection = _random.Next(2) == 0 ? -1 : 1;
        _edgeRoamRouteTangent = GetEdgeRoamRouteTangent(
            _edgeRoamRouteBounds,
            radius,
            _edgeRoamRouteStartDistance,
            _edgeRoamDirection);
        _edgeRoamApproachLength = GetPointDistance(
            _edgeRoamStartPoint,
            _edgeRoamRouteStartPoint);
        _edgeRoamReturnLength = _edgeRoamApproachLength;
        _edgeRoamLogicalLeft = currentWindowLeft;
        _edgeRoamLogicalTop = currentWindowTop;
        _edgeRoamFacingScaleX = PetFacingScale.ScaleX < 0 ? -1 : 1;
        _edgeRoamStartedTimestamp = 0;
        _edgeRoamLastRenderingTimestamp = 0;
        _edgeRoamClockStarted = false;
        _edgeRoamBoardingPagesReady = false;
        _edgeRoamFlightPagesReady = false;
        _edgeRoamBoardingReverse = false;
        _edgeRoamStopScheduleNext = false;
        _edgeRoamStopInterrupted = false;
        _edgeRoamBoardingStartIndex = 0;
        _edgeRoamPhase = EdgeRoamPhase.None;
        _edgeRoamLandingSeamElapsedSeconds = 0;
        _nextEdgeRoamDueTimestamp = 0;
        TrimReusableSpritePageBuffersForReadyRoam();
        _edgeRoamPreloadRequested = false;
        CancelTodoOpenAfterEdgeRoamStop();
        _isEdgeRoaming = true;

        StopPillowBreathing();
        _automaticTimer.Stop();
        StartEdgeRoamBoarding(
            reverse: false,
            timestamp: Stopwatch.GetTimestamp());
        return true;
    }

    private void CenterPetViewboxForRotation()
    {
        var displayedHeight = PetSizeViewbox.ActualHeight > 0
            ? PetSizeViewbox.ActualHeight
            : PetHeight * NormalizePetSizeScale(_petSizeScale);
        var centeredPivotOffsetY =
            -(PetRoamRotationOriginYRatio - 0.5) * displayedHeight;
        if (PetSizeViewbox.HorizontalAlignment ==
                HorizontalAlignment.Center &&
            PetSizeViewbox.VerticalAlignment ==
                VerticalAlignment.Center &&
            Math.Abs(PetUserSizeOffset.X) < 0.000001 &&
            Math.Abs(PetUserSizeOffset.Y - centeredPivotOffsetY) <
                0.000001)
        {
            return;
        }

        var visibleBoundsBefore = GetPetViewboxBoundsInScreenDips();
        PetSizeViewbox.HorizontalAlignment = HorizontalAlignment.Center;
        PetSizeViewbox.VerticalAlignment = VerticalAlignment.Center;
        PetSizeViewbox.RenderTransformOrigin = new Point(
            0.5,
            0.5);
        PetUserSizeOffset.X = 0;
        PetUserSizeOffset.Y = centeredPivotOffsetY;
        UpdateLayout();
        var visibleBoundsAfter = GetPetViewboxBoundsInScreenDips();
        MoveMainWindowTo(
            Left + visibleBoundsBefore.Left - visibleBoundsAfter.Left,
            Top + visibleBoundsBefore.Top - visibleBoundsAfter.Top);
        _petSizeLogicalAnchor = null;
    }

    private void StopEdgeRoaming(
        bool scheduleNext,
        bool restoreIdleFrame,
        bool interrupted,
        bool immediate = false)
    {
        var wasRoaming = _isEdgeRoaming;
        if (!wasRoaming)
        {
            if (scheduleNext &&
                _edgeRoamingEnabled &&
                _nextEdgeRoamDueTimestamp <= 0)
            {
                ScheduleNextEdgeRoam(
                    Stopwatch.GetTimestamp(),
                    EdgeRoamInterval);
            }
            else if (!_edgeRoamingEnabled)
            {
                _nextEdgeRoamDueTimestamp = 0;
            }

            return;
        }

        _edgeRoamStopScheduleNext = scheduleNext;
        _edgeRoamStopInterrupted |= interrupted;
        if (immediate)
        {
            if (!restoreIdleFrame || _isReminderActive)
            {
                // Keep the exact rotated/mirrored pixels stable until the
                // Todo or reminder pose takes over. Resetting transforms
                // before a cold target page is ready causes a one-frame flip.
                BakeCurrentPetVisualTransformIntoDisplayFrame();
            }

            CancelEdgeRoamSpritePagePrefetch(includeBoarding: true);
            CompleteEdgeRoamStop(
                restoreIdleFrame: restoreIdleFrame && !_isReminderActive);
            return;
        }

        var boardingPageFailed =
            IsSequenceSpritePageName(
                _roamBoardingFrames,
                _failedSpritePageName);
        if (restoreIdleFrame && !_isReminderActive && !boardingPageFailed)
        {
            // Stop movement immediately, but keep the visual state active until
            // the same boarding sequence has played backwards to frame zero.
            CancelEdgeRoamSpritePagePrefetch(includeBoarding: false);
            if (_edgeRoamPhase != EdgeRoamPhase.Disembarking)
            {
                StartEdgeRoamBoarding(
                    reverse: true,
                    timestamp: Stopwatch.GetTimestamp());
            }

            RequestResidentSpritePageTrim();
            return;
        }

        // Todo/reminder/edge pages can be cold. Preserve the exact mirrored
        // panda pixels before clearing transforms so the old hot frame cannot
        // visibly flip while its replacement page is decoding.
        BakeCurrentPetVisualTransformIntoDisplayFrame();
        CompleteEdgeRoamStop(
            restoreIdleFrame: restoreIdleFrame && !_isReminderActive);
    }

    private void CompleteEdgeRoamStop(bool restoreIdleFrame)
    {
        var wasRoaming = _isEdgeRoaming;
        var scheduleNext = _edgeRoamStopScheduleNext;
        var interrupted = _edgeRoamStopInterrupted;
        var shouldOpenTodoAfterStop =
            wasRoaming && interrupted && restoreIdleFrame &&
            _openTodoAfterEdgeRoamStopRequested &&
            !_isClosing && !_sessionInactive && !_isReminderActive &&
            !_isTransientPetSizeOverride;
        if (!shouldOpenTodoAfterStop)
        {
            CancelTodoOpenAfterEdgeRoamStop();
        }

        _isEdgeRoaming = false;
        _edgeRoamPhase = EdgeRoamPhase.None;
        _edgeRoamClockStarted = false;
        _edgeRoamBoardingPagesReady = false;
        _edgeRoamFlightPagesReady = false;
        _edgeRoamBoardingReverse = false;
        _edgeRoamCurrentSupportPointValid = false;
        _edgeRoamBoardingStartIndex = 0;
        _edgeRoamStopScheduleNext = false;
        _edgeRoamStopInterrupted = false;
        _edgeRoamStartedTimestamp = 0;
        _edgeRoamLastRenderingTimestamp = 0;
        _edgeRoamRouteLength = 0;
        _edgeRoamApproachLength = 0;
        _edgeRoamReturnLength = 0;
        _edgeRoamLandingSeamElapsedSeconds = 0;
        _edgeRoamRouteTangent = default;
        _edgeRoamFacingScaleX = 1;
        _edgeRoamRotationDegrees = 0;
        _edgeRoamDisembarkStartRotationDegrees = 0;
        _edgeRoamCurrentSupportPoint = default;
        _edgeRoamDisembarkSupportPoint = default;
        CancelEdgeRoamSpritePagePrefetch(includeBoarding: true);
        if (wasRoaming)
        {
            ResetPetVisualTransforms();
            if (restoreIdleFrame && !_isReminderActive)
            {
                _nextFrameBlendDuration = TimeSpan.Zero;
                ShowStableFrame(_idleFrame);
            }
        }

        if (scheduleNext)
        {
            ScheduleNextEdgeRoam(
                Stopwatch.GetTimestamp(),
                EdgeRoamInterval);
        }
        else if (!_edgeRoamingEnabled)
        {
            _nextEdgeRoamDueTimestamp = 0;
        }

        if (wasRoaming)
        {
            RequestIdleSpritePageTrim(immediate: true);
            RestartAutomaticCountdown();
            UpdateVisualClockSubscription();
        }

        if (shouldOpenTodoAfterStop)
        {
            QueueTodoOpenAfterEdgeRoamStop();
        }
    }

    private void AdvanceEdgeRoaming(long timestamp)
    {
        if (!_isEdgeRoaming)
        {
            return;
        }

        if (_edgeRoamPhase is EdgeRoamPhase.Boarding or
            EdgeRoamPhase.Disembarking)
        {
            AdvanceEdgeRoamBoarding(timestamp);
            return;
        }

        if (_edgeRoamPhase == EdgeRoamPhase.Traveling)
        {
            AdvanceEdgeRoamTravel(timestamp);
        }
    }

    private void StartEdgeRoamBoarding(bool reverse, long timestamp)
    {
        if (!_isEdgeRoaming)
        {
            return;
        }

        var disembarkStartRotationDegrees =
            reverse && double.IsFinite(_edgeRoamRotationDegrees)
                ? _edgeRoamRotationDegrees
                : 0;
        var wasForwardBoarding =
            _edgeRoamPhase == EdgeRoamPhase.Boarding &&
            !_edgeRoamBoardingReverse;
        var boardingStartIndex = 0;
        if (reverse &&
            _currentSpriteFrame is SpriteFrame currentFrame)
        {
            var currentBoardingIndex = FindSpriteFrameIndex(
                _roamBoardingFrames,
                currentFrame);
            boardingStartIndex = currentBoardingIndex >= 0
                ? currentBoardingIndex
                : wasForwardBoarding
                    ? 0
                    : _roamBoardingFrames.Length - 1;
        }

        _edgeRoamPhase = reverse
            ? EdgeRoamPhase.Disembarking
            : EdgeRoamPhase.Boarding;
        _edgeRoamBoardingReverse = reverse;
        _edgeRoamBoardingStartIndex = boardingStartIndex;
        _edgeRoamClockStarted = false;
        _edgeRoamStartedTimestamp = 0;
        _edgeRoamLastRenderingTimestamp = 0;
        _edgeRoamBoardingPagesReady =
            AreRequiredEdgeRoamBoardingPagesResident();

        if (!reverse)
        {
            ResetPetVisualTransforms();
            _edgeRoamDisembarkStartRotationDegrees = 0;
        }
        else
        {
            _edgeRoamDisembarkSupportPoint =
                _edgeRoamCurrentSupportPointValid &&
                double.IsFinite(_edgeRoamCurrentSupportPoint.X) &&
                double.IsFinite(_edgeRoamCurrentSupportPoint.Y)
                    ? _edgeRoamCurrentSupportPoint
                    : _edgeRoamStartPoint;
            // Keep the complete final travel transform for the first reverse
            // boarding pose, then straighten it smoothly. Clearing Angle here
            // made a vertical-edge stop snap from +/-90 to 0 in one frame.
            PetFacingScale.ScaleY = 1;
            _edgeRoamDisembarkStartRotationDegrees =
                disembarkStartRotationDegrees;
            _edgeRoamRotationDegrees =
                disembarkStartRotationDegrees;
            PetRoamRotate.Angle =
                disembarkStartRotationDegrees;
        }
        _nextFrameBlendDuration = TimeSpan.Zero;
        ShowStableFrame(
            _roamBoardingFrames[_edgeRoamBoardingStartIndex]);
        if (!_edgeRoamBoardingPagesReady)
        {
            RequestNextMissingEdgeRoamBoardingPage();
        }

        UpdateVisualClockSubscription();
    }

    private void AdvanceEdgeRoamBoarding(long timestamp)
    {
        if (!_edgeRoamBoardingPagesReady)
        {
            _edgeRoamBoardingPagesReady =
                AreRequiredEdgeRoamBoardingPagesResident();
        }

        var firstFrame = _edgeRoamBoardingReverse
            ? _roamBoardingFrames[_edgeRoamBoardingStartIndex]
            : _roamBoardingFrames[0];
        if (!_edgeRoamClockStarted)
        {
            if (_currentSpriteFrame is not SpriteFrame currentFrame ||
                currentFrame != firstFrame ||
                !_edgeRoamBoardingPagesReady)
            {
                _nextFrameBlendDuration = TimeSpan.Zero;
                ShowStableFrame(firstFrame);
                RequestNextMissingEdgeRoamBoardingPage();
                return;
            }

            _edgeRoamClockStarted = true;
            _edgeRoamStartedTimestamp = timestamp;
            _edgeRoamLastRenderingTimestamp = timestamp;
        }

        if (!_edgeRoamBoardingReverse && !_edgeRoamFlightPagesReady)
        {
            _edgeRoamFlightPagesReady =
                AreAllSequencePagesResident(_roamFlightFrames);
            if (!_edgeRoamFlightPagesReady)
            {
                RequestNextMissingSequencePage(_roamFlightFrames);
            }
        }

        var elapsedSeconds = AdvanceEdgeRoamClock(timestamp);
        if (_edgeRoamBoardingReverse)
        {
            _edgeRoamRotationDegrees =
                ResolveEdgeRoamDisembarkRotationDegrees(
                    _edgeRoamDisembarkStartRotationDegrees,
                    elapsedSeconds);
            PetRoamRotate.Angle = _edgeRoamRotationDegrees;
            ApplyEdgeRoamingPosition(
                _edgeRoamDisembarkSupportPoint.X,
                _edgeRoamDisembarkSupportPoint.Y);
        }

        var poseSpeed = EdgeRoamPoseFramesPerSecond * AnimationPlaybackSpeed;
        var frameStepCount = _edgeRoamBoardingReverse
            ? _edgeRoamBoardingStartIndex + 1
            : _roamBoardingFrames.Length;
        var boardingDuration = frameStepCount / poseSpeed;
        if (elapsedSeconds < boardingDuration)
        {
            var frameStep = Math.Min(
                frameStepCount - 1,
                (int)ResolveEdgeRoamPoseStep(elapsedSeconds, poseSpeed));
            var frameIndex = _edgeRoamBoardingReverse
                ? _edgeRoamBoardingStartIndex - frameStep
                : frameStep;
            _nextFrameBlendDuration = TimeSpan.Zero;
            ShowStableFrame(_roamBoardingFrames[frameIndex]);
            return;
        }

        if (_edgeRoamBoardingReverse)
        {
            CompleteEdgeRoamStop(restoreIdleFrame: true);
            return;
        }

        if (!_edgeRoamFlightPagesReady)
        {
            // The panda is already fully mounted. Hold the authored final pose
            // instead of letting a cold flight page consume logical time.
            _nextFrameBlendDuration = TimeSpan.Zero;
            ShowStableFrame(_roamBoardingFrames[^1]);
            RequestNextMissingSequencePage(_roamFlightFrames);
            return;
        }

        StartEdgeRoamTravel(timestamp);
    }

    private static double ResolveEdgeRoamDisembarkRotationDegrees(
        double startRotationDegrees,
        double elapsedSeconds)
    {
        if (!double.IsFinite(startRotationDegrees))
        {
            return 0;
        }

        var durationSeconds =
            EdgeRoamDisembarkStraightenDuration.TotalSeconds;
        if (!double.IsFinite(elapsedSeconds) ||
            elapsedSeconds <= 0 ||
            durationSeconds <= 0)
        {
            return startRotationDegrees;
        }

        var progress = Math.Clamp(
            elapsedSeconds / durationSeconds,
            0,
            1);
        var easedProgress =
            progress * progress * (3 - 2 * progress);
        var nearestUprightRotation =
            Math.Round(
                startRotationDegrees / 360,
                MidpointRounding.AwayFromZero) *
            360;
        return startRotationDegrees +
               (nearestUprightRotation - startRotationDegrees) *
               easedProgress;
    }

    private void StartEdgeRoamTravel(long timestamp)
    {
        _edgeRoamPhase = EdgeRoamPhase.Traveling;
        _edgeRoamBoardingReverse = false;
        _edgeRoamClockStarted = true;
        _edgeRoamStartedTimestamp = timestamp;
        _edgeRoamLastRenderingTimestamp = timestamp;

        var speed = EdgeRoamBaseSpeedDipsPerSecond * AnimationPlaybackSpeed;
        var totalDistance = _edgeRoamApproachLength +
                            _edgeRoamRouteLength +
                            _edgeRoamReturnLength;
        var motionDuration = totalDistance / speed;
        var authoredPoseSpeed =
            EdgeRoamPoseFramesPerSecond * AnimationPlaybackSpeed;
        var authoredLoopCount = motionDuration * authoredPoseSpeed /
                                _roamFlightFrames.Length;
        var loopCount = Math.Max(
            1L,
            (long)Math.Round(
                authoredLoopCount,
                MidpointRounding.AwayFromZero));
        _edgeRoamLandingSeamElapsedSeconds = Math.Max(0, motionDuration);
        if (motionDuration > 0 && double.IsFinite(motionDuration))
        {
            _edgeRoamTravelPoseFramesPerSecond =
                loopCount * _roamFlightFrames.Length / motionDuration;
        }
        else
        {
            _edgeRoamTravelPoseFramesPerSecond = authoredPoseSpeed;
        }

        ResetPetVisualTransforms();
        Point initialPosition;
        Point initialLookAhead;
        if (_edgeRoamApproachLength > 0)
        {
            initialPosition = _edgeRoamStartPoint;
            initialLookAhead = GetEdgeRoamConnectionPoint(
                _edgeRoamStartPoint,
                _edgeRoamRouteStartPoint,
                startTangent: default,
                endTangent: _edgeRoamRouteTangent * _edgeRoamApproachLength,
                progress: GetEdgeRoamConnectionLookAhead(
                    _edgeRoamApproachLength));
        }
        else
        {
            var radius = GetEdgeRoamCornerRadius(_edgeRoamRouteBounds);
            initialPosition = _edgeRoamRouteStartPoint;
            initialLookAhead = GetEdgeRoamRoutePoint(
                _edgeRoamRouteBounds,
                radius,
                _edgeRoamRouteStartDistance + _edgeRoamDirection * 2);
        }

        UpdateEdgeRoamFacing(initialPosition, initialLookAhead);
        ApplyEdgeRoamingPosition(
            initialPosition.X,
            initialPosition.Y);
        _nextFrameBlendDuration = TimeSpan.Zero;
        ShowStableFrame(_roamFlightFrames[0]);
        UpdateVisualClockSubscription();
    }

    private void AdvanceEdgeRoamTravel(long timestamp)
    {
        var elapsedSeconds = AdvanceEdgeRoamClock(timestamp);
        var speed = EdgeRoamBaseSpeedDipsPerSecond * AnimationPlaybackSpeed;
        var distance = elapsedSeconds * speed;
        var totalDistance = _edgeRoamApproachLength +
                            _edgeRoamRouteLength +
                            _edgeRoamReturnLength;

        if (elapsedSeconds >= _edgeRoamLandingSeamElapsedSeconds)
        {
            ApplyEdgeRoamingPosition(
                _edgeRoamStartPoint.X,
                _edgeRoamStartPoint.Y);
            StopEdgeRoaming(
                scheduleNext: true,
                restoreIdleFrame: true,
                interrupted: false);
            return;
        }

        var radius = GetEdgeRoamCornerRadius(_edgeRoamRouteBounds);
        Point position;
        Point lookAhead;
        if (distance < _edgeRoamApproachLength)
        {
            var progress = _edgeRoamApproachLength <= 0
                ? 1
                : distance / _edgeRoamApproachLength;
            position = GetEdgeRoamConnectionPoint(
                _edgeRoamStartPoint,
                _edgeRoamRouteStartPoint,
                startTangent: default,
                endTangent: _edgeRoamRouteTangent * _edgeRoamApproachLength,
                progress: progress);
            lookAhead = GetEdgeRoamConnectionPoint(
                _edgeRoamStartPoint,
                _edgeRoamRouteStartPoint,
                startTangent: default,
                endTangent: _edgeRoamRouteTangent * _edgeRoamApproachLength,
                progress: Math.Min(
                    1,
                    progress +
                    GetEdgeRoamConnectionLookAhead(_edgeRoamApproachLength)));
        }
        else if (distance < _edgeRoamApproachLength + _edgeRoamRouteLength)
        {
            var routeDistance = distance - _edgeRoamApproachLength;
            var directedDistance = _edgeRoamRouteStartDistance +
                                   routeDistance * _edgeRoamDirection;
            position = GetEdgeRoamRoutePoint(
                _edgeRoamRouteBounds,
                radius,
                directedDistance);
            lookAhead = GetEdgeRoamRoutePoint(
                _edgeRoamRouteBounds,
                radius,
                directedDistance + _edgeRoamDirection * 2);
        }
        else if (distance < totalDistance)
        {
            var returnDistance = distance -
                                 _edgeRoamApproachLength -
                                 _edgeRoamRouteLength;
            var progress = _edgeRoamReturnLength <= 0
                ? 1
                : returnDistance / _edgeRoamReturnLength;
            position = GetEdgeRoamConnectionPoint(
                _edgeRoamRouteStartPoint,
                _edgeRoamStartPoint,
                startTangent: _edgeRoamRouteTangent * _edgeRoamReturnLength,
                endTangent: default,
                progress: progress);
            lookAhead = GetEdgeRoamConnectionPoint(
                _edgeRoamRouteStartPoint,
                _edgeRoamStartPoint,
                startTangent: _edgeRoamRouteTangent * _edgeRoamReturnLength,
                endTangent: default,
                progress: Math.Min(
                    1,
                    progress +
                    GetEdgeRoamConnectionLookAhead(_edgeRoamReturnLength)));
        }
        else
        {
            position = _edgeRoamStartPoint;
            lookAhead = _edgeRoamStartPoint;
        }

        UpdateEdgeRoamFacing(position, lookAhead);
        ApplyEdgeRoamingPosition(position.X, position.Y);
        ShowStableFrame(GetEdgeRoamPose(elapsedSeconds));
    }

    private SpriteFrame GetEdgeRoamPose(double elapsedSeconds)
    {
        // Flight and wave were authored as independent loops. Switching at a
        // wall-clock interval jumped between unrelated silhouettes. Keep wave
        // available in the atlas for a future seam-authored bridge, but present
        // only the phase-stable flight loop at runtime.
        var absoluteFrame = ResolveEdgeRoamPoseStep(
            elapsedSeconds,
            _edgeRoamTravelPoseFramesPerSecond);
        var flightIndex = (int)(absoluteFrame % _roamFlightFrames.Length);
        return _roamFlightFrames[flightIndex];
    }

    private static long ResolveEdgeRoamPoseStep(
        double elapsedSeconds,
        double poseFramesPerSecond)
    {
        if (!double.IsFinite(elapsedSeconds) ||
            elapsedSeconds <= 0 ||
            !double.IsFinite(poseFramesPerSecond) ||
            poseFramesPerSecond <= 0)
        {
            return 0;
        }

        // VSync timestamps are integer QPC ticks. At nominal 60Hz that makes a
        // Floor(elapsed * 60) sampler alternate 1,0,2 as values land a few
        // microticks either side of an integer boundary. Sampling the nearest
        // absolute pose keeps 60Hz at a steady one-pose cadence without ever
        // replaying a backlog after a stalled render callback.
        return Math.Max(
            0L,
            (long)Math.Floor(
                elapsedSeconds * poseFramesPerSecond + 0.5));
    }

    private double AdvanceEdgeRoamClock(long timestamp)
    {
        var frameGap = timestamp - _edgeRoamLastRenderingTimestamp;
        if (frameGap > ToStopwatchTicks(EdgeRoamMaximumClockGap))
        {
            // Suspend/debugger/UI stalls are dead time. Moving the phase origin
            // drops the gap without replaying poses or accumulated distance.
            _edgeRoamStartedTimestamp = checked(
                _edgeRoamStartedTimestamp + frameGap);
        }

        _edgeRoamLastRenderingTimestamp = timestamp;
        var elapsedTicks = Math.Max(
            0,
            timestamp - _edgeRoamStartedTimestamp);
        return elapsedTicks / (double)Stopwatch.Frequency;
    }

    private bool AreRequiredEdgeRoamBoardingPagesResident()
    {
        var finalRequiredIndex = _edgeRoamBoardingReverse
            ? _edgeRoamBoardingStartIndex
            : _roamBoardingFrames.Length - 1;
        for (var index = 0; index <= finalRequiredIndex; index++)
        {
            if (!_residentSpritePages.ContainsKey(
                    _roamBoardingFrames[index].PageName))
            {
                return false;
            }
        }

        return true;
    }

    private void RequestNextMissingEdgeRoamBoardingPage()
    {
        var finalRequiredIndex = _edgeRoamBoardingReverse
            ? _edgeRoamBoardingStartIndex
            : _roamBoardingFrames.Length - 1;
        for (var index = 0; index <= finalRequiredIndex; index++)
        {
            var pageName = _roamBoardingFrames[index].PageName;
            if (!_residentSpritePages.ContainsKey(pageName))
            {
                RequestSpritePagePrefetch(pageName, urgent: true);
                return;
            }
        }
    }

    private bool AreAllSequencePagesResident(SpriteFrame[] frames)
    {
        foreach (var frame in frames)
        {
            if (!_residentSpritePages.ContainsKey(frame.PageName))
            {
                return false;
            }
        }

        return true;
    }

    private static int FindSpriteFrameIndex(
        SpriteFrame[] frames,
        SpriteFrame target)
    {
        for (var index = 0; index < frames.Length; index++)
        {
            if (frames[index] == target)
            {
                return index;
            }
        }

        return -1;
    }

    private void RequestNextMissingSequencePage(SpriteFrame[] frames)
    {
        foreach (var frame in frames)
        {
            if (!_residentSpritePages.ContainsKey(frame.PageName))
            {
                RequestSpritePagePrefetch(frame.PageName, urgent: true);
                return;
            }
        }
    }

    private void CancelEdgeRoamSpritePagePrefetch(bool includeBoarding)
    {
        var activeRoamDecode =
            IsEdgeRoamSpritePageName(
                _spritePagePrefetchPageName,
                includeBoarding);
        if (_pendingSpriteFrame is SpriteFrame pendingFrame &&
            IsEdgeRoamSpritePageName(
                pendingFrame.PageName,
                includeBoarding))
        {
            _pendingSpriteFrame = null;
            _pendingSpriteFrameBlendDuration = TimeSpan.Zero;
        }

        if (IsEdgeRoamSpritePageName(
                _renderDeferredSpritePageName,
                includeBoarding))
        {
            _renderDeferredSpritePageName = null;
            _renderDeferredSpritePageUrgent = false;
        }

        if (IsEdgeRoamSpritePageName(
                _renderDeferredSpritePageFailureName,
                includeBoarding))
        {
            _renderDeferredSpritePageFailureName = null;
            _renderDeferredSpritePageFailureReason = null;
        }

        if (IsEdgeRoamSpritePageName(
                _desiredSpritePageName,
                includeBoarding))
        {
            _desiredSpritePageName = null;
            _desiredSpritePageUrgent = false;
        }

        if (activeRoamDecode)
        {
            // A completion already queued to the UI thread must be stale even if
            // cancellation loses the race. It will discard its decoded buffer
            // instead of publishing an unused roam page after the idle trim.
            _spritePagePrefetchGeneration++;
            RequestSpritePagePrefetchCancellation();
        }

        if (IsEdgeRoamSpritePageName(
                _failedSpritePageName,
                includeBoarding))
        {
            _failedSpritePageName = null;
        }

        if (!HasDeferredSpritePageDispatchWork())
        {
            _spritePagePrefetchDispatchTimer.Stop();
        }
    }

    private bool IsEdgeRoamSpritePageName(
        string? pageName,
        bool includeBoarding) =>
        pageName is not null &&
        ((includeBoarding &&
          FrameSequenceUsesSpritePage(_roamBoardingFrames, pageName)) ||
         FrameSequenceUsesSpritePage(_roamFlightFrames, pageName) ||
         FrameSequenceUsesSpritePage(_roamWaveFrames, pageName));

    private static bool IsSequenceSpritePageName(
        SpriteFrame[] frames,
        string? pageName) =>
        pageName is not null &&
        FrameSequenceUsesSpritePage(frames, pageName);

    private void UpdateEdgeRoamFacing(Point position, Point lookAhead)
    {
        var orientation = ResolveEdgeRoamOrientation(
            position,
            lookAhead,
            _edgeRoamRouteBounds,
            _edgeRoamFacingScaleX,
            _edgeRoamRotationDegrees);
        _edgeRoamFacingScaleX = orientation.ScaleX;
        _edgeRoamRotationDegrees = orientation.RotationDegrees;

        if (Math.Abs(PetFacingScale.ScaleX - _edgeRoamFacingScaleX) > 0.0001)
        {
            PetFacingScale.ScaleX = _edgeRoamFacingScaleX;
        }
        if (Math.Abs(PetFacingScale.ScaleY - 1) > 0.0001)
        {
            PetFacingScale.ScaleY = 1;
        }
        if (Math.Abs(PetRoamRotate.Angle - _edgeRoamRotationDegrees) > 0.0001)
        {
            PetRoamRotate.Angle = _edgeRoamRotationDegrees;
        }
    }

    private static EdgeRoamOrientation ResolveEdgeRoamOrientation(
        Point position,
        Point lookAhead,
        Rect routeBounds,
        double currentScaleX,
        double currentRotationDegrees)
    {
        var deltaX = lookAhead.X - position.X;
        var deltaY = lookAhead.Y - position.Y;
        if (!double.IsFinite(deltaX) ||
            !double.IsFinite(deltaY) ||
            Math.Abs(deltaX) + Math.Abs(deltaY) <= 0.01)
        {
            return new EdgeRoamOrientation(
                currentScaleX < 0 ? -1 : 1,
                double.IsFinite(currentRotationDegrees)
                    ? currentRotationDegrees
                    : 0);
        }

        // Treat every monitor edge as the ground. Luban's authored up axis is
        // U=(0,-1), so rotate U onto the rounded rectangle's inward normal:
        // bottom=0, left=+90, top=+/-180 and right=-90. The corner normal
        // rotates continuously, avoiding quarter-turn snaps.
        var inwardNormal = GetEdgeRoamInwardNormal(
            position,
            routeBounds);
        var targetRotationDegrees =
            Math.Atan2(inwardNormal.X, -inwardNormal.Y) *
            180 /
            Math.PI;
        var rotationDegrees = UnwrapDegreesNear(
            targetRotationDegrees,
            currentRotationDegrees);

        // Of the two possible horizontal mirrors, choose the one whose
        // transformed authored heading has the larger dot product with the
        // current motion tangent. Ties retain the previous mirror so the
        // diagonal midpoint cannot chatter between signs.
        var radians = rotationDegrees * Math.PI / 180;
        var mirroredHeadingX = Math.Cos(radians);
        var mirroredHeadingY = Math.Sin(radians);
        var mirroredAlignment =
            mirroredHeadingX * deltaX +
            mirroredHeadingY * deltaY;
        var resolvedScaleX = Math.Abs(mirroredAlignment) <= 0.0001
            ? currentScaleX < 0 ? -1 : 1
            : mirroredAlignment > 0 ? -1 : 1;
        return new EdgeRoamOrientation(
            resolvedScaleX,
            rotationDegrees);
    }

    private static Vector GetEdgeRoamInwardNormal(
        Point position,
        Rect routeBounds)
    {
        var radius = GetEdgeRoamCornerRadius(routeBounds);
        var innerLeft = routeBounds.Left + radius;
        var innerRight = routeBounds.Right - radius;
        var innerTop = routeBounds.Top + radius;
        var innerBottom = routeBounds.Bottom - radius;
        var cornerCenter = new Point(
            Math.Clamp(position.X, innerLeft, innerRight),
            Math.Clamp(position.Y, innerTop, innerBottom));
        var inward = cornerCenter - position;
        if (inward.LengthSquared > 1e-9)
        {
            inward.Normalize();
            return inward;
        }

        var topDistance = Math.Abs(position.Y - routeBounds.Top);
        var rightDistance = Math.Abs(routeBounds.Right - position.X);
        var bottomDistance = Math.Abs(routeBounds.Bottom - position.Y);
        var leftDistance = Math.Abs(position.X - routeBounds.Left);
        var nearestDistance = Math.Min(
            Math.Min(topDistance, rightDistance),
            Math.Min(bottomDistance, leftDistance));
        if (nearestDistance == topDistance)
        {
            return new Vector(0, 1);
        }

        if (nearestDistance == rightDistance)
        {
            return new Vector(-1, 0);
        }

        return nearestDistance == bottomDistance
            ? new Vector(0, -1)
            : new Vector(1, 0);
    }

    private static double UnwrapDegreesNear(
        double targetDegrees,
        double referenceDegrees)
    {
        if (!double.IsFinite(targetDegrees))
        {
            return double.IsFinite(referenceDegrees)
                ? referenceDegrees
                : 0;
        }

        if (!double.IsFinite(referenceDegrees))
        {
            return targetDegrees;
        }

        var unwrapped = targetDegrees;
        while (unwrapped - referenceDegrees > 180)
        {
            unwrapped -= 360;
        }

        while (unwrapped - referenceDegrees < -180)
        {
            unwrapped += 360;
        }

        return unwrapped;
    }

    private static double ResolveEdgeRoamFacingScaleX(
        Point position,
        Point lookAhead,
        Rect routeBounds,
        double radius,
        double currentScaleX)
    {
        _ = radius;
        return ResolveEdgeRoamOrientation(
            position,
            lookAhead,
            routeBounds,
            currentScaleX,
            currentRotationDegrees: 0).ScaleX;
    }

    private static double ResolveEdgeRoamRotationDegrees(
        Point position,
        Point lookAhead,
        Rect routeBounds,
        double currentScaleX,
        double currentRotationDegrees) =>
        ResolveEdgeRoamOrientation(
            position,
            lookAhead,
            routeBounds,
            currentScaleX,
            currentRotationDegrees).RotationDegrees;

    private void ApplyEdgeRoamingPosition(
        double supportScreenX,
        double supportScreenY)
    {
        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        var displayedWidth = visiblePetBounds.Width;
        var displayedHeight = visiblePetBounds.Height;
        var supportOffset = GetEdgeRoamSupportOffset(
            displayedWidth,
            displayedHeight,
            _edgeRoamRotationDegrees);
        var desiredVisibleLeft = supportScreenX - supportOffset.X;
        var desiredVisibleTop = supportScreenY - supportOffset.Y;
        _edgeRoamLogicalLeft =
            Left + desiredVisibleLeft - visiblePetBounds.Left;
        _edgeRoamLogicalTop =
            Top + desiredVisibleTop - visiblePetBounds.Top;
        _isApplyingEdgeRoamPosition = true;
        try
        {
            MoveMainWindowTo(
                _edgeRoamLogicalLeft,
                _edgeRoamLogicalTop);
            _edgeRoamCurrentSupportPoint = new Point(
                supportScreenX,
                supportScreenY);
            _edgeRoamCurrentSupportPointValid = true;
        }
        finally
        {
            _isApplyingEdgeRoamPosition = false;
        }
    }

    private static Point GetEdgeRoamSupportOffset(
        double displayedWidth,
        double displayedHeight,
        double rotationDegrees)
    {
        var normalizedOriginX = 0.5;
        var normalizedOriginY = PetRoamRotationOriginYRatio;
        var supportDeltaY =
            EdgeRoamSupportAnchorYRatio - normalizedOriginY;
        var radians =
            (double.IsFinite(rotationDegrees) ? rotationDegrees : 0) *
            Math.PI /
            180;
        return new Point(
            normalizedOriginX * displayedWidth -
            supportDeltaY * displayedHeight * Math.Sin(radians),
            (normalizedOriginY +
             supportDeltaY * Math.Cos(radians)) *
            displayedHeight);
    }

    private static double GetPointDistance(Point first, Point second)
    {
        var deltaX = second.X - first.X;
        var deltaY = second.Y - first.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static Point GetEdgeRoamConnectionPoint(
        Point start,
        Point end,
        Vector startTangent,
        Vector endTangent,
        double progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        var t2 = t * t;
        var t3 = t2 * t;
        var startWeight = 2 * t3 - 3 * t2 + 1;
        var startTangentWeight = t3 - 2 * t2 + t;
        var endWeight = -2 * t3 + 3 * t2;
        var endTangentWeight = t3 - t2;
        return new Point(
            startWeight * start.X +
            startTangentWeight * startTangent.X +
            endWeight * end.X +
            endTangentWeight * endTangent.X,
            startWeight * start.Y +
            startTangentWeight * startTangent.Y +
            endWeight * end.Y +
            endTangentWeight * endTangent.Y);
    }

    private static double GetEdgeRoamConnectionLookAhead(double length) =>
        length > 0.01
            ? Math.Min(0.1, 2 / length)
            : 1;

    private static double GetEdgeRoamCornerRadius(Rect bounds) =>
        Math.Max(
            1,
            Math.Min(
                EdgeRoamCornerRadiusDips,
                Math.Min(bounds.Width, bounds.Height) / 2));

    private static double GetEdgeRoamRouteLength(Rect bounds, double radius)
    {
        var straightWidth = Math.Max(0, bounds.Width - radius * 2);
        var straightHeight = Math.Max(0, bounds.Height - radius * 2);
        return 2 * straightWidth +
               2 * straightHeight +
               2 * Math.PI * radius;
    }

    private static Vector GetEdgeRoamRouteTangent(
        Rect bounds,
        double radius,
        double distance,
        int direction)
    {
        var normalizedDirection = direction < 0 ? -1 : 1;
        var before = GetEdgeRoamRoutePoint(
            bounds,
            radius,
            distance - normalizedDirection);
        var after = GetEdgeRoamRoutePoint(
            bounds,
            radius,
            distance + normalizedDirection);
        var tangent = after - before;
        if (tangent.LengthSquared <= 1e-9)
        {
            return new Vector(normalizedDirection, 0);
        }

        tangent.Normalize();
        return tangent;
    }

    private static double FindClosestEdgeRoamRouteDistance(
        Rect bounds,
        double radius,
        Point point)
    {
        var routeLength = GetEdgeRoamRouteLength(bounds, radius);
        var bestDistance = 0d;
        var bestSquaredDistance = double.PositiveInfinity;
        for (var sample = 0; sample < EdgeRoamClosestPointSamples; sample++)
        {
            var distance = routeLength * sample / EdgeRoamClosestPointSamples;
            var candidate = GetEdgeRoamRoutePoint(bounds, radius, distance);
            var deltaX = candidate.X - point.X;
            var deltaY = candidate.Y - point.Y;
            var squaredDistance = deltaX * deltaX + deltaY * deltaY;
            if (squaredDistance < bestSquaredDistance)
            {
                bestSquaredDistance = squaredDistance;
                bestDistance = distance;
            }
        }

        return bestDistance;
    }

    private static Point GetEdgeRoamRoutePoint(
        Rect bounds,
        double radius,
        double distance)
    {
        var routeLength = GetEdgeRoamRouteLength(bounds, radius);
        var normalized = distance % routeLength;
        if (normalized < 0)
        {
            normalized += routeLength;
        }

        var horizontal = Math.Max(0, bounds.Width - radius * 2);
        var vertical = Math.Max(0, bounds.Height - radius * 2);
        var arc = Math.PI * radius / 2;
        var left = bounds.Left;
        var top = bounds.Top;
        var right = bounds.Right;
        var bottom = bounds.Bottom;

        if (normalized < horizontal)
        {
            return new Point(left + radius + normalized, top);
        }

        normalized -= horizontal;
        if (normalized < arc)
        {
            var angle = -Math.PI / 2 + normalized / radius;
            return new Point(
                right - radius + Math.Cos(angle) * radius,
                top + radius + Math.Sin(angle) * radius);
        }

        normalized -= arc;
        if (normalized < vertical)
        {
            return new Point(right, top + radius + normalized);
        }

        normalized -= vertical;
        if (normalized < arc)
        {
            var angle = normalized / radius;
            return new Point(
                right - radius + Math.Cos(angle) * radius,
                bottom - radius + Math.Sin(angle) * radius);
        }

        normalized -= arc;
        if (normalized < horizontal)
        {
            return new Point(right - radius - normalized, bottom);
        }

        normalized -= horizontal;
        if (normalized < arc)
        {
            var angle = Math.PI / 2 + normalized / radius;
            return new Point(
                left + radius + Math.Cos(angle) * radius,
                bottom - radius + Math.Sin(angle) * radius);
        }

        normalized -= arc;
        if (normalized < vertical)
        {
            return new Point(left, bottom - radius - normalized);
        }

        normalized -= vertical;
        var finalAngle = Math.PI + normalized / radius;
        return new Point(
            left + radius + Math.Cos(finalAngle) * radius,
            top + radius + Math.Sin(finalAngle) * radius);
    }

    private void AutomaticTimer_Tick(object? sender, EventArgs e)
    {
        _automaticTimer.Stop();
        if (_isClosing || _sessionInactive ||
            _isReminderActive || !_automaticAnimationEnabled ||
            _workState != WorkState.Idle)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        StartEdgeRoamPreloadIfDue(timestamp);
        if (IsEdgeRoamDue(timestamp))
        {
            if (_edgeRoamPreloadRequested &&
                !AreAllEdgeRoamPreloadPagesResident())
            {
                ContinueEdgeRoamPreload();
                // Page completions continue the preload chain immediately.
                // This coarse one-shot is only a watchdog for an unexpected
                // lost callback, not a 60 Hz UI-thread polling loop.
                _automaticTimer.Interval = EdgeRoamPreloadWatchdogInterval;
                _automaticTimer.Start();
                return;
            }

            StopPillowBreathing();
            if (StartEdgeRoaming())
            {
                return;
            }

            // A click animation or another short-lived visual state can overlap
            // the due instant. Retry soon instead of silently postponing the
            // whole ten-minute cycle.
            ScheduleNextEdgeRoam(timestamp, EdgeRoamBusyRetryDelay);
        }
        else if (_edgeRoamPreloadRequested)
        {
            StopPillowBreathing();
            ArmAutomaticWakeTimer(timestamp);
            return;
        }

        if (_isPillowBreathing)
        {
            if (_pillowBreathingDueTimestamp > timestamp)
            {
                ArmAutomaticWakeTimer(timestamp);
                return;
            }

            StopPillowBreathing();
            RestartAutomaticCountdown();
            return;
        }

        if (_activeClip is not null || _dragInteractionActive ||
            _workState != WorkState.Idle ||
            _bubbleMode == BubbleMode.Todo || _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            return;
        }

        if (_nextAutomaticActivityDueTimestamp <= 0 ||
            timestamp < _nextAutomaticActivityDueTimestamp)
        {
            ArmAutomaticWakeTimer(timestamp);
            return;
        }

        _nextAutomaticActivityDueTimestamp = 0;
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
        var timestamp = Stopwatch.GetTimestamp();
        _pillowBreathingDueTimestamp = 0;
        _nextAutomaticActivityDueTimestamp = checked(
            timestamp + ToStopwatchTicks(AutomaticAnimationInterval));
        if (_isClosing || _sessionInactive || !_automaticAnimationEnabled ||
            _isReminderActive ||
            _workState != WorkState.Idle ||
            _activeClip is not null || _isPillowBreathing || _dragInteractionActive ||
            _bubbleMode == BubbleMode.Todo || _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            return;
        }

        ArmAutomaticWakeTimer(timestamp);
    }

    private void ArmAutomaticWakeTimer(long timestamp)
    {
        if (_isClosing || _sessionInactive || !_automaticAnimationEnabled ||
            _isReminderActive ||
            _workState != WorkState.Idle ||
            _activeClip is not null || _dragInteractionActive ||
            _bubbleMode == BubbleMode.Todo || _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            _automaticTimer.Stop();
            return;
        }

        var nextDueTimestamp = long.MaxValue;
        if (_nextAutomaticActivityDueTimestamp > 0)
        {
            nextDueTimestamp = Math.Min(
                nextDueTimestamp,
                _nextAutomaticActivityDueTimestamp);
        }
        if (_pillowBreathingDueTimestamp > 0)
        {
            nextDueTimestamp = Math.Min(
                nextDueTimestamp,
                _pillowBreathingDueTimestamp);
        }
        if (_edgeRoamingEnabled && _nextEdgeRoamDueTimestamp > 0)
        {
            if (!_edgeRoamPreloadRequested)
            {
                nextDueTimestamp = Math.Min(
                    nextDueTimestamp,
                    _nextEdgeRoamDueTimestamp -
                    ToStopwatchTicks(EdgeRoamPreloadLeadTime));
            }

            nextDueTimestamp = Math.Min(
                nextDueTimestamp,
                _nextEdgeRoamDueTimestamp);
        }

        if (nextDueTimestamp == long.MaxValue)
        {
            _automaticTimer.Stop();
            return;
        }

        var remainingTicks = nextDueTimestamp - timestamp;
        var interval = remainingTicks <= 0
            ? TimeSpan.FromMilliseconds(1)
            : TimeSpan.FromSeconds(
                remainingTicks / (double)Stopwatch.Frequency);
        if (interval > TimeSpan.FromDays(1))
        {
            interval = TimeSpan.FromDays(1);
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

    private void StartPillowBreathing()
    {
        StopPillowBreathing();
        _isPillowBreathing = true;
        var timestamp = Stopwatch.GetTimestamp();
        _nextAutomaticActivityDueTimestamp = 0;
        _pillowBreathingDueTimestamp = checked(
            timestamp + ToStopwatchTicks(PillowAnimationDuration));
        // This timer reserves one explicit five-second rest slot in the
        // shuffled activity bag. The independent snore bubble is driven by the
        // absolute composition clock throughout every stable idle period.
        ArmAutomaticWakeTimer(timestamp);
        RefreshSnoreBubbleAnimationState();
    }

    private void StopPillowBreathing()
    {
        _isPillowBreathing = false;
        _pillowBreathingDueTimestamp = 0;
        _automaticTimer.Stop();
        RefreshSnoreBubbleAnimationState();
    }

    private static AnimationClock CreateSnoreBubbleScaleClock()
    {
        var easing = new SineEase
        {
            EasingMode = EasingMode.EaseInOut
        };
        easing.Freeze();

        var animation = new DoubleAnimation
        {
            From = SnoreBubbleMinimumScale,
            To = SnoreBubbleMaximumScale,
            Duration = new Duration(TimeSpan.FromTicks(
                SnoreBubbleCycleDuration.Ticks / 2)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = easing
        };
        animation.Freeze();
        return (AnimationClock)animation.CreateClock(true);
    }

    private void RefreshSnoreBubbleAnimationState()
    {
        var isTodoExitIdleEndpoint =
            ReferenceEquals(_activeClip, _todoExitClip) &&
            _activeFrameIndex == _todoExitClip.Frames.Length - 1;
        var shouldAnimate = IsLoaded &&
                            !_isClosing &&
                            !_sessionInactive &&
                            _workState == WorkState.Idle &&
                            _currentSpriteFrame is SpriteFrame currentFrame &&
                            currentFrame == _idleFrame &&
                            (_activeClip is null ||
                             isTodoExitIdleEndpoint) &&
                            !_isReminderActive &&
                            !_isEdgeRoaming &&
                            !_dragInteractionActive &&
                            !_pointerDown &&
                            !_dragStarted &&
                            _edgeDock == EdgeDock.None;
        if (shouldAnimate == _isSnoreBubbleAnimating)
        {
            return;
        }

        _isSnoreBubbleAnimating = shouldAnimate;
        SnoreBubbleHost.Opacity = shouldAnimate ? 1 : 0;
        _snoreBubbleScaleClock.Controller?.Stop();
        SnoreBubbleScale.ApplyAnimationClock(
            ScaleTransform.ScaleXProperty,
            null);
        SnoreBubbleScale.ApplyAnimationClock(
            ScaleTransform.ScaleYProperty,
            null);
        SnoreBubbleScale.ScaleX = SnoreBubbleMinimumScale;
        SnoreBubbleScale.ScaleY = SnoreBubbleMinimumScale;
        if (shouldAnimate)
        {
            SnoreBubbleScale.ApplyAnimationClock(
                ScaleTransform.ScaleXProperty,
                _snoreBubbleScaleClock,
                HandoffBehavior.SnapshotAndReplace);
            SnoreBubbleScale.ApplyAnimationClock(
                ScaleTransform.ScaleYProperty,
                _snoreBubbleScaleClock,
                HandoffBehavior.SnapshotAndReplace);
            _snoreBubbleScaleClock.Controller?.Begin();
        }

        // The tiny bubble now runs on WPF's composition clock. It no longer
        // keeps the application's managed Rendering callback alive while the
        // character and window are otherwise completely idle.
        UpdateVisualClockSubscription();
    }

    private static double SmoothStep(double value) =>
        value * value * (3 - 2 * value);

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
                CompleteActiveClipAt(clip, timestamp);
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

        if (IsWorkTypingLoopClip(clip))
        {
            // A serious loop can begin at any neutral micro-seam, including
            // frame 094 on the final atlas page. Use the cyclic look-ahead as
            // soon as that first pose is actually visible so frame 096 -> 001
            // never waits for a cold first page.
            PrefetchNextWorkLoopPage(
                ReferenceEquals(clip, _workSeriousLoopClip)
                    ? _workSeriousLoopFrames
                    : _workLoopFrames,
                displayedFrameIndex);
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
        else if (ReferenceEquals(clip, _workEnterClip))
        {
            var loopPageName = _workLoopFrames[0].PageName;
            if (!IsSpritePageImmediatelyAvailable(loopPageName))
            {
                RequestSpritePagePrefetch(loopPageName, urgent: true);
            }
        }
        else if (ReferenceEquals(clip, _workSeriousEnterClip))
        {
            if (_workExitRequested)
            {
                var exitPageName = _workExitClip.Frames[0].Image.PageName;
                if (!IsSpritePageImmediatelyAvailable(exitPageName))
                {
                    RequestSpritePagePrefetch(exitPageName, urgent: true);
                }
                return;
            }

            var framePosition = double.IsFinite(
                    _workSeriousEnterTargetFramePosition)
                ? _workSeriousEnterTargetFramePosition
                : 0;
            var frameIndex = (int)(
                Math.Floor(framePosition) % WorkSeriousLoopFrameCount);
            if (frameIndex < 0)
            {
                frameIndex += WorkSeriousLoopFrameCount;
            }

            var loopPageName = _workSeriousLoopFrames[frameIndex].PageName;
            if (!IsSpritePageImmediatelyAvailable(loopPageName))
            {
                RequestSpritePagePrefetch(loopPageName, urgent: true);
            }
        }
        else if (ReferenceEquals(clip, _workSeriousExitClip))
        {
            var continuationPageName = _workExitRequested
                ? _workExitClip.Frames[0].Image.PageName
                : _workLoopFrames[0].PageName;
            if (!IsSpritePageImmediatelyAvailable(continuationPageName))
            {
                RequestSpritePagePrefetch(continuationPageName, urgent: true);
            }
        }
    }

    private void CompleteActiveClip(AnimationClip clip)
    {
        CompleteActiveClipAt(clip, Stopwatch.GetTimestamp());
    }

    private void CompleteActiveClipAt(AnimationClip clip, long timestamp)
    {
        ShowStableFrame(clip.Frames[^1].Image);
        if (!ReferenceEquals(_activeClip, clip))
        {
            return;
        }

        if (ReferenceEquals(clip, _workEnterClip))
        {
            if (_workExitRequested)
            {
                StartWorkExitClip();
            }
            else
            {
                StartWorkLoopAt(timestamp);
            }
            return;
        }

        if (ReferenceEquals(clip, _workSeriousEnterClip))
        {
            if (_workExitRequested)
            {
                // Finish the short brow transition, then play its authored
                // forward relaxation before leaving work mode. This prevents
                // one-frame face swaps when right-click lands mid-transition.
                StartWorkSeriousExitClip();
            }
            else
            {
                StartSeriousWorkLoopAt(timestamp);
            }
            return;
        }

        if (ReferenceEquals(clip, _workSeriousExitClip))
        {
            if (_workExitRequested)
            {
                StartWorkExitClip();
            }
            else
            {
                StartWorkLoopAt(timestamp);
            }
            return;
        }

        if (ReferenceEquals(clip, _workExitClip))
        {
            FinishWorkExit();
            return;
        }

        if (ReferenceEquals(clip, _workLoopClip))
        {
            StartWorkLoopAt(timestamp);
            return;
        }

        if (ReferenceEquals(clip, _reminderEnterClip) && _isReminderActive)
        {
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
            RefreshSnoreBubbleAnimationState();
            RequestIdleSpritePageTrim();
            UpdateVisualClockSubscription();
            return;
        }

        _activeClip = null;
        _activeFrameIndex = -1;
        _activeClipStartedTimestamp = 0;
        _activeFrameDeadlineTimestamp = 0;
        ClearDeferredActiveClipClock();
        RefreshSnoreBubbleAnimationState();
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
        ScheduleUnpinnedPetSizePreviewCommit();
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
        if (IsWorkTypingLoopClip(_activeClip))
        {
            _activeClipStartedTimestamp = timestamp;
            _workLoopAnchorTimestamp = timestamp;
            _activeFrameDeadlineTimestamp = long.MaxValue;
            return;
        }
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
            RefreshSnoreBubbleAnimationState();
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
            // Capture the reuse decision before StopFrameBlend clears its state:
            // a blend target is not necessarily the pixels currently on screen.
            var canReuseDisplayedSpritePixels =
                CanReuseDisplayedSpritePixels(frame, requestedBlendDuration);
            StopFrameBlend(snapToTarget: false);
            if (!canReuseDisplayedSpritePixels)
            {
                WriteDirectSpriteFrame(frame);
            }
        }

        // Visibility would invalidate layout from inside Rendering exactly when
        // the first edge pose is published. Opacity is render-only, and changing
        // it here keeps the character pixels and pillow state atomic without a
        // one-frame flash or a layout pass on the animation clock.
        var pillowOpacity = IsEdgeSpriteFrame(frame) ||
                            IsWorkPillowHiddenFrame(frame)
            ? 0d
            : 1d;
        if (PillowImage.Opacity != pillowOpacity)
        {
            PillowImage.Opacity = pillowOpacity;
        }

        _currentSpriteFrame = frame;
        _spriteFrameDescriptorPublishedForTesting?.Invoke(
            frame.PageName,
            frame.Name,
            frame.Width,
            frame.Height,
            frame.DestinationX,
            frame.DestinationY);
        RefreshSnoreBubbleAnimationState();
    }

    private static bool IsEdgeSpriteFrame(SpriteFrame frame) =>
        frame.Name.StartsWith("Assets/luban-edge-", StringComparison.Ordinal) ||
        frame.Name.StartsWith("Assets/luban-roam-", StringComparison.Ordinal);

    private static bool IsWorkPillowHiddenFrame(SpriteFrame frame)
    {
        const string enterPrefix = "Assets/luban-work-enter-";
        if (frame.Name.StartsWith(
                "Assets/luban-work-loop-",
                StringComparison.Ordinal) ||
            frame.Name.StartsWith(
                "Assets/luban-work-serious-loop-",
                StringComparison.Ordinal) ||
            frame.Name.StartsWith(
                "Assets/luban-work-serious-exit-",
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!frame.Name.StartsWith(enterPrefix, StringComparison.Ordinal) ||
            frame.Name.Length < enterPrefix.Length + 3)
        {
            return false;
        }

        var first = frame.Name[enterPrefix.Length];
        var second = frame.Name[enterPrefix.Length + 1];
        var third = frame.Name[enterPrefix.Length + 2];
        if (first is < '0' or > '9' ||
            second is < '0' or > '9' ||
            third is < '0' or > '9')
        {
            return false;
        }

        var frameNumber = (first - '0') * 100 +
                          (second - '0') * 10 +
                          third - '0';
        return frameNumber > WorkEnterPillowVisibleFrameCount;
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
            if (!HasDeferredSpritePageDispatchWork())
            {
                _spritePagePrefetchDispatchTimer.Stop();
            }
        }

        // A frame that can be displayed from the current page is newer than an
        // outstanding cold-page request. Invalidate that demand as well as the
        // pending pose so a completion already queued behind a drag cannot
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
            ClearPixelBoundsDifference(
                _displayFramePixels,
                previousBounds,
                nextBounds);
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

    private bool CanReuseDisplayedSpritePixels(
        SpriteFrame frame,
        TimeSpan requestedBlendDuration) =>
        _workState == WorkState.Typing &&
        IsWorkTypingLoopClip(_activeClip) &&
        requestedBlendDuration == TimeSpan.Zero &&
        !_isFrameBlending &&
        _currentSpriteFrame is SpriteFrame displayedFrame &&
        _directDisplayFrameBounds is { } displayedBounds &&
        displayedBounds == GetVisibleFrameBounds(displayedFrame) &&
        ReferencesSameSpritePixels(displayedFrame, frame);

    private static bool ReferencesSameSpritePixels(
        SpriteFrame first,
        SpriteFrame second) =>
        string.Equals(first.PageName, second.PageName, StringComparison.Ordinal) &&
        first.X == second.X &&
        first.Y == second.Y &&
        first.Width == second.Width &&
        first.Height == second.Height &&
        first.DestinationX == second.DestinationX &&
        first.DestinationY == second.DestinationY;

    private static void ClearPixelBoundsDifference(
        byte[] pixels,
        Int32Rect previousBounds,
        Int32Rect nextBounds)
    {
        if (previousBounds.Width <= 0 || previousBounds.Height <= 0)
        {
            return;
        }

        var previousRight = checked(
            previousBounds.X + previousBounds.Width);
        var previousBottom = checked(
            previousBounds.Y + previousBounds.Height);
        var nextRight = checked(nextBounds.X + nextBounds.Width);
        var nextBottom = checked(nextBounds.Y + nextBounds.Height);
        var intersectionLeft = Math.Max(
            previousBounds.X,
            nextBounds.X);
        var intersectionTop = Math.Max(
            previousBounds.Y,
            nextBounds.Y);
        var intersectionRight = Math.Min(previousRight, nextRight);
        var intersectionBottom = Math.Min(previousBottom, nextBottom);
        if (intersectionRight <= intersectionLeft ||
            intersectionBottom <= intersectionTop)
        {
            ClearPixelBounds(pixels, previousBounds);
            return;
        }

        ClearPixelBounds(
            pixels,
            new Int32Rect(
                previousBounds.X,
                previousBounds.Y,
                previousBounds.Width,
                intersectionTop - previousBounds.Y));
        ClearPixelBounds(
            pixels,
            new Int32Rect(
                previousBounds.X,
                intersectionBottom,
                previousBounds.Width,
                previousBottom - intersectionBottom));
        ClearPixelBounds(
            pixels,
            new Int32Rect(
                previousBounds.X,
                intersectionTop,
                intersectionLeft - previousBounds.X,
                intersectionBottom - intersectionTop));
        ClearPixelBounds(
            pixels,
            new Int32Rect(
                intersectionRight,
                intersectionTop,
                previousRight - intersectionRight,
                intersectionBottom - intersectionTop));
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
                _synchronizeEdgePeekToRenderingCadence =
                    ShouldSynchronizeEdgePeekToRenderingCadence(
                        presentationInterval);
            }
        }

        _isInsideVisualRenderingCallback = true;
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var workEdgeHandoffPending =
                _workEdgeDock != EdgeDock.None &&
                _workState != WorkState.Idle;
            if (!workEdgeHandoffPending)
            {
                TryShowPendingSpriteFrameAt(timestamp);
            }
            AdvancePetSizeCompositionFrame(timestamp);
            PrefetchPendingWorkTransitionPages();

            if (workEdgeHandoffPending)
            {
                _ = TryCompletePendingWorkEdgeHandoff();
            }
            else if (_activeClip is not null)
            {
                if (IsWorkTypingLoopClip(_activeClip))
                {
                    AdvanceWorkLoop(timestamp);
                }
                else
                {
                    AdvanceActiveClip(timestamp);
                }
            }

            AdvanceWorkModeIconTransition(timestamp);

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
        finally
        {
            _isInsideVisualRenderingCallback = false;
            _synchronizeActiveClipToRenderingCadence = false;
            _synchronizeEdgePeekToRenderingCadence = false;
        }
    }

    private static bool ShouldSynchronizeActiveClipToRenderingCadence(
        TimeSpan presentationInterval) =>
        // One-pose-per-composition locking is valid only for the authored 1x
        // 60fps timing. Faster or slower code-configured playback must use the
        // absolute clock so it can skip or hold poses without changing duration.
        Math.Abs(AnimationPlaybackSpeed - 1d) <= 0.0001 &&
        ShouldSynchronizeEdgePeekToRenderingCadence(presentationInterval);

    private static bool ShouldSynchronizeEdgePeekToRenderingCadence(
        TimeSpan presentationInterval) =>
        presentationInterval >= MinimumNearSixtyHzPresentationInterval &&
        presentationInterval <= MaximumNearSixtyHzPresentationInterval;

    private void UpdateVisualClockSubscription()
    {
        RefreshWorkModeButton();
        var shouldRun = !_isClosing &&
                          (_isPetSizeAdjustmentActive ||
                           _petSizeTargetUpdatePending ||
                           _isPetSizeTransitioning ||
                           _workModeIconTransitionActive ||
                           _activeClip is not null ||
                           (_workEdgeDock != EdgeDock.None &&
                            _workState != WorkState.Idle) ||
                           (_edgeDock != EdgeDock.None &&
                            !_edgePeekHoldTimer.IsEnabled) ||
                           _isEdgeRoaming ||
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
        var transformedPixels = SpriteDecodeScratchPool.Rent(
            DisplayFrameByteCount);
        try
        {
            TransformPremultipliedPixels(
                _displayFramePixels,
                transformedPixels,
                DisplayPixelWidth,
                DisplayPixelHeight,
                relativeMatrix);
            WriteDisplayFrame(transformedPixels);
        }
        finally
        {
            SpriteDecodeScratchPool.Return(
                transformedPixels,
                clearArray: false);
        }
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

        var transformedPixels = SpriteDecodeScratchPool.Rent(
            DisplayFrameByteCount);
        try
        {
            if (visualMatrix.IsIdentity)
            {
                Array.Copy(
                    _displayFramePixels,
                    transformedPixels,
                    _displayFramePixels.Length);
            }
            else
            {
                TransformPremultipliedPixels(
                    _displayFramePixels,
                    transformedPixels,
                    DisplayPixelWidth,
                    DisplayPixelHeight,
                    visualMatrix);
            }

            if (opacity < 1)
            {
                for (var index = 0; index < DisplayFrameByteCount; index++)
                {
                    transformedPixels[index] = (byte)Math.Clamp(
                        Math.Round(transformedPixels[index] * opacity),
                        byte.MinValue,
                        byte.MaxValue);
                }
            }

            WriteDisplayFrame(transformedPixels);
        }
        finally
        {
            SpriteDecodeScratchPool.Return(
                transformedPixels,
                clearArray: false);
        }
    }

    private static void TransformPremultipliedPixels(
        byte[] sourcePixels,
        byte[] outputPixels,
        int width,
        int height,
        Matrix visualMatrix)
    {
        var expectedLength = checked(width * height * 4);
        if (sourcePixels.Length != expectedLength ||
            outputPixels.Length < expectedLength)
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
        if (TryTransformExactHorizontalMirror(
                sourcePixels,
                outputPixels,
                width,
                height,
                inverse))
        {
            return;
        }

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

        Array.Clear(outputPixels, 0, expectedLength);
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

    private static bool TryTransformExactHorizontalMirror(
        byte[] sourcePixels,
        byte[] outputPixels,
        int width,
        int height,
        Matrix inverse)
    {
        // The decoded atlas/display pipeline supplies valid Pbgra32 pixels
        // (B/G/R <= A), enforced by the production atlas QA. Under that
        // precondition, zero-weight bilinear samples are byte-for-byte copies.
        // Admit only the exact, complete-frame horizontal mirror produced by
        // the right-edge facing transform. Every other matrix keeps the
        // established axis/general sampling and rounding path below.
        if (width <= 0 ||
            height <= 0 ||
            ReferenceEquals(sourcePixels, outputPixels) ||
            inverse.M11 != -1d ||
            inverse.M12 != 0d ||
            inverse.M21 != 0d ||
            inverse.M22 != 1d ||
            inverse.OffsetX != width ||
            inverse.OffsetY != 0d)
        {
            return false;
        }

        var expectedLength = checked(width * height * 4);
        var sourceWords = MemoryMarshal.Cast<byte, uint>(sourcePixels);
        var outputWords = MemoryMarshal.Cast<byte, uint>(
            outputPixels.AsSpan(0, expectedLength));
        for (var destinationY = 0; destinationY < height; destinationY++)
        {
            var sourceRow = sourceWords.Slice(
                destinationY * width,
                width);
            var outputRow = outputWords.Slice(destinationY * width, width);
            for (var destinationX = 0; destinationX < width; destinationX++)
            {
                outputRow[destinationX] = sourceRow[
                    width - 1 - destinationX];
            }
        }

        return true;
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
        Array.Clear(outputPixels, 0, checked(width * height * 4));
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
                _residentSpritePageTrimPending = false;
                TrimResidentSpritePagesToBudget();
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
        if (_isClosing)
        {
            return;
        }

        if (_residentSpritePages.ContainsKey(pageName))
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

        // Only an accepted decode request extends the idle-cache grace period.
        // Rendering publishes a deferred signal above and returns; its dispatcher
        // handoff comes back through this method outside composition and performs
        // this deferral exactly once. Resident hits, duplicate demand and rejected
        // non-urgent work therefore cannot keep the atlas high-water set alive.
        DeferIdleSpritePageTrim();
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

        PrepareSpritePageBufferForIncomingPage(pageName, page);
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
                        ReleaseCompletedSpritePagePrefetchForShutdown(
                            cancellation,
                            completedTask);
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
                    ReleaseCompletedSpritePagePrefetchForShutdown(
                        cancellation,
                        completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PrepareSpritePageBufferForIncomingPage(
        string pageName,
        SpriteAtlasPage page)
    {
        var budgetBytes = GetSpritePageResidentBudgetBytes();
        TrimSpritePageBufferPoolToTarget(budgetBytes);

        // Decode used to allocate first and evict after publication, so the
        // buffer that was about to become free could never satisfy this Rent.
        // Pre-evict only unprotected LRU pages that the post-publication trim
        // would remove anyway. Current, pending, desired, pinned and roaming
        // pages remain protected by IsSpritePageProtected.
        var incomingCapacity = SpritePageBufferPool.GetCapacity(
            page.UncompressedByteCount);
        var residentTargetBytes = Math.Max(
            0L,
            budgetBytes - incomingCapacity);
        TrimResidentSpritePagesToTarget(
            residentTargetBytes,
            preservePageName: pageName);
    }

    private void ReleaseCompletedSpritePagePrefetchForShutdown(
        CancellationTokenSource cancellation,
        Task<SpritePageLoadResult> completedTask)
    {
        ReleaseCompletedSpritePageResult(completedTask);
        if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _spritePagePrefetchTask,
                    null,
                    completedTask),
                completedTask))
        {
            _ = Interlocked.CompareExchange(
                ref _spritePagePrefetchCancellation,
                null,
                cancellation);
            _spritePagePrefetchPageName = null;
        }

        cancellation.Dispose();
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
            ReleaseCompletedSpritePageResult(completedTask);
            return;
        }

        if (completedTask.IsCanceled)
        {
            _ = completedTask.Exception;
            StartSpritePagePrefetch();
            TrimResidentSpritePagesToBudget();
            if (_spritePagePrefetchTask is null &&
                _desiredSpritePageName is null)
            {
                ContinueEdgeRoamPreload();
            }
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
                ReturnSpritePageBuffer(completedTask.Result.Pixels);
            }
            StartSpritePagePrefetch();
            TrimResidentSpritePagesToBudget();
            if (_spritePagePrefetchTask is null &&
                _desiredSpritePageName is null)
            {
                ContinueEdgeRoamPreload();
            }
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
        ContinueEdgeRoamPreload();
        if (_desiredSpritePageName is not null)
        {
            StartSpritePagePrefetch();
        }
        else
        {
            ResumeSpritePageWarmup();
        }
    }

    private void ReleaseCompletedSpritePageResult(
        Task<SpritePageLoadResult> completedTask)
    {
        _ = completedTask.Exception;
        if (completedTask.IsCompletedSuccessfully)
        {
            ReturnSpritePageBuffer(completedTask.Result.Pixels);
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
        var edgeRoamPreloadFailed =
            _edgeRoamPreloadRequested &&
            IsEdgeRoamSpritePageName(pageName, includeBoarding: true);
        if (edgeRoamPreloadFailed)
        {
            _edgeRoamPreloadRequested = false;
            ScheduleNextEdgeRoam(
                Stopwatch.GetTimestamp(),
                EdgeRoamInterval);
        }
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
        if (edgeRoamPreloadFailed)
        {
            RestartAutomaticCountdown();
        }

        UpdateVisualClockSubscription();
        if (_spritePageWarmupIndex < _spritePageWarmupOrder.Length &&
            string.Equals(
                _spritePageWarmupOrder[_spritePageWarmupIndex],
                pageName,
                StringComparison.Ordinal))
        {
            _spritePageWarmupIndex++;
        }

        TrimResidentSpritePagesToBudget();
        ResumeSpritePageWarmup();
    }

    private bool StopAnimatedStateForFailedSpritePage(string pageName)
    {
        var failedCurrentEdge = false;
        var failedWorkPage =
            _workState != WorkState.Idle &&
            (FrameSequenceUsesSpritePage(_workEnterFrames, pageName) ||
             FrameSequenceUsesSpritePage(_workLoopFrames, pageName) ||
             FrameSequenceUsesSpritePage(_workSeriousLoopFrames, pageName) ||
             FrameSequenceUsesSpritePage(_workSeriousExitFrames, pageName));
        var failedRoamingPage =
            _isEdgeRoaming &&
            (FrameSequenceUsesSpritePage(_roamBoardingFrames, pageName) ||
             FrameSequenceUsesSpritePage(_roamFlightFrames, pageName) ||
             FrameSequenceUsesSpritePage(_roamWaveFrames, pageName));
        var failedPendingWorkEdge =
            _workEdgeDock != EdgeDock.None &&
            FrameSequenceUsesSpritePage(GetEdgeFrames(_workEdgeDock), pageName);
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

        if (!failedCurrentEdge && !failedRoamingPage && !failedWorkPage &&
            !failedPendingWorkEdge)
        {
            return false;
        }

        if (failedPendingWorkEdge)
        {
            // The direct work-to-edge handoff deliberately freezes its current
            // descriptor. A corrupt target must terminate that frozen state
            // explicitly instead of resuming a stale absolute clock or waiting
            // forever for a page that can never become resident.
            StopWorkModeImmediately(restoreIdleFrame: true);
            ScheduleNextEdgeRoam(
                Stopwatch.GetTimestamp(),
                EdgeRoamInterval);
            RestartAutomaticCountdown();
            return true;
        }

        if (failedWorkPage)
        {
            StopWorkModeImmediately(restoreIdleFrame: true);
            ScheduleNextEdgeRoam(
                Stopwatch.GetTimestamp(),
                EdgeRoamInterval);
            RestartAutomaticCountdown();
            return true;
        }

        if (failedCurrentEdge)
        {
            // The old decoded pixels are still valid. Bake the currently
            // visible edge transform once before clearing it.
            BakeCurrentPetVisualTransformIntoDisplayFrame();
            ExitEdgePeek(
                restartAutomaticCountdown: false,
                restoreIdleFrame: false);
        }
        if (failedRoamingPage)
        {
            // A failed boarding page cannot play a reverse exit. Stop
            // immediately so the normal fallback path cannot bake an already
            // transformed roam frame a second time.
            StopEdgeRoaming(
                scheduleNext: true,
                restoreIdleFrame: true,
                interrupted: true,
                immediate: true);
        }

        if (!failedRoamingPage)
        {
            ResetPetVisualTransforms();
        }
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
        _upcomingReminderPreloadPageName = null;
        _pendingSpriteFrame = null;
        _pendingSpriteFrameBlendDuration = TimeSpan.Zero;
        _spritePagePrefetchDispatchTimer.Stop();
        StopIdleSpritePageTrim();
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
        var decodedPixels =
            _spritePageBufferPool.Rent(page.UncompressedByteCount);
        try
        {
            var stride = checked(page.Width * 4);
            var readElapsed = TimeSpan.Zero;
            if (!page.IsContentHashValidated)
            {
                var readStartedAt = Stopwatch.GetTimestamp();
                var validationResource = Application.GetResourceStream(
                    page.ResourceUri)
                    ?? throw new InvalidOperationException(
                        $"Missing sprite atlas page resource: {page.ResourcePath}");
                using (validationResource.Stream)
                {
                    ValidateSpriteAtlasPageContentHashCore(
                        validationResource.Stream,
                        page.CompressedByteCount,
                        page.ResourcePath,
                        page.ContentSha256Bytes,
                        cancellationToken);
                }

                page.MarkContentHashValidated();
                readElapsed = Stopwatch.GetElapsedTime(readStartedAt);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var decodeResource = Application.GetResourceStream(
                page.ResourceUri)
                ?? throw new InvalidOperationException(
                    $"Missing sprite atlas page resource: {page.ResourcePath}");
            using (decodeResource.Stream)
            using (var brotliStream = new BrotliStream(
                       decodeResource.Stream,
                       CompressionMode.Decompress,
                       leaveOpen: false))
            {
                DecodeSpritePageStreamCore(
                    page.ResourcePath,
                    page.Encoding,
                    brotliStream,
                    page.PayloadByteCount,
                    decodedPixels,
                    page.Width,
                    page.Height,
                    page.FrameDescriptorValues,
                    page.DecodedSha256Bytes,
                    page.UniqueSpriteCount,
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
        catch
        {
            ReturnSpritePageBuffer(decodedPixels);
            throw;
        }
    }

    private static void ValidateSpriteAtlasPageContentHash(
        Stream compressedStream,
        int compressedByteCount,
        string resourcePath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalSha256(expectedSha256))
        {
            throw new InvalidDataException(
                $"Brotli sprite page hash declaration is invalid: {resourcePath}");
        }

        ValidateSpriteAtlasPageContentHashCore(
            compressedStream,
            compressedByteCount,
            resourcePath,
            Convert.FromHexString(expectedSha256),
            cancellationToken);
    }

    private static void ValidateSpriteAtlasPageContentHashCore(
        Stream compressedStream,
        int compressedByteCount,
        string resourcePath,
        ReadOnlySpan<byte> expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!compressedStream.CanRead || compressedByteCount <= 0 ||
            expectedSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                $"Brotli sprite page hash declaration is invalid: {resourcePath}");
        }

        const int hashBufferSize = 64 * 1024;
        var hashBuffer = ArrayPool<byte>.Shared.Rent(hashBufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var remaining = compressedByteCount;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = Math.Min(remaining, hashBufferSize);
                var read = compressedStream.Read(hashBuffer, 0, requested);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        "Brotli sprite page compressed data ended early.");
                }

                hash.AppendData(hashBuffer, 0, read);
                remaining -= read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (compressedStream.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    "Brotli sprite page compressed length does not match the manifest.");
            }

            Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
            if (!hash.TryGetHashAndReset(actualHash, out var written) ||
                written != SHA256.HashSizeInBytes)
            {
                throw new CryptographicException(
                    "Brotli sprite page SHA-256 could not be finalized.");
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    expectedSha256))
            {
                throw new InvalidDataException(
                    $"Brotli sprite page SHA-256 does not match the manifest: {resourcePath}");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hashBuffer, clearArray: false);
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
                ReturnSpritePageBuffer(result.Pixels);
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
        var targetBytes = GetSpritePageResidentBudgetBytes();
        TrimResidentSpritePagesToTarget(
            targetBytes,
            preservePageName);
        TrimSpritePageBufferPoolToTarget(targetBytes);
    }

    private long GetSpritePageResidentBudgetBytes() =>
        _isEdgeRoaming || _edgeRoamPreloadRequested
            ? SpritePageRoamResidentBudgetBytes
            : _workState != WorkState.Idle
                ? _workSeriousEnterRequested ||
                  _workSeriousExitRequested ||
                  ReferenceEquals(_activeClip, _workSeriousEnterClip) ||
                  ReferenceEquals(_activeClip, _workSeriousLoopClip) ||
                  ReferenceEquals(_activeClip, _workSeriousExitClip)
                    ? SpritePageSeriousWorkResidentBudgetBytes
                    : SpritePageWorkResidentBudgetBytes
                : SpritePageResidentBudgetBytes;

    private void TrimResidentSpritePagesToIdleTarget()
    {
        TrimResidentSpritePagesToTarget(
            SpritePageIdleResidentTargetBytes,
            preservePageName: null);

        // Resident eviction returns page arrays to the pool first. Converge
        // total resident plus reusable storage to the same idle target so LOH
        // high-water memory cannot grow with each different automatic action.
        TrimSpritePageBufferPoolToTarget(SpritePageIdleResidentTargetBytes);
    }

    private void TrimSpritePageBufferPoolToTarget(long targetBytes)
    {
        var discardedBytes = _spritePageBufferPool.TrimFreeBuffers(targetBytes);
        if (discardedBytes > 0)
        {
            RecordDiscardedSpritePageBytes(discardedBytes);
        }
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

        ScheduleSpritePageCollectionIfNeeded();
    }

    private void RequestResidentSpritePageTrim()
    {
        if (_isClosing)
        {
            return;
        }

        DeferIdleSpritePageTrim();
        if (!_isInsideVisualRenderingCallback)
        {
            TrimResidentSpritePagesToBudget();
            return;
        }

        // Clip completion can occur inside CompositionTarget.Rendering. Only
        // publish a flag there; dictionary/LRU mutation runs on the existing
        // dispatcher timer after composition returns.
        _residentSpritePageTrimPending = true;
        if (!_spritePagePrefetchDispatchTimer.IsEnabled)
        {
            _spritePagePrefetchDispatchTimer.Start();
        }
    }

    private void RequestIdleSpritePageTrim(bool immediate = false)
    {
        if (IsLongLivedSpritePageIdleTrimBlocker())
        {
            StopIdleSpritePageTrim();
            return;
        }

        if (!immediate)
        {
            DeferIdleSpritePageTrim();
            return;
        }

        if (_isClosing)
        {
            return;
        }

        // CompleteEdgeRoamStop can run inside CompositionTarget.Rendering.
        // A zero-delay DispatcherTimer posts the deep trim until composition
        // has returned instead of mutating the resident dictionary/LRU there.
        // The Tick keeps the same full-idle gate and five-second busy retry.
        _spritePageIdleTrimTimer.Stop();
        _spritePageIdleTrimTimer.Interval = TimeSpan.Zero;
        _spritePageIdleTrimTimer.Start();
    }

    private void DeferIdleSpritePageTrim()
    {
        if (_isClosing)
        {
            return;
        }

        if (IsLongLivedSpritePageIdleTrimBlocker())
        {
            StopIdleSpritePageTrim();
            return;
        }

        _spritePageIdleTrimTimer.Stop();
        _spritePageIdleTrimTimer.Interval =
            SpritePageIdleTrimGracePeriod;
        _spritePageIdleTrimTimer.Start();
    }

    private void StopIdleSpritePageTrim()
    {
        if (_spritePageIdleTrimTimer.IsEnabled)
        {
            _spritePageIdleTrimTimer.Stop();
        }

        _spritePageIdleTrimTimer.Interval =
            SpritePageIdleTrimGracePeriod;
    }

    private void SpritePageIdleTrimTimer_Tick(object? sender, EventArgs e)
    {
        _spritePageIdleTrimTimer.Stop();
        if (_isClosing)
        {
            return;
        }

        // Deep trimming is allowed only in the same fully idle window used for
        // low-frequency collection. Long-lived states stop this timer until
        // their explicit exit path schedules one new grace period; transient
        // decode, drag, resize and Rendering work retain the five-second
        // watchdog so a missed completion signal cannot keep hot pages forever.
        if (IsLongLivedSpritePageIdleTrimBlocker())
        {
            _spritePageIdleTrimTimer.Interval =
                SpritePageIdleTrimGracePeriod;
            return;
        }

        if (!CanRunIdleSpritePageCollection())
        {
            _spritePageIdleTrimTimer.Interval =
                SpritePageIdleTrimRetryDelay;
            _spritePageIdleTrimTimer.Start();
            return;
        }

        TrimResidentSpritePagesToIdleTarget();
        _spritePageIdleTrimTimer.Interval =
            SpritePageIdleTrimGracePeriod;
    }

    private bool IsLongLivedSpritePageIdleTrimBlocker() =>
        _sessionInactive ||
        _workState != WorkState.Idle ||
        _isReminderActive ||
        _bubbleMode is BubbleMode.Todo or BubbleMode.Reminder ||
        _todoWindow.IsVisible ||
        _isEdgeRoaming;

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
        _workState == WorkState.Idle &&
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
        !_edgeRoamPreloadRequested &&
        !_isEdgeRoaming &&
        _bubbleMode == BubbleMode.None &&
        !_todoWindow.IsVisible &&
        !BubblePopup.IsOpen &&
        IsEdgeDockIdleForSpritePageCollection() &&
        _pendingSpriteFrame is null &&
        _spritePagePrefetchTask is null &&
        _desiredSpritePageName is null &&
        _renderDeferredSpritePageName is null &&
        _renderDeferredSpritePageFailureName is null &&
        !_renderDeferredSpritePageCancellation &&
        !_residentSpritePageTrimPending &&
        _upcomingReminderPreloadPageName is null;

    private bool IsEdgeDockIdleForSpritePageCollection()
    {
        if (_edgeDock == EdgeDock.None)
        {
            return true;
        }

        if (!_edgePeekHoldTimer.IsEnabled)
        {
            return false;
        }

        var frames = GetEdgeFrames(_edgeDock);
        return _edgePeekFrameIndex >= 0 &&
               _edgePeekFrameIndex < frames.Length &&
               _currentSpriteFrame is SpriteFrame displayedFrame &&
               displayedFrame == frames[_edgePeekFrameIndex];
    }

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
        // A normal Gen2 collection can reclaim references without compacting
        // the LOH segments that held discarded sprite pages. Keep the eviction
        // debt until our explicitly requested idle CompactOnce has completed;
        // otherwise unrelated natural collections suppress the only path that
        // returns the long-running process from its atlas high-water mark.
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
                ScheduleSpritePageCollectionIfNeeded();
                return;
            }

            _spritePageCollectionPollCount++;
            if (_spritePageCollectionPollCount >= 10)
            {
                _spritePageCollectionInProgress = false;
                _spritePageCollectionDebtAtRequest = 0;
                _spritePageCollectionPollCount = 0;
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

        _ = Task.Run(static () =>
            {
                // Page arrays live on the LOH. A normal Gen2 collection drops
                // references but can leave the committed high-water segments
                // behind indefinitely, which is why Task Manager kept rising
                // after each different automatic action. This path runs only
                // after the same fully-idle gate and 30-second rate limit used
                // above, so compact the LOH once and return those segments.
                GCSettings.LargeObjectHeapCompactionMode =
                    GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(
                    GC.MaxGeneration,
                    GCCollectionMode.Aggressive,
                    blocking: true,
                    compacting: true);
            })
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
            string.Equals(
                pageName,
                _upcomingReminderPreloadPageName,
                StringComparison.Ordinal) ||
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

        if ((_isEdgeRoaming || _edgeRoamPreloadRequested) &&
            (FrameSequenceUsesSpritePage(_roamBoardingFrames, pageName) ||
             (_edgeRoamPhase != EdgeRoamPhase.Disembarking &&
              (FrameSequenceUsesSpritePage(_roamFlightFrames, pageName) ||
               FrameSequenceUsesSpritePage(_roamWaveFrames, pageName)))))
        {
            return true;
        }

        if (_edgeDock != EdgeDock.None &&
            FrameSequenceUsesSpritePage(GetEdgeFrames(_edgeDock), pageName))
        {
            return true;
        }

        if (_workEdgeDock != EdgeDock.None &&
            FrameSequenceUsesSpritePage(GetEdgeFrames(_workEdgeDock), pageName))
        {
            return true;
        }

        return false;
    }

    private static bool FrameSequenceUsesSpritePage(
        SpriteFrame[] frames,
        string pageName)
    {
        foreach (var frame in frames)
        {
            if (string.Equals(frame.PageName, pageName, StringComparison.Ordinal))
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
        ReturnSpritePageBuffer(residentPage.Pixels);
        return true;
    }

    private void ReturnSpritePageBuffer(byte[] pixels)
    {
        var discardedBytes =
            _spritePageBufferPool.ReturnAndGetDiscardedBytes(pixels);
        if (_isClosing)
        {
            // A decode can finish after Closing cleared the resident cache.
            // Never let that late result repopulate the free LOH pool.
            _ = _spritePageBufferPool.ClearFreeBuffers();
            return;
        }

        if (discardedBytes > 0 &&
            Dispatcher.CheckAccess())
        {
            RecordDiscardedSpritePageBytes(discardedBytes);
        }
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
        _spritePagePixels = Array.Empty<byte>();
        foreach (var residentPage in _residentSpritePages.Values)
        {
            ReturnSpritePageBuffer(residentPage.Pixels);
        }

        _residentSpritePages.Clear();
        _residentSpritePageLru.Clear();
        _residentSpritePageBytes = 0;
        _spritePageEvictedBytesSinceCollection = 0;
        _spritePageCollectionDebtAtRequest = 0;
        _spritePageCollectionInProgress = false;
        _spritePageCollectionPollCount = 0;
        _spritePageCollectionTimer.Stop();
        _ = _spritePageBufferPool.ClearFreeBuffers();
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
        if (payload.Length < expectedPayloadByteCount)
        {
            throw new InvalidDataException(
                $"Sprite page payload declaration is invalid: {resourcePath}");
        }

        using var payloadStream = new MemoryStream(
            payload,
            0,
            expectedPayloadByteCount,
            writable: false,
            publiclyVisible: true);
        DecodeSpritePageStream(
            resourcePath,
            encoding,
            payloadStream,
            expectedPayloadByteCount,
            decodedPixels,
            atlasWidth,
            atlasHeight,
            frameDescriptorValues,
            expectedDecodedSha256,
            cancellationToken);
    }

    private static void DecodeSpritePageStream(
        string resourcePath,
        string encoding,
        Stream payloadStream,
        int expectedPayloadByteCount,
        byte[] decodedPixels,
        int atlasWidth,
        int atlasHeight,
        int[] frameDescriptorValues,
        string expectedDecodedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalSha256(expectedDecodedSha256))
        {
            throw new InvalidDataException(
                $"Sprite page payload declaration is invalid: {resourcePath}");
        }

        DecodeSpritePageStreamCore(
            resourcePath,
            encoding,
            payloadStream,
            expectedPayloadByteCount,
            decodedPixels,
            atlasWidth,
            atlasHeight,
            frameDescriptorValues,
            Convert.FromHexString(expectedDecodedSha256),
            frameDescriptorValues.Length / SpriteFrameDescriptorValueCount,
            cancellationToken);
    }

    private static void DecodeSpritePageStreamCore(
        string resourcePath,
        string encoding,
        Stream payloadStream,
        int expectedPayloadByteCount,
        byte[] decodedPixels,
        int atlasWidth,
        int atlasHeight,
        int[] frameDescriptorValues,
        ReadOnlySpan<byte> expectedDecodedSha256,
        int uniqueSpriteCount,
        CancellationToken cancellationToken)
    {
        if (!payloadStream.CanRead ||
            !IsSupportedSpriteAtlasEncoding(encoding) ||
            atlasWidth <= 0 || atlasHeight <= 0 ||
            (long)atlasWidth * atlasHeight > int.MaxValue / 4 ||
            expectedPayloadByteCount <= 0 ||
            expectedPayloadByteCount > MaximumSpritePagePayloadBytes ||
            expectedDecodedSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                $"Sprite page payload declaration is invalid: {resourcePath}");
        }

        var expectedAtlasByteCount = checked(atlasWidth * atlasHeight * 4);
        if (decodedPixels.Length < expectedAtlasByteCount)
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

            var payloadOffset = 0;
            ReadPayloadExactly(
                payloadStream,
                decodedPixels.AsSpan(0, expectedAtlasByteCount),
                ref payloadOffset,
                expectedPayloadByteCount,
                cancellationToken);
        }
        else
        {
            ReconstructDeltaSubSpritePage(
                payloadStream,
                expectedPayloadByteCount,
                decodedPixels,
                atlasWidth,
                atlasHeight,
                frameDescriptorValues,
                uniqueSpriteCount,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (payloadStream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"Sprite page payload exceeds its declared length: {resourcePath}");
        }

        ValidateSpriteAtlasDecodedHashCore(
            resourcePath,
            decodedPixels.AsSpan(0, expectedAtlasByteCount),
            expectedDecodedSha256);
    }

    private static void ReadPayloadExactly(
        Stream payloadStream,
        Span<byte> destination,
        ref int payloadOffset,
        int expectedPayloadByteCount,
        CancellationToken cancellationToken)
    {
        if (destination.Length > expectedPayloadByteCount - payloadOffset)
        {
            throw new EndOfStreamException(
                "Sprite page payload ended before the declared frame data.");
        }

        var destinationOffset = 0;
        while (destinationOffset < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = payloadStream.Read(destination[destinationOffset..]);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "Sprite page payload stream ended early.");
            }

            destinationOffset += read;
            payloadOffset = checked(payloadOffset + read);
        }
    }

    private static void ReconstructDeltaSubSpritePage(
        Stream payloadStream,
        int payloadByteCount,
        byte[] atlasPixels,
        int atlasWidth,
        int atlasHeight,
        int[] frameDescriptorValues,
        int uniqueSpriteCount,
        CancellationToken cancellationToken)
    {
        if (atlasWidth <= 0 || atlasHeight <= 0 ||
            (long)atlasWidth * atlasHeight > int.MaxValue / 4 ||
            frameDescriptorValues.Length == 0 ||
            frameDescriptorValues.Length % SpriteFrameDescriptorValueCount != 0 ||
            uniqueSpriteCount <= 0)
        {
            throw new InvalidDataException("Delta-sub sprite page geometry is invalid.");
        }


        var expectedAtlasByteCount = checked(atlasWidth * atlasHeight * 4);
        if (atlasPixels.Length < expectedAtlasByteCount)
        {
            throw new InvalidDataException("Delta-sub sprite page buffer length is invalid.");
        }

        var frameCount = frameDescriptorValues.Length /
                         SpriteFrameDescriptorValueCount;
        if (payloadByteCount <= 0 ||
            payloadByteCount < checked(frameCount * DeltaSubFrameHeaderByteCount))
        {
            throw new InvalidDataException("Delta-sub sprite page payload ended before its headers.");
        }

        Array.Clear(atlasPixels, 0, expectedAtlasByteCount);
        var previousDisplayFrameByteCount = DisplayFrameByteCount;
        var previousDisplayFrame = SpriteDecodeScratchPool.Rent(
            previousDisplayFrameByteCount);
        Array.Clear(previousDisplayFrame, 0, previousDisplayFrameByteCount);
        try
        {
        var writtenRegionDestinations =
            new Dictionary<SpriteAtlasRegion, (int X, int Y)>(
                uniqueSpriteCount);
        var validatedRegions = new List<SpriteAtlasRegion>(
            uniqueSpriteCount);
        var payloadOffset = 0;
        var atlasStride = checked(atlasWidth * 4);
        var displayStride = checked(DisplayPixelWidth * 4);
        Span<byte> header = stackalloc byte[DeltaSubFrameHeaderByteCount];
        Span<byte> deltaRow = stackalloc byte[DisplayPixelWidth * 4];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payloadByteCount - payloadOffset < DeltaSubFrameHeaderByteCount)
            {
                throw new InvalidDataException(
                    $"Delta-sub frame header is truncated at frame {frameIndex}.");
            }

            ReadPayloadExactly(
                payloadStream,
                header,
                ref payloadOffset,
                payloadByteCount,
                cancellationToken);
            var deltaX = BinaryPrimitives.ReadUInt16LittleEndian(header);
            var deltaY = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            var deltaWidth = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            var deltaHeight = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);

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
                    var rowPayload = deltaRow[..deltaRowByteCount];
                    ReadPayloadExactly(
                        payloadStream,
                        rowPayload,
                        ref payloadOffset,
                        payloadByteCount,
                        cancellationToken);
                    for (var byteIndex = 0;
                         byteIndex < deltaRowByteCount;
                         byteIndex++)
                    {
                        previousDisplayFrame[previousOffset + byteIndex] = unchecked(
                            (byte)(previousDisplayFrame[previousOffset + byteIndex] +
                                   rowPayload[byteIndex]));
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
                for (var regionIndex = 0;
                     regionIndex < validatedRegions.Count;
                     regionIndex++)
                {
                    if (validatedRegions[regionIndex].Intersects(region))
                    {
                        throw new InvalidDataException(
                            $"Delta-sub atlas regions overlap at frame {frameIndex}.");
                    }
                }

                validatedRegions.Add(region);
                writtenRegionDestinations.Add(
                    region,
                    (destinationX, destinationY));
            }

            CopyOrValidateDeltaSpriteRegion(
                previousDisplayFrame,
                atlasPixels,
                atlasStride,
                atlasX,
                atlasY,
                spriteWidth,
                spriteHeight,
                destinationX,
                destinationY,
                regionWasWritten,
                frameIndex);
        }

        if (payloadOffset != payloadByteCount)
        {
            throw new InvalidDataException(
                $"Delta-sub sprite page has {payloadByteCount - payloadOffset} trailing bytes.");
        }
        }
        finally
        {
            SpriteDecodeScratchPool.Return(
                previousDisplayFrame,
                clearArray: false);
        }
    }

    private static void CopyOrValidateDeltaSpriteRegion(
        byte[] displayPixels,
        byte[] atlasPixels,
        int atlasStride,
        int atlasX,
        int atlasY,
        int spriteWidth,
        int spriteHeight,
        int destinationX,
        int destinationY,
        bool validateExisting,
        int frameIndex)
    {
        var displayStride = checked(DisplayPixelWidth * 4);
        var spriteRowByteCount = checked(spriteWidth * 4);
        for (var row = 0; row < spriteHeight; row++)
        {
            var atlasRowOffset = checked(
                (atlasY + row) * atlasStride + atlasX * 4);
            var displayY = (long)destinationY + row;
            var visibleStartColumn = checked((int)Math.Max(
                0L,
                -(long)destinationX));
            var visibleEndColumn = checked((int)Math.Min(
                (long)spriteWidth,
                (long)DisplayPixelWidth - destinationX));
            if (displayY < 0 ||
                displayY >= DisplayPixelHeight ||
                visibleStartColumn >= visibleEndColumn)
            {
                if (validateExisting &&
                    ContainsNonZero(
                        atlasPixels.AsSpan(
                            atlasRowOffset,
                            spriteRowByteCount)))
                {
                    throw new InvalidDataException(
                        $"Repeated delta-sub sprite differs at frame {frameIndex}.");
                }

                continue;
            }

            var visibleByteOffset = checked(visibleStartColumn * 4);
            var visibleByteCount = checked(
                (visibleEndColumn - visibleStartColumn) * 4);
            var visibleDisplayX = checked((int)(
                (long)destinationX + visibleStartColumn));
            var sourceOffset = checked(
                (int)displayY * displayStride + visibleDisplayX * 4);
            var atlasVisibleOffset = checked(
                atlasRowOffset + visibleByteOffset);
            if (!validateExisting)
            {
                Buffer.BlockCopy(
                    displayPixels,
                    sourceOffset,
                    atlasPixels,
                    atlasVisibleOffset,
                    visibleByteCount);
                continue;
            }

            if ((visibleByteOffset > 0 &&
                 ContainsNonZero(
                     atlasPixels.AsSpan(
                         atlasRowOffset,
                         visibleByteOffset))) ||
                !atlasPixels.AsSpan(
                        atlasVisibleOffset,
                        visibleByteCount)
                    .SequenceEqual(
                        displayPixels.AsSpan(
                            sourceOffset,
                            visibleByteCount)) ||
                (visibleByteOffset + visibleByteCount < spriteRowByteCount &&
                 ContainsNonZero(
                     atlasPixels.AsSpan(
                         atlasVisibleOffset + visibleByteCount,
                         spriteRowByteCount -
                         visibleByteOffset -
                         visibleByteCount))))
            {
                throw new InvalidDataException(
                    $"Repeated delta-sub sprite differs at frame {frameIndex}.");
            }
        }
    }

    private static bool ContainsNonZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateSpriteAtlasDecodedHash(
        string resourcePath,
        ReadOnlySpan<byte> decodedPixels,
        string expectedSha256)
    {
        if (decodedPixels.Length == 0 || !IsCanonicalSha256(expectedSha256))
        {
            throw new InvalidDataException(
                $"Sprite page decoded hash declaration is invalid: {resourcePath}");
        }

        ValidateSpriteAtlasDecodedHashCore(
            resourcePath,
            decodedPixels,
            Convert.FromHexString(expectedSha256));
    }

    private static void ValidateSpriteAtlasDecodedHashCore(
        string resourcePath,
        ReadOnlySpan<byte> decodedPixels,
        ReadOnlySpan<byte> expectedSha256)
    {
        if (decodedPixels.Length == 0 ||
            expectedSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException(
                $"Sprite page decoded hash declaration is invalid: {resourcePath}");
        }

        Span<byte> actualHash = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(decodedPixels, actualHash);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedSha256))
        {
            throw new InvalidDataException(
                $"Sprite page decoded SHA-256 does not match the manifest: {resourcePath}");
        }
    }

    private void SetBubbleMode(BubbleMode mode)
    {
        if (mode != BubbleMode.None)
        {
            CancelTodoOpenAfterEdgeRoamStop();
            CancelTodoOpenAfterWorkExit();
        }

        if (mode is BubbleMode.Todo or BubbleMode.Reminder &&
            (_dragInteractionActive || _pointerDown || _dragStarted ||
             PetHost.IsMouseCaptured))
        {
            CancelPetPointerInteractionForInterruption();
        }

        if (mode is BubbleMode.Todo or BubbleMode.Reminder &&
            _workState != WorkState.Idle)
        {
            StopWorkModeImmediately(restoreIdleFrame: false);
        }

        if (_bubbleMode == mode)
        {
            return;
        }

        var previousMode = _bubbleMode;
        if (mode == BubbleMode.Reminder &&
            previousMode != BubbleMode.Reminder)
        {
            _todoWindow.BeginReminderInterruption();
        }

        if (mode is BubbleMode.Todo or BubbleMode.Reminder ||
            previousMode is BubbleMode.Todo or BubbleMode.Reminder)
        {
            StopEdgeRoaming(
                scheduleNext: true,
                restoreIdleFrame: false,
                interrupted: true,
                immediate: true);
            // Showing an owned WPF window can synchronously pump layout/render work.
            // Unsubscribe before Show() so a re-entrant composition callback cannot
            // observe BubbleMode.Todo while the old reaction is still active and
            // replace it with the final think pose. EnterTodoVisualState subscribes
            // again only after the owned window has finished its one-time work.
            _automaticTimer.Stop();
            StopVisualClock();
        }

        var preserveTodoWindow =
            _todoWindow.IsVisible &&
            mode is BubbleMode.Todo or BubbleMode.Reminder;
        HideBubbleVisuals(preserveTodoWindow);
        _bubbleMode = mode;
        if (mode == BubbleMode.Todo)
        {
            // Materialize the exact pose currently on screen and publish the
            // first Todo-entry pose before the transparent owner changes
            // state or the owned panel can pump a nested layout/render pass.
            // Keep the clip frozen until Show() returns, so the right-click
            // path cannot expose an old transform or skip an entry pose.
            EnterTodoVisualState();
            StopVisualClock();

            // The native owner already has its permanent maximum envelope.
            // Merely opening Todo must not begin a size-preview session:
            // re-anchoring the Viewbox and moving the layered HWND in the same
            // right-click could otherwise expose one intermediate frame.
            EnsureTodoPetSizePreviewEnvelope();
            ShowBubbleVisuals(mode);
            UpdateVisualClockSubscription();
        }
        else
        {
            ShowBubbleVisuals(mode);
            if (mode == BubbleMode.Reminder)
            {
                EnterReminderVisualState();
            }
        }

        if (previousMode == BubbleMode.Reminder &&
            mode != BubbleMode.Reminder)
        {
            _todoWindow.EndReminderInterruption(
                restoreEditorFocus: mode == BubbleMode.Todo);
        }

        if (mode != BubbleMode.Todo &&
            mode != BubbleMode.Reminder &&
            previousMode == BubbleMode.Todo)
        {
            CommitStableTodoPetSizePreviewBeforeExit();
            StartTodoExitTransition();
        }
        else if (mode != BubbleMode.Reminder &&
                 mode != BubbleMode.Todo &&
                 previousMode == BubbleMode.Reminder)
        {
            StartReminderExitTransition();
        }

        if (mode is not BubbleMode.Todo and not BubbleMode.Reminder)
        {
            ReleaseTodoPetSizePreviewEnvelope();
        }
    }

    private void CommitStableTodoPetSizePreviewBeforeExit()
    {
        ConsumeLatestPetSizeInputAt(Stopwatch.GetTimestamp());
        if (!_petSizePreviewEnvelopePinnedForTodo ||
            !_isPetSizePreviewSessionActive ||
            _isTransientPetSizeOverride ||
            _isPetSizeAdjustmentActive ||
            _isPetSizeTransitioning ||
            _petSizeTargetUpdatePending ||
            _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            return;
        }

        // A Todo panel opens one maximum transparent envelope before its entry
        // animation. If that envelope is tightened only after todo-close reaches
        // its idle endpoint, the otherwise still final pose visibly nudges once.
        // Tighten while the current Todo pose is frozen, then start the exit
        // clip inside the final native bounds. No native geometry is left to
        // change after the last animation frame.
        StopVisualClock();
        _petSizePreviewEnvelopePinnedForTodo = false;
        CommitPetSizePreviewSession(persist: true);
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
        ShowActiveClipFrame(enterStartIndex);
    }

    private int GetTodoEnterStartIndex(SpriteFrame? frame)
    {
        if (frame is not { } currentFrame)
        {
            return 0;
        }

        for (var frameIndex = 0;
             frameIndex < _todoEnterClip.Frames.Length;
             frameIndex++)
        {
            if (string.Equals(
                    _todoEnterClip.Frames[frameIndex].Image.Name,
                    currentFrame.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                return frameIndex;
            }
        }

        // Non-think actions and edge-peek poses are already upright. Resume
        // from the final wake pose so a right click never flashes back to the
        // sleeping pillow before entering the think sequence.
        for (var frameIndex = 0;
             frameIndex < _todoEnterClip.Frames.Length;
             frameIndex++)
        {
            if (string.Equals(
                    _todoEnterClip.Frames[frameIndex].Image.Name,
                    _wakeFrames[^1].Name,
                    StringComparison.Ordinal))
            {
                return frameIndex;
            }
        }

        return -1;
    }

    private void StartTodoExitTransition()
    {
        if (_isClosing)
        {
            return;
        }

        StopPillowBreathing();
        _automaticTimer.Stop();
        if (_edgeDock != EdgeDock.None)
        {
            // The Todo window and the pet pose have independent ownership after
            // a drag reaches a screen edge. Closing the panel must not replace
            // the active edge-peek sequence with the standing-to-idle Todo exit.
            UpdateVisualClockSubscription();
            return;
        }

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
        ShowActiveClipFrame(0);
    }

    private void ConfigureReminderBubblePlacement()
    {
        RefreshReminderWindowPosition();
    }

    private void RefreshReminderBubbleOffset()
    {
        RefreshReminderWindowPosition();
    }

    private void ResetPetVisualTransforms()
    {
        _edgeRoamFacingScaleX = 1;
        _edgeRoamRotationDegrees = 0;
        _edgeRoamDisembarkStartRotationDegrees = 0;
        PetFacingScale.ScaleX = 1;
        PetFacingScale.ScaleY = 1;
        PetRoamRotate.Angle = 0;
        PetScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PetScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PetScale.ScaleX = 1;
        PetScale.ScaleY = 1;
    }

    private void HideBubbleVisuals(bool preserveTodoWindow = false)
    {
        _outsideTodoCloseGeneration++;
        BubblePopup.IsOpen = false;
        _cuteBubblePlacedOnLeft = null;
        _cuteBubbleTailPlacedOnLeft = null;
        BubbleHost.Visibility = Visibility.Collapsed;
        BubbleTailHost.Visibility = Visibility.Collapsed;
        CuteBubble.Visibility = Visibility.Collapsed;
        ReminderBubble.Visibility = Visibility.Collapsed;
        _reminderWindow?.HideSafely();
        if (!_isReminderActive)
        {
            // A 100-item reminder can own a large combined TextBox string.
            // Release it after the displayed occurrences have been consumed
            // or safely carried into the next reminder page.
            _reminderWindow?.ClearPresentation();
        }

        if (!preserveTodoWindow)
        {
            HideTodoWindowVisual();
        }
    }

    private void HideTodoWindowVisual()
    {
        if (!_todoWindow.IsVisible)
        {
            return;
        }

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

    private void ShowBubbleVisuals(BubbleMode mode)
    {
        if (mode == BubbleMode.None)
        {
            return;
        }

        if (mode == BubbleMode.Todo)
        {
            _todoWindow.SetPetSizeScale(_petSizeScale);
            _todoWindow.SetEdgeRoamingEnabled(_edgeRoamingEnabled);
            if (!_todoWindow.IsVisible)
            {
                _todoWindow.ShowDefaultTab();
                _todoWindow.RecoverAfterSystemResume();
                _todoWindow.Opacity = 0;
                _todoWindow.Show();
                _todoWindow.UpdateLayout();
            }

            _todoWindowPositionCache.InvalidateGeometry();
            UpdateTodoWindowPosition();
            _todoWindow.Opacity = 1;
            return;
        }

        if (mode == BubbleMode.Reminder)
        {
            ShowReminderWindow();
            return;
        }

        var displayedPetHeight = GetPetViewboxBoundsInScreenDips().Height;
        UpdateCuteBubblePlacementAndTail();
        BubblePopup.VerticalOffset = displayedPetHeight - CuteBubbleHeight;
        BubbleHost.Visibility = Visibility.Visible;
        BubbleTailHost.Visibility = Visibility.Visible;
        CuteBubble.Visibility = Visibility.Visible;
        BubblePopup.IsOpen = true;
        QueueCuteBubbleTailReconciliation();
    }

    private void UpdateCuteBubblePlacementAndTail()
    {
        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var petTopLeft = PetSizeViewbox.TranslatePoint(new Point(0, 0), this);
        var petBottomRight = PetSizeViewbox.TranslatePoint(
            new Point(
                PetSizeViewbox.ActualWidth,
                PetSizeViewbox.ActualHeight),
            this);
        var petLeft = Left + Math.Min(petTopLeft.X, petBottomRight.X);
        var petRight = Left + Math.Max(petTopLeft.X, petBottomRight.X);
        var bubbleWidth = CuteBubbleWidth + 12;
        var availableLeft = petLeft - workArea.Left;
        var availableRight = workArea.Right - petRight;
        var placeOnLeft =
            availableLeft >= bubbleWidth ||
            (availableRight < bubbleWidth &&
             availableLeft >= availableRight);
        if (_cuteBubblePlacedOnLeft != placeOnLeft)
        {
            _cuteBubblePlacedOnLeft = placeOnLeft;
            BubblePopup.Placement = placeOnLeft
                ? PlacementMode.Left
                : PlacementMode.Right;
            ApplyCuteBubbleTailSide(placeOnLeft);
        }

        if (_cuteBubbleTailPlacedOnLeft is null)
        {
            ApplyCuteBubbleTailSide(placeOnLeft);
        }

        QueueCuteBubbleTailReconciliation();
    }

    private void BubblePopup_Opened(object? sender, EventArgs e) =>
        QueueCuteBubbleTailReconciliation();

    private void QueueCuteBubbleTailReconciliation()
    {
        if (_cuteBubbleTailReconciliationQueued ||
            !BubblePopup.IsOpen ||
            _bubbleMode != BubbleMode.Cute)
        {
            return;
        }

        _cuteBubbleTailReconciliationQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _cuteBubbleTailReconciliationQueued = false;
                ReconcileCuteBubbleTailWithActualPlacement();
            }));
    }

    private void ReconcileCuteBubbleTailWithActualPlacement()
    {
        if (!BubblePopup.IsOpen ||
            _bubbleMode != BubbleMode.Cute ||
            BubblePopup.Child is not FrameworkElement popupChild ||
            !popupChild.IsLoaded ||
            !PetSizeViewbox.IsLoaded ||
            popupChild.ActualWidth <= 0 ||
            PetSizeViewbox.ActualWidth <= 0)
        {
            return;
        }

        try
        {
            var popupTopLeft = popupChild.PointToScreen(new Point(0, 0));
            var popupBottomRight = popupChild.PointToScreen(
                new Point(popupChild.ActualWidth, popupChild.ActualHeight));
            var petTopLeft = PetSizeViewbox.PointToScreen(new Point(0, 0));
            var petBottomRight = PetSizeViewbox.PointToScreen(
                new Point(
                    PetSizeViewbox.ActualWidth,
                    PetSizeViewbox.ActualHeight));
            var actualPopupIsOnLeft = ResolveCuteBubbleIsOnLeft(
                Math.Min(popupTopLeft.X, popupBottomRight.X),
                Math.Max(popupTopLeft.X, popupBottomRight.X),
                Math.Min(petTopLeft.X, petBottomRight.X),
                Math.Max(petTopLeft.X, petBottomRight.X),
                _cuteBubblePlacedOnLeft ?? true);
            ApplyCuteBubbleTailSide(actualPopupIsOnLeft);
        }
        catch (InvalidOperationException)
        {
            // The popup HWND can be recreated while crossing monitors or
            // changing DPI. The next Opened/LocationChanged pass will retry.
        }
    }

    private static bool ResolveCuteBubbleIsOnLeft(
        double popupLeftPixels,
        double popupRightPixels,
        double petLeftPixels,
        double petRightPixels,
        bool fallback)
    {
        if (!double.IsFinite(popupLeftPixels) ||
            !double.IsFinite(popupRightPixels) ||
            !double.IsFinite(petLeftPixels) ||
            !double.IsFinite(petRightPixels) ||
            popupRightPixels <= popupLeftPixels ||
            petRightPixels <= petLeftPixels)
        {
            return fallback;
        }

        var popupCenterPixels =
            popupLeftPixels + (popupRightPixels - popupLeftPixels) / 2;
        var petCenterPixels =
            petLeftPixels + (petRightPixels - petLeftPixels) / 2;
        if (Math.Abs(popupCenterPixels - petCenterPixels) <= 0.5)
        {
            return fallback;
        }

        return popupCenterPixels < petCenterPixels;
    }

    private void ApplyCuteBubbleTailSide(bool bubbleIsOnLeft)
    {
        if (_cuteBubbleTailPlacedOnLeft == bubbleIsOnLeft)
        {
            return;
        }

        _cuteBubbleTailPlacedOnLeft = bubbleIsOnLeft;
        BubbleBodyColumn.Width = bubbleIsOnLeft
            ? GridLength.Auto
            : new GridLength(12);
        BubbleTailColumn.Width = bubbleIsOnLeft
            ? new GridLength(12)
            : GridLength.Auto;
        Grid.SetColumn(BubbleHost, bubbleIsOnLeft ? 0 : 1);
        Grid.SetColumn(BubbleTailHost, bubbleIsOnLeft ? 1 : 0);
        BubbleTailHost.Margin = bubbleIsOnLeft
            ? new Thickness(-1, 0, 0, 0)
            : new Thickness(0, 0, -1, 0);
        BubbleTailHost.HorizontalAlignment = bubbleIsOnLeft
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        BubbleTailPolygon.Points = bubbleIsOnLeft
            ? CuteTailPointsRight
            : CuteTailPointsLeft;
        BubbleTailPolygon.Fill = Brushes.White;
        BubbleTailPolygon.Stroke = CuteBubbleStrokeBrush;
    }

    private ReminderWindow EnsureReminderWindow()
    {
        if (_reminderWindow is null)
        {
            _reminderWindow = new ReminderWindow();
            _reminderWindow.AcknowledgeRequested +=
                ReminderWindow_AcknowledgeRequested;
            _reminderWindow.DismissRequested +=
                ReminderWindow_DismissRequested;
        }

        if (IsLoaded && _reminderWindow.Owner is null)
        {
            _reminderWindow.Owner = this;
        }

        return _reminderWindow;
    }

    private void ShowReminderWindow()
    {
        if (!IsVisible)
        {
            return;
        }

        var reminderWindow = EnsureReminderWindow();
        if (_todoWindow.IsVisible)
        {
            reminderWindow.ShowBeside(_todoWindow);
        }
        else
        {
            // The main HWND deliberately stays at the permanent 140% envelope
            // to prevent layered-window flashes. Position the reminder beside
            // the rendered pet, not beside the transparent unused margin.
            reminderWindow.ShowBeside(this, PetSizeViewbox);
        }

        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        var petCenter =
            visiblePetBounds.Left + visiblePetBounds.Width / 2;
        var reminderCenter =
            reminderWindow.Left +
            (reminderWindow.ActualWidth > 0
                ? reminderWindow.ActualWidth
                : reminderWindow.Width) / 2;
        _reminderFacingScaleX = reminderCenter <= petCenter ? 1 : -1;
    }

    private void RefreshReminderWindowPosition()
    {
        if (_isClosing ||
            !_isReminderActive ||
            _bubbleMode != BubbleMode.Reminder ||
            _reminderWindow?.IsVisible != true)
        {
            return;
        }

        ShowReminderWindow();
    }

    private void QueueReminderWindowPositionUpdate()
    {
        if (_reminderPositionUpdateQueued ||
            _isClosing ||
            _reminderWindow?.IsVisible != true)
        {
            return;
        }

        _reminderPositionUpdateQueued = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            _processReminderWindowPositionUpdateAction);
    }

    private void ProcessReminderWindowPositionUpdate()
    {
        _reminderPositionUpdateQueued = false;
        RefreshReminderWindowPosition();
    }

    private void ReminderWindow_AcknowledgeRequested(
        object? sender,
        EventArgs e)
    {
        AcknowledgeActiveReminder();
    }

    private void ReminderWindow_DismissRequested(
        object? sender,
        EventArgs e)
    {
        AcknowledgeActiveReminder();
    }

    private void TodoWindow_PetSizeScaleChanged(double scale)
    {
        if (_isTransientPetSizeOverride)
        {
            return;
        }

        QueuePetSizeScaleTargetAt(scale, Stopwatch.GetTimestamp());
    }

    private void TodoWindow_EdgeRoamingEnabledChanged(bool enabled)
    {
        if (_edgeRoamingEnabled == enabled)
        {
            return;
        }

        _edgeRoamingEnabled = enabled;
        _todoWindow.SetEdgeRoamingEnabled(enabled);
        if (enabled)
        {
            ScheduleNextEdgeRoam(
                Stopwatch.GetTimestamp(),
                EdgeRoamInterval);
        }
        else
        {
            _edgeRoamPreloadRequested = false;
            StopEdgeRoaming(
                scheduleNext: false,
                restoreIdleFrame: true,
                interrupted: true);
            _nextEdgeRoamDueTimestamp = 0;
        }

        SaveSettings();
        RestartAutomaticCountdown();
    }

    private void TodoWindow_StartupEnabledChanged(bool enabled)
    {
        if (_startupRegistration is null)
        {
            _todoWindow.SetStartupEnabled(
                enabled: false,
                "当前运行方式无法设置开机自启");
            return;
        }

        if (_startupRegistration.TrySetEnabled(
                enabled,
                out var actualEnabled,
                out var error))
        {
            _todoWindow.SetStartupEnabled(actualEnabled);
            return;
        }

        _todoWindow.SetStartupEnabled(
            actualEnabled,
            $"开机自启设置失败：{error}");
    }

    private void TodoWindow_PetSizeAdjustmentStarted()
    {
        if (_isTransientPetSizeOverride)
        {
            return;
        }

        DeferIdleSpritePageTrim();
        StopEdgeRoaming(
            scheduleNext: true,
            restoreIdleFrame: true,
            interrupted: true);
        _isPetSizeAdjustmentActive = true;
        _petSizeAdjustmentValueChanged = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();

        // Do not resize the transparent HWND merely because the Track was
        // pressed. A move-to-point click can legitimately resolve to the value
        // it already has; eagerly opening and immediately committing the
        // maximum preview envelope made those no-op clicks flash while an
        // animation frame was being presented. The first real ValueChanged
        // reaches QueuePetSizeScaleTargetAt, which prepares the envelope once.
        UpdateVisualClockSubscription();
    }

    private void TodoWindow_PetSizeAdjustmentCompleted()
    {
        if (_isTransientPetSizeOverride ||
            !_isPetSizeAdjustmentActive)
        {
            return;
        }

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
            if (!_petSizePreviewEnvelopePinnedForTodo)
            {
                CommitPetSizePreviewSession(persist: false);
            }
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

    private void EnsureTodoPetSizePreviewEnvelope()
    {
        _petSizePreviewEnvelopePinnedForTodo = true;
        if (_isClosing || _isTransientPetSizeOverride)
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        ConsumeLatestPetSizeInputAt(timestamp);
        if (_isPetSizePreviewSessionActive)
        {
            PreparePetSizePreviewEnvelope();
        }

        _petSizePersistTimer.Stop();
        _petSizeCommitPending = false;
    }

    private void ReleaseTodoPetSizePreviewEnvelope()
    {
        if (!_petSizePreviewEnvelopePinnedForTodo)
        {
            return;
        }

        _petSizePreviewEnvelopePinnedForTodo = false;
        ScheduleUnpinnedPetSizePreviewCommit();
    }

    private void ScheduleUnpinnedPetSizePreviewCommit()
    {
        if (_isClosing ||
            _petSizePreviewEnvelopePinnedForTodo ||
            _isTransientPetSizeOverride ||
            !_isPetSizePreviewSessionActive)
        {
            return;
        }

        _petSizeCommitPending = true;
        // A timer started by the last slider MouseUp must never survive into
        // the Todo exit animation. Otherwise its 400 ms Tick can shrink the
        // layered window while exit poses are still being presented.
        _petSizePersistTimer.Stop();
        if (_isPetSizeAdjustmentActive ||
            _activeClip is not null ||
            _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            return;
        }

        _petSizePersistTimer.Interval = PetSizePersistDelay;
        _petSizePersistTimer.Start();
    }

    private void BeginPetSizePreviewSession(double currentScale)
    {
        if (_isPetSizePreviewSessionActive)
        {
            return;
        }

        _petSizePreviewAnchor = CapturePetSizeAnchor(preservePosition: true);
        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        _petSizeTodoChildOnLeft = _todoWindow.IsVisible
            ? _todoWindow.Left <
              visiblePetBounds.Left + visiblePetBounds.Width / 2
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

        if (_petSizeTodoPositionNeedsUpdate && _todoWindow.IsVisible)
        {
            // Keep the arrow visually attached to Luban on every composition
            // frame without moving the Todo HWND that currently owns the
            // captured Slider. The full child-window move remains deferred
            // until release so fast pointer drags stay stable.
            var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
            var todoIsBeforePet = _petSizeTodoChildOnLeft ??
                                  _todoWindow.Left +
                                  (_todoWindow.ActualWidth > 0
                                      ? _todoWindow.ActualWidth
                                      : _todoWindow.Width) /
                                  2 <=
                                  visiblePetBounds.Left +
                                  visiblePetBounds.Width / 2;
            UpdateTodoWindowTailPlacement(todoIsBeforePet);
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
        if (completed &&
            _isReminderActive &&
            _reminderWindow?.IsVisible == true)
        {
            // RenderTransform does not raise SizeChanged on the placement
            // anchor. Re-anchor the reminder once the final enlarged/shrunk
            // pet geometry has been committed.
            QueueReminderWindowPositionUpdate();
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

        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        if (_petSizeLogicalAnchor is { } logicalAnchor &&
            logicalAnchor.WorkArea == workArea)
        {
            return logicalAnchor;
        }

        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        var anchor = CreatePetSizeAnchor(
            workArea,
            visiblePetBounds);
        _petSizeLogicalAnchor = anchor;
        return anchor;
    }

    private Rect GetPetViewboxBoundsInScreenDips()
    {
        if (IsLoaded &&
            PetSizeViewbox.IsLoaded &&
            PetSizeViewbox.ActualWidth > 0 &&
            PetSizeViewbox.ActualHeight > 0)
        {
            try
            {
                var topLeft = PetSizeViewbox.TranslatePoint(
                    new Point(0, 0),
                    this);
                var bottomRight = PetSizeViewbox.TranslatePoint(
                    new Point(
                        PetSizeViewbox.ActualWidth,
                        PetSizeViewbox.ActualHeight),
                    this);
                return new Rect(
                    Left + Math.Min(topLeft.X, bottomRight.X),
                    Top + Math.Min(topLeft.Y, bottomRight.Y),
                    Math.Abs(bottomRight.X - topLeft.X),
                    Math.Abs(bottomRight.Y - topLeft.Y));
            }
            catch (InvalidOperationException)
            {
                // Fall through to deterministic layout math while the visual
                // tree is being reattached after a monitor/DPI transition.
            }
        }

        var scale = NormalizePetSizeScale(_petSizeScale);
        var displayedWidth = PetWidth * scale;
        var displayedHeight = PetHeight * scale;
        var offset = CalculatePetEnvelopeContentOffset(
            displayedWidth,
            displayedHeight,
            PetSizeViewbox.HorizontalAlignment,
            PetSizeViewbox.VerticalAlignment);
        return new Rect(
            Left + offset.X,
            Top + offset.Y,
            displayedWidth,
            displayedHeight);
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
                : anchor is null
                    ? HorizontalAlignment.Center
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
        var wasApplyingLayout = _isApplyingPetSizeLayout;
        _isApplyingPetSizeLayout = true;
        try
        {
            if (anchor is not { } fixedAnchor)
            {
                Width = PetEnvelopeWidth;
                Height = PetEnvelopeHeight;
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
            if (!OwnedWindowPositioner.TrySetBounds(this, bounds))
            {
                // Before SourceInitialized (or if the native call fails), keep
                // the ordinary WPF dependency-property path as a safe fallback.
                Width = PetEnvelopeWidth;
                Height = PetEnvelopeHeight;
                Left = bounds.Left;
                Top = bounds.Top;
            }
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
        var visibleBounds = CalculatePetSizeLogicalWindowBounds(scale, anchor);
        var displayedWidth = PetWidth * scale;
        var displayedHeight = PetHeight * scale;
        var horizontalAlignment = anchor.PreserveLeftEdge
            ? HorizontalAlignment.Left
            : anchor.PreserveRightEdge
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Center;
        var verticalAlignment = anchor.PreserveTopEdge
            ? VerticalAlignment.Top
            : VerticalAlignment.Bottom;
        var contentOffset = CalculatePetEnvelopeContentOffset(
            displayedWidth,
            displayedHeight,
            horizontalAlignment,
            verticalAlignment);
        return new Rect(
            SnapDipToPhysicalPixelAtScale(
                visibleBounds.Left - contentOffset.X,
                dpiScaleX),
            SnapDipToPhysicalPixelAtScale(
                visibleBounds.Top - contentOffset.Y,
                dpiScaleY),
            PetEnvelopeWidth,
            PetEnvelopeHeight);
    }

    private static Vector CalculatePetEnvelopeContentOffset(
        double displayedWidth,
        double displayedHeight,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment)
    {
        var remainingWidth = Math.Max(0, PetEnvelopeWidth - displayedWidth);
        var remainingHeight = Math.Max(0, PetEnvelopeHeight - displayedHeight);
        var offsetX = horizontalAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => remainingWidth,
            _ => remainingWidth / 2
        };
        var offsetY = verticalAlignment == VerticalAlignment.Top
            ? 0
            : remainingHeight;
        return new Vector(offsetX, offsetY);
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
            ? Math.Round(
                  value * dpiScale,
                  MidpointRounding.AwayFromZero) / dpiScale
            : Math.Round(value, MidpointRounding.AwayFromZero);

    private void BeginReminderPetSizeOverrideAt(long timestamp)
    {
        if (_isClosing || _isTransientPetSizeOverride)
        {
            return;
        }

        // Close a Track/Thumb gesture before reminder sizing takes ownership.
        // A later MouseUp is then idempotent and cannot commit the reminder's
        // transient preview session as if it belonged to the slider.
        _todoWindow.CompletePetSizeAdjustmentForInterruption();
        ConsumeLatestPetSizeInputAt(timestamp);
        var reuseTodoPreviewEnvelope =
            _petSizePreviewEnvelopePinnedForTodo &&
            _todoWindow.IsVisible;
        if (_isPetSizeAdjustmentActive)
        {
            _isPetSizeAdjustmentActive = false;
            _petSizeAdjustmentValueChanged = false;
            _petSizeCommitPending = false;
        }

        if (reuseTodoPreviewEnvelope)
        {
            // The Todo panel already owns the permanent maximum transparent
            // envelope, even before the user touches the size slider. Keep
            // that exact HWND surface and its current screen anchor while the
            // reminder grows to 140%; moving to the screen corner here would
            // detach the pet from an open editor and make it appear to jump.
            _reminderRestoreScale =
                NormalizePetSizeScale(_petSizeTargetScale);
            if (_petSizeSettingsDirty)
            {
                SaveSettings();
            }
        }
        else
        {
            _petSizePreviewEnvelopePinnedForTodo = false;
            if (_isPetSizePreviewSessionActive)
            {
                CommitPetSizePreviewSession(persist: true);
            }
            else if (_petSizeSettingsDirty)
            {
                SaveSettings();
            }

            // A non-Todo slider preview owns an anchor captured at its old
            // screen position. Commit it first, then establish the reminder
            // corner before the temporary preview captures its new anchor.
            MovePetToReminderCorner();
            _reminderRestoreScale = NormalizePetSizeScale(_petSizeScale);
        }

        _isTransientPetSizeOverride = true;
        _isRestoringReminderSize = false;
        _petSizeSettingsDirty = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();
        QueuePetSizeScaleTargetAt(MaximumPetSizeScale, timestamp);
        RefreshReminderBubbleOffset();
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

    private void EnsureReminderPetSizeOverrideAt(long timestamp)
    {
        if (!_isTransientPetSizeOverride)
        {
            BeginReminderPetSizeOverrideAt(timestamp);
            return;
        }

        // A recurring reminder may arrive while the previous completed page is
        // still shrinking. Retarget the same temporary override to 140%
        // without replacing the user's original scale.
        _reminderSizeCommitTimer.Stop();
        _isRestoringReminderSize = false;
        _petSizeSettingsDirty = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();
        QueuePetSizeScaleTargetAt(MaximumPetSizeScale, timestamp);
        RefreshReminderBubbleOffset();
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

        var keepTodoPreviewEnvelope =
            _petSizePreviewEnvelopePinnedForTodo &&
            _bubbleMode == BubbleMode.Todo &&
            _todoWindow.IsVisible;
        if (!keepTodoPreviewEnvelope &&
            (_activeClip is not null ||
             _edgeDock != EdgeDock.None ||
             _isEdgeRoaming))
        {
            _reminderSizeCommitTimer.Interval =
                TimeSpan.FromMilliseconds(16);
            _reminderSizeCommitTimer.Start();
            return;
        }

        if (_isPetSizePreviewSessionActive && !keepTodoPreviewEnvelope)
        {
            CommitPetSizePreviewSession(persist: false);
        }

        _isTransientPetSizeOverride = false;
        _isRestoringReminderSize = false;
        _petSizeSettingsDirty = false;
        _petSizeCommitPending = false;
        _petSizePersistTimer.Stop();
        if (keepTodoPreviewEnvelope)
        {
            // The reminder restore already ended at the user's scale inside a
            // maximum-size envelope. Reuse that exact surface for the visible
            // Todo panel instead of shrinking and immediately expanding the
            // layered HWND underneath its restored editor.
            _petSizeSettingsDirty = false;
        }
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

        if (_petSizePreviewEnvelopePinnedForTodo)
        {
            if (_petSizeSettingsDirty && SaveSettings())
            {
                _petSizeSettingsDirty = false;
            }

            _petSizeCommitPending = false;
            return;
        }

        if (_activeClip is not null ||
            _edgeDock != EdgeDock.None ||
            _isEdgeRoaming)
        {
            // Defensive guard for a stale/externally fired timer: native
            // bounds are committed only after the active visual state settles.
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

        if (persist && !_isTransientPetSizeOverride && _petSizeSettingsDirty)
        {
            var saved = SaveSettings();
            if (saved)
            {
                _petSizeSettingsDirty = false;
            }
        }
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
    }

    private void TodoWindow_TodoChanged(TodoItem item)
    {
        var oldIndex = _todos.IndexOf(item);
        if (item.IsCompleted && oldIndex >= 0 && oldIndex < _todos.Count - 1)
        {
            _todos.Move(oldIndex, _todos.Count - 1);
        }

        SaveTodos();
    }

    private void TodoWindow_TodoEdited(TodoItem item)
    {
        SaveTodos();
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
        }
    }

    private void TodoWindow_ScheduledTaskAddRequested(
        string text,
        DateTimeOffset dueAt,
        TimeSpan? repeatInterval,
        ScheduledRepeatRule? repeatRule,
        ScheduledQuietHours? quietHours)
    {
        var normalizedText = text.Trim();
        if (normalizedText.Length == 0)
        {
            return;
        }

        var now = _nowProvider();
        var normalizedRepeatInterval =
            ScheduledTaskStore.NormalizeRepeatInterval(repeatInterval);
        var normalizedQuietHours =
            repeatRule is not null ||
            normalizedRepeatInterval is { } interval &&
            interval > TimeSpan.Zero
                ? ScheduledQuietHoursSchedule.Normalize(quietHours)
                : null;
        var item = new ScheduledTaskItem
        {
            Id = Guid.NewGuid(),
            Text = normalizedText,
            DueAt = ScheduledTaskStore.NormalizeToWholeSecond(dueAt),
            CreatedAt = now,
            RepeatInterval = normalizedRepeatInterval,
            RepeatRule = repeatRule,
            QuietHours = normalizedQuietHours
        };
        InsertScheduledTaskSorted(item);
        SaveScheduledTasks();
        ProcessScheduledTasksAt(now);
    }

    private void TodoWindow_ScheduledTaskDeleteRequested(ScheduledTaskItem item)
    {
        if (!_scheduledTasks.Remove(item))
        {
            return;
        }

        var now = _nowProvider();
        RemoveDeletedScheduledTaskFromReminderState(item.Id);
        SaveScheduledTasks();
        var suspendedQuietPresentation =
            SuspendQuietScheduledTaskPresentationsAt(now);
        RebuildReminderQueueAt(now);

        if (_activeReminder is null)
        {
            if (!ShowNextQueuedReminderAt(now))
            {
                if (suspendedQuietPresentation ||
                    HasSuppressedQuietReminderStateAt(now))
                {
                    FinishQuietScheduledTaskSuspension();
                    ScheduleNextReminderAt(now);
                    return;
                }

                FinishReminderAfterScheduledTaskDeletion(now);
                return;
            }
        }

        RefreshActiveReminderPresentation(now);
        ScheduleNextReminderAt(now);
    }

    private void RemoveDeletedScheduledTaskFromReminderState(Guid itemId)
    {
        _queuedReminderIds.Remove(itemId);
        _activeReminderBatch.RemoveAll(item => item.Id == itemId);
        _visibleReminderOccurrences.RemoveAll(
            occurrence => occurrence.TaskId == itemId);
        _presentedReminderOccurrenceCounts.Remove(itemId);

        if (_activeReminder?.Id == itemId)
        {
            _activeReminder = _activeReminderBatch.Count > 0
                ? _activeReminderBatch[0]
                : null;
        }
    }

    private void FinishReminderAfterScheduledTaskDeletion(
        DateTimeOffset now)
    {
        _activeReminder = null;
        _activeReminderBatch.Clear();
        _visibleReminderOccurrences.Clear();
        _presentedReminderOccurrenceCounts.Clear();
        _totalReminderOccurrenceCount = 0;
        _reminderQueue.Clear();
        _queuedReminderIds.Clear();
        _isReminderPresentationDismissed = false;

        var wasReminderVisible =
            _isReminderActive || _bubbleMode == BubbleMode.Reminder;
        _isReminderActive = false;
        if (wasReminderVisible)
        {
            SetBubbleMode(
                _todoWindow.IsVisible
                    ? BubbleMode.Todo
                    : BubbleMode.None);
        }
        else
        {
            _reminderWindow?.HideSafely();
            _reminderWindow?.ClearPresentation();
        }

        RestoreReminderPetSizeAt(Stopwatch.GetTimestamp());
        ScheduleNextReminderAt(now);
    }

    private void TodoWindow_ScheduledTaskEditRequested(
        ScheduledTaskItem item,
        string text,
        DateTimeOffset dueAt,
        TimeSpan? repeatInterval,
        ScheduledRepeatRule? repeatRule,
        ScheduledQuietHours? quietHours)
    {
        if (_queuedReminderIds.Contains(item.Id) ||
            _presentedReminderOccurrenceCounts.GetValueOrDefault(
                item.Id) > 0)
        {
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
        var normalizedRepeatInterval =
            ScheduledTaskStore.NormalizeRepeatInterval(repeatInterval);
        item.RepeatInterval = normalizedRepeatInterval;
        item.RepeatRule = repeatRule;
        item.QuietHours =
            repeatRule is not null ||
            normalizedRepeatInterval is { } interval &&
            interval > TimeSpan.Zero
                ? ScheduledQuietHoursSchedule.Normalize(quietHours)
                : null;
        InsertScheduledTaskSorted(item);

        var now = _nowProvider();
        SaveScheduledTasks();
        ProcessScheduledTasksAt(now);
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
        var now = _nowProvider();
        PreloadUpcomingReminderAt(now);
        ProcessScheduledTasksAt(now);
    }

    private void ProcessScheduledTasksAt(DateTimeOffset now)
    {
        if (_isClosing)
        {
            return;
        }

        _scheduledTaskTimer.Stop();
        var suspendedQuietPresentation =
            SuspendQuietScheduledTaskPresentationsAt(now);
        RebuildReminderQueueAt(now);
        if (_activeReminder is null)
        {
            ShowNextQueuedReminderAt(now);
        }
        else
        {
            RefreshActiveReminderPresentation(now);
        }

        if (suspendedQuietPresentation && _activeReminder is null)
        {
            FinishQuietScheduledTaskSuspension();
        }

        ScheduleNextReminderAt(now);
    }

    private void RebuildReminderQueueAt(DateTimeOffset now)
    {
        _reminderQueue.Clear();
        _queuedReminderIds.Clear();
        if (_activeReminder is { } activeReminder)
        {
            if (_scheduledTasks.Contains(activeReminder))
            {
                _queuedReminderIds.Add(activeReminder.Id);
                foreach (var displayedItem in _activeReminderBatch)
                {
                    if (_scheduledTasks.Contains(displayedItem))
                    {
                        _queuedReminderIds.Add(displayedItem.Id);
                    }
                }
            }
            else
            {
                _activeReminder = null;
                _isReminderPresentationDismissed = false;
                _activeReminderBatch.Clear();
                _visibleReminderOccurrences.Clear();
                _presentedReminderOccurrenceCounts.Clear();
                _totalReminderOccurrenceCount = 0;
            }
        }

        foreach (var item in _scheduledTasks)
        {
            var hasPresentedBacklog =
                _presentedReminderOccurrenceCounts.GetValueOrDefault(
                    item.Id) > 0;
            if (item.DueAt > now && !hasPresentedBacklog)
            {
                // When no carried-forward batch exists, the collection's due
                // time ordering still gives us the normal fast exit. With a
                // carried batch we must keep scanning because a later task can
                // also own already-presented, unacknowledged occurrences.
                if (_presentedReminderOccurrenceCounts.Count == 0)
                {
                    break;
                }

                continue;
            }

            if (IsScheduledTaskQuietAt(item, now))
            {
                continue;
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

            if (IsScheduledTaskQuietAt(item, now))
            {
                _queuedReminderIds.Remove(item.Id);
                continue;
            }

            _upcomingReminderPreloadPageName = null;
            _activeReminder = item;
            if (_presentedReminderOccurrenceCounts.GetValueOrDefault(
                    item.Id) == 0)
            {
                _isReminderPresentationDismissed = false;
            }

            RefreshActiveReminderPresentation(now);
            return true;
        }

        return false;
    }

    private void RefreshActiveReminderPresentation(DateTimeOffset now)
    {
        if (_activeReminder is not { } activeReminder)
        {
            return;
        }

        var hasNewReminderOccurrences = false;
        _activeReminderBatch.RemoveAll(item =>
            !_scheduledTasks.Contains(item) ||
            IsScheduledTaskQuietAt(item, now));
        if (_activeReminderBatch.All(item => item.Id != activeReminder.Id))
        {
            _activeReminderBatch.Add(activeReminder);
        }

        foreach (var item in _reminderQueue)
        {
            if (_activeReminderBatch.All(existing => existing.Id != item.Id))
            {
                _activeReminderBatch.Add(item);
            }
        }

        _activeReminderBatch.Sort(CompareScheduledTasks);
        var activeIds = _activeReminderBatch
            .Select(item => item.Id)
            .ToHashSet();
        var quietIds = _scheduledTasks
            .Where(item => IsScheduledTaskQuietAt(item, now))
            .Select(item => item.Id)
            .ToHashSet();
        foreach (var staleId in _presentedReminderOccurrenceCounts.Keys
                     .Where(id =>
                         !activeIds.Contains(id) &&
                         !quietIds.Contains(id))
                     .ToArray())
        {
            _presentedReminderOccurrenceCounts.Remove(staleId);
        }
        _visibleReminderOccurrences.RemoveAll(
            occurrence => !activeIds.Contains(occurrence.TaskId));
        _totalReminderOccurrenceCount = 0;

        foreach (var item in _activeReminderBatch)
        {
            _queuedReminderIds.Add(item.Id);
            var dueCount = CalculateDueOccurrenceCount(item, now);
            var observedCount =
                _presentedReminderOccurrenceCounts.GetValueOrDefault(
                    item.Id);
            var previouslyObservedCount = observedCount;
            observedCount = Math.Max(observedCount, dueCount);
            hasNewReminderOccurrences |=
                observedCount > previouslyObservedCount;
            _presentedReminderOccurrenceCounts[item.Id] = observedCount;
            _totalReminderOccurrenceCount = SaturatingAdd(
                _totalReminderOccurrenceCount,
                observedCount);
            var visibleCount = _visibleReminderOccurrences.LongCount(
                occurrence => occurrence.TaskId == item.Id);
            AppendReminderOccurrences(
                item,
                visibleCount,
                observedCount);
        }

        if (_isReminderPresentationDismissed)
        {
            if (!hasNewReminderOccurrences)
            {
                return;
            }

            // A new occurrence is the only in-process event that reopens a
            // dismissed, still-unacknowledged stack.
            _isReminderPresentationDismissed = false;
        }

        UpdateReminderWindowPresentation();

        if (_isReminderActive && _bubbleMode == BubbleMode.Reminder)
        {
            if (hasNewReminderOccurrences)
            {
                ShowReminderWindow();
                RestartReminderAttentionAnimation();
            }
            else
            {
                RefreshReminderWindowPosition();
            }

            return;
        }

        _isReminderActive = true;
        EnsureReminderPetSizeOverrideAt(Stopwatch.GetTimestamp());
        SetBubbleMode(BubbleMode.Reminder);
    }

    private void RestartReminderAttentionAnimation()
    {
        if (_isClosing ||
            !_isReminderActive ||
            _bubbleMode != BubbleMode.Reminder)
        {
            return;
        }

        // A newly due occurrence must be noticeable even when the previous
        // reminder is still waiting for acknowledgement. Restarting the
        // megaphone hold sequence gives one fresh, bounded shake without
        // rebuilding the window, clearing its message stack, or disturbing an
        // open Todo/Scheduled editor.
        StartReminderHoldAnimation();
    }

    private void AppendReminderOccurrences(
        ScheduledTaskItem item,
        long visibleCount,
        long dueCount)
    {
        // Each task contributes at most its next 100 occurrences. The global
        // trim then keeps the earliest 100 across every task, so confirmation
        // advances only messages the user could actually read.
        var lastCandidateOffset = Math.Min(
            dueCount,
            SaturatingAdd(
                visibleCount,
                MaximumVisibleReminderOccurrences));
        for (var occurrenceOffset = visibleCount;
             occurrenceOffset < lastCandidateOffset;
             occurrenceOffset++)
        {
            if (!TryGetReminderOccurrenceDueAt(
                    item,
                    occurrenceOffset,
                    out var dueAt))
            {
                continue;
            }

            _visibleReminderOccurrences.Add(
                new ReminderOccurrence(
                    item.Id,
                    item.CreatedAt,
                    item.Text,
                    item.IsRecurring ? item.RepeatDisplayText : string.Empty,
                    dueAt,
                    occurrenceOffset));
        }

        _visibleReminderOccurrences.Sort(CompareReminderOccurrences);
        if (_visibleReminderOccurrences.Count >
            MaximumVisibleReminderOccurrences)
        {
            _visibleReminderOccurrences.RemoveRange(
                MaximumVisibleReminderOccurrences,
                _visibleReminderOccurrences.Count -
                MaximumVisibleReminderOccurrences);
        }
    }

    private static int CompareReminderOccurrences(
        ReminderOccurrence left,
        ReminderOccurrence right)
    {
        var dueComparison = left.DueAt.UtcDateTime.Ticks.CompareTo(
            right.DueAt.UtcDateTime.Ticks);
        if (dueComparison != 0)
        {
            return dueComparison;
        }

        var createdComparison =
            left.TaskCreatedAt.UtcDateTime.Ticks.CompareTo(
                right.TaskCreatedAt.UtcDateTime.Ticks);
        if (createdComparison != 0)
        {
            return createdComparison;
        }

        var taskComparison = left.TaskId.CompareTo(right.TaskId);
        return taskComparison != 0
            ? taskComparison
            : left.OccurrenceOffset.CompareTo(right.OccurrenceOffset);
    }

    private static bool TryGetReminderOccurrenceDueAt(
        ScheduledTaskItem item,
        long occurrenceOffset,
        out DateTimeOffset dueAt)
    {
        dueAt = default;
        if (occurrenceOffset < 0)
        {
            return false;
        }

        if (item.RepeatRule is { } repeatRule)
        {
            try
            {
                return ScheduledRepeatSchedule.TryGetOccurrence(
                    repeatRule,
                    checked(repeatRule.NextOrdinal + occurrenceOffset),
                    out dueAt);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (occurrenceOffset == 0)
        {
            dueAt = item.DueAt;
            return true;
        }

        var repeatInterval =
            ScheduledTaskStore.NormalizeRepeatInterval(item.RepeatInterval);
        if (repeatInterval is not { } interval)
        {
            return false;
        }

        try
        {
            dueAt = ScheduledTaskStore.NormalizeToWholeSecond(
                item.DueAt.AddTicks(
                    checked(occurrenceOffset * interval.Ticks)));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private void UpdateReminderWindowPresentation()
    {
        var overflowCount = Math.Max(
            0,
            _totalReminderOccurrenceCount -
            _visibleReminderOccurrences.Count);
        var title = _totalReminderOccurrenceCount == 1
            ? "定时任务到点啦"
            : $"{_totalReminderOccurrenceCount} 次提醒待确认";
        var entries = BuildReminderPresentationEntries();
        EnsureReminderWindow().SetPresentation(
            title,
            entries,
            overflowCount);
    }

    private string[] BuildReminderPresentationEntries()
    {
        var entries = new string[_visibleReminderOccurrences.Count];
        for (var index = 0;
             index < _visibleReminderOccurrences.Count;
             index++)
        {
            var occurrence = _visibleReminderOccurrences[index];
            var dueAtText = occurrence.DueAt.ToLocalTime()
                .ToString("M月d日 HH:mm:ss");
            var repeatText = occurrence.RepeatText.Length > 0
                ? $" · {occurrence.RepeatText}"
                : string.Empty;
            entries[index] =
                $"• {dueAtText}{repeatText}{Environment.NewLine}" +
                occurrence.Text;
        }

        return entries;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
        {
            return left;
        }

        return left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
    }

    private void MovePetToReminderCorner()
    {
        StopEdgeRoaming(
            scheduleNext: true,
            restoreIdleFrame: false,
            interrupted: true,
            immediate: true);
        ExitEdgePeek(
            restartAutomaticCountdown: false,
            restoreIdleFrame: false);

        if (_todoWindow.IsVisible)
        {
            return;
        }

        var workArea = MonitorWorkArea.GetForVisual(this, PetSizeViewbox);
        var visiblePetBounds = GetPetViewboxBoundsInScreenDips();
        _petSizeLogicalAnchor = null;
        MoveMainWindowTo(
            Left + workArea.Right - visiblePetBounds.Right,
            Top + workArea.Bottom - visiblePetBounds.Bottom);
        _todoWindowPositionCache.InvalidateGeometry();
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
        if (_activeReminder is null ||
            _visibleReminderOccurrences.Count == 0)
        {
            return;
        }

        var now = _nowProvider();
        _isReminderPresentationDismissed = false;
        var acknowledgedCounts = _visibleReminderOccurrences
            .GroupBy(occurrence => occurrence.TaskId)
            .ToDictionary(group => group.Key, group => group.LongCount());
        var acknowledgedItems = _activeReminderBatch
            .Where(item => acknowledgedCounts.ContainsKey(item.Id))
            .ToArray();
        var carriedForwardCounts =
            _presentedReminderOccurrenceCounts.ToDictionary(
                pair => pair.Key,
                pair => Math.Max(
                    0,
                    pair.Value -
                    acknowledgedCounts.GetValueOrDefault(pair.Key)));
        _activeReminder = null;
        _activeReminderBatch.Clear();
        foreach (var item in acknowledgedItems)
        {
            _queuedReminderIds.Remove(item.Id);
            _scheduledTasks.Remove(item);
            AdvanceAcknowledgedScheduledTask(
                item,
                acknowledgedCounts[item.Id]);
        }

        _visibleReminderOccurrences.Clear();
        _presentedReminderOccurrenceCounts.Clear();
        foreach (var item in _scheduledTasks)
        {
            var carriedCount =
                carriedForwardCounts.GetValueOrDefault(item.Id);
            if (carriedCount > 0)
            {
                _presentedReminderOccurrenceCounts[item.Id] = carriedCount;
            }
        }

        _totalReminderOccurrenceCount = 0;
        _reminderQueue.Clear();
        _queuedReminderIds.Clear();
        SaveScheduledTasks();

        RebuildReminderQueueAt(now);
        if (ShowNextQueuedReminderAt(now))
        {
            ScheduleNextReminderAt(now);
            return;
        }

        _isReminderActive = false;
        SetBubbleMode(
            _todoWindow.IsVisible
                ? BubbleMode.Todo
                : BubbleMode.None);
        RestoreReminderPetSizeAt(Stopwatch.GetTimestamp());
        ScheduleNextReminderAt(now);
    }

    private void AdvanceAcknowledgedScheduledTask(
        ScheduledTaskItem item,
        long acknowledgedCount)
    {
        if (acknowledgedCount <= 0)
        {
            InsertScheduledTaskSorted(item);
            return;
        }

        if (item.RepeatRule is { } repeatRule)
        {
            try
            {
                var nextOrdinal = checked(
                    repeatRule.NextOrdinal + acknowledgedCount);
                if (ScheduledRepeatSchedule.TryGetOccurrence(
                        repeatRule,
                        nextOrdinal,
                        out var nextDueAt))
                {
                    item.DueAt = ScheduledTaskStore.NormalizeToWholeSecond(
                        nextDueAt);
                    item.RepeatRule = repeatRule with
                    {
                        NextOrdinal = nextOrdinal
                    };
                    InsertScheduledTaskSorted(item);
                    return;
                }
            }
            catch (OverflowException)
            {
            }

            SuspendExhaustedScheduledTask(item);
            return;
        }

        var repeatInterval =
            ScheduledTaskStore.NormalizeRepeatInterval(item.RepeatInterval);
        if (repeatInterval is not { } interval)
        {
            return;
        }

        try
        {
            item.DueAt = ScheduledTaskStore.NormalizeToWholeSecond(
                item.DueAt.AddTicks(
                    checked(acknowledgedCount * interval.Ticks)));
            InsertScheduledTaskSorted(item);
        }
        catch (ArgumentOutOfRangeException)
        {
            SuspendExhaustedScheduledTask(item);
        }
        catch (OverflowException)
        {
            SuspendExhaustedScheduledTask(item);
        }
    }

    private void SuspendExhaustedScheduledTask(ScheduledTaskItem item)
    {
        var maximumWholeSecond =
            DateTimeOffset.MaxValue.AddTicks(
                -(DateTimeOffset.MaxValue.Ticks %
                  TimeSpan.TicksPerSecond));
        item.DueAt = maximumWholeSecond;
        item.RepeatInterval = null;
        item.RepeatRule = null;
        item.QuietHours = null;
        InsertScheduledTaskSorted(item);
    }

    private static string FormatReminderMessageLine(
        ScheduledTaskItem item,
        DateTimeOffset now)
    {
        return FormatReminderMessageLine(
            item,
            CalculateMissedOccurrenceCount(item, now));
    }

    private static string FormatReminderMessageLine(
        ScheduledTaskItem item,
        long missedCount)
    {
        var repeatText = item.IsRecurring
            ? $" · {item.RepeatDisplayText}"
            : string.Empty;
        var missedText = missedCount > 0
            ? $"（已错过 {missedCount} 次）"
            : string.Empty;
        return
            $"{item.DueAt.ToLocalTime():M月d日 HH:mm:ss}{repeatText}  " +
            $"{item.Text}{missedText}";
    }

    private static long CalculateMissedOccurrenceCount(
        ScheduledTaskItem item,
        DateTimeOffset now)
    {
        var elapsedTicks =
            now.UtcDateTime.Ticks - item.DueAt.UtcDateTime.Ticks;
        if (elapsedTicks <= MissedReminderGracePeriod.Ticks)
        {
            return 0;
        }

        return CalculateDueOccurrenceCount(item, now);
    }

    private static long CalculateDueOccurrenceCount(
        ScheduledTaskItem item,
        DateTimeOffset now)
    {
        if (item.RepeatRule is { } repeatRule &&
            ScheduledRepeatSchedule.TryEvaluate(
                repeatRule,
                item.DueAt,
                now,
                out var evaluation))
        {
            return evaluation.DueCount;
        }

        var elapsedTicks =
            now.UtcDateTime.Ticks - item.DueAt.UtcDateTime.Ticks;
        if (elapsedTicks < 0)
        {
            return 0;
        }

        var repeatInterval =
            ScheduledTaskStore.NormalizeRepeatInterval(item.RepeatInterval);
        return repeatInterval is { } interval
            ? 1L + elapsedTicks / interval.Ticks
            : 1L;
    }

    private void ScheduleNextReminderAt(DateTimeOffset now)
    {
        _scheduledTaskTimer.Stop();
        if (_isClosing)
        {
            return;
        }

        var nextDueAt = FindNextReminderDueAt(now);
        if (nextDueAt is null)
        {
            ClearUpcomingReminderPreload();
            return;
        }

        PreloadUpcomingReminderAt(now);
        _scheduledTaskTimer.Interval = _isReminderActive
            ? CalculateReminderProcessingDelay(now, nextDueAt.Value)
            : CalculateReminderWakeDelay(now, nextDueAt.Value);
        _scheduledTaskTimer.Start();
    }

    private DateTimeOffset? FindNextReminderDueAt(DateTimeOffset now)
    {
        DateTimeOffset? nextDueAt = null;
        foreach (var item in _scheduledTasks)
        {
            DateTimeOffset? candidate;
            var observedCount =
                _presentedReminderOccurrenceCounts.GetValueOrDefault(
                    item.Id);
            if (observedCount > 0)
            {
                candidate = item.IsRecurring &&
                            TryGetReminderOccurrenceDueAt(
                                item,
                                observedCount,
                                out var nextObservedOccurrence)
                    ? nextObservedOccurrence
                    : null;
            }
            else if (item.DueAt > now)
            {
                candidate = item.DueAt;
            }
            else if (item.IsRecurring)
            {
                var nextOccurrenceOffset =
                    CalculateDueOccurrenceCount(item, now);
                candidate = TryGetReminderOccurrenceDueAt(
                    item,
                    nextOccurrenceOffset,
                    out var nextOccurrence)
                    ? nextOccurrence
                    : null;
            }
            else
            {
                candidate = null;
            }

            if (IsScheduledTaskQuietAt(item, now) &&
                ScheduledQuietHoursSchedule.TryGetQuietEnd(
                    item.QuietHours,
                    now,
                    out var quietEnd) &&
                (observedCount > 0 ||
                 item.DueAt <= now ||
                 candidate is { } quietCandidate &&
                 quietCandidate <= quietEnd))
            {
                candidate = quietEnd;
            }
            else if (observedCount > 0 &&
                     item.IsRecurring &&
                     ScheduledQuietHoursSchedule.TryGetNextQuietStart(
                         item.QuietHours,
                         now,
                         out var quietStart) &&
                     (candidate is null || quietStart < candidate.Value))
            {
                candidate = quietStart;
            }

            if (candidate is { } dueAt &&
                (nextDueAt is null || dueAt < nextDueAt.Value))
            {
                nextDueAt = dueAt;
            }
        }

        return nextDueAt;
    }

    private static bool IsScheduledTaskQuietAt(
        ScheduledTaskItem item,
        DateTimeOffset now) =>
        item.IsRecurring &&
        ScheduledQuietHoursSchedule.IsQuietAt(item.QuietHours, now);

    private bool SuspendQuietScheduledTaskPresentationsAt(
        DateTimeOffset now)
    {
        if (_activeReminder is null && _activeReminderBatch.Count == 0)
        {
            return false;
        }

        var quietIds = _activeReminderBatch
            .Where(item => IsScheduledTaskQuietAt(item, now))
            .Select(item => item.Id)
            .ToHashSet();
        if (_activeReminder is { } activeReminder &&
            IsScheduledTaskQuietAt(activeReminder, now))
        {
            quietIds.Add(activeReminder.Id);
        }

        if (quietIds.Count == 0)
        {
            return false;
        }

        _activeReminderBatch.RemoveAll(item => quietIds.Contains(item.Id));
        _visibleReminderOccurrences.RemoveAll(
            occurrence => quietIds.Contains(occurrence.TaskId));
        foreach (var quietId in quietIds)
        {
            _queuedReminderIds.Remove(quietId);
        }

        if (_activeReminder is { } current &&
            quietIds.Contains(current.Id))
        {
            _activeReminder = _activeReminderBatch.Count > 0
                ? _activeReminderBatch[0]
                : null;
        }

        _totalReminderOccurrenceCount = 0;
        return true;
    }

    private bool HasSuppressedQuietReminderStateAt(
        DateTimeOffset now) =>
        _scheduledTasks.Any(item =>
            _presentedReminderOccurrenceCounts.ContainsKey(item.Id) &&
            IsScheduledTaskQuietAt(item, now));

    private void FinishQuietScheduledTaskSuspension()
    {
        if (_activeReminder is not null)
        {
            return;
        }

        var wasReminderVisible =
            _isReminderActive || _bubbleMode == BubbleMode.Reminder;
        _isReminderActive = false;
        if (wasReminderVisible)
        {
            SetBubbleMode(
                _todoWindow.IsVisible
                    ? BubbleMode.Todo
                    : BubbleMode.None);
            RestoreReminderPetSizeAt(Stopwatch.GetTimestamp());
        }
        else
        {
            _reminderWindow?.HideSafely();
            _reminderWindow?.ClearPresentation();
        }
    }

    private void PreloadUpcomingReminderAt(DateTimeOffset now)
    {
        if (_isClosing || _isReminderActive)
        {
            return;
        }

        var nextDueAt = FindNextReminderDueAt(now);
        if (nextDueAt is null)
        {
            ClearUpcomingReminderPreload();
            return;
        }

        var remaining = nextDueAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        if (remaining > ReminderSpritePreloadLeadTime)
        {
            ClearUpcomingReminderPreload();
            return;
        }

        var pageName = _reminderEnterClip.Frames[0].Image.PageName;
        if (string.Equals(
                _upcomingReminderPreloadPageName,
                pageName,
                StringComparison.Ordinal))
        {
            return;
        }

        _upcomingReminderPreloadPageName = pageName;
        RequestSpritePagePrefetch(pageName, urgent: true);
    }

    private void ClearUpcomingReminderPreload()
    {
        if (_upcomingReminderPreloadPageName is null)
        {
            return;
        }

        _upcomingReminderPreloadPageName = null;
        RequestIdleSpritePageTrim();
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

        var wakeDelay = remaining > ReminderSpritePreloadLeadTime
            ? remaining - ReminderSpritePreloadLeadTime
            : remaining;
        return wakeDelay > MaximumReminderWakeInterval
            ? MaximumReminderWakeInterval
            : wakeDelay;
    }

    private static TimeSpan CalculateReminderProcessingDelay(
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
        if (_isReminderActive)
        {
            HideTodoWindowVisual();
            RefreshReminderWindowPosition();
            return;
        }

        SetBubbleMode(BubbleMode.None);
    }

    private void TodoWindow_ExitRequested(object? sender, EventArgs e)
    {
        _todoWindow.AllowApplicationClose();
        Application.Current.Shutdown();
    }

    private void TodoWindow_ImeCompositionChanged(bool composing)
    {
        if (!composing)
        {
            ScheduleOutsideTodoClose();
        }
    }

    private void SaveTodos()
    {
        _todoStore.Save(_todos);
    }

    private void SaveScheduledTasks()
    {
        _scheduledTaskStore.Save(_scheduledTasks);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativeCursorPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCursorPoint
    {
        public int X;
        public int Y;
    }

    private enum BubbleMode
    {
        None,
        Cute,
        Todo,
        Reminder
    }

    private enum WorkState
    {
        Idle,
        Entering,
        Typing,
        Exiting
    }

    private enum EdgeRoamPhase
    {
        None,
        Boarding,
        Traveling,
        Disembarking
    }

    private enum EdgeDock
    {
        None,
        Left,
        Right,
        Bottom
    }

    private readonly record struct EdgeDockDragContext(
        EdgeDock OriginDock,
        Rect WorkArea,
        Rect StartWindowBounds,
        Rect StartContactBounds);

    private readonly record struct ReminderOccurrence(
        Guid TaskId,
        DateTimeOffset TaskCreatedAt,
        string Text,
        string RepeatText,
        DateTimeOffset DueAt,
        long OccurrenceOffset);

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

    private readonly record struct EdgeRoamOrientation(
        double ScaleX,
        double RotationDegrees);

    private readonly record struct MotionClipProfile(
        int SmoothFrameCount,
        int LoopCycleCount,
        TimeSpan EndpointHoldDuration,
        TimeSpan FrameInterval);

    private readonly record struct WorkModeIconVisualState(
        double SunOpacity,
        double MoonOpacity,
        double SunScale,
        double MoonScale,
        double SunRotationDegrees,
        double MoonRotationDegrees,
        double SunTranslateX,
        double SunTranslateY,
        double MoonTranslateX,
        double MoonTranslateY,
        double SunHaloOpacity,
        double MoonHaloOpacity,
        double SunHaloScale,
        double MoonHaloScale,
        double TwinkleOpacity,
        double TwinkleScale,
        double TwinkleRotationDegrees);

    private sealed record AnimationClip(
        string Message,
        string ActionName,
        AnimationFrame[] Frames,
        int ActionFrameIndex);

    private sealed record ActionTimeline(
        SpriteFrame[] Frames,
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
        Uri ResourceUri,
        int Width,
        int Height,
        int UncompressedByteCount,
        int PayloadByteCount,
        int CompressedByteCount,
        string Encoding,
        string ContentSha256,
        string DecodedSha256,
        byte[] ContentSha256Bytes,
        byte[] DecodedSha256Bytes,
        int UniqueSpriteCount,
        int[] FrameDescriptorValues,
        IReadOnlyDictionary<string, SpriteFrame> Frames)
    {
        private int _contentHashValidated;

        public bool IsContentHashValidated =>
            Volatile.Read(ref _contentHashValidated) != 0;

        public void MarkContentHashValidated()
        {
            Volatile.Write(ref _contentHashValidated, 1);
        }
    }

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

    private readonly record struct AnimationFrame(
        SpriteFrame Image,
        TimeSpan HoldDuration);
}
