using WirelessCable.Core.Abstractions;
using WirelessCable.Core.Domain;
using WirelessCable.Core.Security;
using WirelessCable.Infrastructure.Security;

namespace WirelessCable.Infrastructure.Files;

/// <summary>
/// Windows 本地文件系统实现。一个实例对应一个共享根目录，
/// 所有路径参数都是相对该根的相对路径（'/' 分隔）。
///
/// 安全约束（需求第五节）：
/// <list type="bullet">
/// <item>相对路径先过 <see cref="PathGuard"/>：拒绝穿越、绝对路径、盘符、ADS、保留名；</item>
/// <item>再过 <see cref="ReparsePointGuard"/>：从根逐段走到目标，任一段是 Junction/符号链接即拒绝——
/// 否则攻击者可以在共享目录里放一个指向 C:\Windows 的 Junction 绕出去；</item>
/// <item>重命名/移动要同时校验源与目标；</item>
/// <item>删除目录默认不递归，非空目录一律拒绝——避免误删整棵目录树。</item>
/// </list>
/// </summary>
public sealed class LocalFileProvider : IFileProvider
{
    private readonly string _root;

    public LocalFileProvider(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("根路径不能为空。", nameof(rootPath));
        }

        _root = Path.GetFullPath(rootPath);
    }

    /// <summary>本提供方对应的根目录绝对路径（服务端内部使用，不下发客户端）。</summary>
    public string RootPath => _root;

    public Task<IReadOnlyList<FileEntry>> GetRootDirectoriesAsync(CancellationToken cancellationToken)
    {
        var name = new DirectoryInfo(_root).Name;
        if (string.IsNullOrEmpty(name))
        {
            name = _root;
        }

        IReadOnlyList<FileEntry> roots = new[]
        {
            new FileEntry(name, string.Empty, true, 0, SafeModifiedTime(_root)),
        };

        return Task.FromResult(roots);
    }

    public Task<FileOperationResult<IReadOnlyList<FileEntry>>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult<IReadOnlyList<FileEntry>>.Fail(resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        if (!Directory.Exists(resolved.Value!))
        {
            return Task.FromResult(FileOperationResult<IReadOnlyList<FileEntry>>.NotFound("目录不存在。"));
        }

        if (HasReparsePoint(resolved.Value!))
        {
            return Task.FromResult(
                FileOperationResult<IReadOnlyList<FileEntry>>.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        var entries = new List<FileEntry>();
        try
        {
            foreach (var directory in new DirectoryInfo(resolved.Value!).EnumerateDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(new FileEntry(
                    directory.Name,
                    Combine(path, directory.Name),
                    true,
                    0,
                    SafeModifiedTime(directory.FullName)));
            }

            foreach (var file in new DirectoryInfo(resolved.Value!).EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(new FileEntry(
                    file.Name,
                    Combine(path, file.Name),
                    false,
                    file.Length,
                    SafeModifiedTime(file.FullName)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                FileOperationResult<IReadOnlyList<FileEntry>>.Fail(
                    FileOperationStatus.PermissionDenied,
                    "directory_unreadable",
                    "无法读取该目录。"));
        }

        return Task.FromResult(FileOperationResult<IReadOnlyList<FileEntry>>.Ok(entries));
    }

    public Task<FileOperationResult<FileEntry?>> GetFileInfoAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult<FileEntry?>.Fail(resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        if (HasReparsePoint(resolved.Value!))
        {
            return Task.FromResult(
                FileOperationResult<FileEntry?>.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        var directory = new DirectoryInfo(resolved.Value!);
        if (directory.Exists)
        {
            return Task.FromResult(FileOperationResult<FileEntry?>.Ok(new FileEntry(
                directory.Name,
                NormalizeRelative(path),
                true,
                0,
                SafeModifiedTime(resolved.Value!))));
        }

        var file = new FileInfo(resolved.Value!);
        if (file.Exists)
        {
            return Task.FromResult(FileOperationResult<FileEntry?>.Ok(new FileEntry(
                file.Name,
                NormalizeRelative(path),
                false,
                file.Length,
                SafeModifiedTime(resolved.Value!))));
        }

        return Task.FromResult(FileOperationResult<FileEntry?>.NotFound());
    }

    public Task<FileOperationResult<Stream>> OpenReadAsync(
        string path,
        long offset,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult<Stream>.Fail(resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        if (HasReparsePoint(resolved.Value!))
        {
            return Task.FromResult(
                FileOperationResult<Stream>.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        if (!File.Exists(resolved.Value!))
        {
            return Task.FromResult(FileOperationResult<Stream>.NotFound());
        }

        try
        {
            var stream = new FileStream(
                resolved.Value!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                useAsync: true);

            if (offset > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            return Task.FromResult(FileOperationResult<Stream>.Ok(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                FileOperationResult<Stream>.Fail(
                    FileOperationStatus.PermissionDenied,
                    "file_unreadable",
                    "无法读取该文件。"));
        }
    }

    public Task<FileOperationResult<Stream>> OpenWriteAsync(
        string path,
        bool append,
        CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult<Stream>.Fail(resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        if (HasReparsePoint(resolved.Value!))
        {
            return Task.FromResult(
                FileOperationResult<Stream>.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        try
        {
            var parent = Path.GetDirectoryName(resolved.Value!);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var stream = new FileStream(
                resolved.Value!,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true);

            return Task.FromResult(FileOperationResult<Stream>.Ok(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                FileOperationResult<Stream>.Fail(
                    FileOperationStatus.PermissionDenied,
                    "file_unwritable",
                    "无法写入该文件。"));
        }
    }

    public Task<FileOperationResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        if (HasReparsePoint(resolved.Value!))
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        if (Directory.Exists(resolved.Value!))
        {
            return Task.FromResult(FileOperationResult.AlreadyExists("该目录已存在。"));
        }

        try
        {
            Directory.CreateDirectory(resolved.Value!);
            return Task.FromResult(FileOperationResult.Ok());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.PermissionDenied,
                    "directory_creation_failed",
                    "无法创建该目录。"));
        }
    }

    public Task<FileOperationResult> RenameAsync(string path, string newName, CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        // 新名字必须是单个合法文件名，不能带路径分隔符
        var nameCheck = PathGuard.SanitizeFileName(newName);
        if (!string.Equals(nameCheck, newName, StringComparison.Ordinal))
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.PathInvalid,
                    "invalid_file_name",
                    "新名称包含非法字符。"));
        }

        if (HasReparsePoint(resolved.Value!))
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        var parent = Path.GetDirectoryName(resolved.Value!);
        if (string.IsNullOrEmpty(parent))
        {
            return Task.FromResult(FileOperationResult.PathInvalid());
        }

        var destination = Path.Combine(parent, newName);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return Task.FromResult(FileOperationResult.AlreadyExists("同名文件或目录已存在。"));
        }

        try
        {
            if (Directory.Exists(resolved.Value!))
            {
                Directory.Move(resolved.Value!, destination);
            }
            else if (File.Exists(resolved.Value!))
            {
                File.Move(resolved.Value!, destination);
            }
            else
            {
                return Task.FromResult(FileOperationResult.NotFound());
            }

            return Task.FromResult(FileOperationResult.Ok());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.IoFailure,
                    "rename_failed",
                    "重命名失败。"));
        }
    }

    public Task<FileOperationResult> DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(resolved.Status, resolved.ReasonCode, resolved.Message));
        }

        // 根目录本身不可删
        if (string.IsNullOrEmpty(NormalizeRelative(path)))
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.PermissionDenied,
                    "cannot_delete_root",
                    "不能删除根目录。"));
        }

        if (HasReparsePoint(resolved.Value!))
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        try
        {
            if (Directory.Exists(resolved.Value!))
            {
                // 非递归删除非空目录必须拒绝：误删一整棵目录树的代价太大
                if (!recursive)
                {
                    return Task.FromResult(
                        FileOperationResult.Fail(
                            FileOperationStatus.IoFailure,
                            "directory_not_empty",
                            "目录非空，需要确认后才能删除。"));
                }

                Directory.Delete(resolved.Value!, true);
                return Task.FromResult(FileOperationResult.Ok());
            }

            if (File.Exists(resolved.Value!))
            {
                File.Delete(resolved.Value!);
                return Task.FromResult(FileOperationResult.Ok());
            }

            return Task.FromResult(FileOperationResult.NotFound());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.IoFailure,
                    "delete_failed",
                    "删除失败。"));
        }
    }

    public Task<FileOperationResult> MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var source = Resolve(sourcePath);
        if (!source.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(source.Status, source.ReasonCode, source.Message));
        }

        var destination = Resolve(destinationPath);
        if (!destination.Success)
        {
            return Task.FromResult(FileOperationResult.Fail(destination.Status, destination.ReasonCode, destination.Message));
        }

        if (HasReparsePoint(source.Value!) || HasReparsePoint(destination.Value!))
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.PathInvalid,
                    "path_reparse_point",
                    "路径包含符号链接或目录联接，已拒绝访问。"));
        }

        if (!File.Exists(source.Value!) && !Directory.Exists(source.Value!))
        {
            return Task.FromResult(FileOperationResult.NotFound());
        }

        if (File.Exists(destination.Value!) || Directory.Exists(destination.Value!))
        {
            return Task.FromResult(FileOperationResult.AlreadyExists("目标已存在。"));
        }

        try
        {
            var parent = Path.GetDirectoryName(destination.Value!);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (Directory.Exists(source.Value!))
            {
                Directory.Move(source.Value!, destination.Value!);
            }
            else
            {
                File.Move(source.Value!, destination.Value!);
            }

            return Task.FromResult(FileOperationResult.Ok());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(
                FileOperationResult.Fail(
                    FileOperationStatus.IoFailure,
                    "move_failed",
                    "移动失败。"));
        }
    }

    public Task<FileProviderCapabilities> CheckCapabilitiesAsync(string? path, CancellationToken cancellationToken)
        => Task.FromResult(FileProviderCapabilities.All);

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        var resolved = Resolve(path);
        if (!resolved.Success)
        {
            return false;
        }

        return File.Exists(resolved.Value!) || Directory.Exists(resolved.Value!);
    }

    // ------------------------------------------------------------ 内部

    /// <summary>把相对路径解析成根内绝对路径，并做路径合法性校验。</summary>
    private FileOperationResult<string> Resolve(string? path)
    {
        var check = PathGuard.Resolve(_root, path ?? string.Empty);
        return check.IsAllowed
            ? FileOperationResult<string>.Ok(check.FullPath)
            : FileOperationResult<string>.Fail(
                FileOperationStatus.PathInvalid,
                check.ReasonCode,
                "路径非法。");
    }

    private bool HasReparsePoint(string fullPath)
        => ReparsePointGuard.ContainsReparsePoint(_root, fullPath);

    private static string NormalizeRelative(string? path)
        => string.Join(
            '/',
            (path ?? string.Empty)
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != "."));

    private static string Combine(string parent, string name)
    {
        var normalized = NormalizeRelative(parent);
        return normalized.Length == 0 ? name : normalized + "/" + name;
    }

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
