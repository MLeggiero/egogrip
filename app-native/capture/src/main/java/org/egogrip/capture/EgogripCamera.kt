package org.egogrip.capture

import android.content.Context

/**
 * Unity-facing facade for external USB camera capture. Unity owns the episode directory and the
 * clock; this records `<streamId>.mp4` + `<streamId>_frames.csv` into that directory on the SAME
 * monotonic clock (SystemClock.elapsedRealtimeNanos — see [CaptureClock]) Unity stamps pose with,
 * and exposes the latest RGBA frame for a live in-headset preview.
 *
 * Preview is continuous and decoupled from recording (EgoKit-style): call [openPreview] once at
 * app start so frames flow as soon as a UVC camera is attached, then [beginRecording] /
 * [stopRecording] around each take. [close] tears the camera down.
 *
 * Uses the libuvc backend ([UvcCameraBackend], herohan/UVCAndroid) — verified empirically that on
 * PICO 4 Ultra the USB camera (e.g. Arducam UVC) is exposed ONLY as a raw USB device
 * (/dev/bus/usb/...), NOT as a Camera2 LENS_FACING_EXTERNAL device and NOT as a /dev/video node, so
 * libuvc/libusb is the only way in — the same path EgoKit's XRCoreHelper uses. The Camera2 backend
 * ([Camera2CameraBackend]) is kept as a fallback for devices that DO surface UVC through Camera2
 * (generic Android phones). The RealSense depth path (librealsense) is Phase 2.
 *
 * Call pattern from Unity (AndroidJavaObject):
 *   val cam = new AndroidJavaObject("org.egogrip.capture.EgogripCamera", activity)
 *   cam.Call<bool>("openPreview")                  // once, at Start()
 *   // each frame: cam.Call<int>("previewWidth/Height"), cam.Call<byte[]>("latestFrame") → texture
 *   cam.Call<bool>("beginRecording", episodeDir, "wrist0", 30)
 *   string streamJson = cam.Call<string>("stopRecording")  // splice into manifest.json "streams"
 *   cam.Call("close")                              // on teardown
 */
class EgogripCamera(private val context: Context) {

    private val backend = UvcCameraBackend(context)

    /** Start continuous preview (open is async). Returns true once the backend is listening. */
    fun openPreview(): Boolean = backend.openPreview()

    /** Begin recording the current take into episodeDir. Returns true if recording armed. */
    fun beginRecording(episodeDir: String, streamId: String, @Suppress("UNUSED_PARAMETER") fps: Int): Boolean =
        backend.beginRecording(episodeDir, streamId)

    /** Stop the current take's recording (preview keeps running). Returns a manifest descriptor. */
    fun stopRecording(): String = backend.stopRecording()

    /** Tear down preview + camera. */
    fun close() = backend.close()

    /** True once a camera is open and frames are flowing. */
    fun isAlive(): Boolean = backend.isAlive()

    fun frameCount(): Int = backend.frameCount

    // --- live preview (RGBA, width*height*4 bytes) for Unity ---
    fun latestFrame(): ByteArray? = backend.latestFrame()
    fun previewWidth(): Int = backend.width
    fun previewHeight(): Int = backend.height
}
