using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WirelessCable.Desktop.Views;

public partial class PhoneFilesView : UserControl
{
    public PhoneFilesView()
    {
        InitializeComponent();

        // 鼠标侧键（XButton1）现在由 MainWindow 统一处理：挂在视图上时，
        // 文件少的目录下半截没有可命中元素，事件根本到不了这里（1.4.6 修）。
    }

    /// <summary>
    /// 双击目录进下一级。只对目录生效——文件的双击没有明确语义
    /// （电脑上没有副本，"保存到电脑"是显式动作，不该被误触）。
    /// </summary>
    private void OnRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: ViewModels.PhoneFileRowViewModel row } || !row.IsDirectory)
        {
            return;
        }

        if (DataContext is ViewModels.PhoneFilesViewModel viewModel)
        {
            viewModel.PrimaryActionCommand.Execute(row);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ 右键菜单
    //
    // 菜单项的 DataContext 就是被右击的那一行（ContextMenu 从 PlacementTarget 继承），
    // 命令则挂在页面的 ViewModel 上——所以这里不绑定 Command，直接用 Click 转一次。
    // 用 x:Reference 绑命令在 DataTemplate 里不可靠（模板有自己的命名域）。

    private ViewModels.PhoneFilesViewModel? Vm => DataContext as ViewModels.PhoneFilesViewModel;

    private static ViewModels.PhoneFileRowViewModel? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as ViewModels.PhoneFileRowViewModel;

    private void OnMenuPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Vm?.PrimaryActionCommand.Execute(row);
        }
    }

    private void OnMenuRenameClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Vm?.RenameCommand.Execute(row);
        }
    }

    private void OnMenuDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Vm?.DeleteCommand.Execute(row);
        }
    }

    private void OnMenuPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Vm?.ShowProperties(row);
        }
    }
}
