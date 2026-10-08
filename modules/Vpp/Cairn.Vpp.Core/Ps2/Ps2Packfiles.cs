using System.Globalization;
using Cairn.Formats.Imaging;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Ps2;

/// <summary>One .peg entry of a packfile and what converting it gave.</summary>
/// <param name="Index">The entry's position in the packfile (two entries may share a name).</param>
/// <param name="EntryName">The .peg entry's name.</param>
/// <param name="Source">The entry's data source when it was read (the conversion applies only while it is unchanged).</param>
/// <param name="Result">The conversion.</param>
/// <param name="Replaces">Entries already in the packfile that a converted texture of the same name replaces (it has
/// more pixels).</param>
public sealed record PegEntryConversion(int Index, string EntryName, VppSource Source, PegConversionResult Result, IReadOnlyList<PegReplacement>? Replaces = null);

/// <summary>One .peg entry to convert, and which of its textures.</summary>
/// <param name="Index">The entry's position in the packfile.</param>
/// <param name="Textures">The indexes of the textures to convert; null for all.</param>
public sealed record PegEntryChoice(int Index, IReadOnlySet<int>? Textures = null);

/// <summary>An entry already in the packfile that a larger converted texture of the same name replaces.</summary>
/// <param name="Name">The entry's name.</param>
/// <param name="Source">Its data when compared (replaced only while unchanged).</param>
/// <param name="AlsoRemove">For an animation's .atx: its frames, which nothing else lists.</param>
/// <param name="Note">"envirohand.tga: replaced the 64×64 already in the packfile by 128×128 from b.peg".</param>
public sealed record PegReplacement(string Name, VppSource Source, IReadOnlyList<string> AlsoRemove, string Note);

