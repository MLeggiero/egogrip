using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.Android;

namespace Egogrip
{
    /// <summary>
    /// The egocentric (headset front) RGB source. Two paths, tried in order:
    ///
    ///  • <b>real</b> (default): the PICO <c>XR_PICO_camera_image</c> OpenXR extension, surfaced by the
    ///    Integration SDK as <see cref="PXR_CameraImage"/> (new in SDK 3.4.0, documented as "camera
    ///    image data (user device)"). This is NOT the old enterprise Main Camera Access path
    ///    (<c>PXR_Enterprise.AcquireVSTCameraFrame</c>), which binds an entitlement to package
    ///    name + device SN. The only gate the runtime applies here is <c>android.permission.CAMERA</c>.
    ///    Delivers RGBA8888 into a CPU buffer — the exact layout <c>_frame</c> already uses — plus
    ///    camera intrinsics/extrinsics for calibration.
    ///  • <b>simulate</b> (fallback): an animated synthetic RGBA frame, so the HUD ego slot and the
    ///    whole capture/pipeline path still exercise end-to-end when the real camera is unavailable.
    ///    Set <see cref="forceSimulate"/> to pin this on.
    ///
    /// Both paths write the SAME <c>_frame</c> + push seam, so recording is identical either way.
    /// Recording goes through the native <c>org.egogrip.capture.EgogripEgoRecorder</c> (MediaCodec
    /// H.264). Spawned + owned by <see cref="EgogripPoseRecorder"/>; the HUD reads its frames.
    /// </summary>
    public class EgogripEgoCamera : MonoBehaviour
    {
        public string streamId = "ego";

        [Header("Source")]
        [Tooltip("Skip the real camera entirely and always generate the synthetic test pattern.")]
        public bool forceSimulate = false;
        [Tooltip("Which passthrough RGB camera to open. LEFT approximates the dominant-eye ego view.")]
        public XrCameraIdPICO cameraId = XrCameraIdPICO.XR_CAMERA_ID_RGB_LEFT_PICO;
        [Tooltip("Preferred capture width; the closest supported resolution is chosen. 0 = largest offered.")]
        public int preferredWidth = 1280;
        public int fps = 30;

        [Header("Simulated fallback")]
        public int simWidth = 640;
        public int simHeight = 480;

        [Tooltip("When false, ego is skipped by recording. Toggled from the HUD roster while idle.")]
        public bool captureEnabled = true;

        private byte[] _frame;
        private int _w, _h;
        private float _last;
        private bool _recording;
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _rec; // org.egogrip.capture.EgogripEgoRecorder
#endif

        // --- real camera state ---
        private enum RealState { Idle, Opening, Streaming, Failed }
        private RealState _real = RealState.Idle;
        private long _lastCaptureTime;
        private string _status = "starting";
        private bool _sessionOpen, _deviceOpen;

        /// <summary>Camera intrinsics of the live feed (focal length, principal point, FOV), if known.</summary>
        public XrCameraIntrinsics Intrinsics { get; private set; }
        /// <summary>Camera pose relative to the XR device, if known.</summary>
        public XrCameraExtrinsics Extrinsics { get; private set; }
        public bool HasCalibration { get; private set; }

        /// <summary>True while the synthetic pattern is the active source.</summary>
        public bool simulate => forceSimulate || _real != RealState.Streaming;

        /// <summary>Short human-readable source state for the HUD roster.</summary>
        public string StatusText => _status;

        public bool Available => forceSimulate || _real == RealState.Streaming || _real == RealState.Failed;
        public bool Active => Available && _frame != null;

        public byte[] LatestFrame() => _frame;
        public int PreviewWidth() => _w;
        public int PreviewHeight() => _h;

        public void SetCaptureEnabled(bool on) { captureEnabled = on; }

        private void Start()
        {
            if (forceSimulate) { _status = "simulate (forced)"; return; }
            RequestCameraPermission();
            OpenRealCameraAsync();
        }

