using Traidr.Core.MarketData;
using Traidr.Core.Scanning;
using Traidr.Core.Trading;

namespace Traidr.Core.Backtesting;

/// <summary>
/// Runs a deterministic backtest against a pre-loaded <see cref="BacktestDataSet"/>.
/// This is used by the optimization loop so we don't re-download data for each trial.
/// </summary>
public static class BacktestSimulator
{
    public static BacktestResult Run(
        BacktestDataSet data,
        BacktestOptions opt,
        ISetupScanner scanner,
        IRiskManager risk,
        CancellationToken ct = default)
    {
        if (opt.Symbols.Count == 0)
            return new BacktestResult(Array.Empty<BacktestTrade>(), new BacktestSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m));

        // Sliding windows per symbol.
        var windows = new Dictionary<string, List<Bar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in opt.Symbols)
            windows[sym] = new List<Bar>(512);

        var openPositions = new Dictionary<string, OpenPosition>(StringComparer.OrdinalIgnoreCase);
        var trades = new List<BacktestTrade>();

        for (var ti = 0; ti < data.TimesUtc.Count; ti++)
        {
            ct.ThrowIfCancellationRequested();
            var tUtc = data.TimesUtc[ti];

            // Update windows (append any bars exactly at this time)
            foreach (var sym in opt.Symbols)
            {
                if (!data.BarsBySymbol.TryGetValue(sym, out var series)) continue;
                var idx = series.BinarySearchByTime(tUtc);
                if (idx >= 0)
                {
                    windows[sym].Add(series[idx]);
                    if (windows[sym].Count > 400)
                        windows[sym].RemoveRange(0, windows[sym].Count - 400);
                }
            }

            // Process exits on this bar for any open positions
            foreach (var sym in openPositions.Keys.ToList())
            {
                if (!data.BarsBySymbol.TryGetValue(sym, out var series)) continue;
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
            var local = TimeZoneInfo.ConvertTime(tUtc, data.MarketTimeZone);
            if (local.TimeOfDay >= opt.FlattenTimeEt.ToTimeSpan())
            {
                foreach (var sym in openPositions.Keys.ToList())
                {
                    if (!data.BarsBySymbol.TryGetValue(sym, out var series)) continue;
                    var idx = series.BinarySearchByTime(tUtc);
                    if (idx < 0) continue;
                    var bar = series[idx];

                    var pos = openPositions[sym];
                    openPositions.Remove(sym);
                    trades.Add(ForceExitEod(bar, pos, opt));
                }
            }

            var inSession = MarketSessionHelper.IsInSession(tUtc, data.MarketTimeZone, opt.SessionMode, opt.SessionHours);
            if (!inSession)
                continue;

            // Scan
            var candidates = scanner.Scan(windows);
            if (candidates.Count == 0)
                continue;

            foreach (var c in candidates)
            {
                if (openPositions.ContainsKey(c.Symbol))
                    continue;

                var now = new DateTimeOffset(tUtc, TimeSpan.Zero);
                var riskDecision = risk.Evaluate(c, takeProfitPrice: null, now);
                if (riskDecision.Decision == RiskDecisionType.Block)
                    continue;

                var qty = riskDecision.Quantity!.Value;
                var tpPrice = ComputeTakeProfit(c.EntryPrice, c.StopPrice, c.Direction, opt.TakeProfitR);
                var limit = ApplyEntryLimitBuffer(c.EntryPrice, c.Direction, opt.EntryLimitBufferPct);

                if (!data.BarsBySymbol.TryGetValue(c.Symbol, out var series))
                    continue;

                var filled = TryFillEntry(series, tUtc, c.Direction, qty, limit, opt, out var fillBar);
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

        foreach (var (sym, pos) in openPositions)
        {
            if (!data.BarsBySymbol.TryGetValue(sym, out var series)) continue;
            var last = series.LastOrDefault();
            if (last is null) continue;
            trades.Add(ForceExitEod(last, pos, opt));
        }

        return BacktestResult.FromTrades(trades);
    }

    // The helper methods below are identical to BacktestEngine (kept internal to avoid a large refactor)

    private static bool TryFillEntry(
        IReadOnlyList<Bar> series,
        DateTime signalTimeUtc,
        BreakoutDirection dir,
        int qty,
        decimal limit,
        BacktestOptions opt,
        out Bar? fillBar)
        => BacktestEngine.TryFillEntry(series, signalTimeUtc, dir, qty, limit, opt, out fillBar);

    private static decimal ApplySlippage(decimal price, BreakoutDirection dir, decimal slippagePct)
        => BacktestEngine.ApplySlippage(price, dir, slippagePct);

    private static decimal ApplyEntryLimitBuffer(decimal entry, BreakoutDirection dir, decimal bufferPct)
        => BacktestEngine.ApplyEntryLimitBuffer(entry, dir, bufferPct);

    private static decimal? ComputeTakeProfit(decimal entry, decimal stop, BreakoutDirection dir, decimal? tpR)
        => BacktestEngine.ComputeTakeProfit(entry, stop, dir, tpR);

    private static BacktestTrade? TryEvaluateExit(Bar bar, OpenPosition pos, BacktestOptions opt)
        => BacktestEngine.TryEvaluateExit(bar, pos, opt);

    private static BacktestTrade ForceExitEod(Bar bar, OpenPosition pos, BacktestOptions opt)
        => BacktestEngine.ForceExitEod(bar, pos, opt);

    // private sealed record OpenPosition(
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
