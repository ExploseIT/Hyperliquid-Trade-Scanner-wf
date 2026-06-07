using HyperliquidScanner.Models;
using HyperliquidScanner.Services;
using HyperliquidScanner.Utils;
using Serilog;
using static HyperliquidScanner.Utils.AudioPlayer;

namespace HyperliquidScanner.Forms
{
    public class MainForm : Form
    {
        // Services
        private readonly AppConfig         _config;
        private readonly AppSettings       _appSettings;
        private readonly HyperliquidClient _client;
        private readonly ScannerService    _scanner;
        private readonly CoinglassClient?  _coinglass;

        // Custom alert sounds (null = fall back to system sound)
        private readonly AudioPlayer? _squeezeSound;
        private readonly AudioPlayer? _cascadeSound;
        private readonly AudioPlayer? _rsiLowerLowSound;
        private readonly AudioPlayer? _slTriggeredSound;
        private readonly AudioPlayer? _entryPlacedSound;
        private readonly AudioPlayer? _entryFilledSound;
        private readonly AutoEntryManager? _autoEntry;

        // UI Controls
        private ComboBox          _timeframeCombo   = null!;
        private Button            _scanButton       = null!;
        private Button            _cancelButton     = null!;
        private DataGridView      _grid             = null!;
        private ProgressBar       _progressBar      = null!;
        private Label             _statusLabel      = null!;
        private Label             _lastScanLabel    = null!;
        private Label             _connectionLabel  = null!;
        private Label             _portfolioLabel   = null!;
        private ComboBox          _filterCombo         = null!;
        private TextBox           _searchBox           = null!;
        private ComboBox          _autoRefreshCombo    = null!;
        private System.Windows.Forms.Timer _autoRefreshTimer = null!;
        private Label             _alertBar            = null!;  // dedicated RSI-LL alert strip
        private LiquidationPanel? _liqPanel;
        private PositionsPanel?   _positionsPanel;
        private readonly AccountManager? _accountManager;

        private readonly BinanceLiquidationFeed?  _binanceFeed;
        private readonly BybitLiquidationFeed?   _bybitFeed;
        private readonly LiquidationAggregator?  _aggregator;
        private CancellationTokenSource? _cts;
        private List<AssetScanResult>    _lastResults = new();

        // Config hot-reload
        private FileSystemWatcher?                 _configWatcher;
        private FileSystemWatcher?                 _sourceConfigWatcher;
        private System.Windows.Forms.Timer?        _configReloadDebounce;
        private System.Windows.Forms.Timer?        _configPollTimer;
        private DateTime                           _lastConfigWrite = DateTime.MinValue;

        // Latest 5-min leaderboard snapshot — keyed by OKX BaseSymbol (upper-case)
        private Dictionary<string, LiquidationAggregator.SymbolSummary> _liqSummaries
            = new(StringComparer.OrdinalIgnoreCase);

        public MainForm(AppConfig config, AppSettings appSettings, AccountManager accountManager,
                        ScannerService scanner,
                        AutoEntryManager? autoEntry = null, CoinglassClient? coinglass = null)
        {
            _config         = config;
            _appSettings    = appSettings;
            _client         = accountManager.Primary.Client;
            _scanner        = scanner;
            _accountManager = accountManager;
            _coinglass      = coinglass;

            // Load custom alert sounds — fall back to system sounds if files not found
            var squeezePath     = AppSettingsLoader.ResolveSoundPath(_appSettings.SqueezeSoundFile);
            var cascadePath     = AppSettingsLoader.ResolveSoundPath(_appSettings.CascadeSoundFile);
            var rsiLLPath       = AppSettingsLoader.ResolveSoundPath(_appSettings.RsiLowerLowSoundFile);
            var slTriggeredPath = AppSettingsLoader.ResolveSoundPath(_appSettings.SlTriggeredSoundFile);
            var entryPlacedPath = AppSettingsLoader.ResolveSoundPath(_appSettings.EntryPlacedSoundFile);
            var entryFilledPath = AppSettingsLoader.ResolveSoundPath(_appSettings.EntryFilledSoundFile);
            _squeezeSound       = squeezePath     != null ? new AudioPlayer(squeezePath)     : null;
            _cascadeSound       = cascadePath     != null ? new AudioPlayer(cascadePath)     : null;
            _rsiLowerLowSound   = rsiLLPath       != null ? new AudioPlayer(rsiLLPath)       : null;
            _slTriggeredSound   = slTriggeredPath  != null ? new AudioPlayer(slTriggeredPath)  : null;
            _entryPlacedSound   = entryPlacedPath  != null ? new AudioPlayer(entryPlacedPath)  : null;
            _entryFilledSound   = entryFilledPath != null ? new AudioPlayer(entryFilledPath) : null;
            ApplySoundVolume(_appSettings.SoundVolume);

            // Wire SL sound alert from all active account monitors
            foreach (var account in accountManager.Accounts)
            {
                if (account.Monitor == null) continue;
                account.Monitor.SlFiredAlert += sym => BeginInvoke(() =>
                {
                    try { if (_slTriggeredSound != null) _slTriggeredSound.Play();
                          else System.Media.SystemSounds.Hand.Play(); }
                    catch { }
                });
            }

            _autoEntry = autoEntry;
            if (_autoEntry != null)
            {
                _autoEntry.EntryPlaced   += (sym, msg) => BeginInvoke(() => OnAutoEntryEvent(msg, Color.FromArgb(100, 180, 255)));
                _autoEntry.EntryFilled   += (sym, msg) => BeginInvoke(() => { OnAutoEntryEvent(msg, Color.FromArgb(80, 220, 130)); _entryFilledSound?.Play(); });
                _autoEntry.EntryFailed   += (sym, msg) => BeginInvoke(() => OnAutoEntryEvent(msg, Color.FromArgb(220, 80, 80)));
                _autoEntry.BracketPlaced += (sym, msg) => BeginInvoke(() => OnAutoEntryEvent(msg, Color.FromArgb(80, 220, 130)));
            }


            // Start the OKX live liquidation feed + aggregator (free public WebSocket)
            if (coinglass != null)
            {
                _binanceFeed = new BinanceLiquidationFeed();
                _binanceFeed.Start();
                _aggregator  = new LiquidationAggregator(_binanceFeed);

                _bybitFeed = new BybitLiquidationFeed();
                _bybitFeed.Start();
                _aggregator.AttachFeed(_bybitFeed);

                _aggregator.LeaderboardUpdated += (summaries, _) =>
                {
                    var dict = summaries.ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
                    if (!IsHandleCreated || IsDisposed) return;
                    BeginInvoke(() =>
                    {
                        _liqSummaries = dict;
                        UpdateGridLiqColumn();
                    });
                };

                _aggregator.BurstDetected += burst =>
                {
                    if (!IsHandleCreated || IsDisposed) return;
                    BeginInvoke(() => OnBurstAlert(burst));
                };
            }

            InitialiseComponent();

            // Wire feed + aggregator to panel after controls are built
            if (_binanceFeed != null && _liqPanel != null)
            {
                _liqPanel.SetFeed(_binanceFeed);
                if (_bybitFeed != null)
                    _liqPanel.SetBybitFeed(_bybitFeed);
                if (_aggregator != null)
                    _liqPanel.SetAggregator(_aggregator);
            }

            Load += async (_, _) => await TestConnectionsOnStartupAsync();
        }

