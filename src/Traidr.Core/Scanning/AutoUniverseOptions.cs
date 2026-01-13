namespace Traidr.Core.Scanning;

public sealed record AutoUniverseOptions
{
    public int TopCount { get; init; } = 50;
    public int ScreenerLimit { get; init; } = 500;
    public int MaxCandidateSymbols { get; init; } = 250;

    public decimal MinMarketCap { get; init; } = 250_000_000m;
    public decimal MaxMarketCap { get; init; } = 2_000_000_000m;
    public decimal MinPrice { get; init; } = 2m;
    public decimal MaxPrice { get; init; } = 30m;

    public decimal MinDayRvol { get; init; } = 2.0m;
    public decimal MinGapPct { get; init; } = 0.02m;
}
