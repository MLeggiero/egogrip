using UnityEngine;
using Unity.XR.PXR;

namespace Egogrip
{
    /// <summary>
    /// Turns on PICO video see-through (MR passthrough) so the operator SEES the real world behind
    /// the app instead of a black VR void. This is the standard MR *display* feature and is FREE —
    /// it does NOT require the enterprise Main Camera Access entitlement (that one is only for
    /// recording the ego camera *pixels* into the dataset). Passthrough is essential for egogrip:
    /// you need to see your hands, the gripper, and the objects while collecting demos.
    ///
    /// Drop this on any GameObject in the scene. It enables passthrough on Start and makes the
    /// camera background transparent so the world composites through where nothing is drawn.
    /// </summary>
    public class EgogripPassthrough : MonoBehaviour
    {
        public bool enableOnStart = true;

        private void Start()
        {
            if (enableOnStart) SetPassthrough(true);
        }

        public void SetPassthrough(bool on)
        {
            // Official PICO toggle → native UPxr_SetSeeThroughBackground.
            PXR_Manager.EnableVideoSeeThrough = on;

            // Make the rendered scene composite over the real world: clear to fully transparent.
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f); // alpha 0 = passthrough shows
            }
            Debug.Log($"egogrip: passthrough {(on ? "ON" : "OFF")}");
        }
    }
}
