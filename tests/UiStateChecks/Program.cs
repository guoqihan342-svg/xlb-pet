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

            AssertDisplayFrameContract(window);
            AssertRoamAssetSequenceContract(window);
            AssertRoamVisualTransitionContract(window);
            AssertMotionTimelineContract(window);
            AssertRunLoopVisualRegistrationContract();
            AssertMotionAssetScaleContract();
            AssertExactEdgeContactContract();
            AssertRoamPerimeterAndFullLap(window);
            AssertUserInterruptedRoamIsRescheduled(window);
            AssertRandomActivityBag(window);
            AssertMonitorWorkAreaContract(window);
            AssertOwnedTodoWindowContract(window);
            AssertTodoWindowLayoutApiAndIme();
            AssertEnableRoamBecomesDueImmediately(window);
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

    private static void AssertDisplayFrameContract(MainWindow window)
    {
        var petImage = GetField<Rectangle>(window, "PetImage");
        var spriteBrush = GetField<ImageBrush>(window, "PetSpriteBrush");
        var transitionImage = GetField<Rectangle>(window, "PetRoamTransitionImage");
        var transitionBrush = GetField<ImageBrush>(window, "PetRoamTransitionBrush");
        var viewport = GetField<Canvas>(window, "PetFrameViewport");
        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var spritePageBuffer = GetField<WriteableBitmap>(window, "_spritePageBuffer");

        Assert(pageMap.Count == 13,
            $"运行时必须登记13个图集分页，实际 {pageMap.Count}");
        var maximumPageWidth = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Width"));
        var maximumPageHeight = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Height"));
        Assert(maximumPageWidth <= 1024 && maximumPageHeight <= 700,
            $"13页图集不得超过1024×700像素，实际 {maximumPageWidth}×{maximumPageHeight}");
        var bitmapFields = typeof(MainWindow).GetFields(InstanceFlags)
                .Select(field => field.GetValue(window))
                .OfType<BitmapSource>()
                .ToArray();
        Assert(bitmapFields.Length == 1 &&
               ReferenceEquals(bitmapFields[0], spritePageBuffer),
            "MainWindow必须只常驻一个_spritePageBuffer位图字段");
        Assert(spritePageBuffer.PixelWidth == maximumPageWidth &&
               spritePageBuffer.PixelHeight == maximumPageHeight &&
               spritePageBuffer.Format == PixelFormats.Pbgra32 &&
               !spritePageBuffer.IsFrozen,
            "复用分页缓冲区必须使用最大分页尺寸、Pbgra32且保持可写");
        Assert(ReferenceEquals(petImage.Fill, spriteBrush) &&
               ReferenceEquals(spriteBrush.ImageSource, spritePageBuffer),
            "PetImage必须永久使用绑定_spritePageBuffer的PetSpriteBrush");
        Assert(ReferenceEquals(transitionImage.Fill, transitionBrush) &&
               ReferenceEquals(transitionBrush.ImageSource, spritePageBuffer),
            "绕屏转角叠层必须复用同一个_spritePageBuffer，不得新增常驻位图");
        Assert(transitionImage.Visibility == Visibility.Collapsed &&
               transitionImage.Opacity == 0,
            "绕屏转角叠层默认必须完全隐藏");
        Assert(spriteBrush.ViewboxUnits == BrushMappingMode.Absolute &&
               spriteBrush.Stretch == Stretch.Fill,
            "分页裁剪必须使用Absolute Viewbox并填充PetImage");
        Assert(window.FindName("PetImageBuffer") is null &&
               window.FindName("PetSpriteBufferBrush") is null &&
               window.FindName("PetImageOverlay") is null,
            "不得恢复旧双位图Surface或旧Overlay图层");

        AssertClose(viewport.Width, 145, "逻辑帧视口宽度");
        AssertClose(viewport.Height, 185, "逻辑帧视口高度");
        Assert(viewport.ClipToBounds, "逻辑帧视口必须裁剪图集其余区域");

        var pages = GetDictionaryEntries(pageMap)
            .Select(entry => new RuntimePage(
                (string)entry.Key,
                GetProperty<string>(entry.Value!, "ResourcePath"),
                GetProperty<int>(entry.Value!, "Width"),
                GetProperty<int>(entry.Value!, "Height"),
                GetProperty<IDictionary>(entry.Value!, "Frames")))
            .ToArray();
        AssertSpritePagesManifestAndResourcesContract(pages);

        var totalPageFrames = 0;
        foreach (var page in pages)
        {
            var pageFrames = GetDictionaryEntries(page.Frames);
            Assert(pageFrames.Length > 0, $"分页 {page.Name} 不得为空");

            Invoke(window, "ShowStableFrame", pageFrames[0].Value);
            Assert(ReferenceEquals(spriteBrush.ImageSource, spritePageBuffer),
                $"切换到 {page.Name} 后ImageSource引用不得改变");
            Assert(GetField<string>(window, "_loadedSpritePageName") == page.Name,
                $"切换后_loadedSpritePageName必须为 {page.Name}");
            AssertBufferMatchesPage(spritePageBuffer, page);

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
                Assert(frame.DestinationX < 145 && frame.DestinationY < 185 &&
                       frame.DestinationX + frame.Width > 0 &&
                       frame.DestinationY + frame.Height > 0,
                    $"{page.Name}/{frame.Name} 必须与145×185显示区相交");

                Invoke(window, "ShowStableFrame", frameEntry.Value);
                Assert(ReferenceEquals(spriteBrush.ImageSource, spritePageBuffer),
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
                spritePageBuffer,
                spriteBrush);
        }

        Assert(totalPageFrames == 401,
            $"13个分页应共包含401个PageFrame，实际 {totalPageFrames}");
        AssertSameFrameReturnsEarly(
            window,
            petImage,
            spriteBrush,
            spritePageBuffer,
            pages[0].Frames.Values.Cast<object>().First());
        Invoke(window, "ShowStableFrame", GetField<object>(window, "_idleFrame"));
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
            new Rect(frame.X, frame.Y, frame.Width, frame.Height),
            $"{frame.PageName}/{frame.Name} Viewbox");
        AssertClose(petImage.Width, frame.Width,
            $"{frame.PageName}/{frame.Name} Rectangle宽度");
        AssertClose(petImage.Height, frame.Height,
            $"{frame.PageName}/{frame.Name} Rectangle高度");
        AssertClose(Canvas.GetLeft(petImage), frame.DestinationX,
            $"{frame.PageName}/{frame.Name} Canvas.Left");
        AssertClose(Canvas.GetTop(petImage), frame.DestinationY,
            $"{frame.PageName}/{frame.Name} Canvas.Top");
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
        WriteableBitmap spritePageBuffer,
        RuntimePage page)
    {
        var resourcePath = FindWorkspaceFile(page.ResourcePath.Split('/'));
        BitmapSource pageBitmap;
        using (var stream = File.OpenRead(resourcePath))
        {
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            Assert(decoder.Frames.Count == 1,
                $"分页PNG必须只有一个图像帧：{page.Name}");
            pageBitmap = decoder.Frames[0];
        }

        Assert(pageBitmap.PixelWidth == page.Width &&
               pageBitmap.PixelHeight == page.Height,
            $"分页PNG尺寸必须匹配运行时元数据：{page.Name}");
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
        var actualPixels = new byte[byteCount];
        var pageBounds = new Int32Rect(0, 0, page.Width, page.Height);
        premultipliedPage.CopyPixels(pageBounds, expectedPixels, stride, 0);
        spritePageBuffer.CopyPixels(pageBounds, actualPixels, stride, 0);
        Assert(actualPixels.AsSpan().SequenceEqual(expectedPixels),
            $"复用缓冲区必须逐像素等于 {page.Name} PNG的Pbgra32内容");
    }

    private static void AssertSamePageDoesNotRewrite(
        MainWindow window,
        RuntimePage page,
        DictionaryEntry[] pageFrames,
        WriteableBitmap spritePageBuffer,
        ImageBrush spriteBrush)
    {
        if (pageFrames.Length < 2)
        {
            return;
        }

        Invoke(window, "ShowStableFrame", pageFrames[0].Value);
        var originalPixel = new byte[4];
        var pixelBounds = new Int32Rect(0, 0, 1, 1);
        spritePageBuffer.CopyPixels(pixelBounds, originalPixel, 4, 0);
        var sentinelPixel = originalPixel.Select(value => (byte)(value ^ 0xff)).ToArray();
        spritePageBuffer.WritePixels(pixelBounds, sentinelPixel, 4, 0);

        Invoke(window, "ShowStableFrame", pageFrames[1].Value);
        var actualPixel = new byte[4];
        spritePageBuffer.CopyPixels(pixelBounds, actualPixel, 4, 0);
        Assert(actualPixel.AsSpan().SequenceEqual(sentinelPixel),
            $"{page.Name} 内切帧不得重新解码或覆写分页缓冲区");
        Assert(ReferenceEquals(spriteBrush.ImageSource, spritePageBuffer) &&
               GetField<string>(window, "_loadedSpritePageName") == page.Name,
            $"{page.Name} 内切帧必须继续复用同一ImageSource和页标记");
        spritePageBuffer.WritePixels(pixelBounds, originalPixel, 4, 0);
    }

    private static void AssertSameFrameReturnsEarly(
        MainWindow window,
        Rectangle petImage,
        ImageBrush spriteBrush,
        WriteableBitmap spritePageBuffer,
        object frameValue)
    {
        Invoke(window, "ShowStableFrame", frameValue);
        var frame = GetSpriteFrameInfo(frameValue);
        var bufferReference = spriteBrush.ImageSource;
        var sentinelViewbox = new Rect(-7, -9, 3, 5);
        spriteBrush.Viewbox = sentinelViewbox;
        petImage.Width = 7;
        petImage.Height = 9;
        Canvas.SetLeft(petImage, -11);
        Canvas.SetTop(petImage, -13);

        Invoke(window, "ShowStableFrame", frameValue);
        AssertRectClose(spriteBrush.Viewbox, sentinelViewbox,
            $"重复显示 {frame.Name} 时应在写入DP前直接返回");
        AssertClose(petImage.Width, 7,
            $"重复显示 {frame.Name} 时不得重写宽度");
        AssertClose(petImage.Height, 9,
            $"重复显示 {frame.Name} 时不得重写高度");
        AssertClose(Canvas.GetLeft(petImage), -11,
            $"重复显示 {frame.Name} 时不得重写Canvas.Left");
        AssertClose(Canvas.GetTop(petImage), -13,
            $"重复显示 {frame.Name} 时不得重写Canvas.Top");
        Assert(ReferenceEquals(bufferReference, spritePageBuffer) &&
               ReferenceEquals(spriteBrush.ImageSource, spritePageBuffer),
            "同帧早退前后ImageSource必须保持唯一缓冲区引用");

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
        Assert(root.GetProperty("version").GetInt32() == 2,
            "分页图集清单版本必须为2");
        Assert(root.GetProperty("displayWidth").GetInt32() == 145 &&
               root.GetProperty("displayHeight").GetInt32() == 185,
            "分页图集显示视口必须为145×185");
        Assert(root.GetProperty("sourceFrameCount").GetInt32() == 291,
            "分页清单sourceFrameCount必须为291");
        Assert(root.GetProperty("pageFrameCount").GetInt32() == 401,
            "分页清单pageFrameCount必须为401");

        var manifestPages = root.GetProperty("pages");
        Assert(manifestPages.EnumerateObject().Count() == 13 && pages.Length == 13,
            "清单与运行时都必须恰好包含13页");
        var runtimeByName = pages.ToDictionary(page => page.Name, StringComparer.Ordinal);
        var pageResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFrames = new HashSet<string>(StringComparer.Ordinal);
        var totalPageFrames = 0;
        foreach (var manifestPageEntry in manifestPages.EnumerateObject())
        {
            Assert(runtimeByName.TryGetValue(manifestPageEntry.Name, out var runtimePage),
                $"运行时缺少分页：{manifestPageEntry.Name}");
            var descriptor = manifestPageEntry.Value;
            var resource = descriptor.GetProperty("resource").GetString()
                ?? throw new InvalidOperationException("分页resource不能为空");
            var width = descriptor.GetProperty("width").GetInt32();
            var height = descriptor.GetProperty("height").GetInt32();
            var logicalCount = descriptor.GetProperty("logicalFrameCount").GetInt32();
            var uniqueCount = descriptor.GetProperty("uniqueSpriteCount").GetInt32();
            var manifestFrames = descriptor.GetProperty("frames");

            Assert(runtimePage!.ResourcePath == resource &&
                   runtimePage.Width == width && runtimePage.Height == height,
                $"运行时分页元数据必须与清单一致：{manifestPageEntry.Name}");
            Assert(runtimePage.Frames.Count == logicalCount &&
                   manifestFrames.EnumerateObject().Count() == logicalCount,
                $"分页帧数必须与清单一致：{manifestPageEntry.Name}");
            totalPageFrames += logicalCount;
            _ = pageResources.Add(resource);

            var pngPath = FindWorkspaceFile(resource.Split('/'));
            using (var stream = File.OpenRead(pngPath))
            {
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                Assert(decoder.Frames.Count == 1 &&
                       decoder.Frames[0].PixelWidth == width &&
                       decoder.Frames[0].PixelHeight == height,
                    $"分页PNG尺寸必须匹配清单：{manifestPageEntry.Name}");
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
                    $"运行时Frame必须与v2清单一致：{manifestPageEntry.Name}/{manifestFrameEntry.Name}");
                _ = uniqueRegions.Add((
                    runtimeFrame.X,
                    runtimeFrame.Y,
                    runtimeFrame.Width,
                    runtimeFrame.Height));
            }

            Assert(uniqueRegions.Count == uniqueCount,
                $"分页uniqueSpriteCount必须与实际区域数一致：{manifestPageEntry.Name}");
        }

        Assert(totalPageFrames == 401 && sourceFrames.Count == 291 &&
               pageResources.Count == 13,
            "13页必须覆盖401个PageFrame和291个源逻辑帧");
        AssertProjectAndAssemblyResourceContract(pageResources);
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
        Assert(includes.Length == 2 &&
               includes.Contains("Assets/sprite-pages/*.png", StringComparer.OrdinalIgnoreCase) &&
               includes.Contains("Assets/luban-sprite-pages.json", StringComparer.OrdinalIgnoreCase),
            "csproj只能嵌入13个分页PNG通配符和v2 manifest");
        Assert(!includes.Any(include =>
                include.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                (include.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                 include.EndsWith("*.png", StringComparison.OrdinalIgnoreCase) &&
                 !include.Contains("sprite-pages/", StringComparison.OrdinalIgnoreCase))),
            "csproj不得重新嵌入291张源PNG或旧单atlas");

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
        Assert(assetKeys.SetEquals(expectedAssets) && assetKeys.Count == 14,
            "主程序集Assets资源必须严格等于13个分页PNG和一个v2 manifest");
        Assert(!assetKeys.Any(key =>
                key.Contains("luban-sprite-atlas", StringComparison.OrdinalIgnoreCase) ||
                (!key.Contains("sprite-pages/", StringComparison.OrdinalIgnoreCase) &&
                 key.EndsWith(".png", StringComparison.OrdinalIgnoreCase))),
            "主程序集不得包含旧单atlas或291张源PNG");
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
            Assert(modes.Length == 3,
                $"{fieldName} 应分别包含蠕动、爬行、走跳 3 种模式");
            var directionName = fieldName switch
            {
                "_roamHorizontalFrames" => "horizontal",
                "_roamVerticalUpFrames" => "vertical-up",
                "_roamVerticalDownFrames" => "vertical-down",
                _ => throw new InvalidOperationException($"未知绕屏序列：{fieldName}")
            };
            var modeNames = new[] { "wriggle", "crawl", "hop" };
            for (var modeIndex = 0; modeIndex < modes.Length; modeIndex++)
            {
                var sequence = modes.GetValue(modeIndex) as Array
                    ?? throw new InvalidOperationException($"{fieldName}[{modeIndex}] 不是帧序列");
                Assert(sequence.Length == 8,
                    $"{fieldName} 的每种模式必须各有 8 个图集 FrameRef");
                for (var frameIndex = 0; frameIndex < sequence.Length; frameIndex++)
                {
                    var frame = GetSpriteFrameInfo(sequence.GetValue(frameIndex)!);
                    var expectedName =
                        $"Assets/luban-roam-{modeNames[modeIndex]}-{directionName}-{frameIndex + 1:00}.png";
                    Assert(frame.Name == expectedName,
                        $"{fieldName}[{modeIndex}][{frameIndex}] 资源顺序不正确：{frame.Name}");
                    Assert(frame.PageName == $"roam-{modeNames[modeIndex]}",
                        $"{expectedName} 必须位于 roam-{modeNames[modeIndex]} 分页");
                    Assert(frame.Width > 0 && frame.Height > 0,
                        $"{frame.Name} 必须指向有效的紧凑图集区域");
                }
            }
        }
    }

    private static void AssertRoamVisualTransitionContract(MainWindow window)
    {
        SetField(window, "_isEdgeRoaming", true);
        SetField(window, "_edgeRoamingEnabled", true);
        SetField(window, "_roamApproaching", false);
        SetField(window, "_roamClockwise", true);
        SetField(window, "_roamMode", GetNestedEnum("RoamMode", "Wriggle"));
        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Top"));
        SetField(window, "_roamVisualEdge", GetNestedEnum("EdgeDock", "None"));
        SetField(window, "_roamVisualDirection",
            GetNestedEnum("RoamVisualDirection", "None"));
        GetField<Stopwatch>(window, "_roamStopwatch").Restart();

        Invoke(window, "UpdateRoamVisual");
        var horizontalFrame = GetSpriteFrameInfo(
            GetField<object>(window, "_currentSpriteFrame"));
        Assert(horizontalFrame.Name.EndsWith("horizontal-01.png", StringComparison.Ordinal),
            "绕屏首次进入横边时必须从接触相位第1帧开始");

        SetField(window, "_roamEdge", GetNestedEnum("EdgeDock", "Right"));
        Invoke(window, "UpdateRoamVisual");
        var verticalFrame = GetSpriteFrameInfo(
            GetField<object>(window, "_currentSpriteFrame"));
        Assert(verticalFrame.Name.EndsWith("vertical-down-01.png", StringComparison.Ordinal),
            "横向转竖向时必须重置到新方向第1帧，不能继承随机相位");

        var overlay = GetField<Rectangle>(window, "PetRoamTransitionImage");
        var overlayBrush = GetField<ImageBrush>(window, "PetRoamTransitionBrush");
        Assert(overlay.Visibility == Visibility.Visible && overlay.Opacity == 1,
            "方向切换时旧姿势必须进入短交叉过渡叠层");
        Assert(ReferenceEquals(
                overlayBrush.ImageSource,
                GetField<WriteableBitmap>(window, "_spritePageBuffer")),
            "交叉过渡不得分配第二张位图");

        var startedAt = GetField<TimeSpan>(window, "_roamVisualTransitionStartedAt");
        Invoke(
            window,
            "UpdateRoamVisualTransition",
            startedAt + TimeSpan.FromMilliseconds(90));
        Assert(overlay.Visibility == Visibility.Visible &&
               overlay.Opacity > 0.45 && overlay.Opacity < 0.55,
            "180ms 转角过渡的中点必须平滑淡出，不能瞬切");
        Invoke(
            window,
            "UpdateRoamVisualTransition",
            startedAt + TimeSpan.FromMilliseconds(181));
        Assert(overlay.Visibility == Visibility.Collapsed && overlay.Opacity == 0,
            "转角过渡结束后叠层必须完全收回，避免残影和内存滞留");

        Invoke(
            window,
            "StopEdgeRoaming",
            "测试清理",
            false,
            false,
            true);
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
        Assert(clips.Length == 8, "应保留 8 组动作");

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

            var expectedActionFrameNumbers = actionName == "run"
                ? Enumerable.Range(9, 16)
                : Enumerable.Range(1, 24);
            var expectedResourceNames = Enumerable.Range(1, 14)
                .Select(frameNumber => $"Assets/luban-wake-{frameNumber:00}.png")
                .Prepend("Assets/luban-idle.png")
                .Concat(expectedActionFrameNumbers
                    .Select(frameNumber =>
                        $"Assets/luban-{actionName}-frame-{frameNumber:00}.png"))
                .ToHashSet(StringComparer.Ordinal);
            var actualResourceNames = spriteFrames
                .Select(frame => frame.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert(actualResourceNames.SetEquals(expectedResourceNames),
                actionName == "run"
                    ? "run 应只使用 idle、14 帧 wake 和新生成的 9-24 十六相位循环，避开姿态异常的旧跑步帧"
                    : $"{actionName} 分页动作应完整使用 idle、14 帧 wake 和 24 帧动作资源");
        }

        foreach (var clip in clips.Where(clip => GetProperty<string>(clip, "ActionName") != "run"))
        {
            var frames = GetClipFrames(clip).Cast<object>().ToArray();
            Assert(frames.Length == 108, "普通动作应为 38 帧进入 + 32 帧微循环 + 38 帧返回");
            Assert(frames.Take(38).All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
                "普通动作进入阶段必须使用 85ms 帧间隔");
            Assert(frames.Skip(frames.Length - 38)
                    .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
                "普通动作返回阶段必须使用 85ms 帧间隔");
        }

        var runClip = clips.Single(clip =>
            GetProperty<string>(clip, "ActionName") == "run");
        var runFrames = GetClipFrames(runClip).Cast<object>().ToArray();
        var runNumbers = runFrames
            .Select(frame => GetProperty<string>(frame, "Name"))
            .Where(name => name.StartsWith("luban-run-frame-", StringComparison.Ordinal))
            .Select(ParseFrameNumber)
            .ToArray();
        var expectedRunNumbers = Enumerable.Range(0, 6)
            .SelectMany(_ => Enumerable.Range(9, 16))
            .Append(9)
            .ToArray();

        Assert(runNumbers.SequenceEqual(expectedRunNumbers),
            "run 应正向循环 9-24，并落到下一周期的 9 接触相位；不得倒放跑步或混入旧踢腿姿势");
        Assert(runFrames.Length == 126,
            "run 应为 14 帧苏醒 + 96 帧十六相位循环 + 1 帧落稳 + 15 帧自然收尾");
        Assert(runFrames.Take(14)
                .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
            "run 的苏醒阶段必须为 85ms");
        Assert(runFrames.Skip(14).Take(97)
                .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(70)),
            "run 的 9-24 十六相位循环和接触落稳帧必须使用 70ms");
        Assert(runFrames.Skip(111)
                .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
            "run 的苏醒逆向收尾必须为 85ms");

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

    private static int ParseFrameNumber(string name)
    {
        var numberText = Path.GetFileNameWithoutExtension(name)["luban-run-frame-".Length..];
        return int.Parse(numberText);
    }

    private static TimeSpan GetFrameDuration(object frame) =>
        GetProperty<TimeSpan>(frame, "HoldDuration");

    private static Array GetClipFrames(object clip) => GetProperty<Array>(clip, "Frames");

    private static void AssertRunLoopVisualRegistrationContract()
    {
        var metrics = Enumerable.Range(9, 16)
            .Select(frameNumber => ReadRunAlphaMetrics(
                FindWorkspaceFile(
                    "Assets",
                    $"luban-run-frame-{frameNumber:00}.png"),
                frameNumber))
            .ToArray();

        Assert(metrics.Max(metric => metric.HeadWidth) -
               metrics.Min(metric => metric.HeadWidth) <= 5,
            "16 相位跑步的头部宽度波动必须控制在 5 个源像素内");
        Assert(metrics.Max(metric => metric.HeadCenterX) -
               metrics.Min(metric => metric.HeadCenterX) <= 1,
            "16 相位跑步的头部水平锚点必须稳定在 1 个源像素内");
        Assert(metrics.Max(metric => metric.HeadTop) -
               metrics.Min(metric => metric.HeadTop) <= 13,
            "16 相位跑步的头部上下起伏必须控制在约 4 个显示像素内");
        Assert(metrics.Max(metric => metric.CentroidX) -
               metrics.Min(metric => metric.CentroidX) <= 11,
            "16 相位跑步的整体水平重心摆动必须控制在约 4 个显示像素内");
        Assert(metrics.Max(metric => metric.CentroidY) -
               metrics.Min(metric => metric.CentroidY) <= 21,
            "16 相位跑步的整体垂直重心摆动必须控制在约 7 个显示像素内");

        var first = metrics[0];
        var last = metrics[^1];
        Assert(Math.Abs(first.CentroidX - last.CentroidX) <= 4 &&
               Math.Abs(first.CentroidY - last.CentroidY) <= 10,
            "16 -> 1 循环接缝的重心位移必须与普通相邻帧一样平滑");
    }

    private static RunAlphaMetrics ReadRunAlphaMetrics(string path, int frameNumber)
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
            bitmap = new FormatConvertedBitmap(
                bitmap,
                PixelFormats.Bgra32,
                null,
                0);
        }

        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var left = bitmap.PixelWidth;
        var top = bitmap.PixelHeight;
        var right = -1;
        var bottom = -1;
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
            }
        }

        Assert(right >= left && bottom >= top,
            $"run-frame-{frameNumber:00} 必须包含可见像素");
        var headBottom = top + Math.Max(1, (bottom - top + 1) / 2);
        var headLeft = bitmap.PixelWidth;
        var headRight = -1;
        long centroidXSum = 0;
        long centroidYSum = 0;
        long centroidCount = 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var alpha = pixels[y * stride + x * 4 + 3];
                if (y < headBottom && alpha > 16)
                {
                    headLeft = Math.Min(headLeft, x);
                    headRight = Math.Max(headRight, x);
                }

                if (alpha < 128)
                {
                    continue;
                }

                centroidXSum += x;
                centroidYSum += y;
                centroidCount++;
            }
        }

        Assert(headRight >= headLeft && centroidCount > 0,
            $"run-frame-{frameNumber:00} 必须包含稳定的头部与不透明主体");
        return new RunAlphaMetrics(
            frameNumber,
            headRight - headLeft + 1,
            (headLeft + headRight) / 2d,
            top,
            centroidXSum / (double)centroidCount,
            centroidYSum / (double)centroidCount);
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
        Assert(Math.Abs(leftPeek.Average(metric => metric.BrimWidth) - idle.BrimWidth) <= 35 &&
               Math.Abs(bottomPeek.Average(metric => metric.BrimWidth) - idle.BrimWidth) <= 35,
            "左、下边缘探头的头部不得再比待机放大约 75%-80%");
        Assert(leftPeek.Max(metric => metric.BrimWidth) -
               leftPeek.Min(metric => metric.BrimWidth) <= 10 &&
               bottomPeek.Max(metric => metric.BrimWidth) -
               bottomPeek.Min(metric => metric.BrimWidth) <= 10,
            "探头四帧内部尺度必须稳定");

        foreach (var mode in new[] { "wriggle", "crawl", "hop" })
        {
            var directionAverages = new List<double>();
            foreach (var direction in new[] { "horizontal", "vertical-up", "vertical-down" })
            {
                var metrics = Enumerable.Range(1, 8)
                    .Select(frameNumber => ReadSpriteVisualMetrics(FindWorkspaceFile(
                        "Assets",
                        $"luban-roam-{mode}-{direction}-{frameNumber:00}.png")))
                    .ToArray();
                directionAverages.Add(metrics.Average(metric => metric.BrimWidth));
                Assert(metrics.Max(metric => metric.VisibleWidth) -
                       metrics.Min(metric => metric.VisibleWidth) <= 36 &&
                       metrics.Max(metric => metric.VisibleHeight) -
                       metrics.Min(metric => metric.VisibleHeight) <= 12,
                    $"{mode}/{direction} 循环内部不得忽大忽小");
            }

            Assert(directionAverages.Max() - directionAverages.Min() <= 20,
                $"{mode} 绕过拐角时横向、上行、下行头部尺度必须接近");
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
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    var x = index % bitmap.PixelWidth;
                    var y = index / bitmap.PixelWidth;
                    count++;
                    componentLeft = Math.Min(componentLeft, x);
                    componentRight = Math.Max(componentRight, x);
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
                }
            }
        }

        Assert(bestWidth > 0, $"精灵必须包含可检测的蓝色帽檐：{path}");
        return new SpriteVisualMetrics(
            bestWidth,
            (bestLeft + bestRight) / 2d,
            left,
            top,
            right,
            bottom);
    }

    private static void AssertExactEdgeContactContract()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        const double width = 145;
        const double height = 185;
        const double safeX = 500;
        const double safeY = 300;

        var cases = new[]
        {
            new EdgeCase("Left", new Rect(1.1, safeY, width, height),
                new Rect(1.0, safeY, width, height)),
            new EdgeCase("Right", new Rect(workArea.Right - width - 1.1, safeY, width, height),
                new Rect(workArea.Right - width - 1.0, safeY, width, height)),
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

        foreach (var top in new[]
                 {
                     new Rect(safeX, 1, width, height),
                     new Rect(safeX, 0, width, height),
                     new Rect(safeX, -5, width, height)
                 })
        {
            var topResult = InvokeStatic(
                typeof(MainWindow),
                "FindTouchedEdge",
                workArea,
                top,
                1d)!;
            Assert(topResult.ToString() == "None",
                "拖到屏幕顶部、贴顶或轻微越界都不得触发手动吸附");
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

    private static void AssertUserInterruptedRoamIsRescheduled(MainWindow window)
    {
        SetField(window, "_isEdgeRoaming", true);
        SetField(window, "_edgeRoamingEnabled", true);
        SetField(window, "_roamBoundaryTargetDistance", 1000d);
        SetField(window, "_roamBoundaryTravelled", 120d);
        SetField(window, "_nextRoamDueUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        GetField<DispatcherTimer>(window, "_roamTimer").Start();

        var interruptedAt = DateTimeOffset.UtcNow;
        Invoke(
            window,
            "StopEdgeRoaming",
            "测试用户点击或拖动",
            false,
            true,
            true);
        Assert(!GetField<bool>(window, "_isEdgeRoaming") &&
               !GetField<DispatcherTimer>(window, "_roamTimer").IsEnabled,
            "用户点击或拖动必须立即停止绕屏及其 Render Timer");
        var nextDue = GetField<DateTimeOffset>(window, "_nextRoamDueUtc");
        Assert(nextDue >= interruptedAt + TimeSpan.FromMinutes(10) &&
               nextDue <= DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20.1),
            "用户打断绕屏后下一圈必须重新安排到 10-20 分钟后");

        Invoke(window, "RestartAutomaticCountdown");
        PumpDispatcher(TimeSpan.FromMilliseconds(250));
        Assert(!GetField<bool>(window, "_isEdgeRoaming"),
            "用户打断后 RestartAutomaticCountdown 不得在 100ms 内重新启动绕屏");
    }

    private static void AssertRoamPerimeterAndFullLap(MainWindow window)
    {
        if (!window.IsVisible)
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
        }

        AssertClose(window.ActualWidth, 145, "绕屏计算使用的宠物实际宽度");
        AssertClose(window.ActualHeight, 185, "绕屏计算使用的宠物实际高度");
        var workArea = new Rect(0, 0, 1920, 1080);
        const double petWidth = 145;
        const double petHeight = 185;
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
        Invoke(window, "AdvanceRoamCornerTurn", TimeSpan.FromMilliseconds(320));
        Assert(!GetField<bool>(window, "_isRoamCornerTurning"),
            "320ms 后应完成转角并继续累计路程");
    }

    private static void AssertRandomActivityBag(MainWindow window)
    {
        var activityCount = GetField<Array>(window, "_automaticActivities").Length;
        Assert(activityCount == 9, "自动活动袋应包含 8 个角色动作和 1 个呼吸动作");

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

    private static void AssertOwnedTodoWindowContract(MainWindow window)
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

        var originalRight = window.Left + window.Width;
        var originalBottom = window.Top + window.Height;
        Invoke(window, "SetBubbleMode", GetNestedEnum("BubbleMode", "Todo"));
        PumpDispatcher(TimeSpan.FromMilliseconds(30));

        Assert(todoWindow.IsVisible, "进入 Todo 模式应显示独立 modeless 待办窗口");
        Assert(ReferenceEquals(todoWindow.Owner, window), "显示后 Owner 关系必须保持");
        Assert(!GetField<Popup>(window, "BubblePopup").IsOpen,
            "Todo 模式不得再打开旧 BubblePopup");
        AssertClose(window.Width, 145, "显示待办时主窗口宽度");
        AssertClose(window.Height, 185, "显示待办时主窗口高度");
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
        Assert(GetField<DispatcherTimer>(window, "_frameTimer").IsEnabled,
            "Todo 起身入场期间动作计时器必须运行");

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
        Assert(GetRawField(window, "_activeClip") is null &&
               !GetField<DispatcherTimer>(window, "_frameTimer").IsEnabled,
            "Todo 起身入场完成后必须停止换帧并释放活动 clip");
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
        Assert(!GetField<DispatcherTimer>(window, "_edgePeekTimer").IsEnabled,
            "Todo 打开时边缘探头计时器必须保持停止");
        Assert(Equals(GetField<object>(window, "_currentSpriteFrame"), todoFrameObject),
            "Todo 打开时拖到屏幕边缘仍应保持专用思考姿势");

        SetField(window, "_edgeDock", GetNestedEnum("EdgeDock", "Left"));
        GetField<DispatcherTimer>(window, "_edgePeekTimer").Start();
        Invoke(window, "EdgePeekTimer_Tick", null, EventArgs.Empty);
        Assert(GetField<object>(window, "_edgeDock").ToString() == "None" &&
               !GetField<DispatcherTimer>(window, "_edgePeekTimer").IsEnabled,
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
        Assert(!GetField<DispatcherTimer>(window, "_edgePeekTimer").IsEnabled,
            "Todo 收起过渡与边缘探头计时器不得并行写画面");

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

    private static void AssertTodoWindowLayoutApiAndIme()
    {
        var type = typeof(TodoWindow);
        foreach (var propertyName in new[] { "Todos", "IsImeComposing" })
        {
            Assert(type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public) is not null,
                $"TodoWindow 应公开 {propertyName} 属性");
        }

        foreach (var methodName in new[] { "FocusInput", "SetAutoRoam" })
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
            AssertClose(todoWindow.Height, 306, "TodoWindow 总高度");
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

    private static void AssertLoggingContract()
    {
        var loggerType = typeof(MainWindow).Assembly.GetType(
            "LubanDesktopPet.AppLogger",
            throwOnError: true)!;
        var probe = $"ui-state-check-{Guid.NewGuid():N}";
        InvokeStatic(loggerType, "Initialize");
        InvokeStatic(loggerType, "Info", probe);
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

    private sealed record RunAlphaMetrics(
        int FrameNumber,
        int HeadWidth,
        double HeadCenterX,
        int HeadTop,
        double CentroidX,
        double CentroidY);

    private sealed record SpriteVisualMetrics(
        int BrimWidth,
        double BrimCenterX,
        int Left,
        int Top,
        int Right,
        int Bottom)
    {
        public int VisibleWidth => Right - Left + 1;
        public int VisibleHeight => Bottom - Top + 1;
    }

    private sealed record RuntimePage(
        string Name,
        string ResourcePath,
        int Width,
        int Height,
        IDictionary Frames);

}
