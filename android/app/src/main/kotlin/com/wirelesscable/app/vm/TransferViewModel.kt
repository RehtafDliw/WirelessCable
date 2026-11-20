package com.wirelesscable.app.vm

import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.webkit.MimeTypeMap
import android.net.Uri
import android.util.Log
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.wirelesscable.app.core.files.InboxStore
import com.wirelesscable.app.core.files.SavedFiles
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.FileApi
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.network.TransferApi
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.TransferDirections
import com.wirelesscable.app.core.protocol.TransferErrorCodes
import com.wirelesscable.app.core.protocol.TransferTask
import com.wirelesscable.app.core.protocol.TransferStates
import com.wirelesscable.app.core.transfer.InboxReceiver
import com.wirelesscable.app.core.transfer.TransferEngine
import com.wirelesscable.app.core.transfer.TransferItem
import com.wirelesscable.app.core.transfer.TransferResult
import com.wirelesscable.app.core.transfer.TransferRuntime
import com.wirelesscable.app.data.db.AppDatabase
import com.wirelesscable.app.data.repository.ConnectionRepository
import com.wirelesscable.app.data.security.TokenVault
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import com.wirelesscable.app.core.i18n.Str
import kotlinx.coroutines.launch
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 传输页的编排者。
 *
 * 职责边界：负责「会话凭据 → 传输引擎 → 界面状态」的粘合，不实现任何传输细节；
 * 分块、校验、进度节流、断点续传都在 core 层的 [TransferEngine] 里。
 *
 * 引擎按 (地址, 令牌) 缓存：换了电脑或重新配对后令牌会变，旧引擎必须丢弃，
 * 否则会拿一个已作废的令牌反复请求。
 */
/** 一个待上传的来源。`displayName` 为空时沿用文件名（应用安装包必须给，否则全是 base.apk）。 */
data class UploadItem(val uri: Uri, val displayName: String? = null)

