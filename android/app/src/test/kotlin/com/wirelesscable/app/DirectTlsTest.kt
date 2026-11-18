package com.wirelesscable.app

import com.wirelesscable.app.core.direct.DirectCertificate
import com.wirelesscable.app.core.network.PinnedTrustManager
import okhttp3.OkHttpClient
import okhttp3.Request
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.security.KeyStore
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.TrustManager
import kotlin.concurrent.thread

/**
 * 直连的 TLS 层端到端：本地起一个用 [DirectCertificate] 签出来的服务端，
 * 再用与 App 相同的 [PinnedTrustManager] 连上去。
 *
 * 之所以要在电脑上跑这一条：真机上最容易出问题、又最难远程排查的就是
 * "证书能不能用"——EC 私钥能不能被 KeyManagerFactory 加载、Java 认不认这张手写 DER、
 * 指纹 pinning 会不会因为大小写/编码不一致而失败。这些在 JVM 上都能复现。
 */
class DirectTlsTest {

    private var serverSocket: SSLServerSocket? = null

    @After
    fun tearDown() {
        runCatching { serverSocket?.close() }
    }

    @Test
    fun handshakeSucceedsWithPinnedCertificate() {
        val identity = DirectCertificate.issue()

        val serverContext = SSLContext.getInstance("TLS").apply {
            val password = charArrayOf('t', 'e', 's', 't')
            val keyStore = KeyStore.getInstance("PKCS12").apply { load(null, null) }
            keyStore.setKeyEntry("direct", identity.privateKey, password, arrayOf(identity.certificate))

            val managers = KeyManagerFactory
                .getInstance(KeyManagerFactory.getDefaultAlgorithm())
                .apply { init(keyStore, password) }
                .keyManagers

            init(managers, null, null)
        }

        val server = serverContext.serverSocketFactory.createServerSocket(0) as SSLServerSocket
        serverSocket = server
        val port = server.localPort

        val body = """{"state":"waiting","sid":"test"}"""

        val worker = thread(isDaemon = true) {
            try {
                server.accept().use { socket ->
                    val reader = socket.getInputStream().bufferedReader()
                    // 读到空行 = 请求头结束
                    while (true) {
                        val line = reader.readLine() ?: break
                        if (line.isEmpty()) {
                            break
                        }
                    }

                    val bytes = body.toByteArray(Charsets.UTF_8)
                    socket.getOutputStream().apply {
                        write(
                            (
                                "HTTP/1.1 200 OK\r\n" +
                                    "Content-Type: application/json\r\n" +
                                    "Content-Length: ${bytes.size}\r\n" +
                                    "\r\n"
                                ).toByteArray(Charsets.US_ASCII),
                        )
                        write(bytes)
                        flush()
                    }
                }
            } catch (error: Exception) {
                // 客户端断言失败时会先关连接，这里不用再抛
            }
        }

        val trustManager = PinnedTrustManager(setOf(identity.fingerprint))
        val clientContext = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(trustManager), null)
        }

        val client = OkHttpClient.Builder()
            .sslSocketFactory(clientContext.socketFactory, trustManager)
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(5, TimeUnit.SECONDS)
            .build()

        client.newCall(
            Request.Builder().url("https://127.0.0.1:$port/direct/status").get().build(),
        ).execute().use { response ->
            assertEquals(200, response.code)
            assertEquals(body, response.body?.string())
        }

        worker.join(2_000)
    }

    @Test
    fun wrongFingerprintIsRejected() {
        val identity = DirectCertificate.issue()

        val serverContext = SSLContext.getInstance("TLS").apply {
            val password = charArrayOf('t', 'e', 's', 't')
            val keyStore = KeyStore.getInstance("PKCS12").apply { load(null, null) }
            keyStore.setKeyEntry("direct", identity.privateKey, password, arrayOf(identity.certificate))

            val managers = KeyManagerFactory
                .getInstance(KeyManagerFactory.getDefaultAlgorithm())
                .apply { init(keyStore, password) }
                .keyManagers

            init(managers, null, null)
        }

        val server = serverContext.serverSocketFactory.createServerSocket(0) as SSLServerSocket
        serverSocket = server

        thread(isDaemon = true) {
            runCatching { server.accept().close() }
        }

        // 故意用另一个会话的指纹：必须连不上
        val other = DirectCertificate.issue()
        val trustManager = PinnedTrustManager(setOf(other.fingerprint + ""))
        val clientContext = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(trustManager), null)
        }

        val client = OkHttpClient.Builder()
            .sslSocketFactory(clientContext.socketFactory, trustManager)
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(5, TimeUnit.SECONDS)
            .build()

        val failed = try {
            client.newCall(
                Request.Builder().url("https://127.0.0.1:${server.localPort}/direct/status").get().build(),
            ).execute().close()
            false
        } catch (error: IOException) {
            true
        }

        assertTrue("指纹不匹配时必须握手失败", failed)
    }
}
