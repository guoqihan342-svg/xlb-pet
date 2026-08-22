using LubanDesktopPet;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

var tempDirectory = Path.Combine(Path.GetTempPath(), "LubanDesktopPetChecks", Guid.NewGuid().ToString("N"));

try
{
    CheckTodoStore(tempDirectory);
    CheckAppSettingsStore(tempDirectory);
    CheckScheduledTaskStore(tempDirectory);
    CheckScheduledRepeatRules(tempDirectory);
    CheckScheduledQuietHours(tempDirectory);
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
    Assert(defaults.AlwaysOnTop, "设置文件缺失时应默认保持桌宠和任务小屋置顶");
    AssertClose(defaults.PetSizeScale, 1.0, "设置文件缺失时桌宠尺寸应为 100%");

    Assert(store.Save(new AppSettings
    {
        EdgeRoamingEnabled = false,
        AlwaysOnTop = false,
        PetSizeScale = 1.25
    }), "首次保存绕屏、置顶和尺寸设置应成功");
    Assert(Directory.Exists(Path.GetDirectoryName(settingsPath)), "保存时应自动创建设置目录");
    var firstLoaded = store.Load();
    Assert(!firstLoaded.EdgeRoamingEnabled, "关闭绕屏动画应能往返保存和加载");
    Assert(!firstLoaded.AlwaysOnTop, "取消置顶应能往返保存和加载");
    AssertClose(firstLoaded.PetSizeScale, 1.25, "125% 尺寸应能往返保存和加载");

    var bytes = File.ReadAllBytes(settingsPath);
    Assert(
        bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF,
        "设置 JSON 应使用无 BOM 的 UTF-8 编码");
    var firstJson = File.ReadAllText(settingsPath);
    Assert(firstJson.Contains("\"edgeRoamingEnabled\"", StringComparison.Ordinal),
        "设置 JSON 必须持久化 camelCase 的绕屏开关字段");
    Assert(firstJson.Contains("\"alwaysOnTop\"", StringComparison.Ordinal),
        "设置 JSON 必须持久化 camelCase 的置顶开关字段");
    Assert(firstJson.Contains("\"petSizeScale\"", StringComparison.Ordinal),
        "设置 JSON 必须持久化 camelCase 的桌宠尺寸字段");

    Assert(store.Save(new AppSettings
    {
        EdgeRoamingEnabled = true,
        AlwaysOnTop = true,
        PetSizeScale = 0.75
    }), "覆盖保存开启绕屏、置顶和尺寸下限应成功");
    var secondLoaded = store.Load();
    Assert(secondLoaded.EdgeRoamingEnabled, "开启绕屏动画应能往返保存和加载");
    Assert(secondLoaded.AlwaysOnTop, "恢复置顶应能往返保存和加载");
    AssertClose(secondLoaded.PetSizeScale, 0.75, "尺寸下限应能往返保存和加载");
    Assert(!File.Exists(settingsPath + ".tmp"), "成功保存后不应残留临时文件");

    File.WriteAllText(settingsPath, "{\"edgeRoamingEnabled\":false,\"petSizeScale\":1.1}");
    var legacySettings = store.Load();
    Assert(!legacySettings.EdgeRoamingEnabled,
        "旧版 JSON 中关闭的绕屏开关应继续保留");
    Assert(legacySettings.AlwaysOnTop,
        "旧版 JSON 缺少置顶字段时应平滑迁移为默认置顶");
    AssertClose(legacySettings.PetSizeScale, 1.1,
        "旧版 JSON 应同时保留桌宠尺寸");
    Assert(store.Save(legacySettings), "加载旧版 JSON 后应能按当前格式保存");
    var migratedJson = File.ReadAllText(settingsPath);
    Assert(migratedJson.Contains("\"edgeRoamingEnabled\": false", StringComparison.Ordinal),
        "旧版 JSON 保存后应继续持久化关闭的绕屏开关");
    Assert(migratedJson.Contains("\"alwaysOnTop\": true", StringComparison.Ordinal),
        "旧版 JSON 保存后应补齐默认置顶字段");
    Assert(migratedJson.Contains("\"petSizeScale\"", StringComparison.Ordinal),
        "旧版 JSON 保存后应继续保留尺寸字段");

    File.WriteAllText(settingsPath, "{\"petSizeScale\":1.15}");
    var missingRoamingSetting = store.Load();
    Assert(missingRoamingSetting.EdgeRoamingEnabled,
        "当前版本缺少绕屏字段的 JSON 应平滑迁移为默认开启");
    Assert(missingRoamingSetting.AlwaysOnTop,
        "当前版本缺少置顶字段的 JSON 应平滑迁移为默认置顶");
    AssertClose(missingRoamingSetting.PetSizeScale, 1.15,
        "迁移缺少绕屏字段的 JSON 时不得丢失桌宠尺寸");

    File.WriteAllText(settingsPath, "这不是有效 JSON");
    var corruptedFallback = store.Load();
    Assert(corruptedFallback.EdgeRoamingEnabled,
        "损坏 JSON 应回退到默认开启绕屏动画");
    Assert(corruptedFallback.AlwaysOnTop,
        "损坏 JSON 应回退到默认置顶");
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

    var recurringPath = Path.Combine(
        tempDirectory,
        "scheduled",
        "recurring-scheduled-tasks.json");
    var recurringStore = new ScheduledTaskStore(recurringPath);
    var expectedRepeatInterval =
        TimeSpan.FromDays(1) + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3);
    var recurringItem = new ScheduledTaskItem
    {
        Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
        Text = "循环提醒",
        DueAt = dueAt.AddDays(1),
        CreatedAt = createdAt,
        RepeatInterval = expectedRepeatInterval
    };
    Assert(recurringStore.Save([recurringItem]),
        "1 天 2 小时 3 分钟的循环间隔应保存成功");
    var recurringReloaded = recurringStore.Load();
    Assert(recurringReloaded.Count == 1 &&
           recurringReloaded[0].RepeatInterval == expectedRepeatInterval &&
           recurringReloaded[0].IsRecurring,
        "1 天 2 小时 3 分钟的循环间隔必须完整往返");
    Assert(
        recurringReloaded[0].RepeatDisplayText == "每1天2小时3分钟" &&
        recurringReloaded[0].DueAtDisplayText.Contains(
            "每1天2小时3分钟",
            StringComparison.Ordinal) &&
        recurringReloaded[0].DueAtDisplayText.Contains(
            "下次",
            StringComparison.Ordinal),
        "循环任务的显示文字应包含完整间隔和下次提醒提示");

    var legacyPath = Path.Combine(
        tempDirectory,
        "scheduled",
        "legacy-scheduled-tasks.json");
    var legacyStore = new ScheduledTaskStore(legacyPath);
    var legacyRecord = new[]
    {
        new
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Text = "旧版单次提醒",
            DueAt = dueAt.AddDays(2),
            CreatedAt = createdAt
        }
    };
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacyRecord));
    var legacyReloaded = legacyStore.Load();
    Assert(legacyReloaded.Count == 1 &&
           legacyReloaded[0].RepeatInterval is null &&
           !legacyReloaded[0].IsRecurring,
        "旧版 JSON 缺少循环间隔字段时必须兼容为单次提醒");
    Assert(legacyReloaded[0].RepeatDisplayText == "单次",
        "旧版单次任务的显示文字应明确为单次");

    var invalidRepeatPath = Path.Combine(
        tempDirectory,
        "scheduled",
        "invalid-repeat-scheduled-tasks.json");
    var invalidRepeatStore = new ScheduledTaskStore(invalidRepeatPath);
    var invalidRepeatItems = new[]
    {
        new ScheduledTaskItem
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000003"),
            Text = "不足一分钟",
            DueAt = dueAt.AddDays(3),
            CreatedAt = createdAt,
            RepeatInterval = TimeSpan.FromSeconds(59)
        },
        new ScheduledTaskItem
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000004"),
            Text = "达到一千天",
            DueAt = dueAt.AddDays(4),
            CreatedAt = createdAt,
            RepeatInterval = TimeSpan.FromDays(1000)
        }
    };
    Assert(invalidRepeatStore.Save(invalidRepeatItems),
        "非法循环间隔不应阻止任务保存");
    var invalidRepeatReloaded = invalidRepeatStore.Load();
    Assert(invalidRepeatReloaded.Count == 2 &&
           invalidRepeatReloaded.All(item =>
               item.RepeatInterval is null && !item.IsRecurring),
        "不足 1 分钟及达到 1000 天的循环间隔都必须归一为 null");

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

