package com.wirelesscable.app

import com.wirelesscable.app.core.pairing.QrPayloadParser
import com.wirelesscable.app.core.pairing.QrParseResult
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.core.protocol.QrPayload
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 二维码负载解析。字段与 Windows 端 QrPayloadDto 一致（camelCase、Unix 毫秒），
 * 用序列化往返 + 各失败分支覆盖。
 */
class QrPayloadParserTest {

    private val validFingerprint = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90"

    private fun payloadJson(
        expiresAtUnixMs: Long,
        addresses: List<String> = listOf("192.0.2.63", " 192.0.2.63 ", "300.1.1.1"),
        nonce: String = "nonce-1",
    ): String {
        val payload = QrPayload(
            version = ProtocolConstants.CURRENT_VERSION,
            deviceId = "9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3",
            deviceName = "客厅电脑",
            addresses = addresses,
            port = 47110,
            fingerprint = validFingerprint.uppercase(),
            nonce = nonce,
            expiresAtUnixMs = expiresAtUnixMs,
        )
        return WcJson.instance.encodeToString(payload)
    }

    @Test
    fun validPayloadParsesAndNormalizes() {
        val now = 1_000_000L
        val result = QrPayloadParser.parse(payloadJson(expiresAtUnixMs = now + 60_000), now)

        assertTrue(result is QrParseResult.Success)
        val payload = (result as QrParseResult.Success).payload
        assertEquals(ProtocolConstants.CURRENT_VERSION, payload.version)
        assertEquals(validFingerprint, payload.fingerprint)

        // 去重 + 无效地址过滤
        assertEquals(listOf("192.0.2.63"), payload.addresses)
    }

    @Test
    fun roundTripKeepsServerCompatibility() {
        // Windows 端 WcJson 序列化出的字段名是 camelCase，Android 必须原样可读
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE)
        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Success)
        // 老二维码没有 hotspot 字段：解析不能因此失败，兜底通道按"没有"处理
        assertNull((result as QrParseResult.Success).payload.hotspot)
    }

    @Test
    fun hotspotFieldParsesAsFallbackOnly() {
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE).withHotspot(
            """{"ssid":"WirelessCable-7F2A","passphrase":"12345678","address":"192.168.137.1"}""",
        )

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Success)
        val payload = (result as QrParseResult.Success).payload

        // 主路径仍是局域网地址，热点地址不混进来
        assertEquals(listOf("192.0.2.63"), payload.addresses)
        assertEquals("WirelessCable-7F2A", payload.hotspot?.ssid)
        assertEquals("12345678", payload.hotspot?.passphrase)
        assertEquals("192.168.137.1", payload.hotspot?.address)
    }

    @Test
    fun invalidHotspotIsIgnoredNotFatal() {
        // 口令不足 8 位：热点字段作废，但整张码仍然可用（走现有网络）
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE).withHotspot(
            """{"ssid":"WirelessCable-7F2A","passphrase":"123","address":"192.168.137.1"}""",
        )

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Success)
        assertNull((result as QrParseResult.Success).payload.hotspot)
    }

    @Test
    fun hotspotWithInvalidAddressIsIgnored() {
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE).withHotspot(
            """{"ssid":"WirelessCable-7F2A","passphrase":"12345678","address":"999.1.1.1"}""",
        )

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Success)
        assertNull((result as QrParseResult.Success).payload.hotspot)
    }

    @Test
    fun emptyAddressesWithHotspotIsStillUsable() {
        // 电脑只剩热点一条路时 addresses 会是空的，不能用"没有可用地址"把整张码判死
        val json = payloadJson(
            expiresAtUnixMs = Long.MAX_VALUE,
            addresses = listOf("300.1.1.1"),
        ).withHotspot(
            """{"ssid":"WirelessCable-7F2A","passphrase":"12345678","address":"192.168.137.1"}""",
        )

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Success)
        val payload = (result as QrParseResult.Success).payload
        assertEquals(emptyList<String>(), payload.addresses)
        assertEquals("192.168.137.1", QrPayloadParser.toDiscoveredServer(payload).host)
    }

    private fun String.withHotspot(hotspotJson: String): String =
        replaceFirst("}", ",\"hotspot\":$hotspotJson}")

    @Test
    fun emptyTextFails() {
        val result = QrPayloadParser.parse("   ")

        assertTrue(result is QrParseResult.Failure)
        assertEquals("二维码内容为空", (result as QrParseResult.Failure).reason)
    }

    @Test
    fun foreignQrFails() {
        val result = QrPayloadParser.parse("""{"some":"other-app"}""")

        assertTrue(result is QrParseResult.Failure)
        assertEquals("不是 WirelessCable 的二维码", (result as QrParseResult.Failure).reason)
    }

    @Test
    fun unsupportedVersionFails() {
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE)
            .replace("\"version\":${ProtocolConstants.CURRENT_VERSION}", "\"version\":99")

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Failure)
        assertEquals("二维码版本不受支持", (result as QrParseResult.Failure).reason)
    }

    @Test
    fun missingNonceFails() {
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE, nonce = "")

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Failure)
        assertEquals("二维码缺少一次性随机数", (result as QrParseResult.Failure).reason)
    }

    @Test
    fun expiredPayloadFails() {
        val now = 2_000_000L
        val result = QrPayloadParser.parse(payloadJson(expiresAtUnixMs = now - 1), now)

        assertTrue(result is QrParseResult.Failure)
        assertEquals("二维码已过期，请在电脑上重新生成", (result as QrParseResult.Failure).reason)
    }

    @Test
    fun invalidFingerprintFails() {
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE)
            .replace(validFingerprint.uppercase(), "1234")

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Failure)
        assertEquals("二维码里的证书指纹无效", (result as QrParseResult.Failure).reason)
    }

    @Test
    fun noUsableAddressFails() {
        val json = payloadJson(expiresAtUnixMs = Long.MAX_VALUE, addresses = listOf("300.1.1.1"))

        val result = QrPayloadParser.parse(json)

        assertTrue(result is QrParseResult.Failure)
        assertEquals("二维码里没有可用地址", (result as QrParseResult.Failure).reason)
    }

    @Test
    fun toDiscoveredServerPrefersFirstAddress() {
        val result = QrPayloadParser.parse(payloadJson(expiresAtUnixMs = Long.MAX_VALUE))
        assertTrue(result is QrParseResult.Success)

        val server = QrPayloadParser.toDiscoveredServer((result as QrParseResult.Success).payload)
        assertEquals("192.0.2.63", server.host)
        assertEquals(47110, server.port)
        assertEquals("客厅电脑", server.displayName)
        assertEquals(listOf("192.0.2.63"), server.candidates)
    }
}
