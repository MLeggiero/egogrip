using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace Egogrip
{
    /// <summary>
    /// The in-VR HUD: a code-generated, head-locked world-space uGUI Canvas (TextMeshPro text +
    /// RawImage camera feeds) laid out in FIXED ZONES — a given sensor/feed always lives in the same
    /// spot, and toggling a sensor greys its slot/row IN PLACE (nothing reflows). Built entirely at
    /// runtime, so there are no prefabs to author; drop this on a GameObject (it's on EgogripRecorder).
    ///
    /// Requires the UI + TextMeshPro package (com.unity.ugui) and a one-time
    /// "Window ▸ TextMeshPro ▸ Import TMP Essential Resources" (creates the default SDF font). Without
    /// that import TMP renders nothing — the layout is still correct.
    ///
    /// Zones: status bar · video zone (ego + wrist RawImage slots) · sensor roster (right) · signals
    /// strip · device/controls. Two states: while recording the roster hides (you can't toggle
    /// mid-take); video/signals/status stay put so feeds never jump between config and capture.
    ///
    /// Interaction: thumbstick moves a highlight over the roster; grip toggles the selected sensor
    /// (idle-only); A/X records (EgogripPoseRecorder).
    /// </summary>
    public class EgogripHud : MonoBehaviour
    {
        [Header("Placement (world-anchored)")]
        [Tooltip("How far in front of you the panel spawns / recenters (metres).")]
        public float viewDistance = 0.8f;
        [Tooltip("How far in front of the controller the panel floats while dragging (metres).")]
        public float grabDistance = 0.3f;
        public Vector2 canvasSize = new Vector2(1100f, 640f);
        [Tooltip("Metres per canvas unit (1100×640 * 0.00055 ≈ 0.6 m wide).")]
        public float canvasScale = 0.00055f;
        [Tooltip("Right edge of the video zone as a fraction of panel width (roster takes the rest).")]
        public float videoZoneRight = 0.66f;
        [Tooltip("Max wrist-camera tiles shown in the video zone.")]
        public int maxWristSlots = 4;
        [Tooltip("Camera-feed texture refresh rate (Hz). Cameras still record every frame regardless.")]
        public float previewHz = 15f;

        // palette
        static readonly Color Panel  = new Color(0.03f, 0.04f, 0.06f, 0.74f);
        static readonly Color Zone   = new Color(0.07f, 0.09f, 0.12f, 0.55f);
        static readonly Color SlotBg = new Color(0.05f, 0.06f, 0.08f, 1f);
        static readonly Color HiCol  = new Color(0.24f, 0.48f, 0.96f, 0.38f);
        static readonly Color Green  = new Color(0.22f, 1f, 0.42f);
        static readonly Color Red    = new Color(1f, 0.35f, 0.35f);
        static readonly Color Amber  = new Color(1f, 0.76f, 0.29f);
        static readonly Color Grey   = new Color(0.55f, 0.57f, 0.62f);
        static readonly Color Ink    = new Color(0.92f, 0.94f, 0.97f);

        // refs
        private EgogripPoseRecorder _rec;
        private EgogripWristCamera[] _cams = System.Array.Empty<EgogripWristCamera>();
        private readonly EgogripEgoCamera _ego = new EgogripEgoCamera();

        // status
        private TextMeshProUGUI _statusPill, _statusMeta;

        // video
        private class Slot { public RawImage img; public TextMeshProUGUI label, state; public Texture2D tex; public int w, h; }
        private Slot _egoSlot;
        private readonly List<Slot> _wristSlots = new List<Slot>();

        // roster
        private enum RowKind { InputMode, Controller, Head, WristCam, EgoPending, SerialPending }
        private class Row
        {
            public RowKind kind; public XRNode node; public int camIndex;
            public Image highlight, dot; public TextMeshProUGUI label, state;
        }
        private readonly List<Row> _rows = new List<Row>();
        private GameObject _rosterPanel;
        private int _cursor;

        // signals (placeholder until serial lands)
        private TextMeshProUGUI _signalsText;

        // bottom
        private TextMeshProUGUI _deviceText, _controlsText;

        // input
        private static readonly XRNode[] BothHands = { XRNode.RightHand, XRNode.LeftHand };
        private readonly List<InputDevice> _devs = new List<InputDevice>();
        private bool _stickLatched, _prevGrip;
        private float _lastVideo;

        // world anchoring + B/Y grab (tap = recenter, hold = drag)
        private RectTransform _canvasRt;
        private bool _placed;
        private bool _byDown, _dragging;
        private float _byStart;
        private XRNode _grabNode;

        private void Start()
        {
            _rec = Object.FindFirstObjectByType<EgogripPoseRecorder>();
            _cams = Object.FindObjectsByType<EgogripWristCamera>(FindObjectsSortMode.None);
            BuildCanvas();
        }

        // ---------- construction ----------

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("EgogripCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var cam = Camera.main;
            if (cam != null) canvas.worldCamera = cam; // world-anchored: NOT parented to the head
            var root = (RectTransform)canvasGo.transform;
            _canvasRt = root;
            root.sizeDelta = canvasSize;
            root.localScale = Vector3.one * canvasScale;
            PlaceInFront(); // spawn in front of the head (retried in Update if the camera isn't ready yet)

            NewImage(root, "Bg", Panel, new Vector2(0, 0), new Vector2(1, 1), 0f);

            // ---- status bar ----
            var status = NewImage(root, "StatusZone", Zone, new Vector2(0, 0.88f), new Vector2(1, 1f)).rectTransform;
            _statusPill = NewText(status, "Pill", 34, TextAlignmentOptions.MidlineLeft, Ink,
                                  new Vector2(0, 0), new Vector2(0.55f, 1));
            _statusMeta = NewText(status, "Meta", 24, TextAlignmentOptions.MidlineRight, Grey,
                                  new Vector2(0.45f, 0), new Vector2(1, 1));

            // ---- video zone : full-width tiles stacked vertically (wide feeds), ego + wrists ----
            var video = NewImage(root, "VideoZone", Zone, new Vector2(0, 0.30f), new Vector2(videoZoneRight, 0.88f)).rectTransform;
            int nWrist = Mathf.Clamp(_cams.Length, 0, maxWristSlots);
            int nTiles = 1 + nWrist; // ego + wrists, each a full-width landscape band
            _egoSlot = RowTile(video, "EgoSlot", "EGO", 0, nTiles);
            for (int i = 0; i < nWrist; i++)
                _wristSlots.Add(RowTile(video, $"WristSlot{i}", $"WRIST {i}", i + 1, nTiles));

            // ---- roster (right) ----
            _rosterPanel = NewImage(root, "RosterZone", Zone, new Vector2(videoZoneRight, 0.30f), new Vector2(1, 0.88f)).gameObject;
            var rosterRt = (RectTransform)_rosterPanel.transform;
            NewText(rosterRt, "RosterHdr", 22, TextAlignmentOptions.TopLeft, Grey,
                    new Vector2(0, 0.92f), new Vector2(1, 1)).text = "SENSORS";
            var rosterBody = NewRect(rosterRt, "RosterBody");
            Stretch(rosterBody, new Vector2(0, 0), new Vector2(1, 0.92f), 2f);
            BuildRows(rosterBody);

            // ---- signals strip ----
            var signals = NewImage(root, "SignalsZone", Zone, new Vector2(0, 0.14f), new Vector2(1, 0.30f)).rectTransform;
            NewText(signals, "SigHdr", 20, TextAlignmentOptions.TopLeft, Grey,
                    new Vector2(0, 0.55f), new Vector2(1, 1)).text = "SIGNALS";
            _signalsText = NewText(signals, "SigBody", 24, TextAlignmentOptions.MidlineLeft, Grey,
                                   new Vector2(0, 0), new Vector2(1, 0.6f));
            _signalsText.text = $"<color=#{Hex(Grey)}>gripper width · tactile — serial: pending</color>";

            // ---- bottom : device + controls ----
            var bottom = NewImage(root, "BottomZone", Zone, new Vector2(0, 0), new Vector2(1, 0.14f)).rectTransform;
            _deviceText = NewText(bottom, "Device", 24, TextAlignmentOptions.MidlineLeft, Ink,
                                  new Vector2(0, 0), new Vector2(0.5f, 1));
            _controlsText = NewText(bottom, "Controls", 22, TextAlignmentOptions.MidlineRight, Grey,
                                    new Vector2(0.4f, 0), new Vector2(1, 1));
        }

        private void BuildRows(RectTransform body)
        {
            _rows.Clear();
            _rows.Add(new Row { kind = RowKind.InputMode });
            if (_rec != null && _rec.controllers != null)
                foreach (var c in _rec.controllers) _rows.Add(new Row { kind = RowKind.Controller, node = c.node });
            _rows.Add(new Row { kind = RowKind.Head });
            for (int i = 0; i < _cams.Length; i++) _rows.Add(new Row { kind = RowKind.WristCam, camIndex = i });
            _rows.Add(new Row { kind = RowKind.EgoPending });
            _rows.Add(new Row { kind = RowKind.SerialPending, node = XRNode.LeftEye });  // tactile marker
            _rows.Add(new Row { kind = RowKind.SerialPending, node = XRNode.RightEye }); // gripper marker

            int n = _rows.Count;
            for (int i = 0; i < n; i++)
            {
                var r = _rows[i];
                var rt = NewRect(body, "Row" + i);
                rt.anchorMin = new Vector2(0, 1f - (i + 1f) / n);
                rt.anchorMax = new Vector2(1, 1f - i / (float)n);
                rt.offsetMin = new Vector2(4, 1); rt.offsetMax = new Vector2(-4, -1);
                r.highlight = NewImage(rt, "hi", HiCol, new Vector2(0, 0), new Vector2(1, 1), 0f);
                r.highlight.enabled = false;
                r.dot = NewImage(rt, "dot", Grey, new Vector2(0.01f, 0.28f), new Vector2(0.08f, 0.72f), 0f);
                r.label = NewText(rt, "lbl", 22, TextAlignmentOptions.MidlineLeft, Ink,
                                  new Vector2(0.10f, 0), new Vector2(0.56f, 1));
                r.state = NewText(rt, "st", 20, TextAlignmentOptions.MidlineRight, Grey,
                                  new Vector2(0.56f, 0), new Vector2(1, 1));
            }
        }

        // ---------- per-frame ----------

        private void Update()
        {
            if (_rec == null) return;
            if (!_placed) PlaceInFront();        // retry until the head camera exists
            HandleGrab();                        // B/Y: tap = recenter, hold = drag (allowed anytime)
            bool rec = _rec.IsRecording;
            bool handsMode = _rec.inputSource == EgogripPoseRecorder.InputSource.Hands;

            // status
            if (rec)
            {
                int s = (int)_rec.RecordingDurationSec;
                _statusPill.text = $"<color=#{Hex(Green)}>●</color> REC  {s / 60:00}:{s % 60:00}";
                _statusMeta.text = $"ep {_rec.CurrentEpisodeId}   {_rec.SampleCount} smp";
            }
            else
            {
                _statusPill.text = "<color=#8A8F98>○</color> IDLE";
                _statusMeta.text = "egogrip";
            }

            // input + cursor (idle only)
            if (!rec) HandleCursor();
            _rosterPanel.SetActive(!rec); // roster only in pre-flight

            if (Time.realtimeSinceStartup - _lastVideo >= 1f / Mathf.Max(1f, previewHz))
            {
                _lastVideo = Time.realtimeSinceStartup;
                UpdateVideo();
            }
            if (!rec) UpdateRows(handsMode);

            // device + controls
            float bat = EgogripDeviceStatus.BatteryPercent();
            long free = EgogripDeviceStatus.FreeBytes();
            string batStr = bat < 0 ? "—" : $"{bat:F0}%{(EgogripDeviceStatus.Charging() ? "+" : "")}";
            string warn = BuildWarnings(bat, free, handsMode);
            _deviceText.text = $"batt {batStr}    free {EgogripDeviceStatus.FormatBytes(free)}"
                             + (warn.Length > 0 ? $"    {warn}" : "");
            _controlsText.text = rec
                ? $"<color=#{Hex(Red)}>● recording…</color>   A/X ■ stop"
                : "A/X ▶ rec    stick ▲▼    grip ⏻ toggle";
        }

        private void UpdateVideo()
        {
            // ego — pending until PICO enterprise access
            if (_ego.Available && _ego.Active)
                DrawSlot(_egoSlot, true, _ego.PreviewWidth(), _ego.PreviewHeight(), _ego.LatestFrame(), null, Grey);
            else
                DrawSlot(_egoSlot, false, 0, 0, null, "enterprise\naccess pending", Amber);

            for (int i = 0; i < _wristSlots.Count; i++)
            {
                var slot = _wristSlots[i];
                var cam = i < _cams.Length ? _cams[i] : null;
                if (cam == null) { DrawSlot(slot, false, 0, 0, null, "—", Grey); continue; }
                slot.label.text = $"WRIST {cam.streamId}";
                if (!cam.captureEnabled) DrawSlot(slot, false, 0, 0, null, "disabled", Grey);
                else if (cam.Active) DrawSlot(slot, true, cam.PreviewWidth(), cam.PreviewHeight(), cam.LatestFrame(), null, Grey);
                else DrawSlot(slot, false, 0, 0, null, "no signal", Grey);
            }
        }

        private static void DrawSlot(Slot s, bool active, int w, int h, byte[] frame, string msg, Color msgColor)
        {
            if (active && frame != null && w > 0 && h > 0 && frame.Length >= w * h * 4)
            {
                if (s.tex == null || s.w != w || s.h != h)
                {
                    s.tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    s.w = w; s.h = h;
                }
                s.img.texture = s.tex;
                s.img.color = Color.white;
                s.tex.LoadRawTextureData(frame);
                s.tex.Apply(false);
                s.state.text = "";
            }
            else
            {
                s.img.texture = null;
                s.img.color = SlotBg;
                s.state.text = msg;
                s.state.color = msgColor;
            }
        }

        private void UpdateRows(bool handsMode)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                r.highlight.enabled = (i == _cursor);
                switch (r.kind)
                {
                    case RowKind.InputMode:
                        r.label.text = "input";
                        if (!handsMode) { r.dot.color = Green; r.state.text = "controllers"; r.state.color = Ink; }
                        else if (!_rec.HandTrackingCompiled) { r.dot.color = Amber; r.state.text = "hands · flag off"; r.state.color = Amber; }
                        else if (!_rec.HandTrackingAvailable) { r.dot.color = Amber; r.state.text = "hands · no track"; r.state.color = Amber; }
                        else { r.dot.color = Green; r.state.text = $"hands  {LR(_rec.LeftHandTracked, _rec.RightHandTracked)}"; r.state.color = Ink; }
                        break;

                    case RowKind.Controller:
                    {
                        string lbl = r.node == XRNode.RightHand ? "R ctrl" : r.node == XRNode.LeftHand ? "L ctrl" : r.node.ToString();
                        r.label.text = lbl;
                        bool on = _rec.GetControllerEnabled(r.node);
                        if (handsMode) { r.dot.color = Grey; r.state.text = "n/a (hands)"; r.state.color = Grey; }
                        else if (!on) { r.dot.color = Grey; r.state.text = "off"; r.state.color = Grey; }
                        else { bool trk = Tracked(r.node); r.dot.color = trk ? Green : Red; r.state.text = trk ? "on · trk" : "on · no trk"; r.state.color = trk ? Ink : Red; }
                        break;
                    }

                    case RowKind.Head:
                        r.label.text = "head";
                        if (handsMode) { r.dot.color = Green; r.state.text = "in poses.jsonl"; r.state.color = Grey; }
                        else if (_rec.recordHead) { r.dot.color = Green; r.state.text = "on"; r.state.color = Ink; }
                        else { r.dot.color = Grey; r.state.text = "off"; r.state.color = Grey; }
                        break;

                    case RowKind.WristCam:
                    {
                        var cam = r.camIndex < _cams.Length ? _cams[r.camIndex] : null;
                        r.label.text = cam != null ? $"wrist {cam.streamId}" : "wrist";
                        if (cam == null) { r.dot.color = Grey; r.state.text = "none"; r.state.color = Grey; }
                        else if (!cam.captureEnabled) { r.dot.color = Grey; r.state.text = "off"; r.state.color = Grey; }
                        else if (cam.Active) { r.dot.color = Green; r.state.text = $"on · {cam.PreviewWidth()}×{cam.PreviewHeight()}"; r.state.color = Ink; }
                        else { r.dot.color = Red; r.state.text = "on · no signal"; r.state.color = Red; }
                        break;
                    }

                    case RowKind.EgoPending:
                        r.label.text = "ego cam"; r.dot.color = Amber; r.state.text = "enterprise: pending"; r.state.color = Amber;
                        break;

                    case RowKind.SerialPending:
                        r.label.text = r.node == XRNode.LeftEye ? "tactile" : "gripper";
                        r.dot.color = Grey; r.state.text = "serial: pending"; r.state.color = Grey;
                        break;
                }
            }
        }

        // ---------- world anchoring + B/Y grab ----------

        // Place the panel a comfortable distance in front of the head, facing the user.
        private void PlaceInFront()
        {
            var cam = Camera.main;
            if (cam == null || _canvasRt == null) return;
            Vector3 fwd = cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = cam.transform.forward; // looking straight up/down
            fwd.Normalize();
            Vector3 pos = cam.transform.position + fwd * viewDistance;
            pos.y = cam.transform.position.y - 0.15f; // just below eye line
            _canvasRt.position = pos;
            // NOTE: if the panel text faces away, flip to LookRotation(cam.position - pos).
            _canvasRt.rotation = Quaternion.LookRotation(pos - cam.transform.position, Vector3.up);
            _placed = true;
        }

        // B/Y (secondaryButton): a quick tap recenters in front; a hold drags the panel with the controller.
        private void HandleGrab()
        {
            bool by = false; XRNode node = XRNode.RightHand;
            foreach (var n in BothHands)
            {
                InputDevices.GetDevicesAtXRNode(n, _devs);
                if (_devs.Count > 0 && _devs[0].TryGetFeatureValue(CommonUsages.secondaryButton, out bool b) && b)
                { by = true; node = n; break; }
            }
            if (by)
            {
                if (!_byDown) { _byDown = true; _byStart = Time.realtimeSinceStartup; _dragging = false; _grabNode = node; }
                else if (Time.realtimeSinceStartup - _byStart > 0.15f) _dragging = true;
                if (_dragging) DragTo(_grabNode);
            }
            else
            {
                if (_byDown && !_dragging) PlaceInFront(); // released without dragging → recenter
                _byDown = false; _dragging = false;
            }
        }

        private void DragTo(XRNode node)
        {
            InputDevices.GetDevicesAtXRNode(node, _devs);
            if (_devs.Count == 0 || _canvasRt == null) return;
            var d = _devs[0];
            if (!d.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 cp)) return;
            d.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion cr);
            Vector3 pos = cp + (cr * Vector3.forward) * grabDistance;
            _canvasRt.position = pos;
            var cam = Camera.main;
            if (cam != null) _canvasRt.rotation = Quaternion.LookRotation(pos - cam.transform.position, Vector3.up);
        }

        // ---------- input ----------

        private void HandleCursor()
        {
            int count = _rows.Count;
            if (count == 0) return;

            Vector2 stick = Vector2.zero;
            bool grip = false;
            foreach (var node in BothHands)
            {
                InputDevices.GetDevicesAtXRNode(node, _devs);
                if (_devs.Count == 0) continue;
                var d = _devs[0];
                if (d.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v) && v.magnitude > stick.magnitude) stick = v;
                if (d.TryGetFeatureValue(CommonUsages.gripButton, out bool g) && g) grip = true;
            }

            if (!_stickLatched && stick.y > 0.6f) { _cursor--; _stickLatched = true; }
            else if (!_stickLatched && stick.y < -0.6f) { _cursor++; _stickLatched = true; }
            else if (Mathf.Abs(stick.y) < 0.3f) _stickLatched = false;
            _cursor = Mathf.Clamp(_cursor, 0, count - 1);

            if (grip && !_prevGrip) Toggle(_rows[_cursor]);
            _prevGrip = grip;
        }

        private void Toggle(Row r)
        {
            switch (r.kind)
            {
                case RowKind.InputMode: _rec.ToggleInputSource(); break;
                case RowKind.Controller: _rec.SetControllerEnabled(r.node, !_rec.GetControllerEnabled(r.node)); break;
                case RowKind.Head: _rec.SetHeadEnabled(!_rec.recordHead); break;
                case RowKind.WristCam:
                    var cam = r.camIndex < _cams.Length ? _cams[r.camIndex] : null;
                    if (cam != null) cam.SetCaptureEnabled(!cam.captureEnabled);
                    break;
                // EgoPending / SerialPending: not available yet → no-op
            }
        }

        // ---------- helpers ----------

        private bool Tracked(XRNode node)
        {
            InputDevices.GetDevicesAtXRNode(node, _devs);
            return _devs.Count > 0 && _devs[0].TryGetFeatureValue(CommonUsages.isTracked, out bool t) && t;
        }

        private string BuildWarnings(float bat, long free, bool handsMode)
        {
            var w = new List<string>();
            if (free >= 0 && free < EgogripDeviceStatus.LowStorageBytes) w.Add("storage low");
            if (bat >= 0 && bat < EgogripDeviceStatus.LowBatteryPct && !EgogripDeviceStatus.Charging()) w.Add("battery low");
            if (handsMode && _rec.HandTrackingCompiled && !_rec.HandTrackingAvailable) w.Add("hands not tracked");
            if (w.Count == 0) return "";
            return $"<color=#{Hex(Amber)}>⚠ {string.Join(" · ", w)}</color>";
        }

        private string LR(bool l, bool r) =>
            $"L<color=#{Hex(l ? Green : Red)}>●</color> R<color=#{Hex(r ? Green : Red)}>●</color>";

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        // ---------- uGUI construction primitives ----------

        private static RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Stretch(RectTransform rt, Vector2 min, Vector2 max, float pad)
        {
            rt.anchorMin = min; rt.anchorMax = max;
            rt.offsetMin = new Vector2(pad, pad); rt.offsetMax = new Vector2(-pad, -pad);
        }

        private static Image NewImage(Transform parent, string name, Color c, Vector2 min, Vector2 max, float pad = 6f)
        {
            var rt = NewRect(parent, name);
            Stretch(rt, min, max, pad);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        // Tile k of n as a full-width horizontal band (top→bottom) — wide landscape camera tiles.
        private static Slot RowTile(Transform parent, string name, string title, int k, int n) =>
            NewSlot(parent, name, title, new Vector2(0, 1f - (k + 1f) / n), new Vector2(1, 1f - k / (float)n));

        private static Slot NewSlot(Transform parent, string name, string title, Vector2 min, Vector2 max)
        {
            var bg = NewImage(parent, name, SlotBg, min, max, 4f);          // slot background (Image)
            var frameRt = NewRect(bg.rectTransform, "frame");              // video frame (RawImage child)
            Stretch(frameRt, new Vector2(0, 0), new Vector2(1, 1), 3f);
            var frame = frameRt.gameObject.AddComponent<RawImage>();
            frame.raycastTarget = false;
            frame.color = SlotBg;
            var label = NewText(bg.rectTransform, "label", 18, TextAlignmentOptions.TopLeft, Ink,
                                new Vector2(0, 0.86f), new Vector2(1, 1));
            label.text = title;
            var state = NewText(bg.rectTransform, "state", 22, TextAlignmentOptions.Center, Grey,
                                new Vector2(0, 0), new Vector2(1, 1));
            return new Slot { img = frame, label = label, state = state };
        }

        private static TextMeshProUGUI NewText(Transform parent, string name, float size,
                                               TextAlignmentOptions align, Color c, Vector2 min, Vector2 max)
        {
            var rt = NewRect(parent, name);
            Stretch(rt, min, max, 4f);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.alignment = align;
            t.color = c;
            t.richText = true;
            t.enableWordWrapping = false;
            t.raycastTarget = false;
            return t;
        }
    }
}
