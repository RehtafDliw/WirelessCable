/*
 * 超椭圆圆角（superellipse）的 Compose Shape 实现。
 *
 * 由 tokens/generate.mjs 生成，请勿手改。
 *
 * 为什么 Compose 侧也要做这件事：
 *   oink-ui 的默认造型是 corner-shape: superellipse(1.75) —— 不是圆角，是超椭圆。
 *   浏览器一条 CSS 就能画，Compose 没有内置等价物 —— 但这不是能力缺失：
 *   Shape 接口本来就允许返回任意 Path，而超椭圆恰好是「两段三次贝塞尔」。
 *
 * 几何与 Chromium 画 corner-shape: superellipse(K) 一致：
 *   每个角拆成两段三次贝塞尔，从直边起点到 45° 点；
 *   把手长度按 K 查表（表来自 Chromium 实际渲染的拟合值）并线性插值。
 *
 * 用法：
 *   Surface(shape = SquircleShape(WcRadius.r3)) { ... }
 *   Modifier.clip(SquircleShape(14.dp))
 */

package com.wirelesscable.app.ui.theme

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Outline
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.LayoutDirection
import kotlin.math.min

/** K → 两段三次贝塞尔的把手长度（以半径为单位）。 */
private val WC_CORNER_HANDLES: Map<Float, FloatArray> = mapOf(
    0f to floatArrayOf(0f, 0.4714f),
    0.25f to floatArrayOf(0.0266f, 0.447f),
    0.5f to floatArrayOf(0.061f, 0.4174f),
    0.75f to floatArrayOf(0.1043f, 0.3848f),
    1.1f to floatArrayOf(0.1846f, 0.3359f),
    1.25f to floatArrayOf(0.2171f, 0.3201f),
    1.375f to floatArrayOf(0.2551f, 0.302f),
    1.5f to floatArrayOf(0.2917f, 0.2864f),
    1.625f to floatArrayOf(0.3294f, 0.2714f),
    1.75f to floatArrayOf(0.3673f, 0.2565f),
    1.875f to floatArrayOf(0.4047f, 0.2418f),
    2f to floatArrayOf(0.4393f, 0.2274f),
    2.25f to floatArrayOf(0.4946f, 0.2011f),
    2.5f to floatArrayOf(0.5519f, 0.1767f),
    2.75f to floatArrayOf(0.6129f, 0.1541f),
    3f to floatArrayOf(0.6749f, 0.1334f),
    3.5f to floatArrayOf(0.7875f, 0.0985f),
    4f to floatArrayOf(0.8701f, 0.0718f),
    5f to floatArrayOf(0.9667f, 0.0372f),
    8f to floatArrayOf(1f, 0f)
)

/** 正圆（K = 1）的把手长度：4/3 * tan(PI/16)。 */
private const val WC_CIRCLE_HANDLE = 0.26521649f

private fun cornerHandles(k: Float): FloatArray {
  if (k == 1f) return floatArrayOf(WC_CIRCLE_HANDLE, WC_CIRCLE_HANDLE)
  val keys = WC_CORNER_HANDLES.keys.sorted()
  if (k >= keys.last()) return floatArrayOf(1f, 0f)
  for (i in 1 until keys.size) {
    val hi = keys[i]
    if (k <= hi) {
      val lo = keys[i - 1]
      val a = WC_CORNER_HANDLES.getValue(lo)
      val b = WC_CORNER_HANDLES.getValue(hi)
      val t = (k - lo) / (hi - lo)
      return floatArrayOf(a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)
    }
  }
  return floatArrayOf(1f, 0f)
}

/**
 * 超椭圆圆角。
 *
 * @param radius 圆角半径（dp）
 * @param k 曲率。1 = 正圆，1.75 = oink 的 superellipse 造型，Infinity = 直角
 */
class SquircleShape(
  private val radius: Dp,
  private val k: Float = WC_SUPERELLIPSE_K,
) : Shape {

  override fun createOutline(
    size: Size,
    layoutDirection: LayoutDirection,
    density: Density,
  ): Outline {
    val r = with(density) { radius.toPx() }.coerceAtMost(min(size.width, size.height) / 2f)
    // Outline.Rectangle 要的是 Rect（不是 Size）
    if (r <= 0f) return Outline.Rectangle(Rect(Offset.Zero, size))
    return Outline.Generic(buildPath(size.width, size.height, r))
  }

  private fun buildPath(w: Float, h: Float, r: Float): Path {
    val handles = cornerHandles(k)
    val n = handles[0]
    val e = 1f - Math.pow(2.0, -1.0 / Math.pow(2.0, k.toDouble())).toFloat()
    val a = handles[1] * 0.70710678f

    // 单位角的 7 个点（u 离左边、v 离上边，单位为半径）：
    // 左直边 (0,1) → 45° 点 (e,e) → 上直边 (1,0)
    val unit = arrayOf(
      0f to 1f,
      0f to (1f - n),
      (e - a) to (e + a),
      e to e,
      (e + a) to (e - a),
      (1f - n) to 0f,
      1f to 0f,
    )

    val path = Path()
    // 左上角：原样（左直边 → 上直边）
    path.moveTo(unit[0].first * r, unit[0].second * r)
    appendCorner(path, unit, 0f, 0f, r, r, mirrorX = false, mirrorY = false, reverse = false)
    // 右上角：镜像 x 并倒序（上直边 → 右直边）
    path.lineTo(w - r, 0f)
    appendCorner(path, unit, w, 0f, r, r, mirrorX = true, mirrorY = false, reverse = true)
    // 右下角：镜像 x/y，正序（右直边 → 下直边）
    // 注意方向：正序的起点是 unit[0] 的镜像 = (w, h-r)，正好接上右直边；
    // 这里若用倒序，起点会变成 (w-r, h)，路径从 (w,h-r) 直接连过去 —— 右下角被切一刀。
    path.lineTo(w, h - r)
    appendCorner(path, unit, w, h, r, r, mirrorX = true, mirrorY = true, reverse = false)
    // 左下角：镜像 y 并倒序（左直边 → 下直边）
    path.lineTo(r, h)
    appendCorner(path, unit, 0f, h, r, r, mirrorX = false, mirrorY = true, reverse = true)
    path.close()
    return path
  }

  /** 追加一个角：两段三次贝塞尔。 */
  private fun appendCorner(
    path: Path,
    unit: Array<Pair<Float, Float>>,
    ox: Float,
    oy: Float,
    rx: Float,
    ry: Float,
    mirrorX: Boolean,
    mirrorY: Boolean,
    reverse: Boolean,
  ) {
    val pts = ArrayList<Pair<Float, Float>>(unit.size)
    for ((u, v) in unit) {
      val x = if (mirrorX) ox - u * rx else ox + u * rx
      val y = if (mirrorY) oy - v * ry else oy + v * ry
      pts.add(x to y)
    }
    val seq = if (reverse) pts.asReversed() else pts
    // seq: [起点, 控制点1, 控制点2, 45°点, 控制点3, 控制点4, 终点]
    path.cubicTo(seq[1].first, seq[1].second, seq[2].first, seq[2].second, seq[3].first, seq[3].second)
    path.cubicTo(seq[4].first, seq[4].second, seq[5].first, seq[5].second, seq[6].first, seq[6].second)
  }
}
