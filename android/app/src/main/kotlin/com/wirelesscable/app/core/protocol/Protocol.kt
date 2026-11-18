package com.wirelesscable.app.core.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** 协议常量，必须与 Windows 端 WirelessCable.Protocol.ProtocolConstants 完全一致。 */
object ProtocolConstants {
    const val CURRENT_VERSION = 1
    const val MINIMUM_SUPPORTED_VERSION = 1
    const val VERSION_HEADER = "X-WC-Protocol"
    const val TRACE_ID_HEADER = "X-WC-Trace-Id"
    const val API_PREFIX = "/api/v1"
    const val DEFAULT_HTTPS_PORT = 47110
    const val CHUNK_SIZE_BYTES = 8 * 1024 * 1024
    const val HEALTH_PATH = "$API_PREFIX/health"
    const val DEVICE_INFO_PATH = "$API_PREFIX/device/info"
    const val SERVICE_TYPE = "_wirelesscable._tcp"
    const val PAIR_REQUEST_PATH = "$API_PREFIX/pair/request"
    const val PAIR_CONFIRM_PATH = "$API_PREFIX/pair/confirm"
    const val PAIR_STATUS_PATH = "$API_PREFIX/pair/status/"
    const val DEVICES_PATH = "$API_PREFIX/devices"
    const val HEARTBEAT_PATH = "$API_PREFIX/session/heartbeat"
    const val PAIRING_CODE_LENGTH = 6
    const val HEARTBEAT_INTERVAL_SECONDS = 20
}

/** 错误码，必须与 Windows 端 ErrorCodes.cs 一致。 */
object ErrorCodes {
    const val INVALID_REQUEST = "invalid_request"
    const val UNAUTHORIZED = "unauthorized"
    const val FORBIDDEN = "forbidden"
    const val NOT_FOUND = "not_found"
    const val PATH_FORBIDDEN = "path_forbidden"
    const val CONFLICT = "conflict"
    const val RATE_LIMITED = "rate_limited"
    const val PROTOCOL_VERSION_UNSUPPORTED = "protocol_version_unsupported"
    const val PAYLOAD_TOO_LARGE = "payload_too_large"
    const val CHECKSUM_MISMATCH = "checksum_mismatch"
    const val SERVICE_UNAVAILABLE = "service_unavailable"
    const val INTERNAL_ERROR = "internal_error"
    const val DEVICE_REVOKED = "device_revoked"

    /** 电脑主动断开了这台设备（配对与授权都还在），等它在首页点「连接」。 */
    const val SESSION_PAUSED = "session_paused"
    const val PAIRING_CODE_INVALID = "pairing_code_invalid"
    const val PAIRING_NOT_APPROVED = "pairing_not_approved"
    const val QR_NONCE_INVALID = "qr_nonce_invalid"
}

/** 统一响应信封。字段名与顺序语义同 C# ApiResult&lt;T&gt;。 */
@Serializable
data class ApiResult<T>(
    val ok: Boolean,
    val data: T? = null,
    val error: ErrorResponse? = null,
    val protocolVersion: Int = ProtocolConstants.CURRENT_VERSION,
    val traceId: String = "",
    val serverTimeUnixMs: Long = 0L,
)

@Serializable
data class ErrorResponse(
    val code: String,
    val message: String,
    val details: String? = null,
    val retryable: Boolean = false,
)

@Serializable
data class DeviceInfo(
    val deviceId: String,
    val displayName: String,
    val platform: String,
    val osVersion: String,
    val appVersion: String,
    val protocolVersion: Int,
    val certificateFingerprint: String,
    val addresses: List<String>,
    val port: Int,
    val capabilities: List<String>,
)

/**
 * 健康检查负载。服务端分两级下发：
 * 匿名只有 status / authenticated / protocolVersion / appVersion / port / certificateFingerprint，
 * 其余字段已认证才出现——缺省值即"未下发"，界面据此决定是否展示运行态细节。
 */
@Serializable
data class HealthInfo(
    val status: String,
    val authenticated: Boolean = false,
    val protocolVersion: Int = ProtocolConstants.CURRENT_VERSION,
    val appVersion: String = "",
    val port: Int = 0,
    val certificateFingerprint: String = "",
    val uptimeSeconds: Long = 0L,
    val databaseReady: Boolean = false,
    val activeSessions: Int = 0,
    val capabilities: List<String> = emptyList(),
)

/**
 * JSON 约定（与 Windows 端 WcJson.cs 对齐）：
 * 属性名 camelCase、null 不输出、枚举用字符串、时间用 Unix 毫秒。
 */
object WcJson {
    val instance: Json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
        encodeDefaults = true
        isLenient = false
    }
}
