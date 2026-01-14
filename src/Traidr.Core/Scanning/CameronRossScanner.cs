using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Traidr.Core.Indicators;
using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed class CameronRossScanner : ISetupScanner
{
    private readonly IndicatorCalculator _indicators;
    private readonly CameronRossScannerOptions _opt;
    private readonly RetestOptions _retest;
    private readonly IMarketMetadataProvider _meta;
    private readonly IMarketDataClient? _marketData;
    private readonly ILogger _log;
    private readonly TimeZoneInfo _marketTz;

    public CameronRossScanner(
        IndicatorCalculator indicators,
        CameronRossScannerOptions opt,
        RetestOptions retest,
        IMarketMetadataProvider meta,
        IMarketDataClient? marketData = null,
        ILogger? log = null,
        string marketTimeZoneId = "America/New_York")
    {
        _indicators = indicators;
        _opt = opt;
        _retest = retest;
        _meta = meta;
        _marketData = marketData;
        _log = log ?? NullLogger.Instance;
        _marketTz = TimeZoneInfo.FindSystemTimeZoneById(marketTimeZoneId);
    }

    public IReadOnlyList<SetupCandidate> Scan(IReadOnlyList<Bar> barsForManySymbols)
    {
        var groups = barsForManySymbols
            .GroupBy(b => b.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(b => b.TimeUtc).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return Scan(groups);
    }

    public IReadOnlyList<SetupCandidate> Scan(IReadOnlyDictionary<string, List<Bar>> barsBySymbolOrdered)
    {
        var candidates = new List<SetupCandidate>();

        foreach (var (symbol, bars) in barsBySymbolOrdered)
        {
            var minBarCount = 7; // 10
            if (bars.Count < minBarCount)
            {
                LogSkip(symbol, $"not enough bars {bars.Count} < {minBarCount}");
                continue;
            }

            var last = bars[^1];
            var lastLocal = TimeZoneInfo.ConvertTimeFromUtc(last.TimeUtc, _marketTz);
            if (lastLocal.TimeOfDay < _opt.StartTimeEt || lastLocal.TimeOfDay > _opt.EndTimeEt)
            {
                LogSkip(symbol, "outside trading window");
                continue;
            }

            if (last.Close < _opt.MinPrice || last.Close > _opt.MaxPrice)
            {
                LogSkip(symbol, $"price out of range ({last.Close:F2})");
                continue;
            }

            if (_opt.RequireNews)
            {
                if (!_meta.TryHasNews(symbol, out var hasNews) || !hasNews)
                {
                    LogSkip(symbol, "news required but not available");
                    continue;
                }
            }

            if (_opt.RequireLowFloat)
            {
                if (!_meta.TryGetFloatShares(symbol, out var floatShares))
                {
                    LogSkip(symbol, "float required but not available");
                    continue;
                }

                if (floatShares > _opt.MaxFloatShares)
                {
                    LogSkip(symbol, $"float too high ({floatShares:N0} > {_opt.MaxFloatShares:N0})");
                    continue;
                }
            }

            if (_opt.RequireShortInterest)
            {
                if (!_meta.TryGetShortInterestPct(symbol, out var shortPct))
                {
                    LogSkip(symbol, "short interest required but not available");
                    continue;
                }

                if (shortPct < _opt.MinShortInterestPct)
                {
                    LogSkip(symbol, $"short interest too low ({shortPct:P2} < {_opt.MinShortInterestPct:P2})");
                    continue;
                }
            }

            if (!TryGetDailyStats(bars, out var stats))
            {
                LogSkip(symbol, "not enough daily history");
                continue;
            }

            if (stats.GapPct < _opt.MinGapPct)
            {
                LogSkip(symbol, $"gap too small ({stats.GapPct:P2} < {_opt.MinGapPct:P2})");
                continue;
            }

            if (stats.DayGainPct < _opt.MinDayGainPct)
            {
                LogSkip(symbol, $"day gain too small ({stats.DayGainPct:P2} < {_opt.MinDayGainPct:P2})");
                continue;
            }

            if (_opt.RequireRvol)
            {
                if (!stats.Rvol.HasValue)
                {
                    LogSkip(symbol, "relative volume unavailable");
                    continue;
                }

                if (stats.Rvol.Value < _opt.MinRvol)
                {
                    LogSkip(symbol, $"rvol too low ({stats.Rvol.Value:F2} < {_opt.MinRvol:F2})");
                    continue;
                }
            }

            var direction = BreakoutDirection.Long;
            if (_opt.AllowShorts && stats.DayGainPct < 0)
                direction = BreakoutDirection.Short;

            if (direction == BreakoutDirection.Short && !_opt.AllowShorts)
            {
                LogSkip(symbol, "shorts disabled");
                continue;
            }

            if (!TryGetPullbackSetup(bars, direction, out var entry, out var stop, out var pullbackPct))
            {
                LogSkip(symbol, "no valid pullback breakout");
                continue;
            }

            if (_opt.RequireRoundBreak && !IsNearRoundLevel(entry))
            {
                LogSkip(symbol, "not near round/half-dollar level");
                continue;
            }

            if (_retest.IncludeRetest && !RetestHelper.HasRetestConfirmation(bars, entry, direction, _retest))
            {
                LogSkip(symbol, "retest not confirmed");
                continue;
            }

            var series = _indicators.Compute(bars);
            var idx = series.TimeUtc.Count - 1;

            candidates.Add(new SetupCandidate(
                Symbol: symbol,
                Direction: direction,
                EntryPrice: entry,
                StopPrice: stop,
                TakeProfitPrice: null,
                ConsolidationHigh: entry,
                ConsolidationLow: stop,
                RangePct: pullbackPct,
                AtrPct: series.Atr14[idx].HasValue && last.Close > 0 ? series.Atr14[idx]!.Value / last.Close : 0m,
                BodyToMedianBody: 0m,
                VolumeToAvgVolume: stats.Rvol ?? 0m,
                Ema20: series.Ema20[idx],
                Ema200: series.Ema200[idx],
                Vwap: series.Vwap[idx],
                Atr14: series.Atr14[idx],
                ElephantBarTimeUtc: last.TimeUtc
            ));
        }

        return candidates;
    }

    private bool TryGetPullbackSetup(
        List<Bar> bars,
        BreakoutDirection direction,
        out decimal entry,
        out decimal stop,
        out decimal pullbackPct)
    {
        entry = 0m;
        stop = 0m;
        pullbackPct = 0m;

        if (!_opt.RequireMicroPullback)
        {
            entry = bars[^1].Close;
            stop = ComputeStop(entry, direction, null);
            return stop > 0m;
        }

        if (bars.Count < _opt.PullbackBars + 1)
            return false;

        var trigger = bars[^1];
        var pullback = bars.Skip(bars.Count - 1 - _opt.PullbackBars).Take(_opt.PullbackBars).ToList();
        var pullbackHigh = pullback.Max(b => b.High);
        var pullbackLow = pullback.Min(b => b.Low);

        if (pullbackHigh <= 0m)
            return false;

        pullbackPct = (pullbackHigh - pullbackLow) / pullbackHigh;
        if (pullbackPct > _opt.MaxPullbackPct)
            return false;

        if (direction == BreakoutDirection.Long && trigger.Close <= pullbackHigh)
            return false;
        if (direction == BreakoutDirection.Short && trigger.Close >= pullbackLow)
            return false;

        entry = direction == BreakoutDirection.Long ? pullbackHigh : pullbackLow;
        stop = ComputeStop(entry, direction, pullbackLow);
        return stop > 0m;
    }

    private decimal ComputeStop(decimal entry, BreakoutDirection direction, decimal? pullbackLow)
    {
        if (_opt.UseFixedStopCents)
        {
            return direction == BreakoutDirection.Long
                ? entry - _opt.StopCents
                : entry + _opt.StopCents;
        }

        if (!pullbackLow.HasValue)
            return 0m;

        return direction == BreakoutDirection.Long
            ? pullbackLow.Value * (1m - _opt.StopBufferPct)
            : pullbackLow.Value * (1m + _opt.StopBufferPct);
    }

    private bool IsNearRoundLevel(decimal price)
    {
        if (_opt.RoundIncrement <= 0m)
            return false;

        var nearest = Math.Round(price / _opt.RoundIncrement) * _opt.RoundIncrement;
        var dist = Math.Abs(price - nearest);
        return dist <= _opt.RoundBreakMaxDistance;
    }

    private bool TryGetDailyStats(List<Bar> bars, out DailyStats stats)
    {
        var byDay = new List<DayBar>();

        DayBar? current = null;
        foreach (var bar in bars)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(bar.TimeUtc, _marketTz).Date;
            if (current == null || current.Date != local)
            {
                if (current != null) byDay.Add(current);
                current = new DayBar(local, bar.Open, bar.Close, bar.Low, bar.High, bar.Volume);
                continue;
            }

            current = current with
            {
                Close = bar.Close,
                Low = Math.Min(current.Low, bar.Low),
                High = Math.Max(current.High, bar.High),
                Volume = current.Volume + bar.Volume
            };
        }

        if (current != null) byDay.Add(current);
        if (byDay.Count >= 2)
        {
            var today = byDay[^1];
            var prev = byDay[^2];

            var gapPct = prev.Close > 0m ? (today.Open - prev.Close) / prev.Close : 0m;
            var dayGainPct = prev.Close > 0m ? (today.Close - prev.Close) / prev.Close : 0m;

            decimal? rvol = null;
            if (_opt.RvolLookbackDays > 0)
            {
                var recent = byDay.Take(byDay.Count - 1).TakeLast(Math.Min(_opt.RvolLookbackDays, byDay.Count - 1)).ToList();
                if (recent.Count > 0)
                {
                    var avg = recent.Average(d => (decimal)d.Volume);
                    if (avg > 0m)
                        rvol = today.Volume / avg;
                }
            }

            stats = new DailyStats(gapPct, dayGainPct, rvol);
            return true;
        }

        if (_opt.EnableDailyHistoryFallback && _marketData is not null)
            return TryGetDailyStatsFromFallback(bars, out stats);

        stats = default;
        return false;
    }

    private bool TryGetDailyStatsFromFallback(List<Bar> bars, out DailyStats stats)
    {
        stats = default;

        var symbol = bars[^1].Symbol;
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddDays(-Math.Max(5, _opt.DailyHistoryLookbackDays + 5));

        IReadOnlyList<Bar> dailyBars;
        try
        {
            dailyBars = _marketData!.GetHistoricalDailyBarsAsync(new[] { symbol }, fromUtc, toUtc)
                .GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }

        var daily = dailyBars.OrderBy(b => b.TimeUtc).ToList();
        if (daily.Count == 0)
            return false;

        var prev = daily.Count >= 2 ? daily[^2] : daily[^1];
        if (prev.Close <= 0m)
            return false;

        var todayBars = bars
            .Where(b => TimeZoneInfo.ConvertTimeFromUtc(b.TimeUtc, _marketTz).Date ==
                        TimeZoneInfo.ConvertTimeFromUtc(bars[^1].TimeUtc, _marketTz).Date)
            .ToList();
        if (todayBars.Count == 0)
            return false;

        var todayOpen = todayBars.First().Open;
        var todayLast = todayBars.Last().Close;
        var todayVol = todayBars.Sum(b => (decimal)b.Volume);

        var gapPct = (todayOpen - prev.Close) / prev.Close;
        var dayGainPct = (todayLast - prev.Close) / prev.Close;

        decimal? rvol = null;
        if (_opt.RvolLookbackDays > 0)
        {
            var recent = daily.Take(Math.Max(0, daily.Count - 1)).TakeLast(Math.Min(_opt.RvolLookbackDays, daily.Count - 1)).ToList();
            if (recent.Count > 0)
            {
                var avg = recent.Average(d => (decimal)d.Volume);
                if (avg > 0m)
                    rvol = todayVol / avg;
            }
        }

        stats = new DailyStats(gapPct, dayGainPct, rvol);
        return true;
    }

    private void LogSkip(string symbol, string reason)
    {
        _log.LogWarning("CameronRoss scan skip {Symbol}: {Reason}", symbol, reason);
    }

    private sealed record DayBar(DateTime Date, decimal Open, decimal Close, decimal Low, decimal High, long Volume);
    private sealed record DailyStats(decimal GapPct, decimal DayGainPct, decimal? Rvol);
}
