package org.egogrip.capture

import android.media.Image
import com.google.ar.core.Camera
import org.opencv.aruco.Aruco
import org.opencv.core.CvType
import org.opencv.core.Mat
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * Recovers the gripper's 6-DoF world pose from an AprilTag/ArUco marker mounted on the gripper, seen
 * by the phone's ego camera. Per frame:
 *   detect marker → estimatePoseSingleMarkers → T_egocam_marker (OpenCV cam frame)
 *   → convert to ARCore cam frame (180° about X: OpenCV +Y-down/+Z-fwd → ARCore +Y-up/−Z-fwd)
 *   → world:  T_world_TCP = camWorldPose ∘ T_arcam_marker ∘ T_marker_TCP
 *   → write gripper_pose (world, openxr_y_up_rh).
 * Composition is done with quaternions to avoid row/column-major matrix bugs.
 *
 * ⚠ VERIFY IN ANDROID STUDIO / ON-DEVICE: OpenCV must be initialized (OpenCVLoader); the ArUco API
 * differs across OpenCV versions (this uses the classic `org.opencv.aruco`); the ego image is
 * grayscale from the Y plane; the OpenCV↔ARCore camera-frame flip and the marker size / T_marker_TCP
 * calibration all need on-device checking. Skeleton, not a compiled artifact.
 */
class GripperMarkerTracker(
    private val writer: EpisodeWriter,
    private val markerId: Int = 0,
    private val markerSizeM: Double = 0.05,          // printed marker edge length (metres)
    offsetTranslation: FloatArray = floatArrayOf(0f, 0f, 0f), // T_marker_TCP
    offsetQuat: FloatArray = floatArrayOf(0f, 0f, 0f, 1f),
) {
    private val dict = Aruco.getPredefinedDictionary(Aruco.DICT_4X4_50)
    private val distCoeffs = Mat.zeros(1, 5, CvType.CV_64F)
    private val toffT = offsetTranslation
    private val toffQ = offsetQuat
    // 180° about X: maps an OpenCV camera-frame pose into the ARCore camera frame.
    private val qFlipX = floatArrayOf(1f, 0f, 0f, 0f)

    init { writer.setGripperOffset(toffT, toffQ) }

    /** Called per ARCore frame with the CPU image + camera (intrinsics + world pose). */
    fun onFrame(image: Image, cam: Camera, ns: Long) {
        try {
            val w = image.width; val h = image.height
            // Y plane → single-channel grayscale Mat (ArUco needs luma only).
            val yPlane = image.planes[0]
            val y = ByteArray(w * h)
            val yb = yPlane.buffer; val rs = yPlane.rowStride; val ps = yPlane.pixelStride
            var o = 0
            for (row in 0 until h) { val base = row * rs; for (col in 0 until w) y[o++] = yb.get(base + col * ps) }
            val gray = Mat(h, w, CvType.CV_8UC1); gray.put(0, 0, y)

            val corners = ArrayList<Mat>()
            val ids = Mat()
            Aruco.detectMarkers(gray, dict, corners, ids)
            if (ids.total() == 0L) { gray.release(); return }

            val camMat = intrinsicsMat(cam)
            val rvecs = Mat(); val tvecs = Mat()
            Aruco.estimatePoseSingleMarkers(corners, markerSizeM.toFloat(), camMat, distCoeffs, rvecs, tvecs)

            for (i in 0 until ids.total().toInt()) {
                if (ids.get(i, 0)[0].toInt() != markerId) continue
                val rv = rvecs.get(i, 0); val tv = tvecs.get(i, 0) // axis-angle + translation, OpenCV cam frame
                val qMarker = axisAngleToQuat(rv[0].toFloat(), rv[1].toFloat(), rv[2].toFloat())
                var tMarker = floatArrayOf(tv[0].toFloat(), tv[1].toFloat(), tv[2].toFloat())
                // OpenCV cam → ARCore cam
                val qArcam = quatMul(qFlipX, qMarker)
                tMarker = rotate(qFlipX, tMarker)
                // camera world pose
                val tCam = FloatArray(3); val qCam = FloatArray(4)
                cam.pose.getTranslation(tCam, 0); cam.pose.getRotationQuaternion(qCam, 0)
                // world ← cam ∘ marker ∘ markerTCP
                var (tW, qW) = compose(tCam, qCam, tMarker, qArcam)
                val c2 = compose(tW, qW, toffT, toffQ); tW = c2.first; qW = c2.second
                writer.writeGripperPose(ns, tW[0], tW[1], tW[2], qW[0], qW[1], qW[2], qW[3], 1)
                break
            }
            camMat.release(); rvecs.release(); tvecs.release(); gray.release()
        } catch (_: Exception) { /* transient CV error → drop this frame */ }
    }

    private fun intrinsicsMat(cam: Camera): Mat {
        val fl = cam.imageIntrinsics.focalLength   // [fx, fy]
        val pp = cam.imageIntrinsics.principalPoint // [cx, cy]
        val m = Mat.zeros(3, 3, CvType.CV_64F)
        m.put(0, 0, fl[0].toDouble()); m.put(1, 1, fl[1].toDouble())
        m.put(0, 2, pp[0].toDouble()); m.put(1, 2, pp[1].toDouble()); m.put(2, 2, 1.0)
        return m
    }

    // ---- tiny quaternion algebra (xyzw) ----
    private fun axisAngleToQuat(rx: Float, ry: Float, rz: Float): FloatArray {
        val ang = sqrt(rx * rx + ry * ry + rz * rz)
        if (ang < 1e-8f) return floatArrayOf(0f, 0f, 0f, 1f)
        val s = sin(ang / 2) / ang
        return floatArrayOf(rx * s, ry * s, rz * s, cos(ang / 2))
    }

    private fun quatMul(a: FloatArray, b: FloatArray) = floatArrayOf(
        a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
        a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
        a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
        a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
    )

    private fun rotate(q: FloatArray, v: FloatArray): FloatArray {
        val qv = floatArrayOf(v[0], v[1], v[2], 0f)
        val qc = floatArrayOf(-q[0], -q[1], -q[2], q[3])
        val r = quatMul(quatMul(q, qv), qc)
        return floatArrayOf(r[0], r[1], r[2])
    }

    private fun compose(ta: FloatArray, qa: FloatArray, tb: FloatArray, qb: FloatArray): Pair<FloatArray, FloatArray> {
        val rt = rotate(qa, tb)
        return Pair(floatArrayOf(ta[0] + rt[0], ta[1] + rt[1], ta[2] + rt[2]), quatMul(qa, qb))
    }
}
