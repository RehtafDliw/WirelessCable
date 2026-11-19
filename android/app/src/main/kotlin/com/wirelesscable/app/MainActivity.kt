package com.wirelesscable.app

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.lifecycleScope
import com.wirelesscable.app.core.protocol.FolderDownloadResult
import com.wirelesscable.app.core.service.SessionSyncService
import com.wirelesscable.app.core.sync.BackgroundSync
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.ui.AppRoot
import com.wirelesscable.app.ui.ShareDraft
import com.wirelesscable.app.ui.theme.WirelessCableTheme
import com.wirelesscable.app.core.i18n.LangState
import com.wirelesscable.app.ui.theme.WcThemeState
import com.wirelesscable.app.core.files.RootGrantManager
import com.wirelesscable.app.core.transfer.TransferItem
import com.wirelesscable.app.vm.ConnectionViewModel
import com.wirelesscable.app.vm.PairingViewModel
import com.wirelesscable.app.vm.FileBrowserViewModel
import com.wirelesscable.app.vm.DirectViewModel
import com.wirelesscable.app.vm.InboxViewModel
import com.wirelesscable.app.vm.TransferViewModel
import com.wirelesscable.app.vm.BatchDownloadItem
import com.wirelesscable.app.vm.UploadItem
import com.wirelesscable.app.vm.UploadTarget
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

class MainActivity : ComponentActivity() {

    private companion object {
        /** 点完设备到真正开传之间最多等这么久。 */
        const val SHARE_CONNECT_TIMEOUT_MS = 20_000L
    }


    private val viewModel: ConnectionViewModel by viewModels {
        val app = application as WirelessCableApp
        ConnectionViewModel.factory(app.repository, app.vault)
    }

    private val pairingViewModel: PairingViewModel by viewModels {
        val app = application as WirelessCableApp
        PairingViewModel.factory(app.repository, app.vault, app.identityKeyStore, app.discovery, app)
    }

    private val transferViewModel: TransferViewModel by viewModels {
        val app = application as WirelessCableApp
        TransferViewModel.factory(app.repository, app.vault, app)
    }

    private val fileBrowserViewModel: FileBrowserViewModel by viewModels {
        val app = application as WirelessCableApp
        FileBrowserViewModel.factory(app, app.repository, app.vault)
    }

    private val directViewModel: DirectViewModel by viewModels()

    private val inboxViewModel: InboxViewModel by viewModels {
        InboxViewModel.factory(applicationContext)
    }

    /** 目录授权选择器。用户选中的 tree URI 交给 ViewModel 处理。 */
    private val directoryPicker = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        val uri = result.data?.data
        if (result.resultCode != android.app.Activity.RESULT_OK || uri == null) {
            // Android 11 起系统会直接拒绝受限制的目录（下载、存储根），
            // 以前这里静默 return，用户只看到"点了没反应"
            fileBrowserViewModel.onDirectoryPickCancelled()
            return@registerForActivityResult
        }

