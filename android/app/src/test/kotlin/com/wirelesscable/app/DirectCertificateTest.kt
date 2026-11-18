package com.wirelesscable.app

import com.wirelesscable.app.core.direct.DirectCertificate
import com.wirelesscable.app.core.direct.DirectPayload
import com.wirelesscable.app.core.direct.DirectProtocol
import com.wirelesscable.app.core.direct.DirectQr
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.Signature

/**
 * 直连用的自签证书与二维码载荷。
 *
 * 证书是手写 DER，最典型的失败是"编码出来自己都不认识"——
 * 所以这里直接用 [CertificateFactory] 反解 + 拿它的公钥验签，而不是只看字段。
 */
class DirectCertificateTest {

    @Test
    fun issuedCertificateParsesAndVerifies() {
        val identity = DirectCertificate.issue()
        val certificate = identity.certificate

        // 能通过有效期校验 = 扩展/时间编码没写错
        certificate.checkValidity()

        assertTrue(certificate.subjectX500Principal.name.contains("WirelessCable Direct"))
        assertEquals(certificate.subjectX500Principal, certificate.issuerX500Principal)

        // 私钥与证书里的公钥必须成对
        val payload = "wireless-cable".toByteArray()
        val signature = Signature.getInstance("SHA256withECDSA").run {
            initSign(identity.privateKey)
            update(payload)
            sign()
        }

        val verified = Signature.getInstance("SHA256withECDSA").run {
            initVerify(certificate.publicKey)
            update(payload)
            verify(signature)
        }

        assertTrue(verified)
    }

    @Test
    fun fingerprintIsDerSha256Hex() {
        val identity = DirectCertificate.issue()

        assertEquals(64, identity.fingerprint.length)
        assertTrue(identity.fingerprint.all { it in "0123456789abcdef" })

        // 指纹必须就是证书 DER 的 SHA-256：客户端 pinning 靠它
        assertEquals(DirectCertificate.sha256Hex(identity.certificate.encoded), identity.fingerprint)
    }

    @Test
    fun payloadRoundTrip() {
        val payload = DirectPayload(
            sid = "abc123",
            deviceName = "Test Phone",
            host = "192.168.1.7",
            addr = listOf("192.168.1.7"),
            port = 47130,
            fp = "a".repeat(64),
            token = "b".repeat(64),
        )

        val text = WcJson.instance.encodeToString(payload)
        val parsed = DirectQr.parse(text)

        assertEquals(payload.sid, parsed?.sid)
        assertEquals(payload.port, parsed?.port)
        assertEquals(payload.fp, parsed?.fp)
    }

    @Test
    fun pairPayloadIsRejected() {
        // 配对二维码（type 不是 direct）必须返回 null，好让调用方走配对流程
        val pair = """{"version":1,"deviceId":"x","deviceName":"PC","addresses":["192.168.1.2"],
            |"port":47110,"fingerprint":"${"a".repeat(64)}","nonce":"n","expiresAtUnixMs":1}"""
            .trimMargin()

        assertNull(DirectQr.parse(pair))
        assertNull(DirectQr.parse("not a json"))
    }

    @Test
    fun invalidDirectPayloadIsRejected() {
        fun build(port: Int, fp: String, token: String, host: String = "192.168.1.7"): String =
            """{"v":1,"type":"direct","sid":"s","deviceName":"d","host":"$host","addr":[],
               |"port":$port,"fp":"$fp","token":"$token"}""".trimMargin()

        // 端口越界
        assertNull(DirectQr.parse(build(0, "a".repeat(64), "b".repeat(64))))
        assertNull(DirectQr.parse(build(70000, "a".repeat(64), "b".repeat(64))))

        // 指纹不是 32 字节 hex
        assertNull(DirectQr.parse(build(47130, "abc", "b".repeat(64))))

        // 口令太短
        assertNull(DirectQr.parse(build(47130, "a".repeat(64), "short")))

        // 一个地址都没有
        assertNull(DirectQr.parse(build(47130, "a".repeat(64), "b".repeat(64), host = "")))

        // 正常的一条
        assertTrue(DirectQr.parse(build(47130, "a".repeat(64), "b".repeat(64))) != null)
    }

    @Test
    fun protocolConstantsAreStable() {
        assertEquals("/direct/status", DirectProtocol.PATH_STATUS)
        assertEquals("/direct/inbox", DirectProtocol.PATH_INBOX)
        assertEquals("/direct/shared", DirectProtocol.PATH_SHARED)
    }
}
