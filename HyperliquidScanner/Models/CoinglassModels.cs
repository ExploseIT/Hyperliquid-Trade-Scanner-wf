using Newtonsoft.Json;

namespace HyperliquidScanner.Models
{
    // Config extension
    public partial class AppConfig
    {
        [JsonProperty("coinglassApiKey")]
        public string CoinglassApiKey { get; set; } = string.Empty;

        public bool HasCoinglassKey => !string.IsNullOrWhiteSpace(CoinglassApiKey);
    }

    // Coin liquidation list (snapshot — long/short totals across windows)
    public class CoinglassLiquidationSnapshot
    {
        [JsonProperty("symbol")]
        public string Symbol { get; set; } = string.Empty;

        // 1h
        [JsonProperty("longLiquidationUsd1h")]
        public decimal LongLiq1h { get; set; }

        [JsonProperty("shortLiquidationUsd1h")]
        public decimal ShortLiq1h { get; set; }

        // 4h
        [JsonProperty("longLiquidationUsd4h")]
        public decimal LongLiq4h { get; set; }

        [JsonProperty("shortLiquidationUsd4h")]
        public decimal ShortLiq4h { get; set; }

        // 24h
        [JsonProperty("longLiquidationUsd24h")]
        public decimal LongLiq24h { get; set; }

        [JsonProperty("shortLiquidationUsd24h")]
        public decimal ShortLiq24h { get; set; }

        // Derived helpers
        public decimal TotalLiq1h  => LongLiq1h  + ShortLiq1h;
        public decimal TotalLiq4h  => LongLiq4h  + ShortLiq4h;
        public decimal TotalLiq24h => LongLiq24h + ShortLiq24h;

        public decimal ShortBias1h  => TotalLiq1h  > 0 ? ShortLiq1h  / TotalLiq1h  : 0.5m;
        public decimal ShortBias4h  => TotalLiq4h  > 0 ? ShortLiq4h  / TotalLiq4h  : 0.5m;
        public decimal ShortBias24h => TotalLiq24h > 0 ? ShortLiq24h / TotalLiq24h : 0.5m;

        public string BiasLabel(decimal shortBias) => shortBias switch
        {
            > 0.65m => "Short squeeze",
            > 0.55m => "Short heavy",
            > 0.45m => "Balanced",
            > 0.35m => "Long heavy",
            _       => "Long squeeze"
        };

        public string Bias1hLabel  => BiasLabel(ShortBias1h);
        public string Bias4hLabel  => BiasLabel(ShortBias4h);
        public string Bias24hLabel => BiasLabel(ShortBias24h);
    }

    // Aggregated history bar (for the chart)
    public class LiquidationBar
    {
        public DateTime Time      { get; set; }
        public decimal  LongUsd   { get; set; }
        public decimal  ShortUsd  { get; set; }
        public decimal  TotalUsd  => LongUsd + ShortUsd;
        public decimal  ShortBias => TotalUsd > 0 ? ShortUsd / TotalUsd : 0.5m;
    }
}
