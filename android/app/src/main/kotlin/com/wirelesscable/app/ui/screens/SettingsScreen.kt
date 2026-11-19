package com.wirelesscable.app.ui.screens

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.BuildConfig
import com.wirelesscable.app.core.files.AllFilesProvider
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.FileRootDto
import com.wirelesscable.app.ui.components.DetailRow
import com.wirelesscable.app.ui.components.SessionSyncCard
import com.wirelesscable.app.ui.components.StatusPill
import com.wirelesscable.app.ui.components.Tone
import com.wirelesscable.app.ui.components.WcCard
import com.wirelesscable.app.ui.components.WcInput
import com.wirelesscable.app.ui.components.WcDialog
import com.wirelesscable.app.ui.components.WcSectionHeader
import com.wirelesscable.app.ui.components.WcLinkButton
import com.wirelesscable.app.ui.components.WcPrimaryButton
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.ThemeMode
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcThemeState
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.vm.LocalRootInfo
import com.wirelesscable.app.vm.ConnectionUiState
import com.wirelesscable.app.vm.PairingUiState
import com.wirelesscable.app.core.i18n.LangMode
import com.wirelesscable.app.core.i18n.LangState
import com.wirelesscable.app.core.i18n.Str
import com.wirelesscable.app.ui.components.deviceLabel

/**
 * 设置：已配对设备（含按设备授权）、服务管理、通知、主题、关于。
 *
 * 连接地址/端口/指纹收进底部「高级」并默认收起——
 * 正常流程靠扫码与发现，普通用户不需要看到 IP。
 */
