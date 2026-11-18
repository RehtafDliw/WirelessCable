package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.TransferStates
import com.wirelesscable.app.core.transfer.TransferFormat
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 进度文案的换算规则。这些数字直接显示在用户面前，任何"看起来快一点"的取整都算造假，
 * 因此这里逐条钉住边界。
 */
class TransferFormatTest {

    @Test
    fun bytesUsesReadableUnits() {
        assertEquals("0 B", TransferFormat.bytes(0))
        assertEquals("1023 B", TransferFormat.bytes(1023))
        assertEquals("1.0 KB", TransferFormat.bytes(1024))
        assertEquals("1.0 MB", TransferFormat.bytes(1024L * 1024))
        assertEquals("1.00 GB", TransferFormat.bytes(1024L * 1024 * 1024))
        assertEquals("5.00 GB", TransferFormat.bytes(5L * 1024 * 1024 * 1024))
    }

    @Test
    fun unknownSizeIsNotFaked() {
        assertEquals("—", TransferFormat.bytes(-1))
    }

    @Test
    fun speedWithoutSampleShowsPlaceholder() {
        assertEquals("—", TransferFormat.speed(0))
        assertEquals("—", TransferFormat.speed(-5))
        assertEquals("2.0 KB/s", TransferFormat.speed(2048))
    }

    @Test
    fun percentIsClampedAndRounded() {
        assertEquals(0, TransferFormat.percent(0, 100))
        assertEquals(50, TransferFormat.percent(50, 100))
        assertEquals(33, TransferFormat.percent(1, 3))
        assertEquals(100, TransferFormat.percent(100, 100))
        // 越界不应产生 >100 或 <0 的百分比
        assertEquals(100, TransferFormat.percent(200, 100))
        assertEquals(0, TransferFormat.percent(-1, 100))
    }

    @Test
    fun zeroLengthFileIsCompleteOnlyWhenStateSaysSo() {
        assertEquals(0, TransferFormat.percent(0, 0, completed = false))
        assertEquals(100, TransferFormat.percent(0, 0, completed = true))
    }

    @Test
    fun remainingNeedsRealSpeed() {
        assertNull("未知速度不给剩余时间", TransferFormat.remaining(0, 1000, 0))
        assertNull("已传完不再显示剩余", TransferFormat.remaining(1000, 1000, 100))

        assertEquals("剩余 10 秒", TransferFormat.remaining(0, 1000, 100))
        assertEquals("剩余 1 分 0 秒", TransferFormat.remaining(0, 6000, 100))
        assertEquals("剩余 1 时 0 分", TransferFormat.remaining(0, 360_000, 100))
    }

    @Test
    fun remainingNeverRoundsDownToZero() {
        // 还剩 1 字节、速度极慢时也要给出"剩余 1 秒"，不能显示 0
        assertEquals("剩余 1 秒", TransferFormat.remaining(999_999, 1_000_000, 1))
    }

    @Test
    fun remainingHidesAbsurdEstimate() {
        // 链路刚断时速度还没衰减到 0，此时总量 5 GB / 1 B/s 会推出上万小时。
        // 这种估算不可信，必须不显示，而不是甩出一个天文数字。
        assertNull(TransferFormat.remaining(0, 5L * 1024 * 1024 * 1024, 1))
    }

    @Test
    fun stateLabelsCoverEveryProtocolState() {
        assertEquals("等待中", TransferFormat.stateLabel(TransferStates.WAITING))
        assertEquals("准备中", TransferFormat.stateLabel(TransferStates.PREPARING))
        assertEquals("传输中", TransferFormat.stateLabel(TransferStates.TRANSFERRING))
        assertEquals("校验中", TransferFormat.stateLabel(TransferStates.VERIFYING))
        assertEquals("已完成", TransferFormat.stateLabel(TransferStates.COMPLETED))
        assertEquals("失败", TransferFormat.stateLabel(TransferStates.FAILED))
        assertEquals("已取消", TransferFormat.stateLabel(TransferStates.CANCELLED))
        assertEquals("已中断", TransferFormat.stateLabel(TransferStates.INTERRUPTED))
        assertEquals("已暂停", TransferFormat.stateLabel(TransferStates.PAUSED))
        // 未知状态原样呈现，不吞掉
        assertEquals("mystery", TransferFormat.stateLabel("mystery"))
    }

    @Test
    fun detailOmitsUnknownParts() {
        val active = TransferFormat.detail(transferred = 512, total = 1024, bytesPerSecond = 256)
        assertEquals("512 B / 1.0 KB · 50% · 256 B/s · 剩余 2 秒", active)

        // 没有速度时不应出现速度和剩余时间两项
        val noSample = TransferFormat.detail(transferred = 512, total = 1024, bytesPerSecond = 0)
        assertEquals("512 B / 1.0 KB · 50%", noSample)

        val done = TransferFormat.detail(1024, 1024, 256, completed = true)
        assertEquals("1.0 KB / 1.0 KB · 100%", done)
    }

    @Test
    fun terminalAndActiveClassificationMatchesProtocol() {
        assertEquals(true, TransferStates.isTerminal(TransferStates.COMPLETED))
        assertEquals(true, TransferStates.isTerminal(TransferStates.FAILED))
        assertEquals(true, TransferStates.isTerminal(TransferStates.CANCELLED))
        assertEquals(false, TransferStates.isTerminal(TransferStates.TRANSFERRING))

        assertEquals(true, TransferStates.isActive(TransferStates.WAITING))
        assertEquals(true, TransferStates.isActive(TransferStates.VERIFYING))
        assertEquals(false, TransferStates.isActive(TransferStates.COMPLETED))
    }
}
