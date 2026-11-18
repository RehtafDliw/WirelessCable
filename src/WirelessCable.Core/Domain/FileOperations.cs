namespace WirelessCable.Core.Domain;

/// <summary>
/// 文件系统提供方能力位。位标志，用位与判断某项操作是否被底层提供方支持。
///
/// 阶段 5 的现实是：Windows 本地文件系统几乎全支持；Android SAF 只是"部分支持"，
/// 具体哪些操作可用取决于用户选中的文档提供器（Downloads / 云盘 / 第三方文件管理器），
/// 必须由实现方用 <c>DocumentsContract</c> 的真实标志位探测，不能假设。
/// </summary>
[Flags]
public enum FileProviderCapabilities
{
    /// <summary>什么都不支持。</summary>
    None = 0,

    /// <summary>列目录内容。</summary>
    ListDirectory = 1 << 0,

    /// <summary>读文件内容。</summary>
    ReadFile = 1 << 1,

    /// <summary>写文件内容（含创建与覆盖）。</summary>
    WriteFile = 1 << 2,

    /// <summary>新建目录。</summary>
    CreateDirectory = 1 << 3,

    /// <summary>重命名文件或目录。</summary>
    Rename = 1 << 4,

    /// <summary>删除文件或目录。</summary>
    Delete = 1 << 5,

    /// <summary>移动文件或目录。</summary>
    Move = 1 << 6,

    /// <summary>查询单个条目元信息。</summary>
    GetFileInfo = 1 << 7,

    /// <summary>全部能力。</summary>
    All = ListDirectory | ReadFile | WriteFile | CreateDirectory | Rename | Delete | Move | GetFileInfo,
}

/// <summary>
/// 文件系统操作的稳定结果状态。业务层用它区分"路径非法""没权限""提供方不支持"，
/// 三者必须给出不同原因码——需求明确要求"不支持的文件操作有明确提示"。
/// </summary>
public enum FileOperationStatus
{
    Ok = 0,

    /// <summary>路径不存在。</summary>
    NotFound,

    /// <summary>路径非法（穿越、绝对路径、非法字符等）。</summary>
    PathInvalid,

    /// <summary>目标已存在。</summary>
    AlreadyExists,

    /// <summary>调用方没有该目录或该操作的权限。</summary>
    PermissionDenied,

    /// <summary>底层提供方不支持此操作。</summary>
    Unsupported,

    /// <summary>授权已被撤销（Android SAF 授权失效）。</summary>
    AuthorizationRevoked,

    /// <summary>存储介质不可用（SD 卡拔出等）。</summary>
    StorageUnavailable,

    /// <summary>底层 IO 失败。</summary>
    IoFailure,
}

/// <summary>一次文件操作的结果。失败时 <see cref="Status"/> 与 <see cref="ReasonCode"/> 必填。</summary>
public readonly record struct FileOperationResult
{
    private FileOperationResult(FileOperationStatus status, string reasonCode, string message)
    {
        Status = status;
        ReasonCode = reasonCode;
        Message = message;
    }

    public FileOperationStatus Status { get; }

    /// <summary>稳定英文原因码，可直接回给客户端做分支处理。</summary>
    public string ReasonCode { get; }

    /// <summary>面向用户的中文说明。</summary>
    public string Message { get; }

    public bool Success => Status == FileOperationStatus.Ok;

    public static FileOperationResult Ok()
        => new(FileOperationStatus.Ok, "ok", string.Empty);

    public static FileOperationResult Fail(FileOperationStatus status, string reasonCode, string message)
        => new(status, reasonCode, message);

    public static FileOperationResult NotFound(string message = "文件或目录不存在。")
        => Fail(FileOperationStatus.NotFound, "file_not_found", message);

    public static FileOperationResult PathInvalid(string message = "路径非法。")
        => Fail(FileOperationStatus.PathInvalid, "path_invalid", message);

    public static FileOperationResult AlreadyExists(string message = "目标已存在。")
        => Fail(FileOperationStatus.AlreadyExists, "already_exists", message);

    public static FileOperationResult PermissionDenied(string message = "没有该目录的操作权限。")
        => Fail(FileOperationStatus.PermissionDenied, "permission_denied", message);

    public static FileOperationResult Unsupported(string message = "该存储提供方不支持此操作。")
        => Fail(FileOperationStatus.Unsupported, "operation_unsupported", message);

    public static FileOperationResult AuthorizationRevoked(string message = "该目录的访问授权已被撤销。")
        => Fail(FileOperationStatus.AuthorizationRevoked, "authorization_revoked", message);

    public static FileOperationResult StorageUnavailable(string message = "存储介质不可用。")
        => Fail(FileOperationStatus.StorageUnavailable, "storage_unavailable", message);

    public static FileOperationResult IoFailure(string message = "文件操作失败。")
        => Fail(FileOperationStatus.IoFailure, "io_failure", message);
}

/// <summary>带值的文件操作结果。失败时 <c>Value</c> 为默认值。</summary>
public readonly record struct FileOperationResult<T>
{
    private FileOperationResult(FileOperationStatus status, string reasonCode, string message, T? value)
    {
        Status = status;
        ReasonCode = reasonCode;
        Message = message;
        Value = value;
    }

    public FileOperationStatus Status { get; }

    public string ReasonCode { get; }

    public string Message { get; }

    /// <summary>成功时的值；失败时为默认值。</summary>
    public T? Value { get; }

    public bool Success => Status == FileOperationStatus.Ok;

    public static FileOperationResult<T> Ok(T value)
        => new(FileOperationStatus.Ok, "ok", string.Empty, value);

    public static FileOperationResult<T> Fail(FileOperationStatus status, string reasonCode, string message)
        => new(status, reasonCode, message, default);

    public static FileOperationResult<T> NotFound(string message = "文件或目录不存在。")
        => Fail(FileOperationStatus.NotFound, "file_not_found", message);

    public static FileOperationResult<T> PathInvalid(string message = "路径非法。")
        => Fail(FileOperationStatus.PathInvalid, "path_invalid", message);

    public static FileOperationResult<T> AlreadyExists(string message = "目标已存在。")
        => Fail(FileOperationStatus.AlreadyExists, "already_exists", message);

    public static FileOperationResult<T> PermissionDenied(string message = "没有该目录的操作权限。")
        => Fail(FileOperationStatus.PermissionDenied, "permission_denied", message);

    public static FileOperationResult<T> Unsupported(string message = "该存储提供方不支持此操作。")
        => Fail(FileOperationStatus.Unsupported, "operation_unsupported", message);

    public static FileOperationResult<T> AuthorizationRevoked(string message = "该目录的访问授权已被撤销。")
        => Fail(FileOperationStatus.AuthorizationRevoked, "authorization_revoked", message);

    public static FileOperationResult<T> StorageUnavailable(string message = "存储介质不可用。")
        => Fail(FileOperationStatus.StorageUnavailable, "storage_unavailable", message);

    public static FileOperationResult<T> IoFailure(string message = "文件操作失败。")
        => Fail(FileOperationStatus.IoFailure, "io_failure", message);

    /// <summary>把无值结果转换为带值结果（失败路径复用）。</summary>
    public static FileOperationResult<T> From(FileOperationResult result)
        => Fail(result.Status, result.ReasonCode, result.Message);
}
