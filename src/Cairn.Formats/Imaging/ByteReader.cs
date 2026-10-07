using System.Buffers.Binary;

namespace Cairn.Formats.Imaging;

/// <summary>
/// Bounds-checked little-endian reader over a byte buffer. Every out-of-range read raises
/// <see cref="ImageDecodeException"/>, which is what keeps the decoders safe against truncated
/// or hostile files.
/// </summary>
internal sealed class ByteReader(byte[] data, string name)
{
    private readonly byte[] _data = data;
    private readonly string _name = name;

    public int Position { get; set; }

    public int Length => _data.Length;

    public int Remaining => Math.Max(0, _data.Length - Position);

    public ReadOnlySpan<byte> Span => _data;

    private void Need(int count)
    {
        if (count < 0 || Position < 0 || Position + count > _data.Length)
            throw new ImageDecodeException($"'{_name}' is truncated or corrupt (needed {count} more bytes).");
    }

    public byte ReadByte()
    {
        Need(1);
        return _data[Position++];
    }

    public ushort ReadUInt16()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position));
        Position += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public ushort ReadUInt16BigEndian()
    {
        Need(2);
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position));
        Position += 2;
        return v;
    }

    public uint ReadUInt32BigEndian()
    {
        Need(4);
        uint v = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Position));
        Position += 4;
        return v;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Need(count);
        var span = _data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public void Skip(int count)
    {
        Need(count);
        Position += count;
    }

    public ImageDecodeException Fail(string reason) => new($"'{_name}': {reason}");
}

/// <summary>Reads bounded byte buffers out of streams for the probes and decoders.</summary>
internal static class StreamBytes
{
    /// <summary>Headers never need more than this; it also caps what a probe will buffer.</summary>
    public const int HeaderScanLimit = 1 << 20;

    /// <summary>A whole image file may not exceed this (matches the engine's own texture cap).</summary>
    public const int MaxFileBytes = 128 * 1024 * 1024;

    public static byte[] ReadAll(Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            long length = stream.Length - stream.Position;
            if (length > MaxFileBytes)
                throw new ImageDecodeException($"'{name}' is larger than {MaxFileBytes} bytes.");
        }
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > MaxFileBytes)
                throw new ImageDecodeException($"'{name}' is larger than {MaxFileBytes} bytes.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public static byte[] ReadHeader(Stream stream, string name, int limit = HeaderScanLimit)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        while (ms.Length < limit)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - ms.Length));
            if (read <= 0) break;
            ms.Write(buffer, 0, read);
        }
        if (ms.Length == 0) throw new ImageDecodeException($"'{name}' is empty.");
        return ms.ToArray();
    }
}
