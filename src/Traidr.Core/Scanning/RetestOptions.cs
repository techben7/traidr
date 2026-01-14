namespace Traidr.Core.Scanning;

public sealed record RetestOptions
{
    public bool IncludeRetest { get; init; } = true;
    public int RetestMaxBars { get; init; } = 6;
    public decimal RetestTolerancePct { get; init; } = 0.001m;
    public decimal RetestConfirmMinClosePct { get; init; } = 0.0m;
}
