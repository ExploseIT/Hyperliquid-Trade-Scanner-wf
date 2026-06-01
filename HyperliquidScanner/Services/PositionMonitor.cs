using HyperliquidScanner.Models;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Monitors open positions against SL/TP thresholds defined in config.json.
    ///
    /// SL confirmation (two-layer filter to prevent wick-induced false triggers):
    ///   1. PnL% must stay below -slDecimal for N consecutive polls (slConfirmationPolls × 5s)
    ///   2. RSI slope for the symbol must be negative (mini-scan confirms momentum)
    ///
    /// TP: fires immediately when PnL% crosses above +tpDecimal (no confirmation needed).
    ///
    /// HIP-3 positions (e.g. xyz:AMD): monitored and alerted but NOT auto-closed.
    /// </summary>
    public class PositionMonitor
    {
        private readonly HyperliquidClient _client;
        private readonly AppConfig         _config;
        private readonly TrendAnalyser     _analyser = new();

        // Per-symbol state
        private class SymbolState
        {
            public bool    HasBeenAboveSl    { get; set; }
            public bool    HasBeenBelowTp    { get; set; }
            public bool    SlFired           { get; set; }
            public bool    TpFired           { get; set; }
            public decimal InitialPnlPct     { get; set; }
            public int     SlBelowPollCount  { get; set; }  // consecutive polls below SL
        }

        private readonly Dictionary<string, SymbolState>     _states
            = new(StringComparer.OrdinalIgnoreCase);

        // Latest RSI slope per symbol from the mini-scan (negative = falling)
        private readonly Dictionary<string, decimal> _rsiSlopes
            = new(StringComparer.OrdinalIgnoreCase);

        // ── Events ────────────────────────────────────────────────────────────

        public event Action<string, string>? OrderPlaced;
        public event Action<string, string>? OrderFailed;
        public event Action<string, decimal>? SlWarning;

        public PositionMonitor(HyperliquidClient client, AppConfig config)
        {
            _client = client;
            _config = config;
        }

        // ── Main check cycle ──────────────────────────────────────────────────

        public async Task CheckPositionsAsync(
            List<PositionInfo> positions, CancellationToken ct = default)
        {
            if (positions.Count == 0) return;

            // Only proceed if at least one symbol has SL or TP enabled
            var anyEnabled = positions.Any(p =>
            {
                var cfg = _config.GetRiskConfig(p.Symbol);
                return cfg.SlEnabled || cfg.TpEnabled;
            });
            if (!anyEnabled) return;

            // Mini-scan: fetch RSI slope for each open position symbol
            await RefreshRsiSlopesAsync(positions, ct);

            foreach (var pos in positions)
            {
                ct.ThrowIfCancellationRequested();

                var isHip3     = pos.Symbol.Contains(':');
                var riskConfig = _config.GetRiskConfig(pos.Symbol);

                // First observation — initialise state, don't act yet
                if (!_states.TryGetValue(pos.Symbol, out var state))
                {
                    state = new SymbolState
                    {
                        InitialPnlPct  = pos.PnlPercent,
                        HasBeenAboveSl = pos.PnlPercent > -riskConfig.SlDecimal,
                        HasBeenBelowTp = pos.PnlPercent < riskConfig.TpDecimal
                    };
                    _states[pos.Symbol] = state;

                    if (!state.HasBeenAboveSl)
                        System.Diagnostics.Debug.WriteLine(
                            $"[Monitor] {pos.Symbol} already past SL at startup " +
                            $"({pos.PnlPercent:P1}) — skipping auto-SL.");
                    continue;
                }

                // ── SL check ─────────────────────────────────────────────────
                if (riskConfig.SlEnabled && !state.SlFired && state.HasBeenAboveSl)
                {
                    if (pos.PnlPercent <= -riskConfig.SlDecimal)
                    {
                        state.SlBelowPollCount++;

                        var rsiSlope       = _rsiSlopes.GetValueOrDefault(pos.Symbol, 0m);
                        var pollsConfirmed = state.SlBelowPollCount >= riskConfig.SlConfirmationPolls;
                        var rsiConfirmed   = rsiSlope < 0m; // momentum falling

                        System.Diagnostics.Debug.WriteLine(
                            $"[Monitor] {pos.Symbol} SL check: polls={state.SlBelowPollCount}/" +
                            $"{riskConfig.SlConfirmationPolls}  RSI slope={rsiSlope:F2}");

                        if (pollsConfirmed && rsiConfirmed)
                        {
                            state.SlFired = true;
                            var label = $"{pos.Symbol} SL @ -{riskConfig.SlDecimal:P0}  " +
                                        $"ROE {pos.PnlPercent:P1}  RSI slope {rsiSlope:F1}";
                            if (isHip3)
                                OrderFailed?.Invoke(pos.Symbol,
                                    $"⚠ SL triggered for {label} — HIP-3, manual close required");
                            else
                                await TriggerSlAsync(pos, riskConfig, label, ct);
                        }
                    }
                    else
                    {
                        // Recovered above SL — reset consecutive poll counter
                        state.SlBelowPollCount = 0;

                        // SL warning: within 20% of threshold
                        var warningLevel = -riskConfig.SlDecimal * 0.8m;
                        if (pos.PnlPercent <= warningLevel)
                            SlWarning?.Invoke(pos.Symbol, pos.PnlPercent);
                    }
                }

                // ── TP check ─────────────────────────────────────────────────
                if (riskConfig.TpEnabled && !state.TpFired && state.HasBeenBelowTp
                    && pos.PnlPercent >= riskConfig.TpDecimal)
                {
                    state.TpFired = true;
                    var label = $"{pos.Symbol} TP @ {riskConfig.TpDecimal:P0}  " +
                                $"ROE {pos.PnlPercent:P1}";
                    if (isHip3)
                        OrderFailed?.Invoke(pos.Symbol,
                            $"🎯 TP triggered for {label} — HIP-3, manual close required");
                    else
                        await TriggerTpAsync(pos, riskConfig, label, ct);
                }

                // Update state flags
                if (pos.PnlPercent > -riskConfig.SlDecimal) state.HasBeenAboveSl = true;
                if (pos.PnlPercent < riskConfig.TpDecimal)  state.HasBeenBelowTp = true;
            }

            // Remove state for closed positions
            var open = positions.Select(p => p.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _states.Keys.Where(k => !open.Contains(k)).ToList())
                _states.Remove(key);
        }

        /// <summary>
        /// Clears all per-symbol state. Called when config.json is reloaded at runtime
        /// so new thresholds apply cleanly — existing positions re-evaluated from scratch.
        /// </summary>
        public void Reset()
        {
            _states.Clear();
            _rsiSlopes.Clear();
            System.Diagnostics.Debug.WriteLine("[Monitor] State reset — config reloaded.");
        }

        public SymbolRiskStatus GetStatus(string symbol)
        {
            if (!_states.TryGetValue(symbol, out var state)) return SymbolRiskStatus.Normal;
            if (state.SlFired) return SymbolRiskStatus.SlFired;
            if (state.TpFired) return SymbolRiskStatus.TpFired;
            return SymbolRiskStatus.Normal;
        }

        /// <summary>Returns the latest RSI slope for a symbol (for display in positions panel).</summary>
        public decimal GetRsiSlope(string symbol) =>
            _rsiSlopes.GetValueOrDefault(symbol, 0m);

        // ── Mini-scan: fetch RSI slope for open positions ─────────────────────

        private async Task RefreshRsiSlopesAsync(
            List<PositionInfo> positions, CancellationToken ct)
        {
            var timeframe   = _config.DefaultTimeframe;
            var candleCount = Timeframes.CandleCount(timeframe);

            foreach (var pos in positions)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Use base symbol for HIP-3 (strip dex prefix for candle API)
                    var coin = pos.Symbol.Contains(':')
                        ? pos.Symbol.Substring(pos.Symbol.IndexOf(':') + 1)
                        : pos.Symbol;

                    var candles = await _client.GetCandlesAsync(coin, timeframe, candleCount, ct);
                    if (candles.Count < 10) continue;

                    var result = _analyser.Analyse(coin, timeframe, candles);
                    _rsiSlopes[pos.Symbol] = result.RsiSlope;

                    System.Diagnostics.Debug.WriteLine(
                        $"[Monitor] {pos.Symbol} mini-scan: RSI={result.Rsi:F1}  slope={result.RsiSlope:F2}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Monitor] mini-scan failed for {pos.Symbol}: {ex.Message}");
                }
            }
        }

        // ── Order execution ───────────────────────────────────────────────────

        private async Task TriggerSlAsync(
            PositionInfo pos, SymbolRiskConfig cfg, string label, CancellationToken ct)
        {
            var assetInfo = _client.GetAssetIndex(pos.Symbol);
            if (assetInfo == null)
            {
                OrderFailed?.Invoke(pos.Symbol, $"SL: asset index unknown for {pos.Symbol}");
                return;
            }

            var (_, szDec) = assetInfo.Value;
            var isBuy      = !pos.IsLong;

            // Try limit first (0.5% aggressive for quick fill)
            var limitPrice = pos.IsLong
                ? pos.MarkPrice * 0.995m
                : pos.MarkPrice * 1.005m;

            var (ok, msg) = await _client.PlaceLimitCloseAsync(
                pos.Symbol, isBuy, limitPrice, pos.Size, szDec, ct);

            if (ok) { OrderPlaced?.Invoke(pos.Symbol, $"🔴 SL {label} — limit close placed"); return; }

            // Fallback: market (IOC)
            System.Diagnostics.Debug.WriteLine($"[Monitor] SL limit failed ({msg}), trying market");
            (ok, msg) = await _client.PlaceMarketCloseAsync(
                pos.Symbol, isBuy, pos.MarkPrice, pos.Size, szDec, ct);

            if (ok)
                OrderPlaced?.Invoke(pos.Symbol, $"🔴 SL {label} — market close placed");
            else
                OrderFailed?.Invoke(pos.Symbol, $"SL FAILED for {label}: {msg}");
        }

        private async Task TriggerTpAsync(
            PositionInfo pos, SymbolRiskConfig cfg, string label, CancellationToken ct)
        {
            var assetInfo = _client.GetAssetIndex(pos.Symbol);
            if (assetInfo == null)
            {
                OrderFailed?.Invoke(pos.Symbol, $"TP: asset index unknown for {pos.Symbol}");
                return;
            }

            var (_, szDec) = assetInfo.Value;
            var isBuy      = !pos.IsLong;

            var closeSize = Math.Round(pos.Size * cfg.TpSizeDecimal,
                szDec, MidpointRounding.ToZero);
            if (closeSize <= 0)
            {
                OrderFailed?.Invoke(pos.Symbol, $"TP: close size is 0 for {pos.Symbol}");
                return;
            }

            var (ok, msg) = await _client.PlaceLimitCloseAsync(
                pos.Symbol, isBuy, pos.MarkPrice, closeSize, szDec, ct);

            if (ok)
                OrderPlaced?.Invoke(pos.Symbol, $"🟢 TP {label} — closed {cfg.TpSizeDecimal:P0}");
            else
                OrderFailed?.Invoke(pos.Symbol, $"TP FAILED for {label}: {msg}");
        }
    }

    public enum SymbolRiskStatus { Normal, SlFired, TpFired }
}
