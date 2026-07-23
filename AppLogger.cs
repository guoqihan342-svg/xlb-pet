using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace LubanDesktopPet;

internal static class AppLogger
{
    private const int QueueCapacity = 256;
    private const int MaxLogEntryBytes = 32 * 1024;
    private const long MaxLogFileBytes = 2L * 1024 * 1024;
    private const int MaxRetainedLogFiles = 8;
    private const long MaxTotalLogBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan MaxLogAge = TimeSpan.FromDays(14);
    private const string TruncatedMessageSuffix = "\n[log entry truncated]";
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

    public static bool Shutdown(TimeSpan timeout)
    {
        try
        {
            if (!_initialized)
            {
                return true;
            }

            var stopwatch = Stopwatch.StartNew();
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

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                _ = QueueDrained.Wait(remaining);
            }

            remaining = timeout - stopwatch.Elapsed;
            if (writerThread is not null && writerThread.IsAlive)
            {
                if (remaining > TimeSpan.Zero)
                {
                    _ = writerThread.Join(remaining);
                }
            }

            return writerThread is null || !writerThread.IsAlive;
        }
        catch
        {
            // Logging shutdown must never prevent the application from exiting.
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

            var entry = new LogEntry(
                DateTimeOffset.Now,
                level,
                TruncateMessageToUtf8Limit(message));
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
        _ = Shutdown(TimeSpan.FromSeconds(2));
    }

    private static string FormatLine(
        DateTimeOffset timestamp,
        string level,
        string message)
    {
        return $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] " +
               $"{message}{Environment.NewLine}";
    }

    private static string TruncateMessageToUtf8Limit(string message)
    {
        if (Utf8WithoutBom.GetByteCount(message) <= MaxLogEntryBytes)
        {
            return message;
        }

        var suffixByteCount = Utf8WithoutBom.GetByteCount(TruncatedMessageSuffix);
        var maximumPrefixBytes = MaxLogEntryBytes - suffixByteCount;
        var buffer = ArrayPool<byte>.Shared.Rent(maximumPrefixBytes);
        try
        {
            var encoder = Utf8WithoutBom.GetEncoder();
            encoder.Convert(
                message.AsSpan(),
                buffer.AsSpan(0, maximumPrefixBytes),
                flush: false,
                out _,
                out var bytesUsed,
                out _);
            return Utf8WithoutBom.GetString(buffer, 0, bytesUsed) +
                   TruncatedMessageSuffix;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
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
            var lineByteCount = Utf8WithoutBom.GetByteCount(line);
            var logPath = PrepareLogPathForAppend(
                directory,
                timestamp,
                lineByteCount);
            File.AppendAllText(logPath, line, Utf8WithoutBom);
            MaintainLogDirectory(directory, logPath, timestamp);
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

    private static string PrepareLogPathForAppend(
        string directory,
        DateTimeOffset timestamp,
        int incomingByteCount)
    {
        var logPath = Path.Combine(
            directory,
            $"xlb-pet-{timestamp:yyyy-MM-dd}.log");
        if (!File.Exists(logPath) ||
            new FileInfo(logPath).Length + incomingByteCount <= MaxLogFileBytes)
        {
            return logPath;
        }

        for (var sequence = 1; sequence <= 999; sequence++)
        {
            var archivePath = Path.Combine(
                directory,
                $"xlb-pet-{timestamp:yyyy-MM-dd}.{sequence:000}.log");
            if (File.Exists(archivePath))
            {
                continue;
            }

            File.Move(logPath, archivePath);
            return logPath;
        }

        throw new IOException("The daily log rotation sequence is exhausted.");
    }

    private static void MaintainLogDirectory(
        string directory,
        string activeLogPath,
        DateTimeOffset now)
    {
        try
        {
            var activeFullPath = Path.GetFullPath(activeLogPath);
            var files = Directory.EnumerateFiles(
                    directory,
                    "xlb-pet-*.log",
                    SearchOption.TopDirectoryOnly)
                .Where(IsManagedLogFile)
                .Select(path => new FileInfo(path))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToList();
            var cutoff = now.UtcDateTime - MaxLogAge;

            foreach (var file in files.ToArray())
            {
                if (file.LastWriteTimeUtc >= cutoff ||
                    PathsEqual(file.FullName, activeFullPath) ||
                    !TryDeleteLogFile(file))
                {
                    continue;
                }

                files.Remove(file);
            }

            while (files.Count > MaxRetainedLogFiles)
            {
                var removed = false;
                foreach (var candidate in files
                             .Where(file => !PathsEqual(file.FullName, activeFullPath))
                             .ToArray())
                {
                    if (!TryDeleteLogFile(candidate))
                    {
                        continue;
                    }

                    files.Remove(candidate);
                    removed = true;
                    break;
                }

                if (!removed)
                {
                    break;
                }
            }

            var totalBytes = files.Sum(file => file.Exists ? file.Length : 0L);
            while (totalBytes > MaxTotalLogBytes)
            {
                var removed = false;
                foreach (var candidate in files
                             .Where(file => !PathsEqual(file.FullName, activeFullPath))
                             .ToArray())
                {
                    var candidateBytes = candidate.Exists ? candidate.Length : 0L;
                    if (!TryDeleteLogFile(candidate))
                    {
                        continue;
                    }

                    files.Remove(candidate);
                    totalBytes -= candidateBytes;
                    removed = true;
                    break;
                }

                if (!removed)
                {
                    break;
                }
            }
        }
        catch (IOException)
        {
            // A later background write retries maintenance.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging remains best-effort and must not affect the pet.
        }
    }

    private static bool IsManagedLogFile(string path)
    {
        var name = Path.GetFileName(path);
        const string prefix = "xlb-pet-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length < prefix.Length + 10 + 4)
        {
            return false;
        }

        var dateText = name.AsSpan(prefix.Length, 10);
        if (!DateOnly.TryParseExact(
                dateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return false;
        }

        var suffix = name.AsSpan(prefix.Length + 10);
        if (suffix.SequenceEqual(".log"))
        {
            return true;
        }

        return suffix.Length == 8 &&
               suffix[0] == '.' &&
               char.IsAsciiDigit(suffix[1]) &&
               char.IsAsciiDigit(suffix[2]) &&
               char.IsAsciiDigit(suffix[3]) &&
               suffix[4..].SequenceEqual(".log");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool TryDeleteLogFile(FileInfo file)
    {
        try
        {
            file.Delete();
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
