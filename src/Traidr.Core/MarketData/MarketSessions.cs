namespace Traidr.Core.MarketData;

public enum MarketSessionMode
{
    Auto,
    Regular,
    PreMarket,
    AfterHours,
    Extended,
    All
}

public enum MarketSession
{
    Closed,
    PreMarket,
    Regular,
    AfterHours
}

public sealed record MarketSessionHours
{
    public TimeSpan PreMarketStartEt { get; init; } = new(4, 0, 0);
    public TimeSpan PreMarketEndEt { get; init; } = new(9, 30, 0);
    public TimeSpan RegularStartEt { get; init; } = new(9, 30, 0);
    public TimeSpan RegularEndEt { get; init; } = new(16, 0, 0);
    public TimeSpan AfterHoursStartEt { get; init; } = new(16, 0, 0);
    public TimeSpan AfterHoursEndEt { get; init; } = new(20, 0, 0);
}

public static class MarketSessionHelper
{
    public static MarketSession ResolveSession(DateTime utc, TimeZoneInfo marketTz, MarketSessionHours hours)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, marketTz);
        var tod = local.TimeOfDay;

        if (tod >= hours.PreMarketStartEt && tod < hours.PreMarketEndEt)
            return MarketSession.PreMarket;
        if (tod >= hours.RegularStartEt && tod < hours.RegularEndEt)
            return MarketSession.Regular;
        if (tod >= hours.AfterHoursStartEt && tod < hours.AfterHoursEndEt)
            return MarketSession.AfterHours;

        return MarketSession.Closed;
    }

    public static bool IsInSession(DateTime utc, TimeZoneInfo marketTz, MarketSessionMode mode, MarketSessionHours hours)
    {
        if (mode == MarketSessionMode.All)
            return true;

        var session = ResolveSession(utc, marketTz, hours);
        return mode switch
        {
            MarketSessionMode.Auto => session != MarketSession.Closed,
            MarketSessionMode.Regular => session == MarketSession.Regular,
            MarketSessionMode.PreMarket => session == MarketSession.PreMarket,
            MarketSessionMode.AfterHours => session == MarketSession.AfterHours,
            MarketSessionMode.Extended => session is MarketSession.PreMarket or MarketSession.AfterHours,
            _ => false
        };
    }
}
