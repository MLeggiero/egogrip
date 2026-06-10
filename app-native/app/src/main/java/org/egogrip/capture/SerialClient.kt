package org.egogrip.capture

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbManager
import android.os.Build
import com.hoho.android.usbserial.driver.UsbSerialDriver
import com.hoho.android.usbserial.driver.UsbSerialPort
import com.hoho.android.usbserial.driver.UsbSerialProber
import com.hoho.android.usbserial.util.SerialInputOutputManager

/**
 * Opens the RP2040 (CDC/ACM) over USB and streams bytes to [onBytes]. Handles the runtime USB
 * permission prompt. Uses usb-serial-for-android.
 */
class SerialClient(
    private val context: Context,
    private val baud: Int = 115200,
    private val onBytes: (ByteArray, Int) -> Unit,
    private val onStatus: (String) -> Unit,
    private val onError: (String) -> Unit,
) {
    private val usb = context.getSystemService(Context.USB_SERVICE) as UsbManager
    private var port: UsbSerialPort? = null
    private var io: SerialInputOutputManager? = null
    private var receiver: BroadcastReceiver? = null

    private val action = "org.egogrip.capture.USB_PERMISSION"

    fun findDrivers(): List<UsbSerialDriver> =
        UsbSerialProber.getDefaultProber().findAllDrivers(usb)

    /** Request permission if needed, then open the first serial device and start reading. */
    fun start() {
        val driver = findDrivers().firstOrNull()
        if (driver == null) {
            onError("No USB-serial device found (is the RP2040 on the hub?)")
            return
        }
        if (usb.hasPermission(driver.device)) {
            openAndRead(driver)
        } else {
            requestPermission(driver)
        }
    }

    private fun requestPermission(driver: UsbSerialDriver) {
        val flags = if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
        val pi = PendingIntent.getBroadcast(context, 0, Intent(action).setPackage(context.packageName), flags)
        receiver = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                if (i.action != action) return
                context.unregisterReceiver(this); receiver = null
                if (usb.hasPermission(driver.device)) openAndRead(driver)
                else onError("USB permission denied")
            }
        }
        val filter = IntentFilter(action)
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }
        onStatus("Requesting USB permission…")
        usb.requestPermission(driver.device, pi)
    }

    private fun openAndRead(driver: UsbSerialDriver) {
        try {
            val connection = usb.openDevice(driver.device)
                ?: run { onError("Could not open USB device"); return }
            val p = driver.ports[0]
            p.open(connection)
            p.setParameters(baud, 8, UsbSerialPort.STOPBITS_1, UsbSerialPort.PARITY_NONE)
            p.dtr = true
            port = p
            io = SerialInputOutputManager(p, object : SerialInputOutputManager.Listener {
                override fun onNewData(data: ByteArray) = onBytes(data, data.size)
                override fun onRunError(e: Exception) = onError("Serial read error: ${e.message}")
            }).also { it.start() }
            onStatus("Serial open @ $baud (${driver.javaClass.simpleName})")
        } catch (e: Exception) {
            onError("Open failed: ${e.message}")
        }
    }

    fun stop() {
        try { io?.stop() } catch (_: Exception) {}
        try { port?.close() } catch (_: Exception) {}
        io = null; port = null
        receiver?.let { try { context.unregisterReceiver(it) } catch (_: Exception) {} }
        receiver = null
    }
}
