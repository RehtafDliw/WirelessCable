package com.wirelesscable.app.core.direct

import android.content.Context
import android.os.Environment
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 直连的诊断日志：同时进 logcat 和**文件**。
 *
 * 为什么一定要写文件：logcat 缓冲区几分钟就被系统日志冲干净了，等用户复现完再抓，
 * 往往只剩最后两行。直连是一次性会话、问题又只在真机上出现，没有日志基本等于瞎猜
 * （1.5.16：用户报"卡在 40 KB 不动"，回头去抓 logcat 已经什么都没有了）。
 *
 * 落点优先级（1.5.54）：
 * 1. `下载/WirelessCable/直连/debug.log` —— 用户能直接翻到，但需要存储权限；
 * 2. `Android/data/<包名>/files/直连/debug.log` —— 应用私有目录，**任何权限下都能写**。
 *
 * 为什么要有第 2 条：三星上一直没有日志，因为公开目录在分区存储/未授权时写不进去，
 * 而 `write()` 用 `runCatching` 把异常吞了 —— 表现为"这台手机压根没产生日志"，
 * 排查全靠猜。现在初始化时先探测公开目录可写，不可写就落到私有目录。
 *
 * 超过 1 MB 自动重来；写在 IO 线程之外也由调用方保证频率（进度类是每秒一行）。
 */
object DirectLog {

    private const val TAG = "WcDirect"
    private const val MAX_BYTES = 1L * 1024 * 1024

    private val stamp = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)

    private var appContext: Context? = null

    /** 在 `Application.onCreate` 里调一次，越早越好。 */
    fun init(context: Context) {
        appContext = context.applicationContext
        file?.let {
            Log.i(TAG, "直连日志落点：${it.absolutePath}")
        }
    }

    /** 当前日志落点（界面/排障用）。 */
    fun path(): String = file?.absolutePath ?: "（不可用）"

    /**
     * 落点文件。拿不到外部存储时返回 null（单元测试、或被裁剪的运行环境），
     * 这时只写 logcat，不写文件——绝不能因为日志把主流程搞崩。
     */
    private val file: File? by lazy { pickFile() }

    private fun pickFile(): File? {
        // 1) 公开目录：用户能直接在文件管理器里翻到（需要存储权限）
        val public = runCatching {
            val root = Environment.getExternalStorageDirectory() ?: return@runCatching null
            val dir = File(root, "Download/WirelessCable/直连")
            if (!dir.exists() && !dir.mkdirs()) {
                return@runCatching null
            }

            // 探测一下真的能写：分区存储下 mkdirs 成功但 write 会 EACCES
            val probe = File(dir, ".probe")
            probe.writeText("ok")
            probe.delete()
            File(dir, "debug.log")
        }.getOrNull()

        if (public != null) {
            return public
        }

        // 2) 应用私有目录：不需要任何权限
        return runCatching {
            appContext?.getExternalFilesDir("直连")?.let { File(it, "debug.log") }
        }.getOrNull()
    }

    fun i(message: String) {
        Log.i(TAG, message)
        write("I", message, null)
    }

    fun w(message: String, error: Throwable? = null) {
        Log.w(TAG, message, error)
        write("W", message, error)
    }

    /** 会话开始时写一行分隔，方便把一次连接从历史里切出来。 */
    fun session(title: String) {
        i("================ $title ================")
    }

    private fun write(level: String, message: String, error: Throwable?) {
        val target = file ?: return

        runCatching {
            val parent = target.parentFile ?: return
            if (!parent.exists() && !parent.mkdirs()) {
                return
            }

            if (target.exists() && target.length() > MAX_BYTES) {
                target.delete()
            }

            val line = buildString {
                append(stamp.format(Date()))
                append(' ')
                append(level)
                append(' ')
                append(message)
                if (error != null) {
                    append(" | ")
                    append(error.javaClass.simpleName)
                    append(": ")
                    append(error.message)
                }
                append('\n')
            }

            target.appendText(line)
        }
    }
}
