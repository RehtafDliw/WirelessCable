namespace WirelessCable.Protocol.Dtos;

/// <summary>任务方向。字符串值与数据库、Android 端完全一致。</summary>
public static class TransferDirections
{
    public const string Upload = "upload";
    public const string Download = "download";
}

/// <summary>
/// 下载任务的去向。决定手机要不要自动接收。
/// <para>
/// 同为 download，两件事的落点完全不同：
/// 「发送到手机」落到固定的收件目录（下载/WirelessCable），手机可以无人值守地收；
/// 「复制到手机目录」的落点在某个 SAF 授权目录里，必须由作业带着目标 URI 去拉。
/// 手机端若靠"任务列表里出现了 download"来猜，就会把后者抢先拉进收件箱，
/// 作业再执行时目标已存在、任务已完成——文件落在错的地方（1.5.6 修）。
/// </para>
/// </summary>
public static class TransferPurposes
{
    /// <summary>发到手机收件目录，可由手机自动接收。</summary>
    public const string Inbox = "inbox";

    /// <summary>写到手机某个授权目录的指定文件，只能由作业执行。</summary>
    public const string Directory = "directory";
}

/// <summary>
/// 任务状态。与 <c>WirelessCable.Core.Domain.TransferState</c> 对应，字符串直接入库与上行。
/// 顺序即状态机的推进顺序；终态不可再变。
/// </summary>
public static class TransferStates
{
    public const string Waiting = "waiting";
    public const string Preparing = "preparing";
    public const string Transferring = "transferring";
    public const string Verifying = "verifying";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>阶段 4 预留：暂停 / 网络中断等待恢复。</summary>
    public const string Paused = "paused";
    public const string Interrupted = "interrupted";

    public static bool IsTerminal(string state)
        => state is Completed or Failed or Cancelled;

    public static bool IsActive(string state)
        => state is Waiting or Preparing or Transferring or Verifying;

    /// <summary>可续传：中断后数据保留，可凭服务端状态继续。</summary>
    public static bool IsResumable(string state)
        => IsActive(state) || state is Interrupted or Paused;
}

/// <summary>分块状态。与 TransferChunks 表、Android 端一致。</summary>
public static class ChunkStates
{
    public const string Pending = "pending";
    public const string Completed = "completed";
}

/// <summary>上传落盘冲突策略。默认 rename：绝不静默覆盖。</summary>
public static class TransferConflictPolicies
{
    /// <summary>自动改名 name (1).ext（默认）。</summary>
    public const string Rename = "rename";

    /// <summary>调用方显式要求覆盖已有文件。</summary>
    public const string Overwrite = "overwrite";

    /// <summary>目标已存在则拒绝。</summary>
    public const string Skip = "skip";

    public static bool IsValid(string? value)
        => value is null or Rename or Overwrite or Skip;
}

/// <summary>
/// 统一分块模型。每一行对应 TransferChunks 表的一条记录，也是 GET chunks 的响应元素。
/// </summary>
public sealed record ChunkInfoDto
{
    public string TransferId { get; set; } = string.Empty;

    public int Index { get; set; }

    /// <summary>在文件内的绝对偏移（= Index × ChunkSize）。</summary>
    public long Offset { get; set; }

    /// <summary>本块字节数；最后一块可小于 ChunkSize。</summary>
    public int Length { get; set; }

    /// <summary>本块内容的 SHA-256（小写十六进制）。上传由客户端提供，下载由服务端惰性计算。</summary>
    public string? Sha256 { get; set; }

    /// <summary><see cref="ChunkStates"/> 之一。</summary>
    public string Status { get; set; } = ChunkStates.Pending;
}

/// <summary>创建上传任务。只允许上传：下载任务必须由电脑端在本机创建。</summary>
public sealed class TransferStartRequestDto
{
    /// <summary>客户端自用标识，服务端原样忽略。</summary>
    public string? ClientRequestId { get; set; }

    /// <summary>仅文件名，不允许含路径分隔符。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>共享目录下的子目录（相对路径），缺省为根。</summary>
    public string? RelativeDirectory { get; set; }

    /// <summary>文件总字节数；0 表示空文件。</summary>
    public long TotalBytes { get; set; }

    /// <summary>上传前声明的整文件 SHA-256（小写十六进制）。</summary>
    public string? Sha256 { get; set; }

    /// <summary>期望分块大小，服务端夹到 [64 KiB, 8 MiB]。</summary>
    public int? RequestedChunkSize { get; set; }

    /// <summary>落盘冲突策略，见 <see cref="TransferConflictPolicies"/>；缺省 rename。</summary>
    public string? Conflict { get; set; }

    /// <summary>
    /// 目标共享目录 id（阶段 5）。缺省为默认共享目录。
    /// 服务端会校验该设备在此目录上的 Upload 权限，无权限直接拒绝。
    /// </summary>
    public string? DirectoryId { get; set; }
}

/// <summary>任务快照。列表、状态查询、创建响应共用。</summary>
public sealed record TransferTaskDto
{
    public string TaskId { get; set; } = string.Empty;

    /// <summary><see cref="TransferDirections"/> 之一。</summary>
    public string Direction { get; set; } = TransferDirections.Upload;

