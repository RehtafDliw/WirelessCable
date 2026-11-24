package com.wirelesscable.app.vm

import android.app.Application
import android.content.Context
import android.graphics.Bitmap
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.Uri
import android.net.wifi.WifiNetworkSpecifier
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.wirelesscable.app.core.apps.ApkInstaller
import com.wirelesscable.app.core.apps.InstalledApp
import com.wirelesscable.app.core.apps.InstalledApps
import com.wirelesscable.app.core.direct.DirectClient
import com.wirelesscable.app.core.direct.DirectFileDto
import com.wirelesscable.app.core.direct.DirectHotspot
import com.wirelesscable.app.core.direct.DirectHttpException
import com.wirelesscable.app.core.direct.DirectLog
import com.wirelesscable.app.core.direct.DirectPayload
import com.wirelesscable.app.core.direct.DirectProtocol
import com.wirelesscable.app.core.direct.DirectQr
import com.wirelesscable.app.core.direct.DirectSession
import com.wirelesscable.app.core.direct.DirectStatus
import com.wirelesscable.app.core.direct.DirectSessionManager
import com.wirelesscable.app.core.direct.HotspotController
import com.wirelesscable.app.core.direct.HotspotNetworkPin
import com.wirelesscable.app.core.network.LocalSubnet
import com.wirelesscable.app.core.direct.HotspotPermissions
import com.wirelesscable.app.core.direct.HotspotSuggester
import com.wirelesscable.app.core.files.FileOpener
import com.wirelesscable.app.core.files.InboxStore
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.encodeToString
import java.io.File
import kotlin.coroutines.resume

/**
 * 等系统连上对方热点的时间。
 *
 * 中间会弹一次系统确认框（"是否连接到 …"），要留够用户点"连接"的时间，
 * 但也不能无限等——用户可能就是不想连。
 */
// 25 秒：系统确认框一般几秒内就点了，等 60 秒只会让人以为卡死（1.5.51）
private const val HOTSPOT_JOIN_TIMEOUT_MS = 25_000L

/**
 * 多久没有新字节就在界面上说"卡住"。
 *
 * 链路断流时进度条完全不动，用户以为程序死了 —— 现场就是这样，等 10 秒就重连了，
 * 而客户端其实还在读超时（8 秒）+ 重试（5 次）里。给一句话，比让他猜划算。
 */
private const val STALL_HINT_MS = 5_000L

/**
 * 选路探测的规模：必须在 [PROBE_BUDGET_MS] 内读满 [PROBE_MIN_BYTES] 字节。
 *
 * 折算下来约 20 KB/s 的下限。低于这个数，1 GB 的文件要传 14 小时，
 * 已经不叫"慢"，叫"不通"——不如去连热点（1.5.63）。
 */
private const val PROBE_MIN_BYTES = 48 * 1024
private const val PROBE_BUDGET_MS = 2_500L

/**
 * 连上之后实测吞吐低于这个值，就自动改走对方热点。
 *
 * [PROBE_MIN_BYTES] 只是"看起来能传"，这里才是真的量。现场那台 AP 实测 1 KB/s，
 * 而热点随便都是几 MB/s（1.5.63）。
 */
private const val LINK_USABLE_MIN_BPS = 120_000L

/** 登记网络建议后，等系统自己关联热点的时间。 */
private const val SUGGESTION_JOIN_TIMEOUT_MS = 20_000L

/** 手动连热点最多等这么久；超时就认为这次不连了（界面仍留着"重试"）。 */
private const val WAIT_FOR_NETWORK_MS = 120_000L

/**
 * 两机直连（不经电脑）。
 *
 * 两种角色：
 * - **出码方**：起一个一次性 HTTPS 服务，出二维码；对方连上后可以拉我共享的文件、也能推文件给我。
 * - **扫码方**：扫上面的码，连过去；能拉对方的共享文件、也能推自己的文件过去。
 *
 * 一旦断开/退出/换网络，会话作废，只能重新扫码——这是需求里明确要的行为。
 */
class DirectViewModel(application: Application) : AndroidViewModel(application) {

    /** 自动加入热点失败时，给用户手动连接需要的信息。 */
    data class HotspotFallback(val ssid: String, val passphrase: String)

    data class State(
        val hosting: Boolean = false,
        val joined: Boolean = false,
        val qr: Bitmap? = null,
        val payload: DirectPayload? = null,
        val connected: Boolean = false,
        val peerName: String = "",
        val shared: List<DirectSession.SharedFile> = emptyList(),
        val peerFiles: List<DirectFileDto> = emptyList(),
        val received: List<DirectSession.ReceivedFile> = emptyList(),
        val sendingName: String? = null,
        /** [sendingName] 这条进度是"收"还是"发"：出码方两个方向都可能发生（1.5.64）。 */
        val transferIncoming: Boolean = false,
        val progress: Float = 0f,
        val sentBytes: Long = 0L,
        val totalBytes: Long = 0L,
        val downloaded: List<String> = emptyList(),
        /** 连上后实测的链路速率（字节/秒）。null = 还没测出来。 */
        val linkBytesPerSecond: Long? = null,
        /** 测速失败：这条路连 512 KB 都送不过来，传大文件必然失败。 */
        val linkFailed: Boolean = false,
        /** 正在测速。 */
        val linkMeasuring: Boolean = false,
        /** 出码时是否顺便开热点（绕过坏 AP 用）。 */
        val useHotspot: Boolean = false,
        /** 这次连接实际走的是不是对方的热点。 */
        val viaHotspot: Boolean = false,
        /** 对方的二维码里带了热点（不管这次有没有走通）。 */
        val peerHasHotspot: Boolean = false,
        /** 自动加入热点没成功时给出 SSID/密码，让用户去系统设置手动连。 */
        val hotspotFallback: HotspotFallback? = null,
        /**
         * 正在等用户手动连上对方热点（连上会自动继续）。
         *
         * 旧原型就是这个行为：加入失败不判死，转入"等待网络"，用户连上手机会自动接着走。
         * 我们中间有一版是静默退回普通 Wi-Fi，用户只能干等（1.5.62 改回）。
         */
        val waitingForNetwork: Boolean = false,
        val message: String? = null,
        /**
         * 缺少热点权限时的可操作提示（非 null 时页面给「打开应用设置 / 重试」）。
         *
         * 单独一个字段而不是塞进 [message]：message 只是一行灰字，
         * 用户看到"没有权限"却没地方点，等于没说（1.5.57）。
         */
        val permissionIssue: String? = null,
        val busy: Boolean = false,
        /** 传输中超过 [STALL_HINT_MS] 没有新字节：界面上要说"卡住、正在重试"。 */
        val stalled: Boolean = false,
    )

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    private var client: DirectClient? = null

    /**
     * 已下载的共享文件：`文件 id -> 本地绝对路径`。
     *
     * 只靠 State 里的 `downloaded`（id 集合）不够——安装分体包要真的拿到文件，
     * 而下载落盘时可能被加了 `(1)` 后缀，靠名字回拼会找错文件（1.5.65）。
     */
    private val downloadedPaths = mutableMapOf<String, String>()
    private var watchJob: Job? = null

    /** 出码方盯着热点：ROM 会把它回收，界面不能继续写"已开热点"。 */
    private var hotspotJob: Job? = null

    /**
     * 扫码连接任务。
     *
     * 相机识别到二维码后，同一帧会连着回调好几次；`state.joined` 要等连上才变 true，
     * 光靠它挡不住并发连接。两次连接都成功时，后一个 client 会覆盖前一个，
     * 前一条下载的 3 个 worker 就停在那里等一个已经没人管的连接（1.5.49 现场：
     * 服务端日志里出现两轮 probe + 测速，下载到第 3 段后再没请求）。
     */
    private var joinJob: Job? = null

    /** 最近一次"有新字节"的时刻（elapsedRealtime），用来判断是不是卡住了。 */
    private var lastProgressAt = 0L

    /** 扫码方定时拉对方的共享清单：对面随时可能"再发一个文件过来"。 */
    private var peerJob: Job? = null

    /** 加入对方热点时注册的回调，断开时必须注销，否则系统一直占着那个 Wi-Fi。 */
    private var hotspotCallback: ConnectivityManager.NetworkCallback? = null

