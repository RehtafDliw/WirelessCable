package com.wirelesscable.app.core.direct

import android.content.Context
import android.net.Uri
import android.net.wifi.WifiManager
import android.provider.OpenableColumns
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * 直连会话管理器（出码方）。
 *
 * 一次只有一个会话：会话进行中不允许再出码（两个码会互相打架）。
 * 会话的结束条件写死在 [tick] 里：轮询到过期、主动停止、进程被杀。
 */
object DirectSessionManager {

    data class State(
        val active: Boolean = false,
        val payload: DirectPayload? = null,
        val port: Int = 0,
        /** 对方连上来了。 */
        val connected: Boolean = false,
        /** 我共享出去、等对方来拉的文件。 */
        val shared: List<DirectSession.SharedFile> = emptyList(),
        /** 对方推给我的文件。 */
        val received: List<DirectSession.ReceivedFile> = emptyList(),
        /** 正在发给对方的文件名（null 表示没在传）。 */
        val sendingName: String? = null,
        val sendingProgress: Float = 0f,
        /** 已经走了多少字节 / 总共多少。进度条只有一条线时看不出在不在动。 */
        val sendingSent: Long = 0L,
        val sendingTotal: Long = 0L,
        /**
         * [sendingName] 这条进度是**收**还是**发**。
         *
         * 出码方既要发（对方拉共享文件）也要收（对方推文件给我），
         * 两条路的字节都在 `sending*` 里，只能靠这个字段区分文案（1.5.64）。
         */
        val incoming: Boolean = false,
        val         error: String? = null,
        /** 收件目录绝对路径（日志/界面用）。 */
        val inboxPath: String? = null,
        /**
         * 收件落在应用私有目录（没有「所有文件访问权」）。
         *
         * 这个状态下**发文件完全正常**，只是收到的东西在文件管理器里看不到。
         */
        val inboxPrivate: Boolean = false,
    )

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    private var session: DirectSession? = null
    private var server: DirectServer? = null

    /**
     * 上一次出码时热点为什么没开起来（null = 没要求开，或开成功了）。
     *
     * 以前这个错误被 `.getOrNull()` 吞掉，界面只显示"热点没开起来"却不说原因，
     * 用户没法判断是权限、信道还是系统策略（1.5.21）。
     */
    var lastHotspotError: String? = null
        private set
    private var wifiLock: WifiManager.WifiLock? = null
    private var scope: CoroutineScope? = null
    private var ticker: Job? = null

    /**
     * 会话收件目录：`Download/WirelessCable/直连/<sid 前 6 位>/`，用户在文件管理器里能找到。
     *
     * 没有「所有文件访问权」时用 [privateInboxDirectory]，见 [start]。
     */
    fun inboxDirectory(sid: String): File =
        File(File(android.os.Environment.getExternalStorageDirectory(), "Download/WirelessCable"), "直连/$sid")

    /**
     * 兜底收件目录：应用私有区，**任何权限下都能写**。
     *
     * 以前没有「所有文件访问权」就直接拒绝出码——哪怕这次只是想发文件。
     * 收到的东西落在私有目录里确实不方便（文件管理器看不到），
     * 但总比"连二维码都出不来"好；界面上会说清楚落在哪（1.5.58）。
     */
    private fun privateInboxDirectory(context: Context, sid: String): File {
        val base = context.getExternalFilesDir("直连") ?: File(context.filesDir, "直连")
        return File(base, sid)
    }

