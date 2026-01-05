using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Traidr.Core.Brokers.Webull;

public sealed class WebullOpenApiClient : IWebullOpenApiClient
{
    private readonly HttpClient _http;
    private readonly WebullOpenApiOptions _opt;

    private readonly Dictionary<string, WebullInstrument> _instrumentCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SnakeJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WebullOpenApiClient(HttpClient http, WebullOpenApiOptions opt)
    {
        _http = http;
        _opt = opt;
        _http.BaseAddress = new Uri(_opt.Endpoint);

        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<WebullInstrument> GetInstrumentAsync(string symbol, string category, CancellationToken ct)
    {
        var cacheKey = $"{category}:{symbol}";
        if (_instrumentCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var path = "/instrument/list";
        var query = new Dictionary<string, string>
        {
            ["symbols"] = symbol,
            ["category"] = category
        };

        var url = path + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddSignedHeaders(req, path, query, bodyJson: null);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var list = await resp.Content.ReadFromJsonAsync<List<WebullInstrumentResponse>>(SnakeJson, ct)
                   ?? throw new InvalidOperationException("Empty instrument response.");

        var first = list.FirstOrDefault(i => string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (first is null || string.IsNullOrWhiteSpace(first.InstrumentId))
            throw new InvalidOperationException($"Instrument not found for {symbol}.");

        var inst = new WebullInstrument(first.Symbol, first.InstrumentId, first.Name ?? "");
        _instrumentCache[cacheKey] = inst;
        return inst;
    }

    public async Task<WebullPlaceOrderResponse> PlaceStockOrderAsync(string accountId, WebullStockOrder order, CancellationToken ct)
    {
        var path = "/trade/order/place";
        var query = new Dictionary<string, string>();

        var bodyObj = new { account_id = accountId, stock_order = order };
        var bodyJson = JsonSerializer.Serialize(bodyObj, SnakeJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        AddSignedHeaders(req, path, query, bodyJson);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        var dto = JsonSerializer.Deserialize<WebullPlaceOrderResponseDto>(raw, SnakeJson)
                  ?? throw new InvalidOperationException("Empty place-order response.");

        var id = dto.ClientOrderId ?? order.ClientOrderId;
        return new WebullPlaceOrderResponse(id);
    }

    public async Task<WebullOrderDetail> QueryOrderDetailAsync(string accountId, string clientOrderId, CancellationToken ct)
    {
        var path = "/trade/order/detail";
        var query = new Dictionary<string, string>
        {
            ["account_id"] = accountId,
            ["client_order_id"] = clientOrderId
        };

        var url = path + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddSignedHeaders(req, path, query, bodyJson: null);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        var dto = JsonSerializer.Deserialize<WebullOrderDetailDto>(raw, SnakeJson)
                  ?? throw new InvalidOperationException("Empty order detail response.");

        var items = dto.Items?.Select(i => new WebullOrderItem(
            Symbol: i.Symbol ?? "",
            OrderStatus: i.OrderStatus ?? "",
            Side: i.Side ?? "",
            Qty: i.Qty ?? "0",
            FilledQty: i.FilledQty,
            FilledPrice: i.FilledPrice,
            LimitPrice: i.LimitPrice,
            StopPrice: i.StopPrice
        )).ToList() ?? new List<WebullOrderItem>();

        return new WebullOrderDetail(dto.ClientOrderId ?? clientOrderId, items);
    }

    public async Task<string> CancelOrderAsync(string accountId, string clientOrderId, CancellationToken ct)
    {
        var path = "/trade/order/cancel";
        var query = new Dictionary<string, string>();

        var bodyObj = new { account_id = accountId, client_order_id = clientOrderId };
        var bodyJson = JsonSerializer.Serialize(bodyObj, SnakeJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        AddSignedHeaders(req, path, query, bodyJson);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        var dto = JsonSerializer.Deserialize<WebullCancelOrderResponseDto>(raw, SnakeJson);
        return dto?.ClientOrderId ?? clientOrderId;
    }

    private void AddSignedHeaders(HttpRequestMessage req, string path, IReadOnlyDictionary<string, string> query, string? bodyJson)
    {
        var signed = WebullSigner.CreateSignedHeaders(_http.BaseAddress!, path, query, bodyJson, _opt.AppKey, _opt.AppSecret);

        req.Headers.TryAddWithoutValidation("x-app-key", signed.AppKey);
        req.Headers.TryAddWithoutValidation("x-timestamp", signed.Timestamp);
        req.Headers.TryAddWithoutValidation("x-signature-version", "1.0");
        req.Headers.TryAddWithoutValidation("x-signature-algorithm", "HMAC-SHA1");
        req.Headers.TryAddWithoutValidation("x-signature-nonce", signed.Nonce);
        req.Headers.TryAddWithoutValidation("x-signature", signed.Signature);
        req.Headers.Host = signed.Host;
    }

    private sealed record WebullInstrumentResponse(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("instrument_id")] string InstrumentId,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record WebullPlaceOrderResponseDto(
        [property: JsonPropertyName("client_order_id")] string? ClientOrderId);

    private sealed record WebullCancelOrderResponseDto(
        [property: JsonPropertyName("client_order_id")] string? ClientOrderId);

    private sealed record WebullOrderDetailDto(
        [property: JsonPropertyName("client_order_id")] string? ClientOrderId,
        [property: JsonPropertyName("items")] List<WebullOrderItemDto>? Items);

    private sealed record WebullOrderItemDto(
        [property: JsonPropertyName("symbol")] string? Symbol,
        [property: JsonPropertyName("order_status")] string? OrderStatus,
        [property: JsonPropertyName("side")] string? Side,
        [property: JsonPropertyName("qty")] string? Qty,
        [property: JsonPropertyName("filled_qty")] string? FilledQty,
        [property: JsonPropertyName("filled_price")] string? FilledPrice,
        [property: JsonPropertyName("limit_price")] string? LimitPrice,
        [property: JsonPropertyName("stop_price")] string? StopPrice);
}
