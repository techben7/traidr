namespace Traidr.Core.Scanning;

public sealed record ReversalUpScannerOptions
{
    public int SidewaysLookbackBars { get; init; } = 40;
    public decimal MaxSidewaysRangePct { get; init; } = 0.035m;
    public int PivotLookbackBars { get; init; } = 3;
    public int MinSwingCount { get; init; } = 4;
    public int MaxBarsAfterSwingLow { get; init; } = 3;

    public decimal MinGreenBodyToMedian { get; init; } = 1.2m;
    public decimal MinLowerWickPct { get; init; } = 0.25m;

    public decimal EntryBufferPct { get; init; } = 0.0m;
    public decimal StopBufferPct { get; init; } = 0.001m;

    public decimal MinBullRunPct { get; init; } = 0.02m;
    public decimal TakeProfitBullRunPct { get; init; } = 0.60m;
}
