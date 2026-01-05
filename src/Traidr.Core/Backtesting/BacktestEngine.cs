using Traidr.Core.MarketData;
using Traidr.Core.Scanning;
using Traidr.Core.Trading;

namespace Traidr.Core.Backtesting;

public sealed class BacktestEngine
{
    private readonly IMarketDataClient _marketData;
    private readonly TraidrScanner _scanner;
    private readonly IRiskManager _risk;
    private readonly TimeZoneInfo _marketTz;

    public BacktestEngine(IMarketDataClient marketData, TraidrScanner scanner, IRiskManager risk, string marketTimeZoneId = "America/New_York")
    {
        _marketData = marketData;
        _scanner = scanner;
        _risk = risk;
        _marketTz = TimeZoneInfo.FindSystemTimeZoneById(marketTimeZoneId);
    }

    public async Task<BacktestResult> RunAsync(BacktestOptions opt, CancellationToken ct = default)
    {
        if (opt.Symbols.Count == 0)
            return new BacktestResult(Array.Empty<BacktestTrade>(), new BacktestSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m));

        // Convert ET dates to UTC bounds.
        var fromEt = opt.FromDateEt.ToDateTime(TimeOnly.MinValue);
        var toEtExclusive = opt.ToDateEt.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromEt, _marketTz);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(toEtExclusive, _marketTz);

        var bars = await _marketData.GetHistoricalBarsAsync(opt.Symbols, fromUtc, toUtc, opt.Timeframe, ct);

        var barsBySymbol = bars
            .GroupBy(b => b.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.TimeUtc).ToList(), StringComparer.OrdinalIgnoreCase);

        // Build global time index (5m close times) based on union of all symbols.
        var times = bars.Select(b => b.TimeUtc).Distinct().OrderBy(t => t).ToList();
        if (times.Count == 0)
            return new BacktestResult(Array.Empty<BacktestTrade>(), new BacktestSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m));

        // Sliding windows per symbol.
        var windows = new Dictionary<string, List<Bar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in opt.Symbols)
            windows[sym] = new List<Bar>(512);

        var openPositions = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase);
        var trades = new List<BacktestTrade>();

        // Iterate time forward.
        for (var ti = 0; ti < times.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var tUtc = times[ti];

            // Update windows
            foreach (var (sym, list) in barsBySymbol)
            {
                // append bars for this exact time (usually 1)
                // NOTE: Alpaca bars are unique per (symbol,timeframe,time)
                var idx = list.BinarySearchByTime(tUtc);
                if (idx >= 0)
                {
                    windows[sym].Add(list[idx]);
                    // Keep a cap to avoid unbounded growth (indicators need history; 300 is plenty for EMA200 warmup)
                    if (windows[sym].Count > 400)
                        windows[sym].RemoveRange(0, windows[sym].Count - 400);
                }
            }

            // Process exits on this bar for any open positions
            foreach (var sym in openPositions.Keys.ToList())
            {
                if (!barsBySymbol.TryGetValue(sym, out var series)) continue;
                var idx = series.BinarySearchByTime(tUtc);
                if (idx < 0) continue;
                var bar = series[idx];

                var pos = openPositions[sym];
                var exit = TryEvaluateExit(bar, pos, opt);
                if (exit is not null)
                {
                    openPositions.Remove(sym);
                    trades.Add(exit);
                }
            }

            // Flatten at/after FlattenTime ET per day
            var local = TimeZoneInfo.ConvertTime(tUtc, _marketTz);
            if (local.TimeOfDay >= opt.FlattenTimeEt.ToTimeSpan())
            {
                foreach (var sym in openPositions.Keys.ToList())
                {
                    if (!barsBySymbol.TryGetValue(sym, out var series)) continue;
                    var idx = series.BinarySearchByTime(tUtc);
                    if (idx < 0) continue;
                    var bar = series[idx];

                    var pos = openPositions[sym];
                    openPositions.Remove(sym);
                    trades.Add(ForceExitEod(bar, pos, opt));
                }
            }

            // No new entries if we already have any position in that symbol
            // Build scan input (all symbol windows that have at least some bars)
            var candidates = _scanner.Scan(windows);
            if (candidates.Count == 0)
                continue;

            foreach (var c in candidates)
            {
                if (openPositions.ContainsKey(c.Symbol))
                    continue;

                // Risk decision at the signal time
                var now = new DateTimeOffset(tUtc, TimeSpan.Zero);
                var riskDecision = _risk.Evaluate(c, takeProfitPrice: null, now);
                if (riskDecision.Decision == RiskDecisionType.Block)
                    continue;

                var qty = riskDecision.Quantity!.Value;
                var tpPrice = ComputeTakeProfit(c.EntryPrice, c.StopPrice, c.Direction, opt.TakeProfitR);

                var limit = ApplyEntryLimitBuffer(c.EntryPrice, c.Direction, opt.EntryLimitBufferPct);
                // Create a virtual intent and simulate entry fill over the next N bars.
                var filled = TryFillEntry(barsBySymbol[c.Symbol], tUtc, c.Direction, qty, limit, opt, out var fillBar);
                if (!filled)
                {
                    trades.Add(new BacktestTrade
                    {
                        Symbol = c.Symbol,
                        Direction = c.Direction.ToString(),
                        Quantity = qty,
                        SignalTimeUtc = tUtc,
                        EntryLimit = limit,
                        StopPrice = c.StopPrice,
                        TakeProfitPrice = null,
                        Outcome = BacktestTradeOutcome.NoFill,
                        PnlDollars = 0m,
                        RMultiple = 0m,
                        Candidate = c,
                        RiskPerShare = Math.Abs(c.EntryPrice - c.StopPrice)
                    });
                    continue;
                }

                var entryFillPrice = ApplySlippage(limit, c.Direction, opt.SlippagePct);

                openPositions[c.Symbol] = new OpenPosition(
                    Symbol: c.Symbol,
                    Direction: c.Direction,
                    Quantity: qty,
                    SignalTimeUtc: tUtc,
                    EntryTimeUtc: fillBar!.TimeUtc,
                    EntryLimit: limit,
                    EntryPrice: entryFillPrice,
                    StopPrice: c.StopPrice,
                    TakeProfitPrice: tpPrice,
                    Candidate: c,
                    CommissionPerTrade: opt.CommissionPerTrade);
            }
        }

        // Any still open at the very end: exit on last known bar.
        foreach (var (sym, pos) in openPositions)
        {
            var last = barsBySymbol[sym].LastOrDefault();
            if (last is null) continue;
            trades.Add(ForceExitEod(last, pos, opt));
        }

        var result = BacktestResult.FromTrades(trades);
        return result;
    }

    internal static decimal ApplyEntryLimitBuffer(decimal entry, BreakoutDirection dir, decimal bufferPct)
    {
        if (bufferPct <= 0) return entry;
        return dir == BreakoutDirection.Long
            ? entry * (1m + bufferPct)
            : entry * (1m - bufferPct);
    }

    internal static bool TryFillEntry(
        IReadOnlyList<Bar> series,
        DateTime signalTimeUtc,
        BreakoutDirection dir,
        int qty,
        decimal limit,
        BacktestOptions opt,
        out Bar? fillBar)
    {
        fillBar = null;

        // start from next bar after signal
        var startIdx = series.BinarySearchByTime(signalTimeUtc);
        if (startIdx < 0) return false;

        var maxIdx = Math.Min(series.Count - 1, startIdx + opt.MaxBarsToFillEntry);

        for (var i = startIdx + 1; i <= maxIdx; i++)
        {
            var b = series[i];
            if (dir == BreakoutDirection.Long)
            {
                if (b.Low <= limit)
                {
                    fillBar = b;
                    return true;
                }
            }
            else
            {
                if (b.High >= limit)
                {
                    fillBar = b;
                    return true;
                }
            }
        }

        return false;
    }

    internal static decimal? ComputeTakeProfit(decimal entry, decimal stop, BreakoutDirection dir, decimal? takeProfitR)
    {
        if (!takeProfitR.HasValue) return null;
        var risk = Math.Abs(entry - stop);
        if (risk <= 0m) return null;
        var dist = risk * takeProfitR.Value;
        return dir == BreakoutDirection.Long ? entry + dist : entry - dist;
    }

    internal static BacktestTrade? TryEvaluateExit(Bar bar, OpenPosition pos, BacktestOptions opt)
    {
        var stop = pos.StopPrice;
        var tp = pos.TakeProfitPrice;

        bool stopHit;
        bool tpHit;

        if (pos.Direction == BreakoutDirection.Long)
        {
            stopHit = bar.Low <= stop;
            tpHit = tp.HasValue && bar.High >= tp.Value;
        }
        else
        {
            stopHit = bar.High >= stop;
            tpHit = tp.HasValue && bar.Low <= tp.Value;
        }

        if (!stopHit && !tpHit)
            return null;

        // Same-bar ambiguity
        if (stopHit && tpHit)
        {
            if (opt.SameBarRule == SameBarFillRule.ConservativeStopFirst)
                return Exit(pos, bar.TimeUtc, stop, BacktestTradeOutcome.Stop, opt);
            return Exit(pos, bar.TimeUtc, tp!.Value, BacktestTradeOutcome.TakeProfit, opt);
        }

        if (stopHit)
            return Exit(pos, bar.TimeUtc, stop, BacktestTradeOutcome.Stop, opt);

        return Exit(pos, bar.TimeUtc, tp!.Value, BacktestTradeOutcome.TakeProfit, opt);
    }

    internal static BacktestTrade ForceExitEod(Bar bar, OpenPosition pos, BacktestOptions opt)
        => Exit(pos, bar.TimeUtc, bar.Close, BacktestTradeOutcome.EndOfDay, opt);

    private static BacktestTrade Exit(OpenPosition pos, DateTime exitTimeUtc, decimal exitPriceRaw, BacktestTradeOutcome outcome, BacktestOptions opt)
    {
        var exitPrice = ApplySlippage(exitPriceRaw, pos.Direction == BreakoutDirection.Long ? BreakoutDirection.Short : BreakoutDirection.Long, opt.SlippagePct);

        var pnlPerShare = pos.Direction == BreakoutDirection.Long
            ? (exitPrice - pos.EntryPrice)
            : (pos.EntryPrice - exitPrice);

        var pnl = pnlPerShare * pos.Quantity;
        pnl -= pos.CommissionPerTrade; // crude: apply once per trade

        var riskPerShare = Math.Abs(pos.EntryPrice - pos.StopPrice);
        var r = riskPerShare > 0 ? pnlPerShare / riskPerShare : 0m;

        return new BacktestTrade
        {
            Symbol = pos.Symbol,
            Direction = pos.Direction.ToString(),
            Quantity = pos.Quantity,
            SignalTimeUtc = pos.SignalTimeUtc,
            EntryTimeUtc = pos.EntryTimeUtc,
            ExitTimeUtc = exitTimeUtc,
            EntryLimit = pos.EntryLimit,
            StopPrice = pos.StopPrice,
            TakeProfitPrice = pos.TakeProfitPrice,
            FilledEntryPrice = pos.EntryPrice,
            ExitPrice = exitPrice,
            Outcome = outcome,
            PnlDollars = pnl,
            RMultiple = r,
            RiskPerShare = riskPerShare,
            RewardPerShare = pos.TakeProfitPrice.HasValue ? Math.Abs(pos.TakeProfitPrice.Value - pos.EntryPrice) : null,
            Candidate = pos.Candidate
        };
    }

    internal static decimal ApplySlippage(decimal price, BreakoutDirection dir, decimal slippagePct)
    {
        var slip = price * slippagePct;
        return dir == BreakoutDirection.Long ? price + slip : price - slip;
    }

    // public sealed record OpenPosition(
    //     string Symbol,
    //     BreakoutDirection Direction,
    //     int Quantity,
    //     DateTime SignalTimeUtc,
    //     DateTime EntryTimeUtc,
    //     decimal EntryLimit,
    //     decimal EntryPrice,
    //     decimal StopPrice,
    //     decimal? TakeProfitPrice,
    //     SetupCandidate Candidate,
    //     decimal CommissionPerTrade);
}

