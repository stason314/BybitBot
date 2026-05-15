# Bybit Trading Bot

.NET 8 Bybit bot with safe default `paper` mode, SQLite state, Telegram notifications, Docker deployment, and strategy routing.

## Architecture

```text
MarketDataService
  -> SignalEngine
  -> PriceActionPhaseDetector
  -> BigRedCandleGuard
  -> MarketRegimeDetector
  -> StrategyRouter
  -> ExpectedProfitFilter
  -> CapitalAllocator
  -> RiskManager
  -> ExecutionEngine
  -> Bybit / Paper broker
```

The existing worker still owns execution and Bybit synchronization. The new architecture is added as testable routing, risk, signal, allocation, and backtest components so existing `paper`, `testnet`, and `mainnet` flows are not rewritten.

## Bot Types

- `dca`: runs only DCA.
- `btd`: runs only Buy The Dip.
- `combo`: preset mode for Grid/DCA/BTD-style overlays; conflicting orders must be resolved before execution.
- `signal`: diagnostics/signal mode. It does not trade unless `SIGNAL_TRADING_ENABLED=true`.
- `hybrid`: manual multi-strategy preset/router mode.
- `auto`: auto-selects a strategy from market regime and strategy scores.
- `grid`: range-bound grid strategy.
- `breakout`: confirmed breakout strategy.
- `trend`: trend-following strategy.
- `pause`: no new trades.

`signal` is an engine, not a standalone trading strategy by default. `hybrid` uses manual strategy intent/weights, while `auto` uses price-action phase detection, market regime, and score thresholds. Auto-select is a risk-adjusted selector; it does not guarantee profit.

## How Auto/Hybrid Mode Works

The bot is not supposed to trade every loop. It first classifies the market phase, then picks the strategy. If the market is dangerous, `Pause` is a valid protective state.

- `Uptrend` -> `TrendFollowing`
- `PullbackInUptrend` -> `BTD`
- `RangeBound` -> `Grid`
- `BreakoutUp` -> `Breakout`
- `BreakoutDown`, `Dump`, `HighVolatility`, `Unknown` -> `Pause`

`BigRedCandleGuard` is the fast protection path. If a 15m/30m-style candle or recent candle sequence drops beyond the configured thresholds, new buy intents are blocked and grid buys can be cancelled. Dump can switch to `Pause` without waiting for the normal strategy cooldown.

`ExpectedProfitFilter` blocks trades whose expected move is too small after round-trip fees, slippage, and the configured minimum profit buffer. This is important for tight grids where fee drag can turn frequent trades into negative net PnL.

SQLite initializes tables for:

- `market_phases`
- `strategy_switches`
- `no_trade_reasons`
- `strategy_performance`
- `strategy_daily_performance`

## Safe Defaults

- `TRADING_MODE=paper`
- `BOT_TYPE=auto`
- API keys are blank in `.env.example`
- `mainnet` requires explicit `TRADING_MODE=mainnet` and API keys
- Docker Compose does not publish inbound ports
- `.env` should stay untracked and must not contain committed secrets

## Example: TON Grid

```env
BOT_TYPE=grid
TRADING_MODE=paper
SYMBOL=TONUSDT
CATEGORY=spot
GRID_LOWER_PRICE=2.30
GRID_UPPER_PRICE=2.60
GRID_STEP=0.05
ORDER_SIZE_USDT=20
STOP_LOWER_PRICE=2.25
STOP_UPPER_PRICE=2.65
```

## Example: Auto Mode

```env
BOT_TYPE=auto
TRADING_MODE=paper
SYMBOL=TONUSDT
CATEGORY=spot
SPOT_ONLY=true
STRATEGY_MIN_SCORE=65
STRATEGY_MIN_CONFIDENCE=0.6
STRATEGY_SWITCH_COOLDOWN_MINUTES=30
ALLOW_HIGH_VOLATILITY_TRADING=false
PHASE_CONFIRMATION_CANDLES=2
MIN_PHASE_CONFIDENCE=0.6
MIN_STRATEGY_SCORE=65
BIG_RED_CANDLE_PERCENT=4.0
DUMP_MOVE_PERCENT=6.0
CANCEL_GRID_BUYS_ON_DUMP=true
MIN_EXPECTED_PROFIT_PERCENT=0.7
```

## Why Bot Did Not Trade

Look for these log fields/reasons:

- `MarketPhase`: current price-action phase.
- `SelectedStrategy`: strategy chosen by router.
- `StrategyScore`: score used for routing.
- `NoTradeReason`: e.g. `DumpDetected`, `HighVolatility`, `ExpectedProfitTooLow`, `BtcRiskOff`, `PriceOutsideRange`.
- `RiskDecision`: whether risk manager rejected the trade.
- `ExpectedProfit`: expected move versus fee/slippage threshold.

## Local Commands

```bash
dotnet test
docker compose config
docker compose up -d --build
docker logs -f bybit-bot
```

Run without Docker:

```bash
dotnet restore
dotnet build
dotnet run --project src/BybitGridBot.App
```

## Deploy

```bash
cd /opt/bybit-bot/BybitBot
git pull --ff-only
docker compose up -d --build
docker logs -f bybit-bot
```

## Project Structure

```text
/src
  /BybitGridBot.App
  /BybitGridBot.Bybit
  /BybitGridBot.Domain
  /BybitGridBot.Notifications
  /BybitGridBot.Risk
  /BybitGridBot.Storage
  /BybitGridBot.Strategy
/tests
  /BybitGridBot.Tests
```

## Before Mainnet

- Run `dotnet test`.
- Run in `paper` first and confirm logs show `MarketRegime`, strategy scores/selection, capital allocation, and risk decisions.
- Run `testnet` with scoped API keys.
- Verify `MAX_DAILY_LOSS_USDT`, `MAX_POSITION_USDT`, `MAX_TOTAL_EXPOSURE_PERCENT`, and `MIN_USDT_RESERVE_PERCENT`.
- Confirm `SPOT_ONLY=true` unless futures support has been intentionally implemented and tested.
- Confirm Telegram notifications and SQLite volume paths.
- Never use API keys with withdrawal permissions.
