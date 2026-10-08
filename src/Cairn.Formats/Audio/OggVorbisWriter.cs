using System.Runtime.InteropServices;
using System.Text;

namespace Cairn.Formats.Audio;

/// <summary>
/// Writes Ogg Vorbis files with the Xiph.Org reference encoder (libvorbis 1.3.7 and libogg 1.3.5, BSD licence), shipped
/// as <c>cairn-vorbis.dll</c> next to the program (built from the official source releases by native/vorbis/build.ps1).
/// Quality is libvorbis' VBR scale: -0.1 (smallest) to 1.0 (best); 0.5 is oggenc's "-q 5".
/// </summary>
public static class OggVorbisWriter
{
    /// <summary>The native library's name (without ".dll").</summary>
    public const string LibraryName = "cairn-vorbis";

    /// <summary>The lowest quality libvorbis accepts.</summary>
    public const float MinQuality = -0.1f;

    /// <summary>The highest quality libvorbis accepts.</summary>
    public const float MaxQuality = 1.0f;

    /// <summary>The usual quality (oggenc's default "-q 3" is 0.3; 0.5 keeps game sounds clean).</summary>
    public const float DefaultQuality = 0.5f;

    private const int Chunk = 4096;
    // Opaque native state is allocated generously and zeroed: every libvorbis/libogg struct used here is far smaller
    // (the largest, ogg_stream_state, is about 400 bytes on x64), and only ogg_page's fields are read from C#.
    private const int StateBytes = 4096;

    private static readonly Lazy<string?> s_version = new(LoadVersion);

    /// <summary>True when the encoder library loads (it ships with Cairn; false only in a damaged install).</summary>
    public static bool IsAvailable => s_version.Value is not null;

    /// <summary>The encoder's version ("Xiph.Org libVorbis 1.3.7"), or null when it does not load.</summary>
    public static string? Version => s_version.Value;

