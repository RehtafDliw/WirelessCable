using Microsoft.Data.Sqlite;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Storage.Repositories;

/// <summary>
/// 手机授权目录的索引与作业队列仓储。
///
/// 索引是手机主动上行的目录快照：电脑据此浏览手机文件，无需手机开反向服务。
/// 作业是电脑下发给手机执行的文件操作（建目录/重命名/删除/移动/上传/下载）。
/// </summary>
public sealed class DeviceFileRepository
{
    private readonly SqliteDatabaseFactory _factory;

    public DeviceFileRepository(SqliteDatabaseFactory factory) => _factory = factory;

    /// <summary>
    /// 存储层的 root_id：`设备id/手机报的rootId`。
    ///
    /// 手机报的 rootId 只在**设备内**唯一——整机根更是固定字符串 `phone-all`。
    /// 以前直接拿它当主键，两台手机都授权整机时，后来的那条会撞上先来的记录：
    /// `ON CONFLICT(root_id)` 只更新名字和能力、不更新 device_id，
    /// 于是第二台手机"上报成功"却永远不属于自己，电脑端也就看不到（1.5.9 真机）。
    ///
    /// 前缀只在仓储内部生效：DTO、作业、手机协议里仍然是原来的 rootId。
    /// </summary>
    private static string StoredRootId(string deviceId, string rootId) => deviceId + "/" + rootId;

    private static string UnwrapRootId(string storedRootId)
    {
        var slash = storedRootId.IndexOf('/');
        return slash > 0 ? storedRootId[(slash + 1)..] : storedRootId;
    }

    // ------------------------------------------------------------ 根

    public async Task UpsertRootAsync(DeviceFileRootRecord root, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DeviceFileRoots (root_id, device_id, display_name, provider_kind,
                                        capabilities, granted_at_utc, updated_at_utc)
            VALUES ($id, $device, $name, $kind, $caps, $granted, $updated)
            ON CONFLICT(root_id) DO UPDATE SET
                device_id = $device,
                display_name = $name,
                provider_kind = $kind,
                capabilities = $caps,
                updated_at_utc = $updated;
            """;
        command.Parameters.AddWithValue("$id", StoredRootId(root.DeviceId, root.RootId));
        command.Parameters.AddWithValue("$device", root.DeviceId);
        command.Parameters.AddWithValue("$name", root.DisplayName);
        command.Parameters.AddWithValue("$kind", root.ProviderKind);
        command.Parameters.AddWithValue("$caps", root.Capabilities);
        command.Parameters.AddWithValue("$granted", root.GrantedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", root.UpdatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceFileRootRecord?> GetRootAsync(
        string deviceId,
        string rootId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT root_id, device_id, display_name, provider_kind, capabilities, granted_at_utc, updated_at_utc
            FROM DeviceFileRoots WHERE root_id = $id;
            """;
        command.Parameters.AddWithValue("$id", StoredRootId(deviceId, rootId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRoot(reader) : null;
    }

    /// <summary>
    /// 按手机报的 rootId 在**所有设备**里找根。
    ///
    /// 只用于分辨"不存在"和"是别人的"：存储层 id 是 `设备id/rootId`，
    /// 按本设备查不到时，得知道它是不是另一台手机的根——否则会回"未授权"，
    /// 让人以为授权丢了（1.5.9 加前缀隔离后需要这一步）。
    /// </summary>
    public async Task<DeviceFileRootRecord?> FindRootAnyDeviceAsync(
        string rootId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT root_id, device_id, display_name, provider_kind, capabilities, granted_at_utc, updated_at_utc
            FROM DeviceFileRoots WHERE root_id LIKE $suffix ESCAPE '\' LIMIT 1;
            """;
        command.Parameters.AddWithValue("$suffix", "%/" + EscapeLike(rootId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRoot(reader) : null;
    }

    public async Task<IReadOnlyList<DeviceFileRootRecord>> ListRootsAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT root_id, device_id, display_name, provider_kind, capabilities, granted_at_utc, updated_at_utc
            FROM DeviceFileRoots WHERE device_id = $device ORDER BY granted_at_utc;
            """;
        command.Parameters.AddWithValue("$device", deviceId);

        var results = new List<DeviceFileRootRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapRoot(reader));
        }

        return results;
    }

