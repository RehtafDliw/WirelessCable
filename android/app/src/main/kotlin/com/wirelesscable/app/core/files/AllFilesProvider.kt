package com.wirelesscable.app.core.files

import android.content.Context
import android.net.Uri
import android.os.Environment
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream

/**
 * 整机访问：直接按文件路径读写内部存储。
 *
 * 与 SAF（[SafFileProvider]）的区别是**授权来源不同**：
 * SAF 是用户逐目录挑选、由系统发放 URI 权限；整机是用户去系统设置里打开
 * "所有文件访问权"（`MANAGE_EXTERNAL_STORAGE`），一次性放开全部路径。
 * 两条路并存：手选目录仍然有效，粒度更细。
 *
 * 边界（系统决定，不是我们偷懒）：
 * - `Android/data`、`Android/obb` 即使拿到该权限也进不去，这里提前给出明确错误；
 * - 外部存储可能未挂载，此时返回 STORAGE_UNAVAILABLE 而不是抛异常。
 */
class AllFilesProvider(private val context: Context) : FileProvider {

    companion object {
        private const val TAG = "AllFilesProvider"

        /** 整机根的固定 id。电脑端按它识别这是"整台手机"而不是某个授权目录。 */
        const val ROOT_ID = "phone-all"

        const val PROVIDER_KIND = "all"

        /** 系统不开放的两个子树，进不去也没必要让用户等一次 IO 失败。 */
        private val BLOCKED_PREFIXES = listOf("Android/data", "Android/obb")
    }

    private val base: File
        get() = Environment.getExternalStorageDirectory()

    override val providerKind: String = PROVIDER_KIND

    override suspend fun roots(): List<FileRoot> = listOfNotNull(root())

    override suspend fun list(path: String): FileResult<List<FileEntry>> = withContext(Dispatchers.IO) {
        val dir = resolve(path) ?: return@withContext resolvedFailure(path)
        if (!dir.exists()) return@withContext FileResult.notFound("目录不存在。")
        if (!dir.isDirectory) return@withContext FileResult.pathInvalid("不是目录。")
        if (!dir.canRead()) {
            return@withContext FileResult.permissionDenied("无法读取该目录（可能被系统限制）。")
        }

        val children = try {
            dir.listFiles()
        } catch (ex: SecurityException) {
            Log.w(TAG, "列举被拒 $path", ex)
            null
        } ?: return@withContext FileResult.permissionDenied("无法读取该目录（可能被系统限制）。")

        val entries = children.mapNotNull { child ->
            val relative = child.toRelative()
                // 只上报相对路径可表达的内容；路径本身已在 resolve 里校验过
                ?: return@mapNotNull null

            // Android/data 与 Android/obb 系统封锁，列出来只会变成一个点进去就报错的死项
            if (BLOCKED_PREFIXES.any { relative == it }) {
                return@mapNotNull null
            }

            FileEntry(
                name = child.name,
                relativePath = relative,
                isDirectory = child.isDirectory,
                sizeBytes = if (child.isDirectory) 0 else child.length(),
                modifiedUnixMs = child.lastModified(),
                capabilities = capabilitiesFor(child),
            )
        }

        FileResult.success(entries)
    }

    override suspend fun info(path: String): FileResult<FileEntry?> = withContext(Dispatchers.IO) {
        val file = resolve(path) ?: return@withContext resolvedFailure(path)
        if (!file.exists()) return@withContext FileResult.success(null)

        FileResult.success(
            FileEntry(
                name = file.name,
                relativePath = if (path.isEmpty()) "" else path,
                isDirectory = file.isDirectory,
                sizeBytes = if (file.isDirectory) 0 else file.length(),
                modifiedUnixMs = file.lastModified(),
                capabilities = capabilitiesFor(file),
            ),
        )
    }

