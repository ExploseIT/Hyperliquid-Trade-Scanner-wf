using HyperliquidScanner.Models;
using Skender.Stock.Indicators;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Calculates bullish/bearish trend signals from OHLCV candle data.
    /// Uses three confirming indicators — an asset is bullish when
    /// at least <see cref="BullishThreshold"/> of the three agree.
    ///
    /// Indicators:
    ///   1. EMA crossover  — EMA(9) above EMA(21)
    ///   2. RSI momentum   — RSI(14) above 50
    ///   3. MACD signal    — MACD line above signal line
    /// </summary>
    public class TrendAnalyser
    {
        public int    BullishThreshold      { get; set; } = 2;
        /// <summary>
        /// Minimum % drop from previous RSI valley to new lower low.
        /// Default 0.125 (12.5%). Raise for faster timeframes to reduce noise.
        /// </summary>
        public double RsiLowerLowMinDropPct { get; set; } = 0.125;

        /// <summary>
        /// Number of consecutive green candles required after the RSI lower low.
        /// Default 1. Set to 2 for 15m to filter single-candle fakeouts.
        /// </summary>
        public int RsiLowerLowConfirmCandles { get; set; } = 1;

        public AssetScanResult Analyse(string asset, string timeframe, List<CandleData> candles)
        {
            var result = new AssetScanResult
            {
                Asset     = asset,
                Timeframe = timeframe,
                ScannedAt = DateTime.UtcNow
            };

            if (candles.Count < 30)
            {
                // Not enough data for reliable indicators
                result.BullishScore = 0;
                result.IsBullish    = false;
                return result;
            }

            // Convert to Skender Quote list (sorted ascending by date)
            var quotes = candles
                .OrderBy(c => c.OpenTimeMs)
                .Select(c => new Quote
                {
                    Date   = c.OpenTime,
                    Open   = c.Open,
                    High   = c.High,
                    Low    = c.Low,
                    Close  = c.Close,
                    Volume = c.Volume
                })
                .ToList();

            result.LastPrice = candles.Last().Close;

            // ── Indicator 1: EMA crossover ────────────────────────────────────
            var ema9  = quotes.GetEma(9) .ToList();
            var ema21 = quotes.GetEma(21).ToList();

            var lastEma9  = ema9 .LastOrDefault(e => e.Ema  != null)?.Ema;
            var lastEma21 = ema21.LastOrDefault(e => e.Ema  != null)?.Ema;

            result.EmaCrossover   = lastEma9.HasValue && lastEma21.HasValue
                                 && lastEma9 > lastEma21;
            result.PriceAboveEma  = lastEma21.HasValue
                                 && (double)result.LastPrice > lastEma21.Value;

            // ── Indicator 2: RSI ──────────────────────────────────────────────
            var rsiSeries = quotes.GetRsi(14).ToList();
            var lastRsi   = rsiSeries.LastOrDefault(r => r.Rsi != null)?.Rsi;

            result.Rsi = lastRsi.HasValue ? (decimal)lastRsi.Value : 0m;

            // RSI slope: change over last 5 valid bars (negative = falling)
            var rsiValid = rsiSeries.Where(r => r.Rsi != null).ToList();
            if (rsiValid.Count >= 6)
                result.RsiSlope = (decimal)(rsiValid[^1].Rsi!.Value - rsiValid[^6].Rsi!.Value);

            // ── Indicator 3: MACD ─────────────────────────────────────────────
            var macdSeries = quotes.GetMacd(12, 26, 9).ToList();
            var lastMacd   = macdSeries.LastOrDefault(m => m.Macd != null && m.Signal != null);

            result.MacdBullish = lastMacd != null && lastMacd.Macd > lastMacd.Signal;

            // ── Score & verdict ───────────────────────────────────────────────
            int score = 0;
            if (result.EmaCrossover)  score++;
            if (result.Rsi > 50m)     score++;
            if (result.MacdBullish)   score++;

            result.BullishScore = score;
            result.IsBullish    = score >= BullishThreshold;

            // ── Bearish score (mirror of bullish) ─────────────────────────────
            int bearishScore = 0;
            if (lastEma9.HasValue && lastEma21.HasValue && lastEma9 < lastEma21) bearishScore++;
            if (result.Rsi < 50m)                                                 bearishScore++;
            if (lastMacd != null && lastMacd.Macd < lastMacd.Signal)             bearishScore++;
            result.BearishScore = bearishScore;
            result.IsBearish    = bearishScore >= BullishThreshold;

            // ── Chaikin Money Flow (20): negative = selling pressure ──────────
            var cmfSeries = quotes.GetCmf(20).ToList();
            var lastCmf   = cmfSeries.LastOrDefault(c => c.Cmf != null)?.Cmf ?? 0;
            result.Cmf = (decimal)lastCmf;

            // ── Recent trend: 10-bar price change on last complete candles ────
            // quotes[^1] is still forming; quotes[^2] is last complete candle;
            // quotes[^12] is 10 complete candles ago.
            // Negative = falling knife context; near-zero = sudden flush
            if (quotes.Count >= 12)
            {
                var tenBarsAgoClose = quotes[^12].Close;
                var lastCompleteClose = quotes[^2].Close;
                result.RecentTrendPct = tenBarsAgoClose > 0
                    ? (lastCompleteClose - tenBarsAgoClose) / tenBarsAgoClose * 100m
                    : 0m;
            }

            // ── Volume spike: last complete candle vs 20-bar rolling average ──
            if (quotes.Count >= 22)
            {
                var avgVol = quotes.Skip(quotes.Count - 21).Take(20)
                                   .Average(q => (double)q.Volume);
                var lastVol = (double)quotes[^2].Volume; // [^1] is still forming
                result.VolumeRatio = avgVol > 0 ? (decimal)(lastVol / avgVol) : 0m;
                result.VolumeSpike = result.VolumeRatio >= 2.0m;
            }

            // ── Price surge: last candle body > 1.5× ATR(14) ─────────────────
            var atrSeries = quotes.GetAtr(14).ToList();
            var lastAtr   = atrSeries.LastOrDefault(a => a.Atr != null)?.Atr;
            if (lastAtr.HasValue && lastAtr.Value > 0)
            {
                var last = quotes[^1];
                var body = Math.Abs((double)(last.Close - last.Open));
                result.PriceChangePct = last.Open > 0
                    ? (last.Close - last.Open) / last.Open * 100m
                    : 0m;
                result.PriceSurge = body > 1.5 * lastAtr.Value;

                // Absorption: vol spike + small body + RSI falling + CMF negative
                // — selling pressure being absorbed (Wyckoff stopping volume) → potential reversal up
                // Distribution: vol spike + small body + RSI rising + CMF positive
                // — buying pressure being absorbed at resistance → potential reversal down
                if (result.VolumeSpike && quotes.Count >= 22)
                {
                    var completeCandle = quotes[^2];
                    var completeBody   = Math.Abs((double)(completeCandle.Close - completeCandle.Open));
                    var smallBody      = completeBody < 0.5 * lastAtr.Value;
                    var largeBearBody  = completeCandle.Close < completeCandle.Open
                                     && completeBody > 1.5 * lastAtr.Value;

                    // Absorption: quiet accumulation at a level — selling absorbed, price barely moves
                    var rsiDeclining = result.RsiSlope < -2m;
                    var sellingVol   = result.Cmf < 0m;
                    result.IsAbsorption = smallBody && rsiDeclining && sellingVol;

                    // Distribution: buying absorbed at resistance — price barely rises despite heavy buying
                    var rsiRising = result.RsiSlope > 2m;
                    var buyingVol = result.Cmf > 0m;
                    result.IsDistribution = smallBody && rsiRising && buyingVol;

                    // Selling climax: explosive panic sell candle + oversold RSI + selling vol
                    // — weak hands all exit at once, exhausting sell pressure → sharp reversal up
                    result.IsClimax = largeBearBody && result.Rsi < 40m && sellingVol;
                }
            }

            // ── Reversal setup: oversold but momentum turning ─────────────────
            // Conditions (all must hold):
            //   1. RSI oversold (< 40) — beaten down
            //   2. RSI slope turning positive — momentum shifting up
            //   3. 10-bar trend negative — asset has been falling (context: not just pausing)
            //   4. Last complete candle is green — early price recovery
            // Not flagged if already bullish (it would be redundant).
            if (!result.IsBullish && quotes.Count >= 12)
            {
                var lastComplete = quotes[^2];
                var lastCandleGreen = lastComplete.Close > lastComplete.Open;

                result.IsReversalSetup = result.Rsi < 40m
                                      && result.RsiSlope > -1m
                                      && result.RecentTrendPct < -1.5m
                                      && lastCandleGreen;
            }

            // ── RSI Lower Low: exhaustion reversal signal ─────────────────────
            // Fires when:
            //   1. RSI makes a lower low below the previous RSI valley (exhaustion)
            //   2. RSI is now turning upward (slope positive — momentum shifting)
            //   3. The lower low valley was recent (within last 15 bars)
            //   4. Both valleys are in oversold territory (< 45) for significance
            if (rsiValid.Count >= 20)
            {
                var valleys = FindRsiValleys(rsiValid);
                if (valleys.Count >= 2)
                {
                    var prevValley = valleys[^2];
                    var lastValley = valleys[^1];

                    var barsFromLastValley = rsiValid.Count - 1 - lastValley.index;

                    // % drop from previous valley to new valley — must be at least 12.5%
                    var pctDrop = prevValley.rsi > 0
                        ? (prevValley.rsi - lastValley.rsi) / prevValley.rsi
                        : 0.0;

                    // N consecutive complete candles must be green — configurable via
                    // RsiLowerLowConfirmCandles (default 1, set to 2 for 15m)
                    int confirmCount = Math.Max(1, RsiLowerLowConfirmCandles);
                    bool lastCompleteGreen = quotes.Count >= confirmCount + 1;
                    if (lastCompleteGreen)
                    {
                        for (int ci = 1; ci <= confirmCount; ci++)
                        {
                            if (quotes[^(ci + 1)].Close <= quotes[^(ci + 1)].Open)
                            { lastCompleteGreen = false; break; }
                        }
                    }

                    result.IsRsiLowerLow =
                        pctDrop >= RsiLowerLowMinDropPct         // configurable min drop (default 12.5%)
                        && lastValley.rsi < 45.0                 // lower low in oversold territory
                        && prevValley.rsi < 45.0                 // previous valley also oversold
                        && barsFromLastValley <= 5               // valley is very recent (within 5 bars)
                        && result.Rsi < 45m                      // current RSI still low (not already recovered)
                        && result.Rsi - (decimal)lastValley.rsi <= 8m  // still close to valley (not recovered >8pts)
                        && result.RsiSlope > 1m                  // clearly turning up, not just noise
                        && lastCompleteGreen;                    // last candle green — price recovering
                }
            }

            return result;
        }

        /// <summary>
        /// Finds RSI local minima (valleys) from a series of valid RSI values.
        /// Uses a 3-bar window on each side and enforces a minimum separation
        /// between valleys to avoid detecting noise as separate signals.
        /// </summary>
        private static List<(int index, double rsi)> FindRsiValleys(
            IList<Skender.Stock.Indicators.RsiResult> rsiValid)
        {
            var valleys      = new List<(int index, double rsi)>();
            const int window = 3;   // bars each side to confirm a valley
            const int minSep = 5;   // minimum bars between valleys

            for (int i = window; i < rsiValid.Count - window; i++)
            {
                var curr = rsiValid[i].Rsi!.Value;
                bool isValley = true;
                for (int j = 1; j <= window; j++)
                {
                    if (rsiValid[i - j].Rsi!.Value <= curr ||
                        rsiValid[i + j].Rsi!.Value <= curr)
                    { isValley = false; break; }
                }
                if (!isValley) continue;

                // Enforce minimum separation — keep the lower of two close valleys
                if (valleys.Count > 0 && i - valleys[^1].index < minSep)
                {
                    if (curr < valleys[^1].rsi)
                        valleys[^1] = (i, curr);
                }
                else
                {
                    valleys.Add((i, curr));
                }
            }

            return valleys;
        }
    }
}
