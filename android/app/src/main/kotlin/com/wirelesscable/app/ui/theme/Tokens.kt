/*
 * 无线数据线 · Android 设计令牌（生成产物，请勿手改）
 *
 * 由 tokens/generate.mjs 从 tokens/design-tokens.json 生成。
 * 改视觉请改 design-tokens.json，然后重跑生成器 —— 两端同时生效。
 *
 * 本文件是 out/tokens.css 的 Kotlin 等价物：值一一对应。
 * 字号与构件尺寸取移动端阶梯（桌面端阶梯见 out/oink-bridge.css）。
 */

package com.wirelesscable.app.ui.theme

import androidx.compose.animation.core.SpringSpec
import androidx.compose.animation.core.spring
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** 形状密度：oink 在支持超椭圆圆角的浏览器上是 1.45，工具型软件收到 1.18。 */
const val WC_SHAPE_DENSITY: Float = 1.18f

/** 超椭圆曲率 K。1 = 正圆，1.75 = oink 的 superellipse 造型。 */
const val WC_SUPERELLIPSE_K: Float = 1.75f

/** 颜色令牌。 */
object WcColor {
  /** 浅色主题 */
  object Light {
    val Bg = Color(0xFFF9F9FB)
    val Surface = Color(0xFFFDFDFE)
    val Card = Color(0xFFFFFFFF)
    val Raised = Color(0xFFFFFFFF)
    val Well = Color(0xFFECECEF)
    val Well2 = Color(0xFFE0E0E5)
    val Fill = Color(0xFFECECEF)
    val FillHover = Color(0xFFE0E0E5)
    val Line = Color(0x121B1C22)
    val Line2 = Color(0x241B1C22)
    val Hi = Color(0xE6FFFFFF)
    val Text = Color(0xFF1B1C22)
    val Text2 = Color(0xFF5F606A)
    val Text3 = Color(0xFF989AA4)
    val Hover = Color(0x0E1B1C22)
    val Scrim = Color(0x61111216)
    val SuccessText = Color(0xFF17794D)
    val WarningText = Color(0xFF96600A)
    val DangerText = Color(0xFFC7283A)
    val InfoText = Color(0xFF1D5FD1)
    val Accent = Color(0xFF2E7BFF)
    val AccentDeep = Color(0xFF1D5FD1)
    val OnAccent = Color(0xFFFFFFFF)
    val Success = Color(0xFF2FBF71)
    val Warning = Color(0xFFF5A524)
    val Danger = Color(0xFFF04452)
    val Info = Color(0xFF3B82F6)
    val AccentSoft = Color(0xFFDEEAFF)
    val AccentTint = Color(0xFFB8D2FF)
    val AccentRing = Color(0xFFB4CFFF)
    val SuccessSoft = Color(0xFFDEF5E8)
    val WarningSoft = Color(0xFFFDEFD8)
    val DangerSoft = Color(0xFFFDE5E7)
    val InfoSoft = Color(0xFFE4EEFE)
    val AccentText = Color(0xFF1D5FD1)
  }

  /** 深色主题 */
  object Dark {
    val Bg = Color(0xFF1B1A18)
    val Surface = Color(0xFF232220)
    val Card = Color(0xFF2A2927)
    val Raised = Color(0xFF3E3C38)
    val Well = Color(0xFF181716)
    val Well2 = Color(0xFF383632)
    val Fill = Color(0xFF33322E)
    val FillHover = Color(0xFF413F3A)
    val Line = Color(0x12FFFFFF)
    val Line2 = Color(0x26FFFFFF)
    val Hi = Color(0x0DFFFFFF)
    val Text = Color(0xFFF4F4F6)
    val Text2 = Color(0xFFAEACA8)
    val Text3 = Color(0xFF7A7875)
    val Hover = Color(0x0EFFFFFF)
    val Scrim = Color(0x8F000000)
    val SuccessText = Color(0xFF5FD99A)
    val WarningText = Color(0xFFFFC35C)
    val DangerText = Color(0xFFFF7C87)
    val InfoText = Color(0xFF7FB0FF)
    val AccentDeep = Color(0xFF6BA5FF)
    val OnAccent = Color(0xFF0B1220)
    val Accent = Color(0xFF2E7BFF)
    val Success = Color(0xFF2FBF71)
    val Warning = Color(0xFFF5A524)
    val Danger = Color(0xFFF04452)
    val Info = Color(0xFF3B82F6)
    val AccentSoft = Color(0xFF2B364A)
    val AccentTint = Color(0xFF2B4570)
    val AccentRing = Color(0xFF2B4775)
    val SuccessSoft = Color(0xFF2B4133)
    val WarningSoft = Color(0xFF4F3F26)
    val DangerSoft = Color(0xFF462D2D)
    val InfoSoft = Color(0xFF2C3544)
    val AccentText = Color(0xFF5C98FF)
  }

  val Accent = Color(0xFF2E7BFF)
  val AccentDeep = Color(0xFF1D5FD1)
  val OnAccent = Color(0xFFFFFFFF)
  val Success = Color(0xFF2FBF71)
  val Warning = Color(0xFFF5A524)
  val Danger = Color(0xFFF04452)
  val Info = Color(0xFF3B82F6)
}

/** 圆角令牌（dp，实际值已乘形状密度）。 */
object WcRadius {
  val r1 = 9.44.dp
  val r2 = 14.16.dp
  val r3 = 18.88.dp
  val r4 = 28.32.dp
  val r5 = 37.76.dp
  val full = 1178.82.dp
}

