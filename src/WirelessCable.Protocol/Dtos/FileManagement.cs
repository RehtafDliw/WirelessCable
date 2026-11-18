namespace WirelessCable.Protocol.Dtos;

/// <summary>文件系统根的类型。字符串值两端一致。</summary>
public static class FileRootKinds
{
    /// <summary>Windows 共享目录（电脑本地路径，只在服务端存在）。</summary>
    public const string Shared = "shared";

    /// <summary>手机授权目录（SAF 树，手机持有 URI 授权）。</summary>
    public const string Device = "device";

    /// <summary>
    /// 整机文件系统（"此电脑"）。手机可浏览电脑全部盘符与文件，等同资源管理器里的"此电脑"。
    /// 只读：写操作仍限定在共享目录授权内。
    /// </summary>
    public const string Machine = "machine";
}

/// <summary>整机根的固定 id。整机只有一个，因此不需要按设备区分。</summary>
public static class FileRootIds
{
    /// <summary>整机根（此电脑）。</summary>
    public const string Machine = "machine";
}

/// <summary>文件操作类型。两端一致。</summary>
public static class FileOperations
{
    public const string Create = "create";
    public const string Rename = "rename";
    public const string Delete = "delete";
    public const string Move = "move";

    /// <summary>复制：源不动，目标必须是新路径（不覆盖）。目录按递归处理。</summary>
    public const string Copy = "copy";

    public const string Upload = "upload";
    public const string Download = "download";

    /// <summary>
    /// 列目录（1.3.0 新增）。
    ///
    /// 索引快照有深度与条数上限（整机 4 层 / 2 万条），超出范围的历史目录在电脑端是空的。
    /// 这条作业让电脑在**进入一个未索引的目录时**现场问一次手机，把结果挂在作业结果里回传。
    /// 它不是高频路径：只有快照拿不到内容时才会下发。
    /// </summary>
    public const string List = "list";
}

/// <summary>作业状态。字符串值两端一致。</summary>
public static class FileJobStatuses
{
    /// <summary>已下发，等待手机执行。</summary>
    public const string Pending = "pending";

    /// <summary>手机已开始执行。</summary>
    public const string Running = "running";

    /// <summary>执行成功。</summary>
    public const string Completed = "completed";

    /// <summary>执行失败，原因在 errorCode / errorMessage。</summary>
    public const string Failed = "failed";
}

/// <summary>能力位。数值与 Core 的 FileProviderCapabilities 一致。</summary>
public static class FileCapabilities
{
    public const int None = 0;
    public const int ListDirectory = 1 << 0;
    public const int ReadFile = 1 << 1;
    public const int WriteFile = 1 << 2;
    public const int CreateDirectory = 1 << 3;
    public const int Rename = 1 << 4;
    public const int Delete = 1 << 5;
    public const int Move = 1 << 6;
    public const int GetFileInfo = 1 << 7;
    public const int All = ListDirectory | ReadFile | WriteFile | CreateDirectory | Rename | Delete | Move | GetFileInfo;
}

/// <summary>
/// 一个可浏览的根。手机看到的是 id + 名字 + 能力，
/// 绝不能看到电脑的绝对路径——那是服务端内部信息。
/// </summary>
public sealed record FileRootDto
{
    /// <summary>根标识：共享目录 id 或手机目录 rootId。</summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary><see cref="FileRootKinds"/> 之一。</summary>
    public string Kind { get; set; } = FileRootKinds.Shared;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>所属设备 id；共享目录的拥有方是电脑本身。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>该根支持的操作能力位。</summary>
    public int Capabilities { get; set; }

    /// <summary>当前设备在该根上的权限位（共享目录用；手机目录恒为 0）。</summary>
    public int Permissions { get; set; }
}

/// <summary>目录条目。字段名与 Android 端 FileEntryDto 一致。</summary>
public sealed record FileEntryDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>相对根的路径，用 '/' 分隔。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>最后修改时间（Unix 毫秒）；未知为 0。</summary>
    public long ModifiedUnixMs { get; set; }

    /// <summary>
    /// 该条目所在目录支持的操作能力位。
    /// 根的能力不代表子目录——SAF 的虚拟目录不支持新建，但里面的真实目录支持。
    /// </summary>
    public int Capabilities { get; set; }
}

/// <summary>目录列表响应。</summary>
public sealed record FileListingDto
{
    public string RootId { get; set; } = string.Empty;

    /// <summary>本次列出的父目录相对路径；空串表示根。</summary>
    public string Path { get; set; } = string.Empty;

    public IReadOnlyList<FileEntryDto> Entries { get; set; } = Array.Empty<FileEntryDto>();

    /// <summary>本根可用能力位。</summary>
    public int Capabilities { get; set; }
}

/// <summary>新建目录请求。</summary>
public sealed class FileCreateDirectoryRequestDto
{
    public string RootId { get; set; } = string.Empty;

    /// <summary>新目录的父目录相对路径；空串表示根。</summary>
    public string ParentPath { get; set; } = string.Empty;

    /// <summary>新目录名（不含路径分隔符）。</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>重命名请求。</summary>
public sealed class FileRenameRequestDto
{
    public string RootId { get; set; } = string.Empty;

