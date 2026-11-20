package com.wirelesscable.app.core.sync

import android.util.Log
import com.wirelesscable.app.WirelessCableApp
import com.wirelesscable.app.core.files.FileManagerService
import com.wirelesscable.app.core.files.RootGrantManager
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.FileApi
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.network.WcApiClient
import com.wirelesscable.app.core.protocol.ErrorCodes
import com.wirelesscable.app.core.transfer.InboxReceiver
import com.wirelesscable.app.core.transfer.TransferRuntime
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * 会话级后台同步：心跳 + 索引上报 + 作业执行。
 *
 * **为什么要有它**：电脑端在「我的手机」里点删除/改名/保存时，手机多半不在文件页、
 * 甚至已经息屏。这些动作靠作业队列传递，必须有人一直在取。
 * 以前这个循环挂在文件页的 ViewModel 上（0.6.4 改成界面生命周期），
 * 界面一退到后台就停——0.6.5 把它搬到应用级，并由前台服务在息屏时保活（K43）。
 *
 * 归属用"持有者标签"管理：界面在前台时是 `ui`，前台服务在跑时是 `service`。
 * 两边都释放才真正停下，避免"退出界面把服务里的循环也停了"。
 */
object SessionSyncEngine {

    data class State(
        val running: Boolean = false,
        /** 最近一次心跳是否成功。 */
        val connected: Boolean = false,
        /** 被电脑主动断开（服务端回 423）。界面据此给「立即重连」。 */
        val paused: Boolean = false,
        /**
         * 当前会话对应的电脑 id。
         *
         * 界面判断"这台设备是不是正连着"必须用它，不能再用配对页那套
         * `PairingViewModel.sessionState`——会话是由本引擎驱动的，配对页的状态只在
         * 配对流程里更新。两者不同步时的表现就是：心跳一直 200，首页却写着"断开"，
         * 用户以为断了去点重连，把好好的会话打断（换网络后必现）。
         */
        val deviceId: String? = null,
        /** 面向用户的失败原因（连不上、授权失效等）。 */
        val lastError: String? = null,
        /** 最近一次执行作业的时间（Unix 毫秒），0 表示还没执行过。 */
        val lastJobAtMs: Long = 0,
        /** 最近一次成功上报索引的时间。 */
        val lastIndexAtMs: Long = 0,
        /** 最近一次循环心跳（每个 tick 都更新）。用来判断循环是否卡死。 */
        val lastTickAtMs: Long = 0,
    )

    private const val TAG = "WcSessionSync"

    /**
     * 取作业失败/服务端不支持长轮询时的间隔。
     *
     * 服务端支持 `?wait=` 时，请求本身会挂起 3 秒，返回即代表"这 3 秒内没有作业"，
     * 立刻再发才是对的；老服务端不认这个参数会立即返回空，必须退回到 3 秒，
     * 否则一台手机每 200 ms 打一次接口。
     */
    private const val POLL_MS = 3_000L

    /** 长轮询空返回后的间隔：几乎立刻续上，把作业延迟压到一次网络往返。 */
    private const val JOB_POLL_IDLE_MS = 200L

    /** 判断"服务端是否真的挂起了"的阈值：明显短于等待时长就是没挂。 */
    private const val LONG_POLL_THRESHOLD_MS = 1_000L
    private const val INDEX_INTERVAL_MS = 12_000L

    /**
     * 心跳间隔。10 秒（1.5.59）：桌面端把"多久没心跳算离线"从 90 秒收到 30 秒，
     * 心跳必须跟着提到 3 倍频，否则一次丢包就误判离线。
     * 相对作业长轮询（没作业时每 200 ms 一次）这点流量可以忽略。
     */
    private const val HEARTBEAT_INTERVAL_MS = 10_000L
    private const val SESSION_RETRY_MS = 5_000L

    /** 被电脑断开后的重试间隔：足够长不烧电，又能在恢复后自动接上。 */
    private const val SESSION_PAUSED_RETRY_MS = 30_000L

    /** 超过这么久没有 tick，就认为循环卡住（网络调用有 40 秒超时，留足余量）。 */
    private const val STALE_TICK_MS = 90_000L

    /** 接收协程最快多久拉起一次（它自己还有 3 秒节流）。 */
    private const val RECEIVE_SPAWN_INTERVAL_MS = 1_000L

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val holders = mutableSetOf<String>()
    private var job: Job? = null

