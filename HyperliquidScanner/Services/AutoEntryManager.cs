using HyperliquidScanner.Forms;
using HyperliquidScanner.Models;
using Serilog;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Phase 3: Orchestrates automated entry orders triggered by RSI Lower Low signals.
    ///
    /// Flow per signal:
    ///   1. Check autotrading_enabled master switch + per-symbol rsiLLAutoEntry
    ///   2. Check DCA policy (skip or allow if position already exists)
    ///   3. Optionally show timed confirmation dialog
    ///   4. Place limit entry order at mark × (1 - entryOffsetPct)
    ///   5. Poll every 2s for fill — cancel after rsiLLEntryTimeoutSec
    ///   6. On fill: immediately place TP limit + SL trigger bracket on exchange
    ///   7. Fire events throughout for UI/sound notifications
    /// </summary>
    public class AutoEntryManager
    {
        private readonly HyperliquidClient      _client;
        private readonly AppConfig              _config;

        // ── Events ────────────────────────────────────────────────────────────
        public event Action<string, string>? EntryPlaced;   // (symbol, message)
        public event Action<string, string>? EntryFilled;   // (symbol, message)
        public event Action<string, string>? EntryFailed;   // (symbol, message)
        public event Action<string, string>? BracketPlaced; // (symbol, message)

        public AutoEntryManager(HyperliquidClient client, AppConfig config)
        {
            _client = client;
            _config = config;
        }

        /// <summary>
        /// Called by MainForm when new RSI-LL signals are detected.
        /// Processes each signal that has rsiLLAutoEntry enabled.
        /// </summary>
        public async Task ProcessSignalsAsync(
            IEnumerable<AssetScanResult> signals,
            IReadOnlyList<PositionInfo>  openPositions,
            decimal                      markPrice,
            CancellationToken            ct = default)
        {
            if (!_config.AutotradingEnabled) return;

            foreach (var sig in signals)
            {
                ct.ThrowIfCancellationRequested();
                var cfg = _config.GetRiskConfig(sig.Asset);
                if (!cfg.RsiLLAutoEntry) continue;

                await ProcessSingleSignalAsync(sig, cfg, openPositions, ct);
            }
        }

        private async Task ProcessSingleSignalAsync(
            AssetScanResult            signal,
            SymbolRiskConfig           cfg,
            IReadOnlyList<PositionInfo> openPositions,
            CancellationToken          ct)
        {
            // Check if position already exists
            var existingPos = openPositions.FirstOrDefault(p =>
                p.Symbol.Equals(signal.Asset, StringComparison.OrdinalIgnoreCase));

            if (existingPos != null && !cfg.RsiLLDcaIfPositionExists)
            {
                Log.Information("AutoEntry: {Symbol} skipped — position exists and DCA disabled",
                    signal.Asset);
                return;
            }

            // Get asset index
            var assetInfo = _client.GetAssetIndex(signal.Asset);
            if (assetInfo == null)
            {
                Log.Warning("AutoEntry: {Symbol} skipped — asset index not found", signal.Asset);
                EntryFailed?.Invoke(signal.Asset, $"Asset index unknown for {signal.Asset}");
                return;
            }
            var (assetIndex, szDec) = assetInfo.Value;

            // Calculate entry details
            var markPx      = signal.LastPrice;
            var entryPrice  = markPx * (1 - cfg.RsiLLEntryOffsetPct);  // slightly below mark (long)
            var notional    = cfg.RsiLLEntrySizeUsd * cfg.RsiLLLeverage;
            var size        = Math.Round(notional / entryPrice, szDec, MidpointRounding.ToZero);

            if (size <= 0)
            {
                EntryFailed?.Invoke(signal.Asset, $"Calculated size is 0 for {signal.Asset}");
                return;
            }

            // Confirmation dialog (must run on UI thread)
            if (cfg.RsiLLRequireConfirmation)
            {
                var confirmed = await ShowConfirmationAsync(
                    signal.Asset, cfg, entryPrice, size, ct);
                if (!confirmed)
                {
                    Log.Information("AutoEntry: {Symbol} — confirmation declined or timed out",
                        signal.Asset);
                    return;
                }
            }

            // Place entry limit order
            Log.Information("AutoEntry: placing entry for {Symbol} size={Size} @ {Price}",
                signal.Asset, size, entryPrice);

            var (placed, placeMsg, orderId) = await _client.PlaceLimitEntryAsync(
                signal.Asset, true, entryPrice, size, szDec, ct);

            if (!placed)
            {
                Log.Warning("AutoEntry: entry failed for {Symbol}: {Msg}", signal.Asset, placeMsg);
                EntryFailed?.Invoke(signal.Asset, $"Entry failed: {placeMsg}");
                return;
            }

            Log.Information("AutoEntry: entry placed for {Symbol} oid={Oid}", signal.Asset, orderId);
            EntryPlaced?.Invoke(signal.Asset,
                $"📥 Entry placed: {signal.Asset} Long ${cfg.RsiLLEntrySizeUsd:F0} @ ~${entryPrice:G6}");

            // Poll for fill
            var fillPrice = await WaitForFillAsync(
                signal.Asset, assetIndex, orderId, size,
                cfg.RsiLLEntryTimeoutSec, ct);

            if (fillPrice == null)
            {
                // Timeout — cancel the order
                Log.Warning("AutoEntry: {Symbol} entry timeout — cancelling oid={Oid}",
                    signal.Asset, orderId);
                await _client.CancelOrderAsync(assetIndex, orderId, ct);
                EntryFailed?.Invoke(signal.Asset,
                    $"Entry timeout — cancelled {signal.Asset} (no fill in {cfg.RsiLLEntryTimeoutSec}s)");
                return;
            }

            Log.Information("AutoEntry: {Symbol} filled @ {FillPrice}", signal.Asset, fillPrice);
            EntryFilled?.Invoke(signal.Asset,
                $"✅ Entry filled: {signal.Asset} @ ${fillPrice:G6}");

            // Place bracket: TP limit + SL trigger
            await PlaceBracketAsync(signal.Asset, assetIndex, szDec,
                fillPrice.Value, size, cfg, ct);
        }

        private async Task<decimal?> WaitForFillAsync(
            string symbol, int assetIndex, long orderId, decimal expectedSize,
            int timeoutSec, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(2_000, ct);

                // Check if order is gone from open orders
                var open = await _client.GetOpenOrdersAsync(ct);
                bool stillOpen = open.Any(o => o.oid == orderId);
                if (stillOpen) continue;

                // Order gone — check positions for fill confirmation
                var positions = await _client.GetPositionsAsync(ct);
                var pos = positions.FirstOrDefault(p =>
                    p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

                if (pos != null && pos.Size >= expectedSize * 0.95m)
                    return pos.MarkPrice; // use mark as fill price approximation

                // Order cancelled/rejected without fill
                return null;
            }
            return null; // timeout
        }

        private async Task PlaceBracketAsync(
            string symbol, int assetIndex, int szDec,
            decimal fillPrice, decimal size,
            SymbolRiskConfig cfg, CancellationToken ct)
        {
            // TP: limit order at fillPrice + (tpUsd / size)
            var tpPrice  = fillPrice + cfg.RsiLLTpUsd / size;
            var tpSize   = Math.Round(size * cfg.RsiLLTpSizeDecimal, szDec, MidpointRounding.ToZero);

            var (tpOk, tpMsg, _) = await _client.PlaceTriggerOrderAsync(
                symbol, false, tpPrice, tpPrice, tpSize, szDec, "tp", false, ct);

            // SL: market trigger at fillPrice - (slUsd / size)
            var slPrice  = fillPrice - cfg.RsiLLSlUsd / size;

            var (slOk, slMsg, _) = await _client.PlaceTriggerOrderAsync(
                symbol, false, slPrice, slPrice, size, szDec, "sl", true, ct);

            if (tpOk && slOk)
            {
                Log.Information("AutoEntry: bracket placed for {Symbol} TP=${Tp:G6} SL=${Sl:G6}",
                    symbol, tpPrice, slPrice);
                BracketPlaced?.Invoke(symbol,
                    $"🎯 Bracket: {symbol}  TP ${tpPrice:G6}  SL ${slPrice:G6}");
            }
            else
            {
                Log.Warning("AutoEntry: bracket partial/failed for {Symbol} — TP:{TpMsg} SL:{SlMsg}",
                    symbol, tpMsg, slMsg);
                BracketPlaced?.Invoke(symbol,
                    $"⚠ Bracket issue: {symbol}  TP:{(tpOk ? "✓" : "✗")}  SL:{(slOk ? "✓" : "✗")}");
            }
        }

        // ── Confirmation dialog (UI thread) ───────────────────────────────────

        private Task<bool> ShowConfirmationAsync(
            string symbol, SymbolRiskConfig cfg,
            decimal entryPrice, decimal size, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();

            // Must show on UI thread
            if (System.Windows.Forms.Application.OpenForms.Count == 0)
            { tcs.SetResult(false); return tcs.Task; }

            var mainForm = System.Windows.Forms.Application.OpenForms[0];
            mainForm.BeginInvoke(() =>
            {
                try
                {
                    using var dlg = new EntryConfirmationForm(
                        symbol,
                        cfg.RsiLLEntrySizeUsd,
                        cfg.RsiLLLeverage,
                        entryPrice,
                        cfg.RsiLLTpUsd,
                        cfg.RsiLLSlUsd,
                        cfg.RsiLLEntryTimeoutSec);
                    dlg.ShowDialog();
                    tcs.SetResult(dlg.Confirmed);
                }
                catch { tcs.SetResult(false); }
            });

            return tcs.Task;
        }
    }
}
