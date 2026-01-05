using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public sealed class UniverseBuilder
{
    private readonly IMarketDataClient _marketData;

    public UniverseBuilder(IMarketDataClient marketData) => _marketData = marketData;

    public async Task<IReadOnlyList<string>> BuildUniverseAsync(
        IReadOnlyList<string> watchlist,
        int topGainersCount,
        CancellationToken ct = default)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in watchlist) if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim().ToUpperInvariant());

        if (topGainersCount > 0)
        {
            var movers = await _marketData.GetTopGainersAsync(topGainersCount, ct);
            foreach (var m in movers) if (!string.IsNullOrWhiteSpace(m)) set.Add(m.Trim().ToUpperInvariant());
        }

        return set.OrderBy(x => x).ToArray();
    }
}
