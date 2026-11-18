package com.wirelesscable.app.core.pairing

import com.wirelesscable.app.core.discovery.DiscoveryTxt
import com.wirelesscable.app.core.network.Fingerprint
import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.core.protocol.QrPayload
import kotlinx.serialization.json.Json

/** 扫码结果。 */
sealed interface QrParseResult {
    data class Success(val payload: QrPayload) : QrParseResult
    data class Failure(val reason: String) : QrParseResult
}

/**
 * 二维码负载解析与校验。纯函数，可脱离 Android 运行时单测。
 *
 * 二维码只出现在**电脑屏幕上**，其中不含任何长期密钥：
 * 只有 deviceId、地址、端口、证书指纹、一次性 nonce、协议版本与过期时间。
 * nonce 由服务端单次消费，扫码即失效，防重放。
 */
object QrPayloadParser {

    private val json = Json { ignoreUnknownKeys = true }

    fun parse(text: String, nowUnixMs: Long = System.currentTimeMillis()): QrParseResult {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) {
            return QrParseResult.Failure("二维码内容为空")
        }

        val payload = try {
            json.decodeFromString<QrPayload>(trimmed)
        } catch (e: Exception) {
            return QrParseResult.Failure("不是 WirelessCable 的二维码")
        }

        if (payload.version != ProtocolConstants.CURRENT_VERSION) {
            return QrParseResult.Failure("二维码版本不受支持")
        }

        if (payload.deviceId.isBlank()) {
            return QrParseResult.Failure("二维码缺少设备标识")
        }

        if (payload.nonce.isBlank()) {
            return QrParseResult.Failure("二维码缺少一次性随机数")
        }

        val fingerprint = Fingerprint.normalize(payload.fingerprint)
        if (!Fingerprint.isValid(fingerprint)) {
            return QrParseResult.Failure("二维码里的证书指纹无效")
        }

        if (payload.port !in 1..65535) {
            return QrParseResult.Failure("二维码里的端口无效")
        }

        val addresses = payload.addresses
            .map(String::trim)
            .filter(DiscoveryTxt::isValidIPv4)
            .distinct()

        // 热点只是兜底：字段不完整就当作没有，不能因为它让整张码失效
        val hotspot = payload.hotspot?.takeIf {
            it.ssid.isNotBlank() &&
                it.passphrase.length in 8..63 &&
                DiscoveryTxt.isValidIPv4(it.address)
        }

        // 电脑只剩热点一条路时 addresses 会是空的（热点地址被剔除），
        // 这时仍然可以靠兜底通道连上，不能判整张码无效。
        if (addresses.isEmpty() && hotspot == null) {
            return QrParseResult.Failure("二维码里没有可用地址")
        }

        if (payload.expiresAtUnixMs <= nowUnixMs) {
            return QrParseResult.Failure("二维码已过期，请在电脑上重新生成")
        }

        return QrParseResult.Success(
            payload.copy(fingerprint = fingerprint, addresses = addresses, hotspot = hotspot),
        )
    }

    /** 转成与 mDNS 发现一致的结构，复用同一套配对流程。 */
    fun toDiscoveredServer(payload: QrPayload): DiscoveredServer {
        // addresses 为空时（只剩热点）用热点地址占位：它的作用是"先试一次"，
        // 试不通自然会走兜底逻辑。
        val primary = payload.addresses.firstOrNull() ?: payload.hotspot?.address.orEmpty()

        return DiscoveredServer(
            deviceId = payload.deviceId,
            displayName = payload.deviceName.ifBlank { "电脑" },
            host = primary,
            port = payload.port,
            fingerprint = payload.fingerprint,
            protocolVersion = payload.version,
            // 只放现有网络的地址：热点地址单独走 hotspot 兜底，
            // 混进候选会让没连热点的手机白等一次连接超时。
            candidates = payload.addresses,
        )
    }
}
