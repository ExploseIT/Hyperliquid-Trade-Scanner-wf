using HyperliquidScanner.Models;

namespace HyperliquidScanner.Services
{
    /// <summary>
    /// Aggregates live OKX liquidation events into rolling windows per symbol.
    ///
    /// Two signal modes:
    ///
    ///   BURST (fast path — fires on event arrival, no polling delay)
    ///     Triggered when ≥3 events for the same symbol arrive within 30 s
    ///     OR a single event ≥$30K lands.
    ///     Raises <see cref="BurstDetected"/> immediately in the ingest thread.
    ///     Cooldown: 90 s per symbol before re-firing.
    ///
    ///   LEADERBOARD (slow path — published every 2 s)
    ///     5-minute rolling window ranked by total USD.
    ///     Raises <see cref="LeaderboardUpdated"/> every 2 s.
    ///     Squeeze/cascade badges appear when $25K+ and 65%+ directional.
    /// </summary>
    public sealed class LiquidationAggregator : IDisposable
    {
        // ── Leaderboard thresholds ────────────────────────────────────────────
        private const decimal MinTotalUsd  = 25_000m;
        private const decimal AlertBias    = 0.65m;
        private const decimal MinEntryUsd  = 500m;
        private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(5);

        // ── Burst thresholds ──────────────────────────────────────────────────
        private const int     BurstMinEvents       = 3;        // events in 30 s window
        private const decimal BurstMinTotal        = 5_000m;   // min $ to confirm cluster
        private const decimal BurstDirectionalBias = 0.55m;    // 55% one-way minimum
        private static readonly TimeSpan BurstWindow   = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan BurstCooldown = TimeSpan.FromSeconds(90);

        // ── State ─────────────────────────────────────────────────────────────

        /// <summary>
        /// When set, only symbols in this set are ingested.
        /// Keys are OKX/Bybit base symbols (upper-case), e.g. "BNB", "1000SHIB".
        /// Null means no filter — all symbols pass through.
        /// </summary>
        private HashSet<string>? _hlAssets;

