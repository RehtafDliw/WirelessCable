package com.wirelesscable.app

import com.wirelesscable.app.core.direct.DirectCertificate
import com.wirelesscable.app.core.direct.DirectLog
import com.wirelesscable.app.core.direct.DirectProtocol
import com.wirelesscable.app.core.direct.DirectServer
import com.wirelesscable.app.core.direct.DirectSession
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.net.HttpURLConnection
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/**
 * 出码方（服务端）收文件这条路的端到端：真起一个 `DirectServer`，用 TLS 往里 PUT 两段。
 *
 * 为什么值得在电脑上跑：
 * - 这条链路在两台真机之间跑通了，但**一行自动化测试都没有**；
 * 服务端的分段落盘、断点续、改名、回调全靠人眼盯日志。
 * - 上一版还有个方向 bug：收文件时也调 `onSending`，出码方界面写"正在发送"（1.5.64）。
 * 这里断言"收文件只触发 onReceiving、绝不触发 onSending"，把它钉住。
 */
class DirectServerInboxTest {

    private var server: DirectServer? = null
    private val sent = mutableListOf<String>()
    private val receiving = mutableListOf<Triple<String, Long, Long>>()
    private val received = mutableListOf<Pair<String, Long>>()
    private val errors = mutableListOf<String>()
    private var connected = 0

    @After
    fun tearDown() {
        server?.stop()
        server = null
    }

    @Test
    fun putInTwoSegmentsLandsExactFileAndReportsAsReceiving() {
        val identity = DirectCertificate.issue()
        val inbox = File(System.getProperty("java.io.tmpdir"), "wc-inbox-${System.nanoTime()}")

        val session = DirectSession(
            sid = "test-sid",
            token = "test-token",
            deviceName = "测试机",
            identity = identity,
            sharedFiles = emptyList(),
            inboxDirectory = inbox,
        )

        val listener = object : DirectServer.Listener {
            override fun onConnected(remote: String) {
                connected += 1
            }

            override fun onReceived(name: String, sizeBytes: Long) {
                received += name to sizeBytes
            }

            override fun onSending(name: String, sent: Long, total: Long) {
                this@DirectServerInboxTest.sent += name
            }

            override fun onReceiving(name: String, receivedBytes: Long, total: Long) {
                receiving += Triple(name, receivedBytes, total)
            }

            override fun onError(message: String) {
                // 不能在这里抛：服务端在自己的线程里 catch(Throwable)，抛了也只会被吞掉
                errors += message
            }
        }

        val instance = DirectServer(context = null, session = session, listener = listener)
        server = instance
        val port = instance.start(47210)

        val payload = ByteArray(192 * 1024).also { java.util.Random(20261118L).nextBytes(it) }
        val half = payload.size / 2

        val first = put(port, session.token, "test.bin", offset = 0, body = payload.copyOfRange(0, half), total = payload.size)
        assertTrue("第一段应当成功，实际：$first", first.startsWith("HTTP/1.1 200"))

        val second = put(port, session.token, "test.bin", offset = half, body = payload.copyOfRange(half, payload.size), total = payload.size)
        assertTrue("第二段应当成功，实际：$second", second.startsWith("HTTP/1.1 201"))

        val landed = File(inbox, "test.bin")
        assertTrue("文件必须真的落盘", landed.exists())
        assertArrayEquals("两段拼起来必须和源一致", payload, landed.readBytes())

        assertEquals("只该有一条「收到完整文件」回调", 1, received.size)
        assertEquals("test.bin", received.first().first)
        assertEquals(payload.size.toLong(), received.first().second)

        assertTrue("收文件必须报「正在接收」", receiving.isNotEmpty())
        assertTrue(
            "最后一条进度必须走到总字节数，进度条才不会卡在半路：$receiving",
            receiving.last().second == payload.size.toLong(),
        )

        // 回归点：1.5.64 之前这里会把「收」当成「发」，界面写"正在发送"
        assertTrue("收文件不该触发 onSending：$sent", sent.isEmpty())

        assertEquals("对方第一次请求只该触发一次 onConnected", 1, connected)
        assertTrue("不该有错误回调：$errors", errors.isEmpty())

        DirectLog.i("测试结束 inbox=${inbox.absolutePath}")
    }

    /** 一段 PUT。返回响应头（只关心状态行）。 */
    private fun put(
        port: Int,
        token: String,
        name: String,
        offset: Int,
        body: ByteArray,
        total: Int,
    ): String {
        val socket = trustAllContext().socketFactory.createSocket("127.0.0.1", port) as javax.net.ssl.SSLSocket
        val header = buildString {
            append("PUT ${DirectProtocol.PATH_INBOX}/$name?offset=$offset HTTP/1.1\r\n")
            append("Host: 127.0.0.1\r\n")
            append("${DirectProtocol.AUTH_HEADER}: ${DirectProtocol.BEARER_PREFIX}$token\r\n")
            append("${DirectProtocol.HEADER_TOTAL_BYTES}: $total\r\n")
            append("Content-Length: ${body.size}\r\n")
            append("Connection: close\r\n")
            append("\r\n")
        }

        return socket.use { connection ->
            connection.getOutputStream().apply {
                write(header.toByteArray(Charsets.US_ASCII))
                write(body)
                flush()
            }

            connection.getInputStream().readBytes()
                .toString(Charsets.UTF_8)
                .lineSequence()
                .firstOrNull()
                .orEmpty()
        }
    }

    /** 测试客户端只认"能连上"，证书 pinning 由 DirectClient 负责，这里不重复测。 */
    private fun trustAllContext(): SSLContext = SSLContext.getInstance("TLS").apply {
        init(
            null,
            arrayOf<TrustManager>(
                object : X509TrustManager {
                    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
                    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = Unit
                    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
                },
            ),
            null,
        )
    }
}
