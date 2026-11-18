using System.Security.Cryptography;
using System.Text;
using WirelessCable.Infrastructure.Paths;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Transfer;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 传输引擎的真实文件测试：不走 HTTP，直接调引擎，验证临时文件、哈希、改名与失败清理。
/// 所有数据都是真实文件落盘，不做任何 mock（磁盘空间探针除外，它被显式替换以验证 507 分支）。
/// </summary>
public sealed class TransferEngineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.Combine("D:\\", "tools", "WorkBuddySpace", "WirelessCable", ".testtmp"),
        "transfer-" + Guid.NewGuid().ToString("N"));

    private SqliteDatabaseFactory _database = null!;
    private TransferRepository _repository = null!;
    private TransferEngine _engine = null!;
    private StoredDevice _device = null!;
    private FakeFreeSpaceProbe _freeSpace = null!;

    private string SharedDirectory => Path.Combine(_root, "shared");
    private string TempDirectory => Path.Combine(_root, "temp");

    /// <summary>手机上传的默认落点：共享目录 / 设备名 / 文件名。</summary>
    private string ReceivePath(params string[] parts)
        => Path.Combine([SharedDirectory, _device.DisplayName, .. parts]);

    /// <summary>预置文件用：先把设备子目录建出来，再返回文件路径。</summary>
    private string PrepareReceivePath(params string[] parts)
    {
        Directory.CreateDirectory(Path.Combine(SharedDirectory, _device.DisplayName));
        return ReceivePath(parts);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(SharedDirectory);
        Directory.CreateDirectory(TempDirectory);

        _database = new SqliteDatabaseFactory(Path.Combine(_root, "test.db"));
        SchemaInitializer.Initialize(_database);
        _repository = new TransferRepository(_database);
        _freeSpace = new FakeFreeSpaceProbe();

        _engine = new TransferEngine(_repository, new TransferOptions
        {
            SharedDirectory = SharedDirectory,
            TempDirectory = TempDirectory,
            FreeSpaceProbe = _freeSpace,
        });

        _device = NewDevice("设备甲");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
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
            // 测试目录残留不影响结论
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ 基本往返

    [Theory]
    [InlineData(1 * 1024)]
    [InlineData(1024 * 1024)]
    [InlineData(7 * 1024 * 1024 + 123)]
    public async Task Upload_SmallAndMediumFiles_ByteIdentical(int size)
    {
        var content = RandomBytes(size);

        var task = await CreateUploadAsync(content, $"file-{size}.bin");
        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));

        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal(TransferStates.Completed, completed.Value!.State);
        Assert.Equal(size, completed.Value.SizeBytes);

        var target = Path.Combine(SharedDirectory, completed.Value.RelativePath.Replace('/', '\\'));
        Assert.True(File.Exists(target));
        Assert.Equal(Sha256(content), Sha256File(target));
    }

    [Fact]
    public async Task Upload_EmptyFile_CompletesWithoutChunks()
    {
        var task = await CreateUploadAsync([], "empty.txt");

        Assert.Equal(0, task.ChunkCount);
        Assert.Equal(0, task.TotalBytes);

        var completed = await CompleteAsync(task.TaskId, Sha256([]));
        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal(0, new FileInfo(ReceivePath("empty.txt")).Length);
    }

    [Fact]
    public async Task Upload_ChineseAndSpecialCharacters_KeepsName()
    {
        const string name = "季度 报表-最终版 #2 (副本).txt";
        var content = Encoding.UTF8.GetBytes("中文内容 with spaces & symbols %$@");

        var task = await CreateUploadAsync(content, name);
        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));

        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal(name, completed.Value!.FileName);
        Assert.True(File.Exists(ReceivePath(name)));
    }

    [Fact]
    public async Task Upload_SubDirectory_IsCreatedUnderSharedRoot()
    {
        var content = RandomBytes(4096);
        var task = await CreateUploadAsync(content, "a.bin", relativeDirectory: "手机上传/2026-11");

        Assert.True(task.RelativePath.StartsWith("手机上传/2026-11/", StringComparison.Ordinal));

        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));

        Assert.True(completed.Success, completed.Error?.Message);
        Assert.True(Directory.Exists(Path.Combine(SharedDirectory, "手机上传", "2026-11")));
    }

    // ------------------------------------------------------------ 命名与覆盖

    /// <summary>默认落点按设备名分子目录：两台手机同时上传，用户不用猜文件是谁的。</summary>
    [Fact]
    public async Task Upload_DefaultTarget_GoesIntoDeviceFolder()
    {
        var content = RandomBytes(2048);
        var task = await CreateUploadAsync(content, "note.txt");

        Assert.StartsWith(_device.DisplayName + "/", task.RelativePath, StringComparison.Ordinal);
        Assert.Equal(_device.DisplayName, task.RelativePath.Split('/')[0]);

        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));

        Assert.True(completed.Success, completed.Error?.Message);
        Assert.True(File.Exists(ReceivePath("note.txt")));
    }

    /// <summary>设备名来自手机自报（型号）或用户重命名，可能带非法字符，必须清洗后才能当目录名。</summary>
    [Fact]
    public async Task Upload_DeviceNameWithInvalidChars_IsSanitized()
    {
        var device = NewDevice("客厅/手机:1*");
        var content = RandomBytes(1024);

        var created = await _engine.CreateUploadAsync(device, new TransferStartRequestDto
        {
            FileName = "a.bin",
            TotalBytes = content.Length,
            Sha256 = Sha256(content),
        });

        Assert.True(created.Success, created.Error?.Message);
        Assert.StartsWith("客厅_手机_1_/", created.Value!.RelativePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// 删除设备时清理它的传输记录：任务行与分块行都删，另一台设备不受影响，
    /// 已经接收的文件必须留在磁盘上——用户删的是设备，不是自己的文件。
    /// </summary>
    [Fact]
    public async Task PurgeDeviceRecords_RemovesTasksAndChunks_KeepsReceivedFiles()
    {
        var content = RandomBytes(4096);
        var task = await CreateUploadAsync(content, "keep-me.txt");
        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));
        Assert.True(completed.Success, completed.Error?.Message);

        var other = NewDevice("另一台设备");
        var otherTask = await _engine.CreateUploadAsync(other, new TransferStartRequestDto
        {
            FileName = "other.txt",
            TotalBytes = content.Length,
            Sha256 = Sha256(content),
        });
        Assert.True(otherTask.Success, otherTask.Error?.Message);

        var received = ReceivePath("keep-me.txt");
        Assert.True(File.Exists(received));

        var purged = await _engine.PurgeDeviceRecordsAsync(_device.DeviceId);

        Assert.Equal(1, purged);
        Assert.Empty(await _repository.ListForDeviceAsync(_device.DeviceId, 100));
        Assert.Single(await _repository.ListForDeviceAsync(other.DeviceId, 100));
        Assert.Empty(await _repository.ListChunksAsync(task.TaskId));

        Assert.True(File.Exists(received));
        Assert.Equal(content, await File.ReadAllBytesAsync(received));
    }

    /// <summary>
    /// 老版本删设备留下的一批"无主"记录：设备行已不在，任务行还在。
    /// 启动时统一收尾，只删记录，不动文件。
    /// </summary>
    [Fact]
    public async Task PurgeOrphanDeviceRecords_RemovesRecordsButKeepsFiles()
    {
        var content = RandomBytes(2048);
        var task = await CreateUploadAsync(content, "orphan.txt");
        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));
        Assert.True(completed.Success, completed.Error?.Message);

        var received = ReceivePath("orphan.txt");
        Assert.True(File.Exists(received));

        // 测试库的 Devices 表是空的，这条任务所属设备当然"不存在"
        var purged = await _engine.PurgeOrphanDeviceRecordsAsync();

        Assert.Equal(1, purged);
        Assert.Empty(await _repository.ListForDeviceAsync(_device.DeviceId, 100));
        Assert.Empty(await _repository.ListChunksAsync(task.TaskId));
        Assert.True(File.Exists(received));
    }

    /// <summary>设备名是路径穿越串时不能借它把文件挪出共享目录。</summary>
    [Fact]
    public async Task Upload_DeviceNameWithDotDot_StaysInsideSharedRoot()
    {
        var device = NewDevice("..");
        var content = RandomBytes(512);

        var created = await _engine.CreateUploadAsync(device, new TransferStartRequestDto
        {
            FileName = "escape.bin",
            TotalBytes = content.Length,
            Sha256 = Sha256(content),
        });

        Assert.True(created.Success, created.Error?.Message);

        // 名字退化成兜底值，路径只有一层，且解析回共享根内
        var relative = created.Value!.RelativePath;
        Assert.Equal(1, relative.Split('/').Length - 1);
        Assert.DoesNotContain("..", relative, StringComparison.Ordinal);

        var full = Path.GetFullPath(Path.Combine(SharedDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.StartsWith(Path.GetFullPath(SharedDirectory), full, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_ReservedDeviceName_FallsBackToDeviceIdPrefix()
    {
        var device = NewDevice("CON");
        var content = RandomBytes(256);

        var created = await _engine.CreateUploadAsync(device, new TransferStartRequestDto
        {
            FileName = "x.bin",
            TotalBytes = content.Length,
            Sha256 = Sha256(content),
        });

        Assert.True(created.Success, created.Error?.Message);
        Assert.StartsWith("设备-" + device.DeviceId[..8] + "/", created.Value!.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_BlankDeviceName_UsesFallbackFolder()
    {
        var device = NewDevice("   ");
        var content = RandomBytes(128);

        var created = await _engine.CreateUploadAsync(device, new TransferStartRequestDto
        {
            FileName = "y.bin",
            TotalBytes = content.Length,
            Sha256 = Sha256(content),
        });

        Assert.True(created.Success, created.Error?.Message);
        Assert.StartsWith("设备/", created.Value!.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_NameConflict_RenamesAndKeepsOriginal()
    {
        var original = PrepareReceivePath("report.txt");
        await File.WriteAllTextAsync(original, "已存在的内容");

        var content = Encoding.UTF8.GetBytes("新内容");
        var task = await CreateUploadAsync(content, "report.txt");

        Assert.Equal("report (1).txt", task.FileName);

        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));

        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal("已存在的内容", await File.ReadAllTextAsync(original));
        Assert.Equal("新内容", await File.ReadAllTextAsync(ReceivePath("report (1).txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("CON.txt")]
    [InlineData("bad:name.txt")]
    [InlineData("trailing. ")]
    [InlineData("")]
    public async Task Upload_IllegalFileName_Rejected(string name)
    {
        var result = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = name,
            TotalBytes = 1,
        });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidFileName, result.Error!.Code);
        Assert.Equal(400, result.Error.StatusCode);
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("C:\\Windows")]
    [InlineData("/etc")]
    public async Task Upload_DirectoryEscape_Rejected(string directory)
    {
        var result = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "ok.txt",
            RelativeDirectory = directory,
            TotalBytes = 1,
        });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PathForbidden, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(SharedDirectory));
    }

    // ------------------------------------------------------------ 分块规则

    [Fact]
    public async Task Upload_OutOfOrderChunk_AcceptedButCompletionRequiresAll()
    {
        // 阶段 4 起分块任意顺序：续传只补差集。缺块时不能 complete。
        var content = RandomBytes(200 * 1024);
        var task = await CreateUploadAsync(content, "order.bin", requestedChunkSize: 64 * 1024);
        Assert.True(task.ChunkCount > 1);

        using var second = new MemoryStream(content, 64 * 1024, 64 * 1024, writable: false);
        var ack = await _engine.WriteChunkAsync(
            _device, task.TaskId, 1, second, 64 * 1024, Sha256(content[^(200 * 1024 - 64 * 1024)..][..(64 * 1024)]));

        Assert.True(ack.Success, ack.Error?.Message);
        Assert.Equal(0, ack.Value!.NextIndex); // 最小未完成序号仍是 0
        Assert.Equal(64 * 1024, ack.Value.TransferredBytes);

        // 缺第 0 块：complete 必须拒绝
        var premature = await _engine.CompleteUploadAsync(_device, task.TaskId, Sha256(content));
        Assert.False(premature.Success);
        Assert.Equal(ErrorCodes.TransferStateInvalid, premature.Error!.Code);

        // 补齐后可以完成
        using var first = new MemoryStream(content, 0, 64 * 1024, writable: false);
        var firstAck = await _engine.WriteChunkAsync(
            _device, task.TaskId, 0, first, 64 * 1024, Sha256(content[..(64 * 1024)]));
        Assert.True(firstAck.Success, firstAck.Error?.Message);

        await WriteAllChunksAsync(task, content, startIndex: 2);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));
        Assert.True(completed.Success, completed.Error?.Message);
    }

    [Fact]
    public async Task Upload_ChunkMissingHashHeader_Rejected()
    {
        var content = RandomBytes(1000);
        var task = await CreateUploadAsync(content, "nohdr.bin");

        using var body = new MemoryStream(content);
        var result = await _engine.WriteChunkAsync(_device, task.TaskId, 0, body, content.Length, null);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task Upload_ChunkHashMismatch_RejectedAndChunkStaysPending()
    {
        // 分块数据在链路上损坏：哈希不符必须拒收，且该块保持未完成可重传
        var content = RandomBytes(128 * 1024);
        var task = await CreateUploadAsync(content, "corrupt-chunk.bin", requestedChunkSize: 64 * 1024);

        using var bad = new MemoryStream(content, 0, 64 * 1024, writable: false);
        var wrongSha = Sha256(RandomBytes(64 * 1024));
        var rejected = await _engine.WriteChunkAsync(_device, task.TaskId, 0, bad, 64 * 1024, wrongSha);

        Assert.False(rejected.Success);
        Assert.Equal(ErrorCodes.ChunkChecksumMismatch, rejected.Error!.Code);
        Assert.True(rejected.Error.Retryable);

        var status = await _engine.GetAsync(_device, task.TaskId);
        Assert.Equal(0, status.Value!.TransferredBytes);
        Assert.Empty(status.Value.CompletedChunks);

        // 原样重传同一块：正常接收并覆盖垃圾数据
        await WriteAllChunksAsync(task, content);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));
        Assert.True(completed.Success, completed.Error?.Message);
    }

    [Fact]
    public async Task Upload_DuplicateChunk_IdempotentWithoutRewrite()
    {
        var content = RandomBytes(128 * 1024);
        var task = await CreateUploadAsync(content, "dup.bin", requestedChunkSize: 64 * 1024);

        var chunkSha = Sha256(content[..(64 * 1024)]);

        using var first = new MemoryStream(content, 0, 64 * 1024, writable: false);
        var ack1 = await _engine.WriteChunkAsync(_device, task.TaskId, 0, first, 64 * 1024, chunkSha);
        Assert.True(ack1.Success, ack1.Error?.Message);
        Assert.False(ack1.Value!.AlreadyCompleted);
        Assert.Equal(64 * 1024, ack1.Value.TransferredBytes);

        // 同哈希重发：幂等回执，不重复写入、不重复计数
        using var again = new MemoryStream(content, 0, 64 * 1024, writable: false);
        var ack2 = await _engine.WriteChunkAsync(_device, task.TaskId, 0, again, 64 * 1024, chunkSha);
        Assert.True(ack2.Success, ack2.Error?.Message);
        Assert.True(ack2.Value!.AlreadyCompleted);
        Assert.Equal(64 * 1024, ack2.Value.TransferredBytes);

        // 不同哈希重发：说明客户端数据与已落盘不一致，拒绝
        using var third = new MemoryStream(content, 0, 64 * 1024, writable: false);
        var ack3 = await _engine.WriteChunkAsync(
            _device, task.TaskId, 0, third, 64 * 1024, Sha256(RandomBytes(64 * 1024)));
        Assert.False(ack3.Success);
        Assert.Equal(ErrorCodes.ChunkChecksumMismatch, ack3.Error!.Code);
    }

    [Fact]
    public async Task Upload_WrongChunkLength_Rejected()
    {
        var content = RandomBytes(1000);
        var task = await CreateUploadAsync(content, "len.bin");

        using var shortBody = new MemoryStream(content, 0, 500, writable: false);
        var result = await _engine.WriteChunkAsync(
            _device, task.TaskId, 0, shortBody, 500, Sha256(content[..500]));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
        Assert.Contains("expected=1000", result.Error.Details!);
    }

    [Fact]
    public async Task Upload_CompleteBeforeAllBytes_Fails()
    {
        var content = RandomBytes(200 * 1024);
        var task = await CreateUploadAsync(content, "partial.bin", requestedChunkSize: 64 * 1024);

        using var first = new MemoryStream(content, 0, 64 * 1024, writable: false);
        Assert.True((await _engine.WriteChunkAsync(
            _device, task.TaskId, 0, first, 64 * 1024, Sha256(content[..(64 * 1024)]))).Success);

        var completed = await _engine.CompleteUploadAsync(_device, task.TaskId, Sha256(content));

        Assert.False(completed.Success);
        Assert.Equal(ErrorCodes.TransferStateInvalid, completed.Error!.Code);
        Assert.Equal("incomplete", completed.Error.Details);
    }

    // ------------------------------------------------------------ 校验失败

    [Fact]
    public async Task Upload_ChecksumMismatch_FailsAndLeavesNoFile()
    {
        var content = RandomBytes(300 * 1024);
        var task = await CreateUploadAsync(content, "corrupt.bin");
        await WriteAllChunksAsync(task, content);

        var wrongSha = Sha256(RandomBytes(32));
        var completed = await _engine.CompleteUploadAsync(_device, task.TaskId, wrongSha);

        Assert.False(completed.Success);
        Assert.Equal(ErrorCodes.ChecksumMismatch, completed.Error!.Code);

        Assert.False(File.Exists(ReceivePath("corrupt.bin")));
        Assert.Empty(Directory.GetFiles(TempDirectory, "*.wcpart"));

        var status = await _engine.GetAsync(_device, task.TaskId);
        Assert.Equal(TransferStates.Failed, status.Value!.State);
    }

    [Fact]
    public async Task Upload_OtherDeviceCannotWrite_Fails403()
    {
        var content = RandomBytes(1024);
        var task = await CreateUploadAsync(content, "owned.bin");

        using var body = new MemoryStream(content);
        var result = await _engine.WriteChunkAsync(
            NewDevice("设备乙"), task.TaskId, 0, body, content.Length, Sha256(content));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.Forbidden, result.Error!.Code);
    }

    // ------------------------------------------------------------ 删除记录

    [Fact]
    public async Task DeleteRecords_KeepsFileWhenNotRequested()
    {
        var content = RandomBytes(4096);
        var task = await CreateUploadAsync(content, "keep-me.bin");
        await WriteAllChunksAsync(task, content);
        await CompleteAsync(task.TaskId, Sha256(content));

        var result = await _engine.DeleteRecordsAsync(_device, [task.TaskId], deleteFiles: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.Value!.Deleted);
        Assert.Equal(0, result.Value.FilesDeleted);
        Assert.True(File.Exists(ReceivePath("keep-me.bin")));
        Assert.Null(await _repository.GetAsync(task.TaskId));
    }

    [Fact]
    public async Task DeleteRecords_WithFiles_RemovesReceivedFile()
    {
        var content = RandomBytes(4096);
        var task = await CreateUploadAsync(content, "drop-me.bin");
        await WriteAllChunksAsync(task, content);
        await CompleteAsync(task.TaskId, Sha256(content));

        var result = await _engine.DeleteRecordsAsync(_device, [task.TaskId], deleteFiles: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.Value!.Deleted);
        Assert.Equal(1, result.Value.FilesDeleted);
        Assert.False(File.Exists(ReceivePath("drop-me.bin")));
        Assert.Null(await _repository.GetAsync(task.TaskId));
    }

    [Fact]
    public async Task DeleteRecords_RefusesTaskStillTransferring()
    {
        var task = await CreateUploadAsync(RandomBytes(4096), "busy.bin");

        var result = await _engine.DeleteRecordsAsync(_device, [task.TaskId], deleteFiles: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0, result.Value!.Deleted);
        Assert.Single(result.Value.Rejected);
        Assert.NotNull(await _repository.GetAsync(task.TaskId));
    }

    [Fact]
    public async Task DeleteRecords_NeverDeletesDownloadSourceFile()
    {
        // 电脑发出去的原文件属于用户数据：哪怕它就在共享目录里、也勾了"删文件"，都不能删
        var source = PrepareReceivePath("source.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(1024));

        var taskId = Guid.NewGuid().ToString("N");
        await _repository.InsertAsync(new TransferTaskRecord
        {
            TaskId = taskId,
            Direction = TransferDirections.Download,
            DeviceId = _device.DeviceId,
            FileName = "source.bin",
            RelativePath = "source.bin",
            TotalBytes = 1024,
            TransferredBytes = 1024,
            ChunkSize = ProtocolConstants.ChunkSizeBytes,
            ChunkCount = 1,
            Status = TransferStates.Completed,
            LocalPath = source,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var result = await _engine.DeleteRecordsAsync(_device, [taskId], deleteFiles: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.Value!.Deleted);
        Assert.Equal(0, result.Value.FilesDeleted);
        Assert.True(File.Exists(source));
    }

    // ------------------------------------------------------------ 取消与回收

    [Fact]
    public async Task Cancel_DeletesTempAndMarksCancelled()
    {
        var content = RandomBytes(128 * 1024);
        var task = await CreateUploadAsync(content, "cancel.bin", requestedChunkSize: 64 * 1024);
        Assert.Equal(2, task.ChunkCount);

        using var first = new MemoryStream(content, 0, 64 * 1024, writable: false);
        var ack = await _engine.WriteChunkAsync(
            _device, task.TaskId, 0, first, 64 * 1024, Sha256(content[..(64 * 1024)]));
        Assert.True(ack.Success, ack.Error?.Message);
        Assert.Single(Directory.GetFiles(TempDirectory, "*.wcpart"));

        var cancelled = await _engine.CancelAsync(_device, task.TaskId);

        Assert.True(cancelled.Success);
        Assert.Equal(TransferStates.Cancelled, cancelled.Value!.State);
        Assert.Empty(Directory.GetFiles(TempDirectory, "*.wcpart"));
        Assert.Empty(Directory.GetFiles(SharedDirectory));

        // 取消后不能再写
        using var again = new MemoryStream(content, 64 * 1024, 64 * 1024, writable: false);
        var write = await _engine.WriteChunkAsync(
            _device, task.TaskId, 1, again, 64 * 1024, Sha256(content[(64 * 1024)..]));
        Assert.False(write.Success);
    }

    [Fact]
    public async Task Upload_RequestedChunkSize_ClampedToProtocolRange()
    {
        var small = await CreateUploadAsync(new byte[1024], "clamp-small.bin", requestedChunkSize: 1024);
        Assert.Equal(ProtocolConstants.MinChunkSizeBytes, small.ChunkSize);

        var large = await CreateUploadAsync(new byte[1024], "clamp-large.bin",
            requestedChunkSize: 64 * 1024 * 1024);
        Assert.Equal(ProtocolConstants.ChunkSizeBytes, large.ChunkSize);
    }

    [Fact]
    public async Task IdleTask_InterruptedThenExpiredBySweeper()
    {
        // 阶段 4 起停滞任务先转 interrupted（数据保留可恢复），超过保留期才判失败清理
        using var engine = new TransferEngine(_repository, new TransferOptions
        {
            SharedDirectory = SharedDirectory,
            TempDirectory = TempDirectory,
            FreeSpaceProbe = _freeSpace,
            TaskIdleTimeout = TimeSpan.FromMilliseconds(200),
            InterruptedRetention = TimeSpan.FromMilliseconds(800),
            JanitorInterval = TimeSpan.FromMilliseconds(100),
        });

        var task = (await engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "idle.bin",
            TotalBytes = 10,
        })).Value!;

        await Task.Delay(600);

        var midway = await engine.GetAsync(_device, task.TaskId);
        Assert.Equal(TransferStates.Interrupted, midway.Value!.State);
        // 中断期间数据保留：临时文件与分块位图都还在
        Assert.Single(Directory.GetFiles(TempDirectory, "*.wcpart"));

        await Task.Delay(1200);

        var expired = await engine.GetAsync(_device, task.TaskId);
        Assert.Equal(TransferStates.Failed, expired.Value!.State);
        Assert.Equal(ErrorCodes.TransferTimeout, expired.Value.ErrorCode);
        Assert.Empty(Directory.GetFiles(TempDirectory, "*.wcpart"));
    }

    [Fact]
    public async Task InsufficientStorage_RejectsBeforeWritingAnything()
    {
        _freeSpace.AvailableBytes = 1024;

        var result = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "big.bin",
            TotalBytes = 10 * 1024 * 1024,
        });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InsufficientStorage, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(TempDirectory));
    }

    [Fact]
    public async Task TooManyActiveTransfers_Rejected()
    {
        for (var i = 0; i < ProtocolConstants.MaxActiveTransfersPerDevice; i++)
        {
            var ok = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
            {
                FileName = $"pending-{i}.bin",
                TotalBytes = 10,
            });
            Assert.True(ok.Success, ok.Error?.Message);
        }

        var rejected = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "extra.bin",
            TotalBytes = 10,
        });

        Assert.False(rejected.Success);
        Assert.Equal(ErrorCodes.TooManyActiveTransfers, rejected.Error!.Code);
        Assert.True(rejected.Error.Retryable);
    }

    // ------------------------------------------------------------ 下载

    [Fact]
    public async Task Download_NoProgress_InterruptedThenExpired()
    {
        // 真机断网场景：手机取走第一块后链路断了，服务端收不到回执。
        // 阶段 4 起先转 interrupted（客户端可以续传），超过保留期才判失败。
        var source = Path.Combine(_root, "stalled.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(4096));

        using var engine = new TransferEngine(_repository, new TransferOptions
        {
            SharedDirectory = SharedDirectory,
            TempDirectory = TempDirectory,
            FreeSpaceProbe = _freeSpace,
            InactivityTimeout = TimeSpan.FromMilliseconds(200),
            InterruptedRetention = TimeSpan.FromMilliseconds(800),
            JanitorInterval = TimeSpan.FromMilliseconds(100),
        });

        var task = (await engine.CreateDownloadAsync(_device, source)).Value!;

        // 取一块即进入 transferring，之后客户端再也没来
        using (var first = (await engine.ReadChunkAsync(_device, task.TaskId, 0)).Value!)
        {
            await first.Stream.CopyToAsync(Stream.Null);
        }

        await Task.Delay(600);

        var midway = await engine.GetAsync(_device, task.TaskId);
        Assert.Equal(TransferStates.Interrupted, midway.Value!.State);

        await Task.Delay(1200);

        var expired = await engine.GetAsync(_device, task.TaskId);
        Assert.Equal(TransferStates.Failed, expired.Value!.State);
        Assert.Equal(ErrorCodes.TransferTimeout, expired.Value.ErrorCode);
    }

    [Fact]
    public async Task DeleteRecords_WithoutDevice_RemovesRecord()
    {
        // 宿主界面能看到"设备已经没了、记录还在"的行：删记录不该再要求设备存在（K91）
        var source = Path.Combine(_root, "orphan.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(2048));

        var task = (await _engine.CreateDownloadAsync(_device, source, TransferPurposes.Directory)).Value!;

        // 活动中的任务本来就不许删（另一条规则），这里要的是"终态 + 设备没了"
        await _repository.UpdateResultAsync(
            task.TaskId, TransferStates.Failed, null, "transfer_timeout", "任务中断后长时间未恢复。",
            null, DateTimeOffset.UtcNow, null);

        var result = await _engine.DeleteRecordsAsync(new[] { task.TaskId }, deleteFiles: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.Value!.Deleted);
        Assert.Empty(result.Value.Rejected);
        Assert.Null(await _repository.GetAsync(task.TaskId));
    }

    [Fact]
    public async Task DeleteRecords_WithDevice_RejectsOtherDevicesRecord()
    {
        // HTTP 那条路必须守住归属：手机不能删别人的记录
        var source = Path.Combine(_root, "others.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(1024));
        var task = (await _engine.CreateDownloadAsync(_device, source, TransferPurposes.Directory)).Value!;

        await _repository.UpdateResultAsync(
            task.TaskId, TransferStates.Failed, null, "transfer_timeout", "任务中断后长时间未恢复。",
            null, DateTimeOffset.UtcNow, null);

        var stranger = new StoredDevice
        {
            DeviceId = "stranger00000000000000000000001",
            DisplayName = "别人",
            Platform = "android",
            IsPaired = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = await _engine.DeleteRecordsAsync(stranger, new[] { task.TaskId }, deleteFiles: false);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0, result.Value!.Deleted);
        Assert.NotNull(await _repository.GetAsync(task.TaskId));
    }

    [Fact]
    public async Task Download_PurposeSeparatesInboxFromDirectory()
    {
        var content = RandomBytes(4096);
        var source = Path.Combine(_root, "purpose.bin");
        await File.WriteAllBytesAsync(source, content);

        // 默认（传输中心「发送到手机」）：手机可以自动接收
        var inbox = (await _engine.CreateDownloadAsync(_device, source)).Value!;
        Assert.Equal(TransferPurposes.Inbox, inbox.Purpose);

        // 文件浏览器「发送到手机目录」：落点由作业带着 SAF URI 去写，手机端不能抢
        var directory = (await _engine.CreateDownloadAsync(_device, source, TransferPurposes.Directory)).Value!;
        Assert.Equal(TransferPurposes.Directory, directory.Purpose);

        // 从库里再读一次，确认值真落了盘（不只是返回值里对）
        var stored = await _engine.GetAsync(_device, directory.TaskId);
        Assert.Equal(TransferPurposes.Directory, stored.Value!.Purpose);
    }

    [Fact]
    public async Task Download_RestartAfterFailure_AllowsFullRetry()
    {
        var content = RandomBytes(200 * 1024);
        var source = Path.Combine(_root, "retry.bin");
        await File.WriteAllBytesAsync(source, content);

        var task = (await _engine.CreateDownloadAsync(_device, source)).Value!;

        // 先完整拉一轮并逐块回执，最后整文件校验失败 → 任务判失败
        var wrongWholeSha = new string('0', 64);
        await PullAndAckAllAsync(task, content);
        var bad = await _engine.AcknowledgeDownloadAsync(_device, task.TaskId, wrongWholeSha);
        Assert.False(bad.Success);
        Assert.Equal(ErrorCodes.ChecksumMismatch, bad.Error!.Code);
        Assert.Equal(TransferStates.Failed, (await _engine.GetAsync(_device, task.TaskId)).Value!.State);

        // 重试：进度与分块位图归零、错误清空，可以重新从第 0 块拉
        var restarted = await _engine.RestartDownloadAsync(_device, task.TaskId);
        Assert.True(restarted.Success, restarted.Error?.Message);
        Assert.Equal(TransferStates.Waiting, restarted.Value!.State);
        Assert.Equal(0, restarted.Value.TransferredBytes);
        Assert.Null(restarted.Value.ErrorCode);

        var assembled = await PullAndAckAllAsync(task, content);
        var ack = await _engine.AcknowledgeDownloadAsync(
            _device, task.TaskId, Sha256(assembled));
        Assert.True(ack.Success, ack.Error?.Message);
        Assert.Equal(TransferStates.Completed, ack.Value!.State);
    }

    [Fact]
    public async Task Restart_RejectsUploadTask()
    {
        var upload = (await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "no-restart.bin",
            TotalBytes = 10,
        })).Value!;

        var result = await _engine.RestartDownloadAsync(_device, upload.TaskId);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.TransferStateInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Download_ChunksConcatenateToOriginalAndAckCompletes()
    {
        var content = RandomBytes(5 * 1024 * 1024 + 777);
        var source = Path.Combine(_root, "source.bin");
        await File.WriteAllBytesAsync(source, content);

        var created = await _engine.CreateDownloadAsync(_device, source);
        Assert.True(created.Success, created.Error?.Message);
        var task = created.Value!;
        Assert.Equal(TransferDirections.Download, task.Direction);
        Assert.Equal(Sha256(content), task.Sha256);

        var assembled = await PullAndAckAllAsync(task, content);

        Assert.Equal(content.Length, assembled.Length);
        Assert.Equal(Sha256(content), Sha256(assembled));

        var ack = await _engine.AcknowledgeDownloadAsync(_device, task.TaskId, Sha256(assembled));
        Assert.True(ack.Success, ack.Error?.Message);
        Assert.Equal(TransferStates.Completed, ack.Value!.State);
    }

    [Fact]
    public async Task Download_ChunkAck_VerifiesHashAndAdvancesProgress()
    {
        var content = RandomBytes(128 * 1024);
        var source = Path.Combine(_root, "chunkack.bin");
        await File.WriteAllBytesAsync(source, content);

        var task = (await _engine.CreateDownloadAsync(_device, source)).Value!;

        using (var chunk = (await _engine.ReadChunkAsync(_device, task.TaskId, 0)).Value!)
        {
            var buffer = new MemoryStream();
            await chunk.Stream.CopyToAsync(buffer);

            // 分块哈希随下发给出，客户端落盘后据此自检
            Assert.Equal(Sha256(buffer.ToArray()), chunk.ChunkSha256);

            // 错哈希回执：拒绝，进度不动
            var bad = await _engine.AcknowledgeChunkAsync(
                _device, task.TaskId, 0, Sha256(RandomBytes(32)));
            Assert.False(bad.Success);
            Assert.Equal(ErrorCodes.ChunkChecksumMismatch, bad.Error!.Code);
            Assert.Equal(0, (await _engine.GetAsync(_device, task.TaskId)).Value!.TransferredBytes);

            // 正确回执：进度推进
            var good = await _engine.AcknowledgeChunkAsync(_device, task.TaskId, 0, chunk.ChunkSha256);
            Assert.True(good.Success, good.Error?.Message);
            Assert.False(good.Value!.AlreadyCompleted);
            Assert.Equal(content.Length, good.Value.TransferredBytes);

            // 幂等重放回执：不重复计数
            var replay = await _engine.AcknowledgeChunkAsync(_device, task.TaskId, 0, chunk.ChunkSha256);
            Assert.True(replay.Success, replay.Error?.Message);
            Assert.True(replay.Value!.AlreadyCompleted);
            Assert.Equal(content.Length, replay.Value.TransferredBytes);
        }

        // 整文件校验前必须所有分块都已回执（此例只有一块）
        var done = await _engine.AcknowledgeDownloadAsync(_device, task.TaskId, Sha256(content));
        Assert.True(done.Success, done.Error?.Message);
    }

    [Fact]
    public async Task Download_FinalAck_RequiresAllChunksAcked()
    {
        var content = RandomBytes(ProtocolConstants.ChunkSizeBytes + 100);
        var source = Path.Combine(_root, "finalgate.bin");
        await File.WriteAllBytesAsync(source, content);

        var task = (await _engine.CreateDownloadAsync(_device, source)).Value!;
        Assert.Equal(2, task.ChunkCount);

        // 只回执第一块就请求完成：必须拒绝
        using (var chunk = (await _engine.ReadChunkAsync(_device, task.TaskId, 0)).Value!)
        {
            await chunk.Stream.CopyToAsync(Stream.Null);
            var ack = await _engine.AcknowledgeChunkAsync(_device, task.TaskId, 0, chunk.ChunkSha256);
            Assert.True(ack.Success, ack.Error?.Message);
        }

        var premature = await _engine.AcknowledgeDownloadAsync(_device, task.TaskId, Sha256(content));
        Assert.False(premature.Success);
        Assert.Equal(ErrorCodes.TransferStateInvalid, premature.Error!.Code);
    }

    [Fact]
    public async Task Download_ChunkStreamStopsAtChunkBoundary()
    {
        // 多块文件的每一块都必须精确取出 chunkSize 字节。
        // 少取会给出残缺块，多取会让 Kestrel 的 Content-Length 校验直接抛异常
        // （真机现象：stream was reset: INTERNAL_ERROR）。
        var chunkSize = ProtocolConstants.ChunkSizeBytes;
        var content = RandomBytes(chunkSize * 2 + 1234);
        var source = Path.Combine(_root, "boundary.bin");
        await File.WriteAllBytesAsync(source, content);

        var task = (await _engine.CreateDownloadAsync(_device, source)).Value!;
        Assert.Equal(3, task.ChunkCount);

        var assembled = new List<byte>(content.Length);
        for (var index = 0; index < task.ChunkCount; index++)
        {
            var chunk = await _engine.ReadChunkAsync(_device, task.TaskId, index);
            Assert.True(chunk.Success, chunk.Error?.Message);

            using (chunk.Value!)
            {
                var expected = Math.Min(chunkSize, content.Length - index * chunkSize);
                Assert.Equal(expected, chunk.Value!.Length);

                var buffer = new MemoryStream();
                await chunk.Value!.Stream.CopyToAsync(buffer);

                // 关键断言：即使源文件后面还有数据，这块也只能读到边界为止
                Assert.Equal(expected, buffer.Length);
                assembled.AddRange(buffer.ToArray());
            }
        }

        Assert.Equal(Sha256(content), Sha256(assembled.ToArray()));
    }

    [Fact]
    public async Task Download_AckWithWrongHash_Fails()
    {
        var content = RandomBytes(128 * 1024);
        var source = Path.Combine(_root, "ack.bin");
        await File.WriteAllBytesAsync(source, content);

        var task = (await _engine.CreateDownloadAsync(_device, source)).Value!;
        await PullAndAckAllAsync(task, content);

        var ack = await _engine.AcknowledgeDownloadAsync(_device, task.TaskId, Sha256(RandomBytes(8)));

        Assert.False(ack.Success);
        Assert.Equal(ErrorCodes.ChecksumMismatch, ack.Error!.Code);

        var status = await _engine.GetAsync(_device, task.TaskId);
        Assert.Equal(TransferStates.Failed, status.Value!.State);
    }

    [Fact]
    public async Task Download_UnknownSource_Rejected()
    {
        var result = await _engine.CreateDownloadAsync(_device, Path.Combine(_root, "missing.bin"));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FileInaccessible, result.Error!.Code);
    }

    [Fact]
    public async Task Download_ChunkIndexOutOfRange_Rejected()
    {
        var source = Path.Combine(_root, "range.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(1024));

        var task = (await _engine.CreateDownloadAsync(_device, source)).Value!;
        var result = await _engine.ReadChunkAsync(_device, task.TaskId, 99);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
    }

    // ------------------------------------------------------------ 大文件

    [Fact]
    public async Task Upload_100MB_StreamsInChunks()
    {
        const long size = 100L * 1024 * 1024;
        var source = Path.Combine(_root, "hundred.bin");
        var sha = CreateFileOfSize(source, size);

        var task = await CreateUploadFromFileAsync(source, "hundred.bin", size);
        await UploadChunksFromFileAsync(task, source);

        var completed = await CompleteAsync(task.TaskId, sha);
        Assert.True(completed.Success, completed.Error?.Message);

        var target = ReceivePath("hundred.bin");
        Assert.Equal(size, new FileInfo(target).Length);
        Assert.Equal(sha, Sha256File(target));
    }

    [Fact]
    public async Task Upload_1GB_StreamsWithoutBufferingWholeFile()
    {
        const long size = 1L * 1024 * 1024 * 1024;
        var source = Path.Combine(_root, "giga.bin");
        var sha = CreateFileOfSize(source, size);

        var task = await CreateUploadFromFileAsync(source, "giga.bin", size);

        // 分块数必须是 128（8 MiB × 128 = 1 GiB），证明是分块而非整载
        Assert.Equal(128, task.ChunkCount);

        long lastBytes = 0;
        var progressEvents = 0;
        _engine.Progress += (_, dto) =>
        {
            if (dto.TaskId == task.TaskId && dto.State == TransferStates.Transferring)
            {
                Assert.True(dto.TransferredBytes > lastBytes, "进度必须单调递增");
                lastBytes = dto.TransferredBytes;
                progressEvents++;
            }
        };

        await UploadChunksFromFileAsync(task, source);
        var completed = await CompleteAsync(task.TaskId, sha);

        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal(128, progressEvents);

        var target = ReceivePath("giga.bin");
        Assert.Equal(size, new FileInfo(target).Length);
        Assert.Equal(sha, Sha256File(target));
    }

    // ------------------------------------------------------------ 恢复（进程重启 / 中断续传）

    [Fact]
    public async Task Upload_ResumeAfterProcessRestart_ContinuesFromServerBitmap()
    {
        // 模拟"传到一半电脑程序关闭"：引擎整个换掉（内存会话全丢），数据库与临时文件还在。
        // 恢复后只能凭服务端分块位图续传，已完成的两块不得重传。
        var content = RandomBytes(256 * 1024);

        using (var first = NewEngine())
        {
            var task = (await first.CreateUploadAsync(_device, new TransferStartRequestDto
            {
                FileName = "resume.bin",
                TotalBytes = content.Length,
                RequestedChunkSize = 64 * 1024,
            })).Value!;
            Assert.Equal(4, task.ChunkCount);

            for (var index = 0; index < 2; index++)
            {
                var slice = content[(index * 64 * 1024)..((index + 1) * 64 * 1024)];
                using var body = new MemoryStream(slice);
                var ack = await first.WriteChunkAsync(_device, task.TaskId, index, body, slice.Length, Sha256(slice));
                Assert.True(ack.Success, ack.Error?.Message);
            }

            _resumedTaskId = task.TaskId;
        }

        using var second = NewEngine();
        await second.RecoverAsync();

        // 启动恢复：任务标记为可续传的中断态
        var interrupted = await second.GetAsync(_device, _resumedTaskId);
        Assert.Equal(TransferStates.Interrupted, interrupted.Value!.State);

        // 恢复：服务端位图是唯一事实来源
        var resumed = await second.ResumeAsync(_device, _resumedTaskId);
        Assert.True(resumed.Success, resumed.Error?.Message);
        Assert.Equal(new[] { 0, 1 }, resumed.Value!.CompletedChunks);
        Assert.Equal(2 * 64 * 1024, resumed.Value.TransferredBytes);

        // 只补差集：从第 2 块继续写（会话不在内存，自动重建）
        for (var index = 2; index < 4; index++)
        {
            var slice = content[(index * 64 * 1024)..((index + 1) * 64 * 1024)];
            using var body = new MemoryStream(slice);
            var ack = await second.WriteChunkAsync(_device, _resumedTaskId, index, body, slice.Length, Sha256(slice));
            Assert.True(ack.Success, ack.Error?.Message);
        }

        var completed = await second.CompleteUploadAsync(_device, _resumedTaskId, Sha256(content));
        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal(Sha256(content), Sha256File(ReceivePath("resume.bin")));
    }

    private string _resumedTaskId = string.Empty;

    [Fact]
    public async Task Upload_ResumeWithCorruptTemp_ResetsBitmap()
    {
        // 临时文件损坏/丢失：位图不可信，整体重置后客户端重传，绝不提交坏数据
        var content = RandomBytes(128 * 1024);
        var task = await CreateUploadAsync(content, "corrupt-temp.bin", requestedChunkSize: 64 * 1024);
        await WriteAllChunksAsync(task, content, startIndex: 0);

        var temp = Assert.Single(Directory.GetFiles(TempDirectory, "*.wcpart"));
        File.Delete(temp);

        using var second = NewEngine();
        await second.RecoverAsync();

        var resumed = await second.ResumeAsync(_device, task.TaskId);
        Assert.True(resumed.Success, resumed.Error?.Message);
        Assert.Empty(resumed.Value!.CompletedChunks);
        Assert.Equal(0, resumed.Value.TransferredBytes);

        // 重传全部并完成
        for (var index = 0; index < task.ChunkCount; index++)
        {
            var offset = index * 64 * 1024;
            var length = (int)Math.Min(64 * 1024, content.Length - offset);
            var slice = content[offset..(offset + length)];
            using var body = new MemoryStream(slice);
            var ack = await second.WriteChunkAsync(_device, task.TaskId, index, body, length, Sha256(slice));
            Assert.True(ack.Success, ack.Error?.Message);
        }

        var completed = await second.CompleteUploadAsync(_device, task.TaskId, Sha256(content));
        Assert.True(completed.Success, completed.Error?.Message);
    }

    [Fact]
    public async Task Resume_DuplicateCalls_IdempotentNoNewTask()
    {
        var content = RandomBytes(64 * 1024);
        var task = await CreateUploadAsync(content, "dup-resume.bin");

        var before = (await _engine.ListAsync(_device)).Count;

        var first = await _engine.ResumeAsync(_device, task.TaskId);
        var second = await _engine.ResumeAsync(_device, task.TaskId);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(first.Value!.TaskId, second.Value!.TaskId);
        Assert.Equal(first.Value.State, second.Value.State);

        var after = (await _engine.ListAsync(_device)).Count;
        Assert.Equal(before, after); // 恢复绝不产生新任务
    }

    [Fact]
    public async Task Download_ResumeAfterRestart_KeepsAckedChunks()
    {
        // 下载回执过的分块跨进程保留：重启后只补未确认的块
        var content = RandomBytes(ProtocolConstants.ChunkSizeBytes + 200 * 1024);
        var source = Path.Combine(_root, "dl-resume.bin");
        await File.WriteAllBytesAsync(source, content);

        string taskId;
        using (var first = NewEngine())
        {
            var task = (await first.CreateDownloadAsync(_device, source)).Value!;
            taskId = task.TaskId;
            Assert.Equal(2, task.ChunkCount);

            using (var chunk = (await first.ReadChunkAsync(_device, taskId, 0)).Value!)
            {
                await chunk.Stream.CopyToAsync(Stream.Null);
                var ack = await first.AcknowledgeChunkAsync(_device, taskId, 0, chunk.ChunkSha256);
                Assert.True(ack.Success, ack.Error?.Message);
            }
        }

        using var second = NewEngine();
        await second.RecoverAsync();

        var interrupted = await second.GetAsync(_device, taskId);
        Assert.Equal(TransferStates.Interrupted, interrupted.Value!.State);

        var resumed = await second.ResumeAsync(_device, taskId);
        Assert.True(resumed.Success, resumed.Error?.Message);
        Assert.Equal(new[] { 0 }, resumed.Value!.CompletedChunks);

        // 第 0 块已确认，只补第 1 块
        var chunk1 = await second.ReadChunkAsync(_device, taskId, 1);
        Assert.True(chunk1.Success, chunk1.Error?.Message);

        var tail = new MemoryStream();
        using (chunk1.Value!)
        {
            await chunk1.Value!.Stream.CopyToAsync(tail);
            var ack = await second.AcknowledgeChunkAsync(_device, taskId, 1, chunk1.Value!.ChunkSha256);
            Assert.True(ack.Success, ack.Error?.Message);
        }

        var done = await second.AcknowledgeDownloadAsync(_device, taskId, Sha256(content));
        Assert.True(done.Success, done.Error?.Message);
        Assert.Equal(content.Length - ProtocolConstants.ChunkSizeBytes, tail.Length);
    }

    [Fact]
    public async Task Recover_DeletesOrphanTempFiles()
    {
        // 建任务途中进程被杀留下的孤儿临时文件：没有任务行，启动恢复直接清理
        var orphan = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".wcpart");
        await File.WriteAllBytesAsync(orphan, RandomBytes(1024));

        await _engine.RecoverAsync();

        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task Upload_CompleteWithTamperedTemp_NeverCommits()
    {
        // 目标文件（临时文件）在传输完成后被改动：整文件校验必须拦下，损坏文件不得提交
        var content = RandomBytes(64 * 1024);
        var task = await CreateUploadAsync(content, "tampered.bin");
        await WriteAllChunksAsync(task, content);

        var temp = Assert.Single(Directory.GetFiles(TempDirectory, "*.wcpart"));
        await using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Seek(100, SeekOrigin.Begin);
            stream.WriteByte(0xFF);
        }

        var completed = await _engine.CompleteUploadAsync(_device, task.TaskId, Sha256(content));

        Assert.False(completed.Success);
        Assert.Equal(ErrorCodes.ChecksumMismatch, completed.Error!.Code);
        Assert.False(File.Exists(ReceivePath("tampered.bin")));
        Assert.Empty(Directory.GetFiles(TempDirectory, "*.wcpart"));
    }

    // ------------------------------------------------------------ 冲突策略

    [Fact]
    public async Task Upload_OverwritePolicy_ReplacesExistingFile()
    {
        var existing = PrepareReceivePath("replace.txt");
        await File.WriteAllTextAsync(existing, "旧内容");

        var content = Encoding.UTF8.GetBytes("新内容");
        var created = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "replace.txt",
            TotalBytes = content.Length,
            Sha256 = Sha256(content),
            Conflict = TransferConflictPolicies.Overwrite,
        });
        Assert.True(created.Success, created.Error?.Message);
        Assert.Equal("replace.txt", created.Value!.FileName); // 不改名

        var task = created.Value;
        using var body = new MemoryStream(content);
        var ack = await _engine.WriteChunkAsync(
            _device, task.TaskId, 0, body, content.Length, Sha256(content));
        Assert.True(ack.Success, ack.Error?.Message);

        var completed = await _engine.CompleteUploadAsync(_device, task.TaskId, Sha256(content));
        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal("新内容", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task Upload_SkipPolicy_RejectedWhenTargetExists()
    {
        var existing = PrepareReceivePath("skip.txt");
        await File.WriteAllTextAsync(existing, "已有");

        var created = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "skip.txt",
            TotalBytes = 10,
            Conflict = TransferConflictPolicies.Skip,
        });

        Assert.False(created.Success);
        Assert.Equal(ErrorCodes.Conflict, created.Error!.Code);
        Assert.Equal(409, created.Error.StatusCode);
        Assert.Equal("已有", await File.ReadAllTextAsync(existing));

        // 目标不存在时正常创建
        var ok = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = "skip-new.txt",
            TotalBytes = 10,
            Conflict = TransferConflictPolicies.Skip,
        });
        Assert.True(ok.Success, ok.Error?.Message);
    }

    private TransferEngine NewEngine() => new(_repository, new TransferOptions
    {
        SharedDirectory = SharedDirectory,
        TempDirectory = TempDirectory,
        FreeSpaceProbe = _freeSpace,
    });

    // ------------------------------------------------------------ 辅助

    // ------------------------------------------------------------ 暂停 / 继续

    [Fact]
    public async Task Pause_Upload_KeepsProgressAndRejectsFurtherChunks()
    {
        var content = RandomBytes(256 * 1024);
        var task = await CreateUploadAsync(
            content, "paused.bin", requestedChunkSize: ProtocolConstants.MinChunkSizeBytes);

        Assert.True(task.ChunkCount >= 4, "分块数不足以验证暂停");

        // 先落一块，暂停后这块必须仍然算数
        using (var body = new MemoryStream(content, 0, task.ChunkSize, writable: false))
        {
            var ack = await _engine.WriteChunkAsync(
                _device, task.TaskId, 0, body, task.ChunkSize, Sha256(content[..task.ChunkSize]));
            Assert.True(ack.Success, ack.Error?.Message);
        }

        var paused = await _engine.PauseAsync(_device, task.TaskId);
        Assert.True(paused.Success, paused.Error?.Message);
        Assert.Equal(TransferStates.Paused, paused.Value!.State);

        // 暂停期间推块必须被拒，且标为可重试（临时状态，不是失败）
        using (var body = new MemoryStream(content, task.ChunkSize, task.ChunkSize, writable: false))
        {
            var rejected = await _engine.WriteChunkAsync(
                _device, task.TaskId, 1, body, task.ChunkSize,
                Sha256(content[task.ChunkSize..(task.ChunkSize * 2)]));

            Assert.False(rejected.Success);
            Assert.Equal(ErrorCodes.TransferStateInvalid, rejected.Error!.Code);
            Assert.True(rejected.Error.Retryable, "暂停应标为可重试，避免客户端判失败");
        }

        // 继续：状态离开 paused，从第二块接着传，最终内容必须逐字节一致
        var resumed = await _engine.ResumeAsync(_device, task.TaskId);
        Assert.True(resumed.Success, resumed.Error?.Message);
        Assert.NotEqual(TransferStates.Paused, resumed.Value!.State);

        await WriteAllChunksAsync(task, content, startIndex: 1);
        var completed = await CompleteAsync(task.TaskId, Sha256(content));

        Assert.True(completed.Success, completed.Error?.Message);
        Assert.Equal(TransferStates.Completed, completed.Value!.State);
        Assert.Equal(Sha256(content), Sha256File(ReceivePath("paused.bin")));
    }

    [Fact]
    public async Task Pause_FinishedTask_IsRejected()
    {
        var content = RandomBytes(1024);
        var task = await CreateUploadAsync(content, "finished.bin");
        await WriteAllChunksAsync(task, content);
        await CompleteAsync(task.TaskId, Sha256(content));

        var paused = await _engine.PauseAsync(_device, task.TaskId);

        Assert.False(paused.Success);
        Assert.Equal(ErrorCodes.TransferStateInvalid, paused.Error!.Code);
    }

    [Fact]
    public async Task Pause_IsIdempotent()
    {
        var content = RandomBytes(128 * 1024);
        var task = await CreateUploadAsync(
            content, "idem.bin", requestedChunkSize: ProtocolConstants.MinChunkSizeBytes);

        var first = await _engine.PauseAsync(_device, task.TaskId);
        var second = await _engine.PauseAsync(_device, task.TaskId);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(TransferStates.Paused, second.Value!.State);
    }

    private async Task<TransferTaskDto> CreateUploadAsync(
        byte[] content,
        string fileName,
        string? relativeDirectory = null,
        int? requestedChunkSize = null)
    {
        var result = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = fileName,
            RelativeDirectory = relativeDirectory,
            TotalBytes = content.Length,
            Sha256 = Sha256(content),
            RequestedChunkSize = requestedChunkSize,
        });

        Assert.True(result.Success, result.Error?.Message);
        return result.Value!;
    }

    private async Task WriteAllChunksAsync(TransferTaskDto task, byte[] content, int startIndex = 0)
    {
        var offset = startIndex * task.ChunkSize;
        for (var index = startIndex; index < task.ChunkCount; index++)
        {
            var length = (int)Math.Min(task.ChunkSize, content.Length - offset);
            using var body = new MemoryStream(content, offset, length, writable: false);
            var ack = await _engine.WriteChunkAsync(
                _device, task.TaskId, index, body, length, Sha256(content[offset..(offset + length)]));

            Assert.True(ack.Success, ack.Error?.Message);
            Assert.Equal(index + 1, ack.Value!.NextIndex);
            offset += length;
        }
    }

    /// <summary>逐块拉取并按服务端给出的分块哈希回执，返回拼出的完整内容。</summary>
    private async Task<byte[]> PullAndAckAllAsync(TransferTaskDto task, byte[] expectedContent)
    {
        var assembled = new MemoryStream();
        for (var index = 0; index < task.ChunkCount; index++)
        {
            var chunk = await _engine.ReadChunkAsync(_device, task.TaskId, index);
            Assert.True(chunk.Success, chunk.Error?.Message);

            using (chunk.Value!)
            {
                var buffer = new MemoryStream();
                await chunk.Value!.Stream.CopyToAsync(buffer);
                Assert.Equal(Sha256(buffer.ToArray()), chunk.Value!.ChunkSha256);
                assembled.Write(buffer.ToArray());

                var ack = await _engine.AcknowledgeChunkAsync(
                    _device, task.TaskId, index, chunk.Value!.ChunkSha256);
                Assert.True(ack.Success, ack.Error?.Message);
            }
        }

        Assert.Equal(expectedContent.Length, assembled.Length);
        return assembled.ToArray();
    }

    private async Task<TransferResult<TransferCompleteResponseDto>> CompleteAsync(string taskId, string sha)
        => await _engine.CompleteUploadAsync(_device, taskId, sha);

    [Fact]
    public async Task Download_ManyChunks_EachStreamYieldsExactlyChunkLength()
    {
        // 回归：阶段 4 真机跑大文件下载时出现过
        // "Response Content-Length mismatch: too many bytes written (8519680 of 8388608)"——
        // 服务端写出比声明多 131072 字节（= 2 × 64 KiB）。
        // 这里用小块 + 多块复刻同样的形状，逐块核对流的字节数。
        var chunkSize = ProtocolConstants.MinChunkSizeBytes;
        var fileSize = chunkSize * 160L + 12345; // 161 块
        var source = Path.Combine(_root, "many-chunks.bin");
        await File.WriteAllBytesAsync(source, RandomBytes((int)fileSize));

        var engine = new TransferEngine(_repository, new TransferOptions
        {
            SharedDirectory = SharedDirectory,
            TempDirectory = TempDirectory,
            FreeSpaceProbe = _freeSpace,
            DefaultChunkSize = chunkSize,
        });

        try
        {
            var created = await engine.CreateDownloadAsync(_device, source);
            Assert.True(created.Success, created.Error?.Message);
            var task = created.Value!;
            Assert.Equal(161, task.ChunkCount);

            for (var index = 0; index < task.ChunkCount; index++)
            {
                var result = await engine.ReadChunkAsync(_device, task.TaskId, index, CancellationToken.None);
                Assert.True(result.Success, result.Error?.Message);

                var chunk = result.Value!;
                await using var counter = new CountingStream();
                await chunk.Stream.CopyToAsync(counter, 81920, CancellationToken.None);

                Assert.Equal(chunk.Length, counter.Count);
                chunk.Dispose();
            }
        }
        finally
        {
            engine.Dispose();
        }
    }

    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Count += count;
        }
    }

    private async Task<TransferTaskDto> CreateUploadFromFileAsync(string path, string fileName, long size)
    {
        var result = await _engine.CreateUploadAsync(_device, new TransferStartRequestDto
        {
            FileName = fileName,
            TotalBytes = size,
        });

        Assert.True(result.Success, result.Error?.Message);
        return result.Value!;
    }

    private async Task UploadChunksFromFileAsync(TransferTaskDto task, string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        for (var index = 0; index < task.ChunkCount; index++)
        {
            var length = (int)Math.Min(task.ChunkSize, task.TotalBytes - (long)index * task.ChunkSize);
            var slice = new byte[length];
            await using (var bounded = new BoundedReadStream(file, length))
            {
                var filled = 0;
                while (filled < length)
                {
                    var read = await bounded.ReadAsync(slice.AsMemory(filled, length - filled));
                    if (read <= 0)
                    {
                        break;
                    }

                    filled += read;
                }
            }

            var ack = await _engine.WriteChunkAsync(
                _device, task.TaskId, index, new MemoryStream(slice), length, Sha256(slice));
            Assert.True(ack.Success, ack.Error?.Message);
        }
    }

    private StoredDevice NewDevice(string name) => new()
    {
        DeviceId = Guid.NewGuid().ToString("N"),
        DisplayName = name,
        Platform = "android",
        IsPaired = true,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static byte[] RandomBytes(int size)
    {
        var buffer = new byte[size];
        Random.Shared.NextBytes(buffer);
        return buffer;
    }

    private static string Sha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>生成指定大小的真实文件，返回其 SHA-256。逐块写入，避免一次性分配。</summary>
    private static string CreateFileOfSize(string path, long size)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, useAsync: false);

        var block = new byte[1024 * 1024];
        Random.Shared.NextBytes(block);

        long written = 0;
        while (written < size)
        {
            var length = (int)Math.Min(block.Length, size - written);
            hash.AppendData(block, 0, length);
            stream.Write(block, 0, length);
            written += length;
        }

        stream.Flush();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed class FakeFreeSpaceProbe : IFreeSpaceProbe
    {
        public long AvailableBytes { get; set; } = long.MaxValue;

        public long GetAvailableBytes(string path) => AvailableBytes;
    }

    /// <summary>把底层流限制在固定长度内，模拟"一个分块的请求体"。</summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            return ReadBoundedAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadBoundedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var want = (int)Math.Min(buffer.Length, _remaining);
            var read = await _inner.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // 底层流由调用方持有，这里不关闭
            base.Dispose(disposing);
        }
    }
}
