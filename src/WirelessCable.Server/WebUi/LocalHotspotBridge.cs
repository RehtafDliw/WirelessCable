namespace WirelessCable.Server.WebUi;

/// <summary>一次热点开启的结果，字段与桌面端的 HotspotOffer 对齐。</summary>
/// <param name="Address">热点网段上本机的 IPv4 地址，拿不到为 null。</param>
public sealed record LocalHotspotOffer(string Ssid, string Passphrase, string? Address);

/// <summary>
/// 热点能力的桥。
///
/// 开热点是 Windows 的 WinRT 能力，只有桌面端有；Server 项目不能反向引用桌面端。
/// 所以由桌面端在启动时把自己的实现挂上来，新版界面的「扫码配对」再通过这里调用。
/// 没挂（例如服务被别的宿主托管）时出码仍然可用，只是二维码里不含热点兜底。
/// </summary>
public sealed class LocalHotspotBridge
{
    /// <summary>确保热点开着。返回 null 表示开不出来（这时二维码里不放热点凭据）。</summary>
    public Func<CancellationToken, Task<LocalHotspotOffer?>>? EnsureAsync { get; set; }

    /// <summary>开不出来时给用户看的一句话原因。</summary>
    public Func<string>? UnavailableReason { get; set; }
}
