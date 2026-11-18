package com.wirelesscable.app.core.direct

import android.content.Context
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSuggestion
import android.os.Build

/**
 * 把对方热点登记成"系统托管的网络建议"。
 *
 * 为什么需要它：`WifiNetworkSpecifier` 在部分 ROM 上会被直接拒（`onUnavailable`），
 * 或者弹一个用户很难注意到的确认框。网络建议走的是另一条路——登记之后由系统自己
 * 关联，应用不再需要"申请加入"，也就没有那次弹框。
 *
 * 旧原型（com.nearshare.share.ShareService.suggestNetwork）当年对电脑热点就是这么做的；
 * 这次把它作为 specifier 失败后的第二手段搬回来（1.5.62）。
 *
 * 注意：建议会**一直留在系统里**，不撤销的话手机会持续黏在这条没有外网的 Wi-Fi 上。
 * 所以离开直连页 / ViewModel 销毁时必须调 [remove]。
 */
object HotspotSuggester {

    private const val TAG = "WcDirect"

    /** 登记一条建议。已经有同 SSID 的建议时按成功处理。 */
    fun add(context: Context, ssid: String, passphrase: String): Boolean {
        if (ssid.isBlank() || passphrase.length !in 8..63) return false

        val manager = context.applicationContext.getSystemService(WifiManager::class.java)
            ?: return false

        return runCatching {
            val builder = WifiNetworkSuggestion.Builder()
                .setSsid(ssid)
                .setWpa2Passphrase(passphrase)

            // 优先级拉高，才不会在旁边有自家 Wi-Fi 时被系统挑走别的网络
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                builder.setPriority(100)
            }

            val status = manager.addNetworkSuggestions(listOf(builder.build()))
            DirectLog.i("热点建议：status=$status ssid=$ssid")

            status == WifiManager.STATUS_NETWORK_SUGGESTIONS_SUCCESS ||
                status == WifiManager.STATUS_NETWORK_SUGGESTIONS_ERROR_ADD_DUPLICATE
        }.onFailure {
            DirectLog.w("登记热点建议失败：${it.message}")
        }.getOrDefault(false)
    }

    /** 撤销本应用登记过的全部建议。幂等，没有建议时什么也不做。 */
    fun remove(context: Context) {
        val manager = context.applicationContext.getSystemService(WifiManager::class.java)
            ?: return

        val mine = runCatching { manager.networkSuggestions }.getOrNull().orEmpty()
        if (mine.isEmpty()) return

        runCatching {
            manager.removeNetworkSuggestions(mine)
            DirectLog.i("已撤销 ${mine.size} 条热点建议")
        }.onFailure {
            DirectLog.w("撤销热点建议失败：${it.message}")
        }
    }
}
