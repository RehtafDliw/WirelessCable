using Microsoft.Data.Sqlite;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Storage.Repositories;

/// <summary>AppConfig 键值仓储。用于持久化设备 ID、显示名、schema 版本等少量配置。</summary>
public sealed class AppConfigRepository
{
    private readonly SqliteDatabaseFactory _factory;

    public AppConfigRepository(SqliteDatabaseFactory factory) => _factory = factory;

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM AppConfig WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AppConfig (key, value, updated_at_utc)
            VALUES ($key, $value, $updated)
            ON CONFLICT(key) DO UPDATE SET value = $value, updated_at_utc = $updated;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取配置项，不存在则用工厂方法生成并落库。并发下以先写入者为准。</summary>
    public async Task<string> GetOrCreateAsync(string key, Func<string> factory, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        var created = factory();
        await SetAsync(key, created, cancellationToken).ConfigureAwait(false);

        var confirmed = await GetAsync(key, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(confirmed) ? created : confirmed;
    }
}
