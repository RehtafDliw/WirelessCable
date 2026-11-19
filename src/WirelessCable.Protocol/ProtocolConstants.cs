namespace WirelessCable.Protocol;

/// <summary>协议级常量。任何端口都不得各自定义这些值。</summary>
public static class ProtocolConstants
{
    /// <summary>当前协议版本。破坏性变更才递增主版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>最低可接受版本。</summary>
    public const int MinimumSupportedVersion = 1;

    /// <summary>版本协商请求头。</summary>
    public const string VersionHeader = "X-WC-Protocol";

    /// <summary>认证请求头（阶段 2 起启用）。</summary>
    public const string AuthorizationHeader = "Authorization";

    /// <summary>所有 API 的统一前缀。</summary>
    public const string ApiPrefix = "/api/v1";

    /// <summary>默认 HTTPS 端口。</summary>
    public const int DefaultHttpsPort = 47110;

    /// <summary>端口被占用时向后探测的最大次数。</summary>
    public const int PortProbeCount = 20;

    /// <summary>传输分块大小：8 MiB。</summary>
    public const int ChunkSizeBytes = 8 * 1024 * 1024;

    /// <summary>客户端可请求的最小分块（再小则请求数爆炸）。</summary>
    public const int MinChunkSizeBytes = 64 * 1024;

    /// <summary>单文件大小上限：100 GiB。超过的请求直接拒绝，避免磁盘被一次性写满。</summary>
    public const long MaxTransferBytes = 100L * 1024 * 1024 * 1024;

    /// <summary>非终态任务空闲多久后被回收（秒）。</summary>
    public const int TransferTaskIdleSeconds = 600;

    /// <summary>
    /// 已开始传输的任务多久没有字节推进就判定为对端断开（秒）。
    /// 局域网内分块间隔是毫秒级，两分钟没有任何进展只可能是链路断了；
    /// 不判定的话界面会一直停在"传输中"，显示一条永远不动的进度条。
    /// </summary>
    public const int TransferInactivitySeconds = 120;

    /// <summary>同一设备同时允许的非终态任务数。</summary>
    public const int MaxActiveTransfersPerDevice = 3;

    /// <summary>
    /// 中断任务保留多久（秒）。中断任务的临时文件与分块状态占着磁盘，
    /// 超过保留期没有恢复就判失败并清理，避免无限占用资源。
    /// </summary>
    public const int InterruptedTaskRetentionSeconds = 24 * 3600;

    /// <summary>
    /// 作业被取走后多久没有回报结果就判失败（秒）。
    ///
    /// 手机被系统回收、进程被杀时没人回报，作业会永远停在 running，
    /// 桌面端只能干等到自己的超时。5 分钟远大于 rename/delete 这类毫秒级操作；
    /// 带传输任务的作业另按"传输是否还在推进"判定，不看这个值。
    /// </summary>
    public const int JobReportTimeoutSeconds = 300;

    /// <summary>
    /// 一次最多下发给手机的作业数。
    ///
    /// 下载整个文件夹会一次入队几百个下载作业；全发下去的话手机只能逐个执行，
    /// 排在后面的会一直等到 <see cref="JobReportTimeoutSeconds"/> 被判定超时
    /// （实测 330 张图：完成 194 个，其余 136 个 job_timeout）。
    /// 改成小批取、取完再取下一批，每个作业"取走→回报"的间隔就很短。
    /// </summary>
    public const int MaxJobsPerPoll = 8;

    /// <summary>分块内容的 SHA-256 请求/响应头（上传必带，下载必回）。</summary>
    public const string ChunkSha256Header = "X-WC-Chunk-Sha256";

    /// <summary>磁盘空间检查预留余量：64 MiB。</summary>
    public const long TransferFreeSpaceMarginBytes = 64L * 1024 * 1024;

    /// <summary>创建上传任务。</summary>
    public const string TransferUploadsPath = ApiPrefix + "/transfer/uploads";

