using System.Globalization;
using System.Text.Json;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IRotationManagerService
{
    Task<RotationStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<RotationStatusResponse> StartAsync(RotationStartRequest request, CancellationToken cancellationToken);
    Task<RotationStatusResponse> StopAsync(CancellationToken cancellationToken);
    Task<RotationRunResult> RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class RotationManagerService : IRotationManagerService
{
    private const string SpotCategory = "spot";
    private const string DecisionStart = "start";
    private const string DecisionStop = "stop";
    private const string DecisionSkip = "skip";
    private const string DecisionActivate = "activate";
    private const string DecisionDisable = "disable";
    private const string DecisionDetach = "detach";
    private const string DecisionKeep = "keep";
    private const string DecisionPrune = "prune";

    private readonly IGridRepository _repository;
    private readonly IMarketScannerService _marketScannerService;
    private readonly RotationOptions _rotationOptions;
    private readonly AppOptions _appOptions;
    private readonly ILogger<RotationManagerService> _logger;

    public RotationManagerService(
        IGridRepository repository,
        IMarketScannerService marketScannerService,
        IOptions<RotationOptions> rotationOptions,
        IOptions<AppOptions> appOptions,
        ILogger<RotationManagerService> logger)
    {
        _repository = repository;
        _marketScannerService = marketScannerService;
        _rotationOptions = rotationOptions.Value;
        _appOptions = appOptions.Value;
        _logger = logger;
    }

    public async Task<RotationStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var state = await GetOrCreateStateAsync(cancellationToken);
        return await BuildStatusAsync(state, cancellationToken);
    }

    public async Task<RotationStatusResponse> StartAsync(RotationStartRequest request, CancellationToken cancellationToken)
    {
        var current = await GetOrCreateStateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var activePairPoolSize = request.ActivePairPoolSize is > 0
            ? request.ActivePairPoolSize.Value
            : current.ActivePairPoolSize;
        var maxActivePositions = request.MaxActivePositions is > 0
            ? request.MaxActivePositions.Value
            : activePairPoolSize;
        var updated = current with
        {
            RotationEnabled = true,
            ActivePairPoolSize = Math.Max(1, activePairPoolSize),
            MaxActivePositions = Math.Max(1, maxActivePositions),
            StartedAt = now,
            StoppedAt = null,
            UpdatedAt = now
        };

        await _repository.SaveRotationStateAsync(updated, cancellationToken);
        await AddDecisionAsync(DecisionStart, null, null, null, 0m, 0m, $"Rotation started with {updated.ActivePairPoolSize} active pair slot(s).", now, cancellationToken);
        await RunOnceAsync(cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<RotationStatusResponse> StopAsync(CancellationToken cancellationToken)
    {
        var current = await GetOrCreateStateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var updated = current with
        {
            RotationEnabled = false,
            StoppedAt = now,
            UpdatedAt = now
        };

        await _repository.SaveRotationStateAsync(updated, cancellationToken);
        await AddDecisionAsync(DecisionStop, null, null, null, 0m, 0m, "Rotation stopped. Existing positions are left intact.", now, cancellationToken);
        return await BuildStatusAsync(updated, cancellationToken);
    }

    public async Task<RotationRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var state = await GetOrCreateStateAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (!state.RotationEnabled)
        {
            await AddDecisionAsync(DecisionSkip, null, null, null, 0m, 0m, "Rotation is disabled.", now, cancellationToken);
            return new RotationRunResult(false, 0, 0, "Rotation is disabled.");
        }

        if (!IsModeAllowed(state.RotationMode, out var modeReason))
        {
            await AddDecisionAsync(DecisionSkip, null, null, null, 0m, 0m, modeReason, now, cancellationToken);
            return new RotationRunResult(false, 0, 0, modeReason);
        }

        var scanLimit = Math.Clamp(state.ActivePairPoolSize * 8, 20, 80);
        var scan = await _marketScannerService.ScanAsync(SpotCategory, scanLimit, cancellationToken);
        var allScanItems = scan.Items
            .ToDictionary(item => item.Symbol, StringComparer.OrdinalIgnoreCase);
        var candidates = scan.Items
            .Where(IsCandidateStrategyEnabled)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.StrategyFitScore)
            .ToArray();

        await SaveCandidateScoresAsync(candidates, now, cancellationToken);

        var profileList = (await _repository.GetRuntimeSettingsProfilesAsync(cancellationToken)).ToList();
        var closedDetachedSymbols = await CleanupClosedDetachedProfilesAsync(profileList, now, cancellationToken);
        var profiles = profileList
            .Where(profile => !closedDetachedSymbols.Contains(profile.Symbol))
            .ToDictionary(profile => profile.Symbol, StringComparer.OrdinalIgnoreCase);
        var activeSymbols = profiles.Values
            .Where(IsActiveRotationProfile)
            .Select(profile => profile.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slots = (await _repository.GetActivePairSlotsAsync(cancellationToken))
            .ToDictionary(slot => slot.SlotIndex);

        var targetSlotCount = state.MaxActivePositions > 0
            ? Math.Min(state.ActivePairPoolSize, state.MaxActivePositions)
            : state.ActivePairPoolSize;
        var activated = 0;
        var replaced = 0;
        var poolSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var slotIndex = 1; slotIndex <= targetSlotCount; slotIndex++)
        {
            slots.TryGetValue(slotIndex, out var slot);
            var currentProfile = slot?.Symbol is { Length: > 0 } symbol && profiles.TryGetValue(symbol, out var profile)
                ? profile
                : null;
            if (slot is not null &&
                !string.IsNullOrWhiteSpace(slot.Symbol) &&
                currentProfile is not null)
            {
                var replacement = await EvaluateReplacementAsync(
                    state,
                    slot,
                    currentProfile,
                    allScanItems,
                    candidates,
                    now,
                    cancellationToken);
                if (!replacement.ShouldReplace)
                {
                    poolSymbols.Add(slot.Symbol);
                    await UpsertSlotAsync(slot with
                    {
                        Status = replacement.Status,
                        Score = replacement.CurrentScore,
                        Reason = replacement.Reason,
                        UpdatedAt = now
                    }, cancellationToken);
                    await AddDecisionAsync(DecisionKeep, slot.Symbol, null, slotIndex, replacement.CurrentScore, replacement.CandidateScore, replacement.Reason, now, cancellationToken);
                    continue;
                }

                var replacementCandidate = FindNextCandidate(candidates, activeSymbols, slot.Symbol);
                if (replacementCandidate is null)
                {
                    await UpsertSlotAsync(slot with
                    {
                        Status = RotationPairStatus.Waiting,
                        Score = replacement.CurrentScore,
                        Reason = $"{replacement.Reason} No eligible replacement candidate found.",
                        UpdatedAt = now
                    }, cancellationToken);
                    await AddDecisionAsync(DecisionSkip, slot.Symbol, null, slotIndex, replacement.CurrentScore, 0m, "Replacement skipped: no eligible candidate.", now, cancellationToken);
                    continue;
                }

                activeSymbols.Remove(slot.Symbol);
                if (ShouldDetachOnReplace(replacement.Status))
                {
                    await DetachProfileAsync(currentProfile, $"Rotation detached pair: {replacement.Reason}", now, cancellationToken);
                    await AddDecisionAsync(DecisionDetach, slot.Symbol, null, slotIndex, replacement.CurrentScore, replacementCandidate.Score, replacement.Reason, now, cancellationToken);
                }
                else
                {
                    await RemoveOrDetachProfileAsync(currentProfile, poolSymbols, $"Rotation replacement: {replacement.Reason}", now, cancellationToken);
                    await AddDecisionAsync(DecisionDisable, slot.Symbol, null, slotIndex, replacement.CurrentScore, replacementCandidate.Score, replacement.Reason, now, cancellationToken);
                }

                await ActivateCandidateAsync(slotIndex, slot, replacementCandidate, activeSymbols, poolSymbols, $"Replacement: {replacement.Reason}", now, cancellationToken);
                replaced++;
                continue;
            }

            if (slot is not null &&
                !string.IsNullOrWhiteSpace(slot.Symbol) &&
                activeSymbols.Contains(slot.Symbol))
            {
                var currentScore = allScanItems.TryGetValue(slot.Symbol, out var currentScan)
                    ? currentScan.Score
                    : slot.Score;
                poolSymbols.Add(slot.Symbol);
                await UpsertSlotAsync(slot with
                {
                    Status = RotationPairStatus.Active,
                    Score = currentScore,
                    Reason = "Active profile kept by rotation.",
                    UpdatedAt = now
                }, cancellationToken);
                continue;
            }

            var candidate = FindNextCandidate(candidates, activeSymbols, null);
            if (candidate is null)
            {
                await UpsertSlotAsync(new ActivePairSlotRecord
                {
                    SlotIndex = slotIndex,
                    Category = SpotCategory,
                    Status = RotationPairStatus.Waiting,
                    Reason = "No eligible rotation candidate found.",
                    ActivatedAt = slot?.ActivatedAt ?? now,
                    UpdatedAt = now
                }, cancellationToken);
                continue;
            }

            await ActivateCandidateAsync(slotIndex, slot, candidate, activeSymbols, poolSymbols, "Activated into empty rotation slot.", now, cancellationToken);
            activated++;
        }

        await DisableSlotsOutsidePoolAsync(slots.Values, targetSlotCount, now, cancellationToken);
        var pruned = await PruneProfilesOutsidePoolAsync(poolSymbols, now, cancellationToken);

        var updatedState = state with
        {
            LastScanAt = now,
            UpdatedAt = now
        };
        await _repository.SaveRotationStateAsync(updatedState, cancellationToken);

        var message = activated == 0 && replaced == 0 && pruned == 0
            ? "Rotation scan completed; no changes needed."
            : $"Rotation scan completed; activated {activated} pair(s), replaced {replaced} pair(s), pruned {pruned} profile(s).";
        return new RotationRunResult(true, scan.ScannedCount, activated + replaced + pruned, message);
    }

    private async Task<RotationStateRecord> GetOrCreateStateAsync(CancellationToken cancellationToken)
    {
        var existing = await _repository.GetRotationStateAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var created = new RotationStateRecord
        {
            RotationEnabled = _rotationOptions.RotationEnabled,
            ActivePairPoolSize = _rotationOptions.ActivePairPoolSize,
            ScanIntervalMinutes = _rotationOptions.ScanIntervalMinutes,
            MinPairLifetimeMinutes = _rotationOptions.MinPairLifetimeMinutes,
            ReplacementScoreGap = _rotationOptions.ReplacementScoreGap,
            AllowReplaceOnlyWhenFlat = _rotationOptions.AllowReplaceOnlyWhenFlat,
            MaxActivePositions = _rotationOptions.MaxActivePositions,
            RotationMode = _rotationOptions.RotationMode,
            StartedAt = _rotationOptions.RotationEnabled ? now : null,
            UpdatedAt = now
        };

        await _repository.SaveRotationStateAsync(created, cancellationToken);
        return created;
    }

    private async Task<RotationStatusResponse> BuildStatusAsync(RotationStateRecord state, CancellationToken cancellationToken)
    {
        var slots = await _repository.GetActivePairSlotsAsync(cancellationToken);
        var decisions = await _repository.GetRotationDecisionsAsync(20, cancellationToken);
        return new RotationStatusResponse
        {
            RotationEnabled = state.RotationEnabled,
            ActivePairPoolSize = state.ActivePairPoolSize,
            ScanIntervalMinutes = state.ScanIntervalMinutes,
            MinPairLifetimeMinutes = state.MinPairLifetimeMinutes,
            ReplacementScoreGap = state.ReplacementScoreGap,
            AllowReplaceOnlyWhenFlat = state.AllowReplaceOnlyWhenFlat,
            MaxActivePositions = state.MaxActivePositions,
            RotationMode = state.RotationMode.ToString(),
            StartedAt = state.StartedAt,
            StoppedAt = state.StoppedAt,
            LastScanAt = state.LastScanAt,
            UpdatedAt = state.UpdatedAt,
            Slots = slots
                .OrderBy(slot => slot.SlotIndex)
                .Select(ToResponse)
                .ToArray(),
            RecentDecisions = decisions
                .Select(ToResponse)
                .ToArray()
        };
    }

    private async Task SaveCandidateScoresAsync(IEnumerable<MarketScanItem> candidates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            var strategyType = ParseTradingStrategyType(candidate.RecommendedStrategy).ToString();
            var metrics = new
            {
                candidate.StrategyFitScore,
                candidate.GridFitScore,
                candidate.BtdFitScore,
                candidate.ComboFitScore,
                candidate.ReversalFitScore,
                candidate.SpreadPercent,
                candidate.AtrPercent,
                candidate.VolatilityPercent,
                candidate.Volume6hUsdt,
                candidate.Reasons
            };

            await _repository.UpsertPairStrategyScoreAsync(new PairStrategyScoreRecord
            {
                Symbol = candidate.Symbol,
                Category = candidate.Category,
                StrategyType = strategyType,
                Score = candidate.Score,
                Reason = string.Join("; ", candidate.Reasons.Take(5)),
                MetricsJson = JsonSerializer.Serialize(metrics),
                UpdatedAt = now
            }, cancellationToken);
        }
    }

    private async Task UpsertSlotAsync(ActivePairSlotRecord slot, CancellationToken cancellationToken) =>
        await _repository.UpsertActivePairSlotAsync(slot, cancellationToken);

    private async Task ActivateCandidateAsync(
        int slotIndex,
        ActivePairSlotRecord? previousSlot,
        MarketScanItem candidate,
        HashSet<string> activeSymbols,
        HashSet<string> poolSymbols,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var previousSymbol = previousSlot?.Symbol;
        var settings = BuildSettings(candidate, now);
        await _repository.SaveRuntimeSettingsAsync(settings, cancellationToken);
        activeSymbols.Add(candidate.Symbol);
        poolSymbols.Add(candidate.Symbol);

        await UpsertSlotAsync(new ActivePairSlotRecord
        {
            SlotIndex = slotIndex,
            Symbol = candidate.Symbol,
            Category = candidate.Category,
            Status = RotationPairStatus.Active,
            Score = candidate.Score,
            Reason = $"{reason} {string.Join("; ", candidate.Reasons.Take(3))}",
            ActivatedAt = now,
            UpdatedAt = now
        }, cancellationToken);

        await _repository.AddPairRotationHistoryAsync(new PairRotationHistoryRecord
        {
            SlotIndex = slotIndex,
            PreviousSymbol = previousSymbol,
            NewSymbol = candidate.Symbol,
            Reason = reason,
            PreviousScore = previousSlot?.Score ?? 0m,
            NewScore = candidate.Score,
            CreatedAt = now
        }, cancellationToken);
        await AddDecisionAsync(DecisionActivate, previousSymbol, candidate.Symbol, slotIndex, previousSlot?.Score ?? 0m, candidate.Score, reason, now, cancellationToken);
    }

    private async Task RemoveOrDetachProfileAsync(
        GridBotSettings profile,
        HashSet<string> poolSymbols,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (poolSymbols.Contains(profile.Symbol))
        {
            return;
        }

        if (await CanDeleteProfileAsync(profile.Symbol, cancellationToken))
        {
            await _repository.DeleteRuntimeSettingsAsync(profile.Symbol, cancellationToken);
            await AddDecisionAsync(DecisionPrune, profile.Symbol, null, null, 0m, 0m, $"{reason}. Flat profile removed from rotation pool.", now, cancellationToken);
            _logger.LogInformation("Rotation removed flat profile {Symbol}. Reason: {Reason}", profile.Symbol, reason);
            return;
        }

        await DetachProfileAsync(profile, reason, now, cancellationToken);
    }

    private async Task<int> PruneProfilesOutsidePoolAsync(
        HashSet<string> poolSymbols,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (poolSymbols.Count == 0)
        {
            await AddDecisionAsync(DecisionSkip, null, null, null, 0m, 0m, "Rotation pruning skipped because no active pool symbols were resolved.", now, cancellationToken);
            return 0;
        }

        var pruned = 0;
        var profiles = await _repository.GetRuntimeSettingsProfilesAsync(cancellationToken);
        foreach (var profile in profiles)
        {
            if (poolSymbols.Contains(profile.Symbol))
            {
                continue;
            }

            if (await CanDeleteProfileAsync(profile.Symbol, cancellationToken))
            {
                await _repository.DeleteRuntimeSettingsAsync(profile.Symbol, cancellationToken);
                await AddDecisionAsync(DecisionPrune, profile.Symbol, null, null, 0m, 0m, "Flat profile outside rotation pool removed.", now, cancellationToken);
                pruned++;
                continue;
            }

            if (IsBuyCapableProfile(profile))
            {
                await DetachProfileAsync(profile, "Profile outside rotation pool is not flat.", now, cancellationToken);
                await AddDecisionAsync(DecisionDetach, profile.Symbol, null, null, 0m, 0m, "Profile outside rotation pool detached until flat.", now, cancellationToken);
            }
        }

        return pruned;
    }

    private async Task DisableSlotsOutsidePoolAsync(
        IEnumerable<ActivePairSlotRecord> slots,
        int targetSlotCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var slot in slots.Where(slot => slot.SlotIndex > targetSlotCount))
        {
            await UpsertSlotAsync(slot with
            {
                Symbol = null,
                Status = RotationPairStatus.Disabled,
                Score = 0m,
                Reason = "Slot disabled because it is outside the active pair pool size.",
                UpdatedAt = now
            }, cancellationToken);
        }
    }

    private async Task<bool> CanDeleteProfileAsync(string symbol, CancellationToken cancellationToken)
    {
        return !await HasOpenLifecycleAsync(symbol, cancellationToken);
    }

    private async Task DetachProfileAsync(
        GridBotSettings profile,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _repository.SaveRuntimeSettingsAsync(new GridBotSettings
        {
            Symbol = profile.Symbol,
            Category = profile.Category,
            StrategySelectionMode = StrategySelectionMode.Auto,
            StrategyType = TradingStrategyType.Detached,
            StrategyConfigJson = profile.StrategyConfigJson,
            LowerPrice = profile.LowerPrice,
            UpperPrice = profile.UpperPrice,
            Step = profile.Step,
            OrderSizeUsdt = profile.OrderSizeUsdt,
            StopLowerPrice = profile.StopLowerPrice,
            StopUpperPrice = profile.StopUpperPrice,
            UpdatedAt = now
        }, cancellationToken);
        _logger.LogInformation("Rotation detached {Symbol}. Reason: {Reason}", profile.Symbol, reason);
    }

    private async Task<HashSet<string>> CleanupClosedDetachedProfilesAsync(
        IReadOnlyCollection<GridBotSettings> profiles,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var closedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles.Where(static profile => profile.StrategyType == TradingStrategyType.Detached))
        {
            if (await HasOpenLifecycleAsync(profile.Symbol, cancellationToken))
            {
                continue;
            }

            await _repository.DeleteRuntimeSettingsAsync(profile.Symbol, cancellationToken);
            await AddDecisionAsync(DecisionDisable, profile.Symbol, null, null, 0m, 0m, "Detached profile closed; no active orders or position remain.", now, cancellationToken);
            closedSymbols.Add(profile.Symbol);
            _logger.LogInformation("Rotation removed closed detached profile {Symbol}.", profile.Symbol);
        }

        return closedSymbols;
    }

    private async Task<bool> HasOpenLifecycleAsync(string symbol, CancellationToken cancellationToken)
    {
        var activeOrders = await _repository.GetActiveOrdersAsync(symbol, cancellationToken);
        if (activeOrders.Count > 0)
        {
            return true;
        }

        var state = await _repository.GetBotStateAsync(symbol, cancellationToken);
        return state?.BaseAssetQuantity > 0m;
    }

    private async Task<ReplacementEvaluation> EvaluateReplacementAsync(
        RotationStateRecord state,
        ActivePairSlotRecord slot,
        GridBotSettings profile,
        IReadOnlyDictionary<string, MarketScanItem> allScanItems,
        IReadOnlyList<MarketScanItem> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var symbol = profile.Symbol;
        var currentScan = allScanItems.TryGetValue(symbol, out var scanItem) ? scanItem : null;
        var currentScore = currentScan?.Score ?? slot.Score;
        var bestReplacement = candidates.FirstOrDefault(item => !string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        var candidateScore = bestReplacement?.Score ?? 0m;
        var lifetimeElapsed = now - slot.ActivatedAt >= TimeSpan.FromMinutes(Math.Max(0, state.MinPairLifetimeMinutes));

        var lifecycle = await ResolveOpenLifecycleAsync(profile, cancellationToken);
        if (!IsBuyCapableProfile(profile))
        {
            if (lifecycle is not null)
            {
                return new ReplacementEvaluation(
                    true,
                    lifecycle.Value.Status,
                    currentScore,
                    candidateScore,
                    $"{profile.StrategyType} profile cannot open new entries. {lifecycle.Value.Reason} Detaching old pair and freeing rotation slot.");
            }

            return new ReplacementEvaluation(true, RotationPairStatus.Disabled, currentScore, candidateScore, $"{profile.StrategyType} profile can be replaced.");
        }

        if (lifecycle is not null)
        {
            if (!lifetimeElapsed)
            {
                return new ReplacementEvaluation(false, lifecycle.Value.Status, currentScore, candidateScore, $"{lifecycle.Value.Reason} Minimum lifetime not reached for {symbol}.");
            }

            if (bestReplacement is not null && candidateScore >= currentScore + state.ReplacementScoreGap)
            {
                return new ReplacementEvaluation(
                    true,
                    lifecycle.Value.Status,
                    currentScore,
                    candidateScore,
                    $"{lifecycle.Value.Reason} Detaching old pair and freeing rotation slot for {bestReplacement.Symbol}.");
            }

            return new ReplacementEvaluation(false, lifecycle.Value.Status, currentScore, candidateScore, lifecycle.Value.Reason);
        }

        if (!lifetimeElapsed)
        {
            return new ReplacementEvaluation(false, RotationPairStatus.Active, currentScore, candidateScore, $"Minimum lifetime not reached for {symbol}.");
        }

        var latestReason = await _repository.GetLatestNoTradeReasonAsync(symbol, cancellationToken);
        if (latestReason?.ReasonCode is NoTradeReason.StrategyCooldown or NoTradeReason.AggressiveStopLoss or NoTradeReason.DailyLossLimitReached)
        {
            return new ReplacementEvaluation(true, RotationPairStatus.Cooldown, currentScore, candidateScore, $"Latest no-trade reason is {latestReason.ReasonCode}.");
        }

        var orders = await _repository.GetOrdersAsync(symbol, cancellationToken);
        var latestClosedSell = orders
            .Where(order => order.Side == TradeSide.Sell && order.Status == OrderStatus.Filled)
            .OrderByDescending(order => order.FilledAt ?? order.UpdatedAt)
            .FirstOrDefault();
        if (latestClosedSell?.RealizedPnl < 0m)
        {
            return new ReplacementEvaluation(true, RotationPairStatus.Cooldown, currentScore, candidateScore, "Latest closed sell was a loss.");
        }

        var filledOrders = orders.Where(order => order.Status == OrderStatus.Filled).ToArray();
        if (filledOrders.Length == 0)
        {
            return new ReplacementEvaluation(true, RotationPairStatus.Candidate, currentScore, candidateScore, $"No filled trades after {state.MinPairLifetimeMinutes}m lifetime.");
        }

        if (currentScan is null || !IsCandidateStrategyEnabled(currentScan))
        {
            return new ReplacementEvaluation(true, RotationPairStatus.Candidate, currentScore, candidateScore, "Scanner no longer recommends an entry-capable strategy.");
        }

        if (bestReplacement is not null && candidateScore >= currentScore + state.ReplacementScoreGap)
        {
            return new ReplacementEvaluation(true, RotationPairStatus.Candidate, currentScore, candidateScore, $"Candidate {bestReplacement.Symbol} beats {symbol} by {candidateScore - currentScore:0.##} score points.");
        }

        return new ReplacementEvaluation(false, RotationPairStatus.Active, currentScore, candidateScore, "Active pair remains competitive.");
    }

    private async Task<OpenLifecycleEvaluation?> ResolveOpenLifecycleAsync(GridBotSettings profile, CancellationToken cancellationToken)
    {
        var activeOrders = await _repository.GetActiveOrdersAsync(profile.Symbol, cancellationToken);
        var state = await _repository.GetBotStateAsync(profile.Symbol, cancellationToken);

        if (state is not null && state.BaseAssetQuantity > 0m)
        {
            var currentPrice = state.LastObservedPrice ?? 0m;
            var positiveExit = currentPrice > 0m && state.AverageEntryPrice > 0m && currentPrice > state.AverageEntryPrice;
            var reason = positiveExit
                ? $"{profile.Symbol} has an open position with possible net-positive exit."
                : $"{profile.Symbol} has an open position without net-positive exit.";
            return new OpenLifecycleEvaluation(RotationPairStatus.InPosition, reason);
        }

        if (activeOrders.Count == 0)
        {
            return null;
        }

        var hasEntryOrders = activeOrders.Any(order => order.Side == TradeSide.Buy);
        var status = hasEntryOrders ? RotationPairStatus.WaitingOrder : RotationPairStatus.InPosition;
        return new OpenLifecycleEvaluation(status, $"{profile.Symbol} has {activeOrders.Count} active order(s).");
    }

    private async Task AddDecisionAsync(
        string action,
        string? symbol,
        string? candidateSymbol,
        int? slotIndex,
        decimal currentScore,
        decimal candidateScore,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _repository.AddRotationDecisionAsync(new RotationDecisionRecord
        {
            Action = action,
            Symbol = symbol,
            CandidateSymbol = candidateSymbol,
            SlotIndex = slotIndex,
            CurrentScore = currentScore,
            CandidateScore = candidateScore,
            Reason = reason,
            CreatedAt = now
        }, cancellationToken);
    }

    private bool IsModeAllowed(RotationMode rotationMode, out string reason)
    {
        if (rotationMode == RotationMode.PaperOnly && _appOptions.TradingMode != TradingMode.Paper)
        {
            reason = $"Rotation mode {rotationMode} requires paper trading mode; current mode is {_appOptions.TradingMode}.";
            return false;
        }

        if (rotationMode == RotationMode.Testnet && _appOptions.TradingMode == TradingMode.Mainnet)
        {
            reason = $"Rotation mode {rotationMode} does not allow mainnet trading.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsBuyCapableProfile(GridBotSettings profile) =>
        profile.StrategyType is not TradingStrategyType.NoTrade and
            not TradingStrategyType.Pause and
            not TradingStrategyType.ReduceOnly and
            not TradingStrategyType.Detached;

    private static bool IsActiveRotationProfile(GridBotSettings profile) =>
        IsBuyCapableProfile(profile);

    private static bool IsCandidateStrategyEnabled(MarketScanItem item)
    {
        var strategyType = ParseTradingStrategyType(item.RecommendedStrategy);
        return strategyType is not TradingStrategyType.NoTrade and
            not TradingStrategyType.Pause and
            not TradingStrategyType.ReduceOnly and
            not TradingStrategyType.Detached;
    }

    private static bool ShouldDetachOnReplace(RotationPairStatus status) =>
        status is RotationPairStatus.WaitingOrder or RotationPairStatus.InPosition;

    private static MarketScanItem? FindNextCandidate(
        IReadOnlyList<MarketScanItem> candidates,
        HashSet<string> activeSymbols,
        string? currentSymbol)
    {
        return candidates.FirstOrDefault(item =>
            !activeSymbols.Contains(item.Symbol) &&
            !string.Equals(item.Symbol, currentSymbol, StringComparison.OrdinalIgnoreCase));
    }

    private static GridBotSettings BuildSettings(MarketScanItem candidate, DateTimeOffset now)
    {
        var request = candidate.Settings;
        return new GridBotSettings
        {
            Symbol = candidate.Symbol,
            Category = string.IsNullOrWhiteSpace(request.Category) ? candidate.Category : request.Category,
            StrategySelectionMode = StrategySelectionMode.Auto,
            StrategyType = ParseTradingStrategyType(request.StrategyType),
            StrategyConfigJson = string.IsNullOrWhiteSpace(request.StrategyConfigJson) ? "{}" : request.StrategyConfigJson,
            LowerPrice = request.LowerPrice,
            UpperPrice = request.UpperPrice,
            Step = request.Step,
            OrderSizeUsdt = request.OrderSizeUsdt,
            StopLowerPrice = request.StopLowerPrice,
            StopUpperPrice = request.StopUpperPrice,
            UpdatedAt = now
        };
    }

    private static TradingStrategyType ParseTradingStrategyType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "grid" => TradingStrategyType.Grid,
            "dca" => TradingStrategyType.Dca,
            "combo" => TradingStrategyType.Combo,
            "btd" => TradingStrategyType.Btd,
            "signal" or "signalbot" or "signal_bot" => TradingStrategyType.Signal,
            "trend" or "trendfollow" or "trend_follow" or "trendfollowing" or "trend_following" => TradingStrategyType.TrendFollowing,
            "breakout" => TradingStrategyType.Breakout,
            "hybrid" => TradingStrategyType.Hybrid,
            "detached" or "orderwatch" or "order_watch" => TradingStrategyType.Detached,
            "reduceonly" or "reduce_only" => TradingStrategyType.ReduceOnly,
            "notrade" or "no_trade" => TradingStrategyType.NoTrade,
            "pause" => TradingStrategyType.Pause,
            _ => Enum.TryParse<TradingStrategyType>(value, true, out var parsed) ? parsed : TradingStrategyType.NoTrade
        };
    }

    private static RotationSlotResponse ToResponse(ActivePairSlotRecord slot) => new()
    {
        SlotIndex = slot.SlotIndex,
        Symbol = slot.Symbol,
        Category = slot.Category,
        Status = FormatEnum(slot.Status),
        Score = slot.Score,
        Reason = slot.Reason,
        ActivatedAt = slot.ActivatedAt,
        CooldownUntil = slot.CooldownUntil,
        UpdatedAt = slot.UpdatedAt
    };

    private static RotationDecisionResponse ToResponse(RotationDecisionRecord decision) => new()
    {
        RotationDecisionId = decision.RotationDecisionId,
        Action = decision.Action,
        Symbol = decision.Symbol,
        CandidateSymbol = decision.CandidateSymbol,
        SlotIndex = decision.SlotIndex,
        CurrentScore = decision.CurrentScore,
        CandidateScore = decision.CandidateScore,
        Reason = decision.Reason,
        CreatedAt = decision.CreatedAt
    };

    private static string FormatEnum<T>(T value)
        where T : struct, Enum
    {
        var text = value.ToString();
        var output = new List<char>(text.Length + 4);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (index > 0 && char.IsUpper(character))
            {
                output.Add('-');
            }

            output.Add(char.ToLower(character, CultureInfo.InvariantCulture));
        }

        return new string(output.ToArray());
    }

    private readonly record struct ReplacementEvaluation(
        bool ShouldReplace,
        RotationPairStatus Status,
        decimal CurrentScore,
        decimal CandidateScore,
        string Reason);

    private readonly record struct OpenLifecycleEvaluation(
        RotationPairStatus Status,
        string Reason);
}

