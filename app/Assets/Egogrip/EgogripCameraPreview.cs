using UnityEngine;

namespace Egogrip
{
    /// <summary>
    /// DEPRECATED — retired to a no-op. Camera previews now live in fixed RawImage slots inside the
    /// uGUI Canvas HUD (see <see cref="EgogripHud"/>), which pulls frames directly from
    /// <see cref="EgogripWristCamera"/> / <see cref="EgogripEgoCamera"/>. This component used to float
    /// preview quads in world space; it's kept (as a disabled stub) only so the existing scene
    /// reference resolves without a "missing script". Safe to remove from the scene.
    /// </summary>
    public class EgogripCameraPreview : MonoBehaviour
    {
        private void Awake() => enabled = false; // the Canvas HUD owns previews now
    }
}
