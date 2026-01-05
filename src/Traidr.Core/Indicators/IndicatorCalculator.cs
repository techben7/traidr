using Traidr.Core.MarketData;

namespace Traidr.Core.Indicators;

public sealed record IndicatorSeries(
    IReadOnlyList<DateTime> TimeUtc,
    IReadOnlyList<decimal?> Ema20,
    IReadOnlyList<decimal?> Ema200,
    IReadOnlyList<decimal?> Vwap,
    IReadOnlyList<decimal?> Atr14);

public sealed record IndicatorCalculatorOptions
{
    public int EmaFastPeriod { get; init; } = 20;
    public int EmaSlowPeriod { get; init; } = 200;
    public int AtrPeriod { get; init; } = 14;
    public bool ComputeVwap { get; init; } = true;
}

/// <summary>
/// Option 1: compute indicators in-process from the bars you already fetched.
/// </summary>
public sealed class IndicatorCalculator
{
    private readonly IndicatorCalculatorOptions _opt;

    public IndicatorCalculator(IndicatorCalculatorOptions opt) => _opt = opt;

    public IndicatorSeries Compute(IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0)
            return new IndicatorSeries(Array.Empty<DateTime>(), Array.Empty<decimal?>(), Array.Empty<decimal?>(), Array.Empty<decimal?>(), Array.Empty<decimal?>());

        var ordered = bars.OrderBy(b => b.TimeUtc).ToList();
        var times = ordered.Select(x => x.TimeUtc).ToArray();

        var closes = ordered.Select(x => x.Close).ToArray();
        var highs = ordered.Select(x => x.High).ToArray();
        var lows = ordered.Select(x => x.Low).ToArray();
        var vols = ordered.Select(x => (decimal)x.Volume).ToArray();

        var emaFast = ComputeEma(closes, _opt.EmaFastPeriod);
        var emaSlow = ComputeEma(closes, _opt.EmaSlowPeriod);

        var atr = ComputeAtr(highs, lows, closes, _opt.AtrPeriod);

        var vwap = _opt.ComputeVwap ? ComputeVwap(highs, lows, closes, vols) : Enumerable.Repeat<decimal?>(null, bars.Count).ToArray();

        return new IndicatorSeries(times, emaFast, emaSlow, vwap, atr);
    }

    public static decimal?[] ComputeEma(decimal[] values, int period)
    {
        var result = new decimal?[values.Length];
        if (values.Length == 0 || period <= 0) return result;

        var k = 2m / (period + 1m);

        // seed with SMA of first period
        if (values.Length < period) return result;

        decimal sma = 0m;
        for (int i = 0; i < period; i++) sma += values[i];
        sma /= period;

        result[period - 1] = sma;

        decimal prev = sma;
        for (int i = period; i < values.Length; i++)
        {
            var ema = (values[i] * k) + (prev * (1m - k));
            result[i] = ema;
            prev = ema;
        }

        return result;
    }

    public static decimal?[] ComputeAtr(decimal[] highs, decimal[] lows, decimal[] closes, int period)
    {
        var n = highs.Length;
        var atr = new decimal?[n];
        if (n == 0 || period <= 0 || lows.Length != n || closes.Length != n) return atr;

        var tr = new decimal[n];
        tr[0] = highs[0] - lows[0];

        for (int i = 1; i < n; i++)
        {
            var a = highs[i] - lows[i];
            var b = Math.Abs(highs[i] - closes[i - 1]);
            var c = Math.Abs(lows[i] - closes[i - 1]);
            tr[i] = Math.Max(a, Math.Max(b, c));
        }

        if (n < period) return atr;

        decimal sma = 0m;
        for (int i = 0; i < period; i++) sma += tr[i];
        sma /= period;

        atr[period - 1] = sma;

        // Wilder smoothing
        var prev = sma;
        for (int i = period; i < n; i++)
        {
            var next = ((prev * (period - 1)) + tr[i]) / period;
            atr[i] = next;
            prev = next;
        }

        return atr;
    }

    public static decimal?[] ComputeVwap(decimal[] highs, decimal[] lows, decimal[] closes, decimal[] volumes)
    {
        var n = closes.Length;
        var vwap = new decimal?[n];
        decimal cumPV = 0m;
        decimal cumV = 0m;

        for (int i = 0; i < n; i++)
        {
            var typical = (highs[i] + lows[i] + closes[i]) / 3m;
            var v = volumes[i];
            cumPV += typical * v;
            cumV += v;
            vwap[i] = cumV > 0m ? (cumPV / cumV) : null;
        }

        return vwap;
    }
}
