using HyperliquidScanner.Models;
using HyperliquidScanner.Services;
using System.Drawing;

namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Right-side panel shown when the user clicks a scan result row.
    ///
    /// Top section  — CoinGlass snapshot for the selected asset
    ///   • 1h / 4h / 24h long vs short bars + bias label
    ///   • Trading signal derived from liquidation bias
    ///
    /// Bottom section — live rolling 5-min leaderboard (all symbols, OKX feed)
    ///   • Powered by <see cref="LiquidationAggregator"/>
    ///   • GREEN bar = short liquidations → consider LONG
    ///   • RED   bar = long  liquidations → consider SHORT
    ///   • Clicking a leaderboard row loads that asset's CoinGlass snapshot
    /// </summary>
    public class LiquidationPanel : Panel
    {
        private readonly CoinglassClient _coinglass;

        // ── Header ────────────────────────────────────────────────────────────
        private Label _assetLabel  = null!;
        private Label _statusLabel = null!;

        // ── Snapshot rows ─────────────────────────────────────────────────────
        private LiquidationRow _row1h  = null!;
        private LiquidationRow _row4h  = null!;
        private LiquidationRow _row24h = null!;

        // ── Signal ────────────────────────────────────────────────────────────
        private Panel _signalBox    = null!;
        private Label _signalLabel  = null!;
        private Label _signalDetail = null!;

        // ── Burst alert strip ─────────────────────────────────────────────────
        private Panel  _burstPanel  = null!;
        private Label  _burstTitle  = null!;
        private Label  _burstDetail = null!;
        private System.Windows.Forms.Timer _burstTimer = null!;

        // ── Live leaderboard ──────────────────────────────────────────────────
        private Label              _lbHeaderLabel = null!;
        private SqueezeLeaderboard _leaderboard   = null!;

        private string? _currentSymbol;
        private CancellationTokenSource? _cts;

        public LiquidationPanel(CoinglassClient coinglass)
        {
            _coinglass = coinglass;
            Width      = 300;
            Dock       = DockStyle.Right;
            BackColor  = Color.FromArgb(20, 20, 20);
            Padding    = new Padding(12);

            BuildLayout();
            ShowEmpty();
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void BuildLayout()
        {
            // Header
            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 48,
                BackColor = Color.FromArgb(28, 28, 28)
            };

            _assetLabel = new Label
            {
                Text      = "Select an asset",
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                AutoSize  = false,
                Width     = 200,
                Location  = new Point(12, 8)
            };

            _statusLabel = new Label
            {
                Text      = string.Empty,
                ForeColor = Color.Gray,
                Font      = new Font("Segoe UI", 8f),
                AutoSize  = false,
                Width     = 260,
                Location  = new Point(12, 28)
            };

            header.Controls.Add(_assetLabel);
            header.Controls.Add(_statusLabel);

            // CoinGlass snapshot rows (1h / 4h / 24h)
            _row1h  = new LiquidationRow("1 hour");
            _row4h  = new LiquidationRow("4 hours");
            _row24h = new LiquidationRow("24 hours");

            var rowPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 3 * LiquidationRow.RowHeight + 8,
                BackColor = Color.Transparent,
                Padding   = new Padding(0, 4, 0, 0)
            };
            _row1h.Location  = new Point(0, 4);
            _row4h.Location  = new Point(0, 4 + LiquidationRow.RowHeight);
            _row24h.Location = new Point(0, 4 + 2 * LiquidationRow.RowHeight);
            rowPanel.Controls.AddRange(new Control[] { _row1h, _row4h, _row24h });

            // Signal box
            _signalBox = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 64,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding   = new Padding(10, 8, 10, 8)
            };

            _signalLabel = new Label
            {
                Text      = string.Empty,
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
                AutoSize  = true,
                Location  = new Point(10, 8)
            };

            _signalDetail = new Label
            {
                Text      = string.Empty,
                ForeColor = Color.Silver,
                Font      = new Font("Segoe UI", 8f),
                AutoSize  = false,
                Width     = 260,
                Height    = 28,
                Location  = new Point(10, 28)
            };

            _signalBox.Controls.Add(_signalLabel);
            _signalBox.Controls.Add(_signalDetail);

            // ── Burst alert strip (hidden until a burst fires) ────────────────
            _burstPanel = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 48,
                BackColor = Color.FromArgb(25, 25, 25),
                Visible   = false
            };

            _burstTitle = new Label
            {
                Text      = string.Empty,
                Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = false,
                Width     = 276,
                Height    = 22,
                Location  = new Point(8, 4)
            };

            _burstDetail = new Label
            {
                Text      = string.Empty,
                Font      = new Font("Segoe UI", 8f),
                ForeColor = Color.Silver,
                AutoSize  = false,
                Width     = 276,
                Height    = 18,
                Location  = new Point(8, 26)
            };

            _burstTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
            _burstTimer.Tick += (_, _) =>
            {
                _burstTimer.Stop();
                _burstPanel.Visible = false;
            };

            _burstPanel.Controls.Add(_burstTitle);
            _burstPanel.Controls.Add(_burstDetail);

            // Leaderboard section header
            var lbHeader = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 22,
                BackColor = Color.FromArgb(22, 22, 34)
            };

            _lbHeaderLabel = new Label
            {
                Text      = "⚡ Leaderboard — connecting...",
                ForeColor = Color.FromArgb(150, 150, 190),
                Font      = new Font("Segoe UI", 8f, FontStyle.Bold),
                AutoSize  = false,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(4, 0, 0, 0)
            };
            lbHeader.Controls.Add(_lbHeaderLabel);

            // Leaderboard control (Fill — takes all remaining height)
            _leaderboard = new SqueezeLeaderboard
            {
                Dock = DockStyle.Fill
            };

            _leaderboard.SymbolClicked += sym =>
            {
                // Clicking a leaderboard row loads that asset's CoinGlass snapshot.
                // Normalise OKX 1000XXX back to Hyperliquid kXXX so CoinGlass lookup works.
                var hlSym = sym.StartsWith("1000", StringComparison.OrdinalIgnoreCase) && sym.Length > 4
                    ? "k" + sym[4..]
                    : sym;
                LoadAsset(hlSym, 0);
            };

            // Stack top-down (Fill must be added first so Top controls push it down)
            Controls.Add(_leaderboard);
            Controls.Add(lbHeader);
            Controls.Add(_burstPanel);
            Controls.Add(_signalBox);
            Controls.Add(rowPanel);
            Controls.Add(header);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void ShowEmpty()
        {
            _assetLabel.Text     = "Select an asset";
            _statusLabel.Text    = "Click a row to see liquidation data";
            _row1h.Clear();
            _row4h.Clear();
            _row24h.Clear();
            _signalLabel.Text    = string.Empty;
            _signalDetail.Text   = string.Empty;
            _signalBox.BackColor = Color.FromArgb(30, 30, 30);
        }

        public void LoadAsset(string symbol, decimal trendScore)
        {
            if (_currentSymbol == symbol) return;
            _currentSymbol = symbol;

            // Highlight the matching leaderboard row
            _leaderboard.SelectedSymbol = symbol;
            _leaderboard.Invalidate();

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            _assetLabel.Text   = symbol;
            _statusLabel.Text  = "Loading...";
            _row1h.Clear();
            _row4h.Clear();
            _row24h.Clear();
            _signalLabel.Text  = string.Empty;
            _signalDetail.Text = string.Empty;

            _ = LoadAsync(symbol, trendScore, _cts.Token);
        }

        // ── Feed wiring ───────────────────────────────────────────────────────

        /// <summary>Wire the OKX WebSocket feed for connection-status updates.</summary>
        public void SetFeed(BinanceLiquidationFeed feed)
        {
            feed.ConnectionChanged += connected =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() =>
                {
                    _lbHeaderLabel.Text = connected
                        ? "🔥 5-min Leaderboard (OKX + Bybit)"
                        : "⚡ OKX — reconnecting...";
                    _lbHeaderLabel.ForeColor = connected
                        ? Color.FromArgb(80, 140, 255)
                        : Color.FromArgb(200, 140, 40);
                });
            };
        }

        /// <summary>Wire the Bybit WebSocket feed for connection-status updates.</summary>
        public void SetBybitFeed(BybitLiquidationFeed feed)
        {
            feed.ConnectionChanged += connected =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                if (!connected)
                    BeginInvoke(() =>
                    {
                        _lbHeaderLabel.Text      = "⚡ Bybit — reconnecting...";
                        _lbHeaderLabel.ForeColor  = Color.FromArgb(200, 140, 40);
                    });
            };
        }

        /// <summary>Wire the aggregator to supply leaderboard data and squeeze alerts.</summary>
        public void SetAggregator(LiquidationAggregator aggregator)
        {
            aggregator.LeaderboardUpdated += (summaries, lastEvent) =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() =>
                {
                    _leaderboard.UpdateData(summaries, lastEvent);

                    // Update header to show last-event time and row count
                    if (lastEvent != DateTime.MinValue)
                    {
                        var age = (DateTime.Now - lastEvent).TotalSeconds;
                        var ageStr = age < 60 ? $"{(int)age}s" : $"{(int)(age / 60)}m";
                        _lbHeaderLabel.Text = summaries.Count > 0
                            ? $"🔥 Leaderboard (OKX+Bybit) · {summaries.Count} active · {ageStr} ago"
                            : $"⚡ Leaderboard (OKX+Bybit) · quiet · last {ageStr} ago";
                        _lbHeaderLabel.ForeColor = age < 30
                            ? Color.FromArgb(80, 140, 255)
                            : age < 120
                                ? Color.FromArgb(200, 160, 40)
                                : Color.FromArgb(150, 60, 60);
                    }
                });
            };

            aggregator.SqueezeDetected += summary =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() => OnSqueezeAlert(summary));
            };

            aggregator.BurstDetected += burst =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() => ShowBurstAlert(burst));
            };
        }

        // ── Burst alert display ───────────────────────────────────────────────

        public void ShowBurstAlert(LiquidationAggregator.BurstAlert burst)
        {
            var isSqueeze = burst.Direction == LiquidationAggregator.SqueezeType.ShortSqueeze;

            _burstTitle.Text     = $"{burst.BriefLabel}  {burst.Symbol}";
            _burstTitle.ForeColor = isSqueeze
                ? Color.FromArgb(255, 110, 110)   // red  = short squeeze
                : Color.FromArgb(80, 240, 140);   // green = long cascade

            var evInfo = $"{burst.EventCount} events · {burst.FormatTotal()} in 30s";
            var tradeHint = isSqueeze ? "consider LONG" : "consider SHORT";
            _burstDetail.Text = $"{evInfo} · {tradeHint} · {burst.Time:HH:mm:ss}";

            _burstPanel.BackColor = isSqueeze
                ? Color.FromArgb(45, 12, 12)   // dark red  = short squeeze
                : Color.FromArgb(12, 45, 18);  // dark green = long cascade
            _burstPanel.Visible = true;

            // Reset auto-hide timer
            _burstTimer.Stop();
            _burstTimer.Start();

            // Also update the CoinGlass signal box if it's the selected asset
            OnBurstForSelectedAsset(burst);
        }

        private void OnBurstForSelectedAsset(LiquidationAggregator.BurstAlert burst)
        {
            if (string.IsNullOrEmpty(_currentSymbol)) return;

            var curKey = _currentSymbol.ToUpperInvariant();
            bool matches = burst.Symbol.Equals(curKey, StringComparison.OrdinalIgnoreCase);
            if (!matches && curKey.StartsWith("K") && curKey.Length > 1)
                matches = burst.Symbol.Equals("1000" + curKey[1..], StringComparison.OrdinalIgnoreCase);
            if (!matches) return;

            var isSqueeze = burst.Direction == LiquidationAggregator.SqueezeType.ShortSqueeze;
            _signalLabel.Text    = isSqueeze ? "🔥 SHORT SQUEEZE (burst)" : "💧 LONG CASCADE (burst)";
            _signalDetail.Text   = $"{burst.EventCount} events · {burst.FormatTotal()} in 30s · {burst.Time:HH:mm:ss}";
            _signalLabel.ForeColor = isSqueeze ? Color.FromArgb(80, 240, 140) : Color.FromArgb(255, 110, 110);
            _signalBox.BackColor   = isSqueeze ? Color.FromArgb(12, 38, 18)   : Color.FromArgb(38, 12, 12);
        }

        // ── Squeeze alert overlay on the selected asset's signal box ──────────

        private void OnSqueezeAlert(LiquidationAggregator.SymbolSummary summary)
        {
            if (string.IsNullOrEmpty(_currentSymbol)) return;

            // Does this alert match the currently viewed asset?
            var curKey = _currentSymbol.ToUpperInvariant();

            bool matches = summary.Symbol.Equals(curKey, StringComparison.OrdinalIgnoreCase);
            // k-prefix ↔ 1000-prefix normalisation
            if (!matches && curKey.StartsWith("K") && curKey.Length > 1)
                matches = summary.Symbol.Equals("1000" + curKey[1..], StringComparison.OrdinalIgnoreCase);

            if (!matches) return;

            if (summary.Alert == LiquidationAggregator.SqueezeType.ShortSqueeze)
            {
                _signalLabel.Text    = "🔥 Short Squeeze (live)";
                _signalDetail.Text   = $"Shorts wiped: {summary.FormatTotal()} in 5 min — consider LONG";
                _signalLabel.ForeColor = Color.FromArgb(80, 230, 140);
                _signalBox.BackColor   = Color.FromArgb(18, 38, 20);
            }
            else
            {
                _signalLabel.Text    = "💧 Long Cascade (live)";
                _signalDetail.Text   = $"Longs wiped: {summary.FormatTotal()} in 5 min — consider SHORT";
                _signalLabel.ForeColor = Color.FromArgb(255, 100, 100);
                _signalBox.BackColor   = Color.FromArgb(38, 14, 14);
            }
        }

        // ── CoinGlass snapshot load ───────────────────────────────────────────

        private async Task LoadAsync(string symbol, decimal trendScore, CancellationToken ct)
        {
            try
            {
                var snap = await _coinglass.GetSnapshotAsync(symbol, ct);

                if (ct.IsCancellationRequested) return;

                if (snap == null)
                {
                    _statusLabel.Text = "No data from Coinglass for this asset";
                    return;
                }

                _row1h.Update(snap.LongLiq1h,   snap.ShortLiq1h,  snap.Bias1hLabel);
                _row4h.Update(snap.LongLiq4h,   snap.ShortLiq4h,  snap.Bias4hLabel);
                _row24h.Update(snap.LongLiq24h, snap.ShortLiq24h, snap.Bias24hLabel);

                UpdateCoinglassSignal(snap, trendScore);

                _statusLabel.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _statusLabel.Text = $"Error: {ex.Message}";
            }
        }

        private void UpdateCoinglassSignal(CoinglassLiquidationSnapshot snap, decimal trendScore)
        {
            var bias1h  = snap.ShortBias1h;
            var bias4h  = snap.ShortBias4h;
            var bias24h = snap.ShortBias24h;

            var squeezeStrength = (bias1h + bias4h + bias24h) / 3m;

            string signal, detail;
            Color  colour;

            if (squeezeStrength > 0.65m && trendScore >= 2)
            {
                signal = "Strong long setup";
                detail = "Short squeeze across all windows + trend confirmed";
                colour = Color.FromArgb(0, 180, 100);
            }
            else if (squeezeStrength > 0.55m && trendScore >= 1)
            {
                signal = "Potential long";
                detail = "Short-heavy liquidations, watch for continuation";
                colour = Color.FromArgb(0, 150, 80);
            }
            else if (squeezeStrength < 0.35m)
            {
                signal = "Caution — long squeeze";
                detail = "Longs being wiped — wait for exhaustion before entry";
                colour = Color.FromArgb(200, 60, 60);
            }
            else if (bias1h < 0.4m && bias4h > 0.55m)
            {
                signal = "Squeeze fading";
                detail = "1h turning long-heavy — possible reversal forming";
                colour = Color.FromArgb(200, 140, 40);
            }
            else
            {
                signal = "Neutral";
                detail = "No strong directional bias in liquidations";
                colour = Color.FromArgb(120, 120, 120);
            }

            _signalLabel.Text    = signal;
            _signalDetail.Text   = detail;
            _signalBox.BackColor = Color.FromArgb(30,
                Math.Max(15, colour.R / 5),
                Math.Max(15, colour.G / 5),
                Math.Max(15, colour.B / 5));
            _signalLabel.ForeColor = colour;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Liquidation row (one CoinGlass window: label + long/short bars + bias)
    // ─────────────────────────────────────────────────────────────────────────

    public class LiquidationRow : Panel
    {
        public const int RowHeight = 56;

        private readonly Label  _windowLabel;
        private readonly Label  _longLabel;
        private readonly Label  _shortLabel;
        private readonly Label  _biasLabel;
        private readonly Panel  _longBar;
        private readonly Panel  _shortBar;
        private readonly Panel  _barTrack;

        public LiquidationRow(string window)
        {
            Height    = RowHeight;
            Width     = 276;
            BackColor = Color.Transparent;

            _windowLabel = MakeLabel(window, 0, 4, 80, Color.Gray, 8f);
            _biasLabel   = MakeLabel("", 160, 4, 110, Color.Silver, 8f);
            _longLabel   = MakeLabel("Long",  0, 22, 60, Color.FromArgb(80, 200, 120), 8f);
            _shortLabel  = MakeLabel("Short", 0, 38, 60, Color.FromArgb(200, 80, 80),  8f);

            _barTrack = new Panel
            {
                Location  = new Point(64, 22),
                Size      = new Size(200, 30),
                BackColor = Color.FromArgb(35, 35, 35)
            };

            _longBar = new Panel
            {
                Location  = new Point(0, 0),
                Size      = new Size(0, 13),
                BackColor = Color.FromArgb(0, 160, 90)
            };
            _shortBar = new Panel
            {
                Location  = new Point(0, 15),
                Size      = new Size(0, 13),
                BackColor = Color.FromArgb(180, 60, 60)
            };

            _barTrack.Controls.Add(_longBar);
            _barTrack.Controls.Add(_shortBar);

            Controls.AddRange(new Control[]
                { _windowLabel, _biasLabel, _longLabel, _shortLabel, _barTrack });
        }

        public void Update(decimal longUsd, decimal shortUsd, string bias)
        {
            var total = longUsd + shortUsd;
            if (total <= 0) { Clear(); return; }

            var trackW    = _barTrack.Width;
            var longRatio = (double)(longUsd / total);

            _longBar.Width   = (int)(trackW * longRatio);
            _shortBar.Width  = (int)(trackW * (1 - longRatio));
            _longLabel.Text  = $"Long  {FormatUsd(longUsd)}";
            _shortLabel.Text = $"Short {FormatUsd(shortUsd)}";
            _biasLabel.Text  = bias;
            _biasLabel.ForeColor = bias.Contains("squeeze") ? Color.FromArgb(80, 220, 130)
                               : bias.Contains("Long")      ? Color.FromArgb(220, 100, 80)
                               : Color.Silver;
        }

        public void Clear()
        {
            _longBar.Width   = 0;
            _shortBar.Width  = 0;
            _longLabel.Text  = "Long  —";
            _shortLabel.Text = "Short —";
            _biasLabel.Text  = string.Empty;
        }

        private static Label MakeLabel(string text, int x, int y, int w, Color fore, float size)
            => new Label
            {
                Text      = text,
                ForeColor = fore,
                Font      = new Font("Segoe UI", size),
                Location  = new Point(x, y),
                Size      = new Size(w, 16),
                AutoSize  = false
            };

        private static string FormatUsd(decimal value) => value switch
        {
            >= 1_000_000 => $"${value / 1_000_000:F2}M",
            >= 1_000     => $"${value / 1_000:F1}K",
            _            => $"${value:F0}"
        };
    }
}
