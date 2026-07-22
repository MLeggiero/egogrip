using UnityEngine;

namespace Egogrip
{
    /// <summary>
    /// The egocentric (headset front) RGB source. On the PICO 4 Ultra Enterprise this is the
    /// enterprise Main Camera Access feed — gated behind an authorized-package entitlement from PICO
    /// (docs/PICO_ENTERPRISE_NOTES.md). Two paths:
    ///
    ///  • <b>simulate</b> (default on): generates an animated synthetic RGBA frame so the HUD ego slot
    ///    goes "live" and, while recording, pushes frames to the native ego encoder → <c>ego.mp4</c> +
    ///    <c>ego_frames.csv</c>. Lets you exercise the whole GUI + capture + pipeline path as if access
    ///    existed — without breaking anything.
    ///  • <b>real</b> (behind the <c>EGOGRIP_PICO_EGO</c> scripting define, off by default): once PICO
    ///    grants access, fill in <see cref="PollReal"/> to pull enterprise-camera frames into the SAME
    ///    <c>_frame</c> + push seam, and flip <c>simulate</c> off. Nothing else in the app changes.
    ///
    /// Recording goes through the native <c>org.egogrip.capture.EgogripEgoRecorder</c> (MediaCodec
    /// H.264). Spawned + owned by <see cref="EgogripPoseRecorder"/>; the HUD reads its frames.
    /// </summary>
    public class EgogripEgoCamera : MonoBehaviour
    {
        public string streamId = "ego";

        [Tooltip("Simulate ego access: synthetic frames feed the HUD slot AND record ego.mp4, as if the " +
                 "enterprise camera worked. Turn off once the real EGOGRIP_PICO_EGO path is wired.")]
        public bool simulate = true;
        public int simWidth = 640;
        public int simHeight = 480;
        public int fps = 30;

        [Tooltip("When false, ego is skipped by recording. Toggled from the HUD roster while idle.")]
        public bool captureEnabled = true;

        private byte[] _frame;
        private int _w, _h;
        private float _last;
        private bool _recording;
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _rec; // org.egogrip.capture.EgogripEgoRecorder
#endif
#if EGOGRIP_PICO_EGO
        private bool _realAvailable;
#endif

        /// <summary>Ego frames are available (simulate on, or the real enterprise feed is up).</summary>
        public bool Available =>
            simulate
#if EGOGRIP_PICO_EGO
            || _realAvailable
#endif
            ;

        public bool Active => Available && _frame != null;

        public byte[] LatestFrame() => _frame;
        public int PreviewWidth() => _w;
        public int PreviewHeight() => _h;

        public void SetCaptureEnabled(bool on) { captureEnabled = on; }

        private void Update()
        {
            if (fps < 1) fps = 30;
            if (Time.realtimeSinceStartup - _last < 1f / fps) return;
            _last = Time.realtimeSinceStartup;

            if (simulate) GenerateSyntheticFrame();
#if EGOGRIP_PICO_EGO
            else PollReal();
#endif
            if (_recording && _frame != null) PushFrame();
        }

        // Animated test pattern (scrolling colour bands + a moving marker) so the ego feed is obviously
        // "live" and distinguishable from a real camera.
        private void GenerateSyntheticFrame()
        {
            _w = simWidth & ~1; _h = simHeight & ~1;
            int n = _w * _h * 4;
            if (_frame == null || _frame.Length != n) _frame = new byte[n];
            float t = Time.realtimeSinceStartup;
            int barY = (int)((Mathf.Sin(t) * 0.5f + 0.5f) * (_h - 1));
            for (int y = 0; y < _h; y++)
            {
                byte r = (byte)((y * 255 / _h));
                byte g = (byte)((int)(t * 60 + y) & 0xFF);
                for (int x = 0; x < _w; x++)
                {
                    int p = (y * _w + x) * 4;
                    byte b = (byte)((x * 255 / _w));
                    bool marker = Mathf.Abs(y - barY) < 3;
                    _frame[p] = marker ? (byte)255 : r;
                    _frame[p + 1] = marker ? (byte)255 : g;
                    _frame[p + 2] = marker ? (byte)0 : b;
                    _frame[p + 3] = 255;
                }
            }
        }

#if EGOGRIP_PICO_EGO
        // TODO (when PICO grants access): pull the enterprise Main Camera Access RGBA frame into
        // _frame, set _w/_h, and _realAvailable. Same push seam as simulate — nothing else changes.
        private void PollReal() { _realAvailable = false; }
#endif

        public bool StartInto(string episodeDir)
        {
            if (!Available) return false;
            _recording = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _rec = new AndroidJavaObject("org.egogrip.capture.EgogripEgoRecorder");
                bool ok = _rec.Call<bool>("beginRecording", episodeDir, streamId, _w > 0 ? _w : simWidth, _h > 0 ? _h : simHeight, fps);
                if (!ok) { _rec.Dispose(); _rec = null; _recording = false; }
                Debug.Log($"egogrip: ego recording {(ok ? "armed" : "failed")}");
                return ok;
            }
            catch (System.Exception e) { Debug.Log("egogrip: ego recorder failed: " + e.Message); _rec = null; _recording = false; return false; }
#else
            return true; // Editor: pretend
#endif
        }

        private void PushFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_rec == null) return;
            try { _rec.Call("pushFrame", _frame, EgogripClock.NowNs()); } catch { }
#endif
        }

        public string Stop()
        {
            _recording = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_rec == null) return "";
            try
            {
                string desc = _rec.Call<string>("stopRecording", streamId, _w > 0 ? _w : simWidth, _h > 0 ? _h : simHeight);
                _rec.Dispose(); _rec = null;
                return desc ?? "";
            }
            catch { _rec = null; return ""; }
#else
            return "";
#endif
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_rec != null) { try { _rec.Call<string>("stopRecording", streamId, simWidth, simHeight); } catch { } try { _rec.Dispose(); } catch { } _rec = null; }
#endif
        }
    }
}
