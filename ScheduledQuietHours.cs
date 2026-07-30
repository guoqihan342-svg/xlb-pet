using System;
using System.Diagnostics.CodeAnalysis;

namespace LubanDesktopPet;

public sealed record ScheduledQuietHours
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public TimeSpan Start { get; init; }

    public TimeSpan End { get; init; }

    public string TimeZoneId { get; init; } = string.Empty;
}

public static class ScheduledQuietHoursSchedule
{
    private const int MaximumInvalidTimeSearchMinutes = 3 * 24 * 60;

    public static ScheduledQuietHours? Normalize(
        ScheduledQuietHours? quietHours)
    {
        if (quietHours is null ||
            quietHours.Version != ScheduledQuietHours.CurrentVersion ||
            !IsTimeOfDay(quietHours.Start) ||
            !IsTimeOfDay(quietHours.End) ||
            !ScheduledRepeatSchedule.TryFindTimeZoneById(
                quietHours.TimeZoneId,
                out _))
        {
            return null;
        }

        var start = NormalizeToWholeSecond(quietHours.Start);
        var end = NormalizeToWholeSecond(quietHours.End);
        if (start == end)
        {
            return null;
        }

        return quietHours with
        {
            Start = start,
            End = end,
            TimeZoneId = quietHours.TimeZoneId.Trim()
        };
    }

    public static bool IsQuietAt(
        ScheduledQuietHours? quietHours,
        DateTimeOffset instant)
    {
        return TryGetContainingInterval(
            quietHours,
            instant,
            out _);
    }

    public static bool TryGetQuietEnd(
        ScheduledQuietHours? quietHours,
        DateTimeOffset instant,
        out DateTimeOffset quietEnd)
    {
        return TryGetContainingInterval(
            quietHours,
            instant,
            out quietEnd);
    }

