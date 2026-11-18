package com.wirelesscable.app.core.direct

import android.content.Context
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.net.Inet4Address
import java.net.NetworkInterface
import kotlin.coroutines.resume

/**
 * 出码方自己开一个临时热点，让扫码方连过来。
 *
 * 为什么需要它：办公/公共 AP 的"客户端互访"经常是坏的——实测一加→电脑（有线）26 ms，
 * 一加→三星（同一个 AP）平均 110 ms、峰值 630 ms、抖动 44 ms，传大文件时 TCP 拥塞窗口
 * 反复塌，几十分钟传不完一个文件。热点是两台手机之间的专用链路（RTT 通常 2–5 ms），
 * 不经过那个 AP，也就没有这个问题（1.5.20，K61）。
 *
 * 代价：开热点会让部分机型临时断开原来的 Wi-Fi（芯片不支持 STA+AP 并发时），
 * 所以这是**用户显式打开**的选项，不是默认行为。
 *
 * 1.5.50 现场：有 ROM 会先回调 `onStarted`（此时 Wi-Fi 已经关了），随后才发现 AP 起不来
 * （或者立刻 `onStopped`）。旧代码只看 `onStarted` 就认为成功，界面上写着"已开热点"，
 * 实际既没热点、又没了 Wi-Fi —— 所以这里把"成功"判得更严：
 * 空 SSID 算失败、地址必须是**开热点后新出现的**、还要稳定存活一段时间。
 */
object HotspotController {

    private const val TAG = "WcDirect"
    private const val START_TIMEOUT_MS = 20_000L
    private const val ADDRESS_TIMEOUT_MS = 8_000L

    /** `onStarted` 之后还要活这么久才算真的开了（有的 ROM 会紧接着 `onStopped`）。 */
    private const val STABILITY_MS = 1_200L

    /** 接口名完全不像热点时的兜底分：排在所有已知 AP 接口之后，但仍参与候选。 */
    private const val UNKNOWN_NIC_SCORE = 9

    private var reservation: WifiManager.LocalOnlyHotspotReservation? = null
    private var cached: DirectHotspot? = null

    private val _active = MutableStateFlow(false)

    /** 热点是否真的在运行。系统随时可能因为 ROM 策略把它关掉，界面要能跟着变。 */
    val active: StateFlow<Boolean> = _active.asStateFlow()

    val isActive: Boolean get() = _active.value

    /**
     * 是否具备开热点所需的权限（各版本要求不同，缺一个就明确说）。
     *
     * 判定统一走 [HotspotPermissions]：13+ 上「附近的设备」和「定位」有一个就够，
     * 以前这里只认定位，只授了「附近的设备」的机器会被自己挡在门外（1.5.57）。
     */
    fun missingPermission(context: Context): String? {
        if (!HotspotPermissions.canUseHotspot(context)) {
            DirectLog.w("开热点缺权限：${HotspotPermissions.describe(context)}")
            return if (HotspotPermissions.hasNearby) {
                "需要「附近的设备」权限才能开热点（系统限制，热点本身不采集位置）"
            } else {
                "需要「定位」权限才能开热点（系统限制，热点本身不采集位置）"
            }
        }

        // 很多 ROM 还要求系统"定位"总开关是打开的；关着时 startLocalOnlyHotspot
        // 会直接失败，错误码还看不出原因（这是最常见的一类"开热点没反应"，1.5.53）。
        val location = context.getSystemService(Context.LOCATION_SERVICE)
            as? android.location.LocationManager
        if (location != null && !location.isLocationEnabled) {
            return "系统「定位」开关是关闭的，开热点需要它（系统限制，不采集位置）"
        }

        return null
    }

