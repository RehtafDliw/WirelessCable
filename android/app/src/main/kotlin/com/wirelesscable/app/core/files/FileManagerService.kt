package com.wirelesscable.app.core.files

import android.content.Context
import android.util.Log
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.FileApi
import com.wirelesscable.app.core.protocol.DeviceFileIndexDto
import com.wirelesscable.app.core.protocol.FileEntryDto
import com.wirelesscable.app.core.protocol.FileJobDto
import com.wirelesscable.app.core.protocol.FileJobResult
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.WcJson
import com.wirelesscable.app.core.transfer.TransferResult
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString

/** 一轮作业执行的结果。executed 用来决定要不要立刻重报索引。 */
data class JobRunSummary(
    val executed: Int,
    val failures: Int,
    val firstError: String?,
)

/**
 * 手机侧文件管理服务。
 *
 * 承担三件事：
 * 1. **上行索引**：把已授权目录的条目快照推给电脑，电脑据此浏览手机文件；
 * 2. **执行作业**：轮询电脑下发的作业，在本地 SAF 目录上执行并回报结果；
 * 3. **浏览电脑共享目录**：直接调 /fs 接口，文件操作在电脑端执行。
 *
 * 索引是快照语义：每次上行整体替换，电脑上不会残留已被删除的条目。
 */
