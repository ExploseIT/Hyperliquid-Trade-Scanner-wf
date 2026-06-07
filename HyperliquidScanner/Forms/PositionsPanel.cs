using HyperliquidScanner.Models;
using HyperliquidScanner.Services;
using HyperliquidScanner.Utils;

namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Bottom panel showing open Hyperliquid positions with live PnL.
    /// Polls every 5 seconds. Feeds PositionMonitor for auto SL/TP when enabled.
    /// </summary>
    public class PositionsPanel : Panel
    {
        private readonly AccountManager    _accountManager;
        private readonly AppConfig         _config;

        private DataGridView                _grid    = null!;
        private Label                       _header  = null!;

        // Independent background loop — not tied to UI thread or asset scanner
        private CancellationTokenSource    _loopCts = new();

        internal List<PositionInfo> _positions    = new();
        private HashSet<string>    _hip3Assets   = new(StringComparer.OrdinalIgnoreCase);
        private bool               _scanInProgress = false;

        // ── Dict key helpers — include account name so same symbol in two accounts works ──
        private static string DK(string accountName, string symbol) => $"{accountName}::{symbol}";
        private static string DK(PositionInfo pos) => $"{pos.AccountName}::{pos.Symbol}";

        // ── Limit entry bracket tracking ─────────────────────────────────────
        /// <summary>Pending limit entry orders — when filled, bracket (TP/SL) is placed.</summary>
        private record PendingBracket(long Oid, decimal? TpPrice, decimal? SlPrice,
                                      int SzDecimals, bool IsLong, string AccountName);
        private readonly Dictionary<string, PendingBracket> _pendingLimitEntries = new();

        // ── Limit close tracking ─────────────────────────────────────────────
        /// <summary>Active limit close orders — auto-updated when position size changes.</summary>
        private readonly Dictionary<string, (long Oid, decimal LimitPrice, decimal TrackedSize)>
            _limitCloseOrders = new();

        /// <summary>
        /// Called when a full asset scan starts/stops.
        /// Pauses the RSI mini-scan only — position fetch and monitor always run.
        /// </summary>
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

        /// <summary>Shortens an account name for the grid — "HL for Shorts" → "Shorts".</summary>
        private static string ShortAccountName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : name;
        }

        /// <summary>Resolves the account context that owns a position (or the primary if unknown).</summary>
        private AccountContext GetAccount(PositionInfo pos) =>
            (string.IsNullOrEmpty(pos.AccountName) ? null : _accountManager.GetByName(pos.AccountName))
            ?? _accountManager.Primary;

        /// <summary>Returns risk config for a position — checks its owning account first.</summary>
        private SymbolRiskConfig GetRiskConfig(PositionInfo pos) =>
            GetAccount(pos).GetRiskConfig(pos.Symbol);

        public PositionsPanel(AccountManager accountManager, AppConfig config)
        {
            _accountManager = accountManager;
            _config         = config;

            foreach (var account in _accountManager.Accounts)
            {
                if (account.Monitor == null) continue;
                account.Monitor.OrderPlaced += (sym, msg) => BeginInvoke(() => ShowOrderResult(sym, msg, true));
                account.Monitor.OrderFailed += (sym, msg) => BeginInvoke(() => ShowOrderResult(sym, msg, false));
                account.Monitor.SlWarning   += (sym, pnl) => BeginInvoke(() => HighlightSlWarning(sym));
            }

            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(20, 20, 20);

            BuildControls();

            // Start independent background loop — runs on thread pool, never blocked by UI or scanner
            _ = RunMonitorLoopAsync(_loopCts.Token);
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
                new DataGridViewTextBoxColumn { HeaderText = "Acct",     Name = "Acct",     MinimumWidth = 65  },
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
                new DataGridViewButtonColumn  { HeaderText = "🎯",        Name = "Bracket",  MinimumWidth = 36, Width = 36, FlatStyle = FlatStyle.Flat },
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

        // SR pre-order limit tracking: symbol → (order ID, time placed)
        // Cancelled automatically when position closes (if cancelonexit=true)
        // Grace period of 15s prevents race condition where refresh fires before new position is visible
        private readonly Dictionary<string, (long oid, DateTime placedAt)> _srLimitOrders
            = new(StringComparer.OrdinalIgnoreCase);

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            try
            {
                // ── Fetch fresh position data from ALL active accounts, merged & tagged ──
                var positions = await _accountManager.GetAllPositionsAsync(ct);

                // Fetch mid prices for watchlist symbols (any client — market data is global)
                try { _lastMids = await _accountManager.Primary.Client.GetAllMidsAsync(ct); }
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

                // ── Per-account housekeeping: SR cancels, bracket fills, limit-close sync, monitor ──
                foreach (var account in _accountManager.Accounts)
                {
                    var acctPositions = _positions
                        .Where(p => p.AccountName.Equals(account.Name, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    await CancelSRLimitsForClosedPositionsAsync(account, acctPositions, ct);

                    var prefix = account.Name + "::";
                    bool hasPending    = _pendingLimitEntries.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    bool hasLimitClose = _limitCloseOrders.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                    if (hasPending || hasLimitClose)
                    {
                        var openOrders = await account.Client.GetOpenOrdersAsync(ct);
                        await CheckPendingLimitEntriesAsync(account, acctPositions, openOrders, ct);
                        await SyncLimitCloseOrdersAsync(account, acctPositions, openOrders, ct);
                    }

                    // Run this account's monitor against its own positions only
                    if (account.Monitor != null && acctPositions.Count > 0)
                        await account.Monitor.CheckPositionsAsync(acctPositions, ct);
                }

                // Always refresh grid from cached data — even during scans
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(() =>
                    {
                        UpdateGrid(_positions, _lastMids);
                        PositionsRefreshed?.Invoke(_positions);
                    });
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

            var accountTag = $"  [{string.Join(" + ", _accountManager.Accounts.Select(a => a.Name))}]";

            if (positions.Count == 0)
            {
                _header.Text = $"Open Positions — none{accountTag}";
                return;
            }

            var visibleCount = positions.Count(p => !GetRiskConfig(p).GridViewDisabled);
            _header.Text = $"Open Positions — {visibleCount} open{accountTag}" +
                           (visibleCount < positions.Count ? $"  ({positions.Count - visibleCount} hidden)" : "");

            foreach (var p in positions)
            {
                p.Symbol = ResolveSymbol(p.Symbol); // resolve HIP-3 prefix if needed

                var account  = GetAccount(p);
                var riskCfg  = account.GetRiskConfig(p.Symbol);

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

                var status   = account.Monitor?.GetStatus(p.Symbol) ?? SymbolRiskStatus.Normal;
                var rsiSlope = account.Monitor?.GetRsiSlope(p.Symbol);
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
                    var exTrail = account.Monitor?.GetExchangeTrailingInfo(p.Symbol);
                    if (exTrail == null || !exTrail.Value.active)
                        trailText = $"📌 Wait ${riskCfg.TrailingMinProfitUsd:F2}";
                    else
                        trailText = $"📌 SL ${exTrail.Value.slPrice:G6}";
                }
                else if (riskCfg.TrailingEnabled)
                {
                    // App-side trailing
                    var trail = account.Monitor?.GetTrailingInfo(p.Symbol);
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
                    ShortAccountName(account.Name),
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

            // ── Watchlist: configured symbols (per account) with no open position ──
            var openKeys = positions
                .Select(p => DK(p.AccountName, p.Symbol))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var watchlist = new List<(AccountContext account, SymbolRiskConfig cfg)>();
            foreach (var account in _accountManager.Accounts)
            {
                var effectiveSymbolInfo = account.Config.SymbolInfo.Count > 0
                    ? account.Config.SymbolInfo
                    : _config.SymbolInfo;

                foreach (var s in effectiveSymbolInfo)
                {
                    if (!s.TradeEntryEnabled) continue;
                    if (s.Symbol.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)) continue;
                    if (openKeys.Contains(DK(account.Name, s.Symbol))) continue;
                    watchlist.Add((account, s));
                }
            }

            if (watchlist.Count == 0) return;

            // Separator row
            var sepIdx = _grid.Rows.Add("── Watchlist ──", "", "", "", "", "", "", "", "", "", "", "", "", "", "");
            _grid.Rows[sepIdx].Tag              = null; // no PositionInfo — click handlers skip it
            _grid.Rows[sepIdx].DefaultCellStyle.BackColor  = Color.FromArgb(28, 28, 28);
            _grid.Rows[sepIdx].DefaultCellStyle.ForeColor  = Color.FromArgb(80, 80, 80);
            _grid.Rows[sepIdx].DefaultCellStyle.Font       = new Font("Segoe UI", 8f, FontStyle.Italic);
            _grid.Rows[sepIdx].Height = 18;

            foreach (var (account, cfg) in watchlist)
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
                    Symbol      = sym,
                    AccountName = account.Name,
                    IsLong      = cfg.TradeIsLong,
                    MarkPrice   = markPrice,
                    Size        = 0
                };

                var direction = cfg.TradeIsLong ? "Long" : "Short";
                var wIdx = _grid.Rows.Add(
                    sym,
                    ShortAccountName(account.Name),
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

            // Separator row (null tag) or watchlist row (Size==0) — paint blank for Close/Bracket columns
            if (e.RowIndex >= 0 && colName is "Enter" or "Close" or "Bracket")
            {
                var rowTag = _grid.Rows[e.RowIndex].Tag;
                bool isNonPosition = rowTag == null ||
                                     (rowTag is PositionInfo wp && wp.Size == 0 && colName is "Close" or "Bracket");
                if (isNonPosition)
                {
                    e.PaintBackground(e.ClipBounds, true);
                    e.Handled = true;
                    return;
                }
            }

            // ── Bracket (set native TP/SL) button column ──────────────────────
            if (colName == "Bracket")
            {
                e.PaintBackground(e.ClipBounds, true);

                if (e.RowIndex < 0)
                {
                    using var headerFont = new Font("Segoe UI Symbol", 9f);
                    var txt  = "🎯";
                    var size = TextRenderer.MeasureText(txt, headerFont);
                    var x    = e.CellBounds.Left + (e.CellBounds.Width  - size.Width)  / 2;
                    var y    = e.CellBounds.Top  + (e.CellBounds.Height - size.Height) / 2;
                    TextRenderer.DrawText(e.Graphics, txt, headerFont,
                        new Point(x, y), Color.FromArgb(160, 160, 160));
                    e.Handled = true;
                    return;
                }

                var bb = new Rectangle(
                    e.CellBounds.Left + 4, e.CellBounds.Top + 3,
                    e.CellBounds.Width - 8, e.CellBounds.Height - 6);
                using var bbBrush = new SolidBrush(Color.FromArgb(35, 90, 110));
                e.Graphics.FillRectangle(bbBrush, bb);
                using var bbPen = new Pen(Color.FromArgb(70, 160, 195));
                e.Graphics.DrawRectangle(bbPen, bb);
                using var bbFont = new Font("Segoe UI Symbol", 8f);
                TextRenderer.DrawText(e.Graphics, "🎯", bbFont, bb,
                    Color.FromArgb(110, 200, 230),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
                    var rc = GetAccount(rp).GetRiskConfig(rp.Symbol);
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
                var account = GetAccount(pos);
                var riskCfg = account.GetRiskConfig(pos.Symbol);
                if (!riskCfg.TradeEntryEnabled) return;

                var direction = riskCfg.TradeIsLong ? "Long" : "Short";
                var notional  = riskCfg.TradeEntrySizeUsd * riskCfg.TradeLeverage;
                if (pos.MarkPrice <= 0) { ShowEntryError(pos.Symbol, "No price available — retry in a moment"); return; }
                var assetInfo = account.Client.GetAssetIndex(pos.Symbol);
                if (assetInfo == null) { ShowEntryError(pos.Symbol, $"Asset index not found for {pos.Symbol} — ensure the scanner has run at least once"); return; }
                var size      = Math.Round(notional / pos.MarkPrice,
                                    assetInfo.Value.szDecimals, MidpointRounding.ToZero);

                if (riskCfg.TradeOrderIsLimit)
                {
                    // ── Limit entry — show price dialog ──────────────────────
                    var sizeDesc = $"Size: {size:G6}  (~${size * pos.MarkPrice:F2} notional)";
                    using var dlg = new LimitPriceDialog(
                        $"Limit {direction} Entry — {pos.Symbol}",
                        pos.MarkPrice, riskCfg.TradeOrderOffsetUsd, sizeDesc, showBracket: true,
                        suggestedTpUsd: riskCfg.TpEnabled ? riskCfg.TpUsd : (decimal?)null,
                        suggestedSlUsd: riskCfg.SlEnabled ? riskCfg.SlUsd : (decimal?)null,
                        size: size, isLong: riskCfg.TradeIsLong);
                    if (dlg.ShowDialog(this) != DialogResult.OK || dlg.LimitPrice == null) return;
                    _ = EnterPositionAsync(account, pos, riskCfg, size, assetInfo.Value.szDecimals,
                                           assetInfo.Value.index, limitPrice: dlg.LimitPrice,
                                           tpPrice: dlg.TpPrice, slPrice: dlg.SlPrice);
                }
                else
                {
                    // ── Market entry — confirm dialog ─────────────────────────
                    var notionalUsd = size * pos.MarkPrice;
                    var confirm = MessageBox.Show(
                        $"Add {direction} {pos.Symbol}?\n\n" +
                        $"  Size:      {size:G6}\n" +
                        $"  Notional:  ~${notionalUsd:F2}\n" +
                        $"  Mark:      {pos.MarkPrice:G6}\n\n" +
                        "Size is calculated from tradeEntrySizeUsd × tradeLeverage.\n" +
                        "Actual position leverage is unchanged.",
                        $"Confirm Market {direction} Entry — {pos.Symbol}",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    if (confirm != DialogResult.Yes) return;
                    _ = EnterPositionAsync(account, pos, riskCfg, size, assetInfo.Value.szDecimals,
                                           assetInfo.Value.index);
                }
                return;
            }

            if (colName == "Bracket")
            {
                if (_grid.Rows[e.RowIndex].Tag is not PositionInfo bpos || bpos.Size == 0) return;
                var bAccount = GetAccount(bpos);
                var bRiskCfg = bAccount.GetRiskConfig(bpos.Symbol);
                _ = SetNativeBracketAsync(bAccount, bpos, bRiskCfg);
                return;
            }

            if (colName != "Close") return;
            if (_grid.Rows[e.RowIndex].Tag is not PositionInfo closePos) return;

            var closeAccount = GetAccount(closePos);
            var closeRiskCfg = closeAccount.GetRiskConfig(closePos.Symbol);
            if (closeRiskCfg.TradeCloseIsLimit)
            {
                // ── Limit close — show price dialog ──────────────────────────
                var pnlSign  = closePos.UnrealisedPnl >= 0 ? "+" : "";
                var sizeDesc = $"Size: {closePos.Size:G6}   PnL: {pnlSign}{closePos.UnrealisedPnl:F2} USD";
                using var dlg = new LimitPriceDialog(
                    $"Limit Close — {closePos.SideLabel} {closePos.Symbol}",
                    closePos.MarkPrice, closeRiskCfg.TradeCloseOffsetUsd, sizeDesc);
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.LimitPrice == null) return;
                _ = ClosePositionAsync(closeAccount, closePos, cancelSRLimit: true, limitPrice: dlg.LimitPrice);
            }
            else
            {
                // ── Market close — confirm dialog ─────────────────────────────
                var pnlSign      = closePos.UnrealisedPnl >= 0 ? "+" : "";
                var closeConfirm = MessageBox.Show(
                    $"Market-close {closePos.SideLabel} {closePos.Symbol}?\n\n" +
                    $"  Size:  {closePos.Size:G6}\n" +
                    $"  PnL:   {pnlSign}{closePos.UnrealisedPnl:F2} USD\n\n" +
                    "This will place a market IOC order at ±5% of mark price.",
                    "Confirm Market Close",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (closeConfirm != DialogResult.Yes) return;
                _ = ClosePositionAsync(closeAccount, closePos, cancelSRLimit: true);
            }
        }

        /// <summary>Shows a soft amber SR note in the header — does not imply entry failure.</summary>
        private void ShowSRNote(string message)
        {
            Serilog.Log.Information("[SR Preorder] {Msg}", message);
            _header.Text      = $"📌 {message}";
            _header.ForeColor = Color.FromArgb(255, 200, 60);
            var t = new System.Windows.Forms.Timer { Interval = 5_000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); _header.ForeColor = Color.Silver; };
            t.Start();
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

        private async Task EnterPositionAsync(AccountContext account, PositionInfo pos, SymbolRiskConfig riskCfg,
                                               decimal size, int szDecimals, int assetIndex,
                                               decimal? limitPrice = null,
                                               decimal? tpPrice    = null,
                                               decimal? slPrice    = null)
        {
            try
            {
                var isBuy     = riskCfg.TradeIsLong;
                var isLimit   = limitPrice.HasValue;
                var orderDesc = isLimit ? $"Limit @ {limitPrice:G8}" : "Market";

                // ── SR pre-order limit (if configured) ───────────────────────
                if (riskCfg.PreorderAtSREnabled && riskCfg.PreorderAtSRSizeUsd > 0)
                    await PlaceSRPreorderAsync(account, pos, riskCfg, isBuy, assetIndex, szDecimals);

                // ── Entry order ───────────────────────────────────────────────
                bool ok; string msg; long oid = 0;
                if (isLimit)
                {
                    (ok, msg, oid) = await account.Client.PlaceLimitEntryAsync(
                        pos.Symbol, isBuy, limitPrice!.Value, size, szDecimals);
                }
                else
                {
                    (ok, msg) = await account.Client.PlaceMarketEntryAsync(
                        pos.Symbol, isBuy, pos.MarkPrice, size, szDecimals);
                }

                if (ok)
                {
                    account.Monitor?.ClearSymbol(pos.Symbol);

                    // Track pending limit entry for bracket placement on fill
                    if (isLimit && oid > 0 && (tpPrice.HasValue || slPrice.HasValue))
                    {
                        _pendingLimitEntries[DK(account.Name, pos.Symbol)] =
                            new PendingBracket(oid, tpPrice, slPrice, szDecimals, isBuy, account.Name);
                        Serilog.Log.Information(
                            "[Bracket] {Symbol} pending — oid={Oid} TP={Tp} SL={Sl}",
                            pos.Symbol, oid, tpPrice, slPrice);
                    }
                }

                BeginInvoke(() =>
                {
                    if (ok)
                        ShowOrderResult(pos.Symbol,
                            $"✓ {(isBuy ? "Long" : "Short")} {orderDesc} entry {pos.Symbol}  size {size:G6}", true);
                    else
                        ShowEntryError(pos.Symbol, msg);
                });
            }
            catch (Exception ex)
            {
                BeginInvoke(() => ShowEntryError(pos.Symbol, ex.Message));
            }
        }

        private async Task PlaceSRPreorderAsync(AccountContext account, PositionInfo pos, SymbolRiskConfig riskCfg,
                                                 bool isBuy, int assetIndex, int szDecimals)
        {
            try
            {
                // Check if an SR-style limit already exists on the exchange
                var existingOid = await account.Client.FindExistingSRLimitAsync(
                    pos.Symbol, isBuy, pos.MarkPrice);

                if (existingOid.HasValue)
                {
                    // Track it so we can cancel on exit, and skip placing a new one
                    _srLimitOrders[DK(account.Name, pos.Symbol)] = (existingOid.Value, DateTime.UtcNow);
                    Serilog.Log.Information(
                        "[SR Preorder] {Symbol} existing limit found oid={Oid} — skipping new order",
                        pos.Symbol, existingOid.Value);
                    BeginInvoke(() => ShowOrderResult(pos.Symbol,
                        $"📌 SR limit already exists (oid {existingOid.Value})", true));
                    return;
                }

                var candles = await account.Client.GetCandlesAsync(pos.Symbol, riskCfg.SRTimeframe, 100);
                if (candles == null || candles.Count == 0) return;

                decimal? srLevel = isBuy
                    ? SwingDetector.FindNearestSupport(candles, pos.MarkPrice)
                    : SwingDetector.FindNearestResistance(candles, pos.MarkPrice);

                Serilog.Log.Information(
                    "[SR Preorder] {Symbol} mark={Mark:G6} isBuy={IsBuy} srLevel={SR} candles={N}",
                    pos.Symbol, pos.MarkPrice, isBuy, srLevel?.ToString("G6") ?? "none", candles.Count);

                if (srLevel == null)
                {
                    Serilog.Log.Information("[SR Preorder] No swing level found for {Symbol} on {TF}",
                        pos.Symbol, riskCfg.SRTimeframe);
                    BeginInvoke(() => ShowSRNote($"No SR level found on {riskCfg.SRTimeframe} — market only"));
                    return;
                }

                // Apply offset: shorts go slightly above resistance, longs slightly below support
                var limitPrice = isBuy
                    ? srLevel.Value * (1 - riskCfg.PreorderAtSROffsetPct)
                    : srLevel.Value * (1 + riskCfg.PreorderAtSROffsetPct);

                var notional  = riskCfg.PreorderAtSRSizeUsd * riskCfg.TradeLeverage;
                var limitSize = Math.Round(notional / limitPrice, szDecimals, MidpointRounding.ToZero);
                if (limitSize <= 0) return;

                var (ok, msg, oid) = await account.Client.PlaceLimitEntryAsync(
                    pos.Symbol, isBuy, limitPrice, limitSize, szDecimals);

                if (ok)
                {
                    _srLimitOrders[DK(account.Name, pos.Symbol)] = (oid, DateTime.UtcNow);
                    Serilog.Log.Information(
                        "[SR Preorder] {Symbol} limit {Side} @ {Price:G6}  size={Size}  oid={Oid}",
                        pos.Symbol, isBuy ? "BUY" : "SELL", limitPrice, limitSize, oid);
                    BeginInvoke(() => ShowOrderResult(pos.Symbol,
                        $"📌 SR limit @ {limitPrice:G6}", true));
                }
                else
                {
                    Serilog.Log.Warning("[SR Preorder] {Symbol} limit failed: {Msg}", pos.Symbol, msg);
                    BeginInvoke(() => ShowSRNote($"SR limit failed for {pos.Symbol} — market order only"));
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning("[SR Preorder] {Symbol} error: {Msg}", pos.Symbol, ex.Message);
            }
        }

        private static readonly TimeSpan SRGracePeriod = TimeSpan.FromSeconds(15);

        private async Task CancelSRLimitsForClosedPositionsAsync(
            AccountContext account, List<PositionInfo> openPositions, CancellationToken ct)
        {
            if (_srLimitOrders.Count == 0) return;
            var openKeys = openPositions.Select(p => DK(p.AccountName, p.Symbol))
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prefix = account.Name + "::";
            foreach (var key in _srLimitOrders.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (openKeys.Contains(key)) continue;
                var symbol  = key.Substring(prefix.Length);
                var riskCfg = account.GetRiskConfig(symbol);
                if (!riskCfg.PreorderAtSRCancelOnExit) continue;

                // Grace period — don't cancel if limit was placed very recently.
                // Prevents race where refresh fires before new position appears in API response.
                if (DateTime.UtcNow - _srLimitOrders[key].placedAt < SRGracePeriod)
                {
                    Serilog.Log.Debug("[SR Preorder] {Symbol} cancel suppressed — within grace period", symbol);
                    continue;
                }

                await CancelSRLimitAsync(account, symbol, ct);
            }
        }

        private async Task CancelSRLimitAsync(AccountContext account, string symbol, CancellationToken ct = default)
        {
            var key = DK(account.Name, symbol);
            if (!_srLimitOrders.TryGetValue(key, out var entry)) return;
            var assetInfo = account.Client.GetAssetIndex(symbol);
            if (assetInfo != null)
            {
                var (ok, msg) = await account.Client.CancelOrderAsync(assetInfo.Value.index, entry.oid, ct);
                Serilog.Log.Information("[SR Preorder] Cancel {Symbol} oid={Oid}: {Result}",
                    symbol, entry.oid, ok ? "ok" : msg);
            }
            _srLimitOrders.Remove(key);
        }

        private async Task ClosePositionAsync(AccountContext account, PositionInfo pos, bool cancelSRLimit = false,
                                               decimal? limitPrice = null)
        {
            try
            {
                var isBuy     = !pos.IsLong;
                var isLimit   = limitPrice.HasValue;
                var assetInfo = account.Client.GetAssetIndex(pos.Symbol);
                if (assetInfo == null)
                {
                    BeginInvoke(() => ShowOrderResult(pos.Symbol,
                        $"⚠ Close failed — asset index not found for {pos.Symbol}", false));
                    return;
                }

                if (cancelSRLimit) await CancelSRLimitAsync(account, pos.Symbol);

                // Cancel any tracked limit close order for this symbol
                var closeKey = DK(account.Name, pos.Symbol);
                if (_limitCloseOrders.TryGetValue(closeKey, out var lco) && assetInfo != null)
                {
                    await account.Client.CancelOrderAsync(assetInfo.Value.index, lco.Oid, ct: default);
                    _limitCloseOrders.Remove(closeKey);
                }

                bool ok; string msg; long closeOid = 0;
                if (isLimit)
                {
                    (ok, msg, closeOid) = await account.Client.PlaceLimitCloseAsync(
                        pos.Symbol, isBuy, limitPrice!.Value, pos.Size, assetInfo.Value.szDecimals);
                }
                else
                {
                    (ok, msg) = await account.Client.PlaceMarketCloseAsync(
                        pos.Symbol, isBuy, pos.MarkPrice, pos.Size, assetInfo.Value.szDecimals);
                }

                if (ok)
                {
                    account.Monitor?.ClearSymbol(pos.Symbol);

                    // Track limit close order for size-sync on DCA
                    if (isLimit && closeOid > 0)
                    {
                        _limitCloseOrders[closeKey] = (closeOid, limitPrice!.Value, pos.Size);
                        Serilog.Log.Information(
                            "[LimitClose] {Symbol} tracking oid={Oid} price={Price} size={Size}",
                            pos.Symbol, closeOid, limitPrice.Value, pos.Size);
                    }
                }

                var orderDesc = isLimit ? $"Limit close @ {limitPrice:G8}" : "Closed";
                BeginInvoke(() => ShowOrderResult(pos.Symbol,
                    ok ? $"✓ {orderDesc} {pos.Symbol}" : $"✗ Close failed: {msg}", ok));
            }
            catch (Exception ex)
            {
                BeginInvoke(() => ShowOrderResult(pos.Symbol,
                    $"✗ Close error: {ex.Message}", false));
            }
        }

        // ── Bracket fill detection ────────────────────────────────────────────

        /// <summary>
        /// Checks whether any pending limit entry orders have been filled.
        /// If filled and TP/SL prices were set, places bracket trigger orders.
        /// </summary>
        private async Task CheckPendingLimitEntriesAsync(
            AccountContext account,
            List<PositionInfo> positions,
            List<(long oid, int assetIndex, bool isBuy, decimal price, decimal size)> openOrders,
            CancellationToken ct)
        {
            var openOids = new HashSet<long>(openOrders.Select(o => o.oid));
            var prefix   = account.Name + "::";

            foreach (var (key, pending) in _pendingLimitEntries.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (openOids.Contains(pending.Oid)) continue; // still resting

                var symbol = key.Substring(prefix.Length);

                // Order gone — did a position appear? (filled) or not (cancelled)
                _pendingLimitEntries.Remove(key);
                var pos = positions.FirstOrDefault(p =>
                    p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

                if (pos == null)
                {
                    Serilog.Log.Information(
                        "[Bracket] {Symbol} limit entry oid={Oid} gone — cancelled/rejected, no bracket",
                        symbol, pending.Oid);
                    continue;
                }

                Serilog.Log.Information(
                    "[Bracket] {Symbol} limit entry oid={Oid} filled — placing bracket TP={Tp} SL={Sl}",
                    symbol, pending.Oid, pending.TpPrice, pending.SlPrice);

                await PlaceManualBracketAsync(account, symbol, pos, pending, ct);
            }
        }

        /// <summary>
        /// Places TP and/or SL trigger orders for a freshly filled limit entry.
        /// TP = limit trigger (non-market).  SL = market trigger.
        /// </summary>
        private async Task PlaceManualBracketAsync(
            AccountContext account, string symbol, PositionInfo pos, PendingBracket pending, CancellationToken ct)
        {
            var assetInfo = account.Client.GetAssetIndex(symbol);
            if (assetInfo == null)
            {
                Serilog.Log.Warning("[Bracket] {Symbol} — asset index not found, bracket skipped", symbol);
                return;
            }

            // To CLOSE a long: sell (isBuy=false).  To CLOSE a short: buy (isBuy=true).
            var closingBuy = !pending.IsLong;
            bool tpOk = true, slOk = true;

            if (pending.TpPrice.HasValue)
            {
                (tpOk, _, _) = await account.Client.PlaceTriggerOrderAsync(
                    symbol, closingBuy,
                    pending.TpPrice.Value, pending.TpPrice.Value,
                    pos.Size, pending.SzDecimals, "tp", isMarket: false, ct);
            }

            if (pending.SlPrice.HasValue)
            {
                (slOk, _, _) = await account.Client.PlaceTriggerOrderAsync(
                    symbol, closingBuy,
                    pending.SlPrice.Value, pending.SlPrice.Value,
                    pos.Size, pending.SzDecimals, "sl", isMarket: true, ct);
            }

            var result = $"🎯 Bracket {symbol}  " +
                         (pending.TpPrice.HasValue ? $"TP {pending.TpPrice:G8} {(tpOk ? "✓" : "✗")}  " : "") +
                         (pending.SlPrice.HasValue ? $"SL {pending.SlPrice:G8} {(slOk ? "✓" : "✗")}" : "");

            Serilog.Log.Information("[Bracket] {Msg}", result);
            BeginInvoke(() => ShowOrderResult(symbol, result, tpOk && slOk));
        }

        /// <summary>
        /// Opens a dialog to retroactively place native exchange-side TP/SL trigger orders
        /// on an EXISTING open position — for positions opened outside LiquidScanner's own
        /// limit-entry flow (e.g. manually on the exchange, or via a market entry), which
        /// never get the automatic bracket-on-fill treatment. The dialog pre-fills suggested
        /// prices from the symbol's configured tpUsd/slUsd PnL thresholds, fully editable.
        /// </summary>
        private async Task SetNativeBracketAsync(AccountContext account, PositionInfo pos, SymbolRiskConfig riskCfg)
        {
            var assetInfo = account.Client.GetAssetIndex(pos.Symbol);
            if (assetInfo == null)
            {
                ShowOrderResult(pos.Symbol, $"🎯 {pos.Symbol}: asset index not found — bracket skipped", false);
                return;
            }

            var pnlSign  = pos.UnrealisedPnl >= 0 ? "+" : "";
            var sizeDesc = $"Size: {pos.Size:G6} {pos.Symbol}   PnL: {pnlSign}{pos.UnrealisedPnl:F2} USD";

            using var dlg = new PositionBracketDialog(
                $"Set Native TP/SL — {pos.SideLabel} {pos.Symbol}",
                pos.EntryPrice, pos.MarkPrice, sizeDesc,
                suggestedTpUsd: riskCfg.TpEnabled ? riskCfg.TpUsd : (decimal?)null,
                suggestedSlUsd: riskCfg.SlEnabled ? riskCfg.SlUsd : (decimal?)null,
                size: pos.Size, isLong: pos.IsLong);

            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (dlg.TpPrice == null && dlg.SlPrice == null) return;

            // To CLOSE a long: sell (isBuy=false).  To CLOSE a short: buy (isBuy=true).
            var closingBuy = !pos.IsLong;
            bool tpOk = true, slOk = true;

            if (dlg.TpPrice.HasValue)
            {
                (tpOk, _, _) = await account.Client.PlaceTriggerOrderAsync(
                    pos.Symbol, closingBuy,
                    dlg.TpPrice.Value, dlg.TpPrice.Value,
                    pos.Size, assetInfo.Value.szDecimals, "tp", isMarket: false, CancellationToken.None);
            }

            if (dlg.SlPrice.HasValue)
            {
                (slOk, _, _) = await account.Client.PlaceTriggerOrderAsync(
                    pos.Symbol, closingBuy,
                    dlg.SlPrice.Value, dlg.SlPrice.Value,
                    pos.Size, assetInfo.Value.szDecimals, "sl", isMarket: true, CancellationToken.None);
            }

            var result = $"🎯 Native bracket {pos.Symbol}  " +
                         (dlg.TpPrice.HasValue ? $"TP {dlg.TpPrice:G8} {(tpOk ? "✓" : "✗")}  " : "") +
                         (dlg.SlPrice.HasValue ? $"SL {dlg.SlPrice:G8} {(slOk ? "✓" : "✗")}" : "");

            Serilog.Log.Information("[Bracket] (manual/retroactive) {Msg}", result);
            ShowOrderResult(pos.Symbol, result, tpOk && slOk);
        }

        // ── Limit close size sync ─────────────────────────────────────────────

        /// <summary>
        /// Keeps tracked limit close orders in sync with the current position size.
        /// If position size changes (DCA), the old limit close is cancelled and replaced.
        /// If the limit close order fills or position disappears, tracking is removed.
        /// </summary>
        private async Task SyncLimitCloseOrdersAsync(
            AccountContext account,
            List<PositionInfo> positions,
            List<(long oid, int assetIndex, bool isBuy, decimal price, decimal size)> openOrders,
            CancellationToken ct)
        {
            var openOids = new HashSet<long>(openOrders.Select(o => o.oid));
            var prefix   = account.Name + "::";

            foreach (var (key, (oid, limitPrice, trackedSize)) in _limitCloseOrders
                         .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var symbol    = key.Substring(prefix.Length);
                var assetInfo = account.Client.GetAssetIndex(symbol);

                // Order no longer on the book — filled or cancelled externally
                if (!openOids.Contains(oid))
                {
                    _limitCloseOrders.Remove(key);
                    Serilog.Log.Information(
                        "[LimitClose] {Symbol} oid={Oid} gone from open orders — tracking removed", symbol, oid);
                    continue;
                }

                var pos = positions.FirstOrDefault(p =>
                    p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

                // Position closed — cancel the resting limit close
                if (pos == null)
                {
                    if (assetInfo != null)
                        await account.Client.CancelOrderAsync(assetInfo.Value.index, oid, ct);
                    _limitCloseOrders.Remove(key);
                    Serilog.Log.Information(
                        "[LimitClose] {Symbol} position gone — limit close oid={Oid} cancelled", symbol, oid);
                    continue;
                }

                // Position size changed (DCA) — cancel and replace with new size
                if (pos.Size != trackedSize && assetInfo != null)
                {
                    await account.Client.CancelOrderAsync(assetInfo.Value.index, oid, ct);

                    var isBuy = !pos.IsLong;
                    var (ok, _, newOid) = await account.Client.PlaceLimitCloseAsync(
                        symbol, isBuy, limitPrice, pos.Size, assetInfo.Value.szDecimals, ct);

                    if (ok && newOid > 0)
                    {
                        _limitCloseOrders[key] = (newOid, limitPrice, pos.Size);
                        Serilog.Log.Information(
                            "[LimitClose] {Symbol} size {Old}→{New} — limit close updated oid={Oid}",
                            symbol, trackedSize, pos.Size, newOid);
                        BeginInvoke(() => ShowOrderResult(symbol,
                            $"📌 Limit close updated — {pos.Size:G6} @ {limitPrice:G8}", true));
                    }
                    else
                    {
                        _limitCloseOrders.Remove(key);
                        Serilog.Log.Warning(
                            "[LimitClose] {Symbol} failed to replace limit close after size change", symbol);
                    }
                }
            }
        }

        /// <summary>
        /// Independent background loop running on the thread pool.
        /// Polls positions and runs the monitor every 5 seconds regardless of
        /// whether the asset scanner is running — never blocked by the UI thread.
        /// </summary>
        private async Task RunMonitorLoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                    await RefreshAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PositionsPanel] monitor loop crashed: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _loopCts.Cancel();
                _loopCts.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
