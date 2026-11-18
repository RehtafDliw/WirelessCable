using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Serilog;
using WirelessCable.Core.Security;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Files;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.Transfer;

/// <summary>
/// 传输引擎：唯一负责文件字节落盘的地方。UI 与端点都不得自行读写文件。
///
/// 设计要点：
/// <list type="bullet">
/// <item>上传先写预分配临时文件，整文件 SHA-256 通过后才移动进共享目录；失败路径一律删临时文件；</item>
/// <item>每个分块按偏移独立写入、独立校验（X-WC-Chunk-Sha256），状态持久化到 TransferChunks；</item>
/// <item>已完成的分块幂等：同哈希重发直接回执不重复写入，不同哈希拒绝；</item>
/// <item>中断（断网/进程重启）任务转入 interrupted 保留数据，超过保留期才判失败清理；</item>
/// <item>恢复一律以数据库里的分块位图为准，内存会话只是缓存，可随时重建；</item>
/// <item>同名默认自动改名，绝不静默覆盖；显式 overwrite 才会替换已有文件；</item>
/// <item>哈希与落盘这类重活受 <see cref="TransferOptions.MaxConcurrentHashJobs"/> 限制。</item>
/// </list>
/// </summary>
public sealed class TransferEngine : IDisposable
{
    /// <summary>电脑端在 source_device / target_device 列里的标识。</summary>
    public const string DesktopDevice = "desktop";

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly TransferRepository _repository;
    private readonly TransferOptions _options;
    private readonly SharedDirectoryResolver? _directories;
    private readonly ConcurrentDictionary<string, UploadSession> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _hashGate;
    private readonly Lock _nameGate = new();
    private readonly HashSet<string> _reservedPaths = new(PathComparer);

    /// <summary>
    /// 曾经用过的接收目录。换目录之后旧记录里的文件仍要能删，
    /// 但只允许删这几个根之内的东西——用户自己选的目录才作数。
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _sharedRoots = new(PathComparer);
    private readonly Timer _janitor;
    private string _sharedDirectory;
    private int _sweeping;
    private bool _disposed;

