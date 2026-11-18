namespace WirelessCable.Core.Abstractions;

/// <summary>
/// 文件系统统一抽象。Windows 用盘符路径实现，Android 用 SAF / MediaStore 实现。
///
/// 业务层只认相对路径与 <see cref="Domain.FileEntry"/>，不感知平台细节：
/// <list type="bullet">
/// <item>路径一律相对"根"，用 '/' 分隔；空串表示根本身；</item>
/// <item>实现方必须拒绝绝对路径与 '..' 穿越；</item>
/// <item>实现方必须自行探测真实能力（<see cref="CheckCapabilitiesAsync"/>），上层不得假设。</item>
/// </list>
///
/// 失败一律通过 <see cref="Domain.FileOperationResult"/> 表达，不把异常抛给业务层——
/// "提供方不支持"与"没有权限"是两种完全不同的用户可见结果，混在异常里就说不清了。
/// </summary>
public interface IFileProvider
{
    /// <summary>列出该提供方下的根（Windows 的盘符，Android 的已授权 SAF 根）。</summary>
    Task<IReadOnlyList<Domain.FileEntry>> GetRootDirectoriesAsync(CancellationToken cancellationToken);

    /// <summary>列出目录内容。<paramref name="path"/> 为相对根的路径，空字符串表示根。</summary>
    Task<Domain.FileOperationResult<IReadOnlyList<Domain.FileEntry>>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken);

    /// <summary>获取单个条目的元信息，不存在返回失败（NotFound）。</summary>
    Task<Domain.FileOperationResult<Domain.FileEntry?>> GetFileInfoAsync(
        string path,
        CancellationToken cancellationToken);

    /// <summary>以只读流打开文件，从 <paramref name="offset"/> 字节开始。调用方负责释放。</summary>
    Task<Domain.FileOperationResult<Stream>> OpenReadAsync(
        string path,
        long offset,
        CancellationToken cancellationToken);

    /// <summary>打开写入流；<paramref name="append"/> 为 true 时续写（断点续传）。调用方负责释放。</summary>
    Task<Domain.FileOperationResult<Stream>> OpenWriteAsync(
        string path,
        bool append,
        CancellationToken cancellationToken);

    /// <summary>新建目录。目标已存在返回 AlreadyExists。</summary>
    Task<Domain.FileOperationResult> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken);

    /// <summary>重命名文件或目录（仅改名，不移动位置）。</summary>
    Task<Domain.FileOperationResult> RenameAsync(
        string path,
        string newName,
        CancellationToken cancellationToken);

    /// <summary>删除文件或目录。<paramref name="recursive"/> 为 true 时允许删除非空目录。</summary>
    Task<Domain.FileOperationResult> DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken);

    /// <summary>移动文件或目录。</summary>
    Task<Domain.FileOperationResult> MoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken);

    /// <summary>探测该提供方（或某个具体根）真实支持哪些操作。</summary>
    Task<Domain.FileProviderCapabilities> CheckCapabilitiesAsync(
        string? path,
        CancellationToken cancellationToken);

    /// <summary>条目是否存在。</summary>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken);
}
