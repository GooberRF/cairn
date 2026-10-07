using System.Collections.Immutable;
using System.Numerics;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>The space a bone's bind (rest) transform is given in.</summary>
public enum BindSpace
{
    /// <summary>Model space: the bone's rest world transform.</summary>
    World,

    /// <summary>Relative to the parent's rest world transform (model space for a root).</summary>
    Local,
}

/// <summary>Which LOD texture entries of a material <see cref="MeshEdit.SetTextureName"/> renames.</summary>
public enum LodTextureUpdate
{
    /// <summary>Only entries whose file name equals the material's old texture name (case-insensitive).</summary>
    MatchingName,

    /// <summary>Every LOD texture entry of the material, LOD-specific names (<c>foo-mip1.tga</c>) included.</summary>
    All,
}

/// <summary>The result of <see cref="MeshEdit.ReorderBones"/>.</summary>
/// <param name="Mesh">The mesh with its bones in the new order.</param>
/// <param name="OldToNew">For each old bone index, its new index.</param>
/// <param name="NewToOld">For each new bone index, the old index it came from (the order passed in).</param>
public sealed record BoneReorderResult(V3dFile Mesh, ImmutableArray<int> OldToNew, ImmutableArray<int> NewToOld);

/// <summary>
/// Pure mesh edits: every operation takes a <see cref="V3dFile"/> and returns a new one, leaving the
/// input untouched. The contract every operation keeps:
/// <list type="bullet">
/// <item>arguments are checked up front and rejected with a plain-language
/// <see cref="ArgumentException"/> / <see cref="ArgumentOutOfRangeException"/> (names too long for
/// their field or not Latin-1, indices out of range, NaN or infinite numbers, ...). A value equal to
/// what the mesh already stores is always accepted, so re-entering a field never fails;</item>
/// <item>when nothing would change, the input instance itself is returned;</item>
/// <item>otherwise only the records that change are copied (with <c>with</c>); every other byte the
/// writer produces is identical, and an unchanged name keeps its raw bytes (leftovers after the
/// terminator included);</item>
/// <item>the result serialises with <see cref="V3dWriter"/> and has no Error-severity lint diagnostic
/// the input did not already have.</item>
/// </list>
/// This file holds the shared helpers; the operations live in the other <c>MeshEdit.*.cs</c> files.
/// </summary>
public static partial class MeshEdit
{
    // ── Sections ─────────────────────────────────────────────────────────────

    /// <summary>The section index of submesh <paramref name="submesh"/> (counted among the SUBM sections).</summary>
    private static int SubmeshSection(V3dFile mesh, int submesh, out V3dSubmesh value, string paramName = "submesh")
    {
        int count = 0;
        for (int i = 0; i < mesh.Sections.Length; i++)
        {
            if (mesh.Sections[i] is not V3dSubmesh s) continue;
            if (count == submesh)
            {
                value = s;
                return i;
            }
            count++;
        }
        if (submesh >= 0) count = mesh.Submeshes.Count();
        throw new ArgumentOutOfRangeException(paramName, $"The mesh has {count} submeshes; there is no submesh {submesh}.");
    }

    /// <summary>The section index of the first BONE section, or -1.</summary>
    private static int BoneSectionIndex(V3dFile mesh)
    {
        for (int i = 0; i < mesh.Sections.Length; i++)
        {
            if (mesh.Sections[i] is V3dBoneSection) return i;
        }
        return -1;
    }

    /// <summary>The mesh with its first BONE section's bones replaced.</summary>
    private static V3dFile WithBones(V3dFile mesh, ImmutableArray<V3dBone> bones)
    {
        int index = BoneSectionIndex(mesh);
        var section = (V3dBoneSection)mesh.Sections[index];
        return mesh with { Sections = mesh.Sections.SetItem(index, section with { Bones = bones }) };
    }

    /// <summary>
    /// Applies <paramref name="map"/> to every submesh; sections it returns unchanged (same instance)
    /// are kept, and when none changes the mesh itself comes back.
    /// </summary>
    private static V3dFile MapSubmeshes(V3dFile mesh, Func<V3dSubmesh, V3dSubmesh> map)
    {
        ImmutableArray<V3dSection>.Builder? builder = null;
        for (int i = 0; i < mesh.Sections.Length; i++)
        {
            if (mesh.Sections[i] is not V3dSubmesh s) continue;
            var changed = map(s);
            if (ReferenceEquals(changed, s)) continue;
            builder ??= mesh.Sections.ToBuilder();
            builder[i] = changed;
        }
        return builder is null ? mesh : mesh with { Sections = builder.MoveToImmutable() };
    }

    /// <summary>Applies <paramref name="map"/> to every LOD of a submesh, keeping unchanged instances.</summary>
    private static V3dSubmesh MapLods(V3dSubmesh submesh, Func<V3dLod, V3dLod> map)
    {
        var lods = MapItems(submesh.Lods, map);
        return lods == submesh.Lods ? submesh : submesh with { Lods = lods };
    }

