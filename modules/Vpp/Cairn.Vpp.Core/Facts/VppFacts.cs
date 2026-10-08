using System.Collections.Concurrent;
using System.Globalization;
using Cairn.Assets;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Facts;

/// <summary>One labelled value in the details pane ("Dimensions", "256 x 256").</summary>
public sealed record VppFactRow(string Label, string Value);

/// <summary>The facts about one entry: ordered rows plus warnings worth highlighting.</summary>
public sealed record VppFactSheet(ImmutableArray<VppFactRow> Rows, ImmutableArray<string> Warnings)
{
    /// <summary>The value of the first row with this label, or null.</summary>
    public string? this[string label] => Rows.FirstOrDefault(r => r.Label == label)?.Value;
}

/// <summary>What a describer may look at besides the entry itself.</summary>
/// <param name="Package">The packfile the entry is in (for "is this referenced file present?"), if any.</param>
/// <param name="Resolver">Game data lookup (for "is this present in the game?"), if any.</param>
public sealed record VppFactsContext(VppPackage? Package = null, AssetResolver? Resolver = null)
{
    public static VppFactsContext None { get; } = new();
}

/// <summary>The entry a describer works on, with helpers that read it within limits.</summary>
public sealed class VppFactInput(string name, Func<Stream> open, long size, VppFactsContext context)
{
    /// <summary>Largest entry a describer reads whole (meshes, effects, levels).</summary>
    public const int MaxWholeBytes = 128 << 20;

    private byte[]? _all;

    public string Name { get; } = name;
    public long Size { get; } = size;
    public VppFactsContext Context { get; } = context;

    /// <summary>A fresh stream over the entry.</summary>
    public Stream Open() => open();

    /// <summary>The whole entry (cached).</summary>
    /// <exception cref="Formats.AssetFormatException">Larger than <see cref="MaxWholeBytes"/>.</exception>
    public byte[] ReadAll()
    {
        if (_all is not null) return _all;
        if (Size > MaxWholeBytes) throw new Formats.AssetFormatException($"'{Name}' is too large to inspect ({Size:N0} bytes).");
        using var stream = open();
        using var memory = new MemoryStream((int)Math.Max(0, Size));
        stream.CopyTo(memory);
        _all = memory.ToArray();
        return _all;
    }

    /// <summary>Up to <paramref name="count"/> bytes from the start.</summary>
    public byte[] ReadHead(int count)
    {
        if (_all is not null) return _all.Length <= count ? _all : _all[..count];
        using var stream = open();
        var buffer = new byte[(int)Math.Min(count, Math.Max(0, Size))];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }
}

/// <summary>Collects rows and warnings.</summary>
public sealed class VppFactSheetBuilder
{
    private readonly ImmutableArray<VppFactRow>.Builder _rows = ImmutableArray.CreateBuilder<VppFactRow>();
    private readonly ImmutableArray<string>.Builder _warnings = ImmutableArray.CreateBuilder<string>();

    public VppFactSheetBuilder Add(string label, string? value)
    {
        if (value is not null) _rows.Add(new VppFactRow(label, value));
        return this;
    }

    public VppFactSheetBuilder Add(string label, long value) => Add(label, value.ToString("N0", CultureInfo.CurrentCulture));

    public VppFactSheetBuilder Warn(string warning)
    {
        _warnings.Add(warning);
        return this;
    }

    public VppFactSheet Build() => new(_rows.ToImmutable(), _warnings.ToImmutable());
}

/// <summary>Adds the facts of one type to a sheet. May throw: <c>VppFacts.Describe</c> turns exceptions into rows.</summary>
public delegate void VppFactDescriber(VppFactInput input, VppFactSheetBuilder sheet);

/// <summary>
/// Facts about packfile entries for the details pane: a registry of describers by extension (and by
/// content when the name does not say), each of which reports what it can and never throws to the
/// caller. Describers are pluggable: <see cref="Register"/> replaces the one for an extension (the level
/// reader plugs into ".rfl" this way).
/// </summary>
public static partial class VppFacts
{
    private static readonly ConcurrentDictionary<string, VppFactDescriber> Describers = new(StringComparer.OrdinalIgnoreCase);

    static VppFacts()
    {
        foreach (string ext in new[] { ".tga", ".dds", ".vbm", ".png", ".jpg", ".jpeg" }) Describers[ext] = DescribeImage;
        Describers[".psd"] = DescribePsd;
        Describers[".wav"] = DescribeAudio;
        Describers[".ogg"] = DescribeAudio;
        Describers[".aif"] = DescribeAudio;
        Describers[".aiff"] = DescribeAudio;
        Describers[".aifc"] = DescribeAudio;
        Describers[".mp3"] = DescribeAudio;
        Describers[".vse"] = DescribePs2Sound;
        Describers[".vmu"] = DescribePs2Sound;
        Describers[".v3m"] = DescribeMesh;
        Describers[".v3c"] = DescribeMesh;
        Describers[".v3d"] = DescribeMesh;
        Describers[".vcm"] = DescribeMesh;
        Describers[".rfm"] = DescribeMesh;
        Describers[".rfc"] = DescribeMesh;
        Describers[".rfa"] = DescribeClip;
        Describers[".mvf"] = DescribeMvf;
        Describers[".vfx"] = DescribeEffect;
        Describers[".atx"] = DescribeAtx;
        foreach (string ext in new[] { ".tbl", ".txt", ".log", ".ini", ".gltf" }) Describers[ext] = DescribeText;
        Describers[".vf"] = DescribeFont;
        Describers[".rfg"] = DescribeGroup;
        Describers[".rfl"] = DescribeLevel;
        Describers[".peg"] = DescribeTexturePack;
    }

