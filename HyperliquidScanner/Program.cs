using HyperliquidScanner.Forms;
using HyperliquidScanner.Services;
using HyperliquidScanner.Utils;

namespace HyperliquidScanner
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            try
            {
                var config      = ConfigLoader.Load();
                var appSettings = AppSettingsLoader.Load();
                var client      = new HyperliquidClient(config);
                var scanner     = new ScannerService(client, config);

                // Coinglass panel is optional — only shown if API key is configured
                CoinglassClient? coinglass = config.HasCoinglassKey
                    ? new CoinglassClient(config)
                    : null;

                Application.Run(new MainForm(config, appSettings, client, scanner, coinglass));
            }
            catch (FileNotFoundException ex)
            {
                MessageBox.Show(ex.Message, "First-time setup",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Startup error:\n\n{ex.Message}", "Hyperliquid Scanner",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
