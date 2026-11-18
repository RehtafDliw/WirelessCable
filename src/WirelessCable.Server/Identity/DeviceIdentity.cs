namespace WirelessCable.Server.Identity;

/// <summary>本机身份：稳定设备 ID + 显示名。</summary>
/// <param name="DeviceId">GUID（N 格式），首次启动生成并持久化到 AppConfig。</param>
/// <param name="DisplayName">用户可见名称。</param>
public sealed record DeviceIdentity(string DeviceId, string DisplayName);
