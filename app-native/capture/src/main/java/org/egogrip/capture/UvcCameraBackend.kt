package org.egogrip.capture

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.os.Build
import com.herohan.uvcapp.CameraHelper
import com.herohan.uvcapp.ICameraHelper
import com.herohan.uvcapp.VideoCapture
import com.serenegiant.usb.IFrameCallback
import com.serenegiant.usb.UVCCamera
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.nio.ByteBuffer

/**
 * libuvc-based external UVC camera backend (herohan/UVCAndroid). Opens external USB cameras that
 * Android Camera2 can't see — generic USB webcams, and (to test) the RealSense D405's color
 * stream.
 *
 * Lifecycle is split so the camera behaves like EgoKit: [openPreview] starts a *continuous* preview
 * the moment a camera is attached (independent of recording), and [beginRecording]/[stopRecording]
 * fold an episode's mp4 in and out without tearing the camera down. RGBA frames from the single
 * frame callback feed BOTH:
 *   - the live Unity preview ([latestFrame] → Texture2D), and
 *   - per-frame timestamps on the shared [CaptureClock] (<streamId>_frames.csv) while recording,
 * while herohan records the color stream to <streamId>.mp4.
 *
 * herohan's USBMonitor (inside CameraHelper) handles USB attach + the permission prompt; opening an
 * already-attached device works because register() replays onAttach for connected devices.
 * Note: RealSense cameras may open but not stream over plain libuvc without RealSense init — if
 * no frames arrive for the D405, that's the signal to use the librealsense backend (Phase 2).
 */
class UvcCameraBackend(private val context: Context) {

    private companion object {
        const val TAG = "egogrip"
        const val ACTION_PERM = "org.egogrip.capture.USB_PERMISSION"
        // device ids already claimed by another backend instance, so N cameras → N streams without
        // two backends grabbing the same physical camera. (Multiple EgogripWristCamera components.)
        val claimed: MutableSet<Int> = java.util.Collections.synchronizedSet(HashSet<Int>())
    }
    private var claimedId = -1

    private var helper: ICameraHelper? = null
    private val uiHandler = Handler(Looper.getMainLooper())
    private val usbManager by lazy { context.getSystemService(Context.USB_SERVICE) as UsbManager }
    private var permReceiver: BroadcastReceiver? = null
    @Volatile private var loggedFirstFrame = false
    @Volatile private var selected = false

    // recording state (an episode); preview can run with none of this set
    private var index: BufferedWriter? = null
    private var recDir: File? = null
    private var recStreamId = "wrist0"
    @Volatile private var recording = false
    @Volatile var recFrames = 0; private set

    @Volatile var width = 0; private set
    @Volatile var height = 0; private set
    @Volatile var frameCount = 0; private set   // total frames since preview opened (liveness)
    @Volatile var opened = false; private set    // camera open + preview running

    // latest RGBA frame for the Unity preview
    private val lock = Any()
    private var latest: ByteArray? = null

