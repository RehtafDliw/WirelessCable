using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using WirelessCable.Protocol;
using WirelessCable.Server.Security;

namespace WirelessCable.Server.Middleware;

/// <summary>
/// 请求频率限制。回环地址（本机 UI / 集成测试）不计入配额，
/// 否则界面轮询健康状态会把自己锁死。
///
/// 分块传输路径不计入配额：一个 5 GB 文件就是 640 次请求，按 60 req/min 限流等于禁止大文件。
/// 这些路径已由设备令牌保护，且每次只处理固定大小的分块，不会放大成 DoS。
/// </summary>
public sealed partial class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly FixedWindowRateLimiter _limiter;

    public RateLimitMiddleware(RequestDelegate next, FixedWindowRateLimiter limiter)
    {
        _next = next;
        _limiter = limiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote) && !IsChunkTransfer(context.Request.Path))
        {
            var key = remote.ToString();
            if (!_limiter.TryAcquire(key, out var retryAfter))
            {
                context.Response.Headers.RetryAfter = retryAfter.ToString();
                await Http.ApiResponseWriter.WriteErrorAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    ErrorCodes.RateLimited,
                    "请求过于频繁。",
                    retryable: true,
                    details: $"retryAfterSeconds={retryAfter}").ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsChunkTransfer(PathString path)
        => path.Value is { } value && ChunkPathPattern().IsMatch(value);

    // 分块本体与分块回执都不计入配额（5 GB = 640 块，每块两个请求）
    [GeneratedRegex(@"^/api/v1/transfer/tasks/[^/]+/chunks(/\d+(/ack)?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ChunkPathPattern();
}
