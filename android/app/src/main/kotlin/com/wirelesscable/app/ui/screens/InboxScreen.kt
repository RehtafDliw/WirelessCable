package com.wirelesscable.app.ui.screens

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.Icon
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.core.files.FileEntry
import com.wirelesscable.app.core.files.InboxStore
import com.wirelesscable.app.ui.components.FileIcon
import com.wirelesscable.app.ui.components.ImageIcon
import com.wirelesscable.app.ui.components.WcCard
import com.wirelesscable.app.ui.components.WcCardList
import com.wirelesscable.app.ui.components.WcDialog
import com.wirelesscable.app.ui.components.WcLinkButton
import com.wirelesscable.app.ui.components.WcPrimaryButton
import com.wirelesscable.app.ui.components.WcRowDivider
import com.wirelesscable.app.ui.components.WcSelectionBar
import com.wirelesscable.app.ui.components.WcSelectionMark
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSize
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.vm.InboxUiState
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import com.wirelesscable.app.core.i18n.Str

/**
 * 「传输目录」：电脑发到手机的文件，按时间倒序（含当面直连落在 `直连/` 的那批）。
 *
 * 交互和「传输中心」是同一套：**短按打开、长按进多选、多选态短按勾选**，
 * 批量动作走底部条。两个列表用同一套手势，用户不用记第二套规则。
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun InboxScreen(
    state: InboxUiState,
    onRefresh: () -> Unit,
    onOpen: (FileEntry) -> Unit,
    onOpenAllFiles: () -> Unit,
    onDismissMessage: () -> Unit,
    onStartSelection: (String) -> Unit,
    onToggleSelect: (String) -> Unit,
    onClearSelection: () -> Unit,
    onSelectAll: () -> Unit,
    onDeleteSelected: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var confirmingDelete by remember { mutableStateOf(false) }

    // 每次切到这一页都重扫：手机可能刚收到文件
    LaunchedEffect(Unit) { onRefresh() }

    Column(modifier = modifier.fillMaxSize()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (state.hasSelection) {
                Text(
                    text = Str.t(state.selectionText),
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = WcColors.Text2,
                    modifier = Modifier.weight(1f),
                )
                WcLinkButton(text = Str.t("全选"), onClick = onSelectAll, color = WcColors.Muted)
                WcLinkButton(text = Str.t("完成"), onClick = onClearSelection, color = WcColors.Muted)
            } else {
                Text(
                    text = if (state.entries.isEmpty()) InboxStore.DISPLAY_NAME else Str.t("${state.entries.size} 个文件"),
                    fontSize = 13.sp,
                    color = WcColors.Muted,
                    modifier = Modifier.weight(1f),
                )
                WcLinkButton(text = Str.t("刷新"), onClick = onRefresh, color = WcColors.Muted)
            }
        }

        state.message?.let { raw ->
            val message = Str.t(raw)
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 2.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = message,
                    fontSize = 12.5.sp,
                    color = WcColors.Error,
                    modifier = Modifier.weight(1f),
                )
                WcLinkButton(text = Str.t("知道了"), onClick = onDismissMessage, color = WcColors.Muted)
            }
        }

        if (!state.granted) {
            NotGrantedInbox(onOpenAllFiles = onOpenAllFiles)
            return@Column
        }

        if (state.entries.isEmpty()) {
            Box(
                // weight 而不是 fillMaxSize：底部操作条要占自己的位置，
                // 否则列表把整列高度吃光，多选条会被压到标签栏底下（看不见也点不到）
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .padding(24.dp),
                contentAlignment = Alignment.TopCenter,
            ) {
                Text(
                    text = if (state.loading) Str.t("读取中…") else Str.t("还没有收到文件"),
                    fontSize = 13.sp,
                    color = WcColors.Muted,
                )
            }
            return@Column
        }

        LazyColumn(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f)
                .padding(horizontal = WcSpace.s4),
        ) {
            item {
                WcCardList {
                    state.entries.forEachIndexed { index, entry ->
                        if (index > 0) WcRowDivider()
                        InboxRow(
                            entry = entry,
                            selected = entry.relativePath in state.selected,
                            selectionMode = state.hasSelection,
                            onOpen = { onOpen(entry) },
                            onStartSelection = { onStartSelection(entry.relativePath) },
                            onToggleSelect = { onToggleSelect(entry.relativePath) },
                        )
                    }
                }
                Spacer(Modifier.height(WcSpace.s4))
            }
        }

        if (state.hasSelection) {
            WcSelectionBar(text = Str.t(state.selectionText), onDone = onClearSelection) {
                if (state.selected.size == 1) {
                    val entry = state.entries.firstOrNull { it.relativePath in state.selected }
                    if (entry != null) {
                        WcLinkButton(text = Str.t("打开"), onClick = { onOpen(entry) })
                    }
                }
                WcLinkButton(
                    text = Str.t("删除"),
                    onClick = { confirmingDelete = true },
                    color = WcColors.Error,
                )
            }
        }
    }

    if (confirmingDelete) {
        WcDialog(
            onDismissRequest = { confirmingDelete = false },
            title = { Text(Str.t("删除文件")) },
            text = {
                Text(
                    text = Str.t("删除 ${state.selected.size} 个文件？文件会从手机里删掉，不能恢复。"),
                    fontSize = 13.sp,
                    color = WcColors.Text,
                )
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        confirmingDelete = false
                        onDeleteSelected()
                    },
                ) {
                    Text(Str.t("删除"), color = WcColors.Error)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmingDelete = false }) { Text(Str.t("取消")) }
            },
        )
    }
}

@Composable
private fun NotGrantedInbox(onOpenAllFiles: () -> Unit) {
    Column(modifier = Modifier.padding(horizontal = 16.dp)) {
        WcCard {
            Text(
                text = Str.t("接收电脑文件需要「所有文件访问权」"),
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium,
                color = WcColors.Text,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                text = Str.t("开启后，电脑发来的文件会出现在这里。"),
                fontSize = 12.5.sp,
                color = WcColors.Muted,
            )
            Spacer(Modifier.height(12.dp))
            WcPrimaryButton(text = Str.t("去开启"), onClick = onOpenAllFiles)
        }
    }
}

private val InboxTimeFormat = SimpleDateFormat("MM-dd HH:mm", Locale.getDefault())

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun InboxRow(
    entry: FileEntry,
    selected: Boolean,
    selectionMode: Boolean,
    onOpen: () -> Unit,
    onStartSelection: () -> Unit,
    onToggleSelect: () -> Unit,
) {
    // 与「电脑文件」页同一套密度：单行、无行内按钮
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(WcSize.RowHeightCompact)
            .background(if (selected) WcColors.AccentSoft else androidx.compose.ui.graphics.Color.Transparent)
            .combinedClickable(
                onClick = { if (selectionMode) onToggleSelect() else onOpen() },
                onLongClick = { if (selectionMode) onToggleSelect() else onStartSelection() },
                onLongClickLabel = Str.t("多选"),
            )
            .padding(horizontal = WcSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (selectionMode) {
            WcSelectionMark(checked = selected)
            Spacer(Modifier.width(WcSpace.s3))
        } else {
            Box(
                modifier = Modifier
                    .size(26.dp)
                    .clip(SquircleShape(WcRadius.r1))
                    .background(WcColors.Well),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = if (entry.name.substringAfterLast('.', "").lowercase() in
                        setOf("png", "jpg", "jpeg", "webp", "gif", "bmp", "svg", "heic")
                    ) {
                        ImageIcon
                    } else {
                        FileIcon
                    },
                    contentDescription = null,
                    tint = WcColors.Text2,
                    modifier = Modifier.size(15.dp),
                )
            }
            Spacer(Modifier.width(WcSpace.s3))
        }

        Text(
            text = entry.name,
            fontSize = 13.5.sp,
            color = WcColors.Text,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        Spacer(Modifier.width(8.dp))
        Text(
            text = "${formatSize(entry.sizeBytes)} · ${InboxTimeFormat.format(Date(entry.modifiedUnixMs))}",
            fontSize = WcType.captionSize,
            color = WcColors.Muted,
        )
    }
}