static void CheckScheduledRepeatRules(string tempDirectory)
{
    var chinaTimeZone = FindAvailableTimeZone(
        "China Standard Time",
        "Asia/Shanghai")
        ?? throw new InvalidOperationException("缺少可用于循环规则测试的中国时区");
    var anchorLocal = new DateTime(
        2026,
        7,
        28,
        10,
        7,
        45,
        DateTimeKind.Unspecified);
    Assert(
        ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Minute,
            5,
            anchorLocal.AddMilliseconds(987),
            chinaTimeZone,
            out var minuteRule,
            out var firstDueAt) &&
        minuteRule is not null &&
        minuteRule.Version == ScheduledRepeatRule.CurrentVersion &&
        minuteRule.Unit == ScheduledRepeatUnit.Minute &&
        minuteRule.Every == 5 &&
        minuteRule.TimeZoneId == chinaTimeZone.Id &&
        minuteRule.AnchorLocal == anchorLocal &&
        minuteRule.NextOrdinal == 0,
        "每 5 分钟规则必须以所选整秒本地时间建立 version 1 锚点");
    Assert(
        ScheduledRepeatSchedule.TryGetNominalInterval(
            minuteRule,
            out var minuteInterval) &&
        minuteInterval == TimeSpan.FromMinutes(5),
        "分钟规则必须提供与旧字段兼容的名义 RepeatInterval");
    Assert(
        ScheduledRepeatSchedule.TryGetOccurrence(
            minuteRule,
            1,
            out var secondOccurrence) &&
        TimeZoneInfo.ConvertTime(secondOccurrence, chinaTimeZone).DateTime ==
            anchorLocal.AddMinutes(5),
        "分钟规则的下一次 occurrence 必须保持第 45 秒");
    Assert(
        ScheduledRepeatSchedule.FormatRule(minuteRule!) ==
            "每5分钟，第45秒",
        "分钟规则预览必须明确显示固定秒");

    const long farOrdinal = 20_000_000;
    Assert(
        ScheduledRepeatSchedule.TryGetOccurrence(
            minuteRule,
            farOrdinal,
            out var farOccurrence),
        "长期分钟规则必须仍能直接计算远期 occurrence");
    var evaluationStopwatch = Stopwatch.StartNew();
    Assert(
        ScheduledRepeatSchedule.TryEvaluate(
            minuteRule,
            firstDueAt,
            farOccurrence,
            out var farEvaluation),
        "长期离线规则必须能计算漏提醒和下一次 occurrence");
    evaluationStopwatch.Stop();
    Assert(
        farEvaluation.DueCount == farOrdinal + 1 &&
        farEvaluation.NextOrdinal == farOrdinal + 1 &&
        farEvaluation.NextDueAt is { } farNextDueAt &&
        ScheduledRepeatSchedule.TryGetOccurrence(
            minuteRule,
            farOrdinal + 1,
            out var expectedFarNextDueAt) &&
        farNextDueAt == expectedFarNextDueAt,
        "对数推进必须一次得到准确 DueCount、NextDueAt 和 NextOrdinal");
    Assert(
        evaluationStopwatch.Elapsed < TimeSpan.FromSeconds(5),
        $"两千万次离线推进必须保持对数级，实际耗时 {evaluationStopwatch.Elapsed}");
    Assert(
        ScheduledRepeatSchedule.TryEvaluate(
            minuteRule,
            firstDueAt,
            firstDueAt.AddSeconds(-1),
            out var notDueEvaluation) &&
        notDueEvaluation.DueCount == 0 &&
        notDueEvaluation.NextDueAt == firstDueAt &&
        notDueEvaluation.NextOrdinal == 0,
        "尚未到点时必须保留当前 DueAt 和 ordinal，不得提前推进");

    var hourAnchor = new DateTime(
        2026,
        7,
        28,
        10,
        15,
        30,
        DateTimeKind.Unspecified);
    Assert(
        ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Hour,
            2,
            hourAnchor,
            chinaTimeZone,
            out var hourRule,
            out _) &&
        hourRule is not null &&
        ScheduledRepeatSchedule.FormatRule(hourRule) ==
            "每2小时，第15分30秒" &&
        ScheduledRepeatSchedule.TryGetOccurrence(
            hourRule,
            1,
            out var nextHourOccurrence) &&
        TimeZoneInfo.ConvertTime(nextHourOccurrence, chinaTimeZone).DateTime ==
            hourAnchor.AddHours(2),
        "小时规则必须明确并保持所选分秒");

    var dayAnchor = new DateTime(
        2026,
        7,
        28,
        8,
        15,
        30,
        DateTimeKind.Unspecified);
    Assert(
        ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Day,
            3,
            dayAnchor,
            chinaTimeZone,
            out var dayRule,
            out _) &&
        dayRule is not null &&
        ScheduledRepeatSchedule.FormatRule(dayRule) ==
            "每3天，08:15:30" &&
        ScheduledRepeatSchedule.TryGetOccurrence(
            dayRule,
            1,
            out var nextDayOccurrence) &&
        TimeZoneInfo.ConvertTime(nextDayOccurrence, chinaTimeZone).DateTime ==
            dayAnchor.AddDays(3),
        "天规则必须明确并保持所选 HH:mm:ss");

    Assert(
        !ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Minute,
            0,
            anchorLocal,
            chinaTimeZone,
            out _,
            out _) &&
        !ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Day,
            1000,
            anchorLocal,
            chinaTimeZone,
            out _,
            out _),
        "循环规则必须拒绝零间隔和达到一千天的间隔");

    var rulePath = Path.Combine(
        tempDirectory,
        "scheduled",
        "versioned-repeat-rules.json");
    var ruleStore = new ScheduledTaskStore(rulePath);
    var ruleItem = new ScheduledTaskItem
    {
        Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
        Text = "版本化分钟循环",
        DueAt = firstDueAt,
        CreatedAt = firstDueAt.AddDays(-1),
        RepeatRule = minuteRule
    };
    Assert(ruleStore.Save([ruleItem]),
        "只提供有效 version 1 rule 时保存层必须补齐兼容 RepeatInterval");
    var ruleJson = File.ReadAllText(rulePath);
    Assert(
        ruleJson.Contains("\"repeatRule\"", StringComparison.Ordinal) &&
        ruleJson.Contains("\"unit\": \"minute\"", StringComparison.Ordinal) &&
        ruleJson.Contains("\"anchorLocal\"", StringComparison.Ordinal) &&
        ruleJson.Contains("\"nextOrdinal\": 0", StringComparison.Ordinal),
        "version 1 rule 必须使用可读的 camelCase JSON 完整持久化");
    var ruleReloaded = ruleStore.Load().Single();
    Assert(
        ruleReloaded.RepeatRule == minuteRule &&
        ruleReloaded.RepeatInterval == TimeSpan.FromMinutes(5) &&
        ruleReloaded.IsRecurring &&
        !ruleReloaded.IsLegacyRecurring,
        "version 1 rule、兼容间隔和非 legacy 身份必须完整往返");
    Assert(
        ScheduledRepeatSchedule.TryEvaluate(
            ruleReloaded.RepeatRule,
            ruleReloaded.DueAt,
            farOccurrence,
            out var restartEvaluation) &&
        restartEvaluation == farEvaluation,
        "重启重新加载后必须得到完全相同的漏提醒次数和未来触发点");

    var advancedRule = ruleReloaded.RepeatRule! with
    {
        NextOrdinal = farEvaluation.NextOrdinal!.Value
    };
    ruleReloaded.RepeatRule = advancedRule;
    ruleReloaded.DueAt = farEvaluation.NextDueAt!.Value;
    Assert(ruleStore.Save([ruleReloaded]),
        "确认提醒后必须能原子保存推进后的 DueAt 和 NextOrdinal");
    var advancedReloaded = ruleStore.Load().Single();
    Assert(
        advancedReloaded.RepeatRule?.NextOrdinal ==
            farEvaluation.NextOrdinal &&
        advancedReloaded.DueAt == farEvaluation.NextDueAt &&
        ScheduledRepeatSchedule.TryEvaluate(
            advancedReloaded.RepeatRule,
            advancedReloaded.DueAt,
            farOccurrence,
            out var afterAdvanceEvaluation) &&
        afterAdvanceEvaluation.DueCount == 0,
        "推进状态重启后不得回退到已经确认过的 occurrence");

    var legacyRulePath = Path.Combine(
        tempDirectory,
        "scheduled",
        "legacy-repeat-without-rule.json");
    var legacyRuleRecord = new[]
    {
        new
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000002"),
            Text = "旧版固定九十分钟",
            DueAt = firstDueAt,
            CreatedAt = firstDueAt.AddDays(-1),
            RepeatIntervalTicks = (long?)TimeSpan.FromMinutes(90).Ticks
        }
    };
    File.WriteAllText(
        legacyRulePath,
        JsonSerializer.Serialize(legacyRuleRecord));
    var legacyRuleStore = new ScheduledTaskStore(legacyRulePath);
    var legacyRuleReloaded = legacyRuleStore.Load().Single();
    Assert(
        legacyRuleReloaded.RepeatRule is null &&
        legacyRuleReloaded.RepeatInterval == TimeSpan.FromMinutes(90) &&
        legacyRuleReloaded.IsLegacyRecurring,
        "旧 JSON 缺少 repeatRule 时必须原样走 legacy 固定间隔");
    Assert(legacyRuleStore.Save([legacyRuleReloaded]),
        "旧循环任务必须仍可编辑并重新保存");
    Assert(
        !File.ReadAllText(legacyRulePath)
            .Contains("\"repeatRule\"", StringComparison.Ordinal),
        "未主动升级的旧任务保存后不得伪造 version 1 rule");

    var badRulePath = Path.Combine(
        tempDirectory,
        "scheduled",
        "invalid-versioned-repeat-rule.json");
    var badRuleRecords = new object[]
    {
        new
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000003"),
            Text = "规则与到期时间不一致",
            DueAt = firstDueAt.AddMinutes(1),
            CreatedAt = firstDueAt.AddDays(-1),
            RepeatIntervalTicks = (long?)TimeSpan.FromMinutes(5).Ticks,
            RepeatRule = new
            {
                Version = 1,
                Unit = "minute",
                Every = 5,
                TimeZoneId = chinaTimeZone.Id,
                AnchorLocal = anchorLocal,
                NextOrdinal = 0L
            }
        },
        new
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000004"),
            Text = "未知规则版本仍保留",
            DueAt = firstDueAt.AddMinutes(2),
            CreatedAt = firstDueAt.AddDays(-1),
            RepeatIntervalTicks = (long?)TimeSpan.FromMinutes(7).Ticks,
            RepeatRule = new
            {
                Version = 99,
                Unit = "minute",
                Every = 7,
                TimeZoneId = chinaTimeZone.Id,
                AnchorLocal = anchorLocal,
                NextOrdinal = 0L
            }
        },
        new
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000005"),
            Text = "坏规则无旧间隔也不能删任务",
            DueAt = firstDueAt.AddMinutes(3),
            CreatedAt = firstDueAt.AddDays(-1),
            RepeatIntervalTicks = (long?)null,
            RepeatRule = new
            {
                Version = 1,
                Unit = "week",
                Every = 1,
                TimeZoneId = "不存在的时区",
                AnchorLocal = anchorLocal,
                NextOrdinal = 0L
            }
        }
    };
    File.WriteAllText(badRulePath, JsonSerializer.Serialize(badRuleRecords));
    var badRuleReloaded = new ScheduledTaskStore(badRulePath).Load();
    Assert(
        badRuleReloaded.Count == 3 &&
        badRuleReloaded.All(item => item.RepeatRule is null) &&
        badRuleReloaded[0].RepeatInterval == TimeSpan.FromMinutes(5) &&
        badRuleReloaded[1].RepeatInterval == TimeSpan.FromMinutes(7) &&
        badRuleReloaded[2].RepeatInterval is null,
        "未知、损坏或与 DueAt 不一致的 rule 必须回退 legacy/单次且绝不能删任务");

    CheckScheduledRepeatAdvanceToFuture(tempDirectory, chinaTimeZone);
    CheckScheduledRepeatDstRules();
}

