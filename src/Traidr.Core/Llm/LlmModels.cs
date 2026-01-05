using System.Text.Json.Serialization;
using Traidr.Core.Scanning;

namespace Traidr.Core.Llm;

public enum LlmTradeAction { Skip, Watch, Trade }

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
