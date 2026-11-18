namespace WirelessCable.Storage.Entities;

/// <summary>
/// 共享目录对某设备的授权位。位标志。
///
/// 需求列了六种权限：只读、读写、上传、下载、删除，以及目录本身的新建/重命名/移动。
/// 这里拆成细粒度位，UI 上再组合成"只读""读写"两档快捷方式。
/// </summary>
[Flags]
public enum SharedDirectoryPermissions
{
    /// <summary>无权限。</summary>
    None = 0,

    /// <summary>浏览目录内容与查看文件信息。</summary>
    Browse = 1 << 0,

    /// <summary>把文件从共享目录下载到本机。</summary>
    Download = 1 << 1,

    /// <summary>往共享目录上传文件。</summary>
    Upload = 1 << 2,

    /// <summary>在共享目录里新建子目录。</summary>
    Create = 1 << 3,

    /// <summary>重命名共享目录内的条目。</summary>
    Rename = 1 << 4,

    /// <summary>删除共享目录内的条目。</summary>
    Delete = 1 << 5,

    /// <summary>移动共享目录内的条目。</summary>
    Move = 1 << 6,

    /// <summary>只读：浏览 + 下载。</summary>
    ReadOnly = Browse | Download,

    /// <summary>读写：除删除外全部允许。</summary>
    ReadWrite = Browse | Download | Upload | Create | Rename | Move,

    /// <summary>全部权限。</summary>
    All = Browse | Download | Upload | Create | Rename | Delete | Move,
}

/// <summary>SharedDirectories 表的一行：用户显式共享出去的 Windows 目录。</summary>
public sealed class SharedDirectoryRecord
{
    public required string DirectoryId { get; init; }

    /// <summary>展示名（用户在 UI 上看到的名称）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>绝对路径（服务端本地路径，不下发到客户端）。</summary>
    public required string RootPath { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>SharedDirectoryGrants 表的一行：某设备在某共享目录上的权限位。</summary>
public sealed class SharedDirectoryGrantRecord
{
    public required string DirectoryId { get; init; }

    public required string DeviceId { get; init; }

    public SharedDirectoryPermissions Permissions { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// DeviceFileRoots 表的一行：手机授权给电脑的一个目录根。
/// 根的 URI 只存在手机本地，服务器只存展示名与能力位。
/// </summary>
public sealed class DeviceFileRootRecord
{
    public required string RootId { get; init; }

    public required string DeviceId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>提供方类型：saf / media / other。</summary>
    public string ProviderKind { get; init; } = "saf";

    /// <summary>能力位（与 Core 的 FileProviderCapabilities 对齐）。</summary>
    public int Capabilities { get; init; }

    public required DateTimeOffset GrantedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>DeviceFileEntries 表的一行：手机目录里的一个条目（供电脑浏览）。</summary>
public sealed class DeviceFileEntryRecord
{
    public required string RootId { get; init; }

    /// <summary>相对根的路径，用 '/' 分隔；根目录本身为空串。</summary>
    public required string RelativePath { get; init; }

    public required string DisplayName { get; init; }

    public bool IsDirectory { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset? ModifiedUtc { get; init; }

    /// <summary>该条目所在目录支持的操作能力位。</summary>
    public int Capabilities { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>
/// FileOperationJobs 表的一行：电脑下发给手机执行的文件操作。
///
/// 手机轮询取走作业、执行后回报状态。电脑端据此显示"操作中/已完成/失败"。
/// </summary>
public sealed class FileOperationJobRecord
{
    public required string JobId { get; init; }

    public required string DeviceId { get; init; }

    public required string RootId { get; init; }

    /// <summary>操作类型：create / rename / delete / move / upload / download。</summary>
    public required string Operation { get; init; }

    public string? SourcePath { get; init; }

    public string? DestinationPath { get; init; }

    /// <summary>新建目录或重命名时使用的名字。</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// 作业关联的传输任务 id。
    /// 电脑上传文件到手机目录时：电脑先建一个下载任务（手机去电脑拉），
    /// 再把任务 id 交给手机，手机拉完写进 SAF 目录。
    /// </summary>
    public string? TransferTaskId { get; init; }

    public required string Status { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 作业回传的数据（仅 list 作业使用）：目录条目的 JSON 数组。
    /// 其他操作不回传数据。
    /// </summary>
    public string? ResultPayload { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
