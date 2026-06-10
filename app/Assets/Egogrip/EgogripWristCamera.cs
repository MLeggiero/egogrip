using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Egogrip
{
    /// <summary>
    /// Bridges to the native egogrip-capture.aar (org.egogrip.capture.EgogripCamera): records a
    /// USB/UVC camera into the current episode dir (wrist0.mp4 + wrist0_frames.csv) on the SAME
    /// monotonic clock Unity stamps pose with, and returns a manifest stream descriptor for
    /// EgogripPoseRecorder to splice into manifest.json.
    ///
    /// Add this component to the scene and drag it onto EgogripPoseRecorder's "Wrist Camera" slot.
    /// Degrades gracefully: no AAR / no UVC camera / no permission → records pose without video.
    /// No-op in the Editor (the AAR is Android-only).
    ///
    /// Requires the powered USB-C hub + a UVC camera plugged into the headset to actually capture.
    /// </summary>
    public class EgogripWristCamera : MonoBehaviour
    {
        [Tooltip("Stream id → <streamId>.mp4 + <streamId>_frames.csv.")]
        public string streamId = "wrist0";
        public int fps = 30;

        public bool Active { get; private set; }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _cam;

        private void Start()
        {
            // Request CAMERA up front so it's granted before the first take.
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                Permission.RequestUserPermission(Permission.Camera);
        }

        /// <summary>Start recording into episodeDir. Returns true if the camera opened.</summary>
        public bool StartInto(string episodeDir)
        {
            try
            {
                if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                {
                    Permission.RequestUserPermission(Permission.Camera);
                    Debug.Log("egogrip: CAMERA permission not granted yet — skipping video this take");
                    return false;
                }
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _cam = new AndroidJavaObject("org.egogrip.capture.EgogripCamera", activity);
                }
                bool ok = _cam.Call<bool>("start", episodeDir, streamId, fps);
                Active = ok;
                Debug.Log($"egogrip: wrist camera {(ok ? "started" : "unavailable (UVC not exposed / not plugged in)")}");
                return ok;
            }
            catch (System.Exception e)
            {
                Debug.Log("egogrip: wrist camera start failed: " + e.Message);
                Active = false;
                return false;
            }
        }

        /// <summary>Stop and return a manifest stream descriptor (JSON), or "" if nothing recorded.</summary>
        public string Stop()
        {
            if (_cam == null) return "";
            try
            {
                string desc = _cam.Call<string>("stop");
                _cam.Dispose();
                _cam = null;
                Active = false;
                Debug.Log($"egogrip: wrist camera stopped ({(string.IsNullOrEmpty(desc) ? "no frames" : "ok")})");
                return desc ?? "";
            }
            catch (System.Exception e)
            {
                Debug.Log("egogrip: wrist camera stop failed: " + e.Message);
                return "";
            }
        }
#else
        // Editor / non-Android stubs.
        public bool StartInto(string episodeDir) => false;
        public string Stop() => "";
#endif
    }
}
