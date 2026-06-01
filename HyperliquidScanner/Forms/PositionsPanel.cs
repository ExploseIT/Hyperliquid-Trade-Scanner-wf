using HyperliquidScanner.Models;
using HyperliquidScanner.Services;

namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Bottom panel showing open Hyperliquid positions with live PnL.
    /// Polls the clearinghouseState API every 5 seconds.
    /// Read-only in Phase 1 — risk management (auto SL/TP) added in Phase 2.
    /// </summary>
    public class PositionsPanel : Panel
    {
        private readonly HyperliquidClient _client;
        private readonly AppConfig         _config;

        private DataGridView                _grid    = null!;
        private Label                       _header  = null!;
        private System.Windows.Forms.Timer  _timer   = null!;

        private List<PositionInfo> _positions  = new();
        private HashSet<string>    _hip3Assets = new(StringComparer.OrdinalIgnoreCase);

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

        public PositionsPanel(HyperliquidClient client, AppConfig config)
        {
            _client = client;
            _config = config;

            Dock      = DockStyle.Bottom;
            Height    = 155;
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
                new DataGridViewTextBoxColumn { HeaderText = "Symbol",    Name = "Symbol",   MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "Side",      Name = "Side",     MinimumWidth = 60  },
                new DataGridViewTextBoxColumn { HeaderText = "Size",      Name = "Size",     MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "Entry",     Name = "Entry",    MinimumWidth = 100 },
                new DataGridViewTextBoxColumn { HeaderText = "Mark",      Name = "Mark",     MinimumWidth = 100 },
                new DataGridViewTextBoxColumn { HeaderText = "PnL $",     Name = "PnlUsd",   MinimumWidth = 90  },
                new DataGridViewTextBoxColumn { HeaderText = "PnL %",     Name = "PnlPct",   MinimumWidth = 75  },
                new DataGridViewTextBoxColumn { HeaderText = "Liq Price", Name = "LiqPrice", MinimumWidth = 100 },
                new DataGridViewTextBoxColumn { HeaderText = "Leverage",  Name = "Leverage", MinimumWidth = 90  },
                new DataGridViewTextBoxColumn { HeaderText = "Margin",    Name = "Margin",   MinimumWidth = 90  }
            );

            foreach (DataGridViewColumn col in _grid.Columns)
                col.SortMode = DataGridViewColumnSortMode.NotSortable;

            _grid.CellFormatting += Grid_CellFormatting;

            Controls.Add(_grid);
            Controls.Add(_header);
        }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            try
            {
                var positions = await _client.GetPositionsAsync(ct);
                _positions = positions;

                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(() =>
                {
                    UpdateGrid(positions);
                    PositionsRefreshed?.Invoke(positions);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PositionsPanel] refresh error: {ex.Message}");
            }
        }

        private void UpdateGrid(List<PositionInfo> positions)
        {
            _grid.Rows.Clear();

            if (positions.Count == 0)
            {
                _header.Text = "Open Positions — none";
                return;
            }

            _header.Text = $"Open Positions — {positions.Count} open";

            foreach (var p in positions)
            {
                p.Symbol = ResolveSymbol(p.Symbol); // resolve HIP-3 prefix if needed

                var pnlSign  = p.UnrealisedPnl >= 0 ? "+" : "";
                var pctSign  = p.PnlPercent    >= 0 ? "+" : "";
                var liqText  = p.LiquidationPrice.HasValue
                             ? p.LiquidationPrice.Value.ToString("G6")
                             : "–";

                var idx = _grid.Rows.Add(
                    p.Symbol,
                    p.SideLabel,
                    p.Size.ToString("G6"),
                    p.EntryPrice.ToString("G6"),
                    p.MarkPrice.ToString("G6"),
                    $"{pnlSign}{p.UnrealisedPnl:F2}",
                    $"{pctSign}{p.PnlPercent * 100:F2}%",
                    liqText,
                    p.LeverageLabel,
                    $"{p.MarginUsed:F2}"
                );

                _grid.Rows[idx].Tag = p;
            }
        }

        private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            if (_grid.Rows[e.RowIndex].Tag is not PositionInfo p) return;

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
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }
    }
}