    /**
     * 收文件跑在这条独立协程里，不占主循环。
     *
     * 一个几 GiB 的文件要传几分钟；串在 [loop] 里会让心跳停发、看门狗判定"卡死"，
     * 于是电脑端看到设备离线、正在传的协程被取消——比"晚点收到"糟得多。
     */
    private var receiveJob: Job? = null

    /** 上次尝试拉起接收协程的时间：长轮询空转时 200 ms 一轮，不必每轮都建协程。 */
    private var lastReceiveSpawnAtMs = 0L

    /**
     * 索引上报也跑在独立协程里。
     *
     * 整机根的上限是 2 万条，BFS 的每一步都是一次 SAF 调用；一次全量扫描实测要十几分钟。
     * 这条扫完之前，`runJobs` 一次都轮不到 —— 心跳还在发、界面还在刷，
     * 但 `fs/device/jobs` 请求彻底消失，电脑发什么作业手机都不理，
     * 看起来像"断连"，实际是作业轮询被索引饿死了（2026-11-20 真机定位）。
     */
    private var indexJob: Job? = null

    /** 连接失败后用来重新发现电脑（mDNS）；[initialize] 时创建。 */
    private var refresher: EndpointRefresher? = null

    private var app: WirelessCableApp? = null
    private var grants: RootGrantManager? = null

    /** 当前会话下的三个对象，按 (地址|指纹|令牌) 一起缓存。 */
    private var sessionKey: String? = null
    private var client: WcApiClient? = null
    private var service: FileManagerService? = null

    /** 选目录/撤销目录后要立刻上报一次索引，不等下一个周期。 */
    @Volatile
    private var publishRequested = false

    /**
     * 被电脑断开后到什么时候为止不再请求（Unix 毫秒）。
     *
     * 服务端对断开的设备一律回 423，继续每 200 ms 轮询只会把电池和日志烧掉。
     * 30 秒后自动再试一次：用户在电脑上点了「连接」，这边就能自己接上。
     */
    @Volatile
    private var pausedUntilMs = 0L

    /** 用户点了「立即重连」：不等 20 秒心跳周期，下一轮直接发。 */
    @Volatile
    private var beatNow = false

    /**
     * 最近一次建会话失败的原因（null = 正常）。
     *
     * 与 [State.lastError] 分开：那个还要装网络错误、断开提示，
     * 这个是"连会话都建不起来"时的准确说法。
     */
    @Volatile
    private var sessionError: String? = null

    fun initialize(application: WirelessCableApp) {
        app = application
        refresher = EndpointRefresher(application.discovery, application.repository)
    }

    /**
     * 连不上时重新找这台电脑（mDNS）。
     *
     * 为什么放在"第一次心跳失败"而不是更早：正常心跳 10 秒一次，
     * 第一次失败到第二次失败之间有 10 秒窗口，够扫一次（4 秒），
     * 且 `failedBeats >= 2` 就会关闸门，晚了就没机会了。
     */
    private suspend fun tryRefreshEndpoint() {
        val refresher = refresher ?: return
        val session = app?.repository?.loadSession() ?: return

        when (val result = refresher.refresh(session)) {
            is RefreshResult.Adopted ->
                // 新地址已写回会话；ensureSession 的 key 含 host，下一轮会自动重建客户端
                _state.update { it.copy(lastError = "已重新找到电脑（${result.host}），正在连接…") }

            RefreshResult.FingerprintMismatch ->
                _state.update { it.copy(lastError = "这台电脑的证书变了，请重新配对") }

            RefreshResult.Unchanged,
            RefreshResult.NotFound,
            -> Unit
        }
    }

    /**
     * 用户点了「立即重连」。
     *
     * 只清掉退避并插一次心跳：服务端如果还是断开状态，会再回 423，
     * 界面仍旧显示那句话——这里不做任何"假乐观"。
     */
    fun retryNow() {
        pausedUntilMs = 0L
        beatNow = true
        clearManualGate()
    }

    /**
     * 断开后**只自动重连一次**：第二次失败就停在这里，等用户点「重连」。
     *
     * 为什么改：无限退避重连会在"电脑关机了/手机换了网络"时一直撞门，
     * 用户看到的是永远转圈的"正在重试"，既不知道到底连没连上，也没法主动收手。
     */
    private var failedBeats = 0

    @Volatile
    private var manualRetryRequired = false

    private fun clearManualGate() {
        manualRetryRequired = false
        failedBeats = 0
    }

