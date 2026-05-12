using BybitGridBot.App;
using BybitGridBot.Bybit;
using BybitGridBot.Notifications;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

var logFilePath = Directory.Exists("/app/logs") ? "/app/logs/bot-.log" : Path.Combine("logs", "bot-.log");
Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? "logs");

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .MinimumLevel.Is(ParseLevel(builder.Configuration["LOG_LEVEL"]))
    .WriteTo.Console()
    .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

builder.Services.AddSerilog();

builder.Services.AddOptions<AppOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<BybitOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<GridOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<RiskOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<TelegramOptions>().Bind(builder.Configuration);

builder.Services.AddSingleton<BybitSigner>();
builder.Services.AddSingleton<GridStrategy>();
builder.Services.AddSingleton<RiskManager>();
builder.Services.AddSingleton<MarketRegimeFilter>();

builder.Services.AddHttpClient<IBybitRestClient, BybitRestClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<BybitOptions>>().Value;
    client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<ITelegramNotifier, TelegramNotifier>(client =>
{
    client.BaseAddress = new Uri("https://api.telegram.org/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddSingleton<IGridRepository>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<AppOptions>>().Value;
    var logger = serviceProvider.GetRequiredService<ILogger<SqliteGridRepository>>();
    return new SqliteGridRepository(options.SqlitePath, logger);
});

builder.Services.AddHostedService<GridBotWorker>();

await builder.Build().RunAsync();

static LogEventLevel ParseLevel(string? value)
{
    return Enum.TryParse<LogEventLevel>(value, true, out var level)
        ? level
        : LogEventLevel.Information;
}
