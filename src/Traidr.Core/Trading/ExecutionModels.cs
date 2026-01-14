using Traidr.Core.Scanning;

namespace Traidr.Core.Trading;

public sealed record TradeIntent(
    string Symbol,
    BreakoutDirection Direction,
    int Quantity,
    decimal EntryPrice,
    decimal StopPrice,
    decimal? TakeProfitPrice,
    TimeSpan? FillTimeoutOverride = null);

public interface IOrderExecutor
{
    Task ExecuteAsync(TradeIntent intent, CancellationToken ct = default);
}

public sealed class PaperOrderExecutor : IOrderExecutor
{
    private readonly IRiskState _riskState;
    private readonly Action<string> _log;

    public PaperOrderExecutor(IRiskState riskState, Action<string>? log = null)
    {
        _riskState = riskState;
        _log = log ?? Console.WriteLine;
    }

    public Task ExecuteAsync(TradeIntent intent, CancellationToken ct = default)
    {
        _log($"PAPER EXECUTE: {intent.Symbol} {intent.Direction} qty={intent.Quantity} entry={intent.EntryPrice} stop={intent.StopPrice} tp={intent.TakeProfitPrice} fillTimeout={intent.FillTimeoutOverride}");
        _riskState.RecordTradePlaced(intent.Symbol, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
