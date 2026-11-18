namespace WirelessCable.Desktop.ViewModels;

/// <summary>设备页视图模型。阶段 2 才接入配对流程，当前只展示已配对设备。</summary>
public sealed class DevicesViewModel(MainViewModel shell)
{
    public MainViewModel Shell { get; } = shell;
}
