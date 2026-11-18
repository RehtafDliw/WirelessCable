using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WirelessCable.Desktop.ViewModels;

namespace WirelessCable.Desktop.Views;

/// <summary>
/// 传输中心。行内不放按钮：暂停/继续/取消/删除都在右键菜单里，
/// 与文件页保持同一套操作习惯（1.4.3 起）。
/// </summary>
public partial class TransfersView : UserControl
{
    public TransfersView() => InitializeComponent();

    private TransfersViewModel? ViewModel => DataContext as TransfersViewModel;

    /// <summary>右键菜单里的项与当前行共用 DataContext（从 PlacementTarget 继承）。</summary>
    private static TransferRowViewModel? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as TransferRowViewModel;

    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 资源管理器的习惯：右键点在没勾选的行上，先把它变成唯一选中项
        if (RowOf(sender) is { } row)
        {
            ViewModel?.EnsureRowSelected(row);
        }
    }

    private void OnRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击定位文件，代替以前行内的「打开所在文件夹」
        if (e.ClickCount == 2 && RowOf(sender) is { } row)
        {
            ViewModel?.Reveal(row);
        }
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
        => ViewModel?.Reveal(RowOf(sender));

    private void OnPauseClick(object sender, RoutedEventArgs e)
        => ViewModel?.PauseCommand.Execute(RowOf(sender)?.TaskId);

    private void OnResumeClick(object sender, RoutedEventArgs e)
        => ViewModel?.ResumeCommand.Execute(RowOf(sender)?.TaskId);

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => ViewModel?.CancelCommand.Execute(RowOf(sender)?.TaskId);

    private void OnDeleteRecordsClick(object sender, RoutedEventArgs e)
        => ViewModel?.DeleteRecordsCommand.Execute(RowOf(sender));
}
