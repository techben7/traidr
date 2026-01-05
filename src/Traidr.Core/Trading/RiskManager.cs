using Traidr.Core.Scanning;

namespace Traidr.Core.Trading;

public sealed class RiskManager : IRiskManager
{
    private readonly IRiskState _state;
    private readonly RiskManagerOptions _opt;
    private readonly TimeZoneInfo _marketTz;

    public RiskManager(IRiskState state, RiskManagerOptions opt, string marketTimeZoneId = "America/New_York")
    {
        _state = state;
        _opt = opt;
        _marketTz = TimeZoneInfo.FindSystemTimeZoneById(marketTimeZoneId);
    }

    public RiskDecision Evaluate(SetupCandidate c, decimal? takeProfitPrice, DateTimeOffset nowUtc)
    {
        _state.ResetIfNewDay(nowUtc, _marketTz);

        if (_state.TradesToday >= _opt.MaxTradesPerDay)
            return Block($"MaxTradesPerDay reached ({_opt.MaxTradesPerDay}).");

        var maxDailyLoss = _opt.AccountEquity * _opt.MaxDailyLossPct;
        if (_state.RealizedPnlToday <= -maxDailyLoss)
            return Block($"MaxDailyLoss reached ({_state.RealizedPnlToday:N2} <= -{maxDailyLoss:N2}).");

        if (_state.TryGetLastTradeTimeUtc(c.Symbol, out var last))
        {
            var until = last + _opt.SymbolCooldown;
            if (nowUtc < until)
                return Block($"Symbol cooldown active ({(until - nowUtc).TotalMinutes:F0} min remaining).");
        }

        var entry = c.EntryPrice;
        var stop = c.StopPrice;

        if (entry <= 0m || stop <= 0m)
            return Block("Invalid entry/stop prices.");

        var stopDist = Math.Abs(entry - stop);
        var stopDistPct = stopDist / entry;

        if (stopDistPct > _opt.MaxStopDistancePct)
            return Block($"Stop distance too large: {stopDistPct:P2} > {_opt.MaxStopDistancePct:P2}.");

        if (stopDistPct < _opt.MinStopDistancePct)
            return Block($"Stop distance too small: {stopDistPct:P2} < {_opt.MinStopDistancePct:P2}.");

        if (takeProfitPrice.HasValue)
        {
            var reward = Math.Abs(takeProfitPrice.Value - entry);
            var rMultiple = reward / stopDist;
            if (rMultiple < _opt.MinRewardToRiskR)
                return Block($"Reward:Risk too low: {rMultiple:F2}R < {_opt.MinRewardToRiskR:F2}R.");
        }

        var slippedEntry = ApplySlippage(entry, c.Direction);
        var riskPerShare = Math.Abs(slippedEntry - stop);
        if (riskPerShare <= 0m)
            return Block("Risk/share computed as zero/negative.");

        var riskBudget = _opt.AccountEquity * _opt.RiskPerTradePct;
        var qtyByRisk = (int)Math.Floor(riskBudget / riskPerShare);
        if (qtyByRisk <= 0)
            return Block($"Risk budget too small for this stop distance (risk/share={riskPerShare:N4}).");

        var qtyByNotional = (int)Math.Floor(_opt.MaxPositionNotional / slippedEntry);
        var qty = Math.Min(qtyByRisk, qtyByNotional);
        qty = Math.Min(qty, _opt.MaxShares);

        if (qty <= 0)
            return Block("Position size after caps is zero.");

        var estRisk = qty * riskPerShare;

        return new RiskDecision(RiskDecisionType.Allow,
            $"Allowed. qty={qty} estRisk=${estRisk:N2} (risk/share={riskPerShare:N4})",
            Quantity: qty,
            EstimatedRiskDollars: estRisk);
    }

    private decimal ApplySlippage(decimal entry, BreakoutDirection dir)
    {
        var slip = entry * _opt.SlippagePct;
        return dir == BreakoutDirection.Long ? entry + slip : entry - slip;
    }

    private static RiskDecision Block(string reason) => new(RiskDecisionType.Block, reason);
}