    /// <summary>The quality as oggenc's "-q" number ("q5" for 0.5, "q-1" for -0.1).</summary>
    public static string QualityLabel(float quality)
    {
        double q = Math.Round(Math.Clamp(quality, MinQuality, MaxQuality) * 10, 1);
        return "q" + q.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Encodes interleaved 16-bit samples. <paramref name="tags"/> become Vorbis comments ("LOOPSTART" = "1200").
    /// </summary>
    /// <exception cref="ArgumentException">The channel count, rate, quality or sample count is not usable.</exception>
    /// <exception cref="InvalidOperationException">The encoder library is missing or refuses the settings.</exception>
    public static byte[] Write(ReadOnlySpan<short> samples, int channels, int rate, float quality,
        IEnumerable<KeyValuePair<string, string>>? tags = null, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream(Math.Max(4096, samples.Length / 4));
        Write(output, samples, channels, rate, quality, tags, cancellationToken);
        return output.ToArray();
    }

    /// <summary>Encodes interleaved 16-bit samples into <paramref name="output"/>.</summary>
    /// <inheritdoc cref="Write(ReadOnlySpan{short}, int, int, float, IEnumerable{KeyValuePair{string, string}}?, CancellationToken)"/>
    public static void Write(Stream output, ReadOnlySpan<short> samples, int channels, int rate, float quality,
        IEnumerable<KeyValuePair<string, string>>? tags = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (channels is < 1 or > 8) throw new ArgumentException($"Ogg Vorbis output supports 1 to 8 channels, not {channels}.", nameof(channels));
        if (rate is < 1000 or > 192000) throw new ArgumentException($"Ogg Vorbis output supports 1,000 to 192,000 Hz, not {rate:N0} Hz.", nameof(rate));
        if (float.IsNaN(quality) || quality < MinQuality - 1e-4f || quality > MaxQuality + 1e-4f)
            throw new ArgumentException($"The quality must be between {MinQuality} and {MaxQuality}.", nameof(quality));
        if (samples.Length % channels != 0) throw new ArgumentException("The sample count is not a whole number of frames.", nameof(samples));
        if (!IsAvailable) throw new InvalidOperationException($"The Ogg Vorbis encoder ({LibraryName}.dll) could not be loaded.");
        quality = Math.Clamp(quality, MinQuality, MaxQuality);

        IntPtr vi = IntPtr.Zero, vc = IntPtr.Zero, vd = IntPtr.Zero, vb = IntPtr.Zero, os = IntPtr.Zero;
        IntPtr og = IntPtr.Zero, op = IntPtr.Zero, h1 = IntPtr.Zero, h2 = IntPtr.Zero, h3 = IntPtr.Zero;
        bool infoInit = false, commentInit = false, dspInit = false, blockInit = false, streamInit = false;
        try
        {
            vi = Alloc(); vc = Alloc(); vd = Alloc(); vb = Alloc(); os = Alloc(); og = Alloc(); op = Alloc();
            h1 = Alloc(); h2 = Alloc(); h3 = Alloc();
            Native.vorbis_info_init(vi); infoInit = true;
            int ret = Native.vorbis_encode_init_vbr(vi, channels, rate, quality);
            if (ret != 0)
                throw new InvalidOperationException($"The Ogg Vorbis encoder does not support {rate:N0} Hz with {channels} channel(s) at quality {quality:0.0#} (error {ret}).");
            Native.vorbis_comment_init(vc); commentInit = true;
            foreach (var (key, value) in tags ?? [])
            {
                if (string.IsNullOrEmpty(key) || key.Contains('=') || value is null) continue;
                IntPtr k = Utf8(key), v = Utf8(value);
                try { Native.vorbis_comment_add_tag(vc, k, v); }
                finally { Marshal.FreeHGlobal(k); Marshal.FreeHGlobal(v); }
            }
            Check(Native.vorbis_analysis_init(vd, vi), "vorbis_analysis_init"); dspInit = true;
            Check(Native.vorbis_block_init(vd, vb), "vorbis_block_init"); blockInit = true;
            Check(Native.ogg_stream_init(os, 0x43414952 /* any fixed serial: output is reproducible */), "ogg_stream_init"); streamInit = true;

            Check(Native.vorbis_analysis_headerout(vd, vc, h1, h2, h3), "vorbis_analysis_headerout");
            Check(Native.ogg_stream_packetin(os, h1), "ogg_stream_packetin");
            Check(Native.ogg_stream_packetin(os, h2), "ogg_stream_packetin");
            Check(Native.ogg_stream_packetin(os, h3), "ogg_stream_packetin");
            // the audio starts on a fresh page, as the Ogg Vorbis mapping requires
            while (Native.ogg_stream_flush(os, og) != 0) WritePage(output, og);

            int frames = samples.Length / channels;
            var floats = new float[Chunk];
            for (int start = 0, count; ; start += count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count = Math.Min(Chunk, frames - start);
                if (count > 0)
                {
                    IntPtr buffers = Native.vorbis_analysis_buffer(vd, count);
                    if (buffers == IntPtr.Zero) throw new InvalidOperationException("The Ogg Vorbis encoder ran out of memory.");
                    for (int c = 0; c < channels; c++)
                    {
                        for (int i = 0; i < count; i++) floats[i] = samples[(start + i) * channels + c] / 32768f;
                        Marshal.Copy(floats, 0, Marshal.ReadIntPtr(buffers, c * IntPtr.Size), count);
                    }
                }
                // a count of 0 marks the end of the stream (the loop's last pass, once every frame is in)
                Check(Native.vorbis_analysis_wrote(vd, count), "vorbis_analysis_wrote");
                bool eos = false;
                while (!eos && Native.vorbis_analysis_blockout(vd, vb) == 1)
                {
                    Check(Native.vorbis_analysis(vb, IntPtr.Zero), "vorbis_analysis");
                    Check(Native.vorbis_bitrate_addblock(vb), "vorbis_bitrate_addblock");
                    while (!eos && Native.vorbis_bitrate_flushpacket(vd, op) == 1)
                    {
                        Check(Native.ogg_stream_packetin(os, op), "ogg_stream_packetin");
                        while (Native.ogg_stream_pageout(os, og) != 0)
                        {
                            WritePage(output, og);
                            if (Native.ogg_page_eos(og) != 0) { eos = true; break; }
                        }
                    }
                }
                if (count == 0) break;
            }
            while (Native.ogg_stream_flush(os, og) != 0) WritePage(output, og);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException($"The Ogg Vorbis encoder ({LibraryName}.dll) could not be loaded: {ex.Message}", ex);
        }
        finally
        {
            // header packets point into the dsp state's buffers; nothing to free for them
            if (streamInit) Native.ogg_stream_clear(os);
            if (blockInit) Native.vorbis_block_clear(vb);
            if (dspInit) Native.vorbis_dsp_clear(vd);
            if (commentInit) Native.vorbis_comment_clear(vc);
            if (infoInit) Native.vorbis_info_clear(vi);
            foreach (var p in new[] { vi, vc, vd, vb, os, og, op, h1, h2, h3 }) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    private static IntPtr Alloc()
    {
        IntPtr p = Marshal.AllocHGlobal(StateBytes);
        Marshal.Copy(new byte[StateBytes], 0, p, StateBytes);
        return p;
    }

    private static IntPtr Utf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        return p;
    }

    private static void Check(int result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"The Ogg Vorbis encoder failed ({what} returned {result}).");
    }

    // ogg_page on x64 Windows: unsigned char *header; long header_len; unsigned char *body; long body_len (long = 32 bits)
    private static void WritePage(Stream output, IntPtr page)
    {
        WriteBytes(output, Marshal.ReadIntPtr(page, 0), Marshal.ReadInt32(page, 8));
        WriteBytes(output, Marshal.ReadIntPtr(page, 16), Marshal.ReadInt32(page, 24));
    }

    private static void WriteBytes(Stream output, IntPtr data, int length)
    {
        if (length <= 0) return;
        var bytes = new byte[length];
        Marshal.Copy(data, bytes, 0, length);
        output.Write(bytes, 0, length);
    }

    private static string? LoadVersion()
    {
        try
        {
            IntPtr p = Native.vorbis_version_string();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private static class Native
    {
        [DllImport(LibraryName)] public static extern IntPtr vorbis_version_string();
        [DllImport(LibraryName)] public static extern void vorbis_info_init(IntPtr vi);
        [DllImport(LibraryName)] public static extern void vorbis_info_clear(IntPtr vi);
        [DllImport(LibraryName)] public static extern int vorbis_encode_init_vbr(IntPtr vi, int channels, int rate, float quality);
        [DllImport(LibraryName)] public static extern void vorbis_comment_init(IntPtr vc);
        [DllImport(LibraryName)] public static extern void vorbis_comment_add_tag(IntPtr vc, IntPtr tag, IntPtr contents);
        [DllImport(LibraryName)] public static extern void vorbis_comment_clear(IntPtr vc);
        [DllImport(LibraryName)] public static extern int vorbis_analysis_init(IntPtr vd, IntPtr vi);
        [DllImport(LibraryName)] public static extern int vorbis_analysis_headerout(IntPtr vd, IntPtr vc, IntPtr op, IntPtr opComm, IntPtr opCode);
        [DllImport(LibraryName)] public static extern IntPtr vorbis_analysis_buffer(IntPtr vd, int vals);
        [DllImport(LibraryName)] public static extern int vorbis_analysis_wrote(IntPtr vd, int vals);
        [DllImport(LibraryName)] public static extern int vorbis_analysis_blockout(IntPtr vd, IntPtr vb);
        [DllImport(LibraryName)] public static extern int vorbis_analysis(IntPtr vb, IntPtr op);
        [DllImport(LibraryName)] public static extern int vorbis_bitrate_addblock(IntPtr vb);
        [DllImport(LibraryName)] public static extern int vorbis_bitrate_flushpacket(IntPtr vd, IntPtr op);
        [DllImport(LibraryName)] public static extern int vorbis_block_init(IntPtr vd, IntPtr vb);
        [DllImport(LibraryName)] public static extern int vorbis_block_clear(IntPtr vb);
        [DllImport(LibraryName)] public static extern void vorbis_dsp_clear(IntPtr vd);
        [DllImport(LibraryName)] public static extern int ogg_stream_init(IntPtr os, int serialno);
        [DllImport(LibraryName)] public static extern int ogg_stream_clear(IntPtr os);
        [DllImport(LibraryName)] public static extern int ogg_stream_packetin(IntPtr os, IntPtr op);
        [DllImport(LibraryName)] public static extern int ogg_stream_pageout(IntPtr os, IntPtr og);
        [DllImport(LibraryName)] public static extern int ogg_stream_flush(IntPtr os, IntPtr og);
        [DllImport(LibraryName)] public static extern int ogg_page_eos(IntPtr og);
    }
}
