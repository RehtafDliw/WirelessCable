package com.wirelesscable.app.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
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
import com.wirelesscable.app.core.protocol.FileEntryDto
import com.wirelesscable.app.core.protocol.FileRootDto
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.ui.components.ArrowLeftIcon
import com.wirelesscable.app.ui.components.FileIcon
import com.wirelesscable.app.ui.components.FolderIcon
import com.wirelesscable.app.ui.components.ImageIcon
import com.wirelesscable.app.ui.components.PlusIcon
import com.wirelesscable.app.ui.components.SearchIcon
import com.wirelesscable.app.ui.components.SortIcon
import com.wirelesscable.app.ui.components.UploadIcon
import com.wirelesscable.app.ui.components.WcIconAction
import com.wirelesscable.app.ui.components.WcLinkButton
import com.wirelesscable.app.ui.components.WcPrimaryButton
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSize
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.vm.FileBrowserUiState
import com.wirelesscable.app.vm.FileSortBy
import com.wirelesscable.app.vm.TargetOperation
import com.wirelesscable.app.core.i18n.Str

/**
 * 文件浏览页。
 *
 * 交互按安卓文件管理器的惯例来：
 * - 点一下 → 进目录 / 下载文件；
 * - 长按 → 进入多选，左侧变复选框，底部出现操作栏（属性/重命名/移动/复制/删除）；
 * - 点「移动」或「复制」→ 进入目标目录选择，底部变成「移动到此处」/「复制到此处」。
 *
 * 行内不放任何按钮：一行只承载名称 + 大小 + 时间，密度优先。
 */
