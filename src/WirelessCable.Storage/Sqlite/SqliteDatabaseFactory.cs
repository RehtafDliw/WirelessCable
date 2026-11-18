using Microsoft.Data.Sqlite;

namespace WirelessCable.Storage.Sqlite;

/// <summary>
/// SQLite 连接工厂。统一开启 WAL、忙等待与外键约束，避免各处 PRAGMA 不一致。
/// </summary>
public sealed class SqliteDatabaseFactory
{
    private readonly string _connectionString;

    public SqliteDatabaseFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>打开连接并应用连接级 PRAGMA。调用方负责释放。</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>探测数据库是否可读写。健康检查用，不抛异常。</summary>
    public bool CanReadWrite()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
