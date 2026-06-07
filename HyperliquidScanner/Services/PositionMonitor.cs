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

            // App-side trailing stop state (USD)
            public decimal HighWaterMarkUsd  { get; set; } = decimal.MinValue;
            public bool    TrailingActive    { get; set; }
            public bool    TrailingFired     { get; set; }

            // Exchange-native ratcheting trailing stop state
            public long    ExchangeSlOrderId { get; set; }          // OID on exchange (0 = none placed)
            public decimal ExchangeSlPrice   { get; set; }          // current SL price on exchange
            public decimal LastRatchetPnl    { get; set; } = decimal.MinValue; // PnL when SL last moved

            // DCA / new-position detection
            public decimal LastKnownSize     { get; set; }
            public decimal LastKnownEntry    { get; set; }
            public bool    LastKnownIsLong   { get; set; }  // side flip → always a new position

            // Order retry limiting
            public int SlRetryCount       { get; set; }
            public int TpRetryCount       { get; set; }
            public int TrailRetryCount    { get; set; }
            public const int MaxRetries   = 3;

            // Concurrency guard — prevents multiple monitor cycles from placing orders simultaneously
            public bool OrderInFlight     { get; set; }
        }

        private readonly Dictionary<string, SymbolState>     _states
            = new(StringComparer.OrdinalIgnoreCase);

        // Symbols present in the previous poll cycle — used to detect close+reopen
        // across cycles (symbol absent for ≥1 cycle then reappears → force fresh state)
        private HashSet<string> _prevCycleSymbols
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

        // Optional per-account symbolInfo override (takes priority over global config)
        private readonly List<SymbolRiskConfig>? _symbolInfoOverride;

        public PositionMonitor(HyperliquidClient client, AppConfig config,
                               List<SymbolRiskConfig>? symbolInfoOverride = null)
        {
            _client              = client;
            _config              = config;
            _symbolInfoOverride  = symbolInfoOverride;
        }

        /// <summary>
        /// Returns risk config for a symbol — checks this monitor's symbolInfo override first,
        /// then falls back to global config.
        /// </summary>
        private SymbolRiskConfig GetRiskConfig(string symbol)
        {
            if (_symbolInfoOverride != null && _symbolInfoOverride.Count > 0)
            {
                var exact = _symbolInfoOverride.FirstOrDefault(
                    s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
                var def = _symbolInfoOverride.FirstOrDefault(
                    s => s.Symbol.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase));
                if (def != null) return def;
            }
            return GetRiskConfig(symbol);
        }

        // ── Main check cycle ──────────────────────────────────────────────────

        public async Task CheckPositionsAsync(
            List<PositionInfo> positions, CancellationToken ct = default)
        {
            if (positions.Count == 0) return;

            // Only proceed if at least one symbol has SL or TP enabled
            var anyEnabled = positions.Any(p =>
            {
                var cfg = GetRiskConfig(p.Symbol);
                return cfg.SlEnabled || cfg.TpEnabled;
            });
            if (!anyEnabled) return;

            // Mini-scan: fetch RSI slope for each open position symbol
            await RefreshRsiSlopesAsync(positions, ct);

            foreach (var pos in positions)
            {
                ct.ThrowIfCancellationRequested();

                var isHip3     = pos.Symbol.Contains(':');
                var riskConfig = GetRiskConfig(pos.Symbol);

                // Force fresh state if the symbol was absent for at least one poll cycle
                // (position closed and reopened between cycles — catches manual close+reopen)
                if (_states.ContainsKey(pos.Symbol) && !_prevCycleSymbols.Contains(pos.Symbol))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Monitor] {pos.Symbol} absent last cycle then reappeared — resetting state (new position).");
                    _states.Remove(pos.Symbol);
                }

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
                        InitialPnlPct    = pos.PnlPercent,
                        HasBeenAboveSl   = hasBeenAboveSl,
                        HasBeenBelowTp   = pos.UnrealisedPnl < riskConfig.TpUsd,
                        LastKnownSize    = pos.Size,
                        LastKnownEntry   = pos.EntryPrice,
                        LastKnownIsLong  = pos.IsLong
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

                // New position detection — side flip always means a new position
                bool isSideFlip = pos.IsLong != state.LastKnownIsLong;
                if (isSideFlip)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Monitor] {pos.Symbol} side flipped ({(state.LastKnownIsLong ? "Long" : "Short")}→{(pos.IsLong ? "Long" : "Short")}) — resetting state (new position).");
                    _states.Remove(pos.Symbol);
                    continue; // fresh state created on next cycle
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
                    state.TrailingActive    = false;
                    state.TrailingFired     = false;
                    state.HighWaterMarkUsd  = decimal.MinValue;
                    state.ExchangeSlOrderId = 0;
                    state.ExchangeSlPrice   = 0;
                    state.LastRatchetPnl    = decimal.MinValue;
                    state.InitialPnlPct    = pos.PnlPercent;
                    state.LastKnownSize    = pos.Size;
                    state.LastKnownEntry   = pos.EntryPrice;
                    state.LastKnownIsLong  = pos.IsLong;
                    // Don't skip — process this observation with fresh state
                }
                else
                {
                    // Update for next cycle
                    state.LastKnownSize    = pos.Size;
                    state.LastKnownEntry   = pos.EntryPrice;
                    state.LastKnownIsLong  = pos.IsLong;
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

                // ── Exchange ratcheting trailing stop ─────────────────────────
                if (riskConfig.ExchangeTrailingEnabled && !state.TrailingFired)
                    await UpdateExchangeTrailingSlAsync(pos, riskConfig, state, ct);

                // Update state flags
                if (pos.UnrealisedPnl > -riskConfig.SlUsd) state.HasBeenAboveSl = true;
                if (pos.UnrealisedPnl < riskConfig.TpUsd)  state.HasBeenBelowTp = true;
            }

            // Remove state for closed positions — cancel any exchange SL orders
            var open = positions.Select(p => p.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _states.Keys.Where(k => !open.Contains(k)).ToList())
            {
                var closedState = _states[key];
                if (closedState.ExchangeSlOrderId != 0)
                {
                    var assetInfo = _client.GetAssetIndex(key);
                    if (assetInfo != null)
                    {
                        await _client.CancelOrderAsync(assetInfo.Value.index,
                            closedState.ExchangeSlOrderId, ct);
                        Log.Information("Cancelled exchange SL for closed position {Symbol}", key);
                    }
                }
                _states.Remove(key);
            }

            // Snapshot the symbols seen this cycle — used next cycle to detect close+reopen
            _prevCycleSymbols = positions.Select(p => p.Symbol)
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // After first full cycle, any new positions are session-opened
            _startupComplete = true;
        }

        /// <summary>
        /// Resets SL-related state when config.json is reloaded so new thresholds apply.
        /// TpFired and TrailingFired are intentionally preserved — a TP/trail that already
        /// fired should not re-fire just because the config was saved again.
        /// </summary>
        /// <summary>
        /// Completely removes all state for a symbol — call immediately after a manual close
        /// so any new position on the same symbol starts with a clean slate.
        /// </summary>
        public void ClearSymbol(string symbol) => _states.Remove(symbol);

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
        /// Returns exchange trailing SL info: (slPrice, isActive).
        /// Returns null if exchange trailing is not enabled or not yet placed.
        /// </summary>
        public (decimal slPrice, bool active)? GetExchangeTrailingInfo(string symbol)
        {
            if (!_states.TryGetValue(symbol, out var state)) return null;
            var cfg = GetRiskConfig(symbol);
            if (!cfg.ExchangeTrailingEnabled) return null;
            if (state.ExchangeSlOrderId == 0) return (0, false);
            return (state.ExchangeSlPrice, true);
        }

        /// <summary>
        /// Returns trailing stop display info: (highWaterMark, isActive, isFired).
        /// Returns null if trailing is not enabled for this symbol.
        /// </summary>
        public (decimal hwmUsd, bool active, bool fired)? GetTrailingInfo(string symbol)
        {
            if (!_states.TryGetValue(symbol, out var state)) return null;
            var cfg = GetRiskConfig(symbol);
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
            var state = _states.GetValueOrDefault(pos.Symbol);
            if (state == null) return;
            if (state.OrderInFlight) { Log.Debug("TriggerSL: skipping {Symbol} — order in flight", pos.Symbol); return; }

            var assetInfo = _client.GetAssetIndex(pos.Symbol);
            if (assetInfo == null) { OrderFailed?.Invoke(pos.Symbol, $"SL: asset index unknown for {pos.Symbol}"); return; }

            var (_, szDec) = assetInfo.Value;
            var isBuy      = !pos.IsLong;

            state.OrderInFlight = true;
            try
            {
                var freshPrice = await _client.GetFreshMarkPriceAsync(pos.Symbol, ct) ?? pos.MarkPrice;
                Log.Debug("SL {Symbol} using price {Fresh:G6} (cached {Cached:G6})",
                    pos.Symbol, freshPrice, pos.MarkPrice);

                var (ok, msg) = await PlaceCloseOrderAsync(
                    pos.Symbol, isBuy, freshPrice, pos.Size, szDec,
                    cfg.SlCloseIsLimit, cfg.SlCloseOffsetUsd, "SL", ct);

                if (ok)
                {
                    Log.Information("SL placed: {Label}", label);
                    OrderPlaced?.Invoke(pos.Symbol, $"🔴 SL {label} — closed");
                }
                else
                {
                    Log.Error("SL FAILED after all retries for {Label}: {Msg}", label, msg);
                    OrderFailed?.Invoke(pos.Symbol, $"⚠ SL FAILED — manual close required for {pos.Symbol}");
                }
            }
            finally { state.OrderInFlight = false; }
        }

        private async Task TriggerTpAsync(
            PositionInfo pos, SymbolRiskConfig cfg, string label, CancellationToken ct)
        {
            var state = _states.GetValueOrDefault(pos.Symbol);
            if (state == null) return;
            if (state.OrderInFlight) { Log.Debug("TriggerTP: skipping {Symbol} — order in flight", pos.Symbol); return; }

            var assetInfo = _client.GetAssetIndex(pos.Symbol);
            if (assetInfo == null) { OrderFailed?.Invoke(pos.Symbol, $"TP: asset index unknown for {pos.Symbol}"); return; }

            var (_, szDec) = assetInfo.Value;
            var isBuy      = !pos.IsLong;

            var closeSize = Math.Round(pos.Size * cfg.TpSizeDecimal, szDec, MidpointRounding.ToZero);
            if (closeSize <= 0) { OrderFailed?.Invoke(pos.Symbol, $"TP: close size is 0 for {pos.Symbol}"); return; }

            state.OrderInFlight = true;
            try
            {
                var freshPrice = await _client.GetFreshMarkPriceAsync(pos.Symbol, ct) ?? pos.MarkPrice;
                Log.Debug("TP {Symbol} using price {Fresh:G6} (cached {Cached:G6})",
                    pos.Symbol, freshPrice, pos.MarkPrice);

                var (ok, msg) = await PlaceCloseOrderAsync(
                    pos.Symbol, isBuy, freshPrice, closeSize, szDec,
                    cfg.TpCloseIsLimit, cfg.TpCloseOffsetUsd, "TP", ct);

                if (ok)
                {
                    Log.Information("TP placed: {Label}", label);
                    OrderPlaced?.Invoke(pos.Symbol, $"🟢 TP {label} — closed {cfg.TpSizeDecimal:P0}");
                }
                else
                {
                    Log.Error("TP FAILED after all retries for {Label}: {Msg}", label, msg);
                    OrderFailed?.Invoke(pos.Symbol, $"⚠ TP FAILED — manual close required for {pos.Symbol}");
                }
            }
            finally { state.OrderInFlight = false; }
        }

        private async Task UpdateExchangeTrailingSlAsync(
            PositionInfo pos, SymbolRiskConfig cfg, SymbolState state, CancellationToken ct)
        {
            // Not yet profitable enough to activate
            if (pos.UnrealisedPnl < cfg.TrailingMinProfitUsd) return;

            // Check if we need to place or ratchet the SL
            bool shouldRatchet = state.ExchangeSlOrderId == 0           // not placed yet
                || pos.UnrealisedPnl >= state.LastRatchetPnl + cfg.TrailingStepUsd; // new peak

            if (!shouldRatchet) return;

            var assetInfo = _client.GetAssetIndex(pos.Symbol);
            if (assetInfo == null) return;
            var (assetIndex, szDec) = assetInfo.Value;

            // Calculate new SL price: retrace amount below current mark
            var newSlPrice = pos.IsLong
                ? pos.MarkPrice - cfg.TrailingRetraceUsd / pos.Size
                : pos.MarkPrice + cfg.TrailingRetraceUsd / pos.Size;

            // Only ratchet in the favourable direction (never move SL against position)
            if (state.ExchangeSlOrderId != 0)
            {
                bool slImproved = pos.IsLong
                    ? newSlPrice > state.ExchangeSlPrice   // long: new SL must be higher
                    : newSlPrice < state.ExchangeSlPrice;  // short: new SL must be lower
                if (!slImproved) return;

                // Cancel existing SL before placing new one
                var (cancelOk, _) = await _client.CancelOrderAsync(assetIndex,
                    state.ExchangeSlOrderId, ct);
                if (!cancelOk)
                {
                    Log.Warning("ExchangeTrailing: failed to cancel SL oid={Oid} for {Symbol}",
                        state.ExchangeSlOrderId, pos.Symbol);
                }
                state.ExchangeSlOrderId = 0;
            }

            // Place new SL trigger on exchange
            var (ok, msg, oid) = await _client.PlaceTriggerOrderAsync(
                pos.Symbol, !pos.IsLong, newSlPrice, newSlPrice,
                pos.Size, szDec, "sl", true, ct);

            if (ok)
            {
                state.ExchangeSlOrderId = oid;
                state.ExchangeSlPrice   = newSlPrice;
                state.LastRatchetPnl    = pos.UnrealisedPnl;
                Log.Information(
                    "ExchangeTrailing ratchet: {Symbol} SL → {SlPrice:G6}  PnL=${Pnl:F2}  oid={Oid}",
                    pos.Symbol, newSlPrice, pos.UnrealisedPnl, oid);
                OrderPlaced?.Invoke(pos.Symbol,
                    $"📌 Exchange SL ratcheted: {pos.Symbol} → ${newSlPrice:G6}");
            }
            else
            {
                Log.Warning("ExchangeTrailing: failed to place SL for {Symbol}: {Msg}",
                    pos.Symbol, msg);
            }
        }

        private async Task TriggerTrailingAsync(
            PositionInfo pos, SymbolRiskConfig cfg, string label, CancellationToken ct)
        {
            var state = _states.GetValueOrDefault(pos.Symbol);
            if (state == null) return;

            // Prevent concurrent fires — if an order is already in flight for this symbol, skip
            if (state.OrderInFlight)
            {
                Log.Debug("TriggerTrailing: skipping {Symbol} — order already in flight", pos.Symbol);
                return;
            }

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

            // Mark in-flight — prevents concurrent monitor cycles from firing simultaneously
            state.OrderInFlight = true;
            try
            {
                // Fetch fresh mark price — avoids stale 5-second poll data at execution time
                var freshPrice = await _client.GetFreshMarkPriceAsync(pos.Symbol, ct)
                                 ?? pos.MarkPrice;
                Log.Debug("Trail {Symbol} using price {Fresh:G6} (cached {Cached:G6})",
                    pos.Symbol, freshPrice, pos.MarkPrice);

                var (ok, msg) = await PlaceCloseOrderAsync(
                    pos.Symbol, isBuy, freshPrice, closeSize, szDec,
                    cfg.TrailingCloseIsLimit, cfg.TrailingCloseOffsetUsd, "Trailing", ct);

                if (ok)
                {
                    Log.Information("Trailing placed: {Label}", label);
                    OrderPlaced?.Invoke(pos.Symbol, $"🔒 Trailing {label} — closed {cfg.TrailingSizeDecimal:P0}");
                }
                else
                {
                    // All internal retries exhausted — log and leave TrailingFired=true
                    // so we don't keep hammering. User must close manually.
                    Log.Error("Trailing FAILED after all retries for {Label}: {Msg}", label, msg);
                    OrderFailed?.Invoke(pos.Symbol, $"⚠ Trailing FAILED — manual close required for {pos.Symbol}");
                }
            }
            finally
            {
                state.OrderInFlight = false;
            }
        }

        /// <summary>
        /// Places either a limit or market close, depending on the isLimit flag.
        /// When limit: price = freshPrice + offsetUsd (signed, so negative = below mark).
        /// Returns (ok, message) in both cases.
        /// </summary>
        private async Task<(bool ok, string msg)> PlaceCloseOrderAsync(
            string symbol, bool isBuy, decimal freshPrice, decimal size, int szDec,
            bool isLimit, decimal offsetUsd, string triggerName, CancellationToken ct)
        {
            if (isLimit)
            {
                var limitPrice = freshPrice + offsetUsd;
                Log.Debug("{Trigger} {Symbol}: limit close @ {Price:G6} (fresh {Fresh:G6} + offset {Off})",
                    triggerName, symbol, limitPrice, freshPrice, offsetUsd);
                var (ok, msg, _) = await _client.PlaceLimitCloseAsync(symbol, isBuy, limitPrice, size, szDec, ct);
                return (ok, msg);
            }
            else
            {
                return await _client.PlaceMarketCloseAsync(symbol, isBuy, freshPrice, size, szDec, ct);
            }
        }
    }

    public enum SymbolRiskStatus { Normal, SlFired, TpFired, TrailingFired }
}
