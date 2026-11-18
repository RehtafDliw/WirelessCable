using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>设备平台类型。序列化为小写字符串。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlatformKind>))]
public enum PlatformKind
{
    [JsonStringEnumMemberName("windows")]
    Windows = 0,

    [JsonStringEnumMemberName("android")]
    Android = 1,

    /// <summary>预留：iOS 客户端遵守同一协议。</summary>
    [JsonStringEnumMemberName("ios")]
    Ios = 2,
}

/// <summary>
/// 设备身份与能力描述。GET /api/v1/device/info 的负载。
/// </summary>
public sealed class DeviceInfo
{
    /// <summary>设备唯一 ID（GUID，无连字符），首次启动生成并持久化。</summary>
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    /// <summary>用户可见设备名。</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>平台类型。</summary>
    [JsonPropertyName("platform")]
    public required PlatformKind Platform { get; init; }

    /// <summary>系统版本，如 "10.0.22631"。</summary>
    [JsonPropertyName("osVersion")]
    public required string OsVersion { get; init; }

    /// <summary>应用版本，如 "0.1.0"。</summary>
    [JsonPropertyName("appVersion")]
    public required string AppVersion { get; init; }

    /// <summary>该设备实现的协议版本。</summary>
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; init; }

    /// <summary>服务端证书指纹：SHA-256(DER) 小写十六进制，64 字符，无分隔符。</summary>
    [JsonPropertyName("certificateFingerprint")]
    public required string CertificateFingerprint { get; init; }

    /// <summary>局域网 IPv4 地址列表，供直连兜底与二维码使用。</summary>
    [JsonPropertyName("addresses")]
    public required IReadOnlyList<string> Addresses { get; init; }

    /// <summary>HTTPS 监听端口。</summary>
    [JsonPropertyName("port")]
    public required int Port { get; init; }

    /// <summary>能力标识。客户端据此判断可用功能，不猜测。</summary>
    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }
}