    public static bool TryGetNextQuietStart(
        ScheduledQuietHours? quietHours,
        DateTimeOffset instant,
        out DateTimeOffset quietStart)
    {
        quietStart = default;
        if (!TryGetValidatedContext(
                quietHours,
                out var normalized,
                out var timeZone))
        {
            return false;
        }

        DateTime localDate;
        try
        {
            localDate = TimeZoneInfo.ConvertTime(instant, timeZone).Date;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or ArgumentOutOfRangeException
                or InvalidTimeZoneException)
        {
            return false;
        }

        for (var dayOffset = 0; dayOffset < 4; dayOffset++)
        {
            DateTime candidateLocal;
            try
            {
                candidateLocal = DateTime.SpecifyKind(
                    localDate.AddDays(dayOffset).Add(normalized.Start),
                    DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (TryResolveBoundary(
                    timeZone,
                    candidateLocal,
                    chooseLaterAmbiguousInstant: false,
                    out var candidate) &&
                candidate.UtcDateTime.Ticks > instant.UtcDateTime.Ticks)
            {
                quietStart = candidate;
                return true;
            }
        }

        return false;
    }

    public static string FormatDisplayText(
        ScheduledQuietHours quietHours)
    {
        ArgumentNullException.ThrowIfNull(quietHours);
        var normalized = Normalize(quietHours);
        if (normalized is null)
        {
            return "免打扰时段无效";
        }

        var startText = FormatTimeOfDay(normalized.Start);
        var endText = FormatTimeOfDay(normalized.End);
        return normalized.Start < normalized.End
            ? $"免打扰 {startText}–{endText}"
            : $"免打扰 {startText}–次日 {endText}";
    }

    private static bool TryGetContainingInterval(
        ScheduledQuietHours? quietHours,
        DateTimeOffset instant,
        out DateTimeOffset quietEnd)
    {
        quietEnd = default;
        if (!TryGetValidatedContext(
                quietHours,
                out var normalized,
                out var timeZone))
        {
            return false;
        }

        DateTime localDate;
        try
        {
            localDate = TimeZoneInfo.ConvertTime(instant, timeZone).Date;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or ArgumentOutOfRangeException
                or InvalidTimeZoneException)
        {
            return false;
        }

        var instantUtcTicks = instant.UtcDateTime.Ticks;
        var found = false;
        for (var dayOffset = -1; dayOffset <= 0; dayOffset++)
        {
            DateTime intervalDate;
            try
            {
                intervalDate = localDate.AddDays(dayOffset);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (!TryBuildInterval(
                    normalized,
                    timeZone,
                    intervalDate,
                    out var start,
                    out var end) ||
                instantUtcTicks < start.UtcDateTime.Ticks ||
                instantUtcTicks >= end.UtcDateTime.Ticks)
            {
                continue;
            }

            if (!found ||
                end.UtcDateTime.Ticks > quietEnd.UtcDateTime.Ticks)
            {
                quietEnd = end;
                found = true;
            }
        }

        return found;
    }

    private static bool TryBuildInterval(
        ScheduledQuietHours quietHours,
        TimeZoneInfo timeZone,
        DateTime intervalDate,
        out DateTimeOffset start,
        out DateTimeOffset end)
    {
        start = default;
        end = default;

        DateTime startLocal;
        DateTime endLocal;
        try
        {
            startLocal = DateTime.SpecifyKind(
                intervalDate.Date.Add(quietHours.Start),
                DateTimeKind.Unspecified);
            var endDate = quietHours.Start < quietHours.End
                ? intervalDate.Date
                : intervalDate.Date.AddDays(1);
            endLocal = DateTime.SpecifyKind(
                endDate.Add(quietHours.End),
                DateTimeKind.Unspecified);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return TryResolveBoundary(
                   timeZone,
                   startLocal,
                   chooseLaterAmbiguousInstant: false,
                   out start) &&
               TryResolveBoundary(
                   timeZone,
                   endLocal,
                   chooseLaterAmbiguousInstant: true,
                   out end) &&
               start.UtcDateTime.Ticks < end.UtcDateTime.Ticks;
    }

    private static bool TryResolveBoundary(
        TimeZoneInfo timeZone,
        DateTime local,
        bool chooseLaterAmbiguousInstant,
        out DateTimeOffset instant)
    {
        instant = default;
        var resolvedLocal = DateTime.SpecifyKind(
            local,
            DateTimeKind.Unspecified);
        try
        {
            if (timeZone.IsInvalidTime(resolvedLocal) &&
                !TryFindFirstValidLocalTime(
                    timeZone,
                    resolvedLocal,
                    out resolvedLocal))
            {
                return false;
            }

            TimeSpan offset;
            if (timeZone.IsAmbiguousTime(resolvedLocal))
            {
                var offsets = timeZone.GetAmbiguousTimeOffsets(resolvedLocal);
                offset = offsets[0];
                for (var index = 1; index < offsets.Length; index++)
                {
                    if (chooseLaterAmbiguousInstant
                            ? offsets[index] < offset
                            : offsets[index] > offset)
                    {
                        offset = offsets[index];
                    }
                }
            }
            else
            {
                offset = timeZone.GetUtcOffset(resolvedLocal);
            }

            instant = new DateTimeOffset(resolvedLocal, offset);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or ArgumentOutOfRangeException
                or InvalidTimeZoneException)
        {
            instant = default;
            return false;
        }
    }

    private static bool TryFindFirstValidLocalTime(
        TimeZoneInfo timeZone,
        DateTime invalidLocal,
        out DateTime firstValidLocal)
    {
        firstValidLocal = default;
        var upper = invalidLocal;
        var foundUpper = false;
        for (var minute = 0;
             minute < MaximumInvalidTimeSearchMinutes;
             minute++)
        {
            try
            {
                upper = upper.AddMinutes(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (!timeZone.IsInvalidTime(upper))
            {
                foundUpper = true;
                break;
            }
        }

        if (!foundUpper)
        {
            return false;
        }

        var invalidSecond = invalidLocal.Ticks / TimeSpan.TicksPerSecond;
        var validSecond = upper.Ticks / TimeSpan.TicksPerSecond;
        while (validSecond - invalidSecond > 1)
        {
            var middleSecond =
                invalidSecond + (validSecond - invalidSecond) / 2;
            var middleLocal = new DateTime(
                checked(middleSecond * TimeSpan.TicksPerSecond),
                DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(middleLocal))
            {
                invalidSecond = middleSecond;
            }
            else
            {
                validSecond = middleSecond;
            }
        }

        firstValidLocal = new DateTime(
            checked(validSecond * TimeSpan.TicksPerSecond),
            DateTimeKind.Unspecified);
        return true;
    }

    private static bool TryGetValidatedContext(
        ScheduledQuietHours? quietHours,
        [NotNullWhen(true)] out ScheduledQuietHours? normalized,
        [NotNullWhen(true)] out TimeZoneInfo? timeZone)
    {
        normalized = Normalize(quietHours);
        timeZone = null;
        return normalized is not null &&
               ScheduledRepeatSchedule.TryFindTimeZoneById(
                   normalized.TimeZoneId,
                   out timeZone);
    }

    private static bool IsTimeOfDay(TimeSpan value)
    {
        return value >= TimeSpan.Zero &&
               value < TimeSpan.FromDays(1);
    }

    private static TimeSpan NormalizeToWholeSecond(TimeSpan value)
    {
        return TimeSpan.FromTicks(
            value.Ticks - value.Ticks % TimeSpan.TicksPerSecond);
    }

    private static string FormatTimeOfDay(TimeSpan value)
    {
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
}
