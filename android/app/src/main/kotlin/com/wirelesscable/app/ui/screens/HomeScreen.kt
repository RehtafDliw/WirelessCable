package com.wirelesscable.app.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.core.transfer.TransferFormat
import com.wirelesscable.app.core.transfer.TransferItem
import com.wirelesscable.app.vm.TransferRow
import com.wirelesscable.app.ui.components.MonitorIcon
import com.wirelesscable.app.ui.components.StatusPill
import com.wirelesscable.app.ui.components.Tone
import com.wirelesscable.app.ui.components.WcCard
import com.wirelesscable.app.ui.components.WcCardList
import com.wirelesscable.app.ui.components.WcLinkButton
import com.wirelesscable.app.ui.components.WcProgressBar
import com.wirelesscable.app.ui.components.WcRowDivider
import com.wirelesscable.app.ui.components.WcSectionHeader
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSize
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.vm.PairingUiState
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import com.wirelesscable.app.core.i18n.Str
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.ui.components.deviceLabel

/**
 * 首页。
 *
 * 只放四块（2026-11-16 裁定）：连接状态、我的设备、传输中、发送文件 / 扫码配对。
 * **不做仪表盘** —— 没有「进行中 N」「今日完成 M」这类计数块：用户问的是
 * 现在连着谁、还在传什么，不是看统计报表。
 *
 * 退出应用在右上角（红色电源图标），不在页面底部：它是我不用了的出口，
 * 放在滚动区末尾意味着要滑到底才找得到，而且每屏都占一行。
 */
