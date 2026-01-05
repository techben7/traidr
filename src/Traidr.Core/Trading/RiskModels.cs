using Traidr.Core.Scanning;

namespace Traidr.Core.Trading;

public enum RiskDecisionType { Allow, Block }

public sealed record RiskDecision(
    RiskDecisionType Decision,
    string Reason,
    int? Quantity = null,
    decimal? EstimatedRiskDollars = null);

public sealed record RiskManagerOptions
{
    public decimal AccountEquity { get; init; } = 25_000m;
    public decimal RiskPerTradePct { get; init; } = 0.003m;

    public decimal MaxPositionNotional { get; init; } = 10_000m;
    public int MaxShares { get; init; } = 5_000;

    public decimal MaxStopDistancePct { get; init; } = 0.015m;
    public decimal MinStopDistancePct { get; init; } = 0.001m;

    public decimal MinRewardToRiskR { get; init; } = 1.8m;

    public int MaxTradesPerDay { get; init; } = 6;
    public decimal MaxDailyLossPct { get; init; } = 0.01m;

    public TimeSpan SymbolCooldown { get; init; } = TimeSpan.FromMinutes(30);

    public decimal SlippagePct { get; init; } = 0.0005m;
}

public interface IRiskState
{
    DateOnly TradingDay { get; }
    int TradesToday { get; }
    decimal RealizedPnlToday { get; }

    void ResetIfNewDay(DateTimeOffset nowUtc, TimeZoneInfo marketTz);

    void RecordTradePlaced(string symbol, DateTimeOffset nowUtc);
    void RecordRealizedPnl(decimal pnlDollars, DateTimeOffset nowUtc);

    bool TryGetLastTradeTimeUtc(string symbol, out DateTimeOffset last);
}

public sealed class InMemoryRiskState : IRiskState
{
    private readonly Dictionary<string, DateTimeOffset> _lastTradeUtc = new(StringComparer.OrdinalIgnoreCase);

    public DateOnly TradingDay { get; private set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public int TradesToday { get; private set; }
    public decimal RealizedPnlToday { get; private set; }

    public void ResetIfNewDay(DateTimeOffset nowUtc, TimeZoneInfo marketTz)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, marketTz);
        var day = DateOnly.FromDateTime(local.DateTime);

        if (day != TradingDay)
        {
            TradingDay = day;
            TradesToday = 0;
            RealizedPnlToday = 0m;
            _lastTradeUtc.Clear();
        }
    }

    public void RecordTradePlaced(string symbol, DateTimeOffset nowUtc)
    {
        TradesToday++;
        _lastTradeUtc[symbol] = nowUtc;
    }

    public void RecordRealizedPnl(decimal pnlDollars, DateTimeOffset nowUtc)
    {
        RealizedPnlToday += pnlDollars;
    }

    public bool TryGetLastTradeTimeUtc(string symbol, out DateTimeOffset last)
        => _lastTradeUtc.TryGetValue(symbol, out last);
}

public interface IRiskManager
{
    RiskDecision Evaluate(SetupCandidate candidate, decimal? takeProfitPrice, DateTimeOffset nowUtc);
}
