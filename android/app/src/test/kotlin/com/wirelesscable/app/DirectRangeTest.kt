package com.wirelesscable.app

import com.wirelesscable.app.core.direct.DirectRange
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * `Range` 解析的边界。
 *
 * 这段算错的表现是"卡死"或"永远传不完"，所以在真机之前先把它钉住：
 * 每一段拼起来必须正好覆盖整个文件，不多一个字节、不少一个字节。
 */
class DirectRangeTest {

    @Test
    fun noHeaderMeansWholeFile() {
        assertEquals(0L to 100L, DirectRange.parse(null, 100))
        assertEquals(0L to 100L, DirectRange.parse("", 100))
    }

    @Test
    fun openEndedRangeGoesToFileEnd() {
        assertEquals(40L to 60L, DirectRange.parse("bytes=40-", 100))
    }

    @Test
    fun closedRangeIsInclusive() {
        // 41..100 是 60 个字节，不是 59
        assertEquals(40L to 60L, DirectRange.parse("bytes=40-99", 100))
    }

    @Test
    fun rangeIsClampedToFileEnd() {
        // 客户端可以按块大小要，最后一块会超出文件尾
        assertEquals(95L to 5L, DirectRange.parse("bytes=95-200", 100))
    }

    @Test
    fun startPastEndIsUnsatisfiable() {
        assertNull(DirectRange.parse("bytes=100-", 100))
        assertNull(DirectRange.parse("bytes=120-130", 100))
    }

    @Test
    fun reversedRangeIsUnsatisfiable() {
        assertNull(DirectRange.parse("bytes=50-10", 100))
    }

    @Test
    fun garbageFallsBackToWholeFile() {
        // 少见的写法不该让正常请求 416
        assertEquals(0L to 100L, DirectRange.parse("bytes=-", 100))
        assertEquals(0L to 100L, DirectRange.parse("items=1-2", 100))
    }

    @Test
    fun zeroLengthFileIsHandled() {
        assertEquals(0L to 0L, DirectRange.parse(null, 0))
    }

    @Test
    fun chunksTileTheFileExactly() {
        // 模拟客户端按 30 字节一块拉一个 100 字节的文件
        val total = 100L
        val chunk = 30L
        var offset = 0L
        var guard = 0

        while (offset < total) {
            check(guard++ < 100) { "分段循环没有前进，会死循环" }
            val (from, length) = DirectRange.parse("bytes=$offset-${offset + chunk - 1}", total)!!
            assertEquals(offset, from)
            offset += length
        }

        assertEquals(total, offset)
    }
}
