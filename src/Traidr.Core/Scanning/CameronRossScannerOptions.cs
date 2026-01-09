namespace Traidr.Core.Scanning;

public sealed record CameronRossScannerOptions
{
    public decimal MinPrice { get; init; } = 2.0m;
    public decimal MaxPrice { get; init; } = 20.0m;
    public decimal MinGapPct { get; init; } = 0.10m;
    public decimal MinDayGainPct { get; init; } = 0.10m;

    public bool RequireRvol { get; init; } = true;
    public int RvolLookbackDays { get; init; } = 20;
    public decimal MinRvol { get; init; } = 5.0m;
    public bool EnableDailyHistoryFallback { get; init; } = false;
    public int DailyHistoryLookbackDays { get; init; } = 30;

    public bool RequireNews { get; init; } = false;
    public bool RequireLowFloat { get; init; } = false;
    public long MaxFloatShares { get; init; } = 20_000_000;
    public bool RequireShortInterest { get; init; } = false;
    public decimal MinShortInterestPct { get; init; } = 0.0m;

    public bool AllowShorts { get; init; } = false;

    public TimeSpan StartTimeEt { get; init; } = new(7, 0, 0);
    public TimeSpan EndTimeEt { get; init; } = new(10, 30, 0);

    public bool RequireMicroPullback { get; init; } = true;
    public int PullbackBars { get; init; } = 3;
    public decimal MaxPullbackPct { get; init; } = 0.03m;

    public bool RequireRoundBreak { get; init; } = false;
    public decimal RoundIncrement { get; init; } = 0.5m;
    public decimal RoundBreakMaxDistance { get; init; } = 0.05m;

    public bool UseFixedStopCents { get; init; } = true;
    public decimal StopCents { get; init; } = 0.15m;
    public decimal StopBufferPct { get; init; } = 0.0m;
}
