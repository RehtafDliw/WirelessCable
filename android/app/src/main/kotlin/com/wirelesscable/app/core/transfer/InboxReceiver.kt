package com.wirelesscable.app.core.transfer

import android.content.Context
import android.net.Uri
import android.util.Log
import com.wirelesscable.app.WirelessCableApp
import com.wirelesscable.app.core.files.InboxStore
import com.wirelesscable.app.core.protocol.TransferPurposes
import com.wirelesscable.app.core.protocol.TransferStates
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex

/**
 * 电脑发到手机收件目录的文件由谁接收。
 *
 * **为什么要单独一个对象**（1.5.6）：接收原来是传输页 ViewModel 的私事，
 * 只在该页面的轮询里发生。用户把 App 切到后台、息屏、甚至只是停在别的页面，
 * 电脑发来的文件就没人拉——任务躺在服务端，直到用户再次打开传输页。
 *
 * 现在接收改成"谁来驱动都行"：由 [com.wirelesscable.app.core.sync.SessionSyncEngine]
 * 每轮调用（它由前台服务保活，息屏也在跑），界面需要时也可以催一次。
 * 两者靠 [Mutex] 串行，同一时刻只有一个接收者——两个协程同时写同一个临时文件
 * 会让文件直接坏掉，这不是"慢一点"的问题。
 *
 * 纪律：
 * - 只接收 [TransferPurposes.INBOX]；写进授权目录的任务归作业，抢了会让作业落空。
 * - 一次只收一个文件：并行写同一个目录只会互相拖慢。
 * - 失败过的任务不每轮重来（[tried]），由用户点「重试」或重启后清除。
 * - 没「所有文件访问权」时明确标记 [State.blocked]，不静默失败。
 */
object InboxReceiver {

    data class State(
        /** 正在接收的文件名；null 表示当前没有在收。 */
        val receiving: String? = null,
        val progress: Float = 0f,
        /** 这一轮还没轮到的文件数。 */
        val remaining: Int = 0,
        val lastReceived: String? = null,
        val lastReceivedAtMs: Long = 0L,
        /** 缺少「所有文件访问权」，收件目录写不进去。 */
        val blocked: Boolean = false,
        val error: String? = null,
    ) {
        val active: Boolean get() = receiving != null

        /** 通知里「已接收」保留的窗口，过时就不必再提。 */
        fun freshNotice(now: Long = System.currentTimeMillis()): String? =
            lastReceived?.takeIf { now - lastReceivedAtMs < NOTICE_TTL_MS }
    }

    private const val TAG = "WcInbox"

    /**
     * 两次接收尝试的最小间隔。
     *
     * 引擎自己还有 1.5 秒的列表节流，这里再放慢到 5 秒：后台接收晚 5 秒人感知不到，
     * 但少了三分之二的常驻请求量——手机是多条轮询叠在一起跑的，
     * 一条跑太快会把服务端限流打满（1.5.6 真机踩到 429）。
     */
    private const val MIN_POLL_INTERVAL_MS = 5_000L

    /** 网络中断后多久再试：抖一下就重试会白白创建/删除占位文件。 */
    private const val INTERRUPT_BACKOFF_MS = 15_000L

    private const val NOTICE_TTL_MS = 8_000L

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state.asStateFlow()

    private val mutex = Mutex()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    /** 试过并失败的任务 id。留在集合里就不会被每个周期反复重试。 */
    private val tried = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

    @Volatile
    private var lastPollAtMs = 0L

    @Volatile
    private var backoffUntilMs = 0L

    /** 当前正在写的那条任务，供进度观察者过滤。 */
    @Volatile
    private var currentTaskId: String? = null

