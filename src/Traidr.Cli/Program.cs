using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Traidr.Core.Brokers.Webull;
using Traidr.Core.Backtesting;
using Traidr.Core.Indicators;
using Traidr.Core.Llm;
using Traidr.Core.MarketData;
using Traidr.Core.Scanning;
using Traidr.Core.Trading;
using Traidr.Cli;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();

services.AddSingleton<IConfiguration>(configuration);

services.AddLogging(b =>
{
    b.ClearProviders();
    b.AddProvider(new ColorConsoleLoggerProvider());
    b.SetMinimumLevel(LogLevel.Information);
});

services.AddHttpClient(); // default factory

// Options
services.AddSingleton(configuration.GetSection("MarketData").Get<AlpacaOptions>() ?? new AlpacaOptions());
services.AddSingleton(configuration.GetSection("PreFilter").Get<PreFilterOptions>() ?? new PreFilterOptions());
services.AddSingleton(configuration.GetSection("Indicators").Get<IndicatorCalculatorOptions>() ?? new IndicatorCalculatorOptions());
services.AddSingleton(configuration.GetSection("TraidrScanner").Get<TraidrScannerOptions>() ?? new TraidrScannerOptions());
services.AddSingleton(configuration.GetSection("Llm").Get<LlmProxyOptions>() ?? new LlmProxyOptions());
services.AddSingleton(configuration.GetSection("Risk").Get<RiskManagerOptions>() ?? new RiskManagerOptions());
services.AddSingleton(configuration.GetSection("Webull").Get<WebullOpenApiOptions>() ?? new WebullOpenApiOptions());
services.AddSingleton(configuration.GetSection("WebullExecution").Get<WebullExecutionOptions>() ?? new WebullExecutionOptions());

// Core services
services.AddSingleton<IMarketDataClient>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var http = httpFactory.CreateClient("alpaca-data");
    var opt = sp.GetRequiredService<AlpacaOptions>();
    return new AlpacaMarketDataClient(http, opt);
});

services.AddSingleton<IUniversePreFilter, UniversePreFilter>();
services.AddSingleton(sp => new IndicatorCalculator(sp.GetRequiredService<IndicatorCalculatorOptions>()));
services.AddSingleton(sp => new TraidrScanner(sp.GetRequiredService<IndicatorCalculator>(), sp.GetRequiredService<TraidrScannerOptions>()));
services.AddSingleton<UniverseBuilder>();
services.AddSingleton<BacktestEngine>();
services.AddSingleton<IRiskState, InMemoryRiskState>();
services.AddSingleton<IRiskManager>(sp => new RiskManager(sp.GetRequiredService<IRiskState>(), sp.GetRequiredService<RiskManagerOptions>()));

services.AddSingleton<ILlmScorer>(sp =>
{
    var opt = sp.GetRequiredService<LlmProxyOptions>();
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var http = httpFactory.CreateClient("llm-proxy");
    return new LlmProxyClient(http, opt);
});

// Webull client + executor
services.AddSingleton<IWebullOpenApiClient>(sp =>
{
    var opt = sp.GetRequiredService<WebullOpenApiOptions>();
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var http = httpFactory.CreateClient("webull");
    return new WebullOpenApiClient(http, opt);
});

services.AddSingleton<IOrderExecutor>(sp =>
{
    var execMode = configuration.GetValue<string>("Execution:Mode") ?? "Paper";
    var riskState = sp.GetRequiredService<IRiskState>();

    if (string.Equals(execMode, "Webull", StringComparison.OrdinalIgnoreCase))
    {
        return new WebullPollingOrderExecutor(
            sp.GetRequiredService<IWebullOpenApiClient>(),
            sp.GetRequiredService<WebullOpenApiOptions>(),
            sp.GetRequiredService<WebullExecutionOptions>(),
            riskState);
    }

    return new PaperOrderExecutor(riskState);
});

services.AddSingleton<Traidr.Cli.Runner>();
services.AddSingleton<Traidr.Cli.BacktestCommand>();
services.AddSingleton<Traidr.Cli.OptimizeCommand>();

var provider = services.BuildServiceProvider();
var log = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Main");

var runner = provider.GetRequiredService<Traidr.Cli.Runner>();

// Command routing
if (args.Length > 0 && string.Equals(args[0], "backtest", StringComparison.OrdinalIgnoreCase))
{
    var cmd = provider.GetRequiredService<Traidr.Cli.BacktestCommand>();
    using var cts0 = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts0.Cancel(); };
    await cmd.RunAsync(args, cts0.Token);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "optimize", StringComparison.OrdinalIgnoreCase))
{
    var cmd = provider.GetRequiredService<Traidr.Cli.OptimizeCommand>();
    using var cts1 = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts1.Cancel(); };
    await cmd.RunAsync(args, cts1.Token);
    return;
}

var runLoop = configuration.GetValue<bool>("Execution:RunLoop");
var delaySeconds = configuration.GetValue<int>("Execution:LoopDelaySeconds");

if (!runLoop)
{
    log.LogGreen("Starting Traidr! Ctrl+C to stop.");
    await runner.RunOnceAsync();
    return;
}

log.LogGreen("Starting Traidr loop mode! Ctrl+C to stop.");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    try
    {
        await runner.RunOnceAsync(cts.Token);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "RunOnce failed.");
    }

    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, delaySeconds)), cts.Token);
}
