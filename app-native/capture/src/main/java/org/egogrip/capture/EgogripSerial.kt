package org.egogrip.capture

import android.content.Context

/**
 * Unity-facing facade for one RP2040 (gripper encoder + tactile) over USB-serial. Mirrors
 * [EgogripCamera]: [openPreview] once at Start so live values flow, [beginRecording]/[stopRecording]
 * around each take, [close] on teardown. Backed by [SerialBackend], which de-dupes USB devices via a
 * static claimed set, so N EgogripSerial instances (one Unity component each) read N distinct Picos.
 *
 *   val s = new AndroidJavaObject("org.egogrip.capture.EgogripSerial", activity)
 *   s.Call<bool>("openPreview")                                   // once, at Start()
 *   s.Call<bool>("beginRecording", episodeDir, 0)                 // per take (index disambiguates)
 *   string streamsJson = s.Call<string>("stopRecording")         // comma-joined manifest descriptor(s)
 *   s.Call("close")
 */
class EgogripSerial(context: Context) {
    private val backend = SerialBackend(context)

    fun openPreview(): Boolean = backend.openPreview()
    fun beginRecording(episodeDir: String, index: Int): Boolean = backend.beginRecording(episodeDir, index)
    fun stopRecording(): String = backend.stopRecording()
    fun close() = backend.close()
    fun isAlive(): Boolean = backend.isAlive()

    // --- live values for the Unity HUD ---
    fun latestWidthPreview(): Double = backend.latestWidthPreview()
    fun latestTactile(): IntArray = backend.latestTactile()
    fun tactileChannels(): Int = backend.latestTactile().size
}
