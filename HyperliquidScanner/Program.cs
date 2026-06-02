using HyperliquidScanner.Forms;
using HyperliquidScanner.Services;
using HyperliquidScanner.Utils;
using HyperliquidScanner.Models;
using Serilog;

namespace HyperliquidScanner
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            AppLogger.Initialise();
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            try
            {
                var config      = ConfigLoader.Load();
                var appSettings = AppSettingsLoader.Load();
                var client      = new HyperliquidClient(config);
                var scanner     = new ScannerService(client, config);
                scanner.Analyser.RsiLowerLowMinDropPct = appSettings.RsiLowerLowMinDropPct;

                // Position monitor — only active if private key is configured
                PositionMonitor? monitor = config.HasPrivateKey
                    ? new PositionMonitor(client, config)
                    : null;

                // Coinglass panel is optional — only shown if API key is configured
                CoinglassClient? coinglass = config.HasCoinglassKey
                    ? new CoinglassClient(config)
                    : null;

                Application.Run(new MainForm(config, appSettings, client, scanner, monitor, coinglass));
            }
            catch (FileNotFoundException ex)
            {
                Log.Warning(ex, "First-time setup — config not found");
                MessageBox.Show(ex.Message, "First-time setup",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Startup error");
                MessageBox.Show($"Startup error:\n\n{ex.Message}", "Hyperliquid Scanner",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                AppLogger.Shutdown();
            }
        }
    }
}