    /// <summary>目标条目的相对路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>新名称（不含路径分隔符）。</summary>
    public string NewName { get; set; } = string.Empty;
}

/// <summary>删除请求。</summary>
public sealed class FileDeleteRequestDto
{
    public string RootId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>是否递归删除非空目录。</summary>
    public bool Recursive { get; set; }
}

/// <summary>移动请求。</summary>
public sealed class FileMoveRequestDto
{
    public string RootId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    /// <summary>目标路径（含目标文件名）。</summary>
    public string DestinationPath { get; set; } = string.Empty;
}

/// <summary>复制请求。语义与移动一致，只是源不动。</summary>
public sealed class FileCopyRequestDto
{
    public string RootId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    /// <summary>目标路径（含目标文件名，不能只是目录）。</summary>
    public string DestinationPath { get; set; } = string.Empty;
}

/// <summary>批量操作请求。多选批量删除/批量传输共用。</summary>
public sealed class FileBatchRequestDto
{
    public string RootId { get; set; } = string.Empty;

    public IReadOnlyList<string> Paths { get; set; } = Array.Empty<string>();

    public bool Recursive { get; set; }
}

/// <summary>批量操作结果，逐条给出成败——批量操作不能一错全废。</summary>
public sealed record FileBatchResultDto
{
    public string Path { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>失败原因码；成功为空。</summary>
    public string? ErrorCode { get; set; }

    /// <summary>失败原因说明；成功为空。</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>手机授权目录的上行索引。</summary>
public sealed class DeviceFileIndexDto
{
    public string RootId { get; set; } = string.Empty;

    /// <summary>根的展示名（如"内部存储 / Download"）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>提供方类型：saf / media。</summary>
    public string ProviderKind { get; set; } = "saf";

    /// <summary>根的能力位。</summary>
    public int Capabilities { get; set; }

    /// <summary>根下全部条目（含各级子目录），扁平列表。</summary>
    public IReadOnlyList<FileEntryDto> Entries { get; set; } = Array.Empty<FileEntryDto>();

    /// <summary>
    /// 手机上当前仍然有效的**全部**根 id（含本条根自己）。
    ///
    /// 索引是快照语义：不在这份名单里的根视为已撤销，服务端会连带删掉它的条目。
    /// 老版本 App 不传这个字段（空列表），此时服务端不做清理，保持向后兼容。
    /// </summary>
    public IReadOnlyList<string> ActiveRootIds { get; set; } = Array.Empty<string>();
}

/// <summary>只同步根的集合，不上报条目。授权被全部撤销时用它清空服务端快照。</summary>
public sealed class DeviceFileSyncDto
{
    /// <summary>仍然有效的根 id；空列表表示该设备当前没有任何授权目录。</summary>
    public IReadOnlyList<string> ActiveRootIds { get; set; } = Array.Empty<string>();
}

/// <summary>根同步结果。</summary>
public sealed record FileSyncResultDto
{
    /// <summary>被剔除的根数量。</summary>
    public int RemovedRoots { get; set; }
}

/// <summary>电脑下发给手机执行的作业。</summary>
public sealed record FileJobDto
{
    public string JobId { get; set; } = string.Empty;

    /// <summary>手机侧的根 id。</summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary><see cref="FileOperations"/> 之一。</summary>
    public string Operation { get; set; } = string.Empty;

    public string? SourcePath { get; set; }

    public string? DestinationPath { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// 关联的传输任务 id。
    /// 电脑→手机时是下载任务（手机从电脑拉块）；手机→电脑时是上传任务（手机推给电脑）。
    /// </summary>
    public string? TransferTaskId { get; set; }

    /// <summary>
    /// 电脑侧目标共享目录 id。
    /// 手机→电脑时指定文件落到电脑的哪个共享目录；电脑→手机时为空。
    /// </summary>
    public string? DestinationDirectoryId { get; set; }

    /// <summary><see cref="FileJobStatuses"/> 之一。</summary>
    public string Status { get; set; } = FileJobStatuses.Pending;

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 作业回传的数据（仅 <see cref="FileOperations.List"/> 使用）。
    /// 内容是 <c>FileEntryDto[]</c> 的 JSON；失败时为空。
    /// </summary>
    public string? Payload { get; set; }
}

/// <summary>电脑下发文件作业的请求。</summary>
public sealed class FileJobRequestDto
{
    /// <summary>目标根 id（手机侧授权目录）。</summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary><see cref="FileOperations"/> 之一。</summary>
    public string Operation { get; set; } = string.Empty;

    public string? SourcePath { get; set; }

    public string? DestinationPath { get; set; }

    /// <summary>新建目录或重命名时使用的名字。</summary>
    public string? DisplayName { get; set; }
}

/// <summary>手机回报作业执行结果。</summary>
public sealed class FileJobResultDto
{
    public string JobId { get; set; } = string.Empty;

    /// <summary><see cref="FileJobStatuses"/> 之一。</summary>
    public string Status { get; set; } = FileJobStatuses.Completed;

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 作业结果携带的数据。只有 <see cref="FileOperations.List"/> 会填：
    /// 内容是 <c>FileEntryDto[]</c> 的 JSON（手机序列化，服务端原样存）。
    /// </summary>
    public string? Payload { get; set; }
}

/// <summary>
/// 从某个根下载文件到手机的请求。
/// 手机在浏览器里点一个文件就发这个，服务端按 rootId + path 定位源文件，
/// 客户端**不能**直接传绝对路径——那会让手机可以指定任意文件。
/// </summary>
public sealed class FileDownloadRequestDto
{
    /// <summary>根 id：共享目录 id，或 <see cref="FileRootIds.Machine"/>。</summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary>相对根的路径。</summary>
    public string Path { get; set; } = string.Empty;
}
