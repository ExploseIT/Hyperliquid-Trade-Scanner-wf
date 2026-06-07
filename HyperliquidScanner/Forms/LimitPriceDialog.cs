namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Modal dialog for choosing a limit order price, with optional TP and SL bracket prices.
    ///
    /// The user sees the mark price, edits a signed USD offset, and the limit price
    /// auto-updates live. TP and SL are optional — leave at 0 to skip each one.
    ///
    /// When <paramref name="suggestedTpUsd"/>/<paramref name="suggestedSlUsd"/> (the symbol's
    /// configured PnL-based auto TP/SL thresholds) and a position size are supplied, the TP/SL
    /// price boxes are pre-filled with the price levels that correspond to those PnL thresholds
    /// at the initial limit price — purely as an editable starting suggestion, so the user isn't
    /// stuck typing prices from scratch. They remain fully overridable.
    ///
    /// Results:
    ///   LimitPrice  — chosen limit order price (null = cancelled)
    ///   TpPrice     — take-profit trigger price (null = not set)
    ///   SlPrice     — stop-loss trigger price (null = not set)
    /// </summary>
    public class LimitPriceDialog : Form
    {
        private readonly decimal _markPrice;

        private NumericUpDown   _offsetInput  = null!;
        private Label           _limitLabel   = null!;
        private NumericUpDown   _tpInput      = null!;
        private NumericUpDown   _slInput      = null!;
        private Button          _confirmBtn   = null!;
        private Button          _cancelBtn    = null!;

        public decimal? LimitPrice { get; private set; }
        public decimal? TpPrice    { get; private set; }
        public decimal? SlPrice    { get; private set; }

        /// <param name="title">Dialog title, e.g. "Limit Short Entry — BTC"</param>
        /// <param name="markPrice">Current mark price (baseline for the offset).</param>
        /// <param name="defaultOffsetUsd">Pre-filled signed USD offset from config.</param>
        /// <param name="sizeDescription">e.g. "Size: 0.01639  (~$1,000 notional)"</param>
        /// <param name="showBracket">If true, shows the TP/SL bracket section.</param>
        /// <param name="suggestedTpUsd">Symbol's configured PnL-based auto take-profit threshold
        /// (e.g. tpUsd = 5 means "+$5 unrealised PnL"), used only to pre-fill a suggested TP
        /// price. Pass null to leave TP at 0 (disabled).</param>
        /// <param name="suggestedSlUsd">Symbol's configured PnL-based auto stop-loss threshold
        /// (e.g. slUsd = 5 means "-$5 unrealised PnL"), used only to pre-fill a suggested SL
        /// price. Pass null to leave SL at 0 (disabled).</param>
        /// <param name="size">Position size, required to convert the PnL thresholds above into
        /// price levels (priceDelta = pnlUsd / size). Ignored if suggestions are null.</param>
        /// <param name="isLong">True if this is a long entry (TP sits above entry, SL below);
        /// false for a short (TP below entry, SL above).</param>
        public LimitPriceDialog(string title, decimal markPrice,
                                decimal defaultOffsetUsd, string sizeDescription,
                                bool showBracket = true,
                                decimal? suggestedTpUsd = null, decimal? suggestedSlUsd = null,
                                decimal size = 0m, bool isLong = true)
        {
            _markPrice = markPrice;

            Text            = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MinimizeBox     = false;
            MaximizeBox     = false;
            ClientSize      = new Size(360, showBracket ? 310 : 190);
            BackColor       = Color.FromArgb(30, 30, 35);
            ForeColor       = Color.Silver;
            Font            = new Font("Segoe UI", 9.5f);

            BuildControls(defaultOffsetUsd, sizeDescription, showBracket);
            UpdateLimitPrice();

            if (showBracket && size > 0m)
            {
                var entryPrice = markPrice + defaultOffsetUsd;

                if (suggestedTpUsd is decimal tpUsd && tpUsd > 0m)
                {
                    var tpDelta = tpUsd / size;
                    var tpPrice = isLong ? entryPrice + tpDelta : entryPrice - tpDelta;
                    if (tpPrice > 0m)
                        _tpInput.Value = Math.Clamp(tpPrice, _tpInput.Minimum, _tpInput.Maximum);
                }

                if (suggestedSlUsd is decimal slUsd && slUsd > 0m)
                {
                    var slDelta = slUsd / size;
                    var slPrice = isLong ? entryPrice - slDelta : entryPrice + slDelta;
                    if (slPrice > 0m)
                        _slInput.Value = Math.Clamp(slPrice, _slInput.Minimum, _slInput.Maximum);
                }
            }
        }

        private void BuildControls(decimal defaultOffset, string sizeDesc, bool showBracket)
        {
            int y = 16;

            // ── Mark price ────────────────────────────────────────────────────
            Add(MakeLabel("Mark price:", 16, y));
            Add(MakeLabel($"{_markPrice:G8}", 180, y, bold: true, color: Color.White));
            y += 30;

            // ── Offset ────────────────────────────────────────────────────────
            Add(MakeLabel("Offset (USD):", 16, y));
            _offsetInput = new NumericUpDown
            {
                Location      = new Point(180, y - 2),
                Size          = new Size(150, 24),
                DecimalPlaces = 2,
                Minimum       = -1_000_000m,
                Maximum       =  1_000_000m,
                Increment     = 1m,
                Value         = Math.Clamp(defaultOffset, -1_000_000m, 1_000_000m),
                BackColor     = Color.FromArgb(45, 45, 50),
                ForeColor     = Color.White,
                BorderStyle   = BorderStyle.FixedSingle,
            };
            _offsetInput.ValueChanged += (_, _) => UpdateLimitPrice();
            Add(_offsetInput);
            y += 32;

            // ── Limit price (computed) ────────────────────────────────────────
            Add(MakeLabel("Limit price:", 16, y));
            _limitLabel = MakeLabel("", 180, y, bold: true, color: Color.FromArgb(80, 200, 255));
            _limitLabel.Size = new Size(160, 20);
            Add(_limitLabel);
            y += 28;

            // ── Size description ──────────────────────────────────────────────
            var szLabel = MakeLabel(sizeDesc, 16, y, color: Color.FromArgb(140, 140, 140));
            szLabel.Size = new Size(328, 18);
            Add(szLabel);
            y += 26;

            if (showBracket)
            {
                // ── Divider ───────────────────────────────────────────────────
                Add(new Panel { Location = new Point(16, y), Size = new Size(328, 1),
                                BackColor = Color.FromArgb(60, 60, 65) });
                y += 10;

                // ── Bracket section header ────────────────────────────────────
                var bracketHeader = MakeLabel("Bracket orders  (0 = disabled)", 16, y,
                    color: Color.FromArgb(160, 160, 160));
                bracketHeader.Size = new Size(328, 18);
                bracketHeader.Font = new Font("Segoe UI", 8.5f, FontStyle.Italic);
                Add(bracketHeader);
                y += 26;

                // ── TP ────────────────────────────────────────────────────────
                Add(MakeLabel("TP price:", 16, y, color: Color.FromArgb(80, 200, 130)));
                _tpInput = MakePriceInput(y);
                Add(_tpInput);
                y += 32;

                // ── SL ────────────────────────────────────────────────────────
                Add(MakeLabel("SL price:", 16, y, color: Color.FromArgb(220, 80, 80)));
                _slInput = MakePriceInput(y);
                Add(_slInput);
                y += 32;

                // ── Bracket note ──────────────────────────────────────────────
                var note = MakeLabel("TP = limit order  ·  SL = market trigger", 16, y,
                    color: Color.FromArgb(100, 100, 100));
                note.Font = new Font("Segoe UI", 8f, FontStyle.Italic);
                note.Size = new Size(328, 18);
                Add(note);
                y += 22;
            }

            // ── Divider ───────────────────────────────────────────────────────
            Add(new Panel { Location = new Point(16, y), Size = new Size(328, 1),
                            BackColor = Color.FromArgb(60, 60, 65) });
            y += 10;

            // ── Buttons ───────────────────────────────────────────────────────
            _confirmBtn = new Button
            {
                Text      = "Place Limit Order",
                Location  = new Point(16, y),
                Size      = new Size(158, 30),
                BackColor = Color.FromArgb(30, 100, 55),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _confirmBtn.FlatAppearance.BorderColor = Color.FromArgb(60, 160, 90);
            _confirmBtn.Click += ConfirmClicked;
            Add(_confirmBtn);

            _cancelBtn = new Button
            {
                Text      = "Cancel",
                Location  = new Point(186, y),
                Size      = new Size(158, 30),
                BackColor = Color.FromArgb(60, 30, 30),
                ForeColor = Color.Silver,
                FlatStyle = FlatStyle.Flat,
            };
            _cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(100, 50, 50);
            _cancelBtn.Click += (_, _) => { LimitPrice = null; DialogResult = DialogResult.Cancel; Close(); };
            Add(_cancelBtn);

            AcceptButton = _confirmBtn;
            CancelButton = _cancelBtn;
        }

        private NumericUpDown MakePriceInput(int y) => new NumericUpDown
        {
            Location      = new Point(180, y - 2),
            Size          = new Size(150, 24),
            DecimalPlaces = 2,
            Minimum       = 0m,
            Maximum       = 10_000_000m,
            Increment     = 1m,
            Value         = 0m,
            BackColor     = Color.FromArgb(45, 45, 50),
            ForeColor     = Color.White,
            BorderStyle   = BorderStyle.FixedSingle,
        };

        private void UpdateLimitPrice()
        {
            var limit = _markPrice + _offsetInput.Value;
            _limitLabel.Text = $"{limit:G8}";
        }

        private void ConfirmClicked(object? sender, EventArgs e)
        {
            var limit = _markPrice + _offsetInput.Value;
            if (limit <= 0)
            {
                MessageBox.Show("Limit price must be greater than zero.", "Invalid Price",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            LimitPrice = limit;
            TpPrice    = (_tpInput != null && _tpInput.Value > 0) ? _tpInput.Value : null;
            SlPrice    = (_slInput != null && _slInput.Value > 0) ? _slInput.Value : null;
            DialogResult = DialogResult.OK;
            Close();
        }

        private Label MakeLabel(string text, int x, int y,
                                bool bold = false, Color? color = null)
            => new Label
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(160, 20),
                ForeColor = color ?? Color.Silver,
                Font      = bold ? new Font("Segoe UI", 9.5f, FontStyle.Bold) : Font,
                AutoSize  = false,
            };

        private void Add(Control c) => Controls.Add(c);
    }
}
