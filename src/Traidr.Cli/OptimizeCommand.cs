using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Traidr.Core.Backtesting;
using Traidr.Core.Indicators;
using Traidr.Core.MarketData;
using Traidr.Core.Scanning;
using Traidr.Core.Trading;

namespace Traidr.Cli;

public sealed class OptimizeCommand
{
    private readonly ILogger<OptimizeCommand> _log;
    private readonly IConfiguration _cfg;
    private readonly IMarketDataClient _marketData;
    private readonly IndicatorCalculatorOptions _indicatorOptions;
    private readonly RiskManagerOptions _riskOptions;

    public OptimizeCommand(
        ILogger<OptimizeCommand> log,
        IConfiguration cfg,
        IMarketDataClient marketData,
        IndicatorCalculatorOptions indicatorOptions,
        RiskManagerOptions riskOptions)
    {
        _log = log;
        _cfg = cfg;
        _marketData = marketData;
        _indicatorOptions = indicatorOptions;
        _riskOptions = riskOptions;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var opt = ParseArgs(args);

        _log.LogInformation(
            "Optimize trials={Trials} symbols={Symbols} train={TrainFrom}..{TrainTo} test={TestFrom}..{TestTo}",
            opt.Trials,
            string.Join(',', opt.Symbols),
            opt.TrainFromEt, opt.TrainToEt,
            opt.TestFromEt, opt.TestToEt);

        Directory.CreateDirectory(opt.OutDir);

        // Load datasets once (fast repeated trials)
        _log.LogInformation("Loading train bars from Alpaca...");
        var trainData = await BacktestDataLoader.LoadAsync(_marketData, opt.Symbols, opt.TrainFromEt, opt.TrainToEt, opt.Timeframe, ct: ct);
        _log.LogInformation("Loading test bars from Alpaca...");
        var testData = await BacktestDataLoader.LoadAsync(_marketData, opt.Symbols, opt.TestFromEt, opt.TestToEt, opt.Timeframe, ct: ct);

        var rng = new Random(opt.Seed);
        var results = new List<OptimizeTrialResult>(capacity: opt.Trials);

        for (var i = 0; i < opt.Trials; i++)
        {
            ct.ThrowIfCancellationRequested();

            _log.LogYellow($"Running trail # {i}...");

            var trial = SampleTrial(rng, opt);

            var trainRun = RunOnce(trainData, opt, trial, opt.TrainFromEt, opt.TrainToEt);
            var testRun = RunOnce(testData, opt, trial, opt.TestFromEt, opt.TestToEt);

            // Gate: require a minimum number of filled trades to avoid "winning" with too few samples.
            var trainFilled = trainRun.Summary.Trades - trainRun.Summary.NoFills;
            var testFilled = testRun.Summary.Trades - testRun.Summary.NoFills;

            var trainMetrics = ComputeMetrics(trainRun, _riskOptions.AccountEquity);
            var testMetrics = ComputeMetrics(testRun, _riskOptions.AccountEquity);

            var trainScore = CompositeScore(trainMetrics, opt);
            var testScore = CompositeScore(testMetrics, opt);

            var finalScore = (trainScore * opt.TrainWeight) + (testScore * opt.TestWeight);

            if (trainFilled < opt.MinFilledTrades || testFilled < opt.MinFilledTrades)
            {
                // Penalize heavily if not enough trades
                finalScore -= 1_000m;
            }

            results.Add(new OptimizeTrialResult
            {
                TrialIndex = i + 1,
                Scanner = trial.Scanner,
                Backtest = trial.Backtest,
                Train = trainMetrics,
                Test = testMetrics,
                TrainScore = trainScore,
                TestScore = testScore,
                FinalScore = finalScore
            });
        }

        var ordered = results.OrderByDescending(r => r.FinalScore).ToList();
        var top = ordered.Take(opt.TopN).ToList();

        var timestamp_Full = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmssfff");

        var outDirBase = "_BacktestOptimizationRuns";

        var timestampOutDir = Path.Combine(outDirBase, timestamp_Full);
        Directory.CreateDirectory(timestampOutDir);

        // var paramsPath = Path.Combine(timestampOutDir, "trials.json");
        // var json0 = JsonSerializer.Serialize(opt.Trials, new JsonSerializerOptions { WriteIndented = true });
        // await File.WriteAllTextAsync(paramsPath, json0, ct);

        var csvPath = Path.Combine(timestampOutDir, "optimization_results.csv");
        await File.WriteAllTextAsync(csvPath, ToCsv(ordered), ct);

        var topPath = Path.Combine(timestampOutDir, "top_configs.json");
        var json = JsonSerializer.Serialize(top, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(topPath, json, ct);

        _log.LogInformation("Optimization complete. Results: {CsvPath} | {TopPath}", csvPath, topPath);
        _log.LogInformation("Top {N} configs (by FinalScore):", top.Count);
        foreach (var r in top)
        {
            _log.LogPink(
                "#{Idx} score={Score:F2} trainR={TrainR:F3} trainPF={TrainPF:F2} trainDD={TrainDD:P2} testR={TestR:F3} testPF={TestPF:F2} testDD={TestDD:P2} | lookback={Look} rangePct={Range:P2} body={Body:F2} vol={Vol:F2} fillBars={FillBars} entryBuf={EntryBuf:P2} tpR={TpR}",
                r.TrialIndex,
                r.FinalScore,
                r.Train.AvgR,
                r.Train.ProfitFactor,
                r.Train.MaxDrawdownPct,
                r.Test.AvgR,
                r.Test.ProfitFactor,
                r.Test.MaxDrawdownPct,
                r.Scanner.ConsolidationLookbackBars,
                r.Scanner.MaxConsolidationRangePct,
                r.Scanner.MinBodyToMedianBody,
                r.Scanner.MinVolumeToAvgVolume,
                r.Backtest.MaxBarsToFillEntry,
                r.Backtest.EntryLimitBufferPct,
                r.Backtest.TakeProfitR?.ToString(CultureInfo.InvariantCulture) ?? "null");
        }

        return 0;
    }

    private BacktestResult RunOnce(BacktestDataSet data, OptimizeOptions opt, TrialSettings trial, DateOnly fromEt, DateOnly toEt)
    {
        // Fresh state per run
        var riskState = new InMemoryRiskState();
        var risk = new RiskManager(riskState, _riskOptions);
        var calc = new IndicatorCalculator(_indicatorOptions);
        var scanner = new TraidrScanner(calc, trial.Scanner);

        var runOpt = new BacktestOptions
        {
            Symbols = opt.Symbols,
            FromDateEt = fromEt,
            ToDateEt = toEt,
            Timeframe = opt.Timeframe,
            MaxBarsToFillEntry = trial.Backtest.MaxBarsToFillEntry,
            EntryLimitBufferPct = trial.Backtest.EntryLimitBufferPct,
            FlattenTimeEt = opt.FlattenTimeEt,
            SameBarRule = opt.SameBarRule,
            SlippagePct = opt.SlippagePct,
            CommissionPerTrade = opt.CommissionPerTrade,
            TakeProfitR = trial.Backtest.TakeProfitR
        };

        return BacktestSimulator.Run(data, runOpt, scanner, risk);
    }

    private static TrialSettings SampleTrial(Random rng, OptimizeOptions opt)
    {
        // Scanner
        var lookback = rng.Next(opt.LookbackMin, opt.LookbackMax + 1);

        decimal RandDecimal(decimal min, decimal max)
        {
            var u = (decimal)rng.NextDouble();
            return min + (u * (max - min));
        }

        var scanner = new TraidrScannerOptions
        {
            ConsolidationLookbackBars = lookback,
            MaxConsolidationRangePct = RandDecimal(opt.RangePctMin, opt.RangePctMax),
            MinBodyToMedianBody = RandDecimal(opt.BodyToMedianMin, opt.BodyToMedianMax),
            MinVolumeToAvgVolume = RandDecimal(opt.VolRatioMin, opt.VolRatioMax),
            BreakoutBufferPct = RandDecimal(opt.BreakoutBufferMin, opt.BreakoutBufferMax),
            StopBufferPct = RandDecimal(opt.StopBufferMin, opt.StopBufferMax),

            // keep indicator filters fixed unless you explicitly want to search these
            RequireNearEma20 = opt.RequireNearEma20,
            MaxDistanceFromEma20Pct = opt.MaxDistanceFromEma20Pct,
            RequireEma20NearEma200 = opt.RequireEma20NearEma200,
            MaxEmaDistancePct = opt.MaxEmaDistancePct,
            RequireAtrAvailable = opt.RequireAtrAvailable,
            MinAtrPct = opt.MinAtrPct,
            MaxAtrPct = opt.MaxAtrPct
        };

        var backtest = new TrialBacktestSettings
        {
            MaxBarsToFillEntry = rng.Next(opt.FillBarsMin, opt.FillBarsMax + 1),
            EntryLimitBufferPct = RandDecimal(opt.EntryBufferMin, opt.EntryBufferMax),
            TakeProfitR = SampleTp(rng, opt.TpRValues)
        };

        return new TrialSettings(scanner, backtest);

        static decimal? SampleTp(Random rng, IReadOnlyList<decimal?> values)
        {
            if (values.Count == 0) return null;
            return values[rng.Next(0, values.Count)];
        }
    }

    private static OptimizeMetrics ComputeMetrics(BacktestResult r, decimal startingEquity)
    {
        var filledTrades = r.Trades.Where(t => t.Outcome != BacktestTradeOutcome.NoFill).ToList();

        var grossProfit = filledTrades.Where(t => (t.PnlDollars ?? 0m) > 0m).Sum(t => t.PnlDollars ?? 0m);
        var grossLoss = Math.Abs(filledTrades.Where(t => (t.PnlDollars ?? 0m) < 0m).Sum(t => t.PnlDollars ?? 0m));
        var pf = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? 999m : 0m);

        var ddPct = startingEquity > 0 ? r.Summary.MaxDrawdown / startingEquity : 0m;

        return new OptimizeMetrics
        {
            Trades = r.Summary.Trades,
            FilledTrades = r.Summary.Trades - r.Summary.NoFills,
            Wins = r.Summary.Wins,
            Losses = r.Summary.Losses,
            WinRate = r.Summary.WinRate,
            TotalPnl = r.Summary.TotalPnl,
            AvgPnl = r.Summary.AvgPnl,
            AvgR = r.Summary.AvgR,
            MaxDrawdown = r.Summary.MaxDrawdown,
            MaxDrawdownPct = ddPct,
            ProfitFactor = pf
        };
    }

