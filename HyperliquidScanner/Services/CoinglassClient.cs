using HyperliquidScanner.Models;
using Newtonsoft.Json.Linq;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Client for CoinGlass API v4.
    /// Base URL : https://open-api-v4.coinglass.com
    /// Auth     : CG-API-KEY header
    /// Rate     : 30 req/min on Hobbyist plan — we call on-demand (click), not on a timer.
    /// </summary>
    public class CoinglassClient : IDisposable
    {
        private const string BaseUrl = "https://open-api-v4.coinglass.com";

        private readonly HttpClient _http;
        private readonly AppConfig  _config;

        public CoinglassClient(AppConfig config)
        {
            _config = config;
            _http   = new HttpClient { BaseAddress = new Uri(BaseUrl) };
            _http.DefaultRequestHeaders.Add("CG-API-KEY",  config.CoinglassApiKey);
            _http.DefaultRequestHeaders.Add("Accept",      "application/json");
            _http.DefaultRequestHeaders.Add("User-Agent",  "LiquidScanner/1.0");
        }

        // ── Snapshot: long/short totals for one coin (1h, 4h, 24h windows) ──
        // Uses /api/futures/liquidation/coin-list scoped to "all" exchanges,
        // then filters for the requested symbol.
        // One call returns all coins — we cache the result for 60 s.

        private List<CoinglassLiquidationSnapshot>? _snapshotCache;
        private DateTime _snapshotCachedAt = DateTime.MinValue;
        private static readonly TimeSpan SnapshotCacheTtl = TimeSpan.FromSeconds(60);

        public async Task<CoinglassLiquidationSnapshot?> GetSnapshotAsync(
            string symbol, CancellationToken ct = default)
        {
            if (_snapshotCache == null || DateTime.UtcNow - _snapshotCachedAt > SnapshotCacheTtl)
            {
                _snapshotCache    = await FetchAllSnapshotsAsync(ct);
                _snapshotCachedAt = DateTime.UtcNow;
            }

            // Coinglass may return "WLDUSDT" while Hyperliquid sends "WLD" — normalise both sides.
            // Hyperliquid k-prefix (kSHIB = 1000 SHIB) — try stripping k prefix if no direct match.
            var needle = StripQuote(symbol);
            var result = _snapshotCache.FirstOrDefault(
                s => StripQuote(s.Symbol).Equals(needle, StringComparison.OrdinalIgnoreCase));

            // Fallback: strip leading 'k' for Hyperliquid 1000× tokens (kSHIB → SHIB)
            if (result == null && needle.StartsWith("k", StringComparison.OrdinalIgnoreCase) && needle.Length > 1)
            {
                var needleNoK = needle[1..];
                result = _snapshotCache.FirstOrDefault(
                    s => StripQuote(s.Symbol).Equals(needleNoK, StringComparison.OrdinalIgnoreCase));
            }

            return result;
        }

        private static string StripQuote(string s)
        {
            // Handle hyphenated pairs first: "WLD-USDC" → "WLD"
            var dash = s.IndexOf('-');
            if (dash > 0) s = s[..dash];

            foreach (var suffix in new[] { "USDT", "USDC", "BUSD", "USD", "PERP" })
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return s[..^suffix.Length];
            return s;
        }

        private static readonly string[] _snapshotExchanges = { "Binance", "Bybit", "OKX", "Hyperliquid" };

        private async Task<List<CoinglassLiquidationSnapshot>> FetchAllSnapshotsAsync(
            CancellationToken ct)
        {
            // Fetch all three exchanges concurrently then sum per symbol,
            // matching the aggregated view shown on the Coinglass heatmap.
            var tasks = _snapshotExchanges
                .Select(ex => FetchExchangeSnapshotsAsync(ex, ct))
                .ToArray();

            await Task.WhenAll(tasks);

            var merged = new Dictionary<string, CoinglassLiquidationSnapshot>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var task in tasks)
            {
                foreach (var snap in task.Result)
                {
                    if (!merged.TryGetValue(snap.Symbol, out var existing))
                    {
                        existing = new CoinglassLiquidationSnapshot { Symbol = snap.Symbol };
                        merged[snap.Symbol] = existing;
                    }
                    existing.LongLiq1h   += snap.LongLiq1h;
                    existing.ShortLiq1h  += snap.ShortLiq1h;
                    existing.LongLiq4h   += snap.LongLiq4h;
                    existing.ShortLiq4h  += snap.ShortLiq4h;
                    existing.LongLiq24h  += snap.LongLiq24h;
                    existing.ShortLiq24h += snap.ShortLiq24h;
                }
            }

            return merged.Values.ToList();
        }

        private async Task<List<CoinglassLiquidationSnapshot>> FetchExchangeSnapshotsAsync(
            string exchange, CancellationToken ct)
        {
            try
            {
                var response = await GetAsync(
                    $"/api/futures/liquidation/coin-list?exchange={exchange}", ct);

                var data = response["data"] as JArray ?? new JArray();
                var list = new List<CoinglassLiquidationSnapshot>();
                foreach (var item in data)
                {
                    var snap = new CoinglassLiquidationSnapshot
                    {
                        Symbol      = item["symbol"]?.Value<string>() ?? string.Empty,
                        LongLiq1h   = ParseDecimal(item["long_liquidation_usd_1h"]),
                        ShortLiq1h  = ParseDecimal(item["short_liquidation_usd_1h"]),
                        LongLiq4h   = ParseDecimal(item["long_liquidation_usd_4h"]),
                        ShortLiq4h  = ParseDecimal(item["short_liquidation_usd_4h"]),
                        LongLiq24h  = ParseDecimal(item["long_liquidation_usd_24h"]),
                        ShortLiq24h = ParseDecimal(item["short_liquidation_usd_24h"]),
                    };
                    if (!string.IsNullOrEmpty(snap.Symbol))
                        list.Add(snap);
                }
                return list;
            }
            catch
            {
                return new List<CoinglassLiquidationSnapshot>();
            }
        }

        // ── History ───────────────────────────────────────────────────────────
        // NOTE: /api/futures/liquidation/aggregated-history returns code=0 data:[]
        // on the Hobbyist plan — the endpoint is silently gated to higher tiers.
        // Method kept for future use if the plan is upgraded.
        public Task<List<LiquidationBar>> GetHistoryAsync(
            string symbol, string interval = "4h", int limit = 24,
            CancellationToken ct = default)
            => Task.FromResult(new List<LiquidationBar>());

        // ── Connection test ───────────────────────────────────────────────────

        public async Task<(bool ok, string message)> TestConnectionAsync(
            CancellationToken ct = default)
        {
            try
            {
                var response = await GetAsync(
                    "/api/futures/liquidation/coin-list?exchange=Binance", ct);

                var code = response["code"]?.Value<int>() ?? -1;
                return code == 0
                    ? (true,  "Coinglass connected")
                    : (false, $"Coinglass error {code}: {response["msg"]}");
            }
            catch (Exception ex)
            {
                return (false, $"Coinglass failed: {ex.Message}");
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private async Task<JObject> GetAsync(string path, CancellationToken ct)
        {
            var response = await _http.GetAsync(path, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            return JObject.Parse(body);
        }

        private static decimal ParseDecimal(JToken? token)
        {
            if (token == null) return 0m;
            var s = token.Value<string>();
            if (string.IsNullOrEmpty(s)) return token.Value<decimal>();
            return decimal.TryParse(s,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
        }

        public void Dispose() => _http.Dispose();
    }
}
