using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed record SpreadRule
{
    public decimal MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public decimal MaxSpreadPct { get; init; }      // (ask-bid)/mid
    public decimal MaxAbsSpread { get; init; }      // ask-bid
}

public sealed record PreFilterOptions
{
    public decimal MinPrice { get; init; } = 1m;
    public decimal MaxPrice { get; init; } = 500m;

    public long MinDayVolume { get; init; } = 500_000;
    public decimal MinDayDollarVolume { get; init; } = 10_000_000m;

    public bool EnableDayRvolFilter { get; init; } = false;
    public int DayRvolLookbackDays { get; init; } = 20;
    public decimal MinDayRvol { get; init; } = 1.20m;

    public decimal? MinDayPercentChange { get; init; } = null;

    public bool RejectIfMissingData { get; init; } = true;

    public IReadOnlyList<SpreadRule> SpreadRules { get; init; } = Array.Empty<SpreadRule>();
}

public sealed record PreFilterDecision(string Symbol, bool Accepted, IReadOnlyList<string> Reasons);

public interface IUniversePreFilter
{
    Task<(IReadOnlyList<string> Accepted, IReadOnlyList<PreFilterDecision> Decisions)> FilterAsync(
        IReadOnlyList<string> symbols,
        PreFilterOptions opt,
        CancellationToken ct = default);
}

public sealed class UniversePreFilter : IUniversePreFilter
{
    private readonly IMarketDataClient _marketData;

    public UniversePreFilter(IMarketDataClient marketData) => _marketData = marketData;

    public async Task<(IReadOnlyList<string> Accepted, IReadOnlyList<PreFilterDecision> Decisions)> FilterAsync(
        IReadOnlyList<string> symbols,
        PreFilterOptions opt,
        CancellationToken ct = default)
    {
        if (symbols.Count == 0)
            return (Array.Empty<string>(), Array.Empty<PreFilterDecision>());

        var snaps = await _marketData.GetSnapshotsAsync(symbols, ct);

        // Optional RVOL lookback
        Dictionary<string, decimal>? rvolBySymbol = null;
        if (opt.EnableDayRvolFilter)
        {
            rvolBySymbol = await ComputeDayRvolAsync(symbols, opt.DayRvolLookbackDays, ct);
        }

        var accepted = new List<string>();
        var decisions = new List<PreFilterDecision>();

        foreach (var sym in symbols)
        {
            var reasons = new List<string>();

            if (!snaps.TryGetValue(sym, out var snap))
            {
                if (opt.RejectIfMissingData)
                    reasons.Add("Missing snapshot");
                decisions.Add(new PreFilterDecision(sym, reasons.Count == 0, reasons));
                if (reasons.Count == 0) accepted.Add(sym);
                continue;
            }

            var price = snap.LatestTradePrice ?? snap.DailyBar?.Close;
            if (price is null)
            {
                if (opt.RejectIfMissingData)
                    reasons.Add("Missing price");
                decisions.Add(new PreFilterDecision(sym, reasons.Count == 0, reasons));
                if (reasons.Count == 0) accepted.Add(sym);
                continue;
            }

            if (price < opt.MinPrice || price > opt.MaxPrice)
                reasons.Add($"Price {price} outside [{opt.MinPrice},{opt.MaxPrice}]");

            var daily = snap.DailyBar;
            if (daily is null)
            {
                if (opt.RejectIfMissingData)
                    reasons.Add("Missing daily bar");
            }
            else
            {
                if (daily.Volume < opt.MinDayVolume)
                    reasons.Add($"Day volume {daily.Volume} < {opt.MinDayVolume}");

                var dollarVol = daily.Close * daily.Volume;
                if (dollarVol < opt.MinDayDollarVolume)
                    reasons.Add($"Day $ volume {dollarVol:N0} < {opt.MinDayDollarVolume:N0}");

                if (opt.MinDayPercentChange.HasValue && daily.Open > 0)
                {
                    var pct = (daily.Close - daily.Open) / daily.Open;
                    if (pct < opt.MinDayPercentChange.Value)
                        reasons.Add($"Day %chg {pct:P2} < {opt.MinDayPercentChange.Value:P2}");
                }
            }

            // Spread filter
            if (opt.SpreadRules.Count > 0 && snap.LatestQuote is not null)
            {
                var bid = snap.LatestQuote.BidPrice;
                var ask = snap.LatestQuote.AskPrice;
                if (bid > 0 && ask > 0 && ask >= bid)
                {
                    var absSpread = ask - bid;
                    var mid = (ask + bid) / 2m;
                    var pctSpread = mid > 0 ? absSpread / mid : 1m;

                    var rule = FindRule(opt.SpreadRules, price.Value);
                    if (rule is not null)
                    {
                        if (absSpread > rule.MaxAbsSpread)
                            reasons.Add($"Abs spread {absSpread} > {rule.MaxAbsSpread}");
                        if (pctSpread > rule.MaxSpreadPct)
                            reasons.Add($"Pct spread {pctSpread:P2} > {rule.MaxSpreadPct:P2}");
                    }
                }
                else if (opt.RejectIfMissingData)
                {
                    reasons.Add("Missing/invalid quote for spread");
                }
            }

            if (opt.EnableDayRvolFilter && rvolBySymbol is not null)
            {
                if (rvolBySymbol.TryGetValue(sym, out var rvol))
                {
                    if (rvol < opt.MinDayRvol)
                        reasons.Add($"RVOL {rvol:F2} < {opt.MinDayRvol:F2}");
                }
                else if (opt.RejectIfMissingData)
                {
                    reasons.Add("Missing RVOL");
                }
            }

            var ok = reasons.Count == 0;
            decisions.Add(new PreFilterDecision(sym, ok, reasons));
            if (ok) accepted.Add(sym);
        }

        return (accepted, decisions);
    }

    private static SpreadRule? FindRule(IReadOnlyList<SpreadRule> rules, decimal price)
    {
        foreach (var r in rules)
        {
            if (price >= r.MinPrice && (r.MaxPrice is null || price < r.MaxPrice.Value))
                return r;
        }
        return null;
    }

    private async Task<Dictionary<string, decimal>> ComputeDayRvolAsync(
        IReadOnlyList<string> symbols,
        int lookbackDays,
        CancellationToken ct)
    {
        var to = DateTime.UtcNow;
        var from = to.AddDays(-(lookbackDays + 5));

        var bars = await _marketData.GetHistoricalDailyBarsAsync(symbols, from, to, ct);

        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var grp in bars.GroupBy(b => b.Symbol))
        {
            var ordered = grp.OrderBy(x => x.TimeUtc).ToList();
            if (ordered.Count < 2) continue;

            // last complete day is the last bar; depending on time of day this might be today-in-progress.
            // We'll take the last bar as "current" and avg of previous N bars.
            var current = ordered[^1].Volume;
            var prev = ordered.Take(Math.Max(0, ordered.Count - 1)).TakeLast(lookbackDays).Select(x => (decimal)x.Volume).ToList();

            if (prev.Count == 0) continue;

            var avg = prev.Average();
            if (avg <= 0) continue;

            result[grp.Key] = current / avg;
        }

        return result;
    }
}
