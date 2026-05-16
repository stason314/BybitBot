using System.Text.Json;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Notifications;
using BybitGridBot.Risk;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IFuturesDashboardService
{
    Task<FuturesDashboardResponse> GetDashboardAsync(string? symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ApplyAutoRecommendationAsync(string? symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateFuturesSettingsRequest request, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> DeleteSettingsAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> SetProfileEnabledAsync(string symbol, bool enabled, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> PauseProfileAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ResumeProfileAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ClosePositionAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> OpenPaperTestPositionAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> OpenPaperTestShortPositionAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> CancelActiveOrdersAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ResetPaperStatsAsync(string symbol, CancellationToken cancellationToken);
    string RenderDashboardPage();
}

public sealed class FuturesDashboardService : IFuturesDashboardService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<string> StrategyActions =
    [
        nameof(FuturesTradeAction.OpenLong),
        nameof(FuturesTradeAction.CloseLong),
        nameof(FuturesTradeAction.OpenShort),
        nameof(FuturesTradeAction.CloseShort),
        nameof(FuturesTradeAction.ReduceOnlyClose)
    ];

    private readonly IBybitRestClient _bybitRestClient;
    private readonly BybitUserStreamTelemetry _userStreamTelemetry;
    private readonly AppOptions _appOptions;
    private readonly FuturesExecutionService _executionService;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesProtectionService _protectionService;
    private readonly FuturesRiskManager _riskManager;
    private readonly FuturesRiskOptions _riskOptions;
    private readonly FuturesStrategyQualityOptions _strategyQualityOptions;
    private readonly FuturesAutoConfigRecommender _recommender;
    private readonly ITelegramNotifier _notifier;
    private readonly IGridRepository _repository;

    public FuturesDashboardService(
        IBybitRestClient bybitRestClient,
        BybitUserStreamTelemetry userStreamTelemetry,
        IOptions<AppOptions> appOptions,
        FuturesExecutionService executionService,
        FuturesProtectionService protectionService,
        IOptions<FuturesOptions> futuresOptions,
        IOptions<FuturesRiskOptions> riskOptions,
        IOptions<FuturesStrategyQualityOptions> strategyQualityOptions,
        FuturesRiskManager riskManager,
        FuturesAutoConfigRecommender recommender,
        ITelegramNotifier notifier,
        IGridRepository repository)
    {
        _bybitRestClient = bybitRestClient;
        _userStreamTelemetry = userStreamTelemetry;
        _appOptions = appOptions.Value;
        _executionService = executionService;
        _protectionService = protectionService;
        _futuresOptions = futuresOptions.Value;
        _riskOptions = riskOptions.Value;
        _strategyQualityOptions = strategyQualityOptions.Value;
        _riskManager = riskManager;
        _recommender = recommender;
        _notifier = notifier;
        _repository = repository;
    }

    public async Task<FuturesDashboardResponse> GetDashboardAsync(string? symbol, CancellationToken cancellationToken)
    {
        var profiles = await _repository.GetFuturesSettingsProfilesAsync(cancellationToken);
        var selectedSymbol = NormalizeOptionalSymbol(symbol);
        var selectedSettings = selectedSymbol is null
            ? profiles.FirstOrDefault() ?? BuildDefaultSettings(_futuresOptions)
            : profiles.FirstOrDefault(profile => string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase))
                ?? BuildDefaultSettings(_futuresOptions, selectedSymbol);

        var position = BuildEmptyPosition(selectedSettings);
        BybitPositionSnapshot? bybitPositionSnapshot = null;
        string? positionError = null;
        try
        {
            if (_appOptions.TradingMode == TradingMode.Paper)
            {
                position = await GetPaperPositionAsync(selectedSettings, cancellationToken) ?? position;
            }
            else
            {
                bybitPositionSnapshot = await _bybitRestClient.GetPositionAsync(selectedSettings.Category, selectedSettings.Symbol, cancellationToken);
                if (bybitPositionSnapshot is not null)
                {
                    position = MapPosition(selectedSettings, bybitPositionSnapshot);
                }
            }
        }
        catch (Exception exception)
        {
            positionError = exception.Message;
            position = await GetPaperPositionAsync(selectedSettings, cancellationToken) ?? position;
        }

        var state = await _repository.GetBotStateAsync(FuturesStateKeys.ForSymbol(selectedSettings.Symbol), cancellationToken);
        var candles = await GetAnalysisCandlesAsync(selectedSettings, cancellationToken);
        var recommendation = _recommender.Recommend(selectedSettings, candles, position.Size > 0m);
        var recentOrders = await _repository.GetFuturesOrdersAsync(selectedSettings.Symbol, cancellationToken);
        var activeOrders = await _repository.GetActiveFuturesOrdersAsync(selectedSettings.Symbol, cancellationToken);
        var riskDecisions = await _repository.GetFuturesRiskDecisionsAsync(selectedSettings.Symbol, 20, cancellationToken);
        var lastPreflight = riskDecisions.FirstOrDefault(decision => string.Equals(decision.Source, "Preflight", StringComparison.OrdinalIgnoreCase));
        var recentFills = await _repository.GetFuturesFillsAsync(selectedSettings.Symbol, FuturesFillLedger.QueryLimit, cancellationToken);
        var fillLedger = FuturesFillLedger.Build(recentFills, DateOnly.FromDateTime(DateTime.UtcNow));
        var runtimeControls = await BuildRuntimeControlsAsync(profiles, state, cancellationToken);
        var selectedInstrumentRules = await TryGetInstrumentRulesAsync(selectedSettings, cancellationToken);
        var positionSnapshot = await TryResolvePositionSnapshotAsync(selectedSettings, cancellationToken);

        return new FuturesDashboardResponse
        {
            Profiles = profiles
                .Select(profile => new FuturesProfileItem
                {
                    Symbol = profile.Symbol,
                    Category = profile.Category,
                    Enabled = profile.Enabled,
                    IsSelected = string.Equals(profile.Symbol, selectedSettings.Symbol, StringComparison.OrdinalIgnoreCase)
                })
                .ToArray(),
            ConfigSummaries = await BuildConfigSummariesAsync(profiles, selectedSettings.Symbol, cancellationToken),
            Settings = MapSettings(selectedSettings),
            Position = position,
            PaperAccount = BuildPaperAccount(state, position, _futuresOptions.PaperInitialEquityUsdt, fillLedger, _appOptions.TradingMode),
            PnlStats = BuildPnlStats(recentFills, fillLedger),
            TestnetSoak = BuildTestnetSoakStatus(position, activeOrders, recentOrders, recentFills, riskDecisions),
            ProtectionStatus = BuildProtectionStatus(selectedSettings, recommendation, position, bybitPositionSnapshot, riskDecisions, _appOptions.TradingMode == TradingMode.Paper),
            UserStreamStatus = BuildUserStreamStatus(),
            RuntimeControls = runtimeControls,
            AggressiveMode = BuildAggressiveModeStatus(selectedSettings, recentFills, riskDecisions),
            StrategyQuality = BuildStrategyQuality(selectedSettings, positionSnapshot, selectedInstrumentRules, riskDecisions, recentFills),
            AutoRecommendation = MapAutoRecommendation(
                recommendation,
                selectedSettings,
                position,
                _futuresOptions.AutoApplyRecommendation,
                AllowsShortPositions()),
            StrategyActions = StrategyActions,
            ActiveOrders = activeOrders.Select(MapOrder).ToArray(),
            RecentOrders = recentOrders.Select(MapOrder).ToArray(),
            RiskDecisions = riskDecisions.Select(MapRiskDecision).ToArray(),
            LastPreflightResult = lastPreflight is null ? null : MapRiskDecision(lastPreflight),
            TradingMode = _appOptions.TradingMode.ToString(),
            FuturesEnabled = _futuresOptions.Enabled,
            PositionError = positionError,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<UpdateSettingsResponse> ApplyAutoRecommendationAsync(string? symbol, CancellationToken cancellationToken)
    {
        var profiles = await _repository.GetFuturesSettingsProfilesAsync(cancellationToken);
        var selectedSymbol = NormalizeOptionalSymbol(symbol);
        var settings = selectedSymbol is null
            ? profiles.FirstOrDefault()
            : profiles.FirstOrDefault(profile => string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase));
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = selectedSymbol,
                Message = "Cannot apply futures auto recommendation.",
                Errors = selectedSymbol is null
                    ? ["No futures profile exists."]
                    : [$"Futures profile {selectedSymbol} does not exist."]
            };
        }

        var candles = await GetAnalysisCandlesAsync(settings, cancellationToken);
        if (candles.Count == 0)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = settings.Symbol,
                Message = "Cannot apply futures auto recommendation.",
                Errors = ["Futures market data is unavailable."]
            };
        }

        var position = new FuturesPositionSnapshot { Symbol = settings.Symbol, Category = settings.Category };
        try
        {
            position = await ResolvePositionSnapshotAsync(settings, cancellationToken);
        }
        catch
        {
            position = new FuturesPositionSnapshot { Symbol = settings.Symbol, Category = settings.Category };
        }

        var hasOpenPosition = position.Size > 0m;
        var recommendation = _recommender.Recommend(settings, candles, hasOpenPosition);
        if (!FuturesAutoRecommendationSafety.CanApply(recommendation, position, AllowsShortPositions(), out var blockReason))
        {
            await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
            {
                Symbol = settings.Symbol,
                Source = "AutoRecommendationSkipped",
                IsAllowed = false,
                Reason = blockReason,
                Severity = RiskSeverity.Warning.ToString(),
                SuggestedAction = RiskSuggestedAction.BlockNewOrders.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);

            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = settings.Symbol,
                Message = "Futures auto recommendation was not applied.",
                Errors = [blockReason]
            };
        }

        var recommendedSettings = BuildRecommendedSettings(settings, recommendation);
        await _repository.SaveFuturesSettingsAsync(recommendedSettings, cancellationToken);
        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = settings.Symbol,
            Source = "AutoRecommendationApply",
            IsAllowed = true,
            Reason = $"Applied futures auto recommendation: {recommendation.StrategyType}. {recommendation.Reason}",
            Severity = RiskSeverity.Info.ToString(),
            SuggestedAction = RiskSuggestedAction.Allow.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        await TryUpdateProtectionAfterRecommendationApplyAsync(settings, recommendedSettings, position, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = settings.Symbol,
            Message = $"Futures auto recommendation applied: {recommendation.StrategyType}. {recommendation.Reason}"
        };
    }

    private async Task TryUpdateProtectionAfterRecommendationApplyAsync(
        FuturesBotSettings currentSettings,
        FuturesBotSettings recommendedSettings,
        FuturesPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        if (position.Size <= 0m || position.EntryPrice <= 0m)
        {
            return;
        }

        var currentTargets = await _protectionService.ResolveTargetsAsync(currentSettings, position, cancellationToken);
        var recommendedTargets = await _protectionService.ResolveTargetsAsync(recommendedSettings, position, cancellationToken);
        if (recommendedTargets.StopLoss <= 0m ||
            recommendedTargets.TakeProfit <= 0m ||
            TargetsMatch(currentTargets, recommendedTargets))
        {
            return;
        }

        if (WouldWorsenStopRisk(position.Side, currentTargets.StopLoss, recommendedTargets.StopLoss))
        {
            await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
            {
                Symbol = currentSettings.Symbol,
                Source = "AutoRecommendationProtectionSkipped",
                IsAllowed = false,
                Reason = $"Auto recommendation protection update skipped because recommended stop-loss would increase risk. Current StopLoss={currentTargets.StopLoss}, TakeProfit={currentTargets.TakeProfit}; Recommended StopLoss={recommendedTargets.StopLoss}, TakeProfit={recommendedTargets.TakeProfit}.",
                Severity = RiskSeverity.Warning.ToString(),
                SuggestedAction = RiskSuggestedAction.BlockNewOrders.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            return;
        }

        await _protectionService.EnsureProtectiveStopAsync(recommendedSettings, position, cancellationToken);
        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = currentSettings.Symbol,
            Source = "AutoRecommendationProtectionUpdate",
            IsAllowed = true,
            Reason = $"Auto recommendation protection update applied. Current StopLoss={currentTargets.StopLoss}, TakeProfit={currentTargets.TakeProfit}; Recommended StopLoss={recommendedTargets.StopLoss}, TakeProfit={recommendedTargets.TakeProfit}.",
            Severity = RiskSeverity.Info.ToString(),
            SuggestedAction = RiskSuggestedAction.Allow.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    public async Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateFuturesSettingsRequest request, CancellationToken cancellationToken)
    {
        var symbol = NormalizeSymbol(request.Symbol);
        var category = NormalizeCategory(request.Category);
        var strategyType = ParseFuturesStrategyType(request.StrategyType);
        var marginMode = ParseMarginMode(request.MarginMode);
        var positionMode = ParsePositionMode(request.PositionMode);
        var direction = ParseDirection(request.Direction);
        var aggressiveModeKind = ParseAggressiveModeKind(request.AggressiveModeKind);
        var strategyConfigJson = NormalizeStrategyConfigJson(request.StrategyConfigJson);

        var errors = ValidateRequest(symbol, category, request);
        if (strategyType is null)
        {
            errors.Add("Strategy type must be trendfollow, breakout, gridlongonly, trendfollowshortonly, breakdownshort, gridshortonly, reduceonly, or pause.");
        }

        if (marginMode is null)
        {
            errors.Add("Margin mode must be isolated for the MVP.");
        }

        if (positionMode is null)
        {
            errors.Add("Position mode must be oneway for the MVP.");
        }

        if (direction is null)
        {
            errors.Add("Direction must be long-only, short-only, or long+short.");
        }

        if (aggressiveModeKind is null)
        {
            errors.Add("Aggressive mode kind must be normal or test.");
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

        var settings = new FuturesBotSettings
        {
            Enabled = request.Enabled,
            Symbol = symbol,
            Category = category,
            StrategyType = strategyType!.Value,
            StrategyConfigJson = strategyConfigJson!,
            Leverage = request.Leverage,
            MarginMode = marginMode!.Value,
            PositionMode = positionMode!.Value,
            Direction = direction!.Value,
            MaxNotionalUsdt = request.MaxNotionalUsdt,
            MaxMarginUsdt = request.MaxMarginUsdt,
            StopLossPercent = request.StopLossPercent,
            TakeProfitPercent = request.TakeProfitPercent,
            LiquidationBufferPercent = request.LiquidationBufferPercent,
            ReduceOnlyEnabled = request.ReduceOnlyEnabled,
            AggressiveModeEnabled = request.AggressiveModeEnabled,
            AggressiveModeKind = aggressiveModeKind!.Value,
            AggressiveEntryMultiplier = request.AggressiveEntryMultiplier,
            AggressiveMaxOrdersPerHour = request.AggressiveMaxOrdersPerHour,
            AggressiveMinSecondsBetweenEntries = request.AggressiveMinSecondsBetweenEntries,
            AggressiveMaxConsecutiveLosses = request.AggressiveMaxConsecutiveLosses,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _repository.SaveFuturesSettingsAsync(settings, cancellationToken);
        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = symbol,
            Message = $"Futures settings saved for {symbol}."
        };
    }

    public async Task<UpdateSettingsResponse> DeleteSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        await _repository.DeleteFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = $"Futures settings deleted for {normalizedSymbol}."
        };
    }

    public async Task<UpdateSettingsResponse> SetProfileEnabledAsync(string symbol, bool enabled, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var settings = await _repository.GetFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile not found.",
                Errors = [$"Futures profile {normalizedSymbol} does not exist."]
            };
        }

        await _repository.SaveFuturesSettingsAsync(WithEnabled(settings, enabled), cancellationToken);
        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = enabled ? $"Futures profile enabled for {normalizedSymbol}." : $"Futures profile disabled for {normalizedSymbol}."
        };

        static FuturesBotSettings WithEnabled(FuturesBotSettings current, bool value) => new()
        {
            Enabled = value,
            Symbol = current.Symbol,
            Category = current.Category,
            StrategyType = current.StrategyType,
            StrategyConfigJson = current.StrategyConfigJson,
            Leverage = current.Leverage,
            MarginMode = current.MarginMode,
            PositionMode = current.PositionMode,
            Direction = current.Direction,
            MaxNotionalUsdt = current.MaxNotionalUsdt,
            MaxMarginUsdt = current.MaxMarginUsdt,
            StopLossPercent = current.StopLossPercent,
            TakeProfitPercent = current.TakeProfitPercent,
            LiquidationBufferPercent = current.LiquidationBufferPercent,
            ReduceOnlyEnabled = current.ReduceOnlyEnabled,
            AggressiveModeEnabled = current.AggressiveModeEnabled,
            AggressiveModeKind = current.AggressiveModeKind,
            AggressiveEntryMultiplier = current.AggressiveEntryMultiplier,
            AggressiveMaxOrdersPerHour = current.AggressiveMaxOrdersPerHour,
            AggressiveMinSecondsBetweenEntries = current.AggressiveMinSecondsBetweenEntries,
            AggressiveMaxConsecutiveLosses = current.AggressiveMaxConsecutiveLosses,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<UpdateSettingsResponse> PauseProfileAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var settings = await _repository.GetFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile not found.",
                Errors = [$"Futures profile {normalizedSymbol} does not exist."]
            };
        }

        var state = await EnsureFuturesStateAsync(settings, 0m, cancellationToken);
        var reason = "Manual futures emergency pause from UI.";
        state.IsPaused = true;
        state.PauseReason = reason;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);
        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = settings.Symbol,
            Source = "ManualEmergencyPause",
            IsAllowed = false,
            Reason = reason,
            Severity = RiskSeverity.Critical.ToString(),
            SuggestedAction = RiskSuggestedAction.PauseBot.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        await _notifier.NotifyAsync($"Futures emergency pause enabled from UI.\nSymbol: `{settings.Symbol}`", cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = $"Futures emergency pause enabled for {normalizedSymbol}."
        };
    }

    public async Task<UpdateSettingsResponse> ResumeProfileAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (_riskOptions.EmergencyPause)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures emergency pause is enabled by env.",
                Errors = ["Set FUTURES_EMERGENCY_PAUSE=false before resuming from UI."]
            };
        }

        var settings = await _repository.GetFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile not found.",
                Errors = [$"Futures profile {normalizedSymbol} does not exist."]
            };
        }

        var state = await EnsureFuturesStateAsync(settings, 0m, cancellationToken);
        state.IsPaused = false;
        state.PauseReason = null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _repository.SaveBotStateAsync(state, cancellationToken);
        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = settings.Symbol,
            Source = "ManualResume",
            IsAllowed = true,
            Reason = "Manual futures resume from UI.",
            Severity = RiskSeverity.Info.ToString(),
            SuggestedAction = RiskSuggestedAction.Allow.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        await _notifier.NotifyAsync($"Futures profile resumed from UI.\nSymbol: `{settings.Symbol}`", cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = $"Futures profile resumed for {normalizedSymbol}."
        };
    }

    public async Task<UpdateSettingsResponse> ClosePositionAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var settings = await _repository.GetFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile not found.",
                Errors = [$"Futures profile {normalizedSymbol} does not exist."]
            };
        }

        var position = await ResolvePositionSnapshotAsync(settings, cancellationToken);
        if (position.Size <= 0m)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "No open futures position.",
                Errors = ["No open futures position to close."]
            };
        }

        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(settings.Category, settings.Symbol, cancellationToken);
        var referencePrice = position.MarkPrice > 0m ? position.MarkPrice : position.EntryPrice;
        if (referencePrice <= 0m)
        {
            referencePrice = (await _bybitRestClient.GetTickerAsync(settings.Category, settings.Symbol, cancellationToken)).LastPrice;
        }

        var price = instrument.RoundPrice(referencePrice);
        var quantity = instrument.RoundQuantity(position.Size);
        var closeAction = IsShort(position.Side)
            ? FuturesTradeAction.CloseShort
            : FuturesTradeAction.CloseLong;
        var result = await _executionService.ExecuteAsync(new FuturesExecutionRequest
        {
            Settings = settings,
            Intent = new FuturesTradeIntent
            {
                Symbol = settings.Symbol,
                Category = settings.Category,
                Action = closeAction,
                Price = price,
                Quantity = quantity,
                Leverage = settings.Leverage,
                PositionIdx = 0,
                OrderLinkId = FuturesOrderLinkIds.Create(closeAction),
                Reason = "dashboard-reduce-only-close"
            },
            Position = position,
            MarkPrice = price,
            Instrument = MapInstrumentRules(instrument)
        }, cancellationToken);

        if (result.IsPaper)
        {
            var state = await EnsurePaperStateAsync(settings, price, cancellationToken);
            FuturesReconciliationService.ApplyPositionToState(state, result.Position, updatePaperEquity: true);
            await _repository.SaveBotStateAsync(state, cancellationToken);
        }

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = result.IsPaper ? "Paper futures position closed reduce-only." : "Futures reduce-only close submitted."
        };
    }

    public Task<UpdateSettingsResponse> OpenPaperTestPositionAsync(string symbol, CancellationToken cancellationToken) =>
        OpenPaperTestPositionAsync(symbol, FuturesTradeAction.OpenLong, cancellationToken);

    public Task<UpdateSettingsResponse> OpenPaperTestShortPositionAsync(string symbol, CancellationToken cancellationToken) =>
        OpenPaperTestPositionAsync(symbol, FuturesTradeAction.OpenShort, cancellationToken);

    private async Task<UpdateSettingsResponse> OpenPaperTestPositionAsync(
        string symbol,
        FuturesTradeAction action,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var isShort = action == FuturesTradeAction.OpenShort;
        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Paper test entry is disabled outside paper mode.",
                Errors = ["Paper test entries are available only when TRADING_MODE=Paper."]
            };
        }

        var settings = await _repository.GetFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile not found.",
                Errors = [$"Futures profile {normalizedSymbol} does not exist."]
            };
        }

        if (!settings.Enabled)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile is disabled.",
                Errors = ["Enable the futures profile before opening a paper test entry."]
            };
        }

        if (isShort && settings.Direction == FuturesDirection.LongOnly)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Paper short entry skipped.",
                Errors = ["Set futures Direction to short-only or long+short before opening a paper short entry."]
            };
        }

        if (!isShort && settings.Direction == FuturesDirection.ShortOnly)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Paper long entry skipped.",
                Errors = ["Set futures Direction to long-only or long+short before opening a paper long entry."]
            };
        }

        var ticker = await _bybitRestClient.GetTickerAsync(settings.Category, settings.Symbol, cancellationToken);
        var instrument = await _bybitRestClient.GetInstrumentInfoAsync(settings.Category, settings.Symbol, cancellationToken);
        var price = instrument.RoundPrice(ticker.LastPrice);
        if (price <= 0m)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Cannot open paper test entry.",
                Errors = ["Current futures ticker price is unavailable."]
            };
        }

        var position = await ResolvePositionSnapshotAsync(settings, cancellationToken);
        if (position.Size > 0m && !settings.AggressiveModeEnabled)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Paper test entry skipped.",
                Errors = ["A futures position is already open."]
            };
        }

        var quantity = CalculateMinimumOrderQuantity(price, MapInstrumentRules(instrument));
        if (quantity <= 0m)
        {
            quantity = instrument.RoundQuantity(settings.MaxNotionalUsdt * 0.25m / price);
        }

        var notional = quantity * price;
        if (quantity <= 0m || notional <= 0m)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Cannot open paper test entry.",
                Errors = ["Instrument minimum quantity could not be resolved."]
            };
        }

        if (notional > settings.MaxNotionalUsdt)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Cannot open paper test entry.",
                Errors = [$"Minimum order notional {notional:F4} exceeds profile max notional {settings.MaxNotionalUsdt:F4}."]
            };
        }

        var intent = new FuturesTradeIntent
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Action = action,
            Price = price,
            Quantity = quantity,
            Leverage = settings.Leverage,
            StopLossPrice = isShort
                ? instrument.RoundPrice(price * (1m + settings.StopLossPercent / 100m))
                : instrument.RoundPrice(price * (1m - settings.StopLossPercent / 100m)),
            TakeProfitPrice = isShort
                ? instrument.RoundPrice(price * (1m - settings.TakeProfitPercent / 100m))
                : instrument.RoundPrice(price * (1m + settings.TakeProfitPercent / 100m)),
            LiquidationPrice = isShort
                ? EstimateShortLiquidationPrice(price, settings.Leverage)
                : EstimateLongLiquidationPrice(price, settings.Leverage),
            PositionIdx = 0,
            OrderLinkId = FuturesOrderLinkIds.Create(action),
            Reason = isShort ? "dashboard-paper-test-short-entry" : "dashboard-paper-test-entry"
        };

        var state = await EnsurePaperStateAsync(settings, price, cancellationToken);
        var aggressiveBlockReason = await GetAggressiveEntryBlockReasonAsync(settings, position, cancellationToken);
        if (aggressiveBlockReason is not null)
        {
            await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
            {
                Symbol = settings.Symbol,
                Source = "AggressiveGuard",
                OrderLinkId = intent.OrderLinkId,
                Action = intent.Action,
                IsAllowed = false,
                Reason = aggressiveBlockReason,
                Severity = RiskSeverity.Warning.ToString(),
                SuggestedAction = RiskSuggestedAction.BlockNewOrders.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Paper aggressive entry blocked.",
                Errors = [aggressiveBlockReason]
            };
        }

        var openPositionCount = await CountOpenFuturesPositionsAsync(cancellationToken);
        var riskDecision = _riskManager.Evaluate(new FuturesRiskEvaluationContext
        {
            RiskOptions = ResolveProfileRiskOptions(settings),
            Intent = intent,
            Position = position,
            MarkPrice = price,
            AvailableMarginUsdt = decimal.Max(0m, settings.MaxMarginUsdt - position.MarginUsedUsdt),
            DailyRealizedPnl = state.DailyRealizedPnl,
            TotalRealizedPnl = state.TotalRealizedPnl,
            AccountEquityUsdt = state.QuoteAssetBalance + state.UnrealizedPnl,
            CurrentDrawdownUsdt = state.CurrentDrawdownUsdt,
            CurrentDrawdownPercent = state.CurrentDrawdownPercent,
            OpenPositionCount = openPositionCount
        });
        await _repository.AddFuturesRiskDecisionAsync(new FuturesRiskDecisionRecord
        {
            Symbol = settings.Symbol,
            Source = position.Size > 0m
                ? (isShort ? "AggressiveShortScaleIn" : "AggressiveScaleIn")
                : (isShort ? "PaperTestShortEntry" : "PaperTestEntry"),
            OrderLinkId = intent.OrderLinkId,
            Action = intent.Action,
            IsAllowed = riskDecision.IsAllowed,
            Reason = riskDecision.Reason,
            Severity = riskDecision.Severity.ToString(),
            SuggestedAction = riskDecision.SuggestedAction.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        if (!riskDecision.IsAllowed)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Paper test entry blocked by futures risk manager.",
                Errors = [riskDecision.Reason]
            };
        }

        var result = await _executionService.ExecuteAsync(new FuturesExecutionRequest
        {
            Settings = settings,
            Intent = intent,
            Position = position,
            MarkPrice = price,
            Instrument = MapInstrumentRules(instrument)
        }, cancellationToken);

        FuturesReconciliationService.ApplyPositionToState(state, result.Position, updatePaperEquity: true);
        await _repository.SaveBotStateAsync(state, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = position.Size > 0m
                ? $"Paper aggressive scale-in opened. Notional: {intent.NotionalUsdt:F4} USDT, qty: {intent.Quantity:F8}."
                : $"Paper test {(isShort ? "short" : "long")} opened. Notional: {intent.NotionalUsdt:F4} USDT, qty: {intent.Quantity:F8}."
        };
    }

    public async Task<UpdateSettingsResponse> CancelActiveOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var settings = await _repository.GetFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile not found.",
                Errors = [$"Futures profile {normalizedSymbol} does not exist."]
            };
        }

        var activeOrders = await _repository.GetActiveFuturesOrdersAsync(normalizedSymbol, cancellationToken);
        foreach (var order in activeOrders)
        {
            if (_appOptions.TradingMode == TradingMode.Testnet)
            {
                await _bybitRestClient.CancelOrderAsync(settings.Category, settings.Symbol, order.BybitOrderId, order.OrderLinkId, cancellationToken);
            }

            order.Status = OrderStatus.Cancelled;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.UpsertFuturesOrderAsync(order, cancellationToken);
        }

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = $"Cancelled {activeOrders.Count} futures orders."
        };
    }

    public async Task<UpdateSettingsResponse> ResetPaperStatsAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Paper stats reset is disabled outside paper mode.",
                Errors = ["Reset Stats is available only when TRADING_MODE=Paper."]
            };
        }

        var settings = await _repository.GetFuturesSettingsAsync(normalizedSymbol, cancellationToken);
        if (settings is null)
        {
            return new UpdateSettingsResponse
            {
                Success = false,
                Symbol = normalizedSymbol,
                Message = "Futures profile not found.",
                Errors = [$"Futures profile {normalizedSymbol} does not exist."]
            };
        }

        var position = await ResolvePositionSnapshotAsync(settings, cancellationToken);
        var stateKey = FuturesStateKeys.ForSymbol(settings.Symbol);
        var currentState = await _repository.GetBotStateAsync(stateKey, cancellationToken);
        var markPrice = position.MarkPrice > 0m
            ? position.MarkPrice
            : currentState?.LastObservedPrice ?? 0m;

        await _repository.ClearFuturesPaperHistoryAsync(settings.Symbol, cancellationToken);
        await _repository.SaveBotStateAsync(new BotState
        {
            Symbol = stateKey,
            TradingMode = TradingMode.Paper,
            IsInitialized = currentState?.IsInitialized ?? true,
            IsPaused = currentState?.IsPaused ?? false,
            PauseReason = currentState?.PauseReason,
            LastObservedPrice = markPrice > 0m ? markPrice : null,
            PositionSide = "None",
            BaseAssetQuantity = 0m,
            QuoteAssetBalance = _futuresOptions.PaperInitialEquityUsdt,
            AverageEntryPrice = 0m,
            ReduceOnly = false,
            PositionIdx = 0,
            Leverage = settings.Leverage,
            MarginMode = settings.MarginMode.ToString(),
            EntryPrice = 0m,
            MarkPrice = markPrice,
            LiquidationPrice = 0m,
            UnrealizedPnl = 0m,
            TotalRealizedPnl = 0m,
            DailyRealizedPnl = 0m,
            PeakEquityUsdt = _futuresOptions.PaperInitialEquityUsdt,
            CurrentDrawdownUsdt = 0m,
            CurrentDrawdownPercent = 0m,
            ProfitProtectionPeakPrice = currentState?.ProfitProtectionPeakPrice ?? 0m,
            ProfitProtectionTrailingStopPrice = currentState?.ProfitProtectionTrailingStopPrice ?? 0m,
            DailyPnlDate = DateOnly.FromDateTime(DateTime.UtcNow),
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = normalizedSymbol,
            Message = $"Paper stats and simulation state reset for {normalizedSymbol}."
        };
    }

    public string RenderDashboardPage() => """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Bybit Futures Console</title>
  <style>
    :root {
      --bg: #f4f2ec;
      --panel: rgba(255,255,255,0.88);
      --ink: #20231f;
      --muted: #69716a;
      --accent: #17664e;
      --accent-2: #2f7f8f;
      --danger: #b13622;
      --line: rgba(32,35,31,0.1);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--ink);
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    .shell { width: min(1280px, calc(100% - 32px)); margin: 0 auto; padding: 28px 0 44px; }
    .topbar { display: flex; justify-content: space-between; align-items: center; gap: 14px; margin-bottom: 18px; flex-wrap: wrap; }
    h1 { margin: 0; font-size: clamp(30px, 5vw, 52px); letter-spacing: 0; }
    h2 { margin: 0 0 14px; font-size: 20px; }
    a { color: var(--accent); font-weight: 700; text-decoration: none; }
    .subtle { color: var(--muted); line-height: 1.5; }
    .layout { display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(360px, .9fr); gap: 18px; align-items: start; }
    .panel {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 18px;
      box-shadow: 0 14px 40px rgba(32,35,31,0.08);
    }
    .tabs, .actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .tab, button {
      appearance: none;
      border: 0;
      border-radius: 8px;
      padding: 10px 13px;
      background: rgba(32,35,31,0.08);
      color: var(--ink);
      font: 700 13px/1 system-ui, sans-serif;
      cursor: pointer;
    }
    button.primary { background: var(--accent); color: #fff; }
    button.danger { background: var(--danger); color: #fff; }
    .tab.active { background: var(--accent); color: #fff; }
    .tab .x { margin-left: 8px; opacity: .75; }
    .stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin: 18px 0; }
    .stat { background: rgba(255,255,255,0.78); border: 1px solid var(--line); border-radius: 8px; padding: 14px; min-height: 86px; }
    .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .08em; margin-bottom: 8px; }
    .value { font-size: 22px; font-weight: 800; overflow-wrap: anywhere; }
    .price-value { transition: color .18s ease; }
    .positive { color: var(--accent); }
    .negative { color: var(--danger); }
    form { display: grid; grid-template-columns: repeat(2, minmax(0,1fr)); gap: 12px; }
    label { display: block; color: var(--muted); font-size: 13px; margin-bottom: 6px; }
    input, select, textarea {
      width: 100%;
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 11px 12px;
      font: inherit;
      color: var(--ink);
      background: #fff;
    }
    textarea { min-height: 104px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 13px; }
    .full { grid-column: 1 / -1; }
    table { width: 100%; border-collapse: collapse; font-size: 14px; }
    th, td { padding: 11px 9px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }
    th { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .08em; }
    tr[data-symbol] { cursor: pointer; }
    tr.selected { background: rgba(23,102,78,0.08); }
    .table-wrap { overflow: auto; }
    .futures-market-scan-table-wrap {
      --futures-market-scan-row-height: 72px;
      max-height: calc(44px + (var(--futures-market-scan-row-height) * 6));
      overflow: auto;
      overscroll-behavior: contain;
    }
    .futures-market-scan-table-wrap th {
      position: sticky;
      top: 0;
      z-index: 1;
      background: var(--panel);
      box-shadow: 0 1px 0 rgba(32,35,31,0.1);
    }
    .futures-market-scan-table-wrap th,
    .futures-market-scan-table-wrap td {
      padding: 10px 8px;
    }
    .futures-market-scan-table-wrap tbody tr {
      height: var(--futures-market-scan-row-height);
    }
    .status { min-height: 22px; color: var(--muted); margin-top: 12px; }
    .status.ok { color: var(--accent); }
    .status.error { color: var(--danger); }
    .notice { padding: 12px; border-radius: 8px; background: rgba(177,54,34,.08); color: var(--danger); margin: 0 0 14px; }
    .actions-list { display: flex; gap: 8px; flex-wrap: wrap; }
    .chip { border-radius: 999px; padding: 8px 10px; background: rgba(47,127,143,0.12); color: var(--accent-2); font-size: 12px; font-weight: 700; }
    .copy-controls { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .hours-input { max-width: 88px; }
    .compact-button { padding: 9px 11px; }
    .market-ticker {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 14px;
      margin: 14px 0 4px;
      padding: 16px 18px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: rgba(255,255,255,0.88);
    }
    .ticker-symbol { font-size: 18px; font-weight: 800; }
    .ticker-price { font-size: 30px; font-weight: 900; line-height: 1; }
    @media (max-width: 980px) {
      .layout, form { grid-template-columns: 1fr; }
      .stats { grid-template-columns: repeat(2, minmax(0,1fr)); }
      .market-ticker { align-items: flex-start; flex-direction: column; }
      th, td { white-space: nowrap; }
    }
  </style>
</head>
<body>
  <main class="shell">
    <div class="topbar">
      <div>
        <h1>Futures Console</h1>
      </div>
      <div class="actions">
        <a href="/">Spot/Grid</a>
        <button type="button" id="newProfile">New Config</button>
      </div>
    </div>

    <div class="tabs" id="profileTabs"></div>

    <section class="market-ticker" id="marketTicker"></section>

    <section class="stats" id="positionStats"></section>

    <section class="panel" style="margin-bottom:18px;">
      <div class="actions" style="justify-content:space-between;">
        <h2 style="margin:0;">Paper Account & PnL</h2>
        <div class="copy-controls">
          <input class="hours-input" id="copyHistoryHours" type="number" min="0.1" step="0.5" value="1" aria-label="History hours" />
          <span class="subtle">hours</span>
          <button type="button" class="compact-button" id="copyLastHistory">Copy Last</button>
          <button type="button" class="compact-button" id="copyDiagnostics">Copy Diagnostics</button>
          <button type="button" class="compact-button danger" id="resetPaperStats">Reset Stats</button>
        </div>
      </div>
      <div class="stats" id="paperAccountStats" style="margin-bottom:0;"></div>
      <div class="stats" id="pnlStats" style="margin-top:12px;margin-bottom:0;"></div>
      <div class="subtle" id="pnlStatsNote" style="margin-top:8px;"></div>
      <div class="status" id="copyStatus"></div>
    </section>

    <section class="panel" style="margin-bottom:18px;">
      <div class="actions" style="justify-content:space-between;">
        <div id="runtimeStatus" class="actions"></div>
        <div class="actions">
          <button type="button" id="toggleProfile">Toggle</button>
          <button type="button" class="danger" id="emergencyPause">Emergency Pause</button>
          <button type="button" id="resumeProfile">Resume</button>
          <button type="button" class="primary" id="paperTestEntry">Paper Test Entry</button>
          <button type="button" class="primary" id="paperTestShortEntry">Paper Short Entry</button>
          <button type="button" class="danger" id="closePosition">Close Position</button>
          <button type="button" class="danger" id="cancelFuturesOrders">Cancel Orders</button>
        </div>
      </div>
      <div class="stats" id="runtimeGuardStats" style="margin-bottom:0;margin-top:12px;"></div>
      <div class="status" id="controlStatus"></div>
    </section>

    <section class="panel" style="margin-bottom:18px;">
      <div class="actions" style="justify-content:space-between;">
        <h2 style="margin:0;">Aggressive Mode</h2>
        <div class="actions">
          <select id="aggressiveModeEnabled" name="aggressiveModeEnabled">
            <option value="false">Conservative</option>
            <option value="true">Aggressive</option>
          </select>
          <select id="aggressiveModeKind" name="aggressiveModeKind">
            <option value="normal">Normal</option>
            <option value="test">Test</option>
          </select>
        </div>
      </div>
      <form id="aggressiveSettingsForm" style="margin-top:12px;">
        <div><label for="aggressiveEntryMultiplier">Entry Multiplier</label><input id="aggressiveEntryMultiplier" name="aggressiveEntryMultiplier" type="number" min="0.01" step="0.01" /></div>
        <div><label for="aggressiveMaxOrdersPerHour">Max Orders / Hour</label><input id="aggressiveMaxOrdersPerHour" name="aggressiveMaxOrdersPerHour" type="number" min="0" step="1" /></div>
        <div><label for="aggressiveMinSecondsBetweenEntries">Min Seconds Between Entries</label><input id="aggressiveMinSecondsBetweenEntries" name="aggressiveMinSecondsBetweenEntries" type="number" min="0" step="1" /></div>
        <div><label for="aggressiveMaxConsecutiveLosses">Max Consecutive Losses</label><input id="aggressiveMaxConsecutiveLosses" name="aggressiveMaxConsecutiveLosses" type="number" min="0" step="1" /></div>
      </form>
      <div class="stats" id="aggressiveModeStats" style="margin-bottom:0;"></div>
      <div class="subtle" id="aggressiveModeReason" style="margin-top:12px;"></div>
    </section>

    <section class="panel" style="margin-bottom:18px;">
      <div class="actions" style="justify-content:space-between;">
        <h2 style="margin:0;">Testnet Soak</h2>
        <span class="subtle">Real-fill readiness and reconciliation signals</span>
      </div>
      <div class="stats" id="testnetSoakStats" style="margin-bottom:0;"></div>
      <div class="subtle" id="testnetSoakRisk" style="margin-top:12px;"></div>
      <div class="stats" id="protectionStats" style="margin-bottom:0;margin-top:12px;"></div>
      <div class="subtle" id="protectionReason" style="margin-top:12px;"></div>
      <div class="stats" id="userStreamStats" style="margin-bottom:0;margin-top:12px;"></div>
    </section>

    <section class="panel" style="margin-bottom:18px;">
      <div class="actions" style="justify-content:space-between;">
        <h2 style="margin:0;">Strategy Quality</h2>
        <span class="subtle">No-trade, filter, and risk diagnostics</span>
      </div>
      <div class="stats" id="strategyQualityStats" style="margin-bottom:0;"></div>
      <div class="subtle" id="strategyQualityReason" style="margin-top:12px;"></div>
    </section>

    <section class="panel" style="margin-bottom:18px;">
      <div class="actions" style="justify-content:space-between;">
        <h2 style="margin:0;">Auto Recommendation</h2>
        <div class="actions">
          <button type="button" id="refreshAutoRecommendation">Refresh</button>
          <button type="button" class="primary" id="applyAutoRecommendation">Apply</button>
        </div>
      </div>
      <div class="stats" id="autoRecommendationStats" style="margin-bottom:0;"></div>
      <div class="subtle" id="autoRecommendationReason" style="margin-top:12px;"></div>
    </section>

    <section class="panel" style="margin-bottom:18px;">
      <div class="actions" style="justify-content:space-between;">
        <div>
          <h2 style="margin:0;">Futures Market Scanner</h2>
          <div class="subtle">Ranks linear USDT futures by candle fit for futures strategies and prepares a config.</div>
        </div>
        <div class="actions">
          <input class="hours-input" id="futuresMarketScanLimit" type="number" min="10" max="500" step="10" value="120" aria-label="Max futures pairs" />
          <button type="button" id="runFuturesMarketScan">Scan Market</button>
        </div>
      </div>
      <div class="status" id="futuresMarketScanStatus">Scanner has not run yet.</div>
      <div class="table-wrap futures-market-scan-table-wrap">
        <table>
          <thead>
            <tr>
              <th>Symbol</th><th>Actionability</th><th>Market</th><th>Strategy</th><th>Fit</th><th>Entry</th><th>Price</th><th>ATR</th><th>6h Volume</th><th>Support / Resistance</th><th>Reasons</th><th>Action</th>
            </tr>
          </thead>
          <tbody id="futuresMarketScanRows"></tbody>
        </table>
      </div>
    </section>

    <div class="layout">
      <section class="panel">
        <h2>Futures Profiles</h2>
        <div class="stats" id="futuresProfilesProfitStats" style="margin-bottom:12px;"></div>
        <div class="table-wrap">
          <table>
            <thead>
              <tr><th>Symbol</th><th>Strategy</th><th>Direction</th><th>Leverage</th><th>Max Notional</th><th>Max Margin</th><th>Daily Profit</th><th>Total Profit ↓</th><th>Updated</th></tr>
            </thead>
            <tbody id="configRows"></tbody>
          </table>
        </div>
      </section>

      <section class="panel">
        <h2>Futures Config</h2>
        <form id="settingsForm">
          <div><label for="symbol">Symbol</label><input id="symbol" name="symbol" required /></div>
          <div><label for="category">Category</label><input id="category" name="category" value="linear" required /></div>
          <div><label for="enabled">Profile</label><select id="enabled" name="enabled"><option value="true">Enabled</option><option value="false">Disabled</option></select></div>
          <div><label for="strategyType">Strategy</label><select id="strategyType" name="strategyType"><option value="pause">Pause</option><option value="trendfollow">Trend Follow</option><option value="breakout">Breakout</option><option value="gridlongonly">Grid Long Only</option><option value="trendfollowshortonly">Trend Follow Short</option><option value="breakdownshort">Breakdown Short</option><option value="gridshortonly">Grid Short Only</option><option value="reduceonly">Reduce Only</option></select></div>
          <div><label for="direction">Direction</label><select id="direction" name="direction"><option value="long-only">Long only</option><option value="short-only">Short only</option><option value="long+short">Long + short</option></select></div>
          <div><label for="leverage">Leverage</label><input id="leverage" name="leverage" type="number" step="0.01" min="1" required /></div>
          <div><label for="marginMode">Margin Mode</label><select id="marginMode" name="marginMode"><option value="isolated">Isolated</option></select></div>
          <div><label for="positionMode">Position Mode</label><select id="positionMode" name="positionMode"><option value="oneway">One-way</option></select></div>
          <div><label for="maxNotionalUsdt">Max Notional USDT</label><input id="maxNotionalUsdt" name="maxNotionalUsdt" type="number" step="0.00000001" required /></div>
          <div><label for="maxMarginUsdt">Max Margin USDT</label><input id="maxMarginUsdt" name="maxMarginUsdt" type="number" step="0.00000001" required /></div>
          <div><label for="rrPreset">RR Preset</label><select id="rrPreset" name="rrPreset"><option value="2/6">Conservative 2/6</option><option value="3/9">Balanced 3/9</option><option value="10/30">Swing 10/30</option><option value="custom">Custom</option></select></div>
          <div><label for="stopLossPercent">Stop Loss %</label><input id="stopLossPercent" name="stopLossPercent" type="number" step="0.0001" required /></div>
          <div><label for="takeProfitPercent">Take Profit %</label><input id="takeProfitPercent" name="takeProfitPercent" type="number" step="0.0001" required /></div>
          <div><label for="liquidationBufferPercent">Liquidation Buffer %</label><input id="liquidationBufferPercent" name="liquidationBufferPercent" type="number" step="0.0001" required /></div>
          <div><label for="reduceOnlyEnabled">Reduce Only</label><select id="reduceOnlyEnabled" name="reduceOnlyEnabled"><option value="true">Enabled</option><option value="false">Disabled</option></select></div>
          <div class="full"><label for="strategyConfigJson">Strategy Config JSON</label><textarea id="strategyConfigJson" name="strategyConfigJson">{}</textarea></div>
          <div class="full actions">
            <button type="submit" class="primary">Save Futures Config</button>
            <button type="button" class="danger" id="deleteProfile">Delete</button>
          </div>
        </form>
        <div class="status" id="formStatus"></div>
      </section>
    </div>

    <section class="panel" style="margin-top:18px;">
      <h2>Active Futures Orders</h2>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Link</th><th>Action</th><th>Side</th><th>Qty</th><th>Filled</th><th>Price</th><th>Status</th><th>Reduce</th><th>Updated</th></tr></thead>
          <tbody id="activeOrderRows"></tbody>
        </table>
      </div>
    </section>

    <section class="panel" style="margin-top:18px;">
      <h2>Recent Futures Orders</h2>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Time</th><th>Link</th><th>Action</th><th>Side</th><th>Qty</th><th>Filled</th><th>Avg</th><th>Status</th><th>Realized PnL</th><th>Fee</th></tr></thead>
          <tbody id="recentOrderRows"></tbody>
        </table>
      </div>
    </section>

    <section class="panel" style="margin-top:18px;">
      <h2>Risk Decisions</h2>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Time</th><th>Source</th><th>Action</th><th>Allowed</th><th>Severity</th><th>Reason</th></tr></thead>
          <tbody id="riskDecisionRows"></tbody>
        </table>
      </div>
    </section>

    <section class="panel" style="margin-top:18px;">
      <h2>Strategy Action Model</h2>
      <div class="actions-list" id="strategyActions"></div>
    </section>
  </main>

  <script>
    const byId = (id) => document.getElementById(id);
    const fields = ['symbol','category','enabled','strategyType','strategyConfigJson','leverage','marginMode','positionMode','direction','maxNotionalUsdt','maxMarginUsdt','stopLossPercent','takeProfitPercent','liquidationBufferPercent','reduceOnlyEnabled','aggressiveModeEnabled','aggressiveModeKind','aggressiveEntryMultiplier','aggressiveMaxOrdersPerHour','aggressiveMinSecondsBetweenEntries','aggressiveMaxConsecutiveLosses'];
    const rrPresets = {
      '2/6': { stopLossPercent: 2, takeProfitPercent: 6 },
      '3/9': { stopLossPercent: 3, takeProfitPercent: 9 },
      '10/30': { stopLossPercent: 10, takeProfitPercent: 30 }
    };
    const defaults = {
      symbol: 'BTCUSDT',
      category: 'linear',
      enabled: true,
      strategyType: 'pause',
      strategyConfigJson: '{}',
      leverage: 2,
      marginMode: 'isolated',
      positionMode: 'oneway',
      direction: 'long-only',
      maxNotionalUsdt: 100,
      maxMarginUsdt: 50,
      stopLossPercent: 2,
      takeProfitPercent: 6,
      liquidationBufferPercent: 15,
      reduceOnlyEnabled: true,
      aggressiveModeEnabled: false,
      aggressiveModeKind: 'normal',
      aggressiveEntryMultiplier: 1.5,
      aggressiveMaxOrdersPerHour: 6,
      aggressiveMinSecondsBetweenEntries: 60,
      aggressiveMaxConsecutiveLosses: 2
    };
    let selectedSymbol = new URLSearchParams(window.location.search).get('symbol')?.toUpperCase() || null;
    let creating = false;
    let dirty = false;
    let latest = null;
    let latestFuturesMarketScanData = null;
    let controlStatusSymbol = null;
    let controlStatusKind = null;
    const lastPrices = new Map();

    const escapeHtml = (value) => String(value)
      .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;').replaceAll("'", '&#039;');
    const formatNumber = (value) => value === null || value === undefined ? '-' : Number(value).toLocaleString(undefined, { maximumFractionDigits: 8 });
    const formatDate = (value) => value ? new Date(value).toLocaleString() : '-';
    const formatPnl = (value) => {
      const number = Number(value || 0);
      const cls = number > 0 ? 'positive' : number < 0 ? 'negative' : '';
      return `<span class="${cls}">${formatNumber(number)}</span>`;
    };
    const formatCurrentPrice = (symbol, price) => {
      const number = Number(price || 0);
      const previous = lastPrices.get(symbol);
      const cls = previous === undefined || number === previous ? '' : number > previous ? 'positive' : 'negative';
      if (number > 0) {
        lastPrices.set(symbol, number);
      }
      return `<span class="price-value ${cls}">${formatNumber(number)}</span>`;
    };
    const setUrl = () => {
      const url = new URL(window.location.href);
      if (selectedSymbol && !creating) {
        url.searchParams.set('symbol', selectedSymbol);
      } else {
        url.searchParams.delete('symbol');
      }
      window.history.replaceState({}, '', url);
    };
    const setControlStatus = (kind, text, symbol) => {
      byId('controlStatus').className = `status ${kind || ''}`.trim();
      byId('controlStatus').textContent = text || '';
      controlStatusSymbol = symbol ? symbol.toUpperCase() : null;
      controlStatusKind = kind || null;
    };
    const clearControlStatus = () => setControlStatus('', '', null);
    const resolveRrPreset = (stopLossPercent, takeProfitPercent) => {
      const sl = Number(stopLossPercent || 0);
      const tp = Number(takeProfitPercent || 0);
      const match = Object.entries(rrPresets).find(([, preset]) =>
        Math.abs(preset.stopLossPercent - sl) < 0.0001 &&
        Math.abs(preset.takeProfitPercent - tp) < 0.0001);
      return match ? match[0] : 'custom';
    };
    const updateStrategyRiskConfig = () => {
      const raw = byId('strategyConfigJson').value || '{}';
      try {
        const config = JSON.parse(raw);
        config.stopLossPercent = Number(byId('stopLossPercent').value || 0);
        config.takeProfitPercent = Number(byId('takeProfitPercent').value || 0);
        byId('strategyConfigJson').value = JSON.stringify(config);
      } catch {
        // Keep invalid JSON untouched so validation can report the real error on save.
      }
    };
    const updateForm = (settings) => {
      byId('symbol').value = settings.symbol;
      byId('category').value = settings.category;
      byId('enabled').value = String(settings.enabled);
      byId('strategyType').value = settings.strategyType;
      byId('strategyConfigJson').value = settings.strategyConfigJson || '{}';
      byId('leverage').value = settings.leverage;
      byId('marginMode').value = settings.marginMode;
      byId('positionMode').value = settings.positionMode;
      byId('direction').value = settings.direction;
      byId('maxNotionalUsdt').value = settings.maxNotionalUsdt;
      byId('maxMarginUsdt').value = settings.maxMarginUsdt;
      byId('stopLossPercent').value = settings.stopLossPercent;
      byId('takeProfitPercent').value = settings.takeProfitPercent;
      byId('rrPreset').value = resolveRrPreset(settings.stopLossPercent, settings.takeProfitPercent);
      byId('liquidationBufferPercent').value = settings.liquidationBufferPercent;
      byId('reduceOnlyEnabled').value = String(settings.reduceOnlyEnabled);
      byId('aggressiveModeEnabled').value = String(settings.aggressiveModeEnabled);
      byId('aggressiveModeKind').value = settings.aggressiveModeKind || 'normal';
      byId('aggressiveEntryMultiplier').value = settings.aggressiveEntryMultiplier;
      byId('aggressiveMaxOrdersPerHour').value = settings.aggressiveMaxOrdersPerHour;
      byId('aggressiveMinSecondsBetweenEntries').value = settings.aggressiveMinSecondsBetweenEntries;
      byId('aggressiveMaxConsecutiveLosses').value = settings.aggressiveMaxConsecutiveLosses;
    };
    const readPayload = () => ({
      symbol: byId('symbol').value,
      category: byId('category').value,
      enabled: byId('enabled').value === 'true',
      strategyType: byId('strategyType').value,
      strategyConfigJson: byId('strategyConfigJson').value,
      leverage: Number(byId('leverage').value),
      marginMode: byId('marginMode').value,
      positionMode: byId('positionMode').value,
      direction: byId('direction').value,
      maxNotionalUsdt: Number(byId('maxNotionalUsdt').value),
      maxMarginUsdt: Number(byId('maxMarginUsdt').value),
      stopLossPercent: Number(byId('stopLossPercent').value),
      takeProfitPercent: Number(byId('takeProfitPercent').value),
      liquidationBufferPercent: Number(byId('liquidationBufferPercent').value),
      reduceOnlyEnabled: byId('reduceOnlyEnabled').value === 'true',
      aggressiveModeEnabled: byId('aggressiveModeEnabled').value === 'true',
      aggressiveModeKind: byId('aggressiveModeKind').value,
      aggressiveEntryMultiplier: Number(byId('aggressiveEntryMultiplier').value),
      aggressiveMaxOrdersPerHour: Number(byId('aggressiveMaxOrdersPerHour').value),
      aggressiveMinSecondsBetweenEntries: Number(byId('aggressiveMinSecondsBetweenEntries').value),
      aggressiveMaxConsecutiveLosses: Number(byId('aggressiveMaxConsecutiveLosses').value)
    });
    const renderTabs = (profiles) => {
      byId('profileTabs').innerHTML = profiles.length === 0 && !creating
        ? '<span class="subtle">No futures configs yet.</span>'
        : profiles.map(profile => `
          <button type="button" class="tab ${profile.isSelected && !creating ? 'active' : ''}" data-symbol="${escapeHtml(profile.symbol)}">
            ${escapeHtml(profile.symbol)} <span class="x" data-delete="${escapeHtml(profile.symbol)}">x</span>
          </button>`).join('');
    };
    const renderConfigs = (configs) => {
      const dailyTotal = configs.reduce((sum, config) => sum + Number(config.dailyRealizedPnl || 0), 0);
      const total = configs.reduce((sum, config) => sum + Number(config.totalRealizedPnl || 0), 0);
      byId('futuresProfilesProfitStats').innerHTML = [
        ['All Daily Profit', formatPnl(dailyTotal)],
        ['All Total Profit', formatPnl(total)]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');
      byId('configRows').innerHTML = configs.length === 0
        ? '<tr><td colspan="9">No futures configs yet.</td></tr>'
        : configs.map(config => `
          <tr data-symbol="${escapeHtml(config.symbol)}" class="${config.isSelected && !creating ? 'selected' : ''}">
            <td><strong>${escapeHtml(config.symbol)}</strong><br><span class="subtle">${escapeHtml(config.category)}</span></td>
            <td>${escapeHtml(config.strategyType)}</td>
            <td>${escapeHtml(config.direction)}<br><span class="subtle">${config.enabled ? 'enabled' : 'disabled'}</span></td>
            <td>${formatNumber(config.leverage)}x</td>
            <td>${formatNumber(config.maxNotionalUsdt)}</td>
            <td>${formatNumber(config.maxMarginUsdt)}</td>
            <td>${formatPnl(config.dailyRealizedPnl)}</td>
            <td>${formatPnl(config.totalRealizedPnl)}</td>
            <td>${formatDate(config.updatedAt)}</td>
          </tr>`).join('');
    };
    const renderFuturesMarketScanRows = (items) => {
      byId('futuresMarketScanRows').innerHTML = !items || items.length === 0
        ? '<tr><td colspan="12">No futures scan results yet.</td></tr>'
        : items.map(item => {
            const actionabilityScore = Number(item.actionabilityScore ?? item.score ?? 0);
            const canApply = item.settings && actionabilityScore >= 15;
            return `
              <tr>
                <td><strong>${escapeHtml(item.symbol)}</strong><br><span class="subtle">${escapeHtml(item.category)}</span></td>
                <td><strong>${formatNumber(item.actionabilityScore)}</strong><br><span class="subtle">${escapeHtml(item.actionabilityLabel || item.label)}</span></td>
                <td><strong>${formatNumber(item.marketFitScore || item.score)}</strong><br><span class="subtle">${escapeHtml(item.label)}</span></td>
                <td>${escapeHtml(item.recommendedStrategy)}<br><span class="subtle">${escapeHtml(item.recommendedDirection)}</span></td>
                <td title="Grid L/S ${formatNumber(item.gridLongFitScore)} / ${formatNumber(item.gridShortFitScore)}; Trend L/S ${formatNumber(item.trendLongFitScore)} / ${formatNumber(item.trendShortFitScore)}; BO/BD ${formatNumber(item.breakoutFitScore)} / ${formatNumber(item.breakdownFitScore)}">
                  <strong>${formatNumber(item.strategyFitScore)}</strong><br><span class="subtle">${escapeHtml(item.strategyFitName || 'Fit')}</span>
                </td>
                <td>${formatNumber(item.entryNotionalUsdt)} USDT</td>
                <td>${formatNumber(item.lastPrice)}</td>
                <td>${formatNumber(item.atrPercent)}%</td>
                <td>${formatNumber(item.volume6hUsdt)}</td>
                <td>${formatNumber(item.support)} / ${formatNumber(item.resistance)}</td>
                <td>${escapeHtml((item.reasons || []).join('; '))}</td>
                <td><button type="button" class="compact-button primary" data-action="apply-futures-scan-profile" data-symbol="${escapeHtml(item.symbol)}" ${canApply ? '' : 'disabled'}>${canApply ? 'Apply Config' : 'View Only'}</button></td>
              </tr>`;
          }).join('');
    };
    const runFuturesMarketScan = async () => {
      const status = byId('futuresMarketScanStatus');
      const limit = Number(byId('futuresMarketScanLimit').value || 120);
      status.className = 'status';
      status.textContent = 'Scanning linear futures market...';
      const response = await fetch(`/api/futures/market-scan?limit=${encodeURIComponent(limit)}`, { cache: 'no-store' });
      const result = await response.json();
      if (!response.ok) {
        throw new Error(result?.errors?.join(' | ') || result?.message || 'Futures market scan failed.');
      }

      latestFuturesMarketScanData = result;
      renderFuturesMarketScanRows(result.items || []);
      status.className = 'status ok';
      status.textContent = `Scanned ${result.scannedCount}/${result.candidateCount} linear futures. Failed: ${result.failedCount}.`;
    };
    const applyFuturesScanConfig = async (symbol) => {
      const status = byId('futuresMarketScanStatus');
      const item = latestFuturesMarketScanData?.items?.find(row => String(row.symbol || '').toUpperCase() === String(symbol || '').toUpperCase());
      if (!item?.settings) {
        status.className = 'status error';
        status.textContent = `No futures scan settings found for ${symbol}.`;
        return;
      }

      status.className = 'status';
      status.textContent = `Applying futures config for ${item.symbol}...`;
      const response = await fetch('/api/futures/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(item.settings)
      });
      const result = await response.json();
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to apply futures config.');
      if (response.ok) {
        selectedSymbol = (result.symbol || item.symbol).toUpperCase();
        creating = false;
        dirty = false;
        setUrl();
        await load(true);
      }
    };
    const resolveCurrentPrice = (data) => Number(data.position?.markPrice || data.autoRecommendation?.lastPrice || 0);
    const renderMarketTicker = (data) => {
      const symbol = data.position?.symbol || data.settings?.symbol || selectedSymbol || '-';
      const price = resolveCurrentPrice(data);
      byId('marketTicker').innerHTML = `
        <div>
          <div class="label">Current Futures Price</div>
          <div class="ticker-symbol">${escapeHtml(symbol)}</div>
        </div>
        <div class="ticker-price">${formatCurrentPrice(symbol, price)}</div>`;
    };
    const renderPosition = (data) => {
      const p = data.position;
      const symbol = p.symbol || data.settings?.symbol || selectedSymbol || '';
      const currentPrice = resolveCurrentPrice(data);
      const error = data.positionError ? `<div class="notice">Position sync unavailable: ${escapeHtml(data.positionError)}</div>` : '';
      byId('positionStats').innerHTML = [
        ['Current Price', formatCurrentPrice(symbol, currentPrice)],
        ['Side', escapeHtml(p.side)],
        ['Size', formatNumber(p.size)],
        ['Entry', formatNumber(p.entryPrice)],
        ['Mark', formatNumber(p.markPrice)],
        ['Liquidation', formatNumber(p.liquidationPrice)],
        ['Unrealized PnL', formatPnl(p.unrealizedPnl)],
        ['Margin Used', formatNumber(p.marginUsedUsdt)],
        ['Funding', formatPnl(p.funding)]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');
      byId('positionStats').insertAdjacentHTML('beforebegin', error);
    };
    const renderPaperAccount = (account, stats) => {
      byId('paperAccountStats').innerHTML = [
        ['Initial Equity', formatNumber(account.initialEquityUsdt)],
        ['Cash', formatNumber(account.cashUsdt)],
        ['Current Equity', formatPnl(account.currentEquityUsdt)],
        ['Return %', formatPnl(account.returnPercent)],
        ['Peak Equity', formatNumber(account.peakEquityUsdt)],
        ['Drawdown', formatPnl(-Math.abs(Number(account.currentDrawdownUsdt || 0)))],
        ['Drawdown %', formatPnl(-Math.abs(Number(account.currentDrawdownPercent || 0)))],
        ['Daily Realized', formatPnl(account.dailyRealizedPnl)]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');
      byId('pnlStats').innerHTML = [
        ['Gross Profit', formatPnl(stats.grossProfit)],
        ['Gross Loss', formatPnl(stats.grossLoss)],
        ['Trading PnL', formatPnl(stats.realizedTradingPnl)],
        ['Net PnL', formatPnl(stats.netPnl)],
        ['Fee / Trading PnL', `${formatNumber(stats.feeToTradingPnlPercent)}%`],
        ['Profit Efficiency', stats.profitEfficiencyStatus || '-'],
        ['Fees', formatPnl(-Math.abs(Number(stats.feesPaid || 0)))],
        ['Entry Fees', formatPnl(-Math.abs(Number(stats.entryFeesPaid || 0)))],
        ['Exit Fees', formatPnl(-Math.abs(Number(stats.exitFeesPaid || 0)))],
        ['Funding', formatPnl(stats.fundingPaid)],
        ['Fills', formatNumber(stats.filledTradesCount)],
        ['Open / Closed', `${formatNumber(stats.openFillsCount)} / ${formatNumber(stats.closedTradesCount)}`],
        ['Win Rate %', formatNumber(stats.winRate)],
        ['Profit Factor', formatNumber(stats.profitFactor)],
        ['Avg Win / Loss', `${formatPnl(stats.averageWin)} / ${formatPnl(stats.averageLoss)}`]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');
      byId('pnlStatsNote').textContent = 'Entry fills can show negative realized PnL because fees are booked immediately; win/loss stats use closed trades only.';
    };
    const renderRuntime = (data) => {
      const preflight = data.lastPreflightResult;
      const paperTestEntry = byId('paperTestEntry');
      const paperTestShortEntry = byId('paperTestShortEntry');
      const paperTestEnabled = data.tradingMode === 'Paper' && data.settings.enabled;
      paperTestEntry.disabled = !paperTestEnabled;
      paperTestEntry.title = data.tradingMode === 'Paper'
        ? (data.settings.enabled ? 'Open a minimal paper long through futures execution.' : 'Enable the futures profile first.')
        : 'Paper Test Entry is available only when TRADING_MODE=Paper.';
      paperTestShortEntry.disabled = !paperTestEnabled;
      paperTestShortEntry.title = data.tradingMode === 'Paper'
        ? (data.settings.enabled ? 'Open a minimal paper short through futures execution.' : 'Enable the futures profile first.')
        : 'Paper Short Entry is available only when TRADING_MODE=Paper.';
      const resetPaperStats = byId('resetPaperStats');
      resetPaperStats.disabled = data.tradingMode !== 'Paper';
      resetPaperStats.title = data.tradingMode === 'Paper'
        ? 'Reset paper account PnL and futures diagnostic history for the selected symbol.'
        : 'Reset Stats is available only when TRADING_MODE=Paper.';
      const runtime = data.runtimeControls || {};
      byId('resumeProfile').disabled = Boolean(runtime.envEmergencyPauseEnabled) || !Boolean(runtime.profilePaused);
      byId('emergencyPause').disabled = Boolean(runtime.profilePaused);
      byId('runtimeStatus').innerHTML = [
        `<span class="chip">${escapeHtml(data.tradingMode)}</span>`,
        `<span class="chip">${data.futuresEnabled ? 'Futures enabled' : 'Futures disabled'}</span>`,
        `<span class="chip">${data.settings.enabled ? 'Profile enabled' : 'Profile disabled'}</span>`,
        `<span class="chip">${runtime.envEmergencyPauseEnabled ? 'Env emergency pause' : 'Env pause off'}</span>`,
        `<span class="chip">${runtime.autoApplyRecommendationEnabled ? 'Auto apply on' : 'Auto apply off'}</span>`,
        `<span class="chip">${runtime.profilePaused ? 'Profile paused' : 'Profile running'}</span>`,
        `<span class="chip">${preflight ? escapeHtml(preflight.isAllowed ? 'Preflight ok' : 'Preflight blocked') : 'No preflight'}</span>`
      ].join('');
      byId('runtimeGuardStats').innerHTML = [
        ['Pause Reason', runtime.pauseReason || '-'],
        ['Auto Apply Recommendation', `${runtime.autoApplyRecommendationEnabled ? 'On' : 'Off'} / ${formatNumber(runtime.autoRecommendationMinApplyIntervalMinutes)}m`],
        ['Daily PnL / Max Loss', `${formatPnl(runtime.dailyRealizedPnl)} / ${formatNumber(runtime.maxDailyLossUsdt)} (${formatNumber(runtime.maxDailyLossEquityPercent)}%)`],
        ['Peak Equity', formatNumber(runtime.peakEquityUsdt)],
        ['Drawdown', `${formatPnl(-Math.abs(Number(runtime.currentDrawdownUsdt || 0)))} / ${formatPnl(-Math.abs(Number(runtime.currentDrawdownPercent || 0)))}%`],
        ['Max Drawdown %', formatNumber(runtime.maxDrawdownEquityPercent)],
        ['Open Positions', `${formatNumber(runtime.openPositionCount)} / ${formatNumber(runtime.maxOpenPositions)}`],
        ['Open Symbols', escapeHtml(runtime.openPositionSymbols || '-')],
        ['Stale Symbols', escapeHtml(runtime.stalePositionSymbols || '-')]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');
      if (preflight) {
        setControlStatus(
          preflight.isAllowed ? 'ok' : 'error',
          `${new Date(preflight.createdAt).toLocaleString()} - ${preflight.reason}`,
          data.settings.symbol);
      } else if ((controlStatusSymbol && controlStatusSymbol !== data.settings.symbol) || (!data.settings.enabled && controlStatusKind === 'error')) {
        clearControlStatus();
      }
    };
    const renderOrders = (orders) => {
      byId('activeOrderRows').innerHTML = orders.length === 0
        ? '<tr><td colspan="9">No active futures orders.</td></tr>'
        : orders.map(order => `
          <tr>
            <td>${escapeHtml(order.orderLinkId)}</td>
            <td>${escapeHtml(order.action)}</td>
            <td>${escapeHtml(order.side)}</td>
            <td>${formatNumber(order.quantity)}</td>
            <td>${formatNumber(order.filledQuantity)}</td>
            <td>${formatNumber(order.price)}</td>
            <td>${escapeHtml(order.status)}</td>
            <td>${order.reduceOnly ? 'yes' : 'no'}</td>
            <td>${formatDate(order.updatedAt)}</td>
          </tr>`).join('');
    };
    const renderRecentOrders = (orders) => {
      byId('recentOrderRows').innerHTML = orders.length === 0
        ? '<tr><td colspan="10">No futures order history yet.</td></tr>'
        : orders.map(order => {
            const isEntry = order.action === 'OpenLong' || order.action === 'OpenShort';
            const realized = isEntry
              ? `${formatPnl(order.realizedPnl)}<br><span class="subtle">entry fee</span>`
              : formatPnl(order.realizedPnl);
            return `
              <tr>
                <td>${formatDate(order.updatedAt)}</td>
                <td>${escapeHtml(order.orderLinkId)}</td>
                <td>${escapeHtml(order.action)}</td>
                <td>${escapeHtml(order.side)}</td>
                <td>${formatNumber(order.quantity)}</td>
                <td>${formatNumber(order.filledQuantity)}</td>
                <td>${formatNumber(order.averageFillPrice)}</td>
                <td>${escapeHtml(order.status)}</td>
                <td>${realized}</td>
                <td>${formatPnl(-Math.abs(Number(order.feePaid || 0)))}</td>
              </tr>`;
          }).join('');
    };
    const recentOrdersForHours = (hours) => {
      const cutoff = Date.now() - hours * 60 * 60 * 1000;
      return (latest?.recentOrders || []).filter(order => new Date(order.updatedAt).getTime() >= cutoff);
    };
    const buildDiagnosticsSnapshot = (hours) => ({
      schema: 'bybit-futures-bot-diagnostics/v1',
      generatedAt: new Date().toISOString(),
      windowHours: hours,
      tradingMode: latest?.tradingMode,
      futuresEnabled: latest?.futuresEnabled,
      settings: latest?.settings,
      paperAccount: latest?.paperAccount,
      pnlStats: latest?.pnlStats,
      testnetSoak: latest?.testnetSoak,
      runtimeControls: latest?.runtimeControls,
      userStreamStatus: latest?.userStreamStatus,
      protectionStatus: latest?.protectionStatus,
      aggressiveMode: latest?.aggressiveMode,
      strategyQuality: latest?.strategyQuality,
      position: latest?.position,
      activeOrders: latest?.activeOrders || [],
      recentOrders: recentOrdersForHours(hours),
      riskDecisions: latest?.riskDecisions || [],
      lastPreflightResult: latest?.lastPreflightResult,
      autoRecommendation: latest?.autoRecommendation,
      positionError: latest?.positionError,
      generatedByServerAt: latest?.generatedAt
    });
    const ordersToCsv = (orders) => {
      const rows = [
        ['Time', 'Symbol', 'Action', 'Side', 'Price', 'Qty', 'Filled', 'Avg Fill', 'Status', 'Realized PnL', 'Fee', 'Order']
      ];
      for (const order of orders) {
        rows.push([
          order.updatedAt,
          order.symbol,
          order.action,
          order.side,
          order.price,
          order.quantity,
          order.filledQuantity,
          order.averageFillPrice,
          order.status,
          order.realizedPnl,
          order.feePaid,
          order.orderLinkId
        ]);
      }
      return rows.map(row => row.map(value => `"${String(value ?? '').replaceAll('"', '""')}"`).join(',')).join('\n');
    };
    const writeClipboard = async (text) => {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
        return;
      }

      const textarea = document.createElement('textarea');
      textarea.value = text;
      textarea.style.position = 'fixed';
      textarea.style.left = '-9999px';
      document.body.appendChild(textarea);
      textarea.focus();
      textarea.select();
      try {
        if (!document.execCommand('copy')) {
          throw new Error('Browser refused clipboard copy.');
        }
      } finally {
        textarea.remove();
      }
    };
    const copyLastHistory = async () => {
      const hours = Number(byId('copyHistoryHours').value);
      const orders = recentOrdersForHours(Number.isFinite(hours) && hours > 0 ? hours : 1);
      await writeClipboard(ordersToCsv(orders));
      byId('copyStatus').className = 'status ok';
      byId('copyStatus').textContent = `Copied ${orders.length} futures order(s).`;
    };
    const copyDiagnostics = async () => {
      const hours = Number(byId('copyHistoryHours').value);
      const resolvedHours = Number.isFinite(hours) && hours > 0 ? hours : 1;
      const snapshot = buildDiagnosticsSnapshot(resolvedHours);
      await writeClipboard(JSON.stringify(snapshot, null, 2));
      byId('copyStatus').className = 'status ok';
      byId('copyStatus').textContent = `Copied diagnostics snapshot for ${latest?.settings?.symbol || 'current profile'}.`;
    };
    const renderRiskDecisions = (decisions) => {
      byId('riskDecisionRows').innerHTML = decisions.length === 0
        ? '<tr><td colspan="6">No futures risk decisions yet.</td></tr>'
        : decisions.map(decision => `
          <tr>
            <td>${formatDate(decision.createdAt)}</td>
            <td>${escapeHtml(decision.source)}</td>
            <td>${escapeHtml(decision.action || '-')}</td>
            <td>${decision.isAllowed ? 'yes' : 'no'}</td>
            <td>${escapeHtml(decision.severity)}</td>
            <td>${escapeHtml(decision.reason)}</td>
          </tr>`).join('');
    };
    const renderTestnetSoak = (soak) => {
      byId('testnetSoakStats').innerHTML = [
        ['Mode', soak.isTestnetMode ? 'Testnet' : latest?.tradingMode],
        ['Testnet Flag', soak.testnetEnabled ? 'enabled' : 'disabled'],
        ['User Stream', soak.userStreamEnabled ? 'enabled' : 'disabled'],
        ['Open Position', soak.hasOpenPosition ? 'yes' : 'no'],
        ['Active Orders', formatNumber(soak.activeOrderCount)],
        ['Recent Orders', formatNumber(soak.recentOrderCount)],
        ['Fills', formatNumber(soak.fillCount)],
        ['Risk Events', formatNumber(soak.riskDecisionCount)]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${escapeHtml(value)}</div></div>`).join('');
      byId('testnetSoakRisk').textContent = `${soak.lastRiskSource || '-'}: ${soak.lastRiskReason || '-'}`;
      const protection = latest?.protectionStatus || {};
      byId('protectionStats').innerHTML = [
        ['Protection', protection.status || '-'],
        ['Expected SL', formatNumber(protection.expectedStopLoss)],
        ['Expected TP', formatNumber(protection.expectedTakeProfit)],
        ['Current SL', formatNumber(protection.currentStopLoss)],
        ['Current TP', formatNumber(protection.currentTakeProfit)],
        ['Recommended SL', formatNumber(protection.recommendedStopLoss)],
        ['Recommended TP', formatNumber(protection.recommendedTakeProfit)],
        ['Last Check', protection.lastCheckedAt ? formatDate(protection.lastCheckedAt) : '-']
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${escapeHtml(value)}</div></div>`).join('');
      byId('protectionReason').textContent = `${protection.lastSource || '-'}: ${protection.lastReason || '-'} | Update: ${protection.lastUpdateReason || '-'}`;
      const stream = latest?.userStreamStatus || {};
      byId('userStreamStats').innerHTML = [
        ['WS Enabled', stream.enabled ? 'yes' : 'no'],
        ['WS Connected', stream.connected ? 'yes' : 'no'],
        ['WS Stale', stream.stale ? 'yes' : 'no'],
        ['REST Fallback', stream.fallbackActive ? 'active' : 'idle'],
        ['Fallback Reason', stream.fallbackReason || '-'],
        ['Connect Attempts', formatNumber(stream.connectAttemptCount)],
        ['Disconnects', formatNumber(stream.disconnectCount)],
        ['Last Connect Try', stream.lastConnectAttemptAt ? formatDate(stream.lastConnectAttemptAt) : '-'],
        ['Last Disconnect', stream.lastDisconnectedAt ? formatDate(stream.lastDisconnectedAt) : '-'],
        ['Last Event', stream.lastEventAt ? formatDate(stream.lastEventAt) : '-'],
        ['Event Type', stream.lastEventType || '-'],
        ['Topic', stream.lastTopic || '-'],
        ['Last Error', stream.lastError || '-']
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${escapeHtml(value)}</div></div>`).join('');
    };
    const renderAggressiveMode = (mode) => {
      byId('aggressiveModeStats').innerHTML = [
        ['Mode', mode.enabled ? 'aggressive' : 'conservative'],
        ['Kind', mode.modeKind || '-'],
        ['Effective', mode.effective ? 'yes' : 'no'],
        ['Entry Mult', formatNumber(mode.entryMultiplier)],
        ['Entries 1h', `${formatNumber(mode.entriesLastHour)} / ${formatNumber(mode.maxEntriesPerHour)}`],
        ['Min Gap Sec', formatNumber(mode.minSecondsBetweenEntries)],
        ['Loss Streak', `${formatNumber(mode.consecutiveLosses)} / ${formatNumber(mode.maxConsecutiveLosses)}`],
        ['Guard', mode.guardStatus || '-']
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${escapeHtml(value)}</div></div>`).join('');
      byId('aggressiveModeReason').textContent = `Block: ${mode.lastBlockReason || '-'} | No trade: ${mode.lastNoTradeReason || '-'}`;
      byId('aggressiveModeEnabled').value = String(Boolean(latest?.settings?.aggressiveModeEnabled));
      byId('aggressiveModeKind').value = latest?.settings?.aggressiveModeKind || 'normal';
      byId('aggressiveEntryMultiplier').value = latest?.settings?.aggressiveEntryMultiplier ?? 1.5;
      byId('aggressiveMaxOrdersPerHour').value = latest?.settings?.aggressiveMaxOrdersPerHour ?? 6;
      byId('aggressiveMinSecondsBetweenEntries').value = latest?.settings?.aggressiveMinSecondsBetweenEntries ?? 60;
      byId('aggressiveMaxConsecutiveLosses').value = latest?.settings?.aggressiveMaxConsecutiveLosses ?? 2;
    };
    const renderStrategyQuality = (quality) => {
      byId('strategyQualityStats').innerHTML = [
        ['Strategy', quality.strategyType || '-'],
        ['Direction', quality.direction || '-'],
        ['ATR Max %', formatNumber(quality.maxEntryAtrPercent)],
        ['BTC Risk-Off', quality.btcRiskOffEnabled ? `on (${formatNumber(quality.btcRiskOffMovePercent)}%)` : 'off'],
        ['Stop Cooldown', `${formatNumber(quality.stopLossCooldownMinutes)}m`],
        ['Position Capacity', quality.positionCapacityStatus || '-'],
        ['Remaining Notional', formatNumber(quality.remainingNotionalUsdt)],
        ['Next Order', formatNumber(quality.nextOrderNotionalUsdt)],
        ['Capital Used %', `${formatNumber(quality.capitalUtilizationPercent)}%`],
        ['Remaining Margin', formatNumber(quality.remainingMarginUsdt)],
        ['Entry Capacity', formatNumber(quality.entriesCapacityLeft)],
        ['Exit Stage', quality.exitStage || '-'],
        ['Current R', formatNumber(quality.currentR)],
        ['Next Exit', formatNumber(quality.nextExitPrice)],
        ['Fee / Trading PnL', `${formatNumber(quality.feeToTradingPnlPercent)}%`],
        ['Profit Efficiency', quality.profitEfficiencyStatus || '-'],
        ['No Trade', formatNumber(quality.noTradeReasonCount)],
        ['Filter Blocks', formatNumber(quality.strategyFilterBlockCount)],
        ['Risk Blocks', formatNumber(quality.riskBlockCount)],
        ['Current Block', quality.currentActiveBlockSource || '-']
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${escapeHtml(value)}</div></div>`).join('');
      byId('strategyQualityReason').textContent = `Current active block: ${quality.currentActiveBlockReason || '-'} | Last historical no-trade: ${quality.lastHistoricalNoTradeReason || quality.lastNoTradeReason || '-'}`;
    };
    const renderAutoRecommendation = (recommendation) => {
      const applyStatus = recommendation.canApply
        ? `Can apply. Compatible for current position: ${recommendation.compatibleStrategyForPosition || '-'}`
        : `Cannot apply: ${recommendation.applyBlockReason || '-'}`;
      byId('autoRecommendationReason').textContent = `${recommendation.reason || '-'} ${applyStatus}`;
      byId('autoRecommendationStats').innerHTML = [
        ['Strategy', escapeHtml(recommendation.strategyType)],
        ['Auto Apply', recommendation.autoApplyEnabled ? 'On' : 'Off'],
        ['Apply Gate', recommendation.canApply ? 'Allowed' : 'Blocked'],
        ['Compatible Strategy', escapeHtml(recommendation.compatibleStrategyForPosition || '-')],
        ['Leverage', `${formatNumber(recommendation.leverage)}x`],
        ['Max Notional', formatNumber(recommendation.maxNotionalUsdt)],
        ['Max Margin', formatNumber(recommendation.maxMarginUsdt)],
        ['Stop Loss %', formatNumber(recommendation.stopLossPercent)],
        ['Take Profit %', formatNumber(recommendation.takeProfitPercent)],
        ['ATR %', formatNumber(recommendation.atrPercent)],
        ['Move %', formatNumber(recommendation.movePercent)]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');
    };
    const load = async (force = false) => {
      document.querySelectorAll('.notice').forEach(item => item.remove());
      const url = selectedSymbol && !creating ? `/api/futures/dashboard?symbol=${encodeURIComponent(selectedSymbol)}` : '/api/futures/dashboard';
      const response = await fetch(url, { cache: 'no-store' });
      const data = await response.json();
      latest = data;
      if (!creating) {
        selectedSymbol = data.settings.symbol;
        setUrl();
      }
      renderTabs(data.profiles);
      renderConfigs(data.configSummaries || []);
      renderMarketTicker(data);
      renderPosition(data);
      renderPaperAccount(data.paperAccount || {}, data.pnlStats || {});
      renderRuntime(data);
      renderAggressiveMode(data.aggressiveMode || {});
      renderStrategyQuality(data.strategyQuality || {});
      renderOrders(data.activeOrders || []);
      renderRecentOrders(data.recentOrders || []);
      renderRiskDecisions(data.riskDecisions || []);
      renderTestnetSoak(data.testnetSoak || {});
      renderAutoRecommendation(data.autoRecommendation);
      byId('strategyActions').innerHTML = data.strategyActions.map(action => `<span class="chip">${escapeHtml(action)}</span>`).join('');
      if (force || !dirty) {
        updateForm(creating ? defaults : data.settings);
        dirty = creating;
      }
    };

    fields.forEach(id => byId(id).addEventListener('input', () => {
      dirty = true;
      if (id === 'stopLossPercent' || id === 'takeProfitPercent') {
        byId('rrPreset').value = resolveRrPreset(byId('stopLossPercent').value, byId('takeProfitPercent').value);
        updateStrategyRiskConfig();
      }
    }));
    byId('rrPreset').addEventListener('change', () => {
      const preset = rrPresets[byId('rrPreset').value];
      if (!preset) {
        return;
      }

      byId('stopLossPercent').value = preset.stopLossPercent;
      byId('takeProfitPercent').value = preset.takeProfitPercent;
      updateStrategyRiskConfig();
      dirty = true;
    });
    const saveAggressiveSettings = async () => {
      if (!latest?.settings) return;
      const payload = {
        ...latest.settings,
        aggressiveModeEnabled: byId('aggressiveModeEnabled').value === 'true',
        aggressiveModeKind: byId('aggressiveModeKind').value,
        aggressiveEntryMultiplier: Number(byId('aggressiveEntryMultiplier').value),
        aggressiveMaxOrdersPerHour: Number(byId('aggressiveMaxOrdersPerHour').value),
        aggressiveMinSecondsBetweenEntries: Number(byId('aggressiveMinSecondsBetweenEntries').value),
        aggressiveMaxConsecutiveLosses: Number(byId('aggressiveMaxConsecutiveLosses').value)
      };
      const response = await fetch('/api/futures/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), payload.symbol);
      if (response.ok) {
        dirty = false;
        await load(true);
      }
    };
    ['aggressiveModeEnabled','aggressiveModeKind','aggressiveEntryMultiplier','aggressiveMaxOrdersPerHour','aggressiveMinSecondsBetweenEntries','aggressiveMaxConsecutiveLosses']
      .forEach(id => byId(id).addEventListener('change', () => {
        saveAggressiveSettings().catch((error) => setControlStatus('error', error.message, latest?.settings?.symbol));
      }));
    byId('newProfile').addEventListener('click', () => {
      creating = true;
      selectedSymbol = null;
      updateForm(latest?.settings || defaults);
      dirty = true;
      setUrl();
      renderTabs(latest?.profiles || []);
      byId('formStatus').className = 'status';
      byId('formStatus').textContent = 'Draft config created.';
    });
    byId('refreshAutoRecommendation').addEventListener('click', async () => {
      const status = byId('formStatus');
      status.className = 'status';
      status.textContent = 'Refreshing futures auto recommendation...';
      await load(false);
      status.className = 'status ok';
      status.textContent = 'Futures auto recommendation refreshed.';
    });
    byId('applyAutoRecommendation').addEventListener('click', async () => {
      const status = byId('formStatus');
      const symbol = selectedSymbol || latest?.settings?.symbol;
      const url = symbol ? `/api/futures/settings/apply-auto?symbol=${encodeURIComponent(symbol)}` : '/api/futures/settings/apply-auto';
      const response = await fetch(url, { method: 'POST' });
      const result = await response.json();
      status.className = `status ${response.ok ? 'ok' : 'error'}`;
      status.textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message || 'Failed to apply futures auto recommendation.');
      if (response.ok) {
        selectedSymbol = (result.symbol || symbol || '').toUpperCase();
        creating = false;
        dirty = false;
        setUrl();
        await load(true);
      }
    });
    byId('runFuturesMarketScan').addEventListener('click', () => {
      runFuturesMarketScan().catch((error) => {
        byId('futuresMarketScanStatus').className = 'status error';
        byId('futuresMarketScanStatus').textContent = error.message;
      });
    });
    byId('futuresMarketScanRows').addEventListener('click', (event) => {
      const button = event.target.closest('[data-action="apply-futures-scan-profile"]');
      if (!button) {
        return;
      }

      applyFuturesScanConfig(button.dataset.symbol).catch((error) => {
        byId('futuresMarketScanStatus').className = 'status error';
        byId('futuresMarketScanStatus').textContent = error.message;
      });
    });
    byId('profileTabs').addEventListener('click', async (event) => {
      const deleteSymbol = event.target.dataset.delete;
      if (deleteSymbol) {
        event.stopPropagation();
        const response = await fetch(`/api/futures/settings/${encodeURIComponent(deleteSymbol)}`, { method: 'DELETE' });
        const result = await response.json();
        byId('formStatus').className = `status ${response.ok ? 'ok' : 'error'}`;
        byId('formStatus').textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message);
        selectedSymbol = null;
        creating = false;
        dirty = false;
        setUrl();
        await load(true);
        return;
      }
      const button = event.target.closest('button[data-symbol]');
      if (!button) return;
      selectedSymbol = button.dataset.symbol.toUpperCase();
      creating = false;
      dirty = false;
      setUrl();
      await load(true);
    });
    byId('configRows').addEventListener('click', async (event) => {
      const row = event.target.closest('tr[data-symbol]');
      if (!row) return;
      selectedSymbol = row.dataset.symbol.toUpperCase();
      creating = false;
      dirty = false;
      setUrl();
      await load(true);
    });
    byId('deleteProfile').addEventListener('click', async () => {
      const symbol = byId('symbol').value.trim().toUpperCase();
      if (!symbol) return;
      const response = await fetch(`/api/futures/settings/${encodeURIComponent(symbol)}`, { method: 'DELETE' });
      const result = await response.json();
      byId('formStatus').className = `status ${response.ok ? 'ok' : 'error'}`;
      byId('formStatus').textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message);
      selectedSymbol = null;
      creating = false;
      dirty = false;
      setUrl();
      await load(true);
    });
    byId('toggleProfile').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (!symbol) return;
      const enabled = !(latest?.settings?.enabled ?? true);
      const response = await fetch(`/api/futures/settings/${encodeURIComponent(symbol)}/enabled?enabled=${enabled}`, { method: 'POST' });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), symbol);
      await load(true);
    });
    byId('emergencyPause').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (!symbol) return;
      const response = await fetch(`/api/futures/settings/${encodeURIComponent(symbol)}/pause`, { method: 'POST' });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), symbol);
      await load(true);
    });
    byId('resumeProfile').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (!symbol) return;
      const response = await fetch(`/api/futures/settings/${encodeURIComponent(symbol)}/resume`, { method: 'POST' });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), symbol);
      await load(true);
    });
    byId('closePosition').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (!symbol) return;
      const response = await fetch(`/api/futures/position/${encodeURIComponent(symbol)}/close`, { method: 'POST' });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), symbol);
      await load(true);
    });
    byId('paperTestEntry').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (latest?.tradingMode !== 'Paper') return;
      if (!symbol) return;
      const response = await fetch(`/api/futures/position/${encodeURIComponent(symbol)}/paper-test-entry`, { method: 'POST' });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), symbol);
      await load(true);
    });
    byId('paperTestShortEntry').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (latest?.tradingMode !== 'Paper') return;
      if (!symbol) return;
      const response = await fetch(`/api/futures/position/${encodeURIComponent(symbol)}/paper-test-short-entry`, { method: 'POST' });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), symbol);
      await load(true);
    });
    byId('cancelFuturesOrders').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (!symbol) return;
      const response = await fetch(`/api/futures/orders/${encodeURIComponent(symbol)}/cancel-active`, { method: 'POST' });
      const result = await response.json();
      setControlStatus(response.ok ? 'ok' : 'error', response.ok ? result.message : (result.errors?.join(' | ') || result.message), symbol);
      await load(true);
    });
    byId('copyLastHistory').addEventListener('click', () => {
      copyLastHistory().catch((error) => {
        byId('copyStatus').className = 'status error';
        byId('copyStatus').textContent = error.message;
      });
    });
    byId('copyDiagnostics').addEventListener('click', () => {
      copyDiagnostics().catch((error) => {
        byId('copyStatus').className = 'status error';
        byId('copyStatus').textContent = error.message;
      });
    });
    byId('resetPaperStats').addEventListener('click', async () => {
      const symbol = selectedSymbol || latest?.settings?.symbol;
      if (latest?.tradingMode !== 'Paper' || !symbol) return;
      if (!window.confirm(`Reset paper simulation for ${symbol}? This clears paper position, orders, fills, risk decisions, PnL history, and restores initial equity for this symbol.`)) {
        return;
      }

      const response = await fetch(`/api/futures/stats/${encodeURIComponent(symbol)}/reset`, { method: 'POST' });
      const result = await response.json();
      byId('copyStatus').className = `status ${response.ok ? 'ok' : 'error'}`;
      byId('copyStatus').textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message);
      await load(true);
    });
    byId('settingsForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const payload = readPayload();
      const response = await fetch('/api/futures/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      const result = await response.json();
      byId('formStatus').className = `status ${response.ok ? 'ok' : 'error'}`;
      byId('formStatus').textContent = response.ok ? result.message : (result.errors?.join(' | ') || result.message);
      if (response.ok) {
        selectedSymbol = (result.symbol || payload.symbol).toUpperCase();
        creating = false;
        dirty = false;
        setUrl();
        await load(true);
      }
    });

    load(true).catch(error => {
      byId('formStatus').className = 'status error';
      byId('formStatus').textContent = error.message;
    });
    setInterval(() => load(false).catch(() => {}), 10000);
  </script>
</body>
</html>
""";

    private async Task<IReadOnlyList<FuturesConfigSummaryItem>> BuildConfigSummariesAsync(
        IReadOnlyCollection<FuturesBotSettings> profiles,
        string selectedSymbol,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var summaries = new List<FuturesConfigSummaryItem>();
        foreach (var profile in profiles)
        {
            var state = await _repository.GetBotStateAsync(FuturesStateKeys.ForSymbol(profile.Symbol), cancellationToken);
            var dailyRealizedPnl = state?.DailyRealizedPnl ?? 0m;
            var totalRealizedPnl = state?.TotalRealizedPnl ?? 0m;

            if (_appOptions.TradingMode != TradingMode.Paper)
            {
                var fills = await _repository.GetFuturesFillsAsync(profile.Symbol, FuturesFillLedger.QueryLimit, cancellationToken);
                var ledger = FuturesFillLedger.Build(fills, today);
                dailyRealizedPnl = ledger.DailyRealizedPnl + ledger.DailyFunding;
                totalRealizedPnl = ledger.TotalRealizedPnl + ledger.TotalFunding;
            }

            summaries.Add(new FuturesConfigSummaryItem
            {
                Symbol = profile.Symbol,
                Category = profile.Category,
                StrategyType = FormatEnum(profile.StrategyType),
                Direction = FormatEnum(profile.Direction),
                Enabled = profile.Enabled,
                Leverage = profile.Leverage,
                MaxNotionalUsdt = profile.MaxNotionalUsdt,
                MaxMarginUsdt = profile.MaxMarginUsdt,
                DailyRealizedPnl = dailyRealizedPnl,
                TotalRealizedPnl = totalRealizedPnl,
                IsSelected = string.Equals(profile.Symbol, selectedSymbol, StringComparison.OrdinalIgnoreCase),
                UpdatedAt = profile.UpdatedAt
            });
        }

        return summaries
            .OrderByDescending(summary => summary.TotalRealizedPnl)
            .ThenByDescending(summary => summary.DailyRealizedPnl)
            .ThenBy(summary => summary.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static FuturesSettingsView MapSettings(FuturesBotSettings settings) => new()
    {
        Enabled = settings.Enabled,
        Symbol = settings.Symbol,
        Category = settings.Category,
        StrategyType = FormatEnum(settings.StrategyType),
        StrategyConfigJson = settings.StrategyConfigJson,
        Leverage = settings.Leverage,
        MarginMode = FormatEnum(settings.MarginMode),
        PositionMode = FormatEnum(settings.PositionMode),
        Direction = FormatEnum(settings.Direction),
        MaxNotionalUsdt = settings.MaxNotionalUsdt,
        MaxMarginUsdt = settings.MaxMarginUsdt,
        StopLossPercent = settings.StopLossPercent,
        TakeProfitPercent = settings.TakeProfitPercent,
        LiquidationBufferPercent = settings.LiquidationBufferPercent,
        ReduceOnlyEnabled = settings.ReduceOnlyEnabled,
        AggressiveModeEnabled = settings.AggressiveModeEnabled,
        AggressiveModeKind = FormatEnum(settings.AggressiveModeKind),
        AggressiveEntryMultiplier = settings.AggressiveEntryMultiplier,
        AggressiveMaxOrdersPerHour = settings.AggressiveMaxOrdersPerHour,
        AggressiveMinSecondsBetweenEntries = settings.AggressiveMinSecondsBetweenEntries,
        AggressiveMaxConsecutiveLosses = settings.AggressiveMaxConsecutiveLosses
    };

    private static FuturesPositionView MapPosition(FuturesBotSettings settings, BybitPositionSnapshot position) => new()
    {
        Symbol = position.Symbol,
        Category = settings.Category,
        Side = position.Size <= 0m ? "None" : position.Side,
        Size = position.Size,
        EntryPrice = position.AveragePrice,
        MarkPrice = position.MarkPrice,
        LiquidationPrice = position.LiquidationPrice,
        PositionValueUsdt = position.PositionValue,
        MarginUsedUsdt = position.PositionInitialMargin,
        Leverage = position.Leverage,
        UnrealizedPnl = position.UnrealizedPnl,
        RealizedPnl = position.RealizedPnl,
        Funding = 0m,
        PositionIdx = position.PositionIdx,
        UpdatedAt = position.UpdatedAt
    };

    private static FuturesOrderView MapOrder(FuturesOrderRecord order) => new()
    {
        OrderLinkId = order.OrderLinkId,
        BybitOrderId = order.BybitOrderId,
        Symbol = order.Symbol,
        Action = order.Action.ToString(),
        Side = order.Side.ToString(),
        Price = order.Price,
        Quantity = order.Quantity,
        FilledQuantity = order.FilledQuantity,
        AverageFillPrice = order.AverageFillPrice,
        RealizedPnl = order.RealizedPnl,
        FeePaid = order.FeePaid,
        Status = order.Status.ToString(),
        ReduceOnly = order.ReduceOnly,
        PositionIdx = order.PositionIdx,
        CreatedAt = order.CreatedAt,
        UpdatedAt = order.UpdatedAt,
        FilledAt = order.FilledAt
    };

    private static FuturesPaperAccountView BuildPaperAccount(
        BotState? state,
        FuturesPositionView position,
        decimal initialEquity,
        FuturesFillLedger fillLedger,
        TradingMode tradingMode)
    {
        var cash = state?.QuoteAssetBalance ?? initialEquity;
        if (cash <= 0m)
        {
            cash = initialEquity;
        }

        var unrealized = state?.UnrealizedPnl ?? position.UnrealizedPnl;
        if (tradingMode != TradingMode.Paper)
        {
            unrealized = position.UnrealizedPnl;
            cash = initialEquity + fillLedger.TotalRealizedPnl + fillLedger.TotalFunding;
        }

        var currentEquity = cash + unrealized;
        var peakEquity = state is { PeakEquityUsdt: > 0m }
            ? state.PeakEquityUsdt
            : decimal.Max(initialEquity, currentEquity);
        var drawdownUsdt = state is { CurrentDrawdownUsdt: > 0m }
            ? state.CurrentDrawdownUsdt
            : decimal.Max(0m, peakEquity - currentEquity);
        var drawdownPercent = state is { CurrentDrawdownPercent: > 0m }
            ? state.CurrentDrawdownPercent
            : peakEquity > 0m ? drawdownUsdt / peakEquity * 100m : 0m;

        return new FuturesPaperAccountView
        {
            InitialEquityUsdt = initialEquity,
            CashUsdt = cash,
            CurrentEquityUsdt = currentEquity,
            PeakEquityUsdt = peakEquity,
            CurrentDrawdownUsdt = drawdownUsdt,
            CurrentDrawdownPercent = drawdownPercent,
            TotalRealizedPnl = tradingMode == TradingMode.Paper
                ? state?.TotalRealizedPnl ?? position.RealizedPnl
                : fillLedger.TotalRealizedPnl + fillLedger.TotalFunding,
            DailyRealizedPnl = tradingMode == TradingMode.Paper
                ? state?.DailyRealizedPnl ?? 0m
                : fillLedger.DailyRealizedPnl + fillLedger.DailyFunding,
            UnrealizedPnl = unrealized,
            ReturnPercent = initialEquity > 0m ? (currentEquity - initialEquity) / initialEquity * 100m : 0m
        };
    }

    private static FuturesPnlStatsView BuildPnlStats(
        IReadOnlyCollection<FuturesFillRecord> fills,
        FuturesFillLedger fillLedger)
    {
        var filled = fills
            .Where(fill => fill.Quantity > 0m)
            .Where(fill => fill.Action != FuturesTradeAction.Funding)
            .ToArray();
        var entryFills = filled
            .Where(fill => fill.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort)
            .ToArray();
        var closingFills = filled
            .Where(fill => fill.Action is FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose)
            .ToArray();
        var closedNetPnl = closingFills.Select(fill => fill.RealizedPnl).ToArray();
        var wins = closedNetPnl.Where(pnl => pnl > 0m).ToArray();
        var losses = closedNetPnl.Where(pnl => pnl < 0m).ToArray();
        var grossProfit = wins.Sum();
        var grossLoss = losses.Sum();
        var entryFeesPaid = entryFills.Sum(fill => fill.Fee);
        var exitFeesPaid = closingFills.Sum(fill => fill.Fee);
        var realizedTradingPnl = closingFills.Sum(fill => fill.RealizedPnl + fill.Fee - fill.Funding);
        var fundingPaid = fillLedger.TotalFunding;
        var netPnl = realizedTradingPnl - fillLedger.FeesPaid - fundingPaid;
        var feeToTradingPnlPercent = CalculateFeeToTradingPnlPercent(fillLedger.FeesPaid, realizedTradingPnl);

        return new FuturesPnlStatsView
        {
            GrossProfit = grossProfit,
            GrossLoss = grossLoss,
            NetPnl = netPnl,
            RealizedTradingPnl = realizedTradingPnl,
            FeesPaid = fillLedger.FeesPaid,
            FeeToTradingPnlPercent = feeToTradingPnlPercent,
            ProfitEfficiencyStatus = ResolveProfitEfficiencyStatus(feeToTradingPnlPercent, realizedTradingPnl, fillLedger.FeesPaid),
            EntryFeesPaid = entryFeesPaid,
            ExitFeesPaid = exitFeesPaid,
            FundingPaid = fundingPaid,
            FilledTradesCount = filled.Length,
            OpenFillsCount = entryFills.Length,
            ClosedTradesCount = closingFills.Length,
            WinningTradesCount = wins.Length,
            LosingTradesCount = losses.Length,
            WinRate = closingFills.Length == 0 ? 0m : (decimal)wins.Length / closingFills.Length * 100m,
            ProfitFactor = grossLoss == 0m ? (grossProfit > 0m ? grossProfit : 0m) : grossProfit / Math.Abs(grossLoss),
            AverageWin = wins.Length == 0 ? 0m : wins.Average(),
            AverageLoss = losses.Length == 0 ? 0m : losses.Average()
        };
    }

    private static FuturesProfitEfficiencyView BuildProfitEfficiency(IReadOnlyCollection<FuturesFillRecord> fills)
    {
        var filled = fills
            .Where(fill => fill.Quantity > 0m)
            .Where(fill => fill.Action != FuturesTradeAction.Funding)
            .ToArray();
        var closingFills = filled
            .Where(fill => fill.Action is FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose)
            .ToArray();
        var realizedTradingPnl = closingFills.Sum(fill => fill.RealizedPnl + fill.Fee - fill.Funding);
        var feesPaid = filled.Sum(fill => fill.Fee);
        var feeToTradingPnlPercent = CalculateFeeToTradingPnlPercent(feesPaid, realizedTradingPnl);

        return new FuturesProfitEfficiencyView(
            feeToTradingPnlPercent,
            ResolveProfitEfficiencyStatus(feeToTradingPnlPercent, realizedTradingPnl, feesPaid));
    }

    private static decimal CalculateFeeToTradingPnlPercent(decimal feesPaid, decimal realizedTradingPnl)
    {
        if (feesPaid <= 0m)
        {
            return 0m;
        }

        if (realizedTradingPnl <= 0m)
        {
            return 100m;
        }

        return decimal.Round(feesPaid / realizedTradingPnl * 100m, 4, MidpointRounding.AwayFromZero);
    }

    private static string ResolveProfitEfficiencyStatus(decimal feeToTradingPnlPercent, decimal realizedTradingPnl, decimal feesPaid)
    {
        if (feesPaid <= 0m)
        {
            return "good";
        }

        if (realizedTradingPnl <= 0m || feeToTradingPnlPercent > 25m)
        {
            return "bad";
        }

        return feeToTradingPnlPercent >= 20m ? "warning" : "good";
    }

    private readonly record struct FuturesProfitEfficiencyView(decimal FeeToTradingPnlPercent, string Status);

    private FuturesSoakStatusView BuildTestnetSoakStatus(
        FuturesPositionView position,
        IReadOnlyCollection<FuturesOrderRecord> activeOrders,
        IReadOnlyCollection<FuturesOrderRecord> recentOrders,
        IReadOnlyCollection<FuturesFillRecord> fills,
        IReadOnlyCollection<FuturesRiskDecisionRecord> riskDecisions)
    {
        var lastRisk = riskDecisions.OrderByDescending(decision => decision.CreatedAt).FirstOrDefault();
        return new FuturesSoakStatusView
        {
            IsTestnetMode = _appOptions.TradingMode == TradingMode.Testnet,
            TestnetEnabled = _futuresOptions.TestnetEnabled,
            UserStreamEnabled = _futuresOptions.UserStreamEnabled,
            HasOpenPosition = position.Size > 0m,
            ActiveOrderCount = activeOrders.Count,
            RecentOrderCount = recentOrders.Count,
            FillCount = fills.Count,
            RiskDecisionCount = riskDecisions.Count,
            LastRiskSource = lastRisk?.Source ?? "-",
            LastRiskReason = lastRisk?.Reason ?? "-"
        };
    }

    private static FuturesProtectionStatusView BuildProtectionStatus(
        FuturesBotSettings settings,
        FuturesAutoConfigRecommendation recommendation,
        FuturesPositionView position,
        BybitPositionSnapshot? bybitPosition,
        IReadOnlyCollection<FuturesRiskDecisionRecord> riskDecisions,
        bool isPaperMode)
    {
        var expectedStopLoss = ResolveExpectedStopLoss(settings, position);
        var expectedTakeProfit = ResolveExpectedTakeProfit(settings, position);
        var recommendedStopLoss = ResolveRecommendedStopLoss(recommendation, position);
        var recommendedTakeProfit = ResolveRecommendedTakeProfit(recommendation, position);
        var lastProtection = riskDecisions
            .Where(decision => IsProtectionDecision(decision.Source))
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        var lastProtectionUpdate = riskDecisions
            .Where(decision => IsProtectionUpdateDecision(decision.Source))
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        return new FuturesProtectionStatusView
        {
            HasOpenPosition = position.Size > 0m,
            ExpectedStopLoss = expectedStopLoss,
            ExpectedTakeProfit = expectedTakeProfit,
            CurrentStopLoss = bybitPosition?.StopLossPrice ?? (isPaperMode ? expectedStopLoss : 0m),
            CurrentTakeProfit = bybitPosition?.TakeProfitPrice ?? (isPaperMode ? expectedTakeProfit : 0m),
            RecommendedStopLoss = recommendedStopLoss,
            RecommendedTakeProfit = recommendedTakeProfit,
            Status = ResolveProtectionStatus(position, bybitPosition, expectedStopLoss, expectedTakeProfit, lastProtection),
            LastSource = lastProtection?.Source ?? "-",
            LastReason = lastProtection?.Reason ?? "-",
            LastUpdateReason = lastProtectionUpdate?.Reason ?? "-",
            LastCheckedAt = lastProtection?.CreatedAt
        };
    }

    private FuturesAggressiveModeView BuildAggressiveModeStatus(
        FuturesBotSettings settings,
        IReadOnlyCollection<FuturesFillRecord> fills,
        IReadOnlyCollection<FuturesRiskDecisionRecord> riskDecisions)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        var entriesLastHour = fills.Count(fill =>
            fill.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort &&
            fill.CreatedAt >= cutoff);
        var consecutiveLosses = CountConsecutiveLosingExits(fills);
        var lastBlock = riskDecisions
            .Where(decision => !decision.IsAllowed &&
                !IsNoTradeDecision(decision.Source))
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        var lastNoTrade = riskDecisions
            .Where(decision => IsNoTradeDecision(decision.Source))
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        var efficiency = BuildProfitEfficiency(fills);
        var configuredMaxOrdersPerHour = ResolveAggressiveMaxOrdersPerHour(settings);
        return new FuturesAggressiveModeView
        {
            Enabled = settings.AggressiveModeEnabled,
            Effective = settings.AggressiveModeEnabled && _appOptions.TradingMode == TradingMode.Paper,
            ModeKind = FormatEnum(settings.AggressiveModeKind),
            PaperOnly = true,
            EntryMultiplier = ResolveAggressiveEntryMultiplier(settings),
            EntriesLastHour = entriesLastHour,
            MaxEntriesPerHour = ApplyProfitEfficiencyMaxOrdersCap(configuredMaxOrdersPerHour, efficiency),
            MinSecondsBetweenEntries = ResolveAggressiveMinSecondsBetweenEntries(settings),
            ConsecutiveLosses = consecutiveLosses,
            MaxConsecutiveLosses = ResolveAggressiveMaxConsecutiveLosses(settings),
            GuardStatus = lastBlock is null ? "allowed" : "blocked",
            LastBlockReason = lastBlock?.Reason ?? "-",
            LastNoTradeReason = lastNoTrade?.Reason ?? "-"
        };
    }

    private static int CountConsecutiveLosingExits(IReadOnlyCollection<FuturesFillRecord> fills)
    {
        var count = 0;
        foreach (var fill in fills.OrderByDescending(fill => fill.CreatedAt))
        {
            if (fill.Action is not (FuturesTradeAction.CloseLong or FuturesTradeAction.CloseShort or FuturesTradeAction.ReduceOnlyClose))
            {
                continue;
            }

            if (fill.RealizedPnl >= 0m)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static int ApplyProfitEfficiencyMaxOrdersCap(
        int configuredMaxOrdersPerHour,
        FuturesProfitEfficiencyView efficiency)
    {
        if (configuredMaxOrdersPerHour <= 0 || efficiency.Status == "good")
        {
            return configuredMaxOrdersPerHour;
        }

        var cap = efficiency.Status == "bad" ? 2 : 3;
        return Math.Min(configuredMaxOrdersPerHour, cap);
    }

    private static bool IsNoTradeDecision(string source) =>
        string.Equals(source, "AggressiveNoTrade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "StrategyNoTrade", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "PositionStrategyGuard", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "AutoRecommendationSkipped", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectionDecision(string source) =>
        string.Equals(source, "ProtectionVerify", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "ProtectionRestore", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "ProtectionFailed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "AutoRecommendationProtectionUpdate", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtectionUpdateDecision(string source) =>
        string.Equals(source, "AutoRecommendationProtectionUpdate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source, "AutoRecommendationProtectionSkipped", StringComparison.OrdinalIgnoreCase);

    private static string ResolveProtectionStatus(
        FuturesPositionView position,
        BybitPositionSnapshot? bybitPosition,
        decimal expectedStopLoss,
        decimal expectedTakeProfit,
        FuturesRiskDecisionRecord? lastProtection)
    {
        if (position.Size <= 0m)
        {
            return "no-position";
        }

        if (lastProtection is not null &&
            string.Equals(lastProtection.Source, "ProtectionFailed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        if (lastProtection is not null &&
            (string.Equals(lastProtection.Source, "ProtectionVerify", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lastProtection.Source, "ProtectionRestore", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(lastProtection.Source, "AutoRecommendationProtectionUpdate", StringComparison.OrdinalIgnoreCase)) &&
            bybitPosition is null)
        {
            return "verified";
        }

        if (bybitPosition is null)
        {
            return "unknown";
        }

        return Matches(bybitPosition.StopLossPrice, expectedStopLoss) &&
            Matches(bybitPosition.TakeProfitPrice, expectedTakeProfit)
            ? "verified"
            : "restore-required";
    }

    private static decimal ResolveExpectedStopLoss(FuturesBotSettings settings, FuturesPositionView position)
    {
        if (position.Size <= 0m || position.EntryPrice <= 0m)
        {
            return 0m;
        }

        return IsShort(position.Side)
            ? position.EntryPrice * (1m + settings.StopLossPercent / 100m)
            : position.EntryPrice * (1m - settings.StopLossPercent / 100m);
    }

    private static decimal ResolveExpectedTakeProfit(FuturesBotSettings settings, FuturesPositionView position)
    {
        if (position.Size <= 0m || position.EntryPrice <= 0m)
        {
            return 0m;
        }

        return IsShort(position.Side)
            ? position.EntryPrice * (1m - settings.TakeProfitPercent / 100m)
            : position.EntryPrice * (1m + settings.TakeProfitPercent / 100m);
    }

    private static decimal ResolveRecommendedStopLoss(FuturesAutoConfigRecommendation recommendation, FuturesPositionView position)
    {
        if (position.Size <= 0m || position.EntryPrice <= 0m)
        {
            return 0m;
        }

        return IsShort(position.Side)
            ? position.EntryPrice * (1m + recommendation.StopLossPercent / 100m)
            : position.EntryPrice * (1m - recommendation.StopLossPercent / 100m);
    }

    private static decimal ResolveRecommendedTakeProfit(FuturesAutoConfigRecommendation recommendation, FuturesPositionView position)
    {
        if (position.Size <= 0m || position.EntryPrice <= 0m)
        {
            return 0m;
        }

        return IsShort(position.Side)
            ? position.EntryPrice * (1m - recommendation.TakeProfitPercent / 100m)
            : position.EntryPrice * (1m + recommendation.TakeProfitPercent / 100m);
    }

    private static bool TargetsMatch(FuturesProtectionTargets current, FuturesProtectionTargets recommended) =>
        current.StopLoss > 0m &&
        current.TakeProfit > 0m &&
        Matches(current.StopLoss, recommended.StopLoss) &&
        Matches(current.TakeProfit, recommended.TakeProfit);

    private static bool WouldWorsenStopRisk(string positionSide, decimal currentStopLoss, decimal recommendedStopLoss)
    {
        if (currentStopLoss <= 0m || recommendedStopLoss <= 0m)
        {
            return false;
        }

        return IsShort(positionSide)
            ? recommendedStopLoss > currentStopLoss
            : recommendedStopLoss < currentStopLoss;
    }

    private static bool Matches(decimal actual, decimal expected)
    {
        if (actual <= 0m || expected <= 0m)
        {
            return false;
        }

        return Math.Abs(actual - expected) <= decimal.Max(0.00000001m, expected * 0.000001m);
    }

    private FuturesUserStreamStatusView BuildUserStreamStatus()
    {
        var snapshot = _userStreamTelemetry.GetSnapshot();
        var enabled = _futuresOptions.Enabled &&
            _futuresOptions.UserStreamEnabled &&
            _appOptions.TradingMode != TradingMode.Paper;
        var lastEventAge = snapshot.LastEventAt is null
            ? (TimeSpan?)null
            : DateTimeOffset.UtcNow - snapshot.LastEventAt.Value;
        var stale = enabled && (snapshot.LastEventAt is null || lastEventAge > TimeSpan.FromMinutes(2));
        var fallbackActive = enabled && (!snapshot.IsConnected || stale);
        return new FuturesUserStreamStatusView
        {
            Enabled = enabled,
            Connected = snapshot.IsConnected,
            Stale = stale,
            FallbackActive = fallbackActive,
            FallbackReason = fallbackActive
                ? BuildUserStreamFallbackReason(snapshot, lastEventAge)
                : "-",
            ConnectedAt = snapshot.ConnectedAt,
            LastConnectAttemptAt = snapshot.LastConnectAttemptAt,
            LastDisconnectedAt = snapshot.LastDisconnectedAt,
            LastMessageAt = snapshot.LastMessageAt,
            LastEventAt = snapshot.LastEventAt,
            LastEventType = snapshot.LastEventType ?? "-",
            LastTopic = snapshot.LastTopic ?? "-",
            DisconnectCount = snapshot.DisconnectCount,
            ConnectAttemptCount = snapshot.ConnectAttemptCount,
            LastError = snapshot.LastError ?? "-"
        };
    }

    private FuturesStrategyQualityView BuildStrategyQuality(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        FuturesInstrumentRules? instrument,
        IReadOnlyCollection<FuturesRiskDecisionRecord> riskDecisions,
        IReadOnlyCollection<FuturesFillRecord> fills)
    {
        var noTradeDecisions = riskDecisions
            .Where(decision => IsNoTradeDecision(decision.Source))
            .ToArray();
        var filterBlocks = riskDecisions.Count(decision =>
            string.Equals(decision.Source, "StrategyFilter", StringComparison.OrdinalIgnoreCase));
        var riskBlocks = riskDecisions.Count(decision =>
            string.Equals(decision.Source, "Risk", StringComparison.OrdinalIgnoreCase) &&
            !decision.IsAllowed);
        var currentActiveBlock = riskDecisions
            .Where(IsCurrentActiveBlock)
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        var lastHistoricalNoTrade = noTradeDecisions
            .OrderByDescending(decision => decision.CreatedAt)
            .FirstOrDefault();
        var positionCapacity = BuildPositionCapacity(settings, position, instrument);
        var efficiency = BuildProfitEfficiency(fills);
        var exitLadder = BuildExitLadderStatus(settings, position, fills);

        return new FuturesStrategyQualityView
        {
            StrategyType = settings.StrategyType.ToString(),
            Direction = settings.Direction.ToString(),
            MaxEntryAtrPercent = _strategyQualityOptions.MaxEntryAtrPercent,
            BtcRiskOffEnabled = _strategyQualityOptions.BtcRiskOffEnabled,
            BtcRiskOffMovePercent = _strategyQualityOptions.BtcRiskOffMovePercent,
            StopLossCooldownMinutes = _strategyQualityOptions.StopLossCooldownMinutes,
            NoTradeReasonCount = noTradeDecisions.Length,
            StrategyFilterBlockCount = filterBlocks,
            RiskBlockCount = riskBlocks,
            PositionCapacityStatus = positionCapacity.Status,
            RemainingNotionalUsdt = positionCapacity.RemainingNotionalUsdt,
            NextOrderNotionalUsdt = positionCapacity.NextOrderNotionalUsdt,
            CapitalUtilizationPercent = positionCapacity.CapitalUtilizationPercent,
            RemainingMarginUsdt = positionCapacity.RemainingMarginUsdt,
            EntriesCapacityLeft = positionCapacity.EntriesCapacityLeft,
            ExitStage = exitLadder.Stage,
            CurrentR = exitLadder.CurrentR,
            NextExitPrice = exitLadder.NextExitPrice,
            CurrentActiveBlockReason = currentActiveBlock?.Reason ?? "-",
            CurrentActiveBlockSource = currentActiveBlock?.Source ?? "-",
            CurrentActiveBlockAt = currentActiveBlock?.CreatedAt,
            LastNoTradeReason = lastHistoricalNoTrade?.Reason ?? "-",
            LastHistoricalNoTradeReason = lastHistoricalNoTrade?.Reason ?? "-",
            FeeToTradingPnlPercent = efficiency.FeeToTradingPnlPercent,
            ProfitEfficiencyStatus = efficiency.Status
        };
    }


    private static bool IsCurrentActiveBlock(FuturesRiskDecisionRecord decision) =>
        !decision.IsAllowed &&
        DateTimeOffset.UtcNow - decision.CreatedAt <= TimeSpan.FromMinutes(2) &&
        (string.Equals(decision.Source, "StrategyFilter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.Source, "Risk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.Source, "AggressiveNoTrade", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.Source, "AutoRecommendationSkipped", StringComparison.OrdinalIgnoreCase));

    private readonly record struct FuturesPositionCapacityView(
        string Status,
        decimal RemainingNotionalUsdt,
        decimal NextOrderNotionalUsdt,
        decimal CapitalUtilizationPercent,
        decimal RemainingMarginUsdt,
        int EntriesCapacityLeft);

    private readonly record struct FuturesExitLadderView(
        string Stage,
        decimal CurrentR,
        decimal NextExitPrice);

    private static FuturesExitLadderView BuildExitLadderStatus(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        IReadOnlyCollection<FuturesFillRecord> fills)
    {
        if (position.Size <= 0m || position.EntryPrice <= 0m || position.MarkPrice <= 0m || settings.StopLossPercent <= 0m)
        {
            return new FuturesExitLadderView("no-position", 0m, 0m);
        }

        var isShort = string.Equals(position.Side, "Sell", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(position.Side, "Short", StringComparison.OrdinalIgnoreCase);
        var profitPercent = isShort
            ? (position.EntryPrice - position.MarkPrice) / position.EntryPrice * 100m
            : (position.MarkPrice - position.EntryPrice) / position.EntryPrice * 100m;
        var currentR = profitPercent / settings.StopLossPercent;
        var hasPartial = HasCloseAfterLastEntry(
            fills,
            isShort ? FuturesTradeAction.OpenShort : FuturesTradeAction.OpenLong,
            isShort ? FuturesTradeAction.CloseShort : FuturesTradeAction.CloseLong);
        var finalR = settings.TakeProfitPercent > 0m
            ? settings.TakeProfitPercent / settings.StopLossPercent
            : 3m;
        var stage = currentR >= finalR
            ? "final-tp"
            : hasPartial
                ? "trailing"
                : currentR >= 1m
                    ? "partial-taken"
                    : "waiting-1r";
        var nextExitPrice = stage switch
        {
            "waiting-1r" or "partial-taken" => CalculateRPrice(position.EntryPrice, settings.StopLossPercent, 1m, isShort),
            "trailing" => CalculateTrailingExitPrice(position.MarkPrice, position.EntryPrice, settings.StopLossPercent, isShort),
            _ => CalculateRPrice(position.EntryPrice, settings.StopLossPercent, finalR, isShort)
        };

        return new FuturesExitLadderView(
            stage,
            decimal.Round(currentR, 4, MidpointRounding.AwayFromZero),
            decimal.Round(nextExitPrice, 8, MidpointRounding.AwayFromZero));
    }

    private static decimal CalculateRPrice(decimal entryPrice, decimal stopLossPercent, decimal r, bool isShort)
    {
        var move = entryPrice * stopLossPercent / 100m * r;
        return isShort ? entryPrice - move : entryPrice + move;
    }

    private static decimal CalculateTrailingExitPrice(decimal markPrice, decimal entryPrice, decimal stopLossPercent, bool isShort)
    {
        var riskDistance = entryPrice * stopLossPercent / 100m;
        var raw = isShort ? markPrice + riskDistance : markPrice - riskDistance;
        return isShort ? decimal.Min(entryPrice, raw) : decimal.Max(entryPrice, raw);
    }

    private static bool HasCloseAfterLastEntry(
        IReadOnlyCollection<FuturesFillRecord> fills,
        FuturesTradeAction entryAction,
        FuturesTradeAction closeAction)
    {
        var lastEntry = fills
            .Where(fill => fill.Action == entryAction)
            .OrderByDescending(fill => fill.CreatedAt)
            .FirstOrDefault();
        if (lastEntry is null)
        {
            return false;
        }

        return fills.Any(fill =>
            fill.CreatedAt > lastEntry.CreatedAt &&
            (fill.Action == closeAction || fill.Action == FuturesTradeAction.ReduceOnlyClose));
    }

    private async Task<FuturesInstrumentRules?> TryGetInstrumentRulesAsync(
        FuturesBotSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var instrument = await _bybitRestClient.GetInstrumentInfoAsync(settings.Category, settings.Symbol, cancellationToken);
            return MapInstrumentRules(instrument);
        }
        catch
        {
            return null;
        }
    }

    private static FuturesPositionCapacityView BuildPositionCapacity(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        FuturesInstrumentRules? instrument)
    {
        var maxNotional = decimal.Max(0m, settings.MaxNotionalUsdt);
        var currentNotional = decimal.Max(0m, position.PositionValueUsdt);
        var remainingNotional = maxNotional > 0m ? decimal.Max(0m, maxNotional - currentNotional) : 0m;
        var maxMargin = decimal.Max(0m, settings.MaxMarginUsdt);
        var usedMargin = decimal.Max(0m, position.MarginUsedUsdt);
        var remainingMargin = maxMargin > 0m ? decimal.Max(0m, maxMargin - usedMargin) : 0m;
        var nextOrderNotional = EstimateNextOrderNotional(settings, position, instrument);
        var nextOrderMargin = settings.Leverage > 0m ? nextOrderNotional / settings.Leverage : nextOrderNotional;
        var entriesByNotional = nextOrderNotional > 0m ? decimal.Floor(remainingNotional / nextOrderNotional) : 0m;
        var entriesByMargin = nextOrderMargin > 0m ? decimal.Floor(remainingMargin / nextOrderMargin) : 0m;
        var entriesCapacityLeft = (int)decimal.Max(0m, decimal.Min(entriesByNotional, entriesByMargin));
        var utilizationPercent = maxNotional > 0m ? currentNotional / maxNotional * 100m : 0m;
        var status = position.Size <= 0m
            ? "no-position"
            : entriesCapacityLeft > 0
                ? "can-scale-in"
                : "full";

        return new FuturesPositionCapacityView(
            status,
            decimal.Round(remainingNotional, 4, MidpointRounding.AwayFromZero),
            decimal.Round(nextOrderNotional, 4, MidpointRounding.AwayFromZero),
            decimal.Round(utilizationPercent, 4, MidpointRounding.AwayFromZero),
            decimal.Round(remainingMargin, 4, MidpointRounding.AwayFromZero),
            entriesCapacityLeft);
    }

    private static decimal EstimateNextOrderNotional(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        FuturesInstrumentRules? instrument)
    {
        var configuredNotional = ResolveConfiguredEntryNotional(settings);
        var price = position.MarkPrice > 0m
            ? position.MarkPrice
            : position.EntryPrice > 0m
                ? position.EntryPrice
                : 0m;
        if (instrument is null || price <= 0m)
        {
            return configuredNotional;
        }

        var requestedQuantity = configuredNotional / price;
        var minQuantity = instrument.MinOrderQty;
        if (instrument.MinOrderAmount > 0m)
        {
            minQuantity = decimal.Max(minQuantity, instrument.MinOrderAmount / price);
        }

        var quantity = decimal.Max(requestedQuantity, minQuantity);
        var step = instrument.QtyStep > 0m ? instrument.QtyStep : instrument.BasePrecision;
        if (step > 0m)
        {
            quantity = Math.Ceiling(quantity / step) * step;
        }

        return price * quantity;
    }

    private static decimal ResolveConfiguredEntryNotional(FuturesBotSettings settings)
    {
        var fallbackMultiplier = settings.AggressiveModeEnabled
            ? 0.25m * decimal.Max(0.01m, settings.AggressiveEntryMultiplier)
            : 0.25m;
        var fallback = settings.MaxNotionalUsdt * fallbackMultiplier;
        var configured = fallback;
        if (!string.IsNullOrWhiteSpace(settings.StrategyConfigJson))
        {
            try
            {
                using var document = JsonDocument.Parse(settings.StrategyConfigJson);
                if (document.RootElement.TryGetProperty("entryNotionalUsdt", out var property) &&
                    property.ValueKind == JsonValueKind.Number &&
                    property.TryGetDecimal(out var value) &&
                    value > 0m)
                {
                    configured = value;
                }
            }
            catch (JsonException)
            {
                configured = fallback;
            }
        }

        var multiplier = settings.AggressiveModeEnabled
            ? decimal.Max(0.01m, settings.AggressiveEntryMultiplier)
            : 1m;
        return decimal.Min(settings.MaxNotionalUsdt, configured * multiplier);
    }

    private async Task<FuturesRuntimeControlsView> BuildRuntimeControlsAsync(
        IReadOnlyCollection<FuturesBotSettings> profiles,
        BotState? state,
        CancellationToken cancellationToken)
    {
        var openSymbols = new List<string>();
        var staleSymbols = new List<string>();
        foreach (var profile in profiles.Where(static profile => profile.Enabled))
        {
            var position = await ResolveOpenPositionDiagnosticsAsync(profile, cancellationToken);
            if (position.IsOpen)
            {
                openSymbols.Add(position.OpenSymbol);
            }

            if (!string.IsNullOrWhiteSpace(position.StaleSymbol))
            {
                staleSymbols.Add(position.StaleSymbol);
            }
        }

        return new FuturesRuntimeControlsView
        {
            EnvEmergencyPauseEnabled = _riskOptions.EmergencyPause,
            AutoApplyRecommendationEnabled = _futuresOptions.AutoApplyRecommendation,
            AutoRecommendationMinApplyIntervalMinutes = _futuresOptions.AutoRecommendationMinApplyIntervalMinutes,
            ProfilePaused = state?.IsPaused ?? false,
            PauseReason = state?.PauseReason ?? "-",
            DailyRealizedPnl = state?.DailyRealizedPnl ?? 0m,
            MaxDailyLossUsdt = _riskOptions.MaxDailyLossUsdt,
            MaxDailyLossEquityPercent = _riskOptions.MaxDailyLossEquityPercent,
            PeakEquityUsdt = state?.PeakEquityUsdt ?? 0m,
            CurrentDrawdownUsdt = state?.CurrentDrawdownUsdt ?? 0m,
            CurrentDrawdownPercent = state?.CurrentDrawdownPercent ?? 0m,
            MaxDrawdownEquityPercent = _riskOptions.MaxDrawdownEquityPercent,
            OpenPositionCount = openSymbols.Count,
            OpenPositionSymbols = openSymbols.Count == 0 ? "-" : string.Join(", ", openSymbols),
            StalePositionSymbols = staleSymbols.Count == 0 ? "-" : string.Join(", ", staleSymbols),
            MaxOpenPositions = _riskOptions.MaxOpenPositions
        };
    }

    private async Task<FuturesOpenPositionDiagnostics> ResolveOpenPositionDiagnosticsAsync(
        FuturesBotSettings profile,
        CancellationToken cancellationToken)
    {
        var storedPosition = await _repository.GetFuturesPositionAsync(profile.Symbol, cancellationToken);
        var storedOpen = storedPosition?.Size > 0m;
        if (_appOptions.TradingMode != TradingMode.Paper)
        {
            return storedOpen
                ? new FuturesOpenPositionDiagnostics(true, FormatOpenPositionSymbol(storedPosition!), string.Empty)
                : new FuturesOpenPositionDiagnostics(false, string.Empty, string.Empty);
        }

        var state = await _repository.GetBotStateAsync(FuturesStateKeys.ForSymbol(profile.Symbol), cancellationToken);
        var stateOpen = state?.BaseAssetQuantity > 0m;
        if (stateOpen)
        {
            var side = state!.PositionSide ?? storedPosition?.Side ?? "Buy";
            return new FuturesOpenPositionDiagnostics(true, $"{profile.Symbol}:{side}:{state.BaseAssetQuantity}", string.Empty);
        }

        if (storedOpen && state is not null)
        {
            return new FuturesOpenPositionDiagnostics(
                false,
                string.Empty,
                $"{profile.Symbol}:{storedPosition!.Side}:{storedPosition.Size} stored, state flat");
        }

        return storedOpen
            ? new FuturesOpenPositionDiagnostics(true, FormatOpenPositionSymbol(storedPosition!), string.Empty)
            : new FuturesOpenPositionDiagnostics(false, string.Empty, string.Empty);
    }

    private static string FormatOpenPositionSymbol(FuturesPositionSnapshot position) =>
        $"{position.Symbol}:{position.Side}:{position.Size}";

    private sealed record FuturesOpenPositionDiagnostics(
        bool IsOpen,
        string OpenSymbol,
        string StaleSymbol);

    private static string BuildUserStreamFallbackReason(
        BybitUserStreamTelemetrySnapshot snapshot,
        TimeSpan? lastEventAge)
    {
        if (!snapshot.IsConnected)
        {
            return "disconnected";
        }

        if (snapshot.LastEventAt is null)
        {
            return "no events";
        }

        return $"stale {Math.Round(lastEventAge?.TotalSeconds ?? 0d)}s";
    }

    private static FuturesRiskDecisionView MapRiskDecision(FuturesRiskDecisionRecord decision) => new()
    {
        Source = decision.Source,
        OrderLinkId = decision.OrderLinkId,
        Action = decision.Action?.ToString(),
        IsAllowed = decision.IsAllowed,
        Reason = decision.Reason,
        Severity = decision.Severity,
        SuggestedAction = decision.SuggestedAction,
        CreatedAt = decision.CreatedAt
    };

    private async Task<FuturesPositionView?> GetPaperPositionAsync(
        FuturesBotSettings settings,
        CancellationToken cancellationToken)
    {
        var futuresPosition = await _repository.GetFuturesPositionAsync(settings.Symbol, cancellationToken);
        if (futuresPosition is not null)
        {
            return MapPosition(settings, futuresPosition);
        }

        var state = await _repository.GetBotStateAsync(FuturesStateKeys.ForSymbol(settings.Symbol), cancellationToken);
        return state is null ? null : MapPosition(settings, state);
    }

    private async Task<BotState> EnsureFuturesStateAsync(
        FuturesBotSettings settings,
        decimal currentPrice,
        CancellationToken cancellationToken)
    {
        var stateKey = FuturesStateKeys.ForSymbol(settings.Symbol);
        var state = await _repository.GetBotStateAsync(stateKey, cancellationToken);
        if (state is not null)
        {
            state.TradingMode = _appOptions.TradingMode;
            state.LastObservedPrice = currentPrice;
            if (_appOptions.TradingMode == TradingMode.Paper && state.QuoteAssetBalance <= 0m)
            {
                state.QuoteAssetBalance = _futuresOptions.PaperInitialEquityUsdt + state.TotalRealizedPnl;
                state.PeakEquityUsdt = decimal.Max(state.PeakEquityUsdt, state.QuoteAssetBalance + state.UnrealizedPnl);
            }

            return state;
        }

        state = new BotState
        {
            Symbol = stateKey,
            TradingMode = _appOptions.TradingMode,
            LastObservedPrice = currentPrice,
            PositionSide = "None",
            Leverage = settings.Leverage,
            MarginMode = settings.MarginMode.ToString(),
            QuoteAssetBalance = _appOptions.TradingMode == TradingMode.Paper ? _futuresOptions.PaperInitialEquityUsdt : 0m,
            PeakEquityUsdt = _appOptions.TradingMode == TradingMode.Paper ? _futuresOptions.PaperInitialEquityUsdt : 0m,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private static FuturesPositionView MapPosition(FuturesBotSettings settings, FuturesPositionSnapshot position) => new()
    {
        Symbol = settings.Symbol,
        Category = settings.Category,
        Side = position.Size <= 0m ? "None" : position.Side,
        Size = position.Size,
        EntryPrice = position.EntryPrice,
        MarkPrice = position.MarkPrice,
        LiquidationPrice = position.LiquidationPrice,
        PositionValueUsdt = position.PositionValueUsdt,
        MarginUsedUsdt = position.MarginUsedUsdt,
        Leverage = position.Leverage,
        UnrealizedPnl = position.UnrealizedPnl,
        RealizedPnl = position.RealizedPnl,
        Funding = position.Funding,
        PositionIdx = position.PositionIdx,
        UpdatedAt = position.UpdatedAt
    };

    private static FuturesPositionView MapPosition(FuturesBotSettings settings, BotState state)
    {
        var markPrice = state.MarkPrice > 0m ? state.MarkPrice : state.LastObservedPrice ?? 0m;
        var positionValue = state.BaseAssetQuantity * markPrice;
        var marginUsed = state.Leverage > 0m ? positionValue / state.Leverage : 0m;
        return new FuturesPositionView
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Side = state.BaseAssetQuantity <= 0m ? "None" : state.PositionSide ?? "Buy",
            Size = state.BaseAssetQuantity,
            EntryPrice = state.EntryPrice > 0m ? state.EntryPrice : state.AverageEntryPrice,
            MarkPrice = markPrice,
            LiquidationPrice = state.LiquidationPrice,
            PositionValueUsdt = positionValue,
            MarginUsedUsdt = marginUsed,
            Leverage = state.Leverage,
            UnrealizedPnl = state.UnrealizedPnl,
            RealizedPnl = state.TotalRealizedPnl,
            Funding = 0m,
            PositionIdx = state.PositionIdx,
            UpdatedAt = state.UpdatedAt
        };
    }

    private static FuturesPositionView BuildEmptyPosition(FuturesBotSettings settings) => new()
    {
        Symbol = settings.Symbol,
        Category = settings.Category,
        Side = "None",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private async Task<FuturesPositionSnapshot> ResolvePositionSnapshotAsync(
        FuturesBotSettings settings,
        CancellationToken cancellationToken)
    {
        if (_appOptions.TradingMode == TradingMode.Paper)
        {
            return await _repository.GetFuturesPositionAsync(settings.Symbol, cancellationToken)
                ?? MapStateToSnapshot(settings, await _repository.GetBotStateAsync(FuturesStateKeys.ForSymbol(settings.Symbol), cancellationToken));
        }

        var bybitPosition = await _bybitRestClient.GetPositionAsync(settings.Category, settings.Symbol, cancellationToken);
        return bybitPosition is null
            ? new FuturesPositionSnapshot { Symbol = settings.Symbol, Category = settings.Category }
            : new FuturesPositionSnapshot
            {
                Symbol = settings.Symbol,
                Category = settings.Category,
                Side = bybitPosition.Size > 0m ? bybitPosition.Side : "None",
                Size = bybitPosition.Size,
                EntryPrice = bybitPosition.AveragePrice,
                MarkPrice = bybitPosition.MarkPrice,
                LiquidationPrice = bybitPosition.LiquidationPrice,
                PositionValueUsdt = bybitPosition.PositionValue,
                MarginUsedUsdt = bybitPosition.PositionInitialMargin,
                Leverage = bybitPosition.Leverage,
                UnrealizedPnl = bybitPosition.UnrealizedPnl,
                RealizedPnl = bybitPosition.RealizedPnl,
                PositionIdx = bybitPosition.PositionIdx,
                UpdatedAt = bybitPosition.UpdatedAt
            };
    }

    private async Task<FuturesPositionSnapshot> TryResolvePositionSnapshotAsync(
        FuturesBotSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolvePositionSnapshotAsync(settings, cancellationToken);
        }
        catch
        {
            return new FuturesPositionSnapshot
            {
                Symbol = settings.Symbol,
                Category = settings.Category,
                Side = "None",
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    private async Task<BotState> EnsurePaperStateAsync(
        FuturesBotSettings settings,
        decimal markPrice,
        CancellationToken cancellationToken)
    {
        var stateKey = FuturesStateKeys.ForSymbol(settings.Symbol);
        var state = await _repository.GetBotStateAsync(stateKey, cancellationToken);
        if (state is not null)
        {
            state.TradingMode = _appOptions.TradingMode;
            state.LastObservedPrice = markPrice;
            if (state.QuoteAssetBalance <= 0m)
            {
                state.QuoteAssetBalance = _futuresOptions.PaperInitialEquityUsdt + state.TotalRealizedPnl;
            }

            if (state.PeakEquityUsdt <= 0m)
            {
                state.PeakEquityUsdt = decimal.Max(_futuresOptions.PaperInitialEquityUsdt, state.QuoteAssetBalance + state.UnrealizedPnl);
            }

            return state;
        }

        state = new BotState
        {
            Symbol = stateKey,
            TradingMode = _appOptions.TradingMode,
            LastObservedPrice = markPrice,
            PositionSide = "None",
            Leverage = settings.Leverage,
            MarginMode = settings.MarginMode.ToString(),
            QuoteAssetBalance = _futuresOptions.PaperInitialEquityUsdt,
            PeakEquityUsdt = _futuresOptions.PaperInitialEquityUsdt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repository.SaveBotStateAsync(state, cancellationToken);
        return state;
    }

    private async Task<int> CountOpenFuturesPositionsAsync(CancellationToken cancellationToken)
    {
        var profiles = await _repository.GetFuturesSettingsProfilesAsync(cancellationToken);
        var count = 0;
        foreach (var profile in profiles.Where(static profile => profile.Enabled))
        {
            var position = await ResolveOpenPositionDiagnosticsAsync(profile, cancellationToken);
            if (position.IsOpen)
            {
                count++;
            }
        }

        return count;
    }

    private async Task<string?> GetAggressiveEntryBlockReasonAsync(
        FuturesBotSettings settings,
        FuturesPositionSnapshot position,
        CancellationToken cancellationToken)
    {
        if (!settings.AggressiveModeEnabled)
        {
            return position.Size > 0m ? "Futures scale-in requires aggressive mode." : null;
        }

        if (position.Size > 0m && _appOptions.TradingMode != TradingMode.Paper)
        {
            return "Futures aggressive scale-in is paper-only until testnet soak validates the lifecycle.";
        }

        var fills = await _repository.GetFuturesFillsAsync(settings.Symbol, 1000, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var maxOrdersPerHour = ResolveAggressiveMaxOrdersPerHour(settings);
        if (maxOrdersPerHour > 0)
        {
            var cutoff = now.AddHours(-1);
            var entriesLastHour = fills.Count(fill =>
                fill.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort &&
                fill.CreatedAt >= cutoff);
            if (entriesLastHour >= maxOrdersPerHour)
            {
                return "FUTURES_AGGRESSIVE_MAX_ORDERS_PER_HOUR limit reached.";
            }
        }

        var minSecondsBetweenEntries = ResolveAggressiveMinSecondsBetweenEntries(settings);
        if (minSecondsBetweenEntries > 0)
        {
            var lastEntry = fills
                .Where(fill => fill.Action is FuturesTradeAction.OpenLong or FuturesTradeAction.OpenShort)
                .OrderByDescending(fill => fill.CreatedAt)
                .FirstOrDefault();
            if (lastEntry is not null &&
                now - lastEntry.CreatedAt < TimeSpan.FromSeconds(minSecondsBetweenEntries))
            {
                return "FUTURES_AGGRESSIVE_MIN_SECONDS_BETWEEN_ENTRIES is active.";
            }
        }

        var maxConsecutiveLosses = ResolveAggressiveMaxConsecutiveLosses(settings);
        if (maxConsecutiveLosses > 0 &&
            CountConsecutiveLosingExits(fills) >= maxConsecutiveLosses)
        {
            return "FUTURES_AGGRESSIVE_MAX_CONSECUTIVE_LOSSES limit reached.";
        }

        return null;
    }

    private FuturesRiskOptions ResolveProfileRiskOptions(FuturesBotSettings settings) => new()
    {
        MaxNotionalUsdt = MinPositive(_riskOptions.MaxNotionalUsdt, settings.MaxNotionalUsdt),
        MaxMarginUsdt = MinPositive(_riskOptions.MaxMarginUsdt, settings.MaxMarginUsdt),
        MaxLeverage = _riskOptions.MaxLeverage,
        MinLiquidationBufferPercent = _riskOptions.MinLiquidationBufferPercent,
        MaxFundingCostUsdt = _riskOptions.MaxFundingCostUsdt,
        MaxDailyLossUsdt = _riskOptions.MaxDailyLossUsdt,
        MaxDailyLossEquityPercent = _riskOptions.MaxDailyLossEquityPercent,
        MaxDrawdownEquityPercent = _riskOptions.MaxDrawdownEquityPercent,
        MaxOpenPositions = _riskOptions.MaxOpenPositions,
        AggressiveMaxOrdersPerHour = ResolveAggressiveMaxOrdersPerHour(settings),
        AggressiveMinSecondsBetweenEntries = ResolveAggressiveMinSecondsBetweenEntries(settings),
        AggressiveMaxConsecutiveLosses = ResolveAggressiveMaxConsecutiveLosses(settings),
        EmergencyPause = _riskOptions.EmergencyPause,
        StopLossRequired = _riskOptions.StopLossRequired
    };

    private decimal ResolveAggressiveEntryMultiplier(FuturesBotSettings settings) =>
        settings.AggressiveEntryMultiplier > 0m ? settings.AggressiveEntryMultiplier : _futuresOptions.AggressiveEntryMultiplier;

    private int ResolveAggressiveMaxOrdersPerHour(FuturesBotSettings settings) =>
        settings.AggressiveMaxOrdersPerHour >= 0 ? settings.AggressiveMaxOrdersPerHour : _riskOptions.AggressiveMaxOrdersPerHour;

    private int ResolveAggressiveMinSecondsBetweenEntries(FuturesBotSettings settings) =>
        settings.AggressiveMinSecondsBetweenEntries >= 0 ? settings.AggressiveMinSecondsBetweenEntries : _riskOptions.AggressiveMinSecondsBetweenEntries;

    private int ResolveAggressiveMaxConsecutiveLosses(FuturesBotSettings settings) =>
        settings.AggressiveMaxConsecutiveLosses >= 0 ? settings.AggressiveMaxConsecutiveLosses : _riskOptions.AggressiveMaxConsecutiveLosses;

    private static decimal MinPositive(decimal left, decimal right)
    {
        if (left <= 0m)
        {
            return Math.Max(0m, right);
        }

        if (right <= 0m)
        {
            return left;
        }

        return Math.Min(left, right);
    }

    private static FuturesPositionSnapshot MapStateToSnapshot(FuturesBotSettings settings, BotState? state)
    {
        if (state is null)
        {
            return new FuturesPositionSnapshot { Symbol = settings.Symbol, Category = settings.Category };
        }

        var markPrice = state.MarkPrice > 0m ? state.MarkPrice : state.LastObservedPrice ?? 0m;
        var size = state.BaseAssetQuantity;
        var leverage = state.Leverage > 0m ? state.Leverage : settings.Leverage;
        return new FuturesPositionSnapshot
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Side = size > 0m ? state.PositionSide ?? "Buy" : "None",
            Size = size,
            EntryPrice = state.EntryPrice > 0m ? state.EntryPrice : state.AverageEntryPrice,
            MarkPrice = markPrice,
            LiquidationPrice = state.LiquidationPrice,
            PositionValueUsdt = size * markPrice,
            MarginUsedUsdt = leverage > 0m ? size * markPrice / leverage : 0m,
            Leverage = leverage,
            UnrealizedPnl = state.UnrealizedPnl,
            RealizedPnl = state.TotalRealizedPnl,
            PositionIdx = state.PositionIdx,
            UpdatedAt = state.UpdatedAt
        };
    }

    private static FuturesInstrumentRules MapInstrumentRules(BybitInstrumentInfo instrument) => new()
    {
        TickSize = instrument.TickSize,
        QtyStep = instrument.QtyStep,
        BasePrecision = instrument.BasePrecision,
        MinOrderQty = instrument.MinOrderQty,
        MinOrderAmount = instrument.MinOrderAmount
    };

    private static decimal CalculateMinimumOrderQuantity(decimal price, FuturesInstrumentRules instrument)
    {
        var minQuantity = instrument.MinOrderQty;
        if (price > 0m && instrument.MinOrderAmount > 0m)
        {
            minQuantity = decimal.Max(minQuantity, instrument.MinOrderAmount / price);
        }

        var step = instrument.QtyStep > 0m ? instrument.QtyStep : instrument.BasePrecision;
        return step > 0m ? Math.Ceiling(minQuantity / step) * step : minQuantity;
    }

    private static decimal EstimateLongLiquidationPrice(decimal entryPrice, decimal leverage) =>
        leverage > 0m ? decimal.Max(0m, entryPrice * (1m - (1m / leverage))) : 0m;

    private static decimal EstimateShortLiquidationPrice(decimal entryPrice, decimal leverage) =>
        leverage > 0m ? entryPrice * (1m + (1m / leverage)) : 0m;

    private static bool IsShort(string side) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(side, "Short", StringComparison.OrdinalIgnoreCase);

    private bool AllowsShortPositions() =>
        _appOptions.TradingMode == TradingMode.Paper ||
        (_appOptions.TradingMode == TradingMode.Testnet && _futuresOptions.TestnetShortsEnabled);

    private async Task<IReadOnlyList<Candle>> GetAnalysisCandlesAsync(
        FuturesBotSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _bybitRestClient.GetKlinesAsync(
                settings.Category,
                settings.Symbol,
                AnalysisDefaults.AutoRecommendationCandleInterval,
                AnalysisDefaults.AutoRecommendationLookbackCandles,
                cancellationToken);
        }
        catch
        {
            return [];
        }
    }

    private static FuturesAutoRecommendationView MapAutoRecommendation(
        FuturesAutoConfigRecommendation recommendation,
        FuturesBotSettings settings,
        FuturesPositionView position,
        bool autoApplyEnabled,
        bool shortsAllowed)
    {
        var positionSnapshot = new FuturesPositionSnapshot
        {
            Symbol = settings.Symbol,
            Category = settings.Category,
            Side = position.Side,
            Size = position.Size
        };
        var canApply = FuturesAutoRecommendationSafety.CanApply(recommendation, positionSnapshot, shortsAllowed, out var blockReason);
        var compatibleStrategy = position.Size > 0m
            ? FuturesAutoRecommendationSafety.ResolveCompatibleStrategy(settings.StrategyType, position.Side)
            : settings.StrategyType;
        return new FuturesAutoRecommendationView
        {
            StrategyType = FormatEnum(recommendation.StrategyType),
            Reason = recommendation.Reason,
            Leverage = recommendation.Leverage,
            MarginMode = FormatEnum(recommendation.MarginMode),
            PositionMode = FormatEnum(recommendation.PositionMode),
            Direction = FormatEnum(recommendation.Direction),
            MaxNotionalUsdt = recommendation.MaxNotionalUsdt,
            MaxMarginUsdt = recommendation.MaxMarginUsdt,
            StopLossPercent = recommendation.StopLossPercent,
            TakeProfitPercent = recommendation.TakeProfitPercent,
            LiquidationBufferPercent = recommendation.LiquidationBufferPercent,
            StrategyConfigJson = recommendation.StrategyConfigJson,
            AutoApplyEnabled = autoApplyEnabled,
            CanApply = canApply,
            ApplyBlockReason = canApply ? "-" : blockReason,
            CompatibleStrategyForPosition = FormatEnum(compatibleStrategy),
            LastPrice = recommendation.Metrics.LastPrice,
            MovePercent = recommendation.Metrics.MovePercent,
            AtrPercent = recommendation.Metrics.AtrPercent,
            DrawdownPercent = recommendation.Metrics.DrawdownPercent,
            Support = recommendation.Metrics.Support,
            Resistance = recommendation.Metrics.Resistance
        };
    }

    private static FuturesBotSettings BuildRecommendedSettings(
        FuturesBotSettings currentSettings,
        FuturesAutoConfigRecommendation recommendation) => new()
    {
        Enabled = currentSettings.Enabled,
        Symbol = currentSettings.Symbol,
        Category = currentSettings.Category,
        StrategyType = recommendation.StrategyType,
        StrategyConfigJson = recommendation.StrategyConfigJson,
        Leverage = recommendation.Leverage,
        MarginMode = recommendation.MarginMode,
        PositionMode = recommendation.PositionMode,
        Direction = recommendation.Direction,
        MaxNotionalUsdt = recommendation.MaxNotionalUsdt,
        MaxMarginUsdt = recommendation.MaxMarginUsdt,
        StopLossPercent = recommendation.StopLossPercent,
        TakeProfitPercent = recommendation.TakeProfitPercent,
        LiquidationBufferPercent = recommendation.LiquidationBufferPercent,
        ReduceOnlyEnabled = recommendation.ReduceOnlyEnabled,
        AggressiveModeEnabled = currentSettings.AggressiveModeEnabled,
        AggressiveModeKind = currentSettings.AggressiveModeKind,
        AggressiveEntryMultiplier = currentSettings.AggressiveEntryMultiplier,
        AggressiveMaxOrdersPerHour = currentSettings.AggressiveMaxOrdersPerHour,
        AggressiveMinSecondsBetweenEntries = currentSettings.AggressiveMinSecondsBetweenEntries,
        AggressiveMaxConsecutiveLosses = currentSettings.AggressiveMaxConsecutiveLosses,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static FuturesBotSettings BuildDefaultSettings(FuturesOptions options, string symbol = "BTCUSDT") => new()
    {
        Enabled = true,
        Symbol = symbol,
        Category = NormalizeCategory(options.Category),
        StrategyType = FuturesStrategyType.Pause,
        StrategyConfigJson = "{}",
        Leverage = options.Leverage,
        MarginMode = ParseMarginMode(options.MarginMode) ?? FuturesMarginMode.Isolated,
        PositionMode = ParsePositionMode(options.PositionMode) ?? FuturesPositionMode.OneWay,
        Direction = FuturesDirection.LongOnly,
        MaxNotionalUsdt = options.MaxNotionalUsdt,
        MaxMarginUsdt = options.MaxMarginUsdt,
        LiquidationBufferPercent = options.MinLiquidationBufferPercent,
        StopLossPercent = options.StopLossRequired ? 2m : 0m,
        ReduceOnlyEnabled = true,
        AggressiveModeEnabled = options.AggressiveModeEnabled,
        AggressiveModeKind = ParseAggressiveModeKind(options.AggressiveModeKind) ?? FuturesAggressiveModeKind.Normal,
        AggressiveEntryMultiplier = options.AggressiveEntryMultiplier,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static List<string> ValidateRequest(string symbol, string category, UpdateFuturesSettingsRequest request)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            errors.Add("Symbol is required.");
        }

        if (category != "linear")
        {
            errors.Add("MVP supports only USDT linear perpetuals: CATEGORY must be linear.");
        }

        if (NormalizeToken(request.MarginMode) != "isolated")
        {
            errors.Add("MVP supports only isolated margin. Cross margin is a later phase.");
        }

        if (NormalizeToken(request.PositionMode) != "oneway")
        {
            errors.Add("MVP supports only one-way position mode. Hedge mode is a later phase.");
        }

        if (request.Leverage < 1m)
        {
            errors.Add("Leverage must be at least 1.");
        }

        if (request.MaxNotionalUsdt <= 0m)
        {
            errors.Add("Max notional must be positive.");
        }

        if (request.MaxMarginUsdt <= 0m)
        {
            errors.Add("Max margin must be positive.");
        }

        if (request.StopLossPercent <= 0m)
        {
            errors.Add("Stop loss percent must be positive.");
        }

        if (request.TakeProfitPercent <= 0m)
        {
            errors.Add("Take profit percent must be positive.");
        }
        else if (request.StopLossPercent > 0m && request.TakeProfitPercent < request.StopLossPercent * 3m)
        {
            errors.Add("Futures take profit must be at least 3x stop loss for 3:1 risk/reward.");
        }

        if (request.LiquidationBufferPercent < 0m)
        {
            errors.Add("Liquidation buffer percent cannot be negative.");
        }

        if (request.AggressiveEntryMultiplier <= 0m)
        {
            errors.Add("Aggressive entry multiplier must be positive.");
        }

        if (request.AggressiveMaxOrdersPerHour < 0)
        {
            errors.Add("Aggressive max orders per hour cannot be negative.");
        }

        if (request.AggressiveMinSecondsBetweenEntries < 0)
        {
            errors.Add("Aggressive min seconds between entries cannot be negative.");
        }

        if (request.AggressiveMaxConsecutiveLosses < 0)
        {
            errors.Add("Aggressive max consecutive losses cannot be negative.");
        }

        return errors;
    }

    private static FuturesStrategyType? ParseFuturesStrategyType(string? value) =>
        NormalizeToken(value) switch
        {
            "trendfollow" or "trend" or "trendfollowing" => FuturesStrategyType.TrendFollow,
            "breakout" => FuturesStrategyType.Breakout,
            "gridlongonly" or "gridlong" or "grid" => FuturesStrategyType.GridLongOnly,
            "trendfollowshortonly" or "trendshort" or "shorttrend" => FuturesStrategyType.TrendFollowShortOnly,
            "breakdownshort" or "breakdown" or "shortbreakdown" => FuturesStrategyType.BreakdownShort,
            "gridshortonly" or "gridshort" => FuturesStrategyType.GridShortOnly,
            "reduceonly" or "reduce" => FuturesStrategyType.ReduceOnly,
            "pause" => FuturesStrategyType.Pause,
            _ => null
        };

    private static FuturesMarginMode? ParseMarginMode(string? value) =>
        NormalizeToken(value) switch
        {
            "isolated" => FuturesMarginMode.Isolated,
            "cross" => FuturesMarginMode.Cross,
            _ => null
        };

    private static FuturesPositionMode? ParsePositionMode(string? value) =>
        NormalizeToken(value) switch
        {
            "oneway" or "onewaymode" => FuturesPositionMode.OneWay,
            "hedge" or "hedgemode" => FuturesPositionMode.Hedge,
            _ => null
        };

    private static FuturesDirection? ParseDirection(string? value) =>
        NormalizeToken(value) switch
        {
            "longonly" or "long" => FuturesDirection.LongOnly,
            "shortonly" or "short" => FuturesDirection.ShortOnly,
            "longshort" or "longandshort" or "both" => FuturesDirection.LongAndShort,
            _ => null
        };

    private static FuturesAggressiveModeKind? ParseAggressiveModeKind(string? value) =>
        NormalizeToken(value) switch
        {
            "" or "normal" => FuturesAggressiveModeKind.Normal,
            "test" => FuturesAggressiveModeKind.Test,
            _ => null
        };

    private static string? NormalizeStrategyConfigJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatEnum<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        object boxed = value;
        return boxed switch
        {
            FuturesStrategyType.TrendFollow => "trendfollow",
            FuturesStrategyType.GridLongOnly => "gridlongonly",
            FuturesStrategyType.TrendFollowShortOnly => "trendfollowshortonly",
            FuturesStrategyType.BreakdownShort => "breakdownshort",
            FuturesStrategyType.GridShortOnly => "gridshortonly",
            FuturesStrategyType.ReduceOnly => "reduceonly",
            FuturesDirection.LongOnly => "long-only",
            FuturesDirection.ShortOnly => "short-only",
            FuturesDirection.LongAndShort => "long+short",
            FuturesMarginMode.Isolated => "isolated",
            FuturesMarginMode.Cross => "cross",
            FuturesPositionMode.OneWay => "oneway",
            FuturesPositionMode.Hedge => "hedge",
            FuturesAggressiveModeKind.Normal => "normal",
            FuturesAggressiveModeKind.Test => "test",
            _ => value.ToString().ToLowerInvariant()
        };
    }

    private static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();

    private static string NormalizeCategory(string category) => string.IsNullOrWhiteSpace(category)
        ? "linear"
        : category.Trim().ToLowerInvariant();

    private static string? NormalizeOptionalSymbol(string? symbol) =>
        string.IsNullOrWhiteSpace(symbol) ? null : NormalizeSymbol(symbol);

    private static string NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant()
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("+", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);

}
