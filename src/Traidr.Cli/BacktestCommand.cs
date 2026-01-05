using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Traidr.Core.Backtesting;

namespace Traidr.Cli;

public sealed class BacktestCommand
{
    private readonly ILogger<BacktestCommand> _log;
    private readonly BacktestEngine _engine;

    public BacktestCommand(ILogger<BacktestCommand> log, BacktestEngine engine)
    {
        _log = log;
        _engine = engine;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var opt = ParseArgs(args);
        _log.LogInformation("Backtest symbols={Symbols} from={From} to={To} timeframe={Tf}",
            string.Join(",", opt.Symbols), opt.FromDateEt, opt.ToDateEt, opt.Timeframe);

        var result = await _engine.RunAsync(opt, ct);

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

    private BacktestOptions ParseArgs(string[] args)
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

        if (!dict.TryGetValue("symbols", out var symCsv) || string.IsNullOrWhiteSpace(symCsv))
            throw new ArgumentException("Backtest requires --symbols (comma-separated). Example: --symbols SOXL,QBTS,PATH");

        if (!dict.TryGetValue("from", out var fromStr) || !DateOnly.TryParse(fromStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
            throw new ArgumentException("Backtest requires --from YYYY-MM-DD");

        if (!dict.TryGetValue("to", out var toStr) || !DateOnly.TryParse(toStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
            throw new ArgumentException("Backtest requires --to YYYY-MM-DD");

        if (dict.TryGetValue("out", out var outDir) && !string.IsNullOrWhiteSpace(outDir))
            _outDir = outDir;

        var timeframe = dict.TryGetValue("timeframe", out var tf) && !string.IsNullOrWhiteSpace(tf) ? tf : "5Min";

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

        var symbols = symCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new BacktestOptions
        {
            Symbols = symbols,
            FromDateEt = fromDate,
            ToDateEt = toDate,
            Timeframe = timeframe,
            MaxBarsToFillEntry = maxBarsToFill,
            EntryLimitBufferPct = entryBufPct,
            FlattenTimeEt = flatten,
            SameBarRule = sameBarRule,
            SlippagePct = slippagePct,
            CommissionPerTrade = commission,
            TakeProfitR = tpR
        };
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
