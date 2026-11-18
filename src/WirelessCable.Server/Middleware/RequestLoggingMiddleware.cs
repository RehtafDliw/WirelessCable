using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace WirelessCable.Server.Middleware;

/// <summary>
/// 请求日志：方法、路径、状态码、耗时、客户端地址。
/// 不记录请求体与 Authorization（阶段 2 起会出现设备令牌）。
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger = Serilog.Log.ForContext<RequestLoggingMiddleware>();

    public RequestLoggingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            var status = context.Response.StatusCode;
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (status >= 500)
            {
                _logger.Error("HTTP {Method} {Path} -> {Status} in {Elapsed:F1}ms from {Client} ({TraceId})",
                    context.Request.Method, context.Request.Path.Value, status, elapsed, client, context.TraceIdentifier);
            }
            else if (status >= 400)
            {
                _logger.Warning("HTTP {Method} {Path} -> {Status} in {Elapsed:F1}ms from {Client} ({TraceId})",
                    context.Request.Method, context.Request.Path.Value, status, elapsed, client, context.TraceIdentifier);
            }
            else
            {
                _logger.Information("HTTP {Method} {Path} -> {Status} in {Elapsed:F1}ms from {Client} ({TraceId})",
                    context.Request.Method, context.Request.Path.Value, status, elapsed, client, context.TraceIdentifier);
            }
        }
    }
}
