using Microsoft.Extensions.Logging;

namespace Traidr.Cli;

public sealed class ColorConsoleLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopeProvider;

    public ILogger CreateLogger(string categoryName)
        => new ColorConsoleLogger(categoryName, () => _scopeProvider);

    public void Dispose()
    {
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }
}

internal sealed class ColorConsoleLogger : ILogger
{
    internal const string GreenMarker = "[GREEN]";
    internal const string PinkMarker = "[PINK]";
    internal const string YellowMarker = "[YELLOW]";
    private readonly string _categoryName;
    private readonly Func<IExternalScopeProvider?> _scopeProviderAccessor;

    public ColorConsoleLogger(string categoryName, Func<IExternalScopeProvider?> scopeProviderAccessor)
    {
        _categoryName = categoryName;
        _scopeProviderAccessor = scopeProviderAccessor;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        var scopeProvider = _scopeProviderAccessor();
        if (scopeProvider == null)
        {
            return NullScope.Instance;
        }

        return scopeProvider.Push(state!);
    }

    public bool IsEnabled(LogLevel logLevel)
        => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception == null)
        {
            return;
        }

        var originalColor = Console.ForegroundColor;
        var color = GetColor(logLevel);
        if (message.StartsWith(GreenMarker, StringComparison.Ordinal))
        {
            color = ConsoleColor.Green;
            message = message.Substring(GreenMarker.Length).TrimStart();
        }
        else if (message.StartsWith(PinkMarker, StringComparison.Ordinal))
        {
            color = ConsoleColor.Magenta;
            message = message.Substring(PinkMarker.Length).TrimStart();
        }
        else if (message.StartsWith(YellowMarker, StringComparison.Ordinal))
        {
            color = ConsoleColor.DarkYellow;
            message = message.Substring(YellowMarker.Length).TrimStart();
        }
        if (color.HasValue)
        {
            Console.ForegroundColor = color.Value;
        }

        var logLevelTxt = GetLogLevelTxt(logLevel);

        var writer = Console.Out;
        var timestamp_Full = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var timestamp_TimeOnly = DateTimeOffset.Now.ToString("HH:mm:ss.fff");
        writer.Write($"[{logLevelTxt}] ");
        writer.Write($"[{timestamp_TimeOnly}] ");
        writer.Write(message);

        if (exception != null)
        {
            writer.Write(' ');
            writer.Write(exception);
        }

        var scopeProvider = _scopeProviderAccessor();
        if (scopeProvider != null)
        {
            scopeProvider.ForEachScope((scope, textWriter) =>
            {
                textWriter.Write(" => ");
                textWriter.Write(scope);
            }, writer);
        }

        writer.WriteLine();
        if (color.HasValue)
        {
            Console.ForegroundColor = originalColor;
        }
    }

    private static ConsoleColor? GetColor(LogLevel level)
        => level switch
        {
            LogLevel.Trace => ConsoleColor.Gray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Information => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.DarkYellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.Red,
            _ => null
        };

    private const string LogLevelTxt_Trace = "TRC";
    private const string LogLevelTxt_Debug = "DBG";
    private const string LogLevelTxt_Information = "INFO";
    private const string LogLevelTxt_Warning = "WARN";
    private const string LogLevelTxt_Error = "ERRR";
    private const string LogLevelTxt_Critical = "CRITICAL";

    private static string GetLogLevelTxt(LogLevel level)
        => level switch
        {
            LogLevel.Trace => LogLevelTxt_Trace,
            LogLevel.Debug => LogLevelTxt_Debug,
            LogLevel.Information => LogLevelTxt_Information,
            LogLevel.Warning => LogLevelTxt_Warning,
            LogLevel.Error => LogLevelTxt_Error,
            LogLevel.Critical => LogLevelTxt_Critical,
            _ => null
        };

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
