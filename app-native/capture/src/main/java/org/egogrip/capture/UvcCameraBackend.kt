package org.egogrip.capture

import android.content.Context
import android.hardware.usb.UsbDevice
import com.herohan.uvcapp.CameraHelper
import com.herohan.uvcapp.ICameraHelper
import com.herohan.uvcapp.VideoCapture
import com.serenegiant.usb.IFrameCallback
import com.serenegiant.usb.UVCCamera
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.nio.ByteBuffer

/**
 * libuvc-based external UVC camera backend (herohan/UVCAndroid). Opens external USB cameras that
 * Android Camera2 can't see — generic USB webcams, and (to test) the RealSense D405's color
 * stream. Delivers RGBA frames via a callback used for BOTH:
 *   - the live Unity preview ([latestFrame] → Texture2D), and
 *   - per-frame timestamps on the shared [CaptureClock] (<streamId>_frames.csv),
 * while herohan records the color stream to <streamId>.mp4.
 *
 * herohan's USBMonitor (inside CameraHelper) handles USB attach + the permission prompt.
 * Note: RealSense cameras may open but not stream over plain libuvc without RealSense init — if
 * no frames arrive for the D405, that's the signal to use the librealsense backend (Phase 2).
 */
class UvcCameraBackend(private val context: Context) {

    private var helper: ICameraHelper? = null
    private var index: BufferedWriter? = null
    private var streamId = "wrist0"
    private var recording = false

    @Volatile var width = 0; private set
    @Volatile var height = 0; private set
    @Volatile var frameCount = 0; private set

    // latest RGBA frame for the Unity preview
    private val lock = Any()
    private var latest: ByteArray? = null

    fun start(episodeDirPath: String, streamId: String, @Suppress("UNUSED_PARAMETER") fps: Int): Boolean {
        this.streamId = streamId
        val dir = File(episodeDirPath).apply { mkdirs() }
        index = BufferedWriter(FileWriter(File(dir, "${streamId}_frames.csv"))).apply {
            write("frame_idx,monotonic_ns,pts_ns"); newLine()
        }
        val h = CameraHelper()
        h.setStateCallback(object : ICameraHelper.StateCallback {
            override fun onAttach(device: UsbDevice) { h.selectDevice(device) }
            override fun onDeviceOpen(device: UsbDevice, isFirstOpen: Boolean) { h.openCamera() }
            override fun onCameraOpen(device: UsbDevice) {
                val size = h.previewSize
                width = size?.width ?: 0
                height = size?.height ?: 0
                // RGBA frames (PIXEL_FORMAT_RGBX) feed both preview and frame-timestamping.
                h.setFrameCallback(IFrameCallback { frame -> onFrame(frame) }, UVCCamera.PIXEL_FORMAT_RGBX)
                h.startPreview()
                try {
                    val opts = VideoCapture.OutputFileOptions.Builder(File(dir, "$streamId.mp4")).build()
                    h.startRecording(opts, object : VideoCapture.OnVideoCaptureCallback {
                        override fun onStart() {}
                        override fun onVideoSaved(result: VideoCapture.OutputFileResults) {}
                        override fun onError(code: Int, message: String, cause: Throwable?) {}
                    })
                    recording = true
                } catch (_: Exception) { /* preview still works without recording */ }
            }
            override fun onCameraClose(device: UsbDevice) {}
            override fun onDeviceClose(device: UsbDevice) {}
            override fun onDetach(device: UsbDevice) {}
            override fun onCancel(device: UsbDevice) {}
        })
        helper = h
        return true // open is async; frames begin once onCameraOpen fires
    }

    private fun onFrame(frame: ByteBuffer) {
        val ns = CaptureClock.nowNs()
        val n = frame.remaining()
        if (n > 0) {
            val buf = ByteArray(n)
            frame.get(buf)
            synchronized(lock) { latest = buf }
        }
        index?.let { it.write("$frameCount,$ns,$ns"); it.newLine() }
        frameCount++
    }

    /** Latest RGBA frame for the Unity preview (width*height*4 bytes), or null. */
    fun latestFrame(): ByteArray? = synchronized(lock) { latest }

    fun stop(): String {
        try { if (recording) helper?.stopRecording() } catch (_: Exception) {}
        try { helper?.stopPreview(); helper?.closeCamera() } catch (_: Exception) {}
        helper = null
        index?.flush(); index?.close(); index = null
        recording = false
        if (frameCount <= 0 || width <= 0) return ""
        return """{"id":"$streamId","kind":"video_rgb","file":"$streamId.mp4",""" +
            """"index_file":"${streamId}_frames.csv","timestamp_field":"monotonic_ns",""" +
            """"sample_count":$frameCount,"codec":"h264",""" +
            """"frame_size":{"width":$width,"height":$height}}"""
    }
}