        private static void RequestCameraPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                Debug.Log("egogrip/ego: requesting android.permission.CAMERA");
                Permission.RequestUserPermission(Permission.Camera);
            }
#endif
        }

        // ---------- real camera (XR_PICO_camera_image) ----------

        // Full open sequence, logged step by step: the log is the diagnostic if PICO gates any of it.
        private async void OpenRealCameraAsync()
        {
            _real = RealState.Opening;
            _status = "opening";
            try
            {
                // The OpenXR session has to be up before the extension answers.
                await Task.Delay(1000);

                var r = PXR_CameraImage.GetAvailableCameras(out var ids);
                Debug.Log($"egogrip/ego: GetAvailableCameras -> {r}, n={(ids == null ? 0 : ids.Length)}" +
                          (ids == null ? "" : " [" + string.Join(", ", ids) + "]"));
                if (r != PxrResult.SUCCESS || ids == null || ids.Length == 0)
                {
                    FailReal($"no cameras ({r})");
                    return;
                }

                if (Array.IndexOf(ids, cameraId) < 0)
                {
                    Debug.Log($"egogrip/ego: {cameraId} not offered, falling back to {ids[0]}");
                    cameraId = ids[0];
                }

                LogCameraProperties();

                if (!PickFormat(out int width, out int height, out var fpsEnum))
                {
                    FailReal("no usable resolution/fps");
                    return;
                }

                var dev = await PXR_CameraImage.CreateCameraDeviceAsync(cameraId);
                Debug.Log($"egogrip/ego: CreateCameraDevice({cameraId}) -> {dev}");
                if (dev != PxrResult.SUCCESS) { FailReal($"open denied ({dev})"); return; }
                _deviceOpen = true;

                var ses = await PXR_CameraImage.CreateCameraCaptureSessionAsync(
                    cameraId, width, height, fpsEnum,
                    XrCameraImageFormatPICO.XR_CAMERA_IMAGE_FORMAT_RGBA_8888_PICO,
                    XrCameraDataTransferTypePICO.XR_CAMERA_DATA_TRANSFER_TYPE_RAW_BUFFER_PICO,
                    XrCameraModelPICO.XR_CAMERA_MODEL_PINHOLE_PICO);
                Debug.Log($"egogrip/ego: CreateCameraCaptureSession({width}x{height}@{fpsEnum}) -> {ses}");
                if (ses != PxrResult.SUCCESS) { FailReal($"session failed ({ses})"); return; }
                _sessionOpen = true;

                var beg = PXR_CameraImage.BeginCameraCapture(cameraId);
                Debug.Log($"egogrip/ego: BeginCameraCapture -> {beg}");
                if (beg != PxrResult.SUCCESS) { FailReal($"capture denied ({beg})"); return; }

                ReadCalibration();

                _w = width; _h = height;
                _real = RealState.Streaming;
                _status = $"live {width}x{height}";
                Debug.Log($"egogrip/ego: REAL CAMERA LIVE — {cameraId} {width}x{height}");
            }
            catch (Exception e)
            {
                FailReal("exception: " + e.Message);
            }
        }

        private void LogCameraProperties()
        {
            if (PXR_CameraImage.GetCameraFacingProperties(cameraId, out var facing) == PxrResult.SUCCESS)
                Debug.Log($"egogrip/ego: facing={facing}");
            if (PXR_CameraImage.GetCameraPositionProperties(cameraId, out var pos) == PxrResult.SUCCESS)
                Debug.Log($"egogrip/ego: position={pos}");
            if (PXR_CameraImage.GetCameraCameraTypeProperties(cameraId, out var type) == PxrResult.SUCCESS)
                Debug.Log($"egogrip/ego: type={type}");
        }

        // Choose the supported resolution closest to preferredWidth (or the largest when 0).
        private bool PickFormat(out int width, out int height, out XrCameraImageFpsPICO fpsEnum)
        {
            width = height = 0;
            fpsEnum = XrCameraImageFpsPICO.XR_CAMERA_IMAGE_FPS_30_PICO;

            var rr = PXR_CameraImage.GetCameraImageResolutionCapability(cameraId, out var res);
            Debug.Log($"egogrip/ego: resolutions -> {rr}" +
                      (res == null ? "" : " [" + string.Join(", ", Array.ConvertAll(res, s => $"{s.width}x{s.height}")) + "]"));
            if (rr != PxrResult.SUCCESS || res == null || res.Length == 0) return false;

            int best = 0;
            for (int i = 1; i < res.Length; i++)
            {
                bool better = preferredWidth > 0
                    ? Mathf.Abs(res[i].width - preferredWidth) < Mathf.Abs(res[best].width - preferredWidth)
                    : res[i].width > res[best].width;
                if (better) best = i;
            }
            width = res[best].width; height = res[best].height;

            var fr = PXR_CameraImage.GetCameraImageFpsCapability(cameraId, out var fpsList);
            Debug.Log($"egogrip/ego: fps -> {fr}" +
                      (fpsList == null ? "" : " [" + string.Join(", ", fpsList) + "]"));
            if (fr == PxrResult.SUCCESS && fpsList != null && fpsList.Length > 0)
            {
                fpsEnum = fpsList[0];
                foreach (var f in fpsList)
                {
                    if (fps >= 60 && f == XrCameraImageFpsPICO.XR_CAMERA_IMAGE_FPS_60_PICO) fpsEnum = f;
                    if (fps < 60 && f == XrCameraImageFpsPICO.XR_CAMERA_IMAGE_FPS_30_PICO) fpsEnum = f;
                }
            }

            var fmt = PXR_CameraImage.GetCameraImageFormatCapability(cameraId, out var formats);
            Debug.Log($"egogrip/ego: formats -> {fmt}" +
                      (formats == null ? "" : " [" + string.Join(", ", formats) + "]"));
            return true;
        }

        private void ReadCalibration()
        {
            bool ok = true;
            if (PXR_CameraImage.GetCameraIntrinsics(cameraId, out var intr) == PxrResult.SUCCESS)
            {
                Intrinsics = intr;
                Debug.Log($"egogrip/ego: intrinsics fx={intr.focalLength.X:F2} fy={intr.focalLength.Y:F2} " +
                          $"cx={intr.principalPoint.X:F2} cy={intr.principalPoint.Y:F2} " +
                          $"fov={intr.fov.X:F1}x{intr.fov.Y:F1}");
            }
            else ok = false;

            if (PXR_CameraImage.GetCameraExtrinsics(cameraId, out var extr) == PxrResult.SUCCESS)
            {
                Extrinsics = extr;
                var p = extr.pose.Position;
                Debug.Log($"egogrip/ego: extrinsics t=({p.X:F4}, {p.Y:F4}, {p.Z:F4})");
            }
            else ok = false;

            HasCalibration = ok;
        }

        private void FailReal(string why)
        {
            _real = RealState.Failed;
            _status = "simulate — " + why;
            Debug.LogWarning($"egogrip/ego: real camera unavailable ({why}); falling back to synthetic frames");
            CloseRealCamera();
        }

        // Pull the newest frame the runtime has captured since _lastCaptureTime.
        private void PollReal()
        {
            var acq = PXR_CameraImage.AcquireCameraImage(cameraId, _lastCaptureTime, out ulong imageId, out long captureTime);
            if (acq != PxrResult.SUCCESS) return; // NO_UPDATE between frames is normal

            try
            {
                if (PXR_CameraImage.GetCameraImageData(cameraId, imageId, out var buf) != PxrResult.SUCCESS) return;
                if (buf.buffer == IntPtr.Zero || buf.width == 0 || buf.height == 0) return;

                _w = (int)buf.width; _h = (int)buf.height;
                int rowBytes = _w * 4;
                int n = rowBytes * _h;
                if (_frame == null || _frame.Length != n) _frame = new byte[n];

                if (buf.stride == rowBytes)
                {
                    Marshal.Copy(buf.buffer, _frame, 0, n);
                }
                else
                {
                    // Padded rows — copy row by row so the texture upload stays tight.
                    for (int y = 0; y < _h; y++)
                        Marshal.Copy(IntPtr.Add(buf.buffer, y * (int)buf.stride), _frame, y * rowBytes, rowBytes);
                }
                _lastCaptureTime = captureTime;
            }
            finally
            {
                PXR_CameraImage.ReleaseCameraImage(cameraId, imageId);
            }
        }

        private void CloseRealCamera()
        {
            try
            {
                if (_sessionOpen)
                {
                    PXR_CameraImage.EndCameraCapture(cameraId);
                    PXR_CameraImage.DestroyCameraCaptureSession(cameraId);
                    _sessionOpen = false;
                }
                if (_deviceOpen)
                {
                    PXR_CameraImage.DestroyCameraDevice(cameraId);
                    _deviceOpen = false;
                }
            }
            catch (Exception e) { Debug.Log("egogrip/ego: close failed: " + e.Message); }
        }

        // ---------- frame pump ----------

        private void Update()
        {
            if (fps < 1) fps = 30;
            if (Time.realtimeSinceStartup - _last < 1f / fps) return;
            _last = Time.realtimeSinceStartup;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_real == RealState.Streaming) PollReal();
            else if (_real != RealState.Opening) GenerateSyntheticFrame();
            _pollTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

            if (_recording && _frame != null) PushFrame();

            if (_recording && Time.realtimeSinceStartup - _lastPerfLog > 5f)
            {
                _lastPerfLog = Time.realtimeSinceStartup;
                LogPerf();
            }
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

        // Main-thread cost of handing a frame to the native encoder. The encoder itself now runs on
        // its own thread (EgogripFrameEncoder), so this should only be the JNI array marshal.
        private long _pushTicks, _pollTicks;
        private int _pushCount;
        private float _lastPerfLog;

        private void PushFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_rec == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { _rec.Call("pushFrame", _frame, EgogripClock.NowNs()); } catch { }
            _pushTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _pushCount++;
#endif
        }

        // Rolling main-thread cost report while recording — tells us whether anything is still
        // stalling the render loop, and whether the encoder is keeping up.
        private void LogPerf()
        {
            if (_pushCount == 0) { _pollTicks = 0; return; }
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double push = _pushTicks * toMs / _pushCount;
            double poll = _pollTicks * toMs / _pushCount;
            int drops = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            try { if (_rec != null) drops = _rec.Call<int>("droppedFrames"); } catch { }
#endif
            Debug.Log($"egogrip/ego perf: push {push:F2} ms/frame, camera-poll {poll:F2} ms/frame, " +
                      $"{_pushCount} frames, {drops} dropped by encoder");
            _pushTicks = 0; _pollTicks = 0; _pushCount = 0;
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
            CloseRealCamera();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_rec != null) { try { _rec.Call<string>("stopRecording", streamId, simWidth, simHeight); } catch { } try { _rec.Dispose(); } catch { } _rec = null; }
#endif
        }
    }
}