static void CheckScheduledRepeatAdvanceToFuture(
    string tempDirectory,
    TimeZoneInfo timeZone)
{
    var nowLocal = new DateTime(
        2026,
        7,
        29,
        12,
        34,
        56,
        DateTimeKind.Unspecified);
    var now = new DateTimeOffset(
        TimeZoneInfo.ConvertTimeToUtc(nowLocal, timeZone),
        TimeSpan.Zero);
    var cases = new[]
    {
        (
            Unit: ScheduledRepeatUnit.Minute,
            Every: 7,
            AnchorLocal: new DateTime(
                2026,
                7,
                29,
                10,
                0,
                30,
                DateTimeKind.Unspecified),
            ExpectedOrdinal: 23L,
            NominalInterval: TimeSpan.FromMinutes(7),
            Label: "分钟"),
        (
            Unit: ScheduledRepeatUnit.Hour,
            Every: 3,
            AnchorLocal: new DateTime(
                2026,
                7,
                28,
                23,
                15,
                20,
                DateTimeKind.Unspecified),
            ExpectedOrdinal: 5L,
            NominalInterval: TimeSpan.FromHours(3),
            Label: "小时"),
        (
            Unit: ScheduledRepeatUnit.Day,
            Every: 3,
            AnchorLocal: new DateTime(
                2026,
                7,
                20,
                8,
                5,
                4,
                DateTimeKind.Unspecified),
            ExpectedOrdinal: 4L,
            NominalInterval: TimeSpan.FromDays(3),
            Label: "天")
    };
    var advancedItems = new List<ScheduledTaskItem>(cases.Length);
    var expectedById =
        new Dictionary<Guid, (ScheduledRepeatRule Rule, DateTimeOffset DueAt)>();

    for (var index = 0; index < cases.Length; index++)
    {
        var testCase = cases[index];
        Assert(
            ScheduledRepeatSchedule.TryCreate(
                testCase.Unit,
                testCase.Every,
                testCase.AnchorLocal,
                timeZone,
                out var initialRule,
                out var initialDueAt) &&
            initialRule is not null &&
            initialDueAt <= now,
            $"过去的{testCase.Label}循环必须先建立有效的已过期锚点");
        var confirmedInitialRule = initialRule!;
        Assert(
            ScheduledRepeatSchedule.TryGetOccurrence(
                confirmedInitialRule,
                testCase.ExpectedOrdinal,
                out var expectedDueAt) &&
            expectedDueAt > now,
            $"{testCase.Label}循环的预期 ordinal 必须对应严格晚于现在的 occurrence");
        Assert(
            ScheduledRepeatSchedule.TryAdvanceToFuture(
                confirmedInitialRule,
                initialDueAt,
                now,
                out var futureRule,
                out var futureDueAt) &&
            futureRule is not null,
            $"过去的{testCase.Label}循环必须能自动推进到未来");
        var confirmedFutureRule = futureRule!;
        Assert(
            confirmedFutureRule ==
                confirmedInitialRule with { NextOrdinal = testCase.ExpectedOrdinal } &&
            confirmedFutureRule.NextOrdinal == testCase.ExpectedOrdinal &&
            confirmedFutureRule.AnchorLocal == confirmedInitialRule.AnchorLocal &&
            futureDueAt == expectedDueAt &&
            futureDueAt > now,
            $"{testCase.Label}循环必须保持原锚点并推进到正确的严格未来 ordinal");
        Assert(
            ScheduledRepeatSchedule.TryValidateForDueAt(
                confirmedFutureRule,
                futureDueAt,
                out var nominalInterval) &&
            nominalInterval == testCase.NominalInterval,
            $"推进后的{testCase.Label}规则和 DueAt 必须仍可通过一致性校验");

        var id = new Guid(
            $"40000000-0000-0000-0000-{index + 1:000000000000}");
        advancedItems.Add(new ScheduledTaskItem
        {
            Id = id,
            Text = $"推进后的{testCase.Label}循环",
            DueAt = futureDueAt,
            CreatedAt = now.AddDays(-1),
            RepeatRule = confirmedFutureRule
        });
        expectedById.Add(id, (confirmedFutureRule, futureDueAt));
    }

    var futureAnchorLocal = nowLocal.AddDays(2);
    Assert(
        ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Day,
            2,
            futureAnchorLocal,
            timeZone,
            out var futureAnchorRule,
            out var futureAnchorDueAt) &&
        futureAnchorRule is not null &&
        futureAnchorDueAt > now,
        "未来循环锚点必须能正常建立");
    Assert(
        ScheduledRepeatSchedule.TryAdvanceToFuture(
            futureAnchorRule,
            futureAnchorDueAt,
            now,
            out var unchangedFutureRule,
            out var unchangedFutureDueAt) &&
        unchangedFutureRule == futureAnchorRule &&
        unchangedFutureRule.AnchorLocal == futureAnchorLocal &&
        unchangedFutureRule.NextOrdinal == 0 &&
        unchangedFutureDueAt == futureAnchorDueAt,
        "已经在未来的循环锚点和 NextOrdinal 必须保持不变");

    Assert(
        ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Minute,
            2,
            nowLocal,
            timeZone,
            out var dueNowRule,
            out var dueNow) &&
        dueNowRule is not null &&
        dueNow == now &&
        ScheduledRepeatSchedule.TryAdvanceToFuture(
            dueNowRule,
            dueNow,
            now,
            out var afterNowRule,
            out var afterNowDueAt) &&
        afterNowRule.NextOrdinal == 1 &&
        afterNowDueAt > now,
        "恰好等于现在的循环也必须推进一次，结果必须严格晚于现在");

    var advanceStorePath = Path.Combine(
        tempDirectory,
        "scheduled",
        "advanced-repeat-rules.json");
    var advanceStore = new ScheduledTaskStore(advanceStorePath);
    Assert(
        advanceStore.Save(advancedItems),
        "推进后的分钟、小时和天循环必须能一起存储");
    Assert(
        advanceStore.TryLoad(out var reloadedItems) &&
        reloadedItems.Count == advancedItems.Count,
        "推进后的分钟、小时和天循环必须能在重启后完整重载");
    foreach (var reloadedItem in reloadedItems)
    {
        Assert(
            expectedById.TryGetValue(reloadedItem.Id, out var expected) &&
            reloadedItem.RepeatRule == expected.Rule &&
            reloadedItem.DueAt == expected.DueAt &&
            ScheduledRepeatSchedule.TryValidateForDueAt(
                reloadedItem.RepeatRule,
                reloadedItem.DueAt,
                out var reloadedInterval) &&
            reloadedInterval == reloadedItem.RepeatInterval,
            "重载后的推进规则、NextOrdinal、DueAt 和兼容间隔必须保持一致");
    }
}