    /**
     * 上一次 `requestNetwork` 是不是被系统以**权限**为由拒的。
     *
     * 以前这里跟"系统拒绝确认框 / 对方热点关了"混成一句话，用户完全不知道该去开权限。
     * 现在保留原因，join 结束时据此给出「打开应用设置」的入口（1.5.57）。
     */
    private var hotspotSecurityFailure: String? = null

    /** 等待用户手动连上热点：探测到可达就自动继续。 */
    private var waitJob: Job? = null

    /** 会话期间把热点那条无外网 Wi-Fi 钉住，避免息屏被系统回收。 */
    private var hotspotPin: HotspotNetworkPin? = null

    /** 这次会话是否登记过网络建议（离开时必须撤销，否则手机会一直黏在热点上）。 */
    private var suggestedHotspot = false

    /**
     * 是否已经因为"实测太慢"自动改走过一次热点。
     *
     * 只允许一次：两条路都坏的时候来回跳，只会让界面一直在闪烁重连（1.5.63）。
     */
    private var hotspotAutoSwitchTried = false

    private val context get() = getApplication<Application>()

    // ------------------------------------------------------------ 出码方

    /**
     * 开始出码。[uris] 是要共享给对方拉的文件，可以为空（纯接收）。
     */
    /** 切换「用热点直连」。只影响下一次出码，不改当前会话。 */
    fun setUseHotspot(enabled: Boolean) {
        _state.update { it.copy(useHotspot = enabled) }
    }

    fun startHosting(uris: List<Uri>) {
        if (_state.value.hosting) {
            return
        }

        _state.update { it.copy(busy = true, message = null) }

        val useHotspot = _state.value.useHotspot

        viewModelScope.launch {
            val result = DirectSessionManager.start(context, uris, useHotspot = useHotspot)
            if (result.isFailure) {
                _state.update {
                    it.copy(busy = false, message = "开启失败：${result.exceptionOrNull()?.message}")
                }
                return@launch
            }

            val payload = result.getOrThrow()
            val text = WcJson.instance.encodeToString(payload)
            val bitmap = withContext(Dispatchers.Default) { DirectQr.encode(text) }

            val managerState = DirectSessionManager.state.value
            val primaryMessage = when {
                bitmap == null -> "二维码生成失败，换少几个文件重试"
                payload.hotspot != null -> "已开热点，对方连不上普通 Wi-Fi 时会自动连过来"
                useHotspot ->
                    "热点没开起来：${DirectSessionManager.lastHotspotError}" +
                        "（本次走普通 Wi-Fi；也可以换一台手机出码）"
                else -> null
            }

            // 没有「所有文件访问权」时不再拦出码，但收件落在私有目录——
            // 这句话必须说，否则用户会去「下载」里找对方发来的文件（1.5.58）。
            val inboxHint = if (managerState.inboxPrivate) {
                "对方发来的文件会存到应用私有目录；想放进「下载」请到「文件 → 手机」开启所有文件访问权"
            } else {
                null
            }

            _state.value = State(
                hosting = true,
                qr = bitmap,
                payload = payload,
                shared = managerState.shared,
                useHotspot = useHotspot,
                message = primaryMessage ?: inboxHint,
            )

            watchHosting()
            watchHotspot()
        }
    }

    /**
     * 出码方：热点被系统收回时把界面拉回现实。
     *
     * 现场（1.5.50）：有的机型先关 Wi-Fi 再失败，旧代码只认 `onStarted`，
     * 界面上从头到尾写着"已开热点"。现在控制器会判定失败，但**已经成功的热点**
     * 也可能几分钟后被 ROM 按省电策略回收 —— 那时二维码里的地址已经不在了，
     * 必须提示用户重新出码，否则对方会一直连一个不存在的 AP。
     */
    private fun watchHotspot() {
        hotspotJob?.cancel()
        if (_state.value.payload?.hotspot == null) {
            return
        }

        hotspotJob = viewModelScope.launch {
            while (isActive) {
                delay(1_500)
                if (!_state.value.hosting || _state.value.payload?.hotspot == null) {
                    return@launch
                }

                if (HotspotController.isActive) {
                    continue
                }

                _state.update {
                    it.copy(
                        payload = it.payload?.copy(hotspot = null),
                        message = "热点已被系统关闭，这个二维码里的热点地址已失效；请重新出码（或关掉「用热点直连」）",
                    )
                }
                return@launch
            }
        }
    }

    /**
     * 出码方：会话进行中再给对方加文件。
     *
     * 和扫码方的「发送」不同——出码方是服务端，没有反向连接，只能把文件放进共享清单
     * 让对方来拉。对方每 5 秒轮询一次，追加完几秒内就会出现在它的列表里。
     */
    fun addShared(uris: List<Uri>) {
        if (!_state.value.hosting || uris.isEmpty()) {
            return
        }

        viewModelScope.launch {
            _state.update { it.copy(busy = true) }

            val result = DirectSessionManager.addShared(context, uris)
            val added = result.getOrDefault(0)

            _state.update {
                it.copy(
                    busy = false,
                    shared = DirectSessionManager.state.value.shared,
                    message = when {
                        result.isFailure -> "添加失败：${result.exceptionOrNull()?.message}"
                        added == 0 -> "没有可发送的文件（可能超过了 2 GiB）"
                        else -> "已加 $added 个文件，对方马上就能看到"
                    },
                )
            }
        }
    }

    /** 出码方跟随会话状态（连上没有、收了几个、正在发哪个）。 */
    private fun watchHosting() {
        watchJob?.cancel()
        watchJob = viewModelScope.launch {
            DirectSessionManager.state.collect { snapshot ->
                if (!snapshot.active && _state.value.hosting) {
                    // 会话被管理器关掉了（过期 / 对方断开 / 主动停止）。
                    // error 里带着原因，别把它吃掉——用户需要知道为什么回到初始页。
                    _state.update {
                        it.copy(
                            hosting = false,
                            qr = null,
                            connected = false,
                            payload = null,
                            message = snapshot.error ?: it.message,
                        )
                    }
                    return@collect
                }

                if (!snapshot.active) {
                    return@collect
                }

                _state.update {
                    it.copy(
                        connected = snapshot.connected,
                        shared = snapshot.shared,
                        received = snapshot.received,
                        sendingName = snapshot.sendingName,
                        transferIncoming = snapshot.incoming,
                        progress = snapshot.sendingProgress,
                        sentBytes = snapshot.sendingSent,
                        totalBytes = snapshot.sendingTotal,
                        message = snapshot.error ?: it.message,
                        busy = false,
                    )
                }
            }
        }
    }

    fun stopHosting() {
        watchJob?.cancel()
        watchJob = null
        hotspotJob?.cancel()
        hotspotJob = null
        DirectSessionManager.stop()
        _state.value = State()
    }

    // ------------------------------------------------------------ 扫码方

    /**
     * 热点权限缺失。
     *
     * [detail] 来自 HotspotPermissions.missingText()：说清是哪个权限没了。
     * 扫码方以前只有一行灰字，出码方（开关弹回去）连字都没有 —— 现在两处都走这里。
     */
    fun notifyHotspotPermissionMissing(detail: String = "") {
        DirectLog.w("缺少热点权限：$detail ${HotspotPermissions.describe(context)}")
        _state.update {
            it.copy(
                permissionIssue = detail.ifBlank { "没有「附近的设备」或「定位」权限" },
                message = null,
                busy = false,
            )
        }
    }

    /**
     * 权限还没到手时先把这张码存下来。
     *
     * 扫码方是先申请权限、拿到才 [join]。用户被送到系统设置、回来点「重试」时，
     * 手里得有这张码——否则只能重新扫（1.5.57）。
     */
    fun rememberPending(payload: DirectPayload) {
        _state.update { it.copy(payload = payload, permissionIssue = null) }
    }

