using System.Globalization;
using System.Text;

namespace ProcessTracker.Service.Logging;

/// <summary>
/// User mode's log: <c>processtracker.log</c> in a folder, one line per entry (UTC time, level, category, message, then
/// the exception if any). When the next entry would take the file past the size limit, it becomes
/// <c>processtracker.log.1</c> (replacing the previous one) and a new file starts, so at most two files exist.
/// Levels are left to logging filters. Write failures are swallowed: logging must never stop the service.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string FileName = "processtracker.log";
    public const long DefaultMaxBytes = 5L * 1024 * 1024;

    private readonly string _path;
    private readonly string _rolledPath;
    private readonly long _maxBytes;
    private readonly Lock _lock = new();
    private FileStream? _stream;

    public FileLoggerProvider(string directory, long maxBytes = DefaultMaxBytes)
    {
        _path = Path.Combine(directory, FileName);
        _rolledPath = _path + ".1";
        _maxBytes = maxBytes;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ").Append(category).Append(": ").Append(message);
        if (exception is not null) line.AppendLine().Append(exception);
        var bytes = Encoding.UTF8.GetBytes(line.AppendLine().ToString());

        lock (_lock)
        {
            try
            {
                _stream ??= Open();
                if (_stream.Length > 0 && _stream.Length + bytes.Length > _maxBytes)
                {
                    _stream.Dispose();
                    File.Move(_path, _rolledPath, overwrite: true);
                    _stream = Open();
                }
                _stream.Write(bytes);
                _stream.Flush();
            }
            catch (IOException) { DropStream(); }
            catch (UnauthorizedAccessException) { DropStream(); }
        }
    }

    /// <summary>Appends; others may read the file while it is open.</summary>
    private FileStream Open() => new(_path, FileMode.Append, FileAccess.Write, FileShare.Read);

    private void DropStream()
    {
        try { _stream?.Dispose(); }
        catch (IOException) { }
        _stream = null; // reopened on the next entry
    }

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
