using System.Buffers.Binary;
using System.Text;
using Cairn.Formats;

namespace Cairn.Formats.Audio;

/// <summary>What an audio file holds. Unknown values are null.</summary>
/// <param name="Container">"WAVE", "AIFF", "AIFF-C", "Ogg Vorbis", "MP3".</param>
/// <param name="Codec">"PCM", "IMA ADPCM", "Vorbis", "MPEG-1 Layer III", ...</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="BitsPerSample">Bits per sample for PCM-like codecs.</param>
/// <param name="Duration">Playing time.</param>
/// <param name="BitrateKbps">Nominal or average bitrate for compressed codecs.</param>
/// <param name="Note">Extra detail (encoder, VBR, tags).</param>
public sealed record AudioInfo(string Container, string Codec, int? SampleRate, int? Channels, int? BitsPerSample,
    TimeSpan? Duration, int? BitrateKbps = null, string? Note = null);

/// <summary>Where the PCM data of a WAVE or AIFF file is, for decoding.</summary>
internal sealed record PcmLayout(int FormatTag, int Channels, int SampleRate, int BitsPerSample, int BlockAlign,
    long DataOffset, long DataLength, bool BigEndian, string Compression);

/// <summary>Header readers for the audio types found in packfiles: WAVE, AIFF/AIFF-C, Ogg Vorbis and MP3.</summary>
public static class AudioProbe
{
    /// <summary>Reads the header of a WAVE file.</summary>
    /// <exception cref="AssetFormatException">Not a readable WAVE file.</exception>
    public static AudioInfo ProbeWav(ReadOnlySpan<byte> bytes, string name) => ProbeWav(bytes, bytes.Length, name);

    /// <summary>
    /// Reads the header of a WAVE file from its first bytes: <paramref name="head"/> must hold the chunks up to the
    /// data chunk's header, and <paramref name="totalSize"/> is the whole file's length (bounds the data length).
    /// </summary>
    /// <exception cref="AssetFormatException">Not a readable WAVE file.</exception>
    public static AudioInfo ProbeWav(ReadOnlySpan<byte> head, long totalSize, string name)
    {
        var layout = ReadWav(head, name, Math.Max(totalSize, head.Length));
        string codec = WaveCodecName(layout.FormatTag);
        int byteRate = layout.FormatTag == 1 ? layout.SampleRate * layout.BlockAlign : ReadWavByteRate(head);
        TimeSpan? duration = byteRate > 0 ? TimeSpan.FromSeconds((double)layout.DataLength / byteRate) : null;
        int? kbps = layout.FormatTag == 1 ? null : byteRate * 8 / 1000;
        return new AudioInfo("WAVE", codec, layout.SampleRate, layout.Channels, layout.BitsPerSample > 0 ? layout.BitsPerSample : null, duration, kbps);
    }

    /// <summary>Reads the header of an AIFF or AIFF-C file.</summary>
    /// <exception cref="AssetFormatException">Not a readable AIFF file.</exception>
    public static AudioInfo ProbeAiff(ReadOnlySpan<byte> bytes, string name)
    {
        var layout = ReadAiff(bytes, name, out long frames, out bool isAifc);
        if (layout.Compression.Contains("'ima4'", StringComparison.Ordinal) && layout.DataOffset >= 0 && layout.Channels > 0)
        {
            // IMA4 packs 64 samples per channel into 34-byte packets; count from the data actually present.
            frames = layout.DataLength / (34L * layout.Channels) * 64;
        }
        TimeSpan? duration = layout.SampleRate > 0 ? TimeSpan.FromSeconds((double)frames / layout.SampleRate) : null;
        return new AudioInfo(isAifc ? "AIFF-C" : "AIFF", layout.Compression, layout.SampleRate, layout.Channels, layout.BitsPerSample, duration);
    }

