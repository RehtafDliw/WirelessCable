package com.wirelesscable.app.vm

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.Fingerprint
import com.wirelesscable.app.core.network.NetworkErrorCodes
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.network.WcApiClient
import com.wirelesscable.app.core.protocol.ErrorCodes
import com.wirelesscable.app.core.protocol.HealthInfo
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.data.db.DeviceEntity
import com.wirelesscable.app.data.repository.ConnectionRepository
import com.wirelesscable.app.data.security.TokenVault
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/**
 * 连接流程的编排者。所有网络与证书细节都在 core 层，这里只做状态机与持久化。
 */
class ConnectionViewModel(
    private val repository: ConnectionRepository,
    private val vault: TokenVault,
) : ViewModel() {

    private val _state = MutableStateFlow(ConnectionUiState())
    val state: StateFlow<ConnectionUiState> = _state.asStateFlow()

    val devices: StateFlow<List<DeviceEntity>> = repository.devices
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())

    val recentTransfers = repository.recentTransfers
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), emptyList())

    private var client: WcApiClient? = null
    private var currentToken: String? = null

    init {
        viewModelScope.launch {
            var host = repository.loadHost().orEmpty()
            var port = repository.loadPort() ?: ProtocolConstants.DEFAULT_HTTPS_PORT.toString()
            var fingerprint = repository.loadFingerprint().orEmpty()

            // 旧版本扫码配对只写了 session.*，设置页读 connection.* 会是空的。
            // 这里用会话兜底：用户升级后不必重新配对，反向服务也能直接开。
            if (host.isEmpty() || fingerprint.isEmpty()) {
                repository.loadSession()?.let { saved ->
                    host = saved.host
                    port = saved.port.toString()
                    fingerprint = saved.fingerprint
                }
            }

            _state.update { it.copy(host = host, port = port, fingerprint = fingerprint) }
        }
    }

    fun onHostChanged(value: String) = _state.update { it.copy(host = value) }

    fun onPortChanged(value: String) = _state.update { it.copy(port = value.filter(Char::isDigit)) }

    fun onFingerprintChanged(value: String) = _state.update { it.copy(fingerprint = value) }

    fun connect() {
        val current = _state.value
        if (current.isBusy) return

        val endpoint = ServerEndpoint.parse(current.host, current.port)
        if (endpoint == null) {
            fail(NetworkErrorCodes.INVALID_ENDPOINT, "地址或端口不合法")
            return
        }

        val pin = Fingerprint.normalize(current.fingerprint)
        if (!Fingerprint.isValid(pin)) {
            fail(NetworkErrorCodes.INVALID_ENDPOINT, "证书指纹需要 64 位十六进制")
            return
        }

        _state.update {
            it.copy(phase = ConnectionPhase.Connecting, errorCode = null, errorMessage = null)
        }

        viewModelScope.launch {
            val api = WcApiClient.create(endpoint, setOf(pin))

            // 凭据按**证书指纹**认，不按地址。
            //
            // 换网络（连手机热点、换 Wi-Fi、DHCP 换租约）后电脑 IP 必然会变，
            // 但电脑还是那台电脑。原来要求 host/port 完全一致才加载令牌，
            // 结果是"用户明明配过对，手动改个地址却被判成未配对，要求重新扫码"——
            // 移动办公场景必踩。指纹是这台电脑的身份，它没变就该继续用旧凭据。
            val saved = repository.loadSession()
            val token = saved
                ?.takeIf { Fingerprint.normalize(it.fingerprint) == pin }
                ?.let { vault.load(it.deviceId) }

            val healthOutcome = api.health(token)
            if (healthOutcome is ApiOutcome.Failure) {
                // 这次连不上，但**配对的那台电脑可能还在正常连着**。
                // 不回滚的话，界面会停在"连接超时"，而实际传输/心跳都好好的——
                // 用户会以为断了，去重新配对，反而把好好的会话拆了。
                // 只在"当前确实连着、且这次试的是另一个地址"时回滚。
                val session = repository.loadSession()
                if (session != null &&
                    SessionSyncEngine.state.value.connected &&
                    (session.host != endpoint.host || session.port != endpoint.port)
                ) {
                    _state.update {
                        it.copy(
                            phase = ConnectionPhase.Failed,
                            errorCode = healthOutcome.code,
                            errorMessage = "这个地址连不上，仍在用 ${session.host} 连接",
                            host = session.host,
                            port = session.port.toString(),
                        )
                    }
                } else {
                    fail(healthOutcome.code, healthOutcome.message)
                }

                return@launch
            }
            val health = (healthOutcome as ApiOutcome.Success).value

            repository.rememberEndpoint(endpoint, pin)

            if (token == null) {
                // 服务可达但没有凭据：不假装已连接，直接指向正确的操作入口
                _state.update {
                    it.copy(
                        phase = ConnectionPhase.Failed,
                        health = health,
                        device = null,
                        errorCode = ErrorCodes.UNAUTHORIZED,
                        errorMessage = "该电脑尚未配对，请在「设备」页完成配对",
                    )
                }
                return@launch
            }

            val deviceOutcome = api.deviceInfo(token)
            if (deviceOutcome is ApiOutcome.Failure) {
                fail(deviceOutcome.code, deviceOutcome.message, health)
                return@launch
            }
            val device = (deviceOutcome as ApiOutcome.Success).value

            // 服务端自报的指纹必须与固定值一致，否则说明对端换了证书
            val reported = Fingerprint.normalize(device.certificateFingerprint)
            if (reported.isNotEmpty() && reported != pin) {
                fail(NetworkErrorCodes.TLS_PIN_MISMATCH, "服务端声明的指纹与已固定指纹不一致", health)
                return@launch
            }

            client = api
            currentToken = token
            val now = System.currentTimeMillis()

            // 地址和会话里记的不一样（换了网络）：把会话迁到新地址。
            // 不迁的话界面显示"已连接"，可后台心跳还打在旧 IP 上——传输、作业全不动，
            // 用户会以为"连上了但传不了"。
            if (saved != null && (saved.host != endpoint.host || saved.port != endpoint.port)) {
                repository.saveSession(device.deviceId, endpoint.host, endpoint.port, pin)
                // 清掉"自动重连已用完"的闸门，让后台立刻按新地址试一次
                SessionSyncEngine.retryNow()
            }

            _state.update {
                it.copy(
                    phase = ConnectionPhase.Connected,
                    device = device,
                    health = health,
                    errorCode = null,
                    errorMessage = null,
                    lastUpdatedUnixMs = now,
                )
            }

            repository.recordConnectedDevice(
                device = DeviceEntity(
                    deviceId = device.deviceId,
                    displayName = device.displayName,
                    platform = device.platform,
                    certificateFingerprint = reported,
                    // 仅"连上了"不等于已配对；配对状态由 PairingViewModel 写入
                    isPaired = false,
                    lastSeenUtc = now,
                ),
                endpoint = endpoint,
                fingerprint = pin,
                now = now,
            )
        }
    }

    /** 重新拉一次健康检查，用于界面下拉/定时刷新。 */
    fun refresh() {
        val api = client ?: return
        viewModelScope.launch {
            when (val outcome = api.health(currentToken)) {
                is ApiOutcome.Success -> _state.update {
                    it.copy(
                        health = outcome.value,
                        lastUpdatedUnixMs = System.currentTimeMillis(),
                        errorMessage = null,
                        errorCode = null,
                    )
                }

                is ApiOutcome.Failure -> _state.update {
                    it.copy(phase = ConnectionPhase.Failed, errorCode = outcome.code, errorMessage = outcome.message)
                }
            }
        }
    }

    fun disconnect() {
        client = null
        currentToken = null
        _state.update {
            it.copy(phase = ConnectionPhase.Idle, device = null, health = null, errorMessage = null, errorCode = null)
        }
    }

    private fun fail(code: String, message: String, health: HealthInfo? = null) {
        _state.update {
            it.copy(
                phase = ConnectionPhase.Failed,
                health = health ?: it.health,
                errorCode = code,
                errorMessage = message,
            )
        }
    }

    companion object {
        fun factory(repository: ConnectionRepository, vault: TokenVault) = viewModelFactory {
            initializer { ConnectionViewModel(repository, vault) }
        }
    }
}
