using WirelessCable.Infrastructure.Files;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Files;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 阶段 5 文件管理测试。
///
/// 重点是**安全边界**：越权访问、路径穿越、联接绕过、无权限设备、
/// 提供方不支持的操作，都必须被明确拒绝而不是"看起来成功"。
/// </summary>
public sealed class FileManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wc-fs-tests", Guid.NewGuid().ToString("N"));
    private readonly string _sharedRoot;
    private readonly SqliteDatabaseFactory _database;
    private readonly SharedDirectoryRepository _directories;
    private readonly DeviceFileRepository _deviceFiles;
    private readonly FileService _service;

    private const string DeviceId = "device-a";
    private const string OtherDeviceId = "device-b";
    private const string DirectoryId = "dir-1";

    public FileManagementTests()
    {
        Directory.CreateDirectory(_root);
        _sharedRoot = Path.Combine(_root, "shared");
        Directory.CreateDirectory(_sharedRoot);

        _database = new SqliteDatabaseFactory(Path.Combine(_root, "test.db"));
        SchemaInitializer.Initialize(_database);

        _directories = new SharedDirectoryRepository(_database);
        _deviceFiles = new DeviceFileRepository(_database);
        _service = new FileService(_directories, _deviceFiles, root => new LocalFileProvider(root));
    }

    // ------------------------------------------------------------ 授权矩阵

    [Fact]
    public async Task UngrantedDevice_CannotListDirectory()
    {
        // 目录只授权给手机 A
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadOnly, DeviceId);

        // 手机 B 没有任何授权
        var result = await _service.ListAsync(Device("b"), DirectoryId, string.Empty);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, result.Error!.Code);
        Assert.Equal(403, result.Error.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyDevice_CanListButNotDelete()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadOnly);
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "a.txt"), "hello");

        var list = await _service.ListAsync(Device("a"), DirectoryId, string.Empty);
        Assert.True(list.Success);
        Assert.Single(list.Value!.Entries);

        var delete = await _service.DeleteAsync(
            Device("a"),
            new FileDeleteRequestDto { RootId = DirectoryId, Path = "a.txt" });

        Assert.False(delete.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, delete.Error!.Code);
        Assert.True(File.Exists(Path.Combine(_sharedRoot, "a.txt")));
    }

    [Fact]
    public async Task ReadOnlyDevice_CannotCreateDirectory()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadOnly);

        var result = await _service.CreateDirectoryAsync(
            Device("a"),
            new FileCreateDirectoryRequestDto { RootId = DirectoryId, Name = "new" });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, result.Error!.Code);
        Assert.False(Directory.Exists(Path.Combine(_sharedRoot, "new")));
    }

    [Fact]
    public async Task Copy_CopiesFileAndDirectoryWithoutTouchingSource()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadWrite);
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_sharedRoot, "src", "inner"));
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "src", "inner", "deep.txt"), "deep");

        var fileCopy = await _service.CopyAsync(
            Device("a"),
            new FileCopyRequestDto
            {
                RootId = DirectoryId,
                SourcePath = "a.txt",
                DestinationPath = "b.txt",
            });

        Assert.True(fileCopy.Success);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_sharedRoot, "b.txt")));
        Assert.True(File.Exists(Path.Combine(_sharedRoot, "a.txt")));

        var directoryCopy = await _service.CopyAsync(
            Device("a"),
            new FileCopyRequestDto
            {
                RootId = DirectoryId,
                SourcePath = "src",
                DestinationPath = "dst",
            });

        Assert.True(directoryCopy.Success);
        Assert.Equal(
            "deep",
            await File.ReadAllTextAsync(Path.Combine(_sharedRoot, "dst", "inner", "deep.txt")));
        Assert.True(Directory.Exists(Path.Combine(_sharedRoot, "src", "inner")));
    }

    [Fact]
    public async Task Copy_RefusesToOverwrite()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadWrite);
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "a.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "b.txt"), "two");

        var result = await _service.CopyAsync(
            Device("a"),
            new FileCopyRequestDto
            {
                RootId = DirectoryId,
                SourcePath = "a.txt",
                DestinationPath = "b.txt",
            });

        Assert.False(result.Success);
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(_sharedRoot, "b.txt")));
    }

    [Fact]
    public async Task ReadOnlyDevice_CannotCopy()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadOnly);
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "a.txt"), "x");

        var result = await _service.CopyAsync(
            Device("a"),
            new FileCopyRequestDto
            {
                RootId = DirectoryId,
                SourcePath = "a.txt",
                DestinationPath = "b.txt",
            });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(_sharedRoot, "b.txt")));
    }

    [Fact]
    public async Task ReadWriteDevice_CanCreateRenameAndMove()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadWrite);

        var created = await _service.CreateDirectoryAsync(
            Device("a"),
            new FileCreateDirectoryRequestDto { RootId = DirectoryId, Name = "folder" });
        Assert.True(created.Success);
        Assert.True(Directory.Exists(Path.Combine(_sharedRoot, "folder")));

        var renamed = await _service.RenameAsync(
            Device("a"),
            new FileRenameRequestDto { RootId = DirectoryId, Path = "folder", NewName = "renamed" });
        Assert.True(renamed.Success);
        Assert.True(Directory.Exists(Path.Combine(_sharedRoot, "renamed")));

        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "m.txt"), "x");
        var moved = await _service.MoveAsync(
            Device("a"),
            new FileMoveRequestDto
            {
                RootId = DirectoryId,
                SourcePath = "m.txt",
                DestinationPath = "renamed/m.txt",
            });

        Assert.True(moved.Success);
        Assert.True(File.Exists(Path.Combine(_sharedRoot, "renamed", "m.txt")));
    }

    [Fact]
    public async Task DeviceWithoutDeletePermission_CannotDeleteEvenWithReadWrite()
    {
        // ReadWrite 不含 Delete 位
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadWrite);
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "keep.txt"), "x");

        var result = await _service.DeleteAsync(
            Device("a"),
            new FileDeleteRequestDto { RootId = DirectoryId, Path = "keep.txt" });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, result.Error!.Code);
        Assert.True(File.Exists(Path.Combine(_sharedRoot, "keep.txt")));
    }

    [Fact]
    public async Task DifferentDevices_HaveIndependentPermissions()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.ReadOnly, DeviceId);
        await _directories.SetGrantAsync(DirectoryId, OtherDeviceId, SharedDirectoryPermissions.All);

        // A 只读：可浏览不可删
        Assert.True((await _service.ListAsync(Device("a"), DirectoryId, string.Empty)).Success);
        Assert.False((await _service.DeleteAsync(
            Device("a"),
            new FileDeleteRequestDto { RootId = DirectoryId, Path = "x.txt" })).Success);

        // B 完全权限：可删
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "x.txt"), "x");
        Assert.True((await _service.DeleteAsync(
            Device("b"),
            new FileDeleteRequestDto { RootId = DirectoryId, Path = "x.txt" })).Success);
    }

    // ------------------------------------------------------------ 路径安全

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\config")]
    [InlineData("file.txt:stream")]
    public async Task TraversalAndAbsolutePaths_AreRejected(string attack)
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.All);

        var result = await _service.ListAsync(Device("a"), DirectoryId, attack);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PathForbidden, result.Error!.Code);
        Assert.Equal(403, result.Error.StatusCode);
    }

    [Fact]
    public async Task DeleteOutsideRoot_IsRejected()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.All);
        var outside = Path.Combine(_root, "outside.txt");
        await File.WriteAllTextAsync(outside, "secret");

        var result = await _service.DeleteAsync(
            Device("a"),
            new FileDeleteRequestDto { RootId = DirectoryId, Path = "../outside.txt" });

        Assert.False(result.Success);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task UnknownRoot_IsRejected()
    {
        var result = await _service.ListAsync(Device("a"), "no-such-dir", string.Empty);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, result.Error!.Code);
    }

    [Fact]
    public async Task Rename_ToExistingName_IsRejected()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.All);
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "b.txt"), "b");

        var result = await _service.RenameAsync(
            Device("a"),
            new FileRenameRequestDto { RootId = DirectoryId, Path = "a.txt", NewName = "b.txt" });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TargetAlreadyExists, result.Error!.Code);
        Assert.True(File.Exists(Path.Combine(_sharedRoot, "a.txt")));
    }

    [Fact]
    public async Task Rename_WithPathSeparator_IsRejected()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.All);
        await File.WriteAllTextAsync(Path.Combine(_sharedRoot, "a.txt"), "a");

        var result = await _service.RenameAsync(
            Device("a"),
            new FileRenameRequestDto { RootId = DirectoryId, Path = "a.txt", NewName = "../evil.txt" });

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task DeleteRoot_Itself_IsRejected()
    {
        await SeedDirectoryAsync(SharedDirectoryPermissions.All);

        var result = await _service.DeleteAsync(
            Device("a"),
            new FileDeleteRequestDto { RootId = DirectoryId, Path = string.Empty });

        Assert.False(result.Success);
        Assert.True(Directory.Exists(_sharedRoot));
    }

    // ------------------------------------------------------------ 手机目录

    [Fact]
    public async Task PublishedIndex_IsBrowsable()
    {
        var device = Device("a");
        var index = new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
            Entries =
            [
                new FileEntryDto { Name = "照片", RelativePath = "照片", IsDirectory = true },
                new FileEntryDto { Name = "a.txt", RelativePath = "a.txt", SizeBytes = 10 },
            ],
        };

        var published = await _service.PublishIndexAsync(device, index);
        Assert.True(published.Success);

        var listing = await _service.ListAsync(device, "root-1", string.Empty);
        Assert.True(listing.Success);
        Assert.Equal(2, listing.Value!.Entries.Count);

        var nested = await _service.ListAsync(device, "root-1", "照片");
        Assert.True(nested.Success);
        Assert.Empty(nested.Value!.Entries);
    }

    /// <summary>
    /// 两台手机都用同一个 rootId（整机根的 id 是固定字符串 "phone-all"）时，谁也不能把谁覆盖掉。
    ///
    /// 以前 root_id 直接当主键，`ON CONFLICT` 只更新名字和能力、不动 device_id，
    /// 第二台手机"上报成功"却永远不属于自己，电脑端「我的手机」里就看不到整机（1.5.9 真机）。
    /// </summary>
    [Fact]
    public async Task TwoDevices_SameRootId_DoNotOverwriteEachOther()
    {
        var a = Device("a");
        var b = Device("b");

        await _service.PublishIndexAsync(a, new DeviceFileIndexDto
        {
            RootId = "phone-all",
            DisplayName = "内部存储（整机）",
            Capabilities = FileCapabilities.All,
            Entries = [new FileEntryDto { Name = "a.txt", RelativePath = "a.txt", SizeBytes = 1 }],
        });

        await _service.PublishIndexAsync(b, new DeviceFileIndexDto
        {
            RootId = "phone-all",
            DisplayName = "内部存储（整机）",
            Capabilities = FileCapabilities.All,
            Entries = [new FileEntryDto { Name = "b.txt", RelativePath = "b.txt", SizeBytes = 2 }],
        });

        var rootsA = await _service.ListDeviceRootsAsync(a.DeviceId);
        var rootsB = await _service.ListDeviceRootsAsync(b.DeviceId);
        Assert.Single(rootsA);
        Assert.Single(rootsB);
        Assert.Equal("phone-all", rootsA[0].RootId);
        Assert.Equal("phone-all", rootsB[0].RootId);

        // 条目也不能串：各自只看得到自己上报的那一个
        var listA = await _service.ListAsync(a, "phone-all", string.Empty);
        var listB = await _service.ListAsync(b, "phone-all", string.Empty);
        Assert.True(listA.Success, listA.Error?.Message);
        Assert.True(listB.Success, listB.Error?.Message);
        Assert.Equal("a.txt", listA.Value!.Entries.Single().Name);
        Assert.Equal("b.txt", listB.Value!.Entries.Single().Name);

        // 撤销其中一台的授权，不能把另一台的一起清掉
        await _service.SyncIndexesAsync(b, new DeviceFileSyncDto { ActiveRootIds = [] });
        Assert.Single(await _service.ListDeviceRootsAsync(a.DeviceId));
        Assert.Empty(await _service.ListDeviceRootsAsync(b.DeviceId));
    }

    [Fact]
    public async Task OtherDevice_CannotBrowsePhoneRoot()
    {
        await _service.PublishIndexAsync(Device("a"), new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
        });

        var result = await _service.ListAsync(Device("b"), "root-1", string.Empty);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.Forbidden, result.Error!.Code);
    }

    [Fact]
    public async Task RepublishedIndex_ReplacesOldEntries()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
            Entries = [new FileEntryDto { Name = "old.txt", RelativePath = "old.txt" }],
        });

        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
            Entries = [new FileEntryDto { Name = "new.txt", RelativePath = "new.txt" }],
        });

        var listing = await _service.ListAsync(device, "root-1", string.Empty);
        Assert.True(listing.Success);
        Assert.Single(listing.Value!.Entries);
        Assert.Equal("new.txt", listing.Value!.Entries[0].Name);
    }

    [Fact]
    public async Task Job_ForUnsupportedOperation_IsRejected()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "只读目录",
            Capabilities = FileCapabilities.ListDirectory | FileCapabilities.ReadFile,
        });

        var result = await _service.EnqueueJobAsync(
            device, "root-1", FileOperations.Delete, "a.txt", null, null);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationUnsupported, result.Error!.Code);
    }

    [Fact]
    public async Task Jobs_AreTakenOnceAndCompleted()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
        });

        var created = await _service.EnqueueJobAsync(
            device, "root-1", FileOperations.Create, "sub", null, "新目录");
        Assert.True(created.Success);

        var taken = await _service.TakePendingJobsAsync(device);
        Assert.Single(taken);
        Assert.Equal(FileJobStatuses.Running, taken[0].Status);

        // 取出后不再重复下发
        var takenAgain = await _service.TakePendingJobsAsync(device);
        Assert.Empty(takenAgain);

        var completed = await _service.CompleteJobAsync(
            device,
            new FileJobResultDto { JobId = created.Value!.JobId, Status = FileJobStatuses.Completed });

        Assert.True(completed.Success);
        Assert.Equal(FileJobStatuses.Completed, completed.Value!.Status);
    }

    [Fact]
    public async Task OtherDevice_CannotCompleteJob()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
        });

        var created = await _service.EnqueueJobAsync(
            device, "root-1", FileOperations.Delete, "a.txt", null, null);

        var result = await _service.CompleteJobAsync(
            Device("b"),
            new FileJobResultDto { JobId = created.Value!.JobId, Status = FileJobStatuses.Completed });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.Forbidden, result.Error!.Code);
    }

    [Fact]
    public async Task Job_CapabilityIsCheckedPerOperationTarget()
    {
        var device = Device("a");

        // 根不支持新建（虚拟目录），但 sub 这个真实目录支持。
        var index = new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.ListDirectory | FileCapabilities.ReadFile,
            Entries =
            [
                new FileEntryDto
                {
                    Name = "内部存储",
                    RelativePath = string.Empty,
                    IsDirectory = true,
                    Capabilities = FileCapabilities.ListDirectory | FileCapabilities.ReadFile,
                },
                new FileEntryDto
                {
                    Name = "sub",
                    RelativePath = "sub",
                    IsDirectory = true,
                    Capabilities = FileCapabilities.All,
                },
            ],
        };

        await _service.PublishIndexAsync(device, index);

        // 在根层新建 → 根不支持 → 拒绝
        var atRoot = await _service.EnqueueJobAsync(
            device, "root-1", FileOperations.Create, string.Empty, null, "x");
        Assert.False(atRoot.Success);
        Assert.Equal(ErrorCodes.OperationUnsupported, atRoot.Error!.Code);

        // 在 sub 里新建 → sub 支持 → 放行
        var inSub = await _service.EnqueueJobAsync(
            device, "root-1", FileOperations.Create, "sub", null, "x");
        Assert.True(inSub.Success);
        Assert.Equal(FileJobStatuses.Pending, inSub.Value!.Status);
    }

    [Fact]
    public async Task RootEntry_IsListedAtTopLevel()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
            Entries =
            [
                new FileEntryDto { Name = "内部存储", RelativePath = string.Empty, IsDirectory = true },
                new FileEntryDto { Name = "a.txt", RelativePath = "a.txt" },
            ],
        });

        var listing = await _service.ListAsync(device, "root-1", string.Empty);

        Assert.True(listing.Success);
        Assert.Equal(2, listing.Value!.Entries.Count);

        var root = await _service.GetInfoAsync(device, "root-1", string.Empty);
        Assert.True(root.Success);
        Assert.NotNull(root.Value);
    }

    // ------------------------------------------------------------ 辅助

    private async Task SeedDirectoryAsync(SharedDirectoryPermissions permissions, string deviceId = DeviceId)
    {
        await _directories.UpsertAsync(new SharedDirectoryRecord
        {
            DirectoryId = DirectoryId,
            DisplayName = "共享",
            RootPath = _sharedRoot,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await _directories.SetGrantAsync(DirectoryId, deviceId, permissions);
    }

    // ------------------------------------------------------------ 索引快照语义

    /// <summary>
    /// 用户撤销一个授权目录后，服务端的快照必须跟着变小。
    ///
    /// 实测事故：在三星上撤销全部授权，电脑端「我的手机」和手机端「文件」
    /// 仍然各显示两条已经不存在的授权目录——因为上报只会 upsert，从不删除。
    /// </summary>
    [Fact]
    public async Task Publish_WithShrunkSnapshot_RemovesRevokedRoot()
    {
        var device = Device("a");

        await _service.PublishIndexAsync(device, Index("root-1", ["root-1", "root-2"]));
        await _service.PublishIndexAsync(device, Index("root-2", ["root-1", "root-2"]));
        Assert.Equal(2, (await _service.ListDeviceRootsAsync(DeviceId)).Count);

        // 用户撤销 root-2：下一次上报只带 root-1
        var published = await _service.PublishIndexAsync(device, Index("root-1", ["root-1"]));
        Assert.True(published.Success, published.Error?.Message);

        var roots = await _service.ListDeviceRootsAsync(DeviceId);
        Assert.Single(roots);
        Assert.Equal("root-1", roots[0].RootId);

        // 条目也要一起清，否则会留下孤儿行
        Assert.Empty(await _deviceFiles.ListEntriesAsync(DeviceId, "root-2", string.Empty));
    }

    /// <summary>全部撤销：手机没有索引可上报，靠 /index/sync 把根清空。</summary>
    [Fact]
    public async Task Sync_WithEmptySnapshot_RemovesAllRoots()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, Index("root-1", ["root-1", "root-2"]));
        await _service.PublishIndexAsync(device, Index("root-2", ["root-1", "root-2"]));

        var result = await _service.SyncIndexesAsync(device, new DeviceFileSyncDto
        {
            ActiveRootIds = Array.Empty<string>(),
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.Value!.RemovedRoots);
        Assert.Empty(await _service.ListDeviceRootsAsync(DeviceId));
    }

    /// <summary>老版本 App 不传 activeRootIds，不能因此把它的根全删了。</summary>
    [Fact]
    public async Task Publish_WithoutSnapshot_KeepsExistingRoots()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, Index("root-1", []));
        await _service.PublishIndexAsync(device, Index("root-2", []));

        var roots = await _service.ListDeviceRootsAsync(DeviceId);
        Assert.Equal(2, roots.Count);
    }

    private static DeviceFileIndexDto Index(string rootId, string[] activeRootIds) => new()
    {
        RootId = rootId,
        DisplayName = rootId,
        Capabilities = FileCapabilities.All,
        ActiveRootIds = activeRootIds,
        Entries =
        [
            new FileEntryDto { Name = "照片", RelativePath = "照片", IsDirectory = true },
            new FileEntryDto { Name = rootId + ".txt", RelativePath = rootId + ".txt" },
        ],
    };

    /// <summary>
    /// list 作业：电脑在索引没覆盖到的深目录里现场问手机一次，条目挂在结果里回来。
    /// 这是 1.3.0 退役反向服务的前提——没有它，超过索引深度的目录在电脑端永远是空的。
    /// </summary>
    [Fact]
    public async Task ListJob_CarriesDirectoryEntriesBack()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "内部存储",
            Capabilities = FileCapabilities.All,
        });

        // 这个路径没有被索引过（只在根上有一条记录），能力位回落到根
        var created = await _service.EnqueueJobAsync(
            device, "root-1", FileOperations.List, "a/b/c/d/e", null, null);
        Assert.True(created.Success);
        Assert.Equal(FileOperations.List, created.Value!.Operation);

        var taken = await _service.TakePendingJobsAsync(device);
        Assert.Single(taken);
        Assert.Equal(FileOperations.List, taken[0].Operation);

        const string payload =
            """[{"name":"deep.txt","relativePath":"a/b/c/d/e/deep.txt","isDirectory":false,"sizeBytes":12,"modifiedUnixMs":0,"capabilities":3}]""";

        var completed = await _service.CompleteJobAsync(device, new FileJobResultDto
        {
            JobId = created.Value.JobId,
            Status = FileJobStatuses.Completed,
            Payload = payload,
        });

        Assert.True(completed.Success);
        Assert.Equal(payload, completed.Value!.Payload);

        // 桌面端是另外查一次作业拿结果，必须能读到
        var refetched = await _service.GetJobAsync(created.Value.JobId);
        Assert.NotNull(refetched);
        Assert.Equal(payload, refetched!.Payload);
        Assert.Equal(FileJobStatuses.Completed, refetched.Status);
    }

    [Fact]
    public async Task ListJob_RejectedWhenRootCannotList()
    {
        var device = Device("a");
        await _service.PublishIndexAsync(device, new DeviceFileIndexDto
        {
            RootId = "root-1",
            DisplayName = "只读虚拟目录",
            // 只能读文件、不能列目录（部分云盘提供方就是这样）
            Capabilities = FileCapabilities.ReadFile,
        });

        var result = await _service.EnqueueJobAsync(
            device, "root-1", FileOperations.List, "some/deep/dir", null, null);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationUnsupported, result.Error!.Code);
    }

    private static StoredDevice Device(string suffix) => new()    {
        DeviceId = suffix == "a" ? DeviceId : OtherDeviceId,
        DisplayName = suffix == "a" ? "手机A" : "手机B",
        Platform = "android",
        IsPaired = true,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 残留不影响结论
        }
    }
}