@Composable
fun SettingsScreen(
    state: ConnectionUiState,
    pairing: PairingUiState,
    /** 手机本地真实授权（与服务器是否收到无关）。只用来提示还没同步到电脑。 */
    localRoots: List<LocalRootInfo>,
    /** 服务器已经收到的根数量；少于本地数量说明同步还没跟上。 */
    syncedRootCount: Int,
    /** 是否已拿到「所有文件访问权」。 */
    allFilesGranted: Boolean,
    /** 设备行「授权」：整机开关（deviceId, 是否允许）。 */
    onSetMachineAccess: (String, Boolean) -> Unit,
    /** 设备行「授权」：为这台电脑选一个目录（deviceId）。 */
    onGrantFolderToDevice: (String) -> Unit,
    /** 设备行「授权」：这台电脑当前能看到的手机目录（同步读本地授权）。 */
    grantedFolders: (String) -> List<LocalRootInfo>,
    /** 设备行「授权」：撤销某个目录。 */
    onRevokeRoot: (String) -> Unit,
    onRenameDevice: (String, String) -> Unit,
    onForgetDevice: (String) -> Unit,
    /** 整机访问是系统权限，只能在系统设置里开。 */
    onOpenAllFilesSettings: () -> Unit,
    onHostChanged: (String) -> Unit,
    onPortChanged: (String) -> Unit,
    onFingerprintChanged: (String) -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var advanced by remember { mutableStateOf(false) }
    var grantingDevice by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current

    Column(
        modifier = modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
    ) {
        WcSectionHeader(Str.t("已配对设备"))

        PairedDevicesSection(
            devices = pairing.pairedDevices,
            sessionState = pairing.sessionState,
            sessionDeviceId = pairing.sessionDeviceId,
            onRename = onRenameDevice,
            onForget = onForgetDevice,
            onGrant = { deviceId -> grantingDevice = deviceId },
        )

        // 授权是本地生效、再上报给电脑的。没同步成功时给一句话，
        // 否则用户只能在电脑端干瞪眼（K72）。没有会话时不提示：那不是故障。
        if (pairing.isConnected && localRoots.size > syncedRootCount) {
            val syncState by SessionSyncEngine.state.collectAsStateWithLifecycle()
            Spacer(Modifier.height(6.dp))
            Text(
                text = Str.t("有 ${localRoots.size - syncedRootCount} 个授权还没同步到电脑") +
                    (syncState.lastError?.let { "：$it" } ?: ""),
                fontSize = 11.5.sp,
                color = WcColors.Muted,
            )
        }

        grantingDevice?.let { deviceId ->
            WcDialog(
                onDismissRequest = { grantingDevice = null },
                title = { Text(Str.t("授权给这台电脑")) },
                text = {
                    Column {
                        Text(
                            text = Str.t("整机访问需要系统权限；目录授权只放开你选的那一层。"),
                            fontSize = 12.5.sp,
                            color = WcColors.Muted,
                        )
                        Spacer(Modifier.height(8.dp))
                        Text(
                            text = if (allFilesGranted) Str.t("允许整机访问") else Str.t("开启整机访问（需系统权限）"),
                            fontSize = 14.sp,
                            color = WcColors.Accent,
                            modifier = Modifier
                                .clickable {
                                    grantingDevice = null
                                    if (allFilesGranted) onSetMachineAccess(deviceId, true) else onOpenAllFilesSettings()
                                }
                                .padding(vertical = 8.dp),
                        )
                        Text(
                            text = Str.t("取消整机访问"),
                            fontSize = 14.sp,
                            color = WcColors.Text,
                            modifier = Modifier
                                .clickable {
                                    grantingDevice = null
                                    onSetMachineAccess(deviceId, false)
                                }
                                .padding(vertical = 8.dp),
                        )
                        Text(
                            text = Str.t("选择目录…"),
                            fontSize = 14.sp,
                            color = WcColors.Accent,
                            modifier = Modifier
                                .clickable {
                                    grantingDevice = null
                                    onGrantFolderToDevice(deviceId)
                                }
                                .padding(vertical = 8.dp),
                        )

                        // 已授权的目录就地列出并可撤销：撤掉这一处之后，
                        // 手机上就没有任何地方能收回授权了
                        val folders = grantedFolders(deviceId)
                        if (folders.isNotEmpty()) {
                            Spacer(Modifier.height(6.dp))
                            Text(
                                text = Str.t("已授权的目录"),
                                fontSize = 11.5.sp,
                                color = WcColors.Muted,
                            )
                            folders.forEach { folder ->
                                Row(
                                    modifier = Modifier.fillMaxWidth(),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Text(
                                        text = folder.displayName,
                                        fontSize = 13.sp,
                                        color = WcColors.Text,
                                        modifier = Modifier.weight(1f),
                                    )
                                    Text(
                                        text = Str.t("撤销"),
                                        fontSize = 12.5.sp,
                                        color = WcColors.Error,
                                        modifier = Modifier
                                            .clickable { onRevokeRoot(folder.rootId) }
                                            .padding(horizontal = 6.dp, vertical = 4.dp),
                                    )
                                }
                            }
                        }
                    }
                },
                confirmButton = {
                    TextButton(onClick = { grantingDevice = null }) { Text(Str.t("关闭")) }
                },
            )
        }

        WcSectionHeader(Str.t("服务管理"))

        // 「有没有配对关系」要看真实的已配对设备，不能看手动配对输入框里的指纹：
        // 扫码/发现配对的设备不会往那个输入框写值，否则会出现
        // 明明连着电脑，服务开关却是灰的（1.1.2 修）。
        val paired = pairing.pairedDevices.isNotEmpty()

        SessionSyncCard(
            paired = paired,
            allFilesGranted = allFilesGranted,
            onOpenAllFilesSettings = onOpenAllFilesSettings,
        )

        WcSectionHeader(Str.t("通知"))

        NotificationCard()

        WcSectionHeader(Str.t("外观"))

        WcCard {
            Text(text = Str.t("主题"), fontSize = 13.sp, color = WcColors.Muted)
            Spacer(Modifier.height(8.dp))

            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ThemeOption(Str.t("跟随系统"), WcThemeState.mode == ThemeMode.System) {
                    WcThemeState.set(context, ThemeMode.System)
                }
                ThemeOption(Str.t("浅色"), WcThemeState.mode == ThemeMode.Light) {
                    WcThemeState.set(context, ThemeMode.Light)
                }
                ThemeOption(Str.t("深色"), WcThemeState.mode == ThemeMode.Dark) {
                    WcThemeState.set(context, ThemeMode.Dark)
                }
            }

            Spacer(Modifier.height(14.dp))
            Text(text = Str.t("语言"), fontSize = 13.sp, color = WcColors.Muted)
            Spacer(Modifier.height(8.dp))

            // 语言独立于系统：手机是中文也能用英文界面，反之同理。
            // 切换是即时的（LangState 是可观察状态），不重建 Activity，正在传的任务不受影响。
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ThemeOption(Str.t("跟随系统"), LangState.mode == LangMode.System) {
                    LangState.set(context, LangMode.System)
                }
                ThemeOption("中文", LangState.mode == LangMode.Zh) {
                    LangState.set(context, LangMode.Zh)
                }
                ThemeOption("English", LangState.mode == LangMode.En) {
                    LangState.set(context, LangMode.En)
                }
            }
        }

        WcSectionHeader(Str.t("关于"))

        WcCard {
            DetailRow(Str.t("应用"), "WirelessCable ${BuildConfig.VERSION_NAME}")

            // 设备名优先取当前会话，其次取配对记录——
            // state.device 只在手动连接路径上有值，正常配对时是空的，
            // 直接显示 "-" 会让用户以为没连上。
            val current = pairing.activeServer?.let { deviceLabel(it.displayName, it.deviceId) }
                ?: pairing.pairedDevices.firstOrNull { it.deviceId == pairing.sessionDeviceId }
                    ?.let { deviceLabel(it.displayName, it.deviceId) }
            if (current != null) {
                DetailRow(Str.t("电脑"), current)
            }

            state.device?.osVersion?.let { os -> DetailRow(Str.t("电脑系统"), os) }
        }

        Spacer(Modifier.height(16.dp))

        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            WcSectionHeader(Str.t("高级"), modifier = Modifier.weight(1f))
            WcLinkButton(
                text = if (advanced) Str.t("收起") else Str.t("展开"),
                onClick = { advanced = !advanced },
                color = WcColors.Muted,
            )
        }

        if (advanced) {
            WcCard {
                WcInput(
                    value = state.host,
                    onValueChange = onHostChanged,
                    label = Str.t("电脑地址"),
                    placeholder = "192.168.1.10",
                    enabled = !state.isBusy,
                )

                Spacer(Modifier.height(8.dp))

                WcInput(
                    value = state.port,
                    onValueChange = onPortChanged,
                    label = Str.t("端口"),
                    enabled = !state.isBusy,
                )

                Spacer(Modifier.height(8.dp))

                WcInput(
                    value = state.fingerprint,
                    onValueChange = onFingerprintChanged,
                    label = Str.t("证书指纹"),
                    enabled = !state.isBusy,
                )

                Spacer(Modifier.height(12.dp))

                WcPrimaryButton(
                    text = when {
                        state.isBusy -> Str.t("连接中…")
                        state.isConnected -> Str.t("断开")
                        else -> Str.t("连接")
                    },
                    onClick = if (state.isConnected) onDisconnect else onConnect,
                    enabled = !state.isBusy,
                    soft = state.isConnected,
                )

                state.errorMessage?.let { raw ->
                    Spacer(Modifier.height(8.dp))
                    Text(text = Str.t(raw), color = WcColors.Error, fontSize = 12.5.sp)
                }
            }
        }

        Spacer(Modifier.height(12.dp))
    }
}

