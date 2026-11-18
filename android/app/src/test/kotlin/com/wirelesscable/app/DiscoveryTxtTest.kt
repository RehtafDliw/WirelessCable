package com.wirelesscable.app

import com.wirelesscable.app.core.discovery.DiscoveryTxt
import com.wirelesscable.app.core.network.LocalSubnet
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * TXT 地址候选解析。真机上电脑有两张网卡（172.18.15.63 / 172.17.13.108），
 * A 记录只会给一条，客户端必须能从 TXT 里拿到完整列表。
 */
class DiscoveryTxtTest {

    @Test
    fun hostComesFirstThenTxtCandidates() {
        val result = DiscoveryTxt.candidates("172.17.13.108", "172.17.13.108,172.18.15.63")

        assertEquals(listOf("172.17.13.108", "172.18.15.63"), result)
    }

    @Test
    fun invalidEntriesAreDropped() {
        val result = DiscoveryTxt.candidates("172.18.15.63", " ,not-an-ip,999.1.1.1,172.17.13.108")

        assertEquals(listOf("172.18.15.63", "172.17.13.108"), result)
    }

    @Test
    fun duplicatesAreRemoved() {
        val result = DiscoveryTxt.candidates("172.18.15.63", "172.18.15.63,172.18.15.63")

        assertEquals(listOf("172.18.15.63"), result)
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
        assertEquals(0xAC120F3F.toInt(), LocalSubnet.toInt("172.18.15.63"))
        assertNull(LocalSubnet.toInt("172.18.15"))
        assertNull(LocalSubnet.toInt("172.18.15.999"))

        // preferLocal 在本机只能验证"不丢地址"
        val input = listOf("172.17.13.108", "172.18.15.63")
        assertEquals(input.toSet(), LocalSubnet.preferLocal(input).toSet())
    }
}