    private static decimal CompositeScore(OptimizeMetrics m, OptimizeOptions opt)
    {
        // Composite (tunable): favor positive expectancy and consistency, penalize drawdown.
        // - AvgR is scaled to be "human readable"
        // - ProfitFactor tends to be noisy with few trades, so MinFilledTrades gate helps.
        var score = (m.AvgR * opt.WeightAvgR)
                    + (m.ProfitFactor * opt.WeightProfitFactor)
                    - (m.MaxDrawdownPct * opt.WeightMaxDrawdownPct);

        // small penalty for too many no-fills (wastes signals)
        var noFillPct = m.Trades > 0 ? (decimal)(m.Trades - m.FilledTrades) / m.Trades : 0m;
        score -= noFillPct * opt.WeightNoFillPct;

        return score;
    }

    private static string ToCsv(IReadOnlyList<OptimizeTrialResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "trial,finalScore,trainScore,testScore," +
            "lookback,maxRangePct,minBodyToMedian,minVolToAvg,breakoutBuf,stopBuf," +
            "fillBars,entryBuf,tpR," +
            "trainFilled,trainAvgR,trainPF,trainDDPct,trainTotalPnl,trainWinRate," +
            "testFilled,testAvgR,testPF,testDDPct,testTotalPnl,testWinRate");

