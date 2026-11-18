package com.wirelesscable.app.data.repository

import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.data.db.AppDatabase
import com.wirelesscable.app.data.db.ConnectionEntity
import com.wirelesscable.app.data.db.DeviceEntity
import kotlinx.coroutines.flow.Flow

/** 数据访问门面。UI 与网络层都不直接碰 Room。 */
class ConnectionRepository(database: AppDatabase) {

    private val deviceDao = database.deviceDao()
    private val connectionDao = database.connectionDao()
    private val transferDao = database.transferDao()
    private val settingsDao = database.settingsDao()

    val devices: Flow<List<DeviceEntity>> = deviceDao.observeAll()
    val latestConnection: Flow<ConnectionEntity?> = connectionDao.observeLatest()
    val recentTransfers = transferDao.observeRecent()

    suspend fun rememberEndpoint(endpoint: ServerEndpoint, fingerprint: String) {
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_HOST, endpoint.host))
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_PORT, endpoint.port.toString()))
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_FINGERPRINT, fingerprint))
    }

    suspend fun loadHost(): String? = settingsDao.get(KEY_HOST)

    suspend fun loadPort(): String? = settingsDao.get(KEY_PORT)

    suspend fun loadFingerprint(): String? = settingsDao.get(KEY_FINGERPRINT)

    suspend fun allDevices(): List<DeviceEntity> = deviceDao.getAll()

    suspend fun savePairedDevice(device: DeviceEntity) = deviceDao.upsert(device)

    /** 改电脑的显示名（只影响本机显示）。 */
    suspend fun renameDevice(deviceId: String, name: String) = deviceDao.rename(deviceId, name)

    suspend fun forgetDevice(deviceId: String) {
        deviceDao.delete(deviceId)
        if (settingsDao.get(KEY_SESSION_DEVICE) == deviceId) {
            // 会话与"当前端点"都指向这台电脑，一起清掉；
            // 否则设备列表已经空了，设置页还显示"已配对"。
            for (key in listOf(
                KEY_SESSION_DEVICE, KEY_SESSION_HOST, KEY_SESSION_PORT, KEY_SESSION_FINGERPRINT,
                KEY_HOST, KEY_PORT, KEY_FINGERPRINT,
            )) {
                settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(key, ""))
            }
        }
    }

    /**
     * 保存最近一次成功配对的会话信息，供下次启动自动连接。
     *
     * 这里同时写 `connection.*`：设置页判断"是否已配对"读的是 `connection.fingerprint`，
     * 而扫码配对只会走到这个方法。以前两套键各写各的，扫码配对后手机上显示
     * "未配对"、反向服务按钮被禁用，电脑端却记录得好好的。
     */
    suspend fun saveSession(deviceId: String, host: String, port: Int, fingerprint: String) {
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_SESSION_DEVICE, deviceId))
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_SESSION_HOST, host))
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_SESSION_PORT, port.toString()))
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_SESSION_FINGERPRINT, fingerprint))

        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_HOST, host))
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_PORT, port.toString()))
        settingsDao.put(com.wirelesscable.app.data.db.AppSettingsEntity(KEY_FINGERPRINT, fingerprint))
    }

    suspend fun loadSession(): SavedSession? {
        val deviceId = settingsDao.get(KEY_SESSION_DEVICE).orEmpty()
        val host = settingsDao.get(KEY_SESSION_HOST).orEmpty()
        val port = settingsDao.get(KEY_SESSION_PORT)?.toIntOrNull() ?: return null
        val fingerprint = settingsDao.get(KEY_SESSION_FINGERPRINT).orEmpty()
        if (deviceId.isEmpty() || host.isEmpty() || fingerprint.isEmpty()) return null
        return SavedSession(deviceId, host, port, fingerprint)
    }

    suspend fun recordConnectedDevice(device: DeviceEntity, endpoint: ServerEndpoint, fingerprint: String, now: Long) {
        // 已存在则保留用户起的名字与配对状态：连一次就把昵称冲回电脑型号、把已配对冲成 false，
        // 会让"改过的名字"和"配对过的设备"看起来都白做了。
        val existing = deviceDao.get(device.deviceId)
        deviceDao.upsert(
            device.copy(
                displayName = existing?.displayName?.takeIf { it.isNotBlank() } ?: device.displayName,
                isPaired = existing?.isPaired ?: device.isPaired,
            ),
        )
        connectionDao.upsert(
            ConnectionEntity(
                endpoint = endpoint.baseUrl,
                host = endpoint.host,
                port = endpoint.port,
                certificateFingerprint = fingerprint,
                lastConnectedUtc = now,
            ),
        )
    }

    /** 已保存的会话。 */
    data class SavedSession(val deviceId: String, val host: String, val port: Int, val fingerprint: String)

    companion object {
        private const val KEY_SESSION_DEVICE = "session.deviceId"
        private const val KEY_SESSION_HOST = "session.host"
        private const val KEY_SESSION_PORT = "session.port"
        private const val KEY_SESSION_FINGERPRINT = "session.fingerprint"
        private const val KEY_HOST = "connection.host"
        private const val KEY_PORT = "connection.port"
        private const val KEY_FINGERPRINT = "connection.fingerprint"
    }
}
