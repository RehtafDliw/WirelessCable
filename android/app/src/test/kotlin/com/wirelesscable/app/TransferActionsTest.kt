package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.TransferStates
import com.wirelesscable.app.core.transfer.TransferActions
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File

/**
 * 能力矩阵（手机端）的装配与查询。
 *
 * 契约表本身由 [TransferStateMachineContractTest] 校验与 Android 常量一致；
 * 这里验证的是**读表之后怎么判**——按钮该不该出现、批量怎么算。
 * 桌面端有对应的一组断言（TransferStateMachineContractTests），两端读同一份表。
 */
class TransferActionsTest {

    @Before
    fun load() {
        val file = contractFile()
        assertTrue("契约表装配失败：${file.path}", TransferActions.initFromJson(file.readText()))
    }

    @Test
    fun `transferring offers pause and cancel but never delete`() {
        assertTrue(TransferActions.shows(TransferActions.PAUSE, TransferStates.TRANSFERRING))
        assertTrue(TransferActions.shows(TransferActions.CANCEL, TransferStates.TRANSFERRING))
        assertFalse(TransferActions.shows(TransferActions.DELETE_RECORD, TransferStates.TRANSFERRING))
        assertFalse(TransferActions.shows(TransferActions.RESUME, TransferStates.TRANSFERRING))
    }

    @Test
    fun `paused offers resume and cancel but not pause`() {
        assertTrue(TransferActions.shows(TransferActions.RESUME, TransferStates.PAUSED))
        assertTrue(TransferActions.shows(TransferActions.CANCEL, TransferStates.PAUSED))
        assertFalse(TransferActions.shows(TransferActions.PAUSE, TransferStates.PAUSED))
    }

    @Test
    fun `interrupted can be resumed manually`() {
        // 以前只判 PAUSED，中断的任务在界面上没有任何按钮，只能干等自动重连。
        assertTrue(TransferActions.shows(TransferActions.RESUME, TransferStates.INTERRUPTED))
    }

    @Test
    fun `finished records can be deleted`() {
        listOf(TransferStates.COMPLETED, TransferStates.FAILED, TransferStates.CANCELLED).forEach { state ->
            assertTrue(state, TransferActions.shows(TransferActions.DELETE_RECORD, state))
        }
    }

    @Test
    fun `batch delete needs every row to be deletable`() {
        val deletable = listOf(TransferStates.COMPLETED, TransferStates.FAILED)

        assertTrue(TransferActions.allShow(TransferActions.DELETE_RECORD, deletable))
        assertFalse(
            "混进一条还在传的就不该给删除按钮",
            TransferActions.allShow(
                TransferActions.DELETE_RECORD,
                deletable + TransferStates.TRANSFERRING,
            ),
        )
        assertTrue(TransferActions.anyShow(TransferActions.CANCEL, deletable + TransferStates.TRANSFERRING))
    }

    @Test
    fun `empty selection offers nothing`() {
        assertFalse(TransferActions.allShow(TransferActions.DELETE_RECORD, emptyList()))
        assertFalse(TransferActions.anyShow(TransferActions.CANCEL, emptyList()))
    }

    @Test
    fun `unknown state falls back to hiding every action`() {
        // 表里没有的状态（新版本服务端加的状态、或者读表失败）一律不显示按钮：
        // 宁可不给，也不能给一个点了会失败的动作。
        listOf(
            TransferActions.PAUSE,
            TransferActions.RESUME,
            TransferActions.CANCEL,
            TransferActions.DELETE_RECORD,
        ).forEach { action ->
            assertFalse(action, TransferActions.shows(action, "some_future_state"))
        }
    }

    @Test
    fun `accepts distinguishes rejected from ok`() {
        // 批量删记录时混进在传的任务：服务端整体成功但这一条被跳过，
        // 对单条而言就是"不接受"，界面不该给这个按钮。
        assertFalse(TransferActions.accepts(TransferActions.DELETE_RECORD, TransferStates.TRANSFERRING))
        assertTrue(TransferActions.accepts(TransferActions.CANCEL, TransferStates.PAUSED))
    }

    private fun contractFile(): File {
        var dir: File? = File(System.getProperty("user.dir") ?: ".")
        while (dir != null) {
            val candidate = File(dir, "contracts/transfer-state-machine.json")
            if (candidate.isFile) {
                return candidate
            }
            dir = dir.parentFile
        }

        error("找不到 contracts/transfer-state-machine.json")
    }
}
