package com.wirelesscable.app.vm

import com.wirelesscable.app.core.protocol.TransferStates

import com.wirelesscable.app.core.transfer.TransferItem

/** 传输页界面状态。 */
data class TransferUiState(
    val items: List<TransferItem> = emptyList(),
    val ready: Boolean = false,
    val busy: Boolean = false,
    /** 最近一次操作的结果（上传/保存失败等），由用户操作触发。 */
    val message: String? = null,
    /** 轮询任务列表失败的原因。与 [message] 分开，避免一次瞬时超时长期挂在界面上。 */
    val refreshError: String? = null,
    /** 可作为上传落点的共享目录（该设备有 Upload 权限的）。 */
    val uploadTargets: List<UploadTargetOption> = emptyList(),
    /** 当前选中的上传落点；null 表示电脑的默认接收目录。 */
    val selectedUploadTargetId: String? = null,
    /** 当前页签。 */
    val tab: TransferTab = TransferTab.ACTIVE,
    /** 方向筛选：全部 / 我发送的 / 我收到的。 */
    val direction: TransferFilter = TransferFilter.ALL,
    /** 多选（长按进入）。 */
    val selected: Set<String> = emptySet(),
    /**
     * 收件目录写不进去（缺「所有文件访问权」）。
     *
     * 这时电脑发来的文件会一直停在「等待接收」，不写清楚用户只会觉得"又没收到"。
     */
    val inboxBlocked: Boolean = false,
) {
    /** 当前落点的显示名。 */
    val selectedUploadTargetName: String
        get() = uploadTargets.firstOrNull { it.rootId == selectedUploadTargetId }?.displayName ?: "默认接收目录"
    val uploads: List<TransferItem> get() = items.filter { it.isUpload }

    val downloads: List<TransferItem> get() = items.filter { !it.isUpload }

    val hasActive: Boolean get() = items.any { it.isActive }

    // ------------------------------------------------------------ 页签

    /** 「传输中」= 所有还没完成的任务：失败、暂停、中断都在这里。 */
    val activeTabItems: List<TransferItem>
        get() = items.filter { it.state != TransferStates.COMPLETED }

    val completedTabItems: List<TransferItem>
        get() = items.filter { it.state == TransferStates.COMPLETED }

    val visibleItems: List<TransferItem>
        get() {
            val byTab = if (tab == TransferTab.ACTIVE) activeTabItems else completedTabItems
            return when (direction) {
                TransferFilter.ALL -> byTab
                TransferFilter.SENT -> byTab.filter { it.isUpload }
                TransferFilter.RECEIVED -> byTab.filter { !it.isUpload }
            }
        }

    val directionLabel: String
        get() = when (direction) {
            TransferFilter.ALL -> "全部"
            TransferFilter.SENT -> "我发送的"
            TransferFilter.RECEIVED -> "我收到的"
        }

    // 页签上的数字是**行数**：一整批算一行，和列表里看到的一致
    val activeTabLabel: String get() = "传输中 ${rowsOf(activeTabItems).size}"

    val completedTabLabel: String get() = "已完成 ${rowsOf(completedTabItems).size}"

    val emptyHint: String
        get() = if (tab == TransferTab.ACTIVE) "没有正在进行的传输" else "还没有已完成的传输"

    // ------------------------------------------------------------ 行

    /**
     * 列表里实际渲染的行：同一个 `batchId` 的任务收成一行。
     *
     * 一次「下载整个文件夹」会在服务端建出几十上百个任务，逐条列出来会把
     * 整个传输页刷满，用户也看不出它们是同一个文件夹。收成一行之后，
     * 行上的暂停/取消/删除都作用到整批（语义由用户定：整批停、整批删）。
     */
    val rows: List<TransferRow>
        get() = rowsOf(visibleItems)

    /**
     * 首页「传输中」那一块：**不看页签**，永远是进行中的行（整批已收成一行）。
     *
     * 首页和传输页共用同一份状态，直接用 [rows] 的话，
     * 用户在传输页切到「已完成」再回首页，首页会跟着显示已完成的东西。
     */
    val activeRows: List<TransferRow>
        get() = rowsOf(items.filter { it.state != TransferStates.COMPLETED })

    /** 按 `batchId` 把任务收成行；顺序按"第一次出现"，不重排（重排会随进度跳）。 */
    private fun rowsOf(source: List<TransferItem>): List<TransferRow> {
        val grouped = LinkedHashMap<String, MutableList<TransferItem>>()
        source.forEach { item ->
            val batchId = item.batchId
            if (!batchId.isNullOrBlank()) {
                grouped.getOrPut(batchId) { mutableListOf() }.add(item)
            }
        }

        val emitted = HashSet<String>()
        val result = mutableListOf<TransferRow>()
        source.forEach { item ->
            val batchId = item.batchId
            if (batchId.isNullOrBlank()) {
                result += TransferRow.Single(item)
                return@forEach
            }

            // 整批按"第一次出现"的位置插进列表，和逐条时的顺序保持一致
            if (!emitted.add(batchId)) return@forEach
            val entries = grouped[batchId].orEmpty()
            result += TransferRow.Batch(batchId, item.batchName ?: "文件夹", entries)
        }

        // 只把"进行中"提到前面；同组内保持服务端顺序（排序是稳定的）。
        // 不能按 updatedAt 排——它每次进度回报都变，行会来回跳。
        return result.sortedByDescending { it.isActive }
    }

    // ------------------------------------------------------------ 多选

    val selectedItems: List<TransferItem> get() = items.filter { it.taskId in selected }

    /** 选中的行（整批要整批选中才算）。 */
    val selectedRows: List<TransferRow>
        get() = rows.filter { row -> row.taskIds.all { it in selected } }

    val hasSelection: Boolean get() = selected.isNotEmpty()

    val selectionText: String
        get() {
            val rows = selectedRows
            val files = rows.sumOf { it.taskIds.size }
            // 整批选中时说"1 项"会让人以为只选了一个文件，把文件数一起写出来
            return if (files == rows.size) "已选 ${rows.size} 项" else "已选 ${rows.size} 项 · $files 个文件"
        }

    /**
     * 选中项里有没有**本机收到**的文件可删。
     *
     * 删除只作用于本机：电脑上的文件一律不碰（那是对方的目录，手机没有资格删）。
     * 所以只统计"电脑发过来、已经落盘"的任务。
     */
    val canDeleteFiles: Boolean
        get() = selectedItems.any {
            !it.isUpload && it.state == TransferStates.COMPLETED && it.savedToPhone
        }

    /** 选中项里有没有能打开的本机文件（已完成下载）。 */
    val openableItem: TransferItem?
        get() = selectedItems.singleOrNull()?.takeIf { it.isOpenableHere }
}

