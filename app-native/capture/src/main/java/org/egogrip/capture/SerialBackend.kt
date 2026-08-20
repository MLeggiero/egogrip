package org.egogrip.capture

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbManager
import android.os.Build
import android.util.Log
import com.hoho.android.usbserial.driver.UsbSerialDriver
import com.hoho.android.usbserial.driver.UsbSerialPort
import com.hoho.android.usbserial.driver.UsbSerialProber
import com.hoho.android.usbserial.util.SerialInputOutputManager
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter

/**
 * One RP2040 (CDC) over USB: opens an UNCLAIMED serial device, parses the framed protocol
 * ([Protocol]), and — while recording — writes gripper_state / tactile / sync CSVs into the episode
 * dir on the shared [CaptureClock]. A static [claimed] set lets N backends bind N distinct Picos
 * (mirrors [UvcCameraBackend]'s de-dupe), so two EgogripSerial instances read two Picos.
 *
 * Each Pico may stream STATE and/or TACTILE; both are routed to this device's index-suffixed files
 * (`gripper_state<idx>.csv`, `tactile<idx>.csv`, `sync_events<idx>.csv`). Latest values are exposed
 * for the Unity HUD.
 */
class SerialBackend(private val context: Context, private val baud: Int = 115200) {

    private companion object {
        const val TAG = "egogrip"
        const val ACTION_PERM = "org.egogrip.capture.USB_SERIAL_PERMISSION"
        // deviceIds already owned by another SerialBackend instance → N Picos → N stream sets.
        val claimed: MutableSet<Int> = java.util.Collections.synchronizedSet(HashSet<Int>())
        const val COUNTS_PER_MM = 100.0 // on-device width PREVIEW only; the pipeline recomputes authoritative width
    }
    private var claimedId = -1

    private val usb = context.getSystemService(Context.USB_SERVICE) as UsbManager
    private var port: UsbSerialPort? = null
    private var ioMgr: SerialInputOutputManager? = null
    private var receiver: BroadcastReceiver? = null
    @Volatile var opened = false; private set
    @Volatile private var frames = 0

    // live values for the Unity HUD
    @Volatile var latestWidthM = 0.0; private set
    @Volatile var latestTactile: IntArray = IntArray(0); private set

    // recording state
    private var dir: File? = null
    private var idx = 0
    private var stateCsv: BufferedWriter? = null
    private var tactileCsv: BufferedWriter? = null
    private var syncCsv: BufferedWriter? = null
    private var tactileChannels = 0
    @Volatile private var recording = false
    @Volatile var stateCount = 0; private set
    @Volatile var tactileCount = 0; private set
    @Volatile private var syncCount = 0

    private val protocol = Protocol(
        onState = { micros, raw, delta, trig -> onState(micros, raw, delta, trig) },
        onTactile = { micros, ch -> onTactile(micros, ch) },
        onSync = { micros, id -> onSync(micros, id) },
        onInfo = { txt -> Log.i(TAG, "SERIAL INFO: $txt") },
    )

    /** Open the first unclaimed serial device (async permission). Returns true if one was claimed. */
    fun openPreview(): Boolean {
        val driver = UsbSerialProber.getDefaultProber().findAllDrivers(usb)
            .firstOrNull { it.device.deviceId !in claimed }
        if (driver == null) { Log.w(TAG, "SERIAL: no unclaimed CDC device on the hub"); return false }
        claimedId = driver.device.deviceId; claimed.add(claimedId)
        if (usb.hasPermission(driver.device)) openAndRead(driver) else requestPermission(driver)
        return true
    }