@Composable
fun FileBrowserScreen(
    state: FileBrowserUiState,
    onOpenRoot: (String) -> Unit,
    onEnter: (FileEntryDto) -> Unit,
    onGoUp: () -> Unit,
    onSearch: (String) -> Unit,
    onToggleSelect: (FileEntryDto) -> Unit,
    onClearSelection: () -> Unit,
    onDeleteSelected: (Boolean) -> Unit,
    onCreateDirectory: (String) -> Unit,
    onRename: (String, String) -> Unit,
    /** 上传到当前所在的电脑目录。 */
    onUploadHere: () -> Unit,
    /** 切换排序方式。 */
    onSortBy: (FileSortBy) -> Unit,
    /** 查看条目属性。 */
    onShowInfo: (FileEntryDto) -> Unit,
    /** 关闭属性详情。 */
    onDismissInfo: () -> Unit,
    /** 批量下载选中的文件（让用户选一个目标目录）。 */
    onBatchDownload: () -> Unit,
    /** 下载整个文件夹：服务端展开成一批任务，落点由用户选。 */
    onDownloadFolder: (FileEntryDto) -> Unit,
    /** 把选中项挂起为移动，进入目标目录选择。 */
    onBeginMove: () -> Unit,
    /** 把选中项挂起为复制，进入目标目录选择。 */
    onBeginCopy: () -> Unit,
    /** 取消挂起的移动/复制。 */
    onCancelPending: () -> Unit,
    /** 在当前目录执行挂起的移动/复制。 */
    onConfirmPendingHere: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var showCreateDialog by remember { mutableStateOf(false) }
    var renameTarget by remember { mutableStateOf<FileEntryDto?>(null) }
    var deleteConfirm by remember { mutableStateOf(false) }

    val selectedEntries = state.entries.filter { it.relativePath in state.selected }
    val singleSelected = selectedEntries.singleOrNull()

    // 选择态与目标选择态都不给新建/上传：这时候用户的操作对象已经换成了选中项
    val mutatingAllowed = state.selected.isEmpty() && state.pending == null && !state.readOnly
    val atMachineRoot = state.currentPath.isEmpty() &&
        state.roots.firstOrNull { it.rootId == state.currentRootId }?.kind == FileProtocol.KIND_MACHINE

    Column(modifier = modifier.fillMaxSize()) {
        FileHeader(
            roots = state.roots,
            currentRootId = state.currentRootId,
            onOpenRoot = onOpenRoot,
            path = state.currentPath,
            canGoUp = state.currentPath.isNotEmpty(),
            onGoUp = onGoUp,
            sortBy = state.sortBy,
            onSortBy = onSortBy,
            onSearch = onSearch,
            onUpload = if (mutatingAllowed && state.canUploadHere) onUploadHere else null,
            onCreate = if (mutatingAllowed && state.currentRootId.isNotEmpty() && !atMachineRoot) {
                { showCreateDialog = true }
            } else {
                null
            },
        )

        state.message?.let { raw ->
            val message = Str.t(raw)
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 4.dp),
            ) {
                Text(message, color = WcColors.Error, fontSize = 12.5.sp)
            }
        }

        state.warning?.let { raw ->
            val warning = Str.t(raw)
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 4.dp),
            ) {
                Text(warning, color = WcColors.Error, fontSize = 12.5.sp)
            }
        }

        if (state.entries.isEmpty()) {
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .padding(top = 40.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(Str.t("空目录"), color = WcColors.Muted, fontSize = 13.sp)
            }
        } else {
            LazyColumn(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
            ) {
                items(state.visibleEntries, key = { it.relativePath }) { entry ->
                    EntryRow(
                        entry = entry,
                        selected = entry.relativePath in state.selected,
                        // 点文件 = 选中/取消；点目录 = 进入（目录要靠左侧勾选框选中，否则没法进目录）
                        onClick = {
                            when {
                                entry.isDirectory -> onEnter(entry)
                                state.pending == null -> onToggleSelect(entry)
                            }
                        },
                        onToggleSelect = {
                            // 选目标目录这一步只用来挑目录，勾选框先停用
                            if (state.pending == null) onToggleSelect(entry)
                        },
                    )
                }

                item { Spacer(Modifier.height(12.dp)) }
            }
        }

        when {
            state.pending != null -> PendingBar(
                operation = state.pending.operation,
                count = state.pending.sources.size,
                target = state.currentPath,
                onConfirm = onConfirmPendingHere,
                onCancel = onCancelPending,
            )

            state.selected.isNotEmpty() -> SelectionBar(
                selected = selectedEntries,
                readOnly = state.readOnly,
                canDeleteHere = state.canDeleteHere,
                onShowInfo = { singleSelected?.let(onShowInfo) },
                onRename = { singleSelected?.let { renameTarget = it } },
                onMove = onBeginMove,
                onCopy = onBeginCopy,
                onDownload = onBatchDownload,
                onDownloadFolder = onDownloadFolder,
                onDelete = { deleteConfirm = true },
                onClear = onClearSelection,
            )
        }
    }

    state.infoText?.let { info ->
        WcDialog(
            onDismissRequest = onDismissInfo,
            title = { Text(Str.t("属性")) },
            text = { Text(info, fontSize = 13.sp) },
            confirmButton = {
                TextButton(onClick = onDismissInfo) { Text(Str.t("关闭")) }
            },
        )
    }

    if (showCreateDialog) {
        NameDialog(
            title = Str.t("新建文件夹"),
            initial = "",
            onDismiss = { showCreateDialog = false },
            onConfirm = { name ->
                showCreateDialog = false
                onCreateDirectory(name)
            },
        )
    }

    renameTarget?.let { target ->
        NameDialog(
            title = Str.t("重命名"),
            initial = target.name,
            onDismiss = { renameTarget = null },
            onConfirm = { name ->
                renameTarget = null
                onRename(target.relativePath, name)
            },
        )
    }

    if (deleteConfirm) {
        WcDialog(
            onDismissRequest = { deleteConfirm = false },
            title = { Text(Str.t("删除选中的 ${state.selected.size} 项？")) },
            text = { Text(Str.t("删除后无法恢复。")) },
            confirmButton = {
                TextButton(onClick = {
                    deleteConfirm = false
                    onDeleteSelected(true)
                }) { Text(Str.t("删除"), color = WcColors.Error) }
            },
            dismissButton = {
                TextButton(onClick = { deleteConfirm = false }) { Text(Str.t("取消")) }
            },
        )
    }
}

