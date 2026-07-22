using System;
using System.Globalization;

namespace LubanDesktopPet;

public sealed class ScheduledTaskItem
{
    private static readonly CultureInfo ChineseCulture =
        CultureInfo.GetCultureInfo("zh-CN");

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string DueAtDisplayText =>
        DueAt.ToLocalTime().ToString(
            "yyyy年M月d日 ddd HH:mm:ss",
            ChineseCulture);

    public string DueDateDisplayText =>
        DueAt.ToLocalTime().ToString("M月d日 ddd", ChineseCulture);

    public string DueTimeDisplayText =>
        DueAt.ToLocalTime().ToString("HH:mm:ss", ChineseCulture);
}
