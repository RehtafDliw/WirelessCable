package com.wirelesscable.app

import com.wirelesscable.app.core.network.Fingerprint
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.MessageDigest

/**
 * 指纹算法必须与标准 SHA-256 十六进制完全一致：
 * 它是证书固定（pinning）的唯一依据，编错一个字符就会导致
 * "指纹永远不匹配"或更糟——匹配到错误证书。
 */
class FingerprintTest {

    private fun standardHex(bytes: ByteArray): String =
        bytes.joinToString("") { "%02x".format(it) }

    @Test
    fun sha256MatchesStandardEncoding() {
        val digest = MessageDigest.getInstance("SHA-256").digest("abc".toByteArray())

        assertEquals(standardHex(digest), Fingerprint.sha256("abc".toByteArray()))
    }

    @Test
    fun sha256CoversAllNibbles() {
        // 换一组输入，确保 0-9a-f 的映射整体正确（字母表被替换过会立刻暴露）
        val bytes = ByteArray(16) { it.toByte() }
        val digest = MessageDigest.getInstance("SHA-256").digest(bytes)

        val hex = Fingerprint.sha256(bytes)

        assertEquals(standardHex(digest), hex)
        // 被改坏的字母表无法输出 2/4/5/6，这里显式断言其存在
        assertTrue("SHA-256 结果应包含 2/4/5/6，实际 $hex", hex.any { it in "2456" })
    }

    @Test
    fun validFingerprintAcceptsRealServerValue() {
        // 真机联调时服务端实际广播的值，含 2/4/6，若字母表缺字符会被误判非法
        assertTrue(Fingerprint.isValid("9a2f86c43c32bf9ffa4f075c50cbf3442523c63195dd84aca63b7bbe55620db2"))
    }

    @Test
    fun normalizeStripsColonsAndUppercase() {
        assertEquals("9a2f86c4", Fingerprint.normalize("9A:2F:86:C4"))
    }

    @Test
    fun invalidLengthRejected() {
        assertFalse(Fingerprint.isValid("9a2f86c4"))
        assertFalse(Fingerprint.isValid("zz2f86c43c32bf9ffa4f075c50cbf3442523c63195dd84aca63b7bbe55620db2"))
    }
}
