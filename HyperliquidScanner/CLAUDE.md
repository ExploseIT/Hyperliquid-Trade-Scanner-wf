# Hyperliquid Trend Scanner — Project Brief

## What this is

A Windows Forms desktop app (.NET 8, C#) that connects to the Hyperliquid
perpetual futures exchange, scans all listed assets for trend signals, and
displays a live liquidation leaderboard with burst alerts for squeeze / cascade events.

No broker integration, no order placement. Read-only market scanner + live data feed.

---

## Architecture

```
Program.cs
  └─ loads config.json via ConfigLoader
  └─ constructs HyperliquidClient, ScannerService, CoinglassClient (optional)
  └─ launches MainForm

MainForm (Forms/)
  └─ toolbar: timeframe ComboBox, Scan/Stop buttons, filter combo, search box, auto-refresh
  └─ connection indicator label (top-right)
  └─ DataGridView: Asset, Price, RSI(14), EMA cross, MACD, Score, Signal, Vol, Liq 5m, 10-bar, Scanned
  └─ status bar: ProgressBar, status label (doubles as burst alert display), last scan timestamp
  └─ LiquidationPanel (right side, 300px, only when coinglassApiKey is set)

LiquidationPanel (Forms/)
  └─ header: selected asset name + status
  └─ 3 CoinGlass rows: 1h / 4h / 24h long vs short bars + bias label
  └─ signal box: combined trading signal derived from liquidation bias + scan score
  └─ burst alert strip: fires immediately on OKX/Bybit burst detection
  └─ leaderboard header: "OKX+Bybit · N active · Xs ago"
  └─ SqueezeLeaderboard: custom-painted 5-min rolling leaderboard

SqueezeLeaderboard (Forms/)
  └─ custom-painted Control (DoubleBuffered, UserPaint, ResizeRedraw)
  └─ up to 12 rows, each showing: symbol, bar, event count, total USD, alert badge
  └─ colour scheme matches CoinGlass heatmap:
       RED   = short liquidations (shorts squeezed → price rising → consider LONG)
       GREEN = long  liquidations (longs cascading → price falling → consider SHORT)
  └─ SymbolClicked event → loads asset in LiquidationPanel

ScannerService (Services/)
  └─ calls HyperliquidClient.GetAssetsAsync() -> all perp assets (main + HIP-3)
  └─ MaxAssets limit applies only to main HL assets; HIP-3 assets always included
  └─ for each asset: GetCandlesAsync() -> feeds TrendAnalyser
  └─ throttles by config.requestDelayMs
  └─ honours CancellationToken

HyperliquidClient (Services/)
  └─ POST https://api.hyperliquid.xyz/info
  └─ GetAssetsAsync() — fetches main universe + HIP-3 dexes from config.hip3Dexes
       HIP-3 asset names prefixed: "xyz:MU", "xyz:XYZ100" etc.
  └─ FetchUniverseAsync(dex?) — internal; null = main HL, string = HIP-3 dex
  └─ GetCandlesAsync(coin, interval, count) — coin can be "BTC" or "xyz:MU"

TrendAnalyser (Services/)
  └─ EMA crossover (9/21), RSI(14) > 50, MACD bullish
  └─ BullishScore 0-3, IsBullish = score >= bullishThreshold
  └─ Also detects: VolumeSpike, PriceSurge, IsAbsorption, IsDistribution, IsClimax

BinanceLiquidationFeed (Services/)
  └─ MISNAMED — actually uses OKX WebSocket (name kept for API compatibility)
  └─ URL: wss://ws.okx.com:8443/ws/v5/public
  └─ Subscribes: {"op":"subscribe","args":[{"channel":"liquidation-orders","instType":"SWAP"}]}
  └─ Heartbeat: plain "ping" text frame every 25s
  └─ Side: "sell" = long liquidated (SELL), "buy" = short liquidated (BUY)
  └─ MinUsdValue = $1,500 filter
  └─ OkxInstIdToSymbol: "BTC-USDT-SWAP" → "BTCUSDT"

BybitLiquidationFeed (Services/)
  └─ URL: wss://stream.bybit.com/v5/public/linear
  └─ Subscribes: {"op":"subscribe","args":["allLiquidation"]}
  └─ Heartbeat: {"op":"ping"} JSON frame every 20s
  └─ Side: "Sell" = long liquidated (SELL), "Buy" = short liquidated (BUY)
  └─ MinUsdValue = $1,500 filter
  └─ Symbol: "SOLUSDT" → BaseSymbol "SOL" (stripped by BinanceLiquidationEvent.BaseSymbol)

LiquidationAggregator (Services/)
  └─ Receives events from both OKX and Bybit feeds
  └─ HL ASSET FILTER: SetHlAssets() loads live HL asset list at startup;
       symbols not on HL are silently dropped before aggregation
       (kSHIB → stored as 1000SHIB to match exchange symbol format)
  └─ Two signal modes:

     LEADERBOARD (2-second timer):
       5-min rolling window per symbol
       Ranked by total USD
       Squeeze/cascade badges at $25K+ total and 65%+ directional bias
       LeaderboardUpdated event: Action<List<SymbolSummary>, DateTime>

     BURST (immediate, fires on WebSocket ingest thread):
       30-second rolling window
       Fires when ≥3 events for same symbol arrive within 30s
       Requires ≥$5K total and ≥55% directional bias
       NO single-event trigger (removed to eliminate commodity false positives)
       90-second cooldown per symbol
       BurstDetected event: Action<BurstAlert>
       Subscribers must BeginInvoke to touch UI

Models/
  └─ AppConfig.cs          — typed config with validation
  └─ HyperliquidModels.cs  — AssetInfo, CandleData, AssetScanResult, Timeframes
  └─ CoinglassModels.cs    — snapshot + history bar models, bias labels
  └─ BinanceModels.cs      — BinanceLiquidationEvent (used for all exchange events)

Utils/
  └─ ConfigLoader.cs       — loads, validates, auto-creates config.json on first run
```

---

## Live Feed Data Flow

```
OKX WebSocket ──────────────────────┐
                                     ├──► LiquidationAggregator
Bybit WebSocket ────────────────────┘         │
                                              │ HL asset filter (drops non-HL symbols)
                                              │
                              ┌───────────────┴───────────────────┐
                              │                                   │
                    LeaderboardUpdated (2s)              BurstDetected (immediate)
                              │                                   │
                    LiquidationPanel                    LiquidationPanel
                    SqueezeLeaderboard                  burst alert strip
                    MainForm Liq5m column               MainForm status bar
                                                        MainForm grid row flash
                                                        SystemSounds alert
```

---

## Colour Scheme (matches CoinGlass heatmap)

| Colour | Meaning | Trading signal |
|--------|---------|----------------|
| RED    | Short liquidations dominant — shorts being squeezed out | Consider LONG |
| GREEN  | Long liquidations dominant — longs cascading / being stopped out | Consider SHORT |

This is intentionally the OPPOSITE of standard bullish/bearish green/red convention.
It matches what CoinGlass shows on their liquidation heatmap.

---

## HIP-3 Assets (Hyperliquid custom dexes)

HIP-3 perps are deployed by third-party dexes on Hyperliquid's chain.
They appear in the scanner grid as `dex:SYMBOL` (e.g. `xyz:MU`).

- Configured in `config.hip3Dexes` (default: `["xyz"]`)
- Each dex triggers an additional `POST /info` call with `{"type":"meta","dex":"xyz"}`
- Asset names prefixed: `xyz:MU`, `xyz:XYZ100` etc. — used directly as `coin` in candle API
- HIP-3 assets will NOT appear in the liquidation leaderboard (they only trade on HL,
  not on OKX or Bybit, so no liquidation events exist for them)
- `MaxAssets` limit does NOT apply to HIP-3 assets (they're always fully scanned)

---

## HL Asset Filter

On startup, after the Hyperliquid connection test succeeds, `MainForm` calls
`GetAssetsAsync()` and passes the name list to `_aggregator.SetHlAssets()`.

The aggregator builds a `HashSet<string>` mapping:
- Regular HL assets: stored as-is (`BNB`, `ETH`, `BTC`)
- k-prefix assets: converted to exchange format (`kSHIB` → `1000SHIB`)
- HIP-3 assets: skipped (won't appear in OKX/Bybit feeds)

Any liquidation event whose `BaseSymbol` is not in this set is silently dropped.
This eliminates noise from commodity perps (XAU), obscure altcoins (GIGGLE), etc.

If the asset fetch fails, `_hlAssets` stays null and all symbols pass through.

---

## Scanner Grid Columns

| Column   | Content |
|----------|---------|
| Asset    | HL asset name (e.g. `BTC`, `kSHIB`, `xyz:MU`) |
| Price    | Last candle close price |
| RSI(14)  | Current RSI value |
| EMA cross | Y if 9 EMA crossed above 21 EMA |
| MACD     | Y if MACD line above signal line |
| Score    | Bullish score (0/3 to 3/3) |
| Signal   | ✓ Bullish / ✗ Bearish / – |
| Vol      | Volume spike (×N), price surge (%), or special pattern (⚡ Climax, ⟳ Absorb, ⟳ Dist) |
| Liq 5m   | 🔥 $X = short squeeze, 💧 $X = long cascade, $X = neutral, – = no data |
| 10-bar   | Price change over last 10 candles |
| Scanned  | Time of scan |

---

## Burst Alert — what fires

When burst detected:
1. `LiquidationPanel` burst strip shows (red bg for squeeze, green for cascade) for 30s
2. `MainForm` status bar turns red/green with symbol + direction + total + scan confirmation
3. Grid row for matching symbol flashes amber for 4 seconds
4. `SystemSounds.Exclamation` (short squeeze) or `SystemSounds.Hand` (long cascade)
5. If symbol is in last scan results: status bar shows "✓ CONFIRMED by scan RSI X Score Y/3"

---

## Authentication

- **Market data**: fully public, no credentials needed
- **Account data**: requires walletAddress; signing via Nethereum.Signer
- **OKX liquidation feed**: public WebSocket, no API key
- **Bybit liquidation feed**: public WebSocket, no API key
- **CoinGlass**: requires `coinglassApiKey` in config.json (Hobbyist plan or above)

config.json is in .gitignore — MUST NEVER be committed (contains private key).
Recommend using a Hyperliquid API sub-wallet, not the main MetaMask key.

---

## Hyperliquid API

Base URL: `https://api.hyperliquid.xyz`
All requests: `POST /info` with JSON body.

**Main asset list:**
```json
{ "type": "meta" }
```
Response: `[metaObj, assetCtxsArray]` — metaObj has `"universe"` array.
Assets with `openInterest == 0` in assetCtxsArray are filtered out (delisted).

**HIP-3 dex asset list:**
```json
{ "type": "meta", "dex": "xyz" }
```
Same response shape; asset names prefixed with `"xyz:"` by the client.

**Candle snapshot:**
```json
{
  "type": "candleSnapshot",
  "req": { "coin": "BTC", "interval": "1h", "startTime": 1234567890000, "endTime": 1234567890000 }
}
```
Fields `o/c/h/l/v` come back as **strings** — use `ParseDecimal()`.

**Supported intervals:** `15m`, `1h`, `4h`, `1d`, `3d`

---

## CoinGlass API

Base: `https://open-api-v4.coinglass.com`
Auth: `CG-API-KEY` request header

| Endpoint | Used for |
|----------|---------|
| GET `/api/futures/liquidation/coin-list?exchange=Binance` | Snapshot: long/short totals |
| GET `/api/futures/liquidation/aggregated-history` | History bars for sparkline |

Note: `/api/futures/liquidation/order` (REST polling) returns 401 on Hobbyist plan.

---

## Config reference (config.json)

```json
{
  "walletAddress":    "0x...",
  "privateKey":       "",
  "coinglassApiKey":  "",
  "defaultTimeframe": "1h",
  "maxAssets":        200,
  "bullishThreshold": 2,
  "requestDelayMs":   100,
  "hip3Dexes":        ["xyz"]
}
```

- `coinglassApiKey` — leave empty to hide the liquidation panel entirely
- `maxAssets` — applies only to main HL universe; HIP-3 assets always fully scanned
- `hip3Dexes` — list of HIP-3 dex namespaces to include in scans

---

## NuGet packages

| Package                  | Purpose |
|--------------------------|---------|
| Newtonsoft.Json 13.0.3   | JSON serialisation / JToken parsing |
| Skender.Stock.Indicators | EMA, RSI, MACD calculations |
| Nethereum.Signer         | ETH signing for private endpoints |

---

## Network connectivity notes

- **OKX WebSocket**: works on this machine
- **Bybit WebSocket**: works on this machine
- **Binance `!forceOrder@arr` WebSocket**: TCP connects but inbound data frames
  are blocked by ISP/firewall on this machine. Do not attempt again.
- **CoinGlass REST polling**: 401 on Hobbyist plan — would require plan upgrade

---

## LiquidationAggregator thresholds (quick reference)

| Setting | Value | Purpose |
|---------|-------|---------|
| Leaderboard window | 5 min | Rolling window for symbol totals |
| Leaderboard min total | $25K | Minimum to show squeeze/cascade badge |
| Leaderboard alert bias | 65% | One direction must be 65%+ of total |
| Leaderboard min entry | $500 | Minimum to appear on leaderboard at all |
| Burst window | 30s | Window for counting cluster events |
| Burst min events | 3 | Minimum events to trigger burst |
| Burst min total | $5K | Minimum USD in burst window |
| Burst directional bias | 55% | Minimum one-way bias for burst |
| Burst cooldown | 90s | Per-symbol cooldown after firing |
| Leaderboard publish interval | 2s | How often leaderboard is updated |

---

## Key design decisions

- `BinanceLiquidationFeed` is misnamed but kept for API compatibility — it uses OKX internally
- Single-event burst trigger was deliberately removed to prevent commodity false positives (XAU gold perps)
- Burst fires on the WebSocket ingest thread — subscribers must `BeginInvoke` to touch UI
- HL asset filter uses `null` to mean "no filter" — if asset fetch fails on startup, all symbols pass through
- `MaxAssets` deliberately excludes HIP-3 assets so user-configured dexes always fully scan
- Colour scheme is inverted from standard trading convention to match CoinGlass heatmap

---

## File structure

```
HyperliquidScanner/
├── Forms/
│   ├── MainForm.cs              — main window, grid, burst alerts, Liq5m column
│   ├── LiquidationPanel.cs      — right panel: CoinGlass snapshot + leaderboard
│   └── SqueezeLeaderboard.cs    — custom-painted leaderboard control
├── Models/
│   ├── AppConfig.cs             — config model + validation
│   ├── HyperliquidModels.cs     — AssetInfo, CandleData, AssetScanResult, Timeframes
│   ├── CoinglassModels.cs       — liquidation snapshot + history models
│   └── BinanceModels.cs         — BinanceLiquidationEvent (shared across all feeds)
├── Services/
│   ├── HyperliquidClient.cs     — REST API client (public + private endpoints)
│   ├── ScannerService.cs        — orchestrates full market scan
│   ├── TrendAnalyser.cs         — indicator calculations + signal scoring
│   ├── CoinglassClient.cs       — CoinGlass REST API client
│   ├── BinanceLiquidationFeed.cs— OKX WebSocket feed (misnamed, kept for compat)
│   ├── BybitLiquidationFeed.cs  — Bybit V5 linear WebSocket feed
│   └── LiquidationAggregator.cs — aggregates both feeds, leaderboard + burst detection
├── Utils/
│   └── ConfigLoader.cs          — config.json load/validate/auto-create
├── Program.cs
├── config.json                  — NOT in git (contains private key)
└── CLAUDE.md                    — this file
```
