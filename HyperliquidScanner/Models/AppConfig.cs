using Newtonsoft.Json;

namespace HyperliquidScanner.Models
{
    /// <summary>
    /// Master account — wallet address only, no private key (never trade from master).
    /// </summary>
    public class MasterAccountConfig
    {
        [JsonProperty("walletAddress")]
        public string WalletAddress { get; set; } = string.Empty;
    }

    /// <summary>
    /// A named sub-account with its own wallet address and API private key.
    /// Set active:true on exactly one sub-account to make it the trading account.
    /// </summary>
    public class SubAccountConfig
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("walletAddress")]
        public string WalletAddress { get; set; } = string.Empty;

        /// <summary>Hyperliquid API wallet private key for this sub-account.</summary>
        [JsonProperty("privateKey")]
        public string PrivateKey { get; set; } = string.Empty;

        /// <summary>Set true on any sub-account to include it in monitoring and trading.</summary>
        [JsonProperty("active")]
        public bool Active { get; set; } = false;

        /// <summary>Per-symbol risk config overrides for this sub-account. Overrides global symbolInfo.</summary>
        [JsonProperty("symbolInfo")]
        public List<SymbolRiskConfig> SymbolInfo { get; set; } = new();

        public bool HasPrivateKey => !string.IsNullOrWhiteSpace(PrivateKey);

        /// <summary>
        /// Returns risk config for a symbol from this sub-account's own symbolInfo.
        /// Returns null if this sub-account has no symbolInfo (fall back to global).
        /// </summary>
        public SymbolRiskConfig? GetLocalRiskConfig(string symbol)
        {
            if (SymbolInfo.Count == 0) return null;
            return SymbolInfo.FirstOrDefault(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                ?? SymbolInfo.FirstOrDefault(s => s.Symbol.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase));
        }
    }

    public partial class AppConfig
    {
        // ── Legacy flat fields (still work if no subAccounts defined) ────────
        [JsonProperty("walletAddress")]
        public string WalletAddressLegacy { get; set; } = string.Empty;

        /// <summary>
        /// Optional — only needed for private endpoints (positions, balances).
        /// Leave empty if you only want public market scanning.
        /// Recommend using a Hyperliquid API sub-wallet key, not your main MetaMask key.
        /// </summary>
        [JsonProperty("privateKey")]
        public string PrivateKeyLegacy { get; set; } = string.Empty;

        // ── New multi-account structure ───────────────────────────────────────
        /// <summary>Master account address (read-only — never trade from this).</summary>
        [JsonProperty("masterAccount")]
        public MasterAccountConfig? MasterAccount { get; set; }

        /// <summary>List of sub-accounts. Set active:true on exactly one to use it.</summary>
        [JsonProperty("subAccounts")]
        public List<SubAccountConfig> SubAccounts { get; set; } = new();

        // ── Computed: prefer active sub-accounts, fall back to legacy ────────
        [JsonIgnore]
        public IReadOnlyList<SubAccountConfig> ActiveSubAccounts =>
            SubAccounts.Where(s => s.Active).ToList();

        /// <summary>Primary active sub-account (first active). Null if legacy mode.</summary>
        [JsonIgnore]
        public SubAccountConfig? ActiveSubAccount =>
            SubAccounts.FirstOrDefault(s => s.Active);

        /// <summary>Display names of all active trading accounts, comma-separated.</summary>
        [JsonIgnore]
        public string ActiveAccountName =>
            ActiveSubAccounts.Count > 0
                ? string.Join(" + ", ActiveSubAccounts.Select(s => s.Name))
                : "Main Account";

        /// <summary>Wallet address of the primary active account (for legacy single-account uses).</summary>
        [JsonIgnore]
        public string WalletAddress =>
            ActiveSubAccount?.WalletAddress ?? WalletAddressLegacy;

        /// <summary>Private key of the primary active account (for legacy single-account uses).</summary>
        [JsonIgnore]
        public string PrivateKey =>
            ActiveSubAccount?.PrivateKey ?? PrivateKeyLegacy;

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

        /// <summary>Portfolio goal in USDC — shown in the connection bar as a progress tracker.</summary>
        [JsonProperty("portfolioGoalUsd")]
        public decimal PortfolioGoalUsd { get; set; } = 1000m;

        /// <summary>
        /// Master switch for all automated trading (Phase 3 auto-entry).
        /// When false, no entry orders are placed regardless of per-symbol rsiLLAutoEntry settings.
        /// Hot-reloadable — set to false to instantly kill all auto-trading.
        /// </summary>
        [JsonProperty("autotrading_enabled")]
        public bool AutotradingEnabled { get; set; } = false;

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

        /// <summary>If true, a scan starts automatically when the app launches.</summary>
        [JsonProperty("scanOnStartup")]
        public bool ScanOnStartup { get; set; } = false;

        /// <summary>
        /// Sets the Auto-refresh dropdown on startup.
        /// Valid values: "Off", "1 min", "2 min", "5 min", "10 min"
        /// </summary>
        [JsonProperty("autoRefreshInterval")]
        public string AutoRefreshInterval { get; set; } = "Off";

        [JsonIgnore]
        public bool HasPrivateKey => !string.IsNullOrWhiteSpace(PrivateKey);
    }
}
