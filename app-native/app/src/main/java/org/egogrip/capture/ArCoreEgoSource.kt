package org.egogrip.capture

import android.content.Context
import android.opengl.GLES20
import android.opengl.GLSurfaceView
import android.util.Log
import com.google.ar.core.Config
import com.google.ar.core.Session
import com.google.ar.core.TrackingState
import javax.microedition.khronos.egl.EGLConfig
import javax.microedition.khronos.opengles.GL10

/**
 * ARCore ego source: drives the phone's rear camera to produce (a) the ego video, (b) the wearer's
 * head/ego 6-DoF world pose (VIO), and (c) per-frame camera image + intrinsics + world pose handed to
 * the [GripperMarkerTracker] so it can localize the AprilTag on the gripper.
 *
 * Runs as a GLSurfaceView.Renderer (ARCore needs a GL camera texture). The GLSurfaceView doubles as
 * the aim preview. The session is created once ([create]); a take binds the live [EpisodeWriter] +
 * tracker via [startRecording] and releases them in [stopRecording]. Frame is ARCore's
 * (+X right, +Y up, −Z forward = `openxr_y_up_rh`), so poses are written raw.
 *
 * ⚠ VERIFY IN ANDROID STUDIO: ARCore session lifecycle + availability/install (ArCoreApk), exact API
 * (Session.update, Frame.acquireCameraImage, Camera.pose/imageIntrinsics), and the GL background
 * render (this skeleton consumes the camera image on the CPU but does not draw a background, so the
 * preview may be black — add a background renderer if you want a visible aim view). Requires
 * `com.google.ar:core` + Google Play Services for AR.
 */
class ArCoreEgoSource(
    private val context: Context,
    private val onLog: (String) -> Unit,
) : GLSurfaceView.Renderer {

    private var session: Session? = null
    private var textureId = -1
    @Volatile private var recording = false
    private var writer: EpisodeWriter? = null
    private var tracker: GripperMarkerTracker? = null
    private var ego: EgogripEgoRecorder? = null
    private val egoStreamId = "ego"
    private var egoW = 0
    private var egoH = 0
    private var displayRotation = 0

    /** Create the ARCore session (call once, on the UI thread, after CAMERA permission). */
    fun create(): Boolean {
        return try {
            val s = Session(context)
            val cfg = Config(s).apply {
                focusMode = Config.FocusMode.AUTO
                updateMode = Config.UpdateMode.LATEST_CAMERA_IMAGE
                depthMode = if (s.isDepthModeSupported(Config.DepthMode.AUTOMATIC))
                    Config.DepthMode.AUTOMATIC else Config.DepthMode.DISABLED
            }
            s.configure(cfg)
            session = s
            true
        } catch (e: Exception) { onLog("ARCore create failed: ${e.message}"); false }
    }

    fun resume() { try { session?.resume() } catch (e: Exception) { onLog("ARCore resume: ${e.message}") } }
    fun pause() { try { session?.pause() } catch (_: Exception) {} }

    /** Bind the live episode writer + gripper tracker and arm recording. */
    fun startRecording(w: EpisodeWriter, t: GripperMarkerTracker?) {
        writer = w
        tracker = t
        w.setEgoCapabilities(rgb = true, depth = (session?.config?.depthMode == Config.DepthMode.AUTOMATIC))
        recording = true
    }

    fun stopRecording() {
        recording = false
        ego?.let { writer?.addRawStream(it.stopRecording(egoStreamId, egoW, egoH)) }
        ego = null; writer = null; tracker = null
    }

    // ---- GLSurfaceView.Renderer ----
    override fun onSurfaceCreated(gl: GL10?, config: EGLConfig?) {
        val tex = IntArray(1)
        GLES20.glGenTextures(1, tex, 0)
        textureId = tex[0]
        GLES20.glBindTexture(0x8D65 /* GL_TEXTURE_EXTERNAL_OES */, textureId)
        try { session?.setCameraTextureName(textureId) } catch (_: Exception) {}
    }

    override fun onSurfaceChanged(gl: GL10?, width: Int, height: Int) {
        try { session?.setDisplayGeometry(displayRotation, width, height) } catch (_: Exception) {}
    }

    override fun onDrawFrame(gl: GL10?) {
        val s = session ?: return
        try {
            if (textureId >= 0) s.setCameraTextureName(textureId)
            val frame = s.update()
            val cam = frame.camera
            if (cam.trackingState != TrackingState.TRACKING) return
            if (!recording) return
            val w = writer ?: return
            val ns = CaptureClock.nowNs()

            // head/ego pose (ARCore world = openxr_y_up_rh)
            val t = FloatArray(3); val q = FloatArray(4)
            cam.pose.getTranslation(t, 0); cam.pose.getRotationQuaternion(q, 0)
            w.writeHeadPose(ns, t[0], t[1], t[2], q[0], q[1], q[2], q[3], 1)

            // camera image → ego video + gripper marker tracking
            val image = try { frame.acquireCameraImage() } catch (_: Exception) { null }
            if (image != null) {
                if (ego == null) {
                    egoW = image.width; egoH = image.height
                    ego = EgogripEgoRecorder().also { it.beginRecording(w.dir.absolutePath, egoStreamId, egoW, egoH, 30) }
                }
                ego?.pushImage(image, ns)
                tracker?.onFrame(image, cam, ns) // localizes the gripper marker → writes gripper_pose
                image.close()
            }
        } catch (e: Exception) {
            Log.w("egogrip", "ARCore frame error: ${e.message}")
        }
    }
}
