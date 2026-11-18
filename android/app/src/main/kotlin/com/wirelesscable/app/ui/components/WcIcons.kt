package com.wirelesscable.app.ui.components

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathBuilder
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp

/**
 * 自绘线性图标集。
 *
 * 为什么不用 material-icons-extended：多一个 APK 依赖换几十个用不到的图标，
 * 而这些形状都是几条线的事。描边风格与样张一致（1.7px、圆头圆角），
 * 颜色交给 `Icon(tint = ...)`，这里的 Black 只是占位。
 */
private fun strokeIcon(
    name: String,
    strokeWidth: Float = 1.7f,
    block: PathBuilder.() -> Unit,
): ImageVector = ImageVector.Builder(
    name = name,
    defaultWidth = 24.dp,
    defaultHeight = 24.dp,
    viewportWidth = 24f,
    viewportHeight = 24f,
).apply {
    path(
        stroke = SolidColor(Color.Black),
        strokeLineWidth = strokeWidth,
        strokeLineCap = StrokeCap.Round,
        strokeLineJoin = StrokeJoin.Round,
    ) { block() }
}.build()

/** 电脑：显示器 + 底座。 */
val MonitorIcon: ImageVector = strokeIcon("Monitor") {
    moveTo(3.5f, 5.5f); lineTo(20.5f, 5.5f); lineTo(20.5f, 16f); lineTo(3.5f, 16f); close()
    moveTo(9f, 20f); lineTo(15f, 20f)
    moveTo(12f, 16f); lineTo(12f, 20f)
}

/** 二维码：三个定位角 + 两个点。 */
val QrIcon: ImageVector = strokeIcon("Qr") {
    moveTo(4f, 4f); lineTo(10f, 4f); lineTo(10f, 10f); lineTo(4f, 10f); close()
    moveTo(14f, 4f); lineTo(20f, 4f); lineTo(20f, 10f); lineTo(14f, 10f); close()
    moveTo(4f, 14f); lineTo(10f, 14f); lineTo(10f, 20f); lineTo(4f, 20f); close()
    moveTo(14f, 14f); lineTo(16.6f, 14f); lineTo(16.6f, 16.6f); lineTo(14f, 16.6f); close()
    moveTo(17.6f, 17.6f); lineTo(20f, 17.6f); lineTo(20f, 20f); lineTo(17.6f, 20f); close()
}

/** 上传：箭头向上 + 底线。 */
val UploadIcon: ImageVector = strokeIcon("Upload") {
    moveTo(12f, 20f); lineTo(12f, 4f)
    moveTo(8f, 8f); lineTo(12f, 4f); lineTo(16f, 8f)
    moveTo(4f, 20.5f); lineTo(20f, 20.5f)
}

/** 文件夹（闭合）。 */
val FolderIcon: ImageVector = strokeIcon("Folder") {
    moveTo(3.5f, 8.5f)
    lineTo(3.5f, 19f)
    lineTo(20.5f, 19f)
    lineTo(20.5f, 10f)
    lineTo(11.5f, 10f)
    lineTo(9.5f, 8f)
    lineTo(4.5f, 8f)
    close()
}

/** 文件夹（打开）：用于「打开已保存」。 */
val FolderOpenIcon: ImageVector = strokeIcon("FolderOpen") {
    moveTo(3.5f, 8.5f)
    lineTo(3.5f, 19f)
    lineTo(19f, 19f)
    lineTo(20.5f, 11f)
    lineTo(6f, 11f)
    close()
    moveTo(3.5f, 8.5f)
    lineTo(4.5f, 6f)
    lineTo(9.5f, 6f)
    lineTo(11.5f, 8.5f)
}

/** 普通文件：折角。 */
val FileIcon: ImageVector = strokeIcon("File") {
    moveTo(6.5f, 3.5f)
    lineTo(13.5f, 3.5f)
    lineTo(17.5f, 7.5f)
    lineTo(17.5f, 20.5f)
    lineTo(6.5f, 20.5f)
    close()
    moveTo(13.5f, 3.5f); lineTo(13.5f, 7.5f); lineTo(17.5f, 7.5f)
}

