package com.wirelesscable.app.ui.screens

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.data.db.DeviceEntity
import com.wirelesscable.app.ui.components.SectionTitle
import com.wirelesscable.app.ui.components.StatusPill
import com.wirelesscable.app.ui.components.Tone
import com.wirelesscable.app.ui.components.WcCard
import com.wirelesscable.app.ui.components.WcDialog
import com.wirelesscable.app.ui.components.WcCardList
import com.wirelesscable.app.ui.components.WcRowDivider
import com.wirelesscable.app.ui.components.WcLinkButton
import com.wirelesscable.app.ui.components.WcPrimaryButton
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSize
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.core.protocol.DiscoveredServer
import com.wirelesscable.app.vm.PairingUiState

/**
 * 首页的「连接电脑」区：当前连接、待确认配对、附近电脑、手动配对。
 *
 * 这些原本是一个独立的「设备」标签页。阶段 7 把底部导航收敛成
 * 首页 / 文件 / 传输 / 设置 四页后，连接与发现并入首页——
 * 打开 App 第一眼就能看到"连上没连上、附近有哪台电脑"。
 */
@Composable
fun HomeDeviceSection(
    state: PairingUiState,
    onOpenScanner: () -> Unit,
    onToggleScan: () -> Unit,
    onRetrySession: () -> Unit,
    onConnect: (DiscoveredServer) -> Unit,
    onCancelPairing: () -> Unit,
) {
    SectionTitle("电脑")

    WcCard {
        if (state.pairedDevices.isEmpty()) {
            Text(
                text = "还没有配对电脑",
                fontSize = 13.sp,
                color = WcColors.Muted,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                text = "扫码或扫描同一 Wi-Fi 下的电脑",
                fontSize = 12.sp,
                color = WcColors.Muted,
            )
        } else {
            // 一行一台电脑。当前连接的那台给"已连接"，其余显示"已配对"——
            // 切换电脑走下面的「附近的电脑」，不在列表里塞一排连接按钮。
            state.pairedDevices.forEach { device ->
                val isCurrent = state.sessionState == "connected" &&
                    state.sessionDeviceId == device.deviceId

                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = device.displayName,
                            fontSize = 14.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = WcColors.Text,
                        )
                        Spacer(Modifier.height(2.dp))
                        Text(
                            text = if (isCurrent) "当前设备" else "已配对",
                            fontSize = 12.sp,
                            color = WcColors.Muted,
                        )
                    }

                    when {
                        isCurrent -> StatusPill(text = "已连接", tone = Tone.Ok)
                        state.sessionState == "connecting" -> StatusPill(text = "连接中", tone = Tone.Idle)
                        else -> Text(text = "—", fontSize = 12.sp, color = WcColors.Muted)
                    }
                }
            }
        }

        Spacer(Modifier.height(10.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            WcLinkButton(text = "扫码配对", onClick = onOpenScanner, color = WcColors.Accent)
            WcLinkButton(
                text = if (state.scanning) "停止扫描" else "扫描电脑",
                onClick = onToggleScan,
                color = WcColors.Accent,
            )
            if (state.sessionState != "connected") {
                WcLinkButton(text = "重试", onClick = onRetrySession, color = WcColors.Muted)
            }
        }

        // 消息紧贴操作区：放到页面最下方用户看不到（真机踩过）
        state.message?.let { message ->
            Spacer(Modifier.height(8.dp))
            Text(
                text = message,
                fontSize = 12.5.sp,
                color = if (state.awaitingApproval) WcColors.Muted else WcColors.Error,
            )
        }
    }

    // 配对确认优先：发起后用户要盯的就是这个码
    if (state.hasPendingPairing) {
        Spacer(Modifier.height(12.dp))

        WcCard {
            Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(
                    text = state.activeServer?.displayName ?: "未知设备",
                    fontSize = 15.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = WcColors.Text,
                    modifier = Modifier.weight(1f),
                )
                StatusPill(text = "等待确认", tone = Tone.Idle)
            }
            Spacer(Modifier.height(8.dp))
            Text(
                text = state.pairingCode,
                fontSize = 34.sp,
                fontWeight = FontWeight.Bold,
                color = WcColors.Text,
            )
            Spacer(Modifier.height(6.dp))
            Text(text = "在电脑上点「允许」", fontSize = 12.sp, color = WcColors.Muted)
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = onCancelPairing) { Text("取消") }
        }
    }

    // 发现列表只跟"是否在扫描 / 有没有结果"有关，不能被折叠区吞掉
    if (state.scanning || state.discovered.isNotEmpty()) {
        Spacer(Modifier.height(16.dp))

        Row(verticalAlignment = Alignment.CenterVertically) {
            SectionTitle("附近的电脑")
            if (state.scanning) {
                Spacer(Modifier.width(8.dp))
                StatusPill(text = "扫描中", tone = Tone.Idle)
            }
        }

        if (state.discovered.isEmpty()) {
            WcCard {
                Text(
                    text = "确认手机与电脑在同一 Wi-Fi",
                    fontSize = 12.sp,
                    color = WcColors.Muted,
                )
            }
        } else {
            state.discovered.forEach { server ->
                val paired = state.pairedDevices.any { it.deviceId == server.deviceId }
                // 已经连着的这台不要再给「连接」按钮：点击没有意义，
                // 还会让人以为连接没成功（真机反馈）。
                val isCurrent = paired &&
                    state.sessionState == "connected" &&
                    state.sessionDeviceId == server.deviceId

                WcCard(modifier = Modifier.padding(bottom = 8.dp)) {
                    Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = server.displayName,
                                fontSize = 15.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = WcColors.Text,
                            )
                            if (paired) {
                                Spacer(Modifier.height(2.dp))
                                Text(
                                    text = if (isCurrent) "当前设备" else "已配对",
                                    fontSize = 12.sp,
                                    color = WcColors.Muted,
                                )
                            }
                        }
                        if (isCurrent) {
                            StatusPill(text = "已连接", tone = Tone.Ok)
                        } else {
                            Button(onClick = { onConnect(server) }) {
                                Text(if (paired) "连接" else "配对")
                            }
                        }
                    }
                }
            }
        }
    }

}