    /**
     * 开一个会话。返回二维码载荷。
     *
     * [sharedUris] 是出码方要共享出去的文件（对方扫码后能看到并下载）。
     * 可以为空——那就是纯接收模式。
     */
    suspend fun start(
        context: Context,
        sharedUris: List<Uri>,
        useHotspot: Boolean = false,
    ): Result<DirectPayload> {
        if (session?.state == DirectSession.State.Waiting ||
            session?.state == DirectSession.State.Connected
        ) {
            return Result.failure(IllegalStateException("已经有一个直连会话在进行中"))
        }

        return withContext(Dispatchers.IO) {
            try {
                /*
                 * 没有「所有文件访问权」不再阻断出码。
                 *
                 * 这条检查原来挡在 start() 最前面：想给对方发文件，也必须先给全盘权限，
                 * 否则连二维码都出不来。可"发"根本不需要写别人的目录，
                 * 只有"收"才需要落盘——那就把收件目录换成应用私有区，功能全保留（1.5.58）。
                 */
                val allFilesGranted = com.wirelesscable.app.core.files.AllFilesAccess.isGranted(context)

                val files = sharedUris.mapIndexedNotNull { index, uri ->
                    val meta = queryMeta(context, uri) ?: return@mapIndexedNotNull null
                    DirectSession.SharedFile(
                        id = "f$index",
                        name = meta.first,
                        sizeBytes = meta.second,
                        uri = uri,
                    )
                }

                val identity = DirectCertificate.issue()
                val sid = DirectSession.newId()
                val inbox = if (allFilesGranted) inboxDirectory(sid) else privateInboxDirectory(context, sid)

                if (!allFilesGranted) {
                    DirectLog.w("没有「所有文件访问权」：收件目录改用私有区 ${inbox.absolutePath}")
                }

                val session = DirectSession(
                    sid = sid,
                    token = DirectSession.newToken(),
                    deviceName = android.os.Build.MODEL ?: "手机",
                    identity = identity,
                    sharedFiles = files,
                    inboxDirectory = inbox,
                )

                val server = DirectServer(context, session, listener)
                val port = server.start()

                // Wi-Fi 默认按 DTIM 周期省电，同网段 RTT 会从 5 ms 抖到 130 ms。
                // TCP 吞吐≈窗口/RTT，抖动还会让拥塞窗口反复缩水——实测就是
                // 「发送方 64K、接收方 8K」这种两边对不上的龟速（1.5.14）。
                acquireWifiLock(context)

                this@DirectSessionManager.session = session
                this@DirectSessionManager.server = server

                // 用户显式要求时才开热点：绕过坏 AP 的唯一办法，但可能顶掉原有 Wi-Fi。
                // 失败不阻断出码——退回普通 Wi-Fi，只是慢（1.5.20）。
                val hotspotResult = if (useHotspot) HotspotController.start(context) else null
                val hotspot = hotspotResult?.getOrNull()

                lastHotspotError = if (useHotspot && hotspot == null) {
                    hotspotResult?.exceptionOrNull()?.message ?: "未知原因"
                } else {
                    null
                }

                val addresses = localIpv4()

                // 开热点失败、而 Wi-Fi 又已经被系统关掉的机型（1.5.50 现场）：
                // 这时一个可达地址都没有，二维码里只能写空 host，对方必然连不上。
                // 与其发一张骗人的码，不如直接说清楚。
                if (addresses.isEmpty()) {
                    throw IllegalStateException(
                        "这台手机现在没有可用的局域网地址（Wi-Fi 可能被关掉了）；" +
                            "请打开 Wi-Fi 后重新出码，或关掉「用热点直连」",
                    )
                }

                // 开了热点就把热点地址排在最前：扫码方第一个试的是 host，
                // 写 AP 地址会让它先等一轮超时才轮到热点（1.5.52）
                val ordered = if (hotspot != null) {
                    (listOf(hotspot.address) + addresses).distinct()
                } else {
                    addresses
                }

                val payload = DirectPayload(
                    sid = sid,
                    deviceName = session.deviceName,
                    host = ordered.firstOrNull().orEmpty(),
                    addr = ordered,
                    port = port,
                    fp = identity.fingerprint,
                    token = session.token,
                    hotspot = hotspot,
                )

                DirectLog.session("出码 sid=$sid port=$port host=${payload.host} addr=${payload.addr}")
                DirectLog.i("会话已开 共享=${files.size} 个文件")

                _state.value = State(
                    active = true,
                    payload = payload,
                    port = port,
                    shared = files,
                    inboxPath = inbox.absolutePath,
                    inboxPrivate = !allFilesGranted,
                )

                startTicker()

                Result.success(payload)
            } catch (error: Exception) {
                stopInternal()
                Result.failure(error)
            }
        }
    }

    /**
     * 会话进行中再追加要共享的文件（出码方）。
     *
     * 出码时不必一次选全：先出码让对方扫，连上之后再挑，少让人干等。
     * 对方会轮询共享清单，追加完几秒内就能看到。
     */
    suspend fun addShared(context: Context, uris: List<Uri>): Result<Int> =
        withContext(Dispatchers.IO) {
            val current = session
                ?: return@withContext Result.failure(IllegalStateException("会话已结束"))

            if (current.state == DirectSession.State.Closed) {
                return@withContext Result.failure(IllegalStateException("会话已结束"))
            }

            var added = 0
            uris.forEach { uri ->
                val meta = queryMeta(context, uri) ?: return@forEach
                val (name, sizeBytes) = meta
                if (sizeBytes > DirectProtocol.MAX_FILE_BYTES) return@forEach

                current.addShared(name, sizeBytes, uri)
                added++
            }

            _state.update { it.copy(shared = current.sharedFiles) }
            Result.success(added)
        }

