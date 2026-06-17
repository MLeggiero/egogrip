using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace Egogrip
{
    /// <summary>
    /// Bridges to the native egogrip-capture.aar (org.egogrip.capture.EgogripCamera): opens a
    /// USB/UVC camera for a CONTINUOUS live preview the moment it's attached (EgoKit-style), and —
    /// while a take is recording — writes it into the episode dir (wrist0.mp4 + wrist0_frames.csv)
    /// on the SAME monotonic clock Unity stamps pose with, returning a manifest stream descriptor
    /// for EgogripPoseRecorder to splice into manifest.json.
    ///
    /// Add this component to the scene and drag it onto EgogripPoseRecorder's "Wrist Camera" slot.
    /// Drop an EgogripCameraPreview anywhere to see the live image. Preview runs as soon as the
    /// camera is plugged in — you do NOT need to be recording.
    /// Degrades gracefully: no AAR / no UVC camera / no permission → records pose without video.
    /// No-op in the Editor (the AAR is Android-only).
    ///
    /// Requires a UVC camera plugged into the headset (directly, or via a powered USB-C hub).
    /// </summary>
    public class EgogripWristCamera : MonoBehaviour
    {
        [Tooltip("Stream id → <streamId>.mp4 + <streamId>_frames.csv.")]
        public string streamId = "wrist0";
        public int fps = 30;

        /// <summary>True once a camera is open and frames are flowing (preview live).</summary>
        public bool Active
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                try { return _cam != null && _cam.Call<bool>("isAlive"); } catch { return false; }
#else
                return false;
#endif
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _cam;

        private void Start()
        {
            // Request CAMERA up front, then open a continuous preview so frames flow on attach.
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                Permission.RequestUserPermission(Permission.Camera);
            OpenPreview();
        }

        private void OpenPreview()
        {
            if (_cam != null) return;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _cam = new AndroidJavaObject("org.egogrip.capture.EgogripCamera", activity);
                }
                bool ok = _cam.Call<bool>("openPreview");
                Debug.Log($"egogrip: wrist camera preview {(ok ? "opening (waiting for UVC attach)" : "failed to start")}");
            }
            catch (System.Exception e)
            {
                Debug.Log("egogrip: wrist camera preview init failed: " + e.Message);
                _cam = null;
            }
        }

        /// <summary>Begin recording the current take into episodeDir. Returns true if armed.</summary>
        public bool StartInto(string episodeDir)
        {
            if (_cam == null) OpenPreview();
            if (_cam == null) return false;
            try
            {
                bool ok = _cam.Call<bool>("beginRecording", episodeDir, streamId, fps);
                Debug.Log($"egogrip: wrist camera recording {(ok ? "armed" : "not armed (camera not open yet / UVC not plugged in)")}");
                return ok;
            }
            catch (System.Exception e)
            {
                Debug.Log("egogrip: wrist camera beginRecording failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Stop this take's recording (preview keeps running). Returns a manifest descriptor.</summary>
        public string Stop()
        {
            if (_cam == null) return "";
            try
            {
                string desc = _cam.Call<string>("stopRecording");
                Debug.Log($"egogrip: wrist camera recording stopped ({(string.IsNullOrEmpty(desc) ? "no frames" : "ok")})");
                return desc ?? "";
            }
            catch (System.Exception e)
            {
                Debug.Log("egogrip: wrist camera stopRecording failed: " + e.Message);
                return "";
            }
        }

        private void Close()
        {
            if (_cam == null) return;
            try { _cam.Call("close"); } catch { }
            try { _cam.Dispose(); } catch { }
            _cam = null;
        }

        private void OnDestroy() => Close();
        private void OnApplicationPause(bool paused) { if (paused) Close(); else OpenPreview(); }

        // --- live preview (RGBA frame for EgogripCameraPreview) ---
        public byte[] LatestFrame() { try { return _cam?.Call<byte[]>("latestFrame"); } catch { return null; } }
        public int PreviewWidth()  { try { return _cam == null ? 0 : _cam.Call<int>("previewWidth"); } catch { return 0; } }
        public int PreviewHeight() { try { return _cam == null ? 0 : _cam.Call<int>("previewHeight"); } catch { return 0; } }
#else
        // Editor / non-Android stubs.
        public bool StartInto(string episodeDir) => false;
        public string Stop() => "";
        public byte[] LatestFrame() => null;
        public int PreviewWidth() => 0;
        public int PreviewHeight() => 0;
#endif
    }
}