    /** 扫到直连码后调用。先试局域网地址，不通再连热点。 */
    /**
     * 扫到直连码后调用。
     *
     * [forceHotspot] 为 true 时跳过"先试普通地址"的快路径 —— 只在**已经确认这条路
     * 是坏的**之后使用（例如实测吞吐只有 1 KB/s，见 [measureLink]）。
     */
    fun join(payload: DirectPayload, forceHotspot: Boolean = false) {
        if (_state.value.joined || joinJob?.isActive == true) {
            return
        }

        // payload 必须存下来：热点兜底卡片的「我已连上，重试」要靠它。
        // 以前扫码方从来不写这个字段，那个按钮永远回「没有可重试的会话」（1.5.57）。
        _state.update {
            it.copy(
                payload = payload,
                busy = true,
                message = "正在连接…",
                permissionIssue = null,
            )
        }

        // 重新连接前先把上一条热点请求注销掉，否则旧网络一直被应用占着。
        releaseHotspotCallback()
        hotspotSecurityFailure = null
        downloadedPaths.clear()

        // 新一次扫码（不是"因太慢换路"）时重置：上一轮试过的那条记录不该带过来，
        // 否则用户换个地方重扫，明明这次慢也该换热点，却被旧标记挡住（1.5.63）
        if (!forceHotspot) {
            hotspotAutoSwitchTried = false
        }

        DirectLog.i(
            "扫码加入 ${HotspotPermissions.describe(context)} 码里带热点=${payload.hotspot != null} " +
                "hotspot=${payload.hotspot?.ssid ?: "-"} host=${payload.host}",
        )

        joinJob = viewModelScope.launch {
            val candidates = (listOf(payload.host) + payload.addr).filter { it.isNotBlank() }.distinct()
            var connected: DirectClient? = null
            var connectedHost: String? = null
            var viaHotspot = false

            // 只发一次请求：以前是"探活 → 再 status"，两次请求之间连接可能已经死了，
            // 表现就是"握手通了、下一步失败:null"
            var peer: DirectStatus? = null

            // 每个候选地址的结果都要留着：只保留最后一条的话，
            // 第一个地址报"口令不对"（对面在、只是会话过期），
            // 会被后面某个不可达地址的"不在同一网络"盖掉，排查方向直接跑偏（1.5.11）
            val failures = mutableListOf<Pair<String, Throwable>>()

            // 对方开了热点：默认还是优先走热点（坏 AP 的典型表现不是连不上，而是能连、
            // 很慢，K105；只看"能不能连"会一直用那条坏路）。
            // 但**先给普通地址 1.5 秒的机会**：同网时没必要过系统弹窗和切网（1.5.62）。
            val hotspot = payload.hotspot
            var hotspotError: String? = null

            /*
             * 快路径：先直连试一次（1.5.62，恢复旧原型的行为）。
             *
             * 两台手机本来就在同一个 Wi-Fi 上时，几百毫秒就能连上；1.5.51 起改成
             * "码里带热点就先去连热点"，等于每次都要过一遍系统弹窗和切网，
             * 失败还得等满 25 秒才退回普通 Wi-Fi。
             *
             * 但**探活不能只看 TCP 通不通**：K105 那类坏 AP 上 SYN/ACK、TLS、
             * `status()` 全都正常，一开始传数据就掉到 1 KB/s。1.5.62 就是被这一点
             * 骗过去了（现场日志：`快路径：192.0.2.58 可达，跳过热点`，
             * 紧接着 `链路测速 16384B / 8034ms = 1 KB/s`）。
             * 所以这里改成真读几万字节，读不满就不算可用（1.5.63）。
             */
            var fastHost: String? = null
            var fastSpeed: Long? = null
            if (hotspot != null && !forceHotspot) {
                // 热点网段的地址在没加入热点之前必然不可达，先探它等于白等 2.5 秒。
                // 同网直连是更常见的情况，所以把普通地址排在前面（1.5.63）。
                val hotspotSubnet = hotspot.address.substringBeforeLast('.', "")
                val probeOrder = if (hotspotSubnet.isEmpty()) {
                    candidates
                } else {
                    candidates.sortedBy { host -> if (host.startsWith("$hotspotSubnet.")) 1 else 0 }
                }

                for (host in probeOrder) {
                    val bytesPerSecond = probeUsable(host, payload) ?: continue
                    fastHost = host
                    fastSpeed = bytesPerSecond
                    break
                }
            }

            if (fastHost != null) {
                DirectLog.i("快路径：$fastHost 可用（${(fastSpeed ?: 0) / 1024} KB/s），跳过热点")
                _state.update { it.copy(message = "正在连接…") }
            } else if (hotspot != null) {
                _state.update {
                    it.copy(
                        message = if (forceHotspot) {
                            "当前网络太慢，正在改连对方热点…"
                        } else {
                            "正在连对方的热点…"
                        },
                    )
                }

                val network = runCatching { requestHotspotNetwork(hotspot) }
                    .onFailure { android.util.Log.w("WcDirect", "加入热点抛异常", it) }
                    .getOrNull()

                if (network != null) {
                    // 网关最可靠：各家 ROM 上报的"AP 侧地址"五花八门，
                    // 但扫码方加入热点后自己的 LinkProperties 里一定有正确网关（1.5.53）
                    val gateway = hotspotGateway(network)
                    val subnet = hotspot.address.substringBeforeLast('.', "")
                    val hotspotHosts = buildList {
                        if (!gateway.isNullOrBlank()) {
                            add(gateway)
                        }
                        add(hotspot.address)
                        if (subnet.isNotEmpty()) {
                            add("$subnet.1")
                        }
                    }.filter { it.isNotBlank() }.distinct()

                    com.wirelesscable.app.core.direct.DirectLog.i(
                        "热点网关=${gateway ?: "未取到"} 二维码地址=${hotspot.address} 候选=$hotspotHosts",
                    )

                    // 只让这条客户端的 socket 走热点（network.socketFactory），
                    // **不做进程级 bindProcessToNetwork**：那会把整个进程——包括与电脑的
                    // 心跳和索引——都绑到一条没有外网的网络上，直连期间电脑端就会显示离线（1.5.58）。
                    suspend fun tryHosts(
                        useSocketFactory: Boolean,
                    ): Triple<DirectClient, String, DirectStatus>? {
                        for (host in hotspotHosts) {
                            val endpoint = DirectClient.Endpoint(
                                host,
                                payload.port,
                                payload.fp,
                                payload.token,
                            )
                            val client = if (useSocketFactory) {
                                DirectClient(context, endpoint, network.socketFactory)
                            } else {
                                DirectClient(context, endpoint)
                            }

                            val result = client.status()
                            val status = result.getOrNull()
                            if (status != null) {
                                return Triple(client, host, status)
                            }

                            result.exceptionOrNull()?.let {
                                failures += "$host（热点）" to it
                            }
                            client.shutdown()
                        }
                        return null
                    }

                    var hit = tryHosts(useSocketFactory = true)

                    // 极少数 ROM 认进程级绑定、不认 socketFactory：临时绑一次再试，
                    // **试完立刻解绑**。只在"确实连不上"时才用，不会常驻。
                    if (hit == null) {
                        DirectLog.w("socketFactory 连不上热点服务，临时绑定进程网络再试一次")
                        bindProcessNetwork(network)
                        hit = try {
                            tryHosts(useSocketFactory = false)
                        } finally {
                            bindProcessNetwork(null)
                        }
                    }

                    if (hit != null) {
                        connected = hit.first
                        connectedHost = hit.second
                        peer = hit.third
                        viaHotspot = true
                    } else {
                        hotspotError = "热点连上了但服务不可达"
                    }
                } else {
                    // 系统以权限为由拒绝时单独说清楚：这两种失败的处置方式完全不同。
                    hotspotError = hotspotSecurityFailure?.let { "缺少权限（$it）" }
                        ?: "加入对方热点失败"
                    failures += "（热点）" to java.io.IOException(
                        if (hotspotSecurityFailure != null) {
                            "$hotspotError（系统没有给 Wi-Fi 权限）"
                        } else {
                            "$hotspotError（系统拒绝确认框，或对方热点已关闭）"
                        },
                    )

                    // 第二手段：登记成系统"网络建议"，让系统自己去关联（1.5.62，旧原型的做法）。
                    // 部分 ROM 直接拒绝 WifiNetworkSpecifier 的加入请求，但认建议——
                    // 系统连上之后默认网络就是热点，按网关访问即可。
                    if (hotspotSecurityFailure == null && !suggestedHotspot &&
                        HotspotSuggester.add(context, hotspot.ssid, hotspot.passphrase)
                    ) {
                        suggestedHotspot = true
                        _state.update { it.copy(message = "已登记热点，等系统加入…") }

                        val joined = awaitSuggestedHotspot(payload, hotspot)
                        if (joined != null) {
                            connected = joined.first
                            connectedHost = joined.second
                            peer = joined.third
                            viaHotspot = true
                            hotspotError = null
                            DirectLog.i("系统按建议连上了热点 ${hotspot.ssid}")
                        }
                    }

                    // 兜底：有些 ROM 不给应用自动加入，但用户可以在系统设置里手动连。
                    // 手动连上之后默认网络就是那个热点，网关就是对方手机 —— 直接试一次（1.5.53）。
                    if (connected == null) {
                        val manualHosts = listOfNotNull(
                        hotspot.address.takeIf { it.isNotBlank() },
                        defaultGateway(),
                    ).distinct()

                    for (host in manualHosts) {
                        val client = DirectClient(
                            context,
                            DirectClient.Endpoint(host, payload.port, payload.fp, payload.token),
                        )

                        val result = client.status()
                        if (result.isSuccess) {
                            connected = client
                            connectedHost = host
                            peer = result.getOrNull()
                            viaHotspot = true
                            break
                        }
                        client.shutdown()
                        }
                    }
                }
            }

            // 热点没成（没开 / 系统拒绝 / 地址不通）再退回普通 Wi-Fi。
            // 这时热点网段的地址一定不可达，跳过它们，别让每个都白等一轮超时。
            // 快路径命中的地址排最前：TCP 通了不代表 TLS/口令都对，先拿它试一次。
            val fallback = if (hotspot != null && !viaHotspot && fastHost == null) {
                val subnet = hotspot.address.substringBeforeLast('.', "")
                if (subnet.isEmpty()) {
                    candidates
                } else {
                    candidates.filterNot { it.startsWith("$subnet.") }
                }
            } else {
                candidates
            }

            val ordered = if (fastHost != null) {
                listOf(fastHost) + fallback.filterNot { it == fastHost }
            } else {
                fallback
            }

            if (connected == null) {
                for (host in ordered) {
                    val candidate = DirectClient(
                        context,
                        DirectClient.Endpoint(host, payload.port, payload.fp, payload.token),
                    )

                    val result = candidate.status()
                    if (result.isSuccess) {
                        connected = candidate
                        connectedHost = host
                        peer = result.getOrNull()
                        break
                    }

                    val error = result.exceptionOrNull()
                    if (error != null) {
                        failures += host to error
                    }
                    candidate.shutdown()
                }
            }

            if (connected == null || peer == null) {
                // **把失败写进 debug 日志**。
                // 以前只写了 android.util.Log.w（logcat），用户导出的 debug*.log 里
                // 失败是空白的——"同一 Wi-Fi 连不上"到底是被路由器隔离（ConnectException）、
                // 还是地址不可达（SocketTimeout / NoRouteToHost），从日志上分辨不出来。
                com.wirelesscable.app.core.direct.DirectLog.w(
                    "扫码连接失败 候选=${candidates.joinToString(",")} " +
                        "失败明细=" + failures.joinToString(" | ") { (host, error) ->
                            "$host:${error.javaClass.simpleName}(${error.message ?: "-"})"
                        },
                )

                // 挑"最有信息量"的那条：连上了但口令不对 > 握手失败 > 其它
                val chosen = failures.firstOrNull {
                    it.second is DirectHttpException && (it.second as DirectHttpException).statusCode == 401
                } ?: failures.firstOrNull { it.second is javax.net.ssl.SSLException }
                    ?: failures.lastOrNull()

                val hint = if (payload.hotspot == null) {
                    "；还连不上就让对方在直连页打开「用热点直连」再出码"
                } else {
                    "；也可以把两台手机都连到电脑，用「发送到手机」中转"
                }

                // 缺权限时不要"等网络"：得先去把权限开了，等是等不来的。
                val canWait = hotspot != null && fastHost == null && hotspotSecurityFailure == null

                _state.update {
                    it.copy(
                        busy = false,
                        hotspotFallback = payload.hotspot?.let { h ->
                            HotspotFallback(h.ssid, h.passphrase)
                        },
                        waitingForNetwork = canWait,
                        message = buildString {
                            append(joinFailureText(chosen?.second, chosen?.first ?: payload.host))
                            if (canWait) {
                                append("；在系统 WLAN 里连上「${payload.hotspot?.ssid}」后会自动继续")
                            } else {
                                append(hint)
                            }
                        },
                        permissionIssue = if (hotspot != null && hotspotSecurityFailure != null) {
                            HotspotPermissions.missingText(context)
                        } else {
                            null
                        },
                    )
                }

                if (canWait) {
                    startWaitingForHotspot(payload)
                }
                return@launch
            }

            client = connected

            // 扫码方同样要禁用 Wi-Fi 省电：接收大文件时，省电会把 RTT 拉到 200 ms 以上，
            // 接收窗口跟着塌，吞吐掉到几十 KB/s（1.5.15）
            DirectSessionManager.holdClientWifi(context)

            startPeerPolling(connected)

            val files = connected.shared().getOrDefault(emptyList())

            com.wirelesscable.app.core.direct.DirectLog.session(
                // 措辞要准：viaHotspot 表示"用的是码里带的热点配置"，
                // 不是"对端网络类型"。对方开着热点但码里没带配置时，扫码方照样
                // 直连对方热点地址成功——那不算 viaHotspot，写成"普通 Wi-Fi"会误导排查。
                "扫码连上 ${peer.deviceName} " +
                    "路径=${if (viaHotspot) "码内热点配置" else "码内普通地址"} " +
                    "实际地址=${connectedHost ?: "?"} 二维码host=${payload.host} port=${payload.port}",
            )

            _state.update {
                it.copy(
                    joined = true,
                    busy = false,
                    connected = true,
                    viaHotspot = viaHotspot,
                    peerHasHotspot = payload.hotspot != null,
                    hotspotFallback = if (payload.hotspot != null && !viaHotspot) {
                        HotspotFallback(payload.hotspot.ssid, payload.hotspot.passphrase)
                    } else {
                        null
                    },
                    peerName = peer.deviceName.orEmpty().ifBlank { "对方手机" },
                    peerFiles = files,
                    linkMeasuring = true,
                    waitingForNetwork = false,
                    // 热点没连上要写在脸上：否则用户以为开了开关就是走热点，还在纳闷为什么慢
                    message = if (hotspot != null && !viaHotspot) {
                        "热点没连上（${hotspotError ?: "原因未知"}），已改用普通 Wi-Fi，可能会慢"
                    } else {
                        null
                    },
                    // 是因为缺权限才退回普通 Wi-Fi？那就把「去开权限」摆在页面上，
                    // 否则用户只会看到"很慢"，永远不知道差一个开关。
                    permissionIssue = if (hotspot != null && !viaHotspot && hotspotSecurityFailure != null) {
                        HotspotPermissions.missingText(context)
                    } else {
                        null
                    },
                )
            }

            // 走的是热点：挂一条常驻的"无外网 Wi-Fi"请求，息屏/系统切网时不会把网卡收走
            if (viaHotspot && hotspot != null) {
                startHotspotPin(hotspot)
            }

            measureLink(connected, payload)
        }
    }

