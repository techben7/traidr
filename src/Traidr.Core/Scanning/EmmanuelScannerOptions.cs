namespace Traidr.Core.Scanning;

public sealed record EmmanuelScannerOptions
{
    public decimal MinPrice { get; init; } = 1.0m;
    public decimal MaxPrice { get; init; } = 20.0m;
    public decimal MinGapPct { get; init; } = 0.04m;
    public long MinPremarketVolume { get; init; } = 100_000;
    public TimeSpan PremarketStartEt { get; init; } = new(4, 0, 0);
    public TimeSpan PremarketEndEt { get; init; } = new(9, 0, 0);

    public bool RequireLowFloat { get; init; } = false;
    public long MaxFloatShares { get; init; } = 20_000_000;
    public bool RequireRvol { get; init; } = true;
    public int RvolLookbackDays { get; init; } = 10;
    public decimal MinRvol { get; init; } = 3.0m;

    public bool RequirePriceAboveVwap { get; init; } = true;
    public bool RequireVwapSlopeUp { get; init; } = true;
    public int VwapSlopeBars { get; init; } = 5;

    public bool RequireTightSpread { get; init; } = true;
    public decimal MaxSpreadCents { get; init; } = 0.01m;
    public bool UseBarRangeAsSpreadProxy { get; init; } = true;
    public decimal MaxBarRangePctOfAtr { get; init; } = 0.25m;

    public bool RequireEma9Hook { get; init; } = true;
    public bool RequireEma9AboveEma20 { get; init; } = false;

    public bool RequireBullFlag { get; init; } = true;
    public int PoleLookbackBars { get; init; } = 8;
    public decimal MinPolePct { get; init; } = 0.05m;
    public int FlagBars { get; init; } = 3;
    public decimal MaxFlagRetracePct { get; init; } = 0.35m;
    public bool RequireLowerFlagVolume { get; init; } = true;

    public decimal EntryBufferCents { get; init; } = 0.02m;
    public decimal StopBufferCents { get; init; } = 0.01m;
}
