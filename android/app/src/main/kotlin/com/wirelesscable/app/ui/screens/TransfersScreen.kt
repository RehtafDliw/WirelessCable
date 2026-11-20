package com.wirelesscable.app.ui.screens

import android.net.Uri
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.ui.components.WcDialog
import com.wirelesscable.app.core.protocol.TransferStates
import com.wirelesscable.app.core.transfer.TransferFormat
import com.wirelesscable.app.core.transfer.TransferItem
import com.wirelesscable.app.ui.components.CheckIcon
import com.wirelesscable.app.ui.components.CloseIcon
import com.wirelesscable.app.ui.components.WcSelectionMark
import com.wirelesscable.app.ui.components.PauseIcon
import com.wirelesscable.app.ui.components.PlayIcon
import com.wirelesscable.app.ui.components.RefreshIcon
import com.wirelesscable.app.ui.components.WcCardList
import com.wirelesscable.app.ui.components.WcIconAction
import com.wirelesscable.app.ui.components.WcLinkButton
import com.wirelesscable.app.ui.components.WcRing
import com.wirelesscable.app.ui.components.WcRowAction
import com.wirelesscable.app.ui.components.WcRowDivider
import com.wirelesscable.app.ui.components.WcSegmented
import com.wirelesscable.app.ui.components.WcSelectionBar
import com.wirelesscable.app.ui.components.WcSelectionMark
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSize
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.vm.TransferFilter
import com.wirelesscable.app.vm.TransferTab
import com.wirelesscable.app.vm.TransferRow
import com.wirelesscable.app.vm.TransferUiState
import com.wirelesscable.app.core.i18n.Str

/** 行首选择槽宽度：圆环与勾选框共用同一个槽，进出多选时行内文字不会左右跳。 */
private val SelectionSlotWidth = 44.dp