    /// <summary>Reads an Ogg Vorbis stream's headers (and its length) with NVorbis.</summary>
    /// <exception cref="AssetFormatException">Not a readable Ogg Vorbis stream.</exception>
    public static AudioInfo ProbeOgg(Stream stream, string name)
    {
        try
        {
            using var reader = new NVorbis.VorbisReader(stream, closeOnDispose: false);
            string? vendor = reader.Tags?.EncoderVendor;
            return new AudioInfo("Ogg Vorbis", "Vorbis", reader.SampleRate, reader.Channels, null, reader.TotalTime,
                reader.NominalBitrate > 0 ? reader.NominalBitrate / 1000 : null, string.IsNullOrWhiteSpace(vendor) ? null : vendor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new AssetFormatException($"'{name}' is not a readable Ogg Vorbis stream: {ex.Message}", ex);
        }
    }

    /// <summary>Reads the first MPEG audio frame (after any ID3v2 tag) and a Xing/Info header if present.</summary>
    /// <exception cref="AssetFormatException">No MPEG audio frame found.</exception>
    public static AudioInfo ProbeMp3(ReadOnlySpan<byte> head, long totalSize, string name)
    {
        int at = 0;
        if (head.Length >= 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
        {
            at = 10 + ((head[6] & 0x7F) << 21 | (head[7] & 0x7F) << 14 | (head[8] & 0x7F) << 7 | (head[9] & 0x7F));
        }
        int tagBytes = at;
        for (; at + 4 <= head.Length; at++)
        {
            if (head[at] != 0xFF || (head[at + 1] & 0xE0) != 0xE0) continue;
            if (ParseMpegHeader(head[at..], out var frame)) break;
        }
        if (at + 4 > head.Length || !ParseMpegHeader(head[at..], out var h))
        {
            throw new AssetFormatException($"'{name}' has no MPEG audio frame in its first {head.Length:N0} bytes.");
        }

        // Xing/Info (VBR) header: in the first frame after the side information.
        int sideInfo = h.Version == 1 ? (h.Channels == 1 ? 17 : 32) : (h.Channels == 1 ? 9 : 17);
        int xing = at + 4 + sideInfo;
        string? note = null;
        TimeSpan? duration = null;
        int kbps = h.BitrateKbps;
        if (xing + 12 <= head.Length && (head.Slice(xing, 4).SequenceEqual("Xing"u8) || head.Slice(xing, 4).SequenceEqual("Info"u8)))
        {
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(head[(xing + 4)..]);
            if ((flags & 1) != 0)
            {
                uint frames = BinaryPrimitives.ReadUInt32BigEndian(head[(xing + 8)..]);
                duration = TimeSpan.FromSeconds((double)frames * h.SamplesPerFrame / h.SampleRate);
                if (duration.Value.TotalSeconds > 0) kbps = (int)((totalSize - tagBytes) * 8 / duration.Value.TotalSeconds / 1000);
            }
            note = head.Slice(xing, 4).SequenceEqual("Xing"u8) ? "variable bitrate (Xing header)" : "constant bitrate (Info header)";
        }
        duration ??= h.BitrateKbps > 0 ? TimeSpan.FromSeconds((totalSize - at) * 8.0 / (h.BitrateKbps * 1000.0)) : null;
        string layer = h.Layer switch { 1 => "I", 2 => "II", _ => "III" };
        string version = h.Version switch { 1 => "1", 2 => "2", _ => "2.5" };
        return new AudioInfo("MP3", $"MPEG-{version} Layer {layer}", h.SampleRate, h.Channels, null, duration, kbps,
            note ?? (tagBytes > 0 ? "ID3v2 tag" : null));
    }

    private readonly record struct MpegHeader(int Version, int Layer, int BitrateKbps, int SampleRate, int Channels, int SamplesPerFrame);

    private static readonly int[,] Bitrates =
    {
        // V1 L1, V1 L2, V1 L3, V2 L1, V2 L2/L3
        { 0, 0, 0, 0, 0 }, { 32, 32, 32, 32, 8 }, { 64, 48, 40, 48, 16 }, { 96, 56, 48, 56, 24 }, { 128, 64, 56, 64, 32 },
        { 160, 80, 64, 80, 40 }, { 192, 96, 80, 96, 48 }, { 224, 112, 96, 112, 56 }, { 256, 128, 112, 128, 64 },
        { 288, 160, 128, 144, 80 }, { 320, 192, 160, 160, 96 }, { 352, 224, 192, 176, 112 }, { 384, 256, 224, 192, 128 },
        { 416, 320, 256, 224, 144 }, { 448, 384, 320, 256, 160 },
    };

    private static bool ParseMpegHeader(ReadOnlySpan<byte> b, out MpegHeader header)
    {
        header = default;
        if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) return false;
        int versionBits = (b[1] >> 3) & 3;
        int layerBits = (b[1] >> 1) & 3;
        int bitrateIndex = b[2] >> 4;
        int rateIndex = (b[2] >> 2) & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) return false;
        int version = versionBits switch { 3 => 1, 2 => 2, _ => 25 };
        int layer = 4 - layerBits;
        int column = version == 1 ? layer - 1 : (layer == 1 ? 3 : 4);
        int rate = new[] { 44100, 48000, 32000 }[rateIndex] / (version == 1 ? 1 : version == 2 ? 2 : 4);
        int channels = (b[3] >> 6) == 3 ? 1 : 2;
        int samples = layer == 1 ? 384 : layer == 2 || version == 1 ? 1152 : 576;
        header = new MpegHeader(version, layer, Bitrates[bitrateIndex, column], rate, channels, samples);
        return true;
    }

