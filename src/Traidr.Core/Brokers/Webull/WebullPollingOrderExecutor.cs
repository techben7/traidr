using Traidr.Core.Scanning;
using Traidr.Core.Trading;

namespace Traidr.Core.Brokers.Webull;

public sealed class WebullPollingOrderExecutor : IOrderExecutor
{
    private readonly IWebullOpenApiClient _webull;
    private readonly WebullOpenApiOptions _opt;
    private readonly WebullExecutionOptions _execOpt;
    private readonly IRiskState _riskState;
    private readonly Action<string> _log;

    public WebullPollingOrderExecutor(
        IWebullOpenApiClient webull,
        WebullOpenApiOptions opt,
        WebullExecutionOptions execOpt,
        IRiskState riskState,
        Action<string>? log = null)
    {
        _webull = webull;
        _opt = opt;
        _execOpt = execOpt;
        _riskState = riskState;
        _log = log ?? Console.WriteLine;
    }

    public async Task ExecuteAsync(TradeIntent intent, CancellationToken ct = default)
    {
        var instrument = await _webull.GetInstrumentAsync(intent.Symbol, _opt.DefaultCategory, ct);

        var entrySide = intent.Direction == BreakoutDirection.Long ? "BUY" : "SELL";
        var exitSide = intent.Direction == BreakoutDirection.Long ? "SELL" : "BUY";

        // Entry LIMIT
        var entryClientOrderId = Guid.NewGuid().ToString("N");
        var entryOrder = new WebullStockOrder
        {
            ClientOrderId = entryClientOrderId,
            Side = entrySide,
            Tif = "DAY",
            ExtendedHoursTrading = false,
            InstrumentId = instrument.InstrumentId,
            OrderType = "LIMIT",
            Qty = intent.Quantity.ToString(),
            LimitPrice = intent.EntryPrice.ToString("0.####")
        };

        _log($"WEBULL ENTRY: {intent.Symbol} {entrySide} qty={intent.Quantity} limit={intent.EntryPrice}");
        var placedEntry = await _webull.PlaceStockOrderAsync(_opt.AccountId, entryOrder, ct);

        var fill = await WaitForFillAsync(_opt.AccountId, placedEntry.ClientOrderId, ct);

        if (fill is null)
        {
            if (_execOpt.CancelEntryOnTimeout)
            {
                _log($"ENTRY TIMEOUT: cancelling {placedEntry.ClientOrderId}");
                await _webull.CancelOrderAsync(_opt.AccountId, placedEntry.ClientOrderId, ct);
            }
            return;
        }

        var filledQty = fill.Value.FilledQty;
        var filledPrice = fill.Value.FilledPrice;

        _log($"ENTRY FILLED: {intent.Symbol} qty={filledQty} avgPrice={filledPrice}");

        // STOP must be placed first
        ExitState? stopExit = null;

        if (_execOpt.PlaceStopLossAfterFill)
        {
            var placedStop = await TryPlaceStopExitAsync(intent.Symbol, instrument.InstrumentId, exitSide, filledQty, intent.StopPrice, ct);

            if (placedStop is null)
            {
                if (_execOpt.RequireStopLoss)
                {
                    _log($"CRITICAL: STOP REQUIRED but failed for {intent.Symbol}. Not placing TP.");
                    await PanicExitIfEnabled(intent.Symbol, instrument.InstrumentId, exitSide, filledQty, ct);
                    return;
                }
            }
            else
            {
                if (_execOpt.VerifyStopSubmitted)
                {
                    var ok = await VerifyOrderSubmittedAsync(
                        accountId: _opt.AccountId,
                        clientOrderId: placedStop.ClientOrderId,
                        symbol: intent.Symbol,
                        kind: "STOP",
                        timeout: _execOpt.StopSubmitVerifyTimeout,
                        pollInterval: _execOpt.StopSubmitVerifyPollInterval,
                        ct: ct);

                    if (!ok && _execOpt.RequireStopLoss)
                    {
                        _log($"CRITICAL: STOP placement returned success but failed verification for {intent.Symbol}. Treating as STOP failure.");
                        await PanicExitIfEnabled(intent.Symbol, instrument.InstrumentId, exitSide, filledQty, ct);
                        return;
                    }
                }

                stopExit = new ExitState(placedStop.ClientOrderId, "STOP", WebullTerminal.None);
            }
        }
        else if (_execOpt.RequireStopLoss)
        {
            _log($"CRITICAL: RequireStopLoss=true but PlaceStopLossAfterFill=false. Aborting trade flow for {intent.Symbol}.");
            return;
        }

        // TP only if stop exists (when stop required)
        ExitState? tpExit = null;
        if (_execOpt.PlaceTakeProfitAfterFill && intent.TakeProfitPrice.HasValue)
        {
            if (_execOpt.RequireStopLoss && stopExit is null)
            {
                _log($"Skipping TP for {intent.Symbol} because STOP was not successfully placed.");
            }
            else
            {
                try
                {
                    var tp = intent.TakeProfitPrice.Value;
                    var tpClientOrderId = Guid.NewGuid().ToString("N");

                    var tpOrder = new WebullStockOrder
                    {
                        ClientOrderId = tpClientOrderId,
                        Side = exitSide,
                        Tif = "DAY",
                        ExtendedHoursTrading = false,
                        InstrumentId = instrument.InstrumentId,
                        OrderType = "LIMIT",
                        Qty = filledQty,
                        LimitPrice = tp.ToString("0.####")
                    };

                    _log($"WEBULL TP: {intent.Symbol} {exitSide} qty={filledQty} limit={tp}");
                    var placedTp = await _webull.PlaceStockOrderAsync(_opt.AccountId, tpOrder, ct);
                    tpExit = new ExitState(placedTp.ClientOrderId, "TP", WebullTerminal.None);
                }
                catch (Exception ex)
                {
                    _log($"TP placement failed for {intent.Symbol}. STOP remains active. Error: {ex.Message}");
                }
            }
        }

        _riskState.RecordTradePlaced(intent.Symbol, DateTimeOffset.UtcNow);

        // Monitor exits
        if (_execOpt.MonitorExitsAndCancelOther && stopExit is not null)
        {
            await MonitorExitsAndCancelOtherAsync(
                accountId: _opt.AccountId,
                symbol: intent.Symbol,
                stop: stopExit,
                tp: tpExit,
                ct: ct);
        }
    }

