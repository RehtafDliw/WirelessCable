using WirelessCable.Infrastructure.Security;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.Security;

/// <summary>设备认证结果。</summary>
public enum DeviceAuthStatus
{
    Ok = 0,
    MissingCredentials,
    InvalidToken,
    Revoked,
}

public sealed record DeviceAuthResult(DeviceAuthStatus Status, StoredDevice? Device)
{
    public bool IsAuthenticated => Status == DeviceAuthStatus.Ok;
}

/// <summary>
/// 解析 <c>Authorization: Bearer &lt;token&gt;</c>。令牌只以 SHA-256 落库，
/// 即使数据库被读走也无法还原出可用令牌。
/// </summary>
public sealed class DeviceAuthenticator
{
    private const string BearerPrefix = "Bearer ";

    private readonly DeviceRepository _devices;

    public DeviceAuthenticator(DeviceRepository devices) => _devices = devices;

    public async Task<DeviceAuthResult> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return new DeviceAuthResult(DeviceAuthStatus.MissingCredentials, null);
        }

        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceAuthResult(DeviceAuthStatus.InvalidToken, null);
        }

        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        if (token.Length < 16)
        {
            return new DeviceAuthResult(DeviceAuthStatus.InvalidToken, null);
        }

        var hash = PairingCrypto.HashToken(token);
        var device = await _devices.FindByTokenHashAsync(hash, cancellationToken).ConfigureAwait(false);

        if (device is null || !device.IsPaired)
        {
            return new DeviceAuthResult(DeviceAuthStatus.InvalidToken, null);
        }

        if (device.IsRevoked)
        {
            return new DeviceAuthResult(DeviceAuthStatus.Revoked, device);
        }

        return new DeviceAuthResult(DeviceAuthStatus.Ok, device);
    }
}
