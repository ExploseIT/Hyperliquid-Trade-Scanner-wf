namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Modal dialog for placing native exchange-side TP/SL trigger orders on an
    /// EXISTING open position — the retroactive counterpart to the bracket section
    /// in LimitPriceDialog (which only fires for entries placed through that dialog).
    ///
    /// Shows the position's entry/mark price for context, and pre-fills suggested
    /// TP/SL price levels derived from the symbol's configured PnL-based thresholds
    /// (tpUsd/slUsd), converted via priceDelta = pnlUsd / size. Both fields remain
    /// fully editable — they're a starting suggestion, not a binding default.
    ///
    /// Results:
    ///   TpPrice — take-profit trigger price (null = skip)
    ///   SlPrice — stop-loss trigger price (null = skip)
    /// </summary>
    public class PositionBracketDialog : Form
    {
        private NumericUpDown _tpInput    = null!;
        private NumericUpDown _slInput    = null!;
        private Button        _confirmBtn = null!;
        private Button        _cancelBtn  = null!;

        public decimal? TpPrice { get; private set; }
        public decimal? SlPrice { get; private set; }

        /// <param name="title">Dialog title, e.g. "Set Native TP/SL — BTC"</param>
        /// <param name="entryPrice">Position's entry price (context only).</param>
        /// <param name="markPrice">Current mark price (context only).</param>
        /// <param name="sizeDescription">e.g. "Size: 0.01628 BTC   PnL: +$2.23"</param>
        /// <param name="suggestedTpUsd">Symbol's configured PnL-based TP threshold (tpUsd),
        /// used only to pre-fill a suggested TP price. Null = leave at 0.</param>
        /// <param name="suggestedSlUsd">Symbol's configured PnL-based SL threshold (slUsd),
        /// used only to pre-fill a suggested SL price. Null = leave at 0.</param>
        /// <param name="size">Position size — required to convert PnL thresholds to prices.</param>
        /// <param name="isLong">True if long (TP above entry, SL below); false if short.</param>
        public PositionBracketDialog(string title, decimal entryPrice, decimal markPrice,
                                     string sizeDescription,
                                     decimal? suggestedTpUsd, decimal? suggestedSlUsd,
                                     decimal size, bool isLong)
        {
            Text            = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MinimizeBox     = false;
            MaximizeBox     = false;
            ClientSize      = new Size(360, 290);
            BackColor       = Color.FromArgb(30, 30, 35);
            ForeColor       = Color.Silver;
            Font            = new Font("Segoe UI", 9.5f);

            BuildControls(entryPrice, markPrice, sizeDescription);

            if (size > 0m)
            {
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

        private void BuildControls(decimal entryPrice, decimal markPrice, string sizeDesc)
        {
            int y = 16;

            Add(MakeLabel("Entry price:", 16, y));
            Add(MakeLabel($"{entryPrice:G8}", 180, y, bold: true, color: Color.White));
            y += 26;

            Add(MakeLabel("Mark price:", 16, y));
            Add(MakeLabel($"{markPrice:G8}", 180, y, bold: true, color: Color.White));
            y += 26;

            var szLabel = MakeLabel(sizeDesc, 16, y, color: Color.FromArgb(140, 140, 140));
            szLabel.Size = new Size(328, 18);
            Add(szLabel);
            y += 30;

            Add(new Panel { Location = new Point(16, y), Size = new Size(328, 1),
                            BackColor = Color.FromArgb(60, 60, 65) });
            y += 14;

            var note0 = MakeLabel("Native exchange-side trigger orders  (0 = skip)", 16, y,
                color: Color.FromArgb(160, 160, 160));
            note0.Size = new Size(328, 18);
            note0.Font = new Font("Segoe UI", 8.5f, FontStyle.Italic);
            Add(note0);
            y += 28;

            Add(MakeLabel("TP price:", 16, y, color: Color.FromArgb(80, 200, 130)));
            _tpInput = MakePriceInput(y);
            Add(_tpInput);
            y += 34;

            Add(MakeLabel("SL price:", 16, y, color: Color.FromArgb(220, 80, 80)));
            _slInput = MakePriceInput(y);
            Add(_slInput);
            y += 34;

            var note = MakeLabel("TP = limit trigger  ·  SL = market trigger", 16, y,
                color: Color.FromArgb(100, 100, 100));
            note.Font = new Font("Segoe UI", 8f, FontStyle.Italic);
            note.Size = new Size(328, 18);
            Add(note);
            y += 24;

            Add(new Panel { Location = new Point(16, y), Size = new Size(328, 1),
                            BackColor = Color.FromArgb(60, 60, 65) });
            y += 10;

            _confirmBtn = new Button
            {
                Text      = "Place TP/SL Orders",
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
            _cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
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

        private void ConfirmClicked(object? sender, EventArgs e)
        {
            if (_tpInput.Value <= 0m && _slInput.Value <= 0m)
            {
                MessageBox.Show("Set at least one of TP or SL above zero, or click Cancel.",
                    "Nothing To Place", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            TpPrice = _tpInput.Value > 0m ? _tpInput.Value : null;
            SlPrice = _slInput.Value > 0m ? _slInput.Value : null;
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