    /** 用户点了「重连」：放开闸门并把循环拉起来。 */
    @Synchronized
    fun reconnectManually() {
        clearManualGate()
        pausedUntilMs = 0L
        beatNow = true

        val current = job
        if (current?.isActive != true) {
            _state.update { it.copy(running = true, lastTickAtMs = System.currentTimeMillis()) }
            job = scope.launch { loop() }
            Log.i(TAG, "手动重连：会话同步已重新启动")
        }
    }

    /** 请求尽快上报一次索引（新建/撤销授权根之后调用）。 */
    fun requestIndexPublish() {
        publishRequested = true
    }

    /** 取得一个持有者。界面可见时用 `ui`，前台服务用 `service`。 */
    @Synchronized
    fun acquire(tag: String) {
        holders += tag

        val current = job
        val now = System.currentTimeMillis()
        val lastTick = _state.value.lastTickAtMs
        val stale = lastTick > 0 && now - lastTick > STALE_TICK_MS

        // 循环还活着且没卡死：不重复启动。卡死的判定与看门狗一致——
        // 光看 isActive 不够，一个挂在网络调用上的协程永远是 "active"，
        // 界面回到前台时也救不回来（1.5.6 排查后台接收时踩到）。
        if (current?.isActive == true && !stale) return
        if (manualRetryRequired && !stale) return
        if (stale) {
            Log.w(TAG, "会话同步已卡住 ${now - lastTick} ms，重新拉起")
            current?.cancel()
        }

        _state.update { it.copy(running = true) }
        job = scope.launch { loop() }
        Log.i(TAG, "会话同步已启动（持有者：$tag）")
    }

    /**
     * 看门狗：持有者还在但循环已经停了/卡住了，就重启它。
     *
     * 前台服务的 ticker 每 5 秒调一次。没有这一步时，一次未捕获的 Error
     * 或某个卡死的调用会让"服务还在、通知还在，但没人取作业"——
     * 用户只能靠杀掉应用恢复，这是发布前实测踩到过的。
     */
    @Synchronized
    fun ensureAlive() {
        if (holders.isEmpty()) return

        // 闸门关着（自动重连已用完、等用户点）时不要偷偷把循环拉起来
        if (manualRetryRequired) return

        val current = job
        val now = System.currentTimeMillis()
        val lastTick = _state.value.lastTickAtMs
        val stale = lastTick > 0 && now - lastTick > STALE_TICK_MS

        if (current?.isActive == true && !stale) return

        Log.w(TAG, "会话同步循环未在运行（active=${current?.isActive} stale=$stale），重新拉起")
        current?.cancel()
        _state.update { it.copy(running = true, lastTickAtMs = now) }
        job = scope.launch { loop() }
    }

    /**
     * 彻底停下（退出应用时用）。
     *
     * 与 [release] 的区别：不管还有几个持有者，直接取消循环并清空。
     * 之后调用方会杀掉进程，所以这里不做"可恢复"处理。
     */
    @Synchronized
    fun shutdown() {
        holders.clear()
        job?.cancel()
        job = null
        receiveJob?.cancel()
        receiveJob = null
        indexJob?.cancel()
        indexJob = null
        _state.update { it.copy(running = false, connected = false, paused = false) }
        Log.i(TAG, "会话同步已彻底停止（退出应用）")
    }

    @Synchronized
    fun release(tag: String) {
        holders -= tag
        if (holders.isNotEmpty()) return

        job?.cancel()
        job = null
        // 索引扫描是在读用户存储，会话断开了就没有理由继续读
        indexJob?.cancel()
        indexJob = null
        // 正在收的那个文件让它写完再走：半路取消会在传输记录里留下一条"已取消"，
        // 文件也已经下载了大半——用户只是关了后台连接，不该丢这个文件。
        // 没有在收的话，循环已停，也不会再发起新的。
        _state.update { it.copy(running = false, connected = false) }
        Log.i(TAG, "会话同步已停止（最后一个持有者：$tag）")
    }

