using Serilog;
using WirelessCable.Core.Abstractions;
using WirelessCable.Core.Domain;
using WirelessCable.Core.Security;
using WirelessCable.Infrastructure.Files;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.Files;

/// <summary>一次文件操作的失败。带 HTTP 状态与稳定错误码，端点直接映射。</summary>
public sealed record FileServiceFailure(
    string Code,
    string Message,
    int StatusCode,
    string? ReasonCode = null,
    bool Retryable = false);

/// <summary>文件操作结果。失败时 <see cref="Error"/> 必填。</summary>
public readonly record struct FileServiceResult<T>(T? Value, FileServiceFailure? Error)
{
    public bool Success => Error is null;

    public static FileServiceResult<T> Ok(T value) => new(value, null);

    public static FileServiceResult<T> Fail(FileServiceFailure failure) => new(default, failure);

    public static implicit operator FileServiceResult<T>(FileServiceFailure failure) => new(default, failure);
}

/// <summary>
/// 文件管理服务。手机浏览/管理电脑共享目录，电脑浏览/管理手机授权目录，都走这里。
///
/// **安全边界集中在这一个类**（需求第五节）：
/// <list type="number">
/// <item>DeviceId——只有已认证且未撤销的设备能调用；</item>
/// <item>授权目录——共享目录必须已授权给该设备，手机目录必须属于该设备；</item>
/// <item>操作权限——共享目录按位判权限；手机目录按提供方真实能力判；</item>
/// <item>文件路径——交给 <see cref="LocalFileProvider"/> 做穿越/联接校验；</item>
/// <item>文件操作能力——提供方不支持时给明确原因码，不是笼统的 500。</item>
/// </list>
///
/// 电脑对手机目录的写操作不直接执行，而是下发作业让手机执行——
/// URI 授权只在手机上有效，服务端碰不到 content:// URI。
/// </summary>
public sealed class FileService
{
    /// <summary>
    /// 每台手机一个"有新作业"的信号。手机端的长轮询挂在这个信号上：
    /// 电脑一下发作业就立刻唤醒，把「点一下 → 手机开始做」压到一次网络往返。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> JobSignals =
        new(StringComparer.Ordinal);

    private readonly SharedDirectoryRepository _directories;
    private readonly DeviceFileRepository _deviceFiles;
    private readonly Func<string, IFileProvider> _providerFactory;

    /// <summary>整机（"此电脑"）文件提供方。没有共享目录那么强的根概念，因此单独一个实例。</summary>
    private readonly IFileProvider _machineProvider;

    public FileService(
        SharedDirectoryRepository directories,
        DeviceFileRepository deviceFiles,
        Func<string, IFileProvider> providerFactory,
        IFileProvider? machineProvider = null)
    {
        _directories = directories;
        _deviceFiles = deviceFiles;
        _providerFactory = providerFactory;
        _machineProvider = machineProvider ?? new MachineFileProvider();
    }

    // ------------------------------------------------------------ 根

    /// <summary>列出当前设备可见的全部根：被授权的共享目录 + 该设备授权给电脑的手机目录。</summary>
    public async Task<IReadOnlyList<FileRootDto>> ListRootsAsync(
        StoredDevice device,
        CancellationToken cancellationToken = default)
    {
        var roots = new List<FileRootDto>();

        var shared = await _directories.ListForDeviceAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        foreach (var (directory, permissions) in shared)
        {
            roots.Add(new FileRootDto
            {
                RootId = directory.DirectoryId,
                Kind = FileRootKinds.Shared,
                DisplayName = directory.DisplayName,
                DeviceId = device.DeviceId,
                Capabilities = FileCapabilities.All,
                Permissions = (int)permissions,
            });
        }

        // 整机根（"此电脑"）：手机像资源管理器那样看到电脑全部文件。
        // 1.1.0 起默认可读写，档位由设备级设置决定（0 关闭 / 1 只读 / 2 可读写）。
        var machineLevel = MachineAccessLevels.Normalize(device.MachineAccess);
        if (MachineAccessLevels.CanBrowse(machineLevel))
        {
            roots.Add(new FileRootDto
            {
                RootId = FileRootIds.Machine,
                Kind = FileRootKinds.Machine,
                DisplayName = "此电脑",
                DeviceId = device.DeviceId,
                Capabilities = (int)MachineCapabilities(machineLevel),
                Permissions = 0,
            });
        }

        var deviceRoots = await _deviceFiles.ListRootsAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        foreach (var root in deviceRoots)
        {
            roots.Add(new FileRootDto
            {
                RootId = root.RootId,
                Kind = FileRootKinds.Device,
                DisplayName = root.DisplayName,
                DeviceId = root.DeviceId,
                Capabilities = root.Capabilities,
                Permissions = 0,
            });
        }

        return roots;
    }

