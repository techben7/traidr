using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Traidr.Core.Llm;
using Traidr.Core.MarketData;
using Traidr.Core.Scanning;
using Traidr.Core.Trading;

namespace Traidr.Cli;

public sealed class Runner
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<Runner> _log;

    private readonly UniverseBuilder _universe;
    private readonly IUniversePreFilter _preFilter;
    private readonly IMarketDataClient _marketData;
    private readonly IStrategyScannerFactory _scannerFactory;
    private readonly ILlmScorer _llm;
    private readonly IRiskManager _risk;
    private readonly IOrderExecutor _executor;

    public Runner(
        IConfiguration cfg,
        ILogger<Runner> log,
        UniverseBuilder universe,
        IUniversePreFilter preFilter,
        IMarketDataClient marketData,
        IStrategyScannerFactory scannerFactory,
        ILlmScorer llm,
        IRiskManager risk,
        IOrderExecutor executor)
    {
        _cfg = cfg;
        _log = log;
        _universe = universe;
        _preFilter = preFilter;
        _marketData = marketData;
        _scannerFactory = scannerFactory;
        _llm = llm;
        _risk = risk;
        _executor = executor;
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var watchlist = _cfg.GetSection("Watchlist").Get<string[]>() ?? Array.Empty<string>();
        var topGainers = _cfg.GetValue<int>("TopGainersCount");
        var timeframe = _cfg.GetValue<string>("Execution:Timeframe") ?? "5Min";
        var lookbackMinutes = _cfg.GetValue<int>("Execution:LookbackMinutes");
        var strategy = ParseStrategy(
            _cfg.GetValue<string>("Execution:Strategy")
            ?? _cfg.GetValue<string>("Strategy:Default"));

        // 1) Universe
        var universe = await _universe.BuildUniverseAsync(watchlist, topGainers, ct);
        _log.LogInformation("Universe size: {Count} ({Symbols})", universe.Count, string.Join(",", universe.Take(30)));

        // 2) Pre-filters
        var preOpt = _cfg.GetSection("PreFilter").Get<PreFilterOptions>() ?? new PreFilterOptions();
        var (accepted, decisions) = await _preFilter.FilterAsync(universe, preOpt, ct);

        var rejected = decisions.Count(d => !d.Accepted);
        _log.LogInformation("PreFilter accepted={Accepted} rejected={Rejected}", accepted.Count, rejected);

        // 3) Fetch bars
        if (accepted.Count == 0)
        {
            _log.LogPink("No symbols passed prefilter.");
            return;
        }

        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddMinutes(-Math.Max(30, lookbackMinutes));

        var bars = await _marketData.GetHistoricalBarsAsync(accepted, fromUtc, toUtc, timeframe, ct);

        _log.LogInformation("Fetched bars: {Count} across {Symbols} symbols", bars.Count, bars.Select(b => b.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // 4) Scan for setups
        var scanner = _scannerFactory.Create(strategy);
        var candidates = scanner.Scan(bars);
        if (candidates.Count == 0)
        {
            _log.LogPink("No {Strategy} setups found this run.", strategy);
            return;
        }

        _log.LogInformation("Candidates found: {Count}", candidates.Count);
        foreach (var c in candidates)
        {
            _log.LogInformation("Candidate {Symbol} {Dir} entry={Entry} stop={Stop} range={RangePct:P2} bodyx={BodyX:F2} volx={VolX:F2}",
                c.Symbol, c.Direction, c.EntryPrice, c.StopPrice, c.RangePct, c.BodyToMedianBody, c.VolumeToAvgVolume);
        }

        // 5) LLM scoring
        var llmResp = await _llm.ScoreAsync(candidates, ct);
        var scoreBySymbol = llmResp.Scores.ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);

        // 6) Risk + Execute
        var nowUtc = DateTimeOffset.UtcNow;

        foreach (var c in candidates)
        {
            if (!scoreBySymbol.TryGetValue(c.Symbol, out var s))
                continue;

            if (s.Action != LlmTradeAction.Trade)
            {
                _log.LogInformation("LLM: {Symbol} action={Action} score={Score} reason={Reason}", s.Symbol, s.Action, s.Score, s.Reason);
                continue;
            }

            var entry = s.EntryPrice ?? c.EntryPrice;
            var stop = s.StopPrice ?? c.StopPrice;

            var decision = _risk.Evaluate(c with { EntryPrice = entry, StopPrice = stop }, s.TakeProfitPrice, nowUtc);
            if (decision.Decision == RiskDecisionType.Block)
            {
                _log.LogInformation("RISK BLOCK: {Symbol} {Reason}", c.Symbol, decision.Reason);
                continue;
            }

            var qty = decision.Quantity!.Value;

            var intent = new TradeIntent(
                Symbol: c.Symbol,
                Direction: c.Direction,
                Quantity: qty,
                EntryPrice: entry,
                StopPrice: stop,
                TakeProfitPrice: s.TakeProfitPrice);

            _log.LogGreen("EXECUTE: {Symbol} qty={Qty} estRisk={Risk} tp={Tp}",
                c.Symbol, qty, decision.EstimatedRiskDollars, s.TakeProfitPrice);

            await _executor.ExecuteAsync(intent, ct);
        }
    }

    private static TradingStrategy ParseStrategy(string? value)
    {
        return Enum.TryParse<TradingStrategy>(value ?? string.Empty, ignoreCase: true, out var strategy)
            ? strategy
            : TradingStrategy.Oliver;
    }
}
