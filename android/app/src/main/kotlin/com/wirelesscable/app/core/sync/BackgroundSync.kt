package com.wirelesscable.app.core.sync

import android.content.Context
import com.wirelesscable.app.core.service.SessionSyncService
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 「后台保持连接」开关。
 *
 * 默认开启：这套应用的存在意义就是"手机放在桌上，电脑随时能看能改"，
 * 关掉等于回到"必须让 App 停在文件页"的旧状态。代价是一条常驻通知，
 * 用户可以在设置里或直接在通知上关掉。
 */
object BackgroundSync {

    private const val PREFS = "wirelesscable_app"
    private const val KEY_ENABLED = "sessionSync.enabled"

    /**
     * 开关的可观察副本。
     *
     * 通知上的「关闭」也会写这个偏好，界面若只在创建时读一次，
     * 就会出现"服务已经停了、设置里还显示已开启"（真机踩过）。
     * null 表示还没读过磁盘，读一次后一直是具体值。
     */
    private val _enabled = MutableStateFlow<Boolean?>(null)
    val enabled: StateFlow<Boolean?> = _enabled.asStateFlow()

    fun isEnabled(context: Context): Boolean {
        _enabled.value?.let { return it }

        val loaded = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getBoolean(KEY_ENABLED, true)
        _enabled.value = loaded
        return loaded
    }

    /** 用户在设置里拨开关，或在通知上点「关闭」。 */
    fun setEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(KEY_ENABLED, enabled)
            .apply()
        _enabled.value = enabled

        if (enabled) {
            SessionSyncService.start(context)
        } else {
            SessionSyncService.stop(context)
        }
    }

    /**
     * 界面回到前台时对一次账：开着且有会话就确保服务在跑，否则确保它停。
     * 不做这一步会出现"设置里开着、实际没跑"（被系统回收、或配对前就打开了）。
     */
    fun reconcile(context: Context, hasSession: Boolean) {
        if (isEnabled(context) && hasSession) {
            SessionSyncService.start(context)
        } else {
            SessionSyncService.stop(context)
        }
    }
}
