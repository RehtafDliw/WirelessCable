using Microsoft.Data.Sqlite;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Storage.Repositories;

/// <summary>
/// Devices 表仓储。配对成功后写入长期凭据；撤销时同时清空 token_hash，
/// 保证旧令牌即便被截获也无法再次解析到设备。
/// </summary>
public sealed class DeviceRepository
{
    private readonly SqliteDatabaseFactory _factory;

    public DeviceRepository(SqliteDatabaseFactory factory) => _factory = factory;

    public async Task UpsertAsync(StoredDevice device, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Devices (device_id, display_name, platform, certificate_fingerprint,
                                 identity_public_key, token_hash, is_paired, is_revoked,
                                 machine_access, last_seen_utc, paired_at_utc, created_at_utc)
            VALUES ($id, $name, $platform, $fingerprint, $identityKey, $tokenHash, $paired, $revoked,
                    $machineAccess, $lastSeen, $pairedAt, $created)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name = $name,
                platform = $platform,
                certificate_fingerprint = $fingerprint,
                identity_public_key = $identityKey,
                token_hash = $tokenHash,
                is_paired = $paired,
                is_revoked = $revoked,
                last_seen_utc = $lastSeen,
                paired_at_utc = $pairedAt;
            """;
        command.Parameters.AddWithValue("$id", device.DeviceId);
        command.Parameters.AddWithValue("$name", device.DisplayName);
        command.Parameters.AddWithValue("$platform", device.Platform);
        command.Parameters.AddWithValue("$fingerprint", device.CertificateFingerprint);
        command.Parameters.AddWithValue("$identityKey", device.IdentityPublicKey);
        command.Parameters.AddWithValue("$tokenHash", device.TokenHash);
        command.Parameters.AddWithValue("$paired", device.IsPaired ? 1 : 0);
        command.Parameters.AddWithValue("$revoked", device.IsRevoked ? 1 : 0);
        command.Parameters.AddWithValue("$machineAccess", device.MachineAccess);
        command.Parameters.AddWithValue("$lastSeen", (object?)device.LastSeenUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$pairedAt", (object?)device.PairedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", device.CreatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredDevice?> GetAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Devices WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>按令牌哈希查找设备（包含已撤销的，便于区分 401 与 403）。</summary>
    public async Task<StoredDevice?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tokenHash))
        {
            return null;
        }

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT * FROM Devices WHERE token_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<StoredDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        // 排序必须**稳定**：原来按 last_seen_utc DESC，在线设备每 20 秒一次心跳就换位，
        // 两台手机都连着时首页列表会来回跳（用户看到的就是"设备位置上下乱窜"）。
        // 改用配对时间（同一次配对不会变），最后用 device_id 兜底保证全序。
        command.CommandText = """
            SELECT * FROM Devices
            ORDER BY is_paired DESC,
                     COALESCE(paired_at_utc, created_at_utc) DESC,
                     device_id ASC;
            """;

        var results = new List<StoredDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task<int> CountPairedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM Devices WHERE is_paired = 1 AND is_revoked = 0;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>撤销授权：旧令牌因 is_revoked 判定立即失效（保留哈希以便区分 401/403）。</summary>
    public async Task SetRevokedAsync(string deviceId, bool revoked, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Devices SET is_revoked = $revoked WHERE device_id = $id;";
        command.Parameters.AddWithValue("$revoked", revoked ? 1 : 0);
        command.Parameters.AddWithValue("$id", deviceId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateLastSeenAsync(string deviceId, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Devices SET last_seen_utc = $seen WHERE device_id = $id;";
        command.Parameters.AddWithValue("$seen", seenAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", deviceId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 改设备的显示名。单独一个方法而不是走 <see cref="UpsertAsync"/>：
    /// 配对与心跳都会调用后者，走冲突更新会把用户改的名字冲回手机自报的型号。
    /// </summary>
    public async Task SetDisplayNameAsync(string deviceId, string displayName, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Devices SET display_name = $name WHERE device_id = $id;";
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$id", deviceId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();

        // 设备没了，它的共享目录授权就没有意义了：留着会变成"1 台设备有权限"却找不到是谁
        await using (var grants = connection.CreateCommand())
        {
            grants.CommandText = "DELETE FROM SharedDirectoryGrants WHERE device_id = $id;";
            grants.Parameters.AddWithValue("$id", deviceId);
            await grants.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Devices WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static StoredDevice Map(SqliteDataReader reader) => new()
    {
        DeviceId = reader.GetString(reader.GetOrdinal("device_id")),
        DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
        Platform = reader.GetString(reader.GetOrdinal("platform")),
        CertificateFingerprint = reader.GetString(reader.GetOrdinal("certificate_fingerprint")),
        IdentityPublicKey = reader.GetString(reader.GetOrdinal("identity_public_key")),
        TokenHash = reader.GetString(reader.GetOrdinal("token_hash")),
        IsPaired = reader.GetInt32(reader.GetOrdinal("is_paired")) == 1,
        IsRevoked = reader.GetInt32(reader.GetOrdinal("is_revoked")) == 1,
        SessionPaused = reader.GetInt32(reader.GetOrdinal("session_paused")) == 1,
        MachineAccess = reader.GetInt32(reader.GetOrdinal("machine_access")),
        LastSeenUtc = ReadNullableDate(reader, "last_seen_utc"),
        PairedAtUtc = ReadNullableDate(reader, "paired_at_utc"),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
    };

    /// <summary>
    /// 设置某设备对"此电脑"的访问档位（0 关闭 / 1 只读 / 2 可读写）。
    /// 单独一个方法而不是走 <see cref="UpsertAsync"/>：配对与心跳都会调用后者，
    /// 若在冲突时覆盖该列，用户改过的档位会被悄悄冲掉。
    /// </summary>
    public async Task SetMachineAccessAsync(
        string deviceId,
        int level,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Devices SET machine_access = $level WHERE device_id = $id;";
        command.Parameters.AddWithValue("$level", WirelessCable.Core.Domain.MachineAccessLevels.Normalize(level));
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 断开/恢复某台设备的会话。
    ///
    /// 与 <see cref="SetRevokedAsync"/> 的区别：撤销会让令牌失效、必须重新配对；
    /// 这里只是让服务端拒绝它的请求，配对与授权原样保留。
    /// 同样不能走 <see cref="UpsertAsync"/>——心跳会把它冲掉。
    /// </summary>
    public async Task SetSessionPausedAsync(
        string deviceId,
        bool paused,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Devices SET session_paused = $paused WHERE device_id = $id;";
        command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }
}
