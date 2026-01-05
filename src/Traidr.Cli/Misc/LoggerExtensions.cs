using Microsoft.Extensions.Logging;

namespace Traidr.Cli;

public static class LoggerExtensions
{
    public static void LogGreen(this ILogger logger, string message)
    {
        logger.LogInformation($"{ColorConsoleLogger.GreenMarker} {message}");
    }

    public static void LogGreen(this ILogger logger, string message, params object[] args)
    {
        logger.LogInformation($"{ColorConsoleLogger.GreenMarker} {message}", args);
    }

    public static void LogGreen(this ILogger logger, Exception exception, string message, params object[] args)
    {
        logger.LogInformation(exception, $"{ColorConsoleLogger.GreenMarker} {message}", args);
    }


    public static void LogPink(this ILogger logger, string message)
    {
        logger.LogInformation($"{ColorConsoleLogger.PinkMarker} {message}");
    }

    public static void LogPink(this ILogger logger, string message, params object[] args)
    {
        logger.LogInformation($"{ColorConsoleLogger.PinkMarker} {message}", args);
    }

    public static void LogPink(this ILogger logger, Exception exception, string message, params object[] args)
    {
        logger.LogInformation(exception, $"{ColorConsoleLogger.PinkMarker} {message}", args);
    }

    public static void LogYellow(this ILogger logger, string message)
    {
        logger.LogInformation($"{ColorConsoleLogger.YellowMarker} {message}");
    }

    public static void LogYellow(this ILogger logger, string message, params object[] args)
    {
        logger.LogInformation($"{ColorConsoleLogger.YellowMarker} {message}", args);
    }

    public static void LogYellow(this ILogger logger, Exception exception, string message, params object[] args)
    {
        logger.LogInformation(exception, $"{ColorConsoleLogger.YellowMarker} {message}", args);
    }
}