    override suspend fun openRead(path: String): FileResult<InputStream> = withContext(Dispatchers.IO) {
        val file = resolve(path) ?: return@withContext resolvedFailure(path)
        if (!file.isFile) return@withContext FileResult.notFound("文件不存在。")

        try {
            FileResult.success(FileInputStream(file) as InputStream)
        } catch (ex: Exception) {
            Log.w(TAG, "打开失败 $path", ex)
            FileResult.permissionDenied("无法读取该文件。")
        }
    }

    override suspend fun uriFor(path: String): FileResult<Uri> = withContext(Dispatchers.IO) {
        val file = resolve(path) ?: return@withContext resolvedFailure(path)
        if (!file.isFile) return@withContext FileResult.notFound("文件不存在。")

        // 整机根给的是真实路径，直接 file:// 交给传输层读
        FileResult.success(Uri.fromFile(file))
    }

    override suspend fun openWrite(path: String, append: Boolean): FileResult<OutputStream> =
        withContext(Dispatchers.IO) {
            val file = resolve(path) ?: return@withContext resolvedFailure(path)

            try {
                file.parentFile?.mkdirs()
                FileResult.success(FileOutputStream(file, append) as OutputStream)
            } catch (ex: Exception) {
                Log.w(TAG, "写入失败 $path", ex)
                FileResult.permissionDenied("无法写入该文件。")
            }
        }

    override suspend fun createDirectory(path: String): FileResult<Unit> = withContext(Dispatchers.IO) {
        val file = resolve(path) ?: return@withContext resolvedFailure(path)
        if (file.exists()) return@withContext FileResult.alreadyExists()

        if (file.mkdirs()) FileResult.success(Unit)
        else FileResult.ioFailure("新建目录失败。")
    }

    override suspend fun createFile(parentPath: String, name: String): FileResult<Uri> =
        withContext(Dispatchers.IO) {
            if (name.isBlank() || name.contains('/')) {
                return@withContext FileResult.pathInvalid("文件名非法。")
            }

            val parent = resolve(parentPath) ?: return@withContext resolvedFailure(parentPath)
            if (!parent.isDirectory) return@withContext FileResult.pathInvalid("目标不是目录。")

            val target = File(parent, name)
            if (target.exists()) return@withContext FileResult.alreadyExists()

            try {
                parent.mkdirs()
                if (!target.createNewFile()) {
                    return@withContext FileResult.ioFailure("新建文件失败。")
                }
                FileResult.success(Uri.fromFile(target))
            } catch (ex: Exception) {
                Log.w(TAG, "新建文件失败 $parentPath/$name", ex)
                FileResult.permissionDenied("无法在该目录新建文件。")
            }
        }

    override suspend fun rename(path: String, newName: String): FileResult<Unit> = withContext(Dispatchers.IO) {
        if (newName.isBlank() || newName.contains('/')) {
            return@withContext FileResult.pathInvalid("名称非法。")
        }

        val source = resolve(path) ?: return@withContext resolvedFailure(path)
        if (!source.exists()) return@withContext FileResult.notFound()

        val target = File(source.parentFile, newName)
        if (target.exists()) return@withContext FileResult.alreadyExists()

        if (source.renameTo(target)) FileResult.success(Unit)
        else FileResult.ioFailure("重命名失败。")
    }

    override suspend fun delete(path: String, recursive: Boolean): FileResult<Unit> = withContext(Dispatchers.IO) {
        val file = resolve(path) ?: return@withContext resolvedFailure(path)
        if (!file.exists()) return@withContext FileResult.notFound()

        // 整机根的"删除"是最高危操作：不允许删根本身，避免一次误点清空存储
        if (file.absolutePath == base.absolutePath) {
            return@withContext FileResult.pathInvalid("不能删除存储根目录。")
        }

        val ok = if (file.isDirectory) file.deleteRecursively() else file.delete()
        if (ok) FileResult.success(Unit) else FileResult.ioFailure("删除失败。")
    }

