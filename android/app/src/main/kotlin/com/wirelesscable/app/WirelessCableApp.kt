package com.wirelesscable.app

import android.app.Application
import com.wirelesscable.app.core.direct.DirectLog
import com.wirelesscable.app.core.direct.HotspotSuggester
import com.wirelesscable.app.core.discovery.NsdDiscoveryService
import com.wirelesscable.app.core.network.HotspotConnector
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.core.transfer.TransferRuntime
import com.wirelesscable.app.data.db.AppDatabase
import com.wirelesscable.app.data.repository.ConnectionRepository
import com.wirelesscable.app.data.security.IdentityKeyStore
import com.wirelesscable.app.data.security.TokenVault

/**
 * 应用级容器。数据库与仓储只在这里创建一次，
 * UI 通过 ViewModel 访问，不自行 new 数据库。
 */
class WirelessCableApp : Application() {

    val database: AppDatabase by lazy { AppDatabase.get(this) }

    val repository: ConnectionRepository by lazy { ConnectionRepository(database) }

    /** 设备令牌保险箱（Keystore AES-GCM）。 */
    val vault: TokenVault by lazy { TokenVault(this) }

    /** 长期身份密钥（Keystore ECDSA，私钥不可导出）。 */
    val identityKeyStore: IdentityKeyStore by lazy { IdentityKeyStore() }

    /** mDNS/NSD 发现。 */
    val discovery: NsdDiscoveryService by lazy { NsdDiscoveryService(this) }

    override fun onCreate() {
        super.onCreate()

        // 直连日志要在任何一次直连尝试之前定下落点：
        // 公开目录写不进去（分区存储/没给存储权限）时自动落到应用私有目录，
        // 否则整台手机看起来"压根没产生日志"（1.5.54）。
        DirectLog.init(this)

        // 会话同步（心跳/索引/作业）与传输引擎也必须应用级：
        // 息屏时执行作业的是前台服务，不是某个页面，页面被回收不能把循环带走。
        SessionSyncEngine.initialize(this)
        TransferRuntime.initialize(this)

        // 电脑热点的兜底连接（扫码连不上现有网络时才用）
        HotspotConnector.initialize(this)

        // 上次会话没正常退出时，直连热点可能还以"网络建议"的形式留在系统里：
        // 手机会一直黏在那条没有外网的 Wi-Fi 上。启动时清一次（只清本应用登记的）。
        HotspotSuggester.remove(this)
    }
}
