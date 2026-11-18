package com.wirelesscable.app.core.transfer

/**
 * 传输速度计。
 *
 * 只做一件事：把"某一时刻已传字节数"序列换算成字节/秒。
 * 规则是谁来调用都一致，因此可以脱离 Android 用 JVM 单测钉住：
 *
 * - 基准点只在真正算出过一次速度之后前移。若每次采样都重置基准，
 *   采样间隔会永远小于窗口，速度会恒为 0（真机上出现过这个 bug）。
 * - 速度用指数平滑（0.6 / 0.4），避免进度条上的数字来回跳。
 * - 已传字节数回退（例如任务重建）时丢弃这一次增量，不给出负速度。
 */
class SpeedMeter(private val windowSeconds: Double = DEFAULT_WINDOW_SECONDS) {

    private var baselineBytes = 0L
    private var baselineMs = 0L
    private var smoothed = 0L
    private var started = false

    /** 最近一次算出的速度（字节/秒）。还没有可信样本时为 0。 */
    val bytesPerSecond: Long get() = smoothed

    /**
     * 记录一次采样。
     * @param nowMs 单调时钟毫秒数（Android 上用 elapsedRealtime）
     * @param transferredBytes 累计已传字节数
     * @return 当前速度（字节/秒），样本不足时返回上一次的值（首次为 0）
     */
    fun sample(nowMs: Long, transferredBytes: Long): Long {
        if (!started) {
            started = true
            baselineBytes = transferredBytes
            baselineMs = nowMs
            return smoothed
        }

        val seconds = (nowMs - baselineMs) / 1000.0
        if (seconds < windowSeconds) {
            return smoothed
        }

        if (transferredBytes < baselineBytes) {
            baselineBytes = transferredBytes
            baselineMs = nowMs
            return smoothed
        }

        val instant = (transferredBytes - baselineBytes) / seconds
        smoothed = if (smoothed <= 0L) instant.toLong() else (smoothed * 0.6 + instant * 0.4).toLong()

        // 链路断开后平滑值会一直按 0.6 衰减、永远不会等于 0。
        // 低于下限就直接归零，避免界面显示 0 B/s 却还拿极小除数去推算剩余时间。
        if (smoothed < MIN_MEANINGFUL_SPEED) {
            smoothed = 0L
        }

        baselineBytes = transferredBytes
        baselineMs = nowMs

        return smoothed
    }

    fun reset() {
        baselineBytes = 0L
        baselineMs = 0L
        smoothed = 0L
        started = false
    }

    companion object {
        const val DEFAULT_WINDOW_SECONDS = 0.3

        /** 低于 1 KiB/s 不计为在传。 */
        const val MIN_MEANINGFUL_SPEED = 1024L
    }
}