    /** Start continuous preview. Returns true once the helper is listening (open is async). */
    fun openPreview(): Boolean {
        if (helper != null) return true
        Log.i(TAG, "UVC: openPreview — registering USBMonitor (waiting for camera attach)")
        val h = CameraHelper()
        h.setStateCallback(object : ICameraHelper.StateCallback {
            override fun onAttach(device: UsbDevice) {
                Log.i(TAG, "UVC: onAttach ${device.deviceName} vid=${device.vendorId} pid=${device.productId}")
                if (selected || device.deviceId in claimed) return  // already handled / owned by another stream
                selected = true
                claim(device)
                requestAndSelect(device)
            }
            override fun onDeviceOpen(device: UsbDevice, isFirstOpen: Boolean) {
                Log.i(TAG, "UVC: onDeviceOpen (firstOpen=$isFirstOpen) -> openCamera")
                h.openCamera()
            }
            override fun onCameraOpen(device: UsbDevice) {
                val size = h.previewSize
                width = size?.width ?: 0
                height = size?.height ?: 0
                Log.i(TAG, "UVC: onCameraOpen size=${width}x${height} -> startPreview")
                // RGBA frames (PIXEL_FORMAT_RGBX) feed both preview and frame-timestamping.
                h.setFrameCallback(IFrameCallback { frame -> onFrame(frame) }, UVCCamera.PIXEL_FORMAT_RGBX)
                h.startPreview()
                opened = true
            }
            override fun onCameraClose(device: UsbDevice) { Log.i(TAG, "UVC: onCameraClose"); opened = false }
            override fun onDeviceClose(device: UsbDevice) {}
            override fun onDetach(device: UsbDevice) { Log.i(TAG, "UVC: onDetach"); release(); opened = false; selected = false; loggedFirstFrame = false; width = 0; height = 0 }
            override fun onCancel(device: UsbDevice) { Log.w(TAG, "UVC: onCancel — USB permission denied / open cancelled") }
        })
        helper = h
        // This herohan build only fires onAttach for a FRESH hotplug, not for a camera that was
        // already plugged in when we registered. So poll getDeviceList() and selectDevice()
        // ourselves (selectDevice → permission prompt → onDeviceOpen → onCameraOpen).
        trySelectExisting(0)
        return true
    }

    private fun isVideo(d: UsbDevice): Boolean {
        for (i in 0 until d.interfaceCount) if (d.getInterface(i).interfaceClass == 14) return true
        return d.deviceClass == 239 || d.deviceClass == 14
    }

    private fun claim(dev: UsbDevice) { claimedId = dev.deviceId; claimed.add(dev.deviceId) }
    private fun release() { if (claimedId != -1) { claimed.remove(claimedId); claimedId = -1 } }

    private fun trySelectExisting(attempt: Int) {
        val h = helper ?: return
        if (selected || opened) return
        try {
            val list = h.deviceList
            if (!list.isNullOrEmpty()) {
                // pick the first UVC device NOT already claimed by another stream's backend
                val dev = list.firstOrNull { isVideo(it) && it.deviceId !in claimed }
                    ?: list.firstOrNull { it.deviceId !in claimed }
                if (dev == null) {
                    Log.w(TAG, "UVC: ${list.size} device(s) but all already claimed by other streams")
                    return  // nothing left for this backend; a fresh hotplug will fire onAttach
                }
                Log.i(TAG, "UVC: ${list.size} device(s); selecting unclaimed ${dev.deviceName} vid=${dev.vendorId} pid=${dev.productId}")
                selected = true
                claim(dev)
                requestAndSelect(dev)
                return
            }
        } catch (e: Exception) { Log.w(TAG, "UVC: getDeviceList not ready (attempt $attempt): ${e.message}") }
        if (attempt < 10) uiHandler.postDelayed({ trySelectExisting(attempt + 1) }, 400)
        else Log.w(TAG, "UVC: no already-connected device via getDeviceList — relying on hotplug onAttach")
    }

