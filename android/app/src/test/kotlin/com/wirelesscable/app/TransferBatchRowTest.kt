package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.TransferDirections
import com.wirelesscable.app.core.protocol.TransferStates
import com.wirelesscable.app.core.transfer.TransferItem
import com.wirelesscable.app.vm.TransferFilter
import com.wirelesscable.app.vm.TransferRow
import com.wirelesscable.app.vm.TransferTab
import com.wirelesscable.app.vm.TransferUiState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 传输列表的"整批收成一行"。
 *
 * 这条逻辑决定用户看到的是"一个文件夹"还是"200 个文件"，
 * 也决定暂停/删除会作用到几个任务——纯计算，值得钉住。
 */
class TransferBatchRowTest {

    private fun download(
        id: String,
        name: String = "$id.txt",
        state: String = TransferStates.TRANSFERRING,
        batchId: String? = null,
        batchName: String? = null,
        total: Long = 100L,
        done: Long = 100L,
        updatedAt: Long = 0L,
    ) = TransferItem(
        taskId = id,
        direction = TransferDirections.DOWNLOAD,
        fileName = name,
        deviceName = "PC",
        totalBytes = total,
        transferredBytes = done,
        state = state,
        batchId = batchId,
        batchName = batchName,
        updatedAt = updatedAt,
    )

    @Test
    fun `同一批次收成一行`() {
        val state = TransferUiState(
            items = listOf(
                download("t1", batchId = "b1", batchName = "照片"),
                download("t2", batchId = "b1", batchName = "照片"),
                download("t3", batchId = "b1", batchName = "照片"),
                download("single"),
            ),
        )

        val rows = state.rows
        assertEquals(2, rows.size)

        val batch = rows.filterIsInstance<TransferRow.Batch>().single()
        // key 带首条 taskId：同一目录可以有多批（昨天 128 张 + 今天 330 张），
        // 只用 batchId 会让 LazyColumn 的 key 冲突
        assertTrue(batch.key.startsWith("batch:b1#"))
        assertEquals("照片", batch.name)
        assertEquals(3, batch.fileCount)
        assertEquals(listOf("t1", "t2", "t3"), batch.taskIds)
        // 名字必须是文件夹名：显示成某个文件名用户就认不出来了
        assertFalse(batch.name.endsWith(".txt"))

        val single = rows.filterIsInstance<TransferRow.Single>().single()
        assertEquals(listOf("single"), single.taskIds)
    }

    @Test
    fun `批内进度按整批算`() {
        val state = TransferUiState(
            items = listOf(
                download("t1", batchId = "b1", total = 100, done = 100),
                download("t2", batchId = "b1", total = 100, done = 0),
                download("t3", batchId = "b1", total = 200, done = 100),
            ),
        )

        val batch = state.rows.filterIsInstance<TransferRow.Batch>().single()
        assertEquals(400L, batch.totalBytes)
        assertEquals(200L, batch.transferredBytes)
        assertEquals(0.5f, batch.progress, 0.001f)
        assertEquals(0, batch.completedCount)
        assertTrue(batch.isActive)
    }

    @Test
    fun `还有在传的批整批留在传输中`() {
        val items = listOf(
            download("done", batchId = "b1", state = TransferStates.COMPLETED),
            download("live", batchId = "b1", state = TransferStates.TRANSFERRING),
        )

        val active = TransferUiState(items = items, tab = TransferTab.ACTIVE).rows
        val completed = TransferUiState(items = items, tab = TransferTab.COMPLETED).rows

        // 一批 330 个传完 300 个时，这一行要留在「传输中」并显示 300/330。
        // 拆成两半会让用户在两个页签各看到一行，以为传了两批。
        assertEquals(listOf("done", "live"), (active.single() as TransferRow.Batch).taskIds)
        assertTrue(completed.isEmpty())
    }

    @Test
    fun `整批完成才进已完成`() {
        val items = listOf(
            download("a", batchId = "b1", state = TransferStates.COMPLETED),
            download("b", batchId = "b1", state = TransferStates.COMPLETED),
        )

        val active = TransferUiState(items = items, tab = TransferTab.ACTIVE).rows
        val completed = TransferUiState(items = items, tab = TransferTab.COMPLETED).rows

        assertTrue(active.isEmpty())
        assertEquals(listOf("a", "b"), (completed.single() as TransferRow.Batch).taskIds)
    }

    @Test
    fun `方向筛选后批次不串`() {
        val state = TransferUiState(
            items = listOf(
                download("d1", batchId = "b1"),
                download("d2", batchId = "b1"),
            ),
            direction = TransferFilter.SENT,
        )

        // 全是下载任务，筛"我发送的"之后应该什么都没有
        assertTrue(state.rows.isEmpty())
    }

    @Test
    fun `整批选中时提示里带上文件数`() {
        val state = TransferUiState(
            items = listOf(
                download("t1", batchId = "b1", batchName = "照片"),
                download("t2", batchId = "b1", batchName = "照片"),
                download("t3", batchId = "b1", batchName = "照片"),
            ),
            // 点一行 = 选中整批（三个 id）
            selected = setOf("t1", "t2", "t3"),
        )

        assertEquals(1, state.selectedRows.size)
        // 说"已选 1 项"会让人以为只删一个文件，必须把文件数写出来
        assertEquals("已选 1 项 · 3 个文件", state.selectionText)
    }

    @Test
    fun `只勾中批里的一条不算选中整行`() {
        val state = TransferUiState(
            items = listOf(
                download("t1", batchId = "b1"),
                download("t2", batchId = "b1"),
            ),
            selected = setOf("t1"),
        )

        assertTrue(state.selectedRows.isEmpty())
        assertTrue(state.hasSelection)
    }
}
