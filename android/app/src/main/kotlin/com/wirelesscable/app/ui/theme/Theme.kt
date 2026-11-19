package com.wirelesscable.app.ui.theme

import android.app.Activity
import android.content.Context
import android.graphics.drawable.ColorDrawable
import android.view.WindowInsetsController
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalView
import com.wirelesscable.app.core.i18n.Str

/**
 * 界面令牌。
 *
 * 数值来源是 [WcColor]（`docs/ui/tokens/design-tokens.json` 生成），本文件只做一层转发：
 * 改视觉改令牌 JSON、重跑生成器，两端一起变。**这里不写颜色字面量。**
 *
 * 深浅两套取值放在 [WcPalette] 里，[WcColors] 保存当前生效的那一份。
 * 用可变 object 而不是 CompositionLocal：全项目有一百多处 `WcColors.Text` 式直接引用，
 * 改成 local 读取要动所有文件；这里的属性都是 Compose State，切换主题会自动触发重组。
 */
data class WcPalette(
    /** 页面底色（旧字段名保留：它一直是内容区背景）。 */
    val surface: Color,
    val card: Color,
    /** 下沉表面：列表底、输入框、图标座。 */
    val tile: Color,
    val border: Color,
    val text: Color,
    val muted: Color,
    val accent: Color,
    val accentSoft: Color,
    val success: Color,
    val successPill: Color,
    val error: Color,
    val errorPill: Color,
    val idlePill: Color,
    val wip: Color,
    val wipText: Color,
    // ---- 新设计用到的令牌（2026-11-16 起，全部来自 WcColor） ----
    val accentText: Color,
    val onAccent: Color,
    val text2: Color,
    val well: Color,
    val well2: Color,
    val soft: Color,
    val line: Color,
    val line2: Color,
    val hi: Color,
    val danger: Color,
    val dangerSoft: Color,
    val warning: Color,
    val warningSoft: Color,
    val warningText: Color,
    /** 旧字段：深色标题栏上的胶囊。标题栏已改成浅色，保留字段避免大面积改调用点。 */
    val headerPill: Color,
    val headerPillText: Color,
    val isDark: Boolean,
)

private fun paletteOf(
    /** true 取深色 */
    dark: Boolean,
    bg: Color,
    card: Color,
    well: Color,
    well2: Color,
    soft: Color,
    line: Color,
    line2: Color,
    hi: Color,
    text: Color,
    text2: Color,
    text3: Color,
    accent: Color,
    accentSoft: Color,
    accentText: Color,
    onAccent: Color,
    success: Color,
    successSoft: Color,
    successText: Color,
    warning: Color,
    warningSoft: Color,
    warningText: Color,
    danger: Color,
    dangerSoft: Color,
    dangerText: Color,
    fill: Color,
): WcPalette = WcPalette(
    surface = bg,
    card = card,
    tile = well,
    border = line2,
    text = text,
    muted = text3,
    accent = accent,
    accentSoft = accentSoft,
    success = successText,
    successPill = successSoft,
    error = dangerText,
    errorPill = dangerSoft,
    idlePill = fill,
    wip = warningSoft,
    wipText = warningText,
    accentText = accentText,
    onAccent = onAccent,
    text2 = text2,
    well = well,
    well2 = well2,
    soft = soft,
    line = line,
    line2 = line2,
    hi = hi,
    danger = danger,
    dangerSoft = dangerSoft,
    warning = warning,
    warningSoft = warningSoft,
    warningText = warningText,
    headerPill = successSoft,
    headerPillText = success,
    isDark = dark,
)

val LightPalette: WcPalette = paletteOf(
    dark = false,
    bg = WcColor.Light.Bg,
    card = WcColor.Light.Card,
    well = WcColor.Light.Well,
    well2 = WcColor.Light.Well2,
    soft = WcColor.Light.Well,
    line = WcColor.Light.Line,
    line2 = WcColor.Light.Line2,
    hi = WcColor.Light.Hi,
    text = WcColor.Light.Text,
    text2 = WcColor.Light.Text2,
    text3 = WcColor.Light.Text3,
    accent = WcColor.Light.Accent,
    accentSoft = WcColor.Light.AccentSoft,
    accentText = WcColor.Light.AccentText,
    onAccent = WcColor.Light.OnAccent,
    success = WcColor.Light.Success,
    successSoft = WcColor.Light.SuccessSoft,
    successText = WcColor.Light.SuccessText,
    warning = WcColor.Light.Warning,
    warningSoft = WcColor.Light.WarningSoft,
    warningText = WcColor.Light.WarningText,
    danger = WcColor.Light.Danger,
    dangerSoft = WcColor.Light.DangerSoft,
    dangerText = WcColor.Light.DangerText,
    fill = WcColor.Light.Fill,
)

