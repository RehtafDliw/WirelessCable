namespace WirelessCable.Server.Identity;

/// <summary>本机身份：稳定设备 ID + 显示名。</summary>
/// <param name="DeviceId">GUID（N 格式），首次启动生成并持久化到 AppConfig。</param>
/// <param name="DisplayName">用户可见名称。</param>
public sealed record DeviceIdentity(string DeviceId, string DisplayName)
{
    /// <summary>设备 ID 的前 4 位，大写。两台电脑重名时靠它区分。</summary>
    public string ShortId => DeviceId.Length >= 4
        ? DeviceId[..4].ToUpperInvariant()
        : DeviceId.ToUpperInvariant();

    /// <summary>
    /// 对外广播用的名字：`机器名 · 短ID`。
    /// 显示名是给人看的，可能两台电脑一样（都叫 DESKTOP-XXXX）；
    /// 手机列表里靠这 4 位区分，否则用户根本不知道自己连的是哪一台。
    /// </summary>
    public string UniqueName => $"{DisplayName} · {ShortId}";
}