static void CheckScheduledRepeatDstRules()
{
    var eastern = FindAvailableTimeZone(
        "Eastern Standard Time",
        "America/New_York");
    if (eastern is not null)
    {
        var invalidLocal = new DateTime(
            2026,
            3,
            8,
            2,
            30,
            0,
            DateTimeKind.Unspecified);
        if (eastern.IsInvalidTime(invalidLocal))
        {
            Assert(
                !ScheduledRepeatSchedule.TryCreate(
                    ScheduledRepeatUnit.Day,
                    1,
                    invalidLocal,
                    eastern,
                    out _,
                    out _),
                "首次所选时间处于 Eastern 春季缺口时必须拒绝保存");
            var easternAnchor = invalidLocal.AddDays(-1);
            Assert(
                ScheduledRepeatSchedule.TryCreate(
                    ScheduledRepeatUnit.Day,
                    1,
                    easternAnchor,
                    eastern,
                    out var easternRule,
                    out var easternFirstDueAt) &&
                easternRule is not null,
                "Eastern 每日规则必须能跨越春季缺口");
            var easternGapOccurrence = default(DateTimeOffset);
            var easternAfterGapOccurrence = default(DateTimeOffset);
            Assert(
                ScheduledRepeatSchedule.TryGetOccurrence(
                    easternRule!,
                    1,
                    out easternGapOccurrence) &&
                ScheduledRepeatSchedule.TryGetOccurrence(
                    easternRule,
                    2,
                    out easternAfterGapOccurrence),
                "Eastern 每日规则必须生成缺口当天及下一天 occurrence");
            var gapLocal =
                TimeZoneInfo.ConvertTime(easternGapOccurrence, eastern).DateTime;
            var afterGapLocal =
                TimeZoneInfo.ConvertTime(easternAfterGapOccurrence, eastern).DateTime;
            Assert(
                gapLocal == new DateTime(
                    2026,
                    3,
                    8,
                    3,
                    0,
                    0,
                    DateTimeKind.Unspecified) &&
                afterGapLocal == new DateTime(
                    2026,
                    3,
                    9,
                    2,
                    30,
                    0,
                    DateTimeKind.Unspecified),
                "不存在的 Eastern 02:30 必须合并到首个有效 03:00，下一天恢复 02:30");
            Assert(
                ScheduledRepeatSchedule.TryEvaluate(
                    easternRule,
                    easternFirstDueAt,
                    easternAfterGapOccurrence,
                    out var easternEvaluation) &&
                easternEvaluation.DueCount == 3 &&
                easternEvaluation.NextOrdinal == 3,
                "跨 Eastern DST 离线推进必须准确统计三个 nominal occurrence");
        }

        var ambiguousLocal = new DateTime(
            2026,
            11,
            1,
            1,
            30,
            0,
            DateTimeKind.Unspecified);
        if (eastern.IsAmbiguousTime(ambiguousLocal))
        {
            Assert(
                ScheduledRepeatSchedule.TryCreate(
                    ScheduledRepeatUnit.Day,
                    1,
                    ambiguousLocal,
                    eastern,
                    out _,
                    out var ambiguousDueAt) &&
                ambiguousDueAt.Offset ==
                    eastern.GetAmbiguousTimeOffsets(ambiguousLocal).Max(),
                "Eastern 秋季重复的 01:30 必须稳定选择较早实际瞬间且只建一个 occurrence");
        }
    }

    var lordHowe = FindAvailableTimeZone(
        "Lord Howe Standard Time",
        "Australia/Lord_Howe");
    if (lordHowe is not null)
    {
        var lordHoweInvalidLocal = new DateTime(
            2026,
            10,
            4,
            2,
            15,
            0,
            DateTimeKind.Unspecified);
        if (lordHowe.IsInvalidTime(lordHoweInvalidLocal))
        {
            var lordHoweAnchor = lordHoweInvalidLocal.AddDays(-1);
            Assert(
                ScheduledRepeatSchedule.TryCreate(
                    ScheduledRepeatUnit.Day,
                    1,
                    lordHoweAnchor,
                    lordHowe,
                    out var lordHoweRule,
                    out _) &&
                lordHoweRule is not null,
                "Lord Howe 每日规则必须支持非整小时 DST 缺口");
            var lordHoweGapOccurrence = default(DateTimeOffset);
            Assert(
                ScheduledRepeatSchedule.TryGetOccurrence(
                    lordHoweRule!,
                    1,
                    out lordHoweGapOccurrence),
                "Lord Howe 每日规则必须生成缺口当天 occurrence");
            var lordHoweGapLocal =
                TimeZoneInfo.ConvertTime(
                    lordHoweGapOccurrence,
                    lordHowe).DateTime;
            Assert(
                lordHoweGapLocal == new DateTime(
                    2026,
                    10,
                    4,
                    2,
                    30,
                    0,
                    DateTimeKind.Unspecified),
                "Lord Howe 02:15 必须推进到真实的 30 分钟缺口末端 02:30，不能写死一小时");
        }
    }
}

