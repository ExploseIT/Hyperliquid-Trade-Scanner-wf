using HyperliquidScanner.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Serilog;

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

        // Asset index cache: symbol → (universe index, size decimals)
        // Populated by InitialiseAssetIndexesAsync() on startup.
        private Dictionary<string, (int index, int szDecimals)> _assetIndexes
            = new(StringComparer.OrdinalIgnoreCase);

        public HyperliquidClient(AppConfig config)
        {
            _config = config;
            _http   = new HttpClient { BaseAddress = new Uri(BaseUrl) };
            _http.DefaultRequestHeaders.Add("User-Agent", "HyperliquidScanner/1.0");
        }

        /// <summary>
        /// Fetches the main HL asset universe and caches symbol → index mappings.
        /// Must be called once on startup before any order placement.
        /// </summary>
        public async Task InitialiseAssetIndexesAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await PostInfoAsync(new { type = "meta" }, ct);
                var metaObj  = response is JArray arr ? arr[0] as JObject : response as JObject;
                var universe = metaObj?["universe"] as JArray;
                if (universe == null) return;

                var indexes = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < universe.Count; i++)
                {
                    var name   = universe[i]["name"]?.Value<string>() ?? string.Empty;
                    var szDec  = universe[i]["szDecimals"]?.Value<int>() ?? 0;
                    if (!string.IsNullOrEmpty(name))
                        indexes[name] = (i, szDec);
                }
                _assetIndexes = indexes;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AssetIndex] init failed: {ex.Message}");
            }
        }

        /// <summary>Returns the asset index and size decimals for a symbol, or null if not found.</summary>
        public (int index, int szDecimals)? GetAssetIndex(string symbol) =>
            _assetIndexes.TryGetValue(symbol, out var v) ? v : null;

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

                // HIP-3 names are prefixed so callers can pass them directly to the candle API.
                // If the API already returns a prefixed name (e.g. "xyz:MU"), don't double-prefix.
                var fullName = dex != null && !name.Contains(':')
                    ? $"{dex}:{name}"
                    : name;

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
        /// Returns the current mid price for every asset in one request.
        /// Keys are asset names as returned by the exchange (e.g. "BTC", "ETH", "xyz:MU").
        /// Returns an empty dictionary on failure — callers fall back to candle close price.
        /// </summary>
        public async Task<Dictionary<string, decimal>> GetAllMidsAsync(CancellationToken ct = default)
        {
            var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var response = await PostInfoAsync(new { type = "allMids" }, ct);
                if (response is JObject obj)
                {
                    foreach (var prop in obj.Properties())
                    {
                        if (decimal.TryParse(prop.Value?.Value<string>(),
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var price))
                            result[prop.Name] = price;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[allMids] failed: {ex.Message}");
            }
            return result;
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

            // For HIP-3 assets (e.g. "xyz:MU"), try the full prefixed name first,
            // then fall back to just the base symbol ("MU") if the API returns nothing.
            var coinNamesToTry = asset.Contains(':')
                ? new[] { asset, asset.Substring(asset.IndexOf(':') + 1) }
                : new[] { asset };

            var candles = new List<CandleData>();

            foreach (var coin in coinNamesToTry)
            {
                var payload = new
                {
                    type = "candleSnapshot",
                    req  = new
                    {
                        coin      = coin,
                        interval  = interval,
                        startTime = startMs,
                        endTime   = endMs
                    }
                };

                var response = await PostInfoAsync(payload, ct);
                if (response is not JArray arr || arr.Count == 0) continue;

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

                if (candles.Count > 0) break; // got data — no need to try fallback name
            }

            return candles;
        }

        // Candle prices come back as strings e.g. "65000.5" — parse safely
        private static decimal ParseDecimal(JToken? token) =>
            decimal.TryParse(token?.Value<string>(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;

        // ── Position data ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns all open perpetual positions for the configured wallet address.
        /// Uses the public clearinghouseState endpoint — wallet address only, no signing needed.
        /// Returns empty list if wallet address is not configured or on any error.
        /// </summary>
        public async Task<List<PositionInfo>> GetPositionsAsync(CancellationToken ct = default)
        {
            var positions = new List<PositionInfo>();
            if (string.IsNullOrWhiteSpace(_config.WalletAddress)) return positions;

            // Fetch main HL positions + each configured HIP-3 dex separately
            var dexesToFetch = new List<string?> { null }; // null = main HL universe
            dexesToFetch.AddRange(_config.Hip3Dexes.Where(d => !string.IsNullOrWhiteSpace(d)));

            foreach (var dex in dexesToFetch)
            {
                try
                {
                    object payload = dex == null
                        ? new { type = "clearinghouseState", user = _config.WalletAddress }
                        : (object)new { type = "clearinghouseState", user = _config.WalletAddress, dex };

                    var response       = await PostInfoAsync(payload, ct);
                    var assetPositions = response["assetPositions"] as JArray;
                    if (assetPositions == null) continue;

                    foreach (var item in assetPositions)
                    {
                        var pos = item["position"];
                        if (pos == null) continue;

                        var sziStr = pos["szi"]?.Value<string>() ?? "0";
                        if (!decimal.TryParse(sziStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var szi)
                            || szi == 0m) continue;

                        var entryPx   = ParseDecimal(pos["entryPx"]);
                        var posVal    = ParseDecimal(pos["positionValue"]);
                        var pnl       = ParseDecimal(pos["unrealizedPnl"]);
                        var marginUsd = ParseDecimal(pos["marginUsed"]);
                        var absSize   = Math.Abs(szi);
                        var markPx    = absSize > 0 ? posVal / absSize : 0m;
                        // ROE: PnL as % of margin (matches Hyperliquid UI convention)
                        var pnlPct    = marginUsd > 0 ? pnl / marginUsd : 0m;

                        var levObj  = pos["leverage"];
                        var levVal  = levObj?["value"]?.Value<int>() ?? 1;
                        var levType = levObj?["type"]?.Value<string>() ?? "cross";

                        decimal? liqPx = null;
                        var liqStr = pos["liquidationPx"]?.Value<string>();
                        if (!string.IsNullOrEmpty(liqStr) && liqStr != "null" &&
                            decimal.TryParse(liqStr, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var liq))
                            liqPx = liq;

                        // Prefix HIP-3 asset names so they match the scanner format (e.g. "AMD" → "xyz:AMD")
                        var coinName = pos["coin"]?.Value<string>() ?? string.Empty;
                        if (dex != null && !coinName.Contains(':'))
                            coinName = $"{dex}:{coinName}";

                        positions.Add(new PositionInfo
                        {
                            Symbol           = coinName,
                            IsLong           = szi > 0,
                            Size             = absSize,
                            EntryPrice       = entryPx,
                            MarkPrice        = markPx,
                            UnrealisedPnl    = pnl,
                            PnlPercent       = pnlPct,
                            LiquidationPrice = liqPx,
                            Leverage         = levVal,
                            LeverageType     = levType,
                            MarginUsed       = marginUsd
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Positions] {dex ?? "main"} fetch failed: {ex.Message}");
                }
            }

            return positions;
        }

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

        // ── Order placement (requires private key) ────────────────────────────

        /// <summary>
        /// Places a reduce-only limit order to close (part of) a position.
        /// For a long close: isBuy=false, price slightly below mark.
        /// For a short close: isBuy=true, price slightly above mark.
        /// Returns the response JToken or null on failure.
        /// </summary>
        public async Task<(bool ok, string message)> PlaceLimitCloseAsync(
            string symbol, bool isBuy, decimal price, decimal size,
            int szDecimals, CancellationToken ct = default)
        {
            var (ok, msg, _) = await PlaceOrderAsync(symbol, isBuy, price, size, szDecimals, "Gtc", ct);
            return (ok, msg);
        }

        /// <summary>Places a limit entry order and returns the exchange order ID on success.</summary>
        public async Task<(bool ok, string message, long orderId)> PlaceLimitEntryAsync(
            string symbol, bool isBuy, decimal price, decimal size,
            int szDecimals, CancellationToken ct = default)
        {
            return await PlaceOrderAsync(symbol, isBuy, price, size, szDecimals, "Gtc", ct);
        }

        /// <summary>
        /// Places a reduce-only IOC (Immediate-or-Cancel) order — effectively a market close.
        /// Uses an aggressive price (5% through the market) to ensure fill.
        /// </summary>
        public async Task<(bool ok, string message)> PlaceMarketCloseAsync(
            string symbol, bool isBuy, decimal markPrice, decimal size,
            int szDecimals, CancellationToken ct = default)
        {
            var aggressivePrice = isBuy ? markPrice * 1.05m : markPrice * 0.95m;
            var (ok, msg, _)    = await PlaceOrderAsync(symbol, isBuy, aggressivePrice, size, szDecimals, "Ioc", ct);
            return (ok, msg);
        }

        private async Task<(bool ok, string message, long orderId)> PlaceOrderAsync(
            string symbol, bool isBuy, decimal price, decimal size,
            int szDecimals, string tif, CancellationToken ct)
        {
            if (!_config.HasPrivateKey)
                return (false, "No private key configured — cannot place orders.", 0);

            var assetInfo = GetAssetIndex(symbol);
            if (assetInfo == null)
                return (false, $"Asset index not found for {symbol} — run InitialiseAssetIndexesAsync first.", 0);

            var (assetIndex, _) = assetInfo.Value;
            var nonce           = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Format price and size to appropriate precision
            var priceStr = FormatDecimal(price, 5);  // HL uses up to 5 sig figs for price
            var sizeStr  = size.ToString($"F{szDecimals}",
                              System.Globalization.CultureInfo.InvariantCulture);

            try
            {
                // Serialise action to msgpack
                var actionBytes = HyperliquidSigner.SerializeLimitOrder(
                    assetIndex, isBuy, priceStr, sizeStr, reduceOnly: true, tif);

                // Sign
                var (r, s, v) = HyperliquidSigner.Sign(actionBytes, nonce, _config.PrivateKey);

                // Build request
                var action = new
                {
                    type     = "order",
                    orders   = new[]
                    {
                        new
                        {
                            a = assetIndex,
                            b = isBuy,
                            p = priceStr,
                            s = sizeStr,
                            r = true,  // reduceOnly
                            t = new { limit = new { tif } }
                        }
                    },
                    grouping = "na"
                };

                var body = new
                {
                    action,
                    nonce,
                    signature = new { r, s, v }
                };

                var json     = JsonConvert.SerializeObject(body);
                var content  = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _http.PostAsync("/exchange", content, ct);
                var respBody = await response.Content.ReadAsStringAsync(ct);

                var parsed = JToken.Parse(respBody);
                var status = parsed["status"]?.Value<string>();

                if (status == "ok")
                {
                    var oid = parsed["response"]?["data"]?["statuses"]?[0]?["resting"]?["oid"]
                              ?.Value<long>() ?? 0;
                    return (true, $"Order placed: {(isBuy ? "Buy" : "Sell")} {sizeStr} {symbol} @ {priceStr}", oid);
                }

                var error = parsed["response"]?["data"]?.ToString() ?? respBody;
                return (false, $"Order rejected: {error}", 0);
            }
            catch (Exception ex)
            {
                return (false, $"Order error: {ex.Message}", 0);
            }
        }

        private static string FormatDecimal(decimal value, int sigFigs)
        {
            if (value == 0) return "0";
            var magnitude = (int)Math.Floor(Math.Log10((double)Math.Abs(value)));
            var decimals  = Math.Max(0, sigFigs - 1 - magnitude);
            return value.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns all open orders for the configured wallet address.
        /// Used to check if an entry order has filled or is still resting.
        /// </summary>
        public async Task<List<(long oid, int assetIndex, bool isBuy, decimal price, decimal size)>>
            GetOpenOrdersAsync(CancellationToken ct = default)
        {
            var result = new List<(long, int, bool, decimal, decimal)>();
            if (string.IsNullOrWhiteSpace(_config.WalletAddress)) return result;
            try
            {
                var payload  = new { type = "openOrders", user = _config.WalletAddress };
                var response = await PostInfoAsync(payload, ct);
                if (response is not JArray arr) return result;
                foreach (var o in arr)
                {
                    var oid   = o["oid"]?.Value<long>() ?? 0;
                    var coin  = o["coin"]?.Value<string>() ?? "";
                    var side  = o["side"]?.Value<string>() ?? "";
                    var px    = ParseDecimal(o["limitPx"]);
                    var sz    = ParseDecimal(o["sz"]);
                    if (_assetIndexes.TryGetValue(coin, out var info))
                        result.Add((oid, info.index, side == "B", px, sz));
                }
            }
            catch (Exception ex) { Log.Warning("GetOpenOrders failed: {Msg}", ex.Message); }
            return result;
        }

        /// <summary>
        /// Cancels a specific order by asset index and order ID.
        /// Returns (true, msg) on success.
        /// </summary>
        public async Task<(bool ok, string message)> CancelOrderAsync(
            int assetIndex, long orderId, CancellationToken ct = default)
        {
            if (!_config.HasPrivateKey) return (false, "No private key configured.");
            var nonce       = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var actionBytes = HyperliquidSigner.SerializeCancelOrder(assetIndex, orderId);
            var (r, s, v)   = HyperliquidSigner.Sign(actionBytes, nonce, _config.PrivateKey);
            var body = new
            {
                action    = new { type = "cancel", cancels = new[] { new { a = assetIndex, o = orderId } } },
                nonce,
                signature = new { r, s, v }
            };
            try
            {
                var json     = Newtonsoft.Json.JsonConvert.SerializeObject(body);
                var content  = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _http.PostAsync("/exchange", content, ct);
                var respBody = await response.Content.ReadAsStringAsync(ct);
                var parsed   = JToken.Parse(respBody);
                return parsed["status"]?.Value<string>() == "ok"
                    ? (true, "Cancelled")
                    : (false, parsed.ToString());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>
        /// Places a native trigger order (TP or SL) that persists on the exchange.
        /// tpsl: "tp" or "sl". isMarket: true = market fill when triggered.
        /// Returns (true, orderId) on success.
        /// </summary>
        public async Task<(bool ok, string message, long orderId)> PlaceTriggerOrderAsync(
            string symbol, bool isBuy, decimal price, decimal triggerPrice,
            decimal size, int szDecimals, string tpsl, bool isMarket,
            CancellationToken ct = default)
        {
            if (!_config.HasPrivateKey) return (false, "No private key.", 0);
            var assetInfo = GetAssetIndex(symbol);
            if (assetInfo == null) return (false, $"Asset index unknown for {symbol}", 0);

            var (assetIndex, _) = assetInfo.Value;
            var nonce     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var priceStr  = FormatDecimal(price, 5);
            var trigStr   = FormatDecimal(triggerPrice, 5);
            var sizeStr   = size.ToString($"F{szDecimals}",
                System.Globalization.CultureInfo.InvariantCulture);

            var actionBytes = HyperliquidSigner.SerializeTriggerOrder(
                assetIndex, isBuy, priceStr, trigStr, sizeStr, true, tpsl, isMarket);
            var (r, s, v) = HyperliquidSigner.Sign(actionBytes, nonce, _config.PrivateKey);

            var body = new
            {
                action = new
                {
                    type   = "order",
                    orders = new[]
                    {
                        new
                        {
                            a = assetIndex, b = isBuy, p = priceStr, s = sizeStr, r = true,
                            t = new { trigger = new { triggerPx = trigStr, isMarket, tpsl } }
                        }
                    },
                    grouping = "na"
                },
                nonce,
                signature = new { r, s, v }
            };
            try
            {
                var json     = Newtonsoft.Json.JsonConvert.SerializeObject(body);
                var content  = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _http.PostAsync("/exchange", content, ct);
                var respBody = await response.Content.ReadAsStringAsync(ct);
                var parsed   = JToken.Parse(respBody);
                if (parsed["status"]?.Value<string>() == "ok")
                {
                    var oid = parsed["response"]?["data"]?["statuses"]?[0]?["resting"]?["oid"]
                              ?.Value<long>() ?? 0;
                    return (true, $"Trigger {tpsl} placed", oid);
                }
                return (false, parsed.ToString(), 0);
            }
            catch (Exception ex) { return (false, ex.Message, 0); }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private async Task<JToken> PostInfoAsync(object payload, CancellationToken ct)
        {
            var json = JsonConvert.SerializeObject(payload);

            // Retry up to 3 times on 429 Too Many Requests with exponential backoff
            int attempt = 0;
            while (true)
            {
                attempt++;
                var content  = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _http.PostAsync("/info", content, ct);

                if ((int)response.StatusCode == 429 && attempt <= 3)
                {
                    var delay = attempt * 2_000; // 2s, 4s, 6s
                    Log.Warning("429 rate limit on attempt {Attempt}/3 — waiting {Delay}ms", attempt, delay);
                    await Task.Delay(delay, ct);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct);
                return JToken.Parse(body);
            }
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