/**
 * 传输中心。
 *
 * 密度优先（2026-11-16 裁定）：一屏要能看十几条，所以
 * 一行 = 环形进度 + 名称 + 「方向 · 速度 · 剩余」+ 一个动作按钮，行高 56。
 * 暂停/继续只保留最常用的一个；取消、打开、删除记录走多选后的底部栏
 * （多选入口在分段控件右侧，长按任意行也能进）。
 *
 * 所有数字都来自任务列表（服务端下发 + 本地字节计数），页面不做任何推算；
 * 速度没有可信采样时就显示实际值，不编一个出来。
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TransfersScreen(
    state: TransferUiState,
    onSaveDownload: (TransferItem, Uri) -> Unit,
    onCancel: (List<String>) -> Unit,
    onPause: (List<String>) -> Unit,
    onResume: (List<String>) -> Unit,
    onSelectTab: (TransferTab) -> Unit,
    onSelectFilter: (TransferFilter) -> Unit,
    onStartSelection: (List<String>) -> Unit,
    onToggleSelect: (List<String>) -> Unit,
    onSelectAll: () -> Unit,
    onClearSelection: () -> Unit,
    onDeleteRecords: (Boolean) -> Unit,
    /** 「传输已被接收方取消」的询问：true = 删掉本机这条记录，false = 只是关掉提示。 */
    onResolvePeerCancelled: (Boolean) -> Unit,
    /** 行上的「取消」确认后：作废队列、删记录，[Boolean] = 是否连已落盘的文件一起删。 */
    onDeleteCancelled: (List<String>, Boolean) -> Unit,
    onOpenSaved: (String) -> Unit,
    onRetryReceive: (List<String>) -> Unit,
    modifier: Modifier = Modifier,
) {
    var confirmingDelete by remember { mutableStateOf(false) }
    var deleteFiles by remember { mutableStateOf(false) }

    // 行上的「取消」：点了先把任务停住再弹确认——
    // 用户还在犹豫的时候文件继续往电脑上流，是这段交互最容易出问题的地方。
    var cancelTarget by remember { mutableStateOf<TransferRow?>(null) }
    var cancelWithFiles by remember { mutableStateOf(false) }

    val active = state.tab == TransferTab.ACTIVE
    // 同一次「下载整个文件夹」的任务在 state 里已经收成一行了，这里直接用
    val rows = state.rows

    Column(modifier = modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = WcSpace.s4, vertical = WcSpace.s2),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            WcSegmented(
                options = listOf(Str.t(state.activeTabLabel), Str.t(state.completedTabLabel)),
                index = if (active) 0 else 1,
                onSelect = { onSelectTab(if (it == 0) TransferTab.ACTIVE else TransferTab.COMPLETED) },
                modifier = Modifier.weight(1f),
            )
            if (rows.isNotEmpty()) {
                Spacer(Modifier.width(WcSpace.s2))
                WcIconAction(
                    icon = CheckIcon,
                    onClick = { onStartSelection(rows.first().taskIds) },
                    contentDescription = Str.t("多选"),
                )
            }
        }

        // 方向筛选：与上面的传输中/已完成正交，一条记录不是发的就是收的
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = WcSpace.s4, vertical = 2.dp),
            horizontalArrangement = Arrangement.spacedBy(WcSpace.s1),
        ) {
            TransferFilter.entries.forEach { filter ->
                val active = state.direction == filter
                Text(
                    text = when (filter) {
                        TransferFilter.ALL -> Str.t("全部")
                        TransferFilter.SENT -> Str.t("我发送的")
                        TransferFilter.RECEIVED -> Str.t("我收到的")
                    },
                    fontSize = WcType.labelSize,
                    fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                    color = if (active) WcColors.AccentText else WcColors.Muted,
                    modifier = Modifier
                        .clip(SquircleShape(WcRadius.r1))
                        .background(if (active) WcColors.AccentSoft else WcColors.Soft)
                        .clickable { onSelectFilter(filter) }
                        .padding(horizontal = 10.dp, vertical = 5.dp),
                )
            }
        }

        (state.refreshError ?: state.message)?.let { raw ->
            // 网络层只会说连接超时，用户不知道下一步该干什么；
            // 这一页能做的只有回首页点重连，直接写出来。
            // 注意比较的是**原文**：拿译文去比中文原文永远不相等（踩过）。
            val text = if (raw == "连接超时") {
                Str.t("连不上电脑（超时）。到首页点「重连」再试")
            } else {
                Str.t(raw)
            }
            Text(
                text = text,
                fontSize = WcType.labelSize,
                color = WcColors.Error,
                modifier = Modifier.padding(horizontal = WcSpace.s4, vertical = 2.dp),
            )
        }

        Column(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = WcSpace.s4),
        ) {
            Spacer(Modifier.height(WcSpace.s2))

            if (rows.isEmpty()) {
                EmptyBlock(
                    title = Str.t(state.emptyHint),
                    detail = when {
                        !state.ready -> Str.t("读取中")
                        active -> Str.t("在电脑上选择文件发送，或从「文件」页发送")
                        else -> Str.t("传完的文件会归档到这里")
                    },
                )
            } else {
                WcCardList {
                    rows.forEachIndexed { index, row ->
                        if (index > 0) WcRowDivider()

                        when (row) {
                            is TransferRow.Single -> TransferItemRow(
                                item = row.item,
                                selected = row.item.taskId in state.selected,
                                selectionMode = state.hasSelection,
                                onClick = {
                                    when {
                                        // 多选态：短按 = 勾选
                                        state.hasSelection -> onToggleSelect(row.taskIds)
                                        // 非多选态：短按 = 打开本地文件
                                        // （以前这个分支是空的，普通点一下什么都不会发生）
                                        row.item.isOpenableHere -> onOpenSaved(row.item.taskId)
                                    }
                                },
                                onLongClick = {
                                    if (state.hasSelection) {
                                        onToggleSelect(row.taskIds)
                                    } else {
                                        onStartSelection(row.taskIds)
                                    }
                                },
                                // 点左边那个圈 = 选中（用户会把它当勾选框按）
                                onSelectOnly = { onStartSelection(row.taskIds) },
                                onPause = { onPause(row.taskIds) },
                                onResume = { onResume(row.taskIds) },
                                onRetry = { onRetryReceive(row.taskIds) },
                                onCancel = {
                                    onPause(row.taskIds)
                                    cancelTarget = row
                                    cancelWithFiles = false
                                },
                                inboxBlocked = state.inboxBlocked,
                            )

                            // 整文件夹：一行代表整批。暂停/继续/取消/删除都作用到整批，
                            // 200 个文件不该刷满 200 行，也不该只停正在传的那一个。
                            is TransferRow.Batch -> TransferBatchRow(
                                group = row,
                                selected = row.taskIds.all { it in state.selected },
                                selectionMode = state.hasSelection,
                                onClick = { if (state.hasSelection) onToggleSelect(row.taskIds) },
                                onLongClick = {
                                    if (state.hasSelection) {
                                        onToggleSelect(row.taskIds)
                                    } else {
                                        onStartSelection(row.taskIds)
                                    }
                                },
                                onSelectOnly = { onStartSelection(row.taskIds) },
                                onPause = { onPause(row.taskIds) },
                                onResume = { onResume(row.taskIds) },
                                onRetry = { onRetryReceive(row.taskIds) },
                                onCancel = {
                                    onPause(row.taskIds)
                                    cancelTarget = row
                                    cancelWithFiles = false
                                },
                                inboxBlocked = state.inboxBlocked,
                            )
                        }
                    }
                }
            }

            Spacer(Modifier.height(WcSpace.s4))
        }

        if (state.hasSelection) {
            // 全都选中了就变成「取消全选」：一个按钮顶两用，不多占地方
            val allSelected = state.rows.isNotEmpty() &&
                state.rows.all { row -> row.taskIds.all { it in state.selected } }

            WcSelectionBar(text = Str.t(state.selectionText), onDone = onClearSelection) {
                WcLinkButton(
                    text = if (allSelected) Str.t("取消全选") else Str.t("全选"),
                    onClick = { if (allSelected) onClearSelection() else onSelectAll() },
                    color = if (allSelected) WcColors.Muted else WcColors.Accent,
                )

                state.openableItem?.let { item ->
                    WcLinkButton(text = Str.t("打开"), onClick = { onOpenSaved(item.taskId) })
                }
                // 选中了几行就取消几行：单选时这是一条，整批时是整批的任务 id
                val cancellable = state.selectedRows
                    .filter { it.isActive }
                    .flatMap { it.taskIds }

                if (cancellable.isNotEmpty()) {
                    WcLinkButton(
                        text = Str.t("取消任务"),
                        onClick = { onCancel(cancellable) },
                        color = WcColors.Error,
                    )
                }
                // **传输中的条目只给「取消」，不给「删除记录」**（需求 4）：
                // 选中的行全都在传的话，这个按钮直接不出现。删记录是"清理历史"，
                // 而传输中的条目只有"取消传输"一个动作，两个放一起容易点错。
                // 电脑端是同样的做法（传输中的整批菜单里不出现删除项）。
                val allInFlight = state.selectedRows.isNotEmpty() &&
                    state.selectedRows.all { row -> row.isActive }

                if (!allInFlight) {
                    WcLinkButton(
                        text = Str.t("删除"),
                        onClick = {
                            deleteFiles = false
                            confirmingDelete = true
                        },
                        color = WcColors.Error,
                    )
                }
            }
        }
    }

    // 行上的「取消」：点按钮的那一刻已经暂停了，这里只决定"删掉"还是"继续"。
    // 关掉弹窗（点外面/返回）等同于继续——用户没有明确说要删，就不能替他删。
    cancelTarget?.let { target ->
        val isBatch = target is TransferRow.Batch
        WcDialog(
            onDismissRequest = {
                onResume(target.taskIds)
                cancelTarget = null
            },
            title = { Text(Str.t(if (isBatch) "取消这批传输？" else "取消这个传输？")) },
            text = {
                Column {
                    Text(
                        text = Str.t("任务已暂停。点「确定」删除记录，点「取消」继续传输。"),
                        fontSize = 13.sp,
                        color = WcColors.Text,
                    )
                    if (isBatch) {
                        Spacer(Modifier.height(8.dp))
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clickable { cancelWithFiles = !cancelWithFiles },
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            WcSelectionMark(checked = cancelWithFiles)
                            Spacer(Modifier.width(8.dp))
                            Text(
                                text = Str.t("同时删除已完成的文件"),
                                fontSize = 13.sp,
                                color = WcColors.Text,
                            )
                        }
                        Text(
                            text = Str.t("只删本机已经收到的；没传完的会一起作废。"),
                            fontSize = 11.5.sp,
                            color = WcColors.Muted,
                            modifier = Modifier.padding(start = 26.dp),
                        )
                    }
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        val ids = target.taskIds
                        val withFiles = isBatch && cancelWithFiles
                        cancelTarget = null
                        onDeleteCancelled(ids, withFiles)
                    },
                ) {
                    Text(Str.t("确定"), color = WcColors.Error)
                }
            },
            dismissButton = {
                TextButton(
                    onClick = {
                        onResume(target.taskIds)
                        cancelTarget = null
                    },
                ) {
                    Text(Str.t("取消"))
                }
            },
        )
    }

    if (confirmingDelete) {
        WcDialog(
            onDismissRequest = { confirmingDelete = false },
            title = { Text(Str.t("删除传输记录")) },
            text = {
                Column {
                    val rows = state.selectedRows.size
                    val files = state.selected.size
                    Text(
                        text = if (files == rows) {
                            Str.t("删除 $rows 条记录？")
                        } else {
                            // 整文件夹选中时把文件数写出来：用户点的是一行，
                            // 实际要删的是几十上百条记录，得让他看清
                            Str.t("删除 $rows 项（共 $files 个文件）的记录？")
                        },
                        fontSize = 13.sp,
                        color = WcColors.Text,
                    )

                    if (state.canDeleteFiles) {
                        Spacer(Modifier.height(8.dp))
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clickable { deleteFiles = !deleteFiles },
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Checkbox(checked = deleteFiles, onCheckedChange = { deleteFiles = it })
                            Text(
                                text = Str.t("同时删除接收到的文件"),
                                fontSize = 13.sp,
                                color = WcColors.Text,
                            )
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        confirmingDelete = false
                        onDeleteRecords(deleteFiles && state.canDeleteFiles)
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

    // 点「继续」才发现对面已经删了这条（删除会先暂停再取消）。
    // 以前这里只有一句"任务已结束"，行留在列表里、永远传不动；
    // 现在问一句要不要把本机记录也清掉。
    if (state.peerCancelled.isNotEmpty()) {
        WcDialog(
            onDismissRequest = { onResolvePeerCancelled(false) },
            title = { Text(Str.t("传输已被接收方取消")) },
            text = {
                Text(
                    text = Str.t("这条传输已经被对方删除，继续也不会再传。要删掉本机的这条记录吗？"),
                    fontSize = 13.sp,
                    color = WcColors.Text,
                )
            },
            confirmButton = {
                TextButton(onClick = { onResolvePeerCancelled(true) }) {
                    Text(Str.t("删除记录"), color = WcColors.Error)
                }
            },
            dismissButton = {
                TextButton(onClick = { onResolvePeerCancelled(false) }) { Text(Str.t("保留记录")) }
            },
        )
    }
}

/** 空状态：一句标题 + 一句落点，不放插图。 */
@Composable
private fun EmptyBlock(title: String, detail: String) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = WcSpace.s6),
    ) {
        Text(text = title, fontSize = WcType.bodySize, color = WcColors.Text)
        Spacer(Modifier.height(WcSpace.s1))
        Text(text = detail, fontSize = WcType.labelSize, color = WcColors.Muted)
    }
}

/**
 * 一行任务。
 *
 * 环形进度承载完成到哪，第二行的灰字承载谁传给谁、多快、还剩多久。
 * 失败原因直接顶掉灰字——错误信息比速度重要。
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TransferItemRow(
    item: TransferItem,
    selected: Boolean,
    selectionMode: Boolean,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
    /** 点左侧的圈：直接进多选并选中这一条。 */
    onSelectOnly: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onRetry: () -> Unit,
    onCancel: () -> Unit,
    inboxBlocked: Boolean,
) {
    val completed = item.state == TransferStates.COMPLETED
    val shownProgress = if (completed) 1f else item.progress
    val failed = item.state == TransferStates.FAILED || item.state == TransferStates.CANCELLED
    val paused = item.state == TransferStates.PAUSED
    val tint = when {
        completed -> WcColors.Success
        failed -> WcColors.Error
        paused -> WcColors.Muted
        else -> WcColors.Accent
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(WcSize.RowHeight)
            .background(if (selected) WcColors.AccentSoft else Color.Transparent)
            .combinedClickable(
                onClick = onClick,
                onLongClick = onLongClick,
                onLongClickLabel = Str.t("多选"),
            )
            .padding(horizontal = WcSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (selectionMode) {
            // 和圆环用同一个槽位，进出多选时文字不会左右跳
            Box(
                modifier = Modifier
                    .width(SelectionSlotWidth)
                    .fillMaxHeight(),
                contentAlignment = Alignment.Center,
            ) {
                WcSelectionMark(checked = selected)
            }
        } else {
            // 左侧的圈**就是**勾选框的入口：点它进多选并选中这一条。
            // 用户看到 100% 的绿圈会本能地当勾选框去按，之前按它只会打开文件，
            // 于是已完成里的多选怎么点都没反应。点击区放大到 40dp，比 34dp 的圆圈好按。
            Box(
                modifier = Modifier
                    .width(SelectionSlotWidth)
                    .fillMaxHeight()
                    // 点击区做满整行高度：只圈 40dp 时，手指偏几像素就落到打开文件上
                    .clickable(onClickLabel = Str.t("多选"), onClick = onSelectOnly),
                contentAlignment = Alignment.Center,
            ) {
                if (completed) {
                    // 已完成就是 100%，再画一个100% 环只是噪音；
                    // 换成绿点对勾，既表示状态，也明确这条已经完事
                    DoneMark()
                } else {
                    WcRing(
                        progress = shownProgress,
                        color = tint,
                        label = if (item.totalBytes > 0L) "${(shownProgress * 100).toInt()}%" else null,
                    )
                }
            }
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = item.fileName,
                fontSize = 13.5.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = item.metaLine(inboxBlocked),
                fontSize = WcType.captionSize,
                color = if (item.errorMessage != null) WcColors.Error else WcColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        Spacer(Modifier.width(WcSpace.s2))

        when {
            item.isActive -> {
                WcRowAction(PauseIcon, onPause, contentDescription = Str.t("暂停"))
                Spacer(Modifier.width(2.dp))
                WcRowAction(CloseIcon, onCancel, contentDescription = Str.t("取消"))
            }

            paused -> {
                WcRowAction(PlayIcon, onResume, contentDescription = Str.t("继续"))
                Spacer(Modifier.width(2.dp))
                WcRowAction(CloseIcon, onCancel, contentDescription = Str.t("取消"))
            }

            item.needsSave && item.state == TransferStates.FAILED ->
                WcRowAction(RefreshIcon, onRetry, contentDescription = Str.t("重试"))
        }
    }
}

/**
 * 「已完成」的标记：绿色圆底 + 对勾。
 *
 * 原来这里画的是 100% 的进度环——已完成就是 100%，那圈数字只是噪音；
 * 而且绿环看上去像个勾选框，用户会去点它。现在它就是勾选框的入口（外面那层可点）。
 */
@Composable
private fun DoneMark() {
    Box(
        modifier = Modifier
            .size(30.dp)
            .clip(CircleShape)
            .background(WcColors.SuccessPill),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            imageVector = CheckIcon,
            contentDescription = null,
            tint = WcColors.Success,
            modifier = Modifier.size(16.dp),
        )
    }
}

/**
 * 整文件夹那一行。
 *
 * 200 个文件挂在同一行上：环形进度按整批字节算，标题是文件夹名，
 * 第二行写已完成 12/200 个文件——用户要的就是这个文件夹收了多少，
 * 而不是 200 个文件名。
 *
 * 行上的暂停/继续/取消/删除都作用到**整批**（这是用户定的语义：
 * 一个文件夹挂着 200 个任务时，暂停就是整批停，删除就是整批删）。
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TransferBatchRow(
    group: TransferRow.Batch,
    selected: Boolean,
    selectionMode: Boolean,
    onClick: () -> Unit,
    onLongClick: () -> Unit,
    /** 点左侧的圈：直接进多选并选中整批。 */
    onSelectOnly: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onRetry: () -> Unit,
    onCancel: () -> Unit,
    inboxBlocked: Boolean,
) {
    val allDone = group.completedCount == group.fileCount
    val paused = group.allPaused
    val failed = group.allFailed

    // 暂停键的显示条件不能用 `isActive`：电脑每次只下发 8 个作业，一个都做完、
    // 下一批还没到的那一两秒里整批没有"进行中"的条目，键会消失再出现，
    // 边界文件一多就看着一直在闪。改成"还没传完 + 最近 8 秒动过"，跨过下发间隙。
    val showPause = !paused && (group.isActive || (!allDone && group.isLive(System.currentTimeMillis())))
    val tint = when {
        allDone -> WcColors.Success
        failed -> WcColors.Error
        paused -> WcColors.Muted
        else -> WcColors.Accent
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(WcSize.RowHeight)
            .background(if (selected) WcColors.AccentSoft else Color.Transparent)
            .combinedClickable(
                onClick = onClick,
                onLongClick = onLongClick,
                onLongClickLabel = Str.t("多选"),
            )
            .padding(horizontal = WcSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (selectionMode) {
            Box(
                modifier = Modifier
                    .width(SelectionSlotWidth)
                    .fillMaxHeight(),
                contentAlignment = Alignment.Center,
            ) {
                WcSelectionMark(checked = selected)
            }
        } else {
            // 同单条：点圈 = 选中整批
            Box(
                modifier = Modifier
                    .width(SelectionSlotWidth)
                    .fillMaxHeight()
                    .clickable(onClickLabel = Str.t("多选"), onClick = onSelectOnly),
                contentAlignment = Alignment.Center,
            ) {
                if (allDone) {
                    DoneMark()
                } else {
                    WcRing(
                        progress = group.progress,
                        color = tint,
                        label = if (group.totalBytes > 0L) "${(group.progress * 100).toInt()}%" else null,
                    )
                }
            }
        }

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = group.name,
                fontSize = 13.5.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                text = group.metaLine(inboxBlocked),
                fontSize = WcType.captionSize,
                color = WcColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        Spacer(Modifier.width(WcSpace.s2))

        when {
            showPause -> {
                WcRowAction(PauseIcon, onPause, contentDescription = Str.t("暂停整批"))
                Spacer(Modifier.width(2.dp))
                WcRowAction(CloseIcon, onCancel, contentDescription = Str.t("取消整批"))
            }

            paused -> {
                WcRowAction(PlayIcon, onResume, contentDescription = Str.t("继续整批"))
                Spacer(Modifier.width(2.dp))
                WcRowAction(CloseIcon, onCancel, contentDescription = Str.t("取消整批"))
            }

            failed -> WcRowAction(RefreshIcon, onRetry, contentDescription = Str.t("重试整批"))
        }
    }
}

/**
 * 整批的第二行：方向 · 已完成几个 · 总大小（在传时追加速度）。
 *
 * 不写速度以外的推算：整批的剩余时间要按剩余字节 ÷ 当前总速度算，
 * 几个文件速度不均时这个数会跳得很厉害，宁可不显示。
 */
private fun TransferRow.Batch.metaLine(inboxBlocked: Boolean): String {
    val parts = mutableListOf<String>()
    // 用本端视角的「接收/发送」：以前两端都写"本机"，指的是各自的自己，同一条传输读起来正好相反
    lead.deviceName.takeIf { it.isNotBlank() }?.let(parts::add)
    parts += Str.t(if (isUpload) "发送" else "接收")
    parts += if (completedCount == fileCount) {
        Str.t("$fileCount 个文件")
    } else {
        Str.t("已完成 $completedCount/$fileCount 个文件")
    }
    // 有整批总量时写成「已传 / 总共」，和进度条同一个分母；
    // 没有就退回已见条目的总大小（老数据）。
    parts += if (plannedBytes > 0L) {
        "${TransferFormat.bytes(transferredBytes)} / ${TransferFormat.bytes(plannedBytes)}"
    } else {
        TransferFormat.bytes(totalBytes)
    }

    if (isActive && !allPaused && bytesPerSecond > 0L) {
        parts += TransferFormat.speed(bytesPerSecond)
    }

    // 缺「所有文件访问权」时整批都卡在等待，不说清楚用户只会觉得没反应
    if (inboxBlocked && entries.any { it.needsSave }) {
        parts += Str.t("需要「所有文件访问权限」")
    }

    return parts.joinToString(" · ")
}

/**
 * 第二行：方向 · 状态 · 速度 · 剩余。
 *
 * 状态语必须来自真实状态机；等待接收时还要区分缺所有文件访问权与还没轮到，
 * 否则用户看到的永远是「接收中…」，会以为卡死。
 */
private fun TransferItem.metaLine(inboxBlocked: Boolean): String {
    val direction = buildList {
        deviceName.takeIf { it.isNotBlank() }?.let(::add)
        add(Str.t(if (isUpload) "发送" else "接收"))
    }.joinToString(" · ")

    if (needsSave) {
        when (state) {
            TransferStates.WAITING ->
                return if (inboxBlocked) Str.t("需要「所有文件访问权限」") else Str.t("等待接收")
            TransferStates.INTERRUPTED -> return Str.t("已中断，会自动续传")
        }
    }

    errorMessage?.let { return Str.t(it) }

    if (state == TransferStates.COMPLETED) {
        return "$direction · ${TransferFormat.bytes(totalBytes)}"
    }

    if (!isActive) {
        return "$direction · ${TransferFormat.stateLabel(state)}"
    }

    // 正在传的时候不必再写传输中：环形进度和暂停键已经说明了一切，
    // 这一行留给多快、还剩多久（准备中/校验中这类中间态才写状态）
    val parts = mutableListOf(direction)
    if (bytesPerSecond > 0L) parts += TransferFormat.speed(bytesPerSecond)
    if (state != TransferStates.TRANSFERRING) parts += TransferFormat.stateLabel(state)
    TransferFormat.remaining(transferredBytes, totalBytes, bytesPerSecond)?.let { parts += it }
    return parts.joinToString(" · ")
}
