package com.wirelesscable.app.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.wirelesscable.app.ui.components.WcSegmented
import com.wirelesscable.app.ui.theme.WcColors
import com.wirelesscable.app.ui.theme.WcSpace

/** 文件管理页的两个来源。 */
enum class FileSource(val label: String) {
    Computer("电脑文件"),
    Phone("传输目录"),
}

/**
 * 文件管理页：切换「电脑文件」与「传输目录」。
 *
 * 传输目录 = 电脑发到手机的文件（只读收件箱），不是手机文件管理器。
 *
 * 两个来源的数据通道完全不同（服务端 /fs vs 本机 File），
 * 所以只共用这一个外壳，内容各自由自己的组合函数渲染。
 *
 * 用 slot 而不是把两套参数都塞进来：这个页面已经有两组十几个回调，
 * 合并成一个函数签名只会让调用点更难读。
 */
@Composable
fun FilesHubScreen(
    source: FileSource,
    onSourceChange: (FileSource) -> Unit,
    computer: @Composable () -> Unit,
    phone: @Composable () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier = modifier.fillMaxSize()) {
        WcSegmented(
            options = listOf(FileSource.Computer.label, FileSource.Phone.label),
            index = if (source == FileSource.Computer) 0 else 1,
            onSelect = { onSourceChange(if (it == 0) FileSource.Computer else FileSource.Phone) },
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = WcSpace.s4, vertical = WcSpace.s2),
        )

        Box(modifier = Modifier.weight(1f)) {
            when (source) {
                FileSource.Computer -> computer()
                FileSource.Phone -> phone()
            }
        }
    }
}