/** 图片：框 + 内景。 */
val ImageIcon: ImageVector = strokeIcon("Image") {
    moveTo(4f, 5.5f); lineTo(20f, 5.5f); lineTo(20f, 18.5f); lineTo(4f, 18.5f); close()
    moveTo(4f, 15f); lineTo(8.5f, 10.5f); lineTo(12.5f, 14.5f); lineTo(15.5f, 12f); lineTo(20f, 16f)
    moveTo(9f, 9.6f); lineTo(9.01f, 9.6f)
}

/** 暂停。 */
val PauseIcon: ImageVector = strokeIcon("Pause") {
    moveTo(9f, 5.5f); lineTo(9f, 18.5f)
    moveTo(15f, 5.5f); lineTo(15f, 18.5f)
}

/** 继续。 */
val PlayIcon: ImageVector = strokeIcon("Play") {
    moveTo(8f, 5f); lineTo(19f, 12f); lineTo(8f, 19f); close()
}

/** 删除记录。 */
val TrashIcon: ImageVector = strokeIcon("Trash") {
    moveTo(4.5f, 7f); lineTo(19.5f, 7f)
    moveTo(9.5f, 7f); lineTo(9.5f, 4.5f); lineTo(14.5f, 4.5f); lineTo(14.5f, 7f)
    moveTo(6.5f, 7f); lineTo(7.5f, 20f); lineTo(16.5f, 20f); lineTo(17.5f, 7f)
}

/** 进入下一级。 */
val ChevronRightIcon: ImageVector = strokeIcon("ChevronRight") {
    moveTo(9.5f, 5.5f); lineTo(16f, 12f); lineTo(9.5f, 18.5f)
}

/** 搜索。 */
val SearchIcon: ImageVector = strokeIcon("Search") {
    moveTo(11f, 18f)
    curveTo(14.9f, 18f, 18f, 14.9f, 18f, 11f)
    curveTo(18f, 7.1f, 14.9f, 4f, 11f, 4f)
    curveTo(7.1f, 4f, 4f, 7.1f, 4f, 11f)
    curveTo(4f, 14.9f, 7.1f, 18f, 11f, 18f)
    close()
    moveTo(16f, 16f); lineTo(20.5f, 20.5f)
}

/** 新建。 */
val PlusIcon: ImageVector = strokeIcon("Plus") {
    moveTo(12f, 5f); lineTo(12f, 19f)
    moveTo(5f, 12f); lineTo(19f, 12f)
}

/** 勾选。 */
val CheckIcon: ImageVector = strokeIcon("Check", strokeWidth = 2.1f) {
    moveTo(5f, 12.5f); lineTo(9.5f, 17f); lineTo(19f, 6.5f)
}

/** 返回上级。 */
val ArrowLeftIcon: ImageVector = strokeIcon("ArrowLeft") {
    moveTo(20f, 12f); lineTo(4f, 12f)
    moveTo(10f, 6f); lineTo(4f, 12f); lineTo(10f, 18f)
}

/** 排序：三条从长到短的横线。 */
val SortIcon: ImageVector = strokeIcon("Sort") {
    moveTo(4f, 7f); lineTo(20f, 7f)
    moveTo(4f, 12f); lineTo(14f, 12f)
    moveTo(4f, 17f); lineTo(9f, 17f)
}

/** 重试。 */
val RefreshIcon: ImageVector = strokeIcon("Refresh") {
    moveTo(20f, 12f)
    curveTo(20f, 7.6f, 16.4f, 4f, 12f, 4f)
    curveTo(8.9f, 4f, 6.2f, 5.8f, 5f, 8.4f)
    moveTo(20f, 4.5f); lineTo(20f, 8.5f); lineTo(16f, 8.5f)
    moveTo(4f, 12f)
    curveTo(4f, 16.4f, 7.6f, 20f, 12f, 20f)
    curveTo(15.1f, 20f, 17.8f, 18.2f, 19f, 15.6f)
    moveTo(4f, 19.5f); lineTo(4f, 15.5f); lineTo(8f, 15.5f)
}

/** 关闭 X。 */
val CloseIcon: ImageVector = strokeIcon("Close") {
    moveTo(6.5f, 6.5f); lineTo(17.5f, 17.5f)
    moveTo(17.5f, 6.5f); lineTo(6.5f, 17.5f)
}
