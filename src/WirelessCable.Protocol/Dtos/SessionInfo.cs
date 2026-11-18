using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>
/// 设备会话信息（阶段 2 配对后由服务端颁发）。
/// 阶段 1 仅定义契约，接口尚未开放。
/// </summary>
public sealed class SessionInfo
{
    /// <summary>会话 ID。</summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>对端设备 ID。</summary>
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    /// <summary>对端设备显示名。</summary>
    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }

    /// <summary>是否已成为受信设备。</summary>
    [JsonPropertyName("paired")]
    public required bool Paired { get; init; }

    /// <summary>会话签发时间（UTC 毫秒）。</summary>
    [JsonPropertyName("issuedAtUnixMs")]
    public required long IssuedAtUnixMs { get; init; }

    /// <summary>会话到期时间（UTC 毫秒）。</summary>
    [JsonPropertyName("expiresAtUnixMs")]
    public required long ExpiresAtUnixMs { get; init; }

    /// <summary>该会话被授予的能力。</summary>
    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<string> Capabilities { get; init; }
}
