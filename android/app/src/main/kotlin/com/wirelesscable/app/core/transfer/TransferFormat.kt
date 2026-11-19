package com.wirelesscable.app.core.transfer

import com.wirelesscable.app.core.protocol.TransferStates
import java.util.Locale
import com.wirelesscable.app.core.i18n.Str

/**
 * 传输界面的纯函数格式化。
 *
 * 不依赖 Android、不依赖 Compose，便于用 JVM 单测钉住行为：
 * 展示给用户的每一个数字都必须能追溯到真实字节数，所以这里只做换算，不做任何插值或估算。
 */
object TransferFormat {

    private const val KB = 1024.0
    private const val MB = KB * 1024
    private const val GB = MB * 1024

    /** 剩余时间估算上限（24 小时）。超过它说明速度已不可信。 */
    private const val MAX_REMAINING_SECONDS = 24 * 3600L

    /** 字节数。小数位数随量级递减，避免 1.00 GB 这种噪声。 */
    fun bytes(value: Long): String {
        if (value < 0) return "—"
        val size = value.toDouble()
        return when {
            size < KB -> "$value B"
            size < MB -> String.format(Locale.US, "%.1f KB", size / KB)
            size < GB -> String.format(Locale.US, "%.1f MB", size / MB)
            else -> String.format(Locale.US, "%.2f GB", size / GB)
        }
    }

    /** 速度。0 或负数表示还没有可信采样，显示占位而不是编一个数字。 */
    fun speed(bytesPerSecond: Long): String =
        if (bytesPerSecond <= 0L) "—" else "${bytes(bytesPerSecond)}/s"

    /** 完成百分比，四舍五入到整数；总大小为 0 时按状态给 0 或 100。 */
    fun percent(transferred: Long, total: Long, completed: Boolean = false): Int {
        if (total <= 0L) return if (completed) 100 else 0
        val ratio = transferred.toDouble() / total
        return (ratio * 100).toInt().coerceIn(0, 100)
    }

    /**
     * 剩余时间。
     *
     * 只有拿到正的速度才给结论；速度未知时返回 null，由界面隐藏这一项——
     * 编一个"剩余 1 分钟"比不显示更糟。
     */
    fun remaining(transferred: Long, total: Long, bytesPerSecond: Long): String? {
        if (bytesPerSecond <= 0L || total <= 0L || transferred >= total) return null

        val seconds = ((total - transferred) / bytesPerSecond.toDouble()).toLong().coerceAtLeast(1)

        // 估算超过一天说明速度已经不可信（例如链路断了、速度在衰减），不显示比乱显示强。
        if (seconds > MAX_REMAINING_SECONDS) return null

        return when {
            seconds < 60 -> Str.t("剩余 $seconds 秒")
            seconds < 3600 -> Str.t("剩余 ${seconds / 60} 分 ${seconds % 60} 秒")
            else -> Str.t("剩余 ${seconds / 3600} 时 ${(seconds % 3600) / 60} 分")
        }
    }

    /** 状态文案。与 TransferStates 一一对应，未知状态原样显示而不是吞掉。 */
    fun stateLabel(state: String): String = when (state) {
        TransferStates.WAITING -> Str.t("等待中")
        TransferStates.PREPARING -> Str.t("准备中")
        TransferStates.TRANSFERRING -> Str.t("传输中")
        TransferStates.VERIFYING -> Str.t("校验中")
        TransferStates.COMPLETED -> Str.t("已完成")
        TransferStates.FAILED -> Str.t("失败")
        TransferStates.CANCELLED -> Str.t("已取消")
        TransferStates.INTERRUPTED -> Str.t("已中断")
        TransferStates.PAUSED -> Str.t("已暂停")
        else -> state
    }

    /** 传输中的明细行：已传 / 总 · 完成率 · 速度 · 剩余。空项自动省略。 */
    fun detail(
        transferred: Long,
        total: Long,
        bytesPerSecond: Long,
        completed: Boolean = false,
    ): String {
        val parts = mutableListOf(
            "${bytes(transferred)} / ${bytes(total)}",
            "${percent(transferred, total, completed)}%",
        )
        if (!completed) {
            if (bytesPerSecond > 0L) parts += speed(bytesPerSecond)
            remaining(transferred, total, bytesPerSecond)?.let { parts += it }
        }
        return parts.joinToString(" · ")
    }
}
