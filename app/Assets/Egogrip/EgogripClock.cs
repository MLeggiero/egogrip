using UnityEngine;

namespace Egogrip
{
    /// <summary>
    /// The single monotonic clock shared with the native capture core. Returns the SAME value as
    /// Android's <c>SystemClock.elapsedRealtimeNanos()</c> — which is exactly what
    /// app-native's <c>CaptureClock</c> (and the serial/camera writers) stamp every sample with.
    /// Using one clock across Unity poses and the native AAR streams is what makes downstream
    /// alignment tractable (docs/SYNC.md). In the Editor it falls back to Unity time so scenes
    /// still run.
    /// </summary>
    public static class EgogripClock
    {
        private static AndroidJavaClass _systemClock;

        public static long NowNs()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_systemClock == null)
                _systemClock = new AndroidJavaClass("android.os.SystemClock");
            return _systemClock.CallStatic<long>("elapsedRealtimeNanos");
#else
            return (long)(Time.realtimeSinceStartupAsDouble * 1e9);
#endif
        }
    }
}
