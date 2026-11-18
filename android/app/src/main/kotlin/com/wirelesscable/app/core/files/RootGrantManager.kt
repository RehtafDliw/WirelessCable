package com.wirelesscable.app.core.files

import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * 已授权根的持久化记录。
 *
 * rootId 用 URI 的 hash 而不是 URI 本身——服务端不存 URI（URI 只在手机本地有意义），
 * 但根 id 必须稳定，重启后还能对上号。
 */
data class GrantedRoot(
    val rootId: String,
    val displayName: String,
    val uriString: String,
    val capabilities: Int,
    /**
     * 这份授权给哪台电脑看。
     *
     * 空串表示"所有已配对电脑"——1.4.x 及更早的记录没有这个字段，
     * 直接按空处理，升级后行为不变；新授权的都会绑定到具体设备。
     */
    val deviceId: String = "",
)

/**
 * 授权目录管理。
 *
 * 职责：
 * - 发起 ACTION_OPEN_DOCUMENT_TREE 让用户选目录；
 * - 用 `takePersistableUriPermission` 保住授权（否则重启 App 就没了）；
 * - 记录已授权根，供 SAF 提供器使用；
 * - 处理授权被撤销（用户在系统设置里撤销）——此时把根标记为失效并从列表移除。
 */
class RootGrantManager(
    private val context: Context,
    private val resolver: ContentResolver,
) {

    companion object {
        private const val TAG = "RootGrantManager"
        private const val PREFS = "wc_roots"
        private const val KEY_ROOTS = "roots"

        /** 字段分隔符。用控制字符，避免与目录名里的普通字符冲突。 */
        private const val SEPARATOR = "\u001F"

        /** 解析不出名字时的兜底展示名。多处共用，避免文案漂移。 */
        const val GENERIC_NAME = "授权目录"

        /** 目录选择器的 Intent。用户选中后回传 tree URI。 */
        fun pickIntent(): Intent =
            Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
                addFlags(
                    Intent.FLAG_GRANT_READ_URI_PERMISSION or
                        Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                        Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION,
                )
            }
    }

    private val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /** 探测出来的删除/重命名能力（提供器不给 flags 时靠实测得知）。 */
    private var probeDelete = false
    private var probeRename = false

    /** 读取已保存的根（含已失效的，失效的会被过滤）。 */
    fun savedRoots(): List<GrantedRoot> {
        val raw = prefs.getStringSet(KEY_ROOTS, emptySet()).orEmpty()
        return raw.mapNotNull { entry ->
            val parts = entry.split(SEPARATOR)
            if (parts.size < 4) {
                null
            } else {
                GrantedRoot(
                    rootId = parts[0],
                    displayName = parts[1],
                    uriString = parts[2],
                    capabilities = parts[3].toIntOrNull() ?: FileCapabilities.NONE,
                    deviceId = parts.getOrNull(4).orEmpty(),
                )
            }
        }
    }

    /**
     * 处理用户选目录的结果。调用方需要自己从 Intent 里取 URI。
     */
    fun onDirectoryPicked(uri: Uri, deviceId: String = ""): GrantedRoot? {
        // 只接受 tree URI
        if (!DocumentsContract.isTreeUri(uri)) {
            Log.w(TAG, "选中的不是目录树 URI：$uri")
            return null
        }

        // 必须显式取持久化授权，否则 App 重启后目录就消失了
        try {
            resolver.takePersistableUriPermission(
                uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
        } catch (ex: SecurityException) {
            Log.w(TAG, "无法取得持久化授权：$uri", ex)
            return null
        }

        val displayName = resolveDisplayName(uri) ?: GENERIC_NAME
        val rootId = rootIdFor(uri)
        val capabilities = queryCapabilities(uri)

        Log.i(TAG, "已授权目录 name=$displayName rootId=$rootId caps=$capabilities")

        val record = GrantedRoot(rootId, displayName, uri.toString(), capabilities, deviceId)
        persist(listOf(record) + savedRoots().filter { it.rootId != rootId })
        return record
    }

    /** 移除一个根，并释放它的持久化授权。 */
    fun revoke(rootId: String) {
        val root = savedRoots().firstOrNull { it.rootId == rootId } ?: return
        try {
            resolver.releasePersistableUriPermission(
                Uri.parse(root.uriString),
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION,
            )
        } catch (ex: SecurityException) {
            Log.d(TAG, "释放授权失败（可能已被系统回收）：$rootId")
        }
        persist(savedRoots().filter { it.rootId != rootId })
    }

    /**
     * 返回当前仍然有效的根。授权被系统撤销的根会被剔除——
     * 这正是"权限被用户撤销"的处理点：不是报错，而是从列表里消失。
     */
    /// <param name="refreshCapabilities">
    /// false 时直接用保存的能力位，不再向提供器查询（目录列表页 3 秒轮询一次，受不了每次探测——
    /// 标志位为 0 的提供器（Downloads）会**建一个临时目录**再删掉）。
    /// 索引上报（12 秒一轮）仍然走 true，能力位以那次为准。
    /// </param>
    suspend fun activeRoots(
        deviceId: String? = null,
        refreshCapabilities: Boolean = true,
    ): List<FileRoot> = withContext(Dispatchers.IO) {
        val persisted = resolver.persistedUriPermissions
        val allowed = persisted.map { it.uri }.toSet()

        val valid = savedRoots().filter { root ->
            // 按设备过滤：空 deviceId 是升级前的老记录，对所有电脑可见
            if (deviceId != null && root.deviceId.isNotEmpty() && root.deviceId != deviceId) {
                return@filter false
            }

            val uri = Uri.parse(root.uriString)

            // 授权是否还在：系统里能找到这个 URI，或者能真的读到内容。
            // 只看 persistedUriPermissions 不够——部分提供器（Downloads 的虚拟目录）
            // 不把它登记进去，但授权实际是有效的。
            val inSystem = allowed.any { it == uri || isChildOf(it, uri) }
            inSystem || canStillRead(uri)
        }

        // 早期版本对部分提供器解析不出目录名，存成了通用的"授权目录"，
        // 于是列表里几行长得一模一样。这里就地补一次名字并写回，
        // 用户不用撤销重授权。
        val repaired = mutableMapOf<String, String>()
        val roots = valid.map { root ->
            val name = if (root.displayName.isNotBlank() && root.displayName != GENERIC_NAME) {
                root.displayName
            } else {
                resolveDisplayName(Uri.parse(root.uriString))
                    ?.takeIf { it.isNotBlank() && it != GENERIC_NAME }
                    ?.also { repaired[root.rootId] = it }
                    ?: root.displayName
            }

            // 每次都重新探测：提供器的能力可能变化（授权升级、提供器升级、
            // 目录被移出云盘），用授权时缓存的旧值会给出错误的"不支持"。
            FileRoot(
                rootId = root.rootId,
                displayName = name,
                uri = Uri.parse(root.uriString),
                capabilities = if (refreshCapabilities) {
                    queryCapabilities(Uri.parse(root.uriString))
                } else {
                    root.capabilities
                },
            )
        }

        if (repaired.isNotEmpty()) {
            Log.i(TAG, "修正授权目录名 ${repaired.size} 个：$repaired")
            // 只改名字，不删记录：读不到可能是 SD 卡临时卸载，不该丢授权
            persist(savedRoots().map { root -> repaired[root.rootId]?.let { root.copy(displayName = it) } ?: root })
        }

        // 整机根不是 SAF 授权，而是系统设置里的"所有文件访问权"。
        // 放在这里统一拼装，是因为所有消费方（索引上报、作业执行、反向服务、界面）
        // 都从 activeRoots() 取根——漏一处就会出现"这边有、那边没有"。
        //
        // 1.1.0 起整机排在最前面：产品上"默认开放全部文件"，手选的目录只是
        // 用户拒绝全盘授权时的补充，不该抢在整机前面当默认入口。
        // 整机访问是系统级权限，但"允许哪台电脑看"由我们按设备记
        val allRoot = if (machineAllowed(deviceId)) {
            AllFilesProvider(context).roots().firstOrNull()
        } else {
            null
        }

        return@withContext if (allRoot == null) roots else listOf(allRoot) + roots
    }

    /**
     * 这台电脑能不能看"整机"。
     *
     * 系统权限（所有文件访问权）是全局的，但"给哪台电脑看"是我们的策略，按设备记。
     * 默认 true：升级前所有电脑都能看整机，迁移时不能突然把它们挡掉。
     */
    fun machineAllowed(deviceId: String?): Boolean {
        if (deviceId.isNullOrEmpty()) return true
        return prefs.getBoolean("machine:$deviceId", true)
    }

    fun setMachineAllowed(deviceId: String, allowed: Boolean) {
        if (deviceId.isEmpty()) return
        prefs.edit().putBoolean("machine:$deviceId", allowed).apply()
        Log.i(TAG, "整机访问 device=$deviceId allowed=$allowed")
    }

    private fun persist(roots: List<GrantedRoot>) {
        val encoded = roots.map { root ->
            listOf(
                root.rootId,
                root.displayName,
                root.uriString,
                root.capabilities.toString(),
                root.deviceId,
            )
                .joinToString(SEPARATOR)
        }.toSet()
        prefs.edit().putStringSet(KEY_ROOTS, encoded).apply()
    }

    /**
     * 解析目录的展示名。
     *
     * 三级兜底，因为 provider 差异极大：有的能直接查 tree URI，
     * 有的（三星「我的文件」）对 tree URI 返回空，得先定位到根文档；
     * 再不行就退回 URI 末段——总比所有目录都叫"授权目录"强。
     */
    private fun resolveDisplayName(uri: Uri): String? {
        queryDisplayName(uri)?.let { return it }

        val documentUri = runCatching {
            DocumentsContract.buildDocumentUriUsingTree(
                uri,
                DocumentsContract.getTreeDocumentId(uri),
            )
        }.getOrNull()

        if (documentUri != null) {
            queryDisplayName(documentUri)?.let { return it }
        }

        return fallbackNameFromUri(uri)
    }

    private fun queryDisplayName(uri: Uri): String? {
        return try {
            resolver.query(
                uri,
                arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME),
                null,
                null,
                null,
            )?.use { cursor ->
                if (cursor.moveToFirst()) cursor.getString(0) else null
            }?.takeIf { it.isNotBlank() }
        } catch (ex: Exception) {
            null
        }
    }

    /** `primary:DCIM/Camera` → `Camera`。 */
    private fun fallbackNameFromUri(uri: Uri): String? {
        val documentId = runCatching { DocumentsContract.getTreeDocumentId(uri) }.getOrNull()
            ?: return null

        return documentId.substringAfterLast('/').substringAfterLast(':').takeIf { it.isNotBlank() }
    }

    /**
     * 由文档 FLAG 位推导真实能力。
     *
     * 这是"不能假设所有提供器都支持完整文件管理"的落地处：
     * 每一项单独判，没有对应标志就是不支持。
     *
     * **但光靠标志位不够**：部分提供器（Android 13 的 Downloads 虚拟目录就是典型）
     * 根本不返回 FLAG，查出来是 0。这时候靠标志位判断会得出"什么都不能做"的错误结论，
     * 于是实际尝试一次新建目录——测出来的才是真的能力。探测用的目录立刻删掉。
     */
    private fun queryCapabilities(uri: Uri): Int {
        val flags = readFlags(uri)

        var caps = FileCapabilities.LIST_DIRECTORY or FileCapabilities.READ_FILE or
            FileCapabilities.GET_FILE_INFO

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

        // 提供器不给标志位时，实地探测：能不能新建目录？
        if (flags == 0 && probeCreateDirectory(uri)) {
            caps = caps or FileCapabilities.CREATE_DIRECTORY or FileCapabilities.WRITE_FILE
            if (probeDelete) {
                caps = caps or FileCapabilities.DELETE
            }
        }

        return caps
    }

    /**
     * 实际读一次目录：能列出内容就说明授权还有效。
     *
     * 有些提供器（Downloads 的虚拟目录）不把 tree URI 登记到
     * persistedUriPermissions，但授权实际可用；只信授权列表会误判为"已撤销"。
     */
    private fun canStillRead(uri: Uri): Boolean {
        return try {
            resolver.query(
                uri,
                arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID),
                null,
                null,
                null,
            )?.use { it.moveToFirst() } ?: false
        } catch (ex: SecurityException) {
            false
        } catch (ex: Exception) {
            false
        }
    }

    /** 读取 FLAG 位；查询失败或提供器不实现时返回 0。 */
    private fun readFlags(uri: Uri): Int {
        return try {
            resolver.query(
                uri,
                arrayOf(DocumentsContract.Document.COLUMN_FLAGS),
                null,
                null,
                null,
            )?.use { cursor ->
                if (!cursor.moveToFirst()) {
                    0
                } else {
                    val index = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_FLAGS)
                    if (index >= 0) cursor.getInt(index) else 0
                }
            } ?: 0
        } catch (ex: Exception) {
            0
        }
    }

    /**
     * 实地探测：在这个目录里能不能新建一个子目录。
     *
     * 探测用的目录立刻删除，用户看不到残留。删不掉时把能力当作"不支持"——
     * 留着垃圾目录比不给能力更糟。
     */
    private fun probeCreateDirectory(uri: Uri): Boolean {
        val probeName = ".wc_probe_${System.currentTimeMillis()}"
        return try {
            val created = DocumentsContract.createDocument(
                resolver,
                uri,
                DocumentsContract.Document.MIME_TYPE_DIR,
                probeName,
            )

            if (created == null) {
                false
            } else {
                // 清理探测目录：能删说明删除能力也有
                val deletable = try {
                    DocumentsContract.deleteDocument(resolver, created)
                } catch (ex: Exception) {
                    false
                }

                if (deletable) {
                    probeDelete = true
                    probeRename = true
                }

                true
            }
        } catch (ex: Exception) {
            false
        }
    }

    /** 根 id：URI 的稳定摘要。同一目录重复授权得到同一个 id。 */
    private fun rootIdFor(uri: Uri): String {
        val digest = java.security.MessageDigest.getInstance("SHA-256")
        val bytes = digest.digest(uri.toString().toByteArray(Charsets.UTF_8))
        return bytes.joinToString("") { "%02x".format(it) }.take(16)
    }

    private fun isChildOf(candidate: Uri, root: Uri): Boolean {
        return try {
            DocumentsContract.isChildDocument(resolver, candidate, root)
        } catch (ex: Exception) {
            // 提供器不支持这个查询（如虚拟目录）：只能按 URI 相同来判，
            // 交给上层用"系统授权列表里还有没有"做最终判断。
            false
        }
    }
}
