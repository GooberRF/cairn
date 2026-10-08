using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Cairn.Atx.Schema;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Vpp.Editing;

namespace Cairn.Vpp.Ps2;

/// <summary>One file a PEG conversion produced: a 32-bit .tga, a numbered animation frame, or an animation's .atx.</summary>
/// <param name="Name">The packfile entry name.</param>
/// <param name="Bytes">The file's contents.</param>
/// <param name="Texture">The PEG texture it came from.</param>
/// <param name="Note">Where it came from, for the Info column ("from PS2 8-bit indexed, 3 mips").</param>
public sealed record PegOutputFile(string Name, byte[] Bytes, PegTexture Texture, string Note);

/// <summary>Whether a skipped texture is a copy of one another PEG file of the same packfile gave.</summary>
public enum PegCopy
{
    /// <summary>Not a copy.</summary>
    None,
    /// <summary>The same pixels as the copy kept.</summary>
    Identical,
    /// <summary>Different pixels or size: the copy with the most pixels was kept.</summary>
    Different,
}

/// <summary>A texture a PEG conversion left out, and why.</summary>
/// <param name="Name">The texture's name in the PEG.</param>
/// <param name="Reason">Why ("MPEG-2 compressed background — not converted").</param>
/// <param name="IsMpeg2">True for an MPEG-2 compressed background.</param>
/// <param name="Copy">Whether it is a copy of a texture another PEG file of the packfile gave.</param>
public sealed record PegSkippedTexture(string Name, string Reason, bool IsMpeg2, PegCopy Copy = PegCopy.None);

