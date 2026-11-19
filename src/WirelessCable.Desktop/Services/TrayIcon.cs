using System;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WirelessCable.Desktop.ViewModels;
using WinForms = System.Windows.Forms;

namespace WirelessCable.Desktop.Services;

/// <summary>
/// 系统托盘图标与菜单。
///
/// WPF 没有托盘 API，用 WinForms 的 NotifyIcon 承载图标与点击事件，
/// 菜单仍然是 WPF 的 ContextMenu——这样配色跟着主题走，不会在深色下弹出一块白底系统菜单。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIconHolder _holder;

    public TrayIcon(Window window, MainViewModel viewModel)
    {
        _holder = new NotifyIconHolder(window, viewModel);
    }

    public void ShowHint(string text)
    {
        _holder.ShowBalloon(text);
    }

    public void Dispose() => _holder.Dispose();

    /// <summary>内部的 WinForms 对象单独一层，避免 using 冲突（System.Drawing 与 WPF 类型重名）。</summary>
    private sealed class NotifyIconHolder : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly ContextMenu _menu;
        private readonly MenuItem _statusItem;
        private readonly MenuItem _serviceItem;
        private readonly Window _window;
        private readonly MainViewModel _viewModel;
        private bool _disposed;

        public NotifyIconHolder(Window window, MainViewModel viewModel)
        {
            _window = window;
            _viewModel = viewModel;

            _statusItem = new MenuItem { Header = Loc.T("服务运行中", "Service running"), IsEnabled = false };
            _serviceItem = new MenuItem { Header = Loc.T("暂停服务", "Pause service") };
            _serviceItem.Click += async (_, _) => await ToggleServiceAsync();

            // 新版界面是默认入口；旧界面保留为回退路径
            var showNewItem = new MenuItem { Header = Loc.T("新版界面", "New UI") };
            showNewItem.Click += (_, _) => _viewModel.OpenNewUiWindow();

            var showItem = new MenuItem { Header = Loc.T("旧版界面", "Classic UI") };
            showItem.Click += (_, _) => ShowWindow();

            var exitItem = new MenuItem { Header = Loc.T("退出程序", "Exit") };
            exitItem.Click += (_, _) => ExitApplication();

            _menu = new ContextMenu();
            _menu.Items.Add(new MenuItem { Header = "WirelessCable", IsEnabled = false });
            _menu.Items.Add(new Separator());
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(showNewItem);
            _menu.Items.Add(showItem);
            _menu.Items.Add(new Separator());
            _menu.Items.Add(_serviceItem);
            _menu.Items.Add(new Separator());
            _menu.Items.Add(exitItem);

            _icon = new WinForms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "WirelessCable",
                Visible = true,
            };

            _icon.MouseUp += OnMouseUp;
            _icon.DoubleClick += (_, _) => ShowWindow();

            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        private static Icon LoadIcon()
        {
            try
            {
                // 托盘用"只有图形、透明底"的角标图：整块深蓝底在 16px 的托盘里就是个黑方点，
                // 反而看不出是什么。图形那版缩到 16/24 仍是三道弧线。
                var badge = Application.GetResourceStream(
                    new Uri("assets/WirelessCable.Badge.ico", UriKind.Relative))?.Stream;

                if (badge is not null)
                {
                    using (badge)
                    {
                        return new Icon(badge, WinForms.SystemInformation.SmallIconSize);
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "加载托盘角标图失败，退回程序图标");
            }

            try
            {
                var executable = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(executable))
                {
                    var extracted = Icon.ExtractAssociatedIcon(executable);
                    if (extracted is not null)
                    {
                        return extracted;
                    }
                }

                var stream = Application.GetResourceStream(
                    new Uri("assets/WirelessCable.ico", UriKind.Relative))?.Stream;

                if (stream is not null)
                {
                    using (stream)
                    {
                        return new Icon(stream);
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "加载托盘图标失败，使用默认图标");
            }

            return SystemIcons.Application;
        }

        private void OnMouseUp(object? sender, WinForms.MouseEventArgs e)
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                ShowMenu();
            }
        }

        private void ShowMenu()
        {
            // 弹菜单前必须让进程有一个前台窗口，否则点菜单外面不会关闭
            _window.Activate();
            _menu.Placement = PlacementMode.MousePoint;
            _menu.IsOpen = true;
        }

        private void ShowWindow()
        {
            if (!_window.IsVisible)
            {
                _window.Show();
            }

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        }

        private async System.Threading.Tasks.Task ToggleServiceAsync()
        {
            var running = _viewModel.Server.Snapshot().State == Core.Diagnostics.ServiceState.Running;

            try
            {
                if (running)
                {
                    await _viewModel.Server.StopAsync();
                    ShowBalloon(Loc.T("服务已暂停，手机将无法连接", "Service paused — phones cannot connect"));
                }
                else
                {
                    await _viewModel.Server.StartAsync();
                    ShowBalloon(Loc.T("服务已恢复", "Service resumed"));
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "托盘切换服务状态失败");
                ShowBalloon(Loc.T("切换服务状态失败，详见日志", "Failed to toggle the service — see logs"));
            }

            await _viewModel.RefreshAsync();
            UpdateMenuText();
        }

        private void ExitApplication()
        {
            _icon.Visible = false;

            // 先解除"关闭到托盘"，否则 Shutdown 关窗时会被 OnClosing 拦回去
            if (Application.Current?.MainWindow is MainWindow main)
            {
                main.AllowClose = true;
            }

            Application.Current?.Shutdown();
        }

        private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.ServiceStatusText) or nameof(MainViewModel.ServiceTone))
            {
                UpdateMenuText();
            }
        }

        private void UpdateMenuText()
        {
            var snapshot = _viewModel.Server.Snapshot();
            var running = snapshot.State == Core.Diagnostics.ServiceState.Running;

            _statusItem.Header = running ? Loc.T("服务运行中", "Service running") : Loc.T("服务已暂停", "Service paused");
            _serviceItem.Header = running ? Loc.T("暂停服务", "Pause service") : Loc.T("恢复服务", "Resume service");
            _icon.Text = running
                ? Loc.T("WirelessCable · 服务运行中", "WirelessCable · service running")
                : Loc.T("WirelessCable · 服务已暂停", "WirelessCable · service paused");
        }

        public void ShowBalloon(string text)
        {
            try
            {
                _icon.BalloonTipTitle = "WirelessCable";
                _icon.BalloonTipText = text;
                _icon.ShowBalloonTip(2500);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "显示托盘气泡失败");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
