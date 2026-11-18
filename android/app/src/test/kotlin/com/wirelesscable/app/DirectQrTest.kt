package com.wirelesscable.app

import com.wirelesscable.app.core.direct.DirectHotspot
import com.wirelesscable.app.core.direct.DirectPayload
import com.wirelesscable.app.core.direct.DirectQr
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 直连二维码的解析与校验。
 *
 * 二维码来自不可信的外部（对方手机的屏幕），每个字段都必须过一遍：
 * 端口范围、指纹长度、口令长度；热点块不完整时**按没有热点处理**，
 * 而不是让 `setWpa2Passphrase` 在连接阶段抛异常（1.5.57 踩过：白等 25 秒）。
 */
class DirectQrTest {

    private fun payload(hotspot: DirectHotspot? = null) = DirectPayload(
        sid = "sid-1",
        deviceName = "测试手机",
        host = "192.168.1.5",
        addr = listOf("192.168.1.5"),
        port = 47130,
        fp = "a".repeat(64),
        token = "t".repeat(32),
        hotspot = hotspot,
    )

    private fun json(value: DirectPayload) = WcJson.instance.encodeToString(value)

    @Test
    fun parse_keeps_valid_payload() {
        val parsed = DirectQr.parse(json(payload()))
        assertNotNull(parsed)
        assertEquals("sid-1", parsed!!.sid)
        assertEquals(47130, parsed.port)
        assertNull(parsed.hotspot)
    }

    @Test
    fun parse_keeps_complete_hotspot() {
        val hotspot = DirectHotspot("AndroidShare_1234", "12345678", "192.168.210.1")
        val parsed = DirectQr.parse(json(payload(hotspot)))

        assertNotNull(parsed)
        assertEquals(hotspot, parsed!!.hotspot)
    }

    @Test
    fun parse_drops_hotspot_with_blank_ssid() {
        val parsed = DirectQr.parse(json(payload(DirectHotspot("", "12345678", "192.168.210.1"))))

        // 整张码仍然可用（普通 Wi-Fi 还能走），只是没有热点
        assertNotNull(parsed)
        assertNull(parsed!!.hotspot)
    }

    @Test
    fun parse_drops_hotspot_with_short_passphrase() {
        val parsed = DirectQr.parse(json(payload(DirectHotspot("AP", "1234", "192.168.210.1"))))

        assertNotNull(parsed)
        assertNull(parsed!!.hotspot)
    }

    @Test
    fun parse_drops_hotspot_with_invalid_address() {
        val parsed = DirectQr.parse(json(payload(DirectHotspot("AP", "12345678", "not-an-ip"))))

        assertNotNull(parsed)
        assertNull(parsed!!.hotspot)
    }

    @Test
    fun parse_rejects_bad_fingerprint() {
        assertNull(DirectQr.parse(json(payload().copy(fp = "a".repeat(63)))))
    }

    @Test
    fun parse_rejects_short_token() {
        assertNull(DirectQr.parse(json(payload().copy(token = "short"))))
    }

    @Test
    fun parse_rejects_bad_port() {
        assertNull(DirectQr.parse(json(payload().copy(port = 0))))
    }

    @Test
    fun parse_rejects_foreign_payload() {
        assertNull(DirectQr.parse("""{"type":"pairing","sid":"x"}"""))
        assertNull(DirectQr.parse("not json"))
    }
}
