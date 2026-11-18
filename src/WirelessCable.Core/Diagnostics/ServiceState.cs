namespace WirelessCable.Core.Diagnostics;

/// <summary>本地服务生命周期状态。只描述本机进程，不参与网络协议。</summary>
public enum ServiceState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Failed = 4,
}