    private async Task PanicExitIfEnabled(string symbol, string instrumentId, string exitSide, string qty, CancellationToken ct)
    {
        if (!_execOpt.PanicMarketExitOnStopFailure) return;

        try
        {
            await PlaceMarketExitAsync(symbol, instrumentId, exitSide, qty, ct);
        }
        catch (Exception ex)
        {
            _log($"CRITICAL: PANIC MARKET EXIT ALSO FAILED for {symbol}. Manual intervention required. Error: {ex.Message}");
        }
    }

    private async Task<(string FilledQty, string FilledPrice)?> WaitForFillAsync(string accountId, string clientOrderId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _execOpt.FillTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var detail = await _webull.QueryOrderDetailAsync(accountId, clientOrderId, ct);
            var item = detail.Items.FirstOrDefault();

            if (item is not null)
            {
                var status = item.OrderStatus?.Trim().ToUpperInvariant();

                if (status == "FILLED")
                {
                    var filledQty = (item.FilledQty ?? "0").Trim();
                    var filledPrice = (item.FilledPrice ?? "0").Trim();
                    if (filledQty == "0") filledQty = item.Qty;
                    return (filledQty, filledPrice);
                }

                if (status is "FAILED" or "CANCELLED")
                {
                    _log($"Entry terminal status {status} for {clientOrderId}");
                    return null;
                }

                _log($"Entry status {status} filled={item.FilledQty}/{item.Qty}");
            }

            await Task.Delay(_execOpt.PollInterval, ct);
        }

