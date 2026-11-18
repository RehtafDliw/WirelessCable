using WirelessCable.Protocol;
using WirelessCable.Server.Transfer;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.Files;

/// <summary>
/// 共享目录解析结果。
/// </summary>
/// <param name="RootPath">目录的绝对路径（服务端内部使用，绝不下发客户端）。</param>
/// <param name="Permissions">当前设备在该目录上的权限位。</param>
/// <param name="DisplayName">展示名。</param>
public sealed record SharedDirectoryResolution(
    string RootPath,
    SharedDirectoryPermissions Permissions,
    string DisplayName);

/// <summary>
/// 把「目录 id + 设备」解析成「本地绝对路径 + 权限」。
///
/// 传输引擎与文件服务都靠它定位目录，因此权限判定只有这一处实现，
/// 不会出现两个地方各判一次、判得不一样的情况。
/// </summary>
public sealed class SharedDirectoryResolver
{
    private readonly SharedDirectoryRepository _directories;
    private string _defaultRoot;

    public SharedDirectoryResolver(SharedDirectoryRepository directories, string defaultRoot)
    {
        _directories = directories;
        _defaultRoot = defaultRoot;
    }

    /// <summary>默认共享目录（手机上传的默认落点）。始终可用，不做目录 id 校验。</summary>
    public string DefaultRoot => _defaultRoot;

    /// <summary>换默认落点（设置页改了接收目录）。不影响已授权目录的解析。</summary>
    public void UpdateDefaultRoot(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _defaultRoot = path;
        }
    }

    /// <summary>
    /// 解析目录。directoryId 为空时返回默认共享目录（等价于全权限，
    /// 保持阶段 3/4 的既有行为：有默认落点权限的设备照常上传）。
    /// </summary>
    public async Task<(SharedDirectoryResolution? Resolution, TransferFailure? Error)> ResolveAsync(
        string? directoryId,
        StoredDevice device,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryId))
        {
            return (
                new SharedDirectoryResolution(_defaultRoot, SharedDirectoryPermissions.All, "接收目录"),
                null);
        }

        var directory = await _directories.GetAsync(directoryId, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return (null, new TransferFailure(
                ErrorCodes.DirectoryNotAuthorized,
                "该目录不存在。",
                403));
        }

        var permissions = await _directories
            .GetPermissionsAsync(directoryId, device.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        if (permissions == SharedDirectoryPermissions.None)
        {
            return (null, new TransferFailure(
                ErrorCodes.DirectoryNotAuthorized,
                "该目录未授权给当前设备。",
                403));
        }

        return (new SharedDirectoryResolution(directory.RootPath, permissions, directory.DisplayName), null);
    }

    /// <summary>只取根路径，解析失败返回 null。</summary>
    public async Task<string?> TryResolveRootAsync(
        string? directoryId,
        StoredDevice device,
        CancellationToken cancellationToken = default)
    {
        var (resolution, error) = await ResolveAsync(directoryId, device, cancellationToken).ConfigureAwait(false);
        return error is null ? resolution!.RootPath : null;
    }
}