static void CheckScheduledQuietHours(string tempDirectory)
{
    var chinaTimeZone = FindAvailableTimeZone(
        "China Standard Time",
        "Asia/Shanghai")
        ?? throw new InvalidOperationException(
            "A China time zone is required for quiet-hours checks.");

    DateTimeOffset AtLocal(
        TimeZoneInfo timeZone,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var local = new DateTime(
            year,
            month,
            day,
            hour,
            minute,
            second,
            DateTimeKind.Unspecified);
        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(local, timeZone),
            TimeSpan.Zero);
    }

    var sameDay = new ScheduledQuietHours
    {
        Version = 1,
        Start = new TimeSpan(12, 30, 15),
        End = new TimeSpan(13, 45, 20),
        TimeZoneId = chinaTimeZone.Id
    };
    var normalizedSameDay = ScheduledQuietHoursSchedule.Normalize(sameDay);
    Assert(
        normalizedSameDay is not null &&
        normalizedSameDay.Version == 1 &&
        normalizedSameDay.Start == new TimeSpan(12, 30, 15) &&
        normalizedSameDay.End == new TimeSpan(13, 45, 20) &&
        normalizedSameDay.TimeZoneId == chinaTimeZone.Id,
        "Valid same-day quiet hours must normalize without losing second precision.");

    var beforeSameDayStart =
        AtLocal(chinaTimeZone, 2026, 7, 30, 12, 30, 14);
    var atSameDayStart =
        AtLocal(chinaTimeZone, 2026, 7, 30, 12, 30, 15);
    var beforeSameDayEnd =
        AtLocal(chinaTimeZone, 2026, 7, 30, 13, 45, 19);
    var atSameDayEnd =
        AtLocal(chinaTimeZone, 2026, 7, 30, 13, 45, 20);
    Assert(
        !ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedSameDay,
            beforeSameDayStart) &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedSameDay,
            atSameDayStart) &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedSameDay,
            beforeSameDayEnd) &&
        !ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedSameDay,
            atSameDayEnd),
        "Same-day quiet hours must use a start-inclusive, end-exclusive interval.");
    Assert(
        ScheduledQuietHoursSchedule.TryGetQuietEnd(
            normalizedSameDay,
            atSameDayStart,
            out var sameDayEnd) &&
        sameDayEnd == atSameDayEnd,
        "A same-day quiet interval must resolve its exact end instant.");
    Assert(
        ScheduledQuietHoursSchedule.TryGetNextQuietStart(
            normalizedSameDay,
            beforeSameDayStart,
            out var sameDayNextStart) &&
        sameDayNextStart == atSameDayStart,
        "Before a same-day interval, the next quiet start must be today.");
    Assert(
        ScheduledQuietHoursSchedule.TryGetNextQuietStart(
            normalizedSameDay,
            atSameDayEnd,
            out var nextDaySameDayStart) &&
        TimeZoneInfo.ConvertTime(nextDaySameDayStart, chinaTimeZone).DateTime ==
            new DateTime(
                2026,
                7,
                31,
                12,
                30,
                15,
                DateTimeKind.Unspecified),
        "At the exclusive end boundary, the next quiet start must be tomorrow.");
    var sameDayDisplay =
        ScheduledQuietHoursSchedule.FormatDisplayText(normalizedSameDay!);
    Assert(
        sameDayDisplay.Contains("12:30:15", StringComparison.Ordinal) &&
        sameDayDisplay.Contains("13:45:20", StringComparison.Ordinal),
        "The quiet-hours display text must retain both exact wall-clock times.");

    var overnight = new ScheduledQuietHours
    {
        Version = 1,
        Start = new TimeSpan(22, 0, 0),
        End = new TimeSpan(7, 0, 0),
        TimeZoneId = chinaTimeZone.Id
    };
    var normalizedOvernight =
        ScheduledQuietHoursSchedule.Normalize(overnight);
    var beforeOvernightStart =
        AtLocal(chinaTimeZone, 2026, 7, 30, 21, 59, 59);
    var atOvernightStart =
        AtLocal(chinaTimeZone, 2026, 7, 30, 22, 0, 0);
    var beforeOvernightEnd =
        AtLocal(chinaTimeZone, 2026, 7, 31, 6, 59, 59);
    var atOvernightEnd =
        AtLocal(chinaTimeZone, 2026, 7, 31, 7, 0, 0);
    Assert(
        normalizedOvernight is not null &&
        !ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedOvernight,
            beforeOvernightStart) &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedOvernight,
            atOvernightStart) &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedOvernight,
            beforeOvernightEnd) &&
        !ScheduledQuietHoursSchedule.IsQuietAt(
            normalizedOvernight,
            atOvernightEnd),
        "Cross-midnight quiet hours must use [start,end) on both calendar days.");
    Assert(
        ScheduledQuietHoursSchedule.TryGetQuietEnd(
            normalizedOvernight,
            atOvernightStart,
            out var overnightEndFromStart) &&
        overnightEndFromStart == atOvernightEnd &&
        ScheduledQuietHoursSchedule.TryGetQuietEnd(
            normalizedOvernight,
            beforeOvernightEnd,
            out var overnightEndFromMorning) &&
        overnightEndFromMorning == atOvernightEnd,
        "Both halves of an overnight interval must resolve the same end instant.");
    Assert(
        ScheduledQuietHoursSchedule.TryGetNextQuietStart(
            normalizedOvernight,
            AtLocal(chinaTimeZone, 2026, 7, 30, 12, 0, 0),
            out var overnightNextStart) &&
        overnightNextStart == atOvernightStart,
        "Outside an overnight interval, the next quiet start must be the upcoming evening.");
    var overnightDisplay =
        ScheduledQuietHoursSchedule.FormatDisplayText(normalizedOvernight!);
    Assert(
        overnightDisplay.Contains("22:00:00", StringComparison.Ordinal) &&
        overnightDisplay.Contains("07:00:00", StringComparison.Ordinal),
        "The overnight display text must retain both exact wall-clock times.");

    var suppressedDisplayItem = new ScheduledTaskItem
    {
        Text = "Quiet occurrence display projection",
        DueAt = atOvernightStart.AddHours(1),
        CreatedAt = atOvernightStart.AddDays(-1),
        RepeatInterval = TimeSpan.FromHours(1),
        QuietHours = normalizedOvernight
    };
    var suppressedLocalDueAtText =
        $"{suppressedDisplayItem.DueAt.ToLocalTime():M月d日 HH:mm:ss}";
    Assert(
        suppressedDisplayItem.DueAtDisplayText.Contains(
            "该时段不提醒",
            StringComparison.Ordinal) &&
        !suppressedDisplayItem.DueAtDisplayText.Contains(
            "下次",
            StringComparison.Ordinal) &&
        !suppressedDisplayItem.DueAtDisplayText.Contains(
            suppressedLocalDueAtText,
            StringComparison.Ordinal),
        "A recurring occurrence inside quiet hours must not appear in the " +
        "task row as a pending next reminder.");

    var endBoundaryDisplayItem = new ScheduledTaskItem
    {
        Text = "Quiet end-boundary display projection",
        DueAt = atOvernightEnd,
        CreatedAt = atOvernightStart.AddDays(-1),
        RepeatInterval = TimeSpan.FromHours(1),
        QuietHours = normalizedOvernight
    };
    var endBoundaryLocalDueAtText =
        $"{endBoundaryDisplayItem.DueAt.ToLocalTime():M月d日 HH:mm:ss}";
    Assert(
        endBoundaryDisplayItem.DueAtDisplayText.Contains(
            "下次",
            StringComparison.Ordinal) &&
        endBoundaryDisplayItem.DueAtDisplayText.Contains(
            endBoundaryLocalDueAtText,
            StringComparison.Ordinal) &&
        !endBoundaryDisplayItem.DueAtDisplayText.Contains(
            "该时段不提醒",
            StringComparison.Ordinal),
        "The end-exclusive boundary must remain a real next reminder.");

    suppressedDisplayItem.QuietHours = null;
    Assert(
        suppressedDisplayItem.DueAtDisplayText.Contains(
            "下次",
            StringComparison.Ordinal) &&
        suppressedDisplayItem.DueAtDisplayText.Contains(
            suppressedLocalDueAtText,
            StringComparison.Ordinal) &&
        !suppressedDisplayItem.DueAtDisplayText.Contains(
            "该时段不提醒",
            StringComparison.Ordinal),
        "Disabling quiet hours before a future occurrence must immediately " +
        "restore the authored next-reminder projection.");

    Assert(
        ScheduledQuietHoursSchedule.Normalize(new ScheduledQuietHours
        {
            Version = 1,
            Start = new TimeSpan(8, 0, 0),
            End = new TimeSpan(8, 0, 0),
            TimeZoneId = chinaTimeZone.Id
        }) is null,
        "start=end must be rejected instead of becoming an accidental all-day mute.");
    Assert(
        ScheduledQuietHoursSchedule.Normalize(new ScheduledQuietHours
        {
            Version = -1,
            Start = new TimeSpan(8, 0, 0),
            End = new TimeSpan(9, 0, 0),
            TimeZoneId = chinaTimeZone.Id
        }) is null &&
        ScheduledQuietHoursSchedule.Normalize(new ScheduledQuietHours
        {
            Version = 1,
            Start = new TimeSpan(8, 0, 0),
            End = new TimeSpan(9, 0, 0),
            TimeZoneId = "Invalid/Quiet-Hours-Time-Zone"
        }) is null &&
        ScheduledQuietHoursSchedule.Normalize(new ScheduledQuietHours
        {
            Version = 1,
            Start = new TimeSpan(8, 0, 0),
            End = new TimeSpan(9, 0, 0),
            TimeZoneId = string.Empty
        }) is null,
        "Negative versions and missing or invalid time-zone identifiers must be rejected.");
    Assert(
        ScheduledQuietHoursSchedule.Normalize(new ScheduledQuietHours
        {
            Version = 1,
            Start = TimeSpan.FromSeconds(-1),
            End = new TimeSpan(9, 0, 0),
            TimeZoneId = chinaTimeZone.Id
        }) is null &&
        ScheduledQuietHoursSchedule.Normalize(new ScheduledQuietHours
        {
            Version = 1,
            Start = new TimeSpan(8, 0, 0),
            End = TimeSpan.FromDays(1),
            TimeZoneId = chinaTimeZone.Id
        }) is null,
        "Negative or 24-hour time-of-day values must be rejected.");

    CheckScheduledQuietHoursDst(AtLocal);

    var anchorLocal = new DateTime(
        2026,
        7,
        30,
        10,
        7,
        45,
        DateTimeKind.Unspecified);
    Assert(
        ScheduledRepeatSchedule.TryCreate(
            ScheduledRepeatUnit.Minute,
            5,
            anchorLocal,
            chinaTimeZone,
            out var repeatRule,
            out var firstDueAt) &&
        repeatRule is not null,
        "Quiet-hours persistence checks require a valid recurring rule.");
    var quietStorePath = Path.Combine(
        tempDirectory,
        "scheduled",
        "quiet-hours-roundtrip.json");
    var quietStore = new ScheduledTaskStore(quietStorePath);
    var quietTask = new ScheduledTaskItem
    {
        Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
        Text = "Quiet-hours round trip",
        DueAt = firstDueAt,
        CreatedAt = firstDueAt.AddDays(-1),
        RepeatRule = repeatRule,
        QuietHours = normalizedOvernight
    };
    Assert(
        quietStore.Save([quietTask]),
        "A recurring task with valid quiet hours must save successfully.");
    var quietJson = File.ReadAllText(quietStorePath);
    Assert(
        quietJson.Contains("\"quietHours\"", StringComparison.Ordinal) &&
        quietJson.Contains("\"start\"", StringComparison.Ordinal) &&
        quietJson.Contains("\"end\"", StringComparison.Ordinal) &&
        quietJson.Contains("\"timeZoneId\"", StringComparison.Ordinal),
        "Quiet hours must use readable camelCase JSON fields.");
    var quietReloaded = quietStore.Load().Single();
    Assert(
        quietReloaded.Id == quietTask.Id &&
        quietReloaded.RepeatRule is not null &&
        quietReloaded.QuietHours is { } reloadedQuiet &&
        reloadedQuiet.Version == normalizedOvernight!.Version &&
        reloadedQuiet.Start == normalizedOvernight.Start &&
        reloadedQuiet.End == normalizedOvernight.End &&
        reloadedQuiet.TimeZoneId == normalizedOvernight.TimeZoneId,
        "Valid quiet hours must survive a scheduled-task JSON round trip.");

    var legacyPath = Path.Combine(
        tempDirectory,
        "scheduled",
        "quiet-hours-legacy-null.json");
    var legacyId = Guid.Parse("40000000-0000-0000-0000-000000000002");
    File.WriteAllText(
        legacyPath,
        JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = legacyId,
                Text = "Legacy recurring task without quiet hours",
                DueAt = firstDueAt,
                CreatedAt = firstDueAt.AddDays(-1),
                RepeatIntervalTicks =
                    (long?)TimeSpan.FromMinutes(5).Ticks
            }
        }));
    var legacyReloaded =
        new ScheduledTaskStore(legacyPath).Load().Single();
    Assert(
        legacyReloaded.Id == legacyId &&
        legacyReloaded.IsRecurring &&
        legacyReloaded.QuietHours is null,
        "Legacy JSON without quietHours must continue to load with a null quiet period.");

    var invalidQuietPath = Path.Combine(
        tempDirectory,
        "scheduled",
        "invalid-quiet-hours.json");
    var invalidQuietId =
        Guid.Parse("40000000-0000-0000-0000-000000000003");
    File.WriteAllText(
        invalidQuietPath,
        JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = invalidQuietId,
                Text = "Keep task when quiet hours are invalid",
                DueAt = firstDueAt,
                CreatedAt = firstDueAt.AddDays(-1),
                RepeatIntervalTicks =
                    (long?)TimeSpan.FromMinutes(5).Ticks,
                QuietHours = new
                {
                    Version = 1,
                    Start = new TimeSpan(22, 0, 0),
                    End = new TimeSpan(22, 0, 0),
                    TimeZoneId = chinaTimeZone.Id
                }
            }
        }));
    var invalidQuietReloaded =
        new ScheduledTaskStore(invalidQuietPath).Load().Single();
    Assert(
        invalidQuietReloaded.Id == invalidQuietId &&
        invalidQuietReloaded.Text ==
            "Keep task when quiet hours are invalid" &&
        invalidQuietReloaded.RepeatInterval ==
            TimeSpan.FromMinutes(5) &&
        invalidQuietReloaded.QuietHours is null,
        "Invalid quiet hours must be dropped without dropping or degrading the task.");

    var oneShotPath = Path.Combine(
        tempDirectory,
        "scheduled",
        "one-shot-quiet-hours.json");
    var oneShotStore = new ScheduledTaskStore(oneShotPath);
    var oneShotId =
        Guid.Parse("40000000-0000-0000-0000-000000000004");
    Assert(
        oneShotStore.Save(
        [
            new ScheduledTaskItem
            {
                Id = oneShotId,
                Text = "One-shot task cannot own quiet hours",
                DueAt = firstDueAt.AddHours(1),
                CreatedAt = firstDueAt,
                QuietHours = normalizedSameDay
            }
        ]),
        "A one-shot task containing stale quiet-hours data must still save.");
    var oneShotReloaded = oneShotStore.Load().Single();
    Assert(
        oneShotReloaded.Id == oneShotId &&
        !oneShotReloaded.IsRecurring &&
        oneShotReloaded.QuietHours is null,
        "Quiet hours on a non-recurring task must normalize to null.");
}