    /// <summary>移除根及其全部条目（用户撤销授权时调用）。</summary>
    public async Task DeleteRootAsync(
        string deviceId,
        string rootId,
        CancellationToken cancellationToken = default)
    {
        var stored = StoredRootId(deviceId, rootId);

        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var deleteEntries = connection.CreateCommand())
        {
            deleteEntries.Transaction = transaction;
            deleteEntries.CommandText = "DELETE FROM DeviceFileEntries WHERE root_id = $id;";
            deleteEntries.Parameters.AddWithValue("$id", stored);
            await deleteEntries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteRoot = connection.CreateCommand())
        {
            deleteRoot.Transaction = transaction;
            deleteRoot.CommandText = "DELETE FROM DeviceFileRoots WHERE root_id = $id;";
            deleteRoot.Parameters.AddWithValue("$id", stored);
            await deleteRoot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 剔除该设备不再上报的根，连同其条目。
    ///
    /// 索引是**快照**语义：手机每次上报都带"当前全部有效根 id"，不在名单里的
    /// 就是已经被用户撤销的目录。不清理的话，电脑端「我的手机」和手机端「文件」
    /// 会一直挂着已经不存在的授权目录——实测用户撤销全部授权后两边还各显两条。
    /// </summary>
    /// <returns>实际删除的根数量。</returns>
    public async Task<int> PruneRootsAsync(
        string deviceId,
        IReadOnlyCollection<string> keepRootIds,
        CancellationToken cancellationToken = default)
    {
        var keep = keepRootIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        var existing = await ListRootsAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var stale = existing
            .Where(root => !keep.Contains(root.RootId))
            .Select(root => root.RootId)
            .ToList();

        foreach (var rootId in stale)
        {
            // 复用 DeleteRootAsync：它已经在同一个事务里删根 + 删条目
            await DeleteRootAsync(deviceId, rootId, cancellationToken).ConfigureAwait(false);
        }

        return stale.Count;
    }

    /// <summary>
    /// 清掉"设备已经不在了"的索引根与条目。
    ///
    /// 2.10.0 之前解绑设备只删授权、不删索引，库里会留下再也用不到的根：
    /// 实测两台历史手机残留 7400+ 行 DeviceFileEntries，既占体积，也会让
    /// 任何按设备统计的查询数出已经不存在的东西。启动时跑一次，两条 DELETE 的事。
    /// </summary>
    /// <returns>删掉的根数量。</returns>
    public async Task<int> PruneOrphanRootsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        // 顺序不能反：先把"设备已不存在"的根删掉，再按剩余根清理条目。
        await using (var staleRoots = connection.CreateCommand())
        {
            staleRoots.Transaction = transaction;
            staleRoots.CommandText =
                "DELETE FROM DeviceFileRoots WHERE device_id NOT IN (SELECT device_id FROM Devices);";
            await staleRoots.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int orphanEntries;
        await using (var entries = connection.CreateCommand())
        {
            entries.Transaction = transaction;
            entries.CommandText =
                "DELETE FROM DeviceFileEntries WHERE root_id NOT IN (SELECT root_id FROM DeviceFileRoots);";
            orphanEntries = await entries.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return orphanEntries;
    }

    // ------------------------------------------------------------ 条目
    /// <summary>整批替换某个根的条目快照。上行索引是一次性覆盖语义，避免残留已删除的条目。</summary>
    public async Task ReplaceEntriesAsync(
        string deviceId,
        string rootId,
        IReadOnlyList<DeviceFileEntryRecord> entries,
        CancellationToken cancellationToken = default)
    {
        var stored = StoredRootId(deviceId, rootId);

        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM DeviceFileEntries WHERE root_id = $id;";
            clear.Parameters.AddWithValue("$id", stored);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in entries)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO DeviceFileEntries (root_id, relative_path, display_name, is_directory,
                                               size_bytes, modified_utc, capabilities, updated_at_utc)
                VALUES ($root, $path, $name, $isDir, $size, $modified, $caps, $updated);
                """;
            insert.Parameters.AddWithValue("$root", stored);
            insert.Parameters.AddWithValue("$path", entry.RelativePath);
            insert.Parameters.AddWithValue("$name", entry.DisplayName);
            insert.Parameters.AddWithValue("$isDir", entry.IsDirectory ? 1 : 0);
            insert.Parameters.AddWithValue("$size", entry.SizeBytes);
            insert.Parameters.AddWithValue(
                "$modified",
                (object?)entry.ModifiedUtc?.ToString("O") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$caps", entry.Capabilities);
            insert.Parameters.AddWithValue("$updated", entry.UpdatedAtUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>列出某个根下的直接子项。</summary>
    public async Task<IReadOnlyList<DeviceFileEntryRecord>> ListEntriesAsync(
        string deviceId,
        string rootId,
        string parentPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedParent = NormalizeRelative(parentPath);

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT root_id, relative_path, display_name, is_directory, size_bytes, modified_utc, capabilities, updated_at_utc
            FROM DeviceFileEntries
            WHERE root_id = $root
              AND (
                    CASE WHEN $parent = ''
                         THEN relative_path NOT LIKE '%/%'
                         ELSE (relative_path LIKE $prefix ESCAPE '\'
                               AND relative_path NOT LIKE $deepPrefix ESCAPE '\')
                   END)
            ORDER BY is_directory DESC, display_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$root", StoredRootId(deviceId, rootId));
        command.Parameters.AddWithValue("$parent", normalizedParent);
        command.Parameters.AddWithValue("$prefix", normalizedParent.Length == 0 ? "%/" : normalizedParent + "/%");
        command.Parameters.AddWithValue(
            "$deepPrefix",
            normalizedParent.Length == 0 ? "%/%" : normalizedParent + "/%/%");

        var results = new List<DeviceFileEntryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapEntry(reader));
        }

        return results;
    }

    /// <summary>按相对路径精确查一个条目。</summary>
    public async Task<DeviceFileEntryRecord?> GetEntryAsync(
        string deviceId,
        string rootId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelative(relativePath);

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT root_id, relative_path, display_name, is_directory, size_bytes, modified_utc, capabilities, updated_at_utc
            FROM DeviceFileEntries WHERE root_id = $root AND relative_path = $path;
            """;
        command.Parameters.AddWithValue("$root", StoredRootId(deviceId, rootId));
        command.Parameters.AddWithValue("$path", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapEntry(reader) : null;
    }

    /// <summary>在整个索引里按文件名搜索（不区分大小写），用于电脑端的文件名搜索。</summary>
    public async Task<IReadOnlyList<DeviceFileEntryRecord>> SearchEntriesAsync(
        string deviceId,
        string rootId,
        string keyword,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT root_id, relative_path, display_name, is_directory, size_bytes, modified_utc, capabilities, updated_at_utc
            FROM DeviceFileEntries
            WHERE root_id = $root AND display_name LIKE $pattern ESCAPE '\'
            ORDER BY is_directory DESC, display_name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$root", StoredRootId(deviceId, rootId));
        command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(keyword) + "%");
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<DeviceFileEntryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapEntry(reader));
        }

