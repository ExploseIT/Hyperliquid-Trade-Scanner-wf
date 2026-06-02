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
    }
}
