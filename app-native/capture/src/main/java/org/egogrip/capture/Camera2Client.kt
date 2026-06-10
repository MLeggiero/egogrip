package org.egogrip.capture

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.TotalCaptureResult
import android.media.MediaRecorder
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import java.util.concurrent.Executor
import android.util.Size
import android.view.Surface
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter

/**
 * Captures a USB / external UVC camera to an mp4 using ONLY the Android Camera2 framework — no
 * third-party dependency, so the app always builds. Works when the PICO exposes the UVC camera
 * as a Camera2 device (LENS_FACING_EXTERNAL), which is the common case on Android 12+ with UVC
 * kernel support. If no such camera exists at runtime, [start] returns false and the app keeps
 * recording serial (graceful degradation). The herohan-lib path in app-native/optional/ is the
 * fallback for devices that DON'T surface UVC through Camera2.
 *
 * Writes wrist0.mp4 + wrist0_frames.csv (frame_idx, monotonic_ns, pts_ns) on the shared clock.
 */
class Camera2Client(
    private val context: Context,
    private val episodeDir: File,
    private val streamId: String = "wrist0",
    private val fps: Int = 30,
    private val onLog: (String) -> Unit = {},
) {
    private val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var recorder: MediaRecorder? = null
    private var index: BufferedWriter? = null

    var width = 0; private set
    var height = 0; private set
    @Volatile var frameCount = 0; private set

    private fun pickCameraId(): String? {
        // egogrip's wrist camera is always an external USB/UVC cam — never grab an internal
        // (e.g. a PICO tracking) camera. Returns null if no external camera is present, so the
        // caller cleanly records pose-only.
        for (id in manager.cameraIdList) {
            val facing = manager.getCameraCharacteristics(id)
                .get(CameraCharacteristics.LENS_FACING)
            if (facing == CameraCharacteristics.LENS_FACING_EXTERNAL) return id
        }
        return null
    }

    private fun pickSize(id: String): Size {
        val map = manager.getCameraCharacteristics(id)
            .get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
        val sizes = map?.getOutputSizes(MediaRecorder::class.java)
        // choose the largest size <= 1280x720, else the first available, else a safe default
        val capped = sizes?.filter { it.width <= 1280 && it.height <= 720 }
            ?.maxByOrNull { it.width.toLong() * it.height }
        return capped ?: sizes?.firstOrNull() ?: Size(1280, 720)
    }

    /** Returns true if the camera opened and recording started. */
    fun start(): Boolean {
        if (context.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            onLog("Camera: CAMERA permission not granted; skipping (serial still records)")
            return false
        }
        val id = try { pickCameraId() } catch (e: Exception) { onLog("Camera: ${e.message}"); null }
        if (id == null) {
            onLog("Camera: no Camera2 camera (UVC not exposed?); see optional/UvcClient.kt")
            return false
        }
        return try {
            val size = pickSize(id)
            width = size.width; height = size.height
            index = BufferedWriter(FileWriter(File(episodeDir, "${streamId}_frames.csv"))).apply {
                write("frame_idx,monotonic_ns,pts_ns"); newLine()
            }
            recorder = buildRecorder(size)
            thread = HandlerThread("cam").also { it.start() }
            handler = Handler(thread!!.looper)
            openCamera(id)
            onLog("Camera: opening $id @ ${width}x$height")
            true
        } catch (e: Exception) {
            onLog("Camera start failed: ${e.message}; serial still records")
            cleanup()
            false
        }
    }

    private fun buildRecorder(size: Size): MediaRecorder {
        val rec = if (android.os.Build.VERSION.SDK_INT >= 31) MediaRecorder(context) else @Suppress("DEPRECATION") MediaRecorder()
        rec.setVideoSource(MediaRecorder.VideoSource.SURFACE)
        rec.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
        rec.setOutputFile(File(episodeDir, "$streamId.mp4").absolutePath)
        rec.setVideoEncoder(MediaRecorder.VideoEncoder.H264)
        rec.setVideoSize(size.width, size.height)
        rec.setVideoFrameRate(fps)
        rec.setVideoEncodingBitRate(4_000_000)
        rec.setOrientationHint(0)
        rec.prepare()
        return rec
    }

    private fun openCamera(id: String) {
        val callback = object : CameraDevice.StateCallback() {
            override fun onOpened(device: CameraDevice) {
                camera = device
                startSession(device)
            }
            override fun onDisconnected(device: CameraDevice) { onLog("Camera disconnected"); cleanup() }
            override fun onError(device: CameraDevice, error: Int) { onLog("Camera error $error"); cleanup() }
        }
        val executor = Executor { command -> (handler ?: Handler(Looper.getMainLooper())).post(command) }
        @Suppress("MissingPermission")
        manager.openCamera(id, executor, callback)
    }

    private fun startSession(device: CameraDevice) {
        try {
            val surface: Surface = recorder!!.surface
            val request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD).apply {
                addTarget(surface)
                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
            }
            @Suppress("DEPRECATION")
            device.createCaptureSession(listOf(surface), object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    session = s
                    try {
                        s.setRepeatingRequest(request.build(), object : CameraCaptureSession.CaptureCallback() {
                            override fun onCaptureStarted(
                                s: CameraCaptureSession, r: CaptureRequest, timestamp: Long, frameNumber: Long
                            ) {
                                // monotonic_ns = our shared clock; pts_ns = camera HW timestamp
                                index?.let { it.write("$frameCount,${CaptureClock.nowNs()},$timestamp"); it.newLine() }
                                frameCount++
                            }
                        }, handler)
                        recorder?.start()
                        onLog("Camera recording")
                    } catch (e: Exception) {
                        onLog("Camera record start failed: ${e.message}"); cleanup()
                    }
                }
                override fun onConfigureFailed(s: CameraCaptureSession) { onLog("Camera session failed"); cleanup() }
            }, handler)
        } catch (e: Exception) {
            onLog("Camera session error: ${e.message}"); cleanup()
        }
    }

    /** Stop recording and return the number of frames written. The caller registers the stream:
     *  the app via [EpisodeWriter.setVideo], or [EgogripCamera] via a manifest descriptor. */
    fun stop(): Int {
        try { session?.stopRepeating() } catch (_: Exception) {}
        try { recorder?.stop() } catch (e: Exception) { onLog("Camera stop: ${e.message}") }
        index?.flush(); index?.close(); index = null
        cleanup()
        onLog("Camera stopped, frames=$frameCount")
        return frameCount
    }

    private fun cleanup() {
        try { session?.close() } catch (_: Exception) {}
        try { camera?.close() } catch (_: Exception) {}
        try { recorder?.release() } catch (_: Exception) {}
        thread?.quitSafely()
        session = null; camera = null; recorder = null; thread = null; handler = null
    }
}
