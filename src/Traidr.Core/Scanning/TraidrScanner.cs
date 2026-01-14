using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Traidr.Core.Indicators;
using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed class TraidrScanner : ISetupScanner
{
    private readonly IndicatorCalculator _indicators;
    private readonly TraidrScannerOptions _opt;
    private readonly RetestOptions _retest;
    private readonly ILogger _log;

    public TraidrScanner(IndicatorCalculator indicators, TraidrScannerOptions opt, RetestOptions retest, ILogger? log = null)
    {
        _indicators = indicators;
        _opt = opt;
        _retest = retest;
        _log = log ?? NullLogger.Instance;
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
            if (bars.Count < _opt.ConsolidationLookbackBars + 2)
            {
                LogSkip(symbol, $"001 not enough bars ({bars.Count} < {_opt.ConsolidationLookbackBars + 2})");
                continue;
            }

            var confirm = bars[^1];
            var elephant = confirm;
            var consolidation = new List<Bar>();
            decimal conHigh = 0m;
            decimal conLow = 0m;
            decimal rangePct = 0m;
            decimal bodyToMedian = 0m;
            decimal volToAvg = 0m;

            BreakoutDirection? dir = null;
            decimal entry = 0m;
            decimal stop = 0m;

            if (_retest.IncludeRetest)
            {
                var confirmIndex = bars.Count - 1;
                var retestStart = Math.Max(1, confirmIndex - _retest.RetestMaxBars);
                var found = false;

                for (var retestIndex = confirmIndex - 1; retestIndex >= retestStart && !found; retestIndex--)
                {
                    var retest = bars[retestIndex];
                    var breakoutStart = Math.Max(1, retestIndex - _retest.RetestMaxBars);

                    for (var breakoutIndex = retestIndex - 1; breakoutIndex >= breakoutStart; breakoutIndex--)
                    {
                        var conStart = breakoutIndex - _opt.ConsolidationLookbackBars;
                        if (conStart < 0)
                            continue;

                        consolidation = bars.Skip(conStart).Take(_opt.ConsolidationLookbackBars).ToList();
                        if (consolidation.Count < _opt.ConsolidationLookbackBars)
                            continue;

                        conHigh = consolidation.Max(b => b.High);
                        conLow = consolidation.Min(b => b.Low);

                        var mid = (conHigh + conLow) / 2m;
                        if (mid <= 0m)
                            continue;

                        rangePct = (conHigh - conLow) / mid;
                        if (rangePct > _opt.MaxConsolidationRangePct)
                            continue;

                        elephant = bars[breakoutIndex];

                        var bodies = consolidation.Select(b => Math.Abs(b.Close - b.Open)).OrderBy(x => x).ToList();
                        var medianBody = bodies.Count == 0 ? 0m : bodies[bodies.Count / 2];
                        if (medianBody <= 0m)
                            continue;

                        var elephantBody = Math.Abs(elephant.Close - elephant.Open);
                        bodyToMedian = elephantBody / medianBody;

                        var avgVol = consolidation.Select(b => (decimal)b.Volume).DefaultIfEmpty(0m).Average();
                        volToAvg = avgVol > 0m ? ((decimal)elephant.Volume / avgVol) : 0m;

                        if (bodyToMedian < _opt.MinBodyToMedianBody || volToAvg < _opt.MinVolumeToAvgVolume)
                            continue;

                        var bufferUp0 = conHigh * _opt.BreakoutBufferPct;
                        var bufferDown0 = conLow * _opt.BreakoutBufferPct;

                        if (elephant.Close > conHigh + bufferUp0)
                        {
                            if (retest.Low > conHigh * (1m + _retest.RetestTolerancePct))
                                continue;
                            if (confirm.Close < conHigh * (1m + _retest.RetestConfirmMinClosePct))
                                continue;

                            dir = BreakoutDirection.Long;
                            stop = conLow * (1m - _opt.StopBufferPct);
                        }
                        else if (elephant.Close < conLow - bufferDown0)
                        {
                            if (retest.High < conLow * (1m - _retest.RetestTolerancePct))
                                continue;
                            if (confirm.Close > conLow * (1m - _retest.RetestConfirmMinClosePct))
                                continue;

                            dir = BreakoutDirection.Short;
                            stop = conHigh * (1m + _opt.StopBufferPct);
                        }
                        else
                        {
                            continue;
                        }

                        if (stop <= 0m)
                            continue;

                        entry = confirm.Close;
                        found = true;
                        break;
                    }
                }

                if (!found || dir is null)
                {
                    LogSkip(symbol, "012 no retest confirmation");
                    continue;
                }
            }
            else
            {
                // Use last closed bar as elephant bar
                elephant = bars[^1];
                consolidation = bars.Skip(bars.Count - 1 - _opt.ConsolidationLookbackBars)
                    .Take(_opt.ConsolidationLookbackBars)
                    .ToList();

                if (consolidation.Count < _opt.ConsolidationLookbackBars)
                {
                    LogSkip(symbol, "002 consolidation window incomplete");
                    continue;
                }

                conHigh = consolidation.Max(b => b.High);
                conLow = consolidation.Min(b => b.Low);

                var mid = (conHigh + conLow) / 2m;
                if (mid <= 0m)
                {
                    LogSkip(symbol, "003 invalid consolidation mid");
                    continue;
                }

                rangePct = (conHigh - conLow) / mid;
                if (rangePct > _opt.MaxConsolidationRangePct)
                {
                    LogSkip(symbol, $"004 rangePct too wide ({rangePct:P2} > {_opt.MaxConsolidationRangePct:P2})");
                    continue;
                }

                var bodies = consolidation.Select(b => Math.Abs(b.Close - b.Open)).OrderBy(x => x).ToList();
                var medianBody = bodies.Count == 0 ? 0m : bodies[bodies.Count / 2];
                if (medianBody <= 0m)
                {
                    LogSkip(symbol, "005 median body <= 0");
                    continue;
                }

                var elephantBody = Math.Abs(elephant.Close - elephant.Open);
                bodyToMedian = elephantBody / medianBody;

                var avgVol = consolidation.Select(b => (decimal)b.Volume).DefaultIfEmpty(0m).Average();
                volToAvg = avgVol > 0m ? ((decimal)elephant.Volume / avgVol) : 0m;

                if (bodyToMedian < _opt.MinBodyToMedianBody)
                {
                    LogSkip(symbol, $"006 body/median too small ({bodyToMedian:F2} < {_opt.MinBodyToMedianBody:F2})");
                    continue;
                }
                if (volToAvg < _opt.MinVolumeToAvgVolume)
                {
                    LogSkip(symbol, $"007 vol/avg too small ({volToAvg:F2} < {_opt.MinVolumeToAvgVolume:F2})");
                    continue;
                }

                // Breakout detection: close beyond consolidation boundary
                var bufferUp1 = conHigh * _opt.BreakoutBufferPct;
                var bufferDown1 = conLow * _opt.BreakoutBufferPct;

                entry = elephant.Close;

                if (elephant.Close > conHigh + bufferUp1)
                {
                    dir = BreakoutDirection.Long;
                    stop = conLow * (1m - _opt.StopBufferPct);
                }
                else if (elephant.Close < conLow - bufferDown1)
                {
                    dir = BreakoutDirection.Short;
                    stop = conHigh * (1m + _opt.StopBufferPct);
                }

                if (dir is null)
                {
                    LogSkip(symbol, "012 no breakout beyond consolidation range");
                    continue;
                }
                if (stop <= 0m)
                {
                    LogSkip(symbol, "013 invalid stop price");
                    continue;
                }
            }

            // Indicators
            var series = _indicators.Compute(bars);
            var idx = series.TimeUtc.Count - 1;

            var ema20 = series.Ema20[idx];
            var ema200 = series.Ema200[idx];
            var vwap = series.Vwap[idx];
            var atr14 = series.Atr14[idx];

            var priceForFilters = _retest.IncludeRetest ? confirm.Close : elephant.Close;
            var atrPct = 0m;
            if (atr14.HasValue && priceForFilters > 0)
                atrPct = atr14.Value / priceForFilters;

            if (_opt.RequireAtrAvailable && !atr14.HasValue)
            {
                LogSkip(symbol, "008 ATR required but unavailable");
                continue;
            }
            if (atr14.HasValue)
            {
                if (atrPct < _opt.MinAtrPct || atrPct > _opt.MaxAtrPct)
                {
                    LogSkip(symbol, $"009 ATR pct out of range ({atrPct:P2} not in {_opt.MinAtrPct:P2}..{_opt.MaxAtrPct:P2})");
                    continue;
                }
            }

            if (_opt.RequireNearEma20 && ema20.HasValue)
            {
                var dist = Math.Abs(priceForFilters - ema20.Value) / priceForFilters;
                if (dist > _opt.MaxDistanceFromEma20Pct)
                {
                    LogSkip(symbol, $"010 EMA20 distance too large ({dist:P2} > {_opt.MaxDistanceFromEma20Pct:P2})");
                    continue;
                }
            }

            if (_opt.RequireEma20NearEma200 && ema20.HasValue && ema200.HasValue && ema200.Value > 0)
            {
                var dist = Math.Abs(ema20.Value - ema200.Value) / ema200.Value;
                if (dist > _opt.MaxEmaDistancePct)
                {
                    LogSkip(symbol, $"011 EMA20/EMA200 distance too large ({dist:P2} > {_opt.MaxEmaDistancePct:P2})");
                    continue;
                }
            }

            // Breakout detection: close beyond consolidation boundary
            var bufferUp2 = conHigh * _opt.BreakoutBufferPct;
            var bufferDown2 = conLow * _opt.BreakoutBufferPct;

            // BreakoutDirection? dir = null;
            // decimal entry = elephant.Close;
            // decimal stop = 0m;

            if (elephant.Close > conHigh + bufferUp2)
            {
                dir = BreakoutDirection.Long;
                stop = conLow * (1m - _opt.StopBufferPct);
            }
            else if (elephant.Close < conLow - bufferDown2)
            {
                dir = BreakoutDirection.Short;
                stop = conHigh * (1m + _opt.StopBufferPct);
            }

            if (dir is null)
            {
                LogSkip(symbol, "012 no breakout beyond consolidation range");
                continue;
            }
            if (stop <= 0m)
            {
                LogSkip(symbol, "013 invalid stop price");
                continue;
            }

            if (_opt.RequireTrendEma20OverEma200)
            {
                if (!ema20.HasValue || !ema200.HasValue)
                {
                    LogSkip(symbol, "014 trend EMA filter requires EMA20/EMA200");
                    continue;
                }
                if (dir == BreakoutDirection.Long && ema20.Value <= ema200.Value)
                {
                    LogSkip(symbol, "015 trend filter failed (EMA20 <= EMA200) for long");
                    continue;
                }
                if (dir == BreakoutDirection.Short && ema20.Value >= ema200.Value)
                {
                    LogSkip(symbol, "016 trend filter failed (EMA20 >= EMA200) for short");
                    continue;
                }
            }

            if (_opt.RequirePriceAboveEma20)
            {
                if (!ema20.HasValue)
                {
                    LogSkip(symbol, "017 price/EMA20 filter requires EMA20");
                    continue;
                }
                if (dir == BreakoutDirection.Long && priceForFilters < ema20.Value)
                {
                    LogSkip(symbol, "018 price below EMA20 for long");
                    continue;
                }
                if (dir == BreakoutDirection.Short && priceForFilters > ema20.Value)
                {
                    LogSkip(symbol, "019 price above EMA20 for short");
                    continue;
                }
            }

            if (_opt.RequirePriceAboveEma200)
            {
                if (!ema200.HasValue)
                {
                    LogSkip(symbol, "020 price/EMA200 filter requires EMA200");
                    continue;
                }
                if (dir == BreakoutDirection.Long && priceForFilters < ema200.Value)
                {
                    LogSkip(symbol, "021 price below EMA200 for long");
                    continue;
                }
                if (dir == BreakoutDirection.Short && priceForFilters > ema200.Value)
                {
                    LogSkip(symbol, "022 price above EMA200 for short");
                    continue;
                }
            }

            if (_opt.RequirePriceAboveVwap)
            {
                if (!vwap.HasValue)
                {
                    LogSkip(symbol, "023 price/VWAP filter requires VWAP");
                    continue;
                }
                if (dir == BreakoutDirection.Long && priceForFilters < vwap.Value)
                {
                    LogSkip(symbol, "024 price below VWAP for long");
                    continue;
                }
                if (dir == BreakoutDirection.Short && priceForFilters > vwap.Value)
                {
                    LogSkip(symbol, "025 price above VWAP for short");
                    continue;
                }
            }

            var range = elephant.High - elephant.Low;
            if (range > 0m)
            {
                var closePos = (elephant.Close - elephant.Low) / range;
                if (dir == BreakoutDirection.Long && closePos < _opt.MinCloseInRangeForLong)
                {
                    LogSkip(symbol, $"026 close position too low for long ({closePos:P0} < {_opt.MinCloseInRangeForLong:P0})");
                    continue;
                }
                if (dir == BreakoutDirection.Short && closePos > _opt.MaxCloseInRangeForShort)
                {
                    LogSkip(symbol, $"027 close position too high for short ({closePos:P0} > {_opt.MaxCloseInRangeForShort:P0})");
                    continue;
                }
            }

            var signalTime = _retest.IncludeRetest ? confirm.TimeUtc : elephant.TimeUtc;
            candidates.Add(new SetupCandidate(
                Symbol: symbol,
                Direction: dir.Value,
                EntryPrice: entry,
                StopPrice: stop,
                TakeProfitPrice: null,
                ConsolidationHigh: conHigh,
                ConsolidationLow: conLow,
                RangePct: rangePct,
                AtrPct: atrPct,
                BodyToMedianBody: bodyToMedian,
                VolumeToAvgVolume: volToAvg,
                Ema20: ema20,
                Ema200: ema200,
                Vwap: vwap,
                Atr14: atr14,
                ElephantBarTimeUtc: signalTime
            ));
        }

        return candidates;
    }

    private void LogSkip(string symbol, string reason)
    {
        _log.LogWarning("Scan skip {Symbol}: {Reason}", symbol, reason);
    }
}