/** 主题选项：选中时用强调色淡底，与桌面端一致。 */
@Composable
private fun ThemeOption(text: String, selected: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .clip(SquircleShape(WcRadius.r2))
            .background(if (selected) WcColors.AccentSoft else WcColors.Soft)
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 8.dp),
    ) {
        Text(
            text = text,
            fontSize = 13.sp,
            color = if (selected) WcColors.Accent else WcColors.Text,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
        )
    }
}

/**
 * 通知卡片：显示真实授权状态，并能直接申请。
 *
 * 以前只有一个打开通知设置的跳转——用户点了不知道要开什么，
 * Android 13+ 其实可以直接弹权限框。
 */
@Composable
private fun NotificationCard() {
    val context = LocalContext.current
    var granted by remember {
        mutableStateOf(
            Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
                ContextCompat.checkSelfPermission(
                    context,
                    Manifest.permission.POST_NOTIFICATIONS,
                ) == PackageManager.PERMISSION_GRANTED,
        )
    }

    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { result -> granted = result }

    WcCard {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = Str.t("通知权限"),
                fontSize = 15.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
                modifier = Modifier.weight(1f),
            )
            StatusPill(
                text = if (granted) Str.t("已允许") else Str.t("未允许"),
                tone = if (granted) Tone.Ok else Tone.Idle,
            )
        }

        if (!granted) {
            Spacer(Modifier.height(6.dp))
            Text(
                text = Str.t("服务照常运行，但看不到常驻通知与进度。"),
                fontSize = 12.5.sp,
                color = WcColors.Muted,
            )
        }

        Spacer(Modifier.height(10.dp))

        if (!granted && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            WcPrimaryButton(
                text = Str.t("允许通知"),
                onClick = { launcher.launch(Manifest.permission.POST_NOTIFICATIONS) },
            )
            Spacer(Modifier.height(6.dp))
        }

        WcLinkButton(
            text = Str.t("系统通知设置"),
            onClick = {
                runCatching {
                    val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                        .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                    context.startActivity(intent)
                }.onFailure { error ->
                    android.util.Log.w("WcSettings", "打开通知设置失败", error)
                }
            },
            color = WcColors.Muted,
        )
    }
}
