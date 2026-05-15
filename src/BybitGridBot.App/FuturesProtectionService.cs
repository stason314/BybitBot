using System.Collections.Concurrent;
using System.Globalization;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class FuturesProtectionService
{
    private readonly AppOptions _appOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly ILogger<FuturesProtectionService> _logger;
    private readonly IGridRepository _repository;
    private readonly ConcurrentDictionary<string, string> _appliedProtectionKeys = new(StringComparer.OrdinalIgnoreCase);

    public FuturesProtectionService(
        IOptions<AppOptions> appOptions,
        IBybitRestClient bybitRestClient,
        IGridRepository repository,
        ILogger<FuturesProtectionService> logger)
    {
        _appOptions = appOptions.Value;
        _bybitRestClient = bybitRestClient;
        _repository = repository;
        _logger = logger;
    }

    public async Task EnsureProtectiveStopAsync(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode == TradingMode.Paper || position.Size <= 0m || position.EntryPrice <= 0m)
        {
            return;
        }

        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(settings.Category, settings.Symbol, cancellationToken);
        var stopLoss = instrument.RoundPrice(position.EntryPrice * (1m - settings.StopLossPercent / 100m));
        var takeProfit = instrument.RoundPrice(position.EntryPrice * (1m + settings.TakeProfitPercent / 100m));
        if (stopLoss <= 0m || takeProfit <= 0m)
        {
            await RecordDecisionAsync(settings.Symbol, false, "Cannot restore futures protective stop: resolved SL/TP is invalid.", cancellationToken);
            return;
        }

        var protectionKey = string.Join(
            ':',
            settings.Symbol,
            position.PositionIdx,
            position.Size.ToString(CultureInfo.InvariantCulture),
            position.EntryPrice.ToString(CultureInfo.InvariantCulture),
            stopLoss.ToString(CultureInfo.InvariantCulture),
            takeProfit.ToString(CultureInfo.InvariantCulture));
        if (_appliedProtectionKeys.TryGetValue(settings.Symbol, out var existingKey) &&
            string.Equals(existingKey, protectionKey, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _bybitRestClient.SetTradingStopAsync(new BybitSetTradingStopRequest
            {
                Category = settings.Category,
                Symbol = settings.Symbol,
                PositionIdx = position.PositionIdx,
                StopLoss = FormatDecimal(stopLoss),
                TakeProfit = FormatDecimal(takeProfit),
                StopLossTriggerBy = "MarkPrice",
                TakeProfitTriggerBy = "MarkPrice",
                TakeProfitStopLossMode = "Full"
            }, cancellationToken);
            _appliedProtectionKeys[settings.Symbol] = protectionKey;
            await RecordDecisionAsync(
                settings.Symbol,
                true,
                $"Protective SL/TP restored. StopLoss={stopLoss}, TakeProfit={takeProfit}.",
                cancellationToken);
            _logger.LogInformation(
                "Futures protective SL/TP restored. Symbol: {Symbol}, StopLoss: {StopLoss}, TakeProfit: {TakeProfit}",
                settings.Symbol,
                stopLoss,
                takeProfit);
        }
        catch (Exception exception)
        {
            await RecordDecisionAsync(
                settings.Symbol,
                false,
                $"Failed to restore futures protective SL/TP: {exception.Message}",
                cancellationToken);
            throw;
        }
    }

    private Task RecordDecisionAsync(string symbol, bool isAllowed, string reason, CancellationToken cancellationToken) =>
        _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = "ProtectiveStop",
            IsAllowed = isAllowed,
            Reason = reason,
            Severity = (isAllowed ? RiskSeverity.Info : RiskSeverity.Critical).ToString(),
            SuggestedAction = (isAllowed ? RiskSuggestedAction.Allow : RiskSuggestedAction.PauseBot).ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.####################", CultureInfo.InvariantCulture);
}
