using Traidr.Core.Scanning;

namespace Traidr.Cli;

public static class TradeDirectionParser
{
    public static TradeDirectionMode Parse(string? value, TradeDirectionMode fallback = TradeDirectionMode.Both)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return Enum.TryParse<TradeDirectionMode>(value, ignoreCase: true, out var mode)
            ? mode
            : fallback;
    }
}
