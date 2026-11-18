package com.wirelesscable.app.data.security

import android.content.Context
import java.util.UUID

/**
 * 本机（手机）的设备标识。
 *
 * 配对与反向服务必须用同一个值——电脑端是按这个 ID 认设备的，
 * 两处各生成一次就会变成"两台设备"。所以抽出来，只在这里生成。
 */
object LocalDeviceIdentity {

    private const val PREFS = "wirelesscable_app"
    private const val KEY_DEVICE_ID = "device.id"

    fun get(context: Context): String {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val existing = prefs.getString(KEY_DEVICE_ID, null)
        if (!existing.isNullOrEmpty()) return existing

        val created = UUID.randomUUID().toString().replace("-", "")
        prefs.edit().putString(KEY_DEVICE_ID, created).apply()
        return created
    }

    /** 显示名：优先用户设置，否则用机型。 */
    fun displayName(context: Context): String =
        android.os.Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android"
}
