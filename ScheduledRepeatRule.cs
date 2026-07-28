using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security;

namespace LubanDesktopPet;

public enum ScheduledRepeatUnit
{
    Minute,
    Hour,
    Day
}

public sealed record ScheduledRepeatRule
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public ScheduledRepeatUnit Unit { get; init; }

    public int Every { get; init; }

    public string TimeZoneId { get; init; } = string.Empty;

    public DateTime AnchorLocal { get; init; }

    public long NextOrdinal { get; init; }
}

public readonly record struct ScheduledRepeatEvaluation(
    long DueCount,
    DateTimeOffset? NextDueAt,
    long? NextOrdinal);

public static class ScheduledRepeatSchedule
{
    private const int MaximumInvalidTimeSearchMinutes = 3 * 24 * 60;

    public static bool TryCreate(
        ScheduledRepeatUnit unit,
        int every,
        DateTime selectedLocal,
        TimeZoneInfo timeZone,
        [NotNullWhen(true)] out ScheduledRepeatRule? rule,
        out DateTimeOffset dueAt)
    {
        rule = null;
        dueAt = default;
        if (timeZone is null)
        {
            return false;
        }

        var anchorLocal = NormalizeLocalToWholeSecond(selectedLocal);
        if (timeZone.IsInvalidTime(anchorLocal))
        {
            return false;
        }

        var candidate = new ScheduledRepeatRule
        {
            Unit = unit,
            Every = every,
            TimeZoneId = timeZone.Id,
            AnchorLocal = anchorLocal,
            NextOrdinal = 0
        };
        if (!TryGetValidatedContext(
                candidate,
                out var validatedTimeZone,
                out _) ||
            !TryGetOccurrenceCore(
                candidate,
                validatedTimeZone,
                candidate.NextOrdinal,
                out dueAt))
        {
            return false;
        }

        rule = candidate;
        return true;
    }

