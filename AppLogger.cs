using System.IO;
using System.Text;

namespace LubanDesktopPet;

internal static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    private static string PreferredLogDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "log");

    private static string FallbackLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LubanDesktopPet",
        "log");

    private static string _activeLogDirectory = PreferredLogDirectory;

    public static string LogDirectory
    {
        get
        {
            lock (SyncRoot)
            {
                return _activeLogDirectory;
            }
        }
    }

    public static void Initialize()
    {
        try
        {
            lock (SyncRoot)
            {
                var now = DateTimeOffset.Now;
                var preferredLine = FormatLine(
                    now,
                    "INFO",
                    $"日志初始化完成，目录：{PreferredLogDirectory}");
                if (TryWrite(PreferredLogDirectory, now, preferredLine))
                {
                    _activeLogDirectory = PreferredLogDirectory;
                    return;
                }

                _activeLogDirectory = FallbackLogDirectory;
                var fallbackLine = FormatLine(
                    now,
                    "WARN",
                    $"EXE 同级目录不可写，日志已回退到：{FallbackLogDirectory}");
                _ = TryWrite(FallbackLogDirectory, now, fallbackLine);
            }
        }
        catch
        {
            // 日志不得影响桌宠主流程。
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception exception)
    {
        string metadata;
        try
        {
            metadata = FormatExceptionMetadata(exception);
        }
        catch
        {
            metadata = $"类型：{exception.GetType().FullName ?? exception.GetType().Name}" +
                       Environment.NewLine +
                       "异常元数据读取失败";
        }

        Write(
            "ERROR",
            $"{message}{Environment.NewLine}{metadata}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                var now = DateTimeOffset.Now;
                var line = FormatLine(now, level, message);
                if (TryWrite(_activeLogDirectory, now, line))
                {
                    return;
                }

                if (!string.Equals(
                        _activeLogDirectory,
                        FallbackLogDirectory,
                        StringComparison.OrdinalIgnoreCase) &&
                    TryWrite(FallbackLogDirectory, now, line))
                {
                    _activeLogDirectory = FallbackLogDirectory;
                }
            }
        }
        catch
        {
            // 日志不得影响桌宠主流程。
        }
    }

    private static string FormatLine(
        DateTimeOffset timestamp,
        string level,
        string message)
    {
        return $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] " +
               $"{message}{Environment.NewLine}";
    }

    private static string FormatExceptionMetadata(Exception exception)
    {
        var builder = new StringBuilder();
        Exception? current = exception;
        var depth = 0;
        while (current is not null && depth < 8)
        {
            if (depth > 0)
            {
                builder.AppendLine("内部异常：");
            }

            builder.Append("类型：")
                .AppendLine(current.GetType().FullName ?? current.GetType().Name);
            builder.Append("HResult：0x")
                .AppendLine(current.HResult.ToString("X8"));
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.AppendLine("堆栈：")
                    .AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryWrite(
        string directory,
        DateTimeOffset timestamp,
        string line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var logPath = Path.Combine(
                directory,
                $"xlb-pet-{timestamp:yyyy-MM-dd}.log");
            File.AppendAllText(logPath, line, Utf8WithoutBom);
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
    }
}
