# Runtime Settings Presets

Paste one block into **Runtime Settings -> Paste Settings Preset**, press **Fill Runtime Settings**, then **Apply Settings**.

TONUSDT is skipped because it already exists.

Prices were anchored to public Bybit spot tickers checked on 2026-05-15:
SOL 89.41, XRP 1.4408, DOGE 0.11236, LINK 10.125, ADA 0.2613, SUI 1.1186.

## SOLUSDT

trend + btd + grid, medium risk

```text
Symbol: SOLUSDT
Category: spot
Strategy Mode: auto
Strategy Type: hybrid
Strategy Config Json: {"orderSizeUsdt":20,"trendOrderSizeUsdt":20,"signalOrderSizeUsdt":0,"minConfidence":0.95,"dipPercent":2.8,"dipLookbackCandles":30,"candleInterval":"1","maxBuys":3,"minMinutesBetweenBuys":15,"takeProfitPercent":1.4,"limitOffsetPercent":0.15,"maxPositionUsdt":300,"dcaBelowPrice":85.00,"lookbackCandles":120,"breakoutLookbackCandles":60,"minTrendStrengthPercent":0.08,"minVolumeRatio":1.2,"breakoutBufferPercent":0.12,"pullbackExitPercent":0.8,"stopLossPercent":2.2}
Grid Lower: 85.00
Grid Upper: 94.50
Grid Step: 1.20
Order Size USDT: 20
Stop Lower: 81.50
Stop Upper: 99.00
```

## XRPUSDT

grid + breakout, medium risk

```text
Symbol: XRPUSDT
Category: spot
Strategy Mode: auto
Strategy Type: grid
Strategy Config Json: {"orderSizeUsdt":20,"trendOrderSizeUsdt":20,"signalOrderSizeUsdt":0,"minConfidence":0.95,"candleInterval":"1","lookbackCandles":120,"breakoutLookbackCandles":60,"minTrendStrengthPercent":0.08,"minVolumeRatio":1.35,"breakoutBufferPercent":0.15,"pullbackExitPercent":0.8,"stopLossPercent":2.3,"takeProfitPercent":3.2,"limitOffsetPercent":0.05}
Grid Lower: 1.37
Grid Upper: 1.53
Grid Step: 0.02
Order Size USDT: 20
Stop Lower: 1.33
Stop Upper: 1.60
```

## DOGEUSDT

grid + breakout, high risk

```text
Symbol: DOGEUSDT
Category: spot
Strategy Mode: auto
Strategy Type: grid
Strategy Config Json: {"orderSizeUsdt":15,"trendOrderSizeUsdt":15,"signalOrderSizeUsdt":0,"minConfidence":0.95,"candleInterval":"1","lookbackCandles":120,"breakoutLookbackCandles":60,"minTrendStrengthPercent":0.09,"minVolumeRatio":1.5,"breakoutBufferPercent":0.18,"pullbackExitPercent":1.0,"stopLossPercent":2.8,"takeProfitPercent":4.0,"limitOffsetPercent":0.05}
Grid Lower: 0.1060
Grid Upper: 0.1200
Grid Step: 0.0015
Order Size USDT: 15
Stop Lower: 0.1020
Stop Upper: 0.1260
```

## LINKUSDT

trend + btd, medium risk

```text
Symbol: LINKUSDT
Category: spot
Strategy Mode: auto
Strategy Type: hybrid
Strategy Config Json: {"orderSizeUsdt":20,"trendOrderSizeUsdt":20,"signalOrderSizeUsdt":0,"minConfidence":0.95,"dipPercent":2.6,"dipLookbackCandles":30,"candleInterval":"1","maxBuys":2,"minMinutesBetweenBuys":20,"takeProfitPercent":1.5,"limitOffsetPercent":0.15,"maxPositionUsdt":250,"dcaBelowPrice":9.65,"lookbackCandles":120,"breakoutLookbackCandles":60,"minTrendStrengthPercent":0.08,"minVolumeRatio":1.2,"breakoutBufferPercent":0.12,"pullbackExitPercent":0.8,"stopLossPercent":2.2}
Grid Lower: 9.65
Grid Upper: 10.80
Grid Step: 0.15
Order Size USDT: 20
Stop Lower: 9.25
Stop Upper: 11.35
```

## ADAUSDT

grid + btd, medium risk

```text
Symbol: ADAUSDT
Category: spot
Strategy Mode: auto
Strategy Type: combo
Strategy Config Json: {"orderSizeUsdt":15,"buyIntervalMinutes":30,"maxActiveBuyOrders":2,"takeProfitPercent":1.2,"limitOffsetPercent":0.1,"dipPercent":2.5,"dipLookbackCandles":30,"candleInterval":"1","maxPositionUsdt":220,"dcaBelowPrice":0.252}
Grid Lower: 0.2470
Grid Upper: 0.2780
Grid Step: 0.0040
Order Size USDT: 15
Stop Lower: 0.2380
Stop Upper: 0.2900
```

## SUIUSDT

incomplete source row; assumed auto hybrid with high-volatility guard

```text
Symbol: SUIUSDT
Category: spot
Strategy Mode: auto
Strategy Type: hybrid
Strategy Config Json: {"orderSizeUsdt":15,"trendOrderSizeUsdt":15,"signalOrderSizeUsdt":0,"minConfidence":0.95,"dipPercent":3.2,"dipLookbackCandles":30,"candleInterval":"1","maxBuys":2,"minMinutesBetweenBuys":25,"takeProfitPercent":1.8,"limitOffsetPercent":0.2,"maxPositionUsdt":220,"dcaBelowPrice":1.05,"lookbackCandles":120,"breakoutLookbackCandles":60,"minTrendStrengthPercent":0.1,"minVolumeRatio":1.5,"breakoutBufferPercent":0.2,"pullbackExitPercent":1.1,"stopLossPercent":3.0}
Grid Lower: 1.05
Grid Upper: 1.20
Grid Step: 0.015
Order Size USDT: 15
Stop Lower: 1.00
Stop Upper: 1.28
```
