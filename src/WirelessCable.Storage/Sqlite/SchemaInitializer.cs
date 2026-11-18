using Microsoft.Data.Sqlite;

namespace WirelessCable.Storage.Sqlite;

/// <summary>
/// 建表与版本迁移。使用幂等 DDL，启动时无条件执行一次。
/// 表结构变更必须同时更新 docs/database.md 并提升 <see cref="SchemaVersion"/>。
/// </summary>
public static class SchemaInitializer
{
    /// <summary>当前 schema 版本，写入 PRAGMA user_version。</summary>
    /// <remarks>
    /// v2：Devices 增加 identity_public_key / token_hash / paired_at_utc。
    /// v3：TransferTasks 增加 chunk_count / error_code / temp_path / local_path / completed_at_utc。
    /// v4：新增 TransferChunks 表；TransferTasks 增加 completed_chunks / conflict / source_device / target_device。
    /// v5：新增 SharedDirectories / SharedDirectoryGrants / DeviceFileRoots / DeviceFileEntries / FileOperationJobs。
    /// v6：Devices 增加 machine_access —— 该设备是否可见"此电脑"（整机浏览）。
    /// v7：FileOperationJobs 增加 transfer_task_id —— 作业关联的传输任务（电脑→手机传文件）。
    /// v8：machine_access 由布尔改为三档（0 关闭 / 1 只读 / 2 可读写），默认 2。
    /// v9：FileOperationJobs 增加 result_payload —— list 作业把目录条目带回电脑。
    /// v10：machine_access 拆成四档，2 = 可读写（不含删除）、3 = 可读写 + 删除；
    ///      老库的 2 一律抬到 3，否则用户升级后突然删不了文件。
    /// v14：TransferTasks 增加 batch_id / batch_name —— 整文件夹传输在列表里收成一行。
    /// </remarks>
    public const int SchemaVersion = 14;

    private const string Ddl =
        """
        CREATE TABLE IF NOT EXISTS AppConfig (
            key           TEXT PRIMARY KEY NOT NULL,
            value         TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Devices (
            device_id              TEXT PRIMARY KEY NOT NULL,
            display_name           TEXT NOT NULL,
            platform               TEXT NOT NULL,
            certificate_fingerprint TEXT NOT NULL DEFAULT '',
            identity_public_key    TEXT NOT NULL DEFAULT '',
            token_hash             TEXT NOT NULL DEFAULT '',
            is_paired              INTEGER NOT NULL DEFAULT 0,
            is_revoked             INTEGER NOT NULL DEFAULT 0,
            session_paused         INTEGER NOT NULL DEFAULT 0,
            last_seen_utc          TEXT NULL,
            paired_at_utc          TEXT NULL,
            created_at_utc         TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_devices_paired ON Devices (is_paired, is_revoked);

        CREATE TABLE IF NOT EXISTS PairingRecords (
            pairing_id     TEXT PRIMARY KEY NOT NULL,
            device_id      TEXT NOT NULL,
            code_hash      TEXT NOT NULL,
            issued_at_utc  TEXT NOT NULL,
            expires_at_utc TEXT NOT NULL,
            attempts       INTEGER NOT NULL DEFAULT 0,
            status         TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_pairing_device ON PairingRecords (device_id);

        CREATE TABLE IF NOT EXISTS TransferTasks (
            task_id            TEXT PRIMARY KEY NOT NULL,
            direction          TEXT NOT NULL,
            device_id          TEXT NOT NULL,
            file_name          TEXT NOT NULL,
            relative_path      TEXT NOT NULL,
            total_bytes        INTEGER NOT NULL,
            transferred_bytes  INTEGER NOT NULL DEFAULT 0,
            chunk_size         INTEGER NOT NULL,
            chunk_count        INTEGER NOT NULL DEFAULT 0,
            status             TEXT NOT NULL,
            sha256             TEXT NULL,
            error_code         TEXT NULL,
            error_message      TEXT NULL,
            temp_path          TEXT NULL,
            local_path         TEXT NULL,
            created_at_utc     TEXT NOT NULL,
            updated_at_utc     TEXT NOT NULL,
            completed_at_utc   TEXT NULL,
            batch_id           TEXT NULL,
            batch_name         TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_transfer_status ON TransferTasks (status);
        CREATE INDEX IF NOT EXISTS ix_transfer_device ON TransferTasks (device_id);

        CREATE TABLE IF NOT EXISTS TransferChunks (
            task_id        TEXT NOT NULL,
            chunk_index    INTEGER NOT NULL,
            offset_bytes   INTEGER NOT NULL,
            length         INTEGER NOT NULL,
            sha256         TEXT NULL,
            status         TEXT NOT NULL DEFAULT 'pending',
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (task_id, chunk_index)
        );

        CREATE INDEX IF NOT EXISTS ix_chunks_task ON TransferChunks (task_id, status);

        CREATE TABLE IF NOT EXISTS SharedDirectories (
            directory_id     TEXT PRIMARY KEY NOT NULL,
            display_name     TEXT NOT NULL,
            root_path        TEXT NOT NULL,
            created_at_utc   TEXT NOT NULL,
            updated_at_utc   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS SharedDirectoryGrants (
            directory_id     TEXT NOT NULL,
            device_id        TEXT NOT NULL,
            permissions      INTEGER NOT NULL DEFAULT 0,
            created_at_utc   TEXT NOT NULL,
            updated_at_utc   TEXT NOT NULL,
            PRIMARY KEY (directory_id, device_id)
        );

        CREATE INDEX IF NOT EXISTS ix_grants_device ON SharedDirectoryGrants (device_id);

        CREATE TABLE IF NOT EXISTS DeviceFileRoots (
            root_id          TEXT PRIMARY KEY NOT NULL,
            device_id        TEXT NOT NULL,
            display_name     TEXT NOT NULL,
            provider_kind    TEXT NOT NULL,
            capabilities     INTEGER NOT NULL DEFAULT 0,
            granted_at_utc   TEXT NOT NULL,
            updated_at_utc   TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_deviceroots_device ON DeviceFileRoots (device_id);

        CREATE TABLE IF NOT EXISTS DeviceFileEntries (
            root_id          TEXT NOT NULL,
            relative_path    TEXT NOT NULL,
            display_name     TEXT NOT NULL,
            is_directory     INTEGER NOT NULL DEFAULT 0,
            size_bytes       INTEGER NOT NULL DEFAULT 0,
            modified_utc     TEXT NULL,
            capabilities     INTEGER NOT NULL DEFAULT 0,
            updated_at_utc   TEXT NOT NULL,
            PRIMARY KEY (root_id, relative_path)
        );

        CREATE INDEX IF NOT EXISTS ix_entries_parent ON DeviceFileEntries (root_id, relative_path);

        CREATE TABLE IF NOT EXISTS FileOperationJobs (
            job_id           TEXT PRIMARY KEY NOT NULL,
            device_id        TEXT NOT NULL,
            root_id          TEXT NOT NULL,
            operation        TEXT NOT NULL,
            source_path      TEXT NULL,
            destination_path TEXT NULL,
            display_name     TEXT NULL,
            transfer_task_id TEXT NULL,
            status           TEXT NOT NULL,
            error_code       TEXT NULL,
            error_message    TEXT NULL,
            result_payload   TEXT NULL,
            created_at_utc   TEXT NOT NULL,
            updated_at_utc   TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_jobs_device_status ON FileOperationJobs (device_id, status);
        """;

