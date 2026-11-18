using Microsoft.AspNetCore.Http;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;

namespace WirelessCable.Server.Http;

/// <summary>统一写出 ApiResult 信封，保证所有响应 Content-Type 与编码一致。</summary>
public static class ApiResponseWriter
{
    public const string JsonContentType = "application/json; charset=utf-8";

    public static Task WriteAsync<T>(
        HttpContext context,
        int statusCode,
        ApiResult<T> result,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        context.Response.Headers[ProtocolConstants.TraceIdHeader] = result.TraceId;

        return context.Response.WriteAsJsonAsync(
            result,
            WcJson.Options,
            JsonContentType,
            cancellationToken);
    }

    public static Task WriteSuccessAsync<T>(
        HttpContext context,
        T data,
        CancellationToken cancellationToken = default)
        => WriteAsync(context, StatusCodes.Status200OK, ApiResult<T>.Success(data, context.TraceIdentifier, DateTimeOffset.UtcNow), cancellationToken);

    public static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable = false,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        var error = new ErrorResponse
        {
            Code = code,
            Message = message,
            Retryable = retryable,
            Details = details,
        };

        var result = ApiResult<object>.Failure(error, context.TraceIdentifier, DateTimeOffset.UtcNow);
        return WriteAsync(context, statusCode, result, cancellationToken);
    }
}
