package com.wirelesscable.app.core.network

import java.security.MessageDigest
import java.security.cert.X509Certificate

/** 证书指纹计算：SHA-256 over DER，小写十六进制，64 字符。 */
object Fingerprint {
    fun sha256(der: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(der)
        val builder = StringBuilder(digest.size * 2)
        for (byte in digest) {
            builder.append(HEX[(byte.toInt() shr 4) and 0x0F])
            builder.append(HEX[byte.toInt() and 0x0F])
        }
        return builder.toString()
    }

    fun of(certificate: X509Certificate): String = sha256(certificate.encoded)

    /** 用户输入可能带冒号或大写，统一归一化后再比较。 */
    fun normalize(raw: String): String = raw.replace(":", "").replace(" ", "").trim().lowercase()

    fun isValid(raw: String): Boolean {
        val normalized = normalize(raw)
        return normalized.length == 64 && normalized.all { it in HEX_DIGITS }
    }

    private const val HEX_DIGITS = "0123456789abcdef"
    private val HEX = HEX_DIGITS.toCharArray()
}
