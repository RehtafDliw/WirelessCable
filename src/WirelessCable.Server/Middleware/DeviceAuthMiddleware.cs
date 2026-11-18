using Microsoft.AspNetCore.Http;
using WirelessCable.Protocol;
using WirelessCable.Server.Security;
using WirelessCable.Storage.Entities;

namespace WirelessCable.Server.Middleware;

/// <summary>
/// 设备令牌校验。受保护路径必须携带 <c>Authorization: Bearer &lt;token&gt;</c>。
///
/// 未配对/令牌无效 → 401；已撤销 → 403 device_revoked（让客户端能区分"从未配对"和"被踢掉"）。
/// 阶段 3 的文件端点（/fs、/transfer）一并纳入保护前缀，避免以后忘记加鉴权。
///
/// 三级分类（K5）：
/// <list type="bullet">
/// <item><b>Required</b>：设备列表、心跳、设备信息、文件、传输、会话——无令牌一律拒；</item>
/// <item><b>Optional</b>：<c>/health</c>——无令牌放行但只得到精简负载；带了令牌就按令牌校验，
/// 吊销的设备拿 403，不会"假装健康"；</item>
/// <item><b>None</b>：配对接口与兜底路由，由各自端点自行约束。</item>
/// </list>
/// </summary>
public sealed class DeviceAuthMiddleware
{
    /// <summary>HttpContext.Items 中存放已认证设备的键。</summary>
    public const string DeviceItemKey = "wc.device";

    /// <summary>
    /// 业务请求刷新 last_seen 的最小间隔。
    ///
    /// 分块上传每秒好几次，每次都写库没必要；5 秒一次足够让"正在干活"的手机不被判离线。
    /// </summary>
    private static readonly TimeSpan LastSeenRefreshInterval = TimeSpan.FromSeconds(5);

    private static readonly string[] ProtectedPrefixes =
    [
        ProtocolConstants.DevicesPath,
        ProtocolConstants.HeartbeatPath,
        ProtocolConstants.DeviceInfoPath,
        ProtocolConstants.ApiPrefix + "/fs",
        ProtocolConstants.ApiPrefix + "/transfer",
        ProtocolConstants.ApiPrefix + "/session",
    ];

    private enum AuthRequirement
    {
        None,
        Optional,
        Required,
    }

    private readonly RequestDelegate _next;

    public DeviceAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        DeviceAuthenticator authenticator,
        Storage.Repositories.DeviceRepository devices)
    {
        var requirement = Classify(context.Request.Path);
        if (requirement == AuthRequirement.None)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 可选鉴权：完全没带凭据就按匿名处理，带了就必须是有效凭据
        if (requirement == AuthRequirement.Optional && context.Request.Headers.Authorization.Count == 0)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var result = await authenticator
            .AuthenticateAsync(context.Request.Headers.Authorization, context.RequestAborted)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case DeviceAuthStatus.Ok when result.Device is not null:
                // 用户在电脑上"断开"了这台设备：配对与授权都还在，只是先别让它连。
                // 423 Locked 而不是 401/403——手机据此停在"被断开"状态，不会引导重新配对。
                if (result.Device.SessionPaused)
                {
                    await Http.ApiResponseWriter.WriteErrorAsync(
                        context,
                        StatusCodes.Status423Locked,
                        ErrorCodes.SessionPaused,
                        "这台电脑已断开该设备。请在电脑首页点「连接」恢复。",
                        retryable: true).ConfigureAwait(false);
                    return;
                }

                context.Items[DeviceItemKey] = result.Device;

                /*
                 * 任何一次带令牌的请求都算"它还活着"。
                 *
                 * 以前只有 /api/v1/heartbeat 会刷新 last_seen：手机跑一个长作业
                 * （下载整个文件夹、传 1 GB 文件）时主循环卡在作业里，10 分钟不发心跳，
                 * 桌面端就会把一台正在干活的手机判成离线。判定窗口从 90 秒缩到 30 秒
                 * 之后这条更致命，所以在这里补上——**按 5 秒节流**，不会每次分块上传都写库。
                 */
                var now = DateTimeOffset.UtcNow;
                if (result.Device.LastSeenUtc is null ||
                    now - result.Device.LastSeenUtc.Value > LastSeenRefreshInterval)
                {
                    await devices
                        .UpdateLastSeenAsync(result.Device.DeviceId, now, context.RequestAborted)
                        .ConfigureAwait(false);
                }

                break;

            case DeviceAuthStatus.Revoked:
                await Http.ApiResponseWriter.WriteErrorAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    ErrorCodes.DeviceRevoked,
                    "该设备的授权已被撤销，请重新配对。",
                    retryable: false).ConfigureAwait(false);
                return;

            default:
                await Http.ApiResponseWriter.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    ErrorCodes.Unauthorized,
                    "需要有效的设备凭据。",
                    retryable: false).ConfigureAwait(false);
                return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>取当前请求的已认证设备；匿名请求返回 null。</summary>
    public static StoredDevice? GetDevice(HttpContext context)
        => context.Items.TryGetValue(DeviceItemKey, out var value) ? value as StoredDevice : null;

    private static AuthRequirement Classify(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return AuthRequirement.None;
        }

        foreach (var prefix in ProtectedPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return AuthRequirement.Required;
            }
        }

        // health 没有子资源，精确匹配，避免误放行 /api/v1/healthz 之类的拼写
        return value.Equals(ProtocolConstants.HealthPath, StringComparison.OrdinalIgnoreCase)
            ? AuthRequirement.Optional
            : AuthRequirement.None;
    }
}
