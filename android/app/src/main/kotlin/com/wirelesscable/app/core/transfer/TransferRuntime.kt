package com.wirelesscable.app.core.transfer

import com.wirelesscable.app.WirelessCableApp
import com.wirelesscable.app.core.network.ServerEndpoint
import com.wirelesscable.app.core.network.TransferApi
import java.io.File

/**
 * 进程内唯一的传输引擎。
 *
 * 以前引擎由传输页的 ViewModel 自己创建，文件浏览页再把它借过来（`attachTransfers`）。
 * 0.6.5 起作业执行要在息屏/后台继续，执行者是前台服务而不是某个页面，
 * 于是引擎的归属必须挪到应用级：
 *
 * - 只允许一个实例。两个引擎同时跑会各自维护一份 Room 镜像与速度计，
 *   遇到同一个任务（比如电脑下发的 download 作业）就会互相覆盖进度。
 * - 按 (地址, 端口, 令牌) 缓存。重新配对会换令牌，旧引擎必须丢弃，
 *   否则会拿一个已作废的令牌反复撞门（0.5.x 踩过）。
 */
object TransferRuntime {

    private var app: WirelessCableApp? = null
    private var engine: TransferEngine? = null
    private var key: String? = null

    fun initialize(application: WirelessCableApp) {
        app = application
    }

    /** 取（或创建）当前会话的传输引擎。没有会话或令牌时返回 null。 */
    suspend fun ensure(): TransferEngine? {
        val application = app ?: return null
        val session = application.repository.loadSession() ?: return null
        val token = application.vault.load(session.deviceId) ?: return null

        val currentKey = "${session.host}:${session.port}|$token"
        engine?.let { existing -> if (key == currentKey) return existing }

        val created = TransferEngine(
            api = TransferApi.create(
                ServerEndpoint(session.host, session.port),
                setOf(session.fingerprint),
            ),
            token = token,
            resolver = application.contentResolver,
            db = application.database,
            tempDir = File(application.getExternalFilesDir(null), "transfers"),
        )

        engine = created
        key = currentKey
        return created
    }
}
