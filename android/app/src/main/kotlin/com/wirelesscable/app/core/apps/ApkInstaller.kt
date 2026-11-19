package com.wirelesscable.app.core.apps

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.provider.Settings
import com.wirelesscable.app.core.i18n.Str
import java.io.File

/**
 * 安装收到的一组分体包。
 *
 * 为什么不用 `ACTION_VIEW` 一个个点：分体包（base + split）必须**同一个安装会话**提交，
 * 单独点 `base.apk` 系统会报"解析包时出现问题"。这里统一走 [PackageInstaller]。
 */
object ApkInstaller {

    /** 是否已获得「安装未知应用」。Android 8 起是应用级开关，不是运行时权限。 */
    fun canInstall(context: Context): Boolean =
        context.packageManager.canRequestPackageInstalls()

    /** 没授权时唯一的出口：应用详情页里的那个开关。 */
    fun requestPermission(context: Context) {
        runCatching {
            context.startActivity(
                Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
                    .setData(Uri.parse("package:${context.packageName}"))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
        }
    }

    /**
     * @param parts 按 base 在前、split 在后的顺序；只会用到 `.apk` 文件。
     * @return null 表示已提交（系统还会弹一次确认框），否则是失败原因。
     */
    fun install(context: Context, parts: List<File>): String? {
        val apks = parts.filter { it.isFile && it.length() > 0 }
        if (apks.isEmpty()) {
            return Str.t("没有可安装的文件")
        }

        return runCatching {
            val installer = context.packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
            val sessionId = installer.createSession(params)

            installer.openSession(sessionId).use { session ->
                apks.forEachIndexed { index, file ->
                    session.openWrite("part_$index.apk", 0, file.length()).use { out ->
                        file.inputStream().use { input -> input.copyTo(out, 64 * 1024) }
                        session.fsync(out)
                    }
                }

                val callback = Intent(context, InstallResultReceiver::class.java)
                    .putExtra(InstallResultReceiver.EXTRA_SESSION, sessionId)
                val flags = android.app.PendingIntent.FLAG_UPDATE_CURRENT or
                    android.app.PendingIntent.FLAG_MUTABLE
                val pending = android.app.PendingIntent.getBroadcast(context, sessionId, callback, flags)
                session.commit(pending.intentSender)
            }
            null
        }.getOrElse { it.message ?: it.toString() }
    }
}

/**
 * 安装结果。只在应用进程活着时收得到——够用了：
 * 用户点完确认框回来，我们的界面就在前台。
 */
class InstallResultReceiver : BroadcastReceiver() {

    companion object {
        const val EXTRA_SESSION = "session"

        /** 最近一次安装结果，界面可以拿它显示一行提示（进程内，不落盘）。 */
        @Volatile
        var lastMessage: String? = null
            private set
    }

    override fun onReceive(context: Context, intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, Int.MIN_VALUE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
                confirm?.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                runCatching { context.startActivity(confirm) }
                lastMessage = null
            }

            PackageInstaller.STATUS_SUCCESS -> {
                lastMessage = "安装完成"
            }

            else -> {
                val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                lastMessage = "安装失败：${message ?: "未知原因"}"
            }
        }
    }
}
