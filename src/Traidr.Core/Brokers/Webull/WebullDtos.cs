using System.Text.Json.Serialization;

namespace Traidr.Core.Brokers.Webull;

public sealed record WebullInstrument(string Symbol, string InstrumentId, string Name);

public sealed record WebullPlaceOrderResponse(string ClientOrderId);

public sealed record WebullStockOrder
{
    [JsonPropertyName("client_order_id")]
    public required string ClientOrderId { get; init; }

    [JsonPropertyName("side")]
    public required string Side { get; init; } // BUY / SELL

    [JsonPropertyName("tif")]
    public required string Tif { get; init; }  // DAY

    [JsonPropertyName("extended_hours_trading")]
    public required bool ExtendedHoursTrading { get; init; }

    [JsonPropertyName("instrument_id")]
    public required string InstrumentId { get; init; }

    [JsonPropertyName("order_type")]
    public required string OrderType { get; init; } // LIMIT / MARKET / STOP_LOSS...

    [JsonPropertyName("qty")]
    public required string Qty { get; init; }

    [JsonPropertyName("limit_price")]
    public string? LimitPrice { get; init; }

    [JsonPropertyName("stop_price")]
    public string? StopPrice { get; init; }
}

public sealed record WebullOrderDetail(
    string ClientOrderId,
    IReadOnlyList<WebullOrderItem> Items);

public sealed record WebullOrderItem(
    string Symbol,
    string OrderStatus,
    string Side,
    string Qty,
    string? FilledQty,
    string? FilledPrice,
    string? LimitPrice,
    string? StopPrice);
