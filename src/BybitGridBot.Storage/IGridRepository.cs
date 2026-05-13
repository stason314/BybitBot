using BybitGridBot.Domain;

namespace BybitGridBot.Storage;

public interface IGridRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<GridBotSettings?> GetRuntimeSettingsAsync(CancellationToken cancellationToken);
    Task SaveRuntimeSettingsAsync(GridBotSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<GridLevel>> GetGridLevelsAsync(string symbol, CancellationToken cancellationToken);
    Task SaveGridLevelsAsync(string symbol, IReadOnlyCollection<GridLevel> levels, CancellationToken cancellationToken);
    Task<IReadOnlyList<GridOrder>> GetOrdersAsync(string symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<GridOrder>> GetActiveOrdersAsync(string symbol, CancellationToken cancellationToken);
    Task<GridOrder?> GetOrderByLinkIdAsync(string orderLinkId, CancellationToken cancellationToken);
    Task<GridOrder?> GetActiveOrderAtLevelAsync(string symbol, TradeSide side, decimal price, CancellationToken cancellationToken);
    Task UpsertOrderAsync(GridOrder order, CancellationToken cancellationToken);
    Task<BotState?> GetBotStateAsync(string symbol, CancellationToken cancellationToken);
    Task SaveBotStateAsync(BotState state, CancellationToken cancellationToken);
}
