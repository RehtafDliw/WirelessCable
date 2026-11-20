package com.wirelesscable.app

import com.wirelesscable.app.core.discovery.DiscoveryTxt
import com.wirelesscable.app.core.network.LocalSubnet
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * TXT 地址候选解析。真机上电脑有两张网卡（192.0.2.63 / 198.51.100.108），
 * A 记录只会给一条，客户端必须能从 TXT 里拿到完整列表。
 */
class DiscoveryTxtTest {

    @Test
    fun hostComesFirstThenTxtCandidates() {
        val result = DiscoveryTxt.candidates("198.51.100.108", "198.51.100.108,192.0.2.63")

        assertEquals(listOf("198.51.100.108", "192.0.2.63"), result)
    }

    @Test
    fun invalidEntriesAreDropped() {
        val result = DiscoveryTxt.candidates("192.0.2.63", " ,not-an-ip,999.1.1.1,198.51.100.108")

        assertEquals(listOf("192.0.2.63", "198.51.100.108"), result)
    }

    @Test
    fun duplicatesAreRemoved() {
        val result = DiscoveryTxt.candidates("192.0.2.63", "192.0.2.63,192.0.2.63")

        assertEquals(listOf("192.0.2.63"), result)
    }

    @Test
    fun missingTxtFallsBackToHost() {
        assertEquals(listOf("10.0.0.5"), DiscoveryTxt.candidates("10.0.0.5", null))
        assertTrue(DiscoveryTxt.candidates(null, null).isEmpty())
    }

    @Test
    fun ipv4Validation() {
        assertTrue(DiscoveryTxt.isValidIPv4("192.168.1.1"))
        assertFalse(DiscoveryTxt.isValidIPv4("192.168.1"))
        assertFalse(DiscoveryTxt.isValidIPv4("192.168.1.256"))
        assertFalse(DiscoveryTxt.isValidIPv4("host.local"))
    }

    @Test
    fun subnetOrderingPutsSameSubnetFirst() {
        // 只验证纯函数部分：地址转整数（32 位有符号，故用 toInt() 对齐）
        assertEquals(0xAC120F3F.toInt(), LocalSubnet.toInt("192.0.2.63"))
        assertNull(LocalSubnet.toInt("172.18.15"))
        assertNull(LocalSubnet.toInt("172.18.15.999"))

        // preferLocal 在本机只能验证"不丢地址"
        val input = listOf("198.51.100.108", "192.0.2.63")
        assertEquals(input.toSet(), LocalSubnet.preferLocal(input).toSet())
    }

    @Test
    fun displayNameIsPercentDecoded() {
        // 电脑端把名字 percent-encode 才写进 TXT（TXT 只认 ASCII），这里要还原。
        // 「·」是 U+00B7，UTF-8 为 %C2%B7
        assertEquals("MY-PC · 3F7A", DiscoveryTxt.displayName("MY-PC%20%C2%B7%203F7A"))
        assertEquals("客厅电脑 · 3F7A", DiscoveryTxt.displayName("%E5%AE%A2%E5%8E%85%E7%94%B5%E8%84%91%20%C2%B7%203F7A"))
    }

    @Test
    fun plainDisplayNameStaysAsIs() {
        // 旧版电脑端没编码，解码后必须还是原样；纯 ASCII 名不受影响
        assertEquals("MY-PC", DiscoveryTxt.displayName("MY-PC"))
        assertEquals("客厅电脑", DiscoveryTxt.displayName("客厅电脑"))
        assertNull(DiscoveryTxt.displayName(null))
        assertEquals("", DiscoveryTxt.displayName(""))
    }

    @Test
    fun malformedPercentSequenceFallsBackToRaw() {
        // 非法 % 序列不能让发现流程崩掉，原样返回
        assertEquals("100%", DiscoveryTxt.displayName("100%"))
    }
}
