package org.egogrip.capture

// OPTIONAL camera module — NOT compiled by default (lives outside app/src). To use:
//   1. uncomment `com.herohan:UVCAndroid` in app/build.gradle.kts
//   2. move this file into app/src/main/java/org/egogrip/capture/
//   3. wire it in MainActivity (see bottom of this file)
//
// API NOTE: herohan/UVCAndroid's high-level helper class/method names vary slightly across
// versions. This targets the `ICameraHelper` pattern; if your installed version differs,
// adjust the imports/method names (the structure stays the same). Synchronized UVC->mp4
// encoding is a follow-up (docs/NATIVE_APP_PLAN.md); this proves the camera streams and logs
// per-frame timestamps on the shared clock.

import android.content.Context
import android.hardware.usb.UsbDevice
import android.view.Surface
import com.herohan.uvcapp.CameraHelper
import com.herohan.uvcapp.ICameraHelper
import com.serenegiant.usb.Size
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter

/**
 * Opens a UVC camera, renders preview to [previewSurface], and logs a frame-index CSV
 * (frame_idx, monotonic_ns, pts_ns) on the shared CaptureClock. Frame pixels are available in
 * the frame callback for a future MediaCodec encode step.
 */
class UvcClient(
    private val context: Context,
    private val episodeDir: File,
    private val streamId: String = "wrist0",
    private val onLog: (String) -> Unit = {},
) {
    private var helper: ICameraHelper? = null
    private var index: BufferedWriter? = null
    private var frameCount = 0
    var width = 0; private set
    var height = 0; private set

    fun start(previewSurface: Surface) {
        index = BufferedWriter(FileWriter(File(episodeDir, "${streamId}_frames.csv"))).apply {
            write("frame_idx,monotonic_ns,pts_ns"); newLine()
        }
        val h = CameraHelper()
        h.setStateCallback(object : ICameraHelper.StateCallback {
            override fun onAttach(device: UsbDevice) { h.selectDevice(device) }
            override fun onDeviceOpen(device: UsbDevice, isFirstOpen: Boolean) { h.openCamera() }
            override fun onCameraOpen(device: UsbDevice) {
                val size: Size? = h.previewSize
                width = size?.width ?: 0; height = size?.height ?: 0
                h.addSurface(previewSurface, false)
                h.startPreview()
                // h.setFrameCallback({ frame -> /* ByteBuffer pixels for future encode */ }, UVCCamera.PIXEL_FORMAT_NV21)
                onLog("UVC open ${width}x${height}")
            }
            override fun onCameraClose(device: UsbDevice) {}
            override fun onDeviceClose(device: UsbDevice) {}
            override fun onDetach(device: UsbDevice) {}
            override fun onCancel(device: UsbDevice) {}
        })
        helper = h
    }

    /** Call from your preview frame callback to timestamp each displayed frame. */
    fun onFrameDisplayed() {
        val ns = CaptureClock.nowNs()
        index?.let { it.write("$frameCount,$ns,$ns"); it.newLine() }
        frameCount++
    }

    fun stop() {
        try { helper?.stopPreview(); helper?.closeCamera() } catch (_: Exception) {}
        helper = null
        index?.flush(); index?.close(); index = null
        onLog("UVC stopped, frames=$frameCount")
    }
}

// --- Wiring sketch for MainActivity ---
// Add a SurfaceView to the UI, then in startCapture():
//     val uvc = UvcClient(this, w.dir, onLog = { runOnUiThread { log(it) } })
//     uvc.start(surfaceView.holder.surface)
// and in stopCapture(), before finalize:
//     uvc.stop()
//     if (uvc.width > 0) w.setVideo("wrist0", "wrist0.mp4", "wrist0_frames.csv",
//                                   uvc.width, uvc.height, /*frames*/ 0)
// (setVideo currently expects an mp4 — only call it once the encode step exists.)
