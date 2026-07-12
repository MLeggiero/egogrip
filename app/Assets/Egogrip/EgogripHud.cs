using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Egogrip
{
    /// <summary>
    /// The in-VR status panel: a head-locked (camera-parented) panel showing recording state + timer,
    /// per-stream health (controllers, head, wrist camera(s), ego camera), and device battery/storage,
    /// plus warn-only pre-flight notices. Built entirely at runtime — a legacy <see cref="TextMesh"/>
    /// with rich-text colour tags for the status dots, over a semi-transparent backing quad that
    /// auto-sizes to the text so it stays legible against passthrough. No Canvas / TextMeshPro / scene
    /// wiring: drop this on a GameObject (it's on EgogripRecorder) and rebuild.
    ///
    /// Warn-only: low storage / low battery / no tracking surface as red notices but never block
    /// recording (recording is toggled by the controller A/X button in EgogripPoseRecorder).
    ///
    /// Tune Local Offset / Text Scale / Bg Padding in the Inspector for placement and size.
    /// </summary>
    public class EgogripHud : MonoBehaviour
    {
        [Tooltip("Which controllers to display (match the recorder).")]
        public XRNode[] hands = { XRNode.RightHand, XRNode.LeftHand };

        [Tooltip("Position relative to the camera: right, up, forward (metres).")]
        public Vector3 localOffset = new Vector3(-0.28f, 0.02f, 0.75f);

        [Tooltip("Overall size of the panel.")]
        public float textScale = 0.0035f;

        [Tooltip("Padding around the text for the backing panel, in text units.")]
        public float bgPadding = 14f;

        // Rich-text colours (hex, no alpha) for the status dots and notices.
        private const string Green = "#39FF6A";
        private const string Red = "#FF5A5A";
        private const string Amber = "#FFC24B";
        private const string Grey = "#8A8F98";

        private TextMesh _text;
        private MeshFilter _textFilter;
        private Transform _bg;
        private EgogripPoseRecorder _recorder;
        private readonly EgogripEgoCamera _ego = new EgogripEgoCamera();
        private EgogripWristCamera[] _cams = System.Array.Empty<EgogripWristCamera>();
        private readonly List<InputDevice> _devs = new List<InputDevice>();

        // Selection cursor over the toggleable USB cameras: thumbstick moves it, grip toggles.
        private static readonly XRNode[] BothHands = { XRNode.RightHand, XRNode.LeftHand };
        private int _cursor;
        private bool _stickLatched, _prevGrip;

        private void Start()
        {
            _recorder = Object.FindFirstObjectByType<EgogripPoseRecorder>();
            RefreshCameras();

            // Root: head-locked anchor. Children live in "text units"; the root scale shrinks the whole
            // panel to metres, so backing-quad layout can be done in the same units as the text mesh.
            var root = new GameObject("EgogripHUD").transform;
            var cam = Camera.main;
            if (cam != null)
            {
                root.SetParent(cam.transform, false);
                root.localPosition = localOffset;
                root.localRotation = Quaternion.identity;
            }
            root.localScale = Vector3.one * textScale;

            // Backing panel (behind the text; +z is farther from the camera).
            var bgGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bgGo.name = "EgogripHUDBg";
            var bgCol = bgGo.GetComponent<Collider>();
            if (bgCol != null) Destroy(bgCol);
            _bg = bgGo.transform;
            _bg.SetParent(root, false);
            _bg.localPosition = new Vector3(0f, 0f, 0.02f);
            bgGo.GetComponent<Renderer>().material = MakePanelMaterial(new Color(0.03f, 0.04f, 0.06f, 0.62f));

            // Text (rich-text status), anchored upper-left so it grows right/down from the origin.
            var textGo = new GameObject("EgogripHUDText");
            textGo.transform.SetParent(root, false);
            _text = textGo.AddComponent<TextMesh>();
            _text.fontSize = 64;
            _text.characterSize = 1f;
            _text.richText = true;
            _text.anchor = TextAnchor.UpperLeft;
            _text.alignment = TextAlignment.Left;
            _text.color = Color.white;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _text.font = font;
            textGo.GetComponent<MeshRenderer>().material = font.material;
            _textFilter = textGo.GetComponent<MeshFilter>();
            _text.text = "egogrip starting…";
        }

        // A semi-transparent unlit panel material (URP-correct transparency, works in the URP project).
        private static Material MakePanelMaterial(Color c)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var m = new Material(shader);
            m.SetColor("_BaseColor", c);
            m.color = c;
            // URP transparent surface incantation.
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }

        private void RefreshCameras() =>
            _cams = Object.FindObjectsByType<EgogripWristCamera>(FindObjectsSortMode.None);

        private static string Dot(bool filled, string color) =>
            $"<color={color}>{(filled ? "●" : "○")}</color>"; // ● / ○

        private void Update()
        {
            if (_text == null) return;

            bool rec = _recorder != null && _recorder.IsRecording;
            bool handsMode = _recorder != null && _recorder.inputSource == EgogripPoseRecorder.InputSource.Hands;
            var sb = new System.Text.StringBuilder();
            var warnings = new List<string>();

            // ---- status + timer ----
            if (rec)
            {
                int secs = (int)_recorder.RecordingDurationSec;
                sb.Append($"<color={Green}>● REC  {secs / 60:00}:{secs % 60:00}</color>\n");
                sb.Append($"ep {_recorder.CurrentEpisodeId}\n");
                sb.Append($"n={_recorder.SampleCount}\n\n");
            }
            else
            {
                sb.Append("○ IDLE\n\n\n\n");
            }

            // ---- selection cursor over [input mode, USB cameras] (thumbstick to pick, grip to toggle) ----
            HandleToggleInput(rec);

            // ---- input mode (cursor target 0) ----
            sb.Append("<color=" + Grey + ">INPUT</color>\n");
            {
                string cur = _cursor == 0 ? "►" : " ";
                if (!handsMode)
                    sb.Append($"{cur}{Dot(true, Green)} input: controllers\n");
                else if (_recorder != null && !_recorder.HandTrackingCompiled)
                    sb.Append($"{cur}{Dot(false, Amber)} input: hands  <color={Amber}>build flag off</color>\n");
                else if (_recorder != null && !_recorder.HandTrackingAvailable)
                    sb.Append($"{cur}{Dot(false, Amber)} input: hands  <color={Amber}>not tracked</color>\n");
                else
                {
                    bool lh = _recorder.LeftHandTracked, rh = _recorder.RightHandTracked;
                    sb.Append($"{cur}{Dot(true, Green)} input: hands  L{Dot(lh, lh ? Green : Red)} R{Dot(rh, rh ? Green : Red)}\n");
                }
            }

            // ---- per-stream health (leading space keeps the cursor column aligned) ----
            sb.Append("<color=" + Grey + ">STREAMS</color>\n");
            bool anyTracked = false;
            foreach (var hand in hands)
            {
                bool tracked = ReadTracked(hand, out Vector3 p);
                anyTracked |= tracked;
                string label = hand == XRNode.RightHand ? "R ctrl" : hand == XRNode.LeftHand ? "L ctrl" : hand.ToString();
                sb.Append(tracked
                    ? $" {Dot(true, Green)} {label}   p({p.x,5:F2},{p.y,5:F2},{p.z,5:F2})\n"
                    : $" {Dot(false, Red)} {label}   <color={Red}>not tracked</color>\n");
            }
            if (_recorder != null && _recorder.recordHead)
            {
                bool ht = ReadTracked(XRNode.CenterEye, out _);
                sb.Append($" {Dot(ht, ht ? Green : Red)} head    {(ht ? "tracked" : "not tracked")}\n");
            }
            else
            {
                sb.Append($" {Dot(false, Grey)} head    <color={Grey}>off</color>\n");
            }

            // wrist USB cameras — the toggleable set the cursor selects over.
            if (_cams.Length == 0)
                sb.Append($" {Dot(false, Grey)} wrist   <color={Grey}>none</color>\n");
            for (int i = 0; i < _cams.Length; i++)
            {
                var cam = _cams[i];
                if (cam == null) continue;
                string cur = i + 1 == _cursor ? "►" : " ";
                if (!cam.captureEnabled)
                    sb.Append($"{cur}{Dot(false, Grey)} wrist {cam.streamId}  <color={Grey}>off</color>\n");
                else
                {
                    bool on = cam.Active;
                    string detail = on ? $"{cam.PreviewWidth()}x{cam.PreviewHeight()}" : "no signal";
                    sb.Append($"{cur}{Dot(on, on ? Green : Red)} wrist {cam.streamId}  {detail}\n");
                }
            }

            // ego camera: live only once PICO enterprise access is granted (stub → "pending" today)
            if (_ego.Available && _ego.Active)
                sb.Append($" {Dot(true, Green)} ego     {_ego.PreviewWidth()}x{_ego.PreviewHeight()}\n");
            else
                sb.Append($" {Dot(false, Grey)} ego     <color={Grey}>enterprise: pending</color>\n");

            // ---- device ----
            sb.Append("\n<color=" + Grey + ">DEVICE</color>\n");
            float bat = EgogripDeviceStatus.BatteryPercent();
            long free = EgogripDeviceStatus.FreeBytes();
            string batStr = bat < 0 ? "—" : $"{bat:F0}%{(EgogripDeviceStatus.Charging() ? "+" : "")}";
            sb.Append($"bat {batStr}   free {EgogripDeviceStatus.FormatBytes(free)}\n");

            // ---- warn-only notices ----
            if (free >= 0 && free < EgogripDeviceStatus.LowStorageBytes) warnings.Add("storage low");
            if (bat >= 0 && bat < EgogripDeviceStatus.LowBatteryPct && !EgogripDeviceStatus.Charging())
                warnings.Add("battery low");
            if (!handsMode && !anyTracked) warnings.Add("no controller tracked");
            if (handsMode && _recorder != null && _recorder.HandTrackingCompiled && !_recorder.HandTrackingAvailable)
                warnings.Add("hands not tracked");
            if (warnings.Count > 0)
            {
                sb.Append('\n');
                foreach (var w in warnings) sb.Append($"<color={Amber}>[!] {w}</color>\n");
            }

            // ---- controls ----
            sb.Append("\n<color=" + Grey + ">A/X rec   B/Y head</color>");
            sb.Append($"\n<color={Grey}>stick pick  grip {(rec ? "toggle (idle only)" : "toggle")}</color>");

            _text.text = sb.ToString();
            FitPanel();
        }

        // Read whether the device at an XR node is tracked, plus its position.
        private bool ReadTracked(XRNode node, out Vector3 p)
        {
            p = Vector3.zero;
            InputDevices.GetDevicesAtXRNode(node, _devs);
            if (_devs.Count == 0) return false;
            var d = _devs[0];
            if (node == XRNode.CenterEye || node == XRNode.Head)
            {
                if (!d.TryGetFeatureValue(CommonUsages.centerEyePosition, out p))
                    d.TryGetFeatureValue(CommonUsages.devicePosition, out p);
            }
            else
            {
                d.TryGetFeatureValue(CommonUsages.devicePosition, out p);
            }
            return d.TryGetFeatureValue(CommonUsages.isTracked, out bool t) && t;
        }

        // Move the selection cursor over the USB cameras (thumbstick, edge-latched) and toggle the
        // selected one with grip. Toggling is idle-only so it can't desync an open take's streams.
        private void HandleToggleInput(bool recording)
        {
            int count = 1 + _cams.Length; // cursor target 0 = input mode, 1..N = USB cameras

            // strongest thumbstick across both controllers
            Vector2 stick = Vector2.zero;
            bool grip = false;
            foreach (var node in BothHands)
            {
                InputDevices.GetDevicesAtXRNode(node, _devs);
                if (_devs.Count == 0) continue;
                var d = _devs[0];
                if (d.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v) && v.magnitude > stick.magnitude)
                    stick = v;
                if (d.TryGetFeatureValue(CommonUsages.gripButton, out bool g) && g) grip = true;
            }

            if (!_stickLatched && stick.y > 0.6f) { _cursor--; _stickLatched = true; }
            else if (!_stickLatched && stick.y < -0.6f) { _cursor++; _stickLatched = true; }
            else if (Mathf.Abs(stick.y) < 0.3f) _stickLatched = false;
            _cursor = Mathf.Clamp(_cursor, 0, count - 1);

            if (grip && !_prevGrip && !recording)
            {
                if (_cursor == 0) _recorder?.ToggleInputSource();
                else
                {
                    var cam = _cams[_cursor - 1];
                    if (cam != null) cam.SetCaptureEnabled(!cam.captureEnabled);
                }
            }
            _prevGrip = grip;
        }

        // Size + centre the backing quad to the current text mesh bounds (in text units).
        private void FitPanel()
        {
            if (_bg == null || _textFilter == null) return;
            var mesh = _textFilter.sharedMesh;
            if (mesh == null) return;
            var b = mesh.bounds;
            if (b.size.x <= 0f || b.size.y <= 0f) return;
            _bg.localScale = new Vector3(b.size.x + bgPadding * 2f, b.size.y + bgPadding * 2f, 1f);
            _bg.localPosition = new Vector3(b.center.x, b.center.y, 0.02f);
        }
    }
}
