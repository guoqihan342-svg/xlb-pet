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

    public TimeSpan? RepeatInterval { get; set; }

    public ScheduledRepeatRule? RepeatRule { get; set; }

    public bool IsRecurring =>
        RepeatRule is not null ||
        RepeatInterval is { } interval && interval > TimeSpan.Zero;

    public bool IsLegacyRecurring =>
        RepeatRule is null &&
        RepeatInterval is { } interval &&
        interval > TimeSpan.Zero;

    public string RepeatDisplayText =>
        RepeatRule is { } rule
            ? ScheduledRepeatSchedule.FormatRule(rule)
            : RepeatInterval is { } interval && interval > TimeSpan.Zero
            ? FormatRepeatInterval(interval)
            : "单次";

    public string DueAtDisplayText =>
        IsRecurring
            ? $"{RepeatDisplayText} · 下次 {DueAt.ToLocalTime():M月d日 HH:mm:ss}"
            : DueAt.ToLocalTime().ToString(
                "yyyy年M月d日 ddd HH:mm:ss",
                ChineseCulture);

    public string DueDateDisplayText =>
        DueAt.ToLocalTime().ToString("M月d日 ddd", ChineseCulture);

    public string DueTimeDisplayText =>
        DueAt.ToLocalTime().ToString("HH:mm:ss", ChineseCulture);

    internal static string FormatRepeatInterval(TimeSpan interval)
    {
        var totalMinutes = Math.Max(1L, (long)interval.TotalMinutes);
        var days = totalMinutes / (24 * 60);
        var hours = totalMinutes / 60 % 24;
        var minutes = totalMinutes % 60;
        var parts = new System.Collections.Generic.List<string>(3);
        if (days > 0)
        {
            parts.Add($"{days}天");
        }

        if (hours > 0)
        {
            parts.Add($"{hours}小时");
        }

        if (minutes > 0)
        {
            parts.Add($"{minutes}分钟");
        }

        return $"每{string.Concat(parts)}";
    }
}
