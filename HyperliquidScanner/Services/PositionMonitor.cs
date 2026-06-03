using HyperliquidScanner.Models;
using Serilog;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Monitors open positions against SL/TP thresholds defined in config.json.
    /// All thresholds are in USD (unrealised PnL).
    ///
    /// SL confirmation (two-layer filter to prevent wick-induced false triggers):
    ///   1. UnrealisedPnl must stay below -slUsd for N consecutive polls (slConfirmationPolls × 5s)
    ///   2. RSI slope for the symbol must be negative (mini-scan confirms momentum)
    ///
    /// TP: fires immediately when UnrealisedPnl >= +tpUsd.
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

            // Trailing stop state (USD)
            public decimal HighWaterMarkUsd  { get; set; } = decimal.MinValue;
            public bool    TrailingActive    { get; set; }
            public bool    TrailingFired     { get; set; }

            // DCA detection — reset state when position size or entry price changes
            public decimal LastKnownSize     { get; set; }
            public decimal LastKnownEntry    { get; set; }
        }

        private readonly Dictionary<string, SymbolState>     _states
            = new(StringComparer.OrdinalIgnoreCase);

        // After first position check completes, new positions are session-opened
        // and should have SL active immediately regardless of current PnL
        private bool _startupComplete = false;

        // Latest RSI slope per symbol from the mini-scan (negative = falling)
        private readonly Dictionary<string, decimal> _rsiSlopes
            = new(StringComparer.OrdinalIgnoreCase);

        // ── Events ────────────────────────────────────────────────────────────

        public event Action<string, string>? OrderPlaced;
        public event Action<string, string>? OrderFailed;
        public event Action<string, decimal>? SlWarning;
        public event Action<string>?          SlFiredAlert; // fires when SL triggers — for sound alert

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
                    // Startup positions: respect current PnL (don't fire if already past SL)
                    // Session-opened positions: always activate SL immediately
                    bool hasBeenAboveSl = _startupComplete
                        ? true                                        // new position during session → SL always active
                        : pos.UnrealisedPnl > -riskConfig.SlUsd;     // startup position → only if above threshold

                    state = new SymbolState
                    {
                        InitialPnlPct  = pos.PnlPercent,
                        HasBeenAboveSl = hasBeenAboveSl,
                        HasBeenBelowTp = pos.UnrealisedPnl < riskConfig.TpUsd,
                        LastKnownSize  = pos.Size,
                        LastKnownEntry = pos.EntryPrice
                    };
                    _states[pos.Symbol] = state;
                    Log.Information(
                        "Monitor watching {Symbol}: SL={SlEnabled} -${SlUsd} TP={TpEnabled} +${TpUsd} " +
                        "Trail={TrailEnabled}  PnL=${Pnl:F2}  AboveSL={AboveSl}",
                        pos.Symbol, riskConfig.SlEnabled, riskConfig.SlUsd,
                        riskConfig.TpEnabled, riskConfig.TpUsd,
                        riskConfig.TrailingEnabled, pos.UnrealisedPnl, state.HasBeenAboveSl);

                    if (!state.HasBeenAboveSl)
                        System.Diagnostics.Debug.WriteLine(
                            $"[Monitor] {pos.Symbol} already past SL at startup " +
                            $"(PnL ${pos.UnrealisedPnl:F2}, SL -${riskConfig.SlUsd}) — skipping auto-SL.");
                    continue;
                }

                // DCA detection — size increased or entry price changed → reset state
                // so thresholds recalculate against the new combined position
                bool isDca = pos.Size > state.LastKnownSize * 1.005m   // size grew by >0.5%
                          || Math.Abs(pos.EntryPrice - state.LastKnownEntry) > pos.EntryPrice * 0.0005m; // entry moved

                if (isDca)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Monitor] {pos.Symbol} DCA detected " +
                        $"(size {state.LastKnownSize}→{pos.Size}, " +
                        $"entry {state.LastKnownEntry}→{pos.EntryPrice}) — resetting state.");

                    state.SlFired          = false;
                    state.TpFired          = false;
                    state.SlBelowPollCount = 0;
                    state.HasBeenAboveSl   = pos.UnrealisedPnl > -riskConfig.SlUsd;
                    state.HasBeenBelowTp   = pos.UnrealisedPnl < riskConfig.TpUsd;
                    state.TrailingActive   = false;
                    state.TrailingFired    = false;
                    state.HighWaterMarkUsd = decimal.MinValue;
                    state.InitialPnlPct    = pos.PnlPercent;
                    state.LastKnownSize    = pos.Size;
                    state.LastKnownEntry   = pos.EntryPrice;
                    // Don't skip — process this observation with fresh state
                }
                else
                {
                    // Update for next cycle
                    state.LastKnownSize  = pos.Size;
                    state.LastKnownEntry = pos.EntryPrice;
                }

                // ── SL check — pure threshold crossing ───────────────────────
                // Fires immediately when UnrealisedPnl crosses below -slUsd.
                // No RSI or poll confirmation — clean and fast.
                // HasBeenAboveSl ensures we only fire on a fresh crossing,
                // not on positions already past the threshold at startup.
                if (riskConfig.SlEnabled && !state.SlFired && state.HasBeenAboveSl)
                {
                    if (pos.UnrealisedPnl <= -riskConfig.SlUsd)
                    {
                        state.SlFired = true;
                        var label = $"{pos.Symbol} SL @ -${riskConfig.SlUsd:F2}  " +
                                    $"PnL ${pos.UnrealisedPnl:F2}";
                        Log.Warning("PositionMonitor SL firing: {Label}", label);
                        SlFiredAlert?.Invoke(pos.Symbol);
                        if (isHip3)
                            OrderFailed?.Invoke(pos.Symbol,
                                $"⚠ SL triggered for {label} — HIP-3, manual close required");
                        else
                            await TriggerSlAsync(pos, riskConfig, label, ct);
                    }
                    else
                    {
                        // SL warning: within 20% of threshold
                        var warningLevel = -riskConfig.SlUsd * 0.8m;
                        if (pos.UnrealisedPnl <= warningLevel)
                            SlWarning?.Invoke(pos.Symbol, pos.UnrealisedPnl);
                    }
                }

                // ── TP check ─────────────────────────────────────────────────
                if (riskConfig.TpEnabled && !state.TpFired
                    && pos.UnrealisedPnl >= riskConfig.TpUsd)
                {
                    state.TpFired = true;
                    var label = $"{pos.Symbol} TP @ +${riskConfig.TpUsd:F2}  " +
                                $"PnL ${pos.UnrealisedPnl:F2}";
                    Log.Information("PositionMonitor TP firing: {Label}", label);
                    if (isHip3)
                        OrderFailed?.Invoke(pos.Symbol,
                            $"🎯 TP triggered for {label} — HIP-3, manual close required");
                    else
                        await TriggerTpAsync(pos, riskConfig, label, ct);
                }

                // ── Trailing stop check ──────────────────────────────────────
                if (riskConfig.TrailingEnabled && !state.TrailingFired)
                {
                    // Update high-water mark (USD)
                    if (pos.UnrealisedPnl > state.HighWaterMarkUsd)
                        state.HighWaterMarkUsd = pos.UnrealisedPnl;

                    // Activate once min profit level reached
                    if (!state.TrailingActive
                        && pos.UnrealisedPnl >= riskConfig.TrailingMinProfitUsd)
                    {
                        state.TrailingActive = true;
                        System.Diagnostics.Debug.WriteLine(
                            $"[Monitor] {pos.Symbol} trailing ACTIVATED at ${pos.UnrealisedPnl:F2}");
                    }

                    // Fire if retrace from peak exceeds threshold
                    if (state.TrailingActive && state.HighWaterMarkUsd > decimal.MinValue)
                    {
                        var retraceFromPeak = state.HighWaterMarkUsd - pos.UnrealisedPnl;
                        if (retraceFromPeak >= riskConfig.TrailingRetraceUsd)
                        {
                            state.TrailingFired = true;
                            var label = $"{pos.Symbol} trailing stop  " +
                                        $"peak ${state.HighWaterMarkUsd:F2}  " +
                                        $"now ${pos.UnrealisedPnl:F2}  " +
                                        $"retrace ${retraceFromPeak:F2}";
                            Log.Information("PositionMonitor trailing firing: {Label}", label);
                            if (isHip3)
                                OrderFailed?.Invoke(pos.Symbol,
                                    $"🔒 Trailing triggered for {label} — HIP-3, manual close required");
                            else
                                await TriggerTrailingAsync(pos, riskConfig, label, ct);
                        }
                    }
                }

                // Update state flags
                if (pos.UnrealisedPnl > -riskConfig.SlUsd) state.HasBeenAboveSl = true;
                if (pos.UnrealisedPnl < riskConfig.TpUsd)  state.HasBeenBelowTp = true;
            }

            // Remove state for closed positions
            var open = positions.Select(p => p.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _states.Keys.Where(k => !open.Contains(k)).ToList())
                _states.Remove(key);

            // After first full cycle, any new positions are session-opened
            _startupComplete = true;
        }

        /// <summary>
        /// Resets SL-related state when config.json is reloaded so new thresholds apply.
        /// TpFired and TrailingFired are intentionally preserved — a TP/trail that already
        /// fired should not re-fire just because the config was saved again.
        /// </summary>
        public void Reset()
        {
            foreach (var state in _states.Values)
            {
                // SL state resets — new threshold should be re-evaluated fresh
                state.HasBeenAboveSl   = false; // re-evaluated on next poll
                state.SlFired          = false;
                state.SlBelowPollCount = 0;

                // TpFired and TrailingFired are NOT reset — prevents re-firing on reload
                // DCA detection values kept so position changes are still tracked correctly
            }
            _rsiSlopes.Clear();
            System.Diagnostics.Debug.WriteLine("[Monitor] SL state reset — config reloaded. TP/Trail state preserved.");
        }

        public SymbolRiskStatus GetStatus(string symbol)
        {
            if (!_states.TryGetValue(symbol, out var state)) return SymbolRiskStatus.Normal;
            if (state.SlFired)      return SymbolRiskStatus.SlFired;
            if (state.TpFired)      return SymbolRiskStatus.TpFired;
            if (state.TrailingFired) return SymbolRiskStatus.TrailingFired;
            return SymbolRiskStatus.Normal;
        }

        /// <summary>Returns the latest RSI slope for a symbol (for display in positions panel).</summary>
        public decimal GetRsiSlope(string symbol) =>
            _rsiSlopes.GetValueOrDefault(symbol, 0m);

        /// <summary>
        /// Returns trailing stop display info: (highWaterMark, isActive, isFired).
        /// Returns null if trailing is not enabled for this symbol.
        /// </summary>
        public (decimal hwmUsd, bool active, bool fired)? GetTrailingInfo(string symbol)
        {
            if (!_states.TryGetValue(symbol, out var state)) return null;
            var cfg = _config.GetRiskConfig(symbol);
            if (!cfg.TrailingEnabled) return null;
            return (state.HighWaterMarkUsd, state.TrailingActive, state.TrailingFired);
        }

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

            if (ok)
            {
                Log.Information("SL limit placed: {Label}", label);
                OrderPlaced?.Invoke(pos.Symbol, $"🔴 SL {label} — limit close placed");
                return;
            }

            // Fallback: market (IOC)
            Log.Warning("SL limit failed ({Msg}), trying market for {Symbol}", msg, pos.Symbol);
            (ok, msg) = await _client.PlaceMarketCloseAsync(
                pos.Symbol, isBuy, pos.MarkPrice, pos.Size, szDec, ct);

            if (ok)
            {
                Log.Information("SL market placed: {Label}", label);
                OrderPlaced?.Invoke(pos.Symbol, $"🔴 SL {label} — market close placed");
            }
            else
            {
                Log.Error("SL FAILED for {Label}: {Msg}", label, msg);
                OrderFailed?.Invoke(pos.Symbol, $"SL FAILED for {label}: {msg}");
            }
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
            {
                Log.Information("TP placed: {Label}", label);
                OrderPlaced?.Invoke(pos.Symbol, $"🟢 TP {label} — closed {cfg.TpSizeDecimal:P0}");
            }
            else
            {
                Log.Error("TP FAILED for {Label}: {Msg}", label, msg);
                OrderFailed?.Invoke(pos.Symbol, $"TP FAILED for {label}: {msg}");
            }
        }

        private async Task TriggerTrailingAsync(
            PositionInfo pos, SymbolRiskConfig cfg, string label, CancellationToken ct)
        {
            var assetInfo = _client.GetAssetIndex(pos.Symbol);
            if (assetInfo == null)
            {
                OrderFailed?.Invoke(pos.Symbol, $"Trailing: asset index unknown for {pos.Symbol}");
                return;
            }

            var (_, szDec) = assetInfo.Value;
            var isBuy      = !pos.IsLong;

            var closeSize = Math.Round(pos.Size * cfg.TrailingSizeDecimal,
                szDec, MidpointRounding.ToZero);
            if (closeSize <= 0)
            {
                OrderFailed?.Invoke(pos.Symbol, $"Trailing: close size is 0 for {pos.Symbol}");
                return;
            }

            // Use IOC at aggressive price for trailing — speed matters here
            var (ok, msg) = await _client.PlaceMarketCloseAsync(
                pos.Symbol, isBuy, pos.MarkPrice, closeSize, szDec, ct);

            if (ok)
            {
                Log.Information("Trailing placed: {Label}", label);
                OrderPlaced?.Invoke(pos.Symbol, $"🔒 Trailing {label} — closed {cfg.TrailingSizeDecimal:P0}");
            }
            else
            {
                Log.Error("Trailing FAILED for {Label}: {Msg}", label, msg);
                OrderFailed?.Invoke(pos.Symbol, $"Trailing FAILED for {label}: {msg}");
            }
        }
    }

    public enum SymbolRiskStatus { Normal, SlFired, TpFired, TrailingFired }
}