class TransferViewModel(
    private val repository: ConnectionRepository,
    private val vault: TokenVault,
    private val resolver: ContentResolver,
    private val db: AppDatabase,
    private val tempDir: File,
    private val appContext: Context,
) : ViewModel() {

    private val _state = MutableStateFlow(TransferUiState())
    val state: StateFlow<TransferUiState> = _state.asStateFlow()

    private var engine: TransferEngine? = null

    /** 用于取共享目录列表（上传落点）。与传输引擎分开，两者生命周期不同。 */
    private var fileApi: FileApi? = null
    private var fileApiKey: String? = null

    /** 删除记录等一次性请求用；与传输引擎的连接池分开，别互相影响。 */
    private var controlApi: TransferApi? = null
    private var controlApiKey: String? = null
    private var collectJob: Job? = null
    private var pollJob: Job? = null
    private var uploadJob: Job? = null
    private var saveJob: Job? = null

    /** 已提示过"缺文件访问权"，避免每轮刷新都往页面塞同一句话。 */
    private var reportedInboxBlocked = false

    /** 已提示过的接收时间点（毫秒），用来判断"这是一次新的接收"。 */
    private var lastReportedReceiveAtMs = 0L

    /** 是否已把"进入页面前的接收记录"当作已知。 */
    private var receiveBaselineSet = false

    /** 一次性提示的到期时间（见 [expireMessage]）。 */
    private var lastMessageText: String? = null
    private var lastMessageAtMs = 0L

    /** 正在自动恢复的任务（防止同一任务被重复拉起）。 */
    private val resumeJobs = mutableMapOf<String, Job>()

    /**
     * 被用户整批暂停/取消的批次。
     *
     * 整文件夹的落盘是 `saveFolderBatch` 里一个 for 循环串行做的。
     * 只取消正在跑的那个 saveJob，`join()` 一返回循环就会接着拉下一个文件——
     * 用户看到的是"点了暂停，但文件还在一个个进来"。循环每次取下一个文件前
     * 都要看这个集合，看到自己就 break。
     */
    private val pausedBatches = mutableSetOf<String>()

    /**
     * 任务 → 批 key。
     *
     * 服务端只给"批量接口"建出来的任务写 batchId；逐条建的（上传、整文件夹落盘）
     * 没有，只能由手机在创建时记一笔。没有这张表，点暂停时找不到"这一批还有哪些
     * 文件没开始"，恢复时也就无从接着跑。
     */
    private val taskBatchKeys = mutableMapOf<String, String>()

    /** 整文件夹落盘的全量任务与目录树：暂停后要能从头续（已完成的跳过）。 */
    private data class FolderBatchState(
        val name: String,
        val tasks: List<TransferTask>,
        val treeUri: Uri,
    )

    /** 一批待上传的来源与进度：暂停后从 [next] 接着来。 */
    private data class UploadBatchState(
        val items: List<UploadItem>,
        val target: UploadTarget?,
        var next: Int = 0,
        /**
         * 已建出来的任务（下标 → taskId）。
         *
         * 传输中断时服务端那边已经有任务与分块了，恢复必须**续传它**；
         * 再调一次 upload 会新建一个同名任务，列表里出现两条、文件传两遍。
         */
        val taskIds: MutableMap<Int, String> = mutableMapOf(),
    )

    private val folderBatches = mutableMapOf<String, FolderBatchState>()
    private val uploadBatches = mutableMapOf<String, UploadBatchState>()

    /** 批落的驱动协程（一次只跑一个：并行写同一个 SAF 目录会互相干扰）。 */
    private var folderJob: Job? = null
    private val queuedFolderPumps = ArrayDeque<String>()
    private val queuedUploadPumps = ArrayDeque<String>()

    /**
     * 取出（或创建）传输引擎。
     *
     * 文件浏览页也要用它——电脑上传文件到手机目录时，
     * 手机要靠它把文件内容拉到 SAF 目标。
     */
    suspend fun currentEngine(): TransferEngine? = ensureEngine()

    /** 进入传输页时调用。拉起轮询并立即刷新一次。 */
    fun start() {
        if (pollJob?.isActive == true) return

        pollJob = viewModelScope.launch {
            while (isActive) {
                // 单轮失败**绝不能让循环死掉**：循环一停，界面上最后那条
                // "连接超时" 就永久挂着了（用户看到的就是"传输页一直挂着连接超时"）。
                // 之前这里没有任何保护，任何一次异常（网络库抛错、Room/SAF 异常）
                // 都会把整个轮询带走，之后再也没有人能把它清掉。
                try {
                    val current = ensureEngine()
                    if (current == null) {
                        _state.update { it.copy(ready = false, refreshError = "尚未连接电脑") }
                    } else {
                        val error = current.refresh()
                        _state.update {
                            it.copy(
                                ready = error == null,
                                // 轮询失败单独记录：成功后立刻清空，不把一次瞬时超时留成常驻错误
                                refreshError = error,
                            )
                        }

                        // **对方取消后，已经落到手机上的文件也要删掉**：
                        // 取消 = 记录和数据都清掉（需求：不管哪一端取消，两端都同步清理）。
                        // 引擎没有 SAF 权限，它把落点交出来，这里删。
                        current.drainCancelledSavedUris().forEach { uri ->
                            runCatching { SavedFiles.delete(resolver, uri) }
                        }

                        if (error == null) {
                            // 另一端（电脑）点了暂停：服务端把这一批标成 paused。
                            // 这里必须把它记成"整批暂停"，否则泵会接着开始下一个文件，
                            // 批也会在 finally 里被丢掉——用户点「继续」时根本拉不起来。
                            _state.value.items
                                .filter { it.state == TransferStates.PAUSED }
                                .mapNotNull { taskBatchKeys[it.taskId] }
                                .forEach { pausedBatches += it }

                            // 反过来：服务端已经恢复（不再是 paused），把被按住的批放行并重启泵。
                            // 电脑端点「继续」时手机这边没有任何本地动作，靠这一段把传输唤醒。
                            _state.value.items
                                .filter { it.state != TransferStates.PAUSED && it.isActive }
                                .mapNotNull { taskBatchKeys[it.taskId] }
                                .forEach { key ->
                                    if (key in pausedBatches) {
                                        pausedBatches -= key
                                        if (folderBatches.containsKey(key)) pumpFolderBatch(key)
                                        if (uploadBatches.containsKey(key)) pumpUploads(key)
                                    }
                                }

                            resumeInterrupted(current)
                            refreshUploadTargets()
                            nudgeReceive()
                            reportReceive()
                        }
                    }

                    expireMessage()
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (t: Throwable) {
                    Log.w("WcTransfer", "传输页刷新异常，本轮跳过", t)
                    _state.update {
                        it.copy(ready = false, refreshError = "刷新失败：${t.message ?: "未知错误"}")
                    }
                }

                // 有任务在跑时更快地合并服务端状态，空闲时降低频率省电
                delay(if (_state.value.hasActive) ACTIVE_POLL_MS else IDLE_POLL_MS)
            }
        }
    }

    fun stop() {
        pollJob?.cancel()
        pollJob = null
    }

    /**
     * 一次性提示（"已取消 3 个任务"、某次操作失败的原因）到点自动消失。
     *
     * [message] 只有在"下一次操作"时才会被覆盖，没人操作就会一直挂在页面上——
     * 一次超时留下的"连接超时"能挂半小时，看着像页面坏了。
     * [refreshError] 不吃这个逻辑：那是"列表当前新不新鲜"，该留着就留着。
     */
    private fun expireMessage() {
        val text = _state.value.message ?: return
        val now = android.os.SystemClock.elapsedRealtime()

        if (text != lastMessageText) {
            lastMessageText = text
            lastMessageAtMs = now
            return
        }

        if (now - lastMessageAtMs >= MESSAGE_TTL_MS) {
            lastMessageText = null
            _state.update { it.copy(message = null) }
        }
    }

    fun clearMessage() = _state.update { it.copy(message = null) }

    /**
     * 上传一批 SAF 文件。串行执行，任一失败不影响后续文件。
     *
     * @param target 落点。null 表示用当前选中的共享目录（[setUploadTarget]）；
     *               未选则落到电脑的默认接收目录。
     */
    fun upload(uris: List<Uri>, target: UploadTarget? = null) {
        uploadItems(uris.map { UploadItem(it) }, target)
    }

    /**
     * 系统分享进来的纯文字（浏览器、微信里分享链接就是这种）。
     *
     * 传输协议只认文件，没有"发一段话"这回事，所以先落成一个小 txt。
     * 名字带时间戳，重复分享不会互相覆盖。
     *
     * 注意这里**只落盘、不发送**：用户还没在选择框里决定发给哪台电脑 / 哪台手机。
     */
    fun materializeShareText(text: String): UploadItem? {
        val directory = File(tempDir, "share").apply { mkdirs() }
        val stamp = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date())
        // 文件名会原样落到电脑上，英文界面下别出现中文
        val file = File(directory, "${Str.t("分享文本")}-$stamp.txt")

        try {
            file.writeText(text)
        } catch (e: Exception) {
            Log.w("TransferViewModel", "写分享文本失败", e)
            notifyMessage("无法读取分享内容")
            return null
        }

        // 缓存目录里只留最近 20 个，不然分享几次就长蘑菇
        runCatching {
            directory.listFiles()
                ?.sortedByDescending { it.lastModified() }
                ?.drop(20)
                ?.forEach { it.delete() }
        }

        return UploadItem(Uri.fromFile(file), file.name)
    }

    /**
     * 把一条提示送到传输页。
     *
     * 分享入口没有自己的界面，提示只能落在用户马上会看到的那一页——
     * 用 Toast 一闪而过，等于没提示。
     */
    fun notifyMessage(text: String) {
        _state.update { it.copy(message = text) }
    }

    /**
     * 把选中的应用安装包发给电脑。
     *
     * 不管对面能不能装——系统应用的签名、分体包的架构都可能对不上，
     * 那是安装那一刻的事；这里只保证"发送成功、文件名是应用名"。
     */
    fun uploadApps(apps: List<com.wirelesscable.app.core.apps.InstalledApp>, target: UploadTarget? = null) {
        val items = apps.flatMap { app ->
            app.parts.mapIndexed { index, part ->
                UploadItem(
                    uri = Uri.fromFile(File(part.path)),
                    displayName = com.wirelesscable.app.core.apps.InstalledApps
                        .partName(app.label, app.packageName, index),
                )
            }
        }

        uploadItems(items, target)
    }

    fun uploadItems(items: List<UploadItem>, target: UploadTarget? = null) {
        if (items.isEmpty()) return
        if (_state.value.busy) {
            _state.update { it.copy(message = "已有传输在进行，请稍候") }
            return
        }

        // 整批存下来：暂停只是把这个泵停住，恢复时从下一个接着发。
        // 以前循环体直接写在这里，协程一取消整批就没了，点「继续」只在恢复
        // 已经建出任务的那一个，剩下的永远不会再发（"暂停了不能恢复"）。
        val key = "up:${System.currentTimeMillis()}"
        uploadBatches[key] = UploadBatchState(items, target)
        pausedBatches -= key
        pumpUploads(key)
    }

    /** 串行发送一批来源；[pausedBatches] 命中时停下，进度停在 [UploadBatchState.next]。 */
    private fun pumpUploads(key: String) {
        val batch = uploadBatches[key] ?: return
        if (uploadJob?.isActive == true) {
            if (key !in queuedUploadPumps) queuedUploadPumps += key
            return
        }

        uploadJob = viewModelScope.launch {
            val current = ensureEngine()
            if (current == null) {
                _state.update { it.copy(message = "尚未连接电脑，请先在「设备」页配对") }
                return@launch
            }

            _state.update { it.copy(busy = true, message = null) }

            var ok = 0
            var interrupted = 0
            var firstError: String? = null

            try {
                while (batch.next < batch.items.size) {
                    if (key in pausedBatches) break

                    val index = batch.next
                    val item = batch.items[index]

                    // 对方把这一条取消了：停下，别再往电脑推
                    val builtTask = batch.taskIds[index]
                    if (builtTask != null && current.isCancelledByPeer(builtTask)) {
                        pausedBatches += key
                        break
                    }

                    // 授权持久化：APP 被杀后重新打开仍能凭 Room 里的 URI 续传
                    // （file:// 没有可持久化的授权，抛异常也无所谓）
                    runCatching {
                        resolver.takePersistableUriPermission(
                            item.uri,
                            Intent.FLAG_GRANT_READ_URI_PERMISSION,
                        )
                    }

                    // 这一项上次已经建过任务（中断/暂停）：续传它，别再新建一个同名任务
                    val builtTaskId = batch.taskIds[index]
                    if (builtTaskId != null) {
                        when (val resumed = current.resumeUpload(builtTaskId, allowResume = false)) {
                            is TransferResult.Ok -> {
                                batch.next++
                                ok++
                            }

                            is TransferResult.Failed -> {
                                batch.next++
                                if (firstError == null) firstError = resumed.message
                            }

                            is TransferResult.Interrupted -> {
                                interrupted++
                                break
                            }

                            TransferResult.Cancelled -> {
                                // 被暂停：保留这一批，用户点「继续」时还能接着发
                                pausedBatches += key
                                break
                            }
                        }
                        continue
                    }

                    val directoryId = batch.target?.directoryId ?: _state.value.selectedUploadTargetId
                    val relativeDirectory = batch.target?.relativeDirectory
                    val result = current.upload(
                        item.uri,
                        directoryId = directoryId,
                        relativeDirectory = relativeDirectory,
                        displayName = item.displayName,
                        onTaskCreated = { taskId ->
                            batch.taskIds[index] = taskId
                            taskBatchKeys[taskId] = key
                        },
                    )
                    when (result) {
                        is TransferResult.Ok -> {
                            batch.next++
                            ok++
                        }

                        is TransferResult.Failed -> {
                            batch.next++
                            if (firstError == null) firstError = result.message
                        }

                        // 中断不前进：恢复后从同一个文件重来（引擎按位图续传，不会重传已完成分块）
                        is TransferResult.Interrupted -> {
                            interrupted++
                            break
                        }

                        TransferResult.Cancelled -> {
                            // 被暂停/被取消：保留批，别在 finally 里丢掉
                            pausedBatches += key
                            break
                        }
                    }
                }
            } finally {
                _state.update {
                    it.copy(
                        busy = false,
                        message = when {
                            key in pausedBatches -> it.message
                            batch.items.size == 1 && firstError != null -> firstError
                            firstError != null -> "已发送 $ok 个，${batch.items.size - ok} 个失败：$firstError"
                            interrupted > 0 -> "连接中断，$interrupted 个任务将在恢复后续传"
                            else -> it.message
                        },
                    )
                }
                uploadJob = null
                if (key !in pausedBatches) uploadBatches.remove(key)
                drainUploadPumps()
            }
        }
    }

    private fun drainUploadPumps() {
        while (uploadJob?.isActive != true && queuedUploadPumps.isNotEmpty()) {
            pumpUploads(queuedUploadPumps.removeFirst())
        }
    }

    /** 接收一个电脑端发起的下载任务，写入用户选定的位置。 */
    fun saveDownload(item: TransferItem, destination: Uri) {
        if (_state.value.busy) {
            _state.update { it.copy(message = "已有传输在进行，请稍候") }
            return
        }

        startSave(item, destination)
    }

    /**
     * 批量下载：把多个文件依次拉到用户选定的目录。
     *
     * 串行执行——每个文件都要先在 SAF 目录里创建目标文件，
     * 再拉取内容；并行会让 SAF 的写入与传输互相干扰。
     */
    fun downloadBatch(items: List<BatchDownloadItem>, directoryUri: Uri) {
        if (items.isEmpty()) return
        if (_state.value.busy) {
            _state.update { it.copy(message = "已有传输在进行，请稍候") }
            return
        }

        viewModelScope.launch {
            val api = ensureFileApi() ?: return@launch
            val token = tokenOf() ?: return@launch

            var ok = 0
            var failed = 0
            var firstError: String? = null

            for (item in items) {
                when (val outcome = api.download(token, item.rootId, item.path)) {
                    is ApiOutcome.Failure -> {
                        failed++
                        firstError = firstError ?: outcome.message
                    }

                    is ApiOutcome.Success -> {
                        val target = createDocumentIn(directoryUri, item.name)
                        if (target == null) {
                            failed++
                            firstError = firstError ?: "无法在所选目录创建 ${item.name}"
                            continue
                        }

                        startSave(transferItemOf(outcome.value, item.name), target)
                        ok++

                        // 等这个文件落盘完再下一个，避免并发写同一个 SAF 目录
                        saveJob?.join()
                    }
                }
            }

            _state.update {
                it.copy(
                    message = when {
                        failed == 0 -> "已加入 $ok 个文件的下载"
                        else -> "已加入 $ok 个，$failed 个失败：${firstError ?: "未知原因"}"
                    },
                )
            }
        }
    }

    /**
     * 在 SAF 目录里创建一个空文件作为下载落点。
     *
     * 注意：`DocumentsContract.createDocument` 要的是**父目录的 document URI**，
     * 直接传目录选择器返回的 tree URI 会失败——tree URI 得先转成 document URI。
     */
    /**
     * 收一整批（整个文件夹）到用户选的目录树里，**保留子目录结构**。
     *
     * 全量任务与目录树要留在内存里（[folderBatches]）：暂停后点「继续」时，
     * 还没建出任务的那些文件得有人接着跑。以前这个 for 循环一 break 就没了，
     * 恢复只恢复"已经建过任务的"几十个，剩下的永远不再传——用户看到的就是
     * "暂停了不能恢复"。
     */
    fun saveFolderBatch(batchName: String, tasks: List<TransferTask>, treeUri: Uri) {
        if (tasks.isEmpty()) return
        if (_state.value.busy) {
            _state.update { it.copy(message = "已有传输在进行，请稍候") }
            return
        }

        val key = tasks.firstOrNull()?.batchId?.takeIf { it.isNotBlank() } ?: "dir:$batchName"
        folderBatches[key] = FolderBatchState(batchName, tasks, treeUri)
        pausedBatches -= key
        pumpFolderBatch(key)
    }

    /** 串行把一批文件落到用户目录；已完成的跳过，暂停的续传。 */
    private fun pumpFolderBatch(key: String) {
        val batch = folderBatches[key] ?: return
        if (folderJob?.isActive == true) {
            // 同一个 SAF 目录并行写会互相干扰：排队，等当前这批跑完
            if (key !in queuedFolderPumps) queuedFolderPumps += key
            return
        }

        folderJob = viewModelScope.launch {
            val current = ensureEngine()
            if (current == null) {
                _state.update { it.copy(message = "尚未连接电脑，请先在「设备」页配对") }
                return@launch
            }

            _state.update {
                it.copy(busy = true, message = "正在接收「${batch.name}」共 ${batch.tasks.size} 个文件")
            }

            var ok = 0
            var failed = 0
            var firstError: String? = null

            try {
                for (task in batch.tasks) {
                    // 用户整批暂停/取消：循环必须自己停，否则会接着一个个拉下去
                    if (key in pausedBatches) break

                    // **对方把这一条（多半是整批）取消了**：停下。
                    // 继续跑下去的话，每个文件都会去服务端问一次、再失败一次，
                    // 手机端冒出一堆"已取消"的行（真机：电脑取消作业后手机还能看到）。
                    if (current.isCancelledByPeer(task.taskId)) {
                        pausedBatches += key
                        break
                    }

                    // 另一端已经把这条取消/传完了：跳过，别再建文件重传
                    val live = _state.value.items.firstOrNull { it.taskId == task.taskId }
                    if (live != null &&
                        (live.state == TransferStates.CANCELLED || live.state == TransferStates.COMPLETED)
                    ) {
                        continue
                    }

                    val entity = runCatching { db.transferDao().get(task.taskId) }.getOrNull()
                    if (entity != null && entity.status != TransferStates.PAUSED) {
                        // 已完成/正在跑/失败中断的都不归这个泵管（中断由自动恢复接手）
                        continue
                    }

                    // relativePath 形如「文件夹名/子目录/文件名」，而用户选的目标目录是**父目录**，
                    // 所以整段原样保留：文件落到「所选目录/文件夹名/子目录/文件名」。
                    //
                    // 以前这里是 `drop(1)` 去掉文件夹名、再 `add(fileName)`，于是变成
                    // 「所选目录/a.jpg/a.jpg」——每个文件都建了一个和自己同名的文件夹，
                    // 文件被塞进那个文件夹里（真机现象：整个文件夹下载完，里面全是同名目录）。
                    val target = if (entity?.destinationUri != null) {
                        // 恢复：落点已经建过并记在本地，别在同一个目录里再建一个同名文件
                        Uri.parse(entity.destinationUri)
                    } else {
                        val segments = task.relativePath
                            .split('/')
                            .filter { it.isNotBlank() }
                            .toMutableList()

                        if (segments.isEmpty()) {
                            segments.add(task.fileName)
                        } else if (segments.last() != task.fileName) {
                            // 兜底：老记录 / 异常数据里 relativePath 没有带文件名
                            segments.add(task.fileName)
                        }

                        createDocumentAt(batch.treeUri, segments)
                    }

                    if (target == null) {
                        failed++
                        firstError = firstError ?: "建不出 ${task.relativePath}"
                        continue
                    }

                    taskBatchKeys[task.taskId] = key

                    val item = _state.value.items.firstOrNull { it.taskId == task.taskId }
                        ?: transferItemOf(task, task.fileName)

                    // allowResume = false：泵是自动流程，不能顺手把服务端的暂停解掉。
                    // Cancelled 表示"被暂停/被取消"，整批必须停下——以前这里是 Unit，
                    // 于是停了当前文件、接着拉下一个，用户看到的就是"暂停停不住"。
                    when (val result = current.saveDownload(item, target, allowResume = false)) {
                        is TransferResult.Ok -> ok++
                        is TransferResult.Failed -> {
                            failed++
                            firstError = firstError ?: result.message
                        }

                        // Cancelled 表示"被暂停/被取消"，整批必须停下——以前这里是 Unit，
                        // 于是停了当前文件、接着拉下一个，用户看到的就是"暂停停不住"。
                        // 同时把批标记成暂停：否则 finally 会把批丢掉，用户点「继续」也拉不起来。
                        else -> {
                            pausedBatches += key
                            break
                        }
                    }
                }
            } finally {
                _state.update {
                    it.copy(
                        busy = false,
                        message = when {
                            key in pausedBatches -> it.message
                            failed > 0 && ok == 0 -> "接收「${batch.name}」失败：${firstError ?: "建不出目录"}"
                            failed > 0 -> "「${batch.name}」已收 $ok 个，$failed 个失败"
                            ok > 0 -> "「${batch.name}」已收到所选目录（$ok 个文件）"
                            else -> it.message
                        },
                    )
                }
                folderJob = null
                if (key !in pausedBatches) folderBatches.remove(key)
                drainFolderPumps()
            }
        }
    }

    private fun drainFolderPumps() {
        while (folderJob?.isActive != true && queuedFolderPumps.isNotEmpty()) {
            pumpFolderBatch(queuedFolderPumps.removeFirst())
        }
    }


    /** 按分段建出目录链并创建文件；已存在的目录直接复用。 */
    private fun createDocumentAt(treeUri: Uri, segments: List<String>): Uri? = try {
        var parent = android.provider.DocumentsContract.buildDocumentUriUsingTree(
            treeUri,
            android.provider.DocumentsContract.getTreeDocumentId(treeUri),
        )

        segments.forEachIndexed { index, name ->
            val isLast = index == segments.lastIndex
            parent = if (isLast) {
                android.provider.DocumentsContract.createDocument(
                    resolver,
                    parent,
                    "application/octet-stream",
                    name,
                )
            } else {
                findOrCreateDirectory(parent, name) ?: return null
            }
        }

        parent
    } catch (e: Exception) {
        Log.w("TransferViewModel", "在所选目录建文件失败: ${e.message}")
        null
    }

    /** 先按名字找，找不到再建——重复点同一个文件夹时不能因为"已存在"就整批失败。 */
    private fun findOrCreateDirectory(parent: Uri, name: String): Uri? {
        val children = android.provider.DocumentsContract.buildChildDocumentsUriUsingTree(
            parent,
            android.provider.DocumentsContract.getDocumentId(parent),
        )

        runCatching {
            resolver.query(
                children,
                arrayOf(
                    android.provider.DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    android.provider.DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                ),
                null,
                null,
                null,
            )?.use { cursor ->
                while (cursor.moveToNext()) {
                    if (cursor.getString(1) == name) {
                        return android.provider.DocumentsContract.buildDocumentUriUsingTree(
                            parent,
                            cursor.getString(0),
                        )
                    }
                }
            }
        }

        return runCatching {
            android.provider.DocumentsContract.createDocument(
                resolver,
                parent,
                android.provider.DocumentsContract.Document.MIME_TYPE_DIR,
                name,
            )
        }.getOrNull()
    }

    private fun createDocumentIn(directoryUri: Uri, name: String): Uri? = try {
        val parent = android.provider.DocumentsContract.buildDocumentUriUsingTree(
            directoryUri,
            android.provider.DocumentsContract.getTreeDocumentId(directoryUri),
        )

        android.provider.DocumentsContract.createDocument(
            resolver,
            parent,
            "application/octet-stream",
            name,
        )
    } catch (e: Exception) {
        Log.w("TransferViewModel", "在所选目录创建文件失败: ${e.message}")
        null
    }

    /** 服务端任务 → 界面用的传输条目。 */
    private fun transferItemOf(
        task: com.wirelesscable.app.core.protocol.TransferTask,
        fallbackName: String,
    ): TransferItem = TransferItem(
        taskId = task.taskId,
        direction = TransferDirections.DOWNLOAD,
        fileName = task.fileName.ifEmpty { fallbackName },
        deviceName = task.deviceName,
        totalBytes = task.totalBytes,
        transferredBytes = task.transferredBytes,
        chunkSize = task.chunkSize,
        chunkCount = task.chunkCount,
        state = task.state,
        purpose = task.purpose,
        sha256 = task.sha256,
        errorMessage = task.errorMessage,
        updatedAt = task.updatedAtUnixMs,
        // 落点路径/批次键必须带：保存流程要据此还原多级目录，
        // 分组也要用它（服务端给了就必须一路传下去，别在中间丢掉）。
        relativePath = task.relativePath,
        batchId = task.batchId,
        batchName = task.batchName,
    )

    /**
     * 电脑发来的文件：直接写进收件目录，不再弹保存框。
     *
     * 收件目录固定为「下载/WirelessCable」（需要「所有文件访问权」）。
     *
     * **接收本身不在这里做**（1.5.6）：执行者是应用级的 [InboxReceiver]，
     * 由会话循环驱动，所以 App 退到后台、息屏也照收。这个页面只是"顺手催一把"，
     * 让刚打开传输页的那一刻不用等 3 秒节流；两者互斥，不会重复下载同一个文件。
     */
    private fun nudgeReceive() {
        viewModelScope.launch { InboxReceiver.pump(appContext) }
    }

    /**
     * 把应用级接收器的结果搬到页面上。
     *
     * 接收可能发生在页面根本没打开的时候（后台服务收的），所以不能靠"接收时弹提示"，
     * 只能每轮比对一次时间戳——只提示新的一次，不会每 3 秒重复。
     */
    private fun reportReceive() {
        // 第一次进来先把"上次收到的"当成已知，否则会把几分钟前的接收再播报一遍
        if (!receiveBaselineSet) {
            receiveBaselineSet = true
            lastReportedReceiveAtMs = InboxReceiver.state.value.lastReceivedAtMs
        }

        val inbox = InboxReceiver.state.value

        // 行内状态要用它："等待接收"和"需要文件访问权限"是两件事
        if (_state.value.inboxBlocked != inbox.blocked) {
            _state.update { it.copy(inboxBlocked = inbox.blocked) }
        }

        if (inbox.blocked && !reportedInboxBlocked) {
            reportedInboxBlocked = true
            _state.update { it.copy(message = "需要在系统设置里允许访问所有文件，才能接收电脑发来的文件") }
            return
        }
        if (!inbox.blocked) reportedInboxBlocked = false

        if (inbox.lastReceivedAtMs > lastReportedReceiveAtMs) {
            lastReportedReceiveAtMs = inbox.lastReceivedAtMs
            val name = inbox.lastReceived ?: return
            _state.update { it.copy(message = "已接收「$name」到 下载/WirelessCable") }
        }
    }

    /** 用户点「重试」：清掉"试过了"的标记，让接收器再看它一次。 */
    fun retryReceive(taskIds: Collection<String>) {
        taskIds.forEach { InboxReceiver.retry(it) }
        viewModelScope.launch { InboxReceiver.pump(appContext, force = true) }
    }

    /** 启动一次落盘（不检查 busy；批量内部串行调用）。 */
    private fun startSave(item: TransferItem, destination: Uri) {
        saveJob = viewModelScope.launch {
            val current = ensureEngine()
            if (current == null) {
                _state.update { it.copy(message = "尚未连接电脑，请先在「设备」页配对") }
                return@launch
            }

            runCatching {
                resolver.takePersistableUriPermission(
                    destination,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
                )
            }

            _state.update { it.copy(busy = true, message = null) }
            try {
                when (val result = current.saveDownload(item, destination)) {
                    is TransferResult.Ok -> Unit
                    is TransferResult.Failed -> _state.update { it.copy(message = result.message) }
                    is TransferResult.Interrupted -> Unit // 已登记为可恢复，轮询会自动拉起
                    TransferResult.Cancelled -> Unit
                }
            } finally {
                _state.update { it.copy(busy = false) }
            }
        }
    }

    /**
     * 行上的「取消」确认后：作废队列、删记录、可选删已落盘的文件。
     *
     * 和 [cancelTasks] 不同：那个只标取消、记录留着让用户还能看到；
     * 这个是用户明确不要了，要从列表里消失，并且**不能让电脑继续下发剩下的文件**。
     */
    fun cancelAndDelete(taskIds: Collection<String>, deleteFiles: Boolean) {
        val ids = taskIds.distinct()
        if (ids.isEmpty()) return

        val items = _state.value.items.filter { it.taskId in ids }
        val dirs = items.mapNotNull { dirKeyOf(it) }.distinct()

        // 先让本机这一路停下：停了才删得掉。
        // 以前这里只作废队列、直接删文件——泵还在跑（整文件夹的循环、恢复协程），
        // 删完的目录马上又被写回去，用户看到的就是"勾了删除，文件还在"。
        items.forEach { item ->
            val key = item.batchId?.takeIf { it.isNotBlank() } ?: taskBatchKeys[item.taskId]
            if (!key.isNullOrBlank()) pausedBatches += key
        }
        ids.forEach { resumeJobs.remove(it)?.cancel() }

        viewModelScope.launch {
            val current = ensureEngine()

            uploadJob?.takeIf { it.isActive }?.cancelAndJoin()
            saveJob?.takeIf { it.isActive }?.cancelAndJoin()
            folderJob?.takeIf { it.isActive }?.cancelAndJoin()

            // 1) 队列里还没执行的整批作废：不作废的话手机会继续传，删了也白删
            dirs.forEach { dir -> current?.controlQueuedJobs(dir, "cancel") }

            // 2) 已落盘的文件（只有勾了"同时删除"才删）
            val outcome = if (deleteFiles) deleteSavedFiles(items) else DeleteOutcome.None()

            // 3) 本机记录与列表
            ids.forEach { id -> runCatching { db.transferDao().delete(id) } }
            current?.forget(ids)

            // 4) 批状态一并作废：留着的话下次「继续」会把已经删掉的文件重新建出来
            items.forEach { item ->
                val key = item.batchId?.takeIf { it.isNotBlank() } ?: taskBatchKeys[item.taskId]
                if (!key.isNullOrBlank()) {
                    folderBatches.remove(key)
                    uploadBatches.remove(key)
                }
            }
            ids.forEach { taskBatchKeys.remove(it) }

            // 5) 告诉服务端"取消"：作业作废、任务终结、**两端都删记录**
            //    （删记录是各删各的；取消必须双向，否则另一端还留着已传完的那些）。
            //    尽力而为：离线不影响本机已经删掉的事实。
            current?.cancelBatchOnServer(ids, deleteFiles)

            _state.update {
                it.copy(
                    selected = emptySet(),
                    message = buildString {
                        append("已取消 ${ids.size} 个任务")
                        append(outcome.describe())
                    },
                )
            }
        }
    }

    /**
     * 取消一个任务。
     *
     * 本机正在跑的任务要连协程一起停：只发取消请求的话，在途的分块还会继续落到
     * 目标文件里；由引擎在 CancellationException 分支里删掉半截文件并通知服务端。
     * 没有本机任务时（例如电脑刚建好、还没开始接收的下载）直接调服务端取消。
     */
    /**
     * 取消一批任务（一条也是批）。
     *
     * 整文件夹那一行挂着的所有任务一起取消：半截文件、分块位图、记录都清掉。
     * 先把本机这一路停下来再统一清，避免和引擎的取消分支抢同一个任务。
     */
    fun cancelTasks(taskIds: Collection<String>) {
        val ids = taskIds.distinct()
        if (ids.isEmpty()) return

        val items = _state.value.items.filter { it.taskId in ids }
        // 批量落盘的循环靠这个标志自己停：只取消 saveJob 的话，
        // join 一返回它就会接着拉下一个文件
        items.forEach { item ->
            val key = item.batchId?.takeIf { it.isNotBlank() } ?: taskBatchKeys[item.taskId]
            if (!key.isNullOrBlank()) pausedBatches += key
        }

        ids.forEach { resumeJobs.remove(it)?.cancel() }
        // 先登记"这是用户点的取消"，否则引擎的取消分支会当成内部取消 → 只标中断
        ids.forEach { engine?.markUserCancelled(it) }

        viewModelScope.launch {
            uploadJob?.takeIf { it.isActive }?.cancelAndJoin()
            saveJob?.takeIf { it.isActive }?.cancelAndJoin()
            folderJob?.takeIf { it.isActive }?.cancelAndJoin()

            val current = ensureEngine() ?: return@launch
            current.cancelAll(ids)
            _state.update {
                it.copy(message = if (ids.size > 1) "已取消 ${ids.size} 个任务" else null)
            }
        }
    }

    /**
     * 暂停一个任务。
     *
     * 先让服务端把状态标成 paused，再取消本机协程——引擎在取消分支里会看到
     * 暂停标记，保留临时文件与已传分块（取消则会删掉半截文件）。
     */
    /**
     * 暂停一批任务（一条也是批）。
     *
     * 整文件夹的 200 个任务是**一行**，用户点暂停是"这一批都停下"，
     * 不是"停下正在传的那个、剩下的接着传"：先一次请求标完服务端，再停本机。
     */
    fun pauseTasks(taskIds: Collection<String>) {
        val ids = taskIds.distinct()
        if (ids.isEmpty()) return

        val items = _state.value.items.filter { it.taskId in ids }

        // 批 key 可能来自服务端（批量接口建的任务），也可能是手机自己登记的上传/落盘批
        items.forEach { item ->
            val key = item.batchId?.takeIf { it.isNotBlank() } ?: taskBatchKeys[item.taskId]
            if (!key.isNullOrBlank()) pausedBatches += key
        }

        // 正在恢复的任务也算"在跑"：不停掉的话，点了暂停它们还会接着传
        ids.forEach { resumeJobs.remove(it)?.cancel() }

        // 队列里还没执行的文件没有任务可暂停；按落点目录先把作业停住，
        // 否则暂停后手机还会继续取走剩下的（用户看到的就是"按了暂停还在传"）
        val dirs = items.mapNotNull { dirKeyOf(it) }.distinct()

        viewModelScope.launch {
            val current = ensureEngine() ?: return@launch

            dirs.forEach { dir -> current.controlQueuedJobs(dir, "pause") }

            current.pauseAll(ids)?.let { message ->
                _state.update { it.copy(message = message) }
            }

            // 三个驱动都要停：一批里可能混着上传、单文件落盘和整文件夹落盘
            uploadJob?.takeIf { it.isActive }?.cancel()
            saveJob?.takeIf { it.isActive }?.cancel()
            folderJob?.takeIf { it.isActive }?.cancel()
        }
    }

    /**
     * 继续一批已暂停的任务。
     *
     * 以前这里有两个致命点：
     * 1) `saveJob/uploadJob` 还活着就直接 return —— 暂停只 cancel 不 join，
     *    用户立刻点「继续」等于什么都没发生（"暂停了不能恢复"）；
     * 2) 恢复是逐个 `join()` 串行跑的，`saveDownload` 要传完整个文件才返回，
     *    43 个任务只会在后台排队，界面上看就是卡住不动。
     *
     * 现在：先把上一次的驱动协程收干净，放行服务端队列，再把批的泵重新拉起来；
     * 剩下的单任务用 2 个并发恢复，失败原因直接显示，不再静默。
     */
    fun resumeTasks(taskIds: Collection<String>) {
        val ids = taskIds.distinct()
        if (ids.isEmpty()) return

        val items = _state.value.items.filter { it.taskId in ids && it.state == TransferStates.PAUSED }
        if (items.isEmpty()) return

        // 找到这些任务所属的批，先放行
        val keys = items
            .mapNotNull { item -> item.batchId?.takeIf { it.isNotBlank() } ?: taskBatchKeys[item.taskId] }
            .distinct()
        keys.forEach { pausedBatches -= it }

        val dirs = items.mapNotNull { dirKeyOf(it) }.distinct()

        viewModelScope.launch {
            val current = ensureEngine()
            if (current == null) {
                _state.update { it.copy(message = "尚未连接电脑，请先在「设备」页配对") }
                return@launch
            }

            // 上一次暂停的协程可能还在收尾（引擎在取消分支里写状态）。等它结束，
            // 别让两路同时写同一个任务。
            uploadJob?.takeIf { it.isActive }?.cancelAndJoin()
            saveJob?.takeIf { it.isActive }?.cancelAndJoin()

            // 先把队列放行，再恢复任务：顺序反过来会让作业停在 paused 里没人取
            dirs.forEach { dir -> current.controlQueuedJobs(dir, "resume") }

            // **显式告诉服务端"这些要继续"**：泵是自动流程，不碰服务端状态，
            // 少了这一步，用户点「继续」后泵虽然重跑，服务端仍是 paused，
            // 第一块就被 409 顶回来——表现为"点了继续没反应"。
            val resumeError = current.resumeBatch(ids)
            if (resumeError != null) {
                _state.update { it.copy(message = resumeError) }
                return@launch
            }

            // 批里还没建任务的文件：把泵重新拉起来（已完成的跳过、暂停的续传）
            keys.forEach { key ->
                if (folderBatches.containsKey(key)) pumpFolderBatch(key)
                if (uploadBatches.containsKey(key)) pumpUploads(key)
            }

            // 泵会自己处理批内任务，这里只恢复"不属于任何批"的
            val singles = items.filter { item ->
                val key = item.batchId?.takeIf { it.isNotBlank() } ?: taskBatchKeys[item.taskId]
                key == null || (!folderBatches.containsKey(key) && !uploadBatches.containsKey(key))
            }

            singles.forEach { item ->
                while (resumeJobs.size >= MAX_RESUME_WORKERS) delay(100)
                startRestore(current, item)
            }
        }
    }

    /** 恢复一个已暂停的任务；失败原因要显示出来，不能点完毫无反应。 */
    private fun startRestore(current: TransferEngine, item: TransferItem) {
        if (resumeJobs.containsKey(item.taskId)) return

        val job = viewModelScope.launch {
            val entity = runCatching { db.transferDao().get(item.taskId) }.getOrNull()
            if (entity == null) {
                _state.update { it.copy(message = "本地记录已不存在，无法续传") }
                return@launch
            }

            val result = if (item.isUpload) {
                if (entity.sourceUri == null) {
                    _state.update { it.copy(message = "缺少源文件授权，无法续传") }
                    return@launch
                }
                current.resumeUpload(item.taskId)
            } else {
                if (entity.destinationUri == null) {
                    _state.update { it.copy(message = "缺少保存位置，无法续传") }
                    return@launch
                }
                current.saveDownload(item, null)
            }

            if (result is TransferResult.Failed) {
                if (result.code == TransferErrorCodes.TRANSFER_CANCELLED) {
                    // 对面删了这条正在传的任务：继续不会再有任何数据。问用户要不要清记录。
                    _state.update { it.copy(peerCancelled = it.peerCancelled + item.taskId) }
                } else {
                    _state.update { it.copy(message = result.message) }
                }
            }
        }

        resumeJobs[item.taskId] = job
        job.invokeOnCompletion { resumeJobs.remove(item.taskId) }
    }

    /**
     * 落点目录：`destination_path` 就是"相对路径去掉文件名"那一段。
     *
     * 以前只取首段，多级目录（相册/2024）会误伤同名前缀的其它批；
     * 整批目录对不上时服务端就一个作业都停不住。
     */
    private fun dirKeyOf(item: TransferItem): String? {
        val path = item.relativePath
        if (path.isBlank()) return item.batchName?.takeIf { it.isNotBlank() }

        val slash = path.lastIndexOf('/')
        return if (slash > 0) path.substring(0, slash) else item.batchName?.takeIf { it.isNotBlank() }
    }

    /** 删除本机文件的统计：整个文件夹算 1 项，不再一个个数文件。 */
    private data class DeleteOutcome(val folders: Int, val files: Int, val failed: Int) {
        fun describe(): String = buildString {
            if (folders > 0) append("、删除 $folders 个文件夹")
            if (files > 0) append("、删除 $files 个文件")
            if (failed > 0) append("、$failed 项删除失败")
        }

        companion object {
            fun None() = DeleteOutcome(0, 0, 0)
        }
    }

    /**
     * 删掉这些任务在本机落下的文件。
     *
     * 整文件夹的批按**顶层文件夹整删**（SAF 的 deleteDocument 对目录是递归的）：
     * 以前一个个删文件，文件没了但一层层空目录全留着，用户还得自己去文件管理器收拾。
     * 一批只删一次，同批其余条目直接算已删；整删失败才退回逐个文件。
     */
    private suspend fun deleteSavedFiles(items: List<TransferItem>): DeleteOutcome {
        var folders = 0
        var files = 0
        var failed = 0
        // 整删成功的文件夹：同批其余条目跟着算已删，不再逐个碰
        val removed = mutableSetOf<String>()
        // 试过整删（失败才退回逐文件，不重复尝试同一个目录）
        val attempted = mutableSetOf<String>()

        items.filter { !it.isUpload }.forEach { item ->
            val entity = runCatching { db.transferDao().get(item.taskId) }.getOrNull()
            val stored = entity?.destinationUri
            if (stored.isNullOrBlank()) {
                // 应该是本机文件却找不到落点（服务端合并来的条目没有本地记录）：
                // 不能装作删了，也不能静默
                if (item.savedToPhone) failed++
                return@forEach
            }

            val folder = SavedFiles.batchFolderOf(stored, item.relativePath)
            if (folder != null) {
                if (folder.docId in removed) return@forEach

                if (folder.docId !in attempted) {
                    attempted += folder.docId
                    if (SavedFiles.deleteTree(resolver, folder)) {
                        removed += folder.docId
                        folders++
                        return@forEach
                    }
                }
                // 整删失败：退回逐个文件，至少把内容删掉
            }

            if (SavedFiles.delete(resolver, stored)) files++ else failed++
        }

        return DeleteOutcome(folders, files, failed)
    }


    // ------------------------------------------------------------ 内部

    /**
     * 自动恢复中断的任务。
     *
     * 只恢复本地有完整凭据的：上传要有源 URI（选择时已取持久授权），
     * 下载要有落点 URI。缺凭据的任务留在「已中断」状态，由用户手动处理。
     * 并发上限 2：手机资源有限，盲目并行只会拖慢每一个任务。
     *
     * 落点在收件目录的下载**不在这里续**（1.5.6）：那是 [InboxReceiver] 的活，
     * 它由会话循环驱动，App 在后台也能续；这里再插一手就会两边同时写同一个文件。
     */
    private fun resumeInterrupted(current: TransferEngine) {
        val candidates = _state.value.items.filter { item ->
            item.state == TransferStates.INTERRUPTED &&
                !resumeJobs.containsKey(item.taskId) &&
                resumeJobs.size < MAX_RESUME_WORKERS
        }

        for (item in candidates) {
            val job = viewModelScope.launch {
                val entity = db.transferDao().get(item.taskId) ?: return@launch

                val result = when (item.direction) {
                    TransferDirections.UPLOAD ->
                        if (entity.sourceUri != null) {
                            current.resumeUpload(item.taskId, allowResume = false)
                        } else {
                            null
                        }

                    else ->
                        if (entity.destinationUri != null && !InboxStore.isInboxUri(entity.destinationUri)) {
                            current.saveDownload(item, null, allowResume = false)
                        } else {
                            null
                        }
                }

                if (result is TransferResult.Failed) {
                    _state.update { it.copy(message = result.message) }
                }
            }

            resumeJobs[item.taskId] = job
            job.invokeOnCompletion { resumeJobs.remove(item.taskId) }
        }
    }

    /**
     * 取当前可用的引擎。地址或令牌变化时重建，否则复用现有实例，
     * 避免每次刷新都新建 OkHttp 客户端（连接池与 TLS 会话会被丢弃）。
     */
    /**
     * 设置上传落点（共享目录 id）。null 表示用电脑的默认接收目录。
     *
     * 落点必须是该设备**有 Upload 权限**的共享目录——服务端也会再校验一次，
     * 这里先筛一遍是为了不给用户选一个注定失败的目录。
     */
    fun setUploadTarget(rootId: String?) {
        _state.update { it.copy(selectedUploadTargetId = rootId) }
    }

    /** 拉取可作为上传落点的共享目录。 */
    private suspend fun refreshUploadTargets() {
        val api = ensureFileApi() ?: return
        val token = tokenOf() ?: return
        when (val outcome = api.roots(token)) {
            is ApiOutcome.Success -> {
                val targets = outcome.value
                    .filter { it.kind == FileProtocol.KIND_SHARED }
                    .filter { (it.permissions and PERMISSION_UPLOAD) != 0 }
                    .map { UploadTargetOption(it.rootId, it.displayName) }

                _state.update { current ->
                    // 选中的目录若已被取消授权或移除，回落到默认接收目录
                    val stillValid = current.selectedUploadTargetId == null ||
                        targets.any { it.rootId == current.selectedUploadTargetId }
                    current.copy(
                        uploadTargets = targets,
                        selectedUploadTargetId = if (stillValid) current.selectedUploadTargetId else null,
                    )
                }
            }

            is ApiOutcome.Failure -> Unit // 拉不到就不动已有的落点选择
        }
    }

    /** 取（或重建）文件管理 API 实例。 */
    private suspend fun ensureFileApi(): FileApi? {
        val session = repository.loadSession() ?: return null
        val token = vault.load(session.deviceId) ?: return null
        val key = "${session.host}:${session.port}|$token"

        fileApi?.let { existing -> if (key == fileApiKey) return existing }

        val endpoint = ServerEndpoint(session.host, session.port)
        val created = FileApi.create(endpoint, setOf(session.fingerprint))
        fileApi = created
        fileApiKey = key
        return created
    }

    private suspend fun tokenOf(): String? {
        val session = repository.loadSession() ?: return null
        return vault.load(session.deviceId)
    }

    private suspend fun ensureEngine(): TransferEngine? {
        // 引擎归属应用级（TransferRuntime）：作业执行在前台服务里也要用它，
        // 两个实例各自维护一份进度会互相覆盖。
        val created = TransferRuntime.ensure() ?: return null

        if (created !== engine) {
            collectJob?.cancel()
            engine = created
            collectJob = viewModelScope.launch {
                created.items.collect { items -> _state.update { it.copy(items = items) } }
            }
        }

        return created
    }

    // ------------------------------------------------------------ 页签与多选

    fun selectFilter(filter: TransferFilter) {
        if (_state.value.direction == filter) return
        _state.update { it.copy(direction = filter, selected = emptySet()) }
    }

    fun selectTab(tab: TransferTab) {
        if (_state.value.tab == tab) return
        // 切页签清空选择：勾选跨页保留会让"已选 N 项"数不到
        _state.update { it.copy(tab = tab, selected = emptySet()) }
    }

    /** 长按进入多选：先只选中被长按的那一项。 */
    /**
     * 进入多选。
     *
     * 传的是一**行**覆盖的所有任务 id：整文件夹那一行是几十上百个 id，
     * 选中它就等于选中整批（暂停/删除都按整批走）。
     */
    fun startSelection(taskIds: Collection<String>) {
        if (taskIds.isEmpty()) return
        _state.update { it.copy(selected = taskIds.toSet()) }
    }

    /** 勾选/取消一整行：整批要么全选中，要么全不选。 */
    fun toggleSelect(taskIds: Collection<String>) {
        if (taskIds.isEmpty()) return
        val ids = taskIds.toSet()
        _state.update { current ->
            val next = if (ids.all { it in current.selected }) {
                current.selected - ids
            } else {
                current.selected + ids
            }
            current.copy(selected = next)
        }
    }

    fun selectAll() {
        _state.update { current ->
            current.copy(selected = current.rows.flatMap { it.taskIds }.toSet())
        }
    }

    fun clearSelection() = _state.update { it.copy(selected = emptySet()) }

    /**
     * 删除选中的传输记录。
     *
     * [deleteFiles] 作用于**本机收到的文件**（电脑发过来的），发出去的文件在电脑上，
     * 手机没有资格删；服务端的删除接口也只处理手机传过去的那些。
     */
    fun deleteSelected(deleteFiles: Boolean, onlyIds: Collection<String>? = null) {
        // onlyIds 非空时按任务 id 删（"已被对方取消，要删记录吗"走这条路），
        // 否则用当前多选。两条路共用同一段删除逻辑，避免清理口径分叉。
        val items = if (onlyIds == null) {
            _state.value.selectedItems
        } else {
            _state.value.items.filter { it.taskId in onlyIds }
        }
        if (items.isEmpty()) return

        // **正在传的不给删**（需求 4）：传输中的条目只有「取消传输」一个动作。
        // 删记录是"清理历史"，不该顺手把正在传的这条也终止掉——
        // 那是"取消"该干的事（取消会双向生效：另一方也停、也删记录）。
        val deletable = items.filter { !TransferStates.isActive(it.state) }
        val skipped = items.size - deletable.size
        if (deletable.isEmpty()) {
            _state.update { it.copy(message = "选中的任务还在传输，请先取消") }
            return
        }

        val ids = deletable.map { it.taskId }

        viewModelScope.launch {
            val outcome = if (deleteFiles) deleteSavedFiles(deletable) else DeleteOutcome.None()

            // 记录先在本机删掉：断联时电脑删不了，但"我这条记录不要了"是本机的决定。
            // 电脑端只做尽力清理，失败不提示——否则离线时用户根本删不掉任何记录。
            ids.forEach { id -> runCatching { db.transferDao().delete(id) } }
            engine?.forget(ids)

            // 批状态与映射一起清：暂停中的批留着的话，下次点到「继续」
            // 会把已经删掉的文件重新建一遍（同 cancelAndDelete）
            items.forEach { item ->
                val key = item.batchId?.takeIf { it.isNotBlank() } ?: taskBatchKeys[item.taskId]
                if (!key.isNullOrBlank()) {
                    folderBatches.remove(key)
                    uploadBatches.remove(key)
                }
            }
            ids.forEach { taskBatchKeys.remove(it) }

            _state.update {
                it.copy(
                    selected = emptySet(),
                    message = buildString {
                        append("已删除 ${ids.size} 条记录")
                        if (skipped > 0) append("（${skipped} 条还在传输，请用「取消任务」）")
                        append(outcome.describe())
                    },
                )
            }

            runCatching {
                val api = ensureControlApi()
                val token = tokenOf()
                // 只删记录，不删电脑上的文件
                if (api != null && token != null) api.deleteRecords(token, ids, false)
            }
        }
    }

    /**
     * 用户对"传输已被接收方取消"的询问做了选择。
     *
     * [delete] 为 true 时按任务 id 走一遍正常删除（记录 + 可选文件），
     * 否则只把提示关掉、记录留着（用户可能还想留个痕迹）。
     */
    fun resolvePeerCancelled(delete: Boolean) {
        val ids = _state.value.peerCancelled
        _state.update { it.copy(peerCancelled = emptyList()) }
        if (!delete || ids.isEmpty()) return

        deleteSelected(deleteFiles = false, onlyIds = ids)
    }

    /** 打开已保存到本机的文件（收件目录或用户选定的落点）。 */
    fun openSaved(taskId: String) {
        viewModelScope.launch {
            val entity = runCatching { db.transferDao().get(taskId) }.getOrNull() ?: return@launch
            val stored = entity.destinationUri ?: return@launch

            val target: Uri? = when {
                stored.startsWith("file:") -> {
                    val path = Uri.parse(stored).path
                    val file = path?.let { File(it) }
                    if (file != null && file.exists()) InboxStore.uriFor(appContext, file) else null
                }

                else -> Uri.parse(stored)
            }

            if (target == null) {
                _state.update { it.copy(message = "文件已不存在，可能被清理了") }
                return@launch
            }

            val mime = MimeTypeMap.getSingleton()
                .getMimeTypeFromExtension(target.lastPathSegment?.substringAfterLast('.', "")?.lowercase())
                ?: "*/*"

            runCatching {
                appContext.startActivity(
                    Intent(Intent.ACTION_VIEW).apply {
                        setDataAndType(target, mime)
                        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                    },
                )
            }.onFailure {
                _state.update { it.copy(message = "没有能打开该文件的应用") }
            }
        }
    }

    private suspend fun ensureControlApi(): TransferApi? {
        val session = repository.loadSession() ?: return null
        val token = vault.load(session.deviceId) ?: return null
        val key = "${session.host}:${session.port}|$token"

        controlApi?.let { existing -> if (key == controlApiKey) return existing }

        val endpoint = ServerEndpoint(session.host, session.port)
        val created = TransferApi.create(endpoint, setOf(session.fingerprint))
        controlApi = created
        controlApiKey = key
        return created
    }

    override fun onCleared() {
        pollJob?.cancel()
        collectJob?.cancel()
        engine = null
        super.onCleared()
    }

    companion object {
        private const val ACTIVE_POLL_MS = 1_200L
        private const val IDLE_POLL_MS = 4_000L
        private const val SESSION_RETRY_MS = 3_000L
        private const val MAX_RESUME_WORKERS = 2

        /** 一次性提示在页面上停留多久（见 [expireMessage]）。 */
        private const val MESSAGE_TTL_MS = 6_000L

        /** Upload 权限位（与 C# 端 SharedDirectoryPermissions.Upload 一致）。 */
        private const val PERMISSION_UPLOAD = 1 shl 2

        fun factory(
            repository: ConnectionRepository,
            vault: TokenVault,
            appContext: Context,
        ) = viewModelFactory {
            initializer {
                TransferViewModel(
                    repository = repository,
                    vault = vault,
                    resolver = appContext.contentResolver,
                    db = AppDatabase.get(appContext),
                    tempDir = File(appContext.getExternalFilesDir(null), "transfers"),
                    appContext = appContext,
                )
            }
        }
    }
}

/** 一次上传的落点：共享目录 id + 该目录下的相对子目录。 */
data class UploadTarget(
    val directoryId: String?,
    val relativeDirectory: String?,
)

/** 批量下载里的一个文件：根 id + 相对路径 + 文件名。 */
data class BatchDownloadItem(
    val rootId: String,
    val path: String,
    val name: String,
)
