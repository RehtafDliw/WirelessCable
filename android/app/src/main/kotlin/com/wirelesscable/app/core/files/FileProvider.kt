package com.wirelesscable.app.core.files

import android.net.Uri

/**
 * 文件系统条目。与 C# 端 Core.Domain.FileEntry 语义一致。
 *
 * @param relativePath 相对根的路径，'/' 分隔；根目录本身为空串。
 */
data class FileEntry(
    val name: String,
    val relativePath: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
    val modifiedUnixMs: Long,
    /**
     * 该条目所在目录支持的操作能力位。
     *
     * 根目录的能力往往不代表子目录——SAF 的虚拟目录（如 Downloads 的"全部下载"聚合视图）
     * 就不支持新建，但里面的真实目录支持。能力下沉到条目级，
     * 电脑端才能准确判断"这个目录能不能改"。
     */
    val capabilities: Int = FileCapabilities.NONE,
)

/**
 * 能力位。数值必须与 C# 端 Core.Domain.FileProviderCapabilities 完全一致。
 */
object FileCapabilities {
    const val NONE = 0
    const val LIST_DIRECTORY = 1 shl 0
    const val READ_FILE = 1 shl 1
    const val WRITE_FILE = 1 shl 2
    const val CREATE_DIRECTORY = 1 shl 3
    const val RENAME = 1 shl 4
    const val DELETE = 1 shl 5
    const val MOVE = 1 shl 6
    const val GET_FILE_INFO = 1 shl 7
    const val ALL =
        LIST_DIRECTORY or READ_FILE or WRITE_FILE or CREATE_DIRECTORY or RENAME or DELETE or MOVE or GET_FILE_INFO

    /** 所有"会改动内容"的能力位。全为零就是只读根，界面据此灰掉写操作。 */
    const val WRITE_MASK = WRITE_FILE or CREATE_DIRECTORY or RENAME or DELETE or MOVE
}

/** 操作结果状态。失败时给出稳定原因码，供 UI 区分"不支持"与"没权限"。 */
enum class FileStatus {
    OK,
    NOT_FOUND,
    PATH_INVALID,
    ALREADY_EXISTS,
    PERMISSION_DENIED,
    UNSUPPORTED,
    AUTHORIZATION_REVOKED,
    STORAGE_UNAVAILABLE,
    IO_FAILURE,
}

/** 一次文件操作的结果。成功与失败分开建模，避免调用方忘判 null。 */
sealed interface FileResult<out T> {

    /** 成功。 */
    data class Success<T>(val value: T) : FileResult<T>

    /** 失败。reasonCode 是稳定机器码，message 面向用户。 */
    data class Failure(
        val status: FileStatus,
        val reasonCode: String,
        val message: String,
    ) : FileResult<Nothing>

    val ok: Boolean get() = this is Success

    companion object {
        fun <T> success(value: T): FileResult<T> = Success(value)

        fun <T> failure(
            status: FileStatus,
            reasonCode: String,
            message: String,
        ): FileResult<T> = Failure(status, reasonCode, message)

        fun <T> notFound(message: String = "文件或目录不存在。"): FileResult<T> =
            failure(FileStatus.NOT_FOUND, "file_not_found", message)

        fun <T> pathInvalid(message: String = "路径非法。"): FileResult<T> =
            failure(FileStatus.PATH_INVALID, "path_invalid", message)

        fun <T> alreadyExists(message: String = "目标已存在。"): FileResult<T> =
            failure(FileStatus.ALREADY_EXISTS, "already_exists", message)

        fun <T> permissionDenied(message: String = "没有该目录的操作权限。"): FileResult<T> =
            failure(FileStatus.PERMISSION_DENIED, "permission_denied", message)

        fun <T> unsupported(message: String = "该存储提供方不支持此操作。"): FileResult<T> =
            failure(FileStatus.UNSUPPORTED, "operation_unsupported", message)

        fun <T> authorizationRevoked(message: String = "该目录的访问授权已被撤销。"): FileResult<T> =
            failure(FileStatus.AUTHORIZATION_REVOKED, "authorization_revoked", message)

        fun <T> storageUnavailable(message: String = "存储介质不可用。"): FileResult<T> =
            failure(FileStatus.STORAGE_UNAVAILABLE, "storage_unavailable", message)

        fun <T> ioFailure(message: String = "文件操作失败。"): FileResult<T> =
            failure(FileStatus.IO_FAILURE, "io_failure", message)
    }
}

/**
 * 文件系统提供方。Windows 端有同名接口（Core.Abstractions.IFileProvider），
 * 两侧语义保持一致：路径一律相对根，失败一律走 [FileResult] 而不抛异常。
 */
interface FileProvider {
    /** 提供方标识（saf / media），上行索引时告知电脑端。 */
    val providerKind: String

    /** 已授权的根。 */
    suspend fun roots(): List<FileRoot>

    /** 列出目录内容。path 为空表示根。 */
    suspend fun list(path: String): FileResult<List<FileEntry>>

    /** 查询单个条目。 */
    suspend fun info(path: String): FileResult<FileEntry?>

    /** 打开只读流。调用方负责关闭。 */
    suspend fun openRead(path: String): FileResult<java.io.InputStream>

    /**
     * 相对路径 → 可直接读取的定位符。
     *
     * 传输层认的是 URI（SAF 给 content://、整机给 file://），
     * 而文件管理用的是相对路径。要"把手机上的文件发到电脑"就得在这里换一次手，
     * 否则调用方只能自己判断提供器类型，新增根类型时又会漏。
     */
    suspend fun uriFor(path: String): FileResult<android.net.Uri>

    /** 打开写入流。 */
    suspend fun openWrite(path: String, append: Boolean): FileResult<java.io.OutputStream>

    /** 新建目录。 */
    suspend fun createDirectory(path: String): FileResult<Unit>

    /**
     * 新建一个空文件并返回可写入的定位符。
     *
     * 电脑往手机传文件时，手机要先把目标"占位"出来，再把内容拉进来——
     * SAF 返回 content:// URI，整机根返回 file:// URI，由传输层统一处理。
     */
    suspend fun createFile(parentPath: String, name: String): FileResult<android.net.Uri>

    /** 重命名（仅改名，不移动位置）。 */
    suspend fun rename(path: String, newName: String): FileResult<Unit>

    /** 删除。 */
    suspend fun delete(path: String, recursive: Boolean): FileResult<Unit>

    /** 移动。 */
    suspend fun move(sourcePath: String, destinationPath: String): FileResult<Unit>

    /** 真实能力检测。不能假设所有 SAF 提供器都支持全部操作。 */
    suspend fun capabilities(path: String?): Int

    /** 条目是否存在。 */
    suspend fun exists(path: String): Boolean
}

/** 一个已授权的根。 */
data class FileRoot(
    val rootId: String,
    val displayName: String,
    val uri: Uri,
    /** 该根的能力位。 */
    val capabilities: Int,
)
