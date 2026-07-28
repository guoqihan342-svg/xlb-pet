using System.Collections;
using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LubanDesktopPet;
using Rectangle = System.Windows.Shapes.Rectangle;

internal static class Program
{
    private const int LogicalPetWidth = 190;
    private const int LogicalPetHeight = 242;
    private const int RenderPixelWidth = 399;
    private const int RenderPixelHeight = 509;
    private const int ExpectedEdgePeekFrameCount = 48;
    private const int MinimumRoamSequenceFrameCount = 48;
    private const long MaximumDecodedSpritePageBytes = 24L * 1024L * 1024L;
    private const long MaximumSpritePagePayloadBytes = 32L * 1024L * 1024L;
    private const long ExpectedResidentSpritePageBudgetBytes = 128L * 1024L * 1024L;
    private const long ExpectedIdleSpritePageTargetBytes = 64L * 1024L * 1024L;
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags StaticFlags =
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            // 测试失败必须通过退出码报告。不要把断言异常交给 Windows 错误报告，
            // 否则会触发“UiStateChecks 已停止工作/CrashSender.exe”弹窗。
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Contains("--failure-exit-probe", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Intentional failure-exit probe.");
        }

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        try
        {
            if (args.Contains("--picker-preview", StringComparer.OrdinalIgnoreCase))
            {
                return RunScheduledPickerPreview(application);
            }

            AssertLoggingContract();
            RunCheck(nameof(AssertRuntimeJankSourceContract), AssertRuntimeJankSourceContract);

            if (args.Contains("--atlas-hash-only", StringComparer.OrdinalIgnoreCase))
            {
                RunCheck(nameof(AssertSpriteAtlasDecodedPageLimitFailClosed),
                    AssertSpriteAtlasDecodedPageLimitFailClosed);
                RunCheck(nameof(AssertSpritePagePayloadEncodingContract),
                    AssertSpritePagePayloadEncodingContract);
                return 0;
            }

            if (args.Contains("--scheduled-editor-only", StringComparer.OrdinalIgnoreCase))
            {
                RunCheck(nameof(AssertScheduledTaskTabContract),
                    AssertScheduledTaskTabContract);
                return 0;
            }

            if (args.Contains("--startup-only", StringComparer.OrdinalIgnoreCase))
            {
                RunCheck(nameof(AssertStartupRegistrationContract),
                    AssertStartupRegistrationContract);
                return 0;
            }

            if (args.Contains("--todo-cut-only", StringComparer.OrdinalIgnoreCase))
            {
                RunCheck(nameof(AssertTodoCutContract), AssertTodoCutContract);
                return 0;
            }

            if (args.Contains("--roam-source-only", StringComparer.OrdinalIgnoreCase))
            {
                RunCheck(nameof(AssertEdgeRoamingSourceContract),
                    AssertEdgeRoamingSourceContract);
                RunCheck(nameof(AssertEdgeRoamingRouteMathContract),
                    AssertEdgeRoamingRouteMathContract);
                RunCheck(nameof(AssertEdgeRoamRotationContract),
                    AssertEdgeRoamRotationContract);
                return 0;
            }

            RunCheck(nameof(AssertEdgeRoamingSourceContract),
                AssertEdgeRoamingSourceContract);
            RunCheck(nameof(AssertStartupRegistrationContract),
                AssertStartupRegistrationContract);

            var settingsDirectory = Path.Combine(
                Path.GetTempPath(),
                $"xlb-pet-ui-checks-{Guid.NewGuid():N}");
            MainWindow? window = null;

            try
            {
                Directory.CreateDirectory(settingsDirectory);
                window = new MainWindow
                {
                    Left = 200,
                    Top = 160,
                    ShowActivated = false
                };

                SetField(
                    window,
                    "_settingsStore",
                    new AppSettingsStore(Path.Combine(settingsDirectory, "settings.json")));
                SetField(
                    window,
                    "_scheduledTaskStore",
                    new ScheduledTaskStore(
                        Path.Combine(settingsDirectory, "scheduled-tasks.json")));
                GetField<ObservableCollection<ScheduledTaskItem>>(
                    window,
                    "_scheduledTasks").Clear();
                GetField<Queue<ScheduledTaskItem>>(
                    window,
                    "_reminderQueue").Clear();
                GetField<HashSet<Guid>>(
                    window,
                    "_queuedReminderIds").Clear();
                GetField<DispatcherTimer>(window, "_scheduledTaskTimer").Stop();

                if (args.Contains("--edge-dock-only", StringComparer.OrdinalIgnoreCase))
                {
                    RunCheck(nameof(AssertExactEdgeContactContract),
                        AssertExactEdgeContactContract);
                    RunCheck(nameof(AssertSupportedEdgeDockIntegration),
                        () => AssertSupportedEdgeDockIntegration(window));
                    RunCheck(nameof(AssertTodoClosePreservesEdgePeek),
                        () => AssertTodoClosePreservesEdgePeek(window));
                    return 0;
                }

                if (args.Contains("--resident-cache-only", StringComparer.OrdinalIgnoreCase))
                {
                    RunCheck(nameof(AssertResidentSpritePageWarmupContract),
                        () => AssertResidentSpritePageWarmupContract(window));
                    RunCheck(nameof(AssertIdleSpritePageTrimContract),
                        () => AssertIdleSpritePageTrimContract(window));
                    RunCheck(nameof(AssertIdleSpritePageCollectionGateContract),
                        () => AssertIdleSpritePageCollectionGateContract(window));
                    RunCheck(nameof(AssertStaleSpritePageCompletionIsDiscarded),
                        () => AssertStaleSpritePageCompletionIsDiscarded(window));
                    RunCheck(nameof(AssertRenderDeferredSpriteCacheMutationContract),
                        () => AssertRenderDeferredSpriteCacheMutationContract(window));
                    RunCheck(nameof(AssertSpriteCacheShutdownAndLateCompletionContract),
                        AssertSpriteCacheShutdownAndLateCompletionContract);
                    RunCheck(nameof(AssertSupersededPendingSpriteFrameDoesNotFlashBack),
                        () => AssertSupersededPendingSpriteFrameDoesNotFlashBack(window));
                    RunCheck(nameof(AssertColdSpritePageClipClockContract),
                        () => AssertColdSpritePageClipClockContract(window));
                    return 0;
                }

                if (args.Contains("--pet-size-only", StringComparer.OrdinalIgnoreCase))
                {
                    RunCheck(nameof(AssertTodoWindowLayoutApiAndIme), AssertTodoWindowLayoutApiAndIme);
                    RunCheck(nameof(AssertPetSizeScaleContract), () => AssertPetSizeScaleContract(window));
                    return 0;
                }

                if (args.Contains("--clip-clock-only", StringComparer.OrdinalIgnoreCase))
                {
                    RunCheck(nameof(AssertSingleBufferPremultipliedBlendContract),
                        () => AssertSingleBufferPremultipliedBlendContract(window));
                    RunCheck(nameof(AssertColdSpritePageClipClockContract),
                        () => AssertColdSpritePageClipClockContract(window));
                    RunCheck(nameof(AssertAbsoluteTimelineMathContract),
                        () => AssertAbsoluteTimelineMathContract(window));
                    return 0;
                }

                if (args.Contains("--reminder-only", StringComparer.OrdinalIgnoreCase))
                {
                    RunCheck(nameof(AssertScheduledTaskTabContract),
                        AssertScheduledTaskTabContract);
                    RunCheck(nameof(AssertScheduledTaskEditContract),
                        () => AssertScheduledTaskEditContract(window));
                    RunCheck(nameof(AssertScheduledReminderBatchContract),
                        () => AssertScheduledReminderBatchContract(window));
                    return 0;
                }

                if (args.Contains("--todo-only", StringComparer.OrdinalIgnoreCase))
                {
                    Invoke(window, "ApplyPetSizeScale", 1d, false, false);
                    RunCheck(nameof(AssertOwnedTodoWindowContract),
                        () => AssertOwnedTodoWindowContract(window));
                    RunCheck(nameof(AssertTodoWindowLayoutApiAndIme), AssertTodoWindowLayoutApiAndIme);
                    RunCheck(nameof(AssertTodoCutContract), AssertTodoCutContract);
                    RunCheck(nameof(AssertScheduledTaskTabContract),
                        AssertScheduledTaskTabContract);
                    RunCheck(nameof(AssertTodoReorderPersistenceContract),
                        () => AssertTodoReorderPersistenceContract(window));
                    return 0;
                }

                RunCheck(nameof(AssertResidentSpritePageWarmupContract),
                    () => AssertResidentSpritePageWarmupContract(window));
                RunCheck(nameof(AssertIdleSpritePageTrimContract),
                    () => AssertIdleSpritePageTrimContract(window));
                RunCheck(nameof(AssertIdleSpritePageCollectionGateContract),
                    () => AssertIdleSpritePageCollectionGateContract(window));
                RunCheck(nameof(AssertStaleSpritePageCompletionIsDiscarded),
                    () => AssertStaleSpritePageCompletionIsDiscarded(window));
                RunCheck(nameof(AssertRenderDeferredSpriteCacheMutationContract),
                    () => AssertRenderDeferredSpriteCacheMutationContract(window));
                RunCheck(nameof(AssertSpriteCacheShutdownAndLateCompletionContract),
                    AssertSpriteCacheShutdownAndLateCompletionContract);
                RunCheck(nameof(AssertDisplayFrameContract), () => AssertDisplayFrameContract(window));
                RunCheck(nameof(AssertSupersededPendingSpriteFrameDoesNotFlashBack),
                    () => AssertSupersededPendingSpriteFrameDoesNotFlashBack(window));
                RunCheck(nameof(AssertColdSpritePageClipClockContract),
                    () => AssertColdSpritePageClipClockContract(window));
                RunCheck(nameof(AssertSpriteAtlasDecodedPageLimitFailClosed),
                    AssertSpriteAtlasDecodedPageLimitFailClosed);
                RunCheck(nameof(AssertSpritePagePayloadEncodingContract),
                    AssertSpritePagePayloadEncodingContract);
                RunCheck(nameof(AssertHighDensityScalingAndDpiContract), () => AssertHighDensityScalingAndDpiContract(window));
                RunCheck(nameof(AssertMotionTimelineContract), () => AssertMotionTimelineContract(window));
                RunCheck(nameof(AssertNoRunContract), () => AssertNoRunContract(window));
                RunCheck(nameof(AssertAbsoluteTimelineMathContract), () => AssertAbsoluteTimelineMathContract(window));
                RunCheck(nameof(AssertExactEdgeContactContract), AssertExactEdgeContactContract);
                RunCheck(nameof(AssertEdgeRoamingRouteMathContract),
                    AssertEdgeRoamingRouteMathContract);
                RunCheck(nameof(AssertEdgeRoamRotationContract),
                    AssertEdgeRoamRotationContract);
                RunCheck(nameof(AssertAutomaticDeadlineContract),
                    () => AssertAutomaticDeadlineContract(window));
                RunCheck(nameof(AssertSnoreBubbleAnimationContract),
                    () => AssertSnoreBubbleAnimationContract(window));
                RunCheck(nameof(AssertSupportedEdgeDockIntegration), () => AssertSupportedEdgeDockIntegration(window));
                RunCheck(nameof(AssertTodoClosePreservesEdgePeek),
                    () => AssertTodoClosePreservesEdgePeek(window));
                RunCheck(nameof(AssertRandomActivityBag), () => AssertRandomActivityBag(window));
                RunCheck(nameof(AssertMonitorWorkAreaContract), () => AssertMonitorWorkAreaContract(window));
                RunCheck(nameof(AssertDisplaySettingsChangeRecovery), () => AssertDisplaySettingsChangeRecovery(window));
                RunCheck(nameof(AssertOwnedTodoWindowContract), () => AssertOwnedTodoWindowContract(window));
                RunCheck(nameof(AssertTodoWindowLayoutApiAndIme), AssertTodoWindowLayoutApiAndIme);
                RunCheck(nameof(AssertTodoCutContract), AssertTodoCutContract);
                RunCheck(nameof(AssertScheduledTaskTabContract),
                    AssertScheduledTaskTabContract);
                RunCheck(nameof(AssertTodoReorderPersistenceContract),
                    () => AssertTodoReorderPersistenceContract(window));
                RunCheck(nameof(AssertScheduledTaskEditContract),
                    () => AssertScheduledTaskEditContract(window));
                RunCheck(nameof(AssertScheduledReminderBatchContract),
                    () => AssertScheduledReminderBatchContract(window));
                RunCheck(nameof(AssertPetSizeScaleContract), () => AssertPetSizeScaleContract(window));
            }
            finally
            {
                try
                {
                    window?.Close();
                }
                finally
                {
                    try
                    {
                        Directory.Delete(settingsDirectory, recursive: true);
                    }
                    catch
                    {
                        // 测试临时目录清理失败不应掩盖产品契约结果。
                    }
                }
            }

            Console.WriteLine("UI state checks passed.");
            return 0;
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static int RunScheduledPickerPreview(Application application)
    {
        var preview = new TodoWindow
        {
            AllowsTransparency = false,
            Background = Brushes.White,
            Left = 420,
            ShowInTaskbar = true,
            Title = "小鲁班日期时间选择器预览",
            Top = 220,
            Topmost = false,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ScheduledTasks = new ObservableCollection<ScheduledTaskItem>()
        };
        Invoke(preview, "SelectTaskPage", true, false);
        preview.AllowApplicationClose();
        application.ShutdownMode = ShutdownMode.OnMainWindowClose;
        application.Run(preview);
        return 0;
    }

    private static void RunCheck(string name, Action check)
    {
        Console.WriteLine($"[RUN] {name}");
        check();
        Console.WriteLine($"[PASS] {name}");
    }

    private static void AssertDisplayFrameContract(MainWindow window)
    {
        var petImage = GetField<Rectangle>(window, "PetImage");
        var spriteBrush = GetField<ImageBrush>(window, "PetSpriteBrush");
        var viewport = GetField<Canvas>(window, "PetFrameViewport");
        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var residentPages = GetField<IDictionary>(window, "_residentSpritePages");
        var displayFrameBuffer = GetField<WriteableBitmap>(window, "_displayFrameBuffer");

        Assert(pageMap.Count > 0, "运行时必须登记至少一个图集分页");
        var maximumPageWidth = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Width"));
        var maximumPageHeight = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Height"));
        var maximumDecodedPageBytes = pageMap.Values.Cast<object>()
            .Max(page => checked(
                (long)GetProperty<int>(page, "Width") *
                GetProperty<int>(page, "Height") * 4));
        var idlePageNameAtEntry = GetSpriteFrameInfo(
            GetField<object>(window, "_idleFrame")).PageName;
        Assert(maximumDecodedPageBytes <= MaximumDecodedSpritePageBytes &&
               residentPages.Contains(idlePageNameAtEntry),
            $"单页不得超过24MiB且idle必须始终常驻；实际最大页 " +
            $"{maximumDecodedPageBytes / 1024d / 1024d:F2}MiB，" +
            $"当前resident页 {residentPages.Count}");
        AssertResidentSpriteCacheAccounting(window, "完整分页校验开始");
        var bitmapFields = typeof(MainWindow).GetFields(InstanceFlags)
                .Select(field => field.GetValue(window))
                .OfType<BitmapSource>()
                .ToArray();
        Assert(bitmapFields.Length == 1 &&
               bitmapFields.Contains(displayFrameBuffer),
            $"MainWindow只能常驻一个{RenderPixelWidth}×{RenderPixelHeight}" +
            "高密度显示位图；常驻解码页只能是托管Pbgra32数组，" +
            "不能额外常驻WPF分页位图");
        Assert(ReferenceEquals(petImage.Fill, spriteBrush) &&
               ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer) &&
               displayFrameBuffer.PixelWidth == RenderPixelWidth &&
               displayFrameBuffer.PixelHeight == RenderPixelHeight &&
               displayFrameBuffer.Format == PixelFormats.Pbgra32 &&
               !displayFrameBuffer.IsFrozen,
            $"PetImage必须永久使用唯一的{RenderPixelWidth}×{RenderPixelHeight} " +
            "Pbgra32高密度完整帧缓冲，不能直接裁切整张图集");
        Assert(spriteBrush.ViewboxUnits == BrushMappingMode.Absolute &&
               spriteBrush.Stretch == Stretch.Fill,
            "分页裁剪必须使用Absolute Viewbox并填充PetImage");
        Assert(window.FindName("PetImageBuffer") is null &&
               window.FindName("PetSpriteBufferBrush") is null &&
               window.FindName("PetImageOverlay") is null,
            "不得恢复旧双位图Surface或任何过渡Overlay图层");

        AssertClose(viewport.Width, LogicalPetWidth, "逻辑帧视口宽度");
        AssertClose(viewport.Height, LogicalPetHeight, "逻辑帧视口高度");
        Assert(viewport.ClipToBounds, "逻辑帧视口必须裁剪图集其余区域");
        Assert(string.Equals(
                window.FontFamily.Source,
                "Microsoft YaHei",
                StringComparison.OrdinalIgnoreCase),
            $"MainWindow 必须统一使用 Microsoft YaHei，实际 {window.FontFamily.Source}");

        var pages = GetDictionaryEntries(pageMap)
            .Select(entry => new RuntimePage(
                (string)entry.Key,
                GetProperty<string>(entry.Value!, "ResourcePath"),
                GetProperty<int>(entry.Value!, "Width"),
                GetProperty<int>(entry.Value!, "Height"),
                GetProperty<int>(entry.Value!, "UncompressedByteCount"),
                GetProperty<int>(entry.Value!, "PayloadByteCount"),
                GetProperty<string>(entry.Value!, "Encoding"),
                GetProperty<string>(entry.Value!, "ContentSha256"),
                GetProperty<string>(entry.Value!, "DecodedSha256"),
                GetProperty<IDictionary>(entry.Value!, "Frames"),
                entry.Value!))
            .ToArray();
        AssertSpritePagesManifestAndResourcesContract(pages);

        var totalPageFrames = 0;
        foreach (var page in pages)
        {
            var pageFrames = GetDictionaryEntries(page.Frames);
            Assert(pageFrames.Length > 0, $"分页 {page.Name} 不得为空");

            // The runtime only permits the constructor to decode synchronously;
            // this explicit private wrapper primes deterministic test data before
            // ShowStableFrame. Runtime page changes themselves remain async.
            Invoke(
                window,
                "LoadSpritePageIntoBuffer",
                page.Name,
                page.RuntimeValue);
            Invoke(window, "ShowStableFrame", pageFrames[0].Value);
            Assert(ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer),
                $"切换到 {page.Name} 后ImageSource引用不得改变");
            Assert(GetField<string>(window, "_loadedSpritePageName") == page.Name,
                $"切换后_loadedSpritePageName必须为 {page.Name}");
            var activePagePixels = GetField<byte[]>(window, "_spritePagePixels");
            AssertBufferMatchesPage(activePagePixels, page);

            foreach (var frameEntry in pageFrames)
            {
                totalPageFrames++;
                var frame = GetSpriteFrameInfo(frameEntry.Value!);
                Assert(frame.PageName == page.Name,
                    $"{frame.Name} 的PageName必须为 {page.Name}");
                Assert(frameEntry.Key is string key && key == frame.Name,
                    $"{page.Name} 帧字典键必须与SpriteFrame.Name一致");
                Assert(frame.X >= 0 && frame.Y >= 0 &&
                       frame.Width > 0 && frame.Height > 0 &&
                       frame.X + frame.Width <= page.Width &&
                       frame.Y + frame.Height <= page.Height,
                    $"{page.Name}/{frame.Name} 必须位于分页边界内");
                Assert(frame.DestinationX < RenderPixelWidth &&
                       frame.DestinationY < RenderPixelHeight &&
                       frame.DestinationX + frame.Width > 0 &&
                       frame.DestinationY + frame.Height > 0,
                    $"{page.Name}/{frame.Name} 必须与{RenderPixelWidth}×" +
                    $"{RenderPixelHeight}渲染区相交");

                Invoke(window, "ShowStableFrame", frameEntry.Value);
                Assert(ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer),
                    $"显示 {page.Name}/{frame.Name} 时ImageSource引用不得改变");
                Assert(GetField<string>(window, "_loadedSpritePageName") == page.Name,
                    $"同页切帧时_loadedSpritePageName必须保持 {page.Name}");
                AssertSpriteSurfaceConfigured(petImage, spriteBrush, frame);
                AssertNoSpriteSurfaceAnimations(petImage, spriteBrush, frame.Name);
            }

            AssertSamePageDoesNotRewrite(
                window,
                page,
                pageFrames,
                activePagePixels,
                spriteBrush);
        }

        var expectedPinnedPageNames = GetExpectedPinnedSpritePageNames(window);
        Assert(residentPages.Count < pages.Length &&
               expectedPinnedPageNames.All(residentPages.Contains) &&
               residentPages.Contains(GetField<string>(window, "_loadedSpritePageName")),
            $"逐页清单/像素校验后缓存必须保持有界，固定热页与当前页仍须常驻；" +
            $"resident={residentPages.Count}/{pages.Length}");
        AssertResidentSpriteCacheAccounting(window, "逐页清单与像素校验完成");

        using (var manifest = JsonDocument.Parse(File.ReadAllText(
                   FindWorkspaceFile("Assets", "luban-sprite-pages.json"))))
        {
            var declaredPageFrames = manifest.RootElement
                .GetProperty("pageFrameCount")
                .GetInt32();
            Assert(totalPageFrames == declaredPageFrames,
                $"运行时分页应动态覆盖清单声明的{declaredPageFrames}个PageFrame，" +
                $"实际 {totalPageFrames}");
        }
        AssertSameFrameReturnsEarly(
            window,
            petImage,
            spriteBrush,
            pages[0].Frames.Values.Cast<object>().First());
        AssertDirtyRectanglePixelContract(window);
        AssertSingleBufferPremultipliedBlendContract(window);
        AssertCompressedPageLoadPerformance(window, pages);
        AssertResidentSpriteCacheAccounting(window, "逐页热命中性能校验完成");
        var idleFrame = GetField<object>(window, "_idleFrame");
        var idlePageName = GetSpriteFrameInfo(idleFrame).PageName;
        var idlePage = pages.Single(page => page.Name == idlePageName);
        Invoke(
            window,
            "LoadSpritePageIntoBuffer",
            idlePage.Name,
            idlePage.RuntimeValue);
        Invoke(window, "ShowStableFrame", idleFrame);
    }

    private static void AssertDirtyRectanglePixelContract(MainWindow window)
    {
        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var offsetFrame = GetDictionaryEntries(pageMap)
            .SelectMany(page => GetDictionaryEntries(
                GetProperty<IDictionary>(page.Value!, "Frames")))
            .Select(frame => frame.Value!)
            .First(frame =>
            {
                var info = GetSpriteFrameInfo(frame);
                return info.X > 0 && info.Y > 0 &&
                       info.DestinationX >= 0 && info.DestinationY >= 0 &&
                       info.DestinationX + info.Width <= RenderPixelWidth &&
                       info.DestinationY + info.Height <= RenderPixelHeight;
            });
        PrimeSpritePageForFrame(window, offsetFrame);
        var offsetInfo = GetSpriteFrameInfo(offsetFrame);
        var offsetPage = GetDictionaryEntries(pageMap).Single(entry =>
            string.Equals(
                entry.Key as string,
                offsetInfo.PageName,
                StringComparison.Ordinal));
        var pageStride = GetProperty<int>(offsetPage.Value!, "Width") * 4;
        var pagePixels = GetField<byte[]>(window, "_spritePagePixels");
        var expectedOffsetCopy = new byte[RenderPixelWidth * RenderPixelHeight * 4];
        var actualOffsetCopy = new byte[expectedOffsetCopy.Length];
        var visibleOffsetBounds = new Int32Rect(
            offsetInfo.DestinationX,
            offsetInfo.DestinationY,
            offsetInfo.Width,
            offsetInfo.Height);
        var displayStride = RenderPixelWidth * 4;
        for (var row = 0; row < offsetInfo.Height; row++)
        {
            Buffer.BlockCopy(
                pagePixels,
                (offsetInfo.Y + row) * pageStride + offsetInfo.X * 4,
                expectedOffsetCopy,
                (offsetInfo.DestinationY + row) * displayStride +
                offsetInfo.DestinationX * 4,
                offsetInfo.Width * 4);
        }

        InvokeOverload(
            window,
            "CopyFramePixels",
            offsetFrame,
            actualOffsetCopy,
            visibleOffsetBounds);
        Assert(actualOffsetCopy.AsSpan().SequenceEqual(expectedOffsetCopy),
            $"非零图集source offset必须逐字节复制正确：{offsetInfo.PageName}/{offsetInfo.Name}, " +
            $"source=({offsetInfo.X},{offsetInfo.Y})");

        var idleFrame = GetField<object>(window, "_idleFrame");
        var idleInfo = GetSpriteFrameInfo(idleFrame);
        var negativeDestinationFrame = GetField<Array>(window, "_edgeLeftFrames").GetValue(0)!;
        var negativeInfo = GetSpriteFrameInfo(negativeDestinationFrame);
        Assert(negativeInfo.DestinationX < 0,
            "脏矩形裁剪回归必须使用负DestinationX的左边界姿势");
        Assert(!string.Equals(
                idleInfo.PageName,
                negativeInfo.PageName,
                StringComparison.Ordinal),
            "脏矩形跨页夹具必须使用两个不同的图集页");
        PrimeSpritePageForFrame(window, idleFrame);
        Assert(GetField<string>(window, "_loadedSpritePageName") == idleInfo.PageName,
            "写入待机帧前必须先加载待机图集页");
        SetField(window, "_directDisplayFrameBounds", null);
        Invoke(window, "WriteDirectSpriteFrame", idleFrame);
        PrimeSpritePageForFrame(window, negativeDestinationFrame);
        Assert(GetField<string>(window, "_loadedSpritePageName") == negativeInfo.PageName,
            "写入边缘帧前必须先加载边缘图集页");
        Invoke(window, "WriteDirectSpriteFrame", negativeDestinationFrame);
        var incrementalPixels = GetField<byte[]>(window, "_displayFramePixels");
        var fullReference = new byte[incrementalPixels.Length];
        InvokeOverload(window, "CopyFramePixels", negativeDestinationFrame, fullReference);
        Assert(incrementalPixels.AsSpan().SequenceEqual(fullReference),
            "不同bounds增量切到负Destination裁剪帧时，结果必须逐字节等于全清重绘参考");

        PrimeSpritePageForFrame(window, idleFrame);
        Assert(GetField<string>(window, "_loadedSpritePageName") == idleInfo.PageName,
            "切回待机帧前必须重新加载待机图集页");
        Invoke(window, "WriteDirectSpriteFrame", idleFrame);
        Array.Clear(fullReference);
        InvokeOverload(window, "CopyFramePixels", idleFrame, fullReference);
        Assert(incrementalPixels.AsSpan().SequenceEqual(fullReference),
            "不同bounds从负Destination帧切回较大普通帧时，旧像素清理必须逐字节等于全清重绘参考");
        SetField(window, "_currentSpriteFrame", null);
        Invoke(window, "ShowStableFrame", idleFrame);
    }

    private static void AssertHighDensityScalingAndDpiContract(MainWindow window)
    {
        var displayFrameBuffer = GetField<WriteableBitmap>(window, "_displayFrameBuffer");
        var spriteBrush = GetField<ImageBrush>(window, "PetSpriteBrush");
        var petImage = GetField<Rectangle>(window, "PetImage");
        var petVisual = GetField<Grid>(window, "PetVisual");

        AssertClose(petVisual.Width, LogicalPetWidth,
            "高密度渲染不得改变人物逻辑画布宽度");
        AssertClose(petVisual.Height, LogicalPetHeight,
            "高密度渲染不得改变人物逻辑画布高度");
        Assert(RenderOptions.GetBitmapScalingMode(petImage) == BitmapScalingMode.Fant,
            "高密度位图缩小时必须继续使用Fant高质量采样");

        try
        {
            Invoke(window, "ApplyPetSizeScale", 1.40d, false, false);
            var requiredPixelsAt96DpiWidth = (int)Math.Ceiling(window.Width);
            var requiredPixelsAt96DpiHeight = (int)Math.Ceiling(window.Height);
            var requiredPixelsAt150DpiWidth = (int)Math.Ceiling(window.Width * 1.5d);
            var requiredPixelsAt150DpiHeight = (int)Math.Ceiling(window.Height * 1.5d);
            AssertClose(window.Width, LogicalPetWidth * 1.40d,
                "140%时窗口逻辑宽度");
            AssertClose(window.Height, LogicalPetHeight * 1.40d,
                "140%时窗口逻辑高度");
            Assert(displayFrameBuffer.PixelWidth >= requiredPixelsAt96DpiWidth &&
                   displayFrameBuffer.PixelHeight >= requiredPixelsAt96DpiHeight,
                $"140%@100%DPI不得再上采样：窗口需要 " +
                $"{requiredPixelsAt96DpiWidth}×{requiredPixelsAt96DpiHeight}px，" +
                $"完整帧实际 {displayFrameBuffer.PixelWidth}×" +
                $"{displayFrameBuffer.PixelHeight}px");
            Assert(requiredPixelsAt150DpiWidth == RenderPixelWidth &&
                   requiredPixelsAt150DpiHeight == RenderPixelHeight &&
                   displayFrameBuffer.PixelWidth >= requiredPixelsAt150DpiWidth &&
                   displayFrameBuffer.PixelHeight >= requiredPixelsAt150DpiHeight,
                $"140%@150%DPI不得上采样：窗口需要 " +
                $"{requiredPixelsAt150DpiWidth}×{requiredPixelsAt150DpiHeight}px，" +
                $"完整帧实际 {displayFrameBuffer.PixelWidth}×" +
                $"{displayFrameBuffer.PixelHeight}px");
            AssertRectClose(
                spriteBrush.Viewbox,
                new Rect(0, 0, RenderPixelWidth, RenderPixelHeight),
                "140%高密度完整帧Viewbox");
        }
        finally
        {
            // 后续布局契约以默认逻辑尺寸为基线；测试不得受用户本机已保存的
            // 140% 设置影响。
            Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        }

        var manifestPath = FindWorkspaceFile("app.manifest");
        var manifest = XDocument.Load(manifestPath);
        var dpiAwareness = manifest
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "dpiAwareness")?
            .Value;
        var legacyDpiAware = manifest
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "dpiAware")?
            .Value;
        Assert(dpiAwareness is not null &&
               dpiAwareness.Split(',').Any(value =>
                   string.Equals(value.Trim(), "PerMonitorV2", StringComparison.OrdinalIgnoreCase)) &&
               string.Equals(legacyDpiAware?.Trim(), "true/pm", StringComparison.OrdinalIgnoreCase),
            "app.manifest必须声明PerMonitorV2，并保留true/pm兼容声明，避免异DPI副屏整窗位图缩放");

        var project = XDocument.Load(FindWorkspaceFile("DesktopPet.csproj"));
        var applicationManifest = project
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "ApplicationManifest")?
            .Value
            .Trim();
        Assert(string.Equals(applicationManifest, "app.manifest", StringComparison.OrdinalIgnoreCase),
            "DesktopPet.csproj必须通过ApplicationManifest引用app.manifest");

        var appXaml = XDocument.Load(FindWorkspaceFile("App.xaml"));
        var toolTipStyle = appXaml.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Style" &&
                ((string?)element.Attribute("TargetType"))?.Contains(
                    "ToolTip",
                    StringComparison.OrdinalIgnoreCase) == true);
        var toolTipFontSetter = toolTipStyle?.Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "Setter" &&
                string.Equals(
                    (string?)element.Attribute("Property"),
                    "FontFamily",
                    StringComparison.OrdinalIgnoreCase));
        Assert(string.Equals(
                (string?)toolTipFontSetter?.Attribute("Value"),
                "Microsoft YaHei",
                StringComparison.OrdinalIgnoreCase),
            "App.xaml必须为所有ToolTip统一设置Microsoft YaHei，覆盖字符串ToolTip弹层");
    }

    private static void AssertSupersededPendingSpriteFrameDoesNotFlashBack(
        MainWindow window)
    {
        var pageEntries = GetDictionaryEntries(
            GetField<IDictionary>(window, "_spritePages"));
        var hotPageEntry = pageEntries.First(entry =>
            GetProperty<IDictionary>(entry.Value!, "Frames").Count >= 2);
        var coldPageEntry = pageEntries.First(entry =>
            !Equals(entry.Key, hotPageEntry.Key));
        var hotFrames = GetDictionaryEntries(
            GetProperty<IDictionary>(hotPageEntry.Value!, "Frames"));
        var coldAtlasFrame = GetDictionaryEntries(
            GetProperty<IDictionary>(coldPageEntry.Value!, "Frames"))[0].Value!;
        var coldFrame = CloneSpriteFrameWithName(
            coldAtlasFrame,
            "Assets/luban-edge-synthetic-cold.png");
        var hotFrameA1 = hotFrames[0].Value!;
        var hotFrameA2 = hotFrames[1].Value!;
        var hotPageName = (string)hotPageEntry.Key;
        var coldPageName = (string)coldPageEntry.Key;

        WaitForSpritePagePrefetchToSettle(window);
        var deferredDispatchTimer = GetField<DispatcherTimer>(
            window,
            "_spritePagePrefetchDispatchTimer");
        deferredDispatchTimer.Stop();
        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_pendingSpriteFrameBlendDuration", TimeSpan.Zero);
        SetField(window, "_desiredSpritePageName", null);
        SetField(window, "_desiredSpritePageUrgent", false);
        SetField(window, "_renderDeferredSpritePageName", null);
        SetField(window, "_renderDeferredSpritePageUrgent", false);
        SetField(window, "_renderDeferredSpritePageCancellation", false);

        Invoke(
            window,
            "LoadSpritePageIntoBuffer",
            hotPageName,
            hotPageEntry.Value!);
        EvictResidentSpritePageForTest(window, coldPageName);
        Invoke(window, "ShowStableFrame", hotFrameA1);
        var pillowImage = GetField<Image>(window, "PillowImage");
        Assert(pillowImage.Visibility == Visibility.Visible && pillowImage.Opacity == 1d,
            "普通热页帧必须显示静态枕头层");

        // Reproduce the narrower Rendering -> one-shot timer race. The cold
        // request exists only in the deferred signal until the timer ticks;
        // a newer hot frame must erase it without ever creating a Task/CTS.
        SetField(window, "_isInsideVisualRenderingCallback", true);
        try
        {
            Invoke(window, "ShowStableFrame", coldFrame);
        }
        finally
        {
            SetField(window, "_isInsideVisualRenderingCallback", false);
        }

        Assert(Equals(GetRawField(window, "_pendingSpriteFrame"), coldFrame) &&
               string.Equals(
                   GetRawField(window, "_renderDeferredSpritePageName") as string,
                   coldPageName,
                   StringComparison.Ordinal) &&
               deferredDispatchTimer.IsEnabled &&
               GetRawField(window, "_spritePagePrefetchTask") is null &&
               pillowImage.Visibility == Visibility.Visible &&
               pillowImage.Opacity == 1d,
            "Rendering 中的冷页请求必须只唤醒一次性调度并保持旧帧枕头状态，不能提前闪目标页或创建后台任务");
        Invoke(window, "ShowStableFrame", hotFrameA2);
        Assert(GetRawField(window, "_pendingSpriteFrame") is null &&
               GetRawField(window, "_renderDeferredSpritePageName") is null &&
               !deferredDispatchTimer.IsEnabled &&
               GetRawField(window, "_spritePagePrefetchTask") is null &&
               Equals(GetRawField(window, "_currentSpriteFrame"), hotFrameA2),
            "Timer Tick 前被热页取代的 deferred 冷页必须彻底淘汰，不能延迟启动过时解码");
        Invoke(window, "SpritePagePrefetchDispatchTimer_Tick", null, EventArgs.Empty);
        Assert(GetRawField(window, "_spritePagePrefetchTask") is null,
            "已淘汰的 deferred 冷页即使触发空 Tick 也不得创建 Task/CTS");
        Invoke(window, "ShowStableFrame", hotFrameA1);

        // Repeat the same supersession while an independent idle-trim signal
        // shares the one-shot timer. Clearing the obsolete page request must
        // not stop the timer and lose the remaining work.
        SetField(window, "_isInsideVisualRenderingCallback", true);
        try
        {
            Invoke(window, "ShowStableFrame", coldFrame);
            Invoke(window, "RequestIdleSpritePageTrim");
        }
        finally
        {
            SetField(window, "_isInsideVisualRenderingCallback", false);
        }

        Assert(Equals(GetRawField(window, "_pendingSpriteFrame"), coldFrame) &&
               GetField<bool>(window, "_residentSpritePageTrimPending") &&
               GetField<bool>(window, "_residentSpritePageIdleTrimPending") &&
               deferredDispatchTimer.IsEnabled,
            "deferred冷页与idle trim必须能共享同一个dispatcher timer信号");
        Invoke(window, "ShowStableFrame", hotFrameA1);
        Assert(GetRawField(window, "_pendingSpriteFrame") is null &&
               GetRawField(window, "_renderDeferredSpritePageName") is null &&
               GetField<bool>(window, "_residentSpritePageTrimPending") &&
               GetField<bool>(window, "_residentSpritePageIdleTrimPending") &&
               deferredDispatchTimer.IsEnabled,
            "热页淘汰obsolete deferred请求时不得停止仍承载idle trim的timer信号");
        Invoke(window, "SpritePagePrefetchDispatchTimer_Tick", null, EventArgs.Empty);
        Assert(!GetField<bool>(window, "_residentSpritePageTrimPending") &&
               !GetField<bool>(window, "_residentSpritePageIdleTrimPending") &&
               !deferredDispatchTimer.IsEnabled &&
               GetField<long>(window, "_residentSpritePageBytes") <=
               (long)(typeof(MainWindow).GetField(
                   "SpritePageIdleResidentTargetBytes",
                   StaticFlags)!.GetValue(null) ?? 0L),
            "保留下来的dispatcher tick必须消费idle trim信号并完成64MiB裁剪");
        ResetSpritePageCollectionTestState(window);

        // A1 -> cold C1: keep A1 visible while C starts decoding in the
        // background and records C1 as the pending pose.
        Invoke(window, "ShowStableFrame", coldFrame);
        Assert(Equals(GetRawField(window, "_pendingSpriteFrame"), coldFrame) &&
               string.Equals(
                   GetRawField(window, "_desiredSpritePageName") as string,
                   coldPageName,
                   StringComparison.Ordinal),
            "请求冷页C1时必须保持热页A1并记录pending=C1");

        // Before C's UI completion callback can run, a newer A2 request is
        // immediately displayable from the still-loaded hot page.
        Invoke(window, "ShowStableFrame", hotFrameA2);
        Assert(GetRawField(window, "_pendingSpriteFrame") is null &&
               GetRawField(window, "_desiredSpritePageName") is null &&
               Equals(GetRawField(window, "_currentSpriteFrame"), hotFrameA2),
            "热页A2取代冷页C1后必须立即清除旧pending/demand并保持current=A2");

        // Let C's canceled/already-completed task and its queued Dispatcher
        // callback fully settle, then emulate the next Rendering retry.
        var completionDeadline = Stopwatch.StartNew();
        while (GetRawField(window, "_spritePagePrefetchTask") is not null &&
               completionDeadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
        }

        Assert(GetRawField(window, "_spritePagePrefetchTask") is null,
            "被A2取代的冷页C任务必须在3秒内完成取消/代际收敛");
        Invoke(window, "TryShowPendingSpriteFrame");
        Assert(GetRawField(window, "_pendingSpriteFrame") is null &&
               Equals(GetRawField(window, "_currentSpriteFrame"), hotFrameA2) &&
               string.Equals(
                   GetRawField(window, "_loadedSpritePageName") as string,
                   hotPageName,
                   StringComparison.Ordinal),
            "冷页C完成回调不得在下一次Rendering闪回C1；必须继续显示A2");

        // Once the exact same cold target is actually resident and committed,
        // its edge-pose pillow state must change atomically with current frame.
        Invoke(window, "ShowStableFrame", coldFrame);
        if (GetRawField(window, "_pendingSpriteFrame") is not null)
        {
            WaitForPrefetchedSpritePage(window, coldFrame);
            Invoke(window, "TryShowPendingSpriteFrame");
        }

        Assert(Equals(GetRawField(window, "_currentSpriteFrame"), coldFrame) &&
               pillowImage.Visibility == Visibility.Visible &&
               pillowImage.Opacity == 0d,
            "冷页目标只有在像素真正提交后才能同步隐藏枕头");
        Invoke(window, "ShowStableFrame", hotFrameA1);
        Assert(pillowImage.Visibility == Visibility.Visible && pillowImage.Opacity == 1d,
            "返回普通常驻页时必须与帧提交同步恢复枕头");

        var idleFrame = GetField<object>(window, "_idleFrame");
        var idlePageName = GetSpriteFrameInfo(idleFrame).PageName;
        var idlePageEntry = pageEntries.Single(entry =>
            string.Equals((string)entry.Key, idlePageName, StringComparison.Ordinal));
        Invoke(
            window,
            "LoadSpritePageIntoBuffer",
            idlePageName,
            idlePageEntry.Value!);
        Invoke(window, "ShowStableFrame", idleFrame);
    }

    private static void AssertResidentSpritePageWarmupContract(MainWindow window)
    {
        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var pageNames = GetDictionaryEntries(pageMap)
            .Select(entry => (string)entry.Key)
            .ToArray();
        Assert(pageNames.Length >= 3,
            "常驻缓存回归至少需要idle、固定热页和按需动作页");

        var residentPages = GetField<IDictionary>(window, "_residentSpritePages");
        var idlePageName = GetField<string>(window, "_loadedSpritePageName");
        var idlePixels = GetField<byte[]>(window, "_spritePagePixels");
        var residentBudgetBytes = (long)(typeof(MainWindow).GetField(
                "SpritePageResidentBudgetBytes",
                StaticFlags)!.GetValue(null) ?? 0L);
        var pinnedPageNames = GetField<HashSet<string>>(
            window,
            "_pinnedSpritePageNames");
        var warmupOrder = GetField<string[]>(window, "_spritePageWarmupOrder");
        var expectedPinnedPageNames = GetExpectedPinnedSpritePageNames(window);
        var expectedWarmupOrder = BuildExpectedSpritePageWarmupOrder(window);
        var residentLru = GetField<LinkedList<string>>(
            window,
            "_residentSpritePageLru");

        Assert(residentBudgetBytes == ExpectedResidentSpritePageBudgetBytes,
            $"解码分页常驻预算必须固定为128MiB，实际 " +
            $"{residentBudgetBytes / 1024d / 1024d:F2}MiB");
        Assert(residentPages.Count == 1 &&
               residentPages.Contains(idlePageName) &&
               ReferenceEquals(
                   GetProperty<byte[]>(residentPages[idlePageName]!, "Pixels"),
                   idlePixels) &&
               GetField<long>(window, "_residentSpritePageBytes") == idlePixels.LongLength &&
               residentLru.SequenceEqual([idlePageName]),
            "首屏前只能同步常驻当前idle页，且字节账与LRU必须同时登记该页");
        Assert(pinnedPageNames.SetEquals(expectedPinnedPageNames),
            "永久pinned集合必须只包含完整idle/wake链分页");
        Assert(warmupOrder.Length == 0 && expectedWarmupOrder.Length == 0,
            "启动warmup顺序必须为空，构造阶段只同步加载idle页");
        AssertResidentSpriteCacheAccounting(window, "首屏缓存");

        var reminderPageNames = GetField<Array>(window, "_reminderEnterFrames")
            .Cast<object>()
            .Concat(GetField<Array>(window, "_reminderHoldFrames").Cast<object>())
            .Select(frame => GetSpriteFrameInfo(frame).PageName)
            .ToHashSet(StringComparer.Ordinal);
        Assert(reminderPageNames.Count > 0 &&
               reminderPageNames.All(pageName =>
                   !pinnedPageNames.Contains(pageName) &&
                   !(bool)Invoke(
                       window,
                       "IsSpritePageProtected",
                       pageName,
                       null)!),
            "提醒页在非提醒期间不得永久pinned或被其他静态条件保护");
        SetField(window, "_isReminderActive", true);
        Assert(reminderPageNames.All(pageName =>
                (bool)Invoke(
                    window,
                    "IsSpritePageProtected",
                    pageName,
                    null)!),
            "提醒页必须仅在_isReminderActive期间动态保护");
        SetField(window, "_isReminderActive", false);
        Assert(reminderPageNames.All(pageName =>
                !(bool)Invoke(
                    window,
                    "IsSpritePageProtected",
                    pageName,
                    null)!),
            "提醒结束后必须立即解除所有提醒页的动态保护");

        var urgentPageName = pageNames
            .Where(name => !expectedPinnedPageNames.Contains(name) &&
                           !expectedWarmupOrder.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => GetSpritePageByteCount(pageMap, name))
            .First();

        SetField(window, "_spritePageWarmupEnabled", true);
        Invoke(window, "ResumeSpritePageWarmup");
        Assert(GetRawField(window, "_desiredSpritePageName") is null &&
               GetRawField(window, "_spritePagePrefetchTask") is null &&
               GetField<int>(window, "_spritePageWarmupIndex") == 0,
            "空warmup顺序不得启动任何后台解码任务");

        Invoke(window, "RequestSpritePagePrefetch", urgentPageName, true);
        Assert(string.Equals(
                   GetRawField(window, "_desiredSpritePageName") as string,
                   urgentPageName,
                   StringComparison.Ordinal) &&
               GetField<bool>(window, "_desiredSpritePageUrgent") &&
               GetRawField(window, "_spritePagePrefetchTask") is Task,
            "空warmup不得阻止紧急动作页启动单个后台解码任务");

        var deadline = Stopwatch.StartNew();
        while ((GetRawField(window, "_spritePagePrefetchTask") is not null ||
                GetRawField(window, "_desiredSpritePageName") is not null ||
                GetField<int>(window, "_spritePageWarmupIndex") < warmupOrder.Length) &&
               deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(5));
            Thread.Yield();
        }

        Assert(GetRawField(window, "_spritePagePrefetchTask") is null &&
               GetRawField(window, "_desiredSpritePageName") is null &&
               GetField<int>(window, "_spritePageWarmupIndex") == warmupOrder.Length,
            "紧急页预取必须在20秒内完成，空warmup不得追加其他页");
        var expectedResidentPageNames = new HashSet<string>(
            [idlePageName, urgentPageName],
            StringComparer.Ordinal);
        Assert(expectedResidentPageNames.SetEquals(
                   GetDictionaryEntries(residentPages)
                       .Select(entry => (string)entry.Key)),
            "紧急页必须进入缓存，按名称固定但尚未使用的wake页不得被空warmup提前加载");
        Assert(string.Equals(
                   GetField<string>(window, "_loadedSpritePageName"),
                   idlePageName,
                   StringComparison.Ordinal) &&
               ReferenceEquals(GetField<byte[]>(window, "_spritePagePixels"), idlePixels),
            "后台预热只能发布常驻数组，不能擅自切换当前显示页");

        AssertResidentSpriteCacheAccounting(window, "空warmup与紧急页预取完成");

        SetField(window, "_spritePageWarmupEnabled", false);
        SetField(window, "_desiredSpritePageName", null);
        SetField(window, "_desiredSpritePageUrgent", false);
        SetField(
            window,
            "_spritePagePrefetchGeneration",
            GetField<int>(window, "_spritePagePrefetchGeneration") + 1);
        Invoke(window, "RequestSpritePagePrefetchCancellation");
        var shutdownDeadline = Stopwatch.StartNew();
        while (GetRawField(window, "_spritePagePrefetchTask") is not null &&
               shutdownDeadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(5));
        }

        Assert(GetRawField(window, "_spritePagePrefetchTask") is null,
            "聚焦测试结束时后台预热必须可取消并干净收敛");

        // Establish two large unprotected pages, touch A after B, then add
        // further pages until pressure chooses a victim. B must be the first of
        // the pair evicted; a dictionary-only FIFO or insertion-order policy
        // would incorrectly remove A as well or keep B.
        var lruCandidates = pageNames
            .Where(name => !expectedPinnedPageNames.Contains(name) &&
                           !expectedWarmupOrder.Contains(name, StringComparer.Ordinal) &&
                           !string.Equals(name, urgentPageName, StringComparison.Ordinal))
            .OrderByDescending(name => GetSpritePageByteCount(pageMap, name))
            .ToArray();
        Assert(lruCandidates.Length >= 5,
            "LRU压力测试至少需要五个非固定、非预热动作页");
        var recentlyTouchedPageName = lruCandidates[0];
        var olderPageName = lruCandidates[1];
        LoadSpritePageForTest(window, recentlyTouchedPageName);
        LoadSpritePageForTest(window, olderPageName);
        LoadSpritePageForTest(window, idlePageName);
        LoadSpritePageForTest(window, recentlyTouchedPageName);
        LoadSpritePageForTest(window, idlePageName);

        var observedLruEviction = false;
        foreach (var pageName in lruCandidates.Skip(2))
        {
            LoadSpritePageForTest(window, pageName);
            LoadSpritePageForTest(window, idlePageName);
            AssertResidentSpriteCacheAccounting(window, $"LRU加入 {pageName}");
            if (!residentPages.Contains(olderPageName) ||
                !residentPages.Contains(recentlyTouchedPageName))
            {
                Assert(!residentPages.Contains(olderPageName) &&
                       residentPages.Contains(recentlyTouchedPageName),
                    $"LRU必须先驱逐较旧页 {olderPageName}，并保留重新触达页 " +
                    recentlyTouchedPageName);
                observedLruEviction = true;
                break;
            }
        }

        Assert(observedLruEviction,
            "128MiB压力必须实际触发一次可观察的LRU驱逐");

        // Discover the largest real click clip from its distinct decoded page
        // footprint instead of depending on a clip index or current atlas
        // pagination. This keeps the protection proof valid as assets evolve.
        // Use it to force the cache close to its
        // budget. A small page outside that clip is first protected as the
        // current frame and then as the pending frame; both states must survive
        // trimming along with every page referenced by the active clip.
        var activeClip = GetField<Array>(window, "_reactionClips")
            .Cast<object>()
            .MaxBy(clip => GetClipPageNames(clip)
                .Sum(pageName => GetSpritePageByteCount(pageMap, pageName)))!;
        var activeClipPages = GetClipPageNames(activeClip);
        var pinnedAndMaximumClipPages = expectedPinnedPageNames
            .Concat(activeClipPages)
            .ToHashSet(StringComparer.Ordinal);
        var pinnedAndMaximumClipBytes = pinnedAndMaximumClipPages.Sum(
            pageName => GetSpritePageByteCount(pageMap, pageName));
        Assert(pinnedAndMaximumClipBytes <= residentBudgetBytes,
            "永久idle页与按distinct分页字节动态选出的最大动作clip必须可同时容纳于" +
            $"128MiB预算：{pinnedAndMaximumClipBytes / 1024d / 1024d:F2}MiB");
        var currentOrPendingPageName = pageNames
            .Where(name => !expectedPinnedPageNames.Contains(name) &&
                           !activeClipPages.Contains(name))
            .OrderBy(name => GetSpritePageByteCount(pageMap, name))
            .First();
        var currentOrPendingFrame = GetFirstSpriteFrameOnPage(
            window,
            currentOrPendingPageName);
        LoadSpritePageForTest(window, currentOrPendingPageName);
        Invoke(window, "ShowStableFrame", currentOrPendingFrame);
        SetField(window, "_activeClip", activeClip);
        PrimeAllClipPagesForTest(window, GetClipFrames(activeClip));
        Invoke(window, "TrimResidentSpritePagesToBudget", (object?)null);
        var protectedCurrentPages = expectedPinnedPageNames
            .Concat(activeClipPages)
            .Append(currentOrPendingPageName)
            .ToHashSet(StringComparer.Ordinal);
        Assert(protectedCurrentPages.All(residentPages.Contains),
            "LRU压力不得驱逐固定热页、当前显示页或活动clip引用的任一分页");
        AssertResidentSpriteCacheAccounting(window, "活动clip与当前页保护");

        var idleFrame = GetField<object>(window, "_idleFrame");
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
        SetField(window, "_pendingSpriteFrame", currentOrPendingFrame);
        var pressurePageName = pageNames
            .Where(name => !protectedCurrentPages.Contains(name))
            .OrderBy(name => GetSpritePageByteCount(pageMap, name))
            .First();
        LoadSpritePageForTest(window, pressurePageName);
        LoadSpritePageForTest(window, idlePageName);
        Invoke(window, "TrimResidentSpritePagesToBudget", (object?)null);
        Assert(residentPages.Contains(currentOrPendingPageName) &&
               activeClipPages.All(residentPages.Contains) &&
               expectedPinnedPageNames.All(residentPages.Contains),
            "LRU压力不得驱逐待显示页、活动clip页或固定热页");
        AssertResidentSpriteCacheAccounting(window, "活动clip与pending页保护");

        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_activeClip", null);
        SetField(window, "_activeFrameIndex", -1);
        SetField(window, "_activeClipStartedTimestamp", 0L);
        SetField(window, "_activeFrameDeadlineTimestamp", 0L);
        Invoke(window, "ClearDeferredActiveClipClock");
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
        Invoke(window, "TrimResidentSpritePagesToBudget", (object?)null);
        SetField(window, "_failedSpritePageName", null);
        Assert(expectedPinnedPageNames.All(residentPages.Contains),
            "压力测试清理后固定热页仍必须常驻");
        AssertResidentSpriteCacheAccounting(window, "LRU压力测试清理");
    }

    private static void AssertIdleSpritePageTrimContract(MainWindow window)
    {
        WaitForSpritePagePrefetchToSettle(window);
        PrepareIdleSpriteCollectionBaseline(window);
        ResetSpritePageCollectionTestState(window);

        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var residentPages = GetField<IDictionary>(window, "_residentSpritePages");
        var idleTargetBytes = (long)(typeof(MainWindow).GetField(
                "SpritePageIdleResidentTargetBytes",
                StaticFlags)!.GetValue(null) ?? 0L);
        var residentBudgetBytes = (long)(typeof(MainWindow).GetField(
                "SpritePageResidentBudgetBytes",
                StaticFlags)!.GetValue(null) ?? 0L);
        Assert(idleTargetBytes == ExpectedIdleSpritePageTargetBytes &&
               residentBudgetBytes == ExpectedResidentSpritePageBudgetBytes,
            "动作结束后的idle常驻目标必须是64MiB，活动软预算必须保持128MiB");

        var idleFrame = GetField<object>(window, "_idleFrame");
        var idlePageName = GetSpriteFrameInfo(idleFrame).PageName;
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
        foreach (var wakeFrame in GetField<Array>(window, "_wakeFrames")
                     .Cast<object>())
        {
            PrimeSpritePageForFrame(window, wakeFrame);
        }

        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
        var pinnedPageNames = GetExpectedPinnedSpritePageNames(window);
        var protectedPageName = GetDictionaryEntries(pageMap)
            .Select(entry => (string)entry.Key)
            .Where(pageName => !pinnedPageNames.Contains(pageName))
            .OrderBy(pageName => GetSpritePageByteCount(pageMap, pageName))
            .First(pageName => pinnedPageNames
                .Append(pageName)
                .Distinct(StringComparer.Ordinal)
                .Sum(name => GetSpritePageByteCount(pageMap, name)) <=
                idleTargetBytes);
        var protectedFrame = GetFirstSpriteFrameOnPage(
            window,
            protectedPageName);

        foreach (var pageName in GetDictionaryEntries(pageMap)
                     .Select(entry => (string)entry.Key)
                     .Where(pageName => !pinnedPageNames.Contains(pageName) &&
                                        !string.Equals(
                                            pageName,
                                            protectedPageName,
                                            StringComparison.Ordinal))
                     .OrderByDescending(pageName =>
                         GetSpritePageByteCount(pageMap, pageName)))
        {
            if (GetField<long>(window, "_residentSpritePageBytes") >
                idleTargetBytes + 12L * 1024L * 1024L)
            {
                break;
            }

            LoadSpritePageForTest(window, pageName);
        }

        LoadSpritePageForTest(window, protectedPageName);
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
        SetField(window, "_pendingSpriteFrame", protectedFrame);
        SetField(
            window,
            "_pendingSpriteFrameBlendDuration",
            TimeSpan.FromMilliseconds(120));

        var residentBytesBefore = GetField<long>(
            window,
            "_residentSpritePageBytes");
        var collectionDebtBefore = GetField<long>(
            window,
            "_spritePageEvictedBytesSinceCollection");
        Assert(residentBytesBefore > idleTargetBytes,
            "idle回收测试必须先建立超过64MiB的真实解码页缓存压力");
        var protectedPageNames = pinnedPageNames
            .Append(idlePageName)
            .Append(protectedPageName)
            .ToHashSet(StringComparer.Ordinal);
        Assert(protectedPageNames.Sum(pageName =>
                   GetSpritePageByteCount(pageMap, pageName)) <= idleTargetBytes,
            "永久idle页、当前idle页和pending保护页必须可共同容纳在64MiB目标内");

        Invoke(window, "TrimResidentSpritePagesToIdleTarget");
        var residentBytesAfter = GetField<long>(
            window,
            "_residentSpritePageBytes");
        Assert(residentBytesAfter <= idleTargetBytes &&
               protectedPageNames.All(residentPages.Contains),
            "idle回收必须把缓存裁到64MiB以内，且不得丢失永久、当前或pending保护页");
        Assert(GetField<long>(window, "_spritePageEvictedBytesSinceCollection") ==
               collectionDebtBefore + residentBytesBefore - residentBytesAfter,
            "idle回收释放的每个resident字节必须精确累计为Gen2回收债务");
        AssertResidentSpriteCacheAccounting(window, "64MiB idle回收与保护");

        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_pendingSpriteFrameBlendDuration", TimeSpan.Zero);
        Invoke(window, "TrimResidentSpritePagesToIdleTarget");
        Assert(GetField<long>(window, "_residentSpritePageBytes") <=
               idleTargetBytes &&
               pinnedPageNames.All(residentPages.Contains),
            "pending保护释放后的idle重试必须保持64MiB目标与永久idle页");
        ResetSpritePageCollectionTestState(window);
    }

    private static void AssertIdleSpritePageCollectionGateContract(
        MainWindow window)
    {
        WaitForSpritePagePrefetchToSettle(window);
        PrepareIdleSpriteCollectionBaseline(window);
        ResetSpritePageCollectionTestState(window);
        Assert((bool)Invoke(window, "CanRunIdleSpritePageCollection")!,
            "完全空闲的桌宠必须允许进入证据驱动Gen2回收门禁");

        var arbitraryPageName = GetDictionaryEntries(
                GetField<IDictionary>(window, "_spritePages"))
            .Select(entry => (string)entry.Key)
            .First(pageName => !string.Equals(
                pageName,
                GetField<string>(window, "_loadedSpritePageName"),
                StringComparison.Ordinal));
        var arbitraryFrame = GetFirstSpriteFrameOnPage(window, arbitraryPageName);
        var completedPageTask = CreateCompletedSpritePageLoadTask(
            window,
            arbitraryPageName);
        var reactionClip = GetField<Array>(window, "_reactionClips").GetValue(0)!;
        var blockers = new (string FieldName, object Value, string Scenario)[]
        {
            ("_isClosing", true, "关闭"),
            ("_isInsideVisualRenderingCallback", true, "Rendering回调"),
            ("_activeClip", reactionClip, "动作"),
            ("_isReminderActive", true, "提醒"),
            ("_dragInteractionActive", true, "拖动"),
            ("_pointerDown", true, "按下拖动前态"),
            ("_isPetSizeTransitioning", true, "缩放过渡"),
            ("_isPetSizePreviewSessionActive", true, "缩放预览"),
            ("_isPetSizeAdjustmentActive", true, "缩放输入"),
            ("_petSizeTargetUpdatePending", true, "缩放目标待提交"),
            ("_petSizeCommitPending", true, "缩放保存待提交"),
            ("_isFrameBlending", true, "帧过渡"),
            ("_isPillowBreathing", true, "枕头呼吸"),
            ("_bubbleMode", GetNestedEnum("BubbleMode", "Todo"), "Todo窗口"),
            ("_edgeDock", GetNestedEnum("EdgeDock", "Left"), "边缘探头"),
            ("_pendingSpriteFrame", arbitraryFrame, "待显示冷页"),
            ("_spritePagePrefetchTask", completedPageTask, "分页预取"),
            ("_desiredSpritePageName", arbitraryPageName, "分页请求"),
            ("_renderDeferredSpritePageName", arbitraryPageName, "Rendering延迟请求"),
            ("_renderDeferredSpritePageFailureName", arbitraryPageName, "Rendering延迟失败"),
            ("_renderDeferredSpritePageCancellation", true, "Rendering延迟取消"),
            ("_residentSpritePageTrimPending", true, "Rendering延迟trim"),
            ("_upcomingReminderPreloadPageName", arbitraryPageName, "到期前提醒图集预取")
        };

        foreach (var blocker in blockers)
        {
            var originalValue = GetRawField(window, blocker.FieldName);
            SetField(window, blocker.FieldName, blocker.Value);
            Assert(!(bool)Invoke(window, "CanRunIdleSpritePageCollection")!,
                $"{blocker.Scenario}期间必须禁止发起后台Gen2回收");
            SetField(window, blocker.FieldName, originalValue);
            Assert((bool)Invoke(window, "CanRunIdleSpritePageCollection")!,
                $"清除{blocker.Scenario}阻塞后空闲门禁必须恢复");
        }

        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        Invoke(window, "StartPillowBreathing");
        Assert(GetField<bool>(window, "_isPillowBreathing") &&
               GetField<long>(window, "_pillowBreathingDueTimestamp") >
                   Stopwatch.GetTimestamp() &&
               !(bool)Invoke(window, "CanRunIdleSpritePageCollection")!,
            "真实枕头待机占位期间必须阻止Gen2回收");
        Invoke(window, "StopPillowBreathing");
        Assert(!GetField<bool>(window, "_isPillowBreathing") &&
               GetField<long>(window, "_pillowBreathingDueTimestamp") == 0 &&
               !automaticTimer.IsEnabled &&
               (bool)Invoke(window, "CanRunIdleSpritePageCollection")!,
            "停止枕头待机必须清理timer并恢复空闲Gen2门禁");

        var thresholdBytes = (long)(typeof(MainWindow).GetField(
                "SpritePageCollectionThresholdBytes",
                StaticFlags)!.GetValue(null) ?? 0L);
        var collectionDelay = (TimeSpan)(typeof(MainWindow).GetField(
                "SpritePageCollectionDelay",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var retryDelay = (TimeSpan)(typeof(MainWindow).GetField(
                "SpritePageCollectionRetryDelay",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var minimumInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "MinimumSpritePageCollectionInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var collectionTimer = GetField<DispatcherTimer>(
            window,
            "_spritePageCollectionTimer");
        Assert(thresholdBytes == 48L * 1024L * 1024L &&
               collectionDelay == TimeSpan.FromSeconds(1) &&
               retryDelay == TimeSpan.FromSeconds(5) &&
               minimumInterval == TimeSpan.FromSeconds(30),
            "idle Gen2门禁必须保持48MiB阈值、1秒延迟、5秒忙重试与30秒最短间隔");

        SetField(
            window,
            "_spritePageEvictedBytesSinceCollection",
            thresholdBytes - 1);
        Invoke(window, "ScheduleSpritePageCollectionIfNeeded");
        Assert(!collectionTimer.IsEnabled,
            "累计淘汰不足48MiB时不得启动Gen2 timer");

        SetField(window, "_spritePageEvictedBytesSinceCollection", thresholdBytes);
        SetField(window, "_lastSpritePageCollectionTimestamp", 0L);
        Invoke(window, "ScheduleSpritePageCollectionIfNeeded");
        Assert(collectionTimer.IsEnabled && collectionTimer.Interval == collectionDelay,
            "累计淘汰达到48MiB且完全空闲时必须先延迟1秒再评估Gen2");
        collectionTimer.Stop();

        SetField(
            window,
            "_lastSpritePageCollectionTimestamp",
            Stopwatch.GetTimestamp());
        Invoke(window, "ScheduleSpritePageCollectionIfNeeded");
        Assert(collectionTimer.IsEnabled &&
               collectionTimer.Interval >= TimeSpan.FromSeconds(29) &&
               collectionTimer.Interval <= minimumInterval,
            "上次请求后30秒内必须按剩余时间节流，不得每个动作都请求Gen2");
        collectionTimer.Stop();

        SetField(window, "_lastSpritePageCollectionTimestamp", 0L);
        SetField(window, "_spritePageCollectionInProgress", false);
        SetField(window, "_activeClip", reactionClip);
        Invoke(window, "SpritePageCollectionTimer_Tick", null, EventArgs.Empty);
        Assert(collectionTimer.IsEnabled &&
               collectionTimer.Interval == retryDelay &&
               !GetField<bool>(window, "_spritePageCollectionInProgress") &&
               GetField<long>(window, "_lastSpritePageCollectionTimestamp") == 0 &&
               GetField<long>(window, "_spritePageEvictedBytesSinceCollection") ==
               thresholdBytes,
            "timer到点若动作仍忙，只能5秒后重试，不能发起GC或扣除债务");
        SetField(window, "_activeClip", null);
        collectionTimer.Stop();

        SetField(
            window,
            "_spritePageEvictedBytesSinceCollection",
            thresholdBytes - 1);
        Invoke(window, "SpritePageCollectionTimer_Tick", null, EventArgs.Empty);
        Assert(!collectionTimer.IsEnabled &&
               !GetField<bool>(window, "_spritePageCollectionInProgress") &&
               GetField<long>(window, "_lastSpritePageCollectionTimestamp") == 0,
            "timer到点时债务低于48MiB必须直接退出，不能发起GC");

        ResetSpritePageCollectionTestState(window);
    }

    private static void PrepareIdleSpriteCollectionBaseline(MainWindow window)
    {
        SetField(window, "_isClosing", false);
        SetField(window, "_isInsideVisualRenderingCallback", false);
        SetField(window, "_activeClip", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_dragInteractionActive", false);
        SetField(window, "_pointerDown", false);
        SetField(window, "_isPetSizeTransitioning", false);
        SetField(window, "_isPetSizePreviewSessionActive", false);
        SetField(window, "_isPetSizeAdjustmentActive", false);
        SetField(window, "_petSizeTargetUpdatePending", false);
        SetField(window, "_petSizeCommitPending", false);
        SetField(window, "_isFrameBlending", false);
        SetField(window, "_isPillowBreathing", false);
        SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
        SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_pendingSpriteFrameBlendDuration", TimeSpan.Zero);
        SetField(window, "_desiredSpritePageName", null);
        SetField(window, "_desiredSpritePageUrgent", false);
        SetField(window, "_renderDeferredSpritePageName", null);
        SetField(window, "_renderDeferredSpritePageUrgent", false);
        SetField(window, "_renderDeferredSpritePageFailureName", null);
        SetField(window, "_renderDeferredSpritePageFailureReason", null);
        SetField(window, "_renderDeferredSpritePageCancellation", false);
        SetField(window, "_residentSpritePageTrimPending", false);
        SetField(window, "_residentSpritePageIdleTrimPending", false);
        SetField(window, "_upcomingReminderPreloadPageName", null);
        SetField(window, "_spritePageWarmupEnabled", false);
    }

    private static void ResetSpritePageCollectionTestState(MainWindow window)
    {
        GetField<DispatcherTimer>(window, "_spritePageCollectionTimer").Stop();
        SetField(window, "_spritePageEvictedBytesSinceCollection", 0L);
        SetField(window, "_lastSpritePageCollectionTimestamp", 0L);
        SetField(window, "_spritePageCollectionDebtAtRequest", 0L);
        SetField(window, "_spritePageCollectionGenerationAtRequest", 0);
        SetField(window, "_spritePageCollectionPollCount", 0);
        SetField(window, "_spritePageCollectionInProgress", false);
        SetField(
            window,
            "_lastObservedSpritePageCollectionGeneration",
            GC.CollectionCount(GC.MaxGeneration));
    }

    private static HashSet<string> GetExpectedPinnedSpritePageNames(
        MainWindow window)
    {
        return GetField<Array>(window, "_wakeFrames")
            .Cast<object>()
            .Append(GetField<object>(window, "_idleFrame"))
            .Select(frame => GetSpriteFrameInfo(frame).PageName)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string[] BuildExpectedSpritePageWarmupOrder(MainWindow window)
    {
        _ = window;
        return [];
    }

    private static long GetSpritePageByteCount(IDictionary pageMap, string pageName)
    {
        return GetProperty<int>(pageMap[pageName]!, "UncompressedByteCount");
    }

    private static void LoadSpritePageForTest(MainWindow window, string pageName)
    {
        WaitForSpritePagePrefetchToSettle(window);
        var pageMap = GetField<IDictionary>(window, "_spritePages");
        Invoke(window, "LoadSpritePageIntoBuffer", pageName, pageMap[pageName]!);
        Assert(GetField<IDictionary>(window, "_residentSpritePages").Contains(pageName),
            $"测试装载后分页 {pageName} 必须进入resident cache");
    }

    private static object GetFirstSpriteFrameOnPage(
        MainWindow window,
        string pageName)
    {
        var page = GetField<IDictionary>(window, "_spritePages")[pageName]!;
        return GetDictionaryEntries(GetProperty<IDictionary>(page, "Frames"))[0].Value!;
    }

    private static HashSet<string> GetClipPageNames(object clip)
    {
        return GetClipFrames(clip)
            .Cast<object>()
            .Select(frame => GetProperty<object>(frame, "Image"))
            .Select(frame => GetSpriteFrameInfo(frame).PageName)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertResidentSpriteCacheAccounting(
        MainWindow window,
        string stage)
    {
        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var residentPages = GetField<IDictionary>(window, "_residentSpritePages");
        var lru = GetField<LinkedList<string>>(window, "_residentSpritePageLru");
        var entries = GetDictionaryEntries(residentPages);
        var calculatedBytes = entries.Sum(entry =>
            GetProperty<byte[]>(entry.Value!, "Pixels").LongLength);
        var trackedBytes = GetField<long>(window, "_residentSpritePageBytes");
        var budgetBytes = (long)(typeof(MainWindow).GetField(
                "SpritePageResidentBudgetBytes",
                StaticFlags)!.GetValue(null) ?? 0L);
        var lruNames = lru.ToArray();
        var lruNodes = new Dictionary<string, LinkedListNode<string>>(
            StringComparer.Ordinal);
        for (var node = lru.First; node is not null; node = node.Next)
        {
            Assert(lruNodes.TryAdd(node.Value, node),
                $"{stage} LRU不得出现同名节点 {node.Value}");
        }

        Assert(trackedBytes == calculatedBytes,
            $"{stage} resident字节账必须等于实际Pixels总长：" +
            $"tracked={trackedBytes}, actual={calculatedBytes}");
        Assert(trackedBytes <= budgetBytes,
            $"{stage} resident cache不得超过128MiB预算：" +
            $"{trackedBytes / 1024d / 1024d:F2}MiB");
        Assert(lruNames.Length == entries.Length &&
               lruNames.Distinct(StringComparer.Ordinal).Count() == lruNames.Length &&
               lruNames.ToHashSet(StringComparer.Ordinal).SetEquals(
                   entries.Select(entry => (string)entry.Key)),
            $"{stage} LRU必须与resident字典一一对应且不得含重复或悬空节点");

        foreach (var entry in entries)
        {
            var pageName = (string)entry.Key;
            var pixels = GetProperty<byte[]>(entry.Value!, "Pixels");
            var residentLruNode = GetProperty<LinkedListNode<string>>(
                entry.Value!,
                "LruNode");
            Assert(pixels.LongLength == GetSpritePageByteCount(pageMap, pageName) &&
                   GetProperty<long>(entry.Value!, "ByteCount") == pixels.LongLength,
                $"{stage} 分页 {pageName} 必须只保留一份清单精确长度的解码数组");
            Assert(string.Equals(
                       residentLruNode.Value,
                       pageName,
                       StringComparison.Ordinal) &&
                   ReferenceEquals(residentLruNode.List, lru) &&
                   lruNodes.TryGetValue(pageName, out var linkedListNode) &&
                   ReferenceEquals(residentLruNode, linkedListNode),
                $"{stage} 分页 {pageName} 的LruNode必须与同名链表节点为同一对象");
        }
    }

    private static void AssertStaleSpritePageCompletionIsDiscarded(
        MainWindow window)
    {
        WaitForSpritePagePrefetchToSettle(window);
        ResetSpritePageCollectionTestState(window);
        SetField(window, "_spritePageWarmupEnabled", false);
        SetField(window, "_desiredSpritePageName", null);
        SetField(window, "_desiredSpritePageUrgent", false);

        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var residentPages = GetField<IDictionary>(window, "_residentSpritePages");
        var residentLru = GetField<LinkedList<string>>(
            window,
            "_residentSpritePageLru");
        var loadedPageName = GetField<string>(window, "_loadedSpritePageName");
        var stalePageName = GetDictionaryEntries(pageMap)
            .Select(entry => (string)entry.Key)
            .Where(pageName => !string.Equals(
                pageName,
                loadedPageName,
                StringComparison.Ordinal))
            .First(pageName => !GetField<HashSet<string>>(
                window,
                "_pinnedSpritePageNames").Contains(pageName));
        EvictResidentSpritePageForTest(window, stalePageName);

        // Decode a real page into an already-successful Task, then make its
        // captured generation stale before invoking the UI completion method
        // directly. No worker continuation or dispatcher timing is involved.
        var completedTask = CreateCompletedSpritePageLoadTask(window, stalePageName);
        Assert(completedTask.IsCompletedSuccessfully,
            "受控过期分页任务必须先成功完成，不能以取消或故障代替成功结果回归");
        var cancellation = new CancellationTokenSource();
        var staleGeneration = GetField<int>(
            window,
            "_spritePagePrefetchGeneration");
        SetField(window, "_spritePagePrefetchTask", completedTask);
        SetField(window, "_spritePagePrefetchCancellation", cancellation);
        SetField(window, "_spritePagePrefetchPageName", stalePageName);

        var residentNamesBefore = GetDictionaryEntries(residentPages)
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var lruBefore = residentLru.ToArray();
        var residentBytesBefore = GetField<long>(
            window,
            "_residentSpritePageBytes");
        SetField(
            window,
            "_spritePagePrefetchGeneration",
            staleGeneration + 1);

        Invoke(
            window,
            "CompleteSpritePagePrefetch",
            stalePageName,
            staleGeneration,
            cancellation,
            completedTask);

        Assert(!residentPages.Contains(stalePageName) &&
               !residentLru.Contains(stalePageName) &&
               GetField<long>(window, "_residentSpritePageBytes") ==
               residentBytesBefore &&
               residentNamesBefore.SetEquals(GetDictionaryEntries(residentPages)
                   .Select(entry => (string)entry.Key)) &&
               residentLru.SequenceEqual(lruBefore),
            $"成功但过期的分页 {stalePageName} 不得写回字典、LRU或resident字节账");
        Assert(GetRawField(window, "_spritePagePrefetchTask") is null &&
               GetRawField(window, "_spritePagePrefetchCancellation") is null &&
               GetRawField(window, "_spritePagePrefetchPageName") is null,
            "过期成功任务完成回调必须只收敛在途槽位，不得留下已完成任务引用");
        AssertResidentSpriteCacheAccounting(window, "成功过期任务丢弃");
    }

    private static void AssertRenderDeferredSpriteCacheMutationContract(
        MainWindow window)
    {
        WaitForSpritePagePrefetchToSettle(window);
        SetField(window, "_spritePageWarmupEnabled", false);
        SetField(window, "_failedSpritePageName", null);
        SetField(window, "_renderDeferredSpritePageFailureName", null);
        SetField(window, "_renderDeferredSpritePageFailureReason", null);
        SetField(window, "_residentSpritePageTrimPending", false);
        SetField(window, "_residentSpritePageIdleTrimPending", false);

        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var residentPages = GetField<IDictionary>(window, "_residentSpritePages");
        var residentLru = GetField<LinkedList<string>>(
            window,
            "_residentSpritePageLru");
        var loadedPageName = GetField<string>(window, "_loadedSpritePageName");
        var failurePageName = GetDictionaryEntries(pageMap)
            .Select(entry => (string)entry.Key)
            .First(pageName => !string.Equals(
                pageName,
                loadedPageName,
                StringComparison.Ordinal));
        var pendingFrame = GetFirstSpriteFrameOnPage(window, failurePageName);
        SetField(window, "_pendingSpriteFrame", pendingFrame);
        SetField(
            window,
            "_pendingSpriteFrameBlendDuration",
            TimeSpan.FromMilliseconds(120));
        SetField(window, "_desiredSpritePageName", failurePageName);
        SetField(window, "_desiredSpritePageUrgent", true);

        var residentNamesBefore = GetDictionaryEntries(residentPages)
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var lruBefore = residentLru.ToArray();
        var residentBytesBefore = GetField<long>(
            window,
            "_residentSpritePageBytes");
        var dispatchTimer = GetField<DispatcherTimer>(
            window,
            "_spritePagePrefetchDispatchTimer");
        dispatchTimer.Stop();

        SetField(window, "_isInsideVisualRenderingCallback", true);
        try
        {
            Invoke(window, "RequestResidentSpritePageTrim");
            Invoke(window, "RequestIdleSpritePageTrim");
            Invoke(
                window,
                "HandleSpritePagePrefetchFailure",
                failurePageName,
                "synthetic Rendering failure");
        }
        finally
        {
            SetField(window, "_isInsideVisualRenderingCallback", false);
        }

        Assert(GetField<bool>(window, "_residentSpritePageTrimPending") &&
               GetField<bool>(window, "_residentSpritePageIdleTrimPending") &&
               string.Equals(
                   GetRawField(window, "_renderDeferredSpritePageFailureName")
                       as string,
                   failurePageName,
                   StringComparison.Ordinal) &&
               string.Equals(
                   GetRawField(window, "_renderDeferredSpritePageFailureReason")
                       as string,
                   "synthetic Rendering failure",
                   StringComparison.Ordinal) &&
               dispatchTimer.IsEnabled,
            "Rendering内普通/idle trim与失败处理只能发布延迟标志并启动既有dispatcher tick");
        Assert(residentNamesBefore.SetEquals(GetDictionaryEntries(residentPages)
                   .Select(entry => (string)entry.Key)) &&
               residentLru.SequenceEqual(lruBefore) &&
               GetField<long>(window, "_residentSpritePageBytes") ==
               residentBytesBefore &&
               Equals(GetRawField(window, "_pendingSpriteFrame"), pendingFrame) &&
               string.Equals(
                   GetRawField(window, "_desiredSpritePageName") as string,
                   failurePageName,
                   StringComparison.Ordinal) &&
               GetField<bool>(window, "_desiredSpritePageUrgent") &&
               GetRawField(window, "_failedSpritePageName") is null,
            "Rendering回调内不得直接修改缓存、pending、desired或failed状态");

        Invoke(window, "SpritePagePrefetchDispatchTimer_Tick", null, EventArgs.Empty);
        var idleTargetBytes = (long)(typeof(MainWindow).GetField(
                "SpritePageIdleResidentTargetBytes",
                StaticFlags)!.GetValue(null) ?? 0L);
        Assert(!GetField<bool>(window, "_residentSpritePageTrimPending") &&
               !GetField<bool>(window, "_residentSpritePageIdleTrimPending") &&
               GetRawField(window, "_renderDeferredSpritePageFailureName") is null &&
               GetRawField(window, "_renderDeferredSpritePageFailureReason") is null &&
               !dispatchTimer.IsEnabled &&
               GetRawField(window, "_pendingSpriteFrame") is null &&
               GetRawField(window, "_desiredSpritePageName") is null &&
               !GetField<bool>(window, "_desiredSpritePageUrgent") &&
               string.Equals(
                   GetRawField(window, "_failedSpritePageName") as string,
                   failurePageName,
                   StringComparison.Ordinal) &&
               GetField<long>(window, "_residentSpritePageBytes") <=
               idleTargetBytes,
            "Composition返回后的dispatcher tick必须消费idle trim/失败标志、裁到64MiB并执行终态更新");

        SetField(window, "_failedSpritePageName", null);
        AssertResidentSpriteCacheAccounting(window, "Rendering延迟缓存变更");
        ResetSpritePageCollectionTestState(window);
    }

    private static void AssertSpriteCacheShutdownAndLateCompletionContract()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"xlb-pet-cache-shutdown-{Guid.NewGuid():N}");
        MainWindow? shutdownWindow = null;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            shutdownWindow = new MainWindow
            {
                ShowActivated = false
            };
            SetField(
                shutdownWindow,
                "_todoStore",
                new TodoStore(Path.Combine(tempDirectory, "todos.json")));
            SetField(
                shutdownWindow,
                "_settingsStore",
                new AppSettingsStore(Path.Combine(tempDirectory, "settings.json")));
            SetField(
                shutdownWindow,
                "_scheduledTaskStore",
                new ScheduledTaskStore(
                    Path.Combine(tempDirectory, "scheduled-tasks.json")));
            GetField<ObservableCollection<TodoItem>>(
                shutdownWindow,
                "_todos").Clear();
            GetField<ObservableCollection<ScheduledTaskItem>>(
                shutdownWindow,
                "_scheduledTasks").Clear();
            GetField<DispatcherTimer>(
                shutdownWindow,
                "_scheduledTaskTimer").Stop();

            var pageMap = GetField<IDictionary>(shutdownWindow, "_spritePages");
            var loadedPageName = GetField<string>(
                shutdownWindow,
                "_loadedSpritePageName");
            var latePageName = GetDictionaryEntries(pageMap)
                .Select(entry => (string)entry.Key)
                .First(pageName => !string.Equals(
                    pageName,
                    loadedPageName,
                    StringComparison.Ordinal));
            var lateTask = CreateCompletedSpritePageLoadTask(
                shutdownWindow,
                latePageName);
            Assert(lateTask.IsCompletedSuccessfully,
                "关闭回归必须持有一个真实解码且成功的迟到分页结果");
            var lateCancellation = new CancellationTokenSource();
            var lateGeneration = GetField<int>(
                shutdownWindow,
                "_spritePagePrefetchGeneration");
            SetField(shutdownWindow, "_spritePagePrefetchTask", lateTask);
            SetField(
                shutdownWindow,
                "_spritePagePrefetchCancellation",
                lateCancellation);
            SetField(
                shutdownWindow,
                "_spritePagePrefetchPageName",
                latePageName);

            // Exercise the real Closing handler: it cancels in-flight work and
            // clears cache state before any late dispatcher completion arrives.
            shutdownWindow.Close();
            Assert(GetField<bool>(shutdownWindow, "_isClosing"),
                "真实Window.Close必须进入关闭状态");
            AssertSpriteCacheIsFullyReleased(shutdownWindow, "窗口关闭");

            Invoke(
                shutdownWindow,
                "CompleteSpritePagePrefetch",
                latePageName,
                lateGeneration,
                lateCancellation,
                lateTask);
            AssertSpriteCacheIsFullyReleased(shutdownWindow, "关闭后迟到成功结果");
            Assert(GetRawField(shutdownWindow, "_spritePagePrefetchTask") is null &&
                   GetRawField(shutdownWindow, "_spritePagePrefetchCancellation") is null &&
                   GetRawField(shutdownWindow, "_spritePagePrefetchPageName") is null,
                "关闭后的迟到成功结果只能释放在途引用，不能重新发布分页");
        }
        finally
        {
            if (shutdownWindow is not null &&
                !GetField<bool>(shutdownWindow, "_isClosing"))
            {
                shutdownWindow.Close();
            }

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // 临时目录清理不得掩盖关闭与迟到完成契约。
            }
        }
    }

    private static Task CreateCompletedSpritePageLoadTask(
        MainWindow window,
        string pageName)
    {
        var page = GetField<IDictionary>(window, "_spritePages")[pageName]!;
        var result = Invoke(
                         window,
                         "DecodeSpritePage",
                         page,
                         CancellationToken.None)
                     ?? throw new InvalidOperationException(
                         $"分页 {pageName} 的受控解码未返回结果");
        var fromResult = typeof(Task)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(Task.FromResult) &&
                              method.IsGenericMethodDefinition &&
                              method.GetParameters().Length == 1)
            .MakeGenericMethod(result.GetType());
        return (Task)(fromResult.Invoke(null, [result])
                      ?? throw new InvalidOperationException(
                          $"分页 {pageName} 无法构造成功Task"));
    }

    private static void AssertSpriteCacheIsFullyReleased(
        MainWindow window,
        string stage)
    {
        Assert(GetField<IDictionary>(window, "_residentSpritePages").Count == 0 &&
               GetField<LinkedList<string>>(
                   window,
                   "_residentSpritePageLru").Count == 0 &&
               GetField<long>(window, "_residentSpritePageBytes") == 0 &&
               GetField<byte[]>(window, "_spritePagePixels").Length == 0 &&
               GetRawField(window, "_loadedSpritePageName") is null &&
               GetField<int>(window, "_loadedSpritePageStride") == 0 &&
               GetField<long>(window, "_spritePageEvictedBytesSinceCollection") == 0 &&
               GetField<long>(window, "_spritePageCollectionDebtAtRequest") == 0 &&
               !GetField<bool>(window, "_spritePageCollectionInProgress") &&
               GetField<int>(window, "_spritePageCollectionPollCount") == 0 &&
               !GetField<DispatcherTimer>(
                   window,
                   "_spritePageCollectionTimer").IsEnabled,
            $"{stage}必须释放resident/LRU/像素/loaded状态，并停止Gen2 timer、清零回收债务");
    }

    private static void AssertColdSpritePageClipClockContract(MainWindow window)
    {
        var idleFrame = GetField<object>(window, "_idleFrame");
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);

        var clip = GetField<Array>(window, "_reactionClips").GetValue(0)!;
        var clipFrames = GetClipFrames(clip);
        var wakeFrameCount = GetField<Array>(window, "_wakeFrames").Length;
        var firstColdFrameIndex = GetProperty<int>(clip, "ActionFrameIndex");
        Assert(firstColdFrameIndex == wakeFrameCount,
            $"播放完{wakeFrameCount}帧起身后，普通动作必须在索引" +
            $"{wakeFrameCount}首次切入60fps动作页");
        var firstAnimationFrame = clipFrames.GetValue(firstColdFrameIndex)!;
        var firstSpriteFrame = GetProperty<object>(firstAnimationFrame, "Image");
        var firstHoldDuration = GetProperty<TimeSpan>(firstAnimationFrame, "HoldDuration");
        var firstPageName = GetSpriteFrameInfo(firstSpriteFrame).PageName;
        Assert(!string.Equals(
                GetRawField(window, "_loadedSpritePageName") as string,
                firstPageName,
                StringComparison.Ordinal),
            "冷页动作时钟测试必须从不同于待机页的动作分页开始");
        EvictResidentSpritePageForTest(window, firstPageName);

        SetField(window, "_activeClip", clip);
        SetField(window, "_activeFrameIndex", firstColdFrameIndex - 1);
        SetField(window, "_nextFrameMinimumHold", TimeSpan.Zero);
        var coldRequestAt = StopwatchTicksFromSeconds(10);
        Invoke(window, "ShowActiveClipFrameAt", firstColdFrameIndex, coldRequestAt);

        Assert(Equals(GetRawField(window, "_pendingSpriteFrame"), firstSpriteFrame) &&
               Equals(GetRawField(window, "_currentSpriteFrame"), idleFrame) &&
               GetField<long>(window, "_activeClipStartedTimestamp") == 0 &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") == long.MaxValue,
            "冷页解码期间必须保留旧稳定画面，并冻结新动作首帧时钟");

        var prefetchTask = GetRawField(window, "_spritePagePrefetchTask") as Task;
        var cancellation = GetRawField(window, "_spritePagePrefetchCancellation")
            as CancellationTokenSource;
        var generation = GetField<int>(window, "_spritePagePrefetchGeneration");
        var desiredPageName = GetRawField(window, "_desiredSpritePageName") as string;
        Assert(prefetchTask is not null && cancellation is not null &&
               string.Equals(desiredPageName, firstPageName, StringComparison.Ordinal),
            "冷页首帧必须只启动后台分页预取，不能在UI线程同步解码");
        Assert(prefetchTask!.Wait(TimeSpan.FromSeconds(3)),
            "冷页动作分页后台解码必须在3秒内完成");
        prefetchTask.GetAwaiter().GetResult();

        // Publish the completed background task without pumping Rendering so the
        // clock can be verified against a deterministic, synthetic 250 ms delay.
        Invoke(
            window,
            "CompleteSpritePagePrefetch",
            desiredPageName!,
            generation,
            cancellation!,
            prefetchTask);
        var coldDisplayedAt = coldRequestAt + StopwatchTicksFromMilliseconds(250);
        Invoke(window, "TryShowPendingSpriteFrameAt", coldDisplayedAt);

        var firstDeadline = coldDisplayedAt +
                            ToProductionCharacterAnimationTicks(firstHoldDuration);
        Assert(Equals(GetRawField(window, "_currentSpriteFrame"), firstSpriteFrame) &&
               GetField<int>(window, "_activeFrameIndex") == firstColdFrameIndex &&
               GetField<long>(window, "_activeClipStartedTimestamp") == coldDisplayedAt &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") == firstDeadline,
            "冷页即使延迟超过单帧间隔，也必须从目标首帧开始并以实际显示时刻重基准，不能补播跳帧");
        var deadlineToleranceTicks = (long)(typeof(MainWindow).GetField(
                "VisualFrameDeadlineToleranceTicks",
                StaticFlags)!.GetValue(null) ?? 0L);
        Invoke(
            window,
            "AdvanceActiveClip",
            firstDeadline - deadlineToleranceTicks - 1);
        Assert(GetField<int>(window, "_activeFrameIndex") == firstColdFrameIndex,
            "冷页首帧必须从实际显示时刻计算，并保持到合成器提前呈现容差边界");

        // The same page is now hot. A delayed Rendering callback must keep the
        // existing absolute timeline and resolve directly to the current pose.
        SetField(window, "_activeClip", clip);
        SetField(window, "_activeFrameIndex", firstColdFrameIndex - 1);
        SetField(window, "_nextFrameMinimumHold", TimeSpan.Zero);
        var hotStartedAt = coldDisplayedAt + StopwatchTicksFromSeconds(1);
        Invoke(window, "ShowActiveClipFrameAt", firstColdFrameIndex, hotStartedAt);
        Assert(GetField<long>(window, "_activeClipStartedTimestamp") == 0 &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") == long.MaxValue,
            "Hot and cold pages must both defer the clip clock until the first composition pass");
        Invoke(window, "TryShowPendingSpriteFrameAt", hotStartedAt);
        Assert(GetField<long>(window, "_activeClipStartedTimestamp") == hotStartedAt &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") ==
               hotStartedAt + ToProductionCharacterAnimationTicks(firstHoldDuration),
            "热页动作必须保持原有绝对时间起点，不得进入冷页冻结路径");

        var delayedRenderingAt = hotStartedAt + StopwatchTicksFromMilliseconds(250);
        var expectedFrameIndex = firstColdFrameIndex;
        var expectedDeadline = hotStartedAt +
                               ToProductionCharacterAnimationTicks(firstHoldDuration);
        while (expectedFrameIndex + 1 < clipFrames.Length &&
               delayedRenderingAt >= expectedDeadline)
        {
            expectedFrameIndex++;
            var expectedFrame = clipFrames.GetValue(expectedFrameIndex)!;
            expectedDeadline += ToProductionCharacterAnimationTicks(
                GetProperty<TimeSpan>(expectedFrame, "HoldDuration"));
        }

        Invoke(window, "AdvanceActiveClip", delayedRenderingAt);
        var expectedSpriteFrame = GetProperty<object>(
            clipFrames.GetValue(expectedFrameIndex)!,
            "Image");
        Assert(GetField<int>(window, "_activeFrameIndex") == expectedFrameIndex &&
               Equals(GetRawField(window, "_currentSpriteFrame"), expectedSpriteFrame) &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") == expectedDeadline,
            "热页延迟250ms必须按绝对时间直接定位当前帧，不能从旧帧逐帧补播");

        // A corrupt/missing resource must not leave the deferred first-frame
        // sentinel alive forever. Exercise the failure terminal path directly;
        // the Task fault branch is source-checked below to call this handler.
        SetField(window, "_activeClip", clip);
        SetField(window, "_activeFrameIndex", firstColdFrameIndex);
        SetField(window, "_activeClipStartedTimestamp", 0L);
        SetField(window, "_activeFrameDeadlineTimestamp", long.MaxValue);
        SetField(window, "_pendingSpriteFrame", firstSpriteFrame);
        SetField(window, "_deferredActiveClipClock", clip);
        SetField(window, "_deferredActiveClipClockFrame", firstSpriteFrame);
        SetField(window, "_deferredActiveClipClockFrameIndex", firstColdFrameIndex);
        SetField(window, "_deferredActiveClipClockHoldDuration", firstHoldDuration);
        Invoke(window, "UpdateVisualClockSubscription");
        Assert(GetField<bool>(window, "_isVisualClockSubscribed"),
            "冷页失败测试必须先建立等待首帧的统一渲染订阅");
        Invoke(
            window,
            "HandleSpritePagePrefetchFailure",
            firstPageName,
            "synthetic failure");
        Assert(GetRawField(window, "_pendingSpriteFrame") is null &&
               GetRawField(window, "_deferredActiveClipClock") is null &&
               GetRawField(window, "_deferredActiveClipClockFrame") is null &&
               GetRawField(window, "_activeClip") is null &&
               GetField<int>(window, "_activeFrameIndex") == -1 &&
               GetField<long>(window, "_activeClipStartedTimestamp") == 0 &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") == 0 &&
               !GetField<bool>(window, "_isVisualClockSubscribed"),
            "冷页解码失败必须清除pending/延迟时钟/动作并停止Rendering，不能永久空转");
        SetField(window, "_failedSpritePageName", null);

        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
        var coldEdgeContracts = new[]
        {
            (Dock: "Left", FieldName: "_edgeLeftFrames", PageName: "edge-left"),
            (Dock: "Right", FieldName: "_edgeLeftFrames", PageName: "edge-left"),
            (Dock: "Bottom", FieldName: "_edgeBottomFrames", PageName: "edge-bottom")
        };
        for (var edgeContractIndex = 0;
             edgeContractIndex < coldEdgeContracts.Length;
             edgeContractIndex++)
        {
            var edgeContract = coldEdgeContracts[edgeContractIndex];
            var edgeFrames = GetField<Array>(window, edgeContract.FieldName);
            var edgeRestFrameIndex = edgeFrames.Length - 1;
            var edgeRestFrame = edgeFrames.GetValue(edgeRestFrameIndex)!;
            var edgePageName = GetSpriteFrameInfo(edgeRestFrame).PageName;
            var idlePageName = GetSpriteFrameInfo(idleFrame).PageName;
            Assert(string.Equals(edgePageName, edgeContract.PageName, StringComparison.Ordinal) &&
                   !string.Equals(edgePageName, idlePageName, StringComparison.Ordinal),
                $"{edgeContract.Dock} smooth探头必须使用独立{edgeContract.PageName}分页，冷页行为必须可验证");
            EvictResidentSpritePageForTest(window, edgePageName);
            Invoke(window, "EnterEdgePeek", GetNestedEnum("EdgeDock", edgeContract.Dock));
            Assert(GetRawField(window, "_edgeDock")!.ToString() == edgeContract.Dock &&
                   GetField<int>(window, "_edgePeekFrameIndex") == edgeRestFrameIndex &&
                   Equals(GetRawField(window, "_currentSpriteFrame"), idleFrame) &&
                   Equals(GetRawField(window, "_pendingSpriteFrame"), edgeRestFrame) &&
                    GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == long.MaxValue &&
                    GetField<bool>(window, "_isVisualClockSubscribed") &&
                    GetField<Image>(window, "PillowImage").Visibility == Visibility.Visible &&
                    GetField<Image>(window, "PillowImage").Opacity == 1d &&
                    GetField<ScaleTransform>(window, "PetFacingScale").ScaleX ==
                    (edgeContract.Dock == "Right" ? -1 : 1),
                $"进入{edgeContract.Dock}冷边缘页必须停在末尾休息姿势，保留旧像素、冻结时钟；" +
                "Right只镜像复用Left序列");

            var edgePrefetchTask = GetRawField(window, "_spritePagePrefetchTask") as Task;
            var edgeCancellation = GetRawField(window, "_spritePagePrefetchCancellation")
                as CancellationTokenSource;
            var edgeGeneration = GetField<int>(window, "_spritePagePrefetchGeneration");
            var edgeDesiredPage = GetRawField(window, "_desiredSpritePageName") as string;
            Assert(edgePrefetchTask is not null && edgeCancellation is not null &&
                   string.Equals(edgeDesiredPage, edgePageName, StringComparison.Ordinal) &&
                   edgePrefetchTask.Wait(TimeSpan.FromSeconds(3)),
                $"冷{edgeContract.PageName}页必须只走后台预取并在3秒内完成");
            edgePrefetchTask!.GetAwaiter().GetResult();
            Invoke(
                window,
                "CompleteSpritePagePrefetch",
                edgeDesiredPage!,
                edgeGeneration,
                edgeCancellation!,
                edgePrefetchTask);

            var edgeDisplayedAt = StopwatchTicksFromSeconds(20 + edgeContractIndex * 10);
            Invoke(window, "TryShowPendingSpriteFrameAt", edgeDisplayedAt);
            var edgeRestDeadline = edgeDisplayedAt +
                                   ToProductionStopwatchTicks(
                                       TimeSpan.FromMilliseconds(800));
            Assert(Equals(GetRawField(window, "_currentSpriteFrame"), edgeRestFrame) &&
                    GetField<int>(window, "_edgePeekFrameIndex") == edgeRestFrameIndex &&
                    GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == edgeRestDeadline &&
                    GetField<Image>(window, "PillowImage").Visibility == Visibility.Visible &&
                    GetField<Image>(window, "PillowImage").Opacity == 0d,
                $"冷{edgeContract.Dock}休息帧必须从实际显示时刻完整停留800ms，不能解码期间偷跑");
            Invoke(window, "AdvanceEdgePeek", edgeRestDeadline - 1);
            Assert(GetField<int>(window, "_edgePeekFrameIndex") == edgeRestFrameIndex,
                "边缘休息姿势的800ms运行hold结束前不得提前换帧");

            Invoke(window, "AdvanceEdgePeek", edgeRestDeadline);
            var firstEdgeDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            Assert(GetField<int>(window, "_edgePeekFrameIndex") == 0 &&
                   Equals(GetRawField(window, "_currentSpriteFrame"), edgeFrames.GetValue(0)) &&
                   firstEdgeDeadline - edgeRestDeadline ==
                   ToProductionStopwatchTicks(
                       TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60)),
                "休息帧后必须按升序进入第001帧，并使用不跳姿势的原生60fps间隔");

            var stalledAt = edgeRestDeadline + StopwatchTicksFromMilliseconds(250);
            var expectedEdgeIndex = 0;
            var expectedEdgeDeadline = firstEdgeDeadline;
            while (stalledAt >= expectedEdgeDeadline)
            {
                expectedEdgeIndex = (expectedEdgeIndex + 1) % edgeFrames.Length;
                var hold = (TimeSpan)InvokeStatic(
                    typeof(MainWindow),
                    "GetEdgePeekFrameHoldDuration",
                    expectedEdgeIndex,
                    edgeFrames.Length)!;
                expectedEdgeDeadline += ToProductionStopwatchTicks(hold);
            }

            Invoke(window, "AdvanceEdgePeek", stalledAt);
            Assert(GetField<int>(window, "_edgePeekFrameIndex") == expectedEdgeIndex &&
                   Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       edgeFrames.GetValue(expectedEdgeIndex)) &&
                   GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == expectedEdgeDeadline,
                "边缘时间轴延迟250ms后必须只提交绝对时间对应姿势，不得快速补播积压帧");
            Invoke(window, "AdvanceEdgePeek", stalledAt);
            Assert(GetField<int>(window, "_edgePeekFrameIndex") == expectedEdgeIndex &&
                   GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == expectedEdgeDeadline,
                "同一延迟时间戳重复调用不得继续追帧，避免视觉补播抖动");

            var edgeCycleTicks = (long)InvokeStatic(
                typeof(MainWindow),
                "GetEdgePeekCycleDurationTicks",
                edgeFrames.Length)!;
            var multiCycleStallAt = expectedEdgeDeadline +
                                    edgeCycleTicks * 3 +
                                    StopwatchTicksFromMilliseconds(40);
            var multiCycleExpectedIndex = expectedEdgeIndex;
            var multiCycleExpectedDeadline = expectedEdgeDeadline + edgeCycleTicks * 3;
            while (multiCycleStallAt >= multiCycleExpectedDeadline)
            {
                multiCycleExpectedIndex = (multiCycleExpectedIndex + 1) % edgeFrames.Length;
                var hold = (TimeSpan)InvokeStatic(
                    typeof(MainWindow),
                    "GetEdgePeekFrameHoldDuration",
                    multiCycleExpectedIndex,
                    edgeFrames.Length)!;
                multiCycleExpectedDeadline +=
                    ToProductionStopwatchTicks(hold);
            }

            Invoke(window, "AdvanceEdgePeek", multiCycleStallAt);
            Assert(GetField<int>(window, "_edgePeekFrameIndex") == multiCycleExpectedIndex &&
                   GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") ==
                   multiCycleExpectedDeadline,
                "跨3个完整闭环的延迟必须动态跳过整周期并只解析余数，不能逐帧补播整轮");

            foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
            {
                SetField(window, "_edgePeekFrameIndex", multiCycleExpectedIndex);
                SetField(
                    window,
                    "_edgePeekFrameDeadlineTimestamp",
                    multiCycleExpectedDeadline);
                Invoke(
                    window,
                    "ShowStableFrame",
                    edgeFrames.GetValue(multiCycleExpectedIndex)!);
                var nextVsyncAt = checked(
                    multiCycleStallAt +
                    (long)Math.Round(Stopwatch.Frequency / refreshRate));
                var nextVsyncExpectedIndex = multiCycleExpectedIndex;
                var nextVsyncExpectedDeadline = multiCycleExpectedDeadline;
                while (nextVsyncAt >= nextVsyncExpectedDeadline)
                {
                    nextVsyncExpectedIndex =
                        (nextVsyncExpectedIndex + 1) % edgeFrames.Length;
                    var hold = (TimeSpan)InvokeStatic(
                        typeof(MainWindow),
                        "GetEdgePeekFrameHoldDuration",
                        nextVsyncExpectedIndex,
                        edgeFrames.Length)!;
                    nextVsyncExpectedDeadline +=
                        ToProductionStopwatchTicks(hold);
                }

                Invoke(window, "AdvanceEdgePeek", nextVsyncAt);
                Assert(GetField<int>(window, "_edgePeekFrameIndex") ==
                       nextVsyncExpectedIndex &&
                       GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") ==
                       nextVsyncExpectedDeadline,
                    $"{edgeContract.Dock}边缘在{refreshRate:F0}Hz的stall恢复回调必须按" +
                    "原生60fps绝对时间直接定位，不得补播积压姿势");
            }

            Invoke(window, "ExitEdgePeek", false, true);
            Assert(GetRawField(window, "_edgeDock")!.ToString() == "None" &&
                   GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0 &&
                   Equals(GetRawField(window, "_currentSpriteFrame"), idleFrame) &&
                   GetField<Image>(window, "PillowImage").Visibility == Visibility.Visible &&
                   GetField<Image>(window, "PillowImage").Opacity == 1d,
                "退出独立分页边缘探头后必须清理时钟并直接恢复 idle");
        }

        // Rewrite the raw idle pixels after the edge test baked a mirrored
        // visible frame into the fixed buffer; metadata alone intentionally
        // remained on the same stable SpriteFrame.
        SetField(window, "_currentSpriteFrame", null);
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var completionSource = ExtractPrivateMethodSource(
            mainSource,
            "CompleteSpritePagePrefetch");
        Assert(completionSource.Contains(
                "HandleSpritePagePrefetchFailure(pageName, error?.Message)",
                StringComparison.Ordinal),
            "真实后台解码Task故障分支必须进入已验证的终止处理路径");

        // Invalidate the dispatcher completion already queued by the manually
        // published task, then drain it before restoring the shared test window.
        SetField(
            window,
            "_spritePagePrefetchGeneration",
            GetField<int>(window, "_spritePagePrefetchGeneration") + 1);
        SetField(window, "_activeClip", null);
        SetField(window, "_activeFrameIndex", -1);
        SetField(window, "_activeClipStartedTimestamp", 0L);
        SetField(window, "_activeFrameDeadlineTimestamp", 0L);
        Invoke(window, "ClearDeferredActiveClipClock");
        Invoke(window, "StopFrameBlend", false);
        Invoke(window, "UpdateVisualClockSubscription");
        PumpDispatcher(TimeSpan.FromMilliseconds(20));
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);
    }

    private static void AssertSingleBufferPremultipliedBlendContract(MainWindow window)
    {
        const int expectedByteCount = RenderPixelWidth * RenderPixelHeight * 4;
        var displayPixels = GetField<byte[]>(window, "_displayFramePixels");
        var fromPixels = GetField<byte[]>(window, "_frameBlendFromPixels");
        var targetPixels = GetField<byte[]>(window, "_frameBlendTargetPixels");
        var outputPixels = GetField<byte[]>(window, "_frameBlendOutputPixels");
        var transformedPixels = GetField<byte[]>(window, "_transformedDisplayFramePixels");
        var actionTransitionDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "ActionTransitionDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.MinValue);
        var frameBlendDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "FrameBlendDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.MinValue);
        var edgeFrameBlendDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgeFrameBlendDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.MinValue);
        Assert(displayPixels.Length == expectedByteCount &&
               actionTransitionDuration == TimeSpan.Zero &&
               frameBlendDuration == TimeSpan.Zero &&
               edgeFrameBlendDuration == TimeSpan.Zero &&
               fromPixels.Length == 0 &&
               targetPixels.Length == 0 &&
               outputPixels.Length == 0 &&
               transformedPixels.Length == expectedByteCount,
            "全部blend duration为Zero时三个淡化缓冲必须为空数组；" +
            "只保留完整显示帧与变换scratch");

        var from = new byte[] { 20, 40, 60, 80 };
        var to = new byte[] { 100, 80, 40, 160 };
        var blended = new byte[4];
        InvokeStatic(typeof(MainWindow), "BlendPremultipliedPixels", from, to, blended, 0.5d);
        Assert(blended.SequenceEqual(new byte[] { 60, 60, 50, 120 }),
            "单buffer淡化中点必须逐通道凸组合Pbgra32像素（包括Alpha）");
        Assert(Enumerable.Range(0, blended.Length).All(index =>
                blended[index] >= Math.Min(from[index], to[index]) &&
                blended[index] <= Math.Max(from[index], to[index])),
            "淡化的每个通道不得超过任一端点最大值，避免出现亮度光纹峰值");
        Assert(blended[0] <= blended[3] &&
               blended[1] <= blended[3] &&
               blended[2] <= blended[3],
            "淡化后的颜色必须继续满足预乘Alpha约束");

        var clampedBefore = new byte[4];
        var clampedAfter = new byte[4];
        InvokeStatic(typeof(MainWindow), "BlendPremultipliedPixels", from, to, clampedBefore, -1d);
        InvokeStatic(typeof(MainWindow), "BlendPremultipliedPixels", from, to, clampedAfter, 2d);
        Assert(clampedBefore.SequenceEqual(from) && clampedAfter.SequenceEqual(to),
            "预乘Alpha淡化进度必须钳制在0..1，防止过冲光纹");

        var transformSource = new byte[]
        {
            10, 20, 30, 40,
            50, 60, 70, 80
        };
        var mirrored = new byte[transformSource.Length];
        var mirrorMatrix = Matrix.Identity;
        mirrorMatrix.ScaleAt(-1, 1, 1, 0.5);
        InvokeStatic(
            typeof(MainWindow),
            "TransformPremultipliedPixels",
            transformSource,
            mirrored,
            2,
            1,
            mirrorMatrix);
        Assert(mirrored.SequenceEqual(new byte[]
            {
                50, 60, 70, 80,
                10, 20, 30, 40
            }),
            "右侧探头进入Todo前必须把镜像后的实际画面烘焙进单buffer");

        var translated = new byte[transformSource.Length];
        var translationMatrix = Matrix.Identity;
        translationMatrix.Translate(1, 0);
        InvokeStatic(
            typeof(MainWindow),
            "TransformPremultipliedPixels",
            transformSource,
            translated,
            2,
            1,
            translationMatrix);
        Assert(translated.SequenceEqual(new byte[]
            {
                0, 0, 0, 0,
                10, 20, 30, 40
            }),
            "视觉偏移进入Todo前必须被烘焙并按窗口边界透明裁剪，不能先跳回中心");

        var combinedSource = new byte[]
        {
            10, 10, 10, 20,
            20, 20, 20, 40,
            30, 30, 30, 60,
            40, 40, 40, 80
        };
        var combinedOutput = new byte[combinedSource.Length];
        var mirrorAndTranslation = Matrix.Identity;
        mirrorAndTranslation.ScaleAt(-1, 1, 2, 0.5);
        mirrorAndTranslation.Translate(1, 0);
        InvokeStatic(
            typeof(MainWindow),
            "TransformPremultipliedPixels",
            combinedSource,
            combinedOutput,
            4,
            1,
            mirrorAndTranslation);
        Assert(combinedOutput.SequenceEqual(new byte[]
            {
                0, 0, 0, 0,
                40, 40, 40, 80,
                30, 30, 30, 60,
                20, 20, 20, 40
            }),
            "单buffer坐标换基必须同时正确处理镜像与视觉平移");

        var fullFrameTransform = Matrix.Identity;
        fullFrameTransform.ScaleAt(
            -1,
            1,
            RenderPixelWidth / 2d,
            RenderPixelHeight * 0.72);
        fullFrameTransform.Translate(0.37, -0.41);
        InvokeStatic(
            typeof(MainWindow),
            "TransformPremultipliedPixels",
            displayPixels,
            transformedPixels,
            RenderPixelWidth,
            RenderPixelHeight,
            fullFrameTransform);
        var transformStopwatch = Stopwatch.StartNew();
        const int transformIterations = 3;
        for (var iteration = 0; iteration < transformIterations; iteration++)
        {
            InvokeStatic(
                typeof(MainWindow),
                "TransformPremultipliedPixels",
                displayPixels,
                transformedPixels,
                RenderPixelWidth,
                RenderPixelHeight,
                fullFrameTransform);
        }

        transformStopwatch.Stop();
        Console.WriteLine(
            $"[METRIC] axis-aligned full-frame bake=" +
            $"{transformStopwatch.Elapsed.TotalMilliseconds / transformIterations:F3}ms");
    }

    private static DictionaryEntry[] GetDictionaryEntries(IDictionary dictionary)
    {
        var entries = new List<DictionaryEntry>(dictionary.Count);
        var enumerator = dictionary.GetEnumerator();
        while (enumerator.MoveNext())
        {
            entries.Add(new DictionaryEntry(enumerator.Key, enumerator.Value));
        }

        return entries.ToArray();
    }

    private static void AssertSpriteSurfaceConfigured(
        Rectangle petImage,
        ImageBrush spriteBrush,
        SpriteFrameInfo frame)
    {
        AssertRectClose(
            spriteBrush.Viewbox,
            new Rect(0, 0, RenderPixelWidth, RenderPixelHeight),
            $"{frame.PageName}/{frame.Name} 完整帧Viewbox");
        AssertClose(petImage.Width, LogicalPetWidth,
            $"{frame.PageName}/{frame.Name} Rectangle固定宽度");
        AssertClose(petImage.Height, LogicalPetHeight,
            $"{frame.PageName}/{frame.Name} Rectangle固定高度");
        AssertClose(NormalizeCanvasCoordinate(Canvas.GetLeft(petImage)), 0,
            $"{frame.PageName}/{frame.Name} Canvas.Left固定锚点");
        AssertClose(NormalizeCanvasCoordinate(Canvas.GetTop(petImage)), 0,
            $"{frame.PageName}/{frame.Name} Canvas.Top固定锚点");
        AssertClose(petImage.Opacity, 1,
            $"{frame.PageName}/{frame.Name} Surface透明度");
    }

    private static void AssertNoSpriteSurfaceAnimations(
        Rectangle petImage,
        ImageBrush spriteBrush,
        string frameName)
    {
        Assert(!DependencyPropertyHelper
                .GetValueSource(spriteBrush, ImageBrush.ViewboxProperty).IsAnimated &&
               !DependencyPropertyHelper
                .GetValueSource(petImage, FrameworkElement.WidthProperty).IsAnimated &&
               !DependencyPropertyHelper
                .GetValueSource(petImage, FrameworkElement.HeightProperty).IsAnimated &&
               !DependencyPropertyHelper
                .GetValueSource(petImage, Canvas.LeftProperty).IsAnimated &&
               !DependencyPropertyHelper
                .GetValueSource(petImage, Canvas.TopProperty).IsAnimated &&
               !DependencyPropertyHelper
                .GetValueSource(petImage, UIElement.OpacityProperty).IsAnimated,
            $"显示 {frameName} 不得产生任何DP动画");
    }

    private static void AssertBufferMatchesPage(
        byte[] spritePagePixels,
        RuntimePage page)
    {
        var stride = checked(page.Width * 4);
        var byteCount = checked(stride * page.Height);
        var actualHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    spritePagePixels.AsSpan(0, byteCount)))
            .ToLowerInvariant();
        Assert(string.Equals(actualHash, page.DecodedSha256, StringComparison.Ordinal),
            $"{page.Name} 的Brotli分页解压结果必须匹配清单decodedSha256");
    }

    private static void AssertCompressedPageLoadPerformance(
        MainWindow window,
        RuntimePage[] pages)
    {
        var loadMethod = typeof(MainWindow).GetMethod(
            "LoadSpritePageIntoBuffer",
            InstanceFlags)
            ?? throw new InvalidOperationException(
                "找不到LoadSpritePageIntoBuffer，无法验证Brotli分页加载性能");
        const int measuredRuns = 5;
        const double maximumMedianMilliseconds = 20d;
        const double maximumSingleRunMilliseconds = 60d;

        foreach (var page in pages)
        {
            // 先预热程序集资源流与JIT；门槛只约束热盘加载，避免把Windows CI的
            // 首次文件映射抖动误判成解压回归。
            _ = loadMethod.Invoke(window, new[] { (object)page.Name, page.RuntimeValue });
            var hotPixels = GetField<byte[]>(window, "_spritePagePixels");
            var elapsed = new double[measuredRuns];
            for (var index = 0; index < measuredRuns; index++)
            {
                var stopwatch = Stopwatch.StartNew();
                _ = loadMethod.Invoke(window, new[] { (object)page.Name, page.RuntimeValue });
                stopwatch.Stop();
                elapsed[index] = stopwatch.Elapsed.TotalMilliseconds;
                Assert(ReferenceEquals(
                           hotPixels,
                           GetField<byte[]>(window, "_spritePagePixels")),
                    $"{page.Name} 连续热命中必须复用同一解码数组，不得重复解压或分配");
            }

            Array.Sort(elapsed);
            var median = elapsed[measuredRuns / 2];
            var maximum = elapsed[^1];
            Assert(median <= maximumMedianMilliseconds &&
                   maximum <= maximumSingleRunMilliseconds,
                $"{page.Name} 的热盘Brotli分页加载过慢：" +
                $"中位数 {median:F2}ms（上限 {maximumMedianMilliseconds:F0}ms），" +
                $"最大 {maximum:F2}ms（上限 {maximumSingleRunMilliseconds:F0}ms）");
            AssertResidentSpriteCacheAccounting(window, $"{page.Name} 热命中");
        }
    }

    private static void AssertSamePageDoesNotRewrite(
        MainWindow window,
        RuntimePage page,
        DictionaryEntry[] pageFrames,
        byte[] spritePagePixels,
        ImageBrush spriteBrush)
    {
        if (pageFrames.Length < 2)
        {
            return;
        }

        Invoke(window, "ShowStableFrame", pageFrames[0].Value);
        var originalPixel = new byte[4];
        Array.Copy(spritePagePixels, originalPixel, originalPixel.Length);
        var sentinelPixel = originalPixel.Select(value => (byte)(value ^ 0xff)).ToArray();
        Array.Copy(sentinelPixel, spritePagePixels, sentinelPixel.Length);

        Invoke(window, "ShowStableFrame", pageFrames[1].Value);
        var actualPixel = new byte[4];
        Array.Copy(spritePagePixels, actualPixel, actualPixel.Length);
        Assert(actualPixel.AsSpan().SequenceEqual(sentinelPixel),
            $"{page.Name} 内切帧不得重新解码或覆写分页缓冲区");
        Assert(ReferenceEquals(spriteBrush.ImageSource,
                   GetField<WriteableBitmap>(window, "_displayFrameBuffer")) &&
               GetField<string>(window, "_loadedSpritePageName") == page.Name,
            $"{page.Name} 内切帧必须继续复用同一完整帧ImageSource和页标记");
        Array.Copy(originalPixel, spritePagePixels, originalPixel.Length);
    }

    private static void AssertSameFrameReturnsEarly(
        MainWindow window,
        Rectangle petImage,
        ImageBrush spriteBrush,
        object frameValue)
    {
        Invoke(window, "ShowStableFrame", frameValue);
        var frame = GetSpriteFrameInfo(frameValue);
        var displayFrameBuffer =
            GetField<WriteableBitmap>(window, "_displayFrameBuffer");
        var bufferReference = spriteBrush.ImageSource;
        var originalPixel = new byte[4];
        var pixelBounds = new Int32Rect(0, 0, 1, 1);
        displayFrameBuffer.CopyPixels(pixelBounds, originalPixel, 4, 0);
        var sentinelPixel = originalPixel.Select(value => (byte)(value ^ 0xff)).ToArray();
        displayFrameBuffer.WritePixels(pixelBounds, sentinelPixel, 4, 0);

        Invoke(window, "ShowStableFrame", frameValue);
        var actualPixel = new byte[4];
        displayFrameBuffer.CopyPixels(pixelBounds, actualPixel, 4, 0);
        Assert(actualPixel.AsSpan().SequenceEqual(sentinelPixel),
            $"重复显示 {frame.Name} 时应在重写完整帧buffer前直接返回");
        Assert(ReferenceEquals(bufferReference, displayFrameBuffer) &&
               ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer),
            "同帧早退前后ImageSource必须保持唯一完整帧buffer引用");
        displayFrameBuffer.WritePixels(pixelBounds, originalPixel, 4, 0);

        SetField(window, "_currentSpriteFrame", null);
        Invoke(window, "ShowStableFrame", frameValue);
        AssertSpriteSurfaceConfigured(petImage, spriteBrush, frame);
        AssertNoSpriteSurfaceAnimations(petImage, spriteBrush, frame.Name);
    }

    private static void AssertSpritePagesManifestAndResourcesContract(RuntimePage[] pages)
    {
        var manifestPath = FindWorkspaceFile("Assets", "luban-sprite-pages.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        Assert(root.GetProperty("version").GetInt32() == 4 &&
               root.GetProperty("compression").GetString() == "brotli",
            "无损Brotli Pbgra分页图集清单必须声明v4/brotli契约");
        Assert(root.GetProperty("displayWidth").GetInt32() == RenderPixelWidth &&
               root.GetProperty("displayHeight").GetInt32() == RenderPixelHeight,
            $"分页图集渲染视口必须为{RenderPixelWidth}×{RenderPixelHeight}，" +
            $"同时由WPF保留{LogicalPetWidth}×{LogicalPetHeight}逻辑尺寸");
        var includeOptionalRoamWave = root.GetProperty("pages")
            .EnumerateObject()
            .Any(page =>
                string.Equals(page.Name, "roam-wave", StringComparison.Ordinal) ||
                page.Name.StartsWith("roam-wave-part-", StringComparison.Ordinal));
        var expectedSourcePaths = BuildExpectedSourceResourcePaths(
            includeOptionalRoamWave);
        var manifestSourceFrameCount = root.GetProperty("sourceFrameCount").GetInt32();
        var manifestPageFrameCount = root.GetProperty("pageFrameCount").GetInt32();
        Assert(manifestSourceFrameCount == expectedSourcePaths.Length,
            $"分页清单sourceFrameCount必须由运行时资源清单动态得到，" +
            $"实际清单 {manifestSourceFrameCount}，运行时 {expectedSourcePaths.Length}");
        var manifestMaximumDecodedPageBytes =
            root.GetProperty("maxDecodedPageBytes").GetInt64();
        Assert(manifestMaximumDecodedPageBytes is > 0 and <= MaximumDecodedSpritePageBytes,
            $"分页清单maxDecodedPageBytes必须为正数且不超过24MiB，实际 " +
            $"{manifestMaximumDecodedPageBytes / 1024d / 1024d:F2}MiB");
        var sourceSetFingerprint = root.GetProperty("sourceSetFingerprint").GetString()
            ?? throw new InvalidOperationException("分页清单sourceSetFingerprint不能为空");
        AssertCanonicalSha256(sourceSetFingerprint, "分页清单sourceSetFingerprint");
        Assert(string.Equals(
                sourceSetFingerprint,
                ComputeSourceSetFingerprint(expectedSourcePaths),
                StringComparison.Ordinal),
            "分页清单sourceSetFingerprint必须与全部源PNG的路径、顺序及实际内容一致");

        var manifestPages = root.GetProperty("pages");
        var manifestPageCount = manifestPages.EnumerateObject().Count();
        Assert(pages.Length == manifestPageCount,
            $"清单与运行时分页数必须动态一致，清单 {manifestPageCount}，运行时 {pages.Length}");
        var requiredPageNames = new[]
            {
                "idle",
                "edge-left",
                "edge-bottom",
                "roam-boarding",
                "roam-flight"
            }
            .Concat(new[] { "yawn", "cry", "cute", "like", "eat", "wave", "think" }
                .SelectMany(action => new[] { $"action-{action}", $"loop-{action}" }))
            .Concat(new[] { "action-reminder-enter", "action-reminder-hold" })
            .ToHashSet(StringComparer.Ordinal);
        var orderedPageNames = manifestPages.EnumerateObject()
            .Select(page => page.Name)
            .ToArray();
        var actualPageNames = orderedPageNames
            .ToHashSet(StringComparer.Ordinal);
        Assert(requiredPageNames.IsSubsetOf(actualPageNames) &&
               manifestSourceFrameCount == expectedSourcePaths.Length &&
               manifestPageFrameCount >= manifestSourceFrameCount &&
               orderedPageNames.Take(3).SequenceEqual(
                   new[] { "idle", "edge-left", "edge-bottom" }) &&
               !manifestPages.TryGetProperty("edge-top", out _) &&
               !manifestPages.TryGetProperty("edge", out _),
            $"清单必须先包含idle与左/下两组独立边缘页，且不得携带顶部边缘页；" +
            "还必须动态包含熊猫坐骑登乘与巡游连续分页、七个动作页、七个循环页和两组专用提醒页，" +
            "且页内帧不得少于逻辑源帧；" +
            $"source={manifestSourceFrameCount}, page-local={manifestPageFrameCount}, pages={manifestPageCount}");
        var expectedWakeFrameNames = GetExpectedWakeFrameNames();
        var expectedIdlePageFrames = expectedWakeFrameNames
            .Select(frameName => $"Assets/{frameName}")
            .Prepend("Assets/luban-idle.png")
            .ToHashSet(StringComparer.Ordinal);
        var actualIdlePageFrames = manifestPages.EnumerateObject()
            .Where(page =>
                string.Equals(page.Name, "idle", StringComparison.Ordinal) ||
                page.Name.StartsWith("idle-part-", StringComparison.Ordinal))
            .SelectMany(page => page.Value.GetProperty("frames").EnumerateObject())
            .Select(frame => frame.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert(actualIdlePageFrames.SetEquals(expectedIdlePageFrames),
            $"idle连续分页必须且只能包含 idle 与{expectedWakeFrameNames.Length}帧60fps wake");
        foreach (var edgeName in new[] { "left", "bottom" })
        {
            var edgePageName = $"edge-{edgeName}";
            var edgePage = manifestPages.GetProperty(edgePageName);
            var actualEdgeFrames = edgePage.GetProperty("frames")
                .EnumerateObject()
                .Select(frame => frame.Name)
                .ToArray();
            var expectedEdgeFrames = Enumerable.Range(1, ExpectedEdgePeekFrameCount)
                .Select(frameNumber =>
                    $"Assets/luban-edge-{edgeName}-smooth-{frameNumber:000}.png")
                .ToArray();
            Assert(edgePage.GetProperty("logicalFrameCount").GetInt32() == ExpectedEdgePeekFrameCount &&
                   actualEdgeFrames.SequenceEqual(expectedEdgeFrames),
                $"{edgePageName} 必须是独立{ExpectedEdgePeekFrameCount}帧分页并严格按" +
                $"smooth-001..{ExpectedEdgePeekFrameCount:000}升序，不能混入idle或旧4帧素材");
        }

        var roamSequences = new List<string> { "boarding", "flight" };
        if (includeOptionalRoamWave)
        {
            Assert(GetExpectedRoamFrameNames("wave", required: false).Length > 0,
                "清单包含可选roam-wave分页时，Assets必须提供连续且唯一的wave源帧");
            roamSequences.Add("wave");
        }
        foreach (var roamSequence in roamSequences)
        {
            var basePageName = $"roam-{roamSequence}";
            var expectedRoamFrames = GetExpectedRoamFrameNames(roamSequence)
                .Select(frameName => $"Assets/{frameName}")
                .ToArray();
            var roamPages = manifestPages.EnumerateObject()
                .Where(page =>
                    string.Equals(page.Name, basePageName, StringComparison.Ordinal) ||
                    page.Name.StartsWith(
                        basePageName + "-part-",
                        StringComparison.Ordinal))
                .OrderBy(page => page.Name, StringComparer.Ordinal)
                .ToArray();
            var expectedRoamPageNames = Enumerable.Range(1, roamPages.Length)
                .Select(partNumber => partNumber == 1
                    ? basePageName
                    : $"{basePageName}-part-{partNumber:00}")
                .ToArray();
            var actualRoamFrames = roamPages
                .SelectMany(page => page.Value
                    .GetProperty("frames")
                    .EnumerateObject()
                    .Select(frame => frame.Name))
                .ToArray();
            Assert(roamPages.Length >= 2 &&
                   roamPages.Select(page => page.Name)
                       .SequenceEqual(expectedRoamPageNames) &&
                   roamPages.All(page =>
                       page.Value.GetProperty("logicalFrameCount").GetInt32()
                           is > 0 and <= 32) &&
                   actualRoamFrames.SequenceEqual(expectedRoamFrames),
                $"熊猫坐骑必须使用{basePageName}连续动态分页，逐页不超过32帧，" +
                $"并完整覆盖{roamSequence}-001..{expectedRoamFrames.Length:000}；" +
                "不得把巡游帧塞进点击动作、idle或手动edge分页");
        }

        foreach (var actionName in new[] { "yawn", "cry", "cute", "like", "eat", "wave", "think" })
        {
            var pageName = $"action-{actionName}";
            var actionPageEntries = manifestPages.EnumerateObject()
                .Where(page =>
                    string.Equals(page.Name, pageName, StringComparison.Ordinal) ||
                    page.Name.StartsWith(pageName + "-part-", StringComparison.Ordinal))
                .OrderBy(page => page.Name, StringComparer.Ordinal)
                .ToArray();
            var actualActionFrames = actionPageEntries
                .SelectMany(page => page.Value
                    .GetProperty("frames")
                    .EnumerateObject()
                    .Select(frame => frame.Name))
                .ToArray();
            Assert(actionPageEntries.Length >= 2 &&
                   actionPageEntries.All(page =>
                       page.Value.GetProperty("logicalFrameCount").GetInt32() is > 0 and <= 32) &&
                   actualActionFrames.Length >= 50 &&
                   actualActionFrames.SequenceEqual(Enumerable.Range(1, actualActionFrames.Length)
                        .Select(frameNumber =>
                            $"Assets/luban-{actionName}-smooth-{frameNumber:000}.png")),
                $"{pageName} 连续分页必须按32帧上限只包含编号连续的60fps动作帧，" +
                "不得重复 idle、wake 或 edge");

            var loopPageName = $"loop-{actionName}";
            var actualLoopFrames = manifestPages.GetProperty(loopPageName)
                .GetProperty("frames")
                .EnumerateObject()
                .Select(frame => frame.Name)
                .ToArray();
            Assert(actualLoopFrames.SequenceEqual(Enumerable.Range(1, 48)
                    .Select(frameNumber =>
                        $"Assets/luban-{actionName}-loop-{frameNumber:000}.png")),
                $"{loopPageName} 必须包含连续的48帧60fps自然循环");
        }

        foreach (var (phase, expectedFrameCount) in new[]
                 {
                     (Phase: "enter", ExpectedFrameCount: 33),
                     (Phase: "hold", ExpectedFrameCount: 48)
                 })
        {
            var pageName = $"action-reminder-{phase}";
            var reminderPageEntries = manifestPages.EnumerateObject()
                .Where(page =>
                    string.Equals(page.Name, pageName, StringComparison.Ordinal) ||
                    page.Name.StartsWith(pageName + "-part-", StringComparison.Ordinal))
                .OrderBy(page => page.Name, StringComparer.Ordinal)
                .ToArray();
            var actualReminderFrames = reminderPageEntries
                .SelectMany(page => page.Value
                    .GetProperty("frames")
                    .EnumerateObject()
                    .Select(frame => frame.Name))
                .ToArray();
            Assert(reminderPageEntries.Length ==
                       (int)Math.Ceiling(expectedFrameCount / 32d) &&
                   reminderPageEntries.All(page =>
                       page.Value.GetProperty("logicalFrameCount").GetInt32()
                           is > 0 and <= 32) &&
                   actualReminderFrames.SequenceEqual(
                       Enumerable.Range(1, expectedFrameCount).Select(frameNumber =>
                           $"Assets/luban-reminder-{phase}-{frameNumber:000}.png")),
                $"{pageName} 必须按32帧上限连续分页并且只包含专用提醒" +
                $"{phase} 001..{expectedFrameCount:000}资源");
        }

        var runtimeByName = pages.ToDictionary(page => page.Name, StringComparer.Ordinal);
        var pageResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFrames = new HashSet<string>(StringComparer.Ordinal);
        var totalPageFrames = 0;
        foreach (var manifestPageEntry in manifestPages.EnumerateObject())
        {
            Assert(runtimeByName.TryGetValue(manifestPageEntry.Name, out var runtimePage),
                $"运行时缺少分页：{manifestPageEntry.Name}");
            var descriptor = manifestPageEntry.Value;
            Assert(!descriptor.TryGetProperty("previewResource", out _) &&
                   !descriptor.TryGetProperty("previewSha256", out _),
                $"{manifestPageEntry.Name} 清单不得保留可重新生成的分页预览PNG字段");
            var resource = descriptor.GetProperty("resource").GetString()
                ?? throw new InvalidOperationException("分页resource不能为空");
            var width = descriptor.GetProperty("width").GetInt32();
            var height = descriptor.GetProperty("height").GetInt32();
            var uncompressedByteCount =
                descriptor.GetProperty("uncompressedByteCount").GetInt64();
            var payloadByteCount = descriptor.GetProperty("payloadByteCount").GetInt64();
            var compressedByteCount = descriptor.GetProperty("compressedByteCount").GetInt64();
            var encoding = descriptor.GetProperty("encoding").GetString()
                ?? throw new InvalidOperationException(
                    $"Page encoding cannot be empty: {manifestPageEntry.Name}");
            var logicalCount = descriptor.GetProperty("logicalFrameCount").GetInt32();
            var uniqueCount = descriptor.GetProperty("uniqueSpriteCount").GetInt32();
            var manifestFrames = descriptor.GetProperty("frames");
            var sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString()
                ?? throw new InvalidOperationException(
                    $"分页sourceFingerprint不能为空：{manifestPageEntry.Name}");
            var contentSha256 = descriptor.GetProperty("contentSha256").GetString()
                ?? throw new InvalidOperationException(
                    $"分页contentSha256不能为空：{manifestPageEntry.Name}");
            var decodedSha256 = descriptor.GetProperty("decodedSha256").GetString()
                ?? throw new InvalidOperationException(
                    $"Page decodedSha256 cannot be empty: {manifestPageEntry.Name}");
            var expectedResource =
                $"Assets/sprite-pages/luban-{manifestPageEntry.Name}.pbgra.br";
            Assert(string.Equals(resource, expectedResource, StringComparison.Ordinal),
                $"{manifestPageEntry.Name} 必须使用约定的.br运行时资源");
            Assert(runtimePage!.ResourcePath == resource &&
                   runtimePage.UncompressedByteCount == uncompressedByteCount &&
                   runtimePage.PayloadByteCount == payloadByteCount &&
                   runtimePage.Encoding == encoding &&
                   runtimePage.ContentSha256 == contentSha256 &&
                   runtimePage.DecodedSha256 == decodedSha256 &&
                   runtimePage.Width == width && runtimePage.Height == height,
                $"运行时分页元数据必须与清单一致：{manifestPageEntry.Name}");
            var expectedUncompressedByteCount = checked((long)width * height * 4);
            Assert(uncompressedByteCount == expectedUncompressedByteCount &&
                   uncompressedByteCount <= manifestMaximumDecodedPageBytes,
                $"{manifestPageEntry.Name} 的uncompressedByteCount必须等于width×height×4，" +
                $"且不超过清单页上限；实际 {uncompressedByteCount} bytes，上限 " +
                $"{manifestMaximumDecodedPageBytes} bytes");
            Assert((encoding == "pbgra32" ||
                    encoding == "pbgra32-delta-sub-v1") &&
                   payloadByteCount is > 0 and <= MaximumSpritePagePayloadBytes &&
                   (encoding != "pbgra32" ||
                    payloadByteCount == uncompressedByteCount),
                $"{manifestPageEntry.Name} encoding must be supported; payloadByteCount must be " +
                "1..32MiB and must equal the atlas length for direct pages");
            Assert(runtimePage.Frames.Count == logicalCount &&
                   manifestFrames.EnumerateObject().Count() == logicalCount,
                $"分页帧数必须与清单一致：{manifestPageEntry.Name}");
            totalPageFrames += logicalCount;
            _ = pageResources.Add(resource);

            var compressedPath = FindWorkspaceFile(resource.Split('/'));
            Assert(new FileInfo(compressedPath).Length == compressedByteCount &&
                   compressedByteCount is > 0 &&
                   compressedByteCount <= payloadByteCount,
                $"分页Brotli资源实际字节数必须匹配compressedByteCount、不得为空，且不得超过" +
                $"Brotli解压payload长度 {payloadByteCount} bytes：" +
                $"{manifestPageEntry.Name}");
            AssertCanonicalSha256(sourceFingerprint,
                $"{manifestPageEntry.Name}/sourceFingerprint");
            AssertCanonicalSha256(contentSha256,
                $"{manifestPageEntry.Name}/contentSha256");
            AssertCanonicalSha256(decodedSha256,
                $"{manifestPageEntry.Name}/decodedSha256");
            var pageSourcePaths = manifestFrames.EnumerateObject()
                .Select(frame => frame.Name)
                .ToArray();
            Assert(string.Equals(
                    sourceFingerprint,
                    ComputeSourceSetFingerprint(pageSourcePaths),
                    StringComparison.Ordinal),
                $"{manifestPageEntry.Name} 的sourceFingerprint必须与本页源PNG路径、顺序及实际内容一致");
            Assert(string.Equals(
                    contentSha256,
                    ComputeFileSha256(compressedPath),
                    StringComparison.Ordinal),
                $"{manifestPageEntry.Name} 的contentSha256必须匹配实际Brotli内容");
            var uniqueRegions = new HashSet<(int X, int Y, int Width, int Height)>();
            foreach (var manifestFrameEntry in manifestFrames.EnumerateObject())
            {
                _ = sourceFrames.Add(manifestFrameEntry.Name);
                Assert(runtimePage.Frames.Contains(manifestFrameEntry.Name),
                    $"运行时分页缺少帧：{manifestPageEntry.Name}/{manifestFrameEntry.Name}");
                var runtimeFrame = GetSpriteFrameInfo(
                    runtimePage.Frames[manifestFrameEntry.Name]!);
                var frameDescriptor = manifestFrameEntry.Value;
                Assert(runtimeFrame.PageName == manifestPageEntry.Name &&
                       runtimeFrame.Name == manifestFrameEntry.Name &&
                       runtimeFrame.X == frameDescriptor.GetProperty("x").GetInt32() &&
                       runtimeFrame.Y == frameDescriptor.GetProperty("y").GetInt32() &&
                       runtimeFrame.Width == frameDescriptor.GetProperty("width").GetInt32() &&
                       runtimeFrame.Height == frameDescriptor.GetProperty("height").GetInt32() &&
                       runtimeFrame.DestinationX == frameDescriptor.GetProperty("destinationX").GetInt32() &&
                       runtimeFrame.DestinationY == frameDescriptor.GetProperty("destinationY").GetInt32(),
                    $"运行时Frame必须与v4清单一致：{manifestPageEntry.Name}/{manifestFrameEntry.Name}");
                _ = uniqueRegions.Add((
                    runtimeFrame.X,
                    runtimeFrame.Y,
                    runtimeFrame.Width,
                    runtimeFrame.Height));
            }

            Assert(uniqueRegions.Count == uniqueCount,
                $"分页uniqueSpriteCount必须与实际区域数一致：{manifestPageEntry.Name}");
        }

        Assert(totalPageFrames == manifestPageFrameCount &&
               sourceFrames.SetEquals(expectedSourcePaths) &&
               pageResources.Count == manifestPageCount,
            $"{manifestPageCount}页必须动态覆盖清单声明的{manifestPageFrameCount}个PageFrame和" +
            $"运行时声明的{expectedSourcePaths.Length}个源逻辑帧");
        AssertProjectAndAssemblyResourceContract(pageResources);
        AssertRuntimeDoesNotUseWpfBitmapDecoders();
    }

    private static void AssertSpriteAtlasDecodedPageLimitFailClosed()
    {
        var validateRootLimit = typeof(MainWindow).GetMethod(
            "ValidateSpriteAtlasDecodedPageLimit",
            StaticFlags)
            ?? throw new InvalidOperationException(
                "找不到ValidateSpriteAtlasDecodedPageLimit，无法验证24MiB fail-closed契约");
        var validatePageSize = typeof(MainWindow).GetMethod(
            "ValidateSpriteAtlasPageDecodedSize",
            StaticFlags)
            ?? throw new InvalidOperationException(
                "找不到ValidateSpriteAtlasPageDecodedSize，无法验证分页解码尺寸fail-closed契约");
        var validateContentHash = typeof(MainWindow).GetMethod(
            "ValidateSpriteAtlasPageContentHash",
            StaticFlags,
            binder: null,
            types:
            [
                typeof(Stream),
                typeof(int),
                typeof(string),
                typeof(string),
                typeof(CancellationToken)
            ],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "找不到流式ValidateSpriteAtlasPageContentHash，无法验证Brotli分页精确长度与内容哈希契约");

        AssertThrowsInvalidOperation(
            () => validateRootLimit.Invoke(null, new object[] { 0 }),
            "manifest缺失maxDecodedPageBytes时反序列化默认0，运行时必须fail-closed");
        AssertThrowsInvalidOperation(
            () => validateRootLimit.Invoke(
                null,
                new object[] { checked((int)MaximumDecodedSpritePageBytes + 1) }),
            "manifest的maxDecodedPageBytes超过24MiB时运行时必须fail-closed");
        _ = validateRootLimit.Invoke(
            null,
            new object[] { checked((int)MaximumDecodedSpritePageBytes) });

        const int width = 128;
        const int height = 128;
        const int decodedByteCount = width * height * 4;
        AssertThrowsInvalidOperation(
            () => validatePageSize.Invoke(
                null,
                new object[]
                {
                    "oversized-page",
                    "pbgra32",
                    width,
                    height,
                    decodedByteCount,
                    decodedByteCount,
                    decodedByteCount,
                    decodedByteCount - 1
                }),
            "分页uncompressedByteCount超过manifest根上限时运行时必须fail-closed");
        AssertThrowsInvalidOperation(
            () => validatePageSize.Invoke(
                null,
                new object[]
                {
                    "mismatched-page",
                    "pbgra32",
                    width,
                    height,
                    decodedByteCount - 4,
                    decodedByteCount - 4,
                    decodedByteCount - 4,
                    decodedByteCount
                }),
            "分页uncompressedByteCount不等于width×height×4时运行时必须fail-closed");
        AssertThrowsInvalidOperation(
            () => validatePageSize.Invoke(
                null,
                new object[]
                {
                    "oversized-compressed-page",
                    "pbgra32",
                    width,
                    height,
                    decodedByteCount,
                    decodedByteCount,
                    decodedByteCount + 1,
                    decodedByteCount
                }),
            "分页compressedByteCount超过解码后的Pbgra32长度时运行时必须fail-closed");
        _ = validatePageSize.Invoke(
            null,
            new object[]
            {
                "valid-page",
                "pbgra32",
                width,
                height,
                decodedByteCount,
                decodedByteCount,
                decodedByteCount,
                checked((int)MaximumDecodedSpritePageBytes)
            });

        AssertThrowsInvalidOperation(
            () => validatePageSize.Invoke(
                null,
                new object[]
                {
                    "unknown-encoding",
                    "pbgra32-delta-xor-v0",
                    width,
                    height,
                    decodedByteCount,
                    decodedByteCount,
                    decodedByteCount,
                    checked((int)MaximumDecodedSpritePageBytes)
                }),
            "manifest page encoding不在白名单时运行时必须fail-closed");
        AssertThrowsInvalidOperation(
            () => validatePageSize.Invoke(
                null,
                new object[]
                {
                    "direct-payload-mismatch",
                    "pbgra32",
                    width,
                    height,
                    decodedByteCount,
                    decodedByteCount - 1,
                    decodedByteCount - 1,
                    checked((int)MaximumDecodedSpritePageBytes)
                }),
            "direct页的payloadByteCount必须严格等于uncompressedByteCount");
        AssertThrowsInvalidOperation(
            () => validatePageSize.Invoke(
                null,
                new object[]
                {
                    "oversized-delta-payload",
                    "pbgra32-delta-sub-v1",
                    width,
                    height,
                    decodedByteCount,
                    checked((int)MaximumSpritePagePayloadBytes + 1),
                    decodedByteCount,
                    checked((int)MaximumDecodedSpritePageBytes)
                }),
            "delta页payloadByteCount超过32MiB时运行时必须fail-closed");
        _ = validatePageSize.Invoke(
            null,
            new object[]
            {
                "valid-delta-page",
                "pbgra32-delta-sub-v1",
                width,
                height,
                decodedByteCount,
                decodedByteCount / 2,
                decodedByteCount / 4,
                checked((int)MaximumDecodedSpritePageBytes)
            });

        var compressedBytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)((index * 37 + 11) & 0xff))
            .ToArray();
        var expectedSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(compressedBytes))
            .ToLowerInvariant();
        using var validCompressedStream = new MemoryStream(
            compressedBytes,
            writable: false);
        _ = validateContentHash.Invoke(
            null,
            new object[]
            {
                validCompressedStream,
                compressedBytes.Length,
                "synthetic-page.pbgra.br",
                expectedSha256,
                CancellationToken.None
            });
        Assert(validCompressedStream.Position == compressedBytes.Length,
            "压缩SHA流校验必须精确消费manifest声明的全部字节");

        var tamperedBytes = compressedBytes.ToArray();
        tamperedBytes[tamperedBytes.Length / 2] ^= 0x40;
        AssertThrowsInvalidData(
            () => validateContentHash.Invoke(
                null,
                new object[]
                {
                    new MemoryStream(tamperedBytes, writable: false),
                    tamperedBytes.Length,
                    "tampered-page.pbgra.br",
                    expectedSha256,
                    CancellationToken.None
                }),
            "Brotli分页任一压缩字节被篡改时必须在解压前fail-closed");
        AssertThrowsInvalidData(
            () => validateContentHash.Invoke(
                null,
                new object[]
                {
                    new MemoryStream(compressedBytes, writable: false),
                    compressedBytes.Length,
                    "noncanonical-hash-page.pbgra.br",
                    expectedSha256.ToUpperInvariant(),
                    CancellationToken.None
                }),
            "Brotli分页contentSha256不是64位小写十六进制时必须fail-closed");

        AssertThrowsInvalidData(
            () => validateContentHash.Invoke(
                null,
                new object[]
                {
                    new MemoryStream(compressedBytes[..^1], writable: false),
                    compressedBytes.Length,
                    "truncated-page.pbgra.br",
                    expectedSha256,
                    CancellationToken.None
                }),
            "压缩资源流少于manifest compressedByteCount一字节时必须fail-closed");
        var compressedWithTrailingByte = new byte[compressedBytes.Length + 1];
        compressedBytes.CopyTo(compressedWithTrailingByte, 0);
        compressedWithTrailingByte[^1] = 0x5a;
        AssertThrowsInvalidData(
            () => validateContentHash.Invoke(
                null,
                new object[]
                {
                    new MemoryStream(compressedWithTrailingByte, writable: false),
                    compressedBytes.Length,
                    "trailing-page.pbgra.br",
                    expectedSha256,
                    CancellationToken.None
                }),
            "压缩资源流超过manifest compressedByteCount时也必须fail-closed，不得忽略尾随字节");
    }

    private static void AssertSpritePagePayloadEncodingContract()
    {
        var decodePayload = typeof(MainWindow).GetMethod(
            "DecodeSpritePagePayload",
            StaticFlags)
            ?? throw new InvalidOperationException(
                "找不到DecodeSpritePagePayload，无法验证direct/delta重建契约");
        var decodeStream = typeof(MainWindow).GetMethod(
            "DecodeSpritePageStream",
            StaticFlags,
            binder: null,
            types:
            [
                typeof(string),
                typeof(string),
                typeof(Stream),
                typeof(int),
                typeof(byte[]),
                typeof(int),
                typeof(int),
                typeof(int[]),
                typeof(string),
                typeof(CancellationToken)
            ],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "找不到DecodeSpritePageStream，无法验证无大scratch的流式direct/delta重建契约");
        Assert(typeof(MainWindow).GetField(
                   "_spritePageCompressedBytes",
                   InstanceFlags) is null &&
               typeof(MainWindow).GetField(
                   "_spritePagePayloadBytes",
                   InstanceFlags) is null,
            "MainWindow不得再永久持有最大压缩页或最大payload的大byte[] scratch");

        static string Hash(byte[] bytes) => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

        static byte[] CompressBrotli(byte[] payload)
        {
            using var output = new MemoryStream();
            using (var compressor = new BrotliStream(
                       output,
                       CompressionLevel.Optimal,
                       leaveOpen: true))
            {
                compressor.Write(payload);
            }

            return output.ToArray();
        }

        static void AppendHeader(
            List<byte> payload,
            ushort x,
            ushort y,
            ushort width,
            ushort height)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(header, x);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], y);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], width);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], height);
            payload.AddRange(header.ToArray());
        }

        static object[] Arguments(
            string encoding,
            byte[] payload,
            int expectedPayloadByteCount,
            byte[] output,
            int atlasWidth,
            int atlasHeight,
            int[] descriptors,
            string decodedSha256) =>
        [
            "synthetic-page.pbgra.br",
            encoding,
            payload,
            expectedPayloadByteCount,
            output,
            atlasWidth,
            atlasHeight,
            descriptors,
            decodedSha256,
            CancellationToken.None
        ];

        static object[] StreamArguments(
            string encoding,
            Stream payloadStream,
            int expectedPayloadByteCount,
            byte[] output,
            int atlasWidth,
            int atlasHeight,
            int[] descriptors,
            string decodedSha256) =>
        [
            "synthetic-page.pbgra.br",
            encoding,
            payloadStream,
            expectedPayloadByteCount,
            output,
            atlasWidth,
            atlasHeight,
            descriptors,
            decodedSha256,
            CancellationToken.None
        ];

        var directPayload = new byte[]
        {
            1, 2, 3, 4,
            5, 6, 7, 8
        };
        var directOutput = new byte[directPayload.Length];
        _ = decodePayload.Invoke(
            null,
            Arguments(
                "pbgra32",
                directPayload,
                directPayload.Length,
                directOutput,
                2,
                1,
                [],
                Hash(directPayload)));
        Assert(directOutput.SequenceEqual(directPayload),
            "direct Pbgra payload必须逐字节还原并校验最终atlas SHA256");

        var streamedDirectOutput = new byte[directPayload.Length];
        _ = decodeStream.Invoke(
            null,
            StreamArguments(
                "pbgra32",
                new MemoryStream(directPayload, writable: false),
                directPayload.Length,
                streamedDirectOutput,
                2,
                1,
                [],
                Hash(directPayload)));
        Assert(streamedDirectOutput.SequenceEqual(directPayload),
            "direct payload必须可从流直接精确读入最终decodedPixels，不依赖整页payload scratch");
        var directBrotliBytes = CompressBrotli(directPayload);
        using var directCompressedStream = new MemoryStream(
            directBrotliBytes,
            writable: false);
        using var directBrotliStream = new BrotliStream(
            directCompressedStream,
            CompressionMode.Decompress,
            leaveOpen: false);
        var brotliDirectOutput = new byte[directPayload.Length];
        _ = decodeStream.Invoke(
            null,
            StreamArguments(
                "pbgra32",
                directBrotliStream,
                directPayload.Length,
                brotliDirectOutput,
                2,
                1,
                [],
                Hash(directPayload)));
        Assert(brotliDirectOutput.SequenceEqual(directPayload),
            "BrotliStream必须可将direct payload直接流式解压到最终decodedPixels");
        AssertThrowsInvalidData(
            () => decodeStream.Invoke(
                null,
                StreamArguments(
                    "pbgra32",
                    new MemoryStream(directPayload[..^1], writable: false),
                    directPayload.Length,
                    new byte[directPayload.Length],
                    2,
                    1,
                    [],
                    Hash(directPayload))),
            "direct payload流比expectedPayloadByteCount少一字节时必须fail-closed");
        var directWithTrailingByte = new byte[directPayload.Length + 1];
        directPayload.CopyTo(directWithTrailingByte, 0);
        directWithTrailingByte[^1] = 0x7f;
        AssertThrowsInvalidData(
            () => decodeStream.Invoke(
                null,
                StreamArguments(
                    "pbgra32",
                    new MemoryStream(directWithTrailingByte, writable: false),
                    directPayload.Length,
                    new byte[directPayload.Length],
                    2,
                    1,
                    [],
                    Hash(directPayload))),
            "direct payload流在expectedPayloadByteCount后仍有尾随字节时必须fail-closed");

        var deltaPayloadBuilder = new List<byte>();
        AppendHeader(deltaPayloadBuilder, 0, 0, 2, 2);
        deltaPayloadBuilder.AddRange(Enumerable.Range(1, 16).Select(value => (byte)value));
        AppendHeader(deltaPayloadBuilder, 0, 0, 1, 1);
        deltaPayloadBuilder.AddRange(new byte[] { 1, 1, 1, 1 });
        AppendHeader(deltaPayloadBuilder, 0, 0, 0, 0);
        var deltaPayload = deltaPayloadBuilder.ToArray();
        var deltaDescriptors = new[]
        {
            0, 0, 2, 2, 0, 0,
            2, 0, 2, 2, -1, 0,
            2, 0, 2, 2, -1, 0
        };
        var expectedDeltaAtlas = new byte[]
        {
            1, 2, 3, 4,
            5, 6, 7, 8,
            0, 0, 0, 0,
            2, 3, 4, 5,
            9, 10, 11, 12,
            13, 14, 15, 16,
            0, 0, 0, 0,
            9, 10, 11, 12
        };
        var deltaOutput = new byte[expectedDeltaAtlas.Length];
        _ = decodePayload.Invoke(
            null,
            Arguments(
                "pbgra32-delta-sub-v1",
                deltaPayload,
                deltaPayload.Length,
                deltaOutput,
                4,
                2,
                deltaDescriptors,
                Hash(expectedDeltaAtlas)));
        Assert(deltaOutput.SequenceEqual(expectedDeltaAtlas),
            "delta-sub必须按manifest frames顺序累加完整display帧，越界透明补0且重复sprite覆盖一致");

        var streamedDeltaOutput = new byte[expectedDeltaAtlas.Length];
        _ = decodeStream.Invoke(
            null,
            StreamArguments(
                "pbgra32-delta-sub-v1",
                new MemoryStream(deltaPayload, writable: false),
                deltaPayload.Length,
                streamedDeltaOutput,
                4,
                2,
                deltaDescriptors,
                Hash(expectedDeltaAtlas)));
        Assert(streamedDeltaOutput.SequenceEqual(expectedDeltaAtlas),
            "delta-sub必须逐头、逐行从流重建相同atlas，不得依赖完整payload byte[]");
        var deltaBrotliBytes = CompressBrotli(deltaPayload);
        using var deltaCompressedStream = new MemoryStream(
            deltaBrotliBytes,
            writable: false);
        using var deltaBrotliStream = new BrotliStream(
            deltaCompressedStream,
            CompressionMode.Decompress,
            leaveOpen: false);
        var brotliDeltaOutput = new byte[expectedDeltaAtlas.Length];
        _ = decodeStream.Invoke(
            null,
            StreamArguments(
                "pbgra32-delta-sub-v1",
                deltaBrotliStream,
                deltaPayload.Length,
                brotliDeltaOutput,
                4,
                2,
                deltaDescriptors,
                Hash(expectedDeltaAtlas)));
        Assert(brotliDeltaOutput.SequenceEqual(expectedDeltaAtlas),
            "BrotliStream必须边解压delta-sub payload边按帧重建atlas，不得先物化整页payload");
        AssertThrowsInvalidData(
            () => decodeStream.Invoke(
                null,
                StreamArguments(
                    "pbgra32-delta-sub-v1",
                    new MemoryStream(deltaPayload[..^1], writable: false),
                    deltaPayload.Length,
                    new byte[expectedDeltaAtlas.Length],
                    4,
                    2,
                    deltaDescriptors,
                    Hash(expectedDeltaAtlas))),
            "delta-sub payload流任一尾部字节截断时必须fail-closed");
        var deltaWithTrailingByte = new byte[deltaPayload.Length + 1];
        deltaPayload.CopyTo(deltaWithTrailingByte, 0);
        deltaWithTrailingByte[^1] = 0x7f;
        AssertThrowsInvalidData(
            () => decodeStream.Invoke(
                null,
                StreamArguments(
                    "pbgra32-delta-sub-v1",
                    new MemoryStream(deltaWithTrailingByte, writable: false),
                    deltaPayload.Length,
                    new byte[expectedDeltaAtlas.Length],
                    4,
                    2,
                    deltaDescriptors,
                    Hash(expectedDeltaAtlas))),
            "delta-sub payload流在expectedPayloadByteCount后有尾随字节时必须fail-closed");

        var tamperedDirect = directPayload.ToArray();
        tamperedDirect[0] ^= 0x40;
        AssertThrowsInvalidData(
            () => decodePayload.Invoke(
                null,
                Arguments(
                    "pbgra32",
                    tamperedDirect,
                    tamperedDirect.Length,
                    new byte[tamperedDirect.Length],
                    2,
                    1,
                    [],
                    Hash(directPayload))),
            "direct payload被篡改后最终decodedSha256必须fail-closed");
        AssertThrowsInvalidData(
            () => decodePayload.Invoke(
                null,
                Arguments(
                    "pbgra32",
                    directPayload,
                    directPayload.Length,
                    new byte[directPayload.Length],
                    2,
                    1,
                    [],
                    Hash(directPayload).ToUpperInvariant())),
            "decodedSha256必须严格为64位小写十六进制");
        AssertThrowsInvalidData(
            () => decodePayload.Invoke(
                null,
                Arguments(
                    "pbgra32",
                    directPayload,
                    directPayload.Length + 1,
                    new byte[directPayload.Length],
                    2,
                    1,
                    [],
                    Hash(directPayload))),
            "payload容器短于expectedPayloadByteCount时必须fail-closed");
        AssertThrowsInvalidData(
            () => decodePayload.Invoke(
                null,
                Arguments(
                    "pbgra32",
                    directPayload,
                    directPayload.Length,
                    new byte[directPayload.Length - 1],
                    2,
                    1,
                    [],
                    Hash(directPayload))),
            "最终atlas缓冲区长度不等于width×height×4时必须fail-closed");
        AssertThrowsInvalidData(
            () => decodePayload.Invoke(
                null,
                Arguments(
                    "pbgra32-delta-xor-v0",
                    directPayload,
                    directPayload.Length,
                    new byte[directPayload.Length],
                    2,
                    1,
                    [],
                    Hash(directPayload))),
            "payload解码阶段也必须拒绝encoding白名单以外的值");

        var oneFrameDescriptor = new[] { 0, 0, 1, 1, 0, 0 };
        var ignoredHash = new string('0', 64);
        void AssertBadDelta(byte[] malformed, int[] descriptors, string message)
        {
            AssertThrowsInvalidData(
                () => decodePayload.Invoke(
                    null,
                    Arguments(
                        "pbgra32-delta-sub-v1",
                        malformed,
                        malformed.Length,
                        new byte[16],
                        2,
                        2,
                        descriptors,
                        ignoredHash)),
                message);
        }

        AssertBadDelta(new byte[7], oneFrameDescriptor,
            "delta header截断时必须fail-closed");

        var partialEmptyHeader = new List<byte>();
        AppendHeader(partialEmptyHeader, 0, 0, 0, 1);
        AssertBadDelta(partialEmptyHeader.ToArray(), oneFrameDescriptor,
            "delta空块只允许x=y=w=h全部为0");

        var outOfBoundsHeader = new List<byte>();
        AppendHeader(outOfBoundsHeader, 399, 0, 1, 1);
        outOfBoundsHeader.AddRange(new byte[4]);
        AssertBadDelta(outOfBoundsHeader.ToArray(), oneFrameDescriptor,
            "delta矩形越过399x509 display边界时必须fail-closed");

        var truncatedBlock = new List<byte>();
        AppendHeader(truncatedBlock, 0, 0, 1, 1);
        truncatedBlock.AddRange(new byte[3]);
        AssertBadDelta(truncatedBlock.ToArray(), oneFrameDescriptor,
            "delta像素块截断时必须fail-closed");

        var trailingByte = new List<byte>();
        AppendHeader(trailingByte, 0, 0, 0, 0);
        trailingByte.Add(0xff);
        var trailingByteWithinExpected = trailingByte.ToArray();
        AssertThrowsInvalidData(
            () => decodePayload.Invoke(
                null,
                Arguments(
                    "pbgra32-delta-sub-v1",
                    trailingByteWithinExpected,
                    trailingByteWithinExpected.Length,
                    new byte[16],
                    2,
                    2,
                    oneFrameDescriptor,
                    ignoredHash)),
            "delta流在所有帧后仍有尾随字节时必须fail-closed");

        var emptyHeader = new List<byte>();
        AppendHeader(emptyHeader, 0, 0, 0, 0);
        AssertBadDelta(emptyHeader.ToArray(), new[] { 2, 0, 1, 1, 0, 0 },
            "delta atlas descriptor越界时必须fail-closed");
        AssertBadDelta(emptyHeader.ToArray(),
            new[] { int.MaxValue, 0, int.MaxValue, 1, 0, 0 },
            "delta atlas descriptor整数溢出风险必须fail-closed");

        var inconsistentRepeat = new List<byte>();
        AppendHeader(inconsistentRepeat, 0, 0, 1, 1);
        inconsistentRepeat.AddRange(new byte[] { 1, 2, 3, 4 });
        AppendHeader(inconsistentRepeat, 0, 0, 1, 1);
        inconsistentRepeat.AddRange(new byte[] { 1, 0, 0, 0 });
        AssertBadDelta(
            inconsistentRepeat.ToArray(),
            new[]
            {
                0, 0, 1, 1, 0, 0,
                0, 0, 1, 1, 0, 0
            },
            "同一atlas sprite区域被重复引用时重建像素必须完全一致");

        var inconsistentDestinationRepeat = new List<byte>();
        AppendHeader(inconsistentDestinationRepeat, 0, 0, 0, 0);
        AppendHeader(inconsistentDestinationRepeat, 0, 0, 0, 0);
        AssertBadDelta(
            inconsistentDestinationRepeat.ToArray(),
            new[]
            {
                0, 0, 1, 1, 0, 0,
                0, 0, 1, 1, 1, 0
            },
            "同一atlas sprite区域被重复引用时destination必须完全一致");
    }

    private static void AssertThrowsInvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is InvalidDataException or EndOfStreamException)
        {
            return;
        }
        catch (InvalidDataException)
        {
            return;
        }
        catch (EndOfStreamException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertThrowsInvalidOperation(Action action, string message)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is InvalidOperationException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static string[] BuildExpectedSourceResourcePaths(
        bool includeOptionalRoamWave)
    {
        var assetsDirectory = Path.GetDirectoryName(
            FindWorkspaceFile("Assets", "luban-idle.png"))!;
        var paths = new List<string> { "Assets/luban-idle.png" };
        paths.AddRange(GetExpectedWakeFrameNames()
            .Select(name => $"Assets/{name}"));
        foreach (var direction in new[] { "left", "bottom" })
        {
            paths.AddRange(Enumerable.Range(1, ExpectedEdgePeekFrameCount).Select(frameNumber =>
                $"Assets/luban-edge-{direction}-smooth-{frameNumber:000}.png"));
        }
        // Keep this order byte-for-byte aligned with
        // build_sprite_atlas.py REQUIRED_ROAM_SEQUENCES because it is part of
        // sourceSetFingerprint, even though runtime lookup is name-based.
        foreach (var sequence in new[] { "flight", "boarding" })
        {
            paths.AddRange(GetExpectedRoamFrameNames(sequence, required: true)
                .Select(name => $"Assets/{name}"));
        }
        if (includeOptionalRoamWave)
        {
            paths.AddRange(GetExpectedRoamFrameNames("wave", required: false)
                .Select(name => $"Assets/{name}"));
        }

        foreach (var (phase, expectedFrameCount) in new[]
                 {
                     (Phase: "enter", ExpectedFrameCount: 33),
                     (Phase: "hold", ExpectedFrameCount: 48)
                 })
        {
            var reminderNames = Directory.EnumerateFiles(
                    assetsDirectory,
                    $"luban-reminder-{phase}-*.png",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var expectedReminderNames = Enumerable.Range(1, expectedFrameCount)
                .Select(frameNumber =>
                    $"luban-reminder-{phase}-{frameNumber:000}.png")
                .ToArray();
            Assert(reminderNames.SequenceEqual(expectedReminderNames),
                $"reminder-{phase}源资源必须严格连续编号为" +
                $"001..{expectedFrameCount:000}");
            paths.AddRange(reminderNames.Select(name => $"Assets/{name}"));
        }

        foreach (var action in new[] { "yawn", "cry", "cute", "like", "eat", "wave", "think" })
        {
            var actionNames = Directory.EnumerateFiles(
                    assetsDirectory,
                    $"luban-{action}-smooth-*.png",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var expectedActionNames = Enumerable.Range(1, actionNames.Length)
                .Select(frameNumber => $"luban-{action}-smooth-{frameNumber:000}.png")
                .ToArray();
            Assert(actionNames.Length >= 50 && actionNames.SequenceEqual(expectedActionNames),
                $"{action} smooth源资源必须从001开始连续编号且至少50帧");
            paths.AddRange(actionNames.Select(name => $"Assets/{name}"));
            paths.AddRange(Enumerable.Range(1, 48).Select(frameNumber =>
                $"Assets/luban-{action}-loop-{frameNumber:000}.png"));
        }

        var result = paths.ToArray();
        Assert(result.Length > 0 &&
               result.Distinct(StringComparer.Ordinal).Count() == result.Length,
            "运行时源PNG路径清单不得为空或包含重复项");
        return result;
    }

    private static string[] GetExpectedRoamFrameNames(
        string sequence,
        bool required = true)
    {
        Assert(sequence is "boarding" or "flight" or "wave",
            $"不支持的熊猫坐骑序列：{sequence}");
        var assetsDirectory = Path.GetDirectoryName(
            FindWorkspaceFile("Assets", "luban-idle.png"))!;
        var actualNames = Directory.EnumerateFiles(
                assetsDirectory,
                $"luban-roam-{sequence}-*.png",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expectedNames = Enumerable.Range(1, actualNames.Length)
            .Select(frameNumber =>
                $"luban-roam-{sequence}-{frameNumber:000}.png")
            .ToArray();
        if (!required && actualNames.Length == 0)
        {
            return [];
        }

        Assert(actualNames.Length >= MinimumRoamSequenceFrameCount &&
               actualNames.SequenceEqual(expectedNames),
            $"熊猫坐骑{sequence}源资源必须从{sequence}-001开始连续编号，至少" +
            $"{MinimumRoamSequenceFrameCount}帧；实际 {actualNames.Length} 帧");

        var uniqueContentHashes = actualNames
            .Select(name => ComputeFileSha256(Path.Combine(assetsDirectory, name)))
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert(uniqueContentHashes == actualNames.Length,
            $"熊猫坐骑{sequence}的{actualNames.Length}帧必须全部是独立姿势，" +
            $"实际只有{uniqueContentHashes}个不同PNG内容");
        return actualNames;
    }

    private static void AssertCanonicalSha256(string value, string name)
    {
        Assert(value.Length == 64 && value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{name}必须是64位小写十六进制SHA256");
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string ComputeSourceSetFingerprint(IEnumerable<string> resourcePaths)
    {
        using var fingerprint = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var resourcePath in resourcePaths)
        {
            fingerprint.AppendData(System.Text.Encoding.UTF8.GetBytes(resourcePath));
            fingerprint.AppendData(new byte[] { 0 });
            using var stream = File.OpenRead(FindWorkspaceFile(resourcePath.Split('/')));
            fingerprint.AppendData(System.Security.Cryptography.SHA256.HashData(stream));
        }

        return Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AssertProjectAndAssemblyResourceContract(
        HashSet<string> expectedPageResources)
    {
        var projectPath = FindWorkspaceFile("DesktopPet.csproj");
        var project = XDocument.Load(projectPath);
        var includes = project.Descendants()
            .Where(element => element.Name.LocalName == "Resource")
            .Select(element => ((string?)element.Attribute("Include") ?? string.Empty)
                .Replace('\\', '/'))
            .ToArray();
        Assert(includes.Length == 4 &&
               includes.Contains("Assets/sprite-pages/*.pbgra.br", StringComparer.OrdinalIgnoreCase) &&
               includes.Contains("Assets/luban-sprite-pages.json", StringComparer.OrdinalIgnoreCase) &&
               includes.Contains("Assets/luban-pillow-layer.png", StringComparer.OrdinalIgnoreCase) &&
               includes.Contains(
                   "Assets/luban-idle-no-snore-patch-source.png",
                   StringComparer.OrdinalIgnoreCase),
            "csproj只能嵌入无损Brotli分页通配符和v4 manifest");
        Assert(!includes.Any(include =>
                include.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                (include.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                 !include.Equals("Assets/luban-pillow-layer.png", StringComparison.OrdinalIgnoreCase) &&
                 !include.Equals(
                     "Assets/luban-idle-no-snore-patch-source.png",
                     StringComparison.OrdinalIgnoreCase))),
            "csproj不得嵌入分页预览PNG、源PNG或旧单atlas");

        var assembly = typeof(MainWindow).Assembly;
        var generatedResourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(generatedResourceName)
            ?? throw new InvalidOperationException("找不到WPF生成资源流");
        using var reader = new ResourceReader(stream);
        var assetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumerator = reader.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerator.Key is string key &&
                key.Replace('\\', '/').StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            {
                _ = assetKeys.Add(key.Replace('\\', '/'));
            }
        }

        var expectedAssets = expectedPageResources
            .Select(resource => resource.ToLowerInvariant())
            .Append("assets/luban-sprite-pages.json")
            .Append("assets/luban-pillow-layer.png")
            .Append("assets/luban-idle-no-snore-patch-source.png")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(assetKeys.SetEquals(expectedAssets) &&
               assetKeys.Count == expectedPageResources.Count + 3,
            $"主程序集Assets资源必须严格等于{expectedPageResources.Count}个Brotli分页和一个v4 manifest");
        Assert(!assetKeys.Any(key =>
                key.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                (key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                 !key.Equals("assets/luban-pillow-layer.png", StringComparison.OrdinalIgnoreCase) &&
                 !key.Equals(
                     "assets/luban-idle-no-snore-patch-source.png",
                     StringComparison.OrdinalIgnoreCase))),
            "主程序集不得包含分页预览PNG、旧单atlas或源PNG");
    }

    private static void AssertRuntimeDoesNotUseWpfBitmapDecoders()
    {
        var workspaceRoot = Path.GetDirectoryName(FindWorkspaceFile("DesktopPet.csproj"))
            ?? throw new InvalidOperationException("无法定位主项目目录");
        var forbiddenTypeNames = new[]
        {
            "BitmapDecoder",
            "BitmapImage",
            "FormatConvertedBitmap"
        };
        var runtimeSources = Directory.EnumerateFiles(
                workspaceRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path =>
            {
                var relativePath = Path.GetRelativePath(workspaceRoot, path)
                    .Replace('\\', '/');
                return !relativePath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                       !relativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !relativePath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !relativePath.StartsWith(".codex_tmp/", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        foreach (var sourcePath in runtimeSources)
        {
            var source = File.ReadAllText(sourcePath);
            foreach (var forbiddenTypeName in forbiddenTypeNames)
            {
                Assert(!source.Contains(forbiddenTypeName, StringComparison.Ordinal),
                    $"运行时代码不得使用{forbiddenTypeName}解码图集：{Path.GetFileName(sourcePath)}");
            }
        }
    }

    private static bool FileHashesMatch(string firstPath, string secondPath)
    {
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        var firstHash = System.Security.Cryptography.SHA256.HashData(first);
        var secondHash = System.Security.Cryptography.SHA256.HashData(second);
        return firstHash.SequenceEqual(secondHash);
    }

    private static void AssertGeneratedManifestMatches(
        string generatedPath,
        string committedPath)
    {
        using var generatedDocument = JsonDocument.Parse(File.ReadAllText(generatedPath));
        using var committedDocument = JsonDocument.Parse(File.ReadAllText(committedPath));
        var generated = generatedDocument.RootElement;
        var committed = committedDocument.RootElement;
        foreach (var property in new[]
                 {
                     "version",
                     "displayWidth",
                     "displayHeight",
                     "sourceFrameCount",
                     "pageFrameCount",
                     "maxDecodedPageBytes"
                 })
        {
            Assert(generated.GetProperty(property).GetInt32() ==
                   committed.GetProperty(property).GetInt32(),
                $"提交图集清单的 {property} 与可重复构建结果不一致");
        }
        Assert(generated.GetProperty("compression").GetString() ==
               committed.GetProperty("compression").GetString() &&
               committed.GetProperty("compression").GetString() == "brotli",
            "提交图集与可重复构建清单必须使用相同的Brotli压缩契约");
        Assert(generated.GetProperty("sourceSetFingerprint").GetString() ==
               committed.GetProperty("sourceSetFingerprint").GetString(),
            "提交图集清单的sourceSetFingerprint与可重复构建结果不一致");

        var generatedPages = generated.GetProperty("pages");
        var committedPages = committed.GetProperty("pages");
        Assert(generatedPages.EnumerateObject().Count() ==
               committedPages.EnumerateObject().Count(),
            "提交图集清单的分页数与可重复构建结果不一致");
        foreach (var committedPage in committedPages.EnumerateObject())
        {
            Assert(generatedPages.TryGetProperty(committedPage.Name, out var generatedPage),
                $"可重复构建结果缺少分页 {committedPage.Name}");
            Assert(Path.GetFileName(
                       generatedPage.GetProperty("resource").GetString()) ==
                   Path.GetFileName(
                       committedPage.Value.GetProperty("resource").GetString()),
                $"分页 {committedPage.Name} 的资源文件名不一致");
            foreach (var property in new[]
                     {
                          "width",
                          "height",
                          "uncompressedByteCount",
                          "payloadByteCount",
                          "compressedByteCount",
                          "logicalFrameCount",
                          "uniqueSpriteCount"
                     })
            {
                Assert(generatedPage.GetProperty(property).GetInt32() ==
                       committedPage.Value.GetProperty(property).GetInt32(),
                    $"分页 {committedPage.Name} 的 {property} 与可重复构建结果不一致");
            }

            foreach (var property in new[]
                     {
                         "sourceFingerprint",
                         "encoding",
                         "contentSha256",
                         "decodedSha256"
                     })
            {
                Assert(generatedPage.GetProperty(property).GetString() ==
                       committedPage.Value.GetProperty(property).GetString(),
                    $"分页 {committedPage.Name} 的 {property} 与可重复构建结果不一致");
            }

            Assert(generatedPage.GetProperty("frames").GetRawText() ==
                   committedPage.Value.GetProperty("frames").GetRawText(),
                $"分页 {committedPage.Name} 的帧坐标与当前源PNG不一致");
        }
    }

    private static SpriteFrameInfo GetSpriteFrameInfo(object frame) => new(
        GetProperty<int>(frame, "X"),
        GetProperty<int>(frame, "Y"),
        GetProperty<int>(frame, "Width"),
        GetProperty<int>(frame, "Height"),
        GetProperty<int>(frame, "DestinationX"),
        GetProperty<int>(frame, "DestinationY"),
        GetProperty<string>(frame, "PageName"),
        GetProperty<string>(frame, "Name"));

    private static string FindWorkspaceFile(params string[] relativeParts)
    {
        var relativePath = Path.Combine(relativeParts);
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(startPath);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"找不到工作区文件：{relativePath}");
    }

    private static void AssertMotionTimelineContract(MainWindow window)
    {
        var motionFrameInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "MotionFrameInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var actionLoopFrameInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "ActionLoopFrameInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var todoMotionFrameInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "TodoMotionFrameInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var actionTransitionDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "ActionTransitionDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.MinValue);
        var actionLoopCycleCount = (int)(typeof(MainWindow).GetField(
                "ActionLoopCycleCount",
                StaticFlags)!.GetValue(null) ?? 0);
        var sixtyFpsInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
        Assert(motionFrameInterval == sixtyFpsInterval &&
               todoMotionFrameInterval == sixtyFpsInterval &&
               actionLoopFrameInterval == sixtyFpsInterval &&
               actionLoopCycleCount == 3,
            "普通动作、Todo与微循环必须统一使用精确60fps姿势间隔，微循环固定3轮");
        Assert(actionTransitionDuration == TimeSpan.Zero,
            "普通动作相邻姿势必须直接切换，ActionTransitionDuration 必须为 zero");

        var clips = GetField<Array>(window, "_reactionClips")
            .Cast<object>()
            .ToArray();
        Assert(clips.Length == 7, "删除跑步后应保留 7 组点击动作");
        var expectedActions = new[] { "yawn", "cry", "cute", "like", "eat", "wave", "think" };
        var actualActions = clips
            .Select(clip => GetProperty<string>(clip, "ActionName"))
            .ToArray();
        Assert(actualActions.SequenceEqual(expectedActions),
            $"点击动作应严格为 {string.Join(", ", expectedActions)}，实际 {string.Join(", ", actualActions)}");
        var expectedWakeFrameNames = GetExpectedWakeFrameNames();
        var wakeFrameCount = expectedWakeFrameNames.Length;
        Assert(GetField<Array>(window, "_wakeFrames").Length == wakeFrameCount,
            $"运行时wake长度必须由清单/Assets动态推导，实际 {GetField<Array>(window, "_wakeFrames").Length}，" +
            $"资源 {wakeFrameCount}");
        AssertEdgeFrameSequenceContract(window);

        foreach (var clip in clips)
        {
            var actionName = GetProperty<string>(clip, "ActionName");
            var expectedTimelineNames = BuildExpectedActionTimelineNames(actionName);
            var expectedMotionNames = BuildExpectedMotionFrameNames(actionName);
            var frames = GetClipFrames(clip).Cast<object>().ToArray();
            var actualMotionNames = frames
                .Select(frame => GetProperty<string>(frame, "Name"))
                .ToArray();
            Assert(actualMotionNames.SequenceEqual(expectedMotionNames),
                $"{actionName} 必须按资源名精确播放{wakeFrameCount}帧wake、dense动作、3轮48帧微循环及反向返回");
            var spriteFrames = GetClipFrames(clip)
                .Cast<object>()
                .Select(frame => GetSpriteFrameInfo(GetProperty<object>(frame, "Image")))
                .ToArray();
            var expectedPageName = $"action-{actionName}";
            Assert(spriteFrames.All(frame =>
                    frame.Name.Contains("luban-idle", StringComparison.Ordinal) ||
                    frame.Name.Contains("luban-wake-smooth-", StringComparison.Ordinal)
                        ? string.Equals(frame.PageName, "idle", StringComparison.Ordinal) ||
                          frame.PageName.StartsWith("idle-part-", StringComparison.Ordinal)
                        : frame.Name.Contains("-loop-", StringComparison.Ordinal)
                            ? frame.PageName == $"loop-{actionName}"
                            : frame.PageName == expectedPageName ||
                              frame.PageName.StartsWith(
                                  expectedPageName + "-part-",
                                  StringComparison.Ordinal)),
                $"{actionName} 的 idle/wake 必须来自共享 idle 页，动作姿势必须来自" +
                $" {expectedPageName} 连续分页");

            var expectedResourceNames = expectedMotionNames
                .Select(frameName => $"Assets/{frameName}")
                .ToHashSet(StringComparer.Ordinal);
            var actualResourceNames = spriteFrames
                .Select(frame => frame.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert(actualResourceNames.SetEquals(expectedResourceNames),
                $"{actionName} 时间轴应完整使用共享 idle、{wakeFrameCount}帧 wake、dense动作与48帧循环资源");
            Assert(frames.Length == expectedMotionNames.Length &&
                   frames.All(frame => GetFrameDuration(frame) == motionFrameInterval),
                $"{actionName} 的{expectedMotionNames.Length}帧必须全部使用精确60fps绝对时间间隔");
            var expectedActionFrameIndex = wakeFrameCount;
            Assert(GetProperty<int>(clip, "ActionFrameIndex") == expectedActionFrameIndex &&
                   actualMotionNames[expectedActionFrameIndex] ==
                   $"luban-{actionName}-smooth-001.png",
                $"{actionName} ActionFrameIndex 必须指向动作页首个60fps姿势以供预取");
        }

        var todoEnterFrames = GetClipFrames(GetField<object>(window, "_todoEnterClip"))
            .Cast<object>()
            .ToArray();
        var todoExitFrames = GetClipFrames(GetField<object>(window, "_todoExitClip"))
            .Cast<object>()
            .ToArray();
        var expectedTodoNames = BuildExpectedActionTimelineNames("think")
            .Select(frameName => $"Assets/{frameName}")
            .ToArray();
        var actualTodoEnterNames = todoEnterFrames
            .Select(frame => GetSpriteFrameInfo(GetProperty<object>(frame, "Image")))
            .ToArray();
        Assert(actualTodoEnterNames.Length == expectedTodoNames.Length &&
               actualTodoEnterNames.Select(frame => frame.Name)
                   .SequenceEqual(expectedTodoNames) &&
               actualTodoEnterNames.Take(wakeFrameCount + 1)
                   .All(frame =>
                       string.Equals(frame.PageName, "idle", StringComparison.Ordinal) ||
                       frame.PageName.StartsWith("idle-part-", StringComparison.Ordinal)) &&
               actualTodoEnterNames.Skip(wakeFrameCount + 1)
                   .Select((frame, frameIndex) => (frame, frameIndex))
                   .All(entry => entry.frame.PageName ==
                       (entry.frameIndex < 32
                           ? "action-think"
                           : $"action-think-part-{entry.frameIndex / 32 + 1:00}")),
            $"Todo 入场必须按 idle→{wakeFrameCount}帧起身→think dense序列跨页播放");
        Assert(todoExitFrames
                .Select(frame => GetProperty<string>(frame, "Name"))
                .SequenceEqual(todoEnterFrames
                    .Select(frame => GetProperty<string>(frame, "Name"))
                    .Reverse()),
            "Todo 入场和收起必须严格互为反序，快速切换时才能映射到同一姿势");
    }

    private static string[] BuildExpectedActionTimelineNames(string actionName)
    {
        var names = new List<string>
        {
            "luban-idle.png"
        };
        names.AddRange(GetExpectedWakeFrameNames());

        var assetsDirectory = Path.GetDirectoryName(
            FindWorkspaceFile("Assets", "luban-idle.png"))!;
        var actionFrames = Directory.EnumerateFiles(
                assetsDirectory,
                $"luban-{actionName}-smooth-*.png",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert(actionFrames.Length >= 50,
            $"{actionName} must contain at least 50 dense 60fps action frames");
        names.AddRange(actionFrames);

        return names.ToArray();
    }

    private static void AssertEdgeFrameSequenceContract(MainWindow window)
    {
        var sequenceContracts = new[]
        {
            (FieldName: "_edgeLeftFrames", PageName: "edge-left", Direction: "left"),
            (FieldName: "_edgeBottomFrames", PageName: "edge-bottom", Direction: "bottom")
        };

        foreach (var contract in sequenceContracts)
        {
            var frames = GetField<Array>(window, contract.FieldName)
                .Cast<object>()
                .Select(GetSpriteFrameInfo)
                .ToArray();
            var assetsDirectory = Path.GetDirectoryName(
                FindWorkspaceFile("Assets", "luban-idle.png"))!;
            var assetNames = Directory.EnumerateFiles(
                    assetsDirectory,
                    $"luban-edge-{contract.Direction}-smooth-*.png",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert(frames.Length == assetNames.Length &&
                   frames.Length >= 8 && frames.Length % 4 == 0,
                $"{contract.PageName} 必须动态覆盖全部{assetNames.Length}帧Assets；" +
                "运行时四阶段契约要求至少8帧且为4的倍数");
            var expectedNames = Enumerable.Range(1, frames.Length)
                .Select(frameNumber =>
                    $"Assets/luban-edge-{contract.Direction}-smooth-{frameNumber:000}.png")
                .ToArray();
            Assert(assetNames.SequenceEqual(expectedNames.Select(Path.GetFileName)) &&
                   frames.Select(frame => frame.Name).SequenceEqual(expectedNames) &&
                   frames.All(frame => string.Equals(
                       frame.PageName,
                       contract.PageName,
                       StringComparison.Ordinal)),
                $"{contract.PageName} 必须从独立同名分页按" +
                $"smooth-001..{frames.Length:000}升序动态载入，不能跳号或借用idle页");

            var quarter = frames.Length / 4;
            var keyPhaseIndices = new[]
            {
                quarter - 1,
                quarter * 2 - 1,
                quarter * 3 - 1,
                quarter * 4 - 1
            };
            var keyPhaseNumbers = Enumerable.Range(1, 4)
                .Select(phase => quarter * phase)
                .ToArray();
            Assert(keyPhaseIndices
                    .Select(index => frames[index].Name)
                    .SequenceEqual(keyPhaseNumbers.Select(frameNumber =>
                        $"Assets/luban-edge-{contract.Direction}-smooth-{frameNumber:000}.png")),
                $"{contract.PageName} 四阶段关键姿势必须动态落在每个1/4末帧；" +
                $"当前为{string.Join('/', keyPhaseNumbers.Select(number => $"{number:000}"))}，" +
                "其中1/2完全探头、末帧收回休息");
        }

        var leftFrames = GetField<Array>(window, "_edgeLeftFrames");
        var rightFrames = (Array)Invoke(
            window,
            "GetEdgeFrames",
            GetNestedEnum("EdgeDock", "Right"))!;
        Assert(ReferenceEquals(leftFrames, rightFrames),
            "右侧探头必须镜像复用完整edge-left序列，不能维护另一套跳号帧");

        foreach (var supportedFrameCount in new[] { 16, 24, ExpectedEdgePeekFrameCount })
        {
            var fullyPeekedIndex = supportedFrameCount / 2 - 1;
            var restIndex = supportedFrameCount - 1;
            var normalHold = (TimeSpan)InvokeStatic(
                typeof(MainWindow),
                "GetEdgePeekFrameHoldDuration",
                0,
                supportedFrameCount)!;
            var fullyPeekedHold = (TimeSpan)InvokeStatic(
                typeof(MainWindow),
                "GetEdgePeekFrameHoldDuration",
                fullyPeekedIndex,
                supportedFrameCount)!;
            var restHold = (TimeSpan)InvokeStatic(
                typeof(MainWindow),
                "GetEdgePeekFrameHoldDuration",
                restIndex,
                supportedFrameCount)!;
            Assert(normalHold == TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60) &&
                   fullyPeekedHold == TimeSpan.FromMilliseconds(650) &&
                   restHold == TimeSpan.FromMilliseconds(800),
                $"{supportedFrameCount}帧边缘序列必须动态计算1/2完全探头和末帧休息hold，" +
                "其余帧保持60fps");
        }
    }

    private static string[] GetExpectedWakeFrameNames()
    {
        var assetsDirectory = Path.GetDirectoryName(
            FindWorkspaceFile("Assets", "luban-idle.png"))!;
        var actualNames = Directory.EnumerateFiles(
                assetsDirectory,
                "luban-wake-smooth-*.png",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert(actualNames.Length > 0,
            "Assets必须包含至少一帧luban-wake-smooth编号序列");
        var expectedNames = Enumerable.Range(1, actualNames.Length)
            .Select(frameNumber => $"luban-wake-smooth-{frameNumber:000}.png")
            .ToArray();
        Assert(actualNames.SequenceEqual(expectedNames),
            "wake smooth资源必须从001开始连续编号，不能缺帧、重号或依赖固定总数");
        return actualNames;
    }

    private static string[] BuildExpectedMotionFrameNames(string actionName)
    {
        var timeline = BuildExpectedActionTimelineNames(actionName);
        var names = new List<string>();
        names.AddRange(timeline.Skip(1));
        var loopNames = Enumerable.Range(1, 48)
            .Select(frameNumber =>
                $"luban-{actionName}-loop-{frameNumber:000}.png")
            .ToArray();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            names.AddRange(loopNames);
        }

        var finalLoopPoseName = timeline[^1];
        var returnStartIndex = timeline.Length - 2;
        Assert(returnStartIndex >= 0,
            $"{actionName} 预期时间线必须包含微循环末姿势 {finalLoopPoseName}");
        names.AddRange(timeline.Take(returnStartIndex + 1).Reverse());
        return names.ToArray();
    }

    private static TimeSpan GetFrameDuration(object frame) =>
        GetProperty<TimeSpan>(frame, "HoldDuration");

    private static Array GetClipFrames(object clip) => GetProperty<Array>(clip, "Frames");

    private static void AssertNoRunContract(MainWindow window)
    {
        var clips = GetField<Array>(window, "_reactionClips")
            .Cast<object>()
            .ToArray();
        Assert(clips.Length == 7 && clips.All(clip =>
                !string.Equals(GetProperty<string>(clip, "ActionName"), "run", StringComparison.OrdinalIgnoreCase)),
            "运行时点击动作中不得再出现 run");

        var activities = GetField<Array>(window, "_automaticActivities")
            .Cast<object?>()
            .ToArray();
        Assert(activities.Length == 8 && activities.Count(activity => activity is null) == 1,
            "自动活动袋必须为 7 个角色动作加 1 个待机项");
        Assert(activities.Where(activity => activity is not null)
                .All(activity => clips.Any(clip => ReferenceEquals(clip, activity))),
            "自动活动袋的非空项必须全部引用保留的 7 个点击动作");

        var workspace = Path.GetDirectoryName(FindWorkspaceFile("DesktopPet.csproj"))!;
        var mainWindowSource = File.ReadAllText(Path.Combine(workspace, "MainWindow.xaml.cs"));
        var forbiddenSourceFragments = new[]
        {
            "\"run\"", "CreateRun", "RunLoop", "luban-run", "action-run"
        };
        Assert(forbiddenSourceFragments.All(fragment =>
                !mainWindowSource.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
            "MainWindow 运行时代码不得保留跑步动作、跑步时序或跑步资源引用");
        var toolsDirectory = Path.Combine(workspace, "tools");
        var toolSources = Directory.EnumerateFiles(toolsDirectory, "*.py", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        var forbiddenToolFragments = new[]
        {
            "'run'", "\"run\"", "--v5-run", "run-loop", "run-bridge", "luban-run"
        };
        Assert(toolSources.All(source => forbiddenToolFragments.All(fragment =>
                !source.Contains(fragment, StringComparison.OrdinalIgnoreCase))),
            "图集及安装脚本不得再生成或登记 run");

        var assetsDirectory = Path.Combine(workspace, "Assets");
        var runAssetPaths = Directory.EnumerateFiles(assetsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("run", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert(runAssetPaths.Length == 0,
            $"Assets 中不得残留跑步派生资产：{string.Join(", ", runAssetPaths.Select(Path.GetFileName))}");
        var manifestText = File.ReadAllText(Path.Combine(assetsDirectory, "luban-sprite-pages.json"));
        Assert(!manifestText.Contains("action-run", StringComparison.OrdinalIgnoreCase) &&
               !manifestText.Contains("luban-run", StringComparison.OrdinalIgnoreCase),
            "分页图集清单不得登记 run 分页及帧");

        Assert(!typeof(MainWindow).Assembly.GetManifestResourceNames()
                .Any(name => name.Contains("action-run", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("luban-run", StringComparison.OrdinalIgnoreCase)),
            "主程序集不得嵌入 run 资源");
    }

    private static void AssertEdgeRoamingSourceContract()
    {
        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var todoSource = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml.cs"));
        var todoXaml = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml"));
        var readmeSource = File.ReadAllText(FindWorkspaceFile("README.md"));
        var settingsSource = File.ReadAllText(FindWorkspaceFile("AppSettingsStore.cs"));
        var atlasBuilderSource = File.ReadAllText(
            FindWorkspaceFile("tools", "build_sprite_atlas.py"));
        var roamAssetBuilderSource = File.ReadAllText(
            FindWorkspaceFile("tools", "build_roam_flight_assets.py"));
        var atlasMotionQaSource = File.ReadAllText(
            FindWorkspaceFile("tools", "qa_sprite_atlas_motion.py"));

        var startRoaming = ExtractPrivateMethodSource(mainSource, "StartEdgeRoaming");
        var stopRoaming = ExtractPrivateMethodSource(mainSource, "StopEdgeRoaming");
        var startRoamBoarding = ExtractPrivateMethodSource(
            mainSource,
            "StartEdgeRoamBoarding");
        var advanceRoamBoarding = ExtractPrivateMethodSource(
            mainSource,
            "AdvanceEdgeRoamBoarding");
        var advanceRoaming = ExtractPrivateMethodSource(mainSource, "AdvanceEdgeRoaming");
        var advanceRoamTravel = ExtractPrivateMethodSource(
            mainSource,
            "AdvanceEdgeRoamTravel");
        var startRoamTravel = ExtractPrivateMethodSource(
            mainSource,
            "StartEdgeRoamTravel");
        var resolveRoamFacing = ExtractPrivateMethodSource(
            mainSource,
            "ResolveEdgeRoamFacingScaleX");
        var advanceRoamClock = ExtractPrivateMethodSource(
            mainSource,
            "AdvanceEdgeRoamClock");
        var getRoamingPose = ExtractPrivateMethodSource(mainSource, "GetEdgeRoamPose");
        var rendering = ExtractPrivateMethodSource(mainSource, "VisualClock_Rendering");
        var updateClock = ExtractPrivateMethodSource(
            mainSource,
            "UpdateVisualClockSubscription");
        var automaticTick = ExtractPrivateMethodSource(mainSource, "AutomaticTimer_Tick");
        var pointerDown = ExtractPrivateMethodSource(
            mainSource,
            "PetHost_MouseLeftButtonDown");
        var pointerUp = ExtractPrivateMethodSource(
            mainSource,
            "PetHost_MouseLeftButtonUp");
        var completeRoamStop = ExtractPrivateMethodSource(
            mainSource,
            "CompleteEdgeRoamStop");
        var resetPetVisualTransforms = ExtractPrivateMethodSource(
            mainSource,
            "ResetPetVisualTransforms");
        var restartAutomaticCountdown = ExtractPrivateMethodSource(
            mainSource,
            "RestartAutomaticCountdown");
        var scheduleNextEdgeRoam = ExtractPrivateMethodSource(
            mainSource,
            "ScheduleNextEdgeRoam");
        var armAutomaticWakeTimer = ExtractPrivateMethodSource(
            mainSource,
            "ArmAutomaticWakeTimer");
        var enterEdgePeek = ExtractPrivateMethodSource(mainSource, "EnterEdgePeek");
        var setBubbleMode = ExtractPrivateMethodSource(mainSource, "SetBubbleMode");
        var enterTodo = ExtractPrivateMethodSource(mainSource, "EnterTodoVisualState");
        var beginReminder = ExtractPrivateMethodSource(
            mainSource,
            "BeginReminderPetSizeOverrideAt");
        var displaySettingsChanged = ExtractPrivateMethodSource(
            mainSource,
            "SystemEvents_DisplaySettingsChanged");
        var processSystemRecovery = ExtractPrivateMethodSource(
            mainSource,
            "ProcessSystemRecovery");
        var canCollect = ExtractPrivateMethodSource(
            mainSource,
            "CanRunIdleSpritePageCollection");
        var isPageProtected = ExtractPrivateMethodSource(
            mainSource,
            "IsSpritePageProtected");
        var failedPage = ExtractPrivateMethodSource(
            mainSource,
            "StopAnimatedStateForFailedSpritePage");
        var saveSettings = ExtractPrivateMethodSource(mainSource, "SaveSettings");
        var roamingSettingChanged = ExtractPrivateMethodSource(
            mainSource,
            "TodoWindow_EdgeRoamingEnabledChanged");
        var setRoamingToggle = todoSource.IndexOf(
            "public void SetEdgeRoamingEnabled(bool enabled)",
            StringComparison.Ordinal);
        var toggleChanged = ExtractPrivateMethodSource(
            todoSource,
            "EdgeRoamingToggle_Changed");

        Assert(settingsSource.Contains(
                   "public bool EdgeRoamingEnabled",
                   StringComparison.Ordinal) &&
               settingsSource.Contains("= true;", StringComparison.Ordinal) &&
               settingsSource.Contains(
                   "PropertyNamingPolicy = JsonNamingPolicy.CamelCase",
                   StringComparison.Ordinal) &&
               mainSource.Contains("_edgeRoamingEnabled", StringComparison.Ordinal) &&
               mainSource.Contains("_isEdgeRoaming", StringComparison.Ordinal) &&
               mainSource.Contains(
                   "_todoWindow.EdgeRoamingEnabledChanged +=",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "TodoWindow_EdgeRoamingEnabledChanged;",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "_todoWindow.SetEdgeRoamingEnabled(_edgeRoamingEnabled)",
                   StringComparison.Ordinal) &&
               saveSettings.Contains(
                   "EdgeRoamingEnabled = _edgeRoamingEnabled",
                   StringComparison.Ordinal),
            "绕屏开关必须默认开启、使用edgeRoamingEnabled驼峰JSON持久化，" +
            "并在MainWindow与TodoWindow之间双向同步；保存尺寸时不得覆盖绕屏选择");
        var legacyChineseMountName = string.Concat("木", "鸢");
        var legacyEnglishMountName = string.Concat("wood", "en", "-bird");
        var legacyEnglishMountPhrase = string.Concat("wood", "en", " bird");
        Assert(readmeSource.Contains("大头熊猫", StringComparison.Ordinal) &&
               readmeSource.Contains("铃铛", StringComparison.Ordinal) &&
               readmeSource.Contains("竹筒", StringComparison.Ordinal) &&
               mainSource.Contains("熊猫坐骑", StringComparison.Ordinal) &&
               !readmeSource.Contains(
                   legacyChineseMountName,
                   StringComparison.Ordinal) &&
               !mainSource.Contains(
                   legacyChineseMountName,
                   StringComparison.Ordinal) &&
               !roamAssetBuilderSource.Contains(
                   legacyEnglishMountName,
                   StringComparison.OrdinalIgnoreCase) &&
               !atlasMotionQaSource.Contains(
                   legacyEnglishMountPhrase,
                   StringComparison.OrdinalIgnoreCase),
            "绕屏视觉必须统一为带铃铛和竹筒的大头熊猫坐骑，README、运行日志、" +
            "生成器与QA不得残留旧版飞行坐骑描述");
        Assert(setRoamingToggle >= 0 &&
               todoSource[setRoamingToggle..].Contains(
                   "_settingEdgeRoamingEnabled = true",
                   StringComparison.Ordinal) &&
               todoSource[setRoamingToggle..].Contains(
                   "EdgeRoamingToggle.IsChecked = enabled",
                   StringComparison.Ordinal) &&
               toggleChanged.Contains(
                   "if (!_settingEdgeRoamingEnabled)",
                   StringComparison.Ordinal) &&
               toggleChanged.Contains(
                   "EdgeRoamingEnabledChanged?.Invoke",
                   StringComparison.Ordinal) &&
               todoXaml.Contains(
                   "x:Name=\"EdgeRoamingToggle\"",
                   StringComparison.Ordinal) &&
               todoXaml.Contains("IsChecked=\"True\"", StringComparison.Ordinal) &&
               todoXaml.Contains(
                   "AutomationProperties.Name=\"绕屏动画\"",
                   StringComparison.Ordinal),
            "绕屏勾选必须位于TodoWindow，默认勾选且有无障碍名称；" +
            "程序加载设置时必须由保护位静默更新，不能反向触发重复保存");

        Assert(mainSource.Contains(
                   "\"Assets/luban-roam-boarding-\"",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "\"Assets/luban-roam-flight-\"",
                   StringComparison.Ordinal) &&
               mainSource.Contains("\"roam-boarding\"", StringComparison.Ordinal) &&
               mainSource.Contains("\"roam-flight\"", StringComparison.Ordinal) &&
               mainSource.Contains(
                   "_roamBoardingFrames.Length < 48",
                   StringComparison.Ordinal) &&
               (!mainSource.Contains("\"roam-wave\"", StringComparison.Ordinal) ||
                mainSource.Contains(
                    "LoadOptionalNumberedFrameSequence",
                    StringComparison.Ordinal) ||
                mainSource.Contains(
                    "TryLoadNumberedFrameSequence",
                    StringComparison.Ordinal)) &&
               !mainSource.Contains("RoamFlightFrameCount", StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "EdgeRoamWaveInterval",
                   StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "EdgeRoamWaveDelay",
                   StringComparison.Ordinal) &&
               atlasBuilderSource.Contains(
                   "luban-roam-{sequence}",
                   StringComparison.Ordinal) &&
               atlasBuilderSource.Contains(
                   "\"boarding\"",
                   StringComparison.Ordinal) &&
               atlasBuilderSource.Contains("\"flight\"", StringComparison.Ordinal) &&
               atlasBuilderSource.Contains(
                   "f\"roam-{sequence}\"",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "luban-roam-boarding",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "luban-roam-flight",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "panda",
                   StringComparison.OrdinalIgnoreCase) &&
               roamAssetBuilderSource.Contains(
                   "bamboo",
                   StringComparison.OrdinalIgnoreCase) &&
               roamAssetBuilderSource.Contains(
                   "roam-panda-v3-balanced-luban-eyes-primary-16-alpha.png",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "roam-panda-v3-balanced-luban-eyes-boarding-16-alpha.png",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "roam-panda-v3-balanced-luban-eyes-secondary-16-alpha.png",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "assert_luban_eye_symmetry",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "minimum_width_ratio=0.79",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "skipped_frames=set(range(22, 29))",
                   StringComparison.Ordinal) &&
               roamAssetBuilderSource.Contains(
                   "len(pixel_hashes) != len(paths)",
                   StringComparison.Ordinal) &&
               atlasMotionQaSource.Contains(
                   "\"boarding\"",
                   StringComparison.Ordinal) &&
               atlasMotionQaSource.Contains("\"flight\"", StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "f\"roam.{sequence}\"",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "ROAM_LOOP_SEQUENCES = (\"flight\", \"wave\")",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "ROAM_NON_LOOP_SEQUENCES = (\"boarding\",)",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "ROAM_BOARDING_SEQUENCE = \"roam.boarding\"",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains("MIN_ALPHA_IOU = 0.92", StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "MIN_MEAN_ALPHA_IOU = 0.95",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "MAX_HAT_SCALE_STEP = 0.025",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "MAX_TORSO_SCALE_STEP = 0.035",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "MAX_BOARDING_CENTROID_STEP_DIP = 10.0",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "MAX_BOARDING_HEAD_CENTER_STEP_DIP = 12.0",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "MAX_BOARDING_WIDE_TRANSLUCENT_TRAIL_RATIO = 0.010",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "is_boarding_transition = sequence_name == ROAM_BOARDING_SEQUENCE",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "if not is_boarding_transition and iou < MIN_ALPHA_IOU:",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "if not is_boarding_transition and head_scale > head_scale_limit:",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "if not is_boarding_transition and torso_width_scale > torso_width_limit:",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "if not is_boarding_transition and torso_height_scale > torso_height_limit:",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "if not is_boarding_transition and mean_iou < MIN_MEAN_ALPHA_IOU:",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "MAX_BOARDING_CENTROID_STEP_DIP + quantisation_dip",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "if is_boarding_transition:",
                    StringComparison.Ordinal) &&
                atlasMotionQaSource.Contains(
                    "violations.append(finding)",
                    StringComparison.Ordinal),
            "熊猫坐骑必须从boarding-001正放登乘、停止时倒放，并使用flight-001连续主循环；" +
            "wave只能是可选补充，禁止固定7秒硬切。运行时不能硬编码总帧数或混用跑步、" +
            "爬行素材；最终图集QA必须让flight保持稳态Alpha IoU及帽子/躯干身形硬门槛，" +
            "boarding则使用非循环姿态转换专用的质心、头部单步位移和半透明拖影硬门槛，" +
            "不得假装复用flight门槛形成测试假覆盖");
        var forbiddenLegacyRoamingNames = new[]
        {
            "roam-crawl",
            "roam-wriggle",
            "roam-hop",
            "EdgeDock.Top",
            "\"edge-top\""
        };
        Assert(forbiddenLegacyRoamingNames.All(name =>
                !mainSource.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                !atlasBuilderSource.Contains(name, StringComparison.OrdinalIgnoreCase)),
            "运行时和最终图集不得恢复旧爬行、蠕动、跳跃绕屏素材，也不得恢复手动顶部吸附");
        Assert(startRoaming.Contains(
                   "!_edgeRoamingEnabled",
                   StringComparison.Ordinal) &&
               startRoaming.Contains("_isReminderActive", StringComparison.Ordinal) &&
               startRoaming.Contains("_dragInteractionActive", StringComparison.Ordinal) &&
               startRoaming.Contains("_pointerDown", StringComparison.Ordinal) &&
               startRoaming.Contains("_bubbleMode", StringComparison.Ordinal) &&
               startRoaming.Contains("_edgeDock", StringComparison.Ordinal) &&
               startRoaming.Contains(
                   "MonitorWorkArea.GetForWindow(this)",
                   StringComparison.Ordinal) &&
               startRoaming.Contains("_isEdgeRoaming = true", StringComparison.Ordinal) &&
               startRoaming.Contains(
                   "StartEdgeRoamBoarding(",
                   StringComparison.Ordinal) &&
               startRoaming.Contains("reverse: false", StringComparison.Ordinal) &&
               stopRoaming.Contains(
                   "StartEdgeRoamBoarding(",
                   StringComparison.Ordinal) &&
               stopRoaming.Contains("reverse: true", StringComparison.Ordinal) &&
                startRoamBoarding.Contains(
                    "_roamBoardingFrames",
                    StringComparison.Ordinal) &&
                startRoamBoarding.Contains("reverse", StringComparison.Ordinal) &&
                startRoamBoarding.Contains(
                    "var boardingStartIndex = 0",
                    StringComparison.Ordinal) &&
                startRoamBoarding.Contains(
                    ": _roamBoardingFrames.Length - 1",
                    StringComparison.Ordinal) &&
                startRoamBoarding.Contains(
                    "_roamBoardingFrames[_edgeRoamBoardingStartIndex]",
                    StringComparison.Ordinal) &&
                advanceRoamBoarding.Contains(
                    "? _edgeRoamBoardingStartIndex - frameStep",
                    StringComparison.Ordinal) &&
                advanceRoamBoarding.Contains(
                    ": frameStep",
                    StringComparison.Ordinal) &&
               !stopRoaming.Contains(
                   "ShowStableFrame(_idleFrame)",
                   StringComparison.Ordinal) &&
               mainSource.Contains("_isEdgeRoaming = false", StringComparison.Ordinal) &&
               automaticTick.Contains("StartEdgeRoaming(", StringComparison.Ordinal),
            "自动计时器只能把熊猫坐骑巡游作为独立活动启动；开始必须正放boarding，" +
            "停止必须倒放同一序列并等退场完成后才恢复idle。提醒、拖拽、指针按下、" +
            "待办和手动探头活跃时必须拒绝启动，且路线锁定人物当前显示器WorkArea");

        var hasLogicalPosition =
            mainSource.Contains("_edgeRoamingLogicalPosition", StringComparison.Ordinal) ||
            (mainSource.Contains("_edgeRoamLogicalLeft", StringComparison.Ordinal) &&
             mainSource.Contains("_edgeRoamLogicalTop", StringComparison.Ordinal)) ||
            (mainSource.Contains("_edgeRoamingLogicalLeft", StringComparison.Ordinal) &&
             mainSource.Contains("_edgeRoamingLogicalTop", StringComparison.Ordinal));
        Assert(hasLogicalPosition &&
               rendering.Contains("AdvanceEdgeRoaming(timestamp)", StringComparison.Ordinal) &&
               updateClock.Contains("_isEdgeRoaming", StringComparison.Ordinal) &&
               advanceRoaming.Contains(
                   "AdvanceEdgeRoamTravel(timestamp)",
                   StringComparison.Ordinal) &&
               startRoamTravel.Contains(
                   "UpdateEdgeRoamFacing(initialPosition, initialLookAhead)",
                   StringComparison.Ordinal) &&
               startRoamTravel.IndexOf(
                   "UpdateEdgeRoamFacing(initialPosition, initialLookAhead)",
                   StringComparison.Ordinal) <
               startRoamTravel.IndexOf(
                   "ShowStableFrame(_roamFlightFrames[0])",
                   StringComparison.Ordinal) &&
               resolveRoamFacing.Contains(
                   "return deltaX > 0 ? -1 : 1",
                   StringComparison.Ordinal) &&
               advanceRoamTravel.Contains(
                   "AdvanceEdgeRoamClock(timestamp)",
                   StringComparison.Ordinal) &&
               advanceRoamTravel.Contains("ShowStableFrame", StringComparison.Ordinal) &&
               advanceRoamClock.Contains("Stopwatch", StringComparison.Ordinal) &&
               advanceRoamClock.Contains(
                   "EdgeRoamMaximumClockGap",
                   StringComparison.Ordinal) &&
               advanceRoamClock.Contains(
                   "_edgeRoamStartedTimestamp = checked(",
                   StringComparison.Ordinal) &&
               !advanceRoaming.Contains("while (", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains("while (", StringComparison.Ordinal) &&
               !advanceRoamClock.Contains("while (", StringComparison.Ordinal) &&
               getRoamingPose.Contains("_roamFlightFrames.Length", StringComparison.Ordinal) &&
               !getRoamingPose.Contains("waveElapsed", StringComparison.Ordinal) &&
               !getRoamingPose.Contains("waveCycle", StringComparison.Ordinal) &&
               (advanceRoamTravel.Contains(
                    "SnapDipToPhysicalPixel",
                    StringComparison.Ordinal) ||
                advanceRoamTravel.Contains(
                    "ApplyEdgeRoamingPosition",
                    StringComparison.Ordinal)) &&
               !advanceRoaming.Contains("Dispatcher", StringComparison.Ordinal) &&
               !advanceRoaming.Contains("AppLogger", StringComparison.Ordinal) &&
               !advanceRoaming.Contains("LogInfo", StringComparison.Ordinal) &&
               !advanceRoaming.Contains("File.", StringComparison.Ordinal) &&
               !advanceRoaming.Contains("Task.Run", StringComparison.Ordinal) &&
               !advanceRoaming.Contains(".Select(", StringComparison.Ordinal) &&
               !advanceRoaming.Contains(".ToArray(", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains("Dispatcher", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains("AppLogger", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains("LogInfo", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains("File.", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains("Task.Run", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains(".Select(", StringComparison.Ordinal) &&
               !advanceRoamTravel.Contains(".ToArray(", StringComparison.Ordinal) &&
               !advanceRoamClock.Contains("Dispatcher", StringComparison.Ordinal) &&
               !advanceRoamClock.Contains("AppLogger", StringComparison.Ordinal) &&
               !advanceRoamClock.Contains("File.", StringComparison.Ordinal) &&
               !advanceRoamClock.Contains("Task.Run", StringComparison.Ordinal) &&
               !advanceRoamClock.Contains(".Select(", StringComparison.Ordinal) &&
               !advanceRoamClock.Contains(".ToArray(", StringComparison.Ordinal) &&
               !getRoamingPose.Contains("Dispatcher", StringComparison.Ordinal) &&
               !getRoamingPose.Contains("AppLogger", StringComparison.Ordinal) &&
               !getRoamingPose.Contains(".Select(", StringComparison.Ordinal) &&
               !getRoamingPose.Contains(".ToArray(", StringComparison.Ordinal) &&
               !advanceRoamBoarding.Contains("Dispatcher", StringComparison.Ordinal) &&
               !advanceRoamBoarding.Contains("AppLogger", StringComparison.Ordinal) &&
               !advanceRoamBoarding.Contains("File.", StringComparison.Ordinal) &&
               !advanceRoamBoarding.Contains("Task.Run", StringComparison.Ordinal) &&
               !advanceRoamBoarding.Contains(".Select(", StringComparison.Ordinal) &&
               !advanceRoamBoarding.Contains(".ToArray(", StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "DispatcherTimer _edgeRoaming",
                   StringComparison.Ordinal),
            "熊猫坐骑位置与姿势必须由唯一Rendering绝对时钟推进；逻辑坐标保持double精度，" +
            "最终Left/Top才对齐物理像素，热路径不得使用定时器、日志、I/O、Task或LINQ分配");

        var stopBeforeDrag = pointerDown.IndexOf(
            "StopEdgeRoaming(",
            StringComparison.Ordinal);
        var dragBecomesActive = pointerDown.IndexOf(
            "_dragInteractionActive = true",
            StringComparison.Ordinal);
        Assert(stopBeforeDrag >= 0 &&
               dragBecomesActive > stopBeforeDrag &&
               pointerDown.Contains(
                   "_suppressClickReactionAfterRoamInterruption = _isEdgeRoaming",
                   StringComparison.Ordinal) &&
               pointerDown.Contains(
                   "immediate: _suppressClickReactionAfterRoamInterruption",
                   StringComparison.Ordinal) &&
               pointerUp.Contains(
                   "!_suppressClickReactionAfterRoamInterruption",
                   StringComparison.Ordinal) &&
               pointerUp.IndexOf(
                   "_suppressClickReactionAfterRoamInterruption = false",
                   StringComparison.Ordinal) <
               pointerUp.IndexOf("ShowCuteReaction()", StringComparison.Ordinal) &&
               stopRoaming.Contains("if (immediate)", StringComparison.Ordinal) &&
               stopRoaming.Contains(
                   "CompleteEdgeRoamStop(",
                   StringComparison.Ordinal) &&
               completeRoamStop.Contains(
                   "ShowStableFrame(_idleFrame)",
                   StringComparison.Ordinal) &&
               resetPetVisualTransforms.Contains(
                   "PetRoamRotate.Angle = 0",
                   StringComparison.Ordinal) &&
               enterEdgePeek.Contains("StopEdgeRoaming(", StringComparison.Ordinal) &&
               (setBubbleMode.Contains("StopEdgeRoaming(", StringComparison.Ordinal) ||
                enterTodo.Contains("StopEdgeRoaming(", StringComparison.Ordinal)) &&
               (setBubbleMode.Contains("StopEdgeRoaming(", StringComparison.Ordinal) ||
                beginReminder.Contains("StopEdgeRoaming(", StringComparison.Ordinal)) &&
               displaySettingsChanged.Contains(
                   "QueueSystemRecovery()",
                   StringComparison.Ordinal) &&
               processSystemRecovery.Contains(
                   "StopEdgeRoaming(",
                   StringComparison.Ordinal) &&
               processSystemRecovery.Contains(
                   "immediate: true",
                   StringComparison.Ordinal) &&
               roamingSettingChanged.Contains(
                   "_edgeRoamingEnabled = enabled",
                   StringComparison.Ordinal) &&
               roamingSettingChanged.Contains("StopEdgeRoaming(", StringComparison.Ordinal),
            "拖拽必须先抢占巡游再捕获鼠标；手动探头、待办、提醒、取消勾选和" +
            "显示器变化也必须停止旧路线，永不让巡游与EdgeDock同时运行");
        var automaticInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "AutomaticAnimationInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var roamInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgeRoamInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        Assert(automaticInterval == TimeSpan.FromMinutes(1) &&
               roamInterval == TimeSpan.FromMinutes(10) &&
               restartAutomaticCountdown.Contains(
                   "_nextAutomaticActivityDueTimestamp = checked(",
                   StringComparison.Ordinal) &&
               !restartAutomaticCountdown.Contains(
                   "_nextEdgeRoamDueTimestamp = checked(",
                   StringComparison.Ordinal) &&
               scheduleNextEdgeRoam.Contains(
                   "_nextEdgeRoamDueTimestamp = checked(",
                   StringComparison.Ordinal) &&
               !scheduleNextEdgeRoam.Contains(
                   "_nextAutomaticActivityDueTimestamp",
                   StringComparison.Ordinal) &&
               armAutomaticWakeTimer.Contains(
                   "_nextAutomaticActivityDueTimestamp",
                   StringComparison.Ordinal) &&
               armAutomaticWakeTimer.Contains(
                   "_nextEdgeRoamDueTimestamp",
                   StringComparison.Ordinal) &&
               armAutomaticWakeTimer.Contains(
                   "Math.Min(",
                   StringComparison.Ordinal),
            "普通可爱动作必须独立按 1 分钟截止，绕屏必须独立按 10 分钟截止；共享唤醒 timer 只能选择最早绝对截止时间");
        Assert(!mainSource.Contains("EdgeDock.Top", StringComparison.Ordinal) &&
               !mainSource.Contains("\"edge-top\"", StringComparison.Ordinal) &&
               mainSource.Contains("CornerRadius", StringComparison.Ordinal),
            "自动巡游可以经过独立圆角路线的顶部段，但不得恢复手动EdgeDock.Top或edge-top分页");

        Assert(canCollect.Contains("!_isEdgeRoaming", StringComparison.Ordinal) &&
               isPageProtected.Contains("_isEdgeRoaming", StringComparison.Ordinal) &&
               isPageProtected.Contains("_roamBoardingFrames", StringComparison.Ordinal) &&
               isPageProtected.Contains("_roamFlightFrames", StringComparison.Ordinal) &&
               failedPage.Contains("StopEdgeRoaming(", StringComparison.Ordinal),
            "巡游活动时不得触发空闲Gen2；缓存只在活动期保护boarding/flight所需分页，" +
            "冷页失败必须安全停止巡游而不能永久忙等或闪回旧帧");
    }

    private static void AssertAbsoluteTimelineMathContract(MainWindow window)
    {
        var source = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var playbackSpeedField = typeof(MainWindow).GetField(
            "AnimationPlaybackSpeed",
            StaticFlags) ?? throw new InvalidOperationException(
            "找不到代码级动画速度常量 AnimationPlaybackSpeed");
        var playbackSpeed = Convert.ToDouble(
            playbackSpeedField.GetRawConstantValue(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert(playbackSpeedField.IsLiteral &&
               !playbackSpeedField.IsInitOnly &&
               playbackSpeed == 1.25d,
            $"动画速度必须是无需设置迁移的代码常量1.25，实际 {playbackSpeed:F3}");
        Assert(typeof(AppSettings).GetProperty(
                   "AnimationPlaybackSpeed",
                   InstanceFlags) is null,
            "动画速度不得写入AppSettings或设置JSON，修改代码常量后重新编译即可");
        var todoXaml = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml"));
        Assert(!todoXaml.Contains("AnimationPlaybackSpeed", StringComparison.Ordinal) &&
               !todoXaml.Contains("AnimationSpeedSlider", StringComparison.Ordinal),
            "待办窗口不得增加动画速度滑块或持久化入口");
        var authoredSixtyFpsInterval = TimeSpan.FromTicks(
            TimeSpan.TicksPerSecond / 60);
        var effectiveMotionFrameTicks =
            ToProductionCharacterAnimationTicks(authoredSixtyFpsInterval);
        var effectiveEdgeFullyPeekedTicks =
            ToProductionStopwatchTicks(TimeSpan.FromMilliseconds(650));
        var effectiveEdgeRestTicks =
            ToProductionStopwatchTicks(TimeSpan.FromMilliseconds(800));
        AssertClose(
            StopwatchTicksToMilliseconds(effectiveMotionFrameTicks),
            1000d / 60d / playbackSpeed,
            "1.25倍代码速度必须把基础60fps运行hold缩放到约13.333ms");
        AssertClose(
            StopwatchTicksToMilliseconds(effectiveEdgeFullyPeekedTicks),
            650d,
            "边缘开心探头必须独立保持650ms，不受全局1.25倍点击动作速度影响");
        AssertClose(
            StopwatchTicksToMilliseconds(effectiveEdgeRestTicks),
            800d,
            "边缘害羞缩回休息必须独立保持800ms，不受全局1.25倍点击动作速度影响");
        Assert(source.Contains("CompositionTarget.Rendering", StringComparison.Ordinal) &&
               source.Contains("Stopwatch.GetTimestamp", StringComparison.Ordinal),
            "动作、探头与淡化必须由 CompositionTarget.Rendering 和绝对 Stopwatch 时钟驱动");
        Assert(!source.Contains("_frameTimer", StringComparison.Ordinal) &&
               !source.Contains("_edgePeekTimer", StringComparison.Ordinal),
            "视觉状态机不得再由 DispatcherTimer 逐帧 stop/start 驱动");
        var renderingStart = source.IndexOf(
            "private void VisualClock_Rendering",
            StringComparison.Ordinal);
        var renderingEnd = renderingStart < 0
            ? -1
            : source.IndexOf("\n    private ", renderingStart + 1, StringComparison.Ordinal);
        Assert(renderingStart >= 0 && renderingEnd > renderingStart &&
               !source[renderingStart..renderingEnd]
                   .Contains("AppLogger", StringComparison.Ordinal),
            "统一渲染回调内不得写日志或触发任何磁盘I/O");
        AssertMainRenderingTimeDedupContract(window);
        AssertRenderingCadenceClassificationContract(source);
        AssertEdgePeekRenderingCadenceTimelineContract(window);

        var clips = GetField<Array>(window, "_reactionClips")
            .Cast<object>()
            .ToArray();
        foreach (var clip in clips)
        {
            var actionName = GetProperty<string>(clip, "ActionName");
            var durations = GetClipFrames(clip)
                .Cast<object>()
                .Select(GetFrameDuration)
                .ToArray();
            var holdTicks = durations
                .Select(ToProductionCharacterAnimationTicks)
                .ToArray();
            var authoredTotal = TimeSpan.FromTicks(
                durations.Sum(duration => duration.Ticks));
            var effectiveTotalTicks = holdTicks.Aggregate(
                0L,
                checked((total, ticks) => total + ticks));
            var expectedFrameCount = BuildExpectedMotionFrameNames(actionName).Length;
            var expectedAuthoredDuration = TimeSpan.FromTicks(
                expectedFrameCount * (TimeSpan.TicksPerSecond / 60));
            Assert(authoredTotal == expectedAuthoredDuration &&
                   durations.All(duration => duration == authoredSixtyFpsInterval),
                $"{actionName} 的{expectedFrameCount}帧必须保留精确60fps基础素材元数据，" +
                $"实际 {authoredTotal.TotalSeconds:F3} 秒");
            Assert(effectiveTotalTicks ==
                   expectedFrameCount * effectiveMotionFrameTicks,
                $"{actionName} 的运行时间轴必须逐帧使用生产ToCharacterAnimationTicks缩放");

            var deadlineToleranceTicks = (long)(typeof(MainWindow).GetField(
                    "VisualFrameDeadlineToleranceTicks",
                    StaticFlags)!.GetValue(null) ?? 0L);
            var absoluteCheckpoints = new[]
            {
                0L,
                effectiveMotionFrameTicks - deadlineToleranceTicks - 1,
                effectiveMotionFrameTicks - deadlineToleranceTicks,
                effectiveMotionFrameTicks * 2 - deadlineToleranceTicks,
                StopwatchTicksFromMilliseconds(250),
                StopwatchTicksFromMilliseconds(500)
            };

            for (var checkpointIndex = 0;
                 checkpointIndex < absoluteCheckpoints.Length;
                 checkpointIndex++)
            {
                var elapsedTicks = absoluteCheckpoints[checkpointIndex];
                var expectedIndex = ResolveAbsoluteFrameIndexAtStopwatchTicks(
                    holdTicks,
                    elapsedTicks,
                    deadlineToleranceTicks);
                var actualIndex = ResolveProductionClipFrameIndex(
                    window,
                    clip,
                    elapsedTicks);
                Assert(actualIndex == expectedIndex,
                    $"生产AdvanceActiveClip必须在同一绝对时刻定位同一动作帧；" +
                    $"action={actionName}, " +
                    $"elapsed={StopwatchTicksToMilliseconds(elapsedTicks):F3}ms，" +
                    $"expected={expectedIndex}, actual={actualIndex}");
            }

            var beforeStall = StopwatchTicksFromMilliseconds(140);
            var afterStall = checked(
                beforeStall + StopwatchTicksFromMilliseconds(250));
            var beforeIndex = ResolveProductionClipFrameIndex(window, clip, beforeStall);
            var afterIndex = ResolveProductionClipFrameIndex(window, clip, afterStall);
            var expectedAfterIndex = ResolveAbsoluteFrameIndexAtStopwatchTicks(
                holdTicks,
                afterStall,
                deadlineToleranceTicks);
            Assert(afterIndex > beforeIndex && afterIndex == expectedAfterIndex,
                "生产AdvanceActiveClip在250ms渲染停顿后必须直接定位正确帧，" +
                    "不得只补播下一帧或累积计时漂移");
        }

        AssertProductionDiscreteVsyncTimeline(
            window,
            clips[0],
            "reaction-yawn");

        AssertTodoTransitionTimelineContract(window, source);
    }

    private static void AssertTodoTransitionTimelineContract(
        MainWindow window,
        string mainWindowSource)
    {
        var expectedEnterNames = BuildExpectedActionTimelineNames("think");
        var expectedFrameCount = expectedEnterNames.Length;
        var wakeFrameCount = GetExpectedWakeFrameNames().Length;
        var enterClip = GetRawField(window, "_todoEnterClip")!;
        var exitClip = GetRawField(window, "_todoExitClip")!;
        var enterFrames = GetClipFrames(enterClip).Cast<object>().ToArray();
        var exitFrames = GetClipFrames(exitClip).Cast<object>().ToArray();
        Assert(enterFrames.Length == expectedFrameCount &&
               exitFrames.Length == expectedFrameCount,
            $"Todo 打开/收起必须使用 idle、{wakeFrameCount}个60fps wake及完整think dense序列，共{expectedFrameCount}帧");
        var enterNames = enterFrames
            .Select(frame => GetProperty<string>(frame, "Name"))
            .ToArray();
        var exitNames = exitFrames
            .Select(frame => GetProperty<string>(frame, "Name"))
            .ToArray();
        Assert(enterNames.SequenceEqual(expectedEnterNames),
            $"Todo 入场必须按资源名完整覆盖{wakeFrameCount}帧起身与think dense序列");
        Assert(exitNames.SequenceEqual(enterNames.Reverse()),
            "Todo 出场必须与入场逐帧严格反序，保证中途反向时落在同一姿势");
        for (var expectedIndex = 0;
             expectedIndex < expectedEnterNames.Length;
             expectedIndex++)
        {
            var spriteFrame = GetProperty<object>(
                enterFrames[expectedIndex],
                "Image");
            var mappedIndex = (int)Invoke(
                window,
                "GetTodoEnterStartIndex",
                spriteFrame)!;
            Assert(mappedIndex == expectedIndex,
                $"Todo路径资源 {expectedEnterNames[expectedIndex]} 中途右键必须精确续播索引 {expectedIndex}");
        }

        const string resumedFrameName = "luban-think-smooth-015.png";
        var resumedEnterIndex = Array.IndexOf(expectedEnterNames, resumedFrameName);
        Assert(resumedEnterIndex >= 0,
            "Todo完整路径必须包含指定的think dense中间姿势");
        var resumedSpriteFrame = GetProperty<object>(
            enterFrames[resumedEnterIndex],
            "Image");
        PrimeSpritePageForFrame(window, resumedSpriteFrame);
        Invoke(window, "ShowStableFrame", resumedSpriteFrame);
        var thinkReactionClip = GetField<Array>(window, "_reactionClips").GetValue(6)!;
        var thinkReactionFrames = GetClipFrames(thinkReactionClip).Cast<object>().ToArray();
        var resumedReactionIndex = Array.FindIndex(
            thinkReactionFrames,
            frame => string.Equals(
                GetProperty<string>(frame, "Name"),
                resumedFrameName,
                StringComparison.Ordinal));
        Assert(resumedReactionIndex >= 0,
            "普通 think 动作必须实际播放同名dense姿势，Todo才能无缝续播");
        SetField(window, "_activeClip", thinkReactionClip);
        SetField(window, "_activeFrameIndex", resumedReactionIndex);
        SetField(window, "_activeClipStartedTimestamp", Stopwatch.GetTimestamp());
        SetField(window, "_activeFrameDeadlineTimestamp", long.MaxValue);
        SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
        Invoke(window, "EnterTodoVisualState");
        var resumedPresentationAt = Stopwatch.GetTimestamp();
        Invoke(window, "TryShowPendingSpriteFrameAt", resumedPresentationAt);
        var resumedAt = GetField<long>(window, "_activeClipStartedTimestamp");
        var resumedDeadline = GetField<long>(window, "_activeFrameDeadlineTimestamp");
        var resumedHoldTicks = ToProductionCharacterAnimationTicks(
            GetFrameDuration(enterFrames[resumedEnterIndex]));
        Assert(ReferenceEquals(GetRawField(window, "_activeClip"), enterClip) &&
               GetField<int>(window, "_activeFrameIndex") == resumedEnterIndex &&
               Equals(GetRawField(window, "_currentSpriteFrame"), resumedSpriteFrame) &&
               resumedAt > 0 &&
               resumedDeadline - resumedAt == resumedHoldTicks &&
               !GetField<bool>(window, "_isFrameBlending"),
            "思考dense姿势中途右键必须保留同名像素，并从1.25倍运行截止点继续，不能重播或淡化闪回");
        SetField(window, "_activeClip", null);
        SetField(window, "_activeFrameIndex", -1);
        SetField(window, "_activeClipStartedTimestamp", 0L);
        SetField(window, "_activeFrameDeadlineTimestamp", 0L);
        Invoke(window, "ClearDeferredActiveClipClock");
        Invoke(window, "UpdateVisualClockSubscription");
        var idleFrame = GetField<object>(window, "_idleFrame");
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);

        var enterDurations = enterFrames
            .Select(GetFrameDuration)
            .ToArray();
        var sixtyFpsInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
        Assert(enterDurations.All(duration => duration == sixtyFpsInterval),
            "Todo全部入场帧必须统一使用精确60fps姿势间隔");
        Assert(exitFrames.All(frame =>
                GetFrameDuration(frame) == sixtyFpsInterval),
            "Todo 收起必须保持与打开相同的精确60fps绝对时间节奏");

        var enterSource = ExtractPrivateMethodSource(
            mainWindowSource,
            "EnterTodoVisualState");
        var exitSource = ExtractPrivateMethodSource(
            mainWindowSource,
            "StartTodoExitTransition");
        Assert(enterSource.Contains(
                   "_nextFrameBlendDuration = TimeSpan.Zero",
                   StringComparison.Ordinal) &&
               exitSource.Contains(
                   "_nextFrameBlendDuration = TimeSpan.Zero",
                   StringComparison.Ordinal) &&
               !mainWindowSource.Contains(
                   "TodoStateBlendDuration",
                   StringComparison.Ordinal),
            "Todo 相邻姿势必须直接切换，不得整图交叉淡化产生双轮廓或像素光纹");

        var total = TimeSpan.FromTicks(enterDurations.Sum(duration => duration.Ticks));
        var expectedTodoDuration = TimeSpan.FromTicks(
            expectedFrameCount * sixtyFpsInterval.Ticks);
        Assert(total == expectedTodoDuration,
            $"Todo 完整{expectedFrameCount}帧时间线应为{expectedTodoDuration.TotalSeconds:F3}秒，" +
            $"实际 {total.TotalMilliseconds:F0}ms");
        var enterHoldTicks = enterDurations
            .Select(ToProductionCharacterAnimationTicks)
            .ToArray();
        var effectiveFrameTicks =
            ToProductionCharacterAnimationTicks(sixtyFpsInterval);
        Assert(enterHoldTicks.All(ticks => ticks == effectiveFrameTicks),
            "Todo运行deadline必须逐帧调用生产ToCharacterAnimationTicks应用1.25倍代码速度");
        var checkpoints = new long[]
        {
            0,
            effectiveFrameTicks / 2,
            effectiveFrameTicks,
            StopwatchTicksFromMilliseconds(250),
            StopwatchTicksFromMilliseconds(500),
            StopwatchTicksFromMilliseconds(800)
        };
        var deadlineToleranceTicks = (long)(typeof(MainWindow).GetField(
                "VisualFrameDeadlineToleranceTicks",
                StaticFlags)!.GetValue(null) ?? 0L);
        var resolvedFrames = checkpoints
            .Select(elapsedTicks => ResolveProductionClipFrameIndex(
                window,
                enterClip,
                elapsedTicks))
            .ToArray();
        var expectedFrames = checkpoints
            .Select(elapsedTicks => ResolveAbsoluteFrameIndexAtStopwatchTicks(
                enterHoldTicks,
                elapsedTicks,
                deadlineToleranceTicks))
            .ToArray();
        Assert(resolvedFrames.SequenceEqual(expectedFrames),
            "Todo生产AdvanceActiveClip必须在相同绝对时刻稳定定位同一姿势");

        AssertProductionDiscreteVsyncTimeline(
            window,
            enterClip,
            "todo-enter");
    }

    private sealed record VsyncClipSample(
        int VsyncOrdinal,
        long ElapsedStopwatchTicks,
        int DisplayedFrameIndex);

    private sealed record VsyncClipRun(
        VsyncClipSample[] Samples,
        long CompletionElapsedTicks);

    private sealed record VsyncStallRun(
        int BeforeFrameIndex,
        int StalledFrameIndex,
        int NextFrameIndex,
        long StallElapsedTicks);

    private readonly record struct EdgePeekClockState(
        int FrameIndex,
        long DeadlineTimestamp);

    private static void AssertEdgePeekRenderingCadenceTimelineContract(
        MainWindow window)
    {
        PauseSpritePageWarmupForClockSimulation(window);
        var frames = GetField<Array>(window, "_edgeLeftFrames");
        Assert(frames.Length == ExpectedEdgePeekFrameCount,
            $"边缘合成节拍回归必须使用完整{ExpectedEdgePeekFrameCount}帧生产序列，" +
            $"实际{frames.Length}帧");

        var holdTicks = Enumerable.Range(0, frames.Length)
            .Select(frameIndex => ToProductionStopwatchTicks(
                (TimeSpan)InvokeStatic(
                    typeof(MainWindow),
                    "GetEdgePeekFrameHoldDuration",
                    frameIndex,
                    frames.Length)!))
            .ToArray();
        var cycleTicks = (long)InvokeStatic(
            typeof(MainWindow),
            "GetEdgePeekCycleDurationTicks",
            frames.Length)!;
        Assert(cycleTicks == holdTicks.Aggregate(
                   0L,
                   checked((total, ticks) => total + ticks)),
            $"边缘生产周期必须等于{ExpectedEdgePeekFrameCount}个姿势hold之和，" +
            "长时间模拟才不会掩盖周期漂移");

        try
        {
            foreach (var refreshRate in new[] { 59d, 59.94d, 60d })
            {
                AssertHealthyNearSixtyEdgePeekTimeline(
                    window,
                    frames,
                    holdTicks,
                    cycleTicks,
                    refreshRate);
            }

            foreach (var refreshRate in new[] { 120d, 144d })
            {
                AssertAbsoluteEdgePeekTimeline(
                    window,
                    frames,
                    holdTicks,
                    cycleTicks,
                    refreshRate);
            }

            AssertEdgePeekStallAndCadenceRecovery(
                window,
                frames,
                holdTicks,
                cycleTicks);
        }
        finally
        {
            CleanupProductionEdgePeekClockSimulation(window);
        }
    }

    private static void AssertHealthyNearSixtyEdgePeekTimeline(
        MainWindow window,
        Array frames,
        IReadOnlyList<long> holdTicks,
        long cycleTicks,
        double refreshRate)
    {
        var startedAt = StopwatchTicksFromSeconds(100);
        PrepareProductionEdgePeekClockSimulation(
            window,
            frames,
            startedAt,
            holdTicks[^1]);
        try
        {
            var fullyPeekedFrameIndex = frames.Length / 2 - 1;
            var restingFrameIndex = frames.Length - 1;
            var maximumVsyncTicks = (long)Math.Ceiling(
                Stopwatch.Frequency / refreshRate);
            var previousFrameIndex = frames.Length - 1;
            var previousDeadline = GetField<long>(
                window,
                "_edgePeekFrameDeadlineTimestamp");
            var endpointDisplayedAt = startedAt;
            var endpointHoldCounts = new int[frames.Length];
            var frameZeroTimestamps = new List<long>();
            var maximumCycleErrorRatio = 0d;
            var maximumVsyncOrdinal = checked(
                (int)Math.Ceiling(60d * refreshRate));
            var lastTimestamp = startedAt;

            for (var vsyncOrdinal = 1;
                 vsyncOrdinal <= maximumVsyncOrdinal;
                 vsyncOrdinal++)
            {
                var timestamp = checked(
                    startedAt + SyntheticStopwatchElapsedTicks(
                        refreshRate,
                        vsyncOrdinal));
                var presentationInterval = SyntheticPresentationInterval(
                    refreshRate,
                    vsyncOrdinal);
                var synchronize = (bool)InvokeStatic(
                    typeof(MainWindow),
                    "ShouldSynchronizeEdgePeekToRenderingCadence",
                    presentationInterval)!;
                Assert(synchronize,
                    $"{refreshRate:F2}Hz健康合成的第{vsyncOrdinal}次实际离散间隔必须持续识别为近60Hz");

                SetField(
                    window,
                    "_synchronizeEdgePeekToRenderingCadence",
                    synchronize);
                try
                {
                    Invoke(window, "AdvanceEdgePeek", timestamp);
                }
                finally
                {
                    SetField(
                        window,
                        "_synchronizeEdgePeekToRenderingCadence",
                        false);
                }

                var frameIndex = GetField<int>(window, "_edgePeekFrameIndex");
                var deadline = GetField<long>(
                    window,
                    "_edgePeekFrameDeadlineTimestamp");
                var logicalAdvance =
                    (frameIndex - previousFrameIndex + frames.Length) %
                    frames.Length;
                Assert(logicalAdvance is 0 or 1 &&
                       (logicalAdvance != 0 || deadline == previousDeadline),
                    $"{refreshRate:F2}Hz健康合成每次回调只能逻辑推进0或1帧，" +
                    $"vsync={vsyncOrdinal}, {previousFrameIndex}->{frameIndex}");

                if (logicalAdvance == 1)
                {
                    var expectedFrameIndex =
                        (previousFrameIndex + 1) % frames.Length;
                    Assert(frameIndex == expectedFrameIndex,
                        $"{refreshRate:F2}Hz边缘姿势必须完整有序且不漏帧，" +
                        $"expected={expectedFrameIndex}, actual={frameIndex}");
                    AssertDisplayedEdgeFrame(window, frames, frameIndex);

                    if (previousFrameIndex == fullyPeekedFrameIndex ||
                        previousFrameIndex == restingFrameIndex)
                    {
                        var expectedEndpointHoldTicks =
                            holdTicks[previousFrameIndex];
                        var actualEndpointHoldTicks =
                            timestamp - endpointDisplayedAt;
                        Assert(actualEndpointHoldTicks >= expectedEndpointHoldTicks &&
                               actualEndpointHoldTicks <
                               expectedEndpointHoldTicks + maximumVsyncTicks,
                            $"{refreshRate:F2}Hz frame{previousFrameIndex}端点实际hold必须" +
                            $"落在[{StopwatchTicksToMilliseconds(expectedEndpointHoldTicks):F0}ms, " +
                            $"目标+1vsync)；实际" +
                            $"{StopwatchTicksToMilliseconds(actualEndpointHoldTicks):F3}ms");
                        endpointHoldCounts[previousFrameIndex]++;
                    }

                    if (frameIndex == fullyPeekedFrameIndex ||
                        frameIndex == restingFrameIndex)
                    {
                        endpointDisplayedAt = timestamp;
                    }

                    if (frameIndex == 0)
                    {
                        frameZeroTimestamps.Add(timestamp);
                    }
                }
                else
                {
                    AssertDisplayedEdgeFrame(window, frames, frameIndex);
                }

                previousFrameIndex = frameIndex;
                previousDeadline = deadline;
                lastTimestamp = timestamp;
            }

            Assert(lastTimestamp - startedAt >= StopwatchTicksFromSeconds(60),
                $"{refreshRate:F2}Hz健康边缘模拟必须覆盖至少60秒");
            var completedCycles = frameZeroTimestamps.Count - 1;
            Assert(completedCycles >= 20 &&
                   endpointHoldCounts[fullyPeekedFrameIndex] >= 20 &&
                   endpointHoldCounts[restingFrameIndex] >= 20,
                $"{refreshRate:F2}Hz健康边缘模拟至少应完整覆盖20轮；" +
                $"cycles={completedCycles}, " +
                $"hold{fullyPeekedFrameIndex}=" +
                $"{endpointHoldCounts[fullyPeekedFrameIndex]}, " +
                $"hold{restingFrameIndex}=" +
                $"{endpointHoldCounts[restingFrameIndex]}");

            for (var cycleIndex = 1;
                 cycleIndex < frameZeroTimestamps.Count;
                 cycleIndex++)
            {
                var actualCycleTicks =
                    frameZeroTimestamps[cycleIndex] -
                    frameZeroTimestamps[cycleIndex - 1];
                var cycleErrorRatio =
                    Math.Abs(actualCycleTicks - cycleTicks) /
                    (double)cycleTicks;
                maximumCycleErrorRatio = Math.Max(
                    maximumCycleErrorRatio,
                    cycleErrorRatio);
                Assert(cycleErrorRatio < 0.02,
                    $"{refreshRate:F2}Hz边缘周期误差必须小于2%，" +
                    $"cycle={cycleIndex}, error={cycleErrorRatio:P3}");
            }

            Console.WriteLine(
                $"[METRIC] edge-peek vsync={refreshRate:F2}Hz: " +
                $"duration={StopwatchTicksToMilliseconds(lastTimestamp - startedAt):F3}ms, " +
                $"cycles={completedCycles}, maxCycleError={maximumCycleErrorRatio:P3}");
        }
        finally
        {
            CleanupProductionEdgePeekClockSimulation(window);
        }
    }

    private static void AssertAbsoluteEdgePeekTimeline(
        MainWindow window,
        Array frames,
        IReadOnlyList<long> holdTicks,
        long cycleTicks,
        double refreshRate)
    {
        var startedAt = StopwatchTicksFromSeconds(200);
        PrepareProductionEdgePeekClockSimulation(
            window,
            frames,
            startedAt,
            holdTicks[^1]);
        try
        {
            var expected = new EdgePeekClockState(
                frames.Length - 1,
                checked(startedAt + holdTicks[^1]));
            var maximumVsyncOrdinal = checked(
                (int)Math.Ceiling(10d * refreshRate));
            for (var vsyncOrdinal = 1;
                 vsyncOrdinal <= maximumVsyncOrdinal;
                 vsyncOrdinal++)
            {
                var timestamp = checked(
                    startedAt + SyntheticStopwatchElapsedTicks(
                        refreshRate,
                        vsyncOrdinal));
                var presentationInterval = SyntheticPresentationInterval(
                    refreshRate,
                    vsyncOrdinal);
                var synchronize = (bool)InvokeStatic(
                    typeof(MainWindow),
                    "ShouldSynchronizeEdgePeekToRenderingCadence",
                    presentationInterval)!;
                Assert(!synchronize,
                    $"{refreshRate:F0}Hz必须持续使用既有绝对deadline模型");

                expected = ResolveAbsoluteEdgePeekClockState(
                    holdTicks,
                    cycleTicks,
                    expected,
                    timestamp);
                SetField(
                    window,
                    "_synchronizeEdgePeekToRenderingCadence",
                    false);
                Invoke(window, "AdvanceEdgePeek", timestamp);
                var actual = new EdgePeekClockState(
                    GetField<int>(window, "_edgePeekFrameIndex"),
                    GetField<long>(
                        window,
                        "_edgePeekFrameDeadlineTimestamp"));
                Assert(actual == expected,
                    $"{refreshRate:F0}Hz边缘绝对时钟必须逐回调匹配既有模型，" +
                    $"vsync={vsyncOrdinal}, expected={expected}, actual={actual}");
                AssertDisplayedEdgeFrame(
                    window,
                    frames,
                    actual.FrameIndex);
            }

            Console.WriteLine(
                $"[METRIC] edge-peek absolute={refreshRate:F0}Hz: " +
                $"callbacks={maximumVsyncOrdinal}, final={expected.FrameIndex}");
        }
        finally
        {
            CleanupProductionEdgePeekClockSimulation(window);
        }
    }

    private static void AssertEdgePeekStallAndCadenceRecovery(
        MainWindow window,
        Array frames,
        IReadOnlyList<long> holdTicks,
        long cycleTicks)
    {
        const double refreshRate = 59.94d;
        var startedAt = StopwatchTicksFromSeconds(300);
        PrepareProductionEdgePeekClockSimulation(
            window,
            frames,
            startedAt,
            holdTicks[^1]);
        try
        {
            var firstFrameTimestamp = -1L;
            var firstFrameVsyncOrdinal = -1;
            for (var vsyncOrdinal = 1;
                 vsyncOrdinal <= (int)Math.Ceiling(refreshRate);
                 vsyncOrdinal++)
            {
                var timestamp = checked(
                    startedAt + SyntheticStopwatchElapsedTicks(
                        refreshRate,
                        vsyncOrdinal));
                var synchronize = (bool)InvokeStatic(
                    typeof(MainWindow),
                    "ShouldSynchronizeEdgePeekToRenderingCadence",
                    SyntheticPresentationInterval(
                        refreshRate,
                        vsyncOrdinal))!;
                Assert(synchronize,
                    "250ms阻塞探针前的59.94Hz合成必须处于同步路径");
                SetField(
                    window,
                    "_synchronizeEdgePeekToRenderingCadence",
                    true);
                try
                {
                    Invoke(window, "AdvanceEdgePeek", timestamp);
                }
                finally
                {
                    SetField(
                        window,
                        "_synchronizeEdgePeekToRenderingCadence",
                        false);
                }

                if (GetField<int>(window, "_edgePeekFrameIndex") == 0)
                {
                    firstFrameTimestamp = timestamp;
                    firstFrameVsyncOrdinal = vsyncOrdinal;
                    break;
                }
            }

            Assert(firstFrameTimestamp >= startedAt &&
                   firstFrameVsyncOrdinal > 0,
                $"59.94Hz基线必须先从rest frame{frames.Length - 1}有序进入frame0");
            var beforeStall = new EdgePeekClockState(
                GetField<int>(window, "_edgePeekFrameIndex"),
                GetField<long>(
                    window,
                    "_edgePeekFrameDeadlineTimestamp"));
            Assert(beforeStall.FrameIndex == 0,
                "250ms阻塞探针必须从frame0的同步deadline开始");

            var stallTimestamp = checked(
                firstFrameTimestamp +
                StopwatchTicksFromMilliseconds(250));
            var gapSynchronizes = (bool)InvokeStatic(
                typeof(MainWindow),
                "ShouldSynchronizeEdgePeekToRenderingCadence",
                TimeSpan.FromMilliseconds(250))!;
            Assert(!gapSynchronizes,
                "250ms合成gap必须关闭近60Hz同步并恢复绝对时钟定位");
            var expectedAfterStall = ResolveAbsoluteEdgePeekClockState(
                holdTicks,
                cycleTicks,
                beforeStall,
                stallTimestamp);
            var skippedFrameCount =
                (expectedAfterStall.FrameIndex -
                 beforeStall.FrameIndex +
                 frames.Length) %
                frames.Length;
            Assert(skippedFrameCount > 1,
                "250ms阻塞样本必须跨过多个运动姿势，才能证明不是逐帧补播");

            SetField(
                window,
                "_synchronizeEdgePeekToRenderingCadence",
                gapSynchronizes);
            Invoke(window, "AdvanceEdgePeek", stallTimestamp);
            var actualAfterStall = new EdgePeekClockState(
                GetField<int>(window, "_edgePeekFrameIndex"),
                GetField<long>(
                    window,
                    "_edgePeekFrameDeadlineTimestamp"));
            Assert(actualAfterStall == expectedAfterStall,
                $"250ms gap必须由一次生产调用直接定位最终帧；" +
                $"expected={expectedAfterStall}, actual={actualAfterStall}");
            AssertDisplayedEdgeFrame(
                window,
                frames,
                actualAfterStall.FrameIndex);

            Invoke(window, "AdvanceEdgePeek", stallTimestamp);
            Assert(new EdgePeekClockState(
                       GetField<int>(window, "_edgePeekFrameIndex"),
                       GetField<long>(
                           window,
                           "_edgePeekFrameDeadlineTimestamp")) ==
                   actualAfterStall,
                "相同stall时间戳重复调用不得继续补播积压姿势");

            var previousFrameIndex = actualAfterStall.FrameIndex;
            var previousDeadline = actualAfterStall.DeadlineTimestamp;
            var synchronizedAdvanceObserved = false;
            for (var recoveryVsyncOrdinal = 1;
                 recoveryVsyncOrdinal <= 40;
                 recoveryVsyncOrdinal++)
            {
                var recoveryTimestamp = checked(
                    stallTimestamp + SyntheticStopwatchElapsedTicks(
                        refreshRate,
                        recoveryVsyncOrdinal));
                var presentationInterval = SyntheticPresentationInterval(
                    refreshRate,
                    recoveryVsyncOrdinal);
                var synchronize = (bool)InvokeStatic(
                    typeof(MainWindow),
                    "ShouldSynchronizeEdgePeekToRenderingCadence",
                    presentationInterval)!;
                Assert(synchronize,
                    "250ms gap后的正常59.94Hz间隔必须重新进入同步路径");
                SetField(
                    window,
                    "_synchronizeEdgePeekToRenderingCadence",
                    true);
                try
                {
                    Invoke(window, "AdvanceEdgePeek", recoveryTimestamp);
                }
                finally
                {
                    SetField(
                        window,
                        "_synchronizeEdgePeekToRenderingCadence",
                        false);
                }

                var frameIndex = GetField<int>(window, "_edgePeekFrameIndex");
                var deadline = GetField<long>(
                    window,
                    "_edgePeekFrameDeadlineTimestamp");
                var logicalAdvance =
                    (frameIndex - previousFrameIndex + frames.Length) %
                    frames.Length;
                Assert(logicalAdvance is 0 or 1 &&
                       (logicalAdvance != 0 || deadline == previousDeadline),
                    "stall恢复后的健康合成仍必须每次只逻辑推进0或1帧");
                if (logicalAdvance == 1)
                {
                    Assert(frameIndex ==
                           (previousFrameIndex + 1) % frames.Length,
                        "stall恢复后不得漏帧或乱序");
                    Assert(deadline == checked(
                               recoveryTimestamp + holdTicks[frameIndex]),
                        "stall恢复后的首次逻辑推进必须把deadline重基到当前合成时刻");
                    synchronizedAdvanceObserved = true;
                }

                AssertDisplayedEdgeFrame(window, frames, frameIndex);
                previousFrameIndex = frameIndex;
                previousDeadline = deadline;
            }

            Assert(synchronizedAdvanceObserved,
                "250ms绝对定位后的正常vsync必须实际恢复逐合成同步");
            Console.WriteLine(
                $"[METRIC] edge-peek stall=250ms: " +
                $"frame0->{actualAfterStall.FrameIndex}, " +
                $"baselineVsync={firstFrameVsyncOrdinal}, recovered=true");
        }
        finally
        {
            SetField(
                window,
                "_synchronizeEdgePeekToRenderingCadence",
                false);
            CleanupProductionEdgePeekClockSimulation(window);
        }
    }

    private static EdgePeekClockState ResolveAbsoluteEdgePeekClockState(
        IReadOnlyList<long> holdTicks,
        long cycleTicks,
        EdgePeekClockState current,
        long timestamp)
    {
        var frameIndex = current.FrameIndex;
        var deadline = current.DeadlineTimestamp;
        var overdueTicks = timestamp - deadline;
        if (overdueTicks >= cycleTicks)
        {
            deadline = checked(
                deadline + overdueTicks / cycleTicks * cycleTicks);
        }

        while (timestamp >= deadline)
        {
            frameIndex = (frameIndex + 1) % holdTicks.Count;
            deadline = checked(deadline + holdTicks[frameIndex]);
        }

        return new EdgePeekClockState(frameIndex, deadline);
    }

    private static long SyntheticStopwatchElapsedTicks(
        double refreshRate,
        int vsyncOrdinal) =>
        (long)Math.Round(
            vsyncOrdinal * Stopwatch.Frequency / refreshRate);

    private static TimeSpan SyntheticPresentationInterval(
        double refreshRate,
        int vsyncOrdinal)
    {
        var currentTicks = (long)Math.Round(
            vsyncOrdinal * TimeSpan.TicksPerSecond / refreshRate);
        var previousTicks = (long)Math.Round(
            (vsyncOrdinal - 1L) * TimeSpan.TicksPerSecond / refreshRate);
        return TimeSpan.FromTicks(currentTicks - previousTicks);
    }

    private static void PrepareProductionEdgePeekClockSimulation(
        MainWindow window,
        Array frames,
        long startedAt,
        long restHoldTicks)
    {
        Invoke(window, "StopVisualClock");
        GetField<DispatcherTimer>(window, "_automaticTimer").Stop();
        Invoke(window, "StopFrameBlend", false);
        Assert(GetRawField(window, "_activeClip") is null &&
               !GetField<bool>(window, "_isReminderActive"),
            "边缘时钟模拟开始前不得残留动作或提醒状态");
        SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_pendingSpriteFrameBlendDuration", TimeSpan.Zero);
        SetField(window, "_failedSpritePageName", null);

        var primedPageNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in frames.Cast<object>())
        {
            if (primedPageNames.Add(GetSpriteFrameInfo(frame).PageName))
            {
                PrimeSpritePageForFrame(window, frame);
            }
        }

        Invoke(window, "ResetPetVisualTransforms");
        var restFrameIndex = frames.Length - 1;
        var restFrame = frames.GetValue(restFrameIndex)!;
        SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "Left"));
        SetField(window, "_edgePeekFrameIndex", restFrameIndex);
        SetField(window, "_nextFrameBlendDuration", TimeSpan.Zero);
        Invoke(window, "ShowStableFrame", restFrame);
        Assert(Equals(GetRawField(window, "_currentSpriteFrame"), restFrame) &&
               GetRawField(window, "_pendingSpriteFrame") is null,
            $"边缘时钟模拟必须从已实际显示的rest frame{restFrameIndex}开始");
        SetField(
            window,
            "_edgePeekFrameDeadlineTimestamp",
            checked(startedAt + restHoldTicks));
        SetField(
            window,
            "_synchronizeEdgePeekToRenderingCadence",
            false);
    }

    private static void CleanupProductionEdgePeekClockSimulation(
        MainWindow window)
    {
        SetField(
            window,
            "_synchronizeEdgePeekToRenderingCadence",
            false);
        SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_edgePeekFrameIndex", 0);
        SetField(window, "_edgePeekFrameDeadlineTimestamp", 0L);
        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_pendingSpriteFrameBlendDuration", TimeSpan.Zero);
        Invoke(window, "ResetPetVisualTransforms");
        var idleFrame = GetField<object>(window, "_idleFrame");
        PrimeSpritePageForFrame(window, idleFrame);
        SetField(window, "_nextFrameBlendDuration", TimeSpan.Zero);
        Invoke(window, "ShowStableFrame", idleFrame);
        Invoke(window, "UpdateVisualClockSubscription");
        Invoke(window, "StopVisualClock");
    }

    private static void AssertDisplayedEdgeFrame(
        MainWindow window,
        Array frames,
        int frameIndex)
    {
        Assert(Equals(
                   GetRawField(window, "_currentSpriteFrame"),
                   frames.GetValue(frameIndex)) &&
               GetRawField(window, "_pendingSpriteFrame") is null &&
               !GetField<bool>(window, "_isFrameBlending"),
            $"生产AdvanceEdgePeek索引{frameIndex}必须直接显示对应SpriteFrame，" +
            "不得留下待显示帧或整图淡化");
    }

    private static void AssertProductionDiscreteVsyncTimeline(
        MainWindow window,
        object clip,
        string label)
    {
        var frames = GetClipFrames(clip);
        Assert(frames.Length > 30,
            $"{label}离散vsync测试至少需要30帧，以覆盖250ms阻塞跳帧");
        var holdTicks = frames
            .Cast<object>()
            .Select(frame => ToProductionCharacterAnimationTicks(
                GetFrameDuration(frame)))
            .ToArray();
        var totalHoldTicks = holdTicks.Aggregate(0L, checked((total, ticks) => total + ticks));
        var deadlineToleranceTicks = (long)(typeof(MainWindow).GetField(
                "VisualFrameDeadlineToleranceTicks",
                StaticFlags)!.GetValue(null) ?? 0L);

        foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
        {
            var firstRun = RunProductionVsyncClip(
                window,
                clip,
                refreshRate,
                holdTicks,
                totalHoldTicks);
            var replayRun = RunProductionVsyncClip(
                window,
                clip,
                refreshRate,
                holdTicks,
                totalHoldTicks);
            Assert(firstRun.CompletionElapsedTicks == replayRun.CompletionElapsedTicks &&
                   firstRun.Samples.SequenceEqual(replayRun.Samples),
                $"{label}在{refreshRate:F2}Hz相同绝对vsync时刻必须产生完全一致的显示帧轨迹");

            for (var sampleIndex = 1; sampleIndex < firstRun.Samples.Length; sampleIndex++)
            {
                var previous = firstRun.Samples[sampleIndex - 1];
                var current = firstRun.Samples[sampleIndex];
                Assert(current.ElapsedStopwatchTicks > previous.ElapsedStopwatchTicks &&
                       current.DisplayedFrameIndex >= previous.DisplayedFrameIndex,
                    $"{label}在{refreshRate:F2}Hz正常vsync下索引必须单调；" +
                    $"sample={sampleIndex}, previous={previous.DisplayedFrameIndex}, " +
                    $"current={current.DisplayedFrameIndex}");

                var expectedIndex = ResolveAbsoluteFrameIndexAtStopwatchTicks(
                    holdTicks,
                    current.ElapsedStopwatchTicks,
                    deadlineToleranceTicks);
                Assert(current.DisplayedFrameIndex == expectedIndex,
                    $"{label}在{refreshRate:F0}Hz必须按1.25倍生产绝对deadline定位，不得累计漂移；" +
                    $"vsync={current.VsyncOrdinal}, expected={expectedIndex}, " +
                    $"actual={current.DisplayedFrameIndex}");
            }

            var refreshIntervalTicks = (long)Math.Ceiling(
                Stopwatch.Frequency / refreshRate);
            Assert(firstRun.CompletionElapsedTicks + deadlineToleranceTicks >= totalHoldTicks &&
                   firstRun.CompletionElapsedTicks - totalHoldTicks <= refreshIntervalTicks,
                $"{label}在{refreshRate:F0}Hz的1.25倍绝对时间轴完成误差不得超过一个vsync");

            var stallRun = RunProductionVsyncStall(
                window,
                clip,
                refreshRate,
                holdTicks,
                deadlineToleranceTicks);
            Assert(stallRun.StalledFrameIndex > stallRun.BeforeFrameIndex &&
                   stallRun.NextFrameIndex >= stallRun.StalledFrameIndex,
                $"{label}在{refreshRate:F2}Hz遇到250ms阻塞时，单次回调必须跳到最终姿势，" +
                    "下一vsync不得快速补播积压帧");

            Console.WriteLine(
                $"[METRIC] {label} vsync={refreshRate:F2}Hz: " +
                $"callbacks={firstRun.Samples.Length - 1}, " +
                $"completion={StopwatchTicksToMilliseconds(firstRun.CompletionElapsedTicks):F3}ms, " +
                $"stallAt={StopwatchTicksToMilliseconds(stallRun.StallElapsedTicks):F3}ms, " +
                $"stall={stallRun.BeforeFrameIndex}->{stallRun.StalledFrameIndex}" +
                $"->{stallRun.NextFrameIndex}");
        }

        RestoreIdleAfterClipClockSimulation(window);
    }

    private static VsyncClipRun RunProductionVsyncClip(
        MainWindow window,
        object clip,
        double refreshRate,
        IReadOnlyList<long> holdTicks,
        long totalHoldTicks)
    {
        var frames = GetClipFrames(clip);
        var startedAt = StopwatchTicksFromSeconds(30);
        PrepareProductionClipClockSimulation(window, clip, frames, startedAt, holdTicks[0]);

        var samples = new List<VsyncClipSample>(frames.Length * 3);
        samples.Add(new VsyncClipSample(0, 0, 0));
        var previousRenderingTime = TimeSpan.Zero;
        var maximumVsyncCount = checked(
            (int)Math.Ceiling(totalHoldTicks / (double)Stopwatch.Frequency * refreshRate * 1.05) + 4);
        long completionElapsedTicks = -1;
        for (var vsyncOrdinal = 1; vsyncOrdinal <= maximumVsyncCount; vsyncOrdinal++)
        {
            var elapsedStopwatchTicks = (long)Math.Round(
                vsyncOrdinal * Stopwatch.Frequency / refreshRate);
            var renderingTime = TimeSpan.FromTicks((long)Math.Round(
                vsyncOrdinal * TimeSpan.TicksPerSecond / refreshRate));
            var displayedFrameIndex = AdvanceProductionClipAtSyntheticVsync(
                window,
                clip,
                frames,
                startedAt,
                elapsedStopwatchTicks,
                previousRenderingTime,
                renderingTime);
            samples.Add(new VsyncClipSample(
                vsyncOrdinal,
                elapsedStopwatchTicks,
                displayedFrameIndex));
            previousRenderingTime = renderingTime;
            if (displayedFrameIndex == frames.Length)
            {
                completionElapsedTicks = elapsedStopwatchTicks;
                break;
            }
        }

        Assert(completionElapsedTicks >= 0,
            $"{refreshRate:F2}Hz离散vsync必须在测试上限内完成动作");
        CleanupProductionClipClockSimulation(window);
        return new VsyncClipRun(samples.ToArray(), completionElapsedTicks);
    }

    private static VsyncStallRun RunProductionVsyncStall(
        MainWindow window,
        object clip,
        double refreshRate,
        IReadOnlyList<long> holdTicks,
        long deadlineToleranceTicks)
    {
        var frames = GetClipFrames(clip);
        var startedAt = StopwatchTicksFromSeconds(30);
        PrepareProductionClipClockSimulation(window, clip, frames, startedAt, holdTicks[0]);
        var previousRenderingTime = TimeSpan.Zero;
        var previousElapsedStopwatchTicks = 0L;
        var preStallVsyncCount = Math.Max(1, (int)Math.Floor(0.140 * refreshRate));
        for (var vsyncOrdinal = 1; vsyncOrdinal <= preStallVsyncCount; vsyncOrdinal++)
        {
            previousElapsedStopwatchTicks = (long)Math.Round(
                vsyncOrdinal * Stopwatch.Frequency / refreshRate);
            var renderingTime = TimeSpan.FromTicks((long)Math.Round(
                vsyncOrdinal * TimeSpan.TicksPerSecond / refreshRate));
            _ = AdvanceProductionClipAtSyntheticVsync(
                window,
                clip,
                frames,
                startedAt,
                previousElapsedStopwatchTicks,
                previousRenderingTime,
                renderingTime);
            previousRenderingTime = renderingTime;
        }

        var beforeFrameIndex = GetDisplayedClipFrameIndex(window, clip, frames);
        var beforeDeadline = GetField<long>(window, "_activeFrameDeadlineTimestamp");
        var stallElapsedTicks = checked(
            previousElapsedStopwatchTicks + StopwatchTicksFromMilliseconds(250));
        var stallRenderingTime = previousRenderingTime + TimeSpan.FromMilliseconds(250);
        var expectedFromCurrentTimeline = ResolveFrameIndexFromProductionDeadline(
            holdTicks,
            beforeFrameIndex,
            beforeDeadline,
            startedAt + stallElapsedTicks,
            deadlineToleranceTicks);
        var expectedFromAbsoluteTimeline = ResolveAbsoluteFrameIndexAtStopwatchTicks(
            holdTicks,
            stallElapsedTicks,
            deadlineToleranceTicks);
        Assert(expectedFromCurrentTimeline == expectedFromAbsoluteTimeline,
            $"{refreshRate:F2}Hz在阻塞前的健康vsync不得积累足以改变姿势的deadline漂移");

        var stalledFrameIndex = AdvanceProductionClipAtSyntheticVsync(
            window,
            clip,
            frames,
            startedAt,
            stallElapsedTicks,
            previousRenderingTime,
            stallRenderingTime);
        Assert(stalledFrameIndex == expectedFromAbsoluteTimeline,
            $"{refreshRate:F2}Hz的250ms阻塞后必须由一次生产AdvanceActiveClip直接落到" +
            $"绝对姿势{expectedFromAbsoluteTimeline}，实际{stalledFrameIndex}");

        SetField(window, "_synchronizeActiveClipToRenderingCadence", false);
        Invoke(window, "AdvanceActiveClip", startedAt + stallElapsedTicks);
        Assert(GetDisplayedClipFrameIndex(window, clip, frames) == stalledFrameIndex,
            $"{refreshRate:F2}Hz同一绝对时间重复调用不得推进或补播第二个姿势");

        var nextElapsedTicks = checked(
            stallElapsedTicks + (long)Math.Round(Stopwatch.Frequency / refreshRate));
        var nextRenderingTime = stallRenderingTime + TimeSpan.FromTicks((long)Math.Round(
            TimeSpan.TicksPerSecond / refreshRate));
        var nextFrameIndex = AdvanceProductionClipAtSyntheticVsync(
            window,
            clip,
            frames,
            startedAt,
            nextElapsedTicks,
            stallRenderingTime,
            nextRenderingTime);
        var expectedNextFrameIndex = ResolveAbsoluteFrameIndexAtStopwatchTicks(
            holdTicks,
            nextElapsedTicks,
            deadlineToleranceTicks);
        Assert(nextFrameIndex == expectedNextFrameIndex,
            $"{refreshRate:F2}Hz阻塞后的下一正常vsync仍须回到绝对时间姿势，" +
            $"expected={expectedNextFrameIndex}, actual={nextFrameIndex}");

        CleanupProductionClipClockSimulation(window);
        return new VsyncStallRun(
            beforeFrameIndex,
            stalledFrameIndex,
            nextFrameIndex,
            stallElapsedTicks);
    }

    private static int AdvanceProductionClipAtSyntheticVsync(
        MainWindow window,
        object clip,
        Array frames,
        long startedAt,
        long elapsedStopwatchTicks,
        TimeSpan previousRenderingTime,
        TimeSpan renderingTime)
    {
        var synchronizeToCadence = (bool)InvokeStatic(
            typeof(MainWindow),
            "ShouldSynchronizeActiveClipToRenderingCadence",
            renderingTime - previousRenderingTime)!;
        SetField(window, "_synchronizeActiveClipToRenderingCadence", synchronizeToCadence);
        try
        {
            Invoke(window, "AdvanceActiveClip", checked(startedAt + elapsedStopwatchTicks));
        }
        finally
        {
            SetField(window, "_synchronizeActiveClipToRenderingCadence", false);
        }

        var nextBlendDuration = GetRawField(window, "_nextFrameBlendDuration");
        Assert(!GetField<bool>(window, "_isFrameBlending") &&
               GetRawField(window, "_pendingSpriteFrame") is null &&
               (nextBlendDuration is null ||
                nextBlendDuration is TimeSpan { Ticks: 0 }),
            "绝对时间轴在正常vsync或250ms stall后必须直接显示正确姿势，" +
            "不得启动整图淡化或留下逐帧补播队列");

        var displayedFrameIndex = GetDisplayedClipFrameIndex(window, clip, frames);
        if (displayedFrameIndex < frames.Length)
        {
            AssertDisplayedClipFrame(window, frames, displayedFrameIndex);
        }
        else
        {
            Assert(!ReferenceEquals(GetRawField(window, "_activeClip"), clip),
                "绝对时间轴到达片段末尾后必须完成该片段；最终静态画面由各片段自己的完成逻辑决定");
        }
        return displayedFrameIndex;
    }

    private static void PrepareProductionClipClockSimulation(
        MainWindow window,
        object clip,
        Array frames,
        long startedAt,
        long firstHoldTicks)
    {
        PauseSpritePageWarmupForClockSimulation(window);
        Invoke(window, "StopVisualClock");
        GetField<DispatcherTimer>(window, "_automaticTimer").Stop();
        Invoke(window, "StopFrameBlend", false);
        SetField(window, "_activeClip", clip);
        PrimeAllClipPagesForTest(window, frames);
        var firstSpriteFrame = GetProperty<object>(frames.GetValue(0)!, "Image");
        PrimeSpritePageForFrame(window, firstSpriteFrame);
        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_pendingSpriteFrameBlendDuration", TimeSpan.Zero);
        SetField(window, "_nextFrameBlendDuration", TimeSpan.Zero);
        Invoke(window, "ShowStableFrame", firstSpriteFrame);
        Invoke(window, "ClearDeferredActiveClipClock");
        SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
        SetField(window, "_activeFrameIndex", 0);
        SetField(window, "_activeClipStartedTimestamp", startedAt);
        SetField(window, "_activeFrameDeadlineTimestamp", checked(startedAt + firstHoldTicks));
        SetField(window, "_synchronizeActiveClipToRenderingCadence", false);
    }

    private static void PauseSpritePageWarmupForClockSimulation(MainWindow window)
    {
        SetField(window, "_spritePageWarmupEnabled", false);
        SetField(window, "_desiredSpritePageName", null);
        SetField(window, "_desiredSpritePageUrgent", false);
        SetField(
            window,
            "_spritePagePrefetchGeneration",
            GetField<int>(window, "_spritePagePrefetchGeneration") + 1);
        Invoke(window, "RequestSpritePagePrefetchCancellation");

        var deadline = Stopwatch.StartNew();
        while (GetRawField(window, "_spritePagePrefetchTask") is not null &&
               deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            PumpDispatcher(TimeSpan.FromMilliseconds(5));
            Thread.Yield();
        }

        Assert(GetRawField(window, "_spritePagePrefetchTask") is null,
            "离散vsync测试开始前必须停止顺序预热，避免测试同步装载与后台解码竞争");
    }

    private static void CleanupProductionClipClockSimulation(MainWindow window)
    {
        GetField<DispatcherTimer>(window, "_automaticTimer").Stop();
        SetField(window, "_activeClip", null);
        SetField(window, "_activeFrameIndex", -1);
        SetField(window, "_activeClipStartedTimestamp", 0L);
        SetField(window, "_activeFrameDeadlineTimestamp", 0L);
        SetField(window, "_synchronizeActiveClipToRenderingCadence", false);
        SetField(window, "_lastVisualRenderingTime", TimeSpan.MinValue);
        Invoke(window, "ClearDeferredActiveClipClock");
        Invoke(window, "StopVisualClock");
        Invoke(window, "TrimResidentSpritePagesToBudget", (object?)null);
        AssertResidentSpriteCacheAccounting(window, "离散vsync片段清理");
    }

    private static void RestoreIdleAfterClipClockSimulation(MainWindow window)
    {
        CleanupProductionClipClockSimulation(window);
        var idleFrame = GetField<object>(window, "_idleFrame");
        PrimeSpritePageForFrame(window, idleFrame);
        SetField(window, "_nextFrameBlendDuration", TimeSpan.Zero);
        Invoke(window, "ShowStableFrame", idleFrame);
    }

    private static void PrimeAllClipPagesForTest(MainWindow window, Array frames)
    {
        var primedPageNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in frames.Cast<object>())
        {
            var spriteFrame = GetProperty<object>(frame, "Image");
            if (primedPageNames.Add(GetSpriteFrameInfo(spriteFrame).PageName))
            {
                PrimeSpritePageForFrame(window, spriteFrame);
            }
        }
    }

    private static int GetDisplayedClipFrameIndex(
        MainWindow window,
        object clip,
        Array frames)
    {
        if (!ReferenceEquals(GetRawField(window, "_activeClip"), clip))
        {
            return frames.Length;
        }

        return GetField<int>(window, "_activeFrameIndex");
    }

    private static void AssertDisplayedClipFrame(
        MainWindow window,
        Array frames,
        int displayedFrameIndex)
    {
        var expectedFrameIndex = Math.Min(displayedFrameIndex, frames.Length - 1);
        var expectedSpriteFrame = GetProperty<object>(
            frames.GetValue(expectedFrameIndex)!,
            "Image");
        Assert(Equals(GetRawField(window, "_currentSpriteFrame"), expectedSpriteFrame),
            $"生产AdvanceActiveClip索引{displayedFrameIndex}必须与实际显示的SpriteFrame一致");
    }

    private static int ResolveAbsoluteFrameIndexAtStopwatchTicks(
        IReadOnlyList<long> holdTicks,
        long elapsedStopwatchTicks,
        long deadlineToleranceTicks)
    {
        var deadline = 0L;
        for (var frameIndex = 0; frameIndex < holdTicks.Count; frameIndex++)
        {
            deadline = checked(deadline + holdTicks[frameIndex]);
            if (elapsedStopwatchTicks < deadline - deadlineToleranceTicks)
            {
                return frameIndex;
            }
        }

        return holdTicks.Count;
    }

    private static int ResolveFrameIndexFromProductionDeadline(
        IReadOnlyList<long> holdTicks,
        int currentFrameIndex,
        long currentDeadline,
        long timestamp,
        long deadlineToleranceTicks)
    {
        var resolvedFrameIndex = currentFrameIndex;
        var deadline = currentDeadline;
        while (timestamp >= deadline - deadlineToleranceTicks)
        {
            var nextFrameIndex = resolvedFrameIndex + 1;
            if (nextFrameIndex >= holdTicks.Count)
            {
                return holdTicks.Count;
            }

            resolvedFrameIndex = nextFrameIndex;
            deadline = checked(deadline + holdTicks[nextFrameIndex]);
        }

        return resolvedFrameIndex;
    }

    private static int ResolveProductionClipFrameIndex(
        MainWindow window,
        object clip,
        long elapsedTicks)
    {
        var frames = GetClipFrames(clip);
        if (frames.Length == 0)
        {
            return 0;
        }

        var firstFrame = frames.GetValue(0)!;
        var firstSpriteFrame = GetProperty<object>(firstFrame, "Image");
        PrimeAllClipPagesForTest(window, frames);
        PrimeSpritePageForFrame(window, firstSpriteFrame);
        Invoke(window, "ShowStableFrame", firstSpriteFrame);
        Invoke(window, "ClearDeferredActiveClipClock");

        var startedAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        var firstHoldTicks = ToProductionCharacterAnimationTicks(
            GetFrameDuration(firstFrame));
        SetField(window, "_activeClip", clip);
        SetField(window, "_activeFrameIndex", 0);
        SetField(window, "_activeClipStartedTimestamp", startedAt);
        SetField(window, "_activeFrameDeadlineTimestamp", startedAt + firstHoldTicks);
        Invoke(window, "AdvanceActiveClip", startedAt + Math.Max(0, elapsedTicks));

        var resolvedIndex = GetField<int>(window, "_activeFrameIndex");
        if (resolvedIndex < 0)
        {
            resolvedIndex = frames.Length;
        }

        SetField(window, "_activeClip", null);
        SetField(window, "_activeFrameIndex", -1);
        SetField(window, "_activeClipStartedTimestamp", 0L);
        SetField(window, "_activeFrameDeadlineTimestamp", 0L);
        Invoke(window, "ClearDeferredActiveClipClock");
        Invoke(window, "UpdateVisualClockSubscription");
        return resolvedIndex;
    }

    private static void AssertMainRenderingTimeDedupContract(MainWindow window)
    {
        var clip = GetField<Array>(window, "_reactionClips").GetValue(0)!;
        var frames = GetClipFrames(clip);
        var firstHoldTicks = ToProductionCharacterAnimationTicks(
            GetFrameDuration(frames.GetValue(0)!));
        foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
        {
            var startedAt = Stopwatch.GetTimestamp();
            PrepareProductionClipClockSimulation(
                window,
                clip,
                frames,
                startedAt,
                firstHoldTicks);
            SetField(window, "_activeFrameDeadlineTimestamp", long.MaxValue);
            SetField(window, "_lastVisualRenderingTime", TimeSpan.MinValue);
            var renderingTime = TimeSpan.FromSeconds(10);
            Invoke(
                window,
                "VisualClock_Rendering",
                null,
                CreateRenderingEventArgs(renderingTime));
            Assert(GetField<int>(window, "_activeFrameIndex") == 0,
                $"{refreshRate:F2}Hz VisualClock基准回调不得在首个RenderingTime提前推进");

            SetField(
                window,
                "_activeFrameDeadlineTimestamp",
                Stopwatch.GetTimestamp() - firstHoldTicks * 2);
            Invoke(
                window,
                "VisualClock_Rendering",
                null,
                CreateRenderingEventArgs(renderingTime));
            Assert(GetField<int>(window, "_activeFrameIndex") == 0,
                $"{refreshRate:F2}Hz相同RenderingTime重复回调必须被VisualClock去重");

            Invoke(
                window,
                "VisualClock_Rendering",
                null,
                CreateRenderingEventArgs(
                    renderingTime + TimeSpan.FromSeconds(1d / refreshRate)));
            var displayedFrameIndex = GetField<int>(window, "_activeFrameIndex");
            Assert(displayedFrameIndex >= 2,
                $"{refreshRate:F0}Hz真实VisualClock不得把1.25倍动画锁成一回调一姿势；" +
                "过期deadline应在一次回调直接定位最终姿势");

            AssertDisplayedClipFrame(window, frames, displayedFrameIndex);
            CleanupProductionClipClockSimulation(window);
        }

        RestoreIdleAfterClipClockSimulation(window);
    }

    private static void AssertRenderingCadenceClassificationContract(
        string mainWindowSource)
    {
        foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
        {
            var shouldLock = (bool)InvokeStatic(
                typeof(MainWindow),
                "ShouldSynchronizeActiveClipToRenderingCadence",
                TimeSpan.FromSeconds(1d / refreshRate))!;
            Assert(!shouldLock,
                $"{refreshRate:F0}Hz不得把1.25倍代码速度误锁成一回调一姿势，" +
                "必须保持ToCharacterAnimationTicks绝对时间轴");
        }

        foreach (var refreshRate in new[] { 59d, 59.94d, 60d })
        {
            var shouldSynchronizeEdgePeek = (bool)InvokeStatic(
                typeof(MainWindow),
                "ShouldSynchronizeEdgePeekToRenderingCadence",
                TimeSpan.FromSeconds(1d / refreshRate))!;
            Assert(shouldSynchronizeEdgePeek,
                $"{refreshRate:F2}Hz必须启用边缘探头逐合成同步，避免原生60fps姿势漏帧");
        }

        foreach (var refreshRate in new[] { 120d, 144d })
        {
            var shouldSynchronizeEdgePeek = (bool)InvokeStatic(
                typeof(MainWindow),
                "ShouldSynchronizeEdgePeekToRenderingCadence",
                TimeSpan.FromSeconds(1d / refreshRate))!;
            Assert(!shouldSynchronizeEdgePeek,
                $"{refreshRate:F0}Hz边缘探头必须保留绝对deadline模型");
        }

        var shouldSynchronizeAfterGap = (bool)InvokeStatic(
            typeof(MainWindow),
            "ShouldSynchronizeEdgePeekToRenderingCadence",
            TimeSpan.FromMilliseconds(250))!;
        Assert(!shouldSynchronizeAfterGap,
            "250ms合成gap不得误判为健康近60Hz节拍");

        var edgeCadenceHelperSource = ExtractPrivateMethodSource(
            mainWindowSource,
            "ShouldSynchronizeEdgePeekToRenderingCadence");
        Assert(!edgeCadenceHelperSource.Contains(
                   "AnimationPlaybackSpeed",
                   StringComparison.Ordinal),
            "边缘探头节拍分类必须独立于全局点击动作AnimationPlaybackSpeed");
    }

    private static int ResolveAbsoluteFrameIndex(IReadOnlyList<TimeSpan> durations, TimeSpan elapsed)
    {
        var cursor = TimeSpan.Zero;
        for (var index = 0; index < durations.Count; index++)
        {
            cursor += durations[index];
            if (elapsed < cursor)
            {
                return index;
            }
        }

        return durations.Count;
    }

    private static double SnapForSimulation(double value, double dpiScale) =>
        Math.Round(value * dpiScale, MidpointRounding.AwayFromZero) / dpiScale;

    private static double IntegrateBoundedMovement(double elapsedSeconds, double speed) =>
        elapsedSeconds is > 0 and <= 0.250 ? elapsedSeconds * speed : 0;

    private static long StopwatchTicksFromMilliseconds(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000d);

    private static long ToProductionCharacterAnimationTicks(TimeSpan baseDuration) =>
        (long)(InvokeStatic(
            typeof(MainWindow),
            "ToCharacterAnimationTicks",
            baseDuration) ?? throw new InvalidOperationException(
            "生产ToCharacterAnimationTicks未返回运行时截止点"));

    private static long ToProductionStopwatchTicks(TimeSpan duration) =>
        (long)(InvokeStatic(
            typeof(MainWindow),
            "ToStopwatchTicks",
            duration) ?? throw new InvalidOperationException(
            "生产ToStopwatchTicks未返回运行时截止点"));

    private static double StopwatchTicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;

    private static void AssertEdgeRoamingRouteMathContract()
    {
        // A negative-coordinate secondary monitor exercises the same geometry
        // used by per-monitor DPI layouts without requiring a physical second
        // display on the test machine.
        var routeBounds = new Rect(-2548, -168, 2346, 1174);
        var radius = (double)InvokeStatic(
            typeof(MainWindow),
            "GetEdgeRoamCornerRadius",
            routeBounds)!;
        var routeLength = (double)InvokeStatic(
            typeof(MainWindow),
            "GetEdgeRoamRouteLength",
            routeBounds,
            radius)!;
        var horizontal = routeBounds.Width - radius * 2;
        var vertical = routeBounds.Height - radius * 2;
        var quarterArc = Math.PI * radius / 2;
        Assert(radius > 0 &&
               radius <= Math.Min(routeBounds.Width, routeBounds.Height) / 2,
            $"熊猫坐骑圆角半径必须适配当前副屏工作区，实际 {radius:F3}");
        AssertClose(
            routeLength,
            horizontal * 2 + vertical * 2 + Math.PI * radius * 2,
            "熊猫坐骑完整一圈必须覆盖四条直线和四个圆角且只计算一次");

        var checkpoints = new[]
        {
            (
                Distance: 0d,
                Expected: new Point(routeBounds.Left + radius, routeBounds.Top),
                Stage: "顶部左侧起点"),
            (
                Distance: horizontal,
                Expected: new Point(routeBounds.Right - radius, routeBounds.Top),
                Stage: "顶部右圆角入口"),
            (
                Distance: horizontal + quarterArc,
                Expected: new Point(routeBounds.Right, routeBounds.Top + radius),
                Stage: "右边直线入口"),
            (
                Distance: horizontal + quarterArc + vertical,
                Expected: new Point(routeBounds.Right, routeBounds.Bottom - radius),
                Stage: "右下圆角入口"),
            (
                Distance: horizontal + quarterArc * 2 + vertical,
                Expected: new Point(routeBounds.Right - radius, routeBounds.Bottom),
                Stage: "底边右侧入口"),
            (
                Distance: horizontal * 2 + quarterArc * 2 + vertical,
                Expected: new Point(routeBounds.Left + radius, routeBounds.Bottom),
                Stage: "左下圆角入口"),
            (
                Distance: horizontal * 2 + quarterArc * 3 + vertical,
                Expected: new Point(routeBounds.Left, routeBounds.Bottom - radius),
                Stage: "左边直线入口"),
            (
                Distance: horizontal * 2 + quarterArc * 3 + vertical * 2,
                Expected: new Point(routeBounds.Left, routeBounds.Top + radius),
                Stage: "左上圆角入口")
        };
        foreach (var checkpoint in checkpoints)
        {
            var actual = (Point)InvokeStatic(
                typeof(MainWindow),
                "GetEdgeRoamRoutePoint",
                routeBounds,
                radius,
                checkpoint.Distance)!;
            AssertClose(actual.X, checkpoint.Expected.X, $"{checkpoint.Stage} X");
            AssertClose(actual.Y, checkpoint.Expected.Y, $"{checkpoint.Stage} Y");
            Assert(actual.X >= routeBounds.Left - 0.01 &&
                   actual.X <= routeBounds.Right + 0.01 &&
                   actual.Y >= routeBounds.Top - 0.01 &&
                   actual.Y <= routeBounds.Bottom + 0.01,
                $"{checkpoint.Stage}不得离开当前显示器独立WorkArea路线");
        }

        var start = (Point)InvokeStatic(
            typeof(MainWindow),
            "GetEdgeRoamRoutePoint",
            routeBounds,
            radius,
            0d)!;
        var completedLap = (Point)InvokeStatic(
            typeof(MainWindow),
            "GetEdgeRoamRoutePoint",
            routeBounds,
            radius,
            routeLength)!;
        var reverseLap = (Point)InvokeStatic(
            typeof(MainWindow),
            "GetEdgeRoamRoutePoint",
            routeBounds,
            radius,
            -routeLength)!;
        AssertClose(completedLap.X, start.X, "顺时针整圈必须回到同一逻辑X");
        AssertClose(completedLap.Y, start.Y, "顺时针整圈必须回到同一逻辑Y");
        AssertClose(reverseLap.X, start.X, "逆时针整圈必须回到同一逻辑X");
        AssertClose(reverseLap.Y, start.Y, "逆时针整圈必须回到同一逻辑Y");

        var topMiddle = new Point(
            routeBounds.Left + radius + horizontal / 2,
            routeBounds.Top);
        var rightMiddle = new Point(
            routeBounds.Right,
            routeBounds.Top + radius + vertical / 2);
        var bottomMiddle = new Point(
            routeBounds.Left + radius + horizontal / 2,
            routeBounds.Bottom);
        var leftMiddle = new Point(
            routeBounds.Left,
            routeBounds.Top + radius + vertical / 2);
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                topMiddle,
                new Point(topMiddle.X + 2, topMiddle.Y),
                routeBounds,
                radius),
            -1,
            "顶部向右移动时左朝向原画必须镜像");
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                topMiddle,
                new Point(topMiddle.X - 2, topMiddle.Y),
                routeBounds,
                radius),
            1,
            "顶部向左移动时左朝向原画必须保持原向");
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                bottomMiddle,
                new Point(bottomMiddle.X - 2, bottomMiddle.Y),
                routeBounds,
                radius),
            1,
            "底部向左移动时左朝向原画必须保持原向");
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                bottomMiddle,
                new Point(bottomMiddle.X + 2, bottomMiddle.Y),
                routeBounds,
                radius),
            -1,
            "底部向右移动时左朝向原画必须镜像");
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                leftMiddle,
                new Point(leftMiddle.X, leftMiddle.Y - 2),
                routeBounds,
                radius),
            -1,
            "左侧竖边必须始终朝屏幕内部");
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                leftMiddle,
                new Point(leftMiddle.X, leftMiddle.Y + 2),
                routeBounds,
                radius),
            -1,
            "左侧反向竖移也必须始终朝屏幕内部");
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                rightMiddle,
                new Point(rightMiddle.X, rightMiddle.Y + 2),
                routeBounds,
                radius),
            1,
            "右侧竖边必须始终朝屏幕内部");
        AssertClose(
            ResolveProductionEdgeRoamFacing(
                rightMiddle,
                new Point(rightMiddle.X, rightMiddle.Y - 2),
                routeBounds,
                radius),
            1,
            "右侧反向竖移也必须始终朝屏幕内部");
    }

    private static void AssertEdgeRoamRotationContract()
    {
        var left = new Point(-1600, 480);
        var right = new Point(0, 480);
        AssertClose(
            ResolveProductionEdgeRoamRotation(
                left,
                new Point(left.X, left.Y - 2),
                -1,
                0),
            -90,
            "左侧向上绕屏时必须旋转 -90 度");
        AssertClose(
            ResolveProductionEdgeRoamRotation(
                right,
                new Point(right.X, right.Y - 2),
                1,
                0),
            90,
            "右侧向上绕屏时必须旋转 90 度");
        AssertClose(
            ResolveProductionEdgeRoamRotation(
                left,
                new Point(left.X, left.Y + 2),
                -1,
                0),
            90,
            "左侧向下绕屏时必须旋转 90 度");
        AssertClose(
            ResolveProductionEdgeRoamRotation(
                right,
                new Point(right.X, right.Y + 2),
                1,
                0),
            -90,
            "右侧向下绕屏时必须旋转 -90 度");
        AssertClose(
            ResolveProductionEdgeRoamRotation(
                left,
                new Point(left.X + 2, left.Y),
                -1,
                90),
            0,
            "恢复横向绕屏时必须清零旋转");
    }

    private static double ResolveProductionEdgeRoamRotation(
        Point position,
        Point lookAhead,
        double facingScaleX,
        double currentRotationDegrees) =>
        (double)(InvokeStatic(
            typeof(MainWindow),
            "ResolveEdgeRoamRotationDegrees",
            position,
            lookAhead,
            facingScaleX,
            currentRotationDegrees) ?? throw new InvalidOperationException(
            "生产绕屏旋转函数未返回角度"));

    private static double ResolveProductionEdgeRoamFacing(
        Point position,
        Point lookAhead,
        Rect routeBounds,
        double radius) =>
        (double)(InvokeStatic(
            typeof(MainWindow),
            "ResolveEdgeRoamFacingScaleX",
            position,
            lookAhead,
            routeBounds,
            radius,
            1d) ?? throw new InvalidOperationException(
            "生产绕屏朝向函数未返回缩放值"));

    private static void AssertExactEdgeContactContract()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var activationDistance = (double)(typeof(MainWindow).GetField(
                "EdgeDockActivationDistance",
                StaticFlags)!.GetValue(null) ?? double.NaN);
        const double width = 190;
        const double height = 242;
        const double safeX = 500;
        const double safeY = 300;
        AssertClose(
            activationDistance,
            12,
            "手动边缘吸附必须提供 12 DIP 的可命中磁吸区，不能退回 ±1 DIP 的像素窄带");

        var cases = new[]
        {
            new EdgeCase(
                "Left",
                new Rect(activationDistance + 0.1, safeY, width, height),
                new Rect(activationDistance, safeY, width, height)),
            new EdgeCase(
                "Right",
                new Rect(
                    workArea.Right - width - activationDistance - 0.1,
                    safeY,
                    width,
                    height),
                new Rect(
                    workArea.Right - width - activationDistance,
                    safeY,
                    width,
                    height)),
            new EdgeCase(
                "Bottom",
                new Rect(
                    safeX,
                    workArea.Bottom - height - activationDistance - 0.1,
                    width,
                    height),
                new Rect(
                    safeX,
                    workArea.Bottom - height - activationDistance,
                    width,
                    height))
        };

        foreach (var edgeCase in cases)
        {
            var near = InvokeStatic(
                typeof(MainWindow),
                "FindTouchedEdge",
                workArea,
                edgeCase.NearBounds,
                activationDistance)!;
            Assert(near.ToString() == "None",
                $"{edgeCase.Edge} 超出 12 DIP 磁吸区后不得提前吸附");

            var touching = InvokeStatic(
                typeof(MainWindow),
                "FindTouchedEdge",
                workArea,
                edgeCase.TouchingBounds,
                activationDistance)!;
            Assert(touching.ToString() == edgeCase.Edge,
                $"{edgeCase.Edge} 进入 12 DIP 磁吸区时必须稳定吸附");
        }

        var crossedLeft = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(-20, safeY, width, height),
            activationDistance)!;
        var fullyOutsideLeft = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(-width - 0.1, safeY, width, height),
            activationDistance)!;
        var exactlyOutsideLeft = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(-width, safeY, width, height),
            activationDistance)!;
        var crossedRight = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(workArea.Right - width + 20, safeY, width, height),
            activationDistance)!;
        var crossedBottom = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(
                safeX,
                workArea.Bottom - height + 20,
                width,
                height),
            activationDistance)!;
        var fullyOutsideBottom = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(safeX, workArea.Bottom + 0.1, width, height),
            activationDistance)!;
        var exactlyOutsideBottom = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(safeX, workArea.Bottom, width, height),
            activationDistance)!;
        Assert(crossedLeft.ToString() == "Left" &&
               crossedRight.ToString() == "Right" &&
               crossedBottom.ToString() == "Bottom" &&
               fullyOutsideLeft.ToString() == "None" &&
               exactlyOutsideLeft.ToString() == "None" &&
               fullyOutsideBottom.ToString() == "None" &&
               exactlyOutsideBottom.ToString() == "None",
            "快速拖动越过外边缘后仍必须吸附，但人物已经完全离开工作区时不得错误夹回");

        var sharedSeamCornerCandidates = ((IEnumerable)InvokeStatic(
                typeof(MainWindow),
                "FindTouchedEdges",
                workArea,
                new Rect(
                    workArea.Right - width - 1,
                    workArea.Bottom - height - 5,
                    width,
                    height),
                activationDistance)!)
            .Cast<object>()
            .Select(candidate => candidate.ToString())
            .ToArray();
        Assert(sharedSeamCornerCandidates.Length >= 2 &&
               sharedSeamCornerCandidates[0] == "Right" &&
               sharedSeamCornerCandidates[1] == "Bottom",
            "双屏共享右缝比底边更近时必须保留 Bottom 作为后备外边缘候选");

        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var updateDock = ExtractPrivateMethodSource(
            mainSource,
            "UpdateEdgeDockAfterDrag");
        var visibleContact = ExtractPrivateMethodSource(
            mainSource,
            "GetPetContactBounds");
        var findTouchedEdge = ExtractPrivateMethodSource(
            mainSource,
            "FindTouchedEdge");
        var findTouchedEdges = ExtractPrivateMethodSource(
            mainSource,
            "FindTouchedEdges");
        var monitorSource = File.ReadAllText(
            FindWorkspaceFile("MonitorWorkArea.cs"));
        var externalEdge = ExtractPrivateMethodSource(
            monitorSource,
            "IsExternalWorkAreaEdgeAt");
        Assert(updateDock.Contains(
                   "GetPetContactBounds(windowBounds)",
                   StringComparison.Ordinal) &&
               updateDock.Contains(
                   "EdgeDockActivationDistance",
                   StringComparison.Ordinal) &&
               updateDock.Contains(
                   "foreach (var candidate in FindTouchedEdges(",
                   StringComparison.Ordinal) &&
               updateDock.Contains(
                   "IsExternalWorkAreaEdgeAt(",
                   StringComparison.Ordinal) &&
               updateDock.Contains(
                   "continue;",
                   StringComparison.Ordinal) &&
               findTouchedEdges.Contains(
                   "candidate.Gap <= activationDistance",
                   StringComparison.Ordinal) &&
               findTouchedEdges.Contains(
                   "candidate.StillVisible",
                   StringComparison.Ordinal) &&
               !findTouchedEdges.Contains(
                   "Math.Abs(candidate.Gap) <= activationDistance",
                   StringComparison.Ordinal) &&
               findTouchedEdge.Contains(
                   ".DefaultIfEmpty(EdgeDock.None)",
                   StringComparison.Ordinal) &&
               visibleContact.Contains(
                   "frame.DestinationX",
                   StringComparison.Ordinal) &&
               visibleContact.Contains(
                   "frame.DestinationY",
                   StringComparison.Ordinal) &&
               externalEdge.Contains(
                   "EnumDisplayMonitors(",
                   StringComparison.Ordinal) &&
               externalEdge.Contains(
                   "hasAdjacentMonitor",
                   StringComparison.Ordinal) &&
               externalEdge.Contains(
                   "return !hasAdjacentMonitor",
                   StringComparison.Ordinal),
            "边缘接触必须使用精灵可见像素边界，并在共享双屏接缝处拒绝假吸附");

        var topCenter = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(safeX, 0, width, height),
            activationDistance)!;
        Assert(topCenter.ToString() == "None",
            "顶部中心即使完全接触屏幕边缘也不得吸附或进入探头状态");

        var topLeftCorner = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(0, 0, width, height),
            activationDistance)!;
        var topRightCorner = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(workArea.Right - width, 0, width, height),
            activationDistance)!;
        Assert(topLeftCorner.ToString() == "Left" &&
               topRightCorner.ToString() == "Right",
            "顶部左右角仍应分别保留左、右吸附");

        var dragMoveSource = ExtractPrivateMethodSource(
            mainSource,
            "PetHost_MouseMove");
        var dragMoveCall = dragMoveSource.IndexOf(
            "DragMove();",
            StringComparison.Ordinal);
        var dragMoveCatch = dragMoveSource.IndexOf(
            "catch (InvalidOperationException)",
            StringComparison.Ordinal);
        var finalDockCheck = dragMoveSource.IndexOf(
            "UpdateEdgeDockAfterDrag();",
            StringComparison.Ordinal);
        Assert(dragMoveCall >= 0 &&
               dragMoveCatch > dragMoveCall &&
               finalDockCheck > dragMoveCatch &&
               dragMoveSource.Contains(
                   "finally",
                   StringComparison.Ordinal),
            "系统 DragMove 快速松手或异常返回后仍必须在统一结束路径补做边缘吸附判定");
    }

    private static void AssertSupportedEdgeDockIntegration(MainWindow window)
    {
        var edgeMotionFrameInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgePeekMotionFrameInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var edgeFullyPeekedHold = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgePeekFullyPeekedHold",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var edgeRestHold = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgePeekRestHold",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var edgeBlendDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgeFrameBlendDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.MinValue);
        Assert(edgeMotionFrameInterval == TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60) &&
               edgeFullyPeekedHold == TimeSpan.FromMilliseconds(650) &&
               edgeRestHold == TimeSpan.FromMilliseconds(800) &&
               edgeBlendDuration == TimeSpan.Zero &&
               typeof(MainWindow).GetField("EdgePeekFrameInterval", StaticFlags) is null &&
               typeof(MainWindow).GetField("_edgePeekFrameDirection", InstanceFlags) is null,
            "边缘探头必须使用不受全局倍速影响的精确60fps间隔、650ms开心探头、800ms缩回休息、禁用整图淡化，" +
            "并彻底删除70ms及ping-pong方向状态");

        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        var monitorType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.MonitorWorkArea",
            throwOnError: true)!;
        AssertWindowBoundaryDockActivation(window, monitorType);
        var workArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        var safeLeft = workArea.Left + Math.Max(20, (workArea.Width - width) / 2);
        var safeTop = workArea.Top + Math.Max(20, (workArea.Height - height) / 2);

        foreach (var edge in new[] { "Left", "Right", "Bottom" })
        {
            var edgeFrames = edge is "Left" or "Right"
                ? GetField<Array>(window, "_edgeLeftFrames")
                : GetField<Array>(window, "_edgeBottomFrames");
            var restFrameIndex = edgeFrames.Length - 1;
            var fullyPeekedFrameIndex = edgeFrames.Length / 2 - 1;
            PrimeSpritePageForFrame(window, edgeFrames.GetValue(restFrameIndex)!);
            window.Left = safeLeft;
            window.Top = safeTop;
            var initialWindowBounds = new Rect(
                window.Left,
                window.Top,
                width,
                height);
            var initialContactBounds = (Rect)Invoke(
                window,
                "GetPetContactBounds",
                initialWindowBounds)!;
            if (edge == "Left")
            {
                window.Left += workArea.Left - initialContactBounds.Left;
            }
            else if (edge == "Right")
            {
                window.Left += workArea.Right - initialContactBounds.Right;
            }
            else
            {
                window.Top += workArea.Bottom - initialContactBounds.Bottom;
            }
            Invoke(window, "UpdateEdgeDockAfterDrag");
            var deadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            Assert(GetField<object>(window, "_edgeDock").ToString() == edge &&
                   GetField<int>(window, "_edgePeekFrameIndex") == restFrameIndex &&
                   Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       edgeFrames.GetValue(restFrameIndex)) &&
                   deadline > Stopwatch.GetTimestamp() &&
                   deadline != long.MaxValue &&
                   GetField<ScaleTransform>(window, "PetFacingScale").ScaleX ==
                   (edge == "Right" ? -1 : 1),
                $"真实拖拽落点贴住{edge}边缘时必须从末尾休息姿势进入；右侧仅镜像左侧序列");

            Invoke(window, "AdvanceEdgePeek", deadline);
            var nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            Assert(GetField<int>(window, "_edgePeekFrameIndex") == 0,
                $"{edge} 探头离开末帧后必须闭环到第001帧，不能反向播放");
            AssertClose(
                StopwatchTicksToMilliseconds(nextDeadline - deadline),
                StopwatchTicksToMilliseconds(
                    ToProductionStopwatchTicks(edgeMotionFrameInterval)),
                $"{edge} 探头离开休息姿势后必须按原生60fps运行时钟换帧");

            while (GetField<int>(window, "_edgePeekFrameIndex") !=
                   fullyPeekedFrameIndex)
            {
                deadline = nextDeadline;
                Invoke(window, "AdvanceEdgePeek", deadline);
                nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
                var frameIndex = GetField<int>(window, "_edgePeekFrameIndex");
                var expectedHold = frameIndex == fullyPeekedFrameIndex
                    ? edgeFullyPeekedHold
                    : edgeMotionFrameInterval;
                AssertClose(
                    StopwatchTicksToMilliseconds(nextDeadline - deadline),
                    StopwatchTicksToMilliseconds(
                        ToProductionStopwatchTicks(expectedHold)),
                    $"{edge} 探头升序姿势 {frameIndex + 1:000} 的hold必须匹配动态四阶段时钟");
            }

            Assert(Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       edgeFrames.GetValue(fullyPeekedFrameIndex)),
                $"{edge} 探头必须在1/2阶段显示开心完全探头姿势并停留650ms");
            while (GetField<int>(window, "_edgePeekFrameIndex") != restFrameIndex)
            {
                deadline = nextDeadline;
                Invoke(window, "AdvanceEdgePeek", deadline);
                nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            }

            AssertClose(
                StopwatchTicksToMilliseconds(nextDeadline - deadline),
                StopwatchTicksToMilliseconds(
                    ToProductionStopwatchTicks(edgeRestHold)),
                $"{edge} 探头回到末尾害羞缩回休息姿势后必须独立停留800ms");
            deadline = nextDeadline;
            Invoke(window, "AdvanceEdgePeek", deadline);
            nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            Assert(GetField<int>(window, "_edgePeekFrameIndex") == 0,
                $"{edge} 探头末帧必须单向闭环回第001帧，不能ping-pong");
            AssertClose(
                StopwatchTicksToMilliseconds(nextDeadline - deadline),
                StopwatchTicksToMilliseconds(
                    ToProductionStopwatchTicks(edgeMotionFrameInterval)),
                $"{edge} 探头新一轮必须继续保持原生60fps绝对时钟");
            Invoke(window, "ExitEdgePeek", false, true);
            Assert(GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0,
                $"退出{edge}探头后必须清除绝对时间截止点");
        }

        window.Left = safeLeft;
        window.Top = workArea.Top;
        var frameBeforeTopContact = GetRawField(window, "_currentSpriteFrame");
        Invoke(window, "UpdateEdgeDockAfterDrag");
        Assert(GetField<object>(window, "_edgeDock").ToString() == "None" &&
               GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0 &&
               Equals(GetRawField(window, "_currentSpriteFrame"), frameBeforeTopContact) &&
               !string.Equals(
                   GetRawField(window, "_desiredSpritePageName") as string,
                   "edge-top",
                   StringComparison.Ordinal),
            "拖到顶部中心松手后必须保持普通状态，不得吸附、换帧或请求edge-top分页");
        window.Top = safeTop;
    }

    private static void AssertTodoClosePreservesEdgePeek(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        var todoWindow = GetField<TodoWindow>(window, "_todoWindow");
        var originalSuppressDeactivate =
            GetField<bool>(window, "_suppressTodoWindowDeactivate");
        var originalLeft = window.Left;
        var originalTop = window.Top;
        var processOutsideClose = ExtractPrivateMethodSource(
            File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs")),
            "ProcessOutsideTodoClose");
        Assert(processOutsideClose.Contains(
                "SetBubbleMode(BubbleMode.None)",
                StringComparison.Ordinal),
            "外部点击收起必须继续汇入统一的 Todo→None 状态切换，不能绕过待办提交与隐藏逻辑");

        try
        {
            SetField(window, "_suppressTodoWindowDeactivate", true);
            Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "ExitEdgePeek", false, true);

            foreach (var edge in new[] { "Left", "Right", "Bottom" })
            {
                var edgeFrames = edge is "Left" or "Right"
                    ? GetField<Array>(window, "_edgeLeftFrames")
                    : GetField<Array>(window, "_edgeBottomFrames");
                var restFrame = edgeFrames.GetValue(edgeFrames.Length - 1)!;

                Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                Assert(todoWindow.IsVisible &&
                       GetField<object>(window, "_bubbleMode").ToString() == "Todo",
                    $"{edge} 吸附回归的前置条件必须真实打开待办窗口");

                // Opening Todo can trim an unprotected edge page after earlier
                // cache-pressure checks. Pin the exact entry pose only after
                // the panel has completed that state transition.
                PrimeSpritePageForFrame(window, restFrame);
                Invoke(window, "EnterEdgePeek", GetNestedEnum("EdgeDock", edge));
                var deadlineBeforeClose =
                    GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
                Assert(GetField<object>(window, "_edgeDock").ToString() == edge &&
                       deadlineBeforeClose > Stopwatch.GetTimestamp() &&
                       deadlineBeforeClose != long.MaxValue &&
                       GetRawField(window, "_activeClip") is null &&
                       Equals(GetRawField(window, "_currentSpriteFrame"), restFrame),
                    $"待办打开时进入 {edge} 吸附必须把视觉所有权交给有效的边缘动画");

                // ProcessOutsideTodoClose、关闭按钮、Alt+F4 和右键切换最终
                // 都调用这一状态切换。直接验证共用终点可避免 CI 焦点时序噪声。
                Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "None"));
                PumpDispatcher(TimeSpan.FromMilliseconds(10));

                var preservedDeadline =
                    GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
                var facingScale = GetField<ScaleTransform>(window, "PetFacingScale");
                Assert(!todoWindow.IsVisible &&
                       GetField<object>(window, "_bubbleMode").ToString() == "None" &&
                       GetField<object>(window, "_edgeDock").ToString() == edge &&
                       preservedDeadline > 0 &&
                       preservedDeadline != long.MaxValue &&
                       GetRawField(window, "_activeClip") is null &&
                       GetField<long>(window, "_activeFrameDeadlineTimestamp") == 0 &&
                       edgeFrames.Cast<object>().Contains(
                           GetRawField(window, "_currentSpriteFrame")) &&
                       GetField<bool>(window, "_isVisualClockSubscribed") &&
                       !GetField<DispatcherTimer>(window, "_automaticTimer").IsEnabled &&
                       Math.Abs(
                           facingScale.ScaleX - (edge == "Right" ? -1 : 1)) <=
                       0.000001,
                    $"点击外部收起待办后必须保留 {edge} 吸附、朝向、边缘帧和视觉时钟，不能播放 todo-close 回待机");

                Invoke(window, "AdvanceEdgePeek", preservedDeadline);
                Assert(GetField<object>(window, "_edgeDock").ToString() == edge &&
                       GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") >
                       preservedDeadline &&
                       edgeFrames.Cast<object>().Contains(
                           GetRawField(window, "_currentSpriteFrame")),
                    $"收起待办后 {edge} 边缘动画必须继续推进，不能只留下僵死的吸附枚举");

                Invoke(window, "ExitEdgePeek", false, true);
            }
        }
        finally
        {
            Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "ExitEdgePeek", false, true);
            SetField(
                window,
                "_suppressTodoWindowDeactivate",
                originalSuppressDeactivate);
            window.Left = originalLeft;
            window.Top = originalTop;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
        }
    }

    private static void AssertWindowBoundaryDockActivation(
        MainWindow window,
        Type monitorType)
    {
        var originalScale = GetField<double>(window, "_petSizeScale");
        var originalLeft = window.Left;
        var originalTop = window.Top;
        var edgeLeftFrames = GetField<Array>(window, "_edgeLeftFrames");
        var edgeBottomFrames = GetField<Array>(window, "_edgeBottomFrames");
        PrimeSpritePageForFrame(
            window,
            edgeLeftFrames.GetValue(edgeLeftFrames.Length - 1)!);
        PrimeSpritePageForFrame(
            window,
            edgeBottomFrames.GetValue(edgeBottomFrames.Length - 1)!);

        try
        {
            foreach (var scale in new[] { 0.75d, 1d, 1.25d, 1.4d })
            {
                Invoke(window, "ApplyPetSizeScale", scale, false, false);
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                window.UpdateLayout();
                var workArea = (Rect)InvokeStatic(
                    monitorType,
                    "GetForWindow",
                    window)!;
                var width = window.ActualWidth;
                var height = window.ActualHeight;
                var safeLeft =
                    workArea.Left + Math.Max(20, (workArea.Width - width) / 2);
                var safeTop =
                    workArea.Top + Math.Max(20, (workArea.Height - height) / 2);

                foreach (var edge in new[] { "Left", "Right", "Bottom" })
                {
                    Invoke(window, "ExitEdgePeek", false, true);
                    window.Left = edge switch
                    {
                        "Left" => workArea.Left,
                        "Right" => workArea.Right - width,
                        _ => safeLeft
                    };
                    window.Top = edge == "Bottom"
                        ? workArea.Bottom - height
                        : safeTop;
                    PumpDispatcher(TimeSpan.FromMilliseconds(10));

                    var contactBounds = (Rect)Invoke(
                        window,
                        "GetPetContactBounds",
                        new Rect(window.Left, window.Top, width, height))!;
                    var visibleGap = edge switch
                    {
                        "Left" => contactBounds.Left - workArea.Left,
                        "Right" => workArea.Right - contactBounds.Right,
                        _ => workArea.Bottom - contactBounds.Bottom
                    };
                    Assert(visibleGap > 1 && visibleGap <= 12,
                        $"{scale:P0} 桌宠的 {edge} 可见边距应复现旧版超过 1 DIP、" +
                        $"但落在 12 DIP 磁吸区内的真实窗口贴边场景；gap={visibleGap:F3}");

                    Invoke(window, "UpdateEdgeDockAfterDrag");
                    var snappedToBoundary = edge switch
                    {
                        "Left" => Math.Abs(window.Left - workArea.Left) <= 0.5,
                        "Right" => Math.Abs(
                            window.Left + window.ActualWidth -
                            workArea.Right) <= 0.5,
                        _ => Math.Abs(
                            window.Top + window.ActualHeight -
                            workArea.Bottom) <= 0.5
                    };
                    Assert(GetField<object>(window, "_edgeDock").ToString() == edge &&
                           snappedToBoundary,
                        $"{scale:P0} 桌宠 HWND 自然贴住 {edge} 边缘时必须吸附，" +
                        $"不能要求用户精确拖出屏幕 2～4 DIP");
                }

                var overshoot = Math.Min(40, Math.Min(width, height) / 3);
                foreach (var edge in new[] { "Left", "Right", "Bottom" })
                {
                    Invoke(window, "ExitEdgePeek", false, true);
                    window.Left = edge switch
                    {
                        "Left" => workArea.Left - overshoot,
                        "Right" => workArea.Right - width + overshoot,
                        _ => safeLeft
                    };
                    window.Top = edge == "Bottom"
                        ? workArea.Bottom - height + overshoot
                        : safeTop;
                    PumpDispatcher(TimeSpan.FromMilliseconds(10));
                    Invoke(window, "UpdateEdgeDockAfterDrag");
                    var snappedToBoundary = edge switch
                    {
                        "Left" => Math.Abs(window.Left - workArea.Left) <= 0.5,
                        "Right" => Math.Abs(
                            window.Left + window.ActualWidth -
                            workArea.Right) <= 0.5,
                        _ => Math.Abs(
                            window.Top + window.ActualHeight -
                            workArea.Bottom) <= 0.5
                    };
                    Assert(GetField<object>(window, "_edgeDock").ToString() == edge &&
                           snappedToBoundary,
                        $"{scale:P0} 桌宠快速越过 {edge} 外边缘后仍必须吸附并夹回准确边界");
                }
            }
        }
        finally
        {
            Invoke(window, "ExitEdgePeek", false, true);
            Invoke(window, "ApplyPetSizeScale", originalScale, false, false);
            window.Left = originalLeft;
            window.Top = originalTop;
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
        }
    }

    private static void AssertAutomaticDeadlineContract(MainWindow window)
    {
        var automaticInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "AutomaticAnimationInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var roamInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgeRoamInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var timer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var timerWasEnabled = timer.IsEnabled;
        var originalTimerInterval = timer.Interval;
        var originalClosing = GetField<bool>(window, "_isClosing");
        var originalSessionInactive = GetField<bool>(window, "_sessionInactive");
        var originalAutomaticEnabled = GetField<bool>(
            window,
            "_automaticAnimationEnabled");
        var originalRoamingEnabled = GetField<bool>(
            window,
            "_edgeRoamingEnabled");
        var originalReminderActive = GetField<bool>(window, "_isReminderActive");
        var originalDragActive = GetField<bool>(window, "_dragInteractionActive");
        var originalRoaming = GetField<bool>(window, "_isEdgeRoaming");
        var originalActiveClip = GetRawField(window, "_activeClip");
        var originalBubbleMode = GetRawField(window, "_bubbleMode")!;
        var originalEdgeDock = GetRawField(window, "_edgeDock")!;
        var originalActivityDue = GetField<long>(
            window,
            "_nextAutomaticActivityDueTimestamp");
        var originalRoamDue = GetField<long>(
            window,
            "_nextEdgeRoamDueTimestamp");
        var originalPillowDue = GetField<long>(
            window,
            "_pillowBreathingDueTimestamp");

        try
        {
            timer.Stop();
            SetField(window, "_isClosing", false);
            SetField(window, "_sessionInactive", false);
            SetField(window, "_automaticAnimationEnabled", true);
            SetField(window, "_edgeRoamingEnabled", true);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_dragInteractionActive", false);
            SetField(window, "_isEdgeRoaming", false);
            SetField(window, "_activeClip", null);
            SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
            SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "None"));
            SetField(window, "_pillowBreathingDueTimestamp", 0L);

            var timestamp = Stopwatch.GetTimestamp();
            var activitySentinel = checked(
                timestamp + ToProductionStopwatchTicks(TimeSpan.FromSeconds(33)));
            SetField(
                window,
                "_nextAutomaticActivityDueTimestamp",
                activitySentinel);
            Invoke(window, "ScheduleNextEdgeRoam", timestamp, roamInterval);
            Assert(GetField<long>(
                       window,
                       "_nextAutomaticActivityDueTimestamp") ==
                       activitySentinel &&
                   GetField<long>(window, "_nextEdgeRoamDueTimestamp") ==
                       checked(timestamp +
                               ToProductionStopwatchTicks(roamInterval)),
                "安排 10 分钟绕屏不得改写普通动作的独立截止时间");

            var roamSentinel = checked(
                timestamp + ToProductionStopwatchTicks(TimeSpan.FromSeconds(45)));
            SetField(window, "_nextEdgeRoamDueTimestamp", roamSentinel);
            var restartBefore = Stopwatch.GetTimestamp();
            Invoke(window, "RestartAutomaticCountdown");
            var restartAfter = Stopwatch.GetTimestamp();
            var activityDue = GetField<long>(
                window,
                "_nextAutomaticActivityDueTimestamp");
            Assert(activityDue >= checked(
                       restartBefore +
                       ToProductionStopwatchTicks(automaticInterval)) &&
                   activityDue <= checked(
                       restartAfter +
                       ToProductionStopwatchTicks(automaticInterval)) &&
                   GetField<long>(window, "_nextEdgeRoamDueTimestamp") ==
                       roamSentinel,
                "重启 1 分钟普通动作倒计时不得推迟独立的绕屏截止时间");

            SetField(
                window,
                "_nextAutomaticActivityDueTimestamp",
                checked(timestamp +
                        ToProductionStopwatchTicks(TimeSpan.FromSeconds(20))));
            SetField(
                window,
                "_nextEdgeRoamDueTimestamp",
                checked(timestamp +
                        ToProductionStopwatchTicks(TimeSpan.FromSeconds(40))));
            Invoke(window, "ArmAutomaticWakeTimer", timestamp);
            Assert(timer.IsEnabled &&
                   Math.Abs(
                       timer.Interval.TotalSeconds -
                       TimeSpan.FromSeconds(20).TotalSeconds) < 0.01,
                "共享唤醒 timer 必须选择较早的普通动作绝对截止时间");

            SetField(
                window,
                "_nextAutomaticActivityDueTimestamp",
                checked(timestamp +
                        ToProductionStopwatchTicks(TimeSpan.FromSeconds(50))));
            SetField(
                window,
                "_nextEdgeRoamDueTimestamp",
                checked(timestamp +
                        ToProductionStopwatchTicks(TimeSpan.FromSeconds(25))));
            Invoke(window, "ArmAutomaticWakeTimer", timestamp);
            Assert(timer.IsEnabled &&
                   Math.Abs(
                       timer.Interval.TotalSeconds -
                       TimeSpan.FromSeconds(25).TotalSeconds) < 0.01,
                "共享唤醒 timer 必须选择较早的绕屏绝对截止时间");
        }
        finally
        {
            timer.Stop();
            SetField(window, "_isClosing", originalClosing);
            SetField(window, "_sessionInactive", originalSessionInactive);
            SetField(
                window,
                "_automaticAnimationEnabled",
                originalAutomaticEnabled);
            SetField(window, "_edgeRoamingEnabled", originalRoamingEnabled);
            SetField(window, "_isReminderActive", originalReminderActive);
            SetField(window, "_dragInteractionActive", originalDragActive);
            SetField(window, "_isEdgeRoaming", originalRoaming);
            SetField(window, "_activeClip", originalActiveClip);
            SetField(window, "_bubbleMode", originalBubbleMode);
            SetField(window, "_edgeDock", originalEdgeDock);
            SetField(
                window,
                "_nextAutomaticActivityDueTimestamp",
                originalActivityDue);
            SetField(window, "_nextEdgeRoamDueTimestamp", originalRoamDue);
            SetField(
                window,
                "_pillowBreathingDueTimestamp",
                originalPillowDue);
            timer.Interval = originalTimerInterval;
            if (timerWasEnabled)
            {
                timer.Start();
            }
        }
    }

    private static void AssertRandomActivityBag(MainWindow window)
    {
        var activityCount = GetField<Array>(window, "_automaticActivities").Length;
        Assert(activityCount == 8, "自动活动袋应包含 7 个角色动作和 1 个待机动作");

        var firstBag = DrainActivityBag(window, activityCount);
        var secondBag = DrainActivityBag(window, activityCount);
        var expected = Enumerable.Range(0, activityCount).ToArray();
        Assert(firstBag.Order().SequenceEqual(expected),
            "随机活动袋第一轮必须完整且无重复");
        Assert(secondBag.Order().SequenceEqual(expected),
            "随机活动袋第二轮必须完整且无重复");
        Assert(secondBag[0] != firstBag[^1],
            "相邻随机袋边界不得立即重复同一活动");

        var pillowDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "PillowAnimationDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var petScale = GetField<ScaleTransform>(window, "PetScale");
        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var startPillowSource = ExtractPrivateMethodSource(
            mainSource,
            "StartPillowBreathing");
        var beginAnimationCalls = mainSource
            .Split("BeginAnimation(", StringSplitOptions.None)
            .Skip(1)
            .Select(fragment => fragment[..fragment.IndexOf(';')])
            .ToArray();
        Assert(pillowDuration == TimeSpan.FromSeconds(5) &&
               startPillowSource.Contains(
                   "_pillowBreathingDueTimestamp = checked(",
                   StringComparison.Ordinal) &&
               startPillowSource.Contains(
                   "ArmAutomaticWakeTimer(timestamp)",
                   StringComparison.Ordinal) &&
               !startPillowSource.Contains("DoubleAnimation", StringComparison.Ordinal) &&
               !startPillowSource.Contains("BeginAnimation", StringComparison.Ordinal) &&
               !mainSource.Contains("new DoubleAnimation", StringComparison.Ordinal) &&
               beginAnimationCalls.All(call => call.Contains(", null)", StringComparison.Ordinal)),
            "枕头待机必须仅用automaticTimer占位5秒；不得创建DoubleAnimation，有BeginAnimation也只能传null清理旧动画");

        Invoke(window, "StartPillowBreathing");
        Assert(GetField<bool>(window, "_isPillowBreathing") &&
               automaticTimer.IsEnabled &&
               automaticTimer.Interval > TimeSpan.Zero &&
               automaticTimer.Interval <= TimeSpan.FromSeconds(5) &&
               GetField<long>(window, "_pillowBreathingDueTimestamp") >
                   Stopwatch.GetTimestamp() &&
               !petScale.HasAnimatedProperties &&
               Math.Abs(petScale.ScaleX - 1) < 0.000001 &&
               Math.Abs(petScale.ScaleY - 1) < 0.000001,
            "枕头待机占位启动后必须只运行5秒automaticTimer，视觉缩放保持静止且零动画属性");
        Invoke(window, "StopPillowBreathing");
        Assert(!GetField<bool>(window, "_isPillowBreathing") &&
               GetField<long>(window, "_pillowBreathingDueTimestamp") == 0 &&
               !automaticTimer.IsEnabled &&
               !petScale.HasAnimatedProperties,
            "停止枕头待机占位后必须关闭automaticTimer且不遗留WPF动画");
    }

    private static void AssertSnoreBubbleAnimationContract(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        var xaml = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml"));
        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var rendering = ExtractPrivateMethodSource(
            mainSource,
            "VisualClock_Rendering");
        var updateClock = ExtractPrivateMethodSource(
            mainSource,
            "UpdateVisualClockSubscription");
        var advanceBubble = ExtractPrivateMethodSource(
            mainSource,
            "AdvanceSnoreBubble");
        var calculateScale = ExtractPrivateMethodSource(
            mainSource,
            "GetSnoreBubbleScale");
        XNamespace xamlNamespace =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        var snoreBubbleElement = XDocument.Parse(xaml)
            .Descendants()
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xamlNamespace + "Name"),
                    "SnoreBubbleHost",
                    StringComparison.Ordinal));
        var snoreBubbleAlphaValues = snoreBubbleElement
            .DescendantsAndSelf()
            .Attributes()
            .Where(attribute =>
                attribute.Name.LocalName is "Color" or "Fill" or "Stroke")
            .Select(attribute => attribute.Value)
            .Where(value => value.Length == 9 && value[0] == '#')
            .Select(value => byte.Parse(
                value.AsSpan(1, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture))
            .ToArray();
        Assert(
            xaml.Contains("x:Name=\"SnoreBubbleHost\"", StringComparison.Ordinal) &&
            xaml.Contains("x:Name=\"SnoreBubbleScale\"", StringComparison.Ordinal) &&
            xaml.Contains("RenderTransformOrigin=\"0.88,0.52\"", StringComparison.Ordinal) &&
            snoreBubbleElement
                .Descendants()
                .Count(element => element.Name.LocalName == "Ellipse") == 1 &&
            snoreBubbleAlphaValues.Length >= 4 &&
            snoreBubbleAlphaValues.All(alpha => alpha is > 0 and < byte.MaxValue) &&
            rendering.Contains("AdvanceSnoreBubble(timestamp)", StringComparison.Ordinal) &&
            updateClock.Contains("_isSnoreBubbleAnimating", StringComparison.Ordinal) &&
            calculateScale.Contains("Math.Cos", StringComparison.Ordinal) &&
            !advanceBubble.Contains("DispatcherTimer", StringComparison.Ordinal) &&
            !advanceBubble.Contains("BeginAnimation", StringComparison.Ordinal) &&
            !advanceBubble.Contains("AppLogger", StringComparison.Ordinal) &&
            !advanceBubble.Contains("LogInfo", StringComparison.Ordinal) &&
            !advanceBubble.Contains(".Width", StringComparison.Ordinal) &&
            !advanceBubble.Contains(".Height", StringComparison.Ordinal) &&
            !advanceBubble.Contains("new ", StringComparison.Ordinal),
            "呼噜泡泡必须使用独立图层和CompositionTarget绝对时钟，只改ScaleTransform且渲染帧零分配");

        var cycle = (TimeSpan)(typeof(MainWindow).GetField(
                "SnoreBubbleCycleDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var minimum = (double)(typeof(MainWindow).GetField(
                "SnoreBubbleMinimumScale",
                StaticFlags)!.GetValue(null) ?? 0d);
        var maximum = (double)(typeof(MainWindow).GetField(
                "SnoreBubbleMaximumScale",
                StaticFlags)!.GetValue(null) ?? 0d);
        Assert(cycle > TimeSpan.Zero &&
               minimum >= 1 &&
               maximum > minimum,
            "覆盖原鼻泡的独立图层必须从原大小平滑放大后再缩回");

        var samples = new[]
        {
            GetProductionSnoreBubbleScale(0),
            GetProductionSnoreBubbleScale(cycle.TotalSeconds * 0.25),
            GetProductionSnoreBubbleScale(cycle.TotalSeconds * 0.5),
            GetProductionSnoreBubbleScale(cycle.TotalSeconds * 0.75),
            GetProductionSnoreBubbleScale(cycle.TotalSeconds)
        };
        AssertClose(samples[0], minimum, "呼噜泡泡周期起点");
        AssertClose(samples[1], minimum + (maximum - minimum) / 2,
            "呼噜泡泡四分之一周期");
        AssertClose(samples[2], maximum, "呼噜泡泡半周期最大值");
        AssertClose(samples[3], samples[1], "呼噜泡泡四分之三周期");
        AssertClose(samples[4], minimum, "呼噜泡泡周期首尾连续");

        foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
        {
            const double absoluteTime = 0.731;
            var direct = GetProductionSnoreBubbleScale(absoluteTime);
            var sampledFrame = Math.Floor(absoluteTime * refreshRate);
            var resumedAtAbsoluteTime = sampledFrame / refreshRate +
                                        (absoluteTime - sampledFrame / refreshRate);
            AssertClose(
                GetProductionSnoreBubbleScale(resumedAtAbsoluteTime),
                direct,
                $"{refreshRate:F0}Hz相同绝对时间必须得到相同泡泡大小");
        }

        var beforeGap = GetProductionSnoreBubbleScale(0.9);
        var afterGap = GetProductionSnoreBubbleScale(1.15);
        Assert(Math.Abs(afterGap - beforeGap) > 0.001,
            "250ms阻塞后必须直接定位到新的绝对时钟大小，不能停留或补播积压帧");

        Invoke(window, "RefreshSnoreBubbleAnimationState");
        var petScale = GetField<ScaleTransform>(window, "PetScale");
        var bubbleScale = GetField<ScaleTransform>(window, "SnoreBubbleScale");
        var bubbleHost = GetField<FrameworkElement>(window, "SnoreBubbleHost");
        Assert(GetField<bool>(window, "_isSnoreBubbleAnimating") &&
               bubbleHost.Opacity > 0.99 &&
               !petScale.HasAnimatedProperties &&
               Math.Abs(petScale.ScaleX - 1) < 0.000001 &&
               Math.Abs(petScale.ScaleY - 1) < 0.000001,
            "稳定待机时必须只让鼻泡持续呼吸，人物和枕头尺寸保持完全不变");

        bubbleHost.UpdateLayout();
        var bubbleEllipse =
            (System.Windows.Shapes.Ellipse)VisualTreeHelper.GetChild(
                bubbleHost,
                0);
        var bubbleStroke =
            (SolidColorBrush)bubbleEllipse.Stroke;
        var bubbleFill =
            (RadialGradientBrush)bubbleEllipse.Fill;
        Assert(bubbleStroke.Color.A is > 0 and < byte.MaxValue &&
               bubbleFill.GradientStops.Count >= 3 &&
               bubbleFill.GradientStops.All(stop =>
                   stop.Color.A is > 0 and < byte.MaxValue),
            "运行时唯一呼噜泡泡的描边和全部渐变色阶都必须保持半透明");
        var viewport = GetField<FrameworkElement>(window, "PetFrameViewport");
        bubbleScale.ScaleX = minimum;
        bubbleScale.ScaleY = minimum;
        var minimumPixels = RenderPetViewport(viewport);
        bubbleScale.ScaleX = maximum;
        bubbleScale.ScaleY = maximum;
        var maximumPixels = RenderPetViewport(viewport);
        var difference = FindPbgraDifferenceBounds(
            minimumPixels,
            maximumPixels,
            RenderPixelWidth,
            RenderPixelHeight);
        Assert(difference.PixelCount >= 100 &&
               difference.Left >= 85 &&
               difference.Right <= 185 &&
               difference.Top >= 335 &&
               difference.Bottom <= 440,
            $"泡泡最小/最大实渲染差异必须只落在鼻尖附近，实际 " +
            $"{difference.Left},{difference.Top}-{difference.Right},{difference.Bottom}，" +
            $"{difference.PixelCount} pixels");

        var startTimestamp = GetField<long>(
            window,
            "_snoreBubbleAnimationStartedTimestamp");
        Invoke(
            window,
            "AdvanceSnoreBubble",
            startTimestamp + StopwatchTicksFromMilliseconds(
                cycle.TotalMilliseconds / 2));
        AssertClose(bubbleScale.ScaleX, maximum, "实际泡泡图层半周期ScaleX");
        AssertClose(bubbleScale.ScaleY, maximum, "实际泡泡图层半周期ScaleY");

        SetField(window, "_isEdgeRoaming", true);
        Invoke(window, "RefreshSnoreBubbleAnimationState");
        Assert(!GetField<bool>(window, "_isSnoreBubbleAnimating") &&
               bubbleHost.Opacity == 0 &&
               Math.Abs(bubbleScale.ScaleX - minimum) < 0.000001 &&
               Math.Abs(bubbleScale.ScaleY - minimum) < 0.000001,
            "离开待机进入绕屏时必须原子隐藏并复位泡泡层");
        SetField(window, "_isEdgeRoaming", false);
        Invoke(window, "RefreshSnoreBubbleAnimationState");
        Assert(GetField<bool>(window, "_isSnoreBubbleAnimating") &&
               bubbleHost.Opacity > 0.99,
            "回到稳定待机后泡泡必须重新从最小尺寸开始呼吸");
    }

    private static double GetProductionSnoreBubbleScale(double elapsedSeconds) =>
        (double)(InvokeStatic(
            typeof(MainWindow),
            "GetSnoreBubbleScale",
            elapsedSeconds) ?? throw new InvalidOperationException(
            "生产呼噜泡泡函数未返回缩放值"));

    private static byte[] RenderPetViewport(FrameworkElement viewport)
    {
        viewport.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            RenderPixelWidth,
            RenderPixelHeight,
            96d * RenderPixelWidth / LogicalPetWidth,
            96d * RenderPixelHeight / LogicalPetHeight,
            PixelFormats.Pbgra32);
        bitmap.Render(viewport);
        var pixels = new byte[RenderPixelWidth * RenderPixelHeight * 4];
        bitmap.CopyPixels(
            pixels,
            RenderPixelWidth * 4,
            offset: 0);
        return pixels;
    }

    private static PixelDifferenceBounds FindPbgraDifferenceBounds(
        byte[] first,
        byte[] second,
        int width,
        int height)
    {
        Assert(first.Length == second.Length &&
               first.Length == width * height * 4,
            "Pbgra实渲染差异缓冲尺寸必须一致");
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        var pixelCount = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var difference =
                    Math.Abs(first[offset] - second[offset]) +
                    Math.Abs(first[offset + 1] - second[offset + 1]) +
                    Math.Abs(first[offset + 2] - second[offset + 2]) +
                    Math.Abs(first[offset + 3] - second[offset + 3]);
                if (difference <= 8)
                {
                    continue;
                }

                pixelCount++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return new PixelDifferenceBounds(left, top, right, bottom, pixelCount);
    }

    private static int[] DrainActivityBag(MainWindow window, int count)
    {
        var result = new int[count];
        for (var index = 0; index < count; index++)
        {
            Invoke(window, "GetNextAutomaticActivity");
            result[index] = GetField<int>(window, "_lastAutomaticActivityIndex");
        }

        return result;
    }

    private static void AssertMonitorWorkAreaContract(MainWindow window)
    {
        _ = new WindowInteropHelper(window).EnsureHandle();
        var monitorType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.MonitorWorkArea",
            throwOnError: true)!;
        var workArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
        AssertValidRect(workArea, "当前显示器工作区");

        var nativeRectType = monitorType.GetNestedType("NativeRect", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 MonitorWorkArea.NativeRect");
        var nativeRect = Activator.CreateInstance(nativeRectType)!;
        SetField(nativeRect, "Left", -1920);
        SetField(nativeRect, "Top", 0);
        SetField(nativeRect, "Right", 0);
        SetField(nativeRect, "Bottom", 1040);
        var arguments = new object?[] { window, nativeRect, null };
        var converted = (bool)(monitorType.GetMethod(
                "TryConvertToWindowDips",
                StaticFlags)!
            .Invoke(null, arguments) ?? false);
        Assert(converted && arguments[2] is Rect negativeWorkArea,
            "多屏物理工作区应能转换成 WPF DIP");
        AssertValidRect(negativeWorkArea, "负坐标副屏工作区");
        Assert(negativeWorkArea.Left < 0,
            "位于主屏左侧的副屏必须保留负坐标，不能钳制到主屏");

        Assert(monitorType.GetMethod("MonitorFromWindow", StaticFlags) is not null &&
               monitorType.GetMethod("GetMonitorInfo", StaticFlags) is not null,
            "工作区解析必须基于窗口当前所在显示器及其独立 rcWork");
    }

    private static void AssertDisplaySettingsChangeRecovery(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var loadedSource = ExtractPrivateMethodSource(mainSource, "Window_Loaded");
        var closedSource = ExtractPrivateMethodSource(mainSource, "Window_Closing");
        var sessionSwitchSource = ExtractPrivateMethodSource(
            mainSource,
            "SystemEvents_SessionSwitch");
        var powerModeSource = ExtractPrivateMethodSource(
            mainSource,
            "SystemEvents_PowerModeChanged");
        var preferenceSource = ExtractPrivateMethodSource(
            mainSource,
            "SystemEvents_UserPreferenceChanged");
        var recoverySource = ExtractPrivateMethodSource(
            mainSource,
            "ProcessSystemRecovery");
        var positionerSource = File.ReadAllText(
            FindWorkspaceFile("OwnedWindowPositioner.cs"));
        var liveChildRead = positionerSource.IndexOf(
            "GetWindowRect(cache._childHandle, out var childRect)",
            StringComparison.Ordinal);
        var unchangedPositionGuard = positionerSource.IndexOf(
            "childRect.Left == desiredPosition.X",
            StringComparison.Ordinal);
        Assert(loadedSource.Contains(
                   "SystemEvents.SessionSwitch +=",
                   StringComparison.Ordinal) &&
               loadedSource.Contains(
                   "SystemEvents.PowerModeChanged +=",
                   StringComparison.Ordinal) &&
               loadedSource.Contains(
                   "SystemEvents.UserPreferenceChanged +=",
                   StringComparison.Ordinal) &&
               closedSource.Contains(
                   "SystemEvents.SessionSwitch -=",
                   StringComparison.Ordinal) &&
               closedSource.Contains(
                   "SystemEvents.PowerModeChanged -=",
                   StringComparison.Ordinal) &&
               closedSource.Contains(
                   "SystemEvents.UserPreferenceChanged -=",
                   StringComparison.Ordinal) &&
               sessionSwitchSource.Contains(
                   "QueueSystemRecovery()",
                   StringComparison.Ordinal) &&
               powerModeSource.Contains(
                   "PowerModes.Resume",
                   StringComparison.Ordinal) &&
               powerModeSource.Contains(
                   "QueueSystemRecovery()",
                   StringComparison.Ordinal) &&
               preferenceSource.Contains(
                   "QueueSystemRecovery()",
                   StringComparison.Ordinal) &&
               recoverySource.Contains(
                   "_todoWindow.RecoverAfterSystemResume()",
                   StringComparison.Ordinal) &&
               recoverySource.Contains(
                   "_todoWindowPositionCache.InvalidateGeometry()",
                   StringComparison.Ordinal) &&
               positionerSource.Contains(
                   "MonitorFromPoint(anchorCenter, MonitorDefaultToNearest)",
                   StringComparison.Ordinal) &&
               liveChildRead >= 0 &&
               unchangedPositionGuard > liveChildRead,
            "解锁、恢复、电源与桌面首选项变化必须统一恢复；待办定位每次都要读取实时 HWND 矩形，不能信任旧坐标缓存");

        SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_activeClip", null);
        SetField(window, "_isPillowBreathing", false);
        SetField(window, "_dragInteractionActive", false);
        SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));

        var monitorType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.MonitorWorkArea",
            throwOnError: true)!;
        var originalWorkArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
        window.Left = originalWorkArea.Left;
        window.Top = Math.Clamp(
            originalWorkArea.Top + 40,
            originalWorkArea.Top,
            originalWorkArea.Bottom - window.ActualHeight);
        var recoveryWindowBounds = new Rect(
            window.Left,
            window.Top,
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            window.ActualHeight > 0 ? window.ActualHeight : window.Height);
        var recoveryContactBounds = (Rect)Invoke(
            window,
            "GetPetContactBounds",
            recoveryWindowBounds)!;
        window.Left += originalWorkArea.Left - recoveryContactBounds.Left;
        Invoke(window, "UpdateEdgeDockAfterDrag");
        Assert(GetField<object>(window, "_edgeDock").ToString() == "Left",
            "显示器变化回归测试必须先进入真实手动边缘探头状态");

        window.Left = originalWorkArea.Left - originalWorkArea.Width * 3;
        window.Top = originalWorkArea.Top - originalWorkArea.Height * 3;
        Invoke(window, "SystemEvents_DisplaySettingsChanged", null, EventArgs.Empty);
        PumpDispatcher(TimeSpan.FromMilliseconds(120));

        Assert(GetField<object>(window, "_edgeDock").ToString() == "None",
            "显示器切换或断开后必须终止旧显示器的手动边缘探头状态");
        var recoveredWorkArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        Assert(window.Left >= recoveredWorkArea.Left - 0.5 &&
               window.Left <= recoveredWorkArea.Right - width + 0.5 &&
               window.Top >= recoveredWorkArea.Top - 0.5 &&
               window.Top <= recoveredWorkArea.Bottom - height + 0.5,
            "显示器切换或断开后桌宠必须被重新夹取到仍有效的工作区内");
    }

    private static void AssertOwnedTodoWindowContract(MainWindow window)
    {
        var workspace = Path.GetDirectoryName(FindWorkspaceFile("DesktopPet.csproj"))!;
        var mainSource = File.ReadAllText(Path.Combine(workspace, "MainWindow.xaml.cs"));
        var setBubbleModeSource = ExtractPrivateMethodSource(
            mainSource,
            "SetBubbleMode");
        var stopClockIndex = setBubbleModeSource.IndexOf(
            "StopVisualClock();",
            StringComparison.Ordinal);
        var showWindowIndex = setBubbleModeSource.IndexOf(
            "ShowBubbleVisuals(mode);",
            StringComparison.Ordinal);
        var enterTodoIndex = setBubbleModeSource.IndexOf(
            "EnterTodoVisualState();",
            StringComparison.Ordinal);
        Assert(stopClockIndex >= 0 &&
               showWindowIndex > stopClockIndex &&
               enterTodoIndex > showWindowIndex,
            "显示 Owned TodoWindow 前必须先停止旧视觉时钟，显示完成后再启动Todo入场，" +
            "避免Show()重入合成回调把当前动作闪到最终思考姿势");

        // 这项测试会打开真实的可激活 Owned Window。测试运行期间终端或
        // 其他进程抢焦点不应被误判为用户在待办外部点击。
        var todoWindow = GetField<TodoWindow>(window, "_todoWindow");
        var originalMainHitTestVisible = window.IsHitTestVisible;
        var originalTodoHitTestVisible = todoWindow.IsHitTestVisible;
        window.IsHitTestVisible = false;
        todoWindow.IsHitTestVisible = false;
        SetField(window, "_suppressTodoWindowDeactivate", true);
        try
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));

            Assert(ReferenceEquals(todoWindow.Owner, window),
                "待办窗口必须是 MainWindow 的 Owned Window");
            Assert(!todoWindow.IsVisible, "待办窗口不得在主窗口启动时自动显示");

            var reactionClip = GetField<Array>(window, "_reactionClips").GetValue(0)!;
            var wakeFrameCount = GetField<Array>(window, "_wakeFrames").Length;
            var reactionStarted = (bool)Invoke(
                window,
                "TryStartReaction",
                reactionClip,
                false)!;
            Assert(reactionStarted, "打开待办前应能启动一段普通动作作为抢占测试");
            Assert(GetRawField(window, "_activeClip") is not null,
                "抢占测试开始后应存在活动动作");
            var reactionActionIndex = GetProperty<int>(reactionClip, "ActionFrameIndex");
            Invoke(window, "ShowActiveClipFrame", reactionActionIndex);
            SetField(window, "_activeFrameDeadlineTimestamp", long.MaxValue);
            var requestedReactionFrame = GetProperty<object>(
                GetClipFrames(reactionClip).GetValue(reactionActionIndex)!,
                "Image");
            WaitForPrefetchedSpritePage(window, requestedReactionFrame);
            Invoke(window, "TryShowPendingSpriteFrame");
            var ordinaryActionFrame = GetField<object>(window, "_currentSpriteFrame");
            Assert((int)Invoke(
                       window,
                       "GetTodoEnterStartIndex",
                       ordinaryActionFrame)! == wakeFrameCount,
                $"从普通动作打开待办必须直接从第{wakeFrameCount}帧起身终点接入，不能闪回趴枕头待机");

            var todoEnterClip = GetField<object>(window, "_todoEnterClip");
            var requestedTodoEntryFrame = GetProperty<object>(
                GetClipFrames(todoEnterClip).GetValue(wakeFrameCount)!,
                "Image");
            var requestedTodoEntryFrameInfo = GetSpriteFrameInfo(requestedTodoEntryFrame);
            // This contract inspects the first live Todo pose. Warm that one page
            // before starting the clip so background atlas decoding cannot consume
            // the entire short wake-to-think segment before the assertion runs.
            PrimeSpritePageForFrame(window, requestedTodoEntryFrame);

            var originalRight = window.Left + window.Width;
            var originalBottom = window.Top + window.Height;
            Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
            var ordinaryTodoStartIndex = GetField<int>(window, "_activeFrameIndex");
            Assert(ReferenceEquals(GetRawField(window, "_activeClip"), todoEnterClip),
                "打开待办必须把活动片段切换为唯一的 Todo 入场片段实例");
            Invoke(window, "TryShowPendingSpriteFrame");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));

            Assert(todoWindow.IsVisible, "进入 Todo 模式应显示独立 modeless 待办窗口");
            Assert(ReferenceEquals(todoWindow.Owner, window), "显示后 Owner 关系必须保持");
            Assert(!GetField<Popup>(window, "BubblePopup").IsOpen,
                "Todo 模式不得再打开旧 BubblePopup");
            AssertClose(window.Width, 190, "显示待办时主窗口宽度");
            AssertClose(window.Height, 242, "显示待办时主窗口高度");
            AssertClose(window.Left + window.Width, originalRight, "显示待办时主窗口右边界");
            AssertClose(window.Top + window.Height, originalBottom, "显示待办时主窗口下边界");

            var idleFrame = GetSpriteFrameInfo(GetField<object>(window, "_idleFrame"));
            var todoFrameObject = GetField<object>(window, "_todoFrame");
            var todoFrame = GetSpriteFrameInfo(todoFrameObject);
            Assert((todoFrame.PageName == "action-think" ||
                    todoFrame.PageName.StartsWith(
                        "action-think-part-",
                        StringComparison.Ordinal)) &&
                   todoFrame.Name.StartsWith(
                       "Assets/luban-think-smooth-",
                       StringComparison.Ordinal),
                "Todo 状态应使用专用的全身思考姿势");
            Assert(todoFrame.Height >= idleFrame.Height + 30,
                "Todo 姿势的可见高度应显著高于趴枕头待机，不能产生突然缩小的错觉");
            Assert(GetProperty<string>(todoEnterClip, "ActionName") == "todo-open",
                "打开 Todo 必须抢占普通动作并启动平滑起身入场");
            var todoEntryDeadline =
                GetField<long>(window, "_activeFrameDeadlineTimestamp");
            var todoEntryClockSubscribed =
                GetField<bool>(window, "_isVisualClockSubscribed");
            Assert(todoEntryDeadline > 0 && todoEntryClockSubscribed,
                "Todo 起身入场期间必须登记绝对帧截止点并订阅统一视觉时钟；" +
                $"deadline={todoEntryDeadline}, subscribed={todoEntryClockSubscribed}, " +
                $"activeIndex={GetField<int>(window, "_activeFrameIndex")}, " +
                $"pending={GetRawField(window, "_pendingSpriteFrame") is not null}");
            Assert(ordinaryTodoStartIndex == wakeFrameCount &&
                   requestedTodoEntryFrameInfo.Name.EndsWith(
                       $"luban-wake-smooth-{wakeFrameCount:000}.png",
                       StringComparison.Ordinal),
                $"普通动作抢占后的Todo入场首帧必须是wake-smooth-{wakeFrameCount:000}；" +
                $"index={ordinaryTodoStartIndex}, requested={requestedTodoEntryFrameInfo.Name}");

            var entryIndex = GetField<int>(window, "_activeFrameIndex");
            var entryFrame = GetField<object>(window, "_currentSpriteFrame");
            var todoClipLength = GetClipFrames(todoEnterClip).Length;
            Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "None"));
            var interruptedExit = GetRawField(window, "_activeClip")!;
            Assert(GetProperty<string>(interruptedExit, "ActionName") == "todo-close" &&
                   GetField<int>(window, "_activeFrameIndex") ==
                   todoClipLength - 1 - entryIndex &&
                   Equals(GetField<object>(window, "_currentSpriteFrame"), entryFrame),
                "Todo 入场中途收起必须从当前对应姿势反向播放，不能闪到站立端点");
            Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
            Assert(GetProperty<string>(GetRawField(window, "_activeClip")!, "ActionName") ==
                   "todo-open" &&
                   GetField<int>(window, "_activeFrameIndex") == entryIndex &&
                   Equals(GetField<object>(window, "_currentSpriteFrame"), entryFrame),
                "Todo 收起中途重新打开必须映射回同一姿势，不能闪到待机端点");

            var todoEnterRemaining = TimeSpan.FromTicks(
                GetClipFrames(todoEnterClip)
                    .Cast<object>()
                    .Skip(GetField<int>(window, "_activeFrameIndex"))
                    .Sum(frame => GetFrameDuration(frame).Ticks));
            PumpDispatcher(todoEnterRemaining + TimeSpan.FromMilliseconds(250));
            var completedTodoClip = GetRawField(window, "_activeClip");
            var completedTodoDeadline = GetField<long>(window, "_activeFrameDeadlineTimestamp");
            var completedTodoClockSubscribed = GetField<bool>(window, "_isVisualClockSubscribed");
            Assert(completedTodoClip is null &&
                   completedTodoDeadline == 0 &&
                   !completedTodoClockSubscribed,
                "Todo 起身入场完成后必须停止统一视觉时钟并释放活动 clip；" +
                $"clip={completedTodoClip}, deadline={completedTodoDeadline}, " +
                $"clock={completedTodoClockSubscribed}, " +
                $"frame={GetField<int>(window, "_activeFrameIndex")}, " +
                $"blend={GetField<bool>(window, "_isFrameBlending")}");
            Assert(Equals(GetField<object>(window, "_currentSpriteFrame"), todoFrameObject),
                "Todo 起身入场完成后应稳定停在专用思考姿势");

            var facingScale = GetField<ScaleTransform>(window, "PetFacingScale");
            var petScale = GetField<ScaleTransform>(window, "PetScale");
            AssertClose(facingScale.ScaleX, 1, "Todo 状态水平朝向缩放");
            AssertClose(facingScale.ScaleY, 1, "Todo 状态垂直朝向缩放");
            AssertClose(petScale.ScaleX, 1, "Todo 状态呼吸水平缩放");
            AssertClose(petScale.ScaleY, 1, "Todo 状态呼吸垂直缩放");
            PumpDispatcher(TimeSpan.FromMilliseconds(220));
            Assert(Equals(GetField<object>(window, "_currentSpriteFrame"), todoFrameObject),
                "Todo 打开期间经过多个动作帧间隔后仍应保持专用思考姿势");

            var monitorType = typeof(MainWindow).Assembly.GetType(
                "LubanDesktopPet.MonitorWorkArea",
                throwOnError: true)!;
            var workArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
            var todoEdgeFrames = GetField<Array>(window, "_edgeLeftFrames");
            var todoEdgeRestFrame =
                todoEdgeFrames.GetValue(todoEdgeFrames.Length - 1)!;
            PrimeSpritePageForFrame(window, todoEdgeRestFrame);
            window.Left = workArea.Left;
            window.Top = Math.Clamp(
                window.Top,
                workArea.Top,
                workArea.Bottom - window.ActualHeight);
            var todoWindowBounds = new Rect(
                window.Left,
                window.Top,
                window.ActualWidth > 0 ? window.ActualWidth : window.Width,
                window.ActualHeight > 0 ? window.ActualHeight : window.Height);
            var todoContactBounds = (Rect)Invoke(
                window,
                "GetPetContactBounds",
                todoWindowBounds)!;
            window.Left += workArea.Left - todoContactBounds.Left;
            Invoke(window, "UpdateEdgeDockAfterDrag");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(GetField<object>(window, "_edgeDock").ToString() == "Left",
                "Todo 打开时拖到可见像素接触屏幕左边缘必须启动探头状态");
            Assert(GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") >
                   Stopwatch.GetTimestamp(),
                "Todo 打开时边缘探头必须保留有效的绝对时间截止点");
            Assert(todoWindow.IsVisible &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Todo" &&
                   Equals(
                       GetField<object>(window, "_currentSpriteFrame"),
                       todoEdgeRestFrame),
                "Todo 打开时边缘动画必须显示休息帧，同时待办窗口继续可见");

            SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "Left"));
            var inFlightEdgeTimestamp = Stopwatch.GetTimestamp();
            SetField(window, "_edgePeekFrameDeadlineTimestamp", inFlightEdgeTimestamp);
            Invoke(window, "AdvanceEdgePeek", inFlightEdgeTimestamp);
            Assert(GetField<object>(window, "_edgeDock").ToString() == "Left" &&
                   GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") >
                       inFlightEdgeTimestamp,
                "Todo 打开时在途边缘 Tick 必须继续推进而不是清理探头状态");
            Assert(todoWindow.IsVisible &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Todo" &&
                   todoEdgeFrames.Cast<object>().Contains(
                       GetField<object>(window, "_currentSpriteFrame")),
                "Todo 打开时推进边缘 Tick 后仍须保持待办窗口和边缘序列");

            todoWindow.Close();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(!todoWindow.IsVisible,
                "Alt+F4/系统关闭待办窗口时应取消销毁并安全隐藏");
            Assert(GetField<object>(window, "_bubbleMode").ToString() == "None",
                "Alt+F4 收起后 MainWindow 的 BubbleMode 必须同步为 None");
            var preservedTodoEdgeDeadline =
                GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            Assert(GetRawField(window, "_activeClip") is null &&
                   GetField<object>(window, "_edgeDock").ToString() == "Left" &&
                   preservedTodoEdgeDeadline > 0 &&
                   preservedTodoEdgeDeadline != long.MaxValue &&
                   todoEdgeFrames.Cast<object>().Contains(
                       GetField<object>(window, "_currentSpriteFrame")) &&
                   GetField<bool>(window, "_isVisualClockSubscribed"),
                "已吸附时收起 Todo 只能隐藏面板，必须保留边缘探头状态、帧和绝对时钟");
            Invoke(window, "AdvanceEdgePeek", preservedTodoEdgeDeadline);
            Assert(GetField<object>(window, "_edgeDock").ToString() == "Left" &&
                   GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") >
                   preservedTodoEdgeDeadline,
                "已吸附时收起 Todo 后边缘探头必须继续推进，不能回到待机或停在僵死帧");

            Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(todoWindow.IsVisible,
                "Alt+F4 收起后应能再次复用同一个 TodoWindow 成功打开");
            Assert(ReferenceEquals(todoWindow.Owner, window),
                "重新打开后 Owned Window 关系必须保持");
            Assert(GetField<object>(window, "_edgeDock").ToString() == "None",
                "吸附中重新打开 Todo 时仍应由思考姿势接管画面并主动退出边缘探头");
            Assert(!GetField<Popup>(window, "BubblePopup").IsOpen,
                "重新打开 TodoWindow 时旧 BubblePopup 仍不得显示");

            Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "None"));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(!todoWindow.IsVisible, "收起 Todo 模式应隐藏而非销毁独立待办窗口");
            var finalTodoExitClip = GetRawField(window, "_activeClip")!;
            Assert(GetProperty<string>(finalTodoExitClip, "ActionName") == "todo-close",
                "右键或外部点击收起 Todo 都应播放同一段平滑过渡");
            var todoExitRemaining = TimeSpan.FromTicks(
                GetClipFrames(finalTodoExitClip)
                    .Cast<object>()
                    .Skip(GetField<int>(window, "_activeFrameIndex"))
                    .Sum(frame => GetFrameDuration(frame).Ticks));
            PumpDispatcher(todoExitRemaining + TimeSpan.FromMilliseconds(250));
            Assert(GetRawField(window, "_activeClip") is null,
                "Todo 收起过渡完成后必须清理活动动作");
        }
        finally
        {
            window.IsHitTestVisible = originalMainHitTestVisible;
            todoWindow.IsHitTestVisible = originalTodoHitTestVisible;
            SetField(window, "_suppressTodoWindowDeactivate", false);
        }
    }

    private static void AssertTodoReorderPersistenceContract(MainWindow window)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"xlb-pet-todo-order-{Guid.NewGuid():N}");
        var todoPath = Path.Combine(tempDirectory, "todos.json");
        var todoStore = new TodoStore(todoPath);
        var originalTodoStore = GetField<TodoStore>(window, "_todoStore");
        var todos = GetField<ObservableCollection<TodoItem>>(window, "_todos");
        var originalTodos = todos.ToArray();
        SetField(window, "_todoStore", todoStore);

        try
        {
            todos.Clear();
            var first = new TodoItem { Text = "第一项" };
            var second = new TodoItem { Text = "第二项", IsCompleted = true };
            var third = new TodoItem { Text = "第三项" };
            todos.Add(first);
            todos.Add(second);
            todos.Add(third);

            var todoWindow = GetField<TodoWindow>(window, "_todoWindow");
            Assert(ReferenceEquals(todoWindow.Todos, todos),
                "TodoWindow 必须直接绑定 MainWindow 的 ObservableCollection，拖放后界面才能立即反映新顺序");

            Invoke(window, "TodoWindow_TodoMoveRequested", third, 0);
            Assert(todos.Count == 3 &&
                   ReferenceEquals(todos[0], third) &&
                   ReferenceEquals(todos[1], first) &&
                   ReferenceEquals(todos[2], second),
                "拖拽重排必须通过 ObservableCollection.Move 更新原集合，不能复制或重建待办项");

            var persistedAfterMove = todoStore.Load();
            Assert(persistedAfterMove.Select(item => item.Text)
                    .SequenceEqual(new[] { "第三项", "第一项", "第二项" }) &&
                   persistedAfterMove[2].IsCompleted,
                "拖拽落下后必须按 ObservableCollection 的最终顺序立即保存，并保留完成状态");

            third.Text = "修改后的第三项";
            Invoke(window, "TodoWindow_TodoEdited", third);
            var persistedAfterEdit = todoStore.Load();
            Assert(persistedAfterEdit.Select(item => item.Text)
                    .SequenceEqual(new[] { "修改后的第三项", "第一项", "第二项" }),
                "行内编辑事件必须立即保存修改后的文字，同时保持拖拽产生的顺序");

            var orderBeforeNoOp = todos.ToArray();
            Invoke(window, "TodoWindow_TodoMoveRequested", third, 0);
            Assert(todos.SequenceEqual(orderBeforeNoOp),
                "拖到原位置必须保持集合不变，不能制造无意义的二次移动");

            Invoke(window, "TodoWindow_TodoMoveRequested", new TodoItem { Text = "外部项" }, 1);
            Assert(todos.SequenceEqual(orderBeforeNoOp),
                "不属于当前 ObservableCollection 的外部拖放项必须被忽略");
        }
        finally
        {
            SetField(window, "_todoStore", originalTodoStore);
            todos.Clear();
            foreach (var item in originalTodos)
            {
                todos.Add(item);
            }

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // 临时持久化文件的清理失败不应掩盖待办顺序契约结果。
            }
        }
    }

    private static void AssertStartupRegistrationContract()
    {
        var startupType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.StartupRegistration",
            throwOnError: true)!;
        var constructor = startupType.GetConstructor(
            InstanceFlags,
            binder: null,
            [
                typeof(string),
                typeof(Func<string>),
                typeof(Action<string>),
                typeof(Action)
            ],
            modifiers: null) ??
            throw new InvalidOperationException(
                "StartupRegistration 必须提供委托后端构造函数以便无注册表测试");
        var tryReadAndRepair = startupType.GetMethod(
            "TryReadAndRepair",
            InstanceFlags) ??
            throw new InvalidOperationException(
                "StartupRegistration 缺少 TryReadAndRepair");
        var trySetEnabled = startupType.GetMethod(
            "TrySetEnabled",
            InstanceFlags) ??
            throw new InvalidOperationException(
                "StartupRegistration 缺少 TrySetEnabled");
        var buildLaunchCommand = startupType.GetMethod(
            "BuildLaunchCommand",
            StaticFlags) ??
            throw new InvalidOperationException(
                "StartupRegistration 缺少 BuildLaunchCommand");

        var executablePath = Path.Combine(
            Path.GetTempPath(),
            "小鲁班 desktop",
            "LubanDesktopPet.exe");
        var expectedCommand = (string)(buildLaunchCommand.Invoke(
            null,
            [executablePath, null]) ??
            throw new InvalidOperationException("开机启动命令不能为空"));
        Assert(expectedCommand ==
               $"\"{Path.GetFullPath(executablePath)}\" --autostart",
            "开机启动命令必须完整引用可执行文件路径并附加 --autostart");

        string? storedValue = null;
        var readCount = 0;
        var writeCount = 0;
        var deleteCount = 0;
        Func<string?> readValue = () =>
        {
            readCount++;
            return storedValue;
        };
        Action<string> writeValue = value =>
        {
            writeCount++;
            storedValue = value;
        };
        Action deleteValue = () =>
        {
            deleteCount++;
            storedValue = null;
        };
        var registration = constructor.Invoke(
            [expectedCommand, readValue, writeValue, deleteValue]);

        object?[] readArguments = [false, null];
        Assert((bool)(tryReadAndRepair.Invoke(
                   registration,
                   readArguments) ?? false) &&
               readArguments[0] is false &&
               readArguments[1] is null &&
               readCount == 1 &&
               writeCount == 0 &&
               deleteCount == 0,
            "委托后端无启动项时必须只读取一次并报告关闭，绝不能访问或写入真实注册表");

        storedValue = "\"C:\\旧目录\\LubanDesktopPet.exe\" --autostart";
        readArguments = [false, null];
        Assert((bool)(tryReadAndRepair.Invoke(
                   registration,
                   readArguments) ?? false) &&
               readArguments[0] is true &&
               readArguments[1] is null &&
               storedValue == expectedCommand &&
               writeCount == 1,
            "发现旧路径时必须通过注入的写委托修复，并重新读取验证");

        storedValue = null;
        object?[] setArguments = [true, false, null];
        Assert((bool)(trySetEnabled.Invoke(
                   registration,
                   setArguments) ?? false) &&
               setArguments[1] is true &&
               setArguments[2] is null &&
               storedValue == expectedCommand,
            "开启自启必须写入精确命令并通过假后端回读验证");
        setArguments = [false, true, null];
        Assert((bool)(trySetEnabled.Invoke(
                   registration,
                   setArguments) ?? false) &&
               setArguments[1] is false &&
               setArguments[2] is null &&
               storedValue is null &&
               deleteCount == 1,
            "关闭自启必须通过假后端删除并验证最终状态");

        storedValue = "legacy-command";
        var corruptExpectedWrite = true;
        Action<string> failingWriteValue = value =>
        {
            if (corruptExpectedWrite &&
                string.Equals(value, expectedCommand, StringComparison.Ordinal))
            {
                storedValue = "corrupt-command";
                return;
            }

            storedValue = value;
        };
        var failingRegistration = constructor.Invoke(
            [
                expectedCommand,
                (Func<string?>)(() => storedValue),
                failingWriteValue,
                (Action)(() => storedValue = null)
            ]);
        setArguments = [true, false, null];
        Assert(!(bool)(trySetEnabled.Invoke(
                    failingRegistration,
                    setArguments) ?? true) &&
               setArguments[1] is true &&
               setArguments[2] is string error &&
               error.Contains("校验失败", StringComparison.Ordinal) &&
               storedValue == "legacy-command",
            "假后端写后校验失败时必须恢复原值，并报告恢复后的真实启用状态");
        corruptExpectedWrite = false;
    }

    private static void AssertTodoCutContract()
    {
        var todoWindow = new TodoWindow
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false
        };
        try
        {
            todoWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var input = GetField<TextBox>(todoWindow, "TodoInput");

            input.Text = "甲乙丙";
            input.Select(0, 1);
            Assert((bool)(Invoke(
                       todoWindow,
                       "TryCutSelectedText",
                       input) ?? false),
                "TodoInput 选中首字符时必须由原子剪切路径处理");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(input.Text == "乙丙" &&
                   input.SelectionStart == 0 &&
                   input.SelectionLength == 0 &&
                   Clipboard.GetText() == "甲",
                "TodoInput 首字符 Ctrl+X 必须复制“甲”、删除一次并把光标稳定放回索引0");

            input.Text = "甲乙丙";
            input.Select(0, 0);
            Assert(!(bool)(Invoke(
                        todoWindow,
                        "TryCutSelectedText",
                        input) ?? true) &&
                   input.Text == "甲乙丙" &&
                   input.SelectionLength == 0,
                "TodoInput 无选区 Ctrl+X 必须无操作，不能沿用 Ctrl+C 的复制全文契约");

            var previewKeySource = ExtractPrivateMethodSource(
                File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml.cs")),
                "TodoWindow_PreviewKeyDown");
            var handledIndex = previewKeySource.IndexOf(
                "e.Handled = true",
                StringComparison.Ordinal);
            var imeGuardIndex = previewKeySource.IndexOf(
                "if (!IsImeComposing)",
                StringComparison.Ordinal);
            var cutIndex = previewKeySource.IndexOf(
                "TryCutSelectedText(cutTextBox)",
                StringComparison.Ordinal);
            Assert(previewKeySource.Contains(
                       "Keyboard.Modifiers == ModifierKeys.Control",
                       StringComparison.Ordinal) &&
                   handledIndex >= 0 &&
                   imeGuardIndex > handledIndex &&
                   cutIndex > imeGuardIndex,
                "Ctrl+X 必须只匹配标准 Control+X，并在 IME 组合态判断前先标记 Handled，防止原生 Cut 继续删字");

            input.Text = "甲乙丙";
            input.Select(0, 1);
            InvokeStatic(
                typeof(TodoWindow),
                "RemovePendingCutSelection",
                input,
                "甲乙丙",
                0,
                1,
                "甲",
                true);
            Assert(input.Text == "乙丙" &&
                   input.SelectionStart == 0 &&
                   input.SelectionLength == 0,
                "延迟剪切快照完全匹配时必须只删除原选区一次");

            input.Text = "甲乙丙丁";
            input.Select(0, 1);
            InvokeStatic(
                typeof(TodoWindow),
                "RemovePendingCutSelection",
                input,
                "甲乙丙",
                0,
                1,
                "甲",
                true);
            Assert(input.Text == "甲乙丙丁" &&
                   input.SelectionStart == 0 &&
                   input.SelectionLength == 1,
                "延迟重试前全文已变化时必须放弃旧剪切，不能删除新输入");

            input.Text = "甲乙丙";
            input.Select(1, 1);
            InvokeStatic(
                typeof(TodoWindow),
                "RemovePendingCutSelection",
                input,
                "甲乙丙",
                0,
                1,
                "甲",
                true);
            Assert(input.Text == "甲乙丙" &&
                   input.SelectionStart == 1 &&
                   input.SelectionLength == 1,
                "延迟重试前选区已变化时必须放弃旧剪切");

            input.Text = "甲乙丙";
            input.Select(0, 1);
            SetField(todoWindow, "_pendingClipboardCutText", "甲");
            SetField(todoWindow, "_pendingClipboardCutSnapshot", "甲乙丙");
            SetField(todoWindow, "_pendingClipboardCutTextBox", input);
            SetField(todoWindow, "_pendingClipboardCutSelectionStart", 0);
            SetField(todoWindow, "_pendingClipboardCutSelectionLength", 1);
            input.Text = "甲乙丙丁";
            input.Select(0, 1);
            Invoke(todoWindow, "RetryClipboardCopy");
            Assert(input.Text == "甲乙丙丁" &&
                   input.SelectionStart == 0 &&
                   input.SelectionLength == 1 &&
                   GetRawField(todoWindow, "_pendingClipboardCutText") is null &&
                   GetRawField(todoWindow, "_pendingClipboardCutTextBox") is null,
                "真实延迟重试必须经过快照校验并清空一次性状态，不能在用户继续输入后删错字符");

            var item = new TodoItem { Text = "甲乙丙" };
            todoWindow.Todos = new ObservableCollection<TodoItem> { item };
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var list = GetField<ListBox>(todoWindow, "TodoItemsControl");
            var container = list.ItemContainerGenerator.ContainerFromIndex(0)
                as DependencyObject ??
                throw new InvalidOperationException("剪切回归找不到待办列表行");
            var editor = FindVisualDescendant<TextBox>(container) ??
                throw new InvalidOperationException("剪切回归找不到待办行 TextBox");
            Assert(editor.IsReadOnly &&
                   !(bool)(Invoke(
                       todoWindow,
                       "IsEditableTextSource",
                       editor) ?? true),
                "只读待办行不得进入自定义 Ctrl+X 路径");

            Invoke(todoWindow, "BeginTodoEdit", editor, item);
            editor.Text = "甲乙丙";
            editor.Select(0, 1);
            Assert(!editor.IsReadOnly &&
                   (bool)(Invoke(
                       todoWindow,
                       "TryCutSelectedText",
                       editor) ?? false),
                "只有进入行内编辑后，待办行才允许 Ctrl+X");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(editor.Text == "乙丙" &&
                   GetField<string>(
                       todoWindow,
                       "_editingTodoDraftText") == "乙丙",
                "行内首字符剪切必须同步更新待办编辑草稿，供 Enter 或外点准确保存");
        }
        finally
        {
            todoWindow.CloseForApplication();
        }
    }

    private static void AssertScheduledTaskTabContract()
    {
        var type = typeof(TodoWindow);
        foreach (var propertyName in new[]
                 {
                     "ScheduledTasks",
                     "IsTransientPopupOpen"
                 })
        {
            Assert(type.GetProperty(
                       propertyName,
                       BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {propertyName} 属性");
        }

        Assert(type.GetMethod(
                   "ShowDefaultTab",
                   BindingFlags.Instance | BindingFlags.Public) is not null,
            "TodoWindow 应公开 ShowDefaultTab 以便每次右键打开时回到待办页");
        foreach (var eventName in new[]
                 {
                     "ScheduledTaskAddRequested",
                     "ScheduledTaskEditRequested",
                     "ScheduledTaskDeleteRequested",
                     "TransientInteractionCompleted"
                 })
        {
            Assert(type.GetEvent(
                       eventName,
                       BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {eventName} 事件");
        }

        var scheduledTasks = new ObservableCollection<ScheduledTaskItem>();
        var todoWindow = new TodoWindow
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ScheduledTasks = scheduledTasks
        };
        try
        {
            var todoTab = GetField<RadioButton>(todoWindow, "TodoTabButton");
            var scheduledTab = GetField<RadioButton>(todoWindow, "ScheduledTaskTabButton");
            var todoPage = GetField<Grid>(todoWindow, "TodoPage");
            var scheduledPage = GetField<Grid>(todoWindow, "ScheduledTaskPage");
            var scheduledList = GetField<ListBox>(todoWindow, "ScheduledTaskItemsControl");
            var scheduledInput = GetField<TextBox>(todoWindow, "ScheduledTaskInput");
            var scheduledDatePickerHost = GetField<Border>(
                todoWindow,
                "ScheduledDatePickerHost");
            var scheduledDateInput = GetField<TextBox>(
                todoWindow,
                "ScheduledDateInput");
            var scheduledDatePickerPopup = GetField<Popup>(
                todoWindow,
                "ScheduledDatePickerPopup");
            var scheduledDateItems = GetField<ItemsControl>(
                todoWindow,
                "ScheduledDateItemsControl");
            var scheduledDateMonthText = GetField<TextBlock>(
                todoWindow,
                "ScheduledDateMonthText");
            var scheduledDatePreviousMonthButton = GetField<Button>(
                todoWindow,
                "ScheduledDatePreviousMonthButton");
            var scheduledDateNextMonthButton = GetField<Button>(
                todoWindow,
                "ScheduledDateNextMonthButton");
            var scheduledDateTodayButton = GetField<Button>(
                todoWindow,
                "ScheduledDatePickerTodayButton");
            var scheduledTime = GetField<TextBox>(todoWindow, "ScheduledTimeInput");
            var scheduledTimePickerHost = GetField<Border>(
                todoWindow,
                "ScheduledTimePickerHost");
            var scheduledTimePickerPopup = GetField<Popup>(
                todoWindow,
                "ScheduledTimePickerPopup");
            var scheduledHourPicker = GetField<ComboBox>(
                todoWindow,
                "ScheduledHourComboBox");
            var scheduledMinutePicker = GetField<ComboBox>(
                todoWindow,
                "ScheduledMinuteComboBox");
            var scheduledSecondPicker = GetField<ComboBox>(
                todoWindow,
                "ScheduledSecondComboBox");
            var scheduledRepeatToggle = GetField<CheckBox>(
                todoWindow,
                "ScheduledRepeatToggle");
            var scheduledRepeatDays = GetField<TextBox>(
                todoWindow,
                "ScheduledRepeatDaysInput");
            var scheduledRepeatHours = GetField<TextBox>(
                todoWindow,
                "ScheduledRepeatHoursInput");
            var scheduledRepeatMinutes = GetField<TextBox>(
                todoWindow,
                "ScheduledRepeatMinutesInput");
            var scheduledRepeatHint = GetField<TextBlock>(
                todoWindow,
                "ScheduledRepeatHintText");
            var scheduledSubmit = GetField<Button>(
                todoWindow,
                "ScheduledTaskSubmitButton");
            var scheduledEditCancel = GetField<Button>(
                todoWindow,
                "ScheduledTaskEditCancelButton");
            var validationText = GetField<TextBlock>(
                todoWindow,
                "ScheduledTaskValidationText");

            Assert(todoTab.IsChecked == true &&
                   scheduledTab.IsChecked != true &&
                   todoPage.Visibility == Visibility.Visible &&
                   scheduledPage.Visibility == Visibility.Collapsed,
                "TodoWindow 创建后必须默认显示左侧“待办事项”选项卡");
            Assert(ReferenceEquals(scheduledList.ItemsSource, scheduledTasks),
                "定时任务列表必须直接绑定传入的 ObservableCollection");
            Assert(scheduledInput.MaxLength == 80 &&
                   scheduledTime.MaxLength == 8 &&
                   Equals(scheduledSubmit.Content, "设定") &&
                   scheduledEditCancel.Visibility == Visibility.Collapsed &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime &&
                   scheduledDateInput.IsReadOnly &&
                   !InputMethod.GetIsInputMethodEnabled(scheduledDateInput) &&
                   scheduledRepeatToggle.IsChecked != true &&
                   scheduledRepeatDays.Text == "0" &&
                   scheduledRepeatHours.Text == "1" &&
                   scheduledRepeatMinutes.Text == "0" &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   DateTime.TryParseExact(
                       scheduledTime.Text,
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out _),
                "定时页必须默认提供单次日期和 HH:mm:ss 秒级时间；循环编辑器默认关闭并预填1小时");
            Assert(string.IsNullOrEmpty(validationText.Text),
                "定时任务初始状态不应显示错误提示");

            var exactNow = new DateTimeOffset(
                2031,
                5,
                6,
                7,
                8,
                9,
                TimeZoneInfo.Local.GetUtcOffset(
                    new DateTime(2031, 5, 6, 7, 8, 9)));
            Invoke(todoWindow, "ResetScheduledTaskDraftClock", exactNow);
            var expectedLocalNow = exactNow.LocalDateTime;
            Assert(GetRawField(todoWindow, "_scheduledDate") is DateTime scheduledDate &&
                   scheduledDate == expectedLocalNow.Date &&
                   scheduledDateInput.Text == expectedLocalNow.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == expectedLocalNow.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture) &&
                   scheduledHourPicker.SelectedIndex == expectedLocalNow.Hour &&
                   scheduledMinutePicker.SelectedIndex == expectedLocalNow.Minute &&
                   scheduledSecondPicker.SelectedIndex == expectedLocalNow.Second &&
                   scheduledHourPicker.Items.Count == 24 &&
                   scheduledMinutePicker.Items.Count == 60 &&
                   scheduledSecondPicker.Items.Count == 60 &&
                   scheduledTime.IsReadOnly &&
                   ReferenceEquals(
                       scheduledDatePickerPopup.PlacementTarget,
                       scheduledDatePickerHost) &&
                   scheduledDatePickerPopup.AllowsTransparency &&
                   scheduledDatePickerPopup.StaysOpen &&
                   ReferenceEquals(
                       scheduledTimePickerPopup.PlacementTarget,
                       scheduledTimePickerHost) &&
                   scheduledTimePickerPopup.AllowsTransparency,
                "定时任务默认值必须精确使用当前本地秒，并通过自定义日期月历和 24/60/60 时间浮层编辑");

            Invoke(todoWindow, "SelectTaskPage", true, false);
            Assert(todoTab.IsChecked != true &&
                   scheduledTab.IsChecked == true &&
                   todoPage.Visibility == Visibility.Collapsed &&
                   scheduledPage.Visibility == Visibility.Visible,
                "点击右侧“定时任务”后必须只显示定时页");
            todoWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(50));
            todoWindow.UpdateLayout();
            var formattedTime = new FormattedText(
                scheduledTime.Text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    scheduledTime.FontFamily,
                    scheduledTime.FontStyle,
                    scheduledTime.FontWeight,
                    scheduledTime.FontStretch),
                scheduledTime.FontSize,
                scheduledTime.Foreground,
                VisualTreeHelper.GetDpi(scheduledTime).PixelsPerDip);
            Assert(scheduledDatePickerHost.ActualWidth >= 101.5 &&
                   scheduledTimePickerHost.ActualWidth >= 91.5 &&
                   scheduledTime.ActualWidth >=
                   formattedTime.WidthIncludingTrailingWhitespace,
                "日期行必须给 HH:mm:ss 留足宽度，不能被时钟图标或下拉箭头裁掉");
            todoWindow.ShowDefaultTab();
            Assert(todoTab.IsChecked == true &&
                   scheduledTab.IsChecked != true &&
                   todoPage.Visibility == Visibility.Visible &&
                   scheduledPage.Visibility == Visibility.Collapsed,
                "ShowDefaultTab 必须可重用地恢复默认待办页");

            var transientCompletionCount = 0;
            todoWindow.TransientInteractionCompleted += () =>
                transientCompletionCount++;
            Invoke(todoWindow, "SelectTaskPage", true, false);
            var timeBeforeDateBrowsing = scheduledTime.Text;
            Invoke(todoWindow, "OpenScheduledDatePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledDatePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen &&
                   scheduledDateItems.Items.Count == 42 &&
                   scheduledDateMonthText.Text ==
                   expectedLocalNow.ToString(
                       "yyyy年M月",
                       CultureInfo.GetCultureInfo("zh-CN")),
                "自定义日期浮层必须使用固定六周、可切换月份并标记 transient 交互");
            var currentMonthCells = scheduledDateItems.Items
                .Cast<object>()
                .Count(cell => GetProperty<bool>(cell, "IsCurrentMonth"));
            var selectedCells = scheduledDateItems.Items
                .Cast<object>()
                .Where(cell => GetProperty<bool>(cell, "IsSelected"))
                .ToArray();
            Assert(currentMonthCells == DateTime.DaysInMonth(
                       expectedLocalNow.Year,
                       expectedLocalNow.Month) &&
                   selectedCells.Length == 1 &&
                   GetProperty<DateTime>(selectedCells[0], "Date") ==
                   expectedLocalNow.Date,
                "自定义日期浮层必须准确生成本月天数并唯一标出当前草稿日期");

            Invoke(
                todoWindow,
                "RefreshScheduledCalendar",
                new DateTime(2031, 12, 1));
            Assert(scheduledDateMonthText.Text == "2031年12月" &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime browsedDate &&
                   browsedDate == expectedLocalNow.Date &&
                   scheduledDateInput.Text == "2031-05-06" &&
                   scheduledTime.Text == timeBeforeDateBrowsing &&
                   GetRawField(todoWindow, "_scheduledTaskDraftClockEdited") is false,
                "浏览到同年其他月份不得偷偷修改已选日期、时分秒或草稿编辑状态");
            scheduledDateNextMonthButton.RaiseEvent(
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    scheduledDateNextMonthButton));
            Assert(scheduledDateMonthText.Text == "2032年1月",
                "点击“下一个月”必须从十二月正确跨到下一年一月");
            scheduledDatePreviousMonthButton.RaiseEvent(
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    scheduledDatePreviousMonthButton));
            Assert(scheduledDateMonthText.Text == "2031年12月" &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime returnedDate &&
                   returnedDate == expectedLocalNow.Date,
                "点击“上一个月”必须从一月正确退回上一年十二月，且不得改动已选日期");
            Invoke(
                todoWindow,
                "RefreshScheduledCalendar",
                new DateTime(2032, 2, 1));
            Assert(scheduledDateItems.Items
                       .Cast<object>()
                       .Any(cell =>
                           GetProperty<DateTime>(cell, "Date") ==
                           new DateTime(2032, 2, 29) &&
                           GetProperty<bool>(cell, "IsCurrentMonth")),
                "固定六周月历必须正确显示闰年二月二十九日");

            var dateInputSource = PresentationSource.FromVisual(scheduledDateInput)
                ?? throw new InvalidOperationException("日期入口未建立输入源");
            var dateEscape = CreateKeyEvent(dateInputSource, Key.Escape);
            Invoke(
                todoWindow,
                "TodoWindow_PreviewKeyDown",
                todoWindow,
                dateEscape);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(dateEscape.Handled &&
                   !scheduledDatePickerPopup.IsOpen &&
                   !todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 1 &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime escapedDate &&
                   escapedDate == expectedLocalNow.Date &&
                   scheduledTime.Text == timeBeforeDateBrowsing,
                $"Esc 必须只关闭日期浮层，不改变已选日期或时分秒，并只通知一次结束；" +
                $"Handled={dateEscape.Handled}, DateOpen={scheduledDatePickerPopup.IsOpen}, " +
                $"Transient={todoWindow.IsTransientPopupOpen}, Completions={transientCompletionCount}, " +
                $"Date={GetRawField(todoWindow, "_scheduledDate")}, Time={scheduledTime.Text}");
            Invoke(todoWindow, "CloseScheduledDatePicker");
            Assert(transientCompletionCount == 1,
                "重复关闭日期浮层不得重复发出 transient 完成事件");

            Invoke(todoWindow, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledTimePickerPopup.IsOpen &&
                   !scheduledDatePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen,
                "打开秒级时间浮层时必须关闭日期浮层并保持 transient 保护");
            Invoke(todoWindow, "OpenScheduledDatePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(scheduledDatePickerPopup.IsOpen &&
                   !scheduledTimePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 1,
                "日期和时间浮层必须原子互斥，切换中不能误报交互结束");
            Invoke(todoWindow, "CloseScheduledDatePicker");
            Assert(!todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 2,
                "最后一个日期或时间浮层关闭时必须且只能通知一次结束");

            Invoke(
                todoWindow,
                "OpenScheduledDatePicker");
            Invoke(
                todoWindow,
                "RefreshScheduledCalendar",
                new DateTime(2032, 2, 1));
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            var leapDate = new DateTime(2032, 2, 29);
            var leapCell = scheduledDateItems.Items
                .Cast<object>()
                .Single(cell => GetProperty<DateTime>(cell, "Date") == leapDate);
            var leapContainer = scheduledDateItems.ItemContainerGenerator
                .ContainerFromItem(leapCell) as DependencyObject
                ?? throw new InvalidOperationException("闰日日期格没有生成可视容器");
            var leapButton = FindVisualDescendants<Button>(leapContainer).Single();
            Assert(leapButton.Tag is DateTime leapTag && leapTag == leapDate,
                "闰日按钮必须通过真实 Tag 绑定携带 2032-02-29");
            var timeBeforeLeapSelection = scheduledTime.Text;
            leapButton.RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent, leapButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(GetRawField(todoWindow, "_scheduledDate") is DateTime selectedLeapDay &&
                   selectedLeapDay == leapDate &&
                   scheduledDateInput.Text == "2032-02-29" &&
                   scheduledTime.Text == timeBeforeLeapSelection &&
                   GetRawField(todoWindow, "_scheduledTaskDraftClockEdited") is true &&
                   !scheduledDatePickerPopup.IsOpen &&
                   transientCompletionCount == 3,
                "真实闰日按钮必须选择日期、关闭浮层且不能改动已经选好的时分秒");

            var todayBeforeClick = DateTime.Today;
            var timeBeforeToday = scheduledTime.Text;
            Invoke(todoWindow, "OpenScheduledDatePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            scheduledDateTodayButton.RaiseEvent(
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    scheduledDateTodayButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var todayAfterClick = DateTime.Today;
            Assert(GetRawField(todoWindow, "_scheduledDate") is DateTime selectedToday &&
                   (selectedToday == todayBeforeClick ||
                    selectedToday == todayAfterClick) &&
                   scheduledDateInput.Text == selectedToday.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == timeBeforeToday &&
                   !scheduledDatePickerPopup.IsOpen &&
                   transientCompletionCount == 4,
                "真实“今天”按钮必须选择本地今天、关闭日期浮层且不能改动时分秒");
            Invoke(todoWindow, "ResetScheduledTaskDraftClock", exactNow);
            Invoke(todoWindow, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            scheduledRepeatToggle.IsChecked = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledTimePickerPopup.IsOpen &&
                   todoWindow.IsTransientPopupOpen,
                "勾选循环后仍必须显示首次/下一次日期和时间，且已经打开的时间浮层不能消失");
            foreach (var (picker, selectedIndex) in new[]
                     {
                         (scheduledHourPicker, 11),
                         (scheduledMinutePicker, 22),
                         (scheduledSecondPicker, 33)
                     })
            {
                picker.IsDropDownOpen = true;
                PumpDispatcher(TimeSpan.FromMilliseconds(20));
                var option = picker.ItemContainerGenerator.ContainerFromIndex(selectedIndex)
                    as ComboBoxItem
                    ?? throw new InvalidOperationException(
                        $"时分秒选择器未生成第 {selectedIndex} 个可点击选项");
                var optionClick = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = option
                };
                option.RaiseEvent(optionClick);
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
                Assert(optionClick.Handled &&
                       picker.SelectedIndex == selectedIndex &&
                       !picker.IsDropDownOpen &&
                       scheduledTimePickerPopup.IsOpen &&
                       todoWindow.IsTransientPopupOpen,
                    "逐一选择时、分、秒并处理完 Dispatcher 消息后，只能关闭当前下拉层，外层时间浮层必须保持打开");
            }

            Assert(scheduledTime.Text == "11:22:33",
                "依次选择时、分、秒后，右侧入口必须完整同步为 11:22:33");
            var timePickerInputSource =
                PresentationSource.FromVisual(scheduledHourPicker)
                ?? throw new InvalidOperationException(
                    "时间浮层没有为小时选择器建立输入源");
            var timePickerEscape = CreateKeyEvent(
                timePickerInputSource,
                Key.Escape);
            Invoke(
                todoWindow,
                "ScheduledTimePickerPopup_PreviewKeyDown",
                scheduledHourPicker,
                timePickerEscape);
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            Assert(timePickerEscape.Handled &&
                   !scheduledTimePickerPopup.IsOpen &&
                   !scheduledHourPicker.IsDropDownOpen &&
                   !scheduledMinutePicker.IsDropDownOpen &&
                   !scheduledSecondPicker.IsDropDownOpen &&
                   !todoWindow.IsTransientPopupOpen &&
                   transientCompletionCount == 5,
                "焦点位于独立时间 Popup 时，Esc 仍必须关闭外层和三列下拉并且只通知一次结束");

            Invoke(todoWindow, "OpenScheduledTimePicker");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var timeConfirmButton = new Button();
            Invoke(
                todoWindow,
                "ScheduledTimePickerConfirmButton_Click",
                timeConfirmButton,
                new RoutedEventArgs(ButtonBase.ClickEvent, timeConfirmButton));
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(!scheduledTimePickerPopup.IsOpen &&
                   scheduledTime.Text == "11:22:33",
                "确定时必须关闭时间浮层并保留刚选好的完整 HH:mm:ss");
            Invoke(todoWindow, "ResetScheduledRepeatDraft");

            var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
            var outsideCloseSource = ExtractPrivateMethodSource(
                mainSource,
                "ProcessOutsideTodoClose");
            var todoSource = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml.cs"));
            var todoXaml = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml"));
            var scheduledTabClickSource = ExtractPrivateMethodSource(
                todoSource,
                "ScheduledTaskTabButton_Click");
            var resetDraftClockSource = ExtractPrivateMethodSource(
                todoSource,
                "ResetScheduledTaskDraftClock");
            Assert(outsideCloseSource.Contains(
                       "_todoWindow.IsTransientPopupOpen",
                       StringComparison.Ordinal) &&
                   scheduledTabClickSource.Contains(
                       "PrepareScheduledTaskDraftClockForDisplay(DateTimeOffset.Now)",
                       StringComparison.Ordinal) &&
                   resetDraftClockSource.Contains(
                       "var suggested = now.LocalDateTime",
                       StringComparison.Ordinal) &&
                   !resetDraftClockSource.Contains(
                       "AddMinutes(",
                       StringComparison.Ordinal),
                "MainWindow 的外部点击收起判定必须显式保护自定义日期和时间 transient popup");
            var datePopupChild = scheduledDatePickerPopup.Child
                ?? throw new InvalidOperationException("日期浮层缺少可视子树");
            var timePopupChild = scheduledTimePickerPopup.Child
                ?? throw new InvalidOperationException("时间浮层缺少可视子树");
            Assert(!(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledDatePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       datePopupChild,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)! &&
                   (bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledInput,
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerPopup)!,
                "日期浮层外点判定必须保护日期宿主、时间宿主和自身子树，只把表单其他区域视为外部");
            Assert(!(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledTimePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledDatePickerHost,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)! &&
                   !(bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       timePopupChild,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)! &&
                   (bool)InvokeStatic(
                       typeof(TodoWindow),
                       "ShouldCloseScheduledPickerPopup",
                       scheduledInput,
                       scheduledTimePickerHost,
                       scheduledDatePickerHost,
                       scheduledTimePickerPopup)!,
                "时间浮层外点判定必须保护时间宿主、日期宿主和自身子树，只把表单其他区域视为外部");
            Assert(!todoXaml.Contains("<DatePicker", StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "x:Name=\"ScheduledDatePickerPopup\"",
                       StringComparison.Ordinal),
                "日期入口必须彻底摆脱系统 DatePicker，并使用命名的自定义日期浮层");
            Assert(mainSource.Contains(
                       "_todoWindow.ScheduledTaskEditRequested +=",
                       StringComparison.Ordinal) &&
                   mainSource.Contains(
                       "TodoWindow_ScheduledTaskEditRequested",
                       StringComparison.Ordinal),
                "MainWindow 必须订阅定时任务编辑事件并交给排序、持久化和重调度处理器");

            var requestedCount = 0;
            string? requestedText = null;
            DateTimeOffset requestedDueAt = default;
            TimeSpan? requestedRepeatInterval = null;
            todoWindow.ScheduledTaskAddRequested += (text, dueAt, repeatInterval) =>
            {
                requestedCount++;
                requestedText = text;
                requestedDueAt = dueAt;
                requestedRepeatInterval = repeatInterval;
            };

            var futureLocal = DateTime.Now.AddHours(2);
            futureLocal = new DateTime(
                futureLocal.Year,
                futureLocal.Month,
                futureLocal.Day,
                futureLocal.Hour,
                futureLocal.Minute,
                futureLocal.Second,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(futureLocal))
            {
                futureLocal = futureLocal.AddHours(1);
            }

            scheduledInput.Text = "  明天带好小喇叭  ";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                futureLocal.Date,
                true);
            scheduledTime.Text = futureLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            var expectedDueAt = new DateTimeOffset(
                futureLocal,
                TimeZoneInfo.Local.GetUtcOffset(futureLocal));
            Assert(requestedCount == 1 &&
                   requestedText == "明天带好小喇叭" &&
                   requestedDueAt == expectedDueAt &&
                   requestedDueAt.Ticks % TimeSpan.TicksPerSecond == 0 &&
                   requestedRepeatInterval is null,
                "定时页应发出去除首尾空白的内容和精确到整秒的本地 DateTimeOffset");
            Assert(scheduledInput.Text.Length == 0 &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime &&
                   DateTime.TryParseExact(
                       scheduledDateInput.Text,
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out _) &&
                   DateTime.TryParseExact(
                       scheduledTime.Text,
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out _),
                "成功设定后应清空内容并重置为新的秒级默认时间");

            var requestedRecurringInterval =
                TimeSpan.FromDays(2) +
                TimeSpan.FromHours(3) +
                TimeSpan.FromMinutes(15);
            var recurringFirstLocal = DateTime.Now.AddDays(3);
            recurringFirstLocal = new DateTime(
                recurringFirstLocal.Year,
                recurringFirstLocal.Month,
                recurringFirstLocal.Day,
                14,
                25,
                36,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(recurringFirstLocal))
            {
                recurringFirstLocal = recurringFirstLocal.AddHours(1);
            }

            Invoke(
                todoWindow,
                "SetScheduledDate",
                recurringFirstLocal.Date,
                true);
            Invoke(
                todoWindow,
                "SetScheduledTimePickerSelection",
                recurringFirstLocal.Hour,
                recurringFirstLocal.Minute,
                recurringFirstLocal.Second,
                true);
            scheduledRepeatToggle.IsChecked = true;
            scheduledRepeatDays.Text = "2";
            scheduledRepeatHours.Text = "3";
            scheduledRepeatMinutes.Text = "15";
            scheduledInput.Text = "  循环检查小喇叭  ";
            Assert(scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledDateInput.Text == recurringFirstLocal.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == recurringFirstLocal.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture),
                "勾选循环后必须继续显示并保留用户选择的首次提醒日期和 HH:mm:ss");
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            var expectedRecurringFirstDueAt = new DateTimeOffset(
                recurringFirstLocal,
                TimeZoneInfo.Local.GetUtcOffset(recurringFirstLocal));
            Assert(requestedCount == 2 &&
                   requestedText == "循环检查小喇叭" &&
                   requestedRepeatInterval == requestedRecurringInterval &&
                   requestedDueAt == expectedRecurringFirstDueAt &&
                   requestedDueAt.Ticks % TimeSpan.TicksPerSecond == 0,
                "新增循环任务必须把用户选择的日期和 HH:mm:ss 作为首次 DueAt，间隔只定义后续提醒");
            Assert(scheduledInput.Text.Length == 0 &&
                   scheduledRepeatToggle.IsChecked != true &&
                   scheduledRepeatDays.Text == "0" &&
                   scheduledRepeatHours.Text == "1" &&
                   scheduledRepeatMinutes.Text == "0" &&
                   scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed,
                "循环任务新增成功后必须安全恢复默认单次草稿，避免下一条被误设为循环");

            var editLocal = DateTime.Now.AddHours(4);
            editLocal = new DateTime(
                editLocal.Year,
                editLocal.Month,
                editLocal.Day,
                editLocal.Hour,
                editLocal.Minute,
                editLocal.Second,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(editLocal))
            {
                editLocal = editLocal.AddHours(1);
            }

            var editItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "要修改的定时任务",
                DueAt = new DateTimeOffset(
                    editLocal,
                    TimeZoneInfo.Local.GetUtcOffset(editLocal)),
                CreatedAt = DateTimeOffset.Now.AddMinutes(-2)
            };
            scheduledTasks.Add(editItem);
            Invoke(todoWindow, "SelectTaskPage", true, false);
            PumpDispatcher(TimeSpan.FromMilliseconds(50));

            var editContainer = scheduledList.ItemContainerGenerator.ContainerFromItem(editItem)
                as FrameworkElement
                ?? throw new InvalidOperationException("定时任务编辑回归未生成列表行");
            var editButton = FindVisualDescendants<Button>(editContainer)
                .SingleOrDefault(button => button.Name == "ScheduledTaskEditButton")
                ?? throw new InvalidOperationException("定时任务行缺少铅笔编辑按钮");
            Assert(ReferenceEquals(editButton.Tag, editItem) &&
                   editButton.Content is Viewbox editIcon &&
                   FindVisualDescendants<System.Windows.Shapes.Path>(editIcon).Count() == 2,
                "定时任务编辑按钮必须使用与待办关闭按钮同风格的双路径斜铅笔图标并绑定当前项");

            var editRequestedCount = 0;
            ScheduledTaskItem? requestedEditItem = null;
            string? requestedEditText = null;
            DateTimeOffset requestedEditDueAt = default;
            TimeSpan? requestedEditRepeatInterval = null;
            todoWindow.ScheduledTaskEditRequested += (
                item,
                text,
                dueAt,
                repeatInterval) =>
            {
                editRequestedCount++;
                requestedEditItem = item;
                requestedEditText = text;
                requestedEditDueAt = dueAt;
                requestedEditRepeatInterval = repeatInterval;
            };

            Invoke(
                todoWindow,
                "ScheduledTaskEditButton_Click",
                editButton,
                new RoutedEventArgs(ButtonBase.ClickEvent, editButton));
            Assert(ReferenceEquals(
                   GetRawField(todoWindow, "_editingScheduledTask"),
                       editItem) &&
                   scheduledInput.Text == editItem.Text &&
                   GetRawField(todoWindow, "_scheduledDate") is DateTime editingDate &&
                   editingDate == editLocal.Date &&
                   scheduledDateInput.Text == editLocal.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == editLocal.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture) &&
                   Equals(scheduledSubmit.Content, "保存") &&
                   scheduledEditCancel.Visibility == Visibility.Visible,
                "点击铅笔后必须回填原内容、本地日期和秒级时间，并切换到可取消的保存状态");

            var savedLocal = DateTime.Now.AddHours(6);
            savedLocal = new DateTime(
                savedLocal.Year,
                savedLocal.Month,
                savedLocal.Day,
                savedLocal.Hour,
                savedLocal.Minute,
                savedLocal.Second,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(savedLocal))
            {
                savedLocal = savedLocal.AddHours(1);
            }

            scheduledInput.Text = "  修改后的定时任务  ";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                savedLocal.Date,
                true);
            scheduledTime.Text = savedLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            var scheduledInputSource = PresentationSource.FromVisual(scheduledInput)
                ?? throw new InvalidOperationException("定时任务输入框未建立输入源");
            Invoke(todoWindow, "SetImeComposing", true);
            var composingScheduledEnter = CreateKeyEvent(
                scheduledInputSource,
                Key.Enter);
            Invoke(
                todoWindow,
                "ScheduledTaskInput_PreviewKeyDown",
                scheduledInput,
                composingScheduledEnter);
            Assert(editRequestedCount == 0 &&
                   ReferenceEquals(
                       GetRawField(todoWindow, "_editingScheduledTask"),
                       editItem),
                "微软输入法仍在组合时，定时任务编辑 Enter 只能选词，不得提前保存或退出编辑");

            Invoke(todoWindow, "SetImeComposing", false);
            var committedScheduledEnter = CreateKeyEvent(
                scheduledInputSource,
                Key.Enter);
            Invoke(
                todoWindow,
                "ScheduledTaskInput_PreviewKeyDown",
                scheduledInput,
                committedScheduledEnter);
            var expectedEditedDueAt = new DateTimeOffset(
                savedLocal,
                TimeZoneInfo.Local.GetUtcOffset(savedLocal));
            Assert(editRequestedCount == 1 &&
                   ReferenceEquals(requestedEditItem, editItem) &&
                   requestedEditText == "修改后的定时任务" &&
                   requestedEditDueAt == expectedEditedDueAt &&
                   requestedEditRepeatInterval is null &&
                   committedScheduledEnter.Handled &&
                   GetRawField(todoWindow, "_editingScheduledTask") is null &&
                   Equals(scheduledSubmit.Content, "设定") &&
                   scheduledEditCancel.Visibility == Visibility.Collapsed,
                "定时任务编辑 Enter 必须只提交一次、Trim 内容、保留整秒时间并恢复新增状态");

            Invoke(
                todoWindow,
                "ScheduledTaskEditButton_Click",
                editButton,
                new RoutedEventArgs(ButtonBase.ClickEvent, editButton));
            scheduledInput.Text = "这一版应该被取消";
            Invoke(
                todoWindow,
                "ScheduledTaskEditCancelButton_Click",
                scheduledEditCancel,
                new RoutedEventArgs(ButtonBase.ClickEvent, scheduledEditCancel));
            Assert(editRequestedCount == 1 &&
                   editItem.Text == "要修改的定时任务" &&
                   editItem.DueAt == new DateTimeOffset(
                       editLocal,
                       TimeZoneInfo.Local.GetUtcOffset(editLocal)) &&
                   scheduledInput.Text.Length == 0 &&
                   GetRawField(todoWindow, "_editingScheduledTask") is null &&
                   Equals(scheduledSubmit.Content, "设定") &&
                   scheduledEditCancel.Visibility == Visibility.Collapsed,
                "取消修改不得触发保存或改动原任务，并必须清空草稿、恢复新增状态");

            Invoke(
                todoWindow,
                "ScheduledTaskEditButton_Click",
                editButton,
                new RoutedEventArgs(ButtonBase.ClickEvent, editButton));
            var pastLocal = DateTime.Now.AddMinutes(-5);
            pastLocal = new DateTime(
                pastLocal.Year,
                pastLocal.Month,
                pastLocal.Day,
                pastLocal.Hour,
                pastLocal.Minute,
                pastLocal.Second,
             DateTimeKind.Unspecified);
            scheduledInput.Text = "不能保存到过去";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                pastLocal.Date,
                true);
            scheduledTime.Text = pastLocal.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(editRequestedCount == 1 &&
                   ReferenceEquals(
                       GetRawField(todoWindow, "_editingScheduledTask"),
                       editItem) &&
                   validationText.Text.Contains("晚于现在", StringComparison.Ordinal) &&
                   editItem.Text == "要修改的定时任务",
                "编辑到过去时间必须保留编辑态、显示校验提示并且不得改变原任务");

            todoWindow.ShowDefaultTab();
            Assert(GetRawField(todoWindow, "_editingScheduledTask") is null &&
                   todoTab.IsChecked == true &&
                   scheduledInput.Text.Length == 0 &&
                   editRequestedCount == 1,
                "切回默认待办页必须取消未提交的定时任务修改，不能静默保存草稿");

            var recurringEditDueAt = DateTimeOffset.Now.AddDays(5);
            recurringEditDueAt = recurringEditDueAt.AddTicks(
                -(recurringEditDueAt.Ticks % TimeSpan.TicksPerSecond));
            var recurringEditLocal = recurringEditDueAt.LocalDateTime;
            var recurringEditInterval =
                TimeSpan.FromDays(1) +
                TimeSpan.FromHours(2) +
                TimeSpan.FromMinutes(30);
            var recurringEditItem = new ScheduledTaskItem
            {
                Id = Guid.NewGuid(),
                Text = "要修改的循环任务",
                DueAt = recurringEditDueAt,
                CreatedAt = DateTimeOffset.Now.AddMinutes(-3),
                RepeatInterval = recurringEditInterval
            };
            scheduledTasks.Add(recurringEditItem);
            Invoke(todoWindow, "SelectTaskPage", true, false);
            var recurringEditButton = new Button { Tag = recurringEditItem };
            Invoke(
                todoWindow,
                "ScheduledTaskEditButton_Click",
                recurringEditButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    recurringEditButton));
            Assert(ReferenceEquals(
                       GetRawField(todoWindow, "_editingScheduledTask"),
                       recurringEditItem) &&
                   scheduledInput.Text == recurringEditItem.Text &&
                   scheduledRepeatToggle.IsChecked == true &&
                   scheduledRepeatDays.Text == "1" &&
                   scheduledRepeatHours.Text == "2" &&
                   scheduledRepeatMinutes.Text == "30" &&
                   scheduledDatePickerHost.Visibility == Visibility.Visible &&
                   scheduledTimePickerHost.Visibility == Visibility.Visible &&
                   scheduledRepeatHint.Visibility == Visibility.Collapsed &&
                   scheduledDateInput.Text == recurringEditLocal.ToString(
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture) &&
                   scheduledTime.Text == recurringEditLocal.ToString(
                       "HH:mm:ss",
                       CultureInfo.InvariantCulture),
                "点击循环任务铅笔必须回填循环模式、天时分和可见的下一次日期时间");

            scheduledInput.Text = "  循环任务只改文案  ";
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(editRequestedCount == 2 &&
                   ReferenceEquals(
                       requestedEditItem,
                       recurringEditItem) &&
                   requestedEditText == "循环任务只改文案" &&
                   requestedEditDueAt == recurringEditDueAt &&
                   requestedEditRepeatInterval == recurringEditInterval,
                "循环任务只修改文案时必须保留原下一次到期时间和周期，重启后不能重新计时");

            Invoke(
                todoWindow,
                "ScheduledTaskEditButton_Click",
                recurringEditButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    recurringEditButton));
            var changedRecurringInterval =
                TimeSpan.FromHours(4) + TimeSpan.FromMinutes(5);
            scheduledInput.Text = "循环任务修改周期";
            scheduledRepeatDays.Text = "0";
            scheduledRepeatHours.Text = "4";
            scheduledRepeatMinutes.Text = "5";
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            Assert(editRequestedCount == 3 &&
                   ReferenceEquals(
                       requestedEditItem,
                       recurringEditItem) &&
                   requestedEditText == "循环任务修改周期" &&
                   requestedEditRepeatInterval == changedRecurringInterval &&
                   requestedEditDueAt.Ticks % TimeSpan.TicksPerSecond == 0 &&
                   requestedEditDueAt == recurringEditDueAt,
                "循环任务只修改间隔时必须保留已经选择的下一次 DueAt，不能从保存时刻偷偷重新计时");

            Invoke(
                todoWindow,
                "ScheduledTaskEditButton_Click",
                recurringEditButton,
                new RoutedEventArgs(
                    ButtonBase.ClickEvent,
                    recurringEditButton));
            var selectedNextLocal = DateTime.Now.AddDays(7);
            selectedNextLocal = new DateTime(
                selectedNextLocal.Year,
                selectedNextLocal.Month,
                selectedNextLocal.Day,
                16,
                47,
                28,
                DateTimeKind.Unspecified);
            while (TimeZoneInfo.Local.IsInvalidTime(selectedNextLocal))
            {
                selectedNextLocal = selectedNextLocal.AddHours(1);
            }

            scheduledInput.Text = "循环任务修改下次时间";
            Invoke(
                todoWindow,
                "SetScheduledDate",
                selectedNextLocal.Date,
                true);
            Invoke(
                todoWindow,
                "SetScheduledTimePickerSelection",
                selectedNextLocal.Hour,
                selectedNextLocal.Minute,
                selectedNextLocal.Second,
                true);
            Invoke(todoWindow, "RequestScheduledTaskSubmit");
            var expectedSelectedNextDueAt = new DateTimeOffset(
                selectedNextLocal,
                TimeZoneInfo.Local.GetUtcOffset(selectedNextLocal));
            Assert(editRequestedCount == 4 &&
                   ReferenceEquals(
                       requestedEditItem,
                       recurringEditItem) &&
                   requestedEditText == "循环任务修改下次时间" &&
                   requestedEditRepeatInterval == recurringEditInterval &&
                   requestedEditDueAt == expectedSelectedNextDueAt,
                "循环任务修改下一次日期和 HH:mm:ss 时，必须把该精确选择作为新的 DueAt");

            var deleteItem = new ScheduledTaskItem
            {
                Text = "可删除的定时任务",
                DueAt = requestedDueAt,
                CreatedAt = DateTimeOffset.Now
            };
            ScheduledTaskItem? requestedDelete = null;
            todoWindow.ScheduledTaskDeleteRequested += item =>
                requestedDelete = item;
            Invoke(
                todoWindow,
                "ScheduledTaskDeleteButton_Click",
                new Button { Tag = deleteItem },
                new RoutedEventArgs());
            Assert(ReferenceEquals(requestedDelete, deleteItem),
                "定时任务删除按钮必须传回当前绑定实例");
        }
        finally
        {
            todoWindow.CloseForApplication();
        }
    }

    private static void AssertScheduledTaskEditContract(MainWindow window)
    {
        var scheduledTasks = GetField<ObservableCollection<ScheduledTaskItem>>(
            window,
            "_scheduledTasks");
        var reminderQueue = GetField<Queue<ScheduledTaskItem>>(
            window,
            "_reminderQueue");
        var queuedReminderIds = GetField<HashSet<Guid>>(
            window,
            "_queuedReminderIds");
        var scheduledStore = GetField<ScheduledTaskStore>(
            window,
            "_scheduledTaskStore");
        var scheduledTimer = GetField<DispatcherTimer>(
            window,
            "_scheduledTaskTimer");
        var originalNowProvider = GetField<Func<DateTimeOffset>>(
            window,
            "_nowProvider");
        var now = new DateTimeOffset(
            2026,
            7,
            22,
            15,
            0,
            0,
            TimeSpan.FromHours(8));
        Func<DateTimeOffset> controlledNow = () => now;

        scheduledTimer.Stop();
        scheduledTasks.Clear();
        reminderQueue.Clear();
        queuedReminderIds.Clear();
        SetField(window, "_activeReminder", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_nowProvider", controlledNow);
        Assert(scheduledStore.Save(scheduledTasks),
            "定时任务编辑回归必须使用临时 ScheduledTaskStore");

        try
        {
            var first = new ScheduledTaskItem
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
                Text = "原本最早",
                DueAt = now.AddSeconds(20),
                CreatedAt = now.AddMinutes(-3).AddMilliseconds(111)
            };
            var middle = new ScheduledTaskItem
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                Text = "原本居中",
                DueAt = now.AddSeconds(30),
                CreatedAt = now.AddMinutes(-2).AddMilliseconds(222)
            };
            var moving = new ScheduledTaskItem
            {
                Id = Guid.Parse("20000000-0000-0000-0000-000000000003"),
                Text = "原本最晚",
                DueAt = now.AddSeconds(40),
                CreatedAt = now.AddMinutes(-1).AddMilliseconds(333)
            };
            foreach (var item in new[] { middle, moving, first })
            {
                Invoke(window, "InsertScheduledTaskSorted", item);
            }

            Assert(scheduledStore.Save(scheduledTasks),
                "编辑排序回归的初始任务必须成功持久化");
            var movingId = moving.Id;
            var movingCreatedAt = moving.CreatedAt;
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                moving,
                "  改到更早并修正文案  ",
                now.AddSeconds(10).AddMilliseconds(875),
                null);
            Assert(scheduledTasks.SequenceEqual([moving, first, middle]) &&
                   moving.Id == movingId &&
                   moving.CreatedAt == movingCreatedAt &&
                   moving.Text == "改到更早并修正文案" &&
                   moving.DueAt == now.AddSeconds(10) &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual([moving.Id, first.Id, middle.Id]) &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(8))
                       .TotalMilliseconds) < 1,
                "编辑到更早时间必须保留 Id/CreatedAt、Trim并归一到整秒，" +
                "同时重排内存/磁盘并重新对准到期前2秒预热点");

            var editedRepeatInterval =
                TimeSpan.FromDays(2) +
                TimeSpan.FromHours(3) +
                TimeSpan.FromMinutes(15);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                moving,
                "改到更晚",
                now.AddSeconds(50).AddMilliseconds(499),
                editedRepeatInterval);
            Assert(scheduledTasks.SequenceEqual([first, middle, moving]) &&
                   moving.Id == movingId &&
                   moving.CreatedAt == movingCreatedAt &&
                   moving.DueAt == now.AddSeconds(50) &&
                   moving.RepeatInterval == editedRepeatInterval &&
                   scheduledStore.Load().Select(item => item.Id)
                        .SequenceEqual([first.Id, middle.Id, moving.Id]) &&
                   scheduledStore.Load().Single(item => item.Id == moving.Id)
                       .RepeatInterval == editedRepeatInterval &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(18))
                       .TotalMilliseconds) < 1,
                "编辑到更晚循环时间必须持久化周期、移动到正确位置并把调度器切回最早任务的预热点");

            scheduledTimer.Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            var active = new ScheduledTaskItem
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
                Text = "正在显示的提醒",
                DueAt = now,
                CreatedAt = now.AddMinutes(-3)
            };
            var queuedFirst = new ScheduledTaskItem
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000002"),
                Text = "排队提醒甲",
                DueAt = now,
                CreatedAt = now.AddMinutes(-2)
            };
            var queuedSecond = new ScheduledTaskItem
            {
                Id = Guid.Parse("30000000-0000-0000-0000-000000000003"),
                Text = "排队提醒乙",
                DueAt = now,
                CreatedAt = now.AddMinutes(-1)
            };
            foreach (var item in new[] { active, queuedFirst, queuedSecond })
            {
                Invoke(window, "InsertScheduledTaskSorted", item);
            }

            SetField(window, "_activeReminder", active);
            SetField(window, "_isReminderActive", true);
            Invoke(window, "RebuildReminderQueueAt", now);
            Assert(reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   queuedReminderIds.SetEquals(
                       [active.Id, queuedFirst.Id, queuedSecond.Id]),
                "已到点的同秒任务必须先形成一个活动项和稳定顺序的等待队列");
            Assert(scheduledStore.Save(scheduledTasks),
                "排队编辑回归的初始状态必须成功持久化");

            var activeCreatedAt = active.CreatedAt;
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                active,
                "不允许覆盖正在显示的内容",
                now.AddMinutes(10),
                null);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       active) &&
                   active.Text == "正在显示的提醒" &&
                   active.DueAt == now &&
                   active.CreatedAt == activeCreatedAt &&
                   reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   scheduledStore.Load().Single(item => item.Id == active.Id).Text ==
                       "正在显示的提醒",
                "正在气泡中显示的任务必须拒绝编辑，避免画面、队列和磁盘内容分裂");

            var queuedFirstId = queuedFirst.Id;
            var queuedFirstCreatedAt = queuedFirst.CreatedAt;
            var queuedFirstOriginalText = queuedFirst.Text;
            var queuedFirstOriginalDueAt = queuedFirst.DueAt;
            var frozenBatchOrder = scheduledTasks.ToArray();
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                queuedFirst,
                "  排队任务延后  ",
                now.AddSeconds(30).AddMilliseconds(900),
                null);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       active) &&
                   queuedFirst.Id == queuedFirstId &&
                   queuedFirst.CreatedAt == queuedFirstCreatedAt &&
                   queuedFirst.Text == queuedFirstOriginalText &&
                   queuedFirst.DueAt == queuedFirstOriginalDueAt &&
                   scheduledTasks.SequenceEqual(frozenBatchOrder) &&
                   reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   queuedReminderIds.SetEquals(
                       [active.Id, queuedFirst.Id, queuedSecond.Id]) &&
                   !scheduledTimer.IsEnabled &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual(frozenBatchOrder.Select(item => item.Id)),
                "已进入可见或排队提醒批次的任务必须冻结修改，保持内存、队列与磁盘顺序一致");

            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                queuedFirst,
                "排队任务提前",
                now.AddSeconds(-1),
                null);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       active) &&
                   queuedFirst.Text == queuedFirstOriginalText &&
                   queuedFirst.DueAt == queuedFirstOriginalDueAt &&
                   scheduledTasks.SequenceEqual(frozenBatchOrder) &&
                   reminderQueue.SequenceEqual([queuedFirst, queuedSecond]) &&
                   queuedReminderIds.SetEquals(
                       [active.Id, queuedFirst.Id, queuedSecond.Id]) &&
                   queuedReminderIds.Count == 3 &&
                   !scheduledTimer.IsEnabled &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual(frozenBatchOrder.Select(item => item.Id)),
                "对冻结批次重复发起修改也不得改变内容、截止时间、顺序或产生重复Id");
        }
        finally
        {
            scheduledTimer.Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            scheduledStore.Save(scheduledTasks);
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_nowProvider", originalNowProvider);
        }
    }

    private static void AssertScheduledReminderBatchContract(MainWindow window)
    {
        var scheduledTasks = GetField<ObservableCollection<ScheduledTaskItem>>(
            window,
            "_scheduledTasks");
        var reminderQueue = GetField<Queue<ScheduledTaskItem>>(
            window,
            "_reminderQueue");
        var queuedReminderIds = GetField<HashSet<Guid>>(
            window,
            "_queuedReminderIds");
        var activeBatch = GetField<List<ScheduledTaskItem>>(
            window,
            "_activeReminderBatch");
        var scheduledStore = GetField<ScheduledTaskStore>(
            window,
            "_scheduledTaskStore");
        var scheduledTimer = GetField<DispatcherTimer>(
            window,
            "_scheduledTaskTimer");
        var automaticTimer = GetField<DispatcherTimer>(
            window,
            "_automaticTimer");
        var reminderSizeTimer = GetField<DispatcherTimer>(
            window,
            "_reminderSizeCommitTimer");
        var originalNowProvider = GetField<Func<DateTimeOffset>>(
            window,
            "_nowProvider");
        var originalAutomaticEnabled = GetField<bool>(
            window,
            "_automaticAnimationEnabled");
        var originalScale = GetField<double>(window, "_petSizeScale");
        var originalHitTestVisible = window.IsHitTestVisible;
        var now = new DateTimeOffset(
            2032,
            6,
            7,
            8,
            9,
            10,
            TimeSpan.FromHours(8));
        Func<DateTimeOffset> controlledNow = () => now;

        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var processSource = ExtractPrivateMethodSource(
            mainSource,
            "ProcessScheduledTasksAt");
        var refreshSource = ExtractPrivateMethodSource(
            mainSource,
            "RefreshActiveReminderPresentation");
        var moveSource = ExtractPrivateMethodSource(
            mainSource,
            "MovePetToReminderCorner");
        var acknowledgeSource = ExtractPrivateMethodSource(
            mainSource,
            "AcknowledgeActiveReminder");
        var rebuildQueueSource = ExtractPrivateMethodSource(
            mainSource,
            "RebuildReminderQueueAt");
        var systemTimeChangedSource = ExtractPrivateMethodSource(
            mainSource,
            "ProcessSystemTimeChanged");
        Assert(processSource.Contains(
                   "RefreshActiveReminderPresentation(now)",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_activeReminderBatch.Sort(CompareScheduledTasks)",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "string.Join(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "Environment.NewLine",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "M月d日 HH:mm:ss",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_reminderMissedOccurrenceCounts.TryAdd(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_reminderMissedOccurrenceCounts.GetValueOrDefault(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "_activeReminderBatch.RemoveAll(",
                   StringComparison.Ordinal) &&
               refreshSource.Contains(
                   "ReminderAcknowledgeButton.Content",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "StopEdgeRoaming(",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "immediate: true",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "workArea.Right - width",
                   StringComparison.Ordinal) &&
               moveSource.Contains(
                   "workArea.Bottom - height",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "_activeReminderBatch.ToArray()",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "foreach (var item in acknowledged)",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "_reminderMissedOccurrenceCounts.Remove(item.Id)",
                   StringComparison.Ordinal) &&
               acknowledgeSource.Contains(
                   "SaveScheduledTasks()",
                   StringComparison.Ordinal) &&
               rebuildQueueSource.Contains(
                   "foreach (var displayedItem in _activeReminderBatch)",
                   StringComparison.Ordinal) &&
               systemTimeChangedSource.Contains(
                   "ProcessScheduledTasksAt(_nowProvider())",
                   StringComparison.Ordinal),
            "到点提醒必须抢占绕屏、移动到右下角，并把同批任务稳定排序合并到一个泡泡和一次确认中");

        scheduledTimer.Stop();
        automaticTimer.Stop();
        reminderSizeTimer.Stop();
        scheduledTasks.Clear();
        reminderQueue.Clear();
        queuedReminderIds.Clear();
        activeBatch.Clear();
        SetField(window, "_activeReminder", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_upcomingReminderPreloadPageName", null);
        SetField(window, "_nowProvider", controlledNow);
        SetField(window, "_automaticAnimationEnabled", false);
        window.IsHitTestVisible = false;

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
            }

            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "HideBubbleVisuals");
            SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));

            var firstByDue = new ScheduledTaskItem
            {
                Id = Guid.Parse("41000000-0000-0000-0000-000000000003"),
                Text = "最早到点",
                DueAt = now.AddSeconds(-2),
                CreatedAt = now.AddMinutes(-1)
            };
            var firstByCreated = new ScheduledTaskItem
            {
                Id = Guid.Parse("41000000-0000-0000-0000-000000000002"),
                Text = "同秒先创建",
                DueAt = now.AddSeconds(-1),
                CreatedAt = now.AddMinutes(-3)
            };
            var firstById = new ScheduledTaskItem
            {
                Id = Guid.Parse("41000000-0000-0000-0000-000000000001"),
                Text = "同秒按编号",
                DueAt = now.AddSeconds(-1),
                CreatedAt = now.AddMinutes(-3)
            };
            foreach (var item in new[]
                     {
                         firstByCreated,
                         firstByDue,
                         firstById
                     })
            {
                Invoke(window, "InsertScheduledTaskSorted", item);
            }

            var expectedOrder = new[]
            {
                firstByDue,
                firstById,
                firstByCreated
            };
            Assert(scheduledTasks.SequenceEqual(expectedOrder) &&
                   scheduledStore.Save(scheduledTasks),
                "提醒合并测试数据必须先按 DueAt、CreatedAt、Id 建立稳定顺序");

            var monitorType = typeof(MainWindow).Assembly.GetType(
                "LubanDesktopPet.MonitorWorkArea",
                throwOnError: true)!;
            var workArea = (Rect)InvokeStatic(
                monitorType,
                "GetForWindow",
                window)!;
            window.Left = workArea.Left +
                          Math.Max(10, workArea.Width * 0.2);
            window.Top = workArea.Top +
                         Math.Max(10, workArea.Height * 0.2);
            Invoke(window, "ApplyPetSizeScale", 1d, false, false);
            var sizePreviewTimestamp = Stopwatch.GetTimestamp();
            Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
            Invoke(
                window,
                "QueuePetSizeScaleTargetAt",
                1.18d,
                sizePreviewTimestamp);
            Invoke(
                window,
                "ConsumeLatestPetSizeInputAt",
                sizePreviewTimestamp +
                StopwatchTicksFromMilliseconds(1));
            Assert(GetField<bool>(
                       window,
                       "_isPetSizePreviewSessionActive"),
                "提醒抢占回归必须先建立一个尚未提交的尺寸预览会话");
            SetField(window, "_isEdgeRoaming", true);
            SetField(
                window,
                "_edgeRoamPhase",
                GetNestedEnum("EdgeRoamPhase", "Traveling"));
            SetField(window, "_edgeRoamRotationDegrees", 90d);
            GetField<RotateTransform>(window, "PetRoamRotate").Angle = 90;

            Invoke(window, "ProcessScheduledTasksAt", now);
            var reminderMessage = GetField<TextBox>(
                window,
                "ReminderMessageText");
            var acknowledgeButton = GetField<Button>(
                window,
                "ReminderAcknowledgeButton");
            var expectedMessage = string.Join(
                Environment.NewLine,
                expectedOrder.Select(item =>
                    $"{item.DueAt.ToLocalTime():M月d日 HH:mm:ss}  {item.Text}"));
            var width = window.ActualWidth > 0
                ? window.ActualWidth
                : window.Width;
            var height = window.ActualHeight > 0
                ? window.ActualHeight
                : window.Height;
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       expectedOrder[0]) &&
                   activeBatch.SequenceEqual(expectedOrder) &&
                   reminderQueue.SequenceEqual(expectedOrder.Skip(1)) &&
                   queuedReminderIds.SetEquals(
                       expectedOrder.Select(item => item.Id)) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   !GetField<bool>(
                       window,
                       "_isPetSizeAdjustmentActive") &&
                   GetField<bool>(
                       window,
                       "_isTransientPetSizeOverride") &&
                   Math.Abs(
                       GetField<double>(window, "_reminderRestoreScale") -
                       1.18d) < 0.001 &&
                   !GetField<bool>(window, "_isEdgeRoaming") &&
                   GetField<object>(window, "_edgeRoamPhase").ToString() ==
                       "None" &&
                   Math.Abs(
                       GetField<RotateTransform>(
                           window,
                           "PetRoamRotate").Angle) < 0.001 &&
                   GetField<object>(window, "_bubbleMode").ToString() ==
                       "Reminder" &&
                   reminderMessage.Text == expectedMessage &&
                   reminderMessage.IsReadOnly &&
                   acknowledgeButton.Content?.ToString()?.Contains(
                       expectedOrder.Length.ToString(
                           CultureInfo.InvariantCulture),
                       StringComparison.Ordinal) == true &&
                   Math.Abs(window.Left - (workArea.Right - width)) <= 1 &&
                   Math.Abs(window.Top - (workArea.Bottom - height)) <= 1,
                "三个到点任务必须抢占绕屏、清零旋转、移到当前屏幕右下角，并按稳定顺序合并为一个可复制泡泡；" +
                $"active={GetRawField(window, "_activeReminder") is not null}, " +
                $"batch={activeBatch.Count}, queue={reminderQueue.Count}, ids={queuedReminderIds.Count}, " +
                $"reminder={GetField<bool>(window, "_isReminderActive")}, " +
                $"preview={GetField<bool>(window, "_isPetSizePreviewSessionActive")}, " +
                $"roam={GetField<bool>(window, "_isEdgeRoaming")}, " +
                $"phase={GetField<object>(window, "_edgeRoamPhase")}, " +
                $"angle={GetField<RotateTransform>(window, "PetRoamRotate").Angle:F2}, " +
                $"bubble={GetField<object>(window, "_bubbleMode")}, " +
                $"textMatch={reminderMessage.Text == expectedMessage}, " +
                $"button={acknowledgeButton.Content}, " +
                $"position=({window.Left:F2},{window.Top:F2}), " +
                $"target=({workArea.Right - width:F2},{workArea.Bottom - height:F2})");

            var frozenTexts = expectedOrder
                .Select(item => item.Text)
                .ToArray();
            var frozenDueTimes = expectedOrder
                .Select(item => item.DueAt)
                .ToArray();
            Invoke(
                window,
                "TodoWindow_ScheduledTaskEditRequested",
                expectedOrder[1],
                "不允许覆盖已展示批次",
                now.AddHours(1),
                null);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskDeleteRequested",
                expectedOrder[2]);
            Assert(scheduledTasks.SequenceEqual(expectedOrder) &&
                   activeBatch.SequenceEqual(expectedOrder) &&
                   expectedOrder.Select(item => item.Text)
                       .SequenceEqual(frozenTexts) &&
                   expectedOrder.Select(item => item.DueAt)
                       .SequenceEqual(frozenDueTimes) &&
                   scheduledStore.Load().Select(item => item.Id)
                       .SequenceEqual(expectedOrder.Select(item => item.Id)),
                "进入可见或排队提醒批次后，修改和删除都必须冻结，不能让气泡、队列与磁盘状态分裂");

            reminderMessage.SelectAll();
            Assert(reminderMessage.SelectedText == expectedMessage,
                "合并提醒泡泡的全部文本必须支持一次选中复制");

            now = now.AddMinutes(-10);
            Invoke(window, "ProcessSystemTimeChanged");
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       expectedOrder[0]) &&
                   activeBatch.SequenceEqual(expectedOrder) &&
                   queuedReminderIds.SetEquals(
                       expectedOrder.Select(item => item.Id)) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   reminderMessage.Text == expectedMessage,
                "系统时间回拨到批次到点前，已经展示的合并提醒不得消失、拆批或重新排序");

            Invoke(window, "AcknowledgeActiveReminder");
            Assert(GetRawField(window, "_activeReminder") is null &&
                   activeBatch.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   scheduledTasks.Count == 0 &&
                   scheduledStore.Load().Count == 0 &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   GetField<object>(window, "_bubbleMode").ToString() ==
                       "None",
                "一次确认必须原子删除合并批次的全部任务，只保存一次并关闭唯一提醒泡泡");

            now = new DateTimeOffset(
                2032,
                6,
                10,
                12,
                0,
                0,
                TimeSpan.FromHours(8));
            var recurringInterval = TimeSpan.FromHours(6);
            var missedRecurringDueAt = now.AddHours(-55);
            var missedRecurring = new ScheduledTaskItem
            {
                Id = Guid.Parse(
                    "42000000-0000-0000-0000-000000000001"),
                Text = "重启后继续的循环提醒",
                DueAt = missedRecurringDueAt,
                CreatedAt = missedRecurringDueAt.AddDays(-1),
                RepeatInterval = recurringInterval
            };
            Invoke(window, "InsertScheduledTaskSorted", missedRecurring);
            Assert(scheduledStore.Save(scheduledTasks),
                "漏提醒循环回归必须先把原锚点和周期持久化");
            Invoke(window, "ProcessScheduledTasksAt", now);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(
                           window,
                           "_activeReminder"),
                       missedRecurring) &&
                   activeBatch.SequenceEqual([missedRecurring]) &&
                   reminderMessage.Text.Contains(
                       "每6小时",
                       StringComparison.Ordinal) &&
                   reminderMessage.Text.Contains(
                       "已错过 10 次",
                       StringComparison.Ordinal),
                "重启加载到逾期循环任务时必须只弹一次，并显示从原锚点计算的漏提醒次数");

            var expectedNextDueAt =
                missedRecurringDueAt.AddHours(60);
            Invoke(window, "AcknowledgeActiveReminder");
            var persistedRecurring = scheduledStore.Load().Single();
            Assert(scheduledTasks.Count == 1 &&
                   ReferenceEquals(scheduledTasks[0], missedRecurring) &&
                   missedRecurring.DueAt == expectedNextDueAt &&
                   missedRecurring.DueAt > now &&
                   missedRecurring.RepeatInterval == recurringInterval &&
                   persistedRecurring.Id == missedRecurring.Id &&
                   persistedRecurring.DueAt == expectedNextDueAt &&
                   persistedRecurring.RepeatInterval == recurringInterval &&
                   GetRawField(window, "_activeReminder") is null &&
                   activeBatch.Count == 0 &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   scheduledTimer.IsEnabled,
                "确认漏提醒后必须从原到期锚点一次跨过全部遗漏周期，持久化首个未来时间且不补播历史提醒");
        }
        finally
        {
            scheduledTimer.Stop();
            automaticTimer.Stop();
            reminderSizeTimer.Stop();
            GetField<DispatcherTimer>(window, "_petSizePersistTimer").Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            activeBatch.Clear();
            scheduledStore.Save(scheduledTasks);
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_upcomingReminderPreloadPageName", null);
            SetField(window, "_isEdgeRoaming", false);
            SetField(
                window,
                "_edgeRoamPhase",
                GetNestedEnum("EdgeRoamPhase", "None"));
            SetField(window, "_isTransientPetSizeOverride", false);
            SetField(window, "_isRestoringReminderSize", false);
            Invoke(window, "HideBubbleVisuals");
            SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "ClearDeferredActiveClipClock");
            Invoke(window, "ResetPetVisualTransforms");
            Invoke(window, "ApplyPetSizeScale", originalScale, false, false);
            SetField(window, "_nowProvider", originalNowProvider);
            SetField(
                window,
                "_automaticAnimationEnabled",
                originalAutomaticEnabled);
            if (originalAutomaticEnabled && window.IsVisible)
            {
                Invoke(window, "RestartAutomaticCountdown");
            }

            window.IsHitTestVisible = originalHitTestVisible;
        }
    }

    private static void AssertScheduledReminderContract(MainWindow window)
    {
        const double baselineScale = 1.13;
        const double maximumScale = 1.40;
        var scheduledTasks = GetField<ObservableCollection<ScheduledTaskItem>>(
            window,
            "_scheduledTasks");
        var reminderQueue = GetField<Queue<ScheduledTaskItem>>(
            window,
            "_reminderQueue");
        var queuedReminderIds = GetField<HashSet<Guid>>(
            window,
            "_queuedReminderIds");
        var scheduledStore = GetField<ScheduledTaskStore>(
            window,
            "_scheduledTaskStore");
        var settingsStore = GetField<AppSettingsStore>(window, "_settingsStore");
        var scheduledTimer = GetField<DispatcherTimer>(
            window,
            "_scheduledTaskTimer");
        var reminderSizeTimer = GetField<DispatcherTimer>(
            window,
            "_reminderSizeCommitTimer");
        var automaticTimer = GetField<DispatcherTimer>(window, "_automaticTimer");
        var originalNowProvider = GetField<Func<DateTimeOffset>>(
            window,
            "_nowProvider");
        var originalAutomaticAnimationEnabled = GetField<bool>(
            window,
            "_automaticAnimationEnabled");
        var reminderPreloadLeadTime = (TimeSpan)(typeof(MainWindow).GetField(
                "ReminderSpritePreloadLeadTime",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var originalHitTestVisible = window.IsHitTestVisible;
        var currentNow = new DateTimeOffset(
            2026,
            7,
            22,
            12,
            0,
            0,
            TimeSpan.FromHours(8));
        Func<DateTimeOffset> controlledNow = () => currentNow;

        scheduledTimer.Stop();
        reminderSizeTimer.Stop();
        automaticTimer.Stop();
        scheduledTasks.Clear();
        reminderQueue.Clear();
        queuedReminderIds.Clear();
        SetField(window, "_activeReminder", null);
        SetField(window, "_isReminderActive", false);
        SetField(window, "_upcomingReminderPreloadPageName", null);
        SetField(window, "_nowProvider", controlledNow);
        SetField(window, "_automaticAnimationEnabled", false);
        window.IsHitTestVisible = false;
        Assert(scheduledStore.Save(scheduledTasks),
            "提醒回归必须使用临时 ScheduledTaskStore");

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(40));
            }

            automaticTimer.Stop();
            SetField(window, "_automaticAnimationEnabled", false);
            Invoke(window, "ApplyPetSizeScale", baselineScale, true, false);
            AssertClose(
                settingsStore.Load().PetSizeScale,
                baselineScale,
                "定时提醒前的用户尺寸设置");

            var reminderEnterClip = GetField<object>(window, "_reminderEnterClip");
            var reminderHoldClip = GetField<object>(window, "_reminderHoldClip");
            var reminderExitClip = GetField<object>(window, "_reminderExitClip");
            var enterFrames = GetClipFrames(reminderEnterClip);
            var holdFrames = GetClipFrames(reminderHoldClip);
            var exitFrames = GetClipFrames(reminderExitClip);
            Assert(GetProperty<string>(reminderEnterClip, "ActionName") ==
                   "reminder-open" &&
                   GetProperty<string>(reminderHoldClip, "ActionName") ==
                   "reminder-hold" &&
                   GetProperty<string>(reminderExitClip, "ActionName") ==
                   "reminder-close" &&
                   !ReferenceEquals(reminderEnterClip, reminderExitClip) &&
                   !ReferenceEquals(reminderEnterClip, reminderHoldClip) &&
                   !ReferenceEquals(reminderHoldClip, reminderExitClip) &&
                   enterFrames.Length == 33 &&
                   holdFrames.Length == 48 &&
                   exitFrames.Length == 33 &&
                   GetProperty<int>(reminderEnterClip, "ActionFrameIndex") == 0 &&
                   GetProperty<int>(reminderHoldClip, "ActionFrameIndex") == 0 &&
                   GetProperty<int>(reminderExitClip, "ActionFrameIndex") == 0,
                "定时提醒必须使用独立的33帧入场、48帧播报保持和33帧退场clip");
            var motionFrameInterval = (TimeSpan)(
                typeof(MainWindow).GetField(
                    "MotionFrameInterval",
                    StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
            Assert(motionFrameInterval == TimeSpan.FromTicks(
                       TimeSpan.TicksPerSecond / 60) &&
                   reminderPreloadLeadTime == TimeSpan.FromSeconds(2) &&
                   enterFrames.Cast<object>()
                        .All(frame =>
                            GetFrameDuration(frame) == motionFrameInterval) &&
                   holdFrames.Cast<object>()
                        .All(frame =>
                            GetFrameDuration(frame) == motionFrameInterval) &&
                   exitFrames.Cast<object>()
                        .All(frame =>
                            GetFrameDuration(frame) == motionFrameInterval),
                "提醒入场、播报保持和退场必须统一使用60fps绝对时间帧时长，" +
                "并只在到期前2秒按需预热");

            for (var frameIndex = 0; frameIndex < enterFrames.Length; frameIndex++)
            {
                var enterImage = GetProperty<object>(
                    enterFrames.GetValue(frameIndex)!,
                    "Image");
                var exitImage = GetProperty<object>(
                    exitFrames.GetValue(exitFrames.Length - 1 - frameIndex)!,
                    "Image");
                var enterInfo = GetSpriteFrameInfo(enterImage);
                Assert(enterInfo.PageName.StartsWith(
                           "action-reminder-enter",
                           StringComparison.Ordinal) &&
                       enterInfo.Name.EndsWith(
                           $"luban-reminder-enter-{frameIndex + 1:000}.png",
                           StringComparison.Ordinal) &&
                       Equals(enterImage, exitImage),
                    $"提醒入场第{frameIndex + 1}帧必须来自专用action-reminder-enter序列，" +
                    "退场必须直接复用同一SpriteFrame的倒序，不能复制第二套图或混入wave");
            }

            for (var frameIndex = 0; frameIndex < holdFrames.Length; frameIndex++)
            {
                var holdInfo = GetSpriteFrameInfo(GetProperty<object>(
                    holdFrames.GetValue(frameIndex)!,
                    "Image"));
                Assert(holdInfo.PageName.StartsWith(
                           "action-reminder-hold",
                           StringComparison.Ordinal) &&
                       holdInfo.Name.EndsWith(
                           $"luban-reminder-hold-{frameIndex + 1:000}.png",
                           StringComparison.Ordinal),
                    $"提醒播报第{frameIndex + 1}帧必须来自专用action-reminder-hold序列");
            }

            var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
            var mainXaml = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml"));
            var createReminderEnterSource = ExtractPrivateMethodSource(
                mainSource,
                "CreateReminderEnterClip");
            var createReminderHoldSource = ExtractPrivateMethodSource(
                mainSource,
                "CreateReminderHoldClip");
            var createReminderExitSource = ExtractPrivateMethodSource(
                mainSource,
                "CreateReminderExitClip");
            foreach (var reminderClipSource in new[]
                     {
                         createReminderEnterSource,
                         createReminderHoldSource,
                         createReminderExitSource
                     })
            {
                Assert(!reminderClipSource.Contains("BuildActionTimeline", StringComparison.Ordinal) &&
                       !reminderClipSource.Contains("\"wave\"", StringComparison.Ordinal),
                    "专用提醒clip不得再次复用普通wave时间轴或wave素材");
            }

            Assert(createReminderExitSource.Contains(
                       "_reminderEnterFrames",
                       StringComparison.Ordinal) &&
                   createReminderExitSource.Contains(
                       "reverse: true",
                       StringComparison.Ordinal) &&
                   !mainSource.Contains("ReminderMotionFrameInterval", StringComparison.Ordinal) &&
                   !mainSource.Contains("ShowReminderMegaphoneAt", StringComparison.Ordinal) &&
                   !mainSource.Contains("AdvanceReminderMegaphoneAnimation", StringComparison.Ordinal) &&
                   !mainSource.Contains("_isReminderMegaphoneAnimating", StringComparison.Ordinal) &&
                   typeof(MainWindow).GetField(
                       "ReminderMegaphone",
                       InstanceFlags) is null &&
                   !mainXaml.Contains("ReminderMegaphone", StringComparison.Ordinal) &&
                   !mainXaml.Contains("MegaphonePulseScale", StringComparison.Ordinal) &&
                   !mainXaml.Contains("MegaphoneSoundWave", StringComparison.Ordinal),
                "喇叭必须烘焙进专用人物帧；不得保留独立矢量贴层、正弦漂浮动画或1/180秒旧时钟");

            AssertProductionDiscreteVsyncTimeline(
                window,
                reminderEnterClip,
                "reminder-open-60fps");
            AssertProductionDiscreteVsyncTimeline(
                window,
                reminderHoldClip,
                "reminder-hold-60fps");
            AssertProductionDiscreteVsyncTimeline(
                window,
                reminderExitClip,
                "reminder-close-60fps");

            var dueAt = currentNow.AddSeconds(10);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "同秒提醒甲",
                dueAt,
                null);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "同秒提醒乙",
                dueAt,
                null);
            Assert(scheduledTasks.Count == 2 &&
                   scheduledTasks.All(item => item.DueAt == dueAt) &&
                   GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive"),
                "两条同秒定时任务在到点前必须只持久化，不得提前提醒");
            Assert(scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(8))
                       .TotalMilliseconds) < 1,
                "距到期10秒时调度器必须先对准到期前2秒的预热点，不做高频轮询");
            Assert(GetRawField(window, "_upcomingReminderPreloadPageName") is null &&
                   (TimeSpan)InvokeStatic(
                       typeof(MainWindow),
                       "CalculateReminderWakeDelay",
                       currentNow,
                       dueAt)! == TimeSpan.FromSeconds(8) &&
                   (TimeSpan)InvokeStatic(
                       typeof(MainWindow),
                       "CalculateReminderWakeDelay",
                       dueAt.AddSeconds(-1.5),
                       dueAt)! == TimeSpan.FromSeconds(1.5) &&
                   (TimeSpan)InvokeStatic(
                       typeof(MainWindow),
                       "CalculateReminderWakeDelay",
                       currentNow,
                       currentNow.AddHours(13))! == TimeSpan.FromHours(12),
                "提醒唤醒延迟必须在2秒窗口外减去预热提前量、窗口内直达截止秒，" +
                "并保留12小时上限");
            var expectedSameSecondOrder = scheduledTasks.ToArray();
            Assert(expectedSameSecondOrder[0].CreatedAt <=
                   expectedSameSecondOrder[1].CreatedAt &&
                   (expectedSameSecondOrder[0].CreatedAt <
                        expectedSameSecondOrder[1].CreatedAt ||
                    expectedSameSecondOrder[0].Id.CompareTo(
                        expectedSameSecondOrder[1].Id) < 0),
                "同秒任务必须按 CreatedAt / Id 稳定排序");
            Assert(scheduledStore.Load().Select(item => item.Id)
                    .SequenceEqual(expectedSameSecondOrder.Select(item => item.Id)),
                "同秒任务的稳定顺序必须与磁盘持久化顺序一致");

            var reminderPreloadPageName = GetSpriteFrameInfo(
                GetProperty<object>(enterFrames.GetValue(0)!, "Image")).PageName;
            currentNow = dueAt - reminderPreloadLeadTime;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            WaitForSpritePagePrefetchToSettle(window);
            Assert(string.Equals(
                       GetField<string>(window, "_upcomingReminderPreloadPageName"),
                       reminderPreloadPageName,
                       StringComparison.Ordinal) &&
                   GetField<IDictionary>(window, "_residentSpritePages")
                       .Contains(reminderPreloadPageName) &&
                   (bool)Invoke(
                       window,
                       "IsSpritePageProtected",
                       reminderPreloadPageName,
                       null)! &&
                   !(bool)Invoke(window, "CanRunIdleSpritePageCollection")! &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - reminderPreloadLeadTime)
                       .TotalMilliseconds) < 1,
                "到期前2秒必须只预取提醒首屏分页、动态保护该页，" +
                "并阻止Gen2回收直至到期");

            currentNow = dueAt.AddTicks(-1);
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(GetRawField(window, "_activeReminder") is null &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   string.Equals(
                       GetField<string>(window, "_upcomingReminderPreloadPageName"),
                       reminderPreloadPageName,
                       StringComparison.Ordinal),
                "到点前 1 tick 仍不得触发定时任务，预热页保护必须保持到截止边界");

            currentNow = dueAt;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            var firstActive = GetField<ScheduledTaskItem>(
                window,
                "_activeReminder");
            Assert(ReferenceEquals(firstActive, expectedSameSecondOrder[0]) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   reminderQueue.Count == 1 &&
                   queuedReminderIds.Count == 2 &&
                   GetRawField(window, "_upcomingReminderPreloadPageName") is null &&
                   enterFrames.Cast<object>()
                       .Concat(holdFrames.Cast<object>())
                       .Select(frame => GetSpriteFrameInfo(
                           GetProperty<object>(frame, "Image")).PageName)
                       .Distinct(StringComparer.Ordinal)
                       .All(pageName => (bool)Invoke(
                           window,
                           "IsSpritePageProtected",
                           pageName,
                           null)!),
                "整秒边界必须立即显示稳定顺序的第一条，其余同秒任务只入队一次");
            Assert(GetRawField(window, "_activeClip") is { } activeReminderClip &&
                   ReferenceEquals(activeReminderClip, reminderEnterClip) &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Reminder",
                "到点后必须切换 BubbleMode.Reminder 并启动 reminder-open clip");

            var reminderBubble = GetField<Border>(window, "ReminderBubble");
            var reminderMessage = GetField<TextBox>(window, "ReminderMessageText");
            var reminderButton = GetField<Button>(
                window,
                "ReminderAcknowledgeButton");
            Assert(reminderBubble.Visibility == Visibility.Visible &&
                   GetField<Popup>(window, "BubblePopup").IsOpen &&
                   reminderMessage.Text == firstActive.Text &&
                   reminderMessage.IsReadOnly &&
                   Equals(reminderButton.Content, "知道啦"),
                "Reminder 模式必须显示可选中内容、可爱气泡和“知道啦”确认按钮");
            reminderMessage.SelectAll();
            Assert(reminderMessage.SelectedText == firstActive.Text,
                "提醒对话框内容必须可选中复制");
            reminderMessage.Select(0, 0);
            var firstReminderSprite = GetProperty<object>(
                enterFrames.GetValue(0)!,
                "Image");
            var activeReminderFrameIndex = GetField<int>(window, "_activeFrameIndex");
            var currentReminderSprite = GetRawField(window, "_currentSpriteFrame");
            var isReminderFrameBlending = GetField<bool>(window, "_isFrameBlending");
            var pendingReminderSprite = GetRawField(window, "_pendingSpriteFrame");
            var expectedVisibleReminderSprite =
                activeReminderFrameIndex is >= 0 and <= 1
                    ? GetProperty<object>(
                        enterFrames.GetValue(activeReminderFrameIndex)!,
                        "Image")
                    : null;
            Assert(activeReminderFrameIndex is >= 0 and <= 1 &&
                   Equals(currentReminderSprite, expectedVisibleReminderSprite) &&
                   !isReminderFrameBlending &&
                   pendingReminderSprite is null,
                "提醒到点后的首个可见姿势必须直接来自专用烘焙喇叭序列，" +
                "允许断言前合成器自然前进一帧，但不得跳过更多姿势、整图淡化、" +
                "叠加旧贴层或留下待补播帧；" +
                $"index={activeReminderFrameIndex}, " +
                $"firstMatches={Equals(currentReminderSprite, firstReminderSprite)}, " +
                $"currentMatches={Equals(currentReminderSprite, expectedVisibleReminderSprite)}, " +
                $"blending={isReminderFrameBlending}, " +
                $"pending={pendingReminderSprite is not null}");

            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       firstActive) &&
                   reminderQueue.Count == 1 &&
                   queuedReminderIds.Count == 2,
                "在同一整秒重复执行调度不得重复入队或覆盖正在显示的提醒");

            // The production clock calls PrefetchNextClipPage at each displayed
            // frame boundary. This focused test jumps directly to clip
            // completion, so synchronously prime the same four entry/hold pages
            // before asserting the 33 -> 48 transition. The earlier assertions
            // still prove these pages were neither pinned nor resident by
            // startup warm-up, while _isReminderActive dynamically protects
            // them from eviction during the reminder.
            PrimeAllClipPagesForTest(window, enterFrames);
            PrimeAllClipPagesForTest(window, holdFrames);
            var activeReminderPageNames = enterFrames.Cast<object>()
                .Concat(holdFrames.Cast<object>())
                .Select(frame => GetSpriteFrameInfo(
                    GetProperty<object>(frame, "Image")).PageName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert(activeReminderPageNames.Length == 4 &&
                   activeReminderPageNames.All(pageName =>
                       GetField<IDictionary>(window, "_residentSpritePages")
                           .Contains(pageName)) &&
                   activeReminderPageNames.All(pageName => (bool)Invoke(
                       window,
                       "IsSpritePageProtected",
                       pageName,
                       null)!),
                "模拟逐页PrefetchNextClipPage完成后，提醒33帧入场与48帧保持的四页" +
                "必须全部resident，并在活动提醒期间动态保护");

            Invoke(window, "CompleteActiveClip", reminderEnterClip);
            Assert(ReferenceEquals(
                       GetRawField(window, "_activeClip"),
                       reminderHoldClip) &&
                   GetField<int>(window, "_activeFrameIndex") == 0 &&
                   Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       GetProperty<object>(holdFrames.GetValue(0)!, "Image")),
                "33帧专用入场完成后必须无缝衔接48帧专用播报动作的第一帧");
            Invoke(window, "CompleteActiveClip", reminderHoldClip);
            Assert(GetRawField(window, "_activeClip") is null &&
                   GetField<int>(window, "_activeFrameIndex") == -1 &&
                   Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       GetProperty<object>(holdFrames.GetValue(holdFrames.Length - 1)!, "Image")) &&
                   !GetField<bool>(window, "_isFrameBlending"),
                "48帧播报完成后必须定格在烘焙喇叭末姿势，不得循环漂浮、闪回或整图淡化");

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            AssertClose(GetField<double>(window, "_petSizeScale"), maximumScale,
                "提醒触发后的临时最大显示尺寸");
            Assert(GetField<bool>(window, "_isTransientPetSizeOverride") &&
                   !GetField<DispatcherTimer>(window, "_petSizePersistTimer").IsEnabled,
                "定时提醒放大必须使用 transient override，不得启动用户设置落盘计时器");
            AssertClose(
                settingsStore.Load().PetSizeScale,
                baselineScale,
                "临时放大到 140% 时不得覆盖用户尺寸设置");

            Invoke(window, "AcknowledgeActiveReminder");
            var secondActive = GetField<ScheduledTaskItem>(
                window,
                "_activeReminder");
            var persistedAfterFirstAcknowledge = scheduledStore.Load();
            Assert(ReferenceEquals(secondActive, expectedSameSecondOrder[1]) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Reminder" &&
                   ReferenceEquals(GetRawField(window, "_activeClip"), reminderHoldClip) &&
                   scheduledTasks.Count == 1 &&
                   persistedAfterFirstAcknowledge.Count == 1 &&
                   persistedAfterFirstAcknowledge[0].Id == secondActive.Id,
                "确认第一条后必须只删除已确认任务并持久化，随即用专用hold动作显示第二条");
            AssertClose(GetField<double>(window, "_petSizeScale"), maximumScale,
                "同秒提醒队列未清空前应继续保持最大尺寸");

            Invoke(window, "AcknowledgeActiveReminder");
            Assert(GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   scheduledTasks.Count == 0 &&
                   scheduledStore.Load().Count == 0 &&
                   GetField<object>(window, "_bubbleMode").ToString() == "None" &&
                   ReferenceEquals(GetRawField(window, "_activeClip"), reminderExitClip),
                "最后一条确认后必须清空并持久化队列、关闭气泡并启动 reminder-close clip");
            Assert(GetField<bool>(window, "_isTransientPetSizeOverride") &&
                   GetField<bool>(window, "_isRestoringReminderSize") &&
                   Math.Abs(GetField<double>(window, "_pendingPetSizeTargetScale") -
                            baselineScale) < 0.0001,
                "最后一条确认后必须平滑返回提醒前的尺寸目标");
            Assert(Equals(
                       GetRawField(window, "_currentSpriteFrame"),
                       GetProperty<object>(exitFrames.GetValue(0)!, "Image")) &&
                   !GetField<bool>(window, "_isFrameBlending"),
                "reminder-close 必须从烘焙喇叭入场序列的末姿势直接倒放，不能闪回或整图淡化");

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            AssertClose(GetField<double>(window, "_petSizeScale"), baselineScale,
                "提醒队列清空后的最终显示尺寸");
            AssertClose(GetField<double>(window, "_petSizeTargetScale"), baselineScale,
                "提醒队列清空后的最终尺寸目标");
            Assert(!GetField<bool>(window, "_isTransientPetSizeOverride") &&
                   !GetField<bool>(window, "_isRestoringReminderSize"),
                "尺寸恢复完成后必须清理 transient override 状态");
            AssertClose(
                settingsStore.Load().PetSizeScale,
                baselineScale,
                "提醒完整结束后用户设置仍应保持原值");

            Invoke(window, "CompleteActiveClip", reminderExitClip);
            currentNow = dueAt.AddSeconds(30);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "应用恢复后的逾期提醒",
                currentNow.AddSeconds(-5),
                null);
            var overdueActive = GetField<ScheduledTaskItem>(
                window,
                "_activeReminder");
            Assert(overdueActive.Text == "应用恢复后的逾期提醒" &&
                   overdueActive.DueAt < currentNow &&
                   GetField<bool>(window, "_isReminderActive") &&
                   GetField<object>(window, "_bubbleMode").ToString() == "Reminder",
                "新增或应用恢复时发现的逾期任务必须立即触发，不等下一个轮询周期");
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       overdueActive) &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 1,
                "逾期任务立即触发后重复校时也不得重复入队");
            Invoke(window, "AcknowledgeActiveReminder");
            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Assert(scheduledTasks.Count == 0 && scheduledStore.Load().Count == 0,
                "逾期提醒确认后也必须立即从内存和磁盘删除");
            AssertClose(GetField<double>(window, "_petSizeScale"), baselineScale,
                "逾期提醒结束后的恢复尺寸");

            Invoke(window, "CompleteActiveClip", reminderExitClip);
            currentNow = dueAt.AddMinutes(2);
            var rewindDueAt = currentNow.AddSeconds(10);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "回拨提醒甲",
                rewindDueAt,
                null);
            Invoke(
                window,
                "TodoWindow_ScheduledTaskAddRequested",
                "回拨提醒乙",
                rewindDueAt,
                null);
            var rewindOrder = scheduledTasks.ToArray();
            Assert(rewindOrder.Length == 2,
                "系统时间回拨回归必须准备两条同秒任务");

            currentNow = rewindDueAt;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       rewindOrder[0]) &&
                   reminderQueue.Count == 1 &&
                   queuedReminderIds.Count == 2,
                "回拨前应由第一条提醒占用气泡，第二条只进入待显示队列");

            currentNow = rewindDueAt.AddSeconds(-5);
            Invoke(window, "ProcessSystemTimeChanged");
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       rewindOrder[0]),
                "系统时间回拨不得撤销已经显示的第一条提醒");

            Invoke(window, "AcknowledgeActiveReminder");
            var persistedAfterRewindAcknowledge = scheduledStore.Load();
            Assert(GetRawField(window, "_activeReminder") is null &&
                   !GetField<bool>(window, "_isReminderActive") &&
                   scheduledTasks.Count == 1 &&
                   scheduledTasks[0].Id == rewindOrder[1].Id &&
                   persistedAfterRewindAcknowledge.Count == 1 &&
                   persistedAfterRewindAcknowledge[0].Id == rewindOrder[1].Id &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.Count == 0 &&
                   scheduledTimer.IsEnabled &&
                   Math.Abs((scheduledTimer.Interval - TimeSpan.FromSeconds(3))
                       .TotalMilliseconds) < 1,
                "回拨后确认第一条时，第二条不得提前显示，必须重新调度到原截止秒");

            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Invoke(window, "CompleteActiveClip", reminderExitClip);

            currentNow = rewindDueAt;
            Invoke(window, "ProcessScheduledTasksAt", currentNow);
            Assert(ReferenceEquals(
                       GetField<ScheduledTaskItem>(window, "_activeReminder"),
                       rewindOrder[1]) &&
                   GetField<bool>(window, "_isReminderActive") &&
                   reminderQueue.Count == 0 &&
                   queuedReminderIds.SetEquals([rewindOrder[1].Id]),
                "系统时间再次到达原截止秒后，第二条提醒必须正常显示且只触发一次");

            Invoke(window, "AcknowledgeActiveReminder");
            CompleteCurrentPetSizeTransitionForReminderTest(window);
            Invoke(
                window,
                "ReminderSizeCommitTimer_Tick",
                null,
                EventArgs.Empty);
            Assert(scheduledTasks.Count == 0 && scheduledStore.Load().Count == 0,
                "回拨回归中的第二条提醒确认后必须正常清理内存和持久化数据");
        }
        finally
        {
            scheduledTimer.Stop();
            reminderSizeTimer.Stop();
            automaticTimer.Stop();
            GetField<DispatcherTimer>(window, "_petSizePersistTimer").Stop();
            scheduledTasks.Clear();
            reminderQueue.Clear();
            queuedReminderIds.Clear();
            scheduledStore.Save(scheduledTasks);
            SetField(window, "_activeReminder", null);
            SetField(window, "_isReminderActive", false);
            SetField(window, "_upcomingReminderPreloadPageName", null);
            SetField(window, "_isTransientPetSizeOverride", false);
            SetField(window, "_isRestoringReminderSize", false);
            Invoke(window, "HideBubbleVisuals");
            SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
            Invoke(window, "StopVisualClock");
            SetField(window, "_activeClip", null);
            SetField(window, "_activeFrameIndex", -1);
            SetField(window, "_activeClipStartedTimestamp", 0L);
            SetField(window, "_activeFrameDeadlineTimestamp", 0L);
            Invoke(window, "ClearDeferredActiveClipClock");
            Invoke(window, "ApplyPetSizeScale", baselineScale, false, false);
            SetField(window, "_nowProvider", originalNowProvider);
            SetField(
                window,
                "_automaticAnimationEnabled",
                originalAutomaticAnimationEnabled);
            if (originalAutomaticAnimationEnabled && window.IsVisible)
            {
                Invoke(window, "RestartAutomaticCountdown");
            }

            window.IsHitTestVisible = originalHitTestVisible;
        }
    }

    private static void CompleteCurrentPetSizeTransitionForReminderTest(
        MainWindow window)
    {
        var timestamp = Stopwatch.GetTimestamp();
        Invoke(window, "ConsumePendingPetSizeTargetAt", timestamp);
        if (!GetField<bool>(window, "_isPetSizeTransitioning"))
        {
            return;
        }

        var transitionStartedAt = GetField<long>(
            window,
            "_petSizeTransitionStartedTimestamp");
        var transitionDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "PetSizeTransitionDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var durationTicks = Math.Max(
            1L,
            (long)Math.Ceiling(
                transitionDuration.TotalSeconds * Stopwatch.Frequency));
        Invoke(
            window,
            "AdvancePetSizeTransition",
            checked(transitionStartedAt + durationTicks + 1));
    }

    private static void AssertTodoWindowLayoutApiAndIme()
    {
        var type = typeof(TodoWindow);
        foreach (var propertyName in new[]
                 {
                     "Todos",
                     "IsImeComposing",
                     "IsTodoDragInProgress"
                 })
        {
            Assert(type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {propertyName} 属性");
        }

        foreach (var methodName in new[]
                 {
                     "FocusInput",
                     "SetEdgeRoamingEnabled",
                     "SetPetSizeScale"
                 })
        {
            Assert(type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {methodName} 方法");
        }

        foreach (var eventName in new[]
                 {
                     "AddRequested",
                     "TodoChanged",
                     "TodoEdited",
                     "TodoMoveRequested",
                     "TodoDragCompleted",
                     "DeleteRequested",
                     "EdgeRoamingEnabledChanged",
                     "PetSizeScaleChanged",
                     "CloseRequested",
                     "ExitRequested",
                     "ImeCompositionChanged"
                 })
        {
            Assert(type.GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {eventName} 事件");
        }

        var todoWindow = new TodoWindow
        {
            Left = -10000,
            Top = -10000,
            ShowActivated = false
        };
        try
        {
            AssertClose(todoWindow.Width, 292, "TodoWindow 总宽度");
            AssertClose(todoWindow.Height, 378, "TodoWindow 增加绕屏行后的总高度");
            Assert(string.Equals(
                    todoWindow.FontFamily.Source,
                    "Microsoft YaHei",
                    StringComparison.OrdinalIgnoreCase),
                $"TodoWindow 必须统一使用 Microsoft YaHei，实际 {todoWindow.FontFamily.Source}");
            Assert(todoWindow.WindowStyle == WindowStyle.None &&
                   todoWindow.AllowsTransparency &&
                   !todoWindow.ShowInTaskbar &&
                   todoWindow.Background is SolidColorBrush windowBackground &&
                   windowBackground.Color.A == 0,
                "TodoWindow 必须为透明背景、无边框且不占任务栏；" +
                $"style={todoWindow.WindowStyle}, allowsTransparency={todoWindow.AllowsTransparency}, " +
                $"taskbar={todoWindow.ShowInTaskbar}, background={todoWindow.Background}");
            var copyBindings = todoWindow.CommandBindings
                .OfType<CommandBinding>()
                .Where(binding => ReferenceEquals(binding.Command, ApplicationCommands.Copy))
                .ToArray();
            Assert(copyBindings.Length == 1,
                "TodoWindow 必须在窗口级显式绑定一次 ApplicationCommands.Copy");
            Assert(!todoWindow.CommandBindings.OfType<CommandBinding>().Any(binding =>
                    ReferenceEquals(binding.Command, ApplicationCommands.Paste) ||
                    ReferenceEquals(binding.Command, ApplicationCommands.Cut) ||
                    ReferenceEquals(binding.Command, ApplicationCommands.SelectAll)),
                "复制修复不得拦截 Ctrl+V、Ctrl+X 或 Ctrl+A 的 TextBox 默认命令");
            var todoXaml = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml"));
            Assert(todoXaml.Contains(
                       "<Trigger Property=\"IsMouseOver\" Value=\"True\">",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "<Setter Property=\"Background\" Value=\"#EAF3FF\" />",
                       StringComparison.Ordinal),
                "待办行悬停时必须切换为浅蓝背景，且透明只读 TextBox 不得遮断行级 hover");
            var todoXamlDocument = XDocument.Parse(todoXaml);
            var explicitFontFamilies = todoXamlDocument.Root!
                .DescendantsAndSelf()
                .Attributes()
                .Where(attribute => attribute.Name.LocalName == "FontFamily")
                .Select(attribute => attribute.Value)
                .ToArray();
            Assert(explicitFontFamilies.Length > 0 && explicitFontFamilies.All(fontFamily =>
                    string.Equals(
                        fontFamily,
                        "Microsoft YaHei",
                        StringComparison.OrdinalIgnoreCase)),
                $"TodoWindow 所有显式字体必须统一为 Microsoft YaHei，实际：" +
                string.Join(", ", explicitFontFamilies.Distinct(StringComparer.OrdinalIgnoreCase)));

            var todoSource = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml.cs"));
            var previewCopySource = ExtractPrivateMethodSource(
                todoSource,
                "TodoWindow_PreviewKeyDown");
            var getCopyTextSource = ExtractPrivateMethodSource(todoSource, "GetCopyText");
            var queueSizeSource = ExtractPrivateMethodSource(
                todoSource,
                "QueuePetSizeScaleChanged");
            var flushSizeSource = ExtractPrivateMethodSource(
                todoSource,
                "FlushPendingPetSizeScaleChanged");
            var endSizeSource = ExtractPrivateMethodSource(
                todoSource,
                "EndPetSizeAdjustment");
            var beginEditSource = ExtractPrivateMethodSource(
                todoSource,
                "BeginTodoEdit");
            var commitEditSource = ExtractPrivateMethodSource(
                todoSource,
                "CommitTodoEdit");
            var cancelEditSource = ExtractPrivateMethodSource(
                todoSource,
                "CancelTodoEdit");
            var outsideClickSource = ExtractPrivateMethodSource(
                todoSource,
                "TodoWindow_PreviewMouseDown");
            var finishOutsideClickSource = ExtractPrivateMethodSource(
                todoSource,
                "FinishTodoEditAfterOutsideClick");
            var scheduleFocusLossSource = ExtractPrivateMethodSource(
                todoSource,
                "ScheduleTodoEditAfterFocusLoss");
            var handleFocusDepartureSource = ExtractPrivateMethodSource(
                todoSource,
                "HandleTodoEditAfterFocusDeparture");
            var finishFocusLossSource = ExtractPrivateMethodSource(
                todoSource,
                "FinishTodoEditAfterFocusLoss");
            var deleteTodoSource = ExtractPrivateMethodSource(
                todoSource,
                "DeleteButton_Click");
            var closingSource = ExtractPrivateMethodSource(
                todoSource,
                "TodoWindow_Closing");
            var dataContextChangedSource = ExtractPrivateMethodSource(
                todoSource,
                "TodoEditTextBox_DataContextChanged");
            var editorUnloadedSource = ExtractPrivateMethodSource(
                todoSource,
                "TodoEditTextBox_Unloaded");
            var editKeySource = ExtractPrivateMethodSource(
                todoSource,
                "TodoEditTextBox_PreviewKeyDown");
            var dragMoveSource = ExtractPrivateMethodSource(
                todoSource,
                "TodoDragHandle_PreviewMouseMove");
            var dropSource = ExtractPrivateMethodSource(
                todoSource,
                "TodoItemsControl_PreviewDrop");
            var autoScrollSource = ExtractPrivateMethodSource(
                todoSource,
                "AutoScrollTodoList");
            Assert(todoSource.Contains(
                       "PreviewKeyDown += TodoWindow_PreviewKeyDown",
                       StringComparison.Ordinal) &&
                   previewCopySource.Contains("e.Key != Key.C", StringComparison.Ordinal) &&
                   previewCopySource.Contains(
                       "Keyboard.Modifiers & ModifierKeys.Control",
                       StringComparison.Ordinal) &&
                   previewCopySource.Contains(
                       "Keyboard.FocusedElement is not TextBox textBox",
                       StringComparison.Ordinal) &&
                   previewCopySource.Contains("!IsCopySource(textBox)", StringComparison.Ordinal) &&
                   previewCopySource.Contains("GetCopyText(textBox)", StringComparison.Ordinal) &&
                   previewCopySource.Contains("CopyTextToClipboard(text)", StringComparison.Ordinal) &&
                   previewCopySource.Contains("e.Handled = true", StringComparison.Ordinal),
                "TodoWindow PreviewKeyDown 必须仅拦截焦点复制源的 Ctrl+C，并经统一取文与剪贴板路径处理");
            Assert(getCopyTextSource.Contains(
                       "ReferenceEquals(textBox, TodoInput)",
                       StringComparison.Ordinal) &&
                   getCopyTextSource.Contains("textBox.SelectionLength > 0", StringComparison.Ordinal) &&
                   getCopyTextSource.Contains("? textBox.SelectedText", StringComparison.Ordinal) &&
                   getCopyTextSource.Contains(": textBox.Text", StringComparison.Ordinal) &&
                   getCopyTextSource.Contains(
                       "textBox.DataContext is TodoItem",
                       StringComparison.Ordinal) &&
                   getCopyTextSource.Contains("? textBox.SelectedText", StringComparison.Ordinal) &&
                   getCopyTextSource.Contains(": null", StringComparison.Ordinal),
                "Ctrl+C 取文契约必须是：输入框无选区复制全文、有选区复制选区；列表文字在只读或编辑状态都仅复制选区");
            Assert(todoXaml.Contains("x:Name=\"TodoDragHandle\"", StringComparison.Ordinal) &&
                   todoXaml.Contains("x:Name=\"TodoEditButton\"", StringComparison.Ordinal) &&
                   todoXaml.Contains("x:Name=\"TodoTextBox\"", StringComparison.Ordinal) &&
                   !todoXaml.Contains("Content=\"改\"", StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "AutomationProperties.Name=\"修改待办\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "M13.2,1.5 L14.5,2.8 L5,12.3 L1.7,14.3 L3.7,11 Z",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains("AllowDrop=\"True\"", StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "LostMouseCapture=\"TodoDragHandle_LostMouseCapture\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "DataContextChanged=\"TodoEditTextBox_DataContextChanged\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "Unloaded=\"TodoEditTextBox_Unloaded\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "PreviewDrop=\"TodoItemsControl_PreviewDrop\"",
                       StringComparison.Ordinal),
                "待办拖拽必须只从专用手柄发起，行内修改必须使用带无障碍名称的左下笔尖图标按钮，" +
                "不能显示“改”字或占用只读文字的选词与复制手势");
            Assert(beginEditSource.Contains("IsReadOnly = false", StringComparison.Ordinal) &&
                   beginEditSource.Contains(
                       "TextCompositionManager.AddPreviewTextInputStartHandler",
                       StringComparison.Ordinal) &&
                    beginEditSource.Contains(
                        "textBox.PreviewTextInput += TodoInput_PreviewTextInputCommitted",
                        StringComparison.Ordinal) &&
                    commitEditSource.Contains(
                        "_editingTodoDraftText.Trim()",
                        StringComparison.Ordinal) &&
                    todoSource.Contains("CaptureTodoEditDraft", StringComparison.Ordinal) &&
                    commitEditSource.Contains("TodoEdited?.Invoke", StringComparison.Ordinal) &&
                   cancelEditSource.Contains("EndTodoEdit", StringComparison.Ordinal) &&
                   todoSource.Contains("textBox.IsReadOnly = true", StringComparison.Ordinal) &&
                   editKeySource.Contains("IsImeComposing", StringComparison.Ordinal) &&
                    editKeySource.Contains("Key.Enter", StringComparison.Ordinal) &&
                    editKeySource.Contains("Key.Escape", StringComparison.Ordinal),
                "行内编辑必须支持 Trim 后保存、Esc 取消和空白保护，且微软输入法组合中的 Enter/Esc 不得误提交或取消");
            Assert(todoSource.Contains(
                       "Mouse.PreviewMouseDownEvent",
                       StringComparison.Ordinal) &&
                   todoSource.Contains(
                       "handledEventsToo: true",
                       StringComparison.Ordinal) &&
                   (outsideClickSource.Contains(
                        "IsWithin(e.OriginalSource as DependencyObject, textBox)",
                        StringComparison.Ordinal) ||
                    outsideClickSource.Contains(
                        "IsWithin(originalSource, textBox)",
                        StringComparison.Ordinal)) &&
                   outsideClickSource.Contains(
                       "ScheduleTodoEditAfterOutsideClick()",
                       StringComparison.Ordinal) &&
                   outsideClickSource.Contains(
                       "if (!IsImeComposing)",
                       StringComparison.Ordinal) &&
                   outsideClickSource.Contains(
                       "CommitTodoEdit()",
                       StringComparison.Ordinal) &&
                   finishOutsideClickSource.Contains(
                       "IsImeComposing",
                       StringComparison.Ordinal) &&
                   finishOutsideClickSource.Contains(
                       "CommitTodoEdit()",
                       StringComparison.Ordinal) &&
                   scheduleFocusLossSource.Contains(
                       "DispatcherPriority.ContextIdle",
                       StringComparison.Ordinal) &&
                   !scheduleFocusLossSource.Contains(
                       "DispatcherPriority.Input",
                       StringComparison.Ordinal),
                "点击行内编辑框外的任意窗口区域必须先保存再继续按钮事件；微软输入法组合或真实失焦则统一延后到ContextIdle，不能保存半成品");
            Assert(handleFocusDepartureSource.Contains(
                       "if (!IsImeComposing)",
                       StringComparison.Ordinal) &&
                   handleFocusDepartureSource.Contains(
                       "CommitTodoEdit()",
                       StringComparison.Ordinal) &&
                   deleteTodoSource.Contains(
                       "ReferenceEquals(item, _editingTodoItem)",
                       StringComparison.Ordinal) &&
                   deleteTodoSource.Contains(
                       "CancelTodoEdit()",
                       StringComparison.Ordinal) &&
                   closingSource.Contains(
                       "if (IsImeComposing)",
                       StringComparison.Ordinal) &&
                   closingSource.Contains(
                       "CancelTodoEdit()",
                       StringComparison.Ordinal) &&
                   dataContextChangedSource.Contains(
                       "if (IsImeComposing)",
                       StringComparison.Ordinal) &&
                   dataContextChangedSource.Contains(
                       "CancelTodoEdit()",
                       StringComparison.Ordinal) &&
                   editorUnloadedSource.Contains(
                       "if (IsImeComposing)",
                       StringComparison.Ordinal) &&
                   editorUnloadedSource.Contains(
                       "CancelTodoEdit()",
                       StringComparison.Ordinal) &&
                   finishFocusLossSource.Contains(
                       "containerWasRecycled && IsImeComposing",
                       StringComparison.Ordinal) &&
                   finishFocusLossSource.Contains(
                       "CancelTodoEdit()",
                       StringComparison.Ordinal),
                "Non-IME focus loss must save synchronously; deleting or closing during IME composition must cancel the unconfirmed draft");
            Assert(dragMoveSource.Contains(
                       "SystemParameters.MinimumHorizontalDragDistance",
                       StringComparison.Ordinal) &&
                    dragMoveSource.Contains(
                        "SystemParameters.MinimumVerticalDragDistance",
                        StringComparison.Ordinal) &&
                    !dragMoveSource.Contains("Opacity", StringComparison.Ordinal) &&
                    autoScrollSource.Contains("_todoScrollViewer ??=", StringComparison.Ordinal) &&
                    autoScrollSource.Contains("Stopwatch.GetElapsedTime", StringComparison.Ordinal) &&
                    dropSource.Contains("TodoMoveRequested?.Invoke", StringComparison.Ordinal),
                "拖拽必须超过 Windows 系统阈值后才启动、不得把状态留在回收容器上，" +
                "自动滚动必须缓存并节流，而且一次 Drop 只发出一次最终索引移动请求");
            var mainWindowSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
            var outsideCloseSource = ExtractPrivateMethodSource(
                mainWindowSource,
                "ProcessOutsideTodoClose");
            Assert(outsideCloseSource.Contains(
                       "_todoWindow.IsTodoDragInProgress",
                       StringComparison.Ordinal) &&
                   mainWindowSource.Contains(
                       "_todoWindow.TodoDragCompleted += TodoWindow_TodoDragCompleted",
                       StringComparison.Ordinal),
                "DoDragDrop 嵌套消息循环期间外部失焦不得收起待办窗口，拖拽结束后才重新检查外部点击");
            Assert(queueSizeSource.Contains(
                       "_petSizeScaleNotificationQueued = true",
                       StringComparison.Ordinal) &&
                   !queueSizeSource.Contains(
                       "CompositionTarget.Rendering",
                       StringComparison.Ordinal) &&
                   flushSizeSource.Contains(
                        "PetSizeScaleChanged?.Invoke(scale)",
                        StringComparison.Ordinal) &&
                   !todoSource.Contains(
                       "CompositionTarget.Rendering",
                       StringComparison.Ordinal) &&
                   !todoSource.Contains(
                       "PetSizeScale_Rendering",
                       StringComparison.Ordinal) &&
                   typeof(TodoWindow).GetField(
                       "_petSizeRenderingSubscribed",
                       InstanceFlags) is null &&
                     !queueSizeSource.Contains(
                        "DispatcherPriority.Background",
                        StringComparison.Ordinal) &&
                   !flushSizeSource.Contains(
                        "DispatcherPriority.Background",
                        StringComparison.Ordinal) &&
                   endSizeSource.IndexOf(
                       "FlushPendingPetSizeScaleChanged()",
                       StringComparison.Ordinal) <
                   endSizeSource.IndexOf(
                       "PetSizeAdjustmentCompleted?.Invoke()",
                       StringComparison.Ordinal),
                "滑块手势必须只缓存最新尺寸并由MainWindow唯一Rendering合帧，" +
                "TodoWindow不得逐帧修改全局Rendering订阅表，松手必须先冲刷最终值");

            todoWindow.Todos = new ObservableCollection<TodoItem>(
                Enumerable.Range(1, 12)
                    .Select(index => new TodoItem { Text = $"待办 {index}" }));
            todoWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));

            var scrollViewer = FindVisualDescendant<ScrollViewer>(todoWindow)
                ?? throw new InvalidOperationException("TodoWindow 找不到待办滚动区域");
            var itemsControl = GetField<ItemsControl>(todoWindow, "TodoItemsControl");
            var roamingToggle = GetField<CheckBox>(todoWindow, "EdgeRoamingToggle");
            var sizeSlider = GetField<Slider>(todoWindow, "PetSizeSlider");
            var sizeLabel = GetField<TextBlock>(todoWindow, "PetSizeLabel");
            Assert(roamingToggle.IsChecked == true &&
                   string.Equals(
                       roamingToggle.Content?.ToString(),
                       "绕屏动画",
                       StringComparison.Ordinal) &&
                   string.Equals(
                       System.Windows.Automation.AutomationProperties.GetName(
                           roamingToggle),
                       "绕屏动画",
                       StringComparison.Ordinal) &&
                   string.Equals(
                       roamingToggle.FontFamily.Source,
                       "Microsoft YaHei",
                       StringComparison.OrdinalIgnoreCase),
                "桌宠大小上方必须显示默认勾选的“绕屏动画”，并统一使用微软雅黑和无障碍名称");
            var roamingToggleCenter = roamingToggle.TranslatePoint(
                new Point(0, roamingToggle.ActualHeight / 2),
                todoWindow);
            var sizeSliderCenter = sizeSlider.TranslatePoint(
                new Point(0, sizeSlider.ActualHeight / 2),
                todoWindow);
            Assert(roamingToggleCenter.Y < sizeSliderCenter.Y &&
                   todoXaml.Contains(
                       "x:Name=\"EdgeRoamingToggle\"",
                       StringComparison.Ordinal) &&
                   todoXaml.Contains(
                       "<Grid Grid.Row=\"3\"",
                       StringComparison.Ordinal),
                "绕屏勾选必须直接位于桌宠大小滑块上方，不能挤进标题或列表区域");
            var roamingEventCount = 0;
            var roamingEventValue = false;
            todoWindow.EdgeRoamingEnabledChanged += enabled =>
            {
                roamingEventCount++;
                roamingEventValue = enabled;
            };
            todoWindow.SetEdgeRoamingEnabled(false);
            Assert(roamingToggle.IsChecked == false && roamingEventCount == 0,
                "程序加载已保存的绕屏设置时必须静默更新勾选，不能反向触发保存事件");
            roamingToggle.IsChecked = true;
            Assert(roamingEventCount == 1 && roamingEventValue,
                "用户重新勾选绕屏必须且只能发布一次true事件");
            roamingToggle.IsChecked = false;
            Assert(roamingEventCount == 2 && !roamingEventValue,
                "用户取消绕屏必须且只能发布一次false事件");
            todoWindow.SetEdgeRoamingEnabled(true);
            Assert(roamingToggle.IsChecked == true && roamingEventCount == 2,
                "程序同步最终绕屏状态不得重复发布用户事件");
            AssertClose(sizeSlider.Minimum, 75, "桌宠尺寸滑块下限");
            AssertClose(sizeSlider.Maximum, 140, "桌宠尺寸滑块上限");
            AssertClose(sizeSlider.TickFrequency, 1, "桌宠尺寸滑块刻度");
            Assert(!sizeSlider.IsSnapToTickEnabled,
                "桌宠尺寸滑块不得强制吸附5%档位");
            Assert(sizeSlider.IsMoveToPointEnabled,
                "桌宠尺寸滑块应允许直接移动到鼠标位置");
            Assert(!sizeSlider.UseLayoutRounding && !sizeSlider.SnapsToDevicePixels,
                "桌宠尺寸滑块必须关闭布局取整和物理像素吸附，避免拖动时逐像素卡顿");
            AssertClose(sizeSlider.SmallChange, 1, "桌宠尺寸键盘小步长");
            AssertClose(sizeSlider.LargeChange, 5, "桌宠尺寸键盘大步长");
            todoWindow.SetPetSizeScale(2);
            AssertClose(sizeSlider.Value, 140, "尺寸API超过上限时应钳制");
            todoWindow.SetPetSizeScale(0.1);
            AssertClose(sizeSlider.Value, 75, "尺寸API低于下限时应钳制");
            todoWindow.SetPetSizeScale(1);
            AssertClose(sizeSlider.Value, 100, "尺寸API默认值");
            Assert(sizeLabel.Text == "100%", "尺寸标签必须同步显示百分比");
            var sizeEventValue = 0d;
            var sizeEventCount = 0;
            var adjustmentStartedCount = 0;
            var adjustmentCompletedCount = 0;
            todoWindow.PetSizeScaleChanged += value =>
            {
                sizeEventValue = value;
                sizeEventCount++;
            };
            todoWindow.PetSizeAdjustmentStarted += () => adjustmentStartedCount++;
            todoWindow.PetSizeAdjustmentCompleted += () => adjustmentCompletedCount++;
            Invoke(todoWindow, "BeginPetSizeAdjustment");
            for (var inputIndex = 0; inputIndex < 240; inputIndex++)
            {
                sizeSlider.Value = inputIndex == 239
                    ? 123.4
                    : 75.1 + inputIndex * (48.2 / 238d);
            }

            Assert(sizeEventCount == 0,
                "连续240次手势输入在MainWindow合成帧消费前不得直接发布尺寸事件");
            Invoke(todoWindow, "FlushPendingPetSizeScaleChanged");
            Assert(sizeEventCount == 1,
                "MainWindow一次合成帧必须把连续240次输入折叠为一个最终尺寸事件");
            AssertClose(sizeEventValue, 1.234,
                "合成帧必须发布240次输入中的最终123.4%值");

            sizeSlider.Value = 124.5;
            Invoke(todoWindow, "FlushPendingPetSizeScaleChanged");
            Assert(sizeEventCount == 2,
                "下一次合成帧必须发布新输入且不能重播上一帧");
            AssertClose(sizeEventValue, 1.245,
                "下一次合成帧必须发布新的124.5%最终值");

            sizeSlider.Value = 126.5;
            Invoke(todoWindow, "EndPetSizeAdjustment");
            Assert(sizeEventCount == 3,
                "松开滑块必须在完成事件前显式冲刷尚未被合成帧消费的最终值");
            AssertClose(sizeEventValue, 1.265,
                "松手最终冲刷必须保留最新126.5%值");
            Assert(adjustmentStartedCount == 1 && adjustmentCompletedCount == 1,
                "尺寸滑块必须明确发出按下与松开手势边界");

            sizeSlider.Value = 127.5;
            Assert(sizeEventCount == 4,
                "非手势的程序化ValueChanged仍必须立即且仅发布一次");
            AssertClose(sizeEventValue, 1.275,
                "非手势程序化ValueChanged必须发布其最终值");
            Assert(scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                "待办列表必须保留自动垂直滚动条");
            Assert(VirtualizingPanel.GetIsVirtualizing(itemsControl),
                "待办列表必须启用 UI 虚拟化，避免大量待办同时创建控件");
            Assert(VirtualizingPanel.GetVirtualizationMode(itemsControl) ==
                   VirtualizationMode.Recycling,
                "待办列表必须使用 Recycling 容器复用模式");
            Assert(scrollViewer.ActualHeight >= 155,
                $"待办可视区应完整容纳五行，实际 {scrollViewer.ActualHeight:F1} DIP");
            Assert(scrollViewer.ExtentHeight > scrollViewer.ViewportHeight,
                "超出可视区域的待办应进入滚动区域而不是撑大窗口");

            var itemHeights = Enumerable.Range(0, 5)
                .Select(index => itemsControl.ItemContainerGenerator.ContainerFromIndex(index))
                .OfType<FrameworkElement>()
                .Select(container => container.ActualHeight)
                .ToArray();
            Assert(itemHeights.Length == 5 && itemHeights.All(height => height <= 32),
                $"待办行高应缩小到约 31 DIP，容器数 {itemHeights.Length}，" +
                $"实际：{string.Join(", ", itemHeights.Select(height => $"{height:F1}"))}；" +
                $"Scroll Actual={scrollViewer.ActualHeight:F1}, " +
                $"Viewport={scrollViewer.ViewportHeight:F1}, Extent={scrollViewer.ExtentHeight:F1}");
            Assert(itemHeights.Sum() <= scrollViewer.ActualHeight + 0.5,
                "列表可视区域必须完整显示前五行");

            var input = GetField<TextBox>(todoWindow, "TodoInput");
            input.Text = "输入框无选区也应复制全文";
            input.Select(0, 0);
            Assert((bool)Invoke(todoWindow, "CanCopyFromTextBox", input)! &&
                   string.Equals(
                       (string?)Invoke(todoWindow, "GetCopyText", input),
                       input.Text,
                       StringComparison.Ordinal),
                "TodoInput 无选区但有文字时 Ctrl+C 必须复制整段且不改变选区");
            todoWindow.Activate();
            input.Focus();
            Keyboard.Focus(input);
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Assert(input.IsKeyboardFocusWithin,
                "输入框选区保留测试必须先建立真实键盘焦点");
            Assert(input.SelectionLength == 0,
                "输入框无选区 Ctrl+C 契约测试必须保持空选区");
            Assert(ApplicationCommands.Copy.CanExecute(parameter: null, target: input),
                "输入框无选区但有全文时窗口级 Copy 命令必须可执行");
            input.Select(3, 5);
            var inputSelectionStart = input.SelectionStart;
            var inputSelectionLength = input.SelectionLength;
            Assert((bool)Invoke(todoWindow, "CanCopyFromTextBox", input)! &&
                   string.Equals(
                       (string?)Invoke(todoWindow, "GetCopyText", input),
                       input.SelectedText,
                       StringComparison.Ordinal),
                "TodoInput 有选区时 Ctrl+C 必须只复制选中文本");
            Assert(ApplicationCommands.Copy.CanExecute(parameter: null, target: input),
                "输入框存在选区时窗口级 Copy 命令必须可执行且只交给统一复制路径");
            input.Focus();
            Keyboard.Focus(input);
            Invoke(todoWindow, "FocusInputCore");
            Assert(input.SelectionStart == inputSelectionStart &&
                   input.SelectionLength == inputSelectionLength,
                "TodoInput 已聚焦时再次请求聚焦不得折叠用户刚选中的文本");

            var longTodoText = string.Concat(Enumerable.Repeat(
                "这是一条需要自动换行并在悬停时显示完整内容的很长待办事项。",
                5));
            todoWindow.Todos = new ObservableCollection<TodoItem>
            {
                new() { Text = longTodoText }
            };
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var longItemContainer = itemsControl.ItemContainerGenerator.ContainerFromIndex(0)
                as FrameworkElement
                ?? throw new InvalidOperationException("长待办没有生成可视容器");
            var longItemTextBox = FindVisualDescendant<TextBox>(longItemContainer)
                ?? throw new InvalidOperationException("待办列表项必须使用只读 TextBox");
            var longItemEditButton = FindVisualDescendants<Button>(longItemContainer)
                .SingleOrDefault(button => button.Name == "TodoEditButton")
                ?? throw new InvalidOperationException("待办列表项必须提供独立编辑按钮");
            var longItemDragHandle = FindVisualDescendants<FrameworkElement>(longItemContainer)
                .SingleOrDefault(element => element.Name == "TodoDragHandle")
                ?? throw new InvalidOperationException("待办列表项必须提供独立拖拽手柄");
            var longItemRowBorder = FindVisualDescendants<Border>(longItemContainer)
                .FirstOrDefault(border =>
                    Math.Abs(border.MaxHeight - 41) < 0.01 &&
                    Math.Abs(border.CornerRadius.TopLeft - 8) < 0.01)
                ?? throw new InvalidOperationException("长待办找不到行级 hover Border");
            var hoverTrigger = longItemRowBorder.Style.Triggers
                .OfType<Trigger>()
                .SingleOrDefault(trigger =>
                    trigger.Property == UIElement.IsMouseOverProperty &&
                    Equals(trigger.Value, true));
            var hoverBackground = hoverTrigger?.Setters
                .OfType<Setter>()
                .SingleOrDefault(setter => setter.Property == Border.BackgroundProperty)
                ?.Value as SolidColorBrush;
            Assert(hoverBackground?.Color == Color.FromRgb(0xEA, 0xF3, 0xFF) &&
                   longItemTextBox.Background is SolidColorBrush textBackground &&
                   textBackground.Color.A == 0,
                "待办行 IsMouseOver 必须应用 #EAF3FF，内部透明只读文字框不能遮住浅蓝 hover");
            Assert(longItemTextBox.IsReadOnly &&
                   longItemTextBox.Name == "TodoTextBox" &&
                   longItemTextBox.IsTabStop == false &&
                   longItemTextBox.Focusable &&
                   longItemTextBox.Cursor == Cursors.IBeam &&
                   longItemTextBox.TextWrapping == TextWrapping.Wrap &&
                   longItemTextBox.MaxLines == 2 &&
                   longItemTextBox.MaxHeight <= 36.5 &&
                   longItemTextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden &&
                   longItemTextBox.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                "列表文字必须是可鼠标选择的无边框只读 TextBox，并限制为两行换行显示");
            Assert(longItemEditButton.Tag is TodoItem &&
                   longItemEditButton.Content is Viewbox editIcon &&
                   FindVisualDescendants<System.Windows.Shapes.Path>(editIcon).Count() == 2 &&
                   longItemDragHandle.DataContext is TodoItem &&
                   longItemDragHandle.Cursor == Cursors.SizeAll,
                "编辑按钮必须显示双路径铅笔图标，编辑按钮和拖拽手柄都要绑定当前 TodoItem；" +
                "文字 TextBox 本身不得兼任拖拽热区");
            Assert(longItemContainer.ActualHeight <= 44.5 &&
                   longItemContainer.ActualWidth <= itemsControl.ActualWidth + 0.5,
                $"长待办不得撑高或撑宽列表：item={longItemContainer.ActualWidth:F1}x" +
                $"{longItemContainer.ActualHeight:F1}, listWidth={itemsControl.ActualWidth:F1}");
            Assert(string.Equals(longItemTextBox.Text, longTodoText, StringComparison.Ordinal),
                "长待办只读 TextBox 必须显示完整绑定文本而不是截断数据");
            var longItemToolTip = longItemTextBox.ToolTip as ToolTip
                ?? throw new InvalidOperationException("长待办必须提供全文 ToolTip");
            var longItemToolTipText = longItemToolTip.Content as TextBlock
                ?? throw new InvalidOperationException("长待办 ToolTip 必须使用可换行文字");
            var toolTipTextBinding = System.Windows.Data.BindingOperations.GetBinding(
                longItemToolTipText,
                TextBlock.TextProperty);
            Assert(longItemToolTip.MaxWidth <= 360.5 &&
                   longItemToolTipText.TextWrapping == TextWrapping.Wrap &&
                   string.Equals(
                       longItemTextBox.FontFamily.Source,
                       "Microsoft YaHei",
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       longItemToolTipText.FontFamily.Source,
                       "Microsoft YaHei",
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       toolTipTextBinding?.Path?.Path,
                       "Text",
                       StringComparison.Ordinal),
                "长待办 ToolTip 必须限宽并自动换行显示全文");
            longItemTextBox.Select(0, 0);
            Assert(!(bool)Invoke(todoWindow, "CanCopyFromTextBox", longItemTextBox)!,
                "列表只读文字无选区时不得误复制整条待办");
            longItemTextBox.Select(2, 8);
            Assert((bool)Invoke(todoWindow, "CanCopyFromTextBox", longItemTextBox)! &&
                   string.Equals(
                       (string?)Invoke(todoWindow, "GetCopyText", longItemTextBox),
                       longItemTextBox.SelectedText,
                       StringComparison.Ordinal),
                "列表只读文字必须支持鼠标/键盘选区并由 Ctrl+C 复制选中文字");
            longItemTextBox.Focus();
            Keyboard.Focus(longItemTextBox);
            PumpDispatcher(TimeSpan.FromMilliseconds(10));
            Assert(ApplicationCommands.Copy.CanExecute(
                    parameter: null,
                    target: longItemTextBox),
                "列表只读文字有选区时窗口级 Copy 命令必须可执行");
            var listSelectionStart = longItemTextBox.SelectionStart;
            var listSelectionLength = longItemTextBox.SelectionLength;
            Invoke(todoWindow, "FocusInputCore");
            Assert(ReferenceEquals(Keyboard.FocusedElement, longItemTextBox) &&
                   longItemTextBox.SelectionStart == listSelectionStart &&
                   longItemTextBox.SelectionLength == listSelectionLength,
                "TodoWindow 内列表正在选字时，延后的输入框聚焦回调不得抢焦点或折叠选区");

            var longTodoItem = (TodoItem)longItemTextBox.DataContext;
            var editedCount = 0;
            TodoItem? lastEditedItem = null;
            todoWindow.TodoEdited += item =>
            {
                editedCount++;
                lastEditedItem = item;
            };

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            Assert(!longItemTextBox.IsReadOnly && longItemTextBox.IsKeyboardFocusWithin,
                "点击编辑按钮后必须在同一行 TextBox 内进入可编辑状态并获得焦点，避免 IME 候选框漂移");
            longItemTextBox.Text = "  修改后的长待办  ";
            longItemTextBox.Select(2, 4);
            Assert(ApplicationCommands.Copy.CanExecute(
                    parameter: null,
                    target: longItemTextBox),
                "行内编辑状态必须保留 TextBox 原生 Ctrl+C/V/X/A，不能被窗口级复制兼容逻辑禁用");

            var editPresentationSource = PresentationSource.FromVisual(longItemTextBox)
                ?? throw new InvalidOperationException("行内编辑 TextBox 未建立输入源");
            Invoke(todoWindow, "SetImeComposing", true);
            var composingEditEnter = CreateKeyEvent(editPresentationSource, Key.Enter);
            Invoke(
                todoWindow,
                "TodoEditTextBox_PreviewKeyDown",
                longItemTextBox,
                composingEditEnter);
            Assert(editedCount == 0 &&
                   longTodoItem.Text == longTodoText &&
                   !longItemTextBox.IsReadOnly,
                "微软输入法仍在组合时，行内编辑 Enter 只能选词，不得提交、退出编辑或覆盖原文");

            Invoke(todoWindow, "SetImeComposing", false);
            var committedEditEnter = CreateKeyEvent(editPresentationSource, Key.Enter);
            Invoke(
                todoWindow,
                "TodoEditTextBox_PreviewKeyDown",
                longItemTextBox,
                committedEditEnter);
            Assert(editedCount == 1 &&
                   ReferenceEquals(lastEditedItem, longTodoItem) &&
                   longTodoItem.Text == "修改后的长待办" &&
                   longItemTextBox.IsReadOnly &&
                   committedEditEnter.Handled,
                "行内编辑 Enter 必须 Trim 后提交一次 TodoEdited、更新原对象并返回只读选择状态");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "这一版应被取消";
            var cancelEditEscape = CreateKeyEvent(editPresentationSource, Key.Escape);
            Invoke(
                todoWindow,
                "TodoEditTextBox_PreviewKeyDown",
                longItemTextBox,
                cancelEditEscape);
            Assert(editedCount == 1 &&
                   longTodoItem.Text == "修改后的长待办" &&
                   longItemTextBox.Text == "修改后的长待办" &&
                   longItemTextBox.IsReadOnly &&
                   cancelEditEscape.Handled,
                "行内编辑 Esc 必须恢复进入编辑前的文字、退出编辑且不得触发保存事件");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "   \t  ";
            Invoke(todoWindow, "CommitTodoEdit");
            Assert(editedCount == 1 &&
                   longTodoItem.Text == "修改后的长待办" &&
                   longItemTextBox.Text == "修改后的长待办" &&
                   longItemTextBox.IsReadOnly,
                "空白编辑不得删除待办或写入空文本；应取消本次修改并保留原文");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "  修改后的长待办  ";
            Invoke(todoWindow, "CommitTodoEdit");
            Assert(editedCount == 1 &&
                   longTodoItem.Text == "修改后的长待办" &&
                   longItemTextBox.IsReadOnly,
                "仅首尾空白不同的编辑应正常结束但不得重复触发 TodoEdited 保存");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "虚拟化回收前保存的草稿";
            var recycledTodoItem = new TodoItem { Text = "回收容器的新待办" };
            longItemTextBox.DataContext = recycledTodoItem;
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 2 &&
                   longTodoItem.Text == "虚拟化回收前保存的草稿" &&
                   recycledTodoItem.Text == "回收容器的新待办" &&
                   longItemTextBox.IsReadOnly,
                "Recycling 复用行容器时必须把已输入草稿提交给原 TodoItem，不能把新行文字写回旧项或污染新项；" +
                $"edited={editedCount}, old={longTodoItem.Text}, recycled={recycledTodoItem.Text}, readOnly={longItemTextBox.IsReadOnly}");
            longItemTextBox.ClearValue(FrameworkElement.DataContextProperty);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(ReferenceEquals(longItemTextBox.DataContext, longTodoItem),
                "虚拟化回收模拟完成后 TextBox 必须恢复继承原行 DataContext");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "失去焦点后自动保存";
            input.Focus();
            Keyboard.Focus(input);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 3 &&
                   longTodoItem.Text == "失去焦点后自动保存" &&
                   longItemTextBox.IsReadOnly,
                "行内编辑失去键盘焦点后必须延后提交一次，点击窗口其他位置不能丢失修改");

            var textBeforeImeRecycle = longTodoItem.Text;
            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "unconfirmed IME text in a recycled container";
            SetField(todoWindow, "_imeCompositionOwner", longItemTextBox);
            Invoke(todoWindow, "SetImeComposing", true);
            var imeRecycledItem = new TodoItem { Text = "new recycled item" };
            longItemTextBox.DataContext = imeRecycledItem;
            Assert(editedCount == 3 &&
                   longTodoItem.Text == textBeforeImeRecycle &&
                   imeRecycledItem.Text == "new recycled item" &&
                   longItemTextBox.IsReadOnly &&
                   !todoWindow.IsImeComposing,
                "Recycling an editor during IME composition must synchronously discard the unconfirmed candidate and never save it to either item");
            longItemTextBox.ClearValue(FrameworkElement.DataContextProperty);
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Assert(ReferenceEquals(longItemTextBox.DataContext, longTodoItem),
                "The IME recycling test must restore the original inherited DataContext");

            var fallbackOriginalText = longTodoItem.Text;
            var fallbackRecycledItem = new TodoItem { Text = "fallback item" };
            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "half-composed fallback text";
            SetField(todoWindow, "_editingTodoItem", fallbackRecycledItem);
            SetField(todoWindow, "_editingTodoOriginalText", fallbackRecycledItem.Text);
            SetField(todoWindow, "_editingTodoDraftText", longItemTextBox.Text);
            SetField(todoWindow, "_imeCompositionOwner", longItemTextBox);
            Invoke(todoWindow, "SetImeComposing", true);
            Invoke(todoWindow, "FinishTodoEditAfterFocusLoss");
            Assert(editedCount == 3 &&
                   longTodoItem.Text == fallbackOriginalText &&
                   fallbackRecycledItem.Text == "fallback item" &&
                   longItemTextBox.IsReadOnly &&
                   !todoWindow.IsImeComposing,
                "The delayed focus-loss fallback must cancel an IME draft when it discovers that the editor was recycled");

            var todoBorder = GetField<Border>(todoWindow, "TodoBorder");
            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "编辑框内部点击不能保存";
            RaisePreviewMouseDown(longItemTextBox);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 3 &&
                   longTodoItem.Text == "失去焦点后自动保存" &&
                   !longItemTextBox.IsReadOnly,
                "点击当前编辑框或其模板子元素时不得提交，也不能折叠正在编辑的状态");

            longItemTextBox.Text = "  点击空白区域自动保存  ";
            RaisePreviewMouseDown(todoBorder);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 4 &&
                   longTodoItem.Text == "点击空白区域自动保存" &&
                   longItemTextBox.IsReadOnly,
                "点击不可聚焦 Border/空白区域后必须 Trim 并只触发一次 TodoEdited");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "微软输入法选词完成后保存";
            SetField(todoWindow, "_imeCompositionOwner", longItemTextBox);
            Invoke(todoWindow, "SetImeComposing", true);
            RaisePreviewMouseDown(todoBorder);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 4 &&
                   longTodoItem.Text == "点击空白区域自动保存" &&
                   !longItemTextBox.IsReadOnly,
                "微软输入法仍在组合时点击空白不得保存尚未上屏的半成品");
            Invoke(todoWindow, "SetImeComposing", false);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 5 &&
                   longTodoItem.Text == "微软输入法选词完成后保存" &&
                   longItemTextBox.IsReadOnly,
                "微软输入法组合结束后，即使焦点仍在编辑框也必须完成一次外点保存");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "旧输入框组合状态结束后保存";
            SetField(todoWindow, "_imeCompositionOwner", input);
            Invoke(todoWindow, "SetImeComposing", true);
            RaisePreviewMouseDown(todoBorder);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 5 &&
                   longTodoItem.Text == "微软输入法选词完成后保存" &&
                   !longItemTextBox.IsReadOnly,
                "IME owner仍指向旧输入框时，外点保存必须保留pending而不能丢失修改");
            Invoke(todoWindow, "SetImeComposing", false);
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            Assert(editedCount == 6 &&
                   longTodoItem.Text == "旧输入框组合状态结束后保存" &&
                   longItemTextBox.IsReadOnly,
                "旧输入框的IME组合状态清除后必须重试并只保存一次当前行草稿");

            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "微软输入法真实失焦后保存";
            SetField(todoWindow, "_imeCompositionOwner", longItemTextBox);
            Invoke(todoWindow, "SetImeComposing", true);
            input.Focus();
            Keyboard.Focus(input);
            PumpDispatcher(TimeSpan.FromMilliseconds(60));
            Assert(editedCount == 7 &&
                   longTodoItem.Text == "微软输入法真实失焦后保存" &&
                   longItemTextBox.IsReadOnly &&
                   !todoWindow.IsImeComposing,
                "微软输入法组合中的真实焦点转移必须等到ContextIdle捕获最终文字，并只保存一次");

            longItemTextBox.Select(1, 5);
            Assert((bool)Invoke(todoWindow, "CanCopyFromTextBox", longItemTextBox)! &&
                   string.Equals(
                       (string?)Invoke(todoWindow, "GetCopyText", longItemTextBox),
                       longItemTextBox.SelectedText,
                       StringComparison.Ordinal),
                "编辑完成后必须恢复列表只读文字的选中复制契约");

            longItemTextBox.Focus();
            Keyboard.Focus(longItemTextBox);
            SetField(todoWindow, "_imeCompositionOwner", longItemTextBox);
            Invoke(todoWindow, "SetImeComposing", true);
            Invoke(todoWindow, "ResetImeCompositionAfterFocusLoss");
            Assert(todoWindow.IsImeComposing,
                "底部输入框旧的延后失焦回调不得清除已经转移到行内编辑框的微软 IME 组合状态");
            Invoke(todoWindow, "SetImeComposing", false);

            var deleteRequestCount = 0;
            TodoItem? deletedItem = null;
            todoWindow.DeleteRequested += item =>
            {
                deleteRequestCount++;
                deletedItem = item;
            };
            var textBeforeDelete = longTodoItem.Text;
            Invoke(todoWindow, "BeginTodoEdit", longItemTextBox, longTodoItem);
            longItemTextBox.Text = "unfinished IME edit must not outlive deletion";
            SetField(todoWindow, "_imeCompositionOwner", longItemTextBox);
            Invoke(todoWindow, "SetImeComposing", true);
            Invoke(
                todoWindow,
                "DeleteButton_Click",
                new Button { Tag = longTodoItem },
                new RoutedEventArgs());
            Assert(deleteRequestCount == 1 &&
                   ReferenceEquals(deletedItem, longTodoItem) &&
                   editedCount == 7 &&
                   longTodoItem.Text == textBeforeDelete &&
                   longItemTextBox.IsReadOnly &&
                   !todoWindow.IsImeComposing,
                "Deleting the item currently edited by IME must cancel its unconfirmed draft before DeleteRequested and never emit a later TodoEdited event");

            var addCount = 0;
            todoWindow.AddRequested += _ => addCount++;
            input.Text = "微软拼音组合文本";
            Invoke(todoWindow, "SetImeComposing", true);
            Assert(todoWindow.IsImeComposing, "TSF 组合开始后应公开组合状态");

            var source = PresentationSource.FromVisual(input)
                ?? throw new InvalidOperationException("TodoWindow 未建立输入源");
            var composingEnter = CreateEnterKeyEvent(source);
            Invoke(todoWindow, "TodoInput_PreviewKeyDown", input, composingEnter);
            Assert(addCount == 0 && input.Text == "微软拼音组合文本",
                "微软拼音仍在组合时 Enter 只能选词，不得误新增待办");

            Invoke(todoWindow, "SetImeComposing", false);
            var committedEnter = CreateEnterKeyEvent(source);
            Invoke(todoWindow, "TodoInput_PreviewKeyDown", input, committedEnter);
            Assert(addCount == 1 && input.Text.Length == 0 && committedEnter.Handled,
                "组合完成后 Enter 应正常发出一次新增请求并清空输入框");

            var dragItems = new ObservableCollection<TodoItem>
            {
                new() { Text = "拖拽第一项" },
                new() { Text = "拖拽第二项" },
                new() { Text = "拖拽第三项" }
            };
            todoWindow.Todos = dragItems;
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            var dragList = GetField<ListBox>(todoWindow, "TodoItemsControl");
            var firstDragContainer = dragList.ItemContainerGenerator.ContainerFromIndex(0)
                as ListBoxItem
                ?? throw new InvalidOperationException("拖拽第一项没有生成容器");
            var thirdDragContainer = dragList.ItemContainerGenerator.ContainerFromIndex(2)
                as ListBoxItem
                ?? throw new InvalidOperationException("拖拽第三项没有生成容器");
            Assert(ReferenceEquals(firstDragContainer.DataContext, dragItems[0]) &&
                   ReferenceEquals(thirdDragContainer.DataContext, dragItems[2]),
                "真实拖放测试必须先确认 Recycling 容器已经绑定新的三项集合");
            var moveRequests = new List<(TodoItem Item, int Index)>();
            todoWindow.TodoMoveRequested += (item, index) =>
                moveRequests.Add((item, index));
            SetField(todoWindow, "_todoDragInProgress", true);
            try
            {
                var moveFirstToEnd = CreateDragEvent(
                    dragItems[0],
                    thirdDragContainer,
                    new Point(4, Math.Max(1, thirdDragContainer.ActualHeight - 1)));
                thirdDragContainer.RaiseEvent(moveFirstToEnd);
                Assert(moveRequests.Count == 1 &&
                       ReferenceEquals(moveRequests[0].Item, dragItems[0]) &&
                       moveRequests[0].Index == 2 &&
                       ReferenceEquals(moveFirstToEnd.OriginalSource, thirdDragContainer) &&
                       moveFirstToEnd.Handled,
                    "真实 PreviewDrop 向下拖到末项下半区必须只请求移动到最终索引 2");

                var moveThirdToStart = CreateDragEvent(
                    dragItems[2],
                    firstDragContainer,
                    new Point(4, 1));
                firstDragContainer.RaiseEvent(moveThirdToStart);
                Assert(moveRequests.Count == 2 &&
                       ReferenceEquals(moveRequests[1].Item, dragItems[2]) &&
                       moveRequests[1].Index == 0 &&
                       ReferenceEquals(moveThirdToStart.OriginalSource, firstDragContainer),
                    "真实 PreviewDrop 向上拖到首项上半区必须只请求移动到最终索引 0；" +
                    $"实际请求：{string.Join(", ", moveRequests.Select(request => $"{request.Item.Text}->{request.Index}"))}");

                var noOpDrop = CreateDragEvent(
                    dragItems[1],
                    firstDragContainer,
                    new Point(4, Math.Max(1, firstDragContainer.ActualHeight - 1)));
                firstDragContainer.RaiseEvent(noOpDrop);
                Assert(moveRequests.Count == 2,
                    "拖到当前相邻位置不得额外发出无意义的移动请求");
            }
            finally
            {
                SetField(todoWindow, "_todoDragInProgress", false);
            }

            var closingTextBox = FindVisualDescendant<TextBox>(firstDragContainer)
                ?? throw new InvalidOperationException(
                    "Closing IME test cannot find the first todo editor");
            var closingItem = dragItems[0];
            var closingOriginalText = closingItem.Text;
            var editedCountBeforeClose = editedCount;
            Invoke(todoWindow, "BeginTodoEdit", closingTextBox, closingItem);
            closingTextBox.Text = "unfinished IME text during application shutdown";
            SetField(todoWindow, "_imeCompositionOwner", closingTextBox);
            Invoke(todoWindow, "SetImeComposing", true);
            todoWindow.CloseForApplication();
            Assert(!todoWindow.IsVisible &&
                   closingTextBox.IsReadOnly &&
                   !todoWindow.IsImeComposing &&
                   closingItem.Text == closingOriginalText &&
                   editedCount == editedCountBeforeClose,
                "Application shutdown during IME composition must discard the unconfirmed draft instead of saving stale or half-composed text");
        }
        finally
        {
            todoWindow.CloseForApplication();
        }
    }

    private static KeyEventArgs CreateEnterKeyEvent(PresentationSource source) =>
        CreateKeyEvent(source, Key.Enter);

    private static DragEventArgs CreateDragEvent(
        TodoItem item,
        DependencyObject dragTarget,
        Point position)
    {
        var constructor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(IDataObject),
                typeof(DragDropKeyStates),
                typeof(DragDropEffects),
                typeof(DependencyObject),
                typeof(Point)
            },
            modifiers: null)
            ?? throw new InvalidOperationException("找不到 WPF DragEventArgs 内部构造函数");
        var dragEvent = (DragEventArgs)constructor.Invoke(new object[]
        {
            new DataObject(typeof(TodoItem), item),
            DragDropKeyStates.LeftMouseButton,
            DragDropEffects.Move,
            dragTarget,
            position
        });
        dragEvent.RoutedEvent = DragDrop.PreviewDropEvent;
        return dragEvent;
    }

    private static KeyEventArgs CreateKeyEvent(PresentationSource source, Key key) => new(
        Keyboard.PrimaryDevice,
        source,
        Environment.TickCount,
        key)
    {
        RoutedEvent = Keyboard.PreviewKeyDownEvent
    };

    private static void RaisePreviewMouseDown(UIElement target)
    {
        var mouseEvent = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = target
        };
        target.RaiseEvent(mouseEvent);
    }

    private static void AssertPetSizeScaleContract(MainWindow window)
    {
        AssertClose(
            (double)InvokeStatic(typeof(MainWindow), "NormalizePetSizeScale", double.NaN)!,
            1,
            "非有限尺寸应回退默认值");
        AssertClose(
            (double)InvokeStatic(typeof(MainWindow), "NormalizePetSizeScale", 0.1d)!,
            0.75,
            "尺寸下限");
        AssertClose(
            (double)InvokeStatic(typeof(MainWindow), "NormalizePetSizeScale", 2d)!,
            1.40,
            "尺寸上限");
        AssertClose(
            (double)InvokeStatic(typeof(MainWindow), "NormalizePetSizeScale", 1.23d)!,
            1.23,
            "尺寸应保留连续滑块精度");

        var store = GetField<AppSettingsStore>(window, "_settingsStore");
        Invoke(window, "ApplyPetSizeScale", 1.23d, true, false);
        AssertClose(GetField<double>(window, "_petSizeScale"), 1.23, "运行时尺寸比例");
        Assert(Math.Abs(window.Width - 233.7) <= 0.5,
            $"123%桌宠窗口宽度只允许物理像素对齐误差，实际 {window.Width}");
        Assert(Math.Abs(window.Height - 297.66) <= 0.5,
            $"123%桌宠窗口高度只允许物理像素对齐误差，实际 {window.Height}");
        var petHost = GetField<Grid>(window, "PetHost");
        var petVisual = GetField<Grid>(window, "PetVisual");
        Assert(double.IsNaN(petHost.Width) && double.IsNaN(petHost.Height),
            "命中区必须使用拉伸布局，避免每帧重复写尺寸");
        Assert(petHost.Background is null && petVisual.Background == Brushes.Transparent,
            "最大透明包络不得拦截可见人物以外的鼠标点击");
        Assert(Math.Abs(GetField<Viewbox>(window, "PetSizeViewbox").Width - 233.7) <= 0.5,
            "123%视觉缩放宽度必须对齐物理像素");
        Assert(Math.Abs(GetField<Viewbox>(window, "PetSizeViewbox").Height - 297.66) <= 0.5,
            "123%视觉缩放高度必须对齐物理像素");
        AssertClose(GetField<Grid>(window, "PetVisual").Width, 190,
            "缩放后逻辑画布宽度必须保持190");
        AssertClose(GetField<Grid>(window, "PetVisual").Height, 242,
            "缩放后逻辑画布高度必须保持242");

        var savedAfterSize = store.Load();
        AssertClose(savedAfterSize.PetSizeScale, 1.23, "尺寸设置持久化");

        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        var persistedBeforePreview = File.ReadAllText(store.FilePath);
        {
            var originalLeft = window.Left;
            var originalTop = window.Top;
            var originalWidth = window.Width;
            var originalHeight = window.Height;
            var originalViewbox = GetField<Viewbox>(window, "PetSizeViewbox");
            var originalViewboxWidth = originalViewbox.Width;
            var originalViewboxHeight = originalViewbox.Height;
            var sentinelWriteTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(store.FilePath, sentinelWriteTime);
            var writeTimeBeforeGesture = File.GetLastWriteTimeUtc(store.FilePath);

            var gestureStartedAt = Stopwatch.GetTimestamp();
            Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
            var gestureStartElapsed = Stopwatch.GetElapsedTime(gestureStartedAt);
            Assert(GetField<bool>(window, "_isPetSizeAdjustmentActive") &&
                   GetField<bool>(window, "_isPetSizePreviewSessionActive") &&
                   GetField<bool>(window, "_petSizeEnvelopePrepared") &&
                   GetField<bool>(window, "_isVisualClockSubscribed"),
                "滑块按下必须同步建立一次最大预览包络，并让MainWindow保持唯一合成订阅");
            Assert(Math.Abs(window.Width - 266) <= 0.5 &&
                   Math.Abs(window.Height - 338.8) <= 0.5,
                "滑块手势开始阶段必须一次性准备最大透明包络");
            Console.WriteLine(
                $"[METRIC] pet-size gesture-start={gestureStartElapsed.TotalMilliseconds:F3}ms; " +
                "native envelope prepared before composition");

            Invoke(window, "TodoWindow_PetSizeAdjustmentCompleted");
            var unchangedScale = GetField<ScaleTransform>(window, "PetUserSizeScale");
            Assert(!GetField<bool>(window, "_isPetSizeAdjustmentActive") &&
                   !GetField<bool>(window, "_isPetSizePreviewSessionActive") &&
                   !GetField<DispatcherTimer>(window, "_petSizePersistTimer").IsEnabled,
                "按下后未改变数值就松手，必须立即收起预览且不得启动落盘计时器");
            AssertClose(unchangedScale.ScaleX, 1,
                "未改变数值的手势结束后水平视觉比例不得跳动");
            AssertClose(unchangedScale.ScaleY, 1,
                "未改变数值的手势结束后垂直视觉比例不得跳动");
            Assert(Math.Abs(window.Left - originalLeft) <= 0.5 &&
                   Math.Abs(window.Top - originalTop) <= 0.5 &&
                   Math.Abs(window.Width - originalWidth) <= 0.5 &&
                   Math.Abs(window.Height - originalHeight) <= 0.5 &&
                   Math.Abs(originalViewbox.Width - originalViewboxWidth) <= 0.5 &&
                   Math.Abs(originalViewbox.Height - originalViewboxHeight) <= 0.5,
                "未改变数值的手势不得造成桌宠位置或可见尺寸跳变");
            Assert(File.ReadAllText(store.FilePath) == persistedBeforePreview &&
                   File.GetLastWriteTimeUtc(store.FilePath) == writeTimeBeforeGesture,
                "未改变数值的滑块手势不得重写设置文件");
        }

        var transitionDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "PetSizeTransitionDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var persistDelay = (TimeSpan)(typeof(MainWindow).GetField(
                "PetSizePersistDelay",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        Assert(persistDelay >= transitionDuration,
            "松手后收紧透明窗口不得早于临界阻尼动画完成");
        var springFrequency = (double)(typeof(MainWindow).GetField(
                "PetSizeSpringAngularFrequency",
                StaticFlags)!.GetValue(null) ?? 0d);
        var transitionTicks = Math.Max(
            1L,
            (long)Math.Round(transitionDuration.TotalSeconds * Stopwatch.Frequency));
        var controlledStart = Stopwatch.GetTimestamp();
        Invoke(window, "StartPetSizeScaleTransitionAt", 1.40d, controlledStart);
        Assert(GetField<bool>(window, "_petSizeEnvelopePrepared") &&
               Math.Abs(window.Width - 266) <= 0.5 &&
               Math.Abs(window.Height - 338.8) <= 0.5,
            "非手势尺寸动画也必须在进入合成热路径前一次性准备最大包络");
        var sampleTicks = transitionTicks * 3 / 10;
        var sampleSeconds = sampleTicks / (double)Stopwatch.Frequency;
        var expectedSampleScale = 1.40 +
                                  (-0.40 -
                                   (0.40 * springFrequency * sampleSeconds)) *
                                  Math.Exp(-springFrequency * sampleSeconds);
        var sampleScale = (double)Invoke(
            window,
            "GetPetSizeScaleAt",
            controlledStart + sampleTicks)!;
        AssertClose(sampleScale, expectedSampleScale,
            "临界阻尼缩放必须按绝对时间可重现");
        Assert(Math.Abs(sampleScale * 100 - Math.Round(sampleScale * 100)) > 0.05,
            "缩放中间值不得退化为1%或5%阶梯档位");
        AssertClose(
            (double)Invoke(
                window,
                "GetPetSizeScaleAt",
                controlledStart + transitionTicks)!,
            1.40,
            "缩放时钟到期后必须直接定位到最终比例");

        Invoke(window, "AdvancePetSizeTransition", controlledStart + sampleTicks);
        AssertClose(GetField<double>(window, "_petSizeScale"), sampleScale,
            "缩放预览中间比例");
        Assert(Math.Abs(window.Width - 266) <= 0.5,
            "缩放预览期间透明窗口只建立一次最大包络");
        Assert(Math.Abs(window.Height - 338.8) <= 0.5,
            "缩放预览期间透明窗口高度包络");
        var userScale = GetField<ScaleTransform>(window, "PetUserSizeScale");
        var userOffset = GetField<TranslateTransform>(window, "PetUserSizeOffset");
        AssertClose(userScale.ScaleX, userScale.ScaleY,
            "缩放预览必须始终等比，不能因宽高分别取整而抖动");
        AssertClose(userScale.ScaleX, sampleScale,
            "100%预览基准上的变换必须直接保留连续比例");
        var scaleBeforeSubPixelStep = userScale.ScaleX;
        Invoke(window, "ApplyPetSizePreviewScale", sampleScale + 0.001d);
        Assert(userScale.ScaleX > scaleBeforeSubPixelStep,
            "小于一个显示像素的尺寸变化也必须连续呈现，不能取整成阶梯");
        Invoke(window, "ApplyPetSizePreviewScale", sampleScale);
        AssertClose(userOffset.X, 0, "缩放预览不得每帧跳转水平像素偏移");
        AssertClose(userOffset.Y, 0, "缩放预览不得每帧跳转垂直像素偏移");
        Assert(File.ReadAllText(store.FilePath) == persistedBeforePreview,
            "拖动预览期间不得同步写入设置文件");

        Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
        Assert(GetField<bool>(window, "_isPetSizeAdjustmentActive") &&
               !GetField<DispatcherTimer>(window, "_petSizePersistTimer").IsEnabled,
            "按住滑块时必须禁止定时提交");
        var firstRetarget = controlledStart + sampleTicks;
        Invoke(window, "StartPetSizeScaleTransitionAt", 1.35d, firstRetarget);
        AssertClose(GetField<double>(window, "_petSizeTransitionStartVelocity"), 0,
            "滑块目标从140%回拉到135%时必须识别为反向调整");
        var secondRetarget = firstRetarget + Stopwatch.Frequency / 100;
        var visualScaleBeforeRetarget = userScale.ScaleX;
        Invoke(window, "StartPetSizeScaleTransitionAt", 1.38d, secondRetarget);
        AssertClose(userScale.ScaleX, visualScaleBeforeRetarget,
            "滑块输入只能更新动画目标，不能在合成渲染回调之外同步写视觉变换");
        Assert(GetField<double>(window, "_petSizeTransitionStartVelocity") > 0,
            "连续同向拖动不得重置临界阻尼速度");
        var subtleReverseTime = secondRetarget + Stopwatch.Frequency / 100;
        var subtleReverseCurrent = (double)Invoke(
            window,
            "GetPetSizeScaleAt",
            subtleReverseTime)!;
        var subtleReverseTarget = Math.Round(subtleReverseCurrent + 0.004, 3);
        Invoke(
            window,
            "StartPetSizeScaleTransitionAt",
            subtleReverseTarget,
            subtleReverseTime);
        AssertClose(GetField<double>(window, "_petSizeTransitionStartVelocity"), 0,
            "滑块目标已反向时必须取消旧速度，避免先越过目标再回弹");
        var finalRetarget = subtleReverseTime + Stopwatch.Frequency / 100;
        Invoke(window, "StartPetSizeScaleTransitionAt", 1.10d, finalRetarget);
        AssertClose(GetField<double>(window, "_petSizeTargetScale"), 1.10,
            "快速拖动时只保留最新缩放目标");
        Assert(GetField<bool>(window, "_isPetSizeTransitioning") &&
               GetField<bool>(window, "_isVisualClockSubscribed"),
            "最新缩放目标必须由单一合成时钟驱动");
        Assert(!GetField<DispatcherTimer>(window, "_petSizePersistTimer").IsEnabled,
            "滑块尚未松开时不得启动落盘定时器");

        Invoke(window, "PetSizePersistTimer_Tick", null, EventArgs.Empty);
        Assert(GetField<bool>(window, "_isPetSizePreviewSessionActive") &&
               File.ReadAllText(store.FilePath) == persistedBeforePreview,
            "按住滑块停顿时不得提交布局或写盘");
        Invoke(window, "TodoWindow_PetSizeAdjustmentCompleted");
        Assert(GetField<DispatcherTimer>(window, "_petSizePersistTimer").IsEnabled,
            "松开滑块后才应启动延迟提交");
        var finalTransitionStart = GetField<long>(window, "_petSizeTransitionStartedTimestamp");
        Invoke(window, "AdvancePetSizeTransition", finalTransitionStart + transitionTicks);
        Invoke(window, "PetSizePersistTimer_Tick", null, EventArgs.Empty);
        Assert(!GetField<bool>(window, "_isPetSizeTransitioning") &&
               !GetField<bool>(window, "_isPetSizePreviewSessionActive"),
            "停止拖动后必须一次性提交最终尺寸");
        Assert(Math.Abs(window.Width - 209) <= 0.5, "110%提交后窗口宽度");
        Assert(Math.Abs(window.Height - 266.2) <= 0.5, "110%提交后窗口高度");
        AssertClose(userScale.ScaleX, 1, "提交后水平预览变换必须归一");
        AssertClose(userScale.ScaleY, 1, "提交后垂直预览变换必须归一");
        AssertClose(userOffset.X, 0, "提交后水平像素对齐偏移必须归零");
        AssertClose(userOffset.Y, 0, "提交后垂直像素对齐偏移必须归零");
        AssertClose(store.Load().PetSizeScale, 1.10,
            "停止拖动后必须持久化最新尺寸");

        AssertHighFrequencyPetSizeRenderingContract(
            window,
            store,
            transitionDuration);
        AssertFractionalPetSizePreviewBounds(window, transitionDuration);
        AssertPetSizeLogicalAnchorContract(window);
        AssertPetSizeNearEdgePreviewAnchorContract();
        AssertPetSizeNearEdgeTodoFollowContract(window, transitionDuration);
        AssertPetSizeInterruptionContract(window, store);

        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
    }

    private static void AssertHighFrequencyPetSizeRenderingContract(
        MainWindow window,
        AppSettingsStore store,
        TimeSpan transitionDuration)
    {
        const int inputRate = 240;
        const double finalScale = 1.273;
        var transitionTicks = Math.Max(
            1L,
            (long)Math.Round(transitionDuration.TotalSeconds * Stopwatch.Frequency));
        var userScale = GetField<ScaleTransform>(window, "PetUserSizeScale");
        var userOffset = GetField<TranslateTransform>(window, "PetUserSizeOffset");
        var viewbox = GetField<Viewbox>(window, "PetSizeViewbox");
        var scaleXDescriptor = DependencyPropertyDescriptor.FromProperty(
            ScaleTransform.ScaleXProperty,
            typeof(ScaleTransform))
            ?? throw new InvalidOperationException("无法监听桌宠尺寸 ScaleX 变换");
        var scaleYDescriptor = DependencyPropertyDescriptor.FromProperty(
            ScaleTransform.ScaleYProperty,
            typeof(ScaleTransform))
            ?? throw new InvalidOperationException("无法监听桌宠尺寸 ScaleY 变换");
        var scaleXChanges = 0;
        var scaleYChanges = 0;
        EventHandler scaleXChanged = (_, _) => scaleXChanges++;
        EventHandler scaleYChanged = (_, _) => scaleYChanges++;
        scaleXDescriptor.AddValueChanged(userScale, scaleXChanged);
        scaleYDescriptor.AddValueChanged(userScale, scaleYChanged);

        try
        {
            foreach (var refreshRate in new[] { 59, 60, 120, 144 })
            {
                Invoke(window, "ApplyPetSizeScale", 1d, false, false);
                var settingsBeforeGesture = File.ReadAllText(store.FilePath);
                var gestureStartedAt = Stopwatch.GetTimestamp();
                Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
                // This loop drives a synthetic composition clock. A visible
                // WPF window can otherwise receive a real Rendering callback
                // between two synthetic samples and inflate the write count.
                Invoke(window, "StopVisualClock");
                var gestureStartElapsed = Stopwatch.GetElapsedTime(gestureStartedAt);
                Assert(GetField<bool>(window, "_petSizeEnvelopePrepared") &&
                       Math.Abs(window.Width - 266) <= 0.5 &&
                       Math.Abs(window.Height - 338.8) <= 0.5,
                    $"{refreshRate}Hz手势开始必须在Rendering前一次性完成最大原生窗口包络");

                var envelopeLeft = window.Left;
                var envelopeTop = window.Top;
                var envelopeWidth = window.Width;
                var envelopeHeight = window.Height;
                var baseViewboxWidth = viewbox.Width;
                var baseViewboxHeight = viewbox.Height;
                var controlledStart = Stopwatch.GetTimestamp();
                var inputIndex = 1;
                var renderIndex = 1;
                var renderFrames = 0;
                var framesWithScaleChange = 0;
                var firstChangedRender = 0;

                while (inputIndex <= inputRate || renderIndex <= refreshRate)
                {
                    var nextInputSeconds = inputIndex <= inputRate
                        ? inputIndex / (double)inputRate
                        : double.PositiveInfinity;
                    var nextRenderSeconds = renderIndex <= refreshRate
                        ? renderIndex / (double)refreshRate
                        : double.PositiveInfinity;

                    // If input and Rendering share the exact timestamp, render
                    // first. This leaves the final 1.000s input pending and
                    // proves that mouse-up explicitly consumes it.
                    if (nextInputSeconds < nextRenderSeconds)
                    {
                        var beforeScaleXChanges = scaleXChanges;
                        var beforeScaleYChanges = scaleYChanges;
                        Invoke(
                            window,
                            "QueuePetSizeScaleTargetAt",
                            ResolveHighFrequencyPetSizeTarget(
                                inputIndex,
                                inputRate,
                                finalScale),
                            controlledStart + StopwatchTicksFromSeconds(nextInputSeconds));
                        Invoke(window, "StopVisualClock");
                        Assert(scaleXChanges == beforeScaleXChanges &&
                               scaleYChanges == beforeScaleYChanges,
                            $"{inputRate}Hz输入只能覆盖最新目标，不能在Rendering外写变换：" +
                            $"refresh={refreshRate}Hz, input={inputIndex}");
                        AssertPetSizePreviewEnvelopeUnchanged(
                            window,
                            viewbox,
                            envelopeLeft,
                            envelopeTop,
                            envelopeWidth,
                            envelopeHeight,
                            baseViewboxWidth,
                            baseViewboxHeight,
                            $"{refreshRate}Hz第{inputIndex}次输入");

                        inputIndex++;
                        continue;
                    }

                    var beforeRenderScaleXChanges = scaleXChanges;
                    var beforeRenderScaleYChanges = scaleYChanges;
                    Invoke(
                        window,
                        "AdvancePetSizeCompositionFrame",
                        controlledStart + StopwatchTicksFromSeconds(nextRenderSeconds));
                    Invoke(window, "StopVisualClock");
                    var scaleXWrites = scaleXChanges - beforeRenderScaleXChanges;
                    var scaleYWrites = scaleYChanges - beforeRenderScaleYChanges;
                    Assert(scaleXWrites is >= 0 and <= 1 &&
                           scaleYWrites is >= 0 and <= 1 &&
                           scaleXWrites == scaleYWrites,
                        $"每个Rendering最多提交一次等比变换：refresh={refreshRate}Hz, " +
                        $"frame={renderIndex}, ScaleX={scaleXWrites}, ScaleY={scaleYWrites}");
                    if (scaleXWrites == 1)
                    {
                        framesWithScaleChange++;
                        firstChangedRender = firstChangedRender == 0
                            ? renderIndex
                            : firstChangedRender;
                    }

                    AssertPetSizePreviewEnvelopeUnchanged(
                        window,
                        viewbox,
                        envelopeLeft,
                        envelopeTop,
                        envelopeWidth,
                        envelopeHeight,
                        baseViewboxWidth,
                        baseViewboxHeight,
                        $"{refreshRate}Hz第{renderIndex}个渲染帧");

                    Assert(double.IsFinite(userScale.ScaleX) &&
                           Math.Abs(userScale.ScaleX - userScale.ScaleY) < 0.000001,
                        $"{refreshRate}Hz预览必须始终保持有限的等比缩放");
                    var previewBaseScale = GetField<double>(
                        window,
                        "_petSizePreviewBaseScale");
                    var logicalScale = GetField<double>(window, "_petSizeScale");
                    Assert(Math.Abs(userScale.ScaleX - logicalScale / previewBaseScale) < 0.000001,
                        $"{refreshRate}Hz预览必须从高精度逻辑比例计算，不能反馈物理像素结果");
                    AssertClose(userOffset.X, 0,
                        $"{refreshRate}Hz预览水平像素反馈");
                    AssertClose(userOffset.Y, 0,
                        $"{refreshRate}Hz预览垂直像素反馈");
                    renderFrames++;
                    renderIndex++;
                }

                Assert(inputIndex - 1 == inputRate && renderFrames == refreshRate,
                    $"必须完整模拟{inputRate}Hz输入与{refreshRate}Hz Rendering一秒钟");
                Assert(firstChangedRender == 1,
                    $"{refreshRate}Hz下首次Rendering必须立即呈现拖动结果，不能首帧卡住");
                Assert(framesWithScaleChange >= renderFrames - 2,
                    $"{refreshRate}Hz持续拖动期间不得阶梯式停帧：" +
                    $"变化帧={framesWithScaleChange}, 总帧={renderFrames}");
                Assert(GetField<bool>(window, "_petSizeTargetUpdatePending") &&
                       Math.Abs(GetField<double>(window, "_pendingPetSizeTargetScale") -
                                finalScale) < 0.000001,
                    $"{refreshRate}Hz最后输入必须等待松手显式消费");
                Assert(File.ReadAllText(store.FilePath) == settingsBeforeGesture,
                    $"{refreshRate}Hz拖动期间不得写设置文件");

                var transitionStartedBeforeRelease = GetField<long>(
                    window,
                    "_petSizeTransitionStartedTimestamp");
                Invoke(
                    window,
                    "CompletePetSizeAdjustmentAt",
                    controlledStart + StopwatchTicksFromSeconds(1.001));
                Assert(!GetField<bool>(window, "_petSizeTargetUpdatePending") &&
                       Math.Abs(GetField<double>(window, "_petSizeTargetScale") -
                                finalScale) < 0.000001,
                    $"{refreshRate}Hz松手必须显式消费最后目标值");
                Assert(GetField<long>(window, "_petSizeTransitionStartedTimestamp") >=
                       transitionStartedBeforeRelease,
                    $"{refreshRate}Hz松手消费最终值时动画时间轴不得倒退");
                Assert(GetField<DispatcherTimer>(window, "_petSizePersistTimer").IsEnabled,
                    $"{refreshRate}Hz松手后必须安排最终值提交");
                var finalTransitionStarted = GetField<long>(
                    window,
                    "_petSizeTransitionStartedTimestamp");
                var beforeFinalScaleXChanges = scaleXChanges;
                var beforeFinalScaleYChanges = scaleYChanges;
                Invoke(
                    window,
                    "AdvancePetSizeCompositionFrame",
                    finalTransitionStarted + transitionTicks);
                Assert(scaleXChanges - beforeFinalScaleXChanges <= 1 &&
                       scaleYChanges - beforeFinalScaleYChanges <= 1,
                    $"{refreshRate}Hz动画最终帧也只能提交一次变换");
                Invoke(window, "PetSizePersistTimer_Tick", null, EventArgs.Empty);
                AssertClose(store.Load().PetSizeScale, finalScale,
                    $"{refreshRate}Hz松手后必须准确保存最后输入值");
                Assert(!GetField<bool>(window, "_isPetSizeTransitioning") &&
                       !GetField<bool>(window, "_isPetSizePreviewSessionActive"),
                    $"{refreshRate}Hz最终值保存后必须结束预览会话");

                Console.WriteLine(
                    $"[METRIC] pet-size input={inputRate}Hz render={refreshRate}Hz: " +
                    $"changed={framesWithScaleChange}/{renderFrames}, " +
                    $"gesture-start={gestureStartElapsed.TotalMilliseconds:F3}ms, " +
                    $"saved={store.Load().PetSizeScale:F3}");
            }
        }
        finally
        {
            scaleXDescriptor.RemoveValueChanged(userScale, scaleXChanged);
            scaleYDescriptor.RemoveValueChanged(userScale, scaleYChanged);
        }
    }

    private static void AssertFractionalPetSizePreviewBounds(
        MainWindow window,
        TimeSpan transitionDuration)
    {
        const double baseScale = 0.75;
        const double targetScale = 1.40;
        const double petWidth = 190;
        const double petHeight = 242;
        Invoke(window, "ApplyPetSizeScale", baseScale, false, false);
        Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
        var startedAt = Stopwatch.GetTimestamp();
        Invoke(window, "QueuePetSizeScaleTargetAt", targetScale, startedAt);
        Invoke(
            window,
            "AdvancePetSizeCompositionFrame",
            startedAt + (long)Math.Ceiling(
                transitionDuration.TotalSeconds * Stopwatch.Frequency));
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();

        var viewbox = GetField<Viewbox>(window, "PetSizeViewbox");
        viewbox.Measure(new Size(viewbox.Width, viewbox.Height));
        viewbox.Arrange(new Rect(0, 0, viewbox.Width, viewbox.Height));
        viewbox.UpdateLayout();
        var userScale = GetField<ScaleTransform>(window, "PetUserSizeScale");
        Assert(!viewbox.UseLayoutRounding,
            "75% 到 140% 的预览容器不得继承根窗口布局取整");
        var renderedWidth = viewbox.ActualWidth * Math.Abs(userScale.ScaleX);
        var renderedHeight = viewbox.ActualHeight * Math.Abs(userScale.ScaleY);
        foreach (var dpiScale in new[] { 1d, 1.25d, 1.5d })
        {
            var widthErrorPixels = Math.Abs(renderedWidth - petWidth * targetScale) * dpiScale;
            var heightErrorPixels = Math.Abs(renderedHeight - petHeight * targetScale) * dpiScale;
            Assert(widthErrorPixels <= 0.5 + 1e-9 && heightErrorPixels <= 0.5 + 1e-9,
                $"75%->140% 预览在 {dpiScale * 100:F0}% DPI 下不得裁切或回缩：" +
                $"widthError={widthErrorPixels:F3}px, heightError={heightErrorPixels:F3}px");
        }

        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        SetField(window, "_isPetSizeAdjustmentActive", false);
        SetField(window, "_petSizeAdjustmentValueChanged", false);
    }

    private static double ResolveHighFrequencyPetSizeTarget(
        int inputIndex,
        int inputRate,
        double finalScale)
    {
        var progress = inputIndex / (double)inputRate;
        var scale = progress <= 1d / 3d
            ? 1 + 0.4 * progress * 3
            : progress <= 2d / 3d
                ? 1.4 - 0.65 * (progress - 1d / 3d) * 3
                : 0.75 + (finalScale - 0.75) * (progress - 2d / 3d) * 3;
        return inputIndex == inputRate
            ? finalScale
            : Math.Round(scale, 3, MidpointRounding.AwayFromZero);
    }

    private static void AssertPetSizePreviewEnvelopeUnchanged(
        MainWindow window,
        Viewbox viewbox,
        double expectedLeft,
        double expectedTop,
        double expectedWidth,
        double expectedHeight,
        double expectedViewboxWidth,
        double expectedViewboxHeight,
        string stage)
    {
        Assert(Math.Abs(window.Left - expectedLeft) < 0.000001 &&
               Math.Abs(window.Top - expectedTop) < 0.000001 &&
               Math.Abs(window.Width - expectedWidth) < 0.000001 &&
               Math.Abs(window.Height - expectedHeight) < 0.000001 &&
               Math.Abs(viewbox.Width - expectedViewboxWidth) < 0.000001 &&
               Math.Abs(viewbox.Height - expectedViewboxHeight) < 0.000001,
            $"{stage}不得重写窗口布局或把物理像素取整结果反馈给下一帧");
    }

    private static void AssertPetSizeLogicalAnchorContract(MainWindow window)
    {
        var workArea = new Rect(-1920, -180, 1920, 1080);
        var initialBounds = new Rect(-1427.37, 181.23, 190, 242);
        var anchor = InvokeStatic(
            typeof(MainWindow),
            "CreatePetSizeAnchor",
            workArea,
            initialBounds)!;
        var scales = new[] { 0.75, 0.83, 0.97, 1.11, 1.27, 1.40 };
        foreach (var dpiScale in new[] { 1d, 1.25, 1.5 })
        {
            var expectedByScale = new Dictionary<double, Rect>();
            for (var cycle = 0; cycle < 600; cycle++)
            {
                foreach (var scale in scales)
                {
                    var bounds = (Rect)InvokeStatic(
                        typeof(MainWindow),
                        "CalculatePetSizeWindowBounds",
                        scale,
                        anchor,
                        dpiScale,
                        dpiScale)!;
                    var centerErrorInPixels = Math.Abs(
                        (bounds.Left + bounds.Width / 2 -
                         (initialBounds.Left + initialBounds.Width / 2)) * dpiScale);
                    var bottomErrorInPixels = Math.Abs(
                        (bounds.Bottom - initialBounds.Bottom) * dpiScale);
                    Assert(centerErrorInPixels <= 0.500001 &&
                           bottomErrorInPixels <= 0.500001,
                        $"负坐标副屏{dpiScale:P0} DPI下逻辑锚点误差不得超过半个物理像素：" +
                        $"cycle={cycle}, scale={scale:F2}, " +
                        $"center={centerErrorInPixels:F3}px, bottom={bottomErrorInPixels:F3}px");
                    if (expectedByScale.TryGetValue(scale, out var expected))
                    {
                        AssertRectClose(bounds, expected,
                            $"{dpiScale:P0} DPI重复600轮不得累计物理像素反馈漂移");
                    }
                    else
                    {
                        expectedByScale.Add(scale, bounds);
                    }
                }
            }

            Console.WriteLine(
                $"[METRIC] pet-size logical-anchor dpi={dpiScale:P0}: " +
                "600 cycles, drift=0px, negative-monitor=true");
        }

        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        var liveAnchor = Invoke(window, "CapturePetSizeAnchor", true);
        Assert(liveAnchor is not null &&
               GetRawField(window, "_petSizeLogicalAnchor") is not null,
            "运行时必须缓存未取整的高精度尺寸锚点");
        SetField(window, "_isApplyingPetSizeLayout", true);
        Invoke(window, "Window_LocationChanged", null, EventArgs.Empty);
        Assert(GetRawField(window, "_petSizeLogicalAnchor") is not null,
            "尺寸布局自身的物理像素写入不得清除逻辑锚点");
        SetField(window, "_isApplyingPetSizeLayout", false);
        Invoke(window, "Window_LocationChanged", null, EventArgs.Empty);
        Assert(GetRawField(window, "_petSizeLogicalAnchor") is null,
            "真实拖动或显示器位置变化后必须重置逻辑锚点");
    }

    private static void AssertPetSizeNearEdgePreviewAnchorContract()
    {
        const double petWidth = 190;
        const double petHeight = 242;
        const double edgeGap = 12;
        var maximumFirstFrameDriftPixels = 0d;
        var maximumCommitDriftPixels = 0d;
        var defaultRightOffset = 0d;

        foreach (var workArea in new[]
                 {
                     new Rect(0, 0, 1920, 1080),
                     new Rect(-2560, -180, 2560, 1440)
                 })
        {
            var centeredLeft = workArea.Left + (workArea.Width - petWidth) / 2;
            var centeredTop = workArea.Top + (workArea.Height - petHeight) / 2;
            var nearLeft = workArea.Left + edgeGap;
            var nearRight = workArea.Right - edgeGap - petWidth;
            var nearTop = workArea.Top + edgeGap;
            var nearBottom = workArea.Bottom - edgeGap - petHeight;
            var cases = new[]
            {
                (Name: "left", Bounds: new Rect(nearLeft, centeredTop, petWidth, petHeight)),
                (Name: "right", Bounds: new Rect(nearRight, centeredTop, petWidth, petHeight)),
                (Name: "top", Bounds: new Rect(centeredLeft, nearTop, petWidth, petHeight)),
                (Name: "bottom", Bounds: new Rect(centeredLeft, nearBottom, petWidth, petHeight)),
                (Name: "top-left", Bounds: new Rect(nearLeft, nearTop, petWidth, petHeight)),
                (Name: "top-right", Bounds: new Rect(nearRight, nearTop, petWidth, petHeight)),
                (Name: "bottom-left", Bounds: new Rect(nearLeft, nearBottom, petWidth, petHeight)),
                (Name: "bottom-right", Bounds: new Rect(nearRight, nearBottom, petWidth, petHeight))
            };

            foreach (var (name, initialBounds) in cases)
            {
                var anchor = InvokeStatic(
                    typeof(MainWindow),
                    "CreatePetSizeAnchor",
                    workArea,
                    initialBounds)!;
                var preserveLeft = GetProperty<bool>(anchor, "PreserveLeftEdge");
                var preserveRight = GetProperty<bool>(anchor, "PreserveRightEdge");
                var preserveTop = GetProperty<bool>(anchor, "PreserveTopEdge");
                Assert(!preserveLeft && !preserveRight && !preserveTop,
                    $"{name}的12 DIP近边样本必须保持非贴边语义");
                var originX = preserveLeft ? 0d : preserveRight ? 1d : 0.5d;
                var originY = preserveTop ? 0d : 1d;

                foreach (var dpiScale in new[] { 1d, 1.25d, 1.5d })
                {
                    var envelopeBounds = (Rect)InvokeStatic(
                        typeof(MainWindow),
                        "CalculatePetSizeWindowBounds",
                        1.40d,
                        anchor,
                        dpiScale,
                        dpiScale)!;
                    Rect? previousVisibleBounds = null;
                    for (var scaleStep = 750; scaleStep <= 1400; scaleStep++)
                    {
                        var scale = scaleStep / 1000d;
                        var desiredBounds = (Rect)InvokeStatic(
                            typeof(MainWindow),
                            "CalculatePetSizeLogicalWindowBounds",
                            scale,
                            anchor)!;
                        var offset = (Vector)InvokeStatic(
                            typeof(MainWindow),
                            "CalculatePetSizePreviewOffset",
                            scale,
                            anchor,
                            envelopeBounds)!;
                        var visibleBounds = new Rect(
                            envelopeBounds.Left + originX * envelopeBounds.Width -
                            originX * petWidth * scale + offset.X,
                            envelopeBounds.Top + originY * envelopeBounds.Height -
                            originY * petHeight * scale + offset.Y,
                            petWidth * scale,
                            petHeight * scale);
                        AssertRectClose(
                            visibleBounds,
                            desiredBounds,
                            $"{name}/{dpiScale:P0}/{scale:P1}预览必须复现当前尺寸的逻辑clamp位置");

                        if (Math.Abs(scale - 1d) < 0.000001)
                        {
                            var firstFrameDriftPixels = Math.Max(
                                Math.Abs(visibleBounds.Left - initialBounds.Left) * dpiScale,
                                Math.Abs(visibleBounds.Top - initialBounds.Top) * dpiScale);
                            maximumFirstFrameDriftPixels = Math.Max(
                                maximumFirstFrameDriftPixels,
                                firstFrameDriftPixels);
                            Assert(firstFrameDriftPixels <= 0.000001,
                                $"{name}/{dpiScale:P0}首次140%包络不得移动当前可见人物：" +
                                $"drift={firstFrameDriftPixels:F6}px");
                            if (workArea.Left == 0 && name == "right" && dpiScale == 1d)
                            {
                                defaultRightOffset = offset.X;
                            }
                        }

                        if (previousVisibleBounds is { } previous)
                        {
                            var maximumPositionStep = Math.Max(
                                Math.Abs(visibleBounds.Left - previous.Left),
                                Math.Abs(visibleBounds.Top - previous.Top));
                            Assert(maximumPositionStep <= 0.243,
                                $"{name}/{dpiScale:P0}逐0.1%预览位置不得阶梯跳动：" +
                                $"step={maximumPositionStep:F6} DIP");
                        }

                        previousVisibleBounds = visibleBounds;

                        var committedBounds = (Rect)InvokeStatic(
                            typeof(MainWindow),
                            "CalculatePetSizeWindowBounds",
                            scale,
                            anchor,
                            dpiScale,
                            dpiScale)!;
                        var commitDriftPixels = Math.Max(
                            Math.Abs(committedBounds.Left - visibleBounds.Left) * dpiScale,
                            Math.Abs(committedBounds.Top - visibleBounds.Top) * dpiScale);
                        maximumCommitDriftPixels = Math.Max(
                            maximumCommitDriftPixels,
                            commitDriftPixels);
                        Assert(commitDriftPixels <= 0.500001,
                            $"{name}/{dpiScale:P0}/{scale:P1}提交只能产生物理像素对齐误差：" +
                            $"drift={commitDriftPixels:F6}px");
                    }
                }
            }
        }

        AssertClose(defaultRightOffset, 26,
            "默认离右边12 DIP时首次包络必须补回原有26 DIP中心差");
        Console.WriteLine(
            $"[METRIC] pet-size near-edge anchor: first-frame={maximumFirstFrameDriftPixels:F6}px, " +
            $"commit<={maximumCommitDriftPixels:F3}px, default-right-offset={defaultRightOffset:F1} DIP");
    }

    private static void AssertPetSizeNearEdgeTodoFollowContract(
        MainWindow window,
        TimeSpan transitionDuration)
    {
        const double edgeGap = 12;
        var originalLeft = window.Left;
        var originalTop = window.Top;
        var todoWindow = GetField<TodoWindow>(window, "_todoWindow");
        var positionCache = GetRawField(window, "_todoWindowPositionCache")!;
        var viewbox = GetField<Viewbox>(window, "PetSizeViewbox");
        var sizeSlider = GetField<Slider>(todoWindow, "PetSizeSlider");
        SetField(window, "_suppressTodoWindowDeactivate", true);
        try
        {
            if (!window.IsVisible)
            {
                window.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
            }

            Invoke(window, "ApplyPetSizeScale", 1d, false, false);
            var workArea = (Rect)InvokeStatic(
                typeof(MainWindow).Assembly.GetType(
                    "LubanDesktopPet.MonitorWorkArea",
                    throwOnError: true)!,
                "GetForWindow",
                window)!;
            window.Left = workArea.Right - window.Width - edgeGap;
            window.Top = workArea.Bottom - window.Height - edgeGap;
            window.UpdateLayout();

            if (!todoWindow.IsVisible)
            {
                todoWindow.Show();
                PumpDispatcher(TimeSpan.FromMilliseconds(30));
            }

            Invoke(positionCache, "InvalidateGeometry");
            Invoke(window, "UpdateTodoWindowPosition");
            // The full suite reuses this owned window after tests that can put
            // its 12-DIP tail on the opposite side. Settle that internal Grid
            // move before taking the Track screen-coordinate baseline; otherwise
            // the first later layout pass looks like Slider HWND movement even
            // though TodoWindow.Left/Top stayed fixed.
            todoWindow.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            Invoke(positionCache, "InvalidateGeometry");
            Invoke(window, "UpdateTodoWindowPosition");
            todoWindow.UpdateLayout();
            var initialPetBounds = GetVisualPhysicalBounds(viewbox);
            AssertTodoWindowFollowsPet(positionCache, initialPetBounds, "近右下初始位置");
            var sliderTrack = FindVisualDescendant<Track>(sizeSlider)
                ?? throw new InvalidOperationException("桌宠尺寸滑块找不到 Track");
            var frozenTodoLeft = todoWindow.Left;
            var frozenTodoTop = todoWindow.Top;
            var frozenTrackOrigin = sliderTrack.PointToScreen(new Point(0, 0));

            Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
            Invoke(window, "AdvancePetSizeCompositionFrame", Stopwatch.GetTimestamp());
            window.UpdateLayout();
            var firstPreviewBounds = GetVisualPhysicalBounds(viewbox);
            Assert(Math.Abs(firstPreviewBounds.Left - initialPetBounds.Left) <= 0.500001 &&
                   Math.Abs(firstPreviewBounds.Top - initialPetBounds.Top) <= 0.500001 &&
                   Math.Abs(firstPreviewBounds.Right - initialPetBounds.Right) <= 0.500001 &&
                   Math.Abs(firstPreviewBounds.Bottom - initialPetBounds.Bottom) <= 0.500001,
                "默认近右下12 DIP首次准备140%透明包络时，可见人物不得移动超过半个物理像素");
            AssertClose(todoWindow.Left, frozenTodoLeft,
                "尺寸手势期间 TodoWindow.Left 必须冻结");
            AssertClose(todoWindow.Top, frozenTodoTop,
                "尺寸手势期间 TodoWindow.Top 必须冻结");
            AssertClose(sliderTrack.PointToScreen(new Point(0, 0)).X, frozenTrackOrigin.X,
                "尺寸手势期间 Slider Track 物理 X 必须冻结");
            AssertClose(sliderTrack.PointToScreen(new Point(0, 0)).Y, frozenTrackOrigin.Y,
                "尺寸手势期间 Slider Track 物理 Y 必须冻结");
            Assert(GetField<bool>(window, "_petSizeTodoPositionNeedsUpdate"),
                "尺寸手势期间人物变化必须保留 Todo 跟随 dirty，不能逐帧移动滑块所属窗口");

            var transitionStartedAt = Stopwatch.GetTimestamp();
            var transitionTicks = (long)Math.Ceiling(
                transitionDuration.TotalSeconds * Stopwatch.Frequency);
            var transitionMidpointAt = transitionStartedAt + transitionTicks / 2;
            var transitionCompletedAt = transitionStartedAt + transitionTicks;
            Invoke(window, "QueuePetSizeScaleTargetAt", 1.273d, transitionStartedAt);
            Invoke(
                window,
                "AdvancePetSizeCompositionFrame",
                transitionMidpointAt);
            window.UpdateLayout();
            AssertClose(todoWindow.Left, frozenTodoLeft,
                "连续变更尺寸期间 TodoWindow.Left 必须保持冻结");
            AssertClose(todoWindow.Top, frozenTodoTop,
                "连续变更尺寸期间 TodoWindow.Top 必须保持冻结");
            var finalFrozenTrackOrigin = sliderTrack.PointToScreen(new Point(0, 0));
            AssertClose(finalFrozenTrackOrigin.X, frozenTrackOrigin.X,
                "连续变更尺寸期间 Slider Track 物理 X 必须保持冻结");
            AssertClose(finalFrozenTrackOrigin.Y, frozenTrackOrigin.Y,
                "连续变更尺寸期间 Slider Track 物理 Y 必须保持冻结");
            Assert(GetField<bool>(window, "_petSizeTodoPositionNeedsUpdate"),
                "尺寸手势结束前必须一直保留 Todo 跟随 dirty");

            var completedAt = transitionMidpointAt + 1;
            Invoke(window, "CompletePetSizeAdjustmentAt", completedAt);
            AssertClose(todoWindow.Left, frozenTodoLeft,
                "Complete 回调内不得同步移动 TodoWindow.Left");
            AssertClose(todoWindow.Top, frozenTodoTop,
                "Complete 回调内不得同步移动 TodoWindow.Top");
            Assert(GetField<bool>(window, "_petSizeTodoPositionNeedsUpdate"),
                "Complete 后下一合成帧前必须保留 Todo 跟随 dirty");
            Assert(GetField<bool>(window, "_isPetSizeTransitioning") &&
                   !GetField<bool>(window, "_todoPositionUpdateQueued"),
                "弹簧中途松手后必须继续视觉过渡，且不得提前排队移动第二 HWND");

            var postReleaseMidpointAt = completedAt + Stopwatch.Frequency / 60;
            Invoke(window, "AdvancePetSizeCompositionFrame", postReleaseMidpointAt);
            Assert(GetField<bool>(window, "_isPetSizeTransitioning") &&
                   GetField<bool>(window, "_petSizeTodoPositionNeedsUpdate") &&
                   !GetField<bool>(window, "_todoPositionUpdateQueued") &&
                   Math.Abs(todoWindow.Left - frozenTodoLeft) <= 0.000001 &&
                   Math.Abs(todoWindow.Top - frozenTodoTop) <= 0.000001,
                "弹簧未结束的松手后合成帧必须继续冻结 Todo/Slider HWND");

            Invoke(window, "AdvancePetSizeCompositionFrame", transitionCompletedAt + 1);
            window.UpdateLayout();
            var finalPreviewBounds = GetVisualPhysicalBounds(viewbox);
            Assert(!GetField<bool>(window, "_petSizeTodoPositionNeedsUpdate"),
                "弹簧终帧必须消费 Todo 跟随 dirty");
            Assert(GetField<bool>(window, "_todoPositionUpdateQueued") &&
                   Math.Abs(todoWindow.Left - frozenTodoLeft) <= 0.000001 &&
                   Math.Abs(todoWindow.Top - frozenTodoTop) <= 0.000001,
                "合成终帧只允许排队缓存委托，不能在 Rendering 内同步移动第二 HWND");
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            window.UpdateLayout();
            Assert(!GetField<bool>(window, "_todoPositionUpdateQueued"),
                "Render优先级的 Todo 跟随回调必须在 Dispatcher 泵后完成");
            AssertTodoWindowFollowsPet(positionCache, finalPreviewBounds, "Complete 后首个合成帧");
            var followedTodoOrigin = todoWindow.PointToScreen(new Point(0, 0));
            var followedTodoLeft = GetField<int>(positionCache, "_lastLeft");
            var followedTodoTop = GetField<int>(positionCache, "_lastTop");
            Assert(Math.Abs(followedTodoOrigin.X - followedTodoLeft) <= 1.000001 &&
                   Math.Abs(followedTodoOrigin.Y - followedTodoTop) <= 1.000001,
                "Complete 后下一 Composition 帧的 Todo 实际位置必须在最终人物目标的1个物理像素内");

            Invoke(window, "PetSizePersistTimer_Tick", null, EventArgs.Empty);
            window.UpdateLayout();
            PumpDispatcher(TimeSpan.FromMilliseconds(20));
            var committedBounds = GetVisualPhysicalBounds(viewbox);
            Assert(Math.Abs(committedBounds.Left - finalPreviewBounds.Left) <= 1.000001 &&
                   Math.Abs(committedBounds.Top - finalPreviewBounds.Top) <= 1.000001 &&
                   Math.Abs(committedBounds.Right - finalPreviewBounds.Right) <= 1.000001 &&
                   Math.Abs(committedBounds.Bottom - finalPreviewBounds.Bottom) <= 1.000001,
                "近边预览提交不得跳回；只允许最终物理像素对齐误差");
            AssertTodoWindowFollowsPet(positionCache, committedBounds, "127.3%提交后");

            Console.WriteLine(
                "[METRIC] pet-size near-edge Todo follow: gesture-hwnd=frozen, release-frame<=1px, commit<=1px");
        }
        finally
        {
            if (GetField<bool>(window, "_isPetSizePreviewSessionActive"))
            {
                Invoke(window, "CommitPetSizePreviewSession", false);
            }

            todoWindow.Hide();
            Invoke(window, "ApplyPetSizeScale", 1d, false, false);
            window.Left = originalLeft;
            window.Top = originalTop;
            SetField(window, "_suppressTodoWindowDeactivate", false);
        }
    }

    private static Rect GetVisualPhysicalBounds(FrameworkElement visual)
    {
        var topLeft = visual.PointToScreen(new Point(0, 0));
        var bottomRight = visual.PointToScreen(
            new Point(visual.ActualWidth, visual.ActualHeight));
        return new Rect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private static void AssertTodoWindowFollowsPet(
        object positionCache,
        Rect petBounds,
        string stage)
    {
        var workArea = GetRawField(positionCache, "_workArea")!;
        var childWidth = GetField<int>(positionCache, "_childWidth");
        var childHeight = GetField<int>(positionCache, "_childHeight");
        var childLeft = GetField<int>(positionCache, "_lastLeft");
        var childTop = GetField<int>(positionCache, "_lastTop");
        var workLeft = GetField<int>(workArea, "Left");
        var workTop = GetField<int>(workArea, "Top");
        var workRight = GetField<int>(workArea, "Right");
        var workBottom = GetField<int>(workArea, "Bottom");
        var petLeft = (int)Math.Round(petBounds.Left);
        var petRight = (int)Math.Round(petBounds.Right);
        var petBottom = (int)Math.Round(petBounds.Bottom);
        var childIsOnLeft = childLeft <= petLeft;
        var expectedLeft = childIsOnLeft
            ? petLeft - childWidth
            : petRight;
        expectedLeft = Math.Clamp(
            expectedLeft,
            workLeft,
            Math.Max(workLeft, workRight - childWidth));
        var expectedTop = Math.Clamp(
            petBottom - childHeight,
            workTop,
            Math.Max(workTop, workBottom - childHeight));
        Assert(childLeft == expectedLeft && childTop == expectedTop,
            $"{stage}待办必须跟随人物当前物理像素边界：" +
            $"actual=({childLeft},{childTop}), expected=({expectedLeft},{expectedTop})");
    }

    private static void AssertPetSizeInterruptionContract(
        MainWindow window,
        AppSettingsStore store)
    {
        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        var shutdownTimestamp = Stopwatch.GetTimestamp();
        Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
        Invoke(
            window,
            "QueuePetSizeScaleTargetAt",
            1.337d,
            shutdownTimestamp);
        Assert(GetField<bool>(window, "_petSizeTargetUpdatePending"),
            "关闭中断回归必须先建立尚未被Rendering消费的最终输入");
        Invoke(
            window,
            "PersistLatestPetSizeForShutdownAt",
            shutdownTimestamp + StopwatchTicksFromSeconds(0.001));
        Assert(!GetField<bool>(window, "_petSizeTargetUpdatePending") &&
               Math.Abs(GetField<double>(window, "_petSizeTargetScale") - 1.337) < 0.000001,
            "关闭前必须先折叠MainWindow尚未消费的最终尺寸目标");
        AssertClose(store.Load().PetSizeScale, 1.337,
            "关闭前必须准确保存Rendering尚未消费的最终尺寸");

        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        var displayChangeTimestamp = Stopwatch.GetTimestamp();
        Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
        Invoke(
            window,
            "QueuePetSizeScaleTargetAt",
            1.219d,
            displayChangeTimestamp);
        Assert(GetField<bool>(window, "_petSizeTargetUpdatePending"),
            "显示器切换回归必须先建立尚未被Rendering消费的最终输入");
        Invoke(
            window,
            "ConsumeLatestPetSizeInputAt",
            displayChangeTimestamp + StopwatchTicksFromSeconds(0.001));
        Assert(!GetField<bool>(window, "_petSizeTargetUpdatePending") &&
               Math.Abs(GetField<double>(window, "_petSizeTargetScale") - 1.219) < 0.000001,
            "显示器切换提交布局前必须先消费最后尺寸目标");
        Invoke(window, "CommitPetSizePreviewSession", true);
        AssertClose(store.Load().PetSizeScale, 1.219,
            "显示器切换中断后必须保存而不是吞掉最后尺寸目标");

        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        SetField(window, "_isPetSizeAdjustmentActive", false);
        SetField(window, "_petSizeAdjustmentValueChanged", false);
    }

    private static long StopwatchTicksFromSeconds(double seconds) =>
        (long)Math.Round(seconds * Stopwatch.Frequency);

    private static void AssertLoggingContract()
    {
        var loggerType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.AppLogger",
            throwOnError: true)!;
        var probe = $"ui-state-check-{Guid.NewGuid():N}";
        InvokeStatic(loggerType, "Initialize");
        InvokeStatic(loggerType, "Info", probe);
        Assert((bool)(InvokeStatic(loggerType, "Flush", TimeSpan.FromSeconds(2)) ?? false),
            "后台日志队列必须能在2秒内安全刷新");
        var loggerSource = File.ReadAllText(FindWorkspaceFile("AppLogger.cs"));
        Assert(loggerSource.Contains("QueueCapacity = 256", StringComparison.Ordinal) &&
               loggerSource.Contains("BlockingCollection", StringComparison.Ordinal) &&
               loggerSource.Contains("IsBackground = true", StringComparison.Ordinal) &&
               loggerSource.Contains("_closing", StringComparison.Ordinal) &&
               loggerSource.Contains("CompleteAdding", StringComparison.Ordinal),
            "日志必须使用容量256的后台队列，并以关闭屏障安全排空，渲染线程不得直接等待磁盘写入");
        var directory = (string)(loggerType.GetProperty(
                "LogDirectory",
                BindingFlags.Static | BindingFlags.Public)!
            .GetValue(null) ?? string.Empty);
        Assert(string.Equals(Path.GetFileName(directory), "log", StringComparison.OrdinalIgnoreCase),
            "日志必须写入 log 文件夹");
        var logPath = Path.Combine(directory, $"xlb-pet-{DateTimeOffset.Now:yyyy-MM-dd}.log");
        Assert(File.Exists(logPath) &&
               File.ReadAllText(logPath).Contains(probe, StringComparison.Ordinal),
            "当天日志文件应包含本次唯一探针");

        var appSource = File.ReadAllText(FindWorkspaceFile("App.xaml.cs"));
        var appXaml = File.ReadAllText(FindWorkspaceFile("App.xaml"));
        Assert(loggerSource.Contains("MaxLogFileBytes = 2L * 1024 * 1024", StringComparison.Ordinal) &&
               loggerSource.Contains("MaxRetainedLogFiles = 8", StringComparison.Ordinal) &&
               loggerSource.Contains("MaxTotalLogBytes = 8L * 1024 * 1024", StringComparison.Ordinal) &&
               loggerSource.Contains("TimeSpan.FromDays(14)", StringComparison.Ordinal) &&
               loggerSource.Contains("MaxLogEntryBytes = 32 * 1024", StringComparison.Ordinal),
            "日志必须同时限制单文件、文件数、目录总字节、保留天数和单条大小");
        Assert(appSource.Contains("SingleInstanceMutexName", StringComparison.Ordinal) &&
               appSource.Contains("new Mutex(", StringComparison.Ordinal) &&
               appSource.Contains("WaitOne(0)", StringComparison.Ordinal) &&
               appSource.Contains("AbandonedMutexException", StringComparison.Ordinal) &&
               appSource.Contains("AppLogger.Shutdown", StringComparison.Ordinal) &&
               appSource.Contains("Shutdown();", StringComparison.Ordinal),
            "应用必须取得会话内命名Mutex、接管废弃锁并在释放锁前排空日志，避免重复占用内存和并发写盘");
        Assert(!appXaml.Contains("StartupUri", StringComparison.Ordinal) &&
               appSource.Contains("var mainWindow = new MainWindow();", StringComparison.Ordinal) &&
               appSource.Contains("MainWindow = mainWindow;", StringComparison.Ordinal) &&
               appSource.Contains("mainWindow.Show();", StringComparison.Ordinal),
            "主窗口必须在取得单实例锁后显式创建，第二实例不得短暂解码图集或闪出窗口");

        var maintenanceDirectory = Path.Combine(
            Path.GetTempPath(),
            $"xlb-pet-log-retention-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(maintenanceDirectory);
            var now = DateTimeOffset.Now;
            var activePath = Path.Combine(
                maintenanceDirectory,
                $"xlb-pet-{now:yyyy-MM-dd}.log");
            var block = new byte[900 * 1024];
            File.WriteAllBytes(activePath, block.AsSpan(0, 512 * 1024).ToArray());
            for (var day = 1; day <= 10; day++)
            {
                var timestamp = now.AddDays(-day);
                var path = Path.Combine(
                    maintenanceDirectory,
                    $"xlb-pet-{timestamp:yyyy-MM-dd}.log");
                File.WriteAllBytes(path, block);
                File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
            }

            var expiredTimestamp = now.AddDays(-20);
            var expiredPath = Path.Combine(
                maintenanceDirectory,
                $"xlb-pet-{expiredTimestamp:yyyy-MM-dd}.001.log");
            File.WriteAllBytes(expiredPath, block);
            File.SetLastWriteTimeUtc(expiredPath, expiredTimestamp.UtcDateTime);
            var unrelatedLogPath = Path.Combine(
                maintenanceDirectory,
                $"xlb-pet-{now:yyyy-MM-dd}.extra.log");
            var todoSentinelPath = Path.Combine(maintenanceDirectory, "todos.json");
            File.WriteAllText(unrelatedLogPath, "unrelated");
            File.WriteAllText(todoSentinelPath, "todo-sentinel");

            InvokeStatic(
                loggerType,
                "MaintainLogDirectory",
                maintenanceDirectory,
                activePath,
                now);
            var managedFiles = Directory.EnumerateFiles(
                    maintenanceDirectory,
                    "*.log",
                    SearchOption.TopDirectoryOnly)
                .Where(path => (bool)(InvokeStatic(
                    loggerType,
                    "IsManagedLogFile",
                    path) ?? false))
                .Select(path => new FileInfo(path))
                .ToArray();
            Assert(managedFiles.Length <= 8 &&
                   managedFiles.Sum(file => file.Length) <= 8L * 1024 * 1024 &&
                   File.Exists(activePath) &&
                   !File.Exists(expiredPath),
                "日志维护必须保留当前文件并收敛到8个文件/8MiB/14天范围内");
            Assert(File.Exists(unrelatedLogPath) &&
                   File.ReadAllText(todoSentinelPath) == "todo-sentinel",
                "日志维护只能删除严格命名的桌宠日志，不能触碰其他日志或用户JSON");

            var lockedDirectory = Path.Combine(maintenanceDirectory, "locked-candidate");
            Directory.CreateDirectory(lockedDirectory);
            var lockedActivePath = Path.Combine(
                lockedDirectory,
                $"xlb-pet-{now:yyyy-MM-dd}.log");
            File.WriteAllBytes(lockedActivePath, block.AsSpan(0, 512 * 1024).ToArray());
            var lockedPath = Path.Combine(
                lockedDirectory,
                $"xlb-pet-{now.AddDays(-13):yyyy-MM-dd}.log");
            File.WriteAllBytes(lockedPath, block);
            File.SetLastWriteTimeUtc(lockedPath, now.AddDays(-13).UtcDateTime);
            for (var day = 1; day <= 10; day++)
            {
                var timestamp = now.AddDays(-day);
                var path = Path.Combine(
                    lockedDirectory,
                    $"xlb-pet-{timestamp:yyyy-MM-dd}.log");
                File.WriteAllBytes(path, block);
                File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
            }

            using (var lockedStream = new FileStream(
                       lockedPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.Read))
            {
                InvokeStatic(
                    loggerType,
                    "MaintainLogDirectory",
                    lockedDirectory,
                    lockedActivePath,
                    now);
                var remainingWithLockedCandidate = Directory.EnumerateFiles(
                        lockedDirectory,
                        "*.log",
                        SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .ToArray();
                Assert(File.Exists(lockedPath) &&
                       remainingWithLockedCandidate.Length <= 8 &&
                       remainingWithLockedCandidate.Sum(file => file.Length) <= 8L * 1024 * 1024,
                    "单个被占用旧日志不得阻塞其余候选清理，目录仍应尽量收敛到8个文件/8MiB");
            }

            File.WriteAllBytes(activePath, new byte[2 * 1024 * 1024]);
            var preparedPath = (string)(InvokeStatic(
                loggerType,
                "PrepareLogPathForAppend",
                maintenanceDirectory,
                now,
                128) ?? string.Empty);
            var archivePath = Path.Combine(
                maintenanceDirectory,
                $"xlb-pet-{now:yyyy-MM-dd}.001.log");
            Assert(string.Equals(preparedPath, activePath, StringComparison.OrdinalIgnoreCase) &&
                   !File.Exists(activePath) &&
                   File.Exists(archivePath),
                "2MiB日志必须原子轮转为三位编号文件，并继续写入当天主文件");

            var truncated = (string)(InvokeStatic(
                loggerType,
                "TruncateMessageToUtf8Limit",
                new string('界', 40_000)) ?? string.Empty);
            Assert(System.Text.Encoding.UTF8.GetByteCount(truncated) <= 32 * 1024 &&
                   truncated.Contains("truncated", StringComparison.Ordinal),
                "超长异常日志必须在UTF-8字符边界内截断到32KiB");
        }
        finally
        {
            if (Directory.Exists(maintenanceDirectory))
            {
                Directory.Delete(maintenanceDirectory, recursive: true);
            }
        }
    }

    private static void AssertRuntimeJankSourceContract()
    {
        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var atlasBuilderSource = File.ReadAllText(
            FindWorkspaceFile("tools", "build_sprite_atlas.py"));
        var rendering = ExtractPrivateMethodSource(mainSource, "VisualClock_Rendering");
        var showStableFrame = ExtractPrivateMethodSource(mainSource, "ShowStableFrame");
        var discardSupersededPending = ExtractPrivateMethodSource(
            mainSource,
            "DiscardSupersededPendingSpriteFrame");
        var hasDeferredSpritePageDispatchWork = ExtractPrivateMethodSource(
            mainSource,
            "HasDeferredSpritePageDispatchWork");
        var requestPagePrefetch = ExtractPrivateMethodSource(
            mainSource,
            "RequestSpritePagePrefetch");
        var requestPageCancellation = ExtractPrivateMethodSource(
            mainSource,
            "RequestSpritePagePrefetchCancellation");
        var prefetchDispatchTick = ExtractPrivateMethodSource(
            mainSource,
            "SpritePagePrefetchDispatchTimer_Tick");
        var updateVisualClockSubscription = ExtractPrivateMethodSource(
            mainSource,
            "UpdateVisualClockSubscription");
        var beginPetSizeGesture = ExtractPrivateMethodSource(
            mainSource,
            "TodoWindow_PetSizeAdjustmentStarted");
        var queuePetSizeTarget = ExtractPrivateMethodSource(
            mainSource,
            "QueuePetSizeScaleTargetAt");
        var advancePetSizeComposition = ExtractPrivateMethodSource(
            mainSource,
            "AdvancePetSizeCompositionFrame");
        var advancePetSizeTransition = ExtractPrivateMethodSource(
            mainSource,
            "AdvancePetSizeTransition");
        var resumePageWarmup = ExtractPrivateMethodSource(
            mainSource,
            "ResumeSpritePageWarmup");
        var completePagePrefetch = ExtractPrivateMethodSource(
            mainSource,
            "CompleteSpritePagePrefetch");
        var decodeSpritePage = ExtractPrivateMethodSource(
            mainSource,
            "DecodeSpritePage");
        var validateSpriteAtlasPageContentHash = ExtractPrivateMethodSource(
            mainSource,
            "ValidateSpriteAtlasPageContentHash");
        var decodeSpritePagePayload = ExtractPrivateMethodSource(
            mainSource,
            "DecodeSpritePagePayload");
        var decodeSpritePageStream = ExtractPrivateMethodSource(
            mainSource,
            "DecodeSpritePageStream");
        var reconstructDeltaSubPage = ExtractPrivateMethodSource(
            mainSource,
            "ReconstructDeltaSubSpritePage");
        var buildSpritePageWarmupOrder = ExtractPrivateMethodSource(
            mainSource,
            "BuildSpritePageWarmupOrder");
        var loadNumberedFrameSequence = ExtractPrivateMethodSource(
            mainSource,
            "LoadNumberedFrameSequence");
        var loadEdgeFrameSequence = ExtractPrivateMethodSource(
            mainSource,
            "LoadEdgeFrameSequence");
        var enterEdgePeek = ExtractPrivateMethodSource(
            mainSource,
            "EnterEdgePeek");
        var advanceEdgePeek = ExtractPrivateMethodSource(
            mainSource,
            "AdvanceEdgePeek");
        var deferredEdgeClock = ExtractPrivateMethodSource(
            mainSource,
            "TryStartDeferredEdgePeekClockAt");
        var windowLoaded = ExtractPrivateMethodSource(
            mainSource,
            "Window_Loaded");
        var requestIdleSpritePageTrim = ExtractPrivateMethodSource(
            mainSource,
            "RequestIdleSpritePageTrim");
        var trimResidentPagesToIdleTarget = ExtractPrivateMethodSource(
            mainSource,
            "TrimResidentSpritePagesToIdleTarget");
        var scheduleSpritePageCollection = ExtractPrivateMethodSource(
            mainSource,
            "ScheduleSpritePageCollectionIfNeeded");
        var canRunIdleSpritePageCollection = ExtractPrivateMethodSource(
            mainSource,
            "CanRunIdleSpritePageCollection");
        var isSpritePageProtected = ExtractPrivateMethodSource(
            mainSource,
            "IsSpritePageProtected");
        var preloadUpcomingReminder = ExtractPrivateMethodSource(
            mainSource,
            "PreloadUpcomingReminderAt");
        var calculateReminderWakeDelay = ExtractPrivateMethodSource(
            mainSource,
            "CalculateReminderWakeDelay");
        var spritePageCollectionTimerTick = ExtractPrivateMethodSource(
            mainSource,
            "SpritePageCollectionTimer_Tick");
        var removeResidentSpritePage = ExtractPrivateMethodSource(
            mainSource,
            "RemoveResidentSpritePage");
        var recordDiscardedSpritePageBytes = ExtractPrivateMethodSource(
            mainSource,
            "RecordDiscardedSpritePageBytes");
        var observeNaturalSpritePageCollection = ExtractPrivateMethodSource(
            mainSource,
            "ObserveNaturalSpritePageCollection");
        var clearResidentSpritePages = ExtractPrivateMethodSource(
            mainSource,
            "ClearResidentSpritePages");
        var windowClosing = ExtractPrivateMethodSource(
            mainSource,
            "Window_Closing");
        var petHostMouseLeftButtonDown = ExtractPrivateMethodSource(
            mainSource,
            "PetHost_MouseLeftButtonDown");
        var stopPillowBreathing = ExtractPrivateMethodSource(
            mainSource,
            "StopPillowBreathing");
        var renderingTerminalMethods = new[]
        {
            ExtractPrivateMethodSource(mainSource, "CompleteActiveClip"),
            ExtractPrivateMethodSource(mainSource, "ExitEdgePeek"),
            ExtractPrivateMethodSource(mainSource, "SetBubbleMode"),
            ExtractPrivateMethodSource(mainSource, "LogInfo")
        };
        var hotPathMethods = new[]
        {
            rendering,
            showStableFrame,
            advancePetSizeComposition,
            advancePetSizeTransition,
            ExtractPrivateMethodSource(mainSource, "ApplyPetSizePreviewScale"),
            ExtractPrivateMethodSource(mainSource, "CopyFramePixels"),
            ExtractPrivateMethodSource(mainSource, "WriteDisplayFrame")
        };

        Assert(mainSource.Contains("_residentSpritePages", StringComparison.Ordinal) &&
               mainSource.Contains("Task.Run(", StringComparison.Ordinal) &&
               mainSource.Contains("_spritePagePrefetchGeneration", StringComparison.Ordinal) &&
               mainSource.Contains("CancellationTokenSource", StringComparison.Ordinal) &&
               mainSource.Contains("TryPromotePrefetchedSpritePage", StringComparison.Ordinal) &&
               mainSource.Contains("_spritePageWarmupOrder", StringComparison.Ordinal),
            "运行时分页必须使用解码页常驻缓存、按需后台预取、代际取消和UI线程引用切换");
        Assert(!rendering.Contains("LoadSpritePage", StringComparison.Ordinal) &&
               !rendering.Contains("DecodeBrotli", StringComparison.Ordinal) &&
               !rendering.Contains("GetResourceStream", StringComparison.Ordinal) &&
               !rendering.Contains("AppLogger", StringComparison.Ordinal) &&
               !showStableFrame.Contains("LoadSpritePageIntoBuffer", StringComparison.Ordinal) &&
               showStableFrame.Contains("_pendingSpriteFrame", StringComparison.Ordinal) &&
               !showStableFrame.Contains("new byte[", StringComparison.Ordinal),
            "Rendering/ShowStableFrame不得同步读取或解压；未就绪必须保持旧帧并记录最新目标帧");
        var contentHashValidation = decodeSpritePage.IndexOf(
            "ValidateSpriteAtlasPageContentHash(",
            StringComparison.Ordinal);
        var brotliDecode = decodeSpritePage.IndexOf(
            "new BrotliStream(",
            StringComparison.Ordinal);
        var streamDecode = decodeSpritePage.IndexOf(
            "DecodeSpritePageStream(",
            StringComparison.Ordinal);
        Assert(contentHashValidation >= 0 &&
               brotliDecode > contentHashValidation &&
               streamDecode > brotliDecode &&
               decodeSpritePage.Split(
                   "Application.GetResourceStream(",
                   StringSplitOptions.None).Length == 3 &&
               validateSpriteAtlasPageContentHash.Contains(
                   "IncrementalHash.CreateHash",
                   StringComparison.Ordinal) &&
               validateSpriteAtlasPageContentHash.Contains(
                   "ArrayPool<byte>.Shared.Rent",
                   StringComparison.Ordinal) &&
               validateSpriteAtlasPageContentHash.Contains(
                   "var remaining = compressedByteCount",
                   StringComparison.Ordinal) &&
               validateSpriteAtlasPageContentHash.Contains(
                   "compressedStream.ReadByte() != -1",
                   StringComparison.Ordinal) &&
               decodeSpritePageStream.Contains(
                   "ValidateSpriteAtlasDecodedHash(",
                   StringComparison.Ordinal) &&
               decodeSpritePageStream.Contains(
                   "payloadStream.ReadByte() != -1",
                   StringComparison.Ordinal),
            "后台分页加载必须先用定长流式SHA严格核对压缩资源，再打开第二条资源流" +
            "直接Brotli解码；压缩流与解码payload都必须拒绝截断或尾随字节");
        Assert(mainSource.Contains("pbgra32-delta-sub-v1", StringComparison.Ordinal) &&
               decodeSpritePagePayload.Contains(
                   "new MemoryStream(",
                   StringComparison.Ordinal) &&
               decodeSpritePagePayload.Contains(
                   "expectedPayloadByteCount",
                   StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "_spritePageCompressedBytes",
                   StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "_spritePagePayloadBytes",
                   StringComparison.Ordinal) &&
               decodeSpritePage.Contains(
                   "new byte[page.UncompressedByteCount]",
                   StringComparison.Ordinal) &&
               !decodeSpritePage.Contains(
                   "new byte[page.PayloadByteCount]",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "Stream payloadStream",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "ReadPayloadExactly(",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "BinaryPrimitives.ReadUInt16LittleEndian",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "previousDisplayFrame",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "Span<byte> header = stackalloc byte[DeltaSubFrameHeaderByteCount]",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "Span<byte> deltaRow = stackalloc byte[DisplayPixelWidth * 4]",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "private static readonly ArrayPool<byte> SpriteDecodeScratchPool",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "maxArraysPerBucket: 1",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "SpriteDecodeScratchPool.Rent(",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "SpriteDecodeScratchPool.Return(",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "payloadOffset != payloadByteCount",
                   StringComparison.Ordinal) &&
               reconstructDeltaSubPage.Contains(
                   "Repeated delta-sub sprite differs",
                   StringComparison.Ordinal) &&
               !rendering.Contains("DecodeSpritePagePayload", StringComparison.Ordinal) &&
               !rendering.Contains("ReconstructDeltaSub", StringComparison.Ordinal),
            "delta-sub必须直接消费Brotli流，前帧暂存只使用容量1的私有池且Rent/Return成对，" +
            "按expected长度严格重建并拒绝不一致的重复sprite；不得保留整页压缩或payload字段");
        Assert(mainSource.Contains(
                   "SpritePageResidentBudgetBytes = 128L * 1024 * 1024",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "SpritePageIdleResidentTargetBytes = 64L * 1024 * 1024",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "SpritePageCollectionThresholdBytes = 48L * 1024 * 1024",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "MinimumSpritePageCollectionInterval",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "TimeSpan.FromSeconds(30)",
                   StringComparison.Ordinal) &&
               trimResidentPagesToIdleTarget.Contains(
                   "SpritePageIdleResidentTargetBytes",
                   StringComparison.Ordinal) &&
               requestIdleSpritePageTrim.Contains(
                   "_residentSpritePageTrimPending = true",
                   StringComparison.Ordinal) &&
               requestIdleSpritePageTrim.Contains(
                   "_residentSpritePageIdleTrimPending = true",
                   StringComparison.Ordinal) &&
               prefetchDispatchTick.Contains(
                   "TrimResidentSpritePagesToIdleTarget()",
                   StringComparison.Ordinal),
            "活动resident软预算必须是128MiB、动作终态idle回收目标必须是64MiB；" +
            "Rendering内只发布idle trim标志并在dispatcher tick执行");
        Assert(removeResidentSpritePage.Contains(
                   "RecordDiscardedSpritePageBytes(residentPage.ByteCount)",
                   StringComparison.Ordinal) &&
               recordDiscardedSpritePageBytes.Contains(
                   "ObserveNaturalSpritePageCollection()",
                   StringComparison.Ordinal) &&
               recordDiscardedSpritePageBytes.Contains(
                   "_spritePageEvictedBytesSinceCollection = checked(",
                   StringComparison.Ordinal) &&
               recordDiscardedSpritePageBytes.Contains(
                   "ScheduleSpritePageCollectionIfNeeded()",
                   StringComparison.Ordinal) &&
               scheduleSpritePageCollection.Contains(
                   "SpritePageCollectionThresholdBytes",
                   StringComparison.Ordinal) &&
               scheduleSpritePageCollection.Contains(
                   "MinimumSpritePageCollectionInterval - elapsed",
                   StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_activeClip is null", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_isInsideVisualRenderingCallback", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_isReminderActive", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_dragInteractionActive", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_pointerDown", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_isPetSizeTransitioning", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_isPetSizePreviewSessionActive", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_isPetSizeAdjustmentActive", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_petSizeTargetUpdatePending", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_petSizeCommitPending", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_isFrameBlending", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_isPillowBreathing", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_bubbleMode == BubbleMode.None", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_todoWindow.IsVisible", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!BubblePopup.IsOpen", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_edgeDock == EdgeDock.None", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_pendingSpriteFrame is null", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_spritePagePrefetchTask is null", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_desiredSpritePageName is null", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_renderDeferredSpritePageName is null", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_renderDeferredSpritePageFailureName is null", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_renderDeferredSpritePageCancellation", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("!_residentSpritePageTrimPending", StringComparison.Ordinal) &&
               canRunIdleSpritePageCollection.Contains("_upcomingReminderPreloadPageName is null", StringComparison.Ordinal),
            "Gen2回收只能在动作、提醒、提醒预热、拖动、缩放、预取、Todo、edge、pillow与帧过渡全部空闲时运行");
        Assert(mainSource.Contains(
                   "ReminderSpritePreloadLeadTime =",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "TimeSpan.FromSeconds(2)",
                   StringComparison.Ordinal) &&
               preloadUpcomingReminder.Contains(
                   "remaining > ReminderSpritePreloadLeadTime",
                   StringComparison.Ordinal) &&
               preloadUpcomingReminder.Contains(
                   "_upcomingReminderPreloadPageName = pageName",
                   StringComparison.Ordinal) &&
               preloadUpcomingReminder.Contains(
                   "RequestSpritePagePrefetch(pageName, urgent: true)",
                   StringComparison.Ordinal) &&
               calculateReminderWakeDelay.Contains(
                   "remaining - ReminderSpritePreloadLeadTime",
                   StringComparison.Ordinal) &&
               calculateReminderWakeDelay.Contains(
                   "MaximumReminderWakeInterval",
                   StringComparison.Ordinal) &&
               isSpritePageProtected.Contains(
                   "_upcomingReminderPreloadPageName",
                   StringComparison.Ordinal) &&
               isSpritePageProtected.Contains(
                   "if (_isReminderActive",
                   StringComparison.Ordinal) &&
               isSpritePageProtected.Contains(
                   "FrameSequenceUsesSpritePage(_reminderEnterFrames, pageName)",
                   StringComparison.Ordinal) &&
               isSpritePageProtected.Contains(
                   "FrameSequenceUsesSpritePage(_reminderHoldFrames, pageName)",
                   StringComparison.Ordinal),
            "定时提醒必须在到期前2秒JIT预取首屏页、在窗口内动态保护，" +
            "活动期间保护完整入场/保持序列，并按12小时上限分段唤醒");
        var stopPillowBeforeDrag = petHostMouseLeftButtonDown.IndexOf(
            "StopPillowBreathing()",
            StringComparison.Ordinal);
        var markDragActive = petHostMouseLeftButtonDown.IndexOf(
            "_dragInteractionActive = true",
            StringComparison.Ordinal);
        Assert(stopPillowBeforeDrag >= 0 &&
               markDragActive > stopPillowBeforeDrag &&
               stopPillowBreathing.Contains("_isPillowBreathing = false", StringComparison.Ordinal) &&
               stopPillowBreathing.Contains("_automaticTimer.Stop()", StringComparison.Ordinal) &&
               stopPillowBreathing.Contains(
                   "RefreshSnoreBubbleAnimationState()",
                   StringComparison.Ordinal) &&
               !stopPillowBreathing.Contains("PetScale.", StringComparison.Ordinal),
            "鼠标按下必须先停止枕头占位并清理timer，再进入拖动状态；鼻泡独立层不得改动人物缩放");
        Assert(observeNaturalSpritePageCollection.Contains(
                   "generation <= _lastObservedSpritePageCollectionGeneration",
                   StringComparison.Ordinal) &&
               observeNaturalSpritePageCollection.Contains(
                   "_spritePageEvictedBytesSinceCollection = 0",
                   StringComparison.Ordinal) &&
               observeNaturalSpritePageCollection.Contains(
                   "_lastSpritePageCollectionTimestamp = Stopwatch.GetTimestamp()",
                   StringComparison.Ordinal),
            "自然Gen2只有在collection count实际增长时才能清零淘汰债务，并刷新30秒节流时间戳");
        var observedGen2Collection = spritePageCollectionTimerTick.IndexOf(
            "if (collectionGeneration >",
            StringComparison.Ordinal);
        var collectionDebtReduction = spritePageCollectionTimerTick.IndexOf(
            "_spritePageEvictedBytesSinceCollection = Math.Max(",
            StringComparison.Ordinal);
        Assert(observedGen2Collection >= 0 &&
               collectionDebtReduction > observedGen2Collection &&
               spritePageCollectionTimerTick.Contains(
                   "var collectionGeneration = GC.CollectionCount(GC.MaxGeneration)",
                   StringComparison.Ordinal) &&
               spritePageCollectionTimerTick.Contains(
                   "Task.Run(static () =>",
                   StringComparison.Ordinal) &&
               spritePageCollectionTimerTick.Contains(
                   "GCCollectionMode.Forced",
                   StringComparison.Ordinal) &&
               spritePageCollectionTimerTick.Contains(
                   "blocking: false",
                   StringComparison.Ordinal) &&
               spritePageCollectionTimerTick.Contains(
                   "compacting: false",
                   StringComparison.Ordinal) &&
               !mainSource.Contains("blocking: true", StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "LargeObjectHeapCompactionMode",
                   StringComparison.Ordinal) &&
               !mainSource.Contains(
                   "WaitForPendingFinalizers",
                   StringComparison.Ordinal),
            "只有观测到Gen2计数增长才能扣除债务；请求必须在Task.Run中nonblocking/noncompacting Forced执行，禁止LOH压缩与等待终结器");
        Assert(windowClosing.Contains("_spritePageCollectionTimer.Stop()", StringComparison.Ordinal) &&
               windowClosing.Contains(
                   "_spritePageCollectionTimer.Tick -= SpritePageCollectionTimer_Tick",
                   StringComparison.Ordinal) &&
               clearResidentSpritePages.Contains(
                   "_spritePageCollectionTimer.Stop()",
                   StringComparison.Ordinal) &&
               clearResidentSpritePages.Contains(
                   "_spritePageEvictedBytesSinceCollection = 0",
                   StringComparison.Ordinal) &&
               clearResidentSpritePages.Contains(
                   "_spritePageCollectionInProgress = false",
                   StringComparison.Ordinal),
            "窗口关闭必须停止并解绑Gen2 timer，清空缓存时必须同步清零债务与在途状态");
        var oversizedBrotliGuard = atlasBuilderSource.IndexOf(
            "if len(runtime_bytes) > len(runtime_payload):",
            StringComparison.Ordinal);
        var runtimePayloadWrite = atlasBuilderSource.IndexOf(
            "write_bytes_atomically(runtime_path, runtime_bytes)",
            StringComparison.Ordinal);
        Assert(oversizedBrotliGuard >= 0 &&
               runtimePayloadWrite > oversizedBrotliGuard &&
               atlasBuilderSource[oversizedBrotliGuard..runtimePayloadWrite].Contains(
                   "raise RuntimeError(",
                   StringComparison.Ordinal),
            "图集构建器必须在写出运行时资源前拒绝大于payload的Brotli结果，保持与运行时长度门禁一致");
        var pendingFramePublication = showStableFrame.IndexOf(
            "_pendingSpriteFrame = frame",
            StringComparison.Ordinal);
        var pillowStateCommit = showStableFrame.IndexOf(
            "var pillowOpacity = IsEdgeSpriteFrame(frame)",
            StringComparison.Ordinal);
        var currentFrameCommit = showStableFrame.LastIndexOf(
            "_currentSpriteFrame = frame",
            StringComparison.Ordinal);
        Assert(pendingFramePublication >= 0 &&
               pillowStateCommit > pendingFramePublication &&
               currentFrameCommit > pillowStateCommit &&
               !showStableFrame.Contains("PillowImage.Visibility", StringComparison.Ordinal),
            "冷页pending不得提前切换枕头；枕头透明度必须只在目标像素成功提交后与current frame一并发布，且不得触发布局");
        Assert(buildSpritePageWarmupOrder.Contains("return [];", StringComparison.Ordinal) &&
               resumePageWarmup.Contains("_spritePageWarmupIndex", StringComparison.Ordinal) &&
               resumePageWarmup.Contains("_residentSpritePages.ContainsKey", StringComparison.Ordinal) &&
               resumePageWarmup.Contains("urgent: false", StringComparison.Ordinal) &&
               completePagePrefetch.Contains("AddResidentSpritePage", StringComparison.Ordinal) &&
               completePagePrefetch.Contains("ResumeSpritePageWarmup", StringComparison.Ordinal) &&
               decodeSpritePage.Contains("new byte[page.UncompressedByteCount]", StringComparison.Ordinal),
            "启动warmup顺序必须为空；按需预取完成后只把精确尺寸Pbgra32页加入常驻缓存，" +
            "不得恢复全图集后台预热");
        Assert(mainSource.Split(
                   "LoadSpritePageIntoBuffer(",
                   StringSplitOptions.None).Length == 3 &&
               windowLoaded.Contains("_spritePageWarmupEnabled = true", StringComparison.Ordinal) &&
               windowLoaded.Contains("ResumeSpritePageWarmup()", StringComparison.Ordinal),
            "构造期间只能同步解码一次idle页；Loaded后的空warmup调用不得启动后台解码");
        Assert(!mainSource.Contains("WakeFrameCount", StringComparison.Ordinal) &&
               loadNumberedFrameSequence.Contains(
                   "TryGetNumberedSequencePagePart(",
                   StringComparison.Ordinal) &&
               loadNumberedFrameSequence.Contains(
                   "matchedPageParts.Contains(expectedPagePart)",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "\"Assets/luban-wake-smooth-\");",
                   StringComparison.Ordinal),
            "wake帧数必须由manifest连续分页动态加载，不能再硬编码总数或假定只位于idle单页");
        Assert(!mainSource.Contains("EdgePeekFrameCount", StringComparison.Ordinal) &&
               !mainSource.Contains("EdgePeekFrameInterval", StringComparison.Ordinal) &&
               !mainSource.Contains("_edgePeekFrameDirection", StringComparison.Ordinal) &&
               mainSource.Contains("\"edge-left\"", StringComparison.Ordinal) &&
               mainSource.Contains(
                   "\"Assets/luban-edge-left-smooth-\"",
                   StringComparison.Ordinal) &&
               !mainSource.Contains("_edgeTopFrames", StringComparison.Ordinal) &&
               !mainSource.Contains("\"edge-top\"", StringComparison.Ordinal) &&
               !mainSource.Contains("EdgeDock.Top", StringComparison.Ordinal) &&
               mainSource.Contains("\"edge-bottom\"", StringComparison.Ordinal) &&
               mainSource.Contains(
                   "\"Assets/luban-edge-bottom-smooth-\"",
                   StringComparison.Ordinal) &&
               loadEdgeFrameSequence.Contains(
                   "LoadNumberedFrameSequence(pageNamePrefix, resourcePrefix)",
                   StringComparison.Ordinal) &&
               loadEdgeFrameSequence.Contains("frames.Length < 8", StringComparison.Ordinal) &&
               loadEdgeFrameSequence.Contains("frames.Length % 4 != 0", StringComparison.Ordinal),
            "边缘序列必须只从左/下独立smooth分页动态加载，右侧镜像复用左侧；" +
            "顶部状态、分页与枚举分支必须彻底移除，同时允许16/24/48等四阶段长度");
        Assert(enterEdgePeek.Contains("frames.Length - 1", StringComparison.Ordinal) &&
               enterEdgePeek.Contains(
                   "_edgePeekFrameDeadlineTimestamp = long.MaxValue",
                   StringComparison.Ordinal) &&
               deferredEdgeClock.Contains(
                   "_edgePeekFrameDeadlineTimestamp != long.MaxValue",
                   StringComparison.Ordinal) &&
               advanceEdgePeek.Contains(
                   "(_edgePeekFrameIndex + 1) % frames.Length",
                   StringComparison.Ordinal) &&
               advanceEdgePeek.Contains(
                   "GetEdgePeekCycleDurationTicks(frames.Length)",
                   StringComparison.Ordinal) &&
               advanceEdgePeek.Contains(
                   "HandleSpritePagePrefetchFailure(",
                   StringComparison.Ordinal) &&
               advanceEdgePeek.IndexOf(
                   "while (timestamp >= _edgePeekFrameDeadlineTimestamp",
                   StringComparison.Ordinal) <
               advanceEdgePeek.IndexOf("ShowStableFrame(targetFrame)", StringComparison.Ordinal),
            "边缘探头必须从末帧休息姿势入场，冷页显示前冻结，随后按绝对时间单向闭环且每次回调只提交最终姿势；" +
            "已失败分页必须安全终止而不能long.MaxValue永久空转");
        Assert(mainSource.Contains(
                   "manifest.PageFrameCount != resourcePaths.Count",
                   StringComparison.Ordinal) &&
               mainSource.Contains(
                   "!foundResources.Add(resourcePath)",
                   StringComparison.Ordinal),
            "运行时必须拒绝跨分页重复资源和不精确的pageFrameCount，不能依赖构建器单边保证");
        Assert(hotPathMethods.All(method =>
                !method.Contains("Dispatcher.", StringComparison.Ordinal) &&
                !method.Contains("AppLogger", StringComparison.Ordinal) &&
                !method.Contains("File.", StringComparison.Ordinal) &&
                !method.Contains("GetResourceStream", StringComparison.Ordinal) &&
                !method.Contains("Task.Run", StringComparison.Ordinal) &&
                !method.Contains(".Select(", StringComparison.Ordinal) &&
                !method.Contains(".Where(", StringComparison.Ordinal) &&
                !method.Contains(".ToArray(", StringComparison.Ordinal)),
            "每帧视觉热路径不得排队Dispatcher、写日志、同步I/O、启动Task或执行LINQ分配");
        Assert(renderingTerminalMethods.All(method =>
                   !method.Contains("=>", StringComparison.Ordinal) &&
                   !method.Contains("new Action", StringComparison.Ordinal) &&
                   !method.Contains("Func<", StringComparison.Ordinal) &&
                   !method.Contains("GC.Collect", StringComparison.Ordinal) &&
                   !method.Contains("Task.Run", StringComparison.Ordinal)) &&
               !mainSource.Contains(
                   "ScheduleUnusedSpritePageCollection",
                   StringComparison.Ordinal),
            "Rendering 的动作/边缘/Todo结束调用链不得创建捕获委托或直接发起GC；" +
            "回收只能经过独立idle timer门禁");
        Assert(showStableFrame.Contains(
                   "DiscardSupersededPendingSpriteFrame(frame)",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "_pendingSpriteFrame = null",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "_desiredSpritePageName = null",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "_spritePagePrefetchGeneration++",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "RequestSpritePagePrefetchCancellation()",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "_renderDeferredSpritePageName = null",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "_renderDeferredSpritePageUrgent = false",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "if (!HasDeferredSpritePageDispatchWork())",
                   StringComparison.Ordinal) &&
               discardSupersededPending.Contains(
                   "_spritePagePrefetchDispatchTimer.Stop()",
                   StringComparison.Ordinal) &&
               hasDeferredSpritePageDispatchWork.Contains(
                   "_renderDeferredSpritePageCancellation",
                   StringComparison.Ordinal) &&
               hasDeferredSpritePageDispatchWork.Contains(
                   "_renderDeferredSpritePageFailureName is not null",
                   StringComparison.Ordinal) &&
               hasDeferredSpritePageDispatchWork.Contains(
                   "_residentSpritePageTrimPending",
                   StringComparison.Ordinal),
            "较新的热页必须淘汰旧pending并取消旧冷页请求；只有没有取消/失败/trim等共享信号时才能停止dispatcher timer");
        var renderDeferralGuard = requestPagePrefetch.IndexOf(
            "if (_isInsideVisualRenderingCallback)",
            StringComparison.Ordinal);
        var startBackgroundWork = requestPagePrefetch.IndexOf(
            "StartSpritePagePrefetch()",
            StringComparison.Ordinal);
        Assert(renderDeferralGuard >= 0 &&
               startBackgroundWork > renderDeferralGuard &&
               requestPagePrefetch[renderDeferralGuard..startBackgroundWork].Contains(
                   "_renderDeferredSpritePageName = pageName",
                   StringComparison.Ordinal) &&
               requestPagePrefetch[renderDeferralGuard..startBackgroundWork].Contains(
                   "return;",
                   StringComparison.Ordinal) &&
               requestPageCancellation.Contains(
                   "_renderDeferredSpritePageCancellation = true",
                   StringComparison.Ordinal) &&
               requestPageCancellation.IndexOf(
                   "_spritePagePrefetchCancellation?.Cancel()",
                   StringComparison.Ordinal) >
               requestPageCancellation.IndexOf(
                   "return;",
                StringComparison.Ordinal),
            "合成回调内的分页请求和取消只能写入复用信号，Task与CTS操作必须延后到回调外");
        Assert(requestPagePrefetch.Contains(
                   "_desiredSpritePageUrgent = urgent",
                   StringComparison.Ordinal) &&
               requestPagePrefetch.Contains(
                   "if (_spritePagePrefetchTask is not null)",
                   StringComparison.Ordinal) &&
               requestPagePrefetch.Contains(
                   "RequestSpritePagePrefetchCancellation()",
                   StringComparison.Ordinal) &&
               completePagePrefetch.Contains(
                   "StartSpritePagePrefetch()",
                   StringComparison.Ordinal),
            "紧急动作页必须抢占在途顺序预热，完成或取消后再继续当前需求与后台预热");
        Assert(requestPagePrefetch[renderDeferralGuard..startBackgroundWork].Contains(
                   "_spritePagePrefetchDispatchTimer.Start()",
                   StringComparison.Ordinal) &&
               requestPageCancellation.Contains(
                   "_spritePagePrefetchDispatchTimer.Start()",
                   StringComparison.Ordinal) &&
               !updateVisualClockSubscription.Contains(
                   "_spritePagePrefetchDispatchTimer.Start()",
                   StringComparison.Ordinal) &&
               prefetchDispatchTick.IndexOf(
                   "_spritePagePrefetchDispatchTimer.Stop()",
                   StringComparison.Ordinal) <
               prefetchDispatchTick.IndexOf(
                   "if (_isClosing)",
                   StringComparison.Ordinal) &&
               !prefetchDispatchTick.Contains(
                   "_spritePagePrefetchDispatchTimer.Start()",
                   StringComparison.Ordinal),
            "分页调度定时器只能由合成回调中的延后请求按需启动，Tick 必须先停止并在无新需求时保持休眠");

        var todoSource = File.ReadAllText(FindWorkspaceFile("TodoWindow.xaml.cs"));
        Assert(!todoSource.Contains("_petSizeRenderingHandler", StringComparison.Ordinal) &&
               !todoSource.Contains("CompositionTarget.Rendering", StringComparison.Ordinal) &&
               !todoSource.Contains("PetSizeScale_Rendering", StringComparison.Ordinal) &&
               todoSource.Contains("_resetImeCompositionAfterFocusLossAction", StringComparison.Ordinal) &&
               todoSource.Contains("_focusInputAction", StringComparison.Ordinal) &&
               todoSource.Contains("_retryClipboardCopyAction", StringComparison.Ordinal) &&
               todoSource.Contains("Clipboard.SetDataObject(text, true)", StringComparison.Ordinal) &&
               todoSource.Contains("catch (ExternalException)", StringComparison.Ordinal) &&
               todoSource.Contains("if (IsKeyboardFocusWithin)", StringComparison.Ordinal) &&
               todoSource.Contains("if (!IsVisible || _hasClosed)", StringComparison.Ordinal),
            "TodoWindow不得拥有尺寸Rendering订阅；剪贴板重试仍须复用委托，延迟聚焦不得在收起后复活或抢走选区");

        var mainXaml = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml"));
        var petSizeViewboxStart = mainXaml.IndexOf(
            "<Viewbox x:Name=\"PetSizeViewbox\"",
            StringComparison.Ordinal);
        var petSizeViewboxEnd = mainXaml.IndexOf(
            "Stretch=\"Fill\">",
            petSizeViewboxStart,
            StringComparison.Ordinal);
        Assert(petSizeViewboxStart >= 0 && petSizeViewboxEnd > petSizeViewboxStart &&
               mainXaml[petSizeViewboxStart..petSizeViewboxEnd].Contains(
                   "UseLayoutRounding=\"False\"",
                   StringComparison.Ordinal),
            "尺寸预览 Viewbox 必须关闭布局取整，避免理论缩放基准被单轴取整后出现 1px 裁切和回缩");

        var outsideCloseSource = ExtractPrivateMethodSource(
            mainSource,
            "ScheduleOutsideTodoClose");
        Assert(mainSource.Contains(
                   "_processOutsideTodoCloseAction",
                   StringComparison.Ordinal) &&
               outsideCloseSource.Contains(
                   "if (_outsideTodoCloseQueued)",
                   StringComparison.Ordinal) &&
               !outsideCloseSource.Contains(
                   "new Action",
                   StringComparison.Ordinal),
            "主窗/待办/IME同时失焦时只能合并为一个可失效的外部点击收起回调");

        var positionerSource = File.ReadAllText(
            FindWorkspaceFile("OwnedWindowPositioner.cs"));
        var unchangedGuard = positionerSource.IndexOf(
            "childRect.Left == desiredPosition.X",
            StringComparison.Ordinal);
        var setWindowPos = positionerSource.IndexOf(
            "if (!SetWindowPos(",
            StringComparison.Ordinal);
        var tryPositionStart = positionerSource.IndexOf(
            "internal static bool TryPosition(",
            StringComparison.Ordinal);
        Assert(positionerSource.Contains("internal sealed class PositionCache", StringComparison.Ordinal) &&
               tryPositionStart >= 0 && unchangedGuard > tryPositionStart &&
               setWindowPos > unchangedGuard &&
               !positionerSource[tryPositionStart..].Contains(
                   "new WindowInteropHelper(child)",
                   StringComparison.Ordinal) &&
               positionerSource.Contains(
                   "var monitor = MonitorFromPoint(anchorCenter, MonitorDefaultToNearest)",
                   StringComparison.Ordinal) &&
               positionerSource.Contains(
                   "GetWindowRect(cache._childHandle, out var childRect)",
                   StringComparison.Ordinal) &&
               positionerSource.Contains(
                   "GetWindowRect(cache._childHandle, out var movedChildRect)",
                   StringComparison.Ordinal) &&
               positionerSource.Contains(
                   "cache._childWidth = movedChildRect.Right - movedChildRect.Left",
                   StringComparison.Ordinal),
            "待办窗口目标物理像素未变时必须跳过SetWindowPos");
        var prepareEnvelope = ExtractPrivateMethodSource(
            mainSource,
            "PreparePetSizePreviewEnvelope");
        Assert(beginPetSizeGesture.Contains(
                   "PreparePetSizePreviewEnvelope()",
                   StringComparison.Ordinal) &&
               queuePetSizeTarget.Contains(
                   "PreparePetSizePreviewEnvelope()",
                   StringComparison.Ordinal) &&
               updateVisualClockSubscription.Contains(
                   "_isPetSizeAdjustmentActive",
                   StringComparison.Ordinal) &&
               advancePetSizeComposition.Contains(
                   "_todoWindow.FlushPendingPetSizeScaleChanged()",
                   StringComparison.Ordinal),
            "最大预览包络必须在手势开始或非手势目标排队阶段准备，MainWindow须在整个手势期间保持唯一合成合帧器");
        var petSizeRenderLayoutWrites = new[]
        {
            "PreparePetSizePreviewEnvelope",
            "ApplyPetSizeWindowBounds",
            "PetSizeViewbox.Width",
            "PetSizeViewbox.Height",
            "PresentationSource.FromVisual",
            "Width =",
            "Height =",
            "Left =",
            "Top ="
        };
        Assert(petSizeRenderLayoutWrites.All(token =>
                   !advancePetSizeComposition.Contains(token, StringComparison.Ordinal) &&
                   !advancePetSizeTransition.Contains(token, StringComparison.Ordinal)) &&
               advancePetSizeComposition.Contains(
                   "!_isPetSizeTransitioning",
                   StringComparison.Ordinal) &&
               !prepareEnvelope.Contains(
                   "QueueTodoWindowPositionUpdate",
                   StringComparison.Ordinal),
            "尺寸Rendering热路径只能消费目标并写Scale/Translate，过渡结束后才能安排一次Todo跟随，不得再改窗口或Viewbox布局");
        var queueTodoPosition = ExtractPrivateMethodSource(
            mainSource,
            "QueueTodoWindowPositionUpdate");
        Assert(!queueTodoPosition.Contains("new Action", StringComparison.Ordinal) &&
               !queueTodoPosition.Contains("=>", StringComparison.Ordinal) &&
               mainSource.Contains(
                   "_processTodoWindowPositionUpdateAction = ProcessTodoWindowPositionUpdate",
                   StringComparison.Ordinal),
            "Todo跟随排队必须复用构造时缓存的委托，不能在Rendering边界帧创建捕获闭包");
    }

    private static string ExtractPrivateMethodSource(string source, string methodName)
    {
        var marker = $"{methodName}(";
        var searchFrom = 0;
        var start = -1;
        while (searchFrom < source.Length)
        {
            var candidate = source.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (candidate < 0)
            {
                break;
            }

            var precedingIndex = candidate - 1;
            if (precedingIndex >= 0 &&
                (char.IsLetterOrDigit(source[precedingIndex]) ||
                 source[precedingIndex] == '_'))
            {
                searchFrom = candidate + marker.Length;
                continue;
            }

            var lineStart = source.LastIndexOf('\n', candidate);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var lineEnd = source.IndexOf('\n', candidate);
            lineEnd = lineEnd < 0 ? source.Length : lineEnd;
            var declarationLine = source[lineStart..lineEnd];
            if (declarationLine.Contains("private ", StringComparison.Ordinal) ||
                declarationLine.Contains("internal ", StringComparison.Ordinal))
            {
                start = lineStart;
                break;
            }

            searchFrom = candidate + marker.Length;
        }

        if (start < 0)
        {
            throw new InvalidOperationException($"找不到源码方法：{methodName}");
        }

        var end = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        // Background priority can be starved by a continuously subscribed
        // CompositionTarget.Rendering callback, turning a 30 ms pump into
        // several seconds and letting the clip finish before an intermediate
        // state assertion. Normal priority keeps the requested wall-clock bound.
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
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

    private static RenderingEventArgs CreateRenderingEventArgs(TimeSpan renderingTime)
    {
        var constructor = typeof(RenderingEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(TimeSpan)],
            modifiers: null)
            ?? throw new InvalidOperationException(
                "找不到 RenderingEventArgs(TimeSpan) 内部构造函数");
        return (RenderingEventArgs)constructor.Invoke([renderingTime]);
    }

    private static object GetNestedEnum(string enumName, string valueName)
    {
        var type = typeof(MainWindow).GetNestedType(enumName, BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"找不到 MainWindow.{enumName}");
        return Enum.Parse(type, valueName);
    }

    private static object FindSpriteFrameBySuffix(
        MainWindow window,
        string frameNameSuffix)
    {
        var pages = GetDictionaryEntries(GetField<IDictionary>(window, "_spritePages"));
        foreach (var page in pages)
        {
            var frames = GetDictionaryEntries(
                GetProperty<IDictionary>(page.Value!, "Frames"));
            foreach (var frame in frames)
            {
                if (GetSpriteFrameInfo(frame.Value!).Name.EndsWith(
                        frameNameSuffix,
                        StringComparison.Ordinal))
                {
                    return frame.Value!;
                }
            }
        }

        throw new InvalidOperationException($"找不到精灵帧后缀：{frameNameSuffix}");
    }

    private static object CloneSpriteFrameWithName(object frame, string name)
    {
        var info = GetSpriteFrameInfo(frame);
        return Activator.CreateInstance(
                   frame.GetType(),
                   InstanceFlags,
                   binder: null,
                   args:
                   [
                       info.X,
                       info.Y,
                       info.Width,
                       info.Height,
                       info.DestinationX,
                       info.DestinationY,
                       info.PageName,
                       name
                   ],
                   culture: null)
               ?? throw new InvalidOperationException("无法克隆测试SpriteFrame");
    }

    private static void PrimeSpritePageForFrame(MainWindow window, object frame)
    {
        WaitForSpritePagePrefetchToSettle(window);
        var pageName = GetSpriteFrameInfo(frame).PageName;
        if (string.Equals(
                GetRawField(window, "_loadedSpritePageName") as string,
                pageName,
                StringComparison.Ordinal))
        {
            return;
        }

        // This synchronous wrapper is test-only. Production page changes must
        // continue to use the background decoder and old-frame retention.
        SetField(window, "_pendingSpriteFrame", null);
        SetField(window, "_pendingSpriteFrameBlendDuration", TimeSpan.Zero);
        SetField(window, "_desiredSpritePageName", null);
        SetField(window, "_desiredSpritePageUrgent", false);
        SetField(window, "_failedSpritePageName", null);

        var pageEntry = GetDictionaryEntries(
                GetField<IDictionary>(window, "_spritePages"))
            .Single(entry => string.Equals(
                entry.Key as string,
                pageName,
                StringComparison.Ordinal));
        Invoke(
            window,
            "LoadSpritePageIntoBuffer",
            pageName,
            pageEntry.Value!);
    }

    private static void WaitForPrefetchedSpritePage(
        MainWindow window,
        object expectedFrame)
    {
        var expectedPageName = GetSpriteFrameInfo(expectedFrame).PageName;
        WaitForSpritePagePrefetchToSettle(window);
        Assert(string.Equals(
                   GetRawField(window, "_loadedSpritePageName") as string,
                   expectedPageName,
                   StringComparison.Ordinal) ||
               GetField<IDictionary>(window, "_residentSpritePages")
                   .Contains(expectedPageName),
            $"预测预取必须准备目标分页：{expectedPageName}");
    }

    private static void EvictResidentSpritePageForTest(
        MainWindow window,
        string pageName)
    {
        _ = Invoke(window, "RemoveResidentSpritePage", pageName);
        AssertResidentSpriteCacheAccounting(window, $"测试驱逐 {pageName}");
    }

    private static void WaitForSpritePagePrefetchToSettle(MainWindow window)
    {
        var stopwatch = Stopwatch.StartNew();
        while (GetRawField(window, "_spritePagePrefetchTask") is Task task)
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(3))
            {
                throw new InvalidOperationException(
                    "精灵分页后台预取必须在3秒内完成并发布到UI线程");
            }

            if (task.IsCompleted)
            {
                PumpDispatcher(TimeSpan.FromMilliseconds(10));
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    private static object? Invoke(object instance, string name, params object?[] arguments)
    {
        var method = instance.GetType().GetMethod(name, InstanceFlags)
            ?? throw new InvalidOperationException($"找不到方法 {instance.GetType().Name}.{name}");
        return method.Invoke(instance, arguments);
    }

    private static object? InvokeOverload(
        object instance,
        string name,
        params object?[] arguments)
    {
        var method = instance.GetType()
            .GetMethods(InstanceFlags)
            .Single(candidate =>
            {
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == arguments.Length &&
                       parameters.Select((parameter, index) => (parameter, index))
                           .All(entry =>
                               arguments[entry.index] is null
                                   ? !entry.parameter.ParameterType.IsValueType ||
                                     Nullable.GetUnderlyingType(
                                         entry.parameter.ParameterType) is not null
                                   : entry.parameter.ParameterType.IsInstanceOfType(
                                       arguments[entry.index]));
            });
        return method.Invoke(instance, arguments);
    }

    private static object? InvokeStatic(Type type, string name, params object?[] arguments)
    {
        var method = type.GetMethod(name, StaticFlags)
            ?? throw new InvalidOperationException($"找不到静态方法 {type.Name}.{name}");
        return method.Invoke(null, arguments);
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, InstanceFlags)
            ?? throw new InvalidOperationException($"找不到字段 {instance.GetType().Name}.{name}");
        var value = field.GetValue(instance);
        return value is T typed
            ? typed
            : throw new InvalidOperationException(
                $"字段 {instance.GetType().Name}.{name} 类型不正确或为空");
    }

    private static object? GetRawField(object instance, string name)
    {
        var field = instance.GetType().GetField(name, InstanceFlags)
            ?? throw new InvalidOperationException($"找不到字段 {instance.GetType().Name}.{name}");
        return field.GetValue(instance);
    }

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, InstanceFlags)
            ?? throw new InvalidOperationException($"找不到字段 {instance.GetType().Name}.{name}");
        field.SetValue(instance, value);
    }

    private static T GetProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, InstanceFlags)
            ?? throw new InvalidOperationException($"找不到属性 {instance.GetType().Name}.{name}");
        var value = property.GetValue(instance);
        return value is T typed
            ? typed
            : throw new InvalidOperationException(
                $"属性 {instance.GetType().Name}.{name} 类型不正确或为空");
    }

    private static void AssertValidRect(Rect rect, string stage)
    {
        Assert(!rect.IsEmpty &&
               double.IsFinite(rect.Left) &&
               double.IsFinite(rect.Top) &&
               double.IsFinite(rect.Width) &&
               double.IsFinite(rect.Height) &&
               rect.Width > 0 && rect.Height > 0,
            $"{stage}必须是有限且宽高为正的矩形，实际 {rect}");
    }

    private static void AssertClose(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) < 0.01,
            $"{message}：期望 {expected}，实际 {actual}");
    }

    private static double NormalizeCanvasCoordinate(double value) =>
        double.IsNaN(value) ? 0 : value;

    private static void AssertRectClose(Rect actual, Rect expected, string message)
    {
        AssertClose(actual.X, expected.X, $"{message} X");
        AssertClose(actual.Y, expected.Y, $"{message} Y");
        AssertClose(actual.Width, expected.Width, $"{message} Width");
        AssertClose(actual.Height, expected.Height, $"{message} Height");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private readonly record struct PixelDifferenceBounds(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int PixelCount);

    private sealed record EdgeCase(string Edge, Rect NearBounds, Rect TouchingBounds);

    private sealed record SpriteFrameInfo(
        int X,
        int Y,
        int Width,
        int Height,
        int DestinationX,
        int DestinationY,
        string PageName,
        string Name);

    private enum HatColor
    {
        Blue,
        Red
    }

    private sealed record HatComponent(
        int Left,
        int Top,
        int Right,
        int Bottom,
        double CenterX,
        double CenterY)
    {
        public int Width => Right - Left + 1;
    }

    private sealed record ContinuityFrame(
        string Path,
        byte[] Pixels,
        bool[] AlphaMask,
        int Left,
        int Top,
        int Right,
        int Bottom,
        int OpaqueArea,
        int BrimWidth,
        double BrimCenterX,
        double BrimCenterY,
        double CapCenterX,
        double CapCenterY)
    {
        public int VisibleWidth => Right - Left + 1;
        public int VisibleHeight => Bottom - Top + 1;
    }

    private sealed record SpriteVisualMetrics(
        int BrimWidth,
        double BrimCenterX,
        double BrimCenterYRatio,
        int Left,
        int Top,
        int Right,
        int Bottom,
        int OpaqueArea)
    {
        public int VisibleWidth => Right - Left + 1;
        public int VisibleHeight => Bottom - Top + 1;
    }

    private sealed record RuntimePage(
        string Name,
        string ResourcePath,
        int Width,
        int Height,
        int UncompressedByteCount,
        int PayloadByteCount,
        string Encoding,
        string ContentSha256,
        string DecodedSha256,
        IDictionary Frames,
        object RuntimeValue);

}