    /**
     * 手动连好对方热点之后，从头再连一次。
     *
     * 有些 ROM 就不允许应用自动加入热点（`WifiNetworkSpecifier` 直接 `onUnavailable`），
     * 但在系统设置里手动连是通的。这条路径不依赖任何 ROM 的"自动加入"能力（1.5.54）。
     */
    fun retryHotspot() {
        val payload = _state.value.payload
        if (payload == null) {
            _state.update { it.copy(message = "没有可重试的会话，请重新扫码") }
            return
        }

        // 用户在应用设置里开了权限再回来点重试，这里要能过；
        // 没开就继续把出口留在页面上，而不是再悄悄失败一次。
        if (payload.hotspot != null && !HotspotPermissions.canUseHotspot(context)) {
            notifyHotspotPermissionMissing(HotspotPermissions.missingText(context).orEmpty())
            return
        }

        joinJob?.cancel()
        joinJob = null
        peerJob?.cancel()
        peerJob = null
        clearHotspotSideEffects()
        client?.shutdown()
        client = null

        _state.update {
            it.copy(
                joined = false,
                connected = false,
                busy = false,
                peerFiles = emptyList(),
                viaHotspot = false,
                progress = 0f,
                message = "正在重试…",
                permissionIssue = null,
            )
        }

        join(payload)
    }

