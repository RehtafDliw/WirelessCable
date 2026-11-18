package com.wirelesscable.app

import com.wirelesscable.app.core.direct.DirectCertificate
import com.wirelesscable.app.core.direct.DirectClient
import com.wirelesscable.app.core.direct.DirectFileDto
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.security.KeyStore
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import kotlin.concurrent.thread

/**
 * 分段下载的端到端：本地起一个真的 HTTPS 服务端（支持 `Range`），
 * 用**与真机完全相同**的 [DirectClient] 拉一个文件，中途故意把连接掐断。
 *
 * 为什么值得在电脑上跑：现场 Wi-Fi 会在传了几十 KB 之后把连接掐掉，
 * 旧实现是"整文件一个请求 + 无限读超时"，于是永远停在某个字节数上。
 * 这条测试模拟的正是那一刻——客户端必须从断点续上，最后拼出一个字节不差的文件。
 */
class DirectChunkedDownloadTest {

    private var serverSocket: SSLServerSocket? = null

    @After
    fun tearDown() {
        runCatching { serverSocket?.close() }
        serverSocket = null
    }

    @Test
    fun interruptedDownloadResumesAndAssemblesExactFile() = runBlocking {
        val identity = DirectCertificate.issue()
        val payload = ByteArray(3 * 1024 * 1024).also { java.util.Random(20261116L).nextBytes(it) }

        val server = startRangeServer(identity, payload, cutFirstResponseAfter = 500 * 1024)
        serverSocket = server.socket

        val directory = File(
            System.getProperty("java.io.tmpdir"),
            "wc-direct-test-${System.nanoTime()}",
        ).apply { mkdirs() }

        val client = DirectClient(
            DirectClient.Endpoint(
                host = "127.0.0.1",
                port = server.port,
                fingerprint = identity.fingerprint,
                token = "test-token",
            ),
        )

        try {
            val result = client.download(
                DirectFileDto(id = "f0", name = "big.bin", sizeBytes = payload.size.toLong()),
                directory,
            )

            val file = result.getOrElse {
                throw AssertionError("下载应当续传成功，实际失败：${it.message}", it)
            }

            assertEquals("文件大小必须与源一致", payload.size.toLong(), file.length())
            assertArrayEquals("内容必须一字节不差", payload, file.readBytes())

            assertTrue(
                "第一次响应被掐断后必须重试，实际只发了 ${server.requests.get()} 次请求",
                server.requests.get() >= 2,
            )

            // 临时文件不能留在用户目录里
            assertEquals(
                "下载完成后不该留下 .wcpart 临时文件",
                0,
                directory.listFiles()?.count { it.name.startsWith(".wcpart-") } ?: 0,
            )
        } finally {
            client.shutdown()
            directory.deleteRecursively()
        }
    }

    /** 只实现 `GET + Range` 的最小 HTTPS 服务端，够 [DirectClient.download] 用。 */
    private class RangeServer(val socket: SSLServerSocket, val port: Int, val requests: AtomicInteger)

    private fun startRangeServer(
        identity: DirectCertificate.Identity,
        payload: ByteArray,
        cutFirstResponseAfter: Int,
    ): RangeServer {
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
        val requests = AtomicInteger()
        var cutOnce = true

        thread(isDaemon = true) {
            while (!socket.isClosed) {
                val accepted = try {
                    socket.accept()
                } catch (error: Exception) {
                    break
                }

                accepted.use { connection ->
                    val reader = connection.getInputStream().bufferedReader()
                    var range: String? = null

                    while (true) {
                        val line = reader.readLine() ?: break
                        if (line.isEmpty()) {
                            break
                        }
                        if (line.startsWith("Range:", ignoreCase = true)) {
                            range = line.substringAfter(':').trim()
                        }
                    }

                    requests.incrementAndGet()

                    val from = range
                        ?.substringAfter("bytes=", "")
                        ?.substringBefore('-')
                        ?.trim()
                        ?.toLongOrNull()
                        ?: 0L
                    val end = range
                        ?.substringAfter('-', "")
                        ?.trim()
                        ?.toLongOrNull()
                        ?: (payload.size - 1L)

                    val last = minOf(end, payload.size - 1L)
                    val length = (last - from + 1).toInt()

                    connection.getOutputStream().apply {
                        write(
                            (
                                "HTTP/1.1 206 Partial Content\r\n" +
                                    "Content-Type: application/octet-stream\r\n" +
                                    "Content-Length: $length\r\n" +
                                    "Content-Range: bytes $from-$last/${payload.size}\r\n" +
                                    "\r\n"
                                ).toByteArray(Charsets.US_ASCII),
                        )
                        flush()

                        if (cutOnce) {
                            // 模拟现场 Wi-Fi：发一半就把连接掐掉，客户端应当退到更小的块重试
                            cutOnce = false
                            val cut = minOf(cutFirstResponseAfter, length)
                            write(payload, from.toInt(), cut)
                            flush()
                            connection.close()
                            return@use
                        }

                        write(payload, from.toInt(), length)
                        flush()
                    }
                }
            }
        }

        return RangeServer(socket, socket.localPort, requests)
    }
}
