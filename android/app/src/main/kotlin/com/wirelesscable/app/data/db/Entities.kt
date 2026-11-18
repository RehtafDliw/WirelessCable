package com.wirelesscable.app.data.db

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/** 已配对/曾连接过的设备。 */
@Entity(tableName = "DeviceEntity")
data class DeviceEntity(
    @PrimaryKey val deviceId: String,
    val displayName: String,
    val platform: String,
    val certificateFingerprint: String,
    val isPaired: Boolean,
    val lastSeenUtc: Long,
)

/** 最近一次连接过的服务端地址。 */
@Entity(tableName = "ConnectionEntity")
data class ConnectionEntity(
    @PrimaryKey val endpoint: String,
    val host: String,
    val port: Int,
    val certificateFingerprint: String,
    val lastConnectedUtc: Long,
)

/** 传输任务。阶段 4 起承载断点续传所需的全部字段。 */
@Entity(tableName = "TransferEntity")
data class TransferEntity(
    @PrimaryKey val taskId: String,
    val direction: String,
    val fileName: String,
    val totalBytes: Long,
    val transferredBytes: Long,
    val status: String,
    val updatedAtUtc: Long,
    val chunkSize: Int = 0,
    val chunkCount: Int = 0,
    val completedChunks: Int = 0,
    val sha256: String? = null,
    val errorMessage: String? = null,
    /** 上传任务的 SAF 源（已取持久化授权）；下载任务为 null。 */
    val sourceUri: String? = null,
    /** 下载任务的本地临时文件绝对路径（应用私有目录）。 */
    val tempPath: String? = null,
    /** 下载任务用户选定的落点 SAF URI（已取持久化授权）。 */
    val destinationUri: String? = null,
    /** 下载任务是否已成功复制到落点。 */
    val savedToPhone: Boolean = false,
    /** 上传建任务前算好的整文件 SHA-256（续传免于重读源文件）。 */
    val declaredSha256: String? = null,
)

/** 分块状态。服务端位图是恢复时的唯一事实来源，本表只是它的本地镜像。 */
@Entity(
    tableName = "TransferChunkEntity",
    primaryKeys = ["taskId", "chunkIndex"],
    indices = [Index("taskId")],
)
data class TransferChunkEntity(
    val taskId: String,
    val chunkIndex: Int,
    val offsetBytes: Long,
    val length: Int,
    val sha256: String? = null,
    val status: String,
    val updatedAtUtc: Long,
)

/** 键值设置。 */
@Entity(tableName = "AppSettings")
data class AppSettingsEntity(
    @PrimaryKey val key: String,
    val value: String,
)