    /// <summary>
    /// Applies <paramref name="map"/> to every item; when it returns every item unchanged (same
    /// instance) the original array comes back.
    /// </summary>
    private static ImmutableArray<T> MapItems<T>(ImmutableArray<T> items, Func<T, T> map) where T : class
    {
        if (items.IsDefaultOrEmpty) return items;
        T[]? result = null;
        for (int i = 0; i < items.Length; i++)
        {
            var changed = map(items[i]);
            if (ReferenceEquals(changed, items[i])) continue;
            result ??= [.. items];
            result[i] = changed;
        }
        return result is null ? items : [.. result];
    }

    // ── Argument checks ──────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="current"/> when its text already is <paramref name="name"/> (its raw bytes kept),
    /// otherwise a clean <paramref name="size"/>-byte field holding the checked name.
    /// </summary>
    private static FixedString ResolveName(FixedString current, string name, int size, string what, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        if (current.Length == size && string.Equals(current.Text, name, StringComparison.Ordinal)) return current;
        return NewName(name, size, what, paramName);
    }

    /// <summary>A clean <paramref name="size"/>-byte field holding the checked name.</summary>
    private static FixedString NewName(string name, int size, string what, string paramName)
    {
        CheckText(name, size - 1, what, paramName);
        return FixedString.FromText(name, size);
    }

    /// <summary>Checks a name stored one Latin-1 byte per character in at most <paramref name="maxLength"/> characters.</summary>
    private static void CheckText(string name, int maxLength, string what, string paramName)
    {
        ArgumentNullException.ThrowIfNull(name, paramName);
        foreach (char c in name)
        {
            if (c == '\0')
                throw new ArgumentException($"The {what} '{name.Replace('\0', ' ')}' contains a NUL character, which would end the name early.", paramName);
            if (c > 0xFF)
                throw new ArgumentException(
                    $"The {what} '{name}' contains '{c}', which the file cannot store: names are one Latin-1 byte per character.", paramName);
        }
        if (name.Length > maxLength)
            throw new ArgumentException(
                $"The {what} '{name}' is {name.Length} characters; it can be at most {maxLength} (the field is {maxLength + 1} bytes including the terminator).",
                paramName);
    }

    /// <summary>Throws unless <paramref name="bone"/> is a bone of a mesh with <paramref name="count"/> bones.</summary>
    private static void CheckBone(int count, int bone, string paramName)
    {
        if ((uint)bone >= (uint)count)
            throw new ArgumentOutOfRangeException(paramName,
                count == 0 ? $"The mesh has no bones; there is no bone {bone}." : $"The mesh has {count} bones (0 to {count - 1}); there is no bone {bone}.");
    }

    /// <summary>Throws unless <paramref name="bone"/> is -1 (none) or a bone of a mesh with <paramref name="count"/> bones.</summary>
    private static void CheckBoneOrNone(int count, int bone, string paramName)
    {
        if (bone == -1) return;
        if ((uint)bone >= (uint)count)
            throw new ArgumentOutOfRangeException(paramName,
                count == 0 ? $"The mesh has no bones, so the bone must be -1 (none), not {bone}."
                : $"The mesh has {count} bones (0 to {count - 1}); use one of them or -1 for none, not {bone}.");
    }

    private static void CheckFinite(float value, string what, string paramName)
    {
        if (!float.IsFinite(value)) throw new ArgumentException($"The {what} must be a finite number, not {value}.", paramName);
    }

    private static void CheckFinite(Vector3 value, string what, string paramName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentException($"The {what} must be finite numbers, not {value}.", paramName);
    }

    /// <summary>Throws for a non-finite or all-zero quaternion (no rotation can be made of it).</summary>
    private static void CheckRotation(Quaternion value, string what, string paramName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new ArgumentException($"The {what} must be finite numbers, not {value}.", paramName);
        if (!(Quat.Dot(value, value) > 1e-12f))
            throw new ArgumentException($"The {what} is all zeros, which is not a rotation.", paramName);
    }

    // ── Bitwise comparisons (an identity edit must not even flip -0 to 0) ────

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static bool Same(Vector3 a, Vector3 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);

    private static bool Same(Quaternion a, Quaternion b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z) && Same(a.W, b.W);

    private static bool Same(Rigid a, Rigid b) => Same(a.Rotation, b.Rotation) && Same(a.Position, b.Position);

    private static bool Same(V3dBone a, V3dBone b) =>
        a.Name == b.Name && Same(a.Rotation, b.Rotation) && Same(a.Position, b.Position) && a.ParentIndex == b.ParentIndex;

    private static bool Same(V3dMaterial a, V3dMaterial b) =>
        a.DiffuseMap == b.DiffuseMap && Same(a.Emissive, b.Emissive) && Same(a.Unknown0, b.Unknown0) && Same(a.Unknown1, b.Unknown1)
        && Same(a.ReflectionCoefficient, b.ReflectionCoefficient) && a.ReflectionMap == b.ReflectionMap && a.Flags == b.Flags;
}
