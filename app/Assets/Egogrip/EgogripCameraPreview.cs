using UnityEngine;
using UnityEngine.Rendering;

namespace Egogrip
{
    /// <summary>
    /// Live camera preview panels floating in front of the headset. Builds two framed panels at
    /// runtime (no scene edits, same pattern as the HUD):
    ///   • <b>ego</b> — the headline egocentric view. Non-functional today: it shows an "enterprise
    ///     access pending" placeholder until PICO Main Camera Access is granted, at which point the
    ///     single <see cref="EgogripEgoCamera"/> seam feeds it frames and the same panel goes live.
    ///   • <b>wrist</b> — the USB/UVC wrist camera (via EgogripWristCamera / egogrip-capture.aar).
    ///     Shows live RGBA frames when a UVC camera is attached, or a "no signal" placeholder until
    ///     then — which doubles as the empirical "can libuvc pull this camera?" test.
    ///
    /// Both panels are head-locked and Inspector-tunable. Recording is unaffected — this is display
    /// only; the camera still records every frame regardless of the preview refresh rate.
    /// </summary>
    public class EgogripCameraPreview : MonoBehaviour
    {
        [Tooltip("Wrist UVC camera source (auto-found if left empty).")]
        public EgogripWristCamera cam;

        [Tooltip("Preview refresh rate (Hz). The camera still records every frame regardless.")]
        public float previewHz = 15f;

        [Header("Ego panel (headline; enterprise access pending)")]
        public Vector3 egoLocalOffset = new Vector3(0.30f, -0.02f, 0.85f);
        public float egoPanelWidth = 0.34f;

        [Header("Wrist panel")]
        public Vector3 wristLocalOffset = new Vector3(0.62f, -0.12f, 0.85f);
        public float wristPanelWidth = 0.22f;

        private readonly EgogripEgoCamera _ego = new EgogripEgoCamera();
        private PreviewPanel _egoPanel, _wristPanel;
        private float _last;

        private void Start()
        {
            if (cam == null) cam = Object.FindFirstObjectByType<EgogripWristCamera>();
            var c = Camera.main != null ? Camera.main.transform : null;
            _egoPanel = new PreviewPanel(c, egoLocalOffset, egoPanelWidth, "EGO");
            _wristPanel = new PreviewPanel(c, wristLocalOffset, wristPanelWidth, "WRIST");
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup - _last < 1f / Mathf.Max(1f, previewHz)) return;
            _last = Time.realtimeSinceStartup;

            // Ego: live only once enterprise access is wired into EgogripEgoCamera; placeholder today.
            if (_ego.Available && _ego.Active)
                _egoPanel.ShowFrame(_ego.PreviewWidth(), _ego.PreviewHeight(), _ego.LatestFrame());
            else
                _egoPanel.ShowStatus("enterprise\naccess pending", PreviewPanel.Amber);

            // Wrist: disabled (panel-toggled off) → "disabled"; live UVC frames; else "no signal".
            if (cam != null && !cam.captureEnabled)
                _wristPanel.ShowStatus("disabled", PreviewPanel.Grey);
            else if (cam != null && cam.Active)
                _wristPanel.ShowFrame(cam.PreviewWidth(), cam.PreviewHeight(), cam.LatestFrame());
            else
                _wristPanel.ShowStatus("no signal", PreviewPanel.Grey);
        }

        /// <summary>One framed preview panel: dark border + image quad + a title and a centre status
        /// label. Constructed under the head camera; updated each tick with either a frame or a
        /// status message.</summary>
        private class PreviewPanel
        {
            public const string Amber = "#FFC24B";
            public const string Grey = "#8A8F98";

            private readonly Transform _image;
            private readonly Material _imageMat;
            private readonly TextMesh _status;
            private readonly Transform _border;
            private readonly float _width;
            private Texture2D _tex;
            private int _w, _h;

            public PreviewPanel(Transform camera, Vector3 offset, float width, string title)
            {
                _width = width;
                var root = new GameObject("EgogripPreview_" + title).transform;
                if (camera != null) root.SetParent(camera, false);
                root.localPosition = offset;
                root.localRotation = Quaternion.identity;

                _border = MakeQuad(root, "border", TransparentUnlit(new Color(0.02f, 0.03f, 0.05f, 0.7f)));
                _border.localPosition = new Vector3(0f, 0f, 0.002f); // behind the image
                _border.localScale = new Vector3(width * 1.07f, width * 1.07f, 1f);

                _imageMat = UnlitTex();
                _image = MakeQuad(root, "image", _imageMat);
                _image.localScale = new Vector3(width, width, 1f);

                var titleTm = MakeText(root, TextAnchor.LowerCenter);
                titleTm.transform.localPosition = new Vector3(0f, width * 0.5f + 0.012f, -0.001f);
                titleTm.text = title;

                _status = MakeText(root, TextAnchor.MiddleCenter);
                _status.transform.localPosition = new Vector3(0f, 0f, -0.001f);
            }

            public void ShowStatus(string msg, string color)
            {
                _imageMat.mainTexture = null;
                _imageMat.SetColor("_BaseColor", new Color(0.05f, 0.06f, 0.08f, 1f));
                _status.text = $"<color={color}>{msg}</color>";
            }

            public void ShowFrame(int w, int h, byte[] bytes)
            {
                if (w <= 0 || h <= 0 || bytes == null || bytes.Length < w * h * 4)
                {
                    ShowStatus("no signal", Grey);
                    return;
                }
                if (_tex == null || _w != w || _h != h)
                {
                    _tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    _w = w; _h = h;
                    // preserve aspect ratio on both image and border
                    float hy = _width * h / (float)w;
                    _image.localScale = new Vector3(_width, hy, 1f);
                    _border.localScale = new Vector3(_width * 1.07f, hy * 1.07f, 1f);
                }
                // (re)bind every frame — idempotent, and recovers after a ShowStatus cleared the image
                _imageMat.SetColor("_BaseColor", Color.white);
                _imageMat.mainTexture = _tex;
                _tex.LoadRawTextureData(bytes);
                _tex.Apply(false);
                _status.text = "";
            }

            private static Transform MakeQuad(Transform parent, string name, Material mat)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = name;
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                go.transform.SetParent(parent, false);
                go.GetComponent<Renderer>().material = mat;
                return go.transform;
            }

            private static TextMesh MakeText(Transform parent, TextAnchor anchor)
            {
                var go = new GameObject("label");
                go.transform.SetParent(parent, false);
                var tm = go.AddComponent<TextMesh>();
                tm.fontSize = 64;
                tm.characterSize = 1f;
                tm.richText = true;
                tm.anchor = anchor;
                tm.alignment = TextAlignment.Center;
                tm.color = Color.white;
                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                tm.font = font;
                go.GetComponent<MeshRenderer>().material = font.material;
                go.transform.localScale = Vector3.one * 0.0012f;
                return tm;
            }

            private static Material UnlitTex()
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
                var m = new Material(shader);
                m.SetColor("_BaseColor", new Color(0.05f, 0.06f, 0.08f, 1f));
                return m;
            }

            private static Material TransparentUnlit(Color c)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                var m = new Material(shader);
                m.SetColor("_BaseColor", c);
                m.color = c;
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
                return m;
            }
        }
    }
}
