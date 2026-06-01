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
    }
}
