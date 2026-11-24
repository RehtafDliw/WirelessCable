package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.TransferStates
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * 传输状态机契约测试（Android 侧）。
 *
 * 读的是仓库根的 `contracts/transfer-state-machine.json`——**和桌面端
 * `TransferStateMachineContractTests` 同一份文件**。以前两端的
 * `TransferStates.isActive/isTerminal` 是人肉抄写的两份，服务端加一个状态、
 * 手机端忘了跟，只有真机上才看得出（列表分组、按钮显隐全错）。
 *
 * 现在：改契约 → 两端测试同时红 → 逼着你两边一起改。
 */
class TransferStateMachineContractTest {

    private val contract: JsonObject by lazy {
        Json.parseToJsonElement(ContractFile.read().readText()).jsonObject
    }

    private val states: Map<String, JsonObject>
        get() = contract["states"]!!.jsonObject.mapValues { it.value.jsonObject }

    /**
     * 契约里的状态名 → Android 的常量值。
     *
     * 用显式映射而不是直接拿字符串比较：这样契约**新增状态**时这里会缺键，
     * 测试立刻失败，而不是静默忽略一个 Android 根本不认识的状态。
     */
    private val androidConstants = mapOf(
        "waiting" to TransferStates.WAITING,
        "preparing" to TransferStates.PREPARING,
        "transferring" to TransferStates.TRANSFERRING,
        "verifying" to TransferStates.VERIFYING,
        "paused" to TransferStates.PAUSED,
        "interrupted" to TransferStates.INTERRUPTED,
        "completed" to TransferStates.COMPLETED,
        "failed" to TransferStates.FAILED,
        "cancelled" to TransferStates.CANCELLED,
    )

    @Test
    fun `every contract state has an Android constant`() {
        val missing = states.keys - androidConstants.keys
        assertTrue("契约里有 Android 不认识的状态：$missing", missing.isEmpty())

        val extra = androidConstants.keys - states.keys
        assertTrue("Android 有契约里没声明的状态：$extra", extra.isEmpty())
    }

    @Test
    fun `isActive matches the contract`() {
        states.forEach { (state, definition) ->
            val expected = definition["active"]!!.jsonPrimitive.boolean
            val actual = TransferStates.isActive(androidConstants.getValue(state))
            assertEquals("isActive($state)", expected, actual)
        }
    }

    @Test
    fun `isTerminal matches the contract`() {
        states.forEach { (state, definition) ->
            val expected = definition["terminal"]!!.jsonPrimitive.boolean
            val actual = TransferStates.isTerminal(androidConstants.getValue(state))
            assertEquals("isTerminal($state)", expected, actual)
        }
    }

    @Test
    fun `isResumable matches the contract`() {
        states.forEach { (state, definition) ->
            val expected = definition["resumable"]!!.jsonPrimitive.boolean
            val actual = TransferStates.isResumable(androidConstants.getValue(state))
            assertEquals("isResumable($state)", expected, actual)
        }
    }

    /**
     * 契约表对每个动作必须**覆盖全部状态**。
     *
     * 少一格意味着"这个状态下做这个动作会怎样"没有定义——服务端测试也不会跑那一格，
     * 于是漏掉的分支就成了只有真机能发现的坑。
     */
    @Test
    fun `every action covers every state`() {
        val actions = contract["actions"]!!.jsonObject
        assertTrue("契约里没有动作", actions.isNotEmpty())

        actions.forEach { (action, rules) ->
            val covered = rules.jsonObject.keys
            val missing = states.keys - covered
            assertTrue("动作 $action 缺少状态：$missing", missing.isEmpty())
        }
    }

    private object ContractFile {
        fun read(): File {
            var dir: File? = File(System.getProperty("user.dir") ?: ".")
            while (dir != null) {
                val candidate = File(dir, "contracts/transfer-state-machine.json")
                if (candidate.isFile) {
                    return candidate
                }
                dir = dir.parentFile
            }

            error("找不到 contracts/transfer-state-machine.json（契约表是两端共享的，别删）")
        }
    }
}
