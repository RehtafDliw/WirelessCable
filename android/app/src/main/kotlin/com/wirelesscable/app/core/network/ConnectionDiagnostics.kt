package com.wirelesscable.app.core.network

import android.content.Context
import android.util.Log
import com.wirelesscable.app.core.discovery.NsdDiscoveryService
import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.data.repository.ConnectionRepository
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

/**
 * 连接诊断：把"连不上"拆成能回答的几步。
 *
 * 背景：所有连接问题在界面上都是同一句"连接中断"，但原因完全不同——
 * 不在同一网络、AP 隔离、防火墙拦了端口、证书换了。用户只能猜，
 * 我们远程也只能猜。
 *
 * 这里把五件事分别测一遍，一次给出结论。全部在手机本地完成，不需要电脑配合
 * （mDNS 是广播，健康检查免令牌）。
 */
class ConnectionDiagnostics(
    private val context: Context,
    private val discovery: NsdDiscoveryService,
    private val repository: ConnectionRepository,
) {
    suspend fun run(): DiagnosticsReport {
        val session = repository.loadSession()
        val localAddress = LocalSubnet.localAddress()
        val host = session?.host.orEmpty()
        val port = session?.port ?: DEFAULT_PORT

        val discovered = if (session != null) {
            discover(session.deviceId)
        } else {
            null
        }

        val tcp = probeTcp(host, port)
        val tls = if (tcp.ok && session != null) {
            probeTls(host, port, session.fingerprint)
        } else {
            Probe(false, if (tcp.ok) "没有配对记录" else "跳过（端口不通）")
        }

        return DiagnosticsEvaluator.evaluate(
            DiagnosticsInput(
                paired = session != null,
                localAddress = localAddress,
                targetHost = host,
                targetPort = port,
                sameSubnet = host.isNotBlank() && LocalSubnet.isSameSubnet(host),
                discoveredHost = discovered?.host,
                discoveryHeld = discovered != null,
                tcpOk = tcp.ok,
                tcpError = tcp.error,
                tlsOk = tls.ok,
                tlsError = tls.error,
            ),
        )
    }

    /** 扫 4 秒，按 deviceId 找这台电脑。找不到就是找不到，不再等。 */
    private suspend fun discover(deviceId: String): DiscoveredServer? {
        discovery.reset()
        discovery.start()
        return try {
            withTimeoutOrNull(DISCOVERY_TIMEOUT_MS) {
                discovery.servers
                    .map { list -> list.firstOrNull { it.deviceId == deviceId } }
                    .first { it != null }
            }
        } finally {
            discovery.stop()
        }
    }

    private suspend fun probeTcp(host: String, port: Int): Probe = withContext(Dispatchers.IO) {
        if (host.isBlank()) {
            return@withContext Probe(false, "没有地址")
        }

        try {
            Socket().use { socket -> socket.connect(InetSocketAddress(host, port), TCP_TIMEOUT_MS) }
            Probe(true, null)
        } catch (timeout: SocketTimeoutException) {
            // 超时 = 包被丢了：防火墙 DROP、AP 隔离、或者对面根本没开机
            Probe(false, "超时")
        } catch (refused: ConnectException) {
            // 拒绝 = 收到了 RST：网络通，但那个端口上没有服务
            Probe(false, "被拒绝")
        } catch (error: Exception) {
            Probe(false, error.javaClass.simpleName)
        }
    }

    private suspend fun probeTls(host: String, port: Int, fingerprint: String): Probe =
        try {
            val api = WcApiClient.create(ServerEndpoint(host, port), setOf(fingerprint))
            when (val outcome = api.health()) {
                is ApiOutcome.Success -> Probe(true, null)
                is ApiOutcome.Failure -> Probe(false, outcome.message.ifBlank { outcome.code })
            }
        } catch (error: Exception) {
            Log.w(TAG, "TLS 探测失败", error)
            Probe(false, describeTlsError(error))
        }

    /**
     * 证书不匹配必须和"其他 TLS 错误"分开说：
     * 前者要重新配对，后者多半是网络中间断了。
     */
    private fun describeTlsError(error: Exception): String {
        val text = (error.message ?: "") + error.cause?.message.orEmpty()
        return when {
            text.contains("PIN", ignoreCase = true) ||
                text.contains("Trust anchor", ignoreCase = true) ||
                text.contains("certificate", ignoreCase = true) -> "证书不匹配"
            error is javax.net.ssl.SSLException -> "TLS 握手失败"
            else -> error.javaClass.simpleName
        }
    }

    private data class Probe(val ok: Boolean, val error: String?)

    companion object {
        private const val TAG = "WcDiagnostics"
        private const val DISCOVERY_TIMEOUT_MS = 4_000L
        private const val TCP_TIMEOUT_MS = 2_500

        /** 与 `ProtocolConstants.DefaultHttpsPort` 一致；只在没有配对记录时用于展示。 */
        private const val DEFAULT_PORT = 47110
    }
}

// ---------------------------------------------------------------- 纯逻辑

data class DiagnosticsStep(
    val label: String,
    val ok: Boolean,
    val detail: String,
)

data class DiagnosticsReport(
    val steps: List<DiagnosticsStep>,
    /** 一句话结论，直接给用户看。 */
    val conclusion: String,
    /** 地址变了：点「重连」就能好。 */
    val suggestReconnect: Boolean = false,
    /** 需要重新配对（证书变了 / 令牌丢了）。 */
    val suggestRepair: Boolean = false,
    /** 疑似网络隔离：建议用电脑热点。 */
    val suggestHotspot: Boolean = false,
)

