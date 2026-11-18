using System.Windows;

namespace WirelessCable.Desktop.Views;

/// <summary>
/// 删除传输记录前的确认：一条记录要不要连磁盘文件一起删。
///
/// 勾选框只在**确实有可删文件**时可用——没有文件却给一个可勾的选项，
/// 用户勾了却发现没删东西，会以为功能坏了。
/// </summary>
public partial class DeleteRecordsWindow : Window
{
    /// <param name="count">要删的记录条数。</param>
    /// <param name="fileCount">这些记录里，电脑上确实存在、且允许删除的文件数。</param>
    public DeleteRecordsWindow(int count, int fileCount)
    {
        InitializeComponent();

        MessageText.Text = $"删除 {count} 条传输记录？";

        if (fileCount == 0)
        {
            DeleteFilesCheck.IsEnabled = false;
            DeleteFilesCheck.Content = "同时删除接收到的文件";
            FilesHint.Text = "这批记录里没有本机收到的文件：文件在手机上，或者还没传完。";
        }
        else
        {
            DeleteFilesCheck.Content = $"同时删除接收到的文件（{fileCount} 个）";
            FilesHint.Text = "只删手机传过来、已经落到接收目录的文件；电脑发给手机的原文件不会被删除；" +
                "选中的记录里若是发出去的，只删记录。";
        }
    }

    /// <summary>用户是否勾了「同时删除文件」。</summary>
    public bool DeleteFiles => DeleteFilesCheck.IsChecked == true;

    /// <summary>铺在主窗口上显示，与 <see cref="ConfirmDialog"/> 一致。</summary>
    public bool? ShowOverOwner(Window? owner)
    {
        if (owner is not null && owner.IsLoaded)
        {
            Owner = owner;
            Left = owner.Left;
            Top = owner.Top;
            Width = owner.ActualWidth;
            Height = owner.ActualHeight;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return ShowDialog();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