/// <summary>What converting PEG texture packs did to a packfile.</summary>
/// <param name="Package">The packfile with each readable .peg converted (replaced by its files, or followed by them).</param>
/// <param name="ReplacedPegs">The .peg entries converted.</param>
/// <param name="AddedFiles">The entries added (.tga textures and frames, .atx animations).</param>
/// <param name="Textures">Textures converted (an animation counts once).</param>
/// <param name="Animations">Animations converted to frames plus an .atx.</param>
/// <param name="Skipped">Textures left out: (.peg entry, texture, reason).</param>
/// <param name="Unreadable">.peg entries that could not be read and stay in the packfile: (entry, reason).</param>
/// <param name="IdenticalCopies">Textures left out because another PEG file, or an entry already in the packfile,
/// has the same pixels under the same name.</param>
/// <param name="Conflicts">Textures present in different versions (in several PEG files, or a PEG file and the
/// packfile), one line each ("envirohand.tga: 2 different versions, kept 128×128 from a.peg, skipped 64×64 from
/// b.peg"; "envirohand.tga: replaced the 64×64 already in the packfile by 128×128 from b.peg").</param>
/// <param name="Mpeg2Decoded">MPEG-2 compressed backgrounds converted to .tga (counted in <paramref name="Textures"/> too).</param>
/// <param name="Mpeg2Sequences">The .atx files written for numbered MPEG-2 frame sequences.</param>
/// <param name="DifferentCopies">Textures among <paramref name="Skipped"/> left out for a larger version.</param>
/// <param name="ReplacedEntries">Entries already in the packfile replaced by a larger converted version (and the
/// frames of a replaced animation).</param>
public sealed record Ps2ConvertReport(
    VppPackage Package,
    IReadOnlyList<string> ReplacedPegs,
    IReadOnlyList<string> AddedFiles,
    int Textures,
    int Animations,
    IReadOnlyList<(string Peg, string Texture, string Reason)> Skipped,
    IReadOnlyList<(string Peg, string Reason)> Unreadable,
    int IdenticalCopies = 0,
    IReadOnlyList<string>? Conflicts = null,
    int Mpeg2Decoded = 0,
    IReadOnlyList<string>? Mpeg2Sequences = null,
    int DifferentCopies = 0,
    IReadOnlyList<string>? ReplacedEntries = null)
{
    /// <summary>Textures among <see cref="Skipped"/> because they were not ticked.</summary>
    public int NotChosen => Skipped.Count(s => s.Reason == PegConverter.NotChosenReason);

    /// <summary>The MPEG-2 backgrounds among <see cref="Skipped"/> because decoding them is switched off.</summary>
    public int Mpeg2Count => Skipped.Count(s => s.Reason == PegConverter.Mpeg2Reason);

    /// <summary>The MPEG-2 backgrounds among <see cref="Skipped"/> whose streams could not be decoded.</summary>
    public int Mpeg2Failed => Skipped.Count(s => s.Reason.StartsWith(PegConverter.Mpeg2FailedReason, StringComparison.Ordinal));

    /// <summary>
    /// "Converted 1,494 textures (30 animated, 191 MPEG-2 backgrounds) from 167 PEG texture packs into 1,880 files;
    /// numbered MPEG-2 frames also listed in interface-bg-mm.atx; 151 identical copies left out; 2 textures differ
    /// between PEG files (the largest kept): a.tga: …".
    /// </summary>
    public string Summary
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var kinds = new List<string>();
            if (Animations > 0) kinds.Add(string.Create(inv, $"{Animations:N0} animated"));
            if (Mpeg2Decoded > 0) kinds.Add(string.Create(inv, $"{Mpeg2Decoded:N0} MPEG-2 background{(Mpeg2Decoded == 1 ? "" : "s")}"));
            string text = string.Create(inv, $"Converted {Textures:N0} texture{(Textures == 1 ? "" : "s")}")
                + (kinds.Count > 0 ? " (" + string.Join(", ", kinds) + ")" : string.Empty)
                + string.Create(inv, $" from {ReplacedPegs.Count:N0} PEG texture pack{(ReplacedPegs.Count == 1 ? "" : "s")} into {AddedFiles.Count:N0} file{(AddedFiles.Count == 1 ? "" : "s")}");
            var notes = new List<string>();
            if (Mpeg2Sequences is { Count: > 0 } sequences)
                notes.Add(string.Create(inv, $"numbered MPEG-2 frames also listed in {string.Join(", ", sequences)} (looping at {PegConverter.Mpeg2SequenceFps} fps, rate unverified)"));
            if (Mpeg2Count > 0) notes.Add(string.Create(inv, $"{Mpeg2Count:N0} MPEG-2 background{(Mpeg2Count == 1 ? "" : "s")} not converted (decoding is switched off)"));
            if (Mpeg2Failed > 0) notes.Add(string.Create(inv, $"{Mpeg2Failed:N0} MPEG-2 background{(Mpeg2Failed == 1 ? "" : "s")} could not be decoded"));
            if (IdenticalCopies > 0) notes.Add(string.Create(inv, $"{IdenticalCopies:N0} identical cop{(IdenticalCopies == 1 ? "y" : "ies")} of textures already present left out"));
            var conflicts = Conflicts ?? [];
            int differing = conflicts.Select(c => c[..Math.Max(0, c.IndexOf(':', StringComparison.Ordinal))]).Distinct(VppNames.Comparer).Count();
            if (conflicts.Count > 0)
                notes.Add(string.Create(inv, $"{differing:N0} texture{(differing == 1 ? " exists" : "s exist")} in more than one version (the largest kept): ")
                    + string.Join("; ", conflicts.Take(3)) + (conflicts.Count > 3 ? string.Create(inv, $"; and {conflicts.Count - 3:N0} more") : string.Empty));
            if (NotChosen > 0) notes.Add(string.Create(inv, $"{NotChosen:N0} texture{(NotChosen == 1 ? "" : "s")} not ticked"));
            int other = Skipped.Count - Mpeg2Count - Mpeg2Failed - IdenticalCopies - DifferentCopies - NotChosen;
            if (other > 0) notes.Add(string.Create(inv, $"{other:N0} other texture{(other == 1 ? "" : "s")} skipped"));
            if (Unreadable.Count > 0) notes.Add(string.Create(inv, $"{Unreadable.Count:N0} PEG file{(Unreadable.Count == 1 ? "" : "s")} could not be read and stay as they are"));
            return text + (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty) + ".";
        }
    }
}

