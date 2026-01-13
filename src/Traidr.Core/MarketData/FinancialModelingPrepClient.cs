using System.Text.Json;
using System.Text.Json.Serialization;
using Traidr.Core.Scanning;

namespace Traidr.Core.MarketData;

public sealed class FinancialModelingPrepClient
{
    private readonly HttpClient _http;
    private readonly FmpOptions _opt;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public FinancialModelingPrepClient(HttpClient http, FmpOptions opt)
    {
        _http = http;
        _opt = opt;
        _http.BaseAddress = new Uri(_opt.BaseUrl);
    }

    public async Task<IReadOnlyList<FmpScreenerRow>> ScreenStocksAsync(
        AutoUniverseOptions opt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            return Array.Empty<FmpScreenerRow>();

        var url = "/api/v3/stock-screener" +
                  $"?marketCapMoreThan={opt.MinMarketCap}" +
                  $"&marketCapLowerThan={opt.MaxMarketCap}" +
                  $"&priceMoreThan={opt.MinPrice}" +
                  $"&priceLowerThan={opt.MaxPrice}" +
                  $"&limit={opt.ScreenerLimit}" +
                  $"&apikey={Uri.EscapeDataString(_opt.ApiKey)}";

        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<FmpScreenerRow>();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var rows = JsonSerializer.Deserialize<List<FmpScreenerRow>>(raw, Json);
        return rows ?? Array.Empty<FmpScreenerRow>();
    }

    public async Task<long?> GetFloatSharesAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            return null;

        var url = $"/api/v4/shares_float?symbol={Uri.EscapeDataString(symbol)}&apikey={Uri.EscapeDataString(_opt.ApiKey)}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var raw = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var first = doc.RootElement[0];
        if (first.TryGetProperty("floatShares", out var floatShares) && floatShares.TryGetInt64(out var fs))
            return fs;
        if (first.TryGetProperty("sharesFloat", out var sharesFloat) && sharesFloat.TryGetInt64(out var sf))
            return sf;

        return null;
    }

    public async Task<decimal?> GetShortInterestPctAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            return null;

        var url = $"/api/v4/short_interest?symbol={Uri.EscapeDataString(symbol)}&apikey={Uri.EscapeDataString(_opt.ApiKey)}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var raw = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var first = doc.RootElement[0];
        if (first.TryGetProperty("shortFloat", out var shortFloat) && shortFloat.TryGetDecimal(out var pct))
            return pct / 100m;
        if (first.TryGetProperty("shortInterest", out var shortInterest) && shortInterest.TryGetDecimal(out var rawPct))
            return rawPct / 100m;

        return null;
    }
}

public sealed record FmpScreenerRow
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = "";

    [JsonPropertyName("price")]
    public decimal Price { get; init; }

    [JsonPropertyName("marketCap")]
    public decimal MarketCap { get; init; }

    [JsonPropertyName("volume")]
    public long Volume { get; init; }

    [JsonPropertyName("avgVolume")]
    public long AvgVolume { get; init; }
}
