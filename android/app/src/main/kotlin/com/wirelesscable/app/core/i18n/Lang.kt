package com.wirelesscable.app.core.i18n

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue

/**
 * 界面语言。
 *
 * `System` 跟随系统（默认），`Zh` / `En` 是用户在设置里显式选的——
 * 手机系统是中文但想用英文界面，或者反过来，都不用改系统语言。
 */
enum class LangMode(val setting: String) {
    System("system"),
    Zh("zh"),
    En("en");

    companion object {
        fun parse(raw: String?): LangMode =
            entries.firstOrNull { it.setting == raw } ?: System
    }
}

/**
 * 语言选择。
 *
 * 用 Compose 的可观察状态：`Str.t()` 在组合里读它，切换后所有用了翻译的界面
 * 会自动重组，不需要重建 Activity（重建会打断正在传的任务）。
 *
 * 存在和主题同一个 SharedPreferences 里，卸载才丢。
 */
object LangState {
    private const val PREFS = "wirelesscable_app"
    private const val KEY = "ui.lang"

    var mode by mutableStateOf(LangMode.System)
        private set

    fun load(context: Context) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        mode = LangMode.parse(prefs.getString(KEY, LangMode.System.setting))
    }

    fun set(context: Context, value: LangMode) {
        mode = value
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY, value.setting)
            .apply()
    }
}
