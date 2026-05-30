using HyperliquidScanner.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Thin wrapper around the Hyperliquid REST API.
    /// All market data endpoints are public — no auth required.
    /// Private endpoints (positions, balances) require a signed request.
    /// </summary>
    public class HyperliquidClient : IDisposable
    {
        private const string BaseUrl = "https://api.hyperliquid.xyz";

        private readonly HttpClient _http;
        private readonly AppConfig  _config;

        public HyperliquidClient(AppConfig config)
        {
            _config = config;
            _http   = new HttpClient { BaseAddress = new Uri(BaseUrl) };
            _http.DefaultRequestHeaders.Add("User-Agent", "HyperliquidScanner/1.0");
        }

        // ── Public endpoints ──────────────────────────────────────────────────

        /// <summary>
        /// Quick connectivity check. Returns (true, assetCount) on success,
        /// or (false, errorMessage) on failure. Safe to call on startup.
        /// </summary>
        public async Task<(bool ok, string message)> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                var assets = await GetAssetsAsync(ct);
                return assets.Count > 0
                    ? (true,  $"Connected — {assets.Count} assets available")
                    : (false, "Connected but received empty asset list");
            }
            catch (Exception ex)
            {
                return (false, $"Connection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns all tradeable perpetual assets from the main HL universe plus any
        /// configured HIP-3 dexes (e.g. "xyz").  HIP-3 assets are prefixed with their
        /// dex name: "xyz:MU", "xyz:XYZ100", etc.
        /// </summary>
        public async Task<List<AssetInfo>> GetAssetsAsync(CancellationToken ct = default)
        {
            var assets = await FetchUniverseAsync(null, ct);

            // Merge in HIP-3 dex assets; failures are non-fatal
            foreach (var dex in _config.Hip3Dexes)
            {
                if (string.IsNullOrWhiteSpace(dex)) continue;
                try
                {
                    var hip3 = await FetchUniverseAsync(dex, ct);
                    assets.AddRange(hip3);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HIP-3] {dex} meta failed: {ex.Message}");
                }
            }

            return assets;
        }

        /// <summary>
        /// Fetches the perpetual universe for the given dex (null = main HL universe).
        /// Names are prefixed with "dex:" for HIP-3 assets (e.g. "xyz:MU").
        /// </summary>
        private async Task<List<AssetInfo>> FetchUniverseAsync(string? dex, CancellationToken ct)
        {
            object payload = dex == null
                ? new { type = "meta" }
                : (object)new { type = "meta", dex };

            var response = await PostInfoAsync(payload, ct);

            // Response shape: [ metaObj, assetCtxsArray ] — ctxs array is parallel to universe.
            JObject? metaObj = null;
            JArray?  ctxArr  = null;

            if (response is JArray outerArr && outerArr.Count > 0)
            {
                metaObj = outerArr[0] as JObject;
                ctxArr  = outerArr.Count > 1 ? outerArr[1] as JArray : null;
            }
            else if (response is JObject obj)
                metaObj = obj;

            if (metaObj == null)
            {
                if (dex != null) return new List<AssetInfo>(); // HIP-3 fetch — fail silently
                throw new InvalidDataException($"Unexpected meta response shape: {response.Type}");
            }

            var universe = metaObj["universe"] as JArray;
            if (universe == null)
            {
                if (dex != null) return new List<AssetInfo>();
                throw new InvalidDataException("Meta response missing 'universe' field.");
            }

            var assets = new List<AssetInfo>();
            for (int i = 0; i < universe.Count; i++)
            {
                var t    = universe[i];
                var name = t["name"]?.Value<string>() ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                // HIP-3 names are prefixed so callers can pass them directly to the candle API
                var fullName = dex != null ? $"{dex}:{name}" : name;

                // Skip delisted assets — open interest is 0 in the parallel assetCtxs array.
                if (ctxArr != null && i < ctxArr.Count)
                {
                    var oiStr = ctxArr[i]["openInterest"]?.Value<string>() ?? "0";
                    if (decimal.TryParse(oiStr, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var oi)
                        && oi == 0m)
                        continue;
                }

                assets.Add(new AssetInfo
                {
                    Name         = fullName,
                    SizeDecimals = t["szDecimals"]?.Value<int>()  ?? 0,
                    MaxLeverage  = t["maxLeverage"]?.Value<int>() ?? 0
                });
            }

            return assets;
        }

        /// <summary>
        /// Returns OHLCV candles for the given asset and interval.
        /// </summary>
        /// <param name="asset">Asset name e.g. "BTC", "ETH"</param>
        /// <param name="interval">API interval string e.g. "1h", "4h"</param>
        /// <param name="count">Number of candles to retrieve</param>
        public async Task<List<CandleData>> GetCandlesAsync(
            string asset, string interval, int count, CancellationToken ct = default)
        {
            var intervalMs = IntervalToMs(interval);
            var endMs      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var startMs    = endMs - intervalMs * count;

            var payload = new
            {
                type = "candleSnapshot",
                req  = new
                {
                    coin      = asset,
                    interval  = interval,
                    startTime = startMs,
                    endTime   = endMs
                }
            };

            var response = await PostInfoAsync(payload, ct);

            var candles = new List<CandleData>();
            if (response is not JArray arr) return candles;

            foreach (var item in arr)
            {
                // Each candle is a JObject with named fields: t, T, s, i, o, c, h, l, v, n
                if (item is not JObject row) continue;

                candles.Add(new CandleData
                {
                    OpenTimeMs  = row["t"]?.Value<long>()             ?? 0,
                    CloseTimeMs = row["T"]?.Value<long>()             ?? 0,
                    Symbol      = row["s"]?.Value<string>()           ?? asset,
                    Interval    = row["i"]?.Value<string>()           ?? interval,
                    Open        = ParseDecimal(row["o"]),
                    Close       = ParseDecimal(row["c"]),
                    High        = ParseDecimal(row["h"]),
                    Low         = ParseDecimal(row["l"]),
                    Volume      = ParseDecimal(row["v"]),
                    TradeCount  = row["n"]?.Value<int>()              ?? 0
                });
            }

            return candles;
        }

        // Candle prices come back as strings e.g. "65000.5" — parse safely
        private static decimal ParseDecimal(JToken? token) =>
            decimal.TryParse(token?.Value<string>(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;

        // ── Private endpoint (requires signing) ───────────────────────────────

        /// <summary>
        /// Returns open perpetual positions for the configured wallet address.
        /// Requires privateKey to be set in config.
        /// </summary>
        public async Task<JToken> GetAccountStateAsync(CancellationToken ct = default)
        {
            if (!_config.HasPrivateKey)
                throw new InvalidOperationException(
                    "privateKey must be set in config.json to access account data.");

            var payload = new
            {
                type = "clearinghouseState",
                user = _config.WalletAddress
            };

            // This endpoint is actually public — Hyperliquid lets you query
            // any wallet's state by address. Signing is only needed for
            // order placement and withdrawals.
            return await PostInfoAsync(payload, ct);
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private async Task<JToken> PostInfoAsync(object payload, CancellationToken ct)
        {
            var json    = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("/info", content, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            return JToken.Parse(body);
        }

        private static long IntervalToMs(string interval) => interval switch
        {
            "15m" => 15  * 60 * 1000L,
            "1h"  => 60  * 60 * 1000L,
            "4h"  => 4   * 60 * 60 * 1000L,
            "1d"  => 24  * 60 * 60 * 1000L,
            "3d"  => 3   * 24 * 60 * 60 * 1000L,
            _     => 60  * 60 * 1000L
        };

        public void Dispose() => _http.Dispose();
    }
}
