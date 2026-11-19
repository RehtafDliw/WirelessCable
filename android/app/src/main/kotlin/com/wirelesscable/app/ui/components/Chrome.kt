package com.wirelesscable.app.ui.components

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcMotion
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSize
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.core.i18n.Str

/**
 * 「关机」图标：一个上端开口的圆弧 + 一根竖线。
 *
 * material-icons-core 里没有 PowerSettingsNew（在 extended 包里），
 * 为一个图标引入整个 extended 不划算，这里按同一条路径手绘。
 */
val PowerIcon: ImageVector = ImageVector.Builder(
    name = "Power",
    defaultWidth = 24.dp,
    defaultHeight = 24.dp,
    viewportWidth = 24f,
    viewportHeight = 24f,
).apply {
    path(
        stroke = SolidColor(Color.Black),
        strokeLineWidth = 2f,
        strokeLineCap = StrokeCap.Round,
    ) {
        // 竖线：从圆心上方穿出圆外
        moveTo(12f, 3.2f)
        lineTo(12f, 10.8f)
        // 圆弧：10 点钟方向起，经底部绕到 2 点钟方向（留顶部缺口）
        moveTo(7.63f, 7.3f)
        curveTo(6.4f, 8.4f, 5.2f, 10.2f, 5.2f, 12.5f)
        curveTo(5.2f, 16.26f, 8.24f, 19.3f, 12f, 19.3f)
        curveTo(15.76f, 19.3f, 18.8f, 16.26f, 18.8f, 12.5f)
        curveTo(18.8f, 10.2f, 17.6f, 8.4f, 16.37f, 7.3f)
    }
}.build()

/**
 * 页面顶栏：大标题 + 右侧动作。
 *
 * 不画深色条——标题直接落在页面底色上（样张口径）。
 * 系统栏内边距由 Scaffold 统一给，这里不再叠加，否则会多出一截空白。
 */
@Composable
fun WcTopBar(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    actions: @Composable RowScope.() -> Unit = {},
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            // 顶栏由 Scaffold 的 topBar 槽承载，Scaffold 只按它的高度给内容留位，
            // 不会替它让开状态栏 —— 少了这一行，标题就会压到系统时间上（Samsung 实测）
            .windowInsetsPadding(WindowInsets.statusBars)
            .padding(start = WcSpace.s5, end = WcSpace.s4, top = WcSpace.s2, bottom = WcSpace.s3),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title,
                fontSize = WcType.displaySize,
                lineHeight = WcType.displayLine,
                fontWeight = WcType.displayWeight,
                letterSpacing = WcType.displayLetterSpacing,
                color = WcColors.Text,
            )
            if (subtitle != null) {
                Text(
                    text = subtitle,
                    fontSize = WcType.labelSize,
                    fontWeight = WcType.labelWeight,
                    color = WcColors.Muted,
                )
            }
        }
        Row(
            horizontalArrangement = Arrangement.spacedBy(WcSpace.s2),
            verticalAlignment = Alignment.CenterVertically,
            content = actions,
        )
    }
}

/**
 * 方形图标按钮（顶栏动作用）。
 *
 * [danger] 用于「退出应用」：红底浅红、图标红。不做成主色按钮——
 * 退出是个不常用且不可撤销的动作，不该和「发送文件」抢注意力。
 */
@Composable
fun WcIconAction(
    icon: ImageVector,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    contentDescription: String? = null,
    danger: Boolean = false,
) {
    val shape = SquircleShape(WcRadius.r2)
    Box(
        modifier = modifier
            .size(34.dp)
            .clip(shape)
            .background(if (danger) WcColors.DangerSoft else WcColors.Card)
            .border(WcSize.Hairline, if (danger) WcColors.DangerSoft else WcColors.Line, shape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = contentDescription,
            tint = if (danger) WcColors.Error else WcColors.Text2,
            modifier = Modifier.size(WcSize.IconSm),
        )
    }
}

