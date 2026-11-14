using System.Globalization;
using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Formats.XForge;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Conversion;

/// <summary>Red Faction for N-Gage level options of a conversion.</summary>
public sealed record LvlXffOptions
{
    /// <summary>Red Faction metres per N-Gage unit (<see cref="XffSpace.RfScale"/>, 2.35, by default), from <see cref="XffSpace.MinScale"/> to <see cref="XffSpace.MaxScale"/>.</summary>
    public float Scale { get; init; } = XffSpace.RfScale;

    /// <summary>
    /// Static meshes and group files: copy the textures the level uses beside the output, a <c>.pcx</c> written as the
    /// <c>.tga</c> the output names and a 16-bit <c>.tga</c> with the alpha fix (<see cref="XffTextureFix.ForPc"/>).
    /// </summary>
    public bool CopyTextures { get; init; } = true;
}

/// <summary>
/// Prepares a Red Faction for N-Gage level for the shared converters: reads it again at the chosen scale, puts its
/// reading notes (object mapping, what was dropped) first in the report, and, for static meshes and group files, names
/// each <c>.pcx</c> texture as <c>.tga</c> (Red Faction's editor and levels take <c>.tga</c>) and copies the textures it
/// uses beside the output, a <c>.pcx</c> written as that <c>.tga</c> (<see cref="XffTextureFix.ForPc"/>). A 16-bit
/// <c>.tga</c> that declares an alpha bit no pixel sets is made opaque (<see cref="XffTextureFix"/>), in those copies and
/// in the PNGs of glTF and OBJ, as the N-Gage game draws it and as the Meshes module converts it.
/// </summary>
internal static class XffLevelConversion
{
    private const int Listed = 8;

    public sealed record Prepared(RfLevel Level, LvlConvertOptions Options, ImmutableArray<LvlConvertIssue> Issues, ImmutableArray<LvlSideFile> SideFiles);

    /// <summary>The level prepared for <paramref name="options"/>, whose scale must be valid (<see cref="XffSpace.IsValidScale"/>).</summary>
    /// <exception cref="Cairn.Formats.AssetFormatException">The scale is out of range.</exception>
    public static Prepared Prepare(RfLevel level, LvlConvertOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var xff = level.Xff!;
        float scale = options.Xff.Scale;
        if (MathF.Abs(scale - xff.Scale) > 1e-6f && xff.Source is { } source)
        {
            level = XffLevelReader.Rescaled(source, scale);
            xff = level.Xff!;
        }
        var r = new LvlReportBuilder();
        foreach (var note in xff.Notes) r.Add(note.Severity, note.Kind, note.Code, note.Message);
        if (options.ResolvedGeometry == LvlGeometryChoice.Brushes)
            foreach (var note in xff.BrushNotes) r.Add(note.Severity, note.Kind, note.Code, note.Message);
        if (options.Target == LvlConvertTarget.Rfg && level.PlayerStart is { } start)
            r.Info(LvlConvertCodes.XffObjects, $"The N-Gage player start is at {Vec(start.Position)} (facing {Vec(start.Orientation.Forward)}); a group file cannot hold it, so place the player start there in the editor.");

        var sides = ImmutableArray<LvlSideFile>.Empty;
        if (options.Target is LvlConvertTarget.V3m or LvlConvertTarget.Rfg)
        {
            level = WithPcNames(level);
            int pcx = xff.Textures.Select(FileOf).Distinct(StringComparer.OrdinalIgnoreCase).Count(XffTextureFix.IsPcx);
            if (options.Xff.CopyTextures) sides = CopyTextures(xff.Textures, options, r, ct);
            else if (xff.Textures.Length > 0)
                r.Info(LvlConvertCodes.XffTextures, $"The {LvlReportBuilder.Count(xff.Textures.Length, "texture")} the level uses were not copied (the option is off); the output names them as stored"
                    + (pcx == 0 ? "." : $", {LvlReportBuilder.Count(pcx, ".pcx texture")} as .tga."));
        }
        else if (options.ExportTextures && options.TextureResolver is { } resolver && !xff.Textures.IsEmpty)
            options = options with { TextureLoader = OpaqueLoader(xff.Textures, resolver, options.TextureLoader, r, ct) };
        return new Prepared(level, options, r.Build().Issues, sides);
    }

    /// <summary>
    /// <paramref name="level"/> with each <c>.pcx</c> texture named as <c>.tga</c> (<see cref="XffTextureFix.PcName"/>), in
    /// its compiled geometry and its brushes: Red Faction's editor and levels take <c>.tga</c> textures.
    /// </summary>
    private static RfLevel WithPcNames(RfLevel level)
    {
        static RfGeometry Renamed(RfGeometry g) => g.Textures.Any(XffTextureFix.IsPcx) ? g with { Textures = [.. g.Textures.Select(XffTextureFix.PcName)] } : g;
        return level with
        {
            StaticGeometry = Renamed(level.StaticGeometry),
            Brushes = [.. level.Brushes.Select(b => b with { Geometry = Renamed(b.Geometry) })],
            Movers = [.. level.Movers.Select(b => b with { Geometry = Renamed(b.Geometry) })],
        };
    }

    /// <summary>A texture's file name without any folder part.</summary>
    private static string FileOf(string name) => Path.GetFileName(name.Replace('\\', '/'));