    /**
     * 开热点并返回 SSID / 口令 / AP 侧地址。
     *
     * 已经开着就直接复用——用户重复点「对方扫我」时不该反复开关热点。
     */
    suspend fun start(context: Context): Result<DirectHotspot> = withContext(Dispatchers.IO) {
        cached?.let { return@withContext Result.success(it) }

        missingPermission(context)?.let {
            return@withContext Result.failure(IllegalStateException(it))
        }

        val manager = context.applicationContext
            .getSystemService(Context.WIFI_SERVICE) as? WifiManager
            ?: return@withContext Result.failure(IllegalStateException("系统没有 Wi-Fi 服务"))

        // 开热点前先把现有地址记下来：之后"新冒出来的 IPv4"才是热点那一侧。
        // 不能只排除 activeNetwork —— 开热点会把 Wi-Fi 关掉，默认网络跟着变成移动数据，
        // 原来的 Wi-Fi 地址反而不再被排除，于是把残留地址当成热点地址（1.5.50 现场）。
        val before = localAddresses()

        val started = runCatching { requestHotspot(manager) }
            .getOrElse {
                DirectLog.w("开热点抛异常：${it.message}")
                return@withContext Result.failure(it)
            }

        val reservation = started.getOrElse {
            DirectLog.w("开热点失败：${it.message}")
            return@withContext Result.failure(it)
        }
        this@HotspotController.reservation = reservation

        // Android 11+ 必须用 `softApConfiguration`：
        // `wifiConfiguration` 早已 deprecated，部分 ROM（一加实测）返回的字段带引号、
        // 甚至给空密码 —— 照它填二维码，对方永远认证失败。
        // 这段是从最早的原型（com.nearshare.wifi.HotspotController）搬回来的，
        // 那边当时能正常开热点分享文件（1.5.55）。
        val ssid: String
        val passphrase: String
        if (android.os.Build.VERSION.SDK_INT >= 30) {
            val config = reservation.softApConfiguration
            ssid = config.ssid.orEmpty().trim().trim('"')
            passphrase = config.passphrase.orEmpty().trim().trim('"')
            DirectLog.i(
                "热点配置来源 softApConfiguration ssid=$ssid 密码长度=${passphrase.length}",
            )
        } else {
            @Suppress("DEPRECATION")
            val config = reservation.wifiConfiguration
            ssid = config?.SSID.orEmpty().trim().trim('"')
            passphrase = config?.preSharedKey.orEmpty().trim().trim('"')
            DirectLog.i(
                "热点配置来源 wifiConfiguration ssid=$ssid 密码长度=${passphrase.length}",
            )
        }

        // 空 SSID 说明系统根本没把热点配起来（不少 ROM 先关 Wi-Fi、再报错，
        // 而 onStarted 已经回调过了）。这时候再在二维码里写"热点已开"就是骗人。
        if (ssid.isEmpty()) {
            DirectLog.w("开热点返回了空 SSID，按失败处理（有些机型会先关 Wi-Fi 再失败）")
            stop()
            return@withContext Result.failure(
                IllegalStateException("系统没有真正开出热点（只关了 Wi-Fi），这台机型可能不支持 Wi-Fi 与热点同时工作"),
            )
        }

        // 有的 ROM 不返回 preSharedKey（空串）。空密码进不了 WifiNetworkSpecifier，
        // 对方一定加不进来 —— 与其出一张"能扫但连不上"的码，不如当场说清楚（1.5.52）。
        if (passphrase.length !in 8..63) {
            DirectLog.w("开热点返回的密码长度不对（${passphrase.length}），按失败处理")
            stop()
            return@withContext Result.failure(
                IllegalStateException("系统没有返回热点密码，对方无法加入；请关掉「用热点直连」用普通 Wi-Fi"),
            )
        }

        val address = awaitHotspotAddress(context, before) ?: run {
            stop()
            val message = "热点开了但找不到它的地址（接口名不在已知范围）"
            DirectLog.w(message)
            return@withContext Result.failure(IllegalStateException(message))
        }

        // 地址有了也别急着报成功：有的 ROM 会紧接着 onStopped，把刚开的热点收回去。
        delay(STABILITY_MS)
        if (!_active.value) {
            DirectLog.w("热点开了又被系统关掉 ssid=$ssid 地址=$address")
            stop()
            return@withContext Result.failure(
                IllegalStateException("热点开了又被系统立刻关掉（这台机型不支持 Wi-Fi 与热点同时工作）"),
            )
        }

        DirectLog.session("出热点 ssid=$ssid 地址=$address")

        val info = DirectHotspot(ssid = ssid, passphrase = passphrase, address = address)
        cached = info
        Result.success(info)
    }

