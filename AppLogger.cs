using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace LubanDesktopPet;

internal static class AppLogger
{
    private const int QueueCapacity = 256;
    private static readonly object StateSyncRoot = new();
    private static readonly object QueueSyncRoot = new();
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly BlockingCollection<LogEntry> PendingEntries =
        new(new ConcurrentQueue<LogEntry>(), QueueCapacity);
    private static readonly ManualResetEventSlim QueueDrained = new(initialState: true);

    private static string PreferredLogDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "log");

    private static string FallbackLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LubanDesktopPet",
        "log");

    private static string _activeLogDirectory = PreferredLogDirectory;
    private static Thread? _writerThread;
    private static bool _initialized;
    private static bool _processExitSubscribed;
    private static bool _closing;
    private static int _droppedEntryCount;

    public static string LogDirectory
    {
        get
        {
            lock (StateSyncRoot)
            {
                return _activeLogDirectory;
            }
        }
    }

    public static void Initialize()
    {
        try
        {
            lock (StateSyncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                var now = DateTimeOffset.Now;
                var preferredLine = FormatLine(
                    now,
                    "INFO",
                    $"日志初始化完成，目录：{PreferredLogDirectory}");
                if (TryWrite(PreferredLogDirectory, now, preferredLine))
                {
                    _activeLogDirectory = PreferredLogDirectory;
                }
                else
                {
                    _activeLogDirectory = FallbackLogDirectory;
                    var fallbackLine = FormatLine(
                        now,
                        "WARN",
                        $"EXE 同级目录不可写，日志已回退到：{FallbackLogDirectory}");
                    _ = TryWrite(FallbackLogDirectory, now, fallbackLine);
                }

                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "LubanDesktopPet.LogWriter"
                };
                _initialized = true;
                _writerThread.Start();

                if (!_processExitSubscribed)
                {
                    AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
                    _processExitSubscribed = true;
                }
            }
        }
        catch
        {
            // 日志不得影响桌宠主流程。
        }
    }

    public static void Info(string message)
    {
        Enqueue("INFO", message);
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

        Enqueue(
            "ERROR",
            $"{message}{Environment.NewLine}{metadata}");
    }

    public static bool Flush(TimeSpan timeout)
    {
        try
        {
            if (!_initialized)
            {
                return true;
            }

            return QueueDrained.Wait(timeout);
        }
        catch
        {
            return false;
        }
    }

    private static void Enqueue(string level, string message)
    {
        try
        {
            if (!_initialized)
            {
                Initialize();
            }

            var entry = new LogEntry(DateTimeOffset.Now, level, message);
            lock (QueueSyncRoot)
            {
                if (_closing || PendingEntries.IsAddingCompleted)
                {
                    return;
                }

                QueueDrained.Reset();
                if (PendingEntries.TryAdd(entry))
                {
                    return;
                }

                // Keep the newest diagnostic data. Dropping the oldest entry also
                // guarantees a render callback can never block on disk I/O.
                if (PendingEntries.TryTake(out _))
                {
                    Interlocked.Increment(ref _droppedEntryCount);
                }

                if (!PendingEntries.TryAdd(entry))
                {
                    Interlocked.Increment(ref _droppedEntryCount);
                    if (PendingEntries.Count == 0)
                    {
                        QueueDrained.Set();
                    }
                }
            }
        }
        catch
        {
            // 日志不得影响桌宠主流程。
        }
    }

    private static void WriterLoop()
    {
        try
        {
            foreach (var entry in PendingEntries.GetConsumingEnumerable())
            {
                var droppedCount = Interlocked.Exchange(ref _droppedEntryCount, 0);
                if (droppedCount > 0)
                {
                    var droppedTimestamp = DateTimeOffset.Now;
                    WriteSynchronously(new LogEntry(
                        droppedTimestamp,
                        "WARN",
                        $"日志队列已满，丢弃最旧记录 {droppedCount} 条"));
                }

                WriteSynchronously(entry);
                lock (QueueSyncRoot)
                {
                    if (PendingEntries.Count == 0)
                    {
                        QueueDrained.Set();
                    }
                }
            }
        }
        catch
        {
            QueueDrained.Set();
        }
    }

    private static void WriteSynchronously(LogEntry entry)
    {
        try
        {
            var line = FormatLine(entry.Timestamp, entry.Level, entry.Message);
            lock (StateSyncRoot)
            {
                if (TryWrite(_activeLogDirectory, entry.Timestamp, line))
                {
                    return;
                }

                if (!string.Equals(
                        _activeLogDirectory,
                        FallbackLogDirectory,
                        StringComparison.OrdinalIgnoreCase) &&
                    TryWrite(FallbackLogDirectory, entry.Timestamp, line))
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

    private static void CurrentDomain_ProcessExit(object? sender, EventArgs e)
    {
        try
        {
            Thread? writerThread;
            lock (QueueSyncRoot)
            {
                _closing = true;
                if (!PendingEntries.IsAddingCompleted)
                {
                    PendingEntries.CompleteAdding();
                }

                writerThread = _writerThread;
            }

            QueueDrained.Wait(TimeSpan.FromSeconds(2));
            writerThread?.Join(millisecondsTimeout: 500);
        }
        catch
        {
            // 进程退出时不再抛出日志异常。
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

    private readonly record struct LogEntry(
        DateTimeOffset Timestamp,
        string Level,
        string Message);
}
