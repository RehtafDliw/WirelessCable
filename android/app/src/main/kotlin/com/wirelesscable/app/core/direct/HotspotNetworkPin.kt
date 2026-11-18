package com.wirelesscable.app.core.direct

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import java.net.Inet4Address

/**
 * 会话期间把"那条没有外网的 Wi-Fi"钉住。
 *
 * 系统会给没有 INTERNET 能力的 Wi-Fi 打很低的分：息屏、或打开"WLAN 助理/自动切换"
 * 之后，默认网络会被切到蜂窝；激进的 ROM 直接把这张网卡拆掉 —— 传输中途断。
 * 前台服务、WakeLock、WifiLock 分别管进程、CPU、射频，都管不到 ConnectivityService
 * 的网络选择。应用侧唯一能阻止它的办法，是始终挂一条**不带 NET_CAPABILITY_INTERNET**
 * 的 NetworkRequest：有请求在，系统就不会因为"验证失败"回收这张网卡。
 *
 * 与旧原型（com.nearshare.wifi.NetworkPin）的区别：这里**不**常驻 bindProcessToNetwork。
 * 进程级绑定会让与电脑的心跳/索引也走这张没有外网的网卡（1.5.58 踩过），
 * 所以它只负责"保命"和"报告当前 Network"，实际 socket 仍由 network.socketFactory 指定。
 */
class HotspotNetworkPin(
    context: Context,
    /** 热点网段前三段，例如 192.168.210 */
    private val subnetPrefix: String,
    /** 系统重新关联热点时 Network 对象会换新的，这里把新的报出去。 */
    private val onNetworkChanged: (Network) -> Unit = {},
    private val onLost: () -> Unit = {},
) {

    private val cm = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private var callback: ConnectivityManager.NetworkCallback? = null

    @Volatile
    var current: Network? = null
        private set

    val isActive: Boolean get() = callback != null

    fun start() {
        if (callback != null) return
        val manager = cm ?: return

        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()

        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = consider(network)

            override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) =
                consider(network)

            // 息屏时系统只改 capabilities（关联还在），这时也要重新确认一次
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) =
                consider(network)

            override fun onLost(network: Network) {
                if (network != current) return
                current = null
                onLost()
                // 系统可能已经建好替代的 Network 对象，顺手接上
                scanExisting()
            }
        }

        callback = cb
        runCatching { manager.requestNetwork(request, cb) }
            .onFailure { DirectLog.w("热点保活请求失败：${it.message}") }

        // requestNetwork 对已存在的网络不一定补 onAvailable，主动扫一遍
        scanExisting()
    }

    private fun consider(network: Network) {
        if (subnetPrefix.isEmpty()) return
        val properties = runCatching { cm?.getLinkProperties(network) }.getOrNull() ?: return

        val hit = properties.linkAddresses.any { link ->
            val address = link.address
            address is Inet4Address &&
                !address.isLoopbackAddress &&
                address.hostAddress?.substringBeforeLast('.') == subnetPrefix
        }
        if (!hit || current == network) return

        current = network
        onNetworkChanged(network)
    }

    private fun scanExisting() {
        runCatching { cm?.allNetworks?.forEach(::consider) }
    }

    fun stop() {
        callback?.let { runCatching { cm?.unregisterNetworkCallback(it) } }
        callback = null
        current = null
    }
}
