package com.wirelesscable.app

import com.wirelesscable.app.core.crypto.PairingCrypto
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.Fingerprint
import com.wirelesscable.app.core.network.NetworkErrorCodes
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.network.WcApiClient
import com.wirelesscable.app.core.protocol.ErrorCodes
import com.wirelesscable.app.core.protocol.PairingRequest
import com.wirelesscable.app.core.protocol.ProtocolConstants
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * 真实 HTTPS 联调测试：直接打运行中的 Windows 桌面端。
 *
 * 运行时通过系统属性传入参数，缺省则跳过（保证 CI/无服务端时不会误报失败）：
 *   -Dwc.host=127.0.0.1 -Dwc.port=47110 -Dwc.fingerprint=<64 位指纹>
 */
class LiveServerTest {

    private fun endpoint(): ServerEndpoint? {
        val host = System.getProperty("wc.host").orEmpty()
        val port = System.getProperty("wc.port").orEmpty()
        val fingerprint = System.getProperty("wc.fingerprint").orEmpty()
        assumeTrue("未提供服务端参数，跳过实连测试", host.isNotEmpty() && fingerprint.isNotEmpty())
        return ServerEndpoint.parse(host, port)
    }

    private fun fingerprint(): String = System.getProperty("wc.fingerprint").orEmpty()

    @Test
    fun anonymousHealthOverPinnedHttpsIsMinimal() = runBlocking {
        val endpoint = endpoint()!!
        val client = WcApiClient.create(endpoint, setOf(fingerprint()))

        val health = client.health()
        assertTrue("健康检查应成功，实际：$health", health is ApiOutcome.Success)
        val healthValue = (health as ApiOutcome.Success).value

        assertEquals("running", healthValue.status)
        assertEquals(endpoint.port, healthValue.port)
        assertEquals(fingerprint(), healthValue.certificateFingerprint)

        // 匿名调用方拿不到运行态细节（K5）
        assertFalse("匿名请求不应声明已认证", healthValue.authenticated)
        assertTrue("匿名请求不应拿到能力清单", healthValue.capabilities.isEmpty())
        assertEquals(0L, healthValue.uptimeSeconds)
    }

    @Test
    fun deviceInfoRequiresDeviceToken() = runBlocking {
        val endpoint = endpoint()!!
        val client = WcApiClient.create(endpoint, setOf(fingerprint()))

        // 结构合法但不存在的令牌：服务端必须回 401，而不是把设备信息交出去
        val outcome = client.deviceInfo(token = "0".repeat(64))

        assertTrue("无有效令牌不应拿到设备信息，实际：$outcome", outcome is ApiOutcome.Failure)
        assertEquals(ErrorCodes.UNAUTHORIZED, (outcome as ApiOutcome.Failure).code)
    }

    @Test
    fun pairingRequestOverPinnedHttpsReturnsChallenge() = runBlocking {
        val endpoint = endpoint()!!
        val client = WcApiClient.create(endpoint, setOf(fingerprint()))

        val ephemeral = PairingCrypto.generateEphemeralKeyPair()
        val identity = java.security.KeyPairGenerator.getInstance("EC").run {
            initialize(java.security.spec.ECGenParameterSpec("secp256r1"))
            generateKeyPair()
        }

        val request = PairingRequest(
            deviceId = "0123456789abcdef0123456789abcdef",
            deviceName = "JVM 联调机",
            platform = "android",
            identityPublicKey = PairingCrypto.encodePublicKey(identity.public),
            ephemeralPublicKey = PairingCrypto.encodePublicKey(ephemeral.public),
            clientNonce = PairingCrypto.base64(PairingCrypto.randomNonce()),
        )

        val outcome = client.pairRequest(request)
        assertTrue("配对请求应成功，实际：$outcome", outcome is ApiOutcome.Success)

        val challenge = (outcome as ApiOutcome.Success).value
        assertEquals("pending", challenge.state)
        assertTrue(challenge.requestId.isNotEmpty())
        assertTrue(challenge.serverEphemeralPublicKey.isNotEmpty())
        assertEquals("P256-ECDH-HKDF-SHA256", challenge.pairingSuite)
        assertTrue(challenge.expiresAtUnixMs > System.currentTimeMillis())
    }

    @Test
    fun wrongFingerprintIsRejected() = runBlocking {
        val endpoint = endpoint()!!
        val bogus = "0".repeat(64)
        val client = WcApiClient.create(endpoint, setOf(bogus))

        val outcome = client.health()

        assertTrue("指纹不符必须失败，实际：$outcome", outcome is ApiOutcome.Failure)
        val failure = outcome as ApiOutcome.Failure
        assertEquals(NetworkErrorCodes.TLS_PIN_MISMATCH, failure.code)
    }

    @Test
    fun fingerprintUtilitiesWork() {
        assertTrue(Fingerprint.isValid("a".repeat(64)))
        assertFalse(Fingerprint.isValid("abc"))
        assertEquals("aabb", Fingerprint.normalize("AA:BB"))
        assertEquals(ProtocolConstants.DEFAULT_HTTPS_PORT, ServerEndpoint.parse("192.168.1.2", "")!!.port)
    }
}
