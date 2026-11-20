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
    /// <summary>删记录时要连带作废这批还没执行的作业（队列不清，删完还会重建任务）。</summary>
    private readonly FileService? _files;
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
        SharedDirectoryResolver? directories = null,
        FileService? files = null)
    {
        _repository = repository;
        _options = options;
        _directories = directories;
        _files = files;
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
            // **批次名**：上传方向的一批文件可能各自在不同目录，
            // 没有它就没法把"取消一批"补全到同批的每一个任务
            // （按目录补全只对同一目录有效，跨目录就漏——真机：漏掉的那条
            // 还是 transferring，电脑端「传输中」角标一直亮着）。
            BatchName = NormalizeBatchName(request.BatchName),
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
            return Fail(ErrorCodes.TransferPaused, "任务已暂停，请先继续。", 409, retryable: true);
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
            // **不能无条件写 transferring**：这一块在暂停之前就已经在写了，
            // 写完时若把内存里的 Paused 覆盖掉，下一块的状态检查就会放行，
            // 于是一路传到尾（真机：80 MB 文件暂停后 16→48→75→completed）。
            // 库那边同样有 CASE 保护，两处必须一起改，否则内存先破防。
            if (!string.Equals(session.State, TransferStates.Paused, StringComparison.Ordinal))
            {
                session.State = TransferStates.Transferring;
            }

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

        // 暂停期间不受理"完成"：最后一块可能正好在暂停前一瞬落地，
        // 不拦的话会被标成 completed —— 用户看到的就是"点了暂停，它自己传完了"。
        // 返回 retryable，用户点继续后客户端会再调一次。
        if (string.Equals(session.State, TransferStates.Paused, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.TransferPaused, "任务已暂停，请先继续。", 409, retryable: true);
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
            return Fail(ErrorCodes.TransferPaused, "任务已暂停，请先继续。", 409, retryable: true);
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

        // 拉块只证明对端还活着；进度（transferred_bytes）只由回执推进，杜绝"读了就算收到"。
        //
        // 这里**只刷时间戳、不写状态**：请求在"不是 paused"检查之后才走到这里，
        // 期间用户可能点了暂停，无条件写 transferring 会把 paused 覆盖掉
        // （真机：暂停后文件又传完、几秒后跳进「已完成」）。
        //
        // 广播也一样：必须用**库里现在的状态**。曾经这里硬写 transferring，
        // 于是暂停后一条在途的拉块请求就把界面顶回"传输中"——底部提示已暂停、
        // 行内却写传输中。更新没命中活跃态（返回 null）时读一次最新记录。
        var now = DateTimeOffset.UtcNow;
        var liveState = await _repository.TouchActiveAsync(taskId, now, cancellationToken).ConfigureAwait(false);

        if (liveState is null)
        {
            var latest = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
            liveState = latest?.Status ?? record.Status;
        }

        RaiseProgress(ToDto(record, device.DisplayName) with
        {
            State = liveState,
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

        // 暂停期间不许"确认完成"：手机可能刚拉完最后一块就被暂停，
        // 这一步放过去的话任务直接变 completed —— 桌面端几秒后跳进「已完成」，
        // 用户看到的就是"暂停了但它自己传完了"。
        if (string.Equals(record.Status, TransferStates.Paused, StringComparison.Ordinal))
        {
            return Fail(ErrorCodes.TransferPaused, "任务已暂停，请先继续。", 409, retryable: true);
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
    /// 取消一批传输：**双向**终止 —— 作业作废、任务终结、**两端都删掉记录**。
    ///
    /// 与「删除记录」是两种语义，必须分开：
    /// - 删除记录 = 清理历史，**各删各的**，不影响另一端；
    /// - 取消传输 = 这条传输到此为止，**两端都不该再留着它**。
    ///
    /// 整文件夹里**已经传完**的那些也要一起清掉：用户点的是"取消这一批"，
    /// 另一端却还把它们归在「已完成」里（真机：勾了"同时删除文件"也照旧留着）。
    /// </summary>
    public async Task<TransferResult<TransferDeleteRecordsResultDto>> CancelBatchAsync(
        StoredDevice? device,
        IReadOnlyList<string> taskIds,
        bool deleteFiles,
        CancellationToken cancellationToken = default)
    {
        var ids = taskIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(ProtocolConstants.MaxDeleteTransferRecords)
            .ToArray();

        var result = new TransferDeleteRecordsResultDto();
        if (ids.Length == 0)
        {
            return TransferResult<TransferDeleteRecordsResultDto>.Ok(result);
        }

        // **补全整批**：用户选中的 taskId 未必覆盖整批。
        // 漏掉的那几条会继续显示在「已完成」里、已收到的文件也还在
        // （真机：取消一批 6 条，漏了 1 条 → 电脑端还显示已完成、文件剩一个）。
        // 取消"一批"的语义就是**这一批全清**。
        //
        // 补全有两条路，**都要走**：
        // ① 按**批次名**：上传方向（手机 → 电脑）的一批文件可能各自在不同目录下
        //    （手机里选了整个 Music，里面是很多子目录），按目录补全会漏。
        // ② 按**落点目录**：老数据没有批次名，只能靠目录兜底。
        var owner0 = await _repository.GetAsync(ids[0], cancellationToken).ConfigureAwait(false);
        if (owner0 is not null)
        {
            var siblings = new List<string>();

            if (!string.IsNullOrWhiteSpace(owner0.BatchName))
            {
                // **有批次名**：只按批次补全。批次名是这一批唯一的标识，
                // 跨目录也能覆盖全；再按目录补会**误伤同目录的别的批**
                // （用户取消 A 批，把 B 批也清了）。
                siblings.AddRange(await _repository
                    .ListTaskIdsByBatchNameAsync(owner0.DeviceId, owner0.BatchName, cancellationToken)
                    .ConfigureAwait(false));
            }
            else
            {
                // **没有批次名**（老数据）：只能按落点目录兜底。
                var directory = await ResolveBatchDirectoryAsync(ids, cancellationToken).ConfigureAwait(false);
                if (directory is not null)
                {
                    siblings.AddRange(await _repository
                        .ListTaskIdsByDirectoryAsync(owner0.DeviceId, directory, cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            if (siblings.Count > 0)
            {
                ids = ids.Concat(siblings).Distinct(StringComparer.Ordinal).ToArray();
            }
        }

        // 1) 还没结束的先终结（置 cancelled、清半成品）
        string? ownerDeviceId = null;
        foreach (var taskId in ids)
        {
            var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            ownerDeviceId ??= record.DeviceId;

            // **终态的也要置 cancelled**：只有 cancelled 才会被两端列表过滤掉。
            // 已经传完的那些如果留着 completed，另一端照旧把它们显示在「已完成」里
            // （真机：双端拉文件夹，取消后另一端还挂着已完成）。
            await CancelForDeleteAsync(record, device?.DisplayName ?? record.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            result.Cancelled++;
        }

        // 2) 删记录 + 删文件（勾了"同时删除"才删文件）
        var deleted = await DeleteRecordsCoreAsync(device, ids, deleteFiles, cancellationToken, hideLocally: false)
            .ConfigureAwait(false);
        if (deleted.Success && deleted.Value is not null)
        {
            result.Deleted += deleted.Value.Deleted;
            result.FilesDeleted += deleted.Value.FilesDeleted;
            result.Rejected.AddRange(deleted.Value.Rejected);
        }

        // 3) **不隐藏那一行**。
        //    "两端都不显示"靠的是**状态 cancelled + 两端列表都过滤 cancelled**，
        //    不是靠隐藏。隐藏会让手机端再也拉不到这条 → 它没法清本地记录 →
        //    本地状态卡在 transferring，永远挂在「传输中」（真机：电脑取消后手机还挂着）。
        //    行由 30 分钟的定时回收清掉：那时两端都已经处理完了。
        //    （"各删各的"仍然只属于"删除记录"那个动作——它走 hidden_for_device/desktop_hidden。）

        // 4) 作业一起作废：手机端不会再把它们领走
        if (device is not null && _files is not null)
        {
            await _files.SetDeviceJobsStatusForTransferTaskAsync(
                ids,
                [FileJobStatuses.Pending, FileJobStatuses.Paused, FileJobStatuses.Running],
                FileJobStatuses.Cancelled,
                cancellationToken).ConfigureAwait(false);
        }

        Log.Information(
            "取消整批 device={DeviceId} records={Records} cancelled={Cancelled} deleted={Deleted} files={Files}",
            device?.DeviceId ?? ownerDeviceId ?? "(宿主界面)", ids.Length, result.Cancelled, result.Deleted, result.FilesDeleted);

        return TransferResult<TransferDeleteRecordsResultDto>.Ok(result);
    }

    /// <summary>
    /// 批次名归一：空的一律当"没有批次"，太长的截断（库里是有限长度的列）。
    /// </summary>
    private static string? NormalizeBatchName(string? batchName)
    {
        if (string.IsNullOrWhiteSpace(batchName))
        {
            return null;
        }

        var trimmed = batchName.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    /// <summary>
    /// 这批任务的落点目录（relative_path 的第一段），用来按目录补全整批。
    /// </summary>
    private async Task<string?> ResolveBatchDirectoryAsync(
        IReadOnlyList<string> taskIds,
        CancellationToken cancellationToken)
    {
        foreach (var taskId in taskIds)
        {
            var record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
            var relative = record?.RelativePath;
            if (string.IsNullOrEmpty(relative))
            {
                continue;
            }

            var slash = relative.IndexOf('/');
            return slash > 0 ? relative[..slash] : null;
        }

        return null;
    }

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

    /// <param name="hideLocally">
    /// 是否给本端打「已删」标记。
    ///
    /// **取消**传 false：取消要靠**状态 cancelled** 让两端都不显示，而不是靠隐藏。
    /// 隐藏会让手机端再也拉不到这条 → 它没法清本地记录 → 本地状态卡在 transferring，
    /// 永远挂在「传输中」（真机：电脑取消后手机还挂着）。
    /// 行由定时回收清掉，那时两端都已经处理完了。
    /// </param>
    private async Task<TransferResult<TransferDeleteRecordsResultDto>> DeleteRecordsCoreAsync(
        StoredDevice? device,
        IReadOnlyList<string> taskIds,
        bool deleteFiles,
        CancellationToken cancellationToken,
        bool hideLocally = true)
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
        var hidden = new List<string>(clean.Length);
        // 这一批还没执行的作业也要作废：整文件夹下载是一文件一作业，删了记录但队列还在，
        // 手机会继续一个个建出新任务，列表里又冒出来（"删不干净"）。
        var queues = new HashSet<(string DeviceId, string Directory)>();

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
                // **正在传输的不给删记录**（需求 4）：传输中的条目只有「取消传输」一个动作。
                // 删记录是"清理历史"，不该顺手把正在传的这条也终止掉——
                // 那是"取消"该干的事，两者的语义必须分开。
                result.Rejected.Add($"{record.FileName}：还在传输，请先取消");
                continue;
            }
            else if (record.Status is TransferStates.Paused or TransferStates.Interrupted)
            {
                // 暂停中/中断中的：**删除记录 = 这条传输到此为止**。
                // 以前只终结"还在跑"的，暂停中的任务删完状态仍是 paused——
                // 于是对方点「继续」会被真的恢复，而记录早已被删掉、文件没人收
                // （真机：点继续照样提示"已继续传输"，实际任务已经没了）。
                // 终结之后，对方点继续会拿到 transfer_cancelled，
                // 两端都能问一句"已被接收方取消，要删记录吗"。
                await CancelForDeleteAsync(record, device?.DisplayName ?? record.DeviceId, cancellationToken)
                    .ConfigureAwait(false);

                record = await _repository.GetAsync(taskId, cancellationToken).ConfigureAwait(false) ?? record;
                result.Cancelled++;
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

            // 记住这一批的落点目录：队列里还没执行的作业要一起作废
            var dir = DirectoryKeyOf(record.RelativePath);
            if (dir is not null)
            {
                queues.Add((record.DeviceId, dir));
            }

            hidden.Add(taskId);
        }

        // 先作废队列，再删记录。反过来的话，两步之间手机还能领走作业、建出新任务。
        if (_files is not null && queues.Count > 0)
        {
            foreach (var (deviceId, dir) in queues)
            {
                try
                {
                    await _files.SetDeviceJobsStatusManyAsync(
                        deviceId, dir,
                        [FileJobStatuses.Pending, FileJobStatuses.Paused, FileJobStatuses.Running],
                        FileJobStatuses.Cancelled,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 队列作废失败不该挡住删记录：用户要的是"这条从列表里消失"
                    Log.Warning(ex, "删除传输记录时作废队列失败 device={DeviceId} dir={Dir}", deviceId, dir);
                }
            }
        }

        if (hidden.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;

            if (hideLocally)
            {
                // 各删各的：只写本端的标记，完全不看另一端删过没有。
                result.Deleted += device is null
                    ? await _repository.HideForDesktopAsync(hidden, now, cancellationToken).ConfigureAwait(false)
                    : await _repository.HideForDeviceAsync(hidden, device.DeviceId, now, cancellationToken)
                        .ConfigureAwait(false);

                // 两端都删过的行只是垃圾，顺手回收（不影响任何一端的可见性）
                await _repository.PurgeHiddenAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 取消：**不打标记**。记录靠 cancelled 状态让两端都不显示，
                // 而且手机端要能拉到它、据此清掉本地记录（隐藏了就拉不到了）。
                result.Deleted += hidden.Count;
            }
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
    /// 删除一个还在传的任务时先把它暂停：对端在下一块之前就会看到 paused 并停手。
    /// 顺序是"暂停 → 取消 → 清半成品"，暂停这一步是为了让在途的那一块不再写下去。
    /// </summary>
    private async Task PauseForDeleteAsync(string taskId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _repository.TouchAsync(taskId, TransferStates.Paused, now, cancellationToken).ConfigureAwait(false);

        // 内存里的上传会话也要翻状态，否则后续推块仍会被接受
        if (_uploads.TryGetValue(taskId, out var session))
        {
            session.State = TransferStates.Paused;
        }
    }

    /// <summary>
    /// 取消一个任务（**不校验归属**，只给删除流程用）：置 cancelled、清分块位图与临时文件。
    ///
    /// 上传的半成品在这里删；下载的半成品在手机上，由手机同步到 cancelled 之后自己清。
    /// </summary>
    private async Task CancelForDeleteAsync(
        TransferTaskRecord record,
        string deviceName,
        CancellationToken cancellationToken)
    {
        if (_uploads.TryGetValue(record.TaskId, out var session))
        {
            await FailSessionAsync(session, null, null, cancellationToken, TransferStates.Cancelled)
                .ConfigureAwait(false);
            Log.Information("删除记录前已取消上传任务 task={TaskId}", record.TaskId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await _repository.UpdateResultAsync(
            record.TaskId, TransferStates.Cancelled, record.Sha256,
            null, null, null, now, null, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteChunksAsync(record.TaskId, cancellationToken).ConfigureAwait(false);
        TryDeleteFile(record.TempPath);

        // **联动作业**：取消传输任务，关联的作业也要作废，
        // 否则手机端还会把它领走、重建一个新任务继续传（真机：电脑取消作业后手机还在传）。
        if (_files is not null)
        {
            await _files.SetDeviceJobsStatusForTransferTaskAsync(
                [record.TaskId],
                [FileJobStatuses.Running, FileJobStatuses.Pending, FileJobStatuses.Paused],
                FileJobStatuses.Cancelled,
                cancellationToken).ConfigureAwait(false);
        }


        RaiseProgress(ToDto(record, deviceName) with
        {
            State = TransferStates.Cancelled,
            UpdatedAtUnixMs = now.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>
    /// 把"任务落点相对路径"折成作业的目录键（去掉最后一段文件名）。
    ///
    /// 与界面分组的规则一致：整文件夹是逐条作业下发的，作业的 `destination_path`
    /// 就是这里的目录；单文件没有 `/`，返回 null——那种记录后面没有排队作业，
    /// 也不该拿空目录去匹配（空值会让 SQL 退化成"这台设备的所有作业"）。
    /// </summary>
    private static string? DirectoryKeyOf(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var slash = relativePath.LastIndexOf('/');
        return slash <= 0 ? null : relativePath[..slash];
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

        if (string.Equals(record.Status, TransferStates.Cancelled, StringComparison.Ordinal))
        {
            // 另一半删掉了这条正在传的任务（删除会先暂停再取消），或者别处点了取消。
            // 给一个专用错误码：两端据此提示"传输已被接收方取消，是否删除记录"，
            // 而不是含糊的"任务已结束"，更不能假装恢复成功——那会让用户以为还在传。
            return Fail(ErrorCodes.TransferCancelled, "传输已被接收方取消。", 409);
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

        // **联动作业**：恢复传输任务后，作业要回到 pending —— 手机端只领 pending 的作业，
        // 停在 paused/running 上手机就永远不会再动它（真机：恢复后手机没反应）。
        if (_files is not null)
        {
            await _files.SetDeviceJobsStatusForTransferTaskAsync(
                [taskId],
                [FileJobStatuses.Paused],
                FileJobStatuses.Pending,
                cancellationToken).ConfigureAwait(false);
        }

        RaiseProgress(dto);
        return TransferResult<TransferTaskDto>.Ok(dto);
    }

    /// <summary>
    /// 按 taskId 直查一条任务记录，**不受"按端隐藏"影响**。
    ///
    /// 本机界面要能操作"手机删过、桌面还留着"的行（删除是各删各的），
    /// 用列表接口反查会把这些行过滤掉，界面只能得到"任务不存在"这种误导性结论。
    /// </summary>
    public Task<TransferTaskRecord?> FindRecordAsync(
        string taskId,
        CancellationToken cancellationToken = default)
        => _repository.GetAsync(taskId, cancellationToken);

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

        // **联动作业**（暂停要作用在两层上）：
        // 作业是"电脑下发给手机的待办"，传输任务是"正在传的数据"。
        // 只停后者，作业还是 running —— 手机端续租照旧、照着推块，
        // "电脑暂停了、手机还在传"就是这么来的（真机）。
        if (_files is not null)
        {
            await _files.SetDeviceJobsStatusForTransferTaskAsync(
                [taskId],
                [FileJobStatuses.Running, FileJobStatuses.Pending],
                FileJobStatuses.Paused,
                cancellationToken).ConfigureAwait(false);
        }


        RaiseProgress(dto);
        return TransferResult<TransferTaskDto>.Ok(dto);
    }

    /// <summary>
    /// 取消一批：作业与传输任务**一起**终结。
    ///
    /// 作业是"电脑下发给手机的待办"，传输任务是"正在传的数据"。只停一层，
    /// 另一层会继续按老状态行动：
    /// - 作废了作业但任务还在 → 手机端列表里挂着一条"已取消"，还在传；
    /// - 取消了任务但作业还在 → 手机端把作业领走、重建一个新任务继续传。
    /// 两端都取消才是用户要的（需求 2）。
    /// </summary>
    public async Task<int> CancelJobsAndTasksAsync(
        StoredDevice device,
        string? destinationPath,
        IReadOnlyList<string>? taskIds = null,
        CancellationToken cancellationToken = default)
    {
        if (_files is null)
        {
            return 0;
        }

        var targets = (taskIds ?? Array.Empty<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (targets.Count == 0)
        {
            return 0;
        }

        // 作业作废：按 transfer_task_id **精确**反查，不按目录（否则会误伤同目录的别的批）
        await _files.SetDeviceJobsStatusForTransferTaskAsync(
            targets,
            [FileJobStatuses.Pending, FileJobStatuses.Paused, FileJobStatuses.Running],
            FileJobStatuses.Cancelled,
            cancellationToken).ConfigureAwait(false);

        // 传输任务一并取消（走完整的取消流程：清半成品、置 cancelled）
        var cancelled = 0;
        foreach (var taskId in targets)
        {
            var one = await CancelAsync(device, taskId, cancellationToken).ConfigureAwait(false);
            if (one.Success)
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    /// <summary>
    /// 停作业时，把这些作业关联的**传输任务**一起停（暂停或取消）。
    ///
    /// 作业是"电脑下发给手机的待办"，传输任务是"正在传的数据"。
    /// 只停作业的话，手机端下一次租约发现才停手，而另一端的列表里
    /// 那条任务还挂着、状态也没变（真机：手机端取消后，电脑端还显示一条"已取消"）。
    /// </summary>
    public async Task<int> StopJobsAndTasksAsync(
        StoredDevice device,
        string? destinationPath,
        bool cancel,
        CancellationToken cancellationToken = default)
    {
        if (_files is null)
        {
            return 0;
        }

        var active = cancel
            ? new[] { FileJobStatuses.Pending, FileJobStatuses.Paused, FileJobStatuses.Running }
            : new[] { FileJobStatuses.Pending, FileJobStatuses.Running };

        // 先查：作业状态改掉之后就查不到了
        var taskIds = await _files.ListActiveJobTransferTaskIdsAsync(
            device.DeviceId, destinationPath, active, cancellationToken).ConfigureAwait(false);

        var changed = await _files.SetDeviceJobsStatusManyAsync(
            device.DeviceId, destinationPath, active,
            cancel ? FileJobStatuses.Cancelled : FileJobStatuses.Paused,
            cancellationToken).ConfigureAwait(false);

        var stopped = 0;
        foreach (var taskId in taskIds)
        {
            var one = cancel
                ? await CancelAsync(device, taskId, cancellationToken).ConfigureAwait(false)
                : await PauseAsync(device, taskId, cancellationToken).ConfigureAwait(false);
            if (one.Success)
            {
                stopped++;
            }
        }

        if (stopped > 0)
        {
            Log.Information(
                "作业已停，关联传输任务同步 device={DeviceId} jobs={Jobs} tasks={Tasks} cancel={Cancel}",
                device.DeviceId, changed, stopped, cancel);
        }

        return changed;
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

            // 取消是**双向**的（需求 2）：状态置成 cancelled 就是"双方都看到取消"。
            // 不在这里打删除标记——两端列表都按"cancelled 不显示"过滤，
            // 手机端再把本地记录清掉。提前隐藏反而会让手机无从得知（看不到那条被删了）。

            Log.Information("上传任务已取消 task={TaskId}", taskId);

        // **联动作业**：取消传输任务，关联的作业也要作废，
        // 否则手机端还会把它领走、重建一个新任务继续传（真机：电脑取消作业后手机还在传）。
        if (_files is not null)
        {
            await _files.SetDeviceJobsStatusForTransferTaskAsync(
                [taskId],
                [FileJobStatuses.Running, FileJobStatuses.Pending, FileJobStatuses.Paused],
                FileJobStatuses.Cancelled,
                cancellationToken).ConfigureAwait(false);
        }


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

        // **联动作业**：取消传输任务，关联的作业也要作废，
        // 否则手机端还会把它领走、重建一个新任务继续传（真机：电脑取消作业后手机还在传）。
        if (_files is not null)
        {
            await _files.SetDeviceJobsStatusForTransferTaskAsync(
                [taskId],
                [FileJobStatuses.Running, FileJobStatuses.Pending, FileJobStatuses.Paused],
                FileJobStatuses.Cancelled,
                cancellationToken).ConfigureAwait(false);
        }


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
            // 取消很久了的记录：两端都处理完了（手机端删本地、电脑端不显示），
            // 留着只会把列表接口的名额占满（真机：341 条 cancelled）。
            var cancelledCutoff = now - _options.CancelledRetention;
            var purged = await _repository.PurgeCancelledAsync(cancelledCutoff, CancellationToken.None)
                .ConfigureAwait(false);
            if (purged > 0)
            {
                Log.Information("回收已取消的传输记录 rows={Rows}", purged);
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
            // **必须尊重数据库里的状态**：以前这里硬写成 transferring/preparing，
            // 于是"先暂停、再开始推"的窗口里重建出一个活跃会话——电脑那边明明按了暂停，
            // 手机一推块就被当成正常上传接受，"电脑拉手机的文件还是停不住"。
            // 会话不在内存（手机重连、电脑重启、上传被中断过）时重建，暂停就是被抹掉的。
            State = record.Status == TransferStates.Paused
                ? TransferStates.Paused
                : written > 0 ? TransferStates.Transferring : TransferStates.Preparing,
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
