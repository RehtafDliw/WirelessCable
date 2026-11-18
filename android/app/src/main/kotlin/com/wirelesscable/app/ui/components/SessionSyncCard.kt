package com.wirelesscable.app.ui.components

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.ui.Alignment
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.foundation.layout.height
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.wirelesscable.app.core.sync.BackgroundSync
import com.wirelesscable.app.core.sync.BatteryOptimization
import com.wirelesscable.app.core.sync.SessionSyncEngine
import com.wirelesscable.app.ui.components.WcDialog
import com.wirelesscable.app.ui.theme.WcColors

/**
 * 「后台保持连接」开关。
 *
 * 开启后息屏、切后台都能继续执行电脑发来的文件操作（K43）；
 * 代价是一条常驻通知——所以让用户能看见、能关掉。
 *
 * 状态一律读 [BackgroundSync] / [SessionSyncEngine]，界面不另存一份。
 *
 * 另外还负责一件事：**电池优化白名单**。只开服务不加白名单的话，
 * 息屏后系统会冻结应用，服务还在、通知还在，但循环停了——电脑端表现为
 * "手机在线却列不出文件"。所以开启时提示一次，卡片上常驻一条状态。
 */
@Composable
fun SessionSyncCard(
    paired: Boolean,
    allFilesGranted: Boolean,
    onOpenAllFilesSettings: () -> Unit,
) {
    val context = LocalContext.current
    val snapshot by SessionSyncEngine.state.collectAsStateWithLifecycle()

    // 跟随可观察开关：通知上点「关闭」也会改它，界面不会留在"已开启"
    val flag by BackgroundSync.enabled.collectAsStateWithLifecycle()
    val enabled = flag ?: BackgroundSync.isEnabled(context)
    var pendingStart by remember { mutableStateOf(false) }

    // 从系统设置返回后要重新判断，否则用户明明放行了，卡片还写着"可能被限制"
    var batteryAllowed by remember { mutableStateOf(BatteryOptimization.isIgnoring(context)) }
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                batteryAllowed = BatteryOptimization.isIgnoring(context)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    var showBatteryPrompt by remember { mutableStateOf(false) }

    /** 耗电管理入口的下一步提示——点了必须有反应，且要说实话落在哪一层。 */
    var powerHint by remember { mutableStateOf<String?>(null) }

    /** 提示是"走错了"还是"还要再点一步"：前者用错误色，后者用次要色。 */
    var powerHintError by remember { mutableStateOf(false) }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) {
        // 拒绝通知权限也照常开启：服务能用，只是通知不可见（下面会提示）
        if (pendingStart) {
            pendingStart = false
            BackgroundSync.setEnabled(context, true)
            if (!BatteryOptimization.isIgnoring(context)) {
                showBatteryPrompt = true
            }
        }
    }

    fun requestStart() {
        val needPermission = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED

        if (needPermission) {
            pendingStart = true
            permissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        } else {
            BackgroundSync.setEnabled(context, true)
            if (!BatteryOptimization.isIgnoring(context)) {
                showBatteryPrompt = true
            }
        }
    }

    WcCard {
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = "后台服务",
                fontSize = 15.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text,
                modifier = Modifier.weight(1f),
            )
            StatusPill(
                text = if (enabled) "已开启" else "已关闭",
                tone = if (enabled) Tone.Ok else Tone.Idle,
            )
        }
        Spacer(Modifier.height(6.dp))

        if (enabled) {
            DetailRow(
                "连接",
                when {
                    snapshot.lastError != null -> snapshot.lastError!!
                    snapshot.connected -> "已连接"
                    else -> "连接中"
                },
            )

            DetailRow("后台运行", if (batteryAllowed) "已允许" else "可能被系统限制")

            /*
             * 系统白名单 ≠ 厂商的「不限制」。
             *
             * 真机（一加 / ColorOS 13）实测：白名单已加、前台服务和两把锁都在，
             * 息屏一分钟后循环照样停；把「耗电管理」设成"不限制"之后，全程息屏收
             * 1 GB 也没问题（K89）。这层没有 API，读不到当前值，只能把入口摆在
             * 状态旁边让用户自己确认。
             */
            Spacer(Modifier.height(6.dp))
            WcLinkButton(
                text = "耗电管理设置",
                onClick = {
                    // 按真实落点给下一步：ColorOS 只给到应用信息页，
                    // 说"已打开耗电管理"就是骗人
                    powerHintError = false
                    powerHint = when (BatteryOptimization.openPowerSettings(context)) {
                        BatteryOptimization.PowerDestination.Vendor -> null
                        BatteryOptimization.PowerDestination.AppDetails ->
                            "在打开的页面里点「耗电管理」，把后台限制设为「不限制」"
                        BatteryOptimization.PowerDestination.SystemList ->
                            "在列表里找到本应用，把电池优化设为「不优化」"
                        BatteryOptimization.PowerDestination.None -> {
                            powerHintError = true
                            "打不开系统设置：请到「设置 → 电池 → 应用耗电管理」里找到本应用"
                        }
                    }
                },
                color = WcColors.Accent,
            )

            // 整机权限决定了电脑能不能看到"内部存储（整机）"。
            // 用户以为开了、实际没开时，这一行是唯一能自证的地方。
            DetailRow(
                "整机权限",
                if (allFilesGranted) "已授予" else "未授予（只能看手选目录，也收不到文件）",
            )

            // 被电脑断开时给一个"现在再试一次"：
            // 默认要等 30 秒退避，用户刚在电脑上点了「连接」时会以为没生效
            if (snapshot.paused) {
                Spacer(Modifier.height(6.dp))
                WcLinkButton(
                    text = "立即重连",
                    onClick = { SessionSyncEngine.retryNow() },
                    color = WcColors.Accent,
                )
            }
        }

        Spacer(Modifier.height(6.dp))
        Text(
            text = if (enabled) {
                // 不要承诺"息屏也实时"（默认状态下 ROM 会冻结循环），
                // 但也要说清怎么才能做到——"不限制"是唯一开关。
                "息屏接收：耗电管理设为「不限制」后实时收，否则解锁后补收。"
            } else {
                "关闭后电脑看不到手机。"
            },
            fontSize = 12.5.sp,
            color = WcColors.Muted,
        )

        if (powerHint != null) {
            Spacer(Modifier.height(6.dp))
            Text(
                text = powerHint!!,
                fontSize = 12.5.sp,
                color = if (powerHintError) WcColors.Error else WcColors.Muted,
            )
        }

        if (!paired) {
            Spacer(Modifier.height(6.dp))
            Text(
                text = "需要先与电脑配对，才能开启。",
                fontSize = 12.5.sp,
                color = WcColors.Muted,
            )
        }

        // 只开服务不加白名单，息屏后等于没开。这条必须一直看得见，不能只在开启时提一次。
        if (!allFilesGranted) {
            Spacer(Modifier.height(6.dp))
            WcLinkButton(
                text = "开启整机访问",
                onClick = onOpenAllFilesSettings,
                color = WcColors.Accent,
            )
        }

        if (enabled && !batteryAllowed) {
            Spacer(Modifier.height(8.dp))
            Text(
                text = "系统会冻结后台应用：不加入电池白名单，息屏后电脑可能看不到手机文件。",
                fontSize = 12.5.sp,
                color = WcColors.Error,
            )
            Spacer(Modifier.height(6.dp))
            WcLinkButton(
                text = "允许后台运行",
                onClick = { BatteryOptimization.request(context) },
                color = WcColors.Accent,
            )
        }

        if (enabled && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val granted = ContextCompat.checkSelfPermission(
                context,
                Manifest.permission.POST_NOTIFICATIONS,
            ) == PackageManager.PERMISSION_GRANTED

            if (!granted) {
                Spacer(Modifier.height(6.dp))
                Text(
                    text = "通知权限未授予：仍在后台运行，但看不到状态通知。",
                    fontSize = 12.5.sp,
                    color = WcColors.Muted,
                )
            }
        }

        Spacer(Modifier.height(12.dp))

        if (enabled) {
            Spacer(Modifier.height(4.dp))
            WcLinkButton(
                text = "立即上报目录",
                onClick = { SessionSyncEngine.requestIndexPublish() },
                color = WcColors.Muted,
            )

            WcPrimaryButton(
                text = "关闭",
                onClick = { BackgroundSync.setEnabled(context, false) },
                enabled = true,
                soft = true,
            )
        } else {
            WcPrimaryButton(
                text = "开启",
                onClick = { requestStart() },
                enabled = paired,
            )
        }
    }

    if (showBatteryPrompt) {
        WcDialog(
            onDismissRequest = { showBatteryPrompt = false },
            title = { Text("允许后台运行") },
            text = {
                Text("息屏后系统会冻结后台应用，电脑就看不到手机文件了。\n请在接下来的系统设置里允许本应用在后台运行。")
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        showBatteryPrompt = false
                        BatteryOptimization.request(context)
                    },
                ) { Text("去设置") }
            },
            dismissButton = {
                TextButton(onClick = { showBatteryPrompt = false }) { Text("稍后") }
            },
        )
    }
}
