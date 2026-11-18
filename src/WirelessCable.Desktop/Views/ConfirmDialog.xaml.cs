using System.Windows;

namespace WirelessCable.Desktop.Views;

/// <summary>
/// 自绘确认框。样式与主界面一致（圆角卡片 + 主题色按钮 + 遮罩），
/// 替代系统 MessageBox —— 后者在深色主题下是一整块浅灰，与产品割裂。
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string confirmText, bool showCancel, bool danger)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (!showCancel)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }

        if (danger)
        {
            // 用引用而不是构造时查找：窗口还没进视觉树时 TryFindResource 可能查不到，
            // 静默回落到主按钮样式——深色下就变成蓝色"删除"（真机踩过）。
            ConfirmButton.SetResourceReference(StyleProperty, "DangerButton");
        }
    }

    /// <summary>
    /// 确认（显示取消）。危险操作把 <paramref name="danger"/> 置真，主按钮变红。
    /// </summary>
    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string confirmText = "确定",
        bool danger = false,
        string cancelText = "取消")
    {
        var dialog = new ConfirmDialog(title, message, confirmText, showCancel: true, danger: danger);
        dialog.CancelButton.Content = cancelText;

        return dialog.ShowOverOwner(owner) == true;
    }

    /// <summary>只告知，不询问。</summary>
    public static void Alert(Window? owner, string title, string message, string confirmText = "知道了")
    {
        var dialog = new ConfirmDialog(title, message, confirmText, showCancel: false, danger: false);
        dialog.ShowOverOwner(owner);
    }

    private bool? ShowOverOwner(Window? owner)
    {
        // 主窗口可能还没显示（启动早期的错误），那时退回屏幕居中
        if (owner is not null && owner.IsLoaded)
        {
            Owner = owner;
            CoverOwner(owner);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return ShowDialog();
    }

    /// <summary>把自己铺在主窗口上，遮住整个窗口而不是只弹一个小框。</summary>
    private void CoverOwner(Window owner)
    {
        Left = owner.Left;
        Top = owner.Top;
        Width = owner.ActualWidth;
        Height = owner.ActualHeight;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
