using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>
/// 所有响应的公共字段。出现在响应体顶层，不嵌套。
/// </summary>
public class BaseResponse
{
    /// <summary>服务端协议版本。</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;

    /// <summary>本次请求的追踪 ID，与响应头 X-WC-Trace-Id 一致。</summary>
    [JsonPropertyName("traceId")]
    public string TraceId { get; init; } = string.Empty;

    /// <summary>服务端 UTC 毫秒时间戳，供客户端校准时钟偏差。</summary>
    [JsonPropertyName("serverTimeUnixMs")]
    public long ServerTimeUnixMs { get; init; }
}
