package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.ApiResult
import com.wirelesscable.app.core.protocol.DeviceInfo
import com.wirelesscable.app.core.protocol.HealthInfo
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 与 Windows 端 WirelessCable.Protocol.Tests.SerializationTests 使用同一份基准 JSON。
 * 两端都必须能解析它，并按各自序列化器还原出等价结构。
 */
class ProtocolSerializationTest {

    private val fixture =
        """{"deviceId":"9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3","displayName":"客厅电脑","platform":"windows","osVersion":"10.0.22631.0","appVersion":"0.1.0","protocolVersion":1,"certificateFingerprint":"a1b2c3d4","addresses":["192.168.1.10","10.0.0.5"],"port":47110,"capabilities":["health","device_info"]}"""

    @Test
    fun deviceInfoFixtureParses() {
        val device = WcJson.instance.decodeFromString<DeviceInfo>(fixture)

        assertEquals("9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3", device.deviceId)
        assertEquals("客厅电脑", device.displayName)
        assertEquals("windows", device.platform)
        assertEquals(47110, device.port)
        assertEquals(2, device.addresses.size)
        assertEquals(ProtocolConstants.CURRENT_VERSION, device.protocolVersion)
    }

    @Test
    fun serializedDeviceInfoEqualsFixtureSemantically() {
        val device = WcJson.instance.decodeFromString<DeviceInfo>(fixture)
        val encoded = WcJson.instance.encodeToString(device)

        assertEquals(Json.parseToJsonElement(fixture), Json.parseToJsonElement(encoded))
    }

    @Test
    fun apiResultOmitsNullFields() {
        val result = ApiResult(ok = true, data = 1, error = null, traceId = "t-1", serverTimeUnixMs = 100L)
        val encoded = WcJson.instance.encodeToString(result)

        assertFalse(encoded.contains("\"error\""))
        assertTrue(encoded.contains("\"ok\":true"))
    }

    @Test
    fun apiResultParsesServerEnvelope() {
        val json =
            """{"ok":true,"data":{"status":"running","authenticated":true,"protocolVersion":1,"appVersion":"0.1.0","port":47110,"certificateFingerprint":"abc","uptimeSeconds":7,"databaseReady":true,"activeSessions":0,"capabilities":["health"]},"protocolVersion":1,"traceId":"x","serverTimeUnixMs":1}"""

        val parsed = WcJson.instance.decodeFromString<ApiResult<HealthInfo>>(json)

        assertTrue(parsed.ok)
        assertNotNull(parsed.data)
        assertEquals("running", parsed.data!!.status)
        assertEquals(47110, parsed.data!!.port)
        assertTrue(parsed.data!!.authenticated)
        assertTrue(parsed.data!!.databaseReady)
    }

    /** 匿名 health 只带基础字段，缺省值必须让反序列化成功而不是抛异常。 */
    @Test
    fun anonymousHealthParsesWithDefaults() {
        val json =
            """{"ok":true,"data":{"status":"running","authenticated":false,"protocolVersion":1,"appVersion":"0.1.0","port":47110,"certificateFingerprint":"abc"},"protocolVersion":1,"traceId":"x","serverTimeUnixMs":1}"""

        val parsed = WcJson.instance.decodeFromString<ApiResult<HealthInfo>>(json)
        val health = parsed.data!!

        assertFalse(health.authenticated)
        assertEquals(0L, health.uptimeSeconds)
        assertFalse(health.databaseReady)
        assertTrue(health.capabilities.isEmpty())
        assertEquals(47110, health.port)
    }

    @Test
    fun unknownFieldsAreIgnored() {
        val json = fixture.replace("}", ",\"futureField\":123}")

        val device = WcJson.instance.decodeFromString<DeviceInfo>(json)

        assertEquals("客厅电脑", device.displayName)
    }
}
