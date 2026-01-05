namespace Traidr.Core.MarketData;

public sealed record Bar(
    string Symbol,
    DateTime TimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

public sealed record Quote(
    string Symbol,
    decimal BidPrice,
    decimal AskPrice,
    long? BidSize = null,
    long? AskSize = null);

public sealed record DailyBar(
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

public sealed record Snapshot(
    string Symbol,
    decimal? LatestTradePrice,
    Quote? LatestQuote,
    DailyBar? DailyBar);