    private suspend fun loop() {
        var lastIndexAt = 0L
        var lastBeatAt = 0L

        while (true) {
            _state.update { it.copy(lastTickAtMs = System.currentTimeMillis()) }

            if (pausedUntilMs > System.currentTimeMillis()) {
                delay(SESSION_RETRY_MS)
                continue
            }

            try {
                val current = ensureSession()
                if (current == null) {
                    // 说清楚是"没配对/凭据读不出来"，而不是笼统的"连接中断"——
                    // 后者会让用户以为是网络问题，一直等下去
                    _state.update { it.copy(connected = false, lastError = sessionError) }
                    delay(SESSION_RETRY_MS)
                    continue
                }

                val now = System.currentTimeMillis()

                if (now - lastBeatAt >= HEARTBEAT_INTERVAL_MS) {
                    lastBeatAt = now
                    beat(current)
                    if (manualRetryRequired) {
                        Log.i(TAG, "自动重连已用完，等待用户手动重连")
                        break
                    }
                }

                if (publishRequested || now - lastIndexAt >= INDEX_INTERVAL_MS) {
                    publishRequested = false
                    lastIndexAt = now
                    // 不 await：整机根扫描是分钟级的，串在这里会把作业轮询彻底饿死。
                    // 上一轮还没扫完就不重复起（否则会堆一串并发扫描，更慢）。
                    if (indexJob?.isActive != true) {
                        val snapshot = current
                        indexJob = scope.launch {
                            try {
                                snapshot.service.publishIndexes(snapshot.token, snapshot.deviceId)
                                _state.update { it.copy(lastIndexAtMs = System.currentTimeMillis()) }
                            } catch (cancelled: CancellationException) {
                                throw cancelled
                            } catch (error: Throwable) {
                                Log.w(TAG, "索引上报失败（不影响心跳与作业）", error)
                            }
                        }
                    }
                }

                val jobStartedAt = System.currentTimeMillis()
                val summary = current.service.runJobs(current.token)
                val jobElapsed = System.currentTimeMillis() - jobStartedAt
                if (summary.executed > 0) {
                    _state.update { it.copy(lastJobAtMs = System.currentTimeMillis()) }
                }

                // 只有**改动过目录**的作业才立刻重报索引，并绕过整机根的重扫节流。
                // 不补这一次，电脑端会出现"操作成功但列表没变"（0.6.4 真机踩过）；
                // 但传输类作业（download / upload / list）不改目录，重报纯属浪费——
                // 整机根 3700 条，每 8 个文件传一次，传输过程中批与批之间就会卡一两秒。
                if (summary.mutated) {
                    lastIndexAt = System.currentTimeMillis()
                    // 同样不 await：改完目录要立刻重报，但扫描本身仍然是分钟级的
                    if (indexJob?.isActive != true) {
                        val snapshot = current
                        indexJob = scope.launch {
                            try {
                                snapshot.service.publishIndexes(snapshot.token, snapshot.deviceId, force = true)
                                _state.update { it.copy(lastIndexAtMs = System.currentTimeMillis()) }
                            } catch (cancelled: CancellationException) {
                                throw cancelled
                            } catch (error: Throwable) {
                                Log.w(TAG, "索引重报失败（不影响心跳与作业）", error)
                            }
                        }
                    }
                }

                /*
                 * 电脑发来的文件也在这条循环里收。
                 *
                 * 这是"App 在后台也能接收"的关键：接收不再挂在传输页的 ViewModel 上，
                 * 而是和心跳、作业同一条循环——前台服务息屏时它照样在跑。
                 * 放在作业之后：写进授权目录的任务要先由作业带着目标 URI 拉走，
                 * 抢在前面会把文件收进收件目录，作业再落盘就撞车（TransferPurposes）。
                 *
                 * 必须独立协程：一个 5 GiB 文件能传几分钟，await 在这里会把心跳卡停，
                 * 电脑端 90 秒后判离线，看门狗还会因为"循环太久没动"把它杀掉。
                 */
                if (receiveJob?.isActive != true && System.currentTimeMillis() - lastReceiveSpawnAtMs >= RECEIVE_SPAWN_INTERVAL_MS) {
                    lastReceiveSpawnAtMs = System.currentTimeMillis()
                    receiveJob = app?.let { application ->
                        scope.launch {
                            try {
                                InboxReceiver.pump(application)
                            } catch (cancelled: CancellationException) {
                                throw cancelled
                            } catch (error: Throwable) {
                                Log.w(TAG, "接收失败（不影响心跳与作业）", error)
                            }
                        }
                    }
                }

                // 取作业这次调用有没有被服务端挂住：挂住了说明长轮询生效，可以立刻续发；
                // 秒回说明对面是老版本或不支持，退回 3 秒，别把手机变成高频轮询器。
                val longPolling = jobElapsed >= LONG_POLL_THRESHOLD_MS || summary.executed > 0
                delay(if (longPolling) JOB_POLL_IDLE_MS else POLL_MS)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Throwable) {
                // 连 Error 一起兜：一次 SAF/网络异常（甚至 OOM）不该让"后台执行"永久停摆——
                // 服务还在、通知还在、作业却没人取，用户只能杀应用才能恢复（实测踩过）。
                // 记日志、歇一会儿再试；真的反复失败会一直留在这个分支里，不会放大危害。
                Log.w(TAG, "会话同步单轮失败，稍后重试", error)
                _state.update { it.copy(lastError = "后台同步出错，正在重试") }
                delay(SESSION_RETRY_MS)
            }
        }
    }

