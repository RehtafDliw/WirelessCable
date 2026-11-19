package com.wirelesscable.app.core.apps

import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import com.wirelesscable.app.core.i18n.Str
import java.io.File

/**
 * 已安装的一个包，可能由 base.apk + 若干 split.apk 组成。
 *
 * 为什么连系统应用一起列：用户要的就是"把这台手机上的东西搬到那台"，
 * 预装应用在应用商店里未必搜得到，只能导出 APK（旧原型 com.nearshare 就是这么做的）。
 * 列出来不代表装得上——系统应用签名是平台密钥，换机安装大概率被系统拒绝，
 * 但这属于"装的时候再说"，不该在列表阶段就藏起来。
 */
data class InstalledApp(
    val label: String,
    val packageName: String,
    val versionName: String?,
    /** base 在前，split 在后；缺一个都装不上。 */
    val parts: List<Part>,
    val isSystem: Boolean,
    /** 有没有桌面图标。系统组件大多没有，但照样有 APK 可导。 */
    val hasLauncher: Boolean,
    val enabled: Boolean = true,
) {
    data class Part(val path: String, val sizeBytes: Long)

    val totalBytes: Long get() = parts.sumOf { it.sizeBytes }

    /** 读不到字节的包不隐藏，但界面上标出来，免得用户点了才发现失败。 */
    val readable: Boolean get() = parts.isNotEmpty() && parts.all { it.sizeBytes > 0L }

    val versionText: String get() = versionName?.takeIf { it.isNotBlank() } ?: Str.t("未知版本")
}

object InstalledApps {

    /**
     * 列出所有已装包。
     *
     * 需要 `QUERY_ALL_PACKAGES`（Android 11 起包可见性默认只给"相关"应用），
     * 没有它这里只能看到自己——而"传预装应用"正是要看到全部。
     */
    fun load(context: Context): List<InstalledApp> {
        val pm = context.packageManager

        // 一定要带 MATCH_DISABLED_COMPONENTS：默认不返回已停用应用的启动项，
        // 结果就是"停用了的应用在列表里消失"（旧原型踩过）。
        val launchable = runCatching {
            pm.queryIntentActivities(
                Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER),
                PackageManager.MATCH_DISABLED_COMPONENTS,
            ).mapNotNull { it.activityInfo?.packageName }.toHashSet()
        }.getOrDefault(emptySet())

        val packages = runCatching {
            pm.getInstalledPackages(
                PackageManager.GET_META_DATA or PackageManager.MATCH_DISABLED_COMPONENTS,
            )
        }.getOrDefault(emptyList())

        val result = ArrayList<InstalledApp>(packages.size)
        for (info in packages) {
            val appInfo = info.applicationInfo ?: continue

            // 预装但当前用户没装（或已被卸载但残留记录）：sourceDir 为空，没有 APK 可导
            if ((appInfo.flags and ApplicationInfo.FLAG_INSTALLED) == 0) continue

            val paths = buildList {
                appInfo.sourceDir?.takeIf { it.isNotBlank() }?.let(::add)
                appInfo.splitSourceDirs?.forEach { if (!it.isNullOrBlank()) add(it) }
            }
            if (paths.isEmpty()) continue

            val parts = paths.map { path ->
                InstalledApp.Part(path, runCatching { File(path).length() }.getOrDefault(0L))
            }

            result += InstalledApp(
                label = runCatching { appInfo.loadLabel(pm).toString() }
                    .getOrDefault(info.packageName),
                packageName = info.packageName,
                versionName = info.versionName,
                parts = parts,
                isSystem = (appInfo.flags and ApplicationInfo.FLAG_SYSTEM) != 0,
                hasLauncher = info.packageName in launchable,
                enabled = runCatching { appInfo.enabled }.getOrDefault(true),
            )
        }

        // 有图标的在前，各自按名称排；系统组件沉到后面（界面上默认还折叠起来）
        return result.sortedWith(
            compareBy({ !it.hasLauncher }, { it.isSystem }, { it.label.lowercase() }),
        )
    }

    /**
     * 分体包的文件名：base 是 `微信.apk`，split 是 `微信-part1.apk`。
     *
     * 名字要能当文件名用：标签里常有空格、斜杠、冒号（比如"QQ 音乐"、"设置/存储"），
     * 直接拼进去会被收件方的路径校验拒掉，或者在 `uniqueTarget` 里出怪名。
     */
    fun partName(label: String, packageName: String, index: Int): String {
        val safe = label
            .replace(Regex("[\\\\/:*?\"<>|\\s]"), "_")
            .trim('_', '.')
            .take(40)
            .ifBlank { packageName.replace('.', '_') }

        return if (index == 0) "$safe.apk" else "$safe-part$index.apk"
    }

    /** 一组分体包属于同一个应用：用包名当分组键（同名不同包不会串）。 */
    fun groupOf(app: InstalledApp): String = app.packageName
}
