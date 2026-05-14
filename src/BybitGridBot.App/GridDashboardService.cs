using System.Text.Json;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IGridDashboardService
{
    Task<DashboardResponse> GetDashboardAsync(string? symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> DeleteSettingsAsync(string symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> ResumeTradingAsync(string? symbol, CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> CancelActiveOrdersAsync(string? symbol, CancellationToken cancellationToken);
    string RenderDashboardPage();
}

public sealed class GridDashboardService : IGridDashboardService
{
    private readonly AppOptions _appOptions;
    private readonly GridOptions _defaultGridOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly MarketRegimeAnalyzer _marketRegimeAnalyzer;
    private readonly IGridRepository _repository;
    private readonly IGridTradingStrategy _strategy;

    public GridDashboardService(
        IOptions<AppOptions> appOptions,
        IOptions<GridOptions> defaultGridOptions,
        IBybitRestClient bybitRestClient,
        MarketRegimeAnalyzer marketRegimeAnalyzer,
        IGridRepository repository,
        IGridTradingStrategy strategy)
    {
        _appOptions = appOptions.Value;
        _defaultGridOptions = defaultGridOptions.Value;
        _bybitRestClient = bybitRestClient;
        _marketRegimeAnalyzer = marketRegimeAnalyzer;
        _repository = repository;
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

    public async Task<DashboardResponse> GetDashboardAsync(string? symbol, CancellationToken cancellationToken)
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
                UpdatedAt = DateTimeOffset.UtcNow
            };
        var levels = await _repository.GetGridLevelsAsync(gridOptions.Symbol, cancellationToken);
        if (levels.Count == 0 && runtimeSettings.StrategyType == TradingStrategyType.Grid)
        {
            levels = _strategy.BuildGrid(gridOptions);
        }

        var orders = (await _repository.GetOrdersAsync(gridOptions.Symbol, cancellationToken))
            .OrderByDescending(order => order.CreatedAt)
            .Take(100)
            .Select(MapOrder)
            .ToArray();
        var activeOrders = orders
            .Where(order => order.Status is nameof(OrderStatus.New) or nameof(OrderStatus.PartiallyFilled))
            .ToArray();

        decimal? currentPrice = state.LastObservedPrice;
        try
        {
            var ticker = await _bybitRestClient.GetTickerAsync(gridOptions.Category, gridOptions.Symbol, cancellationToken);
            currentPrice = ticker.LastPrice;
        }
        catch
        {
            currentPrice ??= state.LastObservedPrice;
        }

        var unrealizedPnl = currentPrice is null
            ? 0m
            : state.BaseAssetQuantity * (currentPrice.Value - state.AverageEntryPrice);
        var estimatedTotalEquity = state.QuoteAssetBalance + (currentPrice ?? 0m) * state.BaseAssetQuantity;
        var generatedAt = DateTimeOffset.UtcNow;
        var marketRegime = await AnalyzeMarketRegimeAsync(gridOptions, cancellationToken);

        return new DashboardResponse
        {
            Profiles = profiles
                .Select(profile => new DashboardProfileItem
                {
                    Symbol = profile.Symbol,
                    Category = profile.Category,
                    IsSelected = string.Equals(profile.Symbol, runtimeSettings.Symbol, StringComparison.OrdinalIgnoreCase)
                })
                .ToArray(),
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
                UpdatedAt = state.UpdatedAt
            },
            MarketRegime = MapMarketRegime(marketRegime),
            Orders = orders,
            ActiveOrders = activeOrders,
            GridLevels = levels.Select(level => level.Price).ToArray(),
            GeneratedAt = generatedAt
        };
    }

    private async Task<MarketRegimeAnalysis> AnalyzeMarketRegimeAsync(GridOptions gridOptions, CancellationToken cancellationToken)
    {
        try
        {
            var candles = await _bybitRestClient.GetKlinesAsync(
                gridOptions.Category,
                gridOptions.Symbol,
                "1",
                60,
                cancellationToken);

            return _marketRegimeAnalyzer.Analyze(candles);
        }
        catch
        {
            return new MarketRegimeAnalysis
            {
                Regime = MarketRegimeType.Danger,
                Confidence = 0m,
                Recommendation = "Market regime analysis is unavailable."
            };
        }
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
            errors.Add("Strategy type must be grid, dca, combo, or btd.");
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
      .stats { grid-template-columns: repeat(2, minmax(0,1fr)); }
      form { grid-template-columns: 1fr; }
    }
  </style>
</head>
<body>
  <div class="shell">
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
        </div>
        <div class="pause-box" id="pauseBox" hidden>
          <strong>Trading paused</strong>
          <p id="pauseReason">-</p>
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
          <div><label for="strategyType">Strategy Type</label><select id="strategyType" name="strategyType"><option value="grid">Grid</option><option value="dca">DCA</option><option value="combo">Combo Grid + DCA</option><option value="btd">BTD Buy The Dip</option></select></div>
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

    <section class="stats" id="stats"></section>

    <section class="panel section regime-card">
      <div>
        <div class="label">Market Regime</div>
        <div class="regime-title" id="marketRegimeTitle">-</div>
        <div class="subtle" id="marketRegimeRecommendation">-</div>
        <div class="regime-meta" id="marketRegimeMeta"></div>
      </div>
      <div class="badge">Confidence <strong id="marketRegimeConfidence">-</strong></div>
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
        <div style="overflow:auto;">
          <table>
            <thead>
              <tr><th>Side</th><th>Price</th><th>Qty</th><th>Filled</th><th>Status</th><th>Link</th></tr>
            </thead>
            <tbody id="activeOrders"></tbody>
          </table>
        </div>
      </section>
    </div>

    <section class="panel section" style="margin-top:20px;">
      <div class="section-head">
        <h2>Order History</h2>
        <button type="button" class="secondary-button compact-button" id="copyLastHourHistory">Copy Last Hour</button>
      </div>
      <div style="overflow:auto;">
        <table>
          <thead>
            <tr>
              <th>Time</th><th>Side</th><th>Price</th><th>Qty</th><th>Filled</th><th>Status</th><th>Realized PnL</th><th>Fee</th><th>Order</th>
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
    let settingsFormDirty = false;

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
    const formatSigned = (value) => {
      const number = Number(value ?? 0);
      const cls = number > 0 ? "positive" : number < 0 ? "negative" : "";
      return `<span class="value ${cls}">${number.toLocaleString(undefined, { maximumFractionDigits: 8 })}</span>`;
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
    const buildLastHourHistoryCsv = () => {
      if (!latestDashboardData) {
        return '';
      }

      const cutoff = Date.now() - 60 * 60 * 1000;
      const rows = latestDashboardData.orders
        .filter(order => {
          const timestamp = new Date(order.filledAt || order.updatedAt || order.createdAt).getTime();
          return Number.isFinite(timestamp) && timestamp >= cutoff;
        })
        .map(order => [
          formatDate(order.filledAt || order.updatedAt || order.createdAt),
          order.symbol,
          order.side,
          order.price,
          order.quantity,
          order.filledQuantity,
          order.status,
          order.realizedPnl,
          order.feePaid,
          order.orderLinkId
        ]);

      return [
        ['Time', 'Symbol', 'Side', 'Price', 'Qty', 'Filled', 'Status', 'Realized PnL', 'Fee', 'Order'],
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
    const copyLastHourHistory = async () => {
      const status = byId('formStatus');
      const csv = buildLastHourHistoryCsv();
      if (!csv) {
        status.className = 'status error';
        status.textContent = 'No dashboard data loaded yet.';
        return;
      }

      await writeClipboard(csv);
      const copiedRows = Math.max(0, csv.split('\n').length - 1);
      status.className = 'status ok';
      status.textContent = `Copied ${copiedRows} history rows from the last hour.`;
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

    async function loadDashboard(options = {}) {
      const forceSettingsRefresh = Boolean(options.forceSettingsRefresh);
      const dashboardUrl = selectedSymbol && !isCreatingNewProfile
        ? `/api/dashboard?symbol=${encodeURIComponent(selectedSymbol)}`
        : '/api/dashboard';
      const response = await fetch(dashboardUrl, { cache: 'no-store' });
      const data = await response.json();
      latestDashboardData = data;
      if (!isCreatingNewProfile) {
        selectedSymbol = data.settings.symbol;
        updateSelectedSymbolUrl();
      }
      renderProfileTabs(data.profiles);

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

      if (forceSettingsRefresh || !isSettingsFormDirty()) {
        updateSettingsForm(data.settings);
        setSettingsFormDirty(false);
      }

      byId('stats').innerHTML = [
        ['Current Price', formatNumber(data.state.currentPrice)],
        ['Total Realized PnL', formatSigned(data.state.totalRealizedPnl)],
        ['Daily Realized PnL', formatSigned(data.state.dailyRealizedPnl)],
        ['Unrealized PnL', formatSigned(data.state.unrealizedPnl)],
        ['Estimated Equity', formatNumber(data.state.estimatedTotalEquity)],
        ['Base Asset Qty', formatNumber(data.state.baseAssetQuantity)],
        ['Quote Balance', formatNumber(data.state.quoteAssetBalance)],
        ['Average Entry', formatNumber(data.state.averageEntryPrice)]
      ].map(([label, value]) => `<div class="stat"><div class="label">${label}</div><div class="value">${value}</div></div>`).join('');

      byId('gridLevels').innerHTML = data.gridLevels.map(level => `<div class="grid-chip">${formatNumber(level)}</div>`).join('');
      byId('activeOrders').innerHTML = data.activeOrders.length === 0
        ? `<tr><td colspan="6">No active orders.</td></tr>`
        : data.activeOrders.map(order => `
            <tr>
              <td>${order.side}</td>
              <td>${formatNumber(order.price)}</td>
              <td>${formatNumber(order.quantity)}</td>
              <td>${formatNumber(order.filledQuantity)}</td>
              <td>${order.status}</td>
              <td class="token">${order.orderLinkId}</td>
            </tr>`).join('');

      byId('historyRows').innerHTML = data.orders.length === 0
        ? `<tr><td colspan="9">No orders yet.</td></tr>`
        : data.orders.map(order => `
            <tr>
              <td>${formatDate(order.filledAt || order.updatedAt || order.createdAt)}</td>
              <td>${order.side}</td>
              <td>${formatNumber(order.price)}</td>
              <td>${formatNumber(order.quantity)}</td>
              <td>${formatNumber(order.filledQuantity)}</td>
              <td>${order.status}</td>
              <td>${formatNumber(order.realizedPnl)}</td>
              <td>${formatNumber(order.feePaid)}</td>
              <td class="token">${order.orderLinkId}</td>
            </tr>`).join('');
    }

    settingsFieldIds.forEach((id) => {
      byId(id).addEventListener('input', () => setSettingsFormDirty(true));
    });
    byId('applyPreset').addEventListener('click', applySettingsPreset);
    byId('copyLastHourHistory').addEventListener('click', () => {
      copyLastHourHistory().catch((error) => {
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
    byId('profileTabs').addEventListener('click', async (event) => {
      const actionTarget = event.target.closest('[data-action]');
      if (!actionTarget) {
        return;
      }

      const action = actionTarget.dataset.action;
      const symbol = actionTarget.dataset.symbol;
      if (action === 'select-profile' && symbol) {
        selectedSymbol = symbol.toUpperCase();
        isCreatingNewProfile = false;
        setSettingsFormDirty(false);
        updateSelectedSymbolUrl();
        await loadDashboard({ forceSettingsRefresh: true });
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

    document.getElementById('settingsForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const payload = {
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
        stopUpperPrice: Number(byId('stopUpperPrice').value)
      };

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
    setInterval(() => loadDashboard().catch(() => {}), 10000);
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

    private static DashboardOrderItem MapOrder(GridOrder order)
    {
        return new DashboardOrderItem
        {
            OrderLinkId = order.OrderLinkId,
            BybitOrderId = order.BybitOrderId,
            Symbol = order.Symbol,
            Side = order.Side.ToString(),
            Price = order.Price,
            Quantity = order.Quantity,
            FilledQuantity = order.FilledQuantity,
            RealizedPnl = order.RealizedPnl,
            FeePaid = order.FeePaid,
            Status = order.Status.ToString(),
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            FilledAt = order.FilledAt
        };
    }

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
}
