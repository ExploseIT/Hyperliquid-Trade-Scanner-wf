using Newtonsoft.Json;

namespace HyperliquidScanner.Models
{
    // ── Meta response (asset list) ────────────────────────────────────────────

    public class MetaResponse
    {
        [JsonProperty("universe")]
        public List<AssetInfo> Universe { get; set; } = new();
    }

    public class AssetInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("szDecimals")]
        public int SizeDecimals { get; set; }

        [JsonProperty("maxLeverage")]
        public int MaxLeverage { get; set; }
    }

    // ── Candle response ───────────────────────────────────────────────────────

    /// <summary>
    /// Raw candle array from Hyperliquid: [t, T, s, i, o, c, h, l, v, n]
    /// t = open time ms, T = close time ms, s = symbol, i = interval,
    /// o = open, c = close, h = high, l = low, v = volume, n = trade count
    /// </summary>
    public class CandleData
    {
        public long OpenTimeMs { get; set; }
        public long CloseTimeMs { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string Interval { get; set; } = string.Empty;
        public decimal Open { get; set; }
        public decimal Close { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Volume { get; set; }
        public int TradeCount { get; set; }

        public DateTime OpenTime => DateTimeOffset.FromUnixTimeMilliseconds(OpenTimeMs).UtcDateTime;
    }

    // ── Scan result (one row in the UI grid) ─────────────────────────────────

    public class AssetScanResult
    {
        public string Asset { get; set; } = string.Empty;
        public decimal LastPrice { get; set; }
        public decimal Rsi { get; set; }
        public bool EmaCrossover { get; set; }
        public bool MacdBullish { get; set; }
        public bool PriceAboveEma { get; set; }
        public int BullishScore { get; set; }   // 0–3
        public bool IsBullish { get; set; }
        public int  BearishScore { get; set; }  // 0–3
        public bool IsBearish    { get; set; }
        public string Timeframe { get; set; } = string.Empty;
        public DateTime ScannedAt { get; set; }

        // Volume / price spike detection
        public decimal VolumeRatio    { get; set; }  // last complete candle vs 20-bar avg
        public bool    VolumeSpike    { get; set; }  // VolumeRatio >= 2.0
        public decimal PriceChangePct { get; set; }  // signed % change of last candle body
        public bool    PriceSurge     { get; set; }  // body > 1.5× ATR(14)
        public bool    IsAbsorption   { get; set; }  // vol spike + small body + RSI falling + CMF negative (reversal up)
        public bool    IsDistribution { get; set; }  // vol spike + small body + RSI rising  + CMF positive (reversal down)
        public bool    IsClimax       { get; set; }  // vol spike + large bearish body + RSI oversold → selling climax (reversal up)
        public decimal RsiSlope       { get; set; }  // RSI change over last 5 bars (negative = falling)
        public decimal Cmf            { get; set; }  // Chaikin Money Flow (negative = selling, positive = buying)
        public decimal RecentTrendPct { get; set; }  // price change over last 10 complete candles (context: falling knife vs sudden flush)

        public bool    IsReversalSetup { get; set; }  // oversold RSI turning up + green candle + beaten-down context
        public bool    IsRsiLowerLow  { get; set; }  // RSI makes lower low below previous valley then turns up — exhaustion reversal

        public bool HasAlert => VolumeSpike || PriceSurge || IsAbsorption || IsDistribution || IsClimax || IsReversalSetup || IsRsiLowerLow;

        public string SignalLabel => IsBullish        ? "✓ Bullish"
                                  : IsRsiLowerLow   ? "📉 RSI-LL"
                                  : IsReversalSetup  ? "↑ Reversal?"
                                  : IsBearish        ? "✗ Bearish"
                                  : "–";
    }

    // ── Timeframe options ─────────────────────────────────────────────────────

    public static class Timeframes
    {
        public static readonly (string Display, string ApiValue)[] All =
        {
            ("15 minutes", "15m"),
            ("1 hour",     "1h"),
            ("4 hours",    "4h"),
            ("1 day",      "1d"),
            ("3 days",     "3d"),
        };

        // How many candles to request per timeframe for reliable indicator calculation
        public static int CandleCount(string apiValue) => apiValue switch
        {
            "15m" => 100,
            "1h"  => 100,
            "4h"  => 100,
            "1d"  => 100,
            "3d"  => 60,
            _     => 100
        };
    }
}
