using WirelessCable.Core.Security;
using WirelessCable.Infrastructure.Files;
using WirelessCable.Core.Domain;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Files;
using WirelessCable.Server.Transfer;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 整机浏览（"此电脑"）测试。
///
/// 用户要求：手机浏览电脑要能看**所有**文件，就像资源管理器里的"此电脑"，
/// 不能只看共享目录。因此这里验证：
/// <list type="bullet">
/// <item>整机根默认对已配对设备可见，列出本机盘符；</item>
/// <item>设备级开关关闭后立刻不可见、访问被拒；</item>
/// <item>整机**按档位**授权：默认可读写，只读档位下写操作一律拒绝；</item>
/// <item>下载走"根 id + 相对路径"，客户端不能指定绝对路径；</item>
/// <item>路径穿越、越权盘符一律拒绝。</item>
/// </list>
/// </summary>
public sealed class MachineBrowseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wc-machine-tests",
        Guid.NewGuid().ToString("N"));

    private readonly SqliteDatabaseFactory _database;
    private readonly SharedDirectoryRepository _directories;
    private readonly DeviceFileRepository _deviceFiles;
    private readonly FileService _service;
    private readonly TransferEngine _engine;

    private const string DeviceId = "device-a";
    private const string OtherDeviceId = "device-b";

    public MachineBrowseTests()
    {
        Directory.CreateDirectory(_root);

        _database = new SqliteDatabaseFactory(Path.Combine(_root, "test.db"));
        SchemaInitializer.Initialize(_database);

        _directories = new SharedDirectoryRepository(_database);
        _deviceFiles = new DeviceFileRepository(_database);
        _service = new FileService(
            _directories,
            _deviceFiles,
            root => new LocalFileProvider(root),
            new MachineFileProvider());

        Directory.CreateDirectory(Path.Combine(_root, "received"));
        Directory.CreateDirectory(Path.Combine(_root, "temp"));

        _engine = new TransferEngine(
            new TransferRepository(_database),
            new TransferOptions
            {
                SharedDirectory = Path.Combine(_root, "received"),
                TempDirectory = Path.Combine(_root, "temp"),
            },
            new SharedDirectoryResolver(_directories, Path.Combine(_root, "received")));
    }

    // ------------------------------------------------------------ 批量删除（整机根）

    [Fact]
    public async Task MachineRoot_BatchDelete_RemovesFile()
    {
        // 整机根的路径是"盘符:绝对路径"，这里用测试目录自己造一个文件
        var target = Path.Combine(_root, "to-delete.txt");
        await File.WriteAllTextAsync(target, "x");
        var machinePath = target.Replace('\\', '/');

        var result = await _service.DeleteBatchAsync(
            Device(DeviceId, machineAccess: MachineAccessLevels.ReadWriteDelete),
            new FileBatchRequestDto { RootId = FileRootIds.Machine, Paths = [machinePath], Recursive = false });

        Assert.True(result.Success);
        Assert.True(result.Value![0].Success);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task MachineRoot_BatchDelete_DeniedWithoutDeleteLevel()
    {
        // 以前整机根没走档位判断，会掉进共享目录分支报"该目录未授权"——
        // 手机删电脑文件时看到的就是那句误导性提示（1.4.4 修）
        var target = Path.Combine(_root, "keep.txt");
        await File.WriteAllTextAsync(target, "x");
        var machinePath = target.Replace('\\', '/');

        var result = await _service.DeleteBatchAsync(
            Device(DeviceId, machineAccess: MachineAccessLevels.ReadWrite),
            new FileBatchRequestDto { RootId = FileRootIds.Machine, Paths = [machinePath], Recursive = false });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, result.Error!.Code);
        Assert.Equal("machine_delete_denied", result.Error.ReasonCode);
        Assert.True(File.Exists(target));
    }

    // ------------------------------------------------------------ 可见性

    [Fact]
    public async Task MachineRoot_IsListedForPairedDevice()
    {
        var roots = await _service.ListRootsAsync(Device(DeviceId));

        var machine = roots.SingleOrDefault(r => r.Kind == FileRootKinds.Machine);
        Assert.NotNull(machine);
        Assert.Equal(FileRootIds.Machine, machine!.RootId);
        Assert.Equal("此电脑", machine.DisplayName);
    }

    [Fact]
    public async Task MachineRoot_Capabilities_AreReadWriteByDefault()
    {
        var roots = await _service.ListRootsAsync(Device(DeviceId));

        var machine = roots.Single(r => r.Kind == FileRootKinds.Machine);

        // 1.1.0 起默认"开放全部文件"：读写全开
        Assert.True(Has(machine.Capabilities, FileCapabilities.ListDirectory));
        Assert.True(Has(machine.Capabilities, FileCapabilities.ReadFile));
        Assert.True(Has(machine.Capabilities, FileCapabilities.GetFileInfo));
        Assert.True(Has(machine.Capabilities, FileCapabilities.WriteFile));
        Assert.True(Has(machine.Capabilities, FileCapabilities.CreateDirectory));
        Assert.True(Has(machine.Capabilities, FileCapabilities.Rename));
        Assert.True(Has(machine.Capabilities, FileCapabilities.Delete));
        Assert.True(Has(machine.Capabilities, FileCapabilities.Move));
    }

    [Fact]
    public async Task MachineRoot_Capabilities_DropWriteBitsWhenReadOnly()
    {
        var roots = await _service.ListRootsAsync(
            Device(DeviceId, machineAccess: MachineAccessLevels.ReadOnly));

        var machine = roots.Single(r => r.Kind == FileRootKinds.Machine);

        Assert.True(Has(machine.Capabilities, FileCapabilities.ReadFile));
        Assert.False(Has(machine.Capabilities, FileCapabilities.WriteFile));
        Assert.False(Has(machine.Capabilities, FileCapabilities.Delete));
    }

    [Fact]
    public async Task MachineRoot_IsHiddenWhenDeviceAccessDisabled()
    {
        var device = Device(DeviceId, machineAccess: MachineAccessLevels.None);

        var roots = await _service.ListRootsAsync(device);

        Assert.DoesNotContain(roots, r => r.Kind == FileRootKinds.Machine);
    }

    [Fact]
    public async Task MachineRoot_AccessDeniedWhenDeviceAccessDisabled()
    {
        var device = Device(DeviceId, machineAccess: MachineAccessLevels.None);

        var listing = await _service.ListAsync(device, FileRootIds.Machine, string.Empty);

        Assert.False(listing.Success);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, listing.Error!.Code);
        Assert.Equal(403, listing.Error.StatusCode);
    }

    [Fact]
    public async Task MachineRoot_ListsLocalDrives()
    {
        var listing = await _service.ListAsync(Device(DeviceId), FileRootIds.Machine, string.Empty);

        Assert.True(listing.Success);
        // 本机至少有 C: 盘
        Assert.Contains(listing.Value!.Entries, e => e.RelativePath.Equals("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MachineRoot_DriveEntries_AreDirectories()
    {
        var listing = await _service.ListAsync(Device(DeviceId), FileRootIds.Machine, string.Empty);

        Assert.True(listing.Success);
        Assert.All(listing.Value!.Entries, entry => Assert.True(entry.IsDirectory));
    }

    // ------------------------------------------------------------ 只读档位

    [Fact]
    public async Task MachineRoot_Write_IsRejectedWhenReadOnly()
    {
        var readOnly = Device(DeviceId, machineAccess: MachineAccessLevels.ReadOnly);

        var create = await _service.CreateDirectoryAsync(
            readOnly,
            new FileCreateDirectoryRequestDto { RootId = FileRootIds.Machine, Name = "hacked" });

        Assert.False(create.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, create.Error!.Code);
        Assert.Equal("machine_read_only", create.Error.ReasonCode);

        var delete = await _service.DeleteAsync(
            readOnly,
            new FileDeleteRequestDto { RootId = FileRootIds.Machine, Path = "C:/Windows" });

        Assert.False(delete.Success);
        Assert.Equal("machine_read_only", delete.Error!.ReasonCode);
    }

    [Fact]
    public async Task MachineRoot_Write_IsRejectedWhenAccessDisabled()
    {
        var none = Device(DeviceId, machineAccess: MachineAccessLevels.None);

        var result = await _service.DeleteAsync(
            none,
            new FileDeleteRequestDto { RootId = FileRootIds.Machine, Path = "C:/Windows" });

        Assert.False(result.Success);
        Assert.Equal(403, result.Error!.StatusCode);
    }

    [Fact]
    public async Task MachineRoot_Rename_IsRejectedWhenReadOnly()
    {
        var result = await _service.RenameAsync(
            Device(DeviceId, machineAccess: MachineAccessLevels.ReadOnly),
            new FileRenameRequestDto { RootId = FileRootIds.Machine, Path = "C:/a.txt", NewName = "b.txt" });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, result.Error!.Code);
    }

    [Fact]
    public async Task MachineRoot_Move_IsRejectedWhenReadOnly()
    {
        var result = await _service.MoveAsync(
            Device(DeviceId, machineAccess: MachineAccessLevels.ReadOnly),
            new FileMoveRequestDto
            {
                RootId = FileRootIds.Machine,
                SourcePath = "C:/a.txt",
                DestinationPath = "C:/b.txt",
            });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, result.Error!.Code);
    }

    // ------------------------------------------------------------ 下载路径解析

    [Fact]
    public async Task ResolveFilePath_MachineRoot_ResolvesExistingFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"wc-machine-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "hello");

        try
        {
            var drive = Path.GetPathRoot(tempFile)!;
            var relative = drive + tempFile[drive.Length..].Replace('\\', '/');

            var resolved = await _service.ResolveFilePathAsync(
                Device(DeviceId),
                FileRootIds.Machine,
                relative);

            Assert.True(resolved.Success);
            Assert.Equal(Path.GetFullPath(tempFile), resolved.Value, ignoreCase: true);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ResolveFilePath_MachineRoot_Traversal_IsRejected()
    {
        var resolved = await _service.ResolveFilePathAsync(
            Device(DeviceId),
            FileRootIds.Machine,
            "C:/Users/../../Windows/System32/config");

        Assert.False(resolved.Success);
        Assert.Equal(ErrorCodes.PathForbidden, resolved.Error!.Code);
    }

    [Fact]
    public async Task ResolveFilePath_MachineRoot_AccessDisabled_IsRejected()
    {
        var device = Device(DeviceId, machineAccess: MachineAccessLevels.None);

        var resolved = await _service.ResolveFilePathAsync(
            device,
            FileRootIds.Machine,
            "C:/Windows/win.ini");

        Assert.False(resolved.Success);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, resolved.Error!.Code);
    }

    [Fact]
    public async Task ResolveFilePath_MachineRoot_Directory_IsRejected()
    {
        var resolved = await _service.ResolveFilePathAsync(
            Device(DeviceId),
            FileRootIds.Machine,
            "C:/Windows");

        Assert.False(resolved.Success);
        Assert.Equal(ErrorCodes.NotFound, resolved.Error!.Code);
    }

    [Fact]
    public async Task ResolveFilePath_SharedDirectory_RequiresDownloadPermission()
    {
        var sharedRoot = Path.Combine(_root, "shared");
        Directory.CreateDirectory(sharedRoot);
        await File.WriteAllTextAsync(Path.Combine(sharedRoot, "a.txt"), "hello");

        await _directories.UpsertAsync(new SharedDirectoryRecord
        {
            DirectoryId = "dir-1",
            DisplayName = "共享",
            RootPath = sharedRoot,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        // 只给浏览权，不给下载权
        await _directories.SetGrantAsync(
            "dir-1",
            DeviceId,
            SharedDirectoryPermissions.Browse);

        var resolved = await _service.ResolveFilePathAsync(
            Device(DeviceId),
            "dir-1",
            "a.txt");

        Assert.False(resolved.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, resolved.Error!.Code);
    }

    [Fact]
    public async Task ResolveFilePath_SharedDirectory_WithDownloadPermission_Succeeds()
    {
        var sharedRoot = Path.Combine(_root, "shared");
        Directory.CreateDirectory(sharedRoot);
        await File.WriteAllTextAsync(Path.Combine(sharedRoot, "a.txt"), "hello");

        await _directories.UpsertAsync(new SharedDirectoryRecord
        {
            DirectoryId = "dir-1",
            DisplayName = "共享",
            RootPath = sharedRoot,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await _directories.SetGrantAsync(
            "dir-1",
            DeviceId,
            SharedDirectoryPermissions.Browse | SharedDirectoryPermissions.Download);

        var resolved = await _service.ResolveFilePathAsync(
            Device(DeviceId),
            "dir-1",
            "a.txt");

        Assert.True(resolved.Success);
        Assert.Equal(Path.Combine(sharedRoot, "a.txt"), resolved.Value, ignoreCase: true);
    }

    [Fact]
    public async Task ResolveFilePath_UngrantedDevice_IsRejected()
    {
        var resolved = await _service.ResolveFilePathAsync(
            Device(OtherDeviceId),
            "dir-not-exist",
            "a.txt");

        Assert.False(resolved.Success);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, resolved.Error!.Code);
    }

    // ------------------------------------------------------------ 提供器层

    [Fact]
    public async Task MachineProvider_ListsDrives()
    {
        var provider = new MachineFileProvider();

        var drives = await provider.GetRootDirectoriesAsync(CancellationToken.None);

        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.True(d.IsDirectory));
        Assert.Contains(drives, d => d.RelativePath.Equals("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MachineProvider_CreatesRenamesAndDeletesUnderTemp()
    {
        var provider = new MachineFileProvider();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var root = ToMachinePath(Path.Combine(Path.GetTempPath(), "wc-machine-" + stamp));
        var renamed = ToMachinePath(Path.Combine(Path.GetTempPath(), "wc-machine-" + stamp + "-2"));

        var create = await provider.CreateDirectoryAsync(root, CancellationToken.None);
        Assert.True(create.Success, create.Message);
        Assert.True(Directory.Exists(root));

        var rename = await provider.RenameAsync(root, Path.GetFileName(renamed), CancellationToken.None);
        Assert.True(rename.Success, rename.Message);
        Assert.True(Directory.Exists(renamed));

        var delete = await provider.DeleteAsync(renamed, recursive: true, CancellationToken.None);
        Assert.True(delete.Success, delete.Message);
        Assert.False(Directory.Exists(renamed));
    }

    [Fact]
    public async Task MachineProvider_RefusesDriveRootWrites()
    {
        var provider = new MachineFileProvider();

        // 删盘符根不是"权限"问题，是根本不提供
        var delete = await provider.DeleteAsync("C:", recursive: true, CancellationToken.None);
        Assert.False(delete.Success);
        Assert.Equal("path_invalid", delete.ReasonCode);

        var create = await provider.CreateDirectoryAsync("C:", CancellationToken.None);
        Assert.False(create.Success);
        Assert.Equal("path_invalid", create.ReasonCode);
    }

    [Fact]
    public async Task MachineProvider_Traversal_IsRejected()
    {
        var provider = new MachineFileProvider();

        var listing = await provider.ListDirectoryAsync("C:/Users/../../Windows", CancellationToken.None);

        Assert.False(listing.Success);
        Assert.Equal("path_traversal", listing.ReasonCode);
    }

    [Fact]
    public async Task MachineProvider_NetworkPath_IsRejected()
    {
        var provider = new MachineFileProvider();

        var listing = await provider.ListDirectoryAsync(@"\\server\share", CancellationToken.None);

        Assert.False(listing.Success);
    }

    [Fact]
    public async Task MachineProvider_NestedEntries_KeepDrivePrefix()
    {
        var provider = new MachineFileProvider();

        // 盘符根下的子项路径必须带盘符前缀，客户端才能原样回传
        var listing = await provider.ListDirectoryAsync("C:", CancellationToken.None);
        Assert.True(listing.Success);

        var first = listing.Value!.FirstOrDefault();
        Assert.NotNull(first);
        Assert.StartsWith("C:/", first!.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MachineProvider_DeepEntries_KeepFullPath()
    {
        var provider = new MachineFileProvider();

        var listing = await provider.ListDirectoryAsync("C:/Users", CancellationToken.None);
        if (!listing.Success)
        {
            // 该机器没有 Users 目录时跳过断言
            return;
        }

        Assert.All(
            listing.Value!,
            entry => Assert.StartsWith("C:/Users/", entry.RelativePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MachineProvider_Exists_ReportsRealState()
    {
        var provider = new MachineFileProvider();

        Assert.True(await provider.ExistsAsync("C:", CancellationToken.None));
        Assert.False(await provider.ExistsAsync("C:/wc-definitely-not-here-9d3f", CancellationToken.None));
    }

    // ------------------------------------------------------------ 上传落点（目录 id）

    [Fact]
    public async Task Upload_WithUnknownDirectory_IsRejected()
    {
        var result = await _engine.CreateUploadAsync(
            Device(DeviceId),
            new TransferStartRequestDto
            {
                FileName = "a.txt",
                TotalBytes = 10,
                DirectoryId = "no-such-dir",
            });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, result.Error!.Code);
    }

    [Fact]
    public async Task Upload_WithDirectoryWithoutUploadPermission_IsRejected()
    {
        var root = Path.Combine(_root, "shared");
        Directory.CreateDirectory(root);

        await _directories.UpsertAsync(new SharedDirectoryRecord
        {
            DirectoryId = "dir-no-upload",
            DisplayName = "只读目录",
            RootPath = root,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        // 只给浏览权，不给上传权
        await _directories.SetGrantAsync(
            "dir-no-upload",
            DeviceId,
            SharedDirectoryPermissions.Browse);

        var result = await _engine.CreateUploadAsync(
            Device(DeviceId),
            new TransferStartRequestDto
            {
                FileName = "a.txt",
                TotalBytes = 10,
                DirectoryId = "dir-no-upload",
            });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.OperationNotPermitted, result.Error!.Code);
    }

    [Fact]
    public async Task Upload_WithDirectoryAndUploadPermission_IsAccepted()
    {
        var root = Path.Combine(_root, "shared-upload");
        Directory.CreateDirectory(root);

        await _directories.UpsertAsync(new SharedDirectoryRecord
        {
            DirectoryId = "dir-upload",
            DisplayName = "可上传目录",
            RootPath = root,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        await _directories.SetGrantAsync(
            "dir-upload",
            DeviceId,
            SharedDirectoryPermissions.Browse | SharedDirectoryPermissions.Upload);

        var result = await _engine.CreateUploadAsync(
            Device(DeviceId),
            new TransferStartRequestDto
            {
                FileName = "a.txt",
                TotalBytes = 10,
                DirectoryId = "dir-upload",
            });

        Assert.True(result.Success, result.Error?.Message);
    }

    // ------------------------------------------------------------ 辅助

    private static bool Has(int capabilities, int flag) => (capabilities & flag) == flag;

    /// <summary>绝对路径 → 整机相对路径（<c>C:/Users/x</c>），与客户端回传的格式一致。</summary>
    private static string ToMachinePath(string absolute) => absolute.Replace('\\', '/').TrimEnd('/');

    private static StoredDevice Device(
        string deviceId,
        int machineAccess = MachineAccessLevels.Default) => new()
    {
        DeviceId = deviceId,
        DisplayName = deviceId == DeviceId ? "手机A" : "手机B",
        Platform = "android",
        IsPaired = true,
        MachineAccess = machineAccess,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        _engine.Dispose();
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
            // 清理失败不影响测试结论
        }
    }
}
