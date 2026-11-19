package com.wirelesscable.app.ui.screens

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.ui.Alignment
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.wirelesscable.app.core.direct.openAppDetails
import com.wirelesscable.app.core.pairing.QrDecoder
import com.wirelesscable.app.ui.components.CloseIcon
import com.wirelesscable.app.ui.theme.WcColors
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import com.wirelesscable.app.core.i18n.Str

/**
 * 扫码配对。相机预览 + 本地解码，识别到内容即回调（只回调一次）。
 *
 * 无相机权限时给出授权入口；无摄像头设备仍可用 mDNS 发现或手动配对。
 */
@Composable
fun ScanScreen(
    onResult: (String) -> Unit,
    onClose: () -> Unit,
    // 手动配对：地址 / 端口 / 指纹都是上次配对时存下来的，通常只需点一下「配对」
    manualHost: String = "",
    manualPort: String = "47110",
    manualFingerprint: String = "",
    manualBusy: Boolean = false,
    manualMessage: String? = null,
    /** 配对请求已发出、等电脑点允许：这时关掉弹窗，让用户去看电脑。 */
    awaitingApproval: Boolean = false,
    onManualHostChange: (String) -> Unit = {},
    onManualPortChange: (String) -> Unit = {},
    onManualFingerprintChange: (String) -> Unit = {},
    onManualPair: () -> Unit = {},
    /** 当面直连（两台手机扫码互传）：入口收在扫码页，首页不再单开卡片。 */
    onOpenDirect: () -> Unit = {},
) {
    val context = LocalContext.current
    var granted by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                PackageManager.PERMISSION_GRANTED,
        )
    }

    // 被拒过一次之后，系统很可能不再弹框（连续拒绝两次即永久拒绝）：
    // 这时唯一能走的就是应用设置页，必须给出来，否则扫码功能等于废了（1.5.57）。
    var deniedOnce by remember { mutableStateOf(false) }

    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { result ->
        granted = result
        deniedOnce = !result
    }

    var manualOpen by remember { mutableStateOf(false) }

    // 请求发出去后弹窗自动收起：这时用户该看的是电脑上的「允许」
    LaunchedEffect(awaitingApproval) {
        if (awaitingApproval) manualOpen = false
    }

    LaunchedEffect(Unit) {
        if (!granted) {
            launcher.launch(Manifest.permission.CAMERA)
        }
    }

    Box(modifier = Modifier.fillMaxSize().background(Color.Black)) {
        if (granted) {
            CameraPreview(onResult = onResult)
        } else {
            Column(
                modifier = Modifier.align(Alignment.Center),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text(text = Str.t("需要相机权限"), color = Color.White, fontSize = 15.sp)
                Button(onClick = { launcher.launch(Manifest.permission.CAMERA) }) {
                    Text(Str.t("授权"))
                }

                if (deniedOnce) {
                    Text(
                        text = Str.t("系统可能不再弹框，需要到应用设置里打开"),
                        color = Color(0x99FFFFFF),
                        fontSize = 12.sp,
                        textAlign = TextAlign.Center,
                    )
                    TextButton(onClick = { openAppDetails(context) }) {
                        Text(Str.t("打开应用设置"), color = WcColors.Accent, fontSize = 13.sp)
                    }
                }
            }
        }

        // 取景框：四个角括号 + 扫描线（不用整圈方框，四角更容易对准）
        Box(
            modifier = Modifier
                .align(Alignment.Center)
                .size(248.dp),
        ) {
            Canvas(modifier = Modifier.fillMaxSize()) {
                val len = 30.dp.toPx()
                val stroke = 3.dp.toPx()
                val w = size.width
                val h = size.height
                fun corner(x: Float, y: Float, dx: Float, dy: Float) {
                    drawLine(
                        color = WcColors.Accent,
                        start = Offset(x, y),
                        end = Offset(x + dx * len, y),
                        strokeWidth = stroke,
                        cap = StrokeCap.Round,
                    )
                    drawLine(
                        color = WcColors.Accent,
                        start = Offset(x, y),
                        end = Offset(x, y + dy * len),
                        strokeWidth = stroke,
                        cap = StrokeCap.Round,
                    )
                }
                corner(0f, 0f, 1f, 1f)
                corner(w, 0f, -1f, 1f)
                corner(0f, h, 1f, -1f)
                corner(w, h, -1f, -1f)
            }

            // 扫描线：上下往复，周期 1.6 秒
            val transition = rememberInfiniteTransition(label = "scan")
            val progress by transition.animateFloat(
                initialValue = 0f,
                targetValue = 1f,
                animationSpec = infiniteRepeatable(
                    animation = tween(1600, easing = LinearEasing),
                    repeatMode = RepeatMode.Reverse,
                ),
                label = "scanline",
            )
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .offset(y = (progress * 240).dp)
                    .height(2.dp)
                    .background(WcColors.Accent.copy(alpha = 0.85f)),
            )
        }

        // 顶栏：关闭图标 + 标题（相机整屏铺，这一行让开状态栏）
        Row(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .fillMaxWidth()
                .windowInsetsPadding(WindowInsets.statusBars)
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ScanIconButton(icon = CloseIcon, contentDescription = Str.t("关闭"), onClick = onClose)
            Text(
                text = Str.t("扫码配对"),
                color = Color.White,
                fontSize = 17.sp,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.weight(1f),
                textAlign = TextAlign.Center,
            )
            // 与左侧图标等宽，标题才是真正居中
            Spacer(Modifier.size(36.dp))
        }

        // 底部：手动配对 / 当面直连
        Row(
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .navigationBarsPadding()
                .padding(bottom = 40.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            ScanPillButton(text = Str.t("手动配对"), onClick = { manualOpen = true })
            ScanPillButton(text = Str.t("当面直连"), onClick = onOpenDirect)
        }

        if (manualOpen) {
            ManualPairDialog(
                host = manualHost,
                port = manualPort,
                fingerprint = manualFingerprint,
                busy = manualBusy,
                message = manualMessage,
                onHost = onManualHostChange,
                onPort = onManualPortChange,
                onFingerprint = onManualFingerprintChange,
                onDismiss = { manualOpen = false },
                onPair = onManualPair,
            )
        }

    }
}