    /**
     * 连上后立刻量一下这条链路。
     *
     * 为什么一连接就测：两机直连踩过太多次"能连上但传不动"。等用户点了 1 GB
     * 才发现只有 40 KB/s，已经浪费了他的时间。这个数字直接写在页面上，
     * 让他决定是继续传还是换网络（1.5.19）。
     */
    private fun measureLink(current: DirectClient, payload: DirectPayload) {
        viewModelScope.launch {
            // 正在传文件时不测：两个大流量会互相抢，测出来的数也不准
            if (_state.value.busy) {
                _state.update { it.copy(linkMeasuring = false) }
                return@launch
            }

            var result = runCatching { current.measureLink() }.getOrNull()

            // 首个请求要新建连接（含 TLS 握手），这条链路上第一次经常失败；
            // 隔一下再试一次，仍失败才判"不通"
            if (result?.isFailure == true) {
                kotlinx.coroutines.delay(800)
                result = runCatching { current.measureLink() }.getOrNull()
            }

            val speed = result?.getOrNull()

            _state.update {
                it.copy(
                    linkMeasuring = false,
                    linkBytesPerSecond = speed,
                    linkFailed = result?.isFailure == true,
                )
            }

            // 实测下来这条链路根本不能传（现场是 1 KB/s），而对方开着热点、这次还没走热点：
            // 直接换路，别让用户手动重来一次。1.5.63
            //
            // 先确认这个回调还属于**当前这次会话**：测速要十几秒，用户完全可能在这期间
            // 退出重扫。不比对 payload 的话，旧协程会把新会话踢下来重连（1.5.64 复查）。
            if (_state.value.payload !== payload || !_state.value.joined) {
                return@launch
            }

            if ((speed == null || speed < LINK_USABLE_MIN_BPS) && !_state.value.busy) {
                switchToHotspot(describeSpeed(speed))
            }
        }
    }

    /** 给"链路有多糟"一句人话：`null` 表示连一个字节都没收到。 */
    private fun describeSpeed(speed: Long?): String = when {
        speed == null -> "测速失败"
        speed < 1024 -> "${speed} B/s"
        else -> "${speed / 1024} KB/s"
    }

    /**
     * 实测吞吐太低时自动改连对方热点。
     *
     * 只会发生一次（[hotspotAutoSwitchTried]），避免在两条都坏的链路之间来回跳。
     * 没开热点就没得换，只留一句提示——总不能凭空造一条快链路。
     */
    private fun switchToHotspot(reason: String) {
        val payload = _state.value.payload ?: return
        if (hotspotAutoSwitchTried || _state.value.viaHotspot) {
            return
        }

        val hotspot = payload.hotspot
        if (hotspot == null) {
            DirectLog.w("链路不可用（$reason），但对方没有开热点")
            _state.update { it.copy(message = "当前网络太慢（$reason），让对方在直连页打开「用热点直连」再出码") }
            return
        }

        hotspotAutoSwitchTried = true
        DirectLog.w("链路不可用（$reason），自动改连对方热点 ${hotspot.ssid}")

        // 先拆干净：不清掉旧 client 的话，下面的 join 会被 joined/joinJob 挡住
        joinJob?.cancel()
        joinJob = null
        peerJob?.cancel()
        peerJob = null
        client?.shutdown()
        client = null
        clearHotspotSideEffects()

        _state.update {
            it.copy(
                joined = false,
                connected = false,
                busy = false,
                linkMeasuring = false,
                linkFailed = false,
                linkBytesPerSecond = null,
                viaHotspot = false,
                message = "当前网络太慢（$reason），正在改连对方热点…",
            )
        }

        join(payload, forceHotspot = true)
    }

    /**
     * 对方热点这一侧的网关地址。
     *
     * AP 网关就是出码方手机本身。各家 ROM 上报的"AP 侧地址"不一样——一加报
     * `192.168.210.2`，多数报 `.1`，也有干脆不返回的。但**扫码方加入热点之后**，
     * 自己的 `LinkProperties` 里一定有正确的网关，用它连一定通，不用猜网段（1.5.53）。
     */
    private fun hotspotGateway(network: Network): String? {
        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return null

        val properties = manager.getLinkProperties(network) ?: return null

        properties.routes
            .asSequence()
            .filter { it.isDefaultRoute }
            .mapNotNull { it.gateway as? java.net.Inet4Address }
            .firstOrNull()
            ?.let { return it.hostAddress }

        // 少数 ROM 没有默认路由：DHCP 服务器就是 AP 本身
        return properties.dhcpServerAddress?.hostAddress
    }

    /** 当前默认网络的网关：用户手动连上对方热点时，这个地址就是对方手机。 */
    private fun defaultGateway(): String? {
        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return null

        val network = manager.activeNetwork ?: return null
        val properties = manager.getLinkProperties(network) ?: return null

        return properties.routes
            .firstOrNull { it.isDefaultRoute }
            ?.gateway?.hostAddress
    }

    /**
     * 加入对方开的热点，返回那个 `Network`（请求只走它）。
     *
     * `WifiNetworkSpecifier` 会弹一次系统确认框，这是系统行为；连上后进程仍然走
     * 原来的默认网络，所以必须把返回的 `network.socketFactory` 交给 [DirectClient]，
     * 否则请求会从原 Wi-Fi 发出去、直接超时。
     */
    private suspend fun requestHotspotNetwork(hotspot: DirectHotspot): Network? {
        hotspotSecurityFailure = null

        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return null

        val spec = runCatching {
            WifiNetworkSpecifier.Builder()
                .setSsid(hotspot.ssid)
                .setWpa2Passphrase(hotspot.passphrase)
                .build()
        }.onFailure {
            com.wirelesscable.app.core.direct.DirectLog.w("构造热点请求失败：${it.message}")
        }.getOrNull() ?: return null

        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            // 热点没有互联网：不显式去掉 INTERNET，系统会认为这个网络不满足请求，
            // 于是一直不回调 onAvailable，最后只剩超时 —— 然后"热点没连上、退回普通 Wi-Fi"。
            // 最早的原型（com.nearshare.wifi.NetworkJoiner）就是带了这一句，
            // 当时在 Android 11/16 上都能连上（1.5.55 从那边搬回来）。
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .setNetworkSpecifier(spec)
            .build()

        val network = withTimeoutOrNull(HOTSPOT_JOIN_TIMEOUT_MS) {
            suspendCancellableCoroutine { continuation ->
                val callback = object : ConnectivityManager.NetworkCallback() {
                    override fun onAvailable(network: Network) {
                        if (continuation.isActive) {
                            continuation.resume(network)
                        }
                    }

                    override fun onUnavailable() {
                        if (continuation.isActive) {
                            continuation.resume(null)
                        }
                    }
                }

                hotspotCallback = callback

                runCatching { manager.requestNetwork(request, callback) }
                    .onFailure {
                        // 13+ 没有 NEARBY_WIFI_DEVICES 时这里抛 SecurityException，
                        // 以前被静默吞掉 —— 现象就是"热点永远连不上"却没有任何日志（1.5.56）
                        if (it is SecurityException) {
                            hotspotSecurityFailure = it.message ?: "SecurityException"
                        }
                        com.wirelesscable.app.core.direct.DirectLog.w(
                            "requestNetwork 失败：${it.javaClass.simpleName}: ${it.message}",
                        )
                        if (continuation.isActive) continuation.resume(null)
                    }

                // 超时/取消必须注销：否则请求还挂在系统里，稍后真连上了也没人处理，
                // 那条热点网络会一直被应用占着（1.5.55）
                continuation.invokeOnCancellation {
                    runCatching { manager.unregisterNetworkCallback(callback) }
                }
            }
        }

        if (network == null) {
            hotspotCallback?.let { runCatching { manager.unregisterNetworkCallback(it) } }
            hotspotCallback = null
            return null
        }

        // 这里**不再** bindProcessToNetwork。
        //
        // 连上无外网的热点后系统仍把移动数据/原 Wi-Fi 当默认网络，所以每个 socket 必须
        // 显式走这条网络——这个由调用方传 network.socketFactory 给 DirectClient 完成。
        // 以前在这里做进程级绑定，代价是直连的整个会话期间，应用跟电脑的心跳、索引请求
        // 也全被绑到没有外网的热点上，电脑端直接显示"离线"（1.5.58）。
        // 只有 socketFactory 确实走不通时才会临时绑一次，见 join()。

        return network
    }