    /// <summary>
    /// 列出某个根下的内容。
    /// 共享目录直接读本地文件系统；手机目录读手机上行并落库的索引快照。
    /// </summary>
    public async Task<FileServiceResult<FileListingDto>> ListAsync(
        StoredDevice device,
        string rootId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(device, rootId, cancellationToken).ConfigureAwait(false);
        if (!root.Success)
        {
            return root.Error!;
        }

        if (root.Value!.Kind == FileRootKinds.Machine)
        {
            var machineResult = await _machineProvider
                .ListDirectoryAsync(path ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (!machineResult.Success)
            {
                return MapFailure(machineResult);
            }

            var machineCapabilities = (int)MachineCapabilities(
                MachineAccessLevels.Normalize(device.MachineAccess));

            // 盘符本身不可改名/删除/移动：不给这些位，客户端就不会在"XOS (C:)"旁边
            // 摆一个点了必然失败的按钮
            var driveCapabilities = machineCapabilities & (int)~(
                FileCapabilities.Rename | FileCapabilities.Delete
                | FileCapabilities.Move | FileCapabilities.CreateDirectory);

            return FileServiceResult<FileListingDto>.Ok(new FileListingDto
            {
                RootId = rootId,
                Path = path ?? string.Empty,
                Entries = machineResult.Value!
                    .Select(entry => ToDto(
                        entry,
                        IsDriveRoot(entry.RelativePath) ? driveCapabilities : machineCapabilities))
                    .ToList(),
                Capabilities = machineCapabilities,
            });
        }

        if (root.Value!.Kind == FileRootKinds.Device)
        {
            var entries = await _deviceFiles
                .ListEntriesAsync(device.DeviceId, rootId, path, cancellationToken)
                .ConfigureAwait(false);

            return FileServiceResult<FileListingDto>.Ok(new FileListingDto
            {
                RootId = rootId,
                Path = path ?? string.Empty,
                Entries = entries.Select(entry => ToDto(entry)).ToList(),
                Capabilities = root.Value.Capabilities,
            });
        }

        var directory = await _directories.GetAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return new FileServiceFailure(
                ErrorCodes.DirectoryNotAuthorized,
                "该目录未授权。",
                403);
        }

        var permissions = await _directories
            .GetPermissionsAsync(rootId, device.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (!permissions.HasFlag(SharedDirectoryPermissions.Browse))
        {
            return new FileServiceFailure(
                ErrorCodes.OperationNotPermitted,
                "没有浏览该目录的权限。",
                403,
                ReasonCode: "permission_denied");
        }

        var provider = _providerFactory(directory.RootPath);
        var result = await provider.ListDirectoryAsync(path ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return MapFailure(result);
        }

        // 条目能力按该目录的权限位换算：界面上「属性」要如实告诉用户这台设备能做什么
        var entryCapabilities = CapabilitiesFor(permissions);

        return FileServiceResult<FileListingDto>.Ok(new FileListingDto
        {
            RootId = rootId,
            Path = path ?? string.Empty,
            Entries = result.Value!.Select(entry => ToDto(entry, entryCapabilities)).ToList(),
            Capabilities = entryCapabilities,
        });
    }

    /// <summary>查询单个条目信息。</summary>
    public async Task<FileServiceResult<FileEntryDto>> GetInfoAsync(
        StoredDevice device,
        string rootId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(device, rootId, cancellationToken).ConfigureAwait(false);
        if (!root.Success)
        {
            return root.Error!;
        }

        if (root.Value!.Kind == FileRootKinds.Machine)
        {
            var machineResult = await _machineProvider
                .GetFileInfoAsync(path ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (!machineResult.Success)
            {
                return MapFailure(machineResult);
            }

            if (machineResult.Value is null)
            {
                return new FileServiceFailure(ErrorCodes.NotFound, "文件不存在。", 404);
            }

            return FileServiceResult<FileEntryDto>.Ok(ToDto(machineResult.Value));
        }

        if (root.Value!.Kind == FileRootKinds.Device)
        {
            var entry = await _deviceFiles.GetEntryAsync(device.DeviceId, rootId, path, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return new FileServiceFailure(ErrorCodes.NotFound, "文件不存在。", 404);
            }

            return FileServiceResult<FileEntryDto>.Ok(ToDto(entry));
        }

        var directory = await _directories.GetAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return new FileServiceFailure(ErrorCodes.DirectoryNotAuthorized, "该目录未授权。", 403);
        }

        var permissions = await _directories
            .GetPermissionsAsync(rootId, device.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (!permissions.HasFlag(SharedDirectoryPermissions.Browse))
        {
            return new FileServiceFailure(
                ErrorCodes.OperationNotPermitted,
                "没有浏览该目录的权限。",
                403,
                ReasonCode: "permission_denied");
        }

        var provider = _providerFactory(directory.RootPath);
        var result = await provider.GetFileInfoAsync(path ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return MapFailure(result);
        }

        if (result.Value is null)
        {
            return new FileServiceFailure(ErrorCodes.NotFound, "文件不存在。", 404);
        }

        // 属性面板要如实显示这台设备在这个目录里能做什么
        return FileServiceResult<FileEntryDto>.Ok(ToDto(result.Value, CapabilitiesFor(permissions)));
    }

    // ------------------------------------------------------------ 共享目录写操作（服务端直接执行）

    public Task<FileServiceResult<bool>> CreateDirectoryAsync(
        StoredDevice device,
        FileCreateDirectoryRequestDto request,
        CancellationToken cancellationToken = default)
        => RunOnSharedAsync(
            device,
            request.RootId,
            SharedDirectoryPermissions.Create,
            async (provider, _) =>
            {
                var parent = request.ParentPath ?? string.Empty;
                var target = string.IsNullOrEmpty(parent)
                    ? request.Name
                    : parent.TrimEnd('/') + "/" + request.Name;

                return await provider.CreateDirectoryAsync(target, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public Task<FileServiceResult<bool>> RenameAsync(
        StoredDevice device,
        FileRenameRequestDto request,
        CancellationToken cancellationToken = default)
        => RunOnSharedAsync(
            device,
            request.RootId,
            SharedDirectoryPermissions.Rename,
            async (provider, _) =>
            {
                return await provider
                    .RenameAsync(request.Path ?? string.Empty, request.NewName ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    public Task<FileServiceResult<bool>> DeleteAsync(
        StoredDevice device,
        FileDeleteRequestDto request,
        CancellationToken cancellationToken = default)
        => RunOnSharedAsync(
            device,
            request.RootId,
            SharedDirectoryPermissions.Delete,
            async (provider, _) =>
            {
                return await provider
                    .DeleteAsync(request.Path ?? string.Empty, request.Recursive, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    public Task<FileServiceResult<bool>> MoveAsync(
        StoredDevice device,
        FileMoveRequestDto request,
        CancellationToken cancellationToken = default)
        => RunOnSharedAsync(
            device,
            request.RootId,
            SharedDirectoryPermissions.Move,
            async (provider, _) =>
            {
                return await provider
                    .MoveAsync(request.SourcePath ?? string.Empty, request.DestinationPath ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// 复制一个条目：文件按流拷贝，目录递归。
    ///
    /// 与移动的区别只有"源不动"。目标已存在时直接拒绝——复制不该有覆盖语义，
    /// 覆盖一次就等于无声删掉别人的文件。所需权限是"能写 + 能建目录"（读源由浏览权限覆盖）。
    /// </summary>
    public Task<FileServiceResult<bool>> CopyAsync(
        StoredDevice device,
        FileCopyRequestDto request,
        CancellationToken cancellationToken = default)
        => RunOnSharedAsync(
            device,
            request.RootId,
            SharedDirectoryPermissions.Upload | SharedDirectoryPermissions.Create,
            (provider, _) => CopyEntryAsync(
                provider,
                request.SourcePath ?? string.Empty,
                request.DestinationPath ?? string.Empty,
                cancellationToken),
            cancellationToken);

    /// <summary>递归复制的深度上限。只防呆：用户误选一个巨大的树时不至于卡死在这里。</summary>
    private const int CopyMaxDepth = 12;

    private static async Task<FileOperationResult> CopyEntryAsync(
        IFileProvider provider,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        int depth = 0)
    {
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationPath))
        {
            return FileOperationResult.PathInvalid("源或目标路径为空。");
        }

        if (depth > CopyMaxDepth)
        {
            return FileOperationResult.Fail(
                FileOperationStatus.IoFailure,
                "copy_too_deep",
                $"目录层级超过 {CopyMaxDepth} 层，已停止复制。");
        }

        var info = await provider.GetFileInfoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!info.Success)
        {
            return FileOperationResult.Fail(info.Status, info.ReasonCode, info.Message);
        }

        if (await provider.ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false))
        {
            return FileOperationResult.AlreadyExists($"目标已存在：{destinationPath}");
        }

        var entry = info.Value!;
        if (!entry.IsDirectory)
        {
            return await CopyFileAsync(provider, sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }

        var created = await provider.CreateDirectoryAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (!created.Success)
        {
            return created;
        }

        var listing = await provider.ListDirectoryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!listing.Success)
        {
            return FileOperationResult.Fail(listing.Status, listing.ReasonCode, listing.Message);
        }

        foreach (var child in listing.Value!)
        {
            var childSource = $"{sourcePath}/{child.Name}";
            var childDestination = $"{destinationPath}/{child.Name}";

            var copied = await CopyEntryAsync(provider, childSource, childDestination, cancellationToken, depth + 1)
                .ConfigureAwait(false);
            if (!copied.Success)
            {
                return copied;
            }
        }

        return FileOperationResult.Ok();
    }

    private static async Task<FileOperationResult> CopyFileAsync(
        IFileProvider provider,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var read = await provider.OpenReadAsync(sourcePath, 0, cancellationToken).ConfigureAwait(false);
        if (!read.Success)
        {
            return FileOperationResult.Fail(read.Status, read.ReasonCode, read.Message);
        }

        await using var input = read.Value!;

        var write = await provider.OpenWriteAsync(destinationPath, append: false, cancellationToken)
            .ConfigureAwait(false);
        if (!write.Success)
        {
            return FileOperationResult.Fail(write.Status, write.ReasonCode, write.Message);
        }

        await using var output = write.Value!;
        await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        return FileOperationResult.Ok();
    }

    /// <summary>批量删除。逐条返回结果——批量操作不能因为一条失败就全废。</summary>
    public async Task<FileServiceResult<IReadOnlyList<FileBatchResultDto>>> DeleteBatchAsync(
        StoredDevice device,
        FileBatchRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(device, request.RootId, cancellationToken).ConfigureAwait(false);
        if (!root.Success)
        {
            return root.Error!;
        }

        if (root.Value!.Kind == FileRootKinds.Device)
        {
            var results = new List<FileBatchResultDto>();
            foreach (var path in request.Paths)
            {
                var job = await EnqueueJobAsync(
                    device,
                    request.RootId,
                    FileOperations.Delete,
                    path,
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false);
                results.Add(new FileBatchResultDto
                {
                    Path = path,
                    Success = job.Success,
                    ErrorCode = job.Error?.Code,
                    ErrorMessage = job.Error?.Message,
                });
            }

            return FileServiceResult<IReadOnlyList<FileBatchResultDto>>.Ok(results);
        }

        // 整机根（"此电脑"）走自己的通道：它没有 SharedDirectory 记录，
        // 以前直接往下走会 `_directories.GetAsync()` 拿到 null，报成"该目录未授权"——
        // 手机删电脑文件时看到的那句误导性提示（1.4.4 修）。
        if (root.Value!.Kind == FileRootKinds.Machine)
        {
            var level = MachineAccessLevels.Normalize(device.MachineAccess);
            var denial = MachineWriteFailure(level, requireDelete: true);
            if (denial is not null)
            {
                return denial;
            }

            var machineResults = new List<FileBatchResultDto>();
            foreach (var path in request.Paths)
            {
                var deleted = await _machineProvider
                    .DeleteAsync(path ?? string.Empty, request.Recursive, cancellationToken)
                    .ConfigureAwait(false);
                machineResults.Add(new FileBatchResultDto
                {
                    Path = path,
                    Success = deleted.Success,
                    ErrorCode = deleted.Success ? null : deleted.ReasonCode,
                    ErrorMessage = deleted.Success ? null : deleted.Message,
                });
            }

            return FileServiceResult<IReadOnlyList<FileBatchResultDto>>.Ok(machineResults);
        }

        var directory = await _directories.GetAsync(request.RootId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return new FileServiceFailure(ErrorCodes.DirectoryNotAuthorized, "该目录未授权。", 403);
        }

        var permissions = await _directories
            .GetPermissionsAsync(request.RootId, device.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (!permissions.HasFlag(SharedDirectoryPermissions.Delete))
        {
            // 说清去哪儿改：只写"没有权限"用户会当成配对/授权失效
            return new FileServiceFailure(
                ErrorCodes.OperationNotPermitted,
                "这台电脑没有给该设备删除权限。请在电脑端「共享目录 → 授权」里把档位改成「读写 + 删除」。",
                403,
                ReasonCode: "permission_denied");
        }

        var provider = _providerFactory(directory.RootPath);
        var batch = new List<FileBatchResultDto>();

        foreach (var path in request.Paths)
        {
            var result = await provider.DeleteAsync(path ?? string.Empty, request.Recursive, cancellationToken)
                .ConfigureAwait(false);
            batch.Add(new FileBatchResultDto
            {
                Path = path,
                Success = result.Success,
                ErrorCode = result.Success ? null : result.ReasonCode,
                ErrorMessage = result.Success ? null : result.Message,
            });
        }

        return FileServiceResult<IReadOnlyList<FileBatchResultDto>>.Ok(batch);
    }

    // ------------------------------------------------------------ 手机目录：下发作业

    /// <summary>对手机授权目录发起操作。下发作业，手机轮询执行。</summary>
    public async Task<FileServiceResult<FileJobDto>> EnqueueJobAsync(
        StoredDevice device,
        string rootId,
        string operation,
        string? sourcePath,
        string? destinationPath,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(device, rootId, cancellationToken).ConfigureAwait(false);
        if (!root.Success)
        {
            return root.Error!;
        }

        if (root.Value!.Kind != FileRootKinds.Device)
        {
            return new FileServiceFailure(
                ErrorCodes.InvalidRequest,
                "共享目录的操作直接在服务端执行，不需要下发作业。",
                400);
        }

        var capabilityCheck = await CheckCapabilityAsync(
            device.DeviceId,
            rootId,
            operation,
            sourcePath,
            root.Value.Capabilities,
            cancellationToken).ConfigureAwait(false);

        if (!capabilityCheck)
        {
            return new FileServiceFailure(
                ErrorCodes.OperationUnsupported,
                "该存储提供方不支持此操作。",
                501,
                ReasonCode: "operation_unsupported");
        }

        var jobId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var record = new FileOperationJobRecord
        {
            JobId = jobId,
            DeviceId = device.DeviceId,
            RootId = rootId,
            Operation = operation,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            DisplayName = displayName,
            Status = FileJobStatuses.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await _deviceFiles.InsertJobAsync(record, cancellationToken).ConfigureAwait(false);
        PulseJobSignal(device.DeviceId);
        return FileServiceResult<FileJobDto>.Ok(ToDto(record));
    }

    /// <summary>手机取走待执行作业。取出即标记 running，防止重复执行。</summary>
    /// <param name="wait">
    /// 长轮询等待时长：没有待执行作业时把请求挂住这么久，一有作业立刻返回。
    /// 手机端原来 3 秒轮询一次，一次往返 + 间隔平均要等 1.5 秒；
    /// 挂起式轮询把「电脑点一下 → 手机开始做」压到一次网络往返。
    /// </param>
    public async Task<IReadOnlyList<FileJobDto>> TakePendingJobsAsync(
        StoredDevice device,
        CancellationToken cancellationToken = default,
        TimeSpan? wait = null)
    {
        // 先把上一次"取走却没回报"的作业结掉：手机被杀之后不会再有人回报，
        // 不结就会永远停在 running，桌面端只能干等（K48）。
        await ReclaimStaleJobsAsync(device, cancellationToken).ConfigureAwait(false);

        var pending = await _deviceFiles
            .ListPendingJobsAsync(device.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (pending.Count == 0 && wait is { TotalMilliseconds: > 0 })
        {
            // 先登记信号再查第二次库：如果顺序反过来，恰好在这两步之间入队的作业
            // 会既没被第一次查询看到、也没唤醒信号——只能等满整个超时。
            var signal = JobSignals.GetOrAdd(
                device.DeviceId,
                _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

            try
            {
                pending = await _deviceFiles
                    .ListPendingJobsAsync(device.DeviceId, cancellationToken)
                    .ConfigureAwait(false);

                if (pending.Count == 0)
                {
                    await WaitForSignalAsync(signal, wait.Value, cancellationToken).ConfigureAwait(false);

                    pending = await _deviceFiles
                        .ListPendingJobsAsync(device.DeviceId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                // 只清掉自己登记的那一个：期间可能已经有新作业把它换掉了
                JobSignals.TryRemove(
                    new KeyValuePair<string, TaskCompletionSource<bool>>(device.DeviceId, signal));
            }
        }

        var results = new List<FileJobDto>();
        foreach (var job in pending)
        {
            await _deviceFiles
                .UpdateJobStatusAsync(job.JobId, FileJobStatuses.Running, null, null, cancellationToken)
                .ConfigureAwait(false);
            results.Add(ToDto(job, FileJobStatuses.Running));
        }

        return results;
    }

    /// <summary>
    /// 回收超时未回报的作业。
    ///
    /// 只需要"取走时间够久"这一个条件；带传输任务的作业由仓储按进度额外放行，
    /// 所以 100 GiB 这种长任务不会被误杀。
    /// </summary>
    private async Task ReclaimStaleJobsAsync(StoredDevice device, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var reclaimed = await _deviceFiles.ReclaimStaleJobsAsync(
                device.DeviceId,
                now.AddSeconds(-ProtocolConstants.JobReportTimeoutSeconds),
                now.AddSeconds(-ProtocolConstants.TransferInactivitySeconds),
                ErrorCodes.JobTimeout,
                "手机在执行过程中断开（可能被系统回收），请重试。",
                cancellationToken)
            .ConfigureAwait(false);

        if (reclaimed.Count > 0)
        {
            Log.Warning(
                "回收 {Count} 个超时未回报的作业 device={DeviceId}",
                reclaimed.Count,
                device.DeviceId);
        }
    }

    /// <summary>手机回报作业结果。</summary>
    public async Task<FileServiceResult<FileJobDto>> CompleteJobAsync(
        StoredDevice device,
        FileJobResultDto result,
        CancellationToken cancellationToken = default)
    {
        var job = await _deviceFiles.GetJobAsync(result.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return new FileServiceFailure(ErrorCodes.NotFound, "作业不存在。", 404);
        }

        if (!string.Equals(job.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return new FileServiceFailure(
                ErrorCodes.Forbidden,
                "不能操作其他设备的作业。",
                403);
        }

        await _deviceFiles.UpdateJobStatusAsync(
            job.JobId,
            result.Status,
            result.ErrorCode,
            result.ErrorMessage,
            cancellationToken,
            result.Payload).ConfigureAwait(false);

        var updated = await _deviceFiles.GetJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        return FileServiceResult<FileJobDto>.Ok(ToDto(updated!));
    }

    /// <summary>列出作业历史。</summary>
    public async Task<IReadOnlyList<FileJobDto>> ListJobsAsync(
        StoredDevice device,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // 手机一直不回来时，桌面端仍要靠这里看到"作业已经失败"，而不是自己等到超时。
        await ReclaimStaleJobsAsync(device, cancellationToken).ConfigureAwait(false);

        var jobs = await _deviceFiles
            .ListRecentJobsAsync(device.DeviceId, limit, cancellationToken)
            .ConfigureAwait(false);
        return jobs.Select(job => ToDto(job)).ToList();
    }

    // ------------------------------------------------------------ 桌面端（电脑本机）视角

    /// <summary>列出某设备授权给电脑的目录根。</summary>
    public async Task<IReadOnlyList<FileRootDto>> ListDeviceRootsAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var roots = await _deviceFiles.ListRootsAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return roots.Select(root => new FileRootDto
        {
            RootId = root.RootId,
            Kind = FileRootKinds.Device,
            DisplayName = root.DisplayName,
            DeviceId = root.DeviceId,
            Capabilities = root.Capabilities,
        }).ToList();
    }

    /// <summary>
    /// 列出手机目录下的条目（读手机上报的索引快照）。
    /// path 为空表示根；非空是相对根的路径，与 Windows 共享目录语义一致。
    /// </summary>
    public async Task<IReadOnlyList<FileEntryDto>> ListDeviceEntriesAsync(
        string deviceId,
        string rootId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var entries = await _deviceFiles
            .ListEntriesAsync(deviceId, rootId, path ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return entries.Select(entry => ToDto(entry)).ToList();
    }

    /// <summary>
    /// 这条路径在索引快照里有没有记录。
    ///
    /// 用来区分两种"列出来是空的"：
    /// <list type="bullet">
    /// <item>索引过的空目录——快照里有一条指向它的记录，没有子项，就是真的空，不必再问手机；</item>
    /// <item>根本没索引到的目录——超出索引深度，快照里连它自己都没有，必须现场问一次。</item>
    /// </list>
    /// </summary>
    public async Task<bool> DeviceEntryExistsAsync(
        string deviceId,
        string rootId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var entry = await _deviceFiles
            .GetEntryAsync(deviceId, rootId, path ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return entry is not null;
    }

    /// <summary>按文件名搜索手机目录。</summary>
    public async Task<IReadOnlyList<FileEntryDto>> SearchDeviceEntriesAsync(
        string deviceId,
        string rootId,
        string keyword,
        CancellationToken cancellationToken = default)
    {
        var entries = await _deviceFiles
            .SearchEntriesAsync(deviceId, rootId, keyword ?? string.Empty, 200, cancellationToken)
            .ConfigureAwait(false);
        return entries.Select(entry => ToDto(entry)).ToList();
    }

    /// <summary>下发手机文件操作作业。设备 id 由调用方（本机界面）给出。</summary>
    public async Task<FileJobDto> EnqueueDeviceJobAsync(
        string deviceId,
        string rootId,
        string operation,
        string? sourcePath,
        string? destinationPath,
        string? displayName,
        string? transferTaskId = null,
        CancellationToken cancellationToken = default)
    {
        var root = await _deviceFiles.GetRootAsync(deviceId, rootId, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            throw new InvalidOperationException("手机目录不存在或已撤销授权。");
        }

        if (!string.Equals(root.DeviceId, deviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不能操作其他设备的目录。");
        }

        var capabilityOk = await CheckCapabilityAsync(
            deviceId,
            rootId,
            operation,
            sourcePath,
            root.Capabilities,
            cancellationToken).ConfigureAwait(false);

        if (!capabilityOk)
        {
            throw new InvalidOperationException("该存储提供方不支持此操作。");
        }

        var jobId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var record = new FileOperationJobRecord
        {
            JobId = jobId,
            DeviceId = deviceId,
            RootId = rootId,
            Operation = operation,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            DisplayName = displayName,
            TransferTaskId = transferTaskId,
            Status = FileJobStatuses.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await _deviceFiles.InsertJobAsync(record, cancellationToken).ConfigureAwait(false);
        PulseJobSignal(deviceId);
        return ToDto(record);
    }


    /// <summary>
    /// 某台手机还没取走的作业。本机界面用它把"已经排队的保存请求"标出来：
    /// 手机休眠时作业会一直 pending，界面不能装作没发生过（否则用户会重复点，排出一堆重复下载）。
    /// </summary>
    public async Task<IReadOnlyList<FileJobDto>> ListPendingDeviceJobsAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var jobs = await _deviceFiles.ListPendingJobsAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return jobs.Select(job => ToDto(job)).ToList();
    }

    /// <summary>按 id 取一条作业（电脑端等待 list 作业的结果）。</summary>
    public async Task<FileJobDto?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var record = await _deviceFiles.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToDto(record);
    }

    /// <summary>挂起等一个"有新作业"的信号，最多等 <paramref name="timeout"/>。</summary>
    private static async Task WaitForSignalAsync(
        TaskCompletionSource<bool> signal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAny(signal.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：当作没等到，让调用方重新查一次库
        }
    }

    /// <summary>唤醒该设备上挂起的取作业请求。</summary>
    private static void PulseJobSignal(string deviceId)
    {
        if (JobSignals.TryRemove(deviceId, out var signal))
        {
            signal.TrySetResult(true);
        }
    }

    // ------------------------------------------------------------ 手机目录索引上行

    /// <summary>
    /// 手机上报授权目录的索引快照。整批替换：手机删掉的文件不会在电脑上留残影。
    /// </summary>
    public async Task<FileServiceResult<FileRootDto>> PublishIndexAsync(
        StoredDevice device,
        DeviceFileIndexDto index,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(index.RootId))
        {
            return new FileServiceFailure(ErrorCodes.InvalidRequest, "缺少 rootId。", 400);
        }

        var now = DateTimeOffset.UtcNow;
        var root = new DeviceFileRootRecord
        {
            RootId = index.RootId,
            DeviceId = device.DeviceId,
            DisplayName = index.DisplayName,
            ProviderKind = index.ProviderKind,
            Capabilities = index.Capabilities,
            GrantedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await _deviceFiles.UpsertRootAsync(root, cancellationToken).ConfigureAwait(false);

        var entries = index.Entries.Select(entry => new DeviceFileEntryRecord
        {
            RootId = index.RootId,
            RelativePath = entry.RelativePath ?? string.Empty,
            DisplayName = entry.Name,
            IsDirectory = entry.IsDirectory,
            SizeBytes = entry.SizeBytes,
            ModifiedUtc = entry.ModifiedUnixMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(entry.ModifiedUnixMs)
                : null,
            Capabilities = entry.Capabilities,
            UpdatedAtUtc = now,
        }).ToList();

        await _deviceFiles
            .ReplaceEntriesAsync(device.DeviceId, index.RootId, entries, cancellationToken)
            .ConfigureAwait(false);

        // 快照语义：手机附带的"当前全部有效根"里没有的，就是已经撤销的。
        // 老客户端不传该字段时跳过，避免误删。
        if (index.ActiveRootIds.Count > 0)
        {
            var keep = index.ActiveRootIds.ToList();
            if (!keep.Contains(index.RootId, StringComparer.Ordinal))
            {
                keep.Add(index.RootId);
            }

            var removed = await _deviceFiles
                .PruneRootsAsync(device.DeviceId, keep, cancellationToken)
                .ConfigureAwait(false);

            if (removed > 0)
            {
                Log.Information("剔除已撤销的授权根 {Count} 个 device={DeviceId}", removed, device.DeviceId);
            }
        }

        return FileServiceResult<FileRootDto>.Ok(new FileRootDto
        {
            RootId = index.RootId,
            Kind = FileRootKinds.Device,
            DisplayName = index.DisplayName,
            DeviceId = device.DeviceId,
            Capabilities = index.Capabilities,
        });
    }

    /// <summary>
    /// 只同步根的集合。授权被**全部**撤销时手机没有任何索引可上报，
    /// 没有这个入口，服务端会永远保留那几条已经不存在的授权目录。
    /// </summary>
    public async Task<FileServiceResult<FileSyncResultDto>> SyncIndexesAsync(
        StoredDevice device,
        DeviceFileSyncDto sync,
        CancellationToken cancellationToken = default)
    {
        var removed = await _deviceFiles
            .PruneRootsAsync(device.DeviceId, sync.ActiveRootIds ?? Array.Empty<string>(), cancellationToken)
            .ConfigureAwait(false);

        if (removed > 0)
        {
            Log.Information("同步索引：剔除 {Count} 个已撤销的根 device={DeviceId}", removed, device.DeviceId);
        }

        return FileServiceResult<FileSyncResultDto>.Ok(new FileSyncResultDto { RemovedRoots = removed });
    }

    // ------------------------------------------------------------ 下载（手机在浏览器里点文件）

    /// <summary>
    /// 解析某个根下的文件在电脑上的绝对路径。
    ///
    /// 手机只能给"根 id + 相对路径"，**不能**直接给绝对路径——
    /// 否则一台被控制的手机可以让服务端读任意文件。
    /// 共享目录要校验 Download 权限；整机根要求该设备有整机浏览权。
    /// </summary>
    public async Task<FileServiceResult<string>> ResolveFilePathAsync(
        StoredDevice device,
        string rootId,
        string path,
        CancellationToken cancellationToken = default,
        // 整文件夹下载要解析目录本身：权限校验完全一样，只是不把"是目录"当成错误
        bool allowDirectory = false)
    {
        var root = await ResolveRootAsync(device, rootId, cancellationToken).ConfigureAwait(false);
        if (!root.Success)
        {
            return root.Error!;
        }

        var relative = path ?? string.Empty;
        if (relative.Length == 0)
        {
            return new FileServiceFailure(ErrorCodes.InvalidRequest, "缺少文件路径。", 400);
        }

        if (root.Value!.Kind == FileRootKinds.Machine)
        {
            var machineResult = await _machineProvider
                .GetFileInfoAsync(relative, cancellationToken)
                .ConfigureAwait(false);

            if (!machineResult.Success)
            {
                return MapFailure(machineResult);
            }

            if (machineResult.Value is null || (machineResult.Value.IsDirectory && !allowDirectory))
            {
                return new FileServiceFailure(ErrorCodes.NotFound, "文件不存在。", 404);
            }

            var absolute = PathGuard.ResolveMachinePath(relative);
            if (!absolute.IsAllowed)
            {
                return new FileServiceFailure(
                    ErrorCodes.PathForbidden,
                    "路径非法。",
                    403,
                    ReasonCode: absolute.ReasonCode);
            }

            return FileServiceResult<string>.Ok(absolute.FullPath);
        }

        if (root.Value.Kind != FileRootKinds.Shared)
        {
            return new FileServiceFailure(
                ErrorCodes.InvalidRequest,
                "该根不支持直接下载。",
                400);
        }

        var directory = await _directories.GetAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return new FileServiceFailure(ErrorCodes.DirectoryNotAuthorized, "该目录未授权。", 403);
        }

        var permissions = await _directories
            .GetPermissionsAsync(rootId, device.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (!permissions.HasFlag(SharedDirectoryPermissions.Download))
        {
            return new FileServiceFailure(
                ErrorCodes.OperationNotPermitted,
                "没有下载该目录文件的权限。",
                403,
                ReasonCode: "permission_denied");
        }

        var provider = _providerFactory(directory.RootPath);
        var info = await provider.GetFileInfoAsync(relative, cancellationToken).ConfigureAwait(false);
        if (!info.Success)
        {
            return MapFailure(info);
        }

        if (info.Value is null || (info.Value.IsDirectory && !allowDirectory))
        {
            return new FileServiceFailure(ErrorCodes.NotFound, "文件不存在。", 404);
        }

        var resolved = PathGuard.Resolve(directory.RootPath, relative);
        if (!resolved.IsAllowed)
        {
            return new FileServiceFailure(
                ErrorCodes.PathForbidden,
                "路径非法。",
                403,
                ReasonCode: resolved.ReasonCode);
        }

        return FileServiceResult<string>.Ok(resolved.FullPath);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>
    /// 解析根：确认存在、属于当前设备、且当前设备有相应权限。
    /// 返回 <see cref="FileRootDto"/>（含能力位）。
    /// </summary>
    private async Task<FileServiceResult<FileRootDto>> ResolveRootAsync(
        StoredDevice device,
        string rootId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootId))
        {
            return new FileServiceFailure(ErrorCodes.InvalidRequest, "缺少 rootId。", 400);
        }

        var directory = await _directories.GetAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (directory is not null)
        {
            var permissions = await _directories
                .GetPermissionsAsync(rootId, device.DeviceId, cancellationToken)
                .ConfigureAwait(false);

            if (permissions == SharedDirectoryPermissions.None)
            {
                return new FileServiceFailure(
                    ErrorCodes.DirectoryNotAuthorized,
                    "该目录未授权给当前设备。",
                    403);
            }

            return FileServiceResult<FileRootDto>.Ok(new FileRootDto
            {
                RootId = rootId,
                Kind = FileRootKinds.Shared,
                DisplayName = directory.DisplayName,
                DeviceId = device.DeviceId,
                Capabilities = FileCapabilities.All,
                Permissions = (int)permissions,
            });
        }

        if (string.Equals(rootId, FileRootIds.Machine, StringComparison.Ordinal))
        {
            if (!MachineAccessLevels.CanBrowse(MachineAccessLevels.Normalize(device.MachineAccess)))
            {
                return new FileServiceFailure(
                    ErrorCodes.DirectoryNotAuthorized,
                    "未授权浏览整机文件。",
                    403,
                    ReasonCode: "machine_access_denied");
            }

            return FileServiceResult<FileRootDto>.Ok(new FileRootDto
            {
                RootId = FileRootIds.Machine,
                Kind = FileRootKinds.Machine,
                DisplayName = "此电脑",
                DeviceId = device.DeviceId,
                Capabilities = (int)(FileCapabilities.ListDirectory
                    | FileCapabilities.ReadFile
                    | FileCapabilities.GetFileInfo),
                Permissions = 0,
            });
        }

        var deviceRoot = await _deviceFiles
            .GetRootAsync(device.DeviceId, rootId, cancellationToken)
            .ConfigureAwait(false);
        if (deviceRoot is not null)
        {
            return FileServiceResult<FileRootDto>.Ok(new FileRootDto
            {
                RootId = rootId,
                Kind = FileRootKinds.Device,
                DisplayName = deviceRoot.DisplayName,
                DeviceId = deviceRoot.DeviceId,
                Capabilities = deviceRoot.Capabilities,
            });
        }

        // 按本设备查不到：rootId 是设备内命名的（整机根固定 phone-all），
        // 另一台手机很可能也有同名根。分清"是别人的"和"真没了"再回话。
        var foreign = await _deviceFiles
            .FindRootAnyDeviceAsync(rootId, cancellationToken)
            .ConfigureAwait(false);

        if (foreign is not null)
        {
            return new FileServiceFailure(
                ErrorCodes.Forbidden,
                "不能访问其他设备的目录。",
                403);
        }

        return new FileServiceFailure(
            ErrorCodes.DirectoryNotAuthorized,
            "该目录不存在或未授权。",
            403);
    }

    /// <summary>在共享目录上执行操作：校验权限 → 调 provider → 映射失败。</summary>
    /// <summary>
    /// 整机档位是否够用：不够返回 403，够用返回 null。
    /// 单条与批量删除共用一份判断，免得两边文案/口径走散。
    /// </summary>
    private static FileServiceFailure? MachineWriteFailure(int level, bool requireDelete)
    {
        if (!MachineAccessLevels.CanWrite(level))
        {
            // 只读档位下明确告知原因：用户可能忘了自己把档位调低了
            return new FileServiceFailure(
                ErrorCodes.OperationNotPermitted,
                level == MachineAccessLevels.ReadOnly
                    ? "这台电脑对该设备是只读。请在电脑端把访问权限改为可读写。"
                    : "该设备没有访问这台电脑的权限。",
                403,
                ReasonCode: "machine_read_only");
        }

        // 删除是唯一不可恢复的动作，档位里单独一档；"可读写（不能删）"要在这里挡住
        if (requireDelete && !MachineAccessLevels.CanDelete(level))
        {
            return new FileServiceFailure(
                ErrorCodes.OperationNotPermitted,
                "这台电脑对该设备不允许删除文件。请在电脑端把访问权限改为「可读写 + 删除」。",
                403,
                ReasonCode: "machine_delete_denied");
        }

        return null;
    }

    private async Task<FileServiceResult<bool>> RunOnSharedAsync(
        StoredDevice device,
        string rootId,
        SharedDirectoryPermissions required,
        Func<IFileProvider, SharedDirectoryRecord, Task<FileOperationResult>> action,
        CancellationToken cancellationToken)
    {
        var root = await ResolveRootAsync(device, rootId, cancellationToken).ConfigureAwait(false);
        if (!root.Success)
        {
            return root.Error!;
        }

        if (root.Value!.Kind == FileRootKinds.Machine)
        {
            var level = MachineAccessLevels.Normalize(device.MachineAccess);
            var denial = MachineWriteFailure(level, required.HasFlag(SharedDirectoryPermissions.Delete));
            if (denial is not null)
            {
                return denial;
            }

            var machineResult = await action(_machineProvider, MachineDirectory)
                .ConfigureAwait(false);
            return machineResult.Success
                ? FileServiceResult<bool>.Ok(true)
                : MapFailure(machineResult);
        }

        if (root.Value!.Kind != FileRootKinds.Shared)
        {
            return new FileServiceFailure(
                ErrorCodes.InvalidRequest,
                "手机目录的操作需通过作业下发。",
                400);
        }

        var directory = await _directories.GetAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return new FileServiceFailure(ErrorCodes.DirectoryNotAuthorized, "该目录未授权。", 403);
        }

        var permissions = await _directories
            .GetPermissionsAsync(rootId, device.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (!permissions.HasFlag(required))
        {
            return new FileServiceFailure(
                ErrorCodes.OperationNotPermitted,
                $"没有该操作的权限（需要 {required}）。",
                403,
                ReasonCode: "permission_denied");
        }

        var provider = _providerFactory(directory.RootPath);
        var result = await action(provider, directory).ConfigureAwait(false);
        if (!result.Success)
        {
            return MapFailure(result);
        }

        return FileServiceResult<bool>.Ok(true);
    }

    /// <summary>是不是一个盘符根（<c>C:</c> / <c>C:/</c>）。</summary>
    private static bool IsDriveRoot(string path)
        => path.Length == 2 && path[1] == ':';

    /// <summary>整机根按档位给出的能力位。</summary>
    private static int MachineCapabilities(int level)
    {
        var capabilities = FileCapabilities.ListDirectory
            | FileCapabilities.ReadFile
            | FileCapabilities.GetFileInfo;

        if (MachineAccessLevels.CanWrite(level))
        {
            capabilities |= FileCapabilities.WriteFile
                | FileCapabilities.CreateDirectory
                | FileCapabilities.Rename
                | FileCapabilities.Move;
        }

        // 删除单独一档：不可恢复，不跟"新建/改名"打包
        if (MachineAccessLevels.CanDelete(level))
        {
            capabilities |= FileCapabilities.Delete;
        }

        return capabilities;
    }

    /// <summary>
    /// 整机根没有共享目录记录，但 <see cref="RunOnSharedAsync"/> 的委托签名要求一个。
    /// 四个写操作的委托都不用它，只是为了统一签名；用静态实例避免每次分配。
    /// </summary>
    private static readonly SharedDirectoryRecord MachineDirectory = new()
    {
        DirectoryId = FileRootIds.Machine,
        DisplayName = "此电脑",
        RootPath = string.Empty,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    /// <summary>把 provider 的操作结果映射成 HTTP 层失败。</summary>
    private static FileServiceFailure MapFailure<T>(FileOperationResult<T> result) => MapFailure(
        FileOperationResult.Fail(result.Status, result.ReasonCode, result.Message));

    /// <summary>把 provider 的操作结果映射成 HTTP 层失败。</summary>
    private static FileServiceFailure MapFailure(FileOperationResult result) => result.Status switch
    {
        FileOperationStatus.NotFound => new FileServiceFailure(
            ErrorCodes.NotFound, result.Message, 404, result.ReasonCode),
        FileOperationStatus.PathInvalid => new FileServiceFailure(
            ErrorCodes.PathForbidden, result.Message, 403, result.ReasonCode),
        FileOperationStatus.AlreadyExists => new FileServiceFailure(
            ErrorCodes.TargetAlreadyExists, result.Message, 409, result.ReasonCode),
        FileOperationStatus.PermissionDenied => new FileServiceFailure(
            ErrorCodes.OperationNotPermitted, result.Message, 403, result.ReasonCode),
        FileOperationStatus.Unsupported => new FileServiceFailure(
            ErrorCodes.OperationUnsupported, result.Message, 501, result.ReasonCode),
        FileOperationStatus.AuthorizationRevoked => new FileServiceFailure(
            ErrorCodes.AuthorizationRevoked, result.Message, 403, result.ReasonCode),
        FileOperationStatus.StorageUnavailable => new FileServiceFailure(
            ErrorCodes.StorageUnavailable, result.Message, 503, result.ReasonCode, Retryable: true),
        _ => new FileServiceFailure(
            ErrorCodes.InternalError, result.Message.Length > 0 ? result.Message : "文件操作失败。", 500, result.ReasonCode),
    };

    /// <summary>
    /// 能力检查：不同操作看的是不同对象的能力。
    ///
    /// - **新建**：看父目录能不能建子项（在父目录里建）；
    /// - **重命名/删除/移动**：看条目自己能不能被改。
    ///
    /// 索引里每个条目带的是**自身**的能力，所以这里按语义取对应条目。
    /// 找不到条目时退回根的能力——宁可保守拒绝，也不假装支持。
    /// </summary>
    private async Task<bool> CheckCapabilityAsync(
        string deviceId,
        string rootId,
        string operation,
        string? sourcePath,
        int fallbackCapabilities,
        CancellationToken cancellationToken)
    {
        var required = RequiredCapability(operation);
        if (required == FileCapabilities.None)
        {
            return true;
        }

        // 新建作业的 sourcePath 表示"在哪个目录里建"（就是父目录本身）；
        // 其余操作表示"对哪个条目做"（条目自己的路径）。
        var lookupPath = operation == FileOperations.Create
            ? (sourcePath ?? string.Empty)
            : (sourcePath ?? string.Empty);

        var entry = await _deviceFiles
            .GetEntryAsync(deviceId, rootId, lookupPath, cancellationToken)
            .ConfigureAwait(false);

        var caps = entry?.Capabilities ?? fallbackCapabilities;
        return (caps & required) == required;
    }

    /// <summary>
    /// 共享目录的权限位 → 该目录条目支持的操作能力位。
    ///
    /// 界面「属性」会显示这份能力，用户据此知道这台设备在这个目录里能做什么。
    /// </summary>
    private static int CapabilitiesFor(SharedDirectoryPermissions permissions)
    {
        var capabilities = FileCapabilities.ListDirectory | FileCapabilities.GetFileInfo;

        if (permissions.HasFlag(SharedDirectoryPermissions.Download))
        {
            capabilities |= FileCapabilities.ReadFile;
        }

        if (permissions.HasFlag(SharedDirectoryPermissions.Upload))
        {
            capabilities |= FileCapabilities.WriteFile;
        }

        if (permissions.HasFlag(SharedDirectoryPermissions.Create))
        {
            capabilities |= FileCapabilities.CreateDirectory;
        }

        if (permissions.HasFlag(SharedDirectoryPermissions.Rename))
        {
            capabilities |= FileCapabilities.Rename;
        }

        if (permissions.HasFlag(SharedDirectoryPermissions.Delete))
        {
            capabilities |= FileCapabilities.Delete;
        }

        if (permissions.HasFlag(SharedDirectoryPermissions.Move))
        {
            capabilities |= FileCapabilities.Move;
        }

        return (int)capabilities;
    }

    /// <summary>某操作需要的能力位。</summary>
    private static int RequiredCapability(string operation) => operation switch
    {
        FileOperations.Create => FileCapabilities.CreateDirectory,
        FileOperations.Rename => FileCapabilities.Rename,
        FileOperations.Delete => FileCapabilities.Delete,
        FileOperations.Move => FileCapabilities.Move,
        // 复制 = 读源 + 写目标（目录还要建目录）；不引入新能力位，
        // 老手机上报的能力位里已经有这几项。
        FileOperations.Copy => FileCapabilities.ReadFile | FileCapabilities.WriteFile,
        FileOperations.Upload => FileCapabilities.WriteFile,
        FileOperations.Download => FileCapabilities.ReadFile,
        FileOperations.List => FileCapabilities.ListDirectory,
        _ => FileCapabilities.None,
    };

    private static FileEntryDto ToDto(FileEntry entry, int capabilities = 0) => new()
    {
        Name = entry.Name,
        RelativePath = entry.RelativePath,
        IsDirectory = entry.IsDirectory,
        Capabilities = capabilities,
        SizeBytes = entry.SizeBytes,
        ModifiedUnixMs = entry.LastModifiedUtc.ToUnixTimeMilliseconds(),
    };

    private static FileEntryDto ToDto(DeviceFileEntryRecord entry) => new()
    {
        Name = entry.DisplayName,
        RelativePath = entry.RelativePath,
        IsDirectory = entry.IsDirectory,
        SizeBytes = entry.SizeBytes,
        ModifiedUnixMs = entry.ModifiedUtc?.ToUnixTimeMilliseconds() ?? 0,
        Capabilities = entry.Capabilities,
    };

    private static FileJobDto ToDto(FileOperationJobRecord job, string? statusOverride = null) => new()
    {
        JobId = job.JobId,
        RootId = job.RootId,
        Operation = job.Operation,
        SourcePath = job.SourcePath,
        DestinationPath = job.DestinationPath,
        DisplayName = job.DisplayName,
        TransferTaskId = job.TransferTaskId,
        Status = statusOverride ?? job.Status,
        ErrorCode = job.ErrorCode,
        ErrorMessage = job.ErrorMessage,
        Payload = job.ResultPayload,
    };
}
