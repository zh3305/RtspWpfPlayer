using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

public class CustomConsoleFormatter : ConsoleFormatter
{
    private readonly IDisposable? _optionsReloadToken;
    private ConsoleFormatterOptions _formatterOptions;

    public CustomConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> options)
        : base("CustomFormatter")
    {
        _optionsReloadToken = options.OnChange(ReloadLoggerOptions);
        _formatterOptions = options.CurrentValue;
    }

    private void ReloadLoggerOptions(ConsoleFormatterOptions options) => _formatterOptions = options;

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        string? message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message == null)
        {
            return;
        }

        var logLevel = logEntry.LogLevel;
        var category = logEntry.Category;
        var eventId = logEntry.EventId.Id;
        var exception = logEntry.Exception;
        var timestamp = DateTime.Now.ToString(_formatterOptions.TimestampFormat);

        // 根据日志级别设置不同的颜色
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = logLevel switch
        {
            LogLevel.Trace => ConsoleColor.Gray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Information => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.DarkRed,
            _ => ConsoleColor.White
        };

        textWriter.Write($"[{timestamp}] [{logLevel}] [{category}] [{eventId}] {message}");

        if (exception != null)
        {
            textWriter.WriteLine();
            textWriter.WriteLine($"Exception: {exception.GetType().FullName}");
            textWriter.WriteLine($"Message: {exception.Message}");
            textWriter.WriteLine($"StackTrace: {exception.StackTrace}");
        }

        textWriter.WriteLine();
        Console.ForegroundColor = originalColor;
    }
} 