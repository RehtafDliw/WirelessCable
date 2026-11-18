package com.wirelesscable.app.core.files

import android.content.Context
import android.net.Uri
import android.os.Environment
import androidx.core.content.FileProvider
import java.io.File

/**
 * 收件目录：电脑发给手机的文件默认落在这里。
 *
 * 为什么要一个固定目录：手机不再当文件管理器用（那是系统「文件」App 的事），
 * App 里只需要回答"电脑发来了什么"。固定落点才能让「传输目录」页永远是对的，
 * 而不是散落在用户当时浏览到的任意目录里。
 */
object InboxStore {

    /** 相对外部存储根（[AllFilesProvider] 的根，也是整机根的相对路径）。 */
    const val RELATIVE_PATH = "Download/WirelessCable"

    /** 页面标题与空态文案共用，避免两处漂移。 */
    const val DISPLAY_NAME = "传输目录"

    /**
     * 当面直连收到的文件落在这个子目录里。
     *
     * 收件箱要单独列它——不能"递归所有子目录"：用户在 Download/WirelessCable 下
     * 可能放着自己的文件夹（这次真机上就有 `wc8-pc` 这种便携目录），递归进去会列出上千个文件。
     */
    const val DIRECT_SUBDIR = "直连"

    /** 没有"所有文件访问权"时既写不进去也列不出来。 */
    fun isAvailable(context: Context): Boolean = AllFilesAccess.isGranted(context)

    /** 收件目录的绝对路径。 */
    fun directory(): File = File(Environment.getExternalStorageDirectory(), RELATIVE_PATH)

    /** 相对路径 → 绝对文件。 */
    fun fileOf(relativePath: String): File =
        File(Environment.getExternalStorageDirectory(), relativePath)

    /**
     * 在收件目录里占一个不重名的文件（同名自动加「 (2)」）。
     *
     * 电脑发来的文件直接写进来，不再问用户"保存到哪里"——
     * 收件目录是固定的，问一次等于多一步且答案永远相同。
     */
    fun reserve(name: String): File? {
        val directory = directory()
        if (!directory.exists() && !directory.mkdirs()) {
            return null
        }

        val base = name.substringBeforeLast('.', name).ifBlank { "file" }
        val extension = name.substringAfterLast('.', "")

        for (index in 1..999) {
            val candidate = when {
                index == 1 -> name
                extension.isEmpty() -> "$base ($index)"
                else -> "$base ($index).$extension"
            }

            val file = File(directory, candidate)
            try {
                if (file.createNewFile()) {
                    return file
                }
            } catch (ex: Exception) {
                android.util.Log.w("WcInbox", "创建收件文件失败 ${file.name}", ex)
                return null
            }
        }

        return null
    }

    /** 交给系统应用打开用的 content:// URI。 */
    fun uriFor(context: Context, file: File): Uri =
        FileProvider.getUriForFile(context, "${context.packageName}.files", file)

    /**
     * 某个落点是不是收件目录里的文件。
     *
     * 用来分辨"这个下载任务该由自动接收续传"还是"用户手动选过落点、不能动"。
     * 只有 file:// 才可能是收件目录——SAF 的 content:// 一定是用户自己选的。
     */
    fun isInboxUri(stored: String?): Boolean {
        if (stored.isNullOrEmpty() || !stored.startsWith("file:")) return false
        val path = runCatching { Uri.parse(stored).path }.getOrNull() ?: return false
        return path.startsWith("${directory().absolutePath}/")
    }
}
