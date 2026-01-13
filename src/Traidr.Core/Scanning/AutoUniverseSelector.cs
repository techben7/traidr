using Microsoft.Extensions.Logging;
using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed class AutoUniverseSelector
{
    private readonly AutoUniverseOptions _opt;
    private readonly FinancialModelingPrepClient _fmp;
    private readonly IMarketDataClient _marketData;
    private readonly ILogger _log;

    public AutoUniverseSelector(
        AutoUniverseOptions opt,
        FinancialModelingPrepClient fmp,
        IMarketDataClient marketData,
        ILogger<AutoUniverseSelector> log)
    {
        _opt = opt;
        _fmp = fmp;
        _marketData = marketData;
        _log = log;
    }

    public async Task<IReadOnlyList<string>> GetSymbolsAsync(CancellationToken ct = default)
    {
        if (_opt.TopCount <= 0)
            return Array.Empty<string>();

        var screen = await _fmp.ScreenStocksAsync(_opt, ct);
        if (screen.Count == 0)
        {
            _log.LogWarning("AutoUniverse: screener returned no results.");
            return Array.Empty<string>();
        }

        var rvolCandidates = screen
            .Where(s => s.AvgVolume > 0 && s.Volume > 0)
            .Select(s => new Candidate(
                s.Symbol,
                s.Price,
                s.MarketCap,
                s.Volume,
                s.AvgVolume,
                (decimal)s.Volume / s.AvgVolume))
            .Where(c => c.Price >= _opt.MinPrice && c.Price <= _opt.MaxPrice)
            .Where(c => c.MarketCap >= _opt.MinMarketCap && c.MarketCap <= _opt.MaxMarketCap)
            .Where(c => c.Rvol >= _opt.MinDayRvol)
            .OrderByDescending(c => c.Rvol)
            .ToList();

        if (rvolCandidates.Count == 0)
            return Array.Empty<string>();

        var candidateSymbols = rvolCandidates
            .Select(c => c.Symbol)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(_opt.MaxCandidateSymbols)
            .ToList();

        if (candidateSymbols.Count == 0)
            return Array.Empty<string>();

        var to = DateTime.UtcNow;
        var from = to.AddDays(-10);
        var bars = await _marketData.GetHistoricalDailyBarsAsync(candidateSymbols, from, to, ct);
        var gapBySymbol = ComputeGapPct(bars);

        var final = rvolCandidates
            .Where(c => gapBySymbol.TryGetValue(c.Symbol, out var gap) && Math.Abs(gap) >= _opt.MinGapPct)
            .OrderByDescending(c => c.Rvol)
            .Take(_opt.TopCount)
            .Select(c => c.Symbol)
            .ToList();

        _log.LogInformation("AutoUniverse: selected {Count} symbols (target {Target}).", final.Count, _opt.TopCount);
        return final;
    }

    private static Dictionary<string, decimal> ComputeGapPct(IReadOnlyList<Bar> bars)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var grp in bars.GroupBy(b => b.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = grp.OrderBy(b => b.TimeUtc).ToList();
            if (ordered.Count < 2)
                continue;

            var last = ordered[^1];
            var prev = ordered[^2];
            if (prev.Close <= 0m)
                continue;

            var gap = (last.Open - prev.Close) / prev.Close;
            result[grp.Key] = gap;
        }

        return result;
    }

    private sealed record Candidate(
        string Symbol,
        decimal Price,
        decimal MarketCap,
        long Volume,
        long AvgVolume,
        decimal Rvol);
}
