package com.wirelesscable.app.core.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiNetworkSpecifier
import android.util.Log
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.coroutines.resume

/**
 * 连接电脑开的移动热点（兜底通道）。
 *
 * 只在"手机与电脑不在同一网络、且手机按二维码里的局域网地址一个都连不上"时才用。
 * 走 [WifiNetworkSpecifier] 而不是让用户去系统设置里手输密码：扫码即连，
 * 系统会弹一次确认框（Android 10 起对点对点 Wi-Fi 请求的硬要求）。
 *
 * 连上之后必须 [ConnectivityManager.bindProcessToNetwork]：
 * 手机这时通常还连着原 Wi-Fi + 移动数据，默认网络仍在原网卡上，
 * 到热点网段（192.168.137.x）的路由不在默认网络里，不绑定就发不出去。
 */
object HotspotConnector {

    private const val TAG = "WcHotspot"

    private var manager: ConnectivityManager? = null
    private var callback: ConnectivityManager.NetworkCallback? = null
    private var bound: Network? = null

    /** 进程当前是否已绑定到电脑热点。 */
    val isBound: Boolean get() = bound != null

    fun initialize(context: Context) {
        manager = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    }

    /**
     * 连接指定热点。返回 true 表示已连上并已把进程绑定过去。
     * 超时、用户拒绝、热点不存在都返回 false（调用方负责给出可读提示）。
     */
    suspend fun connect(ssid: String, passphrase: String, timeoutMs: Long = 30_000): Boolean {
        val cm = manager ?: return false
        release()

        val specifier = try {
            WifiNetworkSpecifier.Builder()
                .setSsid(ssid)
                .setWpa2Passphrase(passphrase)
                .build()
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "热点名称或密码不合法", e)
            return false
        }

        // 不要求 NET_CAPABILITY_INTERNET：电脑热点多数没有独立外网出口，
        // 一旦要求"可上网"，系统会一直判定该网络不合格。
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .setNetworkSpecifier(specifier)
            .build()

        val network = withTimeoutOrNull(timeoutMs) {
            suspendCancellableCoroutine<Network?> { continuation ->
                val networkCallback = object : ConnectivityManager.NetworkCallback() {
                    override fun onAvailable(network: Network) {
                        if (continuation.isActive) continuation.resume(network)
                    }

                    override fun onUnavailable() {
                        if (continuation.isActive) continuation.resume(null)
                    }

                    override fun onLost(network: Network) {
                        // 热点自己消失（电脑关热点/走远）时解除绑定，别让整个进程断网
                        if (bound == network) {
                            bound = null
                            runCatching { cm.bindProcessToNetwork(null) }
                        }
                    }
                }

                callback = networkCallback

                try {
                    cm.requestNetwork(request, networkCallback)
                } catch (e: SecurityException) {
                    Log.w(TAG, "没有 Wi-Fi 相关权限，无法自动连热点", e)
                    callback = null
                    if (continuation.isActive) continuation.resume(null)
                }

                continuation.invokeOnCancellation {
                    runCatching { cm.unregisterNetworkCallback(networkCallback) }
                    if (callback === networkCallback) {
                        callback = null
                    }
                }
            }
        }

        if (network == null) {
            Log.w(TAG, "连不上电脑热点 ssid=$ssid")
            release()
            return false
        }

        return try {
            cm.bindProcessToNetwork(network)
            bound = network
            Log.i(TAG, "已连上电脑热点 ssid=$ssid")
            true
        } catch (e: Exception) {
            Log.w(TAG, "绑定热点网络失败", e)
            release()
            false
        }
    }

    /** 解除绑定并撤销请求（断开该热点）。 */
    fun release() {
        val cm = manager
        if (bound != null && cm != null) {
            runCatching { cm.bindProcessToNetwork(null) }
        }
        bound = null

        val cb = callback
        callback = null
        if (cb != null && cm != null) {
            runCatching { cm.unregisterNetworkCallback(cb) }
        }
    }
}
