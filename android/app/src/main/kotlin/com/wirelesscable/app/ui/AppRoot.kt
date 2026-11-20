package com.wirelesscable.app.ui

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.togetherWith
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.List
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.platform.LocalContext
import com.wirelesscable.app.core.direct.DirectLog
import com.wirelesscable.app.core.direct.DirectPayload
import com.wirelesscable.app.core.direct.HotspotPermissions
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.awaitCancellation
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.wirelesscable.app.core.direct.DirectQr
import com.wirelesscable.app.core.protocol.FileEntryDto
import com.wirelesscable.app.core.sync.BatteryOptimization
import com.wirelesscable.app.core.sync.BackgroundSync
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.ui.components.AppPickerDialog
import com.wirelesscable.app.ui.components.ShareTargetDialog
import com.wirelesscable.app.ui.components.WcDialog
import com.wirelesscable.app.ui.screens.DirectScreen
import com.wirelesscable.app.ui.screens.FileBrowserScreen
import com.wirelesscable.app.ui.screens.FileSource
import com.wirelesscable.app.ui.screens.FilesHubScreen
import com.wirelesscable.app.ui.screens.HomeScreen
import com.wirelesscable.app.ui.screens.InboxScreen
import com.wirelesscable.app.ui.screens.ScanScreen
import com.wirelesscable.app.ui.screens.SettingsScreen
import com.wirelesscable.app.ui.screens.TransfersScreen
import com.wirelesscable.app.ui.components.PowerIcon
import com.wirelesscable.app.ui.components.WcIconAction
import com.wirelesscable.app.ui.components.WcTopBar
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcMotion
import com.wirelesscable.app.vm.ConnectionViewModel
import com.wirelesscable.app.vm.FileBrowserViewModel
import com.wirelesscable.app.vm.PairingViewModel
import com.wirelesscable.app.vm.DirectViewModel
import com.wirelesscable.app.vm.InboxViewModel
import com.wirelesscable.app.vm.TransferViewModel
import com.wirelesscable.app.core.i18n.Str

/**
 * 底部导航。
 *
 * 阶段 7 收敛成四页：首页（连接与发现）、文件（电脑/手机）、传输中心、设置。
 * 原来的「设备」「发送」两个标签并入首页与文件页——六个标签在手机上太挤，
 * 而且设备与首页讲的是同一件事。
 */
// 只存中文原文：枚举常量在类初始化时求值，写 Str.t() 会把语言冻在进程启动那一刻，
// 之后在设置里切语言，底部导航与顶栏标题不会跟着变。
private enum class Tab(val label: String, val title: String, val icon: ImageVector) {
    Home("首页", "无线数据线", Icons.Filled.Home),
    Files("文件", "文件", Icons.Filled.List),
    Transfers("传输", "传输", SyncIcon),
    Settings("设置", "设置", Icons.Filled.Settings),
}

/**
 * 「传输」图标：上下双向箭头。
 *
 * material-icons-core 里没有合适的双向箭头（SwapVert 在 extended 包里），
 * 为了一个图标引入整个 extended 不划算，这里自己画两条带箭头的线。
 * Icon() 会按 LocalContentColor 着色，描边颜色只是占位。
 */
private val SyncIcon: ImageVector = ImageVector.Builder(
    name = "Sync",
    defaultWidth = 24.dp,
    defaultHeight = 24.dp,
    viewportWidth = 24f,
    viewportHeight = 24f,
).apply {
    path(
        stroke = SolidColor(Color.Black),
        strokeLineWidth = 2f,
        strokeLineCap = StrokeCap.Round,
        strokeLineJoin = StrokeJoin.Round,
    ) {
        // 上箭头：从右往左送
        moveTo(7f, 9f)
        lineTo(17f, 9f)
        moveTo(14f, 6f)
        lineTo(17f, 9f)
        lineTo(14f, 12f)
        // 下箭头：从左往右收
        moveTo(17f, 15f)
        lineTo(7f, 15f)
        moveTo(10f, 12f)
        lineTo(7f, 15f)
        lineTo(10f, 18f)
    }
}.build()

