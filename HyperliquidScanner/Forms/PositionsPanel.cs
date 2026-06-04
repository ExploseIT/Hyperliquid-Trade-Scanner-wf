using HyperliquidScanner.Models;
using HyperliquidScanner.Services;

namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Bottom panel showing open Hyperliquid positions with live PnL.
    /// Polls every 5 seconds. Feeds PositionMonitor for auto SL/TP when enabled.
    /// </summary>
    public class PositionsPanel : Panel
    {
        private readonly HyperliquidClient _client;
        private readonly AppConfig         _config;
        private readonly PositionMonitor?  _monitor;

        private DataGridView                _grid    = null!;
        private Label                       _header  = null!;
        private System.Windows.Forms.Timer  _timer   = null!;

        internal List<PositionInfo> _positions    = new();
        private HashSet<string>    _hip3Assets   = new(StringComparer.OrdinalIgnoreCase);
        private bool               _scanInProgress = false;

        /// <summary>Set to true while a full asset scan is running to pause the mini-scan.</summary>
        public void SetScanInProgress(bool scanning) => _scanInProgress = scanning;

        /// <summary>Fired after each position refresh — lets MainForm highlight matching grid rows.</summary>
        public event Action<List<PositionInfo>>? PositionsRefreshed;

        /// <summary>
        /// Called by MainForm after each scan with the full known asset list.
        /// Used to resolve HIP-3 coin names (e.g. "AMD" → "xyz:AMD") in position data.
        /// </summary>
        public void SetKnownAssets(IEnumerable<string> assetNames)
        {
            // Build a set of HIP-3 assets (those containing ':')
            _hip3Assets = new HashSet<string>(
                assetNames.Where(a => a.Contains(':')),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a bare coin name to a full HIP-3 name if a match exists.
        /// e.g. "AMD" → "xyz:AMD" if "xyz:AMD" is in the known asset list.
        /// </summary>
        private string ResolveSymbol(string coin)
        {
            if (coin.Contains(':')) return coin; // already prefixed
            var match = _hip3Assets.FirstOrDefault(a =>
                a.EndsWith(":" + coin, StringComparison.OrdinalIgnoreCase));
            return match ?? coin;
        }

        public PositionsPanel(HyperliquidClient client, AppConfig config,
                              PositionMonitor? monitor = null)
        {
            _client  = client;
            _config  = config;
            _monitor = monitor;

            if (_monitor != null)
            {
                _monitor.OrderPlaced += (sym, msg) => BeginInvoke(() => ShowOrderResult(sym, msg, true));
                _monitor.OrderFailed += (sym, msg) => BeginInvoke(() => ShowOrderResult(sym, msg, false));
                _monitor.SlWarning   += (sym, pnl) => BeginInvoke(() => HighlightSlWarning(sym));
            }

            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(20, 20, 20);

            BuildControls();

            _timer = new System.Windows.Forms.Timer { Interval = 5_000 };
            _timer.Tick += async (_, _) => await RefreshAsync();
            _timer.Start();
        }

        private void BuildControls()
        {
            // Header bar
            _header = new Label
            {
                Text      = "Open Positions",
                ForeColor = Color.Silver,
                BackColor = Color.FromArgb(30, 30, 30),
                Dock      = DockStyle.Top,
                Height    = 24,
                Padding   = new Padding(8, 4, 0, 0),
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            // Positions grid
            _grid = new DataGridView
            {
                Dock                  = DockStyle.Fill,
                ReadOnly              = true,
                AllowUserToAddRows    = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible     = false,
                SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.AllCells,
                BackgroundColor       = Color.FromArgb(20, 20, 20),
                GridColor             = Color.FromArgb(40, 40, 40),
                BorderStyle           = BorderStyle.None,
                Font                  = new Font("Consolas", 9f),
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight   = 24,
                ScrollBars            = ScrollBars.Both
            };

            _grid.DefaultCellStyle.BackColor          = Color.FromArgb(22, 22, 22);
            _grid.DefaultCellStyle.ForeColor          = Color.FromArgb(210, 210, 210);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 80, 60);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 30, 30);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Silver;

            _grid.Columns.AddRange(
                new DataGridViewTextBoxColumn { HeaderText = "Symbol",   Name = "Symbol",   MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "Side",     Name = "Side",     MinimumWidth = 55  },
                new DataGridViewTextBoxColumn { HeaderText = "Size",     Name = "Size",     MinimumWidth = 70  },
                new DataGridViewTextBoxColumn { HeaderText = "Entry",    Name = "Entry",    MinimumWidth = 75  },
                new DataGridViewTextBoxColumn { HeaderText = "Mark",     Name = "Mark",     MinimumWidth = 75  },
                new DataGridViewTextBoxColumn { HeaderText = "PnL$",     Name = "PnlUsd",   MinimumWidth = 70  },
                new DataGridViewTextBoxColumn { HeaderText = "ROE%",     Name = "PnlPct",   MinimumWidth = 70  },
                new DataGridViewTextBoxColumn { HeaderText = "Liq",      Name = "LiqPrice", MinimumWidth = 75  },
                new DataGridViewTextBoxColumn { HeaderText = "⇅",        Name = "Leverage", MinimumWidth = 50  },
                new DataGridViewTextBoxColumn { HeaderText = "Margin",   Name = "Margin",   MinimumWidth = 70  },
                new DataGridViewTextBoxColumn { HeaderText = "SL Price", Name = "Sl",       MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "TP Price", Name = "Tp",       MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "Trail",    Name = "Trail",    MinimumWidth = 120 },
                new DataGridViewTextBoxColumn { HeaderText = "Status",   Name = "Status",   MinimumWidth = 160 },
                new DataGridViewButtonColumn  { HeaderText = "+",         Name = "Enter",    MinimumWidth = 36, Width = 36, FlatStyle = FlatStyle.Flat },
                new DataGridViewButtonColumn  { HeaderText = "⏹",        Name = "Close",    MinimumWidth = 36, Width = 36, FlatStyle = FlatStyle.Flat }
            );

            foreach (DataGridViewColumn col in _grid.Columns)
                col.SortMode = DataGridViewColumnSortMode.NotSortable;

            // Keep all rows visually unselected
            bool _clearingSelection = false;
            _grid.SelectionChanged += (_, _) =>
            {
                if (_clearingSelection) return;
                _clearingSelection = true;
                _grid.ClearSelection();
                _clearingSelection = false;
            };

            _grid.CellFormatting  += Grid_CellFormatting;
            _grid.CellPainting    += Grid_CellPainting;
            _grid.CellClick       += Grid_CellClick;

            Controls.Add(_grid);
            Controls.Add(_header);
        }

        private int _emptyResponseCount = 0;
        private const int EmptyResponseThreshold = 3; // require 3 consecutive empty responses before clearing

        // Latest mid prices for watchlist symbols (no open position)
        private Dictionary<string, decimal> _lastMids = new(StringComparer.OrdinalIgnoreCase);

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            try
            {
                // ── Fetch fresh data (skipped during scans to avoid 429s) ──────
                if (!_scanInProgress)
                {
                    // Fetch fresh position data — skipped during scans to avoid 429s
                    var positions = await _client.GetPositionsAsync(ct);

                    // Fetch mid prices for watchlist symbols (configured but no open position)
                    try { _lastMids = await _client.GetAllMidsAsync(ct); }
                    catch { /* non-fatal — watchlist rows will show – for price */ }

                    if (positions.Count == 0 && _positions.Count > 0)
                    {
                        _emptyResponseCount++;
                        if (_emptyResponseCount >= EmptyResponseThreshold)
                            _positions = positions; // genuinely empty — clear
                        // else keep cached data, still fall through to grid update
                    }
                    else
                    {
                        _emptyResponseCount = 0;
                        _positions = positions;
                    }
                }

                // Always refresh grid from cached data — even during scans
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(() =>
                    {
                        UpdateGrid(_positions, _lastMids);
                        PositionsRefreshed?.Invoke(_positions);
                    });

                // Always run monitor against cached data — trailing/TP/SL keep ticking
                if (_monitor != null && _positions.Count > 0)
                    await _monitor.CheckPositionsAsync(_positions, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PositionsPanel] refresh error: {ex.Message}");
            }
        }

        private void ShowOrderResult(string symbol, string message, bool success)
        {
            // Flash the matching row and update its status cell
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is PositionInfo p && p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                {
                    row.Cells["Status"].Value = success ? "🟢 Order placed" : "🔴 Order failed";
                    row.DefaultCellStyle.BackColor = success
                        ? Color.FromArgb(10, 40, 20)
                        : Color.FromArgb(50, 10, 10);
                }
            }
        }

        private void HighlightSlWarning(string symbol)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is PositionInfo p && p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(50, 40, 10); // amber warning
                    row.Cells["Status"].Value = "⚠ Near SL";
                }
            }
        }

        private void UpdateGrid(List<PositionInfo> positions,
                                Dictionary<string, decimal>? mids = null)
        {
            _grid.Rows.Clear();

            if (positions.Count == 0)
            {
                _header.Text = "Open Positions — none";
                return;
            }

            var visibleCount = positions.Count(p => !_config.GetRiskConfig(ResolveSymbol(p.Symbol)).GridViewDisabled);
            _header.Text = $"Open Positions — {visibleCount} open" +
                           (visibleCount < positions.Count ? $"  ({positions.Count - visibleCount} hidden)" : "");

            foreach (var p in positions)
            {
                p.Symbol = ResolveSymbol(p.Symbol); // resolve HIP-3 prefix if needed

                var riskCfg  = _config.GetRiskConfig(p.Symbol);

                // Skip positions marked as hidden in config
                if (riskCfg.GridViewDisabled) continue;
                var pnlSign  = p.UnrealisedPnl >= 0 ? "+" : "";
                var pctSign  = p.PnlPercent    >= 0 ? "+" : "";
                var liqText  = p.LiquidationPrice.HasValue
                             ? p.LiquidationPrice.Value.ToString("G6")
                             : "–";

                // SL/TP trigger prices derived from USD thresholds:
                // pnl = (markPrice - entryPrice) × size  →  slPrice = entryPrice - (slUsd / size)
                string slText, tpText;
                if (p.Size > 0 && (riskCfg.SlEnabled || riskCfg.TpEnabled))
                {
                    var slPrice = p.IsLong
                        ? p.EntryPrice - riskCfg.SlUsd / p.Size
                        : p.EntryPrice + riskCfg.SlUsd / p.Size;
                    var tpPrice = p.IsLong
                        ? p.EntryPrice + riskCfg.TpUsd / p.Size
                        : p.EntryPrice - riskCfg.TpUsd / p.Size;
                    slText = riskCfg.SlEnabled ? slPrice.ToString("G6") : "–";
                    tpText = riskCfg.TpEnabled ? tpPrice.ToString("G6") : "–";
                }
                else
                {
                    slText = "–";
                    tpText = "–";
                }

                var status   = _monitor?.GetStatus(p.Symbol) ?? SymbolRiskStatus.Normal;
                var rsiSlope = _monitor?.GetRsiSlope(p.Symbol);
                var slopeStr = rsiSlope.HasValue && (riskCfg.SlEnabled || riskCfg.TpEnabled)
                    ? $" RSI↕{rsiSlope.Value:F1}"
                    : string.Empty;
                var activeFlags = (riskCfg.SlEnabled ? $"SL-${riskCfg.SlUsd:F0}" : "") +
                                  (riskCfg.SlEnabled && riskCfg.TpEnabled ? "+" : "") +
                                  (riskCfg.TpEnabled ? $"TP+${riskCfg.TpUsd:F0}" : "");
                var statusText = status switch
                {
                    SymbolRiskStatus.SlFired      => "🔴 SL fired",
                    SymbolRiskStatus.TpFired      => "🟢 TP fired",
                    SymbolRiskStatus.TrailingFired => "🔒 Trail fired",
                    _ => activeFlags.Length > 0 ? $"{activeFlags}{slopeStr}" : "–"
                };

                // Trail column: show high-water mark and activation state
                string trailText = "–";
                if (riskCfg.ExchangeTrailingEnabled)
                {
                    // Exchange-native ratcheting trailing
                    var exTrail = _monitor?.GetExchangeTrailingInfo(p.Symbol);
                    if (exTrail == null || !exTrail.Value.active)
                        trailText = $"📌 Wait ${riskCfg.TrailingMinProfitUsd:F2}";
                    else
                        trailText = $"📌 SL ${exTrail.Value.slPrice:G6}";
                }
                else if (riskCfg.TrailingEnabled)
                {
                    // App-side trailing
                    var trail = _monitor?.GetTrailingInfo(p.Symbol);
                    if (trail == null)
                        trailText = $"Min ${riskCfg.TrailingMinProfitUsd:F2}";
                    else if (trail.Value.fired)
                        trailText = "🔒 Fired";
                    else if (trail.Value.active && trail.Value.hwmUsd > decimal.MinValue)
                        trailText = $"Peak ${trail.Value.hwmUsd:F2}";
                    else
                        trailText = $"Wait ${riskCfg.TrailingMinProfitUsd:F2}";
                }

                var idx = _grid.Rows.Add(
                    p.Symbol,
                    p.SideLabel,
                    p.Size.ToString("G6"),
                    p.EntryPrice.ToString("G6"),
                    p.MarkPrice.ToString("G6"),
                    $"{pnlSign}{p.UnrealisedPnl:F2}",
                    $"{pctSign}{p.PnlPercent * 100:F2}%",
                    liqText,
                    $"{p.Leverage}x",           // just "10x" — no cross/isolated
                    $"{p.MarginUsed:F2}",
                    slText,
                    tpText,
                    trailText,
                    statusText,
                    riskCfg.TradeEntryEnabled ? "+" : "",  // entry button (blank = disabled)
                    "✕"                                     // close button
                );

                _grid.Rows[idx].Tag = p;
            }

            // ── Watchlist: configured symbols with no open position ────────────
            var openSymbols = positions
                .Select(p => p.Symbol)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var watchlist = _config.SymbolInfo
                .Where(s => s.TradeEntryEnabled &&
                            !s.Symbol.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase) &&
                            !openSymbols.Contains(s.Symbol))
                .ToList();

            if (watchlist.Count == 0) return;

            // Separator row
            var sepIdx = _grid.Rows.Add("── Watchlist ──", "", "", "", "", "", "", "", "", "", "", "", "", "");
            _grid.Rows[sepIdx].Tag              = null; // no PositionInfo — click handlers skip it
            _grid.Rows[sepIdx].DefaultCellStyle.BackColor  = Color.FromArgb(28, 28, 28);
            _grid.Rows[sepIdx].DefaultCellStyle.ForeColor  = Color.FromArgb(80, 80, 80);
            _grid.Rows[sepIdx].DefaultCellStyle.Font       = new Font("Segoe UI", 8f, FontStyle.Italic);
            _grid.Rows[sepIdx].Height = 18;

            foreach (var cfg in watchlist)
            {
                // Resolve symbol name (HIP-3 aware)
                var sym      = cfg.Symbol;
                var midKey   = sym.Contains(':') ? sym.Split(':')[1] : sym;
                decimal markPrice = 0;
                mids?.TryGetValue(sym, out markPrice);
                if (markPrice == 0) mids?.TryGetValue(midKey, out markPrice);
                var markText = markPrice > 0 ? markPrice.ToString("G6") : "–";

                // Synthetic PositionInfo so + button click handler works unchanged
                var synthetic = new PositionInfo
                {
                    Symbol     = sym,
                    IsLong     = cfg.TradeIsLong,
                    MarkPrice  = markPrice,
                    Size       = 0
                };

                var direction = cfg.TradeIsLong ? "Long" : "Short";
                var wIdx = _grid.Rows.Add(
                    sym,
                    direction,
                    "–", "–",
                    markText,
                    "–", "–", "–",
                    $"{cfg.TradeLeverage}x",
                    "–", "–", "–", "–",
                    "No position",
                    cfg.TradeEntryEnabled ? "+" : "",
                    ""   // no close button
                );
                _grid.Rows[wIdx].Tag = synthetic;
                _grid.Rows[wIdx].DefaultCellStyle.BackColor = Color.FromArgb(18, 18, 18);
            }
        }

        private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            if (_grid.Rows[e.RowIndex].Tag is not PositionInfo p) return;

            // Watchlist rows (Size == 0, synthetic) — muted styling
            if (p.Size == 0)
            {
                var wcol = _grid.Columns[e.ColumnIndex].Name;
                if (wcol == "Side")
                    e.CellStyle.ForeColor = p.IsLong
                        ? Color.FromArgb(40, 120, 70)
                        : Color.FromArgb(120, 40, 40);
                if (wcol == "Status")
                    e.CellStyle.ForeColor = Color.FromArgb(70, 70, 70);
                if (wcol == "Mark")
                    e.CellStyle.ForeColor = Color.FromArgb(130, 130, 130);
                return;
            }

            var col = _grid.Columns[e.ColumnIndex].Name;

            // Side column
            if (col == "Side")
            {
                e.CellStyle.ForeColor = p.IsLong
                    ? Color.FromArgb(80, 220, 130)   // green = long
                    : Color.FromArgb(220, 80, 80);   // red = short
            }

            // PnL columns — green for profit, red for loss
            if (col is "PnlUsd" or "PnlPct")
            {
                e.CellStyle.ForeColor = p.UnrealisedPnl >= 0
                    ? Color.FromArgb(80, 220, 130)
                    : Color.FromArgb(220, 80, 80);
                e.CellStyle.Font = new Font("Consolas", 9f, FontStyle.Bold);
            }

            // Liq price — amber warning
            if (col == "LiqPrice" && p.LiquidationPrice.HasValue)
                e.CellStyle.ForeColor = Color.FromArgb(255, 180, 50);

            // SL/TP threshold columns — muted grey
            if (col is "Sl" or "Tp")
                e.CellStyle.ForeColor = Color.FromArgb(140, 140, 140);

            // Trail column
            if (col == "Trail")
            {
                var val = e.Value?.ToString() ?? "–";
                e.CellStyle.ForeColor = val.StartsWith("Peak")  ? Color.FromArgb(80, 220, 130)   // green — trailing active
                                      : val.StartsWith("🔒")    ? Color.FromArgb(255, 200, 60)   // gold  — fired
                                      : Color.FromArgb(100, 100, 100);                            // grey  — waiting
            }

            // Status column
            if (col == "Status")
            {
                var val = e.Value?.ToString() ?? "";
                e.CellStyle.ForeColor = val.Contains("SL fired")    ? Color.FromArgb(220, 80,  80)
                                      : val.Contains("TP fired")    ? Color.FromArgb(80,  220, 130)
                                      : val.Contains("Trail fired") ? Color.FromArgb(255, 200, 60)
                                      : val.Contains("Near SL")     ? Color.FromArgb(255, 180, 50)
                                      : Color.FromArgb(100, 100, 100);
            }
        }

        /// <summary>Custom-paint the Close column header and cells for compact red styling.</summary>
        private void Grid_CellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            var colName = _grid.Columns[e.ColumnIndex].Name;

            // Separator row (null tag) — paint blank for button columns
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag == null &&
                colName is "Enter" or "Close")
            {
                e.PaintBackground(e.ClipBounds, true);
                e.Handled = true;
                return;
            }

            // ── Entry button column ───────────────────────────────────────────
            if (colName == "Enter")
            {
                e.PaintBackground(e.ClipBounds, true);

                if (e.RowIndex < 0)
                {
                    // Header — "+" icon
                    using var hf = new Font("Segoe UI", 11f, FontStyle.Bold);
                    TextRenderer.DrawText(e.Graphics, "+", hf, e.CellBounds,
                        Color.FromArgb(80, 200, 120),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    e.Handled = true;
                    return;
                }

                // Only paint button if entry is configured for this row
                if (_grid.Rows[e.RowIndex].Tag is PositionInfo rp)
                {
                    var rc = _config.GetRiskConfig(rp.Symbol);
                    if (rc.TradeEntryEnabled)
                    {
                        var eb = new Rectangle(
                            e.CellBounds.Left + 4, e.CellBounds.Top + 3,
                            e.CellBounds.Width - 8, e.CellBounds.Height - 6);
                        using var gb = new SolidBrush(Color.FromArgb(30, 110, 60));
                        e.Graphics.FillRectangle(gb, eb);
                        using var gp = new Pen(Color.FromArgb(60, 170, 90));
                        e.Graphics.DrawRectangle(gp, eb);
                        using var lf = new Font("Consolas", 9f, FontStyle.Bold);
                        TextRenderer.DrawText(e.Graphics, "+", lf, eb,
                            Color.FromArgb(80, 220, 130),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                e.Handled = true;
                return;
            }

            if (colName != "Close") return;

            e.PaintBackground(e.ClipBounds, true);

            // Header row — draw the stop square icon centred
            if (e.RowIndex < 0)
            {
                using var headerFont = new Font("Segoe UI Symbol", 10f);
                var txt  = "⏹";
                var size = TextRenderer.MeasureText(txt, headerFont);
                var x    = e.CellBounds.Left + (e.CellBounds.Width  - size.Width)  / 2;
                var y    = e.CellBounds.Top  + (e.CellBounds.Height - size.Height) / 2;
                TextRenderer.DrawText(e.Graphics, txt, headerFont,
                    new Point(x, y), Color.FromArgb(160, 160, 160));
                e.Handled = true;
                return;
            }

            // Data cells — draw a small red ✕ button
            var bounds = new Rectangle(
                e.CellBounds.Left  + 4,
                e.CellBounds.Top   + 3,
                e.CellBounds.Width  - 8,
                e.CellBounds.Height - 6);

            using var btnBrush = new SolidBrush(Color.FromArgb(160, 40, 40));
            using var hoverBrush = new SolidBrush(Color.FromArgb(200, 60, 60));
            e.Graphics.FillRectangle(btnBrush, bounds);

            using var pen = new Pen(Color.FromArgb(200, 80, 80));
            e.Graphics.DrawRectangle(pen, bounds);

            using var lblFont = new Font("Consolas", 8f, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, "✕", lblFont, bounds,
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            e.Handled = true;
        }

        /// <summary>Entry/Close button click handler.</summary>
        private void Grid_CellClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var colName = _grid.Columns[e.ColumnIndex].Name;

            if (colName == "Enter")
            {
                if (_grid.Rows[e.RowIndex].Tag is not PositionInfo pos) return;
                var riskCfg = _config.GetRiskConfig(pos.Symbol);
                if (!riskCfg.TradeEntryEnabled) return;

                var direction  = riskCfg.TradeIsLong ? "Long" : "Short";
                var notional   = riskCfg.TradeEntrySizeUsd * riskCfg.TradeLeverage;
                if (pos.MarkPrice <= 0) { ShowEntryError(pos.Symbol, "No price available — retry in a moment"); return; }
                var assetInfo  = _client.GetAssetIndex(pos.Symbol);
                if (assetInfo == null) { ShowEntryError(pos.Symbol, $"Asset index not found for {pos.Symbol} — ensure the scanner has run at least once"); return; }
                var size       = Math.Round(notional / pos.MarkPrice,
                                     assetInfo.Value.szDecimals, MidpointRounding.ToZero);

                var notionalUsd = size * pos.MarkPrice;
                var confirm = MessageBox.Show(
                    $"Add {direction} {pos.Symbol}?\n\n" +
                    $"  Size:      {size:G6}\n" +
                    $"  Notional:  ~${notionalUsd:F2}\n" +
                    $"  Mark:      {pos.MarkPrice:G6}\n\n" +
                    "Size is calculated from tradeEntrySizeUsd × tradeLeverage.\n" +
                    "Actual position leverage is unchanged.",
                    $"Confirm {direction} Entry — {pos.Symbol}",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes) return;
                _ = EnterPositionAsync(pos, riskCfg, size, assetInfo.Value.szDecimals);
                return;
            }

            if (colName != "Close") return;
            if (_grid.Rows[e.RowIndex].Tag is not PositionInfo closePos) return;

            var pnlSign      = closePos.UnrealisedPnl >= 0 ? "+" : "";
            var closeConfirm = MessageBox.Show(
                $"Market-close {closePos.SideLabel} {closePos.Symbol}?\n\n" +
                $"  Size:  {closePos.Size:G6}\n" +
                $"  PnL:   {pnlSign}{closePos.UnrealisedPnl:F2} USD\n\n" +
                "This will place a market IOC order at ±5% of mark price.",
                "Confirm Close",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (closeConfirm != DialogResult.Yes) return;
            _ = ClosePositionAsync(closePos);
        }

        /// <summary>Shows an entry error in the header — visible even if the grid has refreshed.</summary>
        private void ShowEntryError(string symbol, string reason)
        {
            System.Diagnostics.Debug.WriteLine($"[Entry] {symbol} FAILED: {reason}");
            Serilog.Log.Warning("[Entry] {Symbol} failed: {Reason}", symbol, reason);
            _header.Text      = $"⚠ {symbol} entry failed: {reason}";
            _header.ForeColor = Color.FromArgb(220, 80, 80);
            var t = new System.Windows.Forms.Timer { Interval = 6_000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); _header.ForeColor = Color.Silver; };
            t.Start();
        }

        private async Task EnterPositionAsync(PositionInfo pos, SymbolRiskConfig riskCfg,
                                               decimal size, int szDecimals)
        {
            try
            {
                var isBuy     = riskCfg.TradeIsLong;
                var (ok, msg) = await _client.PlaceMarketEntryAsync(
                    pos.Symbol, isBuy, pos.MarkPrice, size, szDecimals);

                if (ok) _monitor?.ClearSymbol(pos.Symbol);

                BeginInvoke(() =>
                {
                    if (ok)
                        ShowOrderResult(pos.Symbol, $"✓ {(isBuy ? "Long" : "Short")} entry {pos.Symbol}  size {size:G6}", true);
                    else
                        ShowEntryError(pos.Symbol, msg);
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() => ShowEntryError(pos.Symbol, ex.Message));
            }
        }

        private async Task ClosePositionAsync(PositionInfo pos)
        {
            try
            {
                // isBuy = true for shorts (buy to close), false for longs (sell to close)
                var isBuy      = !pos.IsLong;
                var assetInfo  = _client.GetAssetIndex(pos.Symbol);
                if (assetInfo == null)
                {
                    BeginInvoke(() => ShowOrderResult(pos.Symbol,
                        $"⚠ Close failed — asset index not found for {pos.Symbol}", false));
                    return;
                }

                var (ok, msg) = await _client.PlaceMarketCloseAsync(
                    pos.Symbol, isBuy, pos.MarkPrice, pos.Size, assetInfo.Value.szDecimals);

                // Immediately clear monitor state so any new position on this symbol
                // starts completely fresh — avoids the stale TrailingFired display bug
                if (ok) _monitor?.ClearSymbol(pos.Symbol);

                BeginInvoke(() => ShowOrderResult(pos.Symbol,
                    ok ? $"✓ Closed {pos.Symbol}" : $"✗ Close failed: {msg}", ok));
            }
            catch (Exception ex)
            {
                BeginInvoke(() => ShowOrderResult(pos.Symbol,
                    $"✗ Close error: {ex.Message}", false));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }
    }
}