        // Form layout

        private void InitialiseComponent()
        {
            SuspendLayout();

            Text          = "Liquid Scanner";
            Size          = new Size(_coinglass != null ? 1280 : 980, 700);
            MinimumSize   = new Size(800, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Font          = new Font("Segoe UI", 9f);

            // Liquidation panel (right side, only if Coinglass key is configured)
            if (_coinglass != null)
            {
                _liqPanel = new LiquidationPanel(_coinglass);
                Controls.Add(_liqPanel);
            }

            // Top toolbar
            var toolbar = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 52,
                Padding   = new Padding(10, 8, 10, 8),
                BackColor = Color.FromArgb(30, 30, 30)
            };

            var tfLabel = new Label
            {
                Text      = "Timeframe:",
                ForeColor = Color.Silver,
                AutoSize  = true,
                Location  = new Point(10, 14)
            };

            _timeframeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width         = 130,
                Location      = new Point(80, 11)
            };
            foreach (var (display, _) in Timeframes.All)
                _timeframeCombo.Items.Add(display);

            var defaultIndex = Array.FindIndex(Timeframes.All,
                t => t.ApiValue == _config.DefaultTimeframe);
            _timeframeCombo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 1;

            _scanButton = new Button
            {
                Text      = "Scan",
                Width     = 80,
                Height    = 30,
                Location  = new Point(230, 10),
                BackColor = Color.FromArgb(0, 122, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _scanButton.FlatAppearance.BorderSize = 0;
            _scanButton.Click += ScanButton_Click;

            _cancelButton = new Button
            {
                Text      = "Stop",
                Width     = 70,
                Height    = 30,
                Location  = new Point(320, 10),
                BackColor = Color.FromArgb(160, 40, 40),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Enabled   = false
            };
            _cancelButton.FlatAppearance.BorderSize = 0;
            _cancelButton.Click += (_, _) => _cts?.Cancel();

            _filterCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width         = 110,
                Location      = new Point(410, 11)
            };
            _filterCombo.Items.AddRange(new object[] { "All assets", "Bullish only", "Bearish only", "Alerts only" });
            _filterCombo.SelectedIndex = 1; // default: Bullish only
            _filterCombo.SelectedIndexChanged += (_, _) => RefreshGrid();

            _searchBox = new TextBox
            {
                Width            = 140,
                Height           = 24,
                Location         = new Point(530, 12),
                BackColor        = Color.FromArgb(45, 45, 45),
                ForeColor        = Color.Silver,
                BorderStyle      = BorderStyle.FixedSingle,
                PlaceholderText  = "Filter asset..."
            };
            _searchBox.TextChanged += (_, _) => RefreshGrid();

            var autoLabel = new Label
            {
                Text      = "Auto:",
                ForeColor = Color.Silver,
                AutoSize  = true,
                Location  = new Point(690, 14)
            };

            _autoRefreshCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width         = 90,
                Location      = new Point(730, 11)
            };
            _autoRefreshCombo.Items.AddRange(new object[] { "Off", "1 min", "2 min", "5 min", "10 min" });
            _autoRefreshCombo.SelectedIndex = 0;

            _autoRefreshTimer = new System.Windows.Forms.Timer();
            _autoRefreshTimer.Tick += (_, _) =>
            {
                if (_scanButton.Enabled) // don't stack scans
                    ScanButton_Click(null, EventArgs.Empty);
            };
            _autoRefreshCombo.SelectedIndexChanged += (_, _) =>
            {
                _autoRefreshTimer.Stop();
                var ms = _autoRefreshCombo.SelectedIndex switch
                {
                    1 => 60_000,
                    2 => 120_000,
                    3 => 300_000,
                    4 => 600_000,
                    _ => 0
                };
                if (ms > 0) { _autoRefreshTimer.Interval = ms; _autoRefreshTimer.Start(); }
            };

            _connectionLabel = new Label
            {
                Text      = "Connecting...",
                ForeColor = Color.Gray,
                AutoSize  = true,
                Anchor    = AnchorStyles.Right | AnchorStyles.Top
            };
            _connectionLabel.Location = new Point(toolbar.Width - 300, 15);
            toolbar.SizeChanged += (_, _) =>
            {
                _connectionLabel.Location = new Point(toolbar.Width - 300, 15);
                _portfolioLabel.Location  = new Point(toolbar.Width - 300, 15);
            };

