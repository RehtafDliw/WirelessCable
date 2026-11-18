package com.wirelesscable.app.core.direct

import android.content.Context
import android.util.Log
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.net.Socket
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.KeyStore
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket

/**
 * 直连会话的 HTTPS 服务端（出码方跑这个）。
 *
 * 设计取舍：
 * - **只做四件事**：状态、共享清单、下载共享文件、接收推来的文件。
 *   没有目录浏览、没有改名删除——一次性会话不需要文件管理器。
 * - **流式收发**：PUT 的正文边读边落盘，GET 的正文边读边写 socket，
 *   2 GiB 文件也不会把内存撑爆。
 * - **keep-alive**：4 MiB 一块的话 2 GiB 是 500 多次请求，
 *   每条连接都重新握手会慢到不可用，所以一个连接上连续处理请求。
 * - **一次性**：会话关闭后 socket 直接关掉，没有重连与续传。
 */
class DirectServer(
    /**
     * 只有「发共享文件」那条路要用它（`ContentResolver` 读 SAF）。
     * 传 null 只出现在单元测试里：收文件那条路完全用不到 Context，
     * 不需要为了跑一条 PUT 去伪造一个 Android 运行时。
     */
    private val context: Context?,
    private val session: DirectSession,
    private val listener: Listener,
) {

    interface Listener {
        /** 第一次收到合法请求。 */
        fun onConnected(remote: String)

        /** 收到一个完整的文件。 */
        fun onReceived(name: String, sizeBytes: Long)

        /** 正在往外发（进度）。 */
        fun onSending(name: String, sent: Long, total: Long)

        /**
         * 正在收对方推过来的文件（进度）。
         *
         * 以前这里复用的是 [onSending]，于是出码方界面在**收**文件时写"正在发送"（1.5.64）。
         */
        fun onReceiving(name: String, received: Long, total: Long)

        /** 出错了（会话不会因此结束，除非是致命错误）。 */
        fun onError(message: String)
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var serverSocket: SSLServerSocket? = null
    private var acceptJob: Job? = null

    /** 实际监听端口；未启动时 -1。 */
    val port: Int get() = serverSocket?.localPort ?: -1

    /**
     * 建好 TLS 服务并开始接受连接。返回实际端口。
     * 端口被占用时往后试 [DirectProtocol.PORT_PROBE_COUNT] 个。
     */
    fun start(preferredPort: Int = DirectProtocol.DEFAULT_PORT): Int {
        val sslContext = buildSslContext()

        var lastError: Exception? = null
        for (offset in 0 until DirectProtocol.PORT_PROBE_COUNT) {
            val candidate = preferredPort + offset
            try {
                val socket = sslContext.serverSocketFactory.createServerSocket(candidate) as SSLServerSocket
                // 一次性会话，不需要会话复用，关掉减少状态
                socket.useClientMode = false
                serverSocket = socket
                acceptJob = scope.launch { acceptLoop(socket) }
                Log.i(TAG, "直连服务已启动 port=$candidate sid=${session.sid}")
                return candidate
            } catch (error: Exception) {
                lastError = error
                Log.w(TAG, "端口 $candidate 不可用：${error.message}")
            }
        }

        throw IOException("没有可用端口：${lastError?.message}")
    }

    fun stop() {
        acceptJob?.cancel()
        acceptJob = null
        runCatching { serverSocket?.close() }
        serverSocket = null
        session.close()
        scope.cancel()
        Log.i(TAG, "直连服务已停止 sid=${session.sid}")
    }

    private fun buildSslContext(): SSLContext {
        val password = charArrayOf('w', 'i', 'r', 'e', 'l', 'e', 's', 's')
        val keyStore = KeyStore.getInstance("PKCS12").apply { load(null, null) }
        keyStore.setKeyEntry(
            "direct",
            session.identity.privateKey,
            password,
            arrayOf(session.identity.certificate),
        )

        val keyManagers = KeyManagerFactory
            .getInstance(KeyManagerFactory.getDefaultAlgorithm())
            .apply { init(keyStore, password) }
            .keyManagers

        return SSLContext.getInstance("TLS").apply {
            init(keyManagers, null, null)
        }
    }

    private suspend fun acceptLoop(socket: SSLServerSocket) = withContext(Dispatchers.IO) {
        while (isActive && !socket.isClosed) {
            val client = try {
                socket.accept()
            } catch (error: Exception) {
                if (isActive) {
                    Log.w(TAG, "accept 失败", error)
                }
                break
            }

            launch { serve(client) }
        }
    }

    private suspend fun serve(socket: Socket) = withContext(Dispatchers.IO) {
        val remote = socket.inetAddress?.hostAddress ?: "?"
        try {
            socket.soTimeout = SOCKET_TIMEOUT_MS
            // 每个连接留一行：下载卡住时要能分清"客户端根本没发新请求"和
            // "发了但没解析出来"，否则只能看到"请求列表停在某一段"（1.5.48）
            DirectLog.i("连接来自 $remote")
            val input = BufferedInputStream(socket.getInputStream())
            val output = BufferedOutputStream(socket.getOutputStream())

            while (true) {
                val request = readRequest(input) ?: break

                if (!isAuthorized(request)) {
                    // 必须自己 flush 再 break：循环末尾的 flush 在 break 之后永远走不到，
                    // BufferedOutputStream 里那点字节就跟着 socket 一起没了。
                    // 客户端只会看到"连接被关闭"（OkHttp: unexpected end of stream），
                    // 看不出是口令不对——扫码失败最难查的一类（1.5.10 实测）。
                    writeJson(output, 401, WcJson.instance.encodeToString(DirectError("unauthorized")))
                    output.flush()
                    DirectLog.w("拒绝未授权请求 ${request.method} ${request.path}")
                    break
                }

                val firstTime = session.state == DirectSession.State.Waiting
                session.markConnected()
                if (firstTime) {
                    listener.onConnected(remote)
                }

                // 每个请求都留一行日志：直连失败时只有"unexpected end of stream"
                // 这种话根本没法定位，至少要知道请求有没有到、到的是哪条
                DirectLog.i("请求 ${request.method} ${request.path}")

                try {
                    route(request, input, output)
                } catch (abort: AbortRequest) {
                    DirectLog.i("响应已发，关闭连接：${abort.message}")
                    break
                } catch (broken: BrokenStream) {
                    // 头已经发出去了，补不了错误响应；断开是唯一能让对面立刻失败的办法
                    DirectLog.w("响应中断，关闭连接：${broken.message}")
                    break
                } catch (error: Throwable) {
                    // 必须关连接。处理失败时请求体通常还没读完（比如上传刚开始就出错），
                    // 这条连接上剩下的全是文件二进制；继续 keep-alive 拿它当下一轮请求行读，
                    // 服务端会卡在 readLine，客户端还在灌数据 —— 双方一起僵住（1.5.16）。
                    DirectLog.w("处理请求失败 ${request.method} ${request.path}", error)

                    // 发文件途中失败（对方断了 / 链路死了）：界面要立刻停止"正在发送"。
                    // 否则出码方一直显示"正在传"，而对面早就失败退出了（1.5.52 现场）。
                    if (request.method == "GET" &&
                        request.path.startsWith("${DirectProtocol.PATH_SHARED}/")
                    ) {
                        listener.onError("对方已断开，发送中断")
                    }

                    if (request.path == DirectProtocol.PATH_STATUS ||
                        request.path == DirectProtocol.PATH_SHARED
                    ) {
                        runCatching {
                            writeJson(
                                output,
                                500,
                                WcJson.instance.encodeToString(
                                    DirectError("internal: ${error.message ?: error.javaClass.simpleName}"),
                                ),
                            )
                            output.flush()
                        }
                    }

                    break
                }

                output.flush()

                if (request.header("connection")?.equals("close", ignoreCase = true) == true) {
                    break
                }

                if (session.state == DirectSession.State.Closed) {
                    break
                }
            }
        } catch (error: Exception) {
            if (session.state != DirectSession.State.Closed) {
                Log.w(TAG, "连接中断：${error.message}")
            }
        } finally {
            runCatching { socket.close() }
        }
    }

    private fun isAuthorized(request: Request): Boolean {
        val header = request.header(DirectProtocol.AUTH_HEADER.lowercase()) ?: return false
        if (!header.startsWith(DirectProtocol.BEARER_PREFIX, ignoreCase = true)) {
            return false
        }

        val presented = header.substring(DirectProtocol.BEARER_PREFIX.length).trim()
        // 定长比较，避免按字符提前返回
        if (presented.length != session.token.length) {
            // 长度不同直接失败，但要把两边的形状留下来：
            // 没有这行日志，扫码失败只能看到"会话失效"，分不清是二维码过期、
            // 扫的是别的会话，还是口令在传输里被改了（1.5.11）
            Log.w(TAG, "鉴权长度不符 presented=${presented.take(6)}(${presented.length}) " +
                "expected=${session.token.take(6)}(${session.token.length}) sid=${session.sid}")
            return false
        }

        var diff = 0
        for (index in presented.indices) {
            diff = diff or (presented[index].code xor session.token[index].code)
        }

        if (diff != 0) {
            Log.w(TAG, "鉴权内容不符 presented=${presented.take(6)} expected=${session.token.take(6)} " +
                "sid=${session.sid}")
        }

        return diff == 0
    }

    private fun route(request: Request, input: InputStream, output: OutputStream) {
        val path = request.path

        when {
            request.method == "GET" && path == DirectProtocol.PATH_STATUS -> {
                val status = DirectStatus(
                    state = session.state.name.lowercase(),
                    sid = session.sid,
                    deviceName = session.deviceName,
                    shared = session.sharedFiles.size,
                    inbox = session.receivedCount(),
                    expiresAtUnixMs = session.lastActivityMs + DirectProtocol.SESSION_IDLE_TIMEOUT_MS,
                )
                writeJson(output, 200, WcJson.instance.encodeToString(status))
            }

            request.method == "GET" && path == DirectProtocol.PATH_SHARED -> {
                val files = session.sharedFiles.map {
                    DirectFileDto(
                        id = it.id,
                        name = it.name,
                        sizeBytes = it.sizeBytes,
                        modifiedUnixMs = 0,
                        group = it.group,
                        kind = it.kind,
                    )
                }
                writeJson(output, 200, WcJson.instance.encodeToString(files))
            }

            request.method == "GET" && path == DirectProtocol.PATH_PROBE -> {
                sendProbe(request, output)
            }

            request.method == "GET" && path.startsWith("${DirectProtocol.PATH_SHARED}/") -> {
                val id = path.removePrefix("${DirectProtocol.PATH_SHARED}/")
                sendSharedFile(id, request, output)
            }

            request.method == "PUT" && path.startsWith("${DirectProtocol.PATH_INBOX}/") -> {
                val raw = path.removePrefix("${DirectProtocol.PATH_INBOX}/")
                receiveFile(raw, request, input, output)
            }

            else -> writeJson(output, 404, WcJson.instance.encodeToString(DirectError("not_found")))
        }
    }

    /**
     * 响应已经发了一半（头写了、正文没写满）。这时不能再补一个 500，
     * 只能立刻断开连接，让对面从"等剩余字节"里解脱出来（1.5.13）。
     */
    private class BrokenStream(message: String) : IOException(message)

    /**
     * 已经回过错误响应、但请求体还没读完 —— 这条连接不能再用。
     *
     * 上传被拒（太大 / 范围不对 / 收件目录建不出来）时，剩下的正文还在路上；
     * 继续 keep-alive 会把文件数据当下一轮请求行读，双方一起僵住（K101 同类）。
     */
    private class AbortRequest(message: String) : IOException(message)

    /**
     * 链路测速：发 N 个零字节。不碰磁盘、不碰 SA F，纯测这条 Wi-Fi 能跑多快。
     *
     * 为什么需要它：两机直连慢的时候，界面只能显示"正在传"，用户没法知道
     * 是文件太大还是这条路根本不通。有了这个数字，一眼就能判断该换网络还是继续等。
     */
    /**
     * 上报发送进度：取"已完成 + 本段已发"，并**只增不减**。
     *
     * 三条流并行时，各段自己的 `起点+已发` 会在 0/256K/512K 之间来回跳；
     * 接收端按汇总字节数显示，两边就对不上。这里用一个高水位把它压成单调序列。
     */
    private fun reportSent(file: DirectSession.SharedFile, value: Long) {
        val capped = minOf(value, file.sizeBytes)
        var previous = file.reportedBytes.get()

        while (capped > previous) {
            if (file.reportedBytes.compareAndSet(previous, capped)) {
                listener.onSending(file.name, capped, file.sizeBytes)
                return
            }
            previous = file.reportedBytes.get()
        }
    }

    private fun sendProbe(request: Request, output: OutputStream) {
        val bytes = (request.query["bytes"]?.toLongOrNull() ?: 1_048_576L)
            .coerceIn(0L, 8L * 1024 * 1024)

        writeHead(output, 200, "OK", "application/octet-stream", bytes)
        output.flush()

        val zeros = ByteArray(IO_BUFFER)
        var sent = 0L
        while (sent < bytes) {
            val count = minOf(zeros.size.toLong(), bytes - sent).toInt()
            output.write(zeros, 0, count)
            sent += count
        }
        output.flush()
        session.touch()
        DirectLog.i("测速发出 $sent 字节")
    }

    /**
     * 发一个共享文件的某一段。
     *
     * 支持 `Range: bytes=start-end`，客户端按段拉、按段续。整文件一个请求在
     * 现场 Wi-Fi 上太脆：连接一被掐断，前面传的全白费，界面还永远挂着（1.5.19）。
     */
    private fun sendSharedFile(id: String, request: Request, output: OutputStream) {
        val file = session.sharedFiles.firstOrNull { it.id == id }
        if (file == null) {
            writeJson(output, 404, WcJson.instance.encodeToString(DirectError("not_found")))
            return
        }

        // 这里**不再**二次打开 SAF URI 去问 statSize。
        // 1.5.13 加的那次 openFileDescriptor 是回归的源头：部分 provider 对同一 URI
        // 二次打开会卡住（或让随后 openInputStream 拿到失效的 fd）。大小在登记时
        // 已经用 statSize 取过，真读不够有 BrokenStream 兜底（1.5.17）。
        val total = file.sizeBytes
        val range = DirectRange.parse(request.header("range"), total)
        if (range == null) {
            writeHead(
                output,
                status = 416,
                reason = "Range Not Satisfiable",
                contentType = "application/octet-stream",
                length = 0,
                extra = mapOf("Content-Range" to "bytes */$total", "Accept-Ranges" to "bytes"),
            )
            output.flush()
            return
        }

        val from = range.first
        val length = range.second
        DirectLog.i("发送开始 id=$id name=${file.name} 段=$from+$length/$total")

        val stream = try {
            context?.contentResolver?.openInputStream(file.uri)
        } catch (error: Exception) {
            DirectLog.w("打不开共享文件 ${file.name}", error)
            null
        }

        if (stream == null) {
            writeJson(output, 500, WcJson.instance.encodeToString(DirectError("open_failed")))
            return
        }

        stream.use {
            if (from > 0) {
                skipFully(it, from)
            }

            writeHead(
                output,
                status = 206,
                reason = "Partial Content",
                contentType = "application/octet-stream",
                length = length,
                extra = buildMap {
                    if (length > 0) {
                        put("Content-Range", "bytes $from-${from + length - 1}/$total")
                    }
                    put("Accept-Ranges", "bytes")
                    put("x-file-name", encodeName(file.name))
                },
            )
            // 头必须立刻出门：客户端在 execute() 里等响应头，头卡在缓冲区时它连 body 都不会读
            output.flush()

            val buffer = ByteArray(IO_BUFFER)
            var sent = 0L
            var lastReport = 0L

            while (sent < length) {
                val want = minOf(buffer.size.toLong(), length - sent).toInt()
                val readStartedAt = android.os.SystemClock.elapsedRealtime()
                val read = it.read(buffer, 0, want)
                val readCost = android.os.SystemClock.elapsedRealtime() - readStartedAt
                if (readCost >= 300) {
                    DirectLog.w("读源文件慢 ${readCost}ms 本次 ${if (read < 0) 0 else read}B 段内 ${sent}B")
                }
                if (read < 0) {
                    break
                }

                output.write(buffer, 0, read)
                sent += read

                val now = android.os.SystemClock.elapsedRealtime()
                if (now - lastReport >= PROGRESS_INTERVAL_MS) {
                    lastReport = now
                    // 传输期间也要续命：否则 15 秒没有"请求"就会被判定对方离线
                    session.touch()
                    // 报"已完成总量 + 本段已发"，不是本段的绝对偏移：
                    // 3 条流并行时后者会在 0/256K/512K 之间跳，和接收端完全对不上（1.5.22）
                    reportSent(file, file.completedBytes.get() + sent)
                }
            }

            if (sent != length) {
                // 少于这一段声明的长度：头已经发出去了，补不了错误响应，
                // 只能断开让客户端立刻失败（否则它在等永远不会来的字节）
                DirectLog.w("共享文件读不全 ${file.name} 段 $from+$length 实发 $sent")
                throw BrokenStream("共享文件读不全：$sent / $length")
            }

            // 这一段落袋：计入"已完成"，进度只往前走
            file.completedBytes.addAndGet(length)
            reportSent(file, file.completedBytes.get())

            session.touch()
            DirectLog.i("已发送 ${file.name} 段 $from+$sent/$total")
        }
    }

    /**
     * 收一段上传。`PUT /direct/inbox/{name}?offset=N`，`X-Total-Bytes` 给整文件大小。
     *
     * 分段的原因和下载一样：整文件一个请求在现场 Wi-Fi 上太脆。现在写到会话内的
     * 临时文件（`.wcpart-<hash>`），最后一段到齐才改名成正式文件——断了重来只丢当前段，
     * 用户也不会在收件目录里看到一堆半截文件。
     */
    private fun receiveFile(
        rawName: String,
        request: Request,
        input: InputStream,
        output: OutputStream,
    ) {
        val name = decodeName(rawName)
        if (name.isEmpty() || name.contains('/') || name.contains('\\') || name.contains("..")) {
            writeJson(output, 400, WcJson.instance.encodeToString(DirectError("invalid_name")))
            return
        }

        val length = request.contentLength()
        if (length < 0 || length > DirectProtocol.MAX_FILE_BYTES) {
            writeJson(output, 413, WcJson.instance.encodeToString(DirectError("too_large")))
            output.flush()
            throw AbortRequest("上传长度不合法：$length")
        }

        val offset = request.query[DirectProtocol.QUERY_OFFSET]?.toLongOrNull() ?: 0L
        val total = request.header(DirectProtocol.HEADER_TOTAL_BYTES)?.toLongOrNull()
            ?: (offset + length)

        // total 允许为 0（空文件），但不能为负，也不能让这一段超出声明的大小
        if (total < 0 || total > DirectProtocol.MAX_FILE_BYTES || offset < 0 || offset + length > total) {
            writeJson(output, 400, WcJson.instance.encodeToString(DirectError("bad_range")))
            output.flush()
            throw AbortRequest("上传范围不合法：offset=$offset len=$length total=$total")
        }

        if (!session.inboxDirectory.exists() && !session.inboxDirectory.mkdirs()) {
            DirectLog.w("收件目录建不出来：${session.inboxDirectory.absolutePath}")
            writeJson(output, 500, WcJson.instance.encodeToString(DirectError("inbox_unavailable")))
            output.flush()
            throw AbortRequest("收件目录不可用")
        }

        // 临时文件按名字取哈希：同名重试落在同一个文件上才能续，也不会被奇怪文件名搞坏
        val temp = File(session.inboxDirectory, ".wcpart-" + Integer.toHexString(name.hashCode()))

        if (offset == 0L) {
            // 上一轮会话中断留下的半截文件：超过一小时就清掉，别越堆越多
            val cutoff = System.currentTimeMillis() - 60 * 60 * 1000L
            session.inboxDirectory.listFiles()?.forEach { stale ->
                if (stale.name.startsWith(".wcpart-") &&
                    stale.absolutePath != temp.absolutePath &&
                    stale.lastModified() < cutoff
                ) {
                    runCatching { stale.delete() }
                }
            }
        }
        val buffer = ByteArray(IO_BUFFER)
        var remaining = length
        var received = 0L
        var lastReport = 0L

        DirectLog.i("接收 $name 段 offset=$offset len=$length total=$total")

        try {
            RandomAccessFile(temp, "rw").use { raf ->
                raf.seek(offset)

                while (remaining > 0) {
                    val want = minOf(buffer.size.toLong(), remaining).toInt()
                    val read = input.read(buffer, 0, want)
                    if (read < 0) {
                        throw IOException("连接提前结束：段内还差 $remaining 字节")
                    }

                    raf.write(buffer, 0, read)
                    remaining -= read
                    received += read

                    val now = android.os.SystemClock.elapsedRealtime()
                    // 最后一块必须报：整段传完之后进度条才走到 100%，
                    // 否则它停在最后一个 200ms 采样点上（1.5.64 复查时发现）
                    if (now - lastReport >= PROGRESS_INTERVAL_MS || remaining <= 0) {
                        lastReport = now
                        session.touch()
                        listener.onReceiving(name, offset + received, total)
                    }
                }
            }
        } catch (error: Exception) {
            // 不写 500：请求体还没读完，写了也只会让调用方误以为这条连接还能用。
            // 抛出去由 serve() 关连接；临时文件留着，下一段从断点续。
            DirectLog.w("接收段失败 $name offset=$offset 已收 $received：${error.message}")
            listener.onError("接收「$name」中断")
            throw error
        }

        val doneAt = offset + received
        session.touch()

        if (doneAt < total) {
            DirectLog.i("接收进度 $name $doneAt/$total")
            writeJson(
                output,
                200,
                WcJson.instance.encodeToString(
                    mapOf("name" to name, "size" to total.toString(), "received" to doneAt.toString()),
                ),
            )
            return
        }

        // 最后一段到齐：改名为正式文件（重名不覆盖）
        val target = uniqueTarget(session.inboxDirectory, name)
        if (!temp.renameTo(target)) {
            target.delete()
            if (!temp.renameTo(target)) {
                throw IOException("临时文件改名失败：${temp.name}")
            }
        }

        DirectLog.i("接收完成 ${target.name} ${target.length()}B")
        // 传应用时对方会带上分组与类型；老客户端不带，按普通文件处理
        val appGroup = request.header(DirectProtocol.HEADER_APK_GROUP)?.takeIf { it.isNotBlank() }
        val appKind = request.header(DirectProtocol.HEADER_APK_KIND)?.toIntOrNull()
            ?: DirectProtocol.KIND_FILE

        session.addReceived(
            DirectSession.ReceivedFile(
                name = target.name,
                sizeBytes = total,
                atMs = System.currentTimeMillis(),
                path = target.absolutePath,
                group = appGroup,
                kind = appKind,
            ),
        )
        listener.onReceived(target.name, total)

        writeJson(
            output,
            201,
            WcJson.instance.encodeToString(
                mapOf("name" to target.name, "size" to total.toString(), "received" to total.toString()),
            ),
        )
    }

    /** 跳到指定偏移。SAF 的流 `skip` 有时返回 0，退化成逐字节读。 */
    private fun skipFully(input: InputStream, count: Long) {
        var left = count
        while (left > 0) {
            val skipped = input.skip(left)
            if (skipped > 0) {
                left -= skipped
                continue
            }

            if (input.read() < 0) {
                throw IOException("源文件比声明的短：还差 $left 字节")
            }
            left--
        }
    }

    /** 重名不覆盖：`a.txt` → `a (1).txt`。会话里反复推同一个文件名很常见。 */
    private fun uniqueTarget(directory: File, name: String): File {
        val candidate = File(directory, name)
        if (!candidate.exists()) {
            return candidate
        }

        val base = name.substringBeforeLast('.', name)
        val extension = if (name.contains('.')) "." + name.substringAfterLast('.') else ""

        for (index in 1..999) {
            val next = File(directory, "$base ($index)$extension")
            if (!next.exists()) {
                return next
            }
        }

        return File(directory, "$base-${System.currentTimeMillis()}$extension")
    }

    // ------------------------------------------------------------ HTTP 细节

    private data class Request(
        val method: String,
        val path: String,
        val query: Map<String, String>,
        val headers: Map<String, String>,
    ) {
        fun header(name: String): String? = headers[name.lowercase()]

        fun contentLength(): Long = header("content-length")?.toLongOrNull() ?: -1
    }

    private fun readRequest(input: InputStream): Request? {
        val requestLine = readLine(input) ?: return null
        if (requestLine.isBlank()) {
            return readRequest(input)
        }

        val parts = requestLine.split(' ')
        if (parts.size < 2) {
            return null
        }

        val headers = mutableMapOf<String, String>()
        while (true) {
            val line = readLine(input) ?: return null
            if (line.isEmpty()) {
                break
            }

            val index = line.indexOf(':')
            if (index > 0) {
                headers[line.substring(0, index).trim().lowercase()] =
                    line.substring(index + 1).trim()
            }
        }

        val target = parts[1]
        val path = target.substringBefore('?')
        val query = mutableMapOf<String, String>()
        val rawQuery = target.substringAfter('?', "")
        if (rawQuery.isNotEmpty()) {
            rawQuery.split('&').forEach { pair ->
                val key = pair.substringBefore('=')
                if (key.isNotEmpty()) {
                    query[key] = decodeName(pair.substringAfter('=', ""))
                }
            }
        }

        return Request(parts[0].uppercase(), path, query, headers)
    }

    private fun readLine(input: InputStream): String? {
        val buffer = StringBuilder(128)
        while (true) {
            val byte = input.read()
            if (byte < 0) {
                return if (buffer.isEmpty()) null else buffer.toString()
            }

            if (byte == '\n'.code) {
                if (buffer.isNotEmpty() && buffer.last() == '\r') {
                    buffer.deleteCharAt(buffer.length - 1)
                }
                return buffer.toString()
            }

            if (buffer.length > MAX_LINE) {
                return null
            }

            buffer.append(byte.toChar())
        }
    }

    private fun writeJson(output: OutputStream, status: Int, body: String) {
        val json = body.toByteArray(Charsets.UTF_8)
        writeHead(
            output = output,
            status = status,
            reason = reasonOf(status),
            contentType = "application/json; charset=utf-8",
            length = json.size.toLong(),
        )
        output.write(json)
    }

    private fun writeHead(
        output: OutputStream,
        status: Int,
        reason: String,
        contentType: String,
        length: Long,
        extra: Map<String, String> = emptyMap(),
    ) {
        val builder = StringBuilder(256)
        builder.append("HTTP/1.1 ").append(status).append(' ').append(reason).append("\r\n")
        builder.append("Content-Type: ").append(contentType).append("\r\n")
        builder.append("Content-Length: ").append(length).append("\r\n")
        builder.append("Connection: keep-alive\r\n")
        builder.append("Cache-Control: no-store\r\n")
        extra.forEach { (key, value) -> builder.append(key).append(": ").append(value).append("\r\n") }
        builder.append("\r\n")

        output.write(builder.toString().toByteArray(Charsets.US_ASCII))
    }

    private fun reasonOf(status: Int): String = when (status) {
        200 -> "OK"
        201 -> "Created"
        400 -> "Bad Request"
        401 -> "Unauthorized"
        404 -> "Not Found"
        413 -> "Payload Too Large"
        else -> "Error"
    }

    private fun encodeName(name: String): String = URLEncoder.encode(name, "UTF-8")

    private fun decodeName(name: String): String =
        runCatching { URLDecoder.decode(name, "UTF-8") }.getOrDefault(name)

    companion object {
        private const val TAG = "WcDirect"

        private const val IO_BUFFER = 64 * 1024

        /** 进度回调的节流步长：每 256 KiB 报一次，UI 上足够细。 */
        private const val PROGRESS_STEP = 256 * 1024L

    /** 进度回调的最小间隔（毫秒）。 */
    private const val PROGRESS_INTERVAL_MS = 200L

        /** 一次连接空闲太久就断掉，避免僵尸连接占着会话。 */
        // 一次请求最长可以是很久：1 GB 在慢一点的 Wi-Fi 上要几分钟，
    // 中途只要有一个 60 秒的空档就会被误判成"连接死掉"并删掉半截文件。
    // 传输中是"每次 read 重新计时"，所以短一点不会误杀正在流动的大文件；
    // 一旦对端真的不动了，该失败就失败，而不是让用户对着进度条等五分钟。
    //
    // 30 秒（1.5.48）：客户端已经改成读超时 8 秒 + 失败即换新连接，
    // 服务端这边的空闲连接也该早点回收，别让对面从池里挑到一条"看着还活着"的死连接。
    private const val SOCKET_TIMEOUT_MS = 30_000

        private const val MAX_LINE = 8 * 1024
    }
}
