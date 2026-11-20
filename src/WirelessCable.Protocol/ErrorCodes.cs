namespace WirelessCable.Protocol;

/// <summary>
/// 统一错误码。值是稳定的 snake_case 字符串，直接出现在 ErrorResponse.code 中。
/// 新增错误码必须同步 docs/protocol.md 与 Android 端 ErrorCodes.kt。
/// </summary>
public static class ErrorCodes
{
    /// <summary>请求参数非法（缺字段、类型不符、越界）。</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>未认证：缺少或无效的设备凭据。</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>已认证但无该目录/文件权限。</summary>
    public const string Forbidden = "forbidden";

    /// <summary>资源不存在。</summary>
    public const string NotFound = "not_found";

    /// <summary>路径非法（穿越、绝对路径、ADS、保留名等）。</summary>
    public const string PathForbidden = "path_forbidden";

    /// <summary>状态冲突（重复任务、任务已结束等）。</summary>
    public const string Conflict = "conflict";

    /// <summary>请求过于频繁。</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>协议版本不受支持。</summary>
    public const string ProtocolVersionUnsupported = "protocol_version_unsupported";

    /// <summary>请求体过大。</summary>
    public const string PayloadTooLarge = "payload_too_large";

    /// <summary>文件哈希与声明不一致。</summary>
    public const string ChecksumMismatch = "checksum_mismatch";

    /// <summary>服务暂不可用（启动中、停止中、端口不可用）。</summary>
    public const string ServiceUnavailable = "service_unavailable";

    /// <summary>配对请求不存在。</summary>
    public const string PairingNotFound = "pairing_not_found";

    /// <summary>配对请求已过期。</summary>
    public const string PairingExpired = "pairing_expired";

    /// <summary>配对码错误。</summary>
    public const string PairingCodeInvalid = "pairing_code_invalid";

    /// <summary>本机用户尚未批准该请求。</summary>
    public const string PairingNotApproved = "pairing_not_approved";

    /// <summary>尝试次数超限，请求已锁定。</summary>
    public const string PairingAttemptsExceeded = "pairing_attempts_exceeded";

    /// <summary>该请求已完成配对。</summary>
    public const string PairingAlreadyCompleted = "pairing_already_completed";

    /// <summary>二维码一次性随机数无效或已被使用。</summary>
    public const string QrNonceInvalid = "qr_nonce_invalid";

    /// <summary>设备已被撤销授权。</summary>
    public const string DeviceRevoked = "device_revoked";

    /// <summary>
    /// 这台电脑主动断开了该设备（配对与授权都还在）。
    ///
    /// 手机收到后应停止重试并提示"在电脑上恢复连接"，而不是当成授权失效去引导重新配对。
    /// </summary>
    public const string SessionPaused = "session_paused";

    /// <summary>设备不存在或未配对。</summary>
    public const string DeviceNotFound = "device_not_found";

    /// <summary>签名校验失败。</summary>
    public const string SignatureInvalid = "signature_invalid";

    // ---- 阶段 3：文件传输 ----

    /// <summary>文件名非法：含路径分隔符、保留设备名、控制字符或超长。</summary>
    public const string InvalidFileName = "invalid_file_name";

    /// <summary>传输任务不存在，或不属于当前设备。</summary>
    public const string TransferNotFound = "transfer_not_found";

    /// <summary>当前任务状态不接受该操作（已终态、字节未收齐、源文件变化）。</summary>
    public const string TransferStateInvalid = "transfer_state_invalid";

    /// <summary>
    /// 任务被暂停，数据面（拉块/推块/确认完成）暂时不能继续。
    ///
    /// 单独给一个码，客户端才能区分"暂停"和"真的出错"：
    /// 以前统一是 transfer_state_invalid，手机端会把文件删掉并标失败
    /// （暂停一下，已经传完的文件反而没了）。
    /// </summary>
    public const string TransferPaused = "transfer_paused";

    /// <summary>
    /// 任务已被另一端取消（对端删记录时会先暂停、再取消）。
    ///
    /// 和 <see cref="TransferStateInvalid"/> 分开的原因：对面删了这条正在传的任务之后，
    /// 本端点「继续」只能得到一个"没效果的成功提示"，用户完全不知道发生了什么。
    /// 有这个码，两端都能提示"传输已被接收方取消，是否删除记录"。
    /// </summary>
    public const string TransferCancelled = "transfer_cancelled";

    /// <summary>分块序号与期望不一致。</summary>
    public const string ChunkOffsetMismatch = "chunk_offset_mismatch";

    /// <summary>分块内容哈希与声明不一致（数据在链路上损坏）。</summary>
    public const string ChunkChecksumMismatch = "chunk_checksum_mismatch";

    /// <summary>同一任务已有分块正在写入。</summary>
    public const string ChunkInFlight = "chunk_in_flight";

    /// <summary>该设备非终态任务数已达上限。</summary>
    public const string TooManyActiveTransfers = "too_many_active_transfers";

    /// <summary>目标磁盘空间不足。</summary>
    public const string InsufficientStorage = "insufficient_storage";

    /// <summary>任务空闲超时被回收。</summary>
    public const string TransferTimeout = "transfer_timeout";

    /// <summary>作业被手机取走后一直没有回报（多半是手机被回收/进程被杀）。</summary>
    public const string JobTimeout = "job_timeout";

    /// <summary>源文件不可读（被删除、权限不足、SAF 授权失效）。</summary>
    public const string FileInaccessible = "file_inaccessible";

    // ---- 阶段 5：文件管理 ----

    /// <summary>该目录未授权给当前设备。</summary>
    public const string DirectoryNotAuthorized = "directory_not_authorized";

    /// <summary>当前设备对该目录没有此操作权限。</summary>
    public const string OperationNotPermitted = "operation_not_permitted";

    /// <summary>底层存储提供方不支持此操作（如 SAF 提供器不支持重命名）。</summary>
    public const string OperationUnsupported = "operation_unsupported";

    /// <summary>手机对该目录的访问授权已被撤销。</summary>
    public const string AuthorizationRevoked = "authorization_revoked";

    /// <summary>存储介质不可用（SD 卡拔出、卷脱机）。</summary>
    public const string StorageUnavailable = "storage_unavailable";

    /// <summary>目标已存在，且调用方未显式要求覆盖。</summary>
    public const string TargetAlreadyExists = "target_already_exists";

    /// <summary>手机授权目录的索引为空或已过期。</summary>
    public const string FileIndexUnavailable = "file_index_unavailable";

    /// <summary>服务端内部错误，详情只进日志。</summary>
    public const string InternalError = "internal_error";

    /// <summary>HTTP 状态码到默认错误码的映射。</summary>
    public static string ForStatusCode(int statusCode) => statusCode switch
    {
        400 => InvalidRequest,
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        405 => InvalidRequest,
        409 => Conflict,
        413 => PayloadTooLarge,
        422 => InvalidRequest,
        426 => ProtocolVersionUnsupported,
        429 => RateLimited,
        503 => ServiceUnavailable,
        >= 500 => InternalError,
        _ => InvalidRequest,
    };
}
