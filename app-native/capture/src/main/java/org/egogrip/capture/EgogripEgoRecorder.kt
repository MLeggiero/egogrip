package org.egogrip.capture

/**
 * Unity-facing facade around [EgogripFrameEncoder] for the egocentric camera stream. Unity pushes
 * RGBA frames (synthetic today; the real PICO enterprise Main Camera Access frames once granted) and
 * this encodes `<streamId>.mp4` + `<streamId>_frames.csv`.
 *
 *   val ego = new AndroidJavaObject("org.egogrip.capture.EgogripEgoRecorder")
 *   ego.Call<bool>("beginRecording", episodeDir, "ego", 1280, 960, 30)
 *   // each frame while recording: ego.Call("pushFrame", rgbaBytes, monotonicNs)
 *   string streamJson = ego.Call<string>("stopRecording", "ego", 1280, 960)
 */
class EgogripEgoRecorder {
    private var enc: EgogripFrameEncoder? = null

    fun beginRecording(episodeDir: String, streamId: String, w: Int, h: Int, fps: Int): Boolean {
        val e = EgogripFrameEncoder()
        val ok = e.start(episodeDir, streamId, w, h, fps)
        if (ok) enc = e
        return ok
    }

    fun pushFrame(rgba: ByteArray, monotonicNs: Long) = enc?.pushFrame(rgba, monotonicNs) ?: Unit

    /**
     * Direct input buffer (`width*height*4` RGBA). Unity resolves its address ONCE with
     * `AndroidJNI.GetDirectBufferAddress`, memcpys pixels straight in, then calls [pushFrameDirect].
     * Avoids marshalling a multi-MB `byte[]` across JNI on every single frame.
     */
    fun inputBuffer(): java.nio.ByteBuffer? = enc?.inputBuffer()

    /** Encode whatever was just written into [inputBuffer]. */
    fun pushFrameDirect(monotonicNs: Long) = enc?.pushFrameDirect(monotonicNs) ?: Unit

    /** Frames discarded because the encoder fell behind (0 is healthy). */
    fun droppedFrames(): Int = enc?.droppedFrames() ?: 0

    /** Encode an ARCore/Camera2 YUV_420_888 frame directly (phone ego path). */
    fun pushImage(image: android.media.Image, monotonicNs: Long) = enc?.pushImage(image, monotonicNs) ?: Unit

    /** Finalize; returns the manifest stream descriptor (empty if nothing was written). */
    fun stopRecording(streamId: String, w: Int, h: Int): String {
        val e = enc ?: return ""
        val frames = e.stop()
        enc = null
        if (frames <= 0) return ""
        return """{"id":"$streamId","kind":"video_rgb","file":"$streamId.mp4",""" +
            """"index_file":"${streamId}_frames.csv","timestamp_field":"monotonic_ns",""" +
            """"sample_count":$frames,"codec":"h264",""" +
            """"frame_size":{"width":${w and 1.inv()},"height":${h and 1.inv()}}}"""
    }

    fun close() { enc?.stop(); enc = null }
}
