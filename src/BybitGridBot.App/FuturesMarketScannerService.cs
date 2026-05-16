using System.Text.Json;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Risk;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IFuturesMarketScannerService
{
    Task<FuturesMarketScanResponse> ScanAsync(int? limit, CancellationToken cancellationToken);
}

public sealed class FuturesMarketScannerService : IFuturesMarketScannerService
{
    private const string ScanInterval = "5";
    private const int ScanCandles = 72;
    private const int MaxConcurrency = 6;
    private const int DefaultLimit = 120;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesAutoConfigRecommender _recommender;
    private readonly FuturesStrategyFitAnalyzer _fitAnalyzer;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesRiskOptions _riskOptions;
    private readonly ILogger<FuturesMarketScannerService> _logger;

    public FuturesMarketScannerService(
        IBybitRestClient bybitRestClient,
        FuturesAutoConfigRecommender recommender,
        FuturesStrategyFitAnalyzer fitAnalyzer,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesRiskOptions> riskOptions,
        ILogger<FuturesMarketScannerService> logger)
    {
        _bybitRestClient = bybitRestClient;
        _recommender = recommender;
        _fitAnalyzer = fitAnalyzer;
        _futuresOptions = futuresOptions.Value;
        _riskOptions = riskOptions.Value;
        _logger = logger;
    }

