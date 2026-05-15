using System.Globalization;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public sealed class FuturesPreflightService
{
    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesOptions _futuresOptions;
    private readonly ILogger<FuturesPreflightService> _logger;
    private readonly IGridRepository _repository;
    private readonly HashSet<string> _configuredSymbols = new(StringComparer.OrdinalIgnoreCase);

    public FuturesPreflightService(
        IOptions<FuturesOptions> futuresOptions,
        IBybitRestClient bybitRestClient,
        IGridRepository repository,
        ILogger<FuturesPreflightService> logger)
    {
        _futuresOptions = futuresOptions.Value;
        _bybitRestClient = bybitRestClient;
        _repository = repository;
        _logger = logger;
    }

    public async Task EnsureTestnetReadyAsync(
        FuturesBotSettings settings,
        BybitInstrumentInfo instrument,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateSettings(settings);
            ValidateInstrumentRules(instrument);
            var setupKey = $"{settings.Symbol}:{settings.MarginMode}:{settings.PositionMode}:{settings.Leverage}";
            if (!_configuredSymbols.Add(setupKey))
            {
                return;
            }

            var leverage = FormatDecimal(settings.Leverage);
            await _bybitRestClient.SwitchPositionModeAsync(new BybitSwitchPositionModeRequest
            {
                Category = settings.Category,
                Symbol = settings.Symbol,
                Mode = 0
            }, cancellationToken);
            await _bybitRestClient.SwitchIsolatedMarginAsync(new BybitSwitchIsolatedMarginRequest
            {
                Category = settings.Category,
                Symbol = settings.Symbol,
                TradeMode = 1,
                BuyLeverage = leverage,
                SellLeverage = leverage
            }, cancellationToken);
            await _bybitRestClient.SetLeverageAsync(new BybitSetLeverageRequest
            {
                Category = settings.Category,
                Symbol = settings.Symbol,
                BuyLeverage = leverage,
                SellLeverage = leverage
            }, cancellationToken);

            var position = await _bybitRestClient.GetPositionAsync(settings.Category, settings.Symbol, cancellationToken);
            if (position is not null)
            {
                if (position.PositionIdx != 0)
                {
                    throw new InvalidOperationException("Futures pre-flight requires one-way positionIdx=0.");
                }

                if (position.Size > 0m && string.Equals(position.Side, "Sell", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Futures pre-flight does not allow an existing short position.");
                }

                if (position.Size > 0m && position.TradeMode != 1)
                {
                    throw new InvalidOperationException("Futures pre-flight requires isolated margin for the existing position.");
                }

                if (position.Leverage > 0m && position.Leverage != settings.Leverage)
                {
                    throw new InvalidOperationException("Futures pre-flight leverage verification failed.");
                }
            }

            await AddPreflightDecisionAsync(settings.Symbol, true, "Futures pre-flight completed.", cancellationToken);
            _logger.LogInformation(
                "Futures pre-flight completed for {Symbol}. Category: {Category}, Margin: {MarginMode}, Position mode: {PositionMode}, Leverage: {Leverage}",
                settings.Symbol,
                settings.Category,
                settings.MarginMode,
                settings.PositionMode,
                settings.Leverage);
        }
        catch (Exception exception)
        {
            await AddPreflightDecisionAsync(settings.Symbol, false, exception.Message, cancellationToken);
            throw;
        }
    }

    private void ValidateSettings(FuturesBotSettings settings)
    {
        if (!_futuresOptions.Enabled)
        {
            throw new InvalidOperationException("Futures pre-flight requires FUTURES_ENABLED=true.");
        }

        if (!string.Equals(settings.Category, "linear", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Futures pre-flight supports only CATEGORY=linear.");
        }

        if (settings.MarginMode != FuturesMarginMode.Isolated)
        {
            throw new InvalidOperationException("Futures pre-flight supports only isolated margin.");
        }

        if (settings.PositionMode != FuturesPositionMode.OneWay)
        {
            throw new InvalidOperationException("Futures pre-flight supports only one-way mode.");
        }

        if (settings.Direction != FuturesDirection.LongOnly)
        {
            throw new InvalidOperationException("Futures pre-flight supports only long-only direction.");
        }

        if (settings.Leverage <= 0m)
        {
            throw new InvalidOperationException("Futures pre-flight leverage must be positive.");
        }
    }

    private static void ValidateInstrumentRules(BybitInstrumentInfo instrument)
    {
        if (instrument.TickSize <= 0m)
        {
            throw new InvalidOperationException("Futures pre-flight requires a positive tick size.");
        }

        if (instrument.QtyStep <= 0m && instrument.BasePrecision <= 0m)
        {
            throw new InvalidOperationException("Futures pre-flight requires a positive quantity step.");
        }

        if (instrument.MinOrderQty <= 0m)
        {
            throw new InvalidOperationException("Futures pre-flight requires a positive min order quantity.");
        }

        if (instrument.MinOrderAmount <= 0m)
        {
            throw new InvalidOperationException("Futures pre-flight requires a positive min notional value.");
        }
    }

    private static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private Task AddPreflightDecisionAsync(string symbol, bool isAllowed, string reason, CancellationToken cancellationToken) =>
        _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = symbol,
            Source = "Preflight",
            IsAllowed = isAllowed,
            Reason = reason,
            Severity = isAllowed ? "Info" : "Critical",
            SuggestedAction = isAllowed ? "Allow" : "BlockNewOrders",
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
}
