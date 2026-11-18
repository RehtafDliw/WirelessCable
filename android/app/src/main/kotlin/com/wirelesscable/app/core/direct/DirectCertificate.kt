package com.wirelesscable.app.core.direct

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * 直连会话用的自签证书。
 *
 * 为什么不引 BouncyCastle：这里只需要"一张能握手的证书"，用不上证书链、CRL、SAN。
 * 手写 DER 大约两百行，比多一个 4 MB 的依赖划算，也避免和 Android 自带的
 * BC 版本打架（历史上 `bcprov` 与 `bcpkix` 混用在部分 ROM 上会 `NoClassDefFoundError`）。
 *
 * 密钥用 EC P-256：生成比 RSA-2048 快一个数量级（RSA 在低端机上要几百毫秒），
 * 证书也小；TLS 1.2/1.3 都支持 ECDSA 服务端证书。
 *
 * 校验方式：客户端拿二维码里的指纹做 pinning（见 [com.wirelesscable.app.core.network.PinnedTrustManager]），
 * 所以这张证书不需要被任何 CA 信任，也不做主机名校验。
 */
object DirectCertificate {

    data class Identity(
        val certificate: X509Certificate,
        val privateKey: PrivateKey,
        /** SHA-256(DER)，小写 hex，与二维码里的 `fp` 一致。 */
        val fingerprint: String,
    )

    private val random = SecureRandom()

    /** 一次性会话的证书活 7 天足够；即使泄漏，会话本身几分钟就作废了。 */
    fun issue(validDays: Int = 7): Identity {
        val generator = KeyPairGenerator.getInstance("EC").apply {
            initialize(ECGenParameterSpec("secp256r1"), random)
        }
        val pair = generator.generateKeyPair()

        val now = System.currentTimeMillis()
        val notBefore = Date(now - 60 * 60 * 1000L)
        val notAfter = Date(now + validDays * 24L * 60 * 60 * 1000L)

        // ecdsa-with-SHA256：1.2.840.10045.4.3.2（这个算法标识的 parameters 必须省略）
        val algorithm = derSequence(derOid("1.2.840.10045.4.3.2"))

        val name = derSequence(
            derSet(derSequence(derOid("2.5.4.3"), derUtf8("WirelessCable Direct"))),
        )

        val tbs = derSequence(
            derExplicit(0, derInteger(BigInteger.valueOf(2))),  // v3
            derInteger(BigInteger(1, secureBytes(16))),
            algorithm,
            name,
            derSequence(derUtcTime(notBefore), derUtcTime(notAfter)),
            name,
            pair.public.encoded,   // SubjectPublicKeyInfo 已经是完整 DER，直接用
        )

        val signer = Signature.getInstance("SHA256withECDSA").apply {
            initSign(pair.private)
            update(tbs)
        }
        val signature = signer.sign()

        val der = derSequence(tbs, algorithm, derBitString(signature))

        val certificate = CertificateFactory.getInstance("X.509")
            .generateCertificate(ByteArrayInputStream(der)) as X509Certificate

        return Identity(certificate, pair.private, sha256Hex(der))
    }

    private fun secureBytes(size: Int): ByteArray = ByteArray(size).also { random.nextBytes(it) }

    fun sha256Hex(bytes: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(bytes)
        return digest.joinToString("") { "%02x".format(it) }
    }

    // ------------------------------------------------------------ DER 编码

    private fun derTlv(tag: Int, content: ByteArray): ByteArray {
        val output = ByteArrayOutputStream(content.size + 6)
        output.write(tag)
        output.write(derLength(content.size))
        output.write(content)
        return output.toByteArray()
    }

    private fun derLength(length: Int): ByteArray = when {
        length < 0x80 -> byteArrayOf(length.toByte())
        length <= 0xFF -> byteArrayOf(0x81.toByte(), length.toByte())
        length <= 0xFFFF -> byteArrayOf(0x82.toByte(), (length shr 8).toByte(), length.toByte())
        else -> byteArrayOf(
            0x83.toByte(),
            (length shr 16).toByte(),
            (length shr 8).toByte(),
            length.toByte(),
        )
    }

    private fun derSequence(vararg parts: ByteArray): ByteArray =
        derTlv(0x30, parts.fold(ByteArray(0)) { acc, part -> acc + part })

    private fun derSet(content: ByteArray): ByteArray = derTlv(0x31, content)

    /** [tagNumber] 是 context-specific 的编号，0 表示 `[0]`，用于 version 与 extensions。 */
    private fun derExplicit(tagNumber: Int, content: ByteArray): ByteArray =
        derTlv(0xA0 or tagNumber, content)

    private fun derInteger(value: BigInteger): ByteArray = derTlv(0x02, value.toByteArray())

    private fun derBitString(content: ByteArray): ByteArray =
        derTlv(0x03, byteArrayOf(0x00) + content)

    private fun derUtf8(text: String): ByteArray = derTlv(0x0C, text.toByteArray(Charsets.UTF_8))

    private fun derUtcTime(date: Date): ByteArray {
        val format = SimpleDateFormat("yyMMddHHmmss'Z'", Locale.US).apply {
            timeZone = TimeZone.getTimeZone("UTC")
        }
        return derTlv(0x17, format.format(date).toByteArray(Charsets.US_ASCII))
    }

    /** 点分 OID → DER。第一个字节是 40×first + second，其余按 7 位分组。 */
    private fun derOid(dotted: String): ByteArray {
        val arcs = dotted.split('.').map { it.toInt() }
        val body = ByteArrayOutputStream()
        body.write(arcs[0] * 40 + arcs[1])

        for (arc in arcs.drop(2)) {
            val stack = ArrayDeque<Int>()
            var value = arc
            stack.addFirst(value and 0x7F)
            value = value shr 7
            while (value > 0) {
                stack.addFirst((value and 0x7F) or 0x80)
                value = value shr 7
            }
            stack.forEach { body.write(it) }
        }

        return derTlv(0x06, body.toByteArray())
    }
}
