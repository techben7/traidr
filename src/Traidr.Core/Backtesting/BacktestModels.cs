using System.Text.Json.Serialization;
using Traidr.Core.MarketData;
using Traidr.Core.Scanning;

namespace Traidr.Core.Backtesting;

public enum SameBarFillRule
{
    // If both TP and Stop are touched in the same bar, assume stop hits first.
    ConservativeStopFirst,
    // If both TP and Stop are touched in the same bar, assume TP hits first.
    OptimisticTakeProfitFirst
}

public sealed record BacktestOptions
{
    public required IReadOnlyList<string> Symbols { get; init; }

    // Interpreted as America/New_York dates.
    public required DateOnly FromDateEt { get; init; }
    public required DateOnly ToDateEt { get; init; }

    public string Timeframe { get; init; } = "5Min";

    // If entry limit doesn't fill within this many bars, treat as no-fill.
    public int MaxBarsToFillEntry { get; init; } = 6;

    // Make limit entries more (or less) likely to fill by widening the acceptable limit.
    // Long: limit = entry * (1 + EntryLimitBufferPct)
    // Short: limit = entry * (1 - EntryLimitBufferPct)
    public decimal EntryLimitBufferPct { get; init; } = 0m;

    // Flatten any still-open position at or after this ET time.
    public TimeOnly FlattenTimeEt { get; init; } = new(15, 50);
    public MarketSessionMode SessionMode { get; init; } = MarketSessionMode.Regular;
    public MarketSessionHours SessionHours { get; init; } = new();

    public SameBarFillRule SameBarRule { get; init; } = SameBarFillRule.ConservativeStopFirst;

    public decimal SlippagePct { get; init; } = 0.0005m;
    public decimal CommissionPerTrade { get; init; } = 0m;

    // Optional: set a take-profit at R multiple of initial risk.
    // Example: 2.0 => TP is 2R away from entry.
    public decimal? TakeProfitR { get; init; } = null;
}

public enum BacktestTradeOutcome
{
    NoFill,
    Stop,
    TakeProfit,
    EndOfDay,
}

public sealed record BacktestTrade
{
    public required string Symbol { get; init; }
    public required string Direction { get; init; } // "Long" / "Short"
    public required int Quantity { get; init; }

    public required DateTime SignalTimeUtc { get; init; }
    public DateTime? EntryTimeUtc { get; init; }
    public DateTime? ExitTimeUtc { get; init; }

    public required decimal EntryLimit { get; init; }
    public required decimal StopPrice { get; init; }
    public decimal? TakeProfitPrice { get; init; }

    public decimal? FilledEntryPrice { get; init; }
    public decimal? ExitPrice { get; init; }

    public BacktestTradeOutcome Outcome { get; init; }

    public decimal? PnlDollars { get; init; }
    public decimal? RMultiple { get; init; }

    // Debug context
    public decimal? RiskPerShare { get; init; }
    public decimal? RewardPerShare { get; init; }

    [JsonIgnore]
    public SetupCandidate? Candidate { get; init; }
}

public sealed record BacktestSummary(
    int Trades,
    int Wins,
    int Losses,
    int NoFills,
    decimal TotalPnl,
    decimal AvgPnl,
    decimal WinRate,
    decimal AvgR,
    decimal MaxDrawdown);

public sealed record OpenPosition(
        string Symbol,
        BreakoutDirection Direction,
        int Quantity,
        DateTime SignalTimeUtc,
        DateTime EntryTimeUtc,
        decimal EntryLimit,
        decimal EntryPrice,
        decimal StopPrice,
        decimal? TakeProfitPrice,
        SetupCandidate Candidate,
        decimal CommissionPerTrade);
