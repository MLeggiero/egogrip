using UnityEngine;

namespace Egogrip
{
    /// <summary>
    /// Live RGB preview panel: polls the UVC camera's latest RGBA frame (from EgogripWristCamera /
    /// the egogrip-capture.aar) and shows it on a quad floating in front of the headset. Stays
    /// blank until frames arrive — so it doubles as the empirical test for whether libuvc can pull
    /// a given camera (generic webcam → live image; RealSense D405 → may stay blank, meaning it
    /// needs the librealsense backend in Phase 2).
    ///
    /// Drop on a GameObject and (optionally) assign the EgogripWristCamera; it auto-finds one.
    /// </summary>
    public class EgogripCameraPreview : MonoBehaviour
    {
        public EgogripWristCamera cam;
        [Tooltip("Preview refresh rate (Hz). The camera still records every frame regardless.")]
        public float previewHz = 15f;
        [Tooltip("Panel position relative to the camera: right, up, forward (metres).")]
        public Vector3 localOffset = new Vector3(0.32f, -0.08f, 0.7f);
        public float panelWidth = 0.45f;

        private Texture2D _tex;
        private Renderer _quad;
        private int _w, _h;
        private float _last;

        private void Start()
        {
            if (cam == null) cam = Object.FindFirstObjectByType<EgogripWristCamera>();

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "EgogripCameraPreview";
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var c = Camera.main;
            if (c != null)
            {
                go.transform.SetParent(c.transform, false);
                go.transform.localPosition = localOffset;
                go.transform.localRotation = Quaternion.identity;
            }
            go.transform.localScale = new Vector3(panelWidth, panelWidth, 1f);

            _quad = go.GetComponent<Renderer>();
            // URP project → use the URP unlit shader; mainTexture maps to its _BaseMap.
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            _quad.material = new Material(shader);
        }

        private void Update()
        {
            if (cam == null || _quad == null) return;
            if (Time.realtimeSinceStartup - _last < 1f / Mathf.Max(1f, previewHz)) return;
            _last = Time.realtimeSinceStartup;

            int w = cam.PreviewWidth(), h = cam.PreviewHeight();
            if (w <= 0 || h <= 0) return;

            var bytes = cam.LatestFrame();
            if (bytes == null || bytes.Length < w * h * 4) return;

            if (_tex == null || _w != w || _h != h)
            {
                _tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                _w = w; _h = h;
                _quad.material.mainTexture = _tex;
                // keep aspect ratio of the panel
                _quad.transform.localScale = new Vector3(panelWidth, panelWidth * h / (float)w, 1f);
            }

            _tex.LoadRawTextureData(bytes);
            _tex.Apply(false);
        }
    }
}
