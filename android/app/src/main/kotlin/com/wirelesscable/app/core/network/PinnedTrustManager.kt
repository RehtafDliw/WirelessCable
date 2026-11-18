package com.wirelesscable.app.core.network

import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.X509TrustManager

/**
 * 只信任指纹匹配的服务器证书。
 *
 * 这是**固定信任（pinning）**而不是 CA 校验：Windows 端用的是自签证书，
 * 没有公共 CA 背书，安全性来自"配对时双方核对过这个指纹"。
 *
 * 未配置指纹时直接拒绝连接——绝不退回"信任所有证书"。
 */
class PinnedTrustManager(pins: Set<String>) : X509TrustManager {

    private val normalizedPins: Set<String> = pins
        .map { Fingerprint.normalize(it) }
        .filter { it.isNotEmpty() }
        .toSet()

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("客户端证书不受支持")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        if (normalizedPins.isEmpty()) {
            throw CertificateException("未配置服务器证书指纹，拒绝连接")
        }

        val leaf = chain?.firstOrNull() ?: throw CertificateException("服务器未提供证书")
        val actual = Fingerprint.of(leaf)

        if (normalizedPins.none { it == actual }) {
            throw CertificateException("证书指纹不匹配：$actual")
        }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
