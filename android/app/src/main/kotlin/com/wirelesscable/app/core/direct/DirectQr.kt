package com.wirelesscable.app.core.direct

import android.graphics.Bitmap
import android.graphics.Color
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.decodeFromString

/** 直连二维码的生成与解析。 */
object DirectQr {

    /** 生成二维码位图。内容过长（共享文件多）时会编码失败，返回 null 由界面提示。 */
    fun encode(text: String, sizePx: Int = 720): Bitmap? = try {
        val matrix = QRCodeWriter().encode(
            text,
            BarcodeFormat.QR_CODE,
            sizePx,
            sizePx,
            mapOf(EncodeHintType.MARGIN to 1),
        )

        val pixels = IntArray(sizePx * sizePx)
        for (y in 0 until sizePx) {
            val row = y * sizePx
            for (x in 0 until sizePx) {
                pixels[row + x] = if (matrix[x, y]) Color.BLACK else Color.WHITE
            }
        }

        Bitmap.createBitmap(pixels, sizePx, sizePx, Bitmap.Config.ARGB_8888)
    } catch (error: Exception) {
        android.util.Log.w("WcDirect", "二维码生成失败", error)
        null
    }

    /**
     * 解析扫码结果。不是直连码就返回 null（调用方继续按配对码处理）。
     *
     * 字段都来自不可信的二维码，所以逐项校验：类型、端口范围、指纹长度、口令长度。
     */
    fun parse(text: String): DirectPayload? {
        val trimmed = text.trim()
        if (!trimmed.startsWith("{")) {
            return null
        }

        val payload = runCatching {
            WcJson.instance.decodeFromString<DirectPayload>(trimmed)
        }.getOrNull() ?: return null

        if (payload.type != DirectProtocol.PAYLOAD_TYPE) {
            return null
        }

        if (payload.port !in 1..65535) {
            return null
        }

        // 自签证书指纹是 32 字节的 hex
        if (payload.fp.length != 64 || payload.token.length < 32) {
            return null
        }

        if (payload.host.isBlank() && payload.addr.isEmpty()) {
            return null
        }

        // 热点字段和配对码一样要校验：空 SSID / 密码长度不对时，
        // `WifiNetworkSpecifier.Builder().setWpa2Passphrase` 会抛 IllegalArgumentException，
        // 用户只会看到"加入对方热点失败"，白等 25 秒（1.5.57）。
        // 不合格就当这张码里没有热点——普通 Wi-Fi 还是能用的。
        val hotspot = payload.hotspot?.takeIf {
            it.ssid.isNotBlank() &&
                it.passphrase.length in 8..63 &&
                com.wirelesscable.app.core.discovery.DiscoveryTxt.isValidIPv4(it.address)
        }

        if (hotspot !== payload.hotspot && payload.hotspot != null) {
            android.util.Log.w("WcDirect", "二维码里的热点字段不完整，按没有热点处理：${payload.hotspot}")
        }

        return payload.copy(hotspot = hotspot)
    }
}
