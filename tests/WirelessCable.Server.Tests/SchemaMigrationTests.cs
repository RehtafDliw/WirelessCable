using Microsoft.Data.Sqlite;
using WirelessCable.Core.Domain;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 迁移回归测试：v1 老库升级到 v2 必须补列且不丢数据。
/// 这个用例源于一次真实启动失败——索引建在补列之前，报 "no such column: token_hash"。
/// </summary>
public sealed class SchemaMigrationTests
{
    private const string V1Ddl =
        """
        CREATE TABLE AppConfig (
            key TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE Devices (
            device_id TEXT PRIMARY KEY NOT NULL,
            display_name TEXT NOT NULL,
            platform TEXT NOT NULL,
            certificate_fingerprint TEXT NOT NULL DEFAULT '',
            is_paired INTEGER NOT NULL DEFAULT 0,
            is_revoked INTEGER NOT NULL DEFAULT 0,
            last_seen_utc TEXT NULL,
            created_at_utc TEXT NOT NULL
        );
        """;

    [Fact]
    public async Task UpgradeFromV1_AddsColumnsPreservesRowsAndBuildsIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var factory = new SqliteDatabaseFactory(Path.Combine(root, "legacy.db"));

        using (var connection = factory.Open())
        {
            using var ddl = connection.CreateCommand();
            ddl.CommandText = V1Ddl;
            ddl.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO Devices (device_id, display_name, platform, certificate_fingerprint, is_paired, created_at_utc)
                VALUES ('olddevice0000000000000000000001', '老设备', 'android', '', 1, '2026-01-01T00:00:00.0000000+00:00');
                PRAGMA user_version = 1;
                """;
            insert.ExecuteNonQuery();
        }

        Assert.True(SchemaInitializer.Initialize(factory));
        Assert.Equal(SchemaInitializer.SchemaVersion, SchemaInitializer.GetSchemaVersion(factory));

        var repository = new DeviceRepository(factory);
        var device = await repository.GetAsync("olddevice0000000000000000000001");

        Assert.NotNull(device);
        Assert.Equal("老设备", device!.DisplayName);
        Assert.Equal(string.Empty, device.TokenHash);

        // 新增列与索引必须可用
        await repository.SetRevokedAsync(device.DeviceId, true);
        var revoked = await repository.GetAsync(device.DeviceId);
        Assert.True(revoked!.IsRevoked);

        // v11：断开标记默认关闭，且能单独切换
        Assert.False(revoked.SessionPaused);
        await repository.SetSessionPausedAsync(device.DeviceId, true);
        Assert.True((await repository.GetAsync(device.DeviceId))!.SessionPaused);

        // 再次初始化保持幂等
        Assert.True(SchemaInitializer.Initialize(factory));

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// v8 → v9：FileOperationJobs 补 result_payload。
    /// 这条路径和 v1 升上来不同——v1 的库里根本没有这张表，DDL 会直接建全；
    /// 只有 v8 的老库才需要 ALTER，漏了它，list 作业一完成就写不进结果。
    /// </summary>
    [Fact]
    public void UpgradeFromV8_AddsResultPayloadColumn()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var factory = new SqliteDatabaseFactory(Path.Combine(root, "v8.db"));

        using (var connection = factory.Open())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                CREATE TABLE FileOperationJobs (
                    job_id TEXT PRIMARY KEY NOT NULL,
                    device_id TEXT NOT NULL,
                    root_id TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    source_path TEXT NULL,
                    destination_path TEXT NULL,
                    display_name TEXT NULL,
                    transfer_task_id TEXT NULL,
                    status TEXT NOT NULL,
                    error_code TEXT NULL,
                    error_message TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                INSERT INTO FileOperationJobs
                    (job_id, device_id, root_id, operation, source_path, status, created_at_utc, updated_at_utc)
                VALUES
                    ('job-v8', 'dev-1', 'root-1', 'delete', 'a.txt', 'completed',
                     '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');

                PRAGMA user_version = 8;
                """;
            seed.ExecuteNonQuery();
        }

        Assert.True(SchemaInitializer.Initialize(factory));
        Assert.Equal(SchemaInitializer.SchemaVersion, SchemaInitializer.GetSchemaVersion(factory));

