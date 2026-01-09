using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Traidr.Core.Backtesting;
using Traidr.Core.MarketData;
using Traidr.Core.Scanning;
using Traidr.Core.Trading;

namespace Traidr.Cli;

public sealed class OptimizeCommand
{
    private readonly ILogger<OptimizeCommand> _log;
    private readonly IConfiguration _cfg;
    private readonly IMarketDataClient _marketData;
    private readonly RiskManagerOptions _riskOptions;
    private readonly IStrategyScannerFactory _scannerFactory;

    public OptimizeCommand(
        ILogger<OptimizeCommand> log,
        IConfiguration cfg,
        IMarketDataClient marketData,
        RiskManagerOptions riskOptions,
        IStrategyScannerFactory scannerFactory)
    {
        _log = log;
        _cfg = cfg;
        _marketData = marketData;
        _riskOptions = riskOptions;
        _scannerFactory = scannerFactory;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var opt = ParseArgs(args);

        _log.LogInformation(
            "Optimize strategy={Strategy} session={Session} trials={Trials} symbols={Symbols} train={TrainFrom}..{TrainTo} test={TestFrom}..{TestTo}",
            opt.Strategy,
            opt.SessionMode,
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

            var trial = opt.Strategy switch
            {
                TradingStrategy.CameronRoss => SampleCameronTrial(rng, opt),
                TradingStrategy.Emmanuel => SampleEmmanuelTrial(rng, opt),
                _ => SampleOliverTrial(rng, opt)
            };

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
                Strategy = trial.Strategy,
                OliverScanner = trial.OliverScanner,
                CameronScanner = trial.CameronScanner,
                EmmanuelScanner = trial.EmmanuelScanner,
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
            if (r.Strategy == TradingStrategy.CameronRoss && r.CameronScanner is not null)
            {
                var s = r.CameronScanner;
                _log.LogPink(
                    "#{Idx} score={Score:F2} trainR={TrainR:F3} trainPF={TrainPF:F2} trainDD={TrainDD:P2} testR={TestR:F3} testPF={TestPF:F2} testDD={TestDD:P2} | gap={Gap:P0} gain={Gain:P0} rvol={Rvol:F1} pullback={Pb:P1} stop=${Stop:F2} fillBars={FillBars} entryBuf={EntryBuf:P2} tpR={TpR}",
                    r.TrialIndex,
                    r.FinalScore,
                    r.Train.AvgR,
                    r.Train.ProfitFactor,
                    r.Train.MaxDrawdownPct,
                    r.Test.AvgR,
                    r.Test.ProfitFactor,
                    r.Test.MaxDrawdownPct,
                    s.MinGapPct,
                    s.MinDayGainPct,
                    s.MinRvol,
                    s.MaxPullbackPct,
                    s.StopCents,
                    r.Backtest.MaxBarsToFillEntry,
                    r.Backtest.EntryLimitBufferPct,
                    r.Backtest.TakeProfitR?.ToString(CultureInfo.InvariantCulture) ?? "null");
                continue;
            }

            if (r.Strategy == TradingStrategy.Emmanuel && r.EmmanuelScanner is not null)
            {
                var s = r.EmmanuelScanner;
                _log.LogPink(
                    "#{Idx} score={Score:F2} trainR={TrainR:F3} trainPF={TrainPF:F2} trainDD={TrainDD:P2} testR={TestR:F3} testPF={TestPF:F2} testDD={TestDD:P2} | gap={Gap:P0} pmVol={PmVol:N0} rvol={Rvol:F1} pole={Pole:P0} flag={Flag:P0} stopBuf=${Stop:F2} fillBars={FillBars} entryBuf={EntryBuf:P2} tpR={TpR}",
                    r.TrialIndex,
                    r.FinalScore,
                    r.Train.AvgR,
                    r.Train.ProfitFactor,
                    r.Train.MaxDrawdownPct,
                    r.Test.AvgR,
                    r.Test.ProfitFactor,
                    r.Test.MaxDrawdownPct,
                    s.MinGapPct,
                    s.MinPremarketVolume,
                    s.MinRvol,
                    s.MinPolePct,
                    s.MaxFlagRetracePct,
                    s.StopBufferCents,
                    r.Backtest.MaxBarsToFillEntry,
                    r.Backtest.EntryLimitBufferPct,
                    r.Backtest.TakeProfitR?.ToString(CultureInfo.InvariantCulture) ?? "null");
                continue;
            }

            if (r.OliverScanner is not null)
            {
                var s = r.OliverScanner;
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
                    s.ConsolidationLookbackBars,
                    s.MaxConsolidationRangePct,
                    s.MinBodyToMedianBody,
                    s.MinVolumeToAvgVolume,
                    r.Backtest.MaxBarsToFillEntry,
                    r.Backtest.EntryLimitBufferPct,
                    r.Backtest.TakeProfitR?.ToString(CultureInfo.InvariantCulture) ?? "null");
            }
        }

