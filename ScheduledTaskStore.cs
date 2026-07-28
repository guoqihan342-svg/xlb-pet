using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LubanDesktopPet;

public sealed class ScheduledTaskStore
{
    internal static readonly TimeSpan MaximumRepeatInterval =
        TimeSpan.FromDays(1000);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _filePath;
    private bool _protectExistingDataAfterLoadFailure;

    public ScheduledTaskStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public static ScheduledTaskStore CreateDefault()
    {
        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new ScheduledTaskStore(
            Path.Combine(appData, "LubanDesktopPet", "scheduled-tasks.json"));
    }

    public IReadOnlyList<ScheduledTaskItem> Load()
    {
        return TryLoad(out var items)
            ? items
            : Array.Empty<ScheduledTaskItem>();
    }

    public bool TryLoad(out IReadOnlyList<ScheduledTaskItem> items)
    {
        try
        {
            if (Directory.Exists(_filePath))
            {
                _protectExistingDataAfterLoadFailure = true;
                items = Array.Empty<ScheduledTaskItem>();
                return false;
            }

            if (!File.Exists(_filePath))
            {
                _protectExistingDataAfterLoadFailure = false;
                items = Array.Empty<ScheduledTaskItem>();
                return true;
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var records = JsonSerializer.Deserialize<List<ScheduledTaskRecord?>>(
                              json,
                              JsonOptions) ?? [];
            items = NormalizeAndSort(records.Select(ToItem));
            _protectExistingDataAfterLoadFailure = false;
            return true;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            // A transient read failure or malformed JSON must never turn into
            // an empty overwrite during application shutdown. The same store
            // instance remains read-only until a later TryLoad succeeds.
            _protectExistingDataAfterLoadFailure = true;
            items = Array.Empty<ScheduledTaskItem>();
            return false;
        }
    }

    public bool Save(IEnumerable<ScheduledTaskItem> items)
    {
        if (items is null)
        {
            return false;
        }

        if (_protectExistingDataAfterLoadFailure)
        {
            return false;
        }

        var tempPath = _filePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var records = NormalizeAndSort(items)
                .Select(item => new ScheduledTaskRecord(
                    item.Id,
                    item.Text,
                    item.DueAt,
                    item.CreatedAt,
                    item.RepeatInterval?.Ticks,
                    ToRecord(item.RepeatRule)))
                .ToArray();
            var json = JsonSerializer.Serialize(records, JsonOptions);

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, _filePath, true);
            return true;
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return false;
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    internal static DateTimeOffset NormalizeToWholeSecond(DateTimeOffset value)
    {
        var remainingTicks = value.Ticks % TimeSpan.TicksPerSecond;
        return remainingTicks == 0
            ? value
            : value.AddTicks(-remainingTicks);
    }

    internal static TimeSpan? NormalizeRepeatInterval(TimeSpan? value)
    {
        if (value is not { } interval ||
            interval < TimeSpan.FromMinutes(1) ||
            interval >= MaximumRepeatInterval)
        {
            return null;
        }

        var wholeMinuteTicks =
            interval.Ticks - interval.Ticks % TimeSpan.TicksPerMinute;
        return wholeMinuteTicks >= TimeSpan.TicksPerMinute
            ? TimeSpan.FromTicks(wholeMinuteTicks)
            : null;
    }

    private static ScheduledTaskItem? ToItem(ScheduledTaskRecord? record)
    {
        if (record is null)
        {
            return null;
        }

        return new ScheduledTaskItem
        {
            Id = record.Id,
            Text = record.Text ?? string.Empty,
            DueAt = record.DueAt,
            CreatedAt = record.CreatedAt,
            RepeatInterval = NormalizeRepeatInterval(
                record.RepeatIntervalTicks is { } ticks && ticks > 0
                    ? TimeSpan.FromTicks(ticks)
                    : null),
            RepeatRule = ToRule(record.RepeatRule)
        };
    }

    private static IReadOnlyList<ScheduledTaskItem> NormalizeAndSort(
        IEnumerable<ScheduledTaskItem?> items)
    {
        var normalized = new List<ScheduledTaskItem>();
        var knownIds = new HashSet<Guid>();

        foreach (var item in items)
        {
            if (item is null ||
                item.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Text) ||
                item.DueAt == default ||
                !knownIds.Add(item.Id))
            {
                continue;
            }

            var dueAt = NormalizeToWholeSecond(item.DueAt);
            var createdAt = item.CreatedAt == default
                ? dueAt
                : item.CreatedAt;
            var repeatInterval = NormalizeRepeatInterval(item.RepeatInterval);
            var repeatRule = NormalizeRepeatRule(
                item.RepeatRule,
                dueAt,
                ref repeatInterval);
            normalized.Add(new ScheduledTaskItem
            {
                Id = item.Id,
                Text = item.Text.Trim(),
                DueAt = dueAt,
                CreatedAt = createdAt,
                RepeatInterval = repeatInterval,
                RepeatRule = repeatRule
            });
        }

        return normalized
            .OrderBy(item => item.DueAt.UtcDateTime.Ticks)
            .ThenBy(item => item.CreatedAt.UtcDateTime.Ticks)
            .ThenBy(item => item.Id)
            .ToArray();
    }