        foreach (var r in results)
        {
            sb.Append(r.TrialIndex).Append(',')
              .Append(r.FinalScore.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TrainScore.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TestScore.ToString(CultureInfo.InvariantCulture)).Append(',')

              .Append(r.Scanner.ConsolidationLookbackBars).Append(',')
              .Append(r.Scanner.MaxConsolidationRangePct.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Scanner.MinBodyToMedianBody.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Scanner.MinVolumeToAvgVolume.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Scanner.BreakoutBufferPct.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Scanner.StopBufferPct.ToString(CultureInfo.InvariantCulture)).Append(',')

              .Append(r.Backtest.MaxBarsToFillEntry).Append(',')
              .Append(r.Backtest.EntryLimitBufferPct.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append((r.Backtest.TakeProfitR?.ToString(CultureInfo.InvariantCulture) ?? "")).Append(',')

              .Append(r.Train.FilledTrades).Append(',')
              .Append(r.Train.AvgR.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Train.ProfitFactor.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Train.MaxDrawdownPct.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Train.TotalPnl.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Train.WinRate.ToString(CultureInfo.InvariantCulture)).Append(',')

              .Append(r.Test.FilledTrades).Append(',')
              .Append(r.Test.AvgR.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Test.ProfitFactor.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Test.MaxDrawdownPct.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Test.TotalPnl.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Test.WinRate.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }

        return sb.ToString();
    }

    private OptimizeOptions ParseArgs(string[] args)
    {
        // args: ["optimize", ...]
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
            throw new ArgumentException("Optimize requires --symbols (comma-separated). Example: --symbols SOXL,QBTS,PATH");

        if (!dict.TryGetValue("trainFrom", out var trainFromStr) || !DateOnly.TryParse(trainFromStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var trainFrom))
            throw new ArgumentException("Optimize requires --trainFrom YYYY-MM-DD");

        if (!dict.TryGetValue("trainTo", out var trainToStr) || !DateOnly.TryParse(trainToStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var trainTo))
            throw new ArgumentException("Optimize requires --trainTo YYYY-MM-DD");

        if (!dict.TryGetValue("testFrom", out var testFromStr) || !DateOnly.TryParse(testFromStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var testFrom))
            throw new ArgumentException("Optimize requires --testFrom YYYY-MM-DD");

        if (!dict.TryGetValue("testTo", out var testToStr) || !DateOnly.TryParse(testToStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var testTo))
            throw new ArgumentException("Optimize requires --testTo YYYY-MM-DD");

        var trials = dict.TryGetValue("trials", out var t) && int.TryParse(t, out var nTrials) ? Math.Max(1, nTrials) : _cfg.GetValue("Optimize:Trials", 500);
        var seed = dict.TryGetValue("seed", out var sd) && int.TryParse(sd, out var nSeed) ? nSeed : _cfg.GetValue("Optimize:Seed", 12345);
        var topN = dict.TryGetValue("top", out var tn) && int.TryParse(tn, out var nTop) ? Math.Max(1, nTop) : _cfg.GetValue("Optimize:TopN", 10);
        var outDir = dict.TryGetValue("out", out var od) && !string.IsNullOrWhiteSpace(od) ? od : _cfg.GetValue<string>("Optimize:OutDir") ?? "out_opt";
        var timeframe = dict.TryGetValue("timeframe", out var tf) && !string.IsNullOrWhiteSpace(tf) ? tf : _cfg.GetValue<string>("Optimize:Timeframe") ?? "5Min";

        var minFilled = dict.TryGetValue("minFilledTrades", out var mft) && int.TryParse(mft, out var mf) ? Math.Max(1, mf) : _cfg.GetValue("Optimize:MinFilledTrades", 20);

        // Backtest execution defaults
        var flattenStr = _cfg.GetValue<string>("Optimize:FlattenTimeEt") ?? "15:50";
        var flatten = TimeOnly.ParseExact(flattenStr, "HH:mm", CultureInfo.InvariantCulture);

        var symbols = symCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Parameter ranges: allow override from appsettings.json, but provide good defaults.
        var p = new OptimizeOptions
        {
            Symbols = symbols,
            TrainFromEt = trainFrom,
            TrainToEt = trainTo,
            TestFromEt = testFrom,
            TestToEt = testTo,
            Timeframe = timeframe,
            Trials = trials,
            Seed = seed,
            TopN = topN,
            OutDir = outDir,
            MinFilledTrades = minFilled,

            FlattenTimeEt = flatten,
            SameBarRule = SameBarFillRule.ConservativeStopFirst,
            SlippagePct = _cfg.GetValue("Backtest:SlippagePct", 0.0005m),
            CommissionPerTrade = _cfg.GetValue("Backtest:CommissionPerTrade", 0m),

            // Composite weights
            WeightAvgR = _cfg.GetValue("Optimize:Weights:AvgR", 100m),
            WeightProfitFactor = _cfg.GetValue("Optimize:Weights:ProfitFactor", 10m),
            WeightMaxDrawdownPct = _cfg.GetValue("Optimize:Weights:MaxDrawdownPct", 50m),
            WeightNoFillPct = _cfg.GetValue("Optimize:Weights:NoFillPct", 5m),

            TrainWeight = _cfg.GetValue("Optimize:TrainWeight", 0.6m),
            TestWeight = _cfg.GetValue("Optimize:TestWeight", 0.4m),

            // Scanner ranges
            LookbackMin = _cfg.GetValue("Optimize:Ranges:LookbackMin", 6),
            LookbackMax = _cfg.GetValue("Optimize:Ranges:LookbackMax", 30),
            RangePctMin = _cfg.GetValue("Optimize:Ranges:RangePctMin", 0.002m),
            RangePctMax = _cfg.GetValue("Optimize:Ranges:RangePctMax", 0.015m),
            BodyToMedianMin = _cfg.GetValue("Optimize:Ranges:BodyToMedianMin", 1.8m),
            BodyToMedianMax = _cfg.GetValue("Optimize:Ranges:BodyToMedianMax", 4.0m),
            VolRatioMin = _cfg.GetValue("Optimize:Ranges:VolRatioMin", 1.5m),
            VolRatioMax = _cfg.GetValue("Optimize:Ranges:VolRatioMax", 5.0m),
            BreakoutBufferMin = _cfg.GetValue("Optimize:Ranges:BreakoutBufferMin", 0.0m),
            BreakoutBufferMax = _cfg.GetValue("Optimize:Ranges:BreakoutBufferMax", 0.002m),
            StopBufferMin = _cfg.GetValue("Optimize:Ranges:StopBufferMin", 0.0m),
            StopBufferMax = _cfg.GetValue("Optimize:Ranges:StopBufferMax", 0.002m),

            // Entry/exits ranges
            FillBarsMin = _cfg.GetValue("Optimize:Ranges:FillBarsMin", 1),
            FillBarsMax = _cfg.GetValue("Optimize:Ranges:FillBarsMax", 12),
            EntryBufferMin = _cfg.GetValue("Optimize:Ranges:EntryBufferMin", 0.0m),
            EntryBufferMax = _cfg.GetValue("Optimize:Ranges:EntryBufferMax", 0.0025m),
            TpRValues = ParseTpValues(_cfg.GetValue<string>("Optimize:Ranges:TpRValues") ?? "null,1.0,1.5,2.0,2.5,3.0")
        };

        // Keep indicator-based requirements as fixed knobs in config (not searched by default)
        p.RequireNearEma20 = _cfg.GetValue("Optimize:Fixed:RequireNearEma20", false);
        p.MaxDistanceFromEma20Pct = _cfg.GetValue("Optimize:Fixed:MaxDistanceFromEma20Pct", 0.010m);
        p.RequireEma20NearEma200 = _cfg.GetValue("Optimize:Fixed:RequireEma20NearEma200", false);
        p.MaxEmaDistancePct = _cfg.GetValue("Optimize:Fixed:MaxEmaDistancePct", 0.010m);
        p.RequireAtrAvailable = _cfg.GetValue("Optimize:Fixed:RequireAtrAvailable", true);
        p.MinAtrPct = _cfg.GetValue("Optimize:Fixed:MinAtrPct", 0.001m);
        p.MaxAtrPct = _cfg.GetValue("Optimize:Fixed:MaxAtrPct", 0.030m);

        return p;

        static IReadOnlyList<decimal?> ParseTpValues(string csv)
        {
            var vals = new List<decimal?>();
            foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Equals("null", StringComparison.OrdinalIgnoreCase) || token.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    vals.Add(null);
                    continue;
                }
                if (decimal.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    vals.Add(d);
            }
            return vals;
        }
    }
}

