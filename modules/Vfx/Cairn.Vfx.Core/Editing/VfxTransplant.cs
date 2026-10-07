using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>One copied section: where it came from and where (and under which name) it landed.</summary>
public sealed record VfxTransplantedSection(int SourceIndex, string SourceName, int TargetIndex, string TargetName);

/// <summary>Outcome of <see cref="VfxTransplant"/>: the new file, the copied sections, the material mapping and warnings.</summary>
/// <param name="File">The new file (version 0x40006).</param>
/// <param name="Sections">The copies in source order.</param>
/// <param name="MaterialMap">Source material index -> material index in the new file (reused or appended).</param>
/// <param name="AddedMaterials">How many material sections were appended (the rest were reused).</param>
/// <param name="Warnings">Upgrades, cleared references and clamped start times.</param>
public sealed record VfxTransplantResult(VfxFile File, IReadOnlyList<VfxTransplantedSection> Sections,
    IReadOnlyDictionary<int, int> MaterialMap, int AddedMaterials, IReadOnlyList<string> Warnings)
{
    /// <summary>Source name -> new name (first section of that name wins when the source repeats a name).</summary>
    public IReadOnlyDictionary<string, string> Names =>
        Sections.GroupBy(s => s.SourceName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().TargetName, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Copies meshes, particle systems, dummies, lights and spacewarps from one VFX into another (or within one file).
/// <list type="bullet">
/// <item>Both files end up at version 0x40006: an older source or target is upgraded with <see cref="VfxUpgrade.ToCurrent"/>
/// first (a target upgrade is reported as a warning). Source indices refer to the source as given; the upgrade keeps
/// the positions of the existing sections and only appends material sections.</item>
/// <item>Materials used by the copied sections are reused when the target already holds an identical material, otherwise
/// appended; mesh slots and particle material indices are remapped.</item>
/// <item>Names are made unique against the target (name_2, name_3, ..., case-insensitive). Parent names and
/// particle-system spacewarp names that point at a copied section follow its new name. A reference to a section outside the
/// copied set is kept when the target has a section of that name (or it is "Scene Root"); otherwise the parent becomes
/// "Scene Root" / the warp entry is removed, and a warning says so.</item>
/// <item><c>timeOffset</c> (effect frames, 15 per second) shifts mesh start/end times (by offset / 15 s), mesh key times
/// (by offset x 320 ticks) and particle start times; dummy, light and spacewarp frame lists (indexed by effect frame)
/// get the first frame repeated in front (positive offset) or leading frames dropped (negative, at least one kept).
/// Material tracks, light initial values and per-frame mesh data are not shifted. Start times that would go negative
/// are clamped to 0 (warning).</item>
/// <item>Copied sections go before the target's material block, in source order; new materials are appended.
/// The header end frame becomes max(target end, source end + offset).</item>
/// </list>
/// The model is immutable (records over immutable arrays), so the copies share no mutable state with the source,
/// which is never changed.
/// </summary>
public static class VfxTransplant
{
    private const string SceneRoot = "Scene Root";

    /// <summary>Copies <paramref name="sections"/> (indices into <paramref name="source"/>) into <paramref name="target"/>.</summary>
    public static VfxTransplantResult Copy(VfxFile source, VfxFile target, IEnumerable<int> sections, int timeOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sections);
        var warnings = new List<string>();
        var picked = sections.Distinct().Order().ToArray();
        foreach (int i in picked)
        {
            if ((uint)i >= (uint)source.Sections.Length) throw new ArgumentOutOfRangeException(nameof(sections), $"Section {i} is not in the source.");
            if (!IsObject(source.Sections[i])) throw new ArgumentException($"Section {i} is {source.Sections[i].GetType().Name}; only meshes, particle systems, dummies, lights and spacewarps can be copied.", nameof(sections));
        }
        var src = Current(source);
        foreach (int i in picked)
            if (src.Sections[i].GetType() != source.Sections[i].GetType()) throw new InvalidOperationException($"Upgrading the source moved section {i}.");
        var dst = target.Version == VfxVersion.Current ? target : Current(target);
        if (!ReferenceEquals(dst, target)) warnings.Add($"Target upgraded from version 0x{target.Version:X} to 0x{VfxVersion.Current:X}.");

        // Materials: identical ones are reused, the others appended in source order.
        var srcMaterials = src.Sections.OfType<VfxMaterial>().ToArray();
        var dstMaterials = dst.Sections.OfType<VfxMaterial>().ToList();
        int originalMaterials = dstMaterials.Count;
        var materialMap = new SortedDictionary<int, int>();
        foreach (int m in picked.SelectMany(i => MaterialsOf(src.Sections[i])).Distinct().Order())
        {
            if (m >= srcMaterials.Length) throw new ArgumentException($"The source names material {m} but has only {srcMaterials.Length}.", nameof(source));
            int found = dstMaterials.FindIndex(x => SameMaterial(x, srcMaterials[m]));
            if (found < 0) { dstMaterials.Add(srcMaterials[m]); found = dstMaterials.Count - 1; }
            materialMap[m] = found;
        }

        // Names unique against the target and against each other.
        var used = new HashSet<string>(dst.Sections.Select(VfxEdit.NameOf).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var targetNames = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var newNames = new string[picked.Length];
        for (int k = 0; k < picked.Length; k++)
        {
            string name = VfxEdit.NameOf(src.Sections[picked[k]])!, unique = name;
            for (int n = 2; used.Contains(unique); n++) unique = $"{name}_{n}";
            used.Add(unique);
            newNames[k] = unique;
            renames.TryAdd(name, unique);
        }

        string Parent(string parent, string owner)
        {
            if (renames.TryGetValue(parent, out var r)) return r;
            if (string.Equals(parent, SceneRoot, StringComparison.OrdinalIgnoreCase) || targetNames.Contains(parent)) return parent;
            if (string.IsNullOrEmpty(parent)) return SceneRoot;
            warnings.Add($"'{owner}': parent '{parent}' is not in the target; set to '{SceneRoot}'.");
            return SceneRoot;
        }

        ImmutableArray<string> Warps(ImmutableArray<string> warps, string owner)
        {
            var b = ImmutableArray.CreateBuilder<string>(warps.Length);
            foreach (var w in warps)
            {
                if (renames.TryGetValue(w, out var r)) b.Add(r);
                else if (targetNames.Contains(w)) b.Add(w);
                else warnings.Add($"'{owner}': spacewarp '{w}' is not in the target; removed from its list.");
            }
            return b.ToImmutable();
        }

        int Mat(int m) => m < 0 ? m : materialMap[m];

        var copies = new VfxSection[picked.Length];
        for (int k = 0; k < picked.Length; k++)
        {
            string name = newNames[k];
            copies[k] = src.Sections[picked[k]] switch
            {
                VfxMesh m => Shift(m with
                {
                    Name = name, Parent = Parent(m.Parent, name),
                    MaterialIndices = m.MaterialIndices?.Select(Mat).ToImmutableArray(),
                }, timeOffset, warnings),
                VfxParticleSystem p => Shift(p with
                {
                    Name = name, Parent = Parent(p.Parent, name), Warps = Warps(p.Warps, name),
                    MaterialIndex = p.MaterialIndex is { } pm ? Mat(pm) : null,
                }, timeOffset, warnings),
                VfxDummy d => d with { Name = name, Parent = Parent(d.Parent, name), Frames = ShiftFrames(d.Frames, timeOffset) },
                VfxLight l => l with { Name = name, Parent = Parent(l.Parent, name), Frames = ShiftFrames(l.Frames, timeOffset) },
                VfxSpacewarp w => w with { Name = name, Parent = Parent(w.Parent, name), Frames = ShiftFrames(w.Frames, timeOffset) },
                var s => throw new InvalidOperationException($"Unexpected {s.GetType().Name}."),
            };
        }

        // Objects before the first material section, new materials at the end.
        var list = dst.Sections.ToList();
        int firstMat = list.FindIndex(s => s is VfxMaterial);
        list.InsertRange(firstMat < 0 ? list.Count : firstMat, copies);
        list.AddRange(dstMaterials.Skip(originalMaterials));
        int end = picked.Length == 0 ? dst.EndFrame : Math.Max(dst.EndFrame, Math.Max(0, src.EndFrame + timeOffset));
        var file = dst with { Sections = [.. list], EndFrame = end };

        var placed = picked.Select((i, k) => new VfxTransplantedSection(i, VfxEdit.NameOf(src.Sections[i])!,
            list.FindIndex(s => ReferenceEquals(s, copies[k])), newNames[k])).ToArray();
        return new VfxTransplantResult(file, placed, materialMap, dstMaterials.Count - originalMaterials, warnings);
    }

    /// <summary>Copies every mesh, particle system, dummy, light and spacewarp of <paramref name="source"/> into <paramref name="target"/>.</summary>
    public static VfxTransplantResult CopyAll(VfxFile source, VfxFile target, int timeOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Copy(source, target, Enumerable.Range(0, source.Sections.Length).Where(i => IsObject(source.Sections[i])), timeOffset);
    }

    /// <summary>
    /// Duplicates a set of sections within one file (same rules as <see cref="Copy"/>: the copies share the materials,
    /// references among the copies follow the new names, references to other sections stay).
    /// </summary>
    public static VfxTransplantResult Duplicate(VfxFile file, IEnumerable<int> sections, int timeOffset = 0) =>
        Copy(file, file, sections, timeOffset);

    /// <inheritdoc cref="Duplicate(VfxFile, IEnumerable{int}, int)"/>
    public static VfxTransplantResult Duplicate(VfxFile file, params int[] sections) => Copy(file, file, sections);

    /// <summary>True for the section types that can be copied.</summary>
    public static bool IsObject(VfxSection s) => s is VfxMesh or VfxParticleSystem or VfxDummy or VfxLight or VfxSpacewarp;

    /// <summary>Field-by-field (and element-by-element) material equality.</summary>
    public static bool SameMaterial(VfxMaterial a, VfxMaterial b)
    {
        static VfxMaterial Scalars(VfxMaterial m) => m with { Mix = null, SelfIllumination = ImmutableArray<float>.Empty, Opacity = null };
        static bool Seq(ImmutableArray<float>? x, ImmutableArray<float>? y) =>
            x is { } xs ? y is { } ys && xs.SequenceEqual(ys) : y is null;
        return Scalars(a) == Scalars(b) && Seq(a.Mix, b.Mix) && Seq(a.Opacity, b.Opacity) && a.SelfIllumination.SequenceEqual(b.SelfIllumination);
    }

    private static VfxFile Current(VfxFile f) => f.Version == VfxVersion.Current ? f : VfxUpgrade.ToCurrent(f);

    private static IEnumerable<int> MaterialsOf(VfxSection s) => s switch
    {
        VfxMesh { MaterialIndices: { } mi } => mi.Where(m => m >= 0),
        VfxParticleSystem { MaterialIndex: int m } when m >= 0 => [m],
        _ => [],
    };

    private static VfxMesh Shift(VfxMesh m, int offset, List<string> warnings)
    {
        if (offset == 0) return m;
        float seconds = offset / (float)VfxTime.FramesPerSecond;
        float? start = m.StartTime + seconds, end = m.EndTime + seconds;
        int ticks = offset * VfxTime.TicksPerFrame;
        if (start < 0)
        {
            end -= start;
            start = 0;
            // Keys move by the same time the start actually moved (review-findings-2 #9), so the motion keeps its start.
            ticks = (int)MathF.Round(-(m.StartTime ?? 0) * VfxTime.FramesPerSecond * VfxTime.TicksPerFrame);
            warnings.Add($"'{m.Name}': start time clamped to 0; the object and its keys moved by {-(m.StartTime ?? 0):0.###} s instead of {seconds:0.###} s.");
        }
        var keys = m.Keys;
        if (keys is not null)
        {
            keys = new VfxKeyLists(
                keys.Translation.Select(k => k with { Time = k.Time + ticks }).ToImmutableArray(),
                keys.Rotation.Select(k => k with { Time = k.Time + ticks }).ToImmutableArray(),
                keys.Scale.Select(k => k with { Time = k.Time + ticks }).ToImmutableArray());
        }
        return m with { StartTime = start, EndTime = end, Keys = keys };
    }

    private static VfxParticleSystem Shift(VfxParticleSystem p, int offset, List<string> warnings)
    {
        if (offset == 0) return p;
        int start = p.StartTime + offset;
        if (start < 0) { warnings.Add($"'{p.Name}': start time clamped to 0."); start = 0; }
        return p with { StartTime = start };
    }

    private static ImmutableArray<T> ShiftFrames<T>(ImmutableArray<T> frames, int offset)
    {
        if (offset == 0 || frames.IsEmpty) return frames;
        return offset > 0
            ? frames.InsertRange(0, Enumerable.Repeat(frames[0], offset))
            : frames.RemoveRange(0, Math.Min(-offset, frames.Length - 1));
    }
}
