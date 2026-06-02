using Newtonsoft.Json;
using Serilog;

namespace HyperliquidScanner.Utils
{
    /// <summary>
    /// Persists RSI Lower Low alert state (cooldowns + history) to a JSON file
    /// so they survive app restarts. Stored at rsi_ll_state.json alongside the exe.
    /// </summary>
    public class RsiLLState
    {
        /// <summary>Per-symbol cooldown expiry times — don't re-alert before this time.</summary>
        public Dictionary<string, DateTime> Cooldowns    { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Rolling alert history shown in the alert bar.</summary>
        public List<string>                 AlertHistory { get; set; } = new();
    }

    public static class RsiLLStateManager
    {
        private static readonly string StatePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "rsi_ll_state.json");

        public static RsiLLState Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return new RsiLLState();
                var json = File.ReadAllText(StatePath);
                return JsonConvert.DeserializeObject<RsiLLState>(json) ?? new RsiLLState();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load RSI-LL state — starting fresh");
                return new RsiLLState();
            }
        }

        public static void Save(RsiLLState state)
        {
            try
            {
                // Prune expired cooldowns before saving to keep the file tidy
                var now     = DateTime.Now;
                var expired = state.Cooldowns.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
                foreach (var key in expired) state.Cooldowns.Remove(key);

                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(StatePath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save RSI-LL state");
            }
        }
    }
}
