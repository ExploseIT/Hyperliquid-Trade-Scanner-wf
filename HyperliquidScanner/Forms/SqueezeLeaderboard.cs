using HyperliquidScanner.Services;

namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Custom-painted rolling leaderboard of top symbols by 5-minute liquidation volume.
    ///
    /// Bar semantics (matches CoinGlass heatmap convention):
    ///   RED   portion = short liquidations → shorts squeezed out → price rising → consider LONG
    ///   GREEN portion = long  liquidations → longs stopped out  → price falling → consider SHORT
    ///
    /// Rows with an active squeeze/cascade alert have a coloured background tint and a
    /// "LONG↑" / "SHORT↓" badge.  Click a row to raise <see cref="SymbolClicked"/>.
    /// </summary>
    public sealed class SqueezeLeaderboard : Control
    {
        private const int HeaderH = 24;
        private const int RowH    = 26;
        private const int MaxRows = 12;

        // Layout constants (px)
        private const int PadX  = 4;
        private const int SymW  = 52;   // symbol column
        private const int UsdW  = 52;   // total-USD column
        private const int PadXR = 4;    // right edge padding

        // Derived: bar fills remaining space
        private int BarX => PadX + SymW;
        private int BarW => Math.Max(20, Width - BarX - UsdW - PadXR);

        private List<LiquidationAggregator.SymbolSummary> _rows = new();
        private int      _hoverRow      = -1;
        private DateTime _lastEventTime = DateTime.MinValue;

        /// <summary>The currently selected HL symbol (e.g. "BTC", "kSHIB").
        /// Matching leaderboard row gets a left-edge accent bar.</summary>
        public string? SelectedSymbol { get; set; }

        /// <summary>Raised (on the UI thread) when the user clicks a leaderboard row.</summary>
        public event Action<string>? SymbolClicked;

        public SqueezeLeaderboard()
        {
            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint            |
                ControlStyles.ResizeRedraw         |
                ControlStyles.OptimizedDoubleBuffer,
                true);
            BackColor = Color.FromArgb(13, 13, 13);
        }

        // ── Data ──────────────────────────────────────────────────────────────

        /// <summary>Replace displayed data and repaint.  Must be called on the UI thread.</summary>
        public void UpdateData(IReadOnlyList<LiquidationAggregator.SymbolSummary> rows,
                               DateTime lastEventTime)
        {
            _rows          = rows.Take(MaxRows).ToList();
            _lastEventTime = lastEventTime;
            Invalidate();
        }

        // ── Mouse handling ────────────────────────────────────────────────────

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var i = HitRow(e.Y);
            if (i != _hoverRow) { _hoverRow = i; Invalidate(); }
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverRow = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var i = HitRow(e.Y);
            if (i >= 0 && i < _rows.Count)
                SymbolClicked?.Invoke(_rows[i].Symbol);
            base.OnMouseClick(e);
        }

        private int HitRow(int y)
        {
            if (y < HeaderH) return -1;
            var i = (y - HeaderH) / RowH;
            return i < _rows.Count ? i : -1;
        }

        // ── Paint ─────────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            DrawHeader(g);

            if (_rows.Count == 0)
            {
                DrawEmpty(g);
                return;
            }

            using var symFont   = new Font("Consolas", 8.5f, FontStyle.Bold);
            using var usdFont   = new Font("Consolas", 7.5f);
            using var badgeFont = new Font("Segoe UI",  7f, FontStyle.Bold);
            using var cntFont   = new Font("Segoe UI",  6.5f);

            for (int i = 0; i < _rows.Count; i++)
                DrawRow(g, i, symFont, usdFont, badgeFont, cntFont);
        }

        // ── Header row ────────────────────────────────────────────────────────

        private void DrawHeader(Graphics g)
        {
            using (var bg = new SolidBrush(Color.FromArgb(22, 22, 34)))
                g.FillRectangle(bg, 0, 0, Width, HeaderH);

            using var hFont  = new Font("Segoe UI", 7f, FontStyle.Bold);
            using var hColor = new SolidBrush(Color.FromArgb(110, 110, 155));
            using var rColor = new SolidBrush(Color.FromArgb(160, 55, 55));
            using var gColor = new SolidBrush(Color.FromArgb(0, 140, 80));
            using var lgFont = new Font("Segoe UI", 6.5f);

            g.DrawString("SYM", hFont, hColor, PadX, 5);
            g.DrawString("◄SHORT liq", lgFont, rColor, BarX, 5);
            g.DrawString("LONG liq►",  lgFont, gColor, BarX + BarW - 50, 13);
            g.DrawString("TOTAL", hFont, hColor, BarX + BarW + 2, 5);

            // Heartbeat: last event timestamp at far right
            if (_lastEventTime != DateTime.MinValue)
            {
                var age     = (DateTime.Now - _lastEventTime).TotalSeconds;
                var ageStr  = age < 60 ? $"{(int)age}s ago" : $"{(int)(age / 60)}m ago";
                var tsColor = age < 30  ? Color.FromArgb(0, 200, 100)
                            : age < 120 ? Color.FromArgb(200, 160, 40)
                            : Color.FromArgb(160, 60, 60);
                using var tsBrush = new SolidBrush(tsColor);
                using var tsFont  = new Font("Segoe UI", 6.5f);
                var str  = $"last {ageStr}";
                var sz   = g.MeasureString(str, tsFont);
                g.DrawString(str, tsFont, tsBrush, Width - sz.Width - PadXR, 7);
            }
        }

        // ── Empty state ───────────────────────────────────────────────────────

        private void DrawEmpty(Graphics g)
        {
            using var b = new SolidBrush(Color.FromArgb(65, 65, 65));
            using var f = new Font("Segoe UI", 8.5f);
            const string msg = "Waiting for liquidations...";
            var sz = g.MeasureString(msg, f);
            g.DrawString(msg, f, b, (Width - sz.Width) / 2f, HeaderH + 50f);

            // Still show last-seen time even when empty
            if (_lastEventTime != DateTime.MinValue)
            {
                using var tf = new Font("Segoe UI", 7.5f);
                using var tb = new SolidBrush(Color.FromArgb(80, 80, 80));
                var ts  = $"Feed alive · last event {_lastEventTime:HH:mm:ss}";
                var tsz = g.MeasureString(ts, tf);
                g.DrawString(ts, tf, tb, (Width - tsz.Width) / 2f, HeaderH + 72f);
            }
        }

        // ── Data row ─────────────────────────────────────────────────────────

        private void DrawRow(Graphics g, int i,
                             Font symFont, Font usdFont, Font badgeFont, Font cntFont)
        {
            var s    = _rows[i];
            var y    = HeaderH + i * RowH;
            var isH  = i == _hoverRow;
            var isSel = SymbolMatches(s.Symbol, SelectedSymbol);

            // ── Background ───────────────────────────────────────────────────

            Color bg;
            if (s.Alert == LiquidationAggregator.SqueezeType.ShortSqueeze)
                bg = Color.FromArgb(isH ? 55 : 38, isH ? 18 : 12, isH ? 18 : 12);  // red tint
            else if (s.Alert == LiquidationAggregator.SqueezeType.LongCascade)
                bg = Color.FromArgb(isH ? 32 : 20, isH ? 55 : 38, isH ? 32 : 20);  // green tint
            else
            {
                var v = isH ? 30 : (i % 2 == 0 ? 16 : 20);
                bg = Color.FromArgb(v, v, v);
            }

            using (var bgBrush = new SolidBrush(bg))
                g.FillRectangle(bgBrush, 0, y, Width, RowH);

            if (isSel)
                using (var accent = new SolidBrush(Color.FromArgb(0, 200, 130)))
                    g.FillRectangle(accent, 0, y, 3, RowH);

            using (var sep = new Pen(Color.FromArgb(28, 28, 28)))
                g.DrawLine(sep, 0, y + RowH - 1, Width, y + RowH - 1);

            // ── Symbol ───────────────────────────────────────────────────────

            Color symColor = s.Alert switch
            {
                LiquidationAggregator.SqueezeType.ShortSqueeze => Color.FromArgb(255, 100, 100),  // red
                LiquidationAggregator.SqueezeType.LongCascade  => Color.FromArgb(80, 230, 140),   // green
                _                                               => Color.FromArgb(190, 190, 190)
            };

            using (var sb = new SolidBrush(symColor))
            {
                var symStr = s.Symbol.Length > 6 ? s.Symbol[..6] : s.Symbol;
                g.DrawString(symStr, symFont, sb, PadX + (isSel ? 3 : 0), y + 3);
            }

            // Event count (small, right of symbol, e.g. "×7")
            if (s.EventCount > 0)
            {
                using var cntBrush = new SolidBrush(Color.FromArgb(90, 90, 90));
                g.DrawString($"×{s.EventCount}", cntFont, cntBrush, PadX + 36, y + 16);
            }

            // ── Bar ───────────────────────────────────────────────────────────

            var bW     = BarW - 2;
            var shortW = s.TotalUsd > 0 ? (int)(bW * (double)s.ShortPct) : 0;
            var longW  = bW - shortW;
            var barY   = y + 9;

            using (var track = new SolidBrush(Color.FromArgb(35, 35, 35)))
                g.FillRectangle(track, BarX, barY, bW, 10);

            if (shortW > 0)
                using (var rb = new SolidBrush(Color.FromArgb(175, 50, 50)))    // red  = short liq
                    g.FillRectangle(rb, BarX, barY, shortW, 10);

            if (longW > 0)
                using (var gb = new SolidBrush(Color.FromArgb(0, 155, 85)))     // green = long liq
                    g.FillRectangle(gb, BarX + shortW, barY, longW, 10);

            // ── USD total + alert badge ───────────────────────────────────────

            var usdX = BarX + BarW + 2;

            using (var ub = new SolidBrush(Color.FromArgb(175, 175, 175)))
                g.DrawString(s.FormatTotal(), usdFont, ub, usdX, y + 3);

            if (s.Alert != LiquidationAggregator.SqueezeType.None)
            {
                var (bText, bCol) = s.Alert == LiquidationAggregator.SqueezeType.ShortSqueeze
                    ? ("LONG ↑", Color.FromArgb(255, 100, 100))   // red  = short squeeze
                    : ("SHORT↓", Color.FromArgb(80, 230, 140));   // green = long cascade

                using (var bb = new SolidBrush(bCol))
                    g.DrawString(bText, badgeFont, bb, usdX, y + 15);
            }
        }

        // ── Symbol matching (handles Hyperliquid kXXX ↔ OKX 1000XXX) ─────────

        private static bool SymbolMatches(string lbSym, string? selected)
        {
            if (string.IsNullOrEmpty(selected)) return false;
            if (lbSym.Equals(selected, StringComparison.OrdinalIgnoreCase)) return true;

            if (selected.StartsWith("k", StringComparison.OrdinalIgnoreCase) && selected.Length > 1)
                return lbSym.Equals("1000" + selected[1..], StringComparison.OrdinalIgnoreCase);

            if (lbSym.StartsWith("1000", StringComparison.OrdinalIgnoreCase) && lbSym.Length > 4)
                return selected.Equals("k" + lbSym[4..], StringComparison.OrdinalIgnoreCase);

            return false;
        }
    }
}
