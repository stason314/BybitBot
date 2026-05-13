using BybitGridBot.Bybit;
using BybitGridBot.Domain;
using BybitGridBot.Storage;
using BybitGridBot.Strategy;
using Microsoft.Extensions.Options;

namespace BybitGridBot.App;

public interface IGridDashboardService
{
    Task<DashboardResponse> GetDashboardAsync(CancellationToken cancellationToken);
    Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken cancellationToken);
    string RenderDashboardPage();
}

public sealed class GridDashboardService : IGridDashboardService
{
    private readonly AppOptions _appOptions;
    private readonly GridOptions _defaultGridOptions;
    private readonly IBybitRestClient _bybitRestClient;
    private readonly IGridRepository _repository;
    private readonly GridStrategy _strategy;

    public GridDashboardService(
        IOptions<AppOptions> appOptions,
        IOptions<GridOptions> defaultGridOptions,
        IBybitRestClient bybitRestClient,
        IGridRepository repository,
        GridStrategy strategy)
    {
        _appOptions = appOptions.Value;
        _defaultGridOptions = defaultGridOptions.Value;
        _bybitRestClient = bybitRestClient;
        _repository = repository;
        _strategy = strategy;
    }

    public async Task<DashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var runtimeSettings = await _repository.GetRuntimeSettingsAsync(cancellationToken)
            ?? RuntimeGridOptionsFactory.ToRuntimeSettings(_defaultGridOptions);
        var gridOptions = RuntimeGridOptionsFactory.ToGridOptions(runtimeSettings, _defaultGridOptions);
        var state = await _repository.GetBotStateAsync(gridOptions.Symbol, cancellationToken)
            ?? new BotState
            {
                Symbol = gridOptions.Symbol,
                TradingMode = _appOptions.TradingMode,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        var levels = await _repository.GetGridLevelsAsync(gridOptions.Symbol, cancellationToken);
        if (levels.Count == 0)
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

        return new DashboardResponse
        {
            Settings = new DashboardSettings
            {
                Symbol = gridOptions.Symbol,
                Category = gridOptions.Category,
                LowerPrice = gridOptions.LowerPrice,
                UpperPrice = gridOptions.UpperPrice,
                Step = gridOptions.Step,
                OrderSizeUsdt = gridOptions.OrderSizeUsdt,
                StopLowerPrice = gridOptions.StopLowerPrice,
                StopUpperPrice = gridOptions.StopUpperPrice
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
            Orders = orders,
            ActiveOrders = activeOrders,
            GridLevels = levels.Select(level => level.Price).ToArray(),
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<UpdateSettingsResponse> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken cancellationToken)
    {
        var symbol = request.Symbol.Trim().ToUpperInvariant();
        var category = string.IsNullOrWhiteSpace(request.Category) ? "spot" : request.Category.Trim().ToLowerInvariant();
        var errors = ValidateRequest(symbol, category, request);
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

        await _repository.SaveRuntimeSettingsAsync(
            new GridBotSettings
            {
                Symbol = symbol,
                Category = category,
                LowerPrice = request.LowerPrice,
                UpperPrice = request.UpperPrice,
                Step = request.Step,
                OrderSizeUsdt = request.OrderSizeUsdt,
                StopLowerPrice = request.StopLowerPrice,
                StopUpperPrice = request.StopUpperPrice,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);

        return new UpdateSettingsResponse
        {
            Success = true,
            Message = "Settings saved. The bot will apply them on the next loop."
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
    h2 {
      margin: 0 0 18px;
      font: 700 22px/1.05 "Space Grotesk", "IBM Plex Sans", sans-serif;
      letter-spacing: -0.03em;
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
    input {
      width: 100%;
      padding: 12px 14px;
      border-radius: 14px;
      border: 1px solid rgba(29,35,31,0.14);
      background: rgba(255,255,255,0.84);
      color: var(--ink);
      font: inherit;
    }
    .full { grid-column: 1 / -1; }
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
          <div class="badge">Last sync <strong id="heroUpdated">-</strong></div>
        </div>
      </section>
      <section class="panel section">
        <h2>Runtime Settings</h2>
        <form id="settingsForm">
          <div><label for="symbol">Symbol</label><input id="symbol" name="symbol" placeholder="BILLUSDT" required /></div>
          <div><label for="category">Category</label><input id="category" name="category" value="spot" required /></div>
          <div><label for="lowerPrice">Grid Lower</label><input id="lowerPrice" name="lowerPrice" type="number" step="0.00000001" required /></div>
          <div><label for="upperPrice">Grid Upper</label><input id="upperPrice" name="upperPrice" type="number" step="0.00000001" required /></div>
          <div><label for="step">Grid Step</label><input id="step" name="step" type="number" step="0.00000001" required /></div>
          <div><label for="orderSizeUsdt">Order Size USDT</label><input id="orderSizeUsdt" name="orderSizeUsdt" type="number" step="0.00000001" required /></div>
          <div><label for="stopLowerPrice">Stop Lower</label><input id="stopLowerPrice" name="stopLowerPrice" type="number" step="0.00000001" required /></div>
          <div><label for="stopUpperPrice">Stop Upper</label><input id="stopUpperPrice" name="stopUpperPrice" type="number" step="0.00000001" required /></div>
          <div class="full"><button type="submit">Apply Settings</button></div>
        </form>
        <div class="status" id="formStatus"></div>
      </section>
    </div>

    <section class="stats" id="stats"></section>

    <div class="layout">
      <section class="panel section">
        <h2>Active Grid Levels</h2>
        <div class="grid-list" id="gridLevels"></div>
      </section>
      <section class="panel section">
        <h2>Active Orders</h2>
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
      <h2>Order History</h2>
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
    const settingsFieldIds = ['symbol', 'category', 'lowerPrice', 'upperPrice', 'step', 'orderSizeUsdt', 'stopLowerPrice', 'stopUpperPrice'];
    let settingsFormDirty = false;

    const isSettingsFormDirty = () => settingsFormDirty;
    const setSettingsFormDirty = (isDirty) => {
      settingsFormDirty = isDirty;
    };
    const updateSettingsForm = (settings) => {
      byId('symbol').value = settings.symbol;
      byId('category').value = settings.category;
      byId('lowerPrice').value = settings.lowerPrice;
      byId('upperPrice').value = settings.upperPrice;
      byId('step').value = settings.step;
      byId('orderSizeUsdt').value = settings.orderSizeUsdt;
      byId('stopLowerPrice').value = settings.stopLowerPrice;
      byId('stopUpperPrice').value = settings.stopUpperPrice;
    };
    const formatNumber = (value) => value === null || value === undefined ? "—" : Number(value).toLocaleString(undefined, { maximumFractionDigits: 8 });
    const formatSigned = (value) => {
      const number = Number(value ?? 0);
      const cls = number > 0 ? "positive" : number < 0 ? "negative" : "";
      return `<span class="value ${cls}">${number.toLocaleString(undefined, { maximumFractionDigits: 8 })}</span>`;
    };
    const formatDate = (value) => value ? new Date(value).toLocaleString() : "—";

    async function loadDashboard(options = {}) {
      const forceSettingsRefresh = Boolean(options.forceSettingsRefresh);
      const response = await fetch('/api/dashboard', { cache: 'no-store' });
      const data = await response.json();

      byId('modePill').textContent = `${data.state.tradingMode} mode`;
      byId('modePill').className = data.state.isPaused ? 'pill paused' : 'pill';
      byId('heroSymbol').textContent = data.settings.symbol;
      byId('heroPrice').textContent = formatNumber(data.state.currentPrice);
      byId('heroUpdated').textContent = formatDate(data.generatedAt);

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

    document.getElementById('settingsForm').addEventListener('submit', async (event) => {
      event.preventDefault();
      const payload = {
        symbol: byId('symbol').value,
        category: byId('category').value,
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
}