    /**
     * 出码方共享若干**应用**：每个包展开成 base.apk + 若干 split.apk。
     *
     * 与 [addShared] 的区别只有两点：名字按应用标签生成，并且带上 `group`（包名）与
     * `kind`，让接收方知道这几块要合成一次安装。
     */
    suspend fun addApps(apps: List<com.wirelesscable.app.core.apps.InstalledApp>): Result<Int> =
        withContext(Dispatchers.IO) {
            val current = session
                ?: return@withContext Result.failure(IllegalStateException("会话已结束"))

            if (current.state == DirectSession.State.Closed) {
                return@withContext Result.failure(IllegalStateException("会话已结束"))
            }

            var added = 0
            apps.forEach { app ->
                app.parts.forEachIndexed { index, part ->
                    if (part.sizeBytes <= 0L || part.sizeBytes > DirectProtocol.MAX_FILE_BYTES) {
                        return@forEachIndexed
                    }

                    current.addShared(
                        name = com.wirelesscable.app.core.apps.InstalledApps
                            .partName(app.label, app.packageName, index),
                        sizeBytes = part.sizeBytes,
                        uri = Uri.fromFile(File(part.path)),
                        group = app.packageName,
                        kind = if (index == 0) {
                            DirectProtocol.KIND_APK_BASE
                        } else {
                            DirectProtocol.KIND_APK_SPLIT
                        },
                    )
                    added++
                }
            }

            _state.update { it.copy(shared = current.sharedFiles) }
            Result.success(added)
        }

    /**
     * 扫码方也要持 Wi-Fi 高性能锁。
     *
     * 之前只在出码方（[start]）加了锁，扫码方裸着跑：它既可能是接收方，
     * 也可能在会话里当发送方，一旦 Wi-Fi 进省电，往返延迟就从 5 ms 跳到 200 ms 以上，
     * 吞吐被窗口/RTT 掐到几十 KB/s（1.5.15）。
     */
    fun holdClientWifi(context: Context) {
        acquireWifiLock(context)
    }

    /** 扫码方断开时释放。幂等。 */
    fun releaseClientWifi() {
        releaseWifiLock()
    }

    fun stop() {
        stopInternal()
    }

    /**
     * 会话期间禁用 Wi-Fi 省电。
     *
     * 不持锁时链路明明 866 Mbps，ping 却 avg 57 ms / max 132 ms：
     * 手机在两次 DTIM 之间睡过去了。传输跟着一起被拖死。
     */
    private fun acquireWifiLock(context: Context) {
        if (wifiLock?.isHeld == true) {
            return
        }

        val manager = context.applicationContext
            .getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return

        wifiLock = runCatching {
            manager.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "WirelessCable:direct").apply {
                setReferenceCounted(false)
                acquire()
            }
        }.onFailure {
            android.util.Log.w("WcDirect", "拿不到 Wi-Fi 高性能锁", it)
        }.getOrNull()

