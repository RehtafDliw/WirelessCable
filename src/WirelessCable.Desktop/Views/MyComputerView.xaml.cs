using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WirelessCable.Desktop.Views;

public partial class MyComputerView : UserControl
{
    public MyComputerView() => InitializeComponent();

    /// <summary>双击行：目录进入下一级，文件交给系统默认程序打开。</summary>
    private void OnRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: ViewModels.LocalFileRowViewModel row })
        {
            return;
        }

        if (DataContext is ViewModels.MyComputerViewModel viewModel)
        {
            viewModel.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ 右键菜单

    private ViewModels.MyComputerViewModel? Vm => DataContext as ViewModels.MyComputerViewModel;

    private static ViewModels.LocalFileRowViewModel? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as ViewModels.LocalFileRowViewModel;

    private void OnMenuOpenClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Vm?.OpenCommand.Execute(row);
        }
    }

    private void OnMenuRevealClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Vm?.RevealCommand.Execute(row);
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
