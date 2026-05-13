# Bybit Grid Bot

Production-ready MVP grid bot for Bybit V5 on .NET 8 with safe default `paper` mode.

## Features

- `paper`, `testnet`, `mainnet` modes
- spot grid strategy for `BILLUSDT`
- SQLite state storage
- Serilog logs to console and file
- Telegram notifications
- Docker and Docker Compose deploy flow
- Bybit V5 REST client with HMAC signing and retry

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

## Local Run

1. Use the tracked `.env` as the default paper config, or copy `.env.example` if you want to rebuild it manually.
2. Restore packages:

```bash
dotnet restore
```

3. Build:

```bash
dotnet build
```

4. Run:

```bash
dotnet run --project src/BybitGridBot.App
```

## Docker

1. The repository already contains a safe default `.env` for `paper` mode. Keep API keys empty in git.
2. Validate config:

```bash
docker compose config
```

3. Build and start:

```bash
docker compose up -d --build
```

4. Read logs:

```bash
docker logs -f bybit-bot
```

## Deploy On Server

```bash
cd /opt/bybit-bot
git pull --ff-only
docker compose up -d --build
docker logs -f bybit-bot
```

## Update

```bash
git pull --ff-only
docker compose up -d --build
```

## Stop

```bash
docker compose down
```

## Tests

```bash
dotnet test
```

## Required `.env` Values

- `TRADING_MODE`: keep `paper` by default. Use `testnet` for real testnet orders. Use `mainnet` only intentionally.
- `BYBIT_API_KEY`, `BYBIT_API_SECRET`: required for `testnet` and `mainnet`.
- `BYBIT_MARKET_DATA_BASE_URL`: public market data endpoint. Default is mainnet `https://api.bybit.com`, which keeps `paper` mode tied to real prices.
- `BYBIT_TRADING_BASE_URL`: private trading endpoint. Default testnet value is `https://api-testnet.bybit.com`.
- `SYMBOL`, `CATEGORY`: default `BILLUSDT` / `spot`.
- `GRID_LOWER_PRICE`, `GRID_UPPER_PRICE`, `GRID_STEP`, `ORDER_SIZE_USDT`: core grid setup.
- `STOP_LOWER_PRICE`, `STOP_UPPER_PRICE`: hard stop zone.
- `MAX_DAILY_LOSS_USDT`, `MAX_OPEN_ORDERS`, `MAX_POSITION_USDT`, `MIN_ORDER_SIZE_USDT`: risk limits.
- `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID`: only when `TELEGRAM_ENABLED=true`.
- `SQLITE_PATH`: default container path is `/app/data/bybit-grid-bot.db`.

## Notes

- The bot does not expose incoming ports.
- `.env` is tracked in git only with safe defaults and blank secrets. Do not commit real API keys or Telegram secrets.
- `paper` mode uses live Bybit prices but does not send real orders.
- By default `paper` mode reads public market data from mainnet and keeps private trading disabled.
- `testnet` mode uses the trading endpoint for both market data and order placement unless you override `BYBIT_MARKET_DATA_BASE_URL`.
- The default `BILLUSDT` paper grid is intentionally conservative: `0.10-0.15` with stop bounds `0.09/0.16`. Recalibrate before live use.
- Existing live orders are synchronized from Bybit on startup to avoid duplicates by `orderLinkId`.
- For spot grids, initial sell levels require base asset inventory. In `paper` mode the bot can bootstrap inventory automatically on first initialization if needed.

## Warning

Use `paper` or `testnet` by default. Enable `mainnet` only consciously and only with properly scoped API keys. Withdraw permissions are not required.
