package com.wirelesscable.app.core.direct

import kotlinx.serialization.Serializable

/**
 * 手机直连（不经电脑）的协议常量与报文。
 *
 * 与电脑端那套 `/api/v1/...` 完全独立：直连会话是**一次性**的——
 * 出码方起一个只在会话期间存在的 HTTPS 服务，扫码方连上来之后双方互发文件，
 * 任何一方断开、退出、换网络，会话立即作废，想再传就重新扫码。
 *
 * 安全模型：二维码就是带外信道（等价于配对时的 6 位码），
 * 里面带着自签证书指纹与会话口令；客户端按指纹 pinning 建 TLS，再用口令换访问权。
 * 不做长期信任，所以不需要证书链、不需要令牌持久化。
 */
object DirectProtocol {

    /** 二维码里的类型标记，用来和配对二维码区分。 */
    const val PAYLOAD_TYPE = "direct"

    const val VERSION = 1

    const val PATH_PREFIX = "/direct"

    /** 会话状态：对端连上后先拉一次，确认口令有效、会话还活着。 */
    const val PATH_STATUS = "$PATH_PREFIX/status"

    /** 往主机收件箱推文件。`PUT /direct/inbox/{name}?offset=N`。 */
    const val PATH_INBOX = "$PATH_PREFIX/inbox"

    /** 主机共享出来的文件清单。`GET /direct/shared`。 */
    const val PATH_SHARED = "$PATH_PREFIX/shared"

    /** 链路测速：`GET /direct/probe?bytes=N`，服务端发 N 个零字节，不碰磁盘。 */
    const val PATH_PROBE = "$PATH_PREFIX/probe"

    /** 端口段：被占用就往后试几个。 */
    const val DEFAULT_PORT = 47130
    const val PORT_PROBE_COUNT = 5

    /**
     * 分段传输的块大小（自适应）。
     *
     * 直连走的是现场 Wi-Fi，链路随时可能把连接掐断。以前整文件一个请求，
     * 断一次就全废、界面还永远挂着（1.5.19 真机：只收到几十 KB 再没动静）。
     * 现在每块一个 Range 请求，断了只丢当前块，从断点续。
     *
     * 初值取 256 KiB：现场 Wi-Fi 实测只有 70 KB/s 左右，1 MiB 一块要 15 秒，
     * 正好踩在读超时线上（1.5.20 真机：第一块刚好传完，第二块就断了）。
     * 之后按**实测速率**调：一块的目标时长是 5 秒，每次最多翻倍。
     *
     * 上限 2 MiB（1.5.48）：4 MiB 在慢链路上要一分钟，一旦这条连接死了，
     * 光一段就要等满读超时才知道；2 MiB 既够快链路走满窗口，失败也来得及重试。
     */
    const val CHUNK_INITIAL = 256 * 1024L

    /** 一块的目标传输时长：按它和实测速率算下一块的大小。 */
    const val CHUNK_TARGET_MS = 5_000L
    const val CHUNK_MIN = 64 * 1024L
    const val CHUNK_MAX = 2 * 1024 * 1024L

    /** 单块最多重试次数：超过就判定链路废了，如实报错而不是无限重试。 */
    const val CHUNK_MAX_ATTEMPTS = 5

    /** 上传时声明整文件大小（分段之后服务端要靠它判断"收完了"）。 */
    const val HEADER_TOTAL_BYTES = "x-total-bytes"

    /**
     * 传应用时的两个附加头：分组键（包名）与类型（base / split）。
     *
     * 收件方要靠它们把 `微信.apk` 和 `微信-part1.apk` 合成一次安装会话；
     * 老版本收到这两个头会直接忽略，行为退化成"若干普通 apk 文件"，不会报错。
     */
    const val HEADER_APK_GROUP = "x-apk-group"
    const val HEADER_APK_KIND = "x-apk-kind"