    private static bool IsExpectedStorageFailure(Exception exception)
    {
        return exception is JsonException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException;
    }

    private static void TryDeleteTemporaryFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            // A stale temporary file can be replaced by a later save attempt.
        }
    }

    private static ScheduledRepeatRule? NormalizeRepeatRule(
        ScheduledRepeatRule? rule,
        DateTimeOffset dueAt,
        ref TimeSpan? repeatInterval)
    {
        if (rule is null ||
            !ScheduledRepeatSchedule.TryValidateForDueAt(
                rule,
                dueAt,
                out var ruleInterval))
        {
            return null;
        }

        if (repeatInterval is { } legacyInterval &&
            legacyInterval != ruleInterval)
        {
            return null;
        }

        repeatInterval = ruleInterval;
        return ScheduledRepeatSchedule.NormalizeForStorage(rule);
    }

    private static ScheduledRepeatRule? ToRule(
        ScheduledRepeatRuleRecord? record)
    {
        if (record is null ||
            !Enum.TryParse<ScheduledRepeatUnit>(
                record.Unit,
                ignoreCase: true,
                out var unit))
        {
            return null;
        }

        return new ScheduledRepeatRule
        {
            Version = record.Version,
            Unit = unit,
            Every = record.Every,
            TimeZoneId = record.TimeZoneId ?? string.Empty,
            AnchorLocal = DateTime.SpecifyKind(
                record.AnchorLocal,
                DateTimeKind.Unspecified),
            NextOrdinal = record.NextOrdinal
        };
    }

    private static ScheduledRepeatRuleRecord? ToRecord(
        ScheduledRepeatRule? rule)
    {
        if (rule is null)
        {
            return null;
        }

        return new ScheduledRepeatRuleRecord(
            rule.Version,
            rule.Unit.ToString().ToLowerInvariant(),
            rule.Every,
            rule.TimeZoneId,
            DateTime.SpecifyKind(
                rule.AnchorLocal,
                DateTimeKind.Unspecified),
            rule.NextOrdinal);
    }

    private sealed record ScheduledTaskRecord(
        Guid Id,
        string? Text,
        DateTimeOffset DueAt,
        DateTimeOffset CreatedAt,
        long? RepeatIntervalTicks = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ScheduledRepeatRuleRecord? RepeatRule = null);

    private sealed record ScheduledRepeatRuleRecord(
        int Version,
        string? Unit,
        int Every,
        string? TimeZoneId,
        DateTime AnchorLocal,
        long NextOrdinal);
}
