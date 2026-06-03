namespace HyperliquidScanner.Models
{
    /// <summary>
    /// Non-sensitive application settings loaded from appsettings.json.
    /// Safe to commit to source control — contains no credentials or private keys.
    /// </summary>
    public class AppSettings
    {
        // ── Sound alerts ──────────────────────────────────────────────────────

        /// <summary>
        /// Path to the wav file played on a short squeeze burst alert.
        /// Relative to the repo root, or absolute. Leave empty to use system sound.
        /// </summary>
        public string SqueezeSoundFile { get; set; } = "sounds/844241__atp2-kh__bubbling-water-notification-ui-alert-cc0.wav";

        /// <summary>
        /// Path to the wav file played on a long cascade burst alert.
        /// Relative to the repo root, or absolute. Leave empty to use system sound.
        /// </summary>
        public string CascadeSoundFile { get; set; } = "sounds/751861__def__alert-sound-single.wav";

        /// <summary>
        /// Path to the wav file played when an RSI Lower Low signal is detected during a scan.
        /// Leave empty to use a system sound.
        /// </summary>
        public string RsiLowerLowSoundFile { get; set; } = "";

        /// <summary>Played when a SL order is triggered.</summary>
        public string SlTriggeredSoundFile { get; set; } = "";

        /// <summary>Played when a Phase 3 entry limit order is placed on the exchange.</summary>
        public string EntryPlacedSoundFile { get; set; } = "";

        /// <summary>Played when a Phase 3 entry order is confirmed filled.</summary>
        public string EntryFilledSoundFile { get; set; } = "";

        /// <summary>
        /// Minimum percentage drop from the previous RSI valley to the new lower low.
        /// 0.125 = 12.5% (default). Raise to 0.15-0.20 on faster timeframes (15m)
        /// to reduce noise; lower to 0.10 on slower timeframes (4h, 1d) to catch
        /// more signals.
        /// </summary>
        public double RsiLowerLowMinDropPct { get; set; } = 0.125;

        /// <summary>
        /// Number of consecutive green candles required after the RSI lower low
        /// before the signal fires. Default 1. Set to 2 for 15m to filter fakeouts.
        /// </summary>
        public int RsiLowerLowConfirmCandles { get; set; } = 1;
    }
}
