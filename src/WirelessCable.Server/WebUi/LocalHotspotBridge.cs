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

    /// <summary>
    /// 关掉应用自己开的热点（用户手动开的不会动）。
    ///
    /// 用户在出码对话框里取消勾选时要调它：否则热点会一直广播到退出应用，
    /// 笔记本白耗电；但只关"我们开的"，别把用户自己在系统设置里开的热点关掉。
    /// 返回 true 表示这次真的关了——调用方据此剔除残留的热点网段地址。
    /// </summary>
    public Func<Task<bool>>? StopAsync { get; set; }

    /// <summary>读"出码时是否开热点"的偏好（存在 settings.json 里）。</summary>
    public Func<bool>? ReadPreference { get; set; }

    /// <summary>记住用户这次的选择，下次出码沿用。</summary>
    public Action<bool>? WritePreference { get; set; }
}
