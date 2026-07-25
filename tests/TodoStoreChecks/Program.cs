using LubanDesktopPet;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

var tempDirectory = Path.Combine(Path.GetTempPath(), "LubanDesktopPetChecks", Guid.NewGuid().ToString("N"));

try
{
    CheckTodoStore(tempDirectory);
    CheckAppSettingsStore(tempDirectory);
    CheckScheduledTaskStore(tempDirectory);
    Console.WriteLine("TodoStore, AppSettingsStore, and ScheduledTaskStore checks passed.");
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

    var reordered = new ObservableCollection<TodoItem>(loaded);
    reordered.Move(1, 0);
    reordered[0].Text = "修改后的已完成事项";
    Assert(store.Save(reordered), "拖拽重排并修改文字后应保存成功");

    var reorderedReloaded = store.Load();
    Assert(reorderedReloaded.Count == 2, "重排并修改后仍应加载两条待办");
    Assert(reorderedReloaded[0].Text == "修改后的已完成事项" &&
           reorderedReloaded[0].IsCompleted,
        "持久化必须保留 ObservableCollection 的最新顺序、修改文字和完成状态");
    Assert(reorderedReloaded[1].Text == "第一件事" &&
           !reorderedReloaded[1].IsCompleted,
        "拖拽后的第二项必须按集合当前顺序保存，不能回到旧索引");

    File.WriteAllText(filePath, "这不是有效 JSON");
    Assert(store.Load().Count == 0, "损坏的数据不应让桌宠崩溃");

    var blockedPath = Path.Combine(tempDirectory, "blocked-todos.json");
    Directory.CreateDirectory(blockedPath);
    var blockedStore = new TodoStore(blockedPath);
    Assert(!blockedStore.Save(original), "目标路径为目录时待办保存必须安全失败");
    Assert(!File.Exists(blockedPath + ".tmp"),
        "待办保存失败后不得留下可无限积累的临时文件");
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
    var defaults = store.Load();
    Assert(defaults.EdgeRoamingEnabled, "设置文件缺失时应默认开启绕屏动画");
    AssertClose(defaults.PetSizeScale, 1.0, "设置文件缺失时桌宠尺寸应为 100%");

    Assert(store.Save(new AppSettings
    {
        EdgeRoamingEnabled = false,
        PetSizeScale = 1.25
    }), "首次保存绕屏开关和尺寸设置应成功");
    Assert(Directory.Exists(Path.GetDirectoryName(settingsPath)), "保存时应自动创建设置目录");
    var firstLoaded = store.Load();
    Assert(!firstLoaded.EdgeRoamingEnabled, "关闭绕屏动画应能往返保存和加载");
    AssertClose(firstLoaded.PetSizeScale, 1.25, "125% 尺寸应能往返保存和加载");

    var bytes = File.ReadAllBytes(settingsPath);
    Assert(
        bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF,
        "设置 JSON 应使用无 BOM 的 UTF-8 编码");
    var firstJson = File.ReadAllText(settingsPath);
    Assert(firstJson.Contains("\"edgeRoamingEnabled\"", StringComparison.Ordinal),
        "设置 JSON 必须持久化 camelCase 的绕屏开关字段");
    Assert(firstJson.Contains("\"petSizeScale\"", StringComparison.Ordinal),
        "设置 JSON 必须持久化 camelCase 的桌宠尺寸字段");

    Assert(store.Save(new AppSettings
    {
        EdgeRoamingEnabled = true,
        PetSizeScale = 0.75
    }), "覆盖保存开启绕屏和尺寸下限应成功");
    var secondLoaded = store.Load();
    Assert(secondLoaded.EdgeRoamingEnabled, "开启绕屏动画应能往返保存和加载");
    AssertClose(secondLoaded.PetSizeScale, 0.75, "尺寸下限应能往返保存和加载");
    Assert(!File.Exists(settingsPath + ".tmp"), "成功保存后不应残留临时文件");

    File.WriteAllText(settingsPath, "{\"edgeRoamingEnabled\":false,\"petSizeScale\":1.1}");
    var legacySettings = store.Load();
    Assert(!legacySettings.EdgeRoamingEnabled,
        "旧版 JSON 中关闭的绕屏开关应继续保留");
    AssertClose(legacySettings.PetSizeScale, 1.1,
        "旧版 JSON 应同时保留桌宠尺寸");
    Assert(store.Save(legacySettings), "加载旧版 JSON 后应能按当前格式保存");
    var migratedJson = File.ReadAllText(settingsPath);
    Assert(migratedJson.Contains("\"edgeRoamingEnabled\": false", StringComparison.Ordinal),
        "旧版 JSON 保存后应继续持久化关闭的绕屏开关");
    Assert(migratedJson.Contains("\"petSizeScale\"", StringComparison.Ordinal),
        "旧版 JSON 保存后应继续保留尺寸字段");

    File.WriteAllText(settingsPath, "{\"petSizeScale\":1.15}");
    var missingRoamingSetting = store.Load();
    Assert(missingRoamingSetting.EdgeRoamingEnabled,
        "当前版本缺少绕屏字段的 JSON 应平滑迁移为默认开启");
    AssertClose(missingRoamingSetting.PetSizeScale, 1.15,
        "迁移缺少绕屏字段的 JSON 时不得丢失桌宠尺寸");

    File.WriteAllText(settingsPath, "这不是有效 JSON");
    var corruptedFallback = store.Load();
    Assert(corruptedFallback.EdgeRoamingEnabled,
        "损坏 JSON 应回退到默认开启绕屏动画");
    AssertClose(corruptedFallback.PetSizeScale, 1.0,
        "损坏 JSON 应回退到 100% 桌宠尺寸");

    var blockedPath = Path.Combine(tempDirectory, "blocked-settings.json");
    Directory.CreateDirectory(blockedPath);
    var blockedStore = new AppSettingsStore(blockedPath);
    var saveResult = blockedStore.Save(new AppSettings
    {
        EdgeRoamingEnabled = false,
        PetSizeScale = 1.2
    });
    Assert(!saveResult, "保存失败时应返回 false 而不是抛异常");
    Assert(!File.Exists(blockedPath + ".tmp"), "保存失败后应尽量清理临时文件");
}

