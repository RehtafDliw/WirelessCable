using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessCable.Server.Transfer;

/// <summary>
/// 把一个流限制在固定字节数之内。
///
/// 为什么需要它：下载分块时文件流是整份打开的，仅靠调用方"记得只拷 N 字节"是不可靠的——
/// 少拷会让客户端拿到残缺块，多拷会直接触发 Kestrel 的 Content-Length 校验异常
/// （Response Content-Length mismatch: too many bytes written）。
/// 把长度约束放进流本身，越界在类型层面就不可能发生。
///
/// 只支持顺序读；不支持 Seek，避免调用方把边界算术带回上层。
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _remaining;

    public BoundedReadStream(Stream inner, long length, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _inner = inner;
        _remaining = length;
        _leaveOpen = leaveOpen;
    }

    /// <summary>还能读出的字节数。</summary>
    public long Remaining => _remaining;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var want = (int)Math.Min(count, _remaining);
        var read = _inner.Read(buffer, offset, want);
        if (read > 0)
        {
            _remaining -= read;
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var want = (int)Math.Min(buffer.Length, _remaining);
        var read = await _inner.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _remaining -= read;
        }

        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
        // 只读流，无缓冲需要刷出
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
