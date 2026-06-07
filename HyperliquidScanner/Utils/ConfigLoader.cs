using HyperliquidScanner.Models;
using Newtonsoft.Json;

namespace HyperliquidScanner.Utils
{
    public static class ConfigLoader
    {
        private const string ConfigFileName = "config.json";

        public static AppConfig Load()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);

            if (!File.Exists(path))
            {
                CreateDefault(path);
                throw new FileNotFoundException(
                    $"No config.json found. A template has been created at:\n{path}\n\nPlease fill in your wallet address and restart.");
            }

            var json = File.ReadAllText(path);
            var config = JsonConvert.DeserializeObject<AppConfig>(json)
                ?? throw new InvalidDataException("config.json is empty or malformed.");

            Validate(config);
            return config;
        }

        private static void Validate(AppConfig config)
        {
            var errors = new List<string>();

            // Must have at least one wallet address — either via subAccounts or legacy field
            if (string.IsNullOrWhiteSpace(config.WalletAddress))
                errors.Add("A wallet address is required. Set it in subAccounts (recommended) or walletAddress (legacy).");

            if (!config.WalletAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                errors.Add("walletAddress must start with 0x.");

            // If subAccounts defined, at least one must be active (multiple allowed)
            if (config.SubAccounts.Count > 0)
            {
                var activeCount = config.SubAccounts.Count(s => s.Active);
                if (activeCount == 0)
                    errors.Add("subAccounts: at least one sub-account must have \"active\": true.");
            }

            if (config.BullishThreshold < 1 || config.BullishThreshold > 3)
                errors.Add("bullishThreshold must be 1, 2, or 3.");

            if (config.MaxAssets < 1 || config.MaxAssets > 500)
                errors.Add("maxAssets must be between 1 and 500.");

            if (errors.Count > 0)
                throw new InvalidDataException("config.json validation failed:\n• " + string.Join("\n• ", errors));
        }

        private static void CreateDefault(string path)
        {
            // Build the template as a raw object so the JSON structure is exactly right
            var template = new
            {
                masterAccount = new { walletAddress = "0xYOUR_MASTER_WALLET_ADDRESS" },
                subAccounts = new[]
                {
                    new { name = "HL for Longs",  walletAddress = "0xSUB_ACCOUNT_ADDRESS_1", privateKey = "", active = true  },
                    new { name = "HL for Shorts", walletAddress = "0xSUB_ACCOUNT_ADDRESS_2", privateKey = "", active = false },
                    new { name = "HL for Spot",   walletAddress = "0xSUB_ACCOUNT_ADDRESS_3", privateKey = "", active = false }
                },
                coinglassApiKey  = "",
                defaultTimeframe = "1h",
                maxAssets        = 200,
                bullishThreshold = 2,
                requestDelayMs   = 100,
                hip3Dexes        = new[] { "xyz" },
                portfolioGoalUsd = 1000,
                symbolInfo       = Array.Empty<object>()
            };

            var json = JsonConvert.SerializeObject(template, Formatting.Indented);
            File.WriteAllText(path, json);
        }
    }
}
