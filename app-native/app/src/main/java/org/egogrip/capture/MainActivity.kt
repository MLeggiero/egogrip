package org.egogrip.capture

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.os.BatteryManager
import android.os.Bundle
import android.os.StatFs
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.Gravity
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.hoho.android.usbserial.driver.UsbSerialProber

/**
 * Native USB+sensor capture app for the PICO (tomorrow's first-light test). Lists USB devices
 * on the hub, then on Start captures serial (RP2040), the USB/external camera (Camera2), and
 * the headset IMU on one shared clock, writing a real egogrip episode on-device.
 */
class MainActivity : Activity() {

    private lateinit var deviceText: TextView
    private lateinit var statusText: TextView
    private lateinit var logText: TextView
    private lateinit var controllerText: TextView
    private lateinit var startBtn: Button
    private lateinit var stopBtn: Button

    private var controllerProbe: ControllerProbe? = null
    private val probeTicker = object : Runnable {
        override fun run() {
            val p = controllerProbe ?: return
            controllerText.text = p.enumerate() + "\n" + p.headOrientationMatrix()
            ui.postDelayed(this, 200)
        }
    }

    private var serial: SerialClient? = null
    private var camera: Camera2Client? = null
    private var imu: ImuClient? = null
    private var writer: EpisodeWriter? = null
    private var recording = false