    /** 文件类型：普通文件 / 应用的 base / 应用的 split。 */
    const val KIND_FILE = 0
    const val KIND_APK_BASE = 1
    const val KIND_APK_SPLIT = 2

    /** 单文件上限。一次性会话不做续传，超过这个量级建议走电脑。 */
    const val MAX_FILE_BYTES = 2L * 1024 * 1024 * 1024

    /** 没人连进来时，二维码多久作废。 */
    const val SESSION_IDLE_TIMEOUT_MS = 5 * 60 * 1000L

    /**
     * 对方多久没有任何请求就算"走了"。
     *
     * 扫码方每 5 秒会拉一次共享清单当心跳，三个周期都没动静基本就是关掉了/断网了。
     * 传输大文件时服务端会持续 touch，不会被误判。
     */
    const val PEER_IDLE_TIMEOUT_MS = 15_000L

    /** 连上之后多久还没开始传，就判超时（防止占着会话不干活）。 */
    const val FIRST_FILE_TIMEOUT_MS = 60 * 1000L

    /** 会话口令的请求头。 */
    const val AUTH_HEADER = "Authorization"

    const val BEARER_PREFIX = "Bearer "

    /** 客户端声明"这是第几块开始的"用查询参数，避免自定义头部被代理吞掉。 */
    const val QUERY_OFFSET = "offset"
}

/** 电脑热点那套凭据在直连会话里的等价物：出码方开的热点。 */
@Serializable
data class DirectHotspot(
    val ssid: String,
    val passphrase: String,
    val address: String,
)

/**
 * 直连二维码载荷。
 *
 * `addr` 是出码方的局域网地址（同 Wi-Fi 时直接用）；
 * 连不上才去连 [hotspot]，连上后用 `hotspot.address` 重试。
 */
@Serializable
data class DirectPayload(
    val v: Int = DirectProtocol.VERSION,
    val type: String = DirectProtocol.PAYLOAD_TYPE,
    /** 会话 id：一次性，断开即作废。 */
    val sid: String,
    /** 出码方设备名，会话页上显示"对方是谁"。 */
    val deviceName: String = "",
    /** 首选地址（局域网）。 */
    val host: String = "",
    /** 备选地址列表。 */
    val addr: List<String> = emptyList(),
    val port: Int = DirectProtocol.DEFAULT_PORT,
    /** 自签证书指纹（SHA-256 / DER / 小写 hex）。 */
    val fp: String,
    /** 会话口令，首连后校验，过期即废。 */
    val token: String,
    val hotspot: DirectHotspot? = null,
)

/** 会话状态。客户端连上后先拉这个，确认对面还是同一个会话。 */
@Serializable
data class DirectStatus(
    val state: String,
    val sid: String,
    val deviceName: String,
    /** 主机共享出来的文件数（可以拉）。 */
    val shared: Int,
    /** 主机已收到的文件数（我推过去的）。 */
    val inbox: Int,
    val expiresAtUnixMs: Long,
)

/** 共享文件的一条。`id` 是主机侧的不透明标识，客户端只拿它来下载。 */
@Serializable
data class DirectFileDto(
    val id: String,
    val name: String,
    val sizeBytes: Long,
    /** 整文件 SHA-256，客户端下完自己核。会话内暂不计算，留空。 */
    val hashSha256: String = "",
    val modifiedUnixMs: Long = 0,
    /**
     * 同一个应用的分体包共用一个 group（用包名当键）；普通文件为 null。
     *
     * 用可空 + 默认值是为了**老版本能互通**：旧服务端不返回这两项，
     * 新客户端按"普通文件"处理；新服务端多返回的两项老客户端会忽略。
     */
    val group: String? = null,
    /** [KIND_FILE] / [KIND_APK_BASE] / [KIND_APK_SPLIT]。 */
    val kind: Int = DirectProtocol.KIND_FILE,
)

/** 出错时的响应体。单独一个类型是为了能被序列化器识别（`mapOf` 不行）。 */
@Serializable
data class DirectError(val error: String)
