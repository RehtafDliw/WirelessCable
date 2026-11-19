package com.wirelesscable.app.ui.screens

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.core.apps.InstalledApp
import com.wirelesscable.app.core.direct.DirectFileDto
import com.wirelesscable.app.core.direct.DirectProtocol
import com.wirelesscable.app.ui.components.AppPickerDialog
import com.wirelesscable.app.core.direct.DirectLog
import com.wirelesscable.app.core.direct.DirectSession
import com.wirelesscable.app.core.direct.HotspotPermissions
import com.wirelesscable.app.ui.components.DetailRow
import com.wirelesscable.app.ui.components.CloseIcon
import com.wirelesscable.app.ui.components.WcCard
import com.wirelesscable.app.ui.components.WcIconAction
import com.wirelesscable.app.ui.components.WcSectionHeader
import com.wirelesscable.app.ui.components.WcTopBar
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.ui.components.WcLinkButton
import com.wirelesscable.app.ui.components.WcPrimaryButton
import com.wirelesscable.app.vm.DirectViewModel
import com.wirelesscable.app.core.i18n.Str

/**
 * 两机直连页。
 *
 * 结构按先选角色、再连、连上才给动作排：
 * 1. 没开始时只有两个按钮：**对方扫我** / **我扫对方**；
 * 2. 出码后只有二维码（对方扫它），不显示发送按钮——还没连上，发了也没人接；
 * 3. 连上之后才出现「已连接 · 对方是谁」和「发送文件给对方」。
 *
 * 会话是一次性的：断开、退出、换网络都要重新扫码，所以这一页永远只有当前这一次。
 */
