namespace BybitGridBot.App;

public static class FuturesBacktestPage
{
    public static string Render() => """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Futures Backtest</title>
  <style>
    :root {
      --bg: #f6f7f4;
      --ink: #17201b;
      --muted: #657069;
      --panel: #ffffff;
      --line: #dfe5df;
      --good: #0b7a53;
      --bad: #b42318;
      --warn: #986a00;
      --accent: #245bdb;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--ink);
      font: 14px/1.45 Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    .shell { width: min(1440px, calc(100% - 28px)); margin: 0 auto; padding: 22px 0 34px; }
    header { display: flex; align-items: flex-end; justify-content: space-between; gap: 18px; margin-bottom: 18px; }
    h1 { margin: 0; font-size: clamp(24px, 3vw, 38px); line-height: 1.05; letter-spacing: 0; }
    .sub { margin-top: 8px; color: var(--muted); }
    .actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; justify-content: flex-end; }
    .btn, .link {
      appearance: none;
      border: 1px solid #1d4ed8;
      background: var(--accent);
      color: #fff;
      min-height: 36px;
      padding: 7px 12px;
      border-radius: 7px;
      font-weight: 720;
      cursor: pointer;
      text-decoration: none;
      display: inline-flex;
      align-items: center;
    }
    .btn.secondary, .link.secondary { background: #fff; color: var(--accent); }
    .btn.danger { border-color: var(--bad); background: var(--bad); }
    .btn:disabled { cursor: wait; opacity: .62; }
    .run-field { display: grid; gap: 3px; min-width: 84px; }
    .run-field label { color: var(--muted); font-size: 11px; font-weight: 650; text-transform: uppercase; letter-spacing: .04em; }
    .run-field input { width: 100%; min-height: 36px; border: 1px solid var(--line); border-radius: 7px; padding: 7px 9px; font: inherit; font-variant-numeric: tabular-nums; }
    .run-field select { width: 100%; min-height: 36px; border: 1px solid var(--line); border-radius: 7px; padding: 7px 9px; font: inherit; background: #fff; }
    .run-field.check { min-width: 92px; }
    .run-field.check input { width: 18px; min-height: 18px; align-self: center; }
    .panel, .metric { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
    .metric { padding: 14px; min-width: 0; }
    .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .04em; }
    .value { margin-top: 7px; font-size: 22px; font-weight: 720; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
    .metrics { display: grid; grid-template-columns: repeat(8, minmax(0, 1fr)); gap: 12px; margin-bottom: 14px; }
    .progress-wrap { padding: 14px 16px; border-bottom: 1px solid var(--line); }
    .progress-top { display: flex; justify-content: space-between; gap: 12px; color: var(--muted); margin-bottom: 9px; flex-wrap: wrap; }
    .bar { height: 12px; background: #e8ece8; border-radius: 999px; overflow: hidden; }
    .bar > div { height: 100%; background: var(--accent); width: 0%; transition: width .25s ease; }
    .grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; margin-top: 14px; }
    .panel h2 { margin: 0; padding: 14px 16px; font-size: 16px; border-bottom: 1px solid var(--line); }
    table { width: 100%; border-collapse: collapse; table-layout: fixed; }
    th, td { padding: 10px 12px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: middle; font-variant-numeric: tabular-nums; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    th { color: var(--muted); font-size: 12px; font-weight: 650; }
    tr:last-child td { border-bottom: 0; }
    .empty { padding: 24px 16px; color: var(--muted); }
    .pos { color: var(--good); }
    .neg { color: var(--bad); }
    .muted { color: var(--muted); }
    @media (max-width: 1100px) {
      .metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .grid { grid-template-columns: 1fr; }
    }
    @media (max-width: 720px) {
      .shell { width: min(100% - 18px, 1440px); padding-top: 14px; }
      header { align-items: flex-start; flex-direction: column; }
      .actions { justify-content: flex-start; }
      .metrics { grid-template-columns: 1fr; }
      th, td { padding: 9px 8px; }
    }
  </style>
</head>
<body>
  <main class="shell">
    <header>
      <div>
        <h1>Backtest 4H NY Strategy</h1>
        <div class="sub">Тестирует score-based regime router. Live gate считается только по strategy:symbol:direction; symbol-only список оставлен как legacy диагностика.</div>
      </div>
      <div class="actions">
        <a class="link secondary" href="/futures">Futures bot</a>
        <div class="run-field">
          <label for="daysInput">Days</label>
          <input id="daysInput" type="number" min="1" max="365" step="1" value="90" />
        </div>
        <div class="run-field">
          <label for="symbolsInput">Pairs</label>
          <input id="symbolsInput" type="number" min="1" max="200" step="1" value="30" />
        </div>
        <div class="run-field">
          <label for="modeInput">Mode</label>
          <select id="modeInput">
            <option value="ScoreBasedRouter">Router</option>
            <option value="TurtleOnly">Turtle only</option>
          </select>
        </div>
        <div class="run-field check">
          <label for="nyBounceInput">NY bounce</label>
          <input id="nyBounceInput" type="checkbox" checked />
        </div>
        <div class="run-field">
          <label for="turtleDirectionsInput">Turtle side</label>
          <select id="turtleDirectionsInput">
            <option value="">Long+Short</option>
            <option value="Long">Long</option>
            <option value="Short">Short</option>
          </select>
        </div>
        <div class="run-field">
          <label for="turtleSystemsInput">Turtle sys</label>
          <select id="turtleSystemsInput">
            <option value="">S1+S2</option>
            <option value="S1">S1</option>
            <option value="S2">S2</option>
          </select>
        </div>
        <div class="run-field">
          <label for="turtleRiskInput">Turtle risk %</label>
          <input id="turtleRiskInput" type="number" min="0" max="100" step="0.05" value="0" />
        </div>
        <div class="run-field">
          <label for="initialEquityInput">Capital</label>
          <input id="initialEquityInput" type="number" min="0.00000001" max="999999999" step="1" value="1000" />
        </div>
        <div class="run-field">
          <label for="weekdaysInput">Weekdays</label>
          <input id="weekdaysInput" type="text" value="" placeholder="Mon,Tue" />
        </div>
        <div class="run-field">
          <label for="hoursInput">NY hours</label>
          <input id="hoursInput" type="text" value="" placeholder="10,11" />
        </div>
        <div class="run-field">
          <label for="maxTradeLossInput">Max loss %</label>
          <input id="maxTradeLossInput" type="number" min="0" max="100" step="0.1" value="2" />
        </div>
        <div class="run-field">
          <label for="maxDrawdownInput">Max DD %</label>
          <input id="maxDrawdownInput" type="number" min="0" max="100" step="0.1" value="30" />
        </div>
        <button class="btn secondary" id="copyDiagnostics" type="button">Copy diagnostics</button>
        <button class="btn" id="start" type="button">Start</button>
        <button class="btn danger" id="stop" type="button">Stop</button>
      </div>
    </header>

    <section class="panel" style="margin-bottom:14px">
      <div class="progress-wrap">
        <div class="progress-top">
          <span id="status">Not started</span>
          <span id="eta">ETA: -</span>
        </div>
        <div class="bar"><div id="bar"></div></div>
      </div>
    </section>

    <section class="metrics">
      <div class="metric"><div class="label">MTM PnL</div><div class="value" id="netPnl">-</div></div>
      <div class="metric"><div class="label">Closed PnL</div><div class="value" id="closedPnl">-</div></div>
      <div class="metric"><div class="label">Forced-close PnL</div><div class="value" id="forcedClosedPnl">-</div></div>
      <div class="metric"><div class="label">MTM drawdown</div><div class="value" id="drawdown">-</div></div>
      <div class="metric"><div class="label">Win rate</div><div class="value" id="winRate">-</div></div>
      <div class="metric"><div class="label">Profit factor</div><div class="value" id="profitFactor">-</div></div>
      <div class="metric"><div class="label">Average R</div><div class="value" id="averageR">-</div></div>
      <div class="metric"><div class="label">Trades/day</div><div class="value" id="tradesDay">-</div></div>
      <div class="metric"><div class="label">Breakouts</div><div class="value" id="breakouts">-</div></div>
      <div class="metric"><div class="label">Live allowed</div><div class="value" id="eligibleCount">-</div></div>
      <div class="metric"><div class="label">Open @ end</div><div class="value" id="openAtEnd">-</div></div>
    </section>

    <section class="grid">
      <div class="panel">
        <h2>Walk-forward gates</h2>
        <table><thead><tr><th>Allowed strategy:symbol:direction</th><th>Excluded</th></tr></thead><tbody id="wfSymbols"><tr><td colspan="2" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Gate diagnostics</h2>
        <table><thead><tr><th>Gate</th><th>Live</th><th>Reason</th><th>Opt</th><th>OOS closed</th><th>OOS open</th><th>OOS forced</th></tr></thead><tbody id="gateDiagnostics"><tr><td colspan="7" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Walk-forward metrics</h2>
        <table><thead><tr><th>Window</th><th>Trades/day</th><th>PnL</th><th>PF</th></tr></thead><tbody id="wfMetrics"><tr><td colspan="4" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Backtest timings</h2>
        <table><thead><tr><th>Stage</th><th>Count</th><th>Total ms</th><th>Avg / max ms</th></tr></thead><tbody id="timings"><tr><td colspan="4" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Best symbols</h2>
        <table><thead><tr><th>Symbol</th><th>Trades</th><th>PnL</th><th>WR</th><th>Largest win</th></tr></thead><tbody id="best"><tr><td colspan="5" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Worst symbols</h2>
        <table><thead><tr><th>Symbol</th><th>Trades</th><th>PnL</th><th>WR</th><th>Largest win</th></tr></thead><tbody id="worst"><tr><td colspan="5" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Long vs short</h2>
        <table><thead><tr><th>Side</th><th>Trades</th><th>PnL</th><th>Avg R</th></tr></thead><tbody id="sides"><tr><td colspan="4" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Strategy performance</h2>
        <table><thead><tr><th>Strategy</th><th>Trades</th><th>PnL</th><th>PF</th></tr></thead><tbody id="strategies"><tr><td colspan="4" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Pattern performance</h2>
        <table><thead><tr><th>Pattern</th><th>Trades</th><th>PnL</th><th>WR</th></tr></thead><tbody id="patterns"><tr><td colspan="4" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Weekday performance</h2>
        <table><thead><tr><th>Day</th><th>Trades</th><th>PnL</th><th>WR</th></tr></thead><tbody id="weekdays"><tr><td colspan="4" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Hour performance</h2>
        <table><thead><tr><th>Hour NY</th><th>Trades</th><th>PnL</th><th>WR</th></tr></thead><tbody id="hours"><tr><td colspan="4" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Recent trades</h2>
        <table><thead><tr><th>Symbol</th><th>Side</th><th>Pattern</th><th>Exit</th><th>PnL</th><th>R</th></tr></thead><tbody id="trades"><tr><td colspan="6" class="empty">Нет данных</td></tr></tbody></table>
      </div>
      <div class="panel">
        <h2>Open at backtest end</h2>
        <table><thead><tr><th>Symbol</th><th>Side</th><th>Pattern</th><th>Exit</th><th>PnL</th><th>R</th></tr></thead><tbody id="openTrades"><tr><td colspan="6" class="empty">Нет данных</td></tr></tbody></table>
      </div>
    </section>
  </main>

  <script>
    const fmt = new Intl.NumberFormat('en-US', { maximumFractionDigits: 6 });
    const money = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const byId = id => document.getElementById(id);
    const cls = v => Number(v) >= 0 ? 'pos' : 'neg';
    const pnl = v => `${Number(v) >= 0 ? '+' : ''}${money.format(Number(v || 0))}`;
    const pct = v => `${fmt.format(Number(v || 0))}%`;
    let latestStatus = null;
    let settingsHydrated = false;
    let settingsDirty = false;
    const settingsFieldIds = [
      'daysInput',
      'symbolsInput',
      'modeInput',
      'nyBounceInput',
      'turtleDirectionsInput',
      'turtleSystemsInput',
      'turtleRiskInput',
      'initialEquityInput',
      'weekdaysInput',
      'hoursInput',
      'maxTradeLossInput',
      'maxDrawdownInput'
    ];

    async function status() {
      const response = await fetch('/api/futures/backtest', { cache: 'no-store' });
      if (!response.ok) throw new Error(`Status ${response.status}`);
      render(await response.json());
    }

    async function start() {
      byId('start').disabled = true;
      const payload = readSettings();
      const response = await fetch('/api/futures/backtest/start', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });
      if (!response.ok) throw new Error(`Start ${response.status}`);
      settingsDirty = false;
      render(await response.json());
    }

    function readSettings() {
      return {
        days: clampInt(byId('daysInput').value, 1, 365, 90),
        symbols: clampInt(byId('symbolsInput').value, 1, 200, 20),
        mode: byId('modeInput').value || 'ScoreBasedRouter',
        runNyBounceRouter: byId('nyBounceInput').checked,
        turtleAllowedDirections: byId('turtleDirectionsInput').value || '',
        turtleAllowedSystems: byId('turtleSystemsInput').value || '',
        turtleRiskPerUnitPercent: clampDecimal(byId('turtleRiskInput').value, 0, 100, 0),
        initialEquityUsdt: clampDecimal(byId('initialEquityInput').value, 0.00000001, 999999999, 1000),
        turtleAllowedWeekdays: byId('weekdaysInput').value || '',
        turtleAllowedNyHours: byId('hoursInput').value || '',
        maxTradeLossEquityPercent: clampDecimal(byId('maxTradeLossInput').value, 0, 100, 0),
        maxProjectedDrawdownEquityPercent: clampDecimal(byId('maxDrawdownInput').value, 0, 100, 0)
      };
    }

    function applySettings(settings) {
      if (!settings) return;
      setValue('daysInput', settings.days);
      setValue('symbolsInput', settings.symbols);
      setValue('modeInput', settings.mode);
      setChecked('nyBounceInput', settings.runNyBounceRouter);
      setValue('turtleDirectionsInput', settings.turtleAllowedDirections);
      setValue('turtleSystemsInput', settings.turtleAllowedSystems);
      setValue('turtleRiskInput', settings.turtleRiskPerUnitPercent);
      setValue('initialEquityInput', settings.initialEquityUsdt);
      setValue('weekdaysInput', settings.turtleAllowedWeekdays);
      setValue('hoursInput', settings.turtleAllowedNyHours);
      setValue('maxTradeLossInput', settings.maxTradeLossEquityPercent);
      setValue('maxDrawdownInput', settings.maxProjectedDrawdownEquityPercent);
      settingsHydrated = true;
    }

    function setValue(id, value) {
      if (value === undefined || value === null) return;
      byId(id).value = String(value);
    }

    function setChecked(id, value) {
      if (value === undefined || value === null) return;
      byId(id).checked = Boolean(value);
    }

    function clampInt(value, min, max, fallback) {
      const parsed = Number.parseInt(value, 10);
      if (!Number.isFinite(parsed)) return fallback;
      return Math.max(min, Math.min(max, parsed));
    }

    function clampDecimal(value, min, max, fallback) {
      const parsed = Number.parseFloat(value);
      if (!Number.isFinite(parsed)) return fallback;
      return Math.max(min, Math.min(max, parsed));
    }

    async function stop() {
      byId('stop').disabled = true;
      const response = await fetch('/api/futures/backtest/stop', { method: 'POST' });
      if (!response.ok) throw new Error(`Stop ${response.status}`);
      render(await response.json());
    }

    function render(data) {
      latestStatus = data;
      if (data.appliedSettings && (!settingsHydrated || (!data.isRunning && !settingsDirty))) {
        applySettings(data.appliedSettings);
      }

      const progress = Number(data.progressPercent || 0);
      const statusText = data.status || 'Not started';
      const isStopping = statusText.toLowerCase().startsWith('stopping');
      byId('bar').style.width = `${Math.max(0, Math.min(100, progress))}%`;
      byId('status').textContent = `${statusText} (${fmt.format(progress)}%)`;
      byId('eta').textContent = data.isRunning && data.estimatedCompletedAt
        ? `ETA: ${new Date(data.estimatedCompletedAt).toLocaleString()}`
        : data.completedAt ? `Completed: ${new Date(data.completedAt).toLocaleString()}` : 'ETA: -';
      byId('start').disabled = Boolean(data.isRunning);
      byId('stop').disabled = !data.isRunning || isStopping;
      byId('daysInput').disabled = Boolean(data.isRunning);
      byId('symbolsInput').disabled = Boolean(data.isRunning);
      byId('modeInput').disabled = Boolean(data.isRunning);
      byId('nyBounceInput').disabled = Boolean(data.isRunning);
      byId('turtleDirectionsInput').disabled = Boolean(data.isRunning);
      byId('turtleSystemsInput').disabled = Boolean(data.isRunning);
      byId('turtleRiskInput').disabled = Boolean(data.isRunning);
      byId('initialEquityInput').disabled = Boolean(data.isRunning);
      byId('weekdaysInput').disabled = Boolean(data.isRunning);
      byId('hoursInput').disabled = Boolean(data.isRunning);
      byId('maxTradeLossInput').disabled = Boolean(data.isRunning);
      byId('maxDrawdownInput').disabled = Boolean(data.isRunning);
      byId('copyDiagnostics').disabled = !data.result;

      const result = data.result;
      if (!result) return;
      const m = result.metrics || {};
      const closedNetPnl = Number(m.closedNetPnl ?? m.netPnl ?? 0);
      const openUnrealizedPnl = Number(m.openUnrealizedPnl ?? result.openAtBacktestEndUnrealizedPnl ?? 0);
      const markToMarketNetPnl = Number(m.markToMarketNetPnl ?? (closedNetPnl + openUnrealizedPnl));
      const forcedClosedNetPnl = Number(m.forcedClosedNetPnl ?? markToMarketNetPnl);
      const markToMarketDrawdown = Number(m.markToMarketMaxDrawdown ?? m.maxDrawdown ?? 0);
      const markToMarketDrawdownPercent = Number(m.markToMarketMaxDrawdownPercent ?? m.maxDrawdownPercent ?? 0);
      byId('netPnl').textContent = pnl(markToMarketNetPnl);
      byId('netPnl').className = `value ${cls(markToMarketNetPnl)}`;
      byId('closedPnl').textContent = pnl(closedNetPnl);
      byId('closedPnl').className = `value ${cls(closedNetPnl)}`;
      byId('forcedClosedPnl').textContent = `${pnl(forcedClosedNetPnl)} / cost ${pnl(m.forcedClosedExitCost || 0)}`;
      byId('forcedClosedPnl').className = `value ${cls(forcedClosedNetPnl)}`;
      byId('drawdown').textContent = `${money.format(markToMarketDrawdown)} (${pct(markToMarketDrawdownPercent)})`;
      byId('winRate').textContent = pct(m.winRate);
      byId('profitFactor').textContent = fmt.format(m.profitFactor || 0);
      byId('averageR').textContent = fmt.format(m.averageR || 0);
      byId('tradesDay').textContent = fmt.format(m.tradesPerDay || 0);
      byId('breakouts').textContent = `${result.falseBreakoutCount || 0} / ${result.trueBreakoutBlockedCount || 0}`;
      const eligibleGates = Array.isArray(result.eligibleStrategySymbolDirections) ? result.eligibleStrategySymbolDirections : [];
      const excludedGates = Array.isArray(result.excludedStrategySymbolDirections) ? result.excludedStrategySymbolDirections : [];
      byId('eligibleCount').textContent = `${Number(result.liveAllowedStrategyGatesCount ?? eligibleGates.length)}`;
      byId('openAtEnd').textContent = `${result.openAtBacktestEndCount || 0} (${pnl(openUnrealizedPnl)})`;
      byId('wfSymbols').innerHTML = walkForwardSymbolRows(eligibleGates, excludedGates);
      byId('gateDiagnostics').innerHTML = gateDiagnosticRows(result.gateDiagnostics || []);
      byId('wfMetrics').innerHTML = walkForwardMetricRows(result);
      byId('timings').innerHTML = timingRows(result.timings || []);
      byId('best').innerHTML = symbolRows(result.bestSymbols || []);
      byId('worst').innerHTML = symbolRows(result.worstSymbols || []);
      byId('sides').innerHTML = sideRows(result.longShort || []);
      byId('strategies').innerHTML = strategyRows(result.strategyPerformance || []);
      byId('patterns').innerHTML = perfRows(result.patternPerformance || [], 'bucket');
      byId('weekdays').innerHTML = perfRows(result.weekdayPerformance || [], 'bucket');
      byId('hours').innerHTML = perfRows(result.hourPerformance || [], 'bucket');
      byId('trades').innerHTML = tradeRows(result.recentTrades || []);
      byId('openTrades').innerHTML = tradeRows(result.openAtBacktestEndTrades || []);
    }

    function perfRows(items, key) {
      return items.length ? items.map(item => `
        <tr>
          <td>${item[key] || '-'}</td>
          <td>${item.trades || 0}</td>
          <td class="${cls(item.markToMarketNetPnl ?? item.netPnl)}">${pnl(item.markToMarketNetPnl ?? item.netPnl)}</td>
          <td>${pct(item.winRate)}</td>
        </tr>`).join('') : '<tr><td colspan="4" class="empty">Нет данных</td></tr>';
    }

    function symbolRows(items) {
      return items.length ? items.map(item => `
        <tr>
          <td>${item.symbol || '-'}</td>
          <td>${item.trades || 0}</td>
          <td class="${cls(item.netPnl)}">${pnl(item.netPnl)}</td>
          <td>${pct(item.winRate)}</td>
          <td>${pct(item.largestWinGrossProfitPercent)}</td>
        </tr>`).join('') : '<tr><td colspan="5" class="empty">Нет данных</td></tr>';
    }

    function walkForwardSymbolRows(eligible, excluded) {
      if (!eligible.length && !excluded.length) return '<tr><td colspan="2" class="empty">Нет данных</td></tr>';
      return `<tr><td>${eligible.slice(0, 60).join(', ') || '-'}</td><td>${excluded.slice(0, 60).join(', ') || '-'}</td></tr>`;
    }

    function walkForwardMetricRows(result) {
      const optimization = result.optimizationMetrics || {};
      const outOfSample = result.outOfSampleMetrics || {};
      const filteredOutOfSample = result.filteredOutOfSampleMetrics || {};
      const optimizationLabel = result.optimizationWindowLabel || 'optimization';
      const outOfSampleLabel = result.outOfSampleWindowLabel || 'out-of-sample';
      return [
        [optimizationLabel, optimization],
        [outOfSampleLabel, outOfSample],
        [`${outOfSampleLabel} live-gated`, filteredOutOfSample]
      ].map(([label, item]) => `
        <tr>
          <td>${label}</td>
          <td>${fmt.format(item.tradesPerDay || 0)}/day</td>
          <td class="${cls(item.markToMarketNetPnl ?? item.netPnl)}">${pnl(item.markToMarketNetPnl ?? item.netPnl)}</td>
          <td>${fmt.format(item.profitFactor || 0)}</td>
        </tr>`).join('');
    }

    function gateDiagnosticRows(items) {
      return items.length ? items.slice(0, 80).map(item => `
        <tr>
          <td>${item.key || '-'}</td>
          <td>${item.isLiveAllowed ? 'yes' : 'no'}</td>
          <td>${item.reason || '-'}</td>
          <td>${item.optimizationTrades || 0} / ${pnl(item.optimizationNetPnl)} / PF ${fmt.format(item.optimizationProfitFactor || 0)}</td>
          <td>${item.oosClosedTrades || 0} / ${pnl(item.oosClosedNetPnl)} / PF ${fmt.format(item.oosClosedProfitFactor || 0)} / DD ${pct(item.oosClosedMaxDrawdownPercent)} / medR ${fmt.format(item.oosClosedMedianR || 0)}</td>
          <td>${item.oosOpenTrades || 0} / ${pnl(item.oosOpenNetPnl)} / MTM ${pnl(item.oosMarkToMarketNetPnl)}</td>
          <td>${item.oosForcedClosedTrades || 0} / ${pnl(item.oosForcedClosedNetPnl)} / DD ${pct(item.oosForcedClosedMaxDrawdownPercent)}</td>
        </tr>`).join('') : '<tr><td colspan="7" class="empty">Нет данных</td></tr>';
    }

    function timingRows(items) {
      return items.length ? items.slice(0, 30).map(item => `
        <tr>
          <td>${item.stage || '-'}</td>
          <td>${item.count || 0}</td>
          <td>${fmt.format(item.totalMilliseconds || 0)}</td>
          <td>${fmt.format(item.averageMilliseconds || 0)} / ${fmt.format(item.maxMilliseconds || 0)}</td>
        </tr>`).join('') : '<tr><td colspan="4" class="empty">Нет данных</td></tr>';
    }

    async function copyDiagnostics() {
      if (!latestStatus?.result) {
        byId('status').textContent = 'No backtest result to copy';
        return;
      }

      const text = buildDiagnostics(latestStatus);
      try {
        if (navigator.clipboard?.writeText) {
          await navigator.clipboard.writeText(text);
        } else {
          const textarea = document.createElement('textarea');
          textarea.value = text;
          textarea.setAttribute('readonly', '');
          textarea.style.position = 'fixed';
          textarea.style.left = '-9999px';
          document.body.appendChild(textarea);
          textarea.select();
          document.execCommand('copy');
          textarea.remove();
        }

        byId('copyDiagnostics').textContent = 'Copied';
        setTimeout(() => { byId('copyDiagnostics').textContent = 'Copy diagnostics'; }, 1400);
      } catch (error) {
        byId('status').textContent = `Copy failed: ${error.message}`;
      }
    }

    function buildDiagnostics(data) {
      const result = data.result || {};
      const lines = [
        'BYBIT FUTURES BACKTEST DIAGNOSTICS',
        `generatedAt=${new Date().toISOString()}`,
        `status=${data.status || '-'}`,
        `progressPercent=${data.progressPercent || 0}`,
        `startedAt=${data.startedAt || '-'}`,
        `completedAt=${data.completedAt || '-'}`,
        '',
        'RUN',
        `strategy=${result.strategyName || '-'}`,
        `period=${result.periodStart || '-'} .. ${result.periodEnd || '-'}`,
        `symbolsRequested=${result.symbolsRequested || 0}`,
        `symbolsProcessed=${result.symbolsProcessed || 0}`,
        `tradesCount=${result.tradesCount || 0}`,
        `openAtBacktestEndCount=${result.openAtBacktestEndCount || 0}`,
        `openAtBacktestEndUnrealizedPnl=${Number(result.openAtBacktestEndUnrealizedPnl || 0)}`,
        `initialEquityUsdt=${Number(result.initialEquityUsdt || 0)}`,
        `hardRiskCapBlockedCount=${result.hardRiskCapBlockedCount || 0}`,
        `liquidationCount=${result.liquidationCount || 0}`,
        `maxTradeLossEquityPercent=${Number(result.maxTradeLossEquityPercent || 0)}`,
        `maxProjectedDrawdownEquityPercent=${Number(result.maxProjectedDrawdownEquityPercent || 0)}`,
        `leverage=${Number(result.leverage || 0)}`,
        `minLiquidationBufferPercent=${Number(result.minLiquidationBufferPercent || 0)}`,
        `runNyBounceRouter=${Boolean(result.runNyBounceRouter)}`,
        `turtleAllowedDirections=${result.turtleAllowedDirections || '-'}`,
        `turtleAllowedSystems=${result.turtleAllowedSystems || '-'}`,
        `turtleRiskPerUnitPercent=${Number(result.turtleRiskPerUnitPercent || 0)}`,
        `optimizationWindow=${result.optimizationWindowLabel || '-'}`,
        `outOfSampleWindow=${result.outOfSampleWindowLabel || '-'}`,
        `liveUseEligibleStrategyGatesOnly=${Boolean(result.liveUseEligibleStrategyGatesOnly)}`,
        `liveAllowedStrategyGatesCount=${Number(result.liveAllowedStrategyGatesCount || 0)}`,
        `liveEligibleGateSizeMultiplier=${Number(result.liveEligibleGateSizeMultiplier || 0)}`,
        `liveIneligibleGateSizeMultiplier=${Number(result.liveIneligibleGateSizeMultiplier || 0)}`,
        `liveEligibleDirections=${result.liveEligibleDirections || '-'}`,
        `falseBreakoutCount=${result.falseBreakoutCount || 0}`,
        `trueBreakoutBlockedCount=${result.trueBreakoutBlockedCount || 0}`,
        '',
        metricBlock('MAIN_OOS_ALL_SYMBOLS', result.metrics || {}),
        metricBlock('OPTIMIZATION', result.optimizationMetrics || {}),
        metricBlock('OUT_OF_SAMPLE_ALL_SYMBOLS', result.outOfSampleMetrics || {}),
        metricBlock('OUT_OF_SAMPLE_LIVE_GATED', result.filteredOutOfSampleMetrics || {}),
        '',
        `liveAllowedClosedStrategySymbolDirections(${(result.eligibleStrategySymbolDirections || []).length})=${(result.eligibleStrategySymbolDirections || []).join(', ') || '-'}`,
        `diagnosticOpenProfitableStrategySymbolDirections(${(result.openProfitableStrategySymbolDirections || []).length})=${(result.openProfitableStrategySymbolDirections || []).join(', ') || '-'}`,
        `diagnosticMarkToMarketProfitableStrategySymbolDirections(${(result.markToMarketProfitableStrategySymbolDirections || []).length})=${(result.markToMarketProfitableStrategySymbolDirections || []).join(', ') || '-'}`,
        `watchlistStrategySymbolDirections(${(result.watchlistStrategySymbolDirections || []).length})=${(result.watchlistStrategySymbolDirections || []).join(', ') || '-'}`,
        `excludedStrategySymbolDirections(${(result.excludedStrategySymbolDirections || []).length})=${(result.excludedStrategySymbolDirections || []).join(', ') || '-'}`,
        `legacyEligibleSymbols_symbolOnly_notLiveGate(${(result.eligibleSymbols || []).length})=${(result.eligibleSymbols || []).join(', ') || '-'}`,
        `legacyExcludedSymbols_symbolOnly_notLiveGate(${(result.excludedSymbols || []).length})=${(result.excludedSymbols || []).join(', ') || '-'}`,
        '',
        walkForwardGateBlock(result.walkForwardStrategyGates || []),
        '',
        tableBlock('BEST_SYMBOLS', result.bestSymbols || []),
        tableBlock('WORST_SYMBOLS', result.worstSymbols || []),
        tableBlock('LONG_SHORT', result.longShort || []),
        tableBlock('GATE_DIAGNOSTICS', result.gateDiagnostics || []),
        tableBlock('STRATEGY_PERFORMANCE', result.strategyPerformance || []),
        tableBlock('PATTERN', result.patternPerformance || []),
        tableBlock('WEEKDAY', result.weekdayPerformance || []),
        tableBlock('HOUR_NY', result.hourPerformance || []),
        tableBlock('TIMINGS', result.timings || []),
        tableBlock('OPEN_AT_BACKTEST_END', result.openAtBacktestEndTrades || []),
        tableBlock('RECENT_TRADES', result.recentTrades || [])
      ];
      return lines.join('\n');
    }

    function metricBlock(name, item) {
      return [
        name,
        `tradesCount=${Number(item.tradesCount || 0)}`,
        `netPnl=${Number(item.netPnl || 0)}`,
        `closedNetPnl=${Number(item.closedNetPnl ?? item.netPnl ?? 0)}`,
        `openUnrealizedPnl=${Number(item.openUnrealizedPnl || 0)}`,
        `markToMarketNetPnl=${Number(item.markToMarketNetPnl ?? item.netPnl ?? 0)}`,
        `forcedClosedNetPnl=${Number(item.forcedClosedNetPnl ?? item.markToMarketNetPnl ?? item.netPnl ?? 0)}`,
        `forcedClosedExitCost=${Number(item.forcedClosedExitCost || 0)}`,
        `pnlWithoutTop1=${Number(item.pnlWithoutTop1 || 0)}`,
        `pnlWithoutTop2=${Number(item.pnlWithoutTop2 || 0)}`,
        `maxDrawdown=${Number(item.maxDrawdown || 0)}`,
        `maxDrawdownPercent=${Number(item.maxDrawdownPercent || 0)}`,
        `markToMarketMaxDrawdown=${Number(item.markToMarketMaxDrawdown ?? item.maxDrawdown ?? 0)}`,
        `markToMarketMaxDrawdownPercent=${Number(item.markToMarketMaxDrawdownPercent ?? item.maxDrawdownPercent ?? 0)}`,
        `forcedClosedMaxDrawdown=${Number(item.forcedClosedMaxDrawdown ?? item.markToMarketMaxDrawdown ?? item.maxDrawdown ?? 0)}`,
        `forcedClosedMaxDrawdownPercent=${Number(item.forcedClosedMaxDrawdownPercent ?? item.markToMarketMaxDrawdownPercent ?? item.maxDrawdownPercent ?? 0)}`,
        `winRate=${Number(item.winRate || 0)}`,
        `profitFactor=${Number(item.profitFactor || 0)}`,
        `averageR=${Number(item.averageR || 0)}`,
        `tradesPerDay=${Number(item.tradesPerDay || 0)}`
      ].join('\n');
    }

    function walkForwardGateBlock(items) {
      if (!items.length) return 'WALK_FORWARD_STRATEGY_GATES\n-';
      const lines = ['WALK_FORWARD_STRATEGY_GATES'];
      for (const item of items.slice(0, 120)) {
        const opt = item.optimizationMetrics || {};
        const oos = item.outOfSampleMetrics || {};
        lines.push([
          item.isLiveAllowed ? 'ALLOWED' : 'excluded',
          item.key || '-',
          `optTrades=${Number(opt.tradesCount || 0)}`,
          `optPnl=${Number(opt.netPnl || 0)}`,
          `optPF=${Number(opt.profitFactor || 0)}`,
          `optAvgR=${Number(opt.averageR || 0)}`,
          `oosTrades=${Number(oos.tradesCount || 0)}`,
          `oosPnl=${Number(oos.netPnl || 0)}`,
          `oosPF=${Number(oos.profitFactor || 0)}`,
          `oosAvgR=${Number(oos.averageR || 0)}`
        ].join(' | '));
      }

      return lines.join('\n');
    }

    function tableBlock(name, items) {
      if (!items.length) return `${name}\n-`;
      return `${name}\n${items.slice(0, 100).map(item => JSON.stringify(item)).join('\n')}`;
    }

    function sideRows(items) {
      return items.length ? items.map(item => `
        <tr>
          <td>${item.side || '-'}</td>
          <td>${item.trades || 0}</td>
          <td class="${cls(item.netPnl)}">${pnl(item.netPnl)}</td>
          <td>${fmt.format(item.averageR || 0)}</td>
        </tr>`).join('') : '<tr><td colspan="4" class="empty">Нет данных</td></tr>';
    }

    function strategyRows(items) {
      return items.length ? items.map(item => `
        <tr>
          <td>${item.strategyName || '-'}</td>
          <td>${item.tradesCount || 0}</td>
          <td class="${cls(item.netPnl)}">${pnl(item.netPnl)}</td>
          <td>${fmt.format(item.profitFactor || 0)}</td>
        </tr>`).join('') : '<tr><td colspan="4" class="empty">Нет данных</td></tr>';
    }

    function tradeRows(items) {
      return items.length ? items.slice(0, 40).map(item => `
        <tr>
          <td>${item.symbol}</td>
          <td>${item.side}</td>
          <td>${item.pattern || '-'}</td>
          <td>${item.exitReason}</td>
          <td class="${cls(item.netPnl)}">${pnl(item.netPnl)}</td>
          <td>${fmt.format(item.rMultiple || 0)}</td>
        </tr>`).join('') : '<tr><td colspan="6" class="empty">Нет данных</td></tr>';
    }

    byId('start').addEventListener('click', () => start().catch(error => { byId('status').textContent = error.message; }));
    byId('stop').addEventListener('click', () => stop().catch(error => { byId('status').textContent = error.message; }));
    byId('copyDiagnostics').addEventListener('click', () => copyDiagnostics());
    for (const id of settingsFieldIds) {
      byId(id).addEventListener('input', () => { settingsDirty = true; });
      byId(id).addEventListener('change', () => { settingsDirty = true; });
    }

    byId('copyDiagnostics').disabled = true;
    status().catch(error => { byId('status').textContent = error.message; });
    setInterval(() => status().catch(() => {}), 5000);
  </script>
</body>
</html>
""";
}
