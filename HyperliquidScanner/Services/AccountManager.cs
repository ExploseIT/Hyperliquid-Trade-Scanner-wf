using HyperliquidScanner.Models;
using Serilog;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// One active sub-account with its own HyperliquidClient and PositionMonitor.
    /// Provides per-account risk config resolution (sub-account symbolInfo → global fallback).
    /// </summary>
    public class AccountContext
    {
        public SubAccountConfig  Config  { get; }
        public HyperliquidClient Client  { get; }
        public PositionMonitor?  Monitor { get; }

        private readonly AppConfig _globalConfig;

        public string Name => !string.IsNullOrWhiteSpace(Config.Name) ? Config.Name : "Main Account";

        public AccountContext(SubAccountConfig config, HyperliquidClient client,
                              PositionMonitor? monitor, AppConfig globalConfig)
        {
            Config        = config;
            Client        = client;
            Monitor       = monitor;
            _globalConfig = globalConfig;
        }

        /// <summary>
        /// Returns risk config for a symbol — checks this account's own symbolInfo first,
        /// then falls back to the global config.
        /// </summary>
        public SymbolRiskConfig GetRiskConfig(string symbol) =>
            Config.GetLocalRiskConfig(symbol) ?? _globalConfig.GetRiskConfig(symbol);
    }

    /// <summary>
    /// Manages one or more active sub-accounts, each with its own client and monitor.
    /// Provides merged position fetching and per-account order routing.
    /// </summary>
    public class AccountManager
    {
        public IReadOnlyList<AccountContext> Accounts { get; }

        /// <summary>Primary account — used for market data (candles, mids, meta).</summary>
        public AccountContext Primary => Accounts[0];

        public AccountManager(IReadOnlyList<AccountContext> accounts)
        {
            if (accounts.Count == 0)
                throw new InvalidOperationException("AccountManager requires at least one active account.");
            Accounts = accounts;
        }

        /// <summary>Finds the context for a named account (null if not found).</summary>
        public AccountContext? GetByName(string name) =>
            Accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Fetches positions from all active accounts in parallel and merges them,
        /// tagging each PositionInfo with its AccountName.
        /// </summary>
        public async Task<List<PositionInfo>> GetAllPositionsAsync(CancellationToken ct = default)
        {
            var tasks   = Accounts.Select(a => FetchForAccountAsync(a, ct)).ToList();
            var results = await Task.WhenAll(tasks);
            return results.SelectMany(r => r).ToList();
        }

        private static async Task<List<PositionInfo>> FetchForAccountAsync(
            AccountContext account, CancellationToken ct)
        {
            try
            {
                var positions = await account.Client.GetPositionsAsync(ct);
                foreach (var p in positions)
                    p.AccountName = account.Name;
                return positions;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AccountManager] Failed to fetch positions for {Account}", account.Name);
                return new List<PositionInfo>();
            }
        }

        /// <summary>
        /// Builds an AccountManager from config — one AccountContext per active sub-account.
        /// Falls back to legacy flat fields if no subAccounts are defined.
        /// </summary>
        public static AccountManager Create(AppConfig config)
        {
            var contexts = new List<AccountContext>();
            var activeAccounts = config.SubAccounts.Where(s => s.Active).ToList();

            if (activeAccounts.Count == 0)
            {
                // Legacy mode — single account from flat config fields
                var legacySub = new SubAccountConfig
                {
                    Name          = "Main Account",
                    WalletAddress = config.WalletAddressLegacy,
                    PrivateKey    = config.PrivateKeyLegacy,
                    Active        = true
                };
                var client  = new HyperliquidClient(config, legacySub);
                var monitor = legacySub.HasPrivateKey
                    ? new PositionMonitor(client, config)
                    : null;
                contexts.Add(new AccountContext(legacySub, client, monitor, config));
            }
            else
            {
                foreach (var sub in activeAccounts)
                {
                    var client  = new HyperliquidClient(config, sub);
                    var monitor = sub.HasPrivateKey
                        ? new PositionMonitor(client, config,
                            sub.SymbolInfo.Count > 0 ? sub.SymbolInfo : null)
                        : null;
                    contexts.Add(new AccountContext(sub, client, monitor, config));
                }
            }

            Log.Information("[AccountManager] {Count} account(s) active: {Names}",
                contexts.Count, string.Join(", ", contexts.Select(c => c.Name)));

            return new AccountManager(contexts);
        }
    }
}