    public async Task<FuturesMarketScanResponse> ScanAsync(int? limit, CancellationToken cancellationToken)
    {
        var maxCandidates = Math.Clamp(limit ?? DefaultLimit, 10, 500);
        var instruments = await _bybitRestClient.GetInstrumentsAsync("linear", cancellationToken);
        var tickers = await _bybitRestClient.GetTickersAsync("linear", cancellationToken);
        var tickerBySymbol = tickers.ToDictionary(ticker => ticker.Symbol, StringComparer.OrdinalIgnoreCase);
        var maxNotional = ResolveMaxNotional();

        var candidates = instruments
            .Where(IsTradableLinearUsdtInstrument)
            .Where(instrument => instrument.MinOrderQty > 0m && tickerBySymbol.ContainsKey(instrument.Symbol))
            .Where(instrument => instrument.MinOrderAmount <= 0m || instrument.MinOrderAmount <= maxNotional)
            .OrderByDescending(instrument => tickerBySymbol[instrument.Symbol].Turnover24h)
            .Take(maxCandidates)
            .ToArray();

        var results = new List<FuturesMarketScanItem>();
        var failures = 0;
        using var throttler = new SemaphoreSlim(MaxConcurrency);
        var tasks = candidates.Select(async instrument =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                return await ScanInstrumentAsync(instrument, tickerBySymbol[instrument.Symbol], cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Interlocked.Increment(ref failures);
                _logger.LogWarning(exception, "Futures market scan failed for {Symbol}", instrument.Symbol);
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

        return new FuturesMarketScanResponse
        {
            Category = "linear",
            CandidateCount = candidates.Length,
            ScannedCount = results.Count,
            FailedCount = failures,
            GeneratedAt = DateTimeOffset.UtcNow,
            Items = results
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Volume6hUsdt)
                .ToArray()
        };
    }

    private async Task<FuturesMarketScanItem> ScanInstrumentAsync(
        BybitInstrumentInfo instrument,
        BybitTicker ticker,
        CancellationToken cancellationToken)
    {
        var candles = await _bybitRestClient.GetKlinesAsync("linear", instrument.Symbol, ScanInterval, ScanCandles, cancellationToken);
        if (candles.Count < 30)
        {
            return BuildNoTrade(instrument, ticker, "not enough candle data");
        }

        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        var lastPrice = ticker.LastPrice > 0m ? ticker.LastPrice : ordered[^1].Close;
        var support = ordered.TakeLast(30).Min(candle => candle.Low);
        var resistance = ordered.TakeLast(30).Max(candle => candle.High);
        var high = ordered.Max(candle => candle.High);
        var low = ordered.Min(candle => candle.Low);
        var atr = CalculateAtr(ordered.TakeLast(30).ToArray());
        var atrPercent = lastPrice > 0m ? atr / lastPrice * 100m : 0m;
        var volatilityPercent = lastPrice > 0m ? (high - low) / lastPrice * 100m : 0m;
        var momentumPercent = ordered[0].Open > 0m ? (ordered[^1].Close - ordered[0].Open) / ordered[0].Open * 100m : 0m;
        var volume6hUsdt = ordered.Sum(candle => candle.Turnover);
        var spreadPercent = CalculateSpreadPercent(ticker);
        var baseSettings = BuildBaseSettings(instrument.Symbol);
        var recommendation = _recommender.Recommend(baseSettings, ordered, hasOpenPosition: false);
        var fit = _fitAnalyzer.Analyze(ordered);
        var strategy = fit.BestStrategyType == FuturesStrategyType.Pause
            ? recommendation.StrategyType
            : fit.BestStrategyType;
        var direction = ResolveDirection(strategy, fit.Direction, recommendation.Direction);
        var maxNotional = ResolveMaxNotional();
        var maxMargin = ResolveMaxMargin(maxNotional, recommendation.Leverage);
        var entryMultiplier = strategy is FuturesStrategyType.Breakout or FuturesStrategyType.BreakdownShort
            ? 0.1875m
            : 0.25m;
        var entryNotional = decimal.Round(decimal.Min(maxNotional, maxNotional * entryMultiplier), 4, MidpointRounding.AwayFromZero);
        var reasons = new List<string>(fit.Reasons);
        ScoreSpread(spreadPercent, reasons, out var spreadScore);
        ScoreVolume(volume6hUsdt, ticker.Turnover24h, reasons, out var volumeScore);
        ScoreVolatility(atrPercent, volatilityPercent, reasons, out var volatilityScore);
        var fitScore = ResolveBestFitScore(fit, strategy);
        var score = Math.Clamp(decimal.Round(fitScore * 0.55m + spreadScore + volumeScore + volatilityScore, 2, MidpointRounding.AwayFromZero), 0m, 100m);
        var label = ResolveLabel(score, strategy);
        var settings = BuildSettings(
            instrument.Symbol,
            strategy,
            direction,
            recommendation,
            maxNotional,
            maxMargin,
            entryNotional,
            support,
            resistance,
            lastPrice);

        return new FuturesMarketScanItem
        {
            Symbol = instrument.Symbol,
            Category = "linear",
            Score = score,
            Label = label,
            RecommendedStrategy = FormatEnum(strategy),
            RecommendedDirection = FormatEnum(direction),
            EntryNotionalUsdt = entryNotional,
            LastPrice = decimal.Round(lastPrice, 8, MidpointRounding.AwayFromZero),
            SpreadPercent = decimal.Round(spreadPercent, 4, MidpointRounding.AwayFromZero),
            AtrPercent = decimal.Round(atrPercent, 4, MidpointRounding.AwayFromZero),
            VolatilityPercent = decimal.Round(volatilityPercent, 4, MidpointRounding.AwayFromZero),
            MomentumPercent = decimal.Round(momentumPercent, 4, MidpointRounding.AwayFromZero),
            Volume6hUsdt = decimal.Round(volume6hUsdt, 2, MidpointRounding.AwayFromZero),
            Support = decimal.Round(support, 8, MidpointRounding.AwayFromZero),
            Resistance = decimal.Round(resistance, 8, MidpointRounding.AwayFromZero),
            GridFitScore = decimal.Max(fit.GridLongOnlyScore, fit.GridShortOnlyScore),
            TrendFitScore = decimal.Max(fit.TrendFollowScore, fit.TrendFollowShortOnlyScore),
            BreakoutFitScore = decimal.Max(fit.BreakoutScore, fit.BreakdownShortScore),
            Reasons = reasons,
            Settings = settings
        };
    }

    private FuturesBotSettings BuildBaseSettings(string symbol) => new()
    {
        Enabled = true,
        Symbol = symbol,
        Category = "linear",
        StrategyType = FuturesStrategyType.Pause,
        Leverage = decimal.Min(_futuresOptions.Leverage, _riskOptions.MaxLeverage <= 0m ? _futuresOptions.Leverage : _riskOptions.MaxLeverage),
        Direction = FuturesDirection.LongAndShort,
        MaxNotionalUsdt = ResolveMaxNotional(),
        MaxMarginUsdt = ResolveMaxMargin(ResolveMaxNotional(), _futuresOptions.Leverage),
        StopLossPercent = 2m,
        TakeProfitPercent = 4m,
        LiquidationBufferPercent = _riskOptions.MinLiquidationBufferPercent,
        ReduceOnlyEnabled = true,
        AggressiveModeEnabled = _futuresOptions.AggressiveModeEnabled,
        AggressiveModeKind = ParseAggressiveModeKind(_futuresOptions.AggressiveModeKind),
        AggressiveEntryMultiplier = _futuresOptions.AggressiveEntryMultiplier,
        AggressiveMaxOrdersPerHour = _riskOptions.AggressiveMaxOrdersPerHour,
        AggressiveMinSecondsBetweenEntries = _riskOptions.AggressiveMinSecondsBetweenEntries,
        AggressiveMaxConsecutiveLosses = _riskOptions.AggressiveMaxConsecutiveLosses
    };

    private UpdateFuturesSettingsRequest BuildSettings(
        string symbol,
        FuturesStrategyType strategy,
        FuturesDirection direction,
        FuturesAutoConfigRecommendation recommendation,
        decimal maxNotional,
        decimal maxMargin,
        decimal entryNotional,
        decimal support,
        decimal resistance,
        decimal lastPrice) => new()
    {
        Enabled = true,
        Symbol = symbol,
        Category = "linear",
        StrategyType = FormatEnum(strategy),
        StrategyConfigJson = BuildStrategyConfig(strategy, entryNotional, recommendation.StopLossPercent, recommendation.TakeProfitPercent, support, resistance, lastPrice, direction),
        Leverage = recommendation.Leverage,
        MarginMode = "isolated",
        PositionMode = "oneway",
        Direction = FormatEnum(direction),
        MaxNotionalUsdt = maxNotional,
        MaxMarginUsdt = maxMargin,
        StopLossPercent = recommendation.StopLossPercent,
        TakeProfitPercent = recommendation.TakeProfitPercent,
        LiquidationBufferPercent = recommendation.LiquidationBufferPercent,
        ReduceOnlyEnabled = true,
        AggressiveModeEnabled = _futuresOptions.AggressiveModeEnabled,
        AggressiveModeKind = _futuresOptions.AggressiveModeKind,
        AggressiveEntryMultiplier = _futuresOptions.AggressiveEntryMultiplier,
        AggressiveMaxOrdersPerHour = _riskOptions.AggressiveMaxOrdersPerHour,
        AggressiveMinSecondsBetweenEntries = _riskOptions.AggressiveMinSecondsBetweenEntries,
        AggressiveMaxConsecutiveLosses = _riskOptions.AggressiveMaxConsecutiveLosses
    };

    private static string BuildStrategyConfig(
        FuturesStrategyType strategy,
        decimal entryNotional,
        decimal stopLossPercent,
        decimal takeProfitPercent,
        decimal support,
        decimal resistance,
        decimal lastPrice,
        FuturesDirection direction) =>
        JsonSerializer.Serialize(new
        {
            strategyType = strategy.ToString(),
            entryNotionalUsdt = entryNotional,
            stopLossPercent,
            takeProfitPercent,
            support,
            resistance,
            lastPrice,
            longOnly = direction == FuturesDirection.LongOnly,
            shortOnly = direction == FuturesDirection.ShortOnly,
            reduceOnlyOnExit = true
        }, JsonOptions);

    private static FuturesDirection ResolveDirection(
        FuturesStrategyType strategy,
        FuturesDirection fitDirection,
        FuturesDirection recommendationDirection) =>
        strategy switch
        {
            FuturesStrategyType.TrendFollowShortOnly or FuturesStrategyType.BreakdownShort or FuturesStrategyType.GridShortOnly => FuturesDirection.ShortOnly,
            FuturesStrategyType.TrendFollow or FuturesStrategyType.Breakout or FuturesStrategyType.GridLongOnly => FuturesDirection.LongOnly,
            _ => fitDirection == FuturesDirection.ShortOnly ? FuturesDirection.ShortOnly : recommendationDirection
        };

    private static decimal ResolveBestFitScore(FuturesStrategyFitResult fit, FuturesStrategyType strategy) =>
        strategy switch
        {
            FuturesStrategyType.GridLongOnly => fit.GridLongOnlyScore,
            FuturesStrategyType.GridShortOnly => fit.GridShortOnlyScore,
            FuturesStrategyType.TrendFollow => fit.TrendFollowScore,
            FuturesStrategyType.TrendFollowShortOnly => fit.TrendFollowShortOnlyScore,
            FuturesStrategyType.Breakout => fit.BreakoutScore,
            FuturesStrategyType.BreakdownShort => fit.BreakdownShortScore,
            _ => 0m
        };

    private decimal ResolveMaxNotional() =>
        MinPositive(_riskOptions.MaxNotionalUsdt, _futuresOptions.MaxNotionalUsdt);

    private decimal ResolveMaxMargin(decimal maxNotional, decimal leverage)
    {
        var configured = MinPositive(_riskOptions.MaxMarginUsdt, _futuresOptions.MaxMarginUsdt);
        return leverage > 0m ? decimal.Min(configured, maxNotional / leverage) : configured;
    }

    private static decimal MinPositive(decimal left, decimal right)
    {
        if (left <= 0m)
        {
            return right;
        }

        if (right <= 0m)
        {
            return left;
        }

        return decimal.Min(left, right);
    }

    private static bool IsTradableLinearUsdtInstrument(BybitInstrumentInfo instrument)
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

        return string.IsNullOrWhiteSpace(instrument.ContractType) ||
            instrument.ContractType.Contains("Perpetual", StringComparison.OrdinalIgnoreCase);
    }

