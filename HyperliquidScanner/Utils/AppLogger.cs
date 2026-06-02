using Serilog;
using Serilog.Events;

namespace HyperliquidScanner.Utils
{
    /// <summary>
    /// Configures and provides the application-wide Serilog logger.
    /// Writes daily rolling log files to logs/scanner-YYYYMMDD.log alongside the exe.
    /// </summary>
    public static class AppLogger
    {
        public static void Initialise()
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: Path.Combine(logDir, "scanner-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,   // keep 2 weeks of logs
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("=== Hyperliquid Scanner started ===");
        }

        public static void Shutdown()
        {
            Log.Information("=== Hyperliquid Scanner stopped ===");
            Log.CloseAndFlush();
        }
    }
}
