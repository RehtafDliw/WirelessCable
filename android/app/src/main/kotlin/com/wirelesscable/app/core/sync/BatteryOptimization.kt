package com.wirelesscable.app.core.sync

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import android.util.Log
import java.util.Locale

/**
 * 电池优化白名单 + 厂商的「耗电管理」入口。
 *
 * 为什么必须处理：息屏后系统会冻结后台应用（国产 ROM 更激进）。被冻结时
 * 前台服务还在、通知还在、`pidof` 也有，但会话循环不再执行——
 * 电脑端看到的是「手机在线，却列不出文件、点了也没反应」。
 * 2026-11-14 现场踩到：**只开服务、不加白名单，息屏后等于没开**。
 *
 * 1.5.6 真机（一加 / ColorOS 13）又踩到第二层：Android 的电池优化白名单已经放行，
 * 前台服务持着 `PARTIAL_WAKE_LOCK` 与 `WIFI_MODE_FULL_LOW_LATENCY`，息屏一分钟后
 * 循环照样停——真正的闸门是厂商自己的「后台不限制 / 允许后台高耗电」。
 * 把它设成"不限制"之后，全程息屏收 1 GB 也没问题（K89）。
 *
 * 这一层**没有公开 API**：读不到当前值，也没有统一的跳转 action。所以：
 * - 状态只能诚实地说"已允许（系统白名单）"，厂商那层让用户自己确认；
 * - 入口按厂商试一组显式组件，全部打不开再退回应用详情页、最后退回系统电池优化列表。
 *   组件名会随 ROM 版本变，只认一个必然在某个版本上失效。
 */
object BatteryOptimization {

    private const val TAG = "WcBattery"

    /** 是否已被系统电池优化豁免。读不到 PowerManager 时按"已豁免"处理，宁可少提示也不要误报。 */
    fun isIgnoring(context: Context): Boolean {
        val manager = context.getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return true
        return runCatching { manager.isIgnoringBatteryOptimizations(context.packageName) }
            .getOrDefault(true)
    }

    /** 请求系统白名单豁免。返回 false 表示入口都打不开（极少见），调用方应给出文字说明。 */
    fun request(context: Context): Boolean {
        val packageUri = Uri.parse("package:${context.packageName}")

        if (start(context, Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, packageUri))) {
            return true
        }

