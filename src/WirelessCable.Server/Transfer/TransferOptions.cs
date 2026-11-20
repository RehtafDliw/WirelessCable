using WirelessCable.Protocol;

namespace WirelessCable.Server.Transfer;

/// <summary>传输引擎配置。共享目录与临时目录由宿主注入，不在这里猜路径。</summary>
public sealed class TransferOptions
{
    /// <summary>上传落点根目录（用户可见）。</summary>
    public required string SharedDirectory { get; init; }

    /// <summary>上传临时目录（用户不可见，必须与共享目录分离）。</summary>
    public required string TempDirectory { get; init; }

    /// <summary>默认分块大小。</summary>
    public int DefaultChunkSize { get; init; } = ProtocolConstants.ChunkSizeBytes;

    /// <summary>哈希与落盘这类重活的最大并发数。</summary>
    public int MaxConcurrentHashJobs { get; init; } = 2;

    /// <summary>同一设备允许的非终态任务数。</summary>
    public int MaxActiveTransfersPerDevice { get; init; } = ProtocolConstants.MaxActiveTransfersPerDevice;

    /// <summary>任务空闲多久后被回收。</summary>
    public TimeSpan TaskIdleTimeout { get; init; } = TimeSpan.FromSeconds(ProtocolConstants.TransferTaskIdleSeconds);

    /// <summary>任务开始传输后，多久没有字节推进就判定为对端断开。</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromSeconds(ProtocolConstants.TransferInactivitySeconds);

    /// <summary>中断任务保留多久；超过后判失败并清理临时文件与分块状态。</summary>
    public TimeSpan InterruptedRetention { get; init; } =
        TimeSpan.FromSeconds(ProtocolConstants.InterruptedTaskRetentionSeconds);

    /// <summary>
    /// 已取消的记录保留多久；超过后回收（行与分块）。
    ///
    /// 取消是双向的：两端列表都不显示它，手机端也会删掉本地记录。
    /// 留一小段时间是为了让**离线的那一端**回来时还能看到"这条被取消了"，
    /// 时间一到就回收，避免这些行一直堆着（真机：341 条 cancelled）。
    /// </summary>
    public TimeSpan CancelledRetention { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>回收扫描间隔。</summary>
    public TimeSpan JanitorInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单文件大小上限。</summary>
    public long MaxFileBytes { get; init; } = ProtocolConstants.MaxTransferBytes;

    /// <summary>磁盘空间探针。测试可替换为固定值，生产用真实磁盘。</summary>
    public IFreeSpaceProbe FreeSpaceProbe { get; init; } = new DriveFreeSpaceProbe();
}

/// <summary>查询某个路径所在卷的可用字节数。</summary>
public interface IFreeSpaceProbe
{
    long GetAvailableBytes(string path);
}

/// <summary>真实磁盘探针。取不到时返回 <see cref="long.MaxValue"/>，宁可放过也不误拦。</summary>
public sealed class DriveFreeSpaceProbe : IFreeSpaceProbe
{
    public long GetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return long.MaxValue;
        }
    }
}

/// <summary>一次失败：稳定错误码 + HTTP 状态 + 是否可重试。</summary>
public sealed record TransferFailure(
    string Code,
    string Message,
    int StatusCode,
    bool Retryable = false,
    string? Details = null);

/// <summary>引擎结果。用返回值而不是异常表达"业务失败"，端点据此直接映射 HTTP。</summary>
public readonly record struct TransferResult<T>(T? Value, TransferFailure? Error)
{
    public bool Success => Error is null;

    public static TransferResult<T> Ok(T value) => new(value, null);

    public static TransferResult<T> Fail(TransferFailure failure) => new(default, failure);

    /// <summary>允许直接 <c>return failure;</c>，省掉每个调用点的类型参数。</summary>
    public static implicit operator TransferResult<T>(TransferFailure failure) => new(default, failure);
}

/// <summary>下载分块的可读流。调用方负责释放 <see cref="Stream"/>。</summary>
public sealed class ChunkReadResult : IDisposable
{
    public ChunkReadResult(
        Stream stream,
        long length,
        string sha256,
        long totalBytes,
        int index,
        string chunkSha256)
    {
        Stream = stream;
        Length = length;
        Sha256 = sha256;
        TotalBytes = totalBytes;
        Index = index;
        ChunkSha256 = chunkSha256;
    }

    public Stream Stream { get; }
    public long Length { get; }

    /// <summary>整文件 SHA-256。</summary>
    public string Sha256 { get; }

    public long TotalBytes { get; }
    public int Index { get; }

    /// <summary>本块内容的 SHA-256，客户端落盘后据此自检再回执。</summary>
    public string ChunkSha256 { get; }

    public void Dispose() => Stream.Dispose();
}