        private readonly Dictionary<string, Queue<LiqEvent>> _buckets
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Queue<LiqEvent>> _burstBuckets
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _burstCooldowns
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SqueezeType> _activeAlerts
            = new(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastEventTime = DateTime.MinValue;
        private readonly object _lock = new();
        private readonly System.Threading.Timer _timer;
        private bool _disposed;

        // ── Public events ─────────────────────────────────────────────────────

        /// <summary>
        /// Fired IMMEDIATELY on the ingest thread when a burst is detected.
        /// Subscribers must BeginInvoke to touch UI.
        /// </summary>
        public event Action<BurstAlert>? BurstDetected;

        /// <summary>Fired every 2 s with ranked 5-min leaderboard + heartbeat timestamp.</summary>
        public event Action<List<SymbolSummary>, DateTime>? LeaderboardUpdated;

        /// <summary>Fired when a new squeeze/cascade badge starts on the leaderboard (debounced).</summary>
        public event Action<SymbolSummary>? SqueezeDetected;

        // ── Public types ──────────────────────────────────────────────────────

        public enum SqueezeType { None, ShortSqueeze, LongCascade }

        /// <summary>Immediate burst alert — fires as events arrive.</summary>
        public sealed class BurstAlert
        {
            public string     Symbol             { get; init; } = "";
            public SqueezeType Direction          { get; init; }
            public int        EventCount         { get; init; }
            public decimal    TotalUsd           { get; init; }
            public decimal    LongUsd            { get; init; }
            public decimal    ShortUsd           { get; init; }
            public DateTime   Time               { get; init; }
            public decimal ShortPct => TotalUsd > 0 ? ShortUsd / TotalUsd : 0m;
            public decimal LongPct  => TotalUsd > 0 ? LongUsd  / TotalUsd : 0m;

            public string FormatTotal() => TotalUsd switch
            {
                >= 1_000_000m => $"${TotalUsd / 1_000_000m:F1}M",
                >= 1_000m     => $"${TotalUsd / 1_000m:F0}K",
                _             => $"${TotalUsd:F0}"
            };

            public string DirectionLabel => Direction == SqueezeType.ShortSqueeze
                ? "SHORT SQUEEZE → consider LONG"
                : "LONG CASCADE → consider SHORT";

            public string BriefLabel => Direction == SqueezeType.ShortSqueeze
                ? "🔥 SHORT SQUEEZE"
                : "💧 LONG CASCADE";
        }

        /// <summary>5-minute rolling summary — updated every 2 s.</summary>
        public sealed class SymbolSummary
        {
            public string     Symbol        { get; init; } = "";
            public decimal    TotalUsd      { get; init; }
            public decimal    LongUsd       { get; init; }
            public decimal    ShortUsd      { get; init; }
            public SqueezeType Alert        { get; init; }
            public int        EventCount    { get; init; }
            public DateTime   LastEventTime { get; init; }

            public decimal ShortPct => TotalUsd > 0 ? ShortUsd / TotalUsd : 0m;
            public decimal LongPct  => TotalUsd > 0 ? LongUsd  / TotalUsd : 0m;

            public string FormatTotal() => TotalUsd switch
            {
                >= 1_000_000m => $"${TotalUsd / 1_000_000m:F1}M",
                >= 1_000m     => $"${TotalUsd / 1_000m:F0}K",
                _             => $"${TotalUsd:F0}"
            };

            public string AlertLabel => Alert switch
            {
                SqueezeType.ShortSqueeze => "SHORT SQUEEZE — consider LONG",
                SqueezeType.LongCascade  => "LONG CASCADE — consider SHORT",
                _                        => ""
            };
        }

        private readonly record struct LiqEvent(DateTime Time, decimal LongUsd, decimal ShortUsd);

        // ── Constructor ───────────────────────────────────────────────────────

        public LiquidationAggregator(BinanceLiquidationFeed feed)
        {
            feed.OnLiquidation += Ingest;
            _timer = new System.Threading.Timer(
                _ => PublishLeaderboard(), null,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        /// <summary>Attach an additional liquidation source (e.g. Bybit).</summary>
        public void AttachFeed(BybitLiquidationFeed feed) => feed.OnLiquidation += Ingest;

        /// <summary>
        /// Restrict the leaderboard and burst alerts to symbols tradeable on Hyperliquid.
        /// Pass the names from <c>HyperliquidClient.GetAssetsAsync()</c>.
        /// HIP-3 names (containing ':') are skipped — they don't appear in OKX/Bybit feeds.
        /// k-prefix names (kSHIB) are stored as their exchange equivalent (1000SHIB).
        /// </summary>
        public void SetHlAssets(IEnumerable<string> hlNames)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in hlNames)
            {
                if (name.Contains(':')) continue; // HIP-3 — won't appear in OKX/Bybit feeds
                if (name.StartsWith("k", StringComparison.OrdinalIgnoreCase) && name.Length > 1)
                    set.Add("1000" + name[1..]);  // kSHIB → 1000SHIB
                else
                    set.Add(name);
            }
            lock (_lock) { _hlAssets = set; }
        }

        // ── Ingest (fast path) ────────────────────────────────────────────────

        private void Ingest(BinanceLiquidationEvent ev)
        {
            var sym = ev.BaseSymbol.ToUpperInvariant();
            var liqEv = ev.IsLongLiquidation
                ? new LiqEvent(ev.Time, ev.UsdValue, 0m)
                : new LiqEvent(ev.Time, 0m,           ev.UsdValue);

            BurstAlert? burst = null;

            lock (_lock)
            {
                // HL asset filter — drop symbols not tradeable on Hyperliquid
                if (_hlAssets != null && !_hlAssets.Contains(sym)) return;

                // 5-min bucket
                if (!_buckets.TryGetValue(sym, out var q))
                    _buckets[sym] = q = new Queue<LiqEvent>();
                q.Enqueue(liqEv);
                if (ev.Time > _lastEventTime) _lastEventTime = ev.Time;

                // 30-s burst bucket
                burst = CheckBurstLocked(sym, liqEv);
            }

            // Fire outside the lock
            if (burst != null)
                try { BurstDetected?.Invoke(burst); } catch { }
        }

        /// <summary>
        /// Called under <c>_lock</c>.  Returns a <see cref="BurstAlert"/> to fire, or null.
        /// Fires only when ≥3 events for the same symbol land within 30 s with
        /// ≥55 % directional bias and ≥$5 K total — never on a single event alone.
        /// </summary>
        private BurstAlert? CheckBurstLocked(string sym, LiqEvent newEvent)
        {
            var now    = DateTime.Now;
            var cutoff = now - BurstWindow;

            // Cooldown check
            if (_burstCooldowns.TryGetValue(sym, out var coolUntil) && now < coolUntil)
                return null;

            if (!_burstBuckets.TryGetValue(sym, out var bq))
                _burstBuckets[sym] = bq = new Queue<LiqEvent>();

            bq.Enqueue(newEvent);
            while (bq.Count > 0 && bq.Peek().Time < cutoff) bq.Dequeue();

            var longUsd = 0m; var shortUsd = 0m;
            foreach (var e in bq) { longUsd += e.LongUsd; shortUsd += e.ShortUsd; }
            var total = longUsd + shortUsd;

            // Require a genuine cluster — never fire on a single large event
            if (bq.Count < BurstMinEvents || total < BurstMinTotal) return null;

            // Require directional clarity
            if (total > 0 && Math.Max(longUsd, shortUsd) / total < BurstDirectionalBias)
                return null;

            var dir = shortUsd >= longUsd ? SqueezeType.ShortSqueeze : SqueezeType.LongCascade;

            _burstCooldowns[sym] = now + BurstCooldown;

            return new BurstAlert
            {
                Symbol     = sym,
                Direction  = dir,
                EventCount = bq.Count,
                TotalUsd   = total,
                LongUsd    = longUsd,
                ShortUsd   = shortUsd,
                Time       = now,
            };
        }

        // ── Leaderboard publish (2-s timer) ───────────────────────────────────

        private void PublishLeaderboard()
        {
            if (_disposed) return;

            var cutoff    = DateTime.Now - WindowLength;
            var summaries = new List<SymbolSummary>(64);
            DateTime lastEvent;

            lock (_lock)
            {
                lastEvent = _lastEventTime;

                foreach (var kv in _buckets)
                {
                    var q = kv.Value;
                    while (q.Count > 0 && q.Peek().Time < cutoff) q.Dequeue();
                    if (q.Count == 0) continue;

                    var longUsd = 0m; var shortUsd = 0m;
                    var count   = 0;  var lastTime = DateTime.MinValue;
                    foreach (var e in q)
                    {
                        longUsd  += e.LongUsd; shortUsd += e.ShortUsd; count++;
                        if (e.Time > lastTime) lastTime = e.Time;
                    }

                    var total = longUsd + shortUsd;
                    if (total < MinEntryUsd) continue;

                    summaries.Add(new SymbolSummary
                    {
                        Symbol        = kv.Key,
                        LongUsd       = longUsd,
                        ShortUsd      = shortUsd,
                        TotalUsd      = total,
                        Alert         = ClassifyAlert(total, longUsd, shortUsd),
                        EventCount    = count,
                        LastEventTime = lastTime,
                    });
                }
            }

            summaries.Sort((a, b) => b.TotalUsd.CompareTo(a.TotalUsd));

            try { LeaderboardUpdated?.Invoke(summaries, lastEvent); } catch { }

            foreach (var s in summaries)
            {
                _activeAlerts.TryGetValue(s.Symbol, out var prev);
                if (s.Alert != SqueezeType.None && s.Alert != prev)
                {
                    _activeAlerts[s.Symbol] = s.Alert;
                    try { SqueezeDetected?.Invoke(s); } catch { }
                }
                else if (s.Alert == SqueezeType.None && prev != SqueezeType.None)
                    _activeAlerts[s.Symbol] = SqueezeType.None;
            }

            foreach (var sym in _activeAlerts.Keys
                .Where(k => !summaries.Any(s => s.Symbol.Equals(k, StringComparison.OrdinalIgnoreCase)))
                .ToList())
                _activeAlerts.Remove(sym);
        }

        private static SqueezeType ClassifyAlert(decimal total, decimal longUsd, decimal shortUsd)
        {
            if (total < MinTotalUsd) return SqueezeType.None;
            if (shortUsd / total >= AlertBias) return SqueezeType.ShortSqueeze;
            if (longUsd  / total >= AlertBias) return SqueezeType.LongCascade;
            return SqueezeType.None;
        }

        // ── Query ─────────────────────────────────────────────────────────────

        public SymbolSummary? GetSummary(string symbol)
        {
            var cutoff = DateTime.Now - WindowLength;
            var key    = symbol.ToUpperInvariant();
            lock (_lock)
            {
                if (!_buckets.TryGetValue(key, out var q)) return null;
                var longUsd = 0m; var shortUsd = 0m; var count = 0; var lastTime = DateTime.MinValue;
                foreach (var e in q)
                {
                    if (e.Time < cutoff) continue;
                    longUsd += e.LongUsd; shortUsd += e.ShortUsd; count++;
                    if (e.Time > lastTime) lastTime = e.Time;
                }
                var total = longUsd + shortUsd;
                if (total == 0) return null;
                return new SymbolSummary
                {
                    Symbol        = key,
                    LongUsd       = longUsd,
                    ShortUsd      = shortUsd,
                    TotalUsd      = total,
                    EventCount    = count,
                    LastEventTime = lastTime,
                    Alert         = ClassifyAlert(total, longUsd, shortUsd),
                };
            }
        }

        public void Dispose() { _disposed = true; _timer.Dispose(); }
    }
}
