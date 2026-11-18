package com.wirelesscable.app.core.direct

/**
 * `Range` 头的解析。
 *
 * 单独拎出来是因为这段算术最容易出错，而错了之后的症状很难看：
 * 算多一个字节 → 客户端等一个永远不会来的字节（就是"卡在 40 KB"那种假死）；
 * 算少一个字节 → 每次续传都原地打转，1 GB 文件永远传不完。
 *
 * 只支持单段（`bytes=start-` / `bytes=start-end`），浏览器和我们的客户端都这么用。
 * 其它形式按"整段"处理，不因为一个少见的头把正常请求拒掉。
 */
internal object DirectRange {

    /**
     * @return (起点, 本段长度)；返回 null 表示这个 Range 不可满足（应答 416）。
     */
    fun parse(header: String?, total: Long): Pair<Long, Long>? {
        if (total <= 0) {
            return 0L to 0L
        }

        if (header.isNullOrBlank()) {
            return 0L to total
        }

        val spec = header.substringAfter("bytes=", "").trim()
        val dash = spec.indexOf('-')
        if (dash <= 0) {
            return 0L to total
        }

        val from = spec.substring(0, dash).trim().toLongOrNull() ?: return null
        if (from < 0 || from >= total) {
            return null
        }

        val endText = spec.substring(dash + 1).trim()
        val requestedEnd = if (endText.isEmpty()) total - 1 else (endText.toLongOrNull() ?: return null)
        if (requestedEnd < from) {
            return null
        }

        val last = minOf(requestedEnd, total - 1)
        return from to (last - from + 1)
    }
}