@Composable
fun AppRoot(
    viewModel: ConnectionViewModel,
    pairingViewModel: PairingViewModel,
    transferViewModel: TransferViewModel,
    fileBrowserViewModel: FileBrowserViewModel,
    inboxViewModel: InboxViewModel,
    directViewModel: DirectViewModel,
    /** 设备行「授权 → 选择目录」：先把授权绑到这台电脑，再弹系统选择器。 */
    onGrantFolderToDevice: (String) -> Unit,
    /** 没有整机访问权时，用系统选择器选文件发送。 */
    onPickSystemFiles: () -> Unit,
    /** 从文件浏览页下载一个文件：由界面弹 SAF 选择器落盘。 */
    onDownload: (FileEntryDto) -> Unit,
    /** 从文件浏览页上传文件到当前所在的电脑目录。 */
    onUploadHere: () -> Unit,
    /** 从文件浏览页批量下载选中的文件。 */
    onBatchDownload: () -> Unit,
    /** 下载整个文件夹（服务端展开成一批，落点由界面选）。 */
    onDownloadFolder: (FileEntryDto) -> Unit,
    /** 退出应用：停服务、杀进程（由 Activity 实现）。 */
    onExitApp: () -> Unit = {},
    /** 每收到一次系统分享 +1：界面据此跳到「传输」，让用户看见刚发出去的东西。 */
    shareTick: Int = 0,
    /** 系统分享递进来、还没选落点的那一批；非空就弹选择框。 */
    shareDraft: ShareDraft? = null,
    /** 选择框里正在连电脑（点完设备到连上之间要等一会儿）。 */
    shareBusy: Boolean = false,
    shareMessage: String? = null,
    onSharePickComputer: (String) -> Unit = {},
    onShareDismiss: () -> Unit = {},
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val pairingState by pairingViewModel.state.collectAsStateWithLifecycle()
    val transferState by transferViewModel.state.collectAsStateWithLifecycle()
    val fileBrowserState by fileBrowserViewModel.state.collectAsStateWithLifecycle()
    val inboxState by inboxViewModel.state.collectAsStateWithLifecycle()

    var tab by remember { mutableStateOf(Tab.Home) }
    var fileSource by remember { mutableStateOf(FileSource.Computer) }

    // 从系统分享进来：跳到「传输」。进度、失败原因都在那一页，别让用户找。
    LaunchedEffect(shareTick) {
        if (shareTick > 0) {
            tab = Tab.Transfers
        }
    }

    // 电池白名单：服务开着但没放行，息屏后循环会被系统冻住，电脑端表现为看不到文件。
    // 首页给一条提示，免得用户去猜为什么息屏就断。
    val context = LocalContext.current
    val backgroundFlag by BackgroundSync.enabled.collectAsStateWithLifecycle()
    var batteryAllowed by remember { mutableStateOf(BatteryOptimization.isIgnoring(context)) }
    val appLifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(appLifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                batteryAllowed = BatteryOptimization.isIgnoring(context)
            }
        }
        appLifecycleOwner.lifecycle.addObserver(observer)
        onDispose { appLifecycleOwner.lifecycle.removeObserver(observer) }
    }

    val backgroundLimited =
        (backgroundFlag ?: BackgroundSync.isEnabled(context)) && !batteryAllowed

    // 系统返回/侧滑返回：越具体的层级越先注册（后注册者优先）。
    // 1) 非首页 → 回首页
    // 2) 电脑文件子目录 → 上一级
    // 4) 首页 → 交给系统退出
    BackHandler(enabled = tab != Tab.Home) {
        tab = Tab.Home
    }

    val browsingComputer = tab == Tab.Files && fileSource == FileSource.Computer
    BackHandler(enabled = browsingComputer && fileBrowserState.currentPath.isNotEmpty()) {
        fileBrowserViewModel.goUp()
    }


    // 首页也要任务数据（最近传输、接收中的任务），所以除设置页外都保持轮询。
    // 空闲时是低频（5 秒），有任务在跑才提速，耗电可控。
    LaunchedEffect(tab, fileSource) {
        if (tab == Tab.Settings) {
            transferViewModel.stop()
        } else {
            transferViewModel.start()
        }

        // 传输目录每次进入都重扫：用户可能刚在系统设置里给了「所有文件访问权」，
        // 也可能刚收到文件
        if (tab == Tab.Files && fileSource == FileSource.Phone) {
            inboxViewModel.refresh()
        }
    }

    /*
     * 作业/索引/心跳归应用级的 SessionSyncEngine（0.6.5 起）。
     *
     * 界面可见时借一个持有者——这样不用开前台服务也能轮询；
     * 息屏后由「后台保持连接」的前台服务继续持有（K43），
     * 所以退出界面不会再把后台循环一起带走。
     */
    val lifecycleOwner = LocalLifecycleOwner.current
    LaunchedEffect(lifecycleOwner) {
        lifecycleOwner.repeatOnLifecycle(Lifecycle.State.STARTED) {
            SessionSyncEngine.acquire("ui")
            fileBrowserViewModel.start()
            try {
                awaitCancellation()
            } finally {
                fileBrowserViewModel.stop()
                SessionSyncEngine.release("ui")
            }
        }
    }

    // 扫码全屏覆盖层：既用于配对，也用于「当面直连」——扫到什么码就走哪条路
    var showScanner by remember { mutableStateOf(false) }
    var showDirect by remember { mutableStateOf(false) }

    // 首页「发送应用」：选完直接走普通上传通道（和发文件的路径完全一致，只是文件名换成应用名）
    var sendingAppsToPc by remember { mutableStateOf(false) }
    if (sendingAppsToPc) {
        AppPickerDialog(
            onDismiss = { sendingAppsToPc = false },
            onConfirm = { apps ->
                sendingAppsToPc = false
                if (apps.isNotEmpty()) transferViewModel.uploadApps(apps)
            },
        )
    }
    val directState by directViewModel.state.collectAsStateWithLifecycle()

    // 连对方热点需要 NEARBY_WIFI_DEVICES（13+）/ 定位（各版本）。
    //
    // 扫码方以前**从来不申请**这两个权限（只有出码方打开热点开关时才申请过），
    // 于是 13+ 上 `requestNetwork` 直接抛 SecurityException、被 runCatching 吞掉，
    // 表现就是热点永远连不上、悄悄退回普通 Wi-Fi（1.5.56 由三星日志定位）。
    //
    // 1.5.57 起判定统一在 HotspotPermissions：
    // - 申请时两个一起要（有的 ROM 只认定位，有的只认附近设备）；
    // - **回调里不再看返回的 map，而是重新查真实授权状态** —— map 里 `any`/`all`
    //   的判断和系统 `anyOf` 的规则对不齐，只授一个时会走到以为有权限的死路；
    // - 申请完仍然没有权限，说明系统不会再弹框（拒绝过两次），给「去设置」的出口。
    var pendingDirect by remember { mutableStateOf<DirectPayload?>(null) }

    val directPermissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { result ->
        val payload = pendingDirect
        pendingDirect = null
        if (payload == null) {
            return@rememberLauncherForActivityResult
        }

        val granted = HotspotPermissions.canUseHotspot(context)
        DirectLog.i(
            "热点权限申请结果 result=$result 实际 ${HotspotPermissions.describe(context)} " +
                "canAskAgain=${HotspotPermissions.canAskAgain(context)}",
        )

        if (granted) {
            directViewModel.join(payload)
        } else {
            directViewModel.notifyHotspotPermissionMissing(
                HotspotPermissions.missingText(context).orEmpty(),
            )
        }
    }

    fun startDirect(payload: DirectPayload) {
        val needsHotspot = payload.hotspot != null
        DirectLog.i(
            "扫码直连：${HotspotPermissions.describe(context)} 码里带热点=$needsHotspot " +
                "hotspot=${payload.hotspot?.ssid ?: "-"}",
        )

        if (needsHotspot && !HotspotPermissions.canUseHotspot(context)) {
            pendingDirect = payload
            // 先把码存进 ViewModel：用户在系统设置里开完权限回来点「重试」时，
            // 不需要重新扫（以前那条路径必然回「没有可重试的会话」）。
            directViewModel.rememberPending(payload)
            directPermissionLauncher.launch(HotspotPermissions.requestArray())
        } else {
            directViewModel.join(payload)
        }
    }

    if (showScanner) {
        // 这是覆盖层，不是页面：返回键要关掉它，而不是把整个应用退掉
        BackHandler(enabled = true) { showScanner = false }
        ScanScreen(
            onResult = { text ->
                showScanner = false
                val direct = DirectQr.parse(text)
                if (direct != null) {
                    showDirect = true
                    startDirect(direct)
                } else {
                    pairingViewModel.beginPairingFromQr(text)
                }
            },
            onClose = { showScanner = false },
            manualHost = pairingState.manualHost,
            manualPort = pairingState.manualPort,
            manualFingerprint = pairingState.manualFingerprint,
            manualBusy = pairingState.manualBusy,
            manualMessage = pairingState.message,
            awaitingApproval = pairingState.awaitingApproval,
            onManualHostChange = pairingViewModel::onManualHostChanged,
            onManualPortChange = pairingViewModel::onManualPortChanged,
            onManualFingerprintChange = pairingViewModel::onManualFingerprintChanged,
            onManualPair = pairingViewModel::beginManualPairing,
            onOpenDirect = {
                showScanner = false
                showDirect = true
            },
        )
        return
    }

    if (showDirect) {
        DirectScreen(
            state = directState,
            onHost = { directViewModel.startHosting(emptyList()) },
            onToggleHotspot = { directViewModel.setUseHotspot(it) },
            onPermissionDenied = { directViewModel.notifyHotspotPermissionMissing(it) },
            onAddShared = { directViewModel.addShared(it) },
            onStopHosting = { directViewModel.stopHosting() },
            onScan = { showScanner = true },
            onLeave = { directViewModel.leave() },
            onDownload = { directViewModel.download(it) },
            onSend = { directViewModel.send(it) },
            onAddApps = { directViewModel.addApps(it) },
            onSendApps = { directViewModel.sendApps(it) },
            onInstallPeerApp = { directViewModel.installPeerApp(it) },
            onInstallReceivedApp = { directViewModel.installReceivedApp(it) },
            onOpenReceived = { directViewModel.openReceived(it) },
            onRetryHotspot = { directViewModel.retryHotspot() },
            onBack = {
                directViewModel.stopHosting()
                directViewModel.leave()
                showDirect = false
            },
        )
        return
    }

    // 退出确认放在根上：一屏一个出口，不在各页面里各写一遍
    var confirmingExit by remember { mutableStateOf(false) }

    // 用 Scaffold 统一处理系统栏内边距：手动叠加 padding 会把底部标签推出可视区
    Scaffold(
        containerColor = WcColors.Surface,
        contentWindowInsets = WindowInsets.safeDrawing,
        topBar = {
            // 退出入口常驻在右上角（所有页签都在），不再只在首页给
            WcTopBar(title = Str.t(tab.title)) {
                WcIconAction(
                    icon = PowerIcon,
                    onClick = { confirmingExit = true },
                    contentDescription = Str.t("退出应用"),
                    danger = true,
                )
            }
        },
        bottomBar = { BottomTabs(selected = tab, onSelect = { tab = it }) },
    ) { innerPadding ->
        // 切页签做淡入 + 轻微上移，不是硬切；时长与令牌同源（240ms）
        AnimatedContent(
            targetState = tab,
            transitionSpec = {
                (fadeIn(tween(WcMotion.DurSlow)) +
                    slideInVertically(tween(WcMotion.DurSlow)) { it / 40 })
                    .togetherWith(fadeOut(tween(WcMotion.DurFast)))
            },
            label = "tab",
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding),
        ) { current ->
        Column(modifier = Modifier.fillMaxSize()) {
            when (current) {
                Tab.Home -> HomeScreen(
                    pairing = pairingState,
                    activeRows = transferState.activeRows,
                    onOpenScanner = { showScanner = true },
                    onSendFiles = onPickSystemFiles,
                    onSendApps = { sendingAppsToPc = true },
                    onOpenDirect = { showDirect = true },
                    onOpenTransfers = { tab = Tab.Transfers },
                    onToggleScan = {
                        if (pairingState.scanning) pairingViewModel.stopScan() else pairingViewModel.startScan()
                    },
                    onRetrySession = pairingViewModel::retrySession,
                    onConnect = pairingViewModel::connectTo,
                    onCancelPairing = pairingViewModel::cancelPairing,
                    onRePair = pairingViewModel::rePairLast,
                    backgroundLimited = backgroundLimited,
                    onAllowBackground = { BatteryOptimization.request(context) },
                    modifier = Modifier.weight(1f),
                )

                Tab.Files -> FilesHubScreen(
                    source = fileSource,
                    onSourceChange = { fileSource = it },
                    computer = {
                        FileBrowserScreen(
                            state = fileBrowserState,
                            onOpenRoot = fileBrowserViewModel::openRoot,
                            onEnter = fileBrowserViewModel::enter,
                            onGoUp = fileBrowserViewModel::goUp,
                            onSearch = fileBrowserViewModel::setSearch,
                            onToggleSelect = fileBrowserViewModel::toggleSelection,
                            onClearSelection = fileBrowserViewModel::clearSelection,
                            onDeleteSelected = fileBrowserViewModel::deleteSelected,
                            onCreateDirectory = fileBrowserViewModel::createDirectory,
                            onRename = fileBrowserViewModel::rename,
                            onUploadHere = onUploadHere,
                            onSortBy = fileBrowserViewModel::setSortBy,
                            onShowInfo = fileBrowserViewModel::showInfo,
                            onDismissInfo = fileBrowserViewModel::dismissInfo,
                            onBatchDownload = onBatchDownload,
                            onDownloadFolder = onDownloadFolder,
                            onBeginMove = fileBrowserViewModel::beginMove,
                            onBeginCopy = fileBrowserViewModel::beginCopy,
                            onCancelPending = fileBrowserViewModel::cancelPending,
                            onConfirmPendingHere = fileBrowserViewModel::confirmPendingHere,
                            modifier = Modifier.fillMaxSize(),
                        )
                    },
                    phone = {
                        InboxScreen(
                            state = inboxState,
                            onRefresh = inboxViewModel::refresh,
                            onOpen = inboxViewModel::open,
                            onOpenAllFiles = fileBrowserViewModel::openAllFilesSettings,
                            onDismissMessage = inboxViewModel::clearMessage,
                            onStartSelection = inboxViewModel::startSelection,
                            onToggleSelect = inboxViewModel::toggleSelect,
                            onClearSelection = inboxViewModel::clearSelection,
                            onSelectAll = inboxViewModel::selectAll,
                            onDeleteSelected = inboxViewModel::deleteSelected,
                            modifier = Modifier.fillMaxSize(),
                        )
                    },
                )

                Tab.Transfers -> TransfersScreen(
                    state = transferState,
                    onSaveDownload = transferViewModel::saveDownload,
                    onCancel = transferViewModel::cancelTasks,
                    onPause = transferViewModel::pauseTasks,
                    onResume = transferViewModel::resumeTasks,
                    onSelectTab = transferViewModel::selectTab,
                    onSelectFilter = transferViewModel::selectFilter,
                    onStartSelection = transferViewModel::startSelection,
                    onToggleSelect = transferViewModel::toggleSelect,
                    onSelectAll = transferViewModel::selectAll,
                    onClearSelection = transferViewModel::clearSelection,
                    onDeleteRecords = { files -> transferViewModel.deleteSelected(files) },
                    onResolvePeerCancelled = transferViewModel::resolvePeerCancelled,
                    onDeleteCancelled = transferViewModel::cancelAndDelete,
                    onOpenSaved = transferViewModel::openSaved,
                    onRetryReceive = transferViewModel::retryReceive,
                    modifier = Modifier.weight(1f),
                )

                Tab.Settings -> SettingsScreen(
                    state = state,
                    pairing = pairingState,
                    localRoots = fileBrowserState.localRoots,
                    syncedRootCount = fileBrowserState.roots.size,
                    allFilesGranted = fileBrowserState.allFilesGranted,
                    onSetMachineAccess = { deviceId, allowed ->
                        fileBrowserViewModel.setMachineAllowed(deviceId, allowed)
                    },
                    onGrantFolderToDevice = onGrantFolderToDevice,
                    onRenameDevice = pairingViewModel::renameDevice,
                    onForgetDevice = pairingViewModel::forgetDevice,
                    grantedFolders = fileBrowserViewModel::grantedFoldersFor,
                    onRevokeRoot = fileBrowserViewModel::revokeRoot,
                    onOpenAllFilesSettings = fileBrowserViewModel::openAllFilesSettings,
                    onHostChanged = viewModel::onHostChanged,
                    onPortChanged = viewModel::onPortChanged,
                    onFingerprintChanged = viewModel::onFingerprintChanged,
                    onConnect = viewModel::connect,
                    onDisconnect = viewModel::disconnect,
                    modifier = Modifier.weight(1f),
                )
            }
        }
        }
    }

    if (confirmingExit) {
        WcDialog(
            onDismissRequest = { confirmingExit = false },
            title = { Text(Str.t("退出应用")) },
            text = {
                Text(
                    text = Str.t("会结束后台服务与所有连接。下次打开需要重新连电脑，不需要重新配对。"),
                    fontSize = 13.sp,
                    color = WcColors.Text,
                )
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        confirmingExit = false
                        onExitApp()
                    },
                ) {
                    Text(Str.t("退出"), color = WcColors.Error)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmingExit = false }) { Text(Str.t("取消")) }
            },
        )
    }

    if (shareDraft != null) {
        val draft = shareDraft
        ShareTargetDialog(
            label = draft.label,
            devices = pairingState.pairedDevices.filter { it.isPaired },
            // 只有真连着才算已连接：配对过但断着的那台，点下去要先找它
            connectedDeviceId = if (pairingState.isConnected) pairingState.sessionDeviceId else null,
            busy = shareBusy,
            message = shareMessage,
            onPickComputer = onSharePickComputer,
            onPickDirect = {
                onShareDismiss()
                if (directState.hosting) {
                    directViewModel.addShared(draft.uris)
                } else {
                    // 直接把分享的文件带进出码页：对方扫完就能看到
                    directViewModel.startHosting(draft.uris)
                }
                showDirect = true
            },
            onDismiss = onShareDismiss,
        )
    }

}

