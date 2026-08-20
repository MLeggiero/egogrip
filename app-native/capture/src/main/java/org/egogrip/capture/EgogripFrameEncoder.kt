package org.egogrip.capture

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaMuxer
import android.util.Log
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Encodes pushed frames to `<streamId>.mp4` (H.264, MediaCodec + MediaMuxer) and writes
 * `<streamId>_frames.csv` (frame_idx, monotonic_ns, pts_ns) on the shared clock.
 *
 * ## Threading — why this matters
 * Everything expensive runs on a private worker thread, NOT the caller's. The caller (Unity's main
 * thread, for the ego stream) only does one memcpy into a pooled buffer and returns. Before this,
 * `pushFrame` ran the whole RGBA→NV12 conversion (~1.2M pixels of scalar Kotlin) plus two blocking
 * 10 ms MediaCodec dequeues inline on the render thread, which dropped the app to well under 1 fps
 * while recording.
 *
 * Back-pressure is handled by DROPPING frames, never by blocking: if the encoder falls behind, the
 * queue fills and new frames are discarded (counted in [dropped]). A dropped frame costs a gap in
 * the video; a blocked frame costs the whole app's frame rate.
 *
 * ## Zero-marshal input
 * [inputBuffer] exposes a direct ByteBuffer whose address Unity resolves ONCE via
 * `AndroidJNI.GetDirectBufferAddress`. Unity then memcpys pixels straight into it and calls
 * [pushFrameDirect], so no `byte[]` crosses JNI per frame — `AndroidJavaObject.Call` with a big
 * array allocates and copies a fresh Java array every call (4.9 MB/frame at 1280x960), which alone
 * generated ~150 MB/s of garbage.
 */
class EgogripFrameEncoder {
    private var codec: MediaCodec? = null
    private var muxer: MediaMuxer? = null
    private var trackIndex = -1
    private var muxerStarted = false
    private var width = 0
    private var height = 0
    private var startNs = 0L
    private var frameIndex = 0
    private var indexCsv: BufferedWriter? = null
    private val info = MediaCodec.BufferInfo()
    private var nv12: ByteArray? = null

    // --- async pipeline ---
    private class Job(val buf: ByteArray, val ns: Long, val isNv12: Boolean)

    private var queue: ArrayBlockingQueue<Job>? = null
    private var rgbaPool: ArrayBlockingQueue<ByteArray>? = null
    private var nv12Pool: ArrayBlockingQueue<ByteArray>? = null
    private var worker: Thread? = null
    @Volatile private var running = false
    @Volatile private var dropped = 0
    @Volatile private var encoded = 0

    /** Direct buffer Unity writes pixels into (see [pushFrameDirect]). */
    private var direct: ByteBuffer? = null

    private companion object {
        const val TAG = "egogrip"
        const val QUEUE_DEPTH = 3       // ~100 ms of slack at 30 fps before we start dropping
        const val POOL_SIZE = QUEUE_DEPTH + 1
    }

