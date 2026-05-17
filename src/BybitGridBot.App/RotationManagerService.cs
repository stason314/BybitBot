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
        var updated = current with
        {
            RotationEnabled = true,
            ActivePairPoolSize = Math.Max(1, activePairPoolSize),
            StartedAt = now,
            StoppedAt = null,
            UpdatedAt = now
        };

        await _repository.SaveRotationStateAsync(updated, cancellationToken);
        await AddDecisionAsync(DecisionStart, null, null, null, 0m, 0m, "Rotation started.", now, cancellationToken);
        return await BuildStatusAsync(updated, cancellationToken);
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
        var candidates = scan.Items
            .Where(IsCandidateStrategyEnabled)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.StrategyFitScore)
            .ToArray();

        await SaveCandidateScoresAsync(candidates, now, cancellationToken);

        var profiles = (await _repository.GetRuntimeSettingsProfilesAsync(cancellationToken))
            .ToDictionary(profile => profile.Symbol, StringComparer.OrdinalIgnoreCase);
        var activeSymbols = profiles.Values
            .Where(IsBuyCapableProfile)
            .Select(profile => profile.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slots = (await _repository.GetActivePairSlotsAsync(cancellationToken))
            .ToDictionary(slot => slot.SlotIndex);

        var activated = 0;
        for (var slotIndex = 1; slotIndex <= state.ActivePairPoolSize; slotIndex++)
        {
            slots.TryGetValue(slotIndex, out var slot);
            if (slot is not null &&
                !string.IsNullOrWhiteSpace(slot.Symbol) &&
                activeSymbols.Contains(slot.Symbol))
            {
                await UpsertSlotAsync(slot with
                {
                    Status = RotationPairStatus.Active,
                    Reason = "Active profile kept by rotation.",
                    UpdatedAt = now
                }, cancellationToken);
                continue;
            }

            var candidate = candidates.FirstOrDefault(item => !activeSymbols.Contains(item.Symbol));
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

            var previousSymbol = slot?.Symbol;
            var settings = BuildSettings(candidate, now);
            await _repository.SaveRuntimeSettingsAsync(settings, cancellationToken);
            activeSymbols.Add(candidate.Symbol);
            activated++;

            await UpsertSlotAsync(new ActivePairSlotRecord
            {
                SlotIndex = slotIndex,
                Symbol = candidate.Symbol,
                Category = candidate.Category,
                Status = RotationPairStatus.Active,
                Score = candidate.Score,
                Reason = $"Activated by scanner rank. {string.Join("; ", candidate.Reasons.Take(3))}",
                ActivatedAt = now,
                UpdatedAt = now
            }, cancellationToken);

            await _repository.AddPairRotationHistoryAsync(new PairRotationHistoryRecord
            {
                SlotIndex = slotIndex,
                PreviousSymbol = previousSymbol,
                NewSymbol = candidate.Symbol,
                Reason = "Activated into empty rotation slot.",
                PreviousScore = slot?.Score ?? 0m,
                NewScore = candidate.Score,
                CreatedAt = now
            }, cancellationToken);
            await AddDecisionAsync(DecisionActivate, previousSymbol, candidate.Symbol, slotIndex, slot?.Score ?? 0m, candidate.Score, "Activated into empty rotation slot.", now, cancellationToken);
        }

        var updatedState = state with
        {
            LastScanAt = now,
            UpdatedAt = now
        };
        await _repository.SaveRotationStateAsync(updatedState, cancellationToken);

        var message = activated == 0
            ? "Rotation scan completed; no empty slot activation needed."
            : $"Rotation scan completed; activated {activated} pair(s).";
        return new RotationRunResult(true, scan.ScannedCount, activated, message);
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
            not TradingStrategyType.ReduceOnly;

    private static bool IsCandidateStrategyEnabled(MarketScanItem item)
    {
        var strategyType = ParseTradingStrategyType(item.RecommendedStrategy);
        return strategyType is not TradingStrategyType.NoTrade and
            not TradingStrategyType.Pause and
            not TradingStrategyType.ReduceOnly;
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
        where T : struct, Enum =>
        value.ToString().ToLower(CultureInfo.InvariantCulture);
}

public sealed class RotationStartRequest
{
    public int? ActivePairPoolSize { get; init; }
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
