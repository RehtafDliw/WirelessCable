using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>
/// GET /api/v1/health 的负载。字段全部来自真实运行状态。
///
/// 分两级下发（K5）：
/// <list type="bullet">
/// <item><b>匿名</b>：只给 <c>status / authenticated / protocolVersion / appVersion / port / certificateFingerprint</c>。
/// 端口与指纹本就随 mDNS 广播、且在 TLS 握手阶段就暴露，不构成额外泄露；</item>
/// <item><b>已认证</b>：额外下发运行时长、数据库状态、活跃会话数、能力列表——这些属于运行态情报，
/// 只有配对过的设备才需要。</item>
/// </list>
/// 未下发的字段为 null，序列化时直接省略（客户端一律给默认值）。
/// </summary>
public sealed class HealthInfo
{
    /// <summary>服务状态："running" / "starting" / "stopping"。</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>本次请求是否带有有效设备令牌。客户端据此判断详情字段是否可用。</summary>
    [JsonPropertyName("authenticated")]
    public required bool Authenticated { get; init; }

    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; init; }

    /// <summary>应用版本。</summary>
    [JsonPropertyName("appVersion")]
    public required string AppVersion { get; init; }

    /// <summary>实际监听端口（与 mDNS SRV 一致，用于确认端口回退后的真实值）。</summary>
    [JsonPropertyName("port")]
    public required int Port { get; init; }

    /// <summary>证书指纹（SHA-256/DER，小写十六进制）。TLS 握手阶段即公开。</summary>
    [JsonPropertyName("certificateFingerprint")]
    public required string CertificateFingerprint { get; init; }

    /// <summary>服务已运行秒数。仅认证后下发。</summary>
    [JsonPropertyName("uptimeSeconds")]
    public long? UptimeSeconds { get; init; }

    /// <summary>数据库是否已初始化并可读写。仅认证后下发。</summary>
    [JsonPropertyName("databaseReady")]
    public bool? DatabaseReady { get; init; }

    /// <summary>当前活跃会话数。仅认证后下发。</summary>
    [JsonPropertyName("activeSessions")]
    public int? ActiveSessions { get; init; }

    /// <summary>能力标识。仅认证后下发。</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; init; }
}
