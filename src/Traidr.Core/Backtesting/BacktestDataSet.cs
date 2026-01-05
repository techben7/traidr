using Traidr.Core.MarketData;

namespace Traidr.Core.Backtesting;

/// <summary>
/// Pre-loaded bar data for fast repeated backtests (e.g., optimization runs).
/// </summary>
public sealed record BacktestDataSet(
    IReadOnlyDictionary<string, IReadOnlyList<Bar>> BarsBySymbol,
    IReadOnlyList<DateTime> TimesUtc,
    TimeZoneInfo MarketTimeZone);

public static class BacktestDataLoader
{
    public static async Task<BacktestDataSet> LoadAsync(
        IMarketDataClient marketData,
        IReadOnlyList<string> symbols,
        DateOnly fromDateEt,
        DateOnly toDateEt,
        string timeframe,
        string marketTimeZoneId = "America/New_York",
        CancellationToken ct = default)
    {
        var marketTz = TimeZoneInfo.FindSystemTimeZoneById(marketTimeZoneId);

        var fromEt = fromDateEt.ToDateTime(TimeOnly.MinValue);
        var toEtExclusive = toDateEt.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromEt, marketTz);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(toEtExclusive, marketTz);

        var bars = await marketData.GetHistoricalBarsAsync(symbols, fromUtc, toUtc, timeframe, ct);

        var barsBySymbol = bars
            .GroupBy(b => b.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Bar>)g.OrderBy(b => b.TimeUtc).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var times = bars.Select(b => b.TimeUtc).Distinct().OrderBy(t => t).ToList();

        return new BacktestDataSet(barsBySymbol, times, marketTz);
    }
}
