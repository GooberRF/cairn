using System.Collections.Concurrent;
using Cairn.Assets;
using Cairn.Formats.XForge;
using Cairn.Ui.Modules;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>
/// Where a Red Faction for N-Gage level's tab and packfile preview find its textures: beside it (the rest of its
/// <c>.cfl</c>, or its folder) and then the game data, as any level, with the 16-bit <c>.tga</c> files it names whose
/// alpha bit is never set read as opaque (<see cref="XffTextureFix"/>), as the N-Gage game draws them and as the Meshes
/// module shows them. Conversions read the files as they are and apply the same fix themselves, so their reports name
/// what it changed.
/// </summary>
internal static class LvlXffTextures
{
    /// <summary>
    /// The resolver for <paramref name="source"/>: for an N-Gage level the textures it names made opaque where their alpha
    /// bit is never set, then <paramref name="siblings"/>, then <paramref name="under"/>; for any other level
    /// <paramref name="siblings"/> over <paramref name="under"/>.
    /// </summary>
    public static AssetResolver? ResolverFor(LvlSource source, IAssetSiblings? siblings, AssetResolver? under)
    {
        if (source.Level?.Xff is not { Textures.IsEmpty: false } xff) return siblings.Layer(under);
        var baseResolver = under ?? new AssetResolver(new AssetResolverOptions());
        var names = new HashSet<string>(xff.Textures.Select(t => Path.GetFileName(t.Replace('\\', '/'))), StringComparer.OrdinalIgnoreCase);
        var read = LvlModule.FilesBeside(siblings, baseResolver)!;
        // each named texture is read once: its opaque copy, or null when it needs no fix (or is not there)
        var opaque = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        byte[]? Fixed(string name) => !names.Contains(name) ? null
            : opaque.GetOrAdd(name, n => read(n) is { } bytes && XffTextureFix.NeedsFix(bytes) ? XffTextureFix.Opaque(bytes) : null);
        // a resolver keeps one in-memory layer: the fixed textures and the siblings are joined in it
        return baseResolver.WithOverlay(siblings?.Label ?? "the N-Gage level's textures",
            name => Fixed(name) is not null || siblings?.Contains(name) == true,
            name => (Fixed(name) ?? siblings?.Read(name)) is { } bytes ? new MemoryStream(bytes, writable: false) : null);
    }
}