    /**
     * 进程级绑定热点网络（只在 [join] 的兜底分支里短暂使用）。
     *
     * 传 null 即解绑。绑上之后整个进程的默认网络都会变，所以必须成对调用。
     */
    private fun bindProcessNetwork(network: Network?) {
        runCatching {
            (context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager)
                ?.bindProcessToNetwork(network)
        }.onFailure {
            com.wirelesscable.app.core.direct.DirectLog.w("绑定/解绑进程网络失败：${it.message}")
        }
    }

    /**
     * 注销热点请求（幂等）。
     *
     * 注册着的 NetworkCallback 就是"我要这个热点"的凭据：不注销，系统会一直保留那条网络。
     * 重连/重试/退出之前都要过一遍，否则第二次请求会把第一次的 callback 覆盖掉，
     * 旧网络再也没人放（1.5.57）。
     */
    private fun releaseHotspotCallback() {
        val callback = hotspotCallback ?: return
        hotspotCallback = null
        runCatching {
            (context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager)
                ?.unregisterNetworkCallback(callback)
        }
    }

    /**
     * 选路探测：这条地址在**真的传数据**时能不能用。
     *
     * 只测 TCP 连接是 1.5.62 的教训（K105：能连、status 秒回，传数据 1 KB/s）。
     * 这里用 [DirectClient.quickProbe] 读满 48 KiB 才算通过，读不满返回 null。
     *
     * 代价：好链路几十毫秒，坏链路最多 2.5 秒 —— 后者本来就是我们要识别的情况。
     */
    private suspend fun probeUsable(host: String, payload: DirectPayload): Long? {
        val client = DirectClient(
            context,
            DirectClient.Endpoint(host, payload.port, payload.fp, payload.token),
        )

        return try {
            client.quickProbe(PROBE_MIN_BYTES, PROBE_BUDGET_MS)
        } finally {
            client.shutdown()
        }
    }

    /**
     * 登记网络建议之后，等系统自己把手机连上热点。
     *
     * 连上之后默认网络就是热点，用普通客户端按网关访问即可（此时不需要 socketFactory）。
     */
    private suspend fun awaitSuggestedHotspot(
        payload: DirectPayload,
        hotspot: DirectHotspot,
    ): Triple<DirectClient, String, DirectStatus>? = withContext(Dispatchers.IO) {
        val subnet = hotspot.address.substringBeforeLast('.', "")
        val hosts = buildList {
            add(hotspot.address)
            if (subnet.isNotEmpty()) add("$subnet.1")
            defaultGateway()?.let { add(it) }
        }.filter { it.isNotBlank() }.distinct()

        val deadline = android.os.SystemClock.elapsedRealtime() + SUGGESTION_JOIN_TIMEOUT_MS
        while (android.os.SystemClock.elapsedRealtime() < deadline) {
            for (host in hosts) {
                val client = DirectClient(
                    context,
                    DirectClient.Endpoint(host, payload.port, payload.fp, payload.token),
                )
                val status = client.status().getOrNull()
                if (status != null) {
                    return@withContext Triple(client, host, status)
                }
                client.shutdown()
            }
            delay(1_500)
        }

        DirectLog.w("登记建议后 ${SUGGESTION_JOIN_TIMEOUT_MS / 1000} 秒仍未连上 ${hotspot.ssid}")
        null
    }

    /**
     * 加入失败后不判死：等用户去系统 WLAN 里手动连上对方热点，连上就自动继续。
     *
     * 旧原型的 `ReceiveState.WaitingForNetwork` 就是这个行为。中间有一版改成静默退回
     * 普通 Wi-Fi，用户只能干等——而热点没连上时普通 Wi-Fi 往往也传不动（1.5.62 恢复）。
     */
    private fun startWaitingForHotspot(payload: DirectPayload) {
        val hotspot = payload.hotspot ?: return
        waitJob?.cancel()

        waitJob = viewModelScope.launch {
            val subnet = hotspot.address.substringBeforeLast('.', "")
            val hosts = buildList {
                add(hotspot.address)
                if (subnet.isNotEmpty()) add("$subnet.1")
                defaultGateway()?.let { add(it) }
            }.filter { it.isNotBlank() }.distinct()

            DirectLog.i("等待手动连上热点 ${hotspot.ssid}，候选=$hosts")
            val deadline = android.os.SystemClock.elapsedRealtime() + WAIT_FOR_NETWORK_MS

            while (isActive && android.os.SystemClock.elapsedRealtime() < deadline) {
                delay(2_000)
                val reachable = hosts.any { probeUsable(it, payload) != null }
                if (reachable) {
                    DirectLog.i("检测到热点已连通，自动继续")
                    _state.update { it.copy(waitingForNetwork = false) }
                    // 上一轮 join 已经走到失败分支，这里把引用清掉再重来
                    joinJob = null
                    join(payload)
                    return@launch
                }
            }

            if (isActive) {
                DirectLog.w("等待网络超时（${WAIT_FOR_NETWORK_MS / 1000} 秒）")
                _state.update { it.copy(waitingForNetwork = false) }
            }
        }
    }

    /** 热点会话期间挂一条常驻请求，防止系统因为"这张 Wi-Fi 没外网"把它回收。 */
    private fun startHotspotPin(hotspot: DirectHotspot) {
        stopHotspotPin()

        val subnet = hotspot.address.substringBeforeLast('.', "")
        if (subnet.isEmpty()) return

        val pin = HotspotNetworkPin(
            context = context,
            subnetPrefix = subnet,
            onLost = {
                DirectLog.w("热点网络被系统回收")
                _state.update { it.copy(message = "热点网络已断开，正在重连…") }
            },
        )
        pin.start()
        hotspotPin = pin
        DirectLog.i("已挂上热点保活请求 subnet=$subnet")
    }

    private fun stopHotspotPin() {
        hotspotPin?.stop()
        hotspotPin = null
    }

    /** 退出 / 重试 / 页面销毁时，把等待任务、保活请求、网络建议一起收掉。 */
    private fun clearHotspotSideEffects() {
        waitJob?.cancel()
        waitJob = null
        stopHotspotPin()

        if (suggestedHotspot) {
            HotspotSuggester.remove(context)
            suggestedHotspot = false
        }
    }

