package com.wirelesscable.app.vm

import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.data.db.DeviceEntity

/** 配对与会话界面状态。 */
data class PairingUiState(
    val scanning: Boolean = false,
    val discovered: List<DiscoveredServer> = emptyList(),
    val activeServer: DiscoveredServer? = null,
    val requestId: String? = null,
    /** 已发出配对请求，等电脑端弹窗确认。 */
    val awaitingApproval: Boolean = false,
    /** 两端一致的六位校验码，供用户比对。 */
    val pairingCode: String = "",
    val message: String? = null,
    val pairedDevices: List<DeviceEntity> = emptyList(),
    val sessionState: String = "disconnected",
    val sessionDeviceId: String? = null,
    val lastHeartbeatAt: Long = 0L,
    val manualHost: String = "",
    val manualPort: String = "47110",
    val manualFingerprint: String = "",
    val manualBusy: Boolean = false,
    /**
     * 配对记录还在，但本机令牌没了（换机恢复、清数据、解绑后重装都会这样）。
     *
     * 这时普通重连永远失败——服务端不认匿名心跳。界面要直接给「重新配对」，
     * 而不是让用户自己猜"为什么重试也没用"。
     */
    val tokenMissing: Boolean = false,
) {
    val isConnected: Boolean get() = sessionState == "connected"
    val hasPendingPairing: Boolean get() = awaitingApproval
}
