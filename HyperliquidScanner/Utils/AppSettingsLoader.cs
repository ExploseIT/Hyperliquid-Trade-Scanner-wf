using HyperliquidScanner.Models;
using Newtonsoft.Json;

namespace HyperliquidScanner.Utils
{
    /// <summary>
    /// Loads appsettings.json from the repo root.
    /// Safe to commit — contains no sensitive data.
    /// Auto-creates with defaults if not found.
    /// </summary>
    public static class AppSettingsLoader
    {
        private const string FileName = "appsettings.json";

        public static AppSettings Load()
        {
            var path = FindSettingsFile();

            if (path == null)
            {
                // Auto-create alongside the exe / project root
                var defaults = new AppSettings();
                var dir = FindRepoRoot() ?? AppDomain.CurrentDomain.BaseDirectory;
                path = Path.Combine(dir, FileName);
                File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                return defaults;
            }

            try
            {
                var json     = File.ReadAllText(path);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }

        /// <summary>
        /// Resolves a sound file path relative to the repo root.
        /// Returns null if the file cannot be found.
        /// </summary>
        public static string? ResolveSoundPath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            // Already absolute
            if (Path.IsPathRooted(relativePath))
                return File.Exists(relativePath) ? relativePath : null;

            // Try relative to repo root
            var repoRoot = FindRepoRoot();
            if (repoRoot != null)
            {
                var full = Path.GetFullPath(Path.Combine(repoRoot, relativePath));
                if (File.Exists(full)) return full;
            }

            // Try relative to exe directory
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var fromExe = Path.GetFullPath(Path.Combine(exeDir, relativePath));
            if (File.Exists(fromExe)) return fromExe;

            return null;
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static string? FindSettingsFile()
        {
            // Search upward from exe for appsettings.json
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, FileName);
                if (File.Exists(candidate)) return candidate;
                if (dir.GetDirectories(".git").Length > 0) break; // stop at repo root
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>Walks up from the exe until it finds the .git folder.</summary>
        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (dir.GetDirectories(".git").Length > 0) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