static void CheckScheduledQuietHoursDst(
    Func<TimeZoneInfo, int, int, int, int, int, int, DateTimeOffset> atLocal)
{
    var eastern = FindAvailableTimeZone(
        "Eastern Standard Time",
        "America/New_York");
    if (eastern is null)
    {
        return;
    }

    var springQuiet = ScheduledQuietHoursSchedule.Normalize(
        new ScheduledQuietHours
        {
            Version = 1,
            Start = new TimeSpan(1, 30, 0),
            End = new TimeSpan(3, 30, 0),
            TimeZoneId = eastern.Id
        });
    var springStart = atLocal(eastern, 2026, 3, 8, 1, 30, 0);
    var springBeforeEnd = atLocal(eastern, 2026, 3, 8, 3, 29, 59);
    var springEnd = atLocal(eastern, 2026, 3, 8, 3, 30, 0);
    Assert(
        springQuiet is not null &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            springQuiet,
            springStart) &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            springQuiet,
            springBeforeEnd) &&
        !ScheduledQuietHoursSchedule.IsQuietAt(
            springQuiet,
            springEnd) &&
        ScheduledQuietHoursSchedule.TryGetQuietEnd(
            springQuiet,
            springStart,
            out var resolvedSpringEnd) &&
        resolvedSpringEnd == springEnd,
        "Quiet-hours boundaries must remain usable across a spring-forward DST gap.");

    var ambiguousLocal = new DateTime(
        2026,
        11,
        1,
        1,
        45,
        0,
        DateTimeKind.Unspecified);
    if (!eastern.IsAmbiguousTime(ambiguousLocal))
    {
        return;
    }

    var fallQuiet = ScheduledQuietHoursSchedule.Normalize(
        new ScheduledQuietHours
        {
            Version = 1,
            Start = new TimeSpan(1, 30, 0),
            End = new TimeSpan(2, 30, 0),
            TimeZoneId = eastern.Id
        });
    var ambiguousOffsets =
        eastern.GetAmbiguousTimeOffsets(ambiguousLocal);
    var firstAmbiguousInstant =
        new DateTimeOffset(ambiguousLocal, ambiguousOffsets.Max())
            .ToUniversalTime();
    var secondAmbiguousInstant =
        new DateTimeOffset(ambiguousLocal, ambiguousOffsets.Min())
            .ToUniversalTime();
    var fallEnd = atLocal(eastern, 2026, 11, 1, 2, 30, 0);
    Assert(
        fallQuiet is not null &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            fallQuiet,
            firstAmbiguousInstant) &&
        ScheduledQuietHoursSchedule.IsQuietAt(
            fallQuiet,
            secondAmbiguousInstant) &&
        ScheduledQuietHoursSchedule.TryGetQuietEnd(
            fallQuiet,
            firstAmbiguousInstant,
            out var firstResolvedFallEnd) &&
        firstResolvedFallEnd == fallEnd &&
        ScheduledQuietHoursSchedule.TryGetQuietEnd(
            fallQuiet,
            secondAmbiguousInstant,
            out var secondResolvedFallEnd) &&
        secondResolvedFallEnd == fallEnd,
        "Both real instants in a fall-back ambiguous hour must share one wall-clock quiet interval.");

    var firstFallDisplayItem = new ScheduledTaskItem
    {
        Text = "First fall-back quiet projection",
        DueAt = firstAmbiguousInstant,
        CreatedAt = firstAmbiguousInstant.AddDays(-1),
        RepeatInterval = TimeSpan.FromHours(1),
        QuietHours = fallQuiet
    };
    var secondFallDisplayItem = new ScheduledTaskItem
    {
        Text = "Second fall-back quiet projection",
        DueAt = secondAmbiguousInstant,
        CreatedAt = secondAmbiguousInstant.AddDays(-1),
        RepeatInterval = TimeSpan.FromHours(1),
        QuietHours = fallQuiet
    };
    var fallEndDisplayItem = new ScheduledTaskItem
    {
        Text = "Fall-back end-boundary projection",
        DueAt = fallEnd,
        CreatedAt = fallEnd.AddDays(-1),
        RepeatInterval = TimeSpan.FromHours(1),
        QuietHours = fallQuiet
    };
    Assert(
        firstFallDisplayItem.DueAtDisplayText.Contains(
            "该时段不提醒",
            StringComparison.Ordinal) &&
        !firstFallDisplayItem.DueAtDisplayText.Contains(
            "下次",
            StringComparison.Ordinal) &&
        secondFallDisplayItem.DueAtDisplayText.Contains(
            "该时段不提醒",
            StringComparison.Ordinal) &&
        !secondFallDisplayItem.DueAtDisplayText.Contains(
            "下次",
            StringComparison.Ordinal) &&
        fallEndDisplayItem.DueAtDisplayText.Contains(
            "下次",
            StringComparison.Ordinal) &&
        !fallEndDisplayItem.DueAtDisplayText.Contains(
            "该时段不提醒",
            StringComparison.Ordinal),
        "Both fall-back 01:45 instants must project as quiet while the " +
        "end-exclusive 02:30 boundary remains a next reminder.");
}

static TimeZoneInfo? FindAvailableTimeZone(params string[] identifiers)
{
    foreach (var identifier in identifiers)
    {
        if (ScheduledRepeatSchedule.TryFindTimeZoneById(
                identifier,
                out var timeZone))
        {
            return timeZone;
        }
    }

    return null;
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
