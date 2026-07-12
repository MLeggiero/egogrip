using UnityEngine;

namespace Egogrip
{
    /// <summary>
    /// Headset battery + free-storage readout for the HUD. Battery comes from Unity's cross-platform
    /// <see cref="SystemInfo"/>; free storage comes from Android's <c>StatFs</c> on the same
    /// <see cref="Application.persistentDataPath"/> the recorder writes episodes into, so "free" is
    /// the space that actually matters for a take. Everything degrades gracefully in the Editor (no
    /// battery / no StatFs) so the scene still runs.
    ///
    /// Pure static helper — no GameObject, no scene wiring. Callers (EgogripHud) poll it; it caches
    /// the StatFs handle path so it isn't re-created every frame.
    /// </summary>
    public static class EgogripDeviceStatus
    {
        // Pre-flight thresholds (warn-only — the recorder never blocks on these).
        public const long LowStorageBytes = 2L * 1024 * 1024 * 1024; // 2 GB
        public const float LowBatteryPct = 15f;

        /// <summary>Battery charge 0..100, or -1 if the platform doesn't report it (e.g. Editor).</summary>
        public static float BatteryPercent()
        {
            float lvl = SystemInfo.batteryLevel; // 0..1, or -1 when unknown
            return lvl < 0f ? -1f : lvl * 100f;
        }

        /// <summary>True while plugged in / charging (or full).</summary>
        public static bool Charging()
        {
            var s = SystemInfo.batteryStatus;
            return s == BatteryStatus.Charging || s == BatteryStatus.Full;
        }

        /// <summary>Free bytes on the episode volume, or -1 if unavailable (Editor / query failed).</summary>
        public static long FreeBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var statfs = new AndroidJavaObject("android.os.StatFs", Application.persistentDataPath))
                    return statfs.Call<long>("getAvailableBytes"); // API 18+, we min at 29
            }
            catch { return -1L; }
#else
            return -1L;
#endif
        }

        /// <summary>Human-readable size, e.g. "41.2 GB". Returns "—" for a negative (unknown) value.</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "—"; // em dash
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            if (gb >= 1.0) return $"{gb:F1} GB";
            double mb = bytes / (1024.0 * 1024.0);
            return $"{mb:F0} MB";
        }
    }
}
