namespace Traidr.Core.MarketData;

public sealed class NullMarketMetadataProvider : IMarketMetadataProvider
{
    public bool TryGetFloatShares(string symbol, out long floatShares)
    {
        floatShares = 0;
        return false;
    }

    public bool TryGetShortInterestPct(string symbol, out decimal shortInterestPct)
    {
        shortInterestPct = 0m;
        return false;
    }

    public bool TryHasNews(string symbol, out bool hasNews)
    {
        hasNews = false;
        return false;
    }
}
