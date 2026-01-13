namespace Traidr.Core.Scanning;

public enum BreakoutDirection
{
    Long,
    Short
}

public sealed record SetupCandidate(
    string Symbol,
    BreakoutDirection Direction,

    decimal EntryPrice,
    decimal StopPrice,
    decimal? TakeProfitPrice,

    decimal ConsolidationHigh,
    decimal ConsolidationLow,

    decimal RangePct,
    decimal AtrPct,
    decimal BodyToMedianBody,
    decimal VolumeToAvgVolume,

    decimal? Ema20,
    decimal? Ema200,
    decimal? Vwap,
    decimal? Atr14,

    DateTime ElephantBarTimeUtc);
