using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LubanDesktopPet;

public sealed class TodoStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _saveSyncRoot = new();

    public TodoStore(string filePath)
    {
        _filePath = filePath;
    }

    public static TodoStore CreateDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new TodoStore(Path.Combine(appData, "LubanDesktopPet", "todos.json"));
    }

    public IReadOnlyList<TodoItem> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<TodoItem>();
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var records = JsonSerializer.Deserialize<List<TodoRecord>>(json, JsonOptions) ?? new List<TodoRecord>();
            return records
                .Where(record => !string.IsNullOrWhiteSpace(record.Text))
                .Select(record => new TodoItem
                {
                    Text = record.Text.Trim(),
                    IsCompleted = record.IsCompleted
                })
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<TodoItem>();
        }
        catch (IOException)
        {
            return Array.Empty<TodoItem>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<TodoItem>();
        }
    }

    public bool Save(IEnumerable<TodoItem> items)
    {
        lock (_saveSyncRoot)
        {
            return SaveCore(items);
        }
    }

    private bool SaveCore(IEnumerable<TodoItem> items)
    {
        var tempPath = _filePath + ".tmp";

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var records = items
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .Select(item => new TodoRecord(item.Text.Trim(), item.IsCompleted))
                .ToArray();
            var json = JsonSerializer.Serialize(records, JsonOptions);

            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, _filePath, true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
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
        catch (IOException)
        {
            // A later save can replace the same temporary file.
        }
        catch (UnauthorizedAccessException)
        {
            // Persistence failures must not crash the desktop pet.
        }
    }

    private sealed record TodoRecord(string Text, bool IsCompleted);
}
