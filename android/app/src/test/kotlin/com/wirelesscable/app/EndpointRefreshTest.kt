package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.core.sync.EndpointDecision
import com.wirelesscable.app.data.repository.ConnectionRepository
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 「连接失败后要不要换地址」的决策。
 *
 * 这条逻辑只有四种出口，但每一种误判的代价都不小：
 * 该换不换 → 用户一直连不上；不该换却换 → 把好的地址写坏；
 * 指纹不同却接受 → 固定证书失效（局域网里 mDNS 应答可以伪造）。
 *
 * 真机上没法可靠制造"电脑换了 IP"这个场景（改 App 私有目录要 root，
 * 装 debug 包会换签名），所以四种分支全部在这里覆盖。
 */
class EndpointRefreshTest {

    private val session = ConnectionRepository.SavedSession(
        deviceId = "device-1",
        host = "192.168.1.10",
        port = 47110,
        fingerprint = "a".repeat(64),
    )

    private fun discovered(
        host: String = "192.168.1.10",
        port: Int = 47110,
        deviceId: String = "device-1",
        fingerprint: String = "a".repeat(64),
    ) = DiscoveredServer(
        deviceId = deviceId,
        displayName = "PC",
        host = host,
        port = port,
        fingerprint = fingerprint,
        protocolVersion = 1,
    )

    @Test
    fun `地址变了就采用新地址`() {
        val result = EndpointDecision.decide(session, discovered(host = "192.168.1.77"))

        assertTrue(result is EndpointDecision.Adopt)
        result as EndpointDecision.Adopt
        assertEquals("192.168.1.77", result.host)
        assertEquals(47110, result.port)
    }

    @Test
    fun `端口变了也算变了`() {
        val result = EndpointDecision.decide(session, discovered(port = 47111))

        assertTrue(result is EndpointDecision.Adopt)
        assertEquals(47111, (result as EndpointDecision.Adopt).port)
    }

    @Test
    fun `地址没变就不写库`() {
        assertEquals(EndpointDecision.Unchanged, EndpointDecision.decide(session, discovered()))
    }

    @Test
    fun `扫不到就是没找到`() {
        assertEquals(EndpointDecision.NotFound, EndpointDecision.decide(session, null))
    }

    @Test
    fun `解析失败拿到空地址也算没找到`() {
        assertEquals(
            EndpointDecision.NotFound,
            EndpointDecision.decide(session, discovered(host = "")),
        )
    }

    @Test
    fun `指纹不同必须拒绝`() {
        // 安全边界：局域网里任何人都能广播同名服务，接受新指纹等于把 pinning 作废
        val result = EndpointDecision.decide(
            session,
            discovered(host = "192.168.1.77", fingerprint = "b".repeat(64)),
        )

        assertEquals(EndpointDecision.FingerprintMismatch, result)
    }

    @Test
    fun `deviceId 不是这台就不认`() {
        // decide 的入参只接收"已按 deviceId 过滤过"的那一条；
        // 这里验证调用方传错时会被指纹拦下，而不是静默改用别的电脑
        val other = discovered(deviceId = "device-2", host = "192.168.1.88", fingerprint = "c".repeat(64))

        assertTrue(EndpointDecision.decide(session, other) is EndpointDecision.FingerprintMismatch)
    }

    @Test
    fun `指纹比较忽略冒号和大小写`() {
        val upper = "AA:BB:CC".replace(":", "").uppercase()
        assertTrue(EndpointDecision.sameFingerprint("aabbcc", upper))
        assertTrue(EndpointDecision.sameFingerprint("AA:BB:CC".lowercase(), "aabbcc"))
    }
}
