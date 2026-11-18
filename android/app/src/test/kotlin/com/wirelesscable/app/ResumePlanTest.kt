package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.ChunkInfo
import com.wirelesscable.app.core.protocol.ChunkStates
import com.wirelesscable.app.core.transfer.ChunkAction
import com.wirelesscable.app.core.transfer.pendingChunks
import com.wirelesscable.app.core.transfer.planReconcile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 恢复决策的纯逻辑测试：服务端位图 × 本地记录的全部组合。
 * 这里的规则是"恢复时以服务端为准"的直接体现。
 */
class ResumePlanTest {

    private fun chunk(index: Int, status: String) = ChunkInfo(
        transferId = "task",
        index = index,
        offset = index * 1024L,
        length = 1024,
        sha256 = if (status == ChunkStates.COMPLETED) "a".repeat(64) else null,
        status = status,
    )

    @Test
    fun serverCompletedLocalMissing_MarksLocalWithoutRefetch() {
        // 上次会话写完并回执过的块：只补记，不重传
        val steps = planReconcile(
            serverChunks = listOf(chunk(0, ChunkStates.COMPLETED), chunk(1, ChunkStates.PENDING)),
            localCompleted = emptySet(),
        )

        assertEquals(
            listOf(
                com.wirelesscable.app.core.transfer.ReconcileStep(0, ChunkAction.MARK_LOCAL_COMPLETED),
                com.wirelesscable.app.core.transfer.ReconcileStep(1, ChunkAction.FETCH),
            ),
            steps,
        )
    }

    @Test
    fun localCompletedServerUnknown_VerifiesThenAcks() {
        // 写完但回执没到达（APP 在两者之间被杀）：验哈希补回执，不盲目重传
        val steps = planReconcile(
            serverChunks = listOf(chunk(0, ChunkStates.PENDING)),
            localCompleted = setOf(0),
        )

        assertEquals(1, steps.size)
        assertEquals(ChunkAction.VERIFY_AND_ACK, steps[0].action)
    }

    @Test
    fun bothCompleted_NoAction() {
        val steps = planReconcile(
            serverChunks = listOf(chunk(0, ChunkStates.COMPLETED), chunk(1, ChunkStates.COMPLETED)),
            localCompleted = setOf(0, 1),
        )

        assertTrue(steps.isEmpty())
    }

    @Test
    fun mixedBitmap_ProducesFullPlan() {
        val steps = planReconcile(
            serverChunks = listOf(
                chunk(0, ChunkStates.COMPLETED),
                chunk(1, ChunkStates.PENDING),
                chunk(2, ChunkStates.PENDING),
                chunk(3, ChunkStates.COMPLETED),
            ),
            localCompleted = setOf(0, 1),
        )

        assertEquals(
            mapOf(
                1 to ChunkAction.VERIFY_AND_ACK,
                2 to ChunkAction.FETCH,
                3 to ChunkAction.MARK_LOCAL_COMPLETED,
            ),
            steps.associate { it.index to it.action },
        )
    }

    @Test
    fun pendingChunks_ReturnsOnlyIncomplete() {
        val pending = pendingChunks(
            listOf(
                chunk(0, ChunkStates.COMPLETED),
                chunk(1, ChunkStates.PENDING),
                chunk(2, ChunkStates.PENDING),
            ),
        )

        assertEquals(listOf(1, 2), pending.map { it.index })
    }
}
