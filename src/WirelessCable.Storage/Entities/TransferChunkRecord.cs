namespace WirelessCable.Storage.Entities;

/// <summary>
/// TransferChunks 表的一行：一个分块的持久化状态。
/// 断点续传的唯一事实来源——内存里的任何进度都不能替代这张表。
/// </summary>
public sealed class TransferChunkRecord
{
    public required string TaskId { get; init; }

    public required int ChunkIndex { get; init; }

    /// <summary>文件内绝对偏移（= ChunkIndex × ChunkSize）。</summary>
    public required long Offset { get; init; }

    /// <summary>本块字节数；最后一块可小于分块大小。</summary>
    public required int Length { get; init; }

    /// <summary>本块内容 SHA-256（小写十六进制）。下载块在首次下发时惰性计算并回写。</summary>
    public string? Sha256 { get; init; }

    /// <summary>pending / completed。</summary>
    public required string Status { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
