using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IMarketScannerService
{
    Task<MarketScanResponse> ScanAsync(string? category, int? limit, CancellationToken cancellationToken);
}

public sealed class MarketScannerService : IMarketScannerService
{
    private const string ScanInterval = "5";
    private const int ScanCandles = 72;
    private const int MaxConcurrency = 6;
    private const int DefaultLimit = 120;

    private readonly IBybitRestClient _bybitRestClient;
    private readonly IGridRepository _repository;
    private readonly MarketRegimeAnalyzer _marketRegimeAnalyzer;
    private readonly AutoStrategySelector _autoStrategySelector;
    private readonly GridOptions _defaultGridOptions;
    private readonly ILogger<MarketScannerService> _logger;

    public MarketScannerService(
        IBybitRestClient bybitRestClient,
        IGridRepository repository,
        MarketRegimeAnalyzer marketRegimeAnalyzer,
        AutoStrategySelector autoStrategySelector,
        IOptions<GridOptions> gridOptions,
        ILogger<MarketScannerService> logger)
    {
        _bybitRestClient = bybitRestClient;
        _repository = repository;
        _marketRegimeAnalyzer = marketRegimeAnalyzer;
        _autoStrategySelector = autoStrategySelector;
        _defaultGridOptions = gridOptions.Value;
        _logger = logger;
    }

    public async Task<MarketScanResponse> ScanAsync(string? category, int? limit, CancellationToken cancellationToken)
    {
        var normalizedCategory = NormalizeCategory(category);
        var maxCandidates = Math.Clamp(limit ?? DefaultLimit, 10, 500);
        var instruments = await _bybitRestClient.GetInstrumentsAsync(normalizedCategory, cancellationToken);
        var tickers = await _bybitRestClient.GetTickersAsync(normalizedCategory, cancellationToken);
        var tickerBySymbol = tickers.ToDictionary(ticker => ticker.Symbol, StringComparer.OrdinalIgnoreCase);

        var candidates = instruments
            .Where(instrument => IsTradableUsdtInstrument(normalizedCategory, instrument))
            .Where(instrument => instrument.MinOrderQty > 0m && instrument.MinOrderAmount <= decimal.Max(_defaultGridOptions.OrderSizeUsdt * 2m, 50m))
            .Where(instrument => tickerBySymbol.ContainsKey(instrument.Symbol))
            .OrderByDescending(instrument => tickerBySymbol[instrument.Symbol].Turnover24h)
            .Take(maxCandidates)
            .ToArray();

        var results = new List<MarketScanItem>();
        var failures = 0;
        using var throttler = new SemaphoreSlim(MaxConcurrency);
        var tasks = candidates.Select(async instrument =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                return await ScanInstrumentAsync(normalizedCategory, instrument, tickerBySymbol[instrument.Symbol], cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                _logger.LogWarning(exception, "Market scan failed for {Symbol}", instrument.Symbol);
                return null;
            }
            finally
            {
                throttler.Release();
            }
        });