@Composable
private fun BottomTabs(selected: Tab, onSelect: (Tab) -> Unit) {
    Column(
        modifier = Modifier
            .background(WcColors.Card)
            // 底部导航要让开系统手势条/导航栏，否则标签会被压在下面
            .navigationBarsPadding(),
    ) {
        HorizontalDivider(color = WcColors.Border, thickness = 1.dp)
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = 8.dp),
            horizontalArrangement = Arrangement.SpaceEvenly,
        ) {
            Tab.entries.forEach { item ->
                val active = item == selected
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    modifier = Modifier
                        .clip(RoundedCornerShape(10.dp))
                        .clickable { onSelect(item) }
                        .padding(horizontal = 18.dp, vertical = 5.dp),
                ) {
                    Icon(
                        imageVector = item.icon,
                        // 图标是装饰，文字已经说明了功能；重复朗读没有意义
                        contentDescription = null,
                        tint = if (active) WcColors.Accent else WcColors.Muted,
                        modifier = Modifier.size(21.dp),
                    )
                    Spacer(Modifier.height(3.dp))
                    Text(
                        text = Str.t(item.label),
                        fontSize = 11.sp,
                        fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                        color = if (active) WcColors.Accent else WcColors.Muted,
                    )
                }
            }
        }
    }
}
