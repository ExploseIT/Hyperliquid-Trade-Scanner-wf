using HyperliquidScanner.Models;

namespace HyperliquidScanner.Utils
{
    /// <summary>
    /// Detects swing highs and swing lows from candle data for support/resistance pre-orders.
    ///
    /// A swing high is a candle whose High is greater than the highs of the N candles
    /// either side of it (strength = N, default 2).
    /// A swing low is the mirror: Low less than the lows of the N candles either side.
    ///
    /// Returns the NEAREST level above current price (resistance) or below (support).
    /// </summary>
    public static class SwingDetector
    {
        /// <summary>
        /// Finds the nearest swing high strictly above <paramref name="currentPrice"/>.
        /// Returns null if no qualifying swing high exists in the candle window.
        /// </summary>
        public static decimal? FindNearestResistance(
            List<CandleData> candles, decimal currentPrice, int strength = 2)
        {
            if (candles == null || candles.Count < strength * 2 + 1) return null;

            decimal? nearest = null;

            for (int i = strength; i < candles.Count - strength; i++)
            {
                var high = candles[i].High;
                if (high <= currentPrice) continue; // must be above current price

                bool isSwingHigh = true;
                for (int j = i - strength; j <= i + strength; j++)
                {
                    if (j == i) continue;
                    if (candles[j].High >= high) { isSwingHigh = false; break; }
                }

                if (isSwingHigh)
                {
                    if (nearest == null || high < nearest.Value)
                        nearest = high; // keep the lowest (nearest to current price)
                }
            }

            return nearest;
        }

        /// <summary>
        /// Finds the nearest swing low strictly below <paramref name="currentPrice"/>.
        /// Returns null if no qualifying swing low exists in the candle window.
        /// </summary>
        public static decimal? FindNearestSupport(
            List<CandleData> candles, decimal currentPrice, int strength = 2)
        {
            if (candles == null || candles.Count < strength * 2 + 1) return null;

            decimal? nearest = null;

            for (int i = strength; i < candles.Count - strength; i++)
            {
                var low = candles[i].Low;
                if (low >= currentPrice) continue; // must be below current price

                bool isSwingLow = true;
                for (int j = i - strength; j <= i + strength; j++)
                {
                    if (j == i) continue;
                    if (candles[j].Low <= low) { isSwingLow = false; break; }
                }

                if (isSwingLow)
                {
                    if (nearest == null || low > nearest.Value)
                        nearest = low; // keep the highest (nearest to current price)
                }
            }

            return nearest;
        }
    }
}
