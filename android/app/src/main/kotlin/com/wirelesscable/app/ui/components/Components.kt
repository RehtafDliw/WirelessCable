package com.wirelesscable.app.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSize

/** 状态色调。 */
enum class Tone { Ok, Idle, Error }

/** 分组标题：15sp 半粗。 */
@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        modifier = modifier.padding(bottom = 8.dp),
        fontSize = 15.sp,
        fontWeight = FontWeight.SemiBold,
        color = WcColors.Text,
    )
}

/** 状态胶囊。底色用 soft 令牌，文字用对应深色，深色主题下自动反转。 */
@Composable
fun StatusPill(text: String, tone: Tone) {
    val background = when (tone) {
        Tone.Ok -> WcColors.SuccessPill
        Tone.Idle -> WcColors.IdlePill
        Tone.Error -> WcColors.ErrorPill
    }
    val foreground = when (tone) {
        Tone.Ok -> WcColors.Success
        Tone.Idle -> WcColors.Muted
        Tone.Error -> WcColors.Error
    }

    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(background)
            .padding(horizontal = 10.dp, vertical = 3.dp),
    ) {
        Text(text = text, color = foreground, fontSize = 12.5.sp)
    }
}

@Composable
fun WcCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    val shape = SquircleShape(WcRadius.r3)
    Column(
        modifier = modifier
            .fillMaxWidth()
            .clip(shape)
            .background(WcColors.Card)
            .border(WcSize.Hairline, WcColors.Line, shape)
            .padding(WcSize.CardPad),
        content = content,
    )
}

/** 未完成功能的标记，避免占位页被误认为已实现。 */
@Composable
fun WipChip() {
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(6.dp))
            .background(WcColors.Wip)
            .padding(horizontal = 8.dp, vertical = 2.dp),
    ) {
        Text(text = "开发中", color = WcColors.WipText, fontSize = 12.sp)
    }
}

/** 菱形布局里的功能入口。 */
@Composable
fun ActionTile(
    glyph: String,
    title: String,
    subtitle: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    /** 不可用时整块变淡且点不动：宁可不响应，也不要让用户挑完文件才发现没连上。 */
    enabled: Boolean = true,
) {
    Column(
        modifier = modifier
            .clip(RoundedCornerShape(10.dp))
            .background(WcColors.Tile)
            .alpha(if (enabled) 1f else 0.45f)
            .clickable(enabled = enabled, onClick = onClick)
            .padding(16.dp),
        verticalArrangement = Arrangement.Top,
    ) {
        Text(text = glyph, color = WcColors.Accent, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(8.dp))
        Text(text = title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = WcColors.Text)
        Spacer(Modifier.height(2.dp))
        Text(text = subtitle, fontSize = 12.5.sp, color = WcColors.Muted)
    }
}

/** 键值行。 */
@Composable
fun DetailRow(label: String, value: String, trailing: @Composable () -> Unit = {}) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text = label, color = WcColors.Muted, fontSize = 12.5.sp, modifier = Modifier.padding(end = 12.dp))
        Text(text = value, color = WcColors.Text, fontSize = 13.5.sp, modifier = Modifier.weight(1f))
        trailing()
    }
}

/** 主操作按钮。禁用时降低不透明度，不做"点了没反应"的沉默失败。 */
@Composable
fun WcPrimaryButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    /** 次要动作用软底：同一屏里两个主色按钮会互相抢注意力。 */
    soft: Boolean = false,
) {
    Box(
        modifier = modifier
            .height(44.dp)
            .clip(SquircleShape(WcRadius.r2))
            .background(
                when {
                    !enabled -> WcColors.Soft
                    soft -> WcColors.Soft
                    else -> WcColors.Accent
                },
            )
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 18.dp),
        contentAlignment = androidx.compose.ui.Alignment.Center,
    ) {
        Text(
            text = text,
            color = when {
                !enabled -> WcColors.Muted
                soft -> WcColors.Text2
                else -> WcColors.OnAccent
            },
            fontSize = 14.sp,
            fontWeight = FontWeight.SemiBold,
        )
    }
}

/** 次级文字操作（取消、保存等）。 */
@Composable
fun WcLinkButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    color: Color = WcColors.Accent,
) {
    Text(
        text = text,
        color = color,
        fontSize = 13.sp,
        fontWeight = FontWeight.Medium,
        modifier = modifier
            .clip(RoundedCornerShape(6.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 8.dp, vertical = 4.dp),
    )
}

/** 细进度条。用真实比例填充，不带动画插值，避免进度看起来比实际更快。 */
@Composable
fun WcProgressBar(fraction: Float, color: Color, modifier: Modifier = Modifier) {
    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(6.dp)
            .clip(RoundedCornerShape(3.dp))
            .background(WcColors.Border),
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth(fraction.coerceIn(0f, 1f))
                .height(6.dp)
                .clip(RoundedCornerShape(3.dp))
                .background(color),
        )
    }
}