@Composable
fun DirectScreen(
    state: DirectViewModel.State,
    onHost: () -> Unit,
    onToggleHotspot: (Boolean) -> Unit,
    /** 申请完热点的权限仍然没有：由 ViewModel 记成一条可操作的提示。 */
    onPermissionDenied: (String) -> Unit,
    onAddShared: (List<Uri>) -> Unit,
    onStopHosting: () -> Unit,
    onScan: () -> Unit,
    onLeave: () -> Unit,
    onDownload: (DirectFileDto) -> Unit,
    onSend: (Uri) -> Unit,
    /** 出码方：把选中的应用加进共享清单。 */
    onAddApps: (List<InstalledApp>) -> Unit,
    /** 扫码方：把选中的应用推给对方。 */
    onSendApps: (List<InstalledApp>) -> Unit,
    /** 扫码方：把一整组应用下到本地并安装。 */
    onInstallPeerApp: (String) -> Unit,
    /** 出码方：安装对方推过来的应用。 */
    onInstallReceivedApp: (String) -> Unit,
    /** 出码方：点一下用系统默认应用打开对方推过来的文件。 */
    onOpenReceived: (DirectSession.ReceivedFile) -> Unit,
    onRetryHotspot: () -> Unit,
    onBack: () -> Unit,
) {
    // 出码方：连接后追加要共享给对方拉的文件
    val pickShared = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments(),
    ) { uris -> if (uris.isNotEmpty()) onAddShared(uris) }

    // 扫码方：直接推给对方
    val pickSend = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument(),
    ) { uri -> uri?.let(onSend) }

    // 应用选择器：出码方是加进共享清单，扫码方是直接推过去
    var appPickerTarget by remember { mutableStateOf<AppPickerTarget?>(null) }
    if (appPickerTarget != null) {
        AppPickerDialog(
            onDismiss = { appPickerTarget = null },
            onConfirm = { apps ->
                val target = appPickerTarget
                appPickerTarget = null
                if (apps.isNotEmpty()) {
                    when (target) {
                        AppPickerTarget.Host -> onAddApps(apps)
                        AppPickerTarget.Join -> onSendApps(apps)
                        null -> Unit
                    }
                }
            },
        )
    }

    // 开热点需要「定位」（各版本）或「附近的设备」（13+）。
    // 先申请，拿到再真的打开开关；被拒就把开关退回去，并说明为什么——
    // 以前这里只是把开关弹回去，用户以为开关坏了（1.5.57）。
    val context = LocalContext.current
    var pendingHotspot by remember { mutableStateOf(false) }

    val hotspotLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) { result ->
        // 不看返回的 map，直接查真实授权状态：系统和 ROM 认的权限不完全一样，
        // 用 `any`/`all` 判会得出有权限但实际调不动的错误结论。
        val ok = HotspotPermissions.canUseHotspot(context)
        DirectLog.i(
            "开热点权限申请结果 result=$result 实际 ${HotspotPermissions.describe(context)} " +
                "canAskAgain=${HotspotPermissions.canAskAgain(context)}",
        )

        if (!ok) {
            onPermissionDenied(HotspotPermissions.missingText(context).orEmpty())
        }
        onToggleHotspot(ok && pendingHotspot)
        pendingHotspot = false
    }

    fun toggleHotspot(enabled: Boolean) {
        if (!enabled) {
            onToggleHotspot(false)
            return
        }

        if (HotspotPermissions.canUseHotspot(context)) {
            onToggleHotspot(true)
        } else {
            pendingHotspot = true
            hotspotLauncher.launch(HotspotPermissions.requestArray())
        }
    }

    // 这一页是覆盖层，没有系统返回键处理，按返回会直接退出应用
    BackHandler(enabled = true) { onBack() }

    Column(modifier = Modifier.fillMaxWidth()) {
        WcTopBar(title = Str.t("当面直连")) {
            WcIconAction(
                icon = CloseIcon,
                onClick = onBack,
                contentDescription = Str.t("关闭"),
            )
        }

        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = WcSpace.s4)
                .navigationBarsPadding(),
        ) {
        when {
            state.hosting -> HostingContent(
                state = state,
                onPickShared = { pickShared.launch(arrayOf("*/*")) },
                onPickApps = { appPickerTarget = AppPickerTarget.Host },
                onInstallReceived = onInstallReceivedApp,
                onOpenReceived = onOpenReceived,
                onStop = onStopHosting,
            )

            state.joined -> JoinedContent(
                state = state,
                onDownload = onDownload,
                onPickSend = { pickSend.launch(arrayOf("*/*")) },
                onPickApps = { appPickerTarget = AppPickerTarget.Join },
                onInstallPeerApp = onInstallPeerApp,
                onLeave = onLeave,
            )

            else -> IdleContent(
                state = state,
                onHost = onHost,
                onScan = onScan,
                onToggleHotspot = { toggleHotspot(it) },
            )
        }

        // 权限被拒（或系统不再弹框）：给一个真正能解决问题的出口。
        // 只写一行没有权限等于把用户扔在原地——13+ 上拒绝两次之后系统就不会再弹了。
        state.permissionIssue?.let { issue ->
            Spacer(Modifier.height(12.dp))
            PermissionCard(
                message = issue,
                onOpenSettings = { HotspotPermissions.openAppSettings(context) },
                onRetry = onRetryHotspot,
            )
        }

        // 自动加入热点失败：给出 SSID/密码，让用户去系统设置手动连一次。
        // 这是唯一在所有 ROM 上都能走通热点的路径（1.5.54）。
        state.hotspotFallback?.let { hint ->
            Spacer(Modifier.height(12.dp))
            HotspotFallbackCard(
                hint = hint,
                waiting = state.waitingForNetwork,
                onOpenSettings = {
                    runCatching {
                        context.startActivity(
                            Intent(android.provider.Settings.ACTION_WIFI_SETTINGS)
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                        )
                    }
                },
                onRetry = onRetryHotspot,
            )
        }

        state.message?.let { raw ->
            val message = Str.t(raw)
            Spacer(Modifier.height(12.dp))
            Text(text = message, fontSize = WcType.labelSize, color = WcColors.Muted)
        }

        Spacer(Modifier.height(WcSpace.s6))
        }
    }
}

