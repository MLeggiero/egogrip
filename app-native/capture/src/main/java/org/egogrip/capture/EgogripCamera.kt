package org.egogrip.capture

import android.content.Context
import java.io.File

/**
 * Unity-facing facade for USB/UVC camera recording. Unity owns the episode directory and the
 * clock; this records `<streamId>.mp4` + `<streamId>_frames.csv` into that directory on the
 * SAME monotonic clock (SystemClock.elapsedRealtimeNanos — see [CaptureClock]) that Unity stamps
 * its pose with, so everything aligns downstream with no offset.
 *
 * Call pattern from Unity (AndroidJavaObject):
 *   val cam = new AndroidJavaObject("org.egogrip.capture.EgogripCamera", activity)
 *   cam.Call<bool>("start", episodeDir, "wrist0", 30)
 *   ... record ...
 *   string streamJson = cam.Call<string>("stop")   // splice into manifest.json "streams"
 *
 * Returns a manifest stream descriptor (JSON) so the Unity layer stays the single author of
 * manifest.json. Degrades gracefully: if the PICO doesn't expose the UVC camera to Camera2,
 * [start] returns false and Unity just records pose without video.
 */
class EgogripCamera(private val context: Context) {

    private var cam: Camera2Client? = null
    private var streamId: String = "wrist0"
    private var lastLog: String = ""

    /** Start recording the (first external/UVC) camera into episodeDir. Returns true if it opened. */
    fun start(episodeDir: String, streamId: String, fps: Int): Boolean {
        this.streamId = streamId
        val dir = File(episodeDir).apply { mkdirs() }
        val c = Camera2Client(context, dir, streamId, fps) { msg -> lastLog = msg }
        val ok = c.start()
        cam = if (ok) c else null
        return ok
    }

    /** Stop and return a manifest stream descriptor (JSON), or "" if nothing was recorded. */
    fun stop(): String {
        val c = cam ?: return ""
        val frames = c.stop()
        cam = null
        if (frames <= 0 || c.width <= 0) return ""
        return """{"id":"$streamId","kind":"video_rgb","file":"$streamId.mp4",""" +
            """"index_file":"${streamId}_frames.csv","timestamp_field":"monotonic_ns",""" +
            """"sample_count":$frames,"codec":"h264",""" +
            """"frame_size":{"width":${c.width},"height":${c.height}}}"""
    }

    fun isActive(): Boolean = cam != null
    fun frameCount(): Int = cam?.frameCount ?: 0
    fun lastMessage(): String = lastLog
}
