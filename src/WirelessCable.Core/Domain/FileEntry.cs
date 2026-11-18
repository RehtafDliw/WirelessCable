namespace WirelessCable.Core.Domain;

/// <summary>文件系统条目（跨平台统一表示）。</summary>
/// <param name="Name">显示名（不含路径）。</param>
/// <param name="RelativePath">相对授权根的路径，使用 '/' 分隔。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="SizeBytes">文件字节数；目录为 0。</param>
/// <param name="LastModifiedUtc">最后修改时间（UTC）。</param>
public sealed record FileEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);
