using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();

// TODO: bring back once build is working
// builder.Services.AddOpenApi();

var app = builder.Build();

// TODO: bring back once build is working
// app.MapOpenApi();

var proxyKey = app.Configuration["ProxyKey"] ?? "";

app.MapPost("/score", async (HttpRequest req, LlmScoreRequest body) =>
{
    if (!string.IsNullOrWhiteSpace(proxyKey))
    {
        if (!req.Headers.TryGetValue("X-Proxy-Key", out var provided) || provided != proxyKey)
            return Results.Unauthorized();
    }

    // This is a SAFE stub: it does NOT predict prices.
    // It just ranks setups based on the metrics you already computed.
    var scores = body.Candidates.Select(c =>
    {
        // heuristic: tighter range + bigger elephant + higher volume => higher score
        var tightness = Clamp01(1m - (c.RangePct / 0.01m)); // 1% range => 0
        var elephant = Clamp01((c.BodyToMedianBody - 1m) / 4m); // 5x body => near 1
        var vol = Clamp01((c.VolumeToAvgVolume - 1m) / 3m);

        var score = 100m * (0.45m * tightness + 0.35m * elephant + 0.20m * vol);

        var action = score >= 75m ? LlmTradeAction.Trade
                   : score >= 55m ? LlmTradeAction.Watch
                   : LlmTradeAction.Skip;

        // Optional take-profit: 2R
        var risk = Math.Abs(c.EntryPrice - c.StopPrice);
        decimal? tp = null;
        if (risk > 0m)
        {
            tp = c.Direction == BreakoutDirection.Long
                ? c.EntryPrice + (2m * risk)
                : c.EntryPrice - (2m * risk);
        }

        return new LlmCandidateScore(
            Symbol: c.Symbol,
            Action: action,
            Score: decimal.Round(score, 2),
            EntryPrice: c.EntryPrice,
            StopPrice: c.StopPrice,
            TakeProfitPrice: tp,
            Reason: $"Stub scorer: tight={tightness:F2}, elephant={elephant:F2}, vol={vol:F2}"
        );
    }).ToList();

    return Results.Ok(new LlmScoreResponse(scores));
})
.WithName("ScoreCandidates");

app.Run("http://localhost:5111");

// ---- models ----
static decimal Clamp01(decimal x) => x < 0m ? 0m : (x > 1m ? 1m : x);

public enum BreakoutDirection { Long, Short }
public enum LlmTradeAction { Skip, Watch, Trade }

public sealed record SetupCandidate(
    string Symbol,
    BreakoutDirection Direction,
    decimal EntryPrice,
    decimal StopPrice,
    decimal ConsolidationHigh,
    decimal ConsolidationLow,
    decimal RangePct,
    decimal AtrPct,
    decimal BodyToMedianBody,
    decimal VolumeToAvgVolume,
    decimal? Ema20,
    decimal? Ema200,
    decimal? Vwap,
    decimal? Atr14,
    DateTime ElephantBarTimeUtc);

public sealed record LlmScoreRequest(
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("candidates")] IReadOnlyList<SetupCandidate> Candidates);

public sealed record LlmCandidateScore(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("action")] LlmTradeAction Action,
    [property: JsonPropertyName("score")] decimal Score,
    [property: JsonPropertyName("entryPrice")] decimal? EntryPrice,
    [property: JsonPropertyName("stopPrice")] decimal? StopPrice,
    [property: JsonPropertyName("takeProfitPrice")] decimal? TakeProfitPrice,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record LlmScoreResponse(
    [property: JsonPropertyName("scores")] IReadOnlyList<LlmCandidateScore> Scores);
