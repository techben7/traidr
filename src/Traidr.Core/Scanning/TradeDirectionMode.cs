namespace Traidr.Core.Scanning;

public enum TradeDirectionMode
{
    Both,
    Long,
    Short
}

public static class TradeDirectionModeExtensions
{
    public static bool Allows(this TradeDirectionMode mode, BreakoutDirection direction)
    {
        return mode switch
        {
            TradeDirectionMode.Both => true,
            TradeDirectionMode.Long => direction == BreakoutDirection.Long,
            TradeDirectionMode.Short => direction == BreakoutDirection.Short,
            _ => true
        };
    }

    public static IReadOnlyList<SetupCandidate> Filter(this TradeDirectionMode mode, IReadOnlyList<SetupCandidate> candidates)
    {
        if (candidates.Count == 0 || mode == TradeDirectionMode.Both)
            return candidates;

        var target = mode == TradeDirectionMode.Long ? BreakoutDirection.Long : BreakoutDirection.Short;
        var filtered = new List<SetupCandidate>();
        foreach (var c in candidates)
        {
            if (c.Direction == target)
                filtered.Add(c);
        }
        return filtered;
    }
}
