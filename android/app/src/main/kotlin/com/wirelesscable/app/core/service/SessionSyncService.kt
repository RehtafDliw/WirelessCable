package com.wirelesscable.app.core.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import com.wirelesscable.app.MainActivity
import com.wirelesscable.app.R
import com.wirelesscable.app.core.sync.BackgroundSync
import com.wirelesscable.app.core.sync.BatteryOptimization
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.core.transfer.InboxReceiver
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 「与电脑保持连接」的前台服务。
 *
 * 存在理由（K43）：电脑端在「我的手机」里做删除/改名/保存时，手机需要一直有人在取作业。
 * 只是把循环挂在前台生命周期上，息屏就停——这不是"用户想要的功能没了"，
 * 而是"用户以为做完了、其实没做"。所以把它放进前台服务：
 *
 * - 通知常驻，用户随时看得见、点得停（停止即关闭这个设置）；
 * - 持有 Partial WakeLock + Wi-Fi 低延迟锁：息屏后 Wi-Fi 芯片会进省电模式，
 *   只持 WakeLock 实测仍然丢包（反向服务踩过，见 android-compatibility.md）；
 * - 与反向服务分开两个服务、两条通知：一个监听入站，一个维持出站轮询，
 *   用户可能只想开其中一个。
 */
class SessionSyncService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var ticker: Job? = null

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                // 先落标记：setEnabled(false) 内部还会调一次 stop()，
                // isRunning 为真就会有回环（真机点通知会卡住）
                isRunning = false
                // 从通知里停 = 用户关掉这个设置。否则下次回到应用又会被拉起来。
                BackgroundSync.setEnabled(this, false)
                shutdown()
                return START_NOT_STICKY
            }

            else -> startInternal()
        }

        return START_NOT_STICKY
    }

    private fun startInternal() {
        if (isRunning) return

        // 先提成前台再干活：Android 12+ 后台启动前台服务会被直接拒掉
        val notification = buildNotification(SessionSyncEngine.State())

        // `specialUse` 这个类型是 Android 14 才有的。在 30–33 上传它会被判成
        // "不是 manifest 声明类型的子集" 而抛 IllegalArgumentException（服务直接起不来），
        // 所以只有 34+ 才带类型；更低的版本用两参数版本，系统按旧规则处理。
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }

        acquireLocks()
        SessionSyncEngine.acquire(HOLDER)
        isRunning = true
        startTicker()
    }

    private fun startTicker() {
        ticker?.cancel()
        ticker = scope.launch {
            while (true) {
                // 看门狗：循环死了/卡住了就重新拉起，别让"通知还在、无人干活"挂着
                SessionSyncEngine.ensureAlive()

                val snapshot = SessionSyncEngine.state.value
                maybeBalloon()
                withContext(Dispatchers.Main) {
                    notificationManager()?.notify(NOTIFICATION_ID, buildNotification(snapshot))
                }

                delay(TICK_INTERVAL_MS)
            }
        }
    }

    private fun shutdown() {
        ticker?.cancel()
        ticker = null
        isRunning = false

        scope.launch {
            SessionSyncEngine.release(HOLDER)
            releaseLocks()
            withContext(Dispatchers.Main) {
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
            }
        }
    }

    override fun onDestroy() {
        ticker?.cancel()
        isRunning = false
        // 服务被系统回收时也要把持有者还回去，否则引擎会一直以为服务还在
        SessionSyncEngine.release(HOLDER)
        releaseLocks()
        scope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    // ------------------------------------------------------------ 通知

    private fun buildNotification(snapshot: SessionSyncEngine.State): Notification {
        val inbox = InboxReceiver.state.value
        val received = inbox.freshNotice()

        // 接收状态优先说：这是用户唯一能"看见后台在干活"的地方。
        // 不显示的话，文件已经收完了用户还在等，或者接收被权限挡住也不知道为什么。
        val text = when {
            inbox.receiving != null ->
                "正在接收「${inbox.receiving}」 ${(inbox.progress * 100).toInt()}%"

            received != null -> "已接收「$received」到 下载/WirelessCable"

            inbox.blocked -> "需要「所有文件访问权」才能接收电脑发来的文件"

            // 未加电池白名单时优先说这件事：息屏后系统会冻住循环，
            // 不改文案的话用户只会看到"电脑可以随时查看"，然后发现根本没反应。
            !BatteryOptimization.isIgnoring(this) -> "后台可能被系统限制，点此允许后台运行"
            snapshot.lastError != null -> snapshot.lastError
            snapshot.connected -> "电脑可以随时查看与操作手机文件"
            else -> "正在连接电脑…"
        }

        val openApp = openAppIntent()

        val stop = PendingIntent.getService(
            this,
            1,
            Intent(this, SessionSyncService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("与电脑保持连接")
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_stat_service)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            // 常驻但彻底安静：不响、不亮屏、不显示时间戳，也不带角标
            .setSilent(true)
            .setShowWhen(false)
            .setContentIntent(openApp)
            .addAction(0, "关闭", stop)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    // ------------------------------------------------------------ 气球

    /** 已经弹过的"收到文件"时间戳、缺权限状态、上一条连接错误——用来防止每 5 秒重复弹。 */
    private var lastReceivedAtMs = 0L
    private var lastBlockedShown = false
    private var lastErrorShown: String? = null

    /**
     * 事件通知：弹一下（heads-up），5 秒后自己收回去。
     *
     * 常驻通知只有一条（前台服务那条，系统强制），而且降到了 IMPORTANCE_MIN；
     * 剩下"用户真正需要知道"的事——收到文件、缺权限、连接断了——都走这里，
     * 弹出即走，不在通知栏里积。
     */
    private fun maybeBalloon() {
        val inbox = InboxReceiver.state.value

        if (inbox.lastReceived != null && inbox.lastReceivedAtMs > lastReceivedAtMs) {
            lastReceivedAtMs = inbox.lastReceivedAtMs
            balloon("已接收「${inbox.lastReceived}」", "保存在 下载/WirelessCable")
            return
        }

        if (inbox.blocked && !lastBlockedShown) {
            lastBlockedShown = true
            balloon("无法接收文件", "需要「所有文件访问权」")
            return
        }
        if (!inbox.blocked) {
            lastBlockedShown = false
        }

        val error = SessionSyncEngine.state.value.lastError
        if (error != null && error != lastErrorShown) {
            lastErrorShown = error
            balloon("连接断开", error)
            return
        }
        if (error == null) {
            lastErrorShown = null
        }
    }

    private fun balloon(title: String, text: String) {
        val openApp = openAppIntent()

        val notification = NotificationCompat.Builder(this, EVENT_CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_stat_service)
            .setAutoCancel(true)
            .setContentIntent(openApp)
            // 5 秒自动消失：这是"气球"，不是"通知"
            .setTimeoutAfter(BALLOON_TTL_MS)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .build()

        // 固定 id：连着收好几个文件时后一个顶掉前一个，不会堆一屏
        notificationManager()?.notify(BALLOON_ID, notification)
    }

    private fun openAppIntent(): PendingIntent = PendingIntent.getActivity(
        this,
        0,
        Intent(this, MainActivity::class.java),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return

        val manager = notificationManager() ?: return

        // 旧通道建好之后级别改不了，只能删掉换新的。
        // wc_reverse_service 是更早版本（手机还监听端口时）遗留的，代码里早就不用了，
        // 但会一直挂在系统设置的通知列表里，一并清掉。
        manager.deleteNotificationChannel(LEGACY_CHANNEL_ID)
        manager.deleteNotificationChannel("wc_reverse_service")

        // 前台服务那条：降到 IMPORTANCE_MIN —— 不响、不弹、不占状态栏图标，
        // 只在通知栏最底下折叠着（Android 强制要求前台服务必须有一条通知）。
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "后台连接", NotificationManager.IMPORTANCE_MIN).apply {
                description = "息屏时仍与电脑保持连接；系统要求前台服务必须有一条通知，已降到最低"
                setShowBadge(false)
                setSound(null, null)
                enableVibration(false)
            },
        )

        // 事件：heads-up 气球，5 秒自动消失
        manager.createNotificationChannel(
            NotificationChannel(EVENT_CHANNEL_ID, "事件提醒", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "收到文件、连接断开等，弹一次，5 秒后自动消失"
                setShowBadge(false)
                setSound(null, null)
                enableVibration(false)
            },
        )
    }

    private fun notificationManager(): NotificationManager? =
        getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager

    // ------------------------------------------------------------ 锁
    //
    // 与反向服务的锁分开持有：两条链路可能只开其中一条，
    // 共用一个引用计数反而容易出现"一个停了另一个还在但锁没了"。

    private fun acquireLocks() {
        if (wakeLock == null) {
            val power = getSystemService(Context.POWER_SERVICE) as? PowerManager
            if (power != null) {
                wakeLock = power
                    .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "WirelessCable:session-sync")
                    .apply { setReferenceCounted(false) }
            }
        }

        runCatching { wakeLock?.takeIf { !it.isHeld }?.acquire() }
            .onFailure { Log.w(TAG, "WakeLock 获取失败", it) }

        if (wifiLock == null) {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            if (wifi != null) {
                wifiLock = runCatching {
                    wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "WirelessCable:session-wifi")
                        .apply { setReferenceCounted(false) }
                }.getOrNull()
            }
        }

        runCatching { wifiLock?.takeIf { !it.isHeld }?.acquire() }
            .onFailure { Log.w(TAG, "WifiLock 获取失败", it) }
    }

    private fun releaseLocks() {
        runCatching { wakeLock?.takeIf { it.isHeld }?.release() }
            .onFailure { Log.w(TAG, "WakeLock 释放失败", it) }
        runCatching { wifiLock?.takeIf { it.isHeld }?.release() }
            .onFailure { Log.w(TAG, "WifiLock 释放失败", it) }
    }

    companion object {
        private const val TAG = "WcSessionSync"

        /** 服务是否在跑。避免为了"停一个没在跑的服务"把它短暂创建一次。 */
        @Volatile
        private var isRunning = false
        /** 前台服务通知（IMPORTANCE_MIN）。改级别只能换通道，v2 是这么来的。 */
        private const val CHANNEL_ID = "wc_session_sync_v2"

        /** 老通道，创建时是 LOW；创建后级别改不了，只能删掉。 */
        private const val LEGACY_CHANNEL_ID = "wc_session_sync"

        /** 事件气球通道。 */
        private const val EVENT_CHANNEL_ID = "wc_events"

        private const val NOTIFICATION_ID = 4712

        /** 气球通知用一个固定 id：连着来几条时互相顶掉，不堆叠。 */
        private const val BALLOON_ID = 4713

        /** 气球停留时间。 */
        private const val BALLOON_TTL_MS = 5_000L
        private const val TICK_INTERVAL_MS = 5_000L
        private const val HOLDER = "service"

        const val ACTION_START = "com.wirelesscable.app.action.START_SESSION_SYNC"
        private const val ACTION_STOP = "com.wirelesscable.app.action.STOP_SESSION_SYNC"

        /** 由界面调用：开启（需要在前台时调用）。 */
        fun start(context: Context) {
            val intent = Intent(context, SessionSyncService::class.java).setAction(ACTION_START)
            runCatching { context.startForegroundService(intent) }
                .onFailure { Log.w(TAG, "无法启动后台连接服务", it) }
        }

        /** 由界面或通知调用：停止。没在跑就什么都不做。 */
        fun stop(context: Context) {
            if (!isRunning) return

            val intent = Intent(context, SessionSyncService::class.java).setAction(ACTION_STOP)
            runCatching { context.startService(intent) }
                .onFailure { Log.w(TAG, "无法停止后台连接服务", it) }
        }
    }
}