    private static FuturesMarketScanItem BuildNoTrade(BybitInstrumentInfo instrument, BybitTicker ticker, string reason) => new()
    {
        Symbol = instrument.Symbol,
        Category = "linear",
        Score = 0m,
        Label = "NO_TRADE",
        RecommendedStrategy = "pause",
        RecommendedDirection = "long-only",
        EntryNotionalUsdt = 0m,
        LastPrice = ticker.LastPrice,
        SpreadPercent = CalculateSpreadPercent(ticker),
        Reasons = [reason],
        Settings = new UpdateFuturesSettingsRequest
        {
            Symbol = instrument.Symbol,
            Category = "linear",
            StrategyType = "pause",
            Direction = "long-only",
            StrategyConfigJson = "{}"
        }
    };

    private static void ScoreSpread(decimal spreadPercent, List<string> reasons, out decimal score)
    {
        if (spreadPercent <= 0.05m)
        {
            score = 14m;
            reasons.Add($"spread tight {spreadPercent:0.###}%");
            return;
        }

        if (spreadPercent <= 0.15m)
        {
            score = 5m;
            reasons.Add($"spread acceptable {spreadPercent:0.###}%");
            return;
        }

        score = -25m;
        reasons.Add($"spread wide {spreadPercent:0.###}%");
    }

