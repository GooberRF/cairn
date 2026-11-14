using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Cairn.Formats.Audio;

/// <summary>What a FastTracker 2 module holds: its header and what libopenmpt reads from it.</summary>
public sealed record XmInfo
{
    /// <summary>The song's title (may be empty).</summary>
    public required string Title { get; init; }
    /// <summary>The tracker named in the header ("FastTracker v2.00", "OpenMPT 1.31").</summary>
    public required string Tracker { get; init; }
    /// <summary>libopenmpt's name for the format ("FastTracker 2 v1.04").</summary>
    public required string Format { get; init; }
    /// <summary>The header's format version (0x0104 for version 1.04).</summary>
    public required int Version { get; init; }
    /// <summary>Channels (tracks) of the patterns.</summary>
    public required int Channels { get; init; }
    /// <summary>Patterns.</summary>
    public required int Patterns { get; init; }
    /// <summary>Entries of the order list (the song's length in patterns).</summary>
    public required int Orders { get; init; }
    /// <summary>Instruments.</summary>
    public required int Instruments { get; init; }
    /// <summary>Samples, over all instruments.</summary>
    public required int Samples { get; init; }
    /// <summary>Separate songs in the order list (the first is the one rendered).</summary>
    public required int Subsongs { get; init; }
    /// <summary>The first song's length played once, as libopenmpt works it out.</summary>
    public required TimeSpan Duration { get; init; }
    /// <summary>The order the song goes back to when it repeats.</summary>
    public required int RestartPosition { get; init; }
    /// <summary>True for linear frequencies, false for the Amiga frequency table.</summary>
    public required bool LinearFrequencies { get; init; }
    /// <summary>The starting speed (ticks per row).</summary>
    public required int Speed { get; init; }
    /// <summary>The starting tempo (BPM).</summary>
    public required int Tempo { get; init; }
    /// <summary>The song's artist, when the tracker stored one (OpenMPT extension), else null.</summary>
    public string? Artist { get; init; }
}

/// <summary>A module rendered to 16-bit stereo PCM.</summary>
/// <param name="Info">The module's facts.</param>
/// <param name="Samples">Interleaved left/right samples at <see cref="XmModule.SampleRate"/>.</param>
/// <param name="Truncated">True when the render stopped at <see cref="XmModule.MaxDuration"/>.</param>
public sealed record XmRender(XmInfo Info, short[] Samples, bool Truncated);

/// <summary>
/// FastTracker 2 modules (<c>.xm</c>) through libopenmpt (BSD licence), shipped as <c>cairn-openmpt.dll</c> next to the
/// program (built from the official source release by native/openmpt/build.ps1, with no optional codecs). Only files
/// with the "Extended Module: " header are handed to the library, so no other module format is read. Every call makes
/// its own module handle and destroys it, so calls on different threads do not share state.
/// </summary>
public static class XmModule
{
    /// <summary>The native library's name (without ".dll").</summary>
    public const string LibraryName = "cairn-openmpt";

    /// <summary>The rate modules are rendered at.</summary>
    public const int SampleRate = 44_100;

    /// <summary>The longest render: a song that plays on past this is cut here (and says so).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(15);

    /// <summary>What the message says when the library does not load.</summary>
    public const string MissingMessage = "The XM player (cairn-openmpt.dll) is missing from this installation; reinstall Cairn to play .xm music.";

    private static ReadOnlySpan<byte> Magic => "Extended Module: "u8;
    private const int HeaderFields = 80; // magic, title, 0x1A, tracker, version, header size and the eight song fields
    private const int Chunk = 4096;

    private static readonly Lazy<string?> s_version = new(LoadVersion);

    /// <summary>True when the player library loads (it ships with Cairn; false only in a damaged install).</summary>
    public static bool IsAvailable => s_version.Value is not null;

    /// <summary>The player's version ("libopenmpt 0.8.9"), or null when it does not load.</summary>
    public static string? Version => s_version.Value;

    /// <summary>True when <paramref name="bytes"/> starts with the FastTracker 2 header ("Extended Module: ").</summary>
    public static bool IsXm(ReadOnlySpan<byte> bytes) => bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Reads the module's facts (libopenmpt loads it; nothing is rendered).</summary>
    /// <exception cref="AssetFormatException">Not a FastTracker 2 module, or one libopenmpt cannot load.</exception>
    /// <exception cref="InvalidOperationException">The player library is missing (<see cref="MissingMessage"/>).</exception>
    public static XmInfo ReadInfo(byte[] bytes, string name)
    {
        IntPtr mod = Load(bytes, name);
        try { return Facts(mod, bytes, name); }
        finally { Native.openmpt_module_destroy(mod); }
    }

