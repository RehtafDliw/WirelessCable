package com.wirelesscable.app.vm

import android.content.Context
import android.content.Intent
import android.provider.Settings
import android.util.Log
import android.net.Uri
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import androidx.lifecycle.viewModelScope
import com.wirelesscable.app.core.files.AllFilesAccess
import com.wirelesscable.app.core.files.AllFilesProvider
import com.wirelesscable.app.core.files.FileCapabilities
import com.wirelesscable.app.core.files.RootGrantManager
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.FileApi
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.protocol.FileEntryDto
import com.wirelesscable.app.core.protocol.FileJobDto
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.FolderDownloadResult
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.core.protocol.TransferDirections
import com.wirelesscable.app.core.protocol.FileRootDto
import com.wirelesscable.app.core.transfer.TransferItem
import com.wirelesscable.app.data.repository.ConnectionRepository
import com.wirelesscable.app.data.security.TokenVault
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** 目标目录选择时要执行的动作。 */
enum class TargetOperation { MOVE, COPY }

/** 手机本地真实持有的一处授权（与"服务器是否已收到"无关）。 */
data class LocalRootInfo(
    val rootId: String,
    val displayName: String,
    val isMachine: Boolean,
    /** 这份授权给哪台电脑；空串 = 升级前的老记录，对所有电脑可见。 */
    val deviceId: String = "",
)

/** 已挂起的一次移动/复制：动作 + 源条目。 */
data class PendingTarget(val operation: TargetOperation, val sources: List<FileEntryDto>)

/** 文件浏览页的状态。 */
data class FileBrowserUiState(
    val ready: Boolean = false,
    val roots: List<FileRootDto> = emptyList(),
    val currentRootId: String = "",
    /** 当前目录的相对路径；空串表示根。 */
    val currentPath: String = "",
    val entries: List<FileEntryDto> = emptyList(),
    val selected: Set<String> = emptySet(),
    val searchKeyword: String = "",
    val busy: Boolean = false,
    val message: String? = null,
    /** 权限不足或提供方不支持时给用户的明确提示。 */
    val warning: String? = null,
    /**
     * 当前根是否只读。
     *
     * 整机根（"此电脑"）是只读的：能看到电脑所有文件，但不能改任何文件。
     * 界面据此隐藏新建/改名/删除等写操作入口，而不是让用户点了才报错。
     */
    val readOnly: Boolean = false,
    /**
     * 是否已拿到"所有文件访问权"。
     *
     * 界面据此决定显示"开启整机访问"入口还是让整机根直接出现在列表里。
     * 权限没有回调，所以跟着轮询（3 秒）刷新，用户从系统设置回来很快就能看到。
     */
    val allFilesGranted: Boolean = false,
    /**
     * 手机本地已授权的根。
     *
     * 与 [roots]（服务器返回）分开：授权后要经过"上报索引"这一步服务器才知道，
     * 中间可能有延迟甚至失败。设置页必须显示本地真实状态，否则新设备会一直显示
     * "还没有授权任何目录给电脑"，用户以为授权没生效（Android 11 设备上踩过）。
     */
    val localRoots: List<LocalRootInfo> = emptyList(),
    /** 排序方式。 */
    val sortBy: FileSortBy = FileSortBy.NAME,
    /**
     * 文件属性详情。
     *
     * 单独一个字段而不是塞进 [message]：message 是错误通道（红色），
     * 而且会被轮询刷新覆盖——属性信息必须自己占一个位置。
     */
    val infoText: String? = null,
    /**
     * 正在选目标目录的移动/复制。
     *
     * 有了它，界面才知道底部该显示「移动到此处」还是「复制到此处」；
     * 源条目在挂起时一次性取好，之后翻目录不会把选中项丢掉。
     */
    val pending: PendingTarget? = null,
) {
    /**
     * 过滤并排序后的条目。
     *
     * 目录永远排在文件前面（与资源管理器一致），
     * 同类之间按当前排序方式排。
     */
    val visibleEntries: List<FileEntryDto>
        get() {
            val filtered = if (searchKeyword.isBlank()) {
                entries
            } else {
                entries.filter { it.name.contains(searchKeyword, ignoreCase = true) }
            }

            val comparator = when (sortBy) {
                FileSortBy.NAME -> compareBy<FileEntryDto> { it.name.lowercase() }
                FileSortBy.SIZE -> compareBy<FileEntryDto> { it.sizeBytes }
                FileSortBy.MODIFIED -> compareBy<FileEntryDto> { it.modifiedUnixMs }
                FileSortBy.TYPE -> compareBy<FileEntryDto> { it.name.substringAfterLast('.', "") }
            }

            return filtered.sortedWith(
                // 目录优先，其余按排序方式
                compareByDescending<FileEntryDto> { it.isDirectory }.then(comparator),
            )
        }

    /**
     * 当前根能不能删除。
     *
     * 必须单独看 Delete 能力位：`readOnly` 只反映"有没有写权限"，
     * 而"可读写（不能删）"这种档位是能写不能删的——只判 readOnly 会让删除按钮亮着，
     * 点下去才被服务端拒绝（用户看到的就是"未授权"）。
     */
    val canDeleteHere: Boolean
        get() {
            val root = roots.firstOrNull { it.rootId == currentRootId } ?: return false
            if (root.capabilities == 0) return true
            return (root.capabilities and FileCapabilities.DELETE) != 0
        }

    /**
     * 当前目录能不能上传文件。
     *
     * 只有电脑的共享目录可以（整机根是只读的，手机授权目录是手机自己的）。
     * 还要该设备在这个共享目录上有 Upload 权限。
     */
    val canUploadHere: Boolean
        get() {
            val root = roots.firstOrNull { it.rootId == currentRootId } ?: return false
            if (root.kind != FileProtocol.KIND_SHARED) return false
            return (root.permissions and PERMISSION_UPLOAD) != 0
        }

    companion object {
        /** Upload 权限位（与 C# 端 SharedDirectoryPermissions.Upload 一致）。 */
        const val PERMISSION_UPLOAD = 1 shl 2
    }
}

