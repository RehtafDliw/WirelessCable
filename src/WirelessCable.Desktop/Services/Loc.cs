using System.Globalization;

namespace WirelessCable.Desktop.Services;

/// <summary>
/// 原生界面（托盘菜单、气泡）的极简翻译。
///
/// 网页界面有自己的 i18n，用户可以在页面里手动切中英；但托盘由 C# 绘制，
/// 读不到浏览器里的 localStorage，所以这里**跟随系统语言**——这也是 Windows
/// 上托盘菜单的惯例：中文系统给中文，其余给英文。
///
/// 只覆盖用户一眼能看见的几行；日志、异常、内部提示保持中文。
/// </summary>
internal static class Loc
{
    private static readonly bool English =
        !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static string T(string zh, string en) => English ? en : zh;
}
