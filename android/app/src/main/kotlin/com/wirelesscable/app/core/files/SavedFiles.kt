package com.wirelesscable.app.core.files

import android.content.ContentResolver
import android.net.Uri
import android.provider.DocumentsContract
import android.util.Log
import java.io.File

/**
 * 删除本机已经落盘的文件。
 *
 * SAF 的 document URI **必须**走 [DocumentsContract.deleteDocument]：
 * [ContentResolver.delete] 打到 DocumentsProvider 上时，没实现 delete 的提供方会抛
 * UnsupportedOperationException，实现了的也常按"没有权限"返回 0。
 * 以前取消传输时只调 `resolver.delete(...) > 0`，结果是勾了「同时删除已完成的文件」
 * 一个都没删掉、界面还报"已取消 N 个任务"（用户看到的正是"实际没有删除"）。
 *
 * 这里按可靠性从高到低试，全部失败才返回 false —— 调用方必须把失败数报给用户，
 * 不能静默。
 */
object SavedFiles {
    private const val TAG = "SavedFiles"

    fun delete(resolver: ContentResolver, stored: String?): Boolean {
        if (stored.isNullOrBlank()) return false

        val uri = runCatching { Uri.parse(stored) }.getOrNull() ?: return false

        // 整机访问/应用私有目录给的是 file://，ContentResolver 对它反而不可靠
        if (uri.scheme == "file") {
            val path = uri.path ?: return false
            val file = File(path)
            return runCatching { !file.exists() || file.delete() }.getOrDefault(false)
        }

        // 1) SAF 的正规删除方式
        val deleted = runCatching { DocumentsContract.deleteDocument(resolver, uri) }.getOrDefault(false)
        if (deleted) return true

        // 2) 有些提供方只实现了 ContentProvider.delete
        val rows = runCatching { resolver.delete(uri, null, null) }.getOrDefault(0)
        if (rows > 0) return true

        Log.w(TAG, "删除文件失败: $stored")
        return false
    }

    /**
     * 一次传输落到的那个**顶层文件夹**。
     *
     * [childUri] 是任意一个落盘文件的 document URI（带 tree 段），
     * [docId] 是顶层文件夹的 document id。两者配合就能构造出目录 URI。
     */
    data class BatchFolder(val childUri: Uri, val docId: String)

    /**
     * 从"文件 URI + 落点相对路径"反推这批文件的顶层文件夹。
     *
     * 例：文件 `primary:Download/WC/(2014) Maroon 5/01.flac`，
     * relativePath = `(2014) Maroon 5/01.flac` → 顶层 = `primary:Download/WC/(2014) Maroon 5`。
     *
     * 返回 null 表示**不能整删**，调用方应退回逐文件删除：
     * - 文件直接落在用户选的目录里（relativePath 只有文件名，没有独立文件夹）；
     * - 段数对不上、目录名与相对路径首段不一致（数据异常，宁可少删）；
     * - file:// 落点（整机访问模式，路径语义与 SAF 不同）。
     */
    fun batchFolderOf(stored: String?, relativePath: String): BatchFolder? {
        if (stored.isNullOrBlank()) return null
        val uri = runCatching { Uri.parse(stored) }.getOrNull() ?: return null
        if (uri.scheme == "file") return null

        val rel = relativePath.split('/').filter { it.isNotBlank() }
        if (rel.size < 2) return null

        val docId = runCatching { DocumentsContract.getDocumentId(uri) }.getOrNull() ?: return null
        val segments = docId.split('/')
        // 文件名占 1 段；relativePath 里除首段（文件夹名）外的都是子目录，一起切掉
        val drop = rel.size - 1
        if (segments.size <= drop + 1) return null

        val top = segments.dropLast(drop).joinToString("/")
        // 目录名必须与相对路径首段逐字一致，否则说明落点不是这条记录推出来的那个文件夹
        if (top.substringAfterLast('/') != rel.first()) return null

        return BatchFolder(uri, top)
    }

    /** 递归删掉整个文件夹（ExternalStorageProvider 的 deleteDocument 对目录是递归的）。 */
    fun deleteTree(resolver: ContentResolver, folder: BatchFolder): Boolean {
        val dirUri = runCatching {
            DocumentsContract.buildDocumentUriUsingTree(folder.childUri, folder.docId)
        }.getOrNull() ?: return false

        val deleted = runCatching { DocumentsContract.deleteDocument(resolver, dirUri) }.getOrDefault(false)
        if (!deleted) {
            Log.w(TAG, "删除文件夹失败: ${folder.docId}")
        }
        return deleted
    }
}
