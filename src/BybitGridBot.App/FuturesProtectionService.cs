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
        if (position.Size <= 0m || position.EntryPrice <= 0m)
        {
            return;
        }

        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(settings.Category, settings.Symbol, cancellationToken);
        var stopLoss = ResolveStopLoss(settings, position, instrument);
        var takeProfit = ResolveTakeProfit(settings, position, instrument);
        if (stopLoss <= 0m || takeProfit <= 0m)
        {
            await RecordDecisionAsync("ProtectionFailed", settings.Symbol, false, "Cannot verify futures protective stop: resolved SL/TP is invalid.", cancellationToken);
            return;
        }

        var protectionKey = string.Join(
            ':',
            _appOptions.TradingMode,
            settings.Symbol,
            position.PositionIdx,
            position.Side,
            position.Size.ToString(CultureInfo.InvariantCulture),
            position.EntryPrice.ToString(CultureInfo.InvariantCulture),
            stopLoss.ToString(CultureInfo.InvariantCulture),
            takeProfit.ToString(CultureInfo.InvariantCulture));
        var protectionKeyCached = _appliedProtectionKeys.TryGetValue(settings.Symbol, out var existingKey) &&
            string.Equals(existingKey, protectionKey, StringComparison.Ordinal);

        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            if (protectionKeyCached)
            {
                return;
            }

            _appliedProtectionKeys[settings.Symbol] = protectionKey;
            await RecordDecisionAsync(
                "ProtectionVerify",
                settings.Symbol,
                true,
                $"Paper protective SL/TP verified. Expected StopLoss={stopLoss}, TakeProfit={takeProfit}.",
                cancellationToken);
            return;
        }

        if (_appOptions.TradingMode == TradingMode.Mainnet)
        {
            await RecordDecisionAsync("ProtectionFailed", settings.Symbol, false, "Mainnet protective SL/TP restore is blocked until the mainnet checklist is complete.", cancellationToken);
            throw new InvalidOperationException("Mainnet protective SL/TP restore is blocked until the mainnet checklist is complete.");
        }

        try
        {
            var remotePosition = await _bybitRestClient.GetPositionAsync(settings.Category, settings.Symbol, cancellationToken);
            if (remotePosition is null || remotePosition.Size <= 0m)
            {
                await RecordDecisionAsync("ProtectionFailed", settings.Symbol, false, "Cannot verify futures protective stop: Bybit position is unavailable.", cancellationToken);
                return;
            }

            if (Matches(remotePosition.StopLossPrice, stopLoss, instrument.TickSize) &&
                Matches(remotePosition.TakeProfitPrice, takeProfit, instrument.TickSize))
            {
                _appliedProtectionKeys[settings.Symbol] = protectionKey;
                if (protectionKeyCached)
                {
                    return;
                }

                await RecordDecisionAsync(
                    "ProtectionVerify",
                    settings.Symbol,
                    true,
                    $"Protective SL/TP verified. StopLoss={remotePosition.StopLossPrice}, TakeProfit={remotePosition.TakeProfitPrice}.",
                    cancellationToken);
                return;
            }

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
                "ProtectionRestore",
                settings.Symbol,
                true,
                $"Protective SL/TP restored. Expected StopLoss={stopLoss}, TakeProfit={takeProfit}. Previous StopLoss={remotePosition.StopLossPrice}, TakeProfit={remotePosition.TakeProfitPrice}.",
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
                "ProtectionFailed",
                settings.Symbol,
                false,
                $"Failed to restore futures protective SL/TP: {exception.Message}",
                cancellationToken);
            throw;
        }
    }

    private Task RecordDecisionAsync(string source, string symbol, bool isAllowed, string reason, CancellationToken cancellationToken) =>
        _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = source,
            IsAllowed = isAllowed,
            Reason = reason,
            Severity = (isAllowed ? RiskSeverity.Info : RiskSeverity.Critical).ToString(),
            SuggestedAction = (isAllowed ? RiskSuggestedAction.Allow : RiskSuggestedAction.PauseBot).ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

    private static decimal ResolveStopLoss(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        BybitInstrumentInfo instrument) =>
        IsShort(position.Side)
            ? instrument.RoundPrice(position.EntryPrice * (1m + settings.StopLossPercent / 100m))
            : instrument.RoundPrice(position.EntryPrice * (1m - settings.StopLossPercent / 100m));

    private static decimal ResolveTakeProfit(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        BybitInstrumentInfo instrument) =>
        IsShort(position.Side)
            ? instrument.RoundPrice(position.EntryPrice * (1m - settings.TakeProfitPercent / 100m))
            : instrument.RoundPrice(position.EntryPrice * (1m + settings.TakeProfitPercent / 100m));

    private static bool Matches(decimal actual, decimal expected, decimal tickSize)
    {
        if (actual <= 0m || expected <= 0m)
        {
            return false;
        }

        var tolerance = tickSize > 0m ? tickSize : 0.00000001m;
        return Math.Abs(actual - expected) <= tolerance;
    }

    private static bool IsShort(string side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase);

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.####################", CultureInfo.InvariantCulture);
}
