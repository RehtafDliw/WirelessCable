package com.wirelesscable.app

import com.wirelesscable.app.core.transfer.SpeedMeter
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 速度计。真机曾出现"进度在动、速度永远是 0"，原因是每次采样都重置基准，
 * 相距永远小于窗口。这里的用例专门锁住那条路径。
 */
class SpeedMeterTest {

    @Test
    fun firstSampleHasNoSpeed() {
        val meter = SpeedMeter()

        assertEquals(0L, meter.sample(nowMs = 1_000, transferredBytes = 0))
    }

    @Test
    fun speedUsesRealByteDelta() {
        val meter = SpeedMeter()
        meter.sample(1_000, 0)

        // 500ms 内传了 5 MB -> 10 MB/s
        val speed = meter.sample(1_500, 5L * 1024 * 1024)

        assertEquals(10L * 1024 * 1024, speed)
        assertEquals(speed, meter.bytesPerSecond)
    }

    @Test
    fun frequentSamplesStillProduceSpeed() {
        // 每 150ms 上报一次（传输引擎的节流间隔），基准必须累积到窗口之外才能算出速度
        val meter = SpeedMeter()
        meter.sample(0, 0)

        var last = 0L
        for (tick in 1..10) {
            val now = tick * 150L
            last = meter.sample(now, tick * 1_000_000L)  // 每 150ms 1 MB ≈ 6.7 MB/s
        }

        assertTrue("多次短间隔采样后必须算出速度，实际 $last", last > 0L)
        // 10 次 × 1 MB / 1.5 s ≈ 6.66 MB/s
        assertEquals(6_666_666.0, last.toDouble(), 200_000.0)
    }

    @Test
    fun speedIsSmoothedNotJumping() {
        val meter = SpeedMeter()
        meter.sample(0, 0)
        val first = meter.sample(1_000, 10_000_000)          // 10 MB/s
        val second = meter.sample(2_000, 20_000_000)         // 再 10 MB/s
        val spike = meter.sample(3_000, 40_000_000)          // 这一秒 20 MB/s

        assertEquals(10_000_000L, first)
        assertEquals(10_000_000L, second)
        // 0.6*10 + 0.4*20 = 14 MB/s，而不是直接跳到 20
        assertEquals(14_000_000.0, spike.toDouble(), 1.0)
    }

    @Test
    fun shrinkingProgressDoesNotProduceNegativeSpeed() {
        val meter = SpeedMeter()
        meter.sample(0, 5_000_000)
        meter.sample(1_000, 6_000_000)

        // 进度回退（任务重建）时保持上一次的速度，不出现负值
        val after = meter.sample(2_000, 1_000_000)

        assertTrue("速度不能为负，实际 $after", after >= 0L)
    }

    @Test
    fun resetClearsBaseline() {
        val meter = SpeedMeter()
        meter.sample(0, 0)
        meter.sample(1_000, 4_000_000)
        meter.reset()

        assertEquals(0L, meter.bytesPerSecond)
        assertEquals(0L, meter.sample(2_000, 1_000))
    }

    @Test
    fun stalledTransferDecaysToZero() {
        val meter = SpeedMeter()
        meter.sample(0, 0)
        assertEquals(10_000_000L, meter.sample(1_000, 10_000_000))

        // 链路断开：字节不再增长。平滑值必须真的落到 0，
        // 否则界面会显示 "0 B/s" 却用这个极小数算出天文数字的剩余时间。
        var now = 1_000L
        var speed = 0L
        repeat(40) {
            now += 500
            speed = meter.sample(now, 10_000_000)
        }

        assertEquals(0L, speed)
        assertEquals(0L, meter.bytesPerSecond)
    }

    @Test
    fun speedRecoversAfterStall() {
        val meter = SpeedMeter()
        meter.sample(0, 0)
        meter.sample(1_000, 10_000_000)

        var now = 1_000L
        repeat(40) {
            now += 500
            meter.sample(now, 10_000_000)
        }

        // 恢复传输后立刻回到真实速率，不被归零影响
        val resumed = meter.sample(now + 1_000, 10_000_000 + 5_000_000)

        assertEquals(5_000_000L, resumed)
    }
}
