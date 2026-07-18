using System.Collections;
using System.Collections.ObjectModel;
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
    private const int WriggleFrameCount = 48;
    private const int WriggleCornerFrameCount = 48;
    private const int WriggleCornerDurationMilliseconds = 800;
    private const int WriggleCornerFacingSwitchFrameNumber = 43;
    private const int ExpectedSpritePageCount = 12;
    private const int ExpectedSourceFrameCount =
        1 + 14 + (3 * 4) + (7 * 24) +
        (WriggleFrameCount * 3) + WriggleCornerFrameCount;
    private const int ExpectedPageFrameCount =
        1 + (7 * (1 + 14 + 24)) + (1 + (3 * 4)) +
        (1 + WriggleFrameCount) + (1 + (WriggleFrameCount * 2)) +
        (1 + WriggleCornerFrameCount);
    private const long MaximumDecodedSpritePageBytes = 24L * 1024L * 1024L;
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags StaticFlags =
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [STAThread]
    private static void Main()
    {
        _ = new Application();
        AssertLoggingContract();

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

            RunCheck(nameof(AssertDisplayFrameContract), () => AssertDisplayFrameContract(window));
            RunCheck(nameof(AssertHighDensityScalingAndDpiContract), () => AssertHighDensityScalingAndDpiContract(window));
            RunCheck(nameof(AssertRoamAssetSequenceContract), () => AssertRoamAssetSequenceContract(window));
            RunCheck(nameof(AssertRoamVisualTransitionContract), () => AssertRoamVisualTransitionContract(window));
            RunCheck(nameof(AssertMotionTimelineContract), () => AssertMotionTimelineContract(window));
            RunCheck(nameof(AssertNoRunContract), () => AssertNoRunContract(window));
            RunCheck(nameof(AssertAbsoluteTimelineMathContract), () => AssertAbsoluteTimelineMathContract(window));
            RunCheck(nameof(AssertExactEdgeContactContract), AssertExactEdgeContactContract);
            RunCheck(nameof(AssertManualTopDockIntegration), () => AssertManualTopDockIntegration(window));
            RunCheck(nameof(AssertRoamPerimeterAndFullLap), () => AssertRoamPerimeterAndFullLap(window));
            RunCheck(nameof(AssertUserInterruptedRoamIsRescheduled), () => AssertUserInterruptedRoamIsRescheduled(window));
            RunCheck(nameof(AssertPointerDownInterruptsRoam), () => AssertPointerDownInterruptsRoam(window));
            RunCheck(nameof(AssertRandomActivityBag), () => AssertRandomActivityBag(window));
            RunCheck(nameof(AssertMonitorWorkAreaContract), () => AssertMonitorWorkAreaContract(window));
            RunCheck(nameof(AssertDisplaySettingsChangeRecovery), () => AssertDisplaySettingsChangeRecovery(window));
            RunCheck(nameof(AssertOwnedTodoWindowContract), () => AssertOwnedTodoWindowContract(window));
            RunCheck(nameof(AssertTodoWindowLayoutApiAndIme), AssertTodoWindowLayoutApiAndIme);
            RunCheck(nameof(AssertPetSizeScaleContract), () => AssertPetSizeScaleContract(window));
            RunCheck(nameof(AssertEnableRoamBecomesDueImmediately), () => AssertEnableRoamBecomesDueImmediately(window));
            RunCheck(nameof(AssertMotionAssetScaleContract), AssertMotionAssetScaleContract);
            RunCheck(nameof(AssertWriggleAssetContinuityContract), AssertWriggleAssetContinuityContract);
            RunCheck(nameof(AssertSpriteAtlasReproducibilityContract), AssertSpriteAtlasReproducibilityContract);
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

        Assert(pageMap.Count == ExpectedSpritePageCount,
            $"运行时必须登记{ExpectedSpritePageCount}个图集分页，实际 {pageMap.Count}");
        var maximumPageWidth = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Width"));
        var maximumPageHeight = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Height"));
        var maximumDecodedPageBytes = pageMap.Values.Cast<object>()
            .Max(page => checked(
                (long)GetProperty<int>(page, "Width") *
                GetProperty<int>(page, "Height") * 4));
        var reusablePageBufferBytes = spritePagePixels.LongLength;
        Assert(maximumDecodedPageBytes <= MaximumDecodedSpritePageBytes &&
               reusablePageBufferBytes == maximumDecodedPageBytes,
            $"高密度分页及复用缓冲均不得超过24MiB，实际最大页 " +
            $"{maximumDecodedPageBytes / 1024d / 1024d:F2}MiB，复用缓冲 " +
            $"{reusablePageBufferBytes / 1024d / 1024d:F2}MiB");
        var bitmapFields = typeof(MainWindow).GetFields(InstanceFlags)
                .Select(field => field.GetValue(window))
                .OfType<BitmapSource>()
                .ToArray();
        Assert(bitmapFields.Length == 1 &&
               bitmapFields.Contains(displayFrameBuffer),
            $"MainWindow只能常驻一个{RenderPixelWidth}×{RenderPixelHeight}" +
            "高密度显示位图；分页必须使用单个紧凑Pbgra32像素数组，" +
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
               window.FindName("PetImageOverlay") is null &&
               window.FindName("PetRoamTransitionImage") is null,
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

        Assert(totalPageFrames == ExpectedPageFrameCount,
            $"{ExpectedSpritePageCount}个分页应共包含{ExpectedPageFrameCount}个PageFrame，" +
            $"实际 {totalPageFrames}");
        AssertSameFrameReturnsEarly(
            window,
            petImage,
            spriteBrush,
            pages[0].Frames.Values.Cast<object>().First());
        AssertSingleBufferPremultipliedBlendContract(window);
        AssertCompressedPageLoadPerformance(window, pages);
        Invoke(window, "ShowStableFrame", GetField<object>(window, "_idleFrame"));
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
            // 后续绕屏契约以默认逻辑尺寸为基线；测试不得受用户本机已保存的
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
            "右侧探头或反向绕屏进入Todo前必须把镜像后的实际画面烘焙进单buffer");

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
            "绕屏偏移进入Todo前必须被烘焙并按窗口边界透明裁剪，不能先跳回中心");

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
            "单buffer坐标换基必须同时正确处理镜像与绕屏平移");
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
        Assert(root.GetProperty("sourceFrameCount").GetInt32() == ExpectedSourceFrameCount,
            $"分页清单sourceFrameCount必须为{ExpectedSourceFrameCount}");
        Assert(root.GetProperty("pageFrameCount").GetInt32() == ExpectedPageFrameCount,
            $"分页清单pageFrameCount必须为{ExpectedPageFrameCount}");

        var manifestPages = root.GetProperty("pages");
        Assert(manifestPages.EnumerateObject().Count() == ExpectedSpritePageCount &&
               pages.Length == ExpectedSpritePageCount,
            $"清单与运行时都必须恰好包含{ExpectedSpritePageCount}页");
        var expectedWrigglePageCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["roam-wriggle-horizontal"] = 1 + WriggleFrameCount,
            ["roam-wriggle-vertical"] = 1 + (WriggleFrameCount * 2),
            ["roam-wriggle-corner"] = 1 + WriggleCornerFrameCount
        };
        foreach (var (pageName, expectedFrameCount) in expectedWrigglePageCounts)
        {
            Assert(manifestPages.TryGetProperty(pageName, out var wrigglePage) &&
                   wrigglePage.GetProperty("logicalFrameCount").GetInt32() == expectedFrameCount,
                $"{pageName} 必须包含idle和{expectedFrameCount - 1}个对应蠕动帧");
        }
        Assert(!manifestPages.TryGetProperty("roam-wriggle", out _),
            "48帧蠕动不得继续挤在旧roam-wriggle单页中");
        Assert(!manifestPages.TryGetProperty("roam-crawl", out _) &&
               !manifestPages.TryGetProperty("roam-hop", out _),
            "分页清单不得保留已取消的绕屏爬行或跳跃分页");
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
            var logicalCount = descriptor.GetProperty("logicalFrameCount").GetInt32();
            var uniqueCount = descriptor.GetProperty("uniqueSpriteCount").GetInt32();
            var manifestFrames = descriptor.GetProperty("frames");

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
            Assert(runtimePage.Frames.Count == logicalCount &&
                   manifestFrames.EnumerateObject().Count() == logicalCount,
                $"分页帧数必须与清单一致：{manifestPageEntry.Name}");
            totalPageFrames += logicalCount;
            _ = pageResources.Add(resource);
            _ = previewResources.Add(previewResource);

            var compressedPath = FindWorkspaceFile(resource.Split('/'));
            Assert(new FileInfo(compressedPath).Length > 0,
                $"分页LZ4资源不得为空：{manifestPageEntry.Name}");
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

        Assert(totalPageFrames == ExpectedPageFrameCount &&
               sourceFrames.Count == ExpectedSourceFrameCount &&
               pageResources.Count == ExpectedSpritePageCount &&
               previewResources.Count == ExpectedSpritePageCount,
            $"{ExpectedSpritePageCount}页必须覆盖{ExpectedPageFrameCount}个PageFrame和" +
            $"{ExpectedSourceFrameCount}个源逻辑帧");
        AssertProjectAndAssemblyResourceContract(pageResources, previewResources);
        AssertRuntimeDoesNotUseWpfBitmapDecoders();
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
            $"csproj只能嵌入{ExpectedSpritePageCount}个无损LZ4分页通配符和v3 manifest");
        Assert(!includes.Any(include =>
                include.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                include.EndsWith("*.png", StringComparison.OrdinalIgnoreCase)),
            $"csproj不得嵌入分页预览PNG、{ExpectedSourceFrameCount}张源PNG或旧单atlas");

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
               assetKeys.Count == ExpectedSpritePageCount + 1,
            $"主程序集Assets资源必须严格等于{ExpectedSpritePageCount}个LZ4分页和一个v3 manifest");
        Assert(!assetKeys.Any(key =>
                key.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
            "主程序集不得包含分页预览PNG、旧单atlas或源PNG");
        Assert(!assetKeys.Any(key =>
                key.Contains("roam-crawl", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("roam-hop", StringComparison.OrdinalIgnoreCase)),
            "主程序集的WPF资源表不得嵌入已取消的绕屏爬行或跳跃分页");
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

    private static void AssertSpriteAtlasReproducibilityContract()
    {
        var buildScript = FindWorkspaceFile("tools", "build_sprite_atlas.py");
        var workspaceRoot = Directory.GetParent(Path.GetDirectoryName(buildScript)!)?.FullName
            ?? throw new InvalidOperationException("无法定位图集构建脚本所在的工作区");
        var probeRoot = Path.Combine(
            workspaceRoot,
            ".codex_tmp",
            $"xlb-pet-atlas-check-{Guid.NewGuid():N}");
        var generatedPages = Path.Combine(probeRoot, "sprite-pages");
        var generatedManifest = Path.Combine(probeRoot, "luban-sprite-pages.json");
        Directory.CreateDirectory(probeRoot);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                WorkingDirectory = workspaceRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(buildScript);
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(workspaceRoot);
            startInfo.ArgumentList.Add("--output-dir");
            startInfo.ArgumentList.Add(generatedPages);
            startInfo.ArgumentList.Add("--manifest");
            startInfo.ArgumentList.Add(generatedManifest);

            using var process = new Process { StartInfo = startInfo };
            Assert(process.Start(), "必须能启动可重复图集构建检查");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("图集可重复构建检查在120秒内未完成");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            Assert(process.ExitCode == 0,
                $"图集构建脚本失败（exit={process.ExitCode}）：{stderr}\n{stdout}");

            var committedManifest = FindWorkspaceFile("Assets", "luban-sprite-pages.json");
            AssertGeneratedManifestMatches(generatedManifest, committedManifest);

            var committedPages = Path.Combine(workspaceRoot, "Assets", "sprite-pages");
            var generatedNames = Directory.GetFiles(generatedPages)
                .Where(path =>
                    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".pbgra.lz4", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var committedNames = Directory.GetFiles(committedPages)
                .Where(path =>
                    path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".pbgra.lz4", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert(generatedNames.Count(name =>
                       name!.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ==
                   ExpectedSpritePageCount &&
                   generatedNames.Count(name =>
                       name!.EndsWith(".pbgra.lz4", StringComparison.OrdinalIgnoreCase)) ==
                   ExpectedSpritePageCount,
                $"可重复构建必须生成{ExpectedSpritePageCount}个预览PNG和" +
                $"{ExpectedSpritePageCount}个无损LZ4分页");
            Assert(generatedNames.SequenceEqual(committedNames, StringComparer.Ordinal),
                "提交的PNG/LZ4图集分页文件集合必须与可重复构建结果完全一致");
            foreach (var pageName in generatedNames)
            {
                Assert(FileHashesMatch(
                        Path.Combine(generatedPages, pageName!),
                        Path.Combine(committedPages, pageName!)),
                    $"提交的图集分页 {pageName} 已陈旧，未包含当前源PNG内容或确定性LZ4结果");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(probeRoot, recursive: true);
            }
            catch
            {
                // 临时图集清理失败不应掩盖可重复构建契约结果。
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
                     "pageFrameCount"
                 })
        {
            Assert(generated.GetProperty(property).GetInt32() ==
                   committed.GetProperty(property).GetInt32(),
                $"提交图集清单的 {property} 与可重复构建结果不一致");
        }

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
                         "logicalFrameCount",
                         "uniqueSpriteCount"
                     })
            {
                Assert(generatedPage.GetProperty(property).GetInt32() ==
                       committedPage.Value.GetProperty(property).GetInt32(),
                    $"分页 {committedPage.Name} 的 {property} 与可重复构建结果不一致");
            }

            Assert(generatedPage.GetProperty("frames").GetRawText() ==
                   committedPage.Value.GetProperty("frames").GetRawText(),
                $"分页 {committedPage.Name} 的帧坐标与当前源PNG不一致");
        }
    }

    private static void AssertRoamAssetSequenceContract(MainWindow window)
    {
        foreach (var fieldName in new[]
                 {
                     "_roamHorizontalFrames",
                     "_roamVerticalUpFrames",
                     "_roamVerticalDownFrames"
                 })
        {
            var modes = GetField<Array>(window, fieldName);
            Assert(modes.Length == 1,
                $"{fieldName} 必须只包含一组蠕动序列，不得再登记爬行或跳跃模式");
            var directionName = fieldName switch
            {
                "_roamHorizontalFrames" => "horizontal",
                "_roamVerticalUpFrames" => "vertical-up",
                "_roamVerticalDownFrames" => "vertical-down",
                _ => throw new InvalidOperationException($"未知绕屏序列：{fieldName}")
            };
            var sequence = modes.GetValue(0) as Array
                ?? throw new InvalidOperationException($"{fieldName}[0] 不是帧序列");
            Assert(sequence.Length == WriggleFrameCount,
                $"{fieldName} 的蠕动序列必须包含 {WriggleFrameCount} 个图集 FrameRef");
            for (var frameIndex = 0; frameIndex < sequence.Length; frameIndex++)
            {
                var frame = GetSpriteFrameInfo(sequence.GetValue(frameIndex)!);
                var expectedDirection = directionName == "vertical-down"
                    ? "vertical-up"
                    : directionName;
                var expectedFrameNumber = directionName == "vertical-down"
                    ? WriggleFrameCount - frameIndex
                    : frameIndex + 1;
                var expectedName =
                    $"Assets/luban-roam-wriggle-{expectedDirection}-{expectedFrameNumber:00}.png";
                Assert(frame.Name == expectedName,
                    $"{fieldName}[0][{frameIndex}] 资源顺序不正确：{frame.Name}");
                var expectedPageName = directionName == "horizontal"
                    ? "roam-wriggle-horizontal"
                    : "roam-wriggle-vertical";
                Assert(frame.PageName == expectedPageName,
                    $"{expectedName} 必须位于 {expectedPageName} 分页");
                Assert(frame.Width > 0 && frame.Height > 0,
                    $"{frame.Name} 必须指向有效的紧凑图集区域");
            }
        }

        var spritePages = GetField<IDictionary>(window, "_spritePages");
        var wriggleCornerPage = GetDictionaryEntries(spritePages)
            .Single(entry => string.Equals(
                entry.Key as string,
                "roam-wriggle-corner",
                StringComparison.Ordinal));
        var wriggleCornerFrames = GetProperty<IDictionary>(wriggleCornerPage.Value!, "Frames");
        for (var frameIndex = 1; frameIndex <= WriggleCornerFrameCount; frameIndex++)
        {
            var expectedName = $"Assets/luban-roam-wriggle-corner-{frameIndex:00}.png";
            Assert(wriggleCornerFrames.Contains(expectedName),
                $"roam-wriggle-corner 图集必须登记转角衔接帧：{expectedName}");
        }

        var cornerFrames = GetField<Array>(window, "_roamWriggleCornerFrames");
        Assert(cornerFrames.Length == WriggleCornerFrameCount,
            $"运行时必须登记{WriggleCornerFrameCount}帧蠕动横向/竖向转角衔接序列");
        for (var frameIndex = 0; frameIndex < cornerFrames.Length; frameIndex++)
        {
            var frame = GetSpriteFrameInfo(cornerFrames.GetValue(frameIndex)!);
            Assert(frame.Name == $"Assets/luban-roam-wriggle-corner-{frameIndex + 1:00}.png" &&
                   frame.PageName == "roam-wriggle-corner",
                $"蠕动转角第{frameIndex + 1}帧必须按顺序来自 roam-wriggle-corner 分页");
        }
    }

    private static void AssertRoamVisualTransitionContract(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
        }

        var petImage = GetField<Rectangle>(window, "PetImage");
        var petVisual = GetField<Grid>(window, "PetVisual");
        var spriteBrush = GetField<ImageBrush>(window, "PetSpriteBrush");
        var displayFrameBuffer = GetField<WriteableBitmap>(window, "_displayFrameBuffer");
        SetField(window, "_isEdgeRoaming", true);
        SetField(window, "_edgeRoamingEnabled", true);
        SetField(window, "_roamApproaching", false);
        SetField(window, "_roamClockwise", true);
        SetField(window, "_roamMode", GetNestedEnum("RoamMode", "Wriggle"));
        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Top"));
        SetField(window, "_roamVisualEdge", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_roamVisualDirection",
            GetNestedEnum("RoamVisualDirection", "None"));
        SetField(window, "_roamElapsed", TimeSpan.Zero);
        SetField(window, "_roamVisualTransitionEndsAt", TimeSpan.Zero);

        Invoke(window, "UpdateRoamVisual");
        var horizontalFrame = GetSpriteFrameInfo(
            GetField<object>(window, "_currentSpriteFrame"));
        Assert(horizontalFrame.Name.EndsWith("horizontal-01.png", StringComparison.Ordinal),
            "绕屏首次进入横边时必须从接触相位第1帧开始");
        Assert(GetField<bool>(window, "_isFrameBlending") &&
               GetField<TimeSpan>(window, "_activeFrameBlendDuration") ==
               TimeSpan.FromMilliseconds(120),
            "首次进入绕屏必须在唯一完整帧buffer内执行一次120ms预乘Alpha淡化");
        Assert(ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer) &&
               petImage.Opacity == 1 &&
               !DependencyPropertyHelper.GetValueSource(
                   petImage,
                   UIElement.OpacityProperty).IsAnimated,
            "绕屏淡化不得创建叠层或动画PetImage.Opacity");
        Assert(petVisual.Opacity == 1 &&
               !DependencyPropertyHelper.GetValueSource(
                   petVisual,
                   UIElement.OpacityProperty).IsAnimated,
            "绕屏开始和拐角不得把整个人淡到透明，否则会被感知为闪烁");
        Assert(GetField<TimeSpan>(window, "_roamVisualPhaseStartedAt") == TimeSpan.Zero &&
               GetField<TimeSpan>(window, "_roamVisualTransitionEndsAt") ==
               TimeSpan.FromMilliseconds(120),
             "新绕屏方向必须持有接触首帧直到120ms状态切换完成");
        Invoke(window, "StopFrameBlend", true);
        AssertRoamTransitionMovementPause(window);

        var facing = GetField<ScaleTransform>(window, "PetFacingScale");
        AssertClose(facing.ScaleX, 1, "顶部顺时针向右移动时的朝向");
        SetField(window, "_roamElapsed", TimeSpan.FromMilliseconds(200));
        SetField(window, "_roamVisualTravelDistance", 1d);
        Invoke(window, "UpdateRoamVisual");
        var secondHorizontalFrame = GetSpriteFrameInfo(
            GetField<object>(window, "_currentSpriteFrame"));
        Assert(secondHorizontalFrame.Name.EndsWith("horizontal-02.png", StringComparison.Ordinal) &&
               !GetField<bool>(window, "_isFrameBlending"),
            "48帧蠕动必须按每1 DIP直接前进一帧，不能进行整图交叉淡化");
        SetField(window, "_roamClockwise", false);
        Invoke(window, "UpdateRoamVisual");
        AssertClose(facing.ScaleX, -1, "顶部逆时针向左移动时的朝向");
        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Bottom"));
        SetField(window, "_roamClockwise", true);
        Invoke(window, "UpdateRoamVisual");
        AssertClose(facing.ScaleX, -1, "底部顺时针向左移动时的朝向");
        SetField(window, "_roamClockwise", false);
        Invoke(window, "UpdateRoamVisual");
        AssertClose(facing.ScaleX, 1, "底部逆时针向右移动时的朝向");
        SetField(window, "_roamClockwise", true);

        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Right"));
        Invoke(window, "UpdateRoamVisual");
        var verticalFrame = GetSpriteFrameInfo(
            GetField<object>(window, "_currentSpriteFrame"));
        Assert(verticalFrame.Name.EndsWith(
                $"vertical-up-{WriggleFrameCount:00}.png",
                StringComparison.Ordinal),
            "向下攀爬应从完整反序后的首相位开始，且人物保持正立");
        AssertClose(facing.ScaleX, -1,
            "右边缘竖向攀爬必须朝左、面向屏幕内部");
        Assert(GetField<bool>(window, "_isFrameBlending") &&
               GetField<TimeSpan>(window, "_activeFrameBlendDuration") ==
               TimeSpan.FromMilliseconds(120) &&
               ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer),
            $"横向转竖向必须继续在同一{RenderPixelWidth}×{RenderPixelHeight}" +
            "高密度buffer内执行120ms淡化");

        var offsetStartedAt =
            GetField<TimeSpan>(window, "_roamBaseOffsetTransitionStartedAt");
        Invoke(
            window,
            "UpdateRoamBaseOffsetTransition",
            offsetStartedAt + TimeSpan.FromMilliseconds(
                WriggleCornerDurationMilliseconds / 2d));
        var roamBaseOffset = GetField<TranslateTransform>(window, "PetRoamBaseOffset");
        Assert(roamBaseOffset.X > 0 && roamBaseOffset.X < 35,
            $"转角锚点必须在{WriggleCornerDurationMilliseconds}ms内逐像素移动，" +
            "不能从横边一帧跳到竖边");
        Invoke(
            window,
            "UpdateRoamBaseOffsetTransition",
            offsetStartedAt + TimeSpan.FromMilliseconds(
                WriggleCornerDurationMilliseconds));
        AssertClose(roamBaseOffset.X, 35, "右边缘平滑锚点终点 X");
        AssertClose(roamBaseOffset.Y, 0, "右边缘平滑锚点终点 Y");
        Assert(!GetField<bool>(window, "_isRoamBaseOffsetTransitioning"),
            "锚点过渡完成后必须停止更新");

        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Left"));
        Invoke(window, "UpdateRoamVisual");
        AssertClose(facing.ScaleX, 1,
            "左边缘竖向攀爬必须朝右、面向屏幕内部");

        SetField(window, "_isRoamCornerTurning", true);
        SetField(window, "_roamCornerSourceEdge", GetNestedEnum("EdgeDock", "Top"));
        SetField(window, "_roamCornerTargetEdge", GetNestedEnum("EdgeDock", "Right"));
        var cornerDuration = TimeSpan.FromMilliseconds(
            WriggleCornerDurationMilliseconds);
        for (var frameIndex = 0; frameIndex < WriggleCornerFrameCount; frameIndex++)
        {
            SetField(
                window,
                "_roamCornerTurnElapsed",
                TimeSpan.FromTicks((long)(cornerDuration.Ticks *
                    ((frameIndex + 0.5) / WriggleCornerFrameCount))));
            Invoke(window, "UpdateWriggleCornerVisual", GetField<TimeSpan>(window, "_roamElapsed"));
            var cornerFrame = GetSpriteFrameInfo(
                GetField<object>(window, "_currentSpriteFrame"));
            Assert(cornerFrame.Name.EndsWith(
                       $"wriggle-corner-{frameIndex + 1:00}.png",
                       StringComparison.Ordinal) &&
                   !GetField<bool>(window, "_isFrameBlending"),
                $"{WriggleCornerDurationMilliseconds}ms转角必须依次播放" +
                $"{WriggleCornerFrameCount}个真实姿势且不交叉淡化，" +
                $"当前第{frameIndex + 1}帧");
            AssertClose(
                facing.ScaleX,
                frameIndex + 1 < WriggleCornerFacingSwitchFrameNumber ? 1 : -1,
                $"转入右边缘第{frameIndex + 1}帧只能在Alpha轮廓最对称的" +
                $"第{WriggleCornerFacingSwitchFrameNumber}帧切换朝向");
        }

        AssertClose(facing.ScaleX, -1,
            $"转入右边缘的{WriggleCornerFrameCount}帧衔接动作必须面向屏幕内部");

        SetField(window, "_roamCornerSourceEdge", GetNestedEnum("EdgeDock", "Right"));
        SetField(window, "_roamCornerTargetEdge", GetNestedEnum("EdgeDock", "Top"));
        for (var playbackIndex = 0; playbackIndex < WriggleCornerFrameCount; playbackIndex++)
        {
            SetField(
                window,
                "_roamCornerTurnElapsed",
                TimeSpan.FromTicks((long)(cornerDuration.Ticks *
                    ((playbackIndex + 0.5) / WriggleCornerFrameCount))));
            Invoke(window, "UpdateWriggleCornerVisual", GetField<TimeSpan>(window, "_roamElapsed"));
            var authoredFrameNumber = WriggleCornerFrameCount - playbackIndex;
            var cornerFrame = GetSpriteFrameInfo(
                GetField<object>(window, "_currentSpriteFrame"));
            Assert(cornerFrame.Name.EndsWith(
                       $"wriggle-corner-{authoredFrameNumber:00}.png",
                       StringComparison.Ordinal) &&
                   !GetField<bool>(window, "_isFrameBlending"),
                "竖向转横向必须反向播放同一真实转角序列且不得淡化");
            AssertClose(
                facing.ScaleX,
                authoredFrameNumber > WriggleCornerFacingSwitchFrameNumber ? -1 : 1,
                $"反向转角只能在素材第{WriggleCornerFacingSwitchFrameNumber}帧" +
                "完成镜像方向交接");
        }

        SetField(window, "_roamCornerSourceEdge", GetNestedEnum("EdgeDock", "Top"));
        SetField(window, "_roamCornerTargetEdge", GetNestedEnum("EdgeDock", "Left"));
        Invoke(window, "UpdateWriggleCornerVisual", GetField<TimeSpan>(window, "_roamElapsed"));
        AssertClose(facing.ScaleX, 1,
            $"转入左边缘的{WriggleCornerFrameCount}帧衔接动作必须面向屏幕内部");
        SetField(window, "_isRoamCornerTurning", false);
        SetField(window, "_roamCornerSourceEdge", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_roamCornerTargetEdge", GetNestedEnum("EdgeDock", "None"));

        Invoke(
            window,
            "StopEdgeRoaming",
            "测试清理",
            false,
            false,
            true);
        AssertClose(roamBaseOffset.X, 0, "停止绕屏后的主画面锚点 X");
        AssertClose(roamBaseOffset.Y, 0, "停止绕屏后的主画面锚点 Y");
        AssertClose(facing.ScaleX, 1, "停止绕屏后的主画面朝向");
        Assert(GetField<bool>(window, "_isFrameBlending") &&
               GetField<TimeSpan>(window, "_activeFrameBlendDuration") ==
               TimeSpan.FromMilliseconds(120),
            "停止绕屏回待机也必须复用单buffer完成120ms淡化");
        Invoke(window, "StopFrameBlend", true);
    }

    private static void AssertRoamTransitionMovementPause(MainWindow window)
    {
        var originalLeft = window.Left;
        var originalTop = window.Top;
        var topEdge = GetNestedEnum("EdgeDock", "Top");
        var noEdge = GetNestedEnum("EdgeDock", "None");
        var horizontalDirection = GetNestedEnum("RoamVisualDirection", "Horizontal");
        var verticalUpDirection = GetNestedEnum("RoamVisualDirection", "VerticalUp");
        var start = new Point(originalLeft, originalTop);
        var workArea = new Rect(originalLeft - 200, originalTop, 1600, 900);

        void ResetMovementState()
        {
            window.Left = originalLeft;
            window.Top = originalTop;
            SetField(window, "_edgeDock", noEdge);
            SetField(window, "_isEdgeRoaming", true);
            SetField(window, "_edgeRoamingEnabled", true);
            SetField(window, "_roamMode", GetNestedEnum("RoamMode", "Wriggle"));
            SetField(window, "_roamEdge", topEdge);
            SetField(window, "_roamClockwise", true);
            SetField(window, "_roamWorkArea", workArea);
            SetField(window, "_roamLogicalLeft", start.X);
            SetField(window, "_roamLogicalTop", start.Y);
            SetField(window, "_roamApproachTarget", start);
            SetField(window, "_roamBoundaryStart", start);
            SetField(window, "_roamBoundaryTargetDistance", 10_000d);
            SetField(window, "_roamBoundaryTravelled", 0d);
            SetField(window, "_roamVisualTravelDistance", 0d);
            SetField(window, "_roamElapsed", TimeSpan.Zero);
            SetField(window, "_roamCornerTurnElapsed", TimeSpan.Zero);
            SetField(window, "_isRoamCornerTurning", false);
            SetField(window, "_roamCornerSourceEdge", noEdge);
            SetField(window, "_roamCornerTargetEdge", noEdge);
            SetField(window, "_roamApproaching", false);
            SetField(window, "_roamVisualDirection", horizontalDirection);
        }

        try
        {
            ResetMovementState();
            SetField(
                window,
                "_roamVisualTransitionEndsAt",
                TimeSpan.FromMilliseconds(120));
            var timestamp = Stopwatch.GetTimestamp();
            SetField(window, "_roamLastRenderingTimestamp", timestamp);
            Invoke(
                window,
                "AdvanceEdgeRoaming",
                timestamp + StopwatchTicksFromMilliseconds(100));
            AssertClose(GetField<double>(window, "_roamLogicalLeft"), start.X,
                "待机转蠕动前100ms逻辑位置必须冻结");
            AssertClose(GetField<double>(window, "_roamVisualTravelDistance"), 0,
                "待机转蠕动前100ms不得偷偷推进姿势距离");
            AssertClose(GetField<TimeSpan>(window, "_roamElapsed").TotalMilliseconds, 100,
                "冻结移动时绝对绕屏时钟仍须前进");

            Invoke(
                window,
                "AdvanceEdgeRoaming",
                timestamp + StopwatchTicksFromMilliseconds(140));
            AssertClose(GetField<double>(window, "_roamLogicalLeft"), start.X + 1.2,
                "跨过120ms截止点后只使用剩余20ms移动");
            AssertClose(GetField<double>(window, "_roamVisualTravelDistance"), 1.2,
                "跨过截止点后的姿势距离必须与实际移动一致");
            AssertClose(GetField<TimeSpan>(window, "_roamElapsed").TotalMilliseconds, 140,
                "过渡与剩余移动必须共享连续绝对时钟");

            ResetMovementState();
            SetField(
                window,
                "_roamVisualTransitionEndsAt",
                TimeSpan.FromMilliseconds(120));
            timestamp = Stopwatch.GetTimestamp();
            SetField(window, "_roamLastRenderingTimestamp", timestamp);
            Invoke(
                window,
                "AdvanceEdgeRoaming",
                timestamp + StopwatchTicksFromMilliseconds(251));
            AssertClose(GetField<double>(window, "_roamLogicalLeft"), start.X,
                "超过250ms阻塞仍必须整段丢弃移动");
            AssertClose(GetField<double>(window, "_roamVisualTravelDistance"), 0,
                "超过250ms阻塞不得补播过渡后的积压姿势");
            AssertClose(GetField<TimeSpan>(window, "_roamElapsed").TotalMilliseconds, 251,
                "超过250ms阻塞只推进绝对时钟");

            ResetMovementState();
            SetField(window, "_roamLogicalTop", start.Y + 0.6);
            SetField(window, "_roamApproaching", true);
            SetField(window, "_roamVisualDirection", verticalUpDirection);
            SetField(window, "_roamVisualTransitionEndsAt", TimeSpan.Zero);
            timestamp = Stopwatch.GetTimestamp();
            SetField(window, "_roamLastRenderingTimestamp", timestamp);
            Invoke(
                window,
                "AdvanceEdgeRoaming",
                timestamp + StopwatchTicksFromMilliseconds(50));
            Assert(!GetField<bool>(window, "_roamApproaching"),
                "普通靠边阶段必须先准确到达边界");
            AssertClose(GetField<double>(window, "_roamLogicalLeft"), start.X,
                "靠边转横向后的120ms普通方向过渡不得滑步");
            AssertClose(
                GetField<TimeSpan>(window, "_roamVisualTransitionEndsAt").TotalMilliseconds,
                130,
                "靠边耗时10ms后必须建立完整120ms方向过渡截止点");

            Invoke(
                window,
                "AdvanceEdgeRoaming",
                timestamp + StopwatchTicksFromMilliseconds(140));
            AssertClose(GetField<double>(window, "_roamLogicalLeft"), start.X + 0.6,
                "普通方向过渡结束后只消费剩余10ms移动");
            AssertClose(GetField<double>(window, "_roamBoundaryTravelled"), 0.6,
                "普通方向过渡结束后的边界距离必须准确累计");
        }
        finally
        {
            window.Left = originalLeft;
            window.Top = originalTop;
            SetField(window, "_roamLogicalLeft", originalLeft);
            SetField(window, "_roamLogicalTop", originalTop);
            SetField(window, "_roamLastRenderingTimestamp", 0L);
            SetField(window, "_roamElapsed", TimeSpan.Zero);
            SetField(window, "_roamVisualTransitionEndsAt", TimeSpan.Zero);
            SetField(window, "_roamVisualTravelDistance", 0d);
            SetField(window, "_roamBoundaryTravelled", 0d);
            SetField(window, "_roamApproaching", false);
            SetField(window, "_isRoamCornerTurning", false);
            SetField(window, "_roamEdge", topEdge);
            SetField(window, "_roamClockwise", true);
            SetField(window, "_roamVisualDirection", horizontalDirection);
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
            var spriteFrames = GetClipFrames(clip)
                .Cast<object>()
                .Select(frame => GetSpriteFrameInfo(GetProperty<object>(frame, "Image")))
                .ToArray();
            var expectedPageName = $"action-{actionName}";
            Assert(spriteFrames.All(frame => frame.PageName == expectedPageName),
                $"{actionName} 的 idle、wake 和动作帧必须全部来自 {expectedPageName} 分页");

            var expectedResourceNames = Enumerable.Range(1, 14)
                .Select(frameNumber => $"Assets/luban-wake-{frameNumber:00}.png")
                .Prepend("Assets/luban-idle.png")
                .Concat(Enumerable.Range(1, 24)
                    .Select(frameNumber =>
                        $"Assets/luban-{actionName}-frame-{frameNumber:00}.png"))
                .ToHashSet(StringComparer.Ordinal);
            var actualResourceNames = spriteFrames
                .Select(frame => frame.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert(actualResourceNames.SetEquals(expectedResourceNames),
                $"{actionName} 分页动作应完整使用 idle、14 帧 wake 和 24 帧动作资源");
            var frames = GetClipFrames(clip).Cast<object>().ToArray();
            Assert(frames.Length == 108, "普通动作应为 38 帧进入 + 32 帧微循环 + 38 帧返回");
            Assert(frames.Take(38).All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
                "普通动作进入阶段必须使用 85ms 帧间隔");
            Assert(frames.Skip(frames.Length - 38)
                    .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
                "普通动作返回阶段必须使用 85ms 帧间隔");
        }

        var todoEnterFrames = GetClipFrames(GetField<object>(window, "_todoEnterClip"))
            .Cast<object>()
            .ToArray();
        var todoExitFrames = GetClipFrames(GetField<object>(window, "_todoExitClip"))
            .Cast<object>()
            .ToArray();
        var expectedTodoNames = Enumerable.Range(1, 14)
            .Select(frameNumber => $"Assets/luban-wake-{frameNumber:00}.png")
            .Prepend("Assets/luban-idle.png")
            .Append("Assets/luban-think-frame-24.png")
            .ToArray();
        var actualTodoEnterNames = todoEnterFrames
            .Select(frame => GetSpriteFrameInfo(GetProperty<object>(frame, "Image")))
            .ToArray();
        Assert(actualTodoEnterNames.Length == 16 &&
               actualTodoEnterNames.Select(frame => frame.Name)
                   .SequenceEqual(expectedTodoNames) &&
               actualTodoEnterNames.All(frame => frame.PageName == "action-think"),
            "Todo 入场必须在 action-think 同一分页内按 idle→wake01..14→think24 播放");
        Assert(todoExitFrames
                .Select(frame => GetProperty<string>(frame, "Name"))
                .SequenceEqual(todoEnterFrames
                    .Select(frame => GetProperty<string>(frame, "Name"))
                    .Reverse()),
            "Todo 入场和收起必须严格互为反序，快速切换时才能映射到同一姿势");
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
        var forbiddenRoamFragments = new[]
        {
            "roam-crawl", "roam-hop", "luban-roam-crawl", "luban-roam-hop",
            "RoamMode.Crawl", "RoamMode.Hop"
        };
        Assert(forbiddenRoamFragments.All(fragment =>
                !mainWindowSource.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
            "MainWindow 运行时代码不得保留绕屏爬行、跳跃模式或对应资源引用");

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
        var removedRoamAssetPaths = Directory
            .EnumerateFiles(assetsDirectory, "*", SearchOption.AllDirectories)
            .Where(path => forbiddenRoamFragments.Take(4).Any(fragment =>
                Path.GetFileName(path).Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert(removedRoamAssetPaths.Length == 0,
            "Assets 中不得残留绕屏爬行或跳跃资产：" +
            string.Join(", ", removedRoamAssetPaths.Select(Path.GetFileName)));

        var manifestText = File.ReadAllText(Path.Combine(assetsDirectory, "luban-sprite-pages.json"));
        Assert(!manifestText.Contains("action-run", StringComparison.OrdinalIgnoreCase) &&
               !manifestText.Contains("luban-run", StringComparison.OrdinalIgnoreCase) &&
               !manifestText.Contains("roam-crawl", StringComparison.OrdinalIgnoreCase) &&
               !manifestText.Contains("roam-hop", StringComparison.OrdinalIgnoreCase),
            "分页图集清单不得登记 run、绕屏爬行或跳跃分页及帧");

        Assert(!typeof(MainWindow).Assembly.GetManifestResourceNames()
                .Any(name => name.Contains("action-run", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("luban-run", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("roam-crawl", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("roam-hop", StringComparison.OrdinalIgnoreCase)),
            "主程序集不得嵌入 run、绕屏爬行或跳跃资源");
    }

    private static void AssertAbsoluteTimelineMathContract(MainWindow window)
    {
        var source = File.ReadAllText(FindWorkspaceFile("MainWindow.xaml.cs"));
        Assert(source.Contains("CompositionTarget.Rendering", StringComparison.Ordinal) &&
               source.Contains("Stopwatch.GetTimestamp", StringComparison.Ordinal),
            "动作、探头、绕屏与淡化必须由 CompositionTarget.Rendering 和绝对 Stopwatch 时钟驱动");
        Assert(!source.Contains("_frameTimer", StringComparison.Ordinal) &&
               !source.Contains("_roamTimer", StringComparison.Ordinal) &&
               !source.Contains("_edgePeekTimer", StringComparison.Ordinal),
            "视觉状态机不得再由 DispatcherTimer 逐帧 stop/start 驱动");
        Assert(source.Contains("_roamLogicalLeft", StringComparison.Ordinal) &&
               source.Contains("_roamLogicalTop", StringComparison.Ordinal) &&
               source.Contains("SnapDipToPhysicalPixel", StringComparison.Ordinal),
            "绕屏必须以 double 逻辑坐标累计，仅输出到窗口时对齐物理像素");
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
        var expectedDuration = TimeSpan.FromMilliseconds(12_220);
        foreach (var clip in clips)
        {
            var durations = GetClipFrames(clip)
                .Cast<object>()
                .Select(GetFrameDuration)
                .ToArray();
            var total = TimeSpan.FromTicks(durations.Sum(duration => duration.Ticks));
            Assert(total == expectedDuration,
                $"{GetProperty<string>(clip, "ActionName")} 的绝对时间轴应为 12.22 秒，实际 {total.TotalSeconds:F3} 秒");

            foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
            {
                var refreshInterval = TimeSpan.FromSeconds(1d / refreshRate);
                var renderCount = (long)Math.Ceiling(total.TotalSeconds * refreshRate);
                var completionAt = TimeSpan.FromSeconds(renderCount / refreshRate);
                Assert(completionAt >= total && completionAt - total <= refreshInterval,
                    $"{refreshRate:F0}Hz 下动作完成误差必须不超过一个刷新周期");

                foreach (var checkpoint in new[] { 0d, 0.085, 1.0, 3.210, 5.875, 9.9, 12.219 })
                {
                    var elapsed = TimeSpan.FromSeconds(checkpoint);
                    var expectedIndex = ResolveAbsoluteFrameIndex(durations, elapsed);
                    var repeatedIndex = ResolveAbsoluteFrameIndex(durations, elapsed);
                    Assert(expectedIndex == repeatedIndex,
                        $"{refreshRate:F0}Hz 下相同绝对时间必须解析到同一动作帧");
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

        Assert(ResolveWriggleFrameIndex(0) == 0 &&
               ResolveWriggleFrameIndex(1) == 1 &&
               ResolveWriggleFrameIndex(47.999) == WriggleFrameCount - 1 &&
               ResolveWriggleFrameIndex(48) == 0,
            $"蠕动必须按每1 DIP一帧、48 DIP一周期解析{WriggleFrameCount}个相位");

        foreach (var refreshRate in new[] { 59d, 60d, 120d, 144d })
        {
            foreach (var dpiScale in new[] { 1d, 1.25d, 1.5d })
            {
                const double durationSeconds = 10;
                const double speed = 60;
                const double start = -1720.375;
                var logical = start;
                var previousOutput = SnapForSimulation(logical, dpiScale);
                var renderCount = (int)Math.Ceiling(durationSeconds * refreshRate);
                for (var renderIndex = 1; renderIndex <= renderCount; renderIndex++)
                {
                    var previousTime = Math.Min(durationSeconds, (renderIndex - 1) / refreshRate);
                    var currentTime = Math.Min(durationSeconds, renderIndex / refreshRate);
                    logical += (currentTime - previousTime) * speed;
                    var output = SnapForSimulation(logical, dpiScale);
                    Assert(output + 1e-9 >= previousOutput,
                        $"{refreshRate:F0}Hz/{dpiScale * 100:F0}% DPI 的负坐标副屏移动不得倒退");
                    previousOutput = output;
                }

                var expectedLogical = start + 600;
                Assert(Math.Abs(logical - expectedLogical) < 1e-8,
                    $"{refreshRate:F0}Hz 下 10 秒逻辑位移必须恰好为 600 DIP");
                Assert(Math.Abs(previousOutput - expectedLogical) <= 0.5 / dpiScale + 1e-9,
                    $"{refreshRate:F0}Hz/{dpiScale * 100:F0}% DPI 输出误差不得超过 1 个物理像素");
            }
        }

        AssertClose(IntegrateBoundedMovement(0.250, 60), 15,
            "恰好250ms的有效渲染间隔应移动15 DIP");
        AssertClose(IntegrateBoundedMovement(0.251, 60), 0,
            "超过250ms的休眠必须整段丢弃，避免恢复后瞬移");
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

    private static int ResolveWriggleFrameIndex(double travelledDips)
    {
        var cycleDistance = ((travelledDips % 48d) + 48d) % 48d;
        return Math.Min(WriggleFrameCount - 1, (int)Math.Floor(cycleDistance));
    }

    private static double SnapForSimulation(double value, double dpiScale) =>
        Math.Round(value * dpiScale, MidpointRounding.AwayFromZero) / dpiScale;

    private static double IntegrateBoundedMovement(double elapsedSeconds, double speed) =>
        elapsedSeconds is > 0 and <= 0.250 ? elapsedSeconds * speed : 0;

    private static long StopwatchTicksFromMilliseconds(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000d);

    private static double StopwatchTicksToMilliseconds(long ticks) =>
        ticks * 1000d / Stopwatch.Frequency;

    private static void AssertWriggleAssetContinuityContract()
    {
        var horizontal = LoadWriggleSequence("horizontal", WriggleFrameCount);
        var verticalUp = LoadWriggleSequence("vertical-up", WriggleFrameCount);
        var verticalDown = LoadWriggleSequence("vertical-down", WriggleFrameCount);
        var corner = Enumerable.Range(1, WriggleCornerFrameCount)
            .Select(frameNumber => ReadContinuityFrame(FindWorkspaceFile(
                "Assets",
                $"luban-roam-wriggle-corner-{frameNumber:00}.png")))
            .ToArray();

        ReportContinuityMetrics(horizontal, "蠕动横向", loop: true);
        ReportContinuityMetrics(verticalUp, "蠕动竖向向上", loop: true);
        ReportContinuityMetrics(verticalDown, "蠕动竖向向下", loop: true);
        ReportContinuityMetrics(corner, "蠕动转角", loop: false);

        AssertUniqueContinuityFrames(horizontal, "蠕动横向");
        AssertUniqueContinuityFrames(verticalUp, "蠕动竖向向上");
        AssertUniqueContinuityFrames(verticalDown, "蠕动竖向向下");
        AssertUniqueContinuityFrames(corner, "蠕动转角");

        AssertLoopContinuity(horizontal, "蠕动横向", 0.92, 0.95, 0.025);
        AssertLoopContinuity(verticalUp, "蠕动竖向向上", 0.92, 0.95, 0.025);
        AssertLoopContinuity(verticalDown, "蠕动竖向向下", 0.92, 0.95, 0.025);
        AssertCornerTransitionContinuity(
            corner,
            horizontal[0],
            verticalUp[0]);

        foreach (var (sequence, name) in new[]
                 {
                     (horizontal, "蠕动横向"),
                     (verticalUp, "蠕动竖向向上"),
                     (verticalDown, "蠕动竖向向下")
                 })
        {
            var brimSpread = (sequence.Max(frame => frame.BrimWidth) -
                              sequence.Min(frame => frame.BrimWidth)) /
                             sequence.Average(frame => frame.BrimWidth);
            Assert(brimSpread <= 0.03,
                $"{name} 的帽檐宽度波动不得超过3%，实际 {brimSpread:P2}");
            var maximumAdjacentCapShift = EnumerateLoopPairs(sequence)
                .Max(pair => Math.Sqrt(
                    Math.Pow(pair.Next.CapCenterX - pair.Current.CapCenterX, 2) +
                    Math.Pow(pair.Next.CapCenterY - pair.Current.CapCenterY, 2)));
            Assert(maximumAdjacentCapShift <= 2.0,
                $"{name} 相邻及首尾帽子中心位移不得超过2px，实际 {maximumAdjacentCapShift:F2}px");
        }

        var maximumBaselineShift = EnumerateLoopPairs(horizontal)
            .Max(pair => Math.Abs(pair.Next.Bottom - pair.Current.Bottom));
        Assert(maximumBaselineShift <= 1,
            $"蠕动横向相邻及首尾接触基线变化不得超过1px，实际 {maximumBaselineShift}px");

        foreach (var frame in verticalUp.Concat(verticalDown))
        {
            Assert(frame.BrimCenterY - frame.CapCenterY >= 8,
                $"竖边攀爬必须保持红色帽冠在蓝色帽檐上方至少8px：{frame.Path}");
            Assert(frame.BrimCenterY <= frame.Top + frame.VisibleHeight * 0.58,
                $"竖边攀爬人物必须正立，帽檐应位于身体上部：{frame.Path}");
        }

        var reverseFrameNumbers = Enumerable.Range(1, WriggleFrameCount).Reverse().ToArray();
        for (var downIndex = 0; downIndex < verticalDown.Length; downIndex++)
        {
            var expectedUp = verticalUp[reverseFrameNumbers[downIndex] - 1];
            Assert(verticalDown[downIndex].Pixels.SequenceEqual(expectedUp.Pixels),
                $"竖向向下第{downIndex + 1}帧必须复用竖向向上的反向相位，且人物保持正立");
        }
    }

    private static ContinuityFrame[] LoadWriggleSequence(string direction, int count) =>
        Enumerable.Range(1, count)
            .Select(frameNumber => ReadContinuityFrame(FindWorkspaceFile(
                "Assets",
                $"luban-roam-wriggle-{direction}-{frameNumber:00}.png")))
            .ToArray();

    private static void ReportContinuityMetrics(
        IReadOnlyList<ContinuityFrame> frames,
        string name,
        bool loop)
    {
        var pairs = loop
            ? EnumerateLoopPairs(frames).ToArray()
            : frames.Zip(frames.Skip(1), (current, next) => (Current: current, Next: next))
                .ToArray();
        var ious = pairs.Select(pair => CalculateAlphaIou(pair.Current, pair.Next)).ToArray();
        var maximumScaleStep = pairs.Max(pair =>
            CalculateEquivalentScaleStep(pair.Current, pair.Next));
        var maximumCapShift = pairs.Max(pair => Math.Sqrt(
            Math.Pow(pair.Next.CapCenterX - pair.Current.CapCenterX, 2) +
            Math.Pow(pair.Next.CapCenterY - pair.Current.CapCenterY, 2)));
        var brimSpread = (frames.Max(frame => frame.BrimWidth) -
                          frames.Min(frame => frame.BrimWidth)) /
                         frames.Average(frame => frame.BrimWidth);
        Console.WriteLine(
            $"[METRIC] {name}: IoU min={ious.Min():F3}, mean={ious.Average():F3}; " +
            $"scaleStep={maximumScaleStep:P2}; brimSpread={brimSpread:P2}; " +
            $"capShift={maximumCapShift:F2}px");
        if (brimSpread > 0.03 || maximumCapShift > 2)
        {
            foreach (var frame in frames)
            {
                Console.WriteLine(
                    $"[METRIC] {name} {Path.GetFileName(frame.Path)}: " +
                    $"BrimWidth={frame.BrimWidth}, " +
                    $"CapCenter=({frame.CapCenterX:F2},{frame.CapCenterY:F2}), " +
                    $"BrimCenter=({frame.BrimCenterX:F2},{frame.BrimCenterY:F2}), " +
                    $"Baseline={frame.Bottom}");
            }
        }
    }

    private static void AssertUniqueContinuityFrames(
        IReadOnlyList<ContinuityFrame> frames,
        string name)
    {
        var hashes = frames
            .Select(frame => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(frame.Pixels)))
            .ToArray();
        Assert(hashes.Distinct(StringComparer.Ordinal).Count() == frames.Count,
            $"{name} 的 {frames.Count} 帧必须全部唯一，不得复制相邻帧伪装补帧");
    }

    private static void AssertLoopContinuity(
        IReadOnlyList<ContinuityFrame> frames,
        string name,
        double minimumIou,
        double minimumMeanIou,
        double maximumScaleStep)
    {
        var pairs = EnumerateLoopPairs(frames).ToArray();
        var ious = pairs
            .Select(pair => new
            {
                pair.Current,
                pair.Next,
                Value = CalculateAlphaIou(pair.Current, pair.Next)
            })
            .ToArray();
        var minimum = ious.MinBy(metric => metric.Value)!;
        var mean = ious.Average(metric => metric.Value);
        Console.WriteLine(
            $"[METRIC] {name} AlphaIoU min={minimum.Value:F3}, mean={mean:F3}, " +
            $"worst={Path.GetFileName(minimum.Current.Path)}->{Path.GetFileName(minimum.Next.Path)}");
        foreach (var metric in ious.Where(metric => metric.Value < minimumIou))
        {
            Console.WriteLine(
                $"[METRIC] {name} below-threshold {metric.Value:F3}: " +
                $"{Path.GetFileName(metric.Current.Path)}->{Path.GetFileName(metric.Next.Path)}");
        }
        Assert(minimum.Value >= minimumIou,
            $"{name} 相邻及首尾 Alpha IoU 不得低于 {minimumIou:F2}，实际最小 {minimum.Value:F3}：" +
            $"{Path.GetFileName(minimum.Current.Path)} -> {Path.GetFileName(minimum.Next.Path)}");
        Assert(mean >= minimumMeanIou,
            $"{name} Alpha IoU 平均值不得低于 {minimumMeanIou:F2}，实际 {mean:F3}");

        var maximumStep = pairs.Max(pair => CalculateEquivalentScaleStep(pair.Current, pair.Next));
        Assert(maximumStep <= maximumScaleStep,
            $"{name} 单帧人物缩放变化不得超过 {maximumScaleStep:P1}，实际 {maximumStep:P2}");
    }

    private static void AssertCornerTransitionContinuity(
        IReadOnlyList<ContinuityFrame> frames,
        ContinuityFrame horizontalEndpoint,
        ContinuityFrame verticalEndpoint)
    {
        Assert(frames.Count == WriggleCornerFrameCount,
            $"蠕动转角必须包含{WriggleCornerFrameCount}个真实衔接姿势");
        Assert(frames[0].Pixels.SequenceEqual(horizontalEndpoint.Pixels),
            "转角第1帧必须与横向蠕动入口姿势完全一致");
        Assert(frames[^1].Pixels.SequenceEqual(verticalEndpoint.Pixels),
            $"转角第{WriggleCornerFrameCount}帧必须与竖向攀爬入口姿势完全一致");

        var pairs = frames.Zip(frames.Skip(1), (current, next) => (Current: current, Next: next))
            .ToArray();
        var ious = pairs
            .Select(pair => CalculateAlphaIou(pair.Current, pair.Next))
            .ToArray();
        var minimumIouIndex = Array.IndexOf(ious, ious.Min());
        Assert(ious.Min() >= 0.55 && ious.Average() >= 0.64,
            $"转角真实起身姿势必须连续，实际 IoU min={ious.Min():F3}, " +
            $"mean={ious.Average():F3}，最差 " +
            $"{Path.GetFileName(frames[minimumIouIndex].Path)} -> " +
            $"{Path.GetFileName(frames[minimumIouIndex + 1].Path)}");

        var maximumBrimDelta = frames.Max(frame => frame.BrimWidth) -
                               frames.Min(frame => frame.BrimWidth);
        var brimSpread = maximumBrimDelta / frames.Average(frame => frame.BrimWidth);
        Assert(maximumBrimDelta <= 2 && brimSpread <= 0.035,
            $"转角过程中帽檐尺寸不得突变，实际相差 {maximumBrimDelta:F0}px、波动 {brimSpread:P2}");
        var capShifts = pairs.Select(pair => Math.Sqrt(
                Math.Pow(pair.Next.CapCenterX - pair.Current.CapCenterX, 2) +
                Math.Pow(pair.Next.CapCenterY - pair.Current.CapCenterY, 2)))
            .ToArray();
        var maximumCapShift = capShifts.Max();
        var maximumCapShiftIndex = Array.IndexOf(capShifts, maximumCapShift);
        Assert(maximumCapShift <= 12,
            $"转角帽子轨迹不得出现跨姿势跳变，实际最大 {maximumCapShift:F2}px：" +
            $"{Path.GetFileName(frames[maximumCapShiftIndex].Path)} -> " +
            $"{Path.GetFileName(frames[maximumCapShiftIndex + 1].Path)}");
        Assert(pairs.All(pair =>
                pair.Next.CapCenterX <= pair.Current.CapCenterX + 1),
            "转角帽子水平轨迹必须持续朝攀爬边移动，不得反向抖动");
        var maximumVerticalSettle = pairs.Max(pair =>
            pair.Next.CapCenterY - pair.Current.CapCenterY);
        Assert(maximumVerticalSettle <= 8,
            $"转角允许身体重心自然落稳，但帽子单帧回落不得超过8px，实际 " +
            $"{maximumVerticalSettle:F2}px");
        Assert(pairs.Max(pair => Math.Abs(pair.Next.Bottom - pair.Current.Bottom)) <= 1,
            $"转角{WriggleCornerFrameCount}帧必须保持一致的屏幕接触基线");
    }

    private static IEnumerable<(ContinuityFrame Current, ContinuityFrame Next)> EnumerateLoopPairs(
        IReadOnlyList<ContinuityFrame> frames)
    {
        for (var index = 0; index < frames.Count; index++)
        {
            yield return (frames[index], frames[(index + 1) % frames.Count]);
        }
    }

    private static double CalculateAlphaIou(ContinuityFrame first, ContinuityFrame second)
    {
        var intersection = 0;
        var union = 0;
        for (var index = 0; index < first.AlphaMask.Length; index++)
        {
            if (first.AlphaMask[index] && second.AlphaMask[index])
            {
                intersection++;
            }

            if (first.AlphaMask[index] || second.AlphaMask[index])
            {
                union++;
            }
        }

        return union == 0 ? 1 : intersection / (double)union;
    }

    private static double CalculateEquivalentScaleStep(ContinuityFrame first, ContinuityFrame second)
    {
        var firstScale = Math.Sqrt(first.OpaqueArea);
        var secondScale = Math.Sqrt(second.OpaqueArea);
        return Math.Abs(firstScale - secondScale) / Math.Max(1, (firstScale + secondScale) / 2d);
    }

    private static ContinuityFrame ReadContinuityFrame(string path)
    {
        BitmapSource source;
        using (var stream = File.OpenRead(path))
        {
            source = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
        }

        const int width = 190;
        const int height = 242;
        var targetHeight = Math.Max(
            1,
            (int)Math.Round(
                source.PixelHeight * width / (double)source.PixelWidth,
                MidpointRounding.ToEven));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(
                source,
                new Rect(0, height - targetHeight, width, targetHeight));
        }

        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        rendered.CopyPixels(pixels, stride, 0);
        var alphaMask = new bool[width * height];
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        var opaqueArea = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var alpha = pixels[y * stride + x * 4 + 3];
                if (alpha <= 16)
                {
                    continue;
                }

                alphaMask[y * width + x] = true;
                opaqueArea++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        Assert(right >= left && bottom >= top, $"蠕动精灵必须包含可见像素：{path}");
        var visibleHeight = bottom - top + 1;
        var brim = FindHatComponent(
            pixels,
            width,
            height,
            new Int32Rect(
                left,
                top,
                right - left + 1,
                Math.Max(1, (int)Math.Ceiling(visibleHeight * 0.58))),
            HatColor.Blue);
        var cap = FindHatComponent(
            pixels,
            width,
            height,
            new Int32Rect(left, top, right - left + 1, Math.Max(1, brim.Top - top + 3)),
            HatColor.Red);
        return new ContinuityFrame(
            path,
            pixels,
            alphaMask,
            left,
            top,
            right,
            bottom,
            opaqueArea,
            brim.Width,
            brim.CenterX,
            brim.CenterY,
            cap.CenterX,
            cap.CenterY);
    }

    private static HatComponent FindHatComponent(
        byte[] pixels,
        int width,
        int height,
        Int32Rect search,
        HatColor color)
    {
        var mask = new bool[width * height];
        var searchRight = Math.Min(width, search.X + search.Width);
        var searchBottom = Math.Min(height, search.Y + search.Height);
        for (var y = Math.Max(0, search.Y); y < searchBottom; y++)
        {
            for (var x = Math.Max(0, search.X); x < searchRight; x++)
            {
                var offset = (y * width + x) * 4;
                var alpha = pixels[offset + 3];
                if (alpha <= 24)
                {
                    continue;
                }

                var blue = pixels[offset] * 255 / alpha;
                var green = pixels[offset + 1] * 255 / alpha;
                var red = pixels[offset + 2] * 255 / alpha;
                mask[y * width + x] = color switch
                {
                    HatColor.Blue => blue >= 100 && green >= 75 &&
                                     blue * 100 >= red * 118 &&
                                     blue * 100 >= green * 105 &&
                                     green * 100 >= red * 90,
                    HatColor.Red => red >= 100 &&
                                    red * 100 >= blue * 125 &&
                                    red * 100 >= green * 125,
                    _ => false
                };
            }
        }

        var visited = new bool[mask.Length];
        HatComponent? best = null;
        long bestScore = -1;
        for (var y = Math.Max(0, search.Y); y < searchBottom; y++)
        {
            for (var x = Math.Max(0, search.X); x < searchRight; x++)
            {
                var start = y * width + x;
                if (!mask[start] || visited[start])
                {
                    continue;
                }

                var queue = new Queue<int>();
                queue.Enqueue(start);
                visited[start] = true;
                var count = 0;
                long sumX = 0;
                long sumY = 0;
                var componentLeft = x;
                var componentTop = y;
                var componentRight = x;
                var componentBottom = y;
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    var currentX = index % width;
                    var currentY = index / width;
                    count++;
                    sumX += currentX;
                    sumY += currentY;
                    componentLeft = Math.Min(componentLeft, currentX);
                    componentTop = Math.Min(componentTop, currentY);
                    componentRight = Math.Max(componentRight, currentX);
                    componentBottom = Math.Max(componentBottom, currentY);
                    for (var nextY = Math.Max(search.Y, currentY - 1);
                         nextY <= Math.Min(searchBottom - 1, currentY + 1);
                         nextY++)
                    {
                        for (var nextX = Math.Max(search.X, currentX - 1);
                             nextX <= Math.Min(searchRight - 1, currentX + 1);
                             nextX++)
                        {
                            var next = nextY * width + nextX;
                            if (!mask[next] || visited[next])
                            {
                                continue;
                            }

                            visited[next] = true;
                            queue.Enqueue(next);
                        }
                    }
                }

                var componentWidth = componentRight - componentLeft + 1;
                var score = (long)count * componentWidth;
                if (count >= 3 && componentWidth >= 3 && score > bestScore)
                {
                    bestScore = score;
                    best = new HatComponent(
                        componentLeft,
                        componentTop,
                        componentRight,
                        componentBottom,
                        sumX / (double)count,
                        sumY / (double)count);
                }
            }
        }

        return best ?? throw new InvalidOperationException(
            $"蠕动精灵必须包含可检测的{(color == HatColor.Blue ? "蓝色帽檐" : "红色帽冠")}");
    }

    private static void AssertMotionAssetScaleContract()
    {
        var idle = ReadSpriteVisualMetrics(
            FindWorkspaceFile("Assets", "luban-idle.png"));
        var wake = Enumerable.Range(1, 14)
            .Select(frameNumber => ReadSpriteVisualMetrics(FindWorkspaceFile(
                "Assets",
                $"luban-wake-{frameNumber:00}.png")))
            .ToArray();
        Assert(wake.Max(metric => metric.BrimWidth) -
               wake.Min(metric => metric.BrimWidth) <= 8,
            "14 帧起身动画的帽檐尺度波动必须小于 5%，不能站起时突然变大");
        Assert(Math.Abs(wake[0].BrimWidth - idle.BrimWidth) <= 10 &&
               Math.Abs(wake[^1].BrimWidth - idle.BrimWidth) <= 10,
            "起身首尾头部尺度必须与趴枕头待机一致");
        Assert(wake.Zip(wake.Skip(1))
                .Max(pair => Math.Abs(pair.First.Top - pair.Second.Top)) <= 40,
            "起身相邻姿态的头顶位移不得超过约 13 个显示像素");

        foreach (var action in new[] { "yawn", "cry", "cute", "like", "eat", "wave", "think" })
        {
            var actionMetrics = Enumerable.Range(1, 24)
                .Select(frameNumber => ReadSpriteVisualMetrics(FindWorkspaceFile(
                    "Assets",
                    $"luban-{action}-frame-{frameNumber:00}.png")))
                .ToArray();
            Assert(actionMetrics.Max(metric => metric.BrimWidth) -
                   actionMetrics.Min(metric => metric.BrimWidth) <= 4,
                $"{action} 的 24 帧帽檐尺度必须稳定，不能在动作内部忽大忽小");
            Assert(Math.Abs(actionMetrics[0].BrimWidth - idle.BrimWidth) <= 10 &&
                   Math.Abs(actionMetrics.Average(metric => metric.BrimWidth) -
                            idle.BrimWidth) <= 10,
                $"{action} 的头部尺度必须与同一个待机人物接近");
            Assert(actionMetrics[0].VisibleHeight >= wake[^1].VisibleHeight * 0.88 &&
                   actionMetrics[0].VisibleHeight <= wake[^1].VisibleHeight * 1.05,
                $"起身末帧接到 {action} 首帧时全身高度不得突然变大或变矮");
            Assert(actionMetrics.Max(metric => Math.Abs(metric.BrimCenterX - 225)) <= 2,
                $"{action} 的帽檐水平锚点必须稳定在画布中心");
            Assert(actionMetrics.Zip(actionMetrics.Skip(1))
                    .Max(pair => Math.Abs(pair.First.Top - pair.Second.Top)) <= 35,
                $"{action} 相邻帧头顶移动不得超过约 11 个显示像素");
            Assert(actionMetrics.Max(metric => metric.VisibleWidth) <= 325 &&
                   actionMetrics.Max(metric => metric.VisibleHeight) <=
                   wake[^1].VisibleHeight + 20,
                $"{action} 不得通过放大整个人物来填满 450×550 源画布");
        }

        var todoPose = ReadSpriteVisualMetrics(FindWorkspaceFile(
            "Assets",
            "luban-think-frame-24.png"));
        Assert(Math.Abs(todoPose.BrimWidth - idle.BrimWidth) <= 10 &&
               Math.Abs(todoPose.VisibleHeight - wake[^1].VisibleHeight) <= 20,
            "Todo 思考姿势的头部和全身尺度必须与待机/起身保持连续");

        var leftPeek = Enumerable.Range(1, 4)
            .Select(frameNumber => ReadSpriteVisualMetrics(FindWorkspaceFile(
                "Assets",
                $"luban-edge-left-{frameNumber:00}.png")))
            .ToArray();
        var bottomPeek = Enumerable.Range(1, 4)
            .Select(frameNumber => ReadSpriteVisualMetrics(FindWorkspaceFile(
                "Assets",
                $"luban-edge-bottom-{frameNumber:00}.png")))
            .ToArray();
        Assert(Math.Abs(leftPeek.Average(metric => metric.BrimWidth) - idle.BrimWidth) <= 5 &&
               Math.Abs(bottomPeek.Average(metric => metric.BrimWidth) - idle.BrimWidth) <= 5,
            "左/右、下边缘探头的帽檐尺度必须与待机保持在约3%以内");
        Assert(leftPeek.Max(metric => metric.BrimWidth) -
               leftPeek.Min(metric => metric.BrimWidth) <= 10 &&
               bottomPeek.Max(metric => metric.BrimWidth) -
               bottomPeek.Min(metric => metric.BrimWidth) <= 10,
            "探头四帧内部尺度必须稳定");

        const string mode = "wriggle";
        {
            const int frameCount = WriggleFrameCount;
            SpriteVisualMetrics[]? verticalUpMetrics = null;
            foreach (var direction in new[] { "horizontal", "vertical-up", "vertical-down" })
            {
                var metrics = Enumerable.Range(1, frameCount)
                    .Select(frameNumber => ReadSpriteVisualMetrics(FindWorkspaceFile(
                        "Assets",
                        $"luban-roam-{mode}-{direction}-{frameNumber:00}.png")))
                    .ToArray();
                var widthSpan = metrics.Max(metric => metric.VisibleWidth) -
                                metrics.Min(metric => metric.VisibleWidth);
                var heightSpan = metrics.Max(metric => metric.VisibleHeight) -
                                 metrics.Min(metric => metric.VisibleHeight);
                var areaScaleSpan = metrics.Max(metric => Math.Sqrt(metric.OpaqueArea)) -
                                    metrics.Min(metric => Math.Sqrt(metric.OpaqueArea));
                if (widthSpan > 40 || heightSpan > 32 || areaScaleSpan > 12)
                {
                    for (var frameIndex = 0; frameIndex < metrics.Length; frameIndex++)
                    {
                        Console.WriteLine(
                            $"[METRIC] {mode}/{direction}-{frameIndex + 1:00}: " +
                            $"Visible={metrics[frameIndex].VisibleWidth}x{metrics[frameIndex].VisibleHeight}, " +
                            $"sqrtArea={Math.Sqrt(metrics[frameIndex].OpaqueArea):F2}");
                    }
                }
                if (direction == "vertical-up")
                {
                    verticalUpMetrics = metrics;
                    Assert(metrics.All(metric => metric.BrimCenterYRatio < 0.58),
                        $"{mode} 向上绕屏的帽檐必须位于身体上部，人物应保持正立；" +
                        $"最大比例 {metrics.Max(metric => metric.BrimCenterYRatio):F3}");
                }
                else if (direction == "vertical-down")
                {
                    Assert(verticalUpMetrics is not null &&
                           metrics.Select(metric => (
                                   metric.VisibleWidth,
                                   metric.VisibleHeight,
                                   metric.OpaqueArea))
                               .OrderBy(metric => metric.VisibleWidth)
                               .ThenBy(metric => metric.VisibleHeight)
                               .ThenBy(metric => metric.OpaqueArea)
                               .SequenceEqual(verticalUpMetrics.Select(metric => (
                                   metric.VisibleWidth,
                                   metric.VisibleHeight,
                                   metric.OpaqueArea))
                                   .OrderBy(metric => metric.VisibleWidth)
                                   .ThenBy(metric => metric.VisibleHeight)
                                   .ThenBy(metric => metric.OpaqueArea)),
                        $"{mode} 向下帧必须是本模式向上帧的等尺度对应动作");
                    Assert(metrics.All(metric => metric.BrimCenterYRatio < 0.58),
                        $"{mode} 向下绕屏也必须保持人物正立，不能把整个人旋转180度；" +
                        $"最大比例 {metrics.Max(metric => metric.BrimCenterYRatio):F3}");
                }
            }

            var reverseFrameNumbers = Enumerable.Range(1, frameCount).Reverse().ToArray();
            var nonRotatedPairCount = 0;
            for (var downFrameNumber = 1; downFrameNumber <= frameCount; downFrameNumber++)
            {
                var upPath = FindWorkspaceFile(
                    "Assets",
                    $"luban-roam-{mode}-vertical-up-{reverseFrameNumbers[downFrameNumber - 1]:00}.png");
                var downPath = FindWorkspaceFile(
                    "Assets",
                    $"luban-roam-{mode}-vertical-down-{downFrameNumber:00}.png");
                Assert(File.ReadAllBytes(upPath).SequenceEqual(File.ReadAllBytes(downPath)),
                    $"{mode} 向下第{downFrameNumber}帧必须复用向上循环的反向相位，而不是翻转人物");
                if (!BitmapEqualsAfterRotate180(upPath, downPath))
                {
                    nonRotatedPairCount++;
                }
            }

            Assert(nonRotatedPairCount >= frameCount - 2,
                $"{mode} 向下循环不得由向上素材整体旋转180度得到");
        }

    }

    private static SpriteVisualMetrics ReadSpriteVisualMetrics(string path)
    {
        BitmapSource bitmap;
        using (var stream = File.OpenRead(path))
        {
            bitmap = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
        }

        if (bitmap.Format != PixelFormats.Bgra32)
        {
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        }

        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var left = bitmap.PixelWidth;
        var top = bitmap.PixelHeight;
        var right = -1;
        var bottom = -1;
        var opaqueArea = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                if (pixels[y * stride + x * 4 + 3] <= 16)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                opaqueArea++;
            }
        }

        Assert(right >= left && bottom >= top, $"精灵必须包含可见像素：{path}");
        var headLimit = top + Math.Max(1, (int)Math.Ceiling((bottom - top + 1) * 0.58));
        var mask = new byte[bitmap.PixelWidth * bitmap.PixelHeight];
        for (var y = top; y < headLimit; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var offset = y * stride + x * 4;
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                var alpha = pixels[offset + 3];
                if (alpha > 24 && blue >= 95 && green >= 55 &&
                    blue * 100 >= red * 122 && blue * 100 >= green * 108)
                {
                    mask[y * bitmap.PixelWidth + x] = 1;
                }
            }
        }

        var visited = new byte[mask.Length];
        long bestScore = -1;
        var bestWidth = 0;
        var bestLeft = 0;
        var bestRight = 0;
        var bestTop = 0;
        var bestBottom = 0;
        for (var startY = top; startY < headLimit; startY++)
        {
            for (var startX = left; startX <= right; startX++)
            {
                var start = startY * bitmap.PixelWidth + startX;
                if (mask[start] == 0 || visited[start] != 0)
                {
                    continue;
                }

                var queue = new Queue<int>();
                queue.Enqueue(start);
                visited[start] = 1;
                var count = 0;
                var componentLeft = startX;
                var componentRight = startX;
                var componentTop = startY;
                var componentBottom = startY;
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    var x = index % bitmap.PixelWidth;
                    var y = index / bitmap.PixelWidth;
                    count++;
                    componentLeft = Math.Min(componentLeft, x);
                    componentRight = Math.Max(componentRight, x);
                    componentTop = Math.Min(componentTop, y);
                    componentBottom = Math.Max(componentBottom, y);
                    for (var nextY = Math.Max(top, y - 1);
                         nextY <= Math.Min(headLimit - 1, y + 1);
                         nextY++)
                    {
                        for (var nextX = Math.Max(left, x - 1);
                             nextX <= Math.Min(right, x + 1);
                             nextX++)
                        {
                            var next = nextY * bitmap.PixelWidth + nextX;
                            if (mask[next] == 0 || visited[next] != 0)
                            {
                                continue;
                            }

                            visited[next] = 1;
                            queue.Enqueue(next);
                        }
                    }
                }

                var width = componentRight - componentLeft + 1;
                var score = (long)count * width;
                if (count >= 18 && width >= 12 && score > bestScore)
                {
                    bestScore = score;
                    bestWidth = width;
                    bestLeft = componentLeft;
                    bestRight = componentRight;
                    bestTop = componentTop;
                    bestBottom = componentBottom;
                }
            }
        }

        Assert(bestWidth > 0, $"精灵必须包含可检测的蓝色帽檐：{path}");
        return new SpriteVisualMetrics(
            bestWidth,
            (bestLeft + bestRight) / 2d,
            ((bestTop + bestBottom) / 2d - top) / Math.Max(1, bottom - top + 1),
            left,
            top,
            right,
            bottom,
            opaqueArea);
    }

    private static bool BitmapEqualsAfterRotate180(string sourcePath, string targetPath)
    {
        var source = LoadBitmapPixels(sourcePath);
        var target = LoadBitmapPixels(targetPath);
        if (source.Width != target.Width || source.Height != target.Height)
        {
            return false;
        }

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var sourceOffset = (y * source.Width + x) * 4;
                var targetOffset =
                    ((source.Height - 1 - y) * target.Width + source.Width - 1 - x) * 4;
                if (!source.Pixels.AsSpan(sourceOffset, 4)
                        .SequenceEqual(target.Pixels.AsSpan(targetOffset, 4)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static (int Width, int Height, byte[] Pixels) LoadBitmapPixels(string path)
    {
        BitmapSource bitmap;
        using (var stream = File.OpenRead(path))
        {
            bitmap = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
        }

        if (bitmap.Format != PixelFormats.Bgra32)
        {
            bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        }

        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return (bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

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
            AssertClose(StopwatchTicksToMilliseconds(nextDeadline - deadline), 220,
                $"{edge} 探头端点之间必须以220ms节奏换帧");
            deadline = nextDeadline;
            Invoke(window, "AdvanceEdgePeek", deadline);
            nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            AssertClose(StopwatchTicksToMilliseconds(nextDeadline - deadline), 220,
                $"{edge} 探头中间帧必须维持220ms节奏");
            deadline = nextDeadline;
            Invoke(window, "AdvanceEdgePeek", deadline);
            nextDeadline = GetField<long>(window, "_edgePeekFrameDeadlineTimestamp");
            AssertClose(StopwatchTicksToMilliseconds(nextDeadline - deadline), 500,
                $"{edge} 探头到达另一端点后必须再次停留500ms");
            Invoke(window, "ExitEdgePeek", false, true);
            Assert(GetField<long>(window, "_edgePeekFrameDeadlineTimestamp") == 0,
                $"退出{edge}探头后必须清除绝对时间截止点");
        }

        window.Left = safeLeft;
        window.Top = safeTop;
    }

    private static void AssertUserInterruptedRoamIsRescheduled(MainWindow window)
    {
        SetField(window, "_isEdgeRoaming", true);
        SetField(window, "_edgeRoamingEnabled", true);
        SetField(window, "_roamBoundaryTargetDistance", 1000d);
        SetField(window, "_roamBoundaryTravelled", 120d);
        SetField(window, "_nextRoamDueUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        SetField(window, "_roamLastRenderingTimestamp", Stopwatch.GetTimestamp());
        Invoke(window, "UpdateVisualClockSubscription");

        var interruptedAt = DateTimeOffset.UtcNow;
        Invoke(
            window,
            "StopEdgeRoaming",
            "测试用户点击或拖动",
            false,
            true,
            true);
        Assert(!GetField<bool>(window, "_isEdgeRoaming") &&
               GetField<long>(window, "_roamLastRenderingTimestamp") == 0,
            "用户点击或拖动必须立即停止绕屏并清除其绝对时间游标");
        var nextDue = GetField<DateTimeOffset>(window, "_nextRoamDueUtc");
        Assert(nextDue >= interruptedAt + TimeSpan.FromMinutes(10) &&
               nextDue <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20.1),
            "用户打断绕屏后下一圈必须重新安排到 10-20 分钟后");

        Invoke(window, "RestartAutomaticCountdown");
        PumpDispatcher(TimeSpan.FromMilliseconds(250));
        Assert(!GetField<bool>(window, "_isEdgeRoaming"),
            "用户打断后 RestartAutomaticCountdown 不得在 100ms 内重新启动绕屏");
    }

    private static void AssertPointerDownInterruptsRoam(MainWindow window)
    {
        var petHost = GetField<Grid>(window, "PetHost");
        SetField(window, "_isEdgeRoaming", true);
        SetField(window, "_edgeRoamingEnabled", true);
        SetField(window, "_roamApproaching", false);
        SetField(window, "_roamClockwise", true);
        SetField(window, "_roamMode", GetNestedEnum("RoamMode", "Wriggle"));
        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Top"));
        SetField(window, "_roamVisualEdge", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_roamVisualDirection",
            GetNestedEnum("RoamVisualDirection", "None"));
        SetField(window, "_roamBoundaryTargetDistance", 1200d);
        SetField(window, "_roamBoundaryTravelled", 180d);
        SetField(window, "_nextRoamDueUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        SetField(window, "_roamElapsed", TimeSpan.Zero);
        SetField(window, "_roamVisualTransitionEndsAt", TimeSpan.Zero);
        SetField(window, "_roamLastRenderingTimestamp", Stopwatch.GetTimestamp());
        Invoke(window, "UpdateRoamVisual");
        Invoke(window, "UpdateVisualClockSubscription");

        var roamBaseOffset =
            GetField<TranslateTransform>(window, "PetRoamBaseOffset");
        var facing = GetField<ScaleTransform>(window, "PetFacingScale");
        var displayFrameBuffer = GetField<WriteableBitmap>(window, "_displayFrameBuffer");
        var spriteBrush = GetField<ImageBrush>(window, "PetSpriteBrush");
        roamBaseOffset.X = 35;
        roamBaseOffset.Y = 10;
        facing.ScaleX = -1;
        Assert(GetField<bool>(window, "_isFrameBlending"),
            "真实点击竞态测试必须从仍在单buffer淡化的绕屏画面开始");

        var interruptedAt = DateTimeOffset.UtcNow;
        var mouseDown = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = petHost
        };
        petHost.RaiseEvent(mouseDown);

        Assert(mouseDown.Handled &&
               !GetField<bool>(window, "_isEdgeRoaming") &&
               GetField<long>(window, "_roamLastRenderingTimestamp") == 0,
            "真实左键按下事件必须在区分点击/拖拽前立即停止绕屏");
        Assert(GetField<bool>(window, "_pointerDown") &&
               GetField<bool>(window, "_dragInteractionActive"),
            "绕屏停止后同一次按下事件仍必须继续进入点击/拖拽判定");
        var nextDue = GetField<DateTimeOffset>(window, "_nextRoamDueUtc");
        Assert(nextDue >= interruptedAt + TimeSpan.FromMinutes(10) &&
               nextDue <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20.1),
            "真实点击或拖拽按下事件必须把下一次绕屏重排到10-20分钟后");
        AssertClose(roamBaseOffset.X, 0, "真实点击停止后的主画面锚点 X");
        AssertClose(roamBaseOffset.Y, 0, "真实点击停止后的主画面锚点 Y");
        AssertClose(facing.ScaleX, 1, "真实点击停止后的主画面朝向");
        Assert(GetField<bool>(window, "_isFrameBlending") &&
               GetField<TimeSpan>(window, "_activeFrameBlendDuration") ==
               TimeSpan.FromMilliseconds(120) &&
               ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer),
            "真实点击停止绕屏后必须在同一完整帧buffer内淡回待机，不能闪切");

        petHost.ReleaseMouseCapture();
        SetField(window, "_pointerDown", false);
        SetField(window, "_dragStarted", false);
        SetField(window, "_dragInteractionActive", false);
        PumpDispatcher(TimeSpan.FromMilliseconds(60));
        Assert(GetField<bool>(window, "_isFrameBlending"),
            "真实点击退出淡化开始60ms后仍应处于单buffer中间态");
        PumpDispatcher(TimeSpan.FromMilliseconds(400));
        Assert(!GetField<bool>(window, "_isFrameBlending") &&
               ReferenceEquals(spriteBrush.ImageSource, displayFrameBuffer),
            "真实点击的120ms退出淡化完成后必须停止渲染且不更换ImageSource");
        Invoke(window, "ShowStableFrame", GetField<object>(window, "_idleFrame"));
    }

    private static void AssertRoamPerimeterAndFullLap(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        AssertClose(window.ActualWidth, 190, "绕屏计算使用的宠物实际宽度");
        AssertClose(window.ActualHeight, 242, "绕屏计算使用的宠物实际高度");
        var workArea = new Rect(0, 0, 1920, 1080);
        const double petWidth = 190;
        const double petHeight = 242;
        var expectedPerimeter = 2 * (
            workArea.Width - petWidth + workArea.Height - petHeight);
        var perimeter = (double)InvokeStatic(
            typeof(MainWindow),
            "CalculateRoamPerimeter",
            workArea,
            petWidth,
            petHeight)!;
        AssertClose(perimeter, expectedPerimeter, "完整绕屏周长公式");

        window.Left = workArea.Left;
        window.Top = workArea.Top;
        SetField(window, "_roamLogicalLeft", workArea.Left);
        SetField(window, "_roamLogicalTop", workArea.Top);
        SetField(window, "_roamWorkArea", workArea);
        SetField(window, "_roamBoundaryStart", new Point(workArea.Left, workArea.Top));
        SetField(window, "_roamBoundaryTargetDistance", perimeter);
        SetField(window, "_roamBoundaryTravelled", 0d);
        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Top"));
        SetField(window, "_roamClockwise", true);
        SetField(window, "_roamApproaching", false);
        SetField(window, "_isRoamCornerTurning", false);
        SetField(window, "_isEdgeRoaming", true);
        SetField(window, "_edgeRoamingEnabled", true);
        SetField(window, "_automaticAnimationEnabled", false);

        var horizontal = workArea.Width - petWidth;
        var vertical = workArea.Height - petHeight;
        AdvanceEdgeAndTurn(window, horizontal);
        AdvanceEdgeAndTurn(window, vertical);
        AdvanceEdgeAndTurn(window, horizontal);

        Invoke(window, "AdvanceRoamAlongBoundary", vertical - 1);
        AssertClose(
            GetField<double>(window, "_roamBoundaryTravelled"),
            perimeter - 1,
            "完整一圈结束前的累计距离");
        Assert(GetField<bool>(window, "_isEdgeRoaming"),
            "累计距离不足完整周长时不得提前结束");

        var completionStart = DateTimeOffset.UtcNow;
        Invoke(window, "AdvanceRoamAlongBoundary", 1d);
        Assert(!GetField<bool>(window, "_isEdgeRoaming"),
            "累计距离达到完整周长后必须结束本圈");
        AssertClose(window.Left, workArea.Left, "完整一圈后的 X 位置");
        AssertClose(window.Top, workArea.Top, "完整一圈后的 Y 位置");

        var nextDue = GetField<DateTimeOffset>(window, "_nextRoamDueUtc");
        Assert(nextDue >= completionStart + TimeSpan.FromMinutes(10) &&
               nextDue <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20.1),
            "完整一圈后下一次绕屏应重新随机安排在 10-20 分钟内");
    }

    private static void AdvanceEdgeAndTurn(MainWindow window, double distance)
    {
        Invoke(window, "AdvanceRoamAlongBoundary", distance);
        Assert(GetField<bool>(window, "_isRoamCornerTurning"),
            "到达边界拐角后应进入转角阶段");
        Invoke(
            window,
            "AdvanceRoamCornerTurn",
            TimeSpan.FromMilliseconds(WriggleCornerDurationMilliseconds));
        Assert(!GetField<bool>(window, "_isRoamCornerTurning"),
            $"{WriggleCornerDurationMilliseconds}ms 后应完成转角并继续累计路程");
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

        SetField(window, "_edgeRoamingEnabled", true);
        SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_activeClip", null);
        SetField(window, "_isPillowBreathing", false);
        SetField(window, "_dragInteractionActive", false);
        SetField(window, "_bubbleMode", GetNestedEnum("BubbleMode", "None"));
        Assert((bool)(Invoke(window, "StartEdgeRoaming") ?? false),
            "显示器变化回归测试必须先进入真实绕屏状态");

        var monitorType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.MonitorWorkArea",
            throwOnError: true)!;
        var originalWorkArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
        window.Left = originalWorkArea.Left - originalWorkArea.Width * 3;
        window.Top = originalWorkArea.Top - originalWorkArea.Height * 3;
        var eventStartedAt = DateTimeOffset.UtcNow;
        Invoke(window, "SystemEvents_DisplaySettingsChanged", null, EventArgs.Empty);
        PumpDispatcher(TimeSpan.FromMilliseconds(120));

        Assert(!GetField<bool>(window, "_isEdgeRoaming"),
            "显示器切换或断开后必须先终止旧显示器的绕屏路径");
        var recoveredWorkArea = (Rect)InvokeStatic(monitorType, "GetForWindow", window)!;
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        Assert(window.Left >= recoveredWorkArea.Left - 0.5 &&
               window.Left <= recoveredWorkArea.Right - width + 0.5 &&
               window.Top >= recoveredWorkArea.Top - 0.5 &&
               window.Top <= recoveredWorkArea.Bottom - height + 0.5,
            "显示器切换或断开后桌宠必须被重新夹取到仍有效的工作区内");
        var nextDue = GetField<DateTimeOffset>(window, "_nextRoamDueUtc");
        Assert(nextDue >= eventStartedAt &&
               nextDue <= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1),
            "活动绕屏遇到显示器变化后必须立即重新排队，不能沿用失效路径");
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
        var ordinaryActionFrame = GetField<object>(window, "_currentSpriteFrame");
        Assert((int)InvokeStatic(
                   typeof(MainWindow),
                   "GetTodoEnterStartIndex",
                   ordinaryActionFrame)! == 14,
            "从普通动作打开待办必须直接从wake14接入，不能闪回趴枕头待机");

        var originalRight = window.Left + window.Width;
        var originalBottom = window.Top + window.Height;
        Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
        var ordinaryTodoStartIndex = GetField<int>(window, "_activeFrameIndex");
        var ordinaryTodoStartFrame = GetSpriteFrameInfo(
            GetField<object>(window, "_currentSpriteFrame"));
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
        var todoEnterClip = GetRawField(window, "_activeClip")!;
        Assert(GetProperty<string>(todoEnterClip, "ActionName") == "todo-open",
            "打开 Todo 必须抢占普通动作并启动平滑起身入场");
        Assert(GetField<long>(window, "_activeFrameDeadlineTimestamp") > 0 &&
               GetField<bool>(window, "_isVisualClockSubscribed"),
            "Todo 起身入场期间必须登记绝对帧截止点并订阅统一视觉时钟");
        Assert(ordinaryTodoStartIndex == 14 &&
               ordinaryTodoStartFrame.Name.EndsWith(
                   "luban-wake-14.png",
                   StringComparison.Ordinal),
            "普通动作抢占后的Todo入场首帧必须是wake14");

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

        PumpDispatcher(TimeSpan.FromMilliseconds(1750));
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
        var cornerScale = GetField<ScaleTransform>(window, "PetCornerScale");
        var roamBaseOffset = GetField<TranslateTransform>(window, "PetRoamBaseOffset");
        var roamOffset = GetField<TranslateTransform>(window, "PetRoamOffset");
        AssertClose(facingScale.ScaleX, 1, "Todo 状态水平朝向缩放");
        AssertClose(facingScale.ScaleY, 1, "Todo 状态垂直朝向缩放");
        AssertClose(petScale.ScaleX, 1, "Todo 状态呼吸水平缩放");
        AssertClose(petScale.ScaleY, 1, "Todo 状态呼吸垂直缩放");
        AssertClose(cornerScale.ScaleX, 1, "Todo 状态转角水平缩放");
        AssertClose(cornerScale.ScaleY, 1, "Todo 状态转角垂直缩放");
        AssertClose(roamBaseOffset.X, 0, "Todo 状态绕屏基础 X 偏移");
        AssertClose(roamBaseOffset.Y, 0, "Todo 状态绕屏基础 Y 偏移");
        AssertClose(roamOffset.X, 0, "Todo 状态绕屏动作 X 偏移");
        AssertClose(roamOffset.Y, 0, "Todo 状态绕屏动作 Y 偏移");
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
        PumpDispatcher(TimeSpan.FromMilliseconds(1750));
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

        foreach (var methodName in new[] { "FocusInput", "SetAutoRoam", "SetPetSizeScale" })
        {
            Assert(type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {methodName} 方法");
        }

        foreach (var eventName in new[]
                 {
                     "AddRequested",
                     "TodoChanged",
                     "DeleteRequested",
                     "AutoRoamChanged",
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
                   todoWindow.Background == Brushes.Transparent,
                "TodoWindow 必须为透明背景、无边框且不占任务栏");

            todoWindow.Todos = new ObservableCollection<TodoItem>(
                Enumerable.Range(1, 6)
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
            var adjustmentStartedCount = 0;
            var adjustmentCompletedCount = 0;
            todoWindow.PetSizeScaleChanged += value => sizeEventValue = value;
            todoWindow.PetSizeAdjustmentStarted += () => adjustmentStartedCount++;
            todoWindow.PetSizeAdjustmentCompleted += () => adjustmentCompletedCount++;
            Invoke(todoWindow, "BeginPetSizeAdjustment");
            sizeSlider.Value = 123.4;
            Invoke(todoWindow, "EndPetSizeAdjustment");
            AssertClose(sizeEventValue, 1.234, "尺寸滑块必须连续输出事件值");
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
                "第六行应进入滚动区域而不是撑大窗口");

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

    private static void AssertEnableRoamBecomesDueImmediately(MainWindow window)
    {
        var todoWindow = GetField<TodoWindow>(window, "_todoWindow");
        var toggle = GetField<CheckBox>(todoWindow, "AutoRoamToggle");
        todoWindow.SetAutoRoam(false);
        SetField(window, "_edgeRoamingEnabled", false);
        SetField(window, "_nextRoamDueUtc", DateTimeOffset.UtcNow + TimeSpan.FromHours(1));

        var before = DateTimeOffset.UtcNow;
        toggle.IsChecked = true;
        var due = GetField<DateTimeOffset>(window, "_nextRoamDueUtc");
        Assert(GetField<bool>(window, "_edgeRoamingEnabled"),
            "勾选后应立即启用自动绕屏");
        Assert(due >= before - TimeSpan.FromSeconds(1) &&
               due <= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1),
            "从关闭切换为启用时，绕屏截止时间必须立即到期，以便马上完整绕一圈");
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
        SetField(window, "_edgeRoamingEnabled", true);
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
        Assert(savedAfterSize.EdgeRoamingEnabled,
            "只修改尺寸时不得丢失已开启的绕屏设置");
        AssertClose(savedAfterSize.PetSizeScale, 1.23, "尺寸设置持久化");

        SetField(window, "_edgeRoamingEnabled", true);
        Invoke(window, "ApplyAutoRoamSetting", false);
        var savedAfterRoam = store.Load();
        Assert(!savedAfterRoam.EdgeRoamingEnabled,
            "关闭绕屏设置必须持久化");
        AssertClose(savedAfterRoam.PetSizeScale, 1.23,
            "只修改绕屏开关时不得丢失桌宠尺寸");

        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        var persistedBeforePreview = File.ReadAllText(store.FilePath);
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
        var viewbox = GetField<Viewbox>(window, "PetSizeViewbox");
        var deviceTransform = PresentationSource.FromVisual(window)?.CompositionTarget?
                                  .TransformToDevice ?? Matrix.Identity;
        var physicalVisualWidth = viewbox.ActualWidth * userScale.ScaleX *
                                  deviceTransform.M11;
        var physicalVisualHeight = viewbox.ActualHeight * userScale.ScaleY *
                                   deviceTransform.M22;
        Assert(Math.Abs(physicalVisualWidth - Math.Round(physicalVisualWidth)) < 0.01 &&
               Math.Abs(physicalVisualHeight - Math.Round(physicalVisualHeight)) < 0.01,
            "缩放预览的可见宽高必须对齐当前DPI的物理像素");
        var previewTopLeft = viewbox.PointToScreen(new Point(0, 0));
        var previewBottomRight = viewbox.PointToScreen(
            new Point(viewbox.ActualWidth, viewbox.ActualHeight));
        Assert(
            Math.Abs(previewTopLeft.X - Math.Round(previewTopLeft.X)) < 0.01 &&
            Math.Abs(previewTopLeft.Y - Math.Round(previewTopLeft.Y)) < 0.01 &&
            Math.Abs(previewBottomRight.X - Math.Round(previewBottomRight.X)) < 0.01 &&
            Math.Abs(previewBottomRight.Y - Math.Round(previewBottomRight.Y)) < 0.01,
            "缩放预览的四角必须落在物理像素边界，不得产生半像素光纹");
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
        Invoke(window, "StartPetSizeScaleTransitionAt", 1.38d, secondRetarget);
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

        Invoke(window, "ApplyPetSizeScale", 1d, false, false);
        SetField(window, "_edgeRoamingEnabled", false);
    }

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