@Composable
private fun CameraPreview(onResult: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val previewView = remember { PreviewView(context) }
    val decoder = remember { QrDecoder() }
    val handled = remember { AtomicBoolean(false) }
    val executor = remember { Executors.newSingleThreadExecutor() }

    DisposableEffect(Unit) {
        val providerFuture = ProcessCameraProvider.getInstance(context)

        providerFuture.addListener(
            {
                val provider = providerFuture.get()

                val preview = Preview.Builder().build().apply {
                    setSurfaceProvider(previewView.surfaceProvider)
                }

                val analysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()

                analysis.setAnalyzer(executor) { image ->
                    try {
                        if (!handled.get()) {
                            val text = decoder.decode(image)
                            if (text != null && handled.compareAndSet(false, true)) {
                                previewView.post { onResult(text) }
                            }
                        }
                    } finally {
                        image.close()
                    }
                }

                try {
                    provider.unbindAll()
                    provider.bindToLifecycle(
                        lifecycleOwner,
                        CameraSelector.DEFAULT_BACK_CAMERA,
                        preview,
                        analysis,
                    )
                } catch (e: Exception) {
                    // 无后置相机或被占用：界面保持黑屏 + 关闭按钮，用户可退回其它配对方式
                }
            },
            ContextCompat.getMainExecutor(context),
        )

        onDispose {
            runCatching {
                ProcessCameraProvider.getInstance(context).get().unbindAll()
            }
            executor.shutdown()
        }
    }

    AndroidView(factory = { previewView }, modifier = Modifier.fillMaxSize())
}

/**
 * 手动配对。
 *
 * 二维码只是一个把地址+端口+指纹一次带过去的信封；这三样东西我们本来就存着，
 * 所以没摄像头、不方便扫码、或者令牌丢了要重配时，直接填/确认这三项走同一套配对流程。
 */
@Composable
private fun ManualPairDialog(
    host: String,
    port: String,
    fingerprint: String,
    busy: Boolean,
    message: String?,
    onHost: (String) -> Unit,
    onPort: (String) -> Unit,
    onFingerprint: (String) -> Unit,
    onDismiss: () -> Unit,
    onPair: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(Str.t("手动配对")) },
        text = {
            Column {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(
                        value = host,
                        onValueChange = onHost,
                        label = { Text(Str.t("地址")) },
                        singleLine = true,
                        enabled = !busy,
                        modifier = Modifier.weight(1f),
                    )
                    OutlinedTextField(
                        value = port,
                        onValueChange = onPort,
                        label = { Text(Str.t("端口")) },
                        singleLine = true,
                        enabled = !busy,
                        modifier = Modifier.width(104.dp),
                    )
                }

                Spacer(Modifier.size(8.dp))

                OutlinedTextField(
                    value = fingerprint,
                    onValueChange = onFingerprint,
                    label = { Text(Str.t("证书指纹")) },
                    singleLine = true,
                    enabled = !busy,
                    textStyle = androidx.compose.ui.text.TextStyle(fontSize = 11.5.sp),
                )

                message?.let {
                    Spacer(Modifier.size(6.dp))
                    Text(text = it, color = WcColors.Error, fontSize = 12.sp)
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onPair, enabled = !busy) {
                Text(if (busy) Str.t("配对中…") else Str.t("配对"))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(Str.t("取消")) }
        },
    )
}

/** 深色相机上的胶囊按钮。 */
@Composable
private fun ScanPillButton(text: String, onClick: () -> Unit) {
    Text(
        text = text,
        color = Color.White,
        fontSize = 13.5.sp,
        fontWeight = FontWeight.Medium,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(Color(0x66000000))
            .clickable(onClick = onClick)
            .padding(horizontal = 18.dp, vertical = 10.dp),
    )
}

/** 深色相机上的方形图标按钮。 */
@Composable
private fun ScanIconButton(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    contentDescription: String,
    onClick: () -> Unit,
) {
    Box(
        modifier = Modifier
            .size(36.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(Color(0x66000000))
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = contentDescription,
            tint = Color.White,
            modifier = Modifier.size(18.dp),
        )
    }
}