        android.util.Log.i("WcDirect", "直连已持有 Wi-Fi 高性能锁")
    }

    private fun releaseWifiLock() {
        runCatching {
            if (wifiLock?.isHeld == true) {
                wifiLock?.release()
            }
        }
        wifiLock = null
    }

    private fun stopInternal(reason: String? = null) {
        releaseWifiLock()
        HotspotController.stop()
        ticker?.cancel()
        ticker = null
        runCatching { server?.stop() }
        server = null
        session = null
        scope = null
        // 一次写到位：先写 State() 再补 error 会让收集方先看到"正常结束"，
        // 等到第二次更新时 hosting 已经 false，提示就被吃掉了（1.5.14）
        _state.value = State(error = reason)
    }

    private fun startTicker() {
        val currentScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        scope = currentScope

        ticker = currentScope.launch {
            while (isActive) {
                delay(1_000)
                val active = session ?: break

                if (active.isExpired()) {
                    stopInternal()
                    break
                }

                // 对方一直没请求 = 它已经走了（关 App / 断网 / 主动断开）。
                // 不判定的话出码方会一直显示"已连接"，用户以为还能发文件（1.5.14）
                if (active.state == DirectSession.State.Connected &&
                    System.currentTimeMillis() - active.lastActivityMs > DirectProtocol.PEER_IDLE_TIMEOUT_MS
                ) {
                    android.util.Log.i("WcDirect", "对方超时没有请求，判定已断开 sid=${active.sid}")
                    stopInternal("对方已断开，会话结束")
                    break
                }

                // 只同步"状态有没有变"，避免每秒写一次 StateFlow 触发重组
                val connected = active.state == DirectSession.State.Connected
                if (connected != _state.value.connected || active.receivedCount() != _state.value.received.size) {
                    _state.update {
                        it.copy(connected = connected, received = active.receivedSnapshot())
                    }
                }
            }
        }
    }

    private val listener = object : DirectServer.Listener {
        override fun onConnected(remote: String) {
            _state.update { it.copy(connected = true) }
        }

        override fun onReceived(name: String, sizeBytes: Long) {
            val current = session ?: return
            _state.update {
                it.copy(
                    received = current.receivedSnapshot(),
                    sendingName = null,
                    sendingProgress = 0f,
                    incoming = false,
                )
            }
        }

        override fun onSending(name: String, sent: Long, total: Long) {
            _state.update {
                it.copy(
                    sendingName = name,
                    sendingProgress = if (total <= 0) 0f else sent.toFloat() / total,
                    sendingSent = sent,
                    sendingTotal = total,
                    incoming = false,
                )
            }
        }

        override fun onReceiving(name: String, received: Long, total: Long) {
            _state.update {
                it.copy(
                    sendingName = name,
                    sendingProgress = if (total <= 0) 0f else received.toFloat() / total,
                    sendingSent = received,
                    sendingTotal = total,
                    incoming = true,
                )
            }
        }

        override fun onError(message: String) {
            // 同时收掉"正在发送"：失败之后还挂着进度条，看起来像还在跑（1.5.52）
            _state.update {
                it.copy(
                    error = message,
                    sendingName = null,
                    sendingProgress = 0f,
                    sendingSent = 0L,
                    sendingTotal = 0L,
                    incoming = false,
                )
            }
        }
    }

    /**
     * 文件名 + **真实**字节数。
     *
     * 大小必须准：两端都按它写 Content-Length，写错了对方会一直等剩下的字节。
     * `OpenableColumns.SIZE` 只是 provider 的说法，可能过期、也可能直接是 -1，
     * 所以再打开文件描述符问一次 `statSize`，以它为准（1.5.13：1 GB 只走 500 KB）。
     */
    private fun queryMeta(context: Context, uri: Uri): Pair<String, Long>? {
        var name = uri.lastPathSegment ?: "file"
        var declared = -1L

        context.contentResolver
            .query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)
            ?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)

                    if (nameIndex >= 0 && !cursor.isNull(nameIndex)) {
                        cursor.getString(nameIndex)?.let { name = it }
                    }

                    if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) {
                        declared = cursor.getLong(sizeIndex)
                    }
                }
            }

        val actual = runCatching {
            context.contentResolver.openFileDescriptor(uri, "r")?.use { it.statSize }
        }.getOrNull() ?: -1L

        val size = if (actual >= 0) actual else declared
        return if (size >= 0) name to size else null
    }

    /** 本机可用的 IPv4（优先 wlan0），同 Wi-Fi 时对方直接用这个地址。 */
    /** 蜂窝 / 运营商内网接口：不是直连可达的地址，出码时要排除。 */
    private fun isCellularInterface(name: String): Boolean {
        val lower = name.lowercase()
        return lower.startsWith("rmnet") ||
            lower.startsWith("ccmni") ||
            lower.startsWith("pdp") ||
            lower.startsWith("clat") ||
            lower.contains("rmnet")
    }

    fun localIpv4(): List<String> {
        val wired = mutableListOf<String>()
        val others = mutableListOf<String>()

        try {
            NetworkInterface.getNetworkInterfaces()?.toList()?.forEach { nic ->
                if (!nic.isUp || nic.isLoopback) {
                    return@forEach
                }

                // 蜂窝接口的地址对扫码的那台手机不可达（10.x / 100.64.x 的运营商内网）。
                // 只有它可用时宁可明确报错，也别出一张"看起来能连、实际连不上"的二维码（1.5.50）。
                if (isCellularInterface(nic.name)) {
                    return@forEach
                }

                nic.inetAddresses.toList().filterIsInstance<Inet4Address>().forEach { address ->
                    if (address.isLoopbackAddress || address.isLinkLocalAddress) {
                        return@forEach
                    }

                    val host = address.hostAddress ?: return@forEach
                    if (nic.name.startsWith("wlan")) {
                        wired += host
                    } else {
                        others += host
                    }
                }
            }
        } catch (error: Exception) {
            android.util.Log.w("WcDirect", "枚举本机地址失败", error)
        }

        return (wired + others).distinct()
    }
}
