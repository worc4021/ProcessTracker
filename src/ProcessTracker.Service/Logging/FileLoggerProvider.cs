using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ProcessTracker.Service.Logging;

/// <summary>
/// User mode's log: <c>processtracker.log</c> in a folder, one line per entry (UTC time, level, category, message, then
/// the exception if any). When the next entry would take the file past the size limit, it becomes
/// <c>processtracker.log.1</c> (replacing the previous one) and a new file starts, so at most two files exist.
/// Levels are left to logging filters. Write failures are swallowed: logging must never stop the service.
/// Another process (a takeover or rollback) may log to the same file at the same time; a named mutex per file, in this
/// logon session, makes each entry's size check, rollover and append one step across processes, and the file is opened
/// for each entry, so an entry never lands in a file another process has just rolled over.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string FileName = "processtracker.log";
    public const long DefaultMaxBytes = 5L * 1024 * 1024;

    private readonly string _path;
    private readonly string _rolledPath;
    private readonly long _maxBytes;
    private readonly Mutex _fileMutex;
    private bool _disposed;
    /// <summary>How long an entry waits for another process's entry; after that it is written anyway.</summary>
    private static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(1);

    public FileLoggerProvider(string directory, long maxBytes = DefaultMaxBytes)
    {
        _path = Path.Combine(directory, FileName);
        _rolledPath = _path + ".1";
        _maxBytes = maxBytes;
        Directory.CreateDirectory(directory);
        _fileMutex = OpenMutex(MutexName(_path));
    }

    /// <summary>The named mutex; when the name can't be used (taken by another kind of object, or denied), an unnamed one.</summary>
    private static Mutex OpenMutex(string name)
    {
        try { return new Mutex(false, name); }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return new Mutex(false);
        }
    }

    /// <summary><c>Local\ProcessTracker.Log.&lt;hash of the full path&gt;</c>: one per log file, case-insensitive like the file system.</summary>
    public static string MutexName(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return @"Local\ProcessTracker.Log." + Convert.ToHexString(hash, 0, 16);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_fileMutex)
        {
            if (_disposed) return;
            _disposed = true;
            _fileMutex.Dispose();
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ").Append(category).Append(": ").Append(message);
        if (exception is not null) line.AppendLine().Append(exception);
        var bytes = Encoding.UTF8.GetBytes(line.AppendLine().ToString());

        // The monitor orders this process's threads; the mutex orders the processes (it is owned per thread, so it
        // alone would not keep a second thread of this process out while the first waits on the file).
        lock (_fileMutex)
        {
            if (_disposed) return;
            var owned = Acquire();
            try
            {
                using var stream = Open();
                if (stream.Length > 0 && stream.Length + bytes.Length > _maxBytes)
                {
                    stream.Dispose();
                    // A reader holding either file without FILE_SHARE_DELETE blocks the rollover: then the file grows past
                    // the limit until it is free, rather than every entry being lost meanwhile.
                    try { File.Move(_path, _rolledPath, overwrite: true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    using var fresh = Open();
                    fresh.Write(bytes);
                }
                else stream.Write(bytes);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                if (owned) _fileMutex.ReleaseMutex();
            }
        }
    }

    private bool Acquire()
    {
        try { return _fileMutex.WaitOne(MutexWait); }
        catch (AbandonedMutexException) { return true; } // its owner died mid-entry; this thread owns it now
    }

    /// <summary>
    /// Appends. Others may read, write (another process's entry, should it ever not wait for the mutex) and rename or
    /// delete it (rolling over) while it is open.
    /// </summary>
    private FileStream Open() => new(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
