package com.wirelesscable.app

import com.wirelesscable.app.core.network.DiagnosticsEvaluator
import com.wirelesscable.app.core.network.DiagnosticsInput
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 诊断结论的判定顺序。
 *
 * 顺序是刻意排的：先排除"根本没在同一网络"，再排除"被隔离"，最后才是端口和证书。
 * 顺序错了会把网络问题说成证书问题，用户会被引到完全错误的方向（重新配对、
 * 改防火墙，结果只是连错了 Wi-Fi）。
 *
 * 每种故障都构造一遍——真机上没法可靠制造 AP 隔离和证书变更。
 */
class DiagnosticsEvaluatorTest {

    @Test
    fun `全部通过时说连接正常`() {
        val report = DiagnosticsEvaluator.evaluate(DiagnosticsInput())

        assertEquals("连接正常", report.conclusion)
        assertFalse(report.suggestRepair)
        assertFalse(report.suggestHotspot)
        assertFalse(report.suggestReconnect)
        assertEquals(5, report.steps.size)
        assertTrue(report.steps.all { it.ok })
    }

    @Test
    fun `没有配对记录先去配对`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(paired = false, localAddress = "192.168.1.20"),
        )

        assertTrue(report.conclusion.contains("还没有配对"))
        assertTrue(report.suggestRepair)
    }

    @Test
    fun `没有局域网地址时说没连 Wi-Fi`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(localAddress = null, sameSubnet = false, discoveryHeld = false, tcpOk = false),
        )

        assertTrue(report.conclusion.contains("没有连 Wi-Fi"))
    }

    @Test
    fun `不同网段排在最前面`() {
        // 即便端口也通不了，也要先说"不在同一网络"——那才是用户能动手改的
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(sameSubnet = false, discoveryHeld = false, tcpOk = false),
        )

        assertTrue(report.conclusion.contains("不在同一个网络"))
        assertFalse(report.suggestHotspot)
    }

    @Test
    fun `同网段但完全不通时怀疑隔离`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(
                sameSubnet = true,
                discoveryHeld = false,
                discoveredHost = null,
                tcpOk = false,
                tcpError = "超时",
            ),
        )

        assertTrue(report.conclusion.contains("AP 隔离"))
        assertTrue(report.suggestHotspot)
    }

    @Test
    fun `搜到了但地址不同时建议重连`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(
                targetHost = "192.168.1.10",
                discoveredHost = "192.168.1.77",
                tcpOk = false,
                tcpError = "超时",
            ),
        )

        assertTrue(report.conclusion.contains("192.168.1.77"))
        assertTrue(report.suggestReconnect)
        assertFalse(report.suggestRepair)
    }

    @Test
    fun `能搜到但端口超时时指向防火墙网络类型`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(
                discoveryHeld = true,
                discoveredHost = "192.168.1.10",
                tcpOk = false,
                tcpError = "超时",
            ),
        )

        assertTrue(report.conclusion.contains("防火墙"))
        assertFalse(report.suggestHotspot)
    }

    @Test
    fun `端口被拒绝时指向服务没开`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(tcpOk = false, tcpError = "被拒绝"),
        )

        assertTrue(report.conclusion.contains("没开"))
    }

    @Test
    fun `证书不匹配时要求重新配对`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(tlsOk = false, tlsError = "证书不匹配"),
        )

        assertTrue(report.suggestRepair)
        assertTrue(report.conclusion.contains("重新配对"))
    }

    @Test
    fun `TLS 失败但证书没问题时不说重新配对`() {
        val report = DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(tlsOk = false, tlsError = "TLS 握手失败"),
        )

        assertFalse(report.suggestRepair)
        assertTrue(report.conclusion.contains("TLS"))
    }
}