    /// <summary>
    /// Renders the module's first song once, with no repeats, to 16-bit stereo at <see cref="SampleRate"/>, cut at
    /// <see cref="MaxDuration"/>. libopenmpt's defaults apply (full stereo separation, its usual interpolation), except
    /// that nothing is dithered, so the same module always gives the same samples.
    /// </summary>
    /// <exception cref="AssetFormatException">Not a FastTracker 2 module, or one libopenmpt cannot load.</exception>
    /// <exception cref="InvalidOperationException">The player library is missing (<see cref="MissingMessage"/>).</exception>
    public static XmRender Render(byte[] bytes, string name, CancellationToken cancellationToken = default)
    {
        IntPtr mod = Load(bytes, name);
        try
        {
            var info = Facts(mod, bytes, name);
            Native.openmpt_module_set_repeat_count(mod, 0);
            long maxFrames = (long)(MaxDuration.TotalSeconds * SampleRate);
            // sized from libopenmpt's length (plus the end fade); grown when the song plays longer
            long guess = Math.Clamp((long)((info.Duration.TotalSeconds + 2) * SampleRate), Chunk, maxFrames);
            var samples = new short[guess * 2];
            var buffer = new short[Chunk * 2];
            long frames = 0;
            bool truncated = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (frames >= maxFrames)
                {
                    truncated = Native.openmpt_module_read_interleaved_stereo(mod, SampleRate, 1, buffer) > 0;
                    break;
                }
                int want = (int)Math.Min(Chunk, maxFrames - frames);
                int got = (int)Native.openmpt_module_read_interleaved_stereo(mod, SampleRate, (nuint)want, buffer);
                if (got <= 0) break;
                got = Math.Min(got, want);
                if ((frames + got) * 2 > samples.Length) Array.Resize(ref samples, (int)Math.Min(Math.Max(samples.LongLength * 3 / 2, (frames + got) * 2), maxFrames * 2));
                Array.Copy(buffer, 0, samples, frames * 2, got * 2);
                frames += got;
            }
            if (frames * 2 != samples.Length) Array.Resize(ref samples, (int)(frames * 2));
            return new XmRender(info, samples, truncated);
        }
        finally
        {
            Native.openmpt_module_destroy(mod);
        }
    }

    /// <summary>Loads the module into a new handle (the caller destroys it).</summary>
    private static IntPtr Load(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        if (!IsXm(bytes)) throw new AssetFormatException($"'{name}' is not a FastTracker 2 module (it does not start with \"Extended Module: \").");
        if (bytes.Length < HeaderFields) throw new AssetFormatException($"'{name}' is cut off inside its header ({bytes.Length:N0} bytes).");
        if (!IsAvailable || s_silentLog == IntPtr.Zero) throw new InvalidOperationException(MissingMessage);
        IntPtr message = IntPtr.Zero;
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        // no dither: libopenmpt's default adds random noise, so two renders of one module would differ in the last bit
        IntPtr ctl = Marshal.StringToCoTaskMemUTF8("dither"), value = Marshal.StringToCoTaskMemUTF8("0");
        IntPtr ctls = Marshal.AllocCoTaskMem(4 * IntPtr.Size); // { ctl, value }, then the terminating { NULL, NULL }
        try
        {
            Marshal.WriteIntPtr(ctls, 0, ctl);
            Marshal.WriteIntPtr(ctls, IntPtr.Size, value);
            Marshal.WriteIntPtr(ctls, 2 * IntPtr.Size, IntPtr.Zero);
            Marshal.WriteIntPtr(ctls, 3 * IntPtr.Size, IntPtr.Zero);
            IntPtr mod = Native.openmpt_module_create_from_memory2(pin.AddrOfPinnedObject(), (nuint)bytes.Length, s_silentLog, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, out int error, out message, ctls);
            if (mod == IntPtr.Zero)
            {
                string why = message != IntPtr.Zero ? Marshal.PtrToStringUTF8(message) ?? "" : "";
                throw new AssetFormatException($"'{name}' is not a module the XM player can load" + (why.Length > 0 ? $": {why}" : error != 0 ? $" (error {error})." : "."));
            }
            string type = Metadata(mod, "type");
            if (!type.Equals("xm", StringComparison.OrdinalIgnoreCase))
            {
                Native.openmpt_module_destroy(mod);
                throw new AssetFormatException($"'{name}' loads as a '{type}' module, not FastTracker 2.");
            }
            return mod;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException(MissingMessage, ex);
        }
        finally
        {
            pin.Free();
            Marshal.FreeCoTaskMem(ctls);
            Marshal.FreeCoTaskMem(ctl);
            Marshal.FreeCoTaskMem(value);
            if (message != IntPtr.Zero) Native.openmpt_free_string(message);
        }
    }

    private static XmInfo Facts(IntPtr mod, byte[] bytes, string name)
    {
        ReadOnlySpan<byte> b = bytes;
        string artist = Metadata(mod, "artist");
        double seconds = Native.openmpt_module_get_duration_seconds(mod);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(b[74..]);
        return new XmInfo
        {
            Title = Metadata(mod, "title") is { Length: > 0 } title ? title : Latin1(b.Slice(17, 20)),
            Tracker = Metadata(mod, "tracker") is { Length: > 0 } tracker ? tracker : Latin1(b.Slice(38, 20)),
            Format = Metadata(mod, "type_long") is { Length: > 0 } format ? format : "FastTracker 2",
            Version = BinaryPrimitives.ReadUInt16LittleEndian(b[58..]),
            Channels = Math.Max(0, Native.openmpt_module_get_num_channels(mod)),
            Patterns = Math.Max(0, Native.openmpt_module_get_num_patterns(mod)),
            Orders = Math.Max(0, Native.openmpt_module_get_num_orders(mod)),
            Instruments = Math.Max(0, Native.openmpt_module_get_num_instruments(mod)),
            Samples = Math.Max(0, Native.openmpt_module_get_num_samples(mod)),
            Subsongs = Math.Max(1, Native.openmpt_module_get_num_subsongs(mod)),
            Duration = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds)),
            RestartPosition = BinaryPrimitives.ReadUInt16LittleEndian(b[66..]),
            LinearFrequencies = (flags & 1) != 0,
            Speed = BinaryPrimitives.ReadUInt16LittleEndian(b[76..]),
            Tempo = BinaryPrimitives.ReadUInt16LittleEndian(b[78..]),
            Artist = artist.Length > 0 ? artist : null,
        };
    }

    private static string Latin1(ReadOnlySpan<byte> text)
    {
        int end = text.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? text : text[..end]).Trim();
    }

    /// <summary>A metadata value ("" when absent); libopenmpt's copy is freed.</summary>
    private static string Metadata(IntPtr mod, string key)
    {
        IntPtr p = Native.openmpt_module_get_metadata(mod, key);
        if (p == IntPtr.Zero) return "";
        try { return (Marshal.PtrToStringUTF8(p) ?? "").Trim(); }
        finally { Native.openmpt_free_string(p); }
    }

    private static IntPtr s_silentLog;

    private static string? LoadVersion()
    {
        try
        {
            if (!NativeLibrary.TryLoad(LibraryName, typeof(XmModule).Assembly, null, out IntPtr library)) return null;
            // libopenmpt writes warnings to stderr unless given a logging function: its own silent one
            if (!NativeLibrary.TryGetExport(library, "openmpt_log_func_silent", out s_silentLog)) return null;
            IntPtr p = Native.openmpt_get_string("library_version");
            if (p == IntPtr.Zero) return null;
            // "0.8.9+r25652.pkg": the release, without the build's source revision
            try { return "libopenmpt " + (Marshal.PtrToStringUTF8(p) ?? "").Split('+')[0]; }
            finally { Native.openmpt_free_string(p); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private static class Native
    {
        [DllImport(LibraryName)] public static extern IntPtr openmpt_get_string([MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(LibraryName)] public static extern void openmpt_free_string(IntPtr str);
        [DllImport(LibraryName)]
        public static extern IntPtr openmpt_module_create_from_memory2(IntPtr filedata, nuint filesize, IntPtr logfunc, IntPtr loguser,
            IntPtr errfunc, IntPtr erruser, out int error, out IntPtr errorMessage, IntPtr ctls);
        [DllImport(LibraryName)] public static extern void openmpt_module_destroy(IntPtr mod);
        [DllImport(LibraryName)] public static extern IntPtr openmpt_module_get_metadata(IntPtr mod, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(LibraryName)] public static extern int openmpt_module_get_num_channels(IntPtr mod);
        [DllImport(LibraryName)] public static extern int openmpt_module_get_num_patterns(IntPtr mod);
        [DllImport(LibraryName)] public static extern int openmpt_module_get_num_orders(IntPtr mod);
        [DllImport(LibraryName)] public static extern int openmpt_module_get_num_instruments(IntPtr mod);
        [DllImport(LibraryName)] public static extern int openmpt_module_get_num_samples(IntPtr mod);
        [DllImport(LibraryName)] public static extern int openmpt_module_get_num_subsongs(IntPtr mod);
        [DllImport(LibraryName)] public static extern double openmpt_module_get_duration_seconds(IntPtr mod);
        [DllImport(LibraryName)] public static extern int openmpt_module_set_repeat_count(IntPtr mod, int repeatCount);
        [DllImport(LibraryName)] public static extern nuint openmpt_module_read_interleaved_stereo(IntPtr mod, int sampleRate, nuint count, [Out] short[] interleavedStereo);
    }
}
