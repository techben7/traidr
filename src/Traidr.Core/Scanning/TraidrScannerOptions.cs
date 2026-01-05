namespace Traidr.Core.Scanning;

public sealed record TraidrScannerOptions
{
    // consolidation window (on 5-min bars)
    public int ConsolidationLookbackBars { get; init; } = 12;  // last hour
    public decimal MaxConsolidationRangePct { get; init; } = 0.006m; // 0.6% of price

    // elephant candle thresholds
    public decimal MinBodyToMedianBody { get; init; } = 2.5m;
    public decimal MinVolumeToAvgVolume { get; init; } = 1.5m;

    // breakout: close beyond range by at least buffer pct
    public decimal BreakoutBufferPct { get; init; } = 0.000m;

    // filters vs indicators (optional)
    public bool RequireNearEma20 { get; init; } = false;
    public decimal MaxDistanceFromEma20Pct { get; init; } = 0.010m; // 1%

    public bool RequirePriceAboveEma20 { get; init; } = false;
    public bool RequirePriceAboveEma200 { get; init; } = false;
    public bool RequireTrendEma20OverEma200 { get; init; } = false;
    public bool RequirePriceAboveVwap { get; init; } = false;

    public bool RequireEma20NearEma200 { get; init; } = false;
    public decimal MaxEmaDistancePct { get; init; } = 0.010m; // 1%

    // ATR sanity
    public bool RequireAtrAvailable { get; init; } = true;
    public decimal MinAtrPct { get; init; } = 0.001m;
    public decimal MaxAtrPct { get; init; } = 0.030m;

    // elephant bar close position (0..1), higher = closer to high
    public decimal MinCloseInRangeForLong { get; init; } = 0.0m;
    public decimal MaxCloseInRangeForShort { get; init; } = 1.0m;

    // Stop placement
    public decimal StopBufferPct { get; init; } = 0.000m; // e.g., 0.001 adds 0.1% beyond consolidation boundary
}