/**
 * 缺少热点权限时的出口。
 *
 * 必须有「去设置」这一条：Android 13 起，同一个权限被拒绝两次之后系统**不会再弹框**，
 * 无论代码里怎么 `launch()` 都是立刻返回 denied —— 界面上看起来就是点了没反应。
 */
@Composable
private fun PermissionCard(
    message: String,
    onOpenSettings: () -> Unit,
    onRetry: () -> Unit,
) {
    WcCard {
        Text(text = Str.t("缺少热点权限"), fontSize = 13.sp, color = WcColors.Text)
        Spacer(Modifier.height(6.dp))
        Text(
            text = Str.t("$message。系统没弹授权框时，多半是之前拒绝过两次；在应用设置里打开后回来点重试。"),
            fontSize = 12.5.sp,
            color = WcColors.Muted,
        )
        Spacer(Modifier.height(10.dp))
        WcPrimaryButton(
            text = Str.t("打开应用设置"),
            onClick = onOpenSettings,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(6.dp))
        WcLinkButton(text = Str.t("重试"), onClick = onRetry, color = WcColors.Accent)
    }
}

/**
 * 自动加入热点失败时的兜底卡片。
 *
 * 有些 ROM 不让应用自动加入热点（`WifiNetworkSpecifier` 直接 `onUnavailable`），
 * 但系统设置里手动连是通的；连好之后点「重试」，走默认网络的网关就能连上对方。
 */
@Composable
private fun HotspotFallbackCard(
    hint: DirectViewModel.HotspotFallback,
    waiting: Boolean,
    onOpenSettings: () -> Unit,
    onRetry: () -> Unit,
) {
    WcCard {
        Text(
            text = if (waiting) Str.t("等待你连上对方热点") else Str.t("自动连对方热点没成功"),
            fontSize = 13.sp,
            color = WcColors.Text,
        )
        Spacer(Modifier.height(6.dp))
        Text(
            text = Str.t("在系统 Wi-Fi 里手动连接「${hint.ssid}」，密码：${hint.passphrase}") +
                if (waiting) Str.t("。连上后这里会自动继续，不用切回来。") else "",
            fontSize = 12.5.sp,
            color = WcColors.Muted,
        )
        Spacer(Modifier.height(10.dp))
        WcPrimaryButton(
            text = Str.t("打开 Wi-Fi 设置"),
            onClick = onOpenSettings,
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(6.dp))
        WcLinkButton(text = Str.t("我已连上，重试"), onClick = onRetry, color = WcColors.Accent)
    }
}

@Composable
private fun IdleContent(
    state: DirectViewModel.State,
    onHost: () -> Unit,
    onScan: () -> Unit,
    onToggleHotspot: (Boolean) -> Unit,
) {
    WcPrimaryButton(
        text = if (state.busy) Str.t("正在准备…") else Str.t("对方扫我"),
        onClick = onHost,
        modifier = Modifier.fillMaxWidth(),
        enabled = !state.busy,
    )

    Spacer(Modifier.height(10.dp))

    WcPrimaryButton(
        text = Str.t("我扫对方"),
        onClick = onScan,
        modifier = Modifier.fillMaxWidth(),
        enabled = !state.busy,
        soft = true,
    )

    Spacer(Modifier.height(16.dp))

    WcCard {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(text = Str.t("用热点直连"), fontSize = 14.sp, color = WcColors.Text)
                Spacer(Modifier.height(3.dp))
                Text(
                    text = Str.t("当前 Wi-Fi 传不动时打开。对方连不上会改用你开的热点。"),
                    fontSize = 12.sp,
                    color = WcColors.Muted,
                )
            }

            Switch(checked = state.useHotspot, onCheckedChange = onToggleHotspot)
        }
    }

    Spacer(Modifier.height(12.dp))
}

