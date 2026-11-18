package com.wirelesscable.app.vm

import com.wirelesscable.app.core.protocol.DeviceInfo
import com.wirelesscable.app.core.protocol.HealthInfo

/** 连接阶段。 */
enum class ConnectionPhase { Idle, Connecting, Connected, Failed }

/** 界面订阅的唯一状态源。 */
data class ConnectionUiState(
    val phase: ConnectionPhase = ConnectionPhase.Idle,
    val host: String = "",
    val port: String = "47110",
    val fingerprint: String = "",
    val device: DeviceInfo? = null,
    val health: HealthInfo? = null,
    val errorMessage: String? = null,
    val errorCode: String? = null,
    val lastUpdatedUnixMs: Long = 0L,
) {
    val isConnected: Boolean get() = phase == ConnectionPhase.Connected
    val isBusy: Boolean get() = phase == ConnectionPhase.Connecting
}
