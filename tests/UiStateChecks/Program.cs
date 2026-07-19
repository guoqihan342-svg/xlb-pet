using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private const long MaximumDecodedSpritePageBytes = 24L * 1024L * 1024L;
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags StaticFlags =
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [STAThread]
    private static void Main(string[] args)
    {
        _ = new Application();
        AssertLoggingContract();
        RunCheck(nameof(AssertRuntimeJankSourceContract), AssertRuntimeJankSourceContract);

        var settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            $"xlb-pet-ui-checks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(settingsDirectory);

        var window = new MainWindow
        {
            Left = 200,
            Top = 160,
            ShowActivated = false
        };

        try
        {
            SetField(
                window,
                "_settingsStore",
                new AppSettingsStore(Path.Combine(settingsDirectory, "settings.json")));

            if (args.Contains("--pet-size-only", StringComparer.OrdinalIgnoreCase))
            {
                RunCheck(nameof(AssertTodoWindowLayoutApiAndIme), AssertTodoWindowLayoutApiAndIme);
                RunCheck(nameof(AssertPetSizeScaleContract), () => AssertPetSizeScaleContract(window));
                return;
            }

            if (args.Contains("--clip-clock-only", StringComparer.OrdinalIgnoreCase))
            {
                RunCheck(nameof(AssertSingleBufferPremultipliedBlendContract),
                    () => AssertSingleBufferPremultipliedBlendContract(window));
                RunCheck(nameof(AssertColdSpritePageClipClockContract),
                    () => AssertColdSpritePageClipClockContract(window));
                return;
            }

            if (args.Contains("--todo-only", StringComparer.OrdinalIgnoreCase))
            {
                Invoke(window, "ApplyPetSizeScale", 1d, false, false);
                RunCheck(nameof(AssertOwnedTodoWindowContract),
                    () => AssertOwnedTodoWindowContract(window));
                RunCheck(nameof(AssertTodoWindowLayoutApiAndIme), AssertTodoWindowLayoutApiAndIme);
                return;
            }

            RunCheck(nameof(AssertDisplayFrameContract), () => AssertDisplayFrameContract(window));
            RunCheck(nameof(AssertSupersededPendingSpriteFrameDoesNotFlashBack),
                () => AssertSupersededPendingSpriteFrameDoesNotFlashBack(window));
            RunCheck(nameof(AssertColdSpritePageClipClockContract),
                () => AssertColdSpritePageClipClockContract(window));
            RunCheck(nameof(AssertSpriteAtlasDecodedPageLimitFailClosed),
                AssertSpriteAtlasDecodedPageLimitFailClosed);
            RunCheck(nameof(AssertHighDensityScalingAndDpiContract), () => AssertHighDensityScalingAndDpiContract(window));
            RunCheck(nameof(AssertMotionTimelineContract), () => AssertMotionTimelineContract(window));
            RunCheck(nameof(AssertNoRunContract), () => AssertNoRunContract(window));
            RunCheck(nameof(AssertAbsoluteTimelineMathContract), () => AssertAbsoluteTimelineMathContract(window));
            RunCheck(nameof(AssertExactEdgeContactContract), AssertExactEdgeContactContract);
            RunCheck(nameof(AssertManualTopDockIntegration), () => AssertManualTopDockIntegration(window));
            RunCheck(nameof(AssertRandomActivityBag), () => AssertRandomActivityBag(window));
            RunCheck(nameof(AssertMonitorWorkAreaContract), () => AssertMonitorWorkAreaContract(window));
            RunCheck(nameof(AssertDisplaySettingsChangeRecovery), () => AssertDisplaySettingsChangeRecovery(window));
            RunCheck(nameof(AssertOwnedTodoWindowContract), () => AssertOwnedTodoWindowContract(window));
            RunCheck(nameof(AssertTodoWindowLayoutApiAndIme), AssertTodoWindowLayoutApiAndIme);
            RunCheck(nameof(AssertPetSizeScaleContract), () => AssertPetSizeScaleContract(window));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            throw;
        }
        finally
        {
            window.Close();
            Application.Current.Shutdown();
            try
            {
                Directory.Delete(settingsDirectory, recursive: true);
            }
            catch
            {
                // 测试临时目录清理失败不应掩盖产品契约结果。
            }
        }

        Console.WriteLine("UI state checks passed.");
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
        var spritePagePixels = GetField<byte[]>(window, "_spritePagePixels");
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
        var reusablePageBufferBytes = spritePagePixels.LongLength;
        var spritePagePrefetchPixels =
            GetField<byte[]>(window, "_spritePagePrefetchPixels");
        Assert(maximumDecodedPageBytes <= MaximumDecodedSpritePageBytes &&
               reusablePageBufferBytes == maximumDecodedPageBytes &&
               spritePagePrefetchPixels.LongLength == maximumDecodedPageBytes &&
               !ReferenceEquals(spritePagePixels, spritePagePrefetchPixels),
            $"高密度分页及当前页+预取页双缓冲均不得超过24MiB，实际最大页 " +
            $"{maximumDecodedPageBytes / 1024d / 1024d:F2}MiB，复用缓冲 " +
            $"{reusablePageBufferBytes / 1024d / 1024d:F2}MiB");
        var bitmapFields = typeof(MainWindow).GetFields(InstanceFlags)
                .Select(field => field.GetValue(window))
                .OfType<BitmapSource>()
                .ToArray();
        Assert(bitmapFields.Length == 1 &&
               bitmapFields.Contains(displayFrameBuffer),
            $"MainWindow只能常驻一个{RenderPixelWidth}×{RenderPixelHeight}" +
            "高密度显示位图；分页只允许当前页+一个预取页Pbgra32数组，" +
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
                GetProperty<string>(entry.Value!, "PreviewResourcePath"),
                GetProperty<int>(entry.Value!, "Width"),
                GetProperty<int>(entry.Value!, "Height"),
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
            AssertBufferMatchesPage(spritePagePixels, page);

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
                spritePagePixels,
                spriteBrush);
        }

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
        AssertSingleBufferPremultipliedBlendContract(window);
        AssertCompressedPageLoadPerformance(window, pages);
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
        var coldFrame = GetDictionaryEntries(
            GetProperty<IDictionary>(coldPageEntry.Value!, "Frames"))[0].Value!;
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
        SetField(window, "_prefetchedSpritePageName", null);
        SetField(window, "_prefetchedSpritePageStride", 0);
        SetField(window, "_renderDeferredSpritePageName", null);
        SetField(window, "_renderDeferredSpritePageUrgent", false);
        SetField(window, "_renderDeferredSpritePageCancellation", false);

        Invoke(
            window,
            "LoadSpritePageIntoBuffer",
            hotPageName,
            hotPageEntry.Value!);
        Invoke(window, "ShowStableFrame", hotFrameA1);

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
               GetRawField(window, "_spritePagePrefetchTask") is null,
            "Rendering 中的冷页请求必须只唤醒一次性调度，不能直接创建后台任务");
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

    private static void AssertColdSpritePageClipClockContract(MainWindow window)
    {
        var idleFrame = GetField<object>(window, "_idleFrame");
        PrimeSpritePageForFrame(window, idleFrame);
        Invoke(window, "ShowStableFrame", idleFrame);

        var clip = GetField<Array>(window, "_reactionClips").GetValue(0)!;
        var clipFrames = GetClipFrames(clip);
        var firstColdFrameIndex = Enumerable.Range(0, clipFrames.Length)
            .First(index => !string.Equals(
                GetSpriteFrameInfo(GetProperty<object>(clipFrames.GetValue(index)!, "Image"))
                    .PageName,
                GetSpriteFrameInfo(idleFrame).PageName,
                StringComparison.Ordinal));
        Assert(firstColdFrameIndex == 27,
            "共享 idle/wake 页播放完 wake01..27 后，普通动作必须在索引27首次切入动作入口桥冷页");
        var firstAnimationFrame = clipFrames.GetValue(firstColdFrameIndex)!;
        var firstSpriteFrame = GetProperty<object>(firstAnimationFrame, "Image");
        var firstHoldDuration = GetProperty<TimeSpan>(firstAnimationFrame, "HoldDuration");
        var firstPageName = GetSpriteFrameInfo(firstSpriteFrame).PageName;
        Assert(!string.Equals(
                GetRawField(window, "_loadedSpritePageName") as string,
                firstPageName,
                StringComparison.Ordinal),
            "冷页动作时钟测试必须从不同于待机页的动作分页开始");

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
                            StopwatchTicksFromMilliseconds(firstHoldDuration.TotalMilliseconds);
        Assert(Equals(GetRawField(window, "_currentSpriteFrame"), firstSpriteFrame) &&
               GetField<int>(window, "_activeFrameIndex") == firstColdFrameIndex &&
               GetField<long>(window, "_activeClipStartedTimestamp") == coldDisplayedAt &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") == firstDeadline,
            "冷页即使延迟超过单帧间隔，也必须从目标首帧开始并以实际显示时刻重基准，不能补播跳帧");
        Invoke(window, "AdvanceActiveClip", firstDeadline - 1);
        Assert(GetField<int>(window, "_activeFrameIndex") == firstColdFrameIndex,
            "冷页首帧的完整hold必须从实际显示时刻计算");

        // The same page is now hot. A delayed Rendering callback must keep the
        // existing absolute timeline and resolve directly to the current pose.
        SetField(window, "_activeClip", clip);
        SetField(window, "_activeFrameIndex", firstColdFrameIndex - 1);
        SetField(window, "_nextFrameMinimumHold", TimeSpan.Zero);
        var hotStartedAt = coldDisplayedAt + StopwatchTicksFromSeconds(1);
        Invoke(window, "ShowActiveClipFrameAt", firstColdFrameIndex, hotStartedAt);
        Assert(GetField<long>(window, "_activeClipStartedTimestamp") == hotStartedAt &&
               GetField<long>(window, "_activeFrameDeadlineTimestamp") ==
               hotStartedAt + StopwatchTicksFromMilliseconds(firstHoldDuration.TotalMilliseconds),
            "热页动作必须保持原有绝对时间起点，不得进入冷页冻结路径");

        var delayedRenderingAt = hotStartedAt + StopwatchTicksFromMilliseconds(250);
        var expectedFrameIndex = firstColdFrameIndex;
        var expectedDeadline = hotStartedAt +
                               StopwatchTicksFromMilliseconds(firstHoldDuration.TotalMilliseconds);
        while (expectedFrameIndex + 1 < clipFrames.Length &&
               delayedRenderingAt >= expectedDeadline)
        {
            expectedFrameIndex++;
            var expectedFrame = clipFrames.GetValue(expectedFrameIndex)!;
            expectedDeadline += StopwatchTicksFromMilliseconds(
                GetProperty<TimeSpan>(expectedFrame, "HoldDuration").TotalMilliseconds);
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
        var edgeFrame = GetField<Array>(window, "_edgeLeftFrames").GetValue(0)!;
        var edgePageName = GetSpriteFrameInfo(edgeFrame).PageName;
        var idlePageName = GetSpriteFrameInfo(idleFrame).PageName;
        Assert(string.Equals(edgePageName, idlePageName, StringComparison.Ordinal),
            "12个边缘探头姿势必须与 idle/wake 共用常驻页，拖动松开时不得等待冷页解码");
        var edgePrefetchGeneration = GetField<int>(window, "_spritePagePrefetchGeneration");
        var edgePrefetchTask = GetRawField(window, "_spritePagePrefetchTask");
        var edgeDesiredPage = GetRawField(window, "_desiredSpritePageName");
        Invoke(window, "EnterEdgePeek", GetNestedEnum("EdgeDock", "Left"));
        Assert(GetRawField(window, "_edgeDock")!.ToString() == "Left" &&
               Equals(GetRawField(window, "_currentSpriteFrame"), edgeFrame) &&
               string.Equals(
                   GetRawField(window, "_loadedSpritePageName") as string,
                   idlePageName,
                   StringComparison.Ordinal) &&
               GetRawField(window, "_pendingSpriteFrame") is null &&
               GetField<int>(window, "_spritePagePrefetchGeneration") == edgePrefetchGeneration &&
               ReferenceEquals(GetRawField(window, "_spritePagePrefetchTask"), edgePrefetchTask) &&
               Equals(GetRawField(window, "_desiredSpritePageName"), edgeDesiredPage) &&
               GetField<bool>(window, "_isVisualClockSubscribed"),
            "进入边缘探头必须从常驻 idle 页原子切到首帧，且不得创建新的分页预取");
        Invoke(window, "ExitEdgePeek", false, true);
        Assert(GetRawField(window, "_edgeDock")!.ToString() == "None" &&
               GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0 &&
               Equals(GetRawField(window, "_currentSpriteFrame"), idleFrame),
            "退出常驻页边缘探头后必须清理时钟并直接恢复 idle");

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
        SetField(window, "_spritePagePrefetchGeneration", generation + 1);
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
        Assert(displayPixels.Length == expectedByteCount &&
               fromPixels.Length == expectedByteCount &&
               targetPixels.Length == expectedByteCount &&
               outputPixels.Length == expectedByteCount &&
               transformedPixels.Length == expectedByteCount,
            $"帧淡化只能复用{RenderPixelWidth}×{RenderPixelHeight}固定高密度像素数组，" +
            "不能为每帧创建BitmapSource");

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
        var previewPath = FindWorkspaceFile(page.PreviewResourcePath.Split('/'));
        BitmapSource pageBitmap;
        using (var stream = File.OpenRead(previewPath))
        {
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            Assert(decoder.Frames.Count == 1,
                $"分页预览PNG必须只有一个图像帧：{page.Name}");
            pageBitmap = decoder.Frames[0];
        }

        Assert(pageBitmap.PixelWidth == page.Width &&
               pageBitmap.PixelHeight == page.Height,
            $"分页预览PNG尺寸必须匹配运行时元数据：{page.Name}");
        BitmapSource premultipliedPage = pageBitmap;
        if (pageBitmap.Format != PixelFormats.Pbgra32)
        {
            premultipliedPage = new FormatConvertedBitmap(
                pageBitmap,
                PixelFormats.Pbgra32,
                null,
                0);
        }

        var stride = checked(page.Width * 4);
        var byteCount = checked(stride * page.Height);
        var expectedPixels = new byte[byteCount];
        var pageBounds = new Int32Rect(0, 0, page.Width, page.Height);
        premultipliedPage.CopyPixels(pageBounds, expectedPixels, stride, 0);
        Assert(spritePagePixels.AsSpan(0, byteCount).SequenceEqual(expectedPixels),
            $"{page.Name} 的LZ4分页解压结果必须逐像素等于预览PNG的Pbgra32内容");
    }

    private static void AssertCompressedPageLoadPerformance(
        MainWindow window,
        RuntimePage[] pages)
    {
        var loadMethod = typeof(MainWindow).GetMethod(
            "LoadSpritePageIntoBuffer",
            InstanceFlags)
            ?? throw new InvalidOperationException(
                "找不到LoadSpritePageIntoBuffer，无法验证LZ4分页加载性能");
        const int measuredRuns = 5;
        const double maximumMedianMilliseconds = 20d;
        const double maximumSingleRunMilliseconds = 60d;

        foreach (var page in pages)
        {
            // 先预热程序集资源流与JIT；门槛只约束热盘加载，避免把Windows CI的
            // 首次文件映射抖动误判成解压回归。
            _ = loadMethod.Invoke(window, new[] { (object)page.Name, page.RuntimeValue });
            var elapsed = new double[measuredRuns];
            for (var index = 0; index < measuredRuns; index++)
            {
                var stopwatch = Stopwatch.StartNew();
                _ = loadMethod.Invoke(window, new[] { (object)page.Name, page.RuntimeValue });
                stopwatch.Stop();
                elapsed[index] = stopwatch.Elapsed.TotalMilliseconds;
            }

            Array.Sort(elapsed);
            var median = elapsed[measuredRuns / 2];
            var maximum = elapsed[^1];
            Assert(median <= maximumMedianMilliseconds &&
                   maximum <= maximumSingleRunMilliseconds,
                $"{page.Name} 的热盘LZ4分页加载过慢：" +
                $"中位数 {median:F2}ms（上限 {maximumMedianMilliseconds:F0}ms），" +
                $"最大 {maximum:F2}ms（上限 {maximumSingleRunMilliseconds:F0}ms）");
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
        Assert(root.GetProperty("version").GetInt32() == 3,
            "无损LZ4 Pbgra分页图集清单版本必须为3");
        Assert(root.GetProperty("displayWidth").GetInt32() == RenderPixelWidth &&
               root.GetProperty("displayHeight").GetInt32() == RenderPixelHeight,
            $"分页图集渲染视口必须为{RenderPixelWidth}×{RenderPixelHeight}，" +
            $"同时由WPF保留{LogicalPetWidth}×{LogicalPetHeight}逻辑尺寸");
        var expectedSourcePaths = BuildExpectedSourceResourcePaths();
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
        Assert(manifestPageCount == 8 &&
               manifestSourceFrameCount == 218 &&
               manifestPageFrameCount == expectedSourcePaths.Length &&
               manifestPageFrameCount == manifestSourceFrameCount &&
               !manifestPages.TryGetProperty("edge", out _),
            $"共享 idle/wake/edge 契约应为8页且每个源帧只出现一次；" +
            $"source={manifestSourceFrameCount}, page-local={manifestPageFrameCount}, pages={manifestPageCount}");
        var expectedIdlePageFrames = Enumerable.Range(1, 27)
            .Select(frameNumber => $"Assets/luban-wake-{frameNumber:00}.png")
            .Prepend("Assets/luban-idle.png")
            .Concat(new[] { "left", "top", "bottom" }.SelectMany(edgeName =>
                Enumerable.Range(1, 4).Select(frameNumber =>
                    $"Assets/luban-edge-{edgeName}-{frameNumber:00}.png")))
            .ToHashSet(StringComparer.Ordinal);
        var actualIdlePageFrames = manifestPages.GetProperty("idle")
            .GetProperty("frames")
            .EnumerateObject()
            .Select(frame => frame.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert(actualIdlePageFrames.SetEquals(expectedIdlePageFrames),
            "常驻 idle 页必须且只能包含 idle、27帧 wake 与12帧 edge，共40个逻辑帧");
        foreach (var actionName in new[] { "yawn", "cry", "cute", "like", "eat", "wave", "think" })
        {
            var pageName = $"action-{actionName}";
            var expectedActionFrames = Enumerable.Range(1, 24)
                .Select(frameNumber =>
                    $"Assets/luban-{actionName}-frame-{frameNumber:00}.png")
                .ToHashSet(StringComparer.Ordinal);
            _ = expectedActionFrames.Add($"Assets/luban-{actionName}-entry-bridge.png");
            var internalBridgeAfter = GetExpectedInternalBridgeAfter(actionName);
            if (internalBridgeAfter is int bridgeAfter)
            {
                _ = expectedActionFrames.Add(
                    $"Assets/luban-{actionName}-bridge-{bridgeAfter:00}-{bridgeAfter + 1:00}.png");
            }
            var actualActionFrames = manifestPages.GetProperty(pageName)
                .GetProperty("frames")
                .EnumerateObject()
                .Select(frame => frame.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert(actualActionFrames.SetEquals(expectedActionFrames),
                $"{pageName} 必须只包含24个动作姿势、入口桥及约定的内部桥，不得重复 idle、wake 或 edge");
        }
        var runtimeByName = pages.ToDictionary(page => page.Name, StringComparer.Ordinal);
        var pageResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFrames = new HashSet<string>(StringComparer.Ordinal);
        var totalPageFrames = 0;
        foreach (var manifestPageEntry in manifestPages.EnumerateObject())
        {
            Assert(runtimeByName.TryGetValue(manifestPageEntry.Name, out var runtimePage),
                $"运行时缺少分页：{manifestPageEntry.Name}");
            var descriptor = manifestPageEntry.Value;
            var resource = descriptor.GetProperty("resource").GetString()
                ?? throw new InvalidOperationException("分页resource不能为空");
            var previewResource = descriptor.GetProperty("previewResource").GetString()
                ?? throw new InvalidOperationException("分页previewResource不能为空");
            var width = descriptor.GetProperty("width").GetInt32();
            var height = descriptor.GetProperty("height").GetInt32();
            var uncompressedByteCount =
                descriptor.GetProperty("uncompressedByteCount").GetInt64();
            var compressedByteCount = descriptor.GetProperty("compressedByteCount").GetInt64();
            var logicalCount = descriptor.GetProperty("logicalFrameCount").GetInt32();
            var uniqueCount = descriptor.GetProperty("uniqueSpriteCount").GetInt32();
            var manifestFrames = descriptor.GetProperty("frames");
            var sourceFingerprint = descriptor.GetProperty("sourceFingerprint").GetString()
                ?? throw new InvalidOperationException(
                    $"分页sourceFingerprint不能为空：{manifestPageEntry.Name}");
            var contentSha256 = descriptor.GetProperty("contentSha256").GetString()
                ?? throw new InvalidOperationException(
                    $"分页contentSha256不能为空：{manifestPageEntry.Name}");
            var previewSha256 = descriptor.GetProperty("previewSha256").GetString()
                ?? throw new InvalidOperationException(
                    $"分页previewSha256不能为空：{manifestPageEntry.Name}");

            var expectedResource =
                $"Assets/sprite-pages/luban-{manifestPageEntry.Name}.pbgra.lz4";
            var expectedPreviewResource =
                $"Assets/sprite-pages/luban-{manifestPageEntry.Name}.png";
            Assert(string.Equals(resource, expectedResource, StringComparison.Ordinal) &&
                   string.Equals(previewResource, expectedPreviewResource, StringComparison.Ordinal),
                $"{manifestPageEntry.Name} 必须使用约定的.lz4运行时资源和同名PNG预览资源");
            Assert(runtimePage!.ResourcePath == resource &&
                   runtimePage.PreviewResourcePath == previewResource &&
                   runtimePage.Width == width && runtimePage.Height == height,
                $"运行时分页元数据必须与清单一致：{manifestPageEntry.Name}");
            var expectedUncompressedByteCount = checked((long)width * height * 4);
            Assert(uncompressedByteCount == expectedUncompressedByteCount &&
                   uncompressedByteCount <= manifestMaximumDecodedPageBytes,
                $"{manifestPageEntry.Name} 的uncompressedByteCount必须等于width×height×4，" +
                $"且不超过清单页上限；实际 {uncompressedByteCount} bytes，上限 " +
                $"{manifestMaximumDecodedPageBytes} bytes");
            Assert(runtimePage.Frames.Count == logicalCount &&
                   manifestFrames.EnumerateObject().Count() == logicalCount,
                $"分页帧数必须与清单一致：{manifestPageEntry.Name}");
            totalPageFrames += logicalCount;
            _ = pageResources.Add(resource);
            _ = previewResources.Add(previewResource);

            var compressedPath = FindWorkspaceFile(resource.Split('/'));
            var maximumCompressedByteCount =
                uncompressedByteCount + uncompressedByteCount / 255L + 16;
            Assert(new FileInfo(compressedPath).Length == compressedByteCount &&
                   compressedByteCount is > 0 &&
                   compressedByteCount <= maximumCompressedByteCount,
                $"分页LZ4资源实际字节数必须匹配compressedByteCount、不得为空，且不得超过" +
                $"LZ4 block最坏长度 {maximumCompressedByteCount} bytes：" +
                $"{manifestPageEntry.Name}");
            var pngPath = FindWorkspaceFile(previewResource.Split('/'));
            using (var stream = File.OpenRead(pngPath))
            {
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                Assert(decoder.Frames.Count == 1 &&
                       decoder.Frames[0].PixelWidth == width &&
                       decoder.Frames[0].PixelHeight == height,
                    $"分页预览PNG尺寸必须匹配清单：{manifestPageEntry.Name}");
            }

            AssertCanonicalSha256(sourceFingerprint,
                $"{manifestPageEntry.Name}/sourceFingerprint");
            AssertCanonicalSha256(contentSha256,
                $"{manifestPageEntry.Name}/contentSha256");
            AssertCanonicalSha256(previewSha256,
                $"{manifestPageEntry.Name}/previewSha256");
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
                $"{manifestPageEntry.Name} 的contentSha256必须匹配实际LZ4内容");
            Assert(string.Equals(
                    previewSha256,
                    ComputeFileSha256(pngPath),
                    StringComparison.Ordinal),
                $"{manifestPageEntry.Name} 的previewSha256必须匹配实际预览PNG内容");

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
                    $"运行时Frame必须与v3清单一致：{manifestPageEntry.Name}/{manifestFrameEntry.Name}");
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
               pageResources.Count == manifestPageCount &&
               previewResources.Count == manifestPageCount,
            $"{manifestPageCount}页必须动态覆盖清单声明的{manifestPageFrameCount}个PageFrame和" +
            $"运行时声明的{expectedSourcePaths.Length}个源逻辑帧");
        AssertProjectAndAssemblyResourceContract(pageResources, previewResources);
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
        const int maximumCompressedByteCount =
            decodedByteCount + decodedByteCount / 255 + 16;
        AssertThrowsInvalidOperation(
            () => validatePageSize.Invoke(
                null,
                new object[]
                {
                    "oversized-page",
                    width,
                    height,
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
                    width,
                    height,
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
                    width,
                    height,
                    decodedByteCount,
                    maximumCompressedByteCount + 1,
                    decodedByteCount
                }),
            "分页compressedByteCount超过LZ4 block最坏长度时运行时必须fail-closed");
        _ = validatePageSize.Invoke(
            null,
            new object[]
            {
                "valid-page",
                width,
                height,
                decodedByteCount,
                maximumCompressedByteCount,
                checked((int)MaximumDecodedSpritePageBytes)
            });
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

    private static string[] BuildExpectedSourceResourcePaths()
    {
        var paths = (string[])(InvokeStatic(
            typeof(MainWindow),
            "BuildSpriteResourcePaths") ?? Array.Empty<string>());
        Assert(paths.Length > 0 &&
               paths.Distinct(StringComparer.Ordinal).Count() == paths.Length,
            "运行时源PNG路径清单不得为空或包含重复项");
        return paths;
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
        HashSet<string> expectedPageResources,
        HashSet<string> previewResources)
    {
        var projectPath = FindWorkspaceFile("DesktopPet.csproj");
        var project = XDocument.Load(projectPath);
        var includes = project.Descendants()
            .Where(element => element.Name.LocalName == "Resource")
            .Select(element => ((string?)element.Attribute("Include") ?? string.Empty)
                .Replace('\\', '/'))
            .ToArray();
        Assert(includes.Length == 2 &&
               includes.Contains("Assets/sprite-pages/*.pbgra.lz4", StringComparer.OrdinalIgnoreCase) &&
               includes.Contains("Assets/luban-sprite-pages.json", StringComparer.OrdinalIgnoreCase),
            "csproj只能嵌入无损LZ4分页通配符和v3 manifest");
        Assert(!includes.Any(include =>
                include.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                include.EndsWith("*.png", StringComparison.OrdinalIgnoreCase)),
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
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(assetKeys.SetEquals(expectedAssets) &&
               assetKeys.Count == expectedPageResources.Count + 1,
            $"主程序集Assets资源必须严格等于{expectedPageResources.Count}个LZ4分页和一个v3 manifest");
        Assert(!assetKeys.Any(key =>
                key.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
            "主程序集不得包含分页预览PNG、旧单atlas或源PNG");
        Assert(!previewResources.Overlaps(assetKeys),
            "previewResource只用于仓库内验图，不得作为WPF Resource嵌入主程序集");
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
            Assert(Path.GetFileName(
                       generatedPage.GetProperty("previewResource").GetString()) ==
                   Path.GetFileName(
                       committedPage.Value.GetProperty("previewResource").GetString()),
                $"分页 {committedPage.Name} 的预览资源文件名不一致");
            foreach (var property in new[]
                     {
                          "width",
                          "height",
                          "uncompressedByteCount",
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
                         "contentSha256",
                         "previewSha256"
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
        Assert(motionFrameInterval == TimeSpan.FromMilliseconds(33) &&
               todoMotionFrameInterval == TimeSpan.FromMilliseconds(33) &&
               actionLoopFrameInterval == TimeSpan.FromMilliseconds(33) &&
               actionLoopCycleCount == 43,
            "普通动作、Todo与微循环必须统一使用33ms姿势间隔，微循环固定43轮");
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
                $"{actionName} 必须按资源名精确播放27帧wake、入口桥、内部桥、43轮微循环及反向返回");
            var spriteFrames = GetClipFrames(clip)
                .Cast<object>()
                .Select(frame => GetSpriteFrameInfo(GetProperty<object>(frame, "Image")))
                .ToArray();
            var expectedPageName = $"action-{actionName}";
            Assert(spriteFrames.All(frame =>
                    frame.Name.Contains("luban-idle", StringComparison.Ordinal) ||
                    frame.Name.Contains("luban-wake-", StringComparison.Ordinal)
                        ? frame.PageName == "idle"
                        : frame.PageName == expectedPageName),
                $"{actionName} 的 idle/wake 必须来自共享 idle 页，动作姿势必须来自 {expectedPageName} 页");

            var expectedResourceNames = expectedTimelineNames
                .Select(frameName => $"Assets/{frameName}")
                .ToHashSet(StringComparer.Ordinal);
            var actualResourceNames = spriteFrames
                .Select(frame => frame.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert(actualResourceNames.SetEquals(expectedResourceNames),
                $"{actionName} 时间轴应完整使用共享 idle、27帧 wake、入口桥、本页24帧动作和约定内部桥");
            Assert(frames.Length == expectedMotionNames.Length &&
                   frames.All(frame => GetFrameDuration(frame) == motionFrameInterval),
                $"{actionName} 的{expectedMotionNames.Length}帧必须全部使用33ms绝对时间间隔");
            var expectedActionFrameIndex = expectedTimelineNames.Length - 2;
            Assert(GetProperty<int>(clip, "ActionFrameIndex") == expectedActionFrameIndex &&
                   actualMotionNames[expectedActionFrameIndex] ==
                   $"luban-{actionName}-frame-24.png",
                $"{actionName} ActionFrameIndex 必须指向入口路径末端的第24动作姿势");
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
               actualTodoEnterNames.Take(28)
                   .All(frame => frame.PageName == "idle") &&
               actualTodoEnterNames.Skip(28)
                   .All(frame => frame.PageName == "action-think"),
            "Todo 入场必须按 idle→wake01..27→think入口桥→think01..06→内部桥→think07..24 跨页播放");
        Assert(todoExitFrames
                .Select(frame => GetProperty<string>(frame, "Name"))
                .SequenceEqual(todoEnterFrames
                    .Select(frame => GetProperty<string>(frame, "Name"))
                    .Reverse()),
            "Todo 入场和收起必须严格互为反序，快速切换时才能映射到同一姿势");
    }

    private static int? GetExpectedInternalBridgeAfter(string actionName) => actionName switch
    {
        "yawn" => 6,
        "cry" => 3,
        "think" => 6,
        _ => null
    };

    private static string[] BuildExpectedActionTimelineNames(string actionName)
    {
        var names = new List<string>(54)
        {
            "luban-idle.png"
        };
        names.AddRange(Enumerable.Range(1, 27)
            .Select(frameNumber => $"luban-wake-{frameNumber:00}.png"));
        names.Add($"luban-{actionName}-entry-bridge.png");
        var internalBridgeAfter = GetExpectedInternalBridgeAfter(actionName);
        for (var frameNumber = 1; frameNumber <= 24; frameNumber++)
        {
            names.Add($"luban-{actionName}-frame-{frameNumber:00}.png");
            if (internalBridgeAfter == frameNumber)
            {
                names.Add(
                    $"luban-{actionName}-bridge-{frameNumber:00}-{frameNumber + 1:00}.png");
            }
        }

        return names.ToArray();
    }

    private static string[] BuildExpectedMotionFrameNames(string actionName)
    {
        var timeline = BuildExpectedActionTimelineNames(actionName);
        var names = new List<string>();
        names.AddRange(timeline.Skip(1));
        var loopPoseNumbers = actionName == "cute"
            ? new[] { 21, 22, 24, 22 }
            : new[] { 21, 22, 23, 24 };
        for (var cycle = 0; cycle < 43; cycle++)
        {
            names.AddRange(loopPoseNumbers.Select(poseNumber =>
                $"luban-{actionName}-frame-{poseNumber:00}.png"));
        }

        var finalLoopPoseName =
            $"luban-{actionName}-frame-{loopPoseNumbers[^1]:00}.png";
        var returnStartIndex = Array.IndexOf(timeline, finalLoopPoseName) - 1;
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

    private static void AssertAbsoluteTimelineMathContract(MainWindow window)
    {
        var source = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
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
            var total = TimeSpan.FromTicks(durations.Sum(duration => duration.Ticks));
            var expectedFrameCount = BuildExpectedMotionFrameNames(actionName).Length;
            var expectedDuration = TimeSpan.FromMilliseconds(expectedFrameCount * 33);
            Assert(total == expectedDuration,
                $"{actionName} 的{expectedFrameCount}帧绝对时间轴应为" +
                $"{expectedDuration.TotalSeconds:F3}秒，实际 {total.TotalSeconds:F3} 秒");

            var absoluteCheckpoints = new[]
            {
                0d,
                0.032,
                0.033,
                1.0,
                3.210,
                total.TotalSeconds / 2,
                total.TotalSeconds - 0.001
            };
            var absoluteCheckpointIndices = absoluteCheckpoints
                .Select(checkpoint => ResolveAbsoluteFrameIndex(
                    durations,
                    TimeSpan.FromSeconds(checkpoint)))
                .ToArray();

            foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
            {
                var refreshInterval = TimeSpan.FromSeconds(1d / refreshRate);
                var renderCount = (long)Math.Ceiling(total.TotalSeconds * refreshRate);
                var completionAt = TimeSpan.FromSeconds(renderCount / refreshRate);
                Assert(completionAt >= total && completionAt - total <= refreshInterval,
                    $"{refreshRate:F0}Hz 下动作完成误差必须不超过一个刷新周期");

                for (var checkpointIndex = 0;
                     checkpointIndex < absoluteCheckpoints.Length;
                     checkpointIndex++)
                {
                    var elapsed = TimeSpan.FromSeconds(
                        absoluteCheckpoints[checkpointIndex]);
                    var actualIndex = ResolveAbsoluteFrameIndex(durations, elapsed);
                    Assert(actualIndex == absoluteCheckpointIndices[checkpointIndex],
                        $"{refreshRate:F0}Hz 下相同绝对时间必须与统一时间轴定位到同一动作帧");
                }
            }

            var beforeStall = TimeSpan.FromSeconds(3.210);
            var afterStall = beforeStall + TimeSpan.FromMilliseconds(250);
            var beforeIndex = ResolveAbsoluteFrameIndex(durations, beforeStall);
            var afterIndex = ResolveAbsoluteFrameIndex(durations, afterStall);
            Assert(afterIndex >= beforeIndex + 2 &&
                   afterIndex == ResolveAbsoluteFrameIndex(durations, afterStall),
                "250ms 渲染停顿后必须直接定位正确帧，不得只补播下一帧");
        }

        AssertTodoTransitionTimelineContract(window, source);
    }

    private static void AssertTodoTransitionTimelineContract(
        MainWindow window,
        string mainWindowSource)
    {
        var expectedEnterNames = BuildExpectedActionTimelineNames("think");
        var expectedFrameCount = expectedEnterNames.Length;
        var enterClip = GetRawField(window, "_todoEnterClip")!;
        var exitClip = GetRawField(window, "_todoExitClip")!;
        var enterFrames = GetClipFrames(enterClip).Cast<object>().ToArray();
        var exitFrames = GetClipFrames(exitClip).Cast<object>().ToArray();
        Assert(enterFrames.Length == expectedFrameCount &&
               exitFrames.Length == expectedFrameCount,
            "Todo 打开/收起必须使用 idle、27个wake、think入口桥、24个思考姿势及06-07内部桥，共54帧");
        var enterNames = enterFrames
            .Select(frame => GetProperty<string>(frame, "Name"))
            .ToArray();
        var exitNames = exitFrames
            .Select(frame => GetProperty<string>(frame, "Name"))
            .ToArray();
        Assert(enterNames.SequenceEqual(expectedEnterNames),
            "Todo 入场必须按资源名完整覆盖 wake01..27、think入口桥、think01..06、内部桥与think07..24");
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

        const string resumedFrameName = "luban-think-bridge-06-07.png";
        var resumedEnterIndex = Array.IndexOf(expectedEnterNames, resumedFrameName);
        Assert(resumedEnterIndex >= 0,
            "Todo完整路径必须包含 think06-07 内部桥");
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
            "普通 think 动作必须实际播放06-07内部桥，Todo才能从同名资源无缝续播");
        SetField(window, "_activeClip", thinkReactionClip);
        SetField(window, "_activeFrameIndex", resumedReactionIndex);
        SetField(window, "_activeClipStartedTimestamp", Stopwatch.GetTimestamp());
        SetField(window, "_activeFrameDeadlineTimestamp", long.MaxValue);
        SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
        Invoke(window, "EnterTodoVisualState");
        var resumedAt = GetField<long>(window, "_activeClipStartedTimestamp");
        var resumedDeadline = GetField<long>(window, "_activeFrameDeadlineTimestamp");
        Assert(ReferenceEquals(GetRawField(window, "_activeClip"), enterClip) &&
               GetField<int>(window, "_activeFrameIndex") == resumedEnterIndex &&
               Equals(GetRawField(window, "_currentSpriteFrame"), resumedSpriteFrame) &&
               resumedAt > 0 &&
               Math.Abs(StopwatchTicksToMilliseconds(resumedDeadline - resumedAt) - 33) <= 0.1 &&
               !GetField<bool>(window, "_isFrameBlending"),
            "思考内部桥中途右键必须保留同名像素姿势，并从对应33ms截止点继续，不能重播或淡化闪回");
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
        Assert(enterDurations.All(duration =>
                duration == TimeSpan.FromMilliseconds(33)),
            "Todo 54帧必须统一使用33ms姿势间隔（约30.3fps）");
        Assert(exitFrames.All(frame =>
                GetFrameDuration(frame) == TimeSpan.FromMilliseconds(33)),
            "Todo 收起必须保持与打开相同的33ms绝对时间节奏");

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
        Assert(total == TimeSpan.FromMilliseconds(1782),
            $"Todo 完整54帧时间线应为1.782秒，实际 {total.TotalMilliseconds:F0}ms");
        var checkpoints = new[]
        {
            0d, 0.032, 0.033, 0.891, 0.924, 1.122, 1.155, 1.188, 1.781
        };
        int[]? referenceFrames = null;
        foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
        {
            var refreshInterval = TimeSpan.FromSeconds(1d / refreshRate);
            var renderCount = (long)Math.Ceiling(total.TotalSeconds * refreshRate);
            var completionAt = TimeSpan.FromSeconds(renderCount / refreshRate);
            Assert(completionAt >= total && completionAt - total <= refreshInterval,
                $"Todo 在{refreshRate:F0}Hz下的完成误差必须不超过一个刷新周期");

            var resolvedFrames = checkpoints
                .Select(seconds => ResolveAbsoluteFrameIndex(
                    enterDurations,
                    TimeSpan.FromSeconds(seconds)))
                .ToArray();
            referenceFrames ??= resolvedFrames;
            Assert(resolvedFrames.SequenceEqual(referenceFrames),
                $"Todo 在{refreshRate:F0}Hz下相同绝对时间必须定位到相同姿势");
        }

        var beforeStall = TimeSpan.FromMilliseconds(140);
        var afterStall = beforeStall + TimeSpan.FromMilliseconds(250);
        var beforeStallFrame = ResolveAbsoluteFrameIndex(enterDurations, beforeStall);
        var afterStallFrame = ResolveAbsoluteFrameIndex(enterDurations, afterStall);
        Assert(afterStallFrame >= beforeStallFrame + 7 &&
               afterStallFrame == ResolveAbsoluteFrameIndex(
                   enterDurations,
                   afterStall),
            "Todo 遇到250ms UI延迟必须直接定位正确姿势，不能快速补播积压帧");
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

    private static double StopwatchTicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;

    private static void AssertExactEdgeContactContract()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        const double width = 190;
        const double height = 242;
        const double safeX = 500;
        const double safeY = 300;

        var cases = new[]
        {
            new EdgeCase("Left", new Rect(1.1, safeY, width, height),
                new Rect(1.0, safeY, width, height)),
            new EdgeCase("Right", new Rect(workArea.Right - width - 1.1, safeY, width, height),
                new Rect(workArea.Right - width - 1.0, safeY, width, height)),
            new EdgeCase("Top", new Rect(safeX, 1.1, width, height),
                new Rect(safeX, 1.0, width, height)),
            new EdgeCase("Bottom", new Rect(safeX, workArea.Bottom - height - 1.1, width, height),
                new Rect(safeX, workArea.Bottom - height - 1.0, width, height))
        };

        foreach (var edgeCase in cases)
        {
            var near = InvokeStatic(
                typeof(MainWindow),
                "FindTouchedEdge",
                workArea,
                edgeCase.NearBounds,
                1d)!;
            Assert(near.ToString() == "None",
                $"{edgeCase.Edge} 距边界 1.1 DIP 时不得提前吸附");

            var touching = InvokeStatic(
                typeof(MainWindow),
                "FindTouchedEdge",
                workArea,
                edgeCase.TouchingBounds,
                1d)!;
            Assert(touching.ToString() == edgeCase.Edge,
                $"{edgeCase.Edge} 距边界 1.0 DIP 时必须吸附");
        }

        var topLeftCorner = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(0, 0, width, height),
            1d)!;
        var topRightCorner = InvokeStatic(
            typeof(MainWindow),
            "FindTouchedEdge",
            workArea,
            new Rect(workArea.Right - width, 0, width, height),
            1d)!;
        Assert(topLeftCorner.ToString() == "Left" &&
               topRightCorner.ToString() == "Right",
            "顶部左右角仍应分别保留左、右吸附");
    }

    private static void AssertManualTopDockIntegration(MainWindow window)
    {
        var edgeFrameInterval = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgePeekFrameInterval",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var edgeEndpointHold = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgePeekEndpointHold",
                StaticFlags)!.GetValue(null) ?? TimeSpan.Zero);
        var edgeBlendDuration = (TimeSpan)(typeof(MainWindow).GetField(
                "EdgeFrameBlendDuration",
                StaticFlags)!.GetValue(null) ?? TimeSpan.MinValue);
        Assert(edgeFrameInterval == TimeSpan.FromMilliseconds(70) &&
               edgeEndpointHold == TimeSpan.FromMilliseconds(350) &&
               edgeBlendDuration == TimeSpan.Zero,
            "边缘探头必须使用70ms姿势间隔、350ms端点停留且禁用整图淡化");

        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        var monitorType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.MonitorWorkArea",
            throwOnError: true)!;
        var workArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
        var width = window.ActualWidth;
        var height = window.ActualHeight;
        var safeLeft = workArea.Left + Math.Max(20, (workArea.Width - width) / 2);
        var safeTop = workArea.Top + Math.Max(20, (workArea.Height - height) / 2);

        foreach (var edge in new[] { "Left", "Right", "Top", "Bottom" })
        {
            window.Left = edge switch
            {
                "Left" => workArea.Left,
                "Right" => workArea.Right - width,
                _ => safeLeft
            };
            window.Top = edge switch
            {
                "Top" => workArea.Top,
                "Bottom" => workArea.Bottom - height,
                _ => safeTop
            };
            Invoke(window, "UpdateEdgeDockAfterDrag");
            var deadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            Assert(GetField<object>(window, "_edgeDock").ToString() == edge &&
                   deadline > Stopwatch.GetTimestamp(),
                $"真实拖拽落点贴住{edge}边缘时必须保留吸附探头状态");
            Invoke(window, "AdvanceEdgePeek", deadline);
            var nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            AssertClose(StopwatchTicksToMilliseconds(nextDeadline - deadline), 70,
                $"{edge} 探头离开端点后必须以70ms节奏换帧");
            deadline = nextDeadline;
            Invoke(window, "AdvanceEdgePeek", deadline);
            nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            AssertClose(StopwatchTicksToMilliseconds(nextDeadline - deadline), 70,
                $"{edge} 探头中间帧必须维持70ms节奏");
            deadline = nextDeadline;
            Invoke(window, "AdvanceEdgePeek", deadline);
            nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            AssertClose(StopwatchTicksToMilliseconds(nextDeadline - deadline), 350,
                $"{edge} 探头到达另一端点后必须再次停留350ms");
            Invoke(window, "ExitEdgePeek", false, true);
            Assert(GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0,
                $"退出{edge}探头后必须清除绝对时间截止点");
        }

        window.Left = safeLeft;
        window.Top = safeTop;
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
                   "_automaticTimer.Interval = PillowAnimationDuration",
                   StringComparison.Ordinal) &&
               startPillowSource.Contains("_automaticTimer.Start()", StringComparison.Ordinal) &&
               !startPillowSource.Contains("DoubleAnimation", StringComparison.Ordinal) &&
               !startPillowSource.Contains("BeginAnimation", StringComparison.Ordinal) &&
               !mainSource.Contains("new DoubleAnimation", StringComparison.Ordinal) &&
               beginAnimationCalls.All(call => call.Contains(", null)", StringComparison.Ordinal)),
            "枕头待机必须仅用automaticTimer占位5秒；不得创建DoubleAnimation，有BeginAnimation也只能传null清理旧动画");

        Invoke(window, "StartPillowBreathing");
        Assert(GetField<bool>(window, "_isPillowBreathing") &&
               automaticTimer.IsEnabled &&
               automaticTimer.Interval == TimeSpan.FromSeconds(5) &&
               !petScale.HasAnimatedProperties &&
               Math.Abs(petScale.ScaleX - 1) < 0.000001 &&
               Math.Abs(petScale.ScaleY - 1) < 0.000001,
            "枕头待机占位启动后必须只运行5秒automaticTimer，视觉缩放保持静止且零动画属性");
        Invoke(window, "StopPillowBreathing");
        Assert(!GetField<bool>(window, "_isPillowBreathing") &&
               !automaticTimer.IsEnabled &&
               !petScale.HasAnimatedProperties,
            "停止枕头待机占位后必须关闭automaticTimer且不遗留WPF动画");
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
        // 这项测试会打开真实的可激活 Owned Window。测试运行期间终端或
        // 其他进程抢焦点不应被误判为用户在待办外部点击。
        SetField(window, "_suppressTodoWindowDeactivate", true);
        try
        {
        window.Show();
        PumpDispatcher(TimeSpan.FromMilliseconds(40));

        var todoWindow = GetField<TodoWindow>(window, "_todoWindow");
        Assert(ReferenceEquals(todoWindow.Owner, window),
            "待办窗口必须是 MainWindow 的 Owned Window");
        Assert(!todoWindow.IsVisible, "待办窗口不得在主窗口启动时自动显示");

        var reactionClip = GetField<Array>(window, "_reactionClips").GetValue(0)!;
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
                   ordinaryActionFrame)! == 27,
            "从普通动作打开待办必须直接从wake27接入，不能闪回趴枕头待机");

        var originalRight = window.Left + window.Width;
        var originalBottom = window.Top + window.Height;
        Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
        var ordinaryTodoStartIndex = GetField<int>(window, "_activeFrameIndex");
        var todoEnterClip = GetRawField(window, "_activeClip")!;
        var requestedTodoEntryFrame = GetProperty<object>(
            GetClipFrames(todoEnterClip).GetValue(ordinaryTodoStartIndex)!,
            "Image");
        var requestedTodoEntryFrameInfo = GetSpriteFrameInfo(requestedTodoEntryFrame);
        WaitForPrefetchedSpritePage(window, requestedTodoEntryFrame);
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
        Assert(todoFrame.PageName == "action-think" &&
               todoFrame.Name == "Assets/luban-think-frame-24.png",
            "Todo 状态应使用专用的全身思考姿势");
        Assert(todoFrame.Height >= idleFrame.Height + 30,
            "Todo 姿势的可见高度应显著高于趴枕头待机，不能产生突然缩小的错觉");
        Assert(GetProperty<string>(todoEnterClip, "ActionName") == "todo-open",
            "打开 Todo 必须抢占普通动作并启动平滑起身入场");
        Assert(GetField<long>(window, "_activeFrameDeadlineTimestamp") > 0 &&
               GetField<bool>(window, "_isVisualClockSubscribed"),
            "Todo 起身入场期间必须登记绝对帧截止点并订阅统一视觉时钟");
        Assert(ordinaryTodoStartIndex == 27 &&
               requestedTodoEntryFrameInfo.Name.EndsWith(
                   "luban-wake-27.png",
                   StringComparison.Ordinal),
            "普通动作抢占后的Todo入场首帧必须是wake27；" +
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

        PumpDispatcher(TimeSpan.FromMilliseconds(1950));
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
        window.Left = workArea.Left;
        window.Top = Math.Clamp(
            window.Top,
            workArea.Top,
            workArea.Bottom - window.ActualHeight);
        Invoke(window, "UpdateEdgeDockAfterDrag");
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        Assert(GetField<object>(window, "_edgeDock").ToString() == "None",
            "Todo 打开时拖到屏幕边缘不得启动探头状态");
        Assert(GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0,
            "Todo 打开时边缘探头绝对时间截止点必须保持清空");
        Assert(Equals(GetField<object>(window, "_currentSpriteFrame"), todoFrameObject),
            "Todo 打开时拖到屏幕边缘仍应保持专用思考姿势");

        SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "Left"));
        var inFlightEdgeTimestamp = Stopwatch.GetTimestamp();
        SetField(window, "_edgePeekFrameDeadlineTimestamp", inFlightEdgeTimestamp);
        Invoke(window, "AdvanceEdgePeek", inFlightEdgeTimestamp);
        Assert(GetField<object>(window, "_edgeDock").ToString() == "None" &&
               GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0,
            "即使有在途边缘 Tick，Todo 也必须防御性清理探头状态");
        Assert(Equals(GetField<object>(window, "_currentSpriteFrame"), todoFrameObject),
            "清理在途边缘 Tick 后必须恢复 Todo 专用姿势");

        todoWindow.Close();
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        Assert(!todoWindow.IsVisible,
            "Alt+F4/系统关闭待办窗口时应取消销毁并安全隐藏");
        Assert(GetField<object>(window, "_bubbleMode").ToString() == "None",
            "Alt+F4 收起后 MainWindow 的 BubbleMode 必须同步为 None");
        Assert(GetProperty<string>(GetRawField(window, "_activeClip")!, "ActionName") == "todo-close",
            "收起 Todo 应启动专用平滑回待机过渡");
        Assert(GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0,
            "Todo 收起过渡与边缘探头绝对时间轴不得并行写画面");

        Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        Assert(todoWindow.IsVisible,
            "Alt+F4 收起后应能再次复用同一个 TodoWindow 成功打开");
        Assert(ReferenceEquals(todoWindow.Owner, window),
            "重新打开后 Owned Window 关系必须保持");
        Assert(!GetField<Popup>(window, "BubblePopup").IsOpen,
            "重新打开 TodoWindow 时旧 BubblePopup 仍不得显示");

        Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "None"));
        PumpDispatcher(TimeSpan.FromMilliseconds(20));
        Assert(!todoWindow.IsVisible, "收起 Todo 模式应隐藏而非销毁独立待办窗口");
        Assert(GetProperty<string>(GetRawField(window, "_activeClip")!, "ActionName") == "todo-close",
            "右键或外部点击收起 Todo 都应播放同一段平滑过渡");
        PumpDispatcher(TimeSpan.FromMilliseconds(1900));
        Assert(GetRawField(window, "_activeClip") is null,
            "Todo 收起过渡完成后必须清理活动动作");
        }
        finally
        {
            SetField(window, "_suppressTodoWindowDeactivate", false);
        }
    }

    private static void AssertTodoWindowLayoutApiAndIme()
    {
        var type = typeof(TodoWindow);
        foreach (var propertyName in new[] { "Todos", "IsImeComposing" })
        {
            Assert(type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {propertyName} 属性");
        }

        foreach (var methodName in new[] { "FocusInput", "SetPetSizeScale" })
        {
            Assert(type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {methodName} 方法");
        }

        foreach (var eventName in new[]
                 {
                     "AddRequested",
                     "TodoChanged",
                     "DeleteRequested",
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
            AssertClose(todoWindow.Height, 350, "TodoWindow 总高度");
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
                       "textBox is { IsReadOnly: true, DataContext: TodoItem }",
                       StringComparison.Ordinal) &&
                   getCopyTextSource.Contains("? textBox.SelectedText", StringComparison.Ordinal) &&
                   getCopyTextSource.Contains(": null", StringComparison.Ordinal),
                "Ctrl+C 取文契约必须是：输入框无选区复制全文、有选区复制选区；列表只读文字仅复制选区");
            Assert(queueSizeSource.Contains(
                       "if (_petSizeScaleNotificationQueued)",
                       StringComparison.Ordinal) &&
                   queueSizeSource.Contains(
                       "DispatcherPriority.Background",
                       StringComparison.Ordinal) &&
                   !queueSizeSource.Contains(
                       "DispatcherPriority.Render",
                       StringComparison.Ordinal) &&
                   queueSizeSource.Contains(
                       "_flushPetSizeScaleChangedAction",
                       StringComparison.Ordinal) &&
                   queueSizeSource.Contains(
                       "_petSizeScaleNotificationQueued = false",
                       StringComparison.Ordinal) &&
                   queueSizeSource.Contains(
                       "PetSizeScaleChanged?.Invoke(_pendingPetSizeScale)",
                       StringComparison.Ordinal),
                "滑块手势必须在 Background 合并通知并只发布最新尺寸，不能让 Render 优先级抢占连续输入");

            todoWindow.Todos = new ObservableCollection<TodoItem>(
                Enumerable.Range(1, 12)
                    .Select(index => new TodoItem { Text = $"待办 {index}" }));
            todoWindow.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));

            var scrollViewer = FindVisualDescendant<ScrollViewer>(todoWindow)
                ?? throw new InvalidOperationException("TodoWindow 找不到待办滚动区域");
            var itemsControl = GetField<ItemsControl>(todoWindow, "TodoItemsControl");
            var sizeSlider = GetField<Slider>(todoWindow, "PetSizeSlider");
            var sizeLabel = GetField<TextBlock>(todoWindow, "PetSizeLabel");
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
            var inputBurstFrame = new DispatcherFrame();
            for (var inputIndex = 0; inputIndex < 240; inputIndex++)
            {
                var scheduledIndex = inputIndex;
                todoWindow.Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() =>
                    {
                        sizeSlider.Value = scheduledIndex == 239
                            ? 123.4
                            : 75.1 + scheduledIndex * (48.2 / 238d);
                        if (scheduledIndex == 239)
                        {
                            todoWindow.Dispatcher.BeginInvoke(
                                DispatcherPriority.Background,
                                new Action(() => inputBurstFrame.Continue = false));
                        }
                    }));
            }

            Dispatcher.PushFrame(inputBurstFrame);
            Assert(sizeEventCount == 1,
                "DispatcherPriority.Input 连续240次滑块输入泵到 Background 后必须只发布一次最新尺寸");
            AssertClose(sizeEventValue, 1.234,
                "DispatcherPriority.Input 输入合并必须发布最终123.4%而不是中间值");
            Invoke(todoWindow, "EndPetSizeAdjustment");
            Assert(sizeEventCount == 1,
                "松开滑块不得补播已经在 Background 发布过的积压尺寸事件");
            Assert(adjustmentStartedCount == 1 && adjustmentCompletedCount == 1,
                "尺寸滑块必须明确发出按下与松开手势边界");
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
                   longItemTextBox.IsTabStop == false &&
                   longItemTextBox.Focusable &&
                   longItemTextBox.Cursor == Cursors.IBeam &&
                   longItemTextBox.TextWrapping == TextWrapping.Wrap &&
                   longItemTextBox.MaxLines == 2 &&
                   longItemTextBox.MaxHeight <= 36.5 &&
                   longItemTextBox.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden &&
                   longItemTextBox.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                "列表文字必须是可鼠标选择的无边框只读 TextBox，并限制为两行换行显示");
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
        }
        finally
        {
            todoWindow.CloseForApplication();
        }
    }

    private static KeyEventArgs CreateEnterKeyEvent(PresentationSource source) => new(
        Keyboard.PrimaryDevice,
        source,
        Environment.TickCount,
        Key.Enter)
    {
        RoutedEvent = Keyboard.PreviewKeyDownEvent
    };

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
                   !GetField<bool>(window, "_petSizeEnvelopePrepared") &&
                   GetField<bool>(window, "_petSizeEnvelopePreparationPending"),
                "滑块按下只能建立预览状态并排队包络，不能同步执行原生窗口布局");
            Assert(Math.Abs(window.Left - originalLeft) < 0.000001 &&
                   Math.Abs(window.Top - originalTop) < 0.000001 &&
                   Math.Abs(window.Width - originalWidth) < 0.000001 &&
                   Math.Abs(window.Height - originalHeight) < 0.000001,
                "滑块按下事件内不得同步改写窗口位置或尺寸");

            Invoke(window, "AdvancePetSizeCompositionFrame", Stopwatch.GetTimestamp());
            Assert(GetField<bool>(window, "_petSizeEnvelopePrepared") &&
                   !GetField<bool>(window, "_petSizeEnvelopePreparationPending"),
                "最大透明包络必须延迟到下一次Composition Rendering一次性准备");
            Assert(Math.Abs(window.Width - 266) <= 0.5 &&
                   Math.Abs(window.Height - 338.8) <= 0.5,
                "合成帧必须一次性准备最大透明包络");
            Console.WriteLine(
                $"[METRIC] pet-size gesture-start={gestureStartElapsed.TotalMilliseconds:F3}ms; " +
                "native envelope deferred to composition");

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
        Assert(!GetField<bool>(window, "_petSizeEnvelopePrepared") &&
               Math.Abs(window.Width - 190) <= 0.5 &&
               Math.Abs(window.Height - 242) <= 0.5,
            "尺寸目标更新本身不得同步执行最大包络布局，布局只能在手势开始或合成帧准备");
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
                var sizeChangedCount = 0;
                var locationChangedCount = 0;
                SizeChangedEventHandler sizeChanged = (_, _) => sizeChangedCount++;
                EventHandler locationChanged = (_, _) => locationChangedCount++;
                window.SizeChanged += sizeChanged;
                window.LocationChanged += locationChanged;
                var gestureStartedAt = Stopwatch.GetTimestamp();
                Invoke(window, "TodoWindow_PetSizeAdjustmentStarted");
                // This loop drives a synthetic composition clock. A visible
                // WPF window can otherwise receive a real Rendering callback
                // between two synthetic samples and inflate the write count.
                Invoke(window, "StopVisualClock");
                var gestureStartElapsed = Stopwatch.GetElapsedTime(gestureStartedAt);
                window.SizeChanged -= sizeChanged;
                window.LocationChanged -= locationChanged;
                Assert(sizeChangedCount == 0 && locationChangedCount == 0 &&
                       !GetField<bool>(window, "_petSizeEnvelopePrepared") &&
                       GetField<bool>(window, "_petSizeEnvelopePreparationPending"),
                    $"{refreshRate}Hz手势按下不得同步触发原生窗口resize或位置布局");

                var initialLeft = window.Left;
                var initialTop = window.Top;
                var initialWidth = window.Width;
                var initialHeight = window.Height;
                var controlledStart = Stopwatch.GetTimestamp();
                var inputIndex = 1;
                var renderIndex = 1;
                var renderFrames = 0;
                var framesWithScaleChange = 0;
                var firstChangedRender = 0;
                var envelopeCaptured = false;
                var envelopeLeft = 0d;
                var envelopeTop = 0d;
                var envelopeWidth = 0d;
                var envelopeHeight = 0d;
                var baseViewboxWidth = 0d;
                var baseViewboxHeight = 0d;

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
                        if (!envelopeCaptured)
                        {
                            Assert(Math.Abs(window.Left - initialLeft) < 0.000001 &&
                                   Math.Abs(window.Top - initialTop) < 0.000001 &&
                                   Math.Abs(window.Width - initialWidth) < 0.000001 &&
                                   Math.Abs(window.Height - initialHeight) < 0.000001,
                                $"{refreshRate}Hz首次Rendering前的输入不得同步布局");
                        }
                        else
                        {
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
                        }

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

                    if (!envelopeCaptured)
                    {
                        Assert(GetField<bool>(window, "_petSizeEnvelopePrepared") &&
                               !GetField<bool>(window, "_petSizeEnvelopePreparationPending"),
                            $"{refreshRate}Hz首次Composition必须完成最大包络准备");
                        envelopeLeft = window.Left;
                        envelopeTop = window.Top;
                        envelopeWidth = window.Width;
                        envelopeHeight = window.Height;
                        baseViewboxWidth = viewbox.Width;
                        baseViewboxHeight = viewbox.Height;
                        envelopeCaptured = true;
                    }
                    else
                    {
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
                    }

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
            var transitionCompletedAt = transitionStartedAt + (long)Math.Ceiling(
                transitionDuration.TotalSeconds * Stopwatch.Frequency);
            Invoke(window, "QueuePetSizeScaleTargetAt", 1.273d, transitionStartedAt);
            Invoke(
                window,
                "AdvancePetSizeCompositionFrame",
                transitionCompletedAt);
            window.UpdateLayout();
            var finalPreviewBounds = GetVisualPhysicalBounds(viewbox);
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

            var completedAt = transitionCompletedAt + 1;
            Invoke(window, "CompletePetSizeAdjustmentAt", completedAt);
            AssertClose(todoWindow.Left, frozenTodoLeft,
                "Complete 回调内不得同步移动 TodoWindow.Left");
            AssertClose(todoWindow.Top, frozenTodoTop,
                "Complete 回调内不得同步移动 TodoWindow.Top");
            Assert(GetField<bool>(window, "_petSizeTodoPositionNeedsUpdate"),
                "Complete 后下一合成帧前必须保留 Todo 跟随 dirty");

            Invoke(window, "AdvancePetSizeCompositionFrame", completedAt + 1);
            window.UpdateLayout();
            Assert(!GetField<bool>(window, "_petSizeTodoPositionNeedsUpdate"),
                "Complete 后下一 Composition 帧必须消费 Todo 跟随 dirty");
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
    }

    private static void AssertRuntimeJankSourceContract()
    {
        var mainSource = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        var rendering = ExtractPrivateMethodSource(mainSource, "VisualClock_Rendering");
        var showStableFrame = ExtractPrivateMethodSource(mainSource, "ShowStableFrame");
        var discardSupersededPending = ExtractPrivateMethodSource(
            mainSource,
            "DiscardSupersededPendingSpriteFrame");
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
            ExtractPrivateMethodSource(mainSource, "AdvancePetSizeCompositionFrame"),
            ExtractPrivateMethodSource(mainSource, "AdvancePetSizeTransition"),
            ExtractPrivateMethodSource(mainSource, "ApplyPetSizePreviewScale"),
            ExtractPrivateMethodSource(mainSource, "CopyFramePixels"),
            ExtractPrivateMethodSource(mainSource, "WriteDisplayFrame")
        };

        Assert(mainSource.Contains("_spritePagePrefetchPixels", StringComparison.Ordinal) &&
               mainSource.Contains("Task.Run(", StringComparison.Ordinal) &&
               mainSource.Contains("_spritePagePrefetchGeneration", StringComparison.Ordinal) &&
               mainSource.Contains("CancellationTokenSource", StringComparison.Ordinal) &&
               mainSource.Contains("TryPromotePrefetchedSpritePage", StringComparison.Ordinal),
            "运行时分页必须使用固定双缓冲、后台任务、代际取消和UI线程原子交换");
        Assert(!rendering.Contains("LoadSpritePage", StringComparison.Ordinal) &&
               !rendering.Contains("DecodeLz4", StringComparison.Ordinal) &&
               !rendering.Contains("GetResourceStream", StringComparison.Ordinal) &&
               !rendering.Contains("AppLogger", StringComparison.Ordinal) &&
               !showStableFrame.Contains("LoadSpritePageIntoBuffer", StringComparison.Ordinal) &&
               showStableFrame.Contains("_pendingSpriteFrame", StringComparison.Ordinal),
            "Rendering/ShowStableFrame不得同步读取或解压；未就绪必须保持旧帧并记录最新目标帧");
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
                   !method.Contains("Func<", StringComparison.Ordinal)) &&
               !mainSource.Contains(
                   "ScheduleUnusedSpritePageCollection",
                   StringComparison.Ordinal) &&
               !mainSource.Contains("GC.Collect", StringComparison.Ordinal),
            "Rendering 的动作/边缘/Todo结束调用链不得创建捕获委托，也不得按动作调度手工GC");
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
                   "_spritePagePrefetchDispatchTimer.Stop()",
                   StringComparison.Ordinal),
            "较新的热页帧必须淘汰旧pending并换代取消旧冷页请求，禁止完成回调闪回过时姿态");
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
        Assert(todoSource.Contains("_flushPetSizeScaleChangedAction", StringComparison.Ordinal) &&
               todoSource.Contains("_resetImeCompositionAfterFocusLossAction", StringComparison.Ordinal) &&
               todoSource.Contains("_focusInputAction", StringComparison.Ordinal) &&
               todoSource.Contains("_retryClipboardCopyAction", StringComparison.Ordinal) &&
               todoSource.Contains("Clipboard.SetDataObject(text, true)", StringComparison.Ordinal) &&
               todoSource.Contains("catch (ExternalException)", StringComparison.Ordinal) &&
               todoSource.Contains("if (IsKeyboardFocusWithin)", StringComparison.Ordinal) &&
               todoSource.Contains("if (!IsVisible || _hasClosed)", StringComparison.Ordinal),
            "TodoWindow高频/剪贴板重试必须复用委托，延迟聚焦不得在收起后复活或抢走选区");

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
            "cache._lastLeft == desiredPosition.X",
            StringComparison.Ordinal);
        var setWindowPos = positionerSource.IndexOf(
            "var positioned = SetWindowPos(",
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
               positionerSource.Contains("if (monitorChanged)", StringComparison.Ordinal) &&
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
        Assert(!prepareEnvelope.Contains("QueueTodoWindowPositionUpdate", StringComparison.Ordinal),
            "尺寸预览首帧必须由当前合成回调完成一次待办定位，不得再排队第二次中间布局定位");
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

            var lineStart = source.LastIndexOf('\n', candidate);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            var lineEnd = source.IndexOf('\n', candidate);
            lineEnd = lineEnd < 0 ? source.Length : lineEnd;
            if (source[lineStart..lineEnd].Contains("private ", StringComparison.Ordinal))
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
        var timer = new DispatcherTimer(DispatcherPriority.Background)
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
        SetField(window, "_prefetchedSpritePageName", null);
        SetField(window, "_prefetchedSpritePageStride", 0);
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
               string.Equals(
                   GetRawField(window, "_prefetchedSpritePageName") as string,
                   expectedPageName,
                   StringComparison.Ordinal),
            $"预测预取必须准备目标分页：{expectedPageName}");
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
        string PreviewResourcePath,
        int Width,
        int Height,
        IDictionary Frames,
        object RuntimeValue);

}