        _log($"Fill timeout reached for {clientOrderId}");
        return null;
    }

    private async Task<WebullPlaceOrderResponse?> TryPlaceStopExitAsync(
        string symbol,
        string instrumentId,
        string exitSide,
        string qty,
        decimal stopPrice,
        CancellationToken ct)
    {
        try
        {
            var stopClientOrderId = Guid.NewGuid().ToString("N");

            var stopOrder = new WebullStockOrder
            {
                ClientOrderId = stopClientOrderId,
                Side = exitSide,
                Tif = "DAY",
                ExtendedHoursTrading = false,
                InstrumentId = instrumentId,
                OrderType = _execOpt.StopExitOrderType,
                Qty = qty,
                StopPrice = stopPrice.ToString("0.####")
            };

            _log($"WEBULL STOP: {symbol} {exitSide} qty={qty} stop={stopPrice}");
            return await _webull.PlaceStockOrderAsync(_opt.AccountId, stopOrder, ct);
        }
        catch (Exception ex)
        {
            _log($"STOP placement failed for {symbol}. Error: {ex.Message}");
            return null;
        }
    }

    private async Task<WebullPlaceOrderResponse> PlaceMarketExitAsync(
        string symbol,
        string instrumentId,
        string exitSide,
        string qty,
        CancellationToken ct)
    {
        var clientOrderId = Guid.NewGuid().ToString("N");

        var order = new WebullStockOrder
        {
            ClientOrderId = clientOrderId,
            Side = exitSide,
            Tif = "DAY",
            ExtendedHoursTrading = false,
            InstrumentId = instrumentId,
            OrderType = "MARKET",
            Qty = qty
        };

        _log($"CRITICAL: PANIC MARKET EXIT: {symbol} {exitSide} qty={qty}");
        return await _webull.PlaceStockOrderAsync(_opt.AccountId, order, ct);
    }

    private enum WebullTerminal { None, Filled, Cancelled, Failed }

    private sealed record ExitState(string ClientOrderId, string Kind, WebullTerminal Terminal);

    private static WebullTerminal ToTerminal(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return WebullTerminal.None;
        return status.Trim().ToUpperInvariant() switch
        {
            "FILLED" => WebullTerminal.Filled,
            "CANCELLED" => WebullTerminal.Cancelled,
            "FAILED" => WebullTerminal.Failed,
            _ => WebullTerminal.None
        };
    }

    private static bool IsAcceptableSubmittedState(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var s = status.Trim().ToUpperInvariant();
        return s is "SUBMITTED" or "PARTIAL_FILLED" or "FILLED";
    }

    private async Task<bool> VerifyOrderSubmittedAsync(
        string accountId,
        string clientOrderId,
        string symbol,
        string kind,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var detail = await _webull.QueryOrderDetailAsync(accountId, clientOrderId, ct);
                var item = detail.Items.FirstOrDefault();

                if (item is not null)
                {
                    var status = item.OrderStatus;
                    if (IsAcceptableSubmittedState(status))
                    {
                        _log($"{kind} verified submitted for {symbol}. status={status} id={clientOrderId}");
                        return true;
                    }

                    var term = ToTerminal(status);
                    if (term is WebullTerminal.Cancelled or WebullTerminal.Failed)
                    {
                        _log($"{kind} terminal early for {symbol}. status={status} id={clientOrderId}");
                        return false;
                    }

                    _log($"{kind} not yet submitted for {symbol}. status={status}");
                }
                else
                {
                    _log($"{kind} detail returned no items yet for {symbol}. id={clientOrderId}");
                }
            }
            catch (Exception ex)
            {
                _log($"{kind} verify poll failed for {symbol}. id={clientOrderId}. err={ex.Message}");
            }

            await Task.Delay(pollInterval, ct);
        }

        _log($"{kind} verify timeout for {symbol}. id={clientOrderId}");
        return false;
    }

    private async Task<WebullTerminal> GetOrderTerminalAsync(string accountId, string clientOrderId, CancellationToken ct)
    {
        var detail = await _webull.QueryOrderDetailAsync(accountId, clientOrderId, ct);
        var item = detail.Items.FirstOrDefault();
        return item is null ? WebullTerminal.None : ToTerminal(item.OrderStatus);
    }

    private async Task MonitorExitsAndCancelOtherAsync(string accountId, string symbol, ExitState stop, ExitState? tp, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _execOpt.ExitMonitorTimeout;

        if (tp is null)
        {
            _log($"Exit monitor: only STOP exists for {symbol}. No cancel-other needed.");
            return;
        }

        _log($"Exit monitor started for {symbol}. stop={stop.ClientOrderId} tp={tp.ClientOrderId}");

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var stopTerm = await GetOrderTerminalAsync(accountId, stop.ClientOrderId, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
            var tpTerm = await GetOrderTerminalAsync(accountId, tp.ClientOrderId, ct);

            if (stopTerm == WebullTerminal.Filled && tpTerm != WebullTerminal.Filled)
            {
                _log($"STOP filled for {symbol}. Cancelling TP {tp.ClientOrderId}");
                await SafeCancelAsync(accountId, tp.ClientOrderId, ct);
                return;
            }

            if (tpTerm == WebullTerminal.Filled && stopTerm != WebullTerminal.Filled)
            {
                _log($"TP filled for {symbol}. Cancelling STOP {stop.ClientOrderId}");
                await SafeCancelAsync(accountId, stop.ClientOrderId, ct);
                return;
            }

            if (tpTerm == WebullTerminal.Filled && stopTerm == WebullTerminal.Filled)
            {
                _log($"CRITICAL: BOTH exits filled for {symbol}. Manual review required.");
                return;
            }

            await Task.Delay(_execOpt.PollInterval, ct);
        }

        _log($"Exit monitor timeout reached for {symbol}.");
    }

    private async Task SafeCancelAsync(string accountId, string clientOrderId, CancellationToken ct)
    {
        try
        {
            await _webull.CancelOrderAsync(accountId, clientOrderId, ct);
        }
        catch (Exception ex)
        {
            _log($"Failed to cancel order {clientOrderId}. err={ex.Message}");
        }
    }
}
