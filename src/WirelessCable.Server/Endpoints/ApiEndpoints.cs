using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WirelessCable.Core.Diagnostics;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Http;
using WirelessCable.Server.Identity;
using WirelessCable.Server.Middleware;
using WirelessCable.Server.Runtime;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Server.Endpoints;

/// <summary>
/// 两个只读接口：健康检查与设备信息。
/// 文件、目录、传输相关接口一律不注册，未授权能力不得提前暴露。
///
/// 暴露面（K5）：
/// <list type="bullet">
/// <item><c>health</c> 允许匿名，但只回基础字段；带令牌才拿到运行态细节；</item>
/// <item><c>device/info</c> 受 <see cref="DeviceAuthMiddleware"/> 保护——设备名、操作系统版本、
/// 网卡地址、能力清单不该对未配对的主机开放。</item>
/// </list>
/// </summary>
public static class ApiEndpoints
{
    public static void MapWirelessCableEndpoints(this WebApplication app)
    {
        app.MapGet(ProtocolConstants.HealthPath, HandleHealthAsync);
        app.MapGet(ProtocolConstants.DeviceInfoPath, HandleDeviceInfoAsync);

        // 阶段 2：配对与受信设备（后者由 DeviceAuthMiddleware 保护）
        app.MapPairingEndpoints();
        app.MapTrustedDeviceEndpoints();

        // 阶段 3：文件传输（整段都在 /transfer 前缀内，同样受设备令牌保护）
        app.MapTransferEndpoints();

        // 阶段 5：文件管理（/fs 前缀，同样受设备令牌保护）
        app.MapFileEndpoints();

        app.MapFallback(HandleFallbackAsync);
    }

    private static async Task HandleHealthAsync(
        HttpContext context,
        ServerRuntime runtime,
        SqliteDatabaseFactory database,
        CancellationToken cancellationToken)
    {
        var snapshot = runtime.Snapshot();
        var authenticated = DeviceAuthMiddleware.GetDevice(context) is not null;

        var health = new HealthInfo
        {
            Status = snapshot.State switch
            {
                ServiceState.Running => "running",
                ServiceState.Starting => "starting",
                ServiceState.Stopping => "stopping",
                ServiceState.Failed => "failed",
                _ => "stopped",
            },
            Authenticated = authenticated,
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            AppVersion = snapshot.AppVersion,
            Port = snapshot.Port ?? 0,
            CertificateFingerprint = snapshot.Fingerprint ?? string.Empty,

            // 运行态细节只对已配对设备下发：谁在用、开了多久、库是否正常，都属于本机内部状态。
            UptimeSeconds = authenticated ? snapshot.UptimeSeconds : null,
            DatabaseReady = authenticated ? database.CanReadWrite() : null,
            ActiveSessions = authenticated ? runtime.ActiveSessions : null,
            Capabilities = authenticated ? runtime.Capabilities : null,
        };

        await ApiResponseWriter.WriteSuccessAsync(context, health, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleDeviceInfoAsync(
        HttpContext context,
        ServerRuntime runtime,
        CancellationToken cancellationToken)
    {
        // 中间件已拦截未认证请求；这里再兜一层，防止将来路由调整时把保护漏掉
        if (DeviceAuthMiddleware.GetDevice(context) is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status401Unauthorized,
                ErrorCodes.Unauthorized,
                "需要有效的设备凭据。",
                retryable: false).ConfigureAwait(false);
            return;
        }

        var identity = runtime.Identity;
        var snapshot = runtime.Snapshot();

        if (identity is null || snapshot.State != ServiceState.Running)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.ServiceUnavailable,
                "服务尚未就绪。",
                retryable: true).ConfigureAwait(false);
            return;
        }

        var info = new DeviceInfo
        {
            DeviceId = identity.DeviceId,
            DisplayName = identity.UniqueName,
            Platform = PlatformKind.Windows,
            OsVersion = Environment.OSVersion.Version.ToString(),
            AppVersion = snapshot.AppVersion,
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            CertificateFingerprint = snapshot.Fingerprint ?? string.Empty,
            Addresses = snapshot.Addresses,
            Port = snapshot.Port ?? 0,
            Capabilities = runtime.Capabilities,
        };

        await ApiResponseWriter.WriteSuccessAsync(context, info, cancellationToken).ConfigureAwait(false);
    }

    private static Task HandleFallbackAsync(HttpContext context)
        => ApiResponseWriter.WriteErrorAsync(
            context,
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            "接口不存在。",
            retryable: false,
            details: context.Request.Path.Value);
}
