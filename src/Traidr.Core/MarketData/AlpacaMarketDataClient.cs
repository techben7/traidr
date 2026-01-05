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
        // var dto = JsonSerializer.Deserialize<AlpacaSnapshotsResponse>(raw, Json)
        //           ?? throw new InvalidOperationException("Failed to parse snapshots response.");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var snapshots = JsonSerializer.Deserialize<Dictionary<string, AlpacaStockSnapshotDto>>(raw, options);

        var dict = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);

        // foreach (var (symbol, s) in dto.Snapshots)
        foreach (var (symbol, s) in snapshots)
        {
            Quote? q = null;
            if (s.LatestQuote is not null)
            {
                q = new Quote(symbol, s.LatestQuote.BidPrice, s.LatestQuote.AskPrice, s.LatestQuote.BidSize, s.LatestQuote.AskSize);
            }

            DailyBar? d = null;
            if (s.DailyBar is not null)
            {
                d = new DailyBar(s.DailyBar.OpenPrice, s.DailyBar.HighPrice, s.DailyBar.LowPrice, s.DailyBar.ClosePrice, s.DailyBar.Volume);
            }

            dict[symbol] = new Snapshot(
                Symbol: symbol,
                LatestTradePrice: s.LatestTrade?.Price,
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
                        TimeUtc: b.TimestampUtc,
                        Open: b.OpenPrice,
                        High: b.HighPrice,
                        Low: b.LowPrice,
                        Close: b.ClosePrice,
                        Volume: b.Volume
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
        var url = $"/v1beta1/screener/stocks/movers?top={top}"; // &direction=up

        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<string>();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        var dto = JsonSerializer.Deserialize<AlpacaGainersResponse>(raw, Json);

        return dto?.Gainers?.Select(m => m.Symbol).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
               ?? Array.Empty<string>();
    }

    // ---- DTOs ----

    public class AlpacaStockSnapshotDto
    {
        [JsonPropertyName("dailyBar")]
        public AlpacaBarDto DailyBar { get; set; }

        [JsonPropertyName("latestQuote")]
        public AlpacaQuoteDto LatestQuote { get; set; }

        [JsonPropertyName("latestTrade")]
        public AlpacaTradeDto LatestTrade { get; set; }

        [JsonPropertyName("minuteBar")]
        public AlpacaBarDto MinuteBar { get; set; }

        [JsonPropertyName("prevDailyBar")]
        public AlpacaBarDto PreviousDailyBar { get; set; }
    }

    public class AlpacaBarDto
    {
        [JsonPropertyName("o")]
        public decimal OpenPrice { get; set; }

        [JsonPropertyName("h")]
        public decimal HighPrice { get; set; }

        [JsonPropertyName("l")]
        public decimal LowPrice { get; set; }

        [JsonPropertyName("c")]
        public decimal ClosePrice { get; set; }

        [JsonPropertyName("v")]
        public long Volume { get; set; }

        [JsonPropertyName("n")]
        public int TradeCount { get; set; }

        [JsonPropertyName("vw")]
        public decimal VolumeWeightedAveragePrice { get; set; }

        [JsonPropertyName("t")]
        public DateTime TimestampUtc { get; set; }
    }

    public class AlpacaQuoteDto
    {
        [JsonPropertyName("bp")]
        public decimal BidPrice { get; set; }

        [JsonPropertyName("bs")]
        public int BidSize { get; set; }

        [JsonPropertyName("bx")]
        public string BidExchange { get; set; }

        [JsonPropertyName("ap")]
        public decimal AskPrice { get; set; }

        [JsonPropertyName("as")]
        public int AskSize { get; set; }

        [JsonPropertyName("ax")]
        public string AskExchange { get; set; }

        [JsonPropertyName("c")]
        public List<string> Conditions { get; set; }

        [JsonPropertyName("t")]
        public DateTime TimestampUtc { get; set; }

        [JsonPropertyName("z")]
        public string Tape { get; set; }
    }

    public class AlpacaTradeDto
    {
        [JsonPropertyName("p")]
        public decimal Price { get; set; }

        [JsonPropertyName("s")]
        public int Size { get; set; }

        [JsonPropertyName("i")]
        public long TradeId { get; set; }

        [JsonPropertyName("x")]
        public string Exchange { get; set; }

        [JsonPropertyName("z")]
        public string Tape { get; set; }

        [JsonPropertyName("c")]
        public List<string> Conditions { get; set; }

        [JsonPropertyName("t")]
        public DateTime TimestampUtc { get; set; }
    }






    // private sealed class AlpacaSnapshotsResponse
    // {
    //     // [JsonPropertyName("snapshots")]
    //     public Dictionary<string, AlpacaSnapshotDto> Snapshots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    //     // Dictionary<string, AlpacaSnapshotDto>
    // }

    // private sealed class AlpacaSnapshotDto
    // {
    //     [JsonPropertyName("latestTrade")]
    //     public AlpacaTradeDto? LatestTrade { get; set; }

    //     [JsonPropertyName("latestQuote")]
    //     public AlpacaQuoteDto? LatestQuote { get; set; }

    //     [JsonPropertyName("dailyBar")]
    //     public AlpacaDailyBarDto? DailyBar { get; set; }

    //     // public DailyBarDto DailyBar { get; set; }
    //     // public LatestQuoteDto LatestQuote { get; set; }
    //     // public LatestTradeDto LatestTrade { get; set; }
    //     // public MinuteBarDto MinuteBar { get; set; }
    //     // public DailyBarDto PrevDailyBar { get; set; }
    // }

    // private sealed class AlpacaTradeDto
    // {
    //     [JsonPropertyName("p")]
    //     public decimal P { get; set; }
    // }

    // private sealed class AlpacaQuoteDto
    // {
    //     [JsonPropertyName("bp")]
    //     public decimal Bp { get; set; }
    //     [JsonPropertyName("ap")]
    //     public decimal Ap { get; set; }
    //     [JsonPropertyName("bs")]
    //     public long? Bs { get; set; }
    //     [JsonPropertyName("as")]
    //     public long? As { get; set; }
    // }

    // private sealed class AlpacaDailyBarDto
    // {
    //     [JsonPropertyName("o")] public decimal O { get; set; }
    //     [JsonPropertyName("h")] public decimal H { get; set; }
    //     [JsonPropertyName("l")] public decimal L { get; set; }
    //     [JsonPropertyName("c")] public decimal C { get; set; }
    //     [JsonPropertyName("v")] public long V { get; set; }
    // }

    private sealed class AlpacaBarsResponse
    {
        [JsonPropertyName("bars")]
        public Dictionary<string, List<AlpacaBarDto>> Bars { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("next_page_token")]
        public string? NextPageToken { get; set; }
    }

    // private sealed class AlpacaBarDto
    // {
    //     [JsonPropertyName("t")] public DateTime T { get; set; }
    //     [JsonPropertyName("o")] public decimal O { get; set; }
    //     [JsonPropertyName("h")] public decimal H { get; set; }
    //     [JsonPropertyName("l")] public decimal L { get; set; }
    //     [JsonPropertyName("c")] public decimal C { get; set; }
    //     [JsonPropertyName("v")] public long V { get; set; }
    // }

    private sealed class AlpacaGainersResponse
    {
        [JsonPropertyName("gainers")]
        public List<AlpacaGainerDto>? Gainers { get; set; }
    }

    private sealed class AlpacaGainerDto
    {
        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";
        [JsonPropertyName("change")]
        public decimal Change { get; set; } = 0;
        [JsonPropertyName("percent_change")]
        public decimal PercentChange { get; set; } = 0;
        [JsonPropertyName("price")]
        public decimal Price { get; set; } = 0;
    }
}