public sealed record BacktestResult(IReadOnlyList<BacktestTrade> Trades, BacktestSummary Summary)
{
    public static BacktestResult FromTrades(IReadOnlyList<BacktestTrade> trades)
    {
        var realized = trades.Where(t => t.Outcome != BacktestTradeOutcome.NoFill).ToList();
        var pnlSeries = realized.Select(t => t.PnlDollars ?? 0m).ToList();

        decimal equity = 0m;
        decimal peak = 0m;
        decimal maxDd = 0m;
        foreach (var p in pnlSeries)
        {
            equity += p;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDd) maxDd = dd;
        }

        var wins = realized.Count(t => (t.PnlDollars ?? 0m) > 0m);
        var losses = realized.Count(t => (t.PnlDollars ?? 0m) < 0m);
        var noFills = trades.Count(t => t.Outcome == BacktestTradeOutcome.NoFill);
        var total = realized.Sum(t => t.PnlDollars ?? 0m);
        var avg = realized.Count > 0 ? total / realized.Count : 0m;
        var winRate = realized.Count > 0 ? (decimal)wins / realized.Count : 0m;
        var avgR = realized.Count > 0 ? realized.Average(t => t.RMultiple ?? 0m) : 0m;

        var summary = new BacktestSummary(
            Trades: trades.Count,
            Wins: wins,
            Losses: losses,
            NoFills: noFills,
            TotalPnl: total,
            AvgPnl: avg,
            WinRate: winRate,
            AvgR: avgR,
            MaxDrawdown: maxDd);

        return new BacktestResult(trades, summary);
    }
}

internal static class BarListExtensions
{
    public static int BinarySearchByTime(this List<Bar> bars, DateTime tUtc)
    {
        int lo = 0, hi = bars.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var mt = bars[mid].TimeUtc;
            if (mt == tUtc) return mid;
            if (mt < tUtc) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    public static int BinarySearchByTime(this IReadOnlyList<Bar> bars, DateTime tUtc)
    {
        int lo = 0, hi = bars.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var mt = bars[mid].TimeUtc;
            if (mt == tUtc) return mid;
            if (mt < tUtc) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }
}