        foreach (var item in await Task.WhenAll(tasks))
        {
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return new MarketScanResponse
        {
            Category = normalizedCategory,
            CandidateCount = candidates.Length,
            ScannedCount = results.Count,
            FailedCount = failures,
            GeneratedAt = DateTimeOffset.UtcNow,
            Items = results
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.StrategyFitScore)
                .ThenByDescending(item => item.Volume6hUsdt)
                .ToArray()
        };
    }

    private async Task<MarketScanItem> ScanInstrumentAsync(
        string category,
        BybitInstrumentInfo instrument,
        BybitTicker ticker,
        CancellationToken cancellationToken)
    {
        var candles = await _bybitRestClient.GetKlinesAsync(category, instrument.Symbol, ScanInterval, ScanCandles, cancellationToken);
        if (candles.Count < 30)
        {
            return BuildNoTrade(category, instrument, ticker, "not enough candle data");
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var lastPrice = ticker.LastPrice > 0m ? ticker.LastPrice : ordered[^1].Close;
        var support = ordered.TakeLast(30).Min(candle => candle.Low);
        var resistance = ordered.TakeLast(30).Max(candle => candle.High);
        var atr = ordered.TakeLast(30).Average(candle => candle.High - candle.Low);
        var atrPercent = lastPrice > 0m ? atr / lastPrice * 100m : 0m;
        var high = ordered.Max(candle => candle.High);
        var low = ordered.Min(candle => candle.Low);
        var volatilityPercent = lastPrice > 0m ? (high - low) / lastPrice * 100m : 0m;
        var momentumPercent = ordered[0].Open > 0m ? (ordered[^1].Close - ordered[0].Open) / ordered[0].Open * 100m : 0m;
        var volume6hUsdt = ordered.Sum(candle => candle.Turnover);
        var spreadPercent = CalculateSpreadPercent(ticker);
        var recentOrders = await _repository.GetOrdersAsync(instrument.Symbol, cancellationToken);
        var recentLosses = recentOrders
            .Where(order => order.Side == TradeSide.Sell &&
                order.FilledAt is not null &&
                DateTimeOffset.UtcNow - order.FilledAt.Value <= TimeSpan.FromHours(12) &&
                order.RealizedPnl < 0m)
            .Count();
        var hasFilledTrades = recentOrders.Any(order => order.Status == OrderStatus.Filled);
        var hasProfitableClosedTrade = recentOrders.Any(order =>
            order.Side == TradeSide.Sell &&
            order.Status == OrderStatus.Filled &&
            order.RealizedPnl > order.FeePaid);

        var scanOptions = BuildScanGridOptions(category, instrument.Symbol, lastPrice, support, resistance, atr);
        var regime = _marketRegimeAnalyzer.Analyze(ordered);
        var recommendation = _autoStrategySelector.Recommend(scanOptions, regime, ordered);
        var strategyFit = StrategyFitAnalyzer.Analyze(
            ordered,
            scanOptions,
            lastPrice,
            atrPercent,
            volatilityPercent,
            momentumPercent,
            recommendation.StrategyType);
        var reasons = new List<string>();
        var score = 50m;

        ScoreSpread(spreadPercent, ref score, reasons);
        ScoreVolatility(atrPercent, volatilityPercent, ref score, reasons);
        ScoreVolume(volume6hUsdt, ticker.Turnover24h, ref score, reasons);
        ScoreMomentum(momentumPercent, ref score, reasons);
        ScoreRangeQuality(volatilityPercent, momentumPercent, ref score, reasons);
        ScoreRecommendation(recommendation, ref score, reasons);
        ScoreStrategyFit(strategyFit, ref score, reasons);
        ScoreDumpRisk(ordered, momentumPercent, ref score, reasons);
        ScoreMinimumOrder(instrument, scanOptions, ref score, reasons);
        if (recentLosses > 0)
        {
            score -= Math.Min(25m, recentLosses * 8m);
            reasons.Add($"recent loss sells {recentLosses}");
        }

        if (!hasFilledTrades)
        {
            score = decimal.Min(score, 85m);
            reasons.Add("probation: no filled trade history");
        }
        else if (!hasProfitableClosedTrade)
        {
            score = decimal.Min(score, 90m);
            reasons.Add("probation: no profitable closed cycle yet");
        }

        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);
        var label = ResolveLabel(score, recommendation.StrategyType);
        var orderSize = ResolveRecommendedOrderSize(score, scanOptions, instrument, recommendation.OrderSizeUsdt, hasProfitableClosedTrade);
        var settings = BuildSettings(category, instrument.Symbol, recommendation, orderSize);

        return new MarketScanItem
        {
            Symbol = instrument.Symbol,
            Category = category,
            Score = score,
            Label = label,
            RecommendedStrategy = recommendation.StrategyType.ToString(),
            RecommendedOrderSizeUsdt = orderSize,
            StrategyFitScore = decimal.Round(strategyFit.SelectedFitScore, 2, MidpointRounding.AwayFromZero),
            StrategyFitName = strategyFit.SelectedFitStrategy,
            GridFitScore = decimal.Round(strategyFit.GridFitScore, 2, MidpointRounding.AwayFromZero),
            BtdFitScore = decimal.Round(strategyFit.BtdFitScore, 2, MidpointRounding.AwayFromZero),
            ComboFitScore = decimal.Round(strategyFit.ComboFitScore, 2, MidpointRounding.AwayFromZero),
            ReversalFitScore = decimal.Round(strategyFit.ReversalFitScore, 2, MidpointRounding.AwayFromZero),
            LastPrice = decimal.Round(lastPrice, 8, MidpointRounding.AwayFromZero),
            SpreadPercent = decimal.Round(spreadPercent, 4, MidpointRounding.AwayFromZero),
            AtrPercent = decimal.Round(atrPercent, 4, MidpointRounding.AwayFromZero),
            VolatilityPercent = decimal.Round(volatilityPercent, 4, MidpointRounding.AwayFromZero),
            MomentumPercent = decimal.Round(momentumPercent, 4, MidpointRounding.AwayFromZero),
            Volume6hUsdt = decimal.Round(volume6hUsdt, 2, MidpointRounding.AwayFromZero),
            Support = decimal.Round(support, 8, MidpointRounding.AwayFromZero),
            Resistance = decimal.Round(resistance, 8, MidpointRounding.AwayFromZero),
            MinOrderAmount = instrument.MinOrderAmount,
            Reasons = reasons.Concat(strategyFit.Reasons).ToArray(),
            Settings = settings
        };
    }

    private static string NormalizeCategory(string? category)
    {
        var value = string.IsNullOrWhiteSpace(category) ? "spot" : category.Trim().ToLowerInvariant();
        return value == "linear" ? "linear" : "spot";
    }

    private static bool IsTradableUsdtInstrument(string category, BybitInstrumentInfo instrument)
    {
        if (!instrument.Symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(instrument.QuoteCoin) &&
            !string.Equals(instrument.QuoteCoin, "USDT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(instrument.Status) &&
            !string.Equals(instrument.Status, "Trading", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return category != "linear" ||
            string.IsNullOrWhiteSpace(instrument.ContractType) ||
            instrument.ContractType.Contains("Perpetual", StringComparison.OrdinalIgnoreCase);
    }

    private GridOptions BuildScanGridOptions(string category, string symbol, decimal lastPrice, decimal support, decimal resistance, decimal atr)
    {
        var step = ChooseStep(lastPrice, atr);
        var padding = decimal.Max(step * 2m, atr * 1.5m);
        var lower = FloorToStep(decimal.Max(step, decimal.Min(support, lastPrice) - padding), step);
        var upper = CeilingToStep(decimal.Max(resistance, lastPrice) + padding, step);
        var stopPadding = decimal.Max(padding * 2m, lastPrice * 0.04m);

        return new GridOptions
        {
            Symbol = symbol,
            Category = category,
            SpotOnly = category == "spot",
            LowerPrice = lower,
            UpperPrice = upper,
            Step = step,
            OrderSizeUsdt = _defaultGridOptions.OrderSizeUsdt,
            MinOrderSizeUsdt = _defaultGridOptions.MinOrderSizeUsdt,
            MinNetProfitUsdt = _defaultGridOptions.MinNetProfitUsdt,
            MinNetProfitPercent = _defaultGridOptions.MinNetProfitPercent,
            StopLowerPrice = FloorToStep(decimal.Max(step, lower - stopPadding), step),
            StopUpperPrice = CeilingToStep(upper + stopPadding, step),
            BtcFilterEnabled = _defaultGridOptions.BtcFilterEnabled,
            BtcMaxMovePercent = _defaultGridOptions.BtcMaxMovePercent,
            BtcLookbackCandles = _defaultGridOptions.BtcLookbackCandles
        };
    }

    private static UpdateSettingsRequest BuildSettings(
        string category,
        string symbol,
        AutoConfigRecommendation recommendation,
        decimal orderSize) => new()
    {
        Symbol = symbol,
        Category = category,
        StrategyMode = "auto",
        StrategyType = recommendation.StrategyType.ToString().ToLowerInvariant(),
        StrategyConfigJson = recommendation.StrategyConfigJson,
        LowerPrice = recommendation.LowerPrice,
        UpperPrice = recommendation.UpperPrice,
        Step = recommendation.Step,
        OrderSizeUsdt = orderSize,
        StopLowerPrice = recommendation.StopLowerPrice,
        StopUpperPrice = recommendation.StopUpperPrice
    };

    private static MarketScanItem BuildNoTrade(string category, BybitInstrumentInfo instrument, BybitTicker ticker, string reason) => new()
    {
        Symbol = instrument.Symbol,
        Category = category,
        Score = 0m,
        Label = "NO_TRADE",
        RecommendedStrategy = TradingStrategyType.NoTrade.ToString(),
        RecommendedOrderSizeUsdt = 0m,
        StrategyFitScore = 0m,
        StrategyFitName = "Unknown",
        GridFitScore = 0m,
        BtdFitScore = 0m,
        ComboFitScore = 0m,
        ReversalFitScore = 0m,
        LastPrice = ticker.LastPrice,
        SpreadPercent = CalculateSpreadPercent(ticker),
        AtrPercent = 0m,
        VolatilityPercent = 0m,
        MomentumPercent = 0m,
        Volume6hUsdt = 0m,
        Support = 0m,
        Resistance = 0m,
        MinOrderAmount = instrument.MinOrderAmount,
        Reasons = [reason],
        Settings = new UpdateSettingsRequest
        {
            Symbol = instrument.Symbol,
            Category = category,
            StrategyMode = "auto",
            StrategyType = "notrade",
            StrategyConfigJson = "{}"
        }
    };

    private static void ScoreSpread(decimal spreadPercent, ref decimal score, List<string> reasons)
    {
        if (spreadPercent <= 0.05m)
        {
            score += 14m;
            reasons.Add($"spread tight {spreadPercent:0.###}%");
        }
        else if (spreadPercent <= 0.15m)
        {
            score += 5m;
            reasons.Add($"spread acceptable {spreadPercent:0.###}%");
        }
        else
        {
            score -= 25m;
            reasons.Add($"spread wide {spreadPercent:0.###}%");
        }
    }

    private static void ScoreVolatility(decimal atrPercent, decimal volatilityPercent, ref decimal score, List<string> reasons)
    {
        if (atrPercent is >= 0.12m and <= 0.9m && volatilityPercent is >= 0.8m and <= 12m)
        {
            score += 16m;
            reasons.Add($"volatility tradable ATR {atrPercent:0.###}%");
        }
        else if (atrPercent > 1.5m || volatilityPercent > 18m)
        {
            score -= 20m;
            reasons.Add($"volatility dangerous {volatilityPercent:0.##}%");
        }
        else
        {
            score -= 6m;
            reasons.Add($"volatility weak ATR {atrPercent:0.###}%");
        }
    }

    private static void ScoreVolume(decimal volume6hUsdt, decimal turnover24h, ref decimal score, List<string> reasons)
    {
        if (volume6hUsdt >= 1_000_000m || turnover24h >= 5_000_000m)
        {
            score += 14m;
            reasons.Add($"volume strong ${volume6hUsdt:0}");
        }
        else if (volume6hUsdt >= 100_000m)
        {
            score += 6m;
            reasons.Add($"volume ok ${volume6hUsdt:0}");
        }
        else
        {
            score -= 12m;
            reasons.Add($"volume weak ${volume6hUsdt:0}");
        }
    }

    private static void ScoreMomentum(decimal momentumPercent, ref decimal score, List<string> reasons)
    {
        if (momentumPercent is > 0.4m and < 8m)
        {
            score += 8m;
            reasons.Add($"positive momentum {momentumPercent:0.##}%");
        }
        else if (momentumPercent <= -8m)
        {
            score -= 10m;
            reasons.Add($"large drawdown {momentumPercent:0.##}%");
        }
    }

    private static void ScoreRangeQuality(decimal volatilityPercent, decimal momentumPercent, ref decimal score, List<string> reasons)
    {
        if (volatilityPercent >= 1m && Math.Abs(momentumPercent) <= volatilityPercent * 0.45m)
        {
            score += 8m;
            reasons.Add("range quality ok");
        }
    }

    private static void ScoreRecommendation(AutoConfigRecommendation recommendation, ref decimal score, List<string> reasons)
    {
        switch (recommendation.StrategyType)
        {
            case TradingStrategyType.Grid:
            case TradingStrategyType.Combo:
            case TradingStrategyType.Btd:
            case TradingStrategyType.Signal:
            case TradingStrategyType.TrendFollow:
            case TradingStrategyType.TrendFollowing:
            case TradingStrategyType.Breakout:
            case TradingStrategyType.Hybrid:
                score += 10m;
                reasons.Add($"strategy {recommendation.StrategyType}");
                break;
            case TradingStrategyType.NoTrade:
            case TradingStrategyType.Pause:
                score -= 30m;
                reasons.Add("auto selector NoTrade");
                break;
        }
    }

    private static void ScoreStrategyFit(StrategyFitResult fit, ref decimal score, List<string> reasons)
    {
        if (fit.SelectedFitScore >= 75m)
        {
            score += 20m;
            reasons.Add($"{fit.SelectedFitStrategy} candle fit strong {fit.SelectedFitScore:0}");
        }
        else if (fit.SelectedFitScore >= 60m)
        {
            score += 10m;
            reasons.Add($"{fit.SelectedFitStrategy} candle fit good {fit.SelectedFitScore:0}");
        }
        else if (fit.SelectedFitScore >= 45m)
        {
            reasons.Add($"{fit.SelectedFitStrategy} candle fit neutral {fit.SelectedFitScore:0}");
        }
        else if (fit.SelectedFitScore >= 30m)
        {
            score -= 12m;
            reasons.Add($"{fit.SelectedFitStrategy} candle fit weak {fit.SelectedFitScore:0}");
        }
        else
        {
            score -= 25m;
            reasons.Add($"{fit.SelectedFitStrategy} candle fit poor {fit.SelectedFitScore:0}");
        }
    }

    private static void ScoreDumpRisk(IReadOnlyList<Candle> candles, decimal momentumPercent, ref decimal score, List<string> reasons)
    {
        var last = candles[^1];
        var lastMove = last.Open > 0m ? (last.Close - last.Open) / last.Open * 100m : 0m;
        if (lastMove <= -3m || momentumPercent <= -15m)
        {
            score -= 30m;
            reasons.Add($"dump risk last {lastMove:0.##}%");
        }
    }

    private static void ScoreMinimumOrder(BybitInstrumentInfo instrument, GridOptions options, ref decimal score, List<string> reasons)
    {
        if (instrument.MinOrderAmount <= 0m || instrument.MinOrderAmount <= options.MinOrderSizeUsdt)
        {
            score += 4m;
            return;
        }

        if (instrument.MinOrderAmount > options.OrderSizeUsdt)
        {
            score -= 12m;
            reasons.Add($"min order high {instrument.MinOrderAmount:0.####} USDT");
        }
    }

    private static string ResolveLabel(decimal score, TradingStrategyType strategyType)
    {
        if (strategyType is TradingStrategyType.NoTrade or TradingStrategyType.Pause || score < 15m)
        {
            return "NO_TRADE";
        }

        return score switch
        {
            >= 80m => "HOT",
            >= 60m => "GOOD",
            >= 40m => "NEUTRAL",
            _ => "AVOID"
        };
    }

    private static decimal ResolveRecommendedOrderSize(
        decimal score,
        GridOptions options,
        BybitInstrumentInfo instrument,
        decimal recommendedOrderSize,
        bool hasProfitableClosedTrade)
    {
        var multiplier = score switch
        {
            >= 80m => 1.4m,
            >= 60m => 1.15m,
            >= 40m => 1m,
            >= 15m => 0.5m,
            _ => 0m
        };
        if (multiplier <= 0m)
        {
            return 0m;
        }

        if (!hasProfitableClosedTrade)
        {
            multiplier = decimal.Min(multiplier, 0.5m);
        }

        var baseSize = recommendedOrderSize > 0m ? recommendedOrderSize : options.OrderSizeUsdt;
        return decimal.Round(decimal.Max(decimal.Max(options.MinOrderSizeUsdt, instrument.MinOrderAmount), baseSize * multiplier), 2, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateSpreadPercent(BybitTicker ticker)
    {
        if (ticker.Bid1Price <= 0m || ticker.Ask1Price <= 0m)
        {
            return 0m;
        }

        var mid = (ticker.Bid1Price + ticker.Ask1Price) / 2m;
        return mid > 0m ? (ticker.Ask1Price - ticker.Bid1Price) / mid * 100m : 0m;
    }

    private static decimal ChooseStep(decimal lastPrice, decimal atr)
    {
        var rawStep = decimal.Max(lastPrice * 0.003m, atr / 2m);
        if (lastPrice < 1m)
        {
            return decimal.Max(0.0001m, decimal.Round(rawStep, 4, MidpointRounding.AwayFromZero));
        }

        if (lastPrice < 10m)
        {
            return decimal.Max(0.001m, decimal.Round(rawStep, 3, MidpointRounding.AwayFromZero));
        }

        return decimal.Max(0.01m, decimal.Round(rawStep, 2, MidpointRounding.AwayFromZero));
    }

    private static decimal FloorToStep(decimal value, decimal step) =>
        decimal.Round(Math.Floor(value / step) * step, 8, MidpointRounding.ToZero);

    private static decimal CeilingToStep(decimal value, decimal step) =>
        decimal.Round(Math.Ceiling(value / step) * step, 8, MidpointRounding.AwayFromZero);
}
