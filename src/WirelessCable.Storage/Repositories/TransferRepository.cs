using Microsoft.Data.Sqlite;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Storage.Repositories;

/// <summary>
/// TransferTasks 表仓储。引擎负责状态机，这里只做读写，不做判断。
/// 所有时间列存 ISO-8601（"O"）文本，避免 SQLite 没有原生时间类型带来的时区歧义。
/// </summary>
public sealed class TransferRepository
{
    private readonly SqliteDatabaseFactory _factory;

    public TransferRepository(SqliteDatabaseFactory factory) => _factory = factory;

    public async Task InsertAsync(TransferTaskRecord task, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TransferTasks (task_id, direction, device_id, file_name, relative_path,
                                       total_bytes, transferred_bytes, chunk_size, chunk_count,
                                       status, sha256, error_code, error_message, temp_path, local_path,
                                       completed_chunks, conflict, source_device, target_device, directory_id,
                                       purpose, created_at_utc, updated_at_utc, completed_at_utc,
                                       batch_id, batch_name)
            VALUES ($id, $direction, $device, $name, $relative, $total, $transferred, $chunkSize, $chunkCount,
                    $status, $sha, $errorCode, $error, $temp, $local,
                    $completedChunks, $conflict, $source, $target, $directory,
                    $purpose, $created, $updated, $completed,
                    $batchId, $batchName);
            """;
        command.Parameters.AddWithValue("$id", task.TaskId);
        command.Parameters.AddWithValue("$batchId", (object?)task.BatchId ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchName", (object?)task.BatchName ?? DBNull.Value);
        command.Parameters.AddWithValue("$direction", task.Direction);
        command.Parameters.AddWithValue("$device", task.DeviceId);
        command.Parameters.AddWithValue("$name", task.FileName);
        command.Parameters.AddWithValue("$relative", task.RelativePath);
        command.Parameters.AddWithValue("$total", task.TotalBytes);
        command.Parameters.AddWithValue("$transferred", task.TransferredBytes);
        command.Parameters.AddWithValue("$chunkSize", task.ChunkSize);
        command.Parameters.AddWithValue("$chunkCount", task.ChunkCount);
        command.Parameters.AddWithValue("$status", task.Status);
        command.Parameters.AddWithValue("$sha", (object?)task.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)task.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)task.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$temp", (object?)task.TempPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$local", (object?)task.LocalPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedChunks", task.CompletedChunks);
        command.Parameters.AddWithValue("$conflict", (object?)task.Conflict ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)task.SourceDevice ?? DBNull.Value);
        command.Parameters.AddWithValue("$target", (object?)task.TargetDevice ?? DBNull.Value);
        command.Parameters.AddWithValue("$directory", (object?)task.DirectoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$purpose", (object?)task.Purpose ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", task.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", task.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$completed",
            (object?)task.CompletedAtUtc?.ToString("O") ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 分块

    /// <summary>建任务时一次性写入全部分块（全 pending）。与任务插入在同一连接事务里执行。</summary>
    public async Task InsertChunksAsync(
        IReadOnlyList<TransferChunkRecord> chunks,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        foreach (var chunk in chunks)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO TransferChunks (task_id, chunk_index, offset_bytes, length, sha256, status, updated_at_utc)
                VALUES ($task, $index, $offset, $length, NULL, 'pending', $updated);
                """;
            command.Parameters.AddWithValue("$task", chunk.TaskId);
            command.Parameters.AddWithValue("$index", chunk.ChunkIndex);
            command.Parameters.AddWithValue("$offset", chunk.Offset);
            command.Parameters.AddWithValue("$length", chunk.Length);
            command.Parameters.AddWithValue("$updated", chunk.UpdatedAtUtc.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按序号升序取一个任务的全部分块。恢复与续传的唯一事实来源。</summary>
    public async Task<IReadOnlyList<TransferChunkRecord>> ListChunksAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT * FROM TransferChunks WHERE task_id = $task ORDER BY chunk_index ASC;";
        command.Parameters.AddWithValue("$task", taskId);

        var items = new List<TransferChunkRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(MapChunk(reader));
        }

        return items;
    }

