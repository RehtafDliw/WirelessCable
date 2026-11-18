package com.wirelesscable.app

import com.wirelesscable.app.core.transfer.BoundedInputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.IOException

/**
 * 分块读取的边界。传输的正确性建立在"每块恰好读 N 字节"上，
 * 这里钉住三个容易出错的地方：不越界、不重复、不关掉底层流。
 */
class BoundedInputStreamTest {

    @Test
    fun readsExactlyTheBudgetThenStops() {
        val inner = ByteArrayInputStream(ByteArray(100) { it.toByte() })
        val bounded = BoundedInputStream(inner, 10)

        val first = bounded.read()
        assertEquals(0, first)

        val rest = ByteArray(20)
        val read = bounded.read(rest, 0, rest.size)

        assertEquals(9, read)
        assertEquals(1, rest[0].toInt())
        assertEquals(-1, bounded.read(rest, 0, rest.size))
        // 预算用尽后不得再消耗底层流
        assertEquals(90, inner.available())
    }

    @Test
    fun budgetShorterThanInnerLeavesRemainder() {
        val inner = ByteArrayInputStream(ByteArray(50))
        val bounded = BoundedInputStream(inner, 50)

        val buffer = ByteArray(64)
        var total = 0
        while (true) {
            val read = bounded.read(buffer, 0, buffer.size)
            if (read < 0) break
            total += read
        }

        assertEquals(50, total)
        assertEquals(0, inner.available())
    }

    @Test
    fun reportableSizeIsClampedToBudget() {
        val inner = ByteArrayInputStream(ByteArray(1000))
        val bounded = BoundedInputStream(inner, 32)

        assertEquals(32, bounded.available())
    }

    @Test
    fun closeDoesNotCloseInnerStream() {
        val inner = TrackingStream(ByteArray(8))
        BoundedInputStream(inner, 8).close()

        assertFalse("底层流由上传逻辑统一关闭", inner.closed)
    }

    @Test(expected = IOException::class)
    fun propagatesInnerFailure() {
        val failing = object : ByteArrayInputStream(ByteArray(8)) {
            override fun read(b: ByteArray, off: Int, len: Int): Int = throw IOException("boom")
        }

        BoundedInputStream(failing, 8).read(ByteArray(4), 0, 4)
    }

    @Test
    fun zeroBudgetIsImmediatelyDone() {
        val inner = ByteArrayInputStream(ByteArray(8))
        val bounded = BoundedInputStream(inner, 0)

        assertEquals(-1, bounded.read())
        assertEquals(-1, bounded.read(ByteArray(4), 0, 4))
        assertTrue(inner.available() == 8)
    }

    private class TrackingStream(bytes: ByteArray) : ByteArrayInputStream(bytes) {
        var closed = false

        override fun close() {
            closed = true
            super.close()
        }
    }
}
