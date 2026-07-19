using System;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;

namespace LubanDesktopPet;

public sealed class AppSettings
{
    public double PetSizeScale { get; set; } = 1.0;
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _filePath;

    public AppSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public static AppSettingsStore CreateDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppSettingsStore(Path.Combine(appData, "LubanDesktopPet", "settings.json"));
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception exception) when (IsExpectedStorageFailure(exception))
        {
            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings)
    {
        if (settings is null)
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

            var json = JsonSerializer.Serialize(settings, JsonOptions);
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
            // A leftover temporary file is harmless and can be replaced next time.
        }
    }
}
