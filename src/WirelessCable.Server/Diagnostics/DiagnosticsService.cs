using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WirelessCable.Core.Abstractions;
using WirelessCable.Server.Runtime;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Server.Diagnostics;

/// <summary>
/// 现场快照与诊断包。两件事共用一个口径：
///
/// * <see cref="SnapshotAsync"/> 只读库，返回结构化的状态统计 —— 回归脚本
///   （<c>scripts/smoke-test.py</c>）拿它做断言，诊断包拿它填 <c>state.json</c>。
///   脚本断言的和用户看到的必须是同一份数据，否则"通过"没有意义。
/// * <see cref="BuildBundleAsync"/> 把快照 + 日志 + 请求统计打成 zip。
///
/// 这里**不做任何写操作**：诊断不能改变现场。唯一的例外是调用方显式要求清理，
/// 那由端点自己去做，不放进这个类。
/// </summary>
public sealed partial class DiagnosticsService
{
    /// <summary>最近多少条作业/任务进快照。够看出"卡在什么状态"，又不至于让包变大。</summary>
    private const int RecentLimit = 30;

    /// <summary>每个日志文件最多带多少行。整份日志可能几十 MB，打包没意义。</summary>
    private const int LogTailLines = 1500;

    /// <summary>请求统计保留的原始条数。</summary>
    private const int HttpRecentLimit = 200;

    private readonly IAppPaths _paths;
    private readonly ServerRuntime _runtime;
    private readonly SqliteDatabaseFactory _factory;
    private readonly ServerOptions _options;

    public DiagnosticsService(
        IAppPaths paths,
        ServerRuntime runtime,
        SqliteDatabaseFactory factory,
        ServerOptions options)
    {
        _paths = paths;
        _runtime = runtime;
        _factory = factory;
        _options = options;
    }

    // ================================================================ 快照

    /// <summary>
    /// 读一遍库，返回当前状态。数据库没就绪时返回一个带 <c>error</c> 的最小快照，
    /// 而不是抛异常 —— 连"库打不开"本身都是要排查的现象。
    /// </summary>
    public async Task<StateSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var runtime = _runtime.Snapshot();
        var now = DateTimeOffset.UtcNow;

        var snapshot = new StateSnapshot
        {
            GeneratedAtUtc = now,
            Version = runtime.AppVersion,
            ProtocolVersion = runtime.ProtocolVersion,
            Port = runtime.Port,
            State = runtime.State.ToString(),
            UptimeSeconds = runtime.UptimeSeconds,
            DatabaseReady = runtime.DatabaseReady,
            LastError = runtime.LastError,
            MachineName = _options.DisplayName ?? Environment.MachineName,
            DatabasePath = _paths.DatabasePath,
            LogsDirectory = _paths.LogsDirectory,
            SchemaVersion = 0,
            Integrity = "unknown",
        };

        try
        {
            await using var connection = _factory.Open();
            await FillAsync(connection, snapshot, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            snapshot.Error = error.Message;
        }

        return snapshot;
    }

    private static async Task FillAsync(
        SqliteConnection connection,
        StateSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        snapshot.SchemaVersion = await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken)
            .ConfigureAwait(false);

        // quick_check 只读且很快（这个库通常几 MB）。integrity_check 在几十万行时要几秒，
        // 诊断包偶尔导一次可以接受，但快照会被界面反复拉，所以这里用 quick_check。
        var integrity = await ScalarStringAsync(connection, "PRAGMA quick_check;", cancellationToken)
            .ConfigureAwait(false);
        snapshot.Integrity = integrity ?? "unknown";

        snapshot.JobsByStatus = await MapAsync(
            connection,
            "SELECT status, COUNT(*) FROM FileOperationJobs GROUP BY status;",
            cancellationToken).ConfigureAwait(false);

        snapshot.JobsByOperation = await MapAsync(
            connection,
            "SELECT operation || '/' || status, COUNT(*) FROM FileOperationJobs GROUP BY 1;",
            cancellationToken).ConfigureAwait(false);

        snapshot.TasksByStatus = await MapAsync(
            connection,
            "SELECT direction || '/' || status, COUNT(*) FROM TransferTasks GROUP BY 1;",
            cancellationToken).ConfigureAwait(false);