        fileBrowserViewModel.onDirectoryPicked(uri)
    }

    /**
     * 每发出去一次分享就 +1。
     *
     * Activity 已经在跑时（singleTop）不会重建，Compose 的初始页签也不会重算，
     * 只能靠这个信号把底部导航切到「传输」，让用户看见刚发出去的东西。
     */
    private var shareTick by mutableIntStateOf(0)

    /** 收到分享、还没选落点的那一批；非空就把选择框顶出来。 */
    private var shareDraft by mutableStateOf<ShareDraft?>(null)

    /** 选择框里正等着连电脑（点完设备到连上之间要几秒）。 */
    private var shareBusy by mutableStateOf(false)
    private var shareMessage by mutableStateOf<String?>(null)

    /** 正在进行的"连电脑并发送"。取消要能把它掐掉，否则用户被卡在弹框里。 */
    private var shareJob: Job? = null

    /** 等待保存的下载任务：文件名用于 SAF 的默认名，选好位置后交给传输引擎。 */
    private var pendingDownload: TransferItem? = null

    /** 上传到电脑目录的文件选择器（允许多选）。 */
    private val uploadPicker = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        if (result.resultCode != android.app.Activity.RESULT_OK) return@registerForActivityResult

        val intent = result.data ?: return@registerForActivityResult
        val picked = mutableListOf<android.net.Uri>()

        // 多选走 clipData，单选走 data；两者都要处理
        intent.clipData?.let { clip ->
            for (index in 0 until clip.itemCount) {
                clip.getItemAt(index).uri?.let(picked::add)
            }
        }

        if (picked.isEmpty()) {
            intent.data?.let(picked::add)
        }

        if (picked.isEmpty()) return@registerForActivityResult

        val state = fileBrowserViewModel.state.value
        transferViewModel.upload(
            picked.distinct(),
            UploadTarget(
                directoryId = state.currentRootId,
                relativeDirectory = state.currentPath,
            ),
        )
    }

    /**
     * 系统文件选择器：发送到电脑的默认接收目录。
     *
     * 与 [uploadPicker] 的区别是不带目标目录——手机文件页已经知道自己所在的位置，
     * 这里只负责"从系统任意位置挑几个文件发过去"。
     */
    private val systemFilePicker = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        if (result.resultCode != android.app.Activity.RESULT_OK) return@registerForActivityResult

        val intent = result.data ?: return@registerForActivityResult
        val picked = mutableListOf<android.net.Uri>()

        intent.clipData?.let { clip ->
            for (index in 0 until clip.itemCount) {
                clip.getItemAt(index).uri?.let(picked::add)
            }
        }

        if (picked.isEmpty()) {
            intent.data?.let(picked::add)
        }

        if (picked.isNotEmpty()) {
            transferViewModel.upload(picked.distinct())
        }
    }

    /** 正在等用户选落点的"整文件夹"批次。 */
    private var pendingFolderBatch: FolderDownloadResult? = null

    /** 整文件夹下载的落点选择器（选目录树，保留子目录结构）。 */
    private val folderBatchPicker = registerForActivityResult(
        ActivityResultContracts.OpenDocumentTree(),
    ) { uri ->
        val batch = pendingFolderBatch
        pendingFolderBatch = null
        if (uri == null || batch == null) return@registerForActivityResult

        transferViewModel.saveFolderBatch(batch.batchName, batch.tasks, uri)
    }

    /** 批量下载的目标目录选择器。 */
    private val batchDirectoryPicker = registerForActivityResult(
        ActivityResultContracts.OpenDocumentTree(),
    ) { uri ->
        if (uri == null) return@registerForActivityResult

        val state = fileBrowserViewModel.state.value
        val items = fileBrowserViewModel.selectedEntries()
            .filter { !it.isDirectory }
            .map { BatchDownloadItem(state.currentRootId, it.relativePath, it.name) }

        if (items.isEmpty()) return@registerForActivityResult

        transferViewModel.downloadBatch(items, uri)
    }

    /** 下载落盘选择器。 */
    private val downloadSaver = registerForActivityResult(
        ActivityResultContracts.CreateDocument("*/*"),
    ) { uri ->
        val item = pendingDownload
        pendingDownload = null
        if (uri == null || item == null) {
            return@registerForActivityResult
        }

        transferViewModel.saveDownload(item, uri)
    }

    /**
     * 「后台保持连接」的对账点。
     *
     * 前台服务只能在界面可见时启动（Android 12+ 禁止后台拉起），所以每次回到前台对一次：
     * 开关开着且有会话就确保服务在跑，否则确保它停。
     * 会话是否存在要去 Room 里读，不能阻塞主线程，所以放在 lifecycleScope 里。
     */
    /**
     * 退出应用：结束后台服务与会话，再结束任务栈并杀进程。
     *
     * 与"按返回键"不同——那只是退到后台，前台服务还活着、电脑仍能操作手机。
     * 效果等价于系统应用详情里的「强行停止」，但走正常流程：先停服务，再杀自己。
     */
    private fun exitApp() {
        runCatching { SessionSyncService.stop(this) }
        SessionSyncEngine.shutdown()
        finishAffinity()
        android.os.Process.killProcess(android.os.Process.myPid())
    }

    override fun onStart() {
        super.onStart()

        val app = application as WirelessCableApp
        lifecycleScope.launch {
            BackgroundSync.reconcile(this@MainActivity, app.repository.loadSession() != null)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 主题偏好在建界面之前读出来，避免先按浅色铺一帧再切深色
        WcThemeState.load(this)
        LangState.load(this)

        setContent {
            WirelessCableTheme {
                AppRoot(
                    viewModel = viewModel,
                    pairingViewModel = pairingViewModel,
                    transferViewModel = transferViewModel,
                    fileBrowserViewModel = fileBrowserViewModel,
                    inboxViewModel = inboxViewModel,
                    directViewModel = directViewModel,
                    onGrantFolderToDevice = { deviceId ->
                        fileBrowserViewModel.beginFolderGrant(deviceId)
                        directoryPicker.launch(RootGrantManager.pickIntent())
                    },
                    onPickSystemFiles = {
                        systemFilePicker.launch(
                            Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
                                addCategory(Intent.CATEGORY_OPENABLE)
                                type = "*/*"
                                putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                            },
                        )
                    },
                    onBatchDownload = {
                        batchDirectoryPicker.launch(null)
                    },
                    onDownloadFolder = { entry ->
                        // 两步：先让服务端把文件夹展开成一批任务，再让用户选落点
                        fileBrowserViewModel.downloadFolder(entry) { batch ->
                            pendingFolderBatch = batch
                            folderBatchPicker.launch(null)
                        }
                    },
                    onUploadHere = {
                        uploadPicker.launch(
                            Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
                                addCategory(Intent.CATEGORY_OPENABLE)
                                type = "*/*"
                                putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                            },
                        )
                    },
                    shareTick = shareTick,
                    shareDraft = shareDraft,
                    shareBusy = shareBusy,
                    shareMessage = shareMessage,
                    onSharePickComputer = ::sendShareToComputer,
                    onShareDismiss = ::dismissShare,
                    onExitApp = { exitApp() },
                    onDownload = { entry ->
                        // 先让服务端建下载任务，拿到任务信息后再让用户选保存位置
                        fileBrowserViewModel.download(entry) { item ->
                            pendingDownload = item
                            downloadSaver.launch(item.fileName)
                        }
                    },
                )
            }
        }

        // 横竖屏重建会带回同一个 intent——那不是新的一次分享
        if (savedInstanceState == null) {
            handleShareIntent(intent)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        // 从别的应用又分享一次时，系统复用这个实例，走这里
        setIntent(intent)
        handleShareIntent(intent)
    }

    /**
     * 系统分享进来的东西。
     *
     * 只做一件事：交给传输引擎，落点是电脑的默认接收目录（分享没有"当前目录"这回事）。
     * 分享给的是**临时**读权限（不是可持久化的授权），所以必须立刻开传，
     * 不能先摆一个确认页等用户点——等回来权限可能已经收回去了。
     */
    private fun handleShareIntent(source: Intent?) {
        val intent = source ?: return
        val action = intent.action
        if (action != Intent.ACTION_SEND && action != Intent.ACTION_SEND_MULTIPLE) {
            return
        }

        val uris = mutableListOf<Uri>()

        // 单选 EXTRA_STREAM 是 Uri，多选是 ArrayList<Uri>；两种都可能缺
        if (action == Intent.ACTION_SEND_MULTIPLE) {
            intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.forEach(uris::add)
        } else {
            @Suppress("DEPRECATION")
            (intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri)?.let(uris::add)
        }

        // 有的应用（微信、部分浏览器）只塞 clipData，不写 EXTRA_STREAM
        intent.clipData?.let { clip ->
            for (index in 0 until clip.itemCount) {
                clip.getItemAt(index).uri?.let(uris::add)
            }
        }

        val picked = uris.distinct()
        Log.i("WcShare", "收到分享 action=$action 文件=${picked.size} 文字=${intent.hasExtra(Intent.EXTRA_TEXT)}")

        val items: List<UploadItem>
        val label: String

        if (picked.isEmpty()) {
            val text = intent.getStringExtra(Intent.EXTRA_TEXT)
            if (text.isNullOrBlank()) {
                // 没有可发的东西：跳到传输页，把那句话放在用户眼前
                shareTick++
                transferViewModel.notifyMessage("没有可发送的文件")
                return
            }

            // 纯文字先落成 txt，发给谁等用户在框里选
            val item = transferViewModel.materializeShareText(text) ?: return
            items = listOf(item)
            label = item.displayName ?: "1 个文件"
        } else {
            items = picked.map { UploadItem(it) }
            label = "${items.size} 个文件"
        }

        shareMessage = null
        shareBusy = false
        shareDraft = ShareDraft(uris = items.map { it.uri }, items = items, label = label)
    }

    /**
     * 选择框里点了某台电脑。
     *
     * 连着就直接发；断着就先重连；换成另一台电脑时地址没有存过，只能在同一 Wi-Fi 里
     * 扫出来再连。这几步都可能要几秒，所以选择框上有 busy 态——别让用户以为点空了。
     */
    private fun sendShareToComputer(deviceId: String) {
        val draft = shareDraft ?: return
        if (shareJob?.isActive == true) return

        shareBusy = true
        shareMessage = null

        shareJob = lifecycleScope.launch {
            // 硬闸门：连不上就 20 秒收工。重连退避、mDNS 扫描都可能很久，
            // 不能让用户对着一个点不动的弹框干等。
            val ready = withTimeoutOrNull(SHARE_CONNECT_TIMEOUT_MS) {
                ensureComputerReady(deviceId)
            } ?: false

            if (!ready) {
                shareBusy = false
                if (shareMessage == null) {
                    shareMessage = "没连上这台电脑，稍后再试"
                }
                return@launch
            }

            shareDraft = null
            shareBusy = false
            shareMessage = null
            shareTick++

            // 落点固定电脑的默认接收目录：分享没有"当前目录"这回事
            transferViewModel.uploadItems(
                draft.items,
                UploadTarget(directoryId = null, relativeDirectory = null),
            )
        }
    }

    /** 取消：连电脑的过程也一起掐掉，不然弹框关了它还在后台连。 */
    private fun dismissShare() {
        shareJob?.cancel()
        shareJob = null
        shareBusy = false
        shareDraft = null
        shareMessage = null
    }

    /** 确保 [deviceId] 这台电脑的会话可用；成功返回 true。 */
    private suspend fun ensureComputerReady(deviceId: String): Boolean {
        val state = pairingViewModel.state.value
        if (state.sessionDeviceId == deviceId && state.isConnected) {
            return true
        }

        // 同一台但断着：按上次的地址重连，比重新扫描快
        if (state.sessionDeviceId == deviceId) {
            shareMessage = "正在重连…"
            Log.i("WcShare", "分享：重连当前电脑 $deviceId")
            pairingViewModel.retrySession()
            if (awaitConnected(deviceId, 10_000)) {
                return true
            }
        }

        // 换一台：设备记录里只有名字和指纹，没有地址，只能在局域网里找回来
        shareMessage = "正在找这台电脑…"
        Log.i("WcShare", "分享：扫描局域网找 $deviceId")
        pairingViewModel.startScan()

        val found = try {
            withTimeoutOrNull(8_000) {
                pairingViewModel.state
                    .map { it.discovered }
                    .first { servers -> servers.any { it.deviceId == deviceId } }
                    .first { it.deviceId == deviceId }
            }
        } finally {
            pairingViewModel.stopScan()
        }

        if (found == null) {
            shareMessage = "没找到这台电脑，确认它开着、和手机在同一个 Wi-Fi"
            return false
        }

        shareMessage = "正在连接…"
        pairingViewModel.connectTo(found)

        if (awaitConnected(deviceId, 15_000)) {
            return true
        }

        shareMessage = "连接超时，稍后再试"
        return false
    }

    /** 等某台电脑连上；超时返回 false。 */
    private suspend fun awaitConnected(deviceId: String, timeoutMs: Long): Boolean =
        withTimeoutOrNull(timeoutMs) {
            pairingViewModel.state.first { it.sessionDeviceId == deviceId && it.isConnected }
        } != null
}
