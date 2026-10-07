using System.Collections.Immutable;
using System.Text;

namespace Cairn.Formats;

/// <summary>
/// A fixed-size, NUL-terminated name field as stored on disk (bone, sphere, material, prop point,
/// submesh names). The raw bytes are kept whole because some stock files carry leftovers after the
/// terminator (prop point names such as <c>"primary_1\0" + "1"</c>), and a byte-exact writer must
/// put them back. <see cref="Text"/> is what the engine sees. Characters are Latin-1, one byte each.
/// </summary>
public readonly struct FixedString : IEquatable<FixedString>
{
    private readonly ImmutableArray<byte> _bytes;

    private FixedString(ImmutableArray<byte> bytes) => _bytes = bytes;

    /// <summary>The whole field, terminator and any bytes after it included.</summary>
    public ImmutableArray<byte> Bytes => _bytes.IsDefault ? [] : _bytes;

    /// <summary>The field size in bytes.</summary>
    public int Length => Bytes.Length;

    /// <summary>The name up to the first NUL.</summary>
    public string Text
    {
        get
        {
            var bytes = Bytes.AsSpan();
            int nul = bytes.IndexOf((byte)0);
            return Encoding.Latin1.GetString(nul < 0 ? bytes : bytes[..nul]);
        }
    }

    /// <summary>True when bytes other than zero follow the terminator.</summary>
    public bool HasTrailingBytes
    {
        get
        {
            var bytes = Bytes.AsSpan();
            int nul = bytes.IndexOf((byte)0);
            return nul >= 0 && bytes[nul..].ContainsAnyExcept((byte)0);
        }
    }

    /// <summary>Wraps raw field bytes exactly as read.</summary>
    public static FixedString FromBytes(ReadOnlySpan<byte> bytes) => new([.. bytes]);

    /// <summary>
    /// A clean field of <paramref name="length"/> bytes holding <paramref name="text"/> and zeros.
    /// The text must leave room for the terminator.
    /// </summary>
    /// <exception cref="ArgumentException">The text is too long or not Latin-1.</exception>
    public static FixedString FromText(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (length < 1) throw new ArgumentOutOfRangeException(nameof(length));
        if (text.Any(c => c > 0xFF || c == '\0'))
            throw new ArgumentException($"'{text}' contains characters a {length}-byte name cannot hold.", nameof(text));
        if (text.Length > length - 1)
            throw new ArgumentException(
                $"'{text}' is {text.Length} characters; this field holds at most {length - 1}.", nameof(text));
        var bytes = new byte[length];
        Encoding.Latin1.GetBytes(text, bytes);
        return new FixedString([.. bytes]);
    }

    /// <summary>The same field with its text replaced and the bytes after the terminator cleared.</summary>
    public FixedString WithText(string text) => FromText(text, Length);

    public bool Equals(FixedString other) => Bytes.AsSpan().SequenceEqual(other.Bytes.AsSpan());

    public override bool Equals(object? obj) => obj is FixedString other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes.AsSpan());
        return hash.ToHashCode();
    }

    public static bool operator ==(FixedString left, FixedString right) => left.Equals(right);

    public static bool operator !=(FixedString left, FixedString right) => !left.Equals(right);

    public override string ToString() => Text;
}
