package org.egogrip.capture

import android.content.Context
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter

/**
 * Logs the headset's 3-DoF orientation (game rotation vector → quaternion) on the shared clock.
 * Framework-only, no dependency. This is NOT full 6-DoF gripper pose (that needs the XR/Unity
 * phase) — it's free, useful head-motion data and proves an orientation stream end to end.
 *
 * Writes imu.csv: monotonic_ns, sensor_ns, qx, qy, qz, qw  (canonical xyzw, OpenXR-ish frame).
 */
class ImuClient(
    private val context: Context,
    private val episodeDir: File,
    private val streamId: String = "imu",
) : SensorEventListener {

    private val sm = context.getSystemService(Context.SENSOR_SERVICE) as SensorManager
    private var sensor: Sensor? = null
    private var out: BufferedWriter? = null
    @Volatile var count = 0; private set
    private val q = FloatArray(4)

    /** Returns true if a rotation-vector sensor was available and listening started. */
    fun start(): Boolean {
        sensor = sm.getDefaultSensor(Sensor.TYPE_GAME_ROTATION_VECTOR)
            ?: sm.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR)
        val s = sensor ?: return false
        out = BufferedWriter(FileWriter(File(episodeDir, "$streamId.csv"))).apply {
            write("monotonic_ns,sensor_ns,qx,qy,qz,qw"); newLine()
        }
        return sm.registerListener(this, s, SensorManager.SENSOR_DELAY_GAME)
    }

    override fun onSensorChanged(e: SensorEvent) {
        // getQuaternionFromVector returns [w, x, y, z]
        SensorManager.getQuaternionFromVector(q, e.values)
        val w = q[0]; val x = q[1]; val y = q[2]; val z = q[3]
        out?.let {
            it.write("${CaptureClock.nowNs()},${e.timestamp},$x,$y,$z,$w"); it.newLine()
            count++
            if (count % 50 == 0) it.flush()
        }
    }

    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {}

    /** Stop and register the stream with the writer. */
    fun stop(writer: EpisodeWriter) {
        sm.unregisterListener(this)
        out?.flush(); out?.close(); out = null
        if (count > 0) writer.setImu(streamId, "$streamId.csv", count)
    }
}
