using System.Collections.Concurrent;
using System.Text;

namespace Soulman.Logging;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly BlockingCollection<string> _entryQueue = new();
    private readonly Task _writeTask;
    private readonly CancellationTokenSource _cts = new();

    public FileLoggerProvider()
    {
        _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulman", "logs");
        Directory.CreateDirectory(_logDirectory);
        _writeTask = Task.Run(ProcessQueue);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));
    }

    internal void Enqueue(string message)
    {
        if (!_cts.IsCancellationRequested)
        {
            _entryQueue.Add(message);
        }
    }

    private async Task ProcessQueue()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var line = _entryQueue.Take(_cts.Token);
                var path = Path.Combine(_logDirectory, $"soulman-{DateTime.Now:yyyy-MM-dd}.log");
                
                // Simple append. In high throughput, a proper stream writer held open would be better,
                // but for this app, File.AppendAllText is safer for locking/sharing.
                // We'll retry a few times if locked.
                
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(50);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // ignore logging errors
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _writeTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
        _entryQueue.Dispose();
        _cts.Dispose();
        _loggers.Clear();
    }
}

public class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{GetLogLevelString(logLevel)}] [{_categoryName}] {message}";
        
        if (exception != null)
        {
            logEntry += Environment.NewLine + exception;
        }

        _provider.Enqueue(logEntry);
    }

    private static string GetLogLevelString(LogLevel logLevel)
    {
        return logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "INF"
        };
    }
}
