package com.wirelesscable.app

import com.wirelesscable.app.core.direct.DirectCertificate
import com.wirelesscable.app.core.direct.DirectClient
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.security.KeyStore
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import kotlin.concurrent.thread

/**
 * 选路探测（[DirectClient.quickProbe]）。
 *
 * 为什么必须有这条测试：1.5.62 用的是"TCP 连上就算通"，而现场那台 AP 上
 * SYN/ACK、TLS、`status()` 全都正常，一开始传数据就掉到 1 KB/s。
 * 探活必须真的读到字节，否则"快路径"会把唯一能用的路（对方热点）跳过去。
 *
 * 覆盖两条：
 * 1. 服务端痛快吐数据 → 判可用；
 * 2. 服务端只吐几 KB 就装死 → 即便 TCP/TLS 都正常，也必须判不可用。
 */
class DirectProbeTest {

    private var serverSocket: SSLServerSocket? = null

    @After
    fun tearDown() {
        runCatching { serverSocket?.close() }
        serverSocket = null
    }

    @Test
    fun probePassesWhenServerSendsData() = runBlocking {
        val identity = DirectCertificate.issue()
        val server = startProbeServer(identity, sendBytes = 48 * 1024, stallAfterMs = 0)
        serverSocket = server.socket

        val client = clientFor(identity, server.port)
        try {
            val bytesPerSecond = client.quickProbe(wantBytes = 32 * 1024, budgetMs = 3_000)
            assertNotNull("服务端正常吐数据时必须判可用", bytesPerSecond)
        } finally {
            client.shutdown()
        }
    }

    @Test
    fun probeRejectsLinkThatStallsMidBody() = runBlocking {
        val identity = DirectCertificate.issue()
        // 只发 4 KB，然后一直挂着 —— 就是现场那台 AP 的行为（连接活着、数据不动）
        val server = startProbeServer(identity, sendBytes = 4 * 1024, stallAfterMs = 5_000)
        serverSocket = server.socket

        val client = clientFor(identity, server.port)
        try {
            val bytesPerSecond = client.quickProbe(wantBytes = 32 * 1024, budgetMs = 800)
            assertNull("只收到 4 KB 就必须判不可用，否则快路径会一直走那条坏链路", bytesPerSecond)
        } finally {
            client.shutdown()
        }
    }

    private fun clientFor(identity: DirectCertificate.Identity, port: Int) = DirectClient(
        DirectClient.Endpoint(
            host = "127.0.0.1",
            port = port,
            fingerprint = identity.fingerprint,
            token = "probe-token",
        ),
    )

    private data class ProbeServer(val socket: SSLServerSocket, val port: Int)

    /**
     * 极简 HTTPS 服务端：对任何请求都回 200 + [sendBytes] 个字节，
     * 发完（或发够 [stallAfterMs] 对应的量）之后按参数决定是否装死。
     */
    private fun startProbeServer(
        identity: DirectCertificate.Identity,
        sendBytes: Int,
        stallAfterMs: Long,
    ): ProbeServer {
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

        val socket = serverContext.serverSocketFactory.createServerSocket(0) as SSLServerSocket

        thread(isDaemon = true) {
            while (!socket.isClosed) {
                val accepted = try {
                    socket.accept()
                } catch (error: Exception) {
                    break
                }

                thread(isDaemon = true) {
                    // 客户端读够就会主动断开，服务端这边的写会抛 broken pipe —— 正常现象
                    runCatching {
                        accepted.use { connection ->
                            val reader = connection.getInputStream().bufferedReader()
                            while (true) {
                                val line = reader.readLine() ?: break
                                if (line.isEmpty()) break
                            }

                            // 装死那一路要**故意多声明**一截：客户端读不满就会一直等，
                            // 直到自己的预算耗尽 —— 这才是那台 AP 的真实形态
                            val declared = if (stallAfterMs > 0) sendBytes + 32 * 1024 else sendBytes

                            connection.getOutputStream().apply {
                                write(
                                    (
                                        "HTTP/1.1 200 OK\r\n" +
                                            "Content-Type: application/octet-stream\r\n" +
                                            "Content-Length: $declared\r\n" +
                                            "Connection: close\r\n\r\n"
                                        ).toByteArray(Charsets.US_ASCII),
                                )
                                flush()

                                if (sendBytes > 0) {
                                    write(ByteArray(sendBytes) { 0x5A })
                                    flush()
                                }

                                if (stallAfterMs > 0) {
                                    Thread.sleep(stallAfterMs)
                                }
                            }
                        }
                    }
                }
            }
        }

        return ProbeServer(socket, socket.localPort)
    }
}
