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
                strategy_source TEXT NOT NULL DEFAULT 'Grid',
                position_side TEXT NULL,
                reduce_only INTEGER NOT NULL DEFAULT 0,
                position_idx INTEGER NOT NULL DEFAULT 0,
                leverage TEXT NOT NULL DEFAULT '0',
                margin_mode TEXT NULL,
                entry_price TEXT NOT NULL DEFAULT '0',
                mark_price TEXT NOT NULL DEFAULT '0',
                liquidation_price TEXT NOT NULL DEFAULT '0',
                unrealized_pnl TEXT NOT NULL DEFAULT '0',
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
                position_side TEXT NULL,
                reduce_only INTEGER NOT NULL DEFAULT 0,
                position_idx INTEGER NOT NULL DEFAULT 0,
                leverage TEXT NOT NULL DEFAULT '0',
                margin_mode TEXT NULL,
                entry_price TEXT NOT NULL DEFAULT '0',
                mark_price TEXT NOT NULL DEFAULT '0',
                liquidation_price TEXT NOT NULL DEFAULT '0',
                unrealized_pnl TEXT NOT NULL DEFAULT '0',
                total_realized_pnl TEXT NOT NULL,
                daily_realized_pnl TEXT NOT NULL,
                peak_equity_usdt TEXT NOT NULL DEFAULT '0',
                current_drawdown_usdt TEXT NOT NULL DEFAULT '0',
                current_drawdown_percent TEXT NOT NULL DEFAULT '0',
                profit_protection_peak_price TEXT NOT NULL DEFAULT '0',
                profit_protection_trailing_stop_price TEXT NOT NULL DEFAULT '0',
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

            CREATE TABLE IF NOT EXISTS futures_settings (
                settings_id TEXT NOT NULL PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 1,
                symbol TEXT NOT NULL,
                category TEXT NOT NULL,
                strategy_type TEXT NOT NULL,
                strategy_config_json TEXT NOT NULL DEFAULT '{}',
                leverage TEXT NOT NULL,
                margin_mode TEXT NOT NULL,
                position_mode TEXT NOT NULL,
                direction TEXT NOT NULL,
                max_notional_usdt TEXT NOT NULL,
                max_margin_usdt TEXT NOT NULL,
                stop_loss_percent TEXT NOT NULL,
                take_profit_percent TEXT NOT NULL,
                liquidation_buffer_percent TEXT NOT NULL,
                reduce_only_enabled INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS futures_orders (
                order_link_id TEXT NOT NULL PRIMARY KEY,
                bybit_order_id TEXT NULL,
                symbol TEXT NOT NULL,
                category TEXT NOT NULL,
                action TEXT NOT NULL,
                side TEXT NOT NULL,
                price TEXT NOT NULL,
                quantity TEXT NOT NULL,
                filled_quantity TEXT NOT NULL,
                average_fill_price TEXT NOT NULL,
                fee_paid TEXT NOT NULL,
                status TEXT NOT NULL,
                trading_mode TEXT NOT NULL,
                position_side TEXT NOT NULL,
                reduce_only INTEGER NOT NULL,
                position_idx INTEGER NOT NULL,
                leverage TEXT NOT NULL,
                margin_mode TEXT NOT NULL,
                stop_loss_price TEXT NOT NULL,
                take_profit_price TEXT NOT NULL,
                realized_pnl TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                filled_at TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS futures_positions (
                symbol TEXT NOT NULL PRIMARY KEY,
                category TEXT NOT NULL,
                side TEXT NOT NULL,
                size TEXT NOT NULL,
                entry_price TEXT NOT NULL,
                mark_price TEXT NOT NULL,
                liquidation_price TEXT NOT NULL,
                position_value_usdt TEXT NOT NULL,
                margin_used_usdt TEXT NOT NULL,
                leverage TEXT NOT NULL,
                unrealized_pnl TEXT NOT NULL,
                realized_pnl TEXT NOT NULL,
                funding TEXT NOT NULL DEFAULT '0',
                position_idx INTEGER NOT NULL,
                trading_mode TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS futures_fills (
                fill_id INTEGER PRIMARY KEY AUTOINCREMENT,
                exec_id TEXT NULL,
                order_link_id TEXT NOT NULL,
                symbol TEXT NOT NULL,
                action TEXT NOT NULL,
                side TEXT NOT NULL,
                exec_type TEXT NOT NULL DEFAULT 'Trade',
                quantity TEXT NOT NULL,
                price TEXT NOT NULL,
                fee TEXT NOT NULL,
                realized_pnl TEXT NOT NULL,
                funding TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS futures_risk_decisions (
                risk_decision_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                source TEXT NOT NULL,
                order_link_id TEXT NULL,
                action TEXT NULL,
                is_allowed INTEGER NOT NULL,
                reason TEXT NOT NULL,
                severity TEXT NOT NULL,
                suggested_action TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS strategy_decisions (
                decision_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                market_regime TEXT NOT NULL,
                selected_strategy TEXT NOT NULL,
                scores_json TEXT NOT NULL,
                reason TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS market_regimes (
                regime_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                regime TEXT NOT NULL,
                confidence TEXT NOT NULL,
                metrics_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS market_phases (
                phase_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                phase TEXT NOT NULL,
                confidence TEXT NOT NULL,
                score TEXT NOT NULL,
                suggested_strategy TEXT NOT NULL,
                key_levels_json TEXT NOT NULL,
                reason TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS strategy_switches (
                switch_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                previous_strategy TEXT NULL,
                selected_strategy TEXT NOT NULL,
                market_phase TEXT NOT NULL,
                score TEXT NOT NULL,
                confidence TEXT NOT NULL,
                reason TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS no_trade_reasons (
                reason_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                strategy_type TEXT NULL,
                reason_code TEXT NOT NULL,
                reason TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS strategy_performance (
                performance_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                strategy_type TEXT NOT NULL,
                gross_pnl TEXT NOT NULL,
                fees_paid TEXT NOT NULL,
                slippage_cost TEXT NOT NULL,
                net_pnl TEXT NOT NULL,
                max_drawdown TEXT NOT NULL,
                trades_count INTEGER NOT NULL,
                win_rate TEXT NOT NULL,
                profit_factor TEXT NOT NULL,
                average_win TEXT NOT NULL,
                average_loss TEXT NOT NULL,
                time_in_strategy_percent TEXT NOT NULL,
                pause_time_percent TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS strategy_daily_performance (
                symbol TEXT NOT NULL,
                strategy_type TEXT NOT NULL,
                performance_date TEXT NOT NULL,
                gross_pnl TEXT NOT NULL,
                fees_paid TEXT NOT NULL,
                net_pnl TEXT NOT NULL,
                trades_count INTEGER NOT NULL,
                win_rate TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(symbol, strategy_type, performance_date)
            );

            CREATE TABLE IF NOT EXISTS signals (
                signal_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                type TEXT NOT NULL,
                direction TEXT NULL,
                strength TEXT NOT NULL,
                confidence TEXT NOT NULL,
                reason TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS capital_allocations (
                allocation_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                strategy_type TEXT NOT NULL,
                requested_usdt TEXT NOT NULL,
                allocated_usdt TEXT NOT NULL,
                is_allowed INTEGER NOT NULL,
                reason TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS trade_intents (
                intent_id INTEGER PRIMARY KEY AUTOINCREMENT,
                order_link_id TEXT NULL,
                symbol TEXT NOT NULL,
                strategy_type TEXT NOT NULL,
                side TEXT NOT NULL,
                order_type TEXT NOT NULL,
                price TEXT NOT NULL,
                quantity TEXT NOT NULL,
                reason TEXT NOT NULL,
                confidence TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS risk_decisions (
                risk_decision_id INTEGER PRIMARY KEY AUTOINCREMENT,
                symbol TEXT NOT NULL,
                order_link_id TEXT NULL,
                is_allowed INTEGER NOT NULL,
                reason TEXT NOT NULL,
                severity TEXT NOT NULL,
                suggested_action TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS orders (
                order_link_id TEXT NOT NULL PRIMARY KEY,
                symbol TEXT NOT NULL,
                strategy_type TEXT NULL,
                side TEXT NOT NULL,
                price TEXT NOT NULL,
                quantity TEXT NOT NULL,
                position_side TEXT NULL,
                reduce_only INTEGER NOT NULL DEFAULT 0,
                position_idx INTEGER NOT NULL DEFAULT 0,
                leverage TEXT NOT NULL DEFAULT '0',
                margin_mode TEXT NULL,
                entry_price TEXT NOT NULL DEFAULT '0',
                mark_price TEXT NOT NULL DEFAULT '0',
                liquidation_price TEXT NOT NULL DEFAULT '0',
                unrealized_pnl TEXT NOT NULL DEFAULT '0',
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS positions (
                symbol TEXT NOT NULL PRIMARY KEY,
                base_asset_quantity TEXT NOT NULL,
                average_entry_price TEXT NOT NULL,
                quote_asset_balance TEXT NOT NULL,
                current_price TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS daily_pnl (
                symbol TEXT NOT NULL,
                pnl_date TEXT NOT NULL,
                realized_pnl TEXT NOT NULL,
                fees_paid TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(symbol, pnl_date)
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
        await EnsureColumnAsync(connection, "grid_orders", "strategy_source", "TEXT NOT NULL DEFAULT 'Grid'", cancellationToken);
        await BackfillGridOrderStrategySourcesAsync(connection, cancellationToken);
        await EnsureFuturesOrderColumnsAsync(connection, "grid_orders", cancellationToken);
        await EnsureFuturesOrderColumnsAsync(connection, "orders", cancellationToken);
        await EnsureFuturesStateColumnsAsync(connection, "bot_state", cancellationToken);
        await EnsureColumnAsync(connection, "futures_settings", "enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureColumnAsync(connection, "futures_fills", "exec_id", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, "futures_fills", "exec_type", "TEXT NOT NULL DEFAULT 'Trade'", cancellationToken);
        await EnsureColumnAsync(connection, "futures_positions", "funding", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureUniqueFuturesFillExecIndexAsync(connection, cancellationToken);
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

    public async Task<FuturesBotSettings?> GetFuturesSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT enabled, symbol, category, strategy_type, strategy_config_json, leverage, margin_mode, position_mode,
                   direction, max_notional_usdt, max_margin_usdt, stop_loss_percent, take_profit_percent,
                   liquidation_buffer_percent, reduce_only_enabled, updated_at
            FROM futures_settings
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

        return ReadFuturesSettings(reader);
    }

    public async Task<IReadOnlyList<FuturesBotSettings>> GetFuturesSettingsProfilesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT enabled, symbol, category, strategy_type, strategy_config_json, leverage, margin_mode, position_mode,
                   direction, max_notional_usdt, max_margin_usdt, stop_loss_percent, take_profit_percent,
                   liquidation_buffer_percent, reduce_only_enabled, updated_at
            FROM futures_settings
            ORDER BY symbol;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FuturesBotSettings>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadFuturesSettings(reader));
        }

        return result;
    }

    public async Task SaveFuturesSettingsAsync(FuturesBotSettings settings, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO futures_settings (
                settings_id, enabled, symbol, category, strategy_type, strategy_config_json, leverage, margin_mode, position_mode,
                direction, max_notional_usdt, max_margin_usdt, stop_loss_percent, take_profit_percent,
                liquidation_buffer_percent, reduce_only_enabled, updated_at
            )
            VALUES (
                $settings_id, $enabled, $symbol, $category, $strategy_type, $strategy_config_json, $leverage, $margin_mode, $position_mode,
                $direction, $max_notional_usdt, $max_margin_usdt, $stop_loss_percent, $take_profit_percent,
                $liquidation_buffer_percent, $reduce_only_enabled, $updated_at
            )
            ON CONFLICT(settings_id) DO UPDATE SET
                enabled = excluded.enabled,
                symbol = excluded.symbol,
                category = excluded.category,
                strategy_type = excluded.strategy_type,
                strategy_config_json = excluded.strategy_config_json,
                leverage = excluded.leverage,
                margin_mode = excluded.margin_mode,
                position_mode = excluded.position_mode,
                direction = excluded.direction,
                max_notional_usdt = excluded.max_notional_usdt,
                max_margin_usdt = excluded.max_margin_usdt,
                stop_loss_percent = excluded.stop_loss_percent,
                take_profit_percent = excluded.take_profit_percent,
                liquidation_buffer_percent = excluded.liquidation_buffer_percent,
                reduce_only_enabled = excluded.reduce_only_enabled,
                updated_at = excluded.updated_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$settings_id", settings.Symbol);
        command.Parameters.AddWithValue("$enabled", settings.Enabled);
        command.Parameters.AddWithValue("$symbol", settings.Symbol);
        command.Parameters.AddWithValue("$category", settings.Category);
        command.Parameters.AddWithValue("$strategy_type", settings.StrategyType.ToString());
        command.Parameters.AddWithValue("$strategy_config_json", string.IsNullOrWhiteSpace(settings.StrategyConfigJson) ? "{}" : settings.StrategyConfigJson);
        command.Parameters.AddWithValue("$leverage", FormatDecimal(settings.Leverage));
        command.Parameters.AddWithValue("$margin_mode", settings.MarginMode.ToString());
        command.Parameters.AddWithValue("$position_mode", settings.PositionMode.ToString());
        command.Parameters.AddWithValue("$direction", settings.Direction.ToString());
        command.Parameters.AddWithValue("$max_notional_usdt", FormatDecimal(settings.MaxNotionalUsdt));
        command.Parameters.AddWithValue("$max_margin_usdt", FormatDecimal(settings.MaxMarginUsdt));
        command.Parameters.AddWithValue("$stop_loss_percent", FormatDecimal(settings.StopLossPercent));
        command.Parameters.AddWithValue("$take_profit_percent", FormatDecimal(settings.TakeProfitPercent));
        command.Parameters.AddWithValue("$liquidation_buffer_percent", FormatDecimal(settings.LiquidationBufferPercent));
        command.Parameters.AddWithValue("$reduce_only_enabled", settings.ReduceOnlyEnabled);
        command.Parameters.AddWithValue("$updated_at", settings.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteFuturesSettingsAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM futures_settings WHERE settings_id = $settings_id;";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$settings_id", symbol);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FuturesOrderRecord>> GetFuturesOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT order_link_id, bybit_order_id, symbol, category, action, side, price, quantity, filled_quantity,
                   average_fill_price, fee_paid, status, trading_mode, position_side, reduce_only, position_idx,
                   leverage, margin_mode, stop_loss_price, take_profit_price, realized_pnl, created_at, updated_at, filled_at
            FROM futures_orders
            WHERE symbol = $symbol
            ORDER BY updated_at DESC
            LIMIT 100;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);
        return await ReadFuturesOrdersAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<FuturesOrderRecord>> GetActiveFuturesOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT order_link_id, bybit_order_id, symbol, category, action, side, price, quantity, filled_quantity,
                   average_fill_price, fee_paid, status, trading_mode, position_side, reduce_only, position_idx,
                   leverage, margin_mode, stop_loss_price, take_profit_price, realized_pnl, created_at, updated_at, filled_at
            FROM futures_orders
            WHERE symbol = $symbol
              AND status IN ('New', 'PartiallyFilled')
            ORDER BY updated_at DESC;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);
        return await ReadFuturesOrdersAsync(command, cancellationToken);
    }

    public async Task<FuturesOrderRecord?> GetFuturesOrderByLinkIdAsync(string orderLinkId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT order_link_id, bybit_order_id, symbol, category, action, side, price, quantity, filled_quantity,
                   average_fill_price, fee_paid, status, trading_mode, position_side, reduce_only, position_idx,
                   leverage, margin_mode, stop_loss_price, take_profit_price, realized_pnl, created_at, updated_at, filled_at
            FROM futures_orders
            WHERE order_link_id = $order_link_id
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$order_link_id", orderLinkId);
        return (await ReadFuturesOrdersAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task UpsertFuturesOrderAsync(FuturesOrderRecord order, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO futures_orders (
                order_link_id, bybit_order_id, symbol, category, action, side, price, quantity, filled_quantity,
                average_fill_price, fee_paid, status, trading_mode, position_side, reduce_only, position_idx,
                leverage, margin_mode, stop_loss_price, take_profit_price, realized_pnl, created_at, updated_at, filled_at
            )
            VALUES (
                $order_link_id, $bybit_order_id, $symbol, $category, $action, $side, $price, $quantity, $filled_quantity,
                $average_fill_price, $fee_paid, $status, $trading_mode, $position_side, $reduce_only, $position_idx,
                $leverage, $margin_mode, $stop_loss_price, $take_profit_price, $realized_pnl, $created_at, $updated_at, $filled_at
            )
            ON CONFLICT(order_link_id) DO UPDATE SET
                bybit_order_id = excluded.bybit_order_id,
                symbol = excluded.symbol,
                category = excluded.category,
                action = excluded.action,
                side = excluded.side,
                price = excluded.price,
                quantity = excluded.quantity,
                filled_quantity = excluded.filled_quantity,
                average_fill_price = excluded.average_fill_price,
                fee_paid = excluded.fee_paid,
                status = excluded.status,
                trading_mode = excluded.trading_mode,
                position_side = excluded.position_side,
                reduce_only = excluded.reduce_only,
                position_idx = excluded.position_idx,
                leverage = excluded.leverage,
                margin_mode = excluded.margin_mode,
                stop_loss_price = excluded.stop_loss_price,
                take_profit_price = excluded.take_profit_price,
                realized_pnl = excluded.realized_pnl,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at,
                filled_at = excluded.filled_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddFuturesOrderParameters(command, order);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FuturesPositionSnapshot?> GetFuturesPositionAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, category, side, size, entry_price, mark_price, liquidation_price, position_value_usdt,
                   margin_used_usdt, leverage, unrealized_pnl, realized_pnl, position_idx, updated_at, funding
            FROM futures_positions
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

        return ReadFuturesPosition(reader);
    }

    public async Task UpsertFuturesPositionAsync(FuturesPositionSnapshot position, TradingMode tradingMode, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO futures_positions (
                symbol, category, side, size, entry_price, mark_price, liquidation_price, position_value_usdt,
                margin_used_usdt, leverage, unrealized_pnl, realized_pnl, funding, position_idx, trading_mode, updated_at
            )
            VALUES (
                $symbol, $category, $side, $size, $entry_price, $mark_price, $liquidation_price, $position_value_usdt,
                $margin_used_usdt, $leverage, $unrealized_pnl, $realized_pnl, $funding, $position_idx, $trading_mode, $updated_at
            )
            ON CONFLICT(symbol) DO UPDATE SET
                category = excluded.category,
                side = excluded.side,
                size = excluded.size,
                entry_price = excluded.entry_price,
                mark_price = excluded.mark_price,
                liquidation_price = excluded.liquidation_price,
                position_value_usdt = excluded.position_value_usdt,
                margin_used_usdt = excluded.margin_used_usdt,
                leverage = excluded.leverage,
                unrealized_pnl = excluded.unrealized_pnl,
                realized_pnl = excluded.realized_pnl,
                funding = excluded.funding,
                position_idx = excluded.position_idx,
                trading_mode = excluded.trading_mode,
                updated_at = excluded.updated_at;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", position.Symbol);
        command.Parameters.AddWithValue("$category", position.Category);
        command.Parameters.AddWithValue("$side", position.Side);
        command.Parameters.AddWithValue("$size", FormatDecimal(position.Size));
        command.Parameters.AddWithValue("$entry_price", FormatDecimal(position.EntryPrice));
        command.Parameters.AddWithValue("$mark_price", FormatDecimal(position.MarkPrice));
        command.Parameters.AddWithValue("$liquidation_price", FormatDecimal(position.LiquidationPrice));
        command.Parameters.AddWithValue("$position_value_usdt", FormatDecimal(position.PositionValueUsdt));
        command.Parameters.AddWithValue("$margin_used_usdt", FormatDecimal(position.MarginUsedUsdt));
        command.Parameters.AddWithValue("$leverage", FormatDecimal(position.Leverage));
        command.Parameters.AddWithValue("$unrealized_pnl", FormatDecimal(position.UnrealizedPnl));
        command.Parameters.AddWithValue("$realized_pnl", FormatDecimal(position.RealizedPnl));
        command.Parameters.AddWithValue("$funding", FormatDecimal(position.Funding));
        command.Parameters.AddWithValue("$position_idx", position.PositionIdx);
        command.Parameters.AddWithValue("$trading_mode", tradingMode.ToString());
        command.Parameters.AddWithValue("$updated_at", position.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddFuturesFillAsync(FuturesFillRecord fill, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT OR IGNORE INTO futures_fills (
                exec_id, order_link_id, symbol, action, side, exec_type, quantity, price, fee, realized_pnl, funding, created_at
            )
            VALUES (
                $exec_id, $order_link_id, $symbol, $action, $side, $exec_type, $quantity, $price, $fee, $realized_pnl, $funding, $created_at
            );
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$exec_id", string.IsNullOrWhiteSpace(fill.ExecId) ? DBNull.Value : fill.ExecId);
        command.Parameters.AddWithValue("$order_link_id", fill.OrderLinkId);
        command.Parameters.AddWithValue("$symbol", fill.Symbol);
        command.Parameters.AddWithValue("$action", fill.Action.ToString());
        command.Parameters.AddWithValue("$side", fill.Side.ToString());
        command.Parameters.AddWithValue("$exec_type", fill.ExecType);
        command.Parameters.AddWithValue("$quantity", FormatDecimal(fill.Quantity));
        command.Parameters.AddWithValue("$price", FormatDecimal(fill.Price));
        command.Parameters.AddWithValue("$fee", FormatDecimal(fill.Fee));
        command.Parameters.AddWithValue("$realized_pnl", FormatDecimal(fill.RealizedPnl));
        command.Parameters.AddWithValue("$funding", FormatDecimal(fill.Funding));
        command.Parameters.AddWithValue("$created_at", fill.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> FuturesFillExistsAsync(string execId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(execId))
        {
            return false;
        }

        const string sql = "SELECT 1 FROM futures_fills WHERE exec_id = $exec_id LIMIT 1;";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$exec_id", execId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<FuturesFillRecord>> GetFuturesFillsAsync(string symbol, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT fill_id, exec_id, order_link_id, symbol, action, side, exec_type, quantity, price, fee, realized_pnl, funding, created_at
            FROM futures_fills
            WHERE symbol = $symbol
            ORDER BY created_at DESC
            LIMIT $limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FuturesFillRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadFuturesFill(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<FuturesRiskDecisionRecord>> GetFuturesRiskDecisionsAsync(string symbol, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT risk_decision_id, symbol, source, order_link_id, action, is_allowed, reason, severity, suggested_action, created_at
            FROM futures_risk_decisions
            WHERE symbol = $symbol
            ORDER BY created_at DESC
            LIMIT $limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FuturesRiskDecisionRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadFuturesRiskDecision(reader));
        }

        return result;
    }

    public async Task AddFuturesRiskDecisionAsync(FuturesRiskDecisionRecord decision, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO futures_risk_decisions (
                symbol, source, order_link_id, action, is_allowed, reason, severity, suggested_action, created_at
            )
            VALUES (
                $symbol, $source, $order_link_id, $action, $is_allowed, $reason, $severity, $suggested_action, $created_at
            );
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", decision.Symbol);
        command.Parameters.AddWithValue("$source", decision.Source);
        command.Parameters.AddWithValue("$order_link_id", (object?)decision.OrderLinkId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", decision.Action is null ? (object)DBNull.Value : decision.Action.Value.ToString());
        command.Parameters.AddWithValue("$is_allowed", decision.IsAllowed);
        command.Parameters.AddWithValue("$reason", decision.Reason);
        command.Parameters.AddWithValue("$severity", decision.Severity);
        command.Parameters.AddWithValue("$suggested_action", decision.SuggestedAction);
        command.Parameters.AddWithValue("$created_at", decision.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearFuturesPaperHistoryAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM futures_fills WHERE symbol = $symbol;
            DELETE FROM futures_orders WHERE symbol = $symbol;
            DELETE FROM futures_risk_decisions WHERE symbol = $symbol;
            DELETE FROM futures_positions WHERE symbol = $symbol;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id,
                   strategy_source, position_side, reduce_only, position_idx, leverage, margin_mode, entry_price, mark_price,
                   liquidation_price, unrealized_pnl, realized_pnl,
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
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id,
                   strategy_source, position_side, reduce_only, position_idx, leverage, margin_mode, entry_price, mark_price,
                   liquidation_price, unrealized_pnl, realized_pnl,
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
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id,
                   strategy_source, position_side, reduce_only, position_idx, leverage, margin_mode, entry_price, mark_price,
                   liquidation_price, unrealized_pnl, realized_pnl,
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
                   average_fill_price, fee_paid, status, trading_mode, parent_order_link_id,
                   strategy_source, position_side, reduce_only, position_idx, leverage, margin_mode, entry_price, mark_price,
                   liquidation_price, unrealized_pnl, realized_pnl,
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
                average_fill_price, fee_paid, status, trading_mode, parent_order_link_id,
                strategy_source,
                position_side, reduce_only, position_idx, leverage, margin_mode, entry_price, mark_price,
                liquidation_price, unrealized_pnl, realized_pnl,
                created_at, updated_at, filled_at
            )
            VALUES (
                $order_link_id, $bybit_order_id, $symbol, $category, $side, $price, $quantity, $filled_quantity,
                $average_fill_price, $fee_paid, $status, $trading_mode, $parent_order_link_id,
                $strategy_source,
                $position_side, $reduce_only, $position_idx, $leverage, $margin_mode, $entry_price, $mark_price,
                $liquidation_price, $unrealized_pnl, $realized_pnl,
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
                strategy_source = excluded.strategy_source,
                position_side = excluded.position_side,
                reduce_only = excluded.reduce_only,
                position_idx = excluded.position_idx,
                leverage = excluded.leverage,
                margin_mode = excluded.margin_mode,
                entry_price = excluded.entry_price,
                mark_price = excluded.mark_price,
                liquidation_price = excluded.liquidation_price,
                unrealized_pnl = excluded.unrealized_pnl,
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
        command.Parameters.AddWithValue("$strategy_source", NormalizeStrategySource(order.StrategySource, order.ParentOrderLinkId));
        command.Parameters.AddWithValue("$position_side", (object?)order.PositionSide ?? DBNull.Value);
        command.Parameters.AddWithValue("$reduce_only", order.ReduceOnly);
        command.Parameters.AddWithValue("$position_idx", order.PositionIdx);
        command.Parameters.AddWithValue("$leverage", FormatDecimal(order.Leverage));
        command.Parameters.AddWithValue("$margin_mode", (object?)order.MarginMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry_price", FormatDecimal(order.EntryPrice));
        command.Parameters.AddWithValue("$mark_price", FormatDecimal(order.MarkPrice));
        command.Parameters.AddWithValue("$liquidation_price", FormatDecimal(order.LiquidationPrice));
        command.Parameters.AddWithValue("$unrealized_pnl", FormatDecimal(order.UnrealizedPnl));
        command.Parameters.AddWithValue("$realized_pnl", FormatDecimal(order.RealizedPnl));
        command.Parameters.AddWithValue("$created_at", order.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", order.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$filled_at", order.FilledAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ResetSpotStatisticsAsync(CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            "DELETE FROM grid_orders;",
            "DELETE FROM grid_levels;",
            "DELETE FROM bot_state WHERE symbol NOT LIKE 'futures:%';",
            "DELETE FROM no_trade_reasons;",
            "DELETE FROM strategy_performance;",
            "DELETE FROM strategy_daily_performance;",
            "DELETE FROM strategy_decisions;",
            "DELETE FROM market_regimes;",
            "DELETE FROM market_phases;",
            "DELETE FROM strategy_switches;",
            "DELETE FROM signals;",
            "DELETE FROM capital_allocations;",
            "DELETE FROM trade_intents;",
            "DELETE FROM risk_decisions;",
            "DELETE FROM orders;",
            "DELETE FROM positions;",
            "DELETE FROM daily_pnl;"
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var deletedRows = 0;
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            deletedRows += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogWarning("Reset spot statistics. Deleted rows: {DeletedRows}", deletedRows);
        return deletedRows;
    }

    public async Task<NoTradeReasonRecord?> GetLatestNoTradeReasonAsync(string symbol, CancellationToken cancellationToken)
    {
        var reasons = await GetNoTradeReasonsAsync(symbol, 1, cancellationToken);
        return reasons.FirstOrDefault();
    }

    public async Task<IReadOnlyList<NoTradeReasonRecord>> GetNoTradeReasonsAsync(string symbol, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, strategy_type, reason_code, reason, created_at
            FROM no_trade_reasons
            WHERE symbol = $symbol
            ORDER BY created_at DESC, reason_id DESC
            LIMIT $limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var result = new List<NoTradeReasonRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadNoTradeReason(reader));
        }

        return result;
    }

    public async Task AddNoTradeReasonAsync(NoTradeReasonRecord reason, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO no_trade_reasons (symbol, strategy_type, reason_code, reason, created_at)
            VALUES ($symbol, $strategy_type, $reason_code, $reason, $created_at);
            """;

        var latest = await GetLatestNoTradeReasonAsync(reason.Symbol, cancellationToken);
        if (latest is not null &&
            string.Equals(latest.StrategyType, reason.StrategyType, StringComparison.OrdinalIgnoreCase) &&
            latest.ReasonCode == reason.ReasonCode &&
            string.Equals(latest.Reason, reason.Reason, StringComparison.Ordinal))
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbol", reason.Symbol);
        command.Parameters.AddWithValue("$strategy_type", (object?)reason.StrategyType ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason_code", reason.ReasonCode.ToString());
        command.Parameters.AddWithValue("$reason", reason.Reason);
        command.Parameters.AddWithValue("$created_at", reason.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await PruneNoTradeReasonsAsync(connection, reason.Symbol, 100, cancellationToken);
    }

    private static async Task PruneNoTradeReasonsAsync(
        SqliteConnection connection,
        string symbol,
        int keepCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM no_trade_reasons
            WHERE symbol = $symbol
              AND reason_id NOT IN (
                  SELECT reason_id
                  FROM no_trade_reasons
                  WHERE symbol = $symbol
                  ORDER BY created_at DESC, reason_id DESC
                  LIMIT $keep_count
              );
            """;
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$keep_count", Math.Max(1, keepCount));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<BotState?> GetBotStateAsync(string symbol, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT symbol, trading_mode, is_initialized, is_paused, pause_reason, last_observed_price,
                   base_asset_quantity, quote_asset_balance, average_entry_price, total_realized_pnl,
                   daily_realized_pnl, daily_pnl_date, updated_at, position_side, reduce_only, position_idx,
                   leverage, margin_mode, entry_price, mark_price, liquidation_price, unrealized_pnl,
                   peak_equity_usdt, current_drawdown_usdt, current_drawdown_percent,
                   profit_protection_peak_price, profit_protection_trailing_stop_price
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
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            PositionSide = reader.IsDBNull(13) ? null : reader.GetString(13),
            ReduceOnly = reader.GetBoolean(14),
            PositionIdx = reader.GetInt32(15),
            Leverage = ParseDecimal(reader.GetString(16)),
            MarginMode = reader.IsDBNull(17) ? null : reader.GetString(17),
            EntryPrice = ParseDecimal(reader.GetString(18)),
            MarkPrice = ParseDecimal(reader.GetString(19)),
            LiquidationPrice = ParseDecimal(reader.GetString(20)),
            UnrealizedPnl = ParseDecimal(reader.GetString(21)),
            PeakEquityUsdt = ParseDecimal(reader.GetString(22)),
            CurrentDrawdownUsdt = ParseDecimal(reader.GetString(23)),
            CurrentDrawdownPercent = ParseDecimal(reader.GetString(24)),
            ProfitProtectionPeakPrice = ParseDecimal(reader.GetString(25)),
            ProfitProtectionTrailingStopPrice = ParseDecimal(reader.GetString(26))
        };
    }

    public async Task SaveBotStateAsync(BotState state, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO bot_state (
                symbol, trading_mode, is_initialized, is_paused, pause_reason, last_observed_price,
                base_asset_quantity, quote_asset_balance, average_entry_price, total_realized_pnl,
                daily_realized_pnl, daily_pnl_date, updated_at, position_side, reduce_only, position_idx,
                leverage, margin_mode, entry_price, mark_price, liquidation_price, unrealized_pnl,
                peak_equity_usdt, current_drawdown_usdt, current_drawdown_percent,
                profit_protection_peak_price, profit_protection_trailing_stop_price
            )
            VALUES (
                $symbol, $trading_mode, $is_initialized, $is_paused, $pause_reason, $last_observed_price,
                $base_asset_quantity, $quote_asset_balance, $average_entry_price, $total_realized_pnl,
                $daily_realized_pnl, $daily_pnl_date, $updated_at, $position_side, $reduce_only, $position_idx,
                $leverage, $margin_mode, $entry_price, $mark_price, $liquidation_price, $unrealized_pnl,
                $peak_equity_usdt, $current_drawdown_usdt, $current_drawdown_percent,
                $profit_protection_peak_price, $profit_protection_trailing_stop_price
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
                updated_at = excluded.updated_at,
                position_side = excluded.position_side,
                reduce_only = excluded.reduce_only,
                position_idx = excluded.position_idx,
                leverage = excluded.leverage,
                margin_mode = excluded.margin_mode,
                entry_price = excluded.entry_price,
                mark_price = excluded.mark_price,
                liquidation_price = excluded.liquidation_price,
                unrealized_pnl = excluded.unrealized_pnl,
                peak_equity_usdt = excluded.peak_equity_usdt,
                current_drawdown_usdt = excluded.current_drawdown_usdt,
                current_drawdown_percent = excluded.current_drawdown_percent,
                profit_protection_peak_price = excluded.profit_protection_peak_price,
                profit_protection_trailing_stop_price = excluded.profit_protection_trailing_stop_price;
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
        command.Parameters.AddWithValue("$position_side", (object?)state.PositionSide ?? DBNull.Value);
        command.Parameters.AddWithValue("$reduce_only", state.ReduceOnly);
        command.Parameters.AddWithValue("$position_idx", state.PositionIdx);
        command.Parameters.AddWithValue("$leverage", FormatDecimal(state.Leverage));
        command.Parameters.AddWithValue("$margin_mode", (object?)state.MarginMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry_price", FormatDecimal(state.EntryPrice));
        command.Parameters.AddWithValue("$mark_price", FormatDecimal(state.MarkPrice));
        command.Parameters.AddWithValue("$liquidation_price", FormatDecimal(state.LiquidationPrice));
        command.Parameters.AddWithValue("$unrealized_pnl", FormatDecimal(state.UnrealizedPnl));
        command.Parameters.AddWithValue("$peak_equity_usdt", FormatDecimal(state.PeakEquityUsdt));
        command.Parameters.AddWithValue("$current_drawdown_usdt", FormatDecimal(state.CurrentDrawdownUsdt));
        command.Parameters.AddWithValue("$current_drawdown_percent", FormatDecimal(state.CurrentDrawdownPercent));
        command.Parameters.AddWithValue("$profit_protection_peak_price", FormatDecimal(state.ProfitProtectionPeakPrice));
        command.Parameters.AddWithValue("$profit_protection_trailing_stop_price", FormatDecimal(state.ProfitProtectionTrailingStopPrice));
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

    private static FuturesBotSettings ReadFuturesSettings(SqliteDataReader reader)
    {
        return new FuturesBotSettings
        {
            Enabled = reader.GetBoolean(0),
            Symbol = reader.GetString(1),
            Category = reader.GetString(2),
            StrategyType = ParseEnum(reader.GetString(3), FuturesStrategyType.Pause),
            StrategyConfigJson = reader.GetString(4),
            Leverage = ParseDecimal(reader.GetString(5)),
            MarginMode = ParseEnum(reader.GetString(6), FuturesMarginMode.Isolated),
            PositionMode = ParseEnum(reader.GetString(7), FuturesPositionMode.OneWay),
            Direction = ParseEnum(reader.GetString(8), FuturesDirection.LongOnly),
            MaxNotionalUsdt = ParseDecimal(reader.GetString(9)),
            MaxMarginUsdt = ParseDecimal(reader.GetString(10)),
            StopLossPercent = ParseDecimal(reader.GetString(11)),
            TakeProfitPercent = ParseDecimal(reader.GetString(12)),
            LiquidationBufferPercent = ParseDecimal(reader.GetString(13)),
            ReduceOnlyEnabled = reader.GetBoolean(14),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture)
        };
    }

    private static async Task<List<FuturesOrderRecord>> ReadFuturesOrdersAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<FuturesOrderRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FuturesOrderRecord
            {
                OrderLinkId = reader.GetString(0),
                BybitOrderId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Symbol = reader.GetString(2),
                Category = reader.GetString(3),
                Action = ParseEnum(reader.GetString(4), FuturesTradeAction.OpenLong),
                Side = ParseEnum(reader.GetString(5), TradeSide.Buy),
                Price = ParseDecimal(reader.GetString(6)),
                Quantity = ParseDecimal(reader.GetString(7)),
                FilledQuantity = ParseDecimal(reader.GetString(8)),
                AverageFillPrice = ParseDecimal(reader.GetString(9)),
                FeePaid = ParseDecimal(reader.GetString(10)),
                Status = ParseEnum(reader.GetString(11), OrderStatus.New),
                TradingMode = ParseEnum(reader.GetString(12), TradingMode.Paper),
                PositionSide = reader.GetString(13),
                ReduceOnly = reader.GetBoolean(14),
                PositionIdx = reader.GetInt32(15),
                Leverage = ParseDecimal(reader.GetString(16)),
                MarginMode = reader.GetString(17),
                StopLossPrice = ParseDecimal(reader.GetString(18)),
                TakeProfitPrice = ParseDecimal(reader.GetString(19)),
                RealizedPnl = ParseDecimal(reader.GetString(20)),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(21), CultureInfo.InvariantCulture),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(22), CultureInfo.InvariantCulture),
                FilledAt = reader.IsDBNull(23) ? null : DateTimeOffset.Parse(reader.GetString(23), CultureInfo.InvariantCulture)
            });
        }

        return result;
    }

    private static FuturesPositionSnapshot ReadFuturesPosition(SqliteDataReader reader) => new()
    {
        Symbol = reader.GetString(0),
        Category = reader.GetString(1),
        Side = reader.GetString(2),
        Size = ParseDecimal(reader.GetString(3)),
        EntryPrice = ParseDecimal(reader.GetString(4)),
        MarkPrice = ParseDecimal(reader.GetString(5)),
        LiquidationPrice = ParseDecimal(reader.GetString(6)),
        PositionValueUsdt = ParseDecimal(reader.GetString(7)),
        MarginUsedUsdt = ParseDecimal(reader.GetString(8)),
        Leverage = ParseDecimal(reader.GetString(9)),
        UnrealizedPnl = ParseDecimal(reader.GetString(10)),
        RealizedPnl = ParseDecimal(reader.GetString(11)),
        Funding = reader.FieldCount > 14 ? ParseDecimal(reader.GetString(14)) : 0m,
        PositionIdx = reader.GetInt32(12),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture)
    };

    private static FuturesRiskDecisionRecord ReadFuturesRiskDecision(SqliteDataReader reader) => new()
    {
        RiskDecisionId = reader.GetInt64(0),
        Symbol = reader.GetString(1),
        Source = reader.GetString(2),
        OrderLinkId = reader.IsDBNull(3) ? null : reader.GetString(3),
        Action = reader.IsDBNull(4) ? null : ParseEnum(reader.GetString(4), FuturesTradeAction.OpenLong),
        IsAllowed = reader.GetBoolean(5),
        Reason = reader.GetString(6),
        Severity = reader.GetString(7),
        SuggestedAction = reader.GetString(8),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture)
    };

    private static FuturesFillRecord ReadFuturesFill(SqliteDataReader reader) => new()
    {
        FillId = reader.GetInt64(0),
        ExecId = reader.IsDBNull(1) ? null : reader.GetString(1),
        OrderLinkId = reader.GetString(2),
        Symbol = reader.GetString(3),
        Action = ParseEnum(reader.GetString(4), FuturesTradeAction.OpenLong),
        Side = ParseEnum(reader.GetString(5), TradeSide.Buy),
        ExecType = reader.GetString(6),
        Quantity = ParseDecimal(reader.GetString(7)),
        Price = ParseDecimal(reader.GetString(8)),
        Fee = ParseDecimal(reader.GetString(9)),
        RealizedPnl = ParseDecimal(reader.GetString(10)),
        Funding = ParseDecimal(reader.GetString(11)),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)
    };

    private static NoTradeReasonRecord ReadNoTradeReason(SqliteDataReader reader) => new()
    {
        Symbol = reader.GetString(0),
        StrategyType = reader.IsDBNull(1) ? null : reader.GetString(1),
        ReasonCode = ParseEnum(reader.GetString(2), NoTradeReason.None),
        Reason = reader.GetString(3),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)
    };

    private static void AddFuturesOrderParameters(SqliteCommand command, FuturesOrderRecord order)
    {
        command.Parameters.AddWithValue("$order_link_id", order.OrderLinkId);
        command.Parameters.AddWithValue("$bybit_order_id", (object?)order.BybitOrderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$symbol", order.Symbol);
        command.Parameters.AddWithValue("$category", order.Category);
        command.Parameters.AddWithValue("$action", order.Action.ToString());
        command.Parameters.AddWithValue("$side", order.Side.ToString());
        command.Parameters.AddWithValue("$price", FormatDecimal(order.Price));
        command.Parameters.AddWithValue("$quantity", FormatDecimal(order.Quantity));
        command.Parameters.AddWithValue("$filled_quantity", FormatDecimal(order.FilledQuantity));
        command.Parameters.AddWithValue("$average_fill_price", FormatDecimal(order.AverageFillPrice));
        command.Parameters.AddWithValue("$fee_paid", FormatDecimal(order.FeePaid));
        command.Parameters.AddWithValue("$status", order.Status.ToString());
        command.Parameters.AddWithValue("$trading_mode", order.TradingMode.ToString());
        command.Parameters.AddWithValue("$position_side", order.PositionSide);
        command.Parameters.AddWithValue("$reduce_only", order.ReduceOnly);
        command.Parameters.AddWithValue("$position_idx", order.PositionIdx);
        command.Parameters.AddWithValue("$leverage", FormatDecimal(order.Leverage));
        command.Parameters.AddWithValue("$margin_mode", order.MarginMode);
        command.Parameters.AddWithValue("$stop_loss_price", FormatDecimal(order.StopLossPrice));
        command.Parameters.AddWithValue("$take_profit_price", FormatDecimal(order.TakeProfitPrice));
        command.Parameters.AddWithValue("$realized_pnl", FormatDecimal(order.RealizedPnl));
        command.Parameters.AddWithValue("$created_at", order.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", order.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$filled_at", order.FilledAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
    }

    private async Task<List<GridOrder>> ReadOrdersAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<GridOrder>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var parentOrderLinkId = reader.IsDBNull(12) ? null : reader.GetString(12);
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
                ParentOrderLinkId = parentOrderLinkId,
                StrategySource = NormalizeStrategySource(reader.IsDBNull(13) ? null : reader.GetString(13), parentOrderLinkId),
                PositionSide = reader.IsDBNull(14) ? null : reader.GetString(14),
                ReduceOnly = reader.GetBoolean(15),
                PositionIdx = reader.GetInt32(16),
                Leverage = ParseDecimal(reader.GetString(17)),
                MarginMode = reader.IsDBNull(18) ? null : reader.GetString(18),
                EntryPrice = ParseDecimal(reader.GetString(19)),
                MarkPrice = ParseDecimal(reader.GetString(20)),
                LiquidationPrice = ParseDecimal(reader.GetString(21)),
                UnrealizedPnl = ParseDecimal(reader.GetString(22)),
                RealizedPnl = ParseDecimal(reader.GetString(23)),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(24), CultureInfo.InvariantCulture),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(25), CultureInfo.InvariantCulture),
                FilledAt = reader.IsDBNull(26) ? null : DateTimeOffset.Parse(reader.GetString(26), CultureInfo.InvariantCulture)
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

    private static async Task EnsureFuturesOrderColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, tableName, "position_side", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "reduce_only", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "position_idx", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "leverage", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "margin_mode", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "entry_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "mark_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "liquidation_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "unrealized_pnl", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
    }

    private static async Task BackfillGridOrderStrategySourcesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var markerCommand = connection.CreateCommand())
        {
            markerCommand.CommandText = """
                UPDATE grid_orders
                SET strategy_source = CASE parent_order_link_id
                    WHEN 'dca-entry' THEN 'DCA'
                    WHEN 'btd-entry' THEN 'BTD'
                    WHEN 'signal-entry' THEN 'Signal'
                    WHEN 'signal-exit' THEN 'Signal'
                    WHEN 'trend-entry' THEN 'Trend'
                    WHEN 'trend-exit' THEN 'Trend'
                    WHEN 'reduce-only-exit' THEN 'ReduceOnly'
                    ELSE strategy_source
                END
                WHERE parent_order_link_id IS NOT NULL
                  AND parent_order_link_id IN (
                      'dca-entry',
                      'btd-entry',
                      'signal-entry',
                      'signal-exit',
                      'trend-entry',
                      'trend-exit',
                      'reduce-only-exit'
                  )
                  AND (strategy_source IS NULL OR strategy_source = '' OR strategy_source = 'Grid' OR strategy_source = 'Managed');
                """;
            await markerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var parentCommand = connection.CreateCommand();
        parentCommand.CommandText = """
            UPDATE grid_orders
            SET strategy_source = (
                SELECT parent.strategy_source
                FROM grid_orders parent
                WHERE parent.order_link_id = grid_orders.parent_order_link_id
                  AND parent.strategy_source IS NOT NULL
                  AND parent.strategy_source <> ''
                  AND parent.strategy_source <> 'Managed'
                  AND parent.strategy_source <> 'Grid'
                LIMIT 1
            )
            WHERE parent_order_link_id IS NOT NULL
              AND (strategy_source IS NULL OR strategy_source = '' OR strategy_source = 'Grid' OR strategy_source = 'Managed')
              AND EXISTS (
                  SELECT 1
                  FROM grid_orders parent
                  WHERE parent.order_link_id = grid_orders.parent_order_link_id
                    AND parent.strategy_source IS NOT NULL
                    AND parent.strategy_source <> ''
                    AND parent.strategy_source <> 'Managed'
                    AND parent.strategy_source <> 'Grid'
              );
            """;
        await parentCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureFuturesStateColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(connection, tableName, "position_side", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "reduce_only", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "position_idx", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "leverage", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "margin_mode", "TEXT NULL", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "entry_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "mark_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "liquidation_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "unrealized_pnl", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "peak_equity_usdt", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "current_drawdown_usdt", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "current_drawdown_percent", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "profit_protection_peak_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
        await EnsureColumnAsync(connection, tableName, "profit_protection_trailing_stop_price", "TEXT NOT NULL DEFAULT '0'", cancellationToken);
    }

    private static async Task EnsureUniqueFuturesFillExecIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_futures_fills_exec_id
            ON futures_fills(exec_id)
            WHERE exec_id IS NOT NULL AND exec_id <> '';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatDecimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static string NormalizeStrategySource(string? source, string? parentOrderLinkId)
    {
        if (!string.IsNullOrWhiteSpace(source) &&
            !string.Equals(source, "Managed", StringComparison.OrdinalIgnoreCase))
        {
            return source.Trim();
        }

        return parentOrderLinkId switch
        {
            "dca-entry" => "DCA",
            "btd-entry" => "BTD",
            "signal-entry" or "signal-exit" => "Signal",
            "trend-entry" or "trend-exit" => "Trend",
            "reduce-only-exit" => "ReduceOnly",
            _ => "Grid"
        };
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
}
