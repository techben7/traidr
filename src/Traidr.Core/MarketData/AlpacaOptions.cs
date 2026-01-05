namespace Traidr.Core.MarketData;

public sealed record AlpacaOptions
{
    public string DataBaseUrl { get; init; } = "https://data.alpaca.markets";
    public string ApiKey { get; init; } = "";
    public string ApiSecret { get; init; } = "";

    // Alpaca data feed: "sip" or "iex" (subscription-dependent)
    public string Feed { get; init; } = "iex";

    // Optional: if you don't have top gainers endpoint access, set false and it will return empty.
    public bool EnableTopGainers { get; init; } = true;
}
