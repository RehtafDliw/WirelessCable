namespace WirelessCable.Server.Diagnostics;

/// <summary>
/// 一份现场快照。字段刻意保持"能直接看懂"的粒度：
/// 状态分布用字典、最近记录用列表，脚本读它比读 SQL 稳，人读它能直接看出卡在哪。
///
/// 这是诊断包和回归脚本的公共契约 —— 改字段名等于改脚本，两边必须一起动。
/// </summary>
public sealed class StateSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string Version { get; set; } = string.Empty;
    public int ProtocolVersion { get; set; }
    public int? Port { get; set; }
    public string State { get; set; } = string.Empty;
    public long UptimeSeconds { get; set; }
    public bool DatabaseReady { get; set; }
    public string? LastError { get; set; }

    /// <summary>本机名。诊断包里会被替换成 &lt;pc&gt;。</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>
    /// 库文件的真实路径。只出现在快照里（本机端点，loopback + 令牌），
    /// 回归脚本靠它定位数据库；诊断包里按脱敏规则处理。
    /// </summary>
    public string DatabasePath { get; set; } = string.Empty;

    public string LogsDirectory { get; set; } = string.Empty;

    /// <summary>SQLite <c>user_version</c>。升级后这里没变，说明迁移根本没跑。</summary>
    public long SchemaVersion { get; set; }

    /// <summary><c>quick_check</c> 的结果，正常是 "ok"。</summary>
    public string Integrity { get; set; } = "unknown";

    /// <summary>快照本身失败时的原因（库打不开、表不存在），不抛异常。</summary>
    public string? Error { get; set; }

    /// <summary>正在被手机执行、且租约还没过期的作业数。暂停后必须归零。</summary>
    public long ActiveLeases { get; set; }

    /// <summary>作业状态分布，键是 status。</summary>
    public Dictionary<string, int> JobsByStatus { get; set; } = new();

    /// <summary>作业操作/状态分布，键形如 <c>download/paused</c>。</summary>
    public Dictionary<string, int> JobsByOperation { get; set; } = new();

    /// <summary>任务方向/状态分布，键形如 <c>upload/completed</c>。</summary>
    public Dictionary<string, int> TasksByStatus { get; set; } = new();

    /// <summary>各表行数，用来看"记录是不是被清空了"。</summary>
    public Dictionary<string, int> TableCounts { get; set; } = new();

    public List<JobRow> RecentJobs { get; set; } = new();
    public List<TaskRow> RecentTasks { get; set; } = new();
}

public sealed class JobRow
{
    public string JobId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? DestinationPath { get; set; }
    public string? DisplayName { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? UpdatedAtUtc { get; set; }

    /// <summary>租约到期时间。暂停后这里应该是 null —— 不清就是"停不住"的根。</summary>
    public string? LeaseUntilUtc { get; set; }

    public string? BatchName { get; set; }
}

public sealed class TaskRow
{
    public string TaskId { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? RelativePath { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BatchName { get; set; }
}

public sealed class HttpStats
{
    /// <summary>按「方法 路径 -&gt; 状态码」聚合，取前 40 条。看错误分布用。</summary>
    public List<HttpCount> Counts { get; set; } = new();

    /// <summary>最近 200 条原始样本，按时间正序。</summary>
    public List<HttpSample> Recent { get; set; } = new();
}

public sealed class HttpCount
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class HttpSample
{
    public string At { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Status { get; set; }
    public double ElapsedMs { get; set; }
}
