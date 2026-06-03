using Newtonsoft.Json;

namespace HyperliquidScanner.Models
{
    /// <summary>
    /// Per-symbol risk management thresholds.
    /// Use symbol = "DEFAULT" as the fallback for any symbol not explicitly listed.
    ///
    /// All SL/TP/trailing thresholds are expressed in USD (unrealised PnL):
    ///   slUsd = 15  →  stop loss when down $15
    ///   tpUsd = 30  →  take profit when up $30
    ///   tpSizeDecimal = 0.5 → close 50% of position size on TP
    /// </summary>
    public class SymbolRiskConfig
    {
        [JsonProperty("symbol")]
        public string Symbol { get; set; } = "DEFAULT";

        /// <summary>Stop loss threshold in USD. Fires when unrealised PnL ≤ -slUsd.</summary>
        [JsonProperty("slUsd")]
        public decimal SlUsd { get; set; } = 10m;

        /// <summary>Take profit threshold in USD. Fires when unrealised PnL ≥ +tpUsd.</summary>
        [JsonProperty("tpUsd")]
        public decimal TpUsd { get; set; } = 20m;

        /// <summary>Fraction of position size to close on TP. 0.5 = close half, 1.0 = close all.</summary>
        [JsonProperty("tpSizeDecimal")]
        public decimal TpSizeDecimal { get; set; } = 0.5m;

        /// <summary>
        /// Number of consecutive 5-second polls the PnL must stay below the SL threshold
        /// before the SL order fires. Filters out wick-induced false triggers.
        /// Also requires RSI slope to be negative (momentum confirming the move).
        /// Default 3 = 15 seconds of sustained breach.
        /// </summary>
        [JsonProperty("slConfirmationPolls")]
        public int SlConfirmationPolls { get; set; } = 3;

        /// <summary>If true, this position is hidden from the positions panel grid.</summary>
        [JsonProperty("gridview_disabled")]
        public bool GridViewDisabled { get; set; } = false;

        /// <summary>Enable automatic stop loss for this symbol.</summary>
        [JsonProperty("sl_enabled")]
        public bool SlEnabled { get; set; } = false;

        /// <summary>Enable automatic take profit for this symbol.</summary>
        [JsonProperty("tp_enabled")]
        public bool TpEnabled { get; set; } = false;

        /// <summary>Enable app-side trailing stop for this symbol.</summary>
        [JsonProperty("trailing_enabled")]
        public bool TrailingEnabled { get; set; } = false;

        /// <summary>
        /// Minimum USD profit the position must reach before the trailing stop activates.
        /// e.g. 15 = trailing only activates after +$15 unrealised PnL.
        /// </summary>
        [JsonProperty("trailingMinProfitUsd")]
        public decimal TrailingMinProfitUsd { get; set; } = 10m;

        /// <summary>
        /// How much the USD PnL must retrace from its peak to trigger the trailing stop.
        /// e.g. 8 = fires if PnL drops $8 from its high-water mark.
        /// </summary>
        [JsonProperty("trailingRetraceUsd")]
        public decimal TrailingRetraceUsd { get; set; } = 5m;

        /// <summary>Fraction of position to close on trailing stop fire. 1.0 = close all.</summary>
        [JsonProperty("trailingSizeDecimal")]
        public decimal TrailingSizeDecimal { get; set; } = 1.0m;

        /// <summary>
        /// Enable exchange-native ratcheting trailing stop.
        /// Places and updates a real SL trigger order on Hyperliquid as PnL grows.
        /// Persists when the app is closed — survives restarts.
        /// Uses trailingMinProfitUsd to activate and trailingRetraceUsd for the SL distance.
        /// </summary>
        [JsonProperty("exchangeTrailing_enabled")]
        public bool ExchangeTrailingEnabled { get; set; } = false;

        /// <summary>
        /// How much additional PnL growth (USD) triggers a SL ratchet.
        /// e.g. 5 = move the exchange SL every time PnL grows another $5.
        /// </summary>
        [JsonProperty("trailingStepUsd")]
        public decimal TrailingStepUsd { get; set; } = 5m;

        // ── Phase 3: RSI-LL auto-entry ────────────────────────────────────────

        /// <summary>Enable automatic entry on RSI Lower Low signal for this symbol.</summary>
        [JsonProperty("rsiLLAutoEntry")]
        public bool RsiLLAutoEntry { get; set; } = false;

        /// <summary>USD margin to commit per auto-entry (e.g. 100 = $100 margin).</summary>
        [JsonProperty("rsiLLEntrySizeUsd")]
        public decimal RsiLLEntrySizeUsd { get; set; } = 100m;

        /// <summary>Leverage to apply on auto-entry.</summary>
        [JsonProperty("rsiLLLeverage")]
        public int RsiLLLeverage { get; set; } = 5;

        /// <summary>
        /// Limit entry price offset from mark price.
        /// 0.001 = 0.1% inside the bid — nearly instant fill, earns maker fee.
        /// </summary>
        [JsonProperty("rsiLLEntryOffsetPct")]
        public decimal RsiLLEntryOffsetPct { get; set; } = 0.001m;

        /// <summary>Seconds to wait for entry limit to fill before cancelling.</summary>
        [JsonProperty("rsiLLEntryTimeoutSec")]
        public int RsiLLEntryTimeoutSec { get; set; } = 30;

        /// <summary>If true, auto-entry fires even if a position already exists (DCA in).</summary>
        [JsonProperty("rsiLLDcaIfPositionExists")]
        public bool RsiLLDcaIfPositionExists { get; set; } = true;

        /// <summary>If true, shows a timed confirmation dialog before placing the entry order.</summary>
        [JsonProperty("rsiLLRequireConfirmation")]
        public bool RsiLLRequireConfirmation { get; set; } = true;

        /// <summary>USD take profit for the auto-entry bracket order.</summary>
        [JsonProperty("rsiLLTpUsd")]
        public decimal RsiLLTpUsd { get; set; } = 20m;

        /// <summary>USD stop loss for the auto-entry bracket order.</summary>
        [JsonProperty("rsiLLSlUsd")]
        public decimal RsiLLSlUsd { get; set; } = 10m;

        /// <summary>Fraction of auto-entry position to close on TP. 1.0 = close all.</summary>
        [JsonProperty("rsiLLTpSizeDecimal")]
        public decimal RsiLLTpSizeDecimal { get; set; } = 1.0m;
    }
}
