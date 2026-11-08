using System.Collections.Immutable;
using Cairn.Formats.Rf2;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// The texture side of a Red Faction II mesh's conversion: its material names translated to Red Faction names
/// (<see cref="Rf2TextureMode.Translate"/>, the default), or kept with the textures written beside the converted mesh
/// from the <c>.peg</c> packs (<see cref="Rf2TextureMode.KeepOriginals"/>). Every name that changes, stays or cannot
/// be found is listed in the report. A character over the texture limit first gets an atlas (<see cref="V3dTextureAtlas"/>)
/// from the packs, in either mode.
/// </summary>
internal static class Rf2MeshTextures
{
    private const int ListedNames = 8;

    public static (LegacyMesh Mesh, List<string> Notes, ImmutableArray<(string Name, byte[] Bytes)> Files) Apply(LegacyMesh mesh, LegacyConvertOptions options, string sourceName)
    {
        var notes = new List<string>();
        var atlases = ImmutableArray<(string Name, byte[] Bytes)>.Empty;
        if (mesh.Description.Kind == V3dKind.Character && mesh.Description.Submeshes.Length == 1)
        {
            var atlas = V3dTextureAtlas.Combine(mesh.Description.Submeshes[0], Path.GetFileNameWithoutExtension(sourceName), name => Pixels(name, options.TexturePacks));
            mesh = mesh with { Description = mesh.Description with { Submeshes = [atlas.Submesh] } };
            atlases = atlas.Files;
            foreach (var (file, sources) in atlas.Combined)
                notes.Add($"{sources.Length:N0} textures were combined into {file}, written beside the mesh with their Red Faction II pixels, because a .v3c holds {V3dBuilder.MaxTexturesPerLod} per level of detail: {List(sources)}.");
        }
        var names = mesh.Description.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text)
            .Where(n => n.Length > 0 && !atlases.Any(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return (mesh, notes, atlases);

        if (options.Rf2Textures == Rf2TextureMode.KeepOriginals) return (mesh, notes, [.. WriteOriginals(names, options.TexturePacks, notes), .. atlases]);

        var translations = names.Select(Rf2TextureNames.Translate).ToList();
        var changed = translations.Where(t => t.IsTranslated).ToList();
        if (changed.Count > 0)
        {
            var map = changed.ToDictionary(t => t.Original, t => t.Translated, StringComparer.OrdinalIgnoreCase);
            var submeshes = mesh.Description.Submeshes.Select(s => s with
            {
                Materials = [.. s.Materials.Select(m => map.TryGetValue(m.DiffuseMap.Text, out var to)
                    ? m with { DiffuseMap = FixedString.FromText(to.Length < V3dMaterial.NameSize ? to : to[..(V3dMaterial.NameSize - 1)], V3dMaterial.NameSize) }
                    : m)],
            }).ToImmutableArray();
            mesh = mesh with { Description = mesh.Description with { Submeshes = submeshes } };
            notes.Add($"Texture names translated to Red Faction names ({changed.Count:N0} of {names.Count:N0}): {List(changed.Select(t => $"{t.Original} → {t.Translated}"))}.");
            var weak = changed.Where(t => t.IsWeak).ToList();
            if (weak.Count > 0)
                notes.Add($"{weak.Count:N0} of those translations are uncertain (several Red Faction names end the same way and the first was taken): {List(weak.Select(t => $"{t.Original} → {t.Translated}"))}.");
            notes.Add(Rf2TextureNames.TextureSetNote);
        }
        var kept = translations.Where(t => !t.IsTranslated).ToList();
        if (kept.Count > 0)
        {
            notes.Add($"{kept.Count:N0} texture name{(kept.Count == 1 ? " has" : "s have")} no Red Faction translation and keep{(kept.Count == 1 ? "s its" : " their")} Red Faction II name{(kept.Count == 1 ? "" : "s")}: {List(kept.Select(t => t.Original))}. "
                + "Convert with the Red Faction II textures kept to write them beside the mesh.");
        }
        return (mesh, notes, atlases);
    }

    /// <summary>A still texture's image from the packs (an animated one cannot go in an atlas); null when there is none.</summary>
    private static BgraImage? Pixels(string name, PegTextureSet? packs) =>
        packs?.Find(name) is { Texture.IsAnimated: false } && !name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase) ? packs.Decode(name) : null;

    private static ImmutableArray<(string Name, byte[] Bytes)> WriteOriginals(List<string> names, PegTextureSet? packs, List<string> notes)
    {
        if (packs is null || packs.IsEmpty)
        {
            notes.Add("The Red Faction II texture names were kept, but no .peg texture packs were found beside the mesh, so no textures were written.");
            return [];
        }
        var files = ImmutableArray.CreateBuilder<(string, byte[])>();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var animated = new List<string>();
        var from = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            // RF2 packs hold uncompressed textures only (16-bit, 32-bit and 8-bit palettised), so .tga loses nothing
            if (packs.Find(name) is not { } hit || packs.ReadAsTga(name) is not { } tga)
            {
                missing.Add(name);
                continue;
            }
            string file = Path.ChangeExtension(Path.GetFileName(name), ".tga");
            if (!written.Add(file)) continue;
            files.Add((file, tga));
            from.Add(hit.Pack);
            if (hit.Texture.IsAnimated || name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase)) animated.Add(name);
        }
        notes.Add($"The Red Faction II texture names were kept; {files.Count:N0} texture{(files.Count == 1 ? " was" : "s were")} written beside the mesh as .tga from {List(from)}.");
        if (animated.Count > 0)
            notes.Add($"{animated.Count:N0} animated texture{(animated.Count == 1 ? " was" : "s were")} written as {(animated.Count == 1 ? "its" : "their")} first frame only ({List(animated)}); the game finds the .tga for a .vbm name.");
        if (missing.Count > 0)
            notes.Add($"{missing.Count:N0} texture{(missing.Count == 1 ? " is" : "s are")} in none of the .peg packs beside the mesh ({List(missing)}); {(missing.Count == 1 ? "it was" : "they were")} not written.");
        foreach (var (pack, reason) in packs.Unreadable) notes.Add($"The texture pack {pack} could not be read: {reason}");
        return files.ToImmutable();
    }

    private static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        return string.Join(", ", all.Take(ListedNames)) + (all.Count > ListedNames ? $" and {all.Count - ListedNames:N0} more" : "");
    }
}