@Composable
private fun HostingContent(
    state: DirectViewModel.State,
    onPickShared: () -> Unit,
    onPickApps: () -> Unit,
    onInstallReceived: (String) -> Unit,
    onOpenReceived: (DirectSession.ReceivedFile) -> Unit,
    onStop: () -> Unit,
) {
    WcSectionHeader(if (state.connected) Str.t("已连接") else Str.t("等对方扫码"))

    WcCard {
        if (!state.connected) {
            // 还没连上：只放二维码。发送按钮等对方进来再给，
            // 否则点了也只是塞进一个没人拉的清单（1.5.12）。
            state.qr?.let { bitmap ->
                Box(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                    Image(
                        bitmap = bitmap.asImageBitmap(),
                        contentDescription = Str.t("直连二维码"),
                        modifier = Modifier
                            .size(220.dp)
                            .clip(RoundedCornerShape(8.dp))
                            .background(WcColors.Card),
                    )
                }

                Spacer(Modifier.height(8.dp))
                Text(
                    text = Str.t("请对方点「我扫对方」扫这张图。二维码 5 分钟内有效。"),
                    fontSize = 12.sp,
                    color = WcColors.Muted,
                )
            }
        } else {
            DetailRow(Str.t("对方"), Str.t("已接入"))
            DetailRow(Str.t("已发送"), Str.t("${state.shared.size} 个文件"))

            if (state.sendingName != null) {
                Spacer(Modifier.height(10.dp))
                Text(
                    // 出码方既会发（对方拉共享文件）也会收（对方推文件给我），
                    // 两个方向都走这一块 UI，文案必须分开（1.5.64）
                    text = (if (state.transferIncoming) Str.t("正在接收：") else Str.t("正在发送：")) + state.sendingName,
                    fontSize = 12.5.sp,
                    color = WcColors.Muted,
                )
                Spacer(Modifier.height(4.dp))
                LinearProgressIndicator(
                    progress = { state.progress },
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    text = "${formatSize(state.sentBytes)} / ${formatSize(state.totalBytes)}",
                    fontSize = 12.sp,
                    color = WcColors.Muted,
                )
            }

            Spacer(Modifier.height(12.dp))
            WcPrimaryButton(
                text = Str.t("发送文件给对方"),
                onClick = onPickShared,
                modifier = Modifier.fillMaxWidth(),
                enabled = !state.busy,
            )
            Spacer(Modifier.height(8.dp))
            WcPrimaryButton(
                text = Str.t("发送应用给对方"),
                onClick = onPickApps,
                modifier = Modifier.fillMaxWidth(),
                enabled = !state.busy,
                soft = true,
            )
            Spacer(Modifier.height(6.dp))
            Text(
                // 出码方是服务端，没有反向连接：文件先登记进共享清单，
                // 对方点下载才真正开始传。不写这句，用户会以为点了没反应。
                text = Str.t("对方在列表里点「下载」后才开始传；应用会把安装包一起发过去。"),
                fontSize = 12.sp,
                color = WcColors.Muted,
            )
        }

        Spacer(Modifier.height(12.dp))
        WcLinkButton(text = Str.t("结束会话"), onClick = onStop, color = WcColors.Muted)
    }

    if (state.shared.isNotEmpty()) {
        Spacer(Modifier.height(14.dp))
        WcSectionHeader(Str.t("已发送给对方"))
        WcCard {
            state.shared.forEach { file ->
                Text(
                    text = "${file.name}  ·  ${formatSize(file.sizeBytes)}",
                    fontSize = 12.5.sp,
                    color = WcColors.Text,
                    modifier = Modifier.padding(vertical = 3.dp),
                )
            }
        }
    }

    val plainReceived = state.received.filter { it.group == null }
    if (plainReceived.isNotEmpty()) {
        Spacer(Modifier.height(14.dp))
        WcSectionHeader(Str.t("对方发来的文件"))
        WcCard {
            plainReceived.forEach { file ->
                // 点一行就用系统默认应用打开——收到的东西本来就该能直接看
                Text(
                    text = "${file.name}  ·  ${formatSize(file.sizeBytes)}",
                    fontSize = 12.5.sp,
                    color = WcColors.Text,
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { onOpenReceived(file) }
                        .padding(vertical = 6.dp),
                )
            }
        }
    }

    // 应用按包分组：分体包必须一起装，单个文件给人看没有意义
    val receivedApps = state.received
        .filter { it.group != null }
        .groupBy { it.group!! }

    if (receivedApps.isNotEmpty()) {
        Spacer(Modifier.height(14.dp))
        WcSectionHeader(Str.t("收到的应用"))

        receivedApps.forEach { (group, files) ->
            WcCard {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = appLabelOf(files.map { it.name }),
                            fontSize = 13.5.sp,
                            color = WcColors.Text,
                        )
                        Text(
                            text = Str.t("${files.size} 个安装包 · ${formatSize(files.sumOf { it.sizeBytes })}"),
                            fontSize = 11.5.sp,
                            color = WcColors.Muted,
                        )
                    }

                    Text(
                        text = Str.t("安装"),
                        fontSize = 13.sp,
                        color = WcColors.Accent,
                        modifier = Modifier
                            .clickable(enabled = !state.busy) { onInstallReceived(group) }
                            .padding(vertical = 4.dp, horizontal = 6.dp),
                    )
                }
            }
            Spacer(Modifier.height(8.dp))
        }
    }
}