    /** 心跳只负责让电脑端看到"在线"；失败不打断作业循环（网络抖动很常见）。 */
    private suspend fun beat(current: SessionSnapshot) {
        when (val outcome = current.client.heartbeat(current.token)) {
            is ApiOutcome.Success -> {
                pausedUntilMs = 0L
                clearManualGate()
                _state.update { it.copy(connected = true, paused = false, lastError = null) }
            }

            is ApiOutcome.Failure -> {
                val revoked = outcome.code == ErrorCodes.DEVICE_REVOKED ||
                    outcome.code == ErrorCodes.UNAUTHORIZED

                if (outcome.code == ErrorCodes.SESSION_PAUSED) {
                    // 电脑主动断开：不是授权失效，别引导用户重新配对
                    pausedUntilMs = System.currentTimeMillis() + SESSION_PAUSED_RETRY_MS
                    _state.update {
                        it.copy(
                            connected = false,
                            paused = true,
                            lastError = "电脑已断开这台设备，请在电脑首页点「连接」",
                        )
                    }
                    return
                }

                if (revoked) {
                    _state.update {
                        it.copy(connected = false, lastError = "该电脑的授权已失效，请重新配对")
                    }
                    return
                }

                failedBeats++

                // 第一次失败先重新找一下这台电脑：电脑换 Wi-Fi / 路由器重启后 IP 会变，
                // 手机存的是死地址，不重新发现就只能等用户去重新扫码。
                // 只在这里做一次——真找不到（关机 / 不在同一网络）就往下走原有逻辑，
                // 不能让每一轮心跳都白扫 4 秒。
                if (failedBeats == 1) {
                    tryRefreshEndpoint()
                }

                if (failedBeats >= 2) {
                    // 自动重连只给一次机会：第二次失败就停下，等用户点
                    manualRetryRequired = true
                    _state.update {
                        it.copy(connected = false, lastError = "连接已断开，点「重连」再试")
                    }
                } else {
                    _state.update {
                        it.copy(connected = false, lastError = "连接中断，正在自动重连…")
                    }
                }
            }
        }
    }

    private data class SessionSnapshot(
        val client: WcApiClient,
        val service: FileManagerService,
        val token: String,
        val deviceId: String,
    )

    private suspend fun ensureSession(): SessionSnapshot? {
        val application = app ?: return null

        val session = application.repository.loadSession()
        if (session == null) {
            sessionError = "还没有与电脑配对，请先配对"
            return null
        }

        val token = application.vault.load(session.deviceId)
        if (token == null) {
            // 密文损坏（TokenVault 已清掉）或 Keystore 暂时不可用。
            // 两种都要给出口：首页的「扫描电脑」能重建会话，不用重新输码。
            sessionError = "连接凭据丢失，请在首页点「扫描电脑」重新连接"
            return null
        }

        sessionError = null

        // 让界面知道"当前这台设备就是正在跑会话的那台"。
        // 首页原来用配对页的状态判断，换网络后会出现"心跳正常但显示断开"。
        if (_state.value.deviceId != session.deviceId) {
            _state.update { it.copy(deviceId = session.deviceId) }
        }

        val key = "${session.host}:${session.port}|${session.fingerprint}|$token"

        if (key != sessionKey) {
            // 换电脑/重新配对后必须重建：旧客户端钉的是旧证书，旧服务持有旧 API
            val endpoint = ServerEndpoint(session.host, session.port)
            val pins = setOf(session.fingerprint)
            val createdApi = FileApi.create(endpoint, pins)
            val createdGrants = grants ?: RootGrantManager(application, application.contentResolver)
            grants = createdGrants

            client = WcApiClient.create(endpoint, pins)
            service = FileManagerService(
                context = application,
                api = createdApi,
                grants = createdGrants,
                // 作业要用同一个传输引擎（拉电脑传来的文件、把手机文件发回去）
                transfers = TransferRuntime.ensure(),
            )
            sessionKey = key
        }

        val currentClient = client ?: return null
        val currentService = service ?: return null
        return SessionSnapshot(currentClient, currentService, token, session.deviceId)
    }
}
