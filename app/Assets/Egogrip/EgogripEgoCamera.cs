namespace Egogrip
{
    /// <summary>
    /// The egocentric (headset front) RGB source. On the PICO 4 Ultra <b>Enterprise</b> this is the
    /// video-see-through / Main Camera Access feed — but that API is gated behind an authorized
    /// package entitlement from PICO (docs/PICO_ENTERPRISE_NOTES.md). Until that access is granted
    /// this is an <b>inert stub</b>: it reports itself unavailable and produces no frames, so the GUI
    /// can lay out and label the ego panel today without any live pixels.
    ///
    /// It deliberately mirrors the read surface of <see cref="EgogripWristCamera"/>
    /// (<see cref="Available"/>, <see cref="Active"/>, <see cref="LatestFrame"/>,
    /// <see cref="PreviewWidth"/>, <see cref="PreviewHeight"/>) so EgogripCameraPreview / EgogripHud
    /// treat ego and wrist uniformly. When Main Camera Access is approved, this is the single seam to
    /// implement: bridge to the enterprise camera (RGBA frames + size) and flip <see cref="Available"/>
    /// true — no GUI/layout change needed. Plain C# object (not a MonoBehaviour): the preview holds
    /// one, so there is nothing to attach in the Unity scene.
    /// </summary>
    public class EgogripEgoCamera
    {
        public string streamId = "ego";

        /// <summary>
        /// False until PICO enterprise Main Camera Access is granted and wired in. Drives the GUI's
        /// "enterprise access pending" placeholder vs. a live view.
        /// </summary>
        public bool Available => false;

        /// <summary>True once the ego camera is open and frames are flowing. Always false while stubbed.</summary>
        public bool Active => false;

        // Frame surface, matching EgogripWristCamera. No frames until enterprise access exists.
        public byte[] LatestFrame() => null;
        public int PreviewWidth() => 0;
        public int PreviewHeight() => 0;
    }
}
