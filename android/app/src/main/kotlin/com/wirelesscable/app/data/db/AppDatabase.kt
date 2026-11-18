package com.wirelesscable.app.data.db

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

@Database(
    entities = [
        DeviceEntity::class,
        ConnectionEntity::class,
        TransferEntity::class,
        TransferChunkEntity::class,
        AppSettingsEntity::class,
    ],
    version = 2,
    exportSchema = false,
)
abstract class AppDatabase : RoomDatabase() {

    abstract fun deviceDao(): DeviceDao
    abstract fun connectionDao(): ConnectionDao
    abstract fun transferDao(): TransferDao
    abstract fun transferChunkDao(): TransferChunkDao
    abstract fun settingsDao(): SettingsDao

    companion object {
        private const val NAME = "wirelesscable.db"

        @Volatile
        private var instance: AppDatabase? = null

        /** v1 → v2：TransferEntity 加续传列，新增 TransferChunkEntity 表。 */
        private val MIGRATION_1_2 = object : androidx.room.migration.Migration(1, 2) {
            override fun migrate(db: androidx.sqlite.db.SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN chunkSize INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN chunkCount INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN completedChunks INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN sha256 TEXT")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN errorMessage TEXT")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN sourceUri TEXT")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN tempPath TEXT")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN destinationUri TEXT")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN savedToPhone INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE TransferEntity ADD COLUMN declaredSha256 TEXT")
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS TransferChunkEntity (
                        taskId TEXT NOT NULL,
                        chunkIndex INTEGER NOT NULL,
                        offsetBytes INTEGER NOT NULL,
                        length INTEGER NOT NULL,
                        sha256 TEXT,
                        status TEXT NOT NULL,
                        updatedAtUtc INTEGER NOT NULL,
                        PRIMARY KEY (taskId, chunkIndex)
                    )
                    """.trimIndent(),
                )
                db.execSQL("CREATE INDEX IF NOT EXISTS index_TransferChunkEntity_taskId ON TransferChunkEntity (taskId)")
            }
        }

        fun get(context: Context): AppDatabase = instance ?: synchronized(this) {
            instance ?: Room.databaseBuilder(
                context.applicationContext,
                AppDatabase::class.java,
                NAME,
            ).addMigrations(MIGRATION_1_2).build().also { instance = it }
        }
    }
}
