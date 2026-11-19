package com.wirelesscable.app.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.ui.theme.SquircleShape
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcRadius
import com.wirelesscable.app.ui.theme.WcSpace
import com.wirelesscable.app.ui.theme.WcType
import com.wirelesscable.app.core.i18n.Str

/**
 * 列表多选的公共件：勾选框 + 底部操作条。
 *
 * 传输中心与传输目录现在是**同一套交互**：
 * 短按 = 打开，长按 = 进入多选，多选态下短按 = 勾选/取消；底部条放批量动作。
 * 两页各写一份的话，改一处必忘另一处（之前勾选框就有三份私有实现）。
 */

/** 自绘勾选框（18dp）：Material 的 Checkbox 带 48dp 最小点击区，会把行撑高。 */
@Composable
fun WcSelectionMark(checked: Boolean) {
    val shape = SquircleShape(WcRadius.r1)

    Box(
        modifier = Modifier
            .size(18.dp)
            .clip(shape)
            .background(if (checked) WcColors.Accent else androidx.compose.ui.graphics.Color.Transparent)
            .border(1.5.dp, if (checked) WcColors.Accent else WcColors.Line2, shape),
        contentAlignment = Alignment.Center,
    ) {
        if (checked) {
            Text(text = "✓", fontSize = 11.sp, color = WcColors.OnAccent)
        }
    }
}

/**
 * 多选时的底部操作条：左边状态文字，右边动作，「完成」永远在最右。
 *
 * @param actions 由调用点自己排（打开 / 删除 / 取消任务…），这里不预设，
 *                免得出现这个页面有、那个页面没有的默认值。
 */
@Composable
fun WcSelectionBar(
    text: String,
    onDone: () -> Unit,
    actions: @Composable RowScope.() -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(WcColors.Card)
            .padding(horizontal = WcSpace.s3, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = text,
            fontSize = WcType.labelSize,
            color = WcColors.Text2,
            modifier = Modifier.weight(1f),
        )
        actions()
        WcLinkButton(text = Str.t("完成"), onClick = onDone, color = WcColors.Muted)
    }
}