/**
 * 设置页的「已配对设备」：改名与移除。
 *
 * 从原「设备」页搬过来。首页负责发现与连接，这里负责管理已有关系——
 * 职责分开后，用户不会在首页误点到"移除"。
 */
@Composable
fun PairedDevicesSection(
    devices: List<DeviceEntity>,
    sessionState: String,
    sessionDeviceId: String?,
    onRename: (String, String) -> Unit,
    onForget: (String) -> Unit,
    onGrant: (String) -> Unit = {},
) {
    var renaming by remember { mutableStateOf<DeviceEntity?>(null) }
    var removing by remember { mutableStateOf<DeviceEntity?>(null) }

    if (devices.isEmpty()) {
        WcCard {
            Text(text = "还没有配对过电脑", fontSize = 13.sp, color = WcColors.Muted)
        }
    } else {
        WcCardList {
            devices.forEachIndexed { index, device ->
                if (index > 0) WcRowDivider()
                val connected = sessionState == "connected" && sessionDeviceId == device.deviceId
                var menuOpen by remember { mutableStateOf(false) }

                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(WcSize.RowHeight)
                        .padding(horizontal = WcSpace.s3),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = device.displayName,
                            fontSize = 14.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = WcColors.Text,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            text = device.platform,
                            fontSize = WcType.captionSize,
                            color = WcColors.Muted,
                            maxLines = 1,
                        )
                    }

                    StatusPill(
                        text = if (connected) "已连接" else "断开",
                        tone = if (connected) Tone.Ok else Tone.Idle,
                    )

                    Text(
                        text = "授权",
                        fontSize = 13.sp,
                        color = WcColors.Accent,
                        modifier = Modifier
                            .clip(SquircleShape(WcRadius.r1))
                            .clickable { onGrant(device.deviceId) }
                            .padding(horizontal = 8.dp, vertical = 6.dp),
                    )

                    Box {
                        Text(
                            text = "⋯",
                            fontSize = 16.sp,
                            color = WcColors.Muted,
                            modifier = Modifier
                                .clip(SquircleShape(WcRadius.r1))
                                .clickable { menuOpen = true }
                                .padding(horizontal = 8.dp, vertical = 4.dp),
                        )
                        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                            DropdownMenuItem(
                                text = { Text("改名", fontSize = 13.sp) },
                                onClick = {
                                    menuOpen = false
                                    renaming = device
                                },
                            )
                            DropdownMenuItem(
                                text = { Text("移除", fontSize = 13.sp, color = WcColors.Error) },
                                onClick = {
                                    menuOpen = false
                                    removing = device
                                },
                            )
                        }
                    }
                }
            }
        }
    }

    removing?.let { device ->
        WcDialog(
            onDismissRequest = { removing = null },
            title = { Text("移除设备") },
            text = { Text("移除「${device.displayName}」后需要重新配对才能再连接。") },
            confirmButton = {
                Button(onClick = {
                    onForget(device.deviceId)
                    removing = null
                }) { Text("移除") }
            },
            dismissButton = {
                OutlinedButton(onClick = { removing = null }) { Text("取消") }
            },
        )
    }

    renaming?.let { device ->
        RenameDeviceDialog(
            device = device,
            onDismiss = { renaming = null },
            onConfirm = { name ->
                onRename(device.deviceId, name)
                renaming = null
            },
        )
    }
}

@Composable
private fun RenameDeviceDialog(
    device: DeviceEntity,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    var name by remember(device.deviceId) { mutableStateOf(device.displayName) }

    WcDialog(
        onDismissRequest = onDismiss,
        title = { Text("设备名称") },
        text = {
            OutlinedTextField(
                value = name,
                onValueChange = { name = it.take(64) },
                singleLine = true,
                label = { Text("名称") },
            )
        },
        confirmButton = {
            Button(onClick = { onConfirm(name) }, enabled = name.isNotBlank()) { Text("确定") }
        },
        dismissButton = {
            OutlinedButton(onClick = onDismiss) { Text("取消") }
        },
    )
}
