using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Egogrip
{
    /// <summary>
    /// Bridges to the native <c>org.egogrip.capture.EgogripSerial</c> (in egogrip-capture.aar): one
    /// RP2040 (gripper encoder + tactile) over USB-serial. Mirrors <see cref="EgogripWristCamera"/> —
    /// opens a continuous read on Start so live values flow to the HUD, and records
    /// <c>gripper_state&lt;idx&gt;.csv</c> / <c>tactile&lt;idx&gt;.csv</c> / <c>sync_events&lt;idx&gt;.csv</c>
    /// while a take runs. Multiple components each claim a distinct Pico (the native backend de-dupes
    /// devices). Degrades gracefully (no AAR / no device / no permission → pose+camera only). No-op in
    /// the Editor.
    /// </summary>
    public class EgogripSerial : MonoBehaviour
    {
        [Tooltip("Stream index → gripper_state<idx>.csv, tactile<idx>.csv, sync_events<idx>.csv.")]
        public int deviceIndex = 0;

        [Tooltip("When false this Pico is skipped by recording and its USB device released. " +
                 "Toggled from the HUD roster while idle.")]
        public bool captureEnabled = true;

        /// <summary>True once the serial device is open and frames are flowing.</summary>
        public bool Active
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try { return _dev != null && _dev.Call<bool>("isAlive"); } catch { return false; }
#else
                return false;
#endif
            }
        }

        public void SetCaptureEnabled(bool on)
        {
            if (captureEnabled == on) return;
            captureEnabled = on;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (on) OpenPreview(); else Close();
#endif
            Debug.Log($"egogrip: serial{deviceIndex} capture {(on ? "ENABLED" : "DISABLED")}");
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _dev;

        private void Start()
        {
            OpenPreview();
        }

        private void OpenPreview()
        {
            if (_dev != null) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _dev = new AndroidJavaObject("org.egogrip.capture.EgogripSerial", activity);
                }
                bool ok = _dev.Call<bool>("openPreview");
                Debug.Log($"egogrip: serial{deviceIndex} preview {(ok ? "opening (waiting for RP2040)" : "no unclaimed device")}");
            }
            catch (System.Exception e) { Debug.Log("egogrip: serial init failed: " + e.Message); _dev = null; }
        }

        public bool StartInto(string episodeDir)
        {
            if (_dev == null) OpenPreview();
            if (_dev == null) return false;
            try { return _dev.Call<bool>("beginRecording", episodeDir, deviceIndex); }
            catch (System.Exception e) { Debug.Log("egogrip: serial beginRecording failed: " + e.Message); return false; }
        }

        public string Stop()
        {
            if (_dev == null) return "";
            try { return _dev.Call<string>("stopRecording") ?? ""; }
            catch (System.Exception e) { Debug.Log("egogrip: serial stopRecording failed: " + e.Message); return ""; }
        }

        private void Close()
        {
            if (_dev == null) return;
            try { _dev.Call("close"); } catch { }
            try { _dev.Dispose(); } catch { }
            _dev = null;
        }

        private void OnDestroy() => Close();
        private void OnApplicationPause(bool paused) { if (paused) Close(); else OpenPreview(); }

        public float LatestWidthMeters { get { try { return _dev == null ? 0f : (float)_dev.Call<double>("latestWidthPreview"); } catch { return 0f; } } }
        public int[] LatestTactile { get { try { return _dev?.Call<int[]>("latestTactile"); } catch { return null; } } }
        public int TactileChannels { get { try { return _dev == null ? 0 : _dev.Call<int>("tactileChannels"); } catch { return 0; } } }
#else
        // Editor / non-Android stubs.
        public bool StartInto(string episodeDir) => false;
        public string Stop() => "";
        public float LatestWidthMeters => 0f;
        public int[] LatestTactile => null;
        public int TactileChannels => 0;
#endif
    }
}
