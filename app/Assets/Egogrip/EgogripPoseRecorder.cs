using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace Egogrip
{
    /// <summary>
    /// Records one or more PICO controllers' live 6-DoF pose into an egogrip raw episode the
    /// pipeline ingests directly (docs/DATA_FORMAT.md). Each controller becomes its own pose6dof
    /// stream: by default Right → gripper_pose.csv (the primary/TCP action stream) and
    /// Left → gripper_pose_left.csv. Both write:
    ///   monotonic_ns,x,y,z,qx,qy,qz,qw,tracking_state   (+ a pose6dof entry in manifest.json)
    ///
    /// Frame: we log RAW Unity pose (left-handed, +Y up) and declare
    /// capabilities.world_frame = "unity_y_up_lh"; the pipeline's geometry.from_unity normalizes
    /// it to canonical OpenXR (p'=(x,y,-z); q'=(-x,-y,z,w)) on import — no handedness math here.
    ///
    /// TCP: gripper_pose is the controller pose with a fixed controller→TCP offset applied at
    /// capture, so it records the real gripper tool-center pose directly. The offset comes from
    /// capture_config.json (xr_pose.pose_offset), falling back to the Inspector fields; default is
    /// identity. The applied offset is written into the manifest, so the raw controller pose stays
    /// recoverable. See docs/CAPTURE_CONFIG.md.
    ///
    /// Setup: drop on a GameObject in a scene with the PICO XR rig active. Press either
    /// controller's primary button (A/X) to toggle recording, or call StartRecording()/Stop().
    /// </summary>
    public class EgogripPoseRecorder : MonoBehaviour
    {
        [System.Serializable]
        public class ControllerStream
        {
            public XRNode node = XRNode.RightHand;
            [Tooltip("Episode CSV + manifest stream id. Right is conventionally 'gripper_pose'.")]
            public string streamId = "gripper_pose";

            [Tooltip("Inspector default controller->TCP offset (metres, controller-local). " +
                     "capture_config.json's xr_pose.pose_offset overrides this at record time.")]
            public Vector3 poseOffsetTranslation = Vector3.zero;
            [Tooltip("Inspector default rotation offset, degrees [rx,ry,rz] applied as Rz*Ry*Rx.")]
            public Vector3 poseOffsetEulerDeg = Vector3.zero;

            [System.NonSerialized] public Vector3 offsetTranslation = Vector3.zero;
            [System.NonSerialized] public Quaternion offsetRot = Quaternion.identity;
            [System.NonSerialized] public StreamWriter csv;
            [System.NonSerialized] public int count;
            [System.NonSerialized] public bool prevButton;
            [System.NonSerialized] public bool prevButton2;
            [System.NonSerialized] public int lastTracked;
        }

        [Tooltip("Controllers to record — one pose6dof stream each.")]
        public ControllerStream[] controllers =
        {
            new ControllerStream { node = XRNode.RightHand, streamId = "gripper_pose" },
            new ControllerStream { node = XRNode.LeftHand,  streamId = "gripper_pose_left" },
        };

        [Tooltip("Log pose to logcat at this rate (Hz). CSV always records every frame.")]
        public float logHz = 5f;

        [Header("Optional streams (HUD-toggleable)")]
        [Tooltip("Also record the headset (head) 6-DoF pose → head_pose.csv. Toggle with B/Y while idle.")]
        public bool recordHead = false;

        public enum InputSource { Controllers, Hands }
        [Tooltip("Pose source. Controllers → 6-DoF controller pose (gripper_pose). Hands → PICO " +
                 "26-joint hand tracking (poses.jsonl) plus a derived wrist gripper_pose. Toggle in " +
                 "the HUD (grip on the INPUT row) while idle. Hands needs the EGOGRIP_PICO_HANDS build flag.")]
        public InputSource inputSource = InputSource.Controllers;

        [Tooltip("Hands mode: which hand holds the gripper. Its wrist joint feeds the derived gripper_pose.")]
        public XRNode gripperHand = XRNode.RightHand;

        [Tooltip("Optional: a USB/UVC wrist camera (egogrip-capture.aar). Leave empty for pose-only.")]
        public EgogripWristCamera wristCamera;

        [Tooltip("Optional: additional cameras (more wrist cams, an ego cam, etc.). Give each a UNIQUE " +
                 "streamId; each auto-binds to a different physical USB camera. Add one component per camera.")]
        public EgogripWristCamera[] extraCameras;

        // wristCamera + extraCameras, non-null and de-duplicated — the full set we drive.
        private IEnumerable<EgogripWristCamera> AllCameras()
        {
            if (wristCamera != null) yield return wristCamera;
            if (extraCameras != null)
                foreach (var c in extraCameras)
                    if (c != null && c != wristCamera) yield return c;
        }

        // head pose stream (XRNode.Head → head_pose.csv); included when recordHead is on at record start
        private readonly ControllerStream _head = new ControllerStream { node = XRNode.Head, streamId = "head_pose" };
        // streams actually being recorded this episode = controllers (+ head). Snapshotted at start
        // so toggling an option mid-recording can't desync the open CSVs.
        private readonly List<ControllerStream> _active = new List<ControllerStream>();

        private string _episodeDir;
        private long _startNs, _stopNs;
        private bool _recording;
        private float _lastLog;
        private readonly List<InputDevice> _devs = new List<InputDevice>();

        // Hands mode state
        private readonly EgogripHandTracker _hands = new EgogripHandTracker();
        private StreamWriter _handsJsonl;                 // poses.jsonl (skeleton stream)
        private int _handCount;                           // poses.jsonl sample count
        private ControllerStream _handGripper;            // derived gripper_pose (reuses CSV writer + pose_offset)

        public bool IsRecording => _recording;
        public int SampleCount => inputSource == InputSource.Hands
            ? _handCount
            : ((controllers != null && controllers.Length > 0) ? controllers[0].count : 0);
        public string CurrentEpisodeId => _episodeDir != null ? Path.GetFileName(_episodeDir) : "(none)";

        /// <summary>Seconds since the current take armed (on the shared monotonic clock), or 0 when idle.
        /// Drives the HUD recording timer.</summary>
        public double RecordingDurationSec => _recording ? (EgogripClock.NowNs() - _startNs) / 1e9 : 0.0;

        // Hand-tracking status for the HUD.
        public bool HandTrackingAvailable => _hands.Available;
        public bool HandTrackingCompiled => _hands.Compiled;
        public bool LeftHandTracked { get; private set; }
        public bool RightHandTracked { get; private set; }

        /// <summary>Flip Controllers ↔ Hands. Idle-only so it can't change streams mid-take.</summary>
        public void ToggleInputSource()
        {
            if (_recording) return;
            inputSource = inputSource == InputSource.Controllers ? InputSource.Hands : InputSource.Controllers;
            Debug.Log($"egogrip: inputSource={inputSource}");
        }

        private void EnsureControllers()
        {
            if (controllers == null || controllers.Length == 0)
                controllers = new[]
                {
                    new ControllerStream { node = XRNode.RightHand, streamId = "gripper_pose" },
                    new ControllerStream { node = XRNode.LeftHand,  streamId = "gripper_pose_left" },
                };
        }

        private void Start()
        {
            EnsureControllers();
            var subs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            foreach (var s in subs) s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            Debug.Log($"egogrip: PoseRecorder ready. Recording {controllers.Length} controller(s). Press A/X to start/stop.");
        }

        private InputDevice DeviceAt(XRNode node)
        {
            InputDevices.GetDevicesAtXRNode(node, _devs);
            return _devs.Count > 0 ? _devs[0] : default;
        }

        private void Update()
        {
            bool doLog = logHz > 0 && Time.realtimeSinceStartup - _lastLog >= 1f / logHz;

            // ---- controller buttons: A/X toggles recording; B/Y toggles head-frame while idle ----
            foreach (var c in controllers)
            {
                var dev = DeviceAt(c.node);
                if (dev.isValid && dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool btn))
                {
                    if (btn && !c.prevButton) { if (_recording) StopRecording(); else StartRecording(); }
                    c.prevButton = btn;
                }
                if (dev.isValid && dev.TryGetFeatureValue(CommonUsages.secondaryButton, out bool btn2))
                {
                    if (btn2 && !c.prevButton2 && !_recording)
                    {
                        recordHead = !recordHead;
                        Debug.Log($"egogrip: recordHead={recordHead}");
                    }
                    c.prevButton2 = btn2;
                }
            }

            // ---- write pose streams while recording (branch by input source) ----
            if (_recording)
            {
                long t = EgogripClock.NowNs();
                if (inputSource == InputSource.Hands) WriteHandsFrame(t);
                else WriteControllerFrames(t, doLog);
            }
            if (doLog) _lastLog = Time.realtimeSinceStartup;
        }

        // Controllers mode: write each active controller/head pose6dof row.
        private void WriteControllerFrames(long t, bool doLog)
        {
            foreach (var c in _active)
            {
                if (c.csv == null || !ReadPose(c, out Vector3 p, out Quaternion q, out int track)) continue;
                c.lastTracked = track;
                // pose -> TCP: p' = p + q*tOff ; q' = q * qOff (local offset; identity for head)
                Vector3 pt = p + q * c.offsetTranslation;
                Quaternion qt = q * c.offsetRot;
                WritePoseRow(c.csv, t, pt, qt, track);
                c.count++;
                if (c.count % 60 == 0) c.csv.Flush();
                _stopNs = t;
                if (doLog)
                    Debug.Log($"egogrip: pose[{c.node}] t={t} p=({p.x:F3},{p.y:F3},{p.z:F3}) tracked={track} n={c.count}");
            }
        }

        // Hands mode: write one poses.jsonl line (head + 26-joint hands) and one derived gripper_pose row.
        private void WriteHandsFrame(long t)
        {
            if (!_hands.TryGetHands(out var hf)) { LeftHandTracked = RightHandTracked = false; return; }
            LeftHandTracked = hf.leftTracked;
            RightHandTracked = hf.rightTracked;

            // head pose (from centre-eye), same read path controllers use for the head stream
            ReadPose(_head, out Vector3 hp, out Quaternion hq, out _);
            if (_handsJsonl != null)
            {
                _handsJsonl.WriteLine(BuildHandJsonLine(t, hp, hq, hf));
                _handCount++;
                if (_handCount % 30 == 0) _handsJsonl.Flush();
            }

            // derived gripper_pose from the gripper hand's wrist, with the same controller->TCP offset
            bool rightHand = gripperHand != XRNode.LeftHand;
            if (_handGripper?.csv != null && _hands.TryGetWrist(hf, rightHand, out Vector3 wp, out Quaternion wq))
            {
                Vector3 pt = wp + wq * _handGripper.offsetTranslation;
                Quaternion qt = wq * _handGripper.offsetRot;
                int track = (rightHand ? hf.rightTracked : hf.leftTracked) ? 1 : 0;
                WritePoseRow(_handGripper.csv, t, pt, qt, track);
                _handGripper.count++;
                if (_handGripper.count % 30 == 0) _handGripper.csv.Flush();
            }
            _stopNs = t;
        }

        private static void WritePoseRow(StreamWriter w, long t, Vector3 p, Quaternion q, int track) =>
            w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:G9},{2:G9},{3:G9},{4:G9},{5:G9},{6:G9},{7:G9},{8}",
                t, p.x, p.y, p.z, q.x, q.y, q.z, q.w, track));

        // {monotonic_ns, head:{p,q}, hand_l:[26×{p,q}], hand_r:[26×{p,q}]}  (docs/DATA_FORMAT.md)
        private static string BuildHandJsonLine(long t, Vector3 hp, Quaternion hq, EgogripHandTracker.HandFrame hf)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\"monotonic_ns\":").Append(t).Append(",\"head\":");
            AppendPoseJson(sb, hp, hq);
            sb.Append(",\"hand_l\":");
            AppendHandJson(sb, hf.left, hf.leftTracked);
            sb.Append(",\"hand_r\":");
            AppendHandJson(sb, hf.right, hf.rightTracked);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendHandJson(StringBuilder sb, Pose[] joints, bool tracked)
        {
            if (!tracked || joints == null) { sb.Append("null"); return; }
            sb.Append('[');
            for (int i = 0; i < joints.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendPoseJson(sb, joints[i].position, joints[i].rotation);
            }
            sb.Append(']');
        }

        private static void AppendPoseJson(StringBuilder sb, Vector3 p, Quaternion q) =>
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "{{\"p\":[{0:G9},{1:G9},{2:G9}],\"q\":[{3:G9},{4:G9},{5:G9},{6:G9}]}}",
                p.x, p.y, p.z, q.x, q.y, q.z, q.w);

        // Read a stream's 6-DoF pose. Head/CenterEye use the centre-eye usages (fall back to device);
        // controllers/hands use the device usages. Returns false if no valid device at that node.
        private bool ReadPose(ControllerStream c, out Vector3 p, out Quaternion q, out int track)
        {
            p = Vector3.zero; q = Quaternion.identity; track = 0;
            var dev = DeviceAt(c.node);
            if (!dev.isValid) return false;
            if (c.node == XRNode.Head || c.node == XRNode.CenterEye)
            {
                if (!dev.TryGetFeatureValue(CommonUsages.centerEyePosition, out p))
                    dev.TryGetFeatureValue(CommonUsages.devicePosition, out p);
                if (!dev.TryGetFeatureValue(CommonUsages.centerEyeRotation, out q))
                    dev.TryGetFeatureValue(CommonUsages.deviceRotation, out q);
            }
            else
            {
                dev.TryGetFeatureValue(CommonUsages.devicePosition, out p);
                dev.TryGetFeatureValue(CommonUsages.deviceRotation, out q);
            }
            track = dev.TryGetFeatureValue(CommonUsages.isTracked, out bool t) && t ? 1 : 0;
            return true;
        }

        // Snapshot the streams to record this episode: controllers + head (if enabled).
        private void BuildActive()
        {
            _active.Clear();
            foreach (var c in controllers) _active.Add(c);
            if (recordHead) _active.Add(_head);
        }

        // Resolve a stream's controller->TCP offset: Inspector default, overridden by config xr_pose.
        private void ResolveOffset(ControllerStream c, EgogripCaptureConfig.Root cfg)
        {
            c.offsetTranslation = c.poseOffsetTranslation;
            c.offsetRot = EgogripCaptureConfig.EulerOffset(
                c.poseOffsetEulerDeg.x, c.poseOffsetEulerDeg.y, c.poseOffsetEulerDeg.z);
            var sensor = FindXrPose(cfg, c.node);
            if (sensor != null)
            {
                if (!string.IsNullOrEmpty(sensor.stream_id)) c.streamId = sensor.stream_id;
                if (sensor.pose_offset != null)
                    EgogripCaptureConfig.ResolvePoseOffset(
                        sensor.pose_offset, out c.offsetTranslation, out c.offsetRot);
            }
        }

        public void StartRecording()
        {
            if (_recording) return;
            EnsureControllers();
            string id = System.DateTime.Now.ToString("yyyy-MM-dd'T'HH-mm-ss") + "_unity";
            _episodeDir = Path.Combine(Application.persistentDataPath, "episodes", id);
            Directory.CreateDirectory(_episodeDir);
            var cfg = EgogripCaptureConfig.Load();
            Debug.Log(cfg != null
                ? $"egogrip: capture_config.json loaded ({(cfg.sensors != null ? cfg.sensors.Length : 0)} sensors)"
                : "egogrip: no capture_config.json — using Inspector pose offsets");
            if (inputSource == InputSource.Hands)
            {
                // Hands: a skeleton stream (poses.jsonl) + a derived gripper_pose from the gripper wrist.
                _handsJsonl = new StreamWriter(Path.Combine(_episodeDir, "poses.jsonl"));
                _handCount = 0;
                _handGripper = new ControllerStream { node = gripperHand, streamId = "gripper_pose" };
                ResolveOffset(_handGripper, cfg);
                _handGripper.csv = new StreamWriter(Path.Combine(_episodeDir, _handGripper.streamId + ".csv"));
                _handGripper.csv.WriteLine("monotonic_ns,x,y,z,qx,qy,qz,qw,tracking_state");
                _handGripper.count = 0;
                LeftHandTracked = RightHandTracked = false;
            }
            else
            {
                BuildActive();
                foreach (var c in _active)
                {
                    ResolveOffset(c, cfg);
                    c.csv = new StreamWriter(Path.Combine(_episodeDir, c.streamId + ".csv"));
                    c.csv.WriteLine("monotonic_ns,x,y,z,qx,qy,qz,qw,tracking_state");
                    c.count = 0;
                }
            }
            foreach (var cam in AllCameras())
                if (cam.captureEnabled) cam.StartInto(_episodeDir); // panel-toggled off → skipped this take
            _startNs = EgogripClock.NowNs();
            _stopNs = _startNs;
            _recording = true;
            Debug.Log($"egogrip: REC → {_episodeDir}");
        }

        public void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            foreach (var c in _active) { c.csv?.Flush(); c.csv?.Close(); c.csv = null; }
            _handsJsonl?.Flush(); _handsJsonl?.Close(); _handsJsonl = null;
            if (_handGripper?.csv != null) { _handGripper.csv.Flush(); _handGripper.csv.Close(); _handGripper.csv = null; }
            var camStreams = new List<string>();
            foreach (var cam in AllCameras())
            {
                string desc = cam.Stop();
                if (!string.IsNullOrEmpty(desc)) camStreams.Add(desc);
            }
            File.WriteAllText(Path.Combine(_episodeDir, "manifest.json"), BuildManifest(camStreams));
            Debug.Log($"egogrip: Saved → {_episodeDir}");
            Debug.Log($"egogrip: Pull: adb pull {_episodeDir}");
        }

        private void OnDestroy() { if (_recording) StopRecording(); }
        private void OnApplicationPause(bool paused) { if (paused && _recording) StopRecording(); }

        // Map a controller XRNode to the matching enabled xr_pose sensor in capture_config.json.
        private static EgogripCaptureConfig.Sensor FindXrPose(EgogripCaptureConfig.Root cfg, XRNode node)
        {
            if (cfg == null || cfg.sensors == null) return null;
            string want = node == XRNode.LeftHand ? "left_hand"
                        : node == XRNode.RightHand ? "right_hand"
                        : node == XRNode.Head ? "head" : null;
            if (want == null) return null;
            foreach (var s in cfg.sensors)
                if (s != null && s.enabled && s.type == "xr_pose" && s.node == want) return s;
            return null;
        }

        // A pose6dof manifest stream entry for a controller/derived-wrist stream.
        private static string PoseEntry(ControllerStream c)
        {
            Vector3 ot = c.offsetTranslation;
            Quaternion oq = c.offsetRot;
            string off = string.Format(CultureInfo.InvariantCulture,
                "\"pose_offset\": {{\"translation_m\": [{0:G9}, {1:G9}, {2:G9}], " +
                "\"rotation_quat_xyzw\": [{3:G9}, {4:G9}, {5:G9}, {6:G9}]}}",
                ot.x, ot.y, ot.z, oq.x, oq.y, oq.z, oq.w);
            return "    {\"id\": \"" + c.streamId + "\", \"kind\": \"pose6dof\", \"file\": \"" +
                   c.streamId + ".csv\", \"timestamp_field\": \"monotonic_ns\", " +
                   "\"frame\": \"world\", \"units\": \"m\", \"sample_count\": " + c.count + ", " + off + "}";
        }

        private string BuildManifest(List<string> camStreams)
        {
            string id = Path.GetFileName(_episodeDir);

            bool hands = inputSource == InputSource.Hands;
            var entries = new List<string>();
            if (hands)
            {
                if (_handGripper != null) entries.Add(PoseEntry(_handGripper));       // derived wrist gripper_pose
                entries.Add("    {\"id\": \"hands\", \"kind\": \"skeleton\", \"file\": \"poses.jsonl\", " +
                            "\"timestamp_field\": \"monotonic_ns\", \"frame\": \"world\", \"units\": \"m\", " +
                            "\"sample_count\": " + _handCount + ", \"layout\": \"openxr_hand_joints_26\", " +
                            "\"hands\": [\"left\", \"right\"]}");
            }
            else
            {
                foreach (var c in _active) entries.Add(PoseEntry(c));
            }
            if (camStreams != null)
                foreach (var s in camStreams)
                    if (!string.IsNullOrEmpty(s)) entries.Add("    " + s);
            string streams = string.Join(",\n", entries) + "\n";

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"format_version\": \"0.1.0\",\n");
            sb.Append($"  \"episode_id\": \"{id}\",\n");
            sb.Append($"  \"task_label\": \"unity {(hands ? "hand tracking" : "controller pose")} capture\",\n");
            sb.Append("  \"conventions\": {\"length_unit\": \"m\", \"time_unit\": \"ns\", " +
                      "\"world_frame\": \"unity_y_up_lh\", \"quaternion_order\": \"xyzw\"},\n");
            sb.Append("  \"device\": {\n");
            sb.Append($"    \"model\": \"{SystemInfo.deviceModel}\", \"platform\": \"pico\", " +
                      $"\"os\": \"{SystemInfo.operatingSystem}\", \"app_version\": \"0.1.0\",\n");
            sb.Append("    \"capabilities\": {\"ego_rgb\": false, \"ego_depth\": false, " +
                      $"\"head_pose\": {((hands || recordHead) ? "true" : "false")}, " +
                      $"\"hand_tracking\": {(hands ? "true" : "false")}, " +
                      $"\"controller_pose\": {(hands ? "false" : "true")}, " +
                      "\"world_frame\": \"unity_y_up_lh\"}\n");
            sb.Append("  },\n");
            sb.Append($"  \"clock\": {{\"source\": \"SystemClock.elapsedRealtimeNanos\", \"unit\": \"ns\", " +
                      $"\"start_monotonic_ns\": {_startNs}, \"stop_monotonic_ns\": {_stopNs}}},\n");
            sb.Append("  \"streams\": [\n");
            sb.Append(streams.ToString());
            sb.Append("  ],\n");
            sb.Append("  \"status\": \"finalized\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