    /// <summary>执行建表。返回是否成功写入 schema 版本。</summary>
    public static bool Initialize(SqliteDatabaseFactory factory)
    {
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction();

        // 先读旧版本：后面的数据迁移要按"从哪个版本升上来"决定做不做
        int previousVersion;
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "PRAGMA user_version;";
            previousVersion = Convert.ToInt32(probe.ExecuteScalar());
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = Ddl;
            command.ExecuteNonQuery();
        }

        // 从 v1 升级：老库的 Devices 表缺少 v2 列，逐列补齐（幂等）
        EnsureColumn(connection, transaction, "Devices", "identity_public_key", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction, "Devices", "token_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, transaction, "Devices", "paired_at_utc", "TEXT NULL");

        // 从 v10 升级：用户主动"断开"某台设备（保留配对，只是拒绝它的请求）
        EnsureColumn(connection, transaction, "Devices", "session_paused", "INTEGER NOT NULL DEFAULT 0");

        // 从 v5 升级：整机访问档位。默认 3（可读写 + 删除）—— 1.1.0 起"默认开放全部文件"，
        // 用户可在桌面端按设备降为只读、可读写（不能删）或关闭。
        EnsureColumn(connection, transaction, "Devices", "machine_access", "INTEGER NOT NULL DEFAULT 3");

        // 从 v7 升级：老库里 1 表示"能看到但只读"，新语义里 1 仍是只读；
        // 产品要求默认全开，所以把历史值统一提到当时的最高档。0（用户主动关闭）保持不动。
        if (previousVersion is > 0 and < 8)
        {
            using var promote = connection.CreateCommand();
            promote.Transaction = transaction;
            promote.CommandText = "UPDATE Devices SET machine_access = 2 WHERE machine_access = 1;";
            promote.ExecuteNonQuery();
        }

        // 从 v9 升级：2 从"可读写（含删除）"拆成"可读写（不含删除）"，删除挪到 3。
        // 老库里的 2 原本含删除，直接抬到 3 才能保持行为不变。
        if (previousVersion is > 0 and < 10)
        {
            using var promoteDelete = connection.CreateCommand();
            promoteDelete.Transaction = transaction;
            promoteDelete.CommandText = "UPDATE Devices SET machine_access = 3 WHERE machine_access = 2;";
            promoteDelete.ExecuteNonQuery();
        }

        // 从 v6 升级：作业关联的传输任务（电脑上传文件到手机目录时用）
        EnsureColumn(connection, transaction, "FileOperationJobs", "transfer_task_id", "TEXT NULL");

        // 从 v8 升级：list 作业要把目录条目带回来
        EnsureColumn(connection, transaction, "FileOperationJobs", "result_payload", "TEXT NULL");

        // 从 v2 升级：TransferTasks 增量列
        EnsureColumn(connection, transaction, "TransferTasks", "batch_id", "TEXT NULL");
        EnsureColumn(connection, transaction, "TransferTasks", "batch_name", "TEXT NULL");
        EnsureColumn(connection, transaction, "TransferTasks", "chunk_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "TransferTasks", "error_code", "TEXT NULL");
        EnsureColumn(connection, transaction, "TransferTasks", "temp_path", "TEXT NULL");
        EnsureColumn(connection, transaction, "TransferTasks", "local_path", "TEXT NULL");
        EnsureColumn(connection, transaction, "TransferTasks", "completed_at_utc", "TEXT NULL");

        // 从 v3 升级：断点续传所需的冗余计数与冲突策略、源/目标设备
        EnsureColumn(connection, transaction, "TransferTasks", "completed_chunks", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, transaction, "TransferTasks", "conflict", "TEXT NULL");
        EnsureColumn(connection, transaction, "TransferTasks", "source_device", "TEXT NULL");
        EnsureColumn(connection, transaction, "TransferTasks", "target_device", "TEXT NULL");

        // 从 v4 升级：阶段 5 多共享目录，任务记录归属到具体目录
        EnsureColumn(connection, transaction, "TransferTasks", "directory_id", "TEXT NULL");

        // 从 v11 升级：下载任务区分"发到手机收件目录"与"写进手机授权目录"。
        // 手机端只自动接收前者；旧记录没有值，按 inbox 处理（那时只有这一种）。
        EnsureColumn(connection, transaction, "TransferTasks", "purpose", "TEXT NULL");

        // 从 v12 升级：根的 id 改成"设备内唯一"（库里存 `设备id/手机报的id`）。
        // 整机根的 id 是固定字符串 "phone-all"，两台手机都授权时后一条会撞上前一条：
        // ON CONFLICT(root_id) 只更新名字和能力、不动 device_id，于是第二台手机
        // "上报成功"却永远不属于自己，电脑端也就看不到（1.5.9 真机）。
        if (previousVersion is > 0 and < 13)
        {
            MigrateRootIdsToDeviceScoped(connection, transaction);
        }

        // 索引必须等列补齐后再建，否则老库升级会报 "no such column"
        using (var tokenIndex = connection.CreateCommand())
        {
            tokenIndex.Transaction = transaction;
            tokenIndex.CommandText = "CREATE INDEX IF NOT EXISTS ix_devices_token ON Devices (token_hash);";
            tokenIndex.ExecuteNonQuery();
        }

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            versionCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// v12 → v13：给根和条目的 root_id 补上设备前缀。
    ///
    /// 先丢掉"设备已经不在了"的根（它们只在界面上留垃圾），
    /// 再给条目加前缀（此时还能 join 到所属根的 device_id），最后给根加前缀。
    /// </summary>
    private static void MigrateRootIdsToDeviceScoped(SqliteConnection connection, SqliteTransaction transaction)
    {
        // 顺序不能反：先把"设备已经不在了"的根删掉，再清它们留下的条目。
        // 反过来做，判断"条目所属的根还在不在"时那些根还没删，孤儿就清不掉
        // （1.5.9 第一次上线就踩到：roots 空了，entries 还留着 4732 条）。
        Execute(connection, transaction,
            "DELETE FROM DeviceFileRoots WHERE device_id NOT IN (SELECT device_id FROM Devices);");

        Execute(connection, transaction,
            "DELETE FROM DeviceFileEntries WHERE root_id NOT IN (SELECT root_id FROM DeviceFileRoots);");

        Execute(connection, transaction,
            """
            UPDATE DeviceFileEntries
            SET root_id = (SELECT r.device_id || '/' || r.root_id
                           FROM DeviceFileRoots r WHERE r.root_id = DeviceFileEntries.root_id)
            WHERE root_id IN (SELECT root_id FROM DeviceFileRoots);
            """);

        Execute(connection, transaction,
            "UPDATE DeviceFileRoots SET root_id = device_id || '/' || root_id;");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"PRAGMA table_info({table});";

        var exists = false;
        using (var reader = check.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    /// <summary>读取当前 schema 版本。</summary>
    public static int GetSchemaVersion(SqliteDatabaseFactory factory)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
