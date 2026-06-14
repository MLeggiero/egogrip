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

        [Tooltip("Optional: a USB/UVC wrist camera (egogrip-capture.aar). Leave empty for pose-only.")]
        public EgogripWristCamera wristCamera;

        private string _episodeDir;
        private long _startNs, _stopNs;
        private bool _recording;
        private float _lastLog;
        private readonly List<InputDevice> _devs = new List<InputDevice>();

        public bool IsRecording => _recording;
        public int SampleCount => (controllers != null && controllers.Length > 0) ? controllers[0].count : 0;
        public string CurrentEpisodeId => _episodeDir != null ? Path.GetFileName(_episodeDir) : "(none)";

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

            foreach (var c in controllers)
            {
                var dev = DeviceAt(c.node);

                // toggle recording on the rising edge of either controller's primary button
                if (dev.isValid && dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool btn))
                {
                    if (btn && !c.prevButton) { if (_recording) StopRecording(); else StartRecording(); }
                    c.prevButton = btn;
                }

                if (!_recording || !dev.isValid || c.csv == null) continue;

                dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 p);
                dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion q);
                int track = dev.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked ? 1 : 0;
                c.lastTracked = track;

                // controller pose -> gripper TCP: p' = p + q*tOff ; q' = q * qOff (local offset)
                Vector3 pt = p + q * c.offsetTranslation;
                Quaternion qt = q * c.offsetRot;

                long t = EgogripClock.NowNs();
                c.csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:G9},{2:G9},{3:G9},{4:G9},{5:G9},{6:G9},{7:G9},{8}",
                    t, pt.x, pt.y, pt.z, qt.x, qt.y, qt.z, qt.w, track));
                c.count++;
                if (c.count % 60 == 0) c.csv.Flush();
                _stopNs = t;

                if (doLog)
                    Debug.Log($"egogrip: pose[{c.node}] t={t} p=({p.x:F3},{p.y:F3},{p.z:F3}) tracked={track} n={c.count}");
            }
            if (doLog) _lastLog = Time.realtimeSinceStartup;
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
            foreach (var c in controllers)
            {
                // resolve controller->TCP offset: Inspector default, overridden by config xr_pose
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
                c.csv = new StreamWriter(Path.Combine(_episodeDir, c.streamId + ".csv"));
                c.csv.WriteLine("monotonic_ns,x,y,z,qx,qy,qz,qw,tracking_state");
                c.count = 0;
            }
            if (wristCamera != null) wristCamera.StartInto(_episodeDir);
            _startNs = EgogripClock.NowNs();
            _stopNs = _startNs;
            _recording = true;
            Debug.Log($"egogrip: REC → {_episodeDir}");
        }

        public void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            foreach (var c in controllers) { c.csv?.Flush(); c.csv?.Close(); c.csv = null; }
            string camStream = wristCamera != null ? wristCamera.Stop() : "";
            File.WriteAllText(Path.Combine(_episodeDir, "manifest.json"), BuildManifest(camStream));
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

        private string BuildManifest(string extraStream)
        {
            string id = Path.GetFileName(_episodeDir);

            var entries = new List<string>();
            foreach (var c in controllers)
            {
                Vector3 ot = c.offsetTranslation;
                Quaternion oq = c.offsetRot;
                string off = string.Format(CultureInfo.InvariantCulture,
                    "\"pose_offset\": {{\"translation_m\": [{0:G9}, {1:G9}, {2:G9}], " +
                    "\"rotation_quat_xyzw\": [{3:G9}, {4:G9}, {5:G9}, {6:G9}]}}",
                    ot.x, ot.y, ot.z, oq.x, oq.y, oq.z, oq.w);
                entries.Add("    {\"id\": \"" + c.streamId + "\", \"kind\": \"pose6dof\", \"file\": \"" +
                            c.streamId + ".csv\", \"timestamp_field\": \"monotonic_ns\", " +
                            "\"frame\": \"world\", \"units\": \"m\", \"sample_count\": " + c.count +
                            ", " + off + "}");
            }
            if (!string.IsNullOrEmpty(extraStream))
                entries.Add("    " + extraStream);
            string streams = string.Join(",\n", entries) + "\n";

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"format_version\": \"0.1.0\",\n");
            sb.Append($"  \"episode_id\": \"{id}\",\n");
            sb.Append("  \"task_label\": \"unity controller pose capture\",\n");
            sb.Append("  \"conventions\": {\"length_unit\": \"m\", \"time_unit\": \"ns\", " +
                      "\"world_frame\": \"unity_y_up_lh\", \"quaternion_order\": \"xyzw\"},\n");
            sb.Append("  \"device\": {\n");
            sb.Append($"    \"model\": \"{SystemInfo.deviceModel}\", \"platform\": \"pico\", " +
                      $"\"os\": \"{SystemInfo.operatingSystem}\", \"app_version\": \"0.1.0\",\n");
            sb.Append("    \"capabilities\": {\"ego_rgb\": false, \"ego_depth\": false, " +
                      "\"head_pose\": false, \"hand_tracking\": false, \"controller_pose\": true, " +
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