/// <summary>
/// Packfiles from Red Faction's PlayStation 2 version. Their layout is the PC one (version 1); what differs is the
/// content: PEG texture packs instead of .tga/.vbm, PS2 meshes (.rfm, .rfc), sound effects (.vse), music (.vmu)
/// and PS2 levels, none of which the PC game loads.
/// </summary>
public static class Ps2Packfiles
{
    /// <summary>The banner above a PlayStation 2 packfile without .peg entries (left).</summary>
    public const string Banner = "This packfile is from the PlayStation 2 version. Its levels (.rfl) are not compatible with the PC game; its meshes (.rfm, .rfc, .v3d, .vcm) convert to .v3m/.v3c with Convert meshes..., and its sounds (.vse, .vmu) to .wav or .ogg with Convert sounds... (right-click them, or the Packfile menu).";

    /// <summary>The banner above a PlayStation 2 packfile that holds .peg entries.</summary>
    public const string PegBanner = "This packfile is from the PlayStation 2 version. To convert its PEG texture packs to .tga for the PC game, select .peg entries and use Convert to .tga... (right-click them, or the Packfile menu); Convert meshes... makes .v3m/.v3c from its meshes and Convert sounds... makes .wav or .ogg from its sounds. Levels (.rfl) are not compatible.";

    /// <summary>The banner above a PlayStation 2 packfile holding <paramref name="pegEntries"/> .peg entries.</summary>
    public static string BannerFor(int pegEntries) => pegEntries > 0 ? PegBanner : Banner;

