package com.wirelesscable.app

import com.wirelesscable.app.core.apps.InstalledApps
import com.wirelesscable.app.core.direct.DirectFileDto
import com.wirelesscable.app.core.direct.DirectProtocol
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 传应用这块的纯逻辑：安装包命名 + 共享清单的协议兼容。
 *
 * 命名看着是小事，但它直接决定收件方能不能把 base 和 split 认回同一个应用，
 * 也决定文件名会不会被收件方的路径校验拒掉（空格、斜杠、冒号都是雷）。
 */
class DirectAppTransferTest {

    @Test
    fun partName_usesLabelForBaseAndPartSuffixForSplits() {
        assertEquals("微信.apk", InstalledApps.partName("微信", "com.tencent.mm", 0))
        assertEquals("微信-part1.apk", InstalledApps.partName("微信", "com.tencent.mm", 1))
        assertEquals("微信-part2.apk", InstalledApps.partName("微信", "com.tencent.mm", 2))
    }

    @Test
    fun partName_replacesCharactersThatBreakFileNames() {
        // 空格、斜杠、冒号在收件方会变成路径问题或被 uniqueTarget 改名
        assertEquals(
            "QQ_音乐-part1.apk",
            InstalledApps.partName("QQ 音乐", "com.tencent.qqmusic", 1),
        )
        assertEquals("a_b_c_d.apk", InstalledApps.partName("a/b:c*d", "com.x", 0))
    }

    @Test
    fun partName_fallsBackToPackageNameWhenLabelIsBlank() {
        assertEquals("com_foo_bar.apk", InstalledApps.partName("   ", "com.foo.bar", 0))
    }

    @Test
    fun partName_capsLengthSoFileNameStaysUsable() {
        val long = "很长的应用名".repeat(20)
        val name = InstalledApps.partName(long, "com.long", 0)
        assertTrue("名字过长：${name.length}", name.length <= 40 + ".apk".length)
        assertTrue(name.endsWith(".apk"))
    }

    @Test
    fun sharedFileDto_keepsGroupAndKind() {
        val dto = DirectFileDto(
            id = "f0",
            name = "微信-part1.apk",
            sizeBytes = 123,
            group = "com.tencent.mm",
            kind = DirectProtocol.KIND_APK_SPLIT,
        )

        val json = WcJson.instance.encodeToString(dto)
        val back = WcJson.instance.decodeFromString<DirectFileDto>(json)

        assertEquals("com.tencent.mm", back.group)
        assertEquals(DirectProtocol.KIND_APK_SPLIT, back.kind)
    }

    /** 老版本的共享清单没有这两个字段：新客户端必须当普通文件处理，不能炸。 */
    @Test
    fun sharedFileDto_decodesOldShapeAsPlainFile() {
        val old = """{"id":"f0","name":"a.txt","sizeBytes":10,"hashSha256":"","modifiedUnixMs":0}"""

        val dto = WcJson.instance.decodeFromString<DirectFileDto>(old)

        assertEquals("a.txt", dto.name)
        assertNull(dto.group)
        assertEquals(DirectProtocol.KIND_FILE, dto.kind)
    }
}
