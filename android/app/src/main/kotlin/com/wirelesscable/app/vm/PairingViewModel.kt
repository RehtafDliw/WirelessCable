package com.wirelesscable.app.vm

import android.content.Context
import android.os.Build
import android.util.Log
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.core.crypto.PairingCrypto
import com.wirelesscable.app.core.discovery.NsdDiscoveryService
import com.wirelesscable.app.core.direct.HotspotPermissions
import com.wirelesscable.app.core.network.ApiOutcome
import com.wirelesscable.app.core.network.Fingerprint
import com.wirelesscable.app.core.network.HotspotConnector
import com.wirelesscable.app.core.network.LocalSubnet
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.network.WcApiClient
import com.wirelesscable.app.core.pairing.QrParseResult
import com.wirelesscable.app.core.pairing.QrPayloadParser
import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.core.protocol.ErrorCodes
import com.wirelesscable.app.core.protocol.QrHotspot
import com.wirelesscable.app.core.service.SessionSyncService
import com.wirelesscable.app.core.protocol.PairingChallenge
import com.wirelesscable.app.core.protocol.PairingConfirmRequest
import com.wirelesscable.app.core.protocol.PairingRequest
import com.wirelesscable.app.core.protocol.PairingStates
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.data.db.DeviceEntity
import com.wirelesscable.app.data.repository.ConnectionRepository
import com.wirelesscable.app.data.security.IdentityKeyStore
import com.wirelesscable.app.data.security.TokenVault
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.security.KeyPair
import java.util.UUID

/**
 * 配对 + 会话编排。网络与密码学细节都在 core 层，这里只做状态机、持久化与重连。
 */
