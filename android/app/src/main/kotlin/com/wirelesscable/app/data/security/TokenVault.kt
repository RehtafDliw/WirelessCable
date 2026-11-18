package com.wirelesscable.app.data.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Log
import java.security.GeneralSecurityException
import java.security.KeyStore
import java.util.Base64
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 设备令牌保险箱：AES-256-GCM 密钥存放在 Android Keystore（不可导出），
 * 令牌密文存 SharedPreferences。deviceId 作为 AAD 绑定，防止串用。
 */
class TokenVault(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun store(deviceId: String, token: String) {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        cipher.updateAAD(deviceId.toByteArray(Charsets.UTF_8))

        val ciphertext = cipher.doFinal(token.toByteArray(Charsets.UTF_8))
        val nonce = cipher.iv
        val blob = Base64.getEncoder().encodeToString(nonce + ciphertext)

        prefs.edit().putString(keyOf(deviceId), blob).apply()
    }

    /**
     * 读令牌。
     *
     * **只有"密文确实坏了"才删**（GCM 校验失败、Base64 非法）——那是换钥匙或数据被改，
     * 留着也没用。Keystore 暂时不可用（锁屏后系统限制、Provider 繁忙）时必须保留密文：
     * 以前一把异常全删，息屏一次就把用户变成"未配对"，而且再也连不回去，
     * 只能重新扫码（1.5.6 真机踩到）。
     */
    fun load(deviceId: String): String? {
        val blob = prefs.getString(keyOf(deviceId), null) ?: return null

        return try {
            val bytes = Base64.getDecoder().decode(blob)
            val nonce = bytes.copyOfRange(0, GCM_NONCE_SIZE)
            val ciphertext = bytes.copyOfRange(GCM_NONCE_SIZE, bytes.size)

            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), GCMParameterSpec(GCM_TAG_BITS, nonce))
            cipher.updateAAD(deviceId.toByteArray(Charsets.UTF_8))
            String(cipher.doFinal(ciphertext), Charsets.UTF_8)
        } catch (e: AEADBadTagException) {
            Log.w(TAG, "令牌密文无法解密，按未配对处理", e)
            prefs.edit().remove(keyOf(deviceId)).apply()
            null
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "令牌密文格式非法，按未配对处理", e)
            prefs.edit().remove(keyOf(deviceId)).apply()
            null
        } catch (e: GeneralSecurityException) {
            Log.w(TAG, "Keystore 暂时不可用，保留令牌稍后重试", e)
            null
        } catch (e: Exception) {
            // 其余情况同样按"暂时读不出来"处理：宁可下一轮再试，也不要静默退配对
            Log.w(TAG, "令牌读取失败，稍后重试", e)
            null
        }
    }

    fun clear(deviceId: String) {
        prefs.edit().remove(keyOf(deviceId)).apply()
    }

    private fun keyOf(deviceId: String) = "token.$deviceId"

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )

        return generator.generateKey()
    }

    private companion object {
        const val TAG = "WcVault"
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val ALIAS = "wirelesscable_token_key"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val GCM_TAG_BITS = 128
        const val GCM_NONCE_SIZE = 12
        const val PREFS = "wirelesscable_vault"
    }
}
