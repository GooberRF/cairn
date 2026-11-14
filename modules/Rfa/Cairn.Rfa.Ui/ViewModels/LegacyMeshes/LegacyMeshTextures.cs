using System.Runtime.CompilerServices;
using Cairn.Formats.XForge;
using Cairn.Rfa.Formats.Legacy;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// Where a Red Faction II mesh finds its textures: inside the <c>.peg</c> packs beside it (the rest of its packfile, or
/// its folder), in the order the game's own packs suggest (<c>&lt;mesh&gt;_v3d.peg</c> / <c>&lt;mesh&gt;_vcm.peg</c>,
/// then the base model's, then the others). Tabs, packfile previews and conversions use the same packs.
/// </summary>
internal static class LegacyMeshTextures
{
    private static readonly ConditionalWeakTable<LegacyMeshSource, PegTextureSet> s_byTab = new();

    /// <summary>
    /// The resolver of a legacy mesh tab: for a Red Faction II mesh its <c>.peg</c> textures (and the rest of its
    /// packfile, for a work copy) first, then <paramref name="under"/>; for an N-Gage scene its textures as its conversion
    /// writes them (<see cref="XffLayer"/>); null for any other mesh (the tab's usual resolver applies).
    /// </summary>
    public static AssetResolver? ResolverFor(LegacyMeshSource source, string meshName, string? filePath, IAssetSiblings? siblings, AssetResolver under)
    {
        if (source.Mesh?.Xff is { } xff) return XffLayer(xff, siblings, under);
        if (source.Mesh is not { IsRedFactionII: true } mesh) return null;
        var packs = s_byTab.GetValue(source, _ => PacksFor(meshName, mesh, filePath, siblings));
        return Combine(packs, siblings, under);
    }

    /// <summary>The texture packs of a tab's Red Faction II mesh (for conversions), or null for any other mesh.</summary>
    public static PegTextureSet? PacksForTab(LegacyMeshSource source, string meshName, string? filePath, IAssetSiblings? siblings) =>
        source.Mesh is { IsRedFactionII: true } mesh ? s_byTab.GetValue(source, _ => PacksFor(meshName, mesh, filePath, siblings)) : null;

    /// <summary>
    /// The resolver of a packfile preview of <paramref name="fileName"/>: for a Red Faction II mesh the textures in the
    /// <c>.peg</c> entries beside it and the other entries, then <paramref name="under"/>; otherwise the siblings over
    /// <paramref name="under"/>, as every preview does.
    /// </summary>
    public static AssetResolver? PreviewResolver(byte[] bytes, string fileName, IAssetSiblings? siblings, AssetResolver? under)
    {
        if (siblings is null || !LegacyMeshSupport.IsRedFactionII(bytes)) return siblings.Layer(under);
        bool character = fileName.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase);
        var packs = LegacyMeshSupport.TexturePacksFor(fileName, character, siblings.Names, siblings.Read, Label(fileName, siblings.Label));
        return Combine(packs, siblings, under ?? new AssetResolver(new AssetResolverOptions()));
    }

    /// <summary>The packs beside a packfile entry for a batch conversion, from the packfile's entries.</summary>
    public static PegTextureSet PacksForEntry(string entryName, IReadOnlyCollection<ArchiveBatchEntry> entries, string archiveName)
    {
        var byName = new Dictionary<string, ArchiveBatchEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) byName.TryAdd(e.Name, e);
        bool character = entryName.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase);
        return LegacyMeshSupport.TexturePacksFor(entryName, character, byName.Keys, n => byName.TryGetValue(n, out var e) ? e.Read() : null, Label(entryName, archiveName));
    }

    private static PegTextureSet PacksFor(string meshName, LegacyMesh mesh, string? filePath, IAssetSiblings? siblings)
    {
        bool character = mesh.Description.Kind == Formats.V3d.V3dKind.Character;
        if (siblings is not null)
            return LegacyMeshSupport.TexturePacksFor(meshName, character, siblings.Names, siblings.Read, Label(meshName, siblings.Label));
        if (filePath is not null)
            return PegTextureSet.ForFolder(Label(meshName, Path.GetDirectoryName(filePath) ?? ""), filePath, LegacyMeshSupport.TexturePackSuffixes(character));
        return new PegTextureSet(meshName, []);
    }

    /// <summary>
    /// The resolver of a packfile preview of an N-Gage scene: the files its conversion makes (the colours of untextured
    /// parts), then the siblings, then <paramref name="under"/>.
    /// </summary>
    public static AssetResolver? PreviewResolver(LegacyMesh mesh, IAssetSiblings? siblings, AssetResolver? under) =>
        mesh.Xff is { } xff ? XffLayer(xff, siblings, under ?? new AssetResolver(new AssetResolverOptions())) : siblings.Layer(under);

    /// <summary>
    /// One in-memory layer (a resolver keeps a single one) with an N-Gage scene's textures as its conversion writes them:
    /// the generated colours, each texture it names under the name the mesh stores (a shortened one included), read from
    /// the siblings or <paramref name="under"/>, a .pcx as the .tga the mesh names and a 16-bit .tga made opaque where its
    /// alpha bit is never set (<see cref="XffTextureFix.ForPc"/>); then the other siblings.
    /// </summary>
    private static AssetResolver XffLayer(XffMeshParts xff, IAssetSiblings? siblings, AssetResolver under)
    {
        byte[]? Generated(string name) => xff.GeneratedTextures.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)).Bytes;
        string? Source(string name) => xff.Textures.FirstOrDefault(t => string.Equals(t.StoredName, name, StringComparison.OrdinalIgnoreCase))?.SourceName;
        byte[]? Named(string name)
        {
            if (Source(name) is not { } source) return null;
            byte[]? bytes = siblings?.Read(source);
            if (bytes is null && under.Resolve(source) is { } found)
            {
                try { bytes = found.ReadAllBytes(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { bytes = null; }
            }
            if (bytes is null) return null;
            try { return XffTextureFix.ForPc(bytes, source); }
            catch (ImageDecodeException) { return null; }
        }
        return under.WithOverlay(siblings?.Label ?? "the N-Gage scene's textures",
            name => Generated(name) is not null || siblings?.Contains(name) == true
                || (Source(name) is { } source && (siblings?.Contains(source) == true || under.Resolve(source) is not null)),
            name => (Generated(name) ?? Named(name) ?? siblings?.Read(name)) is { } bytes ? new MemoryStream(bytes, writable: false) : null);
    }

    private static string Label(string meshName, string where) => $"the .peg packs beside {Path.GetFileName(meshName)}" + (where.Length > 0 ? $" ({where})" : "");

    /// <summary>
    /// One in-memory layer of the siblings and the packs' textures (a resolver keeps a single layer, so the two are
    /// joined): a sibling of the name wins, then a texture from a pack.
    /// </summary>
    private static AssetResolver Combine(PegTextureSet packs, IAssetSiblings? siblings, AssetResolver under)
    {
        if (packs.IsEmpty) return siblings.Layer(under) ?? under;
        if (siblings is null) return packs.Layer(under);
        return under.WithOverlay(siblings.Label,
            name => siblings.Contains(name) || packs.Offers(name),
            name => siblings.Contains(name)
                ? siblings.Read(name) is { } bytes ? new MemoryStream(bytes, writable: false) : null
                : packs.Open(name));
    }
}