    /**
     * 连不上时给一句能定位的话。
     *
     * 以前只拼了 `exception.message`，而 SSL/IO 异常在 Android 上常常是 null，
     * 界面就显示"握手失败：null"——等于什么都没说。现在退化到类名，并按类型给方向。
     */
    private fun joinFailureText(error: Throwable?, host: String): String {
        if (error == null) {
            return "连不上对方"
        }

        // 界面上只给一句话，完整栈留给 logcat（adb logcat -s WcDirect）；
        // 只要了 debug 日志的用户也能看到异常类型与消息（否则现场日志一片空白）。
        android.util.Log.w("WcDirect", "直连失败 host=$host", error)
        com.wirelesscable.app.core.direct.DirectLog.w(
            "直连失败 host=$host 异常=${error.javaClass.name} 消息=${error.message ?: "-"}" +
                (error.cause?.let { " 原因=${it.javaClass.simpleName}(${it.message ?: "-"})" } ?: ""),
        )

        val detail = error.message?.takeIf { it.isNotBlank() }
            ?: error.cause?.message?.takeIf { it.isNotBlank() }
            ?: error.javaClass.simpleName

        return when {
            error is DirectHttpException && error.statusCode == 401 ->
                "会话已失效，请让对方重新出码"

            error is javax.net.ssl.SSLException ->
                "握手失败：$detail（对方的码可能已经换过，重新扫一次）"

            // 三种失败的原因完全不同，混成一句"不在同一网络"会把排查带偏：
            // ConnectException 是端口没人接（对方停了出码，或者 Wi-Fi 开了客户端隔离，
            // 能 ping 通但 TCP 入站被拒）；超时/不可达才更像网络本身不通（1.5.9 实测）。
            error is java.net.ConnectException ->
                "连不上 $host：对方已停止出码，或这个 Wi-Fi 禁止设备互访（可换热点再试）"

            // 「超时」= SYN 发出去**没有任何回应**（网络层丢包），
            // 这和「连接被拒绝」是两回事：后者说明包到了、只是端口没人接。
            //
            // 而且**必须先判断同网段**：两台手机都在 192.168.158.x 时还在说
            // "可能不在同一网络"，等于把用户往错误方向带——他去看手机网络设置，
            // 可两台明明连的是同一个 Wi-Fi。真正的原因几乎都是路由器那一侧
            // （AP 隔离 / 客户端隔离 / 访客网络），或者连了不同的 AP。
            error is java.net.SocketTimeoutException ->
                if (LocalSubnet.isSameSubnet(host)) {
                    "连不上 $host：连接超时。两台手机在同一网段，但对端收不到请求——" +
                        "多半是路由器开了「AP 隔离 / 客户端隔离」，或其中一台连的是访客网络。" +
                        "可在对方直连页打开「用热点直连」再出码"
                } else {
                    "连不上 $host：连接超时，两台手机可能不在同一网络（一个连 Wi-Fi、一个连热点或流量？）"
                }

            error is java.net.NoRouteToHostException ||
                error is java.net.UnknownHostException ->
                if (LocalSubnet.isSameSubnet(host)) {
                    "连不上 $host：地址不可达（同一网段内被阻断，常见于路由器隔离）"
                } else {
                    "连不上 $host：地址不可达，两台手机不在同一网络"
                }

            // TCP/TLS 都通了，但对端在回响应前把连接关掉：最常见是二维码过期
            // （对方已经结束会话），其次是对方退出了出码页。
            detail.contains("unexpected end of stream", ignoreCase = true) ||
                detail.contains("Connection reset", ignoreCase = true) ||
                detail.contains("Broken pipe", ignoreCase = true) ->
                "对方断开了连接，二维码可能已过期，请让对方重新出码"

            else -> "连不上 $host：$detail（${error.javaClass.simpleName}）"
        }
    }

    /**
     * 定时拉对方的共享清单。
     *
     * 出码方可以随时"再发一个文件"，而扫码方这边没有推送通道；
     * 不轮询的话，对方发了文件这边要退出重连才看得到（5 秒一次，会话只有几分钟）。
     */
    private fun startPeerPolling(current: DirectClient) {
        peerJob?.cancel()
        peerJob = viewModelScope.launch {
            var failures = 0

            while (true) {
                kotlinx.coroutines.delay(5_000)

                val result = current.shared()
                if (result.isSuccess) {
                    failures = 0
                    val files = result.getOrDefault(emptyList())
                    if (files != _state.value.peerFiles) {
                        _state.update { it.copy(peerFiles = files) }
                    }
                    continue
                }

                // 单次失败可能只是抖一下；连续三次（15 秒）说明对面已经不在了。
                // 以前这里是 `?: continue`，对方关掉后这边永远停在"已连接"（1.5.14）
                failures++
                if (failures >= 3) {
                    android.util.Log.i("WcDirect", "对方连续 3 次无响应，判定已断开")
                    current.shutdown()
                    client = null
                    clearHotspotSideEffects()
                    _state.update {
                        it.copy(
                            joined = false,
                            connected = false,
                            peerFiles = emptyList(),
                            sendingName = null,
                            progress = 0f,
                            message = "对方已断开，会话结束",
                        )
                    }
                    break
                }
            }
        }
    }

    fun leave() {
        joinJob?.cancel()
        joinJob = null
        peerJob?.cancel()
        peerJob = null
        releaseHotspotCallback()
        clearHotspotSideEffects()
        hotspotSecurityFailure = null
        downloadedPaths.clear()
        DirectSessionManager.releaseClientWifi()
        client?.shutdown()
        client = null
        _state.update {
            State(message = "已断开，需要时重新扫码")
        }
    }

    /** 拉取对方共享的一个文件。 */
    fun download(item: DirectFileDto) {
        val current = client ?: return
        if (_state.value.busy) {
            return
        }

        lastProgressAt = android.os.SystemClock.elapsedRealtime()
        com.wirelesscable.app.core.direct.DirectLog.i(
            "下载请求 name=${item.name} id=${item.id} 大小=${item.sizeBytes}",
        )
        _state.update {
            it.copy(
                busy = true,
                message = null,
                sendingName = item.name,
                progress = 0f,
                sentBytes = 0L,
                totalBytes = item.sizeBytes,
                stalled = false,
            )
        }

        viewModelScope.launch {
            // 断流时进度条一动不动，用户只能猜。超过 5 秒没有新字节就换一句提示，
            // 让"正在重试"和"程序死了"能区分开（1.5.48 现场）。
            val stallWatch = launch {
                while (isActive) {
                    delay(1_000)
                    val idleMs = android.os.SystemClock.elapsedRealtime() - lastProgressAt
                    val stalled = idleMs > STALL_HINT_MS
                    if (_state.value.stalled != stalled) {
                        _state.update { it.copy(stalled = stalled) }
                    }
                }
            }

            val directory = File(InboxStore.directory(), InboxStore.DIRECT_SUBDIR)
            val result = current.download(item, directory) { sent, total ->
                lastProgressAt = android.os.SystemClock.elapsedRealtime()
                if (total > 0) {
                    _state.update {
                        it.copy(
                            progress = sent.toFloat() / total,
                            sentBytes = sent,
                            totalBytes = total,
                            stalled = false,
                        )
                    }
                }
            }

            stallWatch.cancel()

            if (result.isSuccess) {
                result.getOrNull()?.let { file -> downloadedPaths[item.id] = file.absolutePath }
                _state.update {
                    it.copy(
                        busy = false,
                        stalled = false,
                        sendingName = null,
                        progress = 0f,
                        downloaded = it.downloaded + item.id,
                        message = "已保存到「${InboxStore.DISPLAY_NAME}/直连」",
                    )
                }
            } else {
                _state.update {
                    it.copy(
                        busy = false,
                        stalled = false,
                        sendingName = null,
                        progress = 0f,
                        message = "下载失败：${result.exceptionOrNull()?.message}",
                    )
                }
            }
        }
    }

    /** 把我这边的文件推给对方。 */
    fun send(uri: Uri) {
        val current = client ?: return
        if (_state.value.busy) {
            return
        }

        viewModelScope.launch {
            // ContentResolver 查询会真的去问 ContentProvider，SAF 首次访问能卡几秒。
            // 这段原来跑在主线程上，表现就是"点了发送，界面僵住好久"（1.5.13）。
            val meta = withContext(Dispatchers.IO) { queryMeta(uri) }
            if (meta == null) {
                _state.update { it.copy(message = "读不到这个文件的信息") }
                return@launch
            }

            val (name, size) = meta
            if (size > DirectProtocol.MAX_FILE_BYTES) {
                _state.update { it.copy(message = "文件太大（超过 2 GiB）") }
                return@launch
            }

            _state.update {
                it.copy(busy = true, sendingName = name, progress = 0f, message = "正在发送…")
            }

            val result = current.upload(uri, name, size) { sent, total ->
                if (total > 0) {
                    _state.update {
                        it.copy(progress = sent.toFloat() / total, sentBytes = sent, totalBytes = total)
                    }
                }
            }

            _state.update {
                it.copy(
                    busy = false,
                    sendingName = null,
                    progress = 0f,
                    message = if (result.isSuccess) "已发送「$name」" else "发送失败：${result.exceptionOrNull()?.message}",
                )
            }
        }
    }

