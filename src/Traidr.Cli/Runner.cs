using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Traidr.Core.Llm;
using Traidr.Core.MarketData;
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
        var sessionMode = ParseSessionMode(_cfg.GetValue<string>("Execution:SessionMode"));
        var tradeDirection = TradeDirectionParser.Parse(_cfg.GetValue<string>("Execution:TradeDirection"));
        var sessionHours = _cfg.GetSection("MarketSessions").Get<MarketSessionHours>() ?? new MarketSessionHours();
        var extendedLookbackHours = _cfg.GetValue("Execution:ExtendedLookbackHours", 72);
        var entryLimitBufferPct = _cfg.GetValue("Execution:EntryLimitBufferPct", 0.0m);
        var maxFillBars = _cfg.GetValue<int?>("Execution:MaxBarsToFillEntry");
        var takeProfitR = ParseNullableDecimal(_cfg.GetValue<string>("Execution:TakeProfitR"));
        var marketTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

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

        var nowUtc = DateTime.UtcNow;
        var effectiveMode = ResolveLiveSessionMode(nowUtc, marketTz, sessionMode, sessionHours);
        if (effectiveMode is null)
        {
            _log.LogPink("Market session closed; skipping scan.");
            return;
        }

        var toUtc = nowUtc;
        var baseLookback = Math.Max(30, lookbackMinutes);
        var fromUtc = toUtc.AddMinutes(-baseLookback);
        if (effectiveMode is MarketSessionMode.PreMarket or MarketSessionMode.AfterHours)
        {
            var extendedMinutes = extendedLookbackHours * 60;
            if (baseLookback < extendedMinutes)
                fromUtc = toUtc.AddMinutes(-extendedMinutes);
        }

        var bars = await _marketData.GetHistoricalBarsAsync(accepted, fromUtc, toUtc, timeframe, ct);

        _log.LogInformation("Fetched bars: {Count} across {Symbols} symbols", bars.Count, bars.Select(b => b.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // 4) Scan for setups
        var scanner = _scannerFactory.Create(strategy);
        var candidates = tradeDirection.Filter(scanner.Scan(bars));
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
        var nowOffset = DateTimeOffset.UtcNow;

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
            entry = ApplyEntryLimitBuffer(entry, c.Direction, entryLimitBufferPct);
            var stop = s.StopPrice ?? c.StopPrice;
            decimal? takeProfit = s.TakeProfitPrice ?? c.TakeProfitPrice;
            if (!takeProfit.HasValue)
            {
                var tpR = strategy == TradingStrategy.Emmanuel
                    ? _cfg.GetValue("Execution:Emmanuel:TakeProfitR", 2.0m)
                    : (takeProfitR ?? 0m);

                if (tpR > 0m)
                {
                    var riskPerShare = Math.Abs(entry - stop);
                    if (riskPerShare > 0m)
                    {
                        takeProfit = c.Direction == BreakoutDirection.Long
                            ? entry + (riskPerShare * tpR)
                            : entry - (riskPerShare * tpR);
                    }
                }
            }

            var decision = _risk.Evaluate(c with { EntryPrice = entry, StopPrice = stop }, takeProfit, nowOffset);
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
                TakeProfitPrice: takeProfit,
                FillTimeoutOverride: ComputeFillTimeout(maxFillBars, timeframe));

            _log.LogGreen("EXECUTE: {Symbol} qty={Qty} estRisk={Risk} tp={Tp}",
                c.Symbol, qty, decision.EstimatedRiskDollars, takeProfit);

            await _executor.ExecuteAsync(intent, ct);
        }
    }

    private static TradingStrategy ParseStrategy(string? value)
    {
        return Enum.TryParse<TradingStrategy>(value ?? string.Empty, ignoreCase: true, out var strategy)
            ? strategy
            : TradingStrategy.Oliver;
    }

    private static MarketSessionMode ParseSessionMode(string? value)
    {
        return Enum.TryParse<MarketSessionMode>(value ?? string.Empty, ignoreCase: true, out var mode)
            ? mode
            : MarketSessionMode.Auto;
    }

    private static MarketSessionMode? ResolveLiveSessionMode(
        DateTime nowUtc,
        TimeZoneInfo marketTz,
        MarketSessionMode mode,
        MarketSessionHours hours)
    {
        if (mode != MarketSessionMode.Auto)
            return mode;

        var session = MarketSessionHelper.ResolveSession(nowUtc, marketTz, hours);
        return session switch
        {
            MarketSession.PreMarket => MarketSessionMode.PreMarket,
            MarketSession.Regular => MarketSessionMode.Regular,
            MarketSession.AfterHours => MarketSessionMode.AfterHours,
            _ => null
        };
    }

    private static decimal ApplyEntryLimitBuffer(decimal entry, BreakoutDirection dir, decimal bufferPct)
    {
        if (bufferPct <= 0m)
            return entry;
        return dir == BreakoutDirection.Long
            ? entry * (1m + bufferPct)
            : entry * (1m - bufferPct);
    }

    private static TimeSpan? ComputeFillTimeout(int? maxBars, string timeframe)
    {
        if (!maxBars.HasValue || maxBars.Value <= 0)
            return null;

        var minutes = ParseTimeframeMinutes(timeframe);
        if (minutes <= 0)
            return null;

        return TimeSpan.FromMinutes(minutes * maxBars.Value);
    }

    private static int ParseTimeframeMinutes(string? timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe))
            return 0;

        var tf = timeframe.Trim();
        if (tf.EndsWith("Min", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(tf[..^3], out var mins))
                return mins;
        }
        if (tf.EndsWith("Hour", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(tf[..^4], out var hours))
                return hours * 60;
        }
        if (tf.EndsWith("H", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(tf[..^1], out var hours))
                return hours * 60;
        }

        return 0;
    }

    private static decimal? ParseNullableDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