/** 整机根的固定 id 与 Android 侧 `AllFilesProvider.ROOT_ID` 一致。 */
private const val ALL_FILES_ROOT_ID = "phone-all"

private fun isAllFilesRoot(root: FileRootDto): Boolean = root.rootId == ALL_FILES_ROOT_ID

/**
 * 文件页顶栏：一行放下「位置 + 排序 + 搜索 + 上传 + 新建」。
 *
 * 旧版这里是三条——「电脑」根列表、路径+排序、搜索框、两枚大按钮，
 * 把一屏的三分之一吃掉了。收成一行后，同样高度能多显示三到四条文件。
 */
@Composable
private fun FileHeader(
    roots: List<FileRootDto>,
    currentRootId: String,
    onOpenRoot: (String) -> Unit,
    path: String,
    canGoUp: Boolean,
    onGoUp: () -> Unit,
    sortBy: FileSortBy,
    onSortBy: (FileSortBy) -> Unit,
    onSearch: (String) -> Unit,
    onUpload: (() -> Unit)?,
    onCreate: (() -> Unit)?,
) {
    var keyword by remember(path) { mutableStateOf("") }
    var searchOpen by remember { mutableStateOf(false) }
    var sortOpen by remember { mutableStateOf(false) }

    val rootName = roots.firstOrNull { it.rootId == currentRootId }?.displayName?.let { Str.t(it) }
        ?: Str.t("电脑")
    // 多个根时上面已经有切换胶囊，这里再写一遍根名就是重复
    val crumb = when {
        path.isEmpty() && roots.size > 1 -> ""
        path.isEmpty() -> rootName
        roots.size > 1 -> path.replace("/", " / ")
        else -> "$rootName / " + path.replace("/", " / ")
    }

    Column(modifier = Modifier.fillMaxWidth()) {
        // 多个根才给切换条：只有一个根时，它就是路径开头那一段，不必再占一行
        if (roots.size > 1) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = WcSpace.s4, vertical = 2.dp),
                horizontalArrangement = Arrangement.spacedBy(WcSpace.s2),
            ) {
                roots.forEach { root ->
                    val active = root.rootId == currentRootId
                    Text(
                        // 根名由服务端下发（"此电脑"、"传输目录"…），渲染时才翻；
                        // 用户自己的共享目录名查不到词条，原样显示
                        text = Str.t(root.displayName),
                        fontSize = WcType.labelSize,
                        fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                        color = if (active) WcColors.Accent else WcColors.Text2,
                        maxLines = 1,
                        modifier = Modifier
                            .clip(SquircleShape(WcRadius.r1))
                            .background(if (active) WcColors.AccentSoft else WcColors.Soft)
                            .clickable { onOpenRoot(root.rootId) }
                            .padding(horizontal = 10.dp, vertical = 6.dp),
                    )
                }
            }
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = WcSpace.s3, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (canGoUp) {
                WcIconAction(icon = ArrowLeftIcon, onClick = onGoUp, contentDescription = Str.t("上一级"))
                Spacer(Modifier.width(WcSpace.s1))
            }

            Text(
                text = crumb,
                fontSize = WcType.labelSize,
                color = WcColors.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )

            Box {
                WcIconAction(
                    icon = SortIcon,
                    onClick = { sortOpen = true },
                    contentDescription = Str.t("排序：${sortBy.label()}"),
                )
                DropdownMenu(expanded = sortOpen, onDismissRequest = { sortOpen = false }) {
                    FileSortBy.entries.forEach { option ->
                        DropdownMenuItem(
                            text = { Text(Str.t("按${option.label()}"), fontSize = 13.sp) },
                            onClick = {
                                sortOpen = false
                                onSortBy(option)
                            },
                        )
                    }
                }
            }

            Spacer(Modifier.width(WcSpace.s2))
            WcIconAction(
                icon = SearchIcon,
                onClick = {
                    searchOpen = !searchOpen
                    if (!searchOpen) {
                        keyword = ""
                        onSearch("")
                    }
                },
                contentDescription = Str.t("搜索"),
            )

            if (onUpload != null) {
                Spacer(Modifier.width(WcSpace.s2))
                WcIconAction(icon = UploadIcon, onClick = onUpload, contentDescription = Str.t("上传到此目录"))
            }
            if (onCreate != null) {
                Spacer(Modifier.width(WcSpace.s2))
                WcIconAction(icon = PlusIcon, onClick = onCreate, contentDescription = Str.t("新建文件夹"))
            }
        }

        if (searchOpen || keyword.isNotEmpty()) {
            OutlinedTextField(
                value = keyword,
                onValueChange = {
                    keyword = it
                    onSearch(it)
                },
                placeholder = { Text(Str.t("搜索当前目录"), fontSize = 13.sp) },
                singleLine = true,
                textStyle = androidx.compose.ui.text.TextStyle(fontSize = 13.sp),
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = WcSpace.s4, vertical = 2.dp),
            )
        }
    }
}