    /// <summary>The bytes of the file named <paramref name="file"/> exactly (not a superseding sibling), or null when it is not found or cannot be read.</summary>
    private static byte[]? Read(AssetResolver resolver, string file)
    {
        try
        {
            return resolver.Resolve(file) is { } location && Path.GetFileName(location.ResolvedName).Equals(file, StringComparison.OrdinalIgnoreCase)
                ? location.ReadAllBytes() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException) { return null; }
    }

    /// <summary>The level's textures from the same <c>.cfl</c> or folder (through the conversion's texture resolver), each <c>.pcx</c> as a <c>.tga</c>, the 16-bit alpha fixed.</summary>
    private static ImmutableArray<LvlSideFile> CopyTextures(ImmutableArray<string> names, LvlConvertOptions options, LvlReportBuilder r, CancellationToken ct)
    {
        if (names.IsEmpty) return [];
        if (options.TextureResolver is not { } resolver)
        {
            r.Warning(LvlConvertCodes.XffTextures, "No texture source was given, so the level's textures were not copied beside the output.", LvlIssueKind.Dropped);
            return [];
        }
        var files = ImmutableArray.CreateBuilder<LvlSideFile>();
        var missing = new List<string>();
        var unreadable = new List<string>();
        var opaque = new List<string>();
        int converted = 0;
        foreach (string name in names)
        {
            ct.ThrowIfCancellationRequested();
            string file = FileOf(name), output = XffTextureFix.PcName(file);
            if (files.Any(f => f.FileName.Equals(output, StringComparison.OrdinalIgnoreCase))) continue;
            if (Read(resolver, file) is not { } bytes)
            {
                missing.Add(file);
                continue;
            }
            byte[] copy;
            try { copy = XffTextureFix.ForPc(bytes, file); }
            catch (ImageDecodeException)
            {
                unreadable.Add(file);
                continue;
            }
            if (XffTextureFix.IsPcx(file)) converted++;
            else if (!ReferenceEquals(copy, bytes)) opaque.Add(file);
            files.Add(new LvlSideFile(output, copy));
        }
        if (files.Count > 0)
            r.Info(LvlConvertCodes.XffTextures, $"{LvlReportBuilder.Count(files.Count, "texture was", "textures were")} copied beside the output"
                + (converted == 0 ? "." : $"; {LvlReportBuilder.Count(converted, ".pcx texture was", ".pcx textures were")} written as .tga, the name the output gives {(converted == 1 ? "it" : "them")} (Red Faction's editor and levels take .tga, not .pcx)."));
        if (unreadable.Count > 0)
            r.Warning(LvlConvertCodes.XffTextures, $"{LvlReportBuilder.Count(unreadable.Count, "texture")} could not be read as an image and {(unreadable.Count == 1 ? "was" : "were")} not copied: {Names(unreadable)}.", LvlIssueKind.Dropped);
        if (opaque.Count > 0)
            r.Info(LvlConvertCodes.XffTextures, $"{LvlReportBuilder.Count(opaque.Count, "16-bit texture declares", "16-bit textures declare")} an alpha bit that no pixel sets ({Names(opaque)}); the N-Gage game draws {(opaque.Count == 1 ? "it" : "them")} opaque, "
                + $"so the copy's header says it has no alpha (the pixels are the same), or Red Faction might draw {(opaque.Count == 1 ? "it" : "them")} see-through.", LvlIssueKind.Approximated);
        if (missing.Count > 0)
            r.Warning(LvlConvertCodes.XffTextures, $"{LvlReportBuilder.Count(missing.Count, "texture was", "textures were")} not found beside the level and not copied: {string.Join(", ", missing)}.", LvlIssueKind.Dropped);
        return files.ToImmutable();
    }

    /// <summary>
    /// glTF and OBJ: the loader that gives the PNGs of the level's 16-bit textures whose alpha bit is never set as the
    /// opaque images the N-Gage game draws. <paramref name="next"/> (the caller's own loader) still comes first, and any
    /// other name goes on to the resolver as usual.
    /// </summary>
    private static Func<string, BgraImage?> OpaqueLoader(ImmutableArray<string> names, AssetResolver resolver, Func<string, BgraImage?>? next, LvlReportBuilder r, CancellationToken ct)
    {
        var fixedFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            ct.ThrowIfCancellationRequested();
            string file = FileOf(name);
            if (fixedFiles.ContainsKey(file) || Read(resolver, file) is not { } bytes || !XffTextureFix.NeedsFix(bytes)) continue;
            fixedFiles[file] = XffTextureFix.Opaque(bytes);
        }
        if (fixedFiles.Count > 0)
            r.Info(LvlConvertCodes.XffTextures, $"{LvlReportBuilder.Count(fixedFiles.Count, "16-bit texture declares", "16-bit textures declare")} an alpha bit that no pixel sets ({Names(fixedFiles.Keys)}); the N-Gage game draws {(fixedFiles.Count == 1 ? "it" : "them")} opaque, so {(fixedFiles.Count == 1 ? "its PNG is" : "their PNGs are")} opaque too.", LvlIssueKind.Approximated);
        return name => next?.Invoke(name) ?? (fixedFiles.TryGetValue(FileOf(name), out var bytes) ? ImageDecoder.Decode(bytes, FileOf(name)) : null);
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(Listed)) + (list.Count > Listed ? $" and {(list.Count - Listed).ToString("N0", CultureInfo.InvariantCulture)} more" : "");
    }

    private static string Vec(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})");
}
