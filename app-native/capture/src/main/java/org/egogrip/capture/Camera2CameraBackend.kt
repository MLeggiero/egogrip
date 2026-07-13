package org.egogrip.capture

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.ImageReader
import android.media.MediaRecorder
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Size
import android.view.Surface
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter

/**
 * Camera2-based external (USB/UVC) camera backend — the path EgoKit uses on PICO 4 Ultra and
 * Quest 3, where the headset surfaces the wrist camera as a Camera2 LENS_FACING_EXTERNAL device
 * (gated by horizonos.permission.USB_CAMERA, declared in the manifest). This replaces the libuvc
 * backend ([UvcCameraBackend], kept as a fallback) for devices that DON'T expose UVC over libusb.
 *
 * Auto-detect: [openPreview] registers a CameraManager.AvailabilityCallback and opens the external
 * camera as soon as it appears — so plugging the USB camera in AFTER app launch still works (and
 * unplug/replug re-opens). Continuous preview (a small ImageReader YUV→RGBA feed for the Unity
 * texture) runs while open; [beginRecording]/[stopRecording] add/remove an mp4 (MediaRecorder,
 * HARDWARE H.264 — no per-frame CPU) without dropping preview. The mp4 keeps the full record
 * resolution; the preview is capped small so it doesn't load the PICO. Frame timestamps go to
 * <streamId>_frames.csv on the shared [CaptureClock]. Same facade API as [UvcCameraBackend].
 */
class Camera2CameraBackend(private val context: Context) {

    companion object {
        private const val TAG = "egogrip"
        private const val PREVIEW_MAX = 640   // max preview width (cheap YUV→RGBA); mp4 is full-res
        private const val RECORD_MAX = 1280
    }

    private val manager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var availCb: CameraManager.AvailabilityCallback? = null
    private var camera: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null
    private var recorder: MediaRecorder? = null
    @Volatile private var opening = false

    private var index: BufferedWriter? = null
    private var recDir: File? = null
    private var recStreamId = "wrist0"
    @Volatile private var recording = false
    @Volatile var recFrames = 0; private set

    // preview dims (Unity texture) vs record dims (mp4 / manifest) — decoupled
    @Volatile var width = 0; private set       // preview width
    @Volatile var height = 0; private set      // preview height
    private var recordSize = Size(RECORD_MAX, 720)
    @Volatile var frameCount = 0; private set
    @Volatile var opened = false; private set

    private val lock = Any()
    private var latest: ByteArray? = null

