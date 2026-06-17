using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Egogrip
{
    /// <summary>
    /// Minimal always-visible in-VR HUD: floats the live controller pose + recording status as
    /// world-space text anchored in front of the headset camera. No Canvas/TextMeshPro setup
    /// needed — it builds a 3D TextMesh at runtime, so you just drop this component on any
    /// GameObject (e.g. the EgogripRecorder object) and rebuild.
    ///
    /// This is the "see the data on screen" quick win. The full per-sensor health tiles + camera
    /// preview come later with the AAR sensor framework (B3). Tweak Local Offset / Text Scale in
    /// the Inspector if it's too big/small or poorly placed.
    /// </summary>
    public class EgogripHud : MonoBehaviour
    {
        [Tooltip("Which controllers to display (match the recorder).")]
        public XRNode[] hands = { XRNode.RightHand, XRNode.LeftHand };

        [Tooltip("Position relative to the camera: right, up, forward (metres).")]
        public Vector3 localOffset = new Vector3(-0.18f, -0.12f, 0.6f);

        [Tooltip("Overall size of the HUD text.")]
        public float textScale = 0.004f;

        private TextMesh _text;
        private EgogripPoseRecorder _recorder;
        private EgogripWristCamera _cam;
        private readonly List<InputDevice> _devs = new List<InputDevice>();

        private void Start()
        {
            _recorder = Object.FindFirstObjectByType<EgogripPoseRecorder>();
            _cam = Object.FindFirstObjectByType<EgogripWristCamera>();

            var go = new GameObject("EgogripHUD");
            var cam = Camera.main;
            if (cam != null)
            {
                go.transform.SetParent(cam.transform, false);
                go.transform.localPosition = localOffset;
                go.transform.localRotation = Quaternion.identity;
            }
            go.transform.localScale = Vector3.one * textScale;

            _text = go.AddComponent<TextMesh>();
            _text.fontSize = 64;
            _text.anchor = TextAnchor.UpperLeft;
            _text.alignment = TextAlignment.Left;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.font = font;
            go.GetComponent<MeshRenderer>().material = font.material;
            _text.text = "egogrip HUD starting…";
        }

        private void Update()
        {
            if (_text == null) return;

            bool rec = _recorder != null && _recorder.IsRecording;
            bool anyTracked = false;

            var sb = new System.Text.StringBuilder();
            sb.Append($"egogrip   {(rec ? "● REC" : "○ idle")}\n");
            sb.Append(rec && _recorder != null ? $"ep {_recorder.CurrentEpisodeId}   n={_recorder.SampleCount}\n" : "\n");

            foreach (var hand in hands)
            {
                InputDevices.GetDevicesAtXRNode(hand, _devs);
                bool have = _devs.Count > 0;
                Vector3 p = Vector3.zero; bool tracked = false;
                if (have)
                {
                    var d = _devs[0];
                    d.TryGetFeatureValue(CommonUsages.devicePosition, out p);
                    d.TryGetFeatureValue(CommonUsages.isTracked, out tracked);
                }
                anyTracked |= tracked;
                string label = hand == XRNode.RightHand ? "R" : hand == XRNode.LeftHand ? "L" : hand.ToString();
                sb.Append($"{label} trk={(tracked ? 1 : 0)}  p({p.x,6:F2},{p.y,6:F2},{p.z,6:F2})\n");
            }
            // optional head frame: show its pose when enabled
            if (_recorder != null && _recorder.recordHead)
            {
                InputDevices.GetDevicesAtXRNode(XRNode.CenterEye, _devs);
                Vector3 hp = Vector3.zero; bool ht = false;
                if (_devs.Count > 0)
                {
                    var d = _devs[0];
                    if (!d.TryGetFeatureValue(CommonUsages.centerEyePosition, out hp))
                        d.TryGetFeatureValue(CommonUsages.devicePosition, out hp);
                    d.TryGetFeatureValue(CommonUsages.isTracked, out ht);
                }
                sb.Append($"H trk={(ht ? 1 : 0)}  p({hp.x,6:F2},{hp.y,6:F2},{hp.z,6:F2})\n");
            }

            if (_cam != null)
                sb.Append($"cam: {(_cam.Active ? "ON (UVC seen)" : "off (no UVC / RealSense needs librealsense)")}\n");

            // options + controls
            string inp = _recorder != null ? _recorder.inputSource.ToString().ToLower() : "controllers";
            bool head = _recorder != null && _recorder.recordHead;
            sb.Append($"input: {inp}    head: {(head ? "ON" : "off")}\n");
            sb.Append("A/X rec    B/Y head");

            _text.color = !anyTracked ? Color.red : (rec ? Color.green : Color.white);
            _text.text = sb.ToString();
        }
    }
}
