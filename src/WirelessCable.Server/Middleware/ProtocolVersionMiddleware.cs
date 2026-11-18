using Microsoft.AspNetCore.Http;
using WirelessCable.Protocol;

namespace WirelessCable.Server.Middleware;

/// <summary>
/// 协议版本协商。请求头缺省时按当前版本处理（便于 curl/健康探测），
/// 显式声明了不受支持的版本则返回 426 并给出支持列表。
/// </summary>
public sealed class ProtocolVersionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServerOptions _options;

    public ProtocolVersionMiddleware(RequestDelegate next, ServerOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(ProtocolConstants.VersionHeader, out var values))
        {
            var raw = values.ToString();
            if (!int.TryParse(raw, out var version) || !_options.SupportedProtocolVersions.Contains(version))
            {
                var supported = string.Join(',', _options.SupportedProtocolVersions);
                await Http.ApiResponseWriter.WriteErrorAsync(
                    context,
                    StatusCodes.Status426UpgradeRequired,
                    ErrorCodes.ProtocolVersionUnsupported,
                    $"不支持的协议版本：{raw}。",
                    retryable: false,
                    details: $"supported={supported}").ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