public sealed record OptimizeOptions
{
    public required IReadOnlyList<string> Symbols { get; init; }
    public required DateOnly TrainFromEt { get; init; }
    public required DateOnly TrainToEt { get; init; }
    public required DateOnly TestFromEt { get; init; }
    public required DateOnly TestToEt { get; init; }
    public string Timeframe { get; init; } = "5Min";

    public int Trials { get; init; } = 500;
    public int Seed { get; init; } = 12345;
    public int TopN { get; init; } = 10;
    public string OutDir { get; init; } = "out_opt";
    public int MinFilledTrades { get; init; } = 20;

    public TimeOnly FlattenTimeEt { get; init; } = new(15, 50);
    public SameBarFillRule SameBarRule { get; init; } = SameBarFillRule.ConservativeStopFirst;
    public decimal SlippagePct { get; init; } = 0.0005m;
    public decimal CommissionPerTrade { get; init; } = 0m;

    // composite weights
    public decimal WeightAvgR { get; init; } = 100m;
    public decimal WeightProfitFactor { get; init; } = 10m;
    public decimal WeightMaxDrawdownPct { get; init; } = 50m;
    public decimal WeightNoFillPct { get; init; } = 5m;
    public decimal TrainWeight { get; init; } = 0.6m;
    public decimal TestWeight { get; init; } = 0.4m;

