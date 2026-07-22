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

    // On-device width PREVIEW only (counts_per_mm); the pipeline recomputes the authoritative width
    // from raw delta_counts + calibration.json (docs/CALIBRATION.md).
    private val countsPerMm = 50.0

    val episodeId: String = SimpleDateFormat("yyyy-MM-dd'T'HH-mm-ss", Locale.US).format(Date()) + "_native"
    val dir: File = File(File(context.getExternalFilesDir(null), "episodes"), episodeId).apply { mkdirs() }

    private val startNs = CaptureClock.nowNs()
    private var stopNs = startNs

    private val stateCsv = open("gripper_state.csv",
        "monotonic_ns,mcu_micros,raw_counts,delta_counts,width_preview_m,trigger")
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

    // pose6dof streams (phone/ARCore): gripper (from the ego-cam marker) + head/ego (ARCore VIO)
    private var gripperCsv: BufferedWriter? = null
    private var gripperCount = 0
    private var headPoseCsv: BufferedWriter? = null
    private var headPoseCount = 0
    // T_marker_TCP (marker→gripper jaw), recorded into the gripper_pose stream's pose_offset
    private var gripperOffsetT = floatArrayOf(0f, 0f, 0f)
    private var gripperOffsetQ = floatArrayOf(0f, 0f, 0f, 1f)
    private var egoRgb = false
    private var egoDepth = false
    private var headPresent = false

    // raw manifest stream descriptors emitted by the :capture facades (wrist UVC, ego mp4, serial)
    private val rawStreams = mutableListOf<String>()

    private fun open(name: String, header: String): BufferedWriter {
        val w = BufferedWriter(FileWriter(File(dir, name)))
        w.write(header); w.newLine()
        return w
    }

    @Synchronized
    fun writeState(arrivalNs: Long, mcuMicros: Long, rawCounts: Int, deltaCounts: Int, trigger: Int) {
        val widthPreview = deltaCounts / countsPerMm / 1000.0  // preview only; pipeline is authoritative
        stateCsv.write("$arrivalNs,$mcuMicros,$rawCounts,$deltaCounts,$widthPreview,$trigger")
        stateCsv.newLine()
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

    private val poseHeader = "monotonic_ns,x,y,z,qx,qy,qz,qw,tracking_state"

    /** Gripper 6-DoF TCP pose (world), recovered from the AprilTag on the gripper. */
    @Synchronized
    fun writeGripperPose(ns: Long, x: Float, y: Float, z: Float, qx: Float, qy: Float, qz: Float, qw: Float, track: Int) {
        if (gripperCsv == null) gripperCsv = open("gripper_pose.csv", poseHeader)
        gripperCsv!!.write("$ns,$x,$y,$z,$qx,$qy,$qz,$qw,$track"); gripperCsv!!.newLine()
        gripperCount++
        if (gripperCount % 50 == 0) gripperCsv!!.flush()
        stopNs = ns
    }

    /** Head/ego 6-DoF pose (world) from ARCore VIO. */
    @Synchronized
    fun writeHeadPose(ns: Long, x: Float, y: Float, z: Float, qx: Float, qy: Float, qz: Float, qw: Float, track: Int) {
        if (headPoseCsv == null) headPoseCsv = open("head_pose.csv", poseHeader)
        headPoseCsv!!.write("$ns,$x,$y,$z,$qx,$qy,$qz,$qw,$track"); headPoseCsv!!.newLine()
        headPoseCount++
        headPresent = true
        if (headPoseCount % 50 == 0) headPoseCsv!!.flush()
        stopNs = ns
    }

    /** T_marker_TCP: how the marker is mounted relative to the gripper jaw midpoint. */
    @Synchronized
    fun setGripperOffset(t: FloatArray, q: FloatArray) { gripperOffsetT = t; gripperOffsetQ = q }

    /** Declare that the phone's ego RGB (and optionally depth) is being recorded. */
    @Synchronized
    fun setEgoCapabilities(rgb: Boolean, depth: Boolean) { egoRgb = rgb; egoDepth = depth }

    /** Splice a manifest stream descriptor produced by a :capture facade (wrist UVC / ego mp4 / serial).
     *  Accepts a single JSON object, or several comma-joined objects (as EgogripSerial returns). */
    @Synchronized
    fun addRawStream(descriptorJson: String?) {
        if (!descriptorJson.isNullOrBlank()) rawStreams.add(descriptorJson)
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
        "gripper=$gripperCount head=$headPoseCount state=$stateCount tactile=$tactileCount" +
            (videoStreamId?.let { " video=$videoCount" } ?: "") +
            (if (rawStreams.isNotEmpty()) " streams+${rawStreams.size}" else "")

    @Synchronized
    fun finalizeEpisode(): File {
        stateCsv.flush(); stateCsv.close()
        tactileCsv?.flush(); tactileCsv?.close()
        gripperCsv?.flush(); gripperCsv?.close()
        headPoseCsv?.flush(); headPoseCsv?.close()

        val streams = JSONArray()
        if (gripperCount > 0) streams.put(JSONObject().apply {
            put("id", "gripper_pose"); put("kind", "pose6dof")
            put("file", "gripper_pose.csv"); put("timestamp_field", "monotonic_ns")
            put("sample_count", gripperCount); put("frame", "world"); put("units", "m")
            put("pose_offset", JSONObject().apply {
                put("translation_m", JSONArray(listOf(gripperOffsetT[0], gripperOffsetT[1], gripperOffsetT[2])))
                put("rotation_quat_xyzw", JSONArray(listOf(gripperOffsetQ[0], gripperOffsetQ[1], gripperOffsetQ[2], gripperOffsetQ[3])))
            })
        })
        if (headPoseCount > 0) streams.put(JSONObject().apply {
            put("id", "head_pose"); put("kind", "pose6dof")
            put("file", "head_pose.csv"); put("timestamp_field", "monotonic_ns")
            put("sample_count", headPoseCount); put("frame", "world"); put("units", "m")
        })
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
        // raw descriptors from the :capture facades (ego mp4, wrist UVC, serial). May be one object
        // or several comma-joined objects (EgogripSerial) — wrap in [] and merge.
        for (raw in rawStreams) {
            try {
                val arr = JSONArray("[$raw]")
                for (i in 0 until arr.length()) streams.put(arr.getJSONObject(i))
            } catch (_: Exception) { /* skip malformed */ }
        }

        val manifest = JSONObject().apply {
            put("format_version", "0.1.0")
            put("episode_id", episodeId)
            put("task_label", "phone ego capture")
            put("conventions", JSONObject().apply {
                put("length_unit", "m"); put("time_unit", "ns")
                put("world_frame", "openxr_y_up_rh"); put("quaternion_order", "xyzw")
            })
            put("device", JSONObject().apply {
                put("model", Build.MODEL ?: "android")
                put("platform", "android")
                put("os", "Android ${Build.VERSION.RELEASE}")
                put("app_version", "0.1.0")
                put("capabilities", JSONObject().apply {
                    put("ego_rgb", egoRgb); put("ego_depth", egoDepth)
                    put("head_pose", headPresent); put("hand_tracking", false)
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