            _portfolioLabel = new Label
            {
                Text      = string.Empty,
                ForeColor = Color.FromArgb(80, 220, 130),
                AutoSize  = true,
                Anchor    = AnchorStyles.Right | AnchorStyles.Top,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _portfolioLabel.Location = new Point(toolbar.Width - 300, 15);

            toolbar.Controls.AddRange(new Control[]
            {
                tfLabel, _timeframeCombo, _scanButton, _cancelButton,
                _filterCombo, _searchBox, autoLabel, _autoRefreshCombo,
                _connectionLabel, _portfolioLabel
            });

            // Status bar
            var statusBar = new Panel
            {
                Dock      = DockStyle.Bottom,
                Height    = 30,
                BackColor = Color.FromArgb(25, 25, 25),
                Padding   = new Padding(8, 5, 8, 5)
            };

            _progressBar = new ProgressBar
            {
                Width    = 200,
                Height   = 16,
                Location = new Point(8, 6),
                Style    = ProgressBarStyle.Continuous
            };

            _statusLabel = new Label
            {
                Text      = "Ready",
                ForeColor = Color.Silver,
                AutoSize  = true,
                Location  = new Point(218, 8)
            };

            _lastScanLabel = new Label
            {
                Text      = string.Empty,
                ForeColor = Color.Gray,
                AutoSize  = true,
                Anchor    = AnchorStyles.Right | AnchorStyles.Top
            };
            _lastScanLabel.Location = new Point(statusBar.Width - 220, 8);
            statusBar.SizeChanged += (_, _) =>
                _lastScanLabel.Location = new Point(statusBar.Width - 220, 8);

            statusBar.Controls.AddRange(new Control[]
                { _progressBar, _statusLabel, _lastScanLabel });

            // Data grid
            _grid = new DataGridView
            {
                Dock                  = DockStyle.Fill,
                ReadOnly              = true,
                AllowUserToAddRows    = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible     = false,
                SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.AllCells,
                BackgroundColor       = Color.FromArgb(18, 18, 18),
                GridColor             = Color.FromArgb(45, 45, 45),
                BorderStyle           = BorderStyle.None,
                Font                  = new Font("Consolas", 9f),
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight   = 28
            };

            _grid.DefaultCellStyle.BackColor          = Color.FromArgb(22, 22, 22);
            _grid.DefaultCellStyle.ForeColor          = Color.FromArgb(210, 210, 210);
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 80, 60);
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(35, 35, 35);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Silver;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(26, 26, 26);

            _grid.Columns.AddRange(
                new DataGridViewTextBoxColumn { HeaderText = "Asset",     Name = "Asset",   MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "Price",     Name = "Price",   MinimumWidth = 100 },
                new DataGridViewTextBoxColumn { HeaderText = "RSI(14)",   Name = "RSI",     MinimumWidth = 70  },
                new DataGridViewTextBoxColumn { HeaderText = "EMA cross", Name = "EMA",     MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "MACD",      Name = "MACD",    MinimumWidth = 70  },
                new DataGridViewTextBoxColumn { HeaderText = "Score",     Name = "Score",   MinimumWidth = 60  },
                new DataGridViewTextBoxColumn { HeaderText = "Signal",    Name = "Signal",  MinimumWidth = 90  },
                new DataGridViewTextBoxColumn { HeaderText = "Vol",       Name = "Vol",     MinimumWidth = 75  },
                new DataGridViewTextBoxColumn { HeaderText = "Liq 5m",   Name = "Liq5m",   MinimumWidth = 80  },
                new DataGridViewTextBoxColumn { HeaderText = "10-bar",    Name = "Trend",   MinimumWidth = 70  },
                new DataGridViewTextBoxColumn { HeaderText = "Scanned",   Name = "Scanned", MinimumWidth = 120 }
            );

            foreach (DataGridViewColumn col in _grid.Columns)
                col.SortMode = DataGridViewColumnSortMode.Automatic;

            _grid.CellFormatting     += Grid_CellFormatting;
            _grid.SelectionChanged   += Grid_SelectionChanged;
            _grid.SortCompare        += Grid_SortCompare;

            // Centre panel: asset grid on top, positions panel below with draggable splitter
            // (liquidation panel occupies the right side separately)
            _positionsPanel = new PositionsPanel(_accountManager!, _config);
            // Remove fixed dock — SplitContainer manages layout instead
            _positionsPanel.Dock = DockStyle.Fill;
            _grid.Dock           = DockStyle.Fill;

            var splitContainer = new SplitContainer
            {
                Dock          = DockStyle.Fill,
                Orientation   = Orientation.Horizontal,
                SplitterWidth = 5,
                BackColor     = Color.FromArgb(50, 50, 50),
                Panel1MinSize = 100,
                Panel2MinSize = 120,
            };

            splitContainer.Panel1.Controls.Add(_grid);
            splitContainer.Panel2.Controls.Add(_positionsPanel);

            var centrePanel = new Panel { Dock = DockStyle.Fill };
            centrePanel.Controls.Add(splitContainer);

            // Set splitter position once the form is fully laid out
            Shown += (_, _) =>
            {
                if (splitContainer.Height > 300)
                    splitContainer.SplitterDistance = splitContainer.Height - 185;
            };

            // On startup: initialise asset indexes (needed for order placement) then load positions
            Load += async (_, _) =>
            {
                await _client.InitialiseAssetIndexesAsync();
                await _positionsPanel.RefreshAsync();
                await UpdatePortfolioLabelAsync();
                SetupConfigWatcher();
                ApplyStartupConfig();
                RestoreAlertBar();
            };

            // Dedicated RSI-LL alert bar — persists between scans, not overwritten by scan progress
            _alertBar = new Label
            {
                Dock      = DockStyle.Top,
                Height    = 22,
                BackColor = Color.FromArgb(35, 18, 0),
                ForeColor = Color.FromArgb(100, 100, 100),
                Text      = "📉 RSI-LL alerts — none yet",
                Font      = new Font("Segoe UI", 8.5f),
                Padding   = new Padding(8, 3, 8, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };

            Controls.Add(centrePanel);
            Controls.Add(_alertBar);
            Controls.Add(toolbar);
            Controls.Add(statusBar);

            ResumeLayout();
        }

        // Connection tests

