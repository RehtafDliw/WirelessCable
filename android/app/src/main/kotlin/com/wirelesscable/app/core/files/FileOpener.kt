package com.wirelesscable.app.core.files

import android.content.Context
import android.content.Intent
import android.util.Log
import android.webkit.MimeTypeMap
import java.io.File

/**
 * 用系统里能打开它的应用打开一个本地文件。
 *
 * 两处要用：收件箱列表（点一行）、直连页收到的文件（点一行）。
 * 以前这套 MIME + FileProvider + FLAG_GRANT_READ 的逻辑只写在 InboxViewModel 里，
 * 直连页想加"点开"就得复制一遍——复制出来的两份必然漂移。
 */
object FileOpener {

    /** @return 打不开时的原因；null 表示已经交给系统了。 */
    fun open(context: Context, file: File): String? {
        if (!file.exists()) {
            return "文件已不存在，可能被清理了"
        }

        if (file.isDirectory) {
            return "这是一个目录"
        }

        val mime = MimeTypeMap.getSingleton()
            .getMimeTypeFromExtension(file.extension.lowercase())
            ?: "*/*"

        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(InboxStore.uriFor(context, file), mime)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }

        return try {
            context.startActivity(intent)
            null
        } catch (ex: Exception) {
            Log.w("WcOpen", "没有能打开该文件的应用：${file.name}", ex)
            "没有能打开「${file.name}」的应用"
        }
    }
}
