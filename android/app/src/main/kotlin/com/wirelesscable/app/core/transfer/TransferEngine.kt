package com.wirelesscable.app.core.transfer

import android.content.ContentResolver
import android.net.Uri
import android.os.SystemClock
import android.provider.DocumentsContract
import android.system.Os
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.NetworkErrorCodes
import com.wirelesscable.app.core.network.TransferApi
import com.wirelesscable.app.core.protocol.ChunkAck
import com.wirelesscable.app.core.protocol.TransferDirections
import com.wirelesscable.app.core.protocol.TransferPurposes
import com.wirelesscable.app.core.protocol.TransferErrorCodes
import com.wirelesscable.app.core.protocol.ChunkStates
import com.wirelesscable.app.core.protocol.TransferConflictPolicies
import com.wirelesscable.app.core.protocol.TransferStartRequest
import com.wirelesscable.app.core.protocol.TransferStates
import com.wirelesscable.app.core.protocol.TransferTask
import com.wirelesscable.app.data.db.AppDatabase
import com.wirelesscable.app.data.db.TransferChunkEntity
import com.wirelesscable.app.data.db.TransferEntity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.withContext
import okhttp3.RequestBody
import okio.BufferedSink
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.io.RandomAccessFile
import java.security.MessageDigest

private const val COPY_BUFFER = 64 * 1024
private const val REPORT_INTERVAL_MS = 150L

/** 两次服务端列表请求的最小间隔（多方轮询共用一次真实请求）。 */
private const val MIN_REFRESH_INTERVAL_MS = 1_500L
private const val MAX_CHUNK_ATTEMPTS = 3
private val HEX_CHARS = "0123456789abcdef".toCharArray()

/** 一条传输任务在手机上需要展示与恢复的全部信息。 */
data class TransferItem(
    val taskId: String,
    val direction: String,
    val fileName: String,
    val deviceName: String,
    val totalBytes: Long,
    val transferredBytes: Long,
    val chunkSize: Int = 0,
    val chunkCount: Int = 0,
    val state: String = TransferStates.WAITING,
    /** 下载去向（[TransferPurposes]）；上传任务恒为 inbox，不参与判断。 */
    val purpose: String = TransferPurposes.INBOX,
    val sha256: String? = null,
    val errorMessage: String? = null,
    val bytesPerSecond: Long = 0L,
    val updatedAt: Long = 0L,
    val savedToPhone: Boolean = false,
    /** 整文件夹传输的分组键；单文件任务为空。 */
    val batchId: String? = null,
    /**
     * 落点相对路径（'/' 分隔）。整文件夹下载时形如 `相册/1.jpg`，
     * 界面据此把同一目录的几百条任务收成一行——这些任务逐条创建，没有 batchId。
     */
    val relativePath: String = "",
    /** 批次文件夹名。 */
    val batchName: String? = null,
) {
    val isActive: Boolean get() = TransferStates.isActive(state)
    val isUpload: Boolean get() = direction == TransferDirections.UPLOAD

    /**
     * 这一行现在就能用默认应用打开吗？
     *
     * 只有"电脑发来 + 已完成 + 已落盘"三个条件都满足才有本地文件可开；
     * 自己发出去的文件在电脑上，手机这边没有可打开的东西。
     */
    val isOpenableHere: Boolean
        get() = !isUpload && state == TransferStates.COMPLETED && savedToPhone

    /** 电脑端发来、还没落到手机本地的文件：界面据此给出「保存到手机」。 */
    val needsSave: Boolean
        get() = !isUpload &&
            // directory 任务的落点由作业带着 SAF URI 去写，界面不该给"接收中/重试"
            purpose == TransferPurposes.INBOX &&
            !savedToPhone &&
            state != TransferStates.CANCELLED &&
            state != TransferStates.TRANSFERRING &&
            state != TransferStates.VERIFYING
    val progress: Float
        get() = when {
            totalBytes <= 0L -> if (TransferStates.isTerminal(state)) 1f else 0f
            else -> (transferredBytes.toFloat() / totalBytes).coerceIn(0f, 1f)
        }
}

/** 一次上传/下载的结果。UI 只消费这个，不接触异常。 */
sealed interface TransferResult {
    data class Ok(val taskId: String) : TransferResult
    data class Failed(val code: String, val message: String) : TransferResult

    /** 网络中断：任务数据保留，连接恢复后可续传。 */
    data class Interrupted(val taskId: String, val message: String) : TransferResult
    data object Cancelled : TransferResult
}

/**
 * 手机侧传输引擎。
 *
 * 职责边界：只做「读源 → 切块 → 发请求 → 落盘 → 更新进度」，不碰 Compose，不碰 ViewModel。
 *
 * 断点续传纪律：
 * - 分块状态双写：服务端回执成功才写本地 Room，恢复时以服务端位图为准；
 * - 本地记录完成但服务端未知的块，先比对服务端分块哈希再补回执，不一致就重传；
 * - 下载先写应用私有临时文件（预分配），整文件校验通过才复制到用户选定的落点；
 * - 网络错误一律转 interrupted，绝不把"没传完"说成"失败"。
 */
