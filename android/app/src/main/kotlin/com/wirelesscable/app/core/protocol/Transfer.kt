package com.wirelesscable.app.core.protocol

import kotlinx.serialization.Serializable

/** 任务方向，与 Windows 端 TransferDirections 一致。 */
object TransferDirections {
    const val UPLOAD = "upload"
    const val DOWNLOAD = "download"
}

/**
 * 下载任务的去向，与 Windows 端 TransferPurposes 一致。
 *
 * 手机只自动接收 [INBOX]：落点固定（下载/WirelessCable）。
 * [DIRECTORY] 的目标是某个 SAF 授权目录里的具体文件，必须由作业带 URI 去拉——
 * 抢先把这种任务收进收件箱，作业再执行时就会撞上"任务已完成"。
 */
object TransferPurposes {
    const val INBOX = "inbox"
    const val DIRECTORY = "directory"
}

/** 任务状态，与 Windows 端 TransferStates 一致（字符串直接入库，禁止改名）。 */
object TransferStates {
    const val WAITING = "waiting"
    const val PREPARING = "preparing"
    const val TRANSFERRING = "transferring"
    const val VERIFYING = "verifying"
    const val COMPLETED = "completed"
    const val FAILED = "failed"
    const val CANCELLED = "cancelled"
    const val INTERRUPTED = "interrupted"
    const val PAUSED = "paused"

    fun isTerminal(state: String): Boolean =
        state == COMPLETED || state == FAILED || state == CANCELLED

    fun isActive(state: String): Boolean =
        state == WAITING || state == PREPARING || state == TRANSFERRING || state == VERIFYING

    /** 可续传：中断后数据保留，凭服务端状态继续。 */
    fun isResumable(state: String): Boolean =
        isActive(state) || state == INTERRUPTED || state == PAUSED
}

/** 分块状态，与 Windows 端 ChunkStates 一致。 */
object ChunkStates {
    const val PENDING = "pending"
    const val COMPLETED = "completed"
}

/** 上传落盘冲突策略，与 Windows 端 TransferConflictPolicies 一致。 */
object TransferConflictPolicies {
    const val RENAME = "rename"
    const val OVERWRITE = "overwrite"
    const val SKIP = "skip"
}

/** 传输相关错误码（与 ErrorCodes.cs 对齐）。 */
object TransferErrorCodes {
    const val INVALID_FILE_NAME = "invalid_file_name"
    const val TRANSFER_NOT_FOUND = "transfer_not_found"
    const val TRANSFER_STATE_INVALID = "transfer_state_invalid"

    /** 任务被暂停：数据面暂时不能继续，但**不是错误**（别删文件、别标失败）。 */
    const val TRANSFER_PAUSED = "transfer_paused"

    /**
     * 任务已被另一端取消（对面删记录会先暂停再取消）。
     *
     * 和 TRANSFER_STATE_INVALID 分开：本端点「继续」时，这个码要提示
     * "传输已被接收方取消，是否删除记录"，而不是含糊的"任务已结束"。
     */
    const val TRANSFER_CANCELLED = "transfer_cancelled"

    const val CHUNK_OFFSET_MISMATCH = "chunk_offset_mismatch"
    const val CHUNK_CHECKSUM_MISMATCH = "chunk_checksum_mismatch"
    const val CHUNK_IN_FLIGHT = "chunk_in_flight"
    const val TOO_MANY_ACTIVE_TRANSFERS = "too_many_active_transfers"
    const val INSUFFICIENT_STORAGE = "insufficient_storage"
    const val TRANSFER_TIMEOUT = "transfer_timeout"
    const val FILE_INACCESSIBLE = "file_inaccessible"
}

/** 传输端点路径。 */
object TransferPaths {
    const val UPLOADS = "${ProtocolConstants.API_PREFIX}/transfer/uploads"
    const val TASKS = "${ProtocolConstants.API_PREFIX}/transfer/tasks"
    const val CHUNK_SHA256_HEADER = "X-WC-Chunk-Sha256"

    fun task(taskId: String) = "$TASKS/$taskId"
    fun chunks(taskId: String) = "$TASKS/$taskId/chunks"
    fun chunk(taskId: String, index: Int) = "$TASKS/$taskId/chunks/$index"
    fun chunkAck(taskId: String, index: Int) = "$TASKS/$taskId/chunks/$index/ack"
    fun complete(taskId: String) = "$TASKS/$taskId/complete"
    fun ack(taskId: String) = "$TASKS/$taskId/ack"
    fun pause(taskId: String) = "$TASKS/$taskId/pause"
    fun cancel(taskId: String) = "$TASKS/$taskId/cancel"
    fun restart(taskId: String) = "$TASKS/$taskId/restart"
    fun resume(taskId: String) = "$TASKS/$taskId/resume"

