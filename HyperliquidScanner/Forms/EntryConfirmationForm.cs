namespace HyperliquidScanner.Forms
{
    /// <summary>
    /// Timed confirmation dialog for Phase 3 auto-entry orders.
    /// Auto-dismisses (cancels) after the timeout expires.
    /// </summary>
    public class EntryConfirmationForm : Form
    {
        public bool Confirmed { get; private set; }

        private readonly Label  _detailsLabel;
        private readonly Label  _countdownLabel;
        private readonly Button _confirmBtn;
        private readonly Button _skipBtn;
        private readonly System.Windows.Forms.Timer _countdown;
        private int _secondsLeft;

        public EntryConfirmationForm(
            string symbol, decimal entrySizeUsd, int leverage,
            decimal entryPrice, decimal tpUsd, decimal slUsd,
            int timeoutSec)
        {
            _secondsLeft = timeoutSec;

            Text            = "⚡ RSI-LL Auto Entry";
            Size            = new Size(420, 280);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = Color.FromArgb(22, 22, 22);
            ForeColor       = Color.FromArgb(210, 210, 210);
            MaximizeBox     = false;
            MinimizeBox     = false;
            TopMost         = true;

            var title = new Label
            {
                Text      = $"📉 RSI Lower Low — {symbol}",
                Font      = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 220, 50),
                AutoSize  = true,
                Location  = new Point(20, 15)
            };

            _detailsLabel = new Label
            {
                Text = $"Direction:   Long\n" +
                       $"Entry price: ~${entryPrice:F4}\n" +
                       $"Size:        ${entrySizeUsd:F2} margin @ {leverage}×\n" +
                       $"Take profit: +${tpUsd:F2}\n" +
                       $"Stop loss:   -${slUsd:F2}",
                Font      = new Font("Consolas", 10f),
                ForeColor = Color.FromArgb(200, 200, 200),
                AutoSize  = true,
                Location  = new Point(20, 55)
            };

            _countdownLabel = new Label
            {
                Text      = $"Auto-cancels in {_secondsLeft}s",
                Font      = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(140, 140, 140),
                AutoSize  = true,
                Location  = new Point(20, 185)
            };

            _confirmBtn = new Button
            {
                Text      = "✓  Confirm Entry",
                Size      = new Size(160, 36),
                Location  = new Point(20, 215),
                BackColor = Color.FromArgb(0, 122, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            _confirmBtn.FlatAppearance.BorderSize = 0;
            _confirmBtn.Click += (_, _) => { Confirmed = true; Close(); };

            _skipBtn = new Button
            {
                Text      = "✕  Skip",
                Size      = new Size(100, 36),
                Location  = new Point(200, 215),
                BackColor = Color.FromArgb(80, 30, 30),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 10f)
            };
            _skipBtn.FlatAppearance.BorderSize = 0;
            _skipBtn.Click += (_, _) => { Confirmed = false; Close(); };

            Controls.AddRange(new Control[]
                { title, _detailsLabel, _countdownLabel, _confirmBtn, _skipBtn });

            _countdown = new System.Windows.Forms.Timer { Interval = 1_000 };
            _countdown.Tick += (_, _) =>
            {
                _secondsLeft--;
                _countdownLabel.Text = _secondsLeft > 0
                    ? $"Auto-cancels in {_secondsLeft}s"
                    : "Cancelled.";
                if (_secondsLeft <= 0) { Confirmed = false; Close(); }
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _countdown.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _countdown?.Dispose();
            base.Dispose(disposing);
        }
    }
}
