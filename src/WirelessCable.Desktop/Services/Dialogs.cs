using System.Windows;
using WirelessCable.Desktop.Views;

namespace WirelessCable.Desktop.Services;

/// <summary>
/// 统一弹窗入口。
///
/// 一处换皮，全局生效：以后要改弹窗样式（圆角、按钮顺序、深色适配）只动
/// <see cref="ConfirmDialog"/>，不会出现"有的地方是自绘、有的地方还是系统 MessageBox"。
/// </summary>
public static class Dialogs
{
    /// <summary>询问。返回 true 表示用户点了主按钮。</summary>
    public static bool Confirm(string title, string message, string? confirmText = null, bool danger = false)
        => ConfirmDialog.Confirm(CurrentOwner, title, message, confirmText ?? Loc.T("确定", "OK"), danger);

    /// <summary>只告知，不询问。</summary>
    public static void Alert(string title, string message, string? confirmText = null)
        => ConfirmDialog.Alert(CurrentOwner, title, message, confirmText ?? Loc.T("知道了", "Got it"));

    private static Window? CurrentOwner => Application.Current?.MainWindow;
}
