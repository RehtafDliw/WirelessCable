package com.wirelesscable.app.core.direct

import android.net.Uri
import java.io.File
import java.security.SecureRandom

/**
 * 一次直连会话。
 *
 * 生命周期是硬的：出码方建会话 → 对方扫码连上 → 双方互发 → 任一方断开/退出/换网络就作废。
 * 所以这里没有"重连"、没有"续传"，只有一个 [State] 与过期时间。
 */
class DirectSession(
    val sid: String,
    val token: String,
    val deviceName: String,
    val identity: DirectCertificate.Identity,
    sharedFiles: List<SharedFile>,
    /** 对方推过来的文件落在这里。 */
    val inboxDirectory: File,
) {

    /** 共享文件。`id` 只在本会话内有效。 */
    data class SharedFile(
        val id: String,
        val name: String,
        val sizeBytes: Long,
        val uri: Uri,
        /** 传应用时是包名（同一应用的分体包共用），普通文件为 null。 */
        val group: String? = null,
        /** `DirectProtocol.KIND_*`。 */
        val kind: Int = 0,
    ) {
        /**
         * 已经完整发出去的字节数（并发分段时由各 worker 累加）。
         *
         * 之前每个分段都拿自己的 `起点+已发` 去上报，3 条流并行时数字在
         * 0+256K / 256K+256K / 512K+256K 之间来回跳，跟接收端完全对不上（1.5.22）。
         */
        val completedBytes: java.util.concurrent.atomic.AtomicLong = java.util.concurrent.atomic.AtomicLong(0L)

        /** 已经上报给界面的最大值：进度只能往前走，不能回退。 */
        val reportedBytes: java.util.concurrent.atomic.AtomicLong = java.util.concurrent.atomic.AtomicLong(0L)
    }

    // 列表要能被追加：出码时不必一次选全，连上之后再挑更顺手。
    // 服务端每条请求都会遍历它，所以读写都过 synchronized，避免并发修改异常。
    private val sharedList = java.util.Collections.synchronizedList(sharedFiles.toMutableList())
    private val nextSharedId = java.util.concurrent.atomic.AtomicInteger(sharedFiles.size)

    /** 出码方共享出去的文件（对方可拉）。 */
    val sharedFiles: List<SharedFile>
        get() = synchronized(sharedList) { sharedList.toList() }

    /** 追加一个共享文件，id 接着现有编号往下排，不会和旧的撞。 */
    fun addShared(
        name: String,
        sizeBytes: Long,
        uri: Uri,
        /** 传应用时是包名；普通文件为 null。 */
        group: String? = null,
        kind: Int = 0,
    ): SharedFile {
        val file = SharedFile(
            id = "f${nextSharedId.getAndIncrement()}",
            name = name,
            sizeBytes = sizeBytes,
            uri = uri,
            group = group,
            kind = kind,
        )
        sharedList += file
        return file
    }

    enum class State {
        /** 二维码已出，还没有人连上来。 */
        Waiting,

        /** 对端已连上（至少完成过一次鉴权请求）。 */
        Connected,

        /** 已关闭：断开、超时、换网络、退出。 */
        Closed,
    }

    @Volatile
    var state: State = State.Waiting
        private set

    val createdAtMs: Long = System.currentTimeMillis()

    @Volatile
    var connectedAtMs: Long = 0

        private set

    @Volatile
    var lastActivityMs: Long = createdAtMs

        private set

    /**
     * 已收到的文件（对方推过来的），用于会话页展示。
     *
     * 只在锁里读写：追加发生在 HTTP 连接线程，读取发生在会话 ticker 与界面。
     * 直接 `toList()` 一个正在被追加的 ArrayList 会抛 ConcurrentModificationException
     * （对方连开两条连接并行推文件时就能撞上）。
     */
    private val received = mutableListOf<ReceivedFile>()

    /**
     * 收到的文件。`path` 是落盘位置——界面上"安装"要用它，
     * 光有名字还得再拼一次收件目录，重名加了后缀就会拼错（1.5.65）。
     */
    data class ReceivedFile(
        val name: String,
        val sizeBytes: Long,
        val atMs: Long,
        val path: String? = null,
        /** 传应用时：同一个应用的分体包共用一个 group（包名）。 */
        val group: String? = null,
        val kind: Int = 0,
    )

    fun addReceived(file: ReceivedFile) {
        synchronized(received) { received += file }
    }

    fun receivedSnapshot(): List<ReceivedFile> = synchronized(received) { received.toList() }

    fun receivedCount(): Int = synchronized(received) { received.size }

    fun markConnected() {
        if (state == State.Waiting) {
            state = State.Connected
            connectedAtMs = System.currentTimeMillis()
        }

        lastActivityMs = System.currentTimeMillis()
    }

    fun touch() {
        lastActivityMs = System.currentTimeMillis()
    }

    fun close() {
        state = State.Closed
    }

    /** 该不该因为空闲而作废。 */
    fun isExpired(nowMs: Long = System.currentTimeMillis()): Boolean = when (state) {
        State.Waiting -> nowMs - createdAtMs > DirectProtocol.SESSION_IDLE_TIMEOUT_MS
        State.Connected -> nowMs - lastActivityMs > DirectProtocol.SESSION_IDLE_TIMEOUT_MS
        State.Closed -> true
    }

    companion object {
        private val random = SecureRandom()

        fun newId(): String = hex(8)

        fun newToken(): String = hex(32)

        private fun hex(size: Int): String {
            val bytes = ByteArray(size)
            random.nextBytes(bytes)
            return bytes.joinToString("") { "%02x".format(it) }
        }
    }
}