data class DiagnosticsInput(
    val paired: Boolean = true,
    val localAddress: String? = "192.168.1.20",
    val targetHost: String = "192.168.1.10",
    val targetPort: Int = 47110,
    val sameSubnet: Boolean = true,
    val discoveredHost: String? = "192.168.1.10",
    val discoveryHeld: Boolean = true,
    val tcpOk: Boolean = true,
    val tcpError: String? = null,
    val tlsOk: Boolean = true,
    val tlsError: String? = null,
)

/**
 * 结论生成。抽成纯函数是为了能在 JVM 单测里把每种故障都跑一遍——
 * 真机上没法可靠制造"AP 隔离""证书变了"这些场景。
 *
 * 判断顺序是刻意的：先排除"根本没在同一个网络"，再排除"被隔离"，
 * 最后才是端口和证书。顺序错了会把网络问题说成证书问题，用户会被引到错的方向。
 */
object DiagnosticsEvaluator {

    fun evaluate(input: DiagnosticsInput): DiagnosticsReport {
        val steps = mutableListOf<DiagnosticsStep>()

        val wifiOk = input.localAddress != null
        steps += DiagnosticsStep(
            "本机网络",
            wifiOk,
            input.localAddress?.let { "本机 $it" } ?: "没有可用的局域网地址",
        )

        if (!input.paired) {
            return DiagnosticsReport(steps, "还没有配对的电脑，先在首页扫码", suggestRepair = true)
        }

        steps += DiagnosticsStep(
            "同一网络",
            input.sameSubnet,
            if (input.sameSubnet) {
                "与 ${input.targetHost} 同网段"
            } else {
                "本机 ${input.localAddress ?: "—"} 与电脑 ${input.targetHost} 不在同一网段"
            },
        )

        steps += DiagnosticsStep(
            "局域网发现",
            input.discoveryHeld,
            when {
                input.discoveryHeld && input.discoveredHost != null -> "找到这台电脑（${input.discoveredHost}）"
                input.sameSubnet -> "同网段但搜不到这台电脑"
                else -> "不在同一网段，搜不到是正常的"
            },
        )

        steps += DiagnosticsStep(
            "端口连接",
            input.tcpOk,
            when {
                input.tcpOk -> "${input.targetHost}:${input.targetPort} 可达"
                input.tcpError == "被拒绝" -> "端口拒绝连接（电脑上服务可能没开）"
                input.tcpError == "超时" -> "连接超时（包被丢弃）"
                else -> input.tcpError ?: "失败"
            },
        )

        steps += DiagnosticsStep(
            "证书与心跳",
            input.tlsOk,
            if (input.tlsOk) "通过" else (input.tlsError ?: "失败"),
        )

        return conclude(input, steps)
    }

    private fun conclude(
        input: DiagnosticsInput,
        steps: List<DiagnosticsStep>,
    ): DiagnosticsReport {
        // 1. 没网 / 没配对
        if (input.localAddress == null) {
            return DiagnosticsReport(steps, "手机没有连 Wi-Fi，先连上和电脑同一个网络", suggestHotspot = false)
        }

        // 2. 不在同一网段：最常见，也最容易被误判成"电脑坏了"
        if (!input.sameSubnet) {
            return DiagnosticsReport(
                steps,
                "手机和电脑不在同一个网络。检查是不是连了不同的 Wi-Fi，或电脑插的是网线",
            )
        }

        // 3. 搜不到 + 端口不通：隔离
        if (!input.discoveryHeld && !input.tcpOk) {
            return DiagnosticsReport(
                steps,
                "同一个网段却完全不通：多半是路由器开了「AP 隔离」，或电脑的防火墙拦了。可以改成电脑开热点直连",
                suggestHotspot = true,
            )
        }

        // 4. 搜到了但地址不同：IP 变了
        val moved = input.discoveredHost != null && input.discoveredHost != input.targetHost
        if (moved) {
            return DiagnosticsReport(
                steps,
                "电脑的地址变成了 ${input.discoveredHost}（存的是 ${input.targetHost}）。点「重新连接」会自动切过去",
                suggestReconnect = true,
            )
        }

        // 5. 端口超时但能搜到：防火墙按网络类型拦了入站
        if (!input.tcpOk && input.tcpError == "超时") {
            return DiagnosticsReport(
                steps,
                "能搜到电脑但端口不通：Windows 防火墙可能把这个 Wi-Fi 当成了「公用网络」。在电脑上把当前网络改成「专用」再试",
            )
        }

        // 6. 端口被拒：服务没起
        if (!input.tcpOk && input.tcpError == "被拒绝") {
            return DiagnosticsReport(
                steps,
                "电脑有响应但端口没开：确认 WirelessCable 正在运行，且没被别的程序占了端口",
            )
        }

        // 7. TCP 通但证书不对
        if (input.tcpOk && !input.tlsOk) {
            val mismatch = input.tlsError?.contains("证书") == true
            return DiagnosticsReport(
                steps,
                if (mismatch) {
                    "端口通但证书对不上：这台电脑重装过或换了一台，需要重新配对"
                } else {
                    "端口通但 TLS 握手失败：可能有中间设备在拦截，换个网络试试"
                },
                suggestRepair = mismatch,
            )
        }

        return DiagnosticsReport(steps, "连接正常")
    }
}
