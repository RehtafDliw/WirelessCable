namespace WirelessCable.Server;

/// <summary>能力标识。客户端据此判断服务端支持的功能，不靠猜。</summary>
public static class Capabilities
{
    public const string Health = "health";
    public const string DeviceInfo = "device_info";
    public const string Pairing = "pairing";
    public const string Devices = "devices";
    public const string Session = "session";
    public const string Transfer = "transfer";

    /// <summary>阶段 1 能力集合。</summary>
    public static readonly string[] Stage1 = [Health, DeviceInfo];

    /// <summary>阶段 2 能力集合。</summary>
    public static readonly string[] Stage2 = [Health, DeviceInfo, Pairing, Devices, Session];

    /// <summary>阶段 3 能力集合（当前生效）。</summary>
    public static readonly string[] Stage3 = [Health, DeviceInfo, Pairing, Devices, Session, Transfer];
}