    public static bool TryFindTimeZoneById(
        string? timeZoneId,
        [NotNullWhen(true)] out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return false;
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException
                or InvalidTimeZoneException
                or SecurityException)
        {
            return false;
        }
    }

    public static bool TryGetNominalInterval(
        ScheduledRepeatRule? rule,
        out TimeSpan interval)
    {
        interval = default;
        if (rule is null ||
            rule.Version != ScheduledRepeatRule.CurrentVersion ||
            rule.Every <= 0)
        {
            return false;
        }

        long unitTicks;
        switch (rule.Unit)
        {
            case ScheduledRepeatUnit.Minute:
                unitTicks = TimeSpan.TicksPerMinute;
                break;
            case ScheduledRepeatUnit.Hour:
                unitTicks = TimeSpan.TicksPerHour;
                break;
            case ScheduledRepeatUnit.Day:
                unitTicks = TimeSpan.TicksPerDay;
                break;
            default:
                return false;
        }

        try
        {
            var ticks = checked(unitTicks * rule.Every);
            interval = TimeSpan.FromTicks(ticks);
            return interval >= TimeSpan.FromMinutes(1) &&
                   interval < ScheduledTaskStore.MaximumRepeatInterval;
        }
        catch (OverflowException)
        {
            interval = default;
            return false;
        }
    }

    public static bool TryGetOccurrence(
        ScheduledRepeatRule? rule,
        long ordinal,
        out DateTimeOffset occurrence)
    {
        occurrence = default;
        return TryGetValidatedContext(rule, out var timeZone, out _) &&
               TryGetOccurrenceCore(rule!, timeZone, ordinal, out occurrence);
    }

    public static bool TryValidateForDueAt(
        ScheduledRepeatRule? rule,
        DateTimeOffset dueAt,
        out TimeSpan nominalInterval)
    {
        nominalInterval = default;
        if (!TryGetValidatedContext(
                rule,
                out var timeZone,
                out nominalInterval) ||
            !TryGetOccurrenceCore(
                rule!,
                timeZone,
                rule!.NextOrdinal,
                out var expectedDueAt))
        {
            return false;
        }

        return NormalizeInstantToWholeSecond(expectedDueAt).UtcDateTime.Ticks ==
               NormalizeInstantToWholeSecond(dueAt).UtcDateTime.Ticks;
    }

    public static bool TryEvaluate(
        ScheduledRepeatRule? rule,
        DateTimeOffset currentDueAt,
        DateTimeOffset now,
        out ScheduledRepeatEvaluation evaluation)
    {
        evaluation = default;
        if (!TryGetValidatedContext(rule, out var timeZone, out _) ||
            !TryGetOccurrenceCore(
                rule!,
                timeZone,
                rule!.NextOrdinal,
                out var expectedDueAt) ||
            NormalizeInstantToWholeSecond(expectedDueAt).UtcDateTime.Ticks !=
            NormalizeInstantToWholeSecond(currentDueAt).UtcDateTime.Ticks)
        {
            return false;
        }

        var nowUtcTicks = now.UtcDateTime.Ticks;
        if (expectedDueAt.UtcDateTime.Ticks > nowUtcTicks)
        {
            evaluation = new ScheduledRepeatEvaluation(
                0,
                expectedDueAt,
                rule.NextOrdinal);
            return true;
        }

        var firstDueOrdinal = rule.NextOrdinal;
        var lastDueOrdinal = firstDueOrdinal;
        var step = 1L;
        long upperNotDueOrdinal;
        while (true)
        {
            long candidateOrdinal;
            try
            {
                candidateOrdinal = checked(firstDueOrdinal + step);
            }
            catch (OverflowException)
            {
                upperNotDueOrdinal = long.MaxValue;
                break;
            }

            if (!TryGetOccurrenceCore(
                    rule,
                    timeZone,
                    candidateOrdinal,
                    out var candidate) ||
                candidate.UtcDateTime.Ticks > nowUtcTicks)
            {
                upperNotDueOrdinal = candidateOrdinal;
                break;
            }

            lastDueOrdinal = candidateOrdinal;
            if (step > long.MaxValue / 2)
            {
                upperNotDueOrdinal = long.MaxValue;
                break;
            }

            step *= 2;
        }

        while (upperNotDueOrdinal - lastDueOrdinal > 1)
        {
            var middleOrdinal =
                lastDueOrdinal +
                (upperNotDueOrdinal - lastDueOrdinal) / 2;
            if (TryGetOccurrenceCore(
                    rule,
                    timeZone,
                    middleOrdinal,
                    out var middleOccurrence) &&
                middleOccurrence.UtcDateTime.Ticks <= nowUtcTicks)
            {
                lastDueOrdinal = middleOrdinal;
            }
            else
            {
                upperNotDueOrdinal = middleOrdinal;
            }
        }

        var dueCount = checked(lastDueOrdinal - firstDueOrdinal + 1);
        long? nextOrdinal = null;
        DateTimeOffset? nextDueAt = null;
        try
        {
            var candidateNextOrdinal = checked(lastDueOrdinal + 1);
            if (TryGetOccurrenceCore(
                    rule,
                    timeZone,
                    candidateNextOrdinal,
                    out var candidateNextDueAt))
            {
                nextOrdinal = candidateNextOrdinal;
                nextDueAt = candidateNextDueAt;
            }
        }
        catch (OverflowException)
        {
            // The final representable occurrence remains due. The caller
            // keeps the task rather than silently deleting it.
        }

        evaluation = new ScheduledRepeatEvaluation(
            dueCount,
            nextDueAt,
            nextOrdinal);
        return true;
    }

    public static string FormatRule(ScheduledRepeatRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var anchor = NormalizeLocalToWholeSecond(rule.AnchorLocal);
        return rule.Unit switch
        {
            ScheduledRepeatUnit.Minute =>
                $"每{rule.Every}分钟，第{anchor.Second:00}秒",
            ScheduledRepeatUnit.Hour =>
                $"每{rule.Every}小时，第{anchor.Minute:00}分{anchor.Second:00}秒",
            ScheduledRepeatUnit.Day =>
                $"每{rule.Every}天，{anchor:HH:mm:ss}",
            _ => "循环"
        };
    }

    internal static ScheduledRepeatRule NormalizeForStorage(
        ScheduledRepeatRule rule)
    {
        return rule with
        {
            TimeZoneId = rule.TimeZoneId.Trim(),
            AnchorLocal = NormalizeLocalToWholeSecond(rule.AnchorLocal)
        };
    }

    private static bool TryGetValidatedContext(
        ScheduledRepeatRule? rule,
        [NotNullWhen(true)] out TimeZoneInfo? timeZone,
        out TimeSpan nominalInterval)
    {
        timeZone = null;
        nominalInterval = default;
        return rule is not null &&
               rule.Version == ScheduledRepeatRule.CurrentVersion &&
               rule.NextOrdinal >= 0 &&
               rule.AnchorLocal != default &&
               rule.AnchorLocal.Ticks % TimeSpan.TicksPerSecond == 0 &&
               TryGetNominalInterval(rule, out nominalInterval) &&
               TryFindTimeZoneById(rule.TimeZoneId, out timeZone);
    }

    private static bool TryGetOccurrenceCore(
        ScheduledRepeatRule rule,
        TimeZoneInfo timeZone,
        long ordinal,
        out DateTimeOffset occurrence)
    {
        occurrence = default;
        if (ordinal < 0 ||
            !TryGetNominalInterval(rule, out var nominalInterval))
        {
            return false;
        }

        DateTime nominalLocal;
        try
        {
            nominalLocal = NormalizeLocalToWholeSecond(
                rule.AnchorLocal.AddTicks(
                    checked(ordinal * nominalInterval.Ticks)));
        }
        catch (Exception exception) when (
            exception is OverflowException or ArgumentOutOfRangeException)
        {
            return false;
        }

        return TryResolveLocalOccurrence(
            timeZone,
            nominalLocal,
            out occurrence);
    }

    private static bool TryResolveLocalOccurrence(
        TimeZoneInfo timeZone,
        DateTime nominalLocal,
        out DateTimeOffset occurrence)
    {
        occurrence = default;
        var resolvedLocal = DateTime.SpecifyKind(
            nominalLocal,
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

            var offset = timeZone.IsAmbiguousTime(resolvedLocal)
                ? timeZone.GetAmbiguousTimeOffsets(resolvedLocal).Max()
                : timeZone.GetUtcOffset(resolvedLocal);
            occurrence = new DateTimeOffset(resolvedLocal, offset);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or ArgumentOutOfRangeException
                or InvalidTimeZoneException)
        {
            occurrence = default;
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

    private static DateTime NormalizeLocalToWholeSecond(DateTime value)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        var remainingTicks = unspecified.Ticks % TimeSpan.TicksPerSecond;
        return remainingTicks == 0
            ? unspecified
            : unspecified.AddTicks(-remainingTicks);
    }

    private static DateTimeOffset NormalizeInstantToWholeSecond(
        DateTimeOffset value)
    {
        var remainingTicks = value.Ticks % TimeSpan.TicksPerSecond;
        return remainingTicks == 0
            ? value
            : value.AddTicks(-remainingTicks);
    }
}