    public TransferEngine(
        TransferRepository repository,
        TransferOptions options,
        SharedDirectoryResolver? directories = null)
    {
        _repository = repository;
        _options = options;
        _directories = directories;
        _sharedDirectory = options.SharedDirectory;
        _sharedRoots[options.SharedDirectory] = 0;
        _hashGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentHashJobs));
        _janitor = new Timer(static state => _ = ((TransferEngine)state!).SweepAsync(), this,
            options.JanitorInterval, options.JanitorInterval);
    }

    /// <summary>进度事件。每个分块写入/下发后触发一次，调用方自行节流。</summary>
    public event EventHandler<TransferTaskDto>? Progress;

    /// <summary>共享目录（用户可见的落点）。</summary>
    public string SharedDirectory => _sharedDirectory;

    /// <summary>
    /// 换接收目录（设置页「更改」）。只影响之后新建的任务；
    /// 已经在传的任务仍落它创建时算好的路径，不会中途换盘。
    /// 目录必须存在且可写，由调用方先校验。
    /// </summary>
    public void UpdateSharedDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _sharedDirectory = path;
            _sharedRoots[path] = 0;
        }
    }

    // ---------------------------------------------------------------- 上传

    /// <summary>创建上传任务：校验名称与目录与冲突策略、预分配临时文件、建分块位图、写库。</summary>
    public async Task<TransferResult<TransferTaskDto>> CreateUploadAsync(
        StoredDevice device,
        TransferStartRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var nameCheck = NormalizeFileName(request.FileName);
        if (!nameCheck.Success)
        {
            return TransferResult<TransferTaskDto>.Fail(nameCheck.Error!);
        }

        var fileName = nameCheck.Value!;

        if (!TransferConflictPolicies.IsValid(request.Conflict))
        {
            return Fail(ErrorCodes.InvalidRequest, "未知的冲突处理策略。", 400,
                details: request.Conflict);
        }

        var conflict = request.Conflict ?? TransferConflictPolicies.Rename;

        if (request.TotalBytes < 0 || request.TotalBytes > _options.MaxFileBytes)
        {
            return Fail(ErrorCodes.InvalidRequest, "文件大小超出允许范围。", 400,
                details: $"max={_options.MaxFileBytes}");
        }

        var active = await _repository.CountActiveForDeviceAsync(device.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        if (active >= _options.MaxActiveTransfersPerDevice)
        {
            return Fail(ErrorCodes.TooManyActiveTransfers,
                "该设备还有未完成的传输任务，请等待完成或取消后再试。", 409,
                retryable: true, details: $"active={active}");
        }

        var directoryCheck = await ResolveTargetDirectoryAsync(
            device,
            request.DirectoryId,
            request.RelativeDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!directoryCheck.Success)
        {
            return TransferResult<TransferTaskDto>.Fail(directoryCheck.Error!);
        }

        var uploadTarget = directoryCheck.Value!;
        var directory = uploadTarget.Directory;
        var directoryRoot = uploadTarget.Root;
        var directoryId = uploadTarget.DirectoryId;

        if (_options.FreeSpaceProbe.GetAvailableBytes(_options.TempDirectory)
            < request.TotalBytes + ProtocolConstants.TransferFreeSpaceMarginBytes)
        {
            return Fail(ErrorCodes.InsufficientStorage, "电脑剩余空间不足。", 507, retryable: true);
        }

        var taskId = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(_options.TempDirectory, taskId + ".wcpart");
        var chunkSize = NormalizeChunkSize(request.RequestedChunkSize);
        var chunkCount = request.TotalBytes == 0
            ? 0
            : (int)((request.TotalBytes + chunkSize - 1) / chunkSize);

        (string FullPath, string FileName) reserved;
        try
        {
            Directory.CreateDirectory(directory);
            var reservation = ReserveByPolicy(directory, fileName, conflict);
            if (!reservation.Success)
            {
                return TransferResult<TransferTaskDto>.Fail(reservation.Error!);
            }

            reserved = reservation.Value!;

            // 预分配到最终大小：分块按偏移随机写入，进程在写块途中被杀后，
            // 长度不符即可判定临时文件不可信（见 RehydrateUploadSessionAsync）
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                temp.SetLength(request.TotalBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            Log.Warning(ex, "创建上传任务失败 temp={TempPath}", LogSafeName(tempPath));
            return Fail(ErrorCodes.InsufficientStorage, "无法在目标目录创建文件。", 507, retryable: true);
        }

        var now = DateTimeOffset.UtcNow;
        var relativePath = ToRelativePath(directoryRoot, reserved.FullPath);

        var record = new TransferTaskRecord
        {
            TaskId = taskId,
            Direction = TransferDirections.Upload,
            DeviceId = device.DeviceId,
            FileName = reserved.FileName,
            RelativePath = relativePath,
            TotalBytes = request.TotalBytes,
            TransferredBytes = 0,
            ChunkSize = chunkSize,
            ChunkCount = chunkCount,
            Status = TransferStates.Preparing,
            Sha256 = NormalizeSha(request.Sha256),
            TempPath = tempPath,
            LocalPath = null,
            CompletedChunks = 0,
            Conflict = conflict,
            SourceDevice = device.DeviceId,
            TargetDevice = DesktopDevice,
            DirectoryId = directoryId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        try
        {
            await _repository.InsertAsync(record, cancellationToken).ConfigureAwait(false);
            await _repository.InsertChunksAsync(BuildChunkPlan(taskId, chunkSize, request.TotalBytes, now), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ReleaseReservation(reserved.FullPath);
            TryDeleteFile(tempPath);
            throw;
        }

        var session = new UploadSession
        {
            TaskId = taskId,
            DeviceId = device.DeviceId,
            DeviceName = device.DisplayName,
            FileName = reserved.FileName,
            RelativePath = relativePath,
            TempPath = tempPath,
            FinalPath = reserved.FullPath,
            RootPath = directoryRoot,
            TotalBytes = request.TotalBytes,
            ChunkSize = chunkSize,
            ChunkCount = chunkCount,
            Conflict = conflict,
            Completed = new bool[chunkCount],
            Hashes = new string?[chunkCount],
            CreatedAtUtc = now,
            LastActivityUtc = now,
        };

        _uploads[taskId] = session;

        Log.Information(
            "上传任务已创建 task={TaskId} device={DeviceId} file={FileName} bytes={Total} chunks={Chunks} conflict={Conflict}",
            taskId, device.DeviceId, LogSafeName(reserved.FileName), request.TotalBytes, chunkCount, conflict);

        return TransferResult<TransferTaskDto>.Ok(ToDto(session));
    }

    /// <summary>
    /// 写入一个分块。任意顺序：客户端按服务端位图补差集。
    /// 已完成的块同哈希重发幂等回执，不重复写入；内容哈希不符拒绝，该块保持未完成。
    /// </summary>
    public async Task<TransferResult<ChunkAckDto>> WriteChunkAsync(
        StoredDevice device,
        string taskId,
        int index,
        Stream body,
        long contentLength,
        string? chunkSha256,
        CancellationToken cancellationToken = default)
    {
        if (!_uploads.TryGetValue(taskId, out var session))
        {
            var rehydrated = await RehydrateUploadSessionAsync(device, taskId, cancellationToken)
                .ConfigureAwait(false);
            if (!rehydrated.Success)
            {
                return TransferResult<ChunkAckDto>.Fail(rehydrated.Error!);
            }

            session = rehydrated.Value!;
        }

        if (!string.Equals(session.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        if (TransferStates.IsTerminal(session.State))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已结束，不能再写入。", 409);
        }

        // 暂停期间拒收分块：属临时状态而非错误，标 retryable 让客户端稍后重试
        if (string.Equals(session.State, TransferStates.Paused, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已暂停，请先继续。", 409, retryable: true);
        }

        var providedSha = NormalizeSha(chunkSha256);
        if (providedSha is null)
        {
            return Fail(ErrorCodes.InvalidRequest, "缺少分块 SHA-256 请求头。", 400,
                details: ProtocolConstants.ChunkSha256Header);
        }

        // 同一任务并发写分块会互相覆盖，直接拒绝让客户端重试
        if (!await session.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Fail(ErrorCodes.ChunkInFlight, "该任务正在写入另一个分块。", 409, retryable: true);
        }

        try
        {
            if (index < 0 || index >= session.ChunkCount)
            {
                return Fail(ErrorCodes.InvalidRequest, "分块序号越界。", 400,
                    details: $"chunkCount={session.ChunkCount}");
            }

            var expected = ExpectedChunkLength(session.ChunkSize, session.TotalBytes, index);
            if (contentLength != expected)
            {
                return Fail(ErrorCodes.InvalidRequest, "分块长度与任务声明不符。", 400,
                    details: $"expected={expected}");
            }

            if (session.Completed[index])
            {
                // 幂等：同哈希重发不重复写入；不同哈希说明客户端数据与已落盘不一致
                if (string.Equals(session.Hashes[index], providedSha, StringComparison.Ordinal))
                {
                    return TransferResult<ChunkAckDto>.Ok(AckOf(session, index, (int)expected, alreadyCompleted: true));
                }

                return Fail(ErrorCodes.ChunkChecksumMismatch, "该分块已完成，且与本次内容不一致。", 409);
            }

            var offset = (long)index * session.ChunkSize;
            string actualSha;

            await using (var file = new FileStream(
                             session.TempPath,
                             FileMode.Open,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                file.Seek(offset, SeekOrigin.Begin);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var written = await CopyExactlyAsync(body, file, expected, hasher, cancellationToken)
                    .ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (written != expected)
                {
                    // 半块：该块保持 pending，重传时整块覆盖，不需要回滚文件内容
                    return Fail(ErrorCodes.InvalidRequest, "分块数据不完整。", 400,
                        details: $"received={written}");
                }

                actualSha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (!string.Equals(actualSha, providedSha, StringComparison.Ordinal))
            {
                // 链路上损坏：块保持 pending，客户端可原样重传覆盖
                Log.Warning("分块校验失败 task={TaskId} index={Index}", taskId, index);
                return Fail(ErrorCodes.ChunkChecksumMismatch, "分块校验失败，请重传该块。", 409,
                    retryable: true);
            }

            var now = DateTimeOffset.UtcNow;
            session.Completed[index] = true;
            session.Hashes[index] = providedSha;
            session.Written += expected;
            session.State = TransferStates.Transferring;
            session.LastActivityUtc = now;

            await _repository
                .CompleteChunkAsync(taskId, index, providedSha, session.Written,
                    TransferStates.Transferring, now, cancellationToken)
                .ConfigureAwait(false);

            RaiseProgress(ToDto(session));

            return TransferResult<ChunkAckDto>.Ok(AckOf(session, index, (int)expected, alreadyCompleted: false));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>提交校验：分块到齐 → 流式算 SHA-256 → 一致才按冲突策略移动到共享目录。</summary>
    public async Task<TransferResult<TransferCompleteResponseDto>> CompleteUploadAsync(
        StoredDevice device,
        string taskId,
        string? declaredSha256,
        CancellationToken cancellationToken = default)
    {
        if (!_uploads.TryGetValue(taskId, out var session))
        {
            var rehydrated = await RehydrateUploadSessionAsync(device, taskId, cancellationToken)
                .ConfigureAwait(false);
            if (!rehydrated.Success)
            {
                return TransferResult<TransferCompleteResponseDto>.Fail(rehydrated.Error!);
            }

            session = rehydrated.Value!;
        }

        if (TransferStates.IsTerminal(session.State))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已结束。", 409);
        }

        if (!await session.Gate.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false))
        {
            return Fail(ErrorCodes.ChunkInFlight, "该任务正在写入分块，请稍后重试。", 409, retryable: true);
        }

        try
        {
            if (session.Written != session.TotalBytes || session.Completed.Any(done => !done))
            {
                return Fail(ErrorCodes.TransferStateInvalid, "文件尚未接收完整。", 409,
                    retryable: true, details: "incomplete");
            }

            var declared = NormalizeSha(declaredSha256);
            if (declared is null)
            {
                return Fail(ErrorCodes.InvalidRequest, "缺少整文件 SHA-256。", 400);
            }

            session.State = TransferStates.Verifying;
            session.LastActivityUtc = DateTimeOffset.UtcNow;
            await _repository
                .TouchAsync(taskId, session.State, session.LastActivityUtc, cancellationToken)
                .ConfigureAwait(false);

            string actual;
            try
            {
                actual = await ComputeSha256Async(session.TempPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FailSessionAsync(session, ErrorCodes.InsufficientStorage, "读取临时文件失败。", cancellationToken)
                    .ConfigureAwait(false);
                return Fail(ErrorCodes.InsufficientStorage, "读取临时文件失败。", 507);
            }

            if (!string.Equals(actual, declared, StringComparison.OrdinalIgnoreCase))
            {
                await FailSessionAsync(session, ErrorCodes.ChecksumMismatch,
                    "文件校验失败，内容与发送端不一致。", cancellationToken).ConfigureAwait(false);
                return Fail(ErrorCodes.ChecksumMismatch, "文件校验失败，内容与发送端不一致。", 409,
                    details: $"expected={declared[..Math.Min(12, declared.Length)]}");
            }

            if (_options.FreeSpaceProbe.GetAvailableBytes(Path.GetDirectoryName(session.FinalPath)!)
                < 1024L * 1024)
            {
                await FailSessionAsync(session, ErrorCodes.InsufficientStorage, "目标磁盘空间不足。", cancellationToken)
                    .ConfigureAwait(false);
                return Fail(ErrorCodes.InsufficientStorage, "目标磁盘空间不足。", 507);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(session.FinalPath)!);
                CommitByPolicy(session);
            }
            catch (InvalidOperationException)
            {
                // skip 策略落盘时发现目标已存在
                await FailSessionAsync(session, ErrorCodes.Conflict, "目标位置已存在同名文件。", cancellationToken)
                    .ConfigureAwait(false);
                return Fail(ErrorCodes.Conflict, "目标位置已存在同名文件。", 409);
            }
            catch (IOException) when (File.Exists(session.FinalPath))
            {
                // rename 策略：占位期间被别人抢先创建，换名重试一次
                var retry = ReserveTarget(Path.GetDirectoryName(session.FinalPath)!, session.FileName);
                ReleaseReservation(session.FinalPath);
                session.FinalPath = retry.FullPath;
                session.FileName = retry.FileName;
                session.RelativePath = ToRelativePath(session.RootPath, retry.FullPath);
                File.Move(session.TempPath, session.FinalPath, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FailSessionAsync(session, ErrorCodes.InsufficientStorage, "写入目标目录失败。", cancellationToken)
                    .ConfigureAwait(false);
                return Fail(ErrorCodes.InsufficientStorage, "写入目标目录失败。", 507);
            }

            var now = DateTimeOffset.UtcNow;
            session.State = TransferStates.Completed;

            await _repository.UpdateResultAsync(
                taskId,
                TransferStates.Completed,
                actual,
                null,
                null,
                session.FinalPath,
                now,
                now,
                cancellationToken).ConfigureAwait(false);

            _uploads.TryRemove(taskId, out _);
            ReleaseReservation(session.FinalPath);

            Log.Information(
                "上传完成 task={TaskId} device={DeviceId} file={FileName} bytes={Total} sha={ShaPrefix}",
                taskId, device.DeviceId, LogSafeName(session.FileName), session.TotalBytes, actual[..12]);

            var dto = ToDto(session);
            dto.State = TransferStates.Completed;
            dto.Sha256 = actual;
            dto.TransferredBytes = session.TotalBytes;
            dto.UpdatedAtUnixMs = now.ToUnixTimeMilliseconds();
            RaiseProgress(dto);

            return TransferResult<TransferCompleteResponseDto>.Ok(new TransferCompleteResponseDto
            {
                TaskId = taskId,
                State = TransferStates.Completed,
                FileName = session.FileName,
                RelativePath = session.RelativePath,
                SizeBytes = session.TotalBytes,
                Sha256 = actual,
                DurationMs = (long)(now - session.CreatedAtUtc).TotalMilliseconds,
            });
        }
        finally
        {
            session.Gate.Release();
        }
    }

    // ---------------------------------------------------------------- 下载

    /// <summary>
    /// 电脑端选中文件后创建下载任务。<b>只能由本机调用</b>，客户端无法指定任意路径。
    /// </summary>
    /// <param name="purpose">
    /// 去向。默认 <see cref="TransferPurposes.Inbox"/>（手机收件目录，可自动接收）；
    /// 写进手机授权目录时必须传 <see cref="TransferPurposes.Directory"/>，
    /// 否则手机会把它抢先拉进收件箱，作业再落盘就撞车。
    /// </param>
    public async Task<TransferResult<TransferTaskDto>> CreateDownloadAsync(
        StoredDevice device,
        string sourcePath,
        string? purpose = null,
        CancellationToken cancellationToken = default,
        // 整文件夹批量下载时传同一个值，界面据此把 N 个任务收成一行。
        string? batchId = null,
        string? batchName = null,
        // 相对路径覆盖：文件夹下载时保留子目录结构（"子目录/文件名"）。
        string? relativePath = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return Fail(ErrorCodes.FileInaccessible, "源文件不存在或不可读。", 403);
        }

        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Fail(ErrorCodes.FileInaccessible, "不允许发送符号链接或目录联接。", 403);
            }

            if (info.Length > _options.MaxFileBytes)
            {
                return Fail(ErrorCodes.InvalidRequest, "文件超过允许的大小上限。", 400);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCodes.FileInaccessible, "无法读取源文件信息。", 403);
        }

        string sha;
        try
        {
            sha = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ErrorCodes.FileInaccessible, "读取源文件失败。", 403);
        }

        var taskId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var chunkSize = _options.DefaultChunkSize;
        var chunkCount = info.Length == 0 ? 0 : (int)((info.Length + chunkSize - 1) / chunkSize);

        var record = new TransferTaskRecord
        {
            TaskId = taskId,
            Direction = TransferDirections.Download,
            Purpose = purpose == TransferPurposes.Directory
                ? TransferPurposes.Directory
                : TransferPurposes.Inbox,
            DeviceId = device.DeviceId,
            FileName = info.Name,
            RelativePath = string.IsNullOrWhiteSpace(relativePath) ? info.Name : relativePath,
            TotalBytes = info.Length,
            TransferredBytes = 0,
            ChunkSize = chunkSize,
            ChunkCount = chunkCount,
            Status = TransferStates.Waiting,
            Sha256 = sha,
            LocalPath = info.FullName,
            CompletedChunks = 0,
            Conflict = null,
            SourceDevice = DesktopDevice,
            TargetDevice = device.DeviceId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            BatchId = batchId,
            BatchName = batchName,
        };

        await _repository.InsertAsync(record, cancellationToken).ConfigureAwait(false);
        await _repository.InsertChunksAsync(BuildChunkPlan(taskId, chunkSize, info.Length, now), cancellationToken)
            .ConfigureAwait(false);

        Log.Information(
            "下载任务已创建 task={TaskId} device={DeviceId} file={FileName} bytes={Total}",
            taskId, device.DeviceId, LogSafeName(info.Name), info.Length);

        return TransferResult<TransferTaskDto>.Ok(ToDto(record, device.DisplayName));
    }

    /// <summary>读取一个下载分块。首次下发时惰性计算并回写分块哈希。调用方负责释放返回的流。</summary>
    public async Task<TransferResult<ChunkReadResult>> ReadChunkAsync(
        StoredDevice device,
        string taskId,
        int index,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        var ownership = CheckDownloadAccess(record, device);
        if (!ownership.Success)
        {
            return TransferResult<ChunkReadResult>.Fail(ownership.Error!);
        }

        if (TransferStates.IsTerminal(record.Status))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已结束。", 409);
        }

        if (string.Equals(record.Status, TransferStates.Paused, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已暂停，请先继续。", 409, retryable: true);
        }

        if (index < 0 || index >= record.ChunkCount)
        {
            return Fail(ErrorCodes.InvalidRequest, "分块序号越界。", 400,
                details: $"chunkCount={record.ChunkCount}");
        }

        var path = record.LocalPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            await FailRecordAsync(record, ErrorCodes.FileInaccessible, "源文件已不可读。", cancellationToken)
                .ConfigureAwait(false);
            return Fail(ErrorCodes.FileInaccessible, "源文件已不可读。", 403);
        }

        var length = ExpectedChunkLength(record.ChunkSize, record.TotalBytes, index);
        var offset = (long)index * record.ChunkSize;

        var chunk = await _repository.GetChunkAsync(taskId, index, cancellationToken).ConfigureAwait(false);
        var chunkSha = chunk?.Sha256;
        if (chunkSha is null)
        {
            // 惰性计算：读一遍该块算哈希并回写，之后（含续传重发）都直接查表
            chunkSha = await ComputeRangeSha256Async(path, offset, length, cancellationToken)
                .ConfigureAwait(false);
            await _repository.UpdateChunkSha256Async(taskId, index, chunkSha, cancellationToken)
                .ConfigureAwait(false);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, useAsync: true);

            if (stream.Length != record.TotalBytes)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                await FailRecordAsync(record, ErrorCodes.TransferStateInvalid, "源文件大小已变化。", cancellationToken)
                    .ConfigureAwait(false);
                return Fail(ErrorCodes.TransferStateInvalid, "源文件大小已变化。", 409);
            }

            stream.Seek(offset, SeekOrigin.Begin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await FailRecordAsync(record, ErrorCodes.FileInaccessible, "打开源文件失败。", cancellationToken)
                .ConfigureAwait(false);
            return Fail(ErrorCodes.FileInaccessible, "打开源文件失败。", 403);
        }

        // 拉块只证明对端还活着；进度（transferred_bytes）只由回执推进，杜绝"读了就算收到"
        var now = DateTimeOffset.UtcNow;
        await _repository.TouchAsync(taskId, TransferStates.Transferring, now, cancellationToken)
            .ConfigureAwait(false);

        RaiseProgress(ToDto(record, device.DisplayName) with
        {
            State = TransferStates.Transferring,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        });

        return TransferResult<ChunkReadResult>.Ok(
            new ChunkReadResult(
                new BoundedReadStream(stream, length),
                length,
                record.Sha256 ?? string.Empty,
                record.TotalBytes,
                index,
                chunkSha));
    }

    /// <summary>
    /// 下载分块回执：客户端确认该块已落盘并附本地算出的哈希。
    /// 哈希与服务端一致才推进进度；已完成的块幂等回执，不重复计数。
    /// </summary>
    public async Task<TransferResult<ChunkAckDto>> AcknowledgeChunkAsync(
        StoredDevice device,
        string taskId,
        int index,
        string? clientSha256,
        CancellationToken cancellationToken = default)
    {
        var gate = _downloadGates.GetOrAdd(taskId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
            }

            var ownership = CheckDownloadAccess(record, device);
            if (!ownership.Success)
            {
                return TransferResult<ChunkAckDto>.Fail(ownership.Error!);
            }

            if (TransferStates.IsTerminal(record.Status))
            {
                return Fail(ErrorCodes.TransferStateInvalid, "任务已结束。", 409);
            }

            if (index < 0 || index >= record.ChunkCount)
            {
                return Fail(ErrorCodes.InvalidRequest, "分块序号越界。", 400,
                    details: $"chunkCount={record.ChunkCount}");
            }

            var clientSha = NormalizeSha(clientSha256);
            if (clientSha is null)
            {
                return Fail(ErrorCodes.InvalidRequest, "缺少分块 SHA-256。", 400);
            }

            var chunk = await _repository.GetChunkAsync(taskId, index, cancellationToken).ConfigureAwait(false);
            if (chunk is null)
            {
                return Fail(ErrorCodes.InvalidRequest, "分块不存在。", 400);
            }

            if (chunk.Status == ChunkStates.Completed)
            {
                if (string.Equals(chunk.Sha256, clientSha, StringComparison.Ordinal))
                {
                    return TransferResult<ChunkAckDto>.Ok(new ChunkAckDto
                    {
                        TaskId = taskId,
                        Index = index,
                        BytesThisChunk = chunk.Length,
                        TransferredBytes = record.TransferredBytes,
                        TotalBytes = record.TotalBytes,
                        NextIndex = record.CompletedChunks >= record.ChunkCount ? record.ChunkCount : index + 1,
                        AlreadyCompleted = true,
                    });
                }

                return Fail(ErrorCodes.ChunkChecksumMismatch, "该分块已完成，且与本地内容不一致。", 409);
            }

            var storedSha = chunk.Sha256;
            if (storedSha is null)
            {
                var path = record.LocalPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    await FailRecordAsync(record, ErrorCodes.FileInaccessible, "源文件已不可读。", cancellationToken)
                        .ConfigureAwait(false);
                    return Fail(ErrorCodes.FileInaccessible, "源文件已不可读。", 403);
                }

                storedSha = await ComputeRangeSha256Async(path, chunk.Offset, chunk.Length, cancellationToken)
                    .ConfigureAwait(false);
                await _repository.UpdateChunkSha256Async(taskId, index, storedSha, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.Equals(storedSha, clientSha, StringComparison.Ordinal))
            {
                // 本地写坏了：块保持 pending，客户端重新拉取覆盖
                Log.Warning("下载分块回执校验失败 task={TaskId} index={Index}", taskId, index);
                return Fail(ErrorCodes.ChunkChecksumMismatch, "分块校验失败，请重新拉取该块。", 409,
                    retryable: true);
            }

            var now = DateTimeOffset.UtcNow;
            var transferred = record.TransferredBytes + chunk.Length;
            await _repository
                .CompleteChunkAsync(taskId, index, storedSha, transferred, TransferStates.Transferring,
                    now, cancellationToken)
                .ConfigureAwait(false);

            RaiseProgress(ToDto(record, device.DisplayName) with
            {
                State = TransferStates.Transferring,
                TransferredBytes = transferred,
                UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            });

            return TransferResult<ChunkAckDto>.Ok(new ChunkAckDto
            {
                TaskId = taskId,
                Index = index,
                BytesThisChunk = chunk.Length,
                TransferredBytes = transferred,
                TotalBytes = record.TotalBytes,
                NextIndex = record.CompletedChunks + 1 >= record.ChunkCount ? record.ChunkCount : index + 1,
                AlreadyCompleted = false,
            });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>下载方完成回执：所有分块都已确认，且本地整文件 SHA-256 与服务端一致才算完成。</summary>
    public async Task<TransferResult<TransferCompleteResponseDto>> AcknowledgeDownloadAsync(
        StoredDevice device,
        string taskId,
        string? clientSha256,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        var ownership = CheckDownloadAccess(record, device);
        if (!ownership.Success)
        {
            return TransferResult<TransferCompleteResponseDto>.Fail(ownership.Error!);
        }

        if (TransferStates.IsTerminal(record.Status))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已结束。", 409);
        }

        if (record.CompletedChunks < record.ChunkCount)
        {
            return Fail(ErrorCodes.TransferStateInvalid, "还有分块未确认接收。", 409,
                retryable: true, details: $"completed={record.CompletedChunks}/{record.ChunkCount}");
        }

        var clientSha = NormalizeSha(clientSha256);
        if (clientSha is null)
        {
            return Fail(ErrorCodes.InvalidRequest, "缺少本地 SHA-256。", 400);
        }

        var now = DateTimeOffset.UtcNow;

        if (!string.Equals(clientSha, record.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await _repository.UpdateResultAsync(
                taskId, TransferStates.Failed, record.Sha256, ErrorCodes.ChecksumMismatch,
                "接收端校验失败，文件内容不一致。", null, now, null, cancellationToken).ConfigureAwait(false);

            RaiseProgress(ToDto(record, device.DisplayName) with
            {
                State = TransferStates.Failed,
                ErrorCode = ErrorCodes.ChecksumMismatch,
                ErrorMessage = "接收端校验失败，文件内容不一致。",
                UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            });

            return Fail(ErrorCodes.ChecksumMismatch, "接收端校验失败，文件内容不一致。", 409);
        }

        await _repository.UpdateResultAsync(
            taskId, TransferStates.Completed, record.Sha256, null, null, null, now, now, cancellationToken)
            .ConfigureAwait(false);

        RaiseProgress(ToDto(record, device.DisplayName) with
        {
            State = TransferStates.Completed,
            TransferredBytes = record.TotalBytes,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        });

        return TransferResult<TransferCompleteResponseDto>.Ok(new TransferCompleteResponseDto
        {
            TaskId = taskId,
            State = TransferStates.Completed,
            FileName = record.FileName,
            RelativePath = record.RelativePath,
            SizeBytes = record.TotalBytes,
            Sha256 = record.Sha256 ?? clientSha,
            DurationMs = (long)(now - record.CreatedAtUtc).TotalMilliseconds,
        });
    }

    // ---------------------------------------------------------------- 恢复

    /// <summary>
    /// 删除若干条传输记录（两端「清理列表」共用）。
    ///
    /// 规则：
    /// <list type="bullet">
    /// <item>还没结束的任务（waiting/preparing/transferring/verifying）拒绝删除——先取消，否则临时文件变孤儿；</item>
    /// <item>deleteFiles 只删**手机上传到电脑**的文件，且必须落在共享目录内；
    ///       电脑发出去的原文件属于用户数据，哪怕记录里带着它的路径也不动；</item>
    /// <item>不属于本设备的任务直接跳过（也不报错，避免泄露别人的任务 id）。</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// 删除传输记录（**校验归属**）。HTTP 端点走这个：手机只能删自己的记录。
    /// </summary>
    public Task<TransferResult<TransferDeleteRecordsResultDto>> DeleteRecordsAsync(
        StoredDevice device,
        IReadOnlyList<string> taskIds,
        bool deleteFiles,
        CancellationToken cancellationToken = default)
        => DeleteRecordsCoreAsync(device, taskIds, deleteFiles, cancellationToken);

    /// <summary>
    /// 删除传输记录（**不校验归属**）。给宿主 UI 用。
    ///
    /// 界面上能看到所有设备的记录——包括那些已经被移除的设备留下的。
    /// 设备一删，`GetPrimaryDeviceAsync()` 就返回 null，于是"删记录"卡在第一步，
    /// 用户只看到「请先在首页配对手机」，明明记录就在眼前却删不掉（1.5.8 真机）。
    /// 删除记录本身不需要设备在线，也不需要设备还存在。
    ///
    /// 不要把这个重载接到 HTTP 上：归属校验是鉴权边界，手机之间不能互删。
    /// </summary>
    public Task<TransferResult<TransferDeleteRecordsResultDto>> DeleteRecordsAsync(
        IReadOnlyList<string> taskIds,
        bool deleteFiles,
        CancellationToken cancellationToken = default)
        => DeleteRecordsCoreAsync(null, taskIds, deleteFiles, cancellationToken);

    private async Task<TransferResult<TransferDeleteRecordsResultDto>> DeleteRecordsCoreAsync(
        StoredDevice? device,
        IReadOnlyList<string> taskIds,
        bool deleteFiles,
        CancellationToken cancellationToken)
    {
        if (taskIds is null || taskIds.Count == 0)
        {
            return Fail(ErrorCodes.InvalidRequest, "没有指定要删除的记录。", 400);
        }

        var clean = taskIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(ProtocolConstants.MaxDeleteTransferRecords)
            .ToArray();

        var result = new TransferDeleteRecordsResultDto();
        var removable = new List<string>(clean.Length);

        foreach (var taskId in clean)
        {
            var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);

            // 记录不存在：静默跳过（可能已经删过一轮）。
            // 归属校验只在带了设备时做——宿主 UI 传 null，删的是用户自己看得见的行。
            if (record is null)
            {
                continue;
            }

            if (device is not null &&
                !string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (TransferStates.IsActive(record.Status))
            {
                result.Rejected.Add($"{record.FileName}：还在传输，请先取消");
                continue;
            }

            // 半截临时文件跟着记录一起清，避免泄漏磁盘空间
            TryDeleteFile(record.TempPath, _options.TempDirectory, requireInside: true);

            if (deleteFiles &&
                string.Equals(record.Direction, TransferDirections.Upload, StringComparison.Ordinal))
            {
                // 换过接收目录之后，旧记录落在旧根里：逐个已知根试一遍，
                // 但绝不越出用户选过的目录（TryDeleteFile 会做包含校验）。
                foreach (var root in _sharedRoots.Keys)
                {
                    if (TryDeleteFile(record.LocalPath, root, requireInside: true))
                    {
                        result.FilesDeleted++;
                        break;
                    }
                }
            }

            removable.Add(taskId);
        }

        if (removable.Count > 0)
        {
            result.Deleted = await _repository
                .DeleteTasksAsync(removable, cancellationToken)
                .ConfigureAwait(false);
        }

        if (result.Deleted > 0 || result.Rejected.Count > 0)
        {
            Log.Information(
                "删除传输记录 device={DeviceId} records={Deleted} files={Files} rejected={Rejected}",
                device?.DeviceId ?? "(宿主界面)", result.Deleted, result.FilesDeleted, result.Rejected.Count);
        }

        return TransferResult<TransferDeleteRecordsResultDto>.Ok(result);
    }

    /// <summary>
    /// 删一个磁盘文件，并要求它落在允许的目录内。
    ///
    /// 目录校验不可省：记录里的路径来自数据库，一旦被改写就可能删到用户别处的文件。
    /// 返回 true 表示确实删掉了一个存在的文件。
    /// </summary>
    private bool TryDeleteFile(string? path, string allowedRoot, bool requireInside)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (requireInside)
            {
                var root = Path.GetFullPath(allowedRoot);
                if (!root.EndsWith(Path.DirectorySeparatorChar))
                {
                    root += Path.DirectorySeparatorChar;
                }

                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("拒绝删除目录外的文件 path={Path}", full);
                    return false;
                }
            }

            if (!File.Exists(full))
            {
                return false;
            }

            File.Delete(full);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning(ex, "删除文件失败 path={Path}", path);
            return false;
        }
    }

    /// <summary>
    /// 设备被删除时清理它的传输记录：内存会话 + 数据库行（任务与分块）。
    ///
    /// 临时文件一并删除（半截文件没有价值）；<b>已完成的接收文件保留</b>——
    /// 那是用户的数据，不属于"设备记录"。返回删除的任务数。
    /// </summary>
    public async Task<int> PurgeDeviceRecordsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        foreach (var pair in _uploads.ToArray())
        {
            if (!string.Equals(pair.Value.DeviceId, deviceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (_uploads.TryRemove(pair.Key, out var removed))
            {
                TryDeleteFile(removed.TempPath);
            }
        }

        var deleted = await _repository.DeleteForDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        Log.Information("已清理设备传输记录 device={DeviceId} 任务={Count}", deviceId, deleted);

        return deleted;
    }

    /// <summary>
    /// 启动时清掉"所属设备已不存在"的传输记录。
    ///
    /// 老版本删设备不删记录，这些行会让传输中心一直显示陌生设备 ID 的任务，
    /// 而用户已经无法通过界面清理。只删数据库行，磁盘文件不动。
    /// </summary>
    public async Task<int> PurgeOrphanDeviceRecordsAsync(CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteOrphanDeviceTasksAsync(cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            Log.Information("已清理无主传输记录 任务={Count}", deleted);
        }

        return deleted;
    }

    /// <summary>
    /// 恢复一个非终态任务（断网、APP 被杀、电脑重启之后）。
    /// 幂等：重复调用返回同一份服务端状态，绝不产生新任务。
    /// 上传会话不在内存时从数据库 + 临时文件重建；临时文件损坏则重置分块位图。
    /// </summary>
    public async Task<TransferResult<TransferTaskDto>> ResumeAsync(
        StoredDevice device,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        if (record.Status == TransferStates.Completed)
        {
            // 幂等读：已完成就是已完成，重复恢复不报错
            return TransferResult<TransferTaskDto>.Ok(await WithChunksAsync(
                ToDto(record, device.DisplayName), cancellationToken).ConfigureAwait(false));
        }

        if (TransferStates.IsTerminal(record.Status))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已结束，无法恢复。", 409);
        }

        TransferTaskDto dto;
        if (string.Equals(record.Direction, TransferDirections.Upload, StringComparison.Ordinal))
        {
            var rehydrated = await RehydrateUploadSessionAsync(device, taskId, cancellationToken)
                .ConfigureAwait(false);
            if (!rehydrated.Success)
            {
                return TransferResult<TransferTaskDto>.Fail(rehydrated.Error!);
            }

            dto = ToDto(rehydrated.Value!);
        }
        else
        {
            var source = record.LocalPath;
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
            {
                await FailRecordAsync(record, ErrorCodes.FileInaccessible, "源文件已不存在。", cancellationToken)
                    .ConfigureAwait(false);
                return Fail(ErrorCodes.FileInaccessible, "源文件已不存在。", 404);
            }

            if (new FileInfo(source).Length != record.TotalBytes)
            {
                await FailRecordAsync(record, ErrorCodes.TransferStateInvalid, "源文件已变化。", cancellationToken)
                    .ConfigureAwait(false);
                return Fail(ErrorCodes.TransferStateInvalid, "源文件已变化。", 409);
            }

            dto = ToDto(record, device.DisplayName);
        }

        // 从 interrupted / waiting 回到活跃态
        var resumedState = dto.TransferredBytes > 0 || dto.CompletedChunks.Length > 0
            ? TransferStates.Transferring
            : dto.State == TransferStates.Waiting ? TransferStates.Waiting : TransferStates.Preparing;

        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(dto.State, resumedState, StringComparison.Ordinal))
        {
            await _repository.TouchAsync(taskId, resumedState, now, cancellationToken).ConfigureAwait(false);
            dto = dto with { State = resumedState };
        }
        else
        {
            await _repository.TouchAsync(taskId, resumedState, now, cancellationToken).ConfigureAwait(false);
        }

        // 内存里的上传会话也要跟着离开 paused。
        // 只改数据库的话，继续之后推块仍会被暂停判断挡下（测试抓到的真实缺陷）。
        if (_uploads.TryGetValue(taskId, out var resumedSession))
        {
            resumedSession.State = resumedState;
        }

        dto = dto with { UpdatedAtUnixMs = now.ToUnixTimeMilliseconds() };
        dto = await WithChunksAsync(dto, cancellationToken).ConfigureAwait(false);

        Log.Information("任务恢复 task={TaskId} state={State} completed={Completed}/{Total}",
            taskId, dto.State, dto.CompletedChunks.Length, dto.ChunkCount);

        RaiseProgress(dto);
        return TransferResult<TransferTaskDto>.Ok(dto);
    }

    /// <summary>任务的分块列表（续传时客户端据此补差集）。</summary>
    public async Task<TransferResult<IReadOnlyList<ChunkInfoDto>>> ListChunksAsync(
        StoredDevice device,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        var chunks = await _repository.ListChunksAsync(taskId, cancellationToken).ConfigureAwait(false);
        var dtos = chunks.Select(static c => new ChunkInfoDto
        {
            TransferId = c.TaskId,
            Index = c.ChunkIndex,
            Offset = c.Offset,
            Length = c.Length,
            Sha256 = c.Sha256,
            Status = c.Status,
        }).ToArray();

        return TransferResult<IReadOnlyList<ChunkInfoDto>>.Ok(dtos);
    }

    // ---------------------------------------------------------------- 通用

    /// <summary>
    /// 暂停一个进行中的任务。
    ///
    /// 服务端不主动中断任何 I/O：上传是客户端推块、下载是客户端拉块，
    /// 所以"暂停"在这里的语义是**拒绝后续分块**，同时保留临时文件与分块位图。
    /// 继续时走 <see cref="ResumeAsync"/>，从已有偏移接上，不重传已完成的分块。
    /// </summary>
    public async Task<TransferResult<TransferTaskDto>> PauseAsync(
        StoredDevice device,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        // 幂等：重复暂停按成功处理，客户端可以安全重试
        if (string.Equals(record.Status, TransferStates.Paused, StringComparison.Ordinal))
        {
            return TransferResult<TransferTaskDto>.Ok(
                await WithChunksAsync(ToDto(record, device.DisplayName), cancellationToken).ConfigureAwait(false));
        }

        if (!TransferStates.IsActive(record.Status))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "只有进行中的任务可以暂停。", 409);
        }

        var now = DateTimeOffset.UtcNow;
        await _repository.TouchAsync(taskId, TransferStates.Paused, now, cancellationToken).ConfigureAwait(false);

        // 内存里的上传会话也要翻状态，否则后续推块仍会被接受
        if (_uploads.TryGetValue(taskId, out var session))
        {
            session.State = TransferStates.Paused;
        }

        var dto = await WithChunksAsync(
            ToDto(record, device.DisplayName) with
            {
                State = TransferStates.Paused,
                UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            },
            cancellationToken).ConfigureAwait(false);

        Log.Information("任务已暂停 task={TaskId}", taskId);

        RaiseProgress(dto);
        return TransferResult<TransferTaskDto>.Ok(dto);
    }

    public async Task<TransferResult<TransferTaskDto>> CancelAsync(
        StoredDevice device,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (_uploads.TryGetValue(taskId, out var session))
        {
            if (!string.Equals(session.DeviceId, device.DeviceId, StringComparison.Ordinal))
            {
                return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
            }

            await FailSessionAsync(session, null, null, cancellationToken, TransferStates.Cancelled)
                .ConfigureAwait(false);

            Log.Information("上传任务已取消 task={TaskId}", taskId);

            return TransferResult<TransferTaskDto>.Ok(ToDto(session) with
            {
                State = TransferStates.Cancelled,
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        if (TransferStates.IsTerminal(record.Status))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已结束。", 409);
        }

        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateResultAsync(
            taskId, TransferStates.Cancelled, record.Sha256, null, null, null, now, null, cancellationToken)
            .ConfigureAwait(false);
        await _repository.DeleteChunksAsync(taskId, cancellationToken).ConfigureAwait(false);
        TryDeleteFile(record.TempPath);

        RaiseProgress(ToDto(record, device.DisplayName) with
        {
            State = TransferStates.Cancelled,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        });

        return TransferResult<TransferTaskDto>.Ok(ToDto(record, device.DisplayName) with
        {
            State = TransferStates.Cancelled,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>
    /// 把下载任务退回"等待中"，分块位图与进度归零，让手机从头再拉一遍。
    /// 源文件始终在电脑本地、不受客户端控制，所以重来是安全的；
    /// 客户端不能借这个接口指定任何路径或改写任务归属。
    /// </summary>
    public async Task<TransferResult<TransferTaskDto>> RestartDownloadAsync(
        StoredDevice device,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        if (!string.Equals(record.Direction, TransferDirections.Download, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "只有下载任务可以重试。", 409);
        }

        if (record.Status == TransferStates.Completed)
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已完成。", 409);
        }

        // 下载任务的源文件是电脑本地任意路径（创建时由桌面端选定），不在共享目录里
        var source = record.LocalPath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            await FailRecordAsync(record, ErrorCodes.FileInaccessible, "源文件已不存在。", cancellationToken)
                .ConfigureAwait(false);
            return Fail(ErrorCodes.FileInaccessible, "源文件已不存在。", 404);
        }

        var now = DateTimeOffset.UtcNow;
        await _repository.ResetForRetryAsync(taskId, TransferStates.Waiting, now, cancellationToken)
            .ConfigureAwait(false);
        await _repository.ResetChunksAsync(taskId, now, cancellationToken).ConfigureAwait(false);

        Log.Information("下载任务重新排队 task={TaskId}", taskId);

        var dto = ToDto(record, device.DisplayName) with
        {
            State = TransferStates.Waiting,
            TransferredBytes = 0,
            ErrorCode = null,
            ErrorMessage = null,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        RaiseProgress(dto);
        return TransferResult<TransferTaskDto>.Ok(dto);
    }

    public async Task<TransferResult<TransferTaskDto>> GetAsync(
        StoredDevice device,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (_uploads.TryGetValue(taskId, out var session))
        {
            if (!string.Equals(session.DeviceId, device.DeviceId, StringComparison.Ordinal))
            {
                return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
            }

            return TransferResult<TransferTaskDto>.Ok(
                await WithChunksAsync(ToDto(session), cancellationToken).ConfigureAwait(false));
        }

        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        return TransferResult<TransferTaskDto>.Ok(
            await WithChunksAsync(ToDto(record, device.DisplayName), cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<TransferTaskDto>> ListAsync(
        StoredDevice device,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var records = await _repository.ListForDeviceAsync(device.DeviceId, limit, cancellationToken)
            .ConfigureAwait(false);

        return records.Select(r =>
        {
            // 上传中的任务以内存会话为准（数据库里的进度可能落后一个分块）
            if (_uploads.TryGetValue(r.TaskId, out var session))
            {
                return ToDto(session);
            }

            return ToDto(r, device.DisplayName);
        }).ToArray();
    }

    /// <summary>所有设备的最近任务，供桌面端界面使用。设备名由调用方提供映射。</summary>
    public async Task<IReadOnlyList<TransferTaskDto>> ListRecentAsync(
        int limit,
        IReadOnlyDictionary<string, string>? deviceNames = null,
        CancellationToken cancellationToken = default)
    {
        var records = await _repository.ListRecentAsync(limit, cancellationToken).ConfigureAwait(false);

        return records.Select(record =>
        {
            if (_uploads.TryGetValue(record.TaskId, out var session))
            {
                return ToDto(session);
            }

            // 设备被删除后就查不到名字了。回退成一句人话，不要把 32 位十六进制设备 ID
            // 直接摆到界面上——用户既读不懂，也不知道该拿它做什么。
            var name = deviceNames is not null && deviceNames.TryGetValue(record.DeviceId, out var resolved)
                ? resolved
                : "已移除的设备";

            return ToDto(record, name);
        }).ToArray();
    }

    /// <summary>
    /// 进程启动时调用：上次退出留下的非终态任务转入 interrupted（数据保留、可恢复），
    /// 临时文件损坏的重置分块位图，孤儿临时文件与孤儿分块行清理掉。
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _repository.DeleteOrphanChunksAsync(cancellationToken).ConfigureAwait(false);

            var stale = await _repository
                .ListStaleAsync(DateTimeOffset.UtcNow.AddSeconds(1), cancellationToken)
                .ConfigureAwait(false);

            var interrupted = 0;
            var failed = 0;

            foreach (var record in stale)
            {
                if (string.Equals(record.Direction, TransferDirections.Upload, StringComparison.Ordinal))
                {
                    // 临时文件丢了或长度不对：数据不可信，重置位图让客户端重传
                    var tempValid = !string.IsNullOrEmpty(record.TempPath)
                        && File.Exists(record.TempPath)
                        && new FileInfo(record.TempPath).Length == record.TotalBytes;

                    if (!tempValid)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var tempPath = string.IsNullOrEmpty(record.TempPath)
                            ? Path.Combine(_options.TempDirectory, record.TaskId + ".wcpart")
                            : record.TempPath;
                        RecreateTempFile(tempPath, record.TotalBytes);
                        await _repository.ResetChunksAsync(record.TaskId, now, cancellationToken)
                            .ConfigureAwait(false);
                        Log.Warning("上传任务临时文件不可信，已重置分块 task={TaskId}", record.TaskId);
                    }
                }
                else
                {
                    // 下载的源文件没了：恢复无意义，直接判失败
                    if (string.IsNullOrEmpty(record.LocalPath) || !File.Exists(record.LocalPath))
                    {
                        await _repository.UpdateResultAsync(
                            record.TaskId, TransferStates.Failed, record.Sha256,
                            ErrorCodes.FileInaccessible, "源文件已不存在。",
                            null, DateTimeOffset.UtcNow, null, cancellationToken).ConfigureAwait(false);
                        await _repository.DeleteChunksAsync(record.TaskId, cancellationToken).ConfigureAwait(false);
                        failed++;
                        continue;
                    }
                }

                await _repository.TouchAsync(record.TaskId, TransferStates.Interrupted,
                    DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                interrupted++;
            }

            // 没有任务行对应的临时文件（比如建任务途中进程被杀）：直接删
            foreach (var file in Directory.EnumerateFiles(_options.TempDirectory, "*.wcpart"))
            {
                var taskId = Path.GetFileNameWithoutExtension(file);
                var owner = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (owner is null || TransferStates.IsTerminal(owner.Status))
                {
                    TryDeleteFile(file);
                }
            }

            if (interrupted > 0 || failed > 0)
            {
                Log.Information("启动恢复：{Interrupted} 个任务标记为可续传，{Failed} 个判失败",
                    interrupted, failed);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "启动恢复传输任务失败");
        }
    }

    /// <summary>回收扫描：停滞的活跃任务转 interrupted（可恢复），超保留期的 interrupted 判失败清理。</summary>
    private async Task SweepAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;

            var idleCutoff = now - _options.TaskIdleTimeout;
            var stalledCutoff = now - _options.InactivityTimeout;
            var retentionCutoff = now - _options.InterruptedRetention;

            foreach (var session in _uploads.Values)
            {
                if (TransferStates.IsTerminal(session.State))
                {
                    continue;
                }

                var started = string.Equals(
                    session.State, TransferStates.Transferring, StringComparison.Ordinal);
                if (session.LastActivityUtc > (started ? stalledCutoff : idleCutoff))
                {
                    continue;
                }

                Log.Warning(
                    "传输任务停滞，转为可恢复中断 task={TaskId} state={State}",
                    session.TaskId,
                    session.State);

                await InterruptSessionAsync(session).ConfigureAwait(false);
            }

            // 下载由客户端拉块，服务端看不到连接，只能以"进度是否还在推进"判断对端是否已断开。
            var stalled = await _repository.ListStaleStartedAsync(stalledCutoff, CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var record in stalled.Where(r => !_uploads.ContainsKey(r.TaskId)))
            {
                Log.Warning("任务长时间没有进度，转为可恢复中断 task={TaskId}", record.TaskId);
                await _repository.TouchAsync(record.TaskId, TransferStates.Interrupted,
                    now, CancellationToken.None).ConfigureAwait(false);

                RaiseProgress(ToDto(record, string.Empty) with
                {
                    State = TransferStates.Interrupted,
                    UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
                });
            }

            var stale = await _repository.ListStaleAsync(idleCutoff, CancellationToken.None).ConfigureAwait(false);
            foreach (var record in stale.Where(r => !_uploads.ContainsKey(r.TaskId)))
            {
                await _repository.TouchAsync(record.TaskId, TransferStates.Interrupted,
                    now, CancellationToken.None).ConfigureAwait(false);
            }

            // 中断任务占着临时文件与磁盘，超过保留期不恢复就判失败清理
            var expired = await _repository.ListInterruptedAsync(retentionCutoff, CancellationToken.None)
                .ConfigureAwait(false);
            foreach (var record in expired)
            {
                Log.Warning("中断任务超过保留期，判失败清理 task={TaskId}", record.TaskId);
                await _repository.UpdateResultAsync(
                    record.TaskId, TransferStates.Failed, record.Sha256,
                    ErrorCodes.TransferTimeout, "任务中断后长时间未恢复。",
                    null, now, null, CancellationToken.None).ConfigureAwait(false);
                await _repository.DeleteChunksAsync(record.TaskId, CancellationToken.None).ConfigureAwait(false);
                TryDeleteFile(record.TempPath);

                RaiseProgress(ToDto(record, string.Empty) with
                {
                    State = TransferStates.Failed,
                    ErrorCode = ErrorCodes.TransferTimeout,
                    ErrorMessage = "任务中断后长时间未恢复。",
                    UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "回收传输任务时出现异常");
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _janitor.Dispose();
        _hashGate.Dispose();

        foreach (var session in _uploads.Values)
        {
            session.Gate.Dispose();
        }

        foreach (var gate in _downloadGates.Values)
        {
            gate.Dispose();
        }

        _uploads.Clear();
        _downloadGates.Clear();
    }

    // ---------------------------------------------------------------- 内部：会话与恢复

    private sealed class UploadSession
    {
        public required string TaskId { get; init; }
        public required string DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public required string TempPath { get; init; }
        public required long TotalBytes { get; init; }
        public required int ChunkSize { get; init; }
        public required int ChunkCount { get; init; }
        public required string Conflict { get; init; }
        public required bool[] Completed { get; init; }
        public required string?[] Hashes { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }

        public required string FinalPath { get; set; }
        public required string FileName { get; set; }
        public required string RelativePath { get; set; }

        /// <summary>本任务所属共享目录的根绝对路径。阶段 5 起不再只有一个共享目录。</summary>
        public required string RootPath { get; set; }

        public long Written { get; set; }
        public string State { get; set; } = TransferStates.Preparing;
        public DateTimeOffset LastActivityUtc { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    /// <summary>
    /// 上传会话不在内存（进程重启 / 中断后被移出）时，从数据库 + 临时文件重建。
    /// 临时文件缺失或长度不符说明数据不可信：重建预分配文件并把分块位图整体重置。
    /// </summary>
    private async Task<TransferResult<UploadSession>> RehydrateUploadSessionAsync(
        StoredDevice device,
        string taskId,
        CancellationToken cancellationToken)
    {
        var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Fail(ErrorCodes.TransferNotFound, "任务不存在。", 404);
        }

        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        if (!string.Equals(record.Direction, TransferDirections.Upload, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "该任务不是上传任务。", 409);
        }

        if (TransferStates.IsTerminal(record.Status))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "任务已结束或已中断。", 409);
        }

        // 再次查内存：并发恢复只允许一个会话
        if (_uploads.TryGetValue(taskId, out var existing))
        {
            return TransferResult<UploadSession>.Ok(existing);
        }

        var tempPath = record.TempPath;
        var tempValid = !string.IsNullOrEmpty(tempPath)
            && File.Exists(tempPath)
            && new FileInfo(tempPath).Length == record.TotalBytes;

        if (!tempValid)
        {
            tempPath = string.IsNullOrEmpty(tempPath)
                ? Path.Combine(_options.TempDirectory, taskId + ".wcpart")
                : tempPath;
            RecreateTempFile(tempPath, record.TotalBytes);
            var resetAt = DateTimeOffset.UtcNow;
            await _repository.ResetChunksAsync(taskId, resetAt, cancellationToken).ConfigureAwait(false);
            Log.Warning("上传任务临时文件不可信，已重置分块 task={TaskId}", taskId);
        }

        var chunks = await _repository.ListChunksAsync(taskId, cancellationToken).ConfigureAwait(false);
        var completed = new bool[record.ChunkCount];
        var hashes = new string?[record.ChunkCount];
        long written = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= record.ChunkCount)
            {
                continue;
            }

            if (chunk.Status == ChunkStates.Completed)
            {
                completed[chunk.ChunkIndex] = true;
                hashes[chunk.ChunkIndex] = chunk.Sha256;
                written += chunk.Length;
            }
        }

        // 重新占位最终路径（中断时释放了占位，期间可能被别人占了）
        var conflict = TransferConflictPolicies.IsValid(record.Conflict)
            ? record.Conflict!
            : TransferConflictPolicies.Rename;
        // 目录根按任务记录里的 directory_id 还原：共享目录可能被移除或改路径，
        // 还原失败就明确报错，不要悄悄落回默认目录去。
        var rootResolution = await ResolveRootForRecordAsync(device, record, cancellationToken).ConfigureAwait(false);
        if (!rootResolution.Success)
        {
            return TransferResult<UploadSession>.Fail(rootResolution.Error!);
        }

        var rootPath = rootResolution.Value!;
        var directory = Path.GetDirectoryName(Path.Combine(rootPath, record.RelativePath))
            ?? rootPath;
        var reservation = ReserveByPolicy(directory, record.FileName, conflict);
        if (!reservation.Success)
        {
            return TransferResult<UploadSession>.Fail(reservation.Error!);
        }

        var session = new UploadSession
        {
            TaskId = record.TaskId,
            DeviceId = record.DeviceId,
            DeviceName = device.DisplayName,
            TempPath = tempPath!,
            FinalPath = reservation.Value!.FullPath,
            RootPath = rootPath,
            FileName = reservation.Value.FileName,
            RelativePath = ToRelativePath(rootPath, reservation.Value.FullPath),
            TotalBytes = record.TotalBytes,
            ChunkSize = record.ChunkSize,
            ChunkCount = record.ChunkCount,
            Conflict = conflict,
            Completed = completed,
            Hashes = hashes,
            Written = written,
            State = written > 0 ? TransferStates.Transferring : TransferStates.Preparing,
            CreatedAtUtc = record.CreatedAtUtc,
            LastActivityUtc = DateTimeOffset.UtcNow,
        };

        _uploads[taskId] = session;
        return TransferResult<UploadSession>.Ok(session);
    }

    /// <summary>预分配临时文件到任务声明的大小（已存在则先删，内容本来也不可信）。</summary>
    private void RecreateTempFile(string tempPath, long totalBytes)
    {
        TryDeleteFile(tempPath);
        Directory.CreateDirectory(_options.TempDirectory);
        using var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        temp.SetLength(totalBytes);
    }

    // ---------------------------------------------------------------- 内部：状态迁移

    /// <summary>活跃任务转中断：保留临时文件与分块位图，释放内存会话与名称占位。</summary>
    private async Task InterruptSessionAsync(UploadSession session)
    {
        var now = DateTimeOffset.UtcNow;
        session.State = TransferStates.Interrupted;

        await _repository.TouchAsync(session.TaskId, TransferStates.Interrupted, now, CancellationToken.None)
            .ConfigureAwait(false);

        _uploads.TryRemove(session.TaskId, out _);
        ReleaseReservation(session.FinalPath);

        RaiseProgress(ToDto(session) with
        {
            State = TransferStates.Interrupted,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>把任务标为失败/取消，删临时文件与分块位图，释放并发配额与名称占位。</summary>
    private async Task FailSessionAsync(
        UploadSession session,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken,
        string terminalState = TransferStates.Failed)
    {
        session.State = terminalState;
        var now = DateTimeOffset.UtcNow;

        await _repository.UpdateResultAsync(
            session.TaskId,
            terminalState,
            null,
            errorCode,
            errorMessage,
            null,
            now,
            null,
            cancellationToken).ConfigureAwait(false);
        await _repository.DeleteChunksAsync(session.TaskId, cancellationToken).ConfigureAwait(false);

        TryDeleteFile(session.TempPath);
        _uploads.TryRemove(session.TaskId, out _);
        ReleaseReservation(session.FinalPath);

        RaiseProgress(ToDto(session) with
        {
            State = terminalState,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        });
    }

    private async Task FailRecordAsync(
        TransferTaskRecord record,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await _repository.UpdateResultAsync(
            record.TaskId,
            TransferStates.Failed,
            record.Sha256,
            errorCode,
            errorMessage,
            null,
            DateTimeOffset.UtcNow,
            null,
            cancellationToken).ConfigureAwait(false);
        await _repository.DeleteChunksAsync(record.TaskId, cancellationToken).ConfigureAwait(false);
        TryDeleteFile(record.TempPath);
    }

    private static TransferResult<bool> CheckDownloadAccess(
        TransferTaskRecord record,
        StoredDevice device)
    {
        if (!string.Equals(record.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.Forbidden, "该任务不属于当前设备。", 403);
        }

        if (!string.Equals(record.Direction, TransferDirections.Download, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.TransferStateInvalid, "该任务不是下载任务。", 409);
        }

        return TransferResult<bool>.Ok(true);
    }

    // ---------------------------------------------------------------- 内部：命名与冲突

    private TransferResult<string> NormalizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return InvalidName("文件名为空。", "empty");
        }

        var name = raw.Trim();
        if (name.Length > 200)
        {
            return InvalidName("文件名过长。", "too_long");
        }

        // 只要文件名，不要路径：带分隔符就说明客户端在试图指定位置
        if (name.Contains('/') || name.Contains('\\'))
        {
            return InvalidName("文件名不能包含路径分隔符。", "path_separator");
        }

        // 用同一个 PathGuard 判定：拒绝穿越、ADS、保留名、结尾点/空格、非法字符
        var check = PathGuard.Resolve(_sharedDirectory, name);
        if (!check.IsAllowed)
        {
            return InvalidName("文件名不合法。", check.ReasonCode);
        }

        return TransferResult<string>.Ok(name);
    }

    private static TransferResult<string> InvalidName(string message, string reason)
        => Fail(ErrorCodes.InvalidFileName, message, 400, details: reason);

    /// <summary>相对子目录解析到共享根内；空表示根本身。</summary>
    private TransferResult<string> ResolveDirectory(string? relativeDirectory)
    {
        var check = PathGuard.Resolve(_sharedDirectory, relativeDirectory?.Trim() ?? string.Empty);
        if (!check.IsAllowed)
        {
            return Fail(ErrorCodes.PathForbidden, "目标目录超出允许范围。", 403,
                details: check.ReasonCode);
        }

        return TransferResult<string>.Ok(check.FullPath);
    }

    /// <summary>上传落点：目录 id + 相对子目录 → 绝对目录。</summary>
    private sealed record UploadTarget(string Directory, string Root, string? DirectoryId);

    /// <summary>
    /// 解析上传落点目录。目录 id 为空时用默认共享目录（阶段 3/4 行为不变）；
    /// 指定目录 id 时必须校验该设备的 Upload 权限，没有权限直接拒绝。
    /// </summary>
    private async Task<TransferResult<UploadTarget>> ResolveTargetDirectoryAsync(
        StoredDevice device,
        string? directoryId,
        string? relativeDirectory,
        CancellationToken cancellationToken)
    {
        var root = _sharedDirectory;
        var resolvedDirectoryId = (string?)null;

        if (!string.IsNullOrWhiteSpace(directoryId))
        {
            if (_directories is null)
            {
                return Fail(ErrorCodes.DirectoryNotAuthorized, "该目录未授权给当前设备。", 403);
            }

            var (resolution, error) = await _directories
                .ResolveAsync(directoryId, device, cancellationToken)
                .ConfigureAwait(false);

            if (error is not null)
            {
                return TransferResult<UploadTarget>.Fail(error);
            }

            if (!resolution!.Permissions.HasFlag(Storage.Entities.SharedDirectoryPermissions.Upload))
            {
                return Fail(ErrorCodes.OperationNotPermitted, "没有向该目录上传的权限。", 403);
            }

            root = resolution.RootPath;
            resolvedDirectoryId = directoryId;
        }

        var relative = relativeDirectory?.Trim() ?? string.Empty;

        // 默认落点（既没指定目录、也没指定子目录）按设备名分文件夹：
        // 两台手机同时往接收目录丢文件时，用户不用猜哪个是哪个的。
        if (string.IsNullOrEmpty(directoryId) && relative.Length == 0)
        {
            relative = DeviceFolderName(device, root);
        }

        var check = PathGuard.Resolve(root, relative);
        if (!check.IsAllowed)
        {
            return Fail(ErrorCodes.PathForbidden, "目标目录超出允许范围。", 403,
                details: check.ReasonCode);
        }

        return TransferResult<UploadTarget>.Ok(new UploadTarget(check.FullPath, root, resolvedDirectoryId));
    }

    /// <summary>
    /// 设备显示名 → 可用的目录名。名字来自手机自报（型号）或用户重命名，
    /// 因此可能带路径分隔符、结尾点、Windows 保留名，必须先过一遍 PathGuard。
    /// </summary>
    private string DeviceFolderName(StoredDevice device, string root)
    {
        var raw = (device.DisplayName ?? string.Empty).Trim();
        var builder = new System.Text.StringBuilder(raw.Length);

        foreach (var c in raw)
        {
            builder.Append(char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : c);
        }

        var name = builder.ToString().Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            name = "设备";
        }

        if (name.Length > 64)
        {
            name = name[..64].TrimEnd('.', ' ');
        }

        if (PathGuard.Resolve(root, name).IsAllowed)
        {
            return name;
        }

        // 保留名（CON/PRN/...）之类过不了守卫：退到设备 ID 前缀，至少能落盘且可辨认
        return "设备-" + device.DeviceId[..Math.Min(8, device.DeviceId.Length)];
    }

    /// <summary>
    /// 按任务记录还原共享目录根。目录被移除或路径变更时明确失败，
    /// 不允许悄悄落回默认目录——那会把文件放到用户意想不到的地方。
    /// </summary>
    private async Task<TransferResult<string>> ResolveRootForRecordAsync(
        StoredDevice device,
        TransferTaskRecord record,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.DirectoryId))
        {
            return TransferResult<string>.Ok(_sharedDirectory);
        }

        if (_directories is null)
        {
            return Fail(ErrorCodes.DirectoryNotAuthorized, "任务所属目录已不可用。", 403);
        }

        var (resolution, error) = await _directories
            .ResolveAsync(record.DirectoryId, device, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return TransferResult<string>.Fail(error);
        }

        return TransferResult<string>.Ok(resolution!.RootPath);
    }

    /// <summary>按冲突策略占位最终路径。</summary>
    private TransferResult<(string FullPath, string FileName)> ReserveByPolicy(
        string directory,
        string fileName,
        string conflict)
    {
        switch (conflict)
        {
            case TransferConflictPolicies.Overwrite:
            {
                var exact = ReserveExact(directory, fileName);
                return exact is null
                    ? Fail(ErrorCodes.Conflict, "目标文件正被另一个传输任务占用。", 409)
                    : TransferResult<(string, string)>.Ok(exact.Value);
            }
            case TransferConflictPolicies.Skip:
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return Fail(ErrorCodes.Conflict, "目标位置已存在同名文件。", 409);
                }

                var exact = ReserveExact(directory, fileName);
                return exact is null
                    ? Fail(ErrorCodes.Conflict, "目标文件正被另一个传输任务占用。", 409)
                    : TransferResult<(string, string)>.Ok(exact.Value);
            }
            default:
                return TransferResult<(string, string)>.Ok(ReserveTarget(directory, fileName));
        }
    }

    /// <summary>按冲突策略提交：overwrite 显式替换，其余不覆盖落盘。</summary>
    private void CommitByPolicy(UploadSession session)
    {
        if (session.Conflict == TransferConflictPolicies.Overwrite)
        {
            // 客户端在建任务时显式声明了覆盖，这里才允许替换已有文件
            File.Move(session.TempPath, session.FinalPath, overwrite: true);
            return;
        }

        if (session.Conflict == TransferConflictPolicies.Skip && File.Exists(session.FinalPath))
        {
            throw new InvalidOperationException("target exists");
        }

        File.Move(session.TempPath, session.FinalPath, overwrite: false);
    }

    /// <summary>在锁内占位一个不冲突的最终路径。不覆盖磁盘上已有文件。</summary>
    private (string FullPath, string FileName) ReserveTarget(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        lock (_nameGate)
        {
            for (var index = 0; index < 1000; index++)
            {
                var candidateName = index == 0 ? fileName : $"{stem} ({index}){extension}";
                var candidate = Path.Combine(directory, candidateName);

                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    continue;
                }

                if (!_reservedPaths.Add(candidate))
                {
                    continue;
                }

                return (candidate, candidateName);
            }

            var fallbackName = $"{stem} ({DateTimeOffset.UtcNow:yyyyMMddHHmmss}){extension}";
            var fallbackPath = Path.Combine(directory, fallbackName);
            _reservedPaths.Add(fallbackPath);
            return (fallbackPath, fallbackName);
        }
    }

    /// <summary>占位一个精确路径（overwrite/skip 用）。已被本进程其他任务占用则返回 null。</summary>
    private (string FullPath, string FileName)? ReserveExact(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        lock (_nameGate)
        {
            return _reservedPaths.Add(candidate) ? (candidate, fileName) : null;
        }
    }

    private void ReleaseReservation(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (_nameGate)
        {
            _reservedPaths.Remove(path);
        }
    }

    // ---------------------------------------------------------------- 内部：工具

    private int NormalizeChunkSize(int? requested)
    {
        var value = requested ?? _options.DefaultChunkSize;
        return Math.Clamp(value, ProtocolConstants.MinChunkSizeBytes, _options.DefaultChunkSize);
    }

    private static int ExpectedChunkLength(int chunkSize, long totalBytes, int index)
    {
        var offset = (long)index * chunkSize;
        var remaining = totalBytes - offset;
        return (int)Math.Min(chunkSize, Math.Max(0, remaining));
    }

    /// <summary>建任务时生成全量分块计划（全 pending）。</summary>
    private static IReadOnlyList<TransferChunkRecord> BuildChunkPlan(
        string taskId,
        int chunkSize,
        long totalBytes,
        DateTimeOffset now)
    {
        var count = totalBytes == 0 ? 0 : (int)((totalBytes + chunkSize - 1) / chunkSize);
        var plan = new List<TransferChunkRecord>(count);

        for (var index = 0; index < count; index++)
        {
            plan.Add(new TransferChunkRecord
            {
                TaskId = taskId,
                ChunkIndex = index,
                Offset = (long)index * chunkSize,
                Length = ExpectedChunkLength(chunkSize, totalBytes, index),
                Sha256 = null,
                Status = ChunkStates.Pending,
                UpdatedAtUtc = now,
            });
        }

        return plan;
    }

    private static ChunkAckDto AckOf(UploadSession session, int index, int bytes, bool alreadyCompleted)
    {
        var next = session.ChunkCount;
        for (var i = 0; i < session.ChunkCount; i++)
        {
            if (!session.Completed[i])
            {
                next = i;
                break;
            }
        }

        return new ChunkAckDto
        {
            TaskId = session.TaskId,
            Index = index,
            BytesThisChunk = bytes,
            TransferredBytes = session.Written,
            TotalBytes = session.TotalBytes,
            NextIndex = next,
            AlreadyCompleted = alreadyCompleted,
        };
    }

    private string ToRelativePath(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(fullPath));
        return relative.Replace('\\', '/');
    }

    private static string? NormalizeSha(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Length != 64)
        {
            return null;
        }

        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigitLower(ch) && !char.IsAsciiDigit(ch))
            {
                return null;
            }
        }

        return text;
    }

    private async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await _hashGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            _hashGate.Release();
        }
    }

    /// <summary>计算文件某一段的 SHA-256（下载分块的惰性哈希）。</summary>
    private async Task<string> ComputeRangeSha256Async(
        string path,
        long offset,
        int length,
        CancellationToken cancellationToken)
    {
        await _hashGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, useAsync: true);
            stream.Seek(offset, SeekOrigin.Begin);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var want = Math.Min(buffer.Length, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new IOException("源文件长度不足。");
                    }

                    hasher.AppendData(buffer.AsSpan(0, read));
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            _hashGate.Release();
        }
    }

    private static async Task<long> CopyExactlyAsync(
        Stream source,
        Stream target,
        long length,
        IncrementalHash? hasher,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long copied = 0;

        try
        {
            while (copied < length)
            {
                var want = (int)Math.Min(buffer.Length, length - copied);
                var read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher?.AppendData(buffer.AsSpan(0, read));
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copied;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "删除临时文件失败 {Path}", LogSafeName(path));
        }
    }

    private void RaiseProgress(TransferTaskDto dto)
    {
        try
        {
            Progress?.Invoke(this, dto);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "传输进度回调异常 task={TaskId}", dto.TaskId);
        }
    }

    /// <summary>日志里只留文件名，不写完整路径。</summary>
    private static string LogSafeName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return "?";
        }
    }

    private static TransferFailure Fail(
        string code,
        string message,
        int statusCode,
        bool retryable = false,
        string? details = null)
        => new(code, message, statusCode, retryable, details);

    private TransferTaskDto ToDto(UploadSession session)
    {
        var now = DateTimeOffset.UtcNow;
        return new TransferTaskDto
        {
            TaskId = session.TaskId,
            Direction = TransferDirections.Upload,
            DeviceName = session.DeviceName,
            FileName = session.FileName,
            RelativePath = session.RelativePath,
            TotalBytes = session.TotalBytes,
            TransferredBytes = session.Written,
            ChunkSize = session.ChunkSize,
            ChunkCount = session.ChunkCount,
            State = session.State,
            Sha256 = null,
            CreatedAtUnixMs = session.CreatedAtUtc.ToUnixTimeMilliseconds(),
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        };
    }

    private static TransferTaskDto ToDto(TransferTaskRecord record, string deviceName) => new()
    {
        TaskId = record.TaskId,
        Direction = record.Direction,
        // 旧记录没有 purpose：那时下载只有"发到手机"一种，按 inbox 补上
        Purpose = record.Purpose ?? TransferPurposes.Inbox,
        DeviceName = deviceName,
        FileName = record.FileName,
        RelativePath = record.RelativePath,
        BatchId = record.BatchId,
        BatchName = record.BatchName,
        LocalPath = record.LocalPath,
        TotalBytes = record.TotalBytes,
        TransferredBytes = record.TransferredBytes,
        ChunkSize = record.ChunkSize,
        ChunkCount = record.ChunkCount,
        State = record.Status,
        Sha256 = record.Sha256,
        ErrorCode = record.ErrorCode,
        ErrorMessage = record.ErrorMessage,
        CreatedAtUnixMs = record.CreatedAtUtc.ToUnixTimeMilliseconds(),
        UpdatedAtUnixMs = record.UpdatedAtUtc.ToUnixTimeMilliseconds(),
    };

    /// <summary>给 DTO 附上已完成分块序号（Get/Resume 用；列表接口不附，避免 N+1）。</summary>
    private async Task<TransferTaskDto> WithChunksAsync(
        TransferTaskDto dto,
        CancellationToken cancellationToken)
    {
        var chunks = await _repository.ListChunksAsync(dto.TaskId, cancellationToken).ConfigureAwait(false);
        return dto with
        {
            CompletedChunks = chunks
                .Where(static c => c.Status == ChunkStates.Completed)
                .Select(static c => c.ChunkIndex)
                .ToArray(),
        };
    }
}
