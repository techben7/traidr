using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Traidr.Core.MarketData;

/// <summary>
/// Minimal Alpaca REST market data client (no Alpaca SDK dependency).
/// You must provide ALPACA_API_KEY and ALPACA_API_SECRET in config/environment.
/// </summary>
public sealed class AlpacaMarketDataClient : IMarketDataClient
{
    private readonly HttpClient _http;
    private readonly AlpacaOptions _opt;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AlpacaMarketDataClient(HttpClient http, AlpacaOptions opt)
    {
        _http = http;
        _opt = opt;
        _http.BaseAddress = new Uri(_opt.DataBaseUrl);

        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
        {
            // Alpaca uses these headers for auth:
            // APCA-API-KEY-ID / APCA-API-SECRET-KEY
            _http.DefaultRequestHeaders.Remove("APCA-API-KEY-ID");
            _http.DefaultRequestHeaders.Remove("APCA-API-SECRET-KEY");
            _http.DefaultRequestHeaders.Add("APCA-API-KEY-ID", _opt.ApiKey);
            _http.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", _opt.ApiSecret);
        }

        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyDictionary<string, Snapshot>> GetSnapshotsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default)
    {
        if (symbols.Count == 0) return new Dictionary<string, Snapshot>();

        var sym = string.Join(',', symbols.Select(Uri.EscapeDataString));
        var url = $"/v2/stocks/snapshots?symbols={sym}&feed={Uri.EscapeDataString(_opt.Feed)}";

        var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<AlpacaSnapshotsResponse>(raw, Json)
                  ?? throw new InvalidOperationException("Failed to parse snapshots response.");

        var dict = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, s) in dto.Snapshots)
        {
            Quote? q = null;
            if (s.LatestQuote is not null)
            {
                q = new Quote(symbol, s.LatestQuote.Bp, s.LatestQuote.Ap, s.LatestQuote.Bs, s.LatestQuote.As);
            }

            DailyBar? d = null;
            if (s.DailyBar is not null)
            {
                d = new DailyBar(s.DailyBar.O, s.DailyBar.H, s.DailyBar.L, s.DailyBar.C, s.DailyBar.V);
            }

            dict[symbol] = new Snapshot(
                Symbol: symbol,
                LatestTradePrice: s.LatestTrade?.P,
                LatestQuote: q,
                DailyBar: d
            );
        }

        return dict;
    }

    public async Task<IReadOnlyDictionary<string, Quote>> GetLatestQuotesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default)
    {
        // We can reuse snapshots for simplicity.
        var snaps = await GetSnapshotsAsync(symbols, ct);
        var dict = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sym, snap) in snaps)
        {
            if (snap.LatestQuote is not null)
                dict[sym] = snap.LatestQuote;
        }

        return dict;
    }

    public async Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        IReadOnlyList<string> symbols,
        DateTime fromUtc,
        DateTime toUtc,
        string timeframe,
        CancellationToken ct = default)
    {
        if (symbols.Count == 0) return Array.Empty<Bar>();

        // Alpaca v2 bars endpoint supports multi-symbol: /v2/stocks/bars
        // We request enough bars; Alpaca paginates with next_page_token.
        var sym = string.Join(',', symbols.Select(Uri.EscapeDataString));
        var start = Uri.EscapeDataString(fromUtc.ToString("O"));
        var end = Uri.EscapeDataString(toUtc.ToString("O"));
        var tf = Uri.EscapeDataString(timeframe);

        var bars = new List<Bar>(capacity: 4096);

        string? pageToken = null;
        for (var page = 0; page < 50; page++)
        {
            var tokenPart = pageToken is null ? "" : $"&page_token={Uri.EscapeDataString(pageToken)}";
            var url = $"/v2/stocks/bars?symbols={sym}&timeframe={tf}&start={start}&end={end}&adjustment=raw&feed={Uri.EscapeDataString(_opt.Feed)}&limit=10000{tokenPart}";

            var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<AlpacaBarsResponse>(raw, Json)
                      ?? throw new InvalidOperationException("Failed to parse bars response.");

            foreach (var (symbol, list) in dto.Bars)
            {
                foreach (var b in list)
                {
                    bars.Add(new Bar(
                        Symbol: symbol,
                        TimeUtc: b.T,
                        Open: b.O,
                        High: b.H,
                        Low: b.L,
                        Close: b.C,
                        Volume: b.V
                    ));
                }
            }

            if (string.IsNullOrWhiteSpace(dto.NextPageToken))
                break;

            pageToken = dto.NextPageToken;
        }

        return bars;
    }

    public Task<IReadOnlyList<Bar>> GetHistoricalDailyBarsAsync(
        IReadOnlyList<string> symbols,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
        => GetHistoricalBarsAsync(symbols, fromUtc, toUtc, "1Day", ct);

    public async Task<IReadOnlyList<string>> GetTopGainersAsync(int top, CancellationToken ct = default)
    {
        if (!_opt.EnableTopGainers) return Array.Empty<string>();

        // Alpaca has (beta) movers/screener endpoints in some plans.
        // If this endpoint is unavailable for your plan, it will throw; set EnableTopGainers=false.
        var url = $"/v1beta1/screener/stocks/movers?top={top}&direction=up";

        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<string>();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<AlpacaMoversResponse>(raw, Json);
        return dto?.Movers?.Select(m => m.Symbol).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
               ?? Array.Empty<string>();
    }

    // ---- DTOs ----
    private sealed class AlpacaSnapshotsResponse
    {
        [JsonPropertyName("snapshots")]
        public Dictionary<string, AlpacaSnapshotDto> Snapshots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AlpacaSnapshotDto
    {
        [JsonPropertyName("latestTrade")]
        public AlpacaTradeDto? LatestTrade { get; set; }

        [JsonPropertyName("latestQuote")]
        public AlpacaQuoteDto? LatestQuote { get; set; }

        [JsonPropertyName("dailyBar")]
        public AlpacaDailyBarDto? DailyBar { get; set; }
    }

    private sealed class AlpacaTradeDto
    {
        [JsonPropertyName("p")]
        public decimal P { get; set; }
    }

    private sealed class AlpacaQuoteDto
    {
        [JsonPropertyName("bp")]
        public decimal Bp { get; set; }
        [JsonPropertyName("ap")]
        public decimal Ap { get; set; }
        [JsonPropertyName("bs")]
        public long? Bs { get; set; }
        [JsonPropertyName("as")]
        public long? As { get; set; }
    }

    private sealed class AlpacaDailyBarDto
    {
        [JsonPropertyName("o")] public decimal O { get; set; }
        [JsonPropertyName("h")] public decimal H { get; set; }
        [JsonPropertyName("l")] public decimal L { get; set; }
        [JsonPropertyName("c")] public decimal C { get; set; }
        [JsonPropertyName("v")] public long V { get; set; }
    }

    private sealed class AlpacaBarsResponse
    {
        [JsonPropertyName("bars")]
        public Dictionary<string, List<AlpacaBarDto>> Bars { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("next_page_token")]
        public string? NextPageToken { get; set; }
    }

    private sealed class AlpacaBarDto
    {
        [JsonPropertyName("t")] public DateTime T { get; set; }
        [JsonPropertyName("o")] public decimal O { get; set; }
        [JsonPropertyName("h")] public decimal H { get; set; }
        [JsonPropertyName("l")] public decimal L { get; set; }
        [JsonPropertyName("c")] public decimal C { get; set; }
        [JsonPropertyName("v")] public long V { get; set; }
    }

    private sealed class AlpacaMoversResponse
    {
        [JsonPropertyName("movers")]
        public List<AlpacaMoverDto>? Movers { get; set; }
    }

    private sealed class AlpacaMoverDto
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";
    }
}