/**
 * 从一组安装包文件名里还原应用名：`微信.apk` / `微信-part1.apk` → `微信`。
 *
 * 优先找 base（不带 `-part`），找不到就取第一个。
 */
private fun appLabelOf(names: List<String>): String {
    val base = names.firstOrNull { !it.contains("-part") } ?: names.firstOrNull().orEmpty()
    return base.removeSuffix(".apk").ifBlank { Str.t("应用") }
}

@Composable
/** 链路一行：说清这条路能跑多快。慢的时候直接给建议，别让用户对着进度条猜。 */
private fun linkText(state: DirectViewModel.State): String {
    // 走的是哪条路要写出来：以前界面只说多快，用户根本不知道有没有走热点（1.5.51）
    val path = if (state.viaHotspot) Str.t("热点 · ") else ""

    if (state.linkMeasuring) {
        return Str.t("${path}测速中…")
    }

    if (state.linkFailed) {
        return Str.t("${path}不通（256 KB 都收不到）")
    }

    val speed = state.linkBytesPerSecond ?: return if (state.viaHotspot) Str.t("热点") else Str.t("未测")
    val text = "$path${formatSize(speed)}/s"

    // 对方的码里已经带了热点，就别再劝他开热点——那会让人以为开关没生效
    val hotspotReady = state.peerHasHotspot || state.viaHotspot

    return when {
        speed < 200 * 1024 && hotspotReady -> Str.t("$text（很慢）")
        speed < 200 * 1024 -> Str.t("$text（很慢，建议让对方打开「用热点直连」）")
        speed < 1024 * 1024 -> Str.t("$text（偏慢）")
        else -> text
    }
}

