using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using WirelessCable.Desktop.ViewModels;

namespace WirelessCable.Desktop.Services;

/// <summary>
/// 任务栏角标：有任务在传的时候，在任务栏按钮右下角贴一张图形角标，传完就撤。
///
/// 图与托盘图标共用 <c>assets/WirelessCable.Badge.ico</c>（只有图形、透明底）。
/// 用 <see cref="Window.TaskbarItemInfo"/> 而不是托盘气泡：角标是"状态"，气泡是"事件"，
/// 传输可能持续几分钟，弹气泡会一直骚扰。
/// </summary>
internal static class TaskbarBadge
{
    private static ImageSource? _image;
    private static bool _loadFailed;

    /// <summary>把窗口接到视图模型的传输状态上。重复调用安全（同一个窗口只挂一次由调用方保证）。</summary>
    public static void Bind(Window window, MainViewModel viewModel)
    {
        void Apply() => Set(window, viewModel.HasActiveTransfer);

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.HasActiveTransfer))
            {
                Apply();
            }
        }

        viewModel.PropertyChanged += OnChanged;
        window.Closed += (_, _) => viewModel.PropertyChanged -= OnChanged;
        Apply();
    }

    private static void Set(Window window, bool on)
    {
        try
        {
            window.TaskbarItemInfo ??= new TaskbarItemInfo();
            window.TaskbarItemInfo.Overlay = on ? (_image ??= Load()) : null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "设置任务栏角标失败");
        }
    }

    private static ImageSource? Load()
    {
        if (_loadFailed)
        {
            return null;
        }

        try
        {
            var stream = Application.GetResourceStream(
                new Uri("assets/WirelessCable.Badge.ico", UriKind.Relative))?.Stream;

            if (stream is null)
            {
                _loadFailed = true;
                return null;
            }

            using (stream)
            // 32px 那帧：高 DPI 下系统会自己缩，直接给 16px 会糊
            using (var icon = new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32)))
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            Serilog.Log.Debug(ex, "加载任务栏角标失败");
            return null;
        }
    }
}
