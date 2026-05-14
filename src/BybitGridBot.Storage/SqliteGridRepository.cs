using System.Globalization;
using BybitGridBot.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BybitGridBot.Storage;

public sealed class SqliteGridRepository : IGridRepository
{
    private readonly ILogger<SqliteGridRepository> _logger;
    private readonly string _connectionString;

    public SqliteGridRepository(string databasePath, ILogger<SqliteGridRepository> logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var sql = """
            CREATE TABLE IF NOT EXISTS grid_levels (
                symbol TEXT NOT NULL,
                level_index INTEGER NOT NULL,
                price TEXT NOT NULL,
                PRIMARY KEY(symbol, level_index)
            );

            CREATE TABLE IF NOT EXISTS grid_orders (
                order_link_id TEXT NOT NULL PRIMARY KEY,
                bybit_order_id TEXT NULL,
                symbol TEXT NOT NULL,
                category TEXT NOT NULL,
                side TEXT NOT NULL,
                price TEXT NOT NULL,
                quantity TEXT NOT NULL,
                filled_quantity TEXT NOT NULL,
                average_fill_price TEXT NOT NULL,
                fee_paid TEXT NOT NULL,
                status TEXT NOT NULL,
                trading_mode TEXT NOT NULL,
                parent_order_link_id TEXT NULL,
                realized_pnl TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                filled_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS bot_state (
                symbol TEXT NOT NULL PRIMARY KEY,
                trading_mode TEXT NOT NULL,
                is_initialized INTEGER NOT NULL,
                is_paused INTEGER NOT NULL,
                pause_reason TEXT NULL,
                last_observed_price TEXT NULL,
                base_asset_quantity TEXT NOT NULL,
                quote_asset_balance TEXT NOT NULL,
                average_entry_price TEXT NOT NULL,
                total_realized_pnl TEXT NOT NULL,
                daily_realized_pnl TEXT NOT NULL,
                daily_pnl_date TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runtime_settings (
                settings_id TEXT NOT NULL PRIMARY KEY,
                symbol TEXT NOT NULL,
                category TEXT NOT NULL,
                strategy_selection_mode TEXT NOT NULL DEFAULT 'Manual',
                strategy_type TEXT NOT NULL DEFAULT 'Grid',
                strategy_config_json TEXT NOT NULL DEFAULT '{}',
                lower_price TEXT NOT NULL,
                upper_price TEXT NOT NULL,
                step TEXT NOT NULL,
                order_size_usdt TEXT NOT NULL,
                stop_lower_price TEXT NOT NULL,
                stop_upper_price TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            UPDATE OR IGNORE runtime_settings
            SET settings_id = symbol
            WHERE settings_id = 'active';

            DELETE FROM runtime_settings
            WHERE settings_id = 'active';
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "runtime_settings", "strategy_selection_mode", "TEXT NOT NULL DEFAULT 'Manual'", cancellationToken);
        await EnsureColumnAsync(connection, "runtime_settings", "strategy_type", "TEXT NOT NULL DEFAULT 'Grid'", cancellationToken);
        await EnsureColumnAsync(connection, "runtime_settings", "strategy_config_json", "TEXT NOT NULL DEFAULT '{}'", cancellationToken);
        _logger.LogInformation("SQLite repository initialized.");
    }

    public async Task<GridBotSettings?> GetRuntimeSettingsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, category, strategy_selection_mode, strategy_type, strategy_config_json, lower_price, upper_price, step, order_size_usdt, stop_lower_price, stop_upper_price, updated_at
            FROM runtime_settings
            ORDER BY symbol
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRuntimeSettings(reader);
    }

    public async Task<GridBotSettings?> GetRuntimeSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, category, strategy_selection_mode, strategy_type, strategy_config_json, lower_price, upper_price, step, order_size_usdt, stop_lower_price, stop_upper_price, updated_at
            FROM runtime_settings
            WHERE settings_id = $settings_id
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$settings_id", symbol);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRuntimeSettings(reader);
    }

    public async Task<IReadOnlyList<GridBotSettings>> GetRuntimeSettingsProfilesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, category, strategy_selection_mode, strategy_type, strategy_config_json, lower_price, upper_price, step, order_size_usdt, stop_lower_price, stop_upper_price, updated_at
            FROM runtime_settings
            ORDER BY symbol;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<GridBotSettings>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRuntimeSettings(reader));
        }

        return result;
    }

    public async Task SaveRuntimeSettingsAsync(GridBotSettings settings, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO runtime_settings (
                settings_id, symbol, category, strategy_selection_mode, strategy_type, strategy_config_json, lower_price, upper_price, step, order_size_usdt, stop_lower_price, stop_upper_price, updated_at
            )
            VALUES (
                $settings_id, $symbol, $category, $strategy_selection_mode, $strategy_type, $strategy_config_json, $lower_price, $upper_price, $step, $order_size_usdt, $stop_lower_price, $stop_upper_price, $updated_at
            )
            ON CONFLICT(settings_id) DO UPDATE SET
                symbol = excluded.symbol,
                category = excluded.category,
                strategy_selection_mode = excluded.strategy_selection_mode,
                strategy_type = excluded.strategy_type,
                strategy_config_json = excluded.strategy_config_json,
                lower_price = excluded.lower_price,
                upper_price = excluded.upper_price,
                step = excluded.step,
                order_size_usdt = excluded.order_size_usdt,
                stop_lower_price = excluded.stop_lower_price,
                stop_upper_price = excluded.stop_upper_price,
                updated_at = excluded.updated_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$settings_id", settings.Symbol);
        command.Parameters.AddWithValue("$symbol", settings.Symbol);
        command.Parameters.AddWithValue("$category", settings.Category);
        command.Parameters.AddWithValue("$strategy_selection_mode", settings.StrategySelectionMode.ToString());
        command.Parameters.AddWithValue("$strategy_type", settings.StrategyType.ToString());
        command.Parameters.AddWithValue("$strategy_config_json", string.IsNullOrWhiteSpace(settings.StrategyConfigJson) ? "{}" : settings.StrategyConfigJson);
        command.Parameters.AddWithValue("$lower_price", FormatDecimal(settings.LowerPrice));
        command.Parameters.AddWithValue("$upper_price", FormatDecimal(settings.UpperPrice));
        command.Parameters.AddWithValue("$step", FormatDecimal(settings.Step));
        command.Parameters.AddWithValue("$order_size_usdt", FormatDecimal(settings.OrderSizeUsdt));
        command.Parameters.AddWithValue("$stop_lower_price", FormatDecimal(settings.StopLowerPrice));
        command.Parameters.AddWithValue("$stop_upper_price", FormatDecimal(settings.StopUpperPrice));
        command.Parameters.AddWithValue("$updated_at", settings.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteRuntimeSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM runtime_settings WHERE settings_id = $settings_id;";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$settings_id", symbol);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GridLevel>> GetGridLevelsAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT level_index, price
            FROM grid_levels
            WHERE symbol = $symbol
            ORDER BY level_index;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<GridLevel>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new GridLevel(reader.GetInt32(0), ParseDecimal(reader.GetString(1))));
        }

        return result;
    }

    public async Task SaveGridLevelsAsync(string symbol, IReadOnlyCollection<GridLevel> levels, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM grid_levels WHERE symbol = $symbol;";
        deleteCommand.Parameters.AddWithValue("$symbol", symbol);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        foreach (var level in levels)
        {
            var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO grid_levels (symbol, level_index, price)
                VALUES ($symbol, $level_index, $price);
                """;
            insertCommand.Parameters.AddWithValue("$symbol", symbol);
            insertCommand.Parameters.AddWithValue("$level_index", level.Index);
            insertCommand.Parameters.AddWithValue("$price", FormatDecimal(level.Price));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GridOrder>> GetOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT order_link_id, bybit_order_id, symbol, category, side, price, quantity, filled_quantity,
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id, realized_pnl,
                   created_at, updated_at, filled_at
            FROM grid_orders
            WHERE symbol = $symbol
            ORDER BY created_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);

