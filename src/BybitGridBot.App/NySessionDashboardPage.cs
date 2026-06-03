namespace BybitGridBot.App;

public static class NySessionDashboardPage
{
    public static string Render() => """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Bybit Futures Bot</title>
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
    .status { display: flex; gap: 8px; align-items: center; color: var(--muted); flex-wrap: wrap; justify-content: flex-end; }
    .dot { width: 9px; height: 9px; border-radius: 50%; background: var(--good); box-shadow: 0 0 0 3px rgba(11,122,83,.12); }
    .metrics { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-bottom: 14px; }
    .metric, .panel { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; }
    .metric { padding: 14px; min-width: 0; }
    .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .04em; }
    .value { margin-top: 7px; font-size: 25px; font-weight: 720; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
    .grid { display: grid; grid-template-columns: minmax(0, 1.25fr) minmax(380px, .75fr); gap: 14px; align-items: start; }
    .panel { overflow: hidden; }
    .panel h2 { margin: 0; padding: 14px 16px; font-size: 16px; border-bottom: 1px solid var(--line); }
    table { width: 100%; border-collapse: collapse; table-layout: fixed; }
    th, td { padding: 10px 12px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: middle; font-variant-numeric: tabular-nums; }
    th { color: var(--muted); font-size: 12px; font-weight: 650; }
    td { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    tr:last-child td { border-bottom: 0; }
    .pair { font-weight: 720; }
    .pill { display: inline-flex; align-items: center; max-width: 100%; min-height: 24px; padding: 2px 8px; border-radius: 999px; background: #eef2ff; color: #2445a8; font-size: 12px; font-weight: 650; }
    .pill.signal { background: #e8f7ef; color: var(--good); }
    .pill.warn { background: #fff4d7; color: var(--warn); }
    .pos { color: var(--good); }
    .neg { color: var(--bad); }
    .muted { color: var(--muted); }
    .events { display: grid; gap: 8px; padding: 12px; }
    .event { border: 1px solid var(--line); border-radius: 8px; padding: 10px; background: #fbfcfb; }
    .event .top { display: flex; justify-content: space-between; gap: 10px; color: var(--muted); font-size: 12px; margin-bottom: 4px; }
    .empty { padding: 24px 16px; color: var(--muted); }
    .btn { appearance: none; border: 1px solid #1d4ed8; background: var(--accent); color: #fff; min-height: 36px; padding: 7px 12px; border-radius: 7px; font-weight: 720; cursor: pointer; }
    .btn { text-decoration: none; display: inline-flex; align-items: center; }
    @media (max-width: 1100px) {
      .metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .grid { grid-template-columns: 1fr; }
    }
    @media (max-width: 720px) {
      .shell { width: min(100% - 18px, 1440px); padding-top: 14px; }
      header { align-items: flex-start; flex-direction: column; }
      .status { justify-content: flex-start; }
      .metrics { grid-template-columns: 1fr; }
      th, td { padding: 9px 8px; }
      .hide-sm { display: none; }
    }
  </style>
</head>
<body>
  <main class="shell">
    <header>
      <div>
        <h1>Futures NY Session Bot</h1>
        <div class="sub">4h диапазон 08:00-12:00 New York, пул из 20 пар, вход после sweep и возврата.</div>
      </div>
      <div class="status"><a class="btn" href="/futures/backtest">Backtest</a><span class="dot"></span><span id="status">Loading</span><span id="updated"></span></div>
    </header>

    <section class="metrics">
      <div class="metric"><div class="label">Общий PnL</div><div class="value" id="totalPnl">-</div></div>
      <div class="metric"><div class="label">PnL за сутки</div><div class="value" id="dailyPnl">-</div></div>
      <div class="metric"><div class="label">Открытый PnL</div><div class="value" id="unrealizedPnl">-</div></div>
      <div class="metric"><div class="label">Режим</div><div class="value" id="mode">-</div></div>
    </section>

    <section class="grid">
      <div class="panel">
        <h2>Текущий пул пар</h2>
        <table>
          <thead>
            <tr>
              <th style="width:44px">#</th>
              <th>Пара</th>
              <th>Статус</th>
              <th class="hide-sm">Цена</th>
              <th class="hide-sm">High 4h</th>
              <th class="hide-sm">Low 4h</th>
              <th>Dist</th>
            </tr>
          </thead>
          <tbody id="poolRows"><tr><td colspan="7" class="empty">Loading</td></tr></tbody>
        </table>
      </div>

      <div class="side">
        <div class="panel">
          <h2>Открытые сделки</h2>
          <table>
            <thead>
              <tr><th>Пара</th><th>Side</th><th>Entry</th><th>Mark</th><th>PnL</th></tr>
            </thead>
            <tbody id="tradeRows"><tr><td colspan="5" class="empty">Нет открытых сделок</td></tr></tbody>
          </table>
        </div>
        <div class="panel" style="margin-top:14px">
          <h2>События</h2>
          <div class="events" id="events"><div class="empty">Событий пока нет</div></div>
        </div>
      </div>
    </section>
  </main>

  <script>
    const fmt = new Intl.NumberFormat('en-US', { maximumFractionDigits: 6 });
    const money = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    function cls(v) { return Number(v) >= 0 ? 'pos' : 'neg'; }
    function pnl(v) { return `${Number(v) >= 0 ? '+' : ''}${money.format(Number(v || 0))}`; }
    function pct(v) { return `${fmt.format(Number(v || 0))}%`; }
    function byId(id) { return document.getElementById(id); }
    function pill(state) {
      const c = state === 'Signal' ? 'signal' : (state.includes('swept') || state === 'Building range' ? 'warn' : '');
      return `<span class="pill ${c}">${state}</span>`;
    }

    async function load() {
      const response = await fetch('/api/futures/dashboard', { cache: 'no-store' });
      if (!response.ok) throw new Error(`Dashboard ${response.status}`);
      const data = await response.json();

      byId('status').textContent = data.status || 'Running';
      byId('updated').textContent = data.generatedAt ? new Date(data.generatedAt).toLocaleTimeString() : '';
      byId('mode').textContent = `${data.tradingMode || '-'}${data.strategyEnabled ? '' : ' off'}`;
      byId('totalPnl').textContent = pnl(data.totalPnl);
      byId('totalPnl').className = `value ${cls(data.totalPnl)}`;
      byId('dailyPnl').textContent = pnl(data.dailyPnl);
      byId('dailyPnl').className = `value ${cls(data.dailyPnl)}`;
      byId('unrealizedPnl').textContent = pnl(data.unrealizedPnl);
      byId('unrealizedPnl').className = `value ${cls(data.unrealizedPnl)}`;

      const pool = data.pool || [];
      byId('poolRows').innerHTML = pool.length ? pool.map(item => `
        <tr title="${item.reason || ''}">
          <td>${item.slot}</td>
          <td><span class="pair">${item.symbol}</span><div class="muted">${item.bias || ''}</div></td>
          <td>${pill(item.state || '-')}</td>
          <td class="hide-sm">${fmt.format(item.lastPrice || 0)}</td>
          <td class="hide-sm">${fmt.format(item.fourHourHigh || 0)}</td>
          <td class="hide-sm">${fmt.format(item.fourHourLow || 0)}</td>
          <td>${pct(Math.min(item.distanceToUpperPercent || 0, item.distanceToLowerPercent || 0))}</td>
        </tr>`).join('') : '<tr><td colspan="7" class="empty">Пул пока пуст</td></tr>';

      const trades = data.openTrades || [];
      byId('tradeRows').innerHTML = trades.length ? trades.map(trade => `
        <tr>
          <td><span class="pair">${trade.symbol}</span><div class="muted">SL ${fmt.format(trade.stopLoss || 0)} TP ${fmt.format(trade.takeProfit || 0)}</div></td>
          <td>${trade.side}</td>
          <td>${fmt.format(trade.entryPrice || 0)}</td>
          <td>${fmt.format(trade.markPrice || 0)}</td>
          <td class="${cls(trade.unrealizedPnl)}">${pnl(trade.unrealizedPnl)}<div class="muted">${pct(trade.unrealizedPnlPercent)}</div></td>
        </tr>`).join('') : '<tr><td colspan="5" class="empty">Нет открытых сделок</td></tr>';

      const events = data.events || [];
      byId('events').innerHTML = events.length ? events.map(event => `
        <div class="event">
          <div class="top"><span>${event.symbol}</span><span>${new Date(event.createdAt).toLocaleTimeString()}</span></div>
          <div>${event.message}</div>
        </div>`).join('') : '<div class="empty">Событий пока нет</div>';
    }

    load().catch(error => { byId('status').textContent = error.message; });
    setInterval(() => load().catch(() => {}), 5000);
  </script>
</body>
</html>
""";
}
