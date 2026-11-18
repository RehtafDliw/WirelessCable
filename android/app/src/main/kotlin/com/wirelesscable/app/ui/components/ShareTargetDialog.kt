package com.wirelesscable.app.ui.components

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.data.db.DeviceEntity
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcSpace

/**
 * 系统分享之后的落点选择。
 *
 * 只给两个方向：发到某台电脑（已配对的都列出来）或当面直连给对方手机扫。
 * 分享给的是**临时**读权限，这一页不能晾太久，所以点哪一行就直接开传，
 * 不再叠一层"确认发送"。
 */
@Composable
fun ShareTargetDialog(
    /** 分享进来的东西：`3 个文件` / `分享文本-20261118-151609.txt`。 */
    label: String,
    devices: List<DeviceEntity>,
    /** 当前连着的那台（其余电脑点下去会先找它、再连上）。 */
    connectedDeviceId: String?,
    busy: Boolean,
    message: String?,
    onPickComputer: (String) -> Unit,
    onPickDirect: () -> Unit,
    onDismiss: () -> Unit,
) {
    WcDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = {
            Text(
                text = "发送到",
                fontSize = 16.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
            )
        },
        text = {
            Column {
                Text(text = label, fontSize = 12.sp, color = WcColors.Muted)
                Spacer(Modifier.height(WcSpace.s3))

                devices.forEach { device ->
                    ShareRow(
                        title = device.displayName,
                        subtitle = if (device.deviceId == connectedDeviceId) "已连接" else "未连接",
                        highlight = device.deviceId == connectedDeviceId,
                        enabled = !busy,
                        onClick = { onPickComputer(device.deviceId) },
                    )
                }

                if (devices.isEmpty()) {
                    Text(
                        text = "还没有配对电脑，可以直接用下面的当面直连",
                        fontSize = 13.sp,
                        color = WcColors.Muted,
                    )
                    Spacer(Modifier.height(WcSpace.s2))
                }

                HorizontalDivider(
                    color = WcColors.Line,
                    modifier = Modifier.padding(vertical = WcSpace.s1),
                )

                ShareRow(
                    title = "当面直连",
                    subtitle = "让对方扫码接收",
                    enabled = !busy,
                    onClick = onPickDirect,
                )

                message?.let { text ->
                    Spacer(Modifier.height(WcSpace.s2))
                    Text(text = text, fontSize = 12.sp, color = WcColors.Text2)
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss, enabled = !busy) { Text("取消") }
        },
    )
}

@Composable
private fun ShareRow(
    title: String,
    subtitle: String,
    highlight: Boolean = false,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(vertical = 9.dp, horizontal = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium,
                color = if (enabled) WcColors.Text else WcColors.Muted,
            )
            Spacer(Modifier.height(2.dp))
            Text(
                text = subtitle,
                fontSize = 12.sp,
                color = if (highlight) WcColors.Accent else WcColors.Muted,
            )
        }
    }
}