        private async Task TestConnectionsOnStartupAsync()
        {
            _connectionLabel.Text      = "Connecting...";
            _connectionLabel.ForeColor = Color.Gray;

            var (hlOk, hlMsg) = await _client.TestConnectionAsync();

            // Seed the liquidation aggregator with the live HL asset list so it
            // filters out symbols not tradeable on Hyperliquid (e.g. GIGGLE, XAU).
            if (hlOk && _aggregator != null)
            {
                try
                {
                    var assets = await _client.GetAssetsAsync();
                    _aggregator.SetHlAssets(assets.Select(a => a.Name));
                }
                catch { /* non-fatal — filter stays off, all symbols pass through */ }
            }

            if (_coinglass != null)
            {
                var (cgOk, cgMsg) = await _coinglass.TestConnectionAsync();

                _connectionLabel.Text = hlOk && cgOk
                    ? $"HL: {hlMsg.Replace("Connected — ", "")}  |  CG: connected"
                    : hlOk
                        ? $"HL: connected  |  CG: {(cgOk ? "ok" : "FAILED")}"
                        : "HL: FAILED";

                _connectionLabel.ForeColor = (hlOk && cgOk)
                    ? Color.FromArgb(80, 220, 130)
                    : Color.FromArgb(220, 140, 40);
            }
            else
            {
                _connectionLabel.Text      = hlOk ? $"Connected — {hlMsg.Replace("Connected — ", "")}" : hlMsg;
                _connectionLabel.ForeColor = hlOk
                    ? Color.FromArgb(80, 220, 130)
                    : Color.FromArgb(220, 80, 80);
            }

            _statusLabel.Text = hlOk ? "Connected. Press Scan to begin." : hlMsg;
        }

        // Grid row click -> load liquidation panel

        private void Grid_SelectionChanged(object? sender, EventArgs e)
        {
            if (_liqPanel == null) return;
            if (_grid.SelectedRows.Count == 0) return;

            var row    = _grid.SelectedRows[0];
            var symbol = row.Cells["Asset"].Value?.ToString() ?? string.Empty;
            var score  = 0m;

            if (row.Cells["Score"].Value?.ToString() is string scoreStr
                && scoreStr.Contains('/'))
                decimal.TryParse(scoreStr.Split('/')[0], out score);

            if (!string.IsNullOrEmpty(symbol))
                _liqPanel.LoadAsset(symbol, score);
        }

        // Scan logic

        private async void ScanButton_Click(object? sender, EventArgs e)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            SetScanning(true);

            // Keep existing grid data visible while the new scan runs —
            // only swap in fresh results when the scan completes.
            _statusLabel.Text = _lastResults.Count > 0
                ? "Refreshing — previous results still shown..."
                : "Scanning...";

            var (_, apiValue) = Timeframes.All[_timeframeCombo.SelectedIndex];

            var progress = new Progress<(int current, int total, string asset)>(p =>
            {
                _progressBar.Maximum = p.total;
                _progressBar.Value   = Math.Min(p.current, p.total);
                _statusLabel.Text    = $"Scanning {p.asset}... ({p.current}/{p.total})";
            });

            try
            {
                var previousRsiLL = _lastResults.Where(r => r.IsRsiLowerLow)
                                               .Select(r => r.Asset).ToHashSet();

                var newResults = await _scanner.ScanAsync(apiValue, progress, _cts.Token);
                _lastResults = newResults;
                RefreshGrid();

                // Pass known asset names to positions panel for HIP-3 symbol resolution
                _positionsPanel?.SetKnownAssets(_lastResults.Select(r => r.Asset));

                // Refresh portfolio tracker after each scan
                await UpdatePortfolioLabelAsync(_cts?.Token ?? default);

                // Alert on newly detected RSI Lower Low signals
                var now = DateTime.Now;

                if (_firstScanCompleted)
                {
                    var newRsiLL = _lastResults
                        .Where(r => r.IsRsiLowerLow
                                 && !previousRsiLL.Contains(r.Asset)
                                 && (!_rsiLLCooldowns.TryGetValue(r.Asset, out var coolUntil)
                                     || now >= coolUntil))
                        .ToList();

                    if (newRsiLL.Count > 0)
                    {
                        foreach (var r in newRsiLL)
                            _rsiLLCooldowns[r.Asset] = now + RsiLLCooldown;
                        OnRsiLowerLowAlert(newRsiLL);

                        // Phase 3: trigger auto-entry for qualifying symbols
                        if (_autoEntry != null && _config.AutotradingEnabled)
                        {
                            var positions = _positionsPanel?._positions
                                            ?? new List<PositionInfo>();
                            _ = _autoEntry.ProcessSignalsAsync(
                                    newRsiLL, positions, 0, _cts?.Token ?? default);
                        }
                    }
                }

                _firstScanCompleted = true;

                var bullishCount = _lastResults.Count(r => r.IsBullish);
                var errorCount   = _lastResults.Count(r => r.BullishScore == -1);
                _statusLabel.Text   = $"Scan complete — {bullishCount} bullish of "
                                    + $"{_lastResults.Count - errorCount} assets"
                                    + (errorCount > 0 ? $" ({errorCount} errors)" : "");
                _lastScanLabel.Text = $"Last scan: {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException)
            {
                _statusLabel.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Scan error:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = "Scan failed.";
            }
            finally
            {
                SetScanning(false);
                // Immediately refresh positions after scan completes so new positions
                // opened during the scan appear without waiting for the next timer tick
                if (_positionsPanel != null)
                    _ = _positionsPanel.RefreshAsync();
            }
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();

            var search = _searchBox.Text.Trim();

            // Base filter — directional alerts punch through their matching filter only
            var toShow = _filterCombo.SelectedIndex switch
            {
                1 => _lastResults.Where(r => r.IsBullish || r.IsAbsorption || r.IsClimax || r.IsReversalSetup || r.IsRsiLowerLow),  // Bullish + reversal-up signals
                2 => _lastResults.Where(r => r.IsBearish || r.IsDistribution),              // Bearish + reversal-down signals
                3 => _lastResults.Where(r => r.HasAlert),                                    // All alerts
                _ => _lastResults                                                             // All assets
            };
            toShow = toShow.Where(r => r.BullishScore >= 0); // exclude scan errors

            // Search overrides filter — show any matching asset including errors
            if (!string.IsNullOrEmpty(search))
                toShow = _lastResults.Where(r =>
                             r.Asset.Contains(search, StringComparison.OrdinalIgnoreCase));

            foreach (var r in toShow.OrderByDescending(r => r.HasAlert)
                                    .ThenByDescending(r => r.BullishScore)
                                    .ThenByDescending(r => r.Rsi))
            {
                var volCell = r.IsRsiLowerLow  ? "📉 RSI-LL"
                           : r.IsClimax       ? "⚡ Climax"
                           : r.IsAbsorption   ? "⟳ Absorb"
                           : r.IsDistribution ? "⟳ Dist"
                           : r.VolumeSpike    ? $"{r.VolumeRatio:F1}×"
                           : r.PriceSurge     ? $"{(r.PriceChangePct >= 0 ? "+" : "")}{r.PriceChangePct:F1}%"
                           : "-";

                var trendCell = r.RecentTrendPct == 0m ? "-"
                              : $"{(r.RecentTrendPct >= 0 ? "+" : "")}{r.RecentTrendPct:F1}%";

                var idx = _grid.Rows.Add(
                    r.Asset,
                    r.LastPrice.ToString("G6"),
                    r.Rsi.ToString("F1"),
                    r.EmaCrossover ? "Y" : "-",
                    r.MacdBullish  ? "Y" : "-",
                    $"{r.BullishScore}/3",
                    r.SignalLabel,
                    volCell,
                    GetLiqCell(r.Asset),
                    trendCell,
                    r.ScannedAt.ToLocalTime().ToString("HH:mm:ss")
                );

                _grid.Rows[idx].Tag = r;
                if (r.IsRsiLowerLow)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(50, 25, 0);   // dark orange — RSI exhaustion lower low
                else if (r.IsClimax)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(40, 10, 55);  // dark purple — panic sell exhaustion
                else if (r.IsReversalSetup)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(15, 30, 60);  // dark blue — oversold reversal candidate
                else if (r.IsAbsorption)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(10, 40, 45);  // teal  — quiet absorption
                else if (r.IsDistribution)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(50, 20, 10);  // dark orange — distribution top
                else if (r.HasAlert)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(45, 38, 10);  // amber
                else if (r.IsBearish)
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(35, 20, 20);  // red
            }
        }

