package com.wirelesscable.app.core.protocol

import kotlinx.serialization.Serializable

/**
 * 文件管理协议模型。字段名与 C# 端 WirelessCable.Protocol.Dtos.FileManagement 一致。
 */
object FileProtocol {
    const val FS_PATH = ProtocolConstants.API_PREFIX + "/fs"
    const val DEVICE_INDEX_PATH = "$FS_PATH/device/index"
    const val DEVICE_INDEX_SYNC_PATH = "$FS_PATH/device/index/sync"
    const val DEVICE_JOBS_PATH = "$FS_PATH/device/jobs"

    /** 根类型。 */
    const val KIND_SHARED = "shared"
    const val KIND_DEVICE = "device"

    /** 整机根（"此电脑"）：手机像资源管理器那样看到电脑全部文件。只读。 */
    const val KIND_MACHINE = "machine"

    /** 整机根的固定 id。 */
    const val ROOT_MACHINE = "machine"

    /** 操作类型。 */
    const val OP_CREATE = "create"
    const val OP_RENAME = "rename"
    const val OP_DELETE = "delete"
    const val OP_MOVE = "move"

    /** 复制：源不动，目标必须是新路径（不覆盖）。 */
    const val OP_COPY = "copy"
    const val OP_UPLOAD = "upload"
    const val OP_DOWNLOAD = "download"

    /**
     * 列目录。索引快照有深度/条数上限，电脑进到未索引的目录时现场问一次，
     * 结果放在 [FileJobResult.payload] 里回去。
     */
    const val OP_LIST = "list"

    /** 作业状态。 */
    const val JOB_PENDING = "pending"
    const val JOB_RUNNING = "running"
    const val JOB_COMPLETED = "completed"
    const val JOB_FAILED = "failed"
}

@Serializable
data class FileRootDto(
    val rootId: String,
    val kind: String,
    val displayName: String,
    val deviceId: String = "",
    val capabilities: Int = 0,
    val permissions: Int = 0,
)

@Serializable
data class FileEntryDto(
    val name: String,
    val relativePath: String,
    val isDirectory: Boolean,
    val sizeBytes: Long,
    val modifiedUnixMs: Long = 0,
    /** 该条目所在目录支持的操作能力位。 */
    val capabilities: Int = 0,
)

@Serializable
data class FileListingDto(
    val rootId: String,
    val path: String,
    val entries: List<FileEntryDto> = emptyList(),
    val capabilities: Int = 0,
)

@Serializable
data class FileCreateDirectoryRequest(
    val rootId: String,
    val parentPath: String = "",
    val name: String,
)

@Serializable
data class FileRenameRequest(
    val rootId: String,
    val path: String,
    val newName: String,
)

@Serializable
data class FileDeleteRequest(
    val rootId: String,
    val path: String,
    val recursive: Boolean = false,
)

@Serializable
data class FileMoveRequest(
    val rootId: String,
    val sourcePath: String,
    val destinationPath: String,
)

/** 复制请求：字段与移动一致，只是源不动。 */
@Serializable
data class FileCopyRequest(
    val rootId: String,
    val sourcePath: String,
    val destinationPath: String,
)

@Serializable
data class FileBatchRequest(
    val rootId: String,
    val paths: List<String> = emptyList(),
    val recursive: Boolean = false,
)

@Serializable
data class FileBatchResult(
    val path: String,
    val success: Boolean,
    val errorCode: String? = null,
    val errorMessage: String? = null,
)

@Serializable
data class FileJobRequest(
    val rootId: String,
    val operation: String,
    val sourcePath: String? = null,
    val destinationPath: String? = null,
    val displayName: String? = null,
)

@Serializable
data class FileJobDto(
    val jobId: String,
    val rootId: String,
    val operation: String,
    val sourcePath: String? = null,
    val destinationPath: String? = null,
    val displayName: String? = null,
    val transferTaskId: String? = null,
    val destinationDirectoryId: String? = null,
    val status: String = FileProtocol.JOB_PENDING,
    val errorCode: String? = null,
    val errorMessage: String? = null,
)

@Serializable
data class FileJobResult(
    val jobId: String,
    val status: String = FileProtocol.JOB_COMPLETED,
    val errorCode: String? = null,
    val errorMessage: String? = null,
    /** list 作业回传的目录条目（FileEntryDto[] 的 JSON）；其他操作为 null。 */
    val payload: String? = null,
)

/** 手机上报授权目录索引的负载。 */
@Serializable
data class DeviceFileIndexDto(
    val rootId: String,
    val displayName: String,
    val providerKind: String = "saf",
    val capabilities: Int = 0,
    val entries: List<FileEntryDto> = emptyList(),
    /**
     * 仍然有效的**全部**根 id（含本条根自己）。服务端据此剔除已撤销的根；
     * 不传（空列表）时服务端不做清理，兼容老客户端。
     */
    val activeRootIds: List<String> = emptyList(),
)

/** 只同步根的集合：全部授权都被撤销时没有索引可上报，用它清空服务端快照。 */
@Serializable
data class DeviceFileSyncDto(
    val activeRootIds: List<String> = emptyList(),
)

/** 根同步结果。 */
@Serializable
data class FileSyncResult(
    val removedRoots: Int = 0,
)

/** 从某个根下载文件到手机。手机只给"根 id + 相对路径"，不给绝对路径。 */
@Serializable
data class FileDownloadRequest(
    val rootId: String,
    val path: String,
)

/**
 * 「下载整个文件夹」的结果：服务端已经把目录展开成一批任务。
 *
 * `batchId` 为空表示文件夹里一个文件都没有（空目录也是合法结果，不该报错）。
 */
@Serializable
data class FolderDownloadResult(
    val batchId: String? = null,
    val batchName: String = "",
    val created: Int = 0,
    val failed: Int = 0,
    /** 被跳过的条目数：符号链接、超过数量上限、读不了的文件。 */
    val skipped: Int = 0,
    val error: String? = null,
    /** 这一批建好的任务。带着走，手机就不用再查一次列表。 */
    val tasks: List<TransferTask> = emptyList(),
)