/** 排序方式的显示名。 */
private fun FileSortBy.label(): String = when (this) {
    FileSortBy.NAME -> Str.t("名称")
    FileSortBy.SIZE -> Str.t("大小")
    FileSortBy.MODIFIED -> Str.t("修改时间")
    FileSortBy.TYPE -> Str.t("类型")
}

/**
 * 多选时的底部操作栏。
 *
 * 与安卓文件管理器一致：常驻在底部，不随列表滚动。
 * 属性、重命名只对**单个**选中项有意义，多选时灰掉而不是隐藏——位置固定才好形成肌肉记忆。
 */
@Composable
private fun SelectionBar(
    selected: List<FileEntryDto>,
    readOnly: Boolean,
    canDeleteHere: Boolean,
    onShowInfo: () -> Unit,
    onRename: () -> Unit,
    onMove: () -> Unit,
    onCopy: () -> Unit,
    onDownload: () -> Unit,
    onDownloadFolder: (FileEntryDto) -> Unit,
    onDelete: () -> Unit,
    onClear: () -> Unit,
) {
    val count = selected.size
    val single = selected.singleOrNull()
    val canWrite = !readOnly

    // 能力位 0 = 老服务端没上报，放行；否则要求**每一项**都支持，别让批量操作做到一半失败
    fun allSupport(bit: Int): Boolean = selected.all { entry ->
        entry.capabilities == 0 || (entry.capabilities and bit) != 0
    }

    val renameEnabled = canWrite && single != null &&
        (single.capabilities == 0 ||
            (single.capabilities and com.wirelesscable.app.core.files.FileCapabilities.RENAME) != 0)

    // 移动/复制都支持多选：以前只判了单选，选两项就点不动了
    val moveEnabled = canWrite && allSupport(com.wirelesscable.app.core.files.FileCapabilities.MOVE)

    // 复制：文件要能读、目录要能列（服务端递归复制），目标侧由服务端按权限位判定
    val readable = com.wirelesscable.app.core.files.FileCapabilities.READ_FILE or
        com.wirelesscable.app.core.files.FileCapabilities.LIST_DIRECTORY
    val copyEnabled = canWrite && selected.all { entry ->
        entry.capabilities == 0 || (entry.capabilities and readable) != 0
    }
    // 选中单个文件夹也能下载——那是整文件夹下载；混选（文件+夹）仍按文件逐个下
    val singleFolder = selected.singleOrNull()?.takeIf { it.isDirectory }
    val downloadEnabled = selected.isNotEmpty() &&
        (selected.any { !it.isDirectory } || singleFolder != null)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(WcColors.Tile)
            .padding(horizontal = 12.dp, vertical = 8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = Str.t("已选 $count 项"),
                color = WcColors.Text,
                fontSize = 13.sp,
                modifier = Modifier.weight(1f),
            )
            WcLinkButton(text = Str.t("取消"), onClick = onClear)
        }

        Spacer(Modifier.height(2.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            BarAction(Str.t("属性"), enabled = single != null, onClick = onShowInfo)
            BarAction(Str.t("重命名"), enabled = renameEnabled, onClick = onRename)
            BarAction(Str.t("移动"), enabled = moveEnabled, onClick = onMove)
            // 复制只需要能读源 + 能写目标，与条目的 Move 能力位无关
            BarAction(Str.t("复制"), enabled = copyEnabled, onClick = onCopy)
            BarAction(
                label = if (singleFolder != null) Str.t("下载文件夹") else Str.t("下载"),
                enabled = downloadEnabled,
                onClick = { if (singleFolder != null) onDownloadFolder(singleFolder) else onDownload() },
            )
            // 删除单独看根上的 Delete 能力位：只判可写会让按钮亮着却被服务端拒绝
            BarAction(Str.t("删除"), enabled = canDeleteHere, onClick = onDelete, danger = true)
        }
    }
}

