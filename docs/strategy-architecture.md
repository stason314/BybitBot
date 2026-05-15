# Strategy Architecture

The bot should separate core execution from trading strategy logic.

Core responsibilities:
- load runtime profiles;
- collect market/account state;
- enforce risk limits and fee-aware profit checks;
- create, cancel, and synchronize orders;
- persist state and expose dashboard data.

Strategy responsibilities:
- analyze the supplied market/profile context;
- return trading intents, not direct Bybit calls;
- avoid owning storage, exchange clients, or notification plumbing.

Current baseline:
- `TradingStrategyType.Grid` is the default strategy;
- `TradingStrategyType.Dca` is supported as a first additional strategy;
- `TradingStrategyType.Combo` runs grid first and enables DCA accumulation below a configured trigger;
- `TradingStrategyType.Btd` buys sharp dips only when market regime is not danger;
- `TradingStrategyType.Signal` trades directly from `SignalAnalyzer` output;
- `TradingStrategyType.Hybrid` runs grid, DCA, BTD, and signal overlay in the same runtime profile;
- `TradingStrategyType.NoTrade` syncs state and fills but creates no new orders;
- `StrategySelectionMode.Manual` is the default runtime mode;
- runtime settings persist strategy mode/type/config JSON so future UI and auto-selection can be added without another schema break.

`Dca` strategy config lives in `StrategyConfigJson`:

```json
{
  "orderSizeUsdt": 25,
  "buyIntervalMinutes": 30,
  "maxActiveBuyOrders": 1,
  "takeProfitPercent": 1,
  "limitOffsetPercent": 0.05,
  "dipPercent": 0,
  "dipLookbackCandles": 30,
  "candleInterval": "1",
  "maxPositionUsdt": 300
}
```

For `Dca`, the existing runtime `Stop Lower` and `Stop Upper` fields act as hard trading boundaries. The bot creates periodic buy limits and places a parent-linked take-profit sell after each buy fill.

`Combo` uses the same JSON fields as `Dca` and adds `dcaBelowPrice`:

```json
{
  "orderSizeUsdt": 20,
  "buyIntervalMinutes": 30,
  "maxActiveBuyOrders": 1,
  "takeProfitPercent": 1,
  "limitOffsetPercent": 0.05,
  "dipPercent": 0.5,
  "dipLookbackCandles": 30,
  "candleInterval": "1",
  "maxPositionUsdt": 500,
  "dcaBelowPrice": 2.08
}
```

If `dcaBelowPrice` is omitted, `Combo` starts DCA when price is at or below `Grid Lower`. Grid behavior remains unchanged inside the configured range.

`Btd` uses the same take-profit and order-size fields as `Dca`, but enters only after a configured drop from recent high:

```json
{
  "orderSizeUsdt": 20,
  "dipPercent": 2,
  "dipLookbackCandles": 30,
  "candleInterval": "1",
  "maxBuys": 3,
  "minMinutesBetweenBuys": 10,
  "takeProfitPercent": 1.2,
  "limitOffsetPercent": 0.2,
  "maxPositionUsdt": 400
}
```

`Btd` skips new entries when `MarketRegimeAnalyzer` returns `Danger`. Runtime `Stop Lower` and `Stop Upper` remain hard entry boundaries.

`Signal` uses the dashboard signal analyzer as an execution strategy:

```json
{
  "orderSizeUsdt": 20,
  "cooldownMinutes": 30,
  "minConfidence": 0.65,
  "maxPositionUsdt": 400,
  "stopLossPercent": 2,
  "takeProfitPercent": 3,
  "limitOffsetPercent": 0,
  "lookbackCandles": 120,
  "candleInterval": "1"
}
```

It opens buy limits on `Buy` signals when confidence is high enough, closes available inventory on `Sell` signals, and creates no new orders on `Hold` or `Avoid`. `Avoid` also cancels pending signal buy entries so stale limits do not open a new position. Stop-loss and take-profit exits are evaluated before ordinary signal entries. Runtime `Stop Lower` and `Stop Upper` remain hard trading boundaries.

`Hybrid` uses the same JSON fields as `Combo`, `Btd`, and `Signal` in one profile. Grid orders are unlabeled grid entries, DCA entries use `dca-entry`, BTD entries use `btd-entry`, and signal orders use `signal-entry`/`signal-exit`.

Signal exits in `Hybrid` are limited to signal-owned inventory, so they do not sell inventory accumulated by grid, DCA, or BTD. DCA and BTD take-profit orders stay parent-linked to their own entry orders. Signal fills do not create grid follow-up orders.

When `Hybrid` needs separate signal limits, `Signal` accepts optional overrides: `signalOrderSizeUsdt`, `signalMaxPositionUsdt`, `signalStopLossPercent`, `signalTakeProfitPercent`, and `signalLimitOffsetPercent`. Without these, the shared fields are used.

`NoTrade` is a safety strategy for auto mode. It keeps market/state synchronization running, including paper/live fill processing for existing orders, but does not create new orders.

Next steps:
1. Move remaining grid order-planning details fully behind strategy implementations.
2. Harden auto recommendation safety checks before enabling timed auto-apply.
3. Add adaptive grid and volume-breakout strategies as separate implementations.

Auto recommendation flow:
- `AutoStrategySelector` analyzes 360 one-minute candles, about 6 hours, plus `MarketRegimeAnalysis`;
- recommended `Stop Lower` and `Stop Upper` are intentionally wider than the active grid range so normal volatility does not pause the bot too often;
- confirmed `Buy`/`Sell` signals can recommend `Signal` in breakout markets, and confirmed `Buy` signals can recommend `Signal` in trend markets;
- dashboard shows the strategy and runtime settings it would choose;
- operator can apply the recommendation manually from the UI;
- timed auto-apply should only be enabled after paper validation and cooldown/safety checks.