/** 分组标题：15sp 半粗 + 右侧可选提示（可点）。 */
@Composable
fun WcSectionHeader(
    title: String,
    modifier: Modifier = Modifier,
    hint: String? = null,
    onHintClick: (() -> Unit)? = null,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .padding(top = WcSpace.s4 + WcSpace.s1, bottom = WcSpace.s2),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = title,
            fontSize = WcType.h3Size,
            lineHeight = WcType.h3Line,
            fontWeight = WcType.h3Weight,
            color = WcColors.Text,
        )
        if (hint != null) {
            Spacer(Modifier.weight(1f))
            Text(
                text = hint,
                fontSize = WcType.labelSize,
                fontWeight = WcType.labelWeight,
                color = WcColors.Muted,
                modifier = Modifier
                    .padding(start = WcSpace.s2)
                    .clip(SquircleShape(WcRadius.r1))
                    .then(if (onHintClick != null) Modifier.clickable(onClick = onHintClick) else Modifier)
                    .padding(horizontal = WcSpace.s1, vertical = 2.dp),
            )
        }
    }
}

/** 卡片背景的列表容器：整块圆角 + 发丝描边，行间自己画分隔线。 */
@Composable
fun WcCardList(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val shape = SquircleShape(WcRadius.r3)
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(shape)
            .background(WcColors.Card)
            .border(WcSize.Hairline, WcColors.Line, shape),
        content = content,
    )
}

/** 列表分隔线：贯通整行。 */
@Composable
fun WcRowDivider() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(WcSize.Hairline)
            .background(WcColors.Line),
    )
}

/** 分段控件：等宽几段，选中段用卡片色托起来。 */
@Composable
fun WcSegmented(
    options: List<String>,
    index: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    val outer = SquircleShape(WcRadius.r2)
    Row(
        modifier = modifier
            .height(36.dp)
            .clip(outer)
            .background(WcColors.Well)
            .border(WcSize.Hairline, WcColors.Line, outer)
            .padding(3.dp),
    ) {
        options.forEachIndexed { i, label ->
            val active = i == index
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxHeight()
                    .clip(SquircleShape(WcRadius.r1))
                    .background(if (active) WcColors.Card else Color.Transparent)
                    .clickable { onSelect(i) },
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    text = label,
                    fontSize = WcType.labelSize,
                    fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                    color = if (active) WcColors.Text else WcColors.Muted,
                )
            }
        }
    }
}

/**
 * 环形进度。
 *
 * 数值就是真实进度（`progress` 由字节数算出来），不做动画插值 ——
 * 进度条看起来比实际快，是上一版被投诉过的点。
 */
@Composable
fun WcRing(
    progress: Float,
    modifier: Modifier = Modifier,
    diameter: Dp = 34.dp,
    color: Color = WcColors.Accent,
    label: String? = null,
) {
    Box(modifier = modifier.size(diameter), contentAlignment = Alignment.Center) {
        Canvas(modifier = Modifier.fillMaxSize()) {
            val stroke = size.minDimension * 0.11f
            val inset = stroke / 2f
            val arcSize = Size(size.width - stroke, size.height - stroke)
            drawArc(
                color = WcColors.Well2,
                startAngle = -90f,
                sweepAngle = 360f,
                useCenter = false,
                topLeft = Offset(inset, inset),
                size = arcSize,
                style = Stroke(width = stroke),
            )
            drawArc(
                color = color,
                startAngle = -90f,
                sweepAngle = 360f * progress.coerceIn(0f, 1f),
                useCenter = false,
                topLeft = Offset(inset, inset),
                size = arcSize,
                style = Stroke(width = stroke, cap = androidx.compose.ui.graphics.StrokeCap.Round),
            )
        }
        if (label != null) {
            Text(
                text = label,
                fontSize = 9.sp,
                fontWeight = FontWeight.SemiBold,
                color = WcColors.Text2,
            )
        }
    }
}

/** 行内动作按钮（暂停/继续/重试），30dp，与行高 56 配合。 */
@Composable
fun WcRowAction(
    icon: ImageVector,
    onClick: () -> Unit,
    contentDescription: String? = null,
    danger: Boolean = false,
) {
    Box(
        modifier = Modifier
            .size(30.dp)
            .clip(SquircleShape(WcRadius.r1))
            .background(WcColors.Soft)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            imageVector = icon,
            contentDescription = contentDescription,
            tint = if (danger) WcColors.Error else WcColors.Text2,
            modifier = Modifier.size(14.dp),
        )
    }
}