    /// <summary>任务列表 / 单个任务 / 子资源的前缀。</summary>
    public const string TransferTasksPath = ApiPrefix + "/transfer/tasks";

    /// <summary>单次最多删除多少条传输记录（多选清理，200 条足够）。</summary>
    public const int MaxDeleteTransferRecords = 200;

    /// <summary>单次请求允许的最大 JSON 体积。</summary>
    public const int MaxJsonBodyBytes = 1024 * 1024;

    /// <summary>请求追踪头，回显到响应体与响应头。</summary>
    public const string TraceIdHeader = "X-WC-Trace-Id";

    /// <summary>健康检查路径。</summary>
    public const string HealthPath = ApiPrefix + "/health";

    /// <summary>设备信息路径。</summary>
    public const string DeviceInfoPath = ApiPrefix + "/device/info";

    /// <summary>mDNS 服务类型（Android NsdManager 使用同一字符串）。</summary>
    public const string ServiceType = "_wirelesscable._tcp";

    /// <summary>发起配对。</summary>
    public const string PairRequestPath = ApiPrefix + "/pair/request";

    /// <summary>提交配对确认（验证码 + 签名）。</summary>
    public const string PairConfirmPath = ApiPrefix + "/pair/confirm";

    /// <summary>查询配对请求状态，后接 requestId。</summary>
    public const string PairStatusPath = ApiPrefix + "/pair/status/";

    /// <summary>受信设备列表 / 撤销。</summary>
    public const string DevicesPath = ApiPrefix + "/devices";

    /// <summary>会话心跳。</summary>
    public const string HeartbeatPath = ApiPrefix + "/session/heartbeat";

    /// <summary>文件管理接口前缀（目录浏览、重命名、删除等）。</summary>
    public const string FileSystemPath = ApiPrefix + "/fs";

    /// <summary>手机授权目录的索引上行入口。</summary>
    public const string FileDeviceIndexPath = ApiPrefix + "/fs/device/index";

    /// <summary>
    /// 只同步"当前还有哪些根"。授权被全部撤销时没有任何索引可上报，
    /// 需要靠这个入口告诉服务端把失效的根删掉。
    /// </summary>
    public const string FileDeviceIndexSyncPath = ApiPrefix + "/fs/device/index/sync";

    /// <summary>手机取待执行作业。</summary>
    public const string FileDeviceJobsPath = ApiPrefix + "/fs/device/jobs";

    /// <summary>配对码位数。</summary>
    public const int PairingCodeLength = 6;

    /// <summary>配对请求有效期（秒）。给人留出看码、批准、在手机上输入的时间。</summary>
    public const int PairingTtlSeconds = 300;

    /// <summary>配对码最大尝试次数，超过即锁定该请求。</summary>
    public const int PairingMaxAttempts = 5;

    /// <summary>二维码一次性随机数有效期（秒）。</summary>
    public const int QrNonceTtlSeconds = 300;

    /// <summary>
    /// 心跳建议间隔（秒）。
    ///
    /// 10 秒而不是 20 秒（1.5.59）：桌面端原来要 90 秒才判离线，用户实测"手机早没了、
    /// 电脑还显示在线"。缩短判定窗口就必须提高心跳频率，否则一次丢包就误判。
    /// 手机的作业循环本身就一直在长轮询，这点增量可以忽略。
    /// </summary>
    public const int HeartbeatIntervalSeconds = 10;

    /// <summary>
    /// 设备超时未心跳即视为离线的秒数。
    ///
    /// 取 3 个心跳周期：连续丢三次才算离线，不会因为一次网络抖动就在线/离线来回跳。
    /// 加上最后一次心跳的 10 秒，最坏情况在停止心跳后约 40 秒翻成离线。
    /// </summary>
    public const int DeviceOfflineAfterSeconds = 30;

    /// <summary>配对密钥协商套件标识。</summary>
    public const string PairingSuite = "P256-ECDH-HKDF-SHA256";

    /// <summary>协议版本字符串，用于 mDNS TXT 与二维码。</summary>
    public static string VersionText => CurrentVersion.ToString();
}
