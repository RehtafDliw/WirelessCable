package com.wirelesscable.app.core.direct

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat

/**
 * 「开/连热点」这一族能力需要的权限——**唯一判定入口**。
 *
 * 规则跟旧原型保持一致（1.5.62 改回来）：
 * - Android 13+：只认 `NEARBY_WIFI_DEVICES`（manifest 里定位权限已截止到 32）；
 * - 12 及以下：只认 `ACCESS_FINE_LOCATION`。
 *
 * 为什么不"两个都要/任一个都行"：两个同时声明时，系统会把应用归到"使用定位"那一类，
 * 部分 ROM 于是要求定位总开关打开、或直接走定位分支失败；而 13 上真正管 Wi-Fi 的
 * 就是「附近的设备」这一个权限。分开之后，13+ 上只有一次授权框，也不会再因为
 * 定位开关关着而"明明有权限却连不上"。
 *
 * 这一处收敛之前（1.5.57）的教训：同一件事在三个地方各判一遍、结论还不一样，
 * 于是"只授了附近设备"的机器开关亮着、控制器却说需要定位。
 */
object HotspotPermissions {

    /** 13 起才有「附近的设备」这个权限。 */
    val hasNearby: Boolean get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU

    /** 一次申请只带当前系统真正需要的那一个。 */
    fun requestArray(): Array<String> = arrayOf(
        if (hasNearby) {
            Manifest.permission.NEARBY_WIFI_DEVICES
        } else {
            Manifest.permission.ACCESS_FINE_LOCATION
        },
    )

    fun granted(context: Context, permission: String): Boolean =
        ContextCompat.checkSelfPermission(context, permission) ==
            android.content.pm.PackageManager.PERMISSION_GRANTED

    /** 现在这点授权够不够开/连热点。 */
    fun canUseHotspot(context: Context): Boolean = if (hasNearby) {
        granted(context, Manifest.permission.NEARBY_WIFI_DEVICES)
    } else {
        granted(context, Manifest.permission.ACCESS_FINE_LOCATION)
    }

    /** 缺什么，给用户看的一句话；已经够用时返回 null。 */
    fun missingText(context: Context): String? = when {
        canUseHotspot(context) -> null
        hasNearby -> "「附近的设备」没有授权"
        coarseOnly(context) -> "只给了「大致位置」，热点需要精确位置"
        else -> "「定位」没有授权"
    }

    /**
     * 只给了"大致位置"（仅 Android 12 及以下）。
     *
     * 12 起授权框里多了「大致/精确」两档，用户很容易点成大致；
     * 这时 FINE 是拒绝、COARSE 是允许，而热点/Wi-Fi 直连要的是 FINE。
     */
    private fun coarseOnly(context: Context): Boolean =
        !hasNearby &&
            !granted(context, Manifest.permission.ACCESS_FINE_LOCATION) &&
            granted(context, Manifest.permission.ACCESS_COARSE_LOCATION)

    /**
     * 一行诊断信息，写进直连日志。
     *
     * 下次"没弹框"时有据可查：是权限早就有了、还是被拒绝且不再询问。
     */
    fun describe(context: Context): String = buildString {
        append("sdk=").append(Build.VERSION.SDK_INT)
        append(" model=").append(Build.MODEL)
        if (hasNearby) {
            append(" nearby=")
                .append(if (granted(context, Manifest.permission.NEARBY_WIFI_DEVICES)) 1 else 0)
        } else {
            append(" fine=")
                .append(if (granted(context, Manifest.permission.ACCESS_FINE_LOCATION)) 1 else 0)
            append(" coarse=")
                .append(if (granted(context, Manifest.permission.ACCESS_COARSE_LOCATION)) 1 else 0)
        }
        append(" canUse=").append(if (canUseHotspot(context)) 1 else 0)
        append(" rationale=").append(rationaleFlags(context))
    }

    /**
     * 还值不值得再弹一次系统框。
     *
     * `shouldShowRequestPermissionRationale` 在"从没问过"和"拒绝且不再询问"两种情况下
     * 都是 false，所以这个值只在**申请过一次之后**才有判别力。
     */
    fun canAskAgain(context: Context): Boolean {
        val activity = context.findActivity() ?: return false
        return requestArray().any { permission ->
            !granted(context, permission) &&
                ActivityCompat.shouldShowRequestPermissionRationale(activity, permission)
        }
    }

    private fun rationaleFlags(context: Context): String {
        val activity = context.findActivity() ?: return "?"
        return requestArray().joinToString("/") { permission ->
            if (ActivityCompat.shouldShowRequestPermissionRationale(activity, permission)) "1" else "0"
        }
    }

    /** 系统不再弹框时的唯一出口：应用详情页 → 权限。 */
    fun openAppSettings(context: Context) {
        openAppDetails(context)
    }
}

/**
 * 打开本应用的系统详情页（权限、通知、存储都在这一页里改）。
 *
 * 放在这里是因为所有"系统不再弹授权框"的出口都是同一句话：
 * 拒绝过两次之后只能引导用户自己去开。
 */
fun openAppDetails(context: Context) {
    runCatching {
        context.startActivity(
            Intent(
                Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.fromParts("package", context.packageName, null),
            ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
    }.onFailure {
        DirectLog.w("打开应用设置失败：${it.message}")
    }
}

/** Compose 里的 LocalContext 常常是 ContextWrapper，要一层层剥到 Activity。 */
internal fun Context.findActivity(): Activity? {
    var current: Context = this
    while (current is ContextWrapper) {
        if (current is Activity) return current
        current = current.baseContext
    }
    return current as? Activity
}
