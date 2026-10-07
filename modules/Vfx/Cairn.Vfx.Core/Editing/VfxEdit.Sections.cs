using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>
/// Pure VfxFile -> VfxFile edits on version-0x40006 files (others throw <see cref="InvalidOperationException"/>).
/// Sections are addressed by index in <see cref="VfxFile.Sections"/>. An edit that changes nothing returns the same instance.
/// </summary>
public static partial class VfxEdit
{
    internal static void RequireCurrent(VfxFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Version != VfxVersion.Current)
            throw new InvalidOperationException($"Edits need version 0x{VfxVersion.Current:X}; this file is 0x{file.Version:X} (use VfxUpgrade.ToCurrent).");
    }

    internal static T Get<T>(VfxFile file, int index) where T : VfxSection
    {
        RequireCurrent(file);
        if ((uint)index >= (uint)file.Sections.Length) throw new ArgumentOutOfRangeException(nameof(index));
        return file.Sections[index] as T ?? throw new ArgumentException($"Section {index} is {file.Sections[index].GetType().Name}, not {typeof(T).Name}.");
    }

    internal static VfxFile Put(VfxFile file, int index, VfxSection section) =>
        ReferenceEquals(file.Sections[index], section) || file.Sections[index].Equals(section)
            ? file : file with { Sections = file.Sections.SetItem(index, section) };

    /// <summary>Applies <paramref name="update"/> to the section at <paramref name="index"/> (any field of any section type).</summary>
    public static VfxFile Update<T>(VfxFile file, int index, Func<T, T> update) where T : VfxSection =>
        Put(file, index, update(Get<T>(file, index)) ?? throw new ArgumentNullException(nameof(update)));

    /// <summary>The object name of a section (null for materials and opaque sections).</summary>
    public static string? NameOf(VfxSection s) => s switch
    {
        VfxMesh m => m.Name, VfxParticleSystem p => p.Name, VfxDummy d => d.Name, VfxLight l => l.Name, VfxSpacewarp w => w.Name, _ => null,
    };

    /// <summary>Index of the first section named <paramref name="name"/>, or -1.</summary>
    public static int FindByName(VfxFile file, string name)
    {
        for (int i = 0; i < file.Sections.Length; i++) if (NameOf(file.Sections[i]) == name) return i;
        return -1;
    }

    public static IEnumerable<int> FindByType<T>(VfxFile file) where T : VfxSection =>
        Enumerable.Range(0, file.Sections.Length).Where(i => file.Sections[i] is T);

    /// <summary>Section index of material number <paramref name="materialIndex"/>, or -1.</summary>
    public static int MaterialSectionIndex(VfxFile file, int materialIndex) =>
        materialIndex < 0 ? -1 : FindByType<VfxMaterial>(file).Skip(materialIndex).DefaultIfEmpty(-1).First();

    /// <summary><paramref name="name"/>, or name_2, name_3, ... so that no other section uses it.</summary>
    public static string UniqueName(VfxFile file, string name)
    {
        var used = file.Sections.Select(NameOf).OfType<string>().ToHashSet();
        if (!used.Contains(name)) return name;
        for (int n = 2; ; n++) if (!used.Contains($"{name}_{n}")) return $"{name}_{n}";
    }

    /// <summary>Inserts a section; materials always go to the end (materials are the last block in stock files).</summary>
    public static VfxFile AddSection(VfxFile file, VfxSection section, int? at = null)
    {
        RequireCurrent(file);
        ArgumentNullException.ThrowIfNull(section);
        int firstMat = file.Sections.IndexOf(file.Sections.FirstOrDefault(s => s is VfxMaterial)!);
        int pos = at ?? (section is VfxMaterial || firstMat < 0 ? file.Sections.Length : firstMat);
        if (pos < 0 || pos > file.Sections.Length) throw new ArgumentOutOfRangeException(nameof(at));
        return file with { Sections = file.Sections.Insert(pos, section) };
    }

    /// <summary>
    /// Removes a section. Removing a material shifts higher material indices down; references to the removed
    /// material throw unless <paramref name="reassignTo"/> (an index in the remaining list, or -1 for faces "none") is given.
    /// Removing a named object clears references to it: objects parented to it take over its parent ("Scene Root" when it
    /// had none), and a removed spacewarp is dropped from particle-system spacewarp lists.
    /// </summary>
    public static VfxFile RemoveSection(VfxFile file, int index, int? reassignTo = null)
    {
        var s = Get<VfxSection>(file, index);
        var sections = file.Sections.RemoveAt(index);
        if (s is VfxMaterial)
        {
            int removed = file.Sections.Take(index).OfType<VfxMaterial>().Count();
            int remaining = sections.OfType<VfxMaterial>().Count();
            if (reassignTo is { } r && (r < -1 || r >= remaining)) throw new ArgumentOutOfRangeException(nameof(reassignTo));
            int Fix(int m, string owner) => m < removed ? m : m > removed ? m - 1
                : reassignTo ?? throw new InvalidOperationException($"Material {removed} is used by '{owner}'.");
            sections = sections.Select(x => x switch
            {
                VfxMesh m when m.MaterialIndices is { } mi => FixMesh(m, mi.Select(v => Fix(v, m.Name)).ToImmutableArray()),
                // particles need a material: "none" (-1) is refused for them rather than turned into material 0
                VfxParticleSystem p when p.MaterialIndex is { } pi && Fix(pi, p.Name) != pi => p with { MaterialIndex = Fix(pi, p.Name) is >= 0 and var to ? to
                    : throw new InvalidOperationException($"Material {removed} is used by the particle system '{p.Name}', which needs a material: choose a replacement.") },
                _ => x,
            }).ToImmutableArray();
        }
        // Dangling references are cleared, not reported: children of the removed object take over its parent, and
        // particle systems drop a removed spacewarp from their list. Skipped while another object still has the name.
        if (NameOf(s) is { Length: > 0 } name && !sections.Any(x => string.Equals(NameOf(x), name, StringComparison.OrdinalIgnoreCase)))
        {
            string? up = ParentOf(s);
            if (string.IsNullOrEmpty(up) || string.Equals(up, name, StringComparison.OrdinalIgnoreCase)) up = "Scene Root";
            sections = RetargetReferences(sections, name, up, null, s is VfxSpacewarp);
        }
        return file with { Sections = sections };

        static VfxSection FixMesh(VfxMesh m, ImmutableArray<int> mi)
        {
            if (mi.SequenceEqual(m.MaterialIndices!.Value)) return m;
            // A slot reassigned to -1 is dropped and the faces using it get -1.
            var keep = Enumerable.Range(0, mi.Length).Where(k => mi[k] >= 0).ToList();
            var faces = m.Faces.Select(f => f.MaterialIndex < 0 ? f : f with { MaterialIndex = keep.IndexOf(f.MaterialIndex) }).ToImmutableArray();
            return m with { MaterialIndices = keep.Select(k => mi[k]).ToImmutableArray(), Faces = faces };
        }
    }

    /// <summary>
    /// Inserts a copy right after the original; named objects get a unique name. A material copy is appended
    /// after the last section instead, so the material indices of meshes and particles keep their meaning.
    /// </summary>
    public static VfxFile DuplicateSection(VfxFile file, int index, string? newName = null)
    {
        var s = Get<VfxSection>(file, index);
        var copy = NameOf(s) is { } n ? WithName(s, UniqueName(file, newName ?? n)) : s;
        return file with { Sections = s is VfxMaterial ? file.Sections.Add(copy) : file.Sections.Insert(index + 1, copy) };
    }

    public static VfxFile Rename(VfxFile file, int index, string name)
    {
        var s = Get<VfxSection>(file, index);
        if (string.IsNullOrEmpty(name) || name.Contains('\0') || name.Any(c => c > 0xFF)) throw new ArgumentException("Name must be non-empty Latin-1 without NUL.");
        if (NameOf(s) is not { } oldName) throw new ArgumentException("Section has no name.");
        var renamed = Put(file, index, WithName(s, name));
        if (ReferenceEquals(renamed, file)) return file;
        var updated = RenameReferences(renamed.Sections, oldName, name, s is VfxSpacewarp);
        return updated == renamed.Sections ? renamed : renamed with { Sections = updated };
    }

    /// <summary>The parent name of an object section (meshes, particle systems, dummies, lights, spacewarps), else null.</summary>
    public static string? ParentOf(VfxSection s) => s switch
    {
        VfxMesh m => m.Parent, VfxParticleSystem p => p.Parent, VfxDummy d => d.Parent,
        VfxLight l => l.Parent, VfxSpacewarp w => w.Parent, _ => null,
    };

    internal static VfxSection WithParent(VfxSection s, string parent) => s switch
    {
        VfxMesh m => m with { Parent = parent }, VfxParticleSystem p => p with { Parent = parent }, VfxDummy d => d with { Parent = parent },
        VfxLight l => l with { Parent = parent }, VfxSpacewarp w => w with { Parent = parent }, _ => s,
    };

    /// <summary>
    /// After an object named <paramref name="oldName"/> was renamed: parent names (and, for a spacewarp, particle-system
    /// spacewarp names) equal to <paramref name="oldName"/> (case-insensitive, like the engine's lookup) follow the rename.
    /// Nothing changes while another object still carries <paramref name="oldName"/>, because the references still resolve to it.
    /// </summary>
    internal static ImmutableArray<VfxSection> RenameReferences(ImmutableArray<VfxSection> sections, string oldName, string newName, bool spacewarp)
    {
        if (string.IsNullOrEmpty(oldName) || string.Equals(oldName, newName, StringComparison.Ordinal)) return sections;
        if (sections.Any(x => string.Equals(NameOf(x), oldName, StringComparison.OrdinalIgnoreCase))) return sections;
        return RetargetReferences(sections, oldName, newName, spacewarp ? newName : null, spacewarp);
    }

    /// <summary>
    /// Rewrites references to <paramref name="name"/>: parents become <paramref name="parent"/>; when <paramref name="warps"/>,
    /// particle-system spacewarp entries become <paramref name="warp"/> (null removes the entry).
    /// </summary>
    internal static ImmutableArray<VfxSection> RetargetReferences(ImmutableArray<VfxSection> sections, string name, string parent, string? warp, bool warps)
    {
        bool Is(string? v) => string.Equals(v, name, StringComparison.OrdinalIgnoreCase);
        var b = sections.ToBuilder();
        bool changed = false;
        for (int i = 0; i < b.Count; i++)
        {
            var x = b[i];
            if (Is(ParentOf(x))) x = WithParent(x, parent);
            if (warps && x is VfxParticleSystem p && p.Warps.Any(Is))
                x = p with { Warps = p.Warps.Select(w => Is(w) ? warp : w).OfType<string>().ToImmutableArray() };
            if (!ReferenceEquals(x, b[i])) { b[i] = x; changed = true; }
        }
        return changed ? b.ToImmutable() : sections;
    }

    public static VfxFile Reparent(VfxFile file, int index, string parent)
    {
        var s = Get<VfxSection>(file, index);
        if (string.IsNullOrEmpty(parent) || parent.Contains('\0')) throw new ArgumentException("Parent must be non-empty without NUL.");
        return Put(file, index, s switch
        {
            VfxMesh m => m with { Parent = parent }, VfxParticleSystem p => p with { Parent = parent }, VfxDummy d => d with { Parent = parent },
            VfxLight l => l with { Parent = parent }, VfxSpacewarp w => w with { Parent = parent },
            _ => throw new ArgumentException("Section has no parent."),
        });
    }

    /// <summary>Moves a section to another position. Moving materials relative to each other remaps material indices.</summary>
    public static VfxFile MoveSection(VfxFile file, int from, int to)
    {
        var s = Get<VfxSection>(file, from);
        if ((uint)to >= (uint)file.Sections.Length) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return file;
        var oldMats = file.Sections.OfType<VfxMaterial>().ToList();
        var sections = file.Sections.RemoveAt(from).Insert(to, s);
        if (s is VfxMaterial)
        {
            var newMats = sections.OfType<VfxMaterial>().ToList();
            var map = oldMats.Select(m => newMats.FindIndex(x => ReferenceEquals(x, m))).ToArray();
            int M(int v) => v >= 0 && v < map.Length ? map[v] : v;
            sections = sections.Select(x => x switch
            {
                VfxMesh m when m.MaterialIndices is { } mi => m with { MaterialIndices = mi.Select(M).ToImmutableArray() },
                VfxParticleSystem p when p.MaterialIndex is { } pi => p with { MaterialIndex = M(pi) },
                _ => x,
            }).ToImmutableArray();
        }
        return file with { Sections = sections };
    }

    public static VfxFile SetEndFrame(VfxFile file, int endFrame)
    {
        RequireCurrent(file);
        ArgumentOutOfRangeException.ThrowIfNegative(endFrame);
        return file.EndFrame == endFrame ? file : file with { EndFrame = endFrame };
    }

    /// <summary>Header end frame = max object frame count - 1 (notes §6.1).</summary>
    public static VfxFile RecomputeEndFrame(VfxFile file)
    {
        RequireCurrent(file);
        int max = file.Sections.Select(s => s switch
        {
            VfxMesh m => m.Frames.Length, VfxParticleSystem p => p.Frames.Length, VfxDummy d => d.Frames.Length,
            VfxLight l => l.Frames.Length, VfxSpacewarp w => w.Frames.Length, _ => 0,
        }).DefaultIfEmpty(0).Max();
        return SetEndFrame(file, Math.Max(max - 1, 0));
    }

    private static VfxSection WithName(VfxSection s, string name) => s switch
    {
        VfxMesh m => m with { Name = name }, VfxParticleSystem p => p with { Name = name }, VfxDummy d => d with { Name = name },
        VfxLight l => l with { Name = name }, VfxSpacewarp w => w with { Name = name }, _ => s,
    };
}
