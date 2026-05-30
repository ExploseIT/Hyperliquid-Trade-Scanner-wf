using HyperliquidScanner.Models;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Orchestrates a full scan: fetches all assets, pulls candles for each,
    /// runs the trend analyser, and reports progress back to the UI.
    /// </summary>
    public class ScannerService
    {
        private readonly HyperliquidClient _client;
        private readonly TrendAnalyser     _analyser;
        private readonly AppConfig         _config;

        public ScannerService(HyperliquidClient client, AppConfig config)
        {
            _client   = client;
            _config   = config;
            _analyser = new TrendAnalyser { BullishThreshold = config.BullishThreshold };
        }

        /// <summary>
        /// Runs a full market scan.
        /// </summary>
        /// <param name="timeframe">API interval string e.g. "1h"</param>
        /// <param name="progress">Reports (currentIndex, totalAssets, assetName)</param>
        /// <param name="ct">Cancellation token — allows the UI to cancel mid-scan</param>
        public async Task<List<AssetScanResult>> ScanAsync(
            string                                    timeframe,
            IProgress<(int current, int total, string asset)>? progress = null,
            CancellationToken                         ct = default)
        {
            // Step 1: get all assets
            // Apply MaxAssets only to the main HL universe (no ':' in name).
            // HIP-3 dex assets (e.g. "xyz:MU") are always included in full —
            // they are few in number and the user specifically configured them.
            var assets    = await _client.GetAssetsAsync(ct);
            var mainAssets = assets.Where(a => !a.Name.Contains(':')).Take(_config.MaxAssets).ToList();
            var hip3Assets = assets.Where(a =>  a.Name.Contains(':')).ToList();
            var limited    = mainAssets.Concat(hip3Assets).ToList();

            var results      = new List<AssetScanResult>();
            var candleCount  = Timeframes.CandleCount(timeframe);

            for (int i = 0; i < limited.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var asset = limited[i];
                progress?.Report((i + 1, limited.Count, asset.Name));

                try
                {
                    var candles = await _client.GetCandlesAsync(asset.Name, timeframe, candleCount, ct);

                    if (candles.Count > 0 && IsDataFresh(candles[^1].CloseTimeMs, timeframe))
                    {
                        var result = _analyser.Analyse(asset.Name, timeframe, candles);
                        results.Add(result);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Log and continue — one failed asset shouldn't stop the scan
                    results.Add(new AssetScanResult
                    {
                        Asset     = asset.Name,
                        Timeframe = timeframe,
                        ScannedAt = DateTime.UtcNow,
                        IsBullish = false,
                        BullishScore = -1  // sentinel: indicates error
                    });
                    System.Diagnostics.Debug.WriteLine($"[Scanner] {asset.Name} failed: {ex.Message}");
                }

                // Throttle to avoid rate limiting
                if (_config.RequestDelayMs > 0 && i < limited.Count - 1)
                    await Task.Delay(_config.RequestDelayMs, ct);
            }

            return results;
        }
        // Returns false if the newest candle is stale — asset is delisted or dormant.
        private static bool IsDataFresh(long latestCloseMs, string timeframe)
        {
            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - latestCloseMs;
            var intervalMs = timeframe switch
            {
                "15m" => 15  * 60 * 1000L,
                "1h"  => 60  * 60 * 1000L,
                "4h"  => 4   * 60 * 60 * 1000L,
                "1d"  => 24  * 60 * 60 * 1000L,
                "3d"  => 3   * 24 * 60 * 60 * 1000L,
                _     => 60  * 60 * 1000L
            };
            return ageMs <= intervalMs * 3;
        }
    }
}
