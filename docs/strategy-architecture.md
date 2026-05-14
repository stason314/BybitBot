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
- `TradingStrategyType.Grid` is the only supported strategy;
- `StrategySelectionMode.Manual` is the default runtime mode;
- runtime settings already persist strategy mode/type so future UI and auto-selection can be added without another schema break.

Next steps:
1. Move grid order-planning decisions behind a strategy decision interface.
2. Add a market regime analyzer that produces range/trend/breakout/no-trade signals.
3. Add dashboard controls for manual strategy selection.
4. Add auto selector in paper mode first, initially switching only between `Grid` and `NoTrade`.
5. Add adaptive grid and volume-breakout strategies as separate implementations.
