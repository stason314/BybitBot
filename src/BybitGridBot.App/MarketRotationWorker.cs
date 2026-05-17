namespace BybitGridBot.App;

public sealed class MarketRotationWorker : BackgroundService
{
    private static readonly TimeSpan DisabledPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ErrorBackoffInterval = TimeSpan.FromMinutes(1);
    private readonly ILogger<MarketRotationWorker> _logger;
    private readonly IRotationManagerService _rotationManagerService;

    public MarketRotationWorker(
        IRotationManagerService rotationManagerService,
        ILogger<MarketRotationWorker> logger)
    {
        _rotationManagerService = rotationManagerService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = await _rotationManagerService.GetStatusAsync(stoppingToken);
                if (!status.RotationEnabled)
                {
                    await Task.Delay(DisabledPollInterval, stoppingToken);
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                var interval = TimeSpan.FromMinutes(Math.Max(1, status.ScanIntervalMinutes));
                var nextScanAt = status.LastScanAt?.Add(interval) ?? now;
                if (nextScanAt > now)
                {
                    var delay = nextScanAt - now;
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                var result = await _rotationManagerService.RunOnceAsync(stoppingToken);
                _logger.LogInformation(
                    "Market rotation run completed. Success: {Success}. Scanned: {ScannedCount}. Activated: {ActivatedCount}. Message: {Message}",
                    result.Success,
                    result.ScannedCount,
                    result.ActivatedCount,
                    result.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Market rotation worker failed.");
                await Task.Delay(ErrorBackoffInterval, stoppingToken);
            }
        }
    }
}
