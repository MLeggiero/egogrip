package org.egogrip.capture

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent

/**
 * Probes what the EXISTING (2D, non-XR) app can actually see from the PICO controllers, and
 * reports the verdict honestly — no faking a transform we can't get.
 *
 * What this CAN see, in-app, with zero extra dependencies:
 *  - InputDevice enumeration: which controllers are connected and what motion axes they expose.
 *    On PICO 4 Ultra these are GAMEPAD/JOYSTICK virtual devices carrying BUTTONS + a generic
 *    axis only — NOT 6-DoF pose.
 *  - Live button/axis values (so you watch the available data update as you press/move).
 *  - The headset orientation matrix (3x3) from the rotation-vector sensor — the ONLY real
 *    transform a non-XR app can compute (head orientation; NO position, NO controller pose).
 *
 * What it canNOT see (and why): the controllers' 6-DoF transform matrices live behind PICO's
 * XR system services (pxrcontrollerservice / pvrtracking), exposed only through the PICO
 * Integration SDK inside an XR session — i.e. the Unity path. This class documents that
 * boundary empirically rather than pretending.
 */
class ControllerProbe(context: Context) {

    private val sensorManager = context.getSystemService(Context.SENSOR_SERVICE) as SensorManager
    private val rotationSensor: Sensor? = sensorManager.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR)
    private val rotationMatrix = FloatArray(9)
    @Volatile private var haveHeadPose = false

    private val sensorListener = object : SensorEventListener {
        override fun onSensorChanged(e: SensorEvent) {
            SensorManager.getRotationMatrixFromVector(rotationMatrix, e.values)
            haveHeadPose = true
        }
        override fun onAccuracyChanged(s: Sensor?, a: Int) {}
    }

    fun start() {
        rotationSensor?.let {
            sensorManager.registerListener(sensorListener, it, SensorManager.SENSOR_DELAY_GAME)
        }
    }

    fun stop() = sensorManager.unregisterListener(sensorListener)

    /** Enumerate controllers + their axes — the "what can we actually see" snapshot. */
    fun enumerate(): String = buildString {
        var found = 0
        for (id in InputDevice.getDeviceIds()) {
            val dev = InputDevice.getDevice(id) ?: continue
            val src = dev.sources
            val isController = (src and InputDevice.SOURCE_GAMEPAD == InputDevice.SOURCE_GAMEPAD) ||
                (src and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK)
            if (!isController) continue
            found++
            append("• ${dev.name}  (id=$id, controllerNumber=${dev.controllerNumber})\n")
            val axes = dev.motionRanges.joinToString(", ") { MotionEvent.axisToString(it.axis) }
            append("    axes: ${axes.ifEmpty { "(none — buttons only)" }}\n")
        }
        if (found == 0) append("(no GAMEPAD/JOYSTICK controllers enumerated)\n")
        append("\nVERDICT: InputDevice exposes buttons + sticks only — NO 6-DoF pose.\n")
        append("Controller transforms live behind PICO XR services (pxrcontrollerservice/\n")
        append("pvrtracking) → reachable only via the PICO Integration SDK in an XR session (Unity).\n")
    }

    /** Head orientation matrix (3x3, row-major, device→world). The only transform we can show. */
    fun headOrientationMatrix(): String {
        if (!haveHeadPose) {
            return if (rotationSensor == null) "head orientation: no rotation-vector sensor"
            else "head orientation: (waiting for rotation-vector sensor…)"
        }
        val m = rotationMatrix
        return buildString {
            append("head orientation R (3x3, device→world; orientation only, NO position):\n")
            append(String.format("  [% .3f % .3f % .3f]\n", m[0], m[1], m[2]))
            append(String.format("  [% .3f % .3f % .3f]\n", m[3], m[4], m[5]))
            append(String.format("  [% .3f % .3f % .3f]\n", m[6], m[7], m[8]))
        }
    }

    companion object {
        /** Decode a controller MotionEvent into a readable live axis dump. */
        fun describeMotion(e: MotionEvent): String {
            val dev = e.device ?: return "motion from unknown device"
            val parts = dev.motionRanges.map {
                "${MotionEvent.axisToString(it.axis)}=${String.format("%.2f", e.getAxisValue(it.axis))}"
            }
            return "axes[${dev.name}]: ${parts.joinToString(" ").ifEmpty { "(none)" }}"
        }

        fun describeKey(e: KeyEvent): String =
            "key ${KeyEvent.keyCodeToString(e.keyCode)} " +
                "${if (e.action == KeyEvent.ACTION_DOWN) "DOWN" else "UP"} (dev=${e.deviceId})"
    }
}