        return 0;
    }

    private BacktestResult RunOnce(BacktestDataSet data, OptimizeOptions opt, TrialSettings trial, DateOnly fromEt, DateOnly toEt)
    {
        // Fresh state per run
        var riskState = new InMemoryRiskState();
        var risk = new RiskManager(riskState, _riskOptions);
        var scanner = _scannerFactory.Create(trial.Strategy, trial.OliverScanner, trial.CameronScanner, trial.EmmanuelScanner);

        var runOpt = new BacktestOptions
        {
            Symbols = opt.Symbols,
            FromDateEt = fromEt,
            ToDateEt = toEt,
            Timeframe = opt.Timeframe,
            MaxBarsToFillEntry = trial.Backtest.MaxBarsToFillEntry,
            EntryLimitBufferPct = trial.Backtest.EntryLimitBufferPct,
            FlattenTimeEt = opt.FlattenTimeEt,
            SessionMode = opt.SessionMode,
            SessionHours = opt.SessionHours,
            SameBarRule = opt.SameBarRule,
            SlippagePct = opt.SlippagePct,
            CommissionPerTrade = opt.CommissionPerTrade,
            TakeProfitR = trial.Backtest.TakeProfitR
        };

        return BacktestSimulator.Run(data, runOpt, scanner, risk);
    }

    static decimal? SampleTp(Random rng, IReadOnlyList<decimal?> values)
    {
        if (values.Count == 0) return null;
        return values[rng.Next(0, values.Count)];
    }

    private static TrialSettings SampleOliverTrial(Random rng, OptimizeOptions opt)
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
            RequirePriceAboveEma20 = opt.RequirePriceAboveEma20,
            RequirePriceAboveEma200 = opt.RequirePriceAboveEma200,
            RequireTrendEma20OverEma200 = opt.RequireTrendEma20OverEma200,
            RequirePriceAboveVwap = opt.RequirePriceAboveVwap,
            RequireEma20NearEma200 = opt.RequireEma20NearEma200,
            MaxEmaDistancePct = opt.MaxEmaDistancePct,
            RequireAtrAvailable = opt.RequireAtrAvailable,
            MinAtrPct = opt.MinAtrPct,
            MaxAtrPct = opt.MaxAtrPct,
            MinCloseInRangeForLong = opt.MinCloseInRangeForLong,
            MaxCloseInRangeForShort = opt.MaxCloseInRangeForShort
        };

        var backtest = new TrialBacktestSettings
        {
            MaxBarsToFillEntry = rng.Next(opt.FillBarsMin, opt.FillBarsMax + 1),
            EntryLimitBufferPct = RandDecimal(opt.EntryBufferMin, opt.EntryBufferMax),
            TakeProfitR = SampleTp(rng, opt.TpRValues)
        };

        return new TrialSettings(
            Strategy: TradingStrategy.Oliver,
            OliverScanner: scanner,
            CameronScanner: null,
            EmmanuelScanner: null,
            Backtest: backtest);
    }

    private static TrialSettings SampleCameronTrial(Random rng, OptimizeOptions opt)
    {
        decimal RandDecimal(decimal min, decimal max)
        {
            var u = (decimal)rng.NextDouble();
            return min + (u * (max - min));
        }

        var scanner = new CameronRossScannerOptions
        {
            MinPrice = RandDecimal(opt.CamMinPriceMin, opt.CamMinPriceMax),
            MaxPrice = RandDecimal(opt.CamMaxPriceMin, opt.CamMaxPriceMax),
            MinGapPct = RandDecimal(opt.CamMinGapPctMin, opt.CamMinGapPctMax),
            MinDayGainPct = RandDecimal(opt.CamMinDayGainPctMin, opt.CamMinDayGainPctMax),
            RequireRvol = opt.CamRequireRvol,
            RvolLookbackDays = rng.Next(opt.CamRvolLookbackMin, opt.CamRvolLookbackMax + 1),
            MinRvol = RandDecimal(opt.CamMinRvolMin, opt.CamMinRvolMax),
            RequireNews = opt.CamRequireNews,
            RequireLowFloat = opt.CamRequireLowFloat,
            MaxFloatShares = opt.CamMaxFloatShares,
            RequireShortInterest = opt.CamRequireShortInterest,
            MinShortInterestPct = opt.CamMinShortInterestPct,
            AllowShorts = opt.CamAllowShorts,
            StartTimeEt = opt.CamStartTimeEt,
            EndTimeEt = opt.CamEndTimeEt,
            RequireMicroPullback = opt.CamRequireMicroPullback,
            PullbackBars = rng.Next(opt.CamPullbackBarsMin, opt.CamPullbackBarsMax + 1),
            MaxPullbackPct = RandDecimal(opt.CamMaxPullbackPctMin, opt.CamMaxPullbackPctMax),
            RequireRoundBreak = opt.CamRequireRoundBreak,
            RoundIncrement = opt.CamRoundIncrement,
            RoundBreakMaxDistance = RandDecimal(opt.CamRoundBreakMaxDistanceMin, opt.CamRoundBreakMaxDistanceMax),
            UseFixedStopCents = opt.CamUseFixedStopCents,
            EnableDailyHistoryFallback = opt.CamEnableDailyHistoryFallback,
            DailyHistoryLookbackDays = opt.CamDailyHistoryLookbackDays,
            StopCents = RandDecimal(opt.CamStopCentsMin, opt.CamStopCentsMax),
            StopBufferPct = RandDecimal(opt.CamStopBufferPctMin, opt.CamStopBufferPctMax)
        };

        var backtest = new TrialBacktestSettings
        {
            MaxBarsToFillEntry = rng.Next(opt.FillBarsMin, opt.FillBarsMax + 1),
            EntryLimitBufferPct = RandDecimal(opt.EntryBufferMin, opt.EntryBufferMax),
            TakeProfitR = SampleTp(rng, opt.TpRValues)
        };

        return new TrialSettings(
            Strategy: TradingStrategy.CameronRoss,
            OliverScanner: null,
            CameronScanner: scanner,
            EmmanuelScanner: null,
            Backtest: backtest);
    }

    private static TrialSettings SampleEmmanuelTrial(Random rng, OptimizeOptions opt)
    {
        decimal RandDecimal(decimal min, decimal max)
        {
            var u = (decimal)rng.NextDouble();
            return min + (u * (max - min));
        }

        var scanner = new EmmanuelScannerOptions
        {
            MinPrice = RandDecimal(opt.EmMinPriceMin, opt.EmMinPriceMax),
            MaxPrice = RandDecimal(opt.EmMaxPriceMin, opt.EmMaxPriceMax),
            MinGapPct = RandDecimal(opt.EmMinGapPctMin, opt.EmMinGapPctMax),
            MinPremarketVolume = rng.Next(opt.EmPremarketVolumeMin, opt.EmPremarketVolumeMax + 1),
            PremarketStartEt = opt.EmPremarketStartEt,
            PremarketEndEt = opt.EmPremarketEndEt,
            RequireLowFloat = opt.EmRequireLowFloat,
            MaxFloatShares = opt.EmMaxFloatShares,
            RequireRvol = opt.EmRequireRvol,
            RvolLookbackDays = rng.Next(opt.EmRvolLookbackMin, opt.EmRvolLookbackMax + 1),
            MinRvol = RandDecimal(opt.EmMinRvolMin, opt.EmMinRvolMax),
            RequirePriceAboveVwap = opt.EmRequirePriceAboveVwap,
            RequireVwapSlopeUp = opt.EmRequireVwapSlopeUp,
            VwapSlopeBars = rng.Next(opt.EmVwapSlopeBarsMin, opt.EmVwapSlopeBarsMax + 1),
            RequireTightSpread = opt.EmRequireTightSpread,
            MaxSpreadCents = RandDecimal(opt.EmMaxSpreadCentsMin, opt.EmMaxSpreadCentsMax),
            UseBarRangeAsSpreadProxy = opt.EmUseBarRangeAsSpreadProxy,
            MaxBarRangePctOfAtr = RandDecimal(opt.EmMaxBarRangePctAtrMin, opt.EmMaxBarRangePctAtrMax),
            RequireEma9Hook = opt.EmRequireEma9Hook,
            RequireEma9AboveEma20 = opt.EmRequireEma9AboveEma20,
            RequireBullFlag = opt.EmRequireBullFlag,
            PoleLookbackBars = rng.Next(opt.EmPoleLookbackMin, opt.EmPoleLookbackMax + 1),
            MinPolePct = RandDecimal(opt.EmMinPolePctMin, opt.EmMinPolePctMax),
            FlagBars = rng.Next(opt.EmFlagBarsMin, opt.EmFlagBarsMax + 1),
            MaxFlagRetracePct = RandDecimal(opt.EmMaxFlagRetracePctMin, opt.EmMaxFlagRetracePctMax),
            RequireLowerFlagVolume = opt.EmRequireLowerFlagVolume,
            EntryBufferCents = RandDecimal(opt.EmEntryBufferCentsMin, opt.EmEntryBufferCentsMax),
            StopBufferCents = RandDecimal(opt.EmStopBufferCentsMin, opt.EmStopBufferCentsMax)
        };

        var backtest = new TrialBacktestSettings
        {
            MaxBarsToFillEntry = rng.Next(opt.FillBarsMin, opt.FillBarsMax + 1),
            EntryLimitBufferPct = RandDecimal(opt.EntryBufferMin, opt.EntryBufferMax),
            TakeProfitR = SampleTp(rng, opt.TpRValues)
        };

        return new TrialSettings(
            Strategy: TradingStrategy.Emmanuel,
            OliverScanner: null,
            CameronScanner: null,
            EmmanuelScanner: scanner,
            Backtest: backtest);
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
        var winRateAdj = m.WinRate - opt.MinWinRate;
        var score = (m.AvgR * opt.WeightAvgR)
                    + (m.ProfitFactor * opt.WeightProfitFactor)
                    + (winRateAdj * opt.WeightWinRate)
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
            "trial,strategy,finalScore,trainScore,testScore," +
            "lookback,maxRangePct,minBodyToMedian,minVolToAvg,breakoutBuf,stopBuf," +
            "camMinPrice,camMaxPrice,camMinGapPct,camMinDayGainPct,camMinRvol,camPullbackBars,camMaxPullbackPct,camStopCents,camRoundBreakDist," +
            "emMinPrice,emMaxPrice,emMinGapPct,emMinPremarketVol,emMinRvol,emPoleBars,emMinPolePct,emFlagBars,emMaxFlagRetrace,emEntryBufCents,emStopBufCents," +
            "fillBars,entryBuf,tpR," +
            "trainFilled,trainAvgR,trainPF,trainDDPct,trainTotalPnl,trainWinRate," +
            "testFilled,testAvgR,testPF,testDDPct,testTotalPnl,testWinRate");

        foreach (var r in results)
        {
            var oliver = r.OliverScanner;
            var cam = r.CameronScanner;
            var em = r.EmmanuelScanner;

            sb.Append(r.TrialIndex).Append(',')
              .Append(r.Strategy).Append(',')
              .Append(r.FinalScore.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TrainScore.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.TestScore.ToString(CultureInfo.InvariantCulture)).Append(',')

              .Append(oliver?.ConsolidationLookbackBars.ToString() ?? "").Append(',')
              .Append(oliver?.MaxConsolidationRangePct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(oliver?.MinBodyToMedianBody.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(oliver?.MinVolumeToAvgVolume.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(oliver?.BreakoutBufferPct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(oliver?.StopBufferPct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')

              .Append(cam?.MinPrice.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(cam?.MaxPrice.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(cam?.MinGapPct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(cam?.MinDayGainPct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(cam?.MinRvol.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(cam?.PullbackBars.ToString() ?? "").Append(',')
              .Append(cam?.MaxPullbackPct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(cam?.StopCents.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(cam?.RoundBreakMaxDistance.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')

              .Append(em?.MinPrice.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.MaxPrice.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.MinGapPct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.MinPremarketVolume.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.MinRvol.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.PoleLookbackBars.ToString() ?? "").Append(',')
              .Append(em?.MinPolePct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.FlagBars.ToString() ?? "").Append(',')
              .Append(em?.MaxFlagRetracePct.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.EntryBufferCents.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(em?.StopBufferCents.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')

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
        var strategy = ParseStrategy(dict.TryGetValue("strategy", out var st) ? st : null);
        var sessionMode = ParseSessionMode(dict.TryGetValue("session", out var sm) ? sm : null);
        var sessionHours = _cfg.GetSection("MarketSessions").Get<MarketSessionHours>() ?? new MarketSessionHours();

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
            Strategy = strategy,
            SessionMode = sessionMode,
            SessionHours = sessionHours,

            FlattenTimeEt = flatten,
            SameBarRule = SameBarFillRule.ConservativeStopFirst,
            SlippagePct = _cfg.GetValue("Backtest:SlippagePct", 0.0005m),
            CommissionPerTrade = _cfg.GetValue("Backtest:CommissionPerTrade", 0m),

            // Composite weights
            WeightAvgR = _cfg.GetValue("Optimize:Weights:AvgR", 100m),
            WeightProfitFactor = _cfg.GetValue("Optimize:Weights:ProfitFactor", 10m),
            WeightMaxDrawdownPct = _cfg.GetValue("Optimize:Weights:MaxDrawdownPct", 50m),
            WeightNoFillPct = _cfg.GetValue("Optimize:Weights:NoFillPct", 5m),
            WeightWinRate = _cfg.GetValue("Optimize:Weights:WinRate", 100m),
            MinWinRate = _cfg.GetValue("Optimize:MinWinRate", 0.5m),

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
            TpRValues = ParseTpValues(_cfg.GetValue<string>("Optimize:Ranges:TpRValues") ?? "1.5,2.0,2.5") // "null,1.0,1.5,2.0,2.5") // ,3.0")
        };

        // Keep indicator-based requirements as fixed knobs in config (not searched by default)
        p.RequireNearEma20 = _cfg.GetValue("Optimize:Fixed:RequireNearEma20", false);
        p.MaxDistanceFromEma20Pct = _cfg.GetValue("Optimize:Fixed:MaxDistanceFromEma20Pct", 0.010m);
        p.RequirePriceAboveEma20 = _cfg.GetValue("Optimize:Fixed:RequirePriceAboveEma20", false);
        p.RequirePriceAboveEma200 = _cfg.GetValue("Optimize:Fixed:RequirePriceAboveEma200", false);
        p.RequireTrendEma20OverEma200 = _cfg.GetValue("Optimize:Fixed:RequireTrendEma20OverEma200", false);
        p.RequirePriceAboveVwap = _cfg.GetValue("Optimize:Fixed:RequirePriceAboveVwap", false);

        p.RequireEma20NearEma200 = _cfg.GetValue("Optimize:Fixed:RequireEma20NearEma200", false);
        p.MaxEmaDistancePct = _cfg.GetValue("Optimize:Fixed:MaxEmaDistancePct", 0.010m);
        p.RequireAtrAvailable = _cfg.GetValue("Optimize:Fixed:RequireAtrAvailable", true);
        p.MinAtrPct = _cfg.GetValue("Optimize:Fixed:MinAtrPct", 0.001m);
        p.MaxAtrPct = _cfg.GetValue("Optimize:Fixed:MaxAtrPct", 0.030m);
        p.MinCloseInRangeForLong = _cfg.GetValue("Optimize:Fixed:MinCloseInRangeForLong", 0.0m);
        p.MaxCloseInRangeForShort = _cfg.GetValue("Optimize:Fixed:MaxCloseInRangeForShort", 1.0m);

        // Cameron Ross ranges
        p.CamMinPriceMin = _cfg.GetValue("Optimize:CameronRanges:MinPriceMin", 2.0m);
        p.CamMinPriceMax = _cfg.GetValue("Optimize:CameronRanges:MinPriceMax", 5.0m);
        p.CamMaxPriceMin = _cfg.GetValue("Optimize:CameronRanges:MaxPriceMin", 10.0m);
        p.CamMaxPriceMax = _cfg.GetValue("Optimize:CameronRanges:MaxPriceMax", 20.0m);
        p.CamMinGapPctMin = _cfg.GetValue("Optimize:CameronRanges:MinGapPctMin", 0.08m);
        p.CamMinGapPctMax = _cfg.GetValue("Optimize:CameronRanges:MinGapPctMax", 0.25m);
        p.CamMinDayGainPctMin = _cfg.GetValue("Optimize:CameronRanges:MinDayGainPctMin", 0.08m);
        p.CamMinDayGainPctMax = _cfg.GetValue("Optimize:CameronRanges:MinDayGainPctMax", 0.30m);
        p.CamMinRvolMin = _cfg.GetValue("Optimize:CameronRanges:MinRvolMin", 3.0m);
        p.CamMinRvolMax = _cfg.GetValue("Optimize:CameronRanges:MinRvolMax", 10.0m);
        p.CamRvolLookbackMin = _cfg.GetValue("Optimize:CameronRanges:RvolLookbackMin", 10);
        p.CamRvolLookbackMax = _cfg.GetValue("Optimize:CameronRanges:RvolLookbackMax", 30);
        p.CamPullbackBarsMin = _cfg.GetValue("Optimize:CameronRanges:PullbackBarsMin", 2);
        p.CamPullbackBarsMax = _cfg.GetValue("Optimize:CameronRanges:PullbackBarsMax", 5);
        p.CamMaxPullbackPctMin = _cfg.GetValue("Optimize:CameronRanges:MaxPullbackPctMin", 0.01m);
        p.CamMaxPullbackPctMax = _cfg.GetValue("Optimize:CameronRanges:MaxPullbackPctMax", 0.05m);
        p.CamStopCentsMin = _cfg.GetValue("Optimize:CameronRanges:StopCentsMin", 0.08m);
        p.CamStopCentsMax = _cfg.GetValue("Optimize:CameronRanges:StopCentsMax", 0.20m);
        p.CamStopBufferPctMin = _cfg.GetValue("Optimize:CameronRanges:StopBufferPctMin", 0.0m);
        p.CamStopBufferPctMax = _cfg.GetValue("Optimize:CameronRanges:StopBufferPctMax", 0.002m);
        p.CamRoundBreakMaxDistanceMin = _cfg.GetValue("Optimize:CameronRanges:RoundBreakMaxDistanceMin", 0.02m);
        p.CamRoundBreakMaxDistanceMax = _cfg.GetValue("Optimize:CameronRanges:RoundBreakMaxDistanceMax", 0.08m);

        // Cameron Ross fixed settings
        p.CamRequireRvol = _cfg.GetValue("Optimize:CameronFixed:RequireRvol", true);
        p.CamRequireNews = _cfg.GetValue("Optimize:CameronFixed:RequireNews", false);
        p.CamRequireLowFloat = _cfg.GetValue("Optimize:CameronFixed:RequireLowFloat", false);
        p.CamMaxFloatShares = _cfg.GetValue("Optimize:CameronFixed:MaxFloatShares", 20_000_000L);
        p.CamRequireShortInterest = _cfg.GetValue("Optimize:CameronFixed:RequireShortInterest", false);
        p.CamMinShortInterestPct = _cfg.GetValue("Optimize:CameronFixed:MinShortInterestPct", 0.0m);
        p.CamAllowShorts = _cfg.GetValue("Optimize:CameronFixed:AllowShorts", false);
        p.CamRequireMicroPullback = _cfg.GetValue("Optimize:CameronFixed:RequireMicroPullback", true);
        p.CamRequireRoundBreak = _cfg.GetValue("Optimize:CameronFixed:RequireRoundBreak", false);
        p.CamRoundIncrement = _cfg.GetValue("Optimize:CameronFixed:RoundIncrement", 0.5m);
        p.CamUseFixedStopCents = _cfg.GetValue("Optimize:CameronFixed:UseFixedStopCents", true);
        p.CamEnableDailyHistoryFallback = _cfg.GetValue("Optimize:CameronFixed:EnableDailyHistoryFallback", false);
        p.CamDailyHistoryLookbackDays = _cfg.GetValue("Optimize:CameronFixed:DailyHistoryLookbackDays", 30);
        p.CamStartTimeEt = TimeSpan.Parse(
            _cfg.GetValue<string>("Optimize:CameronFixed:StartTimeEt") ?? "07:00",
            CultureInfo.InvariantCulture);
        p.CamEndTimeEt = TimeSpan.Parse(
            _cfg.GetValue<string>("Optimize:CameronFixed:EndTimeEt") ?? "10:30",
            CultureInfo.InvariantCulture);

        // Emmanuel ranges
        p.EmMinPriceMin = _cfg.GetValue("Optimize:EmmanuelRanges:MinPriceMin", 1.0m);
        p.EmMinPriceMax = _cfg.GetValue("Optimize:EmmanuelRanges:MinPriceMax", 5.0m);
        p.EmMaxPriceMin = _cfg.GetValue("Optimize:EmmanuelRanges:MaxPriceMin", 10.0m);
        p.EmMaxPriceMax = _cfg.GetValue("Optimize:EmmanuelRanges:MaxPriceMax", 20.0m);
        p.EmMinGapPctMin = _cfg.GetValue("Optimize:EmmanuelRanges:MinGapPctMin", 0.04m);
        p.EmMinGapPctMax = _cfg.GetValue("Optimize:EmmanuelRanges:MinGapPctMax", 0.15m);
        p.EmPremarketVolumeMin = _cfg.GetValue("Optimize:EmmanuelRanges:PremarketVolumeMin", 100_000);
        p.EmPremarketVolumeMax = _cfg.GetValue("Optimize:EmmanuelRanges:PremarketVolumeMax", 500_000);
        p.EmMinRvolMin = _cfg.GetValue("Optimize:EmmanuelRanges:MinRvolMin", 2.0m);
        p.EmMinRvolMax = _cfg.GetValue("Optimize:EmmanuelRanges:MinRvolMax", 8.0m);
        p.EmRvolLookbackMin = _cfg.GetValue("Optimize:EmmanuelRanges:RvolLookbackMin", 5);
        p.EmRvolLookbackMax = _cfg.GetValue("Optimize:EmmanuelRanges:RvolLookbackMax", 15);
        p.EmVwapSlopeBarsMin = _cfg.GetValue("Optimize:EmmanuelRanges:VwapSlopeBarsMin", 3);
        p.EmVwapSlopeBarsMax = _cfg.GetValue("Optimize:EmmanuelRanges:VwapSlopeBarsMax", 8);
        p.EmMaxSpreadCentsMin = _cfg.GetValue("Optimize:EmmanuelRanges:MaxSpreadCentsMin", 0.01m);
        p.EmMaxSpreadCentsMax = _cfg.GetValue("Optimize:EmmanuelRanges:MaxSpreadCentsMax", 0.03m);
        p.EmMaxBarRangePctAtrMin = _cfg.GetValue("Optimize:EmmanuelRanges:MaxBarRangePctAtrMin", 0.15m);
        p.EmMaxBarRangePctAtrMax = _cfg.GetValue("Optimize:EmmanuelRanges:MaxBarRangePctAtrMax", 0.35m);
        p.EmPoleLookbackMin = _cfg.GetValue("Optimize:EmmanuelRanges:PoleLookbackMin", 6);
        p.EmPoleLookbackMax = _cfg.GetValue("Optimize:EmmanuelRanges:PoleLookbackMax", 12);
        p.EmMinPolePctMin = _cfg.GetValue("Optimize:EmmanuelRanges:MinPolePctMin", 0.04m);
        p.EmMinPolePctMax = _cfg.GetValue("Optimize:EmmanuelRanges:MinPolePctMax", 0.12m);
        p.EmFlagBarsMin = _cfg.GetValue("Optimize:EmmanuelRanges:FlagBarsMin", 2);
        p.EmFlagBarsMax = _cfg.GetValue("Optimize:EmmanuelRanges:FlagBarsMax", 5);
        p.EmMaxFlagRetracePctMin = _cfg.GetValue("Optimize:EmmanuelRanges:MaxFlagRetracePctMin", 0.20m);
        p.EmMaxFlagRetracePctMax = _cfg.GetValue("Optimize:EmmanuelRanges:MaxFlagRetracePctMax", 0.45m);
        p.EmEntryBufferCentsMin = _cfg.GetValue("Optimize:EmmanuelRanges:EntryBufferCentsMin", 0.01m);
        p.EmEntryBufferCentsMax = _cfg.GetValue("Optimize:EmmanuelRanges:EntryBufferCentsMax", 0.03m);
        p.EmStopBufferCentsMin = _cfg.GetValue("Optimize:EmmanuelRanges:StopBufferCentsMin", 0.00m);
        p.EmStopBufferCentsMax = _cfg.GetValue("Optimize:EmmanuelRanges:StopBufferCentsMax", 0.03m);

        // Emmanuel fixed settings
        p.EmRequireLowFloat = _cfg.GetValue("Optimize:EmmanuelFixed:RequireLowFloat", false);
        p.EmMaxFloatShares = _cfg.GetValue("Optimize:EmmanuelFixed:MaxFloatShares", 20_000_000L);
        p.EmRequireRvol = _cfg.GetValue("Optimize:EmmanuelFixed:RequireRvol", true);
        p.EmRequirePriceAboveVwap = _cfg.GetValue("Optimize:EmmanuelFixed:RequirePriceAboveVwap", true);
        p.EmRequireVwapSlopeUp = _cfg.GetValue("Optimize:EmmanuelFixed:RequireVwapSlopeUp", true);
        p.EmRequireTightSpread = _cfg.GetValue("Optimize:EmmanuelFixed:RequireTightSpread", true);
        p.EmUseBarRangeAsSpreadProxy = _cfg.GetValue("Optimize:EmmanuelFixed:UseBarRangeAsSpreadProxy", true);
        p.EmRequireEma9Hook = _cfg.GetValue("Optimize:EmmanuelFixed:RequireEma9Hook", true);
        p.EmRequireEma9AboveEma20 = _cfg.GetValue("Optimize:EmmanuelFixed:RequireEma9AboveEma20", false);
        p.EmRequireBullFlag = _cfg.GetValue("Optimize:EmmanuelFixed:RequireBullFlag", true);
        p.EmRequireLowerFlagVolume = _cfg.GetValue("Optimize:EmmanuelFixed:RequireLowerFlagVolume", true);
        p.EmPremarketStartEt = TimeSpan.Parse(
            _cfg.GetValue<string>("Optimize:EmmanuelFixed:PremarketStartEt") ?? "04:00",
            CultureInfo.InvariantCulture);
        p.EmPremarketEndEt = TimeSpan.Parse(
            _cfg.GetValue<string>("Optimize:EmmanuelFixed:PremarketEndEt") ?? "09:00",
            CultureInfo.InvariantCulture);

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

    private TradingStrategy ParseStrategy(string? value)
    {
        var fallback = _cfg.GetValue<string>("Optimize:Strategy") ?? _cfg.GetValue<string>("Strategy:Default") ?? "Oliver";
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return Enum.TryParse<TradingStrategy>(raw, ignoreCase: true, out var strategy)
            ? strategy
            : TradingStrategy.Oliver;
    }

    private MarketSessionMode ParseSessionMode(string? value)
    {
        var fallback = _cfg.GetValue<string>("Optimize:SessionMode")
                       ?? _cfg.GetValue<string>("Execution:SessionMode")
                       ?? "Auto";
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return Enum.TryParse<MarketSessionMode>(raw, ignoreCase: true, out var mode)
            ? mode
            : MarketSessionMode.Auto;
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
    public TradingStrategy Strategy { get; init; } = TradingStrategy.Oliver;
    public MarketSessionMode SessionMode { get; init; } = MarketSessionMode.Auto;
    public MarketSessionHours SessionHours { get; init; } = new();

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
    public decimal WeightWinRate { get; init; } = 100m;
    public decimal MinWinRate { get; init; } = 0.5m;
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
    public bool RequirePriceAboveEma20 { get; set; }
    public bool RequirePriceAboveEma200 { get; set; }
    public bool RequireTrendEma20OverEma200 { get; set; }
    public bool RequirePriceAboveVwap { get; set; }
    public bool RequireEma20NearEma200 { get; set; }
    public decimal MaxEmaDistancePct { get; set; }
    public bool RequireAtrAvailable { get; set; }
    public decimal MinAtrPct { get; set; }
    public decimal MaxAtrPct { get; set; }
    public decimal MinCloseInRangeForLong { get; set; }
    public decimal MaxCloseInRangeForShort { get; set; }

    // Cameron Ross ranges
    public decimal CamMinPriceMin { get; set; }
    public decimal CamMinPriceMax { get; set; }
    public decimal CamMaxPriceMin { get; set; }
    public decimal CamMaxPriceMax { get; set; }
    public decimal CamMinGapPctMin { get; set; }
    public decimal CamMinGapPctMax { get; set; }
    public decimal CamMinDayGainPctMin { get; set; }
    public decimal CamMinDayGainPctMax { get; set; }
    public decimal CamMinRvolMin { get; set; }
    public decimal CamMinRvolMax { get; set; }
    public int CamRvolLookbackMin { get; set; }
    public int CamRvolLookbackMax { get; set; }
    public int CamPullbackBarsMin { get; set; }
    public int CamPullbackBarsMax { get; set; }
    public decimal CamMaxPullbackPctMin { get; set; }
    public decimal CamMaxPullbackPctMax { get; set; }
    public decimal CamStopCentsMin { get; set; }
    public decimal CamStopCentsMax { get; set; }
    public decimal CamStopBufferPctMin { get; set; }
    public decimal CamStopBufferPctMax { get; set; }
    public decimal CamRoundBreakMaxDistanceMin { get; set; }
    public decimal CamRoundBreakMaxDistanceMax { get; set; }

    // Cameron Ross fixed settings
    public bool CamRequireRvol { get; set; }
    public bool CamRequireNews { get; set; }
    public bool CamRequireLowFloat { get; set; }
    public long CamMaxFloatShares { get; set; }
    public bool CamRequireShortInterest { get; set; }
    public decimal CamMinShortInterestPct { get; set; }
    public bool CamAllowShorts { get; set; }
    public bool CamRequireMicroPullback { get; set; }
    public bool CamRequireRoundBreak { get; set; }
    public decimal CamRoundIncrement { get; set; }
    public bool CamUseFixedStopCents { get; set; }
    public bool CamEnableDailyHistoryFallback { get; set; }
    public int CamDailyHistoryLookbackDays { get; set; }
    public TimeSpan CamStartTimeEt { get; set; }
    public TimeSpan CamEndTimeEt { get; set; }

    // Emmanuel ranges
    public decimal EmMinPriceMin { get; set; }
    public decimal EmMinPriceMax { get; set; }
    public decimal EmMaxPriceMin { get; set; }
    public decimal EmMaxPriceMax { get; set; }
    public decimal EmMinGapPctMin { get; set; }
    public decimal EmMinGapPctMax { get; set; }
    public int EmPremarketVolumeMin { get; set; }
    public int EmPremarketVolumeMax { get; set; }
    public decimal EmMinRvolMin { get; set; }
    public decimal EmMinRvolMax { get; set; }
    public int EmRvolLookbackMin { get; set; }
    public int EmRvolLookbackMax { get; set; }
    public int EmVwapSlopeBarsMin { get; set; }
    public int EmVwapSlopeBarsMax { get; set; }
    public decimal EmMaxSpreadCentsMin { get; set; }
    public decimal EmMaxSpreadCentsMax { get; set; }
    public decimal EmMaxBarRangePctAtrMin { get; set; }
    public decimal EmMaxBarRangePctAtrMax { get; set; }
    public int EmPoleLookbackMin { get; set; }
    public int EmPoleLookbackMax { get; set; }
    public decimal EmMinPolePctMin { get; set; }
    public decimal EmMinPolePctMax { get; set; }
    public int EmFlagBarsMin { get; set; }
    public int EmFlagBarsMax { get; set; }
    public decimal EmMaxFlagRetracePctMin { get; set; }
    public decimal EmMaxFlagRetracePctMax { get; set; }
    public decimal EmEntryBufferCentsMin { get; set; }
    public decimal EmEntryBufferCentsMax { get; set; }
    public decimal EmStopBufferCentsMin { get; set; }
    public decimal EmStopBufferCentsMax { get; set; }

    // Emmanuel fixed settings
    public bool EmRequireLowFloat { get; set; }
    public long EmMaxFloatShares { get; set; }
    public bool EmRequireRvol { get; set; }
    public bool EmRequirePriceAboveVwap { get; set; }
    public bool EmRequireVwapSlopeUp { get; set; }
    public bool EmRequireTightSpread { get; set; }
    public bool EmUseBarRangeAsSpreadProxy { get; set; }
    public bool EmRequireEma9Hook { get; set; }
    public bool EmRequireEma9AboveEma20 { get; set; }
    public bool EmRequireBullFlag { get; set; }
    public bool EmRequireLowerFlagVolume { get; set; }
    public TimeSpan EmPremarketStartEt { get; set; }
    public TimeSpan EmPremarketEndEt { get; set; }
}

public sealed record TrialSettings(
    TradingStrategy Strategy,
    TraidrScannerOptions? OliverScanner,
    CameronRossScannerOptions? CameronScanner,
    EmmanuelScannerOptions? EmmanuelScanner,
    TrialBacktestSettings Backtest);

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
    public TradingStrategy Strategy { get; init; }
    public TraidrScannerOptions? OliverScanner { get; init; }
    public CameronRossScannerOptions? CameronScanner { get; init; }
    public EmmanuelScannerOptions? EmmanuelScanner { get; init; }
    public required TrialBacktestSettings Backtest { get; init; }
    public required OptimizeMetrics Train { get; init; }
    public required OptimizeMetrics Test { get; init; }
    public decimal TrainScore { get; init; }
    public decimal TestScore { get; init; }
    public decimal FinalScore { get; init; }
}
