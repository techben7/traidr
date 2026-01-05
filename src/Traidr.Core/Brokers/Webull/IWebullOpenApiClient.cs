namespace Traidr.Core.Brokers.Webull;

public interface IWebullOpenApiClient
{
    Task<WebullInstrument> GetInstrumentAsync(string symbol, string category, CancellationToken ct);
    Task<WebullPlaceOrderResponse> PlaceStockOrderAsync(string accountId, WebullStockOrder order, CancellationToken ct);
    Task<WebullOrderDetail> QueryOrderDetailAsync(string accountId, string clientOrderId, CancellationToken ct);
    Task<string> CancelOrderAsync(string accountId, string clientOrderId, CancellationToken ct);
}