@Composable
fun HomeScreen(
    pairing: PairingUiState,
    /** 进行中的传输：整文件夹（一次下载展开的一批）已经收成一行。 */
    activeRows: List<TransferRow>,
    onOpenScanner: () -> Unit,
    onSendFiles: () -> Unit,
    /** 把应用安装包发给电脑（不关心对面能不能装，能发就行）。 */
    onSendApps: () -> Unit,
    /** 面对面直连（手机 ↔ 手机），从首页直接进。 */
    onOpenDirect: () -> Unit,
    onOpenTransfers: () -> Unit,
    onToggleScan: () -> Unit,
    onRetrySession: () -> Unit,
    onConnect: (DiscoveredServer) -> Unit,
    onCancelPairing: () -> Unit,
    /** 配对记录还在但令牌丢了：直接对上次那台电脑重新发起配对。 */
    onRePair: () -> Unit = {},
    /** 后台服务开着但没进电池白名单：息屏后会被系统冻结。 */
    backgroundLimited: Boolean = false,
    onAllowBackground: () -> Unit = {},
    modifier: Modifier = Modifier,
) {
    Column(modifier = modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = WcSpace.s4),
        ) {
            // 配对确认放最上面：发起后用户要盯的就是这个码
            if (pairing.hasPendingPairing) {
                Spacer(Modifier.height(WcSpace.s3))
                PairingRequestCard(pairing, onCancelPairing)
            }

            if (backgroundLimited) {
                Spacer(Modifier.height(WcSpace.s3))
                BackgroundWarningCard(onAllowBackground)
            }

            // ---------------- 我的设备 ----------------
            WcSectionHeader(
                title = Str.t("我的设备"),
                hint = if (pairing.scanning) Str.t("停止扫描") else Str.t("扫描"),
                onHintClick = onToggleScan,
            )

            if (pairing.pairedDevices.isEmpty()) {
                WcCard {
                    Text(
                        text = Str.t("还没有配对电脑"),
                        fontSize = WcType.bodySize,
                        color = WcColors.Text,
                    )
                    Spacer(Modifier.height(WcSpace.s1))
                    WcLinkButton(text = Str.t("扫码配对"), onClick = onOpenScanner)
                }
            } else {
                WcCardList {
                    // 会话状态以引擎为准：配对页那套 sessionState 只在配对流程里更新，
                    // 换网络后心跳已经通了它还是 disconnected，首页就会写"断开"。
                    val sync by SessionSyncEngine.state.collectAsStateWithLifecycle()

                    pairing.pairedDevices.forEachIndexed { index, device ->
                        if (index > 0) WcRowDivider()
                        val isCurrent = sync.connected && sync.deviceId == device.deviceId
                        DeviceRow(
                            name = deviceLabel(device.displayName, device.deviceId),
                            meta = when {
                                isCurrent -> Str.t("当前设备")
                                pairing.sessionState == "connecting" -> Str.t("连接中")
                                else -> Str.t("上次连接 ") + agoText(device.lastSeenUtc)
                            },
                            connected = isCurrent,
                            connecting = pairing.sessionState == "connecting",
                        )
                    }
                }
            }

            // 附近的电脑：只在扫描或有结果时出现，平时不占位
            if (pairing.scanning || pairing.discovered.isNotEmpty()) {
                WcSectionHeader(title = Str.t("附近的电脑"), hint = if (pairing.scanning) Str.t("扫描中") else null)
                // 已经连着的那台不再出现在附近的电脑里：它已经在上面标着已连接，
                // 重复列出只会让人以为还有第二台
                val nearby = pairing.discovered.filterNot { server ->
                    pairing.isConnected && pairing.sessionDeviceId == server.deviceId
                }

                if (nearby.isEmpty()) {
                    WcCard {
                        Text(
                            text = Str.t("确认手机与电脑在同一 Wi-Fi"),
                            fontSize = WcType.labelSize,
                            color = WcColors.Muted,
                        )
                    }
                } else {
                    val servers = nearby
                    WcCardList {
                        servers.forEachIndexed { index, server ->
                            if (index > 0) WcRowDivider()
                            val paired = pairing.pairedDevices.any { it.deviceId == server.deviceId }
                            val isCurrent = paired && pairing.isConnected &&
                                pairing.sessionDeviceId == server.deviceId
                            DiscoveredRow(
                                server = server,
                                paired = paired,
                                current = isCurrent,
                                onConnect = { onConnect(server) },
                            )
                        }
                    }
                }
            }

            // 断开时**始终**给出口：自动重连只用一次，之后必须用户自己点
            if (!pairing.isConnected && pairing.pairedDevices.isNotEmpty()) {
                Spacer(Modifier.height(WcSpace.s2))
                if (pairing.tokenMissing) {
                    // 令牌没了，重试永远失败——直接给唯一的出口，别让用户去猜
                    WcLinkButton(text = Str.t("重新配对"), onClick = onRePair)
                } else {
                    WcLinkButton(
                        text = if (pairing.sessionState == "connecting") Str.t("连接中…") else Str.t("重连"),
                        onClick = onRetrySession,
                    )
                }
            }

            pairing.message?.let { raw ->
                val message = Str.t(raw)
                Spacer(Modifier.height(WcSpace.s2))
                Text(
                    text = message,
                    fontSize = WcType.labelSize,
                    color = if (pairing.awaitingApproval) WcColors.Muted else WcColors.Error,
                )
            }

            // ---------------- 传输中 ----------------
            WcSectionHeader(title = Str.t("传输中"), hint = Str.t("全部"), onHintClick = onOpenTransfers)

            if (activeRows.isEmpty()) {
                Text(
                    text = Str.t("没有正在进行的传输"),
                    fontSize = WcType.labelSize,
                    color = WcColors.Muted,
                    modifier = Modifier.padding(vertical = WcSpace.s2),
                )
            } else {
                // 只放两条：首页是扫一眼的地方，多了会把快捷动作挤下去
                activeRows.take(2).forEach { row ->
                    when (row) {
                        is TransferRow.Single -> TransferSummaryCard(
                            title = row.item.fileName,
                            subtitle = row.item.directionLine(),
                            progress = row.item.progress,
                            transferredBytes = row.item.transferredBytes,
                            totalBytes = row.item.totalBytes,
                            onOpen = onOpenTransfers,
                        )

                        // 整文件夹：一张卡代表整批，不把 200 个文件铺到首页
                        is TransferRow.Batch -> TransferSummaryCard(
                            title = row.name,
                            subtitle = row.homeSubtitle(),
                            progress = row.progress,
                            transferredBytes = row.transferredBytes,
                            totalBytes = row.totalBytes,
                            onOpen = onOpenTransfers,
                        )
                    }
                    Spacer(Modifier.height(WcSpace.s2))
                }
            }

            // ---------------- 快捷动作 ----------------
            // 两两一组：上面是往电脑送东西，下面是连上谁
            Spacer(Modifier.height(WcSpace.s1))
            Row(horizontalArrangement = Arrangement.spacedBy(WcSpace.s2)) {
                PrimaryAction(
                    text = Str.t("发送文件"),
                    onClick = onSendFiles,
                    modifier = Modifier.weight(1f),
                )
                SecondaryAction(
                    text = Str.t("发送应用"),
                    onClick = onSendApps,
                    modifier = Modifier.weight(1f),
                )
            }

            Spacer(Modifier.height(WcSpace.s2))

            Row(horizontalArrangement = Arrangement.spacedBy(WcSpace.s2)) {
                SecondaryAction(
                    text = Str.t("扫码配对"),
                    onClick = onOpenScanner,
                    modifier = Modifier.weight(1f),
                )
                SecondaryAction(
                    text = Str.t("当面直连"),
                    onClick = onOpenDirect,
                    modifier = Modifier.weight(1f),
                )
            }


            Spacer(Modifier.height(WcSpace.s6))
        }
    }

}