    /** 删除传输记录（可同时删除电脑上的文件）。 */
    const val DELETE = "$TASKS/delete"

    /** 整批动作：整文件夹在列表里是一行，行上的暂停/取消一次作用到整批。 */
    const val BATCH = "$TASKS/batch"
}

/** 一批任务上的同一个动作。`action` 取 pause / resume / cancel / restart。 */
@Serializable
data class TransferBatchActionRequest(
    val taskIds: List<String>,
    val action: String,
    /** `cancel` 时：连已经收到的文件一起删掉（用户勾了"同时删除文件"）。 */
    val deleteFiles: Boolean = false,
)

@Serializable
data class TransferBatchActionResult(
    val ok: Int = 0,
    val failed: Int = 0,
    val firstError: String? = null,
    val tasks: List<TransferTask> = emptyList(),
)

@Serializable
data class TransferStartRequest(
    val clientRequestId: String? = null,
    val fileName: String,
    val relativeDirectory: String? = null,
    val totalBytes: Long,
    val sha256: String? = null,
    val requestedChunkSize: Int? = null,
    val conflict: String? = null,
    /**
     * 目标共享目录 id。缺省为电脑的默认接收目录。
     * 服务端会校验该设备在此目录上的 Upload 权限，无权限直接拒绝。
     */
    val directoryId: String? = null,
    /**
     * 整批传输的批次名（缺省为单个文件）。
     *
     * 上传方向（手机 → 电脑）的一批文件可能各自在不同的目录下，
     * 服务端要靠它把"取消一批"补全到同批的每一个任务 ——
     * 按目录补全只对同一目录有效，跨目录就会漏（漏掉的那条还在传，
     * 电脑端「传输中」角标一直亮着）。
     */
    val batchName: String? = null,
)

@Serializable
data class TransferTask(
    val taskId: String,
    val direction: String = TransferDirections.UPLOAD,
    /**
     * 下载去向：inbox = 发到手机收件目录（可自动接收）；
     * directory = 写进手机授权目录（只能由作业带目标 URI 去拉）。
     *
     * 缺省 inbox：老服务端没有这个字段，那时下载只有一种语义。
     */
    val purpose: String = TransferPurposes.INBOX,
    val deviceName: String = "",
    val fileName: String = "",
    val relativePath: String = "",
    /** 整文件夹传输的分组键：同一批里的任务共用，界面据此收成一行。 */
    val batchId: String? = null,
    /** 批次文件夹名（收成一行时显示）。 */
    val batchName: String? = null,
    val totalBytes: Long = 0L,
    val transferredBytes: Long = 0L,
    val chunkSize: Int = 0,
    val chunkCount: Int = 0,
    val state: String = TransferStates.WAITING,
    val sha256: String? = null,
    val errorCode: String? = null,
    val errorMessage: String? = null,
    val completedChunks: List<Int> = emptyList(),
    val createdAtUnixMs: Long = 0L,
    val updatedAtUnixMs: Long = 0L,
)

/** 删除传输记录的请求；两端共用同一份协议。 */
@Serializable
data class TransferDeleteRecordsRequest(
    val taskIds: List<String>,
    /**
     * 是否同时删除电脑上的文件。
     * 服务端只会删手机传过去的文件，电脑发出去的原文件不动。
     */
    val deleteFiles: Boolean = false,
)

@Serializable
data class TransferDeleteRecordsResult(
    val deleted: Int = 0,
    val filesDeleted: Int = 0,
    val rejected: List<String> = emptyList(),
)

/** 统一分块模型，与 Windows 端 ChunkInfoDto 对应。 */
@Serializable
data class ChunkInfo(
    val transferId: String,
    val index: Int,
    val offset: Long,
    val length: Int,
    val sha256: String? = null,
    val status: String = ChunkStates.PENDING,
) {
    val isCompleted: Boolean get() = status == ChunkStates.COMPLETED
}

@Serializable
data class ChunkAck(
    val taskId: String,
    val index: Int,
    val bytesThisChunk: Int,
    val transferredBytes: Long,
    val totalBytes: Long,
    val nextIndex: Int,
    val alreadyCompleted: Boolean = false,
)

@Serializable
data class DownloadChunkAckRequest(
    val sha256: String,
)

@Serializable
data class TransferCompleteRequest(
    val sha256: String,
)

@Serializable
data class TransferCompleteResponse(
    val taskId: String,
    val state: String,
    val fileName: String = "",
    val relativePath: String = "",
    val sizeBytes: Long = 0L,
    val sha256: String = "",
    val durationMs: Long = 0L,
)
