package com.wirelesscable.app.ui.components

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.core.apps.InstalledApp
import com.wirelesscable.app.core.apps.InstalledApps
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcSpace
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import com.wirelesscable.app.core.i18n.Str

/**
 * 应用选择器。
 *
 * 只做一件事：把这台手机上装了什么列出来让人勾。默认只显示有桌面图标的，
 * 系统组件（几百个）折叠起来——但**不隐藏**，用户要传的很可能正是预装应用。
 */
@Composable
fun AppPickerDialog(
    onDismiss: () -> Unit,
    onConfirm: (List<InstalledApp>) -> Unit,
) {
    val context = LocalContext.current
    val maxListHeight = (LocalConfiguration.current.screenHeightDp * 0.55f)
        .dp
        .coerceIn(280.dp, 520.dp)

    var apps by remember { mutableStateOf<List<InstalledApp>?>(null) }
    var selected by remember { mutableStateOf<Set<String>>(emptySet()) }
    var query by remember { mutableStateOf("") }
    var showComponents by remember { mutableStateOf(false) }
    // 重新读取：有些 ROM 的「应用列表」开关是弹框时才给的，给完得再读一次
    var reloadKey by remember { mutableStateOf(0) }

    LaunchedEffect(reloadKey) {
        apps = null
        apps = withContext(Dispatchers.IO) {
            runCatching { InstalledApps.load(context) }.getOrDefault(emptyList())
        }
    }

    WcDialog(
        onDismissRequest = onDismiss,
        title = { Text(Str.t("选择应用"), fontSize = 16.sp) },
        text = {
            val list = apps
            if (list == null) {
                Text(Str.t("正在读取应用列表…"), fontSize = 13.sp, color = WcColors.Muted)
                return@WcDialog
            }

            Column(modifier = Modifier.fillMaxWidth()) {
                WcInput(
                    value = query,
                    onValueChange = { query = it },
                    label = Str.t("搜索名称或包名"),
                    modifier = Modifier.fillMaxWidth(),
                )

                // 只读到 1 个应用基本就是被系统挡了（ColorOS 的「读取应用列表」、
                // 部分 ROM 的隐私开关）。这时列表再好看也没用，先告诉用户去哪里开。
                if (list.size <= 1) {
                    Spacer(Modifier.height(WcSpace.s2))
                    Text(
                        text = Str.t("只读到 ${list.size} 个应用。系统限制了「应用列表」读取：") +
                            Str.t("到应用权限里打开它，再点「重新读取」；仍不行就重启一次应用") +
                            Str.t("（有的 ROM 在进程启动时才生效）。"),
                        fontSize = 11.5.sp,
                        color = WcColors.WarningText,
                    )
                    Text(
                        text = Str.t("打开应用设置"),
                        fontSize = 12.5.sp,
                        color = WcColors.Accent,
                        modifier = Modifier
                            .clickable {
                                runCatching {
                                    context.startActivity(
                                        Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                                            .setData(Uri.fromParts("package", context.packageName, null))
                                            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                                    )
                                }
                            }
                            .padding(vertical = 6.dp),
                    )
                    Text(
                        text = Str.t("重新读取"),
                        fontSize = 12.5.sp,
                        color = WcColors.Accent,
                        modifier = Modifier
                            .clickable { reloadKey += 1 }
                            .padding(vertical = 6.dp),
                    )
                }

                Spacer(Modifier.height(WcSpace.s2))

                val keyword = query.trim()
                val matched = list.filter { app ->
                    keyword.isEmpty() ||
                        app.label.contains(keyword, ignoreCase = true) ||
                        app.packageName.contains(keyword, ignoreCase = true)
                }

                // 搜索时不做折叠：用户明确在找东西，折叠只会让结果看起来没有
                val visible = if (keyword.isNotEmpty() || showComponents) {
                    matched
                } else {
                    matched.filter { it.hasLauncher }
                }
                val components = matched.count { !it.hasLauncher }

                LazyColumn(modifier = Modifier.heightIn(max = maxListHeight)) {
                    items(visible, key = { it.packageName }) { app ->
                        AppRow(
                            app = app,
                            checked = app.packageName in selected,
                            onCheckedChange = { on ->
                                selected = if (on) selected + app.packageName
                                else selected - app.packageName
                            },
                        )
                    }

                    if (keyword.isEmpty() && !showComponents && components > 0) {
                        item {
                            Text(
                                text = Str.t("显示系统组件（$components）"),
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable { showComponents = true }
                                    .padding(vertical = WcSpace.s3),
                                fontSize = 13.sp,
                                color = WcColors.Accent,
                            )
                        }
                    }

                    if (visible.isEmpty()) {
                        item {
                            Text(
                                text = Str.t("没有匹配的应用"),
                                modifier = Modifier.padding(vertical = WcSpace.s3),
                                fontSize = 13.sp,
                                color = WcColors.Muted,
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(
                onClick = { onConfirm(apps.orEmpty().filter { it.packageName in selected }) },
                enabled = selected.isNotEmpty(),
            ) {
                Text(if (selected.isEmpty()) Str.t("加入") else Str.t("加入 ${selected.size} 个"))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(Str.t("取消")) }
        },
    )
}

@Composable
private fun AppRow(
    app: InstalledApp,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(enabled = app.readable) { onCheckedChange(!checked) }
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        SelectionMark(checked && app.readable)

        Column(
            modifier = Modifier
                .weight(1f)
                .padding(start = WcSpace.s3),
        ) {
            Text(
                text = app.label,
                fontSize = 13.5.sp,
                color = if (app.readable) WcColors.Text else WcColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )

            val detail = buildString {
                append(app.versionText)
                append(" · ")
                append(formatAppSize(app.totalBytes))
                if (app.parts.size > 1) append(Str.t(" · 分体 ${app.parts.size}"))
                if (app.isSystem) append(Str.t(" · 系统"))
                if (!app.readable) append(Str.t(" · 读不到安装包"))
            }
            Text(
                text = detail,
                fontSize = 11.sp,
                color = WcColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/** 自绘勾选框：Material 的 Checkbox 带 48dp 最小点击区，会把行撑高。 */
@Composable
private fun SelectionMark(checked: Boolean) {
    val shape = androidx.compose.foundation.shape.RoundedCornerShape(4.dp)

    Box(
        modifier = Modifier
            .size(18.dp)
            .clip(shape)
            .background(if (checked) WcColors.Accent else Color.Transparent)
            .border(1.5.dp, if (checked) WcColors.Accent else WcColors.Border, shape),
        contentAlignment = Alignment.Center,
    ) {
        if (checked) {
            Text(text = "✓", fontSize = 11.sp, color = Color.White)
        }
    }
}

private fun formatAppSize(bytes: Long): String = when {
    bytes >= 1024L * 1024 * 1024 -> String.format("%.2f GB", bytes / 1024.0 / 1024 / 1024)
    bytes >= 1024L * 1024 -> String.format("%.1f MB", bytes / 1024.0 / 1024)
    bytes >= 1024L -> String.format("%.0f KB", bytes / 1024.0)
    else -> "$bytes B"
}
