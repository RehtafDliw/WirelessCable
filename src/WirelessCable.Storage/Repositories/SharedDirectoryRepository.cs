using Microsoft.Data.Sqlite;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Storage.Repositories;

/// <summary>
/// 共享目录与设备授权仓储。
///
/// 共享目录是 Windows 本机的绝对路径；授权是 (目录, 设备) → 权限位。
/// 服务端每次文件操作都要过这里，手机本地记什么都不算数。
/// </summary>
public sealed class SharedDirectoryRepository
{
    private readonly SqliteDatabaseFactory _factory;

    public SharedDirectoryRepository(SqliteDatabaseFactory factory) => _factory = factory;

    public async Task UpsertAsync(SharedDirectoryRecord directory, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SharedDirectories (directory_id, display_name, root_path, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $path, $created, $updated)
            ON CONFLICT(directory_id) DO UPDATE SET
                display_name = $name,
                root_path = $path,
                updated_at_utc = $updated;
            """;
        command.Parameters.AddWithValue("$id", directory.DirectoryId);
        command.Parameters.AddWithValue("$name", directory.DisplayName);
        command.Parameters.AddWithValue("$path", directory.RootPath);
        command.Parameters.AddWithValue("$created", directory.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", directory.UpdatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SharedDirectoryRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT directory_id, display_name, root_path, created_at_utc, updated_at_utc FROM SharedDirectories ORDER BY created_at_utc;";

        var results = new List<SharedDirectoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapDirectory(reader));
        }

        return results;
    }

    public async Task<SharedDirectoryRecord?> GetAsync(string directoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT directory_id, display_name, root_path, created_at_utc, updated_at_utc FROM SharedDirectories WHERE directory_id = $id;";
        command.Parameters.AddWithValue("$id", directoryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapDirectory(reader) : null;
    }

    /// <summary>按本地绝对路径查找共享目录。添加目录时用来判重。</summary>
    public async Task<SharedDirectoryRecord?> FindByPathAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT directory_id, display_name, root_path, created_at_utc, updated_at_utc FROM SharedDirectories WHERE root_path = $path;";
        command.Parameters.AddWithValue("$path", rootPath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapDirectory(reader) : null;
    }

    /// <summary>移除共享目录，同时清掉它的全部设备授权——目录没了，授权没有意义。</summary>
    public async Task DeleteAsync(string directoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var deleteGrants = connection.CreateCommand())
        {
            deleteGrants.Transaction = transaction;
            deleteGrants.CommandText = "DELETE FROM SharedDirectoryGrants WHERE directory_id = $id;";
            deleteGrants.Parameters.AddWithValue("$id", directoryId);
            await deleteGrants.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteDirectory = connection.CreateCommand())
        {
            deleteDirectory.Transaction = transaction;
            deleteDirectory.CommandText = "DELETE FROM SharedDirectories WHERE directory_id = $id;";
            deleteDirectory.Parameters.AddWithValue("$id", directoryId);
            await deleteDirectory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 授权

    public async Task SetGrantAsync(
        string directoryId,
        string deviceId,
        SharedDirectoryPermissions permissions,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SharedDirectoryGrants (directory_id, device_id, permissions, created_at_utc, updated_at_utc)
            VALUES ($dir, $device, $perms, $created, $updated)
            ON CONFLICT(directory_id, device_id) DO UPDATE SET
                permissions = $perms,
                updated_at_utc = $updated;
            """;
        command.Parameters.AddWithValue("$dir", directoryId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$perms", (int)permissions);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>收回某台设备在某目录上的授权。</summary>
    public async Task DeleteGrantAsync(
        string directoryId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM SharedDirectoryGrants WHERE directory_id = $dir AND device_id = $device;";
        command.Parameters.AddWithValue("$dir", directoryId);
        command.Parameters.AddWithValue("$device", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取某设备在某目录上的权限位；无授权返回 <see cref="SharedDirectoryPermissions.None"/>。</summary>
    public async Task<SharedDirectoryPermissions> GetPermissionsAsync(
        string directoryId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT permissions FROM SharedDirectoryGrants WHERE directory_id = $dir AND device_id = $device;";
        command.Parameters.AddWithValue("$dir", directoryId);
        command.Parameters.AddWithValue("$device", deviceId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null
            ? SharedDirectoryPermissions.None
            : (SharedDirectoryPermissions)Convert.ToInt32(result);
    }

    /// <summary>列出某设备被授权的全部共享目录及其权限。</summary>
    public async Task<IReadOnlyList<(SharedDirectoryRecord Directory, SharedDirectoryPermissions Permissions)>> ListForDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.directory_id, d.display_name, d.root_path, d.created_at_utc, d.updated_at_utc, g.permissions
            FROM SharedDirectories d
            JOIN SharedDirectoryGrants g ON g.directory_id = d.directory_id
            WHERE g.device_id = $device AND g.permissions <> 0
            ORDER BY d.created_at_utc;
            """;
        command.Parameters.AddWithValue("$device", deviceId);

        var results = new List<(SharedDirectoryRecord, SharedDirectoryPermissions)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var directory = new SharedDirectoryRecord
            {
                DirectoryId = reader.GetString(0),
                DisplayName = reader.GetString(1),
                RootPath = reader.GetString(2),
                CreatedAtUtc = ParseTime(reader.GetString(3)),
                UpdatedAtUtc = ParseTime(reader.GetString(4)),
            };
            var permissions = (SharedDirectoryPermissions)reader.GetInt32(5);
            results.Add((directory, permissions));
        }

        return results;
    }

    /// <summary>列出某目录的全部授权，用于桌面端展示与编辑。</summary>
    public async Task<IReadOnlyList<(string DeviceId, SharedDirectoryPermissions Permissions)>> ListGrantsAsync(
        string directoryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT device_id, permissions FROM SharedDirectoryGrants WHERE directory_id = $dir ORDER BY device_id;";
        command.Parameters.AddWithValue("$dir", directoryId);

        var results = new List<(string, SharedDirectoryPermissions)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add((reader.GetString(0), (SharedDirectoryPermissions)reader.GetInt32(1)));
        }

        return results;
    }

    private static SharedDirectoryRecord MapDirectory(SqliteDataReader reader) => new()
    {
        DirectoryId = reader.GetString(0),
        DisplayName = reader.GetString(1),
        RootPath = reader.GetString(2),
        CreatedAtUtc = ParseTime(reader.GetString(3)),
        UpdatedAtUtc = ParseTime(reader.GetString(4)),
    };

    internal static DateTimeOffset ParseTime(string value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;
}
