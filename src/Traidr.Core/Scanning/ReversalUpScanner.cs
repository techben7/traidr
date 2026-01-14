using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Traidr.Core.Indicators;
using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed class ReversalUpScanner : ISetupScanner
{
    private readonly IndicatorCalculator _indicators;
    private readonly ReversalUpScannerOptions _opt;
    private readonly RetestOptions _retest;
    private readonly ILogger _log;

    public ReversalUpScanner(IndicatorCalculator indicators, ReversalUpScannerOptions opt, RetestOptions retest, ILogger? log = null)
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
            var minBars = _opt.SidewaysLookbackBars + (_opt.PivotLookbackBars * 2) + 2;
            if (bars.Count < minBars)
            {
                LogSkip(symbol, $"R01 not enough bars ({bars.Count} < {minBars})");
                continue;
            }

            var window = bars.Skip(bars.Count - _opt.SidewaysLookbackBars).Take(_opt.SidewaysLookbackBars).ToList();
            if (window.Count < _opt.SidewaysLookbackBars)
            {
                LogSkip(symbol, "R02 sideways window incomplete");
                continue;
            }

            var rangeHigh = window.Max(b => b.High);
            var rangeLow = window.Min(b => b.Low);
            var mid = (rangeHigh + rangeLow) / 2m;
            if (mid <= 0m)
            {
                LogSkip(symbol, "R03 invalid range mid");
                continue;
            }

            var rangePct = (rangeHigh - rangeLow) / mid;
            if (rangePct > _opt.MaxSidewaysRangePct)
            {
                LogSkip(symbol, $"R04 range too wide ({rangePct:P2} > {_opt.MaxSidewaysRangePct:P2})");
                continue;
            }

            var swings = GetSwings(window, _opt.PivotLookbackBars);
            if (swings.Count < _opt.MinSwingCount)
            {
                LogSkip(symbol, $"R05 not enough swings ({swings.Count} < {_opt.MinSwingCount})");
                continue;
            }

            var lastLow = swings.LastOrDefault(s => s.Type == SwingType.Low);
            if (lastLow is null)
            {
                LogSkip(symbol, "R06 no swing low");
                continue;
            }

            var lastHigh = swings.LastOrDefault(s => s.Type == SwingType.High && s.Index < lastLow.Index);
            if (lastHigh is null)
            {
                LogSkip(symbol, "R07 no swing high before last low");
                continue;
            }

            var priorLow = swings.LastOrDefault(s => s.Type == SwingType.Low && s.Index < lastHigh.Index);
            if (priorLow is null)
            {
                LogSkip(symbol, "R08 no prior swing low for bull run");
                continue;
            }

            var barsSinceLow = (window.Count - 1) - lastLow.Index;
            if (barsSinceLow > _opt.MaxBarsAfterSwingLow)
            {
                LogSkip(symbol, $"R09 last swing low too old ({barsSinceLow} bars)");
                continue;
            }

            var bullRunDist = lastHigh.Price - priorLow.Price;
            if (bullRunDist <= 0m)
            {
                LogSkip(symbol, "R10 invalid bull run distance");
                continue;
            }

            var bullRunPct = bullRunDist / priorLow.Price;
            if (bullRunPct < _opt.MinBullRunPct)
            {
                LogSkip(symbol, $"R11 bull run too small ({bullRunPct:P2} < {_opt.MinBullRunPct:P2})");
                continue;
            }

            var confirm = window[^1];
            var signal = confirm;
            if (_retest.IncludeRetest)
            {
                var confirmIndex = window.Count - 1;
                var retestStart = Math.Max(1, confirmIndex - _retest.RetestMaxBars);
                var found = false;

                for (var retestIndex = confirmIndex - 1; retestIndex >= retestStart && !found; retestIndex--)
                {
                    var retest = window[retestIndex];
                    var breakoutStart = Math.Max(1, retestIndex - _retest.RetestMaxBars);

                    for (var breakoutIndex = retestIndex - 1; breakoutIndex >= breakoutStart; breakoutIndex--)
                    {
                        var breakout = window[breakoutIndex];
                        var breakoutLevel = breakout.Close;
                        if (breakoutLevel <= 0m)
                            continue;

                        if (retest.Low > breakoutLevel * (1m + _retest.RetestTolerancePct))
                            continue;
                        if (confirm.Close < breakoutLevel * (1m + _retest.RetestConfirmMinClosePct))
                            continue;

                        signal = breakout;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    LogSkip(symbol, "R12 retest not confirmed");
                    continue;
                }
            }

            if (signal.Close <= signal.Open)
            {
                LogSkip(symbol, "R13 signal bar not green");
                continue;
            }

            var bodies = window.Select(b => Math.Abs(b.Close - b.Open)).OrderBy(x => x).ToList();
            var medianBody = bodies.Count == 0 ? 0m : bodies[bodies.Count / 2];
            if (medianBody <= 0m)
            {
                LogSkip(symbol, "R14 median body <= 0");
                continue;
            }

            var body = Math.Abs(signal.Close - signal.Open);
            var bodyToMedian = body / medianBody;
            if (bodyToMedian < _opt.MinGreenBodyToMedian)
            {
                LogSkip(symbol, $"R15 body/median too small ({bodyToMedian:F2} < {_opt.MinGreenBodyToMedian:F2})");
                continue;
            }

            var range = signal.High - signal.Low;
            if (range <= 0m)
            {
                LogSkip(symbol, "R16 invalid signal range");
                continue;
            }

            var lowerWick = Math.Min(signal.Open, signal.Close) - signal.Low;
            var lowerWickPct = lowerWick / range;
            if (lowerWickPct < _opt.MinLowerWickPct)
            {
                LogSkip(symbol, $"R17 lower wick too small ({lowerWickPct:P2} < {_opt.MinLowerWickPct:P2})");
                continue;
            }

            var baseEntry = _retest.IncludeRetest ? confirm.Close : signal.Close;
            var entry = baseEntry * (1m + _opt.EntryBufferPct);
            var stop = lastLow.Price * (1m - _opt.StopBufferPct);
            if (entry <= 0m || stop <= 0m)
            {
                LogSkip(symbol, "R18 invalid entry/stop");
                continue;
            }

            var takeProfit = lastLow.Price + (_opt.TakeProfitBullRunPct * bullRunDist);
            if (takeProfit <= entry)
            {
                LogSkip(symbol, "R19 take profit below entry");
                continue;
            }

            var avgVol = window.Select(b => (decimal)b.Volume).DefaultIfEmpty(0m).Average();
            var volToAvg = avgVol > 0m ? ((decimal)signal.Volume / avgVol) : 0m;

            var series = _indicators.Compute(bars);
            var idx = series.TimeUtc.Count - 1;

            var atrPct = 0m;
            var atr14 = series.Atr14[idx];
            if (atr14.HasValue && baseEntry > 0m)
                atrPct = atr14.Value / baseEntry;

            var signalTime = _retest.IncludeRetest ? confirm.TimeUtc : signal.TimeUtc;
            candidates.Add(new SetupCandidate(
                Symbol: symbol,
                Direction: BreakoutDirection.Long,
                EntryPrice: entry,
                StopPrice: stop,
                TakeProfitPrice: takeProfit,
                ConsolidationHigh: rangeHigh,
                ConsolidationLow: rangeLow,
                RangePct: rangePct,
                AtrPct: atrPct,
                BodyToMedianBody: bodyToMedian,
                VolumeToAvgVolume: volToAvg,
                Ema20: series.Ema20[idx],
                Ema200: series.Ema200[idx],
                Vwap: series.Vwap[idx],
                Atr14: atr14,
                ElephantBarTimeUtc: signalTime
            ));
        }

        return candidates;
    }

    private enum SwingType { High, Low }

    private sealed record SwingPoint(int Index, decimal Price, SwingType Type);

    private static List<SwingPoint> GetSwings(IReadOnlyList<Bar> bars, int pivot)
    {
        var swings = new List<SwingPoint>();
        if (bars.Count == 0 || pivot < 1)
            return swings;

        for (var i = pivot; i < bars.Count - pivot; i++)
        {
            var high = bars[i].High;
            var low = bars[i].Low;
            var isHigh = true;
            var isLow = true;

            for (var j = i - pivot; j <= i + pivot; j++)
            {
                if (bars[j].High > high) isHigh = false;
                if (bars[j].Low < low) isLow = false;
                if (!isHigh && !isLow) break;
            }

            if (isHigh && !isLow)
                swings.Add(new SwingPoint(i, high, SwingType.High));
            else if (isLow && !isHigh)
                swings.Add(new SwingPoint(i, low, SwingType.Low));
        }

        return swings;
    }

    private void LogSkip(string symbol, string reason)
        => _log.LogDebug("ReversalUp skip {Symbol}: {Reason}", symbol, reason);
}