        return results;
    }

    // ------------------------------------------------------------ 作业

    public async Task InsertJobAsync(FileOperationJobRecord job, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO FileOperationJobs (job_id, device_id, root_id, operation, source_path,
                                          destination_path, display_name, transfer_task_id,
                                          status, error_code,
                                          error_message, created_at_utc, updated_at_utc, result_payload,
                                          batch_total, batch_name, batch_total_bytes)
            VALUES ($id, $device, $root, $op, $src, $dst, $name, $taskId, $status, $errCode,
                    $err, $created, $updated, $payload, $batchTotal, $batchName, $batchBytes);
            """;
        command.Parameters.AddWithValue("$id", job.JobId);
        command.Parameters.AddWithValue("$device", job.DeviceId);
        command.Parameters.AddWithValue("$root", job.RootId);
        command.Parameters.AddWithValue("$op", job.Operation);
        command.Parameters.AddWithValue("$src", (object?)job.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$dst", (object?)job.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)job.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", (object?)job.TransferTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$errCode", (object?)job.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$err", (object?)job.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", job.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", job.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$payload", (object?)job.ResultPayload ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchTotal", (object?)job.BatchTotal ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchName", (object?)job.BatchName ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchBytes", (object?)job.BatchTotalBytes ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取该设备待执行的作业（pending 状态），按创建顺序。</summary>
    /// <summary>
    /// 把某设备某目录下的一批作业整体换状态（pending ↔ paused）。
    ///
    /// 整文件夹下载时，真正"还没做"的部分留在作业队列里——传输任务要等手机执行才建出来。
    /// 只暂停已经建出来的任务，队列里的会继续被取走，用户按了暂停文件还在流。
    /// </summary>
    /// <param name="destinationPath">落点相对目录；空 = 该设备全部作业。
    /// 按前缀匹配，因为一个文件夹分组覆盖它的所有子目录。</param>
    public Task<int> SetJobsStatusAsync(
        string? deviceId,
        string? destinationPath,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default)
        => SetJobsStatusManyAsync(deviceId, destinationPath, [fromStatus], toStatus, cancellationToken);

    /// <summary>
    /// 按状态批量改作业（可一次匹配多个原状态）。
    ///
    /// 暂停/取消必须能打到 **running**：手机把作业领走之后就在本地执行，
    /// 只改 pending 等于没停（"暂停刹不住车"的根）。改完同时清掉租约。
    /// </summary>
    public async Task<int> SetJobsStatusManyAsync(
        string? deviceId,
        string? destinationPath,
        IReadOnlyList<string> fromStatuses,
        string toStatus,
        CancellationToken cancellationToken = default)
    {
        if (fromStatuses.Count == 0)
        {
            return 0;
        }

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();

        // 传输记录被删光时就取不到 deviceId 了，但队列里还压着几十上百个作业。
        // 这时按目录匹配所有设备——本机同时连两台手机、且落点目录同名的概率极低，
        // 比"点了暂停什么都不动"好。
        var hasDevice = !string.IsNullOrWhiteSpace(deviceId);
        var hasPath = !string.IsNullOrWhiteSpace(destinationPath);
        var deviceFilter = hasDevice ? "AND device_id = $device" : string.Empty;
        var pathFilter = hasPath
            ? "AND (destination_path = $dir OR destination_path LIKE $dir || '/%')"
            : string.Empty;

        var placeholders = new List<string>(fromStatuses.Count);
        for (var i = 0; i < fromStatuses.Count; i++)
        {
            placeholders.Add($"$from{i}");
        }

        command.CommandText = $"""
            UPDATE FileOperationJobs
            SET status = $to, updated_at_utc = $now, lease_until_utc = NULL
            WHERE status IN ({string.Join(", ", placeholders)}) {deviceFilter} {pathFilter};
            """;

        if (hasDevice)
        {
            command.Parameters.AddWithValue("$device", deviceId!);
        }

        for (var i = 0; i < fromStatuses.Count; i++)
        {
            command.Parameters.AddWithValue($"$from{i}", fromStatuses[i]);
        }

        command.Parameters.AddWithValue("$to", toStatus);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (hasPath)
        {
            command.Parameters.AddWithValue("$dir", destinationPath!.TrimEnd('/'));
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 每个落点目录下**还没结束**的作业数（pending / paused / running）。
    ///
    /// 桌面端拿它判断"这一批还在不在传输中"：整文件夹是逐个作业下发的，
    /// 暂停时队列里压着的文件还没有对应的传输任务，只看任务列表会以为批已经结束——
    /// 界面几秒后把这一行扔进「已完成」，用户却在手机上看到任务还挂着。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, (int Open, int Paused)>> CountOpenJobsByDirectoryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT destination_path,
                   COUNT(*),
                   SUM(CASE WHEN status = 'paused' THEN 1 ELSE 0 END)
            FROM FileOperationJobs
            WHERE status IN ('pending', 'paused', 'running')
              AND destination_path IS NOT NULL AND destination_path <> ''
            GROUP BY destination_path;
            """;

        var map = new Dictionary<string, (int Open, int Paused)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // SQLite 的 COUNT/SUM 都是 64 位，GetInt32 会抛 InvalidCast
            var open = (int)reader.GetInt64(1);
            var paused = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2);
            map[reader.GetString(0)] = (open, paused);
        }

        return map;
    }

    public async Task<IReadOnlyList<FileOperationJobRecord>> ListPendingJobsAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id, device_id, root_id, operation, source_path, destination_path,
                   display_name, transfer_task_id, status, error_code, error_message,
                   created_at_utc, updated_at_utc, result_payload, batch_total, batch_name, batch_total_bytes
            FROM FileOperationJobs WHERE device_id = $device AND status = 'pending' ORDER BY created_at_utc;
            """;
        command.Parameters.AddWithValue("$device", deviceId);

        var results = new List<FileOperationJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapJob(reader));
        }

        return results;
    }

    /// <summary>
    /// 回收"取走后一直没有回报"的作业。
    ///
    /// 手机被系统回收、进程被杀时不会有人回报结果，这些作业会永远停在 running：
    /// 桌面端一直显示"等待手机执行"，用户重试还会被当成两个作业。
    /// 判定分两种：
    /// <list type="bullet">
    /// <item>带传输任务的（upload / download）：只要任务还在非终态且最近还有进度，就说明手机还在干，不动；</item>
    /// <item>其余情况：取走时间超过 <paramref name="takenBeforeUtc"/> 即判失败。</item>
    /// </list>
    /// 判失败而不是重投，是因为重跑一个已经执行过的删除/改名会让用户看到奇怪的结果；
    /// 失败后由用户决定要不要再来一次。
    /// </summary>
    /// <param name="takenBeforeUtc">取走时间早于此时刻的作业才会被回收。</param>
    /// <param name="progressSinceUtc">关联传输任务在此之后还有进度，就认为手机仍在执行。</param>
    public async Task<IReadOnlyList<FileOperationJobRecord>> ReclaimStaleJobsAsync(
        string deviceId,
        DateTimeOffset takenBeforeUtc,
        DateTimeOffset progressSinceUtc,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        var reclaimed = new List<FileOperationJobRecord>();

        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT job_id, device_id, root_id, operation, source_path, destination_path,
                       display_name, transfer_task_id, status, error_code, error_message,
                       created_at_utc, updated_at_utc, result_payload, batch_total, batch_name, batch_total_bytes
                FROM FileOperationJobs
                WHERE device_id = $device
                  AND status = 'running'
                  AND updated_at_utc < $taken
                  AND NOT EXISTS (
                      SELECT 1 FROM TransferTasks
                      WHERE TransferTasks.task_id = FileOperationJobs.transfer_task_id
                        AND TransferTasks.status IN ('waiting', 'preparing', 'transferring', 'verifying')
                        AND TransferTasks.updated_at_utc >= $progress
                  );
                """;
            select.Parameters.AddWithValue("$device", deviceId);
            select.Parameters.AddWithValue("$taken", takenBeforeUtc.ToUniversalTime().ToString("O"));
            select.Parameters.AddWithValue("$progress", progressSinceUtc.ToUniversalTime().ToString("O"));

            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reclaimed.Add(MapJob(reader));
            }
        }

        if (reclaimed.Count > 0)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            foreach (var job in reclaimed)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE FileOperationJobs
                    SET status = 'failed', error_code = $code, error_message = $msg, updated_at_utc = $now
                    WHERE job_id = $id AND status = 'running';
                    """;
                update.Parameters.AddWithValue("$id", job.JobId);
                update.Parameters.AddWithValue("$code", errorCode);
                update.Parameters.AddWithValue("$msg", errorMessage);
                update.Parameters.AddWithValue("$now", now);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reclaimed;
    }

    public async Task<FileOperationJobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id, device_id, root_id, operation, source_path, destination_path,
                   display_name, transfer_task_id, status, error_code, error_message,
                   created_at_utc, updated_at_utc, result_payload, batch_total, batch_name, batch_total_bytes
            FROM FileOperationJobs WHERE job_id = $id;
            """;
        command.Parameters.AddWithValue("$id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    public async Task UpdateJobStatusAsync(
        string jobId,
        string status,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default,
        string? resultPayload = null,
        DateTimeOffset? leaseUntilUtc = null)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        // 租约只在 running 时有意义：写终态/暂停时顺手清掉，
        // 否则一条 completed 的作业还挂着租约，回收逻辑会看着很怪
        command.CommandText =
            """
            UPDATE FileOperationJobs
            SET status = $status, error_code = $errCode, error_message = $err, updated_at_utc = $updated,
                result_payload = COALESCE($payload, result_payload),
                lease_until_utc = CASE WHEN $status = 'running' THEN $lease ELSE NULL END
            WHERE job_id = $id;
            """;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$errCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$err", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$payload", (object?)resultPayload ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lease",
            leaseUntilUtc is null ? DBNull.Value : leaseUntilUtc.Value.ToUniversalTime().ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 续租并带回服务端的最新状态。
    ///
    /// 手机执行作业期间每几秒调一次：状态还是 running 就继续干；
    /// 变成 paused/cancelled 就立刻停手（**不回报结果**，状态由服务端定）。
    /// 这是"一次领多个作业"不失控的关键——没有它，已被取走的作业只能等它传完。
    /// </summary>
    public async Task<string?> RenewJobLeaseAsync(
        string jobId,
        string deviceId,
        DateTimeOffset leaseUntilUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();

        string? status;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText =
                "SELECT status FROM FileOperationJobs WHERE job_id = $id AND device_id = $device;";
            read.Parameters.AddWithValue("$id", jobId);
            read.Parameters.AddWithValue("$device", deviceId);
            status = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        if (status is null)
        {
            return null;
        }

        if (!string.Equals(status, "running", StringComparison.Ordinal))
        {
            return status;
        }

        await using var renew = connection.CreateCommand();
        renew.CommandText =
            """
            UPDATE FileOperationJobs
            SET lease_until_utc = $lease, updated_at_utc = $now
            WHERE job_id = $id AND device_id = $device AND status = 'running';
            """;
        renew.Parameters.AddWithValue("$id", jobId);
        renew.Parameters.AddWithValue("$device", deviceId);
        renew.Parameters.AddWithValue("$lease", leaseUntilUtc.ToUniversalTime().ToString("O"));
        renew.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        var changed = await renew.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // 续约时被别的请求改成了别的状态：以库里为准
        return changed > 0 ? "running" : status;
    }

    /// <summary>
    /// 把手机建出来的传输任务挂到作业上。
    ///
    /// 电脑拉手机时，传输任务是手机自己在 `uploadStart` 里建的，服务端本来不知道。
    /// 暂停后继续，作业会被重新取走；没有这条关联就只能再建一个同名任务
    /// （列表里两条、文件传两遍）。有了它，手机能认出"上次传到一半的那个"。
    /// </summary>
    public async Task AttachJobTransferTaskAsync(
        string jobId,
        string deviceId,
        string transferTaskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE FileOperationJobs SET transfer_task_id = $task, updated_at_utc = $now
            WHERE job_id = $id AND device_id = $device;
            """;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$task", transferTaskId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按**传输任务**改它关联的作业状态。
    ///
    /// 传输任务与作业是同一件事的两层：作业是"电脑下发给手机的待办"，
    /// 传输任务是"正在传的数据"。电脑点暂停/取消时只动其中一层，
    /// 另一层不动就会各说各话——作业还在 running，手机续租照旧、照着推块，
    /// "暂停同步"就成了空话（真机：电脑暂停作业，手机还在传）。
    /// </summary>
    public async Task<int> SetJobsStatusForTransferTaskAsync(
        IReadOnlyList<string> transferTaskIds,
        IReadOnlyList<string> fromStatuses,
        string toStatus,
        CancellationToken cancellationToken = default)
    {
        if (transferTaskIds.Count == 0 || fromStatuses.Count == 0)
        {
            return 0;
        }

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();

        var taskPlaceholders = new List<string>(transferTaskIds.Count);
        for (var i = 0; i < transferTaskIds.Count; i++)
        {
            taskPlaceholders.Add($"$task{i}");
        }

        var statusPlaceholders = new List<string>(fromStatuses.Count);
        for (var i = 0; i < fromStatuses.Count; i++)
        {
            statusPlaceholders.Add($"$from{i}");
        }

        // 按 transfer_task_id 精确反查：**不能按目录作废**，否则取消一个文件
        // 会把同一落点目录下的另一批也作废掉（手机端 batch 不带目录参数）。
        command.CommandText = $"""
            UPDATE FileOperationJobs
            SET status = $to, updated_at_utc = $now, lease_until_utc = NULL
            WHERE transfer_task_id IN ({string.Join(", ", taskPlaceholders)})
              AND status IN ({string.Join(", ", statusPlaceholders)});
            """;

        command.Parameters.AddWithValue("$to", toStatus);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        for (var i = 0; i < transferTaskIds.Count; i++)
        {
            command.Parameters.AddWithValue($"$task{i}", transferTaskIds[i]);
        }

        for (var i = 0; i < fromStatuses.Count; i++)
        {
            command.Parameters.AddWithValue($"$from{i}", fromStatuses[i]);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 列出某设备"还没结束的作业"所关联的传输任务。
    ///
    /// 作业是"电脑下发给手机的待办"，传输任务是"正在传的数据"。
    /// 停作业的时候要把这些任务一起停，否则"暂停/取消"只作用在一半上
    /// （真机：手机端取消后，另一边还挂着一条"已取消"、也还在传）。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListActiveJobTransferTaskIdsAsync(
        string deviceId,
        string? destinationPath,
        IReadOnlyList<string> fromStatuses,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || fromStatuses.Count == 0)
        {
            return Array.Empty<string>();
        }

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();

        var placeholders = new List<string>(fromStatuses.Count);
        for (var i = 0; i < fromStatuses.Count; i++)
        {
            placeholders.Add($"$from{i}");
        }

        var hasPath = !string.IsNullOrWhiteSpace(destinationPath);
        command.CommandText = $"""
            SELECT transfer_task_id FROM FileOperationJobs
            WHERE device_id = $device
              AND status IN ({string.Join(", ", placeholders)})
              AND transfer_task_id IS NOT NULL AND transfer_task_id <> ''
              {(hasPath ? "AND (destination_path = $dir OR destination_path LIKE $dir || '/%')" : string.Empty)};
            """;

        command.Parameters.AddWithValue("$device", deviceId);
        for (var i = 0; i < fromStatuses.Count; i++)
        {
            command.Parameters.AddWithValue($"$from{i}", fromStatuses[i]);
        }

        if (hasPath)
        {
            command.Parameters.AddWithValue("$dir", destinationPath!);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>列出某设备的近期作业，供电脑端展示操作历史。</summary>
    public async Task<IReadOnlyList<FileOperationJobRecord>> ListRecentJobsAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id, device_id, root_id, operation, source_path, destination_path,
                   display_name, transfer_task_id, status, error_code, error_message,
                   created_at_utc, updated_at_utc, result_payload, batch_total, batch_name, batch_total_bytes
            FROM FileOperationJobs WHERE device_id = $device ORDER BY created_at_utc DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<FileOperationJobRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapJob(reader));
        }

        return results;
    }

    // ------------------------------------------------------------ 映射

    private static DeviceFileRootRecord MapRoot(SqliteDataReader reader) => new()
    {
        RootId = UnwrapRootId(reader.GetString(0)),
        DeviceId = reader.GetString(1),
        DisplayName = reader.GetString(2),
        ProviderKind = reader.GetString(3),
        Capabilities = reader.GetInt32(4),
        GrantedAtUtc = SharedDirectoryRepository.ParseTime(reader.GetString(5)),
        UpdatedAtUtc = SharedDirectoryRepository.ParseTime(reader.GetString(6)),
    };

    private static DeviceFileEntryRecord MapEntry(SqliteDataReader reader) => new()
    {
        RootId = UnwrapRootId(reader.GetString(0)),
        RelativePath = reader.GetString(1),
        DisplayName = reader.GetString(2),
        IsDirectory = reader.GetInt32(3) != 0,
        SizeBytes = reader.GetInt64(4),
        ModifiedUtc = reader.IsDBNull(5)
            ? null
            : SharedDirectoryRepository.ParseTime(reader.GetString(5)),
        Capabilities = reader.GetInt32(6),
        UpdatedAtUtc = SharedDirectoryRepository.ParseTime(reader.GetString(7)),
    };

    private static FileOperationJobRecord MapJob(SqliteDataReader reader) => new()
    {
        JobId = reader.GetString(0),
        DeviceId = reader.GetString(1),
        RootId = reader.GetString(2),
        Operation = reader.GetString(3),
        SourcePath = reader.IsDBNull(4) ? null : reader.GetString(4),
        DestinationPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        DisplayName = reader.IsDBNull(6) ? null : reader.GetString(6),
        TransferTaskId = reader.IsDBNull(7) ? null : reader.GetString(7),
        Status = reader.GetString(8),
        ErrorCode = reader.IsDBNull(9) ? null : reader.GetString(9),
        ErrorMessage = reader.IsDBNull(10) ? null : reader.GetString(10),
        CreatedAtUtc = SharedDirectoryRepository.ParseTime(reader.GetString(11)),
        UpdatedAtUtc = SharedDirectoryRepository.ParseTime(reader.GetString(12)),
        ResultPayload = reader.IsDBNull(13) ? null : reader.GetString(13),
        BatchTotal = reader.IsDBNull(14) ? null : reader.GetInt32(14),
        BatchName = reader.IsDBNull(15) ? null : reader.GetString(15),
        BatchTotalBytes = reader.IsDBNull(16) ? null : reader.GetInt64(16),
    };

    /// <summary>把相对路径规范化成 "a/b" 形式；空串表示根。</summary>
    internal static string NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "." && segment.Length > 0);

        return string.Join('/', segments);
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