val DarkPalette: WcPalette = paletteOf(
    dark = true,
    bg = WcColor.Dark.Bg,
    card = WcColor.Dark.Card,
    well = WcColor.Dark.Well,
    well2 = WcColor.Dark.Well2,
    soft = WcColor.Dark.Fill,
    line = WcColor.Dark.Line,
    line2 = WcColor.Dark.Line2,
    hi = WcColor.Dark.Hi,
    text = WcColor.Dark.Text,
    text2 = WcColor.Dark.Text2,
    text3 = WcColor.Dark.Text3,
    accent = WcColor.Dark.Accent,
    accentSoft = WcColor.Dark.AccentSoft,
    accentText = WcColor.Dark.AccentText,
    onAccent = WcColor.Dark.OnAccent,
    success = WcColor.Dark.Success,
    successSoft = WcColor.Dark.SuccessSoft,
    successText = WcColor.Dark.SuccessText,
    warning = WcColor.Dark.Warning,
    warningSoft = WcColor.Dark.WarningSoft,
    warningText = WcColor.Dark.WarningText,
    danger = WcColor.Dark.Danger,
    dangerSoft = WcColor.Dark.DangerSoft,
    dangerText = WcColor.Dark.DangerText,
    fill = WcColor.Dark.Fill,
)

/** 当前生效的令牌。切换主题时由 [WirelessCableTheme] 整体替换。 */
object WcColors {
    var Surface by mutableStateOf(LightPalette.surface); private set
    var Card by mutableStateOf(LightPalette.card); private set
    var Tile by mutableStateOf(LightPalette.tile); private set
    var Border by mutableStateOf(LightPalette.border); private set
    var Text by mutableStateOf(LightPalette.text); private set
    var Muted by mutableStateOf(LightPalette.muted); private set
    var Accent by mutableStateOf(LightPalette.accent); private set
    var AccentSoft by mutableStateOf(LightPalette.accentSoft); private set
    var Success by mutableStateOf(LightPalette.success); private set
    var SuccessPill by mutableStateOf(LightPalette.successPill); private set
    var Error by mutableStateOf(LightPalette.error); private set
    var ErrorPill by mutableStateOf(LightPalette.errorPill); private set
    var IdlePill by mutableStateOf(LightPalette.idlePill); private set
    var Wip by mutableStateOf(LightPalette.wip); private set
    var WipText by mutableStateOf(LightPalette.wipText); private set

    var AccentText by mutableStateOf(LightPalette.accentText); private set
    var OnAccent by mutableStateOf(LightPalette.onAccent); private set
    var Text2 by mutableStateOf(LightPalette.text2); private set
    var Well by mutableStateOf(LightPalette.well); private set
    var Well2 by mutableStateOf(LightPalette.well2); private set
    var Soft by mutableStateOf(LightPalette.soft); private set
    var Line by mutableStateOf(LightPalette.line); private set
    var Line2 by mutableStateOf(LightPalette.line2); private set
    var Hi by mutableStateOf(LightPalette.hi); private set
    var Danger by mutableStateOf(LightPalette.danger); private set
    var DangerSoft by mutableStateOf(LightPalette.dangerSoft); private set
    var Warning by mutableStateOf(LightPalette.warning); private set
    var WarningSoft by mutableStateOf(LightPalette.warningSoft); private set
    var WarningText by mutableStateOf(LightPalette.warningText); private set

    // 旧字段：深色标题栏的胶囊配色。标题栏已改为浅色主题，仅保存兼容。
    var HeaderPill by mutableStateOf(LightPalette.headerPill); private set
    var HeaderPillText by mutableStateOf(LightPalette.headerPillText); private set

