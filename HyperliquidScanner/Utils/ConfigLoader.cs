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

            if (string.IsNullOrWhiteSpace(config.WalletAddress))
                errors.Add("walletAddress is required.");

            if (!config.WalletAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                errors.Add("walletAddress must start with 0x.");

            if (config.BullishThreshold < 1 || config.BullishThreshold > 3)
                errors.Add("bullishThreshold must be 1, 2, or 3.");

            if (config.MaxAssets < 1 || config.MaxAssets > 500)
                errors.Add("maxAssets must be between 1 and 500.");

            if (errors.Count > 0)
                throw new InvalidDataException("config.json validation failed:\n• " + string.Join("\n• ", errors));
        }

        private static void CreateDefault(string path)
        {
            var template = new AppConfig
            {
                WalletAddress    = "0xYOUR_WALLET_ADDRESS",
                PrivateKey       = "",
                DefaultTimeframe = "1h",
                MaxAssets        = 200,
                BullishThreshold = 2,
                RequestDelayMs   = 100
            };

            var json = JsonConvert.SerializeObject(template, Formatting.Indented);
            File.WriteAllText(path, json);
        }
    }
}
