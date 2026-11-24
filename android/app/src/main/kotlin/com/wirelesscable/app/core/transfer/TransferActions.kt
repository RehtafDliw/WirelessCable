package com.wirelesscable.app.core.transfer

import android.content.Context
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.IOException

/**
 * 能力矩阵（手机端）。
 *
 * 读的是随 APK 一起打包的 `transfer-state-machine.json`——它就是仓库根
 * `contracts/transfer-state-machine.json`（gradle 把那个目录挂进 assets）。
 * 桌面端把它嵌进程序集、前端在构建时 import，**三处同一份**。
 *
 * 以前这些判断散在界面里：`item.isActive` 决定给不给暂停、
 * `paused` 决定给不给继续、底部栏单独判"全都在传输时不给删除"……
 * 改一条语义要翻三四处，漏一处就是"这端能点、那端不能"。
 *
 * 现在统一走这里。两个问题分开：
 *  - [shows]   ：界面上**显不显示**这个按钮（可以比服务端更严格，见 D-060）
 *  - [accepts] ：服务端**接不接受**这个动作（`rejected` 算不接受：批量里这一条被跳过）
 */
object TransferActions {

    const val PAUSE = "pause"
    const val RESUME = "resume"
    const val CANCEL = "cancel"
    const val RETRY = "retry"
    const val DELETE_RECORD = "deleteRecord"

    @Volatile
    private var table: Map<String, Map<String, JsonObject>> = emptyMap()

    private val json = Json { ignoreUnknownKeys = true }

    /** 在 Application.onCreate 里调一次。读不到就让表为空（按钮全不显示，不会误操作）。 */
    fun init(context: Context) {
        if (table.isNotEmpty()) {
            return
        }

        val text = try {
            context.assets.open(FILE).bufferedReader().use { it.readText() }
        } catch (error: IOException) {
            return
        }

        initFromJson(text)
    }

    /**
     * 从契约表文本装配（测试用；生产走 [init] 读 assets）。
     *
     * 抽出来是为了让 JVM 测试能验证**解析与查询逻辑**——
     * assets 只有真机能读，而这里错一格就是按钮到处乱显示。
     */
    internal fun initFromJson(text: String): Boolean {
        table = try {
            json.parseToJsonElement(text).jsonObject["actions"]!!.jsonObject
                .mapValues { (_, rules) ->
                    rules.jsonObject
                        .mapNotNull { (state, rule) ->
                            // 动作里除了状态格还有 note 之类的字符串元数据，只认对象
                            (rule as? JsonObject)?.let { state to it }
                        }
                        .toMap()
                }
        } catch (error: Exception) {
            emptyMap()
        }

        return table.isNotEmpty()
    }

    /** 界面上是否**显示**这个动作。 */
    fun shows(action: String, state: String): Boolean =
        rule(action, state)?.get("ui")?.jsonPrimitive?.booleanOrNull == true

    /** **这一条**服务端接不接受。 */
    fun accepts(action: String, state: String): Boolean =
        rule(action, state)?.get("outcome")?.jsonPrimitive?.contentOrNullSafe() == "ok"

    /**
     * 一批任务能不能做某个动作：**全都**可以才给按钮。
     *
     * 批量删除的语义就是"整批都清掉"。只要有一条还在传，
     * 就不该给这个按钮——点了只会被跳过几条，用户以为删了其实没删。
     */
    fun allShow(action: String, states: Collection<String>): Boolean =
        states.isNotEmpty() && states.all { shows(action, it) }

    /** 一批里是否至少有一条能做某个动作。 */
    fun anyShow(action: String, states: Collection<String>): Boolean =
        states.any { shows(action, it) }

    private fun rule(action: String, state: String): JsonObject? =
        table[action]?.get(state)

    private fun kotlinx.serialization.json.JsonPrimitive.contentOrNullSafe(): String? =
        if (this is kotlinx.serialization.json.JsonNull) null else content

    private const val FILE = "transfer-state-machine.json"
}
