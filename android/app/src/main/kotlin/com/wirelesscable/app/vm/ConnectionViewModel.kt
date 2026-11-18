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

            // 设备信息需要设备令牌（K5）：只有这台电脑正是上次配对的那台时才拿得到
            val saved = repository.loadSession()
            val token = saved
                ?.takeIf { it.host == endpoint.host && it.port == endpoint.port }
                ?.let { vault.load(it.deviceId) }

            val healthOutcome = api.health(token)
            if (healthOutcome is ApiOutcome.Failure) {
                fail(healthOutcome.code, healthOutcome.message)
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
