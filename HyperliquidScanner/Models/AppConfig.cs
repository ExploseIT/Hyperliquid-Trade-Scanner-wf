using Newtonsoft.Json;

namespace HyperliquidScanner.Models
{
    public partial class AppConfig
    {
        [JsonProperty("walletAddress")]
        public string WalletAddress { get; set; } = string.Empty;

        /// <summary>
        /// Optional — only needed for private endpoints (positions, balances).
        /// Leave empty if you only want public market scanning.
        /// Recommend using a Hyperliquid API sub-wallet key, not your main MetaMask key.
        /// </summary>
        [JsonProperty("privateKey")]
        public string PrivateKey { get; set; } = string.Empty;

        [JsonProperty("defaultTimeframe")]
        public string DefaultTimeframe { get; set; } = "1h";

        /// <summary>
        /// Maximum number of assets to scan. Hyperliquid has ~150+ perps.
        /// </summary>
        [JsonProperty("maxAssets")]
        public int MaxAssets { get; set; } = 200;

        /// <summary>
        /// How many of the 3 trend indicators must agree to flag an asset bullish.
        /// 1 = permissive, 2 = balanced (recommended), 3 = strict
        /// </summary>
        [JsonProperty("bullishThreshold")]
        public int BullishThreshold { get; set; } = 2;

        /// <summary>
        /// Milliseconds to wait between asset candle requests to avoid rate limiting.
        /// </summary>
        [JsonProperty("requestDelayMs")]
        public int RequestDelayMs { get; set; } = 100;

        /// <summary>
        /// HIP-3 dex namespaces to include in scans alongside the main HL universe.
        /// Each entry triggers an additional meta call: {"type":"meta","dex":"xyz"}.
        /// Assets from these dexes appear in the grid as "dex:SYMBOL" (e.g. "xyz:MU").
        /// </summary>
        [JsonProperty("hip3Dexes")]
        public List<string> Hip3Dexes { get; set; } = new() { "xyz" };

        /// <summary>
        /// Master switch for automatic stop loss and take profit management.
        /// When false, positions are monitored and displayed but no orders are placed.
        /// </summary>
        [JsonProperty("autoRiskManagement")]
        public bool AutoRiskManagement { get; set; } = false;

        /// <summary>
        /// Per-symbol SL/TP thresholds. Use symbol = "DEFAULT" as fallback.
        /// </summary>
        [JsonProperty("symbolInfo")]
        public List<SymbolRiskConfig> SymbolInfo { get; set; } = new();

        /// <summary>Returns the risk config for a symbol, falling back to DEFAULT.</summary>
        public SymbolRiskConfig GetRiskConfig(string symbol) =>
            SymbolInfo.FirstOrDefault(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            ?? SymbolInfo.FirstOrDefault(s => s.Symbol.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
            ?? new SymbolRiskConfig();

        public bool HasPrivateKey => !string.IsNullOrWhiteSpace(PrivateKey);
    }
}