        return await ReadOrdersAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<GridOrder>> GetActiveOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT order_link_id, bybit_order_id, symbol, category, side, price, quantity, filled_quantity,
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id, realized_pnl,
                   created_at, updated_at, filled_at
            FROM grid_orders
            WHERE symbol = $symbol AND status IN ('New', 'PartiallyFilled')
            ORDER BY created_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);

        return await ReadOrdersAsync(command, cancellationToken);
    }

    public async Task<GridOrder?> GetOrderByLinkIdAsync(string orderLinkId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT order_link_id, bybit_order_id, symbol, category, side, price, quantity, filled_quantity,
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id, realized_pnl,
                   created_at, updated_at, filled_at
            FROM grid_orders
            WHERE order_link_id = $order_link_id
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$order_link_id", orderLinkId);

        var items = await ReadOrdersAsync(command, cancellationToken);
        return items.FirstOrDefault();
    }

    public async Task<GridOrder?> GetActiveOrderAtLevelAsync(string symbol, TradeSide side, decimal price, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT order_link_id, bybit_order_id, symbol, category, side, price, quantity, filled_quantity,
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id, realized_pnl,
                   created_at, updated_at, filled_at
            FROM grid_orders
            WHERE symbol = $symbol
              AND side = $side
              AND price = $price
              AND status IN ('New', 'PartiallyFilled')
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$side", side.ToString());
        command.Parameters.AddWithValue("$price", FormatDecimal(price));

        var items = await ReadOrdersAsync(command, cancellationToken);
        return items.FirstOrDefault();
    }

    public async Task UpsertOrderAsync(GridOrder order, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO grid_orders (
                order_link_id, bybit_order_id, symbol, category, side, price, quantity, filled_quantity,
                average_fill_price, fee_paid, status, trading_mode, parent_order_link_id, realized_pnl,
                created_at, updated_at, filled_at
            )
            VALUES (
                $order_link_id, $bybit_order_id, $symbol, $category, $side, $price, $quantity, $filled_quantity,
                $average_fill_price, $fee_paid, $status, $trading_mode, $parent_order_link_id, $realized_pnl,
                $created_at, $updated_at, $filled_at
            )
            ON CONFLICT(order_link_id) DO UPDATE SET
                bybit_order_id = excluded.bybit_order_id,
                symbol = excluded.symbol,
                category = excluded.category,
                side = excluded.side,
                price = excluded.price,
                quantity = excluded.quantity,
                filled_quantity = excluded.filled_quantity,
                average_fill_price = excluded.average_fill_price,
                fee_paid = excluded.fee_paid,
                status = excluded.status,
                trading_mode = excluded.trading_mode,
                parent_order_link_id = excluded.parent_order_link_id,
                realized_pnl = excluded.realized_pnl,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                filled_at = excluded.filled_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$order_link_id", order.OrderLinkId);
        command.Parameters.AddWithValue("$bybit_order_id", (object?)order.BybitOrderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$symbol", order.Symbol);
        command.Parameters.AddWithValue("$category", order.Category);
        command.Parameters.AddWithValue("$side", order.Side.ToString());
        command.Parameters.AddWithValue("$price", FormatDecimal(order.Price));
        command.Parameters.AddWithValue("$quantity", FormatDecimal(order.Quantity));
        command.Parameters.AddWithValue("$filled_quantity", FormatDecimal(order.FilledQuantity));
        command.Parameters.AddWithValue("$average_fill_price", FormatDecimal(order.AverageFillPrice));
        command.Parameters.AddWithValue("$fee_paid", FormatDecimal(order.FeePaid));
        command.Parameters.AddWithValue("$status", order.Status.ToString());
        command.Parameters.AddWithValue("$trading_mode", order.TradingMode.ToString());
        command.Parameters.AddWithValue("$parent_order_link_id", (object?)order.ParentOrderLinkId ?? DBNull.Value);
        command.Parameters.AddWithValue("$realized_pnl", FormatDecimal(order.RealizedPnl));
        command.Parameters.AddWithValue("$created_at", order.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", order.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$filled_at", order.FilledAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BotState?> GetBotStateAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, trading_mode, is_initialized, is_paused, pause_reason, last_observed_price,
                   base_asset_quantity, quote_asset_balance, average_entry_price, total_realized_pnl,
                   daily_realized_pnl, daily_pnl_date, updated_at
            FROM bot_state
            WHERE symbol = $symbol
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BotState
        {
            Symbol = reader.GetString(0),
            TradingMode = Enum.Parse<TradingMode>(reader.GetString(1), true),
            IsInitialized = reader.GetBoolean(2),
            IsPaused = reader.GetBoolean(3),
            PauseReason = reader.IsDBNull(4) ? null : reader.GetString(4),
            LastObservedPrice = reader.IsDBNull(5) ? null : ParseDecimal(reader.GetString(5)),
            BaseAssetQuantity = ParseDecimal(reader.GetString(6)),
            QuoteAssetBalance = ParseDecimal(reader.GetString(7)),
            AverageEntryPrice = ParseDecimal(reader.GetString(8)),
            TotalRealizedPnl = ParseDecimal(reader.GetString(9)),
            DailyRealizedPnl = ParseDecimal(reader.GetString(10)),
            DailyPnlDate = DateOnly.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)
        };
    }

    public async Task SaveBotStateAsync(BotState state, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bot_state (
                symbol, trading_mode, is_initialized, is_paused, pause_reason, last_observed_price,
                base_asset_quantity, quote_asset_balance, average_entry_price, total_realized_pnl,
                daily_realized_pnl, daily_pnl_date, updated_at
            )
            VALUES (
                $symbol, $trading_mode, $is_initialized, $is_paused, $pause_reason, $last_observed_price,
                $base_asset_quantity, $quote_asset_balance, $average_entry_price, $total_realized_pnl,
                $daily_realized_pnl, $daily_pnl_date, $updated_at
            )
            ON CONFLICT(symbol) DO UPDATE SET
                trading_mode = excluded.trading_mode,
                is_initialized = excluded.is_initialized,
                is_paused = excluded.is_paused,
                pause_reason = excluded.pause_reason,
                last_observed_price = excluded.last_observed_price,
                base_asset_quantity = excluded.base_asset_quantity,
                quote_asset_balance = excluded.quote_asset_balance,
                average_entry_price = excluded.average_entry_price,
                total_realized_pnl = excluded.total_realized_pnl,
                daily_realized_pnl = excluded.daily_realized_pnl,
                daily_pnl_date = excluded.daily_pnl_date,
                updated_at = excluded.updated_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", state.Symbol);
        command.Parameters.AddWithValue("$trading_mode", state.TradingMode.ToString());
        command.Parameters.AddWithValue("$is_initialized", state.IsInitialized);
        command.Parameters.AddWithValue("$is_paused", state.IsPaused);
        command.Parameters.AddWithValue("$pause_reason", (object?)state.PauseReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_observed_price", state.LastObservedPrice is null ? (object)DBNull.Value : FormatDecimal(state.LastObservedPrice.Value));
        command.Parameters.AddWithValue("$base_asset_quantity", FormatDecimal(state.BaseAssetQuantity));
        command.Parameters.AddWithValue("$quote_asset_balance", FormatDecimal(state.QuoteAssetBalance));
        command.Parameters.AddWithValue("$average_entry_price", FormatDecimal(state.AverageEntryPrice));
        command.Parameters.AddWithValue("$total_realized_pnl", FormatDecimal(state.TotalRealizedPnl));
        command.Parameters.AddWithValue("$daily_realized_pnl", FormatDecimal(state.DailyRealizedPnl));
        command.Parameters.AddWithValue("$daily_pnl_date", state.DailyPnlDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", state.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static GridBotSettings ReadRuntimeSettings(SqliteDataReader reader)
    {
        return new GridBotSettings
        {
            Symbol = reader.GetString(0),
            Category = reader.GetString(1),
            StrategySelectionMode = ParseEnum(reader.GetString(2), StrategySelectionMode.Manual),
            StrategyType = ParseEnum(reader.GetString(3), TradingStrategyType.Grid),
            StrategyConfigJson = reader.GetString(4),
            LowerPrice = ParseDecimal(reader.GetString(5)),
            UpperPrice = ParseDecimal(reader.GetString(6)),
            Step = ParseDecimal(reader.GetString(7)),
            OrderSizeUsdt = ParseDecimal(reader.GetString(8)),
            StopLowerPrice = ParseDecimal(reader.GetString(9)),
            StopUpperPrice = ParseDecimal(reader.GetString(10)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture)
        };
    }

    private async Task<List<GridOrder>> ReadOrdersAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<GridOrder>();

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new GridOrder
            {
                OrderLinkId = reader.GetString(0),
                BybitOrderId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Symbol = reader.GetString(2),
                Category = reader.GetString(3),
                Side = Enum.Parse<TradeSide>(reader.GetString(4), true),
                Price = ParseDecimal(reader.GetString(5)),
                Quantity = ParseDecimal(reader.GetString(6)),
                FilledQuantity = ParseDecimal(reader.GetString(7)),
                AverageFillPrice = ParseDecimal(reader.GetString(8)),
                FeePaid = ParseDecimal(reader.GetString(9)),
                Status = Enum.Parse<OrderStatus>(reader.GetString(10), true),
                TradingMode = Enum.Parse<TradingMode>(reader.GetString(11), true),
                ParentOrderLinkId = reader.IsDBNull(12) ? null : reader.GetString(12),
                RealizedPnl = ParseDecimal(reader.GetString(13)),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture),
                FilledAt = reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16), CultureInfo.InvariantCulture)
            });
        }

        return result;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        var hasColumn = false;
        await using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await pragmaCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
}
