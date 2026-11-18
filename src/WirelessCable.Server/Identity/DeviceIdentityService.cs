using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.Identity;

/// <summary>解析/生成设备身份。</summary>
public static class DeviceIdentityService
{
    public const string DeviceIdKey = "device.id";
    public const string DisplayNameKey = "device.display_name";

    /// <summary>
    /// 读取设备 ID；不存在则生成并落库。显示名优先取配置，其次取调用方给的默认值。
    /// </summary>
    public static async Task<DeviceIdentity> ResolveAsync(
        AppConfigRepository repository,
        string? defaultDisplayName,
        CancellationToken cancellationToken = default)
    {
        var deviceId = await repository
            .GetOrCreateAsync(DeviceIdKey, static () => Guid.NewGuid().ToString("N"), cancellationToken)
            .ConfigureAwait(false);

        var fallbackName = string.IsNullOrWhiteSpace(defaultDisplayName)
            ? Environment.MachineName
            : defaultDisplayName.Trim();

        var displayName = await repository
            .GetOrCreateAsync(DisplayNameKey, () => fallbackName, cancellationToken)
            .ConfigureAwait(false);

        return new DeviceIdentity(deviceId, displayName);
    }
}
