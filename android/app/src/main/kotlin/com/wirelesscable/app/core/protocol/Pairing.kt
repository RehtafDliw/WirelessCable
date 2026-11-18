package com.wirelesscable.app.core.protocol

import kotlinx.serialization.Serializable

/** 配对状态常量，与 C# PairingStates 一致。 */
object PairingStates {
    const val PENDING = "pending"
    const val APPROVED = "approved"
    const val REJECTED = "rejected"
    const val EXPIRED = "expired"
    const val COMPLETED = "completed"
    const val LOCKED = "locked"
}

/** 二维码里的电脑热点（兜底通道）。旧版电脑不会带这个字段。 */
@Serializable
data class QrHotspot(
    val ssid: String,
    val passphrase: String,
    val address: String,
)

/** 二维码负载。不含任何长期密钥。 */
@Serializable
data class QrPayload(
    val version: Int,
    val deviceId: String,
    val deviceName: String,
    val addresses: List<String> = emptyList(),
    val port: Int,
    val fingerprint: String,
    val nonce: String,
    val expiresAtUnixMs: Long,
    /**
     * 电脑热点：**只在现有网络连不上时才用**。
     * 手机扫码后先用 [addresses] 连，全部失败才连这个热点、再按 [QrHotspot.address] 连一次。
     */
    val hotspot: QrHotspot? = null,
)

@Serializable
data class PairingRequest(
    val deviceId: String,
    val deviceName: String,
    val platform: String,
    val identityPublicKey: String,
    val ephemeralPublicKey: String,
    val qrNonce: String? = null,
    val clientNonce: String,
    val protocolVersion: Int = ProtocolConstants.CURRENT_VERSION,
)

@Serializable
data class PairingChallenge(
    val requestId: String,
    val serverEphemeralPublicKey: String,
    val serverNonce: String,
    val expiresAtUnixMs: Long,
    val state: String,
    val serverDeviceId: String,
    val serverName: String,
    val pairingSuite: String,
)

@Serializable
data class PairingConfirmRequest(
    val requestId: String,
    val confirmation: String,
    val signature: String,
    val protocolVersion: Int = ProtocolConstants.CURRENT_VERSION,
)

@Serializable
data class PairingConfirmResponse(
    val sealedToken: String,
    val serverDeviceId: String,
    val pairedAtUnixMs: Long,
    val heartbeatIntervalSeconds: Int,
)

@Serializable
data class PairingStatus(
    val requestId: String,
    val state: String,
    val expiresAtUnixMs: Long,
)

@Serializable
data class TrustedDevice(
    val deviceId: String,
    val displayName: String,
    val platform: String,
    val isOnline: Boolean,
    val pairedAtUnixMs: Long,
    val lastSeenUnixMs: Long? = null,
)

@Serializable
data class HeartbeatResponse(
    val sessionState: String,
    val nextHeartbeatSeconds: Int,
    val deviceOnline: Boolean,
    val pendingPairingRequests: Int,
)

/** mDNS TXT 记录解析出的服务端信息。 */
data class DiscoveredServer(
    val deviceId: String,
    val displayName: String,
    val host: String,
    val port: Int,
    val fingerprint: String,
    val protocolVersion: Int,
    /** 电脑在 TXT 里给出的全部候选地址（多网卡时用于择优重试）。 */
    val candidates: List<String> = emptyList(),
    val lastSeenElapsedMs: Long = System.currentTimeMillis(),
)
