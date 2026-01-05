using Traidr.Core.Indicators;
using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed class TraidrScanner
{
    private readonly IndicatorCalculator _indicators;
    private readonly TraidrScannerOptions _opt;

    public TraidrScanner(IndicatorCalculator indicators, TraidrScannerOptions opt)
    {
        _indicators = indicators;
        _opt = opt;
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
                continue;

            // Use last closed bar as elephant bar
            var elephant = bars[^1];
            var consolidation = bars.Skip(bars.Count - 1 - _opt.ConsolidationLookbackBars)
                .Take(_opt.ConsolidationLookbackBars)
                .ToList();

            if (consolidation.Count < _opt.ConsolidationLookbackBars)
                continue;

            var conHigh = consolidation.Max(b => b.High);
            var conLow = consolidation.Min(b => b.Low);

            var mid = (conHigh + conLow) / 2m;
            if (mid <= 0m) continue;

            var rangePct = (conHigh - conLow) / mid;
            if (rangePct > _opt.MaxConsolidationRangePct)
                continue;

            var bodies = consolidation.Select(b => Math.Abs(b.Close - b.Open)).OrderBy(x => x).ToList();
            var medianBody = bodies.Count == 0 ? 0m : bodies[bodies.Count / 2];
            if (medianBody <= 0m) continue;

            var elephantBody = Math.Abs(elephant.Close - elephant.Open);
            var bodyToMedian = elephantBody / medianBody;

            var avgVol = consolidation.Select(b => (decimal)b.Volume).DefaultIfEmpty(0m).Average();
            var volToAvg = avgVol > 0m ? ((decimal)elephant.Volume / avgVol) : 0m;

            if (bodyToMedian < _opt.MinBodyToMedianBody) continue;
            if (volToAvg < _opt.MinVolumeToAvgVolume) continue;

            // Indicators
            var series = _indicators.Compute(bars);
            var idx = series.TimeUtc.Count - 1;

            var ema20 = series.Ema20[idx];
            var ema200 = series.Ema200[idx];
            var vwap = series.Vwap[idx];
            var atr14 = series.Atr14[idx];

            var atrPct = 0m;
            if (atr14.HasValue && elephant.Close > 0)
                atrPct = atr14.Value / elephant.Close;

            if (_opt.RequireAtrAvailable && !atr14.HasValue) continue;
            if (atr14.HasValue)
            {
                if (atrPct < _opt.MinAtrPct || atrPct > _opt.MaxAtrPct) continue;
            }

            if (_opt.RequireNearEma20 && ema20.HasValue)
            {
                var dist = Math.Abs(elephant.Close - ema20.Value) / elephant.Close;
                if (dist > _opt.MaxDistanceFromEma20Pct) continue;
            }

            if (_opt.RequireEma20NearEma200 && ema20.HasValue && ema200.HasValue && ema200.Value > 0)
            {
                var dist = Math.Abs(ema20.Value - ema200.Value) / ema200.Value;
                if (dist > _opt.MaxEmaDistancePct) continue;
            }

            // Breakout detection: close beyond consolidation boundary
            var bufferUp = conHigh * _opt.BreakoutBufferPct;
            var bufferDown = conLow * _opt.BreakoutBufferPct;

            BreakoutDirection? dir = null;
            decimal entry = elephant.Close;
            decimal stop = 0m;

            if (elephant.Close > conHigh + bufferUp)
            {
                dir = BreakoutDirection.Long;
                stop = conLow * (1m - _opt.StopBufferPct);
            }
            else if (elephant.Close < conLow - bufferDown)
            {
                dir = BreakoutDirection.Short;
                stop = conHigh * (1m + _opt.StopBufferPct);
            }

            if (dir is null) continue;
            if (stop <= 0m) continue;

            candidates.Add(new SetupCandidate(
                Symbol: symbol,
                Direction: dir.Value,
                EntryPrice: entry,
                StopPrice: stop,
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
                ElephantBarTimeUtc: elephant.TimeUtc
            ));
        }

        return candidates;
    }
}