static void CheckScheduledTaskStore(string tempDirectory)
{
    var expectedDefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LubanDesktopPet",
        "scheduled-tasks.json");
    Assert(
        string.Equals(
            ScheduledTaskStore.CreateDefault().FilePath,
            expectedDefaultPath,
            StringComparison.OrdinalIgnoreCase),
        "默认定时任务路径应位于 LocalAppData\\LubanDesktopPet\\scheduled-tasks.json");

    var filePath = Path.Combine(tempDirectory, "scheduled", "scheduled-tasks.json");
    var store = new ScheduledTaskStore(filePath);
    Assert(store.Load().Count == 0, "定时任务文件不存在时应返回空列表");
    Assert(store.TryLoad(out var missingItems) && missingItems.Count == 0,
        "TryLoad 遇到不存在的定时任务文件时应返回 true 和空集合");

    var earlyId = Guid.Parse("00000000-0000-0000-0000-000000000010");
    var sameCreatedFirstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var sameCreatedSecondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var laterId = Guid.Parse("00000000-0000-0000-0000-000000000020");
    var dueAt = new DateTimeOffset(
            2026,
            7,
            22,
            18,
            30,
            45,
            TimeSpan.FromHours(8))
        .AddMilliseconds(987);
    var createdAt = new DateTimeOffset(
            2026,
            7,
            20,
            9,
            10,
            11,
            TimeSpan.FromHours(8))
        .AddMilliseconds(654);
    var original = new ScheduledTaskItem[]
    {
        new()
        {
            Id = laterId,
            Text = "稍后提醒",
            DueAt = dueAt.AddSeconds(1),
            CreatedAt = createdAt
        },
        new()
        {
            Id = sameCreatedSecondId,
            Text = "同秒第二条",
            DueAt = dueAt,
            CreatedAt = createdAt
        },
        new()
        {
            Id = earlyId,
            Text = "  提前提醒  ",
            DueAt = dueAt.AddSeconds(-1),
            CreatedAt = createdAt.AddSeconds(2)
        },
        new()
        {
            Id = sameCreatedFirstId,
            Text = "同秒第一条",
            DueAt = dueAt,
            CreatedAt = createdAt
        },
        new()
        {
            Id = sameCreatedFirstId,
            Text = "重复 ID 应被忽略",
            DueAt = dueAt.AddMinutes(2),
            CreatedAt = createdAt
        },
        new()
        {
            Id = Guid.Empty,
            Text = "空 ID 应被忽略",
            DueAt = dueAt,
            CreatedAt = createdAt
        },
        new()
        {
            Id = Guid.NewGuid(),
            Text = "   ",
            DueAt = dueAt,
            CreatedAt = createdAt
        },
        null!
    };

    Assert(store.Save(original), "定时任务首次保存应成功");
    Assert(Directory.Exists(Path.GetDirectoryName(filePath)), "保存定时任务时应创建数据目录");
    Assert(!File.Exists(filePath + ".tmp"), "保存定时任务后不应残留临时文件");

    var bytes = File.ReadAllBytes(filePath);
    Assert(
        bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF,
        "定时任务 JSON 应使用无 BOM 的 UTF-8 编码");
    var savedJson = File.ReadAllText(filePath);
    Assert(savedJson.Contains("\"dueAt\"", StringComparison.Ordinal) &&
           savedJson.Contains("\"createdAt\"", StringComparison.Ordinal),
        "定时任务 JSON 应使用 camelCase 时间字段");

    var loaded = store.Load();
    Assert(loaded.Count == 4, "保存时应清理空文字、空 ID、重复 ID 和空项");
    Assert(store.TryLoad(out var tryLoaded) &&
           tryLoaded.Select(item => item.Id)
               .SequenceEqual(loaded.Select(item => item.Id)),
        "TryLoad 遇到有效 JSON 时应返回 true 并产生与 Load 一致的稳定顺序");
    Assert(loaded[0].Id == earlyId && loaded[0].Text == "提前提醒",
        "定时任务应按 UTC 到期时间排序并去除文字首尾空白");
    Assert(loaded[1].Id == sameCreatedFirstId &&
           loaded[2].Id == sameCreatedSecondId &&
           loaded[3].Id == laterId,
        "同秒任务应再按创建时间和 ID 稳定排序");
    Assert(loaded.All(item =>
            item.DueAt.Ticks % TimeSpan.TicksPerSecond == 0),
        "加载后的到期时间必须精确到整秒");
    Assert(loaded[1].CreatedAt == createdAt,
        "创建时间必须保留亚秒精度，保证同秒新增任务仍按真实创建顺序排序");
    Assert(loaded[1].DueAt.Offset == TimeSpan.FromHours(8),
        "DateTimeOffset 往返保存必须保留时区偏移");

    var chineseCulture = CultureInfo.GetCultureInfo("zh-CN");
    var localDueAt = loaded[1].DueAt.ToLocalTime();
    Assert(
        loaded[1].DueAtDisplayText ==
            localDueAt.ToString("yyyy年M月d日 ddd HH:mm:ss", chineseCulture) &&
        loaded[1].DueDateDisplayText ==
            localDueAt.ToString("M月d日 ddd", chineseCulture) &&
        loaded[1].DueTimeDisplayText ==
            localDueAt.ToString("HH:mm:ss", chineseCulture),
        "定时任务应提供可直接用于 UI 绑定的中文本地日期和秒级时间");
    Assert(store.Save(loaded.Reverse()), "定时任务应能原子覆盖旧文件");
    var overwritten = store.Load();
    Assert(overwritten.Count == 4 &&
           overwritten.Select(item => item.Id).SequenceEqual(loaded.Select(item => item.Id)),
        "覆盖保存后仍应按统一的时间顺序加载");
    Assert(!File.Exists(filePath + ".tmp"), "原子覆盖后不应残留临时文件");

    var edited = overwritten.Single(item => item.Id == laterId);
    var editedId = edited.Id;
    var editedCreatedAt = edited.CreatedAt;
    var requestedEarlierDueAt = dueAt.AddMinutes(-10).AddMilliseconds(222);
    var expectedEarlierDueAt = requestedEarlierDueAt.AddTicks(
        -(requestedEarlierDueAt.Ticks % TimeSpan.TicksPerSecond));
    edited.Text = "  修改后提前提醒  ";
    edited.DueAt = requestedEarlierDueAt;
    Assert(store.Save(overwritten), "修改定时任务到更早时间后应保存成功");
    var earlierReloaded = store.Load();
    Assert(earlierReloaded.Count == 4 &&
           earlierReloaded[0].Id == editedId &&
           earlierReloaded[0].Text == "修改后提前提醒" &&
           earlierReloaded[0].DueAt == expectedEarlierDueAt &&
           earlierReloaded[0].CreatedAt == editedCreatedAt,
        "编辑到更早时间必须保留 Id/CreatedAt、Trim文字、归一到整秒并移动到磁盘首位");

    var editedAgain = earlierReloaded.Single(item => item.Id == editedId);
    var requestedLaterDueAt = dueAt.AddMinutes(10).AddMilliseconds(444);
    var expectedLaterDueAt = requestedLaterDueAt.AddTicks(
        -(requestedLaterDueAt.Ticks % TimeSpan.TicksPerSecond));
    editedAgain.Text = "修改后延后提醒";
    editedAgain.DueAt = requestedLaterDueAt;
    Assert(store.Save(earlierReloaded), "修改定时任务到更晚时间后应覆盖保存成功");
    var laterReloaded = store.Load();
    Assert(laterReloaded.Count == 4 &&
           laterReloaded[^1].Id == editedId &&
           laterReloaded[^1].Text == "修改后延后提醒" &&
           laterReloaded[^1].DueAt == expectedLaterDueAt &&
           laterReloaded[^1].CreatedAt == editedCreatedAt &&
           !File.Exists(filePath + ".tmp"),
        "编辑到更晚时间必须保留身份与创建顺序、移动到磁盘末位且不残留临时文件");

    var duplicateId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    var validAfterInvalidDuplicateId =
        Guid.Parse("10000000-0000-0000-0000-000000000002");
    var rawRecords = new object[]
    {
        new
        {
            Id = duplicateId,
            Text = "保留的重复项",
            DueAt = dueAt.AddHours(2),
            CreatedAt = createdAt
        },
        new
        {
            Id = duplicateId,
            Text = "后续重复项",
            DueAt = dueAt.AddHours(-2),
            CreatedAt = createdAt
        },
        new
        {
            Id = Guid.Empty,
            Text = "空 ID",
            DueAt = dueAt,
            CreatedAt = createdAt
        },
        new
        {
            Id = validAfterInvalidDuplicateId,
            Text = "   ",
            DueAt = dueAt,
            CreatedAt = createdAt
        },
        new
        {
            Id = validAfterInvalidDuplicateId,
            Text = "无效项不应占用 ID",
            DueAt = dueAt.AddHours(1),
            CreatedAt = createdAt
        },
        new
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000003"),
            Text = "缺失到期时间",
            DueAt = default(DateTimeOffset),
            CreatedAt = createdAt
        }
    };
    File.WriteAllText(filePath, JsonSerializer.Serialize(rawRecords));
    var cleaned = store.Load();
    Assert(cleaned.Count == 2,
        "加载时应过滤空文字、空 ID、重复 ID 和缺失到期时间的记录");
    Assert(cleaned.Any(item =>
               item.Id == duplicateId && item.Text == "保留的重复项") &&
           cleaned.Any(item =>
               item.Id == validAfterInvalidDuplicateId &&
               item.Text == "无效项不应占用 ID"),
        "重复 ID 应保留第一条有效记录，无效记录不应抢占 ID");
    Assert(cleaned.All(item =>
            item.DueAt.Ticks % TimeSpan.TicksPerSecond == 0),
        "直接加载带毫秒的 JSON 时也必须把到期时间统一截断到整秒");
    Assert(cleaned.All(item => item.CreatedAt.Millisecond == createdAt.Millisecond),
        "直接加载 JSON 时必须保留创建时间的亚秒精度");

    const string invalidJson = "这不是有效 JSON";
    File.WriteAllText(filePath, invalidJson);
    var invalidBytesBeforeTryLoad = File.ReadAllBytes(filePath);
    Assert(!store.TryLoad(out var invalidItems) && invalidItems.Count == 0,
        "TryLoad 遇到损坏 JSON 时应返回 false 和空集合");
    Assert(File.ReadAllBytes(filePath).SequenceEqual(invalidBytesBeforeTryLoad),
        "TryLoad 解析失败不得清空、覆盖或重写原始损坏 JSON");
    Assert(store.Load().Count == 0, "损坏的定时任务 JSON 应安全回退为空列表");

    var lockedPath = Path.Combine(tempDirectory, "locked-scheduled-tasks.json");
    File.WriteAllText(lockedPath, savedJson);
    var lockedBytesBeforeTryLoad = File.ReadAllBytes(lockedPath);
    var lockedStore = new ScheduledTaskStore(lockedPath);
    using (var exclusiveLock = new FileStream(
               lockedPath,
               FileMode.Open,
               FileAccess.ReadWrite,
               FileShare.None))
    {
        Assert(!lockedStore.TryLoad(out var lockedItems) && lockedItems.Count == 0,
            "TryLoad 遇到被独占锁定的不可读文件时应返回 false 和空集合");
        Assert(exclusiveLock.Length == lockedBytesBeforeTryLoad.Length,
            "TryLoad 读取锁定文件失败时不得改变原始文件长度");
    }
    Assert(File.ReadAllBytes(lockedPath).SequenceEqual(lockedBytesBeforeTryLoad),
        "TryLoad 读取锁定文件失败后不得改写原始内容");

    var blockedPath = Path.Combine(tempDirectory, "blocked-scheduled-tasks.json");
    Directory.CreateDirectory(blockedPath);
    var blockedStore = new ScheduledTaskStore(blockedPath);
    var directorySentinelPath = Path.Combine(blockedPath, "keep-me.txt");
    const string directorySentinel = "scheduled task directory sentinel";
    File.WriteAllText(directorySentinelPath, directorySentinel);
    Assert(!blockedStore.TryLoad(out var directoryItems) && directoryItems.Count == 0,
        "TryLoad 遇到目录路径时应返回 false，不能将目录误判为文件缺失");
    Assert(Directory.Exists(blockedPath) &&
           File.ReadAllText(directorySentinelPath) == directorySentinel,
        "TryLoad 目录路径失败时不得覆盖目标、删除目录或改写其中内容");
    Assert(!blockedStore.Save(original), "定时任务保存失败时应返回 false 而不是抛异常");
    Assert(!File.Exists(blockedPath + ".tmp"),
        "定时任务保存失败后应尽量清理临时文件");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertClose(double actual, double expected, string message)
{
    if (Math.Abs(actual - expected) >= 0.0001)
    {
        throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
    }
}
