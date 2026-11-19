using Microsoft.Data.Sqlite;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 解绑设备时的级联清理。
///
/// 2.10.0 之前 <c>DeviceRepository.DeleteAsync</c> 只删共享授权，文件索引快照
/// （<c>DeviceFileRoots</c> / <c>DeviceFileEntries</c>）会永远留在库里：
/// 实测两台历史手机留下 7400+ 行没人会再读的条目。
/// </summary>
public sealed class DeviceCleanupTests : IDisposable
{
    private const string DeviceId = "dev-cleanup-01";
    private const string RootId = "phone-all";

    private readonly string _directory;
    private readonly SqliteDatabaseFactory _database;

    public DeviceCleanupTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wc-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        _database = new SqliteDatabaseFactory(Path.Combine(_directory, "test.db"));
        SchemaInitializer.Initialize(_database);
    }

    public void Dispose()
    {
        // 连接池不释放会锁住文件，Windows 上删不掉临时目录
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task DeleteDevice_RemovesFileIndexes()
    {
        var devices = new DeviceRepository(_database);
        var files = new DeviceFileRepository(_database);
        var now = DateTimeOffset.UtcNow;

        await devices.UpsertAsync(new StoredDevice
        {
            DeviceId = DeviceId,
            DisplayName = "清理测试手机",
            Platform = "android",
            IsPaired = true,
            PairedAtUtc = now,
            CreatedAtUtc = now,
        });

        await files.UpsertRootAsync(new DeviceFileRootRecord
        {
            RootId = RootId,
            DeviceId = DeviceId,
            DisplayName = "内部存储（整机）",
            ProviderKind = "all",
            Capabilities = 255,
            GrantedAtUtc = now,
            UpdatedAtUtc = now,
        });

        await files.ReplaceEntriesAsync(DeviceId, RootId, new[]
        {
            new DeviceFileEntryRecord
            {
                RootId = RootId,
                RelativePath = string.Empty,
                DisplayName = "内部存储（整机）",
                IsDirectory = true,
                Capabilities = 255,
                UpdatedAtUtc = now,
            },
            new DeviceFileEntryRecord
            {
                RootId = RootId,
                RelativePath = "DCIM",
                DisplayName = "DCIM",
                IsDirectory = true,
                Capabilities = 255,
                UpdatedAtUtc = now,
            },
        });

        // 前置条件：根与条目都写进去了
        Assert.Single(await files.ListRootsAsync(DeviceId));
        Assert.NotEmpty(await files.ListEntriesAsync(DeviceId, RootId, string.Empty));

        await devices.DeleteAsync(DeviceId);

        // 设备行、根、条目必须一起消失
        Assert.Empty(await devices.ListAsync());
        Assert.Empty(await files.ListRootsAsync(DeviceId));
        Assert.Empty(await files.ListEntriesAsync(DeviceId, RootId, string.Empty));
    }

    [Fact]
    public async Task PruneOrphanRoots_RemovesIndexesOfMissingDevices()
    {
        var devices = new DeviceRepository(_database);
        var files = new DeviceFileRepository(_database);
        var now = DateTimeOffset.UtcNow;

        // 造一条"设备已经不在了"的残留：只写根和条目，不写 Devices 行
        await files.UpsertRootAsync(new DeviceFileRootRecord
        {
            RootId = RootId,
            DeviceId = "ghost-device",
            DisplayName = "内部存储（整机）",
            ProviderKind = "all",
            Capabilities = 255,
            GrantedAtUtc = now,
            UpdatedAtUtc = now,
        });

        await files.ReplaceEntriesAsync("ghost-device", RootId, new[]
        {
            new DeviceFileEntryRecord
            {
                RootId = RootId,
                RelativePath = "Download",
                DisplayName = "Download",
                IsDirectory = true,
                Capabilities = 255,
                UpdatedAtUtc = now,
            },
        });

        var removed = await files.PruneOrphanRootsAsync();

        Assert.True(removed >= 1);
        Assert.Empty(await files.ListRootsAsync("ghost-device"));
        Assert.Empty(await files.ListEntriesAsync("ghost-device", RootId, string.Empty));

        // 正常设备不受影响
        await devices.UpsertAsync(new StoredDevice
        {
            DeviceId = DeviceId,
            DisplayName = "正常手机",
            Platform = "android",
            IsPaired = true,
            CreatedAtUtc = now,
        });

        await files.UpsertRootAsync(new DeviceFileRootRecord
        {
            RootId = RootId,
            DeviceId = DeviceId,
            DisplayName = "内部存储（整机）",
            ProviderKind = "all",
            Capabilities = 255,
            GrantedAtUtc = now,
            UpdatedAtUtc = now,
        });

        await files.PruneOrphanRootsAsync();

        Assert.Single(await files.ListRootsAsync(DeviceId));
    }
}