    public async Task<TransferChunkRecord?> GetChunkAsync(
        string taskId,
        int index,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT * FROM TransferChunks WHERE task_id = $task AND chunk_index = $index LIMIT 1;";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$index", index);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapChunk(reader) : null;
    }

    /// <summary>
    /// 把一个分块标为完成，并同事务推进任务的 transferred_bytes / completed_chunks / status。
    /// 两步必须一起成功，否则重启后"任务进度"和"分块位图"会对不上。
    /// 返回 true 表示本次真正推进；false 表示该块早已完成（幂等重放，未重复计数）。
    /// </summary>
    public async Task<bool> CompleteChunkAsync(
        string taskId,
        int index,
        string? sha256,
        long transferredBytes,
        string taskStatus,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var chunkCommand = connection.CreateCommand())
        {
            chunkCommand.Transaction = transaction;
            chunkCommand.CommandText =
                """
                UPDATE TransferChunks
                SET status = 'completed', sha256 = $sha, updated_at_utc = $updated
                WHERE task_id = $task AND chunk_index = $index AND status != 'completed';
                """;
            chunkCommand.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value);
            chunkCommand.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
            chunkCommand.Parameters.AddWithValue("$task", taskId);
            chunkCommand.Parameters.AddWithValue("$index", index);

            var affected = await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                // 已经是 completed：幂等重放，不动任务计数，调用方按"未重复写入"处理
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.Transaction = transaction;
            taskCommand.CommandText =
                """
                UPDATE TransferTasks
                SET transferred_bytes = $transferred,
                    completed_chunks = completed_chunks + 1,
                    status = $status,
                    updated_at_utc = $updated
                WHERE task_id = $task;
                """;
            taskCommand.Parameters.AddWithValue("$transferred", transferredBytes);
            taskCommand.Parameters.AddWithValue("$status", taskStatus);
            taskCommand.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
            taskCommand.Parameters.AddWithValue("$task", taskId);

            await taskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>回写分块哈希（下载块首次下发时惰性计算）。</summary>
    public async Task UpdateChunkSha256Async(
        string taskId,
        int index,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TransferChunks SET sha256 = $sha
            WHERE task_id = $task AND chunk_index = $index;
            """;
        command.Parameters.AddWithValue("$sha", sha256);
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$index", index);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 全部分块退回 pending，任务进度归零。
    /// 用于临时文件损坏后的重置与下载重试：数据不可信时，位图也不可信。
    /// </summary>
    public async Task ResetChunksAsync(
        string taskId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var chunkCommand = connection.CreateCommand())
        {
            chunkCommand.Transaction = transaction;
            chunkCommand.CommandText =
                """
                UPDATE TransferChunks SET status = 'pending', updated_at_utc = $updated
                WHERE task_id = $task;
                """;
            chunkCommand.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
            chunkCommand.Parameters.AddWithValue("$task", taskId);
            await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.Transaction = transaction;
            taskCommand.CommandText =
                """
                UPDATE TransferTasks
                SET transferred_bytes = 0, completed_chunks = 0, updated_at_utc = $updated
                WHERE task_id = $task;
                """;
            taskCommand.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
            taskCommand.Parameters.AddWithValue("$task", taskId);
            await taskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务终结时清理分块行（completed 任务保留作审计）。</summary>
    public async Task DeleteChunksAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TransferChunks WHERE task_id = $task;";
        command.Parameters.AddWithValue("$task", taskId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清掉任务行已不存在的孤儿分块（启动恢复时调用）。</summary>
    public async Task DeleteOrphanChunksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM TransferChunks WHERE task_id NOT IN (SELECT task_id FROM TransferTasks);";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除某台设备的全部传输记录（任务行 + 分块行），返回删掉的任务数。
    ///
    /// 只动数据库行，<b>不碰磁盘文件</b>：用户删的是"这台设备的访问权"，
    /// 不是它传过来的文件——那些已经落在接收目录里，属于用户数据。
    /// </summary>
    public async Task<int> DeleteForDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var chunkCommand = connection.CreateCommand())
        {
            chunkCommand.Transaction = transaction;
            chunkCommand.CommandText =
                """
                DELETE FROM TransferChunks
                WHERE task_id IN (SELECT task_id FROM TransferTasks WHERE device_id = $device);
                """;
            chunkCommand.Parameters.AddWithValue("$device", deviceId);
            await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int deleted;
        await using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.Transaction = transaction;
            taskCommand.CommandText = "DELETE FROM TransferTasks WHERE device_id = $device;";
            taskCommand.Parameters.AddWithValue("$device", deviceId);
            deleted = await taskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// 删除若干条传输记录（任务行 + 分块行）。
    ///
    /// 只动数据库行：要不要删磁盘文件由调用方决定（见 TransferEngine.DeleteRecordsAsync）。
    /// </summary>
    public async Task<int> DeleteTasksAsync(
        IReadOnlyList<string> taskIds,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        var deleted = 0;
        foreach (var taskId in taskIds)
        {
            await using (var chunkCommand = connection.CreateCommand())
            {
                chunkCommand.Transaction = transaction;
                chunkCommand.CommandText = "DELETE FROM TransferChunks WHERE task_id = $id;";
                chunkCommand.Parameters.AddWithValue("$id", taskId);
                await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var taskCommand = connection.CreateCommand())
            {
                taskCommand.Transaction = transaction;
                taskCommand.CommandText = "DELETE FROM TransferTasks WHERE task_id = $id;";
                taskCommand.Parameters.AddWithValue("$id", taskId);
                deleted += await taskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// 清掉所属设备已经不存在的传输记录（任务行 + 分块行），返回删掉的任务数。
    ///
    /// 老版本删设备时不删记录，留下的行会让传输中心一直显示陌生设备 ID 的任务，
    /// 而用户已经无法通过界面清理它们。启动时跑一次把这批历史垃圾收掉。
    /// 只删数据库行，磁盘文件不动。
    /// </summary>
    public async Task<int> DeleteOrphanDeviceTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var chunkCommand = connection.CreateCommand())
        {
            chunkCommand.Transaction = transaction;
            chunkCommand.CommandText =
                """
                DELETE FROM TransferChunks WHERE task_id IN (
                    SELECT task_id FROM TransferTasks
                    WHERE device_id NOT IN (SELECT device_id FROM Devices)
                );
                """;
            await chunkCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int deleted;
        await using (var taskCommand = connection.CreateCommand())
        {
            taskCommand.Transaction = transaction;
            taskCommand.CommandText =
                "DELETE FROM TransferTasks WHERE device_id NOT IN (SELECT device_id FROM Devices);";
            deleted = await taskCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>只更新进度与状态，用于每个分块之后的高频写入。</summary>
    public async Task UpdateProgressAsync(
        string taskId,
        long transferredBytes,
        string status,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TransferTasks
            SET transferred_bytes = $transferred, status = $status, updated_at_utc = $updated
            WHERE task_id = $id;
            """;
        command.Parameters.AddWithValue("$transferred", transferredBytes);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只刷新状态与时间戳，不动进度（下载拉块时的"存活"信号）。</summary>
    public async Task TouchAsync(
        string taskId,
        string status,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TransferTasks SET status = $status, updated_at_utc = $updated
            WHERE task_id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把失败/取消的任务退回"等待中"，进度归零，错误清空。
    /// 只用于下载重试：源文件在电脑上没动过，从头再拉一遍是安全的。
    /// </summary>
    public async Task ResetForRetryAsync(
        string taskId,
        string status,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TransferTasks
            SET transferred_bytes = 0,
                completed_chunks = 0,
                status = $status,
                error_code = NULL,
                error_message = NULL,
                completed_at_utc = NULL,
                updated_at_utc = $updated
            WHERE task_id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写入终态或纠错信息。</summary>
    public async Task UpdateResultAsync(
        string taskId,
        string status,
        string? sha256,
        string? errorCode,
        string? errorMessage,
        string? localPath,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE TransferTasks
            SET status = $status,
                sha256 = COALESCE($sha, sha256),
                error_code = $errorCode,
                error_message = $error,
                local_path = COALESCE($local, local_path),
                updated_at_utc = $updated,
                completed_at_utc = $completed
            WHERE task_id = $id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$sha", (object?)sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$local", (object?)localPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$completed",
            (object?)completedAtUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", taskId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferTaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM TransferTasks WHERE task_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", taskId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>某设备最近的任务，按更新时间倒序。列表只用于 UI 与轮询，不参与状态判断。</summary>
    public async Task<IReadOnlyList<TransferTaskRecord>> ListForDeviceAsync(
        string deviceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM TransferTasks
            WHERE device_id = $device
            ORDER BY
                CASE WHEN status IN ('completed', 'failed', 'cancelled') THEN 1 ELSE 0 END,
                updated_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        // 上限要与两端的 /transfers 一致（1000）：整夹下载一次几百条，
        // 卡在 200 会让同目录分组看到的条目数少于实际。
        // 排序同样让未结束的优先，否则排队中的条目会被已完成的历史挤出去。
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var items = new List<TransferTaskRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }

        return items;
    }

    /// <summary>所有设备最近的任务，供桌面端界面展示（手机发起的上传也在内）。</summary>
    public async Task<IReadOnlyList<TransferTaskRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM TransferTasks
            ORDER BY
                CASE WHEN status IN ('completed', 'failed', 'cancelled') THEN 1 ELSE 0 END,
                updated_at_utc DESC
            LIMIT $limit;
            """;
        // 未结束的（等传输 / 传输中 / 校验 / 暂停 / 中断）永远排在前面。
        // 只按 updated_at 排会有个致命边界：历史任务攒到几千条后，
        // 一个 330 文件的批次里"还在排队"的那些（updated_at 停在创建时刻）
        // 会被挤出前 N 条，界面上就只剩半批，进度永远对不上。
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var items = new List<TransferTaskRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }

        return items;
    }

    /// <summary>统计该设备未结束的任务数，用于并发上限。</summary>
    public async Task<int> CountActiveForDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM TransferTasks
            WHERE device_id = $device
              AND status IN ('waiting', 'preparing', 'transferring', 'verifying');
            """;
        command.Parameters.AddWithValue("$device", deviceId);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    /// <summary>取出超过时限仍未结束的任务（进程重启或客户端掉线留下的）。</summary>
    public async Task<IReadOnlyList<TransferTaskRecord>> ListStaleAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM TransferTasks
            WHERE status IN ('waiting', 'preparing', 'transferring', 'verifying')
              AND updated_at_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.UtcDateTime.ToString("O"));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>超过保留期仍未恢复的中断任务。</summary>
    public async Task<IReadOnlyList<TransferTaskRecord>> ListInterruptedAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM TransferTasks
            WHERE status IN ('interrupted', 'paused')
              AND updated_at_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.UtcDateTime.ToString("O"));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只查"已经开搬、却很久没有推进"的任务。
    /// 下载任务由客户端主动拉块，服务端不掌握连接状态，只能靠进度停摆来判断对端是否已经走了。
    /// 不含 verifying：整文件哈希是本地计算，耗时与进度无关，由空闲回收兜底。
    /// </summary>
    public async Task<IReadOnlyList<TransferTaskRecord>> ListStaleStartedAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT * FROM TransferTasks
            WHERE status IN ('preparing', 'transferring')
              AND updated_at_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.UtcDateTime.ToString("O"));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TransferTaskRecord>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var items = new List<TransferTaskRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Map(reader));
        }

        return items;
    }

    private static TransferTaskRecord Map(SqliteDataReader reader)
    {
        return new TransferTaskRecord
        {
            TaskId = reader.GetString(reader.GetOrdinal("task_id")),
            Direction = reader.GetString(reader.GetOrdinal("direction")),
            Purpose = GetNullableString(reader, "purpose"),
            DeviceId = reader.GetString(reader.GetOrdinal("device_id")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
            TotalBytes = reader.GetInt64(reader.GetOrdinal("total_bytes")),
            TransferredBytes = reader.GetInt64(reader.GetOrdinal("transferred_bytes")),
            ChunkSize = reader.GetInt32(reader.GetOrdinal("chunk_size")),
            ChunkCount = reader.GetInt32(reader.GetOrdinal("chunk_count")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Sha256 = GetNullableString(reader, "sha256"),
            ErrorCode = GetNullableString(reader, "error_code"),
            ErrorMessage = GetNullableString(reader, "error_message"),
            TempPath = GetNullableString(reader, "temp_path"),
            LocalPath = GetNullableString(reader, "local_path"),
            CompletedChunks = reader.GetInt32(reader.GetOrdinal("completed_chunks")),
            Conflict = GetNullableString(reader, "conflict"),
            SourceDevice = GetNullableString(reader, "source_device"),
            TargetDevice = GetNullableString(reader, "target_device"),
            DirectoryId = GetNullableString(reader, "directory_id"),
            CreatedAtUtc = ParseTime(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            UpdatedAtUtc = ParseTime(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
            CompletedAtUtc = ParseNullableTime(reader, "completed_at_utc"),
            BatchId = GetNullableString(reader, "batch_id"),
            BatchName = GetNullableString(reader, "batch_name"),
        };
    }

    private static TransferChunkRecord MapChunk(SqliteDataReader reader)
    {
        return new TransferChunkRecord
        {
            TaskId = reader.GetString(reader.GetOrdinal("task_id")),
            ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
            Offset = reader.GetInt64(reader.GetOrdinal("offset_bytes")),
            Length = reader.GetInt32(reader.GetOrdinal("length")),
            Sha256 = GetNullableString(reader, "sha256"),
            Status = reader.GetString(reader.GetOrdinal("status")),
            UpdatedAtUtc = ParseTime(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset ParseTime(string value)
        => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullableTime(SqliteDataReader reader, string column)
    {
        var value = GetNullableString(reader, column);
        return string.IsNullOrEmpty(value) ? null : ParseTime(value);
    }
}
