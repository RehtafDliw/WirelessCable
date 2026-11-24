package com.wirelesscable.app.vm

import android.util.Log
import com.wirelesscable.app.core.protocol.TransferStates

import com.wirelesscable.app.core.transfer.TransferItem

/** 同一目录的两次传输隔了这么久，就当成两批（目录会被复用，光看目录名会混在一起）。 */
private const val BATCH_GAP_MS = 5 * 60 * 1000L

/** 任务可能几十毫秒就结束、卡在两次轮询之间；刚更新过的行先别急着移到「已完成」。 */
private const val LIVE_WINDOW_MS = 8_000L

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
    /**
     * 点「继续」时发现已被另一端取消的任务。
     *
     * 对面删记录会先暂停再取消，本机的行还留着但服务端已经终结；继续只会得到
     * 一句"已被接收方取消"。界面据此问一句"要删掉这条记录吗"，
     * 而不是把错误塞进 message 一闪而过、留下一行永远传不动。
     */
    val peerCancelled: List<String> = emptyList(),
) {
    /** 当前落点的显示名。 */
    val selectedUploadTargetName: String
        get() = uploadTargets.firstOrNull { it.rootId == selectedUploadTargetId }?.displayName ?: "默认接收目录"
    val uploads: List<TransferItem> get() = items.filter { it.isUpload }

    val downloads: List<TransferItem> get() = items.filter { !it.isUpload }

    val hasActive: Boolean get() = items.any { it.isActive }

    // ------------------------------------------------------------ 页签

    /**
     * 按方向筛过的全部条目，**不分页签**。
     *
     * 页签要在分组之后再分：一个 300 文件的文件夹，传完的立刻满足 `COMPLETED`，
     * 没传完的还挂着——先按单条分流就会把同一批劈成两半，用户在「传输中」只看到
     * 零星的几条，进度数字在「已完成」里疯涨，永远看不到整批的 `已完成 120/300`。
     */
    val byDirection: List<TransferItem>
        get() = when (direction) {
            TransferFilter.ALL -> items
            TransferFilter.SENT -> items.filter { it.isUpload }
            TransferFilter.RECEIVED -> items.filter { !it.isUpload }
        }

    /** 整组还有没传完的文件 → 归「传输中」；全部传完才掉进「已完成」。 */
    val activeTabRows: List<TransferRow>
        get() {
            val now = System.currentTimeMillis()
            return rowsOf(byDirection).filter { it.isLive(now) }
        }

    val completedTabRows: List<TransferRow>
        get() {
            val now = System.currentTimeMillis()
            return rowsOf(byDirection).filter { !it.isLive(now) }
        }

    val directionLabel: String
        get() = when (direction) {
            TransferFilter.ALL -> "全部"
            TransferFilter.SENT -> "我发送的"
            TransferFilter.RECEIVED -> "我收到的"
        }

    // 页签上的数字是**行数**：一整批算一行，和列表里看到的一致
    // 标签上的数字用**文件条数**：一整批只占一行，显示组数会让人以为只有几个文件
    val activeTabLabel: String get() = "传输中 ${activeTabRows.sumOf { it.taskIds.size }}"

    val completedTabLabel: String get() = "已完成 ${completedTabRows.sumOf { it.taskIds.size }}"

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
        get() = if (tab == TransferTab.ACTIVE) activeTabRows else completedTabRows

    /**
     * 首页「传输中」那一块：**不看页签**，永远是进行中的行（整批已收成一行）。
     *
     * 首页和传输页共用同一份状态，直接用 [rows] 的话，
     * 用户在传输页切到「已完成」再回首页，首页会跟着显示已完成的东西。
     */
    val activeRows: List<TransferRow>
        get() {
            val now = System.currentTimeMillis()
            return rowsOf(items).filter { it.isLive(now) }
        }

    /** 按 `batchId` 把任务收成行；没有批次的按落点目录兜底，顺序按"第一次出现"。 */
    private fun rowsOf(source: List<TransferItem>): List<TransferRow> {
        // **取消的记录两端都不显示**（需求 2）：取消 = 这条传输到此为止。
        // 不排除的话它会落进「已完成」页签、显示成一行红色，
        // 用户以为传完了，其实半成品已经被清掉（真机：文件夹是空的）。
        val visible = source.filter { it.state != TransferStates.CANCELLED }

        // 整文件夹下载走的是逐条作业，服务端不会给这些任务写 batchId，
        // 于是 330 张图铺成 330 行。用落点目录的第一段兜底分组；
        // 单文件下载的 relativePath 就是文件名（没有 '/'），不会被误并进去。
        fun keyOf(item: TransferItem): String? {
            item.batchId?.takeIf { it.isNotBlank() }?.let { return it }
            val slash = item.relativePath.indexOf('/')
            return if (slash > 0) "dir:" + item.relativePath.substring(0, slash) else null
        }

        val buckets = LinkedHashMap<String, MutableList<TransferItem>>()
        visible.forEach { item ->
            keyOf(item)?.let { key -> buckets.getOrPut(key) { mutableListOf() }.add(item) }
        }

        // 目录会复用：昨天传的 128 张和今天传的 330 张都在 Screenshots 里。
        // 光按目录合并会显示成"文件夹只有 330 个文件，怎么传了 458 个"，
        // 所以桶内再按更新时间断层切成多批。用 updatedAt 而不是创建时间：
        // 传输中的条目一直在刷新，断层只会出现在"两批之间"。
        val chunks = buckets.mapValues { (key, list) ->
            val sorted = list.sortedBy { it.updatedAt }
            val parts = mutableListOf<List<TransferItem>>()
            var current = mutableListOf<TransferItem>()

            sorted.forEach { item ->
                if (current.isNotEmpty() && item.updatedAt - current.last().updatedAt > BATCH_GAP_MS) {
                    parts += current
                    current = mutableListOf()
                }

                current += item
            }

            if (current.isNotEmpty()) {
                parts += current
            }

            val name = list.first().batchName ?: key.removePrefix("dir:")
            // 只有该目录只切出一批时才用 batchTotal：同目录的第二批会共用同一个总数
            // （它描述的是"这个文件夹有多大"，不是"这一批多大"），两批都显示 330 会翻倍。
            val lead = parts.first().firstOrNull()
            val planned = if (parts.size == 1) lead?.batchTotal ?: 0 else 0
            val plannedBytes = if (parts.size == 1) lead?.batchTotalBytes ?: 0L else 0L
            parts.map { entries -> TransferRow.Batch(key, name, entries, planned, plannedBytes) }
        }

        val emitted = HashSet<String>()
        val result = mutableListOf<TransferRow>()
        source.forEach { item ->
            val key = keyOf(item)
            if (key == null) {
                result += TransferRow.Single(item)
                return@forEach
            }

            // 整批按"第一次出现"的位置插进列表，和逐条时的顺序保持一致
            if (!emitted.add(key)) return@forEach
            result += chunks[key].orEmpty()
        }

        // 诊断：一次批量里到底拿到几个 distinct 目录、首条的 relativePath 是什么。
        // 分组失效时这两个值能立刻区分"字段没到"和"分组逻辑没跑"。
        if (source.size > 20) {
            Log.d(
                "TransferUi",
                "rowsOf: ${source.size} 条 → ${result.size} 行, 首条 path='${source.first().relativePath}', file='${source.first().fileName}'",
            )
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

    /**
     * 这一行涉及的任务状态。
     *
     * 能力矩阵（暂停/继续/取消/删除该不该给按钮）按状态算；
     * 整批行只要有一条不满足，那个动作就不该出现——所以这里要**全部**状态。
     */
    val states: List<String>

    data class Single(val item: TransferItem) : TransferRow {
        override val key: String get() = item.taskId
        override val taskIds: List<String> get() = listOf(item.taskId)
        override val states: List<String> get() = listOf(item.state)
        override val isActive: Boolean get() = item.isActive
        override fun isLive(now: Long): Boolean =
            // 取消也算"结束了"：它不会再有进展，留在「传输中」只会让那一页永远清不空。
            // 与电脑端口径一致（那边 cancelled 是终态，直接落进「已完成」）。
            (item.state != TransferStates.COMPLETED && item.state != TransferStates.CANCELLED) ||
                now - item.updatedAt < LIVE_WINDOW_MS
    }

    data class Batch(
        val batchId: String,
        val name: String,
        val entries: List<TransferItem>,
        /**
         * 电脑下发的本批总数（0 = 未知）。
         *
         * 手机逐条执行作业，条目是边传边建出来的；只看 `entries.size` 的话，
         * 传了 17 个就显示「17/17 · 100%」，而实际还有 300 多个在排队。
         */
        val planned: Int = 0,
        /**
         * 整批总字节（电脑下发，0 = 未知）。
         *
         * 进度条的分母必须是它：传输任务是逐条建出来的，用已见条目的总大小
         * 当分母，分母会随着任务创建越来越大，进度就来回跳。
         */
        val plannedBytes: Long = 0L,
    ) : TransferRow {
        // 同一目录可能有多批：key 必须带上首条 taskId，否则 LazyColumn 的 key 冲突
        override val key: String get() = "batch:$batchId#${entries.first().taskId}"
        override val taskIds: List<String> get() = entries.map { it.taskId }
        override val states: List<String> get() = entries.map { it.state }

        /** 代表行：名字和设备名取它（同批里这几项都一样）。 */
        val lead: TransferItem get() = entries.first()
        val fileCount: Int get() = maxOf(entries.size, planned)
        override val isActive: Boolean get() = entries.any { it.isActive }
        override fun isLive(now: Long): Boolean =
            entries.any {
                it.state != TransferStates.COMPLETED && it.state != TransferStates.CANCELLED
            } || entries.any { now - it.updatedAt < LIVE_WINDOW_MS }
        val isUpload: Boolean get() = lead.isUpload

        val completedCount: Int get() = entries.count { it.state == TransferStates.COMPLETED }
        val totalBytes: Long get() = entries.sumOf { it.totalBytes }
        val transferredBytes: Long get() = entries.sumOf { it.transferredBytes }
        val bytesPerSecond: Long get() = entries.sumOf { it.bytesPerSecond }

        /**
         * 整批现在是不是"暂停态"：没有在跑的，且至少有一条被暂停。
         *
         * 不能用 `all { PAUSED }`：整批里已经传完的那些永远是 completed，
         * 只要有一个完成，`all` 就永远为假 —— 暂停后再也点不到"继续"。
         */
        val allPaused: Boolean get() = !isActive && entries.any { it.state == TransferStates.PAUSED }

        val allFailed: Boolean
            get() = entries.isNotEmpty() && entries.all {
                it.state == TransferStates.FAILED || it.state == TransferStates.CANCELLED
            }

        val progress: Float
            get() = when {
                // 优先按整批总字节：这是唯一稳定的分母，进度随字节连续增长，
                // 不会因为"又建出来一个任务"而往回跳。
                plannedBytes > 0L -> (transferredBytes.toFloat() / plannedBytes).coerceIn(0f, 1f)
                totalBytes > 0L -> (transferredBytes.toFloat() / totalBytes).coerceIn(0f, 1f)
                entries.all { TransferStates.isTerminal(it.state) } -> 1f
                else -> 0f
            }
    }

    /** 这一行有没有还没结束的任务（决定要不要给暂停/取消）。 */
    val isActive: Boolean

    /**
     * 这一行还在进行吗。
     *
     * 光看"有没有没传完的"不够：单个文件几十毫秒就结束，卡在两次轮询之间，
     * 界面上「传输中」永远是空的、数字全堆在「已完成」。所以再加一条：
     * 组里最近还有条目被更新过，就说明这一批还没结束。
     */
    fun isLive(now: Long): Boolean
}
