using LubanDesktopPet;

var tempDirectory = Path.Combine(Path.GetTempPath(), "LubanDesktopPetChecks", Guid.NewGuid().ToString("N"));
var filePath = Path.Combine(tempDirectory, "todos.json");

try
{
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

    Console.WriteLine("TodoStore checks passed.");
}
finally
{
    if (Directory.Exists(tempDirectory))
    {
        Directory.Delete(tempDirectory, true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
