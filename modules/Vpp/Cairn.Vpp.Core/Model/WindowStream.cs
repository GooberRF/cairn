namespace Cairn.Vpp.Model;

/// <summary>A read-only window onto part of another stream.</summary>
internal sealed class WindowStream(Stream inner, long offset, long length, bool ownsInner) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= length) return 0;
        int want = (int)Math.Min(buffer.Length, length - _position);
        if (inner.Position != offset + _position) inner.Position = offset + _position;
        int read = inner.Read(buffer[..want]);
        _position += read;
        return read;
    }

    public override long Seek(long target, SeekOrigin origin)
    {
        long next = origin switch
        {
            SeekOrigin.Begin => target,
            SeekOrigin.Current => _position + target,
            _ => length + target,
        };
        _position = Math.Clamp(next, 0, length);
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsInner) inner.Dispose();
        base.Dispose(disposing);
    }
}
