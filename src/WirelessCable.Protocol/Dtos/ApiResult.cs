using System.Text.Json.Serialization;

namespace WirelessCable.Protocol.Dtos;

/// <summary>
/// 统一响应信封：成功时 <see cref="Ok"/> 为 true 且 <see cref="Data"/> 有值；
/// 失败时 <see cref="Ok"/> 为 false 且 <see cref="Error"/> 有值。二者互斥。
/// </summary>
/// <typeparam name="T">负载类型。</typeparam>
public sealed class ApiResult<T> : BaseResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("error")]
    public ErrorResponse? Error { get; init; }

    /// <summary>构造成功结果。</summary>
    public static ApiResult<T> Success(T data, string traceId, DateTimeOffset now) => new()
    {
        Ok = true,
        Data = data,
        Error = null,
        TraceId = traceId,
        ServerTimeUnixMs = now.ToUnixTimeMilliseconds(),
        ProtocolVersion = ProtocolConstants.CurrentVersion,
    };

    /// <summary>构造失败结果。</summary>
    public static ApiResult<T> Failure(ErrorResponse error, string traceId, DateTimeOffset now) => new()
    {
        Ok = false,
        Data = default,
        Error = error,
        TraceId = traceId,
        ServerTimeUnixMs = now.ToUnixTimeMilliseconds(),
        ProtocolVersion = ProtocolConstants.CurrentVersion,
    };

    /// <summary>构造失败结果（常用简写）。</summary>
    public static ApiResult<T> Failure(string code, string message, string traceId, DateTimeOffset now, bool retryable = false, string? details = null)
        => Failure(new ErrorResponse { Code = code, Message = message, Retryable = retryable, Details = details }, traceId, now);
}
