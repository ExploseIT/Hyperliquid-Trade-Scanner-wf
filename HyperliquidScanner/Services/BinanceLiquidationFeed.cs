using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using HyperliquidScanner.Models;
using Newtonsoft.Json.Linq;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Connects to the OKX public liquidation-orders WebSocket channel.
    /// No API key required — free, public endpoint.
    ///
    /// URL     : wss://ws.okx.com:8443/ws/v5/public
    /// Channel : liquidation-orders, instType=SWAP  (all USDT-perp symbols)
    /// Heartbeat: "ping" text frame every 25 s (OKX disconnects after 30 s idle)
    ///
    /// OKX side semantics:
    ///   details[].side = "sell" → long  position liquidated (maps to SELL)
    ///   details[].side = "buy"  → short position liquidated (maps to BUY)
    ///
    /// Class is named BinanceLiquidationFeed to avoid changing call-sites.
    /// </summary>
    public class BinanceLiquidationFeed : IDisposable
    {
        private const string  WsUrl       = "wss://ws.okx.com:8443/ws/v5/public";
        private const decimal MinUsdValue = 1_500m;   // catch small altcoin events too

        public event Action<BinanceLiquidationEvent>? OnLiquidation;
        public event Action<bool>?                    ConnectionChanged;
        public bool IsConnected { get; private set; }

        private readonly CancellationTokenSource _cts = new();

        // CoinglassClient param kept for API compatibility — not used here
        public BinanceLiquidationFeed(Services.CoinglassClient? _ = null) { }

        public void Start() => Task.Run(() => RunAsync(_cts.Token));

        // ── Main loop ─────────────────────────────────────────────────────────

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndReadAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch { /* connection dropped — fall through to reconnect */ }

                SetConnected(false);

                if (!ct.IsCancellationRequested)
                    await Task.Delay(3_000, ct).ConfigureAwait(false);
            }
        }

        private async Task ConnectAndReadAsync(CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(WsUrl), ct);
            SetConnected(true);

            // Subscribe to ALL SWAP (USDT-perp) liquidation orders — no symbol filter needed
            var sub = """{"op":"subscribe","args":[{"channel":"liquidation-orders","instType":"SWAP"}]}""";
            await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, ct);

            var buf = new byte[65_536];
            var msg = new StringBuilder();

            // OKX requires a ping every 25 s to stay connected
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await timer.WaitForNextTickAsync(ct);
                    try { await ws.SendAsync(Encoding.UTF8.GetBytes("ping"),
                              WebSocketMessageType.Text, true, ct); }
                    catch { break; }
                }
            }, ct);

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                msg.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    msg.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                } while (!result.EndOfMessage);

                ParseAndFire(msg.ToString());
            }
        }

        // ── Parse OKX liquidation-orders message ──────────────────────────────
        // {"arg":{"channel":"liquidation-orders","instType":"SWAP"},
        //  "data":[{"details":[{"side":"sell","sz":"0.01","bkPx":"73000","bkLoss":"0","ccy":"USDT"}],
        //           "instId":"BTC-USDT-SWAP","uly":"BTC-USDT",...}]}

        private void ParseAndFire(string json)
        {
            try
            {
                if (json.Trim() == "pong") return;

                var obj = JObject.Parse(json);

                var channel = obj["arg"]?["channel"]?.Value<string>() ?? "";
                if (!channel.Equals("liquidation-orders", StringComparison.OrdinalIgnoreCase))
                    return;

                var dataArr = obj["data"] as JArray;
                if (dataArr == null) return;

                foreach (var item in dataArr)
                {
                    var instId  = item["instId"]?.Value<string>() ?? "";
                    var details = item["details"] as JArray;
                    if (details == null) continue;

                    foreach (var detail in details)
                    {
                        var side     = detail["side"]?.Value<string>()  ?? "";
                        var szStr    = detail["sz"]?.Value<string>()    ?? "0";
                        var priceStr = detail["bkPx"]?.Value<string>()  ?? "0";

                        if (!decimal.TryParse(priceStr, NumberStyles.Any,
                                CultureInfo.InvariantCulture, out var price)) continue;
                        if (!decimal.TryParse(szStr, NumberStyles.Any,
                                CultureInfo.InvariantCulture, out var qty)) continue;

                        var ev = new BinanceLiquidationEvent
                        {
                            Symbol   = OkxInstIdToSymbol(instId),
                            Side     = side.Equals("sell", StringComparison.OrdinalIgnoreCase)
                                           ? "SELL" : "BUY",
                            Price    = price,
                            Quantity = qty,
                            Time     = DateTime.Now,
                            Exchange = "OKX",
                        };

                        if (!string.IsNullOrEmpty(ev.Symbol) && ev.UsdValue >= MinUsdValue)
                            OnLiquidation?.Invoke(ev);
                    }
                }
            }
            catch { }
        }

        /// <summary>"BTC-USDT-SWAP" → "BTCUSDT"</summary>
        private static string OkxInstIdToSymbol(string instId)
        {
            var parts = instId.Split('-');
            return parts.Length >= 2 ? parts[0] + parts[1] : instId;
        }

        private void SetConnected(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(connected);
        }

        public void Dispose() => _cts.Cancel();
    }
}
