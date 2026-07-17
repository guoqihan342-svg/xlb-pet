using LubanDesktopPet;

var tempDirectory = Path.Combine(Path.GetTempPath(), "LubanDesktopPetChecks", Guid.NewGuid().ToString("N"));

try
{
    CheckTodoStore(tempDirectory);
    CheckAppSettingsStore(tempDirectory);
    Console.WriteLine("TodoStore and AppSettingsStore checks passed.");
}
finally
{
    if (Directory.Exists(tempDirectory))
    {
        Directory.Delete(tempDirectory, true);
    }
}

static void CheckTodoStore(string tempDirectory)
{
    var filePath = Path.Combine(tempDirectory, "todos.json");
    var store = new TodoStore(filePath);
    var original = new[]
    {
        new TodoItem { Text = "第一件事", IsCompleted = false },
        new TodoItem { Text = "已经完成", IsCompleted = true }
    };

    Assert(store.Save(original), "保存应成功");
    var loaded = store.Load();
    Assert(loaded.Count == 2, "应加载两条待办");
    Assert(loaded[0].Text == "第一件事" && !loaded[0].IsCompleted, "未完成状态应保留");
    Assert(loaded[1].Text == "已经完成" && loaded[1].IsCompleted, "完成状态应保留");

    File.WriteAllText(filePath, "这不是有效 JSON");
    Assert(store.Load().Count == 0, "损坏的数据不应让桌宠崩溃");
}

static void CheckAppSettingsStore(string tempDirectory)
{
    var expectedDefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LubanDesktopPet",
        "settings.json");
    Assert(
        string.Equals(AppSettingsStore.CreateDefault().FilePath, expectedDefaultPath, StringComparison.OrdinalIgnoreCase),
        "默认设置路径应位于 LocalAppData\\LubanDesktopPet\\settings.json");

    var settingsPath = Path.Combine(tempDirectory, "nested", "settings.json");
    var store = new AppSettingsStore(settingsPath);
    Assert(store.Load().EdgeRoamingEnabled, "设置文件缺失时应默认开启绕屏移动");

    Assert(store.Save(new AppSettings { EdgeRoamingEnabled = false }), "首次保存 false 应成功");
    Assert(Directory.Exists(Path.GetDirectoryName(settingsPath)), "保存时应自动创建设置目录");
    Assert(!store.Load().EdgeRoamingEnabled, "false 设置应能往返保存和加载");

    var bytes = File.ReadAllBytes(settingsPath);
    Assert(
        bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF,
        "设置 JSON 应使用无 BOM 的 UTF-8 编码");
    Assert(File.ReadAllText(settingsPath).Contains("\"edgeRoamingEnabled\"", StringComparison.Ordinal),
        "设置 JSON 应使用 camelCase 字段名");

    Assert(store.Save(new AppSettings { EdgeRoamingEnabled = true }), "覆盖保存 true 应成功");
    Assert(store.Load().EdgeRoamingEnabled, "true 设置应能往返保存和加载");
    Assert(!File.Exists(settingsPath + ".tmp"), "成功保存后不应残留临时文件");

    File.WriteAllText(settingsPath, "这不是有效 JSON");
    Assert(store.Load().EdgeRoamingEnabled, "损坏 JSON 应回退到默认开启且不抛异常");

    var blockedPath = Path.Combine(tempDirectory, "blocked-settings.json");
    Directory.CreateDirectory(blockedPath);
    var blockedStore = new AppSettingsStore(blockedPath);
    var saveResult = blockedStore.Save(new AppSettings { EdgeRoamingEnabled = false });
    Assert(!saveResult, "保存失败时应返回 false 而不是抛异常");
    Assert(!File.Exists(blockedPath + ".tmp"), "保存失败后应尽量清理临时文件");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
