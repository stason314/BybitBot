using System.Text.Json;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
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
    private const decimal DefaultFuturesFeeRatePercent = 0.06m;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IBybitRestClient _bybitRestClient;
    private readonly FuturesAutoConfigRecommender _recommender;
    private readonly FuturesStrategyFitAnalyzer _fitAnalyzer;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesRiskOptions _riskOptions;
    private readonly IGridRepository _repository;
    private readonly ILogger<FuturesMarketScannerService> _logger;

    public FuturesMarketScannerService(
        IBybitRestClient bybitRestClient,
        FuturesAutoConfigRecommender recommender,
        FuturesStrategyFitAnalyzer fitAnalyzer,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesRiskOptions> riskOptions,
        IGridRepository repository,
        ILogger<FuturesMarketScannerService> logger)
    {
        _bybitRestClient = bybitRestClient;
        _recommender = recommender;
        _fitAnalyzer = fitAnalyzer;
        _futuresOptions = futuresOptions.Value;
        _riskOptions = riskOptions.Value;
        _repository = repository;
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
                .OrderByDescending(item => item.ActionabilityScore)
                .ThenByDescending(item => item.MarketFitScore)
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
        var fitScore = ResolveBestFitScore(fit, strategy);
        var reasons = new List<string>(fit.Reasons);
        var score = 50m;
        ScoreSpread(spreadPercent, ref score, reasons);
        ScoreVolume(volume6hUsdt, ticker.Turnover24h, ref score, reasons);
        ScoreVolatility(atrPercent, volatilityPercent, ref score, reasons);
        ScoreMomentum(momentumPercent, strategy, ref score, reasons);
        ScoreRangeQuality(fit, strategy, volatilityPercent, momentumPercent, ref score, reasons);
        ScoreRecommendationAlignment(recommendation, strategy, direction, ref score, reasons);
        ScoreStrategyFit(strategy, fitScore, ref score, reasons);
        ScoreDirectionalRisk(ordered, momentumPercent, strategy, ref score, reasons);
        ScoreMinimumOrder(instrument, maxNotional, ref score, reasons);
        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);
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
        var position = await _repository.GetFuturesPositionAsync(instrument.Symbol, cancellationToken);
        var recentRiskDecisions = await _repository.GetFuturesRiskDecisionsAsync(instrument.Symbol, 20, cancellationToken);
        var actionability = BuildActionability(
            score,
            strategy,
            direction,
            position,
            maxNotional,
            entryNotional,
            atrPercent,
            volatilityPercent,
            spreadPercent,
            recommendation.TakeProfitPercent,
            recentRiskDecisions);
        foreach (var reason in actionability.Reasons)
        {
            reasons.Add(reason);
        }

        return new FuturesMarketScanItem
        {
            Symbol = instrument.Symbol,
            Category = "linear",
            Score = score,
            MarketFitScore = score,
            ActionabilityScore = actionability.Score,
            ActionabilityLabel = actionability.Label,
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
            StrategyFitScore = decimal.Round(fitScore, 2, MidpointRounding.AwayFromZero),
            StrategyFitName = strategy.ToString(),
            GridFitScore = decimal.Round(decimal.Max(fit.GridLongOnlyScore, fit.GridShortOnlyScore), 2, MidpointRounding.AwayFromZero),
            TrendFitScore = decimal.Round(decimal.Max(fit.TrendFollowScore, fit.TrendFollowShortOnlyScore), 2, MidpointRounding.AwayFromZero),
            BreakoutFitScore = decimal.Round(decimal.Max(fit.BreakoutScore, fit.BreakdownShortScore), 2, MidpointRounding.AwayFromZero),
            GridLongFitScore = decimal.Round(fit.GridLongOnlyScore, 2, MidpointRounding.AwayFromZero),
            GridShortFitScore = decimal.Round(fit.GridShortOnlyScore, 2, MidpointRounding.AwayFromZero),
            TrendLongFitScore = decimal.Round(fit.TrendFollowScore, 2, MidpointRounding.AwayFromZero),
            TrendShortFitScore = decimal.Round(fit.TrendFollowShortOnlyScore, 2, MidpointRounding.AwayFromZero),
            BreakdownFitScore = decimal.Round(fit.BreakdownShortScore, 2, MidpointRounding.AwayFromZero),
            Reasons = reasons,
            Settings = settings
        };
    }

    private FuturesActionabilityView BuildActionability(
        decimal marketFitScore,
        FuturesStrategyType strategy,
        FuturesDirection direction,
        FuturesPositionSnapshot? position,
        decimal maxNotional,
        decimal entryNotional,
        decimal atrPercent,
        decimal volatilityPercent,
        decimal spreadPercent,
        decimal takeProfitPercent,
        IReadOnlyCollection<FuturesRiskDecisionRecord> recentRiskDecisions)
    {
        var score = marketFitScore;
        var reasons = new List<string>();

        if (position is { Size: > 0m })
        {
            if (IsOppositePosition(position.Side, direction))
            {
                score -= 45m;
                reasons.Add($"action blocked: opposite open position {position.Side}");
            }
            else
            {
                reasons.Add($"action compatible with open position {position.Side}");
            }

            var remainingNotional = maxNotional - position.PositionValueUsdt;
            if (remainingNotional < entryNotional)
            {
                score -= 30m;
                reasons.Add($"action blocked: position full remaining {remainingNotional:0.####} < next {entryNotional:0.####}");
            }
        }
        else
        {
            reasons.Add("action no open position");
        }

        var estimatedMovePercent = decimal.Max(atrPercent, volatilityPercent * 0.25m);
        var estimatedGrossPnl = entryNotional * estimatedMovePercent / 100m;
        var roundTripFees = entryNotional * DefaultFuturesFeeRatePercent / 100m * 2m;
        var minNetProfit = ResolveMinNetProfitThreshold(entryNotional);
        if (estimatedGrossPnl < roundTripFees + minNetProfit)
        {
            score -= 25m;
            reasons.Add("action weak: ATR/range does not cover fees plus min profit");
        }
        else
        {
            reasons.Add("action fee opportunity ok");
        }

        if (takeProfitPercent > 0m && volatilityPercent < takeProfitPercent * 0.5m)
        {
            score -= 10m;
            reasons.Add("action weak: TP outside recent range");
        }
        else
        {
            reasons.Add("action TP reachable by recent range");
        }

        if (spreadPercent > 0.15m)
        {
            score -= 10m;
            reasons.Add("action weak: spread reduces net profit");
        }

        var activeBlock = recentRiskDecisions
            .Where(decision => !decision.IsAllowed)
            .Where(decision => DateTimeOffset.UtcNow - decision.CreatedAt <= TimeSpan.FromMinutes(2))
            .Where(IsActionabilityBlockingSource)
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        if (activeBlock is not null)
        {
            score -= 20m;
            reasons.Add($"action blocked by {activeBlock.Source}: {activeBlock.Reason}");
        }

        score = Math.Clamp(decimal.Round(score, 2, MidpointRounding.AwayFromZero), 0m, 100m);
        return new FuturesActionabilityView(score, ResolveLabel(score, strategy), reasons);
    }

    private static bool IsOppositePosition(string side, FuturesDirection direction) =>
        (string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) && direction == FuturesDirection.ShortOnly) ||
        (string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) && direction == FuturesDirection.LongOnly);

    private static bool IsActionabilityBlockingSource(FuturesRiskDecisionRecord decision) =>
        string.Equals(decision.Source, "Risk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "StrategyFilter", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "AggressiveGuard", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "AggressiveNoTrade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(decision.Source, "AutoRecommendationSkipped", StringComparison.OrdinalIgnoreCase);

    private decimal ResolveMinNetProfitThreshold(decimal notionalUsdt)
    {
        var minUsdt = decimal.Max(0m, _riskOptions.MinNetProfitUsdt);
        var minPercent = decimal.Max(0m, _riskOptions.MinNetProfitPercent);
        return decimal.Max(minUsdt, notionalUsdt * minPercent / 100m);
    }

    private readonly record struct FuturesActionabilityView(
        decimal Score,
        string Label,
        IReadOnlyList<string> Reasons);

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
        TakeProfitPercent = 6m,
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
        MarketFitScore = 0m,
        ActionabilityScore = 0m,
        ActionabilityLabel = "NO_TRADE",
        Label = "NO_TRADE",
        RecommendedStrategy = "pause",
        RecommendedDirection = "long-only",
        EntryNotionalUsdt = 0m,
        LastPrice = ticker.LastPrice,
        SpreadPercent = CalculateSpreadPercent(ticker),
        StrategyFitScore = 0m,
        StrategyFitName = "Pause",
        GridLongFitScore = 0m,
        GridShortFitScore = 0m,
        TrendLongFitScore = 0m,
        TrendShortFitScore = 0m,
        BreakdownFitScore = 0m,
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

    private static void ScoreSpread(decimal spreadPercent, ref decimal score, List<string> reasons)
    {
        if (spreadPercent <= 0.05m)
        {
            score += 14m;
            reasons.Add($"spread tight {spreadPercent:0.###}%");
            return;
        }

        if (spreadPercent <= 0.15m)
        {
            score += 5m;
            reasons.Add($"spread acceptable {spreadPercent:0.###}%");
            return;
        }

        score -= 25m;
        reasons.Add($"spread wide {spreadPercent:0.###}%");
    }

    private static void ScoreVolume(decimal volume6hUsdt, decimal turnover24h, ref decimal score, List<string> reasons)
    {
        if (volume6hUsdt >= 1_000_000m || turnover24h >= 5_000_000m)
        {
            score += 14m;
            reasons.Add($"volume strong ${volume6hUsdt:0}");
            return;
        }

        if (volume6hUsdt >= 100_000m)
        {
            score += 6m;
            reasons.Add($"volume ok ${volume6hUsdt:0}");
            return;
        }

        score -= 12m;
        reasons.Add($"volume weak ${volume6hUsdt:0}");
    }

    private static void ScoreVolatility(decimal atrPercent, decimal volatilityPercent, ref decimal score, List<string> reasons)
    {
        if (atrPercent is >= 0.08m and <= 1.5m && volatilityPercent is >= 0.8m and <= 14m)
        {
            score += 12m;
            reasons.Add($"volatility tradable ATR {atrPercent:0.###}%");
            return;
        }

        if (atrPercent > 2m || volatilityPercent > 18m)
        {
            score -= 16m;
            reasons.Add($"volatility dangerous {volatilityPercent:0.##}%");
            return;
        }

        score -= 5m;
        reasons.Add($"volatility weak ATR {atrPercent:0.###}%");
    }

    private static void ScoreMomentum(
        decimal momentumPercent,
        FuturesStrategyType strategy,
        ref decimal score,
        List<string> reasons)
    {
        var directionalMomentum = IsShortStrategy(strategy) ? -momentumPercent : momentumPercent;
        if (strategy is FuturesStrategyType.GridLongOnly or FuturesStrategyType.GridShortOnly)
        {
            if (Math.Abs(momentumPercent) <= 4m)
            {
                score += 6m;
                reasons.Add($"grid momentum contained {momentumPercent:0.##}%");
            }
            else
            {
                score -= 10m;
                reasons.Add($"grid momentum stretched {momentumPercent:0.##}%");
            }

            return;
        }

        if (directionalMomentum is >= 0.4m and <= 6m)
        {
            score += 8m;
            reasons.Add($"directional momentum {directionalMomentum:0.##}%");
        }
        else if (directionalMomentum > 9m)
        {
            score -= 8m;
            reasons.Add($"directional move extended {directionalMomentum:0.##}%");
        }
        else if (directionalMomentum <= -0.8m)
        {
            score -= 12m;
            reasons.Add($"momentum against strategy {directionalMomentum:0.##}%");
        }
    }

    private static void ScoreRangeQuality(
        FuturesStrategyFitResult fit,
        FuturesStrategyType strategy,
        decimal volatilityPercent,
        decimal momentumPercent,
        ref decimal score,
        List<string> reasons)
    {
        if (strategy is FuturesStrategyType.GridLongOnly or FuturesStrategyType.GridShortOnly)
        {
            if (fit.RangePercent >= 1m &&
                fit.MeanReversionCrosses >= 6 &&
                Math.Abs(momentumPercent) <= decimal.Max(0.75m, fit.RangePercent * 0.55m))
            {
                score += 10m;
                reasons.Add("grid range quality strong");
            }
            else if (fit.MaxDirectionalStreak >= 7)
            {
                score -= 10m;
                reasons.Add($"grid directional streak risk {fit.MaxDirectionalStreak}");
            }

            return;
        }

        if (volatilityPercent >= 1m && fit.MaxDirectionalStreak >= 3)
        {
            score += 5m;
            reasons.Add("trend structure visible");
        }
    }

    private static void ScoreRecommendationAlignment(
        FuturesAutoConfigRecommendation recommendation,
        FuturesStrategyType strategy,
        FuturesDirection direction,
        ref decimal score,
        List<string> reasons)
    {
        if (recommendation.StrategyType is FuturesStrategyType.Pause)
        {
            score -= 25m;
            reasons.Add("auto selector Pause");
            return;
        }

        if (recommendation.StrategyType == strategy && recommendation.Direction == direction)
        {
            score += 10m;
            reasons.Add($"auto selector agrees {strategy}");
            return;
        }

        if (recommendation.Direction != direction)
        {
            score -= 8m;
            reasons.Add($"auto selector direction mismatch {recommendation.Direction}");
            return;
        }

        score += 3m;
        reasons.Add($"auto selector same direction {recommendation.StrategyType}");
    }

    private static void ScoreStrategyFit(
        FuturesStrategyType strategy,
        decimal fitScore,
        ref decimal score,
        List<string> reasons)
    {
        if (fitScore >= 75m)
        {
            score += 20m;
            reasons.Add($"{strategy} candle fit strong {fitScore:0}");
        }
        else if (fitScore >= 60m)
        {
            score += 10m;
            reasons.Add($"{strategy} candle fit good {fitScore:0}");
        }
        else if (fitScore >= 45m)
        {
            reasons.Add($"{strategy} candle fit neutral {fitScore:0}");
        }
        else if (fitScore >= 30m)
        {
            score -= 12m;
            reasons.Add($"{strategy} candle fit weak {fitScore:0}");
        }
        else
        {
            score -= 25m;
            reasons.Add($"{strategy} candle fit poor {fitScore:0}");
        }
    }

    private static void ScoreDirectionalRisk(
        IReadOnlyList<Candle> candles,
        decimal momentumPercent,
        FuturesStrategyType strategy,
        ref decimal score,
        List<string> reasons)
    {
        var last = candles[^1];
        var lastMove = last.Open > 0m ? (last.Close - last.Open) / last.Open * 100m : 0m;
        if (IsShortStrategy(strategy))
        {
            if (lastMove >= 2.5m || momentumPercent >= 8m)
            {
                score -= 25m;
                reasons.Add($"short squeeze risk last {lastMove:0.##}%");
            }

            return;
        }

        if (lastMove <= -2.5m || momentumPercent <= -8m)
        {
            score -= 25m;
            reasons.Add($"long dump risk last {lastMove:0.##}%");
        }
    }

    private static void ScoreMinimumOrder(
        BybitInstrumentInfo instrument,
        decimal maxNotional,
        ref decimal score,
        List<string> reasons)
    {
        if (instrument.MinOrderAmount <= 0m || instrument.MinOrderAmount <= maxNotional)
        {
            score += 4m;
            reasons.Add("minimum order fits profile");
            return;
        }

        score -= 40m;
        reasons.Add($"minimum order {instrument.MinOrderAmount:0.####} exceeds max notional {maxNotional:0.####}");
    }

    private static bool IsShortStrategy(FuturesStrategyType strategy) =>
        strategy is FuturesStrategyType.GridShortOnly or FuturesStrategyType.TrendFollowShortOnly or FuturesStrategyType.BreakdownShort;

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
