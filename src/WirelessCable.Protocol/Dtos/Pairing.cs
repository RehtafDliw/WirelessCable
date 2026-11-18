using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>配对请求状态。字符串值稳定，双端共用。</summary>
public static class PairingStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Completed = "completed";
    public const string Locked = "locked";
}

/// <summary>
/// 二维码里的电脑热点信息。
///
/// 用途：手机与电脑不在同一网络时，电脑开一个热点让手机连过来，
/// 之后就回到"同一局域网"的常规路径（配对、心跳、传输都不用改）。
/// 这是可选的：旧客户端会忽略它，新客户端遇到没有它的二维码照旧处理。
/// </summary>
public sealed class QrHotspotDto
{
    /// <summary>热点名称（SSID）。</summary>
    [JsonPropertyName("ssid")]
    public required string Ssid { get; init; }

    /// <summary>WPA2 口令（8 位数字）。</summary>
    [JsonPropertyName("passphrase")]
    public required string Passphrase { get; init; }

    /// <summary>热点网段上电脑的 IPv4 地址，也是手机连上热点后要访问的地址。</summary>
    [JsonPropertyName("address")]
    public required string Address { get; init; }
}

/// <summary>
/// 二维码负载。仅用于带外传递"如何连上这台电脑"，不含任何长期密钥。
/// </summary>
public sealed class QrPayloadDto
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }

    /// <summary>局域网地址，按优先级排列；客户端逐个尝试。</summary>
    [JsonPropertyName("addresses")]
    public required IReadOnlyList<string> Addresses { get; init; }

    /// <summary>电脑热点（可选）；有值时手机应先连热点，再按地址配对。</summary>
    [JsonPropertyName("hotspot")]
    public QrHotspotDto? Hotspot { get; init; }

    [JsonPropertyName("port")]
    public required int Port { get; init; }

    /// <summary>TLS 证书指纹（SHA-256/DER 小写十六进制），用于固定信任。</summary>
    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    /// <summary>一次性随机数，服务端只接受一次，防重放。</summary>
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }

    [JsonPropertyName("expiresAtUnixMs")]
    public required long ExpiresAtUnixMs { get; init; }
}

/// <summary>发起配对。</summary>
public sealed class PairingRequestDto : BaseRequest
{
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    /// <summary>客户端长期身份公钥（ECDSA P-256，Base64 的 SPKI）。</summary>
    [JsonPropertyName("identityPublicKey")]
    public required string IdentityPublicKey { get; init; }

    /// <summary>客户端本次配对的临时 ECDH 公钥（P-256，Base64 的 SPKI）。</summary>
    [JsonPropertyName("ephemeralPublicKey")]
    public required string EphemeralPublicKey { get; init; }

    /// <summary>二维码里的一次性随机数；手动输入地址时为空。</summary>
    [JsonPropertyName("qrNonce")]
    public string? QrNonce { get; init; }

    /// <summary>客户端随机数，参与会话转录哈希。</summary>
    [JsonPropertyName("clientNonce")]
    public required string ClientNonce { get; init; }
}

/// <summary>配对挑战：服务端临时公钥与请求 ID。**不包含验证码**。</summary>
public sealed class PairingChallengeDto
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("serverEphemeralPublicKey")]
    public required string ServerEphemeralPublicKey { get; init; }

    [JsonPropertyName("serverNonce")]
    public required string ServerNonce { get; init; }

    [JsonPropertyName("expiresAtUnixMs")]
    public required long ExpiresAtUnixMs { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("serverDeviceId")]
    public required string ServerDeviceId { get; init; }

    [JsonPropertyName("serverName")]
    public required string ServerName { get; init; }

    [JsonPropertyName("pairingSuite")]
    public required string PairingSuite { get; init; }
}

/// <summary>提交配对确认：验证码派生的 HMAC + 长期身份私钥签名。</summary>
public sealed class PairingConfirmRequestDto : BaseRequest
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    /// <summary>Base64(HMAC-SHA256(Kc, "confirm:" + code))。</summary>
    [JsonPropertyName("confirmation")]
    public required string Confirmation { get; init; }

    /// <summary>Base64(ECDSA-P256-SHA256(identityKey, transcript))。</summary>
    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}

/// <summary>配对成功：返回用协商密钥封装的设备令牌。</summary>
public sealed class PairingConfirmResponseDto
{
    /// <summary>Base64(nonce(12) || tag(16) || ciphertext)，AES-256-GCM。</summary>
    [JsonPropertyName("sealedToken")]
    public required string SealedToken { get; init; }

    [JsonPropertyName("serverDeviceId")]
    public required string ServerDeviceId { get; init; }

    [JsonPropertyName("pairedAtUnixMs")]
    public required long PairedAtUnixMs { get; init; }

    /// <summary>下次心跳建议间隔（秒）。</summary>
    [JsonPropertyName("heartbeatIntervalSeconds")]
    public required int HeartbeatIntervalSeconds { get; init; }
}

/// <summary>配对请求的只读视图，供 Windows 界面展示队列。</summary>
public sealed class PairingSessionViewDto
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("deviceName")]
    public required string DeviceName { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    /// <summary>六位配对码。只在本机界面显示，绝不出现在网络响应里。</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("createdAtUnixMs")]
    public required long CreatedAtUnixMs { get; init; }

    [JsonPropertyName("expiresAtUnixMs")]
    public required long ExpiresAtUnixMs { get; init; }

    [JsonPropertyName("attempts")]
    public required int Attempts { get; init; }
}

/// <summary>配对请求状态查询结果（不含验证码）。</summary>
public sealed class PairingStatusDto
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("expiresAtUnixMs")]
    public required long ExpiresAtUnixMs { get; init; }
}

/// <summary>受信设备。</summary>
public sealed class TrustedDeviceDto
{
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("isOnline")]
    public required bool IsOnline { get; init; }

    [JsonPropertyName("pairedAtUnixMs")]
    public required long PairedAtUnixMs { get; init; }

    [JsonPropertyName("lastSeenUnixMs")]
    public long? LastSeenUnixMs { get; init; }
}

/// <summary>心跳响应。</summary>
public sealed class HeartbeatResponseDto
{
    [JsonPropertyName("sessionState")]
    public required string SessionState { get; init; }

    [JsonPropertyName("nextHeartbeatSeconds")]
    public required int NextHeartbeatSeconds { get; init; }

    [JsonPropertyName("deviceOnline")]
    public required bool DeviceOnline { get; init; }

    [JsonPropertyName("pendingPairingRequests")]
    public required int PendingPairingRequests { get; init; }
}
