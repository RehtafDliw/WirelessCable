using Microsoft.AspNetCore.Http;
using Serilog;
using WirelessCable.Protocol;

namespace WirelessCable.Server.Middleware;

/// <summary>
/// 全局异常兜底。客户端只拿到稳定错误码与 traceId，异常细节只进日志。
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger = Serilog.Log.ForContext<ExceptionHandlingMiddleware>();

    public ExceptionHandlingMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端主动断开，不算错误，也无法写响应
        }
        catch (BadHttpRequestException ex)
        {
            _logger.Warning(ex, "请求格式非法 {Method} {Path}", context.Request.Method, context.Request.Path.Value);
            await TryWriteAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest,
                "请求格式非法。", retryable: false, details: ex.StatusCode.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "未处理异常 {Method} {Path}", context.Request.Method, context.Request.Path.Value);
            await TryWriteAsync(context, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
                "服务端内部错误。", retryable: true).ConfigureAwait(false);
        }
    }

    private static async Task TryWriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        bool retryable,
        string? details = null)
    {
        if (context.Response.HasStarted)
        {
            // 已经写出部分响应，无法再改状态码；直接断开让客户端感知失败
            context.Abort();
            return;
        }

        context.Response.Clear();
        await Http.ApiResponseWriter
            .WriteErrorAsync(context, statusCode, code, message, retryable, details)
            .ConfigureAwait(false);
    }
}
