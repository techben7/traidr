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
        EmmanuelScannerOptions? emmanuel = null);
}

public sealed class StrategyScannerFactory : IStrategyScannerFactory
{
    private readonly IndicatorCalculator _indicators;
    private readonly TraidrScannerOptions _oliverDefaults;
    private readonly CameronRossScannerOptions _cameronDefaults;
    private readonly EmmanuelScannerOptions _emmanuelDefaults;
    private readonly IMarketMetadataProvider _meta;
    private readonly ILoggerFactory _logs;

    public StrategyScannerFactory(
        IndicatorCalculator indicators,
        TraidrScannerOptions oliverDefaults,
        CameronRossScannerOptions cameronDefaults,
        EmmanuelScannerOptions emmanuelDefaults,
        IMarketMetadataProvider meta,
        ILoggerFactory logs)
    {
        _indicators = indicators;
        _oliverDefaults = oliverDefaults;
        _cameronDefaults = cameronDefaults;
        _emmanuelDefaults = emmanuelDefaults;
        _meta = meta;
        _logs = logs;
    }

    public ISetupScanner Create(
        TradingStrategy strategy,
        TraidrScannerOptions? oliver = null,
        CameronRossScannerOptions? cameron = null,
        EmmanuelScannerOptions? emmanuel = null)
    {
        return strategy switch
        {
            TradingStrategy.CameronRoss => new CameronRossScanner(
                _indicators,
                cameron ?? _cameronDefaults,
                _meta,
                _logs.CreateLogger<CameronRossScanner>()),
            TradingStrategy.Emmanuel => new EmmanuelScanner(
                _indicators,
                emmanuel ?? _emmanuelDefaults,
                _meta,
                _logs.CreateLogger<EmmanuelScanner>()),
            _ => new TraidrScanner(
                _indicators,
                oliver ?? _oliverDefaults,
                _logs.CreateLogger<TraidrScanner>())
        };
    }
}
