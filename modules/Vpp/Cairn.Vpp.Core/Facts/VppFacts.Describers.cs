using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Formats.Audio;

namespace Cairn.Vpp.Facts;

public static partial class VppFacts
{
    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static void DescribeImage(VppFactInput input, VppFactSheetBuilder sheet)
    {
        string ext = Editing.VppNames.ExtensionOf(input.Name);
        byte[] bytes = input.ReadAll();
        ImageInfo info;
        try
        {
            info = ImageProbe.Probe(bytes, input.Name);
        }
        catch (ImageDecodeException ex)
        {
            throw new AssetFormatException(ex.Message, ex);
        }
        sheet.Add("Format", info.ContainerLabel);
        sheet.Add("Dimensions", $"{info.Width} x {info.Height}");
        sheet.Add("Pixel format", EngineFormats.DisplayName(info.Format));
        sheet.Add("Alpha", EngineFormats.HasAlpha(info.Format) ? "yes" : "no");
        if (info.MipLevels is { } mips) sheet.Add("Mip levels", mips.ToString(CultureInfo.CurrentCulture));
        if (info.Container == ImageContainer.Vbm)
        {
            var vbm = VbmCodec.ReadInfo(bytes, input.Name);
            sheet.Add("Frames", vbm.FrameCount);
            if (vbm.FrameCount > 1)
            {
                sheet.Add("Frame rate", $"{vbm.Fps} fps");
                if (vbm.Fps > 0) sheet.Add("Duration", FormatDuration(TimeSpan.FromSeconds((double)vbm.FrameCount / vbm.Fps)));
            }
            if (!vbm.LengthMatchesHeader) sheet.Warn("The VBM holds a different amount of pixel data than its header implies.");
        }
        if (info.Container == ImageContainer.Tga && bytes.Length >= 18)
        {
            int type = bytes[2];
            sheet.Add("TGA type", type switch
            {
                1 => "colour-mapped",
                2 => "true colour",
                3 => "greyscale",
                9 => "colour-mapped, RLE",
                10 => "true colour, RLE",
                11 => "greyscale, RLE",
                _ => $"type {type}",
            });
            sheet.Add("Bits per pixel", bytes[16]);
            sheet.Add("Alpha bits", bytes[17] & 0x0F);
            sheet.Add("Origin", (bytes[17] & 0x20) != 0 ? "top left" : "bottom left");
        }
        if (info.Note is { Length: > 0 } note) sheet.Add("Note", note);
        if (ext is ".tga" or ".vbm" && (!IsPowerOfTwo(info.Width) || !IsPowerOfTwo(info.Height)))
        {
            sheet.Warn("The dimensions are not powers of two, which RF's renderer expects for textures.");
        }
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    private static void DescribePsd(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var b = input.ReadHead(26);
        if (b.Length < 26) throw new AssetFormatException($"'{input.Name}' is too short for a Photoshop header.");
        sheet.Add("Dimensions", $"{BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(18))} x {BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(14))}");
        sheet.Add("Channels", BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(12)));
        sheet.Add("Bits per channel", BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(22)));
        sheet.Warn("Photoshop documents are source art; the game does not load them.");
    }

    private static void DescribeAudio(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var head = input.ReadHead(64);
        string kind = SniffExtension(head) ?? Editing.VppNames.ExtensionOf(input.Name);
        AudioInfo info;
        switch (kind)
        {
            case ".ogg":
                using (var stream = input.Open()) info = AudioProbe.ProbeOgg(stream, input.Name);
                break;
            case ".aif" or ".aiff" or ".aifc":
                info = AudioProbe.ProbeAiff(input.ReadAll(), input.Name);
                break;
            case ".mp3":
                info = AudioProbe.ProbeMp3(input.ReadHead(256 << 10), input.Size, input.Name);
                break;
            default:
                info = AudioProbe.ProbeWav(input.ReadAll(), input.Name);
                break;
        }
        sheet.Add("Container", info.Container);
        sheet.Add("Codec", info.Codec);
        if (info.SampleRate is { } rate) sheet.Add("Sample rate", $"{rate:N0} Hz");
        if (info.Channels is { } ch) sheet.Add("Channels", ch switch { 1 => "1 (mono)", 2 => "2 (stereo)", _ => ch.ToString(CultureInfo.CurrentCulture) });
        if (info.BitsPerSample is { } bits) sheet.Add("Bits per sample", bits);
        if (info.BitrateKbps is { } kbps) sheet.Add("Bitrate", $"{kbps:N0} kbps");
        if (info.Duration is { } d) sheet.Add("Duration", FormatDuration(d));
        if (info.Note is { } note) sheet.Add("Note", note);
        if (kind == ".ogg" && !input.Name.EndsWith(".ogg", StringComparison.Ordinal))
        {
            sheet.Warn("Alpine Faction only plays a sound as Ogg Vorbis when its name ends in lower-case '.ogg'.");
        }
    }

    /// <summary>A PlayStation 2 sound (.vse/.vmu): rate, channels, duration, loop and the header's fields.</summary>
    private static void DescribePs2Sound(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var sound = Ps2Sound.Decode(input.ReadAll(), input.Name);
        sheet.Add("Codec", sound.Codec);
        sheet.Add("Sample rate", $"{sound.SampleRate:N0} Hz");
        sheet.Add("Channels", sound.Channels switch { 1 => "1 (mono)", 2 => "2 (stereo)", _ => sound.Channels.ToString(CultureInfo.CurrentCulture) });
        sheet.Add("Duration", FormatDuration(sound.Duration));
        sheet.Add("Loop", sound.Loop is { } loop ? loop.IsWhole(sound.FrameCount) ? "the whole sound" : $"samples {loop.Start:N0} to {loop.End:N0}" : "none");
        foreach (var (label, value) in sound.Details.Where(d => d.Label is "Header" or "SPU pitch" or "Envelope (ADSR)" or "Flags"))
            sheet.Add(label, value);
        foreach (var problem in sound.Problems.Where(p => p.Severity != SoundSeverity.Info)) sheet.Warn(problem.Message);
    }

    private static void DescribeMesh(VppFactInput input, VppFactSheetBuilder sheet)
    {
        byte[] bytes = input.ReadAll();
        if (LegacyMeshSupport.Identify(bytes, input.Name) is not null)
        {
            DescribeLegacyMesh(input, bytes, sheet);
            return;
        }
        var mesh = V3dProbe.Probe(bytes, input.Name);
        sheet.Add("Kind", mesh.Kind == V3dKind.Character ? "Character mesh" : "Static mesh");
        sheet.Add("Submeshes", mesh.SubmeshCount);
        if (mesh.SubmeshCount > 0)
        {
            sheet.Add("Submesh names", string.Join(", ", mesh.SubmeshNames.Take(8)) + (mesh.SubmeshCount > 8 ? ", ..." : ""));
            sheet.Add("LODs", string.Join(", ", mesh.LodCounts.Take(8)));
        }
        sheet.Add("Bones", mesh.BoneCount);
        if (mesh.Kind == V3dKind.Character) sheet.Add("Collision spheres", mesh.CollisionSphereCount);
        if (!mesh.StructureReadable) sheet.Warn("The mesh's section list could not be walked completely; counts cover only what was read.");
    }

    /// <summary>An exporter (.v3d/.vcm) or PS2 (.rfm/.rfc) mesh: what it holds and what converting it makes.</summary>
    private static void DescribeLegacyMesh(VppFactInput input, byte[] bytes, VppFactSheetBuilder sheet)
    {
        if (!LegacyMeshSupport.TryRead(bytes, input.Name, out var legacy, out var error))
        {
            sheet.Add("Error", error?.Message ?? "The mesh cannot be read.").Warn(error?.Message ?? "The mesh cannot be read.");
            return;
        }
        var d = legacy!.Description;
        string format = LegacyMeshSupport.FormatName(legacy.SourceFormat);
        sheet.Add("Kind", char.ToUpperInvariant(format[0]) + format[1..]);
        sheet.Add("Submeshes", d.Submeshes.Length);
        if (d.Submeshes.Length > 0)
        {
            sheet.Add("Submesh names", string.Join(", ", d.Submeshes.Take(8).Select(s => s.Name.Text)) + (d.Submeshes.Length > 8 ? ", ..." : ""));
            sheet.Add("LODs", string.Join(", ", d.Submeshes.Take(8).Select(s => s.Lods.Length)));
        }
        sheet.Add("Triangles", d.Submeshes.Sum(s => s.Lods.IsDefaultOrEmpty ? 0 : s.Lods[0].Groups.Sum(g => g.Triangles.Length)));
        sheet.Add("Bones", d.Bones.Length);
        if (d.Kind == V3dKind.Character) sheet.Add("Collision spheres", d.CollisionSpheres.Length);
        sheet.Add("Converts to", LegacyMeshSupport.TargetExtension(legacy) + (legacy.Notes.Length > 0 ? $" ({legacy.Notes.Length} notes in the report)" : " (no approximations)"));
    }

    private static void DescribeClip(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var clip = RfaProbe.Probe(input.ReadAll(), input.Name);
        const double TicksPerSecond = 4800;
        sheet.Add("Version", clip.Version);
        sheet.Add("Bones", clip.BoneCount);
        sheet.Add("Frames", $"{clip.Duration / 160.0:0.#} at 30 fps ({N(clip.Duration)} ticks)");
        sheet.Add("Duration", FormatDuration(TimeSpan.FromSeconds(clip.Duration / TicksPerSecond)));
        sheet.Add("Ramp in / out", $"{clip.RampIn / TicksPerSecond:0.###} s / {clip.RampOut / TicksPerSecond:0.###} s");
        if (clip.HasMorph) sheet.Add("Morph", $"{N(clip.MorphVertexCount)} vertices, {N(clip.MorphKeyframeCount)} keyframes");
        string stem = input.Name.Contains('.') ? input.Name[..input.Name.IndexOf('.')] : input.Name;
        if (input.Name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) && !string.Equals(stem + ".rfa", input.Name, StringComparison.OrdinalIgnoreCase))
        {
            sheet.Warn($"The game cuts animation names at the first dot, so it looks for '{stem}.rfa' and never loads this entry.");
        }
    }

    private static void DescribeMvf(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var b = input.ReadHead(8);
        if (b.Length >= 8) sheet.Add("Version", BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)));
        string stem = input.Name.Contains('.') ? input.Name[..input.Name.IndexOf('.')] : input.Name;
        sheet.Add("Kind", "Legacy motion file (an old RFA version)");
        sheet.Warn($"The game does not load .mvf files; a reference to this name loads '{stem}.rfa' instead.");
    }

    private static void DescribeEffect(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var bytes = input.ReadAll();
        var probe = VfxProbe.Read(bytes, input.Name);
        var c = probe.Counts;
        sheet.Add("Version", $"0x{probe.Version:X}");
        sheet.Add("End frame", probe.EndFrame);
        sheet.Add("Objects", $"{c.Meshes} meshes, {c.ParticleSystems} particle systems, {c.Lights} lights, {c.Dummies} dummies, {c.Spacewarps} spacewarps");
        if (c.Materials is { } materials) sheet.Add("Materials", materials);
        sheet.Add("Faces", c.Faces);
        if (probe.IsEngineFatalVersion) sheet.Warn($"Version 0x{probe.Version:X} is one the game refuses to load.");
        try
        {
            var file = VfxReader.Read(bytes, input.Name);
            var problems = VfxLinter.Lint(file, new VfxLintContext(input.Name));
            sheet.Add("Problems", CountProblems(problems.Select(p => (int)p.Severity)));
        }
        catch (AssetFormatException ex)
        {
            sheet.Add("Problems", $"not checked: {ex.Message}");
        }
    }

    private static void DescribeAtx(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var bytes = input.ReadAll();
        string text = DecodeText(bytes, out _);
        var parse = AtxParser.Parse(text, input.Name);
        if (parse.Model is { } model)
        {
            sheet.Add("Frames", model.Frames.Count);
            sheet.Add("Animation mode", AtxSchema.AnimationModeLabel(model.Header.EffectiveAnimationMode));
            sheet.Add("Frame time", $"{model.Header.EffectiveFrameTimeMs} ms");
        }
        else
        {
            sheet.Add("Frames", "unreadable (TOML syntax error)").Warn("The animated texture has TOML syntax errors; the game refuses it.");
        }
        var problems = AtxLinter.Analyze(parse);
        sheet.Add("Problems", CountProblems(problems.Select(p => (int)p.Severity)));
    }

    /// <summary>"2 errors, 1 warning" from severities (0 info, 1 warning, 2 error).</summary>
    private static string CountProblems(IEnumerable<int> severities)
    {
        var list = severities.ToList();
        int errors = list.Count(s => s == 2), warnings = list.Count(s => s == 1), infos = list.Count(s => s == 0);
        if (list.Count == 0) return "none";
        var parts = new List<string>();
        if (errors > 0) parts.Add(errors == 1 ? "1 error" : $"{errors} errors");
        if (warnings > 0) parts.Add(warnings == 1 ? "1 warning" : $"{warnings} warnings");
        if (infos > 0) parts.Add(infos == 1 ? "1 note" : $"{infos} notes");
        return string.Join(", ", parts);
    }

    private static void DescribeText(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var bytes = input.ReadAll();
        string text = DecodeText(bytes, out string encoding);
        sheet.Add("Encoding", encoding);
        int crlf = 0, lf = 0, lines = text.Length == 0 ? 0 : 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines++;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        if (text.EndsWith('\n')) lines--;
        sheet.Add("Lines", lines);
        sheet.Add("Line endings", crlf > 0 && lf > 0 ? "mixed" : crlf > 0 ? "CRLF (Windows)" : lf > 0 ? "LF" : "single line");
        var first = text.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith(';') && !l.StartsWith('#'))
            .Take(5)
            .Select(l => l.Length > 120 ? l[..120] + "..." : l)
            .ToList();
        if (first.Count > 0) sheet.Add("First lines", string.Join(Environment.NewLine, first));
    }

    /// <summary>Decodes text by BOM, else as UTF-8 when valid, else as Windows-1252/Latin-1, and names the guess.</summary>
    internal static string DecodeText(byte[] bytes, out string encoding)
    {
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { encoding = "UTF-8 with BOM"; return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); }
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) { encoding = "UTF-16 LE"; return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) { encoding = "UTF-16 BE"; return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
        if (!bytes.Any(b => b >= 0x80)) { encoding = "ASCII"; return Encoding.ASCII.GetString(bytes); }
        try
        {
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            encoding = "UTF-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
            encoding = "8-bit (Windows-1252 or a localised code page)";
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static void DescribeFont(VppFactInput input, VppFactSheetBuilder sheet)
    {
        // The whole font through the fonts module's reader and checks; the header alone when it cannot be read.
        var (vf, problems) = Cairn.Vf.Formats.VfReader.Inspect(input.ReadAll(), input.Name);
        if (vf is not null)
        {
            sheet.Add("Version", vf.Version);
            sheet.Add("Pixel format", Cairn.Vf.Model.VfFont.FormatName(vf.Format));
            sheet.Add("Glyphs", vf.GlyphCount);
            sheet.Add("First character", vf.FirstCharacter);
            if (vf.GlyphCount > 0) sheet.Add("Characters", $"{Cairn.Vf.Formats.VfReader.Describe(vf.FirstCharacter)} to {Cairn.Vf.Formats.VfReader.Describe(vf.LastCharacter)}");
            sheet.Add("Height", $"{vf.Height} px");
            sheet.Add("Default spacing", $"{vf.DefaultSpacing} px");
            sheet.Add("Widest glyph", $"{vf.MaxGlyphWidth} px");
            sheet.Add("Kerning pairs", vf.Kerning.Length);
            sheet.Add("Pixel data", FormatSize(Cairn.Vf.Rendering.VfAtlas.PixelDataSize(vf)));
            var atlas = Cairn.Vf.Rendering.VfAtlas.Plan(vf);
            sheet.Add("Texture", atlas.Fits ? $"{atlas.Size} x {atlas.Size}" : $"too big for {atlas.Size} x {atlas.Size}");
            foreach (var p in problems.Where(p => p.Severity != Cairn.Vf.Model.VfSeverity.Information)) sheet.Warn(p.Message);
            return;
        }
        foreach (var p in problems) sheet.Warn(p.Message);
        var font = ReadFontHeader(input.ReadHead(48), input.Name);
        sheet.Add("Version", font.Version);
        sheet.Add("Pixel format", font.Format);
        sheet.Add("Glyphs", font.Glyphs);
        sheet.Add("First character", font.FirstCharacter);
        sheet.Add("Height", $"{font.Height} px");
        sheet.Add("Kerning pairs", font.KerningPairs);
        if (font.PixelBytes is { } pixels) sheet.Add("Pixel data", FormatSize(pixels));
    }

    /// <summary>The header fields of a VFNT font.</summary>
    internal sealed record FontHeader(int Version, string Format, int Glyphs, int FirstCharacter, int Height, int KerningPairs, int? PixelBytes);

    /// <summary>Reads a VFNT font's header from its first 48 bytes.</summary>
    /// <exception cref="AssetFormatException">Not a VFNT font.</exception>
    internal static FontHeader ReadFontHeader(byte[] b, string name)
    {
        if (b.Length < 40 || !b.AsSpan(0, 4).SequenceEqual("VFNT"u8)) throw new AssetFormatException($"'{name}' is not a VFNT font.");
        int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
        int version = I(4);
        int at = 8;
        string format = "4-bit monochrome";
        if (version >= 1)
        {
            uint f = (uint)I(at);
            at += 4;
            format = f switch
            {
                0xF => "4-bit monochrome",
                0x0F0F0F0F => "RGBA 4444",
                0xFFFFFFF0 => "8-bit indexed (palette)",
                _ => $"0x{f:X8}",
            };
        }
        int pixelAt = version >= 1 ? at + 20 : at + 28;
        return new FontHeader(version, format, I(at), I(at + 4), I(at + 12), I(at + 16), pixelAt + 4 <= b.Length ? I(pixelAt) : null);
    }

    private static void DescribeGroup(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var b = input.ReadHead(16 + 256);
        int groups = GroupCount(b, input.Name);
        sheet.Add("Kind", "Editor group (prefab), not used by the game");
        sheet.Add("Version", BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)));
        sheet.Add("Groups", groups);
        if (b.Length >= 14)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12));
            if (14 + length <= b.Length) sheet.Add("First group", Encoding.Latin1.GetString(b, 14, length));
        }
    }

    /// <summary>The number of groups an editor group file (.rfg) holds, from its first 12 bytes.</summary>
    /// <exception cref="AssetFormatException">Not an editor group file.</exception>
    internal static int GroupCount(byte[] b, string name)
    {
        if (b.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 0xD43DD00D) throw new AssetFormatException($"'{name}' is not an editor group file.");
        return BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
    }

    /// <summary>
    /// Levels: the summary from <see cref="Formats.Rfl.RflReader"/> (read from the stream, so lightmaps
    /// are skipped), as rows labelled "Group: label" outside the identity group, with referenced files
    /// checked against this packfile and the game data. Missing files and reader notes become warnings.
    /// </summary>
    private static void DescribeLevel(VppFactInput input, VppFactSheetBuilder sheet)
    {
        Formats.Rfl.RflSummary summary;
        using (var stream = input.Open()) summary = Formats.Rfl.RflReader.ReadSummary(stream, input.Name);
        var missing = new List<string>();
        foreach (var row in Formats.Rfl.RflFacts.Rows(summary, LevelPresence(input.Context)))
        {
            sheet.Add(row.Section == "Level" ? row.Label : $"{row.Section}: {row.Label}", row.Value);
            if (!row.Flagged) continue;
            if (row.Section == "Notes") sheet.Warn(row.Value);
            else if (row.Section is "References" or "Preloads" && row.Label != "Missing") missing.Add(row.Label);
        }
        if (missing.Count > 0)
        {
            sheet.Warn((missing.Count == 1 ? "1 file referenced by this level is" : $"{missing.Count} files referenced by this level are")
                + " not in this packfile or the game data: "
                + string.Join(", ", missing.Take(10)) + (missing.Count > 10 ? ", ..." : "."));
        }
    }

    /// <summary>Where a name a level references is found: this packfile, the game data, or nowhere; null when nothing can be checked.</summary>
    internal static Func<string, Formats.Rfl.AssetPresence>? LevelPresence(VppFactsContext context)
    {
        var resolver = context.Resolver;
        bool canResolve = resolver is not null
            && (resolver.Options.GameDirectory is not null || resolver.Options.SearchFolders.Count > 0);
        if (context.Package is null && !canResolve) return null;
        // The engine compares names case-insensitively for A-Z only (vpp-facts 1.3), as VppNames.Comparer does.
        var names = context.Package?.Items.Select(i => i.Name).ToHashSet(Editing.VppNames.Comparer) ?? new HashSet<string>(Editing.VppNames.Comparer);
        // A level names many files twice (placed and preloaded): each is looked up in the game data once.
        var known = new Dictionary<string, Formats.Rfl.AssetPresence>(Editing.VppNames.Comparer);
        return reference =>
        {
            var candidates = EngineCandidates(reference);
            if (candidates.Count == 0) return Formats.Rfl.AssetPresence.Unknown;
            if (candidates.Any(names.Contains)) return Formats.Rfl.AssetPresence.SamePackfile;
            if (!canResolve) return Formats.Rfl.AssetPresence.Unknown;
            // Resolve applies the texture supersede chain itself, so only the names outside it are asked for.
            if (known.TryGetValue(reference, out var cached)) return cached;
            // Likely hits first (a miss walks every archive): the name as written, then the stock texture types.
            string asWritten = Path.GetFileName(reference.Trim());
            var lookups = candidates.Where(c => !Cairn.Assets.AssetResolver.SupersedeProbeExtensions.Any(e => c.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => Editing.VppNames.Comparer.Equals(c, asWritten) ? 0
                    : c.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase) || c.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
            var found = lookups.Any(c => resolver!.Resolve(c) is not null) ? Formats.Rfl.AssetPresence.GameData : Formats.Rfl.AssetPresence.Missing;
            known[reference] = found;
            return found;
        };
    }

    /// <summary>
    /// The entry names under which the game would find a file a level references, in the engine's order: the bare
    /// file name (requests drop any folder part); an animation (<c>.rfa</c>, or a legacy <c>.mvf</c>) is cut at the
    /// first dot and loaded as <c>&lt;stem&gt;.rfa</c>; a texture is found under any texture extension, in the
    /// bitmap manager's order (<see cref="Cairn.Assets.AssetResolver.TextureExtensions"/>); the exporter's mesh extensions stand for the saved
    /// ones (<c>.vcm</c> = <c>.v3c</c>, <c>.v3d</c> = <c>.v3m</c> or <c>.v3c</c>). Empty when nothing is left of the name.
    /// </summary>
    internal static IReadOnlyList<string> EngineCandidates(string reference)
    {
        string bare = reference.Trim();
        int slash = bare.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) bare = bare[(slash + 1)..];
        if (bare.Length == 0) return [];
        string ext = Path.GetExtension(bare);
        if (ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mvf", StringComparison.OrdinalIgnoreCase))
        {
            int dot = bare.IndexOf('.');
            return dot > 0 ? [bare[..dot] + ".rfa"] : [];
        }
        // Exporter extensions the tools and RED's preload lists still use: .vcm is a character mesh, .v3d either kind.
        if (ext.Equals(".vcm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".v3d", StringComparison.OrdinalIgnoreCase))
            return Formats.Tbl.TblFileName.Normalize(bare);
        if (Cairn.Assets.AssetResolver.IsTextureName(bare))
        {
            // The bitmap manager strips the extension and tries every texture extension in order (a level's
            // "water.tga" is found as an animated "water.vbm").
            string stem = Cairn.Assets.AssetResolver.StripTextureExtension(bare);
            return Cairn.Assets.AssetResolver.TextureExtensions.Select(e => stem + e).ToList();
        }
        return [bare];
    }

    /// <summary>The most textures a PEG's details list one by one.</summary>
    private const int MaxListedTextures = 300;

    /// <summary>A PEG texture pack (PlayStation 2): its version and counts, then one row per texture.</summary>
    private static void DescribeTexturePack(VppFactInput input, VppFactSheetBuilder sheet)
    {
        var pack = PegCodec.Read(input.ReadAll(), input.Name);
        sheet.Add("Format", $"PEG texture pack, version {pack.Version}");
        sheet.Add("Textures", pack.Textures.Count);
        int animated = pack.Textures.Count(t => t.IsAnimated && !t.IsMpeg2);
        if (animated > 0) sheet.Add("Animated", animated);
        if (pack.Mpeg2Count > 0) sheet.Add("MPEG-2 backgrounds", pack.Mpeg2Count);
        int broken = pack.Textures.Count(t => t.Problem is not null);
        if (broken > 0) sheet.Warn($"{N(broken)} texture(s) cannot be decoded.");
        sheet.Add("PC game", "Not loaded: Convert to .tga... on the selected .peg entry (or opening the .peg in Cairn) turns the textures into .tga files");
        foreach (var t in pack.Textures.Take(MaxListedTextures))
        {
            string value = t.Describe() + (t.Problem is { } p ? $"; cannot be decoded: {p}" : t.IsMpeg2 ? "; no alpha" : string.Empty);
            sheet.Add("Textures: " + (t.Name.Length > 0 ? t.Name : $"#{t.Index}"), value);
        }
        if (pack.Textures.Count > MaxListedTextures) sheet.Add("Textures: ...", $"and {N(pack.Textures.Count - MaxListedTextures)} more");
    }
}
