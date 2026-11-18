namespace WirelessCable.Desktop.ViewModels;

/// <summary>首页视图模型。四个功能入口与设备概览都直接绑定 Shell，不复制状态。</summary>
public sealed class HomeViewModel(MainViewModel shell)
{
    public MainViewModel Shell { get; } = shell;
}
