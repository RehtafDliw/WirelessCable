package com.wirelesscable.app.core.discovery

import java.net.URLDecoder

/**
 * mDNS TXT 记录解析。抽成纯函数以便在 JVM 单测里覆盖（不需要真机）。
 *
 * 电脑多网卡时，A 记录只会带其中一条地址；服务端同时把全部候选写进
 * TXT 的 `addrs` 字段（逗号分隔），客户端据此逐个尝试。
 */
object DiscoveryTxt {

    /**
     * 还原设备名。
     *
     * 电脑端把名字做了 percent-encoding 才写进 TXT——TXT 的 character-string 只认 ASCII，
     * 而设备名里有「·」这种非 ASCII 字符，不编码的话 `Unadvertise` 会抛异常。
     * 不含 `%` 的旧值解码后仍是原样，所以新旧两端可以混用。
     */
    fun displayName(raw: String?): String? {
        if (raw.isNullOrEmpty()) return raw
        return runCatching { URLDecoder.decode(raw, "UTF-8") }.getOrDefault(raw)
    }

    /**
     * 返回候选地址列表：解析出的 [host] 排第一，其余来自 `addrs`。
     * 非法项丢弃，重复项去重，保持出现顺序。
     */
    fun candidates(host: String?, addrs: String?): List<String> {
        val result = LinkedHashSet<String>()

        host?.trim()?.takeIf(::isValidIPv4)?.let(result::add)
        addrs?.split(',')
            ?.map(String::trim)
            ?.filter(::isValidIPv4)
            ?.forEach(result::add)

        return result.toList()
    }

    fun isValidIPv4(value: String): Boolean {
        val parts = value.split('.')
        if (parts.size != 4) return false

        return parts.all { part ->
            part.isNotEmpty() && part.all(Char::isDigit) && part.toIntOrNull()?.let { it in 0..255 } == true
        }
    }
}
