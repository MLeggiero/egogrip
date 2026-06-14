using System;
using System.IO;
using UnityEngine;

namespace Egogrip
{
    /// <summary>
    /// Loads capture_config.json (schema/capture_config.schema.json) on the headset so editing that
    /// file — not code — changes what is recorded. Uses Unity's JsonUtility, so the [Serializable]
    /// classes below mirror the schema's field names exactly (unknown keys are ignored).
    ///
    /// Search order: Application.persistentDataPath (adb-pushable, the on-device location) ->
    /// StreamingAssets (editor/desktop convenience) -> none (callers fall back to Inspector
    /// defaults). On Android StreamingAssets lives inside the APK, so push the file to
    /// persistentDataPath:  adb push capture_config.json /sdcard/Android/data/&lt;pkg&gt;/files/
    ///
    /// MVP scope: this resolves the xr_pose pose_offset that EgogripPoseRecorder applies. The full
    /// config-driven CaptureManager (cameras + serial) consumes the same classes. See
    /// docs/CAPTURE_CONFIG.md.
    /// </summary>
    public static class EgogripCaptureConfig
    {
        public const string FileName = "capture_config.json";

        [Serializable]
        public class PoseOffset
        {
            public float[] translation_m;
            public float[] rotation_quat_xyzw;
            public float[] rotation_euler_deg;
            public bool from_calibration;
        }

        [Serializable]
        public class Channel { public string name; public string unit; public string location; }

        [Serializable]
        public class SerialStream
        {
            public string packet;
            public string stream_id;
            public string kind;
            public Channel[] channels;
        }

        [Serializable]
        public class Usb { public string vid; public string pid; }

        [Serializable]
        public class Sensor
        {
            public string id;
            public string type;
            public bool enabled = true;
            public float rate_hz;
            public string stream_id;
            // xr_pose
            public string node;
            public string frame;
            public PoseOffset pose_offset;
            // cameras
            public int width;
            public int height;
            public float fps;
            public string codec;
            public int bitrate;
            public int index;
            public Usb usb;
            // rp2040_serial
            public int baud;
            public SerialStream[] streams;

            public string OutStreamId => string.IsNullOrEmpty(stream_id) ? id : stream_id;
        }

        [Serializable]
        public class Root
        {
            public string format_version;
            public string task_label;
            public string calibration_ref;
            public Sensor[] sensors;
        }

        /// <summary>Load the config, or null if none is present / parseable.</summary>
        public static Root Load()
        {
            string[] paths =
            {
                Path.Combine(Application.persistentDataPath, FileName),
                Path.Combine(Application.streamingAssetsPath, FileName),
            };
            foreach (var path in paths)
            {
                try
                {
                    if (File.Exists(path))
                        return JsonUtility.FromJson<Root>(File.ReadAllText(path));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"egogrip: failed to parse {path}: {e.Message}");
                }
            }
            return null;
        }

        /// <summary>Resolve a pose_offset into Unity (translation, rotation). Identity if absent.</summary>
        public static void ResolvePoseOffset(PoseOffset o, out Vector3 t, out Quaternion q)
        {
            t = Vector3.zero;
            q = Quaternion.identity;
            if (o == null) return;
            if (o.translation_m != null && o.translation_m.Length == 3)
                t = new Vector3(o.translation_m[0], o.translation_m[1], o.translation_m[2]);
            if (o.rotation_quat_xyzw != null && o.rotation_quat_xyzw.Length == 4)
                q = new Quaternion(o.rotation_quat_xyzw[0], o.rotation_quat_xyzw[1],
                                   o.rotation_quat_xyzw[2], o.rotation_quat_xyzw[3]);
            else if (o.rotation_euler_deg != null && o.rotation_euler_deg.Length == 3)
                q = EulerOffset(o.rotation_euler_deg[0], o.rotation_euler_deg[1], o.rotation_euler_deg[2]);
        }

        /// <summary>
        /// Build a rotation from euler degrees as Rz*Ry*Rx (apply X, then Y, then Z), matching
        /// egogrip_pipeline.geometry.euler_deg_to_quat so config numbers mean the same on both sides.
        /// </summary>
        public static Quaternion EulerOffset(float rx, float ry, float rz)
        {
            return Quaternion.AngleAxis(rz, Vector3.forward)
                 * Quaternion.AngleAxis(ry, Vector3.up)
                 * Quaternion.AngleAxis(rx, Vector3.right);
        }
    }
}