public sealed class RotationStartRequest
{
    public int? ActivePairPoolSize { get; init; }

    public int? MaxActivePositions { get; init; }
}

public sealed class RotationRunResult
{
    public RotationRunResult(bool success, int scannedCount, int activatedCount, string message)
    {
        Success = success;
        ScannedCount = scannedCount;
        ActivatedCount = activatedCount;
        Message = message;
    }

    public bool Success { get; }

    public int ScannedCount { get; }

    public int ActivatedCount { get; }

    public string Message { get; }
}

public sealed class RotationStatusResponse
{
    public bool RotationEnabled { get; init; }

    public int ActivePairPoolSize { get; init; }

    public int ScanIntervalMinutes { get; init; }

    public int MinPairLifetimeMinutes { get; init; }

    public decimal ReplacementScoreGap { get; init; }

    public bool AllowReplaceOnlyWhenFlat { get; init; }

    public int MaxActivePositions { get; init; }

    public required string RotationMode { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? StoppedAt { get; init; }

    public DateTimeOffset? LastScanAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public required IReadOnlyList<RotationSlotResponse> Slots { get; init; }

    public required IReadOnlyList<RotationDecisionResponse> RecentDecisions { get; init; }
}

public sealed class RotationSlotResponse
{
    public int SlotIndex { get; init; }

    public string? Symbol { get; init; }

    public required string Category { get; init; }

    public required string Status { get; init; }

    public decimal Score { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset ActivatedAt { get; init; }

    public DateTimeOffset? CooldownUntil { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class RotationDecisionResponse
{
    public long RotationDecisionId { get; init; }

    public required string Action { get; init; }

    public string? Symbol { get; init; }

    public string? CandidateSymbol { get; init; }

    public int? SlotIndex { get; init; }

    public decimal CurrentScore { get; init; }

    public decimal CandidateScore { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
