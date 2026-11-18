package com.wirelesscable.app.core.files

import android.content.ContentResolver
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.FileNotFoundException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest

/**
 * SAF 文档提供器实现。
 *
 * 现实约束（不能假设所有提供器都支持完整文件管理）：
 * - `DocumentsContract` 的 `COLUMN_FLAGS` 才是真实能力来源，
 *   不同提供器（Downloads / 云盘 / 第三方文件管理器）支持的���作集不一样；
 * - 授权可能被用户在系统设置里撤销，表现为 SecurityException；
 * - 目录可能不存在（SD 卡拔出、文件被外部删除），表现为 FileNotFoundException；
 * - 提供器本身可能异常（IO / RemoteException）。
 *
 * 因此所有操作都先探测能力再执行，不支持就明确报 "operation_unsupported"。
 */
class SafFileProvider(
    private val resolver: ContentResolver,
    private val grantedRoots: List<FileRoot>,
) : FileProvider {

    override val providerKind: String = "saf"

    override suspend fun roots(): List<FileRoot> = grantedRoots

    override suspend fun list(path: String): FileResult<List<FileEntry>> = withContext(Dispatchers.IO) {
        val resolved = resolve(path)
        if (resolved !is ResolveSuccess) {
            return@withContext resolved.toFailure<List<FileEntry>>()
        }

        val root = resolved.root
        val docUri = resolved.docUri

        // 目录自身的标志位决定它能做什么
        val dirFlags = queryFlags(docUri)
        if (dirFlags == null) {
            return@withContext FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )
        }


        val children = ArrayList<FileEntry>()
        try {
            resolver.query(
                buildChildUri(root.uri, resolved.documentId),
                arrayOf(
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_MIME_TYPE,
                    DocumentsContract.Document.COLUMN_SIZE,
                    DocumentsContract.Document.COLUMN_LAST_MODIFIED,
                ),
                null,
                null,
                null,
            )?.use { cursor ->
                val idIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                val nameIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                val mimeIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_MIME_TYPE)
                val sizeIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_SIZE)
                val modifiedIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)

                while (cursor.moveToNext()) {
                    val docId = cursor.getString(idIndex) ?: continue
                    val name = cursor.getString(nameIndex) ?: continue
                    val mime = cursor.getString(mimeIndex)
                    val isDir = mime == DocumentsContract.Document.MIME_TYPE_DIR
                    val size = if (cursor.isNull(sizeIndex)) 0L else cursor.getLong(sizeIndex)
                    val modified = if (cursor.isNull(modifiedIndex)) 0L else cursor.getLong(modifiedIndex)

                    // 条目的能力 = **该条目自身**的能力：
                    // 它能不能被改名、删除、移动。父目录能不能建子项，
                    // 是父目录自己条目的能力——两者不是一回事。
                    val childUri = DocumentsContract.buildDocumentUriUsingTree(
                        root.uri,
                        docId,
                    )
                    val childCaps = capabilitiesOf(childUri)

                    children.add(
                        FileEntry(
                            name = name,
                            relativePath = join(path, name),
                            isDirectory = isDir,
                            sizeBytes = if (isDir) 0L else size,
                            modifiedUnixMs = modified,
                            capabilities = childCaps,
                        ),
                    )
                }
            }
        } catch (ex: SecurityException) {
            return@withContext FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )
        } catch (ex: FileNotFoundException) {
            return@withContext FileResult.notFound("目录不存在，可能已被外部删除。")
        } catch (ex: IllegalArgumentException) {
            return@withContext FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已失效。",
            )
        } catch (ex: IOException) {
            return@withContext FileResult.ioFailure("读取目录失败。")
        }

        FileResult.success(children)
    }

    override suspend fun info(path: String): FileResult<FileEntry?> = withContext(Dispatchers.IO) {
        val resolved = resolve(path)
        if (resolved !is ResolveSuccess) {
            return@withContext resolved.toFailure<FileEntry?>()
        }

        val name = resolved.name
        val docUri = resolved.docUri

        val flags = queryFlags(docUri)
        if (flags == null) {
            return@withContext FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )
        }

        val isDir = queryMime(docUri) == DocumentsContract.Document.MIME_TYPE_DIR

        var size = 0L
        var modified = 0L
        try {
            resolver.query(
                docUri,
                arrayOf(
                    DocumentsContract.Document.COLUMN_SIZE,
                    DocumentsContract.Document.COLUMN_LAST_MODIFIED,
                ),
                null,
                null,
                null,
            )?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val sizeIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_SIZE)
                    val modifiedIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)
                    if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex)
                    if (modifiedIndex >= 0 && !cursor.isNull(modifiedIndex)) modified = cursor.getLong(modifiedIndex)
                }
            }
        } catch (ex: SecurityException) {
            return@withContext FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )
        } catch (ex: FileNotFoundException) {
            return@withContext FileResult.notFound()
        }

        FileResult.success(
            FileEntry(
                name = name,
                relativePath = normalize(path),
                isDirectory = isDir,
                sizeBytes = if (isDir) 0L else size,
                modifiedUnixMs = modified,
            ),
        )
    }

    override suspend fun openRead(path: String): FileResult<InputStream> = withContext(Dispatchers.IO) {
        val resolved = resolve(path)
        if (resolved !is ResolveSuccess) {
            return@withContext resolved.toFailure<InputStream>()
        }

        try {
            val stream = resolver.openInputStream(resolved.docUri)
                ?: return@withContext FileResult.ioFailure("无法读取该文件。")
            FileResult.success(stream)
        } catch (ex: SecurityException) {
            FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )
        } catch (ex: FileNotFoundException) {
            FileResult.notFound("文件不存在，可能已被外部删除。")
        } catch (ex: IOException) {
            FileResult.ioFailure("读取文件失败。")
        }
    }

    override suspend fun uriFor(path: String): FileResult<android.net.Uri> = withContext(Dispatchers.IO) {
        val resolved = resolve(path)
        if (resolved !is ResolveSuccess) {
            return@withContext resolved.toFailure<android.net.Uri>()
        }

        // 带 tree 的文档 URI 才继承用户授的读权限；裸 document URI 在这里读不出来
        FileResult.success(resolved.docUri)
    }

    override suspend fun openWrite(path: String, append: Boolean): FileResult<OutputStream> =
        withContext(Dispatchers.IO) {
            val resolved = resolve(path)
            if (resolved !is ResolveSuccess) {
                return@withContext resolved.toFailure<OutputStream>()
            }

            val caps = capabilitiesOf(resolved.docUri)
            if (caps and FileCapabilities.WRITE_FILE == 0) {
                return@withContext FileResult.unsupported("该文件不支持写入。")
            }

            try {
                // "rwt" 模式覆盖写；追加在 SAF 下不可靠，统一用覆盖写 + 客户端自己按偏移写
                val stream = resolver.openOutputStream(resolved.docUri, "wt")
                    ?: return@withContext FileResult.ioFailure("无法写入该文件。")
                FileResult.success(stream)
            } catch (ex: SecurityException) {
                FileResult.failure(
                    FileStatus.AUTHORIZATION_REVOKED,
                    "authorization_revoked",
                    "该目录的访问授权已被撤销。",
                )
            } catch (ex: FileNotFoundException) {
                FileResult.notFound("文件不存在。")
            } catch (ex: IOException) {
                FileResult.ioFailure("写入文件失败。")
            }
        }

    /**
     * 在指定目录下创建一个空文件，返回它的 URI。
     *
     * 电脑上传文件到手机目录时要用：手机先在 SAF 里建出目标文件，
     * 再把文件内容（从电脑拉来的）写进去。
     */
    override suspend fun createFile(parentPath: String, name: String): FileResult<android.net.Uri> =
        withContext(Dispatchers.IO) {
            if (name.isEmpty()) {
                return@withContext FileResult.pathInvalid("文件名不能为空。")
            }

            val resolved = resolve(parentPath)
            if (resolved !is ResolveSuccess) {
                return@withContext resolved.toFailure<android.net.Uri>()
            }

            val caps = capabilitiesOf(resolved.docUri)
            if (caps and FileCapabilities.WRITE_FILE == 0) {
                return@withContext FileResult.unsupported("该目录不支持写入。")
            }

            try {
                val created = DocumentsContract.createDocument(
                    resolver,
                    resolved.docUri,
                    "application/octet-stream",
                    name,
                ) ?: return@withContext FileResult.ioFailure("新建文件失败。")

                takePersistableIfPossible(created, resolved.root.uri)
                FileResult.success(created)
            } catch (ex: SecurityException) {
                FileResult.failure(
                    FileStatus.AUTHORIZATION_REVOKED,
                    "authorization_revoked",
                    "该目录的访问授权已被撤销。",
                )
            } catch (ex: IOException) {
                FileResult.ioFailure("新建文件失败。")
            }
        }

    override suspend fun createDirectory(path: String): FileResult<Unit> = withContext(Dispatchers.IO) {
        val parentPath = parentOf(path)
        val name = nameOf(path)
        if (name.isEmpty()) {
            return@withContext FileResult.pathInvalid("目录名不能为空。")
        }

        val resolved = resolve(parentPath)
        if (resolved !is ResolveSuccess) {
            return@withContext resolved.toFailure<Unit>()
        }

        val caps = capabilitiesOf(resolved.docUri)
        if (caps and FileCapabilities.CREATE_DIRECTORY == 0) {
            return@withContext FileResult.unsupported("该目录不支持新建子目录。")
        }

        try {
            val created = DocumentsContract.createDocument(
                resolver,
                resolved.docUri,
                DocumentsContract.Document.MIME_TYPE_DIR,
                name,
            ) ?: return@withContext FileResult.ioFailure("新建目录失败。")

            // 新建的目录没有自己的授权标记，父目录的读写权限已覆盖它
            takePersistableIfPossible(created, resolved.root.uri)
            FileResult.success(Unit)
        } catch (ex: SecurityException) {
            FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )
        } catch (ex: IOException) {
            FileResult.ioFailure("新建目录失败。")
        }
    }

    override suspend fun rename(path: String, newName: String): FileResult<Unit> = withContext(Dispatchers.IO) {
        if (newName.isBlank() || newName.contains('/')) {
            return@withContext FileResult.pathInvalid("新名称不合法。")
        }

        val resolved = resolve(path)
        if (resolved !is ResolveSuccess) {
            return@withContext resolved.toFailure<Unit>()
        }

        val caps = capabilitiesOf(resolved.docUri)
        if (caps and FileCapabilities.RENAME == 0) {
            return@withContext FileResult.unsupported("该条目不支持重命名。")
        }

        try {
            DocumentsContract.renameDocument(resolver, resolved.docUri, newName)
            FileResult.success(Unit)
        } catch (ex: SecurityException) {
            FileResult.failure(
                FileStatus.AUTHORIZATION_REVOKED,
                "authorization_revoked",
                "该目录的访问授权已被撤销。",
            )
        } catch (ex: UnsupportedOperationException) {
            FileResult.unsupported("该条目不支持重命名。")
        } catch (ex: IOException) {
            FileResult.ioFailure("重命名失败。")
        }
    }

    override suspend fun delete(path: String, recursive: Boolean): FileResult<Unit> =
        withContext(Dispatchers.IO) {
            val resolved = resolve(path)
            if (resolved !is ResolveSuccess) {
                return@withContext resolved.toFailure<Unit>()
            }

            val caps = capabilitiesOf(resolved.docUri)
            if (caps and FileCapabilities.DELETE == 0) {
                return@withContext FileResult.unsupported("该条目不支持删除。")
            }

            try {
                if (!DocumentsContract.deleteDocument(resolver, resolved.docUri)) {
                    return@withContext FileResult.ioFailure("删除失败。")
                }
                FileResult.success(Unit)
            } catch (ex: SecurityException) {
                FileResult.failure(
                    FileStatus.AUTHORIZATION_REVOKED,
                    "authorization_revoked",
                    "该目录的访问授权已被撤销。",
                )
            } catch (ex: UnsupportedOperationException) {
                FileResult.unsupported("该条目不支持删除。")
            } catch (ex: IOException) {
                FileResult.ioFailure("删除失败。")
            }
        }

    override suspend fun move(sourcePath: String, destinationPath: String): FileResult<Unit> =
        withContext(Dispatchers.IO) {
            val source = resolve(sourcePath)
            if (source !is ResolveSuccess) {
                return@withContext source.toFailure<Unit>()
            }

            val destination = resolve(parentOf(destinationPath))
            if (destination !is ResolveSuccess) {
                return@withContext destination.toFailure<Unit>()
            }

            val caps = capabilitiesOf(destination.docUri)
            if (caps and FileCapabilities.MOVE == 0) {
                return@withContext FileResult.unsupported("该目录不支持移动。")
            }

            val newName = nameOf(destinationPath)
            try {
                val moved = DocumentsContract.moveDocument(
                    resolver,
                    source.docUri,
                    destination.docUri,
                    destination.docUri,
                ) ?: return@withContext FileResult.ioFailure("移动失败。")

                if (newName.isNotEmpty() && newName != nameOf(sourcePath)) {
                    DocumentsContract.renameDocument(resolver, moved, newName)
                }
                FileResult.success(Unit)
            } catch (ex: SecurityException) {
                FileResult.failure(
                    FileStatus.AUTHORIZATION_REVOKED,
                    "authorization_revoked",
                    "该目录的访问授权已被撤销。",
                )
            } catch (ex: UnsupportedOperationException) {
                FileResult.unsupported("该目录不支持移动。")
            } catch (ex: IOException) {
                FileResult.ioFailure("移动失败。")
            }
        }

    override suspend fun capabilities(path: String?): Int {
        val target = path ?: ""
        val resolved = resolve(target)
        if (resolved !is ResolveSuccess) {
            return FileCapabilities.NONE
        }
        return capabilitiesOf(resolved.docUri)
    }

    override suspend fun exists(path: String): Boolean {
        val resolved = resolve(path)
        if (resolved !is ResolveSuccess) {
            return false
        }
        return queryFlags(resolved.docUri) != null
    }

    // ------------------------------------------------------------ 内部

    /**
     * 相对路径 → 文档 URI。路径段按名字逐级在 SAF 树里查找。
     *
     * 路径语义与 C# 端一致：**相对根**。空串表示根本身。
     * 找不到就是 NotFound（文件被外部删除），授权失效是 AuthorizationRevoked。
     */
    private fun resolve(path: String): ResolveOutcome {
        val normalized = normalize(path)
        val segments = normalized.split('/').filter { it.isNotEmpty() }

        if (grantedRoots.isEmpty()) {
            return ResolveFailure(
                FileStatus.PERMISSION_DENIED,
                "permission_denied",
                "没有授权目录。",
            )
        }

        // 空路径表示根本身；否则先看首段是不是根名，是则剥掉，否则整段当作根下相对路径。
        val root: FileRoot
        val relativeSegments: List<String>
        if (segments.isEmpty()) {
            root = grantedRoots.singleOrNull()
                ?: return ResolveFailure(
                    FileStatus.PERMISSION_DENIED,
                    "permission_denied",
                    "该目录未授权。",
                )
            relativeSegments = emptyList()
        } else {
            val matched = grantedRoots.firstOrNull {
                it.displayName == segments[0] || it.rootId == segments[0]
            }
            if (matched != null) {
                root = matched
                relativeSegments = segments.drop(1)
            } else if (grantedRoots.size == 1) {
                root = grantedRoots[0]
                relativeSegments = segments
            } else {
                return ResolveFailure(
                    FileStatus.PERMISSION_DENIED,
                    "permission_denied",
                    "该目录未授权。",
                )
            }
        }

        // 根自身的 documentId 就是它 URI 的最后一段
        var currentDocumentId = DocumentsContract.getTreeDocumentId(root.uri)
        var currentName = root.displayName

        for (segment in relativeSegments) {
            val childId = findChildDocumentId(root.uri, currentDocumentId, segment)
                ?: return ResolveFailure(
                    FileStatus.NOT_FOUND,
                    "file_not_found",
                    "路径不存在：$segment",
                )
            currentDocumentId = childId
            currentName = segment
        }

        val docUri = DocumentsContract.buildDocumentUriUsingTree(root.uri, currentDocumentId)
        return ResolveSuccess(root, docUri, currentDocumentId, currentName)
    }

    /** 在某个目录下按显示名查找子项的 documentId。 */
    private fun findChildDocumentId(rootUri: Uri, parentDocumentId: String, name: String): String? {
        return try {
            resolver.query(
                buildChildUri(rootUri, parentDocumentId),
                arrayOf(
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                    DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                ),
                null,
                null,
                null,
            )?.use { cursor ->
                val idIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DOCUMENT_ID)
                val nameIndex = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
                while (cursor.moveToNext()) {
                    if (cursor.getString(nameIndex) == name) {
                        return@use cursor.getString(idIndex)
                    }
                }
                null
            }
        } catch (ex: SecurityException) {
            null
        } catch (ex: FileNotFoundException) {
            null
        }
    }

    /**
     * 构造子文档查询 URI。
     *
     * `buildChildDocumentsUriUsingTree` 收的是 (treeUri, documentId)：
     * 第一个是 tree URI，第二个是 documentId 字符串——不是 URI，这是最容易踩的一处。
     */
    private fun buildChildUri(rootUri: Uri, parentDocumentId: String): Uri =
        DocumentsContract.buildChildDocumentsUriUsingTree(rootUri, parentDocumentId)

    /** 读一个文档的 FLAG 位。返回 null 表示授权失效或文档不存在。 */
    private fun queryFlags(docUri: Uri): Int? {
        return try {
            resolver.query(
                docUri,
                arrayOf(DocumentsContract.Document.COLUMN_FLAGS),
                null,
                null,
                null,
            )?.use { cursor ->
                if (!cursor.moveToFirst()) {
                    null
                } else {
                    val index = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_FLAGS)
                    if (index >= 0) cursor.getInt(index) else null
                }
            }
        } catch (ex: SecurityException) {
            null
        } catch (ex: FileNotFoundException) {
            null
        }
    }

    private fun queryMime(docUri: Uri): String? {
        return try {
            resolver.query(
                docUri,
                arrayOf(DocumentsContract.Document.COLUMN_MIME_TYPE),
                null,
                null,
                null,
            )?.use { cursor ->
                if (!cursor.moveToFirst()) null else cursor.getString(0)
            }
        } catch (ex: Exception) {
            null
        }
    }

    /**
     * 由 FLAG 位推导真实能力。这是"不能假设所有提供器都支持完整文件管理"的落地处：
     * 每一项都单独判，没标志就是不支持。
     */
    private fun capabilitiesOf(docUri: Uri): Int {
        val flags = queryFlags(docUri) ?: return FileCapabilities.NONE
        val mime = queryMime(docUri)
        val isDirectory = mime == DocumentsContract.Document.MIME_TYPE_DIR

        var caps = FileCapabilities.LIST_DIRECTORY or FileCapabilities.GET_FILE_INFO

        if (isDirectory) {
            // SAF 只给目录一个专属标志（FLAG_DIR_SUPPORTS_CREATE）；
            // 目录条目的删除/重命名/移动能力要看它自己的 FLAG_SUPPORTS_* 位。
            if (flags and DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE != 0) {
                caps = caps or FileCapabilities.CREATE_DIRECTORY or FileCapabilities.WRITE_FILE
            }
            if (flags and DocumentsContract.Document.FLAG_SUPPORTS_DELETE != 0) {
                caps = caps or FileCapabilities.DELETE
            }
            if (flags and DocumentsContract.Document.FLAG_SUPPORTS_RENAME != 0) {
                caps = caps or FileCapabilities.RENAME
            }
            if (flags and DocumentsContract.Document.FLAG_SUPPORTS_MOVE != 0) {
                caps = caps or FileCapabilities.MOVE
            }
        } else {
            caps = caps or FileCapabilities.READ_FILE
            if (flags and DocumentsContract.Document.FLAG_SUPPORTS_WRITE != 0) {
                caps = caps or FileCapabilities.WRITE_FILE
            }
            if (flags and DocumentsContract.Document.FLAG_SUPPORTS_DELETE != 0) {
                caps = caps or FileCapabilities.DELETE
            }
            if (flags and DocumentsContract.Document.FLAG_SUPPORTS_RENAME != 0) {
                caps = caps or FileCapabilities.RENAME
            }
            if (flags and DocumentsContract.Document.FLAG_SUPPORTS_MOVE != 0) {
                caps = caps or FileCapabilities.MOVE
            }
        }

        return caps
    }

    /** 子文档默认继承父目录授权；对新建项再取一次持久化授权尽量保住。 */
    private fun takePersistableIfPossible(docUri: Uri, rootUri: Uri) {
        try {
            resolver.takePersistableUriPermission(
                docUri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
        } catch (ex: SecurityException) {
            // 子文档通常不支持单独持久化，继承父目录即可，忽略
            Log.d(TAG, "子文档无法单独持久化授权：$docUri")
        }
    }

    private sealed interface ResolveOutcome {
        fun <T> toFailure(): FileResult<T>
    }

    private data class ResolveSuccess(
        val root: FileRoot,
        val docUri: Uri,
        val documentId: String,
        val name: String,
    ) : ResolveOutcome {
        override fun <T> toFailure(): FileResult<T> =
            FileResult.failure(FileStatus.IO_FAILURE, "resolve_failed", "内部错误。")
    }

    private data class ResolveFailure(
        val status: FileStatus,
        val reasonCode: String,
        val message: String,
    ) : ResolveOutcome {
        override fun <T> toFailure(): FileResult<T> =
            FileResult.failure(status, reasonCode, message)
    }

    companion object {
        private const val TAG = "SafFileProvider"

        /** 相对路径规范化：'/' 分隔，去掉前后空段与 '.'。 */
        fun normalize(path: String?): String =
            (path ?: "")
                .replace('\\', '/')
                .split('/')
                .filter { it.isNotEmpty() && it != "." }
                .joinToString("/")

        /** 取父路径；根目录的父路径是空串。 */
        fun parentOf(path: String): String {
            val normalized = normalize(path)
            val index = normalized.lastIndexOf('/')
            return if (index <= 0) "" else normalized.substring(0, index)
        }

        /** 取末段名字。 */
        fun nameOf(path: String): String {
            val normalized = normalize(path)
            val index = normalized.lastIndexOf('/')
            return if (index < 0) normalized else normalized.substring(index + 1)
        }

        private fun join(parent: String, name: String): String {
            val normalized = normalize(parent)
            return if (normalized.isEmpty()) name else "$normalized/$name"
        }
    }
}
