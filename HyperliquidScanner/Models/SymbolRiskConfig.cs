using Newtonsoft.Json;

namespace HyperliquidScanner.Models
{
    /// <summary>
    /// Per-symbol risk management thresholds.
    /// Use symbol = "DEFAULT" as the fallback for any symbol not explicitly listed.
    ///
    /// Thresholds are expressed as a decimal fraction of notional position value:
    ///   slDecimal = 0.025  →  stop loss at -2.5% of notional
    ///   tpDecimal = 0.05   →  take profit at +5.0% of notional
    ///   tpSizeDecimal = 0.5 → close 50% of position size on TP
    /// </summary>
    public class SymbolRiskConfig
    {
        [JsonProperty("symbol")]
        public string Symbol { get; set; } = "DEFAULT";

        /// <summary>Stop loss threshold as fraction of notional. 0.025 = 2.5% loss.</summary>
        [JsonProperty("slDecimal")]
        public decimal SlDecimal { get; set; } = 0.025m;

        /// <summary>Take profit threshold as fraction of notional. 0.05 = 5.0% gain.</summary>
        [JsonProperty("tpDecimal")]
        public decimal TpDecimal { get; set; } = 0.05m;

        /// <summary>Fraction of position size to close on TP. 0.5 = close half.</summary>
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

        /// <summary>Enable automatic stop loss for this symbol.</summary>
        [JsonProperty("sl_enabled")]
        public bool SlEnabled { get; set; } = false;

        /// <summary>Enable automatic take profit for this symbol.</summary>
        [JsonProperty("tp_enabled")]
        public bool TpEnabled { get; set; } = false;
    }
}
