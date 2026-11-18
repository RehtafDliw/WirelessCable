package com.wirelesscable.app.core.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log
import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.core.protocol.ProtocolConstants
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 基于 Android NSD 的服务发现。
 *
 * 关键点：
 * - 以 deviceId（TXT 的 id 字段）为唯一键去重，绝不按 IP 去重；
 * - 服务下线或解析失败时移除，避免列表里留死条目；
 * - 回调在系统线程，统一收敛到 StateFlow 由 UI 订阅；
 * - **必须持有 MulticastLock**：Android 的 Wi-Fi 省电策略默认丢弃入站组播帧，
 *   不加锁时查询能发出去、应答收不回来，表现为"扫描一直空"（真机实测）。
 */
class NsdDiscoveryService(context: Context) {

    private val appContext = context.applicationContext
    private val nsdManager = appContext.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val wifiManager = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager

    private val _servers = MutableStateFlow<List<DiscoveredServer>>(emptyList())
    val servers: StateFlow<List<DiscoveredServer>> = _servers.asStateFlow()

    private val byDeviceId = LinkedHashMap<String, DiscoveredServer>()
    private val resolving = HashSet<String>()
    private var discoveryListener: NsdManager.DiscoveryListener? = null
    private var multicastLock: WifiManager.MulticastLock? = null

    val isScanning: Boolean get() = discoveryListener != null

    fun start() {
        if (discoveryListener != null) return

        acquireMulticastLock()

        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                Log.i(TAG, "NSD 发现已启动 $serviceType")
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                // 部分 ROM 返回的 serviceType 带尾点或大小写不同，不能直接字符串相等比较
                if (!sameServiceType(serviceInfo.serviceType)) {
                    Log.d(TAG, "忽略非目标服务 ${serviceInfo.serviceType}")
                    return
                }

                resolve(serviceInfo)
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                // 实例名形如 WirelessCable-<deviceId 前 8 位>，用于下线时兜底移除
                val suffix = serviceInfo.serviceName.substringAfterLast('-', "")
                if (suffix.isEmpty()) return

                synchronized(byDeviceId) {
                    val key = byDeviceId.keys.firstOrNull { it.startsWith(suffix, ignoreCase = true) }
                    if (key != null) {
                        byDeviceId.remove(key)
                        publishLocked()
                    }
                }
            }

            override fun onDiscoveryStopped(serviceType: String) {
                Log.i(TAG, "NSD 发现已停止 $serviceType")
            }

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "NSD 启动失败 error=$errorCode")
                discoveryListener = null
                releaseMulticastLock()
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "NSD 停止失败 error=$errorCode")
            }
        }

        discoveryListener = listener

        try {
            nsdManager.discoverServices(ProtocolConstants.SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "NSD 参数非法", e)
            discoveryListener = null
            releaseMulticastLock()
        }
    }

    fun stop() {
        val listener = discoveryListener ?: return
        discoveryListener = null

        try {
            nsdManager.stopServiceDiscovery(listener)
        } catch (e: IllegalArgumentException) {
            Log.w(TAG, "停止 NSD 失败", e)
        } finally {
            releaseMulticastLock()
        }
    }

    /** 释放系统资源；Activity 销毁时调用。 */
    fun close() {
        stop()
        releaseMulticastLock()
    }

    private fun sameServiceType(candidate: String?): Boolean =
        candidate?.trimEnd('.')?.equals(ProtocolConstants.SERVICE_TYPE, ignoreCase = true) == true

    /**
     * Wi-Fi 省电会丢弃入站组播帧，没有这个锁时 mDNS 应答收不到，
     * NSD 只会"启动成功但永远 0 结果"。锁只在扫描期间持有，避免额外耗电。
     */
    private fun acquireMulticastLock() {
        if (multicastLock != null) return

        val lock = try {
            wifiManager?.createMulticastLock("wc-nsd")?.apply {
                setReferenceCounted(false)
                acquire()
            }
        } catch (e: SecurityException) {
            Log.w(TAG, "无法获取组播锁（缺少权限）", e)
            null
        }

        multicastLock = lock
        Log.i(TAG, if (lock != null) "已获取 Wi-Fi 组播锁" else "组播锁不可用，退化为直连配对")
    }

    private fun releaseMulticastLock() {
        val lock = multicastLock ?: return
        multicastLock = null

        try {
            if (lock.isHeld) lock.release()
            Log.i(TAG, "已释放 Wi-Fi 组播锁")
        } catch (e: RuntimeException) {
            Log.w(TAG, "释放组播锁失败", e)
        }
    }

    private fun resolve(serviceInfo: NsdServiceInfo) {
        val name = serviceInfo.serviceName
        synchronized(resolving) {
            if (!resolving.add(name)) return
        }

        try {
            @Suppress("DEPRECATION")
            nsdManager.resolveService(serviceInfo, object : NsdManager.ResolveListener {
                override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                    Log.w(TAG, "解析失败 ${info.serviceName} error=$errorCode")
                    synchronized(resolving) { resolving.remove(name) }
                }

                override fun onServiceResolved(info: NsdServiceInfo) {
                    synchronized(resolving) { resolving.remove(name) }

                    val attributes = info.attributes ?: emptyMap()
                    val deviceId = attributes["id"]?.toString(Charsets.UTF_8)
                    val host = info.host?.hostAddress

                    // 注意：部分 ROM 的 mdnsd 会截断 TXT，键多时最后几个会丢（实测 addrs 丢失），
                    // 因此地址不能依赖 TXT 传递，只能靠 A 记录。
                    Log.d(TAG, "解析 ${info.serviceName} host=$host keys=${attributes.size}")

                    if (deviceId.isNullOrBlank() || host.isNullOrBlank()) {
                        Log.w(TAG, "解析结果缺少 id 或地址，忽略 ${info.serviceName}")
                        return
                    }

                    val discovered = DiscoveredServer(
                        deviceId = deviceId,
                        displayName = attributes["name"]?.toString(Charsets.UTF_8) ?: info.serviceName,
                        host = host,
                        port = info.port,
                        fingerprint = attributes["fp"]?.toString(Charsets.UTF_8).orEmpty(),
                        protocolVersion = attributes["proto"]?.toString(Charsets.UTF_8)?.toIntOrNull() ?: 1,
                        candidates = DiscoveryTxt.candidates(
                            host,
                            attributes["addrs"]?.toString(Charsets.UTF_8),
                        ),
                    )

                    synchronized(byDeviceId) {
                        byDeviceId[deviceId] = discovered
                        publishLocked()
                    }
                }
            })
        } catch (e: IllegalArgumentException) {
            synchronized(resolving) { resolving.remove(name) }
            Log.w(TAG, "resolveService 调用失败", e)
        }
    }

    private fun publishLocked() {
        _servers.value = byDeviceId.values.sortedByDescending { it.lastSeenElapsedMs }
    }

    private companion object {
        const val TAG = "WcNsd"
    }
}
