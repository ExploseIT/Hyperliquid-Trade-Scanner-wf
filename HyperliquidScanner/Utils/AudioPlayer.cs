using NAudio.Wave;

namespace HyperliquidScanner.Utils
{
    /// <summary>
    /// Plays audio files using NAudio — supports WAV, MP3 and other formats.
    /// Drop-in replacement for System.Media.SoundPlayer with broader format support.
    /// </summary>
    public class AudioPlayer : IDisposable
    {
        private readonly string _filePath;

        /// <summary>Master volume: 0.0 (silent) to 1.0 (full). Hot-reloadable — updated between plays.</summary>
        public float Volume { get; set; } = 1.0f;

        public AudioPlayer(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>Plays the audio file asynchronously (non-blocking).</summary>
        public void Play()
        {
            if (!File.Exists(_filePath)) return;

            var volume = Math.Clamp(Volume, 0f, 1f);

            // Fire-and-forget on a thread pool thread so UI is never blocked
            Task.Run(() =>
            {
                try
                {
                    using var reader  = new AudioFileReader(_filePath);
                    reader.Volume     = volume;
                    using var output  = new WaveOutEvent();
                    output.Init(reader);
                    output.Play();
                    // Wait for playback to finish before disposing
                    while (output.PlaybackState == PlaybackState.Playing)
                        Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Audio] playback failed: {ex.Message}");
                }
            });
        }

        public void Dispose() { /* nothing to dispose — each Play() self-cleans */ }
    }
}