class PairingViewModel(
    private val repository: ConnectionRepository,
    private val vault: TokenVault,
    private val identityStore: IdentityKeyStore,
    private val discovery: NsdDiscoveryService,
    private val appContext: Context,
) : ViewModel() {

    private val _state = MutableStateFlow(PairingUiState())
    val state: StateFlow<PairingUiState> = _state.asStateFlow()

    private var client: WcApiClient? = null
    private var pendingEphemeral: KeyPair? = null
    private var pendingTranscript: ByteArray? = null
    private var pendingPairingKey: ByteArray? = null
    private var pendingClientEphemeral: String = ""
    private var pendingClientNonce: String = ""
    private var pendingQrNonce: String? = null
    private var sessionJob: Job? = null
    private var approvalJob: Job? = null

    val localDeviceId: String by lazy { ensureLocalDeviceId() }

    init {
        viewModelScope.launch {
            discovery.servers.collect { servers ->
                _state.update { it.copy(discovered = servers) }
            }
        }

        viewModelScope.launch {
            refreshPairedDevices()

            // 手动配对是兜底路径，但地址、端口、指纹都是同一台电脑的固定信息，
            // 没有理由让用户每次重新抄一遍指纹（64 位十六进制，抄错一位就失败）。
            // 上次配对成功时已经存进 connection.*，这里直接带出来。
            val host = repository.loadHost().orEmpty()
            val port = repository.loadPort().orEmpty()
            val fingerprint = repository.loadFingerprint().orEmpty()
            if (fingerprint.length == 64) {
                _state.update {
                    it.copy(
                        manualHost = if (it.manualHost.isBlank()) host else it.manualHost,
                        manualPort = if (it.manualPort.isBlank() || it.manualPort == "47110") {
                            port.ifBlank { it.manualPort }
                        } else {
                            it.manualPort
                        },
                        manualFingerprint = if (it.manualFingerprint.isBlank()) fingerprint else it.manualFingerprint,
                    )
                }
            }

            tryAutoConnect()
        }
    }

    fun startScan() {
        discovery.start()
        _state.update { it.copy(scanning = true) }
    }

    fun stopScan() {
        discovery.stop()
        _state.update { it.copy(scanning = false) }
    }

    /** 发起配对：生成临时 ECDH 与身份签名材料，向目标电脑请求挑战。 */
    /**
     * 扫码配对：二维码里带一次性 nonce，服务端单次消费。
     * 与 mDNS 发现、手动配对共用同一套密码学流程，只是多了 nonce 与已知指纹。
     */
    fun beginPairingFromQr(text: String) {
        when (val parsed = QrPayloadParser.parse(text)) {
            is QrParseResult.Failure -> _state.update { it.copy(message = parsed.reason) }
            is QrParseResult.Success -> {
                beginPairing(
                    server = QrPayloadParser.toDiscoveredServer(parsed.payload),
                    qrNonce = parsed.payload.nonce,
                    hotspot = parsed.payload.hotspot,
                )
            }
        }
    }

    /**
     * 从发现列表连一台电脑。
     *
     * 已配对过（本机存有令牌）就直接建立会话，不再走一遍配对；
     * 没配对过的才发起配对请求。多设备切换因此只是"点一下"，
     * 不会出现"点已配对设备又被要求重新确认"的困惑。
     */
    fun connectTo(server: DiscoveredServer) {
        viewModelScope.launch {
            val token = vault.load(server.deviceId)
            if (token == null) {
                beginPairing(server)
                return@launch
            }

            // 端点可能变了（换了网段/DHCP 重新分配），连接成功前先记下来，
            // 下次启动自动连接才不会拿着旧地址空转。
            repository.saveSession(server.deviceId, server.host, server.port, server.fingerprint)

            _state.update {
                it.copy(
                    activeServer = server,
                    sessionState = "connecting",
                    sessionDeviceId = server.deviceId,
                    message = null,
                )
            }

            startSession(server.deviceId, server.host, server.port, server.fingerprint)
        }
    }

    fun beginPairing(
        server: DiscoveredServer,
        qrNonce: String? = null,
        hotspot: QrHotspot? = null,
    ) {
        viewModelScope.launch {
            approvalJob?.cancel()
            pendingPairingKey = null
            pendingTranscript = null
            if (server.fingerprint.length != 64) {
                _state.update {
                    it.copy(activeServer = server, message = "该设备的指纹不完整，请改用二维码连接")
                }
                return@launch
            }

            pendingQrNonce = qrNonce

            _state.update {
                it.copy(
                    activeServer = server,
                    awaitingApproval = false,
                    pairingCode = "",
                    message = null,
                    requestId = null,
                    tokenMissing = false,
                )
            }

            // 第一优先：现有网络。电脑可能有多张网卡，mDNS 的 A 记录只会给出其中一条，
            // 先按"与手机同子网"排序，再逐个探测，取第一个真正可达且指纹匹配的地址。
            // 探测用匿名的 health（精简字段），指纹匹配由 TLS 固定校验保证——device/info 需要令牌，
            // 配对前拿不到，也不该拿到（K5）。
            val candidates = LocalSubnet.preferLocal(
                (listOf(server.host) + server.candidates).filter { it.isNotBlank() }.distinct(),
            )

            Log.i(TAG, "配对候选地址 host=${server.host} candidates=$candidates port=${server.port}")

            var probe = probeEndpoints(candidates, server)

            // 兜底：现有网络一个都连不上，才去连电脑热点。
            // 顺序不能反——同一 Wi-Fi 下走原路更快，也不必让用户看到一个 Wi-Fi 确认框。
            if (probe.endpoint == null && hotspot != null) {
                Log.i(TAG, "现有网络不可达，尝试电脑热点 ssid=${hotspot.ssid}")
                _state.update { it.copy(message = "现有网络连不上，正在连接电脑热点…") }

                // 连热点要运行时权限，和两机直连共用同一套。没有时 requestNetwork 抛
                // SecurityException，HotspotConnector 只能返回 false，界面就会说
                // "连不上电脑热点"——用户完全想不到是权限（1.5.57）。
                if (!HotspotPermissions.canUseHotspot(appContext)) {
                    Log.w(TAG, "缺少热点权限：${HotspotPermissions.describe(appContext)}")
                    _state.update {
                        it.copy(
                            message = "连接电脑热点需要「附近的设备」权限：" +
                                "到系统设置里给 WirelessCable 打开后重新扫码",
                        )
                    }
                    return@launch
                }

                val joined = HotspotConnector.connect(hotspot.ssid, hotspot.passphrase)
                if (!joined) {
                    _state.update {
                        it.copy(
                            message = "连不上电脑热点 ${hotspot.ssid}：请在电脑二维码下方查看密码，手动连一次再扫码",
                        )
                    }
                    return@launch
                }

                probe = probeEndpoints(listOf(hotspot.address), server)

                if (probe.endpoint == null) {
                    // 热点也没成：解除绑定，否则整个进程会继续走一条不通的网络
                    HotspotConnector.release()
                }
            }

            val endpoint = probe.endpoint
            val chosenClient = probe.api
            if (endpoint == null || chosenClient == null) {
                _state.update {
                    it.copy(message = "无法连接电脑：${probe.error ?: "地址不可达"}")
                }
                return@launch
            }

            _state.update { it.copy(message = null) }
            client = chosenClient

            val identity = identityStore.loadOrCreate()
            val ephemeral = PairingCrypto.generateEphemeralKeyPair()
            val clientEphemeral = PairingCrypto.encodePublicKey(ephemeral.public)
            val clientNonce = PairingCrypto.base64(PairingCrypto.randomNonce())

            pendingEphemeral = ephemeral
            pendingClientEphemeral = clientEphemeral
            pendingClientNonce = clientNonce

            val request = PairingRequest(
                deviceId = localDeviceId,
                deviceName = Build.MODEL ?: "Android",
                platform = "android",
                identityPublicKey = PairingCrypto.encodePublicKey(identity.public),
                ephemeralPublicKey = clientEphemeral,
                qrNonce = pendingQrNonce,
                clientNonce = clientNonce,
            )

            when (val outcome = chosenClient.pairRequest(request)) {
                is ApiOutcome.Success -> {
                    val challenge = outcome.value
                    // 服务端在挑战里自报身份，权威覆盖本地占位值：
                    // 手动配对（只填了地址+指纹）由此拿到真实 deviceId 与显示名。
                    // host 用"实际探测通的地址"——走热点兜底时它是 192.168.137.x，
                    // 沿用二维码里的原网段地址会让随后的会话一直连不上。
                    val authoritative = server.copy(
                        deviceId = challenge.serverDeviceId,
                        displayName = challenge.serverName,
                        host = endpoint.host,
                        candidates = listOf(endpoint.host),
                        protocolVersion = ProtocolConstants.CURRENT_VERSION,
                    )

                    // 挑战到手即可协商密钥：校验码由密钥派生，两端显示同一个数字，
                    // 用户比对后只需在电脑上点「允许」，手机这边不再手输。
                    val pairingKey = derivePairingKey(challenge)
                    if (pairingKey == null) {
                        _state.update { it.copy(message = "配对密钥协商失败，请重试") }
                        return@launch
                    }

                    pendingPairingKey = pairingKey
                    val code = PairingCrypto.deriveSasCode(pairingKey)

                    _state.update {
                        it.copy(
                            activeServer = authoritative,
                            requestId = challenge.requestId,
                            awaitingApproval = true,
                            pairingCode = code,
                            message = null,
                        )
                    }

                    watchApproval(chosenClient, challenge)
                }

                is ApiOutcome.Failure -> {
                    // 二维码 nonce 一次性：重试必须重新扫码/重新生成
                    val text = if (outcome.code == ErrorCodes.QR_NONCE_INVALID) {
                        "二维码已使用或已过期，请在电脑上重新生成"
                    } else {
                        "${outcome.message}（${outcome.code}）"
                    }
                    _state.update { it.copy(message = text) }
                }
            }
        }
    }

    /** 逐个地址探测，返回第一个可达的端点。 */
    private suspend fun probeEndpoints(hosts: List<String>, server: DiscoveredServer): ProbeResult {
        var lastError: String? = null

        for (host in hosts) {
            val parsed = ServerEndpoint.parse(host, server.port.toString()) ?: continue
            val client = WcApiClient.create(parsed, setOf(server.fingerprint))

            when (val outcome = client.health()) {
                is ApiOutcome.Success -> {
                    Log.i(TAG, "探测 $host -> 成功")
                    return ProbeResult(parsed, client, null)
                }

                is ApiOutcome.Failure -> {
                    Log.i(TAG, "探测 $host -> ${outcome.message}")
                    lastError = outcome.message
                }
            }
        }

        return ProbeResult(null, null, lastError)
    }

    private data class ProbeResult(
        val endpoint: ServerEndpoint?,
        val api: WcApiClient?,
        val error: String?,
    )

    /** 用已有临时密钥 + 挑战算出配对密钥。 */
    private fun derivePairingKey(challenge: PairingChallenge): ByteArray? {
        val ephemeral = pendingEphemeral ?: return null
        return runCatching {
            val serverKey = PairingCrypto.decodePublicKey(challenge.serverEphemeralPublicKey)
            val shared = PairingCrypto.deriveSharedSecret(ephemeral.private, serverKey)
            val transcript = PairingCrypto.buildTranscript(
                localDeviceId,
                pendingClientEphemeral,
                challenge.serverEphemeralPublicKey,
                pendingClientNonce,
                challenge.serverNonce,
            )
            pendingTranscript = transcript
            PairingCrypto.derivePairingKey(shared, transcript)
        }.getOrElse { error ->
            Log.w(TAG, "派生配对密钥失败", error)
            null
        }
    }

    /**
     * 轮询配对状态。电脑端点了「允许」就自动完成确认——用户不用再把数字抄到手机上。
     * 失败不立刻放弃（局域网偶发丢包很常见），持续到请求本身过期为止。
     */
    private fun watchApproval(api: WcApiClient, challenge: PairingChallenge) {
        approvalJob?.cancel()
        approvalJob = viewModelScope.launch {
            while (isActive) {
                delay(APPROVAL_POLL_INTERVAL_MS)

                if (System.currentTimeMillis() >= challenge.expiresAtUnixMs) {
                    _state.update {
                        it.copy(awaitingApproval = false, message = "配对超时，请重新发起")
                    }
                    return@launch
                }

                when (val status = api.pairStatus(challenge.requestId)) {
                    is ApiOutcome.Failure -> Unit

                    is ApiOutcome.Success -> when (status.value.state) {
                        PairingStates.APPROVED, PairingStates.COMPLETED -> {
                            completePairing(api, challenge.requestId)
                            return@launch
                        }

                        PairingStates.REJECTED -> {
                            _state.update {
                                it.copy(awaitingApproval = false, message = "电脑已拒绝该请求")
                            }
                            return@launch
                        }

                        PairingStates.EXPIRED, PairingStates.LOCKED -> {
                            _state.update {
                                it.copy(awaitingApproval = false, message = "配对已失效，请重新发起")
                            }
                            return@launch
                        }

                        else -> Unit
                    }
                }
            }
        }
    }

    fun onManualHostChanged(value: String) = _state.update { it.copy(manualHost = value) }

    fun onManualPortChanged(value: String) = _state.update { it.copy(manualPort = value.filter(Char::isDigit)) }

    fun onManualFingerprintChanged(value: String) = _state.update { it.copy(manualFingerprint = value) }

    /**
     * 手动配对：不走 mDNS，直接用"地址 + 指纹"探测可达性，再发起同一套配对流程。
     * 适用于 AP 过滤组播、或跨网段但可路由的场景。
     *
     * 探测只能走匿名 health（device/info 需令牌），因此这里拿不到设备名与 deviceId；
     * 真实身份由服务端在配对挑战里返回，再由 [beginPairing] 回填。
     */
    fun beginManualPairing() {
        val current = _state.value
        val endpoint = ServerEndpoint.parse(current.manualHost, current.manualPort)
        if (endpoint == null) {
            _state.update { it.copy(message = "地址或端口不合法") }
            return
        }

        val pin = Fingerprint.normalize(current.manualFingerprint)
        if (!Fingerprint.isValid(pin)) {
            _state.update { it.copy(message = "证书指纹需要 64 位十六进制（电脑「设置」页可复制）") }
            return
        }

        viewModelScope.launch {
            _state.update { it.copy(manualBusy = true, message = null) }

            val api = WcApiClient.create(endpoint, setOf(pin))
            when (val probe = api.health()) {
                is ApiOutcome.Failure -> _state.update {
                    it.copy(manualBusy = false, message = "无法连接：${probe.message}（${probe.code}）")
                }

                is ApiOutcome.Success -> {
                    _state.update { it.copy(manualBusy = false) }
                    beginPairing(
                        DiscoveredServer(
                            deviceId = "",
                            displayName = endpoint.host,
                            host = endpoint.host,
                            port = endpoint.port,
                            fingerprint = pin,
                            protocolVersion = probe.value.protocolVersion,
                        ),
                    )
                }
            }
        }
    }

    private suspend fun completePairing(api: WcApiClient, requestId: String) {
        val pairingKey = pendingPairingKey
        val transcript = pendingTranscript
        if (pairingKey == null || transcript == null) {
            _state.update { it.copy(message = "会话已失效，请重新发起配对") }
            return
        }

        val code = PairingCrypto.deriveSasCode(pairingKey)
        val confirmation = PairingCrypto.computeConfirmation(pairingKey, code)
        val identity = identityStore.loadOrCreate()
        val signature = PairingCrypto.signTranscript(identity.private, transcript)

        val request = PairingConfirmRequest(
            requestId = requestId,
            confirmation = PairingCrypto.base64(confirmation),
            signature = PairingCrypto.base64(signature),
        )

        when (val outcome = api.pairConfirm(request)) {
            is ApiOutcome.Failure -> _state.update {
                it.copy(message = "${outcome.message}（${outcome.code}）")
            }

            is ApiOutcome.Success -> {
                val sealed = java.util.Base64.getDecoder().decode(outcome.value.sealedToken)
                val token = String(PairingCrypto.unsealToken(pairingKey, requestId, sealed), Charsets.UTF_8)

                val server = _state.value.activeServer
                if (server == null) {
                    _state.update { it.copy(message = "缺少服务器信息") }
                    return
                }

                vault.store(server.deviceId, token)
                repository.savePairedDevice(
                    DeviceEntity(
                        deviceId = server.deviceId,
                        displayName = server.displayName,
                        platform = "windows",
                        certificateFingerprint = server.fingerprint,
                        isPaired = true,
                        lastSeenUtc = System.currentTimeMillis(),
                    ),
                )
                repository.saveSession(server.deviceId, server.host, server.port, server.fingerprint)

                _state.update {
                    it.copy(
                        awaitingApproval = false,
                        pairingCode = "",
                        message = "配对完成",
                        requestId = null,
                        sessionState = "connecting",
                        sessionDeviceId = server.deviceId,
                    )
                }

                refreshPairedDevices()
                startSession(server.deviceId, server.host, server.port, server.fingerprint)
            }
        }
    }

    fun cancelPairing() {
        approvalJob?.cancel()
        pendingPairingKey = null
        pendingTranscript = null

        // 如果这一轮已经切到电脑热点，取消就放掉绑定：否则进程会继续困在热点网络上，
        // 用户回到原来的 Wi-Fi 反而上不了网。
        HotspotConnector.release()

        _state.update {
            it.copy(
                activeServer = null,
                requestId = null,
                awaitingApproval = false,
                pairingCode = "",
                message = null,
            )
        }
    }

    /** 改电脑在本机的显示名。只影响这台手机的显示，不发给电脑。 */
    fun renameDevice(deviceId: String, name: String) {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return

        viewModelScope.launch {
            repository.renameDevice(deviceId, trimmed.take(64))
            refreshPairedDevices()
        }
    }

    fun forgetDevice(deviceId: String) {
        viewModelScope.launch {
            sessionJob?.cancel()
            // 会话没了，后台连接也失去意义：不停掉的话通知会一直挂着"正在连接电脑…"
            SessionSyncService.stop(appContext)
            HotspotConnector.release()
            vault.clear(deviceId)
            repository.forgetDevice(deviceId)
            refreshPairedDevices()
            _state.update { it.copy(sessionState = "disconnected", sessionDeviceId = null, message = null) }
        }
    }

    /** 手动重连（界面上"重试"）。 */
    fun retrySession() {
        viewModelScope.launch {
            val saved = repository.loadSession()
            if (saved == null) {
                _state.update { it.copy(message = "尚未配对任何设备") }
                return@launch
            }

            // 后台引擎那头也有闸门（只自动重连一次），手动点要一起放开
            SessionSyncEngine.reconnectManually()

            _state.update { it.copy(sessionState = "connecting", sessionDeviceId = saved.deviceId) }
            startSession(saved.deviceId, saved.host, saved.port, saved.fingerprint)
        }
    }

    /**
     * 一键重新配对：配对记录还在，只是令牌丢了。
     *
     * 地址、端口、指纹在配对成功时就存进了 `connection.*`，没必要让用户再扫一次码、
     * 也不用重抄 64 位指纹 —— 直接对这台电脑发起同一套配对请求，电脑上点「允许」即可。
     */
    fun rePairLast() {
        viewModelScope.launch {
            val saved = repository.loadSession()
            if (saved == null) {
                _state.update { it.copy(message = "没有可重新配对的电脑，请扫码或搜索配对") }
                return@launch
            }

            val name = repository.allDevices()
                .firstOrNull { it.deviceId == saved.deviceId }
                ?.displayName
                ?: saved.host

            beginPairing(
                DiscoveredServer(
                    deviceId = saved.deviceId,
                    displayName = name,
                    host = saved.host,
                    port = saved.port,
                    fingerprint = saved.fingerprint,
                    protocolVersion = 1,
                ),
            )
        }
    }

    private suspend fun refreshPairedDevices() {
        _state.update { it.copy(pairedDevices = repository.allDevices()) }
    }

    private suspend fun tryAutoConnect() {
        val saved = repository.loadSession() ?: return
        if (vault.load(saved.deviceId) == null) return

        _state.update { it.copy(sessionState = "connecting", sessionDeviceId = saved.deviceId) }
        startSession(saved.deviceId, saved.host, saved.port, saved.fingerprint)
    }

    /**
     * 会话循环：心跳成功则每 20 秒一次；失败按 5s→10s→…→60s 退避重试。
     * 遇到令牌失效/被撤销立即停止，避免无意义的撞门。
     */
    private fun startSession(deviceId: String, host: String, port: Int, fingerprint: String) {
        sessionJob?.cancel()
        sessionJob = viewModelScope.launch {
            var backoffMs = 5_000L
            // 本次断线是否已经自动重连过一次：只给一次机会，再失败就等用户点
            var autoRetried = false
            val endpoint = ServerEndpoint(host, port)

            while (isActive) {
                val token = vault.load(deviceId)
                if (token == null) {
                    _state.update {
                        it.copy(
                            sessionState = "disconnected",
                            tokenMissing = true,
                            message = "缺少设备令牌，需要重新配对",
                        )
                    }
                    return@launch
                }

                val api = WcApiClient.create(endpoint, setOf(fingerprint))

                when (val beat = api.heartbeat(token)) {
                    is ApiOutcome.Success -> {
                        backoffMs = 5_000L
                        autoRetried = false
                        _state.update {
                            it.copy(
                                sessionState = "connected",
                                lastHeartbeatAt = System.currentTimeMillis(),
                                message = null,
                                tokenMissing = false,
                            )
                        }
                        delay((beat.value.nextHeartbeatSeconds.coerceAtLeast(5)) * 1000L)
                    }

                    is ApiOutcome.Failure -> {
                        if (beat.code == ErrorCodes.DEVICE_REVOKED || beat.code == ErrorCodes.UNAUTHORIZED) {
                            vault.clear(deviceId)
                            SessionSyncService.stop(appContext)
                            _state.update {
                                it.copy(
                                    sessionState = "revoked",
                                    message = "该设备授权已失效，请重新配对",
                                )
                            }
                            return@launch
                        }

                        if (!autoRetried) {
                            autoRetried = true
                            _state.update {
                                it.copy(sessionState = "disconnected", message = "连接中断，正在自动重连…")
                            }
                            delay(backoffMs)
                            backoffMs = (backoffMs * 2).coerceAtMost(60_000L)
                            continue
                        }

                        // 自动重连已经用过一次：停在这里，首页给「重连」按钮
                        _state.update {
                            it.copy(sessionState = "disconnected", message = "连接已断开，点「重连」再试")
                        }
                        return@launch
                    }
                }
            }
        }
    }

    fun clearMessage() {
        _state.update { it.copy(message = null) }
    }

    private fun ensureLocalDeviceId(): String {
        val prefs = appContext.getSharedPreferences("wirelesscable_app", Context.MODE_PRIVATE)
        val existing = prefs.getString(KEY_DEVICE_ID, null)
        if (!existing.isNullOrEmpty()) return existing

        val created = UUID.randomUUID().toString().replace("-", "")
        prefs.edit().putString(KEY_DEVICE_ID, created).apply()
        return created
    }

    override fun onCleared() {
        sessionJob?.cancel()
        approvalJob?.cancel()
        discovery.stop()
        super.onCleared()
    }

    companion object {
        private const val TAG = "WcPair"
        private const val KEY_DEVICE_ID = "device.id"

        /** 等待电脑确认时的轮询间隔。局域网一次查询几十毫秒，1.2 秒足够跟手。 */
        private const val APPROVAL_POLL_INTERVAL_MS = 1_200L

        fun factory(
            repository: ConnectionRepository,
            vault: TokenVault,
            identityStore: IdentityKeyStore,
            discovery: NsdDiscoveryService,
            appContext: Context,
        ) = viewModelFactory {
            initializer { PairingViewModel(repository, vault, identityStore, discovery, appContext) }
        }
    }
}
