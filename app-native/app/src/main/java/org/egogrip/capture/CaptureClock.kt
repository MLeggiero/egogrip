package org.egogrip.capture

import android.os.SystemClock

/**
 * The single monotonic clock shared by every stream (serial, camera, future XR poses).
 * Using one clock for all sources is what makes alignment tractable downstream
 * (see docs/SYNC.md, Tier-0/1).
 */
object CaptureClock {
    /** Nanoseconds on the device's monotonic clock. */
    fun nowNs(): Long = SystemClock.elapsedRealtimeNanos()

    const val SOURCE = "SystemClock.elapsedRealtimeNanos"
}
