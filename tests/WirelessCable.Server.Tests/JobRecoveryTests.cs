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
/// K48：作业被取走后手机被杀，没人回报结果。
///
/// 真实场景（1.0.0 实测）：电脑 → 手机传 100 MB，传输中 `am force-stop` 手机应用。
/// 任务停在 transferring、作业停在 running，重启应用不会自动继续——桌面端只能干等到自己的超时。
/// 现在的规则：取走够久且"关联传输没有在推进"的 running 作业，由服务端判失败。
/// </summary>
public sealed class JobRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wc-job-tests", Guid.NewGuid().ToString("N"));
    private readonly DeviceFileRepository _deviceFiles;
    private readonly TransferRepository _transfers;
    private readonly FileService _service;

    private const string DeviceId = "device-a";
    private const string RootId = "root-1";

    public JobRecoveryTests()
    {
        Directory.CreateDirectory(_root);

        var database = new SqliteDatabaseFactory(Path.Combine(_root, "test.db"));
        SchemaInitializer.Initialize(database);

        _deviceFiles = new DeviceFileRepository(database);
        _transfers = new TransferRepository(database);
        _service = new FileService(
            new SharedDirectoryRepository(database),
            _deviceFiles,
            root => new LocalFileProvider(root));
    }

    [Fact]
    public async Task RunningJobWithoutTransfer_IsReclaimedAsFailed()
    {
        var device = Device();
        await SeedRunningJobAsync("job-1", takenMinutesAgo: 10);

        var jobs = await _service.ListJobsAsync(device, 20);

        var job = Assert.Single(jobs);
        Assert.Equal(FileJobStatuses.Failed, job.Status);
        Assert.Equal(ErrorCodes.JobTimeout, job.ErrorCode);
        Assert.Contains("断开", job.ErrorMessage);
    }

    [Fact]
    public async Task RunningJobWithLiveTransfer_IsLeftAlone()
    {
        var device = Device();
        await SeedRunningJobAsync("job-2", takenMinutesAgo: 10, transferTaskId: "task-1");
        await _transfers.InsertAsync(Task("task-1", TransferStates.Transferring, DateTimeOffset.UtcNow));

        var jobs = await _service.ListJobsAsync(device, 20);

        // 大文件（最长 100 GiB）跑起来可以远超 5 分钟，只要分块还在推进就不能判死
        Assert.Equal(FileJobStatuses.Running, Assert.Single(jobs).Status);
    }

    [Fact]
    public async Task RunningJobWhoseTransferFinished_IsReclaimed()
    {
        var device = Device();
        await SeedRunningJobAsync("job-3", takenMinutesAgo: 10, transferTaskId: "task-2");

        // 传输已完成但手机还没来得及回报作业就没了：这种要按超时结掉
        await _transfers.InsertAsync(Task("task-2", TransferStates.Completed, DateTimeOffset.UtcNow));

        var jobs = await _service.ListJobsAsync(device, 20);

        Assert.Equal(FileJobStatuses.Failed, Assert.Single(jobs).Status);
    }

    [Fact]
    public async Task TakePendingJobs_ReclaimsBeforeHandingOut()
    {
        var device = Device();
        await SeedRunningJobAsync("job-4", takenMinutesAgo: 10);
        await SeedJobAsync("job-5", FileJobStatuses.Pending, DateTimeOffset.UtcNow.AddMinutes(-1));

        var taken = await _service.TakePendingJobsAsync(device);

        // 只交出真正 pending 的那一个；上一个已经在回收里结掉了
        var job = Assert.Single(taken);
        Assert.Equal("job-5", job.JobId);
        Assert.Equal(FileJobStatuses.Failed, (await _deviceFiles.GetJobAsync("job-4"))!.Status);
    }

    [Fact]
    public async Task RecentlyTakenJob_IsNotReclaimed()
    {
        var device = Device();
        await SeedRunningJobAsync("job-6", takenMinutesAgo: 1);

        var jobs = await _service.ListJobsAsync(device, 20);

        // 手机刚取走一秒，正常执行中，不能被误杀
        Assert.Equal(FileJobStatuses.Running, Assert.Single(jobs).Status);
    }

    private static StoredDevice Device() => new()
    {
        DeviceId = DeviceId,
        DisplayName = "手机A",
        Platform = "android",
        IsPaired = true,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static TransferTaskRecord Task(string id, string status, DateTimeOffset updatedAt) => new()
    {
        TaskId = id,
        Direction = "download",
        DeviceId = DeviceId,
        FileName = "big.bin",
        RelativePath = "big.bin",
        TotalBytes = 1024,
        Status = status,
        CreatedAtUtc = updatedAt.AddMinutes(-20),
        UpdatedAtUtc = updatedAt,
    };

    private Task SeedRunningJobAsync(string jobId, int takenMinutesAgo, string? transferTaskId = null) =>
        SeedJobAsync(jobId, FileJobStatuses.Running, DateTimeOffset.UtcNow.AddMinutes(-takenMinutesAgo), transferTaskId);

    private async Task SeedJobAsync(
        string jobId,
        string status,
        DateTimeOffset updatedAt,
        string? transferTaskId = null)
    {
        await _deviceFiles.InsertJobAsync(new FileOperationJobRecord
        {
            JobId = jobId,
            DeviceId = DeviceId,
            RootId = RootId,
            Operation = FileOperations.Download,
            SourcePath = "wc8/big.bin",
            DisplayName = "big.bin",
            TransferTaskId = transferTaskId,
            Status = status,
            CreatedAtUtc = updatedAt.AddMinutes(-1),
            UpdatedAtUtc = updatedAt,
        });
    }

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
