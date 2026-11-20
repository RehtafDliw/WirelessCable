namespace WirelessCable.Storage.Entities;

/// <summary>
/// TransferTasks 表的一行。阶段 3 起由 <c>TransferEngine</c> 写入。
/// <para>
/// <see cref="TempPath"/> 只在上传任务存在期间有值；<see cref="LocalPath"/> 是任务完成后的最终绝对路径
/// （上传为共享目录里的落点，下载为电脑上被发送的原文件）。
/// </para>
/// </summary>
public sealed class TransferTaskRecord
{
    public required string TaskId { get; init; }

    /// <summary>upload / download。</summary>
    public required string Direction { get; init; }

    /// <summary>
    /// 下载去向：inbox（手机收件目录，可自动接收）/ directory（手机授权目录，由作业落地）。
    /// 旧库没有这一列时为 null，按 inbox 处理（那时的下载只有"发到手机"一种）。
    /// </summary>
    public string? Purpose { get; init; }

    public required string DeviceId { get; init; }

    /// <summary>最终文件名（重名时已改名）。</summary>
    public required string FileName { get; init; }

    /// <summary>共享目录内相对路径，'/' 分隔。</summary>
    public required string RelativePath { get; init; }

    public long TotalBytes { get; init; }

    public long TransferredBytes { get; init; }

    public int ChunkSize { get; init; }

    public int ChunkCount { get; init; }

    /// <summary>waiting / preparing / transferring / verifying / completed / failed / cancelled。</summary>
    public required string Status { get; init; }

    public string? Sha256 { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public string? TempPath { get; init; }

    public string? LocalPath { get; init; }

    /// <summary>已完成分块数（冗余列，与 TransferChunks 中 completed 行数一致）。</summary>
    public int CompletedChunks { get; init; }

    /// <summary>落盘冲突策略：rename / overwrite / skip（仅上传有意义）。</summary>
    public string? Conflict { get; init; }

    /// <summary>源设备标识（上传=手机 device_id，下载=desktop）。</summary>
    public string? SourceDevice { get; init; }

    /// <summary>目标设备标识（上传=desktop，下载=手机 device_id）。</summary>
    public string? TargetDevice { get; init; }

    /// <summary>
    /// 目标/来源共享目录 id（阶段 5）。空表示默认共享目录。
    /// 上传时是落点目录；下载时是源文件所在目录。
    /// </summary>
    public string? DirectoryId { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// 文件夹批量传输的分组键：一次"下载整个文件夹"里的所有文件共用同一个值。
    /// 界面据此把 N 个任务收成一行，不然传一个 200 张图的文件夹会刷满整屏。
    /// </summary>
    public string? BatchId { get; init; }

    /// <summary>批次对应的文件夹名（显示用）。</summary>
    public string? BatchName { get; init; }

    /// <summary>
    /// 这条记录对**哪台手机**隐藏（deviceId）；null 表示手机端还看得见。
    /// 两端各删各的，桌面端的标记是 <see cref="DesktopHidden"/>，互不影响。
    /// </summary>
    public string? HiddenForDevice { get; init; }

    /// <summary>桌面端删过这条记录（1 = 桌面列表不再显示）。不影响任何一台手机的视图。</summary>
    public int DesktopHidden { get; init; }
}