    var isDark by mutableStateOf(false); private set

    fun apply(palette: WcPalette) {
        if (isDark == palette.isDark && Text == palette.text && Surface == palette.surface) {
            return
        }

        Surface = palette.surface
        Card = palette.card
        Tile = palette.tile
        Border = palette.border
        Text = palette.text
        Muted = palette.muted
        Accent = palette.accent
        AccentSoft = palette.accentSoft
        Success = palette.success
        SuccessPill = palette.successPill
        Error = palette.error
        ErrorPill = palette.errorPill
        IdlePill = palette.idlePill
        Wip = palette.wip
        WipText = palette.wipText
        AccentText = palette.accentText
        OnAccent = palette.onAccent
        Text2 = palette.text2
        Well = palette.well
        Well2 = palette.well2
        Soft = palette.soft
        Line = palette.line
        Line2 = palette.line2
        Hi = palette.hi
        Danger = palette.danger
        DangerSoft = palette.dangerSoft
        Warning = palette.warning
        WarningSoft = palette.warningSoft
        WarningText = palette.warningText
        HeaderPill = palette.headerPill
        HeaderPillText = palette.headerPillText
        isDark = palette.isDark
    }
}

/** 主题偏好。字符串值写进 SharedPreferences，改动要兼容旧值。 */
enum class ThemeMode(val setting: String) {
    System("system"),
    Light("light"),
    Dark("dark"),
    ;

    companion object {
        fun parse(value: String?): ThemeMode = when (value) {
            Light.setting -> Light
            Dark.setting -> Dark
            else -> System
        }
    }
}

/** 主题选择的唯一真相：设置页改这里，根组合读这里。 */
object WcThemeState {
    private const val PREFS = "wirelesscable_app"
    private const val KEY = "theme.mode"

    var mode by mutableStateOf(ThemeMode.System)
        private set

    fun load(context: Context) {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        mode = ThemeMode.parse(prefs.getString(KEY, ThemeMode.System.setting))
    }

    fun set(context: Context, value: ThemeMode) {
        mode = value
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY, value.setting)
            .apply()
    }
}

private fun scheme(palette: WcPalette) = if (palette.isDark) {
    darkColorScheme(
        primary = palette.accent,
        onPrimary = palette.onAccent,
        background = palette.surface,
        onBackground = palette.text,
        surface = palette.card,
        onSurface = palette.text,
        surfaceVariant = palette.tile,
        onSurfaceVariant = palette.muted,
        outline = palette.border,
        error = palette.error,
    )
} else {
    lightColorScheme(
        primary = palette.accent,
        onPrimary = palette.onAccent,
        background = palette.surface,
        onBackground = palette.text,
        surface = palette.card,
        onSurface = palette.text,
        surfaceVariant = palette.tile,
        onSurfaceVariant = palette.muted,
        outline = palette.border,
        error = palette.error,
    )
}

@Composable
fun WirelessCableTheme(content: @Composable () -> Unit) {
    val mode = WcThemeState.mode
    val dark = when (mode) {
        ThemeMode.System -> isSystemInDarkTheme()
        ThemeMode.Light -> false
        ThemeMode.Dark -> true
    }
    val palette = if (dark) DarkPalette else LightPalette

    // 组合体内同步写令牌：用 SideEffect 的话深色主题第一帧会是浅色，能看见闪一下。
    WcColors.apply(palette)

    // 系统栏跟随主题：透明底 + 深/浅图标。写在组合体里而不是 styles.xml，
    // 因为深浅两套要各自配对，XML 只能写死一种（旧版是深底白字，浅色页顶上一条黑边）。
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as? Activity)?.window ?: return@SideEffect
            window.setBackgroundDrawable(ColorDrawable(palette.surface.toArgb()))
            window.statusBarColor = android.graphics.Color.TRANSPARENT
            window.navigationBarColor = android.graphics.Color.TRANSPARENT

            val light = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or
                WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS
            window.insetsController?.setSystemBarsAppearance(if (dark) 0 else light, light)
        }
    }

    MaterialTheme(colorScheme = scheme(palette), content = content)
}