/** 传输中心的两个页签。失败/暂停/中断都归在「传输中」。 */
enum class TransferTab { ACTIVE, COMPLETED }

/** 方向筛选：一条记录不是"我发的"就是"我收的"。 */
enum class TransferFilter { ALL, SENT, RECEIVED }

/** 一个可作为上传落点的共享目录。 */
data class UploadTargetOption(
    val rootId: String,
    val displayName: String,
)

/**
 * 传输列表里的一行：单条任务，或一整批（一次「下载整个文件夹」）。
 *
 * [taskIds] 是这一行覆盖的所有任务 —— 多选、暂停、取消、删除都按它走，
 * 界面不需要知道"这一行到底是一条还是一批"。
 */
sealed interface TransferRow {
    /** 稳定 key：单条用 taskId，整批用 `batch:<batchId>`。 */
    val key: String

    val taskIds: List<String>

    data class Single(val item: TransferItem) : TransferRow {
        override val key: String get() = item.taskId
        override val taskIds: List<String> get() = listOf(item.taskId)
        override val isActive: Boolean get() = item.isActive
    }

    data class Batch(
        val batchId: String,
        val name: String,
        val entries: List<TransferItem>,
    ) : TransferRow {
        override val key: String get() = "batch:$batchId"
        override val taskIds: List<String> get() = entries.map { it.taskId }

        /** 代表行：名字和设备名取它（同批里这几项都一样）。 */
        val lead: TransferItem get() = entries.first()
        val fileCount: Int get() = entries.size
        override val isActive: Boolean get() = entries.any { it.isActive }
        val isUpload: Boolean get() = lead.isUpload

        val completedCount: Int get() = entries.count { it.state == TransferStates.COMPLETED }
        val totalBytes: Long get() = entries.sumOf { it.totalBytes }
        val transferredBytes: Long get() = entries.sumOf { it.transferredBytes }
        val bytesPerSecond: Long get() = entries.sumOf { it.bytesPerSecond }

        /** 整批都暂停了才算"已暂停"：一条暂停、一条在传时，按钮该是"暂停"。 */
        val allPaused: Boolean get() = entries.isNotEmpty() && entries.all { it.state == TransferStates.PAUSED }

        val allFailed: Boolean
            get() = entries.isNotEmpty() && entries.all {
                it.state == TransferStates.FAILED || it.state == TransferStates.CANCELLED
            }

        val progress: Float
            get() = when {
                totalBytes > 0L -> (transferredBytes.toFloat() / totalBytes).coerceIn(0f, 1f)
                entries.all { TransferStates.isTerminal(it.state) } -> 1f
                else -> 0f
            }
    }

    /** 这一行有没有还没结束的任务（决定要不要给暂停/取消）。 */
    val isActive: Boolean
}
