package org.egogrip.capture

/**
 * Parser for the RP2040 USB-serial protocol (firmware/rp2040-gripper/README.md).
 *
 * Frame:  0xAA 0x55 | type:u8 | seq:u8 | micros:u32(LE) | len:u8 | payload[len] | crc8
 * crc8 = CRC-8 (poly 0x07, init 0x00) over bytes [type .. last payload byte].
 *
 * Feed raw bytes from the serial read thread via [feed]; valid frames are delivered to the
 * callbacks on that same thread. Bytes are buffered across reads, so split frames are fine.
 */
class Protocol(
    private val onState: (micros: Long, rawCounts: Int, deltaCounts: Int, trigger: Int) -> Unit,
    private val onTactile: (micros: Long, channels: IntArray) -> Unit,
    private val onSync: (micros: Long, eventId: Long) -> Unit = { _, _ -> },
    private val onInfo: (text: String) -> Unit = {},
    private val onCrcError: () -> Unit = {},
) {
    companion object {
        const val M0 = 0xAA
        const val M1 = 0x55
        const val T_STATE = 0x01
        const val T_TACTILE = 0x02
        const val T_SYNC = 0x03
        const val T_INFO = 0x10

        fun crc8(data: ByteArray, from: Int, toExclusive: Int): Int {
            var crc = 0
            for (i in from until toExclusive) {
                crc = crc xor (data[i].toInt() and 0xFF)
                repeat(8) {
                    crc = if (crc and 0x80 != 0) (crc shl 1) xor 0x07 else crc shl 1
                    crc = crc and 0xFF
                }
            }
            return crc
        }
    }

    private val buf = ArrayDeque<Byte>()

    fun feed(data: ByteArray, length: Int = data.size) {
        for (i in 0 until length) buf.addLast(data[i])
        parse()
    }

    private fun parse() {
        while (true) {
            // find magic
            while (buf.size >= 2 && !(u(buf.elementAt(0)) == M0 && u(buf.elementAt(1)) == M1)) {
                buf.removeFirst()
            }
            // header = magic(2)+type+seq+micros(4)+len = 9 bytes minimum
            if (buf.size < 9) return
            val len = u(buf.elementAt(8))
            val total = 9 + len + 1 // + crc
            if (buf.size < total) return

            val frame = ByteArray(total)
            for (i in 0 until total) frame[i] = buf.elementAt(i)

            val type = u(frame[2])
            val micros = readU32(frame, 4)
            val crcGot = u(frame[total - 1])
            val crcCalc = crc8(frame, 2, total - 1) // type..payload
            if (crcGot != crcCalc) {
                buf.removeFirst() // resync past the bad magic and retry
                onCrcError()
                continue
            }
            val payloadStart = 9
            when (type) {
                T_STATE -> if (len >= 7) {
                    val raw = readU16(frame, payloadStart)
                    val delta = readI32(frame, payloadStart + 2)
                    val trig = u(frame[payloadStart + 6])
                    onState(micros, raw, delta, trig)
                }
                T_TACTILE -> if (len >= 1) {
                    val n = u(frame[payloadStart])
                    val ch = IntArray(n)
                    var p = payloadStart + 1
                    for (k in 0 until n) {
                        ch[k] = readI16(frame, p); p += 2
                    }
                    onTactile(micros, ch)
                }
                T_SYNC -> if (len >= 4) onSync(micros, readU32(frame, payloadStart))
                T_INFO -> onInfo(String(frame, payloadStart, len))
            }
            repeat(total) { buf.removeFirst() }
        }
    }

    private fun u(b: Byte) = b.toInt() and 0xFF
    private fun readU32(b: ByteArray, o: Int) =
        ((u(b[o])) or (u(b[o + 1]) shl 8) or (u(b[o + 2]) shl 16) or (u(b[o + 3]) shl 24)).toLong() and 0xFFFFFFFFL
    private fun readI32(b: ByteArray, o: Int) =
        (u(b[o])) or (u(b[o + 1]) shl 8) or (u(b[o + 2]) shl 16) or (u(b[o + 3]) shl 24)
    private fun readI16(b: ByteArray, o: Int): Int {
        val v = (u(b[o])) or (u(b[o + 1]) shl 8)
        return if (v >= 0x8000) v - 0x10000 else v
    }
    private fun readU16(b: ByteArray, o: Int): Int = (u(b[o])) or (u(b[o + 1]) shl 8)
}