    fun stop() {
        runCatching { reservation?.close() }
        reservation = null
        cached = null
        _active.value = false
        android.util.Log.i(TAG, "已关闭直连热点")
    }

    // ------------------------------------------------------------ 内部

    private suspend fun requestHotspot(
        manager: WifiManager,
    ): Result<WifiManager.LocalOnlyHotspotReservation> {
        val outcome = withTimeoutOrNull(START_TIMEOUT_MS) {
            suspendCancellableCoroutine { continuation ->
                val callback = object : WifiManager.LocalOnlyHotspotCallback() {
                    override fun onStarted(reservation: WifiManager.LocalOnlyHotspotReservation) {
                        if (continuation.isActive) {
                            _active.value = true
                            continuation.resume(Result.success(reservation))
                        } else {
                            // 已经超时/取消：热点白开了，关掉
                            runCatching { reservation.close() }
                        }
                    }

                    override fun onFailed(reason: Int) {
                        // 关键：成功之后系统仍可能回调 onFailed（先关 Wi-Fi 再失败）。
                        // 这时 continuation 已经不活跃，旧代码直接丢掉，界面就永远停在"已开热点"。
                        _active.value = false
                        DirectLog.w("热点启动失败/被停止：${reasonText(reason)}")
                        if (continuation.isActive) {
                            continuation.resume(
                                Result.failure(IllegalStateException(reasonText(reason))),
                            )
                        }
                    }

                    override fun onStopped() {
                        _active.value = false
                        cached = null
                        reservation = null
                        DirectLog.w("热点被系统停止")
                    }
                }

                try {
                    manager.startLocalOnlyHotspot(callback, Handler(Looper.getMainLooper()))
                } catch (error: Exception) {
                    if (continuation.isActive) {
                        continuation.resume(Result.failure(error))
                    }
                }
            }
        }

        return outcome ?: Result.failure(IllegalStateException("开热点超时（20 秒）"))
    }

    /**
     * 等热点那一侧的 IPv4 出现。
     *
     * 判据是**开热点前不存在**：[before] 是启动前的全部 IPv4，之后新出现的才算；
     * 名字像 AP 的接口（ap0 / swlan0 / wlan1 / softap0）优先，不认识的接口排后面。
     * 只认"接口名像 AP"会在部分 ROM 上永远找不到（它们复用 wlan0），
     * 只认"不是默认网络"又会在 Wi-Fi 被关掉后误判 —— 两者结合才稳（1.5.50）。
     */
    private suspend fun awaitHotspotAddress(context: Context, before: Set<String>): String? {
        val deadline = android.os.SystemClock.elapsedRealtime() + ADDRESS_TIMEOUT_MS

        while (android.os.SystemClock.elapsedRealtime() < deadline) {
            apAddresses(context, before).firstOrNull()?.let { return it }
            delay(250)
        }

        DirectLog.w(
            "热点地址等了 ${ADDRESS_TIMEOUT_MS}ms 还没出现，开热点前=${before.size} 个地址，" +
                "现有网卡：${describeInterfaces()}",
        )
        return null
    }

