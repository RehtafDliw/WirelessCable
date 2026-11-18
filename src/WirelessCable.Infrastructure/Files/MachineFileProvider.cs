using WirelessCable.Core.Abstractions;
using WirelessCable.Core.Domain;
using WirelessCable.Core.Security;

namespace WirelessCable.Infrastructure.Files;

/// <summary>
/// 整机文件系统提供方（"此电脑"视图）。
///
/// 手机要能像资源管理器那样看到电脑上的**全部**文件，而不是只有共享目录。
/// 本提供方的顶层是本机的盘符列表（本地磁盘、U 盘、光驱…），
/// 进入某个盘符后委托给 <see cref="LocalFileProvider"/>，因此段级校验
/// （穿越 / ADS / 保留名）与逐段联接检查的规则完全一致。
///
/// **可写**（1.1.0 起）：产品改为"默认开放全部文件"，写操作与读走同一套范围校验，
/// 落盘仍由按盘符构造的 <see cref="LocalFileProvider"/> 完成，段级规则完全一致。
/// 是否允许写由设备档位决定（服务层按 <c>machine_access</c> 判定），
/// 用户可以在桌面端把某台设备降为只读或关闭。
/// </summary>
public sealed class MachineFileProvider : IFileProvider
{
    public Task<IReadOnlyList<FileEntry>> GetRootDirectoriesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FileEntry> roots = ListDrives();
        return Task.FromResult(roots);
    }

    public Task<FileOperationResult<IReadOnlyList<FileEntry>>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);

        // 顶层：列出盘符
        if (normalized.Length == 0)
        {
            return Task.FromResult(FileOperationResult<IReadOnlyList<FileEntry>>.Ok(ListDrives()));
        }

        var resolved = PathGuard.ResolveMachinePath(normalized);
        if (!resolved.IsAllowed)
        {
            return Task.FromResult(FileOperationResult<IReadOnlyList<FileEntry>>.Fail(
                FileOperationStatus.PathInvalid,
                resolved.ReasonCode,
                "路径非法。"));
        }

        var fullPath = resolved.FullPath;

        if (!IsWithinLocalDrive(fullPath))
        {
            return Task.FromResult(
                FileOperationResult<IReadOnlyList<FileEntry>>.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_outside_machine",
                    "该路径不在本机磁盘范围内。"));
        }

        // 盘符根与其下的目录都自己列举：
        // 委派给 LocalFileProvider 会丢掉盘符前缀（相对路径是 "coding/x" 而不是 "C:/coding/x"），
        // 客户端拿到的路径与它发回来的就对不上了。
        return Task.FromResult(ListDirectoryContents(fullPath, normalized));
    }

    public Task<FileOperationResult<FileEntry?>> GetFileInfoAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return Task.FromResult(FileOperationResult<FileEntry?>.NotFound("路径为空。"));
        }

        var resolved = PathGuard.ResolveMachinePath(normalized);
        if (!resolved.IsAllowed)
        {
            return Task.FromResult(FileOperationResult<FileEntry?>.Fail(
                FileOperationStatus.PathInvalid,
                resolved.ReasonCode,
                "路径非法。"));
        }

        if (!IsWithinLocalDrive(resolved.FullPath))
        {
            return Task.FromResult(
                FileOperationResult<FileEntry?>.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_outside_machine",
                    "该路径不在本机磁盘范围内。"));
        }

        if (IsDriveRoot(normalized))
        {
            var info = new DirectoryInfo(resolved.FullPath);
            if (!info.Exists)
            {
                return Task.FromResult(FileOperationResult<FileEntry?>.NotFound());
            }

            return Task.FromResult(FileOperationResult<FileEntry?>.Ok(new FileEntry(
                normalized,
                normalized,
                true,
                0,
                SafeModifiedTime(resolved.FullPath))));
        }

        var directory = new DirectoryInfo(resolved.FullPath);
        if (directory.Exists)
        {
            return Task.FromResult(FileOperationResult<FileEntry?>.Ok(new FileEntry(
                NameOf(normalized),
                normalized,
                true,
                0,
                SafeModifiedTime(resolved.FullPath))));
        }

        var file = new FileInfo(resolved.FullPath);
        if (file.Exists)
        {
            return Task.FromResult(FileOperationResult<FileEntry?>.Ok(new FileEntry(
                file.Name,
                normalized,
                false,
                file.Length,
                SafeModifiedTime(resolved.FullPath))));
        }

        return Task.FromResult(FileOperationResult<FileEntry?>.NotFound());
    }

    public Task<FileOperationResult<Stream>> OpenReadAsync(
        string path,
        long offset,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveTarget(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult<Stream>.Fail(
                resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        var provider = new LocalFileProvider(DriveRootOf(resolved.Value!));
        return provider.OpenReadAsync(RelativeUnderDrive(Normalize(path)), offset, cancellationToken);
    }

    // ---------------------------------------------------------------- 写操作
    //
    // 1.1.0 起整机默认可写（产品要求"默认开放所有文件"）。
    // 写操作与读操作走同一套范围校验：解析出的绝对路径必须仍在本地盘符内，
    // 逐段重解析点检查照旧——写路径上更要挡 junction，否则一次删除就能删到网络位置。
    // 具体落盘交给按盘符构造的 LocalFileProvider，段级校验规则完全一致。

    public Task<FileOperationResult<Stream>> OpenWriteAsync(
        string path,
        bool append,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveTarget(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult<Stream>.Fail(
                resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        var provider = new LocalFileProvider(DriveRootOf(resolved.Value!));
        return provider.OpenWriteAsync(RelativeUnderDrive(Normalize(path)), append, cancellationToken);
    }

    public Task<FileOperationResult> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0 || IsDriveRoot(normalized))
        {
            return Task.FromResult(FileOperationResult.Fail(
                FileOperationStatus.PathInvalid,
                "path_invalid",
                "盘符根不能作为新建目录的路径。"));
        }

        var resolved = ResolveTarget(normalized);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(
                resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        var provider = new LocalFileProvider(DriveRootOf(resolved.Value!));
        return provider.CreateDirectoryAsync(RelativeUnderDrive(normalized), cancellationToken);
    }

    public Task<FileOperationResult> RenameAsync(
        string path,
        string newName,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0 || IsDriveRoot(normalized))
        {
            return Task.FromResult(FileOperationResult.Fail(
                FileOperationStatus.PathInvalid,
                "path_invalid",
                "盘符根不能重命名。"));
        }

        var resolved = ResolveTarget(normalized);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(
                resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        var provider = new LocalFileProvider(DriveRootOf(resolved.Value!));
        return provider.RenameAsync(RelativeUnderDrive(normalized), newName, cancellationToken);
    }

    public Task<FileOperationResult> DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0 || IsDriveRoot(normalized))
        {
            // 删盘符根不是"危险操作"的边界问题，是根本不提供
            return Task.FromResult(FileOperationResult.Fail(
                FileOperationStatus.PathInvalid,
                "path_invalid",
                "不能删除盘符根。"));
        }

        var resolved = ResolveTarget(normalized);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(
                resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        var provider = new LocalFileProvider(DriveRootOf(resolved.Value!));
        return provider.DeleteAsync(RelativeUnderDrive(normalized), recursive, cancellationToken);
    }

    public Task<FileOperationResult> MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var source = Normalize(sourcePath);
        var destination = Normalize(destinationPath);

        if (source.Length == 0 || destination.Length == 0
            || IsDriveRoot(source) || IsDriveRoot(destination))
        {
            return Task.FromResult(FileOperationResult.Fail(
                FileOperationStatus.PathInvalid,
                "path_invalid",
                "路径非法。"));
        }

        var resolvedSource = ResolveTarget(source);
        if (!resolvedSource.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(
                resolvedSource.Status, resolvedSource.ReasonCode, resolvedSource.Message));
        }

        var resolvedDestination = ResolveTarget(destination);
        if (!resolvedDestination.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(
                resolvedDestination.Status,
                resolvedDestination.ReasonCode,
                resolvedDestination.Message));
        }

        // 跨盘移动要先复制再删，失败会留下半个文件；明确拒绝比做到一半好
        if (!string.Equals(
                DriveRootOf(resolvedSource.Value!),
                DriveRootOf(resolvedDestination.Value!),
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(FileOperationResult.Fail(
                FileOperationStatus.Unsupported,
                "cross_drive_move",
                "跨盘移动请分两步：先复制，再删除。"));
        }

        var provider = new LocalFileProvider(DriveRootOf(resolvedSource.Value!));
        return provider.MoveAsync(
            RelativeUnderDrive(source),
            RelativeUnderDrive(destination),
            cancellationToken);
    }

    public Task<FileProviderCapabilities> CheckCapabilitiesAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        // 整机：读写全开。能否真的写由设备档位（machine_access）在服务层判定，
        // 这里回答的是"提供方本身支持什么"。
        var capabilities = FileProviderCapabilities.ListDirectory
            | FileProviderCapabilities.ReadFile
            | FileProviderCapabilities.GetFileInfo
            | FileProviderCapabilities.WriteFile
            | FileProviderCapabilities.CreateDirectory
            | FileProviderCapabilities.Delete
            | FileProviderCapabilities.Rename
            | FileProviderCapabilities.Move;

        return Task.FromResult(capabilities);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return Task.FromResult(true);
        }

        var resolved = PathGuard.ResolveMachinePath(normalized);
        if (!resolved.IsAllowed)
        {
            return Task.FromResult(false);
        }

        try
        {
            return Task.FromResult(Directory.Exists(resolved.FullPath) || File.Exists(resolved.FullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>列出本机可用盘符。</summary>
    private static IReadOnlyList<FileEntry> ListDrives()
    {
        var entries = new List<FileEntry>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            // 只列本地固定盘与可移动盘：网络盘与光驱不作为整机浏览入口
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            if (!drive.IsReady)
            {
                continue;
            }

            var letter = drive.Name.TrimEnd('\\', '/');
            if (letter.Length != 2 || letter[1] != ':')
            {
                continue;
            }

            var name = drive.VolumeLabel;
            var display = string.IsNullOrWhiteSpace(name)
                ? letter + " 磁盘"
                : name + " (" + letter + ")";

            entries.Add(new FileEntry(
                display,
                letter,
                true,
                0,
                SafeModifiedTime(drive.RootDirectory.FullName)));
        }

        return entries;
    }

    /// <summary>
    /// 列目录内容。子项的相对路径**始终带盘符前缀**（<c>C:/Users</c>），
    /// 与请求里用的路径格式完全一致，客户端才能原样回传。
    /// </summary>
    private static FileOperationResult<IReadOnlyList<FileEntry>> ListDirectoryContents(
        string fullPath,
        string prefix)
    {
        if (!Directory.Exists(fullPath))
        {
            return FileOperationResult<IReadOnlyList<FileEntry>>.NotFound("目录不存在。");
        }

        var entries = new List<FileEntry>();

        try
        {
            foreach (var directory in new DirectoryInfo(fullPath).EnumerateDirectories())
            {
                entries.Add(new FileEntry(
                    directory.Name,
                    prefix + "/" + directory.Name,
                    true,
                    0,
                    SafeModifiedTime(directory.FullName)));
            }

            foreach (var file in new DirectoryInfo(fullPath).EnumerateFiles())
            {
                entries.Add(new FileEntry(
                    file.Name,
                    prefix + "/" + file.Name,
                    false,
                    file.Length,
                    SafeModifiedTime(file.FullName)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult<IReadOnlyList<FileEntry>>.Fail(
                FileOperationStatus.PermissionDenied,
                "directory_unreadable",
                "无法读取该目录。");
        }

        return FileOperationResult<IReadOnlyList<FileEntry>>.Ok(entries);
    }

    /// <summary>解析目标路径（读写共用），并做本机范围校验。</summary>
    private FileOperationResult<string> ResolveTarget(string? path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return FileOperationResult<string>.NotFound("路径为空。");
        }

        var resolved = PathGuard.ResolveMachinePath(normalized);
        if (!resolved.IsAllowed)
        {
            return FileOperationResult<string>.Fail(
                FileOperationStatus.PathInvalid,
                resolved.ReasonCode,
                "路径非法。");
        }

        if (!IsWithinLocalDrive(resolved.FullPath))
        {
            return FileOperationResult<string>.Fail(
                FileOperationStatus.PathInvalid,
                "path_outside_machine",
                "该路径不在本机磁盘范围内。");
        }

        return FileOperationResult<string>.Ok(resolved.FullPath);
    }

    /// <summary>
    /// 判断绝对路径是否仍在本机磁盘范围内。
    ///
    /// 逐段检查重解析点：允许 junction（资源管理器里也能看到并进入），
    /// 但解析后的目标必须仍在某个本地盘符内——
    /// 否则一个指向 <c>\\server\share</c> 的 junction 会把手机带到网络位置上。
    /// </summary>
    private static bool IsWithinLocalDrive(string fullPath)
    {
        if (fullPath.Length < 3 || fullPath[1] != ':')
        {
            return false;
        }

        var root = fullPath[..3];

        var current = root;
        var remainder = fullPath.Length > root.Length
            ? fullPath[(root.Length + 1)..]
            : string.Empty;

        foreach (var segment in remainder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            try
            {
                if (!Directory.Exists(current) && !File.Exists(current))
                {
                    // 尚未创建的目标：其祖先已检查过
                    return true;
                }

                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = ResolveLinkTarget(current);
                    if (target is null || !IsLocalDrivePath(target))
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>解析重解析点的最终目标；解析不出就按不安全处理。</summary>
    private static string? ResolveLinkTarget(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (directory.Exists)
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName;
            }

            var file = new FileInfo(path);
            if (file.Exists)
            {
                var target = file.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }

        return null;
    }

    /// <summary>判断绝对路径是否指向本机某个盘符（排除 UNC 与设备路径）。</summary>
    private static bool IsLocalDrivePath(string fullPath)
        => fullPath.Length >= 3
            && char.IsAsciiLetter(fullPath[0])
            && fullPath[1] == ':'
            && (fullPath[2] == '\\' || fullPath[2] == '/');

    /// <summary>取绝对路径所属的盘符根，如 <c>C:\</c>。</summary>
    private static string DriveRootOf(string fullPath)
        => fullPath[..3];

    /// <summary>取整机路径中盘符之后的部分（相对盘符根）。</summary>
    private static string RelativeUnderDrive(string normalized)
    {
        var rest = normalized.Length > 2 ? normalized[2..] : string.Empty;
        return rest.TrimStart('/', '\\');
    }

    /// <summary>判断是否就是盘符根（<c>C:</c> 或 <c>C:/</c>）。</summary>
    private static bool IsDriveRoot(string normalized)
        => normalized.Length == 2 || (normalized.Length == 3 && normalized[2] == '/');

    /// <summary>取路径的最后一段（显示名）。</summary>
    private static string NameOf(string normalized)
    {
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 && slash < normalized.Length - 1
            ? normalized[(slash + 1)..]
            : normalized;
    }

    private static string Normalize(string? path)
        => (path ?? string.Empty).Replace('\\', '/').Trim();


    private static DateTimeOffset SafeModifiedTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }
}
