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
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.FileApi
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.network.TransferApi
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.TransferDirections
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
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
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

                        if (error == null) {
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
        val file = File(directory, "分享文本-$stamp.txt")

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
                for (item in items) {
                    // 授权持久化：APP 被杀后重新打开仍能凭 Room 里的 URI 续传
                    // （file:// 没有可持久化的授权，抛异常也无所谓）
                    runCatching {
                        resolver.takePersistableUriPermission(
                            item.uri,
                            Intent.FLAG_GRANT_READ_URI_PERMISSION,
                        )
                    }

                    val directoryId = target?.directoryId ?: _state.value.selectedUploadTargetId
                    val relativeDirectory = target?.relativeDirectory
                    val result = current.upload(
                        item.uri,
                        directoryId = directoryId,
                        relativeDirectory = relativeDirectory,
                        displayName = item.displayName,
                    )
                    when (result) {
                        is TransferResult.Ok -> ok++
                        is TransferResult.Failed -> if (firstError == null) firstError = result.message
                        is TransferResult.Interrupted -> interrupted++
                        TransferResult.Cancelled -> break
                    }
                }
            } finally {
                _state.update {
                    it.copy(
                        busy = false,
                        message = when {
                            items.size == 1 && firstError != null -> firstError
                            firstError != null -> "已发送 $ok 个，${items.size - ok} 个失败：$firstError"
                            interrupted > 0 -> "连接中断，$interrupted 个任务将在恢复后续传"
                            else -> null
                        },
                    )
                }
            }
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
     * 任务已经由服务端建好并随响应带回来，这里只负责逐个落盘。
     * 子目录用 DocumentsContract 一层层建；已存在的目录复用（不重复建）。
     */
    fun saveFolderBatch(batchName: String, tasks: List<TransferTask>, treeUri: Uri) {
        if (tasks.isEmpty()) return
        if (_state.value.busy) {
            _state.update { it.copy(message = "已有传输在进行，请稍候") }
            return
        }

        viewModelScope.launch {
            _state.update {
                it.copy(busy = true, message = "正在接收「$batchName」共 ${tasks.size} 个文件")
            }

            var ok = 0
            var failed = 0
            var firstError: String? = null
            val batchId = tasks.firstOrNull()?.batchId

            for (task in tasks) {
                // 用户整批暂停/取消：循环必须自己停，否则会接着一个个拉下去
                if (batchId != null && batchId in pausedBatches) break

                // relativePath 形如「文件夹名/子目录/文件名」；用户选的目标目录就是"文件夹名"的位置
                val segments = task.relativePath
                    .split('/')
                    .filter { it.isNotBlank() }
                    .drop(1)
                    .toMutableList()
                segments.add(task.fileName)

                val target = createDocumentAt(treeUri, segments)
                if (target == null) {
                    failed++
                    firstError = firstError ?: "建不出 ${task.relativePath}"
                    continue
                }

                startSave(transferItemOf(task, task.fileName), target)
                ok++
                saveJob?.join()
            }

            _state.update {
                it.copy(
                    busy = false,
                    message = when {
                        failed > 0 && ok == 0 -> "接收「$batchName」失败：${firstError ?: "建不出目录"}"
                        failed > 0 -> "「$batchName」已收 $ok 个，$failed 个失败"
                        else -> "「$batchName」已收到所选目录（$ok 个文件）"
                    },
                )
            }
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
        items.mapNotNull { it.batchId }.forEach { pausedBatches += it }

        ids.forEach { resumeJobs.remove(it)?.cancel() }
        // 先登记"这是用户点的取消"，否则引擎的取消分支会当成内部取消 → 只标中断
        ids.forEach { engine?.markUserCancelled(it) }

        val running = if (items.any { it.isUpload }) uploadJob else saveJob

        viewModelScope.launch {
            if (running?.isActive == true) {
                running.cancel()
                running.join()
            }

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
        items.mapNotNull { it.batchId }.forEach { pausedBatches += it }

        val running = if (items.any { it.isUpload }) uploadJob else saveJob

        viewModelScope.launch {
            val current = ensureEngine() ?: return@launch

            current.pauseAll(ids)?.let { message ->
                _state.update { it.copy(message = message) }
            }

            if (running?.isActive == true) {
                running.cancel()
            }
        }
    }

    /** 继续一个已暂停的任务：按本地记录里的凭据续传，不重传已完成分块。 */
    /**
     * 继续一批已暂停的任务。
     *
     * 落点沿用 Room 里记的 URI（整文件夹是逐个文件记的），不用重新选目录。
     * 一个接一个来：并行写同一个 SAF 目录会互相干扰。
     */
    fun resumeTasks(taskIds: Collection<String>) {
        val items = _state.value.items.filter { it.taskId in taskIds && it.state == TransferStates.PAUSED }
        if (items.isEmpty()) return
        if (saveJob?.isActive == true || uploadJob?.isActive == true) return

        items.mapNotNull { it.batchId }.forEach { pausedBatches -= it }

        viewModelScope.launch {
            val current = ensureEngine() ?: return@launch

            items.forEach { item ->
                if (item.isUpload) {
                    current.resumeUpload(item.taskId)
                    return@forEach
                }

                saveJob = viewModelScope.launch { current.saveDownload(item, null) }
                saveJob?.join()
            }
        }
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
                        if (entity.sourceUri != null) current.resumeUpload(item.taskId) else null

                    else ->
                        if (entity.destinationUri != null && !InboxStore.isInboxUri(entity.destinationUri)) {
                            current.saveDownload(item, null)
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
     * [deleteFiles] 只作用于电脑上的文件，而且服务端只会删手机传过去的；
     * 电脑发过来的文件永远不动。
     */
    fun deleteSelected(deleteFiles: Boolean) {
        val items = _state.value.selectedItems
        if (items.isEmpty()) return
        val ids = items.map { it.taskId }

        viewModelScope.launch {
            var filesDeleted = 0

            if (deleteFiles) {
                items.filter { !it.isUpload }.forEach { item ->
                    val entity = runCatching { db.transferDao().get(item.taskId) }.getOrNull()
                    val stored = entity?.destinationUri
                    val ok = when {
                        stored.isNullOrBlank() -> false
                        stored.startsWith("file:") -> runCatching {
                            val path = Uri.parse(stored).path ?: return@runCatching false
                            File(path).delete()
                        }.getOrDefault(false)

                        else -> runCatching {
                            resolver.delete(Uri.parse(stored), null, null) > 0
                        }.getOrDefault(false)
                    }
                    if (ok) filesDeleted++
                }
            }

            // 记录先在本机删掉：断联时电脑删不了，但"我这条记录不要了"是本机的决定。
            // 电脑端只做尽力清理，失败不提示——否则离线时用户根本删不掉任何记录。
            ids.forEach { id -> runCatching { db.transferDao().delete(id) } }
            engine?.forget(ids)

            _state.update {
                it.copy(
                    selected = emptySet(),
                    message = buildString {
                        append("已删除 ${ids.size} 条记录")
                        if (filesDeleted > 0) append("、$filesDeleted 个文件")
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