    /** 名字像热点的接口地址，按评分从高到低；不认识的接口排在最后（仍可能是热点）。 */
    private fun apAddresses(context: Context, before: Set<String>): List<String> {
        val station = stationAddresses(context)
        val scored = mutableListOf<Pair<Int, String>>()

        try {
            NetworkInterface.getNetworkInterfaces()?.toList()
                ?.filter { it.isUp && !it.isLoopback }
                ?.forEach { nic ->
                    nic.inetAddresses.toList()
                        .filterIsInstance<Inet4Address>()
                        .forEach { address ->
                            if (address.isLoopbackAddress || address.isLinkLocalAddress) {
                                return@forEach
                            }

                            val host = address.hostAddress ?: return@forEach

                            // 开热点前就有 → 不是热点；当前默认网络上的 → 也不是
                            if (host in before || host in station) {
                                return@forEach
                            }

                            val byName = apScore(nic.name)
                            val score = if (byName == 0) UNKNOWN_NIC_SCORE else byName
                            scored += score to host
                        }
                }
        } catch (error: Exception) {
            android.util.Log.w(TAG, "枚举热点地址失败", error)
        }

        return scored
            .sortedWith(compareBy({ it.first }, { preferGateway(it.second) }))
            .map { it.second }
    }

    /** 当前默认网络（原 Wi-Fi / 移动数据）的地址。只用来"排除"，不当候选。 */
    private fun stationAddresses(context: Context): Set<String> {
        val manager = context.getSystemService(android.net.ConnectivityManager::class.java)
            ?: return emptySet()

        val network = manager.activeNetwork ?: return emptySet()
        val properties = manager.getLinkProperties(network) ?: return emptySet()

        return properties.linkAddresses
            .mapNotNull { it.address.hostAddress }
            .toSet()
    }

    private fun localAddresses(): Set<String> = try {
        NetworkInterface.getNetworkInterfaces()?.toList()
            ?.filter { it.isUp && !it.isLoopback }
            ?.flatMap { nic ->
                nic.inetAddresses.toList()
                    .filterIsInstance<Inet4Address>()
                    .mapNotNull { it.hostAddress }
            }
            ?.toSet()
            .orEmpty()
    } catch (error: Exception) {
        android.util.Log.w(TAG, "读取现有地址失败", error)
        emptySet()
    }

    private fun describeInterfaces(): String = try {
        NetworkInterface.getNetworkInterfaces()?.toList()
            ?.filter { it.isUp && !it.isLoopback }
            ?.joinToString(", ") { nic ->
                val ips = nic.inetAddresses.toList()
                    .filterIsInstance<Inet4Address>()
                    .joinToString("/") { it.hostAddress.orEmpty() }
                "${nic.name}[$ips]"
            }
            .orEmpty()
    } catch (error: Exception) {
        "枚举失败"
    }

    /** 越像热点的接口分越低，排序时优先。 */
    private fun apScore(name: String): Int {
        val lower = name.lowercase()
        return when {
            // 各家 AP 接口名：ap0 / softap0 / swlan0 / wlan1 / wlan2 / p2p0 / 少数复用 wlan0
            lower.startsWith("ap") || lower.contains("softap") -> 1
            lower.contains("swlan") -> 2
            lower == "wlan1" || lower == "wlan2" -> 3
            lower.startsWith("p2p") || lower.contains("wifi-direct") -> 4
            lower == "wlan0" -> 5
            else -> 0
        }
    }

    /**
     * 同一网段里优先 `.1`：AP 网关几乎总是 .1。
     *
     * 一加实测把 AP 侧地址报成 `192.168.210.2`（1.5.52 现场），
     * 写进二维码后对方连这个地址不通，于是退回普通 Wi-Fi、又慢又卡。
     * 排序时把 .1 提到前面，扫码方也会同时试 .1（见 DirectViewModel 的热点分支）。
     */
    private fun preferGateway(host: String): Int = if (host.endsWith(".1")) 0 else 1

    private fun reasonText(reason: Int): String = when (reason) {
        WifiManager.LocalOnlyHotspotCallback.ERROR_NO_CHANNEL -> "没有可用信道，热点开不起来"
        WifiManager.LocalOnlyHotspotCallback.ERROR_INCOMPATIBLE_MODE -> "当前 Wi-Fi 模式不允许开热点"
        WifiManager.LocalOnlyHotspotCallback.ERROR_TETHERING_DISALLOWED -> "系统策略禁止开热点"
        else -> "开热点失败（错误码 $reason）"
    }
}
