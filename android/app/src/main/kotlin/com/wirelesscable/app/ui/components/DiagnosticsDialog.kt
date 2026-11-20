package com.wirelesscable.app.ui.components

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.WirelessCableApp
import com.wirelesscable.app.core.i18n.Str
import com.wirelesscable.app.core.network.ConnectionDiagnostics
import com.wirelesscable.app.core.network.DiagnosticsReport
import com.wirelesscable.app.ui.theme.WcColors
import kotlinx.coroutines.launch

/**
 * 连接诊断。
 *
 * 为什么要有它：所有连接问题在界面上都是同一句「连接中断」，
 * 但原因可能是没连同一个 Wi-Fi、路由器开了 AP 隔离、Windows 防火墙按
 * 「公用网络」拦了入站、或者电脑换了地址。用户只能猜，远程也只能猜。
 *
 * 这里把五件事分别测一遍，直接给结论——测完就知道该改网络、点重连、还是重新配对。
 */
@Composable
fun DiagnosticsDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val app = context.applicationContext as WirelessCableApp
    val scope = rememberCoroutineScope()

    var report by remember { mutableStateOf<DiagnosticsReport?>(null) }
    var busy by remember { mutableStateOf(false) }

    fun refresh() {
        if (busy) {
            return
        }

        busy = true
        scope.launch {
            report = runCatching {
                ConnectionDiagnostics(context, app.discovery, app.repository).run()
            }.getOrElse { error ->
                DiagnosticsReport(
                    steps = emptyList(),
                    conclusion = "诊断本身出错：${error.message ?: error.javaClass.simpleName}",
                )
            }
            busy = false
        }
    }

    LaunchedEffect(Unit) { refresh() }

    WcDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                text = Str.t("连接诊断"),
                fontSize = 16.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
            )
        },
        text = {
            Column {
                val current = report
                if (current == null) {
                    Text(
                        text = Str.t("检查中…"),
                        fontSize = 13.sp,
                        color = WcColors.Muted,
                    )
                } else {
                    current.steps.forEach { step -> StepRow(step.label, step.ok, step.detail) }

                    Spacer(Modifier.height(10.dp))
                    Text(
                        text = Str.t(current.conclusion),
                        fontSize = 13.sp,
                        fontWeight = FontWeight.Medium,
                        color = WcColors.Text,
                    )
                }
            }
        },
        confirmButton = {
            WcLinkButton(
                text = if (busy) Str.t("检查中…") else Str.t("重新诊断"),
                onClick = { if (!busy) refresh() },
            )
        },
        dismissButton = {
            WcLinkButton(text = Str.t("关闭"), onClick = onDismiss)
        },
    )
}

@Composable
private fun StepRow(label: String, ok: Boolean, detail: String) {
    Column(modifier = Modifier.padding(vertical = 3.dp)) {
        Row {
            Text(
                text = if (ok) "✓" else "✗",
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
                color = if (ok) WcColors.Success else WcColors.Error,
            )
            Text(
                text = Str.t(label),
                fontSize = 13.sp,
                fontWeight = FontWeight.Medium,
                color = WcColors.Text,
                modifier = Modifier.padding(start = 6.dp),
            )
        }

        Text(
            text = Str.t(detail),
            fontSize = 12.sp,
            color = WcColors.Muted,
            modifier = Modifier.padding(start = 17.dp, top = 1.dp),
        )
    }
}
