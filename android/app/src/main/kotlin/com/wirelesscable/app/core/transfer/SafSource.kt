package com.wirelesscable.app.core.transfer

import android.content.ContentResolver
import android.net.Uri
import android.provider.OpenableColumns
import java.io.File
import java.io.FileInputStream
import java.io.IOException
import java.io.InputStream

/**
 * 上传来源。只做三件事：查显示名与大小、开读流、给出可读的错误。
 *
 * 两种协议都要支持：
 * - `content://`：系统选择器给的 URI，走 ContentResolver；
 * - `file://`：整机访问（AllFilesProvider）列出来的文件，直接开文件流。
 *
 * 只支持前者时，"手机文件"页选中的文件会被判成"无法获取文件大小"而发不出去
 * ——真机踩过一次，错误信息还是引擎给的兜底文案，很难定位。
 */
class SafSource(
    private val resolver: ContentResolver,
    private val uri: Uri,
    /**
     * 覆盖显示名。应用安装包的原文件名全是 `base.apk` / `split_config.xxx.apk`，
     * 直接发过去对面只会看到一堆 base.apk（重名还会被加 (1)(2)），所以传应用时由调用方给名字。
     */
    private val overrideName: String? = null,
) {

    data class Meta(val displayName: String, val sizeBytes: Long)

    /** file:// 时对应的本地文件；content:// 时为 null。 */
    private val localFile: File? = if (uri.scheme == "file") uri.path?.let(::File) else null

    /** 读取显示名与大小。授权失效时抛 [SecurityException]，由调用方转成用户可读提示。 */
    fun meta(): Meta? {
        localFile?.let { file ->
            // 目录不能当文件发；不存在说明刚被删掉，交给上层给出失败态
            if (!file.exists() || file.isDirectory) return null
            return Meta(overrideName ?: file.name, file.length())
        }

        val projection = arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)

        resolver.query(uri, projection, null, null, null)?.use { cursor ->
            if (!cursor.moveToFirst()) {
                return fallback()
            }

            val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)

            val name = if (nameIndex >= 0 && !cursor.isNull(nameIndex)) {
                cursor.getString(nameIndex)
            } else {
                null
            }

            val size = if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) {
                cursor.getLong(sizeIndex)
            } else {
                -1L
            }

            return Meta(overrideName ?: name ?: fallbackName(), size)
        }

        return fallback()
    }

    fun openRead(): InputStream =
        localFile?.let { FileInputStream(it) }
            ?: resolver.openInputStream(uri)
            ?: throw IOException("无法读取该文件，授权可能已失效")

    private fun fallback(): Meta? = Meta(overrideName ?: fallbackName(), -1L)

    private fun fallbackName(): String = uri.lastPathSegment?.substringAfterLast('/') ?: "file"
}

/**
 * 把底层流限制在固定长度内。一个分块一个实例，避免把整个文件读进内存。
 * 底层流由调用方负责关闭。
 */
class BoundedInputStream(
    private val inner: InputStream,
    private var remaining: Long,
) : InputStream() {

    override fun read(): Int {
        if (remaining <= 0) return -1
        val value = inner.read()
        if (value >= 0) remaining--
        return value
    }

    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
        if (remaining <= 0) return -1
        val want = minOf(length.toLong(), remaining).toInt()
        val read = inner.read(buffer, offset, want)
        if (read > 0) remaining -= read
        return read
    }

    override fun available(): Int = minOf(inner.available().toLong(), remaining).toInt()

    override fun close() {
        // 底层流由调用方持有，这里不关闭
    }
}
