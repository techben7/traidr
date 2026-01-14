using Microsoft.Extensions.Logging;
using Traidr.Core.Indicators;
using Traidr.Core.MarketData;
using Traidr.Core.Scanning;

namespace Traidr.Cli;

public interface IStrategyScannerFactory
{
    ISetupScanner Create(
        TradingStrategy strategy,
        TraidrScannerOptions? oliver = null,
        CameronRossScannerOptions? cameron = null,
        EmmanuelScannerOptions? emmanuel = null,
        ReversalUpScannerOptions? reversalUp = null);
}

public sealed class StrategyScannerFactory : IStrategyScannerFactory
{
    private readonly IndicatorCalculator _indicators;
    private readonly TraidrScannerOptions _oliverDefaults;
    private readonly CameronRossScannerOptions _cameronDefaults;
    private readonly EmmanuelScannerOptions _emmanuelDefaults;
    private readonly ReversalUpScannerOptions _reversalUpDefaults;
    private readonly RetestOptions _retest;
    private readonly IMarketMetadataProvider _meta;
    private readonly IMarketDataClient _marketData;
    private readonly ILoggerFactory _logs;

    public StrategyScannerFactory(
        IndicatorCalculator indicators,
        TraidrScannerOptions oliverDefaults,
        CameronRossScannerOptions cameronDefaults,
        EmmanuelScannerOptions emmanuelDefaults,
        ReversalUpScannerOptions reversalUpDefaults,
        RetestOptions retest,
        IMarketMetadataProvider meta,
        IMarketDataClient marketData,
        ILoggerFactory logs)
    {
        _indicators = indicators;
        _oliverDefaults = oliverDefaults;
        _cameronDefaults = cameronDefaults;
        _emmanuelDefaults = emmanuelDefaults;
        _reversalUpDefaults = reversalUpDefaults;
        _retest = retest;
        _meta = meta;
        _marketData = marketData;
        _logs = logs;
    }

    public ISetupScanner Create(
        TradingStrategy strategy,
        TraidrScannerOptions? oliver = null,
        CameronRossScannerOptions? cameron = null,
        EmmanuelScannerOptions? emmanuel = null,
        ReversalUpScannerOptions? reversalUp = null)
    {
        return strategy switch
        {
            TradingStrategy.CameronRoss => new CameronRossScanner(
                _indicators,
                cameron ?? _cameronDefaults,
                _retest,
                _meta,
                _marketData,
                _logs.CreateLogger<CameronRossScanner>()),
            TradingStrategy.Emmanuel => new EmmanuelScanner(
                _indicators,
                emmanuel ?? _emmanuelDefaults,
                _retest,
                _meta,
                _logs.CreateLogger<EmmanuelScanner>()),
            TradingStrategy.ReversalUp => new ReversalUpScanner(
                _indicators,
                reversalUp ?? _reversalUpDefaults,
                _retest,
                _logs.CreateLogger<ReversalUpScanner>()),
            _ => new TraidrScanner(
                _indicators,
                oliver ?? _oliverDefaults,
                _retest,
                _logs.CreateLogger<TraidrScanner>())
        };
    }
}
