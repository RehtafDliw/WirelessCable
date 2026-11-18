package com.wirelesscable.app.vm

import android.content.Context
import android.util.Log
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.wirelesscable.app.core.files.AllFilesProvider
import com.wirelesscable.app.core.files.FileEntry
import com.wirelesscable.app.core.files.FileOpener
import com.wirelesscable.app.core.files.FileResult
import com.wirelesscable.app.core.files.FileStatus
import com.wirelesscable.app.core.files.InboxStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 「传输目录」：电脑发到手机的文件。
 *
 * 不作为文件管理器用（那是系统「文件」App 的事），但**要能多选删掉**——
 * 收件目录会越攒越多，而"删一个文件还得去系统文件管理器找半天"是常态痛点。
 * 交互与「传输中心」一致：短按打开、长按进多选。
 */
data class InboxUiState(
    val granted: Boolean = false,
    val loading: Boolean = false,
    val entries: List<FileEntry> = emptyList(),
    val message: String? = null,
    /** 选中的条目按 `relativePath` 记（名字可能重名，路径不会）。 */
    val selected: Set<String> = emptySet(),
) {
    val hasSelection: Boolean get() = selected.isNotEmpty()
    val selectionText: String get() = "已选 ${selected.size} 项"
}

class InboxViewModel(private val context: Context) : ViewModel() {

    private val _state = MutableStateFlow(InboxUiState())
    val state: StateFlow<InboxUiState> = _state.asStateFlow()

    private val provider = AllFilesProvider(context)

    /** 进入页面 / 收到文件后调用。 */
    fun refresh() {
        if (_state.value.loading) return

        viewModelScope.launch {
            _state.update { it.copy(loading = true) }

            val granted = InboxStore.isAvailable(context)
            if (!granted) {
                _state.update { it.copy(granted = false, loading = false, entries = emptyList()) }
                return@launch
            }

            val entries = withContext(Dispatchers.IO) { collectInboxFiles() }

            _state.update { it.copy(granted = true, loading = false, entries = entries) }
        }
    }

    // ------------------------------------------------------------ 多选

    fun startSelection(relativePath: String) {
        _state.update { it.copy(selected = setOf(relativePath)) }
    }

    fun toggleSelect(relativePath: String) {
        _state.update {
            val next = if (relativePath in it.selected) it.selected - relativePath
            else it.selected + relativePath
            it.copy(selected = next)
        }
    }

    fun clearSelection() {
        _state.update { it.copy(selected = emptySet()) }
    }

    fun selectAll() {
        _state.update { it.copy(selected = it.entries.map { entry -> entry.relativePath }.toSet()) }
    }

    /** 删掉选中的文件。删不掉的逐条计数，最后如实报出来。 */
    fun deleteSelected() {
        val targets = _state.value.entries.filter { it.relativePath in _state.value.selected }
        if (targets.isEmpty()) {
            return
        }

        viewModelScope.launch {
            var failed = 0
            withContext(Dispatchers.IO) {
                targets.forEach { entry ->
                    val result = provider.delete(entry.relativePath, recursive = false)
                    if (result is FileResult.Failure) {
                        Log.w("WcInbox", "删除失败 ${entry.name}：${result.message}")
                        failed++
                    }
                }
            }

            _state.update {
                it.copy(
                    selected = emptySet(),
                    // 成功时不写提示：文件从列表里消失本身就是反馈
                    message = if (failed > 0) "有 $failed 个文件删不掉" else null,
                )
            }

            refresh()
        }
    }

    /** 用系统里能打开它的应用打开。 */
    fun open(entry: FileEntry) {
        val file = InboxStore.fileOf(entry.relativePath)
        FileOpener.open(context, file)?.let { message ->
            _state.update { it.copy(message = message) }
        }
    }

    /**
     * 收件目录里的文件：顶层 + 直连子目录。
     *
     * 只列顶层的话，当面直连收到的文件（落在 `直连/`）在应用里根本看不到，
     * 用户得去系统文件管理器翻；但也不能递归所有子目录——用户自己的文件夹
     * （便携软件之类）动辄上千个文件，会把这一页撑爆。
     */
    private suspend fun collectInboxFiles(): List<FileEntry> {
        val result = mutableListOf<FileEntry>()

        suspend fun scan(relativePath: String, prefix: String) {
            when (val listed = provider.list(relativePath)) {
                is FileResult.Success -> listed.value.forEach { entry ->
                    if (entry.isDirectory) return@forEach
                    // 分片临时文件不给人看（中断续传时才会有）
                    if (entry.name.startsWith(".wcpart-")) return@forEach
                    result += if (prefix.isEmpty()) entry else entry.copy(name = prefix + entry.name)
                }

                is FileResult.Failure -> {
                    // 目录还不存在是正常状态（还没收到过文件），不当作错误
                    if (listed.status != FileStatus.NOT_FOUND) {
                        Log.w("WcInbox", "读取 $relativePath 失败：${listed.message}")
                    }
                }
            }
        }

        scan(InboxStore.RELATIVE_PATH, "")
        scan("${InboxStore.RELATIVE_PATH}/${InboxStore.DIRECT_SUBDIR}", "${InboxStore.DIRECT_SUBDIR}/")

        return result.sortedByDescending { it.modifiedUnixMs }
    }

    fun clearMessage() {
        _state.update { it.copy(message = null) }
    }

    companion object {
        fun factory(context: Context): ViewModelProvider.Factory = object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <T : ViewModel> create(modelClass: Class<T>): T =
                InboxViewModel(context.applicationContext) as T
        }
    }
}
