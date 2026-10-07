using System.Collections.Immutable;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Editing;

public static partial class MeshEdit
{
    /// <summary>
    /// Replaces a material record as given. Both name fields must be 32 bytes with a terminator, and
    /// the numbers finite (fields equal to the stored ones are always accepted). LOD texture names are
    /// NOT touched: use <see cref="SetTextureName"/> to rename a texture everywhere.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="submesh">Submesh index.</param>
    /// <param name="material">Material index within the submesh.</param>
    /// <param name="value">The new record.</param>
    public static V3dFile SetMaterial(V3dFile mesh, int submesh, int material, V3dMaterial value)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(value);
        int index = SubmeshSection(mesh, submesh, out var sub);
        CheckMaterial(sub, submesh, material);
        var old = sub.Materials[material];
        if (Same(value, old)) return mesh;
        if (value.DiffuseMap != old.DiffuseMap) CheckNameField(value.DiffuseMap, V3dMaterial.NameSize, "texture name");
        if (value.ReflectionMap != old.ReflectionMap) CheckNameField(value.ReflectionMap, V3dMaterial.NameSize, "reflection map name");
        if (!Same(value.Emissive, old.Emissive)) CheckFinite(value.Emissive, "emissive value", nameof(value));
        if (!Same(value.Unknown0, old.Unknown0)) CheckFinite(value.Unknown0, "Unknown0 value", nameof(value));
        if (!Same(value.Unknown1, old.Unknown1)) CheckFinite(value.Unknown1, "Unknown1 value", nameof(value));
        if (!Same(value.ReflectionCoefficient, old.ReflectionCoefficient)) CheckFinite(value.ReflectionCoefficient, "reflection coefficient", nameof(value));
        var changed = sub with { Materials = sub.Materials.SetItem(material, value) };
        return mesh with { Sections = mesh.Sections.SetItem(index, changed) };
    }

    /// <summary>
    /// Renames a material's texture: its diffuse map name and the LOD texture entries with that
    /// material index — with <see cref="LodTextureUpdate.MatchingName"/> only the entries whose file
    /// name equals the OLD diffuse name (case-insensitive), with <see cref="LodTextureUpdate.All"/>
    /// every entry of the material. Texture names hold at most 31 Latin-1 characters. Setting the
    /// current name with <see cref="LodTextureUpdate.MatchingName"/> changes nothing.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="submesh">Submesh index.</param>
    /// <param name="material">Material index within the submesh.</param>
    /// <param name="name">The new texture file name.</param>
    /// <param name="update">Which LOD texture entries follow.</param>
    public static V3dFile SetTextureName(V3dFile mesh, int submesh, int material, string name, LodTextureUpdate update = LodTextureUpdate.MatchingName)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(name);
        int index = SubmeshSection(mesh, submesh, out var sub);
        CheckMaterial(sub, submesh, material);
        if (update is not (LodTextureUpdate.MatchingName or LodTextureUpdate.All))
            throw new ArgumentOutOfRangeException(nameof(update), $"{(int)update} is not a LOD texture update mode; use MatchingName or All.");
        var old = sub.Materials[material];
        string oldName = old.DiffuseMap.Text;
        if (update == LodTextureUpdate.MatchingName && string.Equals(oldName, name, StringComparison.Ordinal)) return mesh;

        var diffuse = ResolveName(old.DiffuseMap, name, V3dMaterial.NameSize, "texture name", nameof(name));
        var changed = diffuse == old.DiffuseMap ? sub : sub with { Materials = sub.Materials.SetItem(material, old with { DiffuseMap = diffuse }) };
        changed = MapLods(changed, lod =>
        {
            var textures = MapItems(lod.Textures, t =>
                t.MaterialIndex != material
                || string.Equals(t.FileName, name, StringComparison.Ordinal)
                || (update == LodTextureUpdate.MatchingName && !string.Equals(t.FileName, oldName, StringComparison.OrdinalIgnoreCase))
                    ? t : t with { FileName = name });
            return textures == lod.Textures ? lod : lod with { Textures = textures };
        });
        return ReferenceEquals(changed, sub) ? mesh : mesh with { Sections = mesh.Sections.SetItem(index, changed) };
    }

    /// <summary>
    /// Sets a submesh's LOD distances: one per LOD, each finite and at least 0 (stock LOD 0 is always 0;
    /// distances that do not increase are only a lint warning).
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="submesh">Submesh index.</param>
    /// <param name="distances">The camera distance at which each LOD starts.</param>
    public static V3dFile SetLodDistances(V3dFile mesh, int submesh, IReadOnlyList<float> distances)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(distances);
        int index = SubmeshSection(mesh, submesh, out var sub);
        var old = sub.LodDistances.IsDefault ? [] : sub.LodDistances;
        if (distances.Count != sub.Lods.Length)
            throw new ArgumentException(
                $"Submesh {submesh} has {sub.Lods.Length} levels of detail, so it needs {sub.Lods.Length} distances, not {distances.Count}.", nameof(distances));
        bool same = old.Length == distances.Count;
        for (int i = 0; i < distances.Count; i++)
        {
            float d = distances[i];
            if (i < old.Length && Same(d, old[i])) continue;
            same = false;
            if (!float.IsFinite(d) || d < 0f)
                throw new ArgumentOutOfRangeException(nameof(distances), $"LOD {i}'s distance must be a finite number of at least 0, not {d}.");
        }
        if (same) return mesh;
        var changed = sub with { LodDistances = [.. distances] };
        return mesh with { Sections = mesh.Sections.SetItem(index, changed) };
    }

    /// <summary>
    /// Renames a submesh (24-byte field: at most 23 Latin-1 characters). Trailer entries and the parent
    /// name that equal the old name are renamed with it; the same name keeps the raw bytes.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="submesh">Submesh index.</param>
    /// <param name="name">The new name.</param>
    public static V3dFile RenameSubmesh(V3dFile mesh, int submesh, string name)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int index = SubmeshSection(mesh, submesh, out var sub);
        var newName = ResolveName(sub.Name, name, V3dSubmesh.NameSize, "submesh name", nameof(name));
        if (newName == sub.Name) return mesh;
        string oldText = sub.Name.Text;
        var parent = string.Equals(sub.ParentName.Text, oldText, StringComparison.Ordinal) ? Renamed(sub.ParentName, name) : sub.ParentName;
        var trailers = MapItems(sub.Trailers, t =>
            string.Equals(t.Name.Text, oldText, StringComparison.Ordinal) ? t with { Name = Renamed(t.Name, name) } : t);
        var changed = sub with { Name = newName, ParentName = parent, Trailers = trailers };
        return mesh with { Sections = mesh.Sections.SetItem(index, changed) };

        static FixedString Renamed(FixedString field, string text) =>
            field.Length == V3dSubmesh.NameSize ? field.WithText(text) : FixedString.FromText(text, V3dSubmesh.NameSize);
    }

    private static void CheckMaterial(V3dSubmesh sub, int submesh, int material)
    {
        int count = sub.Materials.IsDefault ? 0 : sub.Materials.Length;
        if ((uint)material >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(material),
                count == 0 ? $"Submesh {submesh} has no materials; there is no material {material}."
                : $"Submesh {submesh} has {count} materials (0 to {count - 1}); there is no material {material}.");
    }

    private static void CheckNameField(FixedString field, int size, string what)
    {
        if (field.Length != size)
            throw new ArgumentException($"The material's {what} field is {field.Length} bytes; it must be {size}.", "value");
        if (field.Bytes.AsSpan().IndexOf((byte)0) < 0)
            throw new ArgumentException(
                $"The material's {what} '{field.Text}' fills its {size}-byte field with no room for the terminator; use at most {size - 1} characters.", "value");
    }
}