    // Scanner ranges
    public int LookbackMin { get; init; } = 6;
    public int LookbackMax { get; init; } = 30;
    public decimal RangePctMin { get; init; } = 0.002m;
    public decimal RangePctMax { get; init; } = 0.015m;
    public decimal BodyToMedianMin { get; init; } = 1.8m;
    public decimal BodyToMedianMax { get; init; } = 4.0m;
    public decimal VolRatioMin { get; init; } = 1.5m;
    public decimal VolRatioMax { get; init; } = 5.0m;
    public decimal BreakoutBufferMin { get; init; } = 0.0m;
    public decimal BreakoutBufferMax { get; init; } = 0.002m;
    public decimal StopBufferMin { get; init; } = 0.0m;
    public decimal StopBufferMax { get; init; } = 0.002m;

    // Entry/exit ranges
    public int FillBarsMin { get; init; } = 1;
    public int FillBarsMax { get; init; } = 12;
    public decimal EntryBufferMin { get; init; } = 0.0m;
    public decimal EntryBufferMax { get; init; } = 0.0025m;
    public required IReadOnlyList<decimal?> TpRValues { get; init; }

    // Fixed indicator requirements (not searched by default)
    public bool RequireNearEma20 { get; set; }
    public decimal MaxDistanceFromEma20Pct { get; set; }
    public bool RequireEma20NearEma200 { get; set; }
    public decimal MaxEmaDistancePct { get; set; }
    public bool RequireAtrAvailable { get; set; }
    public decimal MinAtrPct { get; set; }
    public decimal MaxAtrPct { get; set; }

}

public sealed record TrialSettings(TraidrScannerOptions Scanner, TrialBacktestSettings Backtest);

public sealed record TrialBacktestSettings
{
    public int MaxBarsToFillEntry { get; init; }
    public decimal EntryLimitBufferPct { get; init; }
    public decimal? TakeProfitR { get; init; }
}

public sealed record OptimizeMetrics
{
    public int Trades { get; init; }
    public int FilledTrades { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public decimal WinRate { get; init; }
    public decimal TotalPnl { get; init; }
    public decimal AvgPnl { get; init; }
    public decimal AvgR { get; init; }
    public decimal MaxDrawdown { get; init; }
    public decimal MaxDrawdownPct { get; init; }
    public decimal ProfitFactor { get; init; }
}

public sealed record OptimizeTrialResult
{
    public int TrialIndex { get; init; }
    public required TraidrScannerOptions Scanner { get; init; }
    public required TrialBacktestSettings Backtest { get; init; }
    public required OptimizeMetrics Train { get; init; }
    public required OptimizeMetrics Test { get; init; }
    public decimal TrainScore { get; init; }
    public decimal TestScore { get; init; }
    public decimal FinalScore { get; init; }
}
