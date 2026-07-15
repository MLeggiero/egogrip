package org.egogrip.capture

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaMuxer
import android.util.Log
import java.io.BufferedWriter
import java.io.File
import java.io.FileWriter

/**
 * Encodes PUSHED RGBA frames to `<streamId>.mp4` (H.264, MediaCodec + MediaMuxer) and writes
 * `<streamId>_frames.csv` (frame_idx, monotonic_ns, pts_ns) on the shared clock. ByteBuffer input
 * (RGBA→NV12 on the CPU) so callers just hand over pixel bytes — no GL/Surface plumbing.
 *
 * This is the ego record sink: fed synthetic frames now (simulate), and the real PICO enterprise
 * Main Camera Access frames later, through the SAME [pushFrame] seam.
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
            nv12 = ByteArray(width * height * 3 / 2)
            return true
        } catch (e: Exception) {
            Log.e("egogrip", "ego encoder start failed: ${e.message}")
            return false
        }
    }

    fun pushFrame(rgba: ByteArray, monotonicNs: Long) {
        val c = codec ?: return
        if (rgba.size < width * height * 4) return
        if (startNs == 0L) startNs = monotonicNs
        val ptsUs = (monotonicNs - startNs) / 1000
        rgbaToNv12(rgba, nv12!!, width, height)
        val inIdx = c.dequeueInputBuffer(10_000)
        if (inIdx >= 0) {
            val ib = c.getInputBuffer(inIdx) ?: return
            ib.clear(); ib.put(nv12!!)
            c.queueInputBuffer(inIdx, 0, nv12!!.size, ptsUs, 0)
        }
        drain(false)
    }

    private fun drain(endOfStream: Boolean) {
        val c = codec ?: return
        if (endOfStream) {
            val inIdx = c.dequeueInputBuffer(10_000)
            if (inIdx >= 0) c.queueInputBuffer(inIdx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
        }
        while (true) {
            val outIdx = c.dequeueOutputBuffer(info, 10_000)
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
        try { drain(true) } catch (_: Exception) {}
        try { codec?.stop() } catch (_: Exception) {}
        try { codec?.release() } catch (_: Exception) {}
        try { if (muxerStarted) muxer?.stop() } catch (_: Exception) {}
        try { muxer?.release() } catch (_: Exception) {}
        indexCsv?.flush(); indexCsv?.close()
        codec = null; muxer = null; indexCsv = null
        return frameIndex
    }

    // BT.601 RGBA → NV12 (Y plane, then interleaved U,V) matching COLOR_FormatYUV420SemiPlanar.
    private fun rgbaToNv12(rgba: ByteArray, out: ByteArray, w: Int, h: Int) {
        val frameSize = w * h
        var y = 0
        var uv = frameSize
        for (j in 0 until h) {
            for (i in 0 until w) {
                val p = (j * w + i) * 4
                val r = rgba[p].toInt() and 0xFF
                val g = rgba[p + 1].toInt() and 0xFF
                val b = rgba[p + 2].toInt() and 0xFF
                out[y++] = (((66 * r + 129 * g + 25 * b + 128) shr 8) + 16).coerceIn(0, 255).toByte()
                if (j and 1 == 0 && i and 1 == 0) {
                    out[uv++] = (((-38 * r - 74 * g + 112 * b + 128) shr 8) + 128).coerceIn(0, 255).toByte()
                    out[uv++] = (((112 * r - 94 * g - 18 * b + 128) shr 8) + 128).coerceIn(0, 255).toByte()
                }
            }
        }
    }
}