        using var verify = factory.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT job_id, result_payload FROM FileOperationJobs WHERE job_id = 'job-v8';";
        using var reader = read.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal("job-v8", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// v9 → v10：整机档位从三档拆成四档，老库的 2（旧"可读写"，含删除）
    /// 必须抬到 3，否则用户升级后突然删不了文件。
    /// </summary>
    [Fact]
    public async Task HeartbeatUpsert_DoesNotClearSessionPaused()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var factory = new SqliteDatabaseFactory(Path.Combine(root, "fresh.db"));
        Assert.True(SchemaInitializer.Initialize(factory));

        var repository = new DeviceRepository(factory);
        var device = new StoredDevice
        {
            DeviceId = "device000000000000000000000001",
            DisplayName = "手机",
            Platform = "android",
            IsPaired = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await repository.UpsertAsync(device);
        await repository.SetSessionPausedAsync(device.DeviceId, true);

        // 心跳/索引上报都会走 UpsertAsync：不能把用户手动"断开"冲掉
        await repository.UpsertAsync(new StoredDevice
        {
            DeviceId = device.DeviceId,
            DisplayName = device.DisplayName,
            Platform = device.Platform,
            IsPaired = true,
            LastSeenUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = device.CreatedAtUtc,
        });

        var reloaded = await repository.GetAsync(device.DeviceId);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.SessionPaused);
    }

    /// <summary>
    /// v11 → v12：下载任务新增 purpose 列。
    /// 老记录为 NULL（那时下载只有"发到手机"一种，读出来按 inbox 处理），
    /// 新值必须真能落盘——手机靠它区分"自动接收"和"作业落盘"，猜错会把文件放到错的地方。
    /// </summary>
    [Fact]
    public async Task UpgradeFromV11_AddsPurposeColumnAndKeepsOldRowsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var factory = new SqliteDatabaseFactory(Path.Combine(root, "v11.db"));

        using (var connection = factory.Open())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                CREATE TABLE TransferTasks (
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
                    completed_at_utc   TEXT NULL
                );

                INSERT INTO TransferTasks
                    (task_id, direction, device_id, file_name, relative_path, total_bytes,
                     chunk_size, status, created_at_utc, updated_at_utc)
                VALUES
                    ('task-v11', 'download', 'dev-1', 'old.bin', 'old.bin', 1024,
                     8388608, 'completed', '2026-01-01T00:00:00.0000000+00:00',
                     '2026-01-01T00:00:00.0000000+00:00');

                PRAGMA user_version = 11;
                """;
            seed.ExecuteNonQuery();
        }

        Assert.True(SchemaInitializer.Initialize(factory));
        Assert.Equal(SchemaInitializer.SchemaVersion, SchemaInitializer.GetSchemaVersion(factory));

        var repository = new TransferRepository(factory);
        var stored = await repository.GetAsync("task-v11");

        Assert.NotNull(stored);
        // 老行保持 NULL：调用方（ToDto）会补成 inbox，升级后行为不变
        Assert.Null(stored!.Purpose);

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task UpgradeFromV9_PromotesMachineAccessToDeleteable()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-migration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var factory = new SqliteDatabaseFactory(Path.Combine(root, "v9.db"));

        using (var connection = factory.Open())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                CREATE TABLE Devices (
                    device_id TEXT PRIMARY KEY NOT NULL,
                    display_name TEXT NOT NULL,
                    platform TEXT NOT NULL,
                    certificate_fingerprint TEXT NOT NULL DEFAULT '',
                    is_paired INTEGER NOT NULL DEFAULT 0,
                    is_revoked INTEGER NOT NULL DEFAULT 0,
                    last_seen_utc TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    identity_public_key TEXT NOT NULL DEFAULT '',
                    token_hash TEXT NOT NULL DEFAULT '',
                    paired_at_utc TEXT NULL,
                    machine_access INTEGER NOT NULL DEFAULT 2
                );

                INSERT INTO Devices (device_id, display_name, platform, is_paired, created_at_utc, machine_access)
                VALUES ('dev-rw', '旧可读写', 'android', 1, '2026-01-01T00:00:00.0000000+00:00', 2),
                       ('dev-ro', '只读', 'android', 1, '2026-01-01T00:00:00.0000000+00:00', 1),
                       ('dev-off', '关闭', 'android', 1, '2026-01-01T00:00:00.0000000+00:00', 0);

                PRAGMA user_version = 9;
                """;
            seed.ExecuteNonQuery();
        }

        Assert.True(SchemaInitializer.Initialize(factory));

        using var verify = factory.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT device_id, machine_access FROM Devices ORDER BY device_id;";
        using var reader = read.ExecuteReader();

        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            levels[reader.GetString(0)] = reader.GetInt32(1);
        }

        Assert.Equal(MachineAccessLevels.ReadWriteDelete, levels["dev-rw"]);
        Assert.Equal(MachineAccessLevels.ReadOnly, levels["dev-ro"]);
        Assert.Equal(MachineAccessLevels.None, levels["dev-off"]);

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