/** 目标目录选择时的底部栏：这里就是用户确认落到这儿的地方。 */
@Composable
private fun PendingBar(
    operation: TargetOperation,
    count: Int,
    target: String,
    onConfirm: () -> Unit,
    onCancel: () -> Unit,
) {
    val label = if (operation == TargetOperation.MOVE) Str.t("移动") else Str.t("复制")

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(WcColors.Tile)
            .padding(horizontal = 12.dp, vertical = 8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = Str.t("把 $count 项${label}到 ${target.ifEmpty { "/" }}"),
                color = WcColors.Text,
                fontSize = 12.5.sp,
                modifier = Modifier.weight(1f),
            )
            WcLinkButton(text = Str.t("取消"), onClick = onCancel)
        }

        Spacer(Modifier.height(6.dp))

        WcPrimaryButton(
            text = if (operation == TargetOperation.MOVE) Str.t("移动到此处") else Str.t("复制到此处"),
            onClick = onConfirm,
            enabled = true,
        )
    }
}

/** 底部栏里的一个动作。禁用时灰掉，位置保持不变。 */
@Composable
private fun BarAction(
    label: String,
    enabled: Boolean,
    onClick: () -> Unit,
    danger: Boolean = false,
) {
    Text(
        text = label,
        fontSize = 13.sp,
        fontWeight = FontWeight.Medium,
        color = when {
            !enabled -> WcColors.Muted.copy(alpha = 0.45f)
            danger -> WcColors.Error
            else -> WcColors.Accent
        },
        modifier = Modifier
            .clip(RoundedCornerShape(6.dp))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 6.dp, vertical = 6.dp),
    )
}

/**
 * 一行条目。
 *
 * 密度优先：单行放完名称，右侧挂大小与时间；选中时才让出左侧位置给复选框。
 * 操作全在底部栏，行内不放任何按钮。
 */
/**
 * 一行条目。
 *
 * 密度优先：单行放完名称，右侧挂大小与时间。
 * **左侧复选框常驻**：不用长按进入多选，勾选就是常驻状态；
 * 点文件=勾选/取消，点目录=进入，点复选框=勾选该目录（否则目录没法被选中去移动/复制/删除）。
 */
