namespace Traidr.Core.MarketData;

public interface IMarketMetadataProvider
{
    bool TryGetFloatShares(string symbol, out long floatShares);
    bool TryGetShortInterestPct(string symbol, out decimal shortInterestPct);
    bool TryHasNews(string symbol, out bool hasNews);
}