/** 间距令牌（dp）。 */
object WcSpace {
  val s1 = 4.dp
  val s2 = 8.dp
  val s3 = 12.dp
  val s4 = 16.dp
  val s5 = 20.dp
  val s6 = 24.dp
  val s8 = 32.dp
  val s10 = 40.dp
}

/** 字号令牌（sp）。同时给出可直接用的 TextStyle。取移动端阶梯。 */
object WcType {
  val displaySize = 26.sp
  val displayWeight = FontWeight.Bold
  val displayLine = 32.sp
  val displayLetterSpacing = -0.5.sp
  val Display = TextStyle(
    fontSize = 26.sp,
    fontWeight = FontWeight.Bold,
    lineHeight = 32.sp,
    letterSpacing = -0.5.sp,
  )
  val h1Size = 21.sp
  val h1Weight = FontWeight.Bold
  val h1Line = 27.sp
  val h1LetterSpacing = -0.3.sp
  val H1 = TextStyle(
    fontSize = 21.sp,
    fontWeight = FontWeight.Bold,
    lineHeight = 27.sp,
    letterSpacing = -0.3.sp,
  )
  val h2Size = 17.sp
  val h2Weight = FontWeight.Bold
  val h2Line = 23.sp
  val h2LetterSpacing = -0.2.sp
  val H2 = TextStyle(
    fontSize = 17.sp,
    fontWeight = FontWeight.Bold,
    lineHeight = 23.sp,
    letterSpacing = -0.2.sp,
  )
  val h3Size = 15.sp
  val h3Weight = FontWeight.SemiBold
  val h3Line = 20.sp
  val h3LetterSpacing = 0.sp
  val H3 = TextStyle(
    fontSize = 15.sp,
    fontWeight = FontWeight.SemiBold,
    lineHeight = 20.sp,
    letterSpacing = 0.sp,
  )
  val bodySize = 14.sp
  val bodyWeight = FontWeight.Normal
  val bodyLine = 20.sp
  val bodyLetterSpacing = 0.sp
  val Body = TextStyle(
    fontSize = 14.sp,
    fontWeight = FontWeight.Normal,
    lineHeight = 20.sp,
    letterSpacing = 0.sp,
  )
  val bodyStrongSize = 14.sp
  val bodyStrongWeight = FontWeight.SemiBold
  val bodyStrongLine = 20.sp
  val bodyStrongLetterSpacing = 0.sp
  val BodyStrong = TextStyle(
    fontSize = 14.sp,
    fontWeight = FontWeight.SemiBold,
    lineHeight = 20.sp,
    letterSpacing = 0.sp,
  )
  val labelSize = 12.5.sp
  val labelWeight = FontWeight.Medium
  val labelLine = 17.sp
  val labelLetterSpacing = 0.sp
  val Label = TextStyle(
    fontSize = 12.5.sp,
    fontWeight = FontWeight.Medium,
    lineHeight = 17.sp,
    letterSpacing = 0.sp,
  )
  val captionSize = 11.5.sp
  val captionWeight = FontWeight.Medium
  val captionLine = 16.sp
  val captionLetterSpacing = 0.sp
  val Caption = TextStyle(
    fontSize = 11.5.sp,
    fontWeight = FontWeight.Medium,
    lineHeight = 16.sp,
    letterSpacing = 0.sp,
  )
  val monoSize = 12.sp
  val monoWeight = FontWeight.Medium
  val monoLine = 16.sp
  val monoLetterSpacing = 0.sp
  val Mono = TextStyle(
    fontSize = 12.sp,
    fontWeight = FontWeight.Medium,
    lineHeight = 16.sp,
    letterSpacing = 0.sp,
  )
}

/** 动效令牌。弹簧参数由 oink-ui 的 linear() 采样表数值拟合而来，两端节奏一致。 */
object WcMotion {
  const val DurFast = 90
  const val DurBase = 150
  const val DurSlow = 240
  const val DurSlower = 360
  const val DurEnter = 303
  const val DurExit = 160

  /** soft · 587ms · 拟合 RMSE 0.000987 */
  val soft = WcSpring(dampingRatio = 1f, stiffness = 170.46f)
  /** spring · 454ms · 拟合 RMSE 0.00151 */
  val spring = WcSpring(dampingRatio = 0.75f, stiffness = 262.03f)
  /** snappy · 325ms · 拟合 RMSE 0.00205 */
  val snappy = WcSpring(dampingRatio = 0.75f, stiffness = 529.53f)
  /** bouncy · 542ms · 拟合 RMSE 0.003358 */
  val bouncy = WcSpring(dampingRatio = 0.52f, stiffness = 294.63f)
  /** wobbly · 1104ms · 拟合 RMSE 0.004076 */
  val wobbly = WcSpring(dampingRatio = 0.33f, stiffness = 177.26f)
}

/** Compose spring 参数。 */
data class WcSpring(val dampingRatio: Float, val stiffness: Float)

/** 把 WcSpring 转成 Compose 的 SpringSpec：animateDpAsState(target, spring(spec))。 */
fun WcSpring.toSpec(): SpringSpec<Float> = spring(dampingRatio = dampingRatio, stiffness = stiffness)

/** 构件尺寸（dp）。取移动端阶梯。 */
object WcSize {
  val TouchTarget = 48.dp
  val CardPad = 14.dp
  val RowHeight = 56.dp
  val RowHeightCompact = 44.dp
  val NavHeight = 58.dp
  val TopBarHeight = 52.dp
  val IconSm = 17.dp
  val IconMd = 20.dp
  val IconLg = 24.dp
  val Hairline = 1.dp
  val Highlight = 1.5.dp
}
