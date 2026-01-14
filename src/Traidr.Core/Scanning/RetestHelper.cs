using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public static class RetestHelper
{
    public static bool HasRetestConfirmation(
        IReadOnlyList<Bar> bars,
        decimal breakoutLevel,
        BreakoutDirection direction,
        RetestOptions opt)
    {
        if (!opt.IncludeRetest)
            return true;

        if (bars.Count < 3)
            return false;

        var confirmIndex = bars.Count - 1;
        var confirm = bars[confirmIndex];

        if (direction == BreakoutDirection.Long)
        {
            if (confirm.Close < breakoutLevel * (1m + opt.RetestConfirmMinClosePct))
                return false;
        }
        else
        {
            if (confirm.Close > breakoutLevel * (1m - opt.RetestConfirmMinClosePct))
                return false;
        }

        var start = Math.Max(0, confirmIndex - opt.RetestMaxBars);
        for (var retestIndex = confirmIndex - 1; retestIndex >= start; retestIndex--)
        {
            var retest = bars[retestIndex];
            if (direction == BreakoutDirection.Long)
            {
                if (retest.Low > breakoutLevel * (1m + opt.RetestTolerancePct))
                    continue;
            }
            else
            {
                if (retest.High < breakoutLevel * (1m - opt.RetestTolerancePct))
                    continue;
            }

            for (var i = retestIndex - 1; i >= start; i--)
            {
                var b = bars[i];
                if (direction == BreakoutDirection.Long && b.Close > breakoutLevel)
                    return true;
                if (direction == BreakoutDirection.Short && b.Close < breakoutLevel)
                    return true;
            }
        }

        return false;
    }
}
