using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>
/// 统一错误对象。<see cref="Code"/> 取自 <see cref="ErrorCodes"/>。
/// </summary>
public sealed class ErrorResponse
{
    /// <summary>稳定错误码，客户端按此分支处理，不要匹配 message。</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>面向人的简短说明，可本地化，不保证稳定。</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>附加细节（如具体字段名、被拒路径原因码）；可选。</summary>
    [JsonPropertyName("details")]
    public string? Details { get; init; }

    /// <summary>客户端重试是否可能成功。</summary>
    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
}