    // ------------------------------------------------------------ 应用

    /**
     * 出码方：把选中的应用加进共享清单。
     *
     * 一个应用可能是一组分体包，全部按 `group=包名` 登记，对方才知道要一起装。
     */
    fun addApps(apps: List<InstalledApp>) {
        if (!_state.value.hosting || apps.isEmpty()) {
            return
        }

        viewModelScope.launch {
            _state.update { it.copy(busy = true) }

            val result = DirectSessionManager.addApps(apps)
            val added = result.getOrDefault(0)
            val parts = apps.sumOf { it.parts.size }

            _state.update {
                it.copy(
                    busy = false,
                    shared = DirectSessionManager.state.value.shared,
                    message = when {
                        result.isFailure -> "添加失败：${result.exceptionOrNull()?.message}"
                        added == 0 -> "这些应用读不出 APK（系统限制），换一个试试"
                        added < parts -> "已加 $added 个文件（${apps.size} 个应用，部分分体读不到）"
                        else -> "已加 ${apps.size} 个应用，对方马上就能看到"
                    },
                )
            }
        }
    }

    /**
     * 扫码方：把选中的应用推给对方。
     *
     * 分体包要**顺序**发：并行会把对方的收件目录写乱，也没法保证 base 先到。
     */
    fun sendApps(apps: List<InstalledApp>) {
        val current = client ?: return
        if (_state.value.busy || apps.isEmpty()) {
            return
        }

        viewModelScope.launch {
            var sent = 0
            var failed = 0
            val totalParts = apps.sumOf { it.parts.size }

            for (app in apps) {
                for ((index, part) in app.parts.withIndex()) {
                    val name = InstalledApps.partName(app.label, app.packageName, index)
                    val showName = if (app.parts.size > 1) {
                        "${app.label}（${index + 1}/${app.parts.size}）"
                    } else {
                        app.label
                    }

                    _state.update {
                        it.copy(
                            busy = true,
                            sendingName = showName,
                            progress = 0f,
                            sentBytes = 0L,
                            totalBytes = part.sizeBytes,
                            message = "正在发送应用 ${sent + failed + 1}/$totalParts",
                        )
                    }

                    val result = current.upload(
                        uri = Uri.fromFile(File(part.path)),
                        name = name,
                        sizeBytes = part.sizeBytes,
                        apkGroup = app.packageName,
                        apkKind = if (index == 0) {
                            DirectProtocol.KIND_APK_BASE
                        } else {
                            DirectProtocol.KIND_APK_SPLIT
                        },
                    ) { done, total ->
                        if (total > 0) {
                            _state.update {
                                it.copy(
                                    progress = done.toFloat() / total,
                                    sentBytes = done,
                                    totalBytes = total,
                                )
                            }
                        }
                    }

                    if (result.isSuccess) sent++ else failed++
                }
            }

            _state.update {
                it.copy(
                    busy = false,
                    sendingName = null,
                    progress = 0f,
                    message = when {
                        failed == 0 -> "已发送 ${apps.size} 个应用"
                        sent == 0 -> "发送应用失败：${_state.value.message ?: "对方没有响应"}"
                        else -> "部分失败：$sent 个文件已发，$failed 个失败"
                    },
                )
            }
        }
    }

    /**
     * 扫码方：只点一下就把整组应用拿到本地并安装。
     *
     * 已经下过的分体不重复下（`downloadedPaths`）；缺哪个补哪个，然后一起交给安装器。
     */
    fun installPeerApp(group: String) {
        val current = client ?: return
        if (_state.value.busy) {
            return
        }

        viewModelScope.launch {
            val parts = _state.value.peerFiles
                .filter { it.group == group }
                .sortedBy { if (it.kind == DirectProtocol.KIND_APK_BASE) 0 else 1 }

            if (parts.isEmpty()) {
                _state.update { it.copy(message = "这组应用已经不在了，让对方重新发送") }
                return@launch
            }

            _state.update { it.copy(busy = true, message = null) }

            val files = mutableListOf<File>()
            val directory = File(InboxStore.directory(), InboxStore.DIRECT_SUBDIR)

            for (item in parts) {
                val cached = downloadedPaths[item.id]
                    ?.let(::File)
                    ?.takeIf { it.isFile && it.length() == item.sizeBytes }
                if (cached != null) {
                    files += cached
                    continue
                }

                _state.update {
                    it.copy(sendingName = item.name, progress = 0f, totalBytes = item.sizeBytes)
                }

                val result = current.download(item, directory) { done, total ->
                    lastProgressAt = android.os.SystemClock.elapsedRealtime()
                    if (total > 0) {
                        _state.update {
                            it.copy(progress = done.toFloat() / total, sentBytes = done, totalBytes = total)
                        }
                    }
                }

                val file = result.getOrNull()
                if (file == null) {
                    _state.update {
                        it.copy(
                            busy = false,
                            sendingName = null,
                            progress = 0f,
                            message = "下载「${item.name}」失败：${result.exceptionOrNull()?.message}",
                        )
                    }
                    return@launch
                }

                downloadedPaths[item.id] = file.absolutePath
                _state.update { it.copy(downloaded = it.downloaded + item.id) }
                files += file
            }

            _state.update { it.copy(busy = false, sendingName = null, progress = 0f) }
            startInstall(files, group)
        }
    }

    /** 出码方：点一下就用系统默认应用打开对方推过来的文件。 */
    fun openReceived(file: DirectSession.ReceivedFile) {
        val path = file.path
        if (path.isNullOrBlank()) {
            _state.update { it.copy(message = "找不到这个文件（可能已被移走）") }
            return
        }

        FileOpener.open(context, File(path))?.let { message ->
            _state.update { it.copy(message = message) }
        }
    }

    /** 出码方：安装对方推过来的应用（分体包按 group 归在一起）。 */
    fun installReceivedApp(group: String) {
        if (_state.value.busy) {
            return
        }

        val parts = _state.value.received
            .filter { it.group == group }
            .sortedBy { if (it.kind == DirectProtocol.KIND_APK_BASE) 0 else 1 }
            .mapNotNull { it.path?.let(::File)?.takeIf { file -> file.isFile } }

        if (parts.isEmpty()) {
            _state.update { it.copy(message = "找不到安装包，可能已被移走") }
            return
        }

        startInstall(parts, group)
    }

    /** 交给系统安装器。没有「安装未知应用」权限时先去开。 */
    private fun startInstall(files: List<File>, group: String) {
        if (!ApkInstaller.canInstall(context)) {
            _state.update {
                it.copy(message = "需要「安装未知应用」权限：打开开关后回来再点一次安装")
            }
            ApkInstaller.requestPermission(context)
            return
        }

        val error = ApkInstaller.install(context, files)
        _state.update {
            it.copy(
                message = if (error == null) {
                    "已提交安装：$group（系统会再弹一次确认）"
                } else {
                    "安装失败：$error"
                },
            )
        }
    }

    fun clearMessage() {
        _state.update { it.copy(message = null) }
    }

    private fun queryMeta(uri: Uri): Pair<String, Long>? {
        val projection = arrayOf(
            android.provider.OpenableColumns.DISPLAY_NAME,
            android.provider.OpenableColumns.SIZE,
        )

        context.contentResolver.query(uri, projection, null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                val sizeIndex = cursor.getColumnIndex(android.provider.OpenableColumns.SIZE)
                val name = if (nameIndex >= 0 && !cursor.isNull(nameIndex)) {
                    cursor.getString(nameIndex)
                } else {
                    uri.lastPathSegment ?: "file"
                }
                val size = if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) cursor.getLong(sizeIndex) else -1L
                if (size >= 0) {
                    return name to size
                }
            }
        }

        return null
    }

    override fun onCleared() {
        watchJob?.cancel()
        // 页面被系统回收时把热点请求放掉；进程级绑定只在 join 的兜底分支里短暂出现，
        // 那里有 try/finally 保证解绑，这里不用再动全局网络。
        releaseHotspotCallback()
        clearHotspotSideEffects()
        DirectSessionManager.releaseClientWifi()
        client?.shutdown()
        if (_state.value.hosting) {
            DirectSessionManager.stop()
        }
        super.onCleared()
    }
}
