using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using WirelessCable.Desktop.ViewModels;

namespace WirelessCable.Desktop.Services;

/// <summary>
/// 任务栏角标：有任务在传的时候，在任务栏按钮右下角贴一个小徽章，传完就撤。
///
/// 徽章必须和主图标**长得不一样**。以前这里加载的是
/// <c>assets/WirelessCable.Badge.ico</c>——也就是托盘用的那张「wifi + 文件」图，
/// 图形与任务栏主图标完全一致，叠上去就是"图标盖图标"，用户看到的是两个 logo 重叠。
/// 任务栏 overlay 的语义是「状态标记」，所以这里现画一个橙底白色上下箭头：
/// 尺寸小、透明底、与主图标不冲突。
///
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
            window.TaskbarItemInfo.Overlay = on ? (_image ??= Create()) : null;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "设置任务栏角标失败");
        }
    }

    /// <summary>
    /// 画一枚 32×32 的徽章：白色描边 + 橙色圆底 + 白色上下箭头。
    /// 给 32 而不是 16，让系统在高 DPI 下自己缩，16 会糊。
    /// </summary>
    private static ImageSource? Create()
    {
        if (_loadFailed)
        {
            return null;
        }

        try
        {
            const int size = 32;
            var center = new Point(size / 2.0, size / 2.0);

            var accent = new SolidColorBrush(Color.FromRgb(0xEF, 0x8A, 0x2B));
            accent.Freeze();
            var arrow = new Pen(Brushes.White, 2.6)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            arrow.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // 白圈先把徽章和任务栏底色分开：浅色主题下没有这层就看不见边界
                dc.DrawEllipse(Brushes.White, null, center, 15, 15);
                dc.DrawEllipse(accent, null, center, 12.5, 12.5);

                // 上箭头
                dc.DrawLine(arrow, new Point(11.6, 19.4), new Point(11.6, 11.2));
                dc.DrawLine(arrow, new Point(8.6, 14.4), new Point(11.6, 11.2));
                dc.DrawLine(arrow, new Point(14.6, 14.4), new Point(11.6, 11.2));

                // 下箭头
                dc.DrawLine(arrow, new Point(20.4, 12.6), new Point(20.4, 20.8));
                dc.DrawLine(arrow, new Point(17.4, 17.6), new Point(20.4, 20.8));
                dc.DrawLine(arrow, new Point(23.4, 17.6), new Point(20.4, 20.8));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            Serilog.Log.Debug(ex, "绘制任务栏角标失败");
            return null;
        }
    }
}
