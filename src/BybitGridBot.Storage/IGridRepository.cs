using BybitGridBot.Domain;

namespace BybitGridBot.Storage;

public interface IGridRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<GridBotSettings?> GetRuntimeSettingsAsync(CancellationToken cancellationToken);
    Task<GridBotSettings?> GetRuntimeSettingsAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<GridBotSettings>> GetRuntimeSettingsProfilesAsync(CancellationToken cancellationToken);
    Task SaveRuntimeSettingsAsync(GridBotSettings settings, CancellationToken cancellationToken);
    Task DeleteRuntimeSettingsAsync(string symbol, CancellationToken cancellationToken);
    Task<RotationStateRecord?> GetRotationStateAsync(CancellationToken cancellationToken);
    Task SaveRotationStateAsync(RotationStateRecord state, CancellationToken cancellationToken);
    Task<IReadOnlyList<ActivePairSlotRecord>> GetActivePairSlotsAsync(CancellationToken cancellationToken);
    Task UpsertActivePairSlotAsync(ActivePairSlotRecord slot, CancellationToken cancellationToken);
    Task AddPairRotationHistoryAsync(PairRotationHistoryRecord history, CancellationToken cancellationToken);
    Task UpsertStrategyPerformanceScoreAsync(StrategyPerformanceScoreRecord score, CancellationToken cancellationToken);
    Task<IReadOnlyList<StrategyPerformanceScoreRecord>> GetStrategyPerformanceScoresAsync(int limit, CancellationToken cancellationToken);
    Task UpsertPairStrategyScoreAsync(PairStrategyScoreRecord score, CancellationToken cancellationToken);
    Task<IReadOnlyList<PairStrategyScoreRecord>> GetPairStrategyScoresAsync(int limit, CancellationToken cancellationToken);
    Task AddRotationDecisionAsync(RotationDecisionRecord decision, CancellationToken cancellationToken);
    Task<IReadOnlyList<RotationDecisionRecord>> GetRotationDecisionsAsync(int limit, CancellationToken cancellationToken);
    Task<FuturesBotSettings?> GetFuturesSettingsAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<FuturesBotSettings>> GetFuturesSettingsProfilesAsync(CancellationToken cancellationToken);
    Task SaveFuturesSettingsAsync(FuturesBotSettings settings, CancellationToken cancellationToken);
    Task DeleteFuturesSettingsAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<FuturesOrderRecord>> GetFuturesOrdersAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<FuturesOrderRecord>> GetActiveFuturesOrdersAsync(string symbol, CancellationToken cancellationToken);
    Task<FuturesOrderRecord?> GetFuturesOrderByLinkIdAsync(string orderLinkId, CancellationToken cancellationToken);
    Task UpsertFuturesOrderAsync(FuturesOrderRecord order, CancellationToken cancellationToken);
    Task<FuturesPositionSnapshot?> GetFuturesPositionAsync(string symbol, CancellationToken cancellationToken);
    Task UpsertFuturesPositionAsync(FuturesPositionSnapshot position, TradingMode tradingMode, CancellationToken cancellationToken);
    Task AddFuturesFillAsync(FuturesFillRecord fill, CancellationToken cancellationToken);
    Task<bool> FuturesFillExistsAsync(string execId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FuturesFillRecord>> GetFuturesFillsAsync(string symbol, int limit, CancellationToken cancellationToken);
    Task<ExecutionTurnoverStats> GetFuturesFillTurnoverAsync(string symbol, DateOnly today, CancellationToken cancellationToken);
    Task<IReadOnlyList<FuturesRiskDecisionRecord>> GetFuturesRiskDecisionsAsync(string symbol, int limit, CancellationToken cancellationToken);
    Task AddFuturesRiskDecisionAsync(FuturesRiskDecisionRecord decision, CancellationToken cancellationToken);
    Task ClearFuturesPaperHistoryAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<GridLevel>> GetGridLevelsAsync(string symbol, CancellationToken cancellationToken);
    Task SaveGridLevelsAsync(string symbol, IReadOnlyCollection<GridLevel> levels, CancellationToken cancellationToken);
    Task<IReadOnlyList<GridOrder>> GetOrdersAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<GridOrder>> GetActiveOrdersAsync(string symbol, CancellationToken cancellationToken);
    Task<GridOrder?> GetOrderByLinkIdAsync(string orderLinkId, CancellationToken cancellationToken);
    Task<GridOrder?> GetActiveOrderAtLevelAsync(string symbol, TradeSide side, decimal price, CancellationToken cancellationToken);
    Task UpsertOrderAsync(GridOrder order, CancellationToken cancellationToken);
    Task<bool> SpotExecutionExistsAsync(string execId, CancellationToken cancellationToken);
    Task<bool> AddSpotExecutionAsync(SpotExecutionRecord execution, CancellationToken cancellationToken);
    Task<ExecutionTurnoverStats> GetSpotExecutionTurnoverAsync(string symbol, DateOnly today, CancellationToken cancellationToken);
    Task<StrategyCooldownRecord?> GetActiveStrategyCooldownAsync(string symbol, string strategyType, DateTimeOffset now, CancellationToken cancellationToken);
    Task UpsertStrategyCooldownAsync(StrategyCooldownRecord cooldown, CancellationToken cancellationToken);
    Task<int> ResetSpotStatisticsAsync(CancellationToken cancellationToken);
    Task<NoTradeReasonRecord?> GetLatestNoTradeReasonAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<NoTradeReasonRecord>> GetNoTradeReasonsAsync(string symbol, int limit, CancellationToken cancellationToken);
    Task AddNoTradeReasonAsync(NoTradeReasonRecord reason, CancellationToken cancellationToken);
    Task<BotState?> GetBotStateAsync(string symbol, CancellationToken cancellationToken);
    Task SaveBotStateAsync(BotState state, CancellationToken cancellationToken);
}