    private val ui = Handler(Looper.getMainLooper())
    private val ticker = object : Runnable {
        override fun run() {
            if (recording) {
                statusText.text = "● REC  ${writer?.statusLine() ?: ""}"
                ui.postDelayed(this, 250)
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildUi())
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), 1)
        }
        refreshDevices()
        log("Ready. Plug the hub (RP2040 + camera), then Refresh / Start.")
    }

    private fun buildUi(): android.view.View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(40, 40, 40, 40)
        }
        fun title(t: String) = TextView(this).apply { text = t; textSize = 20f; setPadding(0, 16, 0, 8) }

        root.addView(TextView(this).apply { text = "egogrip Capture"; textSize = 28f })

        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        startBtn = Button(this).apply { text = "Start"; setOnClickListener { startCapture() } }
        stopBtn = Button(this).apply { text = "Stop"; isEnabled = false; setOnClickListener { stopCapture() } }
        val refreshBtn = Button(this).apply { text = "Refresh"; setOnClickListener { refreshDevices() } }
        val controllerBtn = Button(this).apply { text = "Controllers"; setOnClickListener { toggleControllerProbe() } }
        row.addView(startBtn); row.addView(stopBtn); row.addView(refreshBtn); row.addView(controllerBtn)
        root.addView(row)

        statusText = TextView(this).apply { text = "idle"; textSize = 18f; setPadding(0, 16, 0, 16) }
        root.addView(statusText)

        root.addView(title("USB devices on the hub"))
        deviceText = TextView(this).apply { textSize = 14f }
        root.addView(deviceText)

        root.addView(title("Controllers (probe — what a non-XR app can see)"))
        controllerText = TextView(this).apply { textSize = 13f; text = "(tap Controllers to probe)" }
        root.addView(controllerText)

        root.addView(title("Log"))
        logText = TextView(this).apply { textSize = 13f; gravity = Gravity.TOP }
        val scroll = ScrollView(this).apply { addView(logText) }
        root.addView(scroll, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT, 1f))
        return root
    }

    private fun refreshDevices() {
        val usb = getSystemService(USB_SERVICE) as UsbManager
        val serialDeviceIds = UsbSerialProber.getDefaultProber().findAllDrivers(usb)
            .map { it.device.deviceId }.toSet()
        val sb = StringBuilder()
        if (usb.deviceList.isEmpty()) sb.append("(none — check the hub power + cable)\n")
        for (d in usb.deviceList.values) {
            val tags = buildList {
                if (d.deviceId in serialDeviceIds) add("SERIAL")
                if (isUvc(d)) add("UVC-CAMERA")
            }.joinToString(",").ifEmpty { "—" }
            sb.append(String.format("• %s  VID:%04X PID:%04X  [%s]\n",
                d.productName ?: d.deviceName, d.vendorId, d.productId, tags))
        }
        deviceText.text = sb.toString()
    }

    private fun isUvc(d: UsbDevice): Boolean {
        for (i in 0 until d.interfaceCount) {
            if (d.getInterface(i).interfaceClass == UsbConstants.USB_CLASS_VIDEO) return true
        }
        return false
    }

    private fun toggleControllerProbe() {
        if (controllerProbe == null) {
            controllerProbe = ControllerProbe(this).also { it.start() }
            ui.post(probeTicker)
            log("Controller probe ON — point/press a controller; press buttons to see events.")
        } else {
            ui.removeCallbacks(probeTicker)
            controllerProbe?.stop(); controllerProbe = null
            controllerText.text = "(probe stopped)"
            log("Controller probe OFF")
        }
    }

    // Controller buttons arrive here (the trigger/grip/A/B/thumbstick-click map to KeyEvents).
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (controllerProbe != null &&
            (event.action == KeyEvent.ACTION_DOWN || event.action == KeyEvent.ACTION_UP)) {
            log(ControllerProbe.describeKey(event))
        }
        return super.dispatchKeyEvent(event)
    }

    // Thumbstick / trigger analog axes arrive here (SOURCE_JOYSTICK). Still NO pose axes.
    override fun onGenericMotionEvent(event: MotionEvent): Boolean {
        if (controllerProbe != null &&
            event.source and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK) {
            log(ControllerProbe.describeMotion(event))
        }
        return super.onGenericMotionEvent(event)
    }

    private fun preflight() {
        val pct = try {
            (getSystemService(BATTERY_SERVICE) as BatteryManager)
                .getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        } catch (_: Exception) { -1 }
        val base = getExternalFilesDir(null) ?: filesDir
        val freeMb = try { StatFs(base.absolutePath).availableBytes / (1024 * 1024) } catch (_: Exception) { -1L }
        log("Pre-flight: battery ${pct}%, free ${freeMb} MB")
        if (pct in 0..15) log("⚠ battery low")
        if (freeMb in 0..499) log("⚠ low storage (<500 MB)")
    }

    private fun startCapture() {
        if (recording) return
        val w = EpisodeWriter(this)
        writer = w
        preflight()

        // headset orientation (3-DoF, dependency-free) and USB/external camera (Camera2)
        imu = ImuClient(this, w.dir).also { log(if (it.start()) "IMU started" else "IMU: no rotation sensor") }
        camera = Camera2Client(this, w.dir, onLog = { s -> runOnUiThread { log(s) } }).also { it.start() }

        val protocol = Protocol(
            onState = { micros, raw, delta, trig -> w.writeState(CaptureClock.nowNs(), micros, raw, delta, trig) },
            onTactile = { micros, ch -> w.writeTactile(CaptureClock.nowNs(), micros, ch) },
            onSync = { _, id -> runOnUiThread { log("SYNC #$id") } },
            onInfo = { txt -> runOnUiThread { log("MCU: $txt") } },
            onCrcError = { /* counted silently; surface if frequent */ },
        )
        serial = SerialClient(
            context = this,
            onBytes = { data, len -> protocol.feed(data, len) },
            onStatus = { s -> runOnUiThread { log(s) } },
            onError = { e -> runOnUiThread { log("ERROR: $e") } },
        ).also { it.start() }

        recording = true
        startBtn.isEnabled = false; stopBtn.isEnabled = true
        log("Recording → ${w.episodeId}")
        ui.post(ticker)
    }

    private fun stopCapture() {
        if (!recording) return
        recording = false
        serial?.stop(); serial = null
        val w = writer
        if (w != null) {
            val cam = camera
            val frames = cam?.stop() ?: 0
            if (cam != null && frames > 0 && cam.width > 0) {
                w.setVideo("wrist0", "wrist0.mp4", "wrist0_frames.csv", cam.width, cam.height, frames)
            }
            imu?.stop(w)
        }
        camera = null; imu = null
        val dir = w?.finalizeEpisode()
        writer = null
        startBtn.isEnabled = true; stopBtn.isEnabled = false
        statusText.text = "idle"
        log("Saved: ${dir?.absolutePath}")
        log("Pull: adb pull ${dir?.absolutePath}")
    }

    private fun log(msg: String) {
        Log.i("egogrip", msg)          // also visible via `adb logcat -s egogrip` / VS Code debugger
        logText.append("• $msg\n")
    }

    override fun onDestroy() {
        super.onDestroy()
        if (recording) stopCapture()
        ui.removeCallbacks(probeTicker)
        controllerProbe?.stop(); controllerProbe = null
    }
}