    /**
     * 拉一次任务列表，把该收的文件收进收件目录。
     *
     * @param force 忽略节流（用户点「重试」、刚授权完目录、界面主动催）。
     */
    suspend fun pump(context: Context, force: Boolean = false) {
        // 拿不到锁说明另一条路径（界面 / 会话循环）正在收，直接让位：
        // 两个协程同时写同一个临时文件会把文件写坏，这比"晚 3 秒收到"严重得多
        if (!mutex.tryLock()) return

        try {
            val now = System.currentTimeMillis()
            if (!force && now - lastPollAtMs < MIN_POLL_INTERVAL_MS) return
            if (!force && now < backoffUntilMs) return
            lastPollAtMs = now

            val application = context.applicationContext as? WirelessCableApp ?: return
            val engine = TransferRuntime.ensure() ?: return

            if (!InboxStore.isAvailable(context)) {
                // 不写 tried：用户去系统设置里授权后，下一轮就能自己接上
                if (!_state.value.blocked) {
                    _state.update { it.copy(blocked = true) }
                }
                return
            }

            if (_state.value.blocked) _state.update { it.copy(blocked = false) }

            if (engine.refresh() != null) return

            val candidates = engine.items.value.filter { item ->
                if (item.isUpload) return@filter false
                if (item.purpose != TransferPurposes.INBOX) return@filter false
                if (item.savedToPhone) return@filter false
                if (item.taskId in tried) return@filter false

                when (item.state) {
                    TransferStates.WAITING -> true
                    // 断在中途的收件任务自己接着传：落点在收件目录，不需要问用户
                    TransferStates.INTERRUPTED -> isInboxDestination(application, item.taskId)
                    else -> false
                }
            }

            if (candidates.isEmpty()) {
                if (_state.value.remaining != 0) _state.update { it.copy(remaining = 0) }
                return
            }

            for ((index, item) in candidates.withIndex()) {
                if (!InboxStore.isAvailable(context)) break

                val name = item.fileName.ifBlank { "file" }
                val target = InboxStore.reserve(name)
                if (target == null) {
                    _state.update { it.copy(error = "无法写入收件目录（下载/WirelessCable）") }
                    break
                }

                currentTaskId = item.taskId
                tried += item.taskId
                _state.update {
                    it.copy(
                        receiving = name,
                        progress = 0f,
                        remaining = candidates.size - index,
                        error = null,
                    )
                }

                val watcher = watchProgress(engine, item.taskId)
                val result = try {
                    engine.saveDownload(item, Uri.fromFile(target), allowResume = false)
                } finally {
                    watcher.cancel()
                }

                when (result) {
                    is TransferResult.Ok -> {
                        Log.i(TAG, "已接收 $name → ${InboxStore.RELATIVE_PATH}")
                        _state.update {
                            it.copy(
                                receiving = null,
                                progress = 0f,
                                lastReceived = target.name,
                                lastReceivedAtMs = System.currentTimeMillis(),
                                remaining = candidates.size - index - 1,
                            )
                        }
                    }

                    is TransferResult.Failed -> {
                        // 留一个 0 字节占位比没有更糟
                        runCatching { target.delete() }
                        Log.w(TAG, "接收失败 $name：${result.message}")
                        _state.update {
                            it.copy(receiving = null, progress = 0f, error = result.message)
                        }
                    }

                    is TransferResult.Interrupted -> {
                        // 半截数据在应用私有临时文件里，落点只是刚占的空文件。
                        // 删掉占位，下次续传会重新占一个不重名的名字。
                        runCatching { target.delete() }
                        tried -= item.taskId
                        backoffUntilMs = System.currentTimeMillis() + INTERRUPT_BACKOFF_MS
                        Log.i(TAG, "接收中断 $name，${INTERRUPT_BACKOFF_MS / 1000} 秒后续传")
                        _state.update { it.copy(receiving = null, progress = 0f) }
                        // 网络已经不可靠了，这一轮别再往下收
                        break
                    }

                    TransferResult.Cancelled -> {
                        runCatching { target.delete() }
                        _state.update { it.copy(receiving = null, progress = 0f) }
                    }
                }

                currentTaskId = null
            }
        } catch (cancelled: kotlinx.coroutines.CancellationException) {
            // 循环被人为停掉（退出应用）：让取消继续向上传播，不要当成"接收出错"
            throw cancelled
        } catch (error: Throwable) {
            // 接收出问题不能连带把心跳/作业循环拖停：这里自己咽下，只记状态
            Log.w(TAG, "接收循环失败", error)
            _state.update { it.copy(receiving = null, error = "接收失败，稍后重试") }
        } finally {
            currentTaskId = null
            // 被取消（退出应用/关掉后台连接）时可能停在"接收中"，别把这句话留在通知上
            if (_state.value.receiving != null) _state.update { it.copy(receiving = null) }
            mutex.unlock()
        }
    }

    /**
     * 用户点「重试」：清掉"试过了"的标记，让下一轮再看它一次。
     *
     * 不需要自己去跑——[pump] 马上会被会话循环调到，这里只把门打开。
     */
    fun retry(taskId: String) {
        tried.remove(taskId)
        lastPollAtMs = 0L
        backoffUntilMs = 0L
        _state.update { it.copy(error = null) }
    }

    /** 任务被取消/删除：把标记也撤掉，别让它一直占着集合。 */
    fun forget(taskId: String) {
        tried.remove(taskId)
    }

    /** 退出应用或换电脑：清空所有短期状态。 */
    fun reset() {
        tried.clear()
        lastPollAtMs = 0L
        backoffUntilMs = 0L
        _state.value = State()
    }

    /** 收件目录是否已授权——界面据此提示"后台接收需要文件访问权限"。 */
    fun isBlocked(): Boolean = _state.value.blocked

    // ------------------------------------------------------------ 内部

    /** 落点在收件目录的下载任务（中断恢复时只认这一类，用户手动保存到别处的不碰）。 */
    private suspend fun isInboxDestination(application: WirelessCableApp, taskId: String): Boolean {
        val entity = application.database.transferDao().get(taskId) ?: return false
        return InboxStore.isInboxUri(entity.destinationUri)
    }

    /** 把引擎里的进度映射到接收状态上；返回的 job 用完即取消。 */
    private fun watchProgress(engine: TransferEngine, taskId: String): Job = scope.launch {
        engine.items.collect { items ->
            if (currentTaskId != taskId) return@collect
            val item = items.firstOrNull { it.taskId == taskId } ?: return@collect
            _state.update { it.copy(progress = item.progress) }
        }
    }
}
