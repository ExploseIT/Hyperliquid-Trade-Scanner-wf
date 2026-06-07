namespace HyperliquidScanner.Models
{
    /// <summary>
    /// Represents a single open perpetual position on Hyperliquid.
    /// Populated from the clearinghouseState API response.
    /// </summary>
    public class PositionInfo
    {
        public string  Symbol           { get; set; } = string.Empty;
        public bool    IsLong           { get; set; }   // true = long, false = short
        public decimal Size             { get; set; }   // absolute size (always positive)
        public decimal EntryPrice       { get; set; }
        public decimal MarkPrice        { get; set; }   // derived from positionValue / size
        public decimal UnrealisedPnl    { get; set; }   // USD
        public decimal PnlPercent       { get; set; }   // fraction of notional e.g. 0.025 = 2.5%
        public decimal? LiquidationPrice { get; set; }  // null if no liquidation risk
        public int     Leverage         { get; set; }
        public string  LeverageType     { get; set; } = "cross"; // "cross" or "isolated"
        public decimal MarginUsed       { get; set; }   // USD margin allocated

        /// <summary>Name of the sub-account that holds this position (e.g. "HL for Shorts").</summary>
        public string AccountName { get; set; } = string.Empty;

        public string SideLabel    => IsLong ? "Long" : "Short";
        public string LeverageLabel => $"{Leverage}× {LeverageType}";
    }
}
