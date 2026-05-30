using System.Net.WebSockets;
using System.Text;
using HyperliquidScanner.Models;
using Newtonsoft.Json.Linq;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Connects to the Bybit V5 linear public WebSocket and subscribes to the
    /// <c>allLiquidation</c> channel (all USDT/USDC perpetual liquidations).
    ///
    /// URL      : wss://stream.bybit.com/v5/public/linear
    /// Channel  : allLiquidation  (no auth required)
    /// Heartbeat: {"op":"ping"} every 20 s (Bybit disconnects after 20 s idle)
    ///
    /// Bybit side semantics:
    ///   data.side = "Sell" → long  position liquidated → SELL order placed
    ///   data.side = "Buy"  → short position liquidated → BUY  order placed
    /// </summary>
    public sealed class BybitLiquidationFeed : IDisposable
    {
        private const string  WsUrl       = "wss://stream.bybit.com/v5/public/linear";
        private const decimal MinUsdValue = 1_500m;

        public event Action<BinanceLiquidationEvent>? OnLiquidation;
        public event Action<bool>?                    ConnectionChanged;
        public bool IsConnected { get; private set; }

        private readonly CancellationTokenSource _cts = new();

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

            // Subscribe to all linear perpetual liquidations
            var sub = """{"op":"subscribe","args":["allLiquidation"]}""";
            await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, ct);

            var buf = new byte[65_536];
            var msg = new StringBuilder();

            // Bybit disconnects after 20 s without activity — send ping every 20 s
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    await timer.WaitForNextTickAsync(ct);
                    try
                    {
                        await ws.SendAsync(
                            Encoding.UTF8.GetBytes("""{"op":"ping"}"""),
                            WebSocketMessageType.Text, true, ct);
                    }
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

        // ── Parse Bybit allLiquidation message ────────────────────────────────
        // {"topic":"allLiquidation","type":"snapshot","ts":1705639508903,
        //  "data":{"updatedTime":1705639508867,"symbol":"SOLUSDT",
        //          "side":"Buy","size":"0.1","price":"96.50"}}

        private void ParseAndFire(string json)
        {
            try
            {
                var obj = JObject.Parse(json);

                // Skip pong / subscription confirmation
                var op = obj["op"]?.Value<string>() ?? "";
                if (op.Length > 0) return;

                var topic = obj["topic"]?.Value<string>() ?? "";
                if (!topic.Equals("allLiquidation", StringComparison.OrdinalIgnoreCase))
                    return;

                // data can be a single object or an array of objects
                var rawData = obj["data"];
                if (rawData == null) return;

                var items = rawData is JArray arr ? arr.Cast<JToken>() : new[] { rawData };

                foreach (var item in items)
                {
                    var symbol   = item["symbol"]?.Value<string>() ?? "";
                    var side     = item["side"]?.Value<string>()   ?? "";
                    var sizeStr  = item["size"]?.Value<string>()   ?? "0";
                    var priceStr = item["price"]?.Value<string>()  ?? "0";

                    if (!decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var price)) continue;
                    if (!decimal.TryParse(sizeStr, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var qty))  continue;

                    // Bybit: "Sell" = long liquidated (SELL to close long)
                    //        "Buy"  = short liquidated (BUY  to close short)
                    var ev = new BinanceLiquidationEvent
                    {
                        Symbol   = symbol,
                        Side     = side.Equals("Sell", StringComparison.OrdinalIgnoreCase)
                                       ? "SELL" : "BUY",
                        Price    = price,
                        Quantity = qty,
                        Time     = DateTime.Now,
                        Exchange = "Bybit",
                    };

                    if (!string.IsNullOrEmpty(ev.Symbol) && ev.UsdValue >= MinUsdValue)
                        OnLiquidation?.Invoke(ev);
                }
            }
            catch { }
        }

        private void SetConnected(bool connected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(connected);
        }

        public void Dispose() => _cts.Cancel();
    }
}
