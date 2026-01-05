namespace Traidr.Core.MarketData;

public interface IMarketDataClient
{
    Task<IReadOnlyDictionary<string, Snapshot>> GetSnapshotsAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, Quote>> GetLatestQuotesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        IReadOnlyList<string> symbols,
        DateTime fromUtc,
        DateTime toUtc,
        string timeframe,
        CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> GetHistoricalDailyBarsAsync(
        IReadOnlyList<string> symbols,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetTopGainersAsync(int top, CancellationToken ct = default);
}