class FileManagerService(
    private val context: Context,
    private val api: FileApi,
    private val grants: RootGrantManager,
    /**
     * 传输引擎。电脑上传文件到手机目录时用它把文件内容拉到 SAF 目标——
     * 为 null 时该作业会明确报"不支持"，不会静默跳过。
     */
    private val transfers: com.wirelesscable.app.core.transfer.TransferEngine? = null,
) {

    /** 上次上报"根为空"的时间。全部撤销后不必每 3 秒都发一次同步请求。 */
    private var lastEmptySyncAt = 0L

    /** 上次扫描整机根的时间。整机条目多，重扫代价高，单独降频。 */
    private var lastAllRootPublishAt = 0L

    companion object {
        private const val TAG = "FileManagerService"

        /** 空根同步的最小间隔。 */
        private const val EMPTY_SYNC_INTERVAL_MS = 30_000L

        /** 单个根最多上报多少条目，防止超大目录把服务端撑爆。 */
        const val MAX_INDEX_ENTRIES = 5000

        /** 索引遍历的最大深度，防止目录环。 */
        const val MAX_INDEX_DEPTH = 8

        /**
         * 整机根的索引上限。
         *
         * 整机根可能包含几万个文件，按授权目录的 5000 条会截断在最外层；
         * 但也不能无上限——索引是每轮上报的整体快照，条目越多上传越慢。
         * 取 20000 条 / 4 层，覆盖到"相册里的照片"这个深度。
         */
        const val ALL_ROOT_INDEX_ENTRIES = 20_000
        const val ALL_ROOT_INDEX_DEPTH = 4

        /** 整机根的重扫间隔。其余根仍按轮询（3 秒）更新。 */
        private const val ALL_ROOT_INTERVAL_MS = 5 * 60 * 1000L
    }

    /**
     * 上报全部已授权根的索引。
     * 返回失败原因；成功返回 null。
     *
     * @param force 作业刚改过目录时传 true：整机根平时 5 分钟才重扫一次，
     *              不绕过这个节流，电脑端会一直看到被删掉的文件。
     */
    suspend fun publishIndexes(
        token: String,
        deviceId: String = "",
        force: Boolean = false,
    ): String? = withContext(Dispatchers.IO) {
        if (force) {
            lastAllRootPublishAt = 0L
        }

        val roots = grants.activeRoots(deviceId = deviceId.ifEmpty { null })

        if (roots.isEmpty()) {
            // 全部授权被撤销：没有任何索引可以上报，但**必须**告诉服务端根已经空了。
            // 以前这里直接 return，服务端于是永远保留着已经不存在的授权目录——
            // 电脑端「我的手机」和手机端「文件」都会一直显示它们。
            // 轮询是 3 秒一次，这里 30 秒内最多同步一次，避免空转。
            val now = System.currentTimeMillis()
            if (now - lastEmptySyncAt < EMPTY_SYNC_INTERVAL_MS) {
                return@withContext null
            }
            lastEmptySyncAt = now

            return@withContext when (val outcome = api.syncIndexes(token, emptyList())) {
                is ApiOutcome.Success -> {
                    if (outcome.value.removedRoots > 0) {
                        Log.i(TAG, "已清理失效的授权目录 ${outcome.value.removedRoots} 个")
                    }
                    null
                }

                is ApiOutcome.Failure -> outcome.message
            }
        }

        lastEmptySyncAt = 0L
        val activeRootIds = roots.map { it.rootId }

        for (root in roots) {
            val isAllRoot = root.rootId == AllFilesProvider.ROOT_ID

            // 整机根每次重扫都是整盘遍历，跟着 3 秒轮询跑会把手机拖住，
            // 也没必要——它变动的频率远低于用户手选的目录。
            if (isAllRoot && System.currentTimeMillis() - lastAllRootPublishAt < ALL_ROOT_INTERVAL_MS) {
                continue
            }

            val provider = FileProviders.forRoot(context, root)
            val entries = collectEntries(
                provider,
                root,
                maxEntries = if (isAllRoot) ALL_ROOT_INDEX_ENTRIES else MAX_INDEX_ENTRIES,
                maxDepth = if (isAllRoot) ALL_ROOT_INDEX_DEPTH else MAX_INDEX_DEPTH,
            )
            val index = DeviceFileIndexDto(
                rootId = root.rootId,
                displayName = root.displayName,
                providerKind = provider.providerKind,
                capabilities = root.capabilities,
                entries = entries,
                activeRootIds = activeRootIds,
            )

            when (val outcome = api.publishIndex(token, index)) {
                is ApiOutcome.Success -> {
                    if (isAllRoot) {
                        lastAllRootPublishAt = System.currentTimeMillis()
                    }
                    Log.i(TAG, "索引已上报：${root.displayName}（${entries.size} 项）")
                }
                is ApiOutcome.Failure -> {
                    Log.w(TAG, "索引上报失败：${outcome.message}")
                    return@withContext outcome.message
                }
            }
        }

        null
    }

    /**
     * 收集一个根下的全部条目（深度受限、数量受限）。
     * 目录本身也作为一个条目上报，便于电脑端显示层级。
     */
    private suspend fun collectEntries(
        provider: FileProvider,
        root: FileRoot,
        maxEntries: Int = MAX_INDEX_ENTRIES,
        maxDepth: Int = MAX_INDEX_DEPTH,
    ): List<FileEntryDto> {
        val result = mutableListOf<FileEntryDto>()
        val queue = ArrayDeque<Pair<String, Int>>()

        // 根本身也要有一条记录（空路径）：
        // 电脑端判断"根目录能不能建子项"时要查这条。
        result.add(
            FileEntryDto(
                name = root.displayName,
                relativePath = "",
                isDirectory = true,
                sizeBytes = 0,
                modifiedUnixMs = 0,
                capabilities = root.capabilities,
            ),
        )

        // 路径语义与 Windows 端一致：相对根。空串表示根本身。
        queue.add("" to 0)

        while (queue.isNotEmpty() && result.size < maxEntries) {
            val (path, depth) = queue.removeFirst()

            when (val listing = provider.list(path)) {
                is FileResult.Failure -> {
                    Log.w(TAG, "列举失败 $path：${listing.message}")
                    continue
                }

                is FileResult.Success -> {
                    for (entry in listing.value.orEmpty()) {
                        if (result.size >= maxEntries) break

                        result.add(
                            FileEntryDto(
                                name = entry.name,
                                relativePath = entry.relativePath,
                                isDirectory = entry.isDirectory,
                                sizeBytes = entry.sizeBytes,
                                modifiedUnixMs = entry.modifiedUnixMs,
                                capabilities = entry.capabilities,
                            ),
                        )

                        if (entry.isDirectory && depth < maxDepth) {
                            queue.add(entry.relativePath to depth + 1)
                        }
                    }
                }
            }
        }

        return result
    }

    /**
     * 取待执行作业并逐个执行、回报结果。
     *
     * 执行过的作业数要一起带回去：作业改的是手机目录，执行完得马上重报一次索引，
     * 否则电脑端要等到下一轮（3 秒）才看得见新建/改名/删除的结果。
     */
    suspend fun runJobs(token: String): JobRunSummary = withContext(Dispatchers.IO) {
        val jobs = when (val outcome = api.takeJobs(token)) {
            is ApiOutcome.Success -> outcome.value
            is ApiOutcome.Failure -> {
                Log.w(TAG, "取作业失败：${outcome.message}")
                return@withContext JobRunSummary(0, 0, null)
            }
        }

        if (jobs.isEmpty()) {
            return@withContext JobRunSummary(0, 0, null)
        }

        // 只有取到作业时才记，平时不刷屏；排查"电脑说下发了手机没反应"时这行是关键
        Log.i(TAG, "取到 ${jobs.size} 个作业：" + jobs.joinToString { "${it.operation}:${it.sourcePath}" })

        var failures = 0
        var firstError: String? = null

        for (job in jobs) {
            // list 要带数据回来，走单独一条路：结果里塞目录条目的 JSON
            var payload: String? = null

            // 单个作业抛异常不能掀掉整轮：以前一个 SAF 异常会让当前作业不回报、
            // 后面的作业也不执行，电脑端只能对着"等待手机执行"干等到超时。
            val result = try {
                if (job.operation == FileProtocol.OP_LIST) {
                    val listed = listDirectoryForJob(job)
                    payload = listed.payload
                    listed.result
                } else {
                    executeJob(job)
                }
            } catch (error: Throwable) {
                Log.w(TAG, "作业执行失败 job=${job.jobId} op=${job.operation}", error)
                FileResult.ioFailure(error.message ?: "手机上执行这个操作时出错了。")
            }

            if (result is FileResult.Failure) {
                failures++
                firstError = firstError ?: result.message
            }

            val failed = result as? FileResult.Failure
            val report = FileJobResult(
                jobId = job.jobId,
                status = if (failed == null) FileProtocol.JOB_COMPLETED else FileProtocol.JOB_FAILED,
                errorCode = failed?.reasonCode,
                errorMessage = failed?.message,
                payload = payload,
            )

            when (val outcome = api.completeJob(token, job.jobId, report)) {
                is ApiOutcome.Success -> Unit
                is ApiOutcome.Failure -> Log.w(TAG, "回报作业结果失败：${outcome.message}")
            }
        }

        JobRunSummary(jobs.size, failures, firstError)
    }

    /**
     * 列目录作业：把该目录的条目序列化进 payload 回传。
     *
     * 电脑端读的是索引快照，索引有深度与条数上限（整机 4 层 / 2 万条）。
     * 用户点进一个没被索引到的目录时，桌面端现场下发这条作业问一次——
     * 这是"退役反向服务"之后深目录仍然能进的前提。
     */
    private suspend fun listDirectoryForJob(job: FileJobDto): ListJobOutcome {
        val root = grants.activeRoots().firstOrNull { it.rootId == job.rootId }
            ?: return ListJobOutcome(
                FileResult.failure(
                    FileStatus.AUTHORIZATION_REVOKED,
                    "authorization_revoked",
                    "该目录的访问授权已被撤销。",
                ),
            )

        val provider = FileProviders.forRoot(context, root)
        val path = job.sourcePath ?: ""

        return when (val listed = provider.list(path)) {
            is FileResult.Failure -> ListJobOutcome(listed)
            is FileResult.Success -> {
                val entries = listed.value.map { entry ->
                    FileEntryDto(
                        name = entry.name,
                        relativePath = entry.relativePath,
                        isDirectory = entry.isDirectory,
                        sizeBytes = entry.sizeBytes,
                        modifiedUnixMs = entry.modifiedUnixMs,
                        capabilities = entry.capabilities,
                    )
                }

                ListJobOutcome(FileResult.success(Unit), WcJson.instance.encodeToString(entries))
            }
        }
    }

    private data class ListJobOutcome(
        val result: FileResult<Unit>,
        val payload: String? = null,
    )

    /** 执行单个作业。 */
    private suspend fun executeJob(job: FileJobDto): FileResult<Unit> {        val roots = grants.activeRoots()
        val root = roots.firstOrNull { it.rootId == job.rootId }
            ?: return FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )

        val provider = FileProviders.forRoot(context, root)

        return when (job.operation) {
            FileProtocol.OP_CREATE -> {
                // sourcePath 是"在哪个目录里建"（父目录本身），与 C# 端一致
                val parent = job.sourcePath ?: ""
                val name = job.displayName ?: ""
                if (name.isEmpty()) {
                    FileResult.pathInvalid("缺少新目录名。")
                } else {
                    provider.createDirectory(if (parent.isEmpty()) name else "$parent/$name")
                }
            }

            FileProtocol.OP_RENAME -> provider.rename(
                job.sourcePath ?: "",
                job.displayName ?: "",
            )

            FileProtocol.OP_DELETE -> provider.delete(job.sourcePath ?: "", recursive = true)

            FileProtocol.OP_MOVE -> provider.move(
                job.sourcePath ?: "",
                job.destinationPath ?: "",
            )

            FileProtocol.OP_COPY -> copyRecursive(
                provider,
                job.sourcePath ?: "",
                job.destinationPath ?: "",
            )

            FileProtocol.OP_UPLOAD -> {
                val engine = transfers
                if (engine == null) {
                    FileResult.unsupported("当前无法接收电脑发来的文件。")
                } else {
                    val taskId = job.transferTaskId
                    if (taskId.isNullOrEmpty()) {
                        FileResult.pathInvalid("缺少传输任务。")
                    } else {
                        // 先在 SAF 里建出目标文件，再把内容（从电脑拉的）写进去
                        val name = job.displayName ?: ""
                        val parent = resolveUploadParent(job.sourcePath, provider)
                        val created = provider.createFile(parent, name)
                        if (created is FileResult.Failure) {
                            created
                        } else {
                            val uri = (created as FileResult.Success).value
                            val error = engine.pullTo(taskId, uri)
                            if (error == null) {
                                FileResult.success(Unit)
                            } else {
                                // 拉取失败就把刚建出来的空文件删掉：留着会让用户下次重发时
                                // 撞上"目标已存在"，而且看起来像"传过来了但是没有内容"
                                val target = if (parent.isEmpty()) name else "$parent/$name"
                                runCatching { provider.delete(target, recursive = false) }
                                FileResult.ioFailure(error)
                            }
                        }
                    }
                }
            }

            /*
             * 下载：电脑要把手机上的这个文件存到它自己那里去。
             *
             * 方向上是"手机发给电脑"，走的是既有的上传通道（/transfer/uploads），
             * 所以不新建一套反向拉取：源文件在手机、落点在电脑的接收目录，
             * 恰好就是手机自己点"发送"时的同一条路。
             */
            FileProtocol.OP_DOWNLOAD -> {
                val engine = transfers
                if (engine == null) {
                    FileResult.unsupported("当前无法向电脑发送文件。")
                } else {
                    when (val located = provider.uriFor(job.sourcePath ?: "")) {
                        is FileResult.Failure -> located
                        is FileResult.Success -> {
                            // 电脑指定了落点目录就落到那里；没指定就是默认接收目录（按设备名分文件夹）。
                            //
                            // 文件夹下载：电脑把「接收目录下的相对子目录」放在 destinationPath 里
                            // （例如「相册/2024」），这里原样传给上传，由服务端建出同样的层级。
                            // 老电脑端不传这个字段，仍然是平铺——向后兼容（1.5.59）。
                            val outcome = engine.upload(
                                located.value,
                                directoryId = job.destinationDirectoryId,
                                relativeDirectory = job.destinationPath,
                            )
                            when (outcome) {
                                is TransferResult.Ok -> FileResult.success(Unit)
                                is TransferResult.Failed ->
                                    FileResult.failure(FileStatus.IO_FAILURE, outcome.code, outcome.message)
                                is TransferResult.Interrupted ->
                                    FileResult.ioFailure("传输中断，稍后可在手机上继续。")
                                TransferResult.Cancelled -> FileResult.ioFailure("传输已取消。")
                            }
                        }
                    }
                }
            }

            else -> FileResult.unsupported("暂不支持的操作：${job.operation}")
        }
    }

    /**
     * 复制文件或目录。
     *
     * SAF 没有跨提供方通用的"复制文档"接口（DocumentsContract.copyDocument 只在同提供方、
     * 且多数第三方提供器没实现），所以用既有原语自己走：目录递归建 + 文件流拷贝。
     * 目标必须是新路径；中途失败会把已经建出来的一半删掉，不留垃圾。
     */
    private suspend fun copyRecursive(
        provider: FileProvider,
        sourcePath: String,
        destinationPath: String,
    ): FileResult<Unit> {
        val source = SafFileProvider.normalize(sourcePath)
        val destination = SafFileProvider.normalize(destinationPath)

        if (source.isEmpty() || destination.isEmpty()) {
            return FileResult.pathInvalid("缺少源或目标路径。")
        }

        if (destination == source || destination.startsWith("$source/")) {
            return FileResult.pathInvalid("不能复制到自身或子目录。")
        }

        if (provider.exists(destination)) {
            return FileResult.alreadyExists()
        }

        val info = provider.info(source)
        if (info is FileResult.Failure) {
            return info
        }

        val entry = (info as FileResult.Success).value ?: return FileResult.notFound()

        return if (entry.isDirectory) {
            val created = provider.createDirectory(destination)
            if (created is FileResult.Failure) {
                return created
            }

            val listed = provider.list(source)
            if (listed is FileResult.Failure) {
                provider.delete(destination, recursive = true)
                return listed
            }

            for (child in (listed as FileResult.Success).value) {
                val childSource = if (child.relativePath.isNotEmpty()) {
                    child.relativePath
                } else {
                    "$source/${child.name}"
                }

                val copied = copyRecursive(provider, childSource, "$destination/${child.name}")
                if (copied is FileResult.Failure) {
                    // 尽力清掉半成品，避免用户看到"复制了一半"的目录
                    runCatching { provider.delete(destination, recursive = true) }
                    return copied
                }
            }

            FileResult.success(Unit)
        } else {
            val parent = SafFileProvider.parentOf(destination)
            val name = SafFileProvider.nameOf(destination)

            val created = provider.createFile(parent, name)
            if (created is FileResult.Failure) {
                return created
            }

            val input = provider.openRead(source)
            if (input is FileResult.Failure) {
                runCatching { provider.delete(destination, recursive = false) }
                return input
            }

            val output = provider.openWrite(destination, append = false)
            if (output is FileResult.Failure) {
                runCatching { provider.delete(destination, recursive = false) }
                return output
            }

            try {
                (input as FileResult.Success).value.use { from ->
                    (output as FileResult.Success).value.use { to ->
                        from.copyTo(to, 64 * 1024)
                    }
                }
                FileResult.success(Unit)
            } catch (ex: Exception) {
                Log.w("FileManagerService", "复制文件失败 path={$source}", ex)
                runCatching { provider.delete(destination, recursive = false) }
                FileResult.ioFailure(ex.message ?: "复制文件失败。")
            }
        }
    }

    /**
     * 电脑上传文件到手机时的落点。
     *
     * 电脑从根（空路径）发来的文件统一落进收件目录——「传输目录」页只显示这一处，
     * 否则"收到的文件"会散落在用户当时浏览到的任意目录里，那一页永远说不清。
     * 电脑明确指到某个子目录时尊重它（那是用户在电脑端主动选的落点）。
     */
    private suspend fun resolveUploadParent(requested: String?, provider: FileProvider): String {
        val parent = requested ?: ""
        if (parent.isNotEmpty()) {
            return parent
        }

        if (provider.providerKind != AllFilesProvider.PROVIDER_KIND) {
            return parent
        }

        // 收件目录可能还不存在；已存在时 createDirectory 会返回 ALREADY_EXISTS，忽略即可
        provider.createDirectory(InboxStore.RELATIVE_PATH)
        return InboxStore.RELATIVE_PATH
    }

}

