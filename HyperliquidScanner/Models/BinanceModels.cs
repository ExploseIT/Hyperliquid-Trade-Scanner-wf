namespace HyperliquidScanner.Models
{
    /// <summary>
    /// A single liquidation event pushed from the Binance Futures
    /// public WebSocket stream (!forceOrder@arr).
    ///
    /// Side semantics (Binance convention):
    ///   "SELL" = the exchange is force-selling the position → long liquidation
    ///   "BUY"  = the exchange is force-buying  the position → short liquidation
    /// </summary>
    public class BinanceLiquidationEvent
    {
        public string   Symbol   { get; set; } = "";
        public string   Side     { get; set; } = "";   // "BUY" | "SELL"
        public decimal  Price    { get; set; }
        public decimal  Quantity { get; set; }
        public DateTime Time     { get; set; }
        public string   Exchange { get; set; } = "";   // "Binance" | "Bybit" | "OKX" etc.
        public string   UniqueId { get; set; } = "";   // dedup key for REST polling

        public decimal UsdValue            => Price * Quantity;
        public bool    IsLongLiquidation   => Side.Equals("SELL", StringComparison.OrdinalIgnoreCase);

        /// <summary>Strips quote currency (BTCUSDT → BTC).</summary>
        public string BaseSymbol
        {
            get
            {
                var s = Symbol;
                foreach (var q in new[] { "USDT", "USDC", "BUSD", "USD", "PERP" })
                    if (s.EndsWith(q, StringComparison.OrdinalIgnoreCase))
                        return s[..^q.Length];
                return s;
            }
        }

        public string FormatUsd() => UsdValue switch
        {
            >= 1_000_000 => $"${UsdValue / 1_000_000:F1}M",
            >= 1_000     => $"${UsdValue / 1_000:F0}K",
            _            => $"${UsdValue:F0}"
        };
    }
}
