package org.egogrip.capture

import android.app.Activity
import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.hoho.android.usbserial.driver.UsbSerialProber

/**
 * Native USB+sensor capture sanity app for the PICO (tomorrow's first-light test).
 * Lists USB devices on the hub, streams the RP2040 over serial, and writes a real egogrip
 * episode on-device. Camera (UVC) capture is an opt-in module (see app-native/README.md).
 */
class MainActivity : Activity() {

    private lateinit var deviceText: TextView
    private lateinit var statusText: TextView
    private lateinit var logText: TextView
    private lateinit var startBtn: Button
    private lateinit var stopBtn: Button

    private var serial: SerialClient? = null
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
        row.addView(startBtn); row.addView(stopBtn); row.addView(refreshBtn)
        root.addView(row)

        statusText = TextView(this).apply { text = "idle"; textSize = 18f; setPadding(0, 16, 0, 16) }
        root.addView(statusText)

        root.addView(title("USB devices on the hub"))
        deviceText = TextView(this).apply { textSize = 14f }
        root.addView(deviceText)

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

    private fun startCapture() {
        if (recording) return
        val w = EpisodeWriter(this)
        writer = w
        val protocol = Protocol(
            onState = { micros, counts, trig -> w.writeState(CaptureClock.nowNs(), micros, counts, trig) },
            onTactile = { micros, ch -> w.writeTactile(CaptureClock.nowNs(), micros, ch) },
            onSync = { _, id -> log("SYNC #$id") },
            onInfo = { txt -> log("MCU: $txt") },
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
        val dir = writer?.finalizeEpisode()
        writer = null
        startBtn.isEnabled = true; stopBtn.isEnabled = false
        statusText.text = "idle"
        log("Saved: ${dir?.absolutePath}")
        log("Pull: adb pull ${dir?.absolutePath}")
    }

    private fun log(msg: String) {
        logText.append("• $msg\n")
    }

    override fun onDestroy() {
        super.onDestroy()
        if (recording) stopCapture()
    }
}