    /**
     * Grant USB permission OURSELVES (herohan 1.0.4's internal request fails silently on Android
     * 12+ due to an immutable PendingIntent) and only then hand the device to herohan. If we
     * already hold permission, select immediately.
     */
    private fun requestAndSelect(dev: UsbDevice) {
        val h = helper ?: return
        if (usbManager.hasPermission(dev)) {
            Log.i(TAG, "UVC: already have USB permission -> selectDevice")
            h.selectDevice(dev)
            return
        }
        if (permReceiver == null) {
            permReceiver = object : BroadcastReceiver() {
                override fun onReceive(c: Context, i: Intent) {
                    if (i.action != ACTION_PERM) return
                    val granted = i.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false)
                    @Suppress("DEPRECATION")
                    val d = i.getParcelableExtra<UsbDevice>(UsbManager.EXTRA_DEVICE)
                    Log.i(TAG, "UVC: permission result granted=$granted")
                    if (granted && d != null) helper?.selectDevice(d)
                    else Log.w(TAG, "UVC: USB permission denied by user")
                }
            }
            val filter = IntentFilter(ACTION_PERM)
            if (Build.VERSION.SDK_INT >= 33)
                context.registerReceiver(permReceiver, filter, Context.RECEIVER_NOT_EXPORTED)
            else
                @Suppress("UnspecifiedRegisterReceiverFlag") context.registerReceiver(permReceiver, filter)
        }
        val flags = if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
        val pi = PendingIntent.getBroadcast(context, 0, Intent(ACTION_PERM).setPackage(context.packageName), flags)
        Log.i(TAG, "UVC: requesting USB permission for ${dev.deviceName}")
        usbManager.requestPermission(dev, pi)
    }

    /** Fold an episode's mp4 + frame index into the running preview. Returns true if armed. */
    fun beginRecording(episodeDirPath: String, streamId: String): Boolean {
        val h = helper ?: return false
        val dir = File(episodeDirPath).apply { mkdirs() }
        recDir = dir
        recStreamId = streamId
        recFrames = 0
        index = BufferedWriter(FileWriter(File(dir, "${streamId}_frames.csv"))).apply {
            write("frame_idx,monotonic_ns,pts_ns"); newLine()
        }
        return try {
            val opts = VideoCapture.OutputFileOptions.Builder(File(dir, "$streamId.mp4")).build()
            h.startRecording(opts, object : VideoCapture.OnVideoCaptureCallback {
                override fun onStart() {}
                override fun onVideoSaved(result: VideoCapture.OutputFileResults) {}
                override fun onError(code: Int, message: String, cause: Throwable?) {}
            })
            recording = true
            true
        } catch (_: Exception) {
            // preview keeps running even if recording couldn't start
            index?.flush(); index?.close(); index = null
            recording = false
            false
        }
    }

    private fun onFrame(frame: ByteBuffer) {
        val ns = CaptureClock.nowNs()
        val n = frame.remaining()
        if (!loggedFirstFrame) { loggedFirstFrame = true; Log.i(TAG, "UVC: first frame ($n bytes) ${width}x${height} — streaming") }
        if (n > 0) {
            val buf = ByteArray(n)
            frame.get(buf)
            synchronized(lock) { latest = buf }
        }
        frameCount++
        if (recording) {
            index?.let { it.write("$recFrames,$ns,$ns"); it.newLine() }
            recFrames++
        }
    }

    /** Latest RGBA frame for the Unity preview (width*height*4 bytes), or null. */
    fun latestFrame(): ByteArray? = synchronized(lock) { latest }

    /** True once a camera is open and preview is running. */
    fun isAlive(): Boolean = opened && frameCount > 0

    /** Stop the current episode's recording (keeps preview alive). Returns a manifest descriptor. */
    fun stopRecording(): String {
        if (!recording && index == null) return ""
        try { if (recording) helper?.stopRecording() } catch (_: Exception) {}
        index?.flush(); index?.close(); index = null
        recording = false
        val frames = recFrames
        if (frames <= 0 || width <= 0) return ""
        return """{"id":"$recStreamId","kind":"video_rgb","file":"$recStreamId.mp4",""" +
            """"index_file":"${recStreamId}_frames.csv","timestamp_field":"monotonic_ns",""" +
            """"sample_count":$frames,"codec":"h264",""" +
            """"frame_size":{"width":$width,"height":$height}}"""
    }

    /** Stop recording (if any), tear down preview, and release the camera. */
    fun close() {
        uiHandler.removeCallbacksAndMessages(null)
        permReceiver?.let { try { context.unregisterReceiver(it) } catch (_: Exception) {} }
        permReceiver = null
        if (recording || index != null) stopRecording()
        try { helper?.stopPreview() } catch (_: Exception) {}
        try { helper?.closeCamera() } catch (_: Exception) {}
        try { helper?.release() } catch (_: Exception) {}
        helper = null
        release()
        opened = false; selected = false; loggedFirstFrame = false
        width = 0; height = 0
    }
}