    fun start(episodeDir: String, streamId: String, w: Int, h: Int, fps: Int): Boolean {
        try {
            width = w and 1.inv()   // MediaCodec needs even dimensions
            height = h and 1.inv()
            val fmt = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
                setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420SemiPlanar)
                setInteger(MediaFormat.KEY_BIT_RATE, width * height * 4)
                setInteger(MediaFormat.KEY_FRAME_RATE, if (fps > 0) fps else 30)
                setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1)
            }
            codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).apply {
                configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                start()
            }
            val dir = File(episodeDir).apply { mkdirs() }
            muxer = MediaMuxer(File(dir, "$streamId.mp4").absolutePath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
            indexCsv = BufferedWriter(FileWriter(File(dir, "${streamId}_frames.csv"))).apply {
                write("frame_idx,monotonic_ns,pts_ns"); newLine()
            }
            startNs = 0L; frameIndex = 0; muxerStarted = false; trackIndex = -1
            dropped = 0; encoded = 0

            val rgbaSize = width * height * 4
            val nv12Size = width * height * 3 / 2
            nv12 = ByteArray(nv12Size)
            direct = ByteBuffer.allocateDirect(rgbaSize).order(ByteOrder.nativeOrder())

            queue = ArrayBlockingQueue(QUEUE_DEPTH)
            rgbaPool = ArrayBlockingQueue<ByteArray>(POOL_SIZE).apply {
                repeat(POOL_SIZE) { offer(ByteArray(rgbaSize)) }
            }
            nv12Pool = ArrayBlockingQueue<ByteArray>(POOL_SIZE).apply {
                repeat(POOL_SIZE) { offer(ByteArray(nv12Size)) }
            }

            running = true
            worker = Thread({ workerLoop() }, "egogrip-enc-$streamId").apply {
                priority = Thread.NORM_PRIORITY - 1   // never outrank Unity's render thread
                start()
            }
            return true
        } catch (e: Exception) {
            Log.e(TAG, "ego encoder start failed: ${e.message}")
            return false
        }
    }

    /** Address of the direct input buffer, for Unity's `AndroidJNI.GetDirectBufferAddress`. */
    fun inputBuffer(): ByteBuffer? = direct

    /**
     * Encode whatever Unity just wrote into [inputBuffer]. Cheap: one memcpy out of the direct
     * buffer into a pooled array, then hand off to the worker. Nothing crosses JNI but a long.
     */
    fun pushFrameDirect(monotonicNs: Long) {
        val d = direct ?: return
        val buf = rgbaPool?.poll()
        if (buf == null) { dropped++; return }          // encoder behind — drop, don't stall Unity
        d.position(0)
        d.get(buf, 0, minOf(buf.size, d.capacity()))
        if (!submit(Job(buf, monotonicNs, isNv12 = false))) {
            rgbaPool?.offer(buf); dropped++
        }
    }

    /** Legacy byte[] path (kept for callers that still marshal an array across JNI). */
    fun pushFrame(rgba: ByteArray, monotonicNs: Long) {
        if (rgba.size < width * height * 4) return
        val buf = rgbaPool?.poll()
        if (buf == null) { dropped++; return }
        System.arraycopy(rgba, 0, buf, 0, buf.size)
        if (!submit(Job(buf, monotonicNs, isNv12 = false))) {
            rgbaPool?.offer(buf); dropped++
        }
    }

    /**
     * Encode an ARCore/Camera2 YUV_420_888 frame (phone ego path). The [image] is only valid until
     * the caller closes it, so the YUV→NV12 conversion happens here on the caller's thread (already
     * a Camera2 callback thread, not the UI thread) and the worker just encodes.
     */
    fun pushImage(image: android.media.Image, monotonicNs: Long) {
        val buf = nv12Pool?.poll()
        if (buf == null) { dropped++; return }
        yuv420ToNv12(image, buf, width, height)
        if (!submit(Job(buf, monotonicNs, isNv12 = true))) {
            nv12Pool?.offer(buf); dropped++
        }
    }

    private fun submit(job: Job): Boolean = queue?.offer(job) ?: false

    private fun recycle(job: Job) {
        if (job.isNv12) nv12Pool?.offer(job.buf) else rgbaPool?.offer(job.buf)
    }

    // ---------- worker ----------

    private fun workerLoop() {
        while (running) {
            val job = try { queue?.poll(50, TimeUnit.MILLISECONDS) } catch (_: InterruptedException) { null } ?: continue
            try { encodeOne(job) } catch (e: Exception) { Log.e(TAG, "encode failed: ${e.message}") }
            finally { recycle(job) }
        }
        // drain anything still queued at stop time
        while (true) {
            val job = queue?.poll() ?: break
            try { encodeOne(job) } catch (_: Exception) {} finally { recycle(job) }
        }
    }

    private fun encodeOne(job: Job) {
        val c = codec ?: return
        if (startNs == 0L) startNs = job.ns
        val ptsUs = (job.ns - startNs) / 1000

        val src: ByteArray
        val len: Int
        if (job.isNv12) {
            src = job.buf; len = job.buf.size
        } else {
            val out = nv12 ?: return
            rgbaToNv12(job.buf, out, width, height)
            src = out; len = out.size
        }

        // Longer timeout is fine here — this is the worker, not the render thread.
        val inIdx = c.dequeueInputBuffer(20_000)
        if (inIdx >= 0) {
            val ib = c.getInputBuffer(inIdx)
            if (ib != null) {
                ib.clear(); ib.put(src, 0, len)
                c.queueInputBuffer(inIdx, 0, len, ptsUs, 0)
                encoded++
            }
        } else {
            dropped++
        }
        drain(false)
    }

    private fun drain(endOfStream: Boolean) {
        val c = codec ?: return
        if (endOfStream) {
            val inIdx = c.dequeueInputBuffer(20_000)
            if (inIdx >= 0) c.queueInputBuffer(inIdx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
        }
        while (true) {
            val outIdx = c.dequeueOutputBuffer(info, if (endOfStream) 20_000 else 0)
            if (outIdx == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (!endOfStream) return else continue
            } else if (outIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                trackIndex = muxer!!.addTrack(c.outputFormat)
                muxer!!.start(); muxerStarted = true
            } else if (outIdx >= 0) {
                val ob = c.getOutputBuffer(outIdx)
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) info.size = 0
                if (info.size > 0 && muxerStarted && ob != null) {
                    ob.position(info.offset); ob.limit(info.offset + info.size)
                    muxer!!.writeSampleData(trackIndex, ob, info)
                    val ptsNs = info.presentationTimeUs * 1000
                    indexCsv?.let { it.write("$frameIndex,${startNs + ptsNs},$ptsNs"); it.newLine() }
                    frameIndex++
                }
                c.releaseOutputBuffer(outIdx, false)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
            }
        }
    }

    /** Flush + finalize. Returns the number of frames written. */
    fun stop(): Int {
        running = false
        try { worker?.join(2000) } catch (_: InterruptedException) {}
        worker = null
        try { drain(true) } catch (_: Exception) {}
        try { codec?.stop() } catch (_: Exception) {}
        try { codec?.release() } catch (_: Exception) {}
        try { if (muxerStarted) muxer?.stop() } catch (_: Exception) {}
        try { muxer?.release() } catch (_: Exception) {}
        indexCsv?.flush(); indexCsv?.close()
        codec = null; muxer = null; indexCsv = null
        queue = null; rgbaPool = null; nv12Pool = null; direct = null
        if (dropped > 0) Log.w(TAG, "encoder: $frameIndex frames written, $dropped dropped (encoder behind)")
        else Log.i(TAG, "encoder: $frameIndex frames written, 0 dropped")
        return frameIndex
    }

    /** Frames discarded because the encoder could not keep up. */
    fun droppedFrames(): Int = dropped

    // YUV_420_888 (planar/semi-planar, arbitrary strides) → NV12 (Y then interleaved U,V).
    private fun yuv420ToNv12(image: android.media.Image, out: ByteArray, w: Int, h: Int) {
        val yp = image.planes[0]; val up = image.planes[1]; val vp = image.planes[2]
        val yb = yp.buffer; val ub = up.buffer; val vb = vp.buffer
        val yrs = yp.rowStride; val yps = yp.pixelStride
        var o = 0
        if (yps == 1) {
            for (row in 0 until h) { yb.position(row * yrs); yb.get(out, o, w); o += w }
        } else {
            for (row in 0 until h) { val base = row * yrs; for (col in 0 until w) out[o++] = yb.get(base + col * yps) }
        }
        val urs = up.rowStride; val ups = up.pixelStride
        val vrs = vp.rowStride; val vps = vp.pixelStride
        var uo = w * h
        for (row in 0 until h / 2) for (col in 0 until w / 2) {
            out[uo++] = ub.get(row * urs + col * ups)
            out[uo++] = vb.get(row * vrs + col * vps)
        }
    }

    /**
     * BT.601 RGBA → NV12, matching COLOR_FormatYUV420SemiPlanar.
     *
     * Walks two rows at a time so each 2x2 block writes its chroma pair once, and keeps a single
     * running source index instead of recomputing `(j * w + i) * 4` per pixel. That removes a
     * multiply and three bounds-check-inducing recomputations from the inner loop, which is worth
     * real milliseconds at 1.2M pixels/frame.
     */
    private fun rgbaToNv12(rgba: ByteArray, out: ByteArray, w: Int, h: Int) {
        val frameSize = w * h
        val rowBytes = w * 4
        var uv = frameSize
        var row = 0
        while (row < h - 1) {
            var top = row * rowBytes           // source index, top row of the 2x2 block
            var bot = top + rowBytes
            var yTop = row * w
            var yBot = yTop + w
            var col = 0
            while (col < w - 1) {
                // top-left — also the chroma sample for this 2x2 block
                var r = rgba[top].toInt() and 0xFF
                var g = rgba[top + 1].toInt() and 0xFF
                var b = rgba[top + 2].toInt() and 0xFF
                out[yTop] = (((66 * r + 129 * g + 25 * b + 128) shr 8) + 16).coerceIn(0, 255).toByte()
                out[uv] = (((-38 * r - 74 * g + 112 * b + 128) shr 8) + 128).coerceIn(0, 255).toByte()
                out[uv + 1] = (((112 * r - 94 * g - 18 * b + 128) shr 8) + 128).coerceIn(0, 255).toByte()
                uv += 2

                // top-right
                r = rgba[top + 4].toInt() and 0xFF
                g = rgba[top + 5].toInt() and 0xFF
                b = rgba[top + 6].toInt() and 0xFF
                out[yTop + 1] = (((66 * r + 129 * g + 25 * b + 128) shr 8) + 16).coerceIn(0, 255).toByte()

                // bottom-left
                r = rgba[bot].toInt() and 0xFF
                g = rgba[bot + 1].toInt() and 0xFF
                b = rgba[bot + 2].toInt() and 0xFF
                out[yBot] = (((66 * r + 129 * g + 25 * b + 128) shr 8) + 16).coerceIn(0, 255).toByte()

                // bottom-right
                r = rgba[bot + 4].toInt() and 0xFF
                g = rgba[bot + 5].toInt() and 0xFF
                b = rgba[bot + 6].toInt() and 0xFF
                out[yBot + 1] = (((66 * r + 129 * g + 25 * b + 128) shr 8) + 16).coerceIn(0, 255).toByte()

                top += 8; bot += 8; yTop += 2; yBot += 2; col += 2
            }
            row += 2
        }
    }
}