@Composable
private fun EntryRow(
    entry: FileEntryDto,
    selected: Boolean,
    onClick: () -> Unit,
    onToggleSelect: () -> Unit,
) {
    val meta = buildString {
        if (!entry.isDirectory) append(formatSize(entry.sizeBytes))
        if (entry.modifiedUnixMs > 0) {
            if (isNotEmpty()) append(" · ")
            append(formatDay(entry.modifiedUnixMs))
        }
    }

    val shape = SquircleShape(WcRadius.r1)

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .height(WcSize.RowHeightCompact)
            .background(if (selected) WcColors.AccentSoft else Color0)
            .clickable(onClick = onClick)
            .padding(horizontal = WcSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // 固定 26dp 槽位：勾选框一直在这里，行高与缩进不随选择状态变化
        Box(
            modifier = Modifier
                .width(26.dp)
                .clickable(onClick = onToggleSelect),
            contentAlignment = Alignment.CenterStart,
        ) {
            SelectionMark(checked = selected)
        }

        Box(
            modifier = Modifier
                .size(26.dp)
                .clip(shape)
                .background(if (entry.isDirectory) WcColors.AccentSoft else WcColors.Well),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = when {
                    entry.isDirectory -> FolderIcon
                    entry.isImage() -> ImageIcon
                    else -> FileIcon
                },
                contentDescription = null,
                tint = if (entry.isDirectory) WcColors.Accent else WcColors.Text2,
                modifier = Modifier.size(15.dp),
            )
        }

        Spacer(Modifier.width(WcSpace.s2 + WcSpace.s1))

        Text(
            text = entry.name,
            fontSize = 13.5.sp,
            color = WcColors.Text,
            fontWeight = if (entry.isDirectory) FontWeight.SemiBold else FontWeight.Normal,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )

        if (meta.isNotEmpty()) {
            Spacer(Modifier.width(WcSpace.s2))
            Text(text = meta, fontSize = WcType.captionSize, color = WcColors.Muted)
        }
    }
}

/** 按扩展名判断是不是图片：只影响图标，不影响任何操作。 */
private fun FileEntryDto.isImage(): Boolean {
    val lower = name.substringAfterLast('.', "").lowercase()
    return lower in setOf("png", "jpg", "jpeg", "webp", "gif", "bmp", "svg", "heic")
}

/**
 * 自绘勾选框（18dp）。
 *
 * 不用 Material 的 Checkbox：它带 48dp 的最小点击区，会把这行撑高、把文字挤偏。
 * 这里尺寸完全受控，选择态与浏览态的行高才一模一样。
 */
@Composable
private fun SelectionMark(checked: Boolean) {
    val shape = RoundedCornerShape(4.dp)

    Box(
        modifier = Modifier
            .size(18.dp)
            .clip(shape)
            .background(if (checked) WcColors.Accent else Color0)
            .border(1.5.dp, if (checked) WcColors.Accent else WcColors.Border, shape),
        contentAlignment = Alignment.Center,
    ) {
        if (checked) {
            Text(text = "✓", fontSize = 11.sp, color = Color.White)
        }
    }
}

@Composable
private fun NameDialog(
    title: String,
    initial: String,
    onDismiss: () -> Unit,
    onConfirm: (String) -> Unit,
) {
    var value by remember { mutableStateOf(initial) }

    WcDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            OutlinedTextField(
                value = value,
                onValueChange = { value = it },
                singleLine = true,
                textStyle = androidx.compose.ui.text.TextStyle(fontSize = 14.sp),
            )
        },
        confirmButton = {
            TextButton(
                onClick = { if (value.isNotBlank()) onConfirm(value.trim()) },
                enabled = value.isNotBlank(),
            ) { Text(Str.t("确定")) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(Str.t("取消")) }
        },
    )
}

private val Color0 = androidx.compose.ui.graphics.Color(0x00000000)

/** 文件大小的人类可读格式。 */
internal fun formatSize(bytes: Long): String {
    if (bytes < 1024) return "$bytes B"
    val units = listOf("KB", "MB", "GB", "TB")
    var value = bytes.toDouble() / 1024
    var index = 0
    while (value >= 1024 && index < units.lastIndex) {
        value /= 1024
        index++
    }
    return String.format("%.1f %s", value, units[index])
}

/** 列表里的日期：只到分钟，年份省略——文件列表不需要精确到年。 */
private fun formatDay(millis: Long): String {
    val format = java.text.SimpleDateFormat("MM-dd HH:mm", java.util.Locale.getDefault())
    return format.format(java.util.Date(millis))
}