    internal static PcmLayout ReadWav(ReadOnlySpan<byte> b, string name) => ReadWav(b, name, b.Length);

    /// <param name="b">The file, or at least its chunks up to the data chunk's header.</param>
    /// <param name="name">For messages.</param>
    /// <param name="totalLength">The whole file's length (the data length is clamped to it, not to <paramref name="b"/>).</param>
    private static PcmLayout ReadWav(ReadOnlySpan<byte> b, string name, long totalLength)
    {
        if (b.Length < 12 || !b[..4].SequenceEqual("RIFF"u8) || !b.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new AssetFormatException($"'{name}' is not a RIFF WAVE file.");
        }
        PcmLayout? fmt = null;
        long dataOffset = -1, dataLength = 0;
        int at = 12;
        while (at + 8 <= b.Length)
        {
            var id = b.Slice(at, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[(at + 4)..]);
            int body = at + 8;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || body + 16 > b.Length) throw new AssetFormatException($"'{name}' has a truncated format chunk.");
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(b[body..]);
                if (tag == 0xFFFE && size >= 40 && body + 26 <= b.Length) tag = BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 24)..]);
                fmt = new PcmLayout(tag, BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 2)..]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(body + 4)..]), BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 14)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(b[(body + 12)..]), 0, 0, false, WaveCodecName(tag));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataOffset = body;
                dataLength = Math.Min(size, Math.Max(0, totalLength - body));
                if (fmt is not null) break;
            }
            long next = body + (long)size + (size & 1);
            if (next > int.MaxValue) break;
            at = (int)next;
        }
        if (fmt is null) throw new AssetFormatException($"'{name}' has no format chunk.");
        if (dataOffset < 0) throw new AssetFormatException($"'{name}' has no data chunk.");
        return fmt with { DataOffset = dataOffset, DataLength = dataLength };
    }

    private static int ReadWavByteRate(ReadOnlySpan<byte> b)
    {
        int at = 12;
        while (at + 8 <= b.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(b[(at + 4)..]);
            if (b.Slice(at, 4).SequenceEqual("fmt "u8) && at + 20 <= b.Length) return (int)BinaryPrimitives.ReadUInt32LittleEndian(b[(at + 16)..]);
            long next = at + 8L + size + (size & 1);
            if (next > int.MaxValue) break;
            at = (int)next;
        }
        return 0;
    }

    private static string WaveCodecName(int tag) => tag switch
    {
        1 => "PCM",
        2 => "Microsoft ADPCM",
        3 => "IEEE float",
        6 => "A-law",
        7 => "µ-law",
        0x11 => "IMA ADPCM",
        0x55 => "MP3",
        _ => $"format 0x{tag:X4}",
    };

    internal static PcmLayout ReadAiff(ReadOnlySpan<byte> b, string name, out long frames, out bool isAifc)
    {
        if (b.Length < 12 || !b[..4].SequenceEqual("FORM"u8)) throw new AssetFormatException($"'{name}' is not an AIFF file.");
        var form = b.Slice(8, 4);
        isAifc = form.SequenceEqual("AIFC"u8);
        if (!isAifc && !form.SequenceEqual("AIFF"u8)) throw new AssetFormatException($"'{name}' is not an AIFF file.");
        int channels = 0, bits = 0, rate = 0;
        frames = 0;
        string compression = "PCM";
        string compressionId = "NONE";
        bool haveComm = false;
        long dataOffset = -1, dataLength = 0;
        int at = 12;
        while (at + 8 <= b.Length)
        {
            var id = b.Slice(at, 4);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(b[(at + 4)..]);
            int body = at + 8;
            if (id.SequenceEqual("COMM"u8))
            {
                if (size < 18 || body + 18 > b.Length) throw new AssetFormatException($"'{name}' has a truncated COMM chunk.");
                channels = BinaryPrimitives.ReadInt16BigEndian(b[body..]);
                frames = BinaryPrimitives.ReadUInt32BigEndian(b[(body + 2)..]);
                bits = BinaryPrimitives.ReadInt16BigEndian(b[(body + 6)..]);
                rate = (int)Math.Round(ReadExtended(b.Slice(body + 8, 10)));
                if (isAifc && size >= 22 && body + 22 <= b.Length)
                {
                    compressionId = Encoding.ASCII.GetString(b.Slice(body + 18, 4));
                    string label = size > 22 && body + 23 <= b.Length && body + 23 + b[body + 22] <= b.Length
                        ? Encoding.Latin1.GetString(b.Slice(body + 23, b[body + 22])) : string.Empty;
                    if (compressionId == "ima4" && string.IsNullOrWhiteSpace(label)) label = "IMA4 ADPCM";
                    compression = compressionId is "NONE" or "twos" ? "PCM" : compressionId == "sowt" ? "PCM (little-endian)"
                        : string.IsNullOrWhiteSpace(label) ? $"'{compressionId}'" : $"{label} ('{compressionId}')";
                }
                haveComm = true;
            }
            else if (id.SequenceEqual("SSND"u8))
            {
                if (body + 8 > b.Length) throw new AssetFormatException($"'{name}' has a truncated SSND chunk.");
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(b[body..]);
                dataOffset = body + 8L + offset;
                if (dataOffset > b.Length)
                    throw new AssetFormatException($"'{name}' is damaged: its SSND chunk puts the sound data at offset {offset:N0}, past the end of the file.");
                dataLength = Math.Max(0, Math.Min((long)size - 8 - offset, b.Length - dataOffset));
            }
            long next = body + (long)size + (size & 1);
            if (next > int.MaxValue) break;
            at = (int)next;
        }
        if (!haveComm) throw new AssetFormatException($"'{name}' has no COMM chunk.");
        bool bigEndian = compressionId != "sowt";
        int tag = compressionId is "NONE" or "twos" or "sowt" ? 1 : 0;
        int blockAlign = channels * ((bits + 7) / 8);
        return new PcmLayout(tag, channels, rate, bits, blockAlign, dataOffset, dataLength, bigEndian, compression);
    }

    /// <summary>An 80-bit IEEE extended float (AIFF sample rates).</summary>
    private static double ReadExtended(ReadOnlySpan<byte> b)
    {
        int exponent = ((b[0] & 0x7F) << 8) | b[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(b[2..]);
        if (exponent == 0 && mantissa == 0) return 0;
        double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (b[0] & 0x80) != 0 ? -value : value;
    }
}
