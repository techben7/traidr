using System.Net.Http.Json;
using System.Text.Json;
using Traidr.Core.Scanning;

namespace Traidr.Core.Llm;

public sealed record LlmProxyOptions
{
    public string BaseUrl { get; init; } = ""; // e.g. https://your-proxy/score
    public string ProxyKey { get; init; } = "";
    public bool Enabled { get; init; } = false;
    public string? SystemPromptOverride { get; init; } = null;
}

public interface ILlmScorer
{
    Task<LlmScoreResponse> ScoreAsync(IReadOnlyList<SetupCandidate> candidates, CancellationToken ct = default);
}

public sealed class LlmProxyClient : ILlmScorer
{
    private readonly HttpClient _http;
    private readonly LlmProxyOptions _opt;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LlmProxyClient(HttpClient http, LlmProxyOptions opt)
    {
        _http = http;
        _opt = opt;
    }

    public async Task<LlmScoreResponse> ScoreAsync(IReadOnlyList<SetupCandidate> candidates, CancellationToken ct = default)
    {
        if (!_opt.Enabled || string.IsNullOrWhiteSpace(_opt.BaseUrl))
        {
            // fallback: everything Watch with score 50
            return new LlmScoreResponse(candidates.Select(c =>
                new LlmCandidateScore(c.Symbol, LlmTradeAction.Watch, 50m, c.EntryPrice, c.StopPrice, null, "LLM disabled; default Watch.")).ToArray());
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.BaseUrl);
        if (!string.IsNullOrWhiteSpace(_opt.ProxyKey))
            req.Headers.TryAddWithoutValidation("X-Proxy-Key", _opt.ProxyKey);

        req.Content = JsonContent.Create(new LlmScoreRequest(_opt.SystemPromptOverride, candidates), options: Json);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM proxy error {(int)resp.StatusCode}: {body}");

        var parsed = JsonSerializer.Deserialize<LlmScoreResponse>(body, Json)
                     ?? throw new InvalidOperationException("Failed to parse LLM proxy response.");

        return parsed;
    }
}
