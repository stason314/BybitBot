using System.Text.Json;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IGridDashboardService
{
    Task<DashboardResponse> GetDashboardAsync(string? symbol, bool fast, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ApplyAutoRecommendationAsync(string? symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ApplyRecommendationForSelectedStrategyAsync(UpdateSettingsRequest request, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> DeleteSettingsAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ResumeTradingAsync(string? symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> CancelActiveOrdersAsync(string? symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ResetSpotStatisticsAsync(CancellationToken cancellationToken);
    string RenderDashboardPage();
}

public sealed class GridDashboardService : IGridDashboardService
{
    private static readonly JsonSerializerOptions StrategyJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppOptions _appOptions;
    private readonly AutoStrategySelector _autoStrategySelector;
    private readonly GridOptions _defaultGridOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly MarketRegimeAnalyzer _marketRegimeAnalyzer;
    private readonly PriceActionPhaseDetector _priceActionPhaseDetector;
    private readonly IGridRepository _repository;
    private readonly RiskOptions _riskOptions;
    private readonly SignalAnalyzer _signalAnalyzer;
    private readonly IGridTradingStrategy _strategy;

    public GridDashboardService(
        IOptions<AppOptions> appOptions,
        IOptions<GridOptions> defaultGridOptions,
        AutoStrategySelector autoStrategySelector,
        IBybitRestClient bybitRestClient,
        MarketRegimeAnalyzer marketRegimeAnalyzer,
        PriceActionPhaseDetector priceActionPhaseDetector,
        IGridRepository repository,
        IOptions<RiskOptions> riskOptions,
        SignalAnalyzer signalAnalyzer,
        IGridTradingStrategy strategy)
    {
        _appOptions = appOptions.Value;
        _autoStrategySelector = autoStrategySelector;
        _defaultGridOptions = defaultGridOptions.Value;
        _bybitRestClient = bybitRestClient;
        _marketRegimeAnalyzer = marketRegimeAnalyzer;
        _priceActionPhaseDetector = priceActionPhaseDetector;
        _repository = repository;
        _riskOptions = riskOptions.Value;
        _signalAnalyzer = signalAnalyzer;
        _strategy = strategy;
    }

    private async Task<IReadOnlyList<GridBotSettings>> EnsureRuntimeSettingsProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = await _repository.GetRuntimeSettingsProfilesAsync(cancellationToken);
        if (profiles.Count > 0)
        {
            return profiles;
        }

        var defaultSettings = RuntimeGridOptionsFactory.ToRuntimeSettings(_defaultGridOptions);
        await _repository.SaveRuntimeSettingsAsync(defaultSettings, cancellationToken);
        return [defaultSettings];
    }

    public async Task<DashboardResponse> GetDashboardAsync(string? symbol, bool fast, CancellationToken cancellationToken)
    {
        var profiles = await EnsureRuntimeSettingsProfilesAsync(cancellationToken);
        var selectedSymbol = NormalizeOptionalSymbol(symbol);
        var runtimeSettings = selectedSymbol is null
            ? profiles[0]
            : profiles.FirstOrDefault(profile => string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
        var gridOptions = RuntimeGridOptionsFactory.ToGridOptions(runtimeSettings, _defaultGridOptions);
        var state = await _repository.GetBotStateAsync(gridOptions.Symbol, cancellationToken)
            ?? new BotState
            {
                Symbol = gridOptions.Symbol,
                TradingMode = _appOptions.TradingMode,
                QuoteAssetBalance = gridOptions.PaperInitialUsdt,
                BaseAssetQuantity = gridOptions.PaperInitialBaseAssetQuantity,
                AggressiveModeEnabled = gridOptions.AggressiveModeEnabled,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        var levels = await _repository.GetGridLevelsAsync(gridOptions.Symbol, cancellationToken);
        if (levels.Count == 0 &&
            runtimeSettings.StrategyType is TradingStrategyType.Grid
                or TradingStrategyType.Combo
                or TradingStrategyType.Hybrid)
        {
            levels = _strategy.BuildGrid(gridOptions);
        }

        var orderSourceContext = ResolveOrderSourceContext(runtimeSettings.StrategyType);
        var allOrders = await _repository.GetOrdersAsync(gridOptions.Symbol, cancellationToken);
        var rawActiveOrders = allOrders.Where(order => order.IsActive).ToArray();
        var orderSourceLabels = ResolveOrderSourceLabels(allOrders, orderSourceContext);
        var orders = allOrders
            .OrderByDescending(order => order.CreatedAt)
            .Take(100)
            .Select(order => MapOrder(order, orderSourceContext, orderSourceLabels))
            .ToArray();
        var activeOrders = orders
            .Where(order => order.Status is nameof(OrderStatus.New) or nameof(OrderStatus.PartiallyFilled))
            .ToArray();
        var performanceByStrategy = BuildStrategyPerformance(allOrders, orderSourceLabels);
        var dailyPerformanceByStrategy = BuildDailyStrategyPerformance(allOrders, orderSourceLabels);
        var noTradeReasonHistory = await _repository.GetNoTradeReasonsAsync(gridOptions.Symbol, 10, cancellationToken);
        var lastNoTradeReason = noTradeReasonHistory.FirstOrDefault();

        decimal? currentPrice = state.LastObservedPrice;
        if (!fast)
        {
            try
            {
                var ticker = await _bybitRestClient.GetTickerAsync(gridOptions.Category, gridOptions.Symbol, cancellationToken);
                currentPrice = ticker.LastPrice;
            }
            catch
            {
                currentPrice ??= state.LastObservedPrice;
            }
        }

        var unrealizedPnl = currentPrice is null
            ? 0m
            : state.BaseAssetQuantity * (currentPrice.Value - state.AverageEntryPrice);
        var currentProfitPercent = state.AverageEntryPrice > 0m && currentPrice is > 0m
            ? (currentPrice.Value - state.AverageEntryPrice) / state.AverageEntryPrice * 100m
            : 0m;
        var peakProfitPercent = state.AverageEntryPrice > 0m && state.ProfitProtectionPeakPrice > 0m
            ? (state.ProfitProtectionPeakPrice - state.AverageEntryPrice) / state.AverageEntryPrice * 100m
            : 0m;
        var estimatedTotalEquity = state.QuoteAssetBalance + (currentPrice ?? 0m) * state.BaseAssetQuantity;
        var generatedAt = DateTimeOffset.UtcNow;
        var aggressiveModeActive = IsAggressiveModeActive(gridOptions, state, generatedAt);
        var marketRegime = new MarketRegimeAnalysis
        {
            Regime = MarketRegimeType.Range,
            Confidence = 0m,
            Recommendation = "Loading market regime in the background."
        };
        var signalAnalysis = new SignalAnalysis
        {
            Signal = SignalType.Hold,
            Confidence = 0m,
            Reason = "Loading signal analysis in the background."
        };
        var btdDiagnostics = BuildFastBtdDiagnostics(currentPrice, aggressiveModeActive);
        var autoRecommendation = BuildFastAutoRecommendation(runtimeSettings, gridOptions, fast);
        IReadOnlyList<string> autoRecommendationSafetyErrors = [];
        IReadOnlyList<DashboardPairScoreItem> pairScores = [];

        if (!fast)
        {
            var analysisCandles = await GetAnalysisCandlesAsync(gridOptions, cancellationToken);
            marketRegime = AnalyzeMarketRegime(analysisCandles);
            signalAnalysis = AnalyzeSignal(analysisCandles);
            var btcCandles = await GetBtcCandlesForPhaseAsync(gridOptions, cancellationToken);
            var marketPhase = DetectMarketPhase(gridOptions, analysisCandles, btcCandles, currentPrice);
            btdDiagnostics = BuildBtdDiagnostics(
                gridOptions,
                runtimeSettings,
                analysisCandles,
                btcCandles,
                marketPhase,
                currentPrice,
                aggressiveModeActive);
            autoRecommendation = _autoStrategySelector.Recommend(
                gridOptions,
                marketRegime,
                marketPhase,
                analysisCandles,
                aggressiveModeActive);
            var recommendedSettings = BuildRecommendedSettings(runtimeSettings, autoRecommendation, StrategySelectionMode.Auto, generatedAt);
            recommendedSettings = UseReduceOnlyWhenNoTradeWouldLeavePosition(recommendedSettings, state);
            autoRecommendation = UseReduceOnlyRecommendationWhenNoTradeWouldLeavePosition(autoRecommendation, recommendedSettings);
            autoRecommendationSafetyErrors = AutoRecommendationApplySafety.Validate(
                runtimeSettings,
                state,
                rawActiveOrders,
                autoRecommendation,
                recommendedSettings,
                _riskOptions,
                _strategy);
            pairScores = await BuildPairScoresAsync(profiles, generatedAt, cancellationToken);
        }

        return new DashboardResponse
        {
            IsPartial = fast,
            Profiles = profiles
                .Select(profile => new DashboardProfileItem
                {
                    Symbol = profile.Symbol,
                    Category = profile.Category,
                    IsSelected = string.Equals(profile.Symbol, runtimeSettings.Symbol, StringComparison.OrdinalIgnoreCase)
                })
                .ToArray(),
            ConfigSummaries = await BuildConfigSummariesAsync(profiles, runtimeSettings.Symbol, pairScores, cancellationToken),
            PairScores = pairScores,
            Settings = new DashboardSettings
            {
                Symbol = gridOptions.Symbol,
                Category = gridOptions.Category,
                StrategyMode = runtimeSettings.StrategySelectionMode.ToString().ToLowerInvariant(),
                StrategyType = runtimeSettings.StrategyType.ToString().ToLowerInvariant(),
                StrategyConfigJson = runtimeSettings.StrategyConfigJson,
                LowerPrice = gridOptions.LowerPrice,
                UpperPrice = gridOptions.UpperPrice,
                Step = gridOptions.Step,
                OrderSizeUsdt = gridOptions.OrderSizeUsdt,
                StopLowerPrice = gridOptions.StopLowerPrice,
                StopUpperPrice = gridOptions.StopUpperPrice
            },
            Runtime = new DashboardRuntime
            {
                StartedAt = runtimeSettings.UpdatedAt,
                ActiveTime = generatedAt - runtimeSettings.UpdatedAt
            },
            State = new DashboardState
            {
                TradingMode = _appOptions.TradingMode.ToString().ToLowerInvariant(),
                IsPaused = state.IsPaused,
                PauseReason = state.PauseReason,
                CurrentPrice = currentPrice,
                TotalRealizedPnl = state.TotalRealizedPnl,
                DailyRealizedPnl = state.DailyRealizedPnl,
                UnrealizedPnl = unrealizedPnl,
                EstimatedTotalEquity = estimatedTotalEquity,
                BaseAssetQuantity = state.BaseAssetQuantity,
                QuoteAssetBalance = state.QuoteAssetBalance,
                AverageEntryPrice = state.AverageEntryPrice,
                ProfitProtectionCurrentProfitPercent = currentProfitPercent,
                ProfitProtectionPeakProfitPercent = peakProfitPercent,
                ProfitProtectionPeakPrice = state.ProfitProtectionPeakPrice,
                ProfitProtectionTrailingStopPrice = state.ProfitProtectionTrailingStopPrice,
                AggressiveModeEnabled = state.AggressiveModeEnabled,
                AggressiveModeDisabledUntil = state.AggressiveModeDisabledUntil,
                AggressiveModeDisabledReason = state.AggressiveModeDisabledReason,
                AggressiveModeLastLossAt = state.AggressiveModeLastLossAt,
                UpdatedAt = state.UpdatedAt
            },
            MarketRegime = MapMarketRegime(marketRegime),
            SignalAnalysis = MapSignalAnalysis(signalAnalysis),
            BtdDiagnostics = btdDiagnostics,
            AutoRecommendation = MapAutoRecommendation(autoRecommendation, autoRecommendationSafetyErrors),
            LastNoTradeReason = lastNoTradeReason is null ? null : MapNoTradeReason(lastNoTradeReason, generatedAt),
            NoTradeReasonHistory = noTradeReasonHistory.Select(reason => MapNoTradeReason(reason, generatedAt)).ToArray(),
            Orders = orders,
            ActiveOrders = activeOrders,
            PerformanceByStrategy = performanceByStrategy,
            DailyPerformanceByStrategy = dailyPerformanceByStrategy,
            GridLevels = levels.Select(level => level.Price).ToArray(),
            GeneratedAt = generatedAt
        };
    }

    private DashboardBtdDiagnostics BuildFastBtdDiagnostics(decimal? currentPrice, bool aggressiveModeActive) =>
        new()
        {
            Phase = "Loading",
            EmaFast = currentPrice ?? 0m,
            EmaSlow = currentPrice ?? 0m,
            BtcRiskOff = false,
            PullbackPercent = 0m,
            DistanceToEmaPercent = 0m,
            DipTriggered = false,
            IsAllowed = aggressiveModeActive,
            Reason = "Loading market diagnostics in the background."
        };

    private static AutoConfigRecommendation BuildFastAutoRecommendation(
        GridBotSettings runtimeSettings,
        GridOptions gridOptions,
        bool fast) =>
        new()
        {
            StrategyType = runtimeSettings.StrategyType,
            Reason = fast
                ? "Loading market recommendation in the background."
                : "Current runtime settings.",
            LowerPrice = gridOptions.LowerPrice,
            UpperPrice = gridOptions.UpperPrice,
            Step = gridOptions.Step,
            OrderSizeUsdt = gridOptions.OrderSizeUsdt,
            StopLowerPrice = gridOptions.StopLowerPrice,
            StopUpperPrice = gridOptions.StopUpperPrice,
            StrategyConfigJson = runtimeSettings.StrategyConfigJson,
            Metrics = new AutoConfigMetrics
            {
                LastPrice = 0m,
                Support = gridOptions.LowerPrice,
                Resistance = gridOptions.UpperPrice
            }
        };

    private async Task<IReadOnlyList<DashboardConfigSummaryItem>> BuildConfigSummariesAsync(
        IReadOnlyList<GridBotSettings> profiles,
        string selectedSymbol,
        IReadOnlyList<DashboardPairScoreItem> pairScores,
        CancellationToken cancellationToken)
    {
        var summaries = new List<DashboardConfigSummaryItem>(profiles.Count);
        var scoreBySymbol = pairScores.ToDictionary(score => score.Symbol, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            var state = await _repository.GetBotStateAsync(profile.Symbol, cancellationToken);
            var isPaused = state?.IsPaused == true || profile.StrategyType is TradingStrategyType.Pause or TradingStrategyType.NoTrade;
            scoreBySymbol.TryGetValue(profile.Symbol, out var pairScore);
            summaries.Add(new DashboardConfigSummaryItem
            {
                Symbol = profile.Symbol,
                Category = profile.Category,
                StrategyName = profile.StrategyType.ToString(),
                StrategyMode = profile.StrategySelectionMode.ToString().ToLowerInvariant(),
                Status = isPaused ? "paused" : "in_progress",
                DailyRealizedPnl = state?.DailyRealizedPnl ?? 0m,
                TotalRealizedPnl = state?.TotalRealizedPnl ?? 0m,
                PairScore = pairScore?.Score ?? 0m,
                PairScoreLabel = pairScore?.Label ?? "Unknown",
                SuggestedOrderSizeMultiplier = pairScore?.SuggestedOrderSizeMultiplier ?? 1m,
                PairScoreReasons = pairScore?.Reasons ?? [],
                IsSelected = string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase),
                UpdatedAt = state?.UpdatedAt ?? profile.UpdatedAt
            });
        }

        return summaries
            .OrderByDescending(summary => summary.PairScore)
            .ThenByDescending(summary => summary.TotalRealizedPnl)
            .ThenByDescending(summary => summary.DailyRealizedPnl)
            .ThenBy(summary => summary.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<DashboardPairScoreItem>> BuildPairScoresAsync(
        IReadOnlyList<GridBotSettings> profiles,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(4);
        var scoreTasks = profiles.Select(async profile =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var stateTask = _repository.GetBotStateAsync(profile.Symbol, cancellationToken);
                var ordersTask = _repository.GetOrdersAsync(profile.Symbol, cancellationToken);
                var noTradeReasonsTask = _repository.GetNoTradeReasonsAsync(profile.Symbol, 1, cancellationToken);
                var marketTask = GetPairScoreMarketMetricsAsync(profile, cancellationToken);

                await Task.WhenAll(new Task[] { stateTask, ordersTask, noTradeReasonsTask, marketTask });
                return BuildPairScore(
                    profile,
                    await stateTask,
                    await ordersTask,
                    (await noTradeReasonsTask).FirstOrDefault(),
                    await marketTask,
                    now);
            }
            finally
            {
                throttle.Release();
            }
        });

        var scores = await Task.WhenAll(scoreTasks);

        return scores
            .OrderByDescending(score => score.Score)
            .ThenByDescending(score => score.RecentWinRate)
            .ThenByDescending(score => score.VolatilityPercent)
            .ThenBy(score => score.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<PairScoreMarketMetrics> GetPairScoreMarketMetricsAsync(
        GridBotSettings profile,
        CancellationToken cancellationToken)
    {
        decimal spreadPercent = 0m;
        decimal lastPrice = 0m;
        try
        {
            var ticker = await _bybitRestClient.GetTickerAsync(profile.Category, profile.Symbol, cancellationToken);
            lastPrice = ticker.LastPrice;
            var middle = (ticker.Bid1Price + ticker.Ask1Price) / 2m;
            spreadPercent = middle > 0m
                ? decimal.Max(0m, (ticker.Ask1Price - ticker.Bid1Price) / middle * 100m)
                : 0m;
        }
        catch
        {
            spreadPercent = 0m;
        }

        try
        {
            var candles = await _bybitRestClient.GetKlinesAsync(
                profile.Category,
                profile.Symbol,
                AnalysisDefaults.AutoRecommendationCandleInterval,
                60,
                cancellationToken);
            var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
            if (ordered.Length == 0)
            {
                return new PairScoreMarketMetrics(lastPrice, spreadPercent, 0m, 0m);
            }

            var close = ordered[^1].Close;
            var volatilityPercent = close > 0m
                ? (ordered.Max(candle => candle.High) - ordered.Min(candle => candle.Low)) / close * 100m
                : 0m;
            var recentVolume = ordered.TakeLast(10).Average(candle => candle.Volume);
            var baselineVolume = ordered.Take(Math.Max(1, ordered.Length - 10)).DefaultIfEmpty(ordered[0]).Average(candle => candle.Volume);
            var volumeRatio = baselineVolume > 0m ? recentVolume / baselineVolume : 0m;

            return new PairScoreMarketMetrics(close, spreadPercent, volatilityPercent, volumeRatio);
        }
        catch
        {
            return new PairScoreMarketMetrics(lastPrice, spreadPercent, 0m, 0m);
        }
    }

    private static DashboardPairScoreItem BuildPairScore(
        GridBotSettings profile,
        BotState? state,
        IReadOnlyCollection<GridOrder> orders,
        NoTradeReasonRecord? lastNoTradeReason,
        PairScoreMarketMetrics market,
        DateTimeOffset now)
    {
        var score = 50m;
        var reasons = new List<string>();
        var isPausedStrategy = profile.StrategyType is TradingStrategyType.NoTrade or TradingStrategyType.Pause;
        if (isPausedStrategy)
        {
            score -= 20m;
            reasons.Add($"strategy paused {profile.StrategyType}");
        }

        if (market.VolatilityPercent >= 0.8m && market.VolatilityPercent <= 8m)
        {
            score += 15m;
            reasons.Add($"volatility ok {market.VolatilityPercent:0.##}%");
        }
        else if (market.VolatilityPercent is > 0m and < 0.8m)
        {
            score -= 15m;
            reasons.Add($"volatility low {market.VolatilityPercent:0.##}%");
        }
        else if (market.VolatilityPercent > 8m)
        {
            score -= 10m;
            reasons.Add($"volatility high {market.VolatilityPercent:0.##}%");
        }

        if (market.SpreadPercent > 0m && market.SpreadPercent <= 0.2m)
        {
            score += 15m;
            reasons.Add($"spread tight {market.SpreadPercent:0.###}%");
        }
        else if (market.SpreadPercent > 0.5m)
        {
            score -= 20m;
            reasons.Add($"spread wide {market.SpreadPercent:0.###}%");
        }

        if (market.VolumeRatio >= 0.7m)
        {
            score += 10m;
            reasons.Add($"volume enough {market.VolumeRatio:0.##}x");
        }
        else if (market.VolumeRatio is > 0m and < 0.35m)
        {
            score -= 10m;
            reasons.Add($"volume weak {market.VolumeRatio:0.##}x");
        }

        var recentClosed = orders
            .Where(order => order.Side == TradeSide.Sell &&
                order.Status == OrderStatus.Filled &&
                (order.FilledAt ?? order.UpdatedAt) >= now.AddHours(-48))
            .ToArray();
        var filledTradesCount = orders.Count(order => order.Status == OrderStatus.Filled);
        if (filledTradesCount == 0)
        {
            score -= 10m;
            reasons.Add("no filled trades yet");
        }

        var recentWinRate = recentClosed.Length == 0
            ? 0m
            : recentClosed.Count(order => order.RealizedPnl > order.FeePaid) / (decimal)recentClosed.Length * 100m;
        if (recentClosed.Length >= 2 && recentWinRate >= 50m)
        {
            score += 12m;
            reasons.Add($"recent win rate {recentWinRate:0.#}%");
        }
        else if (recentClosed.Length >= 2)
        {
            score -= 12m;
            reasons.Add($"recent win rate {recentWinRate:0.#}%");
        }

        var recentLossSell = recentClosed
            .Where(order => (order.FilledAt ?? order.UpdatedAt) >= now.AddHours(-6))
            .Any(order => order.RealizedPnl <= order.FeePaid);
        if (recentLossSell)
        {
            score -= 18m;
            reasons.Add("recent loss sell penalty");
        }

        var dailyPnl = state?.DailyRealizedPnl ?? 0m;
        var totalPnl = state?.TotalRealizedPnl ?? 0m;
        if (dailyPnl > 0m || totalPnl > 0m)
        {
            score += 8m;
            reasons.Add("positive realized PnL");
        }
        else if (dailyPnl < 0m)
        {
            score -= 10m;
            reasons.Add("negative daily PnL");
        }

        var currentDrawdownPercent = CalculatePairCurrentDrawdownPercent(state, market.LastPrice);
        if (currentDrawdownPercent >= 3m)
        {
            score -= 25m;
            reasons.Add($"position drawdown {currentDrawdownPercent:0.##}%");
        }
        else if (currentDrawdownPercent >= 1m)
        {
            score -= 10m;
            reasons.Add($"position drawdown {currentDrawdownPercent:0.##}%");
        }

        if (lastNoTradeReason is not null)
        {
            var reasonPenalty = GetNoTradeReasonScorePenalty(lastNoTradeReason.ReasonCode);
            if (reasonPenalty > 0m)
            {
                score -= reasonPenalty;
                reasons.Add($"no-trade {lastNoTradeReason.ReasonCode}");
            }

            if (now - lastNoTradeReason.CreatedAt >= TimeSpan.FromHours(1))
            {
                score -= 5m;
                reasons.Add("stale no-trade reason");
            }
        }

        score = decimal.Max(0m, decimal.Min(100m, decimal.Round(score, 2, MidpointRounding.AwayFromZero)));
        return new DashboardPairScoreItem
        {
            Symbol = profile.Symbol,
            Category = profile.Category,
            Score = score,
            Label = score >= 75m ? "Hot" : score >= 60m ? "Good" : score >= 40m ? "Neutral" : "Avoid",
            SuggestedOrderSizeMultiplier = score >= 75m ? 1.4m : score >= 60m ? 1.15m : score < 35m ? 0.5m : 1m,
            SpreadPercent = decimal.Round(market.SpreadPercent, 4, MidpointRounding.AwayFromZero),
            VolatilityPercent = decimal.Round(market.VolatilityPercent, 4, MidpointRounding.AwayFromZero),
            VolumeRatio = decimal.Round(market.VolumeRatio, 4, MidpointRounding.AwayFromZero),
            RecentWinRate = decimal.Round(recentWinRate, 2, MidpointRounding.AwayFromZero),
            CurrentDrawdownPercent = decimal.Round(currentDrawdownPercent, 4, MidpointRounding.AwayFromZero),
            LastNoTradeReason = lastNoTradeReason?.ReasonCode.ToString(),
            Reasons = reasons.Count == 0 ? ["market data limited"] : reasons
        };
    }

    private static decimal CalculatePairCurrentDrawdownPercent(BotState? state, decimal currentPrice)
    {
        if (state is null || state.BaseAssetQuantity <= 0m || state.AverageEntryPrice <= 0m || currentPrice <= 0m)
        {
            return 0m;
        }

        return currentPrice >= state.AverageEntryPrice
            ? 0m
            : (state.AverageEntryPrice - currentPrice) / state.AverageEntryPrice * 100m;
    }

    private static decimal GetNoTradeReasonScorePenalty(NoTradeReason reason) => reason switch
    {
        NoTradeReason.DumpDetected => 30m,
        NoTradeReason.BtcRiskOff => 30m,
        NoTradeReason.DailyLossLimitReached => 30m,
        NoTradeReason.AggressiveStopLoss => 25m,
        NoTradeReason.AggressiveCooldown => 20m,
        NoTradeReason.HighVolatility => 20m,
        NoTradeReason.MaxPositionReached => 15m,
        NoTradeReason.PriceOutsideRange => 12m,
        NoTradeReason.ExpectedProfitTooLow => 10m,
        NoTradeReason.ScoreTooLow => 8m,
        NoTradeReason.UnknownMarketPhase => 6m,
        _ => 0m
    };

    private sealed record PairScoreMarketMetrics(
        decimal LastPrice,
        decimal SpreadPercent,
        decimal VolatilityPercent,
        decimal VolumeRatio);

    private async Task<IReadOnlyList<Candle>> GetAnalysisCandlesAsync(GridOptions gridOptions, CancellationToken cancellationToken)
    {
        try
        {
            return await _bybitRestClient.GetKlinesAsync(
                gridOptions.Category,
                gridOptions.Symbol,
                AnalysisDefaults.AutoRecommendationCandleInterval,
                AnalysisDefaults.AutoRecommendationLookbackCandles,
                cancellationToken);
        }
        catch
        {
            return [];
        }
    }

    private MarketRegimeAnalysis AnalyzeMarketRegime(IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0)
        {
            return new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Danger,
                Confidence = 0m,
                Recommendation = "Market regime analysis is unavailable."
            };
        }

        return _marketRegimeAnalyzer.Analyze(candles);
    }

    private SignalAnalysis AnalyzeSignal(IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0)
        {
            return new SignalAnalysis
            {
                Signal = SignalType.Avoid,
                Confidence = 0m,
                Reason = "Signal analysis is unavailable."
            };
        }

        return _signalAnalyzer.Analyze(candles);
    }

    private async Task<MarketPhaseResult> DetectMarketPhaseAsync(
        GridOptions gridOptions,
        IReadOnlyList<Candle> candles,
        decimal? currentPrice,
        CancellationToken cancellationToken)
    {
        var btcCandles = await GetBtcCandlesForPhaseAsync(gridOptions, cancellationToken);
        return DetectMarketPhase(gridOptions, candles, btcCandles, currentPrice);
    }

    private MarketPhaseResult DetectMarketPhase(
        GridOptions gridOptions,
        IReadOnlyList<Candle> candles,
        IReadOnlyCollection<Candle> btcCandles,
        decimal? currentPrice)
    {
        var price = currentPrice ?? candles.OrderBy(candle => candle.OpenTime).LastOrDefault()?.Close ?? 0m;
        return _priceActionPhaseDetector.Detect(gridOptions, price, candles, btcCandles);
    }

    private async Task<IReadOnlyList<Candle>> GetBtcCandlesForPhaseAsync(
        GridOptions gridOptions,
        CancellationToken cancellationToken)
    {
        if (!gridOptions.BtcFilterEnabled)
        {
            return [];
        }

        try
        {
            return await _bybitRestClient.GetKlinesAsync(
                "spot",
                "BTCUSDT",
                AnalysisDefaults.AutoRecommendationCandleInterval,
                Math.Max(20, gridOptions.BtcLookbackCandles),
                cancellationToken);
        }
        catch
        {
            return [];
        }
    }

    public async Task<UpdateSettingsResponse> ApplyAutoRecommendationAsync(string? symbol, CancellationToken cancellationToken)
    {
        var profiles = await EnsureRuntimeSettingsProfilesAsync(cancellationToken);
        var selectedSymbol = NormalizeOptionalSymbol(symbol);
        var runtimeSettings = selectedSymbol is null
            ? profiles[0]
            : profiles.FirstOrDefault(profile => string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase));
        if (runtimeSettings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = selectedSymbol,
                Message = "Cannot apply auto recommendation.",
                Errors = [$"Runtime settings profile {selectedSymbol} does not exist."]
            };
        }

        var gridOptions = RuntimeGridOptionsFactory.ToGridOptions(runtimeSettings, _defaultGridOptions);
        var candles = await GetAnalysisCandlesAsync(gridOptions, cancellationToken);
        if (candles.Count == 0)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = runtimeSettings.Symbol,
                Message = "Cannot apply auto recommendation.",
                Errors = ["Market data is unavailable."]
            };
        }

        var state = await _repository.GetBotStateAsync(runtimeSettings.Symbol, cancellationToken);
        var marketRegime = AnalyzeMarketRegime(candles);
        var marketPhase = await DetectMarketPhaseAsync(gridOptions, candles, state?.LastObservedPrice, cancellationToken);
        var recommendation = _autoStrategySelector.Recommend(
            gridOptions,
            marketRegime,
            marketPhase,
            candles,
            IsAggressiveModeActive(gridOptions, state, DateTimeOffset.UtcNow));
        var activeOrders = await _repository.GetActiveOrdersAsync(runtimeSettings.Symbol, cancellationToken);
        var recommendedSettings = BuildRecommendedSettings(runtimeSettings, recommendation, StrategySelectionMode.Auto, DateTimeOffset.UtcNow);
        recommendedSettings = UseReduceOnlyWhenNoTradeWouldLeavePosition(recommendedSettings, state);
        var safetyErrors = AutoRecommendationApplySafety.Validate(
            runtimeSettings,
            state,
            activeOrders,
            recommendation,
            recommendedSettings,
            _riskOptions,
            _strategy);
        if (safetyErrors.Count > 0)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = runtimeSettings.Symbol,
                Message = "Auto recommendation was not applied by safety checks.",
                Errors = safetyErrors
            };
        }

        await _repository.SaveRuntimeSettingsAsync(recommendedSettings, cancellationToken);
        var resumeMessage = await TryClearPauseForSettingsAsync(recommendedSettings, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = runtimeSettings.Symbol,
            Message = $"Auto recommendation applied: {recommendedSettings.StrategyType}. {recommendation.Reason}{resumeMessage}"
        };
    }

    private static GridBotSettings UseReduceOnlyWhenNoTradeWouldLeavePosition(
        GridBotSettings settings,
        BotState? state)
    {
        if (settings.StrategyType != TradingStrategyType.NoTrade ||
            state is null ||
            state.BaseAssetQuantity <= 0m)
        {
            return settings;
        }

        return new GridBotSettings
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            StrategySelectionMode = settings.StrategySelectionMode,
            StrategyType = TradingStrategyType.ReduceOnly,
            StrategyConfigJson = settings.StrategyConfigJson,
            LowerPrice = settings.LowerPrice,
            UpperPrice = settings.UpperPrice,
            Step = settings.Step,
            OrderSizeUsdt = settings.OrderSizeUsdt,
            StopLowerPrice = settings.StopLowerPrice,
            StopUpperPrice = settings.StopUpperPrice,
            UpdatedAt = settings.UpdatedAt
        };
    }

    private static AutoConfigRecommendation UseReduceOnlyRecommendationWhenNoTradeWouldLeavePosition(
        AutoConfigRecommendation recommendation,
        GridBotSettings recommendedSettings)
    {
        if (recommendation.StrategyType != TradingStrategyType.NoTrade ||
            recommendedSettings.StrategyType != TradingStrategyType.ReduceOnly)
        {
            return recommendation;
        }

        return new AutoConfigRecommendation
        {
            StrategyType = TradingStrategyType.ReduceOnly,
            Reason = $"Position is open; using ReduceOnly instead of NoTrade. {recommendation.Reason}",
            LowerPrice = recommendation.LowerPrice,
            UpperPrice = recommendation.UpperPrice,
            Step = recommendation.Step,
            OrderSizeUsdt = recommendation.OrderSizeUsdt,
            StopLowerPrice = recommendation.StopLowerPrice,
            StopUpperPrice = recommendation.StopUpperPrice,
            StrategyConfigJson = recommendation.StrategyConfigJson,
            Metrics = recommendation.Metrics
        };
    }

    public async Task<UpdateSettingsResponse> ApplyRecommendationForSelectedStrategyAsync(
        UpdateSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var category = string.IsNullOrWhiteSpace(request.Category) ? "spot" : request.Category.Trim().ToLowerInvariant();
        var strategyMode = ParseStrategySelectionMode(request.StrategyMode);
        var strategyType = ParseTradingStrategyType(request.StrategyType);
        var strategyConfigJson = NormalizeStrategyConfigJson(request.StrategyConfigJson);
        var errors = ValidateRequest(symbol, category, request);
        if (strategyMode is null)
        {
            errors.Add("Strategy mode must be manual or auto.");
        }

        if (strategyType is null)
        {
            errors.Add("Strategy type must be grid, dca, combo, btd, signal, trendfollow, hybrid, reduceonly, or notrade.");
        }

        if (strategyConfigJson is null)
        {
            errors.Add("Strategy config JSON is invalid.");
        }

        if (errors.Count > 0)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = symbol,
                Message = "Validation failed.",
                Errors = errors
            };
        }

        var currentSettings = new GridBotSettings
        {
            Symbol = symbol,
            Category = category,
            StrategySelectionMode = strategyMode!.Value,
            StrategyType = strategyType!.Value,
            StrategyConfigJson = strategyConfigJson!,
            LowerPrice = request.LowerPrice,
            UpperPrice = request.UpperPrice,
            Step = request.Step,
            OrderSizeUsdt = request.OrderSizeUsdt,
            StopLowerPrice = request.StopLowerPrice,
            StopUpperPrice = request.StopUpperPrice,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var gridOptions = RuntimeGridOptionsFactory.ToGridOptions(currentSettings, _defaultGridOptions);
        var candles = await GetAnalysisCandlesAsync(gridOptions, cancellationToken);
        if (candles.Count == 0)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = symbol,
                Message = "Cannot apply recommendation for selected strategy.",
                Errors = ["Market data is unavailable."]
            };
        }

        var recommendation = _autoStrategySelector.RecommendForStrategy(
            gridOptions,
            AnalyzeMarketRegime(candles),
            candles,
            strategyType!.Value);
        var recommendedSettings = BuildRecommendedSettings(currentSettings, recommendation, strategyMode!.Value, DateTimeOffset.UtcNow);
        var state = await _repository.GetBotStateAsync(symbol, cancellationToken);
        var activeOrders = await _repository.GetActiveOrdersAsync(symbol, cancellationToken);
        var safetyErrors = AutoRecommendationApplySafety.Validate(
            currentSettings,
            state,
            activeOrders,
            recommendation,
            recommendedSettings,
            _riskOptions,
            _strategy);
        safetyErrors = safetyErrors
            .Where(error => !error.StartsWith("Recommended settings are too close", StringComparison.Ordinal))
            .ToArray();
        var forceResumeAfterStopPause = state?.IsPaused == true && IsStopBoundaryPause(state.PauseReason);
        if (safetyErrors.Count > 0 && !forceResumeAfterStopPause)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = symbol,
                Message = "Selected strategy recommendation was not applied by safety checks.",
                Errors = safetyErrors
            };
        }

        await _repository.SaveRuntimeSettingsAsync(recommendedSettings, cancellationToken);
        var resumeMessage = await TryClearPauseForSettingsAsync(recommendedSettings, cancellationToken);
        var forceMessage = forceResumeAfterStopPause && safetyErrors.Count > 0
            ? $" Safety checks were bypassed because trading was paused by stop boundaries: {string.Join(" | ", safetyErrors)}."
            : string.Empty;

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = symbol,
            Message = $"Recommendation applied to selected {strategyType.Value} strategy without changing symbol or strategy.{resumeMessage}{forceMessage}"
        };
    }

    public async Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var category = string.IsNullOrWhiteSpace(request.Category) ? "spot" : request.Category.Trim().ToLowerInvariant();
        var strategyMode = ParseStrategySelectionMode(request.StrategyMode);
        var strategyType = ParseTradingStrategyType(request.StrategyType);
        var strategyConfigJson = NormalizeStrategyConfigJson(request.StrategyConfigJson);
        var errors = ValidateRequest(symbol, category, request);
        if (strategyMode is null)
        {
            errors.Add("Strategy mode must be manual or auto.");
        }

        if (strategyType is null)
        {
            errors.Add("Strategy type must be grid, dca, combo, btd, signal, trendfollow, hybrid, reduceonly, or notrade.");
        }

        if (strategyConfigJson is null)
        {
            errors.Add("Strategy config JSON is invalid.");
        }

        if (errors.Count > 0)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Message = "Validation failed.",
                Errors = errors
            };
        }

        try
        {
            await _bybitRestClient.GetInstrumentInfoAsync(category, symbol, cancellationToken);
        }
        catch (Exception exception)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Message = "Instrument validation failed.",
                Errors = [$"Bybit rejected instrument {symbol}: {exception.Message}"]
            };
        }

        var settings = new GridBotSettings
        {
            Symbol = symbol,
            Category = category,
            StrategySelectionMode = strategyMode!.Value,
            StrategyType = strategyType!.Value,
            StrategyConfigJson = strategyConfigJson!,
            LowerPrice = request.LowerPrice,
            UpperPrice = request.UpperPrice,
            Step = request.Step,
            OrderSizeUsdt = request.OrderSizeUsdt,
            StopLowerPrice = request.StopLowerPrice,
            StopUpperPrice = request.StopUpperPrice,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _repository.SaveRuntimeSettingsAsync(settings, cancellationToken);
        var resumeMessage = await TryClearPauseForSettingsAsync(settings, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = symbol,
            Message = $"Settings saved. The bot will apply them on the next loop.{resumeMessage}"
        };
    }

    public async Task<UpdateSettingsResponse> DeleteSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var profiles = await EnsureRuntimeSettingsProfilesAsync(cancellationToken);
        if (profiles.Count <= 1)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Cannot delete settings.",
                Errors = ["At least one runtime settings profile must remain."]
            };
        }

        var existing = await _repository.GetRuntimeSettingsAsync(normalizedSymbol, cancellationToken);
        if (existing is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Cannot delete settings.",
                Errors = [$"Runtime settings profile {normalizedSymbol} does not exist."]
            };
        }

        await _repository.DeleteRuntimeSettingsAsync(normalizedSymbol, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = $"Settings profile {normalizedSymbol} deleted. Active orders will be cancelled on the next bot loop."
        };
    }

    public async Task<UpdateSettingsResponse> ResumeTradingAsync(string? symbol, CancellationToken cancellationToken)
    {
        var profiles = await EnsureRuntimeSettingsProfilesAsync(cancellationToken);
        var selectedSymbol = NormalizeOptionalSymbol(symbol);
        var runtimeSettings = selectedSymbol is null
            ? profiles[0]
            : profiles.FirstOrDefault(profile => string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase));
        if (runtimeSettings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = selectedSymbol,
                Message = "Cannot resume trading.",
                Errors = [$"Runtime settings profile {selectedSymbol} does not exist."]
            };
        }

        var state = await _repository.GetBotStateAsync(runtimeSettings.Symbol, cancellationToken);
        if (state is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = runtimeSettings.Symbol,
                Message = "Cannot resume trading.",
                Errors = [$"No bot state exists for {runtimeSettings.Symbol} yet."]
            };
        }

        if (!state.IsPaused)
        {
            return new UpdateSettingsResponse
            {
                Success = true,
                Symbol = runtimeSettings.Symbol,
                Message = $"Trading is already active for {runtimeSettings.Symbol}."
            };
        }

        var currentPrice = await GetCurrentOrLastPriceAsync(runtimeSettings.Category, runtimeSettings.Symbol, state.LastObservedPrice, cancellationToken);
        var resumeBlockReason = GetResumeBlockReason(runtimeSettings, currentPrice);
        if (resumeBlockReason is not null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = runtimeSettings.Symbol,
                Message = "Cannot resume trading.",
                Errors = [resumeBlockReason]
            };
        }

        state.IsPaused = false;
        state.PauseReason = null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = runtimeSettings.Symbol,
            Message = $"Trading resumed for {runtimeSettings.Symbol}. The bot will continue on the next loop."
        };
    }

    public async Task<UpdateSettingsResponse> CancelActiveOrdersAsync(string? symbol, CancellationToken cancellationToken)
    {
        var profiles = await EnsureRuntimeSettingsProfilesAsync(cancellationToken);
        var selectedSymbol = NormalizeOptionalSymbol(symbol);
        var runtimeSettings = selectedSymbol is null
            ? profiles[0]
            : profiles.FirstOrDefault(profile => string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase));
        if (runtimeSettings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = selectedSymbol,
                Message = "Cannot cancel active orders.",
                Errors = [$"Runtime settings profile {selectedSymbol} does not exist."]
            };
        }

        var activeOrders = await _repository.GetActiveOrdersAsync(runtimeSettings.Symbol, cancellationToken);
        foreach (var order in activeOrders)
        {
            if (_appOptions.TradingMode != TradingMode.Paper)
            {
                await _bybitRestClient.CancelOrderAsync(order.Category, order.Symbol, order.BybitOrderId, order.OrderLinkId, cancellationToken);
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.UpsertOrderAsync(order, cancellationToken);
        }

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = runtimeSettings.Symbol,
            Message = $"Cancelled {activeOrders.Count} active orders for {runtimeSettings.Symbol}."
        };
    }

    public async Task<UpdateSettingsResponse> ResetSpotStatisticsAsync(CancellationToken cancellationToken)
    {
        var deletedRows = await _repository.ResetSpotStatisticsAsync(cancellationToken);
        return new UpdateSettingsResponse
        {
            Success = true,
            Message = $"Spot statistics reset. Deleted rows: {deletedRows}."
        };
    }

    public string RenderDashboardPage() => """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Bybit Grid Bot</title>
  <style>
    :root {
      --bg: #f4efe3;
      --surface: rgba(255,255,255,0.78);
      --ink: #1d231f;
      --muted: #5f665f;
      --accent: #c6672f;
      --accent-2: #17664e;
      --danger: #b13622;
      --border: rgba(29,35,31,0.12);
      --shadow: 0 18px 60px rgba(58, 42, 25, 0.12);
      font-family: "IBM Plex Sans", "Segoe UI", sans-serif;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: var(--ink);
      background:
        radial-gradient(circle at top left, rgba(198,103,47,0.2), transparent 28%),
        radial-gradient(circle at bottom right, rgba(23,102,78,0.16), transparent 30%),
        linear-gradient(135deg, #f7f1e5, #f0e7d7 56%, #ebdfca);
      min-height: 100vh;
    }
    .shell {
      max-width: 1320px;
      margin: 0 auto;
      padding: 28px;
    }
    .hero {
      display: grid;
      grid-template-columns: 1.2fr 0.8fr;
      gap: 20px;
      align-items: stretch;
      margin-bottom: 20px;
    }
    .panel {
      background: var(--surface);
      backdrop-filter: blur(18px);
      border: 1px solid var(--border);
      border-radius: 24px;
      box-shadow: var(--shadow);
      overflow: hidden;
    }
    .hero-main {
      padding: 28px;
      position: relative;
    }
    .hero-main::after {
      content: "";
      position: absolute;
      inset: auto -120px -120px auto;
      width: 280px;
      height: 280px;
      border-radius: 50%;
      background: radial-gradient(circle, rgba(198,103,47,0.28), transparent 70%);
    }
    h1 {
      margin: 0 0 10px;
      font: 700 44px/0.95 "Space Grotesk", "IBM Plex Sans", sans-serif;
      letter-spacing: -0.04em;
      max-width: 8ch;
    }
    .subtle {
      color: var(--muted);
      font-size: 15px;
      line-height: 1.5;
      max-width: 60ch;
    }
    .badge-row {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
      margin-top: 22px;
    }
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 10px 14px;
      border-radius: 999px;
      background: rgba(29,35,31,0.06);
      font-size: 13px;
    }
    .dashboard-loading {
      position: sticky;
      top: 0;
      z-index: 20;
      display: none;
      align-items: center;
      gap: 10px;
      margin-bottom: 14px;
      padding: 10px 14px;
      border: 1px solid rgba(29,35,31,0.12);
      border-radius: 12px;
      background: rgba(255,252,246,0.94);
      box-shadow: 0 12px 40px rgba(58,42,25,0.12);
      color: var(--muted);
      font-weight: 700;
    }
    .dashboard-loading.active { display: inline-flex; }
    .spinner {
      width: 16px;
      height: 16px;
      border: 2px solid rgba(29,35,31,0.16);
      border-top-color: var(--accent);
      border-radius: 999px;
      animation: spin 0.8s linear infinite;
    }
    @keyframes spin {
      to { transform: rotate(360deg); }
    }
    .stats {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 14px;
      margin-bottom: 20px;
    }
    .stat {
      padding: 18px;
      border-radius: 20px;
      background: var(--surface);
      border: 1px solid var(--border);
      box-shadow: var(--shadow);
      min-height: 124px;
      animation: fadeUp .45s ease both;
    }
    .stat .label {
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: .14em;
    }
    .stat .value {
      margin-top: 12px;
      font: 700 31px/1 "Space Grotesk", "IBM Plex Sans", sans-serif;
    }
    .value.positive { color: var(--accent-2); }
    .value.negative { color: var(--danger); }
    .layout {
      display: grid;
      grid-template-columns: 0.9fr 1.1fr;
      gap: 20px;
    }
    .section {
      padding: 22px;
    }
    .section-head {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      align-items: center;
      flex-wrap: wrap;
      margin-bottom: 18px;
    }
    .section-head h2 { margin-bottom: 0; }
    h2 {
      margin: 0 0 18px;
      font: 700 22px/1.05 "Space Grotesk", "IBM Plex Sans", sans-serif;
      letter-spacing: -0.03em;
    }
    .profile-tabs {
      display: flex;
      gap: 8px;
      flex-wrap: wrap;
      margin-bottom: 16px;
    }
    .profile-tab {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 9px 10px 9px 12px;
      border-radius: 999px;
      background: rgba(29,35,31,0.07);
      color: var(--ink);
      border: 1px solid transparent;
      box-shadow: none;
      letter-spacing: 0;
    }
    .profile-tab.active {
      background: rgba(198,103,47,0.14);
      border-color: rgba(198,103,47,0.28);
      color: var(--accent);
    }
    .profile-tab.new {
      background: rgba(23,102,78,0.12);
      color: var(--accent-2);
    }
    .profile-tab .close-tab {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 18px;
      height: 18px;
      border-radius: 999px;
      background: rgba(29,35,31,0.12);
      font-size: 14px;
      line-height: 1;
    }
    form {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }
    label {
      display: block;
      font-size: 13px;
      color: var(--muted);
      margin-bottom: 6px;
    }
    input, select, textarea {
      width: 100%;
      padding: 12px 14px;
      border-radius: 14px;
      border: 1px solid rgba(29,35,31,0.14);
      background: rgba(255,255,255,0.84);
      color: var(--ink);
      font: inherit;
    }
    textarea {
      min-height: 148px;
      resize: vertical;
      font-family: "IBM Plex Mono", monospace;
      font-size: 13px;
      line-height: 1.45;
    }
    .full { grid-column: 1 / -1; }
    .preset-box {
      margin-bottom: 16px;
      padding: 14px;
      border-radius: 18px;
      background: rgba(29,35,31,0.04);
      border: 1px solid rgba(29,35,31,0.08);
    }
    .preset-actions {
      display: flex;
      gap: 10px;
      align-items: center;
      flex-wrap: wrap;
      margin-top: 10px;
    }
    .secondary-button {
      background: rgba(29,35,31,0.86);
      box-shadow: 0 12px 28px rgba(29,35,31,.14);
    }
    .compact-button {
      padding: 10px 13px;
      border-radius: 12px;
      font-size: 12px;
    }
    .auto-actions {
      display: flex;
      flex-direction: column;
      gap: 8px;
      align-items: stretch;
    }
    .top-recommendation {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 16px;
      align-items: start;
      margin-top: 22px;
      padding-top: 18px;
      border-top: 1px solid rgba(29,35,31,0.08);
    }
    .history-copy-controls {
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
    }
    .hours-input {
      width: 86px;
      padding: 10px 12px;
      border-radius: 12px;
      font-size: 12px;
    }
    .preset-hint {
      color: var(--muted);
      font-size: 12px;
    }
    .pause-box {
      margin-top: 18px;
      padding: 14px;
      border-radius: 18px;
      background: rgba(177,54,34,0.09);
      border: 1px solid rgba(177,54,34,0.16);
    }
    .pause-box strong {
      display: block;
      margin-bottom: 6px;
      color: var(--danger);
      font-size: 13px;
      letter-spacing: .04em;
      text-transform: uppercase;
    }
    .pause-box p {
      margin: 0 0 12px;
      color: var(--muted);
      font-size: 13px;
      line-height: 1.45;
    }
    button {
      appearance: none;
      border: 0;
      border-radius: 16px;
      padding: 14px 18px;
      background: linear-gradient(135deg, var(--accent), #df8b3f);
      color: white;
      font: 700 14px/1 "Space Grotesk", "IBM Plex Sans", sans-serif;
      letter-spacing: .04em;
      cursor: pointer;
      transition: transform .18s ease, box-shadow .18s ease;
      box-shadow: 0 14px 32px rgba(198,103,47,.22);
    }
    button:hover { transform: translateY(-1px); }
    .status {
      margin-top: 14px;
      min-height: 22px;
      font-size: 14px;
      color: var(--muted);
    }
    .status.error { color: var(--danger); }
    .status.ok { color: var(--accent-2); }
    .danger-button {
      background: linear-gradient(135deg, var(--danger), #d76545);
      box-shadow: 0 14px 32px rgba(177,54,34,.18);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 14px;
    }
    th, td {
      text-align: left;
      padding: 12px 10px;
      border-bottom: 1px solid rgba(29,35,31,0.08);
      vertical-align: top;
    }
    th {
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: .14em;
      color: var(--muted);
    }
    .token { font-family: "IBM Plex Mono", monospace; font-size: 12px; }
    .table-wrap { overflow: auto; }
    .config-summary-panel { margin-bottom: 20px; }
    .config-profit-totals {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      justify-content: flex-end;
    }
    .config-profit-total {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 9px 12px;
      border-radius: 999px;
      background: rgba(29,35,31,0.06);
      font-size: 13px;
      white-space: nowrap;
    }
    .config-profit-total span {
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: .08em;
    }
    .config-table tbody tr {
      cursor: pointer;
      transition: background .18s ease;
    }
    .config-table tbody tr:hover,
    .config-table tbody tr.selected {
      background: rgba(198,103,47,0.08);
    }
    .config-symbol {
      display: flex;
      flex-direction: column;
      gap: 3px;
    }
    .config-symbol span {
      color: var(--muted);
      font-size: 12px;
    }
    .status-pill {
      display: inline-flex;
      align-items: center;
      padding: 7px 10px;
      border-radius: 999px;
      background: rgba(23,102,78,0.12);
      color: var(--accent-2);
      font-size: 12px;
      font-weight: 700;
      white-space: nowrap;
    }
    .status-pill.paused {
      background: rgba(177,54,34,0.14);
      color: var(--danger);
    }
    .pnl.positive { color: var(--accent-2); font-weight: 700; }
    .pnl.negative { color: var(--danger); font-weight: 700; }
    .pill {
      display: inline-block;
      padding: 7px 10px;
      border-radius: 999px;
      background: rgba(23,102,78,0.12);
      color: var(--accent-2);
      font-size: 12px;
    }
    .pill.paused {
      background: rgba(177,54,34,0.14);
      color: var(--danger);
    }
    .grid-list {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }
    .grid-chip {
      border-radius: 999px;
      padding: 10px 14px;
      background: rgba(29,35,31,0.06);
      font-family: "IBM Plex Mono", monospace;
      font-size: 13px;
    }
    .regime-card {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 12px;
      align-items: start;
      margin-bottom: 20px;
    }
    .regime-title {
      font-size: 28px;
      font-weight: 900;
      text-transform: capitalize;
    }
    .no-trade-title {
      font-size: clamp(22px, 3vw, 28px);
      line-height: 1.12;
      letter-spacing: 0;
      text-transform: none;
      overflow-wrap: anywhere;
    }
    .regime-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-top: 12px;
    }
    .regime-chip {
      padding: 8px 10px;
      border-radius: 999px;
      background: rgba(29,35,31,0.06);
      font-family: "IBM Plex Mono", monospace;
      font-size: 12px;
    }
    @keyframes fadeUp {
      from { opacity: 0; transform: translateY(10px); }
      to { opacity: 1; transform: translateY(0); }
    }
    @media (max-width: 980px) {
      .hero, .layout { grid-template-columns: 1fr; }
      .top-recommendation { grid-template-columns: 1fr; }
      .top-recommendation .auto-actions { flex-direction: row; flex-wrap: wrap; }
      .stats { grid-template-columns: repeat(2, minmax(0,1fr)); }
      form { grid-template-columns: 1fr; }
      th, td { white-space: nowrap; }
    }
  </style>
</head>
<body>
  <div class="shell">
    <div class="dashboard-loading" id="dashboardLoading">
      <span class="spinner"></span>
      <span id="dashboardLoadingText">Loading dashboard...</span>
    </div>
    <div class="hero">
      <section class="panel hero-main">
        <div class="pill" id="modePill">loading mode</div>
        <h1>Bybit Grid Console</h1>
        <div class="subtle">Live operator panel for the bot: current price, realized profit, open orders, recent execution history, and editable runtime grid configuration.</div>
        <div class="badge-row">
          <div class="badge">Symbol <strong id="heroSymbol">-</strong></div>
          <div class="badge">Price <strong id="heroPrice">-</strong></div>
          <div class="badge">Active <strong id="heroActiveTime">-</strong></div>
          <div class="badge">Last sync <strong id="heroUpdated">-</strong></div>
          <a class="profile-tab new" href="/futures">Futures</a>
        </div>
        <div class="top-recommendation">
          <div>
            <div class="label">Auto Recommendation</div>
            <div class="regime-title" id="autoStrategyTitle">-</div>
            <div class="subtle" id="autoStrategyReason">-</div>
            <div class="regime-meta" id="autoStrategyMeta"></div>
          </div>
          <div class="auto-actions">
            <button type="button" class="secondary-button compact-button" id="refreshAutoRecommendation">Refresh Recommendation</button>
            <button type="button" class="secondary-button compact-button" id="applyAutoRecommendation">Apply Recommendation</button>
            <button type="button" class="secondary-button compact-button" id="applySelectedStrategyRecommendation">Apply To Selected Strategy</button>
          </div>
        </div>
        <div class="pause-box" id="pauseBox" hidden>
          <strong>Trading paused</strong>
          <p id="pauseReason">-</p>
          <button type="button" class="secondary-button" id="autoUpdateResume">Auto Update Config & Resume</button>
          <button type="button" class="secondary-button" id="resumeTrading">Resume Trading</button>
        </div>
      </section>
      <section class="panel section">
        <div class="profile-tabs" id="profileTabs"></div>
        <h2>Runtime Settings</h2>
        <div class="preset-box">
          <label for="settingsPreset">Paste Settings Preset</label>
          <textarea id="settingsPreset" placeholder="Symbol: BILLUSDT&#10;Category: spot&#10;Grid Lower: 1.6&#10;Grid Upper: 2.8&#10;Grid Step: 0.1&#10;Order Size USDT: 20&#10;Stop Lower: 1.5&#10;Stop Upper: 3.0"></textarea>
          <div class="preset-actions">
            <button type="button" class="secondary-button" id="applyPreset">Fill Runtime Settings</button>
            <span class="preset-hint">Review the fields, then press Apply Settings to save.</span>
          </div>
        </div>
        <form id="settingsForm">
          <div><label for="symbol">Symbol</label><input id="symbol" name="symbol" placeholder="BILLUSDT" required /></div>
          <div><label for="category">Category</label><input id="category" name="category" value="spot" required /></div>
          <div><label for="strategyMode">Strategy Mode</label><select id="strategyMode" name="strategyMode"><option value="manual">manual</option><option value="auto">auto</option></select></div>
          <div><label for="strategyType">Strategy Type</label><select id="strategyType" name="strategyType"><option value="grid">Grid</option><option value="dca">DCA</option><option value="combo">Combo Grid + DCA</option><option value="btd">BTD Buy The Dip</option><option value="signal">Signal Bot</option><option value="trendfollow">TrendFollow</option><option value="breakout">Breakout</option><option value="hybrid">Hybrid</option><option value="reduceonly">ReduceOnly / SellOnly</option><option value="pause">Pause</option><option value="notrade">NoTrade</option></select></div>
          <div><label for="lowerPrice">Grid Lower</label><input id="lowerPrice" name="lowerPrice" type="number" step="0.00000001" required /></div>
          <div><label for="upperPrice">Grid Upper</label><input id="upperPrice" name="upperPrice" type="number" step="0.00000001" required /></div>
          <div><label for="step">Grid Step</label><input id="step" name="step" type="number" step="0.00000001" required /></div>
          <div><label for="orderSizeUsdt">Order Size USDT</label><input id="orderSizeUsdt" name="orderSizeUsdt" type="number" step="0.00000001" required /></div>
          <div><label for="stopLowerPrice">Stop Lower</label><input id="stopLowerPrice" name="stopLowerPrice" type="number" step="0.00000001" required /></div>
          <div><label for="stopUpperPrice">Stop Upper</label><input id="stopUpperPrice" name="stopUpperPrice" type="number" step="0.00000001" required /></div>
          <div class="full"><label for="strategyConfigJson">Strategy Config JSON</label><textarea id="strategyConfigJson" name="strategyConfigJson" rows="3">{}</textarea></div>
          <div class="full"><button type="submit">Apply Settings</button></div>
        </form>
        <div class="status" id="formStatus"></div>
      </section>
    </div>

    <section class="panel section config-summary-panel">
      <div class="section-head">
        <h2>Configs</h2>
        <div class="config-profit-totals">
          <div class="config-profit-total"><span>All Daily</span><strong id="allConfigsDailyProfit">-</strong></div>
          <div class="config-profit-total"><span>All Total</span><strong id="allConfigsTotalProfit">-</strong></div>
          <button type="button" class="danger-button compact-button" id="resetSpotStats">Reset Spot Stats</button>
        </div>
      </div>
      <div class="table-wrap">
        <table class="config-table">
          <thead>
            <tr>
              <th>Symbol</th><th>Strategy</th><th>Type</th><th>Status</th><th>Score</th><th>Capital</th><th>Daily Profit</th><th>Total Profit ↓</th><th>Updated</th>
            </tr>
          </thead>
          <tbody id="configSummaryRows"></tbody>
        </table>
      </div>
    </section>

    <section class="stats" id="stats"></section>

    <section class="panel section regime-card" id="noTradeReasonCard" hidden>
      <div>
        <div class="label">No-Trade Reason</div>
        <div class="regime-title no-trade-title" id="noTradeReasonCode">-</div>
        <div class="subtle" id="noTradeReasonText">-</div>
        <div class="regime-meta" id="noTradeReasonMeta"></div>
      </div>
      <div class="badge">Recorded <strong id="noTradeReasonTime">-</strong></div>
    </section>

    <section class="panel section" id="noTradeReasonHistoryCard" style="margin-bottom:20px;" hidden>
      <div class="section-head">
        <h2>No-Trade History</h2>
      </div>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Code</th><th>Strategy</th><th>Reason</th><th>Age</th><th>Time</th>
            </tr>
          </thead>
          <tbody id="noTradeReasonRows"></tbody>
        </table>
      </div>
    </section>

    <section class="panel section" style="margin-bottom:20px;">
      <div class="section-head">
        <h2>Strategy Performance</h2>
      </div>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Strategy</th><th>Net PnL</th><th>Gross PnL</th><th>Fees</th><th>Fills</th><th>Closed</th><th>Win Rate</th><th>Active</th>
            </tr>
          </thead>
          <tbody id="strategyPerformanceRows"></tbody>
        </table>
      </div>
    </section>

    <section class="panel section" style="margin-bottom:20px;">
      <div class="section-head">
        <h2>Daily Strategy Performance</h2>
      </div>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Date</th><th>Strategy</th><th>Net PnL</th><th>Fees</th><th>Fills</th><th>Closed</th><th>Win Rate</th>
            </tr>
          </thead>
          <tbody id="dailyStrategyPerformanceRows"></tbody>
        </table>
      </div>
    </section>

    <section class="panel section regime-card">
      <div>
        <div class="label">Market Regime</div>
        <div class="regime-title" id="marketRegimeTitle">-</div>
        <div class="subtle" id="marketRegimeRecommendation">-</div>
        <div class="regime-meta" id="marketRegimeMeta"></div>
      </div>
      <div class="badge">Confidence <strong id="marketRegimeConfidence">-</strong></div>
    </section>

    <section class="panel section regime-card">
      <div>
        <div class="label">Signal Analyzer</div>
        <div class="regime-title" id="signalTitle">-</div>
        <div class="subtle" id="signalReason">-</div>
        <div class="regime-meta" id="signalMeta"></div>
      </div>
      <div class="badge">Confidence <strong id="signalConfidence">-</strong></div>
    </section>

    <section class="panel section regime-card">
      <div>
        <div class="label">BTD Diagnostics</div>
        <div class="regime-title" id="btdDiagnosticsTitle">-</div>
        <div class="subtle" id="btdDiagnosticsReason">-</div>
        <div class="regime-meta" id="btdDiagnosticsMeta"></div>
      </div>
      <div class="badge">Allowed <strong id="btdDiagnosticsAllowed">-</strong></div>
    </section>

    <div class="layout">
      <section class="panel section">
        <h2>Active Grid Levels</h2>
        <div class="grid-list" id="gridLevels"></div>
      </section>
      <section class="panel section">
        <div class="section-head">
          <h2>Active Orders</h2>
          <button type="button" class="danger-button compact-button" id="cancelActiveOrders">Cancel Active</button>
        </div>
        <div class="table-wrap">
          <table>
            <thead>
              <tr><th>Source</th><th>Group</th><th>Side</th><th>Price</th><th>Qty</th><th>Filled</th><th>USDT</th><th>Status</th><th>Link</th></tr>
            </thead>
            <tbody id="activeOrders"></tbody>
          </table>
        </div>
      </section>
    </div>

    <section class="panel section" style="margin-top:20px;">
      <div class="section-head">
        <h2>Order History</h2>
        <div class="history-copy-controls">
          <input class="hours-input" id="copyHistoryHours" type="number" min="0.1" step="0.5" value="1" aria-label="History hours" />
          <span class="preset-hint">hours</span>
          <button type="button" class="secondary-button compact-button" id="copyLastHistory">Copy Last</button>
          <button type="button" class="secondary-button compact-button" id="copyDiagnostics">Copy Diagnostics</button>
        </div>
      </div>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Time</th><th>Source</th><th>Group</th><th>Side</th><th>Price</th><th>Qty</th><th>Filled</th><th>Status</th><th>Trade PnL</th><th>Cash Flow</th><th>Fee</th><th>Order</th>
            </tr>
          </thead>
          <tbody id="historyRows"></tbody>
        </table>
      </div>
    </section>
  </div>

  <script>
    const byId = (id) => document.getElementById(id);
    const settingsFieldIds = ['symbol', 'category', 'strategyMode', 'strategyType', 'lowerPrice', 'upperPrice', 'step', 'orderSizeUsdt', 'stopLowerPrice', 'stopUpperPrice', 'strategyConfigJson'];
    const presetLabelToFieldId = {
      'symbol': 'symbol',
      'category': 'category',
      'strategy mode': 'strategyMode',
      'strategy type': 'strategyType',
      'strategy config json': 'strategyConfigJson',
      'grid lower': 'lowerPrice',
      'grid upper': 'upperPrice',
      'grid step': 'step',
      'order size usdt': 'orderSizeUsdt',
      'stop lower': 'stopLowerPrice',
      'stop upper': 'stopUpperPrice'
    };
    const defaultNewSettings = {
      symbol: '',
      category: 'spot',
      strategyMode: 'manual',
      strategyType: 'grid',
      strategyConfigJson: '{}',
      lowerPrice: '',
      upperPrice: '',
      step: '',
      orderSizeUsdt: '',
      stopLowerPrice: '',
      stopUpperPrice: ''
    };
    let selectedSymbol = new URLSearchParams(window.location.search).get('symbol')?.toUpperCase() || null;
    let isCreatingNewProfile = false;
    let profileCache = [];
    let latestDashboardData = null;
    let latestFullDashboardData = null;
    let settingsFormDirty = false;
    let dashboardRequestSeq = 0;

    const isSettingsFormDirty = () => settingsFormDirty;
    const setSettingsFormDirty = (isDirty) => {
      settingsFormDirty = isDirty;
    };
    const updateSettingsForm = (settings) => {
      byId('symbol').value = settings.symbol;
      byId('category').value = settings.category;
      byId('strategyMode').value = settings.strategyMode || 'manual';
      byId('strategyType').value = settings.strategyType || 'grid';
      byId('lowerPrice').value = settings.lowerPrice;
      byId('upperPrice').value = settings.upperPrice;
      byId('step').value = settings.step;
      byId('orderSizeUsdt').value = settings.orderSizeUsdt;
      byId('stopLowerPrice').value = settings.stopLowerPrice;
      byId('stopUpperPrice').value = settings.stopUpperPrice;
      byId('strategyConfigJson').value = settings.strategyConfigJson || '{}';
    };
    const escapeHtml = (value) => String(value)
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#039;');
    const updateSelectedSymbolUrl = () => {
      const url = new URL(window.location.href);
      if (selectedSymbol) {
        url.searchParams.set('symbol', selectedSymbol);
      } else {
        url.searchParams.delete('symbol');
      }
      window.history.replaceState({}, '', url);
    };
    const waitForPaint = () => new Promise((resolve) => requestAnimationFrame(() => resolve()));
    const setDashboardLoading = (isLoading, text = 'Loading dashboard...') => {
      byId('dashboardLoading').classList.toggle('active', isLoading);
      byId('dashboardLoadingText').textContent = text;
    };
    const updateSelectedConfigSummaries = (summaries, symbol) => (summaries || []).map(item => ({
      ...item,
      isSelected: String(item.symbol || '').toUpperCase() === String(symbol || '').toUpperCase()
    }));
    const loadSelectedProfile = async (symbol) => {
      selectedSymbol = symbol.toUpperCase();
      isCreatingNewProfile = false;
      setSettingsFormDirty(false);
      updateSelectedSymbolUrl();
      await loadDashboard({ forceSettingsRefresh: true, fast: true, loadingText: `Switching to ${selectedSymbol}...` });
      loadDashboard({ forceSettingsRefresh: true, background: true, loadingText: `Loading ${selectedSymbol} analytics...` }).catch((error) => {
        byId('formStatus').className = 'status error';
        byId('formStatus').textContent = error.message;
      });
    };
    const renderProfileTabs = (profiles) => {
      profileCache = profiles;
      byId('profileTabs').innerHTML = [
        ...profiles.map(profile => `
          <button type="button" class="profile-tab ${profile.isSelected && !isCreatingNewProfile ? 'active' : ''}" data-action="select-profile" data-symbol="${escapeHtml(profile.symbol)}">
            ${escapeHtml(profile.symbol)}
            <span class="close-tab" data-action="delete-profile" data-symbol="${escapeHtml(profile.symbol)}">x</span>
          </button>`),
        `<button type="button" class="profile-tab new ${isCreatingNewProfile ? 'active' : ''}" data-action="new-profile">+ New Config</button>`
      ].join('');
    };
    const formatStatus = (status) => status === 'paused' ? 'Paused' : 'In progress';
    const formatEnumLabel = (value) => String(value || 'Unknown')
      .replaceAll('_', ' ')
      .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
      .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
      .trim();
    const renderConfigTotals = (configs) => {
      const dailyTotal = configs.reduce((sum, config) => sum + Number(config.dailyRealizedPnl || 0), 0);
      const total = configs.reduce((sum, config) => sum + Number(config.totalRealizedPnl || 0), 0);
      byId('allConfigsDailyProfit').innerHTML = formatPnl(dailyTotal);
      byId('allConfigsTotalProfit').innerHTML = formatPnl(total);
    };
    const renderConfigSummaries = (configs) => {
      renderConfigTotals(configs);
      byId('configSummaryRows').innerHTML = configs.length === 0
        ? `<tr><td colspan="9">No configs yet.</td></tr>`
        : configs.map(config => `
            <tr class="${config.isSelected && !isCreatingNewProfile ? 'selected' : ''}" data-symbol="${escapeHtml(config.symbol)}">
              <td>
                <div class="config-symbol">
                  <strong>${escapeHtml(config.symbol)}</strong>
                  <span>${escapeHtml(config.category)}</span>
                </div>
              </td>
              <td>${escapeHtml(config.strategyName)}</td>
              <td>${escapeHtml(config.strategyMode)}</td>
              <td><span class="status-pill ${config.status === 'paused' ? 'paused' : ''}">${formatStatus(config.status)}</span></td>
              <td title="${escapeHtml((config.pairScoreReasons || []).join('; '))}">
                <strong>${formatNumber(config.pairScore)}</strong>
                <span class="subtle">${escapeHtml(config.pairScoreLabel || 'Unknown')}</span>
              </td>
              <td>${formatNumber(config.suggestedOrderSizeMultiplier || 1)}x</td>
              <td>${formatPnl(config.dailyRealizedPnl)}</td>
              <td>${formatPnl(config.totalRealizedPnl)}</td>
              <td>${formatDate(config.updatedAt)}</td>
            </tr>`).join('');
    };
    const parseSettingsPreset = (text) => {
      const parsed = {};
      const errors = [];

      text.split(/\r?\n/).forEach((line, index) => {
        const trimmed = line.trim();
        if (!trimmed) {
          return;
        }

        const separatorIndex = trimmed.indexOf(':');
        if (separatorIndex < 0) {
          errors.push(`Line ${index + 1}: missing ":" separator.`);
          return;
        }

        const label = trimmed.slice(0, separatorIndex).trim().toLowerCase();
        const value = trimmed.slice(separatorIndex + 1).trim();
        const fieldId = presetLabelToFieldId[label];
        if (!fieldId) {
          errors.push(`Line ${index + 1}: unknown setting "${trimmed.slice(0, separatorIndex).trim()}".`);
          return;
        }

        if (!value) {
          errors.push(`Line ${index + 1}: value is empty.`);
          return;
        }

        parsed[fieldId] = value;
      });

      return { parsed, errors };
    };
    const applySettingsPreset = () => {
      const status = byId('formStatus');
      const { parsed, errors } = parseSettingsPreset(byId('settingsPreset').value);
      const missingLabels = Object.entries(presetLabelToFieldId)
        .filter(([, fieldId]) => !Object.prototype.hasOwnProperty.call(parsed, fieldId))
        .map(([label]) => label);

      if (errors.length > 0 || missingLabels.length > 0) {
        status.className = 'status error';
        status.textContent = [
          ...errors,
          ...(missingLabels.length > 0 ? [`Missing settings: ${missingLabels.join(', ')}.`] : [])
        ].join(' ');
        return;
      }

      Object.values(presetLabelToFieldId).forEach((fieldId) => {
        byId(fieldId).value = parsed[fieldId];
      });
      setSettingsFormDirty(true);
      status.className = 'status ok';
      status.textContent = 'Preset applied to the form. Press Apply Settings to save.';
    };
    const formatNumber = (value) => value === null || value === undefined ? "—" : Number(value).toLocaleString(undefined, { maximumFractionDigits: 8 });
    const formatMinutesAgo = (minutes) => {
      const value = Math.max(0, Number(minutes ?? 0));
      if (value < 1) return 'just now';
      if (value < 60) return `${Math.floor(value)}m ago`;
      if (value < 1440) return `${Math.floor(value / 60)}h ${Math.floor(value % 60)}m ago`;
      return `${Math.floor(value / 1440)}d ${Math.floor((value % 1440) / 60)}h ago`;
    };
    const formatSigned = (value) => {
      const number = Number(value ?? 0);
      const cls = number > 0 ? "positive" : number < 0 ? "negative" : "";
      return `<span class="value ${cls}">${number.toLocaleString(undefined, { maximumFractionDigits: 8 })}</span>`;
    };
    const formatPnl = (value) => {
      const number = Number(value ?? 0);
      const cls = number > 0 ? "positive" : number < 0 ? "negative" : "";
      return `<span class="pnl ${cls}">${number.toLocaleString(undefined, { maximumFractionDigits: 8 })}</span>`;
    };
    const formatDate = (value) => value ? new Date(value).toLocaleString() : "—";
    const csvEscape = (value) => {
      const text = String(value ?? '');
      return /[",\n]/.test(text) ? `"${text.replaceAll('"', '""')}"` : text;
    };
    const formatDuration = (value) => {
      const totalSeconds = Math.max(0, Math.floor((typeof value === 'string' ? parseTimeSpanSeconds(value) : Number(value ?? 0))));
      const days = Math.floor(totalSeconds / 86400);
      const hours = Math.floor((totalSeconds % 86400) / 3600);
      const minutes = Math.floor((totalSeconds % 3600) / 60);
      const seconds = totalSeconds % 60;
      if (days > 0) {
        return `${days}d ${hours}h ${minutes}m`;
      }
      if (hours > 0) {
        return `${hours}h ${minutes}m`;
      }
      return `${minutes}m ${seconds}s`;
    };
    const parseTimeSpanSeconds = (value) => {
      const match = /^(-?\d+)\.(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?$|^(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(value);
      if (!match) {
        return 0;
      }
      if (match[1] !== undefined) {
        return (Number(match[1]) * 86400) + (Number(match[2]) * 3600) + (Number(match[3]) * 60) + Number(match[4]);
      }
      return (Number(match[5]) * 3600) + (Number(match[6]) * 60) + Number(match[7]);
    };
    const getCopyHistoryHours = () => {
      const hours = Number(byId('copyHistoryHours').value);
      return Number.isFinite(hours) && hours > 0 ? hours : 1;
    };
    const readSettingsFormSnapshot = () => ({
      symbol: byId('symbol').value,
      category: byId('category').value,
      strategyMode: byId('strategyMode').value,
      strategyType: byId('strategyType').value,
      strategyConfigJson: byId('strategyConfigJson').value,
      lowerPrice: Number(byId('lowerPrice').value),
      upperPrice: Number(byId('upperPrice').value),
      step: Number(byId('step').value),
      orderSizeUsdt: Number(byId('orderSizeUsdt').value),
      stopLowerPrice: Number(byId('stopLowerPrice').value),
      stopUpperPrice: Number(byId('stopUpperPrice').value),
      isDirty: isSettingsFormDirty()
    });
    const buildSettingsPayload = () => {
      const snapshot = readSettingsFormSnapshot();
      delete snapshot.isDirty;
      return snapshot;
    };
    const parseStrategyConfigSnapshot = (strategyConfigJson) => {
      try {
        return {
          ok: true,
          value: JSON.parse(strategyConfigJson || '{}')
        };
      } catch (error) {
        return {
          ok: false,
          error: error.message,
          raw: strategyConfigJson || ''
        };
      }
    };
    const expectedPerformanceStrategies = ['Grid', 'BTD', 'DCA', 'Signal', 'ReduceOnly', 'Hybrid'];
    const inferSourceFromParent = (parentOrderLinkId, fallback) => {
      switch (parentOrderLinkId) {
        case 'dca-entry': return 'DCA';
        case 'btd-entry': return 'BTD';
        case 'signal-entry':
        case 'signal-exit': return 'Signal';
        case 'reduce-only-exit': return 'ReduceOnly';
        default: return fallback || 'Grid';
      }
    };
    const buildPerformanceSanitySnapshot = (data) => {
      const orders = data.orders || [];
      const sourceCounts = orders.reduce((result, order) => {
        const source = order.source || 'Unknown';
        result[source] = (result[source] || 0) + 1;
        return result;
      }, {});
      const suspiciousOrders = orders
        .filter(order => {
          const inferred = inferSourceFromParent(order.parentOrderLinkId, order.source);
          return !order.source || inferred !== order.source;
        })
        .map(order => ({
          orderLinkId: order.orderLinkId,
          parentOrderLinkId: order.parentOrderLinkId,
          source: order.source,
          inferredSource: inferSourceFromParent(order.parentOrderLinkId, order.source),
          side: order.side,
          status: order.status,
          filledQuantity: order.filledQuantity,
          realizedPnl: order.realizedPnl
        }));
      const presentStrategies = new Set((data.performanceByStrategy || []).map(item => item.strategy));
      return {
        expectedStrategies: expectedPerformanceStrategies,
        sourceCounts,
        missingPerformanceRows: expectedPerformanceStrategies.filter(strategy => !presentStrategies.has(strategy)),
        suspiciousOrders,
        hasSuspiciousOrders: suspiciousOrders.length > 0
      };
    };
    const buildDailyPerformanceCsv = (rows) => [
      ['Date', 'Strategy', 'Net PnL', 'Fees', 'Fills', 'Closed', 'Win Rate'],
      ...(rows || []).map(item => [
        item.performanceDate,
        item.strategy,
        item.netPnl,
        item.feesPaid,
        item.filledTradesCount,
        item.closedTradesCount,
        item.winRate
      ])
    ].map(row => row.map(csvEscape).join(',')).join('\n');
    const buildDiagnosticsSnapshot = (hours) => {
      if (!latestDashboardData) {
        return '';
      }

      const cutoff = Date.now() - hours * 60 * 60 * 1000;
      const recentOrders = latestDashboardData.orders.filter(order => {
        const timestamp = new Date(order.filledAt || order.updatedAt || order.createdAt).getTime();
        return Number.isFinite(timestamp) && timestamp >= cutoff;
      });
      const formSettings = readSettingsFormSnapshot();
      return JSON.stringify({
        schema: 'bybit-grid-bot-diagnostics/v1',
        copiedAt: new Date().toISOString(),
        page: {
          url: window.location.href,
          selectedSymbol,
          isCreatingNewProfile
        },
        analysisTargets: [
          'runtime-settings',
          'strategy-config-json',
          'signal-bot',
          'auto-recommendation',
          'risk-limits',
          'active-orders',
          'recent-order-history',
          'performance-source-sanity',
          'pair-scoring'
        ],
        formSettings,
        parsedFormStrategyConfig: parseStrategyConfigSnapshot(formSettings.strategyConfigJson),
        savedSettings: latestDashboardData.settings,
        parsedSavedStrategyConfig: parseStrategyConfigSnapshot(latestDashboardData.settings?.strategyConfigJson),
        runtime: latestDashboardData.runtime,
        state: latestDashboardData.state,
        configSummaries: latestDashboardData.configSummaries,
        pairScores: latestDashboardData.pairScores,
        marketRegime: latestDashboardData.marketRegime,
        signalAnalysis: latestDashboardData.signalAnalysis,
        btdDiagnostics: latestDashboardData.btdDiagnostics,
        autoRecommendation: latestDashboardData.autoRecommendation,
        gridLevels: latestDashboardData.gridLevels,
        activeOrders: latestDashboardData.activeOrders,
        lastNoTradeReason: latestDashboardData.lastNoTradeReason,
        noTradeReasonHistory: latestDashboardData.noTradeReasonHistory,
        performanceByStrategy: latestDashboardData.performanceByStrategy,
        dailyPerformanceByStrategy: latestDashboardData.dailyPerformanceByStrategy,
        performanceSanity: buildPerformanceSanitySnapshot(latestDashboardData),
        dailyPerformanceCsv: buildDailyPerformanceCsv(latestDashboardData.dailyPerformanceByStrategy),
        recentOrdersWindowHours: hours,
        recentOrders,
        generatedAt: latestDashboardData.generatedAt
      }, null, 2);
    };
    const buildLastHistoryCsv = (hours) => {
      if (!latestDashboardData) {
        return '';
      }

      const cutoff = Date.now() - hours * 60 * 60 * 1000;
      const rows = latestDashboardData.orders
        .filter(order => {
          const timestamp = new Date(order.filledAt || order.updatedAt || order.createdAt).getTime();
          return Number.isFinite(timestamp) && timestamp >= cutoff;
        })
        .map(order => [
          formatDate(order.filledAt || order.updatedAt || order.createdAt),
          order.symbol,
          order.source,
          order.side,
          order.price,
          order.quantity,
          order.filledQuantity,
          order.status,
          order.tradePnl,
          order.netCashFlow,
          order.feePaid,
          order.orderLinkId
        ]);

      return [
        ['Time', 'Symbol', 'Source', 'Side', 'Price', 'Qty', 'Filled', 'Status', 'Trade PnL', 'Cash Flow', 'Fee', 'Order'],
        ...rows
      ].map(row => row.map(csvEscape).join(',')).join('\n');
    };
    const writeClipboard = async (text) => {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return;
      }

      const textarea = document.createElement('textarea');
      textarea.value = text;
      textarea.setAttribute('readonly', '');
      textarea.style.position = 'fixed';
      textarea.style.left = '-9999px';
      textarea.style.top = '0';
      document.body.appendChild(textarea);
      textarea.focus();
      textarea.select();

      try {
        if (!document.execCommand('copy')) {
          throw new Error('Browser refused clipboard copy.');
        }
      } finally {
        document.body.removeChild(textarea);
      }
    };
    const copyLastHistory = async () => {
      const status = byId('formStatus');
      const hours = getCopyHistoryHours();
      const csv = buildLastHistoryCsv(hours);
      if (!csv) {
        status.className = 'status error';
        status.textContent = 'No dashboard data loaded yet.';
        return;
      }

      await writeClipboard(csv);
      const copiedRows = Math.max(0, csv.split('\n').length - 1);
      status.className = 'status ok';
      status.textContent = `Copied ${copiedRows} history rows from the last ${hours} hour(s).`;
    };
    const copyDiagnostics = async () => {
      const status = byId('formStatus');
      const hours = getCopyHistoryHours();
      const snapshot = buildDiagnosticsSnapshot(hours);
      if (!snapshot) {
        status.className = 'status error';
        status.textContent = 'No dashboard data loaded yet.';
        return;
      }

      await writeClipboard(snapshot);
      const copiedOrders = JSON.parse(snapshot).recentOrders.length;
      status.className = 'status ok';
      status.textContent = `Copied diagnostics snapshot for ${latestDashboardData.settings?.symbol || 'current profile'} with ${copiedOrders} order(s) from the last ${hours} hour(s).`;
    };
    const cancelActiveOrders = async () => {
      const status = byId('formStatus');
      const symbol = selectedSymbol || latestDashboardData?.settings?.symbol;
      const cancelUrl = symbol ? `/api/orders/cancel-active?symbol=${encodeURIComponent(symbol)}` : '/api/orders/cancel-active';
      const response = await fetch(cancelUrl, { method: 'POST' });
      const result = await response.json();
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to cancel active orders.');
      if (response.ok) {
        await loadDashboard({ forceSettingsRefresh: true });
      }
    };
    const resetSpotStats = async () => {
      const status = byId('formStatus');
      if (!confirm('Reset all spot statistics, orders, no-trade reasons, and paper balances? Runtime configs will stay.')) {
        return;
      }

      status.className = 'status';
      status.textContent = 'Resetting spot statistics...';
      const response = await fetch('/api/spot/statistics/reset', { method: 'POST' });
      const result = await response.json();
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to reset spot statistics.');
      if (response.ok) {
        await loadDashboard({ forceSettingsRefresh: true });
      }
    };
    const applySelectedStrategyRecommendation = async (message) => {
      const status = byId('formStatus');
      status.className = 'status';
      status.textContent = message || 'Applying recommendation to the selected strategy...';
      const response = await fetch('/api/settings/apply-selected-recommendation', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(buildSettingsPayload())
      });
      const result = await response.json();
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to apply selected strategy recommendation.');
      if (response.ok) {
        selectedSymbol = (result.symbol || byId('symbol').value).toUpperCase();
        isCreatingNewProfile = false;
        updateSelectedSymbolUrl();
        setSettingsFormDirty(false);
        await loadDashboard({ forceSettingsRefresh: true });
      }
    };

    async function loadDashboard(options = {}) {
      const forceSettingsRefresh = Boolean(options.forceSettingsRefresh);
      const fast = Boolean(options.fast);
      const background = Boolean(options.background);
      const requestId = ++dashboardRequestSeq;
      const params = new URLSearchParams();
      if (selectedSymbol && !isCreatingNewProfile) {
        params.set('symbol', selectedSymbol);
      }
      if (fast) {
        params.set('fast', 'true');
      }
      const dashboardUrl = params.toString() ? `/api/dashboard?${params}` : '/api/dashboard';
      if (!background) {
        setDashboardLoading(true, options.loadingText || (fast ? 'Switching config...' : 'Loading dashboard...'));
        await waitForPaint();
      } else {
        setDashboardLoading(true, options.loadingText || 'Loading analytics...');
      }

      let data;
      try {
        const response = await fetch(dashboardUrl, { cache: 'no-store' });
        data = await response.json();
        if (!response.ok) {
          throw new Error(data?.message || data?.errors?.join(' | ') || 'Failed to load dashboard.');
        }
      } catch (error) {
        if (background && requestId === dashboardRequestSeq) {
          setDashboardLoading(false);
        }
        throw error;
      } finally {
        if (!background && requestId === dashboardRequestSeq) {
          setDashboardLoading(false);
        }
      }

      if (requestId !== dashboardRequestSeq) {
        return;
      }

      latestDashboardData = data;
      if (!data.isPartial) {
        latestFullDashboardData = data;
        setDashboardLoading(false);
      }
      if (!isCreatingNewProfile) {
        selectedSymbol = data.settings.symbol;
        updateSelectedSymbolUrl();
      }
      renderProfileTabs(data.profiles);
      if (data.isPartial && latestFullDashboardData?.configSummaries?.length) {
        renderConfigSummaries(updateSelectedConfigSummaries(latestFullDashboardData.configSummaries, data.settings.symbol));
      } else {
        renderConfigSummaries(data.configSummaries || []);
      }

      byId('modePill').textContent = `${data.state.tradingMode} mode`;
      byId('modePill').className = data.state.isPaused ? 'pill paused' : 'pill';
      byId('heroSymbol').textContent = data.settings.symbol;
      byId('heroPrice').textContent = formatNumber(data.state.currentPrice);
      byId('heroActiveTime').textContent = formatDuration(data.runtime.activeTime);
      byId('heroUpdated').textContent = formatDate(data.generatedAt);
      byId('pauseBox').hidden = !data.state.isPaused;
      byId('pauseReason').textContent = data.state.pauseReason || 'Trading is paused.';
      byId('marketRegimeTitle').textContent = data.marketRegime.regime;
      byId('marketRegimeRecommendation').textContent = data.marketRegime.recommendation;
      byId('marketRegimeConfidence').textContent = `${formatNumber(Number(data.marketRegime.confidence || 0) * 100)}%`;
      byId('marketRegimeMeta').innerHTML = [
        ['ADX', formatNumber(data.marketRegime.adx)],
        ['Move', `${formatNumber(data.marketRegime.movePercent)}%`],
        ['Range', `${formatNumber(data.marketRegime.rangePercent)}%`],
        ['Volume x', formatNumber(data.marketRegime.volumeRatio)],
        ['Support', formatNumber(data.marketRegime.support)],
        ['Resistance', formatNumber(data.marketRegime.resistance)]
      ].map(([label, value]) => `<span class="regime-chip">${label}: ${value}</span>`).join('');
      byId('signalTitle').textContent = data.signalAnalysis.signal;
      byId('signalReason').textContent = data.signalAnalysis.reason;
      byId('signalConfidence').textContent = `${formatNumber(Number(data.signalAnalysis.confidence || 0) * 100)}%`;
      byId('signalMeta').innerHTML = [
        ['EMA fast', formatNumber(data.signalAnalysis.emaFast)],
        ['EMA slow', formatNumber(data.signalAnalysis.emaSlow)],
        ['RSI', formatNumber(data.signalAnalysis.rsi)],
        ['Bollinger', formatNumber(data.signalAnalysis.bollingerPosition)],
        ['Volume x', formatNumber(data.signalAnalysis.volumeRatio)],
        ['Trend', `${formatNumber(data.signalAnalysis.trendStrength)}%`]
      ].map(([label, value]) => `<span class="regime-chip">${label}: ${value}</span>`).join('');
      byId('btdDiagnosticsTitle').textContent = data.btdDiagnostics.phase;
      byId('btdDiagnosticsReason').textContent = data.btdDiagnostics.reason;
      byId('btdDiagnosticsAllowed').textContent = data.btdDiagnostics.isAllowed ? 'yes' : 'no';
      byId('btdDiagnosticsMeta').innerHTML = [
        ['EMA fast', formatNumber(data.btdDiagnostics.emaFast)],
        ['EMA slow', formatNumber(data.btdDiagnostics.emaSlow)],
        ['BTC risk-off', data.btdDiagnostics.btcRiskOff ? 'yes' : 'no'],
        ['Pullback', `${formatNumber(data.btdDiagnostics.pullbackPercent)}%`],
        ['Distance to EMA', `${formatNumber(data.btdDiagnostics.distanceToEmaPercent)}%`],
        ['Dip trigger', data.btdDiagnostics.dipTriggered ? 'yes' : 'no']
      ].map(([label, value]) => `<span class="regime-chip">${label}: ${value}</span>`).join('');
      byId('autoStrategyTitle').textContent = data.autoRecommendation.strategyType;
      byId('autoStrategyReason').textContent = data.autoRecommendation.reason;
      byId('autoStrategyMeta').innerHTML = [
        ['Range', `${formatNumber(data.autoRecommendation.lowerPrice)} - ${formatNumber(data.autoRecommendation.upperPrice)}`],
        ['Step', formatNumber(data.autoRecommendation.step)],
        ['Order', `${formatNumber(data.autoRecommendation.orderSizeUsdt)} USDT`],
        ['Stop', `${formatNumber(data.autoRecommendation.stopLowerPrice)} - ${formatNumber(data.autoRecommendation.stopUpperPrice)}`],
        ['Window', `${data.autoRecommendation.analysisLookbackCandles || 0} x ${data.autoRecommendation.analysisCandleInterval || '1'}m`],
        ['ATR', `${formatNumber(data.autoRecommendation.atrPercent)}%`],
        ['Drawdown', `${formatNumber(data.autoRecommendation.drawdownPercent)}%`]
      ].map(([label, value]) => `<span class="regime-chip">${label}: ${value}</span>`).join('');

      if (forceSettingsRefresh || !isSettingsFormDirty()) {
        updateSettingsForm(data.settings);
        setSettingsFormDirty(false);
      }

      const aggressiveModeText = data.state.aggressiveModeEnabled
        ? 'on'
        : data.state.aggressiveModeDisabledUntil
          ? `off until ${formatDate(data.state.aggressiveModeDisabledUntil)}`
          : 'off';
      const aggressiveCooldownText = data.state.aggressiveModeDisabledUntil
        ? formatDate(data.state.aggressiveModeDisabledUntil)
        : '—';
      const aggressiveLastLossText = data.state.aggressiveModeLastLossAt
        ? formatDate(data.state.aggressiveModeLastLossAt)
        : '—';
      byId('stats').innerHTML = [
        ['Current Price', formatNumber(data.state.currentPrice)],
        ['Total Realized PnL', formatSigned(data.state.totalRealizedPnl)],
        ['Daily Realized PnL', formatSigned(data.state.dailyRealizedPnl)],
        ['Unrealized PnL', formatSigned(data.state.unrealizedPnl)],
        ['Estimated Equity', formatNumber(data.state.estimatedTotalEquity)],
        ['Base Asset Qty', formatNumber(data.state.baseAssetQuantity)],
        ['Quote Balance', formatNumber(data.state.quoteAssetBalance)],
        ['Average Entry', formatNumber(data.state.averageEntryPrice)],
        ['Profit %', `${formatNumber(data.state.profitProtectionCurrentProfitPercent)}%`],
        ['Peak Profit %', `${formatNumber(data.state.profitProtectionPeakProfitPercent)}%`],
        ['Trailing Stop', formatNumber(data.state.profitProtectionTrailingStopPrice)],
        ['Aggressive Mode', aggressiveModeText],
        ['Aggressive Cooldown', aggressiveCooldownText],
        ['Aggressive Last Loss', aggressiveLastLossText],
        ['Aggressive Reason', data.state.aggressiveModeDisabledReason || '—']
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');

      const noTradeReason = data.lastNoTradeReason;
      byId('noTradeReasonCard').hidden = !noTradeReason;
      if (noTradeReason) {
        byId('noTradeReasonCode').textContent = formatEnumLabel(noTradeReason.code);
        byId('noTradeReasonText').textContent = noTradeReason.reason;
        byId('noTradeReasonTime').textContent = formatMinutesAgo(noTradeReason.minutesAgo);
        byId('noTradeReasonMeta').innerHTML = [
          ['Strategy', noTradeReason.strategyType || data.settings.strategyType],
          ['Symbol', data.settings.symbol]
        ].map(([label, value]) => `<span class="regime-chip">${label}: ${escapeHtml(value)}</span>`).join('');
      }
      const noTradeHistory = data.noTradeReasonHistory || [];
      byId('noTradeReasonHistoryCard').hidden = noTradeHistory.length === 0;
      byId('noTradeReasonRows').innerHTML = noTradeHistory.length === 0
        ? `<tr><td colspan="5">No no-trade reasons yet.</td></tr>`
        : noTradeHistory.map(item => `
            <tr>
              <td>${escapeHtml(formatEnumLabel(item.code))}</td>
              <td>${escapeHtml(item.strategyType || data.settings.strategyType)}</td>
              <td>${escapeHtml(item.reason)}</td>
              <td>${formatMinutesAgo(item.minutesAgo)}</td>
              <td>${formatDate(item.createdAt)}</td>
            </tr>`).join('');

      byId('strategyPerformanceRows').innerHTML = (data.performanceByStrategy || []).length === 0
        ? `<tr><td colspan="8">No strategy performance yet.</td></tr>`
        : data.performanceByStrategy.map(item => `
            <tr>
              <td>${escapeHtml(item.strategy)}</td>
              <td>${formatPnl(item.netPnl)}</td>
              <td>${formatPnl(item.grossPnl)}</td>
              <td>${formatNumber(item.feesPaid)}</td>
              <td>${formatNumber(item.filledTradesCount)}</td>
              <td>${formatNumber(item.closedTradesCount)}</td>
              <td>${formatNumber(item.winRate)}%</td>
              <td>${formatNumber(item.activeOrdersCount)}</td>
            </tr>`).join('');

      byId('dailyStrategyPerformanceRows').innerHTML = (data.dailyPerformanceByStrategy || []).length === 0
        ? `<tr><td colspan="7">No daily strategy performance yet.</td></tr>`
        : data.dailyPerformanceByStrategy.map(item => `
            <tr>
              <td>${escapeHtml(item.performanceDate)}</td>
              <td>${escapeHtml(item.strategy)}</td>
              <td>${formatPnl(item.netPnl)}</td>
              <td>${formatNumber(item.feesPaid)}</td>
              <td>${formatNumber(item.filledTradesCount)}</td>
              <td>${formatNumber(item.closedTradesCount)}</td>
              <td>${formatNumber(item.winRate)}%</td>
            </tr>`).join('');

      byId('gridLevels').innerHTML = data.gridLevels.map(level => `<div class="grid-chip">${formatNumber(level)}</div>`).join('');
      byId('activeOrders').innerHTML = data.activeOrders.length === 0
        ? `<tr><td colspan="9">No active orders.</td></tr>`
        : data.activeOrders.map(order => `
            <tr>
              <td>${order.source}</td>
              <td>${order.orderGroup ? `<span class="token" title="${escapeHtml(order.orderGroup)}">${escapeHtml(order.ladderRole || 'Grouped')}</span>` : '-'}</td>
              <td>${order.side}</td>
              <td>${formatNumber(order.price)}</td>
              <td>${formatNumber(order.quantity)}</td>
              <td>${formatNumber(order.filledQuantity)}</td>
              <td>${formatNumber(order.remainingNotionalUsdt)}</td>
              <td>${order.status}</td>
              <td class="token">${order.orderLinkId}</td>
            </tr>`).join('');

      byId('historyRows').innerHTML = data.orders.length === 0
        ? `<tr><td colspan="12">No orders yet.</td></tr>`
        : data.orders.map(order => `
            <tr>
              <td>${formatDate(order.filledAt || order.updatedAt || order.createdAt)}</td>
              <td>${order.source}</td>
              <td>${order.orderGroup ? `<span class="token" title="${escapeHtml(order.orderGroup)}">${escapeHtml(order.ladderRole || 'Grouped')}</span>` : '-'}</td>
              <td>${order.side}</td>
              <td>${formatNumber(order.price)}</td>
              <td>${formatNumber(order.quantity)}</td>
              <td>${formatNumber(order.filledQuantity)}</td>
              <td>${order.status}</td>
              <td>${formatNumber(order.tradePnl)}</td>
              <td>${formatNumber(order.netCashFlow)}</td>
              <td>${formatNumber(order.feePaid)}</td>
              <td class="token">${order.orderLinkId}</td>
            </tr>`).join('');
    }

    settingsFieldIds.forEach((id) => {
      byId(id).addEventListener('input', () => setSettingsFormDirty(true));
    });
    byId('applyPreset').addEventListener('click', applySettingsPreset);
    byId('copyLastHistory').addEventListener('click', () => {
      copyLastHistory().catch((error) => {
        byId('formStatus').className = 'status error';
        byId('formStatus').textContent = error.message;
      });
    });
    byId('copyDiagnostics').addEventListener('click', () => {
      copyDiagnostics().catch((error) => {
        byId('formStatus').className = 'status error';
        byId('formStatus').textContent = error.message;
      });
    });
    byId('cancelActiveOrders').addEventListener('click', () => {
      cancelActiveOrders().catch((error) => {
        byId('formStatus').className = 'status error';
        byId('formStatus').textContent = error.message;
      });
    });
    byId('resetSpotStats').addEventListener('click', () => {
      resetSpotStats().catch((error) => {
        byId('formStatus').className = 'status error';
        byId('formStatus').textContent = error.message;
      });
    });
    byId('refreshAutoRecommendation').addEventListener('click', async () => {
      const status = byId('formStatus');
      status.className = 'status';
      status.textContent = 'Refreshing auto recommendation from Bybit market data...';
      await loadDashboard({ forceSettingsRefresh: false });
      status.className = 'status ok';
      status.textContent = `Auto recommendation refreshed for ${latestDashboardData?.settings?.symbol || 'current profile'}.`;
    });
    byId('applyAutoRecommendation').addEventListener('click', async () => {
      const status = byId('formStatus');
      const symbol = selectedSymbol || latestDashboardData?.settings?.symbol;
      const applyUrl = symbol ? `/api/settings/apply-auto?symbol=${encodeURIComponent(symbol)}` : '/api/settings/apply-auto';
      const response = await fetch(applyUrl, { method: 'POST' });
      const result = await response.json();
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to apply auto recommendation.');
      if (response.ok) {
        await loadDashboard({ forceSettingsRefresh: true });
      }
    });
    byId('applySelectedStrategyRecommendation').addEventListener('click', async () => {
      await applySelectedStrategyRecommendation('Refreshing market data and applying it to the selected strategy...');
    });
    byId('profileTabs').addEventListener('click', async (event) => {
      const actionTarget = event.target.closest('[data-action]');
      if (!actionTarget) {
        return;
      }

      const action = actionTarget.dataset.action;
      const symbol = actionTarget.dataset.symbol;
      if (action === 'select-profile' && symbol) {
        await loadSelectedProfile(symbol);
        return;
      }

      if (action === 'delete-profile' && symbol) {
        event.stopPropagation();
        const response = await fetch(`/api/settings/${encodeURIComponent(symbol)}`, { method: 'DELETE' });
        const result = await response.json();
        const status = byId('formStatus');
        status.className = `status ${response.ok ? 'ok' : 'error'}`;
        status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to delete settings.');
        if (response.ok) {
          if (selectedSymbol === symbol.toUpperCase()) {
            selectedSymbol = null;
          }
          isCreatingNewProfile = false;
          setSettingsFormDirty(false);
          updateSelectedSymbolUrl();
          await loadDashboard({ forceSettingsRefresh: true });
        }
        return;
      }

      if (action === 'new-profile') {
        selectedSymbol = null;
        isCreatingNewProfile = true;
        updateSettingsForm(defaultNewSettings);
        setSettingsFormDirty(true);
        updateSelectedSymbolUrl();
        renderProfileTabs(profileCache);
        byId('formStatus').className = 'status';
        byId('formStatus').textContent = 'Fill the new config and press Apply Settings to create a profile.';
      }
    });
    byId('configSummaryRows').addEventListener('click', async (event) => {
      const row = event.target.closest('tr[data-symbol]');
      if (!row) {
        return;
      }

      await loadSelectedProfile(row.dataset.symbol);
    });
    byId('resumeTrading').addEventListener('click', async () => {
      const resumeUrl = selectedSymbol ? `/api/resume?symbol=${encodeURIComponent(selectedSymbol)}` : '/api/resume';
      const response = await fetch(resumeUrl, { method: 'POST' });
      const result = await response.json();
      const status = byId('formStatus');
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to resume trading.');
      if (response.ok) {
        await loadDashboard({ forceSettingsRefresh: true });
      }
    });
    byId('autoUpdateResume').addEventListener('click', async () => {
      await applySelectedStrategyRecommendation('Updating stop/range/config for the selected strategy and resuming if possible...');
    });

    document.getElementById('settingsForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const payload = buildSettingsPayload();

      const response = await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      const result = await response.json();
      const status = byId('formStatus');
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to save settings.');
      if (response.ok) {
        selectedSymbol = (result.symbol || payload.symbol).toUpperCase();
        isCreatingNewProfile = false;
        updateSelectedSymbolUrl();
        setSettingsFormDirty(false);
        await loadDashboard({ forceSettingsRefresh: true });
      }
    });

    loadDashboard().catch((error) => {
      byId('formStatus').className = 'status error';
      byId('formStatus').textContent = error.message;
    });
    setInterval(() => loadDashboard({ background: true }).catch(() => {}), 10000);
  </script>
</body>
</html>
""";

    private List<string> ValidateRequest(string symbol, string category, UpdateSettingsRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(symbol))
        {
            errors.Add("Symbol is required.");
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            errors.Add("Category is required.");
        }

        if (request.LowerPrice >= request.UpperPrice)
        {
            errors.Add("GRID_LOWER_PRICE must be lower than GRID_UPPER_PRICE.");
        }

        if (request.Step <= 0m)
        {
            errors.Add("GRID_STEP must be positive.");
        }

        if (request.OrderSizeUsdt <= 0m)
        {
            errors.Add("ORDER_SIZE_USDT must be positive.");
        }

        if (request.StopLowerPrice >= request.LowerPrice)
        {
            errors.Add("STOP_LOWER_PRICE must be lower than GRID_LOWER_PRICE.");
        }

        if (request.StopUpperPrice <= request.UpperPrice)
        {
            errors.Add("STOP_UPPER_PRICE must be higher than GRID_UPPER_PRICE.");
        }

        return errors;
    }

    private static StrategySelectionMode? ParseStrategySelectionMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "manual" => StrategySelectionMode.Manual,
            "auto" => StrategySelectionMode.Auto,
            _ => null
        };
    }

    private static TradingStrategyType? ParseTradingStrategyType(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "grid" => TradingStrategyType.Grid,
            "dca" => TradingStrategyType.Dca,
            "combo" => TradingStrategyType.Combo,
            "btd" => TradingStrategyType.Btd,
            "signal" or "signalbot" or "signal_bot" or "signal bot" => TradingStrategyType.Signal,
            "trend" or "trend_follow" or "trendfollow" or "trend following" => TradingStrategyType.TrendFollowing,
            "breakout" or "breakout_trend" => TradingStrategyType.Breakout,
            "hybrid" or "multi" or "all" or "combo_signal" or "combo signal" or "hybrid_signal" or "grid_dca_btd_signal" => TradingStrategyType.Hybrid,
            "reduceonly" or "reduce_only" or "reduce only" or "sellonly" or "sell_only" or "sell only" => TradingStrategyType.ReduceOnly,
            "notrade" or "no_trade" or "no trade" => TradingStrategyType.NoTrade,
            "pause" => TradingStrategyType.Pause,
            _ => null
        };
    }

    private static string? NormalizeStrategyConfigJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> TryClearPauseForSettingsAsync(GridBotSettings settings, CancellationToken cancellationToken)
    {
        var state = await _repository.GetBotStateAsync(settings.Symbol, cancellationToken);
        if (state is null || !state.IsPaused)
        {
            return string.Empty;
        }

        var currentPrice = await GetCurrentOrLastPriceAsync(settings.Category, settings.Symbol, state.LastObservedPrice, cancellationToken);
        var resumeBlockReason = GetResumeBlockReason(settings, currentPrice);
        if (resumeBlockReason is not null)
        {
            return $" Bot remains paused: {resumeBlockReason}";
        }

        state.IsPaused = false;
        state.PauseReason = null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return $" Pause cleared for {settings.Symbol}.";
    }

    private static bool IsStopBoundaryPause(string? pauseReason)
    {
        return pauseReason is not null &&
            (pauseReason.Contains("STOP_LOWER_PRICE", StringComparison.OrdinalIgnoreCase) ||
             pauseReason.Contains("STOP_UPPER_PRICE", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAggressiveModeActive(GridOptions gridOptions, BotState? state, DateTimeOffset now)
    {
        return gridOptions.AggressiveModeEnabled &&
            (state is null ||
             (state.AggressiveModeEnabled &&
              (state.AggressiveModeDisabledUntil is null || state.AggressiveModeDisabledUntil <= now)));
    }

    private async Task<decimal?> GetCurrentOrLastPriceAsync(string category, string symbol, decimal? fallbackPrice, CancellationToken cancellationToken)
    {
        try
        {
            var ticker = await _bybitRestClient.GetTickerAsync(category, symbol, cancellationToken);
            return ticker.LastPrice;
        }
        catch
        {
            return fallbackPrice;
        }
    }

    private static string? GetResumeBlockReason(GridBotSettings settings, decimal? currentPrice)
    {
        if (currentPrice is null)
        {
            return null;
        }

        if (currentPrice < settings.StopLowerPrice)
        {
            return $"Current price {currentPrice} is below Stop Lower {settings.StopLowerPrice}. Update settings first or wait for price recovery.";
        }

        if (currentPrice > settings.StopUpperPrice)
        {
            return $"Current price {currentPrice} is above Stop Upper {settings.StopUpperPrice}. Update settings first or wait for price recovery.";
        }

        return null;
    }

    private static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();

    private static string? NormalizeOptionalSymbol(string? symbol) =>
        string.IsNullOrWhiteSpace(symbol) ? null : NormalizeSymbol(symbol);

    private static DashboardOrderItem MapOrder(
        GridOrder order,
        OrderSourceContext sourceContext,
        IReadOnlyDictionary<string, string> sourceLabels)
    {
        var source = sourceLabels.TryGetValue(order.OrderLinkId, out var sourceLabel)
            ? sourceLabel
            : ResolveOrderSource(order, sourceContext);

        var filledQuantity = order.FilledQuantity;
        var remainingQuantity = decimal.Max(0m, order.Quantity - order.FilledQuantity);
        var notionalUsdt = order.Price * order.Quantity;
        var remainingNotionalUsdt = order.Price * remainingQuantity;
        var fillPrice = order.AverageFillPrice > 0m ? order.AverageFillPrice : order.Price;
        var filledNotional = fillPrice * filledQuantity;
        var tradePnl = order.Side == TradeSide.Sell ? order.RealizedPnl : 0m;
        var netCashFlow = filledQuantity <= 0m
            ? 0m
            : order.Side == TradeSide.Buy
                ? -filledNotional - order.FeePaid
                : filledNotional - order.FeePaid;

        return new DashboardOrderItem
        {
            OrderLinkId = order.OrderLinkId,
            BybitOrderId = order.BybitOrderId,
            ParentOrderLinkId = order.ParentOrderLinkId,
            OrderGroup = ResolveOrderGroup(order),
            LadderRole = ResolveLadderRole(order, source),
            Symbol = order.Symbol,
            Side = order.Side.ToString(),
            Source = source,
            Price = order.Price,
            Quantity = order.Quantity,
            FilledQuantity = filledQuantity,
            NotionalUsdt = notionalUsdt,
            RemainingNotionalUsdt = remainingNotionalUsdt,
            RealizedPnl = order.RealizedPnl,
            TradePnl = tradePnl,
            NetCashFlow = netCashFlow,
            FeePaid = order.FeePaid,
            Status = order.Status.ToString(),
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            FilledAt = order.FilledAt
        };
    }

    private static string? ResolveOrderGroup(GridOrder order) =>
        order.Side == TradeSide.Sell && !string.IsNullOrWhiteSpace(order.ParentOrderLinkId)
            ? order.ParentOrderLinkId
            : null;

    private static string? ResolveLadderRole(GridOrder order, string source)
    {
        if (order.Side != TradeSide.Sell || string.IsNullOrWhiteSpace(order.ParentOrderLinkId))
        {
            return null;
        }

        if (string.Equals(order.ParentOrderLinkId, "reduce-only-exit", StringComparison.Ordinal))
        {
            return "Reduce-only exit";
        }

        return source.Contains("Grid", StringComparison.OrdinalIgnoreCase)
            ? "Grid follow-up"
            : "TP ladder";
    }

    private static DashboardNoTradeReason MapNoTradeReason(NoTradeReasonRecord reason, DateTimeOffset now)
    {
        return new DashboardNoTradeReason
        {
            Code = reason.ReasonCode.ToString(),
            StrategyType = reason.StrategyType,
            Reason = reason.Reason,
            CreatedAt = reason.CreatedAt,
            MinutesAgo = Math.Max(0, (int)Math.Floor((now - reason.CreatedAt).TotalMinutes))
        };
    }

    private static IReadOnlyList<DashboardStrategyPerformanceItem> BuildStrategyPerformance(
        IReadOnlyCollection<GridOrder> orders,
        IReadOnlyDictionary<string, string> sourceLabels)
    {
        var strategyOrder = new[] { "Grid", "BTD", "DCA", "Signal", "ReduceOnly", "Hybrid" };
        var groups = orders
            .GroupBy(
                order => NormalizePerformanceStrategy(sourceLabels.TryGetValue(order.OrderLinkId, out var source)
                    ? source
                    : order.StrategySource),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var result = new List<DashboardStrategyPerformanceItem>();
        foreach (var strategy in strategyOrder)
        {
            groups.Remove(strategy, out var strategyOrders);
            result.Add(MapStrategyPerformance(strategy, strategyOrders ?? Array.Empty<GridOrder>()));
        }

        foreach (var extraGroup in groups.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(MapStrategyPerformance(extraGroup.Key, extraGroup.Value));
        }

        return result;
    }

    private static IReadOnlyList<DashboardDailyStrategyPerformanceItem> BuildDailyStrategyPerformance(
        IReadOnlyCollection<GridOrder> orders,
        IReadOnlyDictionary<string, string> sourceLabels)
    {
        return orders
            .Where(order => order.FilledQuantity > 0m)
            .GroupBy(order => new
            {
                Date = (order.FilledAt ?? order.UpdatedAt).UtcDateTime.Date,
                Strategy = NormalizePerformanceStrategy(sourceLabels.TryGetValue(order.OrderLinkId, out var source)
                    ? source
                    : order.StrategySource)
            })
            .OrderByDescending(group => group.Key.Date)
            .ThenBy(group => PerformanceStrategySortIndex(group.Key.Strategy))
            .ThenBy(group => group.Key.Strategy, StringComparer.OrdinalIgnoreCase)
            .Take(42)
            .Select(group =>
            {
                var filledOrders = group.ToArray();
                var closedOrders = filledOrders.Where(order => order.Side == TradeSide.Sell).ToArray();
                var wins = closedOrders.Count(order => order.RealizedPnl > 0m);

                return new DashboardDailyStrategyPerformanceItem
                {
                    PerformanceDate = group.Key.Date.ToString("yyyy-MM-dd"),
                    Strategy = group.Key.Strategy,
                    FeesPaid = filledOrders.Sum(order => order.FeePaid),
                    NetPnl = closedOrders.Sum(order => order.RealizedPnl),
                    FilledTradesCount = filledOrders.Length,
                    ClosedTradesCount = closedOrders.Length,
                    WinRate = closedOrders.Length == 0 ? 0m : wins * 100m / closedOrders.Length
                };
            })
            .ToArray();
    }

    private static DashboardStrategyPerformanceItem MapStrategyPerformance(
        string strategy,
        IReadOnlyCollection<GridOrder> orders)
    {
        var filledOrders = orders
            .Where(order => order.FilledQuantity > 0m)
            .ToArray();
        var closedOrders = filledOrders
            .Where(order => order.Side == TradeSide.Sell)
            .ToArray();
        var wins = closedOrders
            .Where(order => order.RealizedPnl > 0m)
            .Select(order => order.RealizedPnl)
            .ToArray();
        var losses = closedOrders
            .Where(order => order.RealizedPnl < 0m)
            .Select(order => order.RealizedPnl)
            .ToArray();

        return new DashboardStrategyPerformanceItem
        {
            Strategy = strategy,
            GrossPnl = closedOrders.Sum(order => order.RealizedPnl + order.FeePaid),
            FeesPaid = filledOrders.Sum(order => order.FeePaid),
            NetPnl = closedOrders.Sum(order => order.RealizedPnl),
            FilledTradesCount = filledOrders.Length,
            ClosedTradesCount = closedOrders.Length,
            ActiveOrdersCount = orders.Count(order => order.IsActive),
            WinRate = closedOrders.Length == 0 ? 0m : wins.Length * 100m / closedOrders.Length,
            AverageWin = wins.Length == 0 ? 0m : wins.Average(),
            AverageLoss = losses.Length == 0 ? 0m : losses.Average()
        };
    }

    private static string NormalizePerformanceStrategy(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            string.Equals(source, "Managed", StringComparison.OrdinalIgnoreCase))
        {
            return "Grid";
        }

        var normalized = source.Trim();
        if (normalized.StartsWith("Hybrid", StringComparison.OrdinalIgnoreCase))
        {
            return "Hybrid";
        }

        if (normalized.Contains("BTD", StringComparison.OrdinalIgnoreCase))
        {
            return "BTD";
        }

        if (normalized.Contains("DCA", StringComparison.OrdinalIgnoreCase))
        {
            return "DCA";
        }

        if (normalized.Contains("Signal", StringComparison.OrdinalIgnoreCase))
        {
            return "Signal";
        }

        if (normalized.Contains("ReduceOnly", StringComparison.OrdinalIgnoreCase))
        {
            return "ReduceOnly";
        }

        if (normalized.Contains("Grid", StringComparison.OrdinalIgnoreCase))
        {
            return "Grid";
        }

        return normalized;
    }

    private static int PerformanceStrategySortIndex(string strategy)
    {
        return strategy switch
        {
            "Grid" => 0,
            "BTD" => 1,
            "DCA" => 2,
            "Signal" => 3,
            "ReduceOnly" => 4,
            "Hybrid" => 5,
            _ => 100
        };
    }

    private static OrderSourceContext ResolveOrderSourceContext(TradingStrategyType strategyType)
    {
        return strategyType switch
        {
            TradingStrategyType.Dca => new OrderSourceContext("Managed", "DCA", "DCA"),
            TradingStrategyType.Btd => new OrderSourceContext("Managed", "BTD", "BTD"),
            TradingStrategyType.Combo => new OrderSourceContext("Combo-Grid", "Combo-DCA", "Combo-BTD"),
            TradingStrategyType.Hybrid => new OrderSourceContext("Hybrid-Grid", "Hybrid-DCA", "Hybrid-BTD"),
            TradingStrategyType.Signal => new OrderSourceContext("Managed", "DCA", "BTD"),
            TradingStrategyType.TrendFollow or TradingStrategyType.TrendFollowing or TradingStrategyType.Breakout => new OrderSourceContext("Managed", "DCA", "BTD"),
            TradingStrategyType.ReduceOnly or TradingStrategyType.NoTrade or TradingStrategyType.Pause => new OrderSourceContext("Grid", "DCA", "BTD"),
            _ => new OrderSourceContext("Grid", "DCA", "BTD")
        };
    }

    private static string ResolveOrderSource(GridOrder order, OrderSourceContext context)
    {
        var directSource = ResolveDirectOrderSource(order);
        if (directSource is not null)
        {
            return directSource;
        }

        return context.DefaultLabel;
    }

    private static string? ResolveDirectOrderSource(GridOrder order)
    {
        var persistedSource = NormalizeOrderSource(order.StrategySource);
        if (persistedSource is not null &&
            (!string.Equals(persistedSource, "Grid", StringComparison.OrdinalIgnoreCase) ||
             string.IsNullOrWhiteSpace(order.ParentOrderLinkId)))
        {
            return persistedSource;
        }

        return order.ParentOrderLinkId switch
        {
            "dca-entry" => "DCA",
            "btd-entry" => "BTD",
            "signal-entry" or "signal-exit" => "Signal",
            "trend-entry" or "trend-exit" => "Trend",
            "reduce-only-exit" => "ReduceOnly",
            null or "" => persistedSource,
            _ => null
        };
    }

    private static string? NormalizeOrderSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            string.Equals(source, "Managed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return source.Trim();
    }

    private static IReadOnlyDictionary<string, string> ResolveOrderSourceLabels(
        IReadOnlyCollection<GridOrder> orders,
        OrderSourceContext context)
    {
        var orderByLinkId = orders.ToDictionary(order => order.OrderLinkId, StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var order in orders)
        {
            labels[order.OrderLinkId] = ResolveOrderSource(order, context, orderByLinkId, labels);
        }

        return labels;
    }

    private static string ResolveOrderSource(
        GridOrder order,
        OrderSourceContext context,
        IReadOnlyDictionary<string, GridOrder> orderByLinkId,
        IDictionary<string, string> labels)
    {
        if (labels.TryGetValue(order.OrderLinkId, out var cachedLabel))
        {
            return cachedLabel;
        }

        var directLabel = ResolveDirectOrderSource(order);
        if (directLabel is not null)
        {
            return directLabel;
        }

        if (string.IsNullOrWhiteSpace(order.ParentOrderLinkId))
        {
            return context.DefaultLabel;
        }

        if (!orderByLinkId.TryGetValue(order.ParentOrderLinkId, out var parentOrder))
        {
            return context.DefaultLabel;
        }

        var parentLabel = ResolveOrderSource(parentOrder, context, orderByLinkId, labels);
        return parentLabel == context.DefaultLabel ? context.DefaultLabel : parentLabel;
    }

    private sealed record OrderSourceContext(
        string DefaultLabel,
        string DcaEntryLabel,
        string BtdEntryLabel);

    private static DashboardMarketRegime MapMarketRegime(MarketRegimeAnalysis analysis)
    {
        return new DashboardMarketRegime
        {
            Regime = analysis.Regime.ToString().ToLowerInvariant(),
            Confidence = analysis.Confidence,
            Recommendation = analysis.Recommendation,
            Adx = analysis.Adx,
            MovePercent = analysis.MovePercent,
            RangePercent = analysis.RangePercent,
            VolumeRatio = analysis.VolumeRatio,
            Support = analysis.Support,
            Resistance = analysis.Resistance
        };
    }

    private static DashboardSignalAnalysis MapSignalAnalysis(SignalAnalysis analysis)
    {
        return new DashboardSignalAnalysis
        {
            Signal = analysis.Signal.ToString().ToLowerInvariant(),
            Confidence = analysis.Confidence,
            Reason = analysis.Reason,
            EmaFast = analysis.EmaFast,
            EmaSlow = analysis.EmaSlow,
            Rsi = analysis.Rsi,
            BollingerPosition = analysis.BollingerPosition,
            VolumeRatio = analysis.VolumeRatio,
            TrendStrength = analysis.TrendStrength
        };
    }

    private static DashboardBtdDiagnostics BuildBtdDiagnostics(
        GridOptions options,
        GridBotSettings settings,
        IReadOnlyList<Candle> candles,
        IReadOnlyCollection<Candle> btcCandles,
        MarketPhaseResult phase,
        decimal? currentPrice,
        bool aggressiveModeActive)
    {
        var price = currentPrice ?? candles.OrderBy(candle => candle.OpenTime).LastOrDefault()?.Close ?? 0m;
        var ordered = candles.OrderBy(candle => candle.OpenTime).ToArray();
        if (price <= 0m || ordered.Length < Math.Max(options.EmaSlow, options.TrendEmaSlow) + 1)
        {
            return new DashboardBtdDiagnostics
            {
                Phase = phase.Phase.ToString(),
                Reason = "BTD diagnostics unavailable: not enough candle data.",
                IsAllowed = false
            };
        }

        var config = ParseBtdDiagnosticsConfig(settings);
        var emaFast = CalculateEma(ordered, options.EmaFast);
        var emaSlow = CalculateEma(ordered, options.EmaSlow);
        var btcRiskOff = options.BtdBlockOnBtcRiskOff &&
            IsBtcRiskOff(btcCandles, options.BtcLookbackCandles, options.BtcMaxMovePercent);
        var distanceToEma = Math.Min(PercentDistance(price, emaFast), PercentDistance(price, emaSlow));
        var recentHigh = ordered.TakeLast(Math.Max(1, options.DumpLookbackCandles * 10)).Max(candle => candle.High);
        var pullbackPercent = recentHigh <= 0m ? 0m : (recentHigh - price) / recentHigh * 100m;
        var dipLookback = Math.Max(1, config.DipLookbackCandles);
        var dipHigh = ordered.TakeLast(dipLookback).Max(candle => candle.High);
        var dipDrawdownPercent = dipHigh <= 0m ? 0m : (dipHigh - price) / dipHigh * 100m;
        var dipTriggered = config.DipPercent <= 0m || dipDrawdownPercent >= config.DipPercent;

        var isAllowed = true;
        var reason = aggressiveModeActive
            ? "BTD aggressive conditions pass. Trend filters are relaxed; hard risk filters remain active."
            : "BTD conditions pass.";
        if (btcRiskOff)
        {
            isAllowed = false;
            reason = "BTD silent: BTC risk-off is active.";
        }
        else if (phase.Phase is MarketPhase.HighVolatility or MarketPhase.BreakoutDown)
        {
            isAllowed = false;
            reason = $"BTD silent: blocked by hard-risk phase {phase.Phase}. {phase.Reason}";
        }
        else if (phase.Phase == MarketPhase.Dump)
        {
            if (aggressiveModeActive && ReversalBtdDetector.TryDetect(ordered, out var reversalSetup))
            {
                reason = $"Reversal BTD allowed after dump: drawdown {reversalSetup.DrawdownPercent:F2}%, {reversalSetup.CandlesSinceLow} candles without a new low, buy volume {reversalSetup.BuyVolumeRatio:F2}x.";
            }
            else
            {
                isAllowed = false;
                reason = $"BTD silent: dump has not stabilized for reversal. {phase.Reason}";
            }
        }
        else if (!aggressiveModeActive && options.BtdRequireUptrend && phase.Phase != MarketPhase.PullbackInUptrend)
        {
            isAllowed = false;
            reason = $"BTD silent: phase is {phase.Phase}, expected PullbackInUptrend. {phase.Reason}";
        }
        else if (!aggressiveModeActive && emaFast <= emaSlow)
        {
            isAllowed = false;
            reason = "BTD silent: EMA fast is below or equal to EMA slow.";
        }
        else if (!aggressiveModeActive && distanceToEma > options.BtdMaxDistanceFromEmaPercent)
        {
            isAllowed = false;
            reason = $"BTD silent: price is {distanceToEma:F2}% from EMA, max {options.BtdMaxDistanceFromEmaPercent:F2}%.";
        }
        else if (!aggressiveModeActive && pullbackPercent < options.BtdMinPullbackPercent)
        {
            isAllowed = false;
            reason = $"BTD silent: pullback is {pullbackPercent:F2}%, min {options.BtdMinPullbackPercent:F2}%.";
        }
        else if (!dipTriggered && !aggressiveModeActive)
        {
            isAllowed = false;
            reason = $"BTD silent: dip trigger is {dipDrawdownPercent:F2}%, min {config.DipPercent:F2}%.";
        }

        return new DashboardBtdDiagnostics
        {
            Phase = phase.Phase.ToString(),
            EmaFast = emaFast,
            EmaSlow = emaSlow,
            BtcRiskOff = btcRiskOff,
            PullbackPercent = pullbackPercent,
            DistanceToEmaPercent = distanceToEma,
            DipTriggered = dipTriggered,
            IsAllowed = isAllowed,
            Reason = reason
        };
    }

    private static BtdStrategyConfig ParseBtdDiagnosticsConfig(GridBotSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.StrategyConfigJson))
        {
            return new BtdStrategyConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<BtdStrategyConfig>(settings.StrategyConfigJson, StrategyJsonOptions) ?? new BtdStrategyConfig();
        }
        catch
        {
            return new BtdStrategyConfig();
        }
    }

    private static decimal CalculateEma(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count == 0)
        {
            return 0m;
        }

        var multiplier = 2m / (Math.Max(1, period) + 1);
        var ema = candles[0].Close;
        foreach (var candle in candles.Skip(1))
        {
            ema = (candle.Close - ema) * multiplier + ema;
        }

        return ema;
    }

    private static decimal PercentDistance(decimal value, decimal reference)
    {
        return reference <= 0m ? 0m : Math.Abs(value - reference) / reference * 100m;
    }

    private static bool IsBtcRiskOff(IReadOnlyCollection<Candle> btcCandles, int lookbackCandles, decimal maxMovePercent)
    {
        var slice = btcCandles.OrderBy(candle => candle.OpenTime).TakeLast(Math.Max(1, lookbackCandles)).ToArray();
        if (slice.Length < Math.Max(1, lookbackCandles) || slice[0].Open <= 0m)
        {
            return false;
        }

        return (slice[^1].Close - slice[0].Open) / slice[0].Open * 100m <= -Math.Abs(maxMovePercent);
    }

    private static GridBotSettings BuildRecommendedSettings(
        GridBotSettings currentSettings,
        AutoConfigRecommendation recommendation,
        StrategySelectionMode strategySelectionMode,
        DateTimeOffset updatedAt)
    {
        return new GridBotSettings
        {
            Symbol = currentSettings.Symbol,
            Category = currentSettings.Category,
            StrategySelectionMode = strategySelectionMode,
            StrategyType = recommendation.StrategyType,
            StrategyConfigJson = recommendation.StrategyConfigJson,
            LowerPrice = recommendation.LowerPrice,
            UpperPrice = recommendation.UpperPrice,
            Step = recommendation.Step,
            OrderSizeUsdt = recommendation.OrderSizeUsdt,
            StopLowerPrice = recommendation.StopLowerPrice,
            StopUpperPrice = recommendation.StopUpperPrice,
            UpdatedAt = updatedAt
        };
    }

    private static DashboardAutoRecommendation MapAutoRecommendation(
        AutoConfigRecommendation recommendation,
        IReadOnlyList<string> applySafetyErrors)
    {
        return new DashboardAutoRecommendation
        {
            StrategyType = recommendation.StrategyType.ToString().ToLowerInvariant(),
            Reason = recommendation.Reason,
            LowerPrice = recommendation.LowerPrice,
            UpperPrice = recommendation.UpperPrice,
            Step = recommendation.Step,
            OrderSizeUsdt = recommendation.OrderSizeUsdt,
            StopLowerPrice = recommendation.StopLowerPrice,
            StopUpperPrice = recommendation.StopUpperPrice,
            StrategyConfigJson = recommendation.StrategyConfigJson,
            AnalysisCandleInterval = AnalysisDefaults.AutoRecommendationCandleInterval,
            AnalysisLookbackCandles = AnalysisDefaults.AutoRecommendationLookbackCandles,
            AtrPercent = recommendation.Metrics.AtrPercent,
            DrawdownPercent = recommendation.Metrics.DrawdownPercent,
            Support = recommendation.Metrics.Support,
            Resistance = recommendation.Metrics.Resistance,
            CanApply = applySafetyErrors.Count == 0,
            ApplySafetyErrors = applySafetyErrors
        };
    }
}
