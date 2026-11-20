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
            // 进度照常记，但 **不允许把 paused 覆盖回 transferring**。
            //
            // 竞态：用户按下暂停的那一刻，可能正好有一块在写。写完后这行 SQL 会把
            // paused 改成 transferring，于是下一块的暂停检查（WriteChunkAsync 里
            // `session.State == paused`）就放行了 —— 真机现象是"点了暂停，80 MB 的文件
            // 还是从头传到尾"（实测 16 → 48 → 75 → completed，只用了 3 秒）。
            // 用 CASE 保住 paused：块可以落地，但状态不再回滚，下一块必然被拒。
            taskCommand.CommandText =
                """
                UPDATE TransferTasks
                SET transferred_bytes = $transferred,
                    completed_chunks = completed_chunks + 1,
                    status = CASE WHEN status = 'paused' THEN status ELSE $status END,
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
    /// 回收"取消很久了"的记录：行与分块一起删。
    ///
    /// 取消是双向的，两端都不再显示它，留着只是占地方（真机：341 条），
    /// 还会把列表接口的名额占满——手机端按 limit 拉列表就拉不到该清理的那几条。
    /// </summary>
    public async Task<int> PurgeCancelledAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM TransferChunks
            WHERE task_id IN (
                SELECT task_id FROM TransferTasks
                WHERE status = 'cancelled' AND updated_at_utc < $cutoff
            );
            DELETE FROM TransferTasks
            WHERE status = 'cancelled' AND updated_at_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按**批次名**列出该设备这一批的全部任务 id。
    ///
    /// 「取消一批」要靠它补全：上传方向（手机 → 电脑）的一批文件可能
    /// **各自在不同的目录**下（手机里选了整个 Music，里面是很多子目录），
    /// 按目录补全只对"同一目录"有效，跨目录就漏——漏掉的那条还是 transferring，
    /// 电脑端「传输中」角标就一直亮着（真机）。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListTaskIdsByBatchNameAsync(
        string deviceId,
        string batchName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(batchName))
        {
            return Array.Empty<string>();
        }

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT task_id FROM TransferTasks
            WHERE device_id = $device AND batch_name = $batch;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$batch", batchName);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// 列出某设备落在某个目录下的**全部**任务（含已隐藏的）。
    ///
    /// 取消"一批"时要靠它补全：用户选中的 taskId 未必覆盖整批
    /// （有的还没建出任务、有的已经不在本机的列表里），
    /// 漏掉的那几条会继续显示在「已完成」里、文件也还在（真机）。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListTaskIdsByDirectoryAsync(
        string deviceId,
        string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(directory))
        {
            return Array.Empty<string>();
        }

        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT task_id FROM TransferTasks
            WHERE device_id = $device
              AND (relative_path = $dir OR relative_path LIKE $dir || '/%')
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$dir", directory);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// 手机端删除记录：把这一批对本设备隐藏（不动桌面端，也不看桌面端删没删）。
    ///
    /// 两端共用一张表，删记录不能直接删行——否则一端清理历史会把另一端的也抹掉
    /// （用户报的"手机上删了记录，电脑的记录一起没了，文件其实还在"）。
    /// 语义是**各删各的**：这里只写手机自己的标记。
    /// </summary>
    public async Task<int> HideForDeviceAsync(
        IReadOnlyList<string> taskIds,
        string deviceId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Count == 0 || string.IsNullOrWhiteSpace(deviceId))
        {
            return 0;
        }

        await using var connection = _factory.Open();
        var hidden = 0;

        foreach (var taskId in taskIds)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE TransferTasks
                SET hidden_for_device = $device, updated_at_utc = $updated
                WHERE task_id = $id;
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$id", taskId);

            hidden += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return hidden;
    }

    /// <summary>
    /// 桌面端删除记录：只对桌面隐藏。手机端照旧看得见，反之亦然。
    /// </summary>
    public async Task<int> HideForDesktopAsync(
        IReadOnlyList<string> taskIds,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (taskIds.Count == 0)
        {
            return 0;
        }

        await using var connection = _factory.Open();
        var hidden = 0;

        foreach (var taskId in taskIds)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE TransferTasks
                SET desktop_hidden = 1, updated_at_utc = $updated
                WHERE task_id = $id;
                """;
            command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$id", taskId);

            hidden += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return hidden;
    }

    /// <summary>
    /// 清理"两端都已经删过"的记录（行 + 分块）。
    ///
    /// 这只是垃圾回收，不参与任何一端的删除判定：删除对用户是即时生效的，
    /// 行还在不在库里的区别只是占不占磁盘。任何一端都看不到它时才清。
    /// 返回删掉的行数。
    /// </summary>
    public async Task<int> PurgeHiddenAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var transaction = connection.BeginTransaction();

        await using (var chunks = connection.CreateCommand())
        {
            chunks.Transaction = transaction;
            chunks.CommandText =
                """
                DELETE FROM TransferChunks WHERE task_id IN (
                    SELECT task_id FROM TransferTasks
                    WHERE desktop_hidden = 1 AND hidden_for_device IS NOT NULL);
                """;
            await chunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int purged;
        await using (var tasks = connection.CreateCommand())
        {
            tasks.Transaction = transaction;
            tasks.CommandText =
                "DELETE FROM TransferTasks WHERE desktop_hidden = 1 AND hidden_for_device IS NOT NULL;";
            purged = await tasks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return purged;
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
    /// 只刷新活跃任务的 updated_at，**不改状态**。
    ///
    /// 数据面（拉块）不能写状态：一次拉块请求在"状态检查通过"之后才执行到这里，
    /// 这期间用户点了暂停，无条件写 transferring 就把 paused 覆盖掉了。
    /// 真机现象：电脑点暂停 → 任务短暂 paused → 手机又传完整文件 → 变 completed，
    /// 桌面端几秒后跳进「已完成」，手机上还显示在传。
    /// </summary>
    /// <summary>
    /// 刷新活跃任务的时间戳，并把 waiting/preparing 推进到 transferring。
    ///
    /// 返回**更新后的状态**；返回 null 表示这一行已经不在活跃态（期间被暂停/取消/收尾）。
    /// 调用方要用它来广播进度：不能想当然地广播 transferring，否则一条在途的拉块请求
    /// 会把刚点完暂停的界面顶回"传输中"（真机：底部提示已暂停、行内仍写传输中）。
    /// </summary>
    public async Task<string?> TouchActiveAsync(
        string taskId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _factory.Open();
        await using var command = connection.CreateCommand();

        // 只刷时间戳、**不动已暂停/已结束的状态**（2.10.18：拉块会把 paused 覆盖回 transferring）。
        // 但 waiting/preparing 要往前推成 transferring——否则两件事会坏：
        //   1. 超时判定（ListStaleStartedAsync 只扫 transferring）永远不命中，
        //      断网的任务要一直等到 TaskIdleTimeout 才被处理（真机就是"任务卡着不动"）；
        //   2. 界面上传了几百兆还显示"等待中"。
        command.CommandText =
            """
            UPDATE TransferTasks
            SET updated_at_utc = $updated,
                status = CASE WHEN status IN ('waiting', 'preparing') THEN 'transferring' ELSE status END
            WHERE task_id = $id
              AND status IN ('waiting', 'preparing', 'transferring', 'verifying')
            RETURNING status;
            """;
        command.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
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
              AND (hidden_for_device IS NULL OR hidden_for_device <> $device)
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
            WHERE desktop_hidden = 0
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
            HiddenForDevice = GetNullableString(reader, "hidden_for_device"),
            DesktopHidden = reader.GetInt32(reader.GetOrdinal("desktop_hidden")),
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
