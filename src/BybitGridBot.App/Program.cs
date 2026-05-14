using BybitGridBot.App;
using BybitGridBot.Bybit;
using BybitGridBot.Notifications;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

var webPort = int.TryParse(builder.Configuration["WEB_PORT"], out var configuredWebPort) ? configuredWebPort : 8080;
builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");

var logFilePath = Directory.Exists("/app/logs") ? "/app/logs/bot-.log" : Path.Combine("logs", "bot-.log");
Directory.CreateDirectory(Path.GetDirectoryName(logFilePath) ?? "logs");

builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .MinimumLevel.Is(ParseLevel(context.Configuration["LOG_LEVEL"]))
        .WriteTo.Console()
        .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);
});

builder.Services.AddOptions<AppOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<BybitOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<GridOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<RiskOptions>().Bind(builder.Configuration);
builder.Services.AddOptions<TelegramOptions>().Bind(builder.Configuration);

builder.Services.AddSingleton<BybitSigner>();
builder.Services.AddSingleton<IGridTradingStrategy, GridStrategy>();
builder.Services.AddSingleton<DcaStrategy>();
builder.Services.AddSingleton<BtdStrategy>();
builder.Services.AddSingleton<ITradingStrategy>(serviceProvider => serviceProvider.GetRequiredService<IGridTradingStrategy>());
builder.Services.AddSingleton<RiskManager>();
builder.Services.AddSingleton<MarketRegimeFilter>();
builder.Services.AddSingleton<MarketRegimeAnalyzer>();
builder.Services.AddSingleton<AutoStrategySelector>();
builder.Services.AddSingleton<IGridDashboardService, GridDashboardService>();

builder.Services.AddHttpClient<IBybitRestClient, BybitRestClient>(client =>
{
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

var app = builder.Build();

await app.Services.GetRequiredService<IGridRepository>().InitializeAsync(CancellationToken.None);

app.MapGet("/", (IGridDashboardService dashboardService) =>
    Results.Content(dashboardService.RenderDashboardPage(), "text/html; charset=utf-8"));

app.MapGet("/api/dashboard", async (string? symbol, IGridDashboardService dashboardService, CancellationToken cancellationToken) =>
    Results.Ok(await dashboardService.GetDashboardAsync(symbol, cancellationToken)));

app.MapPost("/api/settings", async (UpdateSettingsRequest request, IGridDashboardService dashboardService, CancellationToken cancellationToken) =>
{
    var response = await dashboardService.UpdateSettingsAsync(request, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/settings/apply-auto", async (string? symbol, IGridDashboardService dashboardService, CancellationToken cancellationToken) =>
{
    var response = await dashboardService.ApplyAutoRecommendationAsync(symbol, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapDelete("/api/settings/{symbol}", async (string symbol, IGridDashboardService dashboardService, CancellationToken cancellationToken) =>
{
    var response = await dashboardService.DeleteSettingsAsync(symbol, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/resume", async (string? symbol, IGridDashboardService dashboardService, CancellationToken cancellationToken) =>
{
    var response = await dashboardService.ResumeTradingAsync(symbol, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/orders/cancel-active", async (string? symbol, IGridDashboardService dashboardService, CancellationToken cancellationToken) =>
{
    var response = await dashboardService.CancelActiveOrdersAsync(symbol, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }));

await app.RunAsync();

static LogEventLevel ParseLevel(string? value)
{
    return Enum.TryParse<LogEventLevel>(value, true, out var level)
        ? level
        : LogEventLevel.Information;
}
