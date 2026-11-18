using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Http;
using WirelessCable.Server.Middleware;
using WirelessCable.Server.Pairing;
using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.Endpoints;

/// <summary>
/// 受信设备与会话接口。全部在该中间件之后，必然已认证。
/// </summary>
public static class TrustedDeviceEndpoints
{
    public static void MapTrustedDeviceEndpoints(this WebApplication app)
    {
        app.MapGet(ProtocolConstants.DevicesPath, ListAsync);
        app.MapDelete(ProtocolConstants.DevicesPath + "/{deviceId}", RevokeSelfAsync);
        app.MapGet(ProtocolConstants.ApiPrefix + "/session/me", MeAsync);
        app.MapPost(ProtocolConstants.HeartbeatPath, HeartbeatAsync);
    }

    private static async Task ListAsync(
        HttpContext context,
        DeviceRepository devices,
        PairingService pairing,
        CancellationToken cancellationToken)
    {
        var all = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var items = all
            .Where(static d => d.IsPaired && !d.IsRevoked)
            .Select(d => ToDto(d, now))
            .ToArray();

        await ApiResponseWriter.WriteSuccessAsync(context, items, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var device = DeviceAuthMiddleware.GetDevice(context);
        if (device is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status401Unauthorized,
                ErrorCodes.Unauthorized,
                "需要有效的设备凭据。").ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter
            .WriteSuccessAsync(context, ToDto(device, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RevokeSelfAsync(
        HttpContext context,
        string deviceId,
        DeviceRepository devices,
        CancellationToken cancellationToken)
    {
        var current = DeviceAuthMiddleware.GetDevice(context);

        // 设备只能撤销自己：跨设备撤销必须由本机界面操作
        if (current is null || !string.Equals(current.DeviceId, deviceId, StringComparison.Ordinal))
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                ErrorCodes.Forbidden,
                "只能撤销本设备的授权。").ConfigureAwait(false);
            return;
        }

        await devices.SetRevokedAsync(deviceId, true, cancellationToken).ConfigureAwait(false);

        Serilog.Log.Information("设备主动撤销授权 device={DeviceId}", deviceId);

        await ApiResponseWriter.WriteSuccessAsync(context, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HeartbeatAsync(
        HttpContext context,
        DeviceRepository devices,
        PairingService pairing,
        CancellationToken cancellationToken)
    {
        var device = DeviceAuthMiddleware.GetDevice(context);
        if (device is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status401Unauthorized,
                ErrorCodes.Unauthorized,
                "需要有效的设备凭据。").ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await devices.UpdateLastSeenAsync(device.DeviceId, now, cancellationToken).ConfigureAwait(false);

        var payload = new HeartbeatResponseDto
        {
            SessionState = "active",
            NextHeartbeatSeconds = ProtocolConstants.HeartbeatIntervalSeconds,
            DeviceOnline = true,
            PendingPairingRequests = pairing.PendingCount,
        };

        await ApiResponseWriter.WriteSuccessAsync(context, payload, cancellationToken).ConfigureAwait(false);
    }

    private static TrustedDeviceDto ToDto(Storage.Entities.StoredDevice device, DateTimeOffset now)
    {
        var online = device.LastSeenUtc is not null
                     && (now - device.LastSeenUtc.Value).TotalSeconds <= ProtocolConstants.DeviceOfflineAfterSeconds;

        return new TrustedDeviceDto
        {
            DeviceId = device.DeviceId,
            DisplayName = device.DisplayName,
            Platform = device.Platform,
            IsOnline = online,
            PairedAtUnixMs = (device.PairedAtUtc ?? device.CreatedAtUtc).ToUnixTimeMilliseconds(),
            LastSeenUnixMs = device.LastSeenUtc?.ToUnixTimeMilliseconds(),
        };
    }
}