        snapshot.TableCounts = await MapAsync(
            connection,
            """
            SELECT 'Devices', COUNT(*) FROM Devices
            UNION ALL SELECT 'SharedDirectories', COUNT(*) FROM SharedDirectories
            UNION ALL SELECT 'FileOperationJobs', COUNT(*) FROM FileOperationJobs
            UNION ALL SELECT 'TransferTasks', COUNT(*) FROM TransferTasks
            UNION ALL SELECT 'TransferChunks', COUNT(*) FROM TransferChunks;
            """,
            cancellationToken).ConfigureAwait(false);

        // 租约是这轮新加的控制手段：有租约 = 手机正拿着这个作业在跑。
        // 暂停后这个数必须归零，否则"停不住"就是它没清。
        snapshot.ActiveLeases = await ScalarLongAsync(
            connection,
            $"""
             SELECT COUNT(*) FROM FileOperationJobs
             WHERE status = 'running'
               AND lease_until_utc IS NOT NULL
               AND lease_until_utc > '{now:O}';
             """,
            cancellationToken).ConfigureAwait(false);

        snapshot.RecentJobs = await ReadJobsAsync(connection, cancellationToken).ConfigureAwait(false);
        snapshot.RecentTasks = await ReadTasksAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<JobRow>> ReadJobsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT job_id, device_id, operation, status, destination_path, display_name,
                   error_code, error_message, updated_at_utc, lease_until_utc, batch_name
            FROM FileOperationJobs
            ORDER BY updated_at_utc DESC
            LIMIT 30;
            """;

        var rows = new List<JobRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new JobRow
            {
                JobId = reader.GetString(0),
                DeviceId = reader.GetString(1),
                Operation = reader.GetString(2),
                Status = reader.GetString(3),
                DestinationPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                DisplayName = reader.IsDBNull(5) ? null : reader.GetString(5),
                ErrorCode = reader.IsDBNull(6) ? null : reader.GetString(6),
                ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAtUtc = reader.IsDBNull(8) ? null : reader.GetString(8),
                LeaseUntilUtc = reader.IsDBNull(9) ? null : reader.GetString(9),
                BatchName = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return rows;
    }

    private static async Task<List<TaskRow>> ReadTasksAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT task_id, direction, status, file_name, relative_path,
                   total_bytes, transferred_bytes, error_code, error_message, batch_name
            FROM TransferTasks
            ORDER BY updated_at_utc DESC
            LIMIT 30;
            """;

        var rows = new List<TaskRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TaskRow
            {
                TaskId = reader.GetString(0),
                Direction = reader.GetString(1),
                Status = reader.GetString(2),
                FileName = reader.IsDBNull(3) ? null : reader.GetString(3),
                RelativePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                TotalBytes = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                TransferredBytes = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                ErrorCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                BatchName = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        return rows;
    }

    // ================================================================ 诊断包

