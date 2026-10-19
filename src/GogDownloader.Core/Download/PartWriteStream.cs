using Microsoft.Win32.SafeHandles;

namespace GogDownloader.Core.Download;

/// <summary>A part's own seek position, with writes independent of other parts on the same file.</summary>
internal sealed class PartWriteStream(SafeFileHandle handle, long offset) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => Position;
    public override long Position { get; set; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int start, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Seek(long value, SeekOrigin origin) =>
        Position = origin switch
        {
            SeekOrigin.Begin => value,
            SeekOrigin.Current => Position + value,
            _ => throw new NotSupportedException(),
        };
    public override void Write(byte[] buffer, int start, int count)
    {
        try { RandomAccess.Write(handle, buffer.AsSpan(start, count), offset + Position); }
        catch (IOException e) { throw new StagingWriteException(e); }
        Position += count;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try { await RandomAccess.WriteAsync(handle, buffer, offset + Position, ct); }
        catch (IOException e) { throw new StagingWriteException(e); }
        Position += buffer.Length;
    }
}

/// <summary>A local write failure must not trigger CDN retries.</summary>
internal sealed class StagingWriteException(IOException inner)
    : Exception($"Cannot write staging content: {inner.Message}", inner);