    /// <summary>Installs (or replaces) the describer for an extension such as ".rfl".</summary>
    public static void Register(string extension, VppFactDescriber describer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentNullException.ThrowIfNull(describer);
        Describers[extension.StartsWith('.') ? extension : "." + extension] = describer;
    }

    /// <summary>The describer installed for an extension, or null.</summary>
    public static VppFactDescriber? DescriberFor(string extension) => Describers.TryGetValue(extension, out var d) ? d : null;

    /// <summary>Describes a packfile entry. Never throws: read and format errors become an "Error" row and a warning.</summary>
    public static VppFactSheet Describe(VppItem item, VppFactsContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var sheet = Describe(item.Name, item.Source.Open, item.Size, context ?? VppFactsContext.None);
        // an entry a PEG conversion made says where it came from
        if (item.Source is MemorySource memory && Ps2.PegConverter.NoteFor(memory.Bytes) is { } note)
            sheet = sheet with { Rows = sheet.Rows.Add(new VppFactRow("Converted", note)) };
        return sheet;
    }

    /// <summary>Describes data under a name. Never throws: read and format errors become an "Error" row and a warning.</summary>
    public static VppFactSheet Describe(string name, Func<Stream> open, long size, VppFactsContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(open);
        var input = new VppFactInput(name, open, size, context ?? VppFactsContext.None);
        var sheet = new VppFactSheetBuilder();
        var type = VppFileTypes.Describe(name);
        sheet.Add("Type", type.DisplayName);
        sheet.Add("Size", FormatSize(size));
        // Alpine Faction is the baseline: only whether the game loads the type at all (with the first Alpine version
        // when an Alpine-added type has a known one).
        sheet.Add("Game", !type.GameLoads ? "Not loaded by the game"
            : type.AlpineSince is { } since ? $"Loaded by the game (Alpine Faction {since} or later)" : "Loaded by the game");

        string ext = Editing.VppNames.ExtensionOf(name);
        byte[] head;
        try
        {
            head = input.ReadHead(64);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sheet.Add("Error", $"The data cannot be read: {ex.Message}").Warn($"The data cannot be read: {ex.Message}");
            return sheet.Build();
        }

        // Content first: some entries are named for a type they do not hold.
        string? actual = SniffExtension(head);
        if (actual is not null && !SameFamily(actual, ext))
        {
            string found = VppFileTypes.Describe("x" + actual).DisplayName;
            sheet.Add("Content", $"{found} (the extension says {type.DisplayName})")
                .Warn($"'{name}': the extension says {type.DisplayName}, but the content is {found}.");
            ext = actual;
        }

        var describer = DescriberFor(ext);
        if (describer is null)
        {
            sheet.Add("First bytes", Hex(head));
            return sheet.Build();
        }
        try
        {
            describer(input, sheet);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sheet.Add("Error", ex.Message).Warn(ex.Message);
            sheet.Add("First bytes", Hex(head));
        }
        return sheet.Build();
    }

    /// <summary>The extension the first bytes identify, or null when they do not identify one.</summary>
    public static string? SniffExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4)
        {
            if (b[..4].SequenceEqual("DDS "u8)) return ".dds";
            if (b[..4].SequenceEqual("OggS"u8)) return ".ogg";
            if (b[..4].SequenceEqual("VSFX"u8)) return ".vfx";
            if (b[..4].SequenceEqual("D3FR"u8)) return ".v3m";
            if (b[..4].SequenceEqual("MCFR"u8)) return ".v3c";
            if (b[..4].SequenceEqual("VFNT"u8)) return ".vf";
            if (b[..4].SequenceEqual(".vbm"u8)) return ".vbm";
            if (b[..4].SequenceEqual("8BPS"u8)) return ".psd";
            if (b[0] == 0x55 && b[1] == 0xDA && b[2] == 0xBA && b[3] == 0xD4) return ".rfl";
            if (b[0] == 0x0D && b[1] == 0xD0 && b[2] == 0x3D && b[3] == 0xD4) return ".rfg";
            if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return ".png";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
            if (b[..4].SequenceEqual("VMVF"u8)) return ".rfa";
            if (b[..4].SequenceEqual("GEKV"u8)) return ".peg";
        }
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WAVE"u8)) return ".wav";
        if (b.Length >= 12 && b[..4].SequenceEqual("FORM"u8) && (b.Slice(8, 4).SequenceEqual("AIFF"u8) || b.Slice(8, 4).SequenceEqual("AIFC"u8))) return ".aif";
        if (b.Length >= 3 && b[..3].SequenceEqual("ID3"u8)) return ".mp3";
        if (b.Length >= 15 && (b[..15].SequenceEqual("<!DOCTYPE html>"u8) || b[..6].SequenceEqual("<html>"u8))) return ".html";
        return null;
    }

    /// <summary>True when content of type <paramref name="actual"/> is fine under extension <paramref name="named"/>.</summary>
    internal static bool SameFamily(string actual, string named) => (actual, named) switch
    {
        _ when actual == named => true,
        (".v3m", ".v3d") or (".v3m", ".v3c") or (".v3c", ".v3d") => true,
        (".v3c", ".vcm") => true, // the exporter's name for a character mesh (the PlayStation 2 packfiles hold them)
        (".rfa", ".mvf") => true,
        (".jpg", ".jpeg") => true,
        (".aif", ".aiff" or ".aifc") => true,
        _ => false,
    };

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB ({bytes:N0} bytes)",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB ({bytes:N0} bytes)",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB ({bytes:N0} bytes)",
    };

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
        : t.TotalMinutes >= 1 ? t.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.00} s");

    /// <summary>Space-separated hex of up to 64 bytes.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 64) bytes = bytes[..64];
        return bytes.Length == 0 ? "(empty)" : Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b);
    }
}
