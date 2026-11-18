package com.wirelesscable.app.data.db

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import kotlinx.coroutines.flow.Flow

@Dao
interface DeviceDao {

    @Query("SELECT * FROM DeviceEntity ORDER BY lastSeenUtc DESC")
    fun observeAll(): Flow<List<DeviceEntity>>

    @Query("SELECT * FROM DeviceEntity ORDER BY lastSeenUtc DESC")
    suspend fun getAll(): List<DeviceEntity>

    @Query("SELECT * FROM DeviceEntity WHERE deviceId = :deviceId LIMIT 1")
    suspend fun get(deviceId: String): DeviceEntity?

    /** 只改显示名：用户起的名字不能被心跳/连接上报的型号名冲掉。 */
    @Query("UPDATE DeviceEntity SET displayName = :name WHERE deviceId = :deviceId")
    suspend fun rename(deviceId: String, name: String)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(device: DeviceEntity)

    @Query("DELETE FROM DeviceEntity WHERE deviceId = :deviceId")
    suspend fun delete(deviceId: String)
}

@Dao
interface ConnectionDao {

    @Query("SELECT * FROM ConnectionEntity ORDER BY lastConnectedUtc DESC LIMIT 1")
    fun observeLatest(): Flow<ConnectionEntity?>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(connection: ConnectionEntity)
}

@Dao
interface TransferDao {

    @Query("SELECT * FROM TransferEntity ORDER BY updatedAtUtc DESC LIMIT 20")
    fun observeRecent(): Flow<List<TransferEntity>>

    @Query("SELECT * FROM TransferEntity WHERE taskId = :taskId LIMIT 1")
    suspend fun get(taskId: String): TransferEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(transfer: TransferEntity)

    /** 本地未完成、可能被系统回收打断的任务（APP 重启后据此提示恢复）。 */
    @Query("SELECT * FROM TransferEntity WHERE status IN ('waiting','preparing','transferring','verifying','interrupted','paused')")
    suspend fun listUnfinished(): List<TransferEntity>

    @Query("DELETE FROM TransferEntity WHERE taskId = :taskId")
    suspend fun delete(taskId: String)
}

@Dao
interface TransferChunkDao {

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertAll(chunks: List<TransferChunkEntity>)

    @Query("SELECT * FROM TransferChunkEntity WHERE taskId = :taskId ORDER BY chunkIndex ASC")
    suspend fun list(taskId: String): List<TransferChunkEntity>

    @Query(
        "UPDATE TransferChunkEntity SET status = 'completed', sha256 = :sha256, updatedAtUtc = :updatedAt " +
            "WHERE taskId = :taskId AND chunkIndex = :index",
    )
    suspend fun markCompleted(taskId: String, index: Int, sha256: String?, updatedAt: Long)

    @Query("SELECT COUNT(*) FROM TransferChunkEntity WHERE taskId = :taskId AND status = 'completed'")
    suspend fun countCompleted(taskId: String): Int

    @Query("UPDATE TransferChunkEntity SET status = 'pending', updatedAtUtc = :updatedAt WHERE taskId = :taskId")
    suspend fun resetAll(taskId: String, updatedAt: Long)

    @Query(
        "UPDATE TransferChunkEntity SET status = 'pending', updatedAtUtc = :updatedAt " +
            "WHERE taskId = :taskId AND chunkIndex = :index",
    )
    suspend fun resetIndex(taskId: String, index: Int, updatedAt: Long)

    @Query("DELETE FROM TransferChunkEntity WHERE taskId = :taskId")
    suspend fun deleteForTask(taskId: String)
}

@Dao
interface SettingsDao {

    @Query("SELECT value FROM AppSettings WHERE key = :key")
    suspend fun get(key: String): String?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun put(setting: AppSettingsEntity)
}
