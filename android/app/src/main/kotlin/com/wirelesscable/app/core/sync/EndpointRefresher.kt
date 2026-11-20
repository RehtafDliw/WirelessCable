package com.wirelesscable.app.core.sync

import android.util.Log
import com.wirelesscable.app.core.discovery.NsdDiscoveryService
import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.data.repository.ConnectionRepository
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withTimeoutOrNull

/**
 * 连接失败后重新找这台电脑。
 *
 * **要解决的问题**：手机把配对时的地址存死在 `settings`（`KEY_SESSION_HOST`），
 * 电脑换 Wi-Fi、路由器重启、DHCP 换租期之后 IP 一变，手机就永远连不上，
 * 唯一出路是重新扫码——而用户完全不知道发生了什么。
 *
 * **为什么现在能做**：电脑一直在广播 mDNS（`MdnsAdvertiser`，TXT 带 `deviceId`、
 * 端口、证书指纹、全部候选地址），手机端也有完整的发现实现（含 MulticastLock，
 * 不锁会"扫描一直空"）。两套能力早就在仓库里，只是从没在重连路径上用过。
 *
 * **为什么只在连续失败后扫**：一次网络抖动就开扫，会让正常场景多等 4 秒；
 * 而真正的 IP 变化是持续性失败，扫一次就能命中。
 */
class EndpointRefresher(
    private val discovery: NsdDiscoveryService,
    private val repository: ConnectionRepository,
) {
    /**
     * 扫一次，命中就写回会话并返回新地址。
     *
     * 调用方负责决定"什么时候值得扫"（见 [SessionSyncEngine] 里的失败计数）。
     */
    suspend fun refresh(current: ConnectionRepository.SavedSession): RefreshResult {
        // 旧结果可能带着失效地址，不清就会立刻"命中"一个错的
        discovery.reset()
        discovery.start()

        val found = try {
            withTimeoutOrNull(DISCOVERY_TIMEOUT_MS) {
                discovery.servers
                    .map { list -> list.firstOrNull { it.deviceId == current.deviceId } }
                    .first { it != null }
            }
        } finally {
            discovery.stop()
        }

        return when (val decision = EndpointDecision.decide(current, found)) {
            is EndpointDecision.Adopt -> {
                // 指纹沿用存着的那个：decision 已经确认过两者一致
                repository.saveSession(current.deviceId, decision.host, decision.port, current.fingerprint)
                Log.i(TAG, "重新发现成功 ${current.host}:${current.port} → ${decision.host}:${decision.port}")
                RefreshResult.Adopted(decision.host)
            }

            EndpointDecision.FingerprintMismatch -> {
                // 局域网里能伪造 mDNS 应答，静默接受新指纹等于把 pinning 作废
                Log.w(TAG, "发现同 deviceId 但证书指纹不同，拒绝采用")
                RefreshResult.FingerprintMismatch
            }

            EndpointDecision.Unchanged -> {
                // 打日志是为了让"到底扫没扫、扫到没"可观察——
                // 这一层出问题时，没日志就只能靠猜（配对页当年就是这么过来的）
                Log.i(TAG, "重新发现：地址未变（${current.host}:${current.port}）")
                RefreshResult.Unchanged
            }

            EndpointDecision.NotFound -> {
                Log.i(TAG, "重新发现：局域网内没找到这台电脑")
                RefreshResult.NotFound
            }
        }
    }

    companion object {
        private const val TAG = "WcEndpoint"

        /** NSD 在多数 ROM 上 1–2 秒内出结果；4 秒是给老设备留的余量，再长用户会以为卡死。 */
        private const val DISCOVERY_TIMEOUT_MS = 4_000L
    }
}

sealed interface RefreshResult {
    /** 地址已更新并写回；下一轮心跳会用新地址。 */
    data class Adopted(val host: String) : RefreshResult

    /** 扫到了，但地址和存着的一样——说明问题不是 IP 变化。 */
    data object Unchanged : RefreshResult

    /** 扫到了同 deviceId 的另一张证书：多半换了电脑或重签过，必须重新配对。 */
    data object FingerprintMismatch : RefreshResult

    /** 局域网里没有这台电脑（关机、不在同一网络、或 AP 隔离）。 */
    data object NotFound : RefreshResult
}

/**
 * 「要不要换地址」的纯决策。
 *
 * 抽成单例是为了能在 JVM 单测里覆盖四种分支——真机上没法可靠地制造
 * "IP 变了"这个场景（改 App 私有目录需要 root，装 debug 包会换签名）。
 */
object EndpointDecision {

    sealed interface Result

    data class Adopt(val host: String, val port: Int) : Result

    data object Unchanged : Result

    data object FingerprintMismatch : Result

    data object NotFound : Result

    fun decide(current: ConnectionRepository.SavedSession, found: DiscoveredServer?): Result {
        if (found == null || found.host.isBlank()) {
            return NotFound
        }

        if (!sameFingerprint(current.fingerprint, found.fingerprint)) {
            return FingerprintMismatch
        }

        if (found.host == current.host && found.port == current.port) {
            return Unchanged
        }

        return Adopt(found.host, found.port)
    }

    /**
     * 指纹按十六进制串比较。
     *
     * 两端生成的都是小写无分隔，但手工填过的那条路径允许冒号和大小写，
     * 所以统一抹掉冒号再比——这条一旦误判，用户会被要求重新配对。
     */
    fun sameFingerprint(left: String, right: String): Boolean =
        left.replace(":", "").equals(right.replace(":", ""), ignoreCase = true)
}
