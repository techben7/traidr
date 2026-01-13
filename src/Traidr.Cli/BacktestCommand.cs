using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Traidr.Core.Backtesting;
using Traidr.Core.MarketData;
using Traidr.Core.Scanning;

namespace Traidr.Cli;

public sealed class BacktestCommand
{
    private readonly ILogger<BacktestCommand> _log;
    private readonly BacktestEngine _engine;
    private readonly IStrategyScannerFactory _scannerFactory;
    private readonly IConfiguration _cfg;
    private readonly AutoUniverseSelector _autoUniverse;

    public BacktestCommand(
        ILogger<BacktestCommand> log,
        BacktestEngine engine,
        IStrategyScannerFactory scannerFactory,
        IConfiguration cfg,
        AutoUniverseSelector autoUniverse)
    {
        _log = log;
        _engine = engine;
        _scannerFactory = scannerFactory;
        _cfg = cfg;
        _autoUniverse = autoUniverse;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var (opt, strategy) = ParseArgs(args);
        if (opt.Symbols.Count == 0)
        {
            var autoSymbols = await _autoUniverse.GetSymbolsAsync(ct);
            opt = opt with { Symbols = autoSymbols };
        }

        _log.LogInformation("Backtest strategy={Strategy} session={Session} symbols={Symbols} from={From} to={To} timeframe={Tf}",
            strategy, opt.SessionMode, string.Join(",", opt.Symbols), opt.FromDateEt, opt.ToDateEt, opt.Timeframe);

        var scanner = _scannerFactory.Create(strategy);
        var result = await _engine.RunAsync(scanner, opt, ct);

        Directory.CreateDirectory(_outDir);

        var timestamp_Full = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmssfff");
        var timestampOutDir = Path.Combine(_outDir, timestamp_Full);
        Directory.CreateDirectory(timestampOutDir);

        var paramsPath = Path.Combine(timestampOutDir, "params.json");
        var json0 = JsonSerializer.Serialize(opt, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(paramsPath, json0, ct);

        var tradesPath = Path.Combine(timestampOutDir, "trades.csv");
        await File.WriteAllTextAsync(tradesPath, ToCsv(result.Trades), ct);

        var summaryPath = Path.Combine(timestampOutDir, "summary.json");
        var json = JsonSerializer.Serialize(result.Summary, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(summaryPath, json, ct);

        _log.LogPink("Backtest complete. Trades={Trades} Wins={Wins} Losses={Losses} NoFills={NoFills} TotalPnL={Total}",
            result.Summary.Trades, result.Summary.Wins, result.Summary.Losses, result.Summary.NoFills, result.Summary.TotalPnl);
        _log.LogInformation("Outputs: {TradesPath} | {SummaryPath}", tradesPath, summaryPath);

        return 0;
    }

    private string _outDir = "_BacktestResults";

    private (BacktestOptions Options, TradingStrategy Strategy) ParseArgs(string[] args)
    {
        // args includes: ["backtest", ...]
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            var key = a.Substring(2);
            var val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
            dict[key] = val;
        }

        dict.TryGetValue("symbols", out var symCsv);

        if (!dict.TryGetValue("from", out var fromStr) || !DateOnly.TryParse(fromStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
            throw new ArgumentException("Backtest requires --from YYYY-MM-DD");

        if (!dict.TryGetValue("to", out var toStr) || !DateOnly.TryParse(toStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
            throw new ArgumentException("Backtest requires --to YYYY-MM-DD");

        if (dict.TryGetValue("out", out var outDir) && !string.IsNullOrWhiteSpace(outDir))
            _outDir = outDir;

        var timeframe = dict.TryGetValue("timeframe", out var tf) && !string.IsNullOrWhiteSpace(tf) ? tf : "5Min";
        var strategy = ParseStrategy(dict.TryGetValue("strategy", out var st) ? st : null);
        var sessionMode = ParseSessionMode(dict.TryGetValue("session", out var sm) ? sm : null);
        var sessionHours = _cfg.GetSection("MarketSessions").Get<MarketSessionHours>() ?? new MarketSessionHours();

        var execDirection = TradeDirectionParser.Parse(_cfg.GetValue<string>("Execution:TradeDirection"));
        var backtestDirection = _cfg.GetValue<string>("Backtest:TradeDirection");
        var argDirection =
            dict.TryGetValue("tradeDirection", out var tdLong)
                ? tdLong
                : (dict.TryGetValue("direction", out var tdShort) ? tdShort : null);
        var tradeDirection = TradeDirectionParser.Parse(argDirection ?? backtestDirection, execDirection);

        var maxBarsToFill = dict.TryGetValue("maxFillBars", out var mb) && int.TryParse(mb, out var n) ? Math.Max(1, n) : 6;

        var entryBufPct = dict.TryGetValue("entryLimitBufferPct", out var eb) && decimal.TryParse(eb, NumberStyles.Number, CultureInfo.InvariantCulture, out var ebd)
            ? Math.Max(0m, ebd)
            : 0m;

        var flatten = new TimeOnly(15, 50);
        if (dict.TryGetValue("flatten", out var fl) && TimeOnly.TryParseExact(fl, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            flatten = t;

        var sameBarRule = SameBarFillRule.ConservativeStopFirst;
        if (dict.TryGetValue("samebar", out var sb))
        {
            if (sb.Equals("optimistic", StringComparison.OrdinalIgnoreCase) || sb.Equals("tpfirst", StringComparison.OrdinalIgnoreCase))
                sameBarRule = SameBarFillRule.OptimisticTakeProfitFirst;
        }

        var slippagePct = dict.TryGetValue("slippagePct", out var sp) && decimal.TryParse(sp, NumberStyles.Number, CultureInfo.InvariantCulture, out var sdp)
            ? sdp
            : 0.0005m;

        var commission = dict.TryGetValue("commission", out var cm) && decimal.TryParse(cm, NumberStyles.Number, CultureInfo.InvariantCulture, out var c)
            ? c
            : 0m;

        decimal? tpR = null;
        if (dict.TryGetValue("tpR", out var tpr) && decimal.TryParse(tpr, NumberStyles.Number, CultureInfo.InvariantCulture, out var tr))
            tpR = tr;

        var symbols = string.IsNullOrWhiteSpace(symCsv)
            ? new List<string>()
            : symCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return (new BacktestOptions
        {
            Symbols = symbols,
            FromDateEt = fromDate,
            ToDateEt = toDate,
            Timeframe = timeframe,
            MaxBarsToFillEntry = maxBarsToFill,
            EntryLimitBufferPct = entryBufPct,
            FlattenTimeEt = flatten,
            SessionMode = sessionMode,
            SessionHours = sessionHours,
            SameBarRule = sameBarRule,
            SlippagePct = slippagePct,
            CommissionPerTrade = commission,
            TradeDirection = tradeDirection,
            TakeProfitR = tpR
        }, strategy);
    }

    private TradingStrategy ParseStrategy(string? value)
    {
        var fallback = _cfg.GetValue<string>("Strategy:Default") ?? "Oliver";
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return Enum.TryParse<TradingStrategy>(raw, ignoreCase: true, out var strategy)
            ? strategy
            : TradingStrategy.Oliver;
    }

    private MarketSessionMode ParseSessionMode(string? value)
    {
        var fallback = _cfg.GetValue<string>("Backtest:SessionMode")
                       ?? _cfg.GetValue<string>("Execution:SessionMode")
                       ?? "Auto";
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return Enum.TryParse<MarketSessionMode>(raw, ignoreCase: true, out var mode)
            ? mode
            : MarketSessionMode.Auto;
    }

    private static string ToCsv(IReadOnlyList<BacktestTrade> trades)
    {
        var sb = new StringBuilder();
        sb.AppendLine("symbol,direction,qty,signalTimeUtc,entryTimeUtc,exitTimeUtc,entryLimit,filledEntry,stop,tp,exitPrice,outcome,pnl,r");
        foreach (var t in trades)
        {
            sb.Append(t.Symbol).Append(',')
              .Append(t.Direction).Append(',')
              .Append(t.Quantity).Append(',')
              .Append(t.SignalTimeUtc.ToString("O")).Append(',')
              .Append(t.EntryTimeUtc?.ToString("O") ?? "").Append(',')
              .Append(t.ExitTimeUtc?.ToString("O") ?? "").Append(',')
              .Append(t.EntryLimit.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(t.FilledEntryPrice?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(t.StopPrice.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(t.TakeProfitPrice?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(t.ExitPrice?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(t.Outcome).Append(',')
              .Append((t.PnlDollars ?? 0m).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append((t.RMultiple ?? 0m).ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
        return sb.ToString();
    }
}
