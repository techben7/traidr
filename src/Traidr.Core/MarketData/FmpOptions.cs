namespace Traidr.Core.MarketData;

public sealed record FmpOptions
{
    public string BaseUrl { get; init; } = "https://financialmodelingprep.com";
    public string ApiKey { get; init; } = "";
}