    /// <summary>
    /// 打一个 zip。默认脱敏（用户名、机器名、内网 IP、文件名），
    /// <paramref name="includeNames"/> 为真时保留原始文件名 —— 排查"落点目录对不对"
    /// 时确实需要它，但那一步只能由用户自己决定。
    /// </summary>
    public async Task<byte[]> BuildBundleAsync(
        bool includeNames,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var mask = new Masker(includeNames);
        // 日志只读一次：http.json 和 logs/ 用的是同一份内容，
        // 否则两次读之间又写进来几百行，统计和原文对不上
        var logs = RecentLogs().ToList();

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(
                zip,
                "manifest.json",
                JsonSerializer.Serialize(
                    new
                    {
                        generatedAtUtc = snapshot.GeneratedAtUtc,
                        version = snapshot.Version,
                        protocolVersion = snapshot.ProtocolVersion,
                        schemaVersion = snapshot.SchemaVersion,
                        state = snapshot.State,
                        port = snapshot.Port,
                        uptimeSeconds = snapshot.UptimeSeconds,
                        databaseReady = snapshot.DatabaseReady,
                        integrity = snapshot.Integrity,
                        os = Environment.OSVersion.VersionString,
                        runtime = Environment.Version.ToString(),
                        frames = Environment.Is64BitProcess ? "x64" : "x86",
                        masked = !includeNames,
                    },
                    JsonOptions),
                cancellationToken).ConfigureAwait(false);

            // 快照是给人看的：库路径换成占位符，设备名换成编号
            var masked = mask.Snapshot(snapshot);
            await WriteEntryAsync(
                zip,
                "state.json",
                JsonSerializer.Serialize(masked, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            await WriteEntryAsync(
                zip,
                "http.json",
                JsonSerializer.Serialize(ReadHttpStats(logs), JsonOptions),
                cancellationToken).ConfigureAwait(false);

            foreach (var log in logs)
            {
                await WriteEntryAsync(
                    zip,
                    "logs/" + log.Name,
                    mask.Text(log.Content),
                    cancellationToken).ConfigureAwait(false);
            }

            await WriteEntryAsync(zip, "README.txt", Readme(snapshot, includeNames), cancellationToken)
                .ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 必须和 /app/api 的默认一致：否则脚本读端点、人读包，字段名两套（camel vs Pascal），
        // 对不上就会以为是"字段丢了"
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 中文别转成 \uXXXX：用户会打开这个文件看，也方便直接贴进 issue
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task WriteEntryAsync(
        ZipArchive zip,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 最近两个日志文件。文件名带日期，按名排序就是按时间排序。
    ///
    /// **必须用 FileShare.ReadWrite 打开**：Serilog 默认独占写文件，
    /// 直接 <c>File.ReadAllLines</c> 会 IOException，而正在写的那个恰好是最有价值的
    /// （第一次实现就踩了：包里只有前一天的日志，当天的和 http.json 全空）。
    /// 也只保留末尾 N 行，避免把几十 MB 的日志整个读进内存。
    /// </summary>
    private IEnumerable<(string Name, string Content)> RecentLogs()
    {
        if (!Directory.Exists(_paths.LogsDirectory))
        {
            yield break;
        }

        var files = Directory.EnumerateFiles(_paths.LogsDirectory, "*.log")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Take(2);

        foreach (var file in files)
        {
            var tail = new Queue<string>(LogTailLines);
            try
            {
                using var stream = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                while (reader.ReadLine() is { } line)
                {
                    if (tail.Count == LogTailLines)
                    {
                        tail.Dequeue();
                    }

                    tail.Enqueue(line);
                }
            }
            catch (IOException)
            {
                // 连共享读都失败（文件刚被删/权限问题）就跳过，不能让整个包失败
                continue;
            }

            yield return (Path.GetFileName(file), string.Join('\n', tail));
        }
    }

    /// <summary>
    /// 从日志里扒请求统计。日志是唯一记录"哪个端点报了多少错"的地方，
    /// 而 500/409 的分布往往直接指出问题所在。
    /// </summary>
    private static HttpStats ReadHttpStats(IReadOnlyList<(string Name, string Content)> logs)
    {
        var recent = new List<HttpSample>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        // 按时间正序：logs 是"新文件在前"，反转后旧文件先解析，recent 的尾部才是最新
        foreach (var (_, content) in logs.Reverse())
        {
            foreach (var line in content.Split('\n'))
            {
                var match = HttpLine().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var path = NormalizePath(match.Groups[3].Value);
                var status = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                var key = $"{match.Groups[2].Value} {path} -> {status}";
                counts[key] = counts.TryGetValue(key, out var seen) ? seen + 1 : 1;

                recent.Add(new HttpSample
                {
                    At = match.Groups[1].Value,
                    Method = match.Groups[2].Value,
                    Path = path,
                    Status = status,
                    ElapsedMs = double.TryParse(
                        match.Groups[5].Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var ms) ? ms : 0,
                });
            }
        }

        return new HttpStats
        {
            Counts = counts
                .OrderByDescending(pair => pair.Value)
                .Take(40)
                .Select(pair => new HttpCount { Key = pair.Key, Count = pair.Value })
                .ToList(),
            Recent = recent.Count <= HttpRecentLimit ? recent : recent[^HttpRecentLimit..],
        };
    }

    /// <summary>把路径里的 id 抹平成 {id}，否则统计表会被几百个不同 taskId 撑爆。</summary>
    private static string NormalizePath(string path)
    {
        var query = path.IndexOf('?');
        if (query >= 0)
        {
            path = path[..query];
        }

        return IdSegment().Replace(path, "/{id}");
    }

    private string Readme(StateSnapshot snapshot, bool includeNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WirelessCable 诊断包");
        builder.AppendLine("=====================");
        builder.AppendLine();
        builder.AppendLine($"生成时间：{snapshot.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"版本：{snapshot.Version}（协议 {snapshot.ProtocolVersion}，库 schema {snapshot.SchemaVersion}）");
        builder.AppendLine($"状态：{snapshot.State}　运行 {TimeSpan.FromSeconds(snapshot.UptimeSeconds):hh\\:mm\\:ss}");
        builder.AppendLine();
        builder.AppendLine("包含：");
        builder.AppendLine("  manifest.json  版本、系统、端口等基本事实");
        builder.AppendLine("  state.json     设备/作业/任务的当前状态与最近记录");
        builder.AppendLine("  http.json      最近请求的统计（按端点与状态码聚合，外加最近 200 条）");
        builder.AppendLine("  logs/          最近两个日志文件的末尾 1500 行");
        builder.AppendLine();
        builder.AppendLine($"脱敏：{(includeNames ? "否（含原始文件名）" : "是")}");
        if (!includeNames)
        {
            builder.AppendLine("  用户名、机器名替换为占位符，内网 IP 抹掉后两段，文件名替换为 ***.ext");
            builder.AppendLine("  设备名替换为「设备1 / 设备2」；目录名保留（排查落点要靠它）");
        }

        builder.AppendLine("  日志里不含任何传输内容，只有状态与错误码");
        builder.AppendLine();
        builder.AppendLine("分享前请自己过一遍：这个包会暴露你传输过哪些目录、失败过多少次。");
        return builder.ToString();
    }

    // ================================================================ 脱敏

    /// <summary>
    /// 把"能定位到人"的部分换掉，同时留下"能定位到问题"的部分。
    ///
    /// 这条界线是：设备名、机器名、用户名、精确 IP、文件名 → 抹掉；
    /// 状态、错误码、错误文案、目录层级、字节数、时间 → 保留。
    /// 抹掉文件名的代价是看不到具体文件，但目录名还在 —— 排查落点错位够用了。
    /// </summary>
    private sealed class Masker
    {
        private readonly bool _includeNames;
        private readonly string _user = Environment.UserName;
        private readonly string _machine = Environment.MachineName;
        private readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private readonly Dictionary<string, string> _devices = new(StringComparer.Ordinal);

        public Masker(bool includeNames) => _includeNames = includeNames;

        public StateSnapshot Snapshot(StateSnapshot source) => new()
        {
            GeneratedAtUtc = source.GeneratedAtUtc,
            Version = source.Version,
            ProtocolVersion = source.ProtocolVersion,
            Port = source.Port,
            State = source.State,
            UptimeSeconds = source.UptimeSeconds,
            DatabaseReady = source.DatabaseReady,
            LastError = Text(source.LastError),
            MachineName = "<pc>",
            // 路径本身会暴露用户名，但保留结构（盘符 + 目录层级）对排查有用
            DatabasePath = Text(source.DatabasePath),
            LogsDirectory = Text(source.LogsDirectory),
            SchemaVersion = source.SchemaVersion,
            Integrity = source.Integrity,
            Error = Text(source.Error),
            ActiveLeases = source.ActiveLeases,
            JobsByStatus = source.JobsByStatus,
            JobsByOperation = source.JobsByOperation,
            TasksByStatus = source.TasksByStatus,
            TableCounts = source.TableCounts,
            RecentJobs = source.RecentJobs.Select(job => new JobRow
            {
                JobId = Short(job.JobId),
                DeviceId = Device(job.DeviceId),
                Operation = job.Operation,
                Status = job.Status,
                DestinationPath = Path(job.DestinationPath),
                DisplayName = Name(job.DisplayName),
                ErrorCode = job.ErrorCode,
                ErrorMessage = Text(job.ErrorMessage),
                UpdatedAtUtc = job.UpdatedAtUtc,
                LeaseUntilUtc = job.LeaseUntilUtc,
                // 批次名就是文件夹名，和 destination_path 是同一个信息；
                // 抹成 *** 只会让人看不出这是哪一批，安全上没有任何收益
                BatchName = Text(job.BatchName),
            }).ToList(),
            RecentTasks = source.RecentTasks.Select(task => new TaskRow
            {
                TaskId = Short(task.TaskId),
                Direction = task.Direction,
                Status = task.Status,
                FileName = Name(task.FileName),
                RelativePath = Path(task.RelativePath),
                TotalBytes = task.TotalBytes,
                TransferredBytes = task.TransferredBytes,
                ErrorCode = task.ErrorCode,
                ErrorMessage = Text(task.ErrorMessage),
                BatchName = Text(task.BatchName),
            }).ToList(),
        };

        /// <summary>日志整段替换：用户名、机器名、内网 IP。</summary>
        public string Text(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            var text = value;
            if (!_includeNames)
            {
                if (!string.IsNullOrEmpty(_profile))
                {
                    text = text.Replace(_profile, "<home>", StringComparison.OrdinalIgnoreCase);
                }

                if (!string.IsNullOrEmpty(_user))
                {
                    text = text.Replace(_user, "<user>", StringComparison.OrdinalIgnoreCase);
                }

                if (!string.IsNullOrEmpty(_machine))
                {
                    text = text.Replace(_machine, "<pc>", StringComparison.OrdinalIgnoreCase);
                }
            }

            // IP 无论如何都抹后两段：网段对排查够用，完整地址是隐私
            return Ipv4().Replace(text, m => $"{m.Groups[1].Value}.{m.Groups[2].Value}.*.*");
        }

        private string Device(string deviceId)
        {
            if (!_devices.TryGetValue(deviceId, out var label))
            {
                label = $"设备{_devices.Count + 1}";
                _devices[deviceId] = label;
            }

            return label;
        }

        private static string Short(string value) =>
            value.Length <= 8 ? value : value[..8];

        /// <summary>文件名：保留扩展名。用户看到 <c>***.jpg</c> 就知道是照片。</summary>
        private string Name(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            if (_includeNames)
            {
                return Text(value);
            }

            var extension = System.IO.Path.GetExtension(value);
            return string.IsNullOrEmpty(extension) ? "***" : "***" + extension;
        }

        /// <summary>路径：目录名保留，最后一段（文件名）抹掉。</summary>
        private string Path(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            if (_includeNames)
            {
                return Text(value);
            }

            var normalized = value.Replace('\\', '/');
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return value;
            }

            var last = segments[^1];
            // 只有一段时无法区分"目录"和"文件"：含扩展名按文件处理
            if (segments.Length == 1 && System.IO.Path.HasExtension(last))
            {
                segments[^1] = Name(last);
            }
            else if (segments.Length > 1 && System.IO.Path.HasExtension(last))
            {
                segments[^1] = Name(last);
            }

            // 路径本身会暴露用户名（C:/Users/xxx），所以拼回去之后还要过一遍 Text()
            return Text(string.Join('/', segments));
        }
    }

    // ================================================================ SQL 小工具

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // SQLite 的 COUNT 是 64 位，long 才安全
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<Dictionary<string, int>> MapAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.IsDBNull(0) ? "(null)" : reader.GetString(0);
            map[key] = (int)reader.GetInt64(1);
        }

        return map;
    }

    [GeneratedRegex(@"^\[(\S+ \S+) \w+\] HTTP (\w+) (\S+) -> (\d+) in ([\d.]+)ms", RegexOptions.Multiline)]
    private static partial Regex HttpLine();

    [GeneratedRegex(@"(/[0-9a-fA-F]{8,}|/[0-9a-fA-F-]{16,})")]
    private static partial Regex IdSegment();

    [GeneratedRegex(@"\b(\d{1,3})\.(\d{1,3})\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex Ipv4();
}