class TransferEngine(
    private val api: TransferApi,
    private val token: String,
    private val resolver: ContentResolver,
    private val db: AppDatabase,
    private val tempDir: File,
) {

    private val _items = MutableStateFlow<List<TransferItem>>(emptyList())
    val items: StateFlow<List<TransferItem>> = _items.asStateFlow()

    private val samples = mutableMapOf<String, SpeedMeter>()
    private val lastReportMs = mutableMapOf<String, Long>()

    /** 上次真正发列表请求的时刻（`elapsedRealtime`），用于多方轮询合并。 */
    @Volatile
    private var lastRefreshAtMs = 0L

    /** 从服务端拉任务列表，合并进本地状态。返回错误提示（成功为 null）。 */
    suspend fun refresh(force: Boolean = false): String? {
        /*
         * 多方轮询合并成一次真实请求。
         *
         * 传输页在前台时每 2–5 秒拉一次，后台接收器每 5 秒拉一次，作业恢复还要再拉——
         * 各拉各的会让整机请求量翻倍，服务端限流直接回 429，手机把心跳失败显示成
         * "连接中断"（1.5.6 真机踩到）。节流放在引擎里，任何调用方都受益。
         */
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastRefreshAtMs < MIN_REFRESH_INTERVAL_MS) return null
        lastRefreshAtMs = now

        return when (val outcome = api.tasks(token)) {
            is ApiOutcome.Success -> {
                merge(outcome.value)
                null
            }

            is ApiOutcome.Failure -> outcome.message
        }
    }

    /**
     * 把指定任务的完整内容拉到 [destination]。
     *
     * 用于「电脑上传文件到手机目录」：电脑建好下载任务后，手机把它拉到 SAF 目标文件。
     * 走的是与普通下载完全相同的分块链路——SHA-256 校验、断点续传都复用现成能力。
     *
     * @return 成功为 null，失败为原因文本。
     */
    suspend fun pullTo(taskId: String, destination: android.net.Uri): String? {
        // 作业可能比任务列表先到（电脑刚建好任务就下发作业）。
        // 本地没有就先拉一次任务列表再找，否则会误报"任务不存在"。
        var item = _items.value.firstOrNull { it.taskId == taskId }
        if (item == null) {
            // 作业可能比任务列表先到，这一下必须真的去问服务端，不能吃节流
            refresh(force = true)
            item = _items.value.firstOrNull { it.taskId == taskId }
        }

        item ?: return "传输任务不存在或尚未同步"

        return when (val result = saveDownload(item, destination)) {
            is TransferResult.Ok -> null
            is TransferResult.Failed -> result.message
            is TransferResult.Interrupted -> "传输已中断，稍后会自动续传"
            TransferResult.Cancelled -> "传输已取消"
        }
    }

    // ------------------------------------------------------------ 上传

    /** 上传一个 SAF 文件。建任务前先算整文件哈希（续传时免于重读源文件）。 */
    /**
     * 上传一个 SAF 文件。
     *
     * @param directoryId 目标共享目录 id；null 表示电脑的默认接收目录。
     * @param relativeDirectory 该目录下的相对子目录；null 或空表示目录根。
     */
    suspend fun upload(
        uri: Uri,
        directoryId: String? = null,
        relativeDirectory: String? = null,
        /** 覆盖显示名：传应用时用应用名，否则对面只会看到 base.apk。 */
        displayName: String? = null,
        onTaskCreated: (String) -> Unit = {},
    ): TransferResult {
        val source = SafSource(resolver, uri, displayName)

        val meta = try {
            source.meta()
        } catch (e: SecurityException) {
            return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "文件授权已失效，请重新选择")
        } ?: return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "无法读取该文件")

        if (meta.sizeBytes < 0) {
            return TransferResult.Failed(
                TransferErrorCodes.FILE_INACCESSIBLE,
                "无法获取文件大小，暂不支持上传该来源",
            )
        }

        // 整文件哈希：complete 时的声明，也是续传免于重读源文件的凭据
        val wholeSha = try {
            hashStream(source.openRead())
        } catch (e: SecurityException) {
            return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "文件授权已失效，请重新选择")
        } catch (e: IOException) {
            return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "读取文件失败")
        }

        val started = api.uploadStart(
            token,
            TransferStartRequest(
                fileName = meta.displayName,
                totalBytes = meta.sizeBytes,
                sha256 = wholeSha,
                conflict = TransferConflictPolicies.RENAME,
                directoryId = directoryId,
                relativeDirectory = relativeDirectory?.takeIf { it.isNotBlank() },
            ),
        )

        val task = when (started) {
            is ApiOutcome.Failure -> return TransferResult.Failed(started.code, started.message)
            is ApiOutcome.Success -> started.value
        }

        onTaskCreated(task.taskId)
        upsertEntity(
            TransferEntity(
                taskId = task.taskId,
                direction = TransferDirections.UPLOAD,
                fileName = task.fileName,
                totalBytes = task.totalBytes,
                transferredBytes = 0L,
                status = TransferStates.PREPARING,
                updatedAtUtc = System.currentTimeMillis(),
                chunkSize = task.chunkSize,
                chunkCount = task.chunkCount,
                sourceUri = uri.toString(),
                declaredSha256 = wholeSha,
            ),
        )
        mirrorServerChunks(task.taskId)

        putItem(
            TransferItem(
                taskId = task.taskId,
                direction = TransferDirections.UPLOAD,
                fileName = task.fileName,
                deviceName = task.deviceName,
                totalBytes = task.totalBytes,
                transferredBytes = 0L,
                chunkSize = task.chunkSize,
                chunkCount = task.chunkCount,
                state = TransferStates.PREPARING,
                updatedAt = System.currentTimeMillis(),
            ),
        )

        return runUpload(task.taskId, uri, task.totalBytes, wholeSha)
    }

    /** 续传一个 Room 里还留着的上传任务（APP 被杀 / 断网之后）。 */
    /**
     * 被用户暂停的任务 id。
     *
     * 暂停与取消走的是同一条"取消协程"路径，区别只在取消异常的处理分支：
     * 在这个集合里的，保留临时文件、不再上报取消，只把状态落成 PAUSED。
     */
    private val pausedTasks = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

    /**
     * 用户主动取消的任务。
     *
     * 只有登记过的，取消协程时才回发 `cancel` 把电脑上的任务一起停掉；
     * 其余取消（页面销毁、会话重启、作业重排、上传挤占）一律按"中断"处理。
     * 真机踩过：手机点发送的同时，电脑发来的 1.45 GB 下载被判成"用户取消"。
     */
    private val userCancels = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

    /** 界面点「取消任务」时先登记，再取消协程。 */
    fun markUserCancelled(taskId: String) {
        userCancels += taskId
    }

    /** 暂停：服务端标 paused，本地停协程但**不删**已传分块。 */
    suspend fun pause(taskId: String): TransferResult {
        return when (val outcome = api.pause(token, taskId)) {
            is ApiOutcome.Failure -> TransferResult.Failed(outcome.code, outcome.message)

            is ApiOutcome.Success -> {
                pausedTasks += taskId
                updateState(taskId, TransferStates.PAUSED)
                TransferResult.Ok(taskId)
            }
        }
    }

    /**
     * 整批暂停：一次请求把整批标成 paused，本地也一起翻。
     *
     * 返回 null 表示成功，否则是给用户看的提示。
     * 单个任务暂停失败不影响其余的——服务端按幂等处理，重复暂停算成功。
     */
    suspend fun pauseAll(taskIds: Collection<String>): String? {
        val ids = taskIds.distinct()
        if (ids.isEmpty()) {
            return null
        }

        return when (val outcome = api.batchAction(token, ids, "pause")) {
            is ApiOutcome.Failure -> outcome.message

            is ApiOutcome.Success -> {
                ids.forEach { id ->
                    pausedTasks += id
                    updateState(id, TransferStates.PAUSED)
                }
                null
            }
        }
    }

    suspend fun resumeUpload(taskId: String): TransferResult {
        val entity = db.transferDao().get(taskId)
            ?: return TransferResult.Failed(TransferErrorCodes.TRANSFER_NOT_FOUND, "本地记录已不存在")
        val uriString = entity.sourceUri
            ?: return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "缺少源文件授权，无法续传")
        val wholeSha = entity.declaredSha256
            ?: return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "缺少文件校验信息，无法续传")

        // 服务端的位图是唯一事实来源；任务在服务端被回收了就如实失败
        return when (val resumed = api.resume(token, taskId)) {
            is ApiOutcome.Failure -> {
                fail(taskId, resumed.code, resumed.message)
                TransferResult.Failed(resumed.code, resumed.message)
            }

            is ApiOutcome.Success -> {
                mirrorServerChunks(taskId)
                runUpload(taskId, Uri.parse(uriString), entity.totalBytes, wholeSha)
            }
        }
    }

    /** 按服务端位图补差集上传。 */
    private suspend fun runUpload(
        taskId: String,
        sourceUri: Uri,
        totalBytes: Long,
        wholeSha: String,
    ): TransferResult {
        val source = SafSource(resolver, sourceUri)
        val serverChunks = when (val outcome = api.chunks(token, taskId)) {
            is ApiOutcome.Failure -> return networkOrFail(taskId, outcome)
            is ApiOutcome.Success -> outcome.value
        }

        val pending = serverChunks.filter { !it.isCompleted }
        var transferred = serverChunks.filter { it.isCompleted }.sumOf { it.length.toLong() }

        try {
            for (chunk in pending) {
                var attempt = 0
                while (true) {
                    attempt++
                    val bodyResult = openChunkBody(source, chunk.offset, chunk.length.toLong()) { body, chunkSha ->
                        api.putChunk(token, taskId, chunk.index, chunkSha, body)
                    }

                    when (bodyResult) {
                        is ChunkAttempt.NetworkError ->
                            return interrupt(taskId, bodyResult.message)

                        is ChunkAttempt.Fatal -> {
                            // 任务在服务端被回收（保留期已过）：尝试恢复一次再重试本块
                            if (bodyResult.code == TransferErrorCodes.TRANSFER_STATE_INVALID && attempt == 1) {
                                when (api.resume(token, taskId)) {
                                    is ApiOutcome.Success -> continue
                                    is ApiOutcome.Failure -> Unit
                                }
                            }

                            if ((bodyResult.code == TransferErrorCodes.CHUNK_CHECKSUM_MISMATCH ||
                                    bodyResult.code == TransferErrorCodes.CHUNK_IN_FLIGHT) &&
                                attempt < MAX_CHUNK_ATTEMPTS
                            ) {
                                continue // 链路上损坏：原样重传覆盖
                            }

                            fail(taskId, bodyResult.code, bodyResult.message)
                            return TransferResult.Failed(bodyResult.code, bodyResult.message)
                        }

                        is ChunkAttempt.Done -> {
                            transferred += chunk.length
                            db.transferChunkDao().markCompleted(
                                taskId, chunk.index, null, System.currentTimeMillis(),
                            )
                            persistProgress(taskId, transferred, TransferStates.TRANSFERRING)
                            report(taskId, transferred, totalBytes, TransferStates.TRANSFERRING)
                            break
                        }
                    }
                }
            }
        } catch (e: CancellationException) {
            if (pausedTasks.remove(taskId)) {
                // 暂停：服务端已经标成 paused，临时文件与分块位图都要留着
                withContext(NonCancellable) { updateState(taskId, TransferStates.PAUSED) }
            } else if (userCancels.remove(taskId)) {
                withContext(NonCancellable) {
                    runCatching { api.cancel(token, taskId) }
                    cancelLocal(taskId)
                }
            } else {
                // 不是用户点的取消：按中断处理，电脑上的任务与半截文件都留着
                withContext(NonCancellable) { interrupt(taskId, "传输被中断，可续传") }
            }
            throw e
        } catch (e: SecurityException) {
            fail(taskId, TransferErrorCodes.FILE_INACCESSIBLE, "文件授权已失效")
            return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "文件授权已失效")
        } catch (e: IOException) {
            return interrupt(taskId, "网络中断：${e.message ?: "读取失败"}")
        }

        updateState(taskId, TransferStates.VERIFYING)
        return when (val done = api.complete(token, taskId, wholeSha)) {
            is ApiOutcome.Failure -> {
                if (done.code == NetworkErrorCodes.CONNECT_FAILED || done.code == NetworkErrorCodes.TIMEOUT) {
                    interrupt(taskId, done.message)
                } else {
                    fail(taskId, done.code, done.message)
                    TransferResult.Failed(done.code, done.message)
                }
            }

            is ApiOutcome.Success -> {
                updateItem(taskId) {
                    it.copy(
                        state = TransferStates.COMPLETED,
                        transferredBytes = totalBytes,
                        sha256 = done.value.sha256,
                        bytesPerSecond = 0,
                        updatedAt = System.currentTimeMillis(),
                    )
                }
                persistFinal(taskId, TransferStates.COMPLETED, totalBytes)
                TransferResult.Ok(taskId)
            }
        }
    }

    // ------------------------------------------------------------ 下载

    /**
     * 把服务端的下载任务接收到手机。
     * 先写应用私有临时文件（预分配、按偏移写块、逐块回执），整文件校验通过才复制到落点。
     * [destination] 为 null 表示沿用 Room 里已存的落点（自动恢复场景）。
     */
    suspend fun saveDownload(
        item: TransferItem,
        destination: Uri?,
        onTaskCreated: (String) -> Unit = {},
    ): TransferResult {
        onTaskCreated(item.taskId)

        // 恢复时要清掉上一次的暂停标记，否则新的循环在第一块就自我暂停
        pausedTasks.remove(item.taskId)

        var entity = db.transferDao().get(item.taskId)
        val destinationUri = destination?.toString() ?: entity?.destinationUri
        if (destinationUri == null) {
            return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "尚未选择保存位置")
        }

        // 失败重试：服务端位图归零，本地镜像同步归零
        if (item.state == TransferStates.FAILED) {
            when (val restarted = api.restart(token, item.taskId)) {
                is ApiOutcome.Failure -> return TransferResult.Failed(restarted.code, restarted.message)
                is ApiOutcome.Success -> {
                    db.transferChunkDao().resetAll(item.taskId, System.currentTimeMillis())
                }
            }
        } else if (item.state != TransferStates.WAITING) {
            // 中断恢复：以服务端状态为准；任务已在服务端终结就如实上报
            when (val resumed = api.resume(token, item.taskId)) {
                is ApiOutcome.Failure -> return TransferResult.Failed(resumed.code, resumed.message)
                is ApiOutcome.Success -> Unit
            }
        }

        val temp = tempFile(item.taskId)
        val tempUsable = temp.exists() && temp.length() == item.totalBytes
        if (!tempUsable) {
            // 临时文件不可信：本地位图整体重置，全部重新拉取
            resetTemp(temp, item.totalBytes)
            db.transferChunkDao().resetAll(item.taskId, System.currentTimeMillis())
        }

        entity = TransferEntity(
            taskId = item.taskId,
            direction = TransferDirections.DOWNLOAD,
            fileName = item.fileName,
            totalBytes = item.totalBytes,
            transferredBytes = entity?.transferredBytes ?: 0L,
            status = TransferStates.TRANSFERRING,
            updatedAtUtc = System.currentTimeMillis(),
            chunkSize = item.chunkSize,
            chunkCount = item.chunkCount,
            completedChunks = entity?.completedChunks ?: 0,
            sha256 = item.sha256,
            tempPath = temp.absolutePath,
            destinationUri = destinationUri,
            savedToPhone = false,
        )
        upsertEntity(entity)
        mirrorServerChunks(item.taskId)

        // 本地记录完成但服务端未知的块：比对服务端分块哈希，一致就补回执，不一致就重传
        val reconcile = reconcileChunks(item.taskId, temp)
        if (reconcile != null) {
            return reconcile
        }

        val result = pullPendingChunks(item, temp)
        if (result !is TransferResult.Ok) {
            return result
        }

        // 整文件校验（临时文件），通过才复制到用户落点
        updateState(item.taskId, TransferStates.VERIFYING)
        val wholeSha = try {
            hashFile(temp)
        } catch (e: IOException) {
            return interrupt(item.taskId, "读取临时文件失败")
        }

        if (!item.sha256.isNullOrEmpty() && !item.sha256.equals(wholeSha, ignoreCase = true)) {
            temp.delete()
            db.transferChunkDao().resetAll(item.taskId, System.currentTimeMillis())
            fail(item.taskId, com.wirelesscable.app.core.protocol.ErrorCodes.CHECKSUM_MISMATCH, "文件校验失败，内容不一致")
            return TransferResult.Failed(
                com.wirelesscable.app.core.protocol.ErrorCodes.CHECKSUM_MISMATCH,
                "文件校验失败，内容不一致",
            )
        }

        // 复制到落点。覆盖同名文件由系统保存对话框/改名逻辑负责，这里只按用户给定的 URI 写。
        val target = Uri.parse(destinationUri)
        val copied = copyToDestination(item.taskId, temp, target)
        if (copied != null) {
            return copied
        }

        return when (val ack = api.acknowledge(token, item.taskId, wholeSha)) {
            is ApiOutcome.Failure -> {
                deleteDestination(target)
                if (ack.code == NetworkErrorCodes.CONNECT_FAILED || ack.code == NetworkErrorCodes.TIMEOUT) {
                    interrupt(item.taskId, ack.message)
                } else {
                    fail(item.taskId, ack.code, ack.message)
                    TransferResult.Failed(ack.code, ack.message)
                }
            }

            is ApiOutcome.Success -> {
                temp.delete()
                updateItem(item.taskId) {
                    it.copy(
                        state = TransferStates.COMPLETED,
                        transferredBytes = item.totalBytes,
                        sha256 = wholeSha,
                        savedToPhone = true,
                        bytesPerSecond = 0,
                        updatedAt = System.currentTimeMillis(),
                    )
                }
                persistFinal(item.taskId, TransferStates.COMPLETED, item.totalBytes, saved = true)
                TransferResult.Ok(item.taskId)
            }
        }
    }

    /** 本地完成的块与服务端位图对齐。返回非 null 表示流程应中止。 */
    private suspend fun reconcileChunks(taskId: String, temp: File): TransferResult? {
        val serverChunks = when (val outcome = api.chunks(token, taskId)) {
            is ApiOutcome.Failure -> return networkOrFail(taskId, outcome)
            is ApiOutcome.Success -> outcome.value
        }
        val localCompleted = db.transferChunkDao().list(taskId)
            .filter { it.status == ChunkStates.COMPLETED }
            .map { it.chunkIndex }
            .toSet()
        val byIndex = serverChunks.associateBy { it.index }

        for (step in planReconcile(serverChunks, localCompleted)) {
            val server = byIndex.getValue(step.index)
            when (step.action) {
                ChunkAction.MARK_LOCAL_COMPLETED ->
                    // 服务端已回执（上次会话写完的），本地只是没记上：补记，不重传
                    db.transferChunkDao().markCompleted(
                        taskId, step.index, server.sha256, System.currentTimeMillis(),
                    )

                ChunkAction.VERIFY_AND_ACK -> {
                    // 本地写完但回执没到达：验哈希补回执；不一致说明本地写坏了，重拉
                    val localSha = runCatching { hashRange(temp, server.offset, server.length) }.getOrNull()
                    if (localSha != null && server.sha256 != null && localSha.equals(server.sha256, true)) {
                        when (val ack = api.ackChunk(token, taskId, step.index, localSha)) {
                            is ApiOutcome.Success -> Unit
                            is ApiOutcome.Failure -> return networkOrFail(taskId, ack)
                        }
                    } else {
                        db.transferChunkDao().resetIndex(taskId, step.index, System.currentTimeMillis())
                    }
                }

                ChunkAction.FETCH -> Unit // 交给后续的拉取循环
            }
        }

        return null
    }

    /** 拉取所有未完成分块。 */
    private suspend fun pullPendingChunks(item: TransferItem, temp: File): TransferResult {
        val chunks = db.transferChunkDao().list(item.taskId)
        val pending = chunks.filter { it.status != ChunkStates.COMPLETED }
        var transferred = chunks.filter { it.status == ChunkStates.COMPLETED }
            .sumOf { it.length.toLong() }

        try {
            RandomAccessFile(temp, "rw").use { file ->
                for (chunk in pending) {
                    // 暂停要能打断正在跑的循环：取消协程只有"当前这个 job 被 ViewModel
                    // 记住"时才有效，自动恢复/批量接收起的那条没人持有引用，
                    // 结果就是"点了暂停还在传"（1.5.5 修）。
                    if (pausedTasks.remove(item.taskId)) {
                        withContext(NonCancellable) { updateState(item.taskId, TransferStates.PAUSED) }
                        return TransferResult.Cancelled
                    }

                    var attempt = 0
                    while (true) {
                        attempt++
                        val digest = MessageDigest.getInstance("SHA-256")
                        var chunkOk = false
                        var chunkError: TransferResult? = null

                        val outcome = api.downloadChunk(token, item.taskId, chunk.chunkIndex) { stream, meta ->
                            file.seek(chunk.offsetBytes)
                            val buffer = ByteArray(COPY_BUFFER)
                            var received = 0L
                            while (true) {
                                val read = stream.read(buffer)
                                if (read < 0) break
                                file.write(buffer, 0, read)
                                digest.update(buffer, 0, read)
                                received += read
                                report(
                                    item.taskId,
                                    transferred + received,
                                    item.totalBytes,
                                    TransferStates.TRANSFERRING,
                                )
                            }
                            if (received != chunk.length.toLong()) {
                                chunkError = TransferResult.Failed(
                                    NetworkErrorCodes.CONNECT_FAILED,
                                    "分块数据不完整",
                                )
                            } else {
                                chunkOk = true
                            }
                        }

                        when {
                            chunkError != null -> {
                                if (attempt < MAX_CHUNK_ATTEMPTS) continue
                                return interrupt(item.taskId, "网络中断")
                            }

                            outcome is ApiOutcome.Failure -> {
                                return networkOrFail(item.taskId, outcome)
                            }

                            chunkOk -> {
                                val actualSha = digest.digest().toHex()
                                val meta = (outcome as ApiOutcome.Success).value
                                if (!actualSha.equals(meta.chunkSha256, ignoreCase = true)) {
                                    if (attempt < MAX_CHUNK_ATTEMPTS) continue
                                    fail(
                                        item.taskId,
                                        TransferErrorCodes.CHUNK_CHECKSUM_MISMATCH,
                                        "分块校验失败",
                                    )
                                    return TransferResult.Failed(
                                        TransferErrorCodes.CHUNK_CHECKSUM_MISMATCH,
                                        "分块校验失败",
                                    )
                                }

                                when (val ack = api.ackChunk(token, item.taskId, chunk.chunkIndex, actualSha)) {
                                    is ApiOutcome.Success -> {
                                        db.transferChunkDao().markCompleted(
                                            item.taskId, chunk.chunkIndex, actualSha,
                                            System.currentTimeMillis(),
                                        )
                                        transferred += chunk.length
                                        persistProgress(
                                            item.taskId, transferred, TransferStates.TRANSFERRING,
                                        )
                                    }

                                    is ApiOutcome.Failure -> {
                                        if (ack.code == TransferErrorCodes.CHUNK_CHECKSUM_MISMATCH &&
                                            attempt < MAX_CHUNK_ATTEMPTS
                                        ) {
                                            continue
                                        }
                                        return networkOrFail(item.taskId, ack)
                                    }
                                }
                                break
                            }
                        }
                    }
                }
            }
        } catch (e: CancellationException) {
            if (pausedTasks.remove(item.taskId)) {
                withContext(NonCancellable) { updateState(item.taskId, TransferStates.PAUSED) }
            } else if (userCancels.remove(item.taskId)) {
                withContext(NonCancellable) {
                    runCatching { api.cancel(token, item.taskId) }
                    cancelLocal(item.taskId)
                }
            } else {
                // 不是用户点的取消：按中断处理，电脑上的任务与半截文件都留着
                withContext(NonCancellable) { interrupt(item.taskId, "传输被中断，可续传") }
            }
            throw e
        } catch (e: IOException) {
            return interrupt(item.taskId, "网络中断：${e.message ?: "写入失败"}")
        }

        return TransferResult.Ok(item.taskId)
    }

    /** 校验通过的临时文件复制到用户落点。返回非 null 表示流程中止。 */
    private suspend fun copyToDestination(taskId: String, temp: File, target: Uri): TransferResult? {
        val output: OutputStream = try {
            openTruncated(target)
                ?: return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "无法写入所选位置")
        } catch (e: SecurityException) {
            return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "没有写入该位置的权限")
        }

        try {
            output.use { sink ->
                temp.inputStream().use { input ->
                    val buffer = ByteArray(COPY_BUFFER)
                    var copied = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        sink.write(buffer, 0, read)
                        copied += read
                    }
                }
            }
        } catch (e: CancellationException) {
            withContext(NonCancellable) {
                deleteDestination(target)
                runCatching { api.cancel(token, taskId) }
                cancelLocal(taskId)
            }
            throw e
        } catch (e: IOException) {
            deleteDestination(target)
            fail(taskId, TransferErrorCodes.FILE_INACCESSIBLE, "写入所选位置失败")
            return TransferResult.Failed(TransferErrorCodes.FILE_INACCESSIBLE, "写入所选位置失败")
        }

        return null
    }

    /** 主动取消：服务端清临时文件，本地删临时文件与位图。 */
    suspend fun cancel(taskId: String) {
        runCatching { api.cancel(token, taskId) }
        tempFile(taskId).delete()
        db.transferChunkDao().deleteForTask(taskId)
        db.transferDao().delete(taskId)
        cancelLocal(taskId)
    }

    /**
     * 整批取消：服务端一次取消完，本地再逐个清理。
     *
     * 本地清理（删半截文件、删分块位图、删记录）必须逐个做——
     * 那是本机的事，跟服务端一次请求没关系。
     */
    suspend fun cancelAll(taskIds: Collection<String>) {
        val ids = taskIds.distinct()
        if (ids.isEmpty()) {
            return
        }

        runCatching { api.batchAction(token, ids, "cancel") }

        ids.forEach { id ->
            userCancels += id
            tempFile(id).delete()
            db.transferChunkDao().deleteForTask(id)
            db.transferDao().delete(id)
            cancelLocal(id)
        }
    }

    // ------------------------------------------------------------ 恢复辅助

    /** APP 重启后调用：本地未完成任务的列表（由 ViewModel 决定是否自动续传）。 */
    suspend fun unfinishedTasks(): List<TransferEntity> = db.transferDao().listUnfinished()

    /** 服务端位图镜像到本地（本地没有的行补建，状态以服务端为准）。 */
    private suspend fun mirrorServerChunks(taskId: String) {
        val serverChunks = when (val outcome = api.chunks(token, taskId)) {
            is ApiOutcome.Success -> outcome.value
            is ApiOutcome.Failure -> return
        }

        val local = db.transferChunkDao().list(taskId).associateBy { it.chunkIndex }
        val now = System.currentTimeMillis()
        val rows = serverChunks.map { server ->
            val existing = local[server.index]
            TransferChunkEntity(
                taskId = taskId,
                chunkIndex = server.index,
                offsetBytes = server.offset,
                length = server.length,
                sha256 = server.sha256,
                // 服务端已完成的以服务端为准；服务端未完成的保留本地记录（可能只差回执）
                status = if (server.isCompleted) ChunkStates.COMPLETED
                else existing?.status ?: ChunkStates.PENDING,
                updatedAtUtc = now,
            )
        }
        db.transferChunkDao().upsertAll(rows)
    }

    private suspend fun networkOrFail(taskId: String, failure: ApiOutcome.Failure): TransferResult {
        return if (failure.code == NetworkErrorCodes.CONNECT_FAILED ||
            failure.code == NetworkErrorCodes.TIMEOUT
        ) {
            interrupt(taskId, failure.message)
        } else {
            fail(taskId, failure.code, failure.message)
            TransferResult.Failed(failure.code, failure.message)
        }
    }

    /** 网络中断：任务数据保留，标为可恢复的中断。 */
    private suspend fun interrupt(taskId: String, message: String): TransferResult {
        updateItem(taskId) {
            if (it.state == TransferStates.CANCELLED) {
                it
            } else {
                it.copy(
                    state = TransferStates.INTERRUPTED,
                    errorMessage = message,
                    bytesPerSecond = 0,
                    updatedAt = System.currentTimeMillis(),
                )
            }
        }
        persistState(taskId, TransferStates.INTERRUPTED, message)
        return TransferResult.Interrupted(taskId, message)
    }

    // ------------------------------------------------------------ 文件与哈希

    private fun tempFile(taskId: String): File {
        tempDir.mkdirs()
        return File(tempDir, "$taskId.wcpart")
    }

    /** 预分配临时文件到声明大小：按偏移写块的前提，也是"文件是否可信"的判据。 */
    private fun resetTemp(temp: File, totalBytes: Long) {
        temp.delete()
        RandomAccessFile(temp, "rw").use { it.setLength(totalBytes) }
    }

    private fun hashFile(file: File): String =
        file.inputStream().use { hashStream(it) }

    private fun hashStream(input: InputStream): String =
        input.use { stream ->
            val digest = MessageDigest.getInstance("SHA-256")
            val buffer = ByteArray(COPY_BUFFER)
            while (true) {
                val read = stream.read(buffer)
                if (read < 0) break
                digest.update(buffer, 0, read)
            }
            digest.digest().toHex()
        }

    /** 计算临时文件某一段的 SHA-256（恢复时验证本地块是否可信）。 */
    private fun hashRange(file: File, offset: Long, length: Int): String {
        val digest = MessageDigest.getInstance("SHA-256")
        RandomAccessFile(file, "r").use { raf ->
            raf.seek(offset)
            val buffer = ByteArray(COPY_BUFFER)
            var remaining = length
            while (remaining > 0) {
                val read = raf.read(buffer, 0, minOf(buffer.size, remaining))
                if (read < 0) throw IOException("临时文件长度不足")
                digest.update(buffer, 0, read)
                remaining -= read
            }
        }
        return digest.digest().toHex()
    }

    /** 打开源文件的指定分块：新流 + skip 到偏移，避免长连接持有源文件。 */
    private suspend fun openChunkBody(
        source: SafSource,
        offset: Long,
        length: Long,
        shaAndSend: suspend (RequestBody, String) -> ApiOutcome<ChunkAck>,
    ): ChunkAttempt {
        val body = object : RequestBody() {
            override fun contentType() = TransferApi.OCTET_MEDIA
            override fun contentLength() = length

            override fun writeTo(sink: BufferedSink) {
                source.openRead().use { input ->
                    skipFully(input, offset)
                    val buffer = ByteArray(COPY_BUFFER)
                    var remaining = length
                    while (remaining > 0) {
                        val read = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                        if (read < 0) throw IOException("文件在传输过程中被截断")
                        sink.write(buffer, 0, read)
                        remaining -= read
                    }
                }
            }
        }

        // 哈希需要在发送前得出：先本地流式算一遍（只读不落盘），再发送。
        // 8 MiB 一块的读取开销在闪存上是毫秒级，换来的是"分块独立可验证"。
        val chunkSha = try {
            source.openRead().use { input ->
                skipFully(input, offset)
                val localDigest = MessageDigest.getInstance("SHA-256")
                val buffer = ByteArray(COPY_BUFFER)
                var remaining = length
                while (remaining > 0) {
                    val read = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                    if (read < 0) throw IOException("文件在读取过程中被截断")
                    localDigest.update(buffer, 0, read)
                    remaining -= read
                }
                localDigest.digest().toHex()
            }
        } catch (e: IOException) {
            return ChunkAttempt.Fatal(TransferErrorCodes.FILE_INACCESSIBLE, "读取源文件失败")
        } catch (e: SecurityException) {
            return ChunkAttempt.Fatal(TransferErrorCodes.FILE_INACCESSIBLE, "文件授权已失效")
        }

        return when (val outcome = shaAndSend(body, chunkSha)) {
            is ApiOutcome.Success -> ChunkAttempt.Done
            is ApiOutcome.Failure ->
                if (outcome.code == NetworkErrorCodes.CONNECT_FAILED ||
                    outcome.code == NetworkErrorCodes.TIMEOUT
                ) {
                    ChunkAttempt.NetworkError(outcome.message)
                } else {
                    ChunkAttempt.Fatal(outcome.code, outcome.message)
                }
        }
    }

    private sealed interface ChunkAttempt {
        data object Done : ChunkAttempt
        data class NetworkError(val message: String) : ChunkAttempt
        data class Fatal(val code: String, val message: String) : ChunkAttempt
    }

    private fun skipFully(input: InputStream, bytes: Long) {
        var remaining = bytes
        while (remaining > 0) {
            var skipped = input.skip(remaining)
            if (skipped <= 0) {
                if (input.read() < 0) throw IOException("源文件长度不足")
                skipped = 1
            }
            remaining -= skipped
        }
    }

    /**
     * 打开目标文件并保证从 0 开始写。
     * 很多 provider（含 MediaStore 的 Downloads）对 "wt" 并没有真正截断，
     * 这里显式 truncate(0) 一次，再以写模式打开。
     */
    private fun openTruncated(uri: Uri): OutputStream? {
        // 整机根给的是 file://（AllFilesProvider）。ContentResolver 对 file 协议的
        // 写模式在不同版本表现不一致，直接开文件流更可控。
        if (uri.scheme == "file") {
            val path = uri.path ?: return null
            return runCatching { java.io.FileOutputStream(java.io.File(path), false) }.getOrNull()
        }

        runCatching { resolver.openFileDescriptor(uri, "rw")?.use { Os.ftruncate(it.fileDescriptor, 0L) } }
        return resolver.openOutputStream(uri, "wt")
    }

    private fun deleteDestination(uri: Uri): Boolean {
        if (uri.scheme == "file") {
            return runCatching { java.io.File(requireNotNull(uri.path)).delete() }.getOrDefault(false)
        }

        runCatching { DocumentsContract.deleteDocument(resolver, uri) }
            .onSuccess { if (it) return true }

        runCatching { resolver.delete(uri, null, null) }
            .onSuccess { if (it > 0) return true }

        return runCatching {
            resolver.openFileDescriptor(uri, "rw")?.use { Os.ftruncate(it.fileDescriptor, 0L) }
            true
        }.getOrDefault(false)
    }

    // ------------------------------------------------------------ 状态与持久化

    private fun merge(remote: List<TransferTask>) {
        _items.update { current ->
            // 服务端已经删掉的记录必须从本地消失：
            // 以前只往里加不往外删，于是"删了记录还在"（1.5.2 修）。
            // 只保留仍在传输中的本地条目——它们可能比列表接口更早存在。
            val remoteIds = remote.mapTo(HashSet()) { it.taskId }
            val byId = current
                .filter { it.taskId in remoteIds || it.isActive }
                .associateBy { it.taskId }
                .toMutableMap()

            for (task in remote) {
                val existing = byId[task.taskId]
                byId[task.taskId] = if (existing == null) {
                    task.toItem()
                } else {
                    // 本地正在传输/已判失败/已中断时不要被服务端的旧快照覆盖：
                    // 本地状态更贴近真相，服务端追上后再以服务端为准。
                    val keepLocal = existing.isActive && task.state == TransferStates.WAITING ||
                        TransferStates.isTerminal(existing.state) &&
                        (TransferStates.isActive(task.state) || task.state == TransferStates.INTERRUPTED) ||
                        existing.state == TransferStates.INTERRUPTED && task.state == TransferStates.TRANSFERRING

                    existing.copy(
                        fileName = task.fileName,
                        deviceName = task.deviceName,
                        totalBytes = task.totalBytes,
                        transferredBytes = maxOf(existing.transferredBytes, task.transferredBytes),
                        chunkSize = task.chunkSize,
                        chunkCount = task.chunkCount,
                        state = if (keepLocal) existing.state else task.state,
                        sha256 = task.sha256 ?: existing.sha256,
                        errorMessage = if (keepLocal) existing.errorMessage else task.errorMessage,
                        updatedAt = maxOf(existing.updatedAt, task.updatedAtUnixMs),
                        savedToPhone = existing.savedToPhone,
                    )
                }
            }

            byId.values.sortedByDescending { it.updatedAt }
        }
    }

    private fun TransferTask.toItem() = TransferItem(
        taskId = taskId,
        direction = direction,
        fileName = fileName,
        deviceName = deviceName,
        totalBytes = totalBytes,
        transferredBytes = transferredBytes,
        chunkSize = chunkSize,
        chunkCount = chunkCount,
        state = state,
        purpose = purpose,
        sha256 = sha256,
        errorMessage = errorMessage,
        updatedAt = updatedAtUnixMs,
        savedToPhone = state == TransferStates.COMPLETED,
        batchId = batchId,
        batchName = batchName,
        relativePath = relativePath,
    )

    /** 记录已在服务端删除：立刻从本地列表里拿掉，不等下一轮刷新。 */
    fun forget(taskIds: Collection<String>) {
        if (taskIds.isEmpty()) return
        val ids = taskIds.toSet()
        _items.update { current -> current.filterNot { it.taskId in ids } }
    }

    private fun putItem(item: TransferItem) {
        _items.update { current ->
            if (current.any { it.taskId == item.taskId }) {
                current.map { if (it.taskId == item.taskId) item else it }
            } else {
                listOf(item) + current
            }
        }
    }

    private fun updateItem(taskId: String, transform: (TransferItem) -> TransferItem) {
        _items.update { current ->
            current.map { if (it.taskId == taskId) transform(it) else it }
        }
    }

    private fun updateState(taskId: String, state: String) {
        updateItem(taskId) { it.copy(state = state, updatedAt = System.currentTimeMillis()) }
    }

    private suspend fun fail(taskId: String, code: String, message: String) {
        updateItem(taskId) {
            // 用户已经取消的任务，后续在途分块的报错不能把它改成"失败"
            if (it.state == TransferStates.CANCELLED) {
                it
            } else {
                it.copy(
                    state = TransferStates.FAILED,
                    errorMessage = message,
                    bytesPerSecond = 0,
                    updatedAt = System.currentTimeMillis(),
                )
            }
        }
        persistState(taskId, TransferStates.FAILED, message)
    }

    private suspend fun cancelLocal(taskId: String) {
        updateItem(taskId) {
            it.copy(
                state = TransferStates.CANCELLED,
                bytesPerSecond = 0,
                updatedAt = System.currentTimeMillis(),
            )
        }
        persistState(taskId, TransferStates.CANCELLED, null)

        // 取消意味着"不要了"：预分配的 .wcpart 必须删掉。
        // 真机踩过：1.45 GB 的下载取消后，临时文件按原大小留在手机上（预分配不写内容也占空间）。
        runCatching { tempFile(taskId).delete() }
        runCatching { samples.remove(taskId) }
        runCatching { lastReportMs.remove(taskId) }
    }

    private fun report(taskId: String, transferred: Long, total: Long, state: String) {
        val now = SystemClock.elapsedRealtime()
        val last = lastReportMs[taskId] ?: 0L
        if (now - last < REPORT_INTERVAL_MS) {
            return
        }
        lastReportMs[taskId] = now

        val speed = (samples[taskId] ?: SpeedMeter().also { samples[taskId] = it })
            .sample(now, transferred)

        updateItem(taskId) {
            it.copy(
                transferredBytes = transferred,
                totalBytes = total,
                state = state,
                bytesPerSecond = speed,
                updatedAt = System.currentTimeMillis(),
            )
        }
    }

    private suspend fun upsertEntity(entity: TransferEntity) {
        db.transferDao().upsert(entity)
    }

    private suspend fun persistState(taskId: String, state: String, message: String?) {
        db.transferDao().get(taskId)?.let {
            db.transferDao().upsert(
                it.copy(status = state, errorMessage = message, updatedAtUtc = System.currentTimeMillis()),
            )
        }
    }

    private suspend fun persistProgress(taskId: String, transferred: Long, state: String) {
        db.transferDao().get(taskId)?.let {
            db.transferDao().upsert(
                it.copy(
                    transferredBytes = transferred,
                    status = state,
                    completedChunks = db.transferChunkDao().countCompleted(taskId),
                    updatedAtUtc = System.currentTimeMillis(),
                ),
            )
        }
    }

    private suspend fun persistFinal(taskId: String, state: String, totalBytes: Long, saved: Boolean = false) {
        db.transferDao().get(taskId)?.let {
            db.transferDao().upsert(
                it.copy(
                    status = state,
                    transferredBytes = totalBytes,
                    savedToPhone = saved || it.savedToPhone,
                    updatedAtUtc = System.currentTimeMillis(),
                ),
            )
        }
    }

    private fun ByteArray.toHex(): String {
        val builder = StringBuilder(size * 2)
        for (byte in this) {
            builder.append(HEX_CHARS[(byte.toInt() shr 4) and 0x0F])
            builder.append(HEX_CHARS[byte.toInt() and 0x0F])
        }
        return builder.toString()
    }
}