    override suspend fun move(sourcePath: String, destinationPath: String): FileResult<Unit> =
        withContext(Dispatchers.IO) {
            val source = resolve(sourcePath) ?: return@withContext resolvedFailure(sourcePath)
            val target = resolve(destinationPath) ?: return@withContext resolvedFailure(destinationPath)

            if (!source.exists()) return@withContext FileResult.notFound()
            if (target.exists()) return@withContext FileResult.alreadyExists()

            target.parentFile?.mkdirs()

            if (source.renameTo(target)) FileResult.success(Unit)
            else FileResult.ioFailure("移动失败。")
        }

    /** 整机根一律给全能力；`Android/data` 这类例外在 [resolve] 就被挡掉。 */
    override suspend fun capabilities(path: String?): Int = FileCapabilities.ALL

    override suspend fun exists(path: String): Boolean = withContext(Dispatchers.IO) {
        resolve(path)?.exists() == true
    }

    // ------------------------------------------------------------ 内部

    private fun root(): FileRoot? {
        if (!AllFilesAccess.isGranted(context)) return null
        val dir = base
        if (!dir.exists()) return null

        return FileRoot(
            rootId = ROOT_ID,
            displayName = "内部存储（整机）",
            uri = Uri.parse("file://" + dir.absolutePath),
            capabilities = FileCapabilities.ALL,
        )
    }

    /** 相对路径 → File。越界、绝对路径、被系统封锁的子树一律返回 null。 */
    private fun resolve(path: String): File? {
        val normalized = path.trim().trim('/')

        if (normalized.contains("..") || File(normalized).isAbsolute) {
            return null
        }

        if (BLOCKED_PREFIXES.any { normalized == it || normalized.startsWith("$it/") }) {
            return null
        }

        val target = if (normalized.isEmpty()) base else File(base, normalized)

        // 规范化后再判一次：挡住符号链接跳到存储外
        val canonical = try {
            target.canonicalFile
        } catch (ex: Exception) {
            return null
        }

        val baseCanonical = try {
            base.canonicalFile
        } catch (ex: Exception) {
            return null
        }

        return if (canonical.path == baseCanonical.path || canonical.path.startsWith(baseCanonical.path + File.separator)) {
            canonical
        } else {
            null
        }
    }

    private fun resolvedFailure(path: String): FileResult<Nothing> =
        if (BLOCKED_PREFIXES.any { path.trim('/').startsWith(it) }) {
            FileResult.failure(
                FileStatus.PERMISSION_DENIED,
                "android_data_restricted",
                "系统不允许访问 Android/data 与 Android/obb，即使开启了整机访问。",
            )
        } else {
            FileResult.pathInvalid()
        }

    private fun File.toRelative(): String? {
        val basePath = base.absolutePath
        val full = absolutePath
        if (!full.startsWith(basePath + File.separator)) return null
        return full.substring(basePath.length + 1).replace(File.separatorChar, '/')
    }

    /** 子项的能力：目录能否建/改/删，文件能否覆盖。系统卡住的就如实降级。 */
    private fun capabilitiesFor(file: File): Int {
        var caps = FileCapabilities.LIST_DIRECTORY or FileCapabilities.READ_FILE or
            FileCapabilities.GET_FILE_INFO

        if (file.isDirectory) {
            if (file.canWrite()) {
                caps = caps or FileCapabilities.CREATE_DIRECTORY or FileCapabilities.WRITE_FILE
            }
            if (file.canWrite()) {
                caps = caps or FileCapabilities.RENAME or FileCapabilities.DELETE or FileCapabilities.MOVE
            }
        } else if (file.canWrite()) {
            caps = caps or FileCapabilities.WRITE_FILE or FileCapabilities.RENAME or
                FileCapabilities.DELETE or FileCapabilities.MOVE
        }

        return caps
    }
}

/**
 * "所有文件访问权"的状态与申请入口。
 *
 * 这是特殊权限：代码里授不了，只能把用户送到系统设置页自己打开。
 * 状态变化没有回调，所以每次判断都实时查。
 */
object AllFilesAccess {

    fun isGranted(context: Context): Boolean = try {
        Environment.isExternalStorageManager()
    } catch (ex: Throwable) {
        // Android 10 及以下没有这个 API，按"未授予"处理（那些版本用不着它）
        false
    }
}
