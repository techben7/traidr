namespace Traidr.Core.Brokers.Webull;

public sealed record WebullOpenApiOptions
{
    public string Endpoint { get; init; } = "https://api.webull.com";
    public string AppKey { get; init; } = "";
    public string AppSecret { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string DefaultCategory { get; init; } = "US_STOCK";
}

public sealed record WebullExecutionOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan FillTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public bool MonitorExitsAndCancelOther { get; init; } = true;
    public TimeSpan ExitMonitorTimeout { get; init; } = TimeSpan.FromMinutes(90);

    public bool PlaceStopLossAfterFill { get; init; } = true;
    public bool PlaceTakeProfitAfterFill { get; init; } = true;

    public string StopExitOrderType { get; init; } = "STOP_LOSS";
    public bool CancelEntryOnTimeout { get; init; } = true;

    public bool RequireStopLoss { get; init; } = true;
    public bool PanicMarketExitOnStopFailure { get; init; } = true;

    public bool VerifyStopSubmitted { get; init; } = true;
    public TimeSpan StopSubmitVerifyTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StopSubmitVerifyPollInterval { get; init; } = TimeSpan.FromSeconds(3);
}