        private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var col = _grid.Columns[e.ColumnIndex].Name;

            if (col == "Signal")
            {
                var sig = e.Value?.ToString() ?? "";
                e.CellStyle.ForeColor = sig.Contains("Bullish")  ? Color.FromArgb(80, 220, 130)
                                      : sig.Contains("Reversal") ? Color.FromArgb(100, 160, 255)  // blue — oversold reversal candidate
                                      : sig.Contains("Bearish")  ? Color.FromArgb(220, 80, 80)
                                      : Color.FromArgb(120, 120, 120);
            }

            if (col is "EMA" or "MACD")
            {
                e.CellStyle.ForeColor = e.Value?.ToString() == "Y"
                    ? Color.FromArgb(80, 220, 130)
                    : Color.FromArgb(100, 100, 100);
            }

            if (col == "Trend")
            {
                var val = e.Value?.ToString() ?? "-";
                if (val == "-")
                    e.CellStyle.ForeColor = Color.FromArgb(100, 100, 100);
                else if (val.StartsWith("+"))
                    e.CellStyle.ForeColor = Color.FromArgb(80, 220, 130);   // green — healthy backdrop
                else
                {
                    // Shade red deeper the steeper the drop
                    if (double.TryParse(val.TrimEnd('%'), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        var intensity = (int)Math.Min(255, 120 + Math.Abs(pct) * 8);
                        e.CellStyle.ForeColor = Color.FromArgb(intensity, 60, 60);  // brighter red = steeper fall
                    }
                }
            }

            if (col == "Vol")
            {
                var val = e.Value?.ToString() ?? "-";
                if (val == "-")
                    e.CellStyle.ForeColor = Color.FromArgb(100, 100, 100);
                else if (val == "📉 RSI-LL")                         // RSI lower low exhaustion → reversal up
                    e.CellStyle.ForeColor = Color.FromArgb(255, 140, 0);
                else if (val == "⚡ Climax")                         // panic sell exhaustion → sharp reversal up
                    e.CellStyle.ForeColor = Color.FromArgb(200, 150, 255);
                else if (val == "⟳ Absorb")                         // selling absorbed → potential reversal up
                    e.CellStyle.ForeColor = Color.FromArgb(0, 220, 210);
                else if (val == "⟳ Dist")                           // buying absorbed → potential reversal down
                    e.CellStyle.ForeColor = Color.FromArgb(255, 120, 50);
                else if (val.Contains("×"))                          // volume spike
                    e.CellStyle.ForeColor = Color.FromArgb(255, 200, 60);
                else if (val.StartsWith("+"))                        // price surge up
                    e.CellStyle.ForeColor = Color.FromArgb(80, 220, 130);
                else                                                 // price dump
                    e.CellStyle.ForeColor = Color.FromArgb(220, 80, 80);
            }

            if (col == "Liq5m")
            {
                var val = e.Value?.ToString() ?? "-";
                if (val == "-")
                    e.CellStyle.ForeColor = Color.FromArgb(55, 55, 55);
                else if (val.StartsWith("🔥"))                       // short squeeze → consider long
                    e.CellStyle.ForeColor = Color.FromArgb(80, 230, 140);
                else if (val.StartsWith("💧"))                       // long cascade → consider short
                    e.CellStyle.ForeColor = Color.FromArgb(255, 100, 100);
                else                                                 // active but no directional signal
                    e.CellStyle.ForeColor = Color.FromArgb(140, 140, 160);
            }
        }

        // ── Live liquidation helpers ──────────────────────────────────────────

        /// <summary>
        /// Returns the Liq5m cell text for a Hyperliquid symbol, looking up the aggregator dict.
        /// Handles kXXX ↔ 1000XXX normalisation for OKX symbols.
        /// </summary>
        private string GetLiqCell(string hlSymbol)
        {
            if (_liqSummaries.Count == 0) return "-";

            LiquidationAggregator.SymbolSummary? s = null;
            _liqSummaries.TryGetValue(hlSymbol, out s);
            if (s == null) _liqSummaries.TryGetValue(HlToOkxBase(hlSymbol), out s);
            if (s == null) return "-";

            return s.Alert switch
            {
                LiquidationAggregator.SqueezeType.ShortSqueeze => $"🔥 {s.FormatTotal()}",
                LiquidationAggregator.SqueezeType.LongCascade  => $"💧 {s.FormatTotal()}",
                _                                               => s.FormatTotal()
            };
        }

