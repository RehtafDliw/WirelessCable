package com.wirelesscable.app.core.direct

import android.content.Context
import android.net.Uri
import com.wirelesscable.app.core.network.PinnedTrustManager
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import okhttp3.MediaType
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ConnectionPool
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.ResponseBody
import okio.BufferedSink
import okio.Buffer
import okio.ForwardingSink
import okio.buffer
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.net.URLEncoder
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager

/**
 * 直连会话的客户端（扫码的一方跑这个）。
 *
 * 与 [com.wirelesscable.app.core.network.FileApi] 的区别：那边连的是电脑，
 * 用的是长期令牌与 mDNS 地址；这边连的是一次性会话，地址/指纹/口令全在二维码里，
 * 证书用 pinning 校验，会话一断就再也连不上。
 */
class DirectClient private constructor(
    /** 只有上传要用它（读 SAF 流）；下载路径完全不碰 Context，所以测试里可以是 null。 */
    private val context: Context?,
    private val endpoint: Endpoint,
    /** 连对方热点时用：`Network.socketFactory`，让这条连接只走那一张网卡。 */
    private val socketFactory: javax.net.SocketFactory?,
    @Suppress("UNUSED_PARAMETER") marker: Unit,
) {

    constructor(
        context: Context,
        endpoint: Endpoint,
        socketFactory: javax.net.SocketFactory? = null,
    ) : this(context, endpoint, socketFactory, Unit)

    /** 仅单元测试：起个本地 HTTPS 服务端就能验证分段下载，不需要 Android 运行时。 */
    internal constructor(endpoint: Endpoint) : this(null, endpoint, null, Unit)

    data class Endpoint(
        val host: String,
        val port: Int,
        /** 服务端自签证书的 SHA-256 指纹（小写 hex）。 */
        val fingerprint: String,
        /** 会话口令。 */
        val token: String,
    ) {
        val baseUrl: String get() = "https://$host:$port"
    }

    private val client: OkHttpClient = buildClient()

    private fun buildClient(): OkHttpClient {
        val trustManager = PinnedTrustManager(setOf(endpoint.fingerprint.lowercase()))
        val sslContext = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(trustManager), SecureRandom())
        }

        val builder = OkHttpClient.Builder()
            .sslSocketFactory(sslContext.socketFactory, trustManager)

        // 指定了就只走那张网卡：连对方热点时，AP 地址在默认网络里是没有路由的，
        // 不绑的话请求会从原 Wi-Fi 发出去、直接超时（1.5.20）。
        socketFactory?.let { builder.socketFactory(it) }

        return builder
            // 自签证书没有 SAN，主机名就是 IP；靠指纹 pinning 保证对面身份
            .hostnameVerifier { _, _ -> true }
            .connectTimeout(8, TimeUnit.SECONDS)
            // 必须有限：以前这里是 0（无限），链路一断就读不到也报不出，
            // 界面永远停在"已收到 40 KB"，用户只能杀进程（1.5.19 真机）。
            // 现在按"多久没有新字节"算，数据在流动就不会被误杀。
            //
            // 8 秒而不是 15 秒（1.5.48 现场）：办公 AP 上两机互访会在传完几段后
            // 突然不再 ACK，15 秒的等待让用户以为"传不动"就重连了。缩短后失败得更早，
            // 失败就换新连接重试（见 fetchRange 里的 evictAll），比等一条死连接强。
            .readTimeout(8, TimeUnit.SECONDS)
            .writeTimeout(8, TimeUnit.SECONDS)
            .callTimeout(0, TimeUnit.MILLISECONDS)
            // 复用连接时对面可能已经关了（我们的服务端是手写 keep-alive）。
            // 关掉重试会直接报"握手失败"，而实际上换一条连接就通了。
            .retryOnConnectionFailure(true)
            .build()
    }

    /** 会话还活着吗、对面是谁。连上后第一件事就是拉它。 */
    suspend fun status(): Result<DirectStatus> = executeJson(
        authorized(
            Request.Builder()
                .url(endpoint.baseUrl + DirectProtocol.PATH_STATUS)
                .get(),
        ).build(),
    )

    /** 对面共享出来的文件清单。 */
    suspend fun shared(): Result<List<DirectFileDto>> = executeJson(
        authorized(
            Request.Builder()
                .url(endpoint.baseUrl + DirectProtocol.PATH_SHARED)
                .get(),
        ).build(),
    )

    /**
     * 拉一个共享文件到 [directory]。
     *
     * 并行分段：3 条连接各拉不同区间。现场 Wi-Fi 的单条 TCP 流常被 RTT/抖动卡在几十 KB/s，
     * 而带宽其实还有富余——多条流各占一个窗口，实测能成倍往上走（1.5.21）。
     *
     * 每条流按 `Range` 请求一段，断了只丢当前段、从断点续；块大小按实测速率自适应
     * （目标一块 5 秒），所以慢链路不会因为块太大而踩到读超时。
     */
    suspend fun download(
        item: DirectFileDto,
        directory: File,
        onProgress: (sent: Long, total: Long) -> Unit = { _, _ -> },
    ): Result<File> = withContext(Dispatchers.IO) {
        var temp: File? = null

        try {
            directory.mkdirs()
            cleanStaleParts(directory)

            val target = uniqueTarget(directory, item.name)
            temp = File(directory, ".wcpart-" + target.name)

            val total = item.sizeBytes
            DirectLog.i("下载开始 name=${item.name} 大小=$total 并行=$DOWNLOAD_WORKERS")

            val startedAt = android.os.SystemClock.elapsedRealtime()
            val mutex = Mutex()
            val done = java.util.concurrent.atomic.AtomicLong(0L)
            val reportedAt = java.util.concurrent.atomic.AtomicLong(0L)

            var nextOffset = 0L
            var chunk = DirectProtocol.CHUNK_INITIAL
            var fatal: Exception? = null

            RandomAccessFile(temp, "rw").use { raf ->
                raf.setLength(0)
                val channel = raf.channel

                suspend fun runWorker() {
                    while (true) {
                        val plan = mutex.withLock {
                            if (fatal != null || nextOffset >= total) {
                                null
                            } else {
                                val offset = nextOffset
                                val want = minOf(chunk, total - offset)
                                nextOffset += want
                                offset to want
                            }
                        } ?: return

                        val (offset, want) = plan
                        var attempt = 0
                        var moved = false

                        while (!moved && attempt < DirectProtocol.CHUNK_MAX_ATTEMPTS) {
                            attempt++
                            val attemptStartedAt = android.os.SystemClock.elapsedRealtime()

                            try {
                                val got = fetchRange(item, channel, offset, want) { delta ->
                                    done.addAndGet(delta)
                                    val now = android.os.SystemClock.elapsedRealtime()
                                    val last = reportedAt.get()
                                    if (now - last >= PROGRESS_INTERVAL_MS && reportedAt.compareAndSet(last, now)) {
                                        onProgress(done.get(), total)
                                    }
                                }

                                if (got <= 0L) {
                                    throw IOException("对方没有返回数据")
                                }

                                moved = true

                                // 按实测速率定下一块：一块大约走 5 秒。
                                //
                                // 每次最多翻倍（1.5.48）：快链路上 3 条流 20 ms 就拉完 256 KB，
                                // 按公式会直接跳到 4 MiB。段越大，卡住时要等的时间越长，
                                // 而且一条流独占窗口之后并行就失去意义了。
                                val spent = android.os.SystemClock.elapsedRealtime() - attemptStartedAt
                                val nextChunk = if (spent > 0) {
                                    val byRate = (got.toDouble() / spent * DirectProtocol.CHUNK_TARGET_MS).toLong()
                                    mutex.withLock {
                                        val grown = minOf(chunk * 2, byRate)
                                        chunk = grown.coerceIn(DirectProtocol.CHUNK_MIN, DirectProtocol.CHUNK_MAX)
                                        chunk
                                    }
                                } else {
                                    chunk
                                }

                                DirectLog.i(
                                    "分块完成 offset=$offset got=$got 耗时=${spent}ms 下一段=${nextChunk / 1024}KB",
                                )

                                // 这一段拖得太久，说明这条连接很可能已经被链路拖死
                                // （办公 AP 上两机互访传几段后就不再 ACK，K105）。
                                // 丢掉连接池，下一段重新握手，别把剩下的段都堵在这条上。
                                if (spent > STALE_CONNECTION_MS) {
                                    DirectLog.w("分块偏慢 ${spent}ms，丢弃连接池让下一段重新握手")
                                    client.connectionPool.evictAll()
                                }
                            } catch (error: Exception) {
                                DirectLog.w(
                                    "分块失败 offset=$offset want=$want 第 $attempt/" +
                                        "${DirectProtocol.CHUNK_MAX_ATTEMPTS} 次：" +
                                        "${error.javaClass.simpleName}: ${error.message}",
                                )

                                // 失败八成是这条连接已经死了。不驱逐的话，重试会从池里
                                // 挑到同一条死连接，再白等一轮超时（1.5.48 现场）。
                                client.connectionPool.evictAll()

                                mutex.withLock { chunk = maxOf(chunk / 2, DirectProtocol.CHUNK_MIN) }

                                if (attempt >= DirectProtocol.CHUNK_MAX_ATTEMPTS) {
                                    mutex.withLock {
                                        if (fatal == null) {
                                            fatal = IOException(
                                                "传输中断：已收 ${done.get()} / $total 字节，这条网络反复断开",
                                                error,
                                            )
                                        }
                                    }
                                    return
                                }

                                delay(400L * attempt)
                            }
                        }
                    }
                }

                coroutineScope {
                    repeat(DOWNLOAD_WORKERS) {
                        launch { runWorker() }
                    }
                }

                fatal?.let { throw it }

                raf.setLength(total)
            }

            if (!temp.renameTo(target)) {
                target.delete()
                if (!temp.renameTo(target)) {
                    throw IOException("改名失败：${temp.name}")
                }
            }

            onProgress(total, total)

            val cost = android.os.SystemClock.elapsedRealtime() - startedAt
            val rate = if (cost <= 0) 0 else total * 1000 / cost / 1024
            DirectLog.i("下载完成 ${target.name} ${total}B 用时 ${cost}ms 约 ${rate} KB/s")

            Result.success(target)
        } catch (error: Exception) {
            DirectLog.w("下载失败 ${item.name}", error)
            runCatching { temp?.delete() }
            Result.failure(error)
        }
    }

    /**
     * 清掉一小时前的 `.wcpart-*`。
     *
     * 会话中断/杀进程会留下这些隐藏文件。用时间而不是"见面就删"，是为了不误删
     * 正在进行的另一个下载。
     */
    private fun cleanStaleParts(directory: File) {
        val cutoff = System.currentTimeMillis() - 60 * 60 * 1000L
        directory.listFiles()?.forEach { file ->
            if (file.name.startsWith(".wcpart-") && file.lastModified() < cutoff) {
                runCatching { file.delete() }
            }
        }
    }

    /**
     * 拉一段，按**绝对位置**写进 [channel]。返回这一块实际收到多少字节。
     *
     * 用 `FileChannel.write(buffer, position)` 而不是 `RandomAccessFile.seek + write`：
     * 多个 worker 同时写同一个文件时，seek/write 会互相改文件指针，写出来的数据是乱的；
     * 带位置的写不影响通道自身的 position，可以并发。
     */
    private fun fetchRange(
        item: DirectFileDto,
        channel: java.nio.channels.FileChannel,
        offset: Long,
        want: Long,
        onDelta: (Long) -> Unit,
    ): Long {
        val request = authorized(
            Request.Builder()
                .url("${endpoint.baseUrl}${DirectProtocol.PATH_SHARED}/${item.id}")
                .header("Range", "bytes=$offset-${offset + want - 1}")
                .get(),
        ).build()

        client.newCall(request).execute().use { response ->
            if (response.code == 416) {
                return 0L
            }

            if (!response.isSuccessful) {
                throw DirectHttpException(
                    response.code,
                    errorMessageOf(response.code, response.body?.string().orEmpty()),
                )
            }

            val body = response.body ?: throw IOException("响应为空")
            val expected = body.contentLength().takeIf { it >= 0 } ?: want

            var position = offset
            var got = 0L
            val buffer = ByteArray(64 * 1024)

            body.byteStream().use { input ->
                while (got < expected) {
                    val readStartedAt = android.os.SystemClock.elapsedRealtime()
                    val read = input.read(buffer, 0, minOf(buffer.size.toLong(), expected - got).toInt())
                    val readCost = android.os.SystemClock.elapsedRealtime() - readStartedAt
                    if (readCost >= 500) {
                        DirectLog.w("下载读网络慢 ${readCost}ms 本次 ${if (read < 0) 0 else read}B 段内 ${got}B")
                    }
                    if (read < 0) {
                        break
                    }

                    val slice = java.nio.ByteBuffer.wrap(buffer, 0, read)
                    while (slice.hasRemaining()) {
                        val written = channel.write(slice, position)
                        if (written <= 0) {
                            throw IOException("写本地文件失败（位置 $position）")
                        }
                        position += written
                    }

                    got += read
                    onDelta(read.toLong())
                }
            }

            if (got < expected) {
                throw IOException("这一段只收到 $got / $expected 字节")
            }

            return got
        }
    }

    /**
     * 把一个文件推到对面（落到对面的收件箱）。
     *
     * 分段 PUT：每块带 `?offset=` 与 `X-Total-Bytes`，服务端写进临时文件，
     * 最后一块到齐才改名成正式文件。断一次只丢当前块（1.5.19）。
     */
    suspend fun upload(
        uri: Uri,
        name: String,
        sizeBytes: Long,
        /** 传应用时为包名，让对方把 base/split 合成一次安装会话；普通文件为 null。 */
        apkGroup: String? = null,
        apkKind: Int = DirectProtocol.KIND_FILE,
        onProgress: (sent: Long, total: Long) -> Unit = { _, _ -> },
    ): Result<Unit> = withContext(Dispatchers.IO) {
        if (sizeBytes > DirectProtocol.MAX_FILE_BYTES) {
            return@withContext Result.failure(IOException("文件太大，超过 2 GiB"))
        }

        try {
            val encoded = URLEncoder.encode(name, "UTF-8")
            val startedAt = android.os.SystemClock.elapsedRealtime()
            DirectLog.i("上传开始 name=$name size=$sizeBytes")

            var offset = 0L
            var chunk = DirectProtocol.CHUNK_INITIAL

            // 空文件也要发一个 0 字节的段，否则对面什么都不会收到
            if (sizeBytes == 0L) {
                sendChunk(uri, encoded, 0L, 0L, 0L, apkGroup, apkKind)
            }

            while (offset < sizeBytes) {
                val want = minOf(chunk, sizeBytes - offset)
                var attempt = 0
                var moved = false

                while (!moved && attempt < DirectProtocol.CHUNK_MAX_ATTEMPTS) {
                    attempt++
                    try {
                        val chunkStartedAt = android.os.SystemClock.elapsedRealtime()
                        sendChunk(uri, encoded, offset, want, sizeBytes, apkGroup, apkKind)
                        val spent = android.os.SystemClock.elapsedRealtime() - chunkStartedAt

                        offset += want
                        moved = true

                        if (spent > 0) {
                            val perMs = want.toDouble() / spent
                            val target = (perMs * DirectProtocol.CHUNK_TARGET_MS).toLong()
                            chunk = target.coerceIn(DirectProtocol.CHUNK_MIN, DirectProtocol.CHUNK_MAX)
                        }

                        onProgress(offset, sizeBytes)
                    } catch (error: Exception) {
                        DirectLog.w(
                            "上传分块失败 offset=$offset want=$want 第 $attempt/" +
                                "${DirectProtocol.CHUNK_MAX_ATTEMPTS} 次：" +
                                "${error.javaClass.simpleName}: ${error.message}",
                        )

                        if (attempt >= DirectProtocol.CHUNK_MAX_ATTEMPTS) {
                            throw IOException(
                                "传输中断：已发 $offset / $sizeBytes 字节，这条网络反复断开",
                                error,
                            )
                        }

                        chunk = maxOf(chunk / 2, DirectProtocol.CHUNK_MIN)
                        delay(400L * attempt)
                    }
                }
            }

            val cost = android.os.SystemClock.elapsedRealtime() - startedAt
            val rate = if (cost <= 0) 0 else sizeBytes * 1000 / cost / 1024
            DirectLog.i("上传完成 ${sizeBytes}B 用时 ${cost}ms 约 ${rate} KB/s")
            Result.success(Unit)
        } catch (error: Exception) {
            DirectLog.w("上传失败 $name", error)
            Result.failure(error)
        }
    }

    /** 推一块。每块重新打开一次源流并跳到 offset——SAF 的流不支持多次 seek。 */
    private fun sendChunk(
        uri: Uri,
        encodedName: String,
        offset: Long,
        length: Long,
        total: Long,
        apkGroup: String?,
        apkKind: Int,
    ) {
        val resolver = context?.contentResolver
            ?: throw IOException("当前环境读不了本地文件")

        val input = resolver.openInputStream(uri) ?: throw IOException("打不开文件")

        input.use { source ->
            if (offset > 0) {
                skipFully(source, offset)
            }

            val body = object : RequestBody() {
                override fun contentType(): MediaType? = "application/octet-stream".toMediaType()

                override fun contentLength(): Long = length

                override fun writeTo(sink: BufferedSink) {
                    val buffer = ByteArray(64 * 1024)
                    var left = length
                    while (left > 0) {
                        val read = source.read(buffer, 0, minOf(buffer.size.toLong(), left).toInt())
                        if (read < 0) {
                            throw IOException("文件实际比声明的短：还差 $left 字节")
                        }
                        sink.write(buffer, 0, read)
                        left -= read
                    }
                }
            }

            val request = authorized(
                Request.Builder()
                    .url(
                        "${endpoint.baseUrl}${DirectProtocol.PATH_INBOX}/$encodedName" +
                            "?${DirectProtocol.QUERY_OFFSET}=$offset",
                    )
                    .header(DirectProtocol.HEADER_TOTAL_BYTES, total.toString())
                    .apply {
                        // 老服务端会忽略这两个头，行为退化成"若干普通 apk 文件"
                        if (!apkGroup.isNullOrBlank()) {
                            header(DirectProtocol.HEADER_APK_GROUP, apkGroup)
                            header(DirectProtocol.HEADER_APK_KIND, apkKind.toString())
                        }
                    }
                    .put(body),
            ).build()

            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    throw DirectHttpException(
                        response.code,
                        errorMessageOf(response.code, response.body?.string().orEmpty()),
                    )
                }
                response.body?.close()
            }
        }
    }

    /**
     * 量一下这条链路能跑多快，返回字节/秒。
     *
     * 服务端发 N 个零字节，这边读到内存里直接丢——不碰磁盘、不碰 SAF。
     * 直连慢的时候界面需要这个数字：是"文件太大要等"，还是"这条路根本不通"，
     * 一眼就能分（1.5.19）。
     */
    suspend fun measureLink(bytes: Long = 256 * 1024L): Result<Long> = withContext(Dispatchers.IO) {
        val request = authorized(
            Request.Builder()
                .url("${endpoint.baseUrl}${DirectProtocol.PATH_PROBE}?bytes=$bytes")
                .header("Connection", "close")
                .get(),
        ).build()

        val startedAt = android.os.SystemClock.elapsedRealtime()
        var got = 0L
        var failure: Exception? = null

        try {
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    throw DirectHttpException(response.code, errorMessageOf(response.code, ""))
                }

                val body = response.body ?: throw IOException("响应为空")
                body.byteStream().use { input ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) {
                            break
                        }
                        got += read
                    }
                }
            }
        } catch (error: Exception) {
            failure = error
        }

        val cost = android.os.SystemClock.elapsedRealtime() - startedAt

        // 超时但已经收到一部分：这仍然是有用的信息（"很慢"比"不通"准确得多）。
        // 只有一字节都没收到才算不通——那才是真的没法传。
        if (got > 0L) {
            val perSecond = if (cost <= 0) got else got * 1000 / cost
            DirectLog.i(
                "链路测速 ${got}B / ${cost}ms = ${perSecond / 1024} KB/s" +
                    if (failure != null) "（中途 ${failure.javaClass.simpleName}）" else "",
            )
            return@withContext Result.success(perSecond)
        }

        DirectLog.w("链路测速失败：${failure?.message}")
        Result.failure(failure ?: IOException("没有收到数据"))
    }

    /** 跳到指定偏移。SAF 的流 `skip` 有时返回 0，退化成逐字节读。 */
    private fun skipFully(input: java.io.InputStream, count: Long) {
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

    /** 试一下地址通不通，用于"同网络直连还是连热点"的决策。 */
    suspend fun probe(timeoutMs: Long = 1_500): Boolean = withContext(Dispatchers.IO) {
        try {
            // 独立连接池：探活拿到的连接如果留在共享池里，
            // 紧接着的 status() 会复用它——而服务端可能已经把这条件断开，
            // 结果就是"握手通了、下一步失败"。
            val probeClient = client.newBuilder()
                .connectionPool(ConnectionPool(0, 1, TimeUnit.SECONDS))
                .readTimeout(timeoutMs, TimeUnit.MILLISECONDS)
                .callTimeout(timeoutMs, TimeUnit.MILLISECONDS)
                .build()

            val request = authorized(
                Request.Builder()
                    .url(endpoint.baseUrl + DirectProtocol.PATH_STATUS)
                    .header("Connection", "close")
                    .get(),
            ).build()

            // 401 也算"通"：说明对面在，只是口令不对（会话可能已被替换）
            probeClient.newCall(request).execute().use { it.isSuccessful || it.code == 401 }
        } catch (error: Exception) {
            false
        }
    }

    /**
     * 可用性探测：在 [budgetMs] 内必须真的读到 [wantBytes] 字节。
     *
     * 只做 TCP 探活是不够的 —— 这是 1.5.62 的教训。现场那台 AP（K105）上，
     * SYN/ACK 和 TLS 握手全都正常，`status()` 也是秒回，但一开始传数据就掉到 1 KB/s：
     *
     * ```
     * 快路径：172.18.15.58 可达，跳过热点
     * 链路测速 16384B / 8034ms = 1 KB/s（中途 SocketTimeoutException）
     * ```
     *
     * 于是"快路径"把唯一能用的路（对方热点）直接跳过了。这里改成读真实字节：
     * 读满 [wantBytes] 才算可用，返回实测字节/秒；读不满返回 null。
     *
     * 服务端 `/direct/probe` 最多发 `bytes` 字节，这里故意多要一些，
     * 这样"读满 48 KiB 就断"是客户端主动收手，不用等整段传完。
     */
    suspend fun quickProbe(
        wantBytes: Int = 48 * 1024,
        budgetMs: Long = 2_500L,
    ): Long? = withContext(Dispatchers.IO) {
        val request = authorized(
            Request.Builder()
                // 只要 wantBytes：多要不划算，慢链路上服务端会一直往一个已经没人读的
                // socket 里写，白白拖住它的处理线程
                .url("${endpoint.baseUrl}${DirectProtocol.PATH_PROBE}?bytes=$wantBytes")
                .header("Connection", "close")
                .get(),
        ).build()

        val startedAt = android.os.SystemClock.elapsedRealtime()
        var got = 0

        try {
            // 独立连接池 + callTimeout：探活占用的连接不能留在共享池里，
            // 否则紧接着的 status() 会复用它，而服务端可能已经断开（1.4.7 踩过）。
            val probeClient = client.newBuilder()
                .connectionPool(ConnectionPool(0, 1, TimeUnit.SECONDS))
                .callTimeout(budgetMs, TimeUnit.MILLISECONDS)
                .build()

            probeClient.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    return@withContext null
                }
                val body = response.body ?: return@withContext null
                body.byteStream().use { input ->
                    val buffer = ByteArray(16 * 1024)
                    while (got < wantBytes) {
                        val read = input.read(buffer)
                        if (read < 0) {
                            break
                        }
                        got += read
                    }
                }
            }
        } catch (error: Exception) {
            // 超时是这里的常态（链路慢时就是在预算内读不完），不当异常处理
            DirectLog.w("可用性探测中断：${error.javaClass.simpleName}: ${error.message}")
        }

        val cost = android.os.SystemClock.elapsedRealtime() - startedAt
        if (got < wantBytes) {
            DirectLog.w("可用性探测只读到 ${got}B / ${cost}ms，判定这条链路不可用")
            return@withContext null
        }

        val perSecond = if (cost <= 0) got * 1000L else got * 1000L / cost
        DirectLog.i("可用性探测 ${got}B / ${cost}ms = ${perSecond / 1024} KB/s")
        perSecond
    }

    fun shutdown() {
        runCatching { client.dispatcher.executorService.shutdown() }
        runCatching { client.connectionPool.evictAll() }
    }

    // ------------------------------------------------------------ 内部

    // 必须自己切到 IO：OkHttp 的 execute() 是阻塞调用，
    // 而 ViewModel 的协程跑在主线程上——不切线程会抛 NetworkOnMainThreadException
    // （它的 message 是 null，所以 1.4.7 之前界面只显示"握不上手：null"）。
    private suspend inline fun <reified T> executeJson(request: Request): Result<T> =
        withContext(Dispatchers.IO) {
            var lastError: Throwable? = null

            // 对"连接被对端提前关闭"重试一次。
            //
            // 对面是手写的 HTTPS 服务：接上就断（EOF）在我们这边没有任何可做的事，
            // 但换一条连接重发一次通常是通的——用户看到的就是"再点一次就好了"。
            // 只看 IOException；HTTP 4xx/5xx 用 DirectHttpException 包装，不重试。
            for (attempt in 0..1) {
                val result = runCatching {
                    client.newCall(request).execute().use { response ->
                        val text = response.body?.string().orEmpty()
                        if (!response.isSuccessful) {
                            throw DirectHttpException(response.code, errorMessageOf(response.code, text))
                        }
                        WcJson.instance.decodeFromString<T>(text)
                    }
                }

                if (result.isSuccess) {
                    return@withContext result
                }

                lastError = result.exceptionOrNull()
                if (attempt == 1 || lastError !is IOException || lastError is DirectHttpException) {
                    return@withContext result
                }

                delay(250)
            }

            Result.failure(lastError ?: IOException("请求失败"))
        }

    private fun authorized(builder: Request.Builder): Request.Builder =
        builder.header(DirectProtocol.AUTH_HEADER, DirectProtocol.BEARER_PREFIX + endpoint.token)

    private fun errorMessageOf(code: Int, body: String): String {
        val detail = runCatching {
            WcJson.instance.decodeFromString<DirectError>(body).error
        }.getOrNull()

        return when (detail) {
            "too_large" -> "文件太大，对方不收"
            "invalid_name" -> "文件名不合法"
            "not_found" -> "对方没有这个文件"
            "unauthorized" -> "会话已失效，请重新扫码"
            null -> "请求失败：HTTP $code"
            else -> "请求失败：$detail"
        }
    }

    private companion object {
        /** 进度回调的最小间隔：再密就是在折腾 UI，而不是在报进度。 */
        const val PROGRESS_INTERVAL_MS = 200L

        /**
         * 并行拉取的连接数。
         *
         * 3 是折中：单流被 RTT 卡住时多条流能各自占一个窗口；
         * 再多会让本来就不宽裕的链路互相抢，反而更慢。
         */
        const val DOWNLOAD_WORKERS = 3

        /**
         * 一段传超过这个时间就认为这条连接已经被链路拖垮。
         *
         * 办公 AP 上两机互访的典型症状：前几段几十毫秒，之后某条连接突然不再 ACK。
         * 8 秒 ≈ 读超时的一整轮，到了就换新连接，而不是继续在这条上赌。
         */
        const val STALE_CONNECTION_MS = 8_000L
    }

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

    private fun copyWithProgress(
        input: java.io.InputStream,
        output: java.io.OutputStream,
        total: Long,
        onProgress: (Long, Long) -> Unit,
    ) {
        val buffer = ByteArray(64 * 1024)
        var sent = 0L
        var lastReport = 0L
        var lastLog = 0L

        while (true) {
            val readStartedAt = android.os.SystemClock.elapsedRealtime()
            val read = input.read(buffer)
            val readCost = android.os.SystemClock.elapsedRealtime() - readStartedAt
            if (readCost >= 500) {
                DirectLog.w("下载读网络慢 ${readCost}ms 本次 ${if (read < 0) 0 else read}B 累计 ${sent}B")
            }
            if (read < 0) {
                break
            }

            val writeStartedAt = android.os.SystemClock.elapsedRealtime()
            output.write(buffer, 0, read)
            val writeCost = android.os.SystemClock.elapsedRealtime() - writeStartedAt
            if (writeCost >= 500) {
                DirectLog.w("写本地文件慢 ${writeCost}ms 本次 ${read}B 累计 ${sent}B")
            }
            sent += read

            // 同样按时间节流：按字节回调在大文件上会把主线程压死
            val now = android.os.SystemClock.elapsedRealtime()
            if (now - lastReport >= PROGRESS_INTERVAL_MS || (total > 0 && sent == total)) {
                lastReport = now
                onProgress(sent, total)
            }

            if (now - lastLog >= 1_000) {
                lastLog = now
                DirectLog.i("下载中 ${sent}/${total}B")
            }
        }

        // 对端声明了长度却提前结束：不能当成功，否则用户拿到一个截断的文件还以为传完了
        if (total > 0 && sent != total) {
            throw IOException("传输不完整：$sent / $total 字节")
        }
    }
}

/** 对方回了一个非 2xx。带上状态码，界面据此区分"会话失效"与"连不上"。 */
class DirectHttpException(val statusCode: Int, message: String) : java.io.IOException(message)
