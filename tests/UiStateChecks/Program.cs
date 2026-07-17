using System.Collections;
using System.Collections.ObjectModel;
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
            AssertMotionTimelineContract(window);
            AssertExactEdgeContactContract();
            AssertRoamPerimeterAndFullLap(window);
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
        var viewport = GetField<Canvas>(window, "PetFrameViewport");
        var pageMap = GetField<IDictionary>(window, "_spritePages");
        var spritePageBuffer = GetField<WriteableBitmap>(window, "_spritePageBuffer");

        Assert(pageMap.Count == 13,
            $"运行时必须登记13个图集分页，实际 {pageMap.Count}");
        var maximumPageWidth = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Width"));
        var maximumPageHeight = pageMap.Values.Cast<object>()
            .Max(page => GetProperty<int>(page, "Height"));
        Assert(maximumPageWidth == 1023 && maximumPageHeight == 815,
            $"13页限制在1024像素宽后最大尺寸应为1023×815，实际 {maximumPageWidth}×{maximumPageHeight}");
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
        Assert(spriteBrush.ViewboxUnits == BrushMappingMode.Absolute &&
               spriteBrush.Stretch == Stretch.Fill,
            "分页裁剪必须使用Absolute Viewbox并填充PetImage");
        Assert(window.FindName("PetImageBuffer") is null &&
               window.FindName("PetSpriteBufferBrush") is null &&
               window.FindName("PetImageOverlay") is null,
            "单缓冲方案不得保留第二Surface或旧Overlay图层");

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

        Assert(totalPageFrames == 385,
            $"13个分页应共包含385个PageFrame，实际 {totalPageFrames}");
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
        Assert(root.GetProperty("sourceFrameCount").GetInt32() == 289,
            "分页清单sourceFrameCount必须为289");
        Assert(root.GetProperty("pageFrameCount").GetInt32() == 385,
            "分页清单pageFrameCount必须为385");

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

        Assert(totalPageFrames == 385 && sourceFrames.Count == 289 &&
               pageResources.Count == 13,
            "13页必须覆盖385个PageFrame和289个源逻辑帧");
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
            "csproj不得重新嵌入289张源PNG或旧单atlas");

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
            "主程序集不得包含旧单atlas或289张源PNG");
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

            var expectedResourceNames = Enumerable.Range(1, 12)
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
                $"{actionName} 分页动作应完整使用 idle、12 帧 wake 和 24 帧动作资源");
        }

        foreach (var clip in clips.Where(clip => GetProperty<string>(clip, "ActionName") != "run"))
        {
            var frames = GetClipFrames(clip).Cast<object>().ToArray();
            Assert(frames.Length == 104, "普通动作应为 36 帧进入 + 32 帧微循环 + 36 帧返回");
            Assert(frames.Take(36).All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
                "普通动作进入阶段必须使用 85ms 帧间隔");
            Assert(frames.Skip(frames.Length - 36)
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
        var expectedRunNumbers = Enumerable.Range(1, 16)
            .Concat(Enumerable.Range(0, 8).SelectMany(_ => Enumerable.Range(17, 8)))
            .Append(17)
            .Concat(Enumerable.Range(1, 16).Reverse())
            .ToArray();

        Assert(runNumbers.SequenceEqual(expectedRunNumbers),
            "run 应进入 1-16、正向循环 17-24、从 17 衔接退出，再反向 16-1；不得倒放 24-17");
        Assert(runFrames.Take(28)
                .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
            "run 的苏醒和 1-16 进入阶段必须为 85ms");
        Assert(runFrames.Skip(28).Take(65)
                .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(110)),
            "run 的 17-24 八相位循环和退出衔接应使用 110ms");
        Assert(runFrames.Skip(93)
                .All(frame => GetFrameDuration(frame) == TimeSpan.FromMilliseconds(85)),
            "run 返回阶段必须为 85ms");
    }

    private static int ParseFrameNumber(string name)
    {
        var numberText = Path.GetFileNameWithoutExtension(name)["luban-run-frame-".Length..];
        return int.Parse(numberText);
    }

    private static TimeSpan GetFrameDuration(object frame) =>
        GetProperty<TimeSpan>(frame, "HoldDuration");

    private static Array GetClipFrames(object clip) => GetProperty<Array>(clip, "Frames");

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

        todoWindow.Close();
        PumpDispatcher(TimeSpan.FromMilliseconds(30));
        Assert(!todoWindow.IsVisible,
            "Alt+F4/系统关闭待办窗口时应取消销毁并安全隐藏");
        Assert(GetField<object>(window, "_bubbleMode").ToString() == "None",
            "Alt+F4 收起后 MainWindow 的 BubbleMode 必须同步为 None");

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

    private sealed record RuntimePage(
        string Name,
        string ResourcePath,
        int Width,
        int Height,
        IDictionary Frames);

}
