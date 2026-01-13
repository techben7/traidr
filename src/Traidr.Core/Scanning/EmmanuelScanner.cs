using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Traidr.Core.Indicators;
using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed class EmmanuelScanner : ISetupScanner
{
    private readonly IndicatorCalculator _indicators;
    private readonly EmmanuelScannerOptions _opt;
    private readonly IMarketMetadataProvider _meta;
    private readonly ILogger _log;
    private readonly TimeZoneInfo _marketTz;

    public EmmanuelScanner(
        IndicatorCalculator indicators,
        EmmanuelScannerOptions opt,
        IMarketMetadataProvider meta,
        ILogger? log = null,
        string marketTimeZoneId = "America/New_York")
    {
        _indicators = indicators;
        _opt = opt;
        _meta = meta;
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
            var minBars = Math.Max(_opt.PoleLookbackBars + _opt.FlagBars + 2, 20);
            if (bars.Count < minBars)
            {
                LogSkip(symbol, $"not enough bars [{bars.Count} < {minBars}]");
                continue;
            }

            var last = bars[^1];
            if (last.Close < _opt.MinPrice || last.Close > _opt.MaxPrice)
            {
                LogSkip(symbol, $"price out of range ({last.Close:F2})");
                continue;
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

            if (!TryGetSessionStats(bars, out var stats))
            {
                LogSkip(symbol, "not enough daily data");
                continue;
            }

            if (stats.GapPct < _opt.MinGapPct)
            {
                LogSkip(symbol, $"gap too small ({stats.GapPct:P2} < {_opt.MinGapPct:P2})");
                continue;
            }

            if (stats.PremarketVolume < _opt.MinPremarketVolume)
            {
                LogSkip(symbol, $"premarket volume too low ({stats.PremarketVolume:N0} < {_opt.MinPremarketVolume:N0})");
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

            var series = _indicators.Compute(bars);
            var idx = series.TimeUtc.Count - 1;

            var ema9 = series.Ema9[idx];
            var ema20 = series.Ema20[idx];
            var vwap = series.Vwap[idx];
            var atr14 = series.Atr14[idx];

            if (_opt.RequirePriceAboveVwap)
            {
                if (!vwap.HasValue)
                {
                    LogSkip(symbol, "VWAP required but unavailable");
                    continue;
                }

                if (last.Close < vwap.Value)
                {
                    LogSkip(symbol, "price below VWAP");
                    continue;
                }
            }

            if (_opt.RequireVwapSlopeUp)
            {
                if (!HasVwapSlopeUp(series, _opt.VwapSlopeBars))
                {
                    LogSkip(symbol, "VWAP slope not positive");
                    continue;
                }
            }

            if (_opt.RequireEma9AboveEma20 && ema9.HasValue && ema20.HasValue && ema9.Value <= ema20.Value)
            {
                LogSkip(symbol, "EMA9 not above EMA20");
                continue;
            }

            if (_opt.RequireEma9Hook)
            {
                if (!ema9.HasValue)
                {
                    LogSkip(symbol, "EMA9 required but unavailable");
                    continue;
                }

                var touches = last.Low <= ema9.Value && last.Close >= ema9.Value;
                if (!touches)
                {
                    LogSkip(symbol, "EMA9 hook not present");
                    continue;
                }
            }

            if (_opt.RequireTightSpread)
            {
                if (!HasTightSpreadProxy(last, atr14, out var proxySpread))
                {
                    LogSkip(symbol, "spread proxy unavailable");
                    continue;
                }

                if (proxySpread > _opt.MaxSpreadCents)
                {
                    LogSkip(symbol, $"spread too wide ({proxySpread:F2} > {_opt.MaxSpreadCents:F2})");
                    continue;
                }
            }

            decimal triggerHigh = 0;
            decimal flagLow = 0;
            decimal flagRetrace = 0;
            if (_opt.RequireBullFlag && !TryGetBullFlagSetup(bars, out triggerHigh, out flagLow, out flagRetrace))
            {
                LogSkip(symbol, "bull flag criteria not met");
                continue;
            }

            var entry = triggerHigh + _opt.EntryBufferCents;
            var stop = flagLow - _opt.StopBufferCents;
            if (stop <= 0m)
            {
                LogSkip(symbol, "invalid stop price");
                continue;
            }

            candidates.Add(new SetupCandidate(
                Symbol: symbol,
                Direction: BreakoutDirection.Long,
                EntryPrice: entry,
                StopPrice: stop,
                TakeProfitPrice: null,
                ConsolidationHigh: triggerHigh,
                ConsolidationLow: flagLow,
                RangePct: flagRetrace,
                AtrPct: atr14.HasValue && last.Close > 0m ? atr14.Value / last.Close : 0m,
                BodyToMedianBody: 0m,
                VolumeToAvgVolume: stats.Rvol ?? 0m,
                Ema20: ema20,
                Ema200: series.Ema200[idx],
                Vwap: vwap,
                Atr14: atr14,
                ElephantBarTimeUtc: last.TimeUtc
            ));
        }

        return candidates;
    }

    private bool TryGetBullFlagSetup(List<Bar> bars, out decimal triggerHigh, out decimal flagLow, out decimal flagRetrace)
    {
        triggerHigh = 0m;
        flagLow = 0m;
        flagRetrace = 0m;

        var flagBars = Math.Max(1, _opt.FlagBars);
        var poleBars = Math.Max(2, _opt.PoleLookbackBars);
        if (bars.Count < poleBars + flagBars + 2)
            return false;

        var trigger = bars[^1];
        var flag = bars.Skip(bars.Count - 1 - flagBars).Take(flagBars).ToList();
        var pole = bars.Skip(bars.Count - 1 - flagBars - poleBars).Take(poleBars).ToList();

        var poleLow = pole.Min(b => b.Low);
        var poleHigh = pole.Max(b => b.High);
        if (poleLow <= 0m) return false;

        var polePct = (poleHigh - poleLow) / poleLow;
        if (polePct < _opt.MinPolePct) return false;

        var flagHigh = flag.Max(b => b.High);
        flagLow = flag.Min(b => b.Low);
        if (flagHigh <= 0m) return false;

        flagRetrace = (flagHigh - flagLow) / flagHigh;
        if (flagRetrace > _opt.MaxFlagRetracePct) return false;

        if (_opt.RequireLowerFlagVolume)
        {
            var poleVol = pole.Average(b => (decimal)b.Volume);
            var flagVol = flag.Average(b => (decimal)b.Volume);
            if (flagVol >= poleVol) return false;
        }

        triggerHigh = flagHigh;
        if (trigger.Close <= triggerHigh)
            return false;

        return true;
    }

    private bool HasVwapSlopeUp(IndicatorSeries series, int slopeBars)
    {
        if (slopeBars <= 0)
            return false;

        var indexFromEnd = slopeBars + 1;
        if (series.Vwap.Count < indexFromEnd)
            return false;

        var end = series.Vwap[^1];
        var start = series.Vwap[^indexFromEnd];
        if (!end.HasValue || !start.HasValue)
            return false;

        return end.Value > start.Value;
    }

    private bool HasTightSpreadProxy(Bar last, decimal? atr14, out decimal spreadProxy)
    {
        spreadProxy = 0m;
        if (!_opt.UseBarRangeAsSpreadProxy)
            return false;

        var range = last.High - last.Low;
        if (range <= 0m)
            return false;

        if (atr14.HasValue && atr14.Value > 0m)
        {
            var pct = range / atr14.Value;
            if (pct > _opt.MaxBarRangePctOfAtr)
                return false;
        }

        spreadProxy = range;
        return true;
    }

    private bool TryGetSessionStats(List<Bar> bars, out SessionStats stats)
    {
        var byDay = new List<DayBar>();
        DayBar? current = null;
        long premarketVolume = 0;

        foreach (var bar in bars)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(bar.TimeUtc, _marketTz);
            var date = local.Date;

            if (current == null || current.Date != date)
            {
                if (current != null) byDay.Add(current);
                current = new DayBar(date, bar.Open, bar.Close, bar.Low, bar.High, bar.Volume);
            }
            else
            {
                current = current with
                {
                    Close = bar.Close,
                    Low = Math.Min(current.Low, bar.Low),
                    High = Math.Max(current.High, bar.High),
                    Volume = current.Volume + bar.Volume
                };
            }
        }

        if (current != null) byDay.Add(current);
        if (byDay.Count < 2)
        {
            _log.LogError($"byDay.Count:: [{byDay.Count}]");
            stats = default;
            return false;
        }

        var today = byDay[^1];
        var prev = byDay[^2];

        var gapPct = prev.Close > 0m ? (today.Open - prev.Close) / prev.Close : 0m;

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

        var todayDate = today.Date;
        foreach (var bar in bars)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(bar.TimeUtc, _marketTz);
            if (local.Date != todayDate) continue;
            if (local.TimeOfDay >= _opt.PremarketStartEt && local.TimeOfDay < _opt.PremarketEndEt)
                premarketVolume += bar.Volume;
        }

        stats = new SessionStats(gapPct, rvol, premarketVolume);
        return true;
    }

    private void LogSkip(string symbol, string reason)
    {
        _log.LogWarning("Emmanuel scan skip {Symbol}: {Reason}", symbol, reason);
    }

    private sealed record DayBar(DateTime Date, decimal Open, decimal Close, decimal Low, decimal High, long Volume);
    private sealed record SessionStats(decimal GapPct, decimal? Rvol, long PremarketVolume);
}
