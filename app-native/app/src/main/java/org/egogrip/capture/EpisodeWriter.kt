package org.egogrip.capture

import android.content.Context
import android.os.Build
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Writes one episode in the egogrip raw format (docs/DATA_FORMAT.md + schema/). Append-only
 * CSVs flushed periodically so a crash loses at most the last buffer; manifest written on stop.
 *
 * Tomorrow's native episodes contain serial streams (and optionally a wrist camera). They do
 * NOT yet contain a gripper pose stream (that needs the controller/Unity phase), so they are
 * for validating CAPTURE; full LeRobot export requires pose.
 */
class EpisodeWriter(context: Context) {

    // Placeholder width calibration: counts -> meters. Replace after the gripper is calibrated.
    private val widthCountsFull = 4096.0
    private val widthMetersFull = 0.08

    val episodeId: String = SimpleDateFormat("yyyy-MM-dd'T'HH-mm-ss", Locale.US).format(Date()) + "_native"
    val dir: File = File(File(context.getExternalFilesDir(null), "episodes"), episodeId).apply { mkdirs() }

    private val startNs = CaptureClock.nowNs()
    private var stopNs = startNs

    private val stateCsv = open("gripper_state.csv", "monotonic_ns,mcu_micros,width_m,raw_counts,trigger")
    private val tactileWriterHeaderWritten = booleanArrayOf(false)
    private var tactileCsv: BufferedWriter? = null
    private var tactileChannels = 0

    private var stateCount = 0
    private var tactileCount = 0

    // optional camera stream (filled in by the camera module when enabled)
    private var videoStreamId: String? = null
    private var videoFile: String? = null
    private var videoIndexFile: String? = null
    private var videoCount = 0
    private var videoW = 0
    private var videoH = 0

    // optional headset IMU/orientation stream
    private var imuStreamId: String? = null
    private var imuFile: String? = null
    private var imuCount = 0

    private fun open(name: String, header: String): BufferedWriter {
        val w = BufferedWriter(FileWriter(File(dir, name)))
        w.write(header); w.newLine()
        return w
    }

    @Synchronized
    fun writeState(arrivalNs: Long, mcuMicros: Long, counts: Int, trigger: Int) {
        val widthM = counts / widthCountsFull * widthMetersFull
        stateCsv.write("$arrivalNs,$mcuMicros,$widthM,$counts,$trigger"); stateCsv.newLine()
        stateCount++
        if (stateCount % 50 == 0) stateCsv.flush()
        stopNs = arrivalNs
    }

    @Synchronized
    fun writeTactile(arrivalNs: Long, mcuMicros: Long, channels: IntArray) {
        if (tactileCsv == null) {
            tactileChannels = channels.size
            val header = buildString {
                append("monotonic_ns,mcu_micros")
                for (c in channels.indices) append(",ch$c")
            }
            tactileCsv = open("tactile.csv", header)
        }
        val sb = StringBuilder().append(arrivalNs).append(',').append(mcuMicros)
        for (v in channels) sb.append(',').append(v)
        tactileCsv!!.write(sb.toString()); tactileCsv!!.newLine()
        tactileCount++
        if (tactileCount % 50 == 0) tactileCsv!!.flush()
        stopNs = arrivalNs
    }

    /** Called by the camera module to register its mp4 + frame index. */
    @Synchronized
    fun setVideo(streamId: String, mp4: String, indexCsv: String, w: Int, h: Int, frames: Int) {
        videoStreamId = streamId; videoFile = mp4; videoIndexFile = indexCsv
        videoW = w; videoH = h; videoCount = frames
    }

    /** Called by the IMU module to register the orientation stream. */
    @Synchronized
    fun setImu(streamId: String, file: String, samples: Int) {
        imuStreamId = streamId; imuFile = file; imuCount = samples
    }

    fun statusLine(): String =
        "state=$stateCount tactile=$tactileCount" +
            (videoStreamId?.let { " video=$videoCount" } ?: "") +
            (imuStreamId?.let { " imu=$imuCount" } ?: "")

    @Synchronized
    fun finalizeEpisode(): File {
        stateCsv.flush(); stateCsv.close()
        tactileCsv?.flush(); tactileCsv?.close()

        val streams = JSONArray()
        streams.put(JSONObject().apply {
            put("id", "gripper_state"); put("kind", "gripper_state")
            put("file", "gripper_state.csv"); put("timestamp_field", "monotonic_ns")
            put("sample_count", stateCount); put("units", "m")
            put("plugin", "rp2040.encoder")
            put("clock_fit", JSONObject().apply { put("a", 1.0); put("b", 0.0) })
        })
        if (tactileCount > 0) {
            val ch = JSONArray()
            for (c in 0 until tactileChannels) ch.put(JSONObject().apply {
                put("name", "ch$c"); put("unit", "raw"); put("location", "pad_$c")
            })
            streams.put(JSONObject().apply {
                put("id", "tactile0"); put("kind", "tactile")
                put("file", "tactile.csv"); put("timestamp_field", "monotonic_ns")
                put("sample_count", tactileCount); put("plugin", "rp2040.tactile")
                put("channels", ch)
                put("clock_fit", JSONObject().apply { put("a", 1.0); put("b", 0.0) })
            })
        }
        videoStreamId?.let { vid ->
            streams.put(JSONObject().apply {
                put("id", vid); put("kind", "video_rgb")
                put("file", videoFile); put("index_file", videoIndexFile)
                put("timestamp_field", "monotonic_ns"); put("sample_count", videoCount)
                put("codec", "h264")
                put("frame_size", JSONObject().apply { put("width", videoW); put("height", videoH) })
            })
        }
        imuStreamId?.let { iid ->
            streams.put(JSONObject().apply {
                put("id", iid); put("kind", "imu")
                put("file", imuFile); put("timestamp_field", "monotonic_ns")
                put("sample_count", imuCount); put("frame", "head")
            })
        }

        val manifest = JSONObject().apply {
            put("format_version", "0.1.0")
            put("episode_id", episodeId)
            put("task_label", "native USB+sensor capture test")
            put("conventions", JSONObject().apply {
                put("length_unit", "m"); put("time_unit", "ns")
                put("world_frame", "openxr_y_up_rh"); put("quaternion_order", "xyzw")
            })
            put("device", JSONObject().apply {
                put("model", Build.MODEL ?: "PICO")
                put("platform", "pico")
                put("os", "Android ${Build.VERSION.RELEASE}")
                put("app_version", "0.1.0")
                put("capabilities", JSONObject().apply {
                    put("ego_rgb", false); put("ego_depth", false)
                    put("head_pose", false); put("hand_tracking", false)
                    put("controller_pose", false); put("world_frame", "openxr_y_up_rh")
                })
            })
            put("clock", JSONObject().apply {
                put("source", CaptureClock.SOURCE); put("unit", "ns")
                put("start_monotonic_ns", startNs); put("stop_monotonic_ns", stopNs)
            })
            put("streams", streams)
            put("status", "finalized")
        }
        File(dir, "manifest.json").writeText(manifest.toString(2))
        return dir
    }
}
