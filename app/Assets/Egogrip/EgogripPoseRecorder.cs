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
    /// TCP note: gripper_pose is meant to be controller→TCP (T_ctrl_gripper) already applied.
    /// Until the gripper is built+calibrated that transform is identity, so this logs the raw
    /// controller pose as the TCP stand-in (correct for proving capture).
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

                long t = EgogripClock.NowNs();
                c.csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:G9},{2:G9},{3:G9},{4:G9},{5:G9},{6:G9},{7:G9},{8}",
                    t, p.x, p.y, p.z, q.x, q.y, q.z, q.w, track));
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
            foreach (var c in controllers)
            {
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

        private string BuildManifest(string extraStream)
        {
            string id = Path.GetFileName(_episodeDir);

            var entries = new List<string>();
            foreach (var c in controllers)
                entries.Add("    {\"id\": \"" + c.streamId + "\", \"kind\": \"pose6dof\", \"file\": \"" +
                            c.streamId + ".csv\", \"timestamp_field\": \"monotonic_ns\", " +
                            "\"frame\": \"world\", \"units\": \"m\", \"sample_count\": " + c.count + "}");
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
