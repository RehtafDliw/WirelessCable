using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>
/// 所有请求体的基类。字段可选，但一旦出现必须符合约定。
/// </summary>
public abstract class BaseRequest
{
    /// <summary>客户端生成的请求 ID，用于日志串联；可选。</summary>
    [JsonPropertyName("clientRequestId")]
    public string? ClientRequestId { get; init; }

    /// <summary>客户端协议版本；缺省视为当前版本。</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;
}