        return start(context, Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, packageUri))
    }

    /**
     * 点了「耗电管理」之后实际落到哪里。
     *
     * 各家 ROM 能开放的层级差别很大（ColorOS 只给到应用信息页），
     * 界面必须按真实落点给下一步，不能一律说"已经打开了"。
     */
    enum class PowerDestination {
        /** 厂商自己的耗电/后台管理页——直接就是目标。 */
        Vendor,

        /** 应用详情页：里面还有「耗电管理」入口，要再点一层。 */
        AppDetails,

        /** 系统的电池优化列表：要在列表里找到本应用。 */
        SystemList,

        /** 一个都打不开。 */
        None,
    }

    /**
     * 打开厂商的「应用耗电管理」。
     *
     * 顺序：厂商专用页 → 应用详情页（本应用，里面通常有「耗电管理」）→ 系统电池优化列表。
     * 返回值告诉调用方落在哪一层，文案据此说下一步；[PowerDestination.None] 时必须给文字指引。
     */
    fun openPowerSettings(context: Context): PowerDestination {
        for (intent in vendorPowerIntents(context)) {
            val component = intent.component?.flattenToShortString()
            if (start(context, intent)) {
                Log.i(TAG, "已打开厂商耗电管理 $component")
                return PowerDestination.Vendor
            }
            Log.d(TAG, "组件打不开，试下一个 $component")
        }

        val packageUri = Uri.parse("package:${context.packageName}")
        if (start(context, Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, packageUri))) {
            Log.i(TAG, "厂商页面不可用，退回应用详情页")
            return PowerDestination.AppDetails
        }

        if (start(context, Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))) {
            Log.i(TAG, "厂商页面不可用，退回系统电池优化列表")
            return PowerDestination.SystemList
        }

        Log.w(TAG, "耗电管理入口全部打不开")
        return PowerDestination.None
    }

    /**
     * 厂商「耗电/后台」页面候选，**按厂商优先**，最后附通用兜底。
     *
     * 每个候选都带 `FLAG_ACTIVITY_NEW_TASK` 与包名 extra：小米的省电策略页靠
     * `package_name` 直接定位到本应用；其余厂商多出来的 extra 会被忽略，无害。
     */
    private fun vendorPowerIntents(context: Context): List<Intent> {
        val manufacturer = Build.MANUFACTURER.lowercase(Locale.ROOT)
        val brand = Build.BRAND.lowercase(Locale.ROOT)
        val label = appLabel(context)

        val candidates: List<ComponentName> = when {
            // 小米 / Redmi / POCO：省电策略（"无限制"就在这里）
            hasAny(manufacturer, brand, "xiaomi", "redmi", "poco") -> listOf(
                ComponentName("com.miui.powerkeeper", "com.miui.powerkeeper.ui.HiddenAppsConfigActivity"),
                ComponentName("com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity"),
            )

            // 华为 / 荣耀：受保护应用（EMUI 10+ 部分版本改为 HwPowerManagerActivity）
            hasAny(manufacturer, brand, "huawei", "honor") -> listOf(
                ComponentName("com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity"),
                ComponentName("com.huawei.systemmanager", "com.huawei.systemmanager.power.ui.HwPowerManagerActivity"),
            )

            // OPPO / 一加 / realme（ColorOS / OxygenOS / realme UI）
            //
            // 实测（一加 / ColorOS 13 / Android 13）：`com.oplus.battery` 的
            // PowerUsageModelActivity 虽然 exported=true，但带
            // `permission=oplus.permission.OPLUS_COMPONENT_SAFE`（系统签名权限），
            // 第三方启动会直接被拒：
            //   Permission Denial: ... not exported from uid 1000
            // `com.coloros.oppoguardelf` / `com.coloros.safecenter` 这些老包在新机上
            // 根本不存在（Error type 3）。所以这一支**故意留空**，交给下面的应用详情页——
            // 那个页面里有「耗电管理」入口，点一下就是目标页（两步，但一定能走通）。
            hasAny(manufacturer, brand, "oppo", "oneplus", "realme") -> emptyList()

            // vivo / iQOO：后台高耗电 / 自启动
            hasAny(manufacturer, brand, "vivo", "iqoo") -> listOf(
                ComponentName("com.vivo.permissionmanager", "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"),
                ComponentName("com.iqoo.secure", "com.iqoo.secure.ui.phoneoptimize.AddWhiteListActivity"),
            )

            // 三星：电池用量（One UI 的"后台使用限制"在应用详情里）
            hasAny(manufacturer, brand, "samsung") -> listOf(
                ComponentName("com.samsung.android.lool", "com.samsung.android.sm.ui.battery.BatteryActivity"),
                ComponentName("com.samsung.android.sm", "com.samsung.android.sm.ui.battery.BatteryActivity"),
            )

            // 魅族：后台管理
            hasAny(manufacturer, brand, "meizu") -> listOf(
                ComponentName("com.meizu.safe", "com.meizu.safe.permission.SmartBGActivity"),
            )

            else -> emptyList()
        }

        return candidates.map { component ->
            Intent().apply {
                this.component = component
                // 小米认这两个；别的 ROM 忽略
                putExtra("package_name", context.packageName)
                putExtra("package_label", label)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
        }
    }

    private fun hasAny(value: String, brand: String, vararg keys: String): Boolean =
        keys.any { value.contains(it) || brand.contains(it) }

    private fun appLabel(context: Context): String = runCatching {
        val manager = context.packageManager
        manager.getApplicationLabel(manager.getApplicationInfo(context.packageName, 0)).toString()
    }.getOrDefault("WirelessCable")

    private fun start(context: Context, intent: Intent): Boolean = try {
        context.startActivity(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        true
    } catch (error: Exception) {
        // 组件不存在（ActivityNotFoundException）、未导出（SecurityException）、
        // 或 ROM 拦了后台启动 Activity（ColorOS 的"关联启动"限制）都会走到这里。
        Log.w(TAG, "打不开 action=${intent.action} component=${intent.component?.flattenToShortString()}", error)
        false
    }
}
