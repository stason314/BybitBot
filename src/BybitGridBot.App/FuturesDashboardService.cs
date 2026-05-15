using System.Text.Json;
using BybitGridBot.Bybit;
using BybitGridBot.Domain;
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
    string RenderDashboardPage();
}

public sealed class FuturesDashboardService : IFuturesDashboardService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyList<string> StrategyActions =
    [
        nameof(FuturesTradeAction.OpenLong),
        nameof(FuturesTradeAction.CloseLong),
        nameof(FuturesTradeAction.ReduceOnlyClose)
    ];

    private readonly IBybitRestClient _bybitRestClient;
    private readonly AppOptions _appOptions;
    private readonly FuturesOptions _futuresOptions;
    private readonly FuturesAutoConfigRecommender _recommender;
    private readonly IGridRepository _repository;

    public FuturesDashboardService(
        IBybitRestClient bybitRestClient,
        IOptions<AppOptions> appOptions,
        IOptions<FuturesOptions> futuresOptions,
        FuturesAutoConfigRecommender recommender,
        IGridRepository repository)
    {
        _bybitRestClient = bybitRestClient;
        _appOptions = appOptions.Value;
        _futuresOptions = futuresOptions.Value;
        _recommender = recommender;
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
        string? positionError = null;
        try
        {
            if (_appOptions.TradingMode == TradingMode.Paper)
            {
                position = await GetPaperPositionAsync(selectedSettings, cancellationToken) ?? position;
            }
            else
            {
                var bybitPosition = await _bybitRestClient.GetPositionAsync(selectedSettings.Category, selectedSettings.Symbol, cancellationToken);
                if (bybitPosition is not null)
                {
                    position = MapPosition(selectedSettings, bybitPosition);
                }
            }
        }
        catch (Exception exception)
        {
            positionError = exception.Message;
            position = await GetPaperPositionAsync(selectedSettings, cancellationToken) ?? position;
        }

        var candles = await GetAnalysisCandlesAsync(selectedSettings, cancellationToken);
        var recommendation = _recommender.Recommend(selectedSettings, candles, position.Size > 0m);

        return new FuturesDashboardResponse
        {
            Profiles = profiles
                .Select(profile => new FuturesProfileItem
                {
                    Symbol = profile.Symbol,
                    Category = profile.Category,
                    IsSelected = string.Equals(profile.Symbol, selectedSettings.Symbol, StringComparison.OrdinalIgnoreCase)
                })
                .ToArray(),
            ConfigSummaries = profiles
                .Select(profile => new FuturesConfigSummaryItem
                {
                    Symbol = profile.Symbol,
                    Category = profile.Category,
                    StrategyType = FormatEnum(profile.StrategyType),
                    Direction = FormatEnum(profile.Direction),
                    Leverage = profile.Leverage,
                    MaxNotionalUsdt = profile.MaxNotionalUsdt,
                    MaxMarginUsdt = profile.MaxMarginUsdt,
                    IsSelected = string.Equals(profile.Symbol, selectedSettings.Symbol, StringComparison.OrdinalIgnoreCase),
                    UpdatedAt = profile.UpdatedAt
                })
                .ToArray(),
            Settings = MapSettings(selectedSettings),
            Position = position,
            AutoRecommendation = MapAutoRecommendation(recommendation),
            StrategyActions = StrategyActions,
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

        var hasOpenPosition = false;
        try
        {
            hasOpenPosition = _appOptions.TradingMode == TradingMode.Paper
                ? ((await GetPaperPositionAsync(settings, cancellationToken))?.Size ?? 0m) > 0m
                : ((await _bybitRestClient.GetPositionAsync(settings.Category, settings.Symbol, cancellationToken))?.Size ?? 0m) > 0m;
        }
        catch
        {
            hasOpenPosition = false;
        }

        var recommendation = _recommender.Recommend(settings, candles, hasOpenPosition);
        var recommendedSettings = BuildRecommendedSettings(settings, recommendation);
        await _repository.SaveFuturesSettingsAsync(recommendedSettings, cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Symbol = settings.Symbol,
            Message = $"Futures auto recommendation applied: {recommendation.StrategyType}. {recommendation.Reason}"
        };
    }

    public async Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateFuturesSettingsRequest request, CancellationToken cancellationToken)
    {
        var symbol = NormalizeSymbol(request.Symbol);
        var category = NormalizeCategory(request.Category);
        var strategyType = ParseFuturesStrategyType(request.StrategyType);
        var marginMode = ParseMarginMode(request.MarginMode);
        var positionMode = ParsePositionMode(request.PositionMode);
        var direction = ParseDirection(request.Direction);
        var strategyConfigJson = NormalizeStrategyConfigJson(request.StrategyConfigJson);

        var errors = ValidateRequest(symbol, category, request);
        if (strategyType is null)
        {
            errors.Add("Strategy type must be trendfollow, breakout, gridlongonly, reduceonly, or pause.");
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
            errors.Add("Direction must be long-only for the MVP.");
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
    .status { min-height: 22px; color: var(--muted); margin-top: 12px; }
    .status.ok { color: var(--accent); }
    .status.error { color: var(--danger); }
    .notice { padding: 12px; border-radius: 8px; background: rgba(177,54,34,.08); color: var(--danger); margin: 0 0 14px; }
    .actions-list { display: flex; gap: 8px; flex-wrap: wrap; }
    .chip { border-radius: 999px; padding: 8px 10px; background: rgba(47,127,143,0.12); color: var(--accent-2); font-size: 12px; font-weight: 700; }
    @media (max-width: 980px) {
      .layout, form { grid-template-columns: 1fr; }
      .stats { grid-template-columns: repeat(2, minmax(0,1fr)); }
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

    <section class="stats" id="positionStats"></section>

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

    <div class="layout">
      <section class="panel">
        <h2>Futures Profiles</h2>
        <div class="table-wrap">
          <table>
            <thead>
              <tr><th>Symbol</th><th>Strategy</th><th>Direction</th><th>Leverage</th><th>Max Notional</th><th>Max Margin</th><th>Updated</th></tr>
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
          <div><label for="strategyType">Strategy</label><select id="strategyType" name="strategyType"><option value="pause">Pause</option><option value="trendfollow">Trend Follow</option><option value="breakout">Breakout</option><option value="gridlongonly">Grid Long Only</option><option value="reduceonly">Reduce Only</option></select></div>
          <div><label for="direction">Direction</label><select id="direction" name="direction"><option value="long-only">Long only</option></select></div>
          <div><label for="leverage">Leverage</label><input id="leverage" name="leverage" type="number" step="0.01" min="1" required /></div>
          <div><label for="marginMode">Margin Mode</label><select id="marginMode" name="marginMode"><option value="isolated">Isolated</option></select></div>
          <div><label for="positionMode">Position Mode</label><select id="positionMode" name="positionMode"><option value="oneway">One-way</option></select></div>
          <div><label for="maxNotionalUsdt">Max Notional USDT</label><input id="maxNotionalUsdt" name="maxNotionalUsdt" type="number" step="0.00000001" required /></div>
          <div><label for="maxMarginUsdt">Max Margin USDT</label><input id="maxMarginUsdt" name="maxMarginUsdt" type="number" step="0.00000001" required /></div>
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
      <h2>Strategy Action Model</h2>
      <div class="actions-list" id="strategyActions"></div>
    </section>
  </main>

  <script>
    const byId = (id) => document.getElementById(id);
    const fields = ['symbol','category','strategyType','strategyConfigJson','leverage','marginMode','positionMode','direction','maxNotionalUsdt','maxMarginUsdt','stopLossPercent','takeProfitPercent','liquidationBufferPercent','reduceOnlyEnabled'];
    const defaults = {
      symbol: 'BTCUSDT',
      category: 'linear',
      strategyType: 'pause',
      strategyConfigJson: '{}',
      leverage: 2,
      marginMode: 'isolated',
      positionMode: 'oneway',
      direction: 'long-only',
      maxNotionalUsdt: 100,
      maxMarginUsdt: 50,
      stopLossPercent: 2,
      takeProfitPercent: 4,
      liquidationBufferPercent: 15,
      reduceOnlyEnabled: true
    };
    let selectedSymbol = new URLSearchParams(window.location.search).get('symbol')?.toUpperCase() || null;
    let creating = false;
    let dirty = false;
    let latest = null;

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
    const setUrl = () => {
      const url = new URL(window.location.href);
      if (selectedSymbol && !creating) {
        url.searchParams.set('symbol', selectedSymbol);
      } else {
        url.searchParams.delete('symbol');
      }
      window.history.replaceState({}, '', url);
    };
    const updateForm = (settings) => {
      byId('symbol').value = settings.symbol;
      byId('category').value = settings.category;
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
      byId('liquidationBufferPercent').value = settings.liquidationBufferPercent;
      byId('reduceOnlyEnabled').value = String(settings.reduceOnlyEnabled);
    };
    const readPayload = () => ({
      symbol: byId('symbol').value,
      category: byId('category').value,
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
      reduceOnlyEnabled: byId('reduceOnlyEnabled').value === 'true'
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
      byId('configRows').innerHTML = configs.length === 0
        ? '<tr><td colspan="7">No futures configs yet.</td></tr>'
        : configs.map(config => `
          <tr data-symbol="${escapeHtml(config.symbol)}" class="${config.isSelected && !creating ? 'selected' : ''}">
            <td><strong>${escapeHtml(config.symbol)}</strong><br><span class="subtle">${escapeHtml(config.category)}</span></td>
            <td>${escapeHtml(config.strategyType)}</td>
            <td>${escapeHtml(config.direction)}</td>
            <td>${formatNumber(config.leverage)}x</td>
            <td>${formatNumber(config.maxNotionalUsdt)}</td>
            <td>${formatNumber(config.maxMarginUsdt)}</td>
            <td>${formatDate(config.updatedAt)}</td>
          </tr>`).join('');
    };
    const renderPosition = (data) => {
      const p = data.position;
      const error = data.positionError ? `<div class="notice">Position sync unavailable: ${escapeHtml(data.positionError)}</div>` : '';
      byId('positionStats').innerHTML = [
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
    const renderAutoRecommendation = (recommendation) => {
      byId('autoRecommendationReason').textContent = recommendation.reason || '-';
      byId('autoRecommendationStats').innerHTML = [
        ['Strategy', escapeHtml(recommendation.strategyType)],
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
      renderPosition(data);
      renderAutoRecommendation(data.autoRecommendation);
      byId('strategyActions').innerHTML = data.strategyActions.map(action => `<span class="chip">${escapeHtml(action)}</span>`).join('');
      if (force || !dirty) {
        updateForm(creating ? defaults : data.settings);
        dirty = creating;
      }
    };

    fields.forEach(id => byId(id).addEventListener('input', () => { dirty = true; }));
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

    private static FuturesSettingsView MapSettings(FuturesBotSettings settings) => new()
    {
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
        ReduceOnlyEnabled = settings.ReduceOnlyEnabled
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

    private async Task<FuturesPositionView?> GetPaperPositionAsync(
        FuturesBotSettings settings,
        CancellationToken cancellationToken)
    {
        var state = await _repository.GetBotStateAsync(FuturesStateKeys.ForSymbol(settings.Symbol), cancellationToken);
        return state is null ? null : MapPosition(settings, state);
    }

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

    private static FuturesAutoRecommendationView MapAutoRecommendation(FuturesAutoConfigRecommendation recommendation) => new()
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
        LastPrice = recommendation.Metrics.LastPrice,
        MovePercent = recommendation.Metrics.MovePercent,
        AtrPercent = recommendation.Metrics.AtrPercent,
        DrawdownPercent = recommendation.Metrics.DrawdownPercent,
        Support = recommendation.Metrics.Support,
        Resistance = recommendation.Metrics.Resistance
    };

    private static FuturesBotSettings BuildRecommendedSettings(
        FuturesBotSettings currentSettings,
        FuturesAutoConfigRecommendation recommendation) => new()
    {
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
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static FuturesBotSettings BuildDefaultSettings(FuturesOptions options, string symbol = "BTCUSDT") => new()
    {
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

        if (NormalizeToken(request.Direction) != "longonly")
        {
            errors.Add("MVP supports only long-only futures trading. Shorts are a later phase.");
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

        if (request.LiquidationBufferPercent < 0m)
        {
            errors.Add("Liquidation buffer percent cannot be negative.");
        }

        return errors;
    }

    private static FuturesStrategyType? ParseFuturesStrategyType(string? value) =>
        NormalizeToken(value) switch
        {
            "trendfollow" or "trend" or "trendfollowing" => FuturesStrategyType.TrendFollow,
            "breakout" => FuturesStrategyType.Breakout,
            "gridlongonly" or "gridlong" or "grid" => FuturesStrategyType.GridLongOnly,
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
            FuturesStrategyType.ReduceOnly => "reduceonly",
            FuturesDirection.LongOnly => "long-only",
            FuturesDirection.ShortOnly => "short-only",
            FuturesDirection.LongAndShort => "long+short",
            FuturesMarginMode.Isolated => "isolated",
            FuturesMarginMode.Cross => "cross",
            FuturesPositionMode.OneWay => "oneway",
            FuturesPositionMode.Hedge => "hedge",
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
