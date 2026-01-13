using System.Collections.Concurrent;

namespace Traidr.Core.MarketData;

public sealed class FmpMarketMetadataProvider : IMarketMetadataProvider
{
    private readonly FinancialModelingPrepClient _client;
    private readonly ConcurrentDictionary<string, long?> _floatShares = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, decimal?> _shortInterestPct = new(StringComparer.OrdinalIgnoreCase);

    public FmpMarketMetadataProvider(FinancialModelingPrepClient client)
    {
        _client = client;
    }

    public bool TryGetFloatShares(string symbol, out long floatShares)
    {
        if (_floatShares.TryGetValue(symbol, out var cached) && cached.HasValue)
        {
            floatShares = cached.Value;
            return true;
        }

        try
        {
            var result = _client.GetFloatSharesAsync(symbol).GetAwaiter().GetResult();
            _floatShares[symbol] = result;
            if (result.HasValue)
            {
                floatShares = result.Value;
                return true;
            }
        }
        catch
        {
            _floatShares[symbol] = null;
        }

        floatShares = 0;
        return false;
    }

    public bool TryGetShortInterestPct(string symbol, out decimal shortInterestPct)
    {
        if (_shortInterestPct.TryGetValue(symbol, out var cached) && cached.HasValue)
        {
            shortInterestPct = cached.Value;
            return true;
        }

        try
        {
            var result = _client.GetShortInterestPctAsync(symbol).GetAwaiter().GetResult();
            _shortInterestPct[symbol] = result;
            if (result.HasValue)
            {
                shortInterestPct = result.Value;
                return true;
            }
        }
        catch
        {
            _shortInterestPct[symbol] = null;
        }

        shortInterestPct = 0m;
        return false;
    }

    public bool TryHasNews(string symbol, out bool hasNews)
    {
        hasNews = false;
        return false;
    }
}
