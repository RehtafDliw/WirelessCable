package com.wirelesscable.app.core.network

import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * 判断候选地址是否与手机某个网卡处于同一子网。
 *
 * 场景：电脑同时插了有线和 Wi-Fi（例如 172.18.15.63 与 172.17.13.108），
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

    private fun localNetworks(): List<Pair<Int, Int>> = try {
        NetworkInterface.getNetworkInterfaces()
            .toList()
            .filter { it.isUp && !it.isLoopback }
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