    private fun isExternal(id: String): Boolean = try {
        val ch = manager.getCameraCharacteristics(id)
        ch.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL) ==
            CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL_EXTERNAL ||
        ch.get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_EXTERNAL
    } catch (_: Exception) { false }

    private fun pickCameraId(): String? {
        val ids = manager.cameraIdList
        Log.i(TAG, "Camera2: ids = ${ids.joinToString()}")
        var byFacing: String? = null
        for (id in ids) {
            val ch = manager.getCameraCharacteristics(id)
            val facing = ch.get(CameraCharacteristics.LENS_FACING)
            val level = ch.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL)
            Log.i(TAG, "  cam $id facing=$facing hwLevel=$level")
            // a true USB cam reports EXTERNAL hardware level; prefer it over a merely
            // LENS_FACING_EXTERNAL internal cam (some PICO cams report that).
            if (level == CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL_EXTERNAL) return id
            if (facing == CameraCharacteristics.LENS_FACING_EXTERNAL && byFacing == null) byFacing = id
        }
        return byFacing
    }

    private fun outputSizes(id: String) =
        manager.getCameraCharacteristics(id)
            .get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
            ?.getOutputSizes(ImageFormat.YUV_420_888)

    private fun largestUnder(sizes: Array<Size>?, maxW: Int, fallback: Size): Size =
        sizes?.filter { it.width <= maxW }?.maxByOrNull { it.width.toLong() * it.height }
            ?: sizes?.firstOrNull() ?: fallback

    /** Start listening for the external camera; opens it now if present, else on hotplug. */
    fun openPreview(): Boolean {
        if (thread != null) return true
        if (context.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            Log.w(TAG, "Camera2: CAMERA permission not granted")
            return false
        }
        thread = HandlerThread("egogrip-cam").also { it.start() }
        handler = Handler(thread!!.looper)
        availCb = object : CameraManager.AvailabilityCallback() {
            override fun onCameraAvailable(id: String) {
                if (camera == null && !opening && isExternal(id)) { Log.i(TAG, "Camera2: external $id available"); tryOpen() }
            }
        }
        manager.registerAvailabilityCallback(availCb!!, handler)
        Log.i(TAG, "Camera2: listening for external camera")
        tryOpen()   // open immediately if already plugged in
        return true
    }

    private fun tryOpen() {
        if (camera != null || opening) return
        val id = try { pickCameraId() } catch (e: Exception) { Log.e(TAG, "Camera2: pick failed", e); null }
        if (id == null) { Log.w(TAG, "Camera2: no external camera yet (waiting for USB attach)"); return }
        try {
            val sizes = outputSizes(id)
            val preview = largestUnder(sizes, PREVIEW_MAX, Size(640, 480))
            recordSize = largestUnder(sizes, RECORD_MAX, Size(1280, 720))
            width = preview.width; height = preview.height
            reader = ImageReader.newInstance(preview.width, preview.height, ImageFormat.YUV_420_888, 3).apply {
                setOnImageAvailableListener({ r ->
                    val img = r.acquireLatestImage() ?: return@setOnImageAvailableListener
                    try {
                        val rgba = yuvToRgba(img, preview.width, preview.height)
                        synchronized(lock) { latest = rgba }
                        frameCount++
                    } catch (_: Exception) {} finally { img.close() }
                }, handler)
            }
            opening = true
            Log.i(TAG, "Camera2: opening external cam $id preview=${preview.width}x${preview.height} record=${recordSize.width}x${recordSize.height}")
            @Suppress("MissingPermission")
            manager.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) { Log.i(TAG, "Camera2: onOpened $id"); opening = false; camera = device; buildSession() }
                override fun onDisconnected(device: CameraDevice) { Log.w(TAG, "Camera2: onDisconnected $id"); opening = false; closeDevice() }
                override fun onError(device: CameraDevice, error: Int) { Log.e(TAG, "Camera2: onError $id code=$error"); opening = false; closeDevice() }
            }, handler)
        } catch (e: Exception) { Log.e(TAG, "Camera2: open failed", e); opening = false; closeDevice() }
    }

    /** (Re)build the capture session with the preview reader, plus the recorder surface if recording. */
    private fun buildSession() {
        val device = camera ?: return
        try { session?.close() } catch (_: Exception) {}
        session = null
        val surfaces = ArrayList<Surface>()
        reader?.surface?.let { surfaces.add(it) }
        if (recording) recorder?.surface?.let { surfaces.add(it) }
        val template = if (recording) CameraDevice.TEMPLATE_RECORD else CameraDevice.TEMPLATE_PREVIEW
        val request = device.createCaptureRequest(template).apply { surfaces.forEach { addTarget(it) } }
        @Suppress("DEPRECATION")
        device.createCaptureSession(surfaces, object : CameraCaptureSession.StateCallback() {
            override fun onConfigured(s: CameraCaptureSession) {
                session = s
                opened = true
                Log.i(TAG, "Camera2: session configured (recording=$recording)")
                try {
                    s.setRepeatingRequest(request.build(), object : CameraCaptureSession.CaptureCallback() {
                        override fun onCaptureStarted(
                            s: CameraCaptureSession, r: CaptureRequest, timestamp: Long, frameNumber: Long
                        ) {
                            if (recording) {
                                index?.let { it.write("$recFrames,${CaptureClock.nowNs()},$timestamp"); it.newLine() }
                                recFrames++
                            }
                        }
                    }, handler)
                    if (recording) recorder?.start()
                } catch (e: Exception) { Log.e(TAG, "Camera2: setRepeatingRequest failed", e) }
            }
            override fun onConfigureFailed(s: CameraCaptureSession) { Log.e(TAG, "Camera2: session configure FAILED") }
        }, handler)
    }

    fun beginRecording(episodeDirPath: String, streamId: String): Boolean {
        if (camera == null) return false
        val dir = File(episodeDirPath).apply { mkdirs() }
        recDir = dir; recStreamId = streamId; recFrames = 0
        index = BufferedWriter(FileWriter(File(dir, "${streamId}_frames.csv"))).apply {
            write("frame_idx,monotonic_ns,pts_ns"); newLine()
        }
        return try {
            recorder = buildRecorder(dir, streamId, recordSize)
            recording = true
            buildSession()   // recreate the session to include the recorder surface
            true
        } catch (e: Exception) {
            Log.e(TAG, "Camera2: beginRecording failed", e)
            index?.flush(); index?.close(); index = null
            try { recorder?.release() } catch (_: Exception) {}
            recorder = null; recording = false
            false
        }
    }

    private fun buildRecorder(dir: File, streamId: String, size: Size): MediaRecorder {
        val rec = if (android.os.Build.VERSION.SDK_INT >= 31) MediaRecorder(context) else @Suppress("DEPRECATION") MediaRecorder()
        rec.setVideoSource(MediaRecorder.VideoSource.SURFACE)
        rec.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
        rec.setOutputFile(File(dir, "$streamId.mp4").absolutePath)
        rec.setVideoEncoder(MediaRecorder.VideoEncoder.H264)   // hardware encoder — no CPU per frame
        rec.setVideoSize(size.width, size.height)
        rec.setVideoFrameRate(30)
        rec.setVideoEncodingBitRate(4_000_000)
        rec.setOrientationHint(0)
        rec.prepare()
        return rec
    }

    fun stopRecording(): String {
        if (!recording && index == null) return ""
        recording = false
        try { session?.stopRepeating() } catch (_: Exception) {}
        try { recorder?.stop() } catch (_: Exception) {}
        try { recorder?.release() } catch (_: Exception) {}
        recorder = null
        index?.flush(); index?.close(); index = null
        val frames = recFrames
        if (camera != null) buildSession()   // back to preview-only
        if (frames <= 0 || recordSize.width <= 0) return ""
        return """{"id":"$recStreamId","kind":"video_rgb","file":"$recStreamId.mp4",""" +
            """"index_file":"${recStreamId}_frames.csv","timestamp_field":"monotonic_ns",""" +
            """"sample_count":$frames,"codec":"h264",""" +
            """"frame_size":{"width":${recordSize.width},"height":${recordSize.height}}}"""
    }

    fun latestFrame(): ByteArray? = synchronized(lock) { latest }
    fun isAlive(): Boolean = opened && frameCount > 0

    /** Close the camera/session/reader but keep listening for a re-plug. */
    private fun closeDevice() {
        if (recording || index != null) stopRecording()
        try { session?.close() } catch (_: Exception) {}
        try { camera?.close() } catch (_: Exception) {}
        try { reader?.close() } catch (_: Exception) {}
        session = null; camera = null; reader = null
        opened = false; opening = false; width = 0; height = 0
    }

    /** Full teardown: stop listening and quit the worker thread. */
    fun close() {
        availCb?.let { try { manager.unregisterAvailabilityCallback(it) } catch (_: Exception) {} }
        availCb = null
        closeDevice()
        thread?.quitSafely()
        thread = null; handler = null
    }

    /** YUV_420_888 → RGBA8888 (BT.601). Handles row/pixel strides; used for the small Unity preview. */
    private fun yuvToRgba(img: android.media.Image, w: Int, h: Int): ByteArray {
        val yP = img.planes[0]; val uP = img.planes[1]; val vP = img.planes[2]
        val yBuf = yP.buffer; val uBuf = uP.buffer; val vBuf = vP.buffer
        val yRow = yP.rowStride; val uRow = uP.rowStride; val vRow = vP.rowStride
        val uPix = uP.pixelStride; val vPix = vP.pixelStride
        val out = ByteArray(w * h * 4)
        var o = 0
        for (j in 0 until h) {
            val yLine = j * yRow
            val cLine = (j shr 1)
            for (i in 0 until w) {
                val y = (yBuf.get(yLine + i).toInt() and 0xFF)
                val uvCol = (i shr 1)
                val u = (uBuf.get(cLine * uRow + uvCol * uPix).toInt() and 0xFF) - 128
                val v = (vBuf.get(cLine * vRow + uvCol * vPix).toInt() and 0xFF) - 128
                var r = y + ((1436 * v) shr 10)
                var g = y - ((352 * u + 731 * v) shr 10)
                var b = y + ((1815 * u) shr 10)
                if (r < 0) r = 0 else if (r > 255) r = 255
                if (g < 0) g = 0 else if (g > 255) g = 255
                if (b < 0) b = 0 else if (b > 255) b = 255
                out[o] = r.toByte(); out[o + 1] = g.toByte(); out[o + 2] = b.toByte(); out[o + 3] = 255.toByte()
                o += 4
            }
        }
        return out
    }
}
