package com.wirelesscable.app.core.network

import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * 判断候选地址是否与手机某个网卡处于同一子网。
 *
 * 场景：电脑同时插了有线和 Wi-Fi（例如 192.0.2.63 与 198.51.100.108），
 * mDNS 的 A 记录只给出其中一条，手机可能拿到自己根本路由不到的那个。
 * 这里把"同子网"的地址排在前面，减少一次无谓的超时等待。
 */
object LocalSubnet {

    /** 同子网的候选排前面，其余保持原有顺序。 */
    fun preferLocal(candidates: List<String>): List<String> {
        val networks = localNetworks()
        if (networks.isEmpty() || candidates.size < 2) {
            return candidates
        }

        return candidates.sortedByDescending { host ->
            val ip = toInt(host) ?: return@sortedByDescending -1
            if (networks.any { (net, prefix) -> sameSubnet(ip, net, prefix) }) 1 else 0
        }
    }

    /** 这个地址是否和手机某个网卡在同一子网。连接诊断用。 */
    fun isSameSubnet(host: String): Boolean {
        val ip = toInt(host) ?: return false
        return localNetworks().any { (net, prefix) -> sameSubnet(ip, net, prefix) }
    }

    /** 手机自己的局域网 IPv4（第一个可用的）。诊断要把它显示给用户看。 */
    fun localAddress(): String? = try {
        NetworkInterface.getNetworkInterfaces()
            .toList()
            .filter { it.isUp && !it.isLoopback && !isCellular(it.name) }
            .flatMap { it.interfaceAddresses }
            .mapNotNull { it.address as? Inet4Address }
            .firstOrNull { address ->
                !address.isLoopbackAddress &&
                    // 169.254 是 APIPA：网线插着但没拿到 DHCP 时才出现，不能用来判断"在同一网络"
                    !address.hostAddress.orEmpty().startsWith("169.254.")
            }
            ?.hostAddress
    } catch (_: Exception) {
        null
    }

    private fun localNetworks(): List<Pair<Int, Int>> = try {
        NetworkInterface.getNetworkInterfaces()
            .toList()
            .filter { it.isUp && !it.isLoopback && !isCellular(it.name) }
            .flatMap { it.interfaceAddresses }
            .mapNotNull { address ->
                val ip = address.address
                if (ip is Inet4Address) {
                    toInt(ip.hostAddress)?.let { it to address.networkPrefixLength.toInt() }
                } else {
                    null
                }
            }
    } catch (_: Exception) {
        emptyList()
    }

    /**
     * 蜂窝接口不属于"局域网"。
     *
     * 为什么必须排除：手机在 Wi-Fi + 蜂窝双活时，`rmnet_data0` 之类也会出现在接口列表里，
     * 运营商内网地址往往是 `10.x` / `100.64.x`。不排除会有两个后果：
     *   1. 诊断页「本机网络」显示成 `10.x`（用户以为 app 认错了网卡）；
     *   2. `localNetworks()` 里多出 `10.0.0.0/8`，电脑恰好在企业网 10.x 时会被误判成
     *      "同一网络"，诊断给出"网络正常"的错误结论。
     *
     * `clat` 是 IPv4-over-IPv6 的合成接口（464xlat），地址是 192.0.0.x，同样不是真链路。
     */
    private fun isCellular(name: String?): Boolean {
        val lower = name?.lowercase() ?: return false
        return CellularPrefixes.any { lower.startsWith(it) }
    }

    private val CellularPrefixes = listOf(
        "rmnet", "ccmni", "pdp", "radio", "seth_lte", "clat", "v4-rmnet",
    )

    private fun sameSubnet(ip: Int, network: Int, prefix: Int): Boolean {
        if (prefix <= 0 || prefix > 32) return false
        val mask = if (prefix == 32) -1 else (-1 shl (32 - prefix))
        return (ip and mask) == (network and mask)
    }

    /** 点分十进制转 32 位整数，非法返回 null。 */
    fun toInt(host: String): Int? {
        val parts = host.split('.')
        if (parts.size != 4) return null

        var value = 0
        for (part in parts) {
            val byte = part.toIntOrNull() ?: return null
            if (byte !in 0..255) return null
            value = (value shl 8) or byte
        }

        return value
    }
}