/**
 * 文件浏览页的编排者。
 *
 * 手机侧的两种目录浏览都走这里：
 * - **电脑共享目录**：直接调服务端 /fs 接口，文件操作在电脑端执行；
 * - **手机授权目录**：本地 SAF 直接读，不需要网络。
 *
 * 同时负责把手机授权目录的索引推给电脑，并执行电脑下发的作业。
 */
class FileBrowserViewModel(
    private val context: Context,
    private val repository: ConnectionRepository,
    private val vault: TokenVault,
) : ViewModel() {

    private val _state = MutableStateFlow(FileBrowserUiState())
    val state: StateFlow<FileBrowserUiState> = _state.asStateFlow()

    private val grants = RootGrantManager(context, context.contentResolver)

    private var api: FileApi? = null
    private var apiKey: String? = null
    private var pollJob: Job? = null

    /**
     * 进入页面时调用：只负责刷新"电脑共享目录"列表。
     *
     * 索引上报与作业执行不在这里——那是应用级 [SessionSyncEngine] 的事，
     * 息屏后由前台服务继续持有；挂在页面上会让"退出界面"等于"不再执行"。
     */
    fun start() {
        if (pollJob?.isActive == true) return

        pollJob = viewModelScope.launch {
            while (isActive) {
                val current = ensureApi()
                if (current == null) {
                    _state.update { it.copy(ready = false) }
                    delay(SESSION_RETRY_MS)
                    continue
                }

                refreshRoots(current)
                refreshLocalRoots()
                delay(POLL_MS)
            }
        }
    }

    fun stop() {
        pollJob?.cancel()
        pollJob = null
    }

    fun clearMessage() = _state.update { it.copy(message = null) }

    fun clearWarning() = _state.update { it.copy(warning = null) }

    /** 用户在系统选择器里选了一个目录。 */
    /** 待授权给哪台电脑（选目录前由界面设置）。 */
    private var grantDeviceId: String = ""

    /** 界面点「选择目录」前调用：把这批授权绑到指定电脑。 */
    fun beginFolderGrant(deviceId: String) {
        grantDeviceId = deviceId
    }

    /**
     * 某台电脑当前能看到哪些手机目录。
     *
     * 同步读 prefs，供授权对话框现算——异步查列表在弹窗里不好用。
     * 也只列 SAF 目录：整机那一条由「允许/取消整机访问」单独管。
     */
    fun grantedFoldersFor(deviceId: String): List<LocalRootInfo> =
        runCatching {
            grants.savedRoots()
                .filter { it.deviceId.isEmpty() || it.deviceId == deviceId }
                .map { LocalRootInfo(it.rootId, it.displayName, isMachine = false, deviceId = it.deviceId) }
        }.getOrDefault(emptyList())

    /** 这台电脑能不能看整机（系统权限之外，我们自己的按设备开关）。 */
    fun isMachineAllowed(deviceId: String): Boolean = grants.machineAllowed(deviceId)

    /** 开关某台电脑的"整机访问"。系统权限没给时调用方负责先引导去开。 */
    fun setMachineAllowed(deviceId: String, allowed: Boolean) {
        grants.setMachineAllowed(deviceId, allowed)
        viewModelScope.launch {
            refreshLocalRoots()
            SessionSyncEngine.requestIndexPublish()
        }
    }

    fun onDirectoryPicked(uri: Uri) {
        val root = grants.onDirectoryPicked(uri, grantDeviceId)
        if (root == null) {
            _state.update {
                it.copy(
                    message = "无法取得该目录的访问授权。" +
                        "Android 11 起系统不允许把「下载」或存储根目录授权给应用，请改选它下面的子目录。",
                )
            }
            return
        }

        viewModelScope.launch {
            refreshLocalRoots()
            // 索引上报由引擎做，这里只请求它尽快跑一次
            SessionSyncEngine.requestIndexPublish()
            val current = ensureApi()
            if (current != null) {
                refreshRoots(current)
            }
        }
    }

    /** 目录选择器没有返回结果（用户取消，或系统拒绝了受限制的目录）。 */
    fun onDirectoryPickCancelled() {
        _state.update {
            it.copy(
                message = "没有选中目录。Android 11 起「下载」与存储根目录不能授权给应用，" +
                    "请改选它们下面的子目录（例如 Download/某文件夹）。",
            )
        }
    }

    /** 撤销一个手机授权目录。 */
    fun revokeRoot(rootId: String) {
        // 整机根不是 SAF 授权，撤销只能去系统设置里关掉"所有文件访问权"
        if (rootId == AllFilesProvider.ROOT_ID) {
            openAllFilesSettings()
            return
        }

        grants.revoke(rootId)
        viewModelScope.launch {
            refreshLocalRoots()
            SessionSyncEngine.requestIndexPublish()
            val current = ensureApi()
            if (current != null) {
                refreshRoots(current)
            }
        }
    }

    /**
     * 打开"所有文件访问权"的系统设置页。
     *
     * 这是特殊权限：应用自己授不了，也不能用运行时权限弹窗。
     * 少数 ROM 没有按包名的入口，退回全局列表页。
     */
    fun openAllFilesSettings() {
        val direct = Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION)
            .setData(Uri.parse("package:${context.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

        try {
            context.startActivity(direct)
            return
        } catch (ex: Exception) {
            Log.w("FileBrowserViewModel", "打不开按包名的所有文件访问页，改用全局列表", ex)
        }

        try {
            context.startActivity(
                Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
        } catch (ex: Exception) {
            Log.w("FileBrowserViewModel", "无法打开所有文件访问设置", ex)
        }
    }

    /** 切换到某个根并列出其内容。 */
    fun openRoot(rootId: String) {
        _state.update {
            it.copy(
                currentRootId = rootId,
                currentPath = "",
                selected = emptySet(),
                pending = null,
                readOnly = isReadOnly(it.roots, rootId),
                // 上一根的"未授权"警告不能跟到新根上
                warning = null,
                message = null,
            )
        }
        reload()
    }

    /**
     * 某个根是否只读。
     *
     * 只看服务端下发的**能力位**，不看根类型：1.1.0 起"此电脑"默认可读写，
     * 用户把某台设备降为只读时服务端会去掉写位，界面跟着灰掉即可。
     * 能力位为 0 表示老服务端没上报，按可写处理，由服务端在真正执行时拒绝。
     */
    private fun isReadOnly(roots: List<FileRootDto>, rootId: String): Boolean {
        val root = roots.firstOrNull { it.rootId == rootId } ?: return false
        if (root.capabilities == 0) return false

        return (root.capabilities and FileCapabilities.WRITE_MASK) == 0
    }

    /** 进入子目录。 */
    fun enter(entry: FileEntryDto) {
        if (!entry.isDirectory) return
        _state.update {
            it.copy(
                currentPath = entry.relativePath,
                selected = emptySet(),
                searchKeyword = "",
            )
        }
        reload()
    }

    /**
     * 返回上一级。
     *
     * 整机根的路径形如 `C:` / `C:/Users` / `C:/Users/foo`：
     * 上一级在盘符处就要停——`C:` 的上一级是"此电脑"的盘符列表，不是别的盘符。
     * 共享目录的路径则是普通相对路径，截断最后一个 `/` 即可。
     */
    fun goUp() {
        val state = _state.value
        val path = state.currentPath
        if (path.isEmpty()) return

        val parent = if (state.readOnly) {
            val slash = path.indexOf('/')
            if (slash <= 0) "" else path.substring(0, slash)
        } else {
            path.substringBeforeLast('/', "")
        }

        _state.update { it.copy(currentPath = parent, selected = emptySet(), searchKeyword = "") }
        reload()
    }

    fun setSearch(keyword: String) = _state.update { it.copy(searchKeyword = keyword) }

    /**
     * 当前选中的条目（按当前列表顺序）。
     *
     * 批量下载要用：把选中项换算成 (根 id, 相对路径, 文件名) 交给传输层。
     */
    fun selectedEntries(): List<FileEntryDto> {
        val current = _state.value
        return current.entries.filter { it.relativePath in current.selected }
    }

    /** 切换排序方式。 */
    fun setSortBy(sortBy: FileSortBy) = _state.update { it.copy(sortBy = sortBy) }

    /**
     * 查询条目详情（属性）。
     *
     * 服务端按 rootId + path 定位——客户端不能直接指定绝对路径。
     * 查到的信息放进 [FileBrowserUiState.message] 由界面展示。
     */
    fun showInfo(entry: FileEntryDto) {
        val rootId = _state.value.currentRootId
        if (rootId.isEmpty()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            val token = tokenOf() ?: return@launch

            when (val outcome = api.info(token, rootId, entry.relativePath)) {
                is ApiOutcome.Success -> {
                    val info = outcome.value
                    _state.update {
                        it.copy(infoText = buildString {
                            append(info.name)
                            append("\n")
                            if (info.isDirectory) {
                                append("类型：文件夹")
                            } else {
                                append("类型：文件")
                                append("　大小：${formatSize(info.sizeBytes)}")
                            }
                            append("\n")
                            append("位置：${info.relativePath}")
                            append("\n")
                            if (info.modifiedUnixMs > 0) {
                                append("修改时间：${formatTime(info.modifiedUnixMs)}")
                                append("\n")
                            }
                            append("可用操作：${describeCapabilities(info.capabilities)}")
                        })
                    }
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(message = outcome.message)
                }
            }
        }
    }

    /** 关闭属性详情。 */
    fun dismissInfo() = _state.update { it.copy(infoText = null) }

    /** 字节数 → 人类可读大小。 */
    private fun formatSize(bytes: Long): String {
        if (bytes < 1024) return "$bytes B"
        val units = arrayOf("KB", "MB", "GB", "TB")
        var value = bytes.toDouble() / 1024
        var index = 0
        while (value >= 1024 && index < units.size - 1) {
            value /= 1024
            index++
        }
        return String.format(java.util.Locale.US, "%.1f %s", value, units[index])
    }

    /** Unix 毫秒 → 本地时间文本。 */
    private fun formatTime(millis: Long): String {
        val format = java.text.SimpleDateFormat("yyyy-MM-dd HH:mm", java.util.Locale.getDefault())
        return format.format(java.util.Date(millis))
    }

    /** 能力位 → 面向用户的能力描述。 */
    private fun describeCapabilities(capabilities: Int): String {
        if (capabilities == 0) return "无"

        val parts = mutableListOf<String>()
        if (capabilities and 1 != 0) parts += "浏览"
        if (capabilities and 2 != 0) parts += "读取"
        if (capabilities and 4 != 0) parts += "写入"
        if (capabilities and 8 != 0) parts += "新建目录"
        if (capabilities and 16 != 0) parts += "重命名"
        if (capabilities and 32 != 0) parts += "删除"
        if (capabilities and 64 != 0) parts += "移动"
        if (capabilities and 128 != 0) parts += "查看信息"
        return parts.joinToString("、")
    }

    /** 多选：点一下切换选中状态。 */
    fun toggleSelection(entry: FileEntryDto) {
        _state.update { current ->
            val next = current.selected.toMutableSet()
            if (!next.add(entry.relativePath)) {
                next.remove(entry.relativePath)
            }
            current.copy(selected = next)
        }
    }

    fun clearSelection() = _state.update { it.copy(selected = emptySet()) }

    /** 批量删除选中的条目。删除必须确认——调用方负责弹确认框。 */
    fun deleteSelected(recursive: Boolean) {
        val current = _state.value
        val rootId = current.currentRootId
        val paths = current.selected.toList()
        if (rootId.isEmpty() || paths.isEmpty()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true) }

            when (val outcome = api.batchDelete(tokenOf(), rootId, paths, recursive)) {
                is ApiOutcome.Success -> {
                    val failed = outcome.value.count { !it.success }
                    _state.update {
                        it.copy(
                            busy = false,
                            selected = emptySet(),
                            message = if (failed == 0) {
                                "已删除 ${paths.size} 项"
                            } else {
                                "已删除 ${paths.size - failed} 项，$failed 项失败：${
                                    outcome.value.first { !it.success }.errorMessage ?: "未知原因"
                                }"
                            },
                        )
                    }
                    reload()
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(busy = false, message = outcome.message)
                }
            }
        }
    }

    /** 新建目录。 */
    fun createDirectory(name: String) {
        val rootId = _state.value.currentRootId
        val parent = _state.value.currentPath
        if (rootId.isEmpty() || name.isBlank()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true) }

            when (val outcome = api.createDirectory(tokenOf(), rootId, parent, name)) {
                is ApiOutcome.Success -> {
                    _state.update { it.copy(busy = false, message = "已创建 $name") }
                    reload()
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(busy = false, message = outcome.message)
                }
            }
        }
    }

    /** 重命名。 */
    fun rename(path: String, newName: String) {
        val rootId = _state.value.currentRootId
        if (rootId.isEmpty() || newName.isBlank()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true) }

            when (val outcome = api.rename(tokenOf(), rootId, path, newName)) {
                is ApiOutcome.Success -> {
                    _state.update { it.copy(busy = false, message = "已重命名为 $newName") }
                    reload()
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(busy = false, message = outcome.message)
                }
            }
        }
    }

    /** 移动。 */
    fun move(sourcePath: String, destinationPath: String) {
        val rootId = _state.value.currentRootId
        if (rootId.isEmpty()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true) }

            when (val outcome = api.move(tokenOf(), rootId, sourcePath, destinationPath)) {
                is ApiOutcome.Success -> {
                    _state.update { it.copy(busy = false, message = "已移动") }
                    reload()
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(busy = false, message = outcome.message)
                }
            }
        }
    }

    /** 复制（单个条目，目标必须是新名字）。 */
    fun copy(sourcePath: String, destinationPath: String) {
        val rootId = _state.value.currentRootId
        if (rootId.isEmpty()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true) }

            when (val outcome = api.copy(tokenOf(), rootId, sourcePath, destinationPath)) {
                is ApiOutcome.Success -> {
                    _state.update { it.copy(busy = false, message = "已复制") }
                    reload()
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(busy = false, message = outcome.message)
                }
            }
        }
    }

    /** 把选中项挂起为"移动"，进入目标目录选择。 */
    fun beginMove() = beginPending(TargetOperation.MOVE)

    /** 把选中项挂起为"复制"。 */
    fun beginCopy() = beginPending(TargetOperation.COPY)

    private fun beginPending(operation: TargetOperation) {
        val sources = selectedEntries()
        if (sources.isEmpty()) {
            _state.update { it.copy(message = "先选要操作的条目") }
            return
        }

        _state.update {
            it.copy(pending = PendingTarget(operation, sources), selected = emptySet(), message = null)
        }
    }

    /** 放弃这次移动/复制。 */
    fun cancelPending() = _state.update { it.copy(pending = null) }

    /**
     * 在当前目录执行挂起的移动/复制。
     *
     * 逐条下发、逐条计数：一条失败不该把其余几条也废掉。
     * 目标与源同目录时：移动跳过，复制改成「xxx - 副本」——否则服务端会因为"目标已存在"拒绝。
     */
    fun confirmPendingHere() {
        val state = _state.value
        val pending = state.pending ?: return
        val rootId = state.currentRootId
        if (rootId.isEmpty()) return

        val targetDirectory = state.currentPath
        val jobs = pending.sources.mapNotNull { entry ->
            val parent = entry.relativePath.substringBeforeLast('/', "")
            val intoItself = entry.isDirectory &&
                (targetDirectory == entry.relativePath ||
                    targetDirectory.startsWith(entry.relativePath + "/"))

            when {
                intoItself -> null
                parent == targetDirectory && pending.operation == TargetOperation.MOVE -> null
                parent == targetDirectory -> entry to copyNameIn(entry.name)
                else -> entry to entry.name
            }
        }

        if (jobs.isEmpty()) {
            _state.update { it.copy(pending = null, message = "目标就是原位置") }
            return
        }

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true) }

            var ok = 0
            var failed = 0
            var firstError: String? = null

            for ((entry, name) in jobs) {
                val destination = if (targetDirectory.isEmpty()) name else "$targetDirectory/$name"
                val outcome = when (pending.operation) {
                    TargetOperation.MOVE -> api.move(tokenOf(), rootId, entry.relativePath, destination)
                    TargetOperation.COPY -> api.copy(tokenOf(), rootId, entry.relativePath, destination)
                }

                when (outcome) {
                    is ApiOutcome.Success -> ok++
                    is ApiOutcome.Failure -> {
                        failed++
                        if (firstError == null) firstError = outcome.message
                    }
                }
            }

            val verb = if (pending.operation == TargetOperation.MOVE) "移动" else "复制"
            _state.update {
                it.copy(
                    busy = false,
                    pending = null,
                    message = if (failed == 0) {
                        "已$verb $ok 项"
                    } else {
                        "已$verb $ok 项，$failed 项失败：${firstError ?: "未知原因"}"
                    },
                )
            }
            reload()
        }
    }

    /** 同目录复制时的目标名：`a.txt` → `a - 副本.txt`。 */
    private fun copyNameIn(name: String): String {
        val dot = name.lastIndexOf('.')
        return if (dot <= 0) {
            "$name - 副本"
        } else {
            name.substring(0, dot) + " - 副本" + name.substring(dot)
        }
    }

    /**
     * 请求下载当前根下的一个文件。
     *
     * 服务端按 rootId + path 定位源文件并建下载任务，手机随后用既有的分块通道接收。
     * 成功后通过 [onDownloadReady] 把任务信息交出去，由界面弹 SAF 选择器落盘。
     */
    fun download(entry: FileEntryDto, onDownloadReady: (TransferItem) -> Unit) {
        if (entry.isDirectory) return

        val rootId = _state.value.currentRootId
        if (rootId.isEmpty()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true, message = null) }

            when (val outcome = api.download(tokenOf(), rootId, entry.relativePath)) {
                is ApiOutcome.Success -> {
                    _state.update { it.copy(busy = false, message = "正在下载 ${entry.name}") }
                    onDownloadReady(
                        TransferItem(
                            taskId = outcome.value.taskId,
                            direction = TransferDirections.DOWNLOAD,
                            fileName = outcome.value.fileName.ifEmpty { entry.name },
                            deviceName = outcome.value.deviceName,
                            totalBytes = outcome.value.totalBytes,
                            transferredBytes = outcome.value.transferredBytes,
                            chunkSize = outcome.value.chunkSize,
                            chunkCount = outcome.value.chunkCount,
                            state = outcome.value.state,
                            sha256 = outcome.value.sha256,
                            errorMessage = outcome.value.errorMessage,
                        ),
                    )
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(busy = false, message = outcome.message)
                }
            }
        }
    }

    /**
     * 下载整个文件夹：让服务端把它展开成一批任务，再交给界面选落点。
     *
     * 手机端**不**自己递归列目录：每个子目录一次 HTTP，几十个文件的文件夹要点半天；
     * 而且服务端展开时能顺手把符号链接、超限文件挡掉。
     */
    fun downloadFolder(entry: FileEntryDto, onBatchReady: (FolderDownloadResult) -> Unit) {
        if (!entry.isDirectory) return

        val rootId = _state.value.currentRootId
        if (rootId.isEmpty()) return

        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            _state.update { it.copy(busy = true, message = null) }

            when (val outcome = api.downloadFolder(tokenOf(), rootId, entry.relativePath)) {
                is ApiOutcome.Success -> {
                    val result = outcome.value
                    _state.update {
                        it.copy(
                            busy = false,
                            message = when {
                                result.batchId == null -> "「${entry.name}」里没有文件"
                                result.failed > 0 -> "已展开 ${result.created} 个文件，${result.failed} 个失败"
                                else -> "准备接收「${result.batchName}」共 ${result.created} 个文件"
                            },
                        )
                    }

                    if (result.batchId != null) {
                        onBatchReady(result)
                    }
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(busy = false, message = outcome.message)
                }
            }
        }
    }

    /** 刷新当前目录。 */
    fun reload() {
        viewModelScope.launch {
            val api = ensureApi() ?: return@launch
            val rootId = _state.value.currentRootId
            val path = _state.value.currentPath
            if (rootId.isEmpty()) return@launch

            when (val outcome = api.list(tokenOf(), rootId, path)) {
                is ApiOutcome.Success -> _state.update {
                    it.copy(
                        ready = true,
                        entries = outcome.value.entries,
                        warning = null,
                    )
                }

                is ApiOutcome.Failure -> _state.update {
                    // 目录加载失败是"当前目录用不了"，写 warning：
                    // message 属于一次性操作结果，成功后不清会一直挂在页面上
                    // （真机踩过：撤销授权后那条"该目录未授权"永远消不掉）。
                    it.copy(entries = emptyList(), warning = outcome.message)
                }
            }
        }
    }

    // ------------------------------------------------------------ 内部

    private suspend fun ensureApi(): FileApi? {
        val session = repository.loadSession() ?: return null
        val token = vault.load(session.deviceId) ?: return null
        val key = "${session.host}:${session.port}|$token"

        api?.let { existing -> if (key == apiKey) return existing }

        val endpoint = ServerEndpoint(session.host, session.port)
        val created = FileApi.create(endpoint, setOf(session.fingerprint))
        api = created
        apiKey = key
        return created
    }

    /** 连续失败多少次才判定"连不上"。 */
    private var consecutiveFailures = 0

    /**
     * 刷新"本地已授权"，并在授权集合发生变化时立刻请求上报一次索引。
     *
     * 整机访问是在系统设置里开的，回来后没有任何回调——只能靠这里轮询发现，
     * 发现变化就主动上报，否则用户要等下一轮（最长 12 秒）才能在电脑上看到。
     */
    private suspend fun refreshLocalRoots() {
        val previous = _state.value.localRoots.map { it.rootId }.toSet()

        // 走轻量版本：这里每 3 秒跑一次，不能每次都去探测提供器能力
        val deviceId = runCatching { repository.loadSession()?.deviceId }.getOrNull().orEmpty()
        val active = runCatching {
            grants.activeRoots(deviceId = deviceId.ifEmpty { null }, refreshCapabilities = false)
        }
            .onFailure { Log.w("FileBrowserViewModel", "读取本地授权失败", it) }
            .getOrDefault(emptyList())

        val mapped = active.map {
            LocalRootInfo(
                rootId = it.rootId,
                displayName = it.displayName,
                isMachine = it.rootId == AllFilesProvider.ROOT_ID,
            )
        }

        _state.update {
            it.copy(localRoots = mapped, allFilesGranted = AllFilesAccess.isGranted(context))
        }

        if (previous != mapped.map { it.rootId }.toSet()) {
            SessionSyncEngine.requestIndexPublish()
        }
    }

    private suspend fun refreshRoots(api: FileApi) {
        when (val outcome = api.roots(tokenOf())) {
            is ApiOutcome.Success -> {
                consecutiveFailures = 0
                val roots = outcome.value
                _state.update { current ->
                    val stillAllowed = roots.any { it.rootId == current.currentRootId }
                    val rootId = if (stillAllowed) {
                        current.currentRootId
                    } else {
                        roots.firstOrNull()?.rootId.orEmpty()
                    }

                    // 授权被撤销时必须清掉旧的条目与路径：
                    // 留着上一轮的列表会让用户以为还能访问，实际服务端已经 403。
                    val revoked = !stillAllowed && current.currentRootId.isNotEmpty()
                    current.copy(
                        ready = true,
                        roots = roots,
                        currentRootId = rootId,
                        currentPath = if (stillAllowed) current.currentPath else "",
                        entries = if (revoked || !stillAllowed) emptyList() else current.entries,
                        selected = if (revoked || !stillAllowed) emptySet() else current.selected,
                        // 只读判定必须跟着当前根走：自动选根时也要更新，
                        // 否则整机根下会露出"改名/移动"按钮。
                        readOnly = isReadOnly(roots, rootId),
                        allFilesGranted = AllFilesAccess.isGranted(context),
                    )
                }

                if (_state.value.currentRootId.isNotEmpty()) {
                    reload()
                }
            }

            is ApiOutcome.Failure -> {
                // 单次轮询失败很常见（息屏切换、Wi-Fi 抖动、系统省电临时挂起），
                // 不足以判定连接断开。以前一失败就把整页标成"无法连接到电脑"，
                // 而传输走的是另一条请求路径，于是出现"提示断线但文件照传"。
                consecutiveFailures++
                if (consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
                    _state.update { it.copy(ready = false, message = outcome.message) }
                }
            }
        }
    }

    private suspend fun tokenOf(): String {
        val session = repository.loadSession() ?: return ""
        return vault.load(session.deviceId).orEmpty()
    }

    companion object {
        private const val MAX_CONSECUTIVE_FAILURES = 3
        private const val POLL_MS = 3_000L
        private const val SESSION_RETRY_MS = 3_000L

        fun factory(
            context: Context,
            repository: ConnectionRepository,
            vault: TokenVault,
        ): ViewModelProvider.Factory = viewModelFactory {
            initializer { FileBrowserViewModel(context, repository, vault) }
        }
    }
}

/** 文件列表的排序方式。 */
enum class FileSortBy {
    /** 按名称。 */
    NAME,

    /** 按大小。 */
    SIZE,

    /** 按修改时间。 */
    MODIFIED,

    /** 按扩展名（类型）。 */
    TYPE,
}