        /// <summary>
        /// Refresh only the Liq5m column in the existing grid rows without rebuilding.
        /// Called every 2 s via the aggregator's leaderboard timer.
        /// </summary>
        private void UpdateGridLiqColumn()
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.IsNewRow) continue;
                var sym = row.Cells["Asset"].Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(sym))
                    row.Cells["Liq5m"].Value = GetLiqCell(sym);
            }
        }

        /// <summary>Maps Hyperliquid kXXX symbols to OKX 1000XXX base symbols.</summary>
        private static string HlToOkxBase(string hlSymbol)
        {
            if (hlSymbol.StartsWith("k", StringComparison.OrdinalIgnoreCase) && hlSymbol.Length > 1)
                return "1000" + hlSymbol[1..].ToUpperInvariant();
            return hlSymbol.ToUpperInvariant();
        }

        // ── Burst alert handling ──────────────────────────────────────────────

        /// <summary>
        /// Called on the UI thread when the aggregator detects a burst.
        /// Plays sound, flashes the matching grid row, updates status bar with scanner context.
        /// </summary>
        private void OnBurstAlert(LiquidationAggregator.BurstAlert burst)
        {
            // Sound — different tone for squeeze vs cascade
            // Uses custom wav files from appsettings.json; falls back to system sounds if not found
            try
            {
                if (burst.Direction == LiquidationAggregator.SqueezeType.ShortSqueeze)
                {
                    if (_squeezeSound != null) _squeezeSound.Play();
                    else System.Media.SystemSounds.Exclamation.Play();
                }
                else
                {
                    if (_cascadeSound != null) _cascadeSound.Play();
                    else System.Media.SystemSounds.Hand.Play();
                }
            }
            catch { /* sound not critical */ }

            // Flash the matching grid row
            FlashGridRow(burst.Symbol);

            // Build scanner-context string for status bar
            // Try to find this symbol in the last scan results
            var scanContext = FindScanContext(burst.Symbol);
            var confidence  = BuildConfidenceLabel(burst, scanContext);

            _statusLabel.Text      = $"⚡ BURST  {burst.Symbol}  {burst.BriefLabel}  {burst.FormatTotal()}  {confidence}";
            _statusLabel.ForeColor = burst.Direction == LiquidationAggregator.SqueezeType.ShortSqueeze
                ? Color.FromArgb(255, 110, 110)   // red  = short squeeze (matches CoinGlass)
                : Color.FromArgb(80, 240, 140);   // green = long cascade

            // Restore status colour after 30 s
            var restoreTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
            restoreTimer.Tick += (_, _) =>
            {
                restoreTimer.Stop(); restoreTimer.Dispose();
                _statusLabel.ForeColor = Color.Silver;
                // Don't overwrite status if a newer alert landed
                if (_statusLabel.Text.StartsWith("⚡ BURST"))
                    _statusLabel.Text = _lastResults.Count > 0
                        ? $"Last scan: {_lastScanLabel.Text}"
                        : "Ready";
            };
            restoreTimer.Start();
        }

        private AssetScanResult? FindScanContext(string okxSymbol)
        {
            // Try direct match then k-prefix reverse
            var r = _lastResults.FirstOrDefault(x =>
                x.Asset.Equals(okxSymbol, StringComparison.OrdinalIgnoreCase));
            if (r != null) return r;

            // 1000SHIB → kSHIB
            if (okxSymbol.StartsWith("1000", StringComparison.OrdinalIgnoreCase) && okxSymbol.Length > 4)
            {
                var kSym = "k" + okxSymbol[4..];
                r = _lastResults.FirstOrDefault(x =>
                    x.Asset.Equals(kSym, StringComparison.OrdinalIgnoreCase));
            }
            return r;
        }

        private static string BuildConfidenceLabel(
            LiquidationAggregator.BurstAlert burst, AssetScanResult? scan)
        {
            if (scan == null) return "(not in last scan)";

            bool alignedLong  = burst.Direction == LiquidationAggregator.SqueezeType.ShortSqueeze
                                 && scan.IsBullish;
            bool alignedShort = burst.Direction == LiquidationAggregator.SqueezeType.LongCascade
                                 && scan.IsBearish;

            if (alignedLong || alignedShort)
                return $"✓ CONFIRMED by scan  RSI {scan.Rsi:F0}  Score {scan.BullishScore}/3";

            return $"RSI {scan.Rsi:F0}  Score {scan.BullishScore}/3  {scan.SignalLabel}";
        }

        /// <summary>
        /// Briefly highlights the grid row(s) matching the OKX burst symbol.
        /// The flash colour overrides the existing row colour for 4 seconds.
        /// </summary>
        private void FlashGridRow(string okxSymbol)
        {
            var targets = new List<DataGridViewRow>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var asset = row.Cells["Asset"].Value?.ToString() ?? "";
                if (string.IsNullOrEmpty(asset)) continue;

                bool hit = asset.Equals(okxSymbol, StringComparison.OrdinalIgnoreCase);
                if (!hit && okxSymbol.StartsWith("1000", StringComparison.OrdinalIgnoreCase)
                         && okxSymbol.Length > 4)
                    hit = asset.Equals("k" + okxSymbol[4..], StringComparison.OrdinalIgnoreCase);

                if (hit) targets.Add(row);
            }

            if (targets.Count == 0) return;

            // Store original colours, apply flash
            var originals = targets.ToDictionary(r => r, r => r.DefaultCellStyle.BackColor);
            var flashCol  = Color.FromArgb(60, 55, 0); // amber

            foreach (var r in targets)
                r.DefaultCellStyle.BackColor = flashCol;
            _grid.Invalidate();

            var t = new System.Windows.Forms.Timer { Interval = 4_000 };
            t.Tick += (_, _) =>
            {
                t.Stop(); t.Dispose();
                foreach (var r in targets)
                    if (originals.TryGetValue(r, out var orig))
                        r.DefaultCellStyle.BackColor = orig;
                _grid.Invalidate();
            };
            t.Start();
        }

        private void Grid_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
        {
            // Numeric sort for the 10-bar trend column (values are strings like "+4.2%" or "-1.2%")
            if (e.Column.Name != "Trend") return;
            e.Handled    = true;
            e.SortResult = ParsePct(e.CellValue1?.ToString())
                           .CompareTo(ParsePct(e.CellValue2?.ToString()));
        }

        private static double ParsePct(string? s)
        {
            if (string.IsNullOrEmpty(s) || s == "-") return 0;
            return double.TryParse(s.TrimEnd('%'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        private void SetScanning(bool scanning)
        {
            _scanButton.Enabled   = !scanning;
            _cancelButton.Enabled = scanning;
            if (!scanning) _progressBar.Value = 0;
            _positionsPanel?.SetScanInProgress(scanning);
        }

        // ── Config hot-reload ─────────────────────────────────────────────────

        private void SetupConfigWatcher()
        {
            // Poll both source and output config.json every 2 seconds.
            // FileSystemWatcher is unreliable with editors that save via temp+rename
            // (VS Code, Notepad++ etc.) — polling is simpler and always works.
            var outputPath        = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            var sourcePath        = FindSourceConfig();
            var appSettingsOutput = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            var appSettingsSource = FindSourceAppSettings();

            // Seed last-write times
            _lastConfigWrite = GetNewestWriteTime(outputPath, sourcePath);
            var lastAppSettingsWrite = GetNewestWriteTime(appSettingsOutput, appSettingsSource);

            _configPollTimer = new System.Windows.Forms.Timer { Interval = 2_000 };
            _configPollTimer.Tick += (_, _) =>
            {
                // Poll config.json
                var newestConfig = GetNewestWriteTime(outputPath, sourcePath);
                if (newestConfig > _lastConfigWrite)
                {
                    _lastConfigWrite = newestConfig;
                    if (sourcePath != null && File.Exists(sourcePath)
                        && File.GetLastWriteTime(sourcePath) >= File.GetLastWriteTime(outputPath))
                    {
                        try { File.Copy(sourcePath, outputPath, overwrite: true); } catch { }
                    }
                    ReloadConfig();
                }

                // Poll appsettings.json
                var newestSettings = GetNewestWriteTime(appSettingsOutput, appSettingsSource);
                if (newestSettings > lastAppSettingsWrite)
                {
                    lastAppSettingsWrite = newestSettings;
                    if (appSettingsSource != null && File.Exists(appSettingsSource)
                        && File.GetLastWriteTime(appSettingsSource) >= File.GetLastWriteTime(appSettingsOutput))
                    {
                        try { File.Copy(appSettingsSource, appSettingsOutput, overwrite: true); } catch { }
                    }
                    ReloadAppSettings();
                }
            };
            _configPollTimer.Start();
        }

        private static DateTime GetNewestWriteTime(string outputPath, string? sourcePath)
        {
            var t = File.Exists(outputPath) ? File.GetLastWriteTime(outputPath) : DateTime.MinValue;
            if (sourcePath != null && File.Exists(sourcePath))
            {
                var st = File.GetLastWriteTime(sourcePath);
                if (st > t) t = st;
            }
            return t;
        }

        /// <summary>
        /// Walks up from the exe directory looking for a config.json alongside a .csproj
        /// (the project source directory, not the build output).
        /// </summary>
        private static string? FindSourceAppSettings()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (dir.GetDirectories(".git").Length > 0)
                {
                    var candidate = Path.Combine(dir.FullName, "appsettings.json");
                    if (File.Exists(candidate)) return candidate;
                    break;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static string? FindSourceConfig()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (dir.GetFiles("*.csproj").Length > 0)
                {
                    var candidate = Path.Combine(dir.FullName, "config.json");
                    if (File.Exists(candidate)) return candidate;
                }
                if (dir.GetDirectories(".git").Length > 0) break;
                dir = dir.Parent;
            }
            return null;
        }

        private void ApplySoundVolume(float volume)
        {
            foreach (var player in new[] { _squeezeSound, _cascadeSound, _rsiLowerLowSound,
                                           _slTriggeredSound, _entryPlacedSound, _entryFilledSound })
                if (player != null) player.Volume = volume;
        }

        private void ReloadAppSettings()
        {
            try
            {
                var fresh = AppSettingsLoader.Load();
                _scanner.Analyser.RsiLowerLowMinDropPct     = fresh.RsiLowerLowMinDropPct;
                _scanner.Analyser.RsiLowerLowConfirmCandles = fresh.RsiLowerLowConfirmCandles;
                ApplySoundVolume(fresh.SoundVolume);
                _statusLabel.Text      = $"⟳ appsettings.json reloaded — RSI-LL min drop: {fresh.RsiLowerLowMinDropPct:P1}  confirm: {fresh.RsiLowerLowConfirmCandles} candle(s)  vol: {fresh.SoundVolume:P0}";
                _statusLabel.ForeColor = Color.FromArgb(100, 180, 255);
                var t = new System.Windows.Forms.Timer { Interval = 5_000 };
                t.Tick += (_, _) => { t.Stop(); t.Dispose(); _statusLabel.ForeColor = Color.Silver; };
                t.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppSettings reload] {ex.Message}");
            }
        }

        private void ReloadConfig()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                var json       = File.ReadAllText(configPath);
                var fresh      = Newtonsoft.Json.JsonConvert.DeserializeObject<AppConfig>(json);
                if (fresh == null) return;

                // Update risk-related fields in-place so all existing references stay valid
                _config.AutoRiskManagement  = fresh.AutoRiskManagement;
                _config.AutotradingEnabled  = fresh.AutotradingEnabled;
                _config.SymbolInfo          = fresh.SymbolInfo;
                _config.BullishThreshold    = fresh.BullishThreshold;
                _config.RequestDelayMs      = fresh.RequestDelayMs;

                // Reset monitor state — new thresholds apply cleanly
                foreach (var account in _accountManager?.Accounts ?? Array.Empty<AccountContext>())
                    account.Monitor?.Reset();

                _statusLabel.Text      = "⟳ config.json reloaded — risk settings updated";
                _statusLabel.ForeColor = Color.FromArgb(100, 180, 255);

                // Restore label colour after 5s
                var t = new System.Windows.Forms.Timer { Interval = 5_000 };
                t.Tick += (_, _) => { t.Stop(); t.Dispose(); _statusLabel.ForeColor = Color.Silver; };
                t.Start();
            }
            catch (Exception ex)
            {
                _statusLabel.Text      = $"Config reload failed: {ex.Message}";
                _statusLabel.ForeColor = Color.FromArgb(220, 80, 80);
            }
        }

        // ── RSI Lower Low alert ───────────────────────────────────────────────

        // RSI-LL persistent state (cooldowns + alert history survive app restarts)
        private RsiLLState _rsiLLState = RsiLLStateManager.Load();
        private const int MaxAlertHistory = 8;
        private bool _firstScanCompleted = false;
        private static readonly TimeSpan RsiLLCooldown = TimeSpan.FromMinutes(30);

        private Dictionary<string, DateTime> _rsiLLCooldowns => _rsiLLState.Cooldowns;
        private List<string> _rsiLLAlertHistory              => _rsiLLState.AlertHistory;

        private void OnAutoEntryEvent(string message, Color colour)
        {
            _statusLabel.Text      = message;
            _statusLabel.ForeColor = colour;
            Log.Information("AutoEntry event: {Message}", message);

            var t = new System.Windows.Forms.Timer { Interval = 15_000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); _statusLabel.ForeColor = Color.Silver; };
            t.Start();
        }

        private void RestoreAlertBar()
        {
            if (_rsiLLAlertHistory.Count == 0) return;
            _alertBar.Text      = "📉 " + string.Join("   ·   ", _rsiLLAlertHistory.AsEnumerable().Reverse());
            _alertBar.ForeColor = Color.FromArgb(255, 220, 50);
            _alertBar.BackColor = Color.FromArgb(45, 35, 0);
        }

        private void OnRsiLowerLowAlert(List<AssetScanResult> signals)
        {
            // Play alert sound
            try
            {
                if (_rsiLowerLowSound != null) _rsiLowerLowSound.Play();
                else System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }

            // Add to alert history with timestamp
            var time = DateTime.Now.ToString("HH:mm");
            var (_, apiValue) = Timeframes.All[_timeframeCombo.SelectedIndex];
            foreach (var sig in signals)
                _rsiLLAlertHistory.Add($"{sig.Asset} {apiValue} RSI{sig.Rsi:F0} @{time}");

            // Keep only last N alerts
            while (_rsiLLAlertHistory.Count > MaxAlertHistory)
                _rsiLLAlertHistory.RemoveAt(0);

            // Persist state so cooldowns and history survive restarts
            RsiLLStateManager.Save(_rsiLLState);

            // Log the alert
            Log.Information("RSI-LL alert: {Assets}", string.Join(", ", signals.Select(r => $"{r.Asset} RSI{r.Rsi:F0}")));

            // Update alert bar
            _alertBar.Text      = "📉 NEW: " + string.Join("   ·   ", _rsiLLAlertHistory.AsEnumerable().Reverse());
            _alertBar.ForeColor = Color.FromArgb(255, 220, 50);   // bright yellow
            _alertBar.BackColor = Color.FromArgb(45, 35, 0);

            // Flash matching grid rows
            foreach (var sig in signals)
                FlashGridRow(sig.Asset);

            // Status bar also shows the new signal
            var assets = string.Join(", ", signals.Select(r => $"{r.Asset} RSI{r.Rsi:F0}"));
            _statusLabel.Text      = $"📉 RSI-LL  {assets}  {apiValue} — exhaustion reversal";
            _statusLabel.ForeColor = Color.FromArgb(255, 220, 50);

            var t = new System.Windows.Forms.Timer { Interval = 30_000 };
            t.Tick += (_, _) =>
            {
                t.Stop(); t.Dispose();
                _statusLabel.ForeColor = Color.Silver;
                if (_statusLabel.Text.StartsWith("📉 RSI-LL"))
                    _statusLabel.Text = "Ready";
            };
            t.Start();
        }

        // ── Portfolio tracker ─────────────────────────────────────────────────

        private async Task UpdatePortfolioLabelAsync(CancellationToken ct = default)
        {
            try
            {
                var value = await _client.GetAccountValueAsync(ct);
                if (value == null) return;

                var goal    = _config.PortfolioGoalUsd;
                var pct     = goal > 0 ? value.Value / goal * 100m : 0m;
                var arrow   = value.Value >= goal ? "🎯" : "📈";
                var colour  = value.Value >= goal ? Color.FromArgb(255, 220, 50)   // gold — goal reached!
                            : value.Value >= goal * 0.9m ? Color.FromArgb(80, 220, 130)   // green — close
                            : Color.FromArgb(100, 180, 255);                               // blue  — in progress

                _portfolioLabel.Text      = $"{arrow} ${value.Value:F2} / ${goal:F0}  ({pct:F1}%)";
                _portfolioLabel.ForeColor = colour;

                // Move connection label left to make room
                _connectionLabel.Location = new Point(
                    _portfolioLabel.Left - _connectionLabel.Width - 20,
                    _connectionLabel.Top);
            }
            catch { /* non-fatal */ }
        }

        private void ApplyStartupConfig()
        {
            // Set auto-refresh dropdown
            var interval = _config.AutoRefreshInterval?.Trim() ?? "Off";
            for (int i = 0; i < _autoRefreshCombo.Items.Count; i++)
            {
                if (string.Equals(_autoRefreshCombo.Items[i]?.ToString(), interval,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _autoRefreshCombo.SelectedIndex = i;
                    break;
                }
            }

            // Auto-start scan
            if (_config.ScanOnStartup && _scanButton.Enabled)
                ScanButton_Click(null, EventArgs.Empty);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _autoRefreshTimer.Stop();
            _configPollTimer?.Stop();
            _configPollTimer?.Dispose();
            _configWatcher?.Dispose();
            _sourceConfigWatcher?.Dispose();
            _configReloadDebounce?.Dispose();
            _cts?.Cancel();
            _aggregator?.Dispose();
            _binanceFeed?.Dispose();
            _bybitFeed?.Dispose();
            _client.Dispose();
            _coinglass?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