@Composable
private fun PairingRequestCard(state: PairingUiState, onCancel: () -> Unit) {
    WcCard {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = state.activeServer?.let { deviceLabel(it.displayName, it.deviceId) }
                    ?: Str.t("未知设备"),
                fontSize = WcType.h3Size,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
                modifier = Modifier.weight(1f),
            )
            StatusPill(text = Str.t("等待确认"), tone = Tone.Idle)
        }
        Spacer(Modifier.height(WcSpace.s2))
        Text(
            text = state.pairingCode,
            fontSize = 34.sp,
            fontWeight = FontWeight.Bold,
            color = WcColors.Text,
        )
        Spacer(Modifier.height(WcSpace.s1))
        Text(text = Str.t("在电脑上点「允许」"), fontSize = WcType.labelSize, color = WcColors.Muted)
        Spacer(Modifier.height(WcSpace.s2))
        WcLinkButton(text = Str.t("取消"), onClick = onCancel, color = WcColors.Muted)
    }
}

@Composable
private fun BackgroundWarningCard(onAllow: () -> Unit) {
    WcCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = Str.t("息屏后会连不上"),
                fontSize = WcType.bodySize,
                color = WcColors.Error,
                modifier = Modifier.weight(1f),
            )
            WcLinkButton(text = Str.t("允许后台运行"), onClick = onAllow, color = WcColors.Accent)
        }
    }
}

// ---------------------------------------------------------------- 设备行

@Composable
private fun DeviceRow(
    name: String,
    meta: String,
    connected: Boolean,
    connecting: Boolean,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(WcSize.RowHeight)
            .padding(horizontal = WcSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .size(32.dp)
                .clip(SquircleShape(WcRadius.r1))
                .background(WcColors.Well),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = MonitorIcon,
                contentDescription = null,
                tint = WcColors.Text2,
                modifier = Modifier.size(WcSize.IconSm),
            )
        }
        Spacer(Modifier.width(WcSpace.s3))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = name,
                fontSize = 14.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = meta,
                fontSize = WcType.captionSize,
                color = WcColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        when {
            connected -> StatusPill(text = Str.t("已连接"), tone = Tone.Ok)
            connecting -> StatusPill(text = Str.t("连接中"), tone = Tone.Idle)
            else -> StatusPill(text = Str.t("断开"), tone = Tone.Idle)
        }
    }
}

@Composable
private fun DiscoveredRow(
    server: DiscoveredServer,
    paired: Boolean,
    current: Boolean,
    onConnect: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(WcSize.RowHeight)
            .padding(horizontal = WcSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = deviceLabel(server.displayName, server.deviceId),
                fontSize = 14.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = if (paired) Str.t("已配对") else Str.t("未配对"),
                fontSize = WcType.captionSize,
                color = WcColors.Muted,
            )
        }
        if (current) {
            StatusPill(text = Str.t("已连接"), tone = Tone.Ok)
        } else {
            Text(
                text = if (paired) Str.t("连接") else Str.t("配对"),
                fontSize = 13.sp,
                color = WcColors.Accent,
                modifier = Modifier
                    .clip(SquircleShape(WcRadius.r1))
                    .clickable(onClick = onConnect)
                    .padding(horizontal = 10.dp, vertical = 6.dp),
            )
        }
    }
}

