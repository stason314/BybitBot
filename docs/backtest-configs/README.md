# Futures Backtest JSON Presets

Paste one of these files into the Futures Backtest page JSON box and click `Apply JSON`.

- `turtle-short-s2-crash-main.json`: main Turtle Short S2 crash-regime test, NY hours `02,03,05,14`.
- `turtle-short-s2-crash-breadth.json`: same backtest request as main crash test; before running it, enable the breadth env values below.
- `turtle-long-s2-control.json`: control Turtle Long S2 test, NY hours `00,05`.

Crash-gate settings are runtime env settings, not `FuturesBacktestRequest` fields. Set these before running the short crash presets:

```env
LIVE_ELIGIBLE_DIRECTIONS=Short
LIVE_ELIGIBLE_TURTLE_SYSTEMS=S2
TURTLE_CRASH_SHORT_GATE_ENABLED=true
TURTLE_CRASH_SHORT_ALLOWED_SYSTEMS=S2
TURTLE_CRASH_SHORT_MIN_OOS_TRADES=20
TURTLE_CRASH_SHORT_REQUIRE_OOS_WITHOUT_TOP2_POSITIVE=true
TURTLE_CRASH_SHORT_MAX_OOS_DRAWDOWN_PERCENT=20
```

For the breadth variant, also set:

```env
TURTLE_CRASH_SHORT_BREADTH_TOP_SYMBOLS=30
TURTLE_CRASH_SHORT_BREADTH_MIN_BELOW_PERCENT=60
TURTLE_CRASH_SHORT_BREADTH_MA_PERIOD=55
```

For the long control preset, disable the crash gate:

```env
LIVE_ELIGIBLE_DIRECTIONS=Long
LIVE_ELIGIBLE_TURTLE_SYSTEMS=S2
TURTLE_CRASH_SHORT_GATE_ENABLED=false
```
