package com.wirelesscable.app

import com.wirelesscable.app.core.crypto.PairingCrypto
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.nio.charset.StandardCharsets
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 与 Windows 端 PairingCryptoVectorTests 使用同一组常量与标准向量，
 * 保证两端转录布局、HKDF、HMAC 标签完全一致。
 */
class PairingCryptoTest {

    @Test
    fun transcriptHashMatchesCrossLanguageConstant() {
        val transcript = PairingCrypto.buildTranscript(
            deviceId = "9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3",
            clientEphemeralPublicKey = "CLIENT_EPHEMERAL_KEY_BASE64",
            serverEphemeralPublicKey = "SERVER_EPHEMERAL_KEY_BASE64",
            clientNonce = "CLIENT_NONCE_BASE64",
            serverNonce = "SERVER_NONCE_BASE64",
        )

        assertEquals(
            "e70d3db3b61f466f1af5568a486d4362bdfc4d20af758d60049594ef40d4a6e3",
            transcript.joinToString("") { "%02x".format(it) },
        )
    }

    /** RFC 5869 Test Case 1（SHA-256）。 */
    @Test
    fun hkdfMatchesRfc5869TestCase1() {
        val ikm = ByteArray(22) { 0x0b }
        val salt = byteArrayOf(0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c)
        val info = byteArrayOf(0xf0.toByte(), 0xf1.toByte(), 0xf2.toByte(), 0xf3.toByte(), 0xf4.toByte(), 0xf5.toByte(), 0xf6.toByte(), 0xf7.toByte(), 0xf8.toByte(), 0xf9.toByte())

        val okm = PairingCrypto.hkdf(ikm, salt, info, 42)

        assertEquals(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            okm.joinToString("") { "%02x".format(it) },
        )
    }

    @Test
    fun confirmationUsesDocumentedLabel() {
        val key = ByteArray(32) { it.toByte() }

        val actual = PairingCrypto.computeConfirmation(key, "123456")

        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        val expected = mac.doFinal("confirm:123456".toByteArray(StandardCharsets.UTF_8))

        assertArrayEquals(expected, actual)
    }

    /**
     * 校验码（SAS）必须与 .NET 端算出同一个数字，否则两端显示的码对不上。
     * 向量同时写在 tests/WirelessCable.Server.Tests/PairingTests.cs。
     */
    @Test
    fun sasCodeMatchesCrossLanguageVector() {
        val key = ByteArray(32) { it.toByte() }

        assertEquals("640833", PairingCrypto.deriveSasCode(key))
    }

    @Test
    fun sasCodeIsAlwaysSixDigits() {
        repeat(200) { seed ->
            val key = ByteArray(32) { (it * seed + 7).toByte() }
            val code = PairingCrypto.deriveSasCode(key)

            assertEquals(6, code.length)
            assertEquals(code, code.filter(Char::isDigit))
        }
    }

    /**
     * 令牌封装布局：nonce(12) || tag(16) || ciphertext，AAD 为 requestId。
     * 这里用同样的算法手工封一次，再交给 unsealToken 解开，验证布局与 AAD 一致。
     */
    @Test
    fun unsealTokenHandlesNonceTagCiphertextLayout() {
        val pairingKey = ByteArray(32) { (it + 1).toByte() }
        val requestId = "req-123"
        val token = "device-token-value".toByteArray(StandardCharsets.UTF_8)

        val key = PairingCrypto.hkdf(pairingKey, null, "WirelessCable/token/v1".toByteArray(StandardCharsets.UTF_8))
        val nonce = ByteArray(12) { (it * 3).toByte() }

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        cipher.updateAAD(requestId.toByteArray(StandardCharsets.UTF_8))
        val sealed = cipher.doFinal(token)

        // JCE 输出为 ciphertext||tag，重排成协议布局 nonce||tag||ciphertext
        val ciphertext = sealed.copyOfRange(0, sealed.size - 16)
        val tag = sealed.copyOfRange(sealed.size - 16, sealed.size)
        val packaged = nonce + tag + ciphertext

        assertArrayEquals(token, PairingCrypto.unsealToken(pairingKey, requestId, packaged))
    }

    /** ECDH 双方应得到相同共享秘密（自洽性）。 */
    @Test
    fun ecdhBothSidesAgree() {
        val generator = KeyPairGenerator.getInstance("EC")
        generator.initialize(ECGenParameterSpec("secp256r1"))
        val a = generator.generateKeyPair()
        val b = generator.generateKeyPair()

        fun agree(private: java.security.PrivateKey, public: java.security.PublicKey): ByteArray {
            val agreement = KeyAgreement.getInstance("ECDH")
            agreement.init(private)
            agreement.doPhase(public, true)
            return agreement.generateSecret()
        }

        assertArrayEquals(agree(a.private, b.public), agree(b.private, a.public))
    }
}