    private fun requestPermission(driver: UsbSerialDriver) {
        val flags = if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
        val pi = PendingIntent.getBroadcast(context, 0, Intent(ACTION_PERM).setPackage(context.packageName), flags)
        receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                if (i.action != ACTION_PERM) return
                try { context.unregisterReceiver(this) } catch (_: Exception) {}
                receiver = null
                if (usb.hasPermission(driver.device)) openAndRead(driver)
                else Log.w(TAG, "SERIAL: USB permission denied")
            }
        }
        val filter = IntentFilter(ACTION_PERM)
        if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        else @Suppress("UnspecifiedRegisterReceiverFlag") context.registerReceiver(receiver, filter)
        usb.requestPermission(driver.device, pi)
    }

    private fun openAndRead(driver: UsbSerialDriver) {
        try {
            val connection = usb.openDevice(driver.device) ?: run { Log.e(TAG, "SERIAL: openDevice failed"); return }
            val p = driver.ports[0]
            p.open(connection)
            p.setParameters(baud, 8, UsbSerialPort.STOPBITS_1, UsbSerialPort.PARITY_NONE)
            p.dtr = true
            port = p
            ioMgr = SerialInputOutputManager(p, object : SerialInputOutputManager.Listener {
                override fun onNewData(data: ByteArray) { frames++; protocol.feed(data, data.size) }
                override fun onRunError(e: Exception) { Log.w(TAG, "SERIAL read error: ${e.message}") }
            }).also { it.start() }
            opened = true
            Log.i(TAG, "SERIAL open @ $baud (${driver.javaClass.simpleName})")
        } catch (e: Exception) { Log.e(TAG, "SERIAL open failed: ${e.message}") }
    }

    /** Arm recording into episodeDir; index disambiguates this Pico's streams. */
    fun beginRecording(episodeDir: String, index: Int): Boolean {
        val d = File(episodeDir).apply { mkdirs() }
        dir = d; idx = index
        stateCount = 0; tactileCount = 0; syncCount = 0; tactileChannels = 0
        stateCsv = open(d, "gripper_state$index.csv",
            "monotonic_ns,mcu_micros,raw_counts,delta_counts,width_preview_m,trigger")
        tactileCsv = null // created lazily once channel count is known
        syncCsv = open(d, "sync_events$index.csv", "monotonic_ns,kind,id")
        recording = true
        return true
    }

    private fun onState(micros: Long, raw: Int, delta: Int, trig: Int) {
        val ns = CaptureClock.nowNs()
        latestWidthM = delta / COUNTS_PER_MM / 1000.0
        if (recording) {
            stateCsv?.let { it.write("$ns,$micros,$raw,$delta,$latestWidthM,$trig"); it.newLine() }
            if (++stateCount % 50 == 0) stateCsv?.flush()
        }
    }

    private fun onTactile(micros: Long, ch: IntArray) {
        val ns = CaptureClock.nowNs()
        latestTactile = ch
        if (recording) {
            if (tactileCsv == null) {
                tactileChannels = ch.size
                val header = buildString { append("monotonic_ns,mcu_micros"); for (c in ch.indices) append(",ch$c") }
                tactileCsv = open(dir!!, "tactile$idx.csv", header)
            }
            val sb = StringBuilder().append(ns).append(',').append(micros)
            for (v in ch) sb.append(',').append(v)
            tactileCsv?.let { it.write(sb.toString()); it.newLine() }
            if (++tactileCount % 50 == 0) tactileCsv?.flush()
        }
    }

    private fun onSync(micros: Long, id: Long) {
        val ns = CaptureClock.nowNs()
        if (recording) { syncCsv?.let { it.write("$ns,LED_PULSE,$id"); it.newLine(); it.flush() }; syncCount++ }
    }

    /** Stop recording; returns manifest stream descriptor object(s), comma-joined (may be empty). */
    fun stopRecording(): String {
        recording = false
        stateCsv?.flush(); stateCsv?.close()
        tactileCsv?.flush(); tactileCsv?.close()
        syncCsv?.flush(); syncCsv?.close()
        val out = ArrayList<String>()
        if (stateCount > 0)
            out.add("""{"id":"gripper_state$idx","kind":"gripper_state","file":"gripper_state$idx.csv",""" +
                    """"timestamp_field":"monotonic_ns","sample_count":$stateCount,"units":"m","plugin":"rp2040.encoder"}""")
        if (tactileCount > 0) {
            val chans = (0 until tactileChannels).joinToString(",") { """{"name":"ch$it","unit":"raw","location":"pad_$it"}""" }
            out.add("""{"id":"tactile$idx","kind":"tactile","file":"tactile$idx.csv",""" +
                    """"timestamp_field":"monotonic_ns","sample_count":$tactileCount,"plugin":"rp2040.tactile","channels":[$chans]}""")
        }
        if (syncCount > 0)
            out.add("""{"id":"sync$idx","kind":"sync_events","file":"sync_events$idx.csv",""" +
                    """"timestamp_field":"monotonic_ns","sample_count":$syncCount}""")
        stateCsv = null; tactileCsv = null; syncCsv = null
        return out.joinToString(",")
    }

    fun isAlive(): Boolean = opened && frames > 0
    fun latestWidthPreview(): Double = latestWidthM
    fun latestTactile(): IntArray = latestTactile

    fun close() {
        if (recording) stopRecording()
        try { ioMgr?.stop() } catch (_: Exception) {}
        try { port?.close() } catch (_: Exception) {}
        ioMgr = null; port = null
        receiver?.let { try { context.unregisterReceiver(it) } catch (_: Exception) {} }
        receiver = null
        if (claimedId != -1) { claimed.remove(claimedId); claimedId = -1 }
        opened = false
    }

    private fun open(dir: File, name: String, header: String): BufferedWriter {
        val w = BufferedWriter(FileWriter(File(dir, name)))
        w.write(header); w.newLine()
        return w
    }
}
