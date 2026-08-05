using Microsoft.AspNetCore.Http;

namespace Acta.AspNetCore.Web;

/// <summary>
/// Read-only pass-through over a request body that throws <see cref="PayloadTooLargeException"/> once
/// more than <paramref name="maxBytes"/> bytes arrive. The declared Content-Length is checked before
/// reading starts; this counting guard is what makes the ceiling hold for chunked bodies too.
/// <para>
/// One concept, one exception: an oversized body raises the same type the ledger raises for an
/// oversized payload, and the API error filter maps it to 413 wherever it comes from.
/// </para>
/// </summary>
internal sealed class BoundedReadStream(Stream inner, int maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int read)
    {
        _read += read;
        return _read > maxBytes ? throw new PayloadTooLargeException("request body", (int)Math.Min(_read, int.MaxValue), maxBytes) : read;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