    /// <summary>Entry types only the PlayStation 2 version uses (their presence marks a PlayStation 2 packfile).</summary>
    public static IReadOnlySet<string> Ps2OnlyExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".peg", ".rfm", ".rfc", ".vse", ".vmu" };

    /// <summary>True when <paramref name="name"/> is of a PlayStation 2-only type.</summary>
    public static bool IsPs2Name(string name) => Ps2OnlyExtensions.Contains(VppNames.ExtensionOf(name));

    /// <summary>True when <paramref name="name"/> is a PEG texture pack.</summary>
    public static bool IsPeg(string name) => VppNames.ExtensionOf(name) == ".peg";

    /// <summary>True when the packfile holds PlayStation 2-only entries.</summary>
    public static bool IsPs2(VppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        foreach (var item in package.Items) if (IsPs2Name(item.Name)) return true;
        return false;
    }

    /// <summary>The .peg entries of the packfile.</summary>
    public static IReadOnlyList<VppItem> Pegs(VppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return [.. package.Items.Where(i => IsPeg(i.Name))];
    }

    /// <summary>
    /// Reads and converts .peg entries (any thread): the chosen ones, or every one. Names are kept unique across the
    /// packfile. A texture that several of the PEG files hold, or whose name an entry already in the packfile has (an
    /// earlier conversion's, or the packfile's own), is compared, because the game finds textures by name so only one
    /// can stay: identical copies are left out; when they differ, the one with the most pixels is kept (on a tie the
    /// one already in the packfile, else the first) and the others are listed as conflicts (<see cref="PegCopy"/>); a
    /// larger converted texture replaces the entry already there (<see cref="PegEntryConversion.Replaces"/>).
    /// Animation frames and other names an entry already has are avoided instead.
    /// </summary>
    /// <param name="package">The packfile.</param>
    /// <param name="progress">(done, total, current entry name), from the calling thread.</param>
    /// <param name="fpsFor">The PC game's frame rate for an animated texture's name, when known.</param>
    /// <param name="cancellationToken">Stops between entries and textures.</param>
    /// <param name="decodeMpeg2">Decode MPEG-2 compressed backgrounds (else they are listed as skipped).</param>
    /// <param name="blackKey">Black as transparent for the MPEG-2 backgrounds, or null (see <see cref="PegConverter.Convert"/>).</param>
    /// <param name="entries">The .peg entries to convert and their chosen textures; null for every .peg entry, all textures.</param>
    public static IReadOnlyList<PegEntryConversion> ConvertEntries(VppPackage package, Action<int, int, string>? progress = null,
        Func<string, int?>? fpsFor = null, CancellationToken cancellationToken = default, bool decodeMpeg2 = true,
        PegBlackKey? blackKey = null, IReadOnlyList<PegEntryChoice>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        var chosen = entries?.GroupBy(e => e.Index).ToDictionary(g => g.Key, g => g.First());
        var pegs = package.Items.Select((item, index) => (Item: item, Index: index))
            .Where(p => IsPeg(p.Item.Name) && (chosen is null || chosen.ContainsKey(p.Index))).ToList();
        var existing = ExistingNames(package, pegs.Select(p => p.Index));
        // Each name produced so far: the texture it belongs to (copies of one texture may produce the same names; they
        // are sorted out below) and the .peg that gave it (for messages).
        var owner = new Dictionary<string, (string Texture, string Peg)>(VppNames.Comparer);
        var results = new List<PegEntryConversion>(pegs.Count);
        for (int i = 0; i < pegs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = pegs[i].Item;
            progress?.Invoke(i, pegs.Count, item.Name);
            PegConversionResult result;
            try
            {
                byte[] bytes = item.Source.ReadAll();
                // A texture's own name may be one an entry already has (compared below); its frames and an MPEG-2
                // sequence's .atx avoid those names.
                result = PegConverter.Convert(bytes, item.Name, null, fpsFor, cancellationToken,
                    (n, texture) =>
                    {
                        string own = PegConverter.OutputName(texture);
                        return (existing.Contains(n) && !VppNames.Comparer.Equals(n, own))
                            || (owner.TryGetValue(n, out var o) && !VppNames.Comparer.Equals(o.Texture, own));
                    },
                    decodeMpeg2, blackKey, chosen?.GetValueOrDefault(pegs[i].Index)?.Textures);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = new PegConversionResult(item.Name, null, [], [], "the data cannot be read: " + ex.Message);
            }
            if (result.Skipped.Any(s => s.Reason.StartsWith(PegConverter.NameTakenReason, StringComparison.Ordinal)))
            {
                result = result with
                {
                    Skipped = [.. result.Skipped.Select(s => s.Reason.StartsWith(PegConverter.NameTakenReason, StringComparison.Ordinal)
                        && owner.TryGetValue(s.Reason[PegConverter.NameTakenReason.Length..], out var from)
                        ? s with { Reason = $"{from.Peg} already gives {s.Reason[PegConverter.NameTakenReason.Length..]} (for its texture {from.Texture})" }
                        : s)],
                };
            }
            foreach (var file in result.Files) owner.TryAdd(file.Name, (PegConverter.OutputName(file.Texture), item.Name));
            results.Add(new PegEntryConversion(pegs[i].Index, item.Name, item.Source, result));
        }
        progress?.Invoke(pegs.Count, pegs.Count, string.Empty);
        return ResolveExisting(package, ResolveCopies(results), existing);
    }

    /// <summary>The names of the entries that stay when the .peg entries at <paramref name="converting"/> are converted (.peg entries aside).</summary>
    public static HashSet<string> ExistingNames(VppPackage package, IEnumerable<int> converting)
    {
        ArgumentNullException.ThrowIfNull(package);
        var skip = new HashSet<int>(converting ?? []);
        var names = new HashSet<string>(VppNames.Comparer);
        for (int i = 0; i < package.Count; i++)
            if (!skip.Contains(i) && !IsPeg(package.Items[i].Name)) names.Add(package.Items[i].Name);
        return names;
    }

    /// <summary>
    /// The size of the picture an entry holds: an image's, or for an .atx its first frame's (the entry it names in
    /// this packfile). Null when it cannot be read.
    /// </summary>
    public static (int Width, int Height)? EntrySize(VppPackage package, VppItem item)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            if (VppNames.ExtensionOf(item.Name) == ".atx")
                return AtxFrames(item).Select(package.Find).FirstOrDefault(f => f is not null) is { } frame && VppNames.ExtensionOf(frame.Name) != ".atx"
                    ? EntrySize(package, frame) : null;
            var info = ImageProbe.Probe(item.Source.ReadAll(), item.Name);
            return info.Width > 0 && info.Height > 0 ? (info.Width, info.Height) : null;
        }
        catch (Exception ex) when (ex is ImageDecodeException or Cairn.Formats.AssetFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The frame names an .atx entry lists (its frames' <c>file</c>), in order; empty when unreadable.</summary>
    private static List<string> AtxFrames(VppItem atx) => AtxImages(atx) is { } images ? images.Frames : [];

    /// <summary>
    /// The images an .atx entry names, read with the Atx module's parser: its frames' <c>file</c> (in order) and the
    /// header's <c>alpha_mask</c>. Null when the entry cannot be read or is not valid TOML (the game refuses it too).
    /// </summary>
    internal static (List<string> Frames, List<string> Other)? AtxImages(VppItem atx)
    {
        try
        {
            var (text, _) = Cairn.Atx.Workspace.AtxTextFiles.Decode(atx.Source.ReadAll());
            if (Cairn.Atx.Parsing.AtxParser.Parse(text, atx.Name).Model is not { } model) return null;
            var frames = model.Frames.Select(f => f.File?.Value).OfType<string>().Where(f => f.Length > 0).ToList();
            var other = new List<string>();
            if (model.Header.AlphaMask?.Value is { Length: > 0 } mask) other.Add(mask);
            return (frames, other);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits the frames of an .atx entry that a larger conversion replaces into those that may go and those that stay
    /// because another .atx of the packfile names them (as a frame or an alpha mask), or this conversion writes that
    /// name. When any other .atx cannot be read, every frame stays (it might name them).
    /// </summary>
    internal static (List<string> Remove, List<string> Keep) SplitOldFrames(VppPackage package, VppItem old, IEnumerable<string> frames,
        ISet<string> produced, Dictionary<VppItem, (List<string> Frames, List<string> Other)?> cache)
    {
        var remove = new List<string>();
        var keep = new List<string>();
        var named = new HashSet<string>(VppNames.Comparer);
        bool unreadable = false;
        foreach (var item in package.Items)
        {
            if (ReferenceEquals(item, old) || VppNames.ExtensionOf(item.Name) != ".atx") continue;
            if (!cache.TryGetValue(item, out var images)) cache[item] = images = AtxImages(item);
            if (images is not { } i) { unreadable = true; continue; }
            named.UnionWith(i.Frames);
            named.UnionWith(i.Other);
        }
        foreach (var frame in frames.Distinct(VppNames.Comparer))
        {
            if (package.Find(frame) is null || produced.Contains(frame)) continue;
            (unreadable || named.Contains(frame) ? keep : remove).Add(frame);
        }
        return (remove, keep);
    }

    /// <summary>
    /// Compares each converted texture whose name an entry that stays already has with that entry (see
    /// <see cref="ConvertEntries"/>): identical or not larger → left out; larger → it replaces the entry.
    /// </summary>
    private static List<PegEntryConversion> ResolveExisting(VppPackage package, List<PegEntryConversion> results, HashSet<string> existing)
    {
        if (existing.Count == 0) return results;
        var inv = CultureInfo.InvariantCulture;
        var replacedNames = new HashSet<string>(VppNames.Comparer);
        var produced = new HashSet<string>(results.SelectMany(r => r.Result.Files).Select(f => f.Name), VppNames.Comparer);
        var atxCache = new Dictionary<VppItem, (List<string> Frames, List<string> Other)?>(ReferenceEqualityComparer.Instance);
        var output = new List<PegEntryConversion>(results.Count);
        foreach (var c in results)
        {
            var drop = new HashSet<int>();
            var skips = new List<PegSkippedTexture>();
            var replaces = new List<PegReplacement>();
            foreach (var group in c.Result.Files.GroupBy(f => f.Texture.Index))
            {
                var texture = group.First().Texture;
                string key = PegConverter.OutputName(texture);
                if (!existing.Contains(key) || package.Find(key) is not { } old) continue;
                bool isAtx = VppNames.ExtensionOf(key) == ".atx";
                var newImages = isAtx ? group.Where(f => VppNames.ExtensionOf(f.Name) != ".atx").ToList() : group.Where(f => VppNames.Comparer.Equals(f.Name, key)).ToList();
                var oldFrames = isAtx ? AtxFrames(old) : [];
                var oldImages = isAtx ? oldFrames.Select(package.Find).ToList() : [old];
                bool identical = oldImages.Count == newImages.Count && oldImages.Zip(newImages).All(p => p.First is not null && p.First.Source.ReadAll().AsSpan().SequenceEqual(p.Second.Bytes));
                string mine = string.Create(inv, $"{texture.Width}×{texture.Height}");
                if (identical)
                {
                    drop.Add(texture.Index);
                    skips.Add(new PegSkippedTexture(texture.Name, $"the packfile already holds the same {key} (identical; the game finds textures by name, so one copy is enough)", texture.IsMpeg2, PegCopy.Identical));
                    continue;
                }
                var size = EntrySize(package, old);
                if (size is not { } s)
                {
                    drop.Add(texture.Index);
                    skips.Add(new PegSkippedTexture(texture.Name, $"{key}: the packfile already has an entry of that name whose size cannot be read; it was kept, and {mine} from {c.EntryName} was left out", texture.IsMpeg2, PegCopy.Different));
                    continue;
                }
                string theirs = string.Create(inv, $"{s.Width}×{s.Height}");
                if ((long)texture.Width * texture.Height > (long)s.Width * s.Height && replacedNames.Add(key))
                {
                    var alsoRemove = new List<string>();
                    string frameNote = string.Empty;
                    if (isAtx)
                    {
                        // the old animation's frames go with it, unless another .atx names them or this conversion writes that name
                        var (remove, keep) = SplitOldFrames(package, old, oldFrames, produced, atxCache);
                        alsoRemove.AddRange(remove);
                        if (remove.Count > 0) frameNote += string.Create(inv, $"; removed its {remove.Count:N0} old frame{(remove.Count == 1 ? "" : "s")}");
                        if (keep.Count > 0) frameNote += $"; kept {string.Join(", ", keep.Take(3))}{(keep.Count > 3 ? string.Create(inv, $" and {keep.Count - 3:N0} more") : string.Empty)} (named by another .atx, or it could not be read)";
                    }
                    replaces.Add(new PegReplacement(old.Name, old.Source, alsoRemove, $"{key}: replaced the {theirs} already in the packfile by {mine} from {c.EntryName}{frameNote}"));
                    continue;
                }
                drop.Add(texture.Index);
                skips.Add(new PegSkippedTexture(texture.Name, $"{key}: kept the {theirs} already in the packfile, skipped {mine} from {c.EntryName}", texture.IsMpeg2, PegCopy.Different));
            }
            output.Add(drop.Count == 0 && replaces.Count == 0 ? c : c with
            {
                Result = c.Result with
                {
                    Files = [.. c.Result.Files.Where(f => !drop.Contains(f.Texture.Index))],
                    Skipped = [.. c.Result.Skipped, .. skips],
                },
                Replaces = [.. c.Replaces ?? [], .. replaces],
            });
        }
        return output;
    }

    /// <summary>One converted texture of one PEG file: its files.</summary>
    private sealed record Copy(int Result, PegTexture Texture, List<PegOutputFile> Files);

    /// <summary>Keeps one copy of each texture several PEG files gave (see <see cref="ConvertEntries"/>).</summary>
    private static List<PegEntryConversion> ResolveCopies(List<PegEntryConversion> results)
    {
        var copies = new Dictionary<string, List<Copy>>(VppNames.Comparer);
        for (int r = 0; r < results.Count; r++)
            foreach (var group in results[r].Result.Files.GroupBy(f => f.Texture.Index))
            {
                var texture = group.First().Texture;
                string key = PegConverter.OutputName(texture);
                if (!copies.TryGetValue(key, out var list)) copies[key] = list = [];
                list.Add(new Copy(r, texture, [.. group]));
            }
        var drop = new HashSet<(int Result, int Texture)>();
        var extra = new Dictionary<int, List<PegSkippedTexture>>();
        var inv = CultureInfo.InvariantCulture;
        foreach (var (name, list) in copies)
        {
            if (list.Count < 2) continue;
            var kept = list[0];
            foreach (var c in list) if ((long)c.Texture.Width * c.Texture.Height > (long)kept.Texture.Width * kept.Texture.Height) kept = c;
            var versions = new List<Copy>();
            foreach (var c in list) if (!versions.Any(v => SameContent(v, c))) versions.Add(c);
            string Size(Copy c) => string.Create(inv, $"{c.Texture.Width}×{c.Texture.Height}");
            foreach (var c in list)
            {
                if (ReferenceEquals(c, kept)) continue;
                bool same = SameContent(c, kept);
                string reason = same
                    ? $"{results[kept.Result].EntryName} holds the same {name} (identical; the game finds textures by name, so one copy is enough)"
                    : string.Create(inv, $"{name}: {versions.Count} different versions, kept {Size(kept)} from {results[kept.Result].EntryName}, skipped {Size(c)} from {results[c.Result].EntryName}");
                drop.Add((c.Result, c.Texture.Index));
                if (!extra.TryGetValue(c.Result, out var skips)) extra[c.Result] = skips = [];
                skips.Add(new PegSkippedTexture(c.Texture.Name, reason, false, same ? PegCopy.Identical : PegCopy.Different));
            }
        }
        if (drop.Count == 0) return results;
        return [.. results.Select((c, r) => !extra.TryGetValue(r, out var skips) ? c : c with
        {
            Result = c.Result with
            {
                Files = [.. c.Result.Files.Where(f => !drop.Contains((r, f.Texture.Index)))],
                Skipped = [.. c.Result.Skipped, .. skips],
            },
        })];
    }

    /// <summary>True when two copies hold the same pixels (an animation's .atx names its PEG, so only frames count).</summary>
    private static bool SameContent(Copy a, Copy b)
    {
        var fa = a.Files.Where(f => !f.Name.EndsWith(".atx", StringComparison.OrdinalIgnoreCase)).ToList();
        var fb = b.Files.Where(f => !f.Name.EndsWith(".atx", StringComparison.OrdinalIgnoreCase)).ToList();
        return fa.Count == fb.Count && fa.Zip(fb).All(p => p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes));
    }

    /// <summary>
    /// Replaces each converted .peg entry (still holding the data it was converted from) by its files, at its
    /// position, or adds them after it with <paramref name="keepPegs"/>. Entries a larger converted texture replaces
    /// (<see cref="PegEntryConversion.Replaces"/>, while unchanged) are removed. .peg entries that could not be read
    /// stay. Pure: returns a new package and the report.
    /// </summary>
    public static Ps2ConvertReport Apply(VppPackage package, IReadOnlyList<PegEntryConversion> conversions, bool keepPegs = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(conversions);
        var byIndex = new Dictionary<int, PegEntryConversion>();
        foreach (var c in conversions) byIndex.TryAdd(c.Index, c);
        bool Applies(int index, PegEntryConversion c) =>
            index < package.Count && c.Source == package.Items[index].Source && string.Equals(c.EntryName, package.Items[index].Name, StringComparison.Ordinal) && c.Result.Error is null;
        // entries replaced by larger converted versions (only while they still hold what was compared)
        var remove = new HashSet<string>(VppNames.Comparer);
        var replacedEntries = new List<string>();
        var replacedNotes = new List<string>();
        foreach (var (index, c) in byIndex)
            foreach (var r in c.Replaces ?? [])
                if (Applies(index, c) && package.Find(r.Name) is { } entry && entry.Source == r.Source && remove.Add(r.Name))
                {
                    replacedEntries.Add(r.Name);
                    replacedNotes.Add(r.Note);
                    foreach (var frame in r.AlsoRemove) if (remove.Add(frame)) replacedEntries.Add(frame);
                }
        var used = new HashSet<string>(package.Items.Select(i => i.Name).Where(n => !remove.Contains(n)), VppNames.Comparer);
        var items = ImmutableArray.CreateBuilder<VppItem>(package.Count);
        var replaced = new List<string>();
        var added = new List<string>();
        var skipped = new List<(string, string, string)>();
        var unreadable = new List<(string, string)>();
        var conflicts = new List<string>();
        int textures = 0, animations = 0, identical = 0, different = 0, mpeg2 = 0;
        var sequences = new List<string>();
        for (int index = 0; index < package.Count; index++)
        {
            var item = package.Items[index];
            if (!byIndex.TryGetValue(index, out var c) || c.Source != item.Source || !string.Equals(c.EntryName, item.Name, StringComparison.Ordinal))
            {
                if (!remove.Contains(item.Name) || IsPeg(item.Name)) items.Add(item);
                continue;
            }
            if (c.Result.Error is { } error) { unreadable.Add((item.Name, error)); items.Add(item); continue; }
            if (keepPegs) items.Add(item);
            else used.Remove(item.Name);
            replaced.Add(item.Name);
            foreach (var file in c.Result.Files)
            {
                // ConvertEntries kept names unique; never add a second entry of one name all the same.
                if (!used.Add(file.Name)) { skipped.Add((item.Name, file.Texture.Name, $"another entry is already named {file.Name}")); continue; }
                items.Add(new VppItem(file.Name, new MemorySource(file.Bytes), VppItemState.Added));
                added.Add(file.Name);
            }
            textures += c.Result.ConvertedTextures;
            animations += c.Result.AnimatedTextures;
            mpeg2 += c.Result.Mpeg2Textures;
            sequences.AddRange(c.Result.Mpeg2Sequences.Select(f => f.Name).Where(added.Contains));
            skipped.AddRange(c.Result.Skipped.Select(s => (item.Name, s.Name, s.Reason)));
            identical += c.Result.Skipped.Count(s => s.Copy == PegCopy.Identical);
            different += c.Result.Skipped.Count(s => s.Copy == PegCopy.Different);
            conflicts.AddRange(c.Result.Skipped.Where(s => s.Copy == PegCopy.Different).Select(s => s.Reason));
        }
        conflicts.AddRange(replacedNotes);
        var result = replaced.Count == 0 ? package : package.WithItems(items.ToImmutable());
        return new Ps2ConvertReport(result, replaced, added, textures, animations, skipped, unreadable, identical, conflicts, mpeg2, sequences, different, replacedEntries);
    }
}