/**
 * 入场：淡入 + 8dp 上移，按 [index] 错峰 40ms。
 *
 * 只在首次组合时跑一次，之后重组不再动 —— 列表刷新时整屏卡片反复飘入会让人分神。
 * 时长用 [WcMotion.DurSlow]（240ms）：比页面切换略快，不拖住可读性。
 */
@Composable
fun WcStagger(index: Int = 0, content: @Composable () -> Unit) {
    val progress = remember { Animatable(0f) }
    LaunchedEffect(Unit) {
        delay(index * 40L)
        progress.animateTo(1f, animationSpec = tween(WcMotion.DurSlow, easing = FastOutSlowInEasing))
    }
    // 用 Column 而不是 Box：一个入场块里通常有多段内容（标题 + 卡片 + 提示），
    // Box 会把它们全部叠在左上角 —— 真机上表现为我的设备被息屏提示盖住。
    Column(
        modifier = Modifier.graphicsLayer {
            alpha = progress.value
            translationY = (1f - progress.value) * 8.dp.toPx()
        },
    ) {
        content()
    }
}

/**
 * 通用对话框。
 *
 * 参数名与 Material `AlertDialog` 对齐，调用点只换函数名即可。
 * 壳子按令牌来：超椭圆、发丝描边、20dp 内边距、按钮右对齐。
 * 危险动作的按钮由调用点自己给颜色，这里不提供主按钮默认值——
 * 一个默认蓝色「删除」比没有按钮更糟。
 */
@Composable
fun WcDialog(
    onDismissRequest: () -> Unit,
    confirmButton: @Composable () -> Unit,
    modifier: Modifier = Modifier,
    title: @Composable () -> Unit = {},
    text: @Composable () -> Unit = {},
    dismissButton: (@Composable () -> Unit)? = null,
) {
    Dialog(onDismissRequest = onDismissRequest) {
        val shape = SquircleShape(WcRadius.r4)
        Column(
            modifier = modifier
                .fillMaxWidth()
                .clip(shape)
                .background(WcColors.Card)
                .border(WcSize.Hairline, WcColors.Line, shape)
                .padding(WcSpace.s5),
        ) {
            title()
            Spacer(Modifier.height(WcSpace.s2))
            text()
            Spacer(Modifier.height(WcSpace.s5 - WcSpace.s1))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                dismissButton?.invoke()
                Spacer(Modifier.width(WcSpace.s2))
                confirmButton()
            }
        }
    }
}

/**
 * 文本输入。
 *
 * 不用 Material 的 OutlinedTextField：它的描边、内边距、聚焦色都是 Material 的，
 * 一放进令牌化的卡片里就格格不入。这里只用 BasicTextField，壳子自己画。
 */
@Composable
fun WcInput(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    placeholder: String? = null,
    singleLine: Boolean = true,
) {
    val shape = SquircleShape(WcRadius.r2)
    Column(modifier = modifier) {
        Text(
            text = label,
            fontSize = WcType.captionSize,
            color = WcColors.Muted,
            modifier = Modifier.padding(start = WcSpace.s1, bottom = WcSpace.s1),
        )
        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            enabled = enabled,
            singleLine = singleLine,
            textStyle = WcType.Body.copy(color = WcColors.Text),
            cursorBrush = SolidColor(WcColors.Accent),
            modifier = Modifier
                .fillMaxWidth()
                .clip(shape)
                .background(if (enabled) WcColors.Well else WcColors.Soft)
                .border(WcSize.Hairline, WcColors.Line, shape)
                .padding(horizontal = WcSpace.s3, vertical = 11.dp),
            decorationBox = { inner ->
                Box {
                    if (value.isEmpty() && placeholder != null) {
                        Text(
                            text = placeholder,
                            fontSize = WcType.bodySize,
                            color = WcColors.Muted,
                        )
                    }
                    inner()
                }
            },
        )
    }
}