/// <summary>What converting one PEG texture pack gave.</summary>
/// <param name="SourceName">The PEG's file or entry name.</param>
/// <param name="Pack">The PEG's directory, or null when it could not be read at all (see <paramref name="Error"/>).</param>
/// <param name="Files">The files written, in directory order (an animation's frames, then its .atx).</param>
/// <param name="Skipped">The textures left out.</param>
/// <param name="Error">Why the PEG could not be read ("unsupported PEG variant ..."), or null.</param>
/// <param name="BlackKey">The "black as transparent" key the MPEG-2 backgrounds were decoded with, or null (opaque).</param>
public sealed record PegConversionResult(
    string SourceName,
    PegFile? Pack,
    IReadOnlyList<PegOutputFile> Files,
    IReadOnlyList<PegSkippedTexture> Skipped,
    string? Error,
    PegBlackKey? BlackKey = null)
{
    /// <summary>Textures left out because they were not ticked.</summary>
    public int NotChosen => Skipped.Count(s => s.Reason == PegConverter.NotChosenReason);

    /// <summary>Textures converted (an animation counts once).</summary>
    public int ConvertedTextures => Files.Select(f => f.Texture.Index).Distinct().Count();

    /// <summary>Animations converted to numbered frames plus an .atx.</summary>
    public int AnimatedTextures => Files.Where(f => f.Texture.IsAnimated).Select(f => f.Texture.Index).Distinct().Count();

    /// <summary>The MPEG-2 compressed backgrounds that were left out (decoding switched off, or a damaged stream).</summary>
    public IEnumerable<PegSkippedTexture> Mpeg2 => Skipped.Where(s => s.IsMpeg2);

    /// <summary>MPEG-2 compressed backgrounds converted to .tga.</summary>
    public int Mpeg2Textures => Files.Where(f => f.Texture.IsMpeg2 && !IsAtx(f.Name)).Select(f => f.Texture.Index).Distinct().Count();

    /// <summary>The .atx files written for numbered MPEG-2 frame sequences (see <see cref="PegConverter.FindSequences"/>).</summary>
    public IEnumerable<PegOutputFile> Mpeg2Sequences => Files.Where(f => f.Texture.IsMpeg2 && IsAtx(f.Name));

    private static bool IsAtx(string name) => name.EndsWith(".atx", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turns a PEG texture pack (PlayStation 2) into files the PC game loads. Mapping, chosen to need no edits to levels,
/// meshes or tables that name the textures:
/// <list type="bullet">
/// <item>A single-frame texture becomes <c>stem.tga</c>: 32-bit, mip level 0. A <c>.vbm</c> name becomes <c>.tga</c>
/// too; the game looks for a <c>.vbm</c> and then a <c>.tga</c> of the same name whichever was asked for.</item>
/// <item>An animated texture (the PS2 files name most of them <c>.vbm</c>, as the PC game does) becomes its frames,
/// <c>stem_00.tga</c>, <c>stem_01.tga</c>, ... (32-bit, numbered from 00, the stem shortened when needed so every frame
/// name fits the game's 31 characters) plus <c>stem.atx</c>, an Alpine Faction animated texture (1.4.0 or later) that
/// lists them. The game looks for an <c>.atx</c> before any other texture of that name, so it takes the animation's
/// place. PEG files store no frame rate: <see cref="DefaultFps"/> is used unless the caller knows the PC game's own
/// rate for that name.</item>
/// <item>An MPEG-2 compressed still (a full-screen menu background) is decoded and becomes <c>stem.tga</c>, 24-bit
/// (there is no alpha), or 32-bit with black as transparent when a <see cref="PegBlackKey"/> applies. Numbered stills that are frames of one animation (<see cref="FindSequences"/>, such as the main
/// menu's <c>plan-0001</c> to <c>plan-0150</c>) also get an <c>.atx</c> listing them, at <see cref="Mpeg2SequenceFps"/>.
/// When decoding is switched off they are listed as skipped.</item>
/// </list>
/// </summary>
public static class PegConverter
{
    /// <summary>The frame rate of a converted animation: the most common rate of the PC game's animated textures.</summary>
    public const int DefaultFps = 15;

    /// <summary>
    /// The frame rate of the .atx written for a numbered MPEG-2 frame sequence: the rate the streams declare. The PS2
    /// version's real playback rate is not known.
    /// </summary>
    public const int Mpeg2SequenceFps = 30;

    /// <summary>The fewest consecutive numbered MPEG-2 stills <see cref="FindSequences"/> treats as an animation.</summary>
    public const int MinSequenceFrames = 8;

    /// <summary>The fewest digits of a sequence frame's number (frame counters are zero-padded, as in plan-0001).</summary>
    public const int MinSequenceDigits = 3;

    /// <summary>The reason given for an MPEG-2 still when decoding them is switched off.</summary>
    public const string Mpeg2Reason = "MPEG-2 compressed background — not converted (decoding is switched off in Settings > Packfiles)";

    /// <summary>The start of the reason given for an MPEG-2 still whose stream cannot be decoded (the error follows).</summary>
    public const string Mpeg2FailedReason = "MPEG-2 background not decoded: ";

    /// <summary>The start of the reason given for a texture whose name is taken (the name follows).</summary>
    public const string NameTakenReason = "another texture already uses the name ";

    /// <summary>The reason given for a texture left out because it was not ticked.</summary>
    public const string NotChosenReason = "not ticked, so not converted";

    /// <summary>The first Alpine Faction version that reads <c>.atx</c> animated textures.</summary>
    public const string AtxAlpineSince = "1.4.0";

    private static readonly ConditionalWeakTable<byte[], string> Notes = new();

    /// <summary>
    /// Where converted bytes came from ("from PS2 8-bit indexed, 3 mips"), for the Info column; null for bytes this
    /// converter did not produce. Keyed by the array itself, so it follows the data through renames and undo.
    /// </summary>
    public static string? NoteFor(byte[] bytes) => bytes is not null && Notes.TryGetValue(bytes, out var note) ? note : null;

    /// <summary>Converts a whole PEG. Never throws for bad data: an unreadable PEG gives <see cref="PegConversionResult.Error"/>.</summary>
    /// <param name="bytes">The PEG file.</param>
    /// <param name="pegName">Its name, for messages and notes.</param>
    /// <param name="isTaken">Names already used elsewhere (the packfile's other entries); a texture whose name is taken is skipped.</param>
    /// <param name="fpsFor">The PC game's frame rate for an animated texture name (such as "boom01.vbm"), or null for <see cref="DefaultFps"/>.</param>
    /// <param name="cancellationToken">Stops between textures.</param>
    /// <param name="isTakenFor">Like <paramref name="isTaken"/>, also given the texture the name is wanted for (a
    /// packfile's conversion lets copies of one texture in several PEGs produce the same names and sorts them out after).</param>
    /// <param name="decodeMpeg2">Decode MPEG-2 compressed backgrounds; false lists them as skipped (<see cref="Mpeg2Reason"/>).</param>
    /// <param name="blackKey">Black as transparent for the MPEG-2 backgrounds (32-bit .tga), or null for opaque 24-bit
    /// ones (a background whose PEG entry asks for the PlayStation 2's own key still gets it: <see cref="PegBlackKey.For"/>).</param>
    /// <param name="textures">The indexes of the textures to convert; null for all. The others are listed as skipped (<see cref="NotChosenReason"/>).</param>
    public static PegConversionResult Convert(byte[] bytes, string pegName, Func<string, bool>? isTaken = null,
        Func<string, int?>? fpsFor = null, CancellationToken cancellationToken = default, Func<string, PegTexture, bool>? isTakenFor = null,
        bool decodeMpeg2 = true, PegBlackKey? blackKey = null, IReadOnlySet<int>? textures = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        pegName ??= "texture pack";
        PegFile pack;
        try { pack = PegCodec.Read(bytes, pegName); }
        catch (AssetFormatException ex) { return new PegConversionResult(pegName, null, [], [], ex.Message, blackKey); }

        var files = new List<PegOutputFile>();
        var skipped = new List<PegSkippedTexture>();
        var taken = new HashSet<string>(VppNames.Comparer);
        PegTexture? current = null;
        bool Taken(string n) => taken.Contains(n) || (isTaken?.Invoke(n) ?? false) || (current is not null && (isTakenFor?.Invoke(n, current) ?? false));
        string version = pack.Version == 6 ? "PS2" : $"PS2 (PEG v{pack.Version.ToString(CultureInfo.InvariantCulture)})";
        // the frames each numbered MPEG-2 sequence actually gave, by the sequence's first texture
        var sequences = decodeMpeg2 ? FindSequences(pack) : [];
        var sequenceOf = new Dictionary<int, List<PegTexture>>();
        foreach (var sequence in sequences) foreach (var t in sequence) sequenceOf[t.Index] = sequence;
        var sequenceFrames = new Dictionary<List<PegTexture>, List<string>>(ReferenceEqualityComparer.Instance);

        foreach (var texture in pack.Textures)
        {
            current = texture;
            cancellationToken.ThrowIfCancellationRequested();
            if (textures is not null && !textures.Contains(texture.Index)) { skipped.Add(new(texture.Name, NotChosenReason, texture.IsMpeg2)); continue; }
            if (texture.IsMpeg2 && !decodeMpeg2) { skipped.Add(new(texture.Name, Mpeg2Reason, true)); continue; }
            if (texture.Problem is { } problem) { skipped.Add(new(texture.Name, (texture.IsMpeg2 ? Mpeg2FailedReason : "cannot be decoded: ") + problem, texture.IsMpeg2)); continue; }
            string stem = StemOf(texture.Name, texture.Index);
            string note = $"from {version} {texture.FormatLabel}" + (texture.MipCount > 1 ? string.Create(CultureInfo.InvariantCulture, $", {texture.MipCount} mips (level 0 kept)") : string.Empty);
            try
            {
                if (texture.IsMpeg2)
                {
                    string name = stem + ".tga";
                    if (Taken(name)) { skipped.Add(new(texture.Name, NameTakenReason + name, true)); continue; }
                    int tiles = PegCodec.Mpeg2Tiles(PegCodec.RawData(bytes, texture), texture.Name).Count;
                    var key = PegBlackKey.For(texture, blackKey);
                    var tga = TgaWriter.Write(PegCodec.DecodeMpeg2(bytes, texture, key), includeAlpha: key is not null);
                    files.Add(Output(name, tga, texture, string.Create(CultureInfo.InvariantCulture, $"from {version} MPEG-2 ({tiles} tile{(tiles == 1 ? "" : "s")})")
                        + (key is null ? string.Empty : ", " + key.Describe())));
                    taken.Add(name);
                    if (sequenceOf.TryGetValue(texture.Index, out var sequence))
                    {
                        if (!sequenceFrames.TryGetValue(sequence, out var written)) sequenceFrames[sequence] = written = [];
                        written.Add(name);
                    }
                    continue;
                }
                if (!texture.IsAnimated)
                {
                    string name = stem + ".tga";
                    if (Taken(name)) { skipped.Add(new(texture.Name, NameTakenReason + name, false)); continue; }
                    var tga = TgaWriter.Write(PegCodec.DecodeFrame(bytes, texture), includeAlpha: true);
                    files.Add(Output(name, tga, texture, note));
                    taken.Add(name);
                    continue;
                }

                // The .atx must keep the texture's own name (it takes its place); the frames only need names nothing
                // else has, so when shortening makes them clash with another animation's, an alternative is used.
                string atxName = stem + ".atx";
                if (Taken(atxName)) { skipped.Add(new(texture.Name, NameTakenReason + atxName, false)); continue; }
                IReadOnlyList<string>? frameNames = null;
                for (int variant = 0; variant <= MaxFrameNameVariants && frameNames is null; variant++)
                {
                    var candidate = FrameNames(stem, texture.FrameCount, variant);
                    if (!candidate.Any(Taken)) frameNames = candidate;
                }
                if (frameNames is null)
                {
                    skipped.Add(new(texture.Name, NameTakenReason + FrameNames(stem, texture.FrameCount)[0], false));
                    continue;
                }
                var frames = PegCodec.DecodeFrames(bytes, texture);
                string frameNote = note + string.Create(CultureInfo.InvariantCulture, $", frame {{0}} of {texture.FrameCount}");
                for (int i = 0; i < frames.Count; i++)
                    files.Add(Output(frameNames[i], TgaWriter.Write(frames[i], includeAlpha: true), texture, string.Format(CultureInfo.InvariantCulture, frameNote, i + 1)));
                int fps = fpsFor?.Invoke(texture.Name) is int known and > 0 ? known : DefaultFps;
                byte[] atx = Encoding.UTF8.GetBytes(AtxText(texture, pegName, frameNames, fps));
                files.Add(Output(atxName, atx, texture, string.Create(CultureInfo.InvariantCulture, $"from {version} animation, {texture.FrameCount} frames at {fps} fps")));
                taken.UnionWith(frameNames);
                taken.Add(atxName);
            }
            catch (ImageDecodeException ex)
            {
                skipped.Add(new(texture.Name, (texture.IsMpeg2 ? Mpeg2FailedReason : "cannot be decoded: ") + ex.Message, texture.IsMpeg2));
            }
        }

        // each numbered MPEG-2 sequence: an .atx listing the frames written (the frames stay as .tga files too)
        foreach (var sequence in sequences)
        {
            if (!sequenceFrames.TryGetValue(sequence, out var frames) || frames.Count < 2) continue;
            current = sequence[0];
            var candidates = SequenceAtxStems(sequences.Count, pegName, sequence[0]);
            string? atxName = candidates.Select(s => s + ".atx")
                .Concat(Enumerable.Range(2, MaxFrameNameVariants).Select(n => candidates[0][..Math.Min(candidates[0].Length, AtxSchema.MaxBitmapNameLength - 6)] + n.ToString(CultureInfo.InvariantCulture) + ".atx"))
                .FirstOrDefault(n => !Taken(n));
            if (atxName is null) { skipped.Add(new(sequence[0].Name, NameTakenReason + candidates[0] + ".atx", true)); continue; }
            var t = sequence[0];
            var comments = new[]
            {
                string.Create(CultureInfo.InvariantCulture, $"# Converted by Cairn from the numbered MPEG-2 backgrounds {frames[0]} to {frames[^1]} in {pegName} (PlayStation 2): {frames.Count} frames, {t.Width} x {t.Height}."),
                "# The PlayStation 2 version shows these frames in turn (it builds their names from a frame counter); the frames stay as .tga files too.",
                string.Create(CultureInfo.InvariantCulture, $"# Its playback rate is not stored: {Mpeg2SequenceFps} fps is the rate the streams declare, unverified. Change frame_time if it plays too fast or too slow."),
            };
            byte[] atx = Encoding.UTF8.GetBytes(AtxText(comments, frames, Mpeg2SequenceFps));
            files.Add(Output(atxName, atx, t, string.Create(CultureInfo.InvariantCulture, $"from {version} MPEG-2 frames, {frames.Count} frames at {Mpeg2SequenceFps} fps (rate unverified)")));
            taken.Add(atxName);
        }
        return new PegConversionResult(pegName, pack, files, skipped, null, blackKey);
    }

    /// <summary>
    /// The stems an MPEG-2 sequence's .atx may take, best first: the PEG's name when it holds one sequence (as
    /// <c>interface-bg-mm.atx</c>), else the frames' common name (<c>plan.atx</c>); cut to fit a bitmap name.
    /// </summary>
    private static List<string> SequenceAtxStems(int sequenceCount, string pegName, PegTexture first)
    {
        string pegStem = StemOf(Path.GetFileName(pegName), 0);
        string common = SequenceStem(first.Name);
        return (sequenceCount == 1 ? new[] { pegStem, common } : [common, pegStem])
            .Where(s => s.Length > 0).Select(s => s.Length > AtxSchema.MaxBitmapNameLength - 4 ? s[..(AtxSchema.MaxBitmapNameLength - 4)] : s).ToList();
    }

    /// <summary>
    /// The .atx each texture of <paramref name="pack"/> leads to (before any name clash is sorted out), by texture index:
    /// an animation's own <c>stem.atx</c>, and for every frame of a numbered MPEG-2 sequence the sequence's .atx. Textures
    /// that give no .atx are absent.
    /// </summary>
    public static IReadOnlyDictionary<int, string> AtxNames(PegFile pack, string pegName)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var names = new Dictionary<int, string>();
        foreach (var t in pack.Textures) if (t.IsAnimated && !t.IsMpeg2) names[t.Index] = OutputName(t);
        var sequences = FindSequences(pack);
        foreach (var sequence in sequences)
        {
            var stems = SequenceAtxStems(sequences.Count, pegName ?? string.Empty, sequence[0]);
            if (stems.Count == 0) continue;
            foreach (var t in sequence) names[t.Index] = stems[0] + ".atx";
        }
        return names;
    }

    private static readonly System.Text.RegularExpressions.Regex NumberedName =
        new(@"^(?<stem>.*?)(?<number>\d+)(\.[^.]*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The frames of numbered MPEG-2 animations in a PEG, each in order: at least <see cref="MinSequenceFrames"/>
    /// single-picture MPEG-2 stills of one size whose names differ only in a zero-padded number of at least
    /// <see cref="MinSequenceDigits"/> digits (all of the same width), counting up by one. That is the PS2 main menu's
    /// spinning planet (<c>plan-0001.tga</c> to <c>plan-0150.tga</c>); numbered pages such as <c>extras01</c> to
    /// <c>extras26</c> (two digits) stay plain stills.
    /// </summary>
    public static IReadOnlyList<List<PegTexture>> FindSequences(PegFile pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var groups = new Dictionary<(string Stem, string Extension, int Digits, int W, int H), List<(long Number, PegTexture Texture)>>();
        foreach (var t in pack.Textures)
        {
            if (!t.IsMpeg2 || t.Problem is not null || t.IsAnimated) continue;
            var m = NumberedName.Match(t.Name);
            string digits = m.Groups["number"].Value;
            if (!m.Success || digits.Length < MinSequenceDigits || digits.Length > 9) continue;
            var key = (m.Groups["stem"].Value.ToLowerInvariant(), Path.GetExtension(t.Name).ToLowerInvariant(), digits.Length, t.Width, t.Height);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add((long.Parse(digits, CultureInfo.InvariantCulture), t));
        }
        var sequences = new List<List<PegTexture>>();
        foreach (var list in groups.Values)
        {
            var sorted = list.OrderBy(e => e.Number).ToList();
            int start = 0;
            for (int i = 1; i <= sorted.Count; i++)
            {
                if (i < sorted.Count && sorted[i].Number == sorted[i - 1].Number + 1) continue;
                if (i - start >= MinSequenceFrames) sequences.Add([.. sorted.Skip(start).Take(i - start).Select(e => e.Texture)]);
                start = i;
            }
        }
        return [.. sequences.OrderBy(s => s[0].Index)];
    }

    /// <summary>"plan-0001.tga" → "plan" (the name before the number, separators trimmed).</summary>
    private static string SequenceStem(string frameName)
    {
        var m = NumberedName.Match(VppNames.Normalize(frameName));
        string stem = m.Success ? m.Groups["stem"].Value.TrimEnd('-', '_', ' ', '.') : string.Empty;
        return stem.Length == 0 ? string.Empty : StemOf(stem + ".tga", 0);
    }

    /// <summary>The banner above a texture pack opened as a packfile: what saving does, and what is left out.</summary>
    public static string BannerFor(PegConversionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder("PlayStation 2 texture pack. Saving writes a PC packfile (.vpp) with each texture as a .tga.");
        if (result.AnimatedTextures > 0)
            text.Append(inv, $" {(result.AnimatedTextures == 1 ? "The animated texture becomes" : $"The {result.AnimatedTextures:N0} animated textures become")} numbered .tga frames plus an .atx (Alpine Faction {AtxAlpineSince} or later).");
        int decoded = result.Mpeg2Textures;
        if (decoded > 0)
        {
            string format = result.BlackKey is { } key ? $"32-bit .tga with {key.Describe()}." : "24-bit .tga (no alpha).";
            text.Append(inv, $" {(decoded == 1 ? "The MPEG-2 compressed background becomes" : $"The {decoded:N0} MPEG-2 compressed backgrounds become")} {format}");
        }
        foreach (var atx in result.Mpeg2Sequences)
            text.Append(inv, $" Its numbered frames are also listed in {atx.Name}, an animation looping at {Mpeg2SequenceFps} fps (the PS2 rate is not known).");
        var off = result.Skipped.Where(s => s.Reason == Mpeg2Reason).Select(s => s.Name).ToList();
        if (off.Count > 0)
            text.Append(inv, $" Not converted, because decoding them is switched off in Settings > Packfiles: {(off.Count == 1 ? "1 MPEG-2 compressed background" : $"{off.Count:N0} MPEG-2 compressed backgrounds")} ({string.Join(", ", off.Take(4))}{(off.Count > 4 ? ", ..." : "")}).");
        var failed = result.Skipped.Where(s => s.Reason.StartsWith(Mpeg2FailedReason, StringComparison.Ordinal)).ToList();
        foreach (var f in failed.Take(3)) text.Append(inv, $" {f.Name} could not be decoded: {f.Reason[Mpeg2FailedReason.Length..]}");
        if (failed.Count > 3) text.Append(inv, $" {failed.Count - 3:N0} more MPEG-2 backgrounds could not be decoded.");
        var others = result.Skipped.Where(s => s.Reason != Mpeg2Reason && s.Reason != NotChosenReason && !s.Reason.StartsWith(Mpeg2FailedReason, StringComparison.Ordinal)).ToList();
        if (others.Count > 0) text.Append(inv, $" {others.Count:N0} other texture{(others.Count == 1 ? " is" : "s are")} left out ({string.Join(", ", others.Take(3).Select(s => s.Name))}{(others.Count > 3 ? ", ..." : "")}).");
        return text.ToString();
    }

    private static PegOutputFile Output(string name, byte[] bytes, PegTexture texture, string note)
    {
        Notes.AddOrUpdate(bytes, note);
        return new PegOutputFile(name, bytes, texture, note);
    }

    /// <summary>
    /// The name a texture takes in the packfile: <c>stem.tga</c>, or <c>stem.atx</c> for an animation (its frames are
    /// named after it). Two textures of one name in different PEG files are copies of one texture.
    /// </summary>
    public static string OutputName(PegTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return StemOf(texture.Name, texture.Index) + (texture.IsAnimated ? ".atx" : ".tga");
    }

    /// <summary>The texture's name without its extension, made storable ("texture7" when nothing is left).</summary>
    internal static string StemOf(string textureName, int index)
    {
        string name = VppNames.Normalize(textureName ?? string.Empty);
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        var clean = new StringBuilder(stem.Length);
        foreach (char c in stem) clean.Append(c < ' ' || c == '\u007F' || c > 'ÿ' || c is '/' or '\\' or ':' ? '_' : c);
        // room for the longest extension added (".tga", ".atx") within a packfile name
        string result = clean.ToString().Trim();
        if (result.Length > VppNames.MaxNameBytes - 4) result = result[..(VppNames.MaxNameBytes - 4)];
        return result.Length == 0 ? "texture" + index.ToString(CultureInfo.InvariantCulture) : result;
    }

    /// <summary>
    /// "stem_00.tga", "stem_01.tga", ...: numbered from 00 with at least two digits, the stem shortened so that every
    /// name fits the game's bitmap names (<see cref="AtxSchema.MaxBitmapNameLength"/> characters).
    /// </summary>
    /// <param name="stem">The texture's name without its extension.</param>
    /// <param name="frameCount">The number of frames.</param>
    /// <param name="variant">0 for the plain names; 1, 2, ... for alternatives when those are taken: the stem is cut
    /// further and the number added ("explosion_big_fire_anim1_00.tga"), so two long names that shorten alike stay apart.</param>
    public static IReadOnlyList<string> FrameNames(string stem, int frameCount, int variant = 0)
    {
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentOutOfRangeException.ThrowIfNegative(variant);
        int digits = Math.Max(2, Math.Max(1, frameCount - 1).ToString(CultureInfo.InvariantCulture).Length);
        string suffix = variant > 0 ? variant.ToString(CultureInfo.InvariantCulture) : string.Empty;
        int room = AtxSchema.MaxBitmapNameLength - 1 - digits - ".tga".Length - suffix.Length;
        string frameStem = stem.Length > room ? stem[..Math.Max(1, room)].TrimEnd('_', '-', ' ', '.') : stem;
        if (frameStem.Length == 0) frameStem = "frame";
        frameStem += suffix;
        return [.. Enumerable.Range(0, frameCount).Select(i => frameStem + "_" + i.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0') + ".tga")];
    }

    /// <summary>The most alternative frame names <see cref="Convert"/> tries before it gives up on an animation.</summary>
    public const int MaxFrameNameVariants = 99;

    /// <summary>The .atx text for a converted animation (CRLF; looping at <paramref name="fps"/>).</summary>
    internal static string AtxText(PegTexture texture, string pegName, IReadOnlyList<string> frameNames, int fps) => AtxText(
    [
        $"# Converted by Cairn from {texture.Name} in {pegName} (PlayStation 2): {frameNames.Count.ToString(CultureInfo.InvariantCulture)} frames, {texture.Width.ToString(CultureInfo.InvariantCulture)} x {texture.Height.ToString(CultureInfo.InvariantCulture)}.",
        "# The game looks for an .atx before any other texture of the same name, so this file takes that texture's place.",
        "# The PlayStation 2 file stores no frame rate: change frame_time if the animation plays too fast or too slow.",
    ], frameNames, fps);

    /// <summary>The .atx text for frames played in a loop at <paramref name="fps"/> (CRLF), after comment lines.</summary>
    internal static string AtxText(IReadOnlyList<string> comments, IReadOnlyList<string> frameNames, int fps)
    {
        int frameTime = Math.Max(AtxSchema.MinFrameTimeMs, (int)Math.Round(1000.0 / fps, MidpointRounding.AwayFromZero));
        var lines = new List<string>(comments)
        {
            string.Empty,
            "[" + AtxSchema.HeaderTable + "]",
            $"{AtxSchema.KeyFrameTime} = {frameTime.ToString(CultureInfo.InvariantCulture)}",
            $"{AtxSchema.KeyAnimationMode} = {((int)AtxAnimationMode.Loop).ToString(CultureInfo.InvariantCulture)}",
        };
        foreach (string name in frameNames)
        {
            lines.Add(string.Empty);
            lines.Add("[[" + AtxSchema.FrameArray + "]]");
            lines.Add($"{AtxSchema.KeyFile} = \"{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
        }
        lines.Add(string.Empty);
        return string.Join("\r\n", lines);
    }
}
