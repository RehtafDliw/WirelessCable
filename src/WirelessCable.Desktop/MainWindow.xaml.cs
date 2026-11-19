using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using WirelessCable.Desktop.Services;
using WirelessCable.Desktop.ViewModels;

namespace WirelessCable.Desktop;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private bool _balloonShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        Closed += OnClosed;

        // 鼠标后退键：在窗口级别接，页面上"没有控件的地方"也能响应。
        // 挂在视图上是靠不住的——文件少的时候页面下半截根本没有可命中的元素，
        // 鼠标事件落不到 UserControl 上（用户报的就是这个）。
        PreviewMouseDown += OnPreviewMouseDown;
    }

    /// <summary>
    /// 鼠标侧键后退（XButton1）→ 当前文件页上一级。
    ///
    /// 只对「我的手机」和「传输目录」生效：别的页面没有"上级目录"这个概念。
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1 || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (ReferenceEquals(viewModel.CurrentPage, viewModel.PhoneFiles))
        {
            viewModel.PhoneFiles.GoUpCommand.Execute(null);
            e.Handled = true;
        }
        else if (ReferenceEquals(viewModel.CurrentPage, viewModel.MyComputer))
        {
            viewModel.MyComputer.GoUpCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>托盘"退出程序"用：绕过"关闭到托盘"，真正退出。</summary>
    public bool AllowClose { get; set; }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
        {
            previous.PairingPromptOpened -= OnPairingPromptOpened;
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is MainViewModel current)
        {
            current.PairingPromptOpened += OnPairingPromptOpened;
            current.PropertyChanged += OnViewModelPropertyChanged;
            CreateTray(current);
        }
    }

    private void CreateTray(MainViewModel viewModel)
    {
        if (_tray is not null)
        {
            return;
        }

        try
        {
            _tray = new TrayIcon(this, viewModel);
            Serilog.Log.Information("托盘图标已就绪");
        }
        catch (Exception ex)
        {
            // 托盘只是便利功能，建不起来不影响主窗口使用
            Serilog.Log.Warning(ex, "创建托盘图标失败");
        }

        // 角标与托盘互不影响：托盘点不着的时候，任务栏角标还有
        TaskbarBadge.Bind(this, viewModel);
    }

    /// <summary>
    /// 配对请求到达时把提示带到用户正在看的那个窗口。
    ///
    /// 新版界面开着的时候绝不能再把旧窗口推出来：旧窗口在后台启动时是隐藏的，
    /// 一旦 <c>Show()</c> 就会盖住新界面，表现成「扫码配对一完成就跳回旧页面」。
    /// 新界面自己会弹配对确认框（App.tsx 里的 GrantDialog），提示不会丢。
    /// </summary>
    private void OnPairingPromptOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.TryBringNewUiToFront())
        {
            return;
        }

        RestoreAndActivate();
    }

    /// <summary>切页面时淡入：不是装饰，而是让"页面换了"这件事有明确反馈。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentPage))
        {
            FadeIn(PageHost);
        }
    }

    private static void FadeIn(UIElement element)
    {
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void RestoreAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>
    /// 关闭行为：**只隐藏窗口**，服务与传输继续跑；真正退出只走托盘菜单的「退出程序」。
    ///
    /// 这个窗口是兜底界面（主界面是 WebView2 那个），以前这里跟着
    /// <c>settings.CloseToTray</c> 走、默认 false，结果点叉号等于
    /// <c>Application.Shutdown()</c>——后台服务、进行中的传输一起没了。
    /// 用户反馈"双击托盘开的是旧页面，而且关不掉，点叉号直接退出程序"，就是这么来的。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_balloonShown)
        {
            _balloonShown = true;
            _tray?.ShowHint("已收进托盘，服务与传输继续在后台运行。");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;

        // ShutdownMode 是 OnExplicitShutdown（托盘需要独立于窗口存活），
        // 所以关窗后必须自己收尾，否则进程会一直挂着。
        Application.Current?.Shutdown();
    }
}