    /// <summary>
    /// 下载任务的去向（<see cref="TransferPurposes"/>）。上传任务固定为 inbox。
    /// 手机端只对 <see cref="TransferPurposes.Inbox"/> 做自动接收。
    /// </summary>
    public string Purpose { get; set; } = TransferPurposes.Inbox;

    /// <summary>任务所属设备显示名。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>最终文件名（重名时已改名）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>共享目录内相对路径，'/' 分隔。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// 整文件夹传输的分组键：同一批里的任务共用一个值，界面据此收成一行。
    /// 单文件任务为空（老客户端会忽略这两个字段，行为不变）。
    /// </summary>
    public string? BatchId { get; set; }

    /// <summary>批次对应的文件夹名（收成一行时显示这个名字）。</summary>
    public string? BatchName { get; set; }

    /// <summary>
    /// 电脑上的绝对路径（上传=已落盘的文件，下载=被发送的原文件）。
    /// 只有电脑端有意义，手机端忽略；不是终态时为空。
    /// </summary>
    public string? LocalPath { get; set; }

    public long TotalBytes { get; set; }

    public long TransferredBytes { get; set; }

    public int ChunkSize { get; set; }

    public int ChunkCount { get; set; }

    /// <summary><see cref="TransferStates"/> 之一。</summary>
    public string State { get; set; } = TransferStates.Waiting;

    public string? Sha256 { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>已完成分块序号（升序）。续传时客户端只补差集。</summary>
    public int[] CompletedChunks { get; set; } = [];

    public long CreatedAtUnixMs { get; set; }

    public long UpdatedAtUnixMs { get; set; }
}

/// <summary>
/// 一批任务上的同一个动作。
///
/// 整文件夹传输在列表里收成一行，行上的暂停/继续/取消/重试要一次作用到整批：
/// 200 个文件逐个发请求就是 200 次往返（手机上尤其慢），也容易只成功一半。
/// </summary>
public sealed class TransferBatchActionRequestDto
{
    /// <summary>批内所有任务 id。</summary>
    public List<string> TaskIds { get; set; } = [];

    /// <summary>`pause` / `resume` / `cancel` / `restart`，大小写不敏感。</summary>
    public string Action { get; set; } = string.Empty;
}

/// <summary>
/// 批量动作结果。
///
/// 已经是目标状态的任务按成功计（引擎里是幂等的），所以 `Failed` 通常为 0；
/// 只有在真有任务处于不允许的状态时才会大于 0，此时 `FirstError` 给出原因。
/// </summary>
public sealed class TransferBatchActionResultDto
{
    public int Ok { get; set; }

    public int Failed { get; set; }

    public string? FirstError { get; set; }

    /// <summary>动作之后的任务列表：客户端据此就地刷新，不必再拉一次。</summary>
    public List<TransferTaskDto> Tasks { get; set; } = [];
}

/// <summary>删除传输记录的请求。两端「清理列表」共用。</summary>
public sealed class TransferDeleteRecordsRequestDto
{
    public List<string> TaskIds { get; set; } = [];

    /// <summary>
    /// 是否同时删除电脑上的文件。
    /// 只会删**手机上传过来的**文件，且必须落在共享目录里；
    /// 电脑发出去的原文件属于用户数据，任何情况下都不删。
    /// </summary>
    public bool DeleteFiles { get; set; }
}

/// <summary>删除结果：删了几条记录、几个文件，以及没能删的原因。</summary>
public sealed class TransferDeleteRecordsResultDto
{
    public int Deleted { get; set; }

    public int FilesDeleted { get; set; }

    /// <summary>被拒绝的条目，形如「文件名：原因」。</summary>
    public List<string> Rejected { get; set; } = [];
}

/// <summary>分块写入成功的回执。</summary>
public sealed class ChunkAckDto
{
    public string TaskId { get; set; } = string.Empty;

    public int Index { get; set; }

    public int BytesThisChunk { get; set; }

    public long TransferredBytes { get; set; }

    public long TotalBytes { get; set; }

    /// <summary>最小的未完成分块序号；等于 <see cref="TransferTaskDto.ChunkCount"/> 表示可以 complete。</summary>
    public int NextIndex { get; set; }

    /// <summary>true 表示这块此前已完成，本次是幂等重发，服务端没有重复写入。</summary>
    public bool AlreadyCompleted { get; set; }
}

/// <summary>下载分块回执：客户端确认某块已落盘并附本地算出的哈希。</summary>
public sealed class DownloadChunkAckRequestDto
{
    /// <summary>客户端对接收到的该块内容算出的 SHA-256（小写十六进制）。</summary>
    public string? Sha256 { get; set; }
}

/// <summary>提交校验。</summary>
public sealed class TransferCompleteRequestDto
{
    /// <summary>整文件 SHA-256（小写十六进制）。上传必填；下载回执同样用它。</summary>
    public string? Sha256 { get; set; }
}

/// <summary>任务终态回执。</summary>
public sealed class TransferCompleteResponseDto
{
    public string TaskId { get; set; } = string.Empty;

    public string State { get; set; } = TransferStates.Completed;

    public string FileName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    /// <summary>任务从创建到完成的耗时。</summary>
    public long DurationMs { get; set; }
}