@Composable
private fun JoinedContent(
    state: DirectViewModel.State,
    onDownload: (DirectFileDto) -> Unit,
    onPickSend: () -> Unit,
    onPickApps: () -> Unit,
    onInstallPeerApp: (String) -> Unit,
    onLeave: () -> Unit,
) {
    WcSectionHeader(Str.t("已连接"))

    WcCard {
        DetailRow(Str.t("对方"), state.peerName)
        DetailRow(Str.t("对方文件"), Str.t("${state.peerFiles.size} 个"))
        DetailRow(Str.t("链路"), linkText(state))

        state.sendingName?.let {
            Spacer(Modifier.height(10.dp))
            Text(text = Str.t("正在传：$it"), fontSize = 12.5.sp, color = WcColors.Muted)
            Spacer(Modifier.height(4.dp))
            LinearProgressIndicator(
                progress = { state.progress },
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(4.dp))
            Text(
                // 断流时进度条不动，用户只能猜是不是死了；换一句话告诉他还在重试（1.5.48）
                text = if (state.stalled) {
                    Str.t("链路卡住，正在重试…")
                } else {
                    "${formatSize(state.sentBytes)} / ${formatSize(state.totalBytes)}"
                },
                fontSize = 12.sp,
                color = if (state.stalled) WcColors.WarningText else WcColors.Muted,
            )
        }

        Spacer(Modifier.height(12.dp))
        WcPrimaryButton(
            text = Str.t("发送文件给对方"),
            onClick = onPickSend,
            modifier = Modifier.fillMaxWidth(),
            enabled = !state.busy,
        )
        Spacer(Modifier.height(8.dp))
        WcPrimaryButton(
            text = Str.t("发送应用给对方"),
            onClick = onPickApps,
            modifier = Modifier.fillMaxWidth(),
            enabled = !state.busy,
            soft = true,
        )
        Spacer(Modifier.height(8.dp))
        WcLinkButton(text = Str.t("断开"), onClick = onLeave, color = WcColors.Muted)
    }

    Spacer(Modifier.height(14.dp))

    val plainPeer = state.peerFiles.filter { it.group == null }
    val peerApps = state.peerFiles.filter { it.group != null }.groupBy { it.group!! }

    if (state.peerFiles.isEmpty()) {
        WcSectionHeader(Str.t("对方还没有文件"))
        Text(
            text = Str.t("对方发送后会出现在这里，不用重连。"),
            fontSize = 12.5.sp,
            color = WcColors.Muted,
        )
    } else if (plainPeer.isNotEmpty()) {
        WcSectionHeader(Str.t("对方发来的文件"))
        plainPeer.forEach { file ->
            WcCard {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(text = file.name, fontSize = 13.5.sp, color = WcColors.Text)
                        Text(
                            text = formatSize(file.sizeBytes),
                            fontSize = 12.sp,
                            color = WcColors.Muted,
                        )
                    }

                    Text(
                        text = if (state.downloaded.contains(file.id)) Str.t("已下载") else Str.t("下载"),
                        fontSize = 13.sp,
                        color = if (state.downloaded.contains(file.id)) WcColors.Muted else WcColors.Accent,
                        modifier = Modifier.clickable(enabled = !state.busy && !state.downloaded.contains(file.id)) {
                            onDownload(file)
                        },
                    )
                }
            }
            Spacer(Modifier.height(8.dp))
        }
    }

    if (peerApps.isNotEmpty()) {
        Spacer(Modifier.height(14.dp))
        WcSectionHeader(Str.t("对方发来的应用"))

        peerApps.forEach { (group, files) ->
            val allDownloaded = files.all { state.downloaded.contains(it.id) }
            val parts = files.sortedBy { if (it.kind == DirectProtocol.KIND_APK_BASE) 0 else 1 }

            WcCard {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = appLabelOf(parts.map { it.name }),
                            fontSize = 13.5.sp,
                            color = WcColors.Text,
                        )
                        Text(
                            text = Str.t("${files.size} 个安装包 · ${formatSize(files.sumOf { it.sizeBytes })}"),
                            fontSize = 11.5.sp,
                            color = WcColors.Muted,
                        )
                    }

                    Text(
                        text = if (allDownloaded) Str.t("安装") else Str.t("下载并安装"),
                        fontSize = 13.sp,
                        color = if (allDownloaded) WcColors.Success else WcColors.Accent,
                        modifier = Modifier
                            .clickable(enabled = !state.busy) { onInstallPeerApp(group) }
                            .padding(vertical = 4.dp, horizontal = 6.dp),
                    )
                }
            }
            Spacer(Modifier.height(8.dp))
        }
    }
}

/** 应用选择器的目标：出码方加进共享清单，扫码方直接推过去。 */
private enum class AppPickerTarget { Host, Join }
