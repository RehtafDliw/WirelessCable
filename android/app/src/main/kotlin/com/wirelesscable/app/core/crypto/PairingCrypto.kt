package com.wirelesscable.app.core.crypto

import java.nio.charset.StandardCharsets
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 配对密码学，与 Windows 端 PairingCrypto.cs 逐字节对齐。
 *
 * 依据：NIST SP 800-56A（ECDH P-256，取 X 坐标）、RFC 5869（HKDF-SHA256）、
 * RFC 2104（HMAC-SHA256）、FIPS 186-5（ECDSA P-256）、NIST SP 800-38D（AES-256-GCM）。
 *
 * 转录字节布局（禁止改动）：
 * UTF8("WC-PAIR-v1\n" + deviceId + "\n" + clientEphemeralPublicKey + "\n" +
 *      serverEphemeralPublicKey + "\n" + clientNonce + "\n" + serverNonce)，取 SHA-256。
 */
object PairingCrypto {

    const val TRANSCRIPT_PREFIX = "WC-PAIR-v1"
    const val HKDF_INFO = "WirelessCable/pair/v1"
    const val CONFIRM_LABEL = "confirm:"
    const val TOKEN_HKDF_INFO = "WirelessCable/token/v1"

    /** 校验码派生标签，与 .NET 端 PairingCrypto.SasLabel 一致。 */
    const val SAS_LABEL = "sas:v1"

    private const val KEY_SIZE = 32
    private const val GCM_TAG_BITS = 128
    private const val GCM_NONCE_SIZE = 12
    private const val GCM_TAG_SIZE = 16

    private val random = SecureRandom()

    /** 生成 P-256 临时 ECDH 密钥对。 */
    fun generateEphemeralKeyPair(): KeyPair {
        val generator = KeyPairGenerator.getInstance("EC")
        generator.initialize(ECGenParameterSpec("secp256r1"), random)
        return generator.generateKeyPair()
    }

    fun encodePublicKey(key: PublicKey): String =
        Base64.getEncoder().encodeToString(key.encoded)

    fun decodePublicKey(base64: String): PublicKey {
        val bytes = Base64.getDecoder().decode(base64)
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(bytes))
        require((key as ECPublicKey).params.curve.field.fieldSize == 256) { "仅支持 P-256 公钥" }
        return key
    }

    /** ECDH 共享秘密：32 字节 X 坐标。 */
    fun deriveSharedSecret(privateKey: PrivateKey, peerPublicKey: PublicKey): ByteArray {
        val agreement = KeyAgreement.getInstance("ECDH")
        agreement.init(privateKey)
        agreement.doPhase(peerPublicKey, true)
        return agreement.generateSecret()
    }

    /** 会话转录哈希。 */
    fun buildTranscript(
        deviceId: String,
        clientEphemeralPublicKey: String,
        serverEphemeralPublicKey: String,
        clientNonce: String,
        serverNonce: String,
    ): ByteArray {
        val text = listOf(
            TRANSCRIPT_PREFIX,
            deviceId,
            clientEphemeralPublicKey,
            serverEphemeralPublicKey,
            clientNonce,
            serverNonce,
        ).joinToString("\n")
        return MessageDigest.getInstance("SHA-256").digest(text.toByteArray(StandardCharsets.UTF_8))
    }

    /** HKDF-SHA256（RFC 5869）。 */
    fun hkdf(
        ikm: ByteArray,
        salt: ByteArray?,
        info: ByteArray,
        length: Int = KEY_SIZE,
    ): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        val effectiveSalt = if (salt == null || salt.isEmpty()) ByteArray(32) else salt
        mac.init(SecretKeySpec(effectiveSalt, "HmacSHA256"))
        val prk = mac.doFinal(ikm)

        mac.init(SecretKeySpec(prk, "HmacSHA256"))

        val okm = ByteArray(length)
        var previous = ByteArray(0)
        var offset = 0
        var counter = 1

        while (offset < length) {
            mac.reset()
            mac.update(previous)
            mac.update(info)
            mac.update(counter.toByte())
            previous = mac.doFinal()

            val take = minOf(previous.size, length - offset)
            System.arraycopy(previous, 0, okm, offset, take)
            offset += take
            counter++
        }

        return okm
    }

    fun derivePairingKey(sharedSecret: ByteArray, transcript: ByteArray): ByteArray =
        hkdf(sharedSecret, transcript, HKDF_INFO.toByteArray(StandardCharsets.UTF_8))

    /** 验证码确认值：HMAC-SHA256(pairingKey, "confirm:" + code)。 */
    fun computeConfirmation(pairingKey: ByteArray, code: String): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(pairingKey, "HmacSHA256"))
        return mac.doFinal((CONFIRM_LABEL + code).toByteArray(StandardCharsets.UTF_8))
    }

    /**
     * 从配对密钥派生六位校验码（SAS）。两端算出同一个码用于比对，不做手输。
     * 布局与 .NET 端 PairingCrypto.DeriveSasCode 一致：HMAC-SHA256 后取前 4 字节大端，模 10^6。
     */
    fun deriveSasCode(pairingKey: ByteArray): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(pairingKey, "HmacSHA256"))
        val digest = mac.doFinal(SAS_LABEL.toByteArray(StandardCharsets.UTF_8))

        val value = ((digest[0].toLong() and 0xFF) shl 24 or
            ((digest[1].toLong() and 0xFF) shl 16) or
            ((digest[2].toLong() and 0xFF) shl 8) or
            (digest[3].toLong() and 0xFF)) % 1_000_000L

        return value.toString().padStart(6, '0')
    }

    /** 用长期身份私钥对转录签名（SHA256withECDSA，DER 编码，与 .NET 的 Rfc3279DerSequence 对应）。 */
    fun signTranscript(privateKey: PrivateKey, transcript: ByteArray): ByteArray {
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(privateKey)
        signature.update(transcript)
        return signature.sign()
    }

    /**
     * 解开服务端封装的设备令牌。
     * 输入布局：nonce(12) || tag(16) || ciphertext，AAD 为 requestId。
     */
    fun unsealToken(pairingKey: ByteArray, requestId: String, sealed: ByteArray): ByteArray {
        require(sealed.size > GCM_NONCE_SIZE + GCM_TAG_SIZE) { "封装令牌长度非法" }

        val key = hkdf(pairingKey, null, TOKEN_HKDF_INFO.toByteArray(StandardCharsets.UTF_8))
        val nonce = sealed.copyOfRange(0, GCM_NONCE_SIZE)
        val tag = sealed.copyOfRange(GCM_NONCE_SIZE, GCM_NONCE_SIZE + GCM_TAG_SIZE)
        val ciphertext = sealed.copyOfRange(GCM_NONCE_SIZE + GCM_TAG_SIZE, sealed.size)

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(GCM_TAG_BITS, nonce))
        cipher.updateAAD(requestId.toByteArray(StandardCharsets.UTF_8))

        // JCE 的 AEAD 需要把 tag 附在密文尾部
        return cipher.doFinal(ciphertext + tag)
    }

    fun randomNonce(bytes: Int = 16): ByteArray {
        val buffer = ByteArray(bytes)
        random.nextBytes(buffer)
        return buffer
    }

    fun base64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)
}