    private static void ScoreVolume(decimal volume6hUsdt, decimal turnover24h, List<string> reasons, out decimal score)
    {
        if (volume6hUsdt >= 1_000_000m || turnover24h >= 5_000_000m)
        {
            score = 14m;
            reasons.Add($"volume strong ${volume6hUsdt:0}");
            return;
        }

        if (volume6hUsdt >= 100_000m)
        {
            score = 6m;
            reasons.Add($"volume ok ${volume6hUsdt:0}");
            return;
        }

        score = -12m;
        reasons.Add($"volume weak ${volume6hUsdt:0}");
    }

    private static void ScoreVolatility(decimal atrPercent, decimal volatilityPercent, List<string> reasons, out decimal score)
    {
        if (atrPercent is >= 0.08m and <= 1.5m && volatilityPercent is >= 0.8m and <= 14m)
        {
            score = 12m;
            reasons.Add($"volatility tradable ATR {atrPercent:0.###}%");
            return;
        }

        if (atrPercent > 2m || volatilityPercent > 18m)
        {
            score = -16m;
            reasons.Add($"volatility dangerous {volatilityPercent:0.##}%");
            return;
        }

        score = -5m;
        reasons.Add($"volatility weak ATR {atrPercent:0.###}%");
    }

    private static string ResolveLabel(decimal score, FuturesStrategyType strategyType)
    {
        if (strategyType is FuturesStrategyType.Pause || score < 15m)
        {
            return "NO_TRADE";
        }

        return score switch
        {
            >= 80m => "EXCELLENT",
            >= 65m => "GOOD",
            >= 45m => "WATCH",
            _ => "RISKY"
        };
    }

    private static string FormatEnum<T>(T value)
        where T : struct, Enum =>
        value.ToString().ToLowerInvariant() switch
        {
            "longonly" => "long-only",
            "shortonly" => "short-only",
            "longandshort" => "long+short",
            _ => value.ToString().ToLowerInvariant()
        };

    private static FuturesAggressiveModeKind ParseAggressiveModeKind(string value) =>
        string.Equals(value, "test", StringComparison.OrdinalIgnoreCase)
            ? FuturesAggressiveModeKind.Test
            : FuturesAggressiveModeKind.Normal;

    private static decimal CalculateSpreadPercent(BybitTicker ticker)
    {
        if (ticker.Bid1Price <= 0m || ticker.Ask1Price <= 0m)
        {
            return 0m;
        }

        var mid = (ticker.Bid1Price + ticker.Ask1Price) / 2m;
        return mid > 0m ? (ticker.Ask1Price - ticker.Bid1Price) / mid * 100m : 0m;
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 2)
        {
            return candles.Count == 0 ? 0m : candles[0].High - candles[0].Low;
        }

        var total = 0m;
        for (var index = 1; index < candles.Count; index++)
        {
            var current = candles[index];
            var previous = candles[index - 1];
            total += decimal.Max(
                current.High - current.Low,
                decimal.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close)));
        }

        return total / (candles.Count - 1);
    }
}
