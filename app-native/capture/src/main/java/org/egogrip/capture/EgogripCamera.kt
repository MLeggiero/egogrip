package org.egogrip.capture

import android.content.Context

/**
 * Unity-facing facade for external USB camera capture. Unity owns the episode directory and the
 * clock; this records `<streamId>.mp4` + `<streamId>_frames.csv` into that directory on the SAME
 * monotonic clock (SystemClock.elapsedRealtimeNanos — see [CaptureClock]) Unity stamps pose with,
 * and exposes the latest RGBA frame for a live in-headset preview.
 *
 * Phase 1 uses the libuvc backend ([UvcCameraBackend]) so it can open external cameras Android
 * Camera2 can't see (generic USB webcams, and to test the RealSense D405's color stream). The
 * RealSense depth path (librealsense) is Phase 2.
 *
 * Call pattern from Unity (AndroidJavaObject):
 *   val cam = new AndroidJavaObject("org.egogrip.capture.EgogripCamera", activity)
 *   cam.Call<bool>("start", episodeDir, "wrist0", 30)
 *   // each frame: cam.Call<int>("previewWidth/Height"), cam.Call<byte[]>("latestFrame") → texture
 *   string streamJson = cam.Call<string>("stop")   // splice into manifest.json "streams"
 */
class EgogripCamera(private val context: Context) {

    private var backend: UvcCameraBackend? = null

    /** Start capture into episodeDir. Returns true if the backend started (open is async). */
    fun start(episodeDir: String, streamId: String, fps: Int): Boolean {
        val b = UvcCameraBackend(context)
        val ok = b.start(episodeDir, streamId, fps)
        backend = if (ok) b else null
        return ok
    }

    /** Stop and return a manifest stream descriptor (JSON), or "" if nothing was recorded. */
    fun stop(): String {
        val s = backend?.stop() ?: ""
        backend = null
        return s
    }

    fun isActive(): Boolean = backend != null
    fun frameCount(): Int = backend?.frameCount ?: 0

    // --- live preview (RGBA, width*height*4 bytes) for Unity ---
    fun latestFrame(): ByteArray? = backend?.latestFrame()
    fun previewWidth(): Int = backend?.width ?: 0
    fun previewHeight(): Int = backend?.height ?: 0
}