// ---------------------------------------------------------------- 传输摘要

/**
 * 一张传输摘要卡。
 *
 * 单条和整批共用：两者要展示的东西完全一样（名字、一行说明、百分比、字节），
 * 只是数据来源不同——各写一份卡片必然逐渐长歪。
 */
@Composable
private fun TransferSummaryCard(
    title: String,
    subtitle: String,
    progress: Float,
    transferredBytes: Long,
    totalBytes: Long,
    onOpen: () -> Unit,
) {
    WcCard(modifier = Modifier.clickable(onClick = onOpen)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = title,
                    fontSize = 13.5.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = WcColors.Text,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = subtitle,
                    fontSize = WcType.captionSize,
                    color = WcColors.Muted,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Spacer(Modifier.width(WcSpace.s2))
            Text(
                text = "${(progress * 100).toInt()}%",
                fontSize = WcType.captionSize,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.AccentText,
            )
        }
        Spacer(Modifier.height(WcSpace.s2))
        WcProgressBar(fraction = progress, color = WcColors.Accent)
        Spacer(Modifier.height(WcSpace.s1))
        Text(
            text = "${TransferFormat.bytes(transferredBytes)} / ${TransferFormat.bytes(totalBytes)}",
            fontSize = WcType.captionSize,
            color = WcColors.Muted,
        )
    }
}

/** 单条的第二行：设备名 · 方向 · 速度。 */
private fun TransferItem.directionLine(): String =
    listOfNotNull(
        deviceName.takeIf { it.isNotBlank() },
        // 用本端视角的「接收/发送」：以前两端都写"本机"，指的是各自的自己，同一条传输读起来正好相反
        Str.t(if (isUpload) "发送" else "接收"),
        TransferFormat.speed(bytesPerSecond),
    ).joinToString(" · ")

/** 整批的第二行：设备名 · 方向 · 已完成几个 · 速度。 */
private fun TransferRow.Batch.homeSubtitle(): String {
    val parts = buildList {
        lead.deviceName.takeIf { it.isNotBlank() }?.let(::add)
        add(Str.t(if (isUpload) "发送" else "接收"))
        add(Str.t("$completedCount/$fileCount 个文件"))
        if (isActive && !allPaused) {
            add(TransferFormat.speed(bytesPerSecond))
        }
    }
    return parts.joinToString(" · ")
}

// ---------------------------------------------------------------- 动作按钮

@Composable
private fun PrimaryAction(text: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val shape = SquircleShape(WcRadius.r2)
    Box(
        modifier = modifier
            .height(44.dp)
            .clip(shape)
            .background(WcColors.Accent)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = text,
            fontSize = 14.sp,
            fontWeight = FontWeight.SemiBold,
            color = WcColors.OnAccent,
        )
    }
}

@Composable
private fun SecondaryAction(text: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val shape = SquircleShape(WcRadius.r2)
    Box(
        modifier = modifier
            .height(44.dp)
            .clip(shape)
            .background(WcColors.Soft)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = text,
            fontSize = 14.sp,
            fontWeight = FontWeight.SemiBold,
            color = WcColors.Text2,
        )
    }
}

/** 「3 分钟前 / 昨天 21:14」。时间戳是 UTC 毫秒。 */
private fun agoText(epochMillis: Long): String {
    if (epochMillis <= 0L) return "—"
    val delta = System.currentTimeMillis() - epochMillis
    return when {
        delta < 60_000L -> Str.t("刚刚")
        delta < 3_600_000L -> Str.t("${delta / 60_000L} 分钟前")
        delta < 86_400_000L -> Str.t("${delta / 3_600_000L} 小时前")
        delta < 7 * 86_400_000L -> Str.t("${delta / 86_400_000L} 天前")
        else -> SimpleDateFormat("MM-dd", Locale.getDefault()).format(Date(epochMillis))
    }
}